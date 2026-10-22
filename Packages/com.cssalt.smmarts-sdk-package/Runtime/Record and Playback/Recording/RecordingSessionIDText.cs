using UnityEngine;
using TMPro;

namespace SMMARTS
{
    /// <summary>
    /// Updates a TextMeshPro component to display the current session ID from RecordingManager.
    /// </summary>
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class RecordingSessionIDText : MonoBehaviour
    {
        [Header("Text Settings")]
        [Tooltip("TextMeshPro component to update with session ID")]
        public TextMeshProUGUI sessionText;

        [Header("Debug Info")]
        [SerializeField] private string currentSessionID = "";

        void Start()
        {
            // Auto-find TextMeshPro component if not assigned
            if (sessionText == null)
            {
                sessionText = GetComponent<TextMeshProUGUI>();
            }

            if (sessionText == null)
            {
                Debug.LogError("SessionIDText: No TextMeshProUGUI component found!");
            }
        }

        void Update()
        {
            if (sessionText == null || RecordingManager.ME == null)
                return;

            // Get current session ID
            currentSessionID = RecordingManager.ME.CurrentSessionID;
            sessionText.text = currentSessionID;
        }
    }
}