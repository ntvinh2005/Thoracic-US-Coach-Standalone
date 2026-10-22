using System.Collections.Generic;
using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Records speech recognition events during recording sessions. 
    /// explicitly called by SpeechRecognitionManager; does not use Windows.Speech. This is just the recorder.
    /// Logs recognized phrases with timestamps to CSV file.
    /// Singleton pattern ensures single speech recording instance.
    /// </summary>
    public class KeywordRecognitionRecorder : MonoBehaviour
    {
        #region Singleton
        public static KeywordRecognitionRecorder ME { get; private set; }

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
        [Tooltip("Enable to record speech recognition data")]
        public bool enableRecording = true;

        [Header("Debug Info")]
        [SerializeField] private bool isRecording = false;
        [SerializeField] private int phrasesRecorded = 0;

        private CSVWriter csvWriter;
        private float recordingStartTime;
        private float pauseTime = 0f;

        void Start()
        {
            Debug.Log("KeywordRecognitionRecorder initialized");
        }

        /// <summary>
        /// Start recording speech data
        /// Called by RecordingManager when recording begins
        /// </summary>
        /// <param name="sessionDirectory">Directory path for this recording session</param>
        /// <param name="sessionID">Unique session identifier</param>
        /// <param name="startTime">Time when recording started</param>
        public void StartRecording(string sessionDirectory, string sessionID, float startTime)
        {
            if (!enableRecording)
            {
                Debug.Log("Speech recording disabled");
                return;
            }

            recordingStartTime = startTime;
            phrasesRecorded = 0;

            // Initialize CSV writer with Time and Phrase columns
            string[] headers = { "Time", "Phrase" };
            csvWriter = new CSVWriter(sessionDirectory, "KeywordCommands.csv", sessionID, headers);

            isRecording = true;
            Debug.Log("Started recording speech data");
        }

        /// <summary>
        /// Log a recognized phrase with timestamp
        /// Called by SpeechRecognitionManager when phrase is recognized
        /// </summary>
        /// <param name="phrase">The recognized phrase</param>
        public void LogPhrase(string phrase) => LogEntry(phrase, "speech");

        /// <summary>
        /// Log a recognized event with timestamp
        /// Called by SpeechRecognitionManager when an event mark dictation has ended
        /// </summary>
        /// <param name="phrase">The recognized phrase</param>
        public void LogEventMark(string phrase) => LogEntry(phrase, "event");

        /// <summary>
        /// Shared writer for recognized phrases and event marks.
        /// </summary>
        private void LogEntry(string phrase, string kind)
        {
            if (!isRecording || !enableRecording || csvWriter == null)
                return;

            float currentTime = Time.time - recordingStartTime;

            // Write row with timestamp and phrase
            object[] rowData = { currentTime, phrase };
            csvWriter.WriteRow(rowData);

            phrasesRecorded++;
            Debug.Log($"Logged {kind}: [{currentTime:F2}s] {phrase}");
        }

        /// <summary>
        /// Stop recording and finalize the CSV file
        /// </summary>
        public void StopRecording()
        {
            if (csvWriter != null)
            {
                csvWriter.Close();
                csvWriter = null;
            }

            isRecording = false;
            Debug.Log($"Stopped recording speech data - {phrasesRecorded} phrases recorded");
        }

        /// <summary>
        /// Pause speech recording
        /// </summary>
        public void PauseRecording()
        {
            if (!isRecording) return;
            pauseTime = Time.time;
            isRecording = false;
            Debug.Log("Paused speech recording");
        }

        /// <summary>
        /// Resume speech recording
        /// </summary>
        public void ResumeRecording()
        {
            if (csvWriter != null && enableRecording)
            {
                recordingStartTime += Time.time - pauseTime;
                isRecording = true;
                Debug.Log("Resumed speech recording");
            }
        }

        /// <summary>
        /// Force flush any buffered data to disk
        /// </summary>
        public void FlushData()
        {
            if (csvWriter != null)
            {
                csvWriter.Flush();
            }
        }

        /// <summary>
        /// Check if this recorder is currently active
        /// </summary>
        public bool IsRecording => isRecording && enableRecording;

        /// <summary>
        /// Get number of phrases recorded in current session
        /// </summary>
        public int PhrasesRecorded => phrasesRecorded;

        /// <summary>
        /// Context menu helper for testing
        /// </summary>
        [ContextMenu("Show Recording Status")]
        public void ShowRecordingStatus()
        {
            Debug.Log($"KeywordRecognitionRecorder Status:");
            Debug.Log($"- Recording Enabled: {enableRecording}");
            Debug.Log($"- Is Recording: {isRecording}");
            Debug.Log($"- Phrases Recorded: {phrasesRecorded}");
        }
    }
}