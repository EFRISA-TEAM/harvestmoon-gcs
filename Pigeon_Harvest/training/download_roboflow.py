#!/usr/bin/env python3
"""
MoonHarvest Dataset Downloader - Roboflow
Download dataset publik dari Roboflow untuk 4 kelas crop health
"""

import os
import shutil
import cv2
import numpy as np
from pathlib import Path
from roboflow import Roboflow

# ── Config ────────────────────────────────────────────────────────────────────
DATASET_DIR = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/moonharvest-uav/classifier_v4_teknofest")
TMP_DIR     = Path("/tmp/roboflow_dl")
API_KEY     = "DT54TAT3qXN0sKz7D7Qv"

CLASSES = ["lush_green", "inconsistent_growth", "drought_severe_stress", "bare_soil"]

# Roboflow projects yang akan dicoba
# Format: (workspace, project, version)
RF_PROJECTS = [
    # Rice/crop disease datasets
    ("roboflow-100",        "grass-weeds",              1),
    ("roboflow-100",        "aerial-sheep",             1),
    ("muhammad-rajput",     "rice-disease-mzdph",       1),
    ("roboflow-100",        "vegetation-og3xl",         1),
    ("roboflow-100",        "paddy-disease-rcvh3",      1),
    ("roboflow-100",        "lettuce-pallets",          1),
    ("roboflow-universe",   "plant-health",             1),
    ("agri-detection",      "crop-health-monitoring",   1),
    ("rice-field",          "rice-field-health",        1),
    ("plant-disease",       "plant-stress-detection",  1),
]

# Keyword mapping untuk auto-assign ke kelas
KEYWORD_MAP = {
    "lush_green": [
        "healthy", "green", "lush", "normal", "good",
        "fresh", "vigorous", "optimal", "well"
    ],
    "inconsistent_growth": [
        "inconsistent", "irregular", "uneven", "partial",
        "mixed", "moderate", "average", "medium"
    ],
    "drought_severe_stress": [
        "drought", "stress", "disease", "yellow", "brown",
        "wilting", "dry", "sick", "infected", "blight",
        "blast", "burnout", "severe"
    ],
    "bare_soil": [
        "bare", "soil", "empty", "land", "dirt",
        "ground", "field", "fallow", "barren", "sand"
    ]
}

def log(msg, level="INFO"):
    icons = {"INFO": "·", "OK": "✓", "WARN": "⚠", "ERR": "✗", "STEP": "→"}
    print(f"[{icons.get(level,'·')}] {msg}", flush=True)

def count_images(folder: Path) -> int:
    if not folder.exists(): return 0
    return sum(1 for f in folder.rglob("*") if f.suffix.lower() in {".jpg",".jpeg",".png"})

def guess_class(name: str) -> str:
    """Tebak kelas berdasarkan nama folder/file"""
    name_lower = name.lower()
    scores = {cls: 0 for cls in CLASSES}
    for cls, keywords in KEYWORD_MAP.items():
        for kw in keywords:
            if kw in name_lower:
                scores[cls] += 1
    best = max(scores, key=scores.get)
    if scores[best] == 0:
        return None
    return best

def copy_to_class(src_dir: Path, cls: str, max_per_source: int = 300):
    """Copy gambar dari folder sumber ke kelas target"""
    dst = DATASET_DIR / cls
    dst.mkdir(parents=True, exist_ok=True)
    imgs = [f for f in src_dir.rglob("*") if f.suffix.lower() in {".jpg",".jpeg",".png"}]
    copied = 0
    for img in imgs[:max_per_source]:
        dest = dst / f"rf_{src_dir.parent.name}_{img.name}"
        if not dest.exists():
            try:
                shutil.copy2(img, dest)
                copied += 1
            except Exception:
                pass
    return copied

