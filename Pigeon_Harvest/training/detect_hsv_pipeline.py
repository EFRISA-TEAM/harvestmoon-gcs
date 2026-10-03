#!/usr/bin/env python3
"""
MoonHarvest HSV-First Detection Pipeline — HSV Only (full)
Pipeline: WB+CLAHE → HSV segmentasi → connected-components → visualization → video output
"""

import argparse, os, sys, time
from pathlib import Path
import cv2
import numpy as np

# ── HSV CONFIG (dikalibrasi dari 15d.mp4) ────────────────────────
HSV_CFG = {
    "wb":          True,
    "clahe_clip":  2.0,
    "clahe_grid":  8,
    "shadow_v_max": 35,
    "exg_veg_thr":     0.004,
    "exg_healthy_min": 0.028,
    "lush":    {"h": [30, 90], "s_lo": 25, "v_lo": 50, "exg_min": 0.028},
    "stress":  {"h": [12, 105], "s_lo":  8, "s_hi": 80, "v_lo": 42, "exg_lo": 0.000, "exg_hi": 0.040},
    "drought": {"h": [ 0,  22], "s_lo": 12, "s_hi": 80, "v_lo": 90,  "exg_hi": 0.006, "exg_remap_thr": 0.010},
    "soil":    {"s_hi": 15, "v_lo": 75, "exg_hi": 0.008},
    "min_area_frac": 0.006,
    "max_area_frac": 0.20,
    "max_regions":   20,
    "morph_k":       7,
    "ema_alpha":     0.30,
}

COLORS = {
    "Lush Green":              ( 50, 210,  50),
    "Inconsistent Growth":     (  0, 200, 255),
    "Drought / Severe Stress": (  0,  90, 255),
    "Bare Soil / Gap":         (140, 140, 140),
}

SEVERITY = {
    "Lush Green":              0.00,
    "Inconsistent Growth":     0.45,
    "Drought / Severe Stress": 0.80,
    "Bare Soil / Gap":         0.10,
}

ONNX_MAP = {
    "lush_green":            "Lush Green",
    "inconsistent_growth":   "Inconsistent Growth",
    "drought_severe_stress": "Drought / Severe Stress",
    "bare_soil":             "Bare Soil / Gap",
}

ONNX_COMPAT = {
    "lush":    {"Lush Green"},
    "stress":  {"Lush Green", "Inconsistent Growth"},
    "drought": {"Drought / Severe Stress", "Inconsistent Growth"},
    "soil":    {"Bare Soil / Gap"},
}


def _wb(bgr):
    b, g, r = cv2.split(bgr.astype(np.float32))
    mb, mg, mr = b.mean()+1e-6, g.mean()+1e-6, r.mean()+1e-6
    k = (mb+mg+mr)/3.0
    return cv2.merge([np.clip(b*(k/mb),0,255), np.clip(g*(k/mg),0,255), np.clip(r*(k/mr),0,255)]).astype(np.uint8)

def _preprocess(bgr, cfg):
    out = _wb(bgr) if cfg["wb"] else bgr.copy()
    hsv = cv2.cvtColor(out, cv2.COLOR_BGR2HSV)
    h, s, v = cv2.split(hsv)
    cl = cv2.createCLAHE(clipLimit=cfg["clahe_clip"], tileGridSize=(cfg["clahe_grid"], cfg["clahe_grid"]))
    v  = cl.apply(v)
    return out, cv2.merge([h, s, v])

def _exg(bgr_f):
    tot = bgr_f.sum(axis=2) + 1e-6
    return (2*bgr_f[:,:,1] - bgr_f[:,:,0] - bgr_f[:,:,2]) / tot

