using UnityEngine;
using TMPro;

// VersionStamp: Simple component to display version information on TMP text objects
// Expects to be attached to a GameObject with TextMeshProUGUI component
// Sets text once at startup using VersionStampManager data
// Dave L, Clean version display component
namespace SMMARTS
{
    public class VersionStamp : MonoBehaviour
    {
        [Header("Version Text Formatting")]
        [Tooltip("Text to show before the version (e.g., '© 2025 University of Florida ')")]
        public string Prepend = "";

        [Tooltip("Text to show after the version (e.g., ' - All Rights Reserved')")]
        public string Append = "";

        private TextMeshProUGUI textComponent;

        void Start()
        {
            // Get the TMP component on this GameObject
            textComponent = GetComponent<TextMeshProUGUI>();

            if (textComponent == null)
            {
                Debug.LogError($"VersionStamp on {gameObject.name} requires a TextMeshProUGUI component!");
                return;
            }

            // Wait for VersionStampManager to be ready, then set text
            UpdateVersionText();
        }

        private void UpdateVersionText()
        {
            if (VersionStampManager.ME == null)
            {
                Debug.LogWarning($"VersionStampManager not found! VersionStamp on {gameObject.name} will show fallback text.");
                textComponent.text = Prepend + "VSDKX.UNKNOWN" + Append;
                return;
            }

            // Build the final text: Prepend + VersionTimeStamp + Append
            string finalText = Prepend + VersionStampManager.ME.VersionTimeStamp + Append;
            textComponent.text = finalText;

        }

        // Editor helper for testing
#if UNITY_EDITOR
        [ContextMenu("Force Update Text")]
        public void ForceUpdateText()
        {
            if (textComponent == null)
                textComponent = GetComponent<TextMeshProUGUI>();
            UpdateVersionText();
        }
#endif
    }
}