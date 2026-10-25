# G2-R4 Revised Recovery — P0/P1

Ngày chạy: 2026-10-25

Kết luận:

- P0 baseline parity: **PASS**.
- P1 engineering/safety: **PASS**.
- Bounded P2 development study: **GO**.
- Independent confirmation, deployment và promotion: **BLOCKED**.

## 1. Tóm tắt điều hành

Frozen spatial-v2 không bị đánh giá sai theo hướng làm baseline yếu đi trong R4-A. Các ảnh fresh R3-A2 được Unity restore trực tiếp tới từng pose rồi render; không có actor action nào dẫn tới ảnh đó. Vì vậy `RESET` là causal live input đúng. Kết quả historical RESET được tái lập chính xác và giống byte-for-byte với causal exact-restore protocol.

P1 đã thu được 16/16 family mới: tám randomized-start và tám target-enriched. Có 10 family chứa ít nhất một acceptable view. Toàn bộ 1.120 ảnh được kiểm tra tồn tại, hash, độ phân giải 1024×1024, label/floor, contact, workspace và action alignment; không có integrity error.

Raw PNG và full case manifests được giữ local theo protocol. Repo chỉ công bố source, predeclared manifests, compact hash-linked summary và báo cáo này.

## 2. P0 — Baseline parity

Checkpoint giữ nguyên:

- `phase2_stop_live_spatial_logistic_v1.json`
- SHA-256 `E0EC390F016078904807954769C3E32F5E1481FB3307E86B9E3A7E488C576792`
- Feature extractor `spatial-v2`
- STOP threshold `0.93`

Kết quả trên 90 fresh renders có stable pose label, gồm 30 positive:

| Protocol | Recall | FPR | Balanced accuracy | ROC-AUC |
| --- | ---: | ---: | ---: | ---: |
| Historical RESET diagnostic | 53,33% | 1,67% | 75,83% | 88,28% |
| Frozen training-action counterfactual | 40,00% | 5,00% | 67,50% | 90,06% |
| Causal live exact-restore | 53,33% | 1,67% | 75,83% | 88,28% |

Historical result được tái lập đúng 16 TP, 1 FP, 14 FN và 59 TN. Historical RESET và causal exact-restore có cùng feature hash trên toàn bộ 96 ảnh.

Nếu gắn `issuedAction` của trajectory nguồn vào fresh restored image, baseline trông yếu hơn: recall giảm 13,33 điểm phần trăm, FPR tăng 3,33 điểm và balanced accuracy giảm 8,33 điểm. Nhưng đây là counterfactual vì action đó tạo source image cũ, không tạo fresh image. Không dùng số này để thay thế historical result.

Kết luận về R4-A CNN không đổi: action timing không giải cứu CNN và không làm invalid baseline comparison. Frozen baseline vẫn là bằng chứng cross-family mạnh hơn CNN R4-A.

## 3. Semantics của action

- Raster stream: `issuedAction` trên row ảnh `I_t` là action đã thực thi trước khi `I_t` được capture. Initial frame dùng `RESET`.
- Live actor: Unity capture `I_t`, sau đó gửi feedback của command được phát sau `I_(t-1)`. Server dùng action đó khi cả `accepted` và `completed`; rejected/incomplete được đổi thành `RESET`.
- Exact-pose R3-A2: pose được restore ngoài actor, sau đó render lặp. Không có action nhân quả, nên `RESET` là đúng.
- Historical spatial-v2 trainer: mã hóa `frame.issued_action` trực tiếp và không dùng accepted/completed flags.

Có một defect riêng trong historical live-adaptation data: 94/11.271 observation, tương đương 0,83%, đứng sau rejected action nhưng trainer vẫn mã hóa action string thay vì `RESET`. P0 chỉ ghi nhận; không retrain và không sửa production STOP.

## 4. P1 — Family và provenance

Main predeclaration SHA-256: `708CC803BABDF0C1ED4FE7388478CFA2B7C8911A82304A71DBC2DC29B94ED3DA`.

16 family mới có family ID, capture seed, preposition và trajectory riêng. Không dùng lại source image identity, không dùng duplicate pose sequence giữa các family. Tất cả vẫn nằm trên cùng một Unity anatomy; đây không phải patient-level generalization.

Sáu historical locked families vẫn sealed và không được mở:

- `test_family_0044`
- `test_family_0054`
- `test_family_0059`
- `test_family_0069`
- `test_family_0089`
- `test_family_0099`

Không có collision giữa family ID mới và locked family ID.

## 5. Capture outcomes và coverage

| Arm | Family hoàn tất | Family có target | Observation | Positive | Near-boundary negative | Hard negative |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Randomized-start | 8/8 | 2/8 (25%) | 648 | 7 | 23 | 64 |
| Target-enriched | 8/8 | 8/8 (100%) | 416 | 52 | 33 | 85 |
| Tổng family mới | 16/16 | 10/16 | 1.064 | 59 | 56 | 149 |

Không family mới nào bắt đầu trực tiếp trên acceptable target. Arm A không recenter bí mật và mọi target-not-observed case đều được giữ. Arm B dùng privileged planning có chủ đích, vì vậy 8/8 không được báo cáo như randomized success.

