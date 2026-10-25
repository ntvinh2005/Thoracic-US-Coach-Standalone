# G2-R3-A2 — Confirmatory Phase 2 observability audit

Ngày chạy: 2026-10-25  
Verdict theo rule predeclare: **INCONCLUSIVE**  
Không train, không retune threshold, không mở locked test, không deploy, không đổi evaluator hoặc Phase 1.

## Executive summary

Exact-pose diagnostic đã chạy thành công trên Unity 6000.3.9f1 với GPU renderer thật. Cả 16/16 pose Phase 2, gồm `train_family_0090` frame 100/101, được restore hợp lệ và tạo đủ 96/96 fresh observations. Sai số position tối đa `3.81e-6 mm`, rotation `0°`, contact-position `3.81e-6 mm`; mọi fresh observation đều contact/workspace-valid.

Kết quả mới không xác nhận kết luận mạnh `PARTIALLY_OBSERVABLE`, nhưng cũng chưa đủ để GO cho training image-only:

- 9/10 fresh opposite-label pairs vượt rule phân biệt thị giác đã khai báo trước: RMS128 giữa hai pose lớn hơn `3 ×` q95 renderer noise. Một trong 11 pair predeclared không còn opposite-label vì negative sát ngưỡng đã flip thành positive ở fresh render và được loại khỏi phép đếm này.
- Frozen spatial-v2 chỉ nhận đúng 2/6 positive fresh poses, recall `33.3%`, 0 false positive. Điều này cho thấy model/representation hiện tại yếu dù phần lớn cặp có tín hiệu ảnh đo được.
- Một near-boundary pose tự flip label trong sáu render không di chuyển probe. Đây là bất ổn ground-truth thực tế ở sát threshold.
- Chỉ một cặp còn ambiguous theo rule; nó chưa đạt điều kiện tối thiểu hai cặp để kết luận `PARTIALLY_OBSERVABLE_WITH_EVIDENCE`.

Vì không rule mạnh nào được thỏa mãn hoàn toàn, verdict bắt buộc là `INCONCLUSIVE`. Không được diễn giải thành Phase 2 PASS.

## 1. Exact-pose reproduction

Manifest được khóa trước khi chạy tại `Tools/ThoracicUSCoach/experiments/g2_r3_a2_exact_pose_v1.json`, SHA-256 `b989938b428a6647f48b10891703438d161042a734fa4a27dc1c1460bc2ab031`.

Selection gồm:

- Mandatory: family 0090 frame 100 positive và 101 matched negative.
- Nearby causal frames 99 và 102.
- Ba independent near-boundary positives và ba negatives.
- Một clear positive và một clear negative.
- Bốn cross-family hard negatives.

Tolerance predeclare dùng contract U08: position `≤0.05 mm`, rotation `≤0.05°`; contact-position `≤0.10 mm`, signed-depth `≤0.10 mm`. Tất cả 16 case đạt. Scene/evaluator hashes và từng source/fresh image hash nằm trong JSON evidence.

## 2. Score components

Score reconstruction trên 96 observations có absolute error lớn nhất `3.61e-8`; không có label/floor mismatch.

Với mandatory pair 0090/100 positive so với 0090/101 negative:

| Thành phần weighted difference | Giá trị |
| --- | ---: |
| Anatomy relationship | 0.000000 |
| Pleura visibility | 0.000661 |
| Target region | 0.036013 |
| Acoustic window | 0.000011 |
| Needle geometry | 0.014300 |
| Shadow/contact penalty | 0.000000 |
| Total difference | 0.050985 |

Khoảng `98.7%` tổng absolute component difference của cặp này đến từ target-region và needle geometry privileged. Tuy nhiên ảnh fresh của hai pose có RMS128 `0.01156`, bằng `3.02 ×` repeat-noise q95, nên cặp này vừa đủ được xếp “visually separated” theo rule predeclare. Vì vậy privileged provenance giải thích score nhưng không chứng minh ảnh không mang tín hiệu tương quan.

## 3. Repeat-render stability

Mỗi pose được render sáu lần không di chuyển probe:

- 240 same-pose image pairs.
- RMS128 q95: `0.003822`; native-resolution q95: `0.004980`.
- Chênh lệch score tối đa giữa repeats: `0.001158`.
- 15/16 pose không flip label trong repeats.
- `train_family_0045/frame100` flip: hai render positive và bốn render negative, score trải từ `0.590870` đến `0.591474` quanh floor `0.591231`.
- Một original→fresh first-render label change, cũng chính case này.

