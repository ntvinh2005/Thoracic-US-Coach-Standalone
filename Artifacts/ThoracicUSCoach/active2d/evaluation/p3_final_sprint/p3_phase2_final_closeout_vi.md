# Phase 2 Final Closeout — Prompt 3/3

## Kết luận bắt buộc

**PHASE2_NO_GO**

Stage F-Final đã xác nhận phần kỹ thuật capture/action-feedback hoạt động đúng. Stage B-Final sau đó không tìm được positive basin liên thông tối thiểu ba cell theo protocol đã khóa. Vì prerequisite vật lý này thất bại, Stage C, Stage D, model study và paired Unity POC không được chạy.

Đây là thiếu coverage của positive basin trên bốn control thuộc cùng fixed Unity anatomy. Kết quả không chứng minh ultrasound image-only về nguyên tắc là không quan sát được.

## 1. Source và integration

- Development branch: `codex/u08-phase2`; không merge `main`.
- Unity worker: `codex/p2-unity-final`, final evidence commit `45a009b`.
- Scientific worker: `codex/p2-science-final`, final audit commit `3a39302`.
- Unity execution source Stage F: `d6b606b375ee32bb35bd02d6f9c3cda0c5202202`.
- Unity execution source Stage B: `a87f4a702f6b23ed08659b2ffaace17d361d48a2`.
- Integration commits: `a339a15` và `06a7a24`.

## 2. Fresh image mechanism

Một helper direct-producer dùng chung đã được tích hợp cho direct pose restoration, marker/skin placement, mapping candidate, movement, rejected/rollback, OBSERVE, confirmation và live/diagnostic controller.

Mỗi accepted image yêu cầu pose epoch hiện tại, Unity frame mới hơn pose application, producer scan serial tăng, đúng một `UltrasoundManager.PublishCurrentPose()`, capture path `direct_producer_texture`, mode `DIRECT_PRODUCER_CAMERA`, và zero active aggregator. Waiting có giới hạn và fail closed; camera readback đơn lẻ không đủ.

Stage F snapshot có 100 physical records: 50 accepted, 50 discarded warmups; 20 action attempts, gồm 16 completed, 4 rejected và 4 rollback; không có safety failure.

## 3. Historical F3 và fault injection

- Historical Prompt 2 F3 được giữ nguyên: `IMAGE_POSE_FRESHNESS_FAIL`, do frozen run không quan sát được factual `accepted=true, completed=false`.
- Prospective real physical controls A→B→A→C→D và lateral/longitudinal/rotation/rejected/rollback/observe đều PASS.
- Synthetic incomplete feedback được cách ly hoàn toàn khỏi physical/scientific/model counts.
- C# bridge xác nhận không bịa partial displacement; production Python reducer nhận `accepted=true, completed=false`, giữ odometry/best/visited state, chuyển ABSTAIN và không STOP. Offline replay độc lập khớp production result.

Prospective Stage F signals:

- `REAL_IMAGE_POSE_FRESHNESS`: PASS
- `REAL_ACTION_FEEDBACK_INTEGRITY`: PASS
- `INCOMPLETE_FAULT_INJECTION`: PASS
- `CAPTURE_PATH_INTEGRATION`: PASS
- `FINAL_CAPTURE_ENGINEERING_PASS`: true

## 4. Stage B positive-basin mapping

Frozen grid gồm 212 base poses và 108 conditional refinement slots, giới hạn tổng 320. Base mapping hoàn tất sạch:

- Base attempted: 212/212
- Physical records: 848
- Accepted fresh images: 424
- Discarded warmups: 424
- Substantive freshness/integrity errors: 0
- Strict passing base candidates: 8
- Largest connected positive component: 2 cells
- Required connected component: ≥3 cells
- Refinement: 0/108, vì hai hard controls không có passing coarse/wider center

Passing coarse/wider centers theo anchor: `hard_00=0`, `random_01=2`, `random_05=2`, `hard_01=0`.

Verdict: `POSITIVE_BASIN_FAIL`.

## 5. Các stage bị chặn

- Stage C target-independent physical reachability: `BLOCKED_NOT_RUN`
- Stage D development data/readiness: `BLOCKED_NOT_RUN`
- Recognition/model study: `BLOCKED_NOT_TRAINED`
- Paired end-to-end Unity POC: `BLOCKED_NOT_RUN`

Không có model/checkpoint mới; không có threshold calibration; không có demo launch được promotion. Vì vậy không có hướng dẫn chạy “working demo” cho cycle này.

