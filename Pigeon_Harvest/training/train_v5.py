#!/usr/bin/env python3
"""
MoonHarvest YOLOv8n Training v5
- YOLOv8n (tetap nano untuk FPS tinggi)
- Full 150 epochs (patience=0, tidak early stop)
- imgsz=416 (lebih besar dari v4)
- 5556 gambar total (lebih banyak dari v4)
- label_smoothing, mixup, dropout untuk regularisasi
"""

import shutil
from pathlib import Path
from ultralytics import YOLO

# ── Config ────────────────────────────────────────────────────────────────────
# Resolved relative to this file so the script runs from any checkout location.
TRAINING_DIR = Path(__file__).resolve().parent
REPO_ROOT    = TRAINING_DIR.parent
DATASET = TRAINING_DIR / "datasets/moonharvest-uav/classifier_v4_teknofest"
OUT_DIR = TRAINING_DIR / "runs/moonharvest_v5_nano_fp32"
ASSETS  = REPO_ROOT / "HarvestmoonGCS/Assets/models"

def main():
    print("=" * 60)
    print("MoonHarvest YOLOv8n Training v5 — Full 150 Epochs")
    print(f"Dataset : {DATASET}")
    print(f"Output  : {OUT_DIR}")
    print("Config  : nano + patience=0 + imgsz=416 + mixup + dropout")
    print("=" * 60)

    # Verify dataset
    total = 0
    for split in ["train", "val"]:
        for cls in ["lush_green", "inconsistent_growth", "drought_severe_stress", "bare_soil"]:
            p = DATASET / split / cls
            n = len(list(p.glob("*.jpg"))) + len(list(p.glob("*.png")))
            print(f"  {split}/{cls}: {n}")
            total += n
    print(f"  TOTAL: {total} gambar\n")

    # Backup model v5 lama jika ada
    old_v5 = ASSETS / "moonharvest-health-cls-v5.onnx"
    if old_v5.exists():
        backup = ASSETS / "moonharvest-health-cls-v5-backup.onnx"
        shutil.copy2(old_v5, backup)
        print(f"[OK] Backup model lama → {backup.name}")

    # Load pretrained YOLOv8n classifier
    model = YOLO("yolov8n-cls.pt")

    # Training
    results = model.train(
        data         = str(DATASET),
        epochs       = 150,
        imgsz        = 416,          # lebih besar dari v4 (224)
        batch        = 16,           # lebih kecil karena imgsz lebih besar
        device       = 0,            # GPU RTX 3050
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

        # KUNCI: patience=0 = full 150 epochs, tidak early stop
        patience     = 0,

        # Regularisasi untuk mencegah overfit
        label_smoothing = 0.1,
        dropout      = 0.2,

        # Augmentasi lengkap
        degrees      = 30.0,
        translate    = 0.1,
        scale        = 0.5,
        shear        = 5.0,
        perspective  = 0.0005,
        flipud       = 0.5,
        fliplr       = 0.5,
        hsv_h        = 0.015,
        hsv_s        = 0.7,
        hsv_v        = 0.4,
        mixup        = 0.1,
        erasing      = 0.4,
        auto_augment = "randaugment",

        # Misc
        amp          = True,
        save         = True,
        save_period  = 25,
        val          = True,
        plots        = True,
        verbose      = True,
    )

    print("\n" + "=" * 60)
    print("Training selesai!")

    # Export ke ONNX FP32
    print("\nExporting ke ONNX FP32...")
    best_pt = OUT_DIR / "weights" / "best.pt"
    if best_pt.exists():
        trained = YOLO(str(best_pt))
        trained.export(
            format   = "onnx",
            imgsz    = 416,
            opset    = 12,
            dynamic  = False,
            simplify = True,
            half     = False,    # FP32 — lebih akurat
        )

        # Copy ke Assets
        onnx_src = OUT_DIR / "weights" / "best.onnx"
        if onnx_src.exists():
            ASSETS.mkdir(parents=True, exist_ok=True)
            dst = ASSETS / "moonharvest-health-cls-v5.onnx"
            shutil.copy2(onnx_src, dst)
            print(f"[OK] ONNX disalin ke: {dst}")
        else:
            print("[WARN] ONNX tidak ditemukan, cek folder weights/")

    # Print metrics
    print("\n=== HASIL TRAINING v5 ===")
    try:
        m = results.results_dict
        for k, v in m.items():
            if isinstance(v, float):
                print(f"  {k}: {v:.4f}")
    except Exception:
        pass

    print(f"\nModel    : {OUT_DIR}/weights/best.pt")
    print(f"ONNX FP32: {ASSETS}/moonharvest-health-cls-v5.onnx")
    print("=" * 60)

if __name__ == "__main__":
    main()
