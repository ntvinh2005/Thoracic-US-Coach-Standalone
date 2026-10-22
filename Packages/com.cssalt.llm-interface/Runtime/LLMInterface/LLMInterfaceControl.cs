// LLMInterfaceControl.cs
// Push-to-talk voice control for ContextCommander. It pulls its LLM context
// from ContextCommander.BuildContext() each turn and dispatches the model's
// bracket commands to ContextCommander.Move/Rotate/MoveToward.
//
//   mic -> WAV -> Whisper -> chat completion -> bracket commands -> ContextCommander
//
// Drop it on any GameObject, assign (or auto-find) a ContextCommander, set the
// LiteLLM API key (Inspector or Resources/llm_api_key.txt). Works in Editor,
// standalone, and WebGL.

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;

namespace CSSALT.LLMInterface
{
    public class LLMInterfaceControl : MonoBehaviour
    {
        [Header("Latency Metrics")]
        // [Tooltip("READ-ONLY. Seconds from releasing push-to-talk to the command being dispatched (the probe starting to move). " +
                 // "This is the delay you actually feel — ASR + LLM plus mic-read, encoding and network overhead.")]
        [SerializeField] float releaseToMoveSeconds;
        // [Tooltip("READ-ONLY. Speech recognition (Whisper) request time.")]
        [SerializeField] float asrSeconds;
        // [Tooltip("READ-ONLY. Language model (chat completion) request time. ASR + LLM is most of release-to-move.")]
        [SerializeField] float llmSeconds;
        // [Tooltip("READ-ONLY. Text-to-speech time — the spoken reply, which happens AFTER the motion, so it's the least critical leg.")]
        [SerializeField] float ttsSeconds;

        [Header("Target")]
        // [Tooltip("The ContextCommander this drives. Auto-found in Awake if left empty.")]
        public ContextCommander commander;

        [Header("LiteLLM proxy")]
        public string liteLLMBaseUrl = "https://api.ai.it.ufl.edu/v1";
        // [Tooltip("Bearer token. If blank, loaded from Resources/llm_api_key.txt.")]
        public string liteLLMApiKey = "";
        public string llmModel = "gpt-oss-120b";
        public string whisperModel = "whisper-large-v3";
        public string reasoningEffort = "medium";
        public int maxTokens = 4096;
        public int requestTimeoutSeconds = 30;

        [Header("System prompt")]
        // [Tooltip("The system prompt the model is given. Defaults to the bundled LLMInterfacePrompt " +
                 // "(auto-filled here in the Editor so it's visible, not a confusing 'None'). Assign your " +
                 // "own TextAsset to use a different application's prompt with this same package.")]
        public TextAsset systemPromptAsset;

        [Header("Push-to-talk")]
        // [Tooltip("Hold-to-talk key. Requires Project Settings > Player > Active Input Handling to be " +
                 // "'Input Manager (Old)' or 'Both' — this package uses the legacy Input API and takes no " +
                 // "package dependencies. Under 'Input System Package (New)' this key is inactive; use " +
                 // "Push To Talk Button, or drive StartRecording()/StopAndSend() yourself.")]
        public KeyCode pushToTalkKey = KeyCode.V;
        // [Tooltip("Optional UI Button to also act as PTT (hold = record). Works under any Active Input " +
                 // "Handling setting, since it routes through the EventSystem rather than UnityEngine.Input.")]
        public Button pushToTalkButton;
        public int maxRecordSeconds = 15;
        public string microphoneDevice = "";
        public int requestedSampleRate = 16000;

        [Header("Default magnitudes (when a command omits the amount)")]
        public float defaultMoveMm = 10f;
        public float defaultRotateDeg = 15f;
        // [Tooltip("When a command gives no duration, glide over this many seconds instead of snapping. " +
                 // "A snap is ambiguous — e.g. a marker jumping 3→9 o'clock gives no sign the probe turned. " +
                 // "0 = snap. An explicit '2s' in a command always overrides this.")]
        public float defaultMotionSeconds = 1f;

        [Header("Voice responses (TTS)")]
        public bool voiceResponsesEnabled = true;
        // [Tooltip("Speak action confirmations too (e.g. \"swept the probe 20 mm cephalad\"), not just [Say] replies. " +
                 // "Off = only spoken questions/prose are voiced; successful commands are silent (chat-log only).")]
        public bool speakActionSummaries = true;
        public TTSProvider ttsProvider = TTSProvider.Deepgram;
        // [Tooltip("Deepgram key. Blank = load Resources/deepgram_api_key.txt.")]
        public string deepgramApiKey = "";
        public string deepgramVoice = "aura-2-hyperion-en";

        [Header("Conversation")]
        // [Tooltip("User/assistant turns sent back to the LLM as context.")]
        public int historyTurns = 10;

