// WebMic.jslib
// jslib exports are prefixed CSSALT_Mic_ so they never clash in a shared build.
// Emscripten bridge that gives C# (on WebGL) the same push-to-talk mic capture
// UnityEngine.Microphone provides elsewhere.
//
// Design: build the audio graph ONCE at init, keep it running, and just toggle
// a "recording" flag on start/stop. Rebuilding the graph per press was flaky.

var CSSALTMicLib = {
    $CSSALTMicState: {
        audioCtx: null,
        micStream: null,
        sourceNode: null,
        workletNode: null,
        muteGain: null,
        chunks: [],
        lastBuffer: null,
        recording: false,
        ready: 0,
        initStarted: false,
    },

    CSSALT_Mic_Init: function () {
        var M = CSSALTMicState;
        if (M.ready || M.initStarted) return;
        M.initStarted = true;
        (async function () {
            try {
                if (!M.audioCtx) {
                    var AC = window.AudioContext || window.webkitAudioContext;
                    M.audioCtx = new AC();
                }
                if (M.audioCtx.state === "suspended") {
                    try { await M.audioCtx.resume(); } catch (e) {}
                }
                var workletSrc =
                    "class PCMCapture extends AudioWorkletProcessor {" +
                    "  process(inputs) {" +
                    "    var input = inputs[0];" +
                    "    if (input && input[0]) {" +
                    "      this.port.postMessage(input[0].slice(0));" +
                    "    }" +
                    "    return true;" +
                    "  }" +
                    "}" +
                    "registerProcessor('pcm-capture', PCMCapture);";
                var blob = new Blob([workletSrc], { type: "application/javascript" });
                var url = URL.createObjectURL(blob);
                await M.audioCtx.audioWorklet.addModule(url);
                M.micStream = await navigator.mediaDevices.getUserMedia({
                    audio: {
                        channelCount: 1,
                        echoCancellation: true,
                        noiseSuppression: true
                    }
                });

                // Build the graph ONCE and keep it running. Only the
                // recording flag gates whether chunks are buffered.
                M.sourceNode  = M.audioCtx.createMediaStreamSource(M.micStream);
                M.workletNode = new AudioWorkletNode(M.audioCtx, "pcm-capture");
                M.muteGain    = M.audioCtx.createGain();
                M.muteGain.gain.value = 0;

                M.workletNode.port.onmessage = function (ev) {
                    if (CSSALTMicState.recording) CSSALTMicState.chunks.push(ev.data);
                };

                M.sourceNode.connect(M.workletNode);
                M.workletNode.connect(M.muteGain);
                M.muteGain.connect(M.audioCtx.destination);

                M.ready = 1;
                console.log("[WebMic] ready @ " + (M.audioCtx.sampleRate | 0) + " Hz");
            } catch (e) {
                console.error("[WebMic] init failed:", e);
                M.ready = 0;
                M.initStarted = false;  // allow retry
            }
        })();
    },

    CSSALT_Mic_IsReady: function () {
        return CSSALTMicState.ready | 0;
    },

    CSSALT_Mic_GetSampleRate: function () {
        return CSSALTMicState.audioCtx ? (CSSALTMicState.audioCtx.sampleRate | 0) : 0;
    },

    CSSALT_Mic_StartRecording: function () {
        var M = CSSALTMicState;
        if (!M.ready) {
            console.warn("[WebMic] StartRecording called before ready");
            return;
        }
        if (M.recording) return;
        M.chunks = [];
        M.lastBuffer = null;
        M.recording = true;
    },

    CSSALT_Mic_StopRecording: function () {
        var M = CSSALTMicState;
        if (!M.recording) return 0;
        M.recording = false;
        var total = 0;
        for (var i = 0; i < M.chunks.length; i++) total += M.chunks[i].length;
        if (total === 0) {
            M.lastBuffer = null;
            M.chunks = [];
            return 0;
        }
        var merged = new Float32Array(total);
        var off = 0;
        for (var i = 0; i < M.chunks.length; i++) {
            merged.set(M.chunks[i], off);
            off += M.chunks[i].length;
        }
        M.lastBuffer = merged;
        M.chunks = [];
        return merged.length;
    },

    CSSALT_Mic_CopySamples: function (outPtr, maxLen) {
        var M = CSSALTMicState;
        if (!M.lastBuffer) return 0;
        var n = Math.min(maxLen, M.lastBuffer.length);
        // HEAPF32 is indexed in 4-byte floats; outPtr is a byte offset.
        HEAPF32.set(M.lastBuffer.subarray(0, n), outPtr >> 2);
        return n;
    },
};

autoAddDeps(CSSALTMicLib, "$CSSALTMicState");
mergeInto(LibraryManager.library, CSSALTMicLib);
