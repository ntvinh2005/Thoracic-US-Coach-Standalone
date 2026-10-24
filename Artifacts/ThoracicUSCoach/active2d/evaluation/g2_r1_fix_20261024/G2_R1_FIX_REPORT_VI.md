# G2-R1 Fix — Báo cáo triển khai và kiểm chứng

Ngày: 2026-10-24  
Branch làm việc: `codex/u08-phase2`  
HEAD nền khi chạy: `61721f060ada9b1b2b2bd042136bf88027b17db9`

## 1. Tóm tắt điều hành

Kết quả cuối cùng là **ENGINEERING PASS / SCIENTIFIC BLOCKED**.

Các lỗi triển khai đã được sửa và kiểm chứng:

- Checkpoint không đạt gate được đánh dấu `BLOCKED`; live loader từ chối load.
- Phase 1 giữ request protocol v1 đúng sáu field; Phase 2 dùng protocol v2 có execution feedback rõ ràng.
- Raster thường, không chỉ U08, phải chờ accepted/completed feedback trước khi cập nhật odometry.
- Rejected hoặc incomplete movement không được xem là probe đã tới cell kế tiếp.
- Offline và live dùng cùng state machine cho exploration, best-view memory, return, yaw recovery, fresh confirmation và STOP.
- Khi quay lại best cell, policy phát `Observe`, Unity kiểm tra contact/workspace và render frame mới; chỉ frame kế tiếp đủ STOP evidence mới được phép phát `Stop`.
- Retry return bị giới hạn; failure chuyển thành abstention có reason, không lặp vô hạn.

Tuy vậy, Phase 2 **chưa deployable**. Corrected temporal-v3 chỉ đạt recall an toàn `10/16 = 62,5%`. Hybrid dùng frozen spatial-v2 để nhận diện STOP và temporal-v3 để xếp hạng best view đạt `5/7 = 71,4%` trên một calibration family set khác. Cả hai đều dưới gate 80%, dù không có false STOP. Locked-test được giữ kín và Unity closed-loop không chạy.

Không có claim lâm sàng, ảnh siêu âm thật hoặc generalization sang anatomy/patient khác.

## 2. Các thay đổi kỹ thuật

### F1A — Checkpoint fail-closed

- Temporal checkpoint version 2 bắt buộc có `deployment.gate`, `status`, `safeOperatingPoint` và `reason`.
- Nếu không có operating point an toàn, trainer lưu `stopRule.threshold = null`, `status = BLOCKED`; diagnostic fallback chỉ nằm trong report.
- `load_stop_checkpoint(..., require_deployable=True)` chặn checkpoint temporal cũ không có deployment decision và mọi checkpoint `BLOCKED`.
- Historical non-temporal v1 baseline được giữ tương thích theo nhánh `legacy-v1`; không đổi threshold hoặc identity.
- Threshold override bị cấm với checkpoint versioned nếu khác threshold đã frozen.

Live-load test trên hybrid checkpoint trả đúng:

`STOP checkpoint is non-deployable: confirmation-calibration-gate-failed`

### F1B — Protocol và execution feedback

- `active2d_image_only_v1`: request Phase 1 legacy, đúng sáu field.
- `active2d_image_only_v2`: Phase 2 request có `previousAction`, `accepted`, `completed`.
- Phase 2 luôn consume feedback, kể cả raster không dùng U08 action model.
- Search schedule có thể consume một attempt đã acknowledged để tránh loop, nhưng physical odometry chỉ tiến khi action vừa accepted vừa completed.
- Partial/clamped movement không được giả định bằng commanded displacement.
- `RESET` history vẫn tách biệt với executed motion.
- Actor input không có pose, marker, target score, true distance hoặc evaluator label.

Frozen Gate-2 hash được kiểm tra với policy-server blob tại `sourceCommit`, kể cả CRLF Windows, thay vì bắt source hiện tại giữ byte-identical. Manifest, stored hash và threshold không bị sửa. Historical Phase-1 report vẫn là 36/40 precise handoff, zero wrong-level handoff và thresholds PASS.

### F1C — Một state machine cho offline và live

State machine mới quản lý:

`PREFIX → EXPLORE → RETURN → CONFIRM → STOPPED/ABSTAIN`

Quality chỉ xếp hạng các location đã vượt STOP evidence. Quality cao một mình không làm view acceptable. State machine theo dõi lateral, longitudinal và yaw bằng acknowledged movement. Nó yêu cầu `Observe` tại recovered best location trước STOP, có failure attribution và giới hạn retry.

