using UnityEngine;
using System.IO;
using System.Collections.Generic;

namespace SMMARTS
{
    /// <summary>
    /// Replays voice annotations synchronized with session replay timeline.
    /// Discovers annotation files based on GameObject name and plays them at recorded timestamps.
    /// </summary>
    public class VoiceAnnotationReplayer : MonoBehaviour
    {
        public static VoiceAnnotationReplayer ME { get; private set; }

        [Header("Replay Settings")]
        [Tooltip("Enable annotation replay")]
        public bool enableReplay = true;

        [Header("Replay Status")]
        [SerializeField] private bool isReplaying = false;
        [SerializeField] private bool isPaused = false;
        [SerializeField] private int totalAnnotations = 0;
        [SerializeField] private int playedAnnotations = 0;

        private AudioSource audioSource;
        private CSVReader csvReader;
        private float replayStartTime;
        private string sessionDirectory;
        private float pauseTime = 0f;

        // Annotation data
        private List<AnnotationData> annotations;
        private int nextAnnotationIndex = 0;

        private struct AnnotationData
        {
            public float timestamp;
            public string wavFilename;
            public AudioClip clip;
        }

        void Awake()
        {

            if (ME != null && ME != this)
            {
                Destroy(this);
                return;
            }
            ME = this;
            DontDestroyOnLoad(gameObject);

            // Get or create AudioSource
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
            }

            // Configure AudioSource
            audioSource.playOnAwake = false;
            audioSource.loop = false;
        }

        /// <summary>
        /// Load annotation data for replay
        /// </summary>
        /// <param name="sessionDirectory">Directory containing session recordings</param>
        /// <returns>True if loaded successfully</returns>
        public bool LoadReplayData(string sessionDirectory)
        {
            if (!enableReplay)
            {
                Debug.Log($"VoiceAnnotationReplayer on {gameObject.name}: Replay disabled");
                return false;
            }

            this.sessionDirectory = sessionDirectory;

            // Construct CSV path based on GameObject name
            string csvFileName = $"{gameObject.name}_annotations.csv";
            string csvPath = Path.Combine(sessionDirectory, csvFileName);

            // Check if CSV exists
            if (!File.Exists(csvPath))
            {
                Debug.LogWarning($"VoiceAnnotationReplayer on {gameObject.name}: Annotation CSV not found: {csvPath}");
                return false;
            }

            // Load CSV
            csvReader = new CSVReader(csvPath);
            if (!csvReader.LoadCSV())
            {
                Debug.LogError($"VoiceAnnotationReplayer on {gameObject.name}: Failed to load annotation CSV");
                return false;
            }

            // Load all annotations
            LoadAnnotations();

            totalAnnotations = annotations.Count;
            Debug.Log($"VoiceAnnotationReplayer on {gameObject.name}: Loaded {totalAnnotations} annotations");

            return totalAnnotations > 0;
        }

        /// <summary>
        /// Load all annotation data from CSV and preload audio clips
        /// </summary>
        private void LoadAnnotations()
        {
            annotations = new List<AnnotationData>();
            List<Dictionary<string, string>> allRows = csvReader.GetAllRows();

            foreach (var row in allRows)
            {
                if (row.ContainsKey("sim_time") && row.ContainsKey("wav_filename"))
                {
                    float timestamp = CSVReader.ParseFloat(row["sim_time"], 0f);
                    string wavFilename = row["wav_filename"];
                    string wavPath = Path.Combine(sessionDirectory, wavFilename);

                    // Load the audio clip
                    AudioClip clip = LoadWavFile(wavPath);

                    if (clip != null)
                    {
                        AnnotationData annotation = new AnnotationData
                        {
                            timestamp = timestamp,
                            wavFilename = wavFilename,
                            clip = clip
                        };
                        annotations.Add(annotation);
                    }
                    else
                    {
                        Debug.LogWarning($"VoiceAnnotationReplayer on {gameObject.name}: Failed to load audio: {wavPath}");
                    }
                }
            }

            // Sort by timestamp just in case
            annotations.Sort((a, b) => a.timestamp.CompareTo(b.timestamp));
        }

        /// <summary>
        /// Load a WAV file as an AudioClip
        /// </summary>
        private AudioClip LoadWavFile(string filepath)
        {
            if (!File.Exists(filepath))
            {
                Debug.LogWarning($"VoiceAnnotationReplayer on {gameObject.name}: WAV file not found: {filepath}");
                return null;
            }

            // Use Unity's WWW/UnityWebRequest for loading
            // For now, using the simpler approach with file:// URL
            string url = "file://" + filepath;

            using (var www = new WWW(url))
            {
                // Wait for loading (this is synchronous in this context)
                while (!www.isDone) { }

                if (!string.IsNullOrEmpty(www.error))
                {
                    Debug.LogError($"VoiceAnnotationReplayer on {gameObject.name}: Error loading {filepath}: {www.error}");
                    return null;
                }

                return www.GetAudioClip(false, false, AudioType.WAV);
            }
        }

