using UnityEngine;
using System.Collections.Generic;

namespace SMMARTS
{
    /// <summary>
    /// Replays recorded microcontroller sensor data from CSV files.
    /// Handles dynamic sensor configurations and provides interpolated values during replay.
    /// Works with Microcontroller_Manager to seamlessly switch between live and replay data.
    /// Singleton pattern ensures single microcontroller replay instance.
    /// </summary>
    public class MicrocontrollerReplayer : MonoBehaviour
    {
        #region Singleton
        public static MicrocontrollerReplayer ME { get; private set; }

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
        [Tooltip("Enable microcontroller data replay")]
        public bool enableReplay = true;

        [Header("Interpolation")]
        [Tooltip("Smooth values between recorded points")]
        public bool useInterpolation = true;

        [Header("Debug Info")]
        [SerializeField] private bool isReplaying = false;
        [SerializeField] private int totalFrames = 0;
        [SerializeField] private int currentFrame = 0;
        [SerializeField] private float currentTime = 0f;
        [SerializeField] private string[] availableSensors = new string[0];

        private CSVReader csvReader;
        private List<SensorData> sensorData;
        private float replayStartTime;
        private float pauseTime = 0f;
        private int lastFrameIndex = 0;

        /// <summary>
        /// Structure to hold sensor data for a single frame
        /// </summary>
        [System.Serializable]
        public struct SensorData
        {
            public float time;
            public Dictionary<string, float> sensorValues;

            public SensorData(float t, Dictionary<string, float> values)
            {
                time = t;
                sensorValues = new Dictionary<string, float>(values);
            }
        }

        void Start()
        {
            sensorData = new List<SensorData>();
            // Debug.Log("MicrocontrollerReplayer initialized");
        }

        void Update()
        {
            // Update current time for debug display
            if (isReplaying && enableReplay)
            {
                currentTime = Time.time - replayStartTime;
            }
        }

        /// <summary>
        /// Load CSV data for microcontroller replay
        /// </summary>
        /// <param name="sessionDirectory">Directory containing session CSV files</param>
        /// <returns>True if loaded successfully</returns>
        public bool LoadReplayData(string sessionDirectory)
        {
            string csvPath = System.IO.Path.Combine(sessionDirectory, "Microcontroller.csv");

            csvReader = new CSVReader(csvPath);
            if (!csvReader.LoadCSV())
            {
                Debug.LogError("Failed to load microcontroller replay data");
                return false;
            }

            // Parse sensor data from CSV
            return ParseSensorData();
        }

        /// <summary>
        /// Parse CSV data into sensor data structures
        /// </summary>
        private bool ParseSensorData()
        {
            sensorData.Clear();

            string[] headers = csvReader.ColumnHeaders;
            if (headers.Length < 2)
            {
                Debug.LogError("Invalid microcontroller CSV - not enough columns");
                return false;
            }

            // Extract sensor names (all columns except "Time")
            List<string> sensors = new List<string>();
            for (int i = 1; i < headers.Length; i++) // Skip "Time" column
            {
                sensors.Add(headers[i]);
            }
            availableSensors = sensors.ToArray();

            var rows = csvReader.GetAllRows();
            foreach (var row in rows)
            {
                try
                {
                    float time = CSVReader.ParseFloat(row["Time"]);

                    // Parse all sensor values for this frame
                    Dictionary<string, float> values = new Dictionary<string, float>();
                    foreach (string sensorName in availableSensors)
                    {
                        if (row.ContainsKey(sensorName))
                        {
                            values[sensorName] = CSVReader.ParseFloat(row[sensorName]);
                        }
                    }

                    sensorData.Add(new SensorData(time, values));
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"Error parsing microcontroller data row: {e.Message}");
                }
            }

            totalFrames = sensorData.Count;
            // Debug.Log($"Parsed {totalFrames} microcontroller frames with {availableSensors.Length} sensors: {string.Join(", ", availableSensors)}");

            return totalFrames > 0;
        }

        /// <summary>
        /// Start replay from the beginning
        /// Called by ReplayerManager when replay begins
        /// </summary>
        /// <param name="startTime">Time when replay started</param>
        public void StartReplay(float startTime)
        {
            if (!enableReplay || sensorData.Count == 0)
            {
                Debug.LogWarning("Cannot start microcontroller replay - no data or disabled");
                return;
            }

            replayStartTime = startTime;
            lastFrameIndex = 0;
            currentFrame = 0;
            isReplaying = true;

            // Debug.Log($"Started microcontroller replay with {totalFrames} frames");
        }

        /// <summary>
        /// Stop replay
        /// </summary>
        public void StopReplay()
        {
            isReplaying = false;
            currentFrame = 0;
            currentTime = 0f;
            // Debug.Log("Stopped microcontroller replay");
        }

