// LiteLLMClient.cs
// Direct HTTP client for an OpenAI-compatible LiteLLM proxy — no third-party deps.
//
// Two stages, both OpenAI-compatible endpoints:
//   1. POST multipart WAV  -> /audio/transcriptions   (Whisper ASR)
//   2. POST JSON chat req  -> /chat/completions       (reasoning model)
//
// No third-party deps. JSON assembled by hand; responses parsed via
// JsonUtility. WAV encoder produces int16 PCM, accepted by Whisper at any
// sample rate (native mic rate is fine).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CSSALT.LLMInterface
{
    /// <summary>
    /// One turn in the rolling conversation passed to /chat/completions.
    /// Roles are "user" or "assistant"; the system prompt is rebuilt fresh
    /// on every call (because LIVE STATE changes) and is NOT stored here.
    /// </summary>
    public struct ChatTurn
    {
        public string role;
        public string content;
        public ChatTurn(string r, string c) { role = r; content = c; }
    }

    public class LiteLLMClient
    {
        // --- config ---------------------------------------------------------
        public string BaseUrl = "https://api.ai.it.ufl.edu/v1";
        public string ApiKey = "";
        public string WhisperModel = "whisper-large-v3";
        public string LlmModel = "gpt-oss-120b";
        public string ReasoningEffort = "medium";
        public int MaxTokens = 2048;
        public int TimeoutSeconds = 30;
        public bool Verbose = false;

        public class Result
        {
            public bool Ok;
            public string Transcript = "";
            public string RawLlmOutput = "";
            public float TranscribeSeconds;
            public float ClassifySeconds;
            public string Error = "";
        }

        /// <summary>
        /// Single coroutine that runs both stages (Whisper + chat) and calls
        /// onDone with the combined Result. Must be started via StartCoroutine
        /// on a MonoBehaviour host.
        ///
        /// <paramref name="history"/> is the rolling user/assistant conversation
        /// from prior turns — pass null or empty for a one-shot call. The
        /// fresh system prompt + history + current transcript are sent as
        /// the OpenAI-compatible messages[] array.
        ///
        /// <paramref name="onTranscript"/> fires the moment Whisper returns,
        /// before the chat completion starts.
        /// </summary>
        public IEnumerator Run(
            float[] monoSamples,
            int sampleRate,
            string systemPrompt,
            List<ChatTurn> history,
            Action<Result> onDone,
            Action<string> onTranscript = null)
        {
            var result = new Result();

            if (string.IsNullOrEmpty(ApiKey))
            {
                result.Ok = false;
                result.Error = "no API key configured";
                onDone?.Invoke(result);
                yield break;
            }

            byte[] wav = EncodePcm16Wav(monoSamples, sampleRate);

            // --- Stage 1: Whisper ASR --------------------------------------
            float t0 = Time.realtimeSinceStartup;
            string transcript = null;
            string err = null;
            yield return Transcribe(wav, (txt, e) => { transcript = txt; err = e; });
            result.TranscribeSeconds = Time.realtimeSinceStartup - t0;

            if (err != null)
            {
                result.Ok = false;
                result.Error = $"whisper: {err}";
                onDone?.Invoke(result);
                yield break;
            }
            result.Transcript = (transcript ?? "").Trim();

            if (Verbose) Debug.Log($"[LiteLLMClient] ASR ({result.TranscribeSeconds:F2}s): \"{result.Transcript}\"");

            // Fire the live-transcript callback BEFORE the chat completion.
            onTranscript?.Invoke(result.Transcript);

            if (string.IsNullOrEmpty(result.Transcript))
            {
                // Empty audio → treat as Noop. Don't bill a chat completion.
                result.Ok = true;
                result.RawLlmOutput = "[Noop]";
                onDone?.Invoke(result);
                yield break;
            }

            // --- Stage 2: Chat completion ----------------------------------
            float t1 = Time.realtimeSinceStartup;
            string chatBody = null;
            err = null;
            yield return Classify(result.Transcript, systemPrompt, history,
                (body, e) => { chatBody = body; err = e; });
            result.ClassifySeconds = Time.realtimeSinceStartup - t1;

            if (err != null)
            {
                result.Ok = false;
                result.Error = $"chat: {err}";
                onDone?.Invoke(result);
                yield break;
            }

            string finishReason;
            string content = ExtractChatContent(chatBody, out finishReason);
            result.RawLlmOutput = content ?? "";
            result.Ok = !string.IsNullOrEmpty(result.RawLlmOutput);

            if (Verbose) Debug.Log($"[LiteLLMClient] LLM ({result.ClassifySeconds:F2}s) finish={finishReason ?? "?"}: \"{result.RawLlmOutput}\"");

            if (!result.Ok)
            {
                result.Error = finishReason == "length"
                    ? $"LLM ran out of tokens (finish=length). Raise Max Tokens in the Inspector (try 4096) or drop Reasoning Effort to 'low'."
                    : $"LLM returned empty content (finish={finishReason ?? "?"}).  Body head: {Head(chatBody, 200)}";
            }

            onDone?.Invoke(result);
        }

        private static string Head(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        // --- Stage 1 helper -------------------------------------------------

        private IEnumerator Transcribe(byte[] wavBytes, Action<string, string> onDone)
        {
            string url = $"{BaseUrl}/audio/transcriptions";
            var form = new WWWForm();
            form.AddField("model", WhisperModel);
            form.AddBinaryData("file", wavBytes, "audio.wav", "audio/wav");

            Debug.Log($"[LiteLLMClient] Whisper upload: {wavBytes.Length / 1024} KB to {url} (timeout={TimeoutSeconds}s)");
            float tSend = Time.realtimeSinceStartup;

            using (var req = UnityWebRequest.Post(url, form))
            {
                req.SetRequestHeader("Authorization", $"Bearer {ApiKey}");
                req.timeout = TimeoutSeconds;
                yield return req.SendWebRequest();

                float elapsed = Time.realtimeSinceStartup - tSend;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[LiteLLMClient] Whisper failed after {elapsed:F1}s — uploaded={req.uploadedBytes}B result={req.result} error=\"{req.error}\" HTTP={req.responseCode}");
                    onDone?.Invoke(null, $"{req.error} (HTTP {req.responseCode})");
                    yield break;
                }
                var parsed = JsonUtility.FromJson<WhisperResponse>(req.downloadHandler.text);
                onDone?.Invoke(parsed != null ? (parsed.text ?? "") : "", null);
            }
        }

        // --- Stage 2 helper -------------------------------------------------

        private IEnumerator Classify(string transcript, string systemPrompt,
                                     List<ChatTurn> history,
                                     Action<string, string> onDone)
        {
            string url = $"{BaseUrl}/chat/completions";
            string json = BuildChatPayload(systemPrompt, history, transcript);
            byte[] body = Encoding.UTF8.GetBytes(json);

            using (var req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", $"Bearer {ApiKey}");
                req.timeout = TimeoutSeconds;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    onDone?.Invoke(null, $"{req.error} (HTTP {req.responseCode})");
                    yield break;
                }
                onDone?.Invoke(req.downloadHandler.text, null);
            }
        }

        // --- Response parsing ----------------------------------------------

        private static string ExtractChatContent(string body, out string finishReason)
        {
            finishReason = null;
            if (string.IsNullOrEmpty(body)) return null;
            var resp = JsonUtility.FromJson<ChatResponse>(body);
            if (resp == null || resp.choices == null || resp.choices.Length == 0) return null;

            finishReason = resp.choices[0].finish_reason;
            string content = resp.choices[0].message?.content ?? "";
            if (!string.IsNullOrEmpty(content.Trim())) return content;

            // gpt-oss reasoning models can exhaust tokens on internal
            // reasoning and emit empty content. Scan reasoning_content as
            // a last-resort fallback — the final bracket is often the last
            // thing the model produced before hitting the budget.
            string reasoning = resp.choices[0].message?.reasoning_content ?? "";
            if (reasoning.Contains("["))
                return reasoning;

            return null;
        }

        [Serializable] private class WhisperResponse { public string text; }
        [Serializable] private class ChatResponse { public ChatChoice[] choices; }
        [Serializable] private class ChatChoice { public ChatMessage message; public string finish_reason; }
        [Serializable] private class ChatMessage { public string content; public string reasoning_content; }

        // --- Request body assembly -----------------------------------------

        private string BuildChatPayload(string systemPrompt, List<ChatTurn> history, string userText)
        {
            var sb = new StringBuilder(systemPrompt.Length + 512);
            sb.Append("{\"model\":\"").Append(EscapeJson(LlmModel)).Append("\",");
            sb.Append("\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"").Append(EscapeJson(systemPrompt)).Append("\"}");
            if (history != null)
            {
                foreach (var turn in history)
                {
                    if (string.IsNullOrEmpty(turn.role) || string.IsNullOrEmpty(turn.content)) continue;
                    sb.Append(",{\"role\":\"").Append(EscapeJson(turn.role));
                    sb.Append("\",\"content\":\"").Append(EscapeJson(turn.content)).Append("\"}");
                }
            }
            sb.Append(",{\"role\":\"user\",\"content\":\"").Append(EscapeJson(userText)).Append("\"}");
            sb.Append("],");
            sb.Append("\"temperature\":0,");
            sb.Append("\"max_tokens\":").Append(MaxTokens).Append(",");
            sb.Append("\"reasoning_effort\":\"").Append(EscapeJson(ReasoningEffort)).Append("\"");
            sb.Append("}");
            return sb.ToString();
        }

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

        // --- WAV encoder ----------------------------------------------------

        /// <summary>
        /// Encode mono float32 PCM [-1,1] into a 16-bit little-endian WAV byte
        /// array. Whisper accepts any sample rate; no resample needed.
        /// </summary>
        public static byte[] EncodePcm16Wav(float[] samples, int sampleRate, int channels = 1)
        {
            const int bitsPerSample = 16;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int dataSize = samples.Length * 2;
            int fileSize = 36 + dataSize;

            using (var ms = new MemoryStream(fileSize + 8))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(fileSize);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);            // PCM subchunk size
                bw.Write((short)1);      // AudioFormat = PCM
                bw.Write((short)channels);
                bw.Write(sampleRate);
                bw.Write(byteRate);
                bw.Write((short)(channels * bitsPerSample / 8));  // block align
                bw.Write((short)bitsPerSample);
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataSize);
                for (int i = 0; i < samples.Length; i++)
                {
                    float v = Mathf.Clamp(samples[i], -1f, 1f);
                    short s = (short)Mathf.RoundToInt(v * 32767f);
                    bw.Write(s);
                }
                return ms.ToArray();
            }
        }
    }
}