        /// <summary>
        /// Start annotation replay synchronized with session
        /// </summary>
        /// <param name="replayStartTime">Time.time when replay started</param>
        public void StartReplay(float replayStartTime)
        {
            if (!enableReplay)
            {
                return;
            }

            if (annotations == null || annotations.Count == 0)
            {
                Debug.LogWarning($"VoiceAnnotationReplayer on {gameObject.name}: No annotations to replay");
                return;
            }

            this.replayStartTime = replayStartTime;
            isReplaying = true;
            isPaused = false;
            nextAnnotationIndex = 0;
            playedAnnotations = 0;

            Debug.Log($"VoiceAnnotationReplayer on {gameObject.name}: Started replay");
        }

        /// <summary>
        /// Stop annotation replay
        /// </summary>
        public void StopReplay()
        {
            if (!isReplaying) return;

            // Stop any playing audio
            if (audioSource != null && audioSource.isPlaying)
            {
                audioSource.Stop();
            }

            isReplaying = false;
            isPaused = false;
            nextAnnotationIndex = 0;

            Debug.Log($"VoiceAnnotationReplayer on {gameObject.name}: Stopped replay");
        }

        /// <summary>
        /// Pause annotation replay
        /// </summary>
        public void PauseReplay()
        {
            if (!isReplaying || isPaused) return;

            pauseTime = Time.time;
            isPaused = true;

            // Pause currently playing audio
            if (audioSource != null && audioSource.isPlaying)
            {
                audioSource.Pause();
            }

            Debug.Log($"VoiceAnnotationReplayer on {gameObject.name}: Paused replay");
        }

        /// <summary>
        /// Resume annotation replay
        /// </summary>
        public void ResumeReplay()
        {
            if (!isReplaying || !isPaused) return;

            // Adjust replay start time to account for pause duration
            float pauseDuration = Time.time - pauseTime;
            replayStartTime += pauseDuration;
            isPaused = false;

            // Resume currently paused audio
            if (audioSource != null && !audioSource.isPlaying && audioSource.time > 0)
            {
                audioSource.UnPause();
            }

            Debug.Log($"VoiceAnnotationReplayer on {gameObject.name}: Resumed replay");
        }

        /// <summary>
        /// Check for annotations that should play at current replay time
        /// </summary>
        void Update()
        {
            if (!isReplaying || isPaused) return;
            if (annotations == null || annotations.Count == 0) return;

            // Calculate current replay time (session-relative)
            float currentReplayTime = Time.time - replayStartTime;

            // Check if we need to play any annotations
            while (nextAnnotationIndex < annotations.Count)
            {
                AnnotationData annotation = annotations[nextAnnotationIndex];

                if (currentReplayTime >= annotation.timestamp)
                {
                    // Play this annotation
                    PlayAnnotation(annotation);
                    nextAnnotationIndex++;
                    playedAnnotations++;
                }
                else
                {
                    // Haven't reached this annotation yet
                    break;
                }
            }
        }

        /// <summary>
        /// Play an annotation audio clip
        /// </summary>
        private void PlayAnnotation(AnnotationData annotation)
        {
            if (audioSource == null || annotation.clip == null) return;

            // Stop any currently playing audio (stop-and-replace behavior)
            if (audioSource.isPlaying)
            {
                audioSource.Stop();
            }

            audioSource.clip = annotation.clip;
            audioSource.Play();

            Debug.Log($"VoiceAnnotationReplayer on {gameObject.name}: Playing annotation at {annotation.timestamp:F2}s: {annotation.wavFilename}");
        }

        /// <summary>
        /// Seek to an absolute replay time (seconds). Repositions the annotation queue
        /// without replaying skipped annotations and stops any playing clip.
        /// </summary>
        public void SeekToTime(float t)
        {
            replayStartTime = Time.time - t;
            pauseTime = Time.time;   // anchor pause compensation to the seek, not the original pause

            // Stop any currently playing audio when seeking
            if (audioSource != null && audioSource.isPlaying)
            {
                audioSource.Stop();
            }

            nextAnnotationIndex = 0;
            playedAnnotations = 0;
            if (annotations == null) return;

            // Fast-forward the index past annotations already due at t (without playing them)
            for (int i = 0; i < annotations.Count; i++)
            {
                if (annotations[i].timestamp <= t)
                {
                    nextAnnotationIndex = i + 1;
                    playedAnnotations = i + 1;
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Back-compat seek that takes an absolute replay start time.
        /// </summary>
        public void Seek(float newReplayStartTime)
        {
            SeekToTime(Time.time - newReplayStartTime);
        }

        /// <summary>
        /// Get replay duration from annotation data
        /// </summary>
        public float GetReplayDuration()
        {
            if (annotations == null || annotations.Count == 0)
                return 0f;

            // Return the last annotation timestamp
            return annotations[annotations.Count - 1].timestamp;
        }

        /// <summary>
        /// Public properties
        /// </summary>
        public bool IsReplaying => isReplaying;
        public bool IsPaused => isPaused;
        public int TotalAnnotations => totalAnnotations;
        public int PlayedAnnotations => playedAnnotations;

        void OnDisable()
        {
            if (isReplaying)
            {
                StopReplay();
            }
        }
    }
}