using UnityEngine;
using System.IO;

namespace SMMARTS
{
    /// <summary>
    /// Records voice annotations during simulation sessions.
    /// Triggered by speech recognition keywords to capture instructor commentary.
    /// Integrates with RecordingManager for session-based recording.
    /// </summary>
    public class VoiceAnnotationRecorder : MonoBehaviour
    {
        #region Singleton
        public static VoiceAnnotationRecorder ME { get; private set; }
        void Awake()
        {
            if (ME != null && ME != this)
            {
                Destroy(this);
                return;
            }
            ME = this;
            DontDestroyOnLoad(gameObject);
        }
        #endregion

        [Header("Recording Settings")]
        [Tooltip("Enable to record voice annotations")]
        public bool enableRecording = true;

        [Header("Speech Recognition")]
        [SerializeField] private string TriggerKeyword = "mark event";
        [SerializeField] private string EndKeyword = "end recording";

        [Header("Audio Settings")]
        [SerializeField] private float maxRecordingDuration = 5f;

        [Header("Recording Progress UI")]
        [Tooltip("Container for recording progress bar (activates when recording)")]
        public GameObject recordingProgressIndicator;

        [Tooltip("Progress bar that shrinks as recording time runs out")]
        public RectTransform recordingProgressBar;

        [Header("Recording Status")]
        [Range(0f, 1f)]
        public float RecordingWindow;

        [SerializeField] private bool isRecording = false;
        [SerializeField] private int annotationCount = 0;

#if !UNITY_WEBGL
        private AudioClip recordedClip;
        private string microphoneDevice;
#endif
        private float recordingStartTime;
        private float sessionStartTime;
        private string currentSessionDirectory;
        private string currentSessionID;
        private CSVWriter csvWriter;
        private float initialProgressBarWidth;

        void Start()
        {
            // Store initial progress bar width
            if (recordingProgressBar != null)
            {
                initialProgressBarWidth = recordingProgressBar.rect.width;
            }

            // Hide progress indicator at start
            if (recordingProgressIndicator != null)
            {
                recordingProgressIndicator.SetActive(false);
            }

#if !UNITY_WEBGL
            // Get default microphone
            if (Microphone.devices.Length > 0)
            {
                microphoneDevice = Microphone.devices[0];
                Debug.Log($"VoiceAnnotationRecorder on {gameObject.name}: Using microphone: {microphoneDevice}");
            }
            else
            {
                Debug.LogWarning($"VoiceAnnotationRecorder on {gameObject.name}: No microphone found!");
            }
#else
            Debug.Log($"VoiceAnnotationRecorder on {gameObject.name}: Microphone recording not supported on WebGL");
#endif
        }

        void Update()
        {
            // Listen for speech recognition triggers
            if (KeywordRecognitionManager.ME != null && KeywordRecognitionManager.ME.NewPhraseRecognized)
            {
                if (KeywordRecognitionManager.ME.MostRecentPhrase == TriggerKeyword)
                {
                    StartAnnotation();
                }
                else if (KeywordRecognitionManager.ME.MostRecentPhrase == EndKeyword)
                {
                    StopAnnotation();
                }
            }

            // Update recording window and progress bar
            if (isRecording)
            {
                float elapsed = Time.time - recordingStartTime;
                RecordingWindow = Mathf.Clamp01(1f - (elapsed / maxRecordingDuration));

                // Update progress bar UI
                if (recordingProgressBar != null)
                {
                    recordingProgressBar.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, initialProgressBarWidth * RecordingWindow);
                }
            }
            else
            {
                RecordingWindow = 1f;
            }
        }

        /// <summary>
        /// Start recording session
        /// </summary>
        /// <param name="sessionDirectory">Directory where session files are stored</param>
        /// <param name="sessionID">Unique session identifier</param>
        /// <param name="sessionStartTime">Session start time (Time.time)</param>
        public void StartRecording(string sessionDirectory, string sessionID, float sessionStartTime)
        {
#if UNITY_WEBGL
            Debug.LogWarning($"VoiceAnnotationRecorder on {gameObject.name}: Microphone recording not supported on WebGL");
            return;
#else
            if (!enableRecording)
            {
                Debug.Log($"VoiceAnnotationRecorder on {gameObject.name}: Recording disabled");
                return;
            }

            if (string.IsNullOrEmpty(microphoneDevice))
            {
                Debug.LogError($"VoiceAnnotationRecorder on {gameObject.name}: No microphone available");
                return;
            }

            // Store session info
            currentSessionDirectory = sessionDirectory;
            currentSessionID = sessionID;
            this.sessionStartTime = sessionStartTime;

            // Ensure session directory exists
            if (!Directory.Exists(currentSessionDirectory))
            {
                Directory.CreateDirectory(currentSessionDirectory);
            }

            // Setup CSV writer for annotations
            string csvFilename = $"{gameObject.name}_annotations.csv";
            try
            {
                csvWriter = new CSVWriter(
                    currentSessionDirectory,
                    csvFilename,
                    currentSessionID,
                    new string[] { "sim_time", "wav_filename" }
                );

                // Flush immediately for each annotation
                csvWriter.SetBufferFlushThreshold(1);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"VoiceAnnotationRecorder on {gameObject.name}: Failed to create CSV writer: {e.Message}");
                return;
            }

