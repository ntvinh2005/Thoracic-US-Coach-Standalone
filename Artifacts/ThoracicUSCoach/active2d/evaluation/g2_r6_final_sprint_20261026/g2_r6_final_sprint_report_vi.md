# G2-R6 Phase 2 Final Sprint — Báo cáo Prompt 1/3

Ngày thực hiện: 2026-10-26  
Nhánh tích hợp: `codex/u08-phase2`  
Verdict độc lập: **`STAGE_B_NO_GO`**  
Stage tiếp theo được phép chạy: **không**

## Kết luận ngắn

Stage B đã tạo snapshot toàn vẹn, nhưng chưa tạo được bản đồ basin T7 hợp lệ. Bốn pose dương lịch sử vẫn tái tạo đúng về pose, contact, workspace, T7 và score; tuy nhiên ảnh đầu tiên sau mỗi lần đổi pose vẫn là ảnh chưa settle của pose trước. Mỗi candidate chỉ có một ảnh ngay sau đổi pose, nên 312 association candidate–ảnh không đủ validity. Trong số đó có 9 ảnh vượt score floor, nhưng cả 9 đều off-T7; số candidate vừa T7-safe vừa positive là 0.

Vì positive-control reproducibility theo quy tắc hình ảnh frozen thất bại và không có repeat-stable acceptable T7 basin, Stage C và D không được chạy. Cả `ENGINEERING_DEMO_DATA_READY` và `SCIENTIFIC_TRAINING_DATA_READY` đều **false**.

## 1. Acceptable T7 basin ở đâu?

Chưa xác định được một basin mới có biên và phạm vi vật lý hợp lệ.

Bốn control lịch sử tại các pose đã consumed vẫn cho 12/12 render có score dương, T7/contact/workspace hợp lệ và pose restoration error bằng 0. Chúng tạo ba component không gian theo ngưỡng primary, hoặc hai component theo ngưỡng conservative. Đây chỉ là control đã dùng trước đó, không phải basin mới.

| Control | Probe position `(x, y, z)` mm | Settled score range |
| --- | --- | --- |
| `r6_control_hard_00_f0008` | `(33.1825, 180.3269, -40.6284)` | `0.60418–0.60517` |
| `r6_control_random_01_f0010` | `(34.1589, 180.9334, -36.3038)` | `0.60018–0.60107` |
| `r6_control_random_05_f0016` | `(34.5964, 180.3470, -40.6018)` | `0.60072–0.60080` |
| `r6_control_hard_01_f0009` | `(32.9588, 180.3448, -40.5000)` | `0.59813–0.59873` |

Trong 312 candidate predeclared:

- 180 candidate là T7-safe negative.
- 9 candidate vượt score floor nhưng đều off-T7.
- 0 candidate đồng thời vượt floor, T7-valid, contact-valid và workspace-valid.
- Score T7-safe cao nhất là `0.5859293938`, thấp hơn floor frozen `0.5912307501`.
- 4 representative được render lại, tổng cộng 12 ảnh, nhưng tất cả vẫn negative; score representative cao nhất là `0.584859`.

Vì candidate image–pose association bị stale-frame contamination, các số trên chỉ đủ để bác bỏ snapshot hiện tại, không đủ để suy ra basin thật hẹp đến mức nào.

## 2. Vì sao scout R5 bỏ lỡ target?

R5 chỉ có 11 movement mỗi route và có các movement đối nghịch đưa probe trở lại gần điểm xuất phát, nên coverage vật lý quá hẹp. R6 còn phát hiện thêm lỗi capture: frame đầu ngay sau đổi pose chưa phản ánh pose mới.

Ở cả ba boundary giữa các control, ảnh repeat 0 của control mới gần ảnh cuối của control trước (`RMS128 0.00191–0.00247`, structural distance `0.00143–0.00289`), sau đó repeat 0 → repeat 1 mới nhảy mạnh (`RMS128 0.0213–0.0254`). Repeats 1–2 lại ổn định. Vì vậy, một pipeline chỉ chụp một frame sau movement có thể gán nhầm ảnh cũ cho pose mới. Đây là nguyên nhân kỹ thuật trực tiếp cần sửa trước khi kết luận thêm về reachability của R5.

## 3. Safe target-independent scan có reach target không?

Chưa được chứng minh. Stage C không chạy vì Stage B fail-closed. Không có claim reachability, không có route success và không có ground-truth-guided actor run.

Code action target-independent đã được review: action runtime không đọc target score, anatomy label hoặc target direction. Tuy nhiên code này chưa được phép chạy trên Stage C evidence vì prerequisite verdict là `NO_GO`.

## 4. Có bao nhiêu positive/negative region độc lập?

