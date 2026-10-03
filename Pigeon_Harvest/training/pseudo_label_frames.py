"""
MoonHarvest — Pseudo-label frame UAV menggunakan model existing
Pakai moonharvest-uav-det.onnx untuk deteksi crop/weed pada 122 frame UAV
Output: label YOLO format (.txt) di detector/labels/train/ dan images di detector/images/train/

Usage:
  python pseudo_label_frames.py [--conf 0.25] [--split val]
"""

import argparse
import shutil
from pathlib import Path

import cv2
import numpy as np

# ── Path ─────────────────────────────────────────────────────────────────────
MODEL_PATH  = Path("/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/model/moonharvest-uav-det.onnx")
FRAMES_DIR  = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/moonharvest-uav/raw_uav_frames")
BASE        = Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/datasets/moonharvest-uav/detector")

# ── Kelas detector ────────────────────────────────────────────────────────────
CLASSES = {0: "crop", 1: "weed"}
INPUT_SIZE = 416  # model ini pakai 416x416


def letterbox(img: np.ndarray, new_shape: int = 416):
    """Resize + pad ke square sambil jaga aspect ratio."""
    h, w = img.shape[:2]
    scale = new_shape / max(h, w)
    new_w, new_h = int(w * scale), int(h * scale)
    img = cv2.resize(img, (new_w, new_h), interpolation=cv2.INTER_LINEAR)

    pad_w = new_shape - new_w
    pad_h = new_shape - new_h
    top, bottom = pad_h // 2, pad_h - pad_h // 2
    left, right = pad_w // 2, pad_w - pad_w // 2

    img = cv2.copyMakeBorder(img, top, bottom, left, right,
                             cv2.BORDER_CONSTANT, value=(114, 114, 114))
    return img, scale, left, top


def postprocess(output: np.ndarray, orig_h: int, orig_w: int,
                scale: float, pad_l: int, pad_t: int,
                conf_thresh: float = 0.25, iou_thresh: float = 0.45):
    """
    Proses output model moonharvest-uav-det.onnx
    Output shape: [1, 8, 3549]
    Format: [batch, 4_bbox + 1_obj + 2_cls + 1_extra, anchors]
    3549 = 13*13 + 26*26 + 52*52 (single anchor per cell, input 416)
    """
    pred = output[0]  # [8, 3549]
    pred = pred.T     # [3549, 8]

    # cx, cy, w, h sudah dalam piksel input (416x416)
    cx_raw = pred[:, 0]
    cy_raw = pred[:, 1]
    w_raw  = pred[:, 2]
    h_raw  = pred[:, 3]
    obj    = pred[:, 4]   # objectness score
    cls    = pred[:, 5:7] # 2 class scores (crop, weed)

    # Confidence = objectness * class_score
    cls_conf = 1 / (1 + np.exp(-cls))   # sigmoid
    obj_conf = 1 / (1 + np.exp(-obj))   # sigmoid
    scores   = obj_conf[:, None] * cls_conf  # [3549, 2]

    max_scores = scores.max(axis=1)
    mask = max_scores > conf_thresh
    if not mask.any():
        return []

    cx_f = cx_raw[mask]
    cy_f = cy_raw[mask]
    w_f  = w_raw[mask]
    h_f  = h_raw[mask]
    scores_f   = scores[mask]
    class_ids  = scores_f.argmax(axis=1)
    confidences = scores_f.max(axis=1)

    # cx,cy,w,h dalam piksel input → x1,y1,x2,y2
    x1 = cx_f - w_f / 2
    y1 = cy_f - h_f / 2
    x2 = cx_f + w_f / 2
    y2 = cy_f + h_f / 2

    # NMS
    indices = cv2.dnn.NMSBoxes(
        bboxes=np.stack([x1, y1, x2 - x1, y2 - y1], axis=1).tolist(),
        scores=confidences.tolist(),
        score_threshold=conf_thresh,
        nms_threshold=iou_thresh,
    )
    if len(indices) == 0:
        return []

    results = []
    for i in indices.flatten():
        # Hilangkan letterbox padding lalu rescale ke gambar asli
        bx1 = (x1[i] - pad_l) / scale
        by1 = (y1[i] - pad_t) / scale
        bx2 = (x2[i] - pad_l) / scale
        by2 = (y2[i] - pad_t) / scale

        bx1 = max(0.0, min(float(bx1), orig_w))
        by1 = max(0.0, min(float(by1), orig_h))
        bx2 = max(0.0, min(float(bx2), orig_w))
        by2 = max(0.0, min(float(by2), orig_h))

        # YOLO format: normalized cx,cy,w,h
        cx = ((bx1 + bx2) / 2) / orig_w
        cy = ((by1 + by2) / 2) / orig_h
        bw = (bx2 - bx1) / orig_w
        bh = (by2 - by1) / orig_h

        if bw > 0.001 and bh > 0.001:
            results.append((int(class_ids[i]), cx, cy, bw, bh, float(confidences[i])))

    return results


