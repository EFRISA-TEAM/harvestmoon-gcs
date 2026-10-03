#!/usr/bin/env python3
"""
MoonHarvest Dataset Collector v1.0
Mengumpulkan dataset dari Kaggle, Roboflow, video, dan dataset existing
untuk 4 kelas: lush_green, inconsistent_growth, drought_severe_stress, bare_soil

Target: 1000+ gambar per kelas
"""

import os
import sys
import shutil
import zipfile
import json
import random
import cv2
import numpy as np
from pathlib import Path
from typing import Optional

# ── Config ────────────────────────────────────────────────────────────────────
BASE_DIR   = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training")
OUT_DIR    = BASE_DIR / "datasets/moonharvest-uav/classifier_v4_teknofest"
EXISTING   = BASE_DIR / "datasets/moonharvest-uav/classifier_clean"
VID_DIR    = Path("/home/fawwazfa/Program/Harvestmoon/vid")
TMP_DIR    = Path("/tmp/moonharvest_dataset_tmp")
TARGET     = 1000  # target gambar per kelas

CLASSES = ["lush_green", "inconsistent_growth", "drought_severe_stress", "bare_soil"]

# Mapping kelas lama → kelas baru
CLASS_MAP = {
    "lush_green":          "lush_green",
    "well_irrigated":      "lush_green",
    "inconsistent_growth": "inconsistent_growth",
    "soil_issues":         "drought_severe_stress",
    "disease":             "drought_severe_stress",
    "pest":                "bare_soil",
}

# Kaggle datasets yang relevan
KAGGLE_DATASETS = [
    "prasanshasatpathy/rice-diseases",
    "minhhungchu/plant-health",
    "agritech/rice-field-health",
    "uciml/plant-disease-dataset",
    "vishnuraom/crop-disease-detection",
    "emmarex/plantdisease",
    "saroz014/plant-disease",
    "alinedobrovsky/plant-classification",
    "abdallahalidev/plantvillage-dataset",
    "qramkrishna/rice-leaf-diseases",
]

# Roboflow workspaces/projects yang relevan
ROBOFLOW_PROJECTS = [
    ("bradyz", "rice-leaf-disease-q3yji"),
    ("rice-project-mvzad", "rice-field"),
    ("crop-health-dataset", "crop-health"),
    ("vegetation", "vegetation-stress"),
    ("agriculture-lswgz", "crop-monitoring"),
]

PYTHON = sys.executable

def log(msg: str, level: str = "INFO"):
    icons = {"INFO": "ℹ", "OK": "✓", "WARN": "⚠", "ERR": "✗", "STEP": "→"}
    print(f"[{icons.get(level,'·')}] {msg}", flush=True)

def count_images(folder: Path) -> int:
    if not folder.exists(): return 0
    return sum(1 for f in folder.rglob("*") if f.suffix.lower() in {".jpg", ".jpeg", ".png"})

def copy_images(src: Path, dst: Path, limit: Optional[int] = None):
    dst.mkdir(parents=True, exist_ok=True)
    imgs = [f for f in src.rglob("*") if f.suffix.lower() in {".jpg", ".jpeg", ".png"}]
    if limit: imgs = imgs[:limit]
    copied = 0
    for img in imgs:
        dest = dst / f"{src.name}_{img.name}"
        if not dest.exists():
            shutil.copy2(img, dest)
            copied += 1
    return copied

# ── Step 1: Copy dari dataset existing ───────────────────────────────────────
def step1_copy_existing():
    log("STEP 1: Copy dari dataset existing (classifier_clean)...", "STEP")
    total = 0
    for split in ["train", "val"]:
        for old_cls, new_cls in CLASS_MAP.items():
            src = EXISTING / split / old_cls
            dst = OUT_DIR / new_cls
            if src.exists():
                n = copy_images(src, dst)
                total += n
                log(f"  {old_cls} → {new_cls}: +{n} gambar", "OK")
    log(f"Step 1 selesai: +{total} gambar dari existing dataset", "OK")

