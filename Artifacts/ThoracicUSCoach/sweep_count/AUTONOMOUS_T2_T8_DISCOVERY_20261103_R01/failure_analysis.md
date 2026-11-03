# Failure analysis

The historical detector reduces each frame to one winning lower bright contour and then searches that contour for valleys. Actual R2/R3/R6.1 images show multiple simultaneous posterior interfaces inside the approximately 50.23 mm longitudinal footprint. A one-degree yaw change can change which interface wins. The old detector consequently missed T2/T3 or accepted the stable secondary interface.

The image-to-motion projection is not the main failure: it is renderer-consistent, and the new spatial likelihood localizes development landmarks to 0.02–2.25 mm. The near-5 mm historical T7 error came from the selected valley center, not from an omitted footprint correction.

The narrow-width development acquisition reduced the footprint to 17.23 mm but did not make the valley representation reliable: it produced four accepted valleys and retained the secondary interface. A model trained only on wide images transferred poorly (3/7), so this direction was rejected rather than rescued with thresholds.

The strongest representation uses all plausible image interfaces at each actor-relative coordinate. Interface-centered intensity and gradient patches are classified by a small ExtraTrees model trained on whole development sessions. The actor then aggregates independent forward/reverse observations into a spatial likelihood. True recesses appear as localized response maxima. The stable secondary interface can have high patch likelihood but has lower isolated peak confidence; it is retained as a raw image event and rejected below the frozen 0.70 classifier gate.

The experiment-specific fixed, anatomically unlabeled Home contract supersedes the old skill's known-T2-start assumption. Anatomical coordinates are used only to label development images and independently score frozen outputs.
