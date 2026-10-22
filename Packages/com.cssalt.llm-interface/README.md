# CSSALT LLM Interface

Voice control for Unity medical-simulation scenes: hold a key, speak, and a tool
moves. *"Sweep the probe two centimetres toward the head."* *"Give me a left
sagittal view."* *"Zoom in."*

The package is **tool-agnostic**. It knows nothing about ultrasound — a probe, a
needle, an endoscope and a camera are all just *tools*, and what each one can be
told to do is **authored as data in the Inspector**, not written in code. Adding a
new instrument means filling in a profile, not editing a dispatcher.

Two assemblies, usable together or apart:

- **ContextCommander** — the execution core. Turns a named **direction + positive
  amount** into a precise Transform motion. The code owns every sign, so left/right
  can never silently flip. Anatomical frame (DICOM LPS), per-tool verb tables,
  refuse-don't-guess on ambiguity.
- **CSSALT.LLMInterface** — push-to-talk → Whisper → chat model → bracket commands
  → ContextCommander. Bundled TTS (Deepgram + browser) and a WebGL mic.

No external package dependencies. Unity **2021.3+** (developed on Unity 6).

> **Required for the push-to-talk key:** set *Project Settings ▸ Player ▸ Active
> Input Handling* to **Both** (or *Input Manager (Old)*), then restart the Editor.
> New Unity 6 projects default to *Input System Package (New)*, under which
> `UnityEngine.Input` is unavailable — keeping this package dependency-free means
> the PTT **key** is inactive there. It degrades cleanly (a Console warning, no
> exceptions), and `pushToTalkButton` plus the public `StartRecording()` /
> `StopAndSend()` work under any setting.

---

## Install

Window ▸ Package Manager ▸ **+** ▸ *Add package from git URL…*

```
https://github.com/CSSALT/CSSALT-LLM-Interface.git?path=/Package
```

Pin a version with a tag: append `#v0.1.0`.

---

## What ships

```
Runtime/
  ContextCommander/          the execution core (its own assembly)
  LLMInterface/              voice pipeline, prefabs (its own assembly)
    For Cameras/             optional camera helper
    Press and Hold button/   ready-made PTT UI prefab
    Anatomical LLM.prefab    ContextCommander + LLMInterfaceControl, pre-authored
    Camera.prefab            camera rig with the up-constraint
```

**`For Cameras/` is optional convenience**, not part of the core — delete it if you
don't want it; nothing in `ContextCommander` or the voice pipeline references it.
It ships because every application we build has a camera and no host SDK provides
this behaviour.

> **Probe-contact scripts deliberately do NOT ship here.** `SkinIntersectionCompression`,
> `ProbeSurfaceClamp` and `ICompressionProvider` live in the **host project**, because
> a SMMARTS SDK project already provides them. They are in the global namespace, and
> Assembly-CSharp automatically references package assemblies, so a package copy
> would put two same-named types in scope at once and every unqualified use in the
> host would fail to compile as ambiguous (`CS0433`). The rule this encodes:
> **the package ships what is tool-agnostic or genuinely its own, and stays out of
> anything the host SDK already owns.**
>
> This costs nothing functionally — the package never references those types. The
> coupling runs the other way: a host's clamp registers itself in a tool's
> `subscribers` list through the package's `IToolMotionSubscriber` interface, which
> the package does own.

---

## First-time setup — read this before wiring anything

The prefabs carry a **fully authored tool vocabulary**. They cannot carry the
**objects** it points at.

That is a Unity rule, not a bug: **a prefab asset cannot store a reference to a
scene object.** Every such reference is written out as null. So when you drop
`Anatomical LLM.prefab` into your scene you get all the hard-won *knowledge* about
how a probe and a camera behave, with empty slots where your objects go.

### What survives the import

| Survives | Why it matters |
|---|---|
| Both **tool profiles**, complete — verb tables, direction qualifiers, `businessEnd`, `rotationQualifiersDescribe`, `markerAxis`, `noonDirection` | This is the part that took longest to get right. It transfers intact. |
| **Capability flags** — `clockfaceEnabled`, `imagingPlanesEnabled`, `viewpointsEnabled`, `viewpointDistanceMm` | The probe keeps imaging planes, the camera keeps viewpoints. |
| **Body axes** — `axisMode`, `cephaladAxis`, `anteriorAxis`, `patientLeftAxis` | Verify these against *your* anatomy's orientation (see below). |
| `unitsPerMm`, `midlineDeadzoneMm`, `gizmoRadius` | |
| The **number** of landmark slots | You get correctly-sized empty rows to fill. |
| References **inside** the prefab (e.g. `LLMInterfaceControl.commander` → the `ContextCommander` on the same object) | Already wired; leave them alone. |