        [Header("Overlay (built-in quick UI)")]
        // [Tooltip("The zero-setup IMGUI overlay. Turn OFF and use the events below to drive your own Canvas UI.")]
        public bool showOverlay = true;
        public int chatLogEntries = 12;

        // [Tooltip("Start with the chat log collapsed to just its +/- button. The status bar is unaffected. " +
                 // "Turn this ON in a project where the log would sit over something that matters — a scanner " +
                 // "display, a procedure view — so it ships out of the way and can still be opened to check what " +
                 // "the model heard. (showOverlay above removes the overlay entirely instead.)")]
        public bool startChatCollapsed = false;

        [System.Serializable] public class StringEvent : UnityEngine.Events.UnityEvent<string> { }

        [Header("UI hooks (bind your own Canvas / TMP UI here)")]
        // [Tooltip("Fires on every status change (recording / processing / result / errors).")]
        public StringEvent onStatus;
        // [Tooltip("Fires with the user's transcribed words.")]
        public StringEvent onUserMessage;
        // [Tooltip("Fires with each assistant reply or action summary.")]
        public StringEvent onAssistantMessage;

        /// <summary>
        /// Optional after-the-fact feedback hook. After a motion command runs (and
        /// one frame passes so any surface constraint has settled), this is called
        /// for a short note about the probe's contact with the patient — e.g.
        /// "pressing at the tissue limit" — or "" for nothing to report. The note
        /// is spoken/logged AND appended to the LLM history, so the model learns
        /// when reality stopped short of what it commanded. The package knows
        /// nothing about colliders or skin: a host sets this at runtime.
        /// </summary>
        public System.Func<string> contactStatusProvider;

        [Header("Debug")]
        public bool verboseLogging = true;

        // -------- internal --------
        LiteLLMClient _client;
        VoiceTTS _tts;
        string _staticPrompt;
        readonly List<ChatTurn> _history = new List<ChatTurn>();
        readonly List<(bool user, string text)> _chat = new List<(bool, string)>();
        string _status = "idle";
        float _pttReleaseTime;   // realtime when push-to-talk was released, for releaseToMoveSeconds
        string _heard = "";
        bool _isRecording;
        float _recordStartTime;

#if !UNITY_WEBGL || UNITY_EDITOR
        AudioClip _recordingClip;
        int _recordStartSamplePosition;
#else
        int _webglSampleRate = 48000;
#endif

#if UNITY_EDITOR
        // Editor convenience: surface the real default in the Inspector instead of a
        // misleading "None". If no prompt is assigned, point the field at the bundled
        // Resources/LLMInterfacePrompt so it's obvious a prompt IS in use and where to
        // swap it. Reset covers a freshly added component; OnValidate covers ones that
        // already exist with the field empty. Runtime still falls back on its own.
        void Reset()      => AutoFillDefaultPrompt();
        void OnValidate() => AutoFillDefaultPrompt();
        void AutoFillDefaultPrompt()
        {
            if (systemPromptAsset == null)
                systemPromptAsset = Resources.Load<TextAsset>("LLMInterfacePrompt");
        }
#endif

        void Awake()
        {
            _chatCollapsed = startChatCollapsed;   // runtime state; the field is only the starting position

            if (commander == null)
#if UNITY_2022_2_OR_NEWER
                commander = FindFirstObjectByType<ContextCommander>();
#else
                commander = FindObjectOfType<ContextCommander>();
#endif

            _client = new LiteLLMClient
            {
                BaseUrl = liteLLMBaseUrl,
                ApiKey = ResolveKey(liteLLMApiKey, "llm_api_key", warn: true),
                WhisperModel = whisperModel,
                LlmModel = llmModel,
                ReasoningEffort = reasoningEffort,
                MaxTokens = maxTokens,
                TimeoutSeconds = requestTimeoutSeconds,
                Verbose = verboseLogging,
            };

            _tts = new VoiceTTS(this)
            {
                Provider = ttsProvider,
                Enabled = voiceResponsesEnabled,
                Verbose = verboseLogging,
                DeepgramApiKey = ResolveKey(deepgramApiKey, "deepgram_api_key", warn: false),
                DeepgramVoice = deepgramVoice,
            };
            _tts.OnSpoken = s => ttsSeconds = s;   // TTS leg, filled when speech starts playing

            _staticPrompt = LoadStaticPrompt();
        }