def segment_regions(frame, cfg=HSV_CFG):
    h, w  = frame.shape[:2]
    scale = 0.5
    small = cv2.resize(frame, (int(w*scale), int(h*scale)))
    sh, sw = small.shape[:2]
    min_px = max(40, int(cfg["min_area_frac"] * sh * sw))
    proc, hsv_img = _preprocess(small, cfg)
    H   = hsv_img[:,:,0].astype(np.int32)
    S   = hsv_img[:,:,1].astype(np.int32)
    V   = hsv_img[:,:,2].astype(np.int32)
    exg = _exg(proc.astype(np.float32))
    shadow = V < cfg["shadow_v_max"]

    c = cfg["lush"]
    lush = ((H>=c["h"][0]) & (H<=c["h"][1]) & (S>=c["s_lo"]) & (V>=c["v_lo"]) & (exg>=c["exg_min"]) & ~shadow)
    c = cfg["stress"]
    stress = ((H>=c["h"][0]) & (H<=c["h"][1]) & (S>=c["s_lo"]) & (S<=c["s_hi"]) & (V>=c["v_lo"]) & (exg>=c["exg_lo"]) & (exg<c["exg_hi"]) & ~lush & ~shadow)
    c = cfg["drought"]
    drought = ((H>=c["h"][0]) & (H<=c["h"][1]) & (S>=c["s_lo"]) & (S<=c["s_hi"]) & (V>=c["v_lo"]) & (exg<c["exg_hi"]) & ~lush & ~stress & ~shadow)
    c = cfg["soil"]
    soil = ((S<=c["s_hi"]) & (V>=c["v_lo"]) & (exg<c["exg_hi"]) & ~lush & ~stress & ~drought & ~shadow)

    zones = [
        (lush,    "lush",    "Lush Green"),
        (stress,  "stress",  "Inconsistent Growth"),
        (drought, "drought", "Inconsistent Growth"),
        (soil,    "soil",    "Bare Soil / Gap"),
    ]

    k  = cfg["morph_k"] | 1
    k2 = max(3, k//2) | 1
    ke = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))
    ks = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k2, k2))
    max_px = int(cfg.get("max_area_frac", 1.0) * sh * sw)
    regions = []
    for mask_bool, zone_key, zone_disp in zones:
        mask = mask_bool.astype(np.uint8)
        mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN,  ks)
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, ke)
        n, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
        for i in range(1, n):
            area = int(stats[i, cv2.CC_STAT_AREA])
            if area < min_px or area > max_px:
                continue
            x  = int(stats[i, cv2.CC_STAT_LEFT]  / scale)
            y  = int(stats[i, cv2.CC_STAT_TOP]   / scale)
            bw = int(stats[i, cv2.CC_STAT_WIDTH]  / scale)
            bh = int(stats[i, cv2.CC_STAT_HEIGHT] / scale)
            x2, y2 = min(x+bw, w-1), min(y+bh, h-1)
            if x2 <= x or y2 <= y:
                continue
            actual_zone_disp = zone_disp
            actual_zone_key  = zone_key
            if zone_key == "drought":
                remap_thr = cfg["drought"].get("exg_remap_thr", 0.006)
                rx1 = int(stats[i, cv2.CC_STAT_LEFT])
                ry1 = int(stats[i, cv2.CC_STAT_TOP])
                rx2 = rx1 + int(stats[i, cv2.CC_STAT_WIDTH])
                ry2 = ry1 + int(stats[i, cv2.CC_STAT_HEIGHT])
                region_mask = (labels[ry1:ry2, rx1:rx2] == i)
                region_exg  = exg[ry1:ry2, rx1:rx2]
                mean_exg    = float(region_exg[region_mask].mean()) if region_mask.any() else 0.0
                if mean_exg > remap_thr:
                    actual_zone_disp = "Inconsistent Growth"
                    actual_zone_key  = "stress"
            regions.append({"bbox":(x,y,x2,y2), "hsv_zone":actual_zone_key, "hsv_display":actual_zone_disp, "area":area})
    regions.sort(key=lambda r: -r["area"])
    return regions[:cfg["max_regions"]]

