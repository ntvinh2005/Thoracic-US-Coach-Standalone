# Thoracic US Coach - M0-C Anatomical Waveform Discovery

## 1. IMPLEMENTATION

This was one bounded offline experiment over the existing M0-B yaw-0 dataset. No Unity process, new capture, controller, scene, renderer, anatomy, navigation architecture, learned model, T7 return, or scientific gate was changed.

Added:

- `Tools/ThoracicUSCoach/anatomical_waveform_analysis.py`
- `Tools/ThoracicUSCoach/test_anatomical_waveform_analysis.py`
- This isolated evidence directory.

The blind analyzer used only clean PNG pixels and a signed coordinate reconstructed from accepted actions and achieved displacement. Evaluator world coordinates, reference markers, mesh identities, and provenance were opened only after blind outputs were hashed.

## 2. VERIFICATION

- Input observations: **103/103 present and hash-valid**.
- Accepted/rejected movements inherited from M0-B: **102/0**.
- Coordinate reconstruction maximum disagreement with the actor trace: below **0.05 mm**.
- Focused M0-C Python tests: **8/8 PASS**.
- Relevant offline regression suite: **25/25 PASS**.
- Unity compilation, Play Mode, and capture: **NOT_RUN** by M0-C authorization.
- Blind freeze verification before evaluator access: **PASS**.
- This dataset was viewed during M0-B, so this is blind-first execution but not a genuinely untouched held-out trial.

## 3. MORPHOLOGY FEATURE RESULTS

Three predeclared interpretable feature families were measured:

1. **Central shadow geometry**: central low-intensity width at normalized depths 0.36, 0.48, and 0.60. Technical valid coverage was **98.1%**; measured width range was **0.088-0.295** of image width.
2. **Bright-interface geometry**: outer-minus-inner depth of twelve fixed-x bright-to-dark ridge samples. Technical valid coverage was **100%**; arch-height range was **-0.280 to +0.134** of image height.
3. **Bilateral structure**: correlation between mirrored fixed lateral image regions. Coverage was **100%**; symmetry range was **0.228-0.640**.

The overlays show that central shadow width is visually inspectable. The interface samples usually follow the lower bright curve, but several cranial-end samples attach to bright speckle or disconnected tissue edges. The interface family is therefore measurable but not independently attributable to one anatomical structure. Bilateral symmetry is highly stable but contains a strong long-range trend rather than repeated structural cycles.

No contour is called rib, lamina, vertebra, or interspace from image appearance alone.

## 4. WAVEFORM PLOTS AND PERIODICITY

The waveforms contain substantial, continuous spatial variation and reproduce image morphology better than the old global scalar. However, they do not establish a common recurrent phase.

The blind multifeature autocorrelation had its strongest local peak at approximately **33.0 mm**:

- Peak correlation: **0.581**
- Peak prominence: **0.242**
- AR(1) smooth-nonperiodic null 95th percentile: **0.689**
- Monte Carlo p-value: **0.259**

It therefore failed the frozen `p <= 0.05` periodicity criterion. This is compatible with a smooth or irregular morphology trend rather than a stable anatomical oscillator.

Feature ablation was unstable:

- Without bilateral symmetry: `NOT_SUPPORTED`, p = **0.494**.
- Without interface arch height: `NOT_SUPPORTED`, p = **0.484**.
- Without shadow width: `SUPPORTED`, p = **0.0025**.

The last result is not sufficient evidence for a vertebral cycle because it depends on the two families most affected by a monotonic trend and disconnected interface tracking, and it disappears in the primary three-family analysis. Parameters were not retuned after this observation.

The old positive-peak scalar control returned only **3 events**, at approximately **-3.0, +24.0, and +60.0 mm** actor-relative. It did not recover a seven-instance sequence and no anatomical identity is assigned to these peaks.

## 5. FORWARD/REVERSE CONSISTENCY

Actual overlap, not nominal frame index, was used.