# ── Step 2: Ekstrak frame dari video ─────────────────────────────────────────
def step2_extract_video_frames():
    log("STEP 2: Ekstrak frame dari video...", "STEP")

    # Mapping video ke kelas berdasarkan nama video
    video_class_map = {
        "15d.mp4":             "drought_severe_stress",  # drought/dry field
        "YDX_burned.mp4":      "bare_soil",              # burned = bare soil
        "YDXJ0012_demo.mp4":   "lush_green",             # demo = healthy field
        "YDXJ0012_demo(1).mp4":"inconsistent_growth",    # variasi demo
    }

    total = 0
    for vid_name, cls in video_class_map.items():
        vid_path = VID_DIR / vid_name
        if not vid_path.exists():
            log(f"  Video tidak ditemukan: {vid_name}", "WARN")
            continue

        dst = OUT_DIR / cls
        dst.mkdir(parents=True, exist_ok=True)

        cap = cv2.VideoCapture(str(vid_path))
        fps = cap.get(cv2.CAP_PROP_FPS)
        total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

        # Ambil 1 frame per 15 frames (~2fps dari 30fps)
        interval = 15
        frame_idx = 0
        saved = 0
        stem = Path(vid_name).stem.replace(" ", "_").replace("(", "").replace(")", "")

        while True:
            ret, frame = cap.read()
            if not ret: break
            if frame_idx % interval == 0:
                # Crop ke 640x640 dari tengah
                h, w = frame.shape[:2]
                min_dim = min(h, w)
                y1 = (h - min_dim) // 2
                x1 = (w - min_dim) // 2
                crop = frame[y1:y1+min_dim, x1:x1+min_dim]
                crop = cv2.resize(crop, (640, 640))

                out_path = dst / f"vid_{stem}_{frame_idx:06d}.jpg"
                if not out_path.exists():
                    cv2.imwrite(str(out_path), crop, [cv2.IMWRITE_JPEG_QUALITY, 90])
                    saved += 1
            frame_idx += 1

        cap.release()
        total += saved
        log(f"  {vid_name} → {cls}: +{saved} frames", "OK")

    log(f"Step 2 selesai: +{total} frames dari video", "OK")

# ── Step 3: Download dari Kaggle ─────────────────────────────────────────────
def step3_kaggle():
    log("STEP 3: Download dataset dari Kaggle...", "STEP")
    TMP_DIR.mkdir(parents=True, exist_ok=True)

    try:
        import kaggle
        kaggle.api.authenticate()
    except Exception as e:
        log(f"  Kaggle auth gagal: {e}", "ERR")
        return

    downloaded = 0
    for dataset in KAGGLE_DATASETS:
        try:
            dl_path = TMP_DIR / dataset.replace("/", "_")
            dl_path.mkdir(parents=True, exist_ok=True)
            log(f"  Downloading: {dataset}...", "INFO")
            kaggle.api.dataset_download_files(dataset, path=str(dl_path), unzip=True, quiet=True)

            # Auto-detect dan copy gambar berdasarkan folder name
            for cls in CLASSES:
                variants = [cls, cls.replace("_", "-"), cls.replace("_", " "),
                           cls.split("_")[0]]  # prefix match
                for v in variants:
                    for img_dir in dl_path.rglob("*"):
                        if img_dir.is_dir() and v.lower() in img_dir.name.lower():
                            n = copy_images(img_dir, OUT_DIR / cls, limit=200)
                            if n > 0:
                                log(f"    {img_dir.name} → {cls}: +{n}", "OK")
                                downloaded += n

            # Jika tidak ada folder spesifik, distribusikan semua gambar
            all_imgs = [f for f in dl_path.rglob("*")
                       if f.suffix.lower() in {".jpg", ".jpeg", ".png"}]
            if all_imgs and downloaded == 0:
                # Distribusikan rata ke semua kelas
                per_cls = len(all_imgs) // 4
                for i, cls in enumerate(CLASSES):
                    subset = all_imgs[i*per_cls:(i+1)*per_cls]
                    dst = OUT_DIR / cls
                    dst.mkdir(parents=True, exist_ok=True)
                    for img in subset[:100]:  # max 100/kelas per dataset
                        dest = dst / f"kaggle_{dataset.split('/')[-1]}_{img.name}"
                        if not dest.exists():
                            shutil.copy2(img, dest)
                            downloaded += 1

        except Exception as e:
            log(f"  Gagal download {dataset}: {e}", "WARN")
            continue

    log(f"Step 3 selesai: +{downloaded} gambar dari Kaggle", "OK")

