using UnityEngine;
using TMPro;

namespace SMMARTS
{
    /// <summary>
    /// Updates a TextMeshPro component to display replaying time in mm:ss format.
    /// Reads replaying time from ReplayerManager and formats as [00:00].
    /// </summary>
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class ReplayingTimeText : MonoBehaviour
    {
        [Header("Text Settings")]
        [Tooltip("TextMeshPro component to update with replaying time")]
        public TextMeshProUGUI timeText;

        [Header("Debug Info")]
        [SerializeField] private float currentReplayingTime = 0f;
        [SerializeField] private string formattedTime = "[00:00]";

        void Start()
        {
            // Auto-find TextMeshPro component if not assigned
            if (timeText == null)
            {
                timeText = GetComponent<TextMeshProUGUI>();
            }

            if (timeText == null)
            {
                Debug.LogError("ReplayingTimeText: No TextMeshProUGUI component found!");
            }
        }

        void Update()
        {
            if (timeText == null || ReplayerManager.ME == null)
                return;

            // Get current replaying time
            currentReplayingTime = ReplayerManager.ME.GetCurrentReplayTime();

            // Convert to minutes and seconds
            int totalSeconds = Mathf.FloorToInt(currentReplayingTime);
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;

            // Format as [mm:ss]
            formattedTime = $"[{minutes:D2}:{seconds:D2}]";
            timeText.text = formattedTime;
        }
    }
}