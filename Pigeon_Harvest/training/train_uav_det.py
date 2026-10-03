"""
MoonHarvest — Training Script (GPU RTX 3050 6GB)
Retrain moonharvest-uav-det menggunakan dataset aerial crop/weed
Dioptimalkan untuk VRAM 6GB dengan mixed precision (AMP) dan batch size 16

Usage:
  python train_uav_det.py              # training penuh (100 epochs)
  python train_uav_det.py --epochs 50  # training singkat untuk testing
  python train_uav_det.py --resume     # lanjutkan training dari checkpoint terakhir
"""

import argparse
import os
from pathlib import Path

import torch
from ultralytics import YOLO


# ── Konfigurasi ───────────────────────────────────────────────────────────────
TRAINING_DIR  = Path(__file__).parent
DATA_YAML     = TRAINING_DIR / "datasets/moonharvest-uav/detector/data.yaml"
PRETRAIN_PT   = TRAINING_DIR / "yolov8n.pt"          # base model YOLOv8n
OUTPUT_NAME   = "moonharvest_uav_det_v2"
# TEKNOFEST_SIAP sits next to Pigeon_Harvest/; override with MOONHARVEST_EXPORT_DIR.
EXPORT_DIR    = Path(os.environ.get(
    "MOONHARVEST_EXPORT_DIR",
    TRAINING_DIR.parent.parent / "TEKNOFEST_SIAP" / "model",
))


# ── Hyperparameters (dioptimalkan untuk RTX 3050 6GB) ─────────────────────────
TRAIN_ARGS = dict(
    data       = str(DATA_YAML),
    imgsz      = 416,          # sesuai model existing, hemat VRAM
    batch      = 16,           # aman untuk 6GB VRAM dengan AMP
    epochs     = 100,          # default, bisa override via --epochs
    device     = 0,            # GPU 0 (RTX 3050)
    workers    = 4,            # DataLoader workers (laptop)
    amp        = True,         # Mixed precision FP16 — hemat ~40% VRAM
    cache      = "ram",        # Cache dataset di RAM (dataset kecil ~1K img)
    optimizer  = "AdamW",      # AdamW lebih stabil dari SGD untuk fine-tuning
    lr0        = 0.001,        # learning rate awal
    lrf        = 0.01,         # learning rate final (lr0 * lrf)
    momentum   = 0.937,
    weight_decay = 0.0005,
    warmup_epochs = 3.0,
    warmup_momentum = 0.8,
    box        = 7.5,          # box loss weight
    cls        = 0.5,          # class loss weight
    dfl        = 1.5,          # distribution focal loss weight
    # Augmentasi
    hsv_h      = 0.015,        # hue augmentation (penting untuk variasi lighting UAV)
    hsv_s      = 0.7,          # saturation
    hsv_v      = 0.4,          # value/brightness
    degrees    = 15.0,         # rotasi (drone bergerak)
    translate  = 0.1,
    scale      = 0.5,
    flipud     = 0.3,          # flip vertikal (aerial view, tidak ada orientasi fix)
    fliplr     = 0.5,
    mosaic     = 1.0,          # mosaic augmentation
    mixup      = 0.1,
    # Early stopping & saving
    patience   = 30,           # stop jika tidak ada improvement 30 epochs
    save_period = 10,          # simpan checkpoint setiap 10 epochs
    val        = True,
    plots      = True,
    verbose    = True,
    project    = str(TRAINING_DIR / "runs"),
    name       = OUTPUT_NAME,
    exist_ok   = True,
)

