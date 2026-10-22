using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Aggregates the last few frames of a US image texture to reduce shimmer artifacts.
    /// Reads from source US texture GameObject, maintains frame history, creates and writes to aggregated output texture.
    /// </summary>
    public class USImageAggregator : MonoBehaviour
    {
        [Header("Source and Target")]
        [Tooltip("GameObject that contains the source US Image Texture")]
        public GameObject sourceUSTextureObject;

        [Header("Frame Blend Settings")]
        [Range(2, 10)]
        [Tooltip("Number of frames to blend together")]
        public int frameHistoryCount = 5;

        [Tooltip("If true, takes maximum pixel value over time. If false, averages pixel values.")]
        public bool useMaximumFilter = false;

        [Header("Debug Info")]
        [SerializeField] private int currentFrameIndex = 0;
        [SerializeField] private bool historyInitialized = false;
        [SerializeField] private int textureWidth = 0;
        [SerializeField] private int textureHeight = 0;

        // Runtime texture references
        private Texture2D sourceUSTexture;
        private Texture2D aggregatedUSTexture;
        private Renderer sourceRenderer;
        private Renderer aggregatedRenderer;

        // Frame history storage (fast Color32 format)
        private Color32[][] frameHistory;

        // Temporary arrays for aggregation math (reused to avoid GC)
        private int[] tempR, tempG, tempB, tempA;
        private Color32[] resultPixels;

        void Start()
        {
            InitializeTextureReferences();
        }

        void Update()
        {
            if (!ValidateReferences())
                return;

            // Initialize frame history if needed
            if (!historyInitialized || TextureSizeChanged())
            {
                InitializeFrameHistory();
            }

            // Process current frame
            CaptureCurrentFrame();
            AggregateFrames();
            UpdateAggregatedTexture();

            // Move to next frame slot
            currentFrameIndex = (currentFrameIndex + 1) % frameHistoryCount;
        }

        /// <summary>
        /// Initialize texture references from GameObjects
        /// </summary>
        private void InitializeTextureReferences()
        {
            if (sourceUSTextureObject == null)
            {
                Debug.LogError("USImageAggregator: Source US Texture Object not assigned!");
                return;
            }

            // Get source renderer and texture
            sourceRenderer = sourceUSTextureObject.GetComponent<Renderer>();
            if (sourceRenderer == null)
            {
                Debug.LogError("USImageAggregator: Source GameObject has no Renderer component!");
                return;
            }

            // Get aggregated renderer (this GameObject)
            aggregatedRenderer = GetComponent<Renderer>();
            if (aggregatedRenderer == null)
            {
                Debug.LogError("USImageAggregator: This GameObject has no Renderer component!");
                return;
            }

            Debug.Log("USImageAggregator: Texture references initialized, waiting for source texture...");
        }

        /// <summary>
        /// Validate that all references are ready
        /// </summary>
        private bool ValidateReferences()
        {
            if (sourceRenderer == null || aggregatedRenderer == null)
                return false;

            // Check if source texture is available
            Texture2D currentSourceTexture = sourceRenderer.material.mainTexture as Texture2D;
            if (currentSourceTexture == null)
                return false;

            // If source texture changed, update reference
            if (sourceUSTexture != currentSourceTexture)
            {
                sourceUSTexture = currentSourceTexture;
                CreateAggregatedTexture();
                historyInitialized = false; // Force re-initialization
            }

            return sourceUSTexture != null && aggregatedUSTexture != null;
        }

        /// <summary>
        /// Create aggregated texture matching source texture dimensions
        /// </summary>
        private void CreateAggregatedTexture()
        {
            if (sourceUSTexture == null) return;

            // Create new texture with same dimensions as source
            aggregatedUSTexture = new Texture2D(
                sourceUSTexture.width,
                sourceUSTexture.height,
                sourceUSTexture.format,
                false
            );

            aggregatedUSTexture.name = "Aggregated_US_Texture";

            // Assign to this GameObject's material
            aggregatedRenderer.material.mainTexture = aggregatedUSTexture;

            Debug.Log($"USImageAggregator: Created aggregated texture {aggregatedUSTexture.width}x{aggregatedUSTexture.height}");
        }

        /// <summary>
        /// Initialize frame history arrays based on texture size
        /// </summary>
        private void InitializeFrameHistory()
        {
            if (sourceUSTexture == null) return;

            textureWidth = sourceUSTexture.width;
            textureHeight = sourceUSTexture.height;
            int pixelCount = textureWidth * textureHeight;

            // Initialize frame history array
            frameHistory = new Color32[frameHistoryCount][];
            for (int i = 0; i < frameHistoryCount; i++)
            {
                frameHistory[i] = new Color32[pixelCount];
            }

            // Initialize temporary arrays for aggregation math
            tempR = new int[pixelCount];
            tempG = new int[pixelCount];
            tempB = new int[pixelCount];
            tempA = new int[pixelCount];
            resultPixels = new Color32[pixelCount];

            // Fill initial history with current frame to avoid startup artifacts
            Color32[] currentPixels = sourceUSTexture.GetPixels32();
            for (int i = 0; i < frameHistoryCount; i++)
            {
                System.Array.Copy(currentPixels, frameHistory[i], pixelCount);
            }

            currentFrameIndex = 0;
            historyInitialized = true;

            Debug.Log($"USImageAggregator: Frame history initialized - {textureWidth}x{textureHeight}, {frameHistoryCount} frames");
        }

        /// <summary>
        /// Check if texture size has changed
        /// </summary>
        private bool TextureSizeChanged()
        {
            return sourceUSTexture != null &&
                   (sourceUSTexture.width != textureWidth || sourceUSTexture.height != textureHeight);
        }

        /// <summary>
        /// Capture current frame from source texture into history
        /// </summary>
        private void CaptureCurrentFrame()
        {
            Color32[] currentPixels = sourceUSTexture.GetPixels32();
            System.Array.Copy(currentPixels, frameHistory[currentFrameIndex], currentPixels.Length);
        }

        /// <summary>
        /// Aggregate all frames in history - either averaging or taking maximum values
        /// </summary>
        private void AggregateFrames()
        {
            int pixelCount = textureWidth * textureHeight;

            if (useMaximumFilter)
            {
                // Maximum filter - take highest value for each pixel across all frames
                for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    byte maxR = 0, maxG = 0, maxB = 0, maxA = 0;

                    for (int frameIndex = 0; frameIndex < frameHistoryCount; frameIndex++)
                    {
                        Color32 pixel = frameHistory[frameIndex][pixelIndex];
                        if (pixel.r > maxR) maxR = pixel.r;
                        if (pixel.g > maxG) maxG = pixel.g;
                        if (pixel.b > maxB) maxB = pixel.b;
                        if (pixel.a > maxA) maxA = pixel.a;
                    }

                    resultPixels[pixelIndex] = new Color32(maxR, maxG, maxB, maxA);
                }
            }
            else
            {
                // Averaging filter - smooth pixel values over time
                // Clear temporary accumulation arrays
                System.Array.Clear(tempR, 0, pixelCount);
                System.Array.Clear(tempG, 0, pixelCount);
                System.Array.Clear(tempB, 0, pixelCount);
                System.Array.Clear(tempA, 0, pixelCount);

                // Sum all frames
                for (int frameIndex = 0; frameIndex < frameHistoryCount; frameIndex++)
                {
                    Color32[] frame = frameHistory[frameIndex];
                    for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                    {
                        Color32 pixel = frame[pixelIndex];
                        tempR[pixelIndex] += pixel.r;
                        tempG[pixelIndex] += pixel.g;
                        tempB[pixelIndex] += pixel.b;
                        tempA[pixelIndex] += pixel.a;
                    }
                }

                // Average and convert back to Color32
                for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    resultPixels[pixelIndex] = new Color32(
                        (byte)(tempR[pixelIndex] / frameHistoryCount),
                        (byte)(tempG[pixelIndex] / frameHistoryCount),
                        (byte)(tempB[pixelIndex] / frameHistoryCount),
                        (byte)(tempA[pixelIndex] / frameHistoryCount)
                    );
                }
            }
        }
        /// <summary>
        /// Apply aggregated result to output texture
        /// </summary>
        private void UpdateAggregatedTexture()
        {
            aggregatedUSTexture.SetPixels32(resultPixels);
            aggregatedUSTexture.Apply();
        }

        /// <summary>
        /// Reset frame history (useful for testing)
        /// </summary>
        [ContextMenu("Reset Frame History")]
        public void ResetFrameHistory()
        {
            historyInitialized = false;
            Debug.Log("USImageAggregator: Frame history reset");
        }
    }
}
