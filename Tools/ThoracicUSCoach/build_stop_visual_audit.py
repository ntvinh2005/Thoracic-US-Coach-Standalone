from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from coach_data import pixel_features, save_json


def make_sheet(items, output: Path, feature_view: bool) -> None:
    tile = 256
    caption = 38
    columns = 4
    rows = (len(items) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * tile, rows * (tile + caption)), "white")
    draw = ImageDraw.Draw(sheet)
    for ordinal, (path, label) in enumerate(items):
        x = (ordinal % columns) * tile
        y = (ordinal // columns) * (tile + caption)
        if feature_view:
            values = pixel_features(path, 16).reshape(16, 16)
            display = np.clip((values + 3.0) / 6.0 * 255.0, 0, 255).astype(np.uint8)
            image = Image.fromarray(display, mode="L").resize((tile, tile), Image.Resampling.NEAREST).convert("RGB")
        else:
            with Image.open(path) as source:
                image = source.convert("RGB")
                if image.size != (tile, tile):
                    image = image.resize((tile, tile), Image.Resampling.BILINEAR)
        sheet.paste(image, (x, y))
        draw.text((x + 4, y + tile + 3), label[:48], fill="black")
        draw.text((x + 4, y + tile + 18), path.name[:48], fill="black")
    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)


def main() -> None:
    parser = argparse.ArgumentParser(description="Build raw and exact-16x16 STOP observability contact sheets")
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--live-image-dir", type=Path, required=True)
    parser.add_argument("--observability-audit", type=Path, required=True)
    parser.add_argument("--diagnostic-image-dir", type=Path)
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    items = []
    for path in sorted(args.live_image_dir.glob("*.png")):
        pose = "inside acceptable" if "_inside_" in path.name else (
            "immediately unacceptable" if "immediately-outside" in path.name else "far unacceptable"
        )
        items.append((path.resolve(), pose))
    audit = json.loads(args.observability_audit.read_text(encoding="utf-8"))
    for ordinal, pair in enumerate(audit["nearestOppositeLabelPairs"]["examples"][:3], start=1):
        items.append(((root / pair["acceptableImage"]).resolve(), f"nearest pair {ordinal}: acceptable"))
        items.append(((root / pair["unacceptableImage"]).resolve(), f"nearest pair {ordinal}: unacceptable"))
    if args.diagnostic_image_dir:
        premature = next(iter(sorted(args.diagnostic_image_dir.glob("B0*immediately-outside*.png"))), None)
        if premature is not None:
            items.append((premature.resolve(), "live premature STOP failure"))
        passage = next(iter(sorted(args.diagnostic_image_dir.glob("B1*step07.png"))), None)
        if passage is not None:
            items.append((passage.resolve(), "live failed trajectory late frame"))
    if not items:
        raise SystemExit("No diagnostic images found")
    output_dir = args.output_dir.resolve()
    raw_output = output_dir / "representative_raw_original_resolution.png"
    feature_output = output_dir / "representative_model_features_16x16.png"
    make_sheet(items, raw_output, feature_view=False)
    make_sheet(items, feature_output, feature_view=True)
    stats = []
    for path, label in items:
        with Image.open(path) as image:
            values = np.asarray(image.convert("L"), dtype=np.float32) / 255.0
        stats.append(
            {
                "label": label,
                "path": str(path),
                "width": image.width,
                "height": image.height,
                "mean": float(values.mean()),
                "std": float(values.std()),
                "p99": float(np.quantile(values, 0.99)),
                "max": float(values.max()),
                "fractionAbove0_03": float(np.mean(values > 0.03)),
            }
        )
    save_json(
        output_dir / "representative_image_stats.json",
        {
            "rawSheet": str(raw_output),
            "featureSheet": str(feature_output),
            "featureDefinition": "exact 16x16 bilinear grayscale per-image z-score, displayed with fixed [-3,3] range",
            "images": stats,
        },
    )
    print(json.dumps({"raw": str(raw_output), "features": str(feature_output), "count": len(items)}, indent=2))


if __name__ == "__main__":
    main()
