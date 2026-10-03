#!/usr/bin/env python3
"""Export a trained YOLOv8 model to ONNX for MoonHarvest.

Usage:
    python export_onnx.py [run_dir_or_pt] [--imgsz N] [--out DIR]

If no argument is given, the most recently modified `best.pt` under
`training/runs/**/weights/` is used. The previous version hardcoded
`runs/crop_weed_v1`, a directory that does not exist in this repo, so the
script always exited with an error.
"""
from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

TRAINING_DIR = Path(__file__).resolve().parent
RUNS_DIR = TRAINING_DIR / "runs"
ASSETS_MODELS = TRAINING_DIR.parent / "HarvestmoonGCS" / "Assets" / "models"


def find_latest_best_pt() -> Path | None:
    """Return the most recently modified best.pt under training/runs."""
    candidates = list(RUNS_DIR.glob("**/weights/best.pt"))
    if not candidates:
        return None
    return max(candidates, key=lambda p: p.stat().st_mtime)


def resolve_pt(target: str | None) -> Path | None:
    if not target:
        return find_latest_best_pt()

    path = Path(target).expanduser()
    if path.is_file() and path.suffix == ".pt":
        return path
    if path.is_dir():
        best = path / "weights" / "best.pt"
        if best.exists():
            return best
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Export YOLOv8 .pt to ONNX")
    parser.add_argument("target", nargs="?", default=None,
                        help="Run directory or .pt file. Defaults to the newest best.pt under training/runs.")
    parser.add_argument("--imgsz", type=int, default=416,
                        help="Input image size. Use 416 to match the health classifier, 640 for the UAV detector.")
    parser.add_argument("--opset", type=int, default=12)
    parser.add_argument("--out", default=None,
                        help="Optional directory to copy the exported .onnx into.")
    parser.add_argument("--no-simplify", action="store_true",
                        help="Disable ONNX graph simplification.")
    args = parser.parse_args()

    best_pt = resolve_pt(args.target)
    if best_pt is None or not best_pt.exists():
        print(f"ERROR: no usable best.pt found (target={args.target!r}, searched {RUNS_DIR})")
        return 1

    try:
        from ultralytics import YOLO
    except ImportError as exc:
        print(f"ERROR: ultralytics is not installed in this interpreter: {exc}")
        return 1

    print(f"Exporting {best_pt}  imgsz={args.imgsz}  opset={args.opset}")
    model = YOLO(str(best_pt))
    export_path = model.export(
        format="onnx",
        imgsz=args.imgsz,
        opset=args.opset,
        simplify=not args.no_simplify,
    )
    print("Exported ONNX:", export_path)

    if args.out:
        out_dir = Path(args.out).expanduser()
        out_dir.mkdir(parents=True, exist_ok=True)
        dest = out_dir / Path(export_path).name
        shutil.copy2(export_path, dest)
        print("Copied to:", dest)
    elif ASSETS_MODELS.is_dir():
        # Convenience hint only — never overwrite a shipped model automatically.
        print(f"Hint: copy the .onnx into {ASSETS_MODELS} and update the matching classes-*.txt.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
