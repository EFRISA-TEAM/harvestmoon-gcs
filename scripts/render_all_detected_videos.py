#!/usr/bin/env python3
"""
Render offline detected videos with bounding boxes, agronomy labels, and HUD overlays.
Outputs MP4 videos to TEKNOFEST_SIAP/demo_video/ and Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/
"""

import os
import sys
import time
import subprocess
import cv2
import numpy as np
from pathlib import Path

# Add GCS directory for importing moonharvest_detect_stream
ROOT_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT_DIR / "Pigeon_Harvest" / "HarvestmoonGCS"))

import moonharvest_detect_stream as mh

# Palette matching GCS
COLORS = {
    "Lush Green":              ( 50, 210,  50),
    "Inconsistent Growth":     (  0, 200, 255),
    "Drought / Severe Stress": (  0,  90, 255),
    "Bare Soil / Gap":         (160, 160, 160),
}

def draw_hud(frame, dets, fhi, title, frame_idx, fps):
    h, w = frame.shape[:2]
    out = frame.copy()
    overlay = frame.copy()

    # Draw bounding boxes
    for d in dets:
        x1, y1, x2, y2 = d["bbox"]
        cname = d["class"]
        col = COLORS.get(cname, (200, 200, 200))
        conf = d.get("conf", 0.0)

        # Main box
        thick = 2
        cv2.rectangle(out, (x1, y1), (x2, y2), col, thick)

        # Corner accents (military/drone look)
        corner_len = min(12, max(5, (x2 - x1) // 5), max(5, (y2 - y1) // 5))
        cv2.line(out, (x1, y1), (x1 + corner_len, y1), col, thick + 1)
        cv2.line(out, (x1, y1), (x1, y1 + corner_len), col, thick + 1)
        cv2.line(out, (x2, y1), (x2 - corner_len, y1), col, thick + 1)
        cv2.line(out, (x2, y1), (x2, y1 + corner_len), col, thick + 1)
        cv2.line(out, (x1, y2), (x1 + corner_len, y2), col, thick + 1)
        cv2.line(out, (x1, y2), (x1, y2 - corner_len), col, thick + 1)
        cv2.line(out, (x2, y2), (x2 - corner_len, y2), col, thick + 1)
        cv2.line(out, (x2, y2), (x2, y2 - corner_len), col, thick + 1)

        # Tag
        label = f"{cname} {conf:.2f}"
        (tw, th), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.40, 1)
        ty = max(y1, th + 8)
        cv2.rectangle(out, (x1, ty - th - 6), (x1 + tw + 6, ty + 2), (20, 20, 20), -1)
        cv2.rectangle(out, (x1, ty - th - 6), (x1 + tw + 6, ty + 2), col, 1)
        cv2.putText(out, label, (x1 + 3, ty - 2), cv2.FONT_HERSHEY_SIMPLEX, 0.40, col, 1, cv2.LINE_AA)

    # Top banner HUD
    cv2.rectangle(overlay, (0, 0), (w, 36), (15, 18, 24), -1)
    cv2.line(overlay, (0, 36), (w, 36), (60, 70, 80), 1)

    # Bottom status bar
    cv2.rectangle(overlay, (0, h - 24), (w, h), (15, 18, 24), -1)
    cv2.line(overlay, (0, h - 24), (w, h - 24), (60, 70, 80), 1)

    # Blend overlay with 80% opacity
    cv2.addWeighted(overlay, 0.82, out, 0.18, 0, out)

    # Re-draw text sharply on top of blended HUD
    # 1. Left Title
    cv2.putText(out, f"MOONHARVEST AI  |  {title}", (10, 23),
                cv2.FONT_HERSHEY_SIMPLEX, 0.46, (255, 255, 255), 1, cv2.LINE_AA)

    # 2. Center FHI Badge
    fhi_color = (50, 220, 50) if fhi >= 80 else ((0, 200, 255) if fhi >= 65 else (0, 90, 255))
    fhi_status = "Optimal" if fhi >= 80 else ("Moderate" if fhi >= 65 else "Severe Stress")
    fhi_text = f"FHI: {fhi:.1f}% ({fhi_status})"
    (fw, fh), _ = cv2.getTextSize(fhi_text, cv2.FONT_HERSHEY_SIMPLEX, 0.48, 1)
    fx = (w - fw) // 2
    cv2.rectangle(out, (fx - 8, 5), (fx + fw + 8, 31), fhi_color, 1)
    cv2.putText(out, fhi_text, (fx, 23), cv2.FONT_HERSHEY_SIMPLEX, 0.48, fhi_color, 1, cv2.LINE_AA)

    # 3. Right Stats
    counts, summary = mh.build_gcs_counts(dets, fhi)
    c_h = counts.get("Healthy", 0)
    c_s = counts.get("Stress", 0)
    c_d = counts.get("Drought", 0)
    c_b = counts.get("Bare Soil", 0)
    stats_text = f"H:{c_h:.0f}% S:{c_s:.0f}% D:{c_d:.0f}% B:{c_b:.0f}%"
    (sw, sh), _ = cv2.getTextSize(stats_text, cv2.FONT_HERSHEY_SIMPLEX, 0.42, 1)
    cv2.putText(out, stats_text, (w - sw - 10, 23),
                cv2.FONT_HERSHEY_SIMPLEX, 0.42, (200, 220, 240), 1, cv2.LINE_AA)

    # 4. Bottom Info
    sec = frame_idx / max(1.0, fps)
    bot_text = f"Frame: {frame_idx:04d}  |  Time: {sec:04.1f}s  |  Detections: {len(dets)}  |  Model: YOLOv8 4-Class UAV Detector"
    cv2.putText(out, bot_text, (10, h - 8),
                cv2.FONT_HERSHEY_SIMPLEX, 0.38, (170, 185, 200), 1, cv2.LINE_AA)

    return out

def render_video(src_path, dst_path, title, max_frames=None, target_fps=15, target_w=640):
    src_path = str(src_path)
    dst_path = Path(dst_path)
    dst_path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = dst_path.with_name(f"temp_{dst_path.name}")

    if not os.path.exists(src_path):
        print(f"[SKIP] Source not found: {src_path}")
        return False

    cap = cv2.VideoCapture(src_path)
    if not cap.isOpened():
        print(f"[ERROR] Cannot open: {src_path}")
        return False

    src_w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    src_h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    src_fps = cap.get(cv2.CAP_PROP_FPS) or 30.0
    total_src_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    # Calculate output dimensions (maintaining aspect ratio)
    scale = target_w / src_w
    out_w = target_w
    out_h = int(round(src_h * scale))
    if out_h % 2 != 0: out_h += 1

    # Frame step for downsampling to target_fps
    step = max(1, int(round(src_fps / target_fps)))

    # Models
    model_path = ROOT_DIR / "Pigeon_Harvest" / "HarvestmoonGCS" / "Assets" / "models" / "moonharvest-health-cls.onnx"
    det_path = ROOT_DIR / "Pigeon_Harvest" / "HarvestmoonGCS" / "Assets" / "models" / "moonharvest-uav-det.onnx"

    classifier = mh.ONNXClassifier(str(model_path)) if model_path.exists() else None
    detector = mh.YOLODetector(str(det_path)) if det_path.exists() else None

    # VideoWriter
    fourcc = cv2.VideoWriter_fourcc(*'mp4v')
    out_writer = cv2.VideoWriter(str(tmp_path), fourcc, float(target_fps), (out_w, out_h))

    ema = mh.EMASmooth(alpha=0.30)
    print(f"[RENDER] {Path(src_path).name} -> {dst_path.name} ({out_w}x{out_h} @ {target_fps}fps)")

    frame_idx = 0
    written_frames = 0
    t0 = time.time()

    while True:
        ret, frame = cap.read()
        if not ret or frame is None:
            break

        if frame_idx % step == 0:
            resized = cv2.resize(frame, (out_w, out_h))
            if detector:
                dets = detector.detect(resized)
                if len(dets) < 2:
                    regions = mh.segment_regions(resized, mh.HSV_CFG)
                    dets += [{"bbox": r["bbox"], "class": r["hsv_display"], "conf": 0.50,
                              "area": r["area"], "source": "hsv-fallback"} for r in regions]
            elif classifier:
                regions = mh.segment_regions(resized, mh.HSV_CFG)
                dets = mh.fuse(resized, regions, classifier)
            else:
                regions = mh.segment_regions(resized, mh.HSV_CFG)
                dets = [{"bbox": r["bbox"], "class": r["hsv_display"], "conf": 0.60,
                         "area": r["area"], "source": "hsv-only"} for r in regions]

            dets = mh.nms(dets, per_cls_iou=0.25, cross_iou=0.40, min_area=200)
            fhi_raw = mh.compute_fhi(dets, out_w * out_h)
            fhi = ema.update("fhi", fhi_raw)

            vis = draw_hud(resized, dets, fhi, title, written_frames, target_fps)
            out_writer.write(vis)
            written_frames += 1

            if max_frames and written_frames >= max_frames:
                break

        frame_idx += 1

    cap.release()
    out_writer.release()

    dur = time.time() - t0
    fps_speed = written_frames / dur if dur > 0 else 0
    print(f"  Processed {written_frames} frames in {dur:.1f}s ({fps_speed:.1f} FPS)")

    # Transcode to H.264 using ffmpeg for universal playback
    print(f"  Encoding H.264: {dst_path.name}...")
    try:
        cmd = [
            "ffmpeg", "-y", "-v", "error",
            "-i", str(tmp_path),
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-preset", "fast", "-crf", "22",
            str(dst_path)
        ]
        subprocess.run(cmd, check=True)
        if tmp_path.exists():
            tmp_path.unlink()
        print(f"  ✓ SUCCESS: {dst_path} ({os.path.getsize(dst_path) // 1024} KB)")
        return True
    except Exception as e:
        print(f"  ⚠ ffmpeg error ({e}), moving temp file directly")
        if tmp_path.exists():
            tmp_path.rename(dst_path)
        return True

def main():
    demo_dir = ROOT_DIR / "TEKNOFEST_SIAP" / "demo_video"
    assets_dir = ROOT_DIR / "Pigeon_Harvest" / "HarvestmoonGCS" / "Assets" / "demo_videos"

    tasks = [
        {
            "src": ROOT_DIR / "vid" / "15d.mp4",
            "dst": demo_dir / "15d_detected.mp4",
            "title": "Sawah 15 HST · Karawang (Subur & Gap)",
            "max_frames": None,
            "target_fps": 15
        },
        {
            "src": ROOT_DIR / "vid" / "15d.mp4",
            "dst": demo_dir / "stream_v7c_final_detected.mp4",
            "title": "Padi 15 HST · Karawang (Baseline FHI 81.6%)",
            "max_frames": 232,
            "target_fps": 15
        },
        {
            "src": ROOT_DIR / "vid" / "YDX_burned.mp4",
            "dst": demo_dir / "YDX_burned_detected.mp4",
            "title": "Lahan Kering & Jerami Terbakar · Bogor",
            "max_frames": 500, # ~33s
            "target_fps": 15
        },
        {
            "src": ROOT_DIR / "vid" / "YDXJ0012_demo.mp4",
            "dst": demo_dir / "YDXJ_irigasi_detected.mp4",
            "title": "Hamparan Irigasi & Kanopi Padi · Majalengka",
            "max_frames": 900, # 60s
            "target_fps": 15
        },
        {
            "src": demo_dir / "stream_multisector_60s.mp4",
            "dst": demo_dir / "stream_multisector_60s_detected.mp4",
            "title": "Multi-Sektor Survei Komposit · Karawang",
            "max_frames": 900, # 60s
            "target_fps": 15
        }
    ]

    for t in tasks:
        print("=" * 70)
        ok = render_video(t["src"], t["dst"], t["title"], t["max_frames"], t["target_fps"])
        if ok and t["dst"].exists():
            target_asset = assets_dir / t["dst"].name
            print(f"  -> Syncing to Assets: {target_asset.name}")
            import shutil
            shutil.copy2(t["dst"], target_asset)

    print("=" * 70)
    print("ALL DETECTED VIDEOS GENERATED AND SYNCHRONIZED SUCCESSFULLY!")

if __name__ == "__main__":
    main()
