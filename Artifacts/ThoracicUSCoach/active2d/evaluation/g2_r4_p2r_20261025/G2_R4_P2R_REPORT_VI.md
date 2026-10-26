# G2-R4 P2-R — Báo cáo sửa leakage, causal history và failure analysis

## Kết luận điều hành

P2-R đã hoàn tất đúng một corrected development rerun. Kết quả cuối:

- `ENGINEERING_INTEGRITY`: **PASS**.
- `DATA_INDEPENDENCE`: **INCONCLUSIVE** — primary policy dùng được, conservative sensitivity bị underpowered.
- `DEVELOPMENT_RECOGNITION`: **FAIL**.
- `READY_FOR_NEW_DATA`: **PASS**.
- `ELIGIBLE_FOR_CONFIRMATION`: **BLOCKED**.

Auxiliary leakage risk và causal-padding bug đều được xác nhận, sửa và có regression tests. Sau sửa, kết luận thất bại vẫn giữ nguyên: không có zero-FP STOP operating point nhận ra positive. Không mở confirmation, historical locked test, Unity closed-loop hay deployment.

## 1. Frozen evidence và identity

- Source commit: `62c847efc63fb542be820cf5dce36281bd4f3959`.
- P2-R protocol SHA-256: `f3aece8b2dd901054db84c44b07f7ed486eccca827a8c6d213d9a96c3146e696`.
- Historical P2 protocol SHA-256: `3c60441f881d677beb6c3de89e58b215b061635079485ecb341a19848ee97a66`.
- Historical P2 result SHA-256: `bd9bc1f9fab113ef0186516e7f6b32fbccd455ab427d0544acbb77bb07747852`.
- Frozen `active2d_stop.py`: `d74f03b0c72d7fe254caf6e74464a156fa56fbd7d4878507b8d7d07bd40dfdc4`.
- Frozen live server: `5e726089ece1a3dac756a8dd6540c116851635b25ac49640b9739636f4f6b9e3`.
- Target floor giữ nguyên `0.5912307500839233`; frozen spatial-v2 giữ threshold `0.93`.

Historical P2 JSON/report/checkpoint không bị ghi đè. Historical P2 không lưu per-frame OOF predictions, vì vậy không dùng final-refit checkpoint để giả làm historical OOF evidence.

## 2. R1 — Complete lineage và leakage

Graph mới dùng toàn bộ 16 primary và 4 auxiliary trajectory thành công. Edge gồm shared source ancestry, strong bidirectional pose overlap, exact-pose overlap và near-start warning; connected components được tính bắc cầu qua cả auxiliary.

### Risk hard_0090 ↔ hard_0066

Risk được **xác nhận**:

- `r4p1_hard_00` (`train_family_0090`) và `r4p1_hard_01` (`train_family_0066`) overlap trajectory `1.0/1.0`.
- `r4p1_hard_01` lại shared source với primary `r4p1_enriched_00/01` của 0066.
- Cả bốn trajectory nằm trong `primary_spatial_00`.
- Khi component này là outer evaluation, `hard_00` và `hard_01` đều bị loại khỏi training.

Leakage cũ là leakage về spatial dependence, không phải bằng chứng byte-identical image leakage.

### Grouping sensitivity

| Policy | Evaluation groups | Positive | Negative | Ba fold khả thi? |
|---|---:|---:|---:|---|
| Primary: shared source + strong captured-trajectory overlap | 11 | 5 | 6 | Có |
| Conservative: thêm near-start dependency | 1 | 1 | 0 | Không |

Primary policy giữ 11 nhóm vì auxiliary 0090 được nối vào 0066 nhưng không thêm primary evaluation family. `0052` và `0058` vẫn chung một component do trajectory overlap. Mỗi outer fold có positive/negative ở evaluation và fit, inner validation độc lập, ít nhất hai positive fit groups, không có shared ancestor/strong overlap/exact pose băng qua split.

Conservative policy nối gần như toàn bộ primary evidence thành một component; auxiliary-only 0077 là component còn lại nhưng không được tính evaluation support. Kết quả này không chứng minh các ảnh siêu âm giống hệt nhau, nhưng cho thấy claim group-generalization rất nhạy với định nghĩa độc lập.

## 3. R2 — Causal multiview contract

Mask đúng cho bốn observation đầu:

