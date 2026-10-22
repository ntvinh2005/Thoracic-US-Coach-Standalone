using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

namespace SMMARTS
{
    /// <summary>
    /// UI controller for replay scrubbing and play/pause.
    /// Binds a Slider (0..1 = session position) and a play/pause Button to ReplayerManager.
    ///
    /// - Dragging the slider PAUSES playback while you scrub and RESUMES on release
    ///   (only if it was playing when you grabbed the handle), so it never drifts while dragging.
    /// - Play/pause state is shown by toggling two GameObjects: playStateObject is active while
    ///   playing, pauseStateObject is active while paused/stopped.
    ///
    /// Place on a GameObject in the persistent replay panel (e.g. State_3_Replaying),
    /// NOT the session-selection menu (whose CloseMenu() disables its hierarchy).
    /// </summary>
    public class ReplayScrubber : MonoBehaviour
    {
        [Header("UI References")]
        [Tooltip("Slider representing replay position (0 = start, 1 = end)")]
        public Slider scrubberSlider;

        [Tooltip("Button that toggles play/pause")]
        public Button playPauseButton;

        [Header("Play/Pause State Indicators")]
        [Tooltip("GameObject shown while replay is PLAYING")]
        public GameObject playStateObject;

        [Tooltip("GameObject shown while replay is PAUSED or stopped")]
        public GameObject pauseStateObject;

        [Header("Optional")]
        [Tooltip("Optional text showing 'current / total' time as mm:ss")]
        public TMP_Text timeText;

        private bool isScrubbing = false;
        private bool wasPlayingBeforeScrub = false;

        void Start()
        {
            if (scrubberSlider != null)
            {
                scrubberSlider.minValue = 0f;
                scrubberSlider.maxValue = 1f;
                scrubberSlider.wholeNumbers = false;
                scrubberSlider.onValueChanged.AddListener(OnScrub);

                // Auto-attach a proxy to the slider so we know when the user grabs / releases
                // the handle - no extra manual wiring needed in the scene.
                ScrubberDragProxy proxy = scrubberSlider.gameObject.GetComponent<ScrubberDragProxy>();
                if (proxy == null) proxy = scrubberSlider.gameObject.AddComponent<ScrubberDragProxy>();
                proxy.owner = this;
            }
            else
            {
                Debug.LogWarning("ReplayScrubber: no Slider assigned");
            }

            if (playPauseButton != null)
                playPauseButton.onClick.AddListener(TogglePlayPause);
        }

        void OnDestroy()
        {
            if (scrubberSlider != null)
                scrubberSlider.onValueChanged.RemoveListener(OnScrub);
            if (playPauseButton != null)
                playPauseButton.onClick.RemoveListener(TogglePlayPause);
        }

        void Update()
        {
            ReplayerManager rm = ReplayerManager.ME;
            if (rm == null) return;

            // Follow playback with the slider unless the user is actively scrubbing.
            // SetValueWithoutNotify avoids re-triggering OnScrub (no seek feedback loop).
            if (scrubberSlider != null && rm.IsReplaying && !isScrubbing)
                scrubberSlider.SetValueWithoutNotify(rm.GetReplayProgress());

            UpdateIndicators(rm);
        }

        /// <summary>Slider value changed by the user - seek to that position.</summary>
        public void OnScrub(float normalized)
        {
            ReplayerManager rm = ReplayerManager.ME;
            if (rm == null) return;
            rm.SeekToNormalized(normalized);
        }

        /// <summary>Called by the drag proxy when the user grabs the slider handle.</summary>
        public void BeginScrub()
        {
            ReplayerManager rm = ReplayerManager.ME;
            if (rm == null) return;

            wasPlayingBeforeScrub = rm.IsReplaying && !rm.IsPaused;
            if (wasPlayingBeforeScrub) rm.PauseReplay(); // freeze so playback doesn't drift while dragging
            isScrubbing = true;
        }

        /// <summary>Called by the drag proxy when the user releases the slider handle.</summary>
        public void EndScrub()
        {
            isScrubbing = false;

            ReplayerManager rm = ReplayerManager.ME;
            if (rm == null) return;

            if (wasPlayingBeforeScrub && rm.IsReplaying && rm.IsPaused)
                rm.ResumeReplay();
            wasPlayingBeforeScrub = false;
        }

        /// <summary>Toggle play/pause (wire this to a single play/pause button).</summary>
        public void TogglePlayPause()
        {
            ReplayerManager rm = ReplayerManager.ME;
            if (rm == null) return;

            if (!rm.IsReplaying) rm.StartReplay();
            else if (rm.IsPaused) rm.ResumeReplay();
            else rm.PauseReplay();
        }

        /// <summary>Explicit play/resume (for a dedicated Play button).</summary>
        public void Play()
        {
            ReplayerManager rm = ReplayerManager.ME;
            if (rm == null) return;

            if (!rm.IsReplaying) rm.StartReplay();
            else if (rm.IsPaused) rm.ResumeReplay();
        }

        /// <summary>Explicit pause (for a dedicated Pause button).</summary>
        public void Pause()
        {
            ReplayerManager rm = ReplayerManager.ME;
            if (rm == null) return;

            if (rm.IsReplaying && !rm.IsPaused) rm.PauseReplay();
        }

        private void UpdateIndicators(ReplayerManager rm)
        {
            bool playing = rm.IsReplaying && !rm.IsPaused;

            if (playStateObject != null) playStateObject.SetActive(playing);
            if (pauseStateObject != null) pauseStateObject.SetActive(!playing);

            if (timeText != null)
                timeText.text = $"{Format(rm.GetCurrentReplayTime())} / {Format(rm.GetSessionDuration())}";
        }

        private static string Format(float seconds)
        {
            if (seconds < 0f) seconds = 0f;
            int total = Mathf.FloorToInt(seconds);
            return $"{total / 60:D2}:{total % 60:D2}";
        }
    }

    /// <summary>
    /// Helper auto-attached to the Slider GameObject by ReplayScrubber to detect when the
    /// user grabs / releases the handle (drives pause-while-scrubbing). Added at runtime,
    /// so it does not need its own file-named script asset.
    /// </summary>
    public class ScrubberDragProxy : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        [System.NonSerialized] public ReplayScrubber owner;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (owner != null) owner.BeginScrub();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (owner != null) owner.EndScrub();
        }
    }
}
