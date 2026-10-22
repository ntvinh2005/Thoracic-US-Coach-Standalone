// VoiceTTS.jslib
// jslib exports are prefixed CSSALT_TTS_ so they never clash in a shared build.
// Two browser TTS paths exposed to C#:
//   1. Browser native — window.speechSynthesis. Free, instant, robotic.
//   2. Deepgram      — POST /v1/speak?model=<voice>, play the MP3.

var CSSALTTTSLib = {
    $CSSALTTTSState: {
        currentAudio: null,
        currentBlobUrl: null,
        speechReady: 0,
    },

    CSSALT_TTS_BrowserSpeak: function (textPtr, rateBp, pitchBp) {
        var text = UTF8ToString(textPtr);
        if (!text) return;
        if (!window.speechSynthesis) {
            console.warn("[VoiceTTS] window.speechSynthesis unavailable");
            return;
        }
        // Chrome drops speak() called immediately after cancel() in the same
        // tick; defer via setTimeout so the cancel completes first.
        try {
            window.speechSynthesis.cancel();
            var rate = Math.max(0.1, Math.min(10.0, (rateBp | 0) / 100));
            var pitch = Math.max(0.0, Math.min(2.0, (pitchBp | 0) / 100));
            setTimeout(function () {
                var u = new SpeechSynthesisUtterance(text);
                u.rate = rate;
                u.pitch = pitch;
                u.onerror = function (e) {
                    console.error("[VoiceTTS] browser speak error: " + (e && e.error ? e.error : e));
                };
                window.speechSynthesis.speak(u);
            }, 0);
        } catch (e) {
            console.error("[VoiceTTS] browser speak failed:", e);
        }
    },

    CSSALT_TTS_BrowserCancel: function () {
        try {
            if (window.speechSynthesis) window.speechSynthesis.cancel();
        } catch (e) {}
    },

    // POST /v1/speak?model=<voice> with body {"text":"..."} and
    // "Authorization: Token <key>". Response is audio/mpeg by default; piped
    // into a Blob and played via an <audio> element.
    CSSALT_TTS_DeepgramSpeak: function (baseUrlPtr, apiKeyPtr, voicePtr, textPtr) {
        var baseUrl = UTF8ToString(baseUrlPtr);
        var key     = UTF8ToString(apiKeyPtr);
        var voice   = UTF8ToString(voicePtr);
        var text    = UTF8ToString(textPtr);
        if (!text || !key) {
            console.warn("[VoiceTTS] Deepgram missing text/key");
            return;
        }
        var endpoint = baseUrl.replace(/\/$/, "") + "/v1/speak?model=" +
                       encodeURIComponent(voice || "aura-2-hyperion-en");
        var body = JSON.stringify({ text: text });

        var S = CSSALTTTSState;
        if (S.currentAudio) {
            try { S.currentAudio.pause(); } catch (e) {}
            S.currentAudio = null;
        }
        if (S.currentBlobUrl) {
            try { URL.revokeObjectURL(S.currentBlobUrl); } catch (e) {}
            S.currentBlobUrl = null;
        }

        fetch(endpoint, {
            method: "POST",
            headers: {
                "Authorization": "Token " + key,
                "Content-Type": "application/json",
            },
            body: body,
        }).then(function (r) {
            if (!r.ok) {
                return r.text().then(function (t) {
                    throw new Error("HTTP " + r.status + " " + r.statusText + " — " + t);
                });
            }
            return r.blob();
        }).then(function (blob) {
            var url = URL.createObjectURL(blob);
            var a = new Audio(url);
            S.currentAudio = a;
            S.currentBlobUrl = url;
            a.addEventListener("ended", function () {
                if (S.currentBlobUrl === url) {
                    URL.revokeObjectURL(url);
                    S.currentBlobUrl = null;
                    S.currentAudio = null;
                }
            });
            return a.play();
        }).catch(function (e) {
            console.error("[VoiceTTS] Deepgram failed:", e && e.message ? e.message : e);
        });
    },

    CSSALT_TTS_DeepgramCancel: function () {
        var S = CSSALTTTSState;
        if (S.currentAudio) {
            try { S.currentAudio.pause(); } catch (e) {}
            S.currentAudio = null;
        }
        if (S.currentBlobUrl) {
            try { URL.revokeObjectURL(S.currentBlobUrl); } catch (e) {}
            S.currentBlobUrl = null;
        }
    },
};

autoAddDeps(CSSALTTTSLib, "$CSSALTTTSState");
mergeInto(LibraryManager.library, CSSALTTTSLib);
