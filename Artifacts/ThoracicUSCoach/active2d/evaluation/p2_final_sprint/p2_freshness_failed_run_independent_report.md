# G2 Phase 2 Final Sprint — Independent Stage F Failure Audit

## Decision

`IMAGE_POSE_FRESHNESS_FAIL`

The immutable failed-run snapshot passes the independent integrity audit, but the
frozen F3 gate is not satisfied. The 16-action budget produced no factual
accepted-but-incomplete action (`actionAccepted=true && actionCompleted=false`).
Rejected actions and rollback attempts do not substitute for that category.

No retry is authorized. Corrected basin mapping, wide scouting, development
capture, training, and the Unity image-only POC remain blocked.

## Immutable lineage

- Handoff seal SHA-256: `8997eadb08554cc1bd9dbcba5098c6be401897284da0623fa8fd750ce98e0d2c`
- Source commit: `c93c18c6af9e9140ed9722cdd4a85bb8470be094`
- Transition manifest SHA-256: `de9c833e2f660da21b5e89e9e818239dcef0f82a843783eec9ae7566845307b7`
- Frozen audit protocol canonical SHA-256: `588c32e6b84bf70789ffa039806d7b70b4bfc3c6b301674ce3bc58e94ea83f24`
- Independent JSON audit SHA-256: `416cad291440c824f20d4ab0b4fed1e49d16c34f19194e31e6cbb18f63580a69`
- Snapshot integrity: `PASS`

## Partial evidence (not a gate pass)

- The exact five-visit A→B→A→C→D control sequence passed.
- All four independent positive-control families passed.
- Lateral, longitudinal, rotation, rejected, rollback, and observe cases were
  present and their factual visits passed.
- Action facts: 4 rejected, 4 rollback, 0 accepted-but-incomplete.
- The movement gate therefore failed solely on the missing `incomplete`
  category.

These partial passes do not override the fail-closed Stage F result.
