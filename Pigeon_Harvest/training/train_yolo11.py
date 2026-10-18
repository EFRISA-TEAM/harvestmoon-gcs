#!/usr/bin/env python3
"""
MoonHarvest YOLOv11 Training — train_yolo11.py
================================================
CARA PAKAI:
    cd /home/fawwazfa/Program/Harvestmoon
    source .venv/bin/activate
    pip install ultralytics --upgrade   # pastikan versi ≥8.3.0

    # Training model nano (untuk Android — cepat, ringan):
    python Pigeon_Harvest/training/train_yolo11.py --model nano

    # Training model small (untuk Desktop — lebih akurat):
    python Pigeon_Harvest/training/train_yolo11.py --model small

    # Training keduanya sekaligus:
    python Pigeon_Harvest/training/train_yolo11.py --model both

CATATAN:
    - Training nano  : ~3-5 jam di RTX 3050
    - Training small : ~6-9 jam di RTX 3050
    - Hasil ONNX otomatis di-copy ke Assets/models/
    - Semua log tersimpan di training/runs/
"""

import argparse
import shutil
import sys
from pathlib import Path

import torch

TRAINING_DIR = Path(__file__).resolve().parent
REPO_ROOT    = TRAINING_DIR.parent
PROPOSAL_DATASET = TRAINING_DIR / "datasets/moonharvest-uav/dataset_proposal"
TEKNOFEST_DATASET = TRAINING_DIR / "datasets/moonharvest-uav/classifier_v4_teknofest"
DATASET      = PROPOSAL_DATASET if PROPOSAL_DATASET.exists() else TEKNOFEST_DATASET
ASSETS       = REPO_ROOT / "HarvestmoonGCS" / "Assets" / "models"

if (DATASET / "train").exists():
    CLASSES = sorted([d.name for d in (DATASET / "train").iterdir() if d.is_dir()])
else:
    CLASSES = ["0_zona_normal", "1_ketidakmerataan_irigasi", "2_defisit_air_kanopi", "3_defisit_air_tanah"]

# ─────────────────────────────────────────────────────────────────────────────
def check_environment():
    """Cek semua dependensi sebelum training."""
    print("=" * 65)
    print("🔍 PENGECEKAN ENVIRONMENT")
    print("=" * 65)

    # Cek ultralytics
    try:
        import ultralytics
        ver = ultralytics.__version__
        major, minor = int(ver.split(".")[0]), int(ver.split(".")[1])
        if major < 8 or (major == 8 and minor < 3):
            print(f"⚠️  Ultralytics {ver} — YOLOv11 butuh ≥8.3.0")
            print("   Jalankan: pip install ultralytics --upgrade")
            sys.exit(1)
        print(f"✅ Ultralytics {ver}")
    except ImportError:
        print("❌ Ultralytics tidak terinstall!")
        print("   Jalankan: pip install ultralytics")
        sys.exit(1)

    # Cek GPU
    if not torch.cuda.is_available():
        print("⚠️  GPU tidak tersedia — training akan sangat lambat di CPU!")
        print("   Lanjutkan? (y/n): ", end="")
        if input().strip().lower() != "y":
            sys.exit(0)
    else:
        gpu = torch.cuda.get_device_name(0)
        vram = round(torch.cuda.get_device_properties(0).total_memory / 1024**3, 1)
        print(f"✅ GPU: {gpu} | VRAM: {vram} GB")

    # Cek dataset
    if not DATASET.exists():
        print(f"❌ Dataset tidak ditemukan: {DATASET}")
        print("   Pastikan dataset sudah ada di path tersebut.")
        sys.exit(1)

    total = 0
    print(f"\n📊 Dataset: {DATASET.name}")
    for split in ["train", "val"]:
        for cls in CLASSES:
            p = DATASET / split / cls
            n = len(list(p.glob("*.jpg"))) + len(list(p.glob("*.png")))
            print(f"   {split}/{cls}: {n} gambar")
            total += n
    print(f"   TOTAL: {total} gambar")

    if total < 1000:
        print("⚠️  Dataset sangat kecil (<1000 gambar). Hasil mungkin tidak optimal.")

    print()
    return True


