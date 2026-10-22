using UnityEngine;
using System.Collections.Generic;

namespace SMMARTS
{
    /// <summary>
    /// Replays recorded transform data for a specific GameObject from CSV data.
    /// Applies position and rotation from recorded sessions to recreate object movement.
    /// Handles timing, interpolation, and playback state for individual tracked objects.
    /// </summary>
    public class TrackedObjectReplayer : MonoBehaviour
    {
        [Header("Replay Settings")]
        [Tooltip("GameObject to apply replay data to (defaults to this GameObject)")]
        public GameObject TargetObject;

        [Tooltip("Enable to apply replay data to target object")]
        public bool EnableReplay = true;

        [Header("Interpolation")]
        [Tooltip("Smooth movement between recorded points")]
        public bool UseInterpolation = true;

        [Header("Debug Info")]
        [SerializeField] private bool isReplaying = false;
        [SerializeField] private string csvFileName = "";
        [SerializeField] private int totalFrames = 0;
        [SerializeField] private int currentFrame = 0;
        [SerializeField] private float currentTime = 0f;

        private CSVReader csvReader;
        private List<TransformData> transformData;
        private float replayStartTime;
        private float pauseTime = 0f;
        private int lastFrameIndex = 0;

        /// <summary>
        /// Structure to hold transform data for a single frame
        /// </summary>
        [System.Serializable]
        public struct TransformData
        {
            public float time;
            public Vector3 position;
            public Quaternion rotation;

            public TransformData(float t, Vector3 pos, Quaternion rot)
            {
                time = t;
                position = pos;
                rotation = rot;
            }
        }

        void Start()
        {
            // Default to this GameObject if no target specified
            if (TargetObject == null)
                TargetObject = gameObject;

            csvFileName = TargetObject.name + ".csv";
            transformData = new List<TransformData>();

            // Debug.Log($"TrackedObjectReplayer initialized for {targetObject.name}");
        }

        void Update()
        {
            // Apply replay data if actively replaying
            if (isReplaying && EnableReplay && transformData.Count > 0)
            {
                ApplyReplayData();
            }
        }

        /// <summary>
        /// Load CSV data for this tracked object
        /// </summary>
        /// <param name="sessionDirectory">Directory containing session CSV files</param>
        /// <returns>True if loaded successfully</returns>
        public bool LoadReplayData(string sessionDirectory)
        {
            string csvPath = System.IO.Path.Combine(sessionDirectory, csvFileName);

            csvReader = new CSVReader(csvPath);
            if (!csvReader.LoadCSV())
            {
                // Debug.LogError($"Failed to load replay data for {targetObject.name}");
                return false;
            }

            // Parse transform data from CSV
            return ParseTransformData();
        }

        /// <summary>
        /// Parse CSV data into transform structures
        /// </summary>
        private bool ParseTransformData()
        {
            transformData.Clear();

            var rows = csvReader.GetAllRows();
            foreach (var row in rows)
            {
                try
                {
                    float time = CSVReader.ParseFloat(row["Time"]);

                    Vector3 position = new Vector3(
                        CSVReader.ParseFloat(row["PosX"]),
                        CSVReader.ParseFloat(row["PosY"]),
                        CSVReader.ParseFloat(row["PosZ"])
                    );

                    Quaternion rotation = new Quaternion(
                        CSVReader.ParseFloat(row["QuatX"]),
                        CSVReader.ParseFloat(row["QuatY"]),
                        CSVReader.ParseFloat(row["QuatZ"]),
                        CSVReader.ParseFloat(row["QuatW"])
                    );

                    transformData.Add(new TransformData(time, position, rotation));
                }
                catch (System.Exception e)
                {
                    // Debug.LogWarning($"Error parsing transform data row: {e.Message}");
                }
            }

            totalFrames = transformData.Count;
            // Debug.Log($"Parsed {totalFrames} transform frames for {targetObject.name}");

            return totalFrames > 0;
        }

        /// <summary>
        /// Start replay from the beginning
        /// Called by ReplayerManager when replay begins
        /// </summary>
        /// <param name="startTime">Time when replay started</param>
        public void StartReplay(float startTime)
        {
            if (!EnableReplay || transformData.Count == 0)
            {
                // Debug.LogWarning($"Cannot start replay for {targetObject.name} - no data or disabled");
                return;
            }

            replayStartTime = startTime;
            lastFrameIndex = 0;
            currentFrame = 0;
            isReplaying = true;

            // Debug.Log($"Started replay for {targetObject.name} with {totalFrames} frames");
        }

