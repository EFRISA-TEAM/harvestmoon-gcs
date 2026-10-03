"""
MoonHarvest — Inference Video dengan hasil training baru
Jalankan model moonharvest_uav_det_v2/best.pt pada video UAV

Usage:
  python run_inference_video.py                         # pakai default video & model
  python run_inference_video.py --video path/video.mp4  # video custom
  python run_inference_video.py --model path/best.pt    # model custom
  python run_inference_video.py --conf 0.35             # confidence threshold
  python run_inference_video.py --no-display            # tanpa window (headless)
"""

import argparse
import time
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO

# ── Default paths ──────────────────────────────────────────────────────────────
DEFAULT_VIDEO = "/home/fawwazfa/Videos/hsvv_fused.mp4"
DEFAULT_MODEL = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/runs/moonharvest_uav_det_v2/weights/best.pt"
OUTPUT_DIR    = Path("/home/fawwazfa/Videos")

# ── Warna per kelas (BGR) ──────────────────────────────────────────────────────
COLORS = {
    "crop": (50, 205, 50),    # hijau
    "weed": (0, 60, 255),     # merah
}
FONT = cv2.FONT_HERSHEY_SIMPLEX


def draw_detections(frame: np.ndarray, results, conf_thresh: float) -> tuple[np.ndarray, dict]:
    """Gambar bounding box dan label pada frame, return frame + stats."""
    counts = {"crop": 0, "weed": 0}
    annotated = frame.copy()

    for result in results:
        boxes = result.boxes
        if boxes is None:
            continue

        for box in boxes:
            conf = float(box.conf[0])
            if conf < conf_thresh:
                continue

            cls_id   = int(box.cls[0])
            cls_name = result.names[cls_id]
            color    = COLORS.get(cls_name, (255, 255, 255))

            # Bounding box
            x1, y1, x2, y2 = map(int, box.xyxy[0])
            cv2.rectangle(annotated, (x1, y1), (x2, y2), color, 2)

            # Label + confidence
            label = f"{cls_name} {conf:.2f}"
            (lw, lh), _ = cv2.getTextSize(label, FONT, 0.45, 1)
            cv2.rectangle(annotated, (x1, y1 - lh - 6), (x1 + lw + 4, y1), color, -1)
            cv2.putText(annotated, label, (x1 + 2, y1 - 3),
                        FONT, 0.45, (255, 255, 255), 1, cv2.LINE_AA)

            counts[cls_name] = counts.get(cls_name, 0) + 1

    return annotated, counts


def draw_overlay(frame: np.ndarray, counts: dict, fps: float,
                 frame_idx: int, total_frames: int) -> np.ndarray:
    """Gambar overlay info di pojok kiri atas."""
    h, w = frame.shape[:2]
    overlay = frame.copy()

    # Background panel
    panel_h = 110
    cv2.rectangle(overlay, (0, 0), (260, panel_h), (0, 0, 0), -1)
    cv2.addWeighted(overlay, 0.6, frame, 0.4, 0, frame)

    total_det = sum(counts.values())
    crop_pct  = counts["crop"] / max(total_det, 1) * 100
    weed_pct  = counts["weed"] / max(total_det, 1) * 100
    progress  = frame_idx / max(total_frames, 1) * 100

    lines = [
        (f"MoonHarvest UAV Det v2",      (255, 255, 100), 0.5),
        (f"Frame: {frame_idx}/{total_frames} ({progress:.0f}%)", (200, 200, 200), 0.45),
        (f"FPS  : {fps:.1f}",             (200, 200, 200), 0.45),
        (f"crop : {counts['crop']:3d} ({crop_pct:.0f}%)",  COLORS["crop"],  0.5),
        (f"weed : {counts['weed']:3d} ({weed_pct:.0f}%)",  COLORS["weed"],  0.5),
    ]

    y = 18
    for text, color, scale in lines:
        cv2.putText(frame, text, (8, y), FONT, scale, color, 1, cv2.LINE_AA)
        y += 20

    return frame