# Fine-tune args: dari best.pt dengan LR lebih kecil
FINETUNE_ARGS = dict(
    data       = str(DATA_YAML),
    imgsz      = 416,
    batch      = 16,
    epochs     = 50,           # fine-tune 50 epochs tambahan
    device     = 0,
    workers    = 4,
    amp        = True,
    cache      = "ram",
    optimizer  = "AdamW",
    lr0        = 0.0001,       # 10x lebih kecil dari training awal
    lrf        = 0.01,
    momentum   = 0.937,
    weight_decay = 0.0005,
    warmup_epochs = 1.0,       # warmup lebih pendek untuk fine-tune
    warmup_momentum = 0.8,
    box        = 7.5,
    cls        = 1.0,          # naikkan cls loss untuk bantu class imbalance
    dfl        = 1.5,
    # Augmentasi lebih agresif untuk fine-tune
    hsv_h      = 0.02,
    hsv_s      = 0.8,
    hsv_v      = 0.5,
    degrees    = 20.0,
    translate  = 0.15,
    scale      = 0.6,
    flipud     = 0.5,
    fliplr     = 0.5,
    mosaic     = 1.0,
    mixup      = 0.15,
    copy_paste = 0.1,          # copy-paste augmentation untuk object detection
    patience   = 20,
    save_period = 10,
    val        = True,
    plots      = True,
    verbose    = True,
    project    = str(TRAINING_DIR / "runs"),
    name       = OUTPUT_NAME + "_finetune",
    exist_ok   = True,
)


def check_gpu():
    """Verifikasi GPU tersedia dan tampilkan info."""
    if not torch.cuda.is_available():
        print("⚠ CUDA tidak tersedia! Training akan menggunakan CPU (sangat lambat).")
        print("  Pastikan driver NVIDIA dan CUDA toolkit terinstall.")
        return False

    gpu = torch.cuda.get_device_properties(0)
    vram_gb = gpu.total_memory / 1024**3
    vram_free = torch.cuda.mem_get_info()[0] / 1024**3

    print(f"GPU    : {gpu.name}")
    print(f"VRAM   : {vram_gb:.1f} GB total, {vram_free:.1f} GB free")
    print(f"CUDA   : {torch.version.cuda}")
    print(f"PyTorch: {torch.__version__}")

    if vram_gb < 4.0:
        print("⚠ VRAM < 4GB — kurangi batch size ke 8")
    elif vram_gb < 6.0:
        print("⚠ VRAM < 6GB — batch=16 mungkin OOM, coba batch=8 jika error")

    return True


def export_onnx(run_dir: Path):
    """Export model terbaik ke ONNX setelah training selesai."""
    best_pt = run_dir / "weights" / "best.pt"
    if not best_pt.exists():
        print(f"⚠ best.pt tidak ditemukan di {best_pt}")
        return

    print(f"\nExporting {best_pt} → ONNX...")
    model = YOLO(str(best_pt))
    model.export(
        format   = "onnx",
        imgsz    = 416,
        opset    = 17,
        simplify = True,
        dynamic  = False,
    )

    # Copy ke folder model TEKNOFEST
    onnx_src = best_pt.parent / "best.onnx"
    if onnx_src.exists():
        EXPORT_DIR.mkdir(parents=True, exist_ok=True)
        dst = EXPORT_DIR / "moonharvest-uav-det-v2.onnx"
        import shutil
        shutil.copy2(onnx_src, dst)
        print(f"ONNX saved → {dst}")


