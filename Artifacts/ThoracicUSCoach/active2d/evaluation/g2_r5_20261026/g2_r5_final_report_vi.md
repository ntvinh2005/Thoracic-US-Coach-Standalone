# G2-R5 — Báo cáo điều phối cuối

Ngày thực hiện: 2026-10-26

Nhánh phát triển: `codex/u08-phase2`

Nguồn ban đầu đã xác minh: `bdbb6e90ebe7e057b704391ede9961815a8bdbb5`

## Kết luận

Kết luận khoa học độc lập là **`FEASIBILITY_INCONCLUSIVE`** với integrity **PASS**.

R5-A chứng minh simulator có thể tạo thêm nhiều cấu hình âm học âm tính khác biệt trên anatomy hiện tại, nhưng không quan sát được positive view nào. Vì điều kiện GO cần tối thiểu ba component positive và ba component negative, **R5-B không được phép chạy**.

Đây là kết quả khoa học chưa đạt GO, không phải lỗi hoàn thành engineering. Pipeline capture, provenance, immutable handoff và independent audit đã hoạt động đầy đủ sau khi các lỗi pre-capture/post-capture được sửa.

## Hai worker chạy song song

Hai worker duy nhất đã chạy đồng thời theo đúng phân công:

- Agent A — Unity & Data Generation: worktree `D:\CSSALT\Thoracic-US-Coach-Standalone-r5-unity`, branch `codex/r5-unity`, HEAD cuối `2156865`.
- Agent B — Independent Scientific Auditor: worktree `D:\CSSALT\Thoracic-US-Coach-Standalone-r5-audit`, branch `codex/r5-audit`, HEAD cuối `893f9f85d0a8c87f6d265b11111e6ba327d9cfd7`.

Không có worker thứ ba. Chỉ Agent A mở Unity. Agent B chỉ đọc snapshot sau khi snapshot được finalize bất biến.

## Contract và provenance đã đóng băng

- Coordinator protocol canonical SHA-256: `04782fd9d216a15f0d67cdc9eaf25a556c3a31d69ecd3359db8eee955a046d5d`.
- Evidence schema canonical SHA-256: `eb66798b4348c7d335090f6d8c210aa1b4cd9409c9f8aa64e800bdd05643e0b0`.
- Independent audit protocol canonical SHA-256: `17e30dd075af8510809ff405a1489a020847f36385ae5cc6f535b269757ed582`.
- Scene canonical SHA-256: `6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d`.
- Evaluator canonical SHA-256: `7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc`.
- Frozen target floor: `0.5912307500839233`.
- Visual novelty cutoff: RMS128 `> 0.013763083470985293`, được cố định từ renderer-noise lịch sử trước khi xem scout.

Contract ghi riêng canonical LF hash và Windows CRLF transport hash để tránh sai provenance do line ending.

## Reference index

Historical index đã kiểm tra:

- 20 consumed trajectories.
- 1.120 frames và 1.120 image hashes duy nhất.
- 12 primary spatial components; 2 conservative components.
- Frame-set digest: `70e0d092…`.
- Không mở locked families; không mở confirmation data.

## Unity capture và job evidence

Các job:

- `2399a8537190`: preflight đầu, COMPLETED exit 0.
- `874bc225c2d5`: fail trước Unity/capture do background Git safe-directory; 0 ảnh.
- `a6ff4093f572`: fail trước capture do PowerShell/.NET không có `Path.GetRelativePath`; 0 ảnh.
- `6236848da4bf`: final no-target-gating preflight, COMPLETED exit 0; 39/39 tests PASS.
- `a8c8dfcb5699`: Unity capture hoàn thành và seal đủ 96 ảnh; wrapper fail sau capture vì `Get-FileHash` không có trong runner environment.
- `2442220e9829`: offline-only finalize, COMPLETED exit 0; không mở Unity, không recapture, kiểm tra toàn bộ hash/size/seal rồi atomic handoff và đặt read-only.

Capture source commit: `04bfadc9119051dec0f57d5f64e4496ea9acbd71`.

Capture manifest SHA-256: `7247cd491a5defafc5a5a438bcad4675fbb43c426737b5db551eaf3c828e37bf`.

Kết quả capture:

- 48 proposal tối đa đã predeclare; 9 proposal thực tế được thử.
- 8 start được chấp nhận; 1 start bị từ chối và vẫn được ghi lại.
- 8/8 scout routes hoàn thành.
- 88/88 movement được thực thi và chấp nhận.
- 96/96 ảnh được capture, đúng budget.
- 0 safety failure, 0 contact violation, 0 workspace violation.
- 87 T7-valid records; 9 off-T7 records được giữ cho integrity/descriptive audit nhưng không được phép hỗ trợ quyết định GO.
- 0 positive observations; 5 hard-negative observations thô.

