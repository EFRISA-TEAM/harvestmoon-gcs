#!/usr/bin/env python3
"""Combine the Kaggle crop-and-weed-detection dataset (1300 imgs) with the
Roboflow-exported WeedCrop dataset (2822 imgs, same crop/weed classes) into
one larger YOLO train/val split."""
import random
import shutil
from pathlib import Path

SRC1 = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/crop-weed/agri_data/data")
SRC2_ROOT = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/weedcrop-extra/WeedCrop.v1i.yolov5pytorch")
DST = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/crop-weed-combined")

random.seed(42)

pairs = []

# Dataset 1: flat folder, agri_0_XXXX.jpeg + .txt
for img in sorted(SRC1.glob("*.jpeg")):
    lbl = img.with_suffix(".txt")
    if lbl.exists():
        pairs.append((img, lbl, "ds1"))

# Dataset 2: Roboflow export, already split train/valid/test -> merge all, we'll re-split ourselves
for split in ["train", "valid", "test"]:
    img_dir = SRC2_ROOT / split / "images"
    lbl_dir = SRC2_ROOT / split / "labels"
    for img in sorted(img_dir.glob("*.jpg")):
        lbl = lbl_dir / (img.stem + ".txt")
        if lbl.exists():
            pairs.append((img, lbl, "ds2"))

print(f"Total combined pairs: {len(pairs)}")
random.shuffle(pairs)

split_idx = int(len(pairs) * 0.85)
train_pairs = pairs[:split_idx]
val_pairs = pairs[split_idx:]
print(f"Train: {len(train_pairs)}  Val: {len(val_pairs)}")

if DST.exists():
    shutil.rmtree(DST)

for split_name, split_pairs in [("train", train_pairs), ("val", val_pairs)]:
    img_dir = DST / "images" / split_name
    lbl_dir = DST / "labels" / split_name
    img_dir.mkdir(parents=True, exist_ok=True)
    lbl_dir.mkdir(parents=True, exist_ok=True)
    for i, (img, lbl, tag) in enumerate(split_pairs):
        # Prefix filenames with source tag + index to avoid collisions across the two datasets
        new_name = f"{tag}_{i}{img.suffix}"
        new_lbl_name = f"{tag}_{i}.txt"
        shutil.copy(img, img_dir / new_name)
        shutil.copy(lbl, lbl_dir / new_lbl_name)

yaml_content = f"""path: {DST}
train: images/train
val: images/val
names:
  0: crop
  1: weed
"""
(DST / "data.yaml").write_text(yaml_content)
print("Wrote", DST / "data.yaml")
print(yaml_content)