### What arrives empty, and must be reassigned

| Field | Assign to |
|---|---|
| `tools[n].tool` | The Transform of each tool in *your* scene. |
| `tools[n].pivot` | The tool's rotation pivot. For a probe: the face-centre marker on the footprint. For a camera: usually the rig itself. Empty = the tool's own origin. |
| `tools[n].subscribers[]` | Optional host physics — e.g. your probe-surface clamp. Leave empty if none. Some hosts populate this at runtime. |
| `landmarks[]` | Your anatomy markers (head, shoulders, liver, …). |
| `midline` | A point on the patient's midline. |
| `origin` | The patient centre. Also the point camera viewpoints orbit and frame. |
| `axisSource` | Only if `axisMode` = *From Transform*. |

**The probe binding is the one to watch.** It arrives with `tool` and `pivot` empty.
Because our applications use the same kind of probe, everything *else* in that
binding — its verbs, its qualifiers, its `businessEnd`, its handle-vs-beam
convention — is already correct and should be left alone. You are only re-pointing
it at your probe object.

### Two things that fail *silently* if you get them wrong

Worth ten minutes, because neither throws — the system just does the wrong thing
while reporting success.

1. **The tool's local axes must match its profile.** `businessEnd` names the local
   axis the tool works along: `MinusZ` for the reference probe (the beam), `PlusZ`
   for a camera (the view direction). If your model is built with a different local
   orientation, every rock, tilt, compression and view will be subtly or completely
   wrong while still reporting success. Check the model's axes in the Scene view
   before trusting any motion.
2. **The body axes must match your anatomy's orientation.** `cephaladAxis` /
   `anteriorAxis` / `patientLeftAxis` map anatomy onto world axes. Get one wrong and
   "move toward the head" travels somewhere else, confidently. The self test checks
   these form a valid right-handed LPS frame, which catches a *mirrored* setup but
   not a merely *rotated* one.

### Verify

Right-click **ContextCommander ▸ Self test**. A clean run logs
`ContextCommander self test passed`.

The sandbox's `CSSALT ▸ Run Conformance Probe` runs the same checks plus a
deliberate **negative control** that always fails — there, a run showing exactly
that one failure is a pass.

The Inspector also warns at edit time if a `subscribers` entry doesn't implement
`IToolMotionSubscriber` (it would otherwise look wired and do nothing), naming the
tool and the offending index.

---

## Core concepts

**Every movable is a tool.** One list, `ContextCommander.tools`. The **first entry
is the primary tool** — what *"it"* and *"the tool"* resolve to when the speaker
names nothing. Order matters; put the tool you talk to most at element 0.

**A tool's vocabulary is data.** A **verb** selects one of the tool's local axes; a
**qualifier** selects the direction along it. Fill in the six `AxisVerbs` tables
(`translateX/Y/Z`, `rotateX/Y/Z`) and the tool can be commanded by those words — the
dispatcher falls through to a tool's own verbs, so `[Zoom camera in]` works with no
code anywhere. Verbs listed together on one axis are synonyms.

**The business end** is the axis a tool works along — a probe's beam, a needle's
tip, a camera's view. It anchors compression, look-at, and rotation qualifiers.

**Capabilities are opt-in per tool**, because the same word means different things
to different instruments:

| Flag | Grants | Typical |
|---|---|---|
| `imagingPlanesEnabled` | `View` — align an imaging plane to a named anatomical plane | probe **on**, camera off |
| `viewpointsEnabled` | `Viewpoint` — place an observer at an anatomical viewpoint | camera **on**, probe off |
| `clockfaceEnabled` | `RotateToClock`, `Aim` — an orientation marker | probe **on**, camera off |

That split is what lets *"sagittal"* mean an imaging plane to the probe and a
side-on camera angle to the camera. **`imagingPlanesEnabled` defaults to off** — a
probe that doesn't get it ticked will refuse `View`.

**Ambiguity is refused, not guessed.** Medial/lateral on the midline, a direction
perpendicular to the axis it would travel along, a plane viewpoint from dead-centre
— all come back as a question for the operator instead of a coin flip.

---

