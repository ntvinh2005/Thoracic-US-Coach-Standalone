using System.Collections.Generic;
using UnityEngine;
using System.IO;
using SMMARTS;

namespace SMMARTS
{
    /// <summary>
    /// Central coordinator for replay operations in SMMARTS sessions.
    /// Manages session loading, coordinates all replayers, and handles playback control.
    /// Singleton pattern ensures single replay session management.
    /// </summary>
    public class ReplayerManager : MonoBehaviour
    {
        #region Singleton
        public static ReplayerManager ME { get; private set; }

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
        [Tooltip("Base directory where recording sessions are stored")]
        public string baseRecordingDirectory = "";
        [Tooltip("Loop replay when reaching the end")]
        public bool Loop = true;

        [Header("Current Session")]
        [field: SerializeField] public string CurrentSessionID { get; private set; } = "";
        [field: SerializeField] public string CurrentSessionDirectory { get; private set; } = "";

        [Header("Replay Status")]
        [field: SerializeField] public float ReplayTime_s { get; private set; } = 0f;
        [SerializeField] private bool isReplaying = false;
        [SerializeField] private bool isPaused = false;
        [SerializeField] private float replayStartTime = 0f;
        [SerializeField] private float pauseTime = 0f;
        [SerializeField] private float sessionDuration = 0f;

        [Header("Debug Info")]
        [SerializeField] private int trackedObjectCount = 0;
        [SerializeField] private List<string> availableSessions = new List<string>();

        private List<TrackedObjectReplayer> trackedObjectReplayers = new List<TrackedObjectReplayer>();
        private List<VideoReplayer> videoReplayers = new List<VideoReplayer>();
        private MicrocontrollerReplayer microcontrollerReplayer;
        private KeyboardInputReplayer keyboardInputReplayer;
        private KeywordRecognitionReplayer speechRecognitionReplayer;
        private VoiceAnnotationReplayer voiceAnnotationReplayer;

        void Start()
        {

            // Don't get the path immediately - RecordingManager might not be ready yet

            RefreshAvailableSessions();

            // Auto-load last recorded session if available
            if (RecordingManager.ME != null && !string.IsNullOrEmpty(RecordingManager.ME.LastRecordedSessionID))
            {
                string lastSession = RecordingManager.ME.LastRecordedSessionID;
                Debug.Log($"Auto-loading last recorded session: {lastSession}");
                LoadSession(lastSession);
            }
        }

        /// <summary>
        /// handles what happens at the end of the replay, depending on if it loops or not.
        /// </summary>
        void Update()
        {
            if (isReplaying)
            {
                // Keep the serialized replay time in sync so UI (and a future scrubber) can read it.
                ReplayTime_s = GetCurrentReplayTime();
            }

            if (isReplaying && !isPaused)
            {
                float currentReplayTime = Time.time - replayStartTime;

                // Only auto-stop when there is a valid duration - guards against an
                // instant stop/restart loop that would freeze playback at 0.
                if (sessionDuration > 0f && currentReplayTime >= sessionDuration)
                {
                    StopReplay();
                    if (Loop) StartReplay();
                }
            }
        }


        /// <summary>
        /// Load and start replay of a specific session, or default to last recorded session
        /// </summary>
        /// <param name="sessionID">Session to replay, or null for last recorded session</param>
        /// <returns>True if replay started successfully</returns>
        public bool ReplaySession(string sessionID = null)
        {
            // Use last recorded session if no specific session provided
            if (string.IsNullOrEmpty(sessionID))
            {
                if (RecordingManager.ME != null && !string.IsNullOrEmpty(RecordingManager.ME.LastRecordedSessionID))
                {
                    sessionID = RecordingManager.ME.LastRecordedSessionID;
                    Debug.Log($"No session specified - using last recorded: {sessionID}");
                }
                else
                {
                    Debug.Log("Can't replay - no session specified and no last recorded session available");
                    return false;
                }
            }

            // Stop current replay if active
            if (isReplaying)
            {
                StopReplay();
            }

            // Load the session
            if (!LoadSession(sessionID))
            {
                Debug.LogError($"Failed to load session: {sessionID}");
                return false;
            }

            // Start replay immediately
            StartReplay();
            return true;
        }

