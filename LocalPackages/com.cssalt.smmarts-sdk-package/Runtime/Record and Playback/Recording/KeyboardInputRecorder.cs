using System.Collections.Generic;
using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Records generic system and UI key presses during recording sessions.
    /// Handles predefined keys like Escape, F1, Tab, etc.
    /// Singleton pattern ensures single input recording instance.
    /// </summary>
    public class KeyboardInputRecorder : MonoBehaviour
    {
        #region Singleton
        public static KeyboardInputRecorder ME { get; private set; }

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

        [Header("Input Recording Settings")]
        [Tooltip("Keys to monitor and record during sessions")]
        public KeyCode[] keysToTrack = {
            KeyCode.Escape,
            KeyCode.F1,
            KeyCode.Tab,
            KeyCode.Return,
            KeyCode.Space
        };

        [Tooltip("Record every frame (true) or only when a key changes (false)")]
        public bool recordEveryFrame = true;

        [Tooltip("Enable to record key input data")]
        public bool enableRecording = true;

        [Header("Debug Info")]
        [SerializeField] private bool isRecording = false;
        [SerializeField] private int trackedKeyCount = 0;

        private CSVWriter csvWriter;
        private float recordingStartTime;
        private float pauseTime = 0f;
        private Dictionary<KeyCode, bool> previousKeyStates;
        private Dictionary<KeyCode, bool> currentKeyStates;

        void Start()
        {
            trackedKeyCount = keysToTrack.Length;

            // Initialize key state tracking
            previousKeyStates = new Dictionary<KeyCode, bool>();
            currentKeyStates = new Dictionary<KeyCode, bool>();

            foreach (KeyCode key in keysToTrack)
            {
                previousKeyStates[key] = false;
                currentKeyStates[key] = false;
            }

            // Debug.Log($"KeyboardInputRecorder initialized - tracking {trackedKeyCount} keys");
        }

        void Update()
        {
            // Only record if we're in an active recording session
            if (isRecording && enableRecording && csvWriter != null)
            {
                RecordKeyStates();
            }
        }

        /// <summary>
        /// Start recording input data
        /// Called by RecordingManager when recording begins
        /// </summary>
        /// <param name="sessionDirectory">Directory path for this recording session</param>
        /// <param name="sessionID">Unique session identifier</param>
        /// <param name="startTime">Time when recording started</param>
        public void StartRecording(string sessionDirectory, string sessionID, float startTime)
        {
            if (!enableRecording)
            {
                // Debug.Log("Input recording disabled");
                return;
            }

            recordingStartTime = startTime;

            // Build column headers dynamically based on tracked keys
            List<string> headers = new List<string> { "Time" };
            foreach (KeyCode key in keysToTrack)
            {
                headers.Add(key.ToString() + "Key");
            }

            // Initialize CSV writer
            csvWriter = new CSVWriter(sessionDirectory, "KeyCommands.csv", sessionID, headers.ToArray());

            // Reset all key states
            foreach (KeyCode key in keysToTrack)
            {
                previousKeyStates[key] = false;
                currentKeyStates[key] = false;
            }

            isRecording = true;
            // Debug.Log($"Started recording input data - tracking {trackedKeyCount} keys");
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
            // Debug.Log("Stopped recording input data");
        }

        /// <summary>
        /// Pause input recording
        /// </summary>
        public void PauseRecording()
        {
            if (!isRecording) return;
            pauseTime = Time.time;
            isRecording = false;
            // Debug.Log("Paused input recording");
        }

        /// <summary>
        /// Resume input recording
        /// </summary>
        public void ResumeRecording()
        {
            if (csvWriter != null && enableRecording)
            {
                recordingStartTime += Time.time - pauseTime;
                isRecording = true;
                // Debug.Log("Resumed input recording");
            }
        }

        /// <summary>
        /// Record current key states to CSV
        /// </summary>
        private void RecordKeyStates()
        {
            float currentTime = Time.time - recordingStartTime;

            // Update current key states
            bool anyKeyChanged = false;
            foreach (KeyCode key in keysToTrack)
            {
                currentKeyStates[key] = Input.GetKey(key);
                if (currentKeyStates[key] != previousKeyStates[key])
                {
                    anyKeyChanged = true;
                }
            }

            // Only write data when any key state actually changes
            if (anyKeyChanged || recordEveryFrame)
            {
                // Build row data
                List<object> rowData = new List<object> { currentTime };
                foreach (KeyCode key in keysToTrack)
                {
                    rowData.Add(currentKeyStates[key]);
                }

                csvWriter.WriteRow(rowData.ToArray());

                // Update previous states
                foreach (KeyCode key in keysToTrack)
                {
                    previousKeyStates[key] = currentKeyStates[key];
                }
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
        /// Add a key to the tracking list (runtime modification)
        /// </summary>
        public void AddKeyToTrack(KeyCode key)
        {
            if (!System.Array.Exists(keysToTrack, k => k == key))
            {
                System.Array.Resize(ref keysToTrack, keysToTrack.Length + 1);
                keysToTrack[keysToTrack.Length - 1] = key;

                previousKeyStates[key] = false;
                currentKeyStates[key] = false;
                trackedKeyCount = keysToTrack.Length;

                // Debug.Log($"Added {key} to input tracking");
            }
        }

        /// <summary>
        /// Get current state of a specific key
        /// </summary>
        public bool GetKeyState(KeyCode key)
        {
            return currentKeyStates.ContainsKey(key) ? currentKeyStates[key] : false;
        }
    }
}