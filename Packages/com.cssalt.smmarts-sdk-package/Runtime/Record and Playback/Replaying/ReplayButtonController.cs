using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

namespace SMMARTS
{
    /// <summary>
    /// Handles short press (show session menu) and long press (instant replay) for replay button.
    /// Short press: Shows session selection UI
    /// Long press (1.5s): Instantly replays last recorded session
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class ReplayButtonController : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        [Header("Press Settings")]
        [Tooltip("Duration in seconds for long press detection")]
        public float longPressDuration = 1.5f;

        public GameObject ScenarioSelectionMenu;
        public GameObject State_1_None;
        public GameObject State_3_Replaying;

        [Header("Debug Info")]
        [SerializeField] private bool isPressed = false;
        [SerializeField] private float pressStartTime = 0f;
        [SerializeField] private float currentPressDuration = 0f;
        [SerializeField] private bool longPressTriggered = false;

        private Button button;

        void Start()
        {
            button = GetComponent<Button>();
            if (button == null)
            {
                Debug.LogError("ReplayButtonController: No Button component found!");
            }
        }


        void OnEnable()
        {
            ScenarioSelectionMenu.SetActive(false);
        }


        void Update()
        {
            if (isPressed)
            {
                currentPressDuration = Time.time - pressStartTime;

                // Check if we've hit the long press threshold
                if (!longPressTriggered && currentPressDuration >= longPressDuration)
                {
                    longPressTriggered = true;
                    Debug.Log($"LONG PRESS triggered at {currentPressDuration:F2}s - Instant Replay!");
                    OnLongPress();
                }
            }
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (button != null && button.interactable)
            {
                isPressed = true;
                pressStartTime = Time.time;
                currentPressDuration = 0f;
                longPressTriggered = false;
                Debug.Log("Button press started");
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!isPressed) return;

            float pressDuration = Time.time - pressStartTime;
            isPressed = false;
            currentPressDuration = 0f;

            if (button != null && button.interactable)
            {
                // Only trigger short press if long press wasn't already triggered
                if (!longPressTriggered && pressDuration < longPressDuration)
                {
                    Debug.Log($"SHORT PRESS detected ({pressDuration:F2}s) - Show Session Menu!");
                    OnShortPress();
                }
            }

            longPressTriggered = false;
        }

        private void OnShortPress()
        {
            Debug.Log("*** SHORT PRESS ACTION: Show Session Selection Menu ***");
            // TODO: Show session selection UI

            // Trigger the button's OnClick events configured in inspector
            // button.onClick.Invoke();

            // 
            if (ScenarioSelectionMenu != null)
            {
                ScenarioSelectionMenu.SetActive(true);
            }

        }

        // long press is instant replay
        private void OnLongPress()
        {
            Debug.Log("*** LONG PRESS ACTION: Instant Replay Last Session ***");

            // Trigger the button's OnClick events configured in inspector
            // button.onClick.Invoke();
            State_1_None.SetActive(false);
            State_3_Replaying.SetActive(true);

            // Replay the last session which is, by default, pre-loaded into the replayer. 
            // this would have been on the OnClick() button but we want to do something different depending on the long/short click context.
            ReplayerManager.ME.ReplaySession();
        }
    }
}