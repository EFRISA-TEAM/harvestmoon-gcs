#!/usr/bin/env python3
"""
train_hsv_det.py — Train YOLOv8n to reproduce HSV segmentation bounding boxes.

Distillation: HSV pipeline pseudo-labels → YOLO detection model.
Uses hsv_det_4cls dataset (2594 images from all videos).

Usage:
  python train_hsv_det.py              # training penuh
  python train_hsv_det.py --epochs 50  # training singkat
  python train_hsv_det.py --finetune   # fine-tune dari best.pt
"""

import argparse, os, sys
from pathlib import Path
import torch
from ultralytics import YOLO

TRAINING_DIR = Path(__file__).parent
DATA_YAML    = TRAINING_DIR / "datasets/moonharvest-uav/hsv_det_4cls/data.yaml"
PRETRAIN_PT  = TRAINING_DIR / "yolov8n.pt"
OUTPUT_NAME  = "moonharvest_hsv_det_v1"
# TEKNOFEST_SIAP sits next to Pigeon_Harvest/; override with MOONHARVEST_EXPORT_DIR.
EXPORT_DIR   = Path(os.environ.get(
    "MOONHARVEST_EXPORT_DIR",
    TRAINING_DIR.parent.parent / "TEKNOFEST_SIAP" / "model",
))

TRAIN_ARGS = dict(
    data        = str(DATA_YAML),
    imgsz       = 640,
    batch       = 16,
    epochs      = 100,
    device      = 0,
    workers     = 4,
    amp         = True,
    cache       = "ram",
    optimizer   = "AdamW",
    lr0         = 0.01,
    lrf         = 0.01,
    momentum    = 0.937,
    weight_decay = 0.0005,
    warmup_epochs = 3.0,
    warmup_momentum = 0.8,
    box         = 7.5,
    cls         = 0.5,
    dfl         = 1.5,
    hsv_h       = 0.015,
    hsv_s       = 0.5,
    hsv_v       = 0.3,
    degrees     = 10.0,
    translate   = 0.1,
    scale       = 0.4,
    flipud      = 0.3,
    fliplr      = 0.3,
    mosaic      = 1.0,
    mixup       = 0.1,
    patience    = 30,
    save_period = 10,
    val         = True,
    plots       = True,
    verbose     = True,
    project     = str(TRAINING_DIR / "runs"),
    name        = OUTPUT_NAME,
    exist_ok    = True,
)

FINETUNE_ARGS = dict(
    data        = str(DATA_YAML),
    imgsz       = 640,
    batch       = 16,
    epochs      = 50,
    device      = 0,
    workers     = 4,
    amp         = True,
    cache       = "ram",
    optimizer   = "AdamW",
    lr0         = 0.0005,
    lrf         = 0.01,
    momentum    = 0.937,
    weight_decay = 0.0005,
    warmup_epochs = 1.0,
    warmup_momentum = 0.8,
    box         = 5.0,
    cls         = 1.0,
    dfl         = 1.5,
    hsv_h       = 0.01,
    hsv_s       = 0.3,
    hsv_v       = 0.2,
    degrees     = 15.0,
    translate   = 0.15,
    scale       = 0.5,
    flipud      = 0.5,
    fliplr      = 0.5,
    mosaic      = 1.0,
    mixup       = 0.15,
    patience    = 20,
    save_period = 10,
    val         = True,
    plots       = True,
    verbose     = True,
    project     = str(TRAINING_DIR / "runs"),
    name        = OUTPUT_NAME + "_finetune",
    exist_ok    = True,
)


def check_gpu():
    if not torch.cuda.is_available():
        print("CUDA tidak tersedia!")
        return False
    gpu = torch.cuda.get_device_properties(0)
    vram = gpu.total_memory / 1024**3
    vram_free = torch.cuda.mem_get_info()[0] / 1024**3
    print(f"GPU: {gpu.name}, VRAM: {vram:.1f}GB ({vram_free:.1f}GB free)")
    return True


def export_onnx(run_dir):
    best_pt = run_dir / "weights" / "best.pt"
    if not best_pt.exists():
        return
    print(f"Export {best_pt} → ONNX...")
    model = YOLO(str(best_pt))
    model.export(format="onnx", imgsz=640, opset=17, simplify=True)
    onnx_src = best_pt.parent / "best.onnx"
    if onnx_src.exists():
        EXPORT_DIR.mkdir(parents=True, exist_ok=True)
        dst = EXPORT_DIR / "moonharvest-hsv-det.onnx"
        import shutil
        shutil.copy2(onnx_src, dst)
        print(f"ONNX → {dst}")


def main():
    parser = argparse.ArgumentParser(description="Train YOLOv8n on HSV pseudo-labels")
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--finetune", action="store_true")
    parser.add_argument("--no-amp", action="store_true")
    parser.add_argument("--export", action="store_true")
    args = parser.parse_args()

    print("=" * 60)
    print("MoonHarvest HSV Detection Training")
    print("=" * 60)
    check_gpu()
    print(f"Dataset   : {DATA_YAML}")
    print(f"Pre-trained: {PRETRAIN_PT}")
    print("=" * 60)

    if not DATA_YAML.exists():
        print(f"ERROR: {DATA_YAML} tidak ditemukan!")
        return

    if args.finetune:
        best_pt = TRAINING_DIR / "runs" / OUTPUT_NAME / "weights" / "best.pt"
        if not best_pt.exists():
            print("ERROR: best.pt tidak ditemukan!")
            return
        FINETUNE_ARGS["epochs"] = args.epochs if args.epochs != 100 else 50
        FINETUNE_ARGS["batch"] = args.batch
        FINETUNE_ARGS["imgsz"] = args.imgsz
        FINETUNE_ARGS["amp"] = not args.no_amp
        model = YOLO(str(best_pt))
        model.train(**FINETUNE_ARGS)
    elif args.resume:
        last_pt = TRAINING_DIR / "runs" / OUTPUT_NAME / "weights" / "last.pt"
        if not last_pt.exists():
            print("ERROR: last.pt tidak ditemukan!")
            return
        model = YOLO(str(last_pt))
        model.train(resume=True)
    else:
        TRAIN_ARGS["epochs"] = args.epochs
        TRAIN_ARGS["batch"] = args.batch
        TRAIN_ARGS["imgsz"] = args.imgsz
        TRAIN_ARGS["amp"] = not args.no_amp
        model = YOLO(str(PRETRAIN_PT))
        model.train(**TRAIN_ARGS)

    run_dir = TRAINING_DIR / "runs" / OUTPUT_NAME
    print(f"\nTraining selesai!")
    print(f"Best weights: {run_dir}/weights/best.pt")
    if args.export:
        export_onnx(run_dir)


if __name__ == "__main__":
    main()