def run(conf_thresh: float = 0.25, split: str = "train"):
    import onnxruntime as ort

    img_dst = BASE / "images" / split
    lbl_dst = BASE / "labels" / split
    img_dst.mkdir(parents=True, exist_ok=True)
    lbl_dst.mkdir(parents=True, exist_ok=True)

    print(f"Model  : {MODEL_PATH}")
    print(f"Frames : {FRAMES_DIR}")
    print(f"Output : {img_dst}")
    print(f"Conf   : {conf_thresh}\n")

    sess = ort.InferenceSession(str(MODEL_PATH),
                                providers=["CUDAExecutionProvider", "CPUExecutionProvider"])
    input_name = sess.get_inputs()[0].name
    print(f"Session ready. Input: {input_name}\n")

    frames = sorted(FRAMES_DIR.glob("*.jpg"))
    if not frames:
        print(f"Tidak ada frame di {FRAMES_DIR}")
        return

    total_det = 0
    total_img = 0
    skipped   = 0

    for frame_path in frames:
        img_bgr = cv2.imread(str(frame_path))
        if img_bgr is None:
            print(f"  [SKIP] Cannot read {frame_path.name}")
            skipped += 1
            continue

        orig_h, orig_w = img_bgr.shape[:2]
        img_lb, scale, pad_l, pad_t = letterbox(img_bgr, INPUT_SIZE)

        # Preprocess: BGR→RGB, normalize, HWC→CHW
        inp = cv2.cvtColor(img_lb, cv2.COLOR_BGR2RGB).astype(np.float32) / 255.0
        inp = np.transpose(inp, (2, 0, 1))[np.newaxis]  # [1,3,640,640]

        outputs = sess.run(None, {input_name: inp})
        detections = postprocess(outputs[0], orig_h, orig_w,
                                 scale, pad_l, pad_t, conf_thresh)

        # Copy gambar ke folder target
        dst_img = img_dst / frame_path.name
        shutil.copy2(frame_path, dst_img)

        # Tulis label
        dst_lbl = lbl_dst / (frame_path.stem + ".txt")
        with open(dst_lbl, "w") as f:
            for cls_id, cx, cy, bw, bh, conf in detections:
                f.write(f"{cls_id} {cx:.6f} {cy:.6f} {bw:.6f} {bh:.6f}\n")

        n = len(detections)
        total_det += n
        total_img += 1

        cls_str = ", ".join(f"{CLASSES[d[0]]}:{d[5]:.2f}" for d in detections[:3])
        print(f"  {frame_path.name}: {n} deteksi [{cls_str}{'...' if n > 3 else ''}]")

    print(f"\n{'='*60}")
    print(f"Selesai: {total_img} frame diproses, {skipped} dilewati")
    print(f"Total deteksi: {total_det}")
    print(f"Rata-rata deteksi/frame: {total_det/max(total_img,1):.1f}")
    print(f"\nOutput:")
    print(f"  Images : {img_dst} ({total_img} files)")
    print(f"  Labels : {lbl_dst} ({total_img} files)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Pseudo-label UAV frames dengan moonharvest-uav-det.onnx")
    parser.add_argument("--conf",  type=float, default=0.25, help="Confidence threshold (default: 0.25)")
    parser.add_argument("--split", type=str,   default="train", choices=["train", "val"],
                        help="Split tujuan: train atau val (default: train)")
    args = parser.parse_args()
    run(conf_thresh=args.conf, split=args.split)
