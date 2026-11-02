# Actor-only revisit selection

The selector ranks candidate uncertainty classes, not coordinates or event IDs.

1. It selects the two strongest `AMBIGUOUS_INSUFFICIENT_EVIDENCE` hypotheses that already have image-derived shadow support but fewer than six distinct frames. Missing reverse support increases expected information gain.
2. It selects the strongest stable unresolved interface with at least six frames, both traversal directions, and insufficient actual pixel-shadow support.
3. Each window is the candidate support interval plus 6 mm of context, constrained to an 8–12 mm half-width and clamped to actor-observed safe Home-relative coverage.
4. Weak hypotheses receive yaw 90/91 at 50 mm depth. Only the stable distal-field ambiguity receives the additional yaw-90, 60 mm-depth view.

The actual selected centers are development evidence and are shown in `actor_selected_revisit_windows.json`. They are not anatomical priors and were not compared with evaluator references during selection.

Exact duplicate images and multiple contours from one image cannot add independent support. Independent evidence is capped by acquisition variant, traversal direction, and a 0.5 mm physical-position bucket.
