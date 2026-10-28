using UnityEngine;
using TMPro;
using System.IO;

namespace SMMARTS
{
    /// <summary>
    /// Manages timestamped comments during and after recording sessions.
    /// Comments can be added anytime - during active recording or after sessions end.
    /// Caches session ID to allow post-recording comments for the same session.
    /// Singleton pattern ensures single comment management instance.
    /// </summary>
    public class CommentManager : MonoBehaviour
    {
        #region Singleton
        public static CommentManager ME { get; private set; }

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

        [Header("Comment Input")]
        public string CommentText = "";


        [Header("Comment Recording Settings")]
        [Tooltip("Enable to record comments")]
        public bool enableRecording = true;

        [Header("Debug Info")]
        [SerializeField] private string cachedSessionID = "";
        [SerializeField] private string cachedSessionDirectory = "";
        [SerializeField] private bool isRecordingActive = false;
        [SerializeField] private int commentsSubmittedThisSession = 0;

        private CSVWriter csvWriter;

        void Update()
        {
            // Monitor RecordingManager for session changes
            UpdateSessionInfo();
        }

        /// <summary>
        /// Monitor RecordingManager and cache session information
        /// </summary>
        private void UpdateSessionInfo()
        {
            if (RecordingManager.ME == null) return;

            string currentSessionID = RecordingManager.ME.GetCurrentSessionID();
            isRecordingActive = RecordingManager.ME.IsRecording;

            // Cache new session ID when it changes (new recording starts)
            if (!string.IsNullOrEmpty(currentSessionID) && currentSessionID != cachedSessionID)
            {
                cachedSessionID = currentSessionID;
                cachedSessionDirectory = RecordingManager.ME.GetSessionDirectory(cachedSessionID);
                commentsSubmittedThisSession = 0;

                Debug.Log($"CommentManager: Cached new session ID: {cachedSessionID}");
            }
        }

        /// <summary>
        /// Submit the current comment text using the current recording time as timestamp
        /// for this, we'll use the public string field and make our own timestamp.
        /// </summary>
        [ContextMenu("Submit Comment")]
        public void SubmitComment()
        {
            if (string.IsNullOrEmpty(CommentText.Trim()))
            {
                Debug.LogWarning("Comment text is empty - nothing to submit");
                return;
            }

            if (!enableRecording)
            {
                Debug.LogWarning("Comment recording disabled");
                return;
            }

            if (string.IsNullOrEmpty(cachedSessionID))
            {
                Debug.LogWarning("No session ID available - start a recording session first");
                return;
            }

            // Calculate timestamp based on recording state
            float timestamp = CalculateTimestamp();

            // Submit the comment
            SubmitComment(CommentText.Trim(), timestamp);

            // Clear the comment field
            CommentText = "";

            Debug.Log($"Comment submitted successfully");
        }


        /// <summary>
        /// Submit a specific comment using the current recording time as timestamp
        /// </summary>
        /// <param name="comment">Comment text to record</param>
        public void SubmitComment(string comment)
        {
            if (!enableRecording || string.IsNullOrEmpty(cachedSessionID))
                return;

            // Ensure CSV writer is ready
            EnsureCSVWriterReady();

            if (csvWriter != null)
            {
                // Calculate timestamp based on recording state
                float timestamp = CalculateTimestamp();

                csvWriter.WriteRow(timestamp, comment);
                commentsSubmittedThisSession++;

                // Flush immediately since comments are usually important and infrequent
                csvWriter.Flush();

                Debug.Log($"Comment recorded: '{comment}' at time {timestamp:F3}");
            }
        }


        /// <summary>
        /// Submit a specific comment with custom timestamp
        /// </summary>
        /// <param name="comment">Comment text to record</param>
        /// <param name="timestamp">Timestamp for the comment</param>
        public void SubmitComment(string comment, float timestamp)
        {
            if (!enableRecording || string.IsNullOrEmpty(cachedSessionID))
                return;

            // Ensure CSV writer is ready
            EnsureCSVWriterReady();

            if (csvWriter != null)
            {
                csvWriter.WriteRow(timestamp, comment);
                commentsSubmittedThisSession++;

                // Flush immediately since comments are usually important and infrequent
                csvWriter.Flush();

                Debug.Log($"Comment recorded: '{comment}' at time {timestamp:F3}");
            }
        }

        /// <summary>
        /// Calculate appropriate timestamp based on current recording state
        /// </summary>
        private float CalculateTimestamp()
        {
            if (isRecordingActive && RecordingManager.ME != null)
            {
                // Recording is active - use time since recording started
                // We need to get the recording start time from RecordingManager
                return Time.time - RecordingManager.ME.StartTime;

            }
            else
            {
                // Recording not active - use 0 timestamp
                return 0f;
            }
        }


        /// <summary>
        /// Ensure CSV writer is initialized for current session
        /// </summary>
        private void EnsureCSVWriterReady()
        {
            if (csvWriter == null && !string.IsNullOrEmpty(cachedSessionID))
            {
                // Create CSV writer for comments
                string[] headers = { "Time", "Comment" };
                csvWriter = new CSVWriter(cachedSessionDirectory, "Comments.csv", cachedSessionID, headers);

                Debug.Log($"Initialized comment CSV for session: {cachedSessionID}");
            }
        }

