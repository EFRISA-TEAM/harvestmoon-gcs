#!/usr/bin/env python3
"""
MoonHarvest YOLOv8n Training v6
- Dataset: 1500 gambar/kelas (vegetasi-aware crops, balanced)
- Full 150 epochs, patience=0
- imgsz=416, FP32
- Augmentasi agresif untuk generalisasi
"""

import os
import shutil
from pathlib import Path
import torch
from ultralytics import YOLO

# Resolved relative to this file so the script runs from any checkout location.
TRAINING_DIR = Path(__file__).resolve().parent
REPO_ROOT    = TRAINING_DIR.parent
DATASET = TRAINING_DIR / "datasets/moonharvest-uav/classifier_v4_teknofest"
OUT_DIR = TRAINING_DIR / "runs/moonharvest_v6_s_fp32"
ASSETS  = REPO_ROOT / "HarvestmoonGCS" / "Assets" / "models"

def main():
    # Pastikan GPU tersedia sebelum training
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA tidak tersedia! Training dibatalkan.")
    gpu_name = torch.cuda.get_device_name(0)
    vram_gb  = round(torch.cuda.get_device_properties(0).total_memory / 1024**3, 2)
    print(f"[GPU] {gpu_name} | VRAM: {vram_gb} GB")
    torch.cuda.set_device(0)

    print("=" * 65)
    print("MoonHarvest YOLOv8s Training v6")
    print(f"Dataset: {DATASET}")
    print("Config : small + 200 epochs + imgsz=640 + balanced 1500/kelas")
    print("=" * 65)

    # Verify dataset balance
    total = 0
    for split in ["train", "val"]:
        for cls in ["lush_green","inconsistent_growth","drought_severe_stress","bare_soil"]:
            p = DATASET / split / cls
            n = len(list(p.glob("*.jpg"))) + len(list(p.glob("*.png")))
            print(f"  {split}/{cls}: {n}")
            total += n
    print(f"  TOTAL: {total}\n")

    # Backup v5
    v5 = ASSETS / "moonharvest-health-cls-v5.onnx"
    if v5.exists():
        shutil.copy2(v5, ASSETS / "moonharvest-health-cls-v5-backup.onnx")
        print("[OK] Backup v5 → moonharvest-health-cls-v5-backup.onnx\n")

    model = YOLO("yolov8s-cls.pt")

    results = model.train(
        data         = str(DATASET),
        epochs       = 200,
        imgsz        = 640,
        batch        = 8,
        device       = 0,
        workers      = 4,
        project      = str(OUT_DIR.parent),
        name         = OUT_DIR.name,
        exist_ok     = True,

        # Optimizer
        optimizer    = "AdamW",
        lr0          = 0.001,
        lrf          = 0.01,
        momentum     = 0.937,
        weight_decay = 0.0005,
        warmup_epochs= 5,
        cos_lr       = True,

        # Full 200 epochs — no early stopping
        patience     = 0,

        # Regularisasi
        label_smoothing = 0.1,
        dropout      = 0.3,

        # Augmentasi aggressif untuk generalisasi
        degrees      = 30.0,
        translate    = 0.1,
        scale        = 0.4,
        shear        = 4.0,
        perspective  = 0.001,
        flipud       = 0.5,
        fliplr       = 0.5,
        hsv_h        = 0.015,
        hsv_s        = 0.6,
        hsv_v        = 0.3,
        mixup        = 0.1,
        erasing      = 0.3,
        auto_augment = "randaugment",

        amp          = True,
        save         = True,
        save_period  = 20,
        val          = True,
        plots        = True,
        verbose      = True,
    )

    print("\n" + "=" * 65)
    print("Training v6 selesai!")

    # Export ONNX FP32
    best_pt = OUT_DIR / "weights" / "best.pt"
    if best_pt.exists():
        print("Exporting ONNX FP32...")
        trained = YOLO(str(best_pt))
        trained.export(
            format   = "onnx",
            imgsz    = 416,
            opset    = 12,
            dynamic  = False,
            simplify = True,
            half     = False,
        )
        onnx_src = OUT_DIR / "weights" / "best.onnx"
        if onnx_src.exists():
            dst = ASSETS / "moonharvest-health-cls-v5.onnx"  # replace v5
            shutil.copy2(onnx_src, dst)
            print(f"[OK] ONNX → {dst}")
        else:
            print("[WARN] best.onnx tidak ditemukan")

    # Metrics
    print("\n=== HASIL v6 ===")
    try:
        for k, v in results.results_dict.items():
            if isinstance(v, float):
                print(f"  {k}: {v:.4f}")
    except Exception:
        pass

    print(f"\nModel : {OUT_DIR}/weights/best.pt")
    print(f"ONNX  : {ASSETS}/moonharvest-health-cls-v5.onnx")
    print("=" * 65)

if __name__ == "__main__":
    main()