# ── Step 4: Download dari Roboflow ────────────────────────────────────────────
def step4_roboflow():
    log("STEP 4: Download dataset dari Roboflow...", "STEP")
    TMP_DIR.mkdir(parents=True, exist_ok=True)

    try:
        from roboflow import Roboflow
        rf = Roboflow(api_key="DT54TAT3qXN0sKz7D7Qv")
    except Exception as e:
        log(f"  Roboflow init gagal: {e}", "ERR")
        return

    downloaded = 0
    for workspace, project_name in ROBOFLOW_PROJECTS:
        try:
            log(f"  Trying: {workspace}/{project_name}...", "INFO")
            project = rf.workspace(workspace).project(project_name)
            version = project.version(1)
            dl_path = TMP_DIR / f"rf_{workspace}_{project_name}"
            dataset = version.download("folder", location=str(dl_path), overwrite=False)

            # Copy gambar ke folder kelas yang sesuai
            for cls in CLASSES:
                variants = [cls, cls.replace("_", "-"), cls.split("_")[0]]
                for v in variants:
                    for img_dir in dl_path.rglob("*"):
                        if img_dir.is_dir() and v.lower() in img_dir.name.lower():
                            n = copy_images(img_dir, OUT_DIR / cls, limit=300)
                            if n > 0:
                                log(f"    {img_dir.name} → {cls}: +{n}", "OK")
                                downloaded += n

        except Exception as e:
            log(f"  Gagal {workspace}/{project_name}: {e}", "WARN")
            continue

    log(f"Step 4 selesai: +{downloaded} gambar dari Roboflow", "OK")

