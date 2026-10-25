# G2-R2 — Báo cáo nhận diện mục tiêu thị giác Phase 2

Ngày: 2026-10-24

Branch: `codex/u08-phase2`

Base commit: `c11c9aac2761b7b62f1dfc08a3daff6b64cfa647`

Kết luận: **PYTHON ENGINEERING PASS / UNITY REGRESSION NOT RUN / SCIENTIFIC BLOCKED**

## 1. Kết luận ngắn

G2-R2 chưa tạo được bộ nhận diện STOP đủ điều kiện triển khai. Cấu hình tốt nhất theo protocol là `structural-v4`; ở seed 29 và 47 nó nhận đúng 5/7 positive frame (71,4%) trên 6 positive family độc lập, với 0 false positive. Kết quả này bằng recall của frozen spatial-v2 và vẫn thấp hơn gate 80%.

Hai mô hình causal temporal h1/h4 giữ chất lượng xếp hạng rất tốt nhưng nhận diện nhị phân không chuyển giao: recall chỉ 0–1/7 ở các seed đã chạy. Không cấu hình nào pass ổn định cả ba seed. Locked test vì vậy vẫn SEALED; Unity closed-loop pilot không chạy.

## 2. Nguyên nhân G2-R1 thất bại

### Phát hiện đã xác nhận

- Downsample 16×16 làm mất đáng kể cấu trúc: median RMS tới hard negative gần nhất tăng từ `0.003975` ở 16×16 lên `0.017787` ở 128×128.
- Tuy nhiên, tăng cấu trúc ảnh không đủ giải quyết bài toán: structural-v4 vẫn chỉ đạt tối đa 5/7 recall ở zero FP.
- Nhãn evaluator nhất quán với frozen score floor `0.5912307500839233`: 0 mismatch trên toàn bộ train+calibration đã đọc. Có 26 frame nằm trong ±0,005 quanh biên và 48 lần đổi nhãn giữa frame kề nhau, nên bài toán nhị phân rất nhạy gần threshold.
- Cặp opposite-label vẫn gần nhau ở ảnh gốc: với hard negative được chọn ở 128×128, median RMS tại 1024×1024 là `0.020698`.
- Evaluator score không chỉ dùng pixel; nó còn dùng anatomy sampling và target/scan-plane geometry. Các thành phần privileged này chỉ làm label/evaluation và không đi vào actor. Do đó một phần label boundary không hoàn toàn quan sát được từ một frame sạch.
- Temporal history hiện tại giúp quality ranking nhưng không giúp recognition qua family. Đây là bằng chứng chống lại giả thuyết “chỉ cần history dài hơn trên dữ liệu raster cũ”.

### Renderer/label stability

Trong evidence repeat-render hiện có: 112 cặp frame liên tiếp cùng pose, 1 lần đổi label; median image RMS 128 là `0.002239419030956924` và median score delta là `0.0001659393310546875`. Đây là diagnostic cũ, không được coi là heldout mới.

Đánh giá root cause: **representation loss là một yếu tố có thật, nhưng nguyên nhân còn lại là sparse positive coverage và partial observability của score boundary từ ảnh sạch**. Chưa đủ bằng chứng để gọi toàn bộ bài toán `NOT_OBSERVABLE`; kết luận đúng hiện tại là nhận diện hiện có chưa generalize qua pose family.

## 3. Dữ liệu và provenance

- Source: 100 family / 12.100 frame của `active2d_phase2_raster_v2_20261023`; ảnh 1024×1024 được đọc tại chỗ, không copy.
- Train: 26 family, 3146 frame, 63 positive frame trong 13 family.
- Calibration mới: 12 family, 1452 frame, 7 positive frame trong 6 family.
- Sáu positive calibration family chưa được dùng trong G2-R1 development: `train_0055`, `train_0076`, `train_0090`, `dev_0073`, `test_0079`, `test_0094`.
- Sáu historical locked-test family giữ nguyên và không được loader mở.
- Dataset fingerprint: `3156bccc4c15d1de51d33c2d183d11589d6b06814b780d7753342636cacc71f4`.
- Manifest hash: `1e7a36f2e8ec0b6678758d492e816b124b9c8f4bcb064ccad4d7b154b01f80c8`.

Chi tiết từng family và prior usage nằm trong `family_usage_ledger.json`.

## 4. So sánh model trên calibration độc lập

Frozen spatial-v2 @0.93: recall 5/7 = 71,4%, FP=0, Spearman=-0.1454, median regret=0.05709.

| Model | Seed | Recall | FP | Quality Spearman | Median regret | Gate |
|---|---:|---:|---:|---:|---:|---|
| `structural-v4` | 13 | 4/7 = 57,1% | 0 | 0.9794 | 0.00673 | FAIL |
| `temporal-structural-v5-h1` | 13 | 0/7 = 0,0% | 0 | 0.9808 | 0.00673 | FAIL |
| `temporal-structural-v5-h4` | 13 | 0/7 = 0,0% | 0 | 0.9791 | 0.01087 | FAIL |
| `structural-v4` | 29 | 5/7 = 71,4% | 0 | 0.9776 | 0.00673 | FAIL |
| `structural-v4` | 47 | 5/7 = 71,4% | 0 | 0.9766 | 0.00673 | FAIL |
| `temporal-structural-v5-h1` | 29 | 1/7 = 14,3% | 0 | 0.9792 | 0.00976 | FAIL |
| `temporal-structural-v5-h1` | 47 | 1/7 = 14,3% | 0 | 0.9766 | 0.00173 | FAIL |