def draw_stream(frame, dets, fhi):
    out = frame.copy()
    h, w = out.shape[:2]

    for d in dets:
        x1, y1, x2, y2 = d["bbox"]
        col = COLORS.get(d["class"], (200, 200, 200))
        # Rounded-corner effect: draw thick box + inner thin box
        cv2.rectangle(out, (x1, y1), (x2, y2), col, 2)
        cv2.rectangle(out, (x1+1, y1+1), (x2-1, y2-1), (0,0,0), 1)
        label = f"{d['class'][:18]}"
        (tw, th), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.48, 1)
        # Label background with alpha-like overlay
        lx1, ly1, lx2, ly2 = x1, max(0, y1 - th - 8), x1 + tw + 6, y1
        cv2.rectangle(out, (lx1, ly1), (lx2, ly2), col, -1)
        cv2.rectangle(out, (lx1, ly1), (lx2, ly2), (0,0,0), 1)
        cv2.putText(out, label, (x1 + 3, y1 - 4),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.48, (0, 0, 0), 1, cv2.LINE_AA)

    # FHI overlay — panel di pojok kiri atas
    if fhi is not None:
        fhi_color = (50, 210, 50) if fhi >= 70 else (0, 200, 255) if fhi >= 45 else (0, 90, 255)
        panel_w, panel_h = 260, 36
        overlay = out.copy()
        cv2.rectangle(overlay, (10, 10), (10 + panel_w, 10 + panel_h), (20, 20, 20), -1)
        cv2.addWeighted(overlay, 0.6, out, 0.4, 0, out)
        cv2.rectangle(out, (10, 10), (10 + panel_w, 10 + panel_h), fhi_color, 2)
        fhi_text = f"FHI: {fhi:.1f}%  |  dets: {len(dets)}"
        cv2.putText(out, fhi_text, (18, 10 + panel_h - 10),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.65, fhi_color, 2, cv2.LINE_AA)

    return out


def nms(dets, per_cls_iou=0.15, cross_iou=0.20, min_conf=0.28, min_area=600,
        contain_thr=0.75):
    """
    NMS dua-tahap:
      1. Per-class hard NMS (IoU thr ketat)
      2. Cross-class hard NMS (IoU thr lebih ketat)
      3. Containment suppression: hapus box kecil yang >contain_thr tertutup box lain
    """
    dets = [d for d in dets if d["conf"] >= min_conf and d.get("area", 0) >= min_area]
    if not dets: return []

    # Tahap 1: per-class NMS
    by_cls = {}
    for d in dets:
        by_cls.setdefault(d["class"], []).append(d)
    after_per_cls = []
    for lst in by_cls.values():
        after_per_cls.extend(_hard_nms(lst, per_cls_iou))

    # Tahap 2: cross-class NMS
    after_cross = _hard_nms(after_per_cls, cross_iou)

    # Tahap 3: containment suppression
    return _containment_suppression(after_cross, contain_thr)


def _hard_nms(dets, iou_thr):
    if not dets: return []
    # Sort by area descending (larger box = higher priority for HSV segmentation)
    dets = sorted(dets, key=lambda d: -d.get("area", 0))
    boxes = np.array([d["bbox"] for d in dets], np.float32)
    x1, y1, x2, y2 = boxes[:,0], boxes[:,1], boxes[:,2], boxes[:,3]
    a = np.maximum(0, x2-x1) * np.maximum(0, y2-y1)
    ix1 = np.maximum(x1[:,None], x1[None,:])
    iy1 = np.maximum(y1[:,None], y1[None,:])
    ix2 = np.minimum(x2[:,None], x2[None,:])
    iy2 = np.minimum(y2[:,None], y2[None,:])
    inter = np.maximum(0, ix2-ix1) * np.maximum(0, iy2-iy1)
    iou = inter / (a[:,None] + a[None,:] - inter + 1e-6)
    suppressed = np.zeros(len(dets), bool)
    keep = []
    for i in range(len(dets)):
        if suppressed[i]: continue
        keep.append(i)
        for j in range(i+1, len(dets)):
            if iou[i, j] > iou_thr:
                suppressed[j] = True
    return [dets[i] for i in keep]


def _containment_suppression(dets, thr=0.75):
    """Hapus box kecil yang >thr area-nya tertutup oleh box lain."""
    if len(dets) < 2: return dets
    boxes = np.array([d["bbox"] for d in dets], np.float32)
    x1, y1, x2, y2 = boxes[:,0], boxes[:,1], boxes[:,2], boxes[:,3]
    a = np.maximum(0, x2-x1) * np.maximum(0, y2-y1)
    ix1 = np.maximum(x1[:,None], x1[None,:])
    iy1 = np.maximum(y1[:,None], y1[None,:])
    ix2 = np.minimum(x2[:,None], x2[None,:])
    iy2 = np.minimum(y2[:,None], y2[None,:])
    inter = np.maximum(0, ix2-ix1) * np.maximum(0, iy2-iy1)
    # overlap ratio relative to each box's own area
    overlap = inter / (a[:,None] + 1e-6)
    suppressed = np.zeros(len(dets), bool)
    for i in range(len(dets)):
        for j in range(len(dets)):
            if i == j: continue
            # if box i is mostly inside box j, suppress i
            if overlap[i, j] > thr and a[i] < a[j]:
                suppressed[i] = True
                break
    return [d for i, d in enumerate(dets) if not suppressed[i]]


