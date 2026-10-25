# G2-R3-A — Ground-truth observability audit

Ngày chạy: 2026-10-25  
Kết luận: **PARTIALLY_OBSERVABLE**  
Quyết định khoa học: **NO-GO cho R3-B/R3-C/R3-D phụ thuộc**

## Tóm tắt

Evaluator hiện tại không phải là nhãn thuần ảnh. Tổng điểm được tái tạo chính xác từ bảy thành phần trên 6.202 frame thuộc 360 family train/dev, không có sai khác giữa nhãn và ngưỡng đóng băng `0.5912307500839233`. Tuy nhiên, phần đóng góp tuyệt đối trung vị từ anatomy/target/needle/contact privileged là `0.4477`, trong khi phần thuần pixel là `0.0570` và phần mixed pixel/anatomy là `0.0106`.

Các cặp positive/negative gần nhất nhìn rất giống nhau ở ảnh sạch. Ví dụ gần nhất cross-family có RMS `0.00166` ở 32×32 và `0.00445` ở 128×128; chênh lệch điểm chủ yếu đến từ anatomy relationship, target geometry và needle geometry, không phải acoustic-window pixel term. Đây không phải chứng minh toán học rằng ảnh không chứa thông tin, nhưng là bằng chứng mạnh rằng nhãn hiện tại chỉ quan sát được một phần từ actor image-only.

## Score decomposition

Evaluator đóng băng:

`0.25*anatomy + 0.23*pleura + 0.20*target + 0.17*acoustic + 0.15*needle - 0.18*shadow - 0.50*contact`

| Thành phần | Nguồn thông tin | Đóng góp tuyệt đối trung bình |
| --- | --- | ---: |
| Anatomy relationship | Unity anatomy raycast, privileged | 0.20248 |
| Pleura visibility | Pixel + anatomy raycast | 0.00995 |
| Target region | Target/scan-plane geometry, privileged | 0.14560 |
| Acoustic window | Clean ultrasound pixels | 0.05611 |
| Needle geometry | Needle/scan-plane geometry, privileged | 0.07793 |
| Shadow penalty | Clean ultrasound pixels | 0.00000 trong dataset này |
| Contact penalty | Contact state, privileged | 0.00000 trong dataset hợp lệ này |

Sai số tái tạo công thức lớn nhất là `3.92e-8`; không có label/score mismatch.

## Matched positive/negative và family 0090

Hai montage được sinh theo hai bước: bản blinded chỉ ghi Pair A/B; bản annotated mới thêm family, nhãn và điểm. Không gán tên rib, transverse process hay pleura chỉ dựa trên contour sáng.

- `train_family_0090` có đúng một frame positive: index 100, score `0.592726`, margin `+0.001496`.
- Opposite-label gần nhất cùng family là index 101, score `0.541635`; RMS lần lượt là `0.00234`, `0.00455`, `0.00826`, `0.01156` và `0.01365` tại 16/32/64/128/1024.
- Schema Active2D của family này chỉ lưu `finalTargetScore`, không lưu từng component. Vì Unity chưa có exclusive access, báo cáo không bịa attribution anatomy/geometry cho case 0090.

Evidence ảnh:

- `g2_r3_matched_pairs_blinded.png`
- `g2_r3_matched_pairs_annotated.png`

## Repeat-render và causal history

Evidence lịch sử cùng commanded pose (`score` so với fresh `observedScore`) có median |Δscore| `0.000110`, max `0.003716`, và 13 label flip trong 6.202 frame. G2 temporal evidence độc lập trước đó có 112 cặp same-pose, median image RMS128 `0.00224`, max `0.00732`, một label flip.

Dataset component-rich có 3.618 transition, 3.258 transition ghi nhận action thực thi; action feedback dùng `executedAction/actionWasExecuted`, không dùng teacher action. Đây chỉ là diagnostic causal ngắn, không phải benchmark recognition độc lập.

G0 lịch sử cho AP `0.8982` single-frame và `0.9531` với past-image delta. Dù vậy, G2-R2 không tạo được candidate temporal ổn định qua seed/family và calibration gate vẫn fail. Multi-view có ích nhưng chưa phục hồi đáng tin cậy phần nhãn phụ thuộc privileged geometry.

## Gate matrix

| Gate | Kết quả |
| --- | --- |
| A1 score decomposition | PASS |
| A2 matched pairs | PASS, thiếu component cho family 0090 |
| A3 fresh exact-pose repeat | BLOCKED — không có exclusive Unity access |
| A4 causal observability | PARTIALLY_OBSERVABLE |
| A5 Unity EditMode/safety regression | NOT RUN, không claim PASS |
| A6 scientific decision | NO-GO dependent retraining |
| R3-B/R3-C/R3-D | BLOCKED bởi A6 |

Unity Editor khác đang mở nên không khởi chạy project đồng thời và không terminate session của người khác. Vì vậy chưa có số position/rotation restoration error mới và chưa revalidate Phase 1/contact/workspace/terminal rules trong R3.

## Quyết định tiếp theo cần người dùng review

Một trong ba hướng phải được phê duyệt trước khi train tiếp:

1. Giữ evaluator và cho phép actor dùng simulator-privileged geometry — thay đổi contract image-only.
2. Định nghĩa lại target theo tín hiệu thực sự quan sát được từ ultrasound — thay đổi scientific target/evaluator.
3. Giữ image-only và coi multi-view uncertainty/abstention là outcome chính thức — không bảo đảm đạt recall 80% với nhãn hiện tại.

Trước quyết định đó, khi có exclusive Unity access chỉ nên chạy diagnostic nhỏ restore đúng pose cho family 0090 và near-boundary controls; không mở chiến dịch capture/training.

## Lệnh kiểm chứng

```powershell
& 'C:\Users\Vinh\anaconda3\python.exe' Tools/ThoracicUSCoach/run_g2_r3_observability.py
& 'C:\Users\Vinh\anaconda3\python.exe' -m unittest Tools/ThoracicUSCoach/test_g2_r3_observability.py
```

JSON đầy đủ: `g2_r3_a_observability_report.json`. Locked test không được mở; evaluator, threshold, Phase 1 và checkpoint lịch sử không bị thay đổi.
