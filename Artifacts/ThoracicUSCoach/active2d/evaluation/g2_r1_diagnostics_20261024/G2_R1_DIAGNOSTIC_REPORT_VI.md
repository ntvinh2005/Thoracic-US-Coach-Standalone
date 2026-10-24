# G2-R1 — Báo cáo chẩn đoán Phase 2

Ngày chạy: 2026-10-24  
Chế độ: chỉ chẩn đoán; không retrain, không sửa source triển khai, không thay checkpoint, không đổi protocol và không chạy episode closed-loop mới.

## 1. Kết luận điều hành

**NO-GO cho retraining ngay, Unity closed-loop và Gate-2 40 episode.** Candidate G2 hiện không deploy được an toàn.

Nguyên nhân chính đã được xác nhận là **khả năng phân tách STOP không tổng quát qua các pose family**. Model tách train gần như hoàn hảo, nhưng trên calibration, positive chỉ đạt `0.0565–0.3411`, trong khi một negative đạt `0.9953`. Không có threshold nào đồng thời đạt recall ít nhất 80% và zero false positive. Fallback chọn `1.0` tránh false STOP nhưng bỏ sót toàn bộ 5/5 positive calibration và 8/8 positive heldout-development.

Đây không đơn thuần là lỗi hiệu chỉnh threshold. Dữ liệu positive quá ít và tập trung: 37/736 frame; calibration có 5 positive chỉ từ 1/4 episode, heldout-development có 8 positive từ 2/4 episode. Candidate cũng thể hiện overfit family rõ rệt: train positive `0.9743–1.0000`, train negative tối đa `0.3147`, nhưng separation đảo ngược trên calibration.

Ngoài ra có ba lỗi parity/guardrail độc lập:

1. Live server không truyền `previousActionAccepted` và `previousActionCompleted` vào temporal feature extractor; các cờ mặc định thành `true`.
2. Live server chỉ áp dụng execution feedback khi action model U08 được bật. Raster + temporal STOP vì vậy không sửa memory/odometry sau rejected hoặc incomplete movement.
3. Offline `replay_stop` dừng ở threshold crossing đầu tiên; nó không mô phỏng raster completion, best-frame memory, physical return, rejected motion hoặc fresh confirming image. Live return-to-best cũng STOP khi odometry báo đã về cell cũ mà không chấm lại một ảnh mới tại pose trả về.

Các Unity safety primitive hiện có hoạt động đúng trong phạm vi test: 34/34 EditMode pass, gồm raycast/contact/workspace/rollback/terminal forced-failure paths. Điều này không đủ để cho phép closed-loop vì STOP và parity vẫn bị chặn.

## 2. Đồng bộ repository và provenance

| Mục | Giá trị |
|---|---|
| Repository cục bộ | `D:\CSSALT\Thoracic-US-Coach-Standalone-u08` |
| Branch | `codex/u08-phase2` |
| HEAD | `535c05fd5d7361e285fc568374405dfdfd4cc6ad` |
| `origin/main` sau fetch | `71e3c248d8e69addee18d9e925b3ab3b109a2d71` |
| So với `origin/main` | ahead 18, behind 1; lịch sử đã phân nhánh |
| Trạng thái worktree | Có nhiều modified/untracked G0/G1/G2/U08 từ trước; tất cả được giữ nguyên |
| Dataset run | `g2_temporal_stop_v1_20261024` |
| Dataset fingerprint | `89b3b95983eef7d8dc556522009909079519c2a8171559f20e6e9a1bf10b6283` |
| G2 checkpoint SHA-256 | `1d93c64f9526baff07908bb7c53b5979b265d7ca7d6f36382bd860a10f5d661d` |
| Frozen spatial-v2 SHA-256 | `e0ec390f016078904807954769c3e32f5e1481fb3307e86b9e3a7e488c576792` |
| Scene SHA-256 | `6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d` |
| Feature source SHA-256 | `e59770d56c2b35d009677e0690a1266bc004198ea8ddab929dfc14e617b611a4` |
| Trainer source SHA-256 | `05f5933ce72bd6188a002de2b17f6edd83e8ffc83440031d3b79d19b1620d38e` |
| Policy server SHA-256 hiện tại | `0320e82a093c0e2fb1e8e94f12d5e3439920b4af7bc53bd8b2a35174b9df03b1` |