Positive family bị miss bởi selected seed 13: `dev_family_0073, train_family_0090`. Seed 29/47 tăng lên 5/7 nhưng vẫn dưới 80%; không seed nào được phép deploy.

Quality ranking vẫn mạnh (Spearman khoảng 0,976–0,981; regret dưới 0,011), nhưng quality head không được dùng để tự cấp quyền STOP. Recognition head mới là gate an toàn và đã fail.

## 5. Thay đổi engineering

- Thêm structural-v4: intensity 32×32 + gradient/statistic pooling từ 64×64.
- Thêm causal temporal h1 và h4 (2 hoặc 5 observation tính cả current), dùng ảnh lịch sử, action thực thi và accepted/completed feedback.
- Sửa live memory để feature construction offline/live khớp nhau; rejected/incomplete action được encode là không thực thi.
- Thêm manifest family-disjoint, provenance ledger, visual multiresolution montage và label/observability audit.
- Checkpoint v3 bị đánh dấu `BLOCKED`, threshold `null`; live loader từ chối fail-closed.
- Attempt 1 được giữ lại vì phát hiện rounding của threshold và action alignment; attempt 2 sửa đúng numeric/parity nhưng không đổi gate hay candidate budget.

## 6. Gate và kiểm thử

| Mục | Kết quả |
|---|---|
| Calibration recall ≥80% | FAIL — tốt nhất 5/7 = 71,4% |
| Calibration false STOP | PASS — 0 |
| Wrong-STOP family | PASS — 0 |
| Quality Spearman ≥0,60 | PASS cho candidate |
| Median regret ≤0,015 | PASS cho candidate |
| Robust cả seed 13/29/47 | FAIL |
| Locked test | SEALED / NOT RUN |
| Unity closed-loop | NOT RUN — offline blocked |
| Python unittest | 75/75 PASS |
| Python pytest | 97/97 PASS |
| Unity EditMode | NOT RUN — exclusive-access safety review chặn do Unity processes đang active |

Không thay Phase 1, frozen threshold, evaluator hay frozen evidence. Python Gate-2 compatibility/no-cheating regressions đều pass. Do Unity EditMode chưa được chạy trên worktree hiện tại, báo cáo không claim Unity regression PASS mới.

## 7. Việc tối thiểu tiếp theo

Không nên hạ threshold hoặc mở locked test. Thử nghiệm falsifiable tiếp theo nên là một capture bounded mới tập trung vào tối thiểu 10 independent positive pose families, mỗi family có chuỗi causal qua hai phía score boundary, yaw variation và repeat render. Sau đó giữ nguyên structural-v4 làm spatial baseline và kiểm tra một recognition head family-balanced; quality head hiện tại có thể giữ làm ranking.

Chỉ khi calibration mới đạt ≥80% recall, zero FP và ổn định qua seed mới được mở locked test một lần; chỉ sau locked-test PASS mới chạy tối đa bốn paired Unity episodes.

## 8. Evidence

- `g2_r2_results.json`
- `g2_r2_structural_temporal_candidate_v3.json` (BLOCKED)
- `g2_r2_ambiguity_multiresolution.png`
- `g2_r2_label_observability_audit.json`
- `g2_r2_repeat_render_audit.json`
- `family_usage_ledger.json`
- `g2_r2_test_matrix.json`
- `g2_r2_attempt1_parity_bug_results.json`
- `g2_r2_attempt1_parity_bug_checkpoint.json`

Source hashes:

```json
{
  "Tools/ThoracicUSCoach/g2_r2_features.py": "f46732d6b786cacd20bc96c28bb836b627fc542d1f829f0cb21740be7deb696d",
  "Tools/ThoracicUSCoach/prepare_g2_r2_manifest.py": "405915e8655dc7ab4b68dbad867f2cdefd8f827cf6ab64c64e9b3e16f08c9e7c",
  "Tools/ThoracicUSCoach/run_g2_r2_experiment.py": "8ad07234c8fcdb33008ae0f3a2236afd888320d41f18e23d90b9db60456ad7e5",
  "Tools/ThoracicUSCoach/active2d_stop.py": "d74f03b0c72d7fe254caf6e74464a156fa56fbd7d4878507b8d7d07bd40dfdc4",
  "Tools/ThoracicUSCoach/run_active2d_policy_server.py": "5e726089ece1a3dac756a8dd6540c116851635b25ac49640b9739636f4f6b9e3"
}
```

Không có claim về ultrasound thật, lâm sàng hoặc anatomy/patient chưa thấy.
