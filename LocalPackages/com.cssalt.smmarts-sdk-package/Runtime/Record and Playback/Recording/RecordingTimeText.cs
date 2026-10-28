using UnityEngine;
using TMPro;

namespace SMMARTS
{
    /// <summary>
    /// Updates a TextMeshPro component to display recording time in mm:ss format.
    /// Reads recording time from RecordingManager and formats as [00:00].
    /// </summary>
    [RequireComponent(typeof(TextMeshProUGUI))]
    public class RecordingTimeText : MonoBehaviour
    {
        [Header("Text Settings")]
        [Tooltip("TextMeshPro component to update with recording time")]
        public TextMeshProUGUI timeText;

        [Header("Debug Info")]
        [SerializeField] private float currentRecordingTime = 0f;
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
                Debug.LogError("RecordingTimeText: No TextMeshProUGUI component found!");
            }
        }

        void Update()
        {
            if (timeText == null || RecordingManager.ME == null)
                return;

            // Get current recording time
            currentRecordingTime = RecordingManager.ME.RecordingTime_s;

            // Convert to minutes and seconds
            int totalSeconds = Mathf.FloorToInt(currentRecordingTime);
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;

            // Format as [mm:ss]
            formattedTime = $"[{minutes:D2}:{seconds:D2}]";
            timeText.text = formattedTime;
        }
    }
}