- Positive region mới, validated: **0**.
- Negative region mới, validated cho scientific readiness: **0**, vì 312 candidate đều dùng single immediate capture và association ảnh–pose chưa fresh.
- Positive control cũ: ba primary spatial components hoặc hai conservative components; chúng được công bố riêng và không được tính là novelty.
- Historical accessible index: 2.048 records/2.048 image hashes/60 trajectories. Strict T7 counts là 24 positive và 739 negative frames, nhưng đây là frame count, không phải số independent region và không được dùng để thổi phồng effective sample size.

Raw của 18 nonlocked R1 episode không còn sẵn; historical coverage vì vậy được ghi rõ là incomplete. Sáu locked test families không được mở.

## 5. Thu được bao nhiêu visual information mới?

Snapshot có 336 ảnh: 12 positive-control renders, 312 candidate renders và 12 representative renders. Transport integrity PASS: 347 indexed files, toàn bộ hash/size/count khớp; 336/336 ảnh tồn tại.

Tuy nhiên, số ảnh mới được chấp nhận cho readiness là **0**, do first-frame lag làm candidate image–pose association không hợp lệ. Không dùng 336 ảnh này để train hoặc báo cáo diversity khoa học.

## 6. Dataset hỗ trợ scientific training, simulator demo hay neither?

**Neither.**

- `ENGINEERING_DEMO_DATA_READY = false`: chưa có basin mới repeat-stable và chưa có safe target-independent reachability.
- `SCIENTIFIC_TRAINING_DATA_READY = false`: điều kiện engineering chưa đạt; không có đủ positive/negative spatial groups hợp lệ, family-disjoint và fresh.

Không train recognition model, không mở locked/confirmation data và không chạy STOP benchmark.

## 7. Exact inputs/artifacts cho Prompt 2

Prompt 2 phải dùng các input sau và coi R6 snapshot hiện tại là diagnostic-only:

- Source capture commit: `06e656377fc7e5ed0822b4f79bfead5e83a705eb`.
- Historical coverage summary SHA-256: `9718182707f9c2a414e10ac84f833fc95bb000985a1c20a7d0ea41e71a9794d3`.
- Historical frame index SHA-256: `da7f23f25bff2032497043f6f60f1f9386467caeb648efa56a633f6cf7520aed`.
- Basin manifest SHA-256: `50c20528c75227b055f41bfc9024af1f9f2b243f77d062a414d040f065598e3f`.
- Immutable snapshot: `D:/CSSALT/Thoracic-US-Coach-R6-Handoff/basin/g2_r6_basin_20261026`.
- Snapshot evidence index SHA-256: `f20995142c878935ae453a7b6a40f092185816e573f606706ab5fe07c9903abb`.
- Snapshot Unity report SHA-256: `7744637a5db612021e2ff7f8fd6e855547d4a500635b2d5be2059ef2ba63c4ae`.
- Independent audit SHA-256: `6bf8dc1393ae37a688d1c06cabb80cad45dd32cf3293dfa77e386ceabebf3b39`.
- Coordinator protocol canonical SHA-256: `d201227ab95e5a858003fa2985d5c118ae724de0b1740972a82c68d1f4eaf7c9`.
- Audit protocol canonical SHA-256: `a01d30e294939eda46f6f2090a11761bd5b37f26d6b516d05b3d3c00c19e07a5`.
- Frozen scene canonical SHA-256: `6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d`.
- Frozen evaluator canonical SHA-256: `7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc`.
- Unity preflight Local Job `c6da5e3a5cbe`: 27/27 pass.
- Stage B Local Job `6287fdf3175e`: completed exit 0; evidence later rejected scientifically.
- Python audit tests: 9/9 pass.

Prompt 2 cần version một protocol mới, không sửa hồi tố R6 evidence. Corrective scope nhỏ nhất:

1. Sau mỗi direct pose placement hoặc completed action, discard ít nhất frame đầu và chờ render settle bằng hai fresh observations liên tiếp; log render frame/time và chứng minh image–pose freshness bằng control transitions.
2. Candidate dùng để lập basin phải có settled capture; vùng được chọn để repeat phải là strict T7-safe acceptable connected component, không phải chỉ là highest-score T7-valid record.
3. Grid mới phải bao gồm chính anchor `(0,0,0)` và các offset đối xứng đủ nhỏ quanh anchor; freeze manifest trước capture và giữ tổng candidate trong budget.
4. Runner phải enforce exact pre-frozen manifest hash trước khi launch, ngoài kiểm tra hậu kỳ của auditor.
5. Chỉ sau khi control reproducibility và basin stability PASS mới chạy bounded target-independent Stage C. Vẫn chưa được train model.

## Integrity và thay đổi dữ liệu cũ

Không xóa ảnh/dataset cũ. Chúng vẫn cần cho provenance, overlap và novelty audit; xóa lúc này sẽ làm coverage kém hơn và phá khả năng tái kiểm tra. Không có locked data nào được đọc hoặc thay đổi.
