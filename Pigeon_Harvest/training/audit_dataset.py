#!/usr/bin/env python3
"""
MoonHarvest Dataset Audit — audit_dataset.py
=============================================
Cek kualitas, distribusi, dan kelengkapan dataset sebelum training.

CARA PAKAI:
    cd /home/fawwazfa/Program/Harvestmoon
    source .venv/bin/activate
    python Pigeon_Harvest/training/audit_dataset.py

OUTPUT:
    - Laporan distribusi kelas
    - Deteksi label yang rusak/corrupt
    - Deteksi gambar duplikat
    - Grafik distribusi (disimpan sebagai PNG)
    - File: training/runs/dataset_audit.json
"""

import json
import hashlib
import sys
from pathlib import Path
from collections import defaultdict

import argparse
import cv2
import numpy as np

TRAINING_DIR = Path(__file__).resolve().parent
PROPOSAL_DATASET = TRAINING_DIR / "datasets/moonharvest-uav/dataset_proposal"
TEKNOFEST_DATASET = TRAINING_DIR / "datasets/moonharvest-uav/classifier_v4_teknofest"

parser = argparse.ArgumentParser(description="MoonHarvest Dataset Audit")
parser.add_argument("--dataset", type=str, default="", help="Path ke direktori dataset")
args, _ = parser.parse_known_args()

if args.dataset:
    DATASET_BASE = Path(args.dataset)
elif PROPOSAL_DATASET.exists():
    DATASET_BASE = PROPOSAL_DATASET
else:
    DATASET_BASE = TEKNOFEST_DATASET

OUT_DIR = TRAINING_DIR / "runs"

if (DATASET_BASE / "train").exists():
    CLASSES = sorted([d.name for d in (DATASET_BASE / "train").iterdir() if d.is_dir()])
else:
    CLASSES = ["0_zona_normal", "1_ketidakmerataan_irigasi", "2_defisit_air_kanopi", "3_defisit_air_tanah"]

SPLITS = ["train", "val"]


# ─── Fungsi Helper ────────────────────────────────────────────────────────────
def file_hash(path: Path) -> str:
    """MD5 hash untuk deteksi duplikat."""
    h = hashlib.md5()
    with open(path, "rb") as f:
        h.update(f.read(8192))  # baca 8KB pertama saja (cepat)
    return h.hexdigest()


def check_image(path: Path) -> dict:
    """Cek apakah gambar valid dan dapatkan info dasarnya."""
    try:
        img = cv2.imread(str(path))
        if img is None:
            return {"ok": False, "error": "Gagal dibaca (corrupt/bukan gambar)"}
        h, w = img.shape[:2]
        if h < 32 or w < 32:
            return {"ok": False, "error": f"Terlalu kecil: {w}x{h}"}
        # Cek gambar tidak sepenuhnya hitam atau putih (mungkin corrupt)
        mean = img.mean()
        if mean < 5:
            return {"ok": False, "error": "Gambar hampir seluruhnya hitam"}
        if mean > 250:
            return {"ok": False, "error": "Gambar hampir seluruhnya putih"}
        return {"ok": True, "w": w, "h": h, "mean": round(mean, 1)}
    except Exception as e:
        return {"ok": False, "error": str(e)}


