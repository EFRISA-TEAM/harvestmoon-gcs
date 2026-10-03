"""
MoonHarvest — Auto-label frame UAV ke 6 kelas kondisi lahan via HSV analysis
Mengisi folder classifier/ dengan gambar berlabel untuk training

Kelas:
  0: lush_green          → hijau subur, tanaman sehat
  1: well_irrigated      → irigasi baik, hijau sedang
  2: inconsistent_growth → pertumbuhan tidak merata, stress
  3: soil_issues         → masalah tanah, kering/coklat
  4: disease             → penyakit, kuning/bercak
  5: pest                → hama, pola irregular

Usage:
  python build_classifier_dataset.py          # proses semua video
  python build_classifier_dataset.py --check  # cek distribusi saja
"""

import argparse
import random
import shutil
from pathlib import Path

import cv2
import numpy as np

# ── Paths ──────────────────────────────────────────────────────────────────────
VIDEOS = [
    "/home/fawwazfa/Program/Harvestmoon/vidio/YDXJ0012_demo(1).mp4",
    "/home/fawwazfa/Program/Harvestmoon/vidio/YDX_burned.mp4",
    "/home/fawwazfa/Program/Harvestmoon/vidio/15d.mp4",
    "/home/fawwazfa/Documents/HarvestmoonGCS/Evidence/Bundles/DEMO-MH-20260615-192036/mission_video.mp4",
    "/home/fawwazfa/Documents/HarvestmoonGCS/Evidence/Bundles/DEMO-MH-20260615-192826/mission_video.mp4",
    "/home/fawwazfa/Documents/HarvestmoonGCS/Evidence/Bundles/DEMO-MH-20260615-200122/mission_video.mp4",
    "/home/fawwazfa/Documents/HarvestmoonGCS/Evidence/Bundles/DEMO-MH-20260615-200400/mission_video.mp4",
    "/home/fawwazfa/Documents/HarvestmoonGCS/Evidence/Bundles/DEMO-MH-20260615-224956/mission_video.mp4",
]

CLS_BASE = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/moonharvest-uav/classifier")
CLASSES  = ["lush_green", "well_irrigated", "inconsistent_growth", "soil_issues", "disease", "pest"]

# Target gambar per kelas
TARGET_PER_CLASS = 400

# ── HSV Classifier ─────────────────────────────────────────────────────────────

def analyze_frame_hsv(frame_bgr: np.ndarray) -> dict:
    """
    Analisis frame dan kembalikan skor per kelas berdasarkan distribusi HSV.
    Return dict {class_name: score} dimana score 0-1.
    """
    hsv = cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2HSV).astype(np.float32)
    h   = hsv[:, :, 0]   # 0-179
    s   = hsv[:, :, 1]   # 0-255
    v   = hsv[:, :, 2]   # 0-255
    total = h.size

    # ── Mask per kondisi ────────────────────────────────────────────────────────

    # Vegetasi umum (ada tanaman)
    veg_mask = (h >= 30) & (h <= 90) & (s > 40) & (v > 40)
    veg_pct  = veg_mask.sum() / total

    # Tanah/bare ground (coklat-merah)
    soil_mask = ((h <= 20) | (h >= 160)) & (s > 30) & (v > 30)
    soil_pct  = soil_mask.sum() / total

    # ── lush_green: hijau gelap subur ───────────────────────────────────────────
    lush_mask = (h >= 38) & (h <= 82) & (s > 60) & (v > 50) & (v < 200)
    lush_pct  = lush_mask.sum() / total

    # ── well_irrigated: hijau terang & cerah, V tinggi (air memantulkan cahaya) ──
    irr_mask  = (h >= 35) & (h <= 85) & (s >= 20) & (s <= 80) & (v >= 120)
    irr_pct   = irr_mask.sum() / total

    # ── inconsistent_growth: campuran hijau + kuning-hijau ──────────────────────
    yellow_green = (h >= 20) & (h <= 42) & (s > 40) & (v > 60)
    yg_pct   = yellow_green.sum() / total

    # ── soil_issues: dominan coklat/kering ──────────────────────────────────────
    dry_mask  = (h >= 5) & (h <= 28) & (s > 20) & (v > 30)
    dry_pct   = dry_mask.sum() / total

    # ── disease: kuning-orange terang (klorosis, bercak penyakit) ────────────────
    # Lebih lebar range untuk tangkap berbagai penyakit tanaman
    disease_mask = ((h >= 12) & (h <= 35) & (s > 50) & (v > 80)) | \
                   ((h >= 0)  & (h <= 12) & (s > 40) & (v > 60))   # merah-kuning
    dis_pct   = disease_mask.sum() / total

    # ── pest: hijau pucat tidak merata + variasi tinggi ──────────────────────────
    pale_green = (h >= 32) & (h <= 78) & (s > 15) & (s < 55) & (v > 80)
    pale_pct  = pale_green.sum() / total

    # Hitung variasi (untuk pest — pola tidak merata)
    h_std = float(np.std(h[veg_mask])) if veg_mask.sum() > 100 else 0.0
    v_std = float(np.std(v)) / 255.0

    # ── Scoring per kelas ────────────────────────────────────────────────────────
    scores = {
        "lush_green":          lush_pct * 2.5 + veg_pct * 0.3,
        "well_irrigated":      irr_pct  * 2.0 + (veg_pct * 0.8 if veg_pct > 0.3 else 0),
        "inconsistent_growth": yg_pct   * 2.5 + (v_std * 0.5),
        "soil_issues":         soil_pct * 2.5 + dry_pct * 1.0 + (1.0 - veg_pct) * 0.8,
        "disease":             dis_pct  * 5.0 + yg_pct * 0.8,
        "pest":                pale_pct * 2.5 + (h_std / 25.0) + v_std * 0.5,
    }

    # Normalisasi
    max_s = max(scores.values()) or 1.0
    scores = {k: v / max_s for k, v in scores.items()}

    return scores, {
        "veg_pct": veg_pct, "soil_pct": soil_pct,
        "lush_pct": lush_pct, "irr_pct": irr_pct,
        "yg_pct": yg_pct, "dry_pct": dry_pct,
        "dis_pct": dis_pct, "pale_pct": pale_pct,
    }


