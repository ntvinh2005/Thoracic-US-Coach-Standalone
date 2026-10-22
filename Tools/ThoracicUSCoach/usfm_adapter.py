from __future__ import annotations

import argparse
import hashlib
import json
import logging
import sys
import types
from pathlib import Path
from types import SimpleNamespace

import numpy as np
from PIL import Image

from coach_data import DEFAULT_EXPERIMENT_ID, dataset_fingerprint, image_sha256, load_frames, project_root


IMAGENET_MEAN = np.asarray([0.485, 0.456, 0.406], dtype=np.float32)
IMAGENET_STD = np.asarray([0.229, 0.224, 0.225], dtype=np.float32)


def require_torch():
    try:
        import torch
    except ImportError as error:
        raise SystemExit("PyTorch is required for USFM verification/extraction") from error
    return torch


def install_timm_layers_compat(torch) -> None:
    """Provide only the three timm layer helpers imported by official USFM; avoids installing full timm/torchvision."""
    try:
        import timm.models.layers  # noqa: F401
        return
    except ImportError:
        pass

    class DropPath(torch.nn.Module):
        def __init__(self, drop_prob: float = 0.0):
            super().__init__()
            self.drop_prob = drop_prob

        def forward(self, value):
            if self.drop_prob == 0.0 or not self.training:
                return value
            keep = 1.0 - self.drop_prob
            shape = (value.shape[0],) + (1,) * (value.ndim - 1)
            random = keep + torch.rand(shape, dtype=value.dtype, device=value.device)
            return value.div(keep) * random.floor()

    layers = types.ModuleType("timm.models.layers")
    layers.DropPath = DropPath
    layers.to_2tuple = lambda value: value if isinstance(value, tuple) else (value, value)
    layers.trunc_normal_ = torch.nn.init.trunc_normal_
    models = types.ModuleType("timm.models")
    models.layers = layers
    timm = types.ModuleType("timm")
    timm.models = models
    sys.modules["timm"] = timm
    sys.modules["timm.models"] = models
    sys.modules["timm.models.layers"] = layers


def build_and_load(official_repo: Path, checkpoint: Path, device: str):
    torch = require_torch()
    install_timm_layers_compat(torch)
    sys.path.insert(0, str(official_repo.resolve()))
    from usdsgen.modules.backbone.vision_transformer import VisionTransformer
    from usdsgen.utils.modelutils import remap_pretrained_keys_vit
    from functools import partial

    model = VisionTransformer(
        img_size=224,
        patch_size=16,
        in_chans=3,
        num_classes=0,
        embed_dim=768,
        depth=12,
        num_heads=12,
        mlp_ratio=4,
        qkv_bias=True,
        attn_drop_rate=0.0,
        drop_path_rate=0.1,
        init_values=0.1,
        use_abs_pos_emb=False,
        use_rel_pos_bias=True,
        use_shared_rel_pos_bias=False,
        use_mean_pooling=True,
        norm_layer=partial(torch.nn.LayerNorm, eps=1e-6),
    )
    payload = torch.load(checkpoint, map_location="cpu", weights_only=False)
    if isinstance(payload, dict) and "model" in payload and isinstance(payload["model"], dict):
        payload = payload["model"]
    if isinstance(payload, dict) and "state_dict" in payload and isinstance(payload["state_dict"], dict):
        payload = payload["state_dict"]
    if not isinstance(payload, dict):
        raise RuntimeError("USFM checkpoint is not a state dictionary")

    normalized = {}
    for key, value in payload.items():
        for prefix in ("module.", "model.", "backbone."):
            if key.startswith(prefix):
                key = key[len(prefix):]
        normalized[key] = value
    normalized = remap_pretrained_keys_vit(model, normalized, logging.getLogger("usfm"))
    model_state = model.state_dict()
    matched = {key: value for key, value in normalized.items() if key in model_state and model_state[key].shape == value.shape}
    backbone_keys = [key for key in model_state if not key.startswith("head.")]
    ratio = len(matched) / max(1, len(backbone_keys))
    if ratio < 0.80:
        raise RuntimeError(f"Only {len(matched)}/{len(backbone_keys)} backbone tensors match ({ratio:.1%}); refusing false success")
    incompatibility = model.load_state_dict(matched, strict=False)
    model.eval().requires_grad_(False).to(device)
    report = {
        "officialRepo": str(official_repo.resolve()),
        "checkpoint": str(checkpoint.resolve()),
        "matchedTensors": len(matched),
        "backboneTensors": len(backbone_keys),
        "matchRatio": ratio,
        "missingKeys": list(incompatibility.missing_keys),
        "unexpectedKeys": list(incompatibility.unexpected_keys),
        "encoderFrozen": True,
        "device": device,
    }
    return model, report


