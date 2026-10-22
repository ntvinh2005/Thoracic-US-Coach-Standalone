using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Records system and simulator configuration data at recording start.
    /// Captures computer info, ATC settings, version stamps, and Unity project details.
    /// Records only once at the beginning of each recording session.
    /// Singleton pattern ensures single system recording instance.
    /// TODO:   if ATC has not established connection when recording begins, zeros will be recorded for the tracking system. 
    ///         in that case, write the file in the first frame, poll ATC for Connection_Established, re-write, and close.
    ///         this handles the edge case if recording begins before ATC has a chance to connect.
    /// </summary>
    public class SystemRecorder : MonoBehaviour
    {
        #region Singleton
        public static SystemRecorder ME { get; private set; }

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

        [Header("System Recording Settings")]
        [Tooltip("Enable to record system configuration data")]
        public bool enableRecording = true;

        [Header("Debug Info")]
        [SerializeField] private bool hasRecordedThisSession = false;

        private CSVWriter csvWriter;

        /// <summary>
        /// Start recording system configuration
        /// Called by RecordingManager when recording begins
        /// </summary>
        /// <param name="sessionDirectory">Directory path for this recording session</param>
        /// <param name="sessionID">Unique session identifier</param>
        /// <param name="startTime">Time when recording started (ignored - always use 0)</param>
        public void StartRecording(string sessionDirectory, string sessionID, float startTime)
        {
            if (!enableRecording)
            {
                Debug.Log("System recording disabled");
                return;
            }

            // Define column headers for name-value pairs
            string[] headers = { "Time", "Property", "Value" };

            // Initialize CSV writer
            csvWriter = new CSVWriter(sessionDirectory, "SystemInfo.csv", sessionID, headers);

            // Record all system information
            RecordSystemInfo();

            // Close CSV writer immediately (only one-time recording needed)
            csvWriter.Close();
            csvWriter = null;

            hasRecordedThisSession = true;
            Debug.Log("System configuration recorded successfully");
        }

        /// <summary>
        /// Record all system and simulator configuration data
        /// </summary>
        private void RecordSystemInfo()
        {
            if (csvWriter == null) return;

            // Record version stamp if available
            RecordVersionInfo();

            // Record ATC configuration if available
            RecordATCInfo();

            // Record computer/system information
            RecordProperty("ComputerName", System.Environment.MachineName);
            RecordProperty("UserName", System.Environment.UserName);
            RecordProperty("OSVersion", System.Environment.OSVersion.ToString());
            RecordProperty("ProcessorCount", System.Environment.ProcessorCount.ToString());
            RecordProperty("UnityPlatform", Application.platform.ToString());
            RecordProperty("UnityVersion", Application.unityVersion);
            RecordProperty("ProjectName", Application.productName);

        }

        /// <summary>
        /// Record ATC tracking configuration
        /// </summary>
        private void RecordATCInfo()
        {
            if (ATC.ME == null)
            {
                RecordProperty("ATC_Status", "Not Available");
                return;
            }

            // Record base object and tracking settings
            RecordProperty("BaseObject", GetStringValue(ATC.ME.BaseObject));
            RecordProperty("EnableTrackingBaseObject", ATC.ME.EnableTrackingBaseObject.ToString());

            // Record tracked objects 1-4
            RecordProperty("TrackedObject1", GetStringValue(ATC.ME.TrackedObject1));
            RecordProperty("EnableTrackingObject1", ATC.ME.EnableTrackingObject1.ToString());

            RecordProperty("TrackedObject2", GetStringValue(ATC.ME.TrackedObject2));
            RecordProperty("EnableTrackingObject2", ATC.ME.EnableTrackingObject2.ToString());

            RecordProperty("TrackedObject3", GetStringValue(ATC.ME.TrackedObject3));
            RecordProperty("EnableTrackingObject3", ATC.ME.EnableTrackingObject3.ToString());

            RecordProperty("TrackedObject4", GetStringValue(ATC.ME.TrackedObject4));
            RecordProperty("EnableTrackingObject4", ATC.ME.EnableTrackingObject4.ToString());

            // Record memory sensor plugs
            RecordProperty("MemorySensorPlug1", GetStringValue(ATC.ME.MemorySensorPlug1));
            RecordProperty("MemorySensorPlug2", GetStringValue(ATC.ME.MemorySensorPlug2));
            RecordProperty("MemorySensorPlug3", GetStringValue(ATC.ME.MemorySensorPlug3));
            RecordProperty("MemorySensorPlug4", GetStringValue(ATC.ME.MemorySensorPlug4));

            // Record memory transmitter plug
            RecordProperty("MemoryTransmitterPlug", GetStringValue(ATC.ME.MemoryTransmitterPlug));
        }

        /// <summary>
        /// Record version stamp information
        /// </summary>
        private void RecordVersionInfo()
        {
            if (VersionStampManager.ME == null)
            {
                RecordProperty("VersionStampManager", "Not Available");
                Debug.LogWarning("Your project needs a version stamp!");
                return;
            }

            string versionStamp = GetStringValue(VersionStampManager.ME.VersionTimeStamp);
            if (string.IsNullOrEmpty(versionStamp) || versionStamp == "None")
            {
                Debug.LogWarning("Your project needs a version stamp!");
                RecordProperty("VersionTimeStamp", "Missing");
            }
            else
            {
                RecordProperty("VersionTimeStamp", versionStamp);
            }
        }

        /// <summary>
        /// Record a single property-value pair
        /// </summary>
        private void RecordProperty(string property, string value)
        {
            csvWriter.WriteRow(0f, property, value);
        }

        /// <summary>
        /// Safely get string value from object, handling nulls
        /// </summary>
        private string GetStringValue(object obj)
        {
            if (obj == null)
                return "None";

            string str = obj.ToString();
            return string.IsNullOrEmpty(str) ? "None" : str;
        }

        /// <summary>
        /// Stop recording (nothing to do - already closed)
        /// </summary>
        public void StopRecording()
        {
            hasRecordedThisSession = false;
            Debug.Log("System recording session ended");
        }

        /// <summary>
        /// Pause recording (not applicable for SystemRecorder)
        /// </summary>
        public void PauseRecording()
        {
            // SystemRecorder doesn't support pause - it's already done recording
        }

        /// <summary>
        /// Resume recording (not applicable for SystemRecorder)
        /// </summary>
        public void ResumeRecording()
        {
            // SystemRecorder doesn't support resume - it's already done recording
        }

        /// <summary>
        /// Force flush data (not applicable - already closed)
        /// </summary>
        public void FlushData()
        {
            // CSV already closed after recording system info
        }

        /// <summary>
        /// Check if system info was recorded this session
        /// </summary>
        public bool HasRecordedThisSession => hasRecordedThisSession;

        /// <summary>
        /// Manual trigger for recording system info (for testing)
        /// </summary>
        [ContextMenu("Show Current System Info")]
        public void ShowSystemInfo()
        {
            Debug.Log($"Computer: {System.Environment.MachineName}");
            Debug.Log($"User: {System.Environment.UserName}");
            Debug.Log($"OS: {System.Environment.OSVersion}");
            Debug.Log($"Unity: {Application.unityVersion}");
            Debug.Log($"Project: {Application.productName}");

            if (ATC.ME != null)
            {
                Debug.Log($"BaseObject: {GetStringValue(ATC.ME.BaseObject)}");
                Debug.Log($"TrackedObject1: {GetStringValue(ATC.ME.TrackedObject1)}");
            }
            else
            {
                Debug.Log("ATC not available");
            }

            if (VersionStampManager.ME != null)
            {
                Debug.Log($"Version: {GetStringValue(VersionStampManager.ME.VersionTimeStamp)}");
            }
            else
            {
                Debug.Log("VersionStampManager not available");
            }
        }
    }
}