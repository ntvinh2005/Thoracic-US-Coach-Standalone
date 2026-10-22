using UnityEngine;
using UnityEngine.UI;

namespace SMMARTS
{
    /// <summary>
    /// Generates noise overlay texture for fluoroscopy grain effect.
    /// Attaches to a UI Image component and generates noise texture with alpha channel.
    /// NOTE, this is working well at 160x160 resolution. if you need to go higher, like 1024x1024, you should move to a shader.
    /// at this low resolution there isnt a performance benefit one way or another, and this CPU script is easier to develop.
    /// </summary>
    [RequireComponent(typeof(Image))]
    public class FluoroGrainOverlay : MonoBehaviour
    {
        [Header("Noise Settings")]

        public bool GenerateNoiseEveryFrame = false;

        [Range(0f, 1f)]
        [Tooltip("Intensity of the grain effect (0 = no grain, 1 = maximum grain)")]
        public float grainIntensity = 0.3f;

        [Range(32, 512)]
        [Tooltip("Resolution of the noise texture")]
        public int textureSize = 128;

        [Range(0f, 1f)]
        [Tooltip("Base alpha transparency (0 = invisible, 1 = opaque)")]
        public float baseAlpha = 0.1f;

        [Header("Debug")]
        [SerializeField] private bool textureGenerated = false;

        private Image imageComponent;
        private Texture2D noiseTexture;

        // Store previous values to detect changes
        private float lastGrainIntensity;
        private int lastTextureSize;
        private float lastBaseAlpha;

        void Start()
        {
            imageComponent = GetComponent<Image>();
            GenerateNoiseTexture();
        }

        void Update()
        {
            // Check if any values changed
            if (GenerateNoiseEveryFrame | HasValuesChanged())
            {
                GenerateNoiseTexture();
                UpdateStoredValues();
            }
        }

        /// <summary>
        /// Check if any settings have changed since last generation
        /// </summary>
        private bool HasValuesChanged()
        {
            return grainIntensity != lastGrainIntensity ||
                   textureSize != lastTextureSize ||
                   baseAlpha != lastBaseAlpha;
        }

        /// <summary>
        /// Update stored values for change detection
        /// </summary>
        private void UpdateStoredValues()
        {
            lastGrainIntensity = grainIntensity;
            lastTextureSize = textureSize;
            lastBaseAlpha = baseAlpha;
        }

        /// <summary>
        /// Generate noise texture and apply to Image component
        /// </summary>
        private void GenerateNoiseTexture()
        {
            // Create new texture
            if (noiseTexture != null)
            {
                DestroyImmediate(noiseTexture);
            }

            noiseTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false);
            noiseTexture.name = "FluoroGrainTexture";

            // Generate noise pixels
            Color[] pixels = new Color[textureSize * textureSize];

            for (int i = 0; i < pixels.Length; i++)
            {
                // Generate random noise value
                float noise = Random.Range(-1f, 1f) * grainIntensity;

                // Create grayscale noise with alpha
                float grayValue = 0.5f + noise; // Center around 0.5
                grayValue = Mathf.Clamp01(grayValue);

                // Set pixel color (grayscale with controlled alpha)
                pixels[i] = new Color(grayValue, grayValue, grayValue, baseAlpha);
            }

            // Apply pixels to texture
            noiseTexture.SetPixels(pixels);
            noiseTexture.Apply();

            // Create sprite from texture and assign to Image
            Sprite noiseSprite = Sprite.Create(
                noiseTexture,
                new Rect(0, 0, textureSize, textureSize),
                new Vector2(0.5f, 0.5f)
            );

            imageComponent.sprite = noiseSprite;
            textureGenerated = true;

            // Debug.Log($"Generated noise texture: {textureSize}x{textureSize}, intensity: {grainIntensity}, alpha: {baseAlpha}");
        }

        /// <summary>
        /// Manually regenerate noise texture
        /// </summary>
        [ContextMenu("Regenerate Noise")]
        public void RegenerateNoise()
        {
            GenerateNoiseTexture();
        }

        void OnDestroy()
        {
            if (noiseTexture != null)
            {
                DestroyImmediate(noiseTexture);
            }
        }
    }
}