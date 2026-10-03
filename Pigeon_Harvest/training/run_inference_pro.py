"""
MoonHarvest — Professional Inference Video
Style: clean, minimal HUD, professional drone footage aesthetic
Seperti referensi 15d.mp4: resolusi tinggi, overlay tipis, warna elegan

Usage:
  python run_inference_pro.py                          # pakai default
  python run_inference_pro.py --video path/video.mp4   # video custom
  python run_inference_pro.py --model path/best.pt     # model custom
  python run_inference_pro.py --conf 0.25 --save       # simpan output
"""

import argparse
import time
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO

# ── Default paths ──────────────────────────────────────────────────────────────
DEFAULT_VIDEO = "/home/fawwazfa/Downloads/15d.mp4"
DEFAULT_MODEL = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/runs/moonharvest_uav_det_v2_finetune/weights/best.pt"
OUTPUT_DIR    = Path("/home/fawwazfa/Videos")

# ── Design system ─────────────────────────────────────────────────────────────
# Warna elegan (BGR)
COLOR_CROP     = (80, 220, 80)       # hijau lembut
COLOR_WEED     = (80, 80, 220)       # merah/ungu lembut
COLOR_HUD_BG   = (15, 15, 15)       # panel gelap
COLOR_WHITE    = (240, 240, 240)
COLOR_GRAY     = (160, 160, 160)
COLOR_ACCENT   = (0, 180, 255)      # cyan accent
COLOR_CROP_HUD = (80, 220, 80)
COLOR_WEED_HUD = (80, 80, 220)

FONT       = cv2.FONT_HERSHEY_SIMPLEX
FONT_MONO  = cv2.FONT_HERSHEY_DUPLEX
ALPHA_HUD  = 0.72   # transparansi panel HUD


