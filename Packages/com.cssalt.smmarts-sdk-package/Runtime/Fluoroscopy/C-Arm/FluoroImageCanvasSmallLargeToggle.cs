using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Toggles fluoroscopy display between small and large sizes by copying RectTransform values
    /// from preset template GameObjects.
    /// </summary>
    public class FluoroImageCanvasSmallLargeToggle : MonoBehaviour
    {
        [Header("Display Components")]
        [Tooltip("The main fluoroscopy display to resize")]
        public RectTransform FluoroDisplay;

        [Tooltip("Template for small size settings")]
        public RectTransform SmallFluoroImageCanvasRect;

        [Tooltip("Template for large size settings")]
        public RectTransform LargeFluoroImageCanvasRect;

        [Header("Debug Info")]
        [SerializeField] private bool isLargeMode = false;

        void Start()
        {
            // Start in small mode
            SetSmall();
        }

        /// <summary>
        /// Toggle between small and large sizes
        /// </summary>
        public void ToggleSize()
        {
            if (isLargeMode)
            {
                SetSmall();
            }
            else
            {
                SetLarge();
            }
        }

        /// <summary>
        /// Set display to small size
        /// </summary>
        public void SetSmall()
        {
            if (FluoroDisplay != null && SmallFluoroImageCanvasRect != null)
            {
                CopyRectTransformValues(SmallFluoroImageCanvasRect, FluoroDisplay);
                isLargeMode = false;
                Debug.Log("Fluoroscopy display set to small size");
            }
        }

        /// <summary>
        /// Set display to large size
        /// </summary>
        public void SetLarge()
        {
            if (FluoroDisplay != null && LargeFluoroImageCanvasRect != null)
            {
                CopyRectTransformValues(LargeFluoroImageCanvasRect, FluoroDisplay);
                isLargeMode = true;
                Debug.Log("Fluoroscopy display set to large size");
            }
        }

        /// <summary>
        /// Copy RectTransform values from source to target
        /// </summary>
        private void CopyRectTransformValues(RectTransform source, RectTransform target)
        {
            target.anchoredPosition = source.anchoredPosition;
            target.sizeDelta = source.sizeDelta;
            target.anchorMin = source.anchorMin;
            target.anchorMax = source.anchorMax;
            target.pivot = source.pivot;
            target.localScale = source.localScale;  // Added scale
        }
    }
}