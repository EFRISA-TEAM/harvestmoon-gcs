"""
MoonHarvest — Prepare UAV Dataset
Menggabungkan semua sumber dataset dan memastikan split train/val bersih.

Fungsi utama:
1. Verifikasi integritas dataset (setiap image punya label)
2. Tampilkan statistik lengkap per split dan per kelas
3. Opsional: pindahkan sebagian train ke val jika val terlalu kecil

Usage:
  python prepare_uav_dataset.py          # cek & tampilkan stats
  python prepare_uav_dataset.py --fix    # pindahkan 15% train → val jika perlu
"""

import argparse
import random
import shutil
from pathlib import Path

BASE = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/moonharvest-uav")
DET  = BASE / "detector"
CLS  = BASE / "classifier"

CLASSES_DET = {0: "crop", 1: "weed"}
CLASSES_CLS = ["lush_green", "well_irrigated", "inconsistent_growth",
               "soil_issues", "disease", "pest"]

VAL_RATIO = 0.15  # target rasio val dari total jika --fix dipakai


# ─────────────────────────────────────────────────────────────
# DETECTOR STATS
# ─────────────────────────────────────────────────────────────

def check_detector_split(split: str):
    img_dir = DET / "images" / split
    lbl_dir = DET / "labels" / split

    images = sorted(img_dir.glob("*.[jJpP][pPnN][gG]*")) if img_dir.exists() else []
    labels = sorted(lbl_dir.glob("*.txt")) if lbl_dir.exists() else []

    img_stems = {p.stem for p in images}
    lbl_stems = {p.stem for p in labels}

    missing_labels = img_stems - lbl_stems
    missing_images = lbl_stems - img_stems

    class_counts = {v: 0 for v in CLASSES_DET.values()}
    total_boxes = 0

    for lbl in labels:
        with open(lbl) as f:
            for line in f:
                parts = line.strip().split()
                if len(parts) >= 5:
                    cls_id = int(parts[0])
                    cls_name = CLASSES_DET.get(cls_id, f"unknown_{cls_id}")
                    class_counts[cls_name] = class_counts.get(cls_name, 0) + 1
                    total_boxes += 1

    return {
        "images": len(images),
        "labels": len(labels),
        "missing_labels": missing_labels,
        "missing_images": missing_images,
        "class_counts": class_counts,
        "total_boxes": total_boxes,
    }


def print_detector_stats():
    print("=" * 60)
    print("DETECTOR DATASET (crop / weed)")
    print(f"Path: {DET}")
    print("=" * 60)

    total_imgs = 0
    for split in ["train", "val"]:
        s = check_detector_split(split)
        total_imgs += s["images"]
        ratio = s["images"] / max(total_imgs, 1) * 100

        print(f"\n[{split.upper()}]")
        print(f"  Images : {s['images']}")
        print(f"  Labels : {s['labels']}")
        print(f"  Boxes  : {s['total_boxes']}")
        for cls, cnt in s["class_counts"].items():
            pct = cnt / max(s["total_boxes"], 1) * 100
            print(f"    {cls:<25}: {cnt:>6} boxes ({pct:.1f}%)")

        if s["missing_labels"]:
            print(f"  ⚠ Missing labels: {len(s['missing_labels'])} files")
            for f in list(s["missing_labels"])[:5]:
                print(f"      {f}")
        if s["missing_images"]:
            print(f"  ⚠ Missing images: {len(s['missing_images'])} files")

    tr = check_detector_split("train")
    vl = check_detector_split("val")
    total = tr["images"] + vl["images"]
    val_pct = vl["images"] / max(total, 1) * 100
    print(f"\n  TOTAL  : {total} images")
    print(f"  Split  : train={tr['images']} ({100-val_pct:.0f}%) | val={vl['images']} ({val_pct:.0f}%)")
    return tr, vl


# ─────────────────────────────────────────────────────────────
# CLASSIFIER STATS
# ─────────────────────────────────────────────────────────────