        void Start()
        {
            if (pushToTalkButton != null)
                pushToTalkButton.gameObject.AddComponent<PTTButtonTrigger>().Owner = this;

#if !ENABLE_LEGACY_INPUT_MANAGER
            // Active Input Handling is "Input System Package (New)", so
            // UnityEngine.Input is unavailable and the PTT key cannot be polled.
            // The button and the public StartRecording/StopAndSend entry points
            // are unaffected — only warn if there is no other way in.
            if (pushToTalkButton == null)
                Debug.LogWarning(
                    $"[LLMInterface] Push-to-talk key '{pushToTalkKey}' is INACTIVE: Active Input Handling is set to " +
                    "'Input System Package (New)'. Fix by setting Project Settings > Player > Active Input Handling " +
                    "to 'Both' (or 'Input Manager (Old)') and restarting the Editor — or assign Push To Talk Button, " +
                    "or call StartRecording()/StopAndSend() from your own input code.");
#endif
        }

        string ResolveKey(string inspectorValue, string resourceName, bool warn)
        {
            if (!string.IsNullOrEmpty(inspectorValue)) return inspectorValue.Trim();
            var asset = Resources.Load<TextAsset>(resourceName);
            if (asset != null && !string.IsNullOrEmpty(asset.text)) return asset.text.Trim();
            if (warn) Debug.LogWarning($"[LLMInterface] No API key — set the Inspector field or add Resources/{resourceName}.txt.");
            return "";
        }

        string LoadStaticPrompt()
        {
            if (systemPromptAsset != null && !string.IsNullOrEmpty(systemPromptAsset.text)) return systemPromptAsset.text;
            var asset = Resources.Load<TextAsset>("LLMInterfacePrompt");
            if (asset != null) return asset.text;
            Debug.LogError("[LLMInterface] Missing Resources/LLMInterfacePrompt.txt — using a minimal fallback.");
            return "You control objects in a 3D scene. Output one or more bracketed commands like [Move probe cephalad 10].";
        }

        void Update()
        {
            // Legacy Input API only — guarded so that a project set to "Input System
            // Package (New)" does not throw here every frame. The package takes no
            // package dependencies, so there is no new-Input-System branch; see the
            // pushToTalkKey tooltip. Button/API-driven recording still works there,
            // which is why the timeout below must stay OUTSIDE this guard.
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(pushToTalkKey) && !_isRecording) StartRecording();
            if (Input.GetKeyUp(pushToTalkKey) && _isRecording) StopAndSend();
#endif
            if (_isRecording && (Time.unscaledTime - _recordStartTime) > maxRecordSeconds) StopAndSend();
        }

        // -------- mic capture (native Microphone + WebGL, both platforms) --------

        public void StartRecording()
        {
            if (_isRecording) return;
            _tts?.Cancel();   // barge-in: stop any reply the instant PTT is held

#if UNITY_WEBGL && !UNITY_EDITOR
            WebMic.Init();
            if (!WebMic.IsReady()) { SetStatus("mic init — hold key again in a moment"); return; }
            WebMic.StartRecording();
            int sr = WebMic.GetSampleRate();
            _webglSampleRate = sr > 0 ? sr : 48000;
#else
            if (Microphone.devices.Length == 0) { Debug.LogError("[LLMInterface] No microphone."); SetStatus("no mic"); return; }
            string device = string.IsNullOrEmpty(microphoneDevice) ? null : microphoneDevice;
            _recordingClip = Microphone.Start(device, false, maxRecordSeconds, requestedSampleRate);
            if (_recordingClip == null) { Debug.LogError("[LLMInterface] Microphone.Start returned null."); SetStatus("mic error"); return; }
            _recordStartSamplePosition = 0;
#endif
            _recordStartTime = Time.unscaledTime;
            _isRecording = true;
            _heard = "";
            SetStatus("recording…");
        }

        public void StopAndSend()
        {
            if (!_isRecording) return;
            _isRecording = false;
            _pttReleaseTime = Time.realtimeSinceStartup;   // start of the release-to-move clock

            int sr;
            float[] mono;

#if UNITY_WEBGL && !UNITY_EDITOR
            int total = WebMic.StopRecording();
            sr = _webglSampleRate;
            if (total <= 0) { SetStatus("too short"); return; }
            mono = new float[total];
            int copied = WebMic.CopySamples(mono, total);
            if (copied < total) System.Array.Resize(ref mono, copied);
            if (mono.Length < sr / 5) { SetStatus("too short"); return; }
#else
            string dev = string.IsNullOrEmpty(microphoneDevice) ? null : microphoneDevice;
            int posAtStop = Microphone.GetPosition(dev);
            Microphone.End(dev);
            sr = _recordingClip.frequency;
            int channels = _recordingClip.channels;
            int samplesToRead = Mathf.Max(0, posAtStop - _recordStartSamplePosition);
            if (samplesToRead < sr / 5) { SetStatus("too short"); return; }
            float[] interleaved = new float[samplesToRead * channels];
            _recordingClip.GetData(interleaved, _recordStartSamplePosition);
            mono = (channels == 1) ? interleaved : DownmixToMono(interleaved, channels);
#endif
            SetStatus("processing…");
            StartCoroutine(RunLiteLLM(mono, sr));
        }

