# G2-R4 — Recognition Recovery

Ngày chạy: 2026-10-25

Kết luận R4-A: **INCONCLUSIVE**

R4-B/R4-C/R4-D: **BLOCKED bởi dependency gate**

## Tóm tắt điều hành

Một CNN nhỏ 28.409 tham số đã được triển khai và chạy trên 96 fresh ultrasound renders thuộc 16 pose/11 family của G2-R3-A2. Architecture, preprocessing, split, ba seed và budget được khóa trước training. Model chỉ nhận ảnh grayscale 128×128; Unity score, anatomy, pose và geometry không đi vào inference.

CNN chứng minh được hai điều kỹ thuật:

- Memorization trên 15 stable-label poses đạt 100% ở cả seed 13/29/47.
- Khi train trên repeats 0–2 và kiểm tra repeats 3–5 của cùng pose, stable stratum đạt recall 100% và FPR 0% ở cả ba seed.

Nhưng đây là repeats tương quan của cùng pose, không phải bằng chứng generalization. Khi family được giữ hoàn toàn ngoài training, CNN kém hơn cả hai baseline:

| Representation | Median cross-family recall | FPR | Balanced accuracy |
| --- | ---: | ---: | ---: |
| Frozen spatial-v2 | 53.3% | 1.7% | 75.8% |
| Structural-v4 diagnostic | 60.0% | 20.0% | 70.0% |
| Small CNN | 40.0% | 10.0% | 60.0% |

CNN cần balanced accuracy ít nhất 80.8% theo gate predeclare, nhưng chỉ đạt 60.0%. Fold chứa family 0090 có recall 0% ở cả ba seed. Vì vậy CNN học được pose đã thấy nhưng chưa học feature tổng quát hóa qua family.

Không có checkpoint deployable, không capture dataset mới, không mở locked test và không chạy Unity closed-loop pilot.

## R4-A — Learned representation diagnostic

### Cấu hình đã khóa

- Input: clean grayscale ultrasound 1024×1024 resize bilinear xuống 128×128.
- Normalization: scalar mean/std chỉ từ training images của từng experiment/fold.
- CNN: bốn convolution stages 8/16/24/32 channels, GroupNorm, adaptive 4×4 pool, dense 32, dropout 0.10, binary head.
- Optimizer: AdamW, learning rate 0.001, weight decay 0.0001.
- Loss: BCEWithLogits cân bằng theo family/pose và class.
- Augmentation: không dùng.
- Seeds: 13, 29, 47.
- Memorization budget 300 epochs; same-pose và cross-family 160 epochs.
- Không architecture search, không threshold tuning.

Protocol SHA-256: `51aecb4f67a1b214ec7e793a926be2913914b0fc73171d2e6419f3c2936968b8`.

### Memorization feasibility

Stable poses đạt accuracy 100% sau 38–46 epochs. Accuracy toàn bộ 96 frames là 97.9%; lỗi còn lại nằm ở pose near-boundary có hai positive và bốn negative repeats gần như cùng hình. Kết quả này cho thấy implementation và model capacity đủ để fit các pose ổn định, nhưng không chứng minh generalization.

### Same-pose repeat robustness

Train repeats 0–2, đánh giá repeats 3–5:

- Stable recall: 100% ở ba seed.
- Stable FPR: 0% ở ba seed.
- Unstable pose accuracy dao động 0–66.7%.

Đây là correlated same-pose evidence. Nó chỉ xác nhận robustness với renderer variation nhỏ quanh pose đã thấy.

### Cross-family discrimination

Ba fold được khai báo cố định, không để một family xuất hiện ở cả train và evaluation. CNN không đạt improvement criterion:

- Seed 13: recall 40%, FPR 10%, balanced accuracy 65%.
- Seed 29: recall 20%, FPR 10%, balanced accuracy 55%.
- Seed 47: recall 40%, FPR 20%, balanced accuracy 60%.
- Fold 0090/0010/0066: CNN recall 0% ở cả ba seed; structural-v4 cũng 0%.

Embedding của memorization model tách được 0090/100 khỏi 0090/101 và 0066/100, nhưng representation này không chuyển thành correct leave-family-out decisions. Đây là bằng chứng overfit diagnostic, không phải recovered recognition.

Đối chiếu ảnh dùng đúng hai contact sheet đã được tạo ở R3-A2, không tạo hoặc chọn lại ví dụ sau khi thấy kết quả:

- `../g2_r3_a2_20261025/g2_r3_a2_pairs_blinded.png`
- `../g2_r3_a2_20261025/g2_r3_a2_pairs_annotated.png`

Các contact sheet cho thấy 0090/100–101 và 0066 có khác biệt pixel vượt renderer noise đã đo, nhưng cross-family fold chứa chúng vẫn có recall 0%. Vì vậy bằng chứng hiện tại nghiêng về lỗi representation/generalization hơn là lỗi model không nhìn thấy byte ảnh hoặc preprocessing làm mất toàn bộ tín hiệu.

### R4-A decision

Gate yêu cầu đồng thời:

- stable memorization ≥98%; đạt 100%;
- same-pose stable recall ≥80%, FPR ≤5%; đạt;
- cross-family balanced accuracy tốt hơn cả baseline ít nhất 5 điểm phần trăm, recall ≥60%, FPR ≤20%; không đạt.

Do prerequisites kỹ thuật đạt nhưng generalization gate fail, kết quả là **INCONCLUSIVE**, không phải FAIL về khả năng fit và không phải PASS cho capture/training tiếp theo.

## R4-B — Provenance và capture

Family ledger đã reconcile 100 source families. 11 family dùng trong R3-A2/R4-A được đánh dấu `consumed-not-independent`. Sáu historical locked-test families vẫn `SEALED_UNTOUCHED`; R4 không truy cập chúng.

R4-B targeted capture và causal multiview capture không chạy vì R4-A không PASS. Không có capture failure bị che giấu và không có family mới được gán role sau khi nhìn label.

## R4-C — Controlled recognition training

Không chạy. R4-A CNN là diagnostic representation model, không phải candidate checkpoint. Vì R4-B không tạo train/calibration/confirmation evidence độc lập, không có threshold calibration hợp lệ, quality-ranking model, confirmation gate hoặc deployable artifact.

## R4-D — Locked validation và Unity pilot

Không chạy. Locked test không mở. Không có paired Unity episode, không có bằng chứng Explore → Recognize → Return → Fresh Observe → Explicit STOP tốt hơn baseline. Phase 1 historical 36/40 được giữ nguyên và không chạy lại.

## Tests

- Targeted Python regressions cũ: 30/30 PASS.
- R4-A decision/input/fold tests: 4/4 PASS.
- Unity EditMode: 34/34 PASS, 0 skipped/inconclusive.
- Một lần chạy gộp Python bị abort do NumPy/Torch nạp trùng OpenMP runtime. Không dùng workaround không an toàn; tách thành hai process và cả hai suite PASS.

Chi tiết: `test_execution_matrix.json`, `unity_editmode_results.xml` và `unity_editmode.log`.

## Điều thực sự cải thiện

So với trước, đã chứng minh CNN nhỏ có thể học stable poses và chịu được repeat-render noise của pose đã thấy. Tuy nhiên không có improvement cross-family; CNN thấp hơn frozen spatial-v2 15,8 điểm balanced accuracy và thấp hơn structural-v4 10 điểm.

Do đó chưa có cải thiện recognition an toàn, chưa đủ điều kiện Unity closed-loop, và chưa có cải thiện end-to-end nào được chứng minh.

## Bước nghiên cứu nhỏ nhất tiếp theo

Trước khi xin capture R4-B, nên duyệt một R4-A2 hoàn toàn offline trên cùng consumed evidence:

1. Giữ CNN và split hiện tại làm baseline.
2. Thử đúng một giả thuyết representation: pairwise/contrastive hard-pair objective kết hợp classifier, vẫn dùng ba seed và cùng family folds.
3. Kiểm tra saliency/occlusion ở 0090 và 0066 để xác định model đang bám speckle/background hay contour có tính ổn định.
4. Không đổi target, floor hoặc threshold; không mở locked test.

Nếu A2 vẫn không vượt frozen/structural baselines qua family, cần researcher quyết định giữa thu thập coverage đa dạng hơn và xem lại scientific target. Không nên bắt đầu chiến dịch capture lớn chỉ dựa trên memorization.

## Evidence chính

- `g2_r4_a_representation_report.json`
- `g2_r4_family_usage_ledger.json`
- `test_execution_matrix.json`
- `Tools/ThoracicUSCoach/experiments/g2_r4_a_representation_v1.json`

Hashes để tái lập/đối chiếu:

- R3-A2 source evidence: `0272f5ceda47ef1df04effc612db6084897e81260a97078fc4dae7370628f5b3`.
- Frozen spatial-v2 checkpoint: `e0ec390f016078904807954769c3e32f5e1481fb3307e86b9e3a7e488c576792`.
- R4-A result JSON: `4573CF082D9E3E0EFDAF26585031F40FC80B8C14779D7ACFFADBE6614AF98A38`.
- Family-usage ledger: `9524b2eaa23349ece214703c68410585d5e84886381f90b260ae7b590f0b4829`.
