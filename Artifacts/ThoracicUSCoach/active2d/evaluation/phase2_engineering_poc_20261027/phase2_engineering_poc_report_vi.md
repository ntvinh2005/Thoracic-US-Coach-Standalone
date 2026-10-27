# Phase 2 Fixed-Anatomy Engineering POC

Kết luận: `INTEGRATION_FAIL`.

Hai Unity job bounded đều dừng ở preflight trước khi Unity mở, nên không có episode mới để tuyên bố end-to-end success. Job `fe180c09a7c5` và corrective rerun duy nhất `8217516383e4` đều exit 1, chạy 0 episode. Blocker đầu tiên là process của CSSALT Local Jobs không nhận linked worktree sạch là Git repository; lần sửa `git -C` sang `Push-Location` không giải quyết được môi trường runner này.

| Episode | Target | Recognized | Max score | Returned | Fresh confirm | Correct STOP | False STOP | Abstain | Violation | Failure |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| Không chạy được episode mới | — | — | — | — | — | — | — | — | — | Scheduler Git-worktree preflight |

Offline diagnostic trên evidence development đã consumed cho thấy không có lỗi preprocessing/live inference: sai số live/offline của spatial-v2 bằng 0. Spatial-v2 cho bốn positive fresh 0.0249–0.1493 nhưng cho hard-negative 0.7585, nên không được cứu bằng threshold tuning.

Recognizer template image-only riêng (`DIAGNOSTIC_ONLY`, `NON_DEPLOYABLE`) dùng pixel ultrasound grayscale 32×32. Calibration consumed có 7/7 positive và 0/210 false positive; bốn ảnh positive fresh không tham gia calibration đạt 0.9852–0.9861, còn hard-negative fresh đạt khoảng 9.36e-14. Đây chỉ là local engineering separability, chưa phải Unity end-to-end hoặc scientific validation.

State machine đã có cờ diagnostic để nhớ positive image-only tại prefix pose đã thật sự visited. Default scientific behavior không đổi. Return vẫn dựa causal executed-action odometry, và STOP vẫn đòi fresh confirmation positive.

Ba ảnh trong `representative_frames/` là ảnh fresh của smoke run development trước, chỉ dùng minh họa offline; không được gọi là frame của final run.

Các frozen contract được giữ nguyên: Phase 1, `ScoreCandidate()`, evaluator floor, spatial-v2 weights và threshold 0.93 không thay đổi. Không checkpoint nào được promote.
