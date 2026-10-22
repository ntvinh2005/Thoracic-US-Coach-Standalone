// WebMic.cs
// Thin C# wrapper around WebMic.jslib (jslib exports prefixed CSSALT_Mic_). All
// public methods are no-ops on non-WebGL platforms (and in the Editor), so
// calling code is written once and stays compile-safe everywhere.

using System.Runtime.InteropServices;

namespace CSSALT.LLMInterface
{
    internal static class WebMic
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void CSSALT_Mic_Init();
        [DllImport("__Internal")] private static extern int  CSSALT_Mic_IsReady();
        [DllImport("__Internal")] private static extern int  CSSALT_Mic_GetSampleRate();
        [DllImport("__Internal")] private static extern void CSSALT_Mic_StartRecording();
        [DllImport("__Internal")] private static extern int  CSSALT_Mic_StopRecording();
        [DllImport("__Internal")] private static extern int  CSSALT_Mic_CopySamples(float[] buf, int maxLen);
#endif

        /// <summary>
        /// Resume the AudioContext, register the AudioWorklet and request mic
        /// permission via getUserMedia. Must be called during a user gesture
        /// (keydown/click) or the browser will refuse. No-op outside WebGL.
        /// </summary>
        public static void Init()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            CSSALT_Mic_Init();
#endif
        }

        /// <summary>Returns true if the mic is initialized and permission granted.</summary>
        public static bool IsReady()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return CSSALT_Mic_IsReady() != 0;
#else
            return true;  // native Microphone handles readiness elsewhere
#endif
        }

        /// <summary>Actual capture sample rate (typically 48000 on macOS/Chrome).</summary>
        public static int GetSampleRate()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return CSSALT_Mic_GetSampleRate();
#else
            return 48000;
#endif
        }

        /// <summary>Begin streaming PCM chunks into the JS-side buffer.</summary>
        public static void StartRecording()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            CSSALT_Mic_StartRecording();
#endif
        }

        /// <summary>Stop capture and flatten the chunks. Returns total sample count.</summary>
        public static int StopRecording()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return CSSALT_Mic_StopRecording();
#else
            return 0;
#endif
        }

        /// <summary>
        /// Copy the flattened buffer into <paramref name="buf"/>. Returns
        /// samples written (≤ maxLen). Should be called immediately after
        /// StopRecording.
        /// </summary>
        public static int CopySamples(float[] buf, int maxLen)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return CSSALT_Mic_CopySamples(buf, maxLen);
#else
            return 0;
#endif
        }
    }
}