def preprocess(path: Path, device: str):
    torch = require_torch()
    with Image.open(path) as image:
        image = image.convert("RGB").resize((224, 224), Image.Resampling.BICUBIC)
        array = np.asarray(image, dtype=np.float32) / 255.0
    tensor = torch.from_numpy(array).permute(2, 0, 1)
    mean = torch.tensor(IMAGENET_MEAN).view(3, 1, 1)
    std = torch.tensor(IMAGENET_STD).view(3, 1, 1)
    return ((tensor - mean) / std).to(device)


def main() -> None:
    parser = argparse.ArgumentParser(description="Thin adapter around the official frozen USFM ViT-B/16 code")
    parser.add_argument("command", choices=("verify", "extract-dataset"))
    parser.add_argument("--experiment-id", default=DEFAULT_EXPERIMENT_ID)
    parser.add_argument("--official-repo", type=Path, default=project_root() / "Library/USFM-official")
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--device", default="cuda")
    parser.add_argument("--output", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/usfm_features.npz")
    parser.add_argument("--load-report", type=Path, default=project_root() / "Artifacts/ThoracicUSCoach/usfm/usfm_load_report.json")
    parser.add_argument("--max-sweeps-per-split", type=int, default=0, help="0 keeps every captured sweep")
    parser.add_argument("--batch-size", type=int, default=8, help="Encoder batch size; 8 is conservative for CPU RAM")
    args = parser.parse_args()

    torch = require_torch()
    if args.device == "cuda" and not torch.cuda.is_available():
        args.device = "cpu"
    model, report = build_and_load(args.official_repo, args.checkpoint, args.device)
    report["checkpointSha256"] = hashlib.sha256(args.checkpoint.read_bytes()).hexdigest()
    report["preprocessing"] = "RGB 224x224 bicubic; ImageNet mean/std"
    args.load_report.parent.mkdir(parents=True, exist_ok=True)
    if args.command == "verify":
        args.load_report.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(json.dumps(report, indent=2))
        return

    frames = load_frames(experiment_id=args.experiment_id)
    if args.max_sweeps_per_split > 0:
        selected_sweeps: set[str] = set()
        for split in ("train", "dev", "test"):
            selected_sweeps.update(sorted({frame.sweep_id for frame in frames if frame.split == split})[: args.max_sweeps_per_split])
        frames = [frame for frame in frames if frame.sweep_id in selected_sweeps]
    if not frames:
        raise SystemExit("No captured frames found")
    features: list[np.ndarray] = []
    paths: list[str] = []
    with torch.inference_mode():
        batch_size = max(1, args.batch_size)
        for start in range(0, len(frames), batch_size):
            batch_frames = frames[start:start + batch_size]
            batch = torch.stack([preprocess(frame.image, args.device) for frame in batch_frames])
            embeddings = model.forward_features(batch).cpu().numpy().astype(np.float32)
            features.extend(embeddings)
            paths.extend(frame.image_relative for frame in batch_frames)
            completed = start + len(batch_frames)
            if completed % 32 == 0 or completed == len(frames):
                print(f"USFM embeddings {completed}/{len(frames)}", flush=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    fingerprint = dataset_fingerprint(frames)
    hashes = [image_sha256(frame.image) for frame in frames]
    np.savez_compressed(
        args.output,
        image_paths=np.asarray(paths),
        image_hashes=np.asarray(hashes),
        features=np.stack(features),
        experiment_id=np.asarray(args.experiment_id),
        dataset_fingerprint=np.asarray(fingerprint),
        preprocessing=np.asarray(report["preprocessing"]),
        checkpoint_sha256=np.asarray(report["checkpointSha256"]),
    )
    report["experimentId"] = args.experiment_id
    report["datasetFingerprint"] = fingerprint
    report["frames"] = len(frames)
    report["featureShape"] = list(np.stack(features).shape)
    args.load_report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"features": str(args.output), "loadReport": str(args.load_report), **report}, indent=2))


if __name__ == "__main__":
    main()
