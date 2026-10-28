using UnityEngine;
using System.Diagnostics;
using System.IO;
using System.Collections;
using SMMARTS;

namespace SMMARTS
{
    /// <summary>
    /// Records video from webcam and/or Unity camera with frame-accurate timestamps.
    /// Integrates with RecordingManager for session-based recording.
    /// </summary>
    public class VideoRecorder : MonoBehaviour
    {
        [Header("Recording Settings")]
        [Tooltip("Enable to record video data")]
        public bool EnableRecording = true;

        [Tooltip("Include webcam feed in recording")]
        public bool IncludeWebcam = true;

        [Tooltip("Include Unity camera component in recording")]
        public bool IncludeCameraComponent = false;

        [Header("Webcam Settings")]
        [Tooltip("Specific webcam name or number from the list of webcams. If empty, uses first available webcam")]
        // you can use a specific webcam name (like "Microsoft� LifeCam HD - 3000" note the "�" which is in the name)
        // or a number from the list of webcams (like 0 for the first webcam, 1, for the second webcam).
        // If empty, uses first available webcam it finds.
        public string WebcamName = ""; // or number from the list of webcams
        public int webcamWidth = 320;
        public int webcamHeight = 240;

        [Header("Unity Camera Settings")]
        public Camera captureCamera;
        public int captureWidth = 1280;
        public int captureHeight = 720;
        public int targetFPS = 30;

        [Header("Log File Name")]
        [SerializeField] private string logFileName; // based on the name of this gameobject.

        [Header("FFmpeg Settings")]
        [Tooltip("Full path to ffmpeg.exe, leave empty to use system PATH")]
        public string ffmpegPath = "";

        [Header("Encoding Quality")]
        [Range(18, 28)]
        [Tooltip("Lower = better quality but larger file (23 is good default)")]
        public int crf = 23;

        [Tooltip("ultrafast, superfast, veryfast, faster, fast, medium")]
        public string preset = "ultrafast";

        [Header("Status")]
        [SerializeField] private bool isRecording = false;
        [SerializeField] private int currentFrame = 0;
        [SerializeField] private string lastRecordingPath = "";

        private RenderTexture renderTexture;
        private Texture2D frameTexture;
        private WebCamTexture webCamTexture;
        private Process ffmpegProcess;
        private BinaryWriter ffmpegInput;
        private CSVWriter csvWriter;
        private int frameCount = 0;
        private string outputPath;
        private Coroutine captureCoroutine;
        private int outputWidth;
        private int outputHeight;
        private float sessionStartTime;
        private string currentSessionDirectory;
        private string currentSessionID;

        // Context menu buttons for easy testing
        [ContextMenu("Start Recording Test")]
        public void StartRecordingTest()
        {
            string testDirectory = Path.Combine(Application.persistentDataPath, "TestSession");
            string testSessionID = "TestSession_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            StartRecording(testDirectory, testSessionID, Time.time);
        }

        [ContextMenu("Stop Recording")]
        public void StopRecordingTest()
        {
            StopRecording();
        }

