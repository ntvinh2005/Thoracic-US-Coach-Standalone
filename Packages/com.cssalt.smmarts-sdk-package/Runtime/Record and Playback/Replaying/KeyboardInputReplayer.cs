using UnityEngine;
using System.Collections.Generic;

namespace SMMARTS
{
    /// <summary>
    /// Replays recorded keyboard input data from CSV files.
    /// Handles keyboard state transitions and provides GetKey/GetKeyDown/GetKeyUp during replay.
    /// Works with KeyboardInputManager to seamlessly switch between live and replay input.
    /// Singleton pattern ensures single keyboard replay instance.
    /// </summary>
    public class KeyboardInputReplayer : MonoBehaviour
    {
        #region Singleton
        public static KeyboardInputReplayer ME { get; private set; }
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
        [Tooltip("Enable keyboard input replay")]
        public bool enableReplay = true;

        [Header("Debug Info")]
        [SerializeField] private bool isReplaying = false;
        [SerializeField] private int totalFrames = 0;
        [SerializeField] private int currentFrame = 0;
        [SerializeField] private float currentTime = 0f;
        [SerializeField] private string[] availableKeys = new string[0];

        private CSVReader csvReader;
        private List<KeyData> keyData;
        private float replayStartTime;
        private float pauseTime = 0f;
        private int lastFrameIndex = 0;
        private Dictionary<KeyCode, bool> currentKeyStates;
        private Dictionary<KeyCode, bool> previousKeyStates;

        // Mapping from CSV column names to KeyCode enums
        private Dictionary<string, KeyCode> keyMapping = new Dictionary<string, KeyCode>
        {
            {"EscapeKey", KeyCode.Escape},
            {"F1Key", KeyCode.F1},
            {"TabKey", KeyCode.Tab},
            {"ReturnKey", KeyCode.Return},
            {"SpaceKey", KeyCode.Space}
            // Add more mappings as needed
        };

        /// <summary>
        /// Structure to hold keyboard data for a single frame
        /// </summary>
        [System.Serializable]
        public struct KeyData
        {
            public float time;
            public Dictionary<KeyCode, bool> keyStates;

            public KeyData(float t, Dictionary<KeyCode, bool> states)
            {
                time = t;
                keyStates = new Dictionary<KeyCode, bool>(states);
            }
        }

        void Start()
        {
            keyData = new List<KeyData>();
            currentKeyStates = new Dictionary<KeyCode, bool>();
            previousKeyStates = new Dictionary<KeyCode, bool>();
            // Debug.Log("KeyboardInputReplayer initialized");
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
        /// Load CSV data for keyboard replay
        /// </summary>
        /// <param name="sessionDirectory">Directory containing session CSV files</param>
        /// <returns>True if loaded successfully</returns>
        public bool LoadReplayData(string sessionDirectory)
        {
            string csvPath = System.IO.Path.Combine(sessionDirectory, "KeyCommands.csv");
            csvReader = new CSVReader(csvPath);

            if (!csvReader.LoadCSV())
            {
                Debug.LogError("Failed to load keyboard replay data");
                return false;
            }

            // Parse keyboard data from CSV
            return ParseKeyboardData();
        }

        /// <summary>
        /// Parse CSV data into keyboard data structures
        /// </summary>
        private bool ParseKeyboardData()
        {
            keyData.Clear();
            string[] headers = csvReader.ColumnHeaders;

            if (headers.Length < 2)
            {
                Debug.LogError("Invalid keyboard CSV - not enough columns");
                return false;
            }

            // Extract key names (all columns except "Time") and resolve each to a KeyCode.
            // The recorder writes columns as "<KeyCode>Key" (e.g. "EscapeKey", "AKey"), so we
            // strip the trailing "Key" and parse the enum - this supports ANY recorded key,
            // not just a hardcoded subset.
            List<string> keys = new List<string>();
            for (int i = 1; i < headers.Length; i++) // Skip "Time" column
            {
                string column = headers[i];
                if (TryResolveKeyCode(column, out KeyCode code))
                {
                    keyMapping[column] = code;
                    keys.Add(column);
                }
                else
                {
                    Debug.LogWarning($"Unknown key mapping for column: {column}");
                }
            }
            availableKeys = keys.ToArray();

            var rows = csvReader.GetAllRows();
            foreach (var row in rows)
            {
                try
                {
                    float time = CSVReader.ParseFloat(row["Time"]);

                    // Parse all key states for this frame
                    Dictionary<KeyCode, bool> states = new Dictionary<KeyCode, bool>();
                    foreach (string keyName in availableKeys)
                    {
                        if (row.ContainsKey(keyName) && keyMapping.ContainsKey(keyName))
                        {
                            KeyCode keyCode = keyMapping[keyName];
                            bool keyState = bool.Parse(row[keyName]);
                            states[keyCode] = keyState;
                        }
                    }

                    keyData.Add(new KeyData(time, states));
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"Error parsing keyboard data row: {e.Message}");
                }
            }

            totalFrames = keyData.Count;
            Debug.Log($"Parsed {totalFrames} keyboard frames with {availableKeys.Length} keys: {string.Join(", ", availableKeys)}");
            return totalFrames > 0;
        }

