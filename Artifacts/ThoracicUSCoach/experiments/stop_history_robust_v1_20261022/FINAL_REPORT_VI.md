# Báo cáo `stop_history_robust_v1_20261022`

Ngày chạy: 2026-10-22/23, America/New_York  
Repository: `Thoracic-US-Coach-Standalone`  
HEAD lúc bắt đầu và kết thúc: `7b1dfc78aeb9151fd6698f15bb5ce06b88da2bb4`  
Trạng thái: thay đổi chưa commit; không chọn checkpoint deployment mới.

## 1. Kết luận

Đã sửa được lỗi semantics dữ liệu/history, triển khai controller hai tầng image-only, chạy bảy STOP candidate CPU, audit renderer thật và một so sánh Unity live interleaved. Tuy nhiên không candidate nào vượt baseline trên development check. Candidate tốt nhất theo cached check giảm acquisition từ 23/54 xuống 18/54. Trên fresh live calibration, acquisition giảm 14/36 xuống 9/36 và recognition giảm 10/12 xuống 6/12.

Vì vậy continuation gate không đạt. Không chạy seed 37, fresh development check, full-development validation, PPO, heldout hay stress. Kết quả đúng là **không có selected candidate**.

Bằng chứng hiện tại cho thấy STOP v3 bị giới hạn bởi khả năng nhận dạng label từ ultrasound observation: label kết hợp tín hiệu ảnh rất yếu với anatomy/geometry đặc quyền; các ảnh khác label có thể gần như giống nhau, và operating point recall >=95% kéo FPR lên mức không dùng được cho search nhiều bước. Đây là bằng chứng mạnh về poor identifiability trong simulator/contract hiện tại, không phải chứng minh toán học rằng mọi mô hình ảnh đều bất khả thi.

## 2. Thay đổi mã nguồn

- `coach_data.py`
  - Thêm `valid_policy_decisions()`; loại terminal/truncated khỏi supervised policy inputs.
  - Thêm `decision_transition_feature_matrix()`; reset dùng `previous=current`, STOP chỉ là sentinel với `actionWasExecuted=false`.
  - Thiếu previous image ngoài reset giờ ném `FileNotFoundError`, không silently thay bằng current.
- `train_bc_policy.py`
  - Lọc manifest `train/dev` trước khi đọc/hash ảnh.
  - Dùng đúng decision rows và fingerprint sau lọc.
- `stop_policy.py`
  - Binary linear/MLP STOP head; current/history/difference/statistics; per-image z-score, global train normalization, measured-noise augmentation và structural image-derived features.
  - Controller hai tầng: STOP head quyết định STOP; nếu từ chối thì frozen navigation head chỉ chọn Backward/Forward.
  - Inference không dùng score, target mask, distance, path index hay geometry.
- Script mới: `run_stop_experiment.py`, `evaluate_two_stage_live.py`, `audit_repeated_render.py`, `audit_stop_separability.py`, `audit_resolution_separability.py`, `build_stop_visual_audit.py`, `split_fresh_development.py`.
- `test_stop_policy.py`: regression tests cho terminal/reset/history/feature contract.
- README có commands tái lập và ghi rõ negative gate.

Không thay C# runtime, reward, target threshold, frozen label, original checkpoint hay original artifact.

## 3. Bảo toàn thí nghiệm cũ

- Toàn bộ `main_cpu_v3_20261022` và `wrong_stop_diagnostic_v1_20261022` giữ nguyên.
- Mọi output mới nằm dưới `Artifacts/ThoracicUSCoach/experiments/stop_history_robust_v1_20261022`.
- Không overwrite checkpoint lịch sử.
- Baseline navigation thực tế:
  - `ppo_live_history_seed13_best_dev.json`
  - SHA-256 `6543eb318a3a14616a67b30e159bb138d4f86d303d4b587151ace709035dcbda`.

## 4. Root cause đã xác nhận và giả thuyết còn mở

Đã xác nhận:

1. Post-terminal rows từng lọt vào BC, dù deployed actor không bao giờ được phép ra quyết định sau terminal.
2. Reset capture dùng hai render khác nhau, còn serving dùng `previous=current`; STOP sentinel từng bị encode giống executed STOP.
3. Loader/trainer cũ enumerate mọi split trước khi lọc; bản sửa lọc train/dev ở manifest level.
4. Wrong STOP là terminal failure chính; dữ liệu cũ đã phân rã 541/51 wrong STOP trước/sau entry cho BC và 301/125 cho PPO trên 860 acquisition cases.
5. Per-image z-score khuếch đại renderer differences; matched-cache audit cũ có raw RMS 0,00191 nhưng feature RMS 0,101 và 5/12 action-sequence flips.
6. Reward không phải root cause: exact DP với horizon 35, gamma 0,95 khớp PPO cho oracle-optimal 6.182/6.182 states; STOP optimal ở 952 inside và 0 outside; rollout 6.182/6.182 thành công.