        /// <summary>
        /// Called by RecordingManager when recording starts (optional coordination)
        /// </summary>
        /// <param name="sessionDirectory">Directory path for this recording session</param>
        /// <param name="sessionID">Unique session identifier</param>
        /// <param name="startTime">Time when recording started</param>
        public void StartRecording(string sessionDirectory, string sessionID, float startTime)
        { 
            Debug.Log("CommentManager: Recording started - comments will use recording timestamps");
        }

        /// <summary>
        /// Called by RecordingManager when recording stops (optional coordination)
        /// </summary>
        public void StopRecording()
        {
            // Don't close CSV writer - allow post-recording comments
            Debug.Log("CommentManager: Recording stopped - future comments will use timestamp 0");
        }

        /// <summary>
        /// Close current session CSV (called when switching to new session)
        /// </summary>
        public void CloseCurrentSession()
        {
            if (csvWriter != null)
            {
                csvWriter.Close();
                csvWriter = null;
                Debug.Log($"Closed comment session: {cachedSessionID}");
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
        /// Check if comment recording is available
        /// </summary>
        public bool CanRecordComments => enableRecording && !string.IsNullOrEmpty(cachedSessionID);

        /// <summary>
        /// Get current session information
        /// </summary>
        public string GetCurrentSessionID() => cachedSessionID;
        public int GetCommentsSubmittedThisSession() => commentsSubmittedThisSession;


/// <summary>
/// Read the entire contents of a Comments.csv file for a specific session
/// </summary>
/// <param name="sessionID">Session ID to read comments for</param>
/// <returns>Full file contents as string, or empty string if no file exists</returns>
public string ReadCommentFile(string sessionID)
        {
            if (string.IsNullOrEmpty(sessionID))
            {
                Debug.LogWarning("Cannot read comments - sessionID is null or empty");
                return "";
            }

            string sessionDirectory = RecordingManager.ME != null
                ? RecordingManager.ME.GetSessionDirectory(sessionID)
                : Path.Combine(Application.persistentDataPath, "Session_Records", sessionID);

            string commentFilePath = Path.Combine(sessionDirectory, "Comments.csv");

            if (!File.Exists(commentFilePath))
            {
                Debug.Log($"No comment file found for session: {sessionID}");
                return "";
            }

            try
            {
                string content = File.ReadAllText(commentFilePath);
                Debug.Log($"Read comment file for session: {sessionID} ({content.Length} characters)");
                return content;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to read comment file for {sessionID}: {e.Message}");
                return "";
            }
        }

        /// <summary>
        /// Write content to a Comments.csv file for a specific session
        /// </summary>
        /// <param name="sessionID">Session ID to write comments for</param>
        /// <param name="content">Full content to write to the file</param>
        /// <returns>True if write successful</returns>
        public bool WriteCommentFile(string sessionID, string content)
        {
            if (string.IsNullOrEmpty(sessionID))
            {
                Debug.LogWarning("Cannot write comments - sessionID is null or empty");
                return false;
            }

            string sessionDirectory = RecordingManager.ME != null
                ? RecordingManager.ME.GetSessionDirectory(sessionID)
                : Path.Combine(Application.persistentDataPath, "Session_Records", sessionID);

            // Create directory if it doesn't exist
            if (!Directory.Exists(sessionDirectory))
            {
                Debug.LogWarning($"Session directory doesn't exist: {sessionDirectory}");
                return false;
            }

            string commentFilePath = Path.Combine(sessionDirectory, "Comments.csv");

            try
            {
                File.WriteAllText(commentFilePath, content);
                Debug.Log($"Wrote comment file for session: {sessionID} ({content.Length} characters)");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to write comment file for {sessionID}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if a Comments.csv file exists for a specific session
        /// </summary>
        /// <param name="sessionID">Session ID to check</param>
        /// <returns>True if comment file exists</returns>
        public bool CommentFileExists(string sessionID)
        {
            if (string.IsNullOrEmpty(sessionID))
                return false;

            string sessionDirectory = RecordingManager.ME != null
                ? RecordingManager.ME.GetSessionDirectory(sessionID)
                : Path.Combine(Application.persistentDataPath, "Session_Records", sessionID);

            string commentFilePath = Path.Combine(sessionDirectory, "Comments.csv");
            return File.Exists(commentFilePath);
        }

        /// <summary>
        /// Create an empty Comments.csv file with proper headers for a session
        /// </summary>
        /// <param name="sessionID">Session ID to create comment file for</param>
        /// <returns>True if creation successful</returns>
        public bool CreateEmptyCommentFile(string sessionID)
        {
            if (string.IsNullOrEmpty(sessionID))
                return false;

            // Create the basic CSV structure
            string emptyContent = $"SessionID,{sessionID}\nTime,Comment\n";

            return WriteCommentFile(sessionID, emptyContent);
        }

    }
}