        static float[] DownmixToMono(float[] interleaved, int channels)
        {
            int frames = interleaved.Length / channels;
            var mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
                mono[i] = sum / channels;
            }
            return mono;
        }

        // -------- pipeline --------

        IEnumerator RunLiteLLM(float[] samples, int sr)
        {
            if (commander == null) { SetStatus("no ContextCommander"); yield break; }

            // *** the automatic context grab ***
            string systemPrompt = _staticPrompt + "\n\n" + commander.BuildContext()
                                + "\nRespond with one or more bracketed commands.";

            LiteLLMClient.Result result = null;
            yield return _client.Run(samples, sr, systemPrompt, _history,
                onDone: r => result = r,
                onTranscript: txt =>
                {
                    string c = (txt ?? "").Trim();
                    if (c.Length == 0) return;
                    AppendChat(true, c);
                    _history.Add(new ChatTurn("user", c));
                    _heard = c;
                    SetStatus($"processing… heard \"{c}\"");
                });

            if (result == null || !result.Ok) { SetStatus(result?.Error ?? "no response"); yield break; }

            // Per-leg latency. TTS fills in later, when it plays.
            asrSeconds = result.TranscribeSeconds;
            llmSeconds = result.ClassifySeconds;
            ttsSeconds = 0f;

            string raw = result.RawLlmOutput ?? "";
            var summaries = new List<string>();
            // Wrap the whole turn so its commands apply in order and replay as one
            // smooth sequence — a multi-command turn ("tilt, then rotate to 2
            // o'clock") can't clobber itself, and each command resolves against the
            // previous one's result (T10).
            commander.BeginCapture();
            DispatchBrackets(raw, summaries, out string speech);
            commander.EndCaptureAndReplay();

            // The delay the operator feels: PTT release → command dispatched.
            if (_pttReleaseTime > 0f) releaseToMoveSeconds = Time.realtimeSinceStartup - _pttReleaseTime;

            // After a motion, give any surface constraint one frame to settle,
            // then ask the host for a contact note (e.g. "pressing at the tissue
            // limit"). Quiet by default — "" when contact is normal. This is
            // decoupled: the package never touches the constraint; a host wires it.
            string contactNote = null;
            if (contactStatusProvider != null && summaries.Count > 0)
            {
                yield return null;
                string n = contactStatusProvider();
                if (!string.IsNullOrWhiteSpace(n)) contactNote = n.Trim();
            }

            // A [Say] reply is prose and is always spoken. Action summaries
            // ("swept the probe 20 mm cephalad") are shown in the chat log and,
            // when speakActionSummaries is on, also voiced — otherwise a
            // successful command produces no sound at all, which reads as the
            // assistant ignoring you. A [Say] takes priority: if the model both
            // acted and spoke, the prose is the reply and the summaries are just
            // the log.
            if (!string.IsNullOrEmpty(speech))
            {
                AppendChat(false, speech);
                foreach (var s in summaries) AppendChat(false, s);   // keep the actions in the log
                if (contactNote != null) AppendChat(false, contactNote);
                _tts?.Speak(contactNote != null ? speech + " " + contactNote : speech);
            }
            else if (summaries.Count > 0)
            {
                foreach (var s in summaries) AppendChat(false, s);
                if (contactNote != null) AppendChat(false, contactNote);
                if (speakActionSummaries)
                {
                    string spoken = string.Join(". ", summaries);
                    if (contactNote != null) spoken += ". " + contactNote;
                    _tts?.Speak(spoken);
                }
            }
            else AppendChat(false, raw);

            // Record the contact note in history too, so the model KNOWS next turn
            // that it was held short — not just the human hearing it once.
            _history.Add(new ChatTurn("assistant", contactNote != null ? raw + "\n(" + contactNote + ")" : raw));
            TrimHistory();
            SetStatus("ok");
        }

        static readonly Regex BracketRe = new Regex(@"\[([^\[\]]+)\]", RegexOptions.Compiled);

        // Parse [Verb ...] commands and route them to the commander. [Say "..."]
        // is captured as prose; everything else is a move/rotate/moveToward.
        void DispatchBrackets(string raw, List<string> summaries, out string speech)
        {
            speech = null;
            // No explicit duration → glide over defaultMotionSeconds instead of
            // snapping. An explicit "Ns" (sec > 0) always wins.
            float Dur(float sec) => sec > 0f ? sec : defaultMotionSeconds;
            foreach (Match m in BracketRe.Matches(raw ?? ""))
            {
                string body = m.Groups[1].Value.Trim();
                if (body.StartsWith("Say", System.StringComparison.OrdinalIgnoreCase))
                {
                    int a = body.IndexOf('"'), b = body.LastIndexOf('"');
                    if (a >= 0 && b > a) speech = body.Substring(a + 1, b - a - 1).Trim();
                    continue;
                }
                var t = body.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                string verb = t[0].ToLowerInvariant();
                if (verb == "noop") continue;

                // Anything we cannot act on is REPORTED, never dropped. A silently
                // ignored command is indistinguishable from one that did nothing:
                // the user speaks, the scene doesn't move, and no one is told why.
                string r = null;
                if (t.Length < 3)   // every remaining command is verb + object + >=1 arg
                {
                    r = $"ignored '[{body}]' — not enough arguments";
                }
                else switch (verb)
                {
                    case "move":        // [Move obj dir <dist> <Ns?>]
                    {
                        ParseOpts(t, 3, out float mm, out bool hasMm, out float sec, out _);
                        r = commander.Move(t[1], t[2], hasMm ? mm : defaultMoveMm, Dur(sec));
                        break;
                    }
                    case "rotateabout": // [RotateAbout obj axis <deg> <pivot?> <Ns?>]
                    {
                        float deg = defaultRotateDeg; int optStart = 3;
                        if (t.Length > 3 && TryMm(t[3], out float d)) { deg = d; optStart = 4; }
                        ParseOpts(t, optStart, out _, out _, out float sec, out string pivot);
                        r = commander.RotateAbout(t[1], t[2], deg, pivot, Dur(sec));
                        break;
                    }
                    case "movetoward":  // [MoveToward obj target <dist?> <Ns?>]
                    {
                        ParseOpts(t, 3, out float mm, out bool hasMm, out float sec, out _);
                        r = commander.MoveToward(t[1], t[2], hasMm ? mm : 0f, Dur(sec));
                        break;
                    }
                    case "sweep":       // [Sweep obj dir <dist> <Ns?>]  — tool's short axis
                    {
                        ParseOpts(t, 3, out float mm, out bool hasMm, out float sec, out _);
                        r = commander.Sweep(t[1], t[2], hasMm ? mm : defaultMoveMm, Dur(sec));
                        break;
                    }
                    case "slide":       // [Slide obj dir <dist> <Ns?>]  — tool's long axis
                    {
                        ParseOpts(t, 3, out float mm, out bool hasMm, out float sec, out _);
                        r = commander.Slide(t[1], t[2], hasMm ? mm : defaultMoveMm, Dur(sec));
                        break;
                    }
                    case "rock":        // [Rock obj dir <deg> <Ns?>]    — about short axis
                    {
                        float deg = defaultRotateDeg; int optStart = 3;
                        if (t.Length > 3 && TryMm(t[3], out float d)) { deg = d; optStart = 4; }
                        ParseOpts(t, optStart, out _, out _, out float sec, out _);
                        r = commander.Rock(t[1], t[2], deg, Dur(sec));
                        break;
                    }
                    case "tilt":        // [Tilt obj dir <deg> <Ns?>]    — about long axis
                    {
                        float deg = defaultRotateDeg; int optStart = 3;
                        if (t.Length > 3 && TryMm(t[3], out float d)) { deg = d; optStart = 4; }
                        ParseOpts(t, optStart, out _, out _, out float sec, out _);
                        r = commander.Tilt(t[1], t[2], deg, Dur(sec));
                        break;
                    }
                    case "rotate":      // [Rotate obj <clockwise|counterclockwise> <deg> <Ns?>]  — longitudinal spin
                    {
                        float deg = defaultRotateDeg; int optStart = 3;
                        if (t.Length > 3 && TryMm(t[3], out float d)) { deg = d; optStart = 4; }
                        ParseOpts(t, optStart, out _, out _, out float sec, out _);
                        r = commander.Rotate(t[1], t[2], deg, Dur(sec));
                        break;
                    }
                    case "rotatetoclock":  // [RotateToClock obj <hour> <Ns?>]
                    {
                        if (float.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float hour))
                        {
                            ParseOpts(t, 3, out _, out _, out float sec, out _);
                            r = commander.RotateToClock(t[1], hour, Dur(sec));
                        }
                        else r = $"ignored '[{body}]' — clock position must be a number (an hour)";
                        break;
                    }
                    case "aim":         // [Aim obj <direction|target> <Ns?>]  — point the marker
                    {
                        ParseOpts(t, 3, out _, out _, out float sec, out _);
                        r = commander.Aim(t[1], t[2], Dur(sec));
                        break;
                    }
                    case "view":        // [View obj <plane> <Ns?>]      — align the imaging plane to a named anatomical plane
                    {
                        ParseOpts(t, 3, out _, out _, out float sec, out _);
                        r = commander.View(t[1], t[2], Dur(sec));
                        break;
                    }
                    case "pointat":     // [PointAt obj <direction|target> <Ns?>]  — aim the business end at a target
                    {
                        ParseOpts(t, 3, out _, out _, out float sec, out _);
                        r = commander.PointAt(t[1], t[2], Dur(sec));
                        break;
                    }
                    case "viewpoint":   // [Viewpoint obj <AP|PA|left|right|superior|inferior> <target?> <Ns?>]  — camera viewpoint
                    {
                        ParseOpts(t, 3, out _, out _, out float sec, out string tgt);
                        r = commander.Viewpoint(t[1], t[2], tgt, Dur(sec));
                        break;
                    }
                    case "compress":    // [Compress obj <dist> <Ns?>]   — into tissue
                    {
                        ParseOpts(t, 2, out float mm, out bool hasMm, out float sec, out _);
                        r = commander.Compress(t[1], hasMm ? mm : defaultMoveMm, Dur(sec));
                        break;
                    }
                    case "lift":        // [Lift obj <dist> <Ns?>]       — off tissue
                    {
                        ParseOpts(t, 2, out float mm, out bool hasMm, out float sec, out _);
                        r = commander.Lift(t[1], hasMm ? mm : defaultMoveMm, Dur(sec));
                        break;
                    }
                    // GENERIC TOOL VERB (T6.1): not one of the fixed commands above, so
                    // try it as a verb the TOOL declares in its own profile — a camera's
                    // "zoom", a needle's "advance". The tool's table says which axis it
                    // acts on and whether the number is millimetres or degrees, so this
                    // one case serves every tool-specific verb without a case of its own.
                    // Only when no tool declares it is it really an unknown command.
                    default:
                        if (commander.TryToolVerbKind(t[1], verb, out bool isRotation))
                        {
                            ParseOpts(t, 3, out float amt, out bool hasAmt, out float sec, out _);
                            float mag = hasAmt ? amt : (isRotation ? defaultRotateDeg : defaultMoveMm);
                            r = commander.ToolCommand(t[1], verb, t[2], mag, Dur(sec));
                        }
                        else
                        {
                            r = $"ignored '[{body}]' — unknown command '{t[0]}'";
                            if (verboseLogging) Debug.LogWarning($"[LLMInterface] unknown command from the model: [{body}]");
                        }
                        break;
                }
                if (!string.IsNullOrEmpty(r)) summaries.Add(r);
            }
        }

        // Classify the trailing optional tokens by unit, so their ORDER and
        // PRESENCE don't matter: "<n>s" = seconds, a bare number / "<n>mm" /
        // "<n>cm" = distance, anything else = a pivot name. This is what lets a
        // timed move omit the distance, e.g. [MoveToward probe gallbladder 2s].
        static void ParseOpts(string[] t, int start, out float mm, out bool hasMm, out float seconds, out string pivot)
        {
            mm = 0f; hasMm = false; seconds = 0f; pivot = null;
            for (int i = start; i < t.Length; i++)
            {
                if (TrySeconds(t[i], out float s)) seconds = s;
                else if (TryMm(t[i], out float d)) { mm = d; hasMm = true; }
                else pivot = t[i];
            }
        }

        static bool TrySeconds(string s, out float v)
        {
            v = 0f;
            if (string.IsNullOrEmpty(s) || !(s.EndsWith("s") || s.EndsWith("S"))) return false;
            return float.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        static bool TryMm(string s, out float mm)
        {
            mm = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            string x = s; float scale = 1f; string low = s.ToLowerInvariant();
            if (low.EndsWith("mm")) x = s.Substring(0, s.Length - 2);
            else if (low.EndsWith("cm")) { x = s.Substring(0, s.Length - 2); scale = 10f; }
            if (!float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out mm)) return false;
            mm *= scale; return true;
        }

        // -------- chat + status --------

        void AppendChat(bool user, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _chat.Add((user, text));
            int over = _chat.Count - Mathf.Max(4, chatLogEntries);
            if (over > 0) _chat.RemoveRange(0, over);
            (user ? onUserMessage : onAssistantMessage)?.Invoke(text);
        }

        void TrimHistory()
        {
            int max = Mathf.Max(0, historyTurns) * 2;
            int over = _history.Count - max;
            if (over > 0) _history.RemoveRange(0, over);
        }

        void SetStatus(string s) { _status = s; onStatus?.Invoke(s); if (verboseLogging) Debug.Log($"[LLMInterface] {s}"); }

        /// <summary>Wipe the LLM history and the on-screen chat log.</summary>
        public void ClearChat() { _history.Clear(); _chat.Clear(); _tts?.Cancel(); SetStatus("idle"); }

        // -------- light overlay: bottom status line + last-N chat log --------

        GUIStyle _statusStyle, _chatStyle, _toggleStyle;
        Texture2D _bgTex;
        bool _chatCollapsed;

        /// <summary>Show or hide the chat log at runtime, leaving the status bar alone. The overlay's own +/- button calls this.</summary>
        public void SetChatCollapsed(bool collapsed) => _chatCollapsed = collapsed;
        /// <summary>Is the chat log currently collapsed?</summary>
        public bool ChatCollapsed => _chatCollapsed;

        void OnGUI()
        {
            if (!showOverlay) return;
            if (_bgTex == null) { _bgTex = new Texture2D(1, 1); _bgTex.SetPixel(0, 0, Color.white); _bgTex.Apply(); }
            if (_statusStyle == null)
            {
                _statusStyle = new GUIStyle { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(16, 16, 8, 8), normal = { textColor = new Color(0.95f, 0.97f, 1f) } };
                _chatStyle = new GUIStyle { fontSize = 18, wordWrap = true, alignment = TextAnchor.UpperLeft,
                    padding = new RectOffset(12, 12, 4, 4), normal = { textColor = new Color(0.85f, 0.92f, 1f) } };
                // No background textures: DrawBox paints the panel behind it, so the
                // button reads as part of the overlay rather than as OS chrome.
                _toggleStyle = new GUIStyle { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = new Color(0.72f, 0.82f, 0.95f) },
                    hover  = { textColor = Color.white },
                    active = { textColor = new Color(0.72f, 1f, 0.78f) } };
            }

            // Bottom status bar. Don't advertise the key when it can't fire.
