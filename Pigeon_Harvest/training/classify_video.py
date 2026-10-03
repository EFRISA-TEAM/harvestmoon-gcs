#!/usr/bin/env python3
"""
MoonHarvest — Klasifikasi Video Realtime
Gunakan model classification (bukan detection) untuk mengklasifikasi
seluruh frame video ke 4 kelas: bare_soil, drought_severe_stress,
inconsistent_growth, lush_green.

Tombol kontrol di window:
  'q' - keluar
  's' - screenshot frame saat ini
  'SPACE' - pause/resume
"""

import argparse
import time
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO


# Default paths
DEFAULT_VIDEO = "/home/fawwazfa/Videos/hsvv_fused.mp4"
DEFAULT_MODEL = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/training/runs/moonharvest_v6_s_fp32/weights/best.pt"
OUTPUT_DIR = Path("/home/fawwazfa/Videos")


def annotate_frame(frame, result, names, conf_thresh=0.25):
    """Menambahkan label klasifikasi di pojok kiri atas frame."""
    probs = result[0].probs
    if probs is None:
        return frame, "unknown"

    class_id = int(probs.top1)
    class_name = names[class_id]
    confidence = float(probs.top1conf)

    if confidence < conf_thresh:
        return frame, "unknown"

    label = f"{class_name} {confidence:.2f}"
    color = (0, 255, 0)  # hijau BGR

    # Background untuk teks
    (lw, lh), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.7, 2)
    cv2.rectangle(frame, (0, 0), (200 + lw, 25 + lh), (0, 0, 0), -1)
    cv2.putText(frame, label, (8, 20),
                cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2, cv2.LINE_AA)

    return frame, class_name


def run(video_path: str, model_path: str, conf: float,
        display: bool, save: bool, imgsz: int):

    print(f"\nMoonHarvest — Klasifikasi Video Inference")
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

    src_fps = cap.get(cv2.CAP_PROP_FPS) or 2.0
    src_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    src_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    n_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    print(f"Video info: {src_w}x{src_h} @ {src_fps}fps, {n_frames} frames")

    # Setup output video writer jika save
    out_writer = None
    if save:
        out_path = OUTPUT_DIR / f"classified_{Path(video_path).name}"
        fourcc = cv2.VideoWriter_fourcc(*"mp4v")
        out_writer = cv2.VideoWriter(str(out_path), fourcc, src_fps, (src_w, src_h))
        print(f"Output video: {out_path}")

    # Frame loop
    frame_idx = 0
    t_start = time.time()
    paused = False

    print("\nTekan 'q' untuk keluar, 's' untuk screenshot, SPACE untuk pause\n")

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
            inference_fps = 1.0 / max(t1 - t0, 1e-6)

            # Annotate frame dengan label klasifikasi
            annotated, class_name = annotate_frame(frame, results, model.names, conf)

            # Overlay info di overlay panel
            h, w = annotated.shape[:2]
            overlay = annotated.copy()
            panel_h = 80
            cv2.rectangle(overlay, (0, 0), (300, panel_h), (0, 0, 0), -1)
            cv2.addWeighted(overlay, 0.7, annotated, 0.3, 0, annotated)

            # Info text
            lines = [
                f"Frame: {frame_idx}/{n_frames}",
                f"Class: {class_name}",
                f"Inference FPS: {inference_fps:.1f}",
            ]
            y = 30
            for text in lines:
                cv2.putText(annotated, text, (10, y),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.6, (255, 255, 255), 1, cv2.LINE_AA)
                y += 25

            # Simpan frame jika diminta
            if out_writer:
                out_writer.write(annotated)

            # Coba tampilkan window, jika gagal (tidak ada GUI) otomatis headless
            window_available = False
            if display:
                try:
                    cv2.imshow("MoonHarvest Classification Video", annotated)
                    cv2.waitKey(1)  # Reset window state
                    window_available = True
                except cv2.error:
                    print("[INFO] GUI tidak tersedia, otomatis ke mode headless")
                    window_available = False

            if window_available:
                key = cv2.waitKey(1 if not paused else 50) & 0xFF
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
                # Headless: print progress tiap 10 frame
                if frame_idx % 10 == 0:
                    elapsed = time.time() - t_start
                    print(f"  Frame {frame_idx:4d}/{n_frames} | "
                          f"inf={inference_fps:.1f}fps | "
                          f"class={class_name} | "
                          f"elapsed={elapsed:.1f}s")

    # Cleanup
    cap.release()
    if out_writer:
        out_writer.release()
    if window_available and display:
        cv2.destroyAllWindows()

    # Ringkasan
    elapsed = time.time() - t_start
    print(f"\n{'='*55}")
    print(f"Selesai: {frame_idx} frames dalam {elapsed:.1f}s")
    print(f"Avg inference fps : {frame_idx/elapsed:.1f}")
    if out_writer:
        print(f"\nOutput disimpan ke: {OUTPUT_DIR}/classified_{Path(video_path).name}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="MoonHarvest Classification Video Inference")
    parser.add_argument("--video", default=DEFAULT_VIDEO, help="Path video input")
    parser.add_argument("--model", default=DEFAULT_MODEL, help="Path model .pt klasifikasi")
    parser.add_argument("--conf", type=float, default=0.35, help="Confidence threshold")
    parser.add_argument("--imgsz", type=int, default=416, help="Inference image size")
    parser.add_argument("--no-display", action="store_true", help="Jalankan tanpa window GUI")
    parser.add_argument("--save", action="store_true", help="Simpan output video klasifikasi")
    args = parser.parse_args()

    run(
        video_path=args.video,
        model_path=args.model,
        conf=args.conf,
        display=not args.no_display,
        save=args.save,
        imgsz=args.imgsz,
    )