| Observation | Availability mask |
|---:|---|
| 1 | `[0,0,0,1]` |
| 2 | `[0,0,1,1]` |
| 3 | `[0,1,1,1]` |
| ≥4 | `[1,1,1,1]` |

Padding vẫn có thể lặp ảnh sớm nhất để giữ tensor shape, nhưng encoder output của slot padding bị nhân 0 trước fusion. Padded issued/executed action, accepted, completed và reset metadata đều bằng 0. Real slots mang riêng issued action, executed action, accepted, completed, reset và availability. Rejected hoặc accepted-but-incomplete action có executed action `RESET`.

Episode reset xóa history; test xác nhận không future image, không cross-episode và offline/casual-replay metadata parity. Privileged pose, score, target và anatomy không đi vào model.

Original P2 H4 metrics không còn hợp lệ như bằng chứng của corrected causal history. Historical report vẫn được giữ nguyên như record của lần chạy cũ.

## 4. R3 — Per-frame ledger và lỗi recognition

Corrected ledger có 10.640 rows: ba learned model × ba seeds trên OOF frames, cộng selected ensemble. Mỗi row lưu lineage, image hash/path, Unity label/score/margin, stop probability, quality prediction, fold, history mask, action feedback và instability flag. Privileged columns chỉ dùng diagnostic/evaluation.

Ledger SHA-256: `02a6cdef68cea7dfa5ca3840d220bcb998d9645084c5f3c983d47ee1f2cc2ed4`.

### Highest-risk negative

Negative cao nhất là `r4p1_random_06`, frame 23, source `train_family_0055`:

- Unity score `0.46884`, margin `-0.12239` — không phải near-boundary.
- STOP probability `0.63784`.

Nhiều negative tiếp theo cũng nằm trong `random_06`, margin khoảng `-0.054` tới `-0.093`. Một số negative của enriched 02/03 gần boundary, nhưng chúng không giải thích negative cực đại.

### Missed positive

Positive thấp nhất tập trung ở `r4p1_enriched_02/03`, component 0080:

- Unity score khoảng `0.607–0.612`.
- Margin khoảng `+0.0156–+0.0203`.
- STOP probability chỉ `0.070–0.083`.

Có **57 negative frames xếp trên mọi positive frame**. Max positive probability là `0.31295`, trong khi max negative là `0.63784`.

### Sensitivity chỉ để chẩn đoán

| False-positive budget | Best recall |
|---:|---:|
| 0 | 0,0% |
| 50 | 0,0% |
| 57 | 3,4% |
| 75 | 37,3% |
| 100 | 37,3% |
| 200 | 74,6% |

Đây không phải threshold tuning. Nó cho thấy muốn gần recall gate 70% phải chấp nhận khoảng 200 false-positive frame trên 1.005 negatives, hoàn toàn không phù hợp STOP safety.

Contact sheets: `r3_high_risk_negatives.png` và `r3_missed_positives.png`.

## 5. R4 — Corrected fixed-seed rerun

Job CSSALT Local Jobs `4701e65d44ef` hoàn tất exit code 0. Chỉ chạy đúng một rerun, seeds 13/29/47, cùng model/budget/loss; không architecture search.

| Model | Frame AP | ROC-AUC | Median seed group AP | Recall @0,5 | False positives @0,5 | Spearman | Median regret |
|---|---:|---:|---:|---:|---:|---:|---:|
| Frozen spatial-v2 @0,93 | 0,060 | 0,547 | 0,114 | 0,0% | 14 | -0,079 | 0,1042 |
| Structural-v4 equivalent | 0,255 | 0,865 | 0,476 | 96,6% | 353 | 0,772 | 0,0365 |
| CNN single-frame | 0,156 | 0,821 | 0,400 | 0,0% | 4 | 0,805 | 0,0473 |
| CNN corrected H4 | 0,174 | 0,841 | 0,368 | 11,9% | 48 | 0,809 | 0,0652 |

H4 corrected không đạt margin +0,02 so với single-frame; nó thấp hơn `-0,0312` theo median group AP. Candidate rule vì vậy chọn single-frame. Đây không có nghĩa single-frame dùng được; nó chỉ là lựa chọn ít tệ hơn theo rule đã khóa.

