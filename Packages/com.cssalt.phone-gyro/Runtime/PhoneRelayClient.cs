using System;
using UnityEngine;
using NativeWebSocket;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
#endif

/*
================================================================================
 PhoneRelayClient
================================================================================

 Single shared WebSocket connection to the phone-gyro Node.js relay. Owns the
 connection lifecycle (open, reconnect, close) and raises MessageReceived for
 every incoming payload so that any number of subscribers — typically one or
 more PhoneGyroController components — can process the stream independently.

 USAGE
 -----
 Put this on the same GameObject that holds your PhoneGyroController
 component(s). PhoneGyroController has [RequireComponent(typeof(PhoneRelayClient))]
 so Unity will auto-attach this for you when you add a PhoneGyroController.
 Configure RelayHost / ServerUrl on this one component; the controllers no
 longer carry their own copy.

 If you want the relay on a different GameObject than the controllers, drag
 it into the PhoneGyroController.Relay field manually. With Relay = null the
 controller falls back to GetComponent on its own GameObject.

 EXECUTION ORDER
 ---------------
 [DefaultExecutionOrder(-100)] runs this component's Update before the
 controllers'. DispatchMessageQueue() fires MessageReceived synchronously,
 so subscribers see this frame's messages during this frame's Update — not
 the next frame's.
================================================================================
*/

[DefaultExecutionOrder(-100)]
public class PhoneRelayClient : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    //  CONFIGURATION
    // ─────────────────────────────────────────────────────────────────────────
    [Header("WebSocket")]

    // Used in the Editor and non-WebGL builds. In WebGL the URL is derived
    // from RelayHost + the room ID parsed from the page's URL — so the
    // WebGL build can be hosted anywhere while still pairing through the
    // central relay.
    public string ServerUrl = "ws://localhost:8080/ws?room=DEV&role=unity";

    [Tooltip("WebGL only. The Node relay host (no scheme). Change this if the relay moves. Keep in sync with RELAY_ORIGIN in WebGLTemplates/RoomQR/index.html.")]
    public string RelayHost = "cssalt.simongeronimo.com";

    [Tooltip("WebGL only. Path to the relay's WebSocket endpoint on RelayHost. Default \"/ws\" (relay at the site root). For a sub-path deployment behind a reverse proxy — e.g. an .htaccess proxy under /websims/gyro-relay/ — set the full path, e.g. \"/websims/gyro-relay/ws\".")]
    public string RelayPath = "/ws";

    public bool ReconnectOnFailure = true;
    public float ReconnectDelaySeconds = 2f;

    [Header("Debug")]
    [Tooltip("Log every incoming message body. Useful for protocol debugging; spammy at 30 Hz.")]
    public bool VerboseLogs = false;

#if UNITY_WEBGL && !UNITY_EDITOR
    // JS shim defined in the hosting page (RoomQR/index.html). Returns the
    // current page's full URL so we can regex out the room ID.
    [DllImport("__Internal")] private static extern string GetPageUrl();
#endif

    // ─────────────────────────────────────────────────────────────────────────
    //  EVENTS / SUBSCRIPTIONS
    // ─────────────────────────────────────────────────────────────────────────
    /// Raised on the main thread for every payload received from the relay.
    /// Subscribers get the raw bytes; parsing is the subscriber's concern.
    public event Action<byte[]> MessageReceived;

    /// True after the WebSocket has finished its initial handshake. Useful
    /// for status-display components that want to render a "connected" dot.
    public bool IsConnected => ws != null && ws.State == WebSocketState.Open;

    // ─────────────────────────────────────────────────────────────────────────
    //  RUNTIME STATE
    // ─────────────────────────────────────────────────────────────────────────
    WebSocket ws;
    bool reconnectScheduled;

    async void Start() { await Open(); }

    // Pump the message queue every frame so OnMessage fires on the main
    // thread. Required on desktop where NativeWebSocket uses a background
    // thread; the no-op on WebGL is safe (single-threaded browser).
    void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        ws?.DispatchMessageQueue();
#endif
    }

    // Builds the WebSocket URL to connect to.
    //
    // In WebGL we read the room ID from the hosting page's URL (the room ID
    // is set by RoomQR/index.html, typically encoded into a QR code that
    // the user scans with their phone). The relay hostname is fixed in the
    // inspector. This decoupling lets the WebGL build be hosted anywhere
    // while always pairing through the same relay.
    //
    // In the Editor we fall back to the inspector-configured ServerUrl so
    // development against a local relay (localhost:8080) just works.
    string BuildServerUrl()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        string page = GetPageUrl();
        var roomMatch = Regex.Match(page ?? "", @"room=([A-Z0-9]+)", RegexOptions.IgnoreCase);
        string room = roomMatch.Success ? roomMatch.Groups[1].Value : "DEV";
        return $"wss://{RelayHost}{RelayPath}?room={room}&role=unity";
#else
        return ServerUrl;
#endif
    }

    async System.Threading.Tasks.Task Open()
    {
        string wsUrl = BuildServerUrl();
        ws = new WebSocket(wsUrl);

        ws.OnOpen += () => Debug.Log($"[PhoneRelay] Connected to {wsUrl}");
        ws.OnError += (e) => Debug.LogError($"[PhoneRelay] WS error: {e}");
        ws.OnClose += (e) =>
        {
            Debug.Log($"[PhoneRelay] WS closed: {e}");
            // Schedule a single reconnect; the flag prevents multiple
            // pending reopens stacking up if OnClose fires more than once.
            if (ReconnectOnFailure && !reconnectScheduled)
            {
                reconnectScheduled = true;
                Invoke(nameof(Reopen), ReconnectDelaySeconds);
            }
        };
        ws.OnMessage += HandleIncoming;

        await ws.Connect();
    }

    async void Reopen() { reconnectScheduled = false; await Open(); }

    // ─────────────────────────────────────────────────────────────────────────
    //  SEND (Unity → phone)
    // ─────────────────────────────────────────────────────────────────────────
    /// Send a text (JSON) message up to the relay, which broadcasts it to the
    /// other peers in the room — i.e. the phone. No-op if the socket isn't open.
    /// This is the reverse channel (the relay is otherwise phone→Unity here);
    /// e.g. PhoneTargetDirectory uses it to reply to a `get-targets` request.
    public void Send(string json)
    {
        if (ws == null || ws.State != WebSocketState.Open) return;
        _ = ws.SendText(json); // fire-and-forget; SendText is async
    }

    // Internal handler: just forwards to subscribers. Lives here so the
    // raw bytes don't need to be deserialized twice if multiple controllers
    // (e.g. a PhoneGyroController per target) listen at once.
    void HandleIncoming(byte[] bytes)
    {
        if (VerboseLogs)
        {
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            Debug.Log($"[PhoneRelay] msg: {text}");
        }
        MessageReceived?.Invoke(bytes);
    }

    // Close the socket cleanly when this component is destroyed (scene
    // unload, app quit, host GameObject disabled+destroyed). Without this
    // the relay sees the socket dangle until its keepalive expires —
    // harmless but noisy.
    async void OnDestroy()
    {
        if (ws != null && ws.State == WebSocketState.Open) await ws.Close();
    }
}