def run(video_path: str, model_path: str, conf: float,
        display: bool, save: bool, imgsz: int):

    print(f"\nMoonHarvest UAV Detector — Inference Video")
    print(f"{'='*55}")
    print(f"Video : {video_path}")
    print(f"Model : {model_path}")
    print(f"Conf  : {conf}")
    print(f"ImgSz : {imgsz}")
    print(f"{'='*55}\n")

    # Load model
    print("Loading model...")
    model = YOLO(model_path)
    print(f"Model loaded: {model.names}\n")

    # Buka video
    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        print(f"ERROR: Tidak bisa membuka video: {video_path}")
        return

    src_fps   = cap.get(cv2.CAP_PROP_FPS) or 2.0
    src_w     = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    src_h     = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    n_frames  = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    print(f"Video info: {src_w}x{src_h} @ {src_fps}fps, {n_frames} frames")

    # Setup output video
    out_writer = None
    if save:
        out_path = OUTPUT_DIR / f"inference_{Path(video_path).stem}.mp4"
        fourcc   = cv2.VideoWriter_fourcc(*"mp4v")
        out_writer = cv2.VideoWriter(str(out_path), fourcc, src_fps, (src_w, src_h))
        print(f"Output video: {out_path}")

    if display:
        cv2.namedWindow("MoonHarvest UAV Inference", cv2.WINDOW_NORMAL)
        cv2.resizeWindow("MoonHarvest UAV Inference", min(src_w, 1280), min(src_h, 400))

    # Loop inference
    frame_idx  = 0
    total_crop = 0
    total_weed = 0
    t_start    = time.time()
    fps_display = 0.0

    print("\nTekan 'q' untuk keluar, 's' untuk screenshot, SPACE untuk pause\n")

    paused = False
    while True:
        if not paused:
            ret, frame = cap.read()
            if not ret:
                break
            frame_idx += 1

        # Inference
        t0 = time.time()
        results = model(frame, imgsz=imgsz, conf=conf, verbose=False, device=0)
        t1 = time.time()
        fps_display = 1.0 / max(t1 - t0, 1e-6)

        # Gambar deteksi
        annotated, counts = draw_detections(frame, results, conf)
        total_crop += counts["crop"]
        total_weed += counts["weed"]

        # Overlay info
        draw_overlay(annotated, counts, fps_display, frame_idx, n_frames)

        # Simpan frame
        if out_writer:
            out_writer.write(annotated)

        # Tampilkan
        if display:
            cv2.imshow("MoonHarvest UAV Inference", annotated)
            wait = 1 if not paused else 50
            key = cv2.waitKey(wait) & 0xFF
            if key == ord('q'):
                print("\nDihentikan oleh user")
                break
            elif key == ord(' '):
                paused = not paused
                print("PAUSED" if paused else "RESUMED")
            elif key == ord('s'):
                ss_path = OUTPUT_DIR / f"screenshot_frame{frame_idx:04d}.jpg"
                cv2.imwrite(str(ss_path), annotated)
                print(f"Screenshot saved: {ss_path}")
        else:
            # Headless: print progress setiap 10 frame
            if frame_idx % 10 == 0:
                elapsed = time.time() - t_start
                print(f"  Frame {frame_idx:4d}/{n_frames} | "
                      f"inf={fps_display:.1f}fps | "
                      f"crop={counts['crop']} weed={counts['weed']} | "
                      f"elapsed={elapsed:.1f}s")

    # Cleanup
    cap.release()
    if out_writer:
        out_writer.release()
    if display:
        cv2.destroyAllWindows()

    # Ringkasan
    elapsed = time.time() - t_start
    total_det = total_crop + total_weed
    print(f"\n{'='*55}")
    print(f"Selesai: {frame_idx} frames dalam {elapsed:.1f}s")
    print(f"Avg inference fps : {frame_idx/elapsed:.1f}")
    print(f"Total crop deteksi: {total_crop}")
    print(f"Total weed deteksi: {total_weed}")
    if total_det > 0:
        print(f"Rasio crop/weed   : {total_crop/total_det*100:.1f}% / {total_weed/total_det*100:.1f}%")
    if out_writer:
        print(f"\nOutput disimpan ke: {OUTPUT_DIR}/inference_{Path(video_path).stem}.mp4")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="MoonHarvest UAV Detector — Inference Video")
    parser.add_argument("--video",      default=DEFAULT_VIDEO,  help="Path video input")
    parser.add_argument("--model",      default=DEFAULT_MODEL,  help="Path model .pt")
    parser.add_argument("--conf",       type=float, default=0.35, help="Confidence threshold (default: 0.35)")
    parser.add_argument("--imgsz",      type=int,   default=416,  help="Inference image size (default: 416)")
    parser.add_argument("--no-display", action="store_true",     help="Jalankan tanpa window (headless)")
    parser.add_argument("--save",       action="store_true",     help="Simpan output video")
    args = parser.parse_args()

    run(
        video_path = args.video,
        model_path = args.model,
        conf       = args.conf,
        display    = not args.no_display,
        save       = args.save,
        imgsz      = args.imgsz,
    )