Chưa chứng minh:

- Một CNN lớn hơn có thể hay không thể học thêm tín hiệu. Không có bằng chứng để biện minh compute lớn sau khi linear, MLP, history, global normalization, noise và structural features đều không vượt baseline.
- Ultrasound observation nói chung không thể giải bài toán. Kết luận chỉ áp dụng cho renderer, label v3, dữ liệu và budget compact CPU này.

## 5. Chất lượng ảnh và score semantics

Ảnh gốc 256x256 có các contour sáng và speckle mờ nhưng phần lớn nền đen; mean intensity khoảng 0,01 không phải ảnh rỗng hay orientation error. `CaptureCleanImage()` blit trực tiếp clean runtime texture; không tìm thấy dấu hiệu magenta/display-lighting contamination.

`acousticWindow=0`, `shadowPenalty=1`, `contactPenalty=0` là phù hợp với dải intensity và các ngưỡng hiện tại, không phải lỗi serialize. Tuy vậy chúng làm score suy biến cho bài toán học:

- acoustic contribution luôn 0;
- shadow contribution luôn -0,18;
- contact contribution luôn 0;
- pleuraVisibility rất nhỏ và gần tương đương giữa class;
- biến thiên label chủ yếu đến từ `anatomyRelationship`, `targetRegion`, `needleGeometry`.

Trên dev, mean component acceptable/unacceptable lần lượt khoảng:

- anatomy: 0,959 / 0,762;
- targetRegion: 0,947 / 0,688;
- needleGeometry: 0,775 / 0,470;
- pleuraVisibility: 0,0174 / 0,0176.

`targetRegion` và `needleGeometry` dùng `TargetProjection()`/target labels; anatomy dùng programmatic image-to-mesh labels. Actor không nhận các trường này. Trong 45 same-pose live resets, observed score không đổi ở từng pose dù ảnh raw thay đổi, xác nhận pixel-dependent terms đang saturated/constant trong các case đã đo.

## 6. Observability và resolution

- 149/1.048 dev frames acceptable.
- 20 nearest opposite-label pairs ở 16x16 đều có raw RMS <0,005; minimum 0,001123.
- Median nearest-unacceptable distance của acceptable frames:
  - 16x16: 0,002773;
  - 32x32: 0,006457 (2,33x).
- Median adjacent boundary RMS:
  - 16x16: 0,007857;
  - 32x32: 0,016779 (2,14x).

32x32 giữ thêm một ít discriminative signal, nhưng nearest-opposite và boundary distance tăng gần cùng tỷ lệ; chưa có bằng chứng closed-loop benefit, phù hợp pilot trước rằng tăng resolution đơn thuần không giải acquisition.

Repeated-render live, 3 calibration geometries x 3 poses x 5 renders = 45 resets:

- same-pose original-resolution raw RMS mean 0,03461;
- same-pose z-score-16 RMS mean 0,08942;
- adjacent inside/outside mean-render raw RMS mean 0,06419;
- between/within ratio chỉ 1,85;
- STOP-score range mean 0,0213, max 0,0566;
- 0/9 pose group action-flip, nhưng có systematic errors: một acceptable pose luôn movement với STOP score khoảng 0,12; một immediately-outside pose luôn STOP với score khoảng 0,85.

Contact sheets:

- `stage_a_visuals/representative_raw_original_resolution.png`
- `stage_a_visuals/representative_model_features_16x16.png`

## 7. Dữ liệu decision-time đã sửa

- 300 train + 60 dev post-terminal STOP rows không còn là policy training inputs.
- Legitimate pre-STOP observations vẫn giữ.
- Reset dùng chính current image làm previous; STOP sentinel được kiểm tra `actionWasExecuted=false`.
- Non-reset bắt buộc previous/current tương ứng action thực thi.
- Missing retained previous image bị reject.
- Targeted train-only augmentation tạo valid reset tại mọi vị trí và cả hai approach directions theo exact deterministic path dynamics.
- Hard negatives ưu tiên các pose không acceptable cách target <=2 steps.
- Fingerprint mới tính trên corrected decision contexts.