## Configuration

### ContextCommander

| Field | Purpose |
|---|---|
| `tools` | Everything the model may move. One `ToolBinding` each. **First = primary.** |
| `landmarks` | Reference-only points: valid as `MoveToward` targets, rotation pivots and look-at targets, never moved. |
| `unitsPerMm` | Scene units per millimetre (1 if 1 unit = 1 mm). |
| `midline` + `midlineDeadzoneMm` | The midsagittal plane. Inside the deadzone, medial/lateral — and a plane viewpoint's side — are ambiguous and refused. |
| `origin` | Patient centre. Origin of the coordinates reported to the model, **and** the point camera viewpoints orbit and frame. |
| `axisMode` = **World Constants** + `cephaladAxis` / `anteriorAxis` / `patientLeftAxis` | Map each body direction to a fixed world axis. For a static scene. |
| `axisMode` = **From Transform** + `axisSource` | Axes track the source's up / forward / right, live. For re-posing scenes. The source is read as the *patient's* own frame. |
| `gizmoRadius` | Scene-view markers (tool = green cube, landmark = blue sphere). 0 = off. |

### ToolBinding (one per tool)

| Field | Purpose |
|---|---|
| `tool` | The Transform this profile describes. |
| `businessEnd` | The local axis the tool works along. Probe = `MinusZ` (beam), camera = `PlusZ` (view). |
| `pivot` | Rotation pivot, read live. Empty = the tool's own origin. |
| `rotationQualifiersDescribe` | Whether a rotation qualifier points the **business end** or the **far end** (handle). Both conventions are taught clinically; the reference probe uses **FarEnd**. |
| `translateX/Y/Z`, `rotateX/Y/Z` | The verb tables. Each has `verbs` (synonyms selecting this axis) and `plusQualifiers` / `minusQualifiers` (tool-local direction words). Anatomical words always work and need not be listed. |
| `clockfaceEnabled`, `markerAxis`, `noonDirection` | Orientation marker, for `RotateToClock` / `Aim`. |
| `imagingPlanesEnabled` | Grants `View`. |
| `viewpointsEnabled`, `viewpointDistanceMm` | Grants `Viewpoint`; how far from the subject to sit. |
| `subscribers` | Optional host physics implementing `IToolMotionSubscriber`. Several may be listed; each may only reduce, so they compose as the **minimum** and order doesn't matter. |

### LLMInterfaceControl

| Field | Purpose |
|---|---|
| **Latency Metrics** (read-only, live in Play) | `releaseToMoveSeconds` — key-up to first motion, the number that matters — plus the `asr` / `llm` / `tts` split. |
| `commander` | The ContextCommander to drive (auto-found if empty). |
| `liteLLMBaseUrl` / `llmModel` / `whisperModel` / `liteLLMApiKey` | Proxy endpoint, models, bearer key (or `Resources/llm_api_key.txt`). |
| `systemPromptAsset` | The prompt text asset. Defaults to the bundled `LLMInterfacePrompt`. |
| `pushToTalkKey` / `pushToTalkButton` | Hold-to-talk key (default **V**) and/or a UI button. |
| `ttsProvider` / `deepgramApiKey` / `deepgramVoice` | Spoken replies: Deepgram (needs a key) or Browser (WebGL, free). |
| `defaultMoveMm` / `defaultRotateDeg` / `defaultMotionSeconds` | Amounts and glide duration used when a command omits them. |
| `showOverlay` + `onStatus` / `onUserMessage` / `onAssistantMessage` | Built-in IMGUI overlay, or bind these UnityEvents to your own UI. |

Put keys in **your project's** `Assets/Resources/llm_api_key.txt` — not this
package's `Resources/`, and not the Inspector field (that serialises the key into
your scene file). Git-ignore them.

---

## The commands the model emits

```
[Move <object> <direction> <dist?> <Ns?>]          anatomical travel, any tool
[MoveToward <object> <target> <dist?> <Ns?>]       omit dist = onto the target
[RotateAbout <object> <axis> <deg> <pivot?> <Ns?>] spin about an anatomical axis
[PointAt <object> <direction-or-target> <Ns?>]     aim the business end, any tool

[Slide|Sweep <object> <direction> <dist> <Ns?>]    along a tool's own axis
[Rock|Tilt <object> <direction> <deg> <Ns?>]       angle a tool about its pivot
[Rotate <object> <clockwise|counterclockwise> <deg> <Ns?>]
[Compress|Lift <object> <dist> <Ns?>]              along the business end
[RotateToClock <object> <hour> <Ns?>]              marker to a clock hour
[Aim <object> <direction-or-target> <Ns?>]         twist the marker to point

[View <object> <sagittal|coronal|transverse|midsagittal> <Ns?>]      imaging plane
[Viewpoint <object> <AP|PA|left|right|superior|inferior|sagittal|coronal|transverse> <target?> <Ns?>]

[<toolVerb> <object> <direction> <amount?> <Ns?>]  ANY verb the tool authors
[Say "<prose>"]   [Noop]
```