        /// <summary>
        /// Stop replay
        /// </summary>
        public void StopReplay()
        {
            isReplaying = false;
            currentFrame = 0;
            currentTime = 0f;
            // Debug.Log($"Stopped replay for {targetObject.name}");
        }

        /// <summary>
        /// Pause replay
        /// </summary>
        public void PauseReplay()
        {
            if (!isReplaying) return;
            pauseTime = Time.time;
            isReplaying = false;
            // Debug.Log($"Paused replay for {targetObject.name}");
        }

        /// <summary>
        /// Resume replay
        /// </summary>
        public void ResumeReplay()
        {
            if (transformData.Count > 0 && EnableReplay)
            {
                // Shift the start time forward by the paused duration so playback
                // continues from where it paused instead of jumping ahead.
                replayStartTime += Time.time - pauseTime;
                isReplaying = true;
                // Debug.Log($"Resumed replay for {targetObject.name}");
            }
        }

        /// <summary>
        /// Seek to an absolute replay time (seconds). Repositions so the existing
        /// time math yields this time, supports backward seek, and previews the
        /// transform immediately even while paused/stopped.
        /// </summary>
        public void SeekToTime(float t)
        {
            if (transformData.Count == 0) return;

            replayStartTime = Time.time - t;
            pauseTime = Time.time;   // anchor pause compensation to the seek, not the original pause
            lastFrameIndex = 0;   // allow the forward-only search to find earlier frames
            ApplyReplayData();    // apply the frame at the new time immediately
        }

        /// <summary>
        /// Apply transform data based on current replay time
        /// </summary>
        private void ApplyReplayData()
        {
            currentTime = Time.time - replayStartTime;

            // Find the appropriate frame(s) for current time
            int frameIndex = FindFrameIndex(currentTime);

            if (frameIndex >= transformData.Count)
            {
                // Replay finished - stay at last position
                ApplyTransform(transformData[transformData.Count - 1]);
                return;
            }

            currentFrame = frameIndex;

            if (UseInterpolation && frameIndex < transformData.Count - 1)
            {
                // Interpolate between current and next frame
                ApplyInterpolatedTransform(frameIndex, currentTime);
            }
            else
            {
                // Apply exact frame data
                ApplyTransform(transformData[frameIndex]);
            }
        }

        /// <summary>
        /// Find the frame index for a given time
        /// </summary>
        private int FindFrameIndex(float time)
        {
            // Start search from last known position for efficiency
            for (int i = lastFrameIndex; i < transformData.Count; i++)
            {
                if (transformData[i].time >= time)
                {
                    lastFrameIndex = Mathf.Max(0, i - 1);
                    return i;
                }
            }

            // Time is beyond all recorded data
            return transformData.Count;
        }

        /// <summary>
        /// Apply interpolated transform between two frames
        /// </summary>
        private void ApplyInterpolatedTransform(int frameIndex, float time)
        {
            TransformData currentData = transformData[frameIndex];
            TransformData nextData = transformData[frameIndex + 1];

            // Calculate interpolation factor
            float timeDiff = nextData.time - currentData.time;
            float t = (timeDiff > 0) ? (time - currentData.time) / timeDiff : 0f;
            t = Mathf.Clamp01(t);

            // Interpolate position and rotation
            Vector3 interpolatedPosition = Vector3.Lerp(currentData.position, nextData.position, t);
            Quaternion interpolatedRotation = Quaternion.Lerp(currentData.rotation, nextData.rotation, t);

            // Apply to target object
            TargetObject.transform.position = interpolatedPosition;
            TargetObject.transform.rotation = interpolatedRotation;
        }

        /// <summary>
        /// Apply exact transform data
        /// </summary>
        private void ApplyTransform(TransformData data)
        {
            TargetObject.transform.position = data.position;
            TargetObject.transform.rotation = data.rotation;
        }

        /// <summary>
        /// Check if replay is currently active
        /// </summary>
        public bool IsReplaying => isReplaying && EnableReplay;

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
            if (transformData.Count == 0) return 0f;
            return transformData[transformData.Count - 1].time;
        }
    }
}