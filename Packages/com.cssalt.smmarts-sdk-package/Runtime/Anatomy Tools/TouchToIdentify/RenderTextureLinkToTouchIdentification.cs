using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

/// <summary>
/// RenderTextureLinkToTouchIdentification: Bridges UI canvas clicks to TouchIdentification
/// 
/// PURPOSE:
/// Connects render texture displays on UI canvas to their source camera's TouchIdentification
/// When user clicks on the render texture, forwards the click to the appropriate TouchID component
/// 
/// USAGE:
/// - Attach to UI Image component displaying a render texture 
/// - Assign the source camera's TouchIdentification component to touchHandler
/// - Clicks on the render texture will be processed as if clicking through the camera 
/// - Be sure to turn on Raycast Target, so clicks on the render texture image work!
/// 
/// COORDINATE TRANSFORMATION:
/// Converts UI canvas click coordinates to camera screen coordinates for raycasting
/// </summary>
namespace SMMARTS
{
    public class RenderTextureLinkToTouchIdentification : MonoBehaviour, IPointerClickHandler
    {
        [Header("Touch Handler Link - set this manually")]
        [Tooltip("The TouchIdentification component (on source camera) that should handle clicks through this render texture")]
        public TouchIdentification touchHandler;

        [Header("UI References")]
        [Tooltip("RectTransform of this UI element (auto-assigned if null)")]
        public RectTransform rectTransform;

        void Start()
        {
            // Auto-assign RectTransform if not set
            if (rectTransform == null)
            {
                rectTransform = GetComponent<RectTransform>();
            }

            // Validation
            if (touchHandler == null)
            {
                Debug.LogError($"RenderTextureLinkToTouchIdentification on {gameObject.name}: No TouchIdentification assigned!");
            }
            else
            {
                if (touchHandler.targetCamera != null & touchHandler.targetCamera.targetTexture == null)
                {
                    Debug.LogError($"RenderTextureLinkToTouchIdentification on {gameObject.name}: TouchIdentification needs to be on a render texture camera!");
                }
            }

        }

        /// <summary>
        /// Handle clicks on the render texture UI element
        /// Forwards to the linked TouchIdentification component for processing
        /// </summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            if (touchHandler != null)
            {
                touchHandler.ProcessRenderTextureClick(eventData, rectTransform);
            }
            else
            {
                Debug.LogWarning($"RenderTextureLinkToTouchIdentification on {gameObject.name}: No TouchIdentification assigned to handle click!");
            }
        }
    }
}