## 6. Jobs và test matrix

| Job | Kết quả | Ý nghĩa |
|---|---:|---|
| `0f1e4d067a4f` | FAIL | Stage F preflight compile failure, preserved |
| `8f9373a594a6` | PASS 10/10 | Stage F engineering preflight |
| `cdf91aaf7a2d` | PASS | Stage F physical outcome sealed |
| `2a48190b60c1` | FAIL | Stage B runner PowerShell hash infrastructure |
| `429947a0b84e` | FAIL 6/7 | Stage B test false-positive, no capture |
| `f9f89dab9dde` | PASS 7/7 | Stage B engineering preflight |
| `8286fcd6e116` | FAIL | Outcome-bearing Stage B basin failure; no retry |

Final Python regression: 92/92 PASS. Hai infrastructure/test-only failure Stage B xảy ra trước outcome và được sửa cơ học với manifest/output mới. Outcome-bearing failure không được retry.

## 7. Frozen contract integrity

- Phase 1 Gate 2: 36/40 precise T7 handoffs, unchanged.
- Original evaluator and `ScoreCandidate()`: unchanged.
- Acceptable floor: `0.5912307500839233`, unchanged.
- Frozen spatial-v2 STOP threshold: `0.93`, unchanged.
- Existing actuator/contact/workspace behavior: unchanged.
- Historical locked families và reserved P2 confirmation cohort: không được dùng.
- Live actor restriction vẫn là clean ultrasound, policy memory và causal executed-action feedback.

Frozen identities: scene canonical SHA `6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d`; evaluator canonical SHA `7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc`.

## 8. Evidence và provenance

Stage F immutable handoff:

- Path: `D:\CSSALT\Thoracic-US-Coach-P3-Handoff\stage_f_final\p3_stage_f_final_20261026`
- Seal SHA: `93a15b1abb6b29ff4e4e3ceccc6b492ca0079b33a9bc8362a10e24a57c708f0c`
- Independent audit SHA: `af574b9b515362e09f6044c4a21e01df216f8fe0cb7e8133514cd61212414d65`

Stage B immutable failure handoff:

- Path: `D:\CSSALT\Thoracic-US-Coach-P3-Handoff\stage_b_final\p3_stage_b_final_v3_failed_20261026`
- Seal SHA: `e12a65ad154e5996d8977099596ab2eb805ec8cde9ea03e94407a91a2ee11131`
- Evidence-index SHA: `d0c6b1c1040c0d2c7d3db26fdf32661299b9f1d9cdca8b8000209954a520cb09`
- Independent audit SHA: `e2fba899c2cb0ace00cabac2fd870e4edf11f64a087c4f32302c587d0475f64f`
- Integrity: 859/859 indexed files valid; read-only failure snapshot.

## 9. Reproduction

Re-run audits only; không re-run Unity outcome:

```powershell
cd D:\CSSALT\Thoracic-US-Coach-Standalone-u08\Tools\ThoracicUSCoach
& "$env:USERPROFILE\anaconda3\python.exe" -m unittest test_g2_p3_stage_b_failure_audit test_g2_p3_stage_b_final test_g2_p3_stage_f_final test_g2_p3_conditional_evaluation test_materialize_p3_stage_b_final test_p3_production_fault_artifact test_phase2_stop_state test_u08_live_integration test_g2_p2_freshness_audit test_g2_p2_basin_audit test_g2_p2_recognition -v
```

Các auditor CLI hỗ trợ `--help` tại `g2_p3_stage_f_final_audit.py` và `g2_p3_stage_b_failure_audit.py`; phải truyền đúng source, manifest và seal hashes nêu trên.

## 10. Giới hạn và backlog hoãn lại

Không có claim clinical, unseen-patient, independent generalization hay deployability. Không có bằng chứng Phase 2 actor có thể hoàn thành random-start acquisition/STOP. Nghiên cứu về basin coverage, anatomy diversity, target-independent reachability, causal development data và recognition model được hoãn; Prompt 3 không cho phép tự động mở R7/R8 hoặc campaign mới.

## Tuyên bố cuối

Hoạt động: direct-producer image freshness, physical action feedback, rollback/rejected handling và deterministic incomplete fail-closed semantics.

Không hoạt động/không được xác nhận: positive basin đủ rộng, target-independent reachability, development-data readiness, image-only recognition/STOP model và end-to-end Unity POC.

**Final status: PHASE2_NO_GO.**
