from __future__ import annotations

import hashlib
import json
import math
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def image_rms(left: Path, right: Path, size: int = 128) -> float:
    def load(path: Path) -> np.ndarray:
        with Image.open(path) as image:
            return np.asarray(image.convert("L").resize((size, size), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0
    delta = load(left) - load(right)
    return float(np.sqrt(np.mean(delta * delta)))


def vector_distance(left: dict[str, float], right: dict[str, float], names: tuple[str, ...]) -> float:
    return math.sqrt(sum((float(left[name]) - float(right[name])) ** 2 for name in names))


def repeat_render_audit(project_root: Path) -> dict[str, Any]:
    capture = project_root / "Artifacts/ThoracicUSCoach/active2d/g2_temporal_capture"
    rows = []
    for stream_path in sorted(capture.glob("*/*/stream_manifest.json")):
        stream = json.loads(stream_path.read_text(encoding="utf-8"))
        annotation_path = stream_path.with_name("privileged_annotations.json")
        annotations = json.loads(annotation_path.read_text(encoding="utf-8"))
        labels = {int(item["index"]): item for item in annotations["frames"]}
        frames = sorted(stream["frames"], key=lambda item: int(item["index"]))
        for previous, current in zip(frames, frames[1:]):
            a, b = labels[int(previous["index"])], labels[int(current["index"])]
            position = vector_distance(a["probePosition"], b["probePosition"], ("x", "y", "z"))
            rotation = vector_distance(a["probeRotation"], b["probeRotation"], ("x", "y", "z", "w"))
            if position > 1e-4 or rotation > 1e-6:
                continue
            left = project_root / str(previous["image"]).replace("\\", "/")
            right = project_root / str(current["image"]).replace("\\", "/")
            rows.append({
                "family": stream["family"], "firstIndex": int(previous["index"]), "secondIndex": int(current["index"]),
                "imageRms128": image_rms(left, right),
                "scoreDelta": abs(float(a["finalTargetScore"]) - float(b["finalTargetScore"])),
                "labelChanged": bool(a["finalTargetAcceptable"]) != bool(b["finalTargetAcceptable"]),
            })
    rms = [row["imageRms128"] for row in rows]
    score = [row["scoreDelta"] for row in rows]
    return {
        "source": "existing G2 temporal capture; diagnostic only",
        "samePoseConsecutivePairs": len(rows),
        "labelChanges": sum(row["labelChanged"] for row in rows),
        "imageRms128": {"min": min(rms) if rms else None, "median": float(np.median(rms)) if rms else None, "max": max(rms) if rms else None},
        "scoreDelta": {"min": min(score) if score else None, "median": float(np.median(score)) if score else None, "max": max(score) if score else None},
        "examples": rows[:24],
    }


def percent(value: float) -> str:
    return f"{100.0 * value:.1f}%".replace(".", ",")


def main() -> None:
    project_root = Path(__file__).resolve().parents[2]
    output = project_root / "Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r2_20261024"
    results_path = output / "g2_r2_results.json"
    results = json.loads(results_path.read_text(encoding="utf-8"))
    repeats = repeat_render_audit(project_root)
    (output / "g2_r2_repeat_render_audit.json").write_text(json.dumps(repeats, indent=2) + "\n", encoding="utf-8")
    matrix = {
        "schemaVersion": 1,
        "pythonExecutable": "C:/Users/Vinh/anaconda3/python.exe",
        "tests": [
            {"name": "Python unittest discovery", "result": "PASS", "passed": 75, "failed": 0},
            {"name": "Python pytest", "result": "PASS", "passed": 97, "failed": 0},
            {"name": "G2-R2 causal/offline-live feature parity", "result": "PASS"},
            {"name": "Blocked checkpoint live-load", "result": "PASS", "detail": "rejected: g2-r2-independent-family-gate-failed"},
            {"name": "Unity EditMode", "result": "NOT_RUN", "detail": "exclusive-access safety review blocked launch while Unity processes were active"},
            {"name": "Unity closed-loop pilot", "result": "NOT_RUN", "detail": "offline scientific gate failed"},
        ],
    }
    (output / "g2_r2_test_matrix.json").write_text(json.dumps(matrix, indent=2) + "\n", encoding="utf-8")

    candidates = results["candidateResults"]
    candidate_lines = []
    for candidate in candidates:
        metrics = candidate["calibration"]
        classification = metrics["classification"]
        candidate_lines.append(
            f"| `{candidate['extractor']}` | {candidate['seed']} | "
            f"{classification['truePositive']}/{classification['positiveSupport']} = {percent(classification['recall'])} | "
            f"{classification['falsePositive']} | {metrics['qualitySpearman']:.4f} | "
            f"{metrics['medianEpisodeBestViewRegret']:.5f} | {'PASS' if candidate['gatePass'] else 'FAIL'} |"
        )
    baseline = results["baseline"]["calibration"]
    selected = results["selected"]["calibration"]
    missed = [
        family for family, row in selected["familyResults"].items()
        if row["positiveFrames"] > 0 and not row["targetRecognized"]
    ]
    resolution = results["observability"]["resolutions"]
    source_files = [
        project_root / "Tools/ThoracicUSCoach/g2_r2_features.py",
        project_root / "Tools/ThoracicUSCoach/prepare_g2_r2_manifest.py",
        project_root / "Tools/ThoracicUSCoach/run_g2_r2_experiment.py",
        project_root / "Tools/ThoracicUSCoach/active2d_stop.py",
        project_root / "Tools/ThoracicUSCoach/run_active2d_policy_server.py",
    ]
    source_hashes = {path.relative_to(project_root).as_posix(): sha256(path) for path in source_files}
    report = f"""# G2-R2 — Báo cáo nhận diện mục tiêu thị giác Phase 2

Ngày: 2026-10-24

Branch: `codex/u08-phase2`

Base commit: `{results['baseCommit']}`

Kết luận: **PYTHON ENGINEERING PASS / UNITY REGRESSION NOT RUN / SCIENTIFIC BLOCKED**

## 1. Kết luận ngắn

G2-R2 chưa tạo được bộ nhận diện STOP đủ điều kiện triển khai. Cấu hình tốt nhất theo protocol là `structural-v4`; ở seed 29 và 47 nó nhận đúng 5/7 positive frame ({percent(5/7)}) trên 6 positive family độc lập, với 0 false positive. Kết quả này bằng recall của frozen spatial-v2 và vẫn thấp hơn gate 80%.

Hai mô hình causal temporal h1/h4 giữ chất lượng xếp hạng rất tốt nhưng nhận diện nhị phân không chuyển giao: recall chỉ 0–1/7 ở các seed đã chạy. Không cấu hình nào pass ổn định cả ba seed. Locked test vì vậy vẫn SEALED; Unity closed-loop pilot không chạy.

## 2. Nguyên nhân G2-R1 thất bại

### Phát hiện đã xác nhận

- Downsample 16×16 làm mất đáng kể cấu trúc: median RMS tới hard negative gần nhất tăng từ `{resolution['16']['nearestOppositeRmsMedian']:.6f}` ở 16×16 lên `{resolution['128']['nearestOppositeRmsMedian']:.6f}` ở 128×128.
- Tuy nhiên, tăng cấu trúc ảnh không đủ giải quyết bài toán: structural-v4 vẫn chỉ đạt tối đa 5/7 recall ở zero FP.
- Nhãn evaluator nhất quán với frozen score floor `0.5912307500839233`: 0 mismatch trên toàn bộ train+calibration đã đọc. Có {results['labelAudit']['nearBoundaryFrames']} frame nằm trong ±0,005 quanh biên và {results['labelAudit']['neighborLabelTransitions']} lần đổi nhãn giữa frame kề nhau, nên bài toán nhị phân rất nhạy gần threshold.
- Cặp opposite-label vẫn gần nhau ở ảnh gốc: với hard negative được chọn ở 128×128, median RMS tại 1024×1024 là `{resolution['original1024_for_128_nearest']['nearestOppositeRmsMedian']:.6f}`.
- Evaluator score không chỉ dùng pixel; nó còn dùng anatomy sampling và target/scan-plane geometry. Các thành phần privileged này chỉ làm label/evaluation và không đi vào actor. Do đó một phần label boundary không hoàn toàn quan sát được từ một frame sạch.
- Temporal history hiện tại giúp quality ranking nhưng không giúp recognition qua family. Đây là bằng chứng chống lại giả thuyết “chỉ cần history dài hơn trên dữ liệu raster cũ”.

### Renderer/label stability

Trong evidence repeat-render hiện có: {repeats['samePoseConsecutivePairs']} cặp frame liên tiếp cùng pose, {repeats['labelChanges']} lần đổi label; median image RMS 128 là `{repeats['imageRms128']['median'] if repeats['imageRms128']['median'] is not None else 'n/a'}` và median score delta là `{repeats['scoreDelta']['median'] if repeats['scoreDelta']['median'] is not None else 'n/a'}`. Đây là diagnostic cũ, không được coi là heldout mới.

Đánh giá root cause: **representation loss là một yếu tố có thật, nhưng nguyên nhân còn lại là sparse positive coverage và partial observability của score boundary từ ảnh sạch**. Chưa đủ bằng chứng để gọi toàn bộ bài toán `NOT_OBSERVABLE`; kết luận đúng hiện tại là nhận diện hiện có chưa generalize qua pose family.

## 3. Dữ liệu và provenance

- Source: 100 family / 12.100 frame của `active2d_phase2_raster_v2_20261023`; ảnh 1024×1024 được đọc tại chỗ, không copy.
- Train: {results['support']['train']['families']} family, {results['support']['train']['frames']} frame, {results['support']['train']['positiveFrames']} positive frame trong {results['support']['train']['positiveFamilies']} family.
- Calibration mới: {results['support']['calibration']['families']} family, {results['support']['calibration']['frames']} frame, {results['support']['calibration']['positiveFrames']} positive frame trong {results['support']['calibration']['positiveFamilies']} family.
- Sáu positive calibration family chưa được dùng trong G2-R1 development: `train_0055`, `train_0076`, `train_0090`, `dev_0073`, `test_0079`, `test_0094`.
- Sáu historical locked-test family giữ nguyên và không được loader mở.
- Dataset fingerprint: `{results['datasetFingerprint']}`.
- Manifest hash: `{results['manifestSha256']}`.

Chi tiết từng family và prior usage nằm trong `family_usage_ledger.json`.

## 4. So sánh model trên calibration độc lập

Frozen spatial-v2 @0.93: recall {baseline['classification']['truePositive']}/{baseline['classification']['positiveSupport']} = {percent(baseline['classification']['recall'])}, FP={baseline['classification']['falsePositive']}, Spearman={baseline['qualitySpearman']:.4f}, median regret={baseline['medianEpisodeBestViewRegret']:.5f}.

| Model | Seed | Recall | FP | Quality Spearman | Median regret | Gate |
|---|---:|---:|---:|---:|---:|---|
{chr(10).join(candidate_lines)}

Positive family bị miss bởi selected seed 13: `{', '.join(missed)}`. Seed 29/47 tăng lên 5/7 nhưng vẫn dưới 80%; không seed nào được phép deploy.

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
| Calibration recall ≥80% | FAIL — tốt nhất 5/7 = {percent(5/7)} |
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
{json.dumps(source_hashes, indent=2)}
```

Không có claim về ultrasound thật, lâm sàng hoặc anatomy/patient chưa thấy.
"""
    (output / "G2_R2_REPORT_VI.md").write_text(report, encoding="utf-8")
    print(json.dumps({"report": str(output / "G2_R2_REPORT_VI.md"), "repeatPairs": repeats["samePoseConsecutivePairs"]}, indent=2))


if __name__ == "__main__":
    main()