Distances are mm (`20`, `20mm`, `2cm`); durations carry an `s` suffix (`2s`).
Degrees are plain numbers. The direction is always a **word** and the amount always
**positive** — to go the other way, name the opposite direction.

`ContextCommander.BuildContext()` is injected every turn, so the model only ever
names objects that exist right now. It also advertises, per tool, that tool's own
verbs and direction words — which is how the model knows a camera can `Zoom` and a
probe cannot.

Positions are reported in the **patient's frame** (DICOM LPS: `x=left, y=posterior,
z=superior`, mm, relative to `origin`) — never world space. The same numbers mean
the same anatomical thing in every simulator, however its anatomy is mounted.

---

## Host physics: what the package does *not* ship

Probe-surface contact — `SkinIntersectionCompression`, `ProbeSurfaceClamp`,
`ICompressionProvider` — lives in **your project**, not here. A SMMARTS SDK project
already provides it, and a package copy would collide (see *What ships*, above).

The package doesn't need it. The seam is `IToolMotionSubscriber`, which the package
*does* own: your clamp implements it, adds itself to the tool's `subscribers` list,
and the dependency points one way only. That is how the probe's tissue cap reaches
the LLM layer without the LLM layer knowing that colliders exist.

To wire a host limiter to a tool:

1. Implement `IToolMotionSubscriber` on a `MonoBehaviour` in your project.
   `BeforeMotion` returns the magnitude to allow — it may only **reduce**, and must
   set `reason` whenever it does. `AfterMotion` returns a short note, or `""`.
2. Add it to that tool's `subscribers` list, in the Inspector or at runtime.
3. Several may be listed. Each may only reduce, so they compose as the **minimum**
   and their order doesn't matter. The reason reported is the one from whichever
   subscriber actually bound the motion.

Note the split of responsibilities that the probe clamp demonstrates: a **limit on a
command** goes through `BeforeMotion`; an **invariant that must hold every frame**
(its depth correction) belongs in `LateUpdate`, because it also has to survive manual
drag and tracked input, which no motion hook ever sees.

## Optional helpers

### `For Cameras/`

- **`CameraUpConstraint`** — aiming a camera fixes two of its three rotational
  degrees of freedom; roll is left free, so a viewpoint can land correctly aimed but
  visibly twisted. This rolls the camera about its own view axis so up points at a
  chosen world axis. Runs in `LateUpdate`, so it holds throughout a glide, and never
  touches the view direction, so it cannot fight an aim.
  It is **not** an `IToolMotionSubscriber` and must not be assigned to that field —
  a continuously-held pose invariant is not a limit on a command.

### Prefabs

- **`Anatomical LLM`** — the commander and the voice pipeline, with the probe and
  camera profiles pre-authored. See *First-time setup* above.
- **`Camera`** — a rig whose +Z faces the patient with the lens standing off behind
  it, so aiming or moving the rig at a landmark doesn't bury the lens inside the
  body. Note that `viewpointDistanceMm` is then the **rig's** distance from the
  subject, not the lens's.
- **`Press and Hold button`** — a ready-made PTT UI button; works under any Active
  Input Handling setting.

---

## Custom UI

The IMGUI overlay is the zero-setup default. For your own UI, turn `showOverlay`
off and bind the `onStatus` / `onUserMessage` / `onAssistantMessage` UnityEvents.

## Further reading

- [`docs/TOOL-ABSTRACTION-SPEC.md`](../docs/TOOL-ABSTRACTION-SPEC.md) — the
  tool-agnostic model, T1–T10, with the checkability rules each requirement is
  written against.
- [`docs/TOOL-CONFORMANCE.md`](../docs/TOOL-CONFORMANCE.md) — how the code measures
  against that spec, and what is still open.
- [`CHANGELOG.md`](CHANGELOG.md) — version history.
