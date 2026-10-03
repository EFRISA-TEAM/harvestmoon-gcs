#!/usr/bin/env python3
"""Split the Kaggle crop-and-weed-detection dataset into YOLO train/val folders."""
import random
import shutil
from pathlib import Path

SRC = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/crop-weed/agri_data/data")
DST = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/crop-weed-yolo")

random.seed(42)

images = sorted(SRC.glob("*.jpeg"))
print(f"Found {len(images)} images")

pairs = []
for img in images:
    lbl = img.with_suffix(".txt")
    if lbl.exists():
        pairs.append((img, lbl))

print(f"Found {len(pairs)} image/label pairs")
random.shuffle(pairs)

split_idx = int(len(pairs) * 0.85)
train_pairs = pairs[:split_idx]
val_pairs = pairs[split_idx:]
print(f"Train: {len(train_pairs)}  Val: {len(val_pairs)}")

for split_name, split_pairs in [("train", train_pairs), ("val", val_pairs)]:
    img_dir = DST / "images" / split_name
    lbl_dir = DST / "labels" / split_name
    img_dir.mkdir(parents=True, exist_ok=True)
    lbl_dir.mkdir(parents=True, exist_ok=True)
    for img, lbl in split_pairs:
        shutil.copy(img, img_dir / img.name)
        shutil.copy(lbl, lbl_dir / lbl.name)

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
