using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Records simulation events with flexible value types during recording sessions.
    /// Handles any event with any value: booleans, numbers, strings, etc.
    /// Simple name-value pair format for maximum flexibility and easy analysis.
    /// Singleton pattern ensures single event recording instance.
    /// </summary>
    public class EventMarkerRecorder : MonoBehaviour
    {
        #region Singleton
        public static EventMarkerRecorder ME { get; private set; }

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

        [Header("Event Recording Settings")]
        [Tooltip("Enable to record event data")]
        public bool enableRecording = true;

        [Header("Debug Info")]
        [SerializeField] private bool isRecording = false;
        [SerializeField] private int eventsRecordedThisSession = 0;

        private CSVWriter csvWriter;
        private float recordingStartTime;

        /// <summary>
        /// Start recording event data
        /// Called by RecordingManager when recording begins
        /// </summary>
        /// <param name="sessionDirectory">Directory path for this recording session</param>
        /// <param name="sessionID">Unique session identifier</param>
        /// <param name="startTime">Time when recording started</param>
        public void StartRecording(string sessionDirectory, string sessionID, float startTime)
        {
            if (!enableRecording)
            {
                Debug.Log("Value event recording disabled");
                return;
            }

            recordingStartTime = startTime;
            eventsRecordedThisSession = 0;

            // Simple 3-column format: Time, EventName, Value
            string[] headers = { "Time", "EventName", "Value" };

            // Initialize CSV writer
            csvWriter = new CSVWriter(sessionDirectory, "EventMarkers.csv", sessionID, headers);

            isRecording = true;
            Debug.Log("Started recording value events");
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
            Debug.Log($"Stopped recording value events - recorded {eventsRecordedThisSession} events");
        }

        /// <summary>
        /// Pause event recording
        /// </summary>
        public void PauseRecording()
        {
            isRecording = false;
            Debug.Log("Paused value event recording");
        }

        /// <summary>
        /// Resume event recording
        /// </summary>
        public void ResumeRecording()
        {
            if (csvWriter != null && enableRecording)
            {
                isRecording = true;
                Debug.Log("Resumed value event recording");
            }
        }

        /// <summary>
        /// Record an event with any value type
        /// </summary>
        /// <param name="eventName">Name of the event</param>
        /// <param name="value">Value of any type (bool, int, float, string, etc.)</param>
        public void RecordEvent(string eventName, object value)
        {
            if (!isRecording || !enableRecording || csvWriter == null)
                return;

            float currentTime = Time.time - recordingStartTime;

            // Convert value to string, handling null gracefully
            string valueString = value?.ToString() ?? "";

            // Write the event
            csvWriter.WriteRow(currentTime, eventName, valueString);
            eventsRecordedThisSession++;

            Debug.Log($"Recorded event: {eventName} = {value}");
        }

        /// <summary>
        /// Convenience method for boolean events
        /// </summary>
        public void RecordBooleanEvent(string eventName, bool value)
        {
            RecordEvent(eventName, value);
        }

        /// <summary>
        /// Convenience method for numeric events
        /// </summary>
        public void RecordNumericEvent(string eventName, float value)
        {
            RecordEvent(eventName, value);
        }

        /// <summary>
        /// Convenience method for string events
        /// </summary>
        public void RecordStringEvent(string eventName, string value)
        {
            RecordEvent(eventName, value);
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
        /// Context menu helpers for testing
        /// </summary>
        [ContextMenu("Test Boolean Event")]
        public void TestBooleanEvent()
        {
            RecordEvent("TestBoolean", true);
        }

        [ContextMenu("Test Numeric Event")]
        public void TestNumericEvent()
        {
            RecordEvent("TestNumber", 123.45f);
        }

        [ContextMenu("Test String Event")]
        public void TestStringEvent()
        {
            RecordEvent("TestString", "Hello World");
        }
    }
}