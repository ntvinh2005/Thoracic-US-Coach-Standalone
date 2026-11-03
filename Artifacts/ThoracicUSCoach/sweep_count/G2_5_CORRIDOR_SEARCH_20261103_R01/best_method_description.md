# Best method

The acquisition controller uses a fixed scene-authored operator contact shifted by a frozen robot-local lateral offset, then follows `Vector3.back` projected into the local skin tangent plane. Both movement directions are traced to generic skin-continuity limits within a symmetric mechanical resource ceiling; no LF object determines Home, axis, endpoint, workspace, or stop count.

Each fresh ultrasound frame supplies up to a few bright-interface paths. At every Home-relative grid coordinate, the actor samples normalized interface-centered 16×16 intensity and gradient patches and evaluates a lightweight ExtraTrees recess-morphology classifier. Evidence is aggregated over forward and reverse frames. Peaks require a score of at least 0.70, support from both directions, and 10 mm spatial separation. The model was trained on fixed-anatomy development data with 2 mm negative sampling so the recurrent secondary interface is explicitly represented as a pixel-morphology negative; coordinate is not a classifier feature.

The map is emitted only for exactly seven accepted events. Their observed order maps V1–V7 to T2–T8 under the approved controlled-region prior; V6 is returned as T7. Otherwise the actor abstains.
