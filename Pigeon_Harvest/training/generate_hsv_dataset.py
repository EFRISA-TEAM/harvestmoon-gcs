#!/usr/bin/env python3
"""
generate_hsv_dataset.py — Generate YOLO-format detection dataset from HSV segmentation.

Distillation: HSV → YOLO labels
Uses HSV pipeline on all videos to produce pseudo-labels for training.

Dataset structure:
  hsv_det_4cls/
    images/train/
    images/val/
    labels/train/
    labels/val/
    data.yaml
"""

import os, sys, cv2, numpy as np, json, random, time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from detect_hsv_pipeline import segment_regions, nms, HSV_CFG

VIDEOS = [
    "/home/fawwazfa/Program/Harvestmoon/vid/15d.mp4",
    "/home/fawwazfa/Program/Harvestmoon/vid/YDX_burned.mp4",
    "/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo.mp4",
    "/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo(1).mp4",
]

DATASET_ROOT = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/moonharvest-uav/hsv_det_4cls"
IMG_DIR      = DATASET_ROOT + "/images"
LABEL_DIR    = DATASET_ROOT + "/labels"

HSV_CLASSES = ["Lush Green", "Inconsistent Growth", "Drought / Severe Stress", "Bare Soil / Gap"]

VAL_RATIO    = 0.2
STEP         = 5      # 1 frame per 5th frame (lebih banyak data)
SEED         = 42


def bbox_to_yolo(bbox, img_w, img_h):
    x1, y1, x2, y2 = bbox
    cx = ((x1 + x2) / 2.0) / img_w
    cy = ((y1 + y2) / 2.0) / img_h
    w  = (x2 - x1) / img_w
    h  = (y2 - y1) / img_h
    return np.clip(cx, 0, 1), np.clip(cy, 0, 1), np.clip(w, 0, 1), np.clip(h, 0, 1)


def class_to_idx(cls_name):
    return HSV_CLASSES.index(cls_name)


def process_video(video_path, img_dir, label_dir, val_ratio=VAL_RATIO, seed=SEED):
    name = Path(video_path).stem
    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        print(f"  [SKIP] {name}")
        return 0

    total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    w     = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    h     = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))

    frame_indices = list(range(0, total, STEP))
    random.seed(seed)
    random.shuffle(frame_indices)
    val_count = max(1, int(len(frame_indices) * val_ratio))
    val_set   = set(frame_indices[:val_count])
    train_set = set(frame_indices[val_count:])

    print(f"  {name}: {total} frames → {len(train_set)} train + {len(val_set)} val")

    frame_idx = 0
    written   = 0
    t0        = time.time()

    while True:
        ret, frame = cap.read()
        if not ret:
            break
        frame_idx += 1
        if (frame_idx - 1) not in frame_indices:
            continue

        try:
            regions = segment_regions(frame, HSV_CFG)
            dets = [{"bbox": r["bbox"], "class": r["hsv_display"],
                     "conf": 0.60, "area": r["area"], "source": "hsv-only"}
                    for r in regions]
            dets = nms(dets)
        except Exception:
            continue

        if not dets:
            continue

        is_val = (frame_idx - 1) in val_set
        split  = "val" if is_val else "train"
        idx    = frame_idx - 1
        fname  = f"{name}_{idx:06d}"

        img_dir_split = os.path.join(img_dir, split)
        lbl_dir_split = os.path.join(label_dir, split)
        os.makedirs(img_dir_split, exist_ok=True)
        os.makedirs(lbl_dir_split, exist_ok=True)

        img_path = os.path.join(img_dir_split, fname + ".jpg")
        cv2.imwrite(img_path, frame, [cv2.IMWRITE_JPEG_QUALITY, 92])

        label_path = os.path.join(lbl_dir_split, fname + ".txt")
        with open(label_path, "w") as f:
            for d in dets:
                cls_idx = class_to_idx(d["class"])
                cx, cy, bw, bh = bbox_to_yolo(d["bbox"], w, h)
                f.write(f"{cls_idx} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}\n")

        written += 1
        if written % 100 == 0:
            elapsed = time.time() - t0
            print(f"  {written} written | elapsed={elapsed:.0f}s")

    cap.release()
    elapsed = time.time() - t0
    print(f"  Selesai: {written} images in {elapsed:.1f}s")
    return written


def create_data_yaml(dataset_root):
    yaml_path = os.path.join(dataset_root, "data.yaml")
    content = f"""# MoonHarvest HSV Detection — 4 Classes
# Distilled from HSV segmentation pipeline
# Model trained to reproduce HSV bounding boxes

path: {dataset_root}
train: images/train
val: images/val

nc: {len(HSV_CLASSES)}
names: {json.dumps(HSV_CLASSES)}
"""
    with open(yaml_path, "w") as f:
        f.write(content)
    print(f"  data.yaml dibuat: {yaml_path}")


def count_labels(label_dir):
    counts = {}
    total  = 0
    per_split = {"train": 0, "val": 0}
    for split in ["train", "val"]:
        split_dir = os.path.join(label_dir, split)
        if not os.path.exists(split_dir):
            continue
        for fname in os.listdir(split_dir):
            if not fname.endswith(".txt"):
                continue
            with open(os.path.join(split_dir, fname)) as f:
                for line in f:
                    parts = line.strip().split()
                    if len(parts) >= 5:
                        cls = int(parts[0])
                        counts[cls] = counts.get(cls, 0) + 1
                        total += 1
                        per_split[split] += 1
    return counts, total, per_split


if __name__ == "__main__":
    print("=" * 60)
    print("MoonHarvest HSV Dataset Generator")
    print("=" * 60)
    print(f"Videos     : {len(VIDEOS)}")
    print(f"Step       : every {STEP} frame")
    print(f"Output     : {DATASET_ROOT}")
    print(f"Classes    : {HSV_CLASSES}")
    print("=" * 60)

    os.makedirs(DATASET_ROOT, exist_ok=True)
    total = 0
    for video_path in VIDEOS:
        total += process_video(video_path, IMG_DIR, LABEL_DIR)

    create_data_yaml(DATASET_ROOT)
    counts, total_labels, per_split = count_labels(LABEL_DIR)

    print(f"\nTotal images  : {total}")
    print(f"Total labels  : {total_labels}")
    print(f"  Train: {per_split['train']}, Val: {per_split['val']}")
    for cls_idx, cls_name in enumerate(HSV_CLASSES):
        print(f"  [{cls_idx}] {cls_name}: {counts.get(cls_idx, 0)} boxes")
