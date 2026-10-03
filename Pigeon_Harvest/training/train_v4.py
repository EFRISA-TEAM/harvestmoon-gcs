#!/usr/bin/env python3
"""
MoonHarvest YOLOv8 Classifier Training v4
4 kelas: lush_green, inconsistent_growth, drought_severe_stress, bare_soil
GPU: RTX 3050, FP32, 150 epochs
"""

import os
import shutil
from pathlib import Path
from ultralytics import YOLO

# ── Config ────────────────────────────────────────────────────────────────────
DATASET = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/moonharvest-uav/classifier_v4_teknofest")
OUT_DIR  = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/runs/moonharvest_v4_fp32")
ASSETS   = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models")

MODEL_NAME = "yolov8n-cls.pt"  # nano classification model
EPOCHS     = 150
IMGSZ      = 224               # optimal untuk classification
BATCH      = 32                # aman untuk RTX 3050 4GB VRAM
WORKERS    = 4
DEVICE     = 0                 # GPU RTX 3050

def main():
    print("=" * 60)
    print("MoonHarvest YOLOv8 Classifier Training v4")
    print(f"Dataset : {DATASET}")
    print(f"Model   : {MODEL_NAME}")
    print(f"Epochs  : {EPOCHS}")
    print(f"Imgsz   : {IMGSZ}")
    print(f"Batch   : {BATCH}")
    print(f"Device  : GPU:{DEVICE} (RTX 3050)")
    print("=" * 60)

    # Verify dataset
    for split in ["train", "val", "test"]:
        for cls in ["lush_green", "inconsistent_growth", "drought_severe_stress", "bare_soil"]:
            p = DATASET / split / cls
            n = len(list(p.glob("*.jpg"))) + len(list(p.glob("*.png")))
            print(f"  {split}/{cls}: {n} gambar")

    print("\nStarting training...\n")

    # Load pretrained model
    model = YOLO(MODEL_NAME)

    # Training dengan augmentasi lengkap
    results = model.train(
        data        = str(DATASET),
        epochs      = EPOCHS,
        imgsz       = IMGSZ,
        batch       = BATCH,
        device      = DEVICE,
        workers     = WORKERS,
        project     = str(OUT_DIR.parent),
        name        = OUT_DIR.name,
        exist_ok    = True,

        # Optimizer
        optimizer   = "AdamW",
        lr0         = 0.001,
        lrf         = 0.01,      # final LR = lr0 * lrf
        momentum    = 0.937,
        weight_decay= 0.0005,
        warmup_epochs = 5,
        cos_lr      = True,      # cosine LR decay

        # Augmentasi — sesuai permintaan
        degrees     = 30.0,      # rotate ±30°
        translate   = 0.1,
        scale       = 0.5,
        shear       = 5.0,
        perspective = 0.0005,
        flipud      = 0.5,       # flip vertical
        fliplr      = 0.5,       # flip horizontal
        hsv_h       = 0.015,     # HSV hue
        hsv_s       = 0.7,       # HSV saturation
        hsv_v       = 0.4,       # HSV brightness
        erasing     = 0.4,       # random erasing
        auto_augment= "randaugment",

        # Misc
        amp         = True,      # Automatic Mixed Precision (hemat VRAM)
        patience    = 30,        # early stopping jika tidak improve 30 epoch
        save        = True,
        save_period = 10,        # save checkpoint setiap 10 epoch
        val         = True,
        plots       = True,
        verbose     = True,
    )

    print("\n" + "=" * 60)
    print("Training selesai!")
    print(f"Best model: {OUT_DIR}/weights/best.pt")
    print("=" * 60)

    # Export ke ONNX FP32
    print("\nExporting ke ONNX FP32...")
    best_pt = OUT_DIR / "weights" / "best.pt"
    if best_pt.exists():
        trained = YOLO(str(best_pt))
        trained.export(
            format   = "onnx",
            imgsz    = IMGSZ,
            opset    = 12,
            dynamic  = False,
            simplify = True,
            half     = False,    # FP32 (bukan half/FP16)
        )

        # Copy ke Assets/models
        onnx_src = best_pt.with_suffix(".onnx")
        if not onnx_src.exists():
            # Cari di folder yang sama
            onnx_src = OUT_DIR / "weights" / "best.onnx"

        if onnx_src.exists():
            ASSETS.mkdir(parents=True, exist_ok=True)
            dst = ASSETS / "moonharvest-health-cls-v5.onnx"
            shutil.copy2(onnx_src, dst)
            print(f"\n[OK] ONNX disalin ke: {dst}")
        else:
            print(f"\n[WARN] ONNX file tidak ditemukan di {onnx_src}")

    # Print final metrics
    print("\n=== HASIL TRAINING ===")
    try:
        metrics = results.results_dict
        for k, v in metrics.items():
            if isinstance(v, float):
                print(f"  {k}: {v:.4f}")
    except:
        pass

    print(f"\nModel tersimpan di: {OUT_DIR}/weights/best.pt")
    print(f"ONNX FP32 di: {ASSETS}/moonharvest-health-cls-v5.onnx")

if __name__ == "__main__":
    main()
