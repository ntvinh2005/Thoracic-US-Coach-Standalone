# Phase 2 Final Sprint — Prompt 2/3

## Kết luận

**Outcome C — SCIENTIFIC / FUNCTIONAL NO-GO**

Gate độc lập cuối cùng là `IMAGE_POSE_FRESHNESS_FAIL`. Snapshot thất bại đã được đóng gói bất biến và kiểm tra integrity thành công, nhưng budget F3 không tạo được một action factual `incomplete` với `actionAccepted=true && actionCompleted=false`. Bốn action bị reject và rollback không được phép thay thế category này. `retryAuthorized=false`; Stage B/C/D, training và Unity POC đều dừng.

## 1. Source, branch và hai worker

- Development branch: `codex/u08-phase2`; evidence đã tích hợp đến commit `baaa575` trước báo cáo tổng hợp này.
- Unity/capture source: `c93c18c6af9e9140ed9722cdd4a85bb8470be094` trên `codex/p2-unity-final`.
- Worker A sửa Unity freshness, batch failure handling, chạy capture, đóng gói failed-run immutable.
- Worker B đóng băng protocol/auditor, cross-review trước capture và công bố verdict độc lập.

## 2. Freshness bug và bản sửa

`CaptureCleanImage()` chỉ render/readback texture đã tồn tại. Pixel ultrasound thật được tạo bởi `UltrasoundManager.Update/Scan/Texture2D.Apply`; đặt pose trong coroutine rồi capture ngay có thể ghép anatomy label mới với pixel pose cũ.

Bản sửa:

- gắn pose epoch, Unity frame và producer scan serial;
- đồng bộ physics rồi gọi explicit producer publication;
- giữ lại hai warmup dưới dạng discard và chỉ nhận hai ảnh fresh ở hai Unity frame sau;
- fail closed nếu source/manifest/protocol/hash hoặc producer handshake sai;
- dùng đúng path `DIRECT_PRODUCER_CAMERA` vì temporal aggregator trong scene POC đang inactive;
- flatten coroutine lồng nhau để exception được ghi vào report và batch thoát nonzero;
- thêm watchdog 1800 giây và output version mới, không ghi đè R6/v1.

## 3. Test trước/sau

- Preflight ban đầu: jobs `3e70f0345559` và `f045ff433d82` fail khi hoàn thiện source contract; `1a799cfc30e1` pass 5/5.
- Direct-producer preflight: `fd5fbf3097c6` fail 5/6 do assertion source cũ; `3c0d64e92546` và `16f1f4ae7c43` pass 6/6.
- Auditor cuối: 18/18 failed-run tests pass; toàn bộ Prompt 2 Python suite đạt 35 tests ở worker B trước report.
- Job `e2e0ec8c236f` fail trước Unity vì PowerShell thiếu `Get-FileHash`; helper được thay bằng .NET SHA256.
- Job `fdc30d317c0f` mất wrapper do external `0xC000013A`; Unity con còn chạy. Evidence v1 này không được promote.
- Watcher `3657647a40ce` chỉ theo PID sống sót; không tạo scientific result.

## 4. Positive-control freshness

Run direct-v2 thực hiện đúng chuỗi A→B→A→C→D. Audit độc lập xác nhận cả bốn control families pass: pose restoration, T7/contact/workspace, evaluator positivity, producer epoch/frame/serial, hai ảnh fresh ổn định và transition freshness.

Các movement category lateral, longitudinal, rotation, rejected, rollback và observe cũng pass. Action facts cuối: `rejected=4`, `rollback=4`, `accepted-but-incomplete=0`.

## 5. Corrected basin mapping

`NOT_RUN`. Audit/protocol Stage B đã được chuẩn bị trước outcome, gồm exact `(0,0,0)` anchors, grid symmetric, tối đa 320 candidates và connected strict-positive component. F3 fail nên không có quyền chạy mapping.

## 6. Safe target-independent reachability

`NOT_RUN`. Stage C bị khóa bởi Stage F. Không dùng lại oracle G1 hay old R6 candidate images để thay thế evidence mới.

## 7. Development data và support

Không có dataset Stage D mới. Engineering readiness và scientific readiness đều `false/not evaluated`; không có positive/negative group count mới để claim.

## 8. Model và Unity POC

Không training, calibration, locked/confirmation evaluation hoặc model promotion. Không chạy Stage P episodes. Checkpoint diagnostic chuẩn bị trước vẫn `BLOCKED/NON_DEPLOYABLE` và không được load qua production loader.

## 9. Immutable failed-run evidence

- Handoff: `D:/CSSALT/Thoracic-US-Coach-P2-Handoff/freshness/p2_freshness_direct_v2_20261026_failed_run_v2`
- Handoff seal SHA-256: `8997eadb08554cc1bd9dbcba5098c6be401897284da0623fa8fd750ce98e0d2c`
- Integrity: 105/105 indexed files pass hash và size; handoff read-only.
- Manifest SHA-256: `de9c833e2f660da21b5e89e9e818239dcef0f82a843783eec9ae7566845307b7`
- Capture protocol SHA-256: `321c042c512f57f4a3c0b4404686f9bae9c7bd718954476a3eb0afc90424c34d`
- Audit protocol canonical SHA-256: `588c32e6b84bf70789ffa039806d7b70b4bfc3c6b301674ce3bc58e94ea83f24`
- Independent audit JSON SHA-256: `416cad291440c824f20d4ab0b4fed1e49d16c34f19194e31e6cbb18f63580a69`
- Independent report SHA-256: `584d2e892ee52f1f89a876f8f162cb80734b87e724fd9882badee92f0d3059f5`

Finalize-only job `4e06925b9770` fail trước seal do PowerShell list serialization; staging được giữ. Fix job `1a1b72357eee` hoàn tất, không mở Unity hay recapture.

## 10. Frozen integrity và blocker còn lại

- Evaluator floor giữ nguyên `0.5912307500839233`.
- Phase 1 giữ nguyên `36/40` precise T7.
- Frozen spatial-v2 STOP threshold giữ nguyên `0.93`.
- Old R6 evidence giữ immutable/diagnostic-only.
- Historical locked families và reserved confirmation không được mở.
- Không merge `main`, không force-push, không commit raw image dataset.

Blocker chính xác: actuator hiện tại trong frozen budget chuyển từ full completed 3 mm actions sang rejected+rollback tại workspace boundary; không phát sinh accepted-but-incomplete factual action. Theo protocol đã đóng băng, đây là mandatory F3 failure và không được sửa nhãn, thay gate hoặc chạy thêm campaign để ép PASS.

**READY FOR PROMPT 3 — FINAL PHASE 2 CLOSEOUT**
