using UnityEngine;
using UnityEngine.Video;
using System.IO;
using System.Collections.Generic;

namespace SMMARTS
{
    /// <summary>
    /// Replays recorded video synchronized with session replay timeline.
    /// Discovers video files based on GameObject name and syncs playback with ReplayerManager.
    /// Uses frame-perfect synchronization based on recorded timestamps.
    /// </summary>
    public class VideoReplayer : MonoBehaviour
    {
        [Header("Replay Settings")]
        [Tooltip("Enable video replay for this recorder")]
        public bool enableReplay = true;

        [Header("Component References")]
        [Tooltip("RenderTexture to render video to (assigned manually)")]
        public RenderTexture targetRenderTexture;

        [Header("Replay Status")]
        [SerializeField] private bool isReplaying = false;
        [SerializeField] private bool isPaused = false;
        [SerializeField] private string loadedVideoPath = "";
        [SerializeField] private float videoDuration = 0f;
        [SerializeField] private int totalFrames = 0;
        [SerializeField] private int currentFrame = 0;

        private VideoPlayer videoPlayer;
        private CSVReader csvReader;
        private float replayStartTime;
        private string sessionDirectory;
        private bool isVideoReady = false;

        // Frame-perfect sync data
        private List<float> frameTimestamps; // sim_time for each frame
        private float pauseTime = 0f; // Time when paused (for resume)

        void Awake()
        {
            // Find VideoPlayer component on same GameObject
            videoPlayer = GetComponent<VideoPlayer>();
            if (videoPlayer == null)
            {
                Debug.LogError($"VideoReplayer on {gameObject.name}: No VideoPlayer component found!");
            }
        }

        /// <summary>
        /// Load video and timestamp data for replay
        /// </summary>
        /// <param name="sessionDirectory">Directory containing session recordings</param>
        /// <returns>True if loaded successfully</returns>
        public bool LoadReplayData(string sessionDirectory)
        {
            if (!enableReplay)
            {
                Debug.Log($"VideoReplayer on {gameObject.name}: Replay disabled");
                return false;
            }

            if (videoPlayer == null)
            {
                Debug.LogError($"VideoReplayer on {gameObject.name}: No VideoPlayer component");
                return false;
            }

            this.sessionDirectory = sessionDirectory;

            // Construct file paths based on GameObject name
            string videoFileName = $"{gameObject.name}_video.mp4";
            string csvFileName = $"{gameObject.name}_timestamps.csv";

            string videoPath = Path.Combine(sessionDirectory, videoFileName);
            string csvPath = Path.Combine(sessionDirectory, csvFileName);

            // Check if video file exists
            if (!File.Exists(videoPath))
            {
                Debug.LogWarning($"VideoReplayer on {gameObject.name}: Video file not found: {videoPath}");
                return false;
            }

            // Check if timestamp CSV exists
            if (!File.Exists(csvPath))
            {
                Debug.LogWarning($"VideoReplayer on {gameObject.name}: Timestamp CSV not found: {csvPath}");
                return false;
            }

            // Load timestamp CSV
            csvReader = new CSVReader(csvPath);
            if (!csvReader.LoadCSV())
            {
                Debug.LogError($"VideoReplayer on {gameObject.name}: Failed to load timestamp CSV");
                return false;
            }

            // Build frame timestamp lookup
            BuildFrameTimestampLookup();

            totalFrames = frameTimestamps.Count;

            // Configure VideoPlayer
            videoPlayer.source = VideoSource.Url;
            videoPlayer.url = videoPath;
            videoPlayer.playOnAwake = false;
            videoPlayer.isLooping = false;
            videoPlayer.waitForFirstFrame = true;
            videoPlayer.playbackSpeed = 0; // We'll control frames manually

            // Assign render texture if provided
            if (targetRenderTexture != null)
            {
                videoPlayer.renderMode = VideoRenderMode.RenderTexture;
                videoPlayer.targetTexture = targetRenderTexture;
            }

            // Prepare the video
            videoPlayer.Prepare();
            videoPlayer.prepareCompleted += OnVideoPrepared;

            loadedVideoPath = videoPath;

            Debug.Log($"VideoReplayer on {gameObject.name}: Loaded video with {totalFrames} frames");
            return true;
        }

        /// <summary>
        /// Build lookup table of frame timestamps from CSV
        /// </summary>
        private void BuildFrameTimestampLookup()
        {
            frameTimestamps = new List<float>();

            List<Dictionary<string, string>> allRows = csvReader.GetAllRows();

            foreach (var row in allRows)
            {
                if (row.ContainsKey("sim_time"))
                {
                    float timestamp = CSVReader.ParseFloat(row["sim_time"], 0f);
                    frameTimestamps.Add(timestamp);
                }
                else
                {
                    Debug.LogWarning($"VideoReplayer on {gameObject.name}: Row missing sim_time column");
                }
            }

            Debug.Log($"VideoReplayer on {gameObject.name}: Built timestamp lookup with {frameTimestamps.Count} entries");
        }

        private void OnVideoPrepared(VideoPlayer source)
        {
            videoDuration = (float)videoPlayer.length;
            isVideoReady = true;
            Debug.Log($"VideoReplayer on {gameObject.name}: Video prepared, duration: {videoDuration:F2}s, frames: {videoPlayer.frameCount}");
        }