Unit test đưa cùng chuỗi observation/action/feedback qua offline replay và live server integration cho output giống hệt:

`Lateral → Medial → Observe → Stop`

## 3. Dữ liệu và split

Không capture ảnh Unity mới và không copy/publish image dataset. Kho evidence local cũ có 100 family/12.100 frame, đủ để tạo comparison family-disjoint tốt hơn tập G2 16 episode ban đầu.

### Comparison manifest 1

- Fingerprint: `d444256472fbcbf71350b025a527cce05e4c7cf71d110a3a1c8512346ff50cbe`
- Train: 12 family, 1.452 frame, 6 positive family, 40 positive frame.
- Calibration: 6 family, 726 frame, 3 positive family, 16 positive frame.
- Locked-test: 6 family, gồm 3 positive family; không load vì calibration fail.

### Hybrid confirmation manifest

- Fingerprint: `f7970c6cd12ba7b56b0d0bf4f7ef4b407ac0f97e35623cf34aec206da8f3e4b2`
- Train: giữ nguyên 12 family.
- Confirmation calibration: 8 family khác, 968 frame, 4 positive family, 7 positive frame.
- Locked-test: giữ nguyên và tiếp tục SEALED.

Mỗi manifest được tạo trước lần train/evaluate tương ứng. Threshold của hybrid là frozen spatial-v2 `0.93`, không tune.

Tất cả action trong capture dùng cho comparison đều accepted/completed; do đó robustness với rejection được chứng minh bằng deterministic tests, chưa bằng training distribution.

## 4. Chẩn đoán observability và distribution

Trong comparison train+calibration, cả 56 positive frame đều có một negative frame với raw16 RMS nhỏ hơn `0.02`; minimum `0.000884`, median khoảng `0.00297`. Confirmation set cho kết luận tương tự với 47/47 positive frame.

Đây là bằng chứng mạnh rằng representation raw16 đơn-frame có ambiguity, nhưng không chứng minh full-resolution multi-view là không quan sát được. Kết quả temporal quality Spearman cao cho thấy history vẫn chứa tín hiệu xếp hạng; phần chưa đủ là safe binary acceptance recall qua family.

## 5. Controlled comparison

### Calibration 6 family — cùng dữ liệu/protocol

| Model | Recall | FP | Wrong STOP episode | Quality Spearman | Median best-view regret | Gate |
|---|---:|---:|---:|---:|---:|---|
| Current raw16 | 2/16 = 12,5% | 0 | 0 | 0,9730 | 0,00481 | FAIL recall |
| Temporal images, không action | 10/16 = 62,5% | 0 | 0 | 0,9748 | 0,00023 | FAIL recall |
| Temporal-v3 full | 10/16 = 62,5% | 0 | 0 | 0,9747 | 0,00000 | FAIL recall |
| Frozen spatial-v2 @0.93 | 14/16 = 87,5% | 0 | 0 | -0,0683 | 0,01769 | FAIL ranking/regret |

Ba seed 13/29/47 của logistic convex cho kết quả giống nhau; không có seed-shopping. Temporal checkpoint được lưu `BLOCKED`, threshold `null`.

### Hybrid confirmation — family khác

Hybrid dùng nguyên frozen spatial-v2 weights/threshold cho STOP và deterministic temporal ridge cho quality ranking.

| Metric | Kết quả | Gate |
|---|---:|---:|
| Recall | 5/7 = 71,4% | ≥80% — FAIL |
| False positive | 0/961 | 0 — PASS |
| Wrong STOP episode | 0/8 | 0 — PASS |
| Target-observed episode | 4/8 | — |
| Target-recognized episode | 3/4 | — |
| Quality Spearman | 0,9659 | ≥0,60 — PASS |
| Median best-view regret | 0,01464 | ≤0,015 — PASS sát biên |

Một positive family (`train_family_0097`) có maximum STOP score `0.9129`, thấp hơn frozen threshold `0.93`. Không hạ threshold vì điều đó trái frozen protocol và sẽ là tuning theo confirmation data.

Hybrid checkpoint cũng `BLOCKED`. Locked-test không được mở.

## 6. Test matrix