Snapshot bất biến:

- Evidence: `2fb7ea6f8dfc870986d2f51febf552fcfc1361b76a44ca1fe1c2eb8b30f87ee0`.
- Evidence index: `6342a5fdce618ad053cf97f68e837df31ab18f3d0d16079f384db1e8298e3988`.
- Snapshot seal: `9e69b6a86895a2d2fb7b1775c9734633b2bc2c494867d2004e2240cc0e07c130`.
- Unity report: `c496f2a8991a3f04930c4dca1182be58d0770beaea85b1b6a2678718ffad4385`.
- 128 files trong handoff đều read-only; index quản lý 125 files.

Raw capture chỉ được giữ local trong worktree/handoff; không commit raw images.

## Novelty audit

Primary grouping tạo sáu scout-connected components:

- Năm component spatially novel.
- Một component gồm routes 03/04/07 nối với historical case `r4p1_hard_02`, nên không spatially novel.

Conservative grouping cũng có sáu component, trong đó năm novel và một nối với lịch sử.

Sau khi so visual RMS128 với toàn bộ 1.120 historical frames và yêu cầu pairwise separation, có năm credible separated spatial+visual components. Cả năm đều negative:

- Positive component: 0.
- Negative component: 5.
- Hard-negative component trong credible separated set: 0.
- Union: 5, thấp hơn yêu cầu 6.

Development route không được chấp nhận: tám supporting record đều đến từ route 00 và chỉ đại diện một negative component. Vì vậy route không chứng minh được con đường bổ sung cả positive lẫn negative support.

Verdict artifact: `g2_r5_a_independent_verdict.json`, SHA-256 `66cabf0698e62a418fdf33b788154ae67762a65a6f3699a81391f76c8a17d40f`.

## Cross-review và sửa lỗi

Agent B phát hiện target-label leakage trong một commit trung gian: action từng bị rollback bằng privileged `t7LevelAcceptable`. Run liên quan fail trước capture. Logic đó đã bị xóa hoàn toàn; action cuối chỉ phụ thuộc safe actuator/contact/workspace. Regression test cấm `t7LevelAcceptable`, `BuildActive2DPrivilegedFrame` và `ScoreCandidate` trong execution path.

Agent A phát hiện auditor trung gian có thể chọn off-T7 frame làm visual representative rồi gián tiếp hỗ trợ component T7. Auditor đã hủy lần audit đang chạy trước verdict, sau đó tách decision path chỉ dùng T7+contact+workspace-valid records. Toàn bộ 96 record vẫn được giữ trong integrity/descriptive analysis.

Agent A duyệt auditor cuối `98d7cd2`; Agent B duyệt Unity target-independent source. Không còn finding mức blocking.

Tests cuối:

- Unity preflight: 39/39 PASS.
- Independent auditor: 16/16 PASS.

Sai lệch quy trình: patched RMS audit cuối mất khoảng 62 giây nhưng Agent B chạy trực tiếp thay vì CSSALT Local Jobs. Điều này không thay đổi dữ liệu hoặc verdict, nhưng được ghi nhận là process deviation.

## Git và tích hợp

Các commit/nhánh quan trọng đã push:

- Coordinator contracts: `0338b2d`, `b85e41e`, `93d0935`, `f02565e`, `30541cc`.
- Agent A pipeline: `2669c5e`; capture source `04bfadc`; offline finalizer `2156865`.
- Agent B final auditor: `98d7cd2`; compact verdict: `893f9f8`.

Các thay đổi đã được coordinator review và tích hợp trực tiếp vào branch phát triển được phê duyệt `codex/u08-phase2`. Không tạo PR riêng vì target chính là chính branch phát triển này. Không merge vào `main`, không force-push, không chạm các thay đổi Unity cũ không thuộc R5.

## Quyết định tiếp theo

Không chạy R5-B và không train recognition candidate từ batch này. Năm negative component mới là bằng chứng hữu ích về khả năng tạo negative diversity, nhưng dataset không có positive support nên chưa biện minh cho một recognition training study cân bằng.

Bước nghiên cứu tiếp theo cần được researcher duyệt riêng: sửa chiến lược generator theo hướng target-independent nhưng tăng xác suất đi qua acceptable basin, ví dụ geometry-only coverage quanh marker/skin strata với route hai chiều và yaw/lateral envelope rộng hơn, sau đó chạy một scout feasibility mới với budget mới. Không được dùng Unity target labels để điều khiển action và không được tự động mở chiến dịch capture/training.

Giới hạn khoa học: mọi bằng chứng vẫn đến từ một anatomy Unity cố định; không có kết luận clinical, patient-specific hay unseen-anatomy generalization.
