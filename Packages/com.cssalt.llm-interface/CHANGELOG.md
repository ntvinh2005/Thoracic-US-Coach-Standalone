# Changelog

All notable changes to this package are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/), and this project adheres to
[Semantic Versioning](https://semver.org/).

## [Unreleased]

Generalized from "a package that drives an ultrasound probe" to one that drives
**any tool**, described by data. See
[`docs/TOOL-ABSTRACTION-SPEC.md`](../docs/TOOL-ABSTRACTION-SPEC.md) and
[`docs/TOOL-CONFORMANCE.md`](../docs/TOOL-CONFORMANCE.md).

### Added
- **Tool profiles.** `ToolBinding` / `AxisVerbs`: per tool, the verbs that select
  each local axis and the qualifiers that select the sign along it. Synonyms work.
  Also per tool: pivot, business end, rotation-qualifier convention, orientation
  marker, host subscriber.
- **Generic verb dispatch.** Any verb a tool authors is a command — the dispatcher
  falls through to the tool's own tables, so `[Zoom camera in]` needs no code. The
  table decides whether the amount is millimetres or degrees.
- **`PointAt`** — a generic look-at: aim any tool's business end at a direction or
  a named object. Distinct from `Aim`, which twists the marker.
- **`Viewpoint`** — anatomical viewpoints for observer tools (AP/PA/lateral/
  superior/inferior, plus plane words). Position and look-at in one command,
  orbiting the subject. Plane words infer their side from which side of the midline
  the tool is on, and ask rather than guess on the plane itself.
- **`View`** — imaging planes (sagittal/coronal/transverse/midsagittal).
- **Capability flags** per tool: `imagingPlanesEnabled`, `viewpointsEnabled`,
  `clockfaceEnabled`.
- **`IToolMotionSubscriber`** — host physics may reduce a motion before it is
  applied, with its own reason, and report after. A tool declares a **list** of
  them; since each may only reduce, they compose as the minimum and their order
  does not matter.
- **Multi-command turns.** Capture at dispatch, then replay: each command resolves
  against the previous one's real result, and a turn can no longer clobber itself.
- **Context advertising.** `[TOOL VERBS]` and `[LOCAL DIRECTIONS]` tell the model
  each tool's own vocabulary; the primary tool is tagged `(primary)`.
- **Latency metrics** in the Inspector, including release-to-first-motion.
- **Optional helper** shipped with the package: `For Cameras/`
  (`CameraUpConstraint`). Plus prefabs: `Anatomical LLM`, `Camera`,
  `Press and Hold button`. Probe-surface contact deliberately stays in the host
  project — a SMMARTS SDK project already provides it, and a package copy would
  put two same-named global types in scope at once.
- **Edit-time warning** when a `subscriber` is assigned that does not implement
  `IToolMotionSubscriber` — it would otherwise look wired and do nothing.

### Changed
- **`movable[]` is gone.** One list, `tools[]`; every movable is a tool. The
  **first entry is the primary tool**.
- **`"probe"` is no longer a generic reference.** Only the pronouns `it` / `the
  tool` resolve to the primary; everything else matches by name. With a camera at
  element 0, `"the probe"` used to resolve to the camera.
- `Compress`/`Lift` are now gated on the tool authoring its business-end axis, and
  that axis is addressed uniformly with the others.
- Rock/tilt default convention is the **handle** (far end).

### Fixed
- **Viewpoints flew through the patient.** They turned in place and translated;
  opposed lateral views are a 180° reversal, and the straight line between them
  crosses the body. They now pivot at the subject.
- **Glided motion drifted off its endpoint.** A step that both turned and travelled
  kept rotating about a pivot captured before the travel, so each frame added
  unwanted orbit. Because capture applies instantly but the *visible* pose comes
  from the replay, the tool silently ended somewhere other than where the command
  computed. The pivot now travels with the translation.
- **Removed a false "pressing at the tissue limit" note**, which fired on a probe
  lifted clear of the skin and duplicated what the rock/tilt cap already reports
  accurately.
- Spoken summaries say "degrees", not "deg"; the duration tail is suppressed.

### Known issues
- Motion summaries still describe every rotation as moving the "beam" or "handle",
  and generic verbs echo without a past tense (**TD8**).
- `Rotate` (longitudinal twist) is not capability-gated (**TD9**).
- A tool's basis is not validated at edit time (**TD6**).

## [0.1.0] — 2026-07-07

Initial release.

### Added
- **ContextCommander** (`ContextCommander.Runtime`): move, rotate, and
  move-toward named objects by direction + positive amount. The code owns every
  sign, so left/right can't silently flip. Features: medial/lateral via a
  midline reference; separate `movable` vs reference-only `landmark` lists;
  timed motion via coroutines; name matching that ignores separators and case
  (`Right-Shoulder` ⇄ `right_shoulder`); per-project body-axis mapping in the
  Inspector (dropdowns, or a live Transform source for re-posing scenes).
- **LLM Interface** (`CSSALT.LLMInterface.Runtime`): push-to-talk → Whisper →
  chat model → bracketed commands dispatched to ContextCommander. Bundled TTS
  (Deepgram + browser SpeechSynthesis) and a WebGL microphone. Built-in IMGUI
  overlay plus `UnityEvent` hooks (`onStatus`, `onUserMessage`,
  `onAssistantMessage`) for custom Canvas/TMP UI.