        /// <summary>
        /// Pause replay
        /// </summary>
        public void PauseReplay()
        {
            if (!isReplaying) return;
            pauseTime = Time.time;
            isReplaying = false;
            // Debug.Log("Paused microcontroller replay");
        }

        /// <summary>
        /// Resume replay
        /// </summary>
        public void ResumeReplay()
        {
            if (sensorData.Count > 0 && enableReplay)
            {
                replayStartTime += Time.time - pauseTime;
                isReplaying = true;
                // Debug.Log("Resumed microcontroller replay");
            }
        }

        /// <summary>
        /// Seek to an absolute replay time (seconds). Sensor values are pulled on demand
        /// via GetReplayValue using the new time; resets the search so backward seeks work.
        /// </summary>
        public void SeekToTime(float t)
        {
            replayStartTime = Time.time - t;
            pauseTime = Time.time;   // anchor pause compensation to the seek, not the original pause
            lastFrameIndex = 0;   // allow the forward-only search to find earlier frames
        }

        /// <summary>
        /// Get replayed sensor value with interpolation
        /// Called by Microcontroller_Manager during replay
        /// </summary>
        /// <param name="sensorName">Name of sensor to get value for</param>
        /// <returns>Interpolated sensor value or 0 if not found</returns>
        public float GetReplayValue(string sensorName)
        {
            if (!isReplaying || sensorData.Count == 0)
                return 0f;

            float replayTime = Time.time - replayStartTime;

            // Find the appropriate frame(s) for current time
            int frameIndex = FindFrameIndex(replayTime);

            if (frameIndex >= sensorData.Count)
            {
                // Replay finished - return last recorded value
                return GetSensorValueFromFrame(sensorData.Count - 1, sensorName);
            }

            currentFrame = frameIndex;

            if (useInterpolation && frameIndex < sensorData.Count - 1)
            {
                // Interpolate between current and next frame
                return GetInterpolatedSensorValue(frameIndex, sensorName, replayTime);
            }
            else
            {
                // Return exact frame value
                return GetSensorValueFromFrame(frameIndex, sensorName);
            }
        }

        /// <summary>
        /// Check if replay data exists for a specific sensor
        /// </summary>
        /// <param name="sensorName">Name of sensor to check</param>
        /// <returns>True if sensor data is available</returns>
        public bool HasSensorData(string sensorName)
        {
            return System.Array.Exists(availableSensors, s => s == sensorName);
        }

        /// <summary>
        /// Find the frame index for a given time
        /// </summary>
        private int FindFrameIndex(float time)
        {
            // Start search from last known position for efficiency
            for (int i = lastFrameIndex; i < sensorData.Count; i++)
            {
                if (sensorData[i].time >= time)
                {
                    lastFrameIndex = Mathf.Max(0, i - 1);
                    return i;
                }
            }

            // Time is beyond all recorded data
            return sensorData.Count;
        }

        /// <summary>
        /// Get sensor value from a specific frame
        /// </summary>
        private float GetSensorValueFromFrame(int frameIndex, string sensorName)
        {
            if (frameIndex < 0 || frameIndex >= sensorData.Count)
                return 0f;

            var frame = sensorData[frameIndex];
            return frame.sensorValues.ContainsKey(sensorName) ? frame.sensorValues[sensorName] : 0f;
        }

        /// <summary>
        /// Get interpolated sensor value between two frames
        /// </summary>
        private float GetInterpolatedSensorValue(int frameIndex, string sensorName, float time)
        {
            var currentFrame = sensorData[frameIndex];
            var nextFrame = sensorData[frameIndex + 1];

            // Get values from both frames
            float currentValue = currentFrame.sensorValues.ContainsKey(sensorName) ? currentFrame.sensorValues[sensorName] : 0f;
            float nextValue = nextFrame.sensorValues.ContainsKey(sensorName) ? nextFrame.sensorValues[sensorName] : 0f;

            // Calculate interpolation factor
            float timeDiff = nextFrame.time - currentFrame.time;
            float t = (timeDiff > 0) ? (time - currentFrame.time) / timeDiff : 0f;
            t = Mathf.Clamp01(t);

            // Interpolate between values
            return Mathf.Lerp(currentValue, nextValue, t);
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
            if (totalFrames == 0) return 0f;
            return (float)currentFrame / totalFrames;
        }

        /// <summary>
        /// Get total replay duration in seconds
        /// </summary>
        public float GetReplayDuration()
        {
            if (sensorData.Count == 0) return 0f;
            return sensorData[sensorData.Count - 1].time;
        }

        /// <summary>
        /// Get list of available sensors in replay data
        /// </summary>
        public string[] GetAvailableSensors() => availableSensors;
    }
}