        /// <summary>
        /// Start video replay synchronized with session
        /// </summary>
        /// <param name="replayStartTime">Time.time when replay started</param>
        public void StartReplay(float replayStartTime)
        {
            if (!enableReplay)
            {
                return;
            }

            if (videoPlayer == null)
            {
                Debug.LogError($"VideoReplayer on {gameObject.name}: No VideoPlayer component");
                return;
            }

            if (!isVideoReady)
            {
                Debug.LogWarning($"VideoReplayer on {gameObject.name}: Video not ready yet");
                return;
            }

            if (frameTimestamps == null || frameTimestamps.Count == 0)
            {
                Debug.LogError($"VideoReplayer on {gameObject.name}: No frame timestamps loaded");
                return;
            }

            this.replayStartTime = replayStartTime;

            // Start video playback (with speed 0, we control frames manually)
            videoPlayer.frame = 0;
            videoPlayer.Play();

            isReplaying = true;
            isPaused = false;
            currentFrame = 0;

            Debug.Log($"VideoReplayer on {gameObject.name}: Started frame-perfect replay");
        }

        /// <summary>
        /// Stop video replay
        /// </summary>
        public void StopReplay()
        {
            if (!isReplaying) return;

            if (videoPlayer != null)
            {
                videoPlayer.Stop();
            }

            isReplaying = false;
            isPaused = false;
            currentFrame = 0;

            Debug.Log($"VideoReplayer on {gameObject.name}: Stopped replay");
        }

        /// <summary>
        /// Pause video replay
        /// </summary>
        public void PauseReplay()
        {
            if (!isReplaying || isPaused) return;

            pauseTime = Time.time;
            isPaused = true;

            Debug.Log($"VideoReplayer on {gameObject.name}: Paused replay at frame {currentFrame}");
        }

        /// <summary>
        /// Resume video replay
        /// </summary>
        public void ResumeReplay()
        {
            if (!isReplaying || !isPaused) return;

            // Adjust replay start time to account for pause duration
            float pauseDuration = Time.time - pauseTime;
            replayStartTime += pauseDuration;

            isPaused = false;

            Debug.Log($"VideoReplayer on {gameObject.name}: Resumed replay from frame {currentFrame}");
        }

        /// <summary>
        /// Seek to an absolute replay time (seconds). Jumps the video to the matching
        /// frame immediately (binary search supports both directions).
        /// </summary>
        public void SeekToTime(float t)
        {
            replayStartTime = Time.time - t;
            pauseTime = Time.time;   // anchor pause compensation to the seek, not the original pause

            if (isVideoReady && frameTimestamps != null && frameTimestamps.Count > 0)
            {
                int targetFrame = FindFrameForTime(t);
                if (targetFrame >= 0 && targetFrame < totalFrames)
                {
                    videoPlayer.frame = targetFrame;
                    currentFrame = targetFrame;
                }
            }
        }

        /// <summary>
        /// Update video frame to stay synchronized with replay timeline
        /// </summary>
        void Update()
        {
            if (!isReplaying || isPaused || !isVideoReady) return;
            if (frameTimestamps == null || frameTimestamps.Count == 0) return;

            // Calculate current replay time (session-relative)
            float currentReplayTime = Time.time - replayStartTime;

            // Find the frame that corresponds to this replay time
            int targetFrame = FindFrameForTime(currentReplayTime);

            // Update video player frame if it changed
            if (targetFrame != currentFrame && targetFrame >= 0 && targetFrame < totalFrames)
            {
                videoPlayer.frame = targetFrame;
                currentFrame = targetFrame;
            }
        }

        /// <summary>
        /// Find the video frame index that best matches the given replay time
        /// Uses binary search for efficiency
        /// </summary>
        private int FindFrameForTime(float replayTime)
        {
            if (frameTimestamps.Count == 0) return 0;

            // Handle edge cases
            if (replayTime <= frameTimestamps[0]) return 0;
            if (replayTime >= frameTimestamps[frameTimestamps.Count - 1]) return frameTimestamps.Count - 1;

            // Binary search for closest frame
            int left = 0;
            int right = frameTimestamps.Count - 1;

            while (left <= right)
            {
                int mid = (left + right) / 2;
                float midTime = frameTimestamps[mid];

                if (Mathf.Approximately(midTime, replayTime))
                {
                    return mid;
                }
                else if (midTime < replayTime)
                {
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            // Return the closest frame
            // 'right' will be the frame just before the target time
            // 'left' will be the frame just after the target time
            if (right < 0) return 0;
            if (left >= frameTimestamps.Count) return frameTimestamps.Count - 1;

            // Choose the closest one
            float timeDiffRight = Mathf.Abs(replayTime - frameTimestamps[right]);
            float timeDiffLeft = Mathf.Abs(replayTime - frameTimestamps[left]);

            return (timeDiffRight < timeDiffLeft) ? right : left;
        }

        /// <summary>
        /// Get replay duration from timestamp data
        /// </summary>
        public float GetReplayDuration()
        {
            if (frameTimestamps == null || frameTimestamps.Count == 0)
                return 0f;

            // Return the last timestamp
            return frameTimestamps[frameTimestamps.Count - 1];
        }

        /// <summary>
        /// Public properties
        /// </summary>
        public bool IsReplaying => isReplaying;
        public bool IsPaused => isPaused;
        public string LoadedVideoPath => loadedVideoPath;
        public float VideoDuration => videoDuration;
        public int TotalFrames => totalFrames;
        public int CurrentFrame => currentFrame;

        void OnDisable()
        {
            if (isReplaying)
            {
                StopReplay();
            }
        }

        void OnDestroy()
        {
            // Unsubscribe from events
            if (videoPlayer != null)
            {
                videoPlayer.prepareCompleted -= OnVideoPrepared;
            }
        }
    }
}