def classify_frame(frame_bgr: np.ndarray, min_veg: float = 0.05) -> tuple[str | None, float]:
    """
    Klasifikasikan frame. Return (class_name, confidence) atau (None, 0) jika tidak jelas.
    """
    scores, stats = analyze_frame_hsv(frame_bgr)

    # Skip frame yang hampir tidak ada vegetasi sama sekali (indoor/non-field)
    if stats["veg_pct"] < min_veg and stats["soil_pct"] < 0.1:
        return None, 0.0

    # Ambil kelas dengan skor tertinggi
    best_cls  = max(scores, key=scores.get)
    best_conf = scores[best_cls]

    # Minimum threshold confidence
    if best_conf < 0.35:
        return None, 0.0

    return best_cls, best_conf


def augment_frame(frame: np.ndarray, aug_type: str) -> np.ndarray:
    """Augmentasi ringan untuk memperbanyak variasi."""
    if aug_type == "fliph":
        return cv2.flip(frame, 1)
    elif aug_type == "flipv":
        return cv2.flip(frame, 0)
    elif aug_type == "bright_up":
        return np.clip(frame.astype(np.float32) * random.uniform(1.1, 1.3), 0, 255).astype(np.uint8)
    elif aug_type == "bright_dn":
        return np.clip(frame.astype(np.float32) * random.uniform(0.7, 0.9), 0, 255).astype(np.uint8)
    elif aug_type == "blur":
        return cv2.GaussianBlur(frame, (3, 3), 0)
    return frame


