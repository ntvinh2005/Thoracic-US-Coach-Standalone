using UnityEngine;
#if !UNITY_WEBGL
using UnityEngine.Windows.Speech;
#endif
using System.Collections;

namespace SMMARTS
{
    /// <summary>
    /// Manages keyword recognition with replay capability for SMMARTS simulations.
    /// Provides polling interface for recognized phrases that seamlessly switches between live input and replay data.
    /// Singleton pattern ensures single speech recognition management instance.
    /// Keywords only, no dictation.
    /// </summary>
    public class KeywordRecognitionManager : MonoBehaviour
    {
        #region Singleton
        public static KeywordRecognitionManager ME { get; private set; }
        void Awake()
        {
            if (ME != null && ME != this)
            {
                Destroy(this);
                return;
            }
            ME = this;
            DontDestroyOnLoad(gameObject);
        }
        #endregion

        [Header("Speech Recognition Settings")]
        [Tooltip("Enable speech recognition processing")]
        public bool enableSpeech = true;

        [Tooltip("Keywords and phrases to recognize")]
        public string[] keywords = new string[] {
            "rotate caudad",
            "rotate cephalad",
            "up",
            "down",
            "left",
            "right"
        };

#if !UNITY_WEBGL
        [Tooltip("Confidence level for speech recognition")]
        public ConfidenceLevel confidence = ConfidenceLevel.Medium;
#endif

        [Header("Toast Settings")]
        [Tooltip("Duration to display speech toast messages")]
        public int toastDuration = 1; // seconds

        [Tooltip("Toast message prefix")]
        public string toastPrefix = "You said: ";

        [Header("Debug Info")]
        [SerializeField] private bool useReplayData = false;
        [SerializeField] private bool isReplayActive = false;
        [SerializeField] private string lastRecognizedPhrase = "";
        [SerializeField] private bool isCapturingEventMark = false;

        // Public polling interface for other scripts
        public string MostRecentPhrase { get; private set; } = "";
        public bool NewPhraseRecognized { get; private set; } = false;

#if !UNITY_WEBGL
        // Live speech recognition
        private KeywordRecognizer recognizer;
        private DictationRecognizer dictationRecognizer;
#endif
        private bool newPhraseThisFrame = false;

        void Start()
        {
            // Check if replayer is available at startup
            useReplayData = (KeywordRecognitionReplayer.ME != null);

#if !UNITY_WEBGL
            InitializeLiveSpeechRecognition();
            Debug.Log($"KeywordRecognitionManager initialized - Replay available: {useReplayData}");
#else
            Debug.Log("KeywordRecognitionManager initialized in WebGL mode - Speech recognition disabled");
#endif
        }

        void Update()
        {
            // Update replay status for debug display
            if (useReplayData && KeywordRecognitionReplayer.ME != null)
            {
                isReplayActive = KeywordRecognitionReplayer.ME.IsReplaying;
            }

            // Reset NewPhraseRecognized flag after one frame
            NewPhraseRecognized = newPhraseThisFrame;
            newPhraseThisFrame = false;
        }

#if !UNITY_WEBGL
        /// <summary>
        /// Initialize live speech recognition system
        /// </summary>
        private void InitializeLiveSpeechRecognition()
        {
            if (keywords != null && keywords.Length > 0)
            {
                recognizer = new KeywordRecognizer(keywords, confidence);
                recognizer.OnPhraseRecognized += OnLivePhraseRecognized;
                recognizer.Start();
                Debug.Log($"Live speech recognition started with {keywords.Length} keywords");
            }
            else
            {
                Debug.LogWarning("No keywords defined for speech recognition");
            }
        }

        /// <summary>
        /// Callback for live speech recognition events
        /// </summary>
        private void OnLivePhraseRecognized(PhraseRecognizedEventArgs args)
        {
            if (!enableSpeech) return;

            string phrase = args.text;
            ProcessRecognizedPhrase(phrase);

            // Notify recorder if it exists and is recording
            if (KeywordRecognitionRecorder.ME != null && KeywordRecognitionRecorder.ME.IsRecording)
            {
                KeywordRecognitionRecorder.ME.LogPhrase(phrase);
            }
        }

