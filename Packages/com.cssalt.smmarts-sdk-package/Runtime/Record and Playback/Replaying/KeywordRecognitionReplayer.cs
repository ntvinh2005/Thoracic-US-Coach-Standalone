using UnityEngine;
using System.Collections.Generic;

namespace SMMARTS
{
    /// <summary>
    /// Replays recorded speech recognition data from CSV files.
    /// Triggers speech recognition events at appropriate times during replay.
    /// Works with SpeechRecognitionManager to seamlessly switch between live and replay input.
    /// Singleton pattern ensures single speech replay instance.
    /// </summary>
    public class KeywordRecognitionReplayer : MonoBehaviour
    {
        #region Singleton
        public static KeywordRecognitionReplayer ME { get; private set; }

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

        [Header("Replay Settings")]
        [Tooltip("Enable speech recognition replay")]
        public bool enableReplay = true;

        [Header("Debug Info")]
        [SerializeField] private bool isReplaying = false;
        [SerializeField] private int totalPhrases = 0;
        [SerializeField] private int phrasesPlayed = 0;
        [SerializeField] private float currentTime = 0f;

        private CSVReader csvReader;
        private List<SpeechData> speechData;
        private float replayStartTime;
        private float pauseTime = 0f;
        private int nextPhraseIndex = 0;

        /// <summary>
        /// Structure to hold speech data for a single phrase
        /// </summary>
        [System.Serializable]
        public struct SpeechData
        {
            public float time;
            public string phrase;

            public SpeechData(float t, string p)
            {
                time = t;
                phrase = p;
            }
        }

        void Start()
        {
            speechData = new List<SpeechData>();
            Debug.Log("KeywordRecognitionReplayer initialized");
        }

        void Update()
        {
            if (isReplaying && enableReplay)
            {
                currentTime = Time.time - replayStartTime;
                CheckForPhrasesToPlay();
            }
        }

        /// <summary>
        /// Load CSV data for speech replay
        /// </summary>
        /// <param name="sessionDirectory">Directory containing session CSV files</param>
        /// <returns>True if loaded successfully</returns>
        public bool LoadReplayData(string sessionDirectory)
        {
            string csvPath = System.IO.Path.Combine(sessionDirectory, "KeywordCommands.csv");
            csvReader = new CSVReader(csvPath);

            if (!csvReader.LoadCSV())
            {
                Debug.LogWarning("No speech replay data found - this is okay if no speech was used");
                return false;
            }

            // Parse speech data from CSV
            return ParseSpeechData();
        }

        /// <summary>
        /// Parse CSV data into speech data structures
        /// </summary>
        private bool ParseSpeechData()
        {
            speechData.Clear();
            string[] headers = csvReader.ColumnHeaders;

            if (headers.Length < 2)
            {
                Debug.LogError("Invalid speech CSV - not enough columns");
                return false;
            }

            var rows = csvReader.GetAllRows();
            foreach (var row in rows)
            {
                try
                {
                    float time = CSVReader.ParseFloat(row["Time"]);
                    string phrase = row["Phrase"];

                    speechData.Add(new SpeechData(time, phrase));
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"Error parsing speech data row: {e.Message}");
                }
            }

            // Sort by time to ensure chronological order
            speechData.Sort((a, b) => a.time.CompareTo(b.time));

            totalPhrases = speechData.Count;
            Debug.Log($"Parsed {totalPhrases} speech phrases from replay data");
            return totalPhrases > 0;
        }

        /// <summary>
        /// Start replay from the beginning
        /// Called by ReplayerManager when replay begins
        /// </summary>
        /// <param name="startTime">Time when replay started</param>
        public void StartReplay(float startTime)
        {
            if (!enableReplay)
            {
                Debug.LogWarning("Speech replay is disabled");
                return;
            }

            if (speechData.Count == 0)
            {
                Debug.Log("No speech data to replay - continuing without speech replay");
                return;
            }

            replayStartTime = startTime;
            nextPhraseIndex = 0;
            phrasesPlayed = 0;
            isReplaying = true;

            Debug.Log($"Started speech replay with {totalPhrases} phrases");
        }