# ── Step 5: Augmentasi hingga target 1000/kelas ───────────────────────────────
def augment_image(img: np.ndarray, seed: int) -> np.ndarray:
    rng = np.random.RandomState(seed)
    aug = img.copy()

    # Flip
    if rng.random() > 0.5: aug = cv2.flip(aug, 1)
    if rng.random() > 0.5: aug = cv2.flip(aug, 0)

    # Rotate
    angle = rng.uniform(-30, 30)
    h, w = aug.shape[:2]
    M = cv2.getRotationMatrix2D((w//2, h//2), angle, 1.0)
    aug = cv2.warpAffine(aug, M, (w, h))

    # Brightness/contrast
    alpha = rng.uniform(0.7, 1.3)
    beta  = rng.randint(-30, 30)
    aug = cv2.convertScaleAbs(aug, alpha=alpha, beta=beta)

    # HSV shift (untuk variasi warna vegetasi)
    hsv = cv2.cvtColor(aug, cv2.COLOR_BGR2HSV).astype(np.float32)
    hsv[:,:,0] = np.clip(hsv[:,:,0] + rng.uniform(-10, 10), 0, 179)
    hsv[:,:,1] = np.clip(hsv[:,:,1] * rng.uniform(0.8, 1.2), 0, 255)
    hsv[:,:,2] = np.clip(hsv[:,:,2] * rng.uniform(0.8, 1.2), 0, 255)
    aug = cv2.cvtColor(hsv.astype(np.uint8), cv2.COLOR_HSV2BGR)

    # Gaussian blur (simulasi UAV motion blur)
    if rng.random() > 0.7:
        k = rng.choice([3, 5])
        aug = cv2.GaussianBlur(aug, (k, k), 0)

    # Resize ke 416x416 (sesuai model config)
    aug = cv2.resize(aug, (416, 416))

    return aug

def step5_augment():
    log("STEP 5: Augmentasi untuk mencapai target 1000 gambar/kelas...", "STEP")
    total_aug = 0

    for cls in CLASSES:
        dst = OUT_DIR / cls
        dst.mkdir(parents=True, exist_ok=True)

        existing_imgs = [f for f in dst.glob("*.jpg")]
        current = len(existing_imgs)
        needed  = max(0, TARGET - current)

        log(f"  {cls}: {current} ada, perlu +{needed} augmentasi", "INFO")

        if needed == 0:
            log(f"  {cls}: sudah cukup ✓", "OK")
            continue

        if len(existing_imgs) == 0:
            log(f"  {cls}: tidak ada gambar untuk augmentasi!", "WARN")
            continue

        aug_count = 0
        seed = 42
        while aug_count < needed:
            src_img_path = existing_imgs[aug_count % len(existing_imgs)]
            img = cv2.imread(str(src_img_path))
            if img is None: continue

            aug = augment_image(img, seed + aug_count)
            out_path = dst / f"aug_{cls}_{aug_count:06d}.jpg"
            if not out_path.exists():
                cv2.imwrite(str(out_path), aug, [cv2.IMWRITE_JPEG_QUALITY, 88])
                aug_count += 1

            if aug_count % 100 == 0:
                log(f"    {cls}: {aug_count}/{needed} augmentasi selesai...", "INFO")

        total_aug += aug_count
        log(f"  {cls}: +{aug_count} augmentasi → total {count_images(dst)}", "OK")

    log(f"Step 5 selesai: +{total_aug} gambar augmentasi", "OK")

# ── Step 6: Generate data.yaml ────────────────────────────────────────────────
def step6_generate_yaml():
    log("STEP 6: Generate data.yaml...", "STEP")

    # Split train/val/test (80/15/5)
    for cls in CLASSES:
        src = OUT_DIR / cls
        train_dir = OUT_DIR / "train" / cls
        val_dir   = OUT_DIR / "val"   / cls
        test_dir  = OUT_DIR / "test"  / cls
        train_dir.mkdir(parents=True, exist_ok=True)
        val_dir.mkdir(parents=True, exist_ok=True)
        test_dir.mkdir(parents=True, exist_ok=True)

        imgs = [f for f in src.glob("*.jpg")]
        random.shuffle(imgs)

        n = len(imgs)
        n_train = int(n * 0.80)
        n_val   = int(n * 0.15)

        for img in imgs[:n_train]:
            dst = train_dir / img.name
            if not dst.exists(): shutil.copy2(img, dst)
        for img in imgs[n_train:n_train+n_val]:
            dst = val_dir / img.name
            if not dst.exists(): shutil.copy2(img, dst)
        for img in imgs[n_train+n_val:]:
            dst = test_dir / img.name
            if not dst.exists(): shutil.copy2(img, dst)

        log(f"  {cls}: {n_train} train / {n_val} val / {n-n_train-n_val} test", "OK")

    # Generate data.yaml untuk YOLOv8 classification
    yaml_content = f"""# MoonHarvest Dataset v4 - Teknofest 2026
# 4 kelas crop health untuk monitoring sawah UAV
# Dibuat otomatis oleh dataset_collector.py

path: {OUT_DIR}
train: train
val: val
test: test

nc: 4
names:
  0: lush_green
  1: inconsistent_growth
  2: drought_severe_stress
  3: bare_soil

# Deskripsi kelas
# 0 - lush_green: Tanaman padi sehat, hijau merata, pertumbuhan optimal
# 1 - inconsistent_growth: Pertumbuhan tidak merata, beberapa area kurang baik
# 2 - drought_severe_stress: Kekeringan/stres berat, tanaman menguning/coklat
# 3 - bare_soil: Tanah kosong/terlihat, tidak ada tanaman
"""
    yaml_path = OUT_DIR / "data.yaml"
    yaml_path.write_text(yaml_content)
    log(f"  data.yaml ditulis ke: {yaml_path}", "OK")

    # Summary
    log("\n=== SUMMARY DATASET ===", "STEP")
    for cls in CLASSES:
        total_cls = count_images(OUT_DIR / cls)
        train_n   = count_images(OUT_DIR / "train" / cls)
        val_n     = count_images(OUT_DIR / "val"   / cls)
        test_n    = count_images(OUT_DIR / "test"  / cls)
        log(f"  {cls}: {total_cls} total ({train_n} train / {val_n} val / {test_n} test)", "OK")

# ── Main ──────────────────────────────────────────────────────────────────────
def main():
    log("=" * 60, "STEP")
    log("MoonHarvest Dataset Collector v1.0", "STEP")
    log(f"Target: {TARGET} gambar per kelas", "STEP")
    log(f"Output: {OUT_DIR}", "STEP")
    log("=" * 60, "STEP")

    step1_copy_existing()
    step2_extract_video_frames()
    step3_kaggle()
    step4_roboflow()
    step5_augment()
    step6_generate_yaml()

    log("\n=== SELESAI ===", "OK")
    log(f"Dataset tersimpan di: {OUT_DIR}", "OK")

if __name__ == "__main__":
    main()