            annotationCount = 0;
            Debug.Log($"VoiceAnnotationRecorder on {gameObject.name}: Ready to record annotations");
#endif
        }

        [ContextMenu("Start Annotation")]
        public void StartAnnotation()
        {
#if UNITY_WEBGL
            Debug.LogWarning($"VoiceAnnotationRecorder on {gameObject.name}: Microphone recording not supported on WebGL");
            return;
#else
            // Check if we're in an active recording session
            if (csvWriter == null || string.IsNullOrEmpty(currentSessionDirectory))
            {
                Debug.LogWarning($"VoiceAnnotationRecorder on {gameObject.name}: Not in an active recording session. StartRecording() must be called first by RecordingManager.");
                return;
            }

            if (!enableRecording)
            {
                Debug.LogWarning($"VoiceAnnotationRecorder on {gameObject.name}: Recording disabled");
                return;
            }

            if (isRecording)
            {
                Debug.LogWarning($"VoiceAnnotationRecorder on {gameObject.name}: Already recording!");
                return;
            }

            if (string.IsNullOrEmpty(microphoneDevice))
            {
                Debug.LogError($"VoiceAnnotationRecorder on {gameObject.name}: No microphone available");
                return;
            }

            // Show progress indicator
            if (recordingProgressIndicator != null)
            {
                recordingProgressIndicator.SetActive(true);
            }

            // Start recording - buffer sized to maxRecordingDuration (+1s headroom), using specified sample rate
            int bufferSeconds = Mathf.Max(1, Mathf.CeilToInt(maxRecordingDuration) + 1);
            recordedClip = Microphone.Start(microphoneDevice, false, bufferSeconds, 44100);
            isRecording = true;
            recordingStartTime = Time.time;

            Debug.Log($"VoiceAnnotationRecorder on {gameObject.name}: Started annotation recording");

            // Stop after max duration
            Invoke("StopAnnotation", maxRecordingDuration);
#endif
        }

        [ContextMenu("Stop Annotation")]
        public void StopAnnotation()
        {
#if UNITY_WEBGL
            return;
#else
            if (!isRecording)
            {
                return;
            }

            // Cancel the auto-stop invoke if we're stopping early
            CancelInvoke("StopAnnotation");

            Microphone.End(microphoneDevice);
            isRecording = false;

            // Hide progress indicator
            if (recordingProgressIndicator != null)
            {
                recordingProgressIndicator.SetActive(false);
            }

            // Save the annotation
            string filename = SaveAnnotation();
            if (!string.IsNullOrEmpty(filename))
            {
                // Log to CSV with session-relative timestamp
                float currentTime = Time.time - sessionStartTime;
                csvWriter.WriteRow(currentTime, filename);

                annotationCount++;
                Debug.Log($"VoiceAnnotationRecorder on {gameObject.name}: Saved annotation #{annotationCount}: {filename}");
            }
#endif
        }

#if !UNITY_WEBGL
        private string SaveAnnotation()
        {
            if (recordedClip == null)
            {
                Debug.LogError($"VoiceAnnotationRecorder on {gameObject.name}: No recorded clip to save");
                return null;
            }

            string filename = $"{gameObject.name}_annotation_{annotationCount:D4}.wav";
            string filepath = Path.Combine(currentSessionDirectory, filename);

            try
            {
                SavWav.Save(filepath, recordedClip);
                Debug.Log($"VoiceAnnotationRecorder on {gameObject.name}: Saved to {filepath}");
                return filename;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"VoiceAnnotationRecorder on {gameObject.name}: Failed to save WAV: {e.Message}");
                return null;
            }
        }
#endif

        /// <summary>
        /// Stop recording session
        /// </summary>
        public void StopRecording()
        {
#if !UNITY_WEBGL
            // Stop any in-progress annotation
            if (isRecording)
            {
                StopAnnotation();
            }

            // Close CSV writer
            if (csvWriter != null)
            {
                csvWriter.Close();
                csvWriter = null;
            }

            Debug.Log($"VoiceAnnotationRecorder on {gameObject.name}: Stopped recording session. Total annotations: {annotationCount}");
#endif
        }

        /// <summary>
        /// Force flush CSV data to disk
        /// </summary>
        public void FlushData()
        {
#if !UNITY_WEBGL
            if (csvWriter != null)
            {
                csvWriter.Flush();
            }
#endif
        }

        void OnDisable()
        {
#if !UNITY_WEBGL
            if (isRecording)
            {
                StopAnnotation();
            }
#endif
        }

        void OnApplicationQuit()
        {
#if !UNITY_WEBGL
            if (isRecording)
            {
                StopAnnotation();
            }
#endif
        }
    }
}