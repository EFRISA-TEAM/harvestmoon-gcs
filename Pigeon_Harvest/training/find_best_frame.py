#!/usr/bin/env python3
"""
find_best_frame.py — Cari frame terbaik di mana semua 4 class HSV terdeteksi.

Scoring per frame:
  - Semua 4 class harus hadir (filter wajib)
  - Score = min jumlah deteksi per class (keseimbangan antar class)
           + bonus FHI moderat (40-75, bukan ekstrim)
           + bonus jumlah total deteksi

Output:
  - Top-N frame disimpan sebagai JPEG ke output_dir
  - Tabel ringkasan dicetak ke terminal
"""

import os, sys, time
import cv2
import numpy as np
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from detect_hsv_pipeline import segment_regions, nms, draw_stream, compute_fhi, HSV_CFG

ALL_CLASSES = {"Lush Green", "Inconsistent Growth", "Drought / Severe Stress", "Bare Soil / Gap"}

# ── CONFIG ────────────────────────────────────────────────────────────────────
VIDEOS = [
    "/home/fawwazfa/Program/Harvestmoon/vid/15d.mp4",
    "/home/fawwazfa/Program/Harvestmoon/vid/YDX_burned.mp4",
    "/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo.mp4",
    "/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo(1).mp4",
]
OUT_DIR    = "/home/fawwazfa/Videos/best_frames"
TOP_N      = 5        # frame terbaik per video yang disimpan
STEP       = 5        # cek 1 dari setiap STEP frame (lebih cepat)
MIN_DETS   = 1        # minimal deteksi per class
FHI_LOW    = 30.0     # FHI minimal (terlalu rendah = terlalu rusak)
FHI_HIGH   = 85.0     # FHI maksimal (terlalu tinggi = tidak ada variasi)
# ─────────────────────────────────────────────────────────────────────────────


def score_frame(dets, fhi):
    """Hitung skor untuk frame. Lebih tinggi = lebih baik."""
    cls_counts = {}
    for d in dets:
        cls_counts[d["class"]] = cls_counts.get(d["class"], 0) + 1

    # semua 4 class harus hadir
    if not ALL_CLASSES.issubset(cls_counts.keys()):
        return -1.0, cls_counts

    # minimal deteksi per class
    if any(cls_counts.get(c, 0) < MIN_DETS for c in ALL_CLASSES):
        return -1.0, cls_counts

    # FHI harus moderat
    if fhi < FHI_LOW or fhi > FHI_HIGH:
        return -1.0, cls_counts

    # Score: keseimbangan antar class (min count) + total dets + FHI bonus
    balance  = min(cls_counts[c] for c in ALL_CLASSES)
    total    = sum(cls_counts.values())
    fhi_norm = 1.0 - abs(fhi - 55.0) / 55.0   # peak di FHI=55
    score    = balance * 3.0 + total * 0.5 + fhi_norm * 2.0
    return score, cls_counts


def process_video(video_path, top_n=TOP_N, step=STEP):
    name = Path(video_path).stem
    cap  = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        print(f"[SKIP] Tidak bisa buka: {video_path}")
        return []

    n_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    print(f"\nProses: {name}  ({n_frames} frames, step={step})")

    candidates = []   # (score, frame_idx, frame, dets, fhi, cls_counts)
    frame_idx  = 0
    fhi_ema    = None
    alpha      = HSV_CFG["ema_alpha"]
    t0         = time.time()

    while True:
        ret, frame = cap.read()
        if not ret:
            break
        frame_idx += 1
        if frame_idx % step != 0:
            continue

        try:
            regions = segment_regions(frame, HSV_CFG)
            dets    = [{"bbox": r["bbox"], "class": r["hsv_display"],
                        "conf": 0.60, "area": r["area"], "source": "hsv-only"}
                       for r in regions]
            dets    = nms(dets)
            fhi_raw = compute_fhi(dets, frame.shape[0] * frame.shape[1])
            fhi_ema = fhi_raw if fhi_ema is None else alpha * fhi_raw + (1-alpha) * fhi_ema
        except Exception as e:
            continue

        score, cls_counts = score_frame(dets, fhi_ema)
        if score > 0:
            candidates.append((score, frame_idx, frame.copy(), dets, fhi_ema, cls_counts))

        if frame_idx % 500 == 0:
            elapsed = time.time() - t0
            print(f"  frame {frame_idx:5d}/{n_frames} | candidates={len(candidates)} | {elapsed:.0f}s")

    cap.release()

    # Sort by score descending, ambil top_n
    candidates.sort(key=lambda x: -x[0])
    top = candidates[:top_n]
    elapsed = time.time() - t0
    print(f"  Selesai: {frame_idx} frames dalam {elapsed:.1f}s | {len(candidates)} kandidat ditemukan")
    return top, name


def save_frames(top_candidates, name, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    saved = []
    print(f"\n  Top {len(top_candidates)} frame untuk '{name}':")
    print(f"  {'Rank':<5} {'Frame':>6} {'Score':>6} {'FHI':>6} {'Lush':>5} {'Stress':>7} {'Drought':>8} {'Soil':>5}")
    print(f"  {'-'*60}")
    for rank, (score, fidx, frame, dets, fhi, cls_counts) in enumerate(top_candidates, 1):
        vis = draw_stream(frame, dets, fhi)
        fname = f"{name}_best_{rank:02d}_f{fidx:05d}.jpg"
        fpath = os.path.join(out_dir, fname)
        cv2.imwrite(fpath, vis, [cv2.IMWRITE_JPEG_QUALITY, 95])
        saved.append(fpath)
        print(f"  {rank:<5} {fidx:>6} {score:>6.2f} {fhi:>6.1f} "
              f"{cls_counts.get('Lush Green',0):>5} "
              f"{cls_counts.get('Inconsistent Growth',0):>7} "
              f"{cls_counts.get('Drought / Severe Stress',0):>8} "
              f"{cls_counts.get('Bare Soil / Gap',0):>5}  -> {fname}")
    return saved


def main():
    print("="*65)
    print("MoonHarvest — Find Best Frames (semua 4 class terdeteksi)")
    print("="*65)
    print(f"Output dir : {OUT_DIR}")
    print(f"Top-N      : {TOP_N} per video")
    print(f"Step       : setiap {STEP} frame")
    print(f"FHI range  : {FHI_LOW} – {FHI_HIGH}")
    print("="*65)

    all_saved = []
    t_total   = time.time()

    for video_path in VIDEOS:
        result = process_video(video_path)
        if not result:
            continue
        top_candidates, name = result
        if not top_candidates:
            print(f"  [!] Tidak ada frame dengan semua 4 class untuk {name}")
            continue
        saved = save_frames(top_candidates, name, OUT_DIR)
        all_saved.extend(saved)

    print(f"\n{'='*65}")
    print(f"Total frame disimpan: {len(all_saved)}")
    print(f"Total waktu         : {time.time()-t_total:.1f}s")
    print(f"Output di           : {OUT_DIR}/")
    print("="*65)


if __name__ == "__main__":
    main()