        /// <summary>
        /// Start video recording for a session
        /// </summary>
        /// <param name="sessionDirectory">Directory where session files are stored</param>
        /// <param name="sessionID">Unique session identifier</param>
        /// <param name="sessionStartTime">Session start time (Time.time)</param>
        public void StartRecording(string sessionDirectory, string sessionID, float sessionStartTime)
        {
            if (!EnableRecording)
            {
                UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: Recording disabled");
                return;
            }

            if (isRecording)
            {
                UnityEngine.Debug.LogWarning($"VideoRecorder on {gameObject.name}: Already recording!");
                return;
            }

            // Validate recording sources
            if (!IncludeCameraComponent && !IncludeWebcam)
            {
                UnityEngine.Debug.LogWarning($"VideoRecorder on {gameObject.name}: Both IncludeCameraComponent and IncludeWebcam are disabled. Enable at least one.");
                return;
            }

            if (IncludeCameraComponent && captureCamera == null)
            {
                UnityEngine.Debug.LogError($"VideoRecorder on {gameObject.name}: IncludeCameraComponent enabled but no capture camera assigned!");
                return;
            }

            // Store session info
            currentSessionDirectory = sessionDirectory;
            currentSessionID = sessionID;
            this.sessionStartTime = sessionStartTime;

            // Ensure session directory exists
            if (!Directory.Exists(currentSessionDirectory))
            {
                Directory.CreateDirectory(currentSessionDirectory);
            }

            // Determine output resolution based on what we're capturing
            if (IncludeCameraComponent)
            {
                outputWidth = captureWidth;
                outputHeight = captureHeight;
            }
            else // webcam only
            {
                outputWidth = webcamWidth;
                outputHeight = webcamHeight;
            }

            // Setup webcam if needed and determine actual webcam name
            if (IncludeWebcam)
            {
                if (!SetupWebcam())
                {
                    // If we were only supposed to record webcam, abort
                    if (!IncludeCameraComponent)
                    {
                        UnityEngine.Debug.LogError($"VideoRecorder on {gameObject.name}: Webcam-only mode but no webcam available");
                        return;
                    }
                    // Otherwise continue without webcam
                    IncludeWebcam = false;
                }
            }

            // Set the log file name for filename generation.
            // VideoReplayer derives filenames from the GameObject name, so we must match it exactly.
            logFileName = gameObject.name;

            // Generate filenames using sessionID and webcam name
            string videoFilename = $"{logFileName}_video.mp4";
            string csvFilename = $"{logFileName}_timestamps.csv";

            outputPath = Path.Combine(currentSessionDirectory, videoFilename);

            UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: Starting recording to {outputPath}");
            UnityEngine.Debug.Log($"Recording mode - Camera: {IncludeCameraComponent}, Webcam: {IncludeWebcam}");

            // Setup render texture for display capture if needed
            if (IncludeCameraComponent)
            {
                renderTexture = new RenderTexture(captureWidth, captureHeight, 24);
            }

            // Create frame texture at output resolution
            frameTexture = new Texture2D(outputWidth, outputHeight, TextureFormat.RGB24, false);

            // Start FFmpeg process
            if (!StartFFmpegProcess())
            {
                Cleanup();
                return;
            }

            // Setup CSV writer for timestamps
            try
            {
                csvWriter = new CSVWriter(
                    currentSessionDirectory,
                    csvFilename,
                    currentSessionID,
                    //new string[] { "frame", "sim_time" }
                    new string[] { "sim_time", "frame" }
                );

                // Set buffer to flush every frame - video timing is critical
                csvWriter.SetBufferFlushThreshold(1);

            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"VideoRecorder on {gameObject.name}: Failed to create CSV writer: {e.Message}");
                Cleanup();
                return;
            }

            isRecording = true;
            frameCount = 0;
            currentFrame = 0;
            lastRecordingPath = outputPath;

            captureCoroutine = StartCoroutine(CaptureFrames());
        }

        /// <summary>
        /// Setup webcam and determine actual webcam name for filename
        /// Supports selection by index (e.g., "0", "1", "2") or by device name
        /// </summary>
        private bool SetupWebcam()
        {
            WebCamDevice[] devices = WebCamTexture.devices;
            if (devices.Length == 0)
            {
                UnityEngine.Debug.LogWarning($"VideoRecorder on {gameObject.name}: No webcam found");
                return false;
            }

            WebCamDevice selectedDevice;
            bool deviceFound = false;

            // Case 1: WebcamName is empty - use first webcam
            if (string.IsNullOrEmpty(WebcamName))
            {
                selectedDevice = devices[0];
                deviceFound = true;
                UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: No webcam specified, using first available: {selectedDevice.name}");
            }
            // Case 2: Try to parse as index number
            else if (int.TryParse(WebcamName, out int index))
            {
                if (index >= 0 && index < devices.Length)
                {
                    selectedDevice = devices[index];
                    deviceFound = true;
                    UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: Using webcam at index {index}: {selectedDevice.name}");
                }
                else
                {
                    UnityEngine.Debug.LogWarning($"VideoRecorder on {gameObject.name}: Webcam index {index} out of bounds. Available webcams: 0-{devices.Length - 1}");
                    return false;
                }
            }
            // Case 3: Treat as device name string
            else
            {
                selectedDevice = default;
                foreach (WebCamDevice device in devices)
                {
                    if (device.name == WebcamName)
                    {
                        selectedDevice = device;
                        deviceFound = true;
                        UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: Found webcam by name: {selectedDevice.name}");
                        break;
                    }
                }

                if (!deviceFound)
                {
                    UnityEngine.Debug.LogWarning($"VideoRecorder on {gameObject.name}: Webcam '{WebcamName}' not found. Use 'List Available Webcams' to see options.");
                    return false;
                }
            }

            // Initialize the selected webcam
            webCamTexture = new WebCamTexture(selectedDevice.name, webcamWidth, webcamHeight, targetFPS);
            webCamTexture.Play();

            UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: Started webcam: {selectedDevice.name}");
            StartCoroutine(WaitForWebcam());

            return true;
        }

        private IEnumerator WaitForWebcam()
        {
            int timeout = 100; // 10 seconds max
            while (webCamTexture != null && !webCamTexture.didUpdateThisFrame && timeout > 0)
            {
                yield return new WaitForSeconds(0.1f);
                timeout--;
            }

            if (webCamTexture != null && webCamTexture.didUpdateThisFrame)
            {
                UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: Webcam is ready!");
            }
            else
            {
                UnityEngine.Debug.LogWarning($"VideoRecorder on {gameObject.name}: Webcam timed out waiting for frames");
            }
        }