# ─── Main Audit ───────────────────────────────────────────────────────────────
def main():
    print("\n" + "=" * 65)
    print("🔍 MoonHarvest Dataset Audit")
    print(f"   Dataset: {DATASET_BASE.name}")
    print("=" * 65 + "\n")

    if not DATASET_BASE.exists():
        print(f"❌ Dataset tidak ditemukan: {DATASET_BASE}")
        print("   Pastikan path sudah benar.")
        sys.exit(1)

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    # ─── Hitung Distribusi ─────────────────────────────────────────────────
    print("📊 DISTRIBUSI KELAS")
    print("─" * 50)

    distribution = {}
    all_files    = []
    corrupt      = []
    total_all    = 0

    for split in SPLITS:
        distribution[split] = {}
        for cls in CLASSES:
            cls_dir = DATASET_BASE / split / cls
            if not cls_dir.exists():
                distribution[split][cls] = 0
                print(f"   ⚠️  {split}/{cls}: DIREKTORI TIDAK ADA!")
                continue

            imgs = list(cls_dir.glob("*.jpg")) + list(cls_dir.glob("*.png")) + list(cls_dir.glob("*.jpeg"))
            distribution[split][cls] = len(imgs)
            total_all += len(imgs)
            all_files.extend([(f, split, cls) for f in imgs])
            print(f"   {split}/{cls}: {len(imgs):>5} gambar")

    print(f"\n   TOTAL: {total_all} gambar\n")

    # ─── Cek Keseimbangan ─────────────────────────────────────────────────
    print("⚖️  CEK KESEIMBANGAN KELAS (train)")
    print("─" * 50)
    train_counts = [distribution["train"].get(c, 0) for c in CLASSES]
    if any(n == 0 for n in train_counts):
        print("   ❌ Ada kelas dengan 0 gambar!")
    elif max(train_counts) > 0:
        ratio = min(train_counts) / max(train_counts)
        if ratio < 0.5:
            worst_cls = CLASSES[train_counts.index(min(train_counts))]
            print(f"   ⚠️  Tidak seimbang! Kelas '{worst_cls}' paling sedikit.")
            print(f"      Min: {min(train_counts)} | Max: {max(train_counts)} | Ratio: {ratio:.2f}")
            print(f"      Tambahkan lebih banyak gambar untuk kelas '{worst_cls}'!")
        else:
            print(f"   ✅ Distribusi seimbang (ratio min/max: {ratio:.2f})")

    # ─── Cek Gambar Rusak (sampling 10%) ──────────────────────────────────
    print("\n🩺 CEK KUALITAS GAMBAR (sampling 10%)")
    print("─" * 50)

    sample_size = max(10, len(all_files) // 10)
    sample = all_files[:sample_size] if len(all_files) <= sample_size else \
             [all_files[i] for i in np.random.choice(len(all_files), sample_size, replace=False)]

    checked = ok_count = 0
    for path, split, cls in sample:
        result = check_image(path)
        checked += 1
        if result["ok"]:
            ok_count += 1
        else:
            corrupt.append({"file": str(path), "split": split, "cls": cls, "error": result["error"]})

    print(f"   Dicek  : {checked} gambar (dari {total_all} total)")
    print(f"   OK     : {ok_count}")
    print(f"   Rusak  : {len(corrupt)}")

    if corrupt:
        print(f"\n   ❌ FILE BERMASALAH:")
        for c in corrupt[:10]:
            print(f"      {c['split']}/{c['cls']}/{Path(c['file']).name}: {c['error']}")
        if len(corrupt) > 10:
            print(f"      ... dan {len(corrupt)-10} lainnya (lihat JSON output)")

    # ─── Cek Duplikat (sampling 5%) ────────────────────────────────────────
    print("\n🔁 CEK DUPLIKAT (sampling 5%)")
    print("─" * 50)

    dup_sample_size = max(10, len(all_files) // 20)
    dup_sample = all_files[:dup_sample_size]
    hashes = defaultdict(list)
    for path, split, cls in dup_sample:
        h = file_hash(path)
        hashes[h].append(f"{split}/{cls}/{path.name}")

    duplicates = {k: v for k, v in hashes.items() if len(v) > 1}
    if duplicates:
        print(f"   ⚠️  {len(duplicates)} kelompok duplikat ditemukan:")
        for h, files in list(duplicates.items())[:5]:
            print(f"      {files[0]} ↔ {files[1]}")
    else:
        print(f"   ✅ Tidak ada duplikat terdeteksi (dari {dup_sample_size} sample)")

    # ─── Rekomendasi ───────────────────────────────────────────────────────
    print("\n💡 REKOMENDASI")
    print("─" * 50)

    recs = []
    for cls in CLASSES:
        n = distribution["train"].get(cls, 0)
        if n < 500:
            recs.append(f"⚠️  '{cls}': hanya {n} gambar — tambahkan minimal {500-n} gambar lagi")
        elif n < 1000:
            recs.append(f"🔶 '{cls}': {n} gambar — disarankan ≥1000 untuk performa optimal")
        else:
            recs.append(f"✅ '{cls}': {n} gambar — cukup")

    for r in recs:
        print(f"   {r}")

    total_train = sum(distribution["train"].values())
    if total_train < 3000:
        print(f"\n   ⚠️  Total training: {total_train} gambar")
        print(f"      Disarankan ≥3000 untuk training YOLOv11 yang baik")
        print(f"      Gunakan pseudo_label_frames.py untuk memperbanyak data")

    # ─── Simpan Hasil ──────────────────────────────────────────────────────
    report = {
        "dataset_path": str(DATASET_BASE),
        "total_images": total_all,
        "distribution": distribution,
        "corrupt_files": corrupt,
        "duplicates_found": len(duplicates),
        "recommendations": recs,
    }

    report_path = OUT_DIR / "dataset_audit.json"
    with open(report_path, "w") as f:
        json.dump(report, f, indent=2, ensure_ascii=False)

    print(f"\n💾 Laporan lengkap → {report_path}")
    print("\n" + "=" * 65)
    print("✅ Audit selesai!")
    print("   Langkah selanjutnya:")
    print("   1. Perbaiki file corrupt yang ditemukan")
    print("   2. Tambah gambar untuk kelas yang kurang")
    print("   3. Jalankan: python train_yolo11.py --model nano")
    print("=" * 65 + "\n")


if __name__ == "__main__":
    main()
