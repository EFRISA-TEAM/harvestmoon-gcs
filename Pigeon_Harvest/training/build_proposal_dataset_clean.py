#!/usr/bin/env python3
"""
build_proposal_dataset_clean.py
================================
Membangun dataset_proposal yang 100% bersih, seimbang, dan bebas kontaminasi antar-kelas:
- 0_zona_normal: Kanopi hijau lebat, vegetasi subur optimal (YDXJ0012, lush_green, well_irrigated, Sektor A)
- 1_ketidakmerataan_irigasi: Padi muda 15 HST, kanopi bercak/tidak merata, anomali pengairan (15d.mp4, Sektor B, non-YDX mission_video). TANPA YDXJ0012!
- 2_defisit_air_kanopi: Klorosis, stres kanopi, kekuningan daun (drought, chlorosis, Sektor C)
- 3_defisit_air_tanah: Lahan kering, tanah terbuka, scorched ground (bare_soil, YDX_burned, Sektor D)

Setiap kelas memiliki:
- Train: 1000 gambar
- Val  : 200 gambar
- Test : 75 gambar
Total  : 5100 gambar
"""

import os
import cv2
import shutil
import hashlib
import random
from pathlib import Path
from collections import defaultdict

random.seed(42)

BASE_DIR = Path("/home/fawwazfa/Program/Harvestmoon")
TRAINING_DIR = BASE_DIR / "Pigeon_Harvest/training"
DATASET_DIR = TRAINING_DIR / "datasets/moonharvest-uav"
OUT_DIR = DATASET_DIR / "dataset_proposal"

VID_15D = BASE_DIR / "vid/15d.mp4"
VID_YDX = BASE_DIR / "vid/YDXJ0012_demo.mp4"
VID_BURNED = BASE_DIR / "vid/YDX_burned.mp4"
VID_MULTI = BASE_DIR / "TEKNOFEST_SIAP/demo_video/stream_multisector_60s.mp4"

CLASSES = [
    "0_zona_normal",
    "1_ketidakmerataan_irigasi",
    "2_defisit_air_kanopi",
    "3_defisit_air_tanah"
]

TMP_BUILD = Path("/tmp/proposal_clean_build")

def get_hash(img_bytes: bytes) -> str:
    return hashlib.md5(img_bytes).hexdigest()