# ─────────────────────────────────────────────────────────────────────────────
def train_model(model_type: str):
    """
    Jalankan training untuk model_type = 'nano' atau 'small'.
    """
    from ultralytics import YOLO

    if model_type == "nano":
        base_weights = "yolo11n-cls.pt"
        out_name     = "moonharvest_v7_nano_yolo11"
        epochs       = 150
        imgsz        = 416
        batch        = 16
        label        = "YOLOv11n (Nano)"
    else:  # small
        base_weights = "yolo11s-cls.pt"
        out_name     = "moonharvest_v7_small_yolo11"
        epochs       = 200
        imgsz        = 640
        batch        = 8
        label        = "YOLOv11s (Small)"

    out_dir = TRAINING_DIR / "runs" / out_name
    onnx_dst_name = f"moonharvest-health-cls-v7-{model_type}.onnx"

    print("=" * 65)
    print(f"🚀 TRAINING: {label}")
    print(f"   Epochs   : {epochs}")
    print(f"   imgsz    : {imgsz}px")
    print(f"   Batch    : {batch}")
    print(f"   Output   : {out_dir}")
    print("=" * 65)

    # Backup model lama jika ada
    old_onnx = ASSETS / onnx_dst_name
    if old_onnx.exists():
        backup = ASSETS / (onnx_dst_name.replace(".onnx", "-backup.onnx"))
        shutil.copy2(old_onnx, backup)
        print(f"[OK] Backup model lama → {backup.name}\n")

    # Load YOLOv11
    try:
        model = YOLO(base_weights)
        print(f"[OK] Model {base_weights} berhasil dimuat\n")
    except Exception as e:
        print(f"❌ Gagal load {base_weights}: {e}")
        print("   Pastikan internet aktif saat pertama kali (untuk download weights)")
        return False

    # Training
    results = model.train(
        data         = str(DATASET),
        epochs       = epochs,
        imgsz        = imgsz,
        batch        = batch,
        device       = 0 if torch.cuda.is_available() else "cpu",
        workers      = 4,
        project      = str(TRAINING_DIR / "runs"),
        name         = out_name,
        exist_ok     = True,

        # Optimizer
        optimizer    = "AdamW",
        lr0          = 0.001,
        lrf          = 0.01,
        weight_decay = 0.0005,
        warmup_epochs= 5,
        cos_lr       = True,

        # Tidak early stop — training penuh
        patience     = 0,

        # Regularisasi
        label_smoothing = 0.1,
        dropout      = 0.3,

        # Augmentasi agresif — penting untuk aerial UAV
        degrees      = 30.0,    # rotasi (drone miring)
        translate    = 0.1,
        scale        = 0.5,     # zoom variasi altitude
        shear        = 5.0,
        flipud       = 0.5,     # flip vertikal (drone bisa dari berbagai angle)
        fliplr       = 0.5,
        hsv_h        = 0.020,   # variasi warna (waktu berbeda)
        hsv_s        = 0.7,     # variasi saturasi (cuaca)
        hsv_v        = 0.4,     # variasi brightness (awan)
        mixup        = 0.15,    # mixup antar kelas
        erasing      = 0.3,     # simulasi oklusi

        amp          = True,    # mixed precision (lebih cepat di GPU)
        save         = True,
        save_period  = 25,      # simpan checkpoint tiap 25 epoch
        val          = True,
        plots        = True,    # grafik training akan tersimpan
        verbose      = True,
    )

    print(f"\n✅ Training {label} selesai!")

    # Export ke ONNX
    best_pt = out_dir / "weights" / "best.pt"
    if best_pt.exists():
        print(f"\n📦 Exporting ke ONNX...")
        trained = YOLO(str(best_pt))
        trained.export(
            format   = "onnx",
            imgsz    = imgsz,
            opset    = 12,
            dynamic  = False,
            simplify = True,
            half     = False,   # FP32 untuk kompatibilitas maksimal
        )
        onnx_src = out_dir / "weights" / "best.onnx"
        if onnx_src.exists():
            ASSETS.mkdir(parents=True, exist_ok=True)
            dst = ASSETS / onnx_dst_name
            shutil.copy2(onnx_src, dst)
            print(f"[OK] ONNX → {dst}")
            size_mb = dst.stat().st_size / 1024 / 1024
            print(f"     Ukuran: {size_mb:.1f} MB")

            # Update primary model for GCS and class names
            primary_onnx = ASSETS / "moonharvest-health-cls.onnx"
            shutil.copy2(onnx_src, primary_onnx)
            classes_file = ASSETS / "classes-moonharvest-health.txt"
            with open(classes_file, "w") as f:
                f.write("\n".join(CLASSES) + "\n")
            print(f"[OK] Primary GCS ONNX & classes-moonharvest-health.txt diperbarui!")
        else:
            print("[WARN] best.onnx tidak ditemukan, skip copy")
    else:
        print(f"[WARN] best.pt tidak ditemukan di {out_dir}")

    # Tampilkan hasil akhir
    print("\n📊 HASIL TRAINING:")
    try:
        for k, v in results.results_dict.items():
            if isinstance(v, float) and ("acc" in k.lower() or "top" in k.lower()):
                print(f"   {k}: {v*100:.2f}%")
    except Exception:
        print("   (Lihat file results.csv di folder output)")

    print(f"\n📁 Model PT  : {out_dir}/weights/best.pt")
    print(f"📁 Model ONNX: {ASSETS}/{onnx_dst_name}")
    return True


# ─────────────────────────────────────────────────────────────────────────────
def main():
    parser = argparse.ArgumentParser(
        description="MoonHarvest YOLOv11 Training",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Contoh:
  python train_yolo11.py --model nano       # Untuk Android (cepat)
  python train_yolo11.py --model small      # Untuk Desktop (akurat)
  python train_yolo11.py --model both       # Keduanya
        """
    )
    parser.add_argument(
        "--model",
        choices=["nano", "small", "both"],
        default="nano",
        help="Pilih model yang ditraining (default: nano)"
    )
    args = parser.parse_args()

    print("\n" + "=" * 65)
    print("🌾 MoonHarvest YOLOv11 Training")
    print("   Upgrade dari YOLOv8n → YOLOv11")
    print("=" * 65 + "\n")

    check_environment()

    if args.model == "both":
        print("📌 Mode: Training NANO dulu, lalu SMALL\n")
        success_n = train_model("nano")
        if success_n:
            print("\n" + "=" * 65)
            print("Nano selesai! Lanjut ke Small...\n")
            train_model("small")
    else:
        train_model(args.model)

    print("\n" + "=" * 65)
    print("✅ SELESAI! Langkah selanjutnya:")
    print("   1. Jalankan ablation study:")
    print("      python Pigeon_Harvest/training/ablation_study.py")
    print("   2. Update GCS untuk pakai model baru")
    print("=" * 65 + "\n")


if __name__ == "__main__":
    main()
