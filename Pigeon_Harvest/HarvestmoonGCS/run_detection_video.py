#!/usr/bin/env python3
"""
MoonHarvest — run pipeline v7c on a video file, save output as MP4.
Usage:
    python3 run_detection_video.py --source 15d.mp4 [--model path/to/cls.onnx] [--det-model path/to/det.onnx]
Output: out/stream_v7c_final.mp4
"""

import argparse, os, sys, time
import cv2
import numpy as np
from pathlib import Path

# ── import pipeline dari moonharvest_detect_stream ────────────────────────────
sys.path.insert(0, str(Path(__file__).parent / "Pigeon_Harvest" / "HarvestmoonGCS"))
from moonharvest_detect_stream import (
    HSV_CFG, segment_regions, fuse, load_classifier,
    YOLODetector, nms, compute_fhi, EMASmooth, draw_stream, build_gcs_counts
)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source",    required=True)
    ap.add_argument("--model",     default="")
    ap.add_argument("--det-model", default="")
    ap.add_argument("--out",       default="out/stream_v7c_final.mp4")
    ap.add_argument("--max-fps",   type=float, default=15.0)
    args = ap.parse_args()

    Path(args.out).parent.mkdir(parents=True, exist_ok=True)

    cap = cv2.VideoCapture(args.source)
    if not cap.isOpened():
        print(f"[ERROR] Cannot open: {args.source}"); return 1

    src_fps  = cap.get(cv2.CAP_PROP_FPS) or 30.0
    width    = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    height   = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    total    = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    target_fps = min(args.max_fps, src_fps)
    step     = max(1, int(round(src_fps / target_fps)))
    PROC_W   = 640

    # Output size matches processed frame
    proc_h = int(height * PROC_W / width) if width > PROC_W else height
    proc_w = PROC_W if width > PROC_W else width

    # MP4 writer — pakai mp4v lalu re-encode dengan ffmpeg ke libx264
    tmp_out = args.out.replace(".mp4", "_tmp.mp4")
    fourcc  = cv2.VideoWriter_fourcc(*"mp4v")
    writer  = cv2.VideoWriter(tmp_out, fourcc, target_fps, (proc_w, proc_h))
    if not writer.isOpened():
        print("[ERROR] VideoWriter gagal dibuka"); return 1

    # Load models
    classifier = None
    if args.model and os.path.isfile(args.model):
        try:
            classifier = load_classifier(args.model)
            print(f"[INFO] Classifier: {Path(args.model).name}")
        except Exception as e:
            print(f"[WARN] Classifier error: {e}, pakai HSV-only")
    else:
        print("[INFO] HSV-only mode (no classifier)")

    detector = None
    det_path = getattr(args, "det_model", "")
    if det_path and os.path.isfile(det_path):
        try:
            detector = YOLODetector(det_path)
            print(f"[INFO] YOLO-det: {Path(det_path).name}")
        except Exception as e:
            print(f"[WARN] YOLO-det error: {e}")

    ema    = EMASmooth(HSV_CFG["ema_alpha"])
    fidx   = 0
    written = 0
    fhi_list = []
    t0 = time.time()

    print(f"[INFO] Sumber: {args.source}  |  {total} frames  |  step={step}  |  out={args.out}")

    while True:
        ret, frame = cap.read()
        if not ret or frame is None:
            break
        fidx += 1
        if fidx % step != 0:
            continue

        # Resize to PROC_W
        fh, fw = frame.shape[:2]
        if fw > PROC_W:
            frame = cv2.resize(frame, (PROC_W, int(fh * PROC_W / fw)))

        try:
            if detector:
                dets = detector.detect(frame)
                if len(dets) < 2:
                    regions = segment_regions(frame, HSV_CFG)
                    dets += [{"bbox": r["bbox"], "class": r["hsv_display"],
                              "conf": 0.50, "area": r["area"], "source": "hsv-fallback"}
                             for r in regions]
            elif classifier:
                regions = segment_regions(frame, HSV_CFG)
                dets = fuse(frame, regions, classifier)
            else:
                regions = segment_regions(frame, HSV_CFG)
                dets = [{"bbox": r["bbox"], "class": r["hsv_display"],
                         "conf": 0.60, "area": r["area"], "source": "hsv-only"}
                        for r in regions]
            dets    = nms(dets, per_cls_iou=0.25, cross_iou=0.40, min_area=200)
            fhi_raw = compute_fhi(dets, frame.shape[0] * frame.shape[1])
            fhi     = ema.update("fhi", fhi_raw)
            vis     = draw_stream(frame, dets, fhi)
        except Exception as exc:
            print(f"[WARN] frame {fidx}: {exc}")
            vis = frame

        writer.write(vis)
        written += 1
        fhi_list.append(fhi)

        if written % 30 == 0:
            pct = fidx / max(total, 1) * 100
            elapsed = time.time() - t0
            fps_proc = written / max(elapsed, 0.001)
            print(f"  Frame {fidx}/{total} ({pct:.0f}%)  FHI={fhi:.1f}  {fps_proc:.1f} fps")

    cap.release()
    writer.release()

    if written == 0:
        print("[ERROR] Tidak ada frame yang ditulis")
        return 1

    # Re-encode ke H.264 MP4 pakai ffmpeg
    print(f"\n[INFO] Re-encoding {tmp_out} → {args.out} (libx264)...")
    ret = os.system(
        f'ffmpeg -i "{tmp_out}" -c:v libx264 -preset fast -crf 23 -y "{args.out}" 2>&1'
    )
    if ret == 0:
        os.remove(tmp_out)
        size_mb = Path(args.out).stat().st_size / 1024 / 1024
        print(f"[DONE] {args.out}  ({size_mb:.1f} MB,  {written} frames)")
    else:
        # ffmpeg gagal — rename tmp sebagai output final
        os.rename(tmp_out, args.out)
        size_mb = Path(args.out).stat().st_size / 1024 / 1024
        print(f"[DONE] {args.out} (mp4v fallback, {size_mb:.1f} MB, {written} frames)")

    if fhi_list:
        print(f"[STAT] FHI avg={sum(fhi_list)/len(fhi_list):.1f}  "
              f"min={min(fhi_list):.1f}  max={max(fhi_list):.1f}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