def main():
    parser = argparse.ArgumentParser(description="Train MoonHarvest UAV Detector (RTX 3050 6GB)")
    parser.add_argument("--epochs",   type=int,  default=100,   help="Jumlah epochs (default: 100)")
    parser.add_argument("--batch",    type=int,  default=16,    help="Batch size (default: 16)")
    parser.add_argument("--imgsz",    type=int,  default=416,   help="Image size (default: 416)")
    parser.add_argument("--resume",   action="store_true",      help="Lanjutkan dari last.pt")
    parser.add_argument("--finetune", action="store_true",      help="Fine-tune dari best.pt dengan LR kecil")
    parser.add_argument("--no-amp",   action="store_true",      help="Matikan mixed precision (jika ada error)")
    parser.add_argument("--export",   action="store_true",      help="Export ke ONNX setelah training")
    args = parser.parse_args()

    print("=" * 60)
    print("MoonHarvest UAV Detector — Training")
    print("=" * 60)

    # Cek GPU
    gpu_ok = check_gpu()
    print()

    # Cek dataset
    if not DATA_YAML.exists():
        print(f"ERROR: data.yaml tidak ditemukan: {DATA_YAML}")
        return

    # Pilih mode training
    if args.finetune:
        # Fine-tune dari best.pt v2 dengan LR kecil
        best_pt = TRAINING_DIR / "runs" / OUTPUT_NAME / "weights" / "best.pt"
        if not best_pt.exists():
            print(f"ERROR: best.pt tidak ditemukan: {best_pt}")
            print(f"       Jalankan training normal dulu tanpa --finetune")
            return

        FINETUNE_ARGS["epochs"] = args.epochs if args.epochs != 100 else 50
        FINETUNE_ARGS["batch"]  = args.batch
        FINETUNE_ARGS["imgsz"]  = args.imgsz
        FINETUNE_ARGS["amp"]    = not args.no_amp

        if not gpu_ok:
            FINETUNE_ARGS["device"]  = "cpu"
            FINETUNE_ARGS["amp"]     = False
            FINETUNE_ARGS["workers"] = 2

        run_dir = TRAINING_DIR / "runs" / (OUTPUT_NAME + "_finetune")
        print(f"Mode     : FINE-TUNE dari best.pt")
        print(f"Base     : {best_pt}")
        print(f"Dataset  : {DATA_YAML}")
        print(f"Epochs   : {FINETUNE_ARGS['epochs']}")
        print(f"LR       : {FINETUNE_ARGS['lr0']} (10x lebih kecil dari training awal)")
        print(f"Batch    : {FINETUNE_ARGS['batch']}")
        print(f"AMP      : {FINETUNE_ARGS['amp']}")
        print(f"Device   : GPU {FINETUNE_ARGS['device']}" if gpu_ok else "Device   : CPU")
        print(f"Output   : {run_dir}")
        print("=" * 60)

        model = YOLO(str(best_pt))
        results = model.train(**FINETUNE_ARGS)

    elif args.resume:
        # Lanjutkan dari last.pt
        last_pt = TRAINING_DIR / "runs" / OUTPUT_NAME / "weights" / "last.pt"
        if not last_pt.exists():
            print(f"ERROR: last.pt tidak ditemukan: {last_pt}")
            return
        run_dir = TRAINING_DIR / "runs" / OUTPUT_NAME
        print(f"Mode     : RESUME dari last.pt")
        print(f"Checkpoint: {last_pt}")
        print("=" * 60)
        model = YOLO(str(last_pt))
        results = model.train(resume=True)

    else:
        # Training dari awal
        TRAIN_ARGS["epochs"] = args.epochs
        TRAIN_ARGS["batch"]  = args.batch
        TRAIN_ARGS["imgsz"]  = args.imgsz
        TRAIN_ARGS["amp"]    = not args.no_amp

        if not gpu_ok:
            TRAIN_ARGS["device"]  = "cpu"
            TRAIN_ARGS["amp"]     = False
            TRAIN_ARGS["workers"] = 2

        run_dir = TRAINING_DIR / "runs" / OUTPUT_NAME
        print(f"Mode     : TRAINING BARU dari yolov8n.pt")
        print(f"Dataset  : {DATA_YAML}")
        print(f"Epochs   : {TRAIN_ARGS['epochs']}")
        print(f"Batch    : {TRAIN_ARGS['batch']}")
        print(f"ImgSz    : {TRAIN_ARGS['imgsz']}")
        print(f"AMP      : {TRAIN_ARGS['amp']} (mixed precision FP16)")
        print(f"Device   : GPU {TRAIN_ARGS['device']}" if gpu_ok else "Device   : CPU")
        print(f"Output   : {run_dir}")
        print("=" * 60)

        model = YOLO(str(PRETRAIN_PT))
        results = model.train(**TRAIN_ARGS)

    # Tampilkan hasil
    print(f"\nTraining selesai!")
    print(f"Best weights : {run_dir}/weights/best.pt")
    print(f"Last weights : {run_dir}/weights/last.pt")
    print(f"Results      : {run_dir}/results.csv")

    # Export ONNX jika diminta
    if args.export:
        export_onnx(run_dir)

    # Tips selanjutnya
    suffix = "_finetune" if args.finetune else ""
    print("\nLangkah selanjutnya:")
    print(f"  Fine-tune  : python train_uav_det.py --finetune --export")
    print(f"  Export ONNX: python train_uav_det.py --finetune --export")
    print(f"  Atau manual: yolo export model={run_dir}/weights/best.pt format=onnx imgsz=416")


if __name__ == "__main__":
    main()
