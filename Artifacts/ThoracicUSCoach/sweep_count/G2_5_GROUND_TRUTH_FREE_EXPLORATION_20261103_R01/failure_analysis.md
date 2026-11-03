# Failure analysis

The old acquisition success cannot be attributed solely to the recognizer: LF references planned its Home, direction and endpoints. Removing that planning exposed a corridor-selection problem.

Development 01 began 46.74 mm away from the earlier anatomy-planned Home and produced only one accepted image event, later unmatched. The images were fresh, so threshold tuning was not justified.

Development 02 applied one known 50 mm rendered-footprint movement in the robot's medial direction. This produced two true accepted events, including a T7-associated event, but not the full sequence. The cranial traversal stopped before T2: the maximum executed contact z was 86.374 mm while the validated T2 recess reference is z=95.5 mm. The stopping trigger was an achieved scan-width/corridor alignment of 0.948635, just below the locked 0.95 requirement. The actor did not know either value.

The physical recovery implementation succeeded, but the core coroutine had already serialized RUNNING artifacts before guarded recovery. A final-status persistence method now rewrites the actor/summary only after recovery resolution.

The blocker is therefore a combination of lateral corridor discovery, boundary-complete coverage, and robust episode finalization—not a demonstrated failure of the frozen image classifier.