def crop_video_frames(vid_path: Path, start_sec: float, end_sec: float, step_frames: int = 2, crops_per_frame: int = 1):
    """Ekstrak frame atau crops dari video secara sekuensial (sangat cepat)."""
    images = []
    if not vid_path.exists():
        print(f"Warning: {vid_path} not found!")
        return images

    cap = cv2.VideoCapture(str(vid_path))
    fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    start_f = int(start_sec * fps)
    end_f = int(end_sec * fps)
    total_f = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    end_f = min(end_f, total_f)

    curr_f = 0
    while True:
        ret, frame = cap.read()
        if not ret or frame is None or curr_f >= end_f:
            break
        if curr_f >= start_f and ((curr_f - start_f) % step_frames == 0):
            h, w = frame.shape[:2]
            if crops_per_frame == 3 and w >= 1920:
                target = 640
                y1 = (h - target) // 2
                y2 = y1 + target
                c1 = frame[y1:y2, 0:target]
                c2 = frame[y1:y2, (w - target)//2:(w - target)//2 + target]
                c3 = frame[y1:y2, w - target:w]
                for c in [c1, c2, c3]:
                    c_resized = cv2.resize(c, (224, 224), interpolation=cv2.INTER_AREA)
                    images.append(c_resized)
            else:
                dim = min(h, w)
                y1 = (h - dim) // 2
                x1 = (w - dim) // 2
                c = frame[y1:y1+dim, x1:x1+dim]
                c_resized = cv2.resize(c, (224, 224), interpolation=cv2.INTER_AREA)
                images.append(c_resized)
        curr_f += 1

    cap.release()
    return images

def collect_existing_images(folder: Path, exclude_keywords=None):
    """Kumpulkan gambar dari direktori yang ada."""
    collected = []
    if not folder.exists():
        return collected

    exclude_keywords = exclude_keywords or []
    for f in folder.rglob("*"):
        if f.suffix.lower() in [".jpg", ".jpeg", ".png"] and not f.name.endswith(".cache"):
            fname_lower = f.name.lower()
            if any(k in fname_lower for k in exclude_keywords):
                continue
            try:
                img = cv2.imread(str(f))
                if img is not None and img.shape[0] >= 32 and img.shape[1] >= 32:
                    img_r = cv2.resize(img, (224, 224), interpolation=cv2.INTER_AREA)
                    collected.append(img_r)
            except Exception:
                pass
    return collected

def main():
    print("=" * 65)
    print("🚀 MEMBANGUN DATASET PROPOSAL BERSIH & BEBAS DUPLIKAT")
    print("=" * 65)

    if TMP_BUILD.exists():
        shutil.rmtree(TMP_BUILD)
    TMP_BUILD.mkdir(parents=True, exist_ok=True)

    pool = {c: [] for c in CLASSES}

    # ── 1. KELAS 0: 0_zona_normal ─────────────────────────────────────────────
    print("\n[1/4] Mengumpulkan Kelas 0 (0_zona_normal)...")
    # Existing lush_green dan well_irrigated dari classifier_clean
    pool["0_zona_normal"].extend(collect_existing_images(DATASET_DIR / "classifier_clean/train/lush_green"))
    pool["0_zona_normal"].extend(collect_existing_images(DATASET_DIR / "classifier_clean/val/lush_green"))
    pool["0_zona_normal"].extend(collect_existing_images(DATASET_DIR / "classifier_clean/train/well_irrigated"))
    pool["0_zona_normal"].extend(collect_existing_images(DATASET_DIR / "classifier_clean/val/well_irrigated"))
    # Sektor A (0-15s) dari stream_multisector_60s
    pool["0_zona_normal"].extend(crop_video_frames(VID_MULTI, 0.0, 15.0, step_frames=1))
    # YDXJ0012 demo frames (healthy lush vegetation)
    pool["0_zona_normal"].extend(crop_video_frames(VID_YDX, 10.0, 120.0, step_frames=6, crops_per_frame=3))
    print(f"  Total raw Kelas 0: {len(pool['0_zona_normal'])} gambar")

    # ── 2. KELAS 1: 1_ketidakmerataan_irigasi ─────────────────────────────────
    print("\n[2/4] Mengumpulkan Kelas 1 (1_ketidakmerataan_irigasi)...")
    # Padi muda 15 HST (15d.mp4) -> 3 crops per frame
    print("  Ekstraksi 15d.mp4 (sawah muda 15 HST)...")
    pool["1_ketidakmerataan_irigasi"].extend(crop_video_frames(VID_15D, 0.0, 15.0, step_frames=1, crops_per_frame=3))
    # Sektor B (15-30s) dari stream_multisector_60s
    print("  Ekstraksi Sektor B stream_multisector_60s (Padi 15 HST baseline)...")
    pool["1_ketidakmerataan_irigasi"].extend(crop_video_frames(VID_MULTI, 15.0, 30.0, step_frames=1))
    # Genuine inconsistent growth dari classifier_clean (EXCLUDE ydx!)
    print("  Mengambil non-YDX inconsistent_growth dari classifier_clean...")
    pool["1_ketidakmerataan_irigasi"].extend(collect_existing_images(
        DATASET_DIR / "classifier_clean/train/inconsistent_growth", exclude_keywords=["ydx"]))
    pool["1_ketidakmerataan_irigasi"].extend(collect_existing_images(
        DATASET_DIR / "classifier_clean/val/inconsistent_growth", exclude_keywords=["ydx"]))
    print(f"  Total raw Kelas 1: {len(pool['1_ketidakmerataan_irigasi'])} gambar")

    # ── 3. KELAS 2: 2_defisit_air_kanopi ──────────────────────────────────────
    print("\n[3/4] Mengumpulkan Kelas 2 (2_defisit_air_kanopi)...")
    pool["2_defisit_air_kanopi"].extend(collect_existing_images(
        DATASET_DIR / "dataset_proposal/train/2_defisit_air_kanopi"))
    pool["2_defisit_air_kanopi"].extend(collect_existing_images(
        DATASET_DIR / "dataset_proposal/val/2_defisit_air_kanopi"))
    # Sektor C (30-45s) dari stream_multisector_60s
    pool["2_defisit_air_kanopi"].extend(crop_video_frames(VID_MULTI, 30.0, 45.0, step_frames=1))
    print(f"  Total raw Kelas 2: {len(pool['2_defisit_air_kanopi'])} gambar")

    # ── 4. KELAS 3: 3_defisit_air_tanah ───────────────────────────────────────
    print("\n[4/4] Mengumpulkan Kelas 3 (3_defisit_air_tanah)...")
    pool["3_defisit_air_tanah"].extend(collect_existing_images(
        DATASET_DIR / "dataset_proposal/train/3_defisit_air_tanah"))
    pool["3_defisit_air_tanah"].extend(collect_existing_images(
        DATASET_DIR / "dataset_proposal/val/3_defisit_air_tanah"))
    # YDX_burned frames (dry bare soil)
    pool["3_defisit_air_tanah"].extend(crop_video_frames(VID_BURNED, 0.0, 35.0, step_frames=2, crops_per_frame=3))
    # Sektor D (45-60s) dari stream_multisector_60s
    pool["3_defisit_air_tanah"].extend(crop_video_frames(VID_MULTI, 45.0, 60.0, step_frames=1))
    print(f"  Total raw Kelas 3: {len(pool['3_defisit_air_tanah'])} gambar")

    # ── 5. DEDUPLIKASI KETAT (CROSS-CLASS & INTRA-CLASS) ───────────────────────
    print("\n[5/5] Melakukan Deduplikasi Ketat & Penyeimbangan...")
    class_hashes = {c: {} for c in CLASSES}
    global_hashes = defaultdict(list)

    for c in CLASSES:
        for idx, img in enumerate(pool[c]):
            _, buf = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, 95])
            h = get_hash(buf.tobytes())
            global_hashes[h].append(c)
            if h not in class_hashes[c]:
                class_hashes[c][h] = img

    # Identifikasi hash yang muncul di lebih dari 1 kelas (KONTAMINASI)
    contaminated_hashes = {h for h, cls_list in global_hashes.items() if len(set(cls_list)) > 1}
    print(f"  Jumlah hash kontaminasi antar-kelas yang dibuang: {len(contaminated_hashes)}")

    clean_images = {}
    for c in CLASSES:
        clean_list = [img for h, img in class_hashes[c].items() if h not in contaminated_hashes]
        random.shuffle(clean_list)
        clean_images[c] = clean_list
        print(f"  Clean unique pool untuk {c}: {len(clean_list)} gambar")

    # Pastikan setiap kelas memiliki cukup gambar
    target_train = 1000
    target_val = 200
    target_test = 75
    target_total = target_train + target_val + target_test

    for c in CLASSES:
        if len(clean_images[c]) < target_total:
            print(f"  Peringatan: {c} kurang ({len(clean_images[c])} < {target_total}), melakukan augmentasi ringan...")
            # Augmentasi ringan sampai cukup
            extra = []
            while len(clean_images[c]) + len(extra) < target_total:
                base_img = random.choice(clean_images[c])
                aug = base_img.copy()
                if random.random() > 0.5:
                    aug = cv2.flip(aug, 1)
                if random.random() > 0.5:
                    aug = cv2.flip(aug, 0)
                # random rotation 90, 180, 270
                k = random.choice([0, 1, 2, 3])
                if k > 0:
                    aug = np.rot90(aug, k)
                extra.append(aug)
            clean_images[c].extend(extra)

    # ── 6. TULIS KE DATASET_PROPOSAL ──────────────────────────────────────────
    print("\nMenulis dataset ke:", OUT_DIR)
    if OUT_DIR.exists():
        # backup metadata / remove directory
        shutil.rmtree(OUT_DIR)
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    for split in ["train", "val", "test"]:
        for c in CLASSES:
            (OUT_DIR / split / c).mkdir(parents=True, exist_ok=True)

    for c in CLASSES:
        imgs = clean_images[c][:target_total]
        train_imgs = imgs[:target_train]
        val_imgs = imgs[target_train:target_train+target_val]
        test_imgs = imgs[target_train+target_val:target_total]

        for i, img in enumerate(train_imgs):
            cv2.imwrite(str(OUT_DIR / "train" / c / f"{c}_{i:05d}.jpg"), img, [cv2.IMWRITE_JPEG_QUALITY, 95])
        for i, img in enumerate(val_imgs):
            cv2.imwrite(str(OUT_DIR / "val" / c / f"{c}_{i:05d}.jpg"), img, [cv2.IMWRITE_JPEG_QUALITY, 95])
        for i, img in enumerate(test_imgs):
            cv2.imwrite(str(OUT_DIR / "test" / c / f"{c}_{i:05d}.jpg"), img, [cv2.IMWRITE_JPEG_QUALITY, 95])

    # Buat data_proposal.yaml
    yaml_content = f"""path: {OUT_DIR.absolute()}
train: train
val: val
test: test

nc: 4
names:
  0: 0_zona_normal
  1: 1_ketidakmerataan_irigasi
  2: 2_defisit_air_kanopi
  3: 3_defisit_air_tanah
"""
    with open(OUT_DIR / "data_proposal.yaml", "w") as f:
        f.write(yaml_content)

    # Hapus cache lama jika ada
    for cache_f in DATASET_DIR.rglob("*.cache"):
        try:
            cache_f.unlink()
        except Exception:
            pass

    print("\n✅ DATASET BERHASIL DIBUAT DENGAN SEMPURNA!")
    for split in ["train", "val", "test"]:
        print(f"=== {split} ===")
        for c in CLASSES:
            n = len(list((OUT_DIR / split / c).glob("*.jpg")))
            print(f"  {c}: {n} gambar")

if __name__ == "__main__":
    main()