        /// <summary>
        /// Resolve a CSV column name (e.g. "EscapeKey") to its KeyCode by stripping the
        /// trailing "Key" suffix written by KeyboardInputRecorder. Supports any recorded key.
        /// </summary>
        private bool TryResolveKeyCode(string column, out KeyCode code)
        {
            code = KeyCode.None;
            if (string.IsNullOrEmpty(column)) return false;

            string name = column.EndsWith("Key") ? column.Substring(0, column.Length - 3) : column;
            return System.Enum.TryParse(name, out code);
        }

        /// <summary>
        /// Start replay from the beginning
        /// Called by ReplayerManager when replay begins
        /// </summary>
        /// <param name="startTime">Time when replay started</param>
        public void StartReplay(float startTime)
        {
            if (!enableReplay || keyData.Count == 0)
            {
                Debug.LogWarning("Cannot start keyboard replay - no data or disabled");
                return;
            }

            replayStartTime = startTime;
            lastFrameIndex = 0;
            currentFrame = 0;
            isReplaying = true;

            // Initialize key states
            currentKeyStates.Clear();
            previousKeyStates.Clear();
            foreach (var mapping in keyMapping)
            {
                currentKeyStates[mapping.Value] = false;
                previousKeyStates[mapping.Value] = false;
            }

            Debug.Log($"Started keyboard replay with {totalFrames} frames");
        }

        /// <summary>
        /// Stop replay
        /// </summary>
        public void StopReplay()
        {
            isReplaying = false;
            currentFrame = 0;
            currentTime = 0f;
            currentKeyStates.Clear();
            previousKeyStates.Clear();
            Debug.Log("Stopped keyboard replay");
        }

        /// <summary>
        /// Pause replay
        /// </summary>
        public void PauseReplay()
        {
            if (!isReplaying) return;
            pauseTime = Time.time;
            isReplaying = false;
            Debug.Log("Paused keyboard replay");
        }

        /// <summary>
        /// Resume replay
        /// </summary>
        public void ResumeReplay()
        {
            if (keyData.Count > 0 && enableReplay)
            {
                replayStartTime += Time.time - pauseTime;
                isReplaying = true;
                Debug.Log("Resumed keyboard replay");
            }
        }

        /// <summary>
        /// Seek to an absolute replay time (seconds). Repositions to the frame at t and
        /// sets the key state so no phantom GetKeyDown/GetKeyUp fires on the next frame.
        /// </summary>
        public void SeekToTime(float t)
        {
            replayStartTime = Time.time - t;
            pauseTime = Time.time;   // anchor pause compensation to the seek, not the original pause
            lastFrameIndex = 0;   // allow the forward-only search to find earlier frames

            if (currentKeyStates == null || previousKeyStates == null || keyData.Count == 0)
                return;

            int frameIndex = FindFrameIndex(t);
            if (frameIndex >= keyData.Count) frameIndex = keyData.Count - 1;

            currentKeyStates.Clear();
            foreach (var kvp in keyData[frameIndex].keyStates)
                currentKeyStates[kvp.Key] = kvp.Value;

            previousKeyStates.Clear();
            foreach (var kvp in currentKeyStates)
                previousKeyStates[kvp.Key] = kvp.Value;
        }