        /// <summary>
        /// Check if any phrases should be played at the current time
        /// </summary>
        private void CheckForPhrasesToPlay()
        {
            float replayTime = Time.time - replayStartTime;

            // Play all phrases that should have occurred by now
            while (nextPhraseIndex < speechData.Count)
            {
                SpeechData phrase = speechData[nextPhraseIndex];

                if (phrase.time <= replayTime)
                {
                    // Trigger this phrase
                    if (KeywordRecognitionManager.ME != null)
                    {
                        KeywordRecognitionManager.ME.ProcessRecognizedPhrase(phrase.phrase);
                    }

                    phrasesPlayed++;
                    nextPhraseIndex++;
                }
                else
                {
                    // This phrase is in the future, stop checking
                    break;
                }
            }
        }

        /// <summary>
        /// Stop replay
        /// </summary>
        public void StopReplay()
        {
            isReplaying = false;
            nextPhraseIndex = 0;
            phrasesPlayed = 0;
            currentTime = 0f;
            Debug.Log("Stopped speech replay");
        }

        /// <summary>
        /// Pause replay
        /// </summary>
        public void PauseReplay()
        {
            if (!isReplaying) return;
            pauseTime = Time.time;
            isReplaying = false;
            Debug.Log("Paused speech replay");
        }

        /// <summary>
        /// Resume replay
        /// </summary>
        public void ResumeReplay()
        {
            if (speechData.Count > 0 && enableReplay)
            {
                replayStartTime += Time.time - pauseTime;
                isReplaying = true;
                Debug.Log("Resumed speech replay");
            }
        }

        /// <summary>
        /// Seek to an absolute replay time (seconds). Repositions the phrase queue to
        /// the new time WITHOUT replaying the phrases that were skipped over.
        /// </summary>
        public void SeekToTime(float t)
        {
            replayStartTime = Time.time - t;
            pauseTime = Time.time;   // anchor pause compensation to the seek, not the original pause

            nextPhraseIndex = 0;
            while (nextPhraseIndex < speechData.Count && speechData[nextPhraseIndex].time <= t)
                nextPhraseIndex++;
            phrasesPlayed = nextPhraseIndex;
        }

        /// <summary>
        /// Check if replay is currently active
        /// </summary>
        public bool IsReplaying => isReplaying && enableReplay;

        /// <summary>
        /// Get current replay progress (0.0 to 1.0)
        /// </summary>
        public float GetReplayProgress()
        {
            if (totalPhrases == 0) return 0f;
            return (float)phrasesPlayed / totalPhrases;
        }

        /// <summary>
        /// Get total replay duration in seconds
        /// </summary>
        public float GetReplayDuration()
        {
            if (speechData.Count == 0) return 0f;
            return speechData[speechData.Count - 1].time;
        }

        /// <summary>
        /// Get number of phrases in replay data
        /// </summary>
        public int GetTotalPhrases() => totalPhrases;

        /// <summary>
        /// Get number of phrases played so far
        /// </summary>
        public int GetPhrasesPlayed() => phrasesPlayed;

        /// <summary>
        /// Context menu helper for testing
        /// </summary>
        [ContextMenu("Show Replay Status")]
        public void ShowReplayStatus()
        {
            Debug.Log($"KeywordRecognitionReplayer Status:");
            Debug.Log($"- Replay Enabled: {enableReplay}");
            Debug.Log($"- Is Replaying: {isReplaying}");
            Debug.Log($"- Total Phrases: {totalPhrases}");
            Debug.Log($"- Phrases Played: {phrasesPlayed}");
            Debug.Log($"- Current Time: {currentTime:F2}s");
        }
    }
}