Regression suite cuối: 27/27 pass.

## 8. STOP classifier seed 13

Development calibration/check dùng 18/18 captured dev families disjoint. Check không dùng để chọn threshold; nhưng suite thuộc development lịch sử, không được gọi là final independent test.

| Candidate | Threshold | Precision | Recall | FPR | Cached acquisition | Recognition |
|---|---:|---:|---:|---:|---:|---:|
| current linear z-score | 0,450 | 34,4% | 93,9% | 22,3% | 14/54 | 16/18 |
| corrected history linear | 0,472 | 35,8% | 92,9% | 20,8% | 16/54 | 16/18 |
| history MLP32 z-score + diff | 0,550 | 36,8% | 89,9% | 19,2% | 18/54 | 16/18 |
| history MLP32 global | 0,475 | 35,7% | 91,9% | 20,6% | 13/54 | 15/18 |
| history MLP32 global + noise | 0,525 | 36,1% | 87,9% | 19,4% | 12/54 | 15/18 |
| current MLP64 structural | 0,525 | 34,0% | 97,0% | 23,4% | 12/54 | 17/18 |
| history MLP32 structural | 0,425 | 31,7% | 88,9% | 23,9% | 11/54 | 15/18 |

Original three-action argmax trên cùng cached check: acquisition 23/54, recognition 15/18. Threshold 0,65: 14/54 và 11/18.

Candidate mạnh nhất trong nhóm mới theo cached acquisition là `history_mlp32_zscore_diff_seed13.json`, SHA-256 `2e2623bc16c8812c0a44812ac8d6bc870914822bddaefe7bd4f5d4378c81646d`; nó vẫn thua baseline 5/54 và không được chọn.

Separability của candidate này: AUC train/calibration/check = 0,908/0,924/0,902. Đây không chỉ là overfit family. Ở check, operating point frame-level recall >=95% yêu cầu FPR 27,2%, quá cao vì false positive tích lũy qua nhiều bước.

## 9. Original actor so với controller hai tầng live

Fresh development calibration identity: 6 geometries đầu từ manifest 12 geometry mới; 4 starts/geometry; mỗi case lặp hai lần; policy order interleaved. Suite này dùng để chẩn đoán/selection, không phải independent check.

| Live policy | Acquisition | Recognition | Immediate recognition | Constraint |
|---|---:|---:|---:|---:|
| Original three-action argmax | 14/36 | 10/12 | 10/12 | 0 |
| Two-stage history MLP32 STOP + frozen movement | 9/36 | 6/12 | 6/12 | 0 |

Inference latency mean: 2,26 ms/decision baseline; 4,80 ms/decision two-stage, CPU only.

## 10. Wrong-STOP và control decomposition live

| Live policy | Wrong STOP trước entry | Wrong STOP sau entry | First-decision wrong STOP | Region entered nhưng fail | Wrong movement | Reversal | Timeout |
|---|---:|---:|---:|---:|---:|---:|---:|
| Argmax | 9 | 15 | 6 | 15 | 4 | 5 | 0 |
| Two-stage | 19 | 7 | 4 | 14 | 30 | 97 | 7 |

Two-stage giảm wrong STOP sau entry nhưng tăng mạnh premature STOP trước entry, movement rejection/reversal và timeout. Đây không phải improvement bị che bởi một metric khác.

## 11. Renderer robustness và cache/live

Global normalization và augmentation Gaussian raw sigma 0,0019 không cải thiện cached check; acquisition lần lượt 13/54 và 12/54. Structural features cũng thất bại.

Audit cũ trên exact actual-cache/live pairs vẫn có hiệu lực cho baseline: 150/150 dynamics matches, raw RMS mean 0,001908 ở 32x32, z-score feature RMS mean 0,1014, 5/12 action-sequence divergences, 2/12 outcome divergences. Audit mới bổ sung same-pose repeats và live closed-loop candidate, nhưng không tạo exact cached equivalent cho fresh geometries. Vì candidate đã fail cả cached check và live calibration, không có claim cache-to-live improvement.

## 12. Compute và Unity cost

- 7 supervised candidate runs, tổng candidate wall time khoảng 933,7 s (15,6 phút), CPU-only, BLAS giới hạn 2 threads.
- Feature matrix lớn nhất khoảng 119,9 MB; toàn bộ experiment artifacts khoảng 8,6 MB.
- 96 closed-loop Unity episodes: 48 baseline + 48 two-stage.
- 45 same-pose Unity resets cho renderer audit.
- 12 development geometry generations để khóa fresh 6 calibration + 6 check.
- Không PPO transitions mới.

