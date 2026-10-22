using UnityEngine;
using TMPro;

namespace SMMARTS
{
    /// <summary>
    /// Updates a TextMeshPro component to display the current session ID from ReplayerManager.
    /// </summary>
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class ReplayingSessionIDText : MonoBehaviour
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
            if (sessionText == null || ReplayerManager.ME == null)
                return;

            // Get current session ID
            currentSessionID = ReplayerManager.ME.CurrentSessionID;
            sessionText.text = currentSessionID;
        }
    }
}