def draw_box_professional(frame, x1, y1, x2, y2, label, conf, color):
    """Gambar bounding box style professional — corner brackets, bukan full rectangle."""
    thickness = 2
    corner_len = min(20, (x2-x1)//4, (y2-y1)//4)

    # Corner brackets — top-left
    cv2.line(frame, (x1, y1), (x1 + corner_len, y1), color, thickness)
    cv2.line(frame, (x1, y1), (x1, y1 + corner_len), color, thickness)
    # top-right
    cv2.line(frame, (x2, y1), (x2 - corner_len, y1), color, thickness)
    cv2.line(frame, (x2, y1), (x2, y1 + corner_len), color, thickness)
    # bottom-left
    cv2.line(frame, (x1, y2), (x1 + corner_len, y2), color, thickness)
    cv2.line(frame, (x1, y2), (x1, y2 - corner_len), color, thickness)
    # bottom-right
    cv2.line(frame, (x2, y2), (x2 - corner_len, y2), color, thickness)
    cv2.line(frame, (x2, y2), (x2, y2 - corner_len), color, thickness)

    # Label kecil di atas box
    label_text = f"{label} {conf:.0%}"
    scale = 0.45
    thick = 1
    (tw, th), _ = cv2.getTextSize(label_text, FONT, scale, thick)

    # Background label transparan
    lx1, ly1 = x1, y1 - th - 8
    lx2, ly2 = x1 + tw + 8, y1
    if ly1 < 0:
        ly1, ly2 = y2, y2 + th + 8

    overlay = frame.copy()
    cv2.rectangle(overlay, (lx1, ly1), (lx2, ly2), COLOR_HUD_BG, -1)
    cv2.addWeighted(overlay, 0.7, frame, 0.3, 0, frame)
    cv2.putText(frame, label_text, (lx1 + 4, ly2 - 3),
                FONT, scale, color, thick, cv2.LINE_AA)


def draw_hud(frame, counts, fps_inf, frame_idx, total_frames, model_name):
    """HUD minimalis di pojok kiri atas — style professional."""
    h, w = frame.shape[:2]

    total_det = sum(counts.values())
    crop_n = counts.get("crop", 0)
    weed_n = counts.get("weed", 0)
    progress = frame_idx / max(total_frames, 1)

    # ── Panel kiri atas ───────────────────────────────────────────────────────
    panel_w = 260
    panel_h = 130
    margin  = 16

    overlay = frame.copy()
    # Panel dengan rounded feel (multiple rectangles)
    cv2.rectangle(overlay, (margin, margin), (margin + panel_w, margin + panel_h),
                  COLOR_HUD_BG, -1)
    cv2.addWeighted(overlay, ALPHA_HUD, frame, 1 - ALPHA_HUD, 0, frame)

    # Border tipis
    cv2.rectangle(frame, (margin, margin), (margin + panel_w, margin + panel_h),
                  (40, 40, 40), 1)

    # Accent line di atas panel
    cv2.rectangle(frame, (margin, margin), (margin + panel_w, margin + 3),
                  COLOR_ACCENT, -1)

    # Teks HUD
    x0, y0 = margin + 10, margin + 18
    line_h  = 22

    # Judul
    cv2.putText(frame, "MOONHARVEST UAV", (x0, y0),
                FONT, 0.42, COLOR_ACCENT, 1, cv2.LINE_AA)

    # Garis pemisah
    cv2.line(frame, (x0, y0 + 5), (margin + panel_w - 10, y0 + 5), (50, 50, 50), 1)

    y0 += line_h
    cv2.putText(frame, f"CROP  {crop_n:3d}", (x0, y0),
                FONT, 0.48, COLOR_CROP_HUD, 1, cv2.LINE_AA)

    y0 += line_h
    cv2.putText(frame, f"WEED  {weed_n:3d}", (x0, y0),
                FONT, 0.48, COLOR_WEED_HUD, 1, cv2.LINE_AA)

    y0 += line_h
    cv2.putText(frame, f"TOTAL {total_det:3d}  |  {fps_inf:.0f} FPS", (x0, y0),
                FONT, 0.42, COLOR_GRAY, 1, cv2.LINE_AA)

    y0 += line_h
    cv2.putText(frame, f"FRAME {frame_idx:5d}/{total_frames}", (x0, y0),
                FONT, 0.38, (100, 100, 100), 1, cv2.LINE_AA)

    # ── Progress bar di bagian bawah ─────────────────────────────────────────
    bar_h    = 3
    bar_y    = h - bar_h - 1
    bar_fill = int(w * progress)

    # Background bar
    cv2.rectangle(frame, (0, bar_y), (w, h), (20, 20, 20), -1)
    # Fill bar
    if bar_fill > 0:
        cv2.rectangle(frame, (0, bar_y), (bar_fill, h), COLOR_ACCENT, -1)

    # ── Timestamp pojok kanan bawah ───────────────────────────────────────────
    ts = f"{frame_idx / max(total_frames, 1) * 100:.1f}%"
    (tw, th), _ = cv2.getTextSize(ts, FONT, 0.45, 1)
    cv2.putText(frame, ts, (w - tw - 12, h - 10),
                FONT, 0.45, (80, 80, 80), 1, cv2.LINE_AA)


def run(video_path, model_path, conf, display, save, imgsz):
    print(f"\nMoonHarvest Professional Inference")
    print(f"{'='*50}")
    print(f"Video : {Path(video_path).name}")
    print(f"Model : {Path(model_path).name}")
    print(f"Conf  : {conf}")
    print(f"{'='*50}\n")

    model = YOLO(model_path)

    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        print(f"ERROR: Tidak bisa buka {video_path}")
        return

    src_fps    = cap.get(cv2.CAP_PROP_FPS) or 30.0
    src_w      = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    src_h      = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    n_frames   = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    print(f"Video : {src_w}x{src_h} @ {src_fps:.0f}fps, {n_frames} frames ({n_frames/src_fps:.0f}s)")

    out_writer = None
    if save:
        vid_stem = Path(video_path).stem
        out_path = OUTPUT_DIR / f"pro_{vid_stem}.mp4"
        fourcc   = cv2.VideoWriter_fourcc(*"mp4v")
        out_writer = cv2.VideoWriter(str(out_path), fourcc, src_fps, (src_w, src_h))
        print(f"Output: {out_path}")

    if display:
        cv2.namedWindow("MoonHarvest Pro", cv2.WINDOW_NORMAL)
        cv2.resizeWindow("MoonHarvest Pro", min(src_w, 1440), min(src_h, 810))

    frame_idx   = 0
    total_crop  = 0
    total_weed  = 0
    t_start     = time.time()
    fps_display = 0.0
    paused      = False

    print("\nKontrol: [q] keluar  [SPACE] pause  [s] screenshot\n")

    while True:
        if not paused:
            ret, frame = cap.read()
            if not ret:
                break
            frame_idx += 1

        t0 = time.time()
        results = model(frame, imgsz=imgsz, conf=conf, verbose=False, device=0)
        fps_display = 1.0 / max(time.time() - t0, 1e-6)

        counts = {"crop": 0, "weed": 0}

        # Gambar deteksi dengan style professional
        for result in results:
            if result.boxes is None:
                continue
            for box in result.boxes:
                c = float(box.conf[0])
                if c < conf:
                    continue
                cls_id   = int(box.cls[0])
                cls_name = result.names[cls_id]
                color    = COLOR_CROP if cls_name == "crop" else COLOR_WEED
                x1, y1, x2, y2 = map(int, box.xyxy[0])
                draw_box_professional(frame, x1, y1, x2, y2, cls_name, c, color)
                counts[cls_name] = counts.get(cls_name, 0) + 1

        total_crop += counts["crop"]
        total_weed += counts["weed"]

        # HUD overlay
        draw_hud(frame, counts, fps_display, frame_idx, n_frames,
                 Path(model_path).parent.parent.name)

        if out_writer:
            out_writer.write(frame)

        if display:
            cv2.imshow("MoonHarvest Pro", frame)
            key = cv2.waitKey(1) & 0xFF
            if key == ord('q'):
                break
            elif key == ord(' '):
                paused = not paused
            elif key == ord('s'):
                ss = OUTPUT_DIR / f"pro_screenshot_{frame_idx:05d}.jpg"
                cv2.imwrite(str(ss), frame)
                print(f"Screenshot: {ss}")
        else:
            if frame_idx % 50 == 0:
                elapsed = time.time() - t_start
                pct = frame_idx / n_frames * 100
                print(f"  {pct:5.1f}% | frame {frame_idx:5d}/{n_frames} | "
                      f"{fps_display:.0f} fps | crop={counts['crop']} weed={counts['weed']}")

    cap.release()
    if out_writer:
        out_writer.release()
    if display:
        cv2.destroyAllWindows()

    elapsed = time.time() - t_start
    total   = total_crop + total_weed
    print(f"\n{'='*50}")
    print(f"Selesai : {frame_idx} frames, {elapsed:.1f}s")
    print(f"Avg fps : {frame_idx/elapsed:.1f}")
    print(f"crop    : {total_crop} det")
    print(f"weed    : {total_weed} det")
    if total > 0:
        print(f"rasio   : crop {total_crop/total*100:.1f}% / weed {total_weed/total*100:.1f}%")
    if out_writer:
        print(f"\nOutput  : {OUTPUT_DIR}/pro_{Path(video_path).stem}.mp4")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="MoonHarvest Professional Inference Video")
    parser.add_argument("--video",      default=DEFAULT_VIDEO)
    parser.add_argument("--model",      default=DEFAULT_MODEL)
    parser.add_argument("--conf",       type=float, default=0.25)
    parser.add_argument("--imgsz",      type=int,   default=416)
    parser.add_argument("--no-display", action="store_true")
    parser.add_argument("--save",       action="store_true")
    args = parser.parse_args()

    run(
        video_path = args.video,
        model_path = args.model,
        conf       = args.conf,
        display    = not args.no_display,
        save       = args.save,
        imgsz      = args.imgsz,
    )