def build_dataset(target_per_class: int = TARGET_PER_CLASS, step_default: int = 15):
    """Ekstrak frame dari semua video dan label ke 6 kelas."""
    random.seed(42)

    # Buat folder output
    for cls in CLASSES:
        (CLS_BASE / cls).mkdir(parents=True, exist_ok=True)
    val_dir = CLS_BASE / "_val"
    for cls in CLASSES:
        (val_dir / cls).mkdir(parents=True, exist_ok=True)

    counts = {cls: 0 for cls in CLASSES}
    val_counts = {cls: 0 for cls in CLASSES}

    print(f"Target per kelas: {target_per_class}")
    print(f"Output dir: {CLS_BASE}\n")

    for vid_path in VIDEOS:
        if not Path(vid_path).exists():
            print(f"SKIP: {vid_path}")
            continue

        cap = cv2.VideoCapture(vid_path)
        total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
        fps   = cap.get(cv2.CAP_PROP_FPS) or 30.0
        vid_name = Path(vid_path).stem.replace("(", "").replace(")", "").replace(" ", "_")

        # Step: lebih rapat untuk video pendek
        step = max(5, int(fps * 0.5))  # setiap 0.5 detik
        print(f"{vid_name}: {total}f @{fps:.0f}fps step={step}")

        frame_count = 0
        for fn in range(0, total, step):
            cap.set(cv2.CAP_PROP_POS_FRAMES, fn)
            ret, frame = cap.read()
            if not ret:
                continue

            # Resize ke 224x224 (standard classification size)
            frame_resized = cv2.resize(frame, (224, 224))

            cls_name, conf = classify_frame(frame_resized)
            if cls_name is None:
                continue

            # Cek apakah kelas sudah cukup
            total_cls = counts[cls_name] + val_counts[cls_name]
            if total_cls >= int(target_per_class * 1.2):
                continue

            fname = f"{vid_name}_f{fn:06d}.jpg"

            # 15% untuk val set
            if val_counts[cls_name] < int(target_per_class * 0.15):
                dst = val_dir / cls_name / fname
                cv2.imwrite(str(dst), frame_resized, [cv2.IMWRITE_JPEG_QUALITY, 92])
                val_counts[cls_name] += 1
            elif counts[cls_name] < target_per_class:
                dst = CLS_BASE / cls_name / fname
                cv2.imwrite(str(dst), frame_resized, [cv2.IMWRITE_JPEG_QUALITY, 92])
                counts[cls_name] += 1

                # Augmentasi untuk kelas yang masih sedikit
                if counts[cls_name] < target_per_class // 2:
                    for aug in ["fliph", "bright_up", "bright_dn"]:
                        if counts[cls_name] >= target_per_class:
                            break
                        aug_frame = augment_frame(frame_resized, aug)
                        aug_fname = f"aug_{aug}_{fname}"
                        cv2.imwrite(str(CLS_BASE / cls_name / aug_fname), aug_frame,
                                    [cv2.IMWRITE_JPEG_QUALITY, 92])
                        counts[cls_name] += 1

            frame_count += 1

        cap.release()

        # Progress per kelas
        print(f"  → {frame_count} frames diproses")
        for cls in CLASSES:
            print(f"     {cls:<25}: train={counts[cls]:3d} val={val_counts[cls]:3d}")

    print(f"\n{'='*55}")
    print("DATASET FINAL:")
    total_train = 0
    total_val   = 0
    for cls in CLASSES:
        t = counts[cls]
        v = val_counts[cls]
        total_train += t
        total_val   += v
        bar = "█" * (t // 10)
        print(f"  {cls:<25}: train={t:4d} val={v:3d}  {bar}")
    print(f"\n  Total train: {total_train}")
    print(f"  Total val  : {total_val}")

    return counts, val_counts


def check_distribution():
    """Cek distribusi dataset yang sudah ada."""
    print("=== Distribusi Dataset Classifier ===\n")
    total = 0
    for cls in CLASSES:
        cls_dir = CLS_BASE / cls
        n = len(list(cls_dir.glob("*.jpg"))) if cls_dir.exists() else 0
        bar = "█" * (n // 10) if n > 0 else "░ (kosong)"
        print(f"  {cls:<25}: {n:4d}  {bar}")
        total += n
    print(f"\n  Total: {total} gambar")

    # Cek val
    val_dir = CLS_BASE / "_val"
    if val_dir.exists():
        print("\n=== Val set ===")
        for cls in CLASSES:
            v = len(list((val_dir / cls).glob("*.jpg"))) if (val_dir / cls).exists() else 0
            print(f"  {cls:<25}: {v:4d}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Build MoonHarvest 6-class classifier dataset")
    parser.add_argument("--check",   action="store_true", help="Cek distribusi saja")
    parser.add_argument("--target",  type=int, default=400, help="Target gambar per kelas (default: 400)")
    parser.add_argument("--clear",   action="store_true", help="Hapus dataset lama sebelum build")
    args = parser.parse_args()

    if args.check:
        check_distribution()
    else:
        if args.clear:
            for cls in CLASSES:
                cls_dir = CLS_BASE / cls
                if cls_dir.exists():
                    for f in cls_dir.glob("*.jpg"):
                        f.unlink()
            print("Dataset lama dihapus")

        build_dataset(target_per_class=args.target)
