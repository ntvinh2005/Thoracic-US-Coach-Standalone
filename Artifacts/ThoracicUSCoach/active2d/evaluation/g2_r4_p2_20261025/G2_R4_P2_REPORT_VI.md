# G2-R4 P2 — Báo cáo lineage-aware recognition

## Kết luận

**DEVELOPMENT FAIL — CONFIRMATION BLOCKED — NON_DEPLOYABLE.**

Job CSSALT Local Jobs `1982c4d12e1d` hoàn tất với exit code 0. Đây là hoàn tất tính toán, không phải model PASS. Candidate causal multiview không đạt development gate, nên cohort confirmation không được capture/mở, historical locked test không được truy cập, Unity closed-loop không được chạy và không có model nào được deploy.

## 1. Lineage và độc lập dữ liệu

P2-A xác minh đủ 1.120/1.120 ảnh P1 ở kích thước gốc 1024×1024; tất cả hash duy nhất, nhãn khớp score floor `0.5912307500839233`, không có lỗi contact/workspace/action alignment.

| Mức đếm | Hỗ trợ thực tế | Ý nghĩa |
|---|---:|---|
| Frame của 16 trajectory chính | 1.064 | Không phải 1.064 mẫu độc lập |
| Trajectory/pose family | 16 | Một số dùng cùng source base |
| Source-base | 12 | 6 nhóm dương, 6 nhóm âm |
| Source-base/spatial | 11 | 5 nhóm dương, 6 nhóm âm |

Hai source-base `train_family_0052` và `dev_family_0058` bị gộp vì trajectory có phủ pose hai chiều vượt tiêu chí đã khóa. Vì vậy hỗ trợ dương độc lập đúng là 5 spatial groups, không phải 10 trajectory dương hay 59 frame dương. Nhiều starting pose khác cũng ở gần nhau; chúng được báo cáo nhưng chỉ gộp khi capture trajectory thật sự overlap. Không có claim generalization sang bệnh nhân/anatomy khác vì anatomy Unity không đổi.

Ba development fold dùng spatial-group-disjoint split. Một spatial component chỉ xuất hiện ở một phía của mỗi fold. Bốn hard-causal trajectory chỉ là auxiliary training evidence; chúng không được tính là independent evaluation support và bị loại khỏi training khi source group tương ứng nằm ở evaluation fold.

## 2. Protocol đã khóa trước training

- Seeds: `13, 29, 47`.
- Input: ảnh sạch grayscale 128×128.
- CNN: kiến trúc nhỏ từ R4-A; multiview dùng 4 quan sát causal, policy memory và action thực sự executed.
- Optimizer: AdamW, learning rate `0.001`, weight decay `0.0001`, batch 64.
- Budget: tối đa 120 epoch; early stop patience 16 sau epoch 24; restore best epoch.
- Không augmentation, không architecture search, không tăng seed/trial sau kết quả xấu.
- Weight theo spatial group và class trong group, không theo số frame thô.
- Calibration chỉ dùng pooled out-of-fold development predictions.
- Confirmation protocol khóa 8 random + 8 target-enriched seed, tối đa 81 ảnh/group, nhưng chỉ được chạy sau development PASS.

Hash protocol: `3c60441f881d677beb6c3de89e58b215b061635079485ecb341a19848ee97a66`.

## 3. So sánh development cùng dữ liệu

Các giá trị CNN/structural dưới đây là ensemble out-of-fold tại ngưỡng chẩn đoán 0,5, chưa phải ngưỡng STOP đã calibration. AP đo khả năng xếp hạng frame dương phía trên frame âm; group-macro AP cho mỗi spatial-positive group trọng số như nhau. Regret là score tốt nhất thật sự trong trajectory trừ score thật của frame model chọn.

| Model | Frame AP | Median seed group-macro AP | Recall @0,5 | False STOP @0,5 | Quality Spearman | Median regret |
|---|---:|---:|---:|---:|---:|---:|
| Frozen spatial-v2, threshold gốc 0,93 | 0,060 | 0,114 | 0,0% | 14 | -0,079 | 0,1042 |
| Structural-v4 equivalent | 0,161 | 0,375 | 55,9% | 320 | 0,807 | 0,0625 |
| CNN single-frame | 0,150 | 0,276 | 0,0% | 6 | 0,782 | 0,0545 |
| CNN causal multiview H4 | 0,245 | 0,305 | 0,0% | 9 | 0,788 | 0,0495 |

CNN cải thiện ranking rõ so với frozen spatial-v2 trên cùng P1 population, nhưng không tạo operating point STOP an toàn. Structural-v4 nhận ra nhiều positive hơn ở 0,5 nhưng false STOP quá lớn và seed variation cao. Không model nào đạt mức có thể promotion.

### Seed robustness

| Model | Seed | Frame AP | Group AP | Recall @0,5 | False STOP | Spearman | Regret |
|---|---:|---:|---:|---:|---:|---:|---:|
| Structural-v4 | 13 | 0,130 | 0,375 | 42,4% | 239 | 0,817 | 0,0567 |
| Structural-v4 | 29 | 0,144 | 0,312 | 49,2% | 323 | 0,782 | 0,0709 |
| Structural-v4 | 47 | 0,225 | 0,416 | 94,9% | 498 | 0,678 | 0,0925 |
| CNN single | 13 | 0,160 | 0,246 | 0,0% | 9 | 0,732 | 0,0639 |
| CNN single | 29 | 0,146 | 0,313 | 0,0% | 13 | 0,743 | 0,0739 |
| CNN single | 47 | 0,170 | 0,276 | 0,0% | 3 | 0,737 | 0,0746 |
| CNN H4 | 13 | 0,213 | 0,316 | 0,0% | 8 | 0,743 | 0,0419 |
| CNN H4 | 29 | 0,248 | 0,305 | 0,0% | 6 | 0,778 | 0,0384 |
| CNN H4 | 47 | 0,215 | 0,297 | 33,9% | 49 | 0,757 | 0,0570 |

Không seed CNN nào đạt AP tối thiểu 0,50. Kết quả không ổn định ở operating point: seed 47 nhận ra một phần positive nhưng kéo theo 49 false STOP.

## 4. Candidate selection và calibration

Rule đã khóa chọn multiview chỉ khi median group AP hơn single-frame ít nhất 0,02. H4 hơn `0,0288`, nên đúng một candidate H4 được freeze. Đây chỉ là diagnostic candidate.

Ngưỡng zero-observed-FP từ pooled OOF development là `0.6262465119361879`. Ở ngưỡng này:

- Recall: **0/59 = 0%**.
- False-positive STOP: **0/1005**.
- Positive spatial-group recognition: **0/5**.
- Abstention: **100%**.
- Quality Spearman: **0,7875**.
- Median best-view regret: **0,04948**.

Zero false positive đạt được bằng abstain toàn bộ positive, nên không phải safe operating point. Candidate fail recall ≥70%, fail 4/5 group recognition và fail every-seed AP ≥0,50. Quality correlation vượt 0,60 nhưng regret fail ngưỡng 0,015.

Checkpoint `g2_r4_p2_candidate.pt` có SHA-256 `e1a179cdd4b7d01cdfbe3365aa71e21fcc4f8bfbbf280c21f5f147e5a415b99e`, được đánh dấu `BLOCKED`, `safeOperatingPoint=false` và không tương thích với live loader hiện tại.

## 5. Confirmation và scientific gates

Confirmation **không chạy**. Đây là hành vi bắt buộc, không phải thiếu evidence: development candidate không đủ điều kiện nên việc capture/mở confirmation rồi điều chỉnh sẽ làm hỏng isolation. Do đó recall ≥80%, zero false STOP, Spearman ≥0,60 và regret ≤0,015 trên confirmation đều là `NOT_EVALUATED`, không được thay bằng development metrics.

Không có ảnh confirmation mới, không có Unity episode, không truy cập sáu historical locked families, không threshold tuning sau kết quả và không P3/G4.

## 6. Root cause và bước tiếp theo tối thiểu

Failure chính là **cross-spatial-family STOP separability chưa đủ**: score dương OOF nằm dưới ít nhất một negative, nên mọi threshold zero-FP đều loại toàn bộ positive. Temporal history cải thiện ranking vừa phải nhưng không giải quyết overlap ở tail an toàn. Failure phụ là support chỉ có 5 positive spatial groups và near-boundary label/render instability.

Bước tiếp theo tối thiểu cần researcher phê duyệt riêng: không train lại ngay; trước hết thiết kế một capture-development cohort mới có các start marker/skin-derived thật sự tách khỏi 11 spatial components hiện tại, ưu tiên hard negatives có score model cao và positive có score model thấp, rồi khóa lại protocol trước capture. Confirmation seeds hiện tại phải giữ chưa mở hoặc chính thức hủy trước khi một protocol mới được tạo; không được dùng chúng để tune.

## 7. Evidence

- `p2_a_lineage_graph.json`: ancestry, overlap, support và folds.
- `p2_a_integrity_manifest.json`: raw roots và image-integrity digest.
- `p2_a_development_split.json`: split/calibration contract.
- `p2_cd_development_results.json`: mọi seed/fold, baseline, selection và compute.
- `p2_scientific_gate_matrix.json`: quyết định gate machine-readable.
- `g2_r4_p2_candidate.json/.pt`: diagnostic checkpoint BLOCKED.

Compute: 767,68 giây CPU study wall time, peak RSS 1.144.315.904 bytes, PyTorch 2.12.0 CPU, bốn threads. Source commit bắt đầu: `34a573665ed0af540e451148543ea7df857f8c72`.

## 8. Verification

- Training/evaluation qua CSSALT Local Jobs: job `1982c4d12e1d`, `COMPLETED`, exit code 0.
- Python unittest: `python -m unittest test_g2_r4_baseline_parity.py test_g2_r4_p1_protocol.py test_g2_r4_p1_analysis.py test_g2_r4_p2.py -v` — 22/22 PASS.
- Pytest: `python -m pytest -q test_g2_r4_baseline_parity.py test_g2_r4_p1_protocol.py test_g2_r4_p1_analysis.py test_g2_r4_p2.py` — 22/22 PASS.
- Unity EditMode qua CSSALT Local Jobs: job `4308e3a42f68`, exit code 0 — 34/34 PASS, 0 fail/skip/inconclusive.

Unity chỉ chạy verification test, không capture confirmation và không chạy closed-loop episode.