Không có reset, clean, checkout, commit hay push nào được thực hiện.

## 3. Ma trận test đã chạy

Môi trường: Windows 11 `10.0.26300`, Python `3.11.5` tại `C:\Users\Vinh\anaconda3\python.exe`, Unity `6000.3.9f1`.

| Test | Lệnh chính xác | Kết quả |
|---|---|---|
| Python unittest discovery | `C:\Users\Vinh\anaconda3\python.exe -m unittest discover -s . -p 'test_*.py' -v` từ `Tools/ThoracicUSCoach` | **FAIL:** 60 total, 58 pass, 1 fail, 1 error |
| Python pytest | `C:\Users\Vinh\anaconda3\python.exe -m pytest -q` từ `Tools/ThoracicUSCoach` | **FAIL:** 82 total, 80 pass, 2 fail |
| Unity EditMode | `Unity.exe -batchmode -nographics -projectPath D:\CSSALT\Thoracic-US-Coach-Standalone-u08 -runTests -testPlatform EditMode -testResults ...\unity_editmode_results.xml -logFile ...\unity_editmode.log` | **PASS:** 34/34, 0 skipped |
| G2-R1 evidence analyzer | `C:\Users\Vinh\anaconda3\python.exe Artifacts\ThoracicUSCoach\active2d\evaluation\g2_r1_diagnostics_20261024\analyze_g2_r1.py` | **PASS:** 16 manifest, 736 frame và 736 image hash hợp lệ |

Hai lỗi Python giống nhau ở cả hai runner và không bị che giấu:

- `Gate2Phase1Tests.test_manifest_is_frozen_balanced_and_direct_only`: `ValueError: policy server hash mismatch`. Frozen Gate-2 manifest không còn khớp policy server hiện tại.
- `Gate2Phase1Tests.test_unity_runner_does_not_expand_actor_request`: frozen test yêu cầu đúng sáu field, nhưng request hiện có thêm `hasPreviousActionFeedback`, `previousAction`, `previousActionAccepted`, `previousActionCompleted`.

Các test anti-cheating, image-only policy, handoff/state, STOP/calibration, G2 temporal, U08 integration còn lại đều pass trong bộ Python. Việc các test mới chấp nhận feedback trong khi frozen Gate-2 test từ chối nó là bằng chứng protocol versioning chưa được giải quyết, không phải lý do để sửa test trong pass chẩn đoán này.

Unity 34/34 bao gồm các đường lỗi cưỡng bức: raycast fail, contact enforcement fail, workspace boundary, rollback fail-safe, rotation contact/workspace rejection, invalid PhaseHandoff, invalid STOP, valid terminal regression và execution-feedback round trip.

## 4. Audit dataset và feature

### Tính toàn vẹn

- 16 episode, 736 frame, 736/736 image SHA-256 được kiểm tra lại.
- Không có manifest error, index-alignment error hoặc mismatch giữa `finalTargetAcceptable` và score floor `0.5912307500839233`.
- Split family-disjoint: 8 train, 4 calibration, 4 heldout-development; không có family overlap.
- Yaw `-6°/+6°` có mặt trong mọi split.
- U08 A/B không nằm trong train/calibration/heldout-development; chỉ là regression evidence.
- Offline numeric parity giữa training feature builder và request feature builder trên toàn train: max absolute error `0.0`.

### Coverage yếu

| Split | Frame | Positive | Episode thấy target | Near-negative trong 0.05 dưới floor | Near-negative trong 0.02 dưới floor |
|---|---:|---:|---:|---:|---:|
| Train | 368 | 24 | 5/8 | 83 | 30 |
| Calibration | 184 | 5 | 1/4 | 21 | 8 |
| Heldout-development | 184 | 8 | 2/4 | 39 | 16 |

