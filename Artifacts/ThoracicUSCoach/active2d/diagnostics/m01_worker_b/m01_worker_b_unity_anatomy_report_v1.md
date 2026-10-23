# M01 Worker B — Unity anatomy ground-truth verification

Status: **EVIDENCE_READY**  
Commit inspected: `5184d92c308bb0791e3a299085f9c4066c7ff1e5`  
Scope: offline renderer/object/triangle attribution only. No training, capture sweep, controller change, or actor input was performed.

## Result

No reviewed prototype is a `VERIFIED_ANATOMICAL_LANDMARK`.

| Evidence target | Classification | Unity-grounded finding |
|---|---|---|
| P0 | `AMBIGUOUS` | Across four representative frames, the first shadow came from the combined `Skeleton` mesh on 2,616/3,024 sampled rays and right lung pleura on 408/3,024. The prototype also spans T7 and T6. It is not a unique T7 or named-bone landmark. |
| P3 | `GENERIC_VISUAL_FEATURE` | All four representatives were dominated by a repeatable shadow from the combined `Skeleton` mesh (3,019 first-shadow rays; five sampled rays had no shadow within depth). This verifies a generic bone-edge/shadow motif, not rib, lamina, transverse process, or vertebral identity. |
| P5 | `AMBIGUOUS` | The first shadow changes source between right lung pleura (1,421 rays) and the combined skeleton (1,386 rays). Three close representatives are lung-dominant, while near-radius P5-S5 is overwhelmingly skeleton-dominant. The visual grouping is not a stable physical landmark. |
| P6 | `AMBIGUOUS` | Mixed first-shadow sources: combined skeleton 1,593 rays and right lung pleura 1,308. T6 examples are lung-dominant; one T7 example is mixed and another T7 example is skeleton-dominant. |
| T6/T7/T8 appearance as an absolute level marker | `AMBIGUOUS` | Right trapezius is the most common superficial texture source at all three levels. The deeper first shadow varies between skeleton and right lung pleura. These broad sources do not uniquely identify vertebral level. |
| Claims that a reviewed contour is specifically a rib, transverse process, lamina, or named T7 structure | `UNSUPPORTED` | The simulator represents all ribs and vertebrae in one `Skeleton` GameObject/mesh. Triangle IDs prove which part of that mesh was hit, but the mesh contains no triangle-to-anatomical-part semantic table. |

## Exact Unity object identities

- Bone shadow: `/IndexPlate/Anatomy/RA Anatomy Set/Skeleton`; mesh `Skeleton`; tag `Bone`; layer 8; `UltrasoundVisibility.Type = Shadow`; 24,376 vertices and 42,966 triangles.
- Lung shadow: `/IndexPlate/Anatomy/RA Anatomy Set/Lung simplified NORMAL/Lung Pleura, Right`; mesh `Lung Right NORMAL`; touch identity `Lung`; `Type = Shadow`.
- Dominant superficial speckle: `/IndexPlate/Anatomy/RA Anatomy Set/RA sim Isaac Anatomy/Muscles/Right Trapezius`; `Type = Texture1`.
- Secondary superficial contributors include `Right Rhomboid` and, in the T8 reference, `Right Erector Spinae`; both use `Texture1`.

The renderer implementation confirms the causal interpretation: `UltrasoundManager.Scan()` raycasts through the imageable layers. The first entering object with `RenderType.Shadow` emits a bright edge, renders black below it, and terminates processing for that beam. Therefore a bright lower contour followed by black is a renderer shadow boundary, but its source must be identified per ray; it is not automatically bone.

## Representative triangle evidence

- P0 T7 examples hit repeatable skeleton triangle neighborhoods around the 7k/29k–30k index regions, plus lung triangles around 1.5k. P0-S5 at T6 shifts to skeleton triangles around 29k–32k and lung triangles around 609–631.
- P3-S1/S2 repeatedly hit skeleton triangles including 6.9k–7.0k and 30.1k neighborhoods. P3-S5 remains skeleton-derived but shifts within the combined mesh. This supports repeatable *bone-shadow appearance*, not a named bone part.
- P5-S1/S2/S3 repeatedly hit right-lung triangles around 0.6k/1.4k as the dominant shadow; P5-S5 instead hits skeleton triangles around 29k–32k on 736/756 sampled rays.
- P6-S2/S3 at T6 repeatedly hit lung triangles around 0.6k/1.4k plus skeleton triangles around 28.6k–31.9k. P6-S5 at T7 switches to skeleton triangles around 7k/29k–30k on 718/756 rays.
- The independent T8 reference is also mixed: 412 right-lung first-shadow rays and 316 skeleton first-shadow rays.

Full per-frame object paths, triangle ranges, top triangle counts, world-space hit bounds, image SHA-256 values, and probe replay residuals are in `m01_worker_b_unity_attribution_v1.json`.

## Method and limitations

Seventeen saved probe poses were replayed exactly: four each for P0/P3/P5/P6 plus one close T8 reference. Position and rotation replay errors were both zero for every frame. For every 256-beam image, the verifier sampled three deterministic beam slabs at local z = `-0.5 × thickness`, `0`, and `+0.5 × thickness`, for 756 rays per frame.

The original renderer randomly offsets each beam inside its 1 mm thickness. Its RNG state was not saved, so the exact original per-pixel ray cannot be reconstructed bit-for-bit. The three-slab attribution bounds the same physical insonation volume but does not claim pixel-exact provenance. The scene uses one simulated anatomy, so this evidence says nothing about patient generalization or clinical landmark validity.

Most importantly, Unity ground truth is restricted to this offline verification. No GameObject, mesh, triangle, pose, marker, or level information may be routed to the image-only actor.

## Evidence

- `Artifacts/ThoracicUSCoach/active2d/diagnostics/m01_worker_b/m01_worker_b_unity_attribution_v1.json`
  - SHA-256: `BB9106A6E56639F069B912E4A6F075C578E665C2526D88AE0DB6BABFB9F808EB`
- `AgentScripts/WorkerBFrameAttribution.cs`
  - SHA-256: `E11F457A1CC61396CDBF4731CE2776E127B58BAC9A326528044096119AFDA7EA`
- Existing F02 visual evidence:
  - `Artifacts/ThoracicUSCoach/active2d/diagnostics/u05_f02/u05_f02_prototypes_view_a_blinded.png`
  - `Artifacts/ThoracicUSCoach/active2d/diagnostics/u05_f02/u05_f02_prototypes_view_b_metadata.png`
- Renderer source:
  - `Packages/com.cssalt.smmarts-sdk-package/Runtime/Ultrasound/UltrasoundManager.cs`
  - `Packages/com.cssalt.smmarts-sdk-package/Runtime/Ultrasound/UltrasoundVisibility.cs`

## Scientific consequence

F02's original caution is confirmed and strengthened. P3 is a real, repeatable renderer motif, but only at the broad level of a bone shadow. P0, P5, and P6 mix physical sources. None can currently support an anatomically named landmark or absolute T7 identity. Prototype memory should therefore remain auxiliary and be assigned zero weight whenever its reliability gate is not met; Unity geometry must remain offline-only.
