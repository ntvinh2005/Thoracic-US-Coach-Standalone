# Báo cáo chẩn đoán wrong STOP — `wrong_stop_diagnostic_v1_20261022`

Ngày chạy: 2026-10-22 (America/New_York)  
Nguồn chính: `main_cpu_v3_20261022`  
HEAD được kiểm tra: `b131093459b91236e6ff3033b4db62d4dbd14e21`

## Kết luận ngắn

Đã xác định cơ chế lỗi trội trên development: policy kết thúc episode bằng STOP sai, chủ yếu trước khi từng đi vào target. Đây không phải lỗi boundary-sticking và cũng không phải do movement policy không biết hướng đi trên subset diagnostic.

Can thiệp NON-DEPLOYABLE `learned movement + oracle STOP` đạt 18/18 acquisition, trong khi `oracle movement + learned STOP` chỉ đạt 6/18. Điều này cô lập STOP decision là nút thắt chính trên subset cố định.

Sửa nhỏ nhất có hiệu ứng là thay three-action argmax bằng STOP threshold 0,65 rồi chọn movement có logit cao nhất khi STOP bị từ chối. Trên check tách biệt, seed 13 tăng 5/18 → 6/18 và seed 37 tăng 7/18 → 8/18; recognition vẫn 6/6, constraint violation bằng 0. Hiệu ứng chỉ +1/18 mỗi seed, chưa đủ để tuyên bố bài toán đã được giải.

## 1. Pause an toàn và tiến độ có thể resume

- Phase đang chạy lúc nhận task: **EVALUATION**, không phải capture/training.
- Runner: `run_dev_selection_matrix.py`; child: `evaluate_live.py` với fixed checkpoint `ppo_best_seed101`.
- `run_frozen_evaluation()` không cập nhật trọng số; chạy tiếp không thể làm model tốt hơn.
- Runner điều phối đã được dừng, child `ppo_best_seed101` được để hoàn tất và ghi JSON lúc 21:28:44.
- Đã lưu 15/23 candidate. Candidate cuối: recognition 152/170, acquisition 396/860, wrong STOP 482, region-entered failure 180, constraint 0.
- 8 candidate còn lại chưa chạy. Không có Python process/bridge owner sau khi diagnostic kết thúc; Unity PID 630572 vẫn sống và responsive.
- Không chạy final held-out/stress. Không thay đổi manifest/selection rule/experiment identity chính.

## 2. Failure decomposition từ episode records

Chỉ xét 860 acquisition episode của mỗi checkpoint:

| Checkpoint | Correct STOP | Wrong STOP trước entry | Wrong STOP sau entry | Timeout/other | Wrong STOP bước 1 | Wrong STOP muộn |
|---|---:|---:|---:|---:|---:|---:|
| BC history seed 13 | 268 | 541 | 51 | 0 | 271 | 321 |
| PPO seed 13 | 434 | 301 | 125 | 0 | 154 | 272 |

Các số này được đếm trực tiếp từ `policy.episodes`, không chỉ trừ aggregate. PPO giảm 240 premature STOP trước target nhưng tăng 74 failure sau khi từng vào target và làm recognition giảm. Không thể quy mọi wrong STOP cho bước đầu: BC có 321 và PPO có 272 wrong STOP sau ít nhất một movement.

## 3. Audit history contexts và shortcut

Training transition rows hợp lệ theo filter hiện tại:

- Train: 3.077 rows; dev: 582 rows.
- Post-terminal STOP rows: 300 train và 60 dev.
- Pre-STOP hợp lệ vẫn có: 384 train và 78 dev.
- Reset rows: 300 train, gồm 54 inside và 246 outside.
- Cả 300 reset dùng STOP one-hot dù `actionWasExecuted=false`.
- 0/300 reset có previous/current hash giống nhau; capture render hai ảnh riêng, online khởi tạo `previous=current`.

Shortcut probes trên dev:

- Previous-action-only accuracy: 59,28%.
- Current-image-only accuracy: 83,16%.
- History image + action accuracy: 86,77%.
- Counterfactual đổi previous action làm STOP/non-STOP flip 22/582 (`-1`), 50/582 (`STOP`) và 31/582 (`+1`). Đây là sensitivity test, không phải trajectory hợp lệ.

Ablation cùng context (522 decision rows, linear history seed 13):

| Variant | STOP precision | STOP recall | STOP FPR |
|---|---:|---:|---:|
| A0 existing | 59,29% | 72,04% | 10,72% |
| A1 bỏ post-terminal | 59,02% | 77,42% | 11,66% |
| A2 A1 + reset `previous=current` | 58,20% | 76,34% | 11,89% |

