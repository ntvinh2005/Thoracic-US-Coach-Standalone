using System.Collections.Generic;
using UnityEngine;
using System;
using System.IO;
using SMMARTS;

namespace SMMARTS
{
    /// <summary>
    /// Central coordinator for all recording operations in SMMARTS sessions.
    /// Manages session lifecycle, coordinates all recorders, and handles session organization.
    /// Singleton pattern ensures single recording session management.
    /// </summary>
    public class RecordingManager : MonoBehaviour
    {
        #region Singleton
        public static RecordingManager ME { get; private set; }

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

        [Tooltip("Name of the simulator for ITData folder. If empty, uses Application.productName")]
        public string SimulatorName = "";

        [Tooltip("Desktop folder name for recordings. If empty, defaults to 'SMMARTS_Recordings'")]
        public string DesktopFolderName = "";

        [Tooltip("Base directory for all recording sessions")]
        public string baseRecordingDirectory = "SMMARTS_Recordings";


        [Header("Session Status")]
        [field: SerializeField] public float RecordingTime_s { get; private set; } = 0f;
        public float StartTime => sessionStartTime; // seconds. Time elapsed since the application started.
        [SerializeField] private bool isRecording = false;
        [SerializeField] private bool isPaused = false;
        [field: SerializeField] public string CurrentSessionID { get; private set; } = "";
        [field: SerializeField] public string CurrentSessionDirectory { get; private set; } = "";
        [field: SerializeField] public string LastRecordedSessionID { get; private set; } = "";

        [Header("Debug Info")]
        [SerializeField] private int trackedObjectCount = 0;
        [SerializeField] private int videoRecorderCount = 0;
        [SerializeField] private float sessionStartTime = 0f; // seconds. 

        private List<TrackedObjectRecorder> trackedObjects = new List<TrackedObjectRecorder>();
        private KeyboardInputRecorder keyboardInputRecorder;
        private EventMarkerRecorder eventMarkerRecorder;
        private MicrocontrollerRecorder microcontrollerRecorder;
        private SystemRecorder systemRecorder;
        private KeywordRecognitionRecorder speechRecognitionRecorder;
        private List<VideoRecorder> videoRecorders = new List<VideoRecorder>();
        private VoiceAnnotationRecorder voiceAnnotationRecorder;

