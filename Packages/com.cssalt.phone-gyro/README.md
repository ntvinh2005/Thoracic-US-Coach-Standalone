# Phone Gyro Controller

Drive Unity Transforms from a phone's gyroscope and on-screen joystick. Multiple phones can share a room and each control a different object, identified by a `target` tag attached to every message.

The relay (a small Node.js WebSocket server, in the `gyro-relay/` folder of [this same repository](https://github.com/CSSALT/Phone-6DOF-Controller)) brokers the connection between the phone's browser and the Unity build. The phone page is plain HTML — no app install required.

---

## Installing this package

In your Unity project's `Packages/manifest.json`:

```json
"com.cssalt.phone-gyro": "https://github.com/CSSALT/Phone-6DOF-Controller.git?path=/Package"
```

Pin to a specific release by appending a tag:

```json
"com.cssalt.phone-gyro": "https://github.com/CSSALT/Phone-6DOF-Controller.git?path=/Package#v1.0.0"
```

The package declares a dependency on [`com.endel.nativewebsocket`](https://github.com/endel/NativeWebSocket). Unity 2021.2+ will pull it automatically; on older versions, add it to your `manifest.json` manually:

```json
"com.endel.nativewebsocket": "https://github.com/endel/NativeWebSocket.git#upm"
```

---

## Quick start

1. **WebGL template**: in your Unity project, **Project Settings → Player → WebGL → Resolution and Presentation → WebGL Template**, pick **RoomQR**.
2. **Scene setup**: create an empty GameObject (e.g. `PhoneControllers`). Add a `PhoneGyroController` component to it — Unity will auto-attach a required `PhoneRelayClient` companion.
3. **On the `PhoneGyroController`**:
   - `TargetName` → a string the phone uses to address this controller. Conventionally `"probe"` for the first, `"needle"` for the second.
   - `TargetTransform` → drag the object this controller drives.
   - `RotationPivot` → drag a child Transform at the object's contact point. Rotation pivots here; lift buttons translate along its `MarkerOutwardAxis`.
   - `ForwardAxis`, `StrafeAxis`, `MarkerOutwardAxis` → which local axes of the target/pivot correspond to "forward" (joystick Y), "strafe right" (joystick X), and "away from skin" (lift buttons).
4. **For a second controllable object**: add another `PhoneGyroController` to the same GameObject. Give it a different `TargetName` (e.g. `"needle"`). The two share the same WebSocket via the single `PhoneRelayClient`.
5. **Build to WebGL** and host the output anywhere with HTTPS (itch.io, GitHub Pages, your own server). The WebGL page shows a QR code that pairs the laptop with a phone via the relay configured on `PhoneRelayClient.RelayHost` (defaults to `cssalt.simongeronimo.com`).

---

## Multi-phone use

Each phone scans the same QR (same room), then picks the object it wants to drive from the **Controlling** picker (filled in from the scene when a `PhoneTargetDirectory` is present; "Any / all objects" otherwise). The phone tags every outgoing message with its current target; controllers filter by tag.

Two phones picking the same target both drive that controller (last-write-wins on each field). There's no ownership tracking — coordinate by convention.

---

## Importing the sample

In **Window → Package Manager**, find this package, expand the **Samples** section, and click **Import** next to `Probe Click-to-Move`. The script is small (~50 lines) and demonstrates how to pair `ProbeClickToMove` with `PhoneGyroController.SetReferenceToCurrent()` so click-to-place doesn't fight phone control.

---

## Architecture

- **`PhoneRelayClient`** — owns the single WebSocket to the relay. Exposes `event Action<byte[]> MessageReceived`. `[DefaultExecutionOrder(-100)]` so messages dispatch before any controller's `Update`.
- **`PhoneGyroController`** — `[RequireComponent(typeof(PhoneRelayClient))]`. Subscribes to `Relay.MessageReceived` in `OnEnable`, drops messages whose `target` tag doesn't match its `TargetName`, applies orientation/translation/lift to its `TargetTransform`.
- **`UrlBridge.jslib`** — exposes `window.location.href` to C# in WebGL builds so the room ID can be parsed from the page URL.
- **`RoomQR` WebGL template** — generates a per-session room ID, encodes it into a QR code pointing at `<RelayHost>/phone?room=<id>`, writes the room into the page URL hash.

See the [`docs/`](https://github.com/CSSALT/Phone-6DOF-Controller/tree/main/docs) folder of the repo for the detailed architecture and coordinate-system notes.

---

## Relay configuration

The default relay host (`cssalt.simongeronimo.com`) is set in two coupled places:

- **`PhoneRelayClient.RelayHost`** — used in WebGL to construct the WebSocket URL.
- **`Package/WebGLTemplates/RoomQR/index.html`** → `RELAY_ORIGIN` constant — used for the QR code's phone URL.

If you stand up your own relay, change both. If you want a different relay for one project only, override `RelayHost` in the Inspector and serve your own copy of `index.html` (or fork the package and bump the constant).

---

## Changelog

See [`CHANGELOG.md`](./CHANGELOG.md).