Margin-to-floor trên toàn bộ 1.120 observation, bao gồm causal diagnostics:

- Min `-0,32157`
- Median `-0,14590`
- Q90 `-0,00950`
- Max `+0,05674`

Action coverage gồm cranial, caudal, medial, lateral và yaw hai chiều. 759 translation có median achieved movement `3,00000 mm`; 251 rotation có median `2,99970°`.

## 6. Label stability

Có 276 nhóm repeat/return observation ở pose gần như giống nhau. Hai nhóm đổi label quanh frozen floor:

- `r4p1_enriched_00`: score `0,591311`, `0,591283`, `0,591098`.
- `r4p1_hard_supplement_0045`: score khoảng `0,591008–0,591488`, có cả positive và negative.

Các frame này được giữ nguyên thành label-unstable stratum. Không smoothing, xóa hard case, đổi floor hoặc thêm acceptance margin.

## 7. Genuine causal multi-view

56 observation hợp lệ được capture từ bốn consumed hard families thành công: `0090`, `0066`, `0077`, `0045`. Mỗi sequence dùng actuator thật với yaw/lateral hai chiều và repeat observations.

- Positive: 14
- Near-boundary positive: 14
- Near-boundary negative: 12
- Hard negative: 24
- Rejected/incomplete/safety actions: 0/0/0

Hai start predeclared bị loại trước capture và vẫn được báo cáo:

- `dev_family_0058/105`: contact/workspace valid nhưng không còn T7-valid.
- `train_family_0080/52`: contact/workspace valid nhưng không T7-valid.

Không observation nào từ hai start này được dùng để tăng support. Supplement cuối `0045/100` được predeclare là lần thử cuối và hoàn tất 14/14 observation, đưa tổng causal từ 42 lên 56.

## 8. Safety và no-cheating

- Validated images: 1.120; unique hashes: 1.120.
- Accepted actions: 1.126.
- Rejected actions: 0.
- Accepted-but-incomplete: 0.
- Safety failures: 0.
- Contact violations: 0.
- Workspace violations: 0.
- Label/frozen-floor mismatches: 0.
- Actor inference không được dùng pose, score, anatomy label, target direction hay evaluator output.
- Privileged geometry chỉ được dùng trong capture planning, annotation và offline audit.

Scene SHA-256: `6C86FF30A42E9719AFF712A9E48B2CA6B5CCE6004BACE055ABBC82B64DB9A02D`.

Evaluator SHA-256: `C37D37C856283D5576F4678587E6865E69B6FB2F04232E027BE04ED2066049D8`.

## 9. Tests và failures

- Existing Python unittest: 30/30 PASS.
- New P0/P1 unittest: 11/11 PASS.
- Pytest combined relevant suite: 41/41 PASS.
- Unity preflight EditMode: 34/34 PASS.
- Unity final EditMode trên source cuối: 34/34 PASS.
- Main Unity capture: 16/16 new families hoàn tất; một consumed hard start bị reject.
- Hard supplement 1: start `0080/52` bị reject vì không T7-valid.
- Hard supplement 2: 14/14 observation hoàn tất.

Hai Unity attempts trong sandbox fail trước compile với exit 198 do licensing entitlement lookup. Cả hai log được giữ local. Cùng command chạy ngoài sandbox đã PASS; đây là infrastructure failure, không được xóa khỏi test matrix.

## 10. GO/NO-GO

**GO cho một bounded P2 development study.** Pilot đã chứng minh có thể thu reproducibly 10 positive families, 149 hard negatives, 56 near-boundary negatives và causal sequences trong safety contract.

**BLOCKED cho independent confirmation, locked evaluation, Unity STOP promotion và deployment.** Toàn bộ 16 pilot family đã được inspect nên là consumed exploratory evidence. Chưa có predeclared family-disjoint confirmation set. P2 chỉ được dùng pilot này cho development/training screen và phải thu/freeze confirmation roles mới trước bất kỳ promotion claim nào.

P2/P3, CNN training, contrastive learning, threshold tuning, locked-test access, paired closed-loop STOP và 40-episode benchmark đều không chạy trong task này. Phase 1, frozen spatial-v2 weights, threshold `0.93`, evaluator và floor được giữ nguyên.

## Evidence

- `g2_r4_p0_baseline_parity.json` — SHA-256 `B960DF017056D2BA85E5536EE3E64EF81959EEC705F1164A5F9DA83CE5B1CB51`
- `g2_r4_p1_capture_summary.json` — SHA-256 `3A6EF11D4FF1DB80447E895819C9419E916A044331AE4523A8FE7D894AD202B4`
- `test_execution_matrix.json`
- `p1_final_editmode.xml` — SHA-256 `32BD2E309F3D72A205C690AAB47A1C3FC6D3BC6A52C8C70D15AAEE4C7AA7A326`

Raw roots local-only:

- `Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_raw_20261025`
- `Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_20261025`
- `Artifacts/ThoracicUSCoach/active2d/g2_r4_p1_hard_supplement_v2_20261025`