def print_classifier_stats():
    print("\n" + "=" * 60)
    print("CLASSIFIER DATASET (6 kelas kondisi lahan)")
    print(f"Path: {CLS}")
    print("=" * 60)

    total = 0
    empty_classes = []
    for cls_name in CLASSES_CLS:
        cls_dir = CLS / cls_name
        if cls_dir.exists():
            imgs = list(cls_dir.glob("*.[jJpP][pPnN][gG]*"))
            n = len(imgs)
            bar = "█" * (n // 5) if n > 0 else "░ (kosong)"
            print(f"  {cls_name:<25}: {n:>5} imgs  {bar}")
            total += n
            if n == 0:
                empty_classes.append(cls_name)
        else:
            print(f"  {cls_name:<25}: folder tidak ada!")
            empty_classes.append(cls_name)

    print(f"\n  TOTAL  : {total} images")
    if empty_classes:
        print(f"\n  ⚠ Kelas kosong (perlu annotasi manual atau download tambahan):")
        for c in empty_classes:
            print(f"      {c}")

    return total, empty_classes


# ─────────────────────────────────────────────────────────────
# FIX: Pindahkan sebagian train → val
# ─────────────────────────────────────────────────────────────

def fix_val_split():
    tr = check_detector_split("train")
    vl = check_detector_split("val")
    total = tr["images"] + vl["images"]
    target_val = int(total * VAL_RATIO)

    if vl["images"] >= target_val:
        print(f"\nVal sudah cukup: {vl['images']}/{total} ({vl['images']/total*100:.0f}%) ≥ target {VAL_RATIO*100:.0f}%")
        return

    need = target_val - vl["images"]
    print(f"\nMemindahkan {need} gambar dari train → val...")

    img_train = sorted((DET / "images" / "train").glob("*.[jJpP][pPnN][gG]*"))
    random.seed(42)
    to_move = random.sample(img_train, min(need, len(img_train)))

    moved = 0
    for img in to_move:
        lbl = DET / "labels" / "train" / (img.stem + ".txt")

        # Pindah image
        dst_img = DET / "images" / "val" / img.name
        shutil.move(str(img), str(dst_img))

        # Pindah label jika ada
        if lbl.exists():
            dst_lbl = DET / "labels" / "val" / lbl.name
            shutil.move(str(lbl), str(dst_lbl))

        moved += 1

    print(f"Dipindahkan: {moved} gambar")
    tr2 = check_detector_split("train")
    vl2 = check_detector_split("val")
    total2 = tr2["images"] + vl2["images"]
    print(f"Split baru: train={tr2['images']} | val={vl2['images']} ({vl2['images']/total2*100:.0f}%)")


# ─────────────────────────────────────────────────────────────
# SUMMARY & NEXT STEPS
# ─────────────────────────────────────────────────────────────

def print_next_steps(det_tr, det_vl, cls_total, empty_classes):
    print("\n" + "=" * 60)
    print("RINGKASAN & LANGKAH SELANJUTNYA")
    print("=" * 60)

    total_det = det_tr["images"] + det_vl["images"]
    print(f"\nDetector : {total_det} gambar siap training")
    print(f"           train={det_tr['images']} | val={det_vl['images']}")

    if cls_total > 0:
        print(f"Classifier: {cls_total} gambar siap training")
    else:
        print(f"Classifier: KOSONG — perlu annotasi manual")

    print("\nUntuk training detector:")
    print("  cd /home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training")
    print("  .venv-yolo/bin/python -c \"")
    print("    from ultralytics import YOLO")
    print("    model = YOLO('yolov8n.pt')")
    print("    model.train(")
    print("      data='datasets/moonharvest-uav/detector/data.yaml',")
    print("      epochs=100, imgsz=416, batch=16,")
    print("      name='moonharvest_uav_det_v2'")
    print("    )\"")

    if empty_classes:
        print(f"\n⚠ Kelas classifier yang perlu data manual:")
        for c in empty_classes:
            print(f"   - {c}: potong frame UAV dari raw_uav_frames/ dan masukkan ke classifier/{c}/")

    print(f"\nFrame UAV mentah (122 frame, belum berlabel):")
    print(f"  {BASE}/raw_uav_frames/")
    print(f"  → Gunakan label tool (LabelImg/Roboflow) untuk annotasi manual")
    print(f"  → Setelah berlabel, jalankan: python pseudo_label_frames.py --conf 0.35")


# ─────────────────────────────────────────────────────────────
# MAIN
# ─────────────────────────────────────────────────────────────

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Prepare & verifikasi MoonHarvest UAV dataset")
    parser.add_argument("--fix", action="store_true",
                        help=f"Pindahkan {VAL_RATIO*100:.0f}% train → val jika val terlalu kecil")
    parser.add_argument("--seed", type=int, default=42, help="Random seed (default: 42)")
    args = parser.parse_args()

    random.seed(args.seed)

    det_tr, det_vl = print_detector_stats()
    cls_total, empty_classes = print_classifier_stats()

    if args.fix:
        fix_val_split()
        det_tr, det_vl = check_detector_split("train"), check_detector_split("val")

    print_next_steps(det_tr, det_vl, cls_total, empty_classes)