A1/A2 tăng recall nhưng tăng false-positive và không đạt tiêu chí giảm premature STOP, nên không được đưa vào live evaluation.

## 4. STOP rule và detector riêng

Offline dev-check tách family:

- B0 three-action argmax: precision 56,96%, recall 77,59%, FPR 13,60%.
- B1 threshold thỏa recall calibration ≥95%: threshold 0,043; check recall 100% nhưng FPR 27,60%.
- Binary detector z-score: check recall 100%, FPR 26,25% tại operating point recall-95.
- Raw `[0,1]`: FPR 62,32%; tệ hơn rõ rệt.
- Z-score + mean/std cho prediction giống z-score trong linear detector này.

Live 6-geometry check, 24 episode/policy:

| Policy | Recognition | Acquisition | Wrong trước entry | Wrong sau entry | Constraint |
|---|---:|---:|---:|---:|---:|
| B0 current argmax | 6/6 | 4/18 | 10 | 4 | 0 |
| B1 offline-calibrated threshold | 6/6 | 2/18 | 16 | 0 | 0 |
| B2 binary STOP + same navigation | 6/6 | 2/18 | 16 | 0 | 0 |
| B3 binary STOP + bounded scan | 6/6 | 2/18 | 16 | 0 | 0 |
| I1 oracle movement + learned STOP | 6/6 | 6/18 | 8 | 4 | 0 |
| I2 learned movement + oracle STOP | 6/6 | 18/18 | 0 | 0 | 0 |
| I3 oracle + oracle | 6/6 | 18/18 | 0 | 0 | 0 |

B3 có quyền truy cập `patchBoundaryViolation` và không phải comparison image-only ngang quyền. I1–I3 dùng privileged fields và là NON-DEPLOYABLE. I2 ép chọn non-STOP logit cao nhất khi actor muốn STOP ngoài target; đây là policy đã bị thay đổi, chỉ dùng để cô lập lỗi.

## 5. Cache-to-live thật

Test mới dùng 3 actual captured development trajectories, 12 pose probes, 6 fixed-action revisits và 12 policy-driven rollouts với BC/PPO checkpoints thật.

- Stored `cached_features()` và `request_features()` khớp tuyệt đối: max abs 0.
- Contract/dynamics: 150/150 comparison khớp.
- Không có history image bị retention xóa trước khi dùng.
- Fresh render raw RMS: mean 0,001908; max 0,003155.
- Feature RMS sau per-image z-score: mean 0,10138.
- Probability max-abs: 0,04223 trong run 3-trajectory.
- 5/12 policy-driven rollout đổi action sequence; 2/12 đổi outcome.
- Một cached success thành live failure do STOP score tại target đổi 0,571 → 0,497; một cached failure thành live success sau action flip.

Vì vậy test RMS <0,005 chỉ xác nhận render gần nhau ở raw pixel, không xác nhận policy equivalence.

## 6. Preprocessing và target learnability

- Main dataset này được tạo bởi `BuildVariedPath()` và dùng discovery reference floor đã khóa.
- Demo `BuildPath()` dùng path-relative max × `acceptableRelativeToBest`; hai target floor không tương đương.
- Dev có 149/1.048 acceptable frames và 1 internal unacceptable hole.
- 122 adjacent boundary pairs: raw RMS mean 0,00796 nhưng z-score RMS mean 0,6774.
- 5 adjacent boundary pairs có raw RMS <0,005.
- 20 nearest opposite-label pairs đều raw RMS <0,005; nhỏ nhất 0,001123.
- Low-contrast quartile không chứa acceptable target và detector error bằng 0; failure không đơn giản là noise ảnh tối.
- Score target kết hợp pixel-derived terms với privileged anatomy labels/geometry. Opposite-label near-neighbors chứng minh ambiguity cho linear representation hiện tại, không chứng minh ultrasound observation bất khả thi.
- Acceptability threshold không bị hạ.

## 7. PPO update và selection

Trên cả 5 seed:

