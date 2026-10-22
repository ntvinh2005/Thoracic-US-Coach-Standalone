using UnityEngine;
using System.Collections;
using System.Collections.Generic;

namespace SMMARTS
{
    [System.Serializable]
    public class C_ArmVoiceCommand
    {
        [Tooltip("Label for inspector organization")]
        public string name;

        [Tooltip("All acceptable keywords for this command")]
        public string[] keywords;

        [Tooltip("Primary widget to control")]
        public C_ArmDragControl widget;

        [Tooltip("Brake handle to unlock if needed")]
        public C_ArmBrakeHandle handle;

        [Header("Duration-based (default)")]
        [Tooltip("Duration to hold widget active (seconds)")]
        public float holdDuration = 1f;

        [Header("Target-based (optional)")]
        [Tooltip("Use target value instead of hold duration")]
        public bool useTargetValue = false;

        [Tooltip("Target orbit rotation value (only if useTargetValue is true)")]
        public float targetValue = 0f;

        [Tooltip("Opposite direction widget (only if useTargetValue is true)")]
        public C_ArmDragControl oppositeWidget;
    }

    public class C_ArmVoiceControl : MonoBehaviour
    {
        [Header("Wake Word for the Radiation Technologist")]
        [Tooltip("Wake phrases like 'rad tech' to start listening for commands")]
        public string[] wakePhrases;

        [Tooltip("Seconds to listen for commands after wake phrase")]
        public float wakeDuration = 5f;

        [Tooltip("Progress indicator: 1.0 = just woke, 0.0 = about to sleep (for UI)")]
        [Range(0, 1)]
        public float wakeFractionLeft = 0f;

        [Header("Wake Progress UI")]
        [Tooltip("Container for wake progress bar (activates when awake)")]
        public GameObject wakeProgressIndicator;

        [Tooltip("Progress bar that shrinks as wake time runs out")]
        public RectTransform wakeProgressBar;

        [Header("Voice Command Mappings")]
        [Tooltip("Assign widgets for each voice command - keep pairs together")]
        public C_ArmVoiceCommand[] voiceCommands;

        // Public interface for external scripts (like C_ArmPositioner)
        public C_ArmVoiceCommand CurrentCommand { get; private set; }
        public bool NewCommandThisFrame { get; private set; }

        // Wake state tracking
        private bool isAwake = false;
        private float wakeTimer = 0f;

        // Track active coroutines per widget to allow stopping/restarting
        private Dictionary<C_ArmDragControl, Coroutine> activeMovementCommands;

        // UI tracking
        private float initialProgressBarWidth;

        // Dynamic target command reference
        private C_ArmVoiceCommand obliqueMarkCommand;

        void Start()
        {
            // Check if KeywordRecognitionManager is available
            if (KeywordRecognitionManager.ME == null)
            {
                Debug.LogWarning("C_ArmVoiceControl needs a KeywordRecognitionManager to function. Disabling this component.");
                this.enabled = false;
                return;
            }

            activeMovementCommands = new Dictionary<C_ArmDragControl, Coroutine>();

            // Store initial progress bar width
            if (wakeProgressBar != null)
            {
                initialProgressBarWidth = wakeProgressBar.rect.width;
            }

            // Hide progress indicator at start
            if (wakeProgressIndicator != null)
            {
                wakeProgressIndicator.SetActive(false);
            }

            // Extract wake phrases and command keywords separately
            List<string> allKeywords = new List<string>();

            // Add wake phrases
            if (wakePhrases != null)
            {
                foreach (var wakePhrase in wakePhrases)
                {
                    if (!string.IsNullOrEmpty(wakePhrase))
                    {
                        allKeywords.Add(wakePhrase);
                    }
                }
            }

            // Add command keywords
            foreach (var cmd in voiceCommands)
            {
                if (cmd.keywords != null)
                {
                    foreach (var keyword in cmd.keywords)
                    {
                        if (!string.IsNullOrEmpty(keyword))
                        {
                            allKeywords.Add(keyword);
                        }
                    }
                }
            }

            // Add image and video commands for the Fluoro Update Control
            allKeywords.Add("xray");
            allKeywords.Add("shot");
            allKeywords.Add("go live");
            allKeywords.Add("stop");
            allKeywords.Add("Stop");
            allKeywords.Add("mark oblique");
            allKeywords.Add("Mark Oblique");

            // Register keywords with recognition manager
            KeywordRecognitionManager.ME.AddKeywords(allKeywords.ToArray());
            Debug.Log($"C_ArmVoiceControl registered {allKeywords.Count} keywords");

            // Cache reference to dynamic target command
            foreach (var cmd in voiceCommands)
            {
                if (cmd.name == "Go to oblique mark")
                {
                    obliqueMarkCommand = cmd;
                    break;
                }
            }

            if (obliqueMarkCommand == null)
            {
                Debug.LogWarning("'Go to oblique mark' command not found in voiceCommands array");
            }
        }

