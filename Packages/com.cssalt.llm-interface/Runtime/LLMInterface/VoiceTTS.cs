// VoiceTTS.cs
// Provider-pluggable text-to-speech, no third-party deps. Two modes:
//   - Browser : window.speechSynthesis (free, instant, no key, robotic).
//   - Deepgram: /v1/speak?model=<voice> (Aura), MP3 streaming (best quality).
//
// In WebGL the playback path goes through VoiceTTS.jslib. In the Editor (and
// other standalone targets) the C# UnityWebRequest path fetches the MP3 to a
// temp file and plays it via UnityWebRequestMultimedia. Browser TTS is a no-op
// outside WebGL (the editor logs the prose).

using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CSSALT.LLMInterface
{
    public enum TTSProvider
    {
        Browser,
        Deepgram,
    }

    /// <summary>
    /// Owns the speech-out side of the voice loop. Not a MonoBehaviour, so it
    /// borrows a coroutine host from its owner.
    /// </summary>
    public class VoiceTTS
    {
        // --- shared config --------------------------------------------------
        public TTSProvider Provider = TTSProvider.Browser;
        public bool Enabled = true;
        public bool Verbose = false;

        // --- browser TTS knobs ----------------------------------------------
        public float BrowserRate = 1.0f;
        public float BrowserPitch = 1.0f;

        // --- Deepgram config ------------------------------------------------
        public string DeepgramBaseUrl = "https://api.deepgram.com";
        public string DeepgramApiKey = "";
        public string DeepgramVoice = "aura-2-hyperion-en";
        public int DeepgramTimeoutSeconds = 20;

        // --- timing (editor/standalone playback path) ----------------------
        /// <summary>Seconds from Speak() to audio starting to play (request + fetch + decode). 0 until first spoken.</summary>
        public float LastSpeakSeconds { get; private set; }
        /// <summary>Fired with LastSpeakSeconds when speech begins playing.</summary>
        public System.Action<float> OnSpoken;
        private float _speakStart;

        // --- editor-only playback plumbing ---------------------------------
        private readonly MonoBehaviour _host;
        private AudioSource _audioSource;
        private Coroutine _activeRequest;

        public VoiceTTS(MonoBehaviour coroutineHost, AudioSource audioSource = null)
        {
            _host = coroutineHost;
            _audioSource = audioSource;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void CSSALT_TTS_BrowserSpeak(string text, int rateBp, int pitchBp);
        [DllImport("__Internal")] private static extern void CSSALT_TTS_BrowserCancel();
        [DllImport("__Internal")] private static extern void CSSALT_TTS_DeepgramSpeak(string baseUrl, string apiKey, string voice, string text);
        [DllImport("__Internal")] private static extern void CSSALT_TTS_DeepgramCancel();
#endif

        public void Speak(string text)
        {
            if (!Enabled || string.IsNullOrEmpty(text)) return;
            Cancel();
            _speakStart = Time.realtimeSinceStartup;   // for LastSpeakSeconds

            switch (Provider)
            {
                case TTSProvider.Browser:  SpeakBrowser(text); break;
                case TTSProvider.Deepgram: SpeakDeepgram(text); break;
            }
        }

        public void Cancel()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            CSSALT_TTS_BrowserCancel();
            CSSALT_TTS_DeepgramCancel();
#else
            if (_host != null && _activeRequest != null)
            {
                _host.StopCoroutine(_activeRequest);
                _activeRequest = null;
            }
            if (_audioSource != null && _audioSource.isPlaying)
                _audioSource.Stop();
#endif
        }

        // --- Browser ---------------------------------------------------------

        private void SpeakBrowser(string text)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            int rateBp = Mathf.RoundToInt(Mathf.Clamp(BrowserRate, 0.1f, 10f) * 100f);
            int pitchBp = Mathf.RoundToInt(Mathf.Clamp(BrowserPitch, 0.0f, 2.0f) * 100f);
            CSSALT_TTS_BrowserSpeak(text, rateBp, pitchBp);
#else
            if (Verbose) Debug.Log($"[VoiceTTS] (browser, editor no-op) \"{text}\"");
#endif
        }

        // --- Deepgram --------------------------------------------------------

        private void SpeakDeepgram(string text)
        {
            if (string.IsNullOrEmpty(DeepgramApiKey))
            {
                Debug.LogWarning("[VoiceTTS] Deepgram API key not set");
                return;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            CSSALT_TTS_DeepgramSpeak(DeepgramBaseUrl, DeepgramApiKey, DeepgramVoice, text);
#else
            if (_host == null) { Debug.LogWarning("[VoiceTTS] no coroutine host"); return; }
            if (_audioSource == null)
            {
                _audioSource = _host.gameObject.GetComponent<AudioSource>()
                              ?? _host.gameObject.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.loop = false;
            }
            _activeRequest = _host.StartCoroutine(SpeakDeepgramEditor(text));
#endif
        }

#if !UNITY_WEBGL || UNITY_EDITOR
        private IEnumerator SpeakDeepgramEditor(string text)
        {
            string url = $"{DeepgramBaseUrl.TrimEnd('/')}/v1/speak?model={UnityWebRequest.EscapeURL(DeepgramVoice)}";
            string body = "{\"text\":\"" + EscapeJson(text) + "\"}";
            byte[] payload = Encoding.UTF8.GetBytes(body);

            byte[] mp3 = null;
            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(payload);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", $"Token {DeepgramApiKey}");
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = DeepgramTimeoutSeconds;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[VoiceTTS] Deepgram HTTP error: {req.error} — {req.downloadHandler?.text}");
                    _activeRequest = null;
                    yield break;
                }
                mp3 = req.downloadHandler.data;
            }
            if (mp3 == null || mp3.Length == 0)
            {
                Debug.LogError("[VoiceTTS] Deepgram returned empty body");
                _activeRequest = null;
                yield break;
            }

            // GetAudioClip only loads from a URI, so detour through a temp
            // file. AudioType.MPEG handles MP3 across editor + standalone.
            string path = Path.Combine(Application.temporaryCachePath, "cssalt_tts.mp3");
            File.WriteAllBytes(path, mp3);

            using (var clipReq = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.MPEG))
            {
                yield return clipReq.SendWebRequest();
                if (clipReq.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[VoiceTTS] AudioClip load failed: {clipReq.error}");
                    _activeRequest = null;
                    yield break;
                }
                var clip = DownloadHandlerAudioClip.GetContent(clipReq);
                if (clip != null && _audioSource != null)
                {
                    _audioSource.PlayOneShot(clip);
                    LastSpeakSeconds = Time.realtimeSinceStartup - _speakStart;
                    OnSpoken?.Invoke(LastSpeakSeconds);
                    if (Verbose) Debug.Log($"[VoiceTTS] (deepgram, editor) {mp3.Length} bytes -> playing (TTS {LastSpeakSeconds:F2}s)");
                }
            }
            _activeRequest = null;
        }
#endif

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