- Cached PPO: khoảng 50k transition, 100 update.
- Live PPO: 2.007–2.023 transition, 4 update × 4 epoch = 16 actor gradient step; critic cũng 16 Adam step.
- Critic live bắt đầu từ 0 nếu không `--resume`; first value prediction mean = 0 trên mọi seed.
- Actor cuối có thay đổi nhỏ, không phải “không học”. Seed 13: weight L2 Δ=0,01023, max |Δ|=0,00209.
- Selected-best của cả 5 seed bằng chính xác cached input (actor Δ=0) vì update không thắng selection rule.
- Clip fraction = 0; KL rất nhỏ; ratio gần 1. Update là bảo thủ.
- Seed 13 dev subset: initial 41,7% acquisition/83,3% recognition; update 2 là 41,7%/75%; update 4 quay về 41,7%/83,3%. Gate 95% được giữ nguyên.
- Live training chỉ dùng 5 named start types; full development đánh giá mọi pose (1.030 starts). Đây là coverage mismatch.

Advantage probe 24 cached episode với final live critic, chỉ là diagnostic chứ không tái tạo batch training:

- Correct STOP: 12/12 advantage dương.
- Wrong STOP: 12/12 advantage âm.
- Positive-reward movement: 58,4% advantage âm do return/critic dài hạn.

DP exact trên 360 train+dev trajectories, horizon 35, gamma 0,99:

- Oracle action tối ưu ở 6.182/6.182 states.
- STOP tối ưu ở 952 inside states và 0 outside states.
- Optimal rollout thành công 6.182/6.182.

Không có bằng chứng để đổi reward.

## 8. “2D localization” hiện tại

Không có learned 2D navigator mới. Pipeline bắt đầu từ `t7RightSeed`, dùng raycast skin, contact/geometry, anatomy labels và `ScoreCandidate()` để discovery/path preparation. Policy học chỉ điều khiển dọc frozen 1D path. Đây là oracle-assisted preparation, không phải learned ultrasound-only 2D localization.

## 9. Smallest correction và measured effect

Decision rule mới: `STOP iff P(STOP) >= 0.65`; nếu không, chọn movement logit cao nhất. Không đổi weights/reward/labels/action budget.

Threshold được chọn chỉ trên 6 calibration geometries với gate recognition ≥95%, sau đó chạy một lần trên 6 check geometries:

| Seed | B0 argmax check | Threshold 0,65 check | Recognition | Constraint |
|---|---:|---:|---:|---:|
| 13 | 5/18 | 6/18 | 6/6 | 0 |
| 37 | 7/18 | 8/18 | 6/6 | 0 |

Seed 13 direct rerender của B0 dao động 4–5/18, phù hợp cache/live sensitivity. Kết quả threshold là cải thiện nhỏ, cùng hướng ở hai seed, nhưng chưa đủ để giải quyết acquisition.

## 10. Decision và exact next experiment

Khuyến nghị: **run a corrected small experiment under a new identity**, không resume unchanged 23-candidate matrix và chưa mở final held-out/stress.

Đề xuất identity: `stop_history_robust_v1_20261022`.

1. Giữ nguyên labels, reward, action budget, movement actor và recognition gate 95%.
2. Baseline bắt buộc: current argmax và threshold 0,65.
3. Train một binary STOP head dùng đúng history inputs (`previous/current/action`) trên train only; bỏ post-terminal contexts, reset `previous=current`, fingerprint mới.
4. Calibrate threshold trên 6 development geometries mới; kiểm tra trên 6 development geometries disjoint khác, seed 13 rồi seed 37.
5. Interleave paired resets/renders để giảm bias do stochastic renderer.
6. Continue criterion: constraint=0, recognition ≥95% ở cả hai seed, premature STOP giảm và acquisition tăng ít nhất 3/18 mỗi seed so với paired argmax. Nếu không đạt, dừng training và chuyển sang observability/contract investigation (versioned repeated-render aggregation hoặc target definition), không “train longer”.

## 11. Success criteria

- Dominant mechanism được xác định: **Có**, trên fixed diagnostic subset. Learned movement + oracle STOP 18/18; oracle movement + learned STOP 6/18.
- Targeted correction cải thiện check mà không che regression: **Có nhưng nhỏ**. +1/18 mỗi seed, recognition 6/6, constraint 0.
- Final task/model solved: **Không**. Subset nhỏ và renderer sensitivity không cho phép tuyên bố final success.

## 12. Kiểm thử và lưu ý isolation

- Python unit tests: 22/22 pass.
- Không thay đổi C# runtime trong diagnostic này.
- Không chạy final held-out/stress performance.
- Một pass loader ban đầu đã enumerate metadata của captured `test` split trước khi thêm filter; không chạy final scenario manifest/performance và không dùng số đó để chọn/tune. Loader sau đó được sửa lọc split trước khi đọc/hash ảnh, và toàn bộ saved diagnostic reports được chạy lại chỉ với train/dev.