        // this is a quick, dirty demo of verbally setting the tape mark to a position
        // this only works with this one piece of tape and the "Mark Oblique" command!
        [SerializeField] private GameObject ObliqueTapeMark;
        [SerializeField] private GameObject PrePlacedObliqueTapeMark;

        void Update()
        {
            // Reset flag each frame
            NewCommandThisFrame = false;

            // Update wake timer and fraction
            if (isAwake)
            {
                wakeTimer -= Time.deltaTime;
                wakeFractionLeft = Mathf.Clamp01(wakeTimer / wakeDuration);

                // Update progress bar UI
                if (wakeProgressBar != null)
                {
                    wakeProgressBar.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, initialProgressBarWidth * wakeFractionLeft);
                }

                // Check if timer expired
                if (wakeTimer <= 0f)
                {
                    GoToSleep();
                }
            }
            else
            {
                wakeFractionLeft = 0f;
            }

            // Poll for new voice commands
            if (KeywordRecognitionManager.ME != null && KeywordRecognitionManager.ME.NewPhraseRecognized)
            {
                string phrase = KeywordRecognitionManager.ME.MostRecentPhrase;

                // If sleeping, only check for wake phrases
                if (!isAwake)
                {
                    if (wakePhrases != null)
                    {
                        foreach (var wakePhrase in wakePhrases)
                        {
                            if (phrase == wakePhrase)
                            {
                                WakeUp();
                                return;
                            }
                        }
                    }
                    return; // Ignore all other commands when sleeping
                }

                // If awake, process commands

                // Handle mark oblique command
                if (phrase == "mark oblique" || phrase == "Mark Oblique")
                {
                    if (obliqueMarkCommand != null)
                    {
                        obliqueMarkCommand.targetValue = C_ArmPositioner.ME.orbitRotation;
                        Debug.Log($"Marked oblique at {obliqueMarkCommand.targetValue}°");
                    }
                    else
                    {
                        Debug.LogWarning("Cannot mark oblique - 'Go to oblique mark' command not configured");
                    }

                    // quick and dirty - place a tape on the arm at the pre-positioned place. 
                    ObliqueTapeMark.transform.position = PrePlacedObliqueTapeMark.transform.position;
                    ObliqueTapeMark.transform.rotation = PrePlacedObliqueTapeMark.transform.rotation;

                    GoToSleep();
                    return;
                }

                // Handle image control as a special case
                if (phrase == "shot" || phrase == "x-ray" || phrase == "xray")
                {
                    FluoroUpdateControl.ME.TakeSingleImage();
                    GoToSleep();
                    return;
                }

                // Handle video control as a special case
                if (phrase == "go live")
                {
                    FluoroUpdateControl.ME.TurnOnContinousFluoro();
                    GoToSleep();
                    return;
                }

                // Handle stop command as special case
                if (phrase == "stop" || phrase == "Stop")
                {
                    FluoroUpdateControl.ME.TurnOffContinousFluoro();
                    StopAllCommands();
                    GoToSleep();
                    return;
                }

                // Simple loop to find matching command
                foreach (var cmd in voiceCommands)
                {
                    // Check if phrase matches any keyword in this command
                    bool matchFound = false;
                    if (cmd.keywords != null)
                    {
                        foreach (var keyword in cmd.keywords)
                        {
                            if (keyword == phrase)
                            {
                                matchFound = true;
                                break;
                            }
                        }
                    }

                    if (matchFound && cmd.widget != null)
                    {
                        // If widget is inactive, activate brake handle first
                        if (!cmd.widget.gameObject.activeInHierarchy && cmd.handle != null)
                        {
                            cmd.handle.OnMouseDown();
                        }

                        // Set current command for external scripts
                        CurrentCommand = cmd;
                        NewCommandThisFrame = true;

                        // Stop any existing command on this widget
                        if (activeMovementCommands.ContainsKey(cmd.widget))
                        {
                            if (activeMovementCommands[cmd.widget] != null)
                            {
                                StopCoroutine(activeMovementCommands[cmd.widget]);
                            }
                            cmd.widget.OnMouseUp();
                            activeMovementCommands.Remove(cmd.widget);
                        }

                        // Start new command - choose coroutine type based on command settings
                        Coroutine routine;
                        if (cmd.useTargetValue)
                        {
                            routine = StartCoroutine(ExecuteCommandToTarget(cmd));
                        }
                        else
                        {
                            routine = StartCoroutine(ExecuteCommand(cmd));
                        }
                        activeMovementCommands[cmd.widget] = routine;

                        // Since using voice commands, don't move camera close to UI arrows
                        C_ArmPositioner.ME.MoveCameraToTechsViewpoint = false;

                        // Go back to sleep after executing command
                        GoToSleep();

                        break; // Found match, stop searching
                    }
                }
            }
        }

        /// <summary>
        /// Wake up and start listening for commands
        /// </summary>
        private void WakeUp()
        {
            isAwake = true;
            wakeTimer = wakeDuration;
            wakeFractionLeft = 1f;

            // Show progress indicator
            if (wakeProgressIndicator != null)
            {
                wakeProgressIndicator.SetActive(true);
            }

            Debug.Log("C_ArmVoiceControl is now awake and listening");
        }

        /// <summary>
        /// Go to sleep and only listen for wake phrases
        /// </summary>
        private void GoToSleep()
        {
            isAwake = false;
            wakeTimer = 0f;
            wakeFractionLeft = 0f;

            // Hide progress indicator
            if (wakeProgressIndicator != null)
            {
                wakeProgressIndicator.SetActive(false);
            }

            Debug.Log("C_ArmVoiceControl is now sleeping");
        }

        /// <summary>
        /// Stop all active voice commands immediately
        /// </summary>
        public void StopAllCommands()
        {
            // Stop all active coroutines and release widgets
            foreach (var kvp in activeMovementCommands)
            {
                StopCoroutine(kvp.Value);
                kvp.Key.OnMouseUp();
            }
            activeMovementCommands.Clear();
            Debug.Log("All voice commands stopped");
        }

        /// <summary>
        /// Execute command for fixed duration (original behavior)
        /// </summary>
        private IEnumerator ExecuteCommand(C_ArmVoiceCommand command)
        {
            // Activate widget
            command.widget.OnMouseDown();

            // Hold for specified duration
            yield return new WaitForSeconds(command.holdDuration);

            // Release widget
            command.widget.OnMouseUp();

            // Remove from active tracking
            activeMovementCommands.Remove(command.widget);
        }

        /// <summary>
        /// Execute command until target value is reached (new behavior for oblique/AP commands)
        /// </summary>
        private IEnumerator ExecuteCommandToTarget(C_ArmVoiceCommand command)
        {
            // Safety counter
            int frameCount = 0;
            int maxFrames = 1000;
            float tolerance = 1f; // degrees

            // Determine which widget to use based on current vs target
            float currentRotation = C_ArmPositioner.ME.orbitRotation;
            float targetRotation = command.targetValue;

            C_ArmDragControl widgetToUse;
            if (Mathf.Abs(currentRotation - targetRotation) <= tolerance)
            {
                // Already at target
                Debug.Log($"Already at target rotation {targetRotation}°");
                yield break;
            }
            else if (targetRotation > currentRotation)
            {
                widgetToUse = command.widget; // assume widget rotates positive
            }
            else // targetRotation < currentRotation
            {
                widgetToUse = command.oppositeWidget; // opposite rotates negative
            }

            // Check that we have a valid widget to use
            if (widgetToUse == null)
            {
                Debug.LogError($"No valid widget to reach target {targetRotation}°");
                yield break;
            }

            // Activate widget
            widgetToUse.OnMouseDown();

            // Wait until target reached or max frames
            while (frameCount < maxFrames)
            {
                currentRotation = C_ArmPositioner.ME.orbitRotation;

                // Check if within tolerance
                if (Mathf.Abs(currentRotation - targetRotation) <= tolerance)
                {
                    Debug.Log($"Reached target rotation {targetRotation}° (actual: {currentRotation}°)");
                    break;
                }

                frameCount++;
                yield return null;
            }

            // Release widget
            widgetToUse.OnMouseUp();

            // Log if we hit max frames
            if (frameCount >= maxFrames)
            {
                Debug.LogWarning($"Target command reached max frames ({maxFrames}) without hitting target {targetRotation}° (stuck at {currentRotation}°)");
            }

            // Remove from active tracking
            activeMovementCommands.Remove(command.widget);
        }
    }
}