        #region Event Mark Dictation - Manual Control
        /// <summary>
        /// Manually start event mark dictation capture (call from UI or context menu)
        /// </summary>
        [ContextMenu("Start Event Mark Dictation")]
        public void ManualStartEventMarkDictation()
        {
            if (isCapturingEventMark)
            {
                Debug.LogWarning("Already capturing event mark");
                return;
            }

            StartEventMarkCapture();
        }

        /// <summary>
        /// Manually stop event mark dictation capture (call from UI or context menu)
        /// </summary>
        [ContextMenu("Stop Event Mark Dictation")]
        public void ManualStopEventMarkDictation()
        {
            if (!isCapturingEventMark)
            {
                Debug.LogWarning("Not currently capturing event mark");
                return;
            }

            Debug.Log("Manually stopping event mark dictation");

            // Manually stop dictation - this should trigger OnDictationComplete
            if (dictationRecognizer != null)
            {
                dictationRecognizer.Stop();
            }
        }

        /// <summary>
        /// Start capturing event mark dictation
        /// </summary>
        private void StartEventMarkCapture()
        {
            Debug.Log("Starting event mark dictation mode");
            PauseKeywordRecognition();

            // Start dictation after a brief delay to ensure phrase recognition system is released
            StartCoroutine(StartDictationDelayed());
        }

        private IEnumerator StartDictationDelayed()
        {
            yield return null;
            System.GC.Collect();
            yield return new WaitForSeconds(0.5f);

            Debug.Log($"About to start dictation - recognizer is null: {recognizer == null}");
            Debug.Log($"PhraseRecognitionSystem.Status: {PhraseRecognitionSystem.Status}");

            try
            {
                dictationRecognizer = new DictationRecognizer();
                dictationRecognizer.DictationResult += OnEventMarkCaptured;
                dictationRecognizer.DictationComplete += OnDictationComplete;
                dictationRecognizer.DictationError += OnDictationError;
                dictationRecognizer.DictationHypothesis += OnDictationHypothesis;

                dictationRecognizer.Start();
                Debug.Log("Dictation recognizer started successfully - speak your annotation");
                isCapturingEventMark = true;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to start dictation: {e.Message}");
                isCapturingEventMark = false;
                ResumeKeywordRecognition();
            }
        }

        private string lastDictationHypothesis = "";

        /// <summary>
        /// Callback for partial dictation results (while speaking)
        /// </summary>
        private void OnDictationHypothesis(string text)
        {
            Debug.Log($"Dictation hypothesis (partial): {text}");
            lastDictationHypothesis = text;
        }

        /// <summary>
        /// Callback when event mark dictation is recognized (final result)
        /// </summary>
        private void OnEventMarkCaptured(string text, ConfidenceLevel dictationConfidence)
        {
            Debug.Log($"Event mark captured (final): {text}");

            // Log to recorder if available and recording
            if (KeywordRecognitionRecorder.ME != null && KeywordRecognitionRecorder.ME.IsRecording)
            {
                KeywordRecognitionRecorder.ME.LogEventMark(text);
            }

            // Display toast for event mark
            string toastMessage = "Event Mark: <b>" + text + "</b>";
            Toast.Show(this, toastMessage, toastDuration, Toast.Type.MESSAGE, Toast.Gravity.BOTTOM);

            lastDictationHypothesis = ""; // Clear hypothesis since we got final result
        }

        /// <summary>
        /// Callback when dictation completes
        /// </summary>
        private void OnDictationComplete(DictationCompletionCause cause)
        {
            Debug.Log($"Dictation complete: {cause}");

            // If we have hypothesis text but no final result, use the hypothesis
            if (!string.IsNullOrEmpty(lastDictationHypothesis))
            {
                Debug.Log($"Using last hypothesis as fallback: {lastDictationHypothesis}");

                if (KeywordRecognitionRecorder.ME != null && KeywordRecognitionRecorder.ME.IsRecording)
                {
                    KeywordRecognitionRecorder.ME.LogEventMark(lastDictationHypothesis);
                }

                string toastMessage = "Event Mark: <b>" + lastDictationHypothesis + "</b>";
                Toast.Show(this, toastMessage, toastDuration, Toast.Type.MESSAGE, Toast.Gravity.BOTTOM);

                lastDictationHypothesis = "";
            }

            StopEventMarkCapture();
        }

