# Changelog

All notable changes to the **Phone Gyro Controller** package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.2.0] — 2026-09-22

*Template change: consumers get it by bumping the package tag and **rebuilding WebGL** (the installer re-copies `RoomQR` into `Assets/WebGLTemplates/` when the package version changes). Editor-only C# added for CI builds; no runtime code or wire-protocol change. Verified in desktop Chromium against a local relay with a stub Unity loader, and the file passes Unity's own template preprocessor from the 2020.3, 2021.3, 2022.3 and 6000.3 editors installed on the dev machine. Built headlessly by the GitLab pipeline for OR_Room_Tour (Unity 6000.3.8f1) and deployed to a staging folder on vam on 2026-09-22: the guide showed, a physical phone paired through the QR code and drove the simulator. Tested with one physical phone; **not verified on both iOS and Android**.*

### Added
- **`PhoneGyroCIBuild.BuildWebGL` (Editor).** Headless WebGL build entry point for the simulators' GitLab pipeline (`ci/webgl-deploy.yml` in the repo): installs the `RoomQR` template synchronously (the `[InitializeOnLoad]` installer is not guaranteed to run before `-executeMethod`), patches `RELAY_ORIGIN` / `SHOW_CAMERA_TRACKING` from environment variables, forces Gzip + decompression fallback, and fails the build if any enabled scene's `PhoneRelayClient` host/path disagree with the template's `RELAY_ORIGIN`. `RoomQRTemplateInstaller.Install` is now `internal` and reports whether it ran; the Editor assembly references the Runtime assembly for this.
- **Pairing guide in the `RoomQR` WebGL template.** The small top-right QR panel is replaced by a centred card shown over the canvas while Unity loads: a large QR code, the room code, the link to type as a fallback, three steps (Scan the code, Tap Start, Tilt to steer), and a "Two ways to move it" pair that explains the joystick (default) and **Camera tracking** (VIO: pick it in the phone's Settings, allow the camera, then move the phone itself), all in the same visual language as the phone page. It follows `prefers-color-scheme` and `prefers-reduced-motion`.
- **The guide notices the phone.** The page joins its own room as a silent extra peer, waits for the first message stamped with this browser's own `me` id, then closes that socket. The steps tick, the status turns to "Phone connected", and the card folds into a corner chip after 3.5 s. No C# or jslib involvement. It joins with `role=phone` on purpose: a `unity` peer would make the relay send `unity-connected` to every phone in the room and reset their UI. Filtering on `me` keeps a second player's page from reacting to the first player's phone in a shared room. A phone opened from a hand-typed link without `&me=` is not detected, and if the relay is unreachable nothing is detected; in both cases the guide simply closes by hand.
- "Continue without a phone", Escape, and a click on the dimmed backdrop all dismiss the guide; the corner chip ("Pair your phone", then "Phone connected") reopens it. Keyboard focus follows the card as it opens and closes, and the connection is announced to screen readers from a live region outside the card. When the build itself runs on a phone the guide starts collapsed.
- **`SHOW_CAMERA_TRACKING` switch in the template** (default `true`). Set it to `false` in joystick-only simulators, e.g. skin-following probe sims, and the guide stops advertising Camera tracking. It only changes the guide's text; the option itself lives in the phone page's Settings.
- If the QR image service is unreachable the guide says so and points at the link, instead of showing a broken image.
- **"Tilt to steer" ticks when the phone is actually tilted** (more than 20° from its pose at Start, on any axis) and the card closes 2 s later; a phone that connects but is never tilted closes it after 12 s. The page's listener stays in the room until that first tilt instead of leaving on the first message. "Scan the code" and "Tap Start" still tick together: the phone page only contacts the relay when Start is tapped.

### Changed
- **One `RELAY_ORIGIN` constant in the template** replaces the hardcoded `phoneUrl` line. The QR link and the phone detection are both derived from it. (The README and the `RelayHost` tooltip already referred to a `RELAY_ORIGIN` constant that did not exist.)
- Page title is the product name alone, without the "Unity Web Player |" prefix.
- A failed Unity load shows Unity's red banner instead of a blocking `alert()`. `#unity-warning` moved to body level (fixed, above the guide) so loader warnings and errors are never hidden behind the card, and any error banner collapses the guide.
- QR image is requested at 440 px and shown at 220 px so it stays sharp on high-density laptop screens.
- **A simulator opened over plain `http://` reloads itself over https**, before Unity starts, when it is served from the relay's own host. An insecure page has no `navigator.mediaDevices` (microphone) and other secure-context APIs, so a simulator's own voice or camera code throws there. `localhost` and LAN test servers are left alone.

## [2.1.0] — 2026-09-17

*Relay-side release: only `gyro-relay/phone.html` and the docs changed. The Unity package contents are identical to 2.0.1, the wire protocol is unchanged, and no WebGL rebuild is needed. To pick it up, deploy the updated `phone.html` to the relay folder. Verified in desktop browsers against a local relay; **not yet tested on a physical iOS or Android phone**, and the Camera tracking (8th Wall) engine was not run against the new layout.*

### Added
- **Start screen.** The phone page opens on a single **Start** button (motion permission + connect in one tap) instead of the control panel with an "Enable Gyro" button. A missing `?room=` and a denied motion permission each get their own plain-language state instead of a status-line string. A room code the relay rejects (close code 1008) now stops with "Invalid room code" instead of reconnecting forever.
- **Settings sheet** (native `<dialog>`). Holds the translation source switch (**Joystick / Camera tracking**, formerly "Source: Joystick / VIO"), the **Tilt mapping** switch (iOS / Android, formerly the "Tilt:" button), and the raw α β γ + joystick readouts. Every control that existed before still exists; the debug ones are no longer the first thing a visitor sees.
- **Connection state in a status bar.** A dot + one line: grey before Start, teal connected, amber connecting/reconnecting, red error. The joystick dims while the socket is not open, so it is visible when input is going nowhere. The status line is a screen-reader live region and is only rewritten when the state actually changes, not on every reconnect attempt.
- **Screen Wake Lock request** on Start (and again when the page becomes visible). Tilting is not "user activity", so a phone can dim and lock mid-session, which stops `deviceorientation` and freezes the controlled object. Best effort: silently skipped where the API is missing or refused.
- **Light theme** (follows `prefers-color-scheme`) alongside the dark one, a **landscape layout**, and `viewport-fit=cover` safe-area padding.

### Changed
- **Layout rebuilt around a larger, centred joystick.** 143 px → 300 px wide on a 375 px phone. The lift / press buttons moved from flanking the joystick to a two-arrow row beneath it, directly above the primary actions. No text on the joystick or the lift buttons (screen-reader labels kept).
- **"Align Phone to Obj" is now "Align to object"** and is the primary bottom action, next to **Settings**. It still sends `zero` + `pose-zero`. The tap is acknowledged with a brief "Aligned" status.
- **"VIO" is called "Camera tracking" in the UI.** Wire values (`translation-source: 'vio'`, `pose-translate`) are unchanged. Choosing it closes the sheet so the camera preview and its permission prompt are visible. A VIO start failure shows a plain message with the XR8 diagnostic string in small text beneath, instead of dumping it into the status pill. The camera preview now fills the area the joystick occupied instead of the 140x100 px thumbnail from 1.2.0.
- **Page title** "Probe Gyro" → "Phone Controller"; the on-screen "Probe Gyro Sender" heading is gone. The "Player &lt;me&gt;" badge moved beside the "Controlling" label.
- Docs updated to the new control names (`docs/01`, `02`, `04`, `05a`, `05b`, `06`, `07`, `deploy/`), and the Package README's multi-phone section now describes the object picker rather than the pre-1.3.0 Target button.

### Fixed
- **The "Controlling" row no longer overflows the right edge of the screen** on a 375 px-wide phone when the Player badge is shown (the object picker was clipped).

### Removed
- The defensive capture-phase `scroll` listener that re-measured the joystick. The page cannot scroll; `ResizeObserver`, `resize`, and the re-measure on `pointerdown` already cover it.

### Notes
- The iOS rule from 1.2.1 is preserved exactly: no `setPointerCapture` / `preventDefault` on the joystick or lift buttons, so `deviceorientation` keeps firing during a drag.
- Requirements: the settings sheet needs `<dialog>` (iOS Safari 15.4+; on older Safari the sheet stays hidden and the rest of the page works); joystick sizing uses container-query units (iOS Safari 16+) with a 255 px fallback bounded by the available height. The spring-back of the joystick thumb, like every other animation on the page, is off under `prefers-reduced-motion`. The page still loads no external fonts, icons, or scripts on open; the 8th Wall engine is still fetched only when Camera tracking is first chosen.

## [2.0.1] — 2026-07-28

### Fixed
- **A controller no longer holds its object forever after the phone stops driving it.** `hasReading` was a write-once latch: once a controller saw a single `orientation` message it wrote `TargetTransform.rotation` every frame for the rest of the session, re-applying the *last* reading it received. With the phone's picker in its default "Any / all objects" mode (empty target, which every controller accepts) one connection latched **every** controller in the scene. From then on each one pinned its object to a frozen rotation, so any other system sharing that object — an LLM commander, a scripted animation — would visibly move it a few degrees and have it snap straight back, every frame. Symptoms included the object staying stuck **after the phone page was closed**, and the only cure being to disable the host GameObject. Controllers now release their object and re-acquire cleanly; `Calibrate()` on re-acquisition anchors to wherever the other system left it, so handoff in both directions is snap-free.
  - **Deselection is now heard.** A `target` message naming a *different* object was dropped by the routing filter before the handler ran, so a controller could never learn it had been deselected — picking "needle" on the phone left the probe's controller driving the probe. Selection changes are now processed *before* the filter.
  - **Silence releases too** — new **`InputTimeoutSeconds`** (default 1 s). Closed tabs, backgrounded browsers, and dropped sockets produce no message at all, so a clean deselect can't be relied on. Set to 0 or negative to disable.
  - **Stale joystick/lift/VIO input is cleared on release.** A joystick held at the moment a connection dropped would otherwise translate the object forever — the same class of bug as the rotation latch, on the translation path.
  - A released controller is **revived by being addressed again**, including by the empty-target broadcast, so a phone page that reloads (back in "Any / all objects" mode, and which never re-sends a `target` message) resumes control as expected.

## [2.0.0] — 2026-07-13

> **⚠️ BREAKING — relay host cutover.** The default relay moved from
> `cssalt.simongeronimo.com` (being decommissioned) to UF at
> `vam.anest.ufl.edu/websims/gyro-relay`. Every consumer must bump to `#v2.0.0`,
> **rebuild** its WebGL, and set `PhoneRelayClient.RelayHost` = `vam.anest.ufl.edu`
> and `RelayPath` = `/websims/gyro-relay/ws`. The QR link and phone page follow the
> new host automatically (the template's `phoneUrl` was updated). Any build still
> pointing at the old host stops working once it's turned off.

### Added
- **`PhoneRelayClient.RelayPath`** (WebGL) — configurable path to the relay's WebSocket endpoint (default `/ws`). Lets a WebGL build reach a relay hosted under a **sub-path** behind a reverse proxy (e.g. `/websims/gyro-relay/ws`, an `.htaccess` proxy on a shared Apache host) instead of only the site root. Backward-compatible: unset → `/ws` as before.
- **Deployment "Pattern C"** in `docs/05-deployment.md` — shared Apache sub-folder via `.htaccess` (no admin), plus `gyro-relay/.htaccess` as a tracked deploy artifact.

### Changed
- **`phone.html` is now the single phone page — VIO/free-fly folded in from the old `phone-dev.html`.** One page with everything: joystick, gyro, object picker, player badge, and VIO (Source toggle → 8th Wall visual-inertial tracking). `phone-dev.html` and its `/phone-dev` route in `server.js` are removed.
- **8th Wall engine loads on demand**, not eagerly in `<head>` — it's injected the first time the user taps Source → VIO, so joystick-only phones (probe/needle sims) never download the multi-MB WASM binary.
- **RoomQR template QR points at the UF relay host** (`vam.anest.ufl.edu/websims/gyro-relay/phone.html`) instead of the retired `cssalt.simongeronimo.com` stopgap.
- **Phone page derives its `/ws` endpoint from its own URL** instead of a hardcoded root `/ws`, so it works when served behind an `.htaccess` reverse-proxy under a sub-path (e.g. `…/websims/gyro-relay/`) as well as at a site root.

### Fixed
- **Repeated "Enable Gyro" taps no longer stack WebSocket connections** — each tap used to open a new socket (leaking a peer on the relay every time) and a stale socket's `onclose`-reconnect compounded it. `connect()` now keeps the existing socket if one is already connecting/open. Same taps also **no longer duplicate the `deviceorientation` listener** (`startGyro()` attaches it once).
- **`pose-translate` (VIO) now carries `me`** — it was the only phone→Unity message missing the client id, so VIO free-fly wouldn't filter to the right browser in multiplayer.

## [1.4.0] — 2026-07-13

### Added
- **Room join by URL + per-browser client id (`me`) — foundation for multi-browser sessions.** The RoomQR template now:
  - **Honors a room already in the page URL** (`?room=` / `#room=`) instead of always minting a fresh one, so a second browser can join the first's room by opening its link.
  - **Mints a stable per-browser client id and bakes it into the page URL (`#…&me=`) and the phone's QR link (`&me=`).** The phone echoes this `me` on every message it sends, so a host running two rigs in one room can tell which browser (and thus which avatar) a phone is driving.
  - Both are additive and backward-compatible: no room in the URL → mint as before; a page/phone with no `me` → empty, and consumers treat empty as single-player (no filtering). **No relay change.**
- **Phone page reads `me` and shows a read-only "Player &lt;me&gt;" badge** (in the "Controlling:" row) so each operator can see which browser/avatar their phone is bound to. Hidden in single-player (no `me`). Applies to both `phone.html` and `phone-dev.html`.

## [1.3.0] — 2026-06-29

### Added
- **Dynamic object picker — the phone lists what Unity can control.** Replaces the hardcoded probe/needle toggle. Unity advertises its controllable objects and the phone shows a dropdown, so any number of named objects (probe, needle, camera, instruments…) can be chosen without editing the phone page.
  - New component **`PhoneTargetDirectory`** (put it on the shared host GameObject next to `PhoneRelayClient`): on a `{type:'get-targets'}` request it gathers the active `PhoneGyroController`s and replies `{type:'targets', targets:[{name,label},…]}`.
  - New optional **`DisplayName`** field on `PhoneGyroController` — friendly label for the picker (falls back to `TargetName`; the wire tag stays `TargetName`).
  - New **`PhoneRelayClient.Send(string)`** — the previously-missing Unity→phone reverse channel.
  - **"Control anything" is the baseline.** The phone sends an empty `target` by default, which every controller accepts — so an older/unknown build with no directory still works with no switching. The picker is always visible with **"Any / all objects"** (empty target) as the standing default; Unity's advertised objects are appended when a `targets` reply arrives. Progressive enhancement; nothing regresses.
  - Protocol is additive and needs **no relay change** — the request and reply ride the existing blind room broadcast.

### Removed
- Deleted the unused demo Unity project (`Phone-DOF-Controller-Unity-Project/`) — a dev harness nothing referenced. No effect on the package.

## [1.2.1] — 2026-06-29

### Added
- **QR pairing panel can be closed.** The WebGL template's QR panel now has a close (×) button that collapses it to a small "Show QR" chip in the corner (click to reopen). Useful when the WebGL build itself is run on a phone and the QR is in the way.
- **`Tools → Phone Gyro → Reinstall WebGL Template`** menu item — forces a re-copy of the WebGL template into `Assets/WebGLTemplates/` on demand and logs the result. Escape hatch for when Unity hasn't recompiled the package assembly (template-only edits, or local-package script changes Unity didn't pick up).

### Changed
- **`RoomQRTemplateInstaller` now refreshes the template by content for local/embedded packages.** Previously it gated purely on the package version, so editing the template while consuming the package via a `file:`/embedded reference never propagated (the version was static) — you had to bump the version to test a template change. Now, for mutable installs (`PackageSource.Local` / `Embedded`) it compares the template's actual file contents and re-copies whenever they differ; immutable git/registry installs keep the cheap version-gate. Local package testing of template changes now works with no version bump.

### Fixed
- **VIO translation now follows the same axes as the joystick.** Previously, `ApplyVIOTranslation` dumped the phone's position delta straight onto Unity world X/Y/Z, while the joystick moved the object along its configured `ForwardAxis` / `StrafeAxis` / `MarkerOutwardAxis`. The two sources disagreed on every direction. VIO now decomposes the phone motion into forward / strafe / lift amounts and routes each onto the **same object axis the joystick uses**, also honoring `InvertTranslation`, `StandOnOtherSide`, and each axis's `AxisSpace`. Moving the phone forward now matches pushing the joystick forward.
  - Phone-axis → control-axis mapping: phone Z = forward, X = strafe, Y = lift. The `InvertVIOX/Y/Z` flags now fix the sign of strafe / lift / forward respectively (tooltips updated).
  - `MaxVIORange` clamp moved from world-axis space to **control-axis space** (max travel along forward / strafe / lift), which is more meaningful and independent of scene orientation.
  - Known limitation: the engine's horizontal frame is fixed at origin-capture, so if forward/strafe feel *swapped* (not merely reversed) on a device, a forward/strafe swap option is still needed — the invert flags only handle sign. Full removal needs aligning the VIO frame to the scene via the phone's heading.
- **Rocking the phone no longer drifts the object in VIO mode (lever-arm suppression).** 8th Wall reports the camera/IMU position, which sits a few cm from the user's hand pivot; rotating ("rocking") the phone swings that sensor along an arc — a real translation that VIO reported and the probe followed. Now, when the phone's angular speed exceeds `VIORockSuppressionDegPerSec` (new Inspector field, default 60 deg/sec; 0 disables), VIO translation is held — the object rotates (gyro, unchanged) without translating. While held, the origin is continuously re-anchored to the phone's current position so motion resumes cleanly with no jump once rocking slows. Angular speed is derived Unity-side from the existing gyro `orientation` stream (`Quaternion.Angle` between successive readings) — **no protocol or phone-side change**. Hysteresis (engage at the threshold, release at half) prevents clutch chatter.
- **No teleport when VIO tracking recovers from limited/lost.** While tracking is degraded the engine's reported position drifts/jumps; the object was correctly frozen, but `vioCurrentPosition` kept absorbing those bad readings, so the first healthy frame produced a huge delta and the object teleported. Now the origin **re-anchors on recovery** — detected either by a tracking transition (limited/lost → normal) or by a gap in the pose stream longer than `VIOReanchorGapSeconds` (new Inspector field, default 0.5 sec; covers the case where the phone goes silent during a `lost` spell). The object holds where it was frozen and resumes smoothly from there. The gap check is universally safe: worst case it re-anchors when not strictly needed, which just means no movement that frame.
- **iOS: the gyro no longer freezes while dragging the joystick / holding a lift button.** On iOS Safari, `setPointerCapture` + `preventDefault` on a touch engages the gesture system and suspends `deviceorientation` events for the duration of the touch — so look (gyro) froze while walking. The relay's `phone.html` and `phone-dev.html` now track the drag with window-level pointer listeners (no capture, no preventDefault; scrolling stays blocked by the elements' `touch-action: none` CSS), so orientation keeps firing during a drag and look + move work together. *Relay-side change — deploy the updated phone pages to the relay; not part of the WebGL build.*