        /// <summary>
        /// Get current key state (equivalent to Input.GetKey)
        /// Called by KeyboardInputManager during replay
        /// </summary>
        /// <param name="key">KeyCode to check</param>
        /// <returns>True if key is currently pressed</returns>
        public bool GetKeyState(KeyCode key)
        {
            if (!isReplaying || keyData.Count == 0)
                return false;

            UpdateKeyStates();
            return currentKeyStates.ContainsKey(key) ? currentKeyStates[key] : false;
        }

        /// <summary>
        /// Check if key was pressed this frame (equivalent to Input.GetKeyDown)
        /// </summary>
        /// <param name="key">KeyCode to check</param>
        /// <returns>True if key was pressed this frame</returns>
        public bool GetKeyDown(KeyCode key)
        {
            if (!isReplaying || keyData.Count == 0)
                return false;

            UpdateKeyStates();

            bool current = currentKeyStates.ContainsKey(key) ? currentKeyStates[key] : false;
            bool previous = previousKeyStates.ContainsKey(key) ? previousKeyStates[key] : false;

            return current && !previous;
        }

        /// <summary>
        /// Check if key was released this frame (equivalent to Input.GetKeyUp)
        /// </summary>
        /// <param name="key">KeyCode to check</param>
        /// <returns>True if key was released this frame</returns>
        public bool GetKeyUp(KeyCode key)
        {
            if (!isReplaying || keyData.Count == 0)
                return false;

            UpdateKeyStates();

            bool current = currentKeyStates.ContainsKey(key) ? currentKeyStates[key] : false;
            bool previous = previousKeyStates.ContainsKey(key) ? previousKeyStates[key] : false;

            return !current && previous;
        }

        /// <summary>
        /// Update key states for current replay time
        /// </summary>
        private void UpdateKeyStates()
        {
            float replayTime = Time.time - replayStartTime;



            // Find the appropriate frame for current time
            int frameIndex = FindFrameIndex(replayTime);
            if (frameIndex >= keyData.Count)
            {
                // Replay finished - maintain last state
                return;
            }


           // Debug.Log("Keyboard Input Replayer / UpdateKeyStates replayTime is " + replayTime);
           // Debug.Log("Keyboard Input Replayer / UpdateKeyStates frameIndex is " + frameIndex);

            currentFrame = frameIndex;

            // Store previous states before updating
            previousKeyStates.Clear();
            foreach (var kvp in currentKeyStates)
            {
                previousKeyStates[kvp.Key] = kvp.Value;
            }

            // Update current states from replay data
            var frameData = keyData[frameIndex];
            foreach (var kvp in frameData.keyStates)
            {
                currentKeyStates[kvp.Key] = kvp.Value;
            }
        }

        /// <summary>
        /// Find the frame index for a given time
        /// </summary>
        private int FindFrameIndex(float time)
        {
            // Find the most recent frame at or before the current time
            int targetFrame = 0;

            for (int i = lastFrameIndex; i < keyData.Count; i++)
            {
                if (keyData[i].time <= time)
                {
                    targetFrame = i;
                }
                else
                {
                    break;
                }
            }

            lastFrameIndex = targetFrame;
            return targetFrame;
        }

        /// <summary>
        /// Check if replay data exists for a specific key
        /// </summary>
        /// <param name="key">KeyCode to check</param>
        /// <returns>True if key data is available</returns>
        public bool HasKeyData(KeyCode key)
        {
            // Check if this key is in our mapping and available in the data
            foreach (var mapping in keyMapping)
            {
                if (mapping.Value == key && System.Array.Exists(availableKeys, k => k == mapping.Key))
                {
                    return true;
                }
            }
            return false;
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
            if (keyData.Count == 0) return 0f;
            return keyData[keyData.Count - 1].time;
        }

        /// <summary>
        /// Get list of available keys in replay data
        /// </summary>
        public string[] GetAvailableKeys() => availableKeys;
    }
}