        void Start()
        {
            // Use SimulatorName if provided, otherwise fall back to product name
            string simulatorName = string.IsNullOrEmpty(SimulatorName) ? Application.productName : SimulatorName;

            // Use DesktopFolderName if provided, otherwise default to "SMMARTS_Recordings"
            string desktopFolder = string.IsNullOrEmpty(DesktopFolderName) ? "SMMARTS_Recordings" : DesktopFolderName;

            // Get desktop path and create folder structure: Desktop/[DesktopFolder]/[SimulatorName]/Session_Records/
            string desktopPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            string basePath = Path.Combine(desktopPath, desktopFolder, simulatorName);
            baseRecordingDirectory = Path.Combine(basePath, "Session_Records");

            // Try to create the directory structure
            try
            {
                if (!Directory.Exists(baseRecordingDirectory))
                {
                    Directory.CreateDirectory(baseRecordingDirectory);
                    Debug.Log($"Created recording directory: {baseRecordingDirectory}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Could not create desktop directory: {e.Message}. Using persistent data path instead.");
                baseRecordingDirectory = Path.Combine(Application.persistentDataPath, "SMMARTS_Recordings");
            }

            Debug.Log($"Recording directory: {baseRecordingDirectory}");
        }


        /// <summary>
        /// Get the base recording directory path for other managers to use
        /// </summary>
        public string GetBaseRecordingDirectory()
        {
            return baseRecordingDirectory;
        }

        /// <summary>
        /// Resolve the full directory path for a given session ID under the base recording directory.
        /// Single source of truth for session paths (used by recording, comments, and replay).
        /// </summary>
        public string GetSessionDirectory(string sessionID)
        {
            return Path.Combine(baseRecordingDirectory, sessionID);
        }

        /// <summary>
        /// Updates a timer for the recording time. Used by the UX. 
        /// </summary>
        private void Update()
        {
            if (isRecording && !isPaused)
            {
                RecordingTime_s += Time.deltaTime;
            }
        }


        /// <summary>
        /// Start a new recording session
        /// </summary>
        public void StartRecording()
        {
            if (isRecording)
            {
                Debug.LogWarning("Recording already in progress!");
                return;
            }

            // Generate session ID and directory
            CurrentSessionID = GenerateSessionID();
            CurrentSessionDirectory = GetSessionDirectory(CurrentSessionID);

            // Find all TrackedObjectRecorder components, video recorders, keyboard recorder, microcontroller recorder, etc. in the scene
            // a simulation doesnt need all of them, programmer adjustable.
            DiscoverRecordableObjects();

            if (trackedObjects.Count == 0)
            {
                Debug.LogWarning("No TrackedObjectRecorder components found in scene!");
                // return; keep running - its possible we may have a scene where we don't have one of these
            }

            // Record session start time
            sessionStartTime = Time.time;

            // Start recording on all tracked objects
            foreach (TrackedObjectRecorder recorder in trackedObjects)
            {
                recorder.StartRecording(CurrentSessionDirectory, CurrentSessionID, sessionStartTime);
            }

            // Start recording on all video recorder objects
            foreach (VideoRecorder recorder in videoRecorders)
            {
                recorder.StartRecording(CurrentSessionDirectory, CurrentSessionID, sessionStartTime);
            }

            // Start Keyboard input recording
            if (keyboardInputRecorder != null)
            {
                keyboardInputRecorder.StartRecording(CurrentSessionDirectory, CurrentSessionID, sessionStartTime);
            }

            // Start speech recognition recording
            if (speechRecognitionRecorder != null)
            {
                speechRecognitionRecorder.StartRecording(CurrentSessionDirectory, CurrentSessionID, sessionStartTime);
            }

            // Start voice annotation recording
            if (voiceAnnotationRecorder != null)
            {
                voiceAnnotationRecorder.StartRecording(CurrentSessionDirectory, CurrentSessionID, sessionStartTime);
            }

            // Start event recording
            if (eventMarkerRecorder != null)
            {
                eventMarkerRecorder.StartRecording(CurrentSessionDirectory, CurrentSessionID, sessionStartTime);
            }

            // Start microcontroller recording
            if (microcontrollerRecorder != null)
            {
                microcontrollerRecorder.StartRecording(CurrentSessionDirectory, CurrentSessionID, sessionStartTime);
            }

            // Record system information
            if (systemRecorder != null)
            {
                systemRecorder.StartRecording(CurrentSessionDirectory, CurrentSessionID, sessionStartTime);
            }

            RecordingTime_s = 0; 
            isRecording = true;
            isPaused = false;

            Debug.Log($"=== RECORDING SESSION STARTED ===");
            Debug.Log($"Session ID: {CurrentSessionID}");
            Debug.Log($"Directory: {CurrentSessionDirectory}");
            Debug.Log($"Tracking {trackedObjects.Count} objects");
        }

        /// <summary>
        /// Stop the current recording session
        /// </summary>
        public void StopRecording()
        {
            if (!isRecording)
            {
                Debug.LogWarning("No recording session active!");
                return;
            }

            // Stop recording on all tracked objects
            foreach (TrackedObjectRecorder recorder in trackedObjects)
            {
                recorder.StopRecording();
            }

            // Stop recording on all video objects
            foreach (VideoRecorder recorder in videoRecorders)
            {
                recorder.StopRecording();
            }

            // Stop keyboard input recording
            if (keyboardInputRecorder != null)
            {
                keyboardInputRecorder.StopRecording();
            }

            // Stop speech recognition input recording
            if (speechRecognitionRecorder != null)
            {
                speechRecognitionRecorder.StopRecording();
            }

            // Stop voice annotation recording
            if (voiceAnnotationRecorder != null)
            {
                voiceAnnotationRecorder.StopRecording();
            }

            // Stop event recording
            if (eventMarkerRecorder != null)
            {
                eventMarkerRecorder.StopRecording();
            }

            // Stop microcontroller recording
            if (microcontrollerRecorder != null)
            {
                microcontrollerRecorder.StopRecording();
            }

            RecordingTime_s = 0;
            isRecording = false;
            isPaused = false;

            // note this session for the replayer's default setting
            LastRecordedSessionID = CurrentSessionID;

            Debug.Log($"=== RECORDING SESSION STOPPED ===");
            Debug.Log($"Session saved to: {CurrentSessionDirectory}");

            // Clear session data
            CurrentSessionID = "";
            CurrentSessionDirectory = "";
            trackedObjects.Clear();
            videoRecorders.Clear();
        }

        /// <summary>
        /// Pause the current recording session
        /// </summary>
        public void PauseRecording()
        {
            if (!isRecording)
            {
                Debug.LogWarning("No recording session active!");
                return;
            }

            if (isPaused)
            {
                Debug.LogWarning("Recording already paused!");
                return;
            }

            // Pause all tracked objects
            foreach (TrackedObjectRecorder recorder in trackedObjects)
            {
                recorder.PauseRecording();
            }

            // Pause all video recording objects
            foreach (VideoRecorder recorder in videoRecorders)
            {
                // TODO: recorder.PauseRecording();
            }

            // Pause the keyboard input recorder
            if (keyboardInputRecorder != null)
            {
                keyboardInputRecorder.PauseRecording();
            }

            // Pause the speech recognition input recorder
            if (speechRecognitionRecorder != null)
            {
                speechRecognitionRecorder.PauseRecording();
            }

            // Pause the voice annotation input recorder
            //if (voiceAnnotationRecorder != null)
            //{
            //    voiceAnnotationRecorder.PauseRecording();
            //}

            if (eventMarkerRecorder != null)
            {
                eventMarkerRecorder.PauseRecording();
            }

            if (microcontrollerRecorder != null)
            {
                microcontrollerRecorder.PauseRecording();
            }

            isPaused = true;
            Debug.Log("Recording session PAUSED");
        }

        /// <summary>
        /// Resume a paused recording session
        /// </summary>
        public void ResumeRecording()
        {
            if (!isRecording)
            {
                Debug.LogWarning("No recording session active!");
                return;
            }

            if (!isPaused)
            {
                Debug.LogWarning("Recording not paused!");
                return;
            }

            // Resume all tracked objects
            foreach (TrackedObjectRecorder recorder in trackedObjects)
            {
                recorder.ResumeRecording();
            }

            // Resume all video recording objects
            foreach (VideoRecorder recorder in videoRecorders)
            {
                // TODO: recorder.ResumeRecording();
            }

            // Resume the keyboard input recorder
            if (keyboardInputRecorder != null)
            {
                keyboardInputRecorder.ResumeRecording();
            }

            // Resume the speech recognition recorder
            if (speechRecognitionRecorder != null)
            {
                speechRecognitionRecorder.ResumeRecording();
            }

            if (eventMarkerRecorder != null)
            {
                eventMarkerRecorder.ResumeRecording();
            }

            if (microcontrollerRecorder != null)
            {
                microcontrollerRecorder.ResumeRecording();
            }

            isPaused = false;
            Debug.Log("Recording session RESUMED");
        }

        /// <summary>
        /// Force flush all buffered data to disk
        /// </summary>
        public void SaveRecord()
        {
            if (!isRecording)
            {
                Debug.LogWarning("No recording session active!");
                return;
            }

            foreach (TrackedObjectRecorder recorder in trackedObjects)
            {
                recorder.FlushData();
            }

            foreach (VideoRecorder recorder in videoRecorders)
            {
                recorder.FlushData();
            }

            if (keyboardInputRecorder != null)
            {
                keyboardInputRecorder.FlushData();
            }

            if (speechRecognitionRecorder != null)
            {
                speechRecognitionRecorder.FlushData();
            }

            if (voiceAnnotationRecorder != null)
            {
                voiceAnnotationRecorder.FlushData();
            }

            if (eventMarkerRecorder != null)
            {
                eventMarkerRecorder.FlushData();
            }

            if (microcontrollerRecorder != null)
            {
                microcontrollerRecorder.FlushData();
            }

            Debug.Log("Forced save of all recording data");
        }

        /// <summary>
        /// Find all TrackedObjectRecorder components in the current scene
        /// </summary>
        private void DiscoverRecordableObjects()
        {

            // Find all the tracked objects that have a recorder component attached - there are typically 2 to 4 of them
            trackedObjects.Clear();
            TrackedObjectRecorder[] foundTrackedObjectRecorders = FindObjectsByType<TrackedObjectRecorder>(FindObjectsSortMode.None);

            foreach (TrackedObjectRecorder recorder in foundTrackedObjectRecorders)
            {
                if (recorder.EnableRecording)
                {
                    trackedObjects.Add(recorder);
                    Debug.Log($"Found tracked object: {recorder.gameObject.name}");
                }
            }

            // Find KeyboardInputRecorder
            keyboardInputRecorder = KeyboardInputRecorder.ME;
            if (keyboardInputRecorder == null)
            {
                keyboardInputRecorder = FindFirstObjectByType<KeyboardInputRecorder>();
            }

            if (keyboardInputRecorder != null && keyboardInputRecorder.enableRecording)
            {
                Debug.Log("Found KeyboardInputRecorder for key tracking");
            }

            // Find SpeechRecognitionRecorder
            speechRecognitionRecorder  = KeywordRecognitionRecorder.ME;
            if (speechRecognitionRecorder == null)
            {
                speechRecognitionRecorder = FindFirstObjectByType<KeywordRecognitionRecorder>();
            }

            if (speechRecognitionRecorder != null && speechRecognitionRecorder.enableRecording)
            {
                Debug.Log("Found speechRecognitionRecorder for speech recognition recording");
            }

            // Find Voice Annotation Recorder
            voiceAnnotationRecorder = VoiceAnnotationRecorder.ME;
            if (voiceAnnotationRecorder == null)
            {
                voiceAnnotationRecorder = FindFirstObjectByType<VoiceAnnotationRecorder>();
            }

            if (voiceAnnotationRecorder != null && voiceAnnotationRecorder.enableRecording)
            {
                Debug.Log("Found speechRecognitionRecorder for speech recognition recording");
            }

            // Find EventMarkerRecorder
            eventMarkerRecorder = EventMarkerRecorder.ME;
            if (eventMarkerRecorder == null)
            {
                eventMarkerRecorder = FindFirstObjectByType<EventMarkerRecorder>();
            }

            if (eventMarkerRecorder != null && eventMarkerRecorder.enableRecording)
            {
                Debug.Log("Found EventMarkerRecorder for event tracking");
            }

            // Find MicrocontrollerRecorder
            microcontrollerRecorder = MicrocontrollerRecorder.ME;
            if (microcontrollerRecorder == null)
            {
                microcontrollerRecorder = FindFirstObjectByType<MicrocontrollerRecorder>();
            }

            if (microcontrollerRecorder != null && microcontrollerRecorder.enableRecording)
            {
                Debug.Log("Found MicrocontrollerRecorder for hardware data tracking");
            }

            // Find the system recorder - saves system information
            systemRecorder = SystemRecorder.ME;
            if (systemRecorder == null)
            {
                systemRecorder = FindFirstObjectByType<SystemRecorder>();
            }

            if (systemRecorder != null && systemRecorder.enableRecording)
            {
                Debug.Log("Found SystemRecorder for initial position tracking");
            }

            // Find all the video recorders - we may have multiple webcams or cameras depending on what we need 
            videoRecorders.Clear();
            VideoRecorder[] foundVideoRecorders = FindObjectsByType<VideoRecorder>(FindObjectsSortMode.None);

            foreach (VideoRecorder recorder in foundVideoRecorders)
            {
                if (recorder.EnableRecording)
                {
                    videoRecorders.Add(recorder);
                    Debug.Log($"Found video recorder object: {recorder.gameObject.name}");
                }
            }


            videoRecorderCount = videoRecorders.Count;
        }


        /// <summary>
        /// Generate a unique session ID with timestamp
        /// </summary>
        private string GenerateSessionID()
        {
            DateTime now = DateTime.Now;
            return $"SessionID_{now:yyyy-MM-dd_HH-mm-ss}";
        }

        /// <summary>
        /// Get current session information
        /// </summary>
        public string GetCurrentSessionID() => CurrentSessionID;
        public bool IsRecording => isRecording;
        public bool IsPaused => isPaused;
        public int GetTrackedObjectCount() => trackedObjects.Count;
        public int GetVideoRecorderCount() => videoRecorders.Count;
        /// <summary>
        /// Inspector buttons for testing
        /// </summary>
        [ContextMenu("Start Recording")]
        public void StartRecordingButton() => StartRecording();

        [ContextMenu("Stop Recording")]
        public void StopRecordingButton() => StopRecording();

        [ContextMenu("Pause Recording")]
        public void PauseRecordingButton() => PauseRecording();

        [ContextMenu("Resume Recording")]
        public void ResumeRecordingButton() => ResumeRecording();

        [ContextMenu("Save Record")]
        public void SaveRecordButton() => SaveRecord();


    }
}