        /// <summary>
        /// Scan for available recording sessions
        /// </summary>
        public void RefreshAvailableSessions()
        {
            availableSessions.Clear();

            // Get recording directory from RecordingManager
            if (RecordingManager.ME != null)
            {
                baseRecordingDirectory = RecordingManager.ME.GetBaseRecordingDirectory();
                // Debug.Log($"ReplayerManager using recording directory: {baseRecordingDirectory}");
            }
            else
            {
                Debug.LogError("RecordingManager not found - cannot determine recording directory!");
                // Fallback to default if needed
                baseRecordingDirectory = Path.Combine(Application.persistentDataPath, "Session_Records");
            }

            string fullBasePath = baseRecordingDirectory;

            if (!Directory.Exists(fullBasePath))
            {
                // Debug.LogWarning($"Recording directory not found: {fullBasePath}");
                return;
            }

            DirectoryInfo baseDir = new DirectoryInfo(fullBasePath);
            DirectoryInfo[] sessionDirectories = baseDir.GetDirectories();

            Debug.Log($"Found {sessionDirectories.Length} total directories:");
            foreach (DirectoryInfo dir in sessionDirectories)
            {
                Debug.Log($"  - Directory: '{dir.Name}' | StartsWith SessionID_: {dir.Name.StartsWith("SessionID_")}");
                if (dir.Name.StartsWith("SessionID_"))
                {
                    availableSessions.Add(dir.Name);
                }
            }
            Debug.Log($"Final availableSessions count: {availableSessions.Count}");
        }


        /// <summary>
        /// Resolve the full directory path for a given session ID under the base recording directory.
        /// </summary>
        public string GetSessionDirectory(string sessionID)
        {
            return Path.Combine(baseRecordingDirectory, sessionID);
        }

        /// <summary>
        /// Load a specific recording session for replay
        /// </summary>
        /// <param name="sessionID">Session ID to load</param>
        /// <returns>True if loaded successfully</returns>
        public bool LoadSession(string sessionID)
        {
            if (isReplaying)
            {
                Debug.LogWarning("Cannot load session while replay is active - stop current replay first");
                return false;
            }

            CurrentSessionDirectory = GetSessionDirectory(sessionID);

            if (!Directory.Exists(CurrentSessionDirectory))
            {
                Debug.LogError($"Session directory not found: {CurrentSessionDirectory}");
                return false;
            }

            CurrentSessionID = sessionID;

            // Find all TrackedObjectReplayer components in the scene
            DiscoverAllReplayers();

            // there are cases where we can record/playback without tracked objects - using just speech, for example. 
            // if (trackedObjectReplayers.Count == 0)
            // {
                // Debug.LogWarning("No TrackedObjectReplayer components found in scene!");
                // return false;
            // }

            // Load CSV data for each replayer
            bool loadSuccess = LoadAllReplayData();

            if (loadSuccess)
            {
                CalculateSessionDuration();
                Debug.Log($"=== SESSION LOADED ===");
                Debug.Log($"Session ID: {CurrentSessionID}");
                Debug.Log($"Directory: {CurrentSessionDirectory}");
                Debug.Log($"Replayers: {trackedObjectReplayers.Count}");
                Debug.Log($"Duration: {sessionDuration:F2} seconds");
            }

            return loadSuccess;
        }

        /// <summary>
        /// Find all TrackedObjectReplayer components in the current scene
        /// </summary>
        private void DiscoverAllReplayers()
        {
            // Find all the tracked object replayers
            trackedObjectReplayers.Clear();
            TrackedObjectReplayer[] foundReplayers = FindObjectsByType<TrackedObjectReplayer>(FindObjectsSortMode.None);

            foreach (TrackedObjectReplayer replayer in foundReplayers)
            {
                if (replayer.EnableReplay)
                {
                    trackedObjectReplayers.Add(replayer);
                    // Debug.Log($"Found tracked object replayer: {replayer.targetObject?.name ?? replayer.gameObject.name}");
                }
            }
            trackedObjectCount = trackedObjectReplayers.Count;

            // Find all the video replayers
            videoReplayers.Clear();
            VideoReplayer[] foundVideoReplayers = FindObjectsByType<VideoReplayer>(FindObjectsSortMode.None);
            foreach (VideoReplayer replayer in foundVideoReplayers)
            {
                if (replayer.enableReplay)
                {
                    videoReplayers.Add(replayer);
                    Debug.Log($"Found video replayer: {replayer.gameObject.name}");
                }
            }

            // Find Microcontroller Replayer
            microcontrollerReplayer = MicrocontrollerReplayer.ME;
            if (microcontrollerReplayer == null)
            {
                microcontrollerReplayer = FindFirstObjectByType<MicrocontrollerReplayer>();
            }

            if (microcontrollerReplayer != null && microcontrollerReplayer.enableReplay)
            {
                // Debug.Log("Found MicrocontrollerReplayer for sensor data replay");
            }
            
            // Find Keyboard Replayer
            keyboardInputReplayer = KeyboardInputReplayer.ME;
            if (keyboardInputReplayer == null)
            {
                keyboardInputReplayer = FindFirstObjectByType<KeyboardInputReplayer>();
            }

            if (keyboardInputReplayer != null && keyboardInputReplayer.enableReplay)
            {
                // Debug.Log("Found KeyboardInputReplayer for sensor data replay");
            }

            // Find Speech Recognition Replayer
            speechRecognitionReplayer = KeywordRecognitionReplayer.ME;
            if (speechRecognitionReplayer == null)
            {
                speechRecognitionReplayer = FindFirstObjectByType<KeywordRecognitionReplayer>();
            }

            if (speechRecognitionReplayer != null && speechRecognitionReplayer.enableReplay)
            {
                // Debug.Log("Found Speech Recognition Replayer for speech rec data replay");
            }

            // Find Voice Annotation Replayer
            voiceAnnotationReplayer = VoiceAnnotationReplayer.ME;
            if (voiceAnnotationReplayer == null)
            {
                voiceAnnotationReplayer = FindFirstObjectByType<VoiceAnnotationReplayer>();
            }

            if (voiceAnnotationReplayer != null && voiceAnnotationReplayer.enableReplay)
            {
                // Debug.Log("Found Speech Recognition Replayer for speech rec data replay");
            }

        }

