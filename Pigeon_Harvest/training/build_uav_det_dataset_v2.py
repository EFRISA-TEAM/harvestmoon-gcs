#!/usr/bin/env python3
"""
MoonHarvest — Build UAV Detection Dataset v2 (4 Classes)
=========================================================
Ekstraksi frame resolusi 640x640 dari rekaman drone asli dan pembuatan anotasi
YOLOv8 Detection (4 Kelas) secara objektif berbasis indeks spektral dan tekstur spasial.

Kelas Target:
  0: Lush Green              (Kanopi hijau lebat & sehat)
  1: Inconsistent Growth     (Pertumbuhan tidak merata, anakan muda, stres ringan, gulma)
  2: Drought / Severe Stress (Kekeringan parah, jerami terbakar, klorosis tinggi)
  3: Bare Soil / Gap         (Lahan kosong, tanah retak, pematang, celah tanam)
"""

import os, sys, time, random
from pathlib import Path
import cv2
import numpy as np

VIDEOS = [
    {
        "name": "15d_sawah_muda",
        "path": "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/sumber_rekaman_asli/15d_asli_sawah_15hst.mp4",
        "step": 2,      # 465 frames -> ~232 frames
    },
    {
        "name": "ydx_burned_kering",
        "path": "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/sumber_rekaman_asli/YDX_burned_asli_lahan_kering.mp4",
        "step": 2,      # 1051 frames -> ~525 frames
    },
    {
        "name": "ydxj_irigasi_subur",
        "path": "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/sumber_rekaman_asli/YDXJ0012_asli_vegetasi_irigasi_full.mp4",
        "step": 8,      # 5744 frames -> ~718 frames
    },
    {
        "name": "multisector_transek",
        "path": "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/stream_multisector_60s.mp4",
        "step": 3,      # 900 frames -> ~300 frames
    },
]

OUTPUT_DIR = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/moonharvest-uav/uav_det_4cls_v2")
TARGET_SIZE = 640
VAL_RATIO   = 0.20
SEED        = 42

CLASS_NAMES = [
    "Lush Green",
    "Inconsistent Growth",
    "Drought / Severe Stress",
    "Bare Soil / Gap"
]

def extract_boxes_from_frame(frame, target_w=640, target_h=640):
    img = cv2.resize(frame, (target_w, target_h), interpolation=cv2.INTER_AREA)
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    H, S, V = hsv[:,:,0], hsv[:,:,1], hsv[:,:,2]
    
    b, g, r = cv2.split(img.astype(np.float32))
    tot = b + g + r + 1e-6
    exg = (2*g - r - b) / tot
    exr = (1.4*r - g) / tot

    masks = {}
    # Class 0: Lush Green (Healthy dense canopy, optimal chlorophyll)
    masks[0] = (H >= 34) & (H <= 90) & (S >= 32) & (V >= 40) & (exg >= 0.035)
    
    # Class 1: Inconsistent Growth (Tillering / Pale / Uneven / Weeds)
    masks[1] = (((H >= 18) & (H < 34) & (S >= 22)) | 
                ((H >= 34) & (H <= 92) & (S >= 20) & (exg >= 0.008) & (exg < 0.035))) & ~masks[0]
                
    # Class 2: Drought / Severe Stress (Scorched / Dried vegetation / High chlorosis)
    masks[2] = (((H >= 6) & (H <= 26) & (S >= 28) & (V >= 40) & (exg < 0.025)) | 
                ((exr > 0.11) & (S >= 25) & (exg < 0.015))) & ~masks[0] & ~masks[1]
                
    # Class 3: Bare Soil / Gap (Unplanted soil, bunds, pathways, dry mud, row gaps)
    masks[3] = (exg < 0.018) & ~masks[0] & ~masks[1] & ~masks[2]

    all_boxes = [] # (cid, cx, cy, nw, nh, score)
    k_small = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))
    k_close = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7))

    for cid in [0, 1, 2, 3]:
        m = masks[cid].astype(np.uint8)
        m = cv2.morphologyEx(m, cv2.MORPH_OPEN, k_small)
        m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, k_close)

        n, labels, stats, centroids = cv2.connectedComponentsWithStats(m, 8)
        cls_boxes = []
        cls_scores = []
        for i in range(1, n):
            area = stats[i, cv2.CC_STAT_AREA]
            if area < 100:
                continue
            x = stats[i, cv2.CC_STAT_LEFT]
            y = stats[i, cv2.CC_STAT_TOP]
            bw = stats[i, cv2.CC_STAT_WIDTH]
            bh = stats[i, cv2.CC_STAT_HEIGHT]

            # Tile large homogeneous regions into natural bounding boxes
            tile_size = 140
            step_size = 100
            if bw > 220 or bh > 220:
                for ty in range(y, y + bh, step_size):
                    for tx in range(x, x + bw, step_size):
                        bx1, by1 = tx, ty
                        bx2, by2 = min(tx + tile_size, x + bw), min(ty + tile_size, y + bh)
                        w_box = bx2 - bx1
                        h_box = by2 - by1
                        if w_box >= 36 and h_box >= 36:
                            sub_mask = m[by1:by2, bx1:bx2]
                            purity = sub_mask.sum() / float(w_box * h_box)
                            if purity >= 0.25:
                                cls_boxes.append([bx1, by1, w_box, h_box])
                                cls_scores.append(float(purity))
            else:
                purity = area / float(bw * bh)
                if purity >= 0.20 and bw >= 22 and bh >= 22:
                    cls_boxes.append([x, y, bw, bh])
                    cls_scores.append(float(purity))

        if cls_boxes:
            indices = cv2.dnn.NMSBoxes(cls_boxes, cls_scores, score_threshold=0.20, nms_threshold=0.35)
            if len(indices) > 0:
                for idx in indices.flatten():
                    bx, by, bw, bh = cls_boxes[idx]
                    cx = (bx + bw / 2.0) / target_w
                    cy = (by + bh / 2.0) / target_h
                    nw = bw / float(target_w)
                    nh = bh / float(target_h)
                    cx = float(np.clip(cx, 0.001, 0.999))
                    cy = float(np.clip(cy, 0.001, 0.999))
                    nw = float(np.clip(nw, 0.010, 0.999))
                    nh = float(np.clip(nh, 0.010, 0.999))
                    all_boxes.append((cid, cx, cy, nw, nh, cls_scores[idx]))

    return img, all_boxes

