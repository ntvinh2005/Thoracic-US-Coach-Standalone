# G2-R1 R1-B Ultrasound Observability at Anatomy-Validated Regions

## Execution

One graphics-capable Unity diagnostic capture session was executed in the unchanged production scene. It acquired 42 fresh PNGs: seven LF-associated regions, three safe longitudinal geometries, and two independent renders per pose.

- `PARAMEDIAN_LONGITUDINAL_M6`, yaw 90 degrees
- `PARAMEDIAN_LONGITUDINAL_P6`, yaw 90 degrees
- `ANATOMY_DERIVED_LONGITUDINAL_M3`, yaw 90 degrees

All 42 captures passed rigid contact, workspace, image integrity, pose epoch, producer frame, scan serial, and provenance matching. There were 42 unique PNG hashes. Diagnostic placement was evaluator-guided and is not autonomous localization.

## Original-scene results

| Geometry | Repeat-region matches | Minimum same-pose SSIM | LF target reached before shadow | Bone shadow on both LF sides | Adjacent-minus-repeat descriptor margin |
|---|---:|---:|---:|---:|---:|
| M6 longitudinal | 7/7 | 0.99049 | 7/7 | 6/7 | +0.05815 |
| P6 longitudinal | 7/7 | 0.97758 | 6/7 | 5/7 | +0.08143 |
| M3 longitudinal | 7/7 | 0.96932 | 6/7 | 6/7 | +0.10917 |

The M6 longitudinal configuration is the best diagnostic geometry because every target LF region is reached before or at the first Skeleton shadow and repeatability is strongest. At six of seven levels, Skeleton shadow occurs on both longitudinal sides of the target LF span. T2 is the one-sided edge case.

The contact sheet shows real, stable changes in curved bright interfaces, shadow onset, shadow width, and central/lateral morphology. The repeat-A images match their independently rendered repeat-B pose at all seven regions. This proves position-specific original-scene image information; it does not yet prove a continuous image-only detector can find all seven events.

## Renderer contribution

Full ordered-interface provenance, rather than `firstVisibleObject` alone, shows:

- the combined Skeleton Shadow contributes at every validated region;
- LF Normal interfaces are reached before or at the first shadow at all seven M6 regions;
- the 40 mm longitudinal plane often contains both the target LF region and an adjacent LF region;
- later interfaces behind the first Skeleton shadow are correctly recorded as occluded and are not treated as visible pixels.

The provenance establishes physically ordered intersections and occlusion. It does not assign a pixel amplitude to LF, and the combined Skeleton still cannot identify rib versus lamina versus spinous process.

## Negative controls and limitations

- Historical label-center/arithmetic-midpoint E4 remained ambiguous and is retained unchanged as a negative control.
- The blind geometry audit retained an additional 33.5 mm recess with no LF association, demonstrating that arbitrary geometry minima cannot be counted as vertebral events.
- Repeat-region nearest-neighbor accuracy is an upper-bound diagnostic using evaluator-guided poses; it is not actor-side enumeration.
- No full longitudinal sweep, autonomous detector, T7 inference, or return was run.

Evidence: `anatomical_acquisition_manifest.json`, `bone_gap_signal_analysis.csv`, `bone_gap_contact_sheet.png`, `bone_gap_waveform.png`, `renderer_contribution_audit.json`, `repeatability_and_negative_controls.json`, raw PNGs, and full per-frame provenance under `evaluator_only/r1b_observability/`.
