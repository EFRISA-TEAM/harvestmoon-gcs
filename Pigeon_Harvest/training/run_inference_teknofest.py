"""
MoonHarvest — Professional Inference Video dengan HSV + YOLO Fusion
Style: clean HUD, corner brackets, progress bar, warna elegan
Siap demo TEKNOFEST

Usage:
  python run_inference_teknofest.py                        # default 15d.mp4
  python run_inference_teknofest.py --video path/vid.mp4  # video custom
  python run_inference_teknofest.py --all                 # semua video UAV
  python run_inference_teknofest.py --conf 0.25 --save
"""

import argparse
import time
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO

# ── Paths ──────────────────────────────────────────────────────────────────────
MODEL_PATH  = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/runs/moonharvest_uav_det_v2_finetune/weights/best.pt"
OUTPUT_DIR  = Path("/home/fawwazfa/Videos/teknofest_output")

ALL_VIDEOS  = [
    "/home/fawwazfa/Program/Harvestmoon/vidio/15d.mp4",
    "/home/fawwazfa/Program/Harvestmoon/vidio/YDXJ0012_demo(1).mp4",
    "/home/fawwazfa/Program/Harvestmoon/vidio/YDX_burned.mp4",
]

# ── Design ────────────────────────────────────────────────────────────────────
C_CROP    = (60, 220, 60)      # hijau
C_WEED    = (60, 60, 220)      # merah
C_ACCENT  = (0, 200, 255)      # cyan
C_PANEL   = (12, 12, 12)
C_WHITE   = (235, 235, 235)
C_GRAY    = (130, 130, 130)
C_DIM     = (60, 60, 60)
FONT      = cv2.FONT_HERSHEY_SIMPLEX
ALPHA     = 0.75

# ── HSV segmentasi (untuk fusion) ─────────────────────────────────────────────
HSV_CROP_RANGES = [
    ((35, 40, 40), (85, 255, 255)),   # hijau subur (crop sehat)
]
HSV_WEED_RANGES = [
    ((86, 30, 30), (130, 255, 255)),  # hijau-biru (gulma)
    ((25, 40, 40), (34,  255, 255)),  # kuning-hijau
]


def hsv_classify_region(roi_bgr):
    """Klasifikasikan region kecil pakai HSV — return 'crop', 'weed', atau None."""
    if roi_bgr.size == 0:
        return None
    hsv = cv2.cvtColor(roi_bgr, cv2.COLOR_BGR2HSV)
    total = hsv.shape[0] * hsv.shape[1]
    if total == 0:
        return None

    crop_px = 0
    for lo, hi in HSV_CROP_RANGES:
        mask = cv2.inRange(hsv, np.array(lo), np.array(hi))
        crop_px += int(mask.sum() / 255)

    weed_px = 0
    for lo, hi in HSV_WEED_RANGES:
        mask = cv2.inRange(hsv, np.array(lo), np.array(hi))
        weed_px += int(mask.sum() / 255)

    if crop_px / total > 0.25:
        return "crop"
    if weed_px / total > 0.20:
        return "weed"
    return None