        private bool StartFFmpegProcess()
        {
            // FFmpeg video recording spawns ffmpeg.exe and is only supported on Windows.
            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.WindowsEditor)
            {
                UnityEngine.Debug.LogError($"VideoRecorder on {gameObject.name}: FFmpeg video recording is only supported on Windows.");
                return false;
            }

            string ffmpegExe = string.IsNullOrEmpty(ffmpegPath) ? "ffmpeg" : ffmpegPath;

            // Check if ffmpeg exists
            if (!string.IsNullOrEmpty(ffmpegPath) && !File.Exists(ffmpegPath))
            {
                UnityEngine.Debug.LogError($"VideoRecorder on {gameObject.name}: FFmpeg not found at: {ffmpegPath}");
                return false;
            }

            // FFmpeg arguments for piped input - use actual output resolution
            string arguments = $"-y -f rawvideo -pixel_format rgb24 -video_size {outputWidth}x{outputHeight} " +
                              $"-framerate {targetFPS} -i pipe:0 " +
                              $"-c:v libx264 -preset {preset} -crf {crf} -pix_fmt yuv420p " +
                              $"\"{outputPath}\"";

            UnityEngine.Debug.Log($"VideoRecorder FFmpeg command: {ffmpegExe} {arguments}");

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            try
            {
                ffmpegProcess = new Process { StartInfo = startInfo };
                ffmpegProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        UnityEngine.Debug.Log($"FFmpeg: {e.Data}");
                };

                ffmpegProcess.Start();
                ffmpegProcess.BeginErrorReadLine();

                ffmpegInput = new BinaryWriter(ffmpegProcess.StandardInput.BaseStream);

                UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: FFmpeg process started successfully");
                return true;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"VideoRecorder on {gameObject.name}: Failed to start FFmpeg: {e.Message}");
                return false;
            }
        }

        private IEnumerator CaptureFrames()
        {
            float frameInterval = 1f / targetFPS;

            while (isRecording)
            {
                yield return new WaitForSeconds(frameInterval);

                try
                {
                    byte[] frameData = null;

                    // Determine what to capture based on mode
                    if (IncludeCameraComponent && IncludeWebcam)
                    {
                        // Both: composite webcam onto display
                        frameData = CaptureDisplayWithWebcam();
                    }
                    else if (IncludeCameraComponent)
                    {
                        // Display only
                        frameData = CaptureDisplayOnly();
                    }
                    else if (IncludeWebcam)
                    {
                        // Webcam only
                        frameData = CaptureWebcamOnly();
                    }

                    if (frameData == null)
                    {
                        UnityEngine.Debug.LogError($"VideoRecorder on {gameObject.name}: Failed to capture frame data");
                        StopRecording();
                        yield break;
                    }

                    // Write to FFmpeg
                    ffmpegInput.Write(frameData);
                    ffmpegInput.Flush();

                    // Log timestamp (session-relative)
                    float currentTime = Time.time - sessionStartTime;
                    csvWriter.WriteRow(currentTime, frameCount);

                    frameCount++;
                    currentFrame = frameCount;
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogError($"VideoRecorder on {gameObject.name}: Error capturing frame: {e.Message}");
                    StopRecording();
                    yield break;
                }
            }
        }

        private byte[] CaptureDisplayOnly()
        {
            // Render camera to texture WITHOUT hijacking its normal rendering
            RenderTexture previousRT = captureCamera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;

            captureCamera.targetTexture = renderTexture;
            captureCamera.Render();
            captureCamera.targetTexture = previousRT;

            // Capture frame
            RenderTexture.active = renderTexture;
            frameTexture.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
            frameTexture.Apply();
            RenderTexture.active = previousActive;

            // Flip vertically and return
            return FlipTextureVertically(frameTexture);
        }

        private byte[] CaptureWebcamOnly()
        {
            if (webCamTexture == null || !webCamTexture.isPlaying)
            {
                // Return black frame
                Color[] blackPixels = new Color[webcamWidth * webcamHeight];
                frameTexture.SetPixels(blackPixels);
                frameTexture.Apply();
            }
            else
            {
                // Copy webcam directly to frame texture
                Color[] webcamPixels = webCamTexture.GetPixels();
                frameTexture.SetPixels(webcamPixels);
                frameTexture.Apply();
            }

            // Flip vertically and return
            return FlipTextureVertically(frameTexture);
        }

        private byte[] CaptureDisplayWithWebcam()
        {
            // First capture display
            RenderTexture previousRT = captureCamera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;

            captureCamera.targetTexture = renderTexture;
            captureCamera.Render();
            captureCamera.targetTexture = previousRT;

            // Capture frame
            RenderTexture.active = renderTexture;
            frameTexture.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
            frameTexture.Apply();
            RenderTexture.active = previousActive;

            // Composite webcam if it's ready
            if (webCamTexture != null && webCamTexture.isPlaying && webCamTexture.didUpdateThisFrame)
            {
                CompositeWebcam();
            }

            // Flip vertically and return
            return FlipTextureVertically(frameTexture);
        }

        private void CompositeWebcam()
        {
            // Simple overlay in corner
            Color[] webcamPixels = webCamTexture.GetPixels();
            int offsetX = captureWidth - webcamWidth - 10;
            int offsetY = captureHeight - webcamHeight - 10;

            for (int y = 0; y < webcamHeight; y++)
            {
                for (int x = 0; x < webcamWidth; x++)
                {
                    int sourceIndex = y * webcamWidth + x;
                    int destX = offsetX + x;
                    int destY = offsetY + y;

                    if (destX >= 0 && destX < captureWidth && destY >= 0 && destY < captureHeight)
                    {
                        frameTexture.SetPixel(destX, destY, webcamPixels[sourceIndex]);
                    }
                }
            }
            frameTexture.Apply();
        }

        private byte[] FlipTextureVertically(Texture2D texture)
        {
            Color[] pixels = texture.GetPixels();
            Color[] flippedPixels = new Color[pixels.Length];

            int width = texture.width;
            int height = texture.height;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    flippedPixels[x + y * width] = pixels[x + (height - y - 1) * width];
                }
            }

            Texture2D flippedTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            flippedTexture.SetPixels(flippedPixels);
            flippedTexture.Apply();

            byte[] result = flippedTexture.GetRawTextureData();
            Destroy(flippedTexture);

            return result;
        }

        public void StopRecording()
        {
            if (!isRecording) return;

            UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: Stopping recording. Captured {frameCount} frames.");

            isRecording = false;

            if (captureCoroutine != null)
            {
                StopCoroutine(captureCoroutine);
                captureCoroutine = null;
            }

            Cleanup();

            UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: Recording saved to: {outputPath}");
        }

        private void Cleanup()
        {
            // Close FFmpeg input
            if (ffmpegInput != null)
            {
                try
                {
                    ffmpegInput.Close();
                    ffmpegInput.Dispose();
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogWarning($"VideoRecorder on {gameObject.name}: Error closing FFmpeg input: {e.Message}");
                }
                ffmpegInput = null;
            }

            // Wait for FFmpeg to finish
            if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            {
                ffmpegProcess.WaitForExit(5000);
                if (!ffmpegProcess.HasExited)
                {
                    UnityEngine.Debug.LogWarning($"VideoRecorder on {gameObject.name}: FFmpeg did not exit cleanly, forcing...");
                    ffmpegProcess.Kill();
                }
                ffmpegProcess.Close();
                ffmpegProcess = null;
            }

            // Close CSV writer
            if (csvWriter != null)
            {
                csvWriter.Close();
                csvWriter = null;
            }

            // Stop webcam
            if (webCamTexture != null)
            {
                webCamTexture.Stop();
                webCamTexture = null;
            }

            // Release camera (only if we were using it)
            if (IncludeCameraComponent && captureCamera != null)
            {
                captureCamera.targetTexture = null;
            }

            // Release render texture
            if (renderTexture != null)
            {
                renderTexture.Release();
                Destroy(renderTexture);
                renderTexture = null;
            }

            if (frameTexture != null)
            {
                Destroy(frameTexture);
                frameTexture = null;
            }
        }

        /// <summary>
        /// Force flush timestamp data to disk
        /// note, we don't really buffer the CSV writer like the other recorders because we need it to sync to the video file
        /// we set the CSVWriter buffer size to one frame. 
        /// </summary>
        public void FlushData() 
        {
            if (csvWriter != null)
            {
                csvWriter.Flush();
            }
        }

        private void OnApplicationQuit()
        {
            if (isRecording)
            {
                StopRecording();
            }
        }

        private void OnDisable()
        {
            if (isRecording)
            {
                StopRecording();
            }
        }

        /// <summary>
        /// List all available webcams with their indices for easy selection.
        /// </summary>
        [ContextMenu("List Available Webcams")]
        private void ListAvailableWebcams()
        {
            WebCamDevice[] devices = WebCamTexture.devices;

            if (devices.Length == 0)
            {
                UnityEngine.Debug.Log($"VideoRecorder on {gameObject.name}: No webcams detected");
                return;
            }

            UnityEngine.Debug.Log($"=== VideoRecorder on {gameObject.name}: Available Webcams ({devices.Length}) ===");
            for (int i = 0; i < devices.Length; i++)
            {
                UnityEngine.Debug.Log($"[{i}] {devices[i].name}");
            }
            UnityEngine.Debug.Log("=== Set WebcamName to index number (e.g., '0', '1') or exact device name ===");
        }
    }
}