def _iou_pair(b1, b2):
    ix1 = max(b1[0], b2[0]); iy1 = max(b1[1], b2[1])
    ix2 = min(b1[2], b2[2]); iy2 = min(b1[3], b2[3])
    inter = max(0, ix2-ix1) * max(0, iy2-iy1)
    a1 = max(0, b1[2]-b1[0]) * max(0, b1[3]-b1[1])
    a2 = max(0, b2[2]-b2[0]) * max(0, b2[3]-b2[1])
    return inter / (a1 + a2 - inter + 1e-6)

def compute_fhi(dets, frame_area):
    if not dets: return 50.0
    total_w = sum(d["area"] for d in dets) + 1e-6
    base = 0.0
    for d in dets:
        w = d["area"] / total_w
        sev = SEVERITY.get(d["class"], 0.5)
        base += w * (1.0 - sev)
    return round(base * 100.0, 1)

def run_pipeline(video_path, output_path):
    print(f"\n{'='*60}")
    print(f"MoonHarvest HSV-First Detection Pipeline")
    print(f"{'='*60}")
    print(f"Video input : {video_path}")
    print(f"Video output: {output_path}")
    print(f"{'='*60}\n")

    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        print(f"ERROR: Tidak bisa membuka video: {video_path}")
        return

    src_fps = cap.get(cv2.CAP_PROP_FPS) or 2.0
    src_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    src_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    n_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    print(f"Video: {src_w}x{src_h} @ {src_fps}fps, {n_frames} frames")

    # Setup output video dengan codec yang benar
    out = cv2.VideoWriter(output_path, cv2.VideoWriter_fourcc(*"mp4v"), src_fps, (src_w, src_h))
    if not out.isOpened():
        print(f"ERROR: Tidak bisa membuat video output: {output_path}")
        cap.release()
        return

    print(f"Output video: {output_path}")
    print(f"\nMulai deteksi...\n")

    ema_alpha = HSV_CFG["ema_alpha"]
    fhi_ema = None
    frame_idx = 0
    t_start = time.time()

    while True:
        ret, frame = cap.read()
        if not ret:
            break

        frame_idx += 1

        try:
            regions = segment_regions(frame, HSV_CFG)
            dets = [{"bbox": r["bbox"], "class": r["hsv_display"],
                     "conf": 0.60, "area": r["area"], "source": "hsv-only"}
                    for r in regions]
            dets = nms(dets)
            fhi_raw = compute_fhi(dets, frame.shape[0] * frame.shape[1])
            if fhi_ema is None:
                fhi_ema = fhi_raw
            else:
                fhi_ema = ema_alpha * fhi_raw + (1 - ema_alpha) * fhi_ema
            vis = draw_stream(frame, dets, fhi_ema)
        except Exception as exc:
            print(f"[ERROR] Frame {frame_idx}: {exc}")
            vis = frame.copy()
            fhi_ema = 50.0

        out.write(vis)

        if frame_idx % 50 == 0:
            elapsed = time.time() - t_start
            print(f"  Frame {frame_idx:4d}/{n_frames} | FHI={fhi_ema:.1f} | dets={len(dets)} | elapsed={elapsed:.1f}s")

    cap.release()
    out.release()

    elapsed = time.time() - t_start
    print(f"\n{'='*60}")
    print(f"Selesai: {frame_idx} frames dalam {elapsed:.1f}s")
    print(f"Avg fps: {frame_idx/elapsed:.1f}")
    print(f"Output: {output_path}")
    print(f"{'='*60}")

def main():
    videos = [
        "/home/fawwazfa/Program/Harvestmoon/vid/15d.mp4",
        "/home/fawwazfa/Program/Harvestmoon/vid/YDX_burned.mp4",
        "/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo.mp4",
        "/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo(1).mp4",
    ]

    base = "/home/fawwazfa/Videos/harvest_"
    names = ["15d", "YDX_burned", "YDXJ0012_demo", "YDXJ0012_demo(1)"]

    for video_path, name in zip(videos, names):
        output_path = f"/home/fawwazfa/Videos/{name}_hsv.mp4"
        run_pipeline(video_path, output_path)

    print("\nSemua video selesai!")

if __name__ == "__main__":
    main()