## [1.2.0] — 2026-06-23

Released from branch `vslam-translation` (branch name retained for git history; the feature is referred to as **VIO** in code and UI from this version onward — see *Terminology* note below). License posture: 8th Wall is community-maintained under the XR Engine License after Niantic's hosted platform shut down (Feb 2026). Dev/research use is permitted; institutional sign-off may be required before broad production distribution.

### Terminology

The underlying pose-source library is a **visual-inertial SLAM (VI-SLAM)** engine (8th Wall's WASM binary). Our integration consumes **only the per-frame metric position output** — the engine's mapping, relocalization, and `recenter()` features are not invoked. Behaviorally, our integration is a **visual-inertial odometry (VIO)** consumer of a VI-SLAM library, so the mode is named **VIO** throughout the codebase, Inspector fields, wire protocol, and phone UI. Earlier development commits on this branch called it "VSLAM" — code-level renames are tracked by `[FormerlySerializedAs]` attributes on the Unity Inspector fields so existing scenes keep their settings across the rename.

### Added
- **VIO as an alternate translation source.** Physically moving the phone in space now drives the controlled object's world position. Rotation is unchanged — it stays on `DeviceOrientationEvent`, because VI-SLAM-derived rotation has two failure modes pure gyroscope doesn't (sudden jumps on feature re-localization, drift on featureless surfaces).
- **Per-controller `TranslationSource` enum** with values `Joystick` (default, existing behavior) and `VIO`. The phone's new Source button toggles this at runtime via a `translation-source` message; the Inspector value is the boot default.
- **`VIOScale`** Inspector field (default `1000`): scene-units per phone-meter. 1 m of phone motion → 1000 mm of object motion at 1:1, suitable for the spine-RA mm-scale scenes.
- **`InvertVIOX/Y/Z`** Inspector booleans: per-axis flips for matching the engine's world frame to the scene frame. Mirror the existing `InvertPitch/Yaw/Roll` pattern.
- **`MaxVIORange`** Inspector field (default `200`, scene units): half-side of a cube centered on the VIO origin. Each axis of the scaled delta is clamped to ±this value so the controlled object can't drift more than `MaxVIORange` from where Align Phone to Obj last anchored it. `0` or negative disables.
- **`VIOSmoothing`** Inspector slider (range `0..0.95`, default `0.5`): exponential moving-average filter on the incoming position to suppress at-rest jitter. `0` = raw signal, higher = smoother but laggier.
- **Align Phone to Obj also returns the object to its initial position.** The phone's Zero button now sends both `zero` (re-calibrates gyroscope) and `pose-zero` (resets VIO origin AND snaps the controlled object back to `initialWorldPosition`). `initialWorldPosition` is captured at `Awake()` and refreshed on target switch / `SetReferenceToCurrent()`, so click-to-place flows update the home automatically.
- **New wire messages** (additive, no breaking changes against legacy joystick-only builds):
  - `pose-translate` `{ pX, pY, pZ, tracking }` — phone position in metric meters, plus a `'normal'` / `'limited'` / `'lost'` tracking status. Sent ~30 Hz while VIO is active.
  - `pose-zero` — re-anchors the VIO origin to the phone's current position and snaps the controlled object back to its initial world position. The Align button sends this alongside `zero`.
  - `translation-source` `{ source: 'joystick' | 'vio' }` — phone reports its current source toggle. Older Unity builds drop this silently.
- **Tracking-status pill** at the top of the phone UI in VIO mode: green (normal — probe follows phone), yellow (limited — probe frozen), red (lost — probe frozen, camera covered or featureless view). The probe freezes on any non-normal status so jumpy tracking data is never fed to the scene.
- **Small camera preview** (140x100 px) in VIO mode so the user can see what the camera is aimed at when debugging "why is tracking limited?".
- **`phone-dev.html`** served at a new `/phone-dev` route on the relay (added to `server.js`). The production `/phone` endpoint is untouched, so existing simulators that only use joystick continue to see no change. Test VIO by manually opening `<relay>/phone-dev?room=XXXXXX` on the phone instead of scanning the QR.

### Fixed
- **Stale-state bug on Joystick → VIO toggle.** Previously, switching back to VIO from Joystick captured the origin against a stale `vioCurrentPosition` (frozen at the last Joystick→VIO transition while the phone had moved physically). The first new pose-translate then produced a large delta and the object visibly jumped. Now the `translation-source` handler resets `hasVioReading = false`, which routes the next incoming pose-translate through the "first reading" branch — vioCurrentPosition is reassigned from the raw value and CapturePoseOrigin is called with fresh data. No jump.

### Notes
- The 8th Wall WASM binary loads from `https://cdn.jsdelivr.net/npm/@8thwall/engine-binary@1/dist/xr.js` lazily on the first Source → VIO tap with `data-preload-chunks="slam"` so the XrController submodule is available. Projects that never use VIO pay no runtime cost.
- iOS Safari requires `XR8.run()` to be invoked from a user gesture (it chains a `getUserMedia` camera-permission prompt). The Source button click satisfies this.
- The joystick + lift code path is untouched in `Joystick` mode and not invoked at all in `VIO` mode — the two sources are mutually exclusive per controller frame.
- License posture: 8th Wall is community-maintained under the XR Engine License after Niantic's hosted platform shut down (Feb 2026). Dev/research use is fine; production/public distribution requires institutional sign-off. **Do not tag v1.2.0 until this is confirmed.**

## [1.1.0] — 2026-05-22

### Added
- **Per-axis Local/World space choice for translation.** A new `AxisSpace` enum (`Local | World`) is now selectable next to each existing axis enum on `PhoneGyroController`:
  - `ForwardAxisSpace` — applies to joystick-Y (and to the forward direction projected onto the skin in skin-following mode).
  - `StrafeAxisSpace` — applies to joystick-X.
  - `MarkerOutwardAxisSpace` — applies to lift buttons and to the skin raycast direction.
  
  When set to `Local` (default), the axis follows the controlled object's rotation, same as before. When set to `World`, the axis is fixed in scene space — pushing the joystick forward moves the object along world +Z regardless of how the phone has rotated it. Useful for cameras, free-floating widgets, or any rig where the user thinks of translation in scene coordinates rather than object-local.
- New static helpers: `WorldAxis(ProbeAxis)` and `ResolveAxis(Transform, ProbeAxis, AxisSpace)`. `ProbeAxisWorld` (the original local-space resolver) is unchanged and still used internally by `ResolveAxis` when space = Local.

### Notes
- Backward compatible. All three new fields default to `AxisSpace.Local`, so existing scenes deserialize unchanged.
- Skin-following mode (`SlideAlongSkin = true`) does honor `MarkerOutwardAxisSpace`. If the patient mesh is tilted in the scene and you use `World` for the outward axis, the raycast direction stops tracking the patient — keep that axis on `Local` for tilted patients.

## [1.0.4] — 2026-05-22

First version of the WebGL template auto-installer that actually works end-to-end. v1.0.1, v1.0.2, and v1.0.3 each fixed one blocker and exposed the next; v1.0.4 ships the working combination.

### Fixed
- `RoomQRTemplateInstaller` had a `CS0104` ambiguity on `PackageInfo`: both `UnityEditor` and `UnityEditor.PackageManager` expose a `PackageInfo` type, and `using` both caused the Editor assembly to fail compilation. The installer never loaded. Resolution: dropped the `using UnityEditor.PackageManager` directive and fully-qualified `UnityEditor.PackageManager.PackageInfo` at the one call site, with a comment so future edits don't reintroduce it.

### Cumulative summary (what works as of v1.0.4)

On every Editor load in a project that consumes this package:

1. The Editor assembly compiles cleanly (CS0104 fixed in v1.0.4).
2. The Editor folder and its assets have `.meta` files committed in the package, so Unity loads them (fixed in v1.0.3).
3. `RoomQRTemplateInstaller` resolves the package's real filesystem path via `PackageInfo.FindForAssembly` (fixed in v1.0.2), which works for any install method (git URL, embedded, local file://).
4. The installer copies `WebGLTemplates/RoomQR/` from the package into the project's `Assets/WebGLTemplates/RoomQR/`, writes a `.phone-gyro-version` stamp, and triggers `AssetDatabase.Refresh()` (v1.0.1).
5. The template appears as `PROJECT:RoomQR` in Project Settings → Player → WebGL → WebGL Template.

## [1.0.3] — 2026-05-22

### Fixed
- Added missing `.meta` files for `Package/Editor/` and the two files inside it (`CSSALT.PhoneGyro.Editor.asmdef` and `RoomQRTemplateInstaller.cs`). Unity treats packages as immutable, so it won't generate meta files at consume time — they must be committed into the package. Without them, Unity refused to load the Editor assembly with the warning:
  > Asset Packages/com.cssalt.phone-gyro/Editor/CSSALT.PhoneGyro.Editor.asmdef has no meta file, but it's in an immutable folder. The asset will be ignored.
  
  Result: `RoomQRTemplateInstaller` never ran. v1.0.3 ships the missing metas with stable GUIDs.

## [1.0.2] — 2026-05-22

### Fixed
- `RoomQRTemplateInstaller` (introduced in v1.0.1) didn't actually fire when the package was installed via git URL. The installer was checking `Directory.Exists("Packages/com.cssalt.phone-gyro")` on the filesystem, but for git-installed packages that's a virtual AssetDatabase path — the real files live under `Library/PackageCache/com.cssalt.phone-gyro@<hash>/`, so the check always failed and the script silently bailed.
- Fix: resolve the package's real filesystem path via `PackageInfo.FindForAssembly(typeof(RoomQRTemplateInstaller).Assembly)`. Works for git-URL, embedded, and local-file:// installs.
- Replaced ad-hoc regex of `package.json` with `packageInfo.version`.

## [1.0.1] — 2026-05-22

### Added
- **`RoomQRTemplateInstaller`** (Editor-only) — on every Editor load, copies `Packages/com.cssalt.phone-gyro/WebGLTemplates/RoomQR/` into `Assets/WebGLTemplates/RoomQR/` so the template reliably appears as `PROJECT:RoomQR` in the WebGL Template dropdown. Uses a `.phone-gyro-version` stamp file to detect package upgrades and re-copy when the package version changes. Local edits to `Assets/WebGLTemplates/RoomQR/` are overwritten on the next package bump — rename the folder if you need a project-specific custom template.
- New Editor assembly definition `CSSALT.PhoneGyro.Editor.asmdef`.

### Fixed
- WebGL template in package was not reliably picked up by Unity's WebGL Template dropdown depending on Editor version. Auto-installer (above) routes around this by copying into the consuming project's `Assets/`.

## [1.0.0] — 2026-05-26

First UPM-packaged release. Extracted from the `Phone-6DOF-Controller` test project into a reusable Unity Package Manager package so downstream projects (Thoracic-Spine-Anesthesia-Simulator, future medical-sim builds) can pull it via git URL instead of file-copying.

### Added
- **`PhoneGyroController`** — drives a Unity `Transform` from a phone's W3C `DeviceOrientationEvent` (rotation) plus an on-screen joystick (forward + strafe translation) and lift buttons (translation along `MarkerOutwardAxis`).
- **`PhoneRelayClient`** — shared `WebSocket` connection to the relay. Auto-attached by `[RequireComponent]` when adding a `PhoneGyroController`. Centralizes `RelayHost` / `ServerUrl` so multiple controllers in one scene share one socket.
- **Per-message `target` tagging** — phone tags every `orientation` / `translate` / `lift` / `zero` message with the currently selected object name; controllers filter by `TargetName`. Enables multiple phones in the same room to control different objects without cross-talk.
- **Per-axis rotation inversion** — `InvertPitch` / `InvertYaw` / `InvertRoll` flags negate a single component of the phone quaternion at calibration and per-frame, so one axis can be flipped without affecting the other two. Useful when an object's local axes are authored with non-standard handedness.
- **Configurable `ForwardAxis` and `StrafeAxis`** — both joystick axes pick their target-local direction from a `ProbeAxis` enum; defaults are `Down` (forward) and `Right` (strafe), but a needle or other object with mirrored handedness can flip either independently.
- **Skin-following translation** — when `SlideAlongSkin` is on, joystick movement projects onto the skin surface via a raycast against `SkinLayer`, with the probe sticking to curved anatomy via cross-product lateral tangents.
- **Lift buttons (↑ / ↓)** — translate along `MarkerOutwardAxis` at `LiftSpeed`. Independent of joystick translation; can be held during a tilt without conflict.
- **Operator-side compensation** (`StandOnOtherSide`) — mirrors tilt and rock while preserving twist, for procedures where the operator views the object from the opposite side.
- **`SetReferenceToCurrent()`** — external hook for any script that programmatically re-orients the controlled object (e.g. click-to-place). Re-anchors the gyro's home pose to the new orientation.
- **`RoomQR` WebGL template** — generates a per-session 6-character room ID, encodes a QR code pointing at `<RelayHost>/phone?room=<id>`, writes `#room=<id>` to the page URL so Unity can parse it back out.
- **`UrlBridge.jslib`** — JavaScript shim that exposes `window.location.href` to C# in WebGL builds.
- **Sample**: `Probe Click-to-Move` — example script for clicking on a skin mesh to position the probe at the contact point with its face flush against the surface normal.

### Notes
- Default relay host is `cssalt.simongeronimo.com`. Change `PhoneRelayClient.RelayHost` (C#) **and** `RELAY_ORIGIN` in `Package/WebGLTemplates/RoomQR/index.html` if you stand up your own relay.
- Unity minimum: `2020.3`. The package uses `[DefaultExecutionOrder]` and `[FormerlySerializedAs]` from `UnityEngine.Serialization`; both are available in 2018+ but the WebGL template + jslib placement assume the 2020.3+ UPM behavior.

[1.0.0]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.0.0
[1.0.1]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.0.1
[1.0.2]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.0.2
[1.0.3]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.0.3
[1.0.4]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.0.4
[1.1.0]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.1.0
[1.2.0]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.2.0
[1.2.1]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.2.1
[1.3.0]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.3.0
[1.4.0]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v1.4.0
[2.0.0]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v2.0.0
[2.0.1]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v2.0.1
[2.1.0]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v2.1.0
[2.2.0]: https://github.com/CSSALT/Phone-6DOF-Controller/releases/tag/v2.2.0
