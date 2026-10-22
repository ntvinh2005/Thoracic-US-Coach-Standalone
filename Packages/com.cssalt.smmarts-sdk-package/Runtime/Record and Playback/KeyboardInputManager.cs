using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Manages keyboard input with replay capability for SMMARTS simulations.
    /// Provides Input.GetKey() style interface that seamlessly switches between live input and replay data.
    /// Singleton pattern ensures single keyboard input management instance.
    /// </summary>
    public class KeyboardInputManager : MonoBehaviour
    {
        #region Singleton
        public static KeyboardInputManager ME { get; private set; }

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

        [Header("Input Settings")]
        [Tooltip("Enable keyboard input processing")]
        public bool enableInput = true;

        [Header("Debug Info")]
        [SerializeField] private bool useReplayData = false;
        [SerializeField] private bool isReplayActive = false;

        void Start()
        {
            // Check if replayer is available at startup
            useReplayData = (KeyboardInputReplayer.ME != null);
            // Debug.Log($"KeyboardInputManager initialized - Replay available: {useReplayData}");
        }

        void Update()
        {
            // Update replay status for debug display
            if (useReplayData && KeyboardInputReplayer.ME != null)
            {
                isReplayActive = KeyboardInputReplayer.ME.IsReplaying;
            }
        }

        /// <summary>
        /// Check if a key is currently being held down
        /// Replaces Input.GetKey() in simulation code
        /// </summary>
        /// <param name="key">Key to check</param>
        /// <returns>True if key is currently pressed</returns>
        public bool GetKey(KeyCode key)
        {
            if (!enableInput) return false;

            // Check replayer first if available and active
            if (useReplayData && KeyboardInputReplayer.ME != null && KeyboardInputReplayer.ME.IsReplaying)
            {
                return KeyboardInputReplayer.ME.GetKeyState(key);
            }

            // Fall back to live input
            return Input.GetKey(key);
        }

        /// <summary>
        /// Check if a key was pressed this frame
        /// Replaces Input.GetKeyDown() in simulation code
        /// </summary>
        /// <param name="key">Key to check</param>
        /// <returns>True if key was pressed this frame</returns>
        public bool GetKeyDown(KeyCode key)
        {
            if (!enableInput) return false;

            // Check replayer first if available and active
            if (useReplayData && KeyboardInputReplayer.ME != null && KeyboardInputReplayer.ME.IsReplaying)
            {
                return KeyboardInputReplayer.ME.GetKeyDown(key);
            }

            // Fall back to live input
            return Input.GetKeyDown(key);
        }

        /// <summary>
        /// Check if a key was released this frame
        /// Replaces Input.GetKeyUp() in simulation code
        /// </summary>
        /// <param name="key">Key to check</param>
        /// <returns>True if key was released this frame</returns>
        public bool GetKeyUp(KeyCode key)
        {
            if (!enableInput) return false;

            // Check replayer first if available and active
            if (useReplayData && KeyboardInputReplayer.ME != null && KeyboardInputReplayer.ME.IsReplaying)
            {
                return KeyboardInputReplayer.ME.GetKeyUp(key);
            }

            // Fall back to live input
            return Input.GetKeyUp(key);
        }

        /// <summary>
        /// Check if any key is currently pressed
        /// Replaces Input.anyKey in simulation code
        /// </summary>
        ///
        /*
        public bool AnyKey
        {
            get
            {
                if (!enableInput) return false;

                // Check replayer first if available and active
                if (useReplayData && KeyboardInputReplayer.ME != null && KeyboardInputReplayer.ME.IsReplaying)
                {
                    return KeyboardInputReplayer.ME.AnyKey;
                }

                // Fall back to live input
                return Input.anyKey;
            }
        }
        */


        /// <summary>
        /// Check if replayer is currently providing input data
        /// </summary>
        public bool IsUsingReplayData => useReplayData && KeyboardInputReplayer.ME != null && KeyboardInputReplayer.ME.IsReplaying;

        /// <summary>
        /// Refresh replayer availability (call if replayer components are added/removed at runtime)
        /// </summary>
        public void RefreshReplayerAvailability()
        {
            useReplayData = (KeyboardInputReplayer.ME != null);
            Debug.Log($"Replayer availability refreshed: {useReplayData}");
        }

        /// <summary>
        /// Get current input source for debugging
        /// </summary>
        public string GetCurrentInputSource()
        {
            if (IsUsingReplayData)
                return "Replay Data";
            else
                return "Live Keyboard";
        }

        /// <summary>
        /// Context menu helper for testing
        /// </summary>
        [ContextMenu("Show Input Status")]
        public void ShowInputStatus()
        {
            Debug.Log($"KeyboardInputManager Status:");
            Debug.Log($"- Input Enabled: {enableInput}");
            Debug.Log($"- Replay Available: {useReplayData}");
            Debug.Log($"- Currently Using: {GetCurrentInputSource()}");
            Debug.Log($"- Space Key State: {GetKey(KeyCode.Space)}");
        }
    }
}