### Caudal half overlap

- Matched observations: **31**, about +3 to +93 mm.
- Shadow-width correlation: **0.959**.
- Interface-arch correlation: **0.835**.
- Bilateral-symmetry correlation: **0.999**.
- Same-position median multifeature state distance: **0.096**.
- Distinct-position median state distance: **2.422**.

### Cranial partial overlap

- Matched observations: **8**, about -87 to -66 mm only.
- Shadow-width correlation: **0.967**.
- Interface-arch correlation: **0.893**.
- Bilateral-symmetry correlation: **0.999**.
- Same-position median state distance: **0.136**.
- Distinct-position median state distance: **2.864**.

The features distinguish spatially different observations and reproduce the same position well. This supports an observable spatial morphology waveform, but it does not establish recurrent cycles. No full bidirectional claim is made for the cranial side.

## 6. BLIND CYCLE DETECTION

Blind result: **`NO_DEFENSIBLE_CYCLES`**.

- Independent candidate cycles: **0**.
- Candidate intervals: **0**.
- No smoothing, spacing, threshold, or event count was adjusted to fit seven references.
- The old scalar's three peaks were retained only as a negative control and were not promoted to cycles.

## 7. ANATOMICAL VALIDATION

After freeze, the seven T2-T8 evaluator contact points were projected onto the M0-B evaluator start/axis. Each reference was associated with the corresponding observation through frame index and evaluator coordinate; nearest-frame errors were **0.10-1.27 mm**.

No T-level region contained a frozen blind cycle. Renderer provenance confirmed valid published frames and a Skeleton shadow source at all seven nearest observations. Level-specific LF objects appeared among first-visible objects at some levels, but the common Skeleton object remains `COMBINED_SKELETON_UNRESOLVED`. This cannot prove that the measured shadow or bright curve is a rib, lamina, vertebra, or interspace.

`VERTEBRAL_CORRESPONDENCE` is therefore **NOT_SUPPORTED**, not merely a failed proximity match.

## 8. T7 INFERENCE

**NOT_ATTEMPTED**.

There is no ordered sequence of independently supported vertebral instances and no image-only absolute anchor. The physical corridor endpoint is not a T-level anchor. No T7 coordinate or error was calculated from evaluator truth.

## 9. SCIENTIFIC VERDICTS

| Outcome | Verdict | Evidence |
| --- | --- | --- |
| WAVEFORM_OBSERVABILITY | **SUPPORTED** | Three visually inspectable morphology measurements; high same-position reverse correlation; same-position state distance far below distinct-position distance. |
| CYCLE_REPEATABILITY | **NOT_SUPPORTED** | Primary periodicity p=0.259; ablation unstable; zero defensible cycles. |
| VERTEBRAL_CORRESPONDENCE | **NOT_SUPPORTED** | Zero blind cycles to match; combined Skeleton identity unresolved. |
| T7_INFERENCE | **NOT_ATTEMPTED** | No validated ordered vertebral sequence or absolute anchor. |

Established project gates are unchanged: G2 remains **FAIL** and G3/G4 remain **NOT_RUN**.

## 10. EXACT REMAINING BLOCKER

The images contain position-specific, repeatable morphology, but the selected simple geometry features do not produce a stable multifeature A-to-B-to-A recurrence that exceeds a smooth nonperiodic null. The only significant ablation depends on features with a monotonic trend and occasional disconnected-edge tracking. Consequently, individual vertebral-scale structural units cannot be separated from arbitrary morphology fluctuations, and no one-to-one T2-T8 sequence can be constructed without using evaluator labels.

## Recommendation

Waveform morphology is worth retaining as descriptive image evidence, but waveform-based vertebral counting does **not** yet merit a fresh full counting experiment. A new capture should require a separately approved, visually validated feature hypothesis that resolves the interface-tracking ambiguity; repeating this detector on more frames would not address the present blocker.