#if ENABLE_LEGACY_INPUT_MANAGER
            string idle = $"Hold {pushToTalkKey} to talk";
#else
            string idle = pushToTalkButton != null
                ? "Hold the talk button"
                : $"'{pushToTalkKey}' inactive — Active Input Handling is New; see Console";
#endif
            string line = _status == "idle" ? idle : _status;
            var bar = new Rect(12, Screen.height - 52, Screen.width - 24, 40);
            DrawBox(bar, new Color(0.06f, 0.10f, 0.16f, 0.82f));
            GUI.Label(bar, line, _statusStyle);

            // Collapse toggle. Drawn at a FIXED rect, not on the chat panel: the panel
            // is skipped entirely until the first message and its height follows its
            // content, so a button parented to it would appear late and then move. Here
            // it is in the same place from frame one, which is what makes it findable
            // when the log is collapsed and there is nothing else on screen to aim at.
            const float toggleSize = 26f;
            var toggleRect = new Rect(12, 12, toggleSize, toggleSize);
            DrawBox(toggleRect, new Color(0.05f, 0.08f, 0.12f, 0.78f));
            if (GUI.Button(toggleRect, _chatCollapsed ? "+" : "−", _toggleStyle))
                _chatCollapsed = !_chatCollapsed;

            // Top-left chat log, below the toggle. Height is measured from the wrapped
            // content, not assumed one line per entry — long summaries wrap, and a
            // fixed per-entry height clips the last line inside the BeginArea.
            // Collapsing hides only this; the status bar above is deliberately left
            // alone, since it is the only sign the mic is live or a request is running.
            if (!_chatCollapsed && _chat.Count > 0)
            {
                float panelWidth = Mathf.Min(560, Screen.width - 24);
                float innerWidth = panelWidth - 8;

                var contents = new GUIContent[_chat.Count];
                float contentHeight = 0f;
                for (int i = 0; i < _chat.Count; i++)
                {
                    contents[i] = new GUIContent((_chat[i].user ? "You ▸ " : "AI ▸ ") + _chat[i].text);
                    contentHeight += _chatStyle.CalcHeight(contents[i], innerWidth);
                }

                var panel = new Rect(12, 12 + toggleSize + 4, panelWidth, contentHeight + 12);
                DrawBox(panel, new Color(0.05f, 0.08f, 0.12f, 0.78f));
                GUILayout.BeginArea(new Rect(panel.x + 4, panel.y + 4, innerWidth, panel.height - 8));
                for (int i = 0; i < _chat.Count; i++)
                {
                    _chatStyle.normal.textColor = _chat[i].user ? new Color(0.85f, 0.92f, 1f) : new Color(0.72f, 1f, 0.78f);
                    GUILayout.Label(contents[i], _chatStyle);
                }
                GUILayout.EndArea();
            }
        }

        void DrawBox(Rect r, Color c) { var p = GUI.color; GUI.color = c; GUI.DrawTexture(r, _bgTex); GUI.color = p; }

