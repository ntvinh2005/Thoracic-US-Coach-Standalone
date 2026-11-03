# Best method: spatial patch likelihood

1. Validate every fresh frame and reconstruct Home-relative position from accepted achieved motion.
2. Extract at most three diverse bright-interface hypotheses per frame.
3. At every 0.5 mm actor-relative grid location, use the calibrated scan footprint to sample an interface-centered pixel patch from each hypothesis.
4. Convert each patch to normalized 16×16 intensity, horizontal gradient and vertical gradient features.
5. Apply the frozen CPU ExtraTrees morphology classifier. Coordinates, T labels and evaluator artifacts are not model inputs.
6. Aggregate the maximum competing-track probability per frame and the 75th percentile across frames.
7. Find spatial response maxima at least 10 mm apart. Retain every raw peak above 0.55 with prominence at least 0.08.
8. Accept a peak only with forward and reverse support and frozen classifier score at least 0.70. Lower-confidence peaks remain explicitly rejected evidence.
9. Emit the ordered T2–T8 map only for exactly seven accepted peaks; otherwise abstain. V6 is T7 by the approved order prior.

The fitted model is fixed-anatomy development evidence. It does not establish clinical or patient-level generalization.