Do đó label nhìn chung ổn định ngoài vùng rất sát threshold, nhưng near-boundary semantic hiện không deterministic đủ để dùng như ground truth tuyệt đối mà không có policy về margin/repeat confirmation.

## 4. Image-only và causal multi-view

Matched-pair rule dùng renderer noise đo thật, không dùng pixel RMS như bằng chứng toán học:

- 9/10 fresh opposite-label pair visually separated; 1/11 predeclared pair bị invalidated bởi fresh label change.
- 1/10 fresh opposite-label pair ambiguous: 0090/100 positive so với 0066/100 negative, RMS128 `2.69 ×` repeat q95.
- Trong cặp ambiguous, privileged terms giải thích `90.7%` absolute component difference, nhưng absolute privileged difference chỉ `0.00566`, thấp hơn tiêu chí `0.02` đã khai báo.

Frozen spatial-v2 tại threshold 0.93:

- Source selected cases: recall `40%`, 0 false positive.
- Fresh restored cases: recall `33.3%` (2/6), 0 false positive.
- Family 0090/frame100 fresh probability chỉ `0.434`, nên miss rõ ràng không thể giải thích bằng pose restoration.

Genuine recorded causal sequence 0090 frames 99→100→101→102 có action accepted/completed thật. Cả ba transition đều có fresh-restored image delta lớn hơn `3 ×` repeat q95. Historical G0 past-image delta tăng AP từ `0.8982` lên `0.9531`, nhưng G2-R2 temporal models không robust qua seed/family. Evidence cho thấy history có tín hiệu, chưa chứng minh một recognizer tổng quát hóa được.

## 5. Scientific decision

Decision procedure đã nằm trong manifest trước fresh outcomes. Inputs thực tế:

- Pose/integrity prerequisites: PASS.
- Visual separated fraction trên fresh opposite-label pairs: `90.0%`.
- Frozen fresh recall: `33.3%`, dưới GO requirement 80%.
- Ambiguous pairs: 1; partial-evidence pairs: 0, dưới requirement 2.
- Historical causal AP gain: `+0.0549`, nhưng single-frame ambiguity không đủ rộng để kích hoạt rule NEEDS_MULTIVIEW.

Do đó verdict là **INCONCLUSIVE**. Dữ liệu nghiêng về “tín hiệu thường có trong ảnh nhưng model hiện tại không khai thác/generalize tốt”, kèm một vấn đề riêng là instability sát threshold. Nó chưa biện minh cho target image-only hiện tại như một learning target đã được validate.

## 6. Tests và failures được giữ lại

- Python regression: 30 passed, 0 failed sau khi chạy từ đúng module directory.
- Unity EditMode: 34 passed, 0 failed/skipped/inconclusive.
- Exact-pose GPU run: 16/16 pose, 96/96 observations.
- Lần đầu compile fail do iterator diagnostic; lần `-nographics` crash vì NullGfxDevice/URP; GPU attempt tiếp theo gặp stale lock từ crash. Không evidence nào từ các run lỗi được dùng. Sau khi xác nhận không còn Editor process, chỉ stale `Temp/UnityLockfile` được xóa và GPU run cuối hoàn tất.
- Full Phase 1 Gate 2 không chạy lại; historical 36/40 được giữ nguyên, không tạo claim mới.

Chi tiết ở `test_execution_matrix.json` và `unity_editmode_results.xml`.

## 7. Đề xuất tiếp theo cần review

Không chạy R3-B/C/D lúc này. Bounded experiment hợp lý nhất là **A2.5 causal-sequence confirmation**:

- 4–6 hard families đã biết, gồm 0090 và 0066.
- Thực thi thật các short bidirectional yaw/lateral sequences bằng safe actuator.
- Repeat endpoint renders để tách renderer noise, hysteresis và action response.
- Tối đa khoảng 48–72 observations; không train, không đổi target/threshold.

Song song, researcher cần quyết định có định nghĩa một near-boundary margin/repeat-confirmation policy hay không. Việc đó thay đổi semantics acceptance nên không được tự động áp dụng.

## Evidence

- `g2_r3_a2_analysis.json`: analysis và verdict inputs.
- `unity_capture/g2_r3_a2_unity_report.json`: per-pose restoration, full components và hashes.
- `unity_capture/*/repeat_*_us.png`: 96 fresh images.
- `g2_r3_a2_pairs_blinded.png` và `g2_r3_a2_pairs_annotated.png`.
- `test_execution_matrix.json`, `unity_editmode_results.xml` và logs.
