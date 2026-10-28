using UnityEngine;
using SMMARTS;

namespace SMMARTS
{
    /// <summary>
    /// Component that records position and rotation data for a GameObject during recording sessions.
    /// Attaches to any GameObject that needs tracking. Uses GameObject name for CSV filename.
    /// Can record every frame or only when transforms change based on RecordEveryFrame setting.
    /// </summary>
    public class TrackedObjectRecorder : MonoBehaviour
    {
        [Header("Recording Settings")]
        [Tooltip("Enable to record this object's transform data")]
        public bool EnableRecording = true;

        [Tooltip("Record every frame (true) or only when position/rotation changes (false)")]
        public bool RecordEveryFrame = true;

        [Header("Debug Info")]
        [SerializeField] private bool isRecording = false;
        [SerializeField] private string csvFileName = "";

        private CSVWriter csvWriter;
        private float recordingStartTime;
        private float pauseTime = 0f;

        // Previous transform values for change detection
        private Vector3 previousPosition;
        private Quaternion previousRotation;
        private bool hasInitialValues = false;

        void Start()
        {
            // Set CSV filename based on GameObject name
            csvFileName = gameObject.name + ".csv";
            // Debug.Log($"TrackedObjectRecorder initialized on {gameObject.name} - RecordEveryFrame: {RecordEveryFrame}");
        }

        void Update()
        {
            // Only record if we're in an active recording session and enabled
            if (isRecording && EnableRecording && csvWriter != null)
            {
                if (RecordEveryFrame)
                {
                    // Original behavior - record every frame
                    RecordCurrentTransform();
                }
                else
                {
                    // New behavior - only record when transforms change
                    CheckForChangesAndRecord();
                }
            }
        }

        /// <summary>
        /// Start recording this object's transform data
        /// Called by RecordingManager when recording begins
        /// </summary>
        /// <param name="sessionDirectory">Directory path for this recording session</param>
        /// <param name="sessionID">Unique session identifier</param>
        /// <param name="startTime">Time when recording started (for timestamp calculation)</param>
        public void StartRecording(string sessionDirectory, string sessionID, float startTime)
        {
            if (!EnableRecording)
            {
                // Debug.Log($"Recording disabled for {gameObject.name}");
                return;
            }

            recordingStartTime = startTime;

            // Define column headers for tracked object CSV
            string[] headers = { "Time", "PosX", "PosY", "PosZ", "QuatX", "QuatY", "QuatZ", "QuatW" };

            // Initialize CSV writer
            csvWriter = new CSVWriter(sessionDirectory, csvFileName, sessionID, headers);

            // Initialize change detection
            previousPosition = transform.position;
            previousRotation = transform.rotation;
            hasInitialValues = true;

            // Always record initial position regardless of recordEveryFrame setting
            RecordCurrentTransform();

            isRecording = true;
            Debug.Log($"Started recording {gameObject.name} to {csvFileName} (RecordEveryFrame: {RecordEveryFrame})");
        }

        /// <summary>
        /// Check if transform changed and record if so (for recordEveryFrame = false)
        /// </summary>
        private void CheckForChangesAndRecord()
        {
            if (!hasInitialValues) return;

            Vector3 currentPosition = transform.position;
            Quaternion currentRotation = transform.rotation;

            // Check if position or rotation changed (with small tolerance for floating point precision)
            bool positionChanged = Vector3.Distance(currentPosition, previousPosition) > 1e-6f;
            bool rotationChanged = Quaternion.Angle(currentRotation, previousRotation) > 1e-4f;

            if (positionChanged || rotationChanged)
            {
                RecordCurrentTransform();

                // Update previous values
                previousPosition = currentPosition;
                previousRotation = currentRotation;
            }
        }

        /// <summary>
        /// Stop recording and finalize the CSV file
        /// Called by RecordingManager when recording ends
        /// </summary>
        public void StopRecording()
        {
            if (csvWriter != null)
            {
                csvWriter.Close();
                csvWriter = null;
            }

            isRecording = false;
            hasInitialValues = false;
            // Debug.Log($"Stopped recording {gameObject.name}");
        }

        /// <summary>
        /// Pause recording (stops writing data but keeps CSV writer alive)
        /// </summary>
        public void PauseRecording()
        {
            if (!isRecording) return;
            pauseTime = Time.time;
            isRecording = false;
            // Debug.Log($"Paused recording {gameObject.name}");
        }

        /// <summary>
        /// Resume recording from pause
        /// </summary>
        public void ResumeRecording()
        {
            if (csvWriter != null && EnableRecording)
            {
                // Exclude the paused span from recorded timestamps so they stay continuous.
                recordingStartTime += Time.time - pauseTime;
                isRecording = true;
                // Debug.Log($"Resumed recording {gameObject.name}");
            }
        }

        /// <summary>
        /// Record current transform data to CSV
        /// </summary>
        private void RecordCurrentTransform()
        {
            float currentTime = Time.time - recordingStartTime;

            Vector3 pos = transform.position;    // World position
            Quaternion rot = transform.rotation; // World rotation

            // Write row: Time, PosX, PosY, PosZ, QuatX, QuatY, QuatZ, QuatW
            csvWriter.WriteRow(
                currentTime,
                pos.x, pos.y, pos.z,
                rot.x, rot.y, rot.z, rot.w
            );
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
        public bool IsRecording => isRecording && EnableRecording;

        /// <summary>
        /// Get the CSV filename this recorder will use
        /// </summary>
        public string GetCSVFileName() => csvFileName;
    }
}