Capture report có 608 action thực sự được phát sau reset và 0 rejected action; analyzer đếm thêm 128 reset frame để đủ 736 record. Do đó các feature `accepted/completed` trong training không có variation hữu ích để học cách xử lý rejection hoặc ineffective movement.

## 5. STOP metrics và failure cases

Candidate có architecture `standardized-temporal-quality-stop-v1`, extractor `temporal-v3`, width 1035. Threshold đã lưu là `1.0`; đây là abstention fallback, không phải operating point hợp lệ.

| Split/model | Positive score | Negative score tối đa | Recall | False positive | Episode success |
|---|---:|---:|---:|---:|---:|
| Candidate train @1.0 | `0.9743–1.0000` | `0.3147` | 0/24 | 0 | 0/8 |
| Candidate calibration @1.0 | `0.0565–0.3411` | `0.9953` | 0/5 | 0 | 0/4 |
| Candidate heldout-dev @1.0 | `0.0365–0.9981` | `0.9993` | 0/8 | 0 | 0/4 |
| Frozen spatial-v2 heldout @0.93 | `0.0818–0.3772` | `0.9725` | 0/8 | 6 | 0/4; 3 wrong-stop episodes |

Threshold sweep chỉ dùng calibration:

- `0.10`: recall 4/5 nhưng 39 false-positive frame; 4/4 episode wrong STOP.
- `0.93`: recall 0/5, 8 false-positive frame; 3 wrong STOP, 1 miss.
- `1.00`: zero false positive nhưng recall 0/5; 4/4 miss.
- Số operating point đạt recall ≥80% và zero false positive: **0**.

Heldout-development chỉ được dùng để đánh giá threshold đã frozen, không chọn lại threshold. Hai episode có target observation nhưng cả hai đều không được STOP đúng; conditional acquisition là 0/2. Hai episode còn lại không quan sát target, cho thấy navigation/coverage cũng vẫn là một failure mode riêng nếu sau này chạy closed-loop.

Quality head có Spearman heldout `0.709`, nhưng median episode best-view regret `0.0603` vượt gate `0.015`. Vì vậy khả năng ranking có tín hiệu nhưng chưa đủ để return-to-best chính xác.

## 6. Offline/live parity

### Đã khớp

- Ảnh hiện tại/trước, delta, absolute delta và action encoding khớp số học giữa training và offline request feature trên dataset hiện tại.
- Input policy không chứa pose, anatomy marker, target score hoặc evaluator field.

### Chưa khớp hoặc chưa mô phỏng

- `request_stop_features(...)` trong live server không nhận hai cờ accepted/completed, nên mặc định coi action đã thực thi thành công.
- `apply_execution_feedback(...)` chỉ chạy khi có `action_model`; raster + temporal STOP bỏ qua feedback dù Unity đã gửi.
- Live chọn best frame bằng STOP score, không dùng temporal quality head đã train.
- Policy-owned odometry dựa vào action đã phát có thể drift khi move bị reject/ineffective.
- Sau return-to-best, live phát `Stop` ngay khi chỉ số odometry bằng best index; không capture và chấm lại ảnh mới.
- Offline replay dừng ở first threshold crossing và không mô hình hóa raster completion, physical return, rollback/rejection, drift hay confirming image.

Vì vậy offline STOP metric hiện chưa đại diện cho product contract: “observe → recognize → revisit → capture fresh image → explicit safe STOP”.

## 7. Safety và movement

Static inspection và Unity tests xác nhận:

- PhaseHandoff và STOP gọi lại contact/workspace validation.
- Translation/rotation failure thực hiện rollback và ghi telemetry; rollback failure chuyển sang fail-safe rõ ràng.
- Workspace boundary và contact loss bị reject.
- Rejected movement được phân biệt với accepted terminal zero-displacement.
- Explicit final success vẫn phụ thuộc correct handoff, evaluator-positive target, contact và workspace.

Chưa có bằng chứng runtime G2-R1 về return-to-best drift hoặc fresh-image confirmation, vì hai cơ chế này chưa được mô phỏng đầy đủ và closed-loop bị chặn trước khi chạy. Kết luận an toàn là **safety primitives pass test**, không phải toàn bộ product loop đã an toàn hoặc thành công.

## 8. Nguyên nhân đã xác nhận và giả thuyết

### Đã xác nhận

1. Cross-family STOP separation collapse; không có false-STOP-safe threshold với recall hữu ích.
2. Positive family coverage rất nhỏ; hard/near negatives overlap score mạnh.
3. Fallback `1.0` được serialize như checkpoint bình thường dù report là `OFFLINE_BLOCKED`; validation/live loader không enforce `nonDeployable`.
4. Temporal live inference không sử dụng đúng accepted/completed feedback trong raster mode.
5. Offline replay và live state machine không cùng product semantics; thiếu fresh revalidation sau return.
6. Frozen Gate-2 policy-server hash và actor request contract đang fail parity.

### Chưa được chứng minh

- Cần model temporal phức tạp hơn hay chỉ cần dữ liệu family-balanced hơn.
- Bao nhiêu history frame là đủ để phân biệt opposite-label views.
- Render variation, yaw hay trajectory cue nào là nguyên nhân chính của cross-family collapse.
- Return-to-best drift thực tế lớn bao nhiêu trong controller đã sửa; cần test bounded sau khi parity gate pass.

## 9. Kế hoạch cải thiện nhỏ nhất và an toàn

Thứ tự đề xuất, cần review/approval trước khi sửa:

1. **Fail closed checkpoint:** thêm metadata deployability và buộc live loader từ chối checkpoint `OFFLINE_BLOCKED`/fallback-only. Không đổi evaluator hoặc threshold baseline.
2. **Version hóa execution-feedback contract:** công khai đây là protocol change; cập nhật manifest/hash/test frozen có chủ đích. Live luôn consume actual accepted/completed feedback và truyền đúng vào feature extractor, kể cả raster không có U08 action model.
3. **Một state machine dùng chung cho offline và live:** hoàn tất raster, lưu best bằng đúng quality signal đã định nghĩa, return theo executed-action odometry, capture ảnh mới, recompute STOP và yêu cầu confirmation trước terminal STOP. Thêm deterministic unit tests cho rejection, ineffective move và return drift.
4. **Sau đó mới duyệt một capture/training nhỏ:** tăng số independent positive families, near-boundary positive, hard negative, yaw và rejected/ineffective histories; split theo family trước capture. So sánh leave-one-family-out và calibration-only threshold selection. Không dùng consumed heldout-development để tune.
5. Chỉ khi offline gate và parity tests đều pass mới chạy bounded Unity regression; 40-episode Gate-2 vẫn cần approval riêng.

## 10. Quyết định gate

| Hành động | Quyết định | Lý do |
|---|---|---|
| Retrain ngay | **NO-GO** | Chưa chốt data/representation redesign và deployability guardrail |
| Unity closed-loop G2 | **NO-GO** | Offline STOP gate fail; live/offline parity fail |
| Gate-2 40 episode | **NO-GO** | STOP/parity blocked và frozen Gate-2 tests fail |
| Giữ Phase 1 | **YES** | Không thay đổi source/checkpoint Phase 1 trong pass này |

## 11. Evidence

- Machine diagnostics: `g2_r1_diagnostics.json`
- Machine test matrix: `g2_r1_test_matrix.json`
- Diagnostic script: `analyze_g2_r1.py`
- Unity test result: `unity_editmode_results.xml`
- Unity log: `unity_editmode.log`
- Primary prior G2 report: `../g2_temporal_stop_v1/g2_final_report.json`
- Candidate checkpoint: `../../checkpoints/g2_temporal_quality_stop_v1.json`

Không có training campaign hoặc Unity episode nào được tự động khởi động. Dừng tại đây để review.