| Test | Kết quả |
|---|---|
| Python unittest discovery | 72/72 PASS |
| Python pytest | 94/94 PASS |
| Unity EditMode lần 1 | Compile FAIL do test U08 còn dùng feedback field trên request v1 |
| Unity EditMode sau migration test sang v2 | 34/34 PASS, 0 skipped |
| Blocked checkpoint live-load | PASS: bị từ chối đúng reason |
| Offline/live identical synthetic sequence | PASS |
| Frozen Phase-1 Gate-2 compatibility | PASS |
| `git diff --check` trên tracked implementation | PASS |

Log compile fail được giữ lại; không che giấu hoặc xóa evidence.

## 7. Gate decisions

| Gate | Quyết định |
|---|---|
| F1A checkpoint safety | PASS |
| F1B protocol/feedback | PASS |
| F1C offline/live state parity | PASS trên deterministic sequences |
| Python/Unity safety tests | PASS |
| Temporal-v3 scientific gate | FAIL |
| Hybrid confirmation gate | FAIL |
| Locked independent test | NOT RUN — SEALED |
| Bounded Unity closed-loop | NOT RUN — BLOCKED |
| STOP deployable | NO |
| Phase-2 40 episode | NO-GO |

Engineering fixes cải thiện tính đúng đắn và fail-safe, nhưng chưa có bằng chứng Phase 2 cải thiện actual probe acquisition. Không được dùng kết quả test xanh để thay cho product success.

## 8. Root causes xác nhận và giả thuyết còn lại

### Đã xác nhận

1. Fallback threshold trước đây có thể tạo checkpoint nhìn hợp lệ dù gate fail; nay đã fail-closed.
2. Feedback/odometry và offline/live semantics trước đây không đồng nhất; nay đã có cùng state transition logic.
3. Safe STOP recognition vẫn thiếu recall qua independent family dù tăng positive-family support.
4. Spatial STOP và temporal quality giải quyết hai phần khác nhau; ghép chúng vẫn bỏ sót một positive family trên confirmatory calibration.
5. Positive và negative có thể rất gần nhau trong raw16 representation.

### Chưa giải quyết

- Cửa sổ hai frame có đủ để giải quyết opposite-label ambiguity hay cần sequence dài hơn.
- Full-resolution structural features có cải thiện recall mà vẫn zero FP hay không.
- Rejected/incomplete action histories trong training có giúp live robustness hay chỉ cần state machine xử lý.
- Physical return drift và fresh confirmation rate thực tế; chưa được chạy vì recognition gate fail.

## 9. Bước tiếp theo nhỏ nhất

Không nên hạ threshold hoặc mở locked-test. Experiment tiếp theo nên là một thiết kế mới, được review trước:

1. Giữ frozen spatial threshold `0.93`, evaluator và state machine hiện tại.
2. Thu hoặc tái sử dụng sequence dài hơn quanh các positive family bị miss và hard negatives gần biên; có repeat render, yaw và rejected/incomplete feedback.
3. Dùng family-level leave-one-out để chọn representation, ưu tiên full-resolution structural/multi-view evidence thay vì model lớn hơn ngay.
4. Dành một calibration family set mới; chỉ khi đạt ≥80% recall và zero false STOP mới mở locked-test.
5. Sau locked-test PASS mới chạy tối đa bốn paired Unity development episodes để đo observe/recognize/return/fresh-confirm/STOP riêng biệt.

## 10. Evidence và hashes

- Frozen spatial-v2: `e0ec390f016078904807954769c3e32f5e1481fb3307e86b9e3a7e488c576792`
- Blocked temporal-v3: `27f695702c4a776d4b0a00943b3bdd8faa4388bd49a031c75ce36ef2a14a08d8`
- Blocked hybrid: `ebd2b2f7b32c12c1ac0d60a7410205ce92e7670bae4f6872cb8624d7711fa624`
- Policy server source: `2c8b0fac07862a199effcf823cfd4584ea8a80ebd7906eef25027c1a25956274`
- Shared Phase-2 state source: `a30ee3a7b02f2efea6eed712a8f91ddc9177a5414979102a60685e0d22c7adeb`

Machine-readable evidence:

- `g2_r1_fix_results.json`
- `g2_r1_fix_test_matrix.json`
- `g2_r1_controlled_comparison.json`
- `g2_r1_hybrid_confirmation.json`
- `unity_editmode_results.xml`
- `unity_editmode.log`
- `unity_editmode_attempt1_compile_fail.log`

Không merge main, không push, không thay frozen Phase-1 checkpoint và không chạy benchmark lớn.