#if UNITY_EDITOR
        // ponytail: the one runnable check — the bracket parser is the new logic.
        [ContextMenu("Self test")]
        void SelfTest()
        {
            // Debug.Assert logs but does not halt, so a bare Assert lets the run
            // reach the "passed" log with failures behind it. Count them instead.
            int failed = 0;
            void Check(bool ok, string what)
            {
                if (ok) return;
                failed++;
                Debug.LogError($"[LLMInterface] self test check FAILED: {what}");
            }

            var go = new GameObject("probe");
            commander = go.AddComponent<ContextCommander>();
            commander.tools = new[] { new ToolBinding { tool = go.transform } };
            // Snap, not glide: the default-glide path animates via a coroutine that
            // needs frames to run, which don't tick in an edit-mode self test. This
            // test checks the PARSER dispatches the move, so force instant motion.
            defaultMotionSeconds = 0f;
            var sums = new List<string>();
            DispatchBrackets("[Move probe cephalad 10] [Say \"done\"]", sums, out var speech);
            Check(go.transform.position == Vector3.forward * 10f, "parser: move dispatched");
            Check(speech == "done", "parser: Say captured");

            // An unactionable command must be REPORTED, not silently dropped —
            // otherwise a stale verb (e.g. the old "Rotate", now RotateAbout)
            // looks exactly like a command that did nothing.
            sums.Clear();
            DispatchBrackets("[Frobnicate probe cephalad 30]", sums, out _);
            Check(sums.Count == 1 && sums[0].Contains("unknown command"),
                  $"parser: unknown verb must be reported, got [{string.Join(" | ", sums)}]");
            sums.Clear();
            DispatchBrackets("[Move probe]", sums, out _);
            Check(sums.Count == 1 && sums[0].Contains("not enough arguments"),
                  $"parser: malformed command must be reported, got [{string.Join(" | ", sums)}]");
            sums.Clear();
            DispatchBrackets("[Noop]", sums, out _);
            Check(sums.Count == 0, "parser: Noop stays silent");

            // T6.1 — a verb the TOOL declares dispatches even though the switch above
            // has no case for it. Without this a camera could author "zoom" and never
            // be asked for it; the model would substitute a generic Move instead.
            // "zoom" is authored here on +Y with in/out qualifiers, deliberately NOT a
            // word any fixed command passes.
            commander.tools = new[] { new ToolBinding {
                tool = go.transform,
                translateY = new AxisVerbs { verbs = new[] { "zoom" },
                                             plusQualifiers = new[] { "in" },
                                             minusQualifiers = new[] { "out" } },
                rotateY    = new AxisVerbs { verbs = new[] { "yaw" },
                                             plusQualifiers = new[] { "port" },
                                             minusQualifiers = new[] { "starboard" } },
            } };
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            sums.Clear();
            DispatchBrackets("[Zoom probe in 25]", sums, out _);
            Check(Vector3.Distance(go.transform.position, Vector3.up * 25f) < 1e-3f,
                  $"parser: a tool-declared verb must dispatch and travel its authored axis, at {go.transform.position}");
            Check(sums.Count == 1 && !sums[0].Contains("unknown command"),
                  $"parser: a tool-declared verb must not be reported unknown, got [{string.Join(" | ", sums)}]");

            // Its number is DEGREES when the verb sits in a rotation table, mm when in
            // a translation table — the tool's profile decides, not the parser.
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            sums.Clear();
            DispatchBrackets("[Yaw probe port 90]", sums, out _);
            Check(Mathf.Abs(Quaternion.Angle(go.transform.rotation, Quaternion.identity) - 90f) < 0.5f,
                  $"parser: a tool-declared ROTATION verb must turn by DEGREES, turned {Quaternion.Angle(go.transform.rotation, Quaternion.identity):0.#}");
            Check(go.transform.position == Vector3.zero, "parser: a rotation verb must not translate");

            // Still unknown when NO tool declares it — the fallthrough must not swallow
            // a genuinely bogus command and report it as done.
            sums.Clear();
            DispatchBrackets("[Frobnicate probe cephalad 30]", sums, out _);
            Check(sums.Count == 1 && sums[0].Contains("unknown command"),
                  $"parser: a verb no tool declares is still unknown, got [{string.Join(" | ", sums)}]");
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;

            DestroyImmediate(go);

            if (failed == 0) Debug.Log("LLMInterfaceControl self test passed");
            else Debug.LogError($"[LLMInterface] SELF TEST FAILED — {failed} check(s) failed; see errors above.");
        }
#endif
    }

    /// <summary>Forwards UI Button pointer down/up to PTT (hold to record).</summary>
    internal class PTTButtonTrigger : MonoBehaviour,
        UnityEngine.EventSystems.IPointerDownHandler,
        UnityEngine.EventSystems.IPointerUpHandler
    {
        public LLMInterfaceControl Owner;
        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e) { if (Owner != null) Owner.StartRecording(); }
        public void OnPointerUp(UnityEngine.EventSystems.PointerEventData e) { if (Owner != null) Owner.StopAndSend(); }
    }
}
