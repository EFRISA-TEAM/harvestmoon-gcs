#!/usr/bin/env python3
"""
MoonHarvest — Train UAV 4-Class YOLOv8 Object Detector
======================================================
Melatih YOLOv8n object detection pada dataset uav_det_4cls_v2 (4 Kelas)
dioptimalkan untuk GPU NVIDIA RTX 3050 6GB (AMP FP16, AdamW).

Kelas:
  0: Lush Green
  1: Inconsistent Growth
  2: Drought / Severe Stress
  3: Bare Soil / Gap

Setelah selesai, otomatis mengekspor ke ONNX dan memperbarui model di:
- Pigeon_Harvest/HarvestmoonGCS/Assets/models/moonharvest-uav-det.onnx
- TEKNOFEST_SIAP/model/moonharvest-uav-det.onnx
"""

import os, sys, shutil, time
from pathlib import Path
import torch
from ultralytics import YOLO

BASE_DIR     = Path(__file__).parent
REPO_ROOT    = BASE_DIR.parent          # Pigeon_Harvest/
DATA_YAML    = BASE_DIR / "datasets/moonharvest-uav/uav_det_4cls_v2/data.yaml"
BASE_MODEL   = BASE_DIR / "yolov8n.pt"
RUN_DIR      = BASE_DIR / "runs"
RUN_NAME     = "moonharvest_uav_det_4cls_v3"

# Deploy targets, resolved relative to the repo so the script works on any
# machine. Set the env vars to override (e.g. when TEKNOFEST_SIAP lives elsewhere).
TARGET_MODEL_GCS = Path(os.environ.get(
    "MOONHARVEST_DEPLOY_GCS",
    REPO_ROOT / "HarvestmoonGCS" / "Assets" / "models" / "moonharvest-uav-det.onnx",
))
# TEKNOFEST_SIAP sits next to Pigeon_Harvest/ in the Harvestmoon repo.
TARGET_MODEL_SIAP = Path(os.environ.get(
    "MOONHARVEST_DEPLOY_SIAP",
    REPO_ROOT.parent / "TEKNOFEST_SIAP" / "model" / "moonharvest-uav-det.onnx",
))

def main():
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA tidak tersedia! Pastikan GPU NVIDIA aktif.")

    gpu_name = torch.cuda.get_device_name(0)
    vram_gb  = round(torch.cuda.get_device_properties(0).total_memory / 1024**3, 2)
    print("=" * 68)
    print(f"  TRAINING MOONHARVEST UAV YOLOv8 DETECTOR (4 CLASSES)")
    print(f"  GPU : {gpu_name} (VRAM: {vram_gb} GB)")
    print(f"  Data: {DATA_YAML}")
    print("=" * 68)

    if not DATA_YAML.exists():
        raise FileNotFoundError(f"data.yaml tidak ditemukan di {DATA_YAML}")

    model = YOLO(str(BASE_MODEL))

    print("\n[*] Memulai training YOLOv8n 4-Kelas...")
    t0 = time.time()

    results = model.train(
        data         = str(DATA_YAML),
        epochs       = 40,             # 40 epochs optimal & cepat di GPU RTX 3050 (~15-20 min)
        imgsz        = 640,
        batch        = 16,
        device       = 0,
        workers      = 2,
        amp          = True,
        cache        = False,
        optimizer    = "AdamW",
        lr0          = 0.001,
        lrf          = 0.01,
        warmup_epochs= 3.0,
        cos_lr       = True,
        patience     = 15,
        save         = True,
        save_period  = 10,
        val          = True,
        plots        = True,
        verbose      = True,
        project      = str(RUN_DIR),
        name         = RUN_NAME,
        exist_ok     = True,
        # Augmentasi UAV
        degrees      = 15.0,
        translate    = 0.10,
        scale        = 0.40,
        flipud       = 0.5,
        fliplr       = 0.5,
        hsv_h        = 0.015,
        hsv_s        = 0.5,
        hsv_v        = 0.3,
        mosaic       = 1.0,
        mixup        = 0.1,
    )

    dur = (time.time() - t0) / 60.0
    print(f"\n[✓] Training selesai dalam {dur:.1f} menit!")

    # Best weights path
    best_pt = RUN_DIR / RUN_NAME / "weights" / "best.pt"
    if not best_pt.exists():
        best_pt = RUN_DIR / RUN_NAME / "weights" / "last.pt"

    print(f"\n[*] Mengekspor model terbaik ({best_pt.name}) ke ONNX...")
    trained_model = YOLO(str(best_pt))
    onnx_path = trained_model.export(format="onnx", imgsz=640, simplify=True, opset=12)

    exported_onnx = Path(onnx_path)
    if not exported_onnx.exists():
        raise RuntimeError(f"Export ONNX gagal, file tidak ditemukan di {exported_onnx}")

    print(f"[✓] Model ONNX berhasil dibuat: {exported_onnx} ({exported_onnx.stat().st_size / 1024**2:.1f} MB)")

    # Update ke GCS Assets dan TEKNOFEST_SIAP
    for target in [TARGET_MODEL_GCS, TARGET_MODEL_SIAP]:
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            backup = target.with_name(target.stem + "_backup_old.onnx")
            shutil.copy2(target, backup)
            print(f"  [Backup] {target.name} -> {backup.name}")
        shutil.copy2(exported_onnx, target)
        print(f"  [Update] Model tersalin ke -> {target}")

    print("\n" + "=" * 68)
    print("  SEMUA TAHAP TRAINING & DEPLOY MODEL DETEKSI 4-KELAS SELESAI!")
    print("=" * 68)

if __name__ == "__main__":
    main()