        /// <summary>
        /// Load replay data for all discovered replayers
        /// </summary>
        private bool LoadAllReplayData()
        {
            // A session may legitimately contain only some streams (e.g. speech only).
            // We count how many streams actually loaded and consider the load successful
            // if at least one did; a session with no loadable data is a real failure.
            int loadedCount = 0;

            foreach (TrackedObjectReplayer replayer in trackedObjectReplayers)
            {
                if (replayer.LoadReplayData(CurrentSessionDirectory)) loadedCount++;
            }

            // Load video replayer data (optional)
            foreach (VideoReplayer replayer in videoReplayers)
            {
                if (replayer.LoadReplayData(CurrentSessionDirectory)) loadedCount++;
            }

            // Load microcontroller data (optional)
            if (microcontrollerReplayer != null && microcontrollerReplayer.LoadReplayData(CurrentSessionDirectory)) loadedCount++;

            // Load keyboard data (optional)
            if (keyboardInputReplayer != null && keyboardInputReplayer.LoadReplayData(CurrentSessionDirectory)) loadedCount++;

            // Load speech rec data (optional)
            if (speechRecognitionReplayer != null && speechRecognitionReplayer.LoadReplayData(CurrentSessionDirectory)) loadedCount++;

            // Load voice annotation data (optional)
            if (voiceAnnotationReplayer != null && voiceAnnotationReplayer.LoadReplayData(CurrentSessionDirectory)) loadedCount++;

            if (loadedCount == 0)
            {
                Debug.LogWarning($"No replay data could be loaded for session {CurrentSessionID} - nothing to replay");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Calculate total session duration from all replayers
        /// </summary>
        private void CalculateSessionDuration()
        {
            sessionDuration = 0f;

            foreach (TrackedObjectReplayer replayer in trackedObjectReplayers)
                sessionDuration = Mathf.Max(sessionDuration, replayer.GetReplayDuration());

            foreach (VideoReplayer replayer in videoReplayers)
                sessionDuration = Mathf.Max(sessionDuration, replayer.GetReplayDuration());

            if (microcontrollerReplayer != null)
                sessionDuration = Mathf.Max(sessionDuration, microcontrollerReplayer.GetReplayDuration());

            if (keyboardInputReplayer != null)
                sessionDuration = Mathf.Max(sessionDuration, keyboardInputReplayer.GetReplayDuration());

            if (speechRecognitionReplayer != null)
                sessionDuration = Mathf.Max(sessionDuration, speechRecognitionReplayer.GetReplayDuration());

            if (voiceAnnotationReplayer != null)
                sessionDuration = Mathf.Max(sessionDuration, voiceAnnotationReplayer.GetReplayDuration());
        }

        /// <summary>
        /// Start replay of the loaded session
        /// </summary>
        public void StartReplay()
        {
            if (string.IsNullOrEmpty(CurrentSessionID))
            {
                Debug.LogWarning("No session loaded - load a session first");
                return;
            }

            if (isReplaying)
            {
                Debug.LogWarning("Replay already in progress!");
                return;
            }

            if (trackedObjectReplayers.Count == 0)
            {
                Debug.LogWarning("No tracked object replayers available");
                // return; // tracked object replayers are not specifically necessary, for example, if you are using only speech recognition.
            }

            // Resume from the current scrubbed/last position (0 for a fresh session).
            float startAt = Mathf.Clamp(ReplayTime_s, 0f, sessionDuration);
            // If we're at/past the end (or have no valid duration yet), restart from the beginning.
            if (sessionDuration <= 0f || startAt >= sessionDuration - 0.0001f)
                startAt = 0f;
            replayStartTime = Time.time - startAt;

            // Start replay on all tracked object replayers
            foreach (TrackedObjectReplayer replayer in trackedObjectReplayers)
            {
                replayer.StartReplay(replayStartTime);
            }

            // Start video replayers
            foreach (VideoReplayer replayer in videoReplayers)
            {
                replayer.StartReplay(replayStartTime);
            }

            // Start microcontroller replay
            if (microcontrollerReplayer != null)
            {
                microcontrollerReplayer.StartReplay(replayStartTime);
            }

            // Start keyboard input replay
            if (keyboardInputReplayer != null)
            {
                keyboardInputReplayer.StartReplay(replayStartTime);
            }

            // Start speech rec input replay
            if (speechRecognitionReplayer != null)
            {
                speechRecognitionReplayer.StartReplay(replayStartTime);
            }

            // Start voice annotation replay
            if (voiceAnnotationReplayer != null)
            {
                voiceAnnotationReplayer.StartReplay(replayStartTime);
            }

            isReplaying = true;
            isPaused = false;

            // Align all streams to the resume position (and preview the first frame on a fresh start).
            SeekToTime(startAt);

            Debug.Log($"=== REPLAY STARTED ===");
            Debug.Log($"Session: {CurrentSessionID}");
            Debug.Log($"Replaying {trackedObjectReplayers.Count} objects");
        }

        /// <summary>
        /// Stop the current replay session
        /// </summary>
        public void StopReplay()
        {
            if (!isReplaying)
            {
                Debug.LogWarning("No replay session active!");
                return;
            }

            // Stop replay on all tracked object replayers
            foreach (TrackedObjectReplayer replayer in trackedObjectReplayers)
            {
                replayer.StopReplay();
            }

            // Stop video replayers
            foreach (VideoReplayer replayer in videoReplayers)
            {
                replayer.StopReplay();
            }

            // Stop microcontroller replay
            if (microcontrollerReplayer != null)
            {
                microcontrollerReplayer.StopReplay();
            }

            // Stop keyboard replay
            if (keyboardInputReplayer != null)
            {
                keyboardInputReplayer.StopReplay();
            }

            // Stop speech rec replay
            if (speechRecognitionReplayer != null)
            {
                speechRecognitionReplayer.StopReplay();
            }

            // Stop voice annotation replay
            if (voiceAnnotationReplayer != null)
            {
                voiceAnnotationReplayer.StopReplay();
            }

            isReplaying = false;
            isPaused = false;
            ReplayTime_s = 0f;

            Debug.Log($"=== REPLAY STOPPED ===");
        }

        /// <summary>
        /// Pause the current replay session
        /// </summary>
        public void PauseReplay()
        {
            if (!isReplaying)
            {
                Debug.LogWarning("No replay session active!");
                return;
            }

            if (isPaused)
            {
                Debug.LogWarning("Replay already paused!");
                return;
            }

            // Pause all replayers
            foreach (TrackedObjectReplayer replayer in trackedObjectReplayers)
            {
                replayer.PauseReplay();
            }

            // Pause video replayers
            foreach (VideoReplayer replayer in videoReplayers)
            {
                replayer.PauseReplay();
            }

            // Pause the microcontroller replayer
            if (microcontrollerReplayer != null)
            {
                microcontrollerReplayer.PauseReplay();
            }

            // Pause the keyboard replayer
            if (keyboardInputReplayer != null)
            {
                keyboardInputReplayer.PauseReplay();
            }

            // Pause the speech rec replayer
            if (speechRecognitionReplayer != null)
            {
                speechRecognitionReplayer.PauseReplay();
            }

            // Pause the voice annotation replayer
            if (voiceAnnotationReplayer != null)
            {
                voiceAnnotationReplayer.PauseReplay();
            }

            isPaused = true;
            pauseTime = Time.time;
            Debug.Log("Replay session PAUSED");
        }

        /// <summary>
        /// Resume a paused replay session
        /// </summary>
        public void ResumeReplay()
        {
            if (!isReplaying)
            {
                Debug.LogWarning("No replay session active!");
                return;
            }

            if (!isPaused)
            {
                Debug.LogWarning("Replay not paused!");
                return;
            }

            // Shift the master start time forward by the paused duration so the timeline
            // continues from where it was paused (each replayer does the same internally).
            replayStartTime += Time.time - pauseTime;

            // Resume all replayers
            foreach (TrackedObjectReplayer replayer in trackedObjectReplayers)
            {
                replayer.ResumeReplay();
            }

            // Resume video replayers
            foreach (VideoReplayer replayer in videoReplayers)
            {
                replayer.ResumeReplay();
            }

            // resume the microcontroller replayer
            if (microcontrollerReplayer != null)
            {
                microcontrollerReplayer.ResumeReplay();
            }

            // resume the keyboard replayer
            if (keyboardInputReplayer != null)
            {
                keyboardInputReplayer.ResumeReplay();
            }

            // resume the speech rec replayer
            if (speechRecognitionReplayer != null)
            {
                speechRecognitionReplayer.ResumeReplay();
            }

            // resume the voice annotation replayer
            if (voiceAnnotationReplayer != null)
            {
                voiceAnnotationReplayer.ResumeReplay();
            }

            isPaused = false;
            Debug.Log("Replay session RESUMED");
        }

        /// <summary>
        /// Seek all replayers to an absolute time (seconds) within the loaded session.
        /// Works while playing (live seek), paused, or stopped.
        /// </summary>
        public void SeekToTime(float seconds)
        {
            if (string.IsNullOrEmpty(CurrentSessionID))
            {
                Debug.LogWarning("Cannot seek - no session loaded");
                return;
            }

            float t = Mathf.Clamp(seconds, 0f, sessionDuration);
            replayStartTime = Time.time - t;
            if (isPaused) pauseTime = Time.time; // keep the frozen-clock reading at t
            ReplayTime_s = t;

            foreach (TrackedObjectReplayer replayer in trackedObjectReplayers)
                replayer.SeekToTime(t);

            foreach (VideoReplayer replayer in videoReplayers)
                replayer.SeekToTime(t);

            if (microcontrollerReplayer != null) microcontrollerReplayer.SeekToTime(t);
            if (keyboardInputReplayer != null) keyboardInputReplayer.SeekToTime(t);
            if (speechRecognitionReplayer != null) speechRecognitionReplayer.SeekToTime(t);
            if (voiceAnnotationReplayer != null) voiceAnnotationReplayer.SeekToTime(t);
        }

        /// <summary>
        /// Seek using a normalized 0..1 position (for a UI slider).
        /// </summary>
        public void SeekToNormalized(float t01)
        {
            SeekToTime(Mathf.Clamp01(t01) * sessionDuration);
        }

        /// <summary>
        /// Get current replay progress (0.0 to 1.0)
        /// </summary>
        public float GetReplayProgress()
        {
            if (!isReplaying || sessionDuration <= 0f) return 0f;

            return Mathf.Clamp01(GetCurrentReplayTime() / sessionDuration);
        }

        /// <summary>
        /// Get current replay time in seconds
        /// </summary>
        public float GetCurrentReplayTime()
        {
            if (!isReplaying) return 0f;
            // While paused the clock is frozen at the moment of pause.
            if (isPaused) return pauseTime - replayStartTime;
            return Time.time - replayStartTime;
        }

        /// <summary>
        /// Get session information
        /// </summary>
        public string GetCurrentSessionID() => CurrentSessionID;
        public bool IsReplaying => isReplaying;
        public bool IsPaused => isPaused;
        public float GetSessionDuration() => sessionDuration;
        public int GetTrackedObjectCount() => trackedObjectReplayers.Count;
        public List<string> GetAvailableSessions()
        {
            RefreshAvailableSessions();
            return new List<string>(availableSessions);
        }

        /// <summary>
        /// Inspector buttons for testing
        /// </summary>
        [ContextMenu("Refresh Available Sessions")]
        public void RefreshSessionsButton() => RefreshAvailableSessions();

        [ContextMenu("Replay Session")]
        public void ReplaySessionButton() => ReplaySession();

       // [ContextMenu("Start Replay")]
       // public void StartReplayButton() => StartReplay();

        [ContextMenu("Stop Replay")]
        public void StopReplayButton() => StopReplay();

        [ContextMenu("Pause Replay")]
        public void PauseReplayButton() => PauseReplay();

        [ContextMenu("Resume Replay")]
        public void ResumeReplayButton() => ResumeReplay();
    }
}