def main():
    print("=" * 68)
    print("  MOONHARVEST — GENERATE UAV DETECTION DATASET v2 (4 CLASSES)")
    print("=" * 68)

    for split in ["train", "val"]:
        (OUTPUT_DIR / "images" / split).mkdir(parents=True, exist_ok=True)
        (OUTPUT_DIR / "labels" / split).mkdir(parents=True, exist_ok=True)

    random.seed(SEED)
    total_imgs_written = {"train": 0, "val": 0}
    class_counts = {"train": {0: 0, 1: 0, 2: 0, 3: 0},
                    "val":   {0: 0, 1: 0, 2: 0, 3: 0}}

    t_start = time.time()

    for v_info in VIDEOS:
        v_name = v_info["name"]
        v_path = v_info["path"]
        v_step = v_info["step"]

        if not os.path.exists(v_path):
            print(f"[WARN] Video tidak ditemukan: {v_path}, lewati...")
            continue

        cap = cv2.VideoCapture(v_path)
        total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
        print(f"\n[*] Memproses: {v_name} ({total_frames} frames, step={v_step})")

        frame_indices = list(range(0, total_frames, v_step))
        random.shuffle(frame_indices)
        n_val = max(1, int(len(frame_indices) * VAL_RATIO))
        val_set = set(frame_indices[:n_val])

        cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
        idx = 0
        written_video = 0

        while True:
            ret, frame = cap.read()
            if not ret or frame is None:
                break
            
            if idx in frame_indices:
                img_resized, boxes = extract_boxes_from_frame(frame, TARGET_SIZE, TARGET_SIZE)
                if len(boxes) >= 4:
                    split = "val" if idx in val_set else "train"
                    file_stem = f"{v_name}_f{idx:05d}"
                    img_file = OUTPUT_DIR / "images" / split / f"{file_stem}.jpg"
                    lbl_file = OUTPUT_DIR / "labels" / split / f"{file_stem}.txt"

                    cv2.imwrite(str(img_file), img_resized, [cv2.IMWRITE_JPEG_QUALITY, 90])

                    with open(lbl_file, "w") as f:
                        for b in boxes:
                            cid, cx, cy, nw, nh, _ = b
                            f.write(f"{cid} {cx:.6f} {cy:.6f} {nw:.6f} {nh:.6f}\n")
                            class_counts[split][cid] += 1

                    total_imgs_written[split] += 1
                    written_video += 1

            idx += 1

        cap.release()
        print(f"    ✓ Berhasil mengekstrak {written_video} frame berlabel dari {v_name}")

    yaml_content = f"""# MoonHarvest UAV 4-Class Object Detection Dataset v2
# Auto-generated from authentic aerial flight videos (15 HST, Irrigated, Burned/Drought, Multisector)

path: {OUTPUT_DIR}
train: images/train
val: images/val

nc: 4
names: {CLASS_NAMES}
"""
    yaml_path = OUTPUT_DIR / "data.yaml"
    with open(yaml_path, "w") as f:
        f.write(yaml_content)

    dur = time.time() - t_start
    print("\n" + "=" * 68)
    print(f"  SELESAI DALAM {dur:.1f} DETIK")
    print("=" * 68)
    print(f"Lokasi Dataset: {OUTPUT_DIR}")
    print(f"Total Gambar  : Train = {total_imgs_written['train']}, Val = {total_imgs_written['val']}")
    print("\nDistribusi Bounding Box:")
    for split in ["train", "val"]:
        tot = sum(class_counts[split].values())
        print(f"  [{split.upper()}] Total: {tot} boxes")
        for cid, name in enumerate(CLASS_NAMES):
            cnt = class_counts[split][cid]
            pct = (cnt / tot * 100) if tot > 0 else 0
            print(f"    - {name:<26}: {cnt:>6} boxes ({pct:.1f}%)")

if __name__ == "__main__":
    main()