def draw_corner_box(frame, x1, y1, x2, y2, color, thickness=2):
    """Corner bracket style bounding box."""
    cl = max(8, min(24, (x2 - x1) // 4, (y2 - y1) // 4))
    pts = [
        ((x1, y1), (x1+cl, y1)), ((x1, y1), (x1, y1+cl)),
        ((x2, y1), (x2-cl, y1)), ((x2, y1), (x2, y1+cl)),
        ((x1, y2), (x1+cl, y2)), ((x1, y2), (x1, y2-cl)),
        ((x2, y2), (x2-cl, y2)), ((x2, y2), (x2, y2-cl)),
    ]
    for p1, p2 in pts:
        cv2.line(frame, p1, p2, color, thickness, cv2.LINE_AA)


def draw_label(frame, x1, y1, x2, y2, text, color):
    """Label kecil transparan di atas/bawah box."""
    scale, thick = 0.42, 1
    (tw, th), _ = cv2.getTextSize(text, FONT, scale, thick)
    lx1 = x1
    ly1 = y1 - th - 7
    lx2 = x1 + tw + 8
    ly2 = y1 - 1
    if ly1 < 0:
        ly1, ly2 = y2 + 1, y2 + th + 8

    overlay = frame.copy()
    cv2.rectangle(overlay, (lx1, ly1), (lx2, ly2), C_PANEL, -1)
    cv2.addWeighted(overlay, 0.72, frame, 0.28, 0, frame)
    cv2.putText(frame, text, (lx1+4, ly2-3), FONT, scale, color, thick, cv2.LINE_AA)


def draw_hud(frame, counts, fps_inf, frame_idx, total, hsv_counts):
    """HUD minimalis professional."""
    h, w = frame.shape[:2]
    crop_y = counts.get("crop", 0)
    weed_y = counts.get("weed", 0)
    crop_h = hsv_counts.get("crop", 0)
    weed_h = hsv_counts.get("weed", 0)
    total_det = crop_y + weed_y
    progress  = frame_idx / max(total, 1)

    # ── Panel kiri atas ───────────────────────────────────────────────────────
    pw, ph = 280, 148
    mg = 14
    overlay = frame.copy()
    cv2.rectangle(overlay, (mg, mg), (mg+pw, mg+ph), C_PANEL, -1)
    cv2.addWeighted(overlay, ALPHA, frame, 1-ALPHA, 0, frame)
    cv2.rectangle(frame, (mg, mg), (mg+pw, mg+ph), (35,35,35), 1)
    # Accent bar atas
    cv2.rectangle(frame, (mg, mg), (mg+pw, mg+4), C_ACCENT, -1)

    x0, y0 = mg+10, mg+18
    lh = 24

    cv2.putText(frame, "MOONHARVEST  TEKNOFEST", (x0, y0),
                FONT, 0.40, C_ACCENT, 1, cv2.LINE_AA)
    cv2.line(frame, (x0, y0+4), (mg+pw-10, y0+4), (40,40,40), 1)

    y0 += lh
    # YOLO detections
    cv2.putText(frame, f"YOLO  CROP {crop_y:3d}   WEED {weed_y:3d}", (x0, y0),
                FONT, 0.42, C_WHITE, 1, cv2.LINE_AA)
    # Color dots
    cv2.circle(frame, (x0+58, y0-5), 4, C_CROP, -1)
    cv2.circle(frame, (x0+138, y0-5), 4, C_WEED, -1)

    y0 += lh
    # HSV confirmation
    cv2.putText(frame, f"HSV   CROP {crop_h:3d}   WEED {weed_h:3d}", (x0, y0),
                FONT, 0.42, C_GRAY, 1, cv2.LINE_AA)

    y0 += lh
    # Fusion total
    total_fused = max(crop_y, crop_h) + max(weed_y, weed_h)
    cv2.putText(frame, f"FUSED TOTAL: {total_fused:3d} objects", (x0, y0),
                FONT, 0.44, C_WHITE, 1, cv2.LINE_AA)

    y0 += lh
    cv2.putText(frame, f"INF {fps_inf:5.1f} fps   FRAME {frame_idx:5d}/{total}", (x0, y0),
                FONT, 0.38, C_DIM, 1, cv2.LINE_AA)

    y0 += lh
    # Mini progress bar di dalam panel
    bar_inner_w = pw - 20
    bar_fill = int(bar_inner_w * progress)
    cv2.rectangle(frame, (x0, y0), (x0+bar_inner_w, y0+5), (35,35,35), -1)
    if bar_fill > 0:
        cv2.rectangle(frame, (x0, y0), (x0+bar_fill, y0+5), C_ACCENT, -1)

    # ── Progress bar bawah (full width) ──────────────────────────────────────
    bar_y = h - 4
    fill  = int(w * progress)
    cv2.rectangle(frame, (0, bar_y), (w, h), (18,18,18), -1)
    if fill > 0:
        cv2.rectangle(frame, (0, bar_y), (fill, h), C_ACCENT, -1)

    # ── Watermark kanan bawah ─────────────────────────────────────────────────
    wm = "MoonHarvest GCS v2"
    (ww, wh), _ = cv2.getTextSize(wm, FONT, 0.38, 1)
    cv2.putText(frame, wm, (w-ww-10, h-10), FONT, 0.38, (45,45,45), 1, cv2.LINE_AA)


def process_video(video_path, model, conf, iou, display, save):
    vid_name = Path(video_path).stem
    print(f"\n{'='*55}")
    print(f"Video  : {vid_name}")

    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        print(f"ERROR: Tidak bisa buka {video_path}")
        return

    fps_src  = cap.get(cv2.CAP_PROP_FPS) or 30.0
    src_w    = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    src_h    = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    n_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    print(f"Res    : {src_w}x{src_h} @{fps_src:.0f}fps {n_frames}f ({n_frames/fps_src:.0f}s)")

    out_writer = None
    if save:
        OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
        out_path   = OUTPUT_DIR / f"teknofest_{vid_name}.mp4"
        fourcc     = cv2.VideoWriter_fourcc(*"mp4v")
        out_writer = cv2.VideoWriter(str(out_path), fourcc, fps_src, (src_w, src_h))
        print(f"Output : {out_path}")

    if display:
        try:
            cv2.namedWindow("MoonHarvest TEKNOFEST", cv2.WINDOW_NORMAL)
            cv2.resizeWindow("MoonHarvest TEKNOFEST", min(src_w, 1440), min(src_h, 810))
        except cv2.error:
            print("GUI tidak tersedia, beralih ke headless mode")
            display = False

    frame_idx   = 0
    tot_crop    = 0
    tot_weed    = 0
    fps_display = 0.0
    paused      = False
    t_start     = time.time()

    print("Kontrol: [q] keluar  [SPACE] pause  [s] screenshot\n")

    while True:
        if not paused:
            ret, frame = cap.read()
            if not ret:
                break
            frame_idx += 1

        t0 = time.time()

        # ── YOLO inference ──────────────────────────────────────────────────
        results = model(frame, imgsz=640, conf=conf, iou=iou, verbose=False, device=0)
        fps_display = 1.0 / max(time.time() - t0, 1e-6)

        yolo_counts = {"crop": 0, "weed": 0}
        hsv_counts  = {"crop": 0, "weed": 0}

        for result in results:
            if result.boxes is None:
                continue
            for box in result.boxes:
                c = float(box.conf[0])
                if c < conf:
                    continue
                cls_id   = int(box.cls[0])
                cls_name = result.names[cls_id]
                x1, y1, x2, y2 = map(int, box.xyxy[0])
                x1, y1 = max(0, x1), max(0, y1)
                x2, y2 = min(src_w, x2), min(src_h, y2)

                # ── HSV verification pada ROI ───────────────────────────────
                roi = frame[y1:y2, x1:x2]
                hsv_cls = hsv_classify_region(roi)

                # Fusion: pakai YOLO sebagai primary, HSV sebagai konfirmasi
                if hsv_cls == cls_name:
                    # Konfirmasi HSV — box lebih tebal, warna solid
                    color = C_CROP if cls_name == "crop" else C_WEED
                    draw_corner_box(frame, x1, y1, x2, y2, color, thickness=2)
                    label = f"{cls_name} {c:.0%} ✓"
                    hsv_counts[cls_name] = hsv_counts.get(cls_name, 0) + 1
                elif hsv_cls is not None and hsv_cls != cls_name:
                    # Konflik HSV — gambar abu-abu tipis
                    draw_corner_box(frame, x1, y1, x2, y2, (100,100,100), thickness=1)
                    label = f"{cls_name}? {c:.0%}"
                else:
                    # HSV tidak konklusif — pakai YOLO saja, box tipis
                    color = C_CROP if cls_name == "crop" else C_WEED
                    draw_corner_box(frame, x1, y1, x2, y2, color, thickness=1)
                    label = f"{cls_name} {c:.0%}"

                draw_label(frame, x1, y1, x2, y2, label,
                           C_CROP if cls_name == "crop" else C_WEED)
                yolo_counts[cls_name] = yolo_counts.get(cls_name, 0) + 1

        tot_crop += yolo_counts["crop"]
        tot_weed += yolo_counts["weed"]

        # ── HUD ────────────────────────────────────────────────────────────
        draw_hud(frame, yolo_counts, fps_display, frame_idx, n_frames, hsv_counts)

        if out_writer:
            out_writer.write(frame)

        if display:
            cv2.imshow("MoonHarvest TEKNOFEST", frame)
            key = cv2.waitKey(1) & 0xFF
            if key == ord('q'):
                break
            elif key == ord(' '):
                paused = not paused
            elif key == ord('s'):
                ss = OUTPUT_DIR / f"ss_{vid_name}_{frame_idx:05d}.jpg"
                OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
                cv2.imwrite(str(ss), frame)
                print(f"Screenshot: {ss}")
        else:
            if frame_idx % 100 == 0:
                elapsed = time.time() - t_start
                pct = frame_idx / n_frames * 100
                print(f"  {pct:5.1f}% | {frame_idx:5d}/{n_frames} | "
                      f"{fps_display:.0f}fps | crop={yolo_counts['crop']} weed={yolo_counts['weed']}")

    cap.release()
    if out_writer:
        out_writer.release()
    if display:
        cv2.destroyAllWindows()

    elapsed = time.time() - t_start
    total   = tot_crop + tot_weed
    print(f"\nSelesai : {frame_idx} frames, {elapsed:.1f}s ({frame_idx/elapsed:.1f} fps avg)")
    print(f"crop    : {tot_crop}  weed : {tot_weed}")
    if total > 0:
        print(f"rasio   : {tot_crop/total*100:.1f}% crop / {tot_weed/total*100:.1f}% weed")
    if save:
        print(f"Output  : {OUTPUT_DIR}/teknofest_{vid_name}.mp4")


def main():
    parser = argparse.ArgumentParser(description="MoonHarvest TEKNOFEST Inference")
    parser.add_argument("--video",      default=ALL_VIDEOS[0],   help="Path video")
    parser.add_argument("--model",      default=MODEL_PATH,       help="Path model .pt")
    parser.add_argument("--conf",       type=float, default=0.20, help="Confidence threshold")
    parser.add_argument("--iou",        type=float, default=0.40, help="NMS IoU threshold")
    parser.add_argument("--imgsz",      type=int,   default=640,  help="Inference size")
    parser.add_argument("--no-display", action="store_true",      help="Headless mode")
    parser.add_argument("--save",       action="store_true",      help="Simpan output video")
    parser.add_argument("--all",        action="store_true",      help="Proses semua video UAV")
    args = parser.parse_args()

    print("=" * 55)
    print("MoonHarvest TEKNOFEST — HSV + YOLO Fusion Inference")
    print("=" * 55)
    print(f"Model : {Path(args.model).name}")
    print(f"Conf  : {args.conf}  |  IoU NMS : {args.iou}")

    model = YOLO(args.model)
    print(f"Kelas : {model.names}\n")

    videos = ALL_VIDEOS if args.all else [args.video]

    for vid in videos:
        if not Path(vid).exists():
            print(f"SKIP (not found): {vid}")
            continue
        process_video(
            video_path = vid,
            model      = model,
            conf       = args.conf,
            iou        = args.iou,
            display    = not args.no_display,
            save       = args.save,
        )

    print("\nDone.")


if __name__ == "__main__":
    main()
