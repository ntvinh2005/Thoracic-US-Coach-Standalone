Thoracic US Coach — Repository Instructions for Codex
Collaboration contract
- For any task involving Phase 1 T2–T8 longitudinal exploration, ultrasound bone-related signal extraction, vertebral event counting, T7 identification, physical return, or evaluation, read and follow .agents/skills/thoracic-sweep-count/SKILL.md before proposing work. Explicit skill invocation: $thoracic-sweep-count.
- Work in this order: inspect current repository and evidence -> present a scoped plan -> wait for explicit user approval -> implement only the approved scope -> test -> report results. A request for a plan, skill or audit is not approval to modify the implementation.
- Do not use subagents. Do not start expensive training, large data capture, or lengthy Unity jobs without explicit approval.
- Preserve all existing code comments verbatim. Make minimal executable-line changes; do not rewrite or remove existing comments or unrelated sections. Preserve guided/oracle and existing image-only modes unless specifically approved otherwise.
- Keep Phase 1 a simple image-only sweep-and-count PoC: clean ultrasound frames + actually executed action history -> event detection -> known-T2 enumeration -> T7 physical return/reacquisition. No PPO, RL, new CNN training, atlas, full 3D, or extra architectural layers unless a measured blocker and an approved plan justify them.
- Never give the localization policy privileged Unity target transforms, T labels, world pose, score, mesh identities or reference annotations. Unity ground truth may be used for fixed-start setup, physical safety and independent evaluation only.
- Do not claim PASS/SUCCESS based on source-code review, synthetic charts, unit tests, oracle-guided motion or one good-looking episode. Report which tests ran, raw evidence, failed cases, and G0..G4 gates; use NOT_RUN or INCONCLUSIVE where appropriate.
- Use the project's existing experiment-tracker status enum if updating an authorized, connected tracker. Keep implementation, execution and verification separate. Do not claim an external tracker was updated without confirmation.
Key project paths
- Scene: Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity
- Unity controller: Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController*.cs
- Python evaluation/capture: Tools/ThoracicUSCoach/
- Evidence: Artifacts/ThoracicUSCoach/
Read the specific source files at the current commit before suggesting changes. End every task with what actually changed, what actually ran, evidence and the next approval required.