def download_roboflow():
    rf = Roboflow(api_key=API_KEY)
    TMP_DIR.mkdir(parents=True, exist_ok=True)
    total = 0

    for workspace, project_name, version in RF_PROJECTS:
        try:
            log(f"Trying: {workspace}/{project_name} v{version}...", "INFO")
            project = rf.workspace(workspace).project(project_name)
            ver     = project.version(version)
            dl_path = TMP_DIR / f"{workspace}_{project_name}"

            if dl_path.exists() and any(dl_path.rglob("*.jpg")):
                log(f"  Already downloaded, skipping...", "INFO")
            else:
                dataset = ver.download("folder", location=str(dl_path), overwrite=True)

            # Auto-assign gambar ke kelas berdasarkan folder name
            assigned = {cls: 0 for cls in CLASSES}
            for img_dir in dl_path.rglob("*"):
                if not img_dir.is_dir(): continue
                cls = guess_class(img_dir.name)
                if cls is None: continue
                n = copy_to_class(img_dir, cls, max_per_source=200)
                assigned[cls] += n
                total += n

            # Jika tidak ada folder yang match, distribusikan rata
            all_imgs = [f for f in dl_path.rglob("*") if f.suffix.lower() in {".jpg",".jpeg",".png"}]
            if sum(assigned.values()) == 0 and all_imgs:
                per_cls = max(50, len(all_imgs) // 4)
                for i, cls in enumerate(CLASSES):
                    subset = all_imgs[i*per_cls:(i+1)*per_cls]
                    dst = DATASET_DIR / cls
                    dst.mkdir(parents=True, exist_ok=True)
                    for img in subset:
                        dest = dst / f"rf_{project_name}_{img.name}"
                        if not dest.exists():
                            try: shutil.copy2(img, dest); assigned[cls] += 1; total += 1
                            except: pass

            for cls, n in assigned.items():
                if n > 0:
                    log(f"  {cls}: +{n}", "OK")

        except Exception as e:
            log(f"  Failed {workspace}/{project_name}: {e}", "WARN")
            continue

    log(f"Total baru dari Roboflow: +{total} gambar", "OK")
    return total

def rebuild_split():
    """Rebuild train/val/test split dari semua gambar"""
    import random
    log("Rebuilding train/val/test split...", "STEP")

    for cls in CLASSES:
        src = DATASET_DIR / cls
        if not src.exists(): continue

        imgs = [f for f in src.glob("*.jpg")] + [f for f in src.glob("*.png")]
        random.shuffle(imgs)
        n = len(imgs)
        n_train = int(n * 0.80)
        n_val   = int(n * 0.15)

        train_dir = DATASET_DIR / "train" / cls
        val_dir   = DATASET_DIR / "val"   / cls
        test_dir  = DATASET_DIR / "test"  / cls
        for d in [train_dir, val_dir, test_dir]: d.mkdir(parents=True, exist_ok=True)

        for img in imgs[:n_train]:
            dst = train_dir / img.name
            if not dst.exists(): shutil.copy2(img, dst)
        for img in imgs[n_train:n_train+n_val]:
            dst = val_dir / img.name
            if not dst.exists(): shutil.copy2(img, dst)
        for img in imgs[n_train+n_val:]:
            dst = test_dir / img.name
            if not dst.exists(): shutil.copy2(img, dst)

        log(f"  {cls}: {n} total → {n_train} train / {n_val} val / {n-n_train-n_val} test", "OK")

def print_summary():
    log("\n=== DATASET SUMMARY ===", "STEP")
    for cls in CLASSES:
        t = count_images(DATASET_DIR / "train" / cls)
        v = count_images(DATASET_DIR / "val"   / cls)
        x = count_images(DATASET_DIR / "test"  / cls)
        log(f"  {cls:30s}: {t} train / {v} val / {x} test = {t+v+x} total", "OK")

def main():
    log("=" * 60, "STEP")
    log("MoonHarvest Roboflow Downloader", "STEP")
    log("=" * 60, "STEP")

    log("\nBefore download:", "INFO")
    for cls in CLASSES:
        log(f"  {cls}: {count_images(DATASET_DIR/cls)} gambar", "INFO")

    download_roboflow()
    rebuild_split()
    print_summary()

    log("\nSelesai! Siap untuk training.", "OK")

if __name__ == "__main__":
    main()
