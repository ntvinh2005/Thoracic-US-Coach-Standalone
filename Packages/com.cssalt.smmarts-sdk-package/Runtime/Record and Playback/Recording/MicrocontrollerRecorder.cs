using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace SMMARTS
{
    /// <summary>
    /// Records microcontroller sensor data during recording sessions.
    /// Dynamically adapts to current device configuration with flexible sensor types.
    /// Records device config metadata and all sensor values every frame for continuous data capture.
    /// Singleton pattern ensures single microcontroller recording instance.
    /// </summary>
    public class MicrocontrollerRecorder : MonoBehaviour
    {
        #region Singleton
        public static MicrocontrollerRecorder ME { get; private set; }

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

        [Header("Microcontroller Recording Settings")]
        [Tooltip("Enable to record microcontroller data")]
        public bool enableRecording = true;

        [Header("Debug Info")]
        [SerializeField] private bool isRecording = false;
        [SerializeField] private string currentDeviceType = "Not Connected";
        [SerializeField] private int sensorCount = 0;
        [SerializeField] private string[] currentSensorNames = new string[0];

        private CSVWriter csvWriter;
        private float recordingStartTime;
        private float pauseTime = 0f;
        private string[] availableSensors;

        void Update()
        {
            // Update debug display
            if (Microcontroller_Manager.ME != null)
            {
                currentDeviceType = Microcontroller_Manager.ME.CurrentDeviceType;
                currentSensorNames = Microcontroller_Manager.ME.GetAvailableSensors();
                sensorCount = currentSensorNames.Length;
            }

            // Record data every frame if recording is active
            if (isRecording && enableRecording && csvWriter != null)
            {
                RecordCurrentSensorData();
            }
        }

        /// <summary>
        /// Start recording microcontroller data
        /// Called by RecordingManager when recording begins
        /// </summary>
        /// <param name="sessionDirectory">Directory path for this recording session</param>
        /// <param name="sessionID">Unique session identifier</param>
        /// <param name="startTime">Time when recording started</param>
        public void StartRecording(string sessionDirectory, string sessionID, float startTime)
        {
            if (!enableRecording)
            {
                Debug.Log("Microcontroller recording disabled");
                return;
            }

            if (Microcontroller_Manager.ME == null)
            {
                Debug.LogError("Microcontroller_Manager not found! Cannot record microcontroller data.");
                return;
            }

            recordingStartTime = startTime;

            // Get current device configuration
            availableSensors = Microcontroller_Manager.ME.GetAvailableSensors();

            if (availableSensors.Length == 0)
            {
                Debug.LogWarning("No sensors available from microcontroller - recording with empty data");
            }

            // Build dynamic column headers: Time + all available sensor names
            List<string> headers = new List<string> { "Time" };
            headers.AddRange(availableSensors);

            // Initialize CSV writer
            csvWriter = new CSVWriter(sessionDirectory, "Microcontroller.csv", sessionID, headers.ToArray());

            // Log device configuration for debugging (not written to the CSV file)
            LogDeviceConfig();

            isRecording = true;
            Debug.Log($"Started recording microcontroller data - Device: {currentDeviceType}, Sensors: {availableSensors.Length}");
        }

        /// <summary>
        /// Log device configuration metadata to the console for debugging.
        /// (CSVWriter does not support comment lines, so this is not written to the CSV file.)
        /// </summary>
        private void LogDeviceConfig()
        {
            if (csvWriter == null || Microcontroller_Manager.ME == null) return;

            try
            {
                string deviceType = Microcontroller_Manager.ME.CurrentDeviceType;

                var configLines = new List<string>
                {
                    $"# Device Type: {deviceType}",
                    $"# Sensor Count: {availableSensors.Length}",
                    $"# Sensors: {string.Join(", ", availableSensors)}",
                    $"# Recording Started: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    "# --- Data Start ---"
                };

                foreach (string line in configLines)
                {
                    Debug.Log($"Device Config: {line}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Error logging device config: {e.Message}");
            }
        }

        /// <summary>
        /// Record current sensor data to CSV (called every frame)
        /// </summary>
        private void RecordCurrentSensorData()
        {
            if (Microcontroller_Manager.ME == null) return;

            float currentTime = Time.time - recordingStartTime;

            // Build row data: timestamp + all sensor values
            List<object> rowData = new List<object> { currentTime };

            foreach (string sensorName in availableSensors)
            {
                float sensorValue = Microcontroller_Manager.ME.GetSensorValue(sensorName);
                rowData.Add(sensorValue);
            }

            csvWriter.WriteRow(rowData.ToArray());
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
            Debug.Log("Stopped recording microcontroller data");
        }

        /// <summary>
        /// Pause microcontroller recording
        /// </summary>
        public void PauseRecording()
        {
            if (!isRecording) return;
            pauseTime = Time.time;
            isRecording = false;
            Debug.Log("Paused microcontroller recording");
        }

        /// <summary>
        /// Resume microcontroller recording
        /// </summary>
        public void ResumeRecording()
        {
            if (csvWriter != null && enableRecording)
            {
                recordingStartTime += Time.time - pauseTime;
                isRecording = true;
                Debug.Log("Resumed microcontroller recording");
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
        /// Get current sensor values for debugging
        /// </summary>
        public Dictionary<string, float> GetCurrentSensorValues()
        {
            var values = new Dictionary<string, float>();

            if (Microcontroller_Manager.ME != null)
            {
                foreach (string sensorName in availableSensors ?? new string[0])
                {
                    values[sensorName] = Microcontroller_Manager.ME.GetSensorValue(sensorName);
                }
            }

            return values;
        }
    }
}