        /// <summary>
        /// Callback for dictation errors
        /// </summary>
        private void OnDictationError(string error, int hresult)
        {
            Debug.LogError($"Dictation error: {error} (HRESULT: {hresult})");
            StopEventMarkCapture();
        }

        /// <summary>
        /// Stop event mark capture and return to keyword recognition
        /// </summary>
        private void StopEventMarkCapture()
        {
            if (!isCapturingEventMark) return;

            if (dictationRecognizer != null)
            {
                dictationRecognizer.DictationResult -= OnEventMarkCaptured;
                dictationRecognizer.DictationComplete -= OnDictationComplete;
                dictationRecognizer.DictationError -= OnDictationError;
                dictationRecognizer.DictationHypothesis -= OnDictationHypothesis;
                dictationRecognizer.Stop();
                dictationRecognizer.Dispose();
                dictationRecognizer = null;
            }

            isCapturingEventMark = false;
            ResumeKeywordRecognition();

            Debug.Log("Returned to keyword recognition mode");
        }

        /// <summary>
        /// Pause keyword recognition (for event mark capture)
        /// </summary>
        private void PauseKeywordRecognition()
        {
            if (recognizer != null)
            {
                recognizer.OnPhraseRecognized -= OnLivePhraseRecognized;
                if (recognizer.IsRunning)
                {
                    recognizer.Stop();
                }
                recognizer.Dispose();
                recognizer = null;
            }

            // Explicitly stop the phrase recognition system
            if (PhraseRecognitionSystem.isSupported)
            {
                PhraseRecognitionSystem.Shutdown();
            }
        }

        /// <summary>
        /// Resume keyword recognition (after event mark capture)
        /// </summary>
        private void ResumeKeywordRecognition()
        {
            // Restart the phrase recognition system if needed
            if (PhraseRecognitionSystem.isSupported && PhraseRecognitionSystem.Status != SpeechSystemStatus.Running)
            {
                PhraseRecognitionSystem.Restart();
            }

            if (recognizer == null)
            {
                InitializeLiveSpeechRecognition();
            }
            else if (!recognizer.IsRunning)
            {
                recognizer.Start();
            }
        }
        #endregion

        /// <summary>
        /// Manually add keywords at runtime (restarts recognizer each time)
        /// </summary>
        public void AddKeyword(string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return;

            // Add to keywords array
            System.Array.Resize(ref keywords, keywords.Length + 1);
            keywords[keywords.Length - 1] = keyword;

            // Restart recognizer with new keywords if running
            if (recognizer != null && recognizer.IsRunning)
            {
                recognizer.OnPhraseRecognized -= OnLivePhraseRecognized;
                recognizer.Stop();
                recognizer.Dispose();
                InitializeLiveSpeechRecognition();
                Debug.Log($"Added keyword: {keyword} - Recognizer restarted");
            }
        }

        /// <summary>
        /// Add multiple keywords at runtime efficiently (restarts recognizer only once)
        /// </summary>
        public void AddKeywords(string[] newKeywords)
        {
            if (newKeywords == null || newKeywords.Length == 0)
            {
                Debug.LogWarning("No keywords provided to add");
                return;
            }

            // Store original length
            int originalLength = keywords.Length;

            // Resize array to fit new keywords
            System.Array.Resize(ref keywords, keywords.Length + newKeywords.Length);

            // Copy new keywords into array
            for (int i = 0; i < newKeywords.Length; i++)
            {
                keywords[originalLength + i] = newKeywords[i];
            }

            // Restart recognizer with new keywords if running
            if (recognizer != null && recognizer.IsRunning)
            {
                recognizer.OnPhraseRecognized -= OnLivePhraseRecognized;
                recognizer.Stop();
                recognizer.Dispose();
                InitializeLiveSpeechRecognition();
                Debug.Log($"Added {newKeywords.Length} keywords - Recognizer restarted with {keywords.Length} total keywords");
            }
            else
            {
                Debug.Log($"Added {newKeywords.Length} keywords - Total: {keywords.Length}");
            }
        }

        /// <summary>
        /// Clean up all recognizers
        /// </summary>
        private void CleanupRecognizers()
        {
            // Clean up keyword recognizer
            if (recognizer != null && recognizer.IsRunning)
            {
                recognizer.OnPhraseRecognized -= OnLivePhraseRecognized;
                recognizer.Stop();
                recognizer.Dispose();
            }

            // Clean up dictation recognizer if active
            if (dictationRecognizer != null)
            {
                dictationRecognizer.DictationResult -= OnEventMarkCaptured;
                dictationRecognizer.DictationComplete -= OnDictationComplete;
                dictationRecognizer.DictationError -= OnDictationError;
                dictationRecognizer.DictationHypothesis -= OnDictationHypothesis;
                dictationRecognizer.Stop();
                dictationRecognizer.Dispose();
            }
        }
#else // UNITY_WEBGL stubs
        // Stub methods for WebGL - maintain API compatibility
        [ContextMenu("Start Event Mark Dictation")]
        public void ManualStartEventMarkDictation()
        {
            Debug.LogWarning("Speech recognition not supported on WebGL");
        }

        [ContextMenu("Stop Event Mark Dictation")]
        public void ManualStopEventMarkDictation()
        {
            Debug.LogWarning("Speech recognition not supported on WebGL");
        }

        public void AddKeyword(string keyword)
        {
            Debug.LogWarning("Speech recognition not supported on WebGL");
        }

        public void AddKeywords(string[] newKeywords)
        {
            Debug.LogWarning("Speech recognition not supported on WebGL");
        }

        private void CleanupRecognizers()
        {
            // Nothing to clean up on WebGL
        }
#endif

        /// <summary>
        /// Process a recognized phrase (called by live recognition or replayer)
        /// </summary>
        /// <param name="phrase">The recognized phrase</param>
        public void ProcessRecognizedPhrase(string phrase)
        {
            if (!enableSpeech) return;

            MostRecentPhrase = phrase;
            newPhraseThisFrame = true;
            lastRecognizedPhrase = phrase;

            // Display toast notification
            string toastMessage = toastPrefix + "<b>" + phrase + "</b>";
            Toast.Show(this, toastMessage, toastDuration, Toast.Type.MESSAGE, Toast.Gravity.BOTTOM);

            Debug.Log($"Speech recognized: {phrase}");
        }

        /// <summary>
        /// Check if replayer is currently providing speech data
        /// </summary>
        public bool IsUsingReplayData => useReplayData && KeywordRecognitionReplayer.ME != null && KeywordRecognitionReplayer.ME.IsReplaying;

        /// <summary>
        /// Refresh replayer availability (call if replayer components are added/removed at runtime)
        /// </summary>
        public void RefreshReplayerAvailability()
        {
            useReplayData = (KeywordRecognitionReplayer.ME != null);

#if !UNITY_WEBGL
            // Start/stop live recognition based on replayer availability
            if (useReplayData && recognizer != null && recognizer.IsRunning)
            {
                recognizer.Stop();
                Debug.Log("Stopped live speech recognition - using replay data");
            }
            else if (!useReplayData && recognizer != null && !recognizer.IsRunning)
            {
                recognizer.Start();
                Debug.Log("Started live speech recognition");
            }
#endif
            Debug.Log($"Replayer availability refreshed: {useReplayData}");
        }

        /// <summary>
        /// Get current input source for debugging
        /// </summary>
        public string GetCurrentInputSource()
        {
#if UNITY_WEBGL
            return "WebGL (Speech Disabled)";
#else
            if (isCapturingEventMark)
                return "Event Mark Dictation";
            else if (IsUsingReplayData)
                return "Replay Data";
            else
                return "Live Speech Recognition";
#endif
        }

        void OnDestroy()
        {
            CleanupRecognizers();
        }

        void OnApplicationQuit()
        {
            CleanupRecognizers();
        }

        /// <summary>
        /// Context menu helper for testing
        /// </summary>
        [ContextMenu("Show Speech Status")]
        public void ShowSpeechStatus()
        {
            Debug.Log($"KeywordRecognitionManager Status:");
            Debug.Log($"- Speech Enabled: {enableSpeech}");
            Debug.Log($"- Replay Available: {useReplayData}");
            Debug.Log($"- Currently Using: {GetCurrentInputSource()}");
            Debug.Log($"- Keywords Count: {keywords.Length}");
            Debug.Log($"- Last Recognized: {lastRecognizedPhrase}");
            Debug.Log($"- Most Recent Phrase: {MostRecentPhrase}");
            Debug.Log($"- Capturing Event Mark: {isCapturingEventMark}");
        }
    }
}