Every-seed frame AP của single-frame là `0.1665`, `0.1656`, `0.1244`, đều fail yêu cầu ≥0,50. Fold AP dao động mạnh, từ khoảng `0.079` tới `0.631`, cho thấy domain/group shift lớn.

### Calibrated STOP

Threshold zero-observed-FP là `0.6378359198570253`, ngay trên max negative. Ở threshold này:

- TP/FP/FN/TN: `0/0/59/1005`.
- Recall: `0%`.
- Positive groups recognized: `0/5`.
- Abstention: `100%`.
- Spearman: `0.8049`.
- Median all-trajectory regret: `0.0473`.
- Median regret chỉ trên trajectory có positive: `0.0822`.

Zero-FP reject positive vì toàn bộ positive score nằm dưới negative tail; không phải chỉ vì calibration chọn hơi cao.

Corrected checkpoint SHA-256 `ffbf411156ea9b0a8c0e36d66922eabc3020a82e07a07b532487fe8638f5631b`, architecture `cnn_single_frame`, history contract `single-frame-v1`, status `NON_DEPLOYABLE`.

## 6. Original versus corrected

| Evidence | Original P2 | Corrected P2-R |
|---|---:|---:|
| Selected architecture | Causal H4 lỗi mask | Single-frame |
| Selected frame AP | 0,245 | 0,156 |
| Selected ROC-AUC | 0,863 | 0,821 |
| Median seed group AP | 0,305 | 0,400 |
| Calibrated recall | 0% | 0% |
| Calibrated false positives | 0 | 0 |
| Regret | 0,0495 | 0,0473 |

Không được đọc bảng này như paired ablation thuần túy: corrected run đồng thời sửa auxiliary grouping, fold membership, seed execution và causal representation. Kết luận hợp lệ là failure robust sau correction; không thể định lượng riêng đóng góp của từng sửa chữa mà không chạy thêm campaign, và task này không cho phép.

## 7. Root-cause assessment

- **Supported:** auxiliary leakage tồn tại; causal padding sai; independent-family diversity yếu; current representation không tách safe STOP tail; global Spearman che giấu regret cao trên positive trajectories.
- **Contradicted:** near-boundary instability là nguyên nhân chính; corrected H4 giải quyết STOP; image-only hoàn toàn không observable.
- **Plausible nhưng chưa xác minh:** model thiếu landmark/motif giải phẫu cụ thể; renderer variation đóng góp cục bộ.

Evidence hỗ trợ một tổ hợp **data diversity/domain shift + representation weakness**. Target observability không bị phủ định tuyệt đối vì AUC/AP vẫn trên random và một số group có within-group ranking tốt, nhưng safe cross-group STOP observability chưa được chứng minh.

## 8. Tests và giới hạn

- Python unittest: 52/52 PASS.
- Pytest: 52/52 PASS.
- Unity EditMode P2-R: không chạy vì không có C#/scene/evaluator/actuator thay đổi. Không tái-label Unity PASS cũ thành P2-R evidence.
- Training/evaluation compute: 1.029,9 giây; peak RSS 1.244.151.808 bytes; PyTorch 2.12.0 CPU, 4 threads.
- Không capture ảnh mới, không mở confirmation/locked test, không closed-loop, không Phase 1 rerun.

## 9. Bước tiếp theo tối thiểu

Không train lại model hiện tại. Đề xuất nhỏ nhất là một **development capture cohort mới**, chưa phải confirmation:

1. Tạo marker/skin-derived starts tách khỏi 11 primary components hiện tại theo primary overlap rule và báo cả conservative proximity.
2. Ưu tiên hai stratum predeclared: hard negatives giống `random_06` nhưng evaluator-negative xa boundary; positives giống 0080/enriched02-03 mà model đang cho xác suất thấp.
3. Mỗi source-base mới giữ toàn bộ trajectory/repeats trong một group; cần tối thiểu sáu positive và sáu negative spatial groups mới.
4. Khóa seeds, starts, movement rule và split trước capture; không dùng confirmation seeds hiện tại.
5. Chỉ sau khi data support đạt, chạy lại đúng single/H4 ablation; chưa đổi kiến trúc.

Mục tiêu là kiểm tra liệu coverage mới có sửa cross-group tail separation hay không. Không có cơ sở để mở confirmation ở trạng thái hiện tại.