## 13. Development/final separation

- Chỉ đọc train/dev cho training và diagnostics mới.
- Fresh manifest SHA-256 gốc: `45660540a41753876334cbf0ff5dcf12e5db80b16ef92def773a725918761d12`.
- Calibration manifest: `6a42ed85973e86cf374bf85e4b5a030b1e50d57c1256eb136aa61473c0d6619a`.
- Fresh check manifest: `7637010a196b06defa586a05eaa4387fcef986811f54c702d7fc36b84ef41005`.
- Fresh check được tạo/khóa identity nhưng không chạy policy performance.
- Không truy cập heldout/stress performance, không final tuning, không final claim.
- Kết quả chỉ seed 13; không replicated vì continuation gate fail. Không được mô tả là two-seed result.

## 14. Selected candidate

Không có. Candidate seed 13 không vượt baseline trên cached check và live calibration; recognition cũng dưới 95%. Seed 37, larger dev và PPO bị dừng đúng preregistered gate.

## 15. Commands tái lập chính

Từ `Tools/ThoracicUSCoach`:

```powershell
$uv = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\astral-sh.uv_Microsoft.Winget.Source_8wekyb3d8bbwe\uv.exe"
& $uv run --python 3.11 python -m unittest discover -v

& $uv run --python 3.11 python run_stop_experiment.py --root ..\.. `
  --dataset-run-id main_cpu_v3_20261022 `
  --navigation-checkpoint ..\..\Artifacts\ThoracicUSCoach\checkpoints\main_cpu_v3_20261022\ppo_live_history_seed13_best_dev.json `
  --seed 13 --calibration-trajectories 18 --check-trajectories 18 `
  --output-dir ..\..\Artifacts\ThoracicUSCoach\experiments\stop_history_robust_v1_20261022\seed13

& $uv run --python 3.11 python audit_resolution_separability.py --root ..\.. `
  --dataset-run-id main_cpu_v3_20261022 `
  --output ..\..\Artifacts\ThoracicUSCoach\experiments\stop_history_robust_v1_20261022\stage_a_resolution_separability.json

& $uv run --python 3.11 python evaluate_two_stage_live.py `
  --manifest ..\..\Artifacts\ThoracicUSCoach\experiments\stop_history_robust_v1_20261022\fresh_development_calibration_6.json `
  --navigation-checkpoint ..\..\Artifacts\ThoracicUSCoach\checkpoints\main_cpu_v3_20261022\ppo_live_history_seed13_best_dev.json `
  --stop-checkpoint ..\..\Artifacts\ThoracicUSCoach\experiments\stop_history_robust_v1_20261022\seed13\checkpoints\history_mlp32_zscore_diff_seed13.json `
  --repeats 2 --max-steps 35 `
  --output ..\..\Artifacts\ThoracicUSCoach\experiments\stop_history_robust_v1_20261022\live_calibration_seed13.json

& $uv run --python 3.11 python diagnose_reward_dp.py --dataset-run-id main_cpu_v3_20261022 `
  --diagnostic-run-id stop_history_robust_v1_20261022 --horizon 35 --gamma 0.95 `
  --output-dir ..\..\Artifacts\ThoracicUSCoach\experiments\stop_history_robust_v1_20261022\stage_a_reward
```

## 16. Hạn chế và next experiment

Không nên tăng PPO steps hay đổi threshold tiếp trên v3. Next experiment hợp lý là versioned environment contract v4:

1. Tách score dùng cho discovery/geometry khỏi label STOP có thể quan sát từ ultrasound.
2. Định nghĩa acceptance bằng image-visible anatomy criteria hoặc render một signature vật lý thật sự hiện diện trong clean ultrasound; không đưa mask/geometry trực tiếp vào actor.
3. Audit từng score component để tránh constant/saturated terms.
4. Recapture train/dev/final hoàn toàn mới; v3 checkpoints/manifests không tương thích và phải ghi rõ non-comparable.
5. Trước policy training, yêu cầu compact image-only classifier đạt recall >=95% với FPR đủ thấp cho episode dài và vượt renderer variability trên family-disjoint live calibration.

Nếu giữ nguyên label v3, thí nghiệm tiếp theo chỉ nên là một bounded sensing/label study (ví dụ nhiều render được tính là action có cost trong contract v4), không phải free STOP confirmation và không phải thêm compute mù quáng.
