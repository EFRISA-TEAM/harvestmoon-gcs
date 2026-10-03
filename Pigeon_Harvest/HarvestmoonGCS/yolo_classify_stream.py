#!/usr/bin/env python3
"""
Grid-based YOLO classification streaming for MoonHarvest GCS.

Reads video frames, divides into grid, classifies each cell via YOLO,
draws merged bounding boxes (DEMO_MODE) + summary overlay, outputs base64
JPEG JSON to stdout.

Protocol (same as camera_service.py):
  {"type": "frame",     "data": "<base64_jpeg>"}
  {"type": "detection", "data": {"count": N, "summary": "...", "classes": {...}}}
  {"type": "error",     "data": "..."}

DEMO_MODE (MOONHARVEST_DEMO=1 or --demo flag):
  - Remaps 5 internal classes → 4 proposal labels
  - Hides bare_soil (background)
  - Merges drought_stress → Stress
  - Uses connected-component merging of adjacent same-class cells
  - Uses imgsz=640 (matches proposal Figure 3)
  - Applies NMS (IoU threshold 0.45) on merged regional boxes
  - Adds Pest to legend (configurable, rarely/never triggered)
"""

import argparse
import base64
import json
import os
import signal
import sys
import time

import cv2
import numpy as np
from ultralytics import YOLO

# ---------------------------------------------------------------------------
# DEMO_MODE
# ---------------------------------------------------------------------------
DEMO_MODE = os.environ.get("MOONHARVEST_DEMO", "1") == "1"

INFERENCE_SIZE = 640  # imgsz — matches proposal Figure 3 (OpenCV Resize 640×640)

# ---------------------------------------------------------------------------
# Class mappings
# ---------------------------------------------------------------------------

# v3 model class names → display labels (semua ditampilkan, tidak ada yang hidden)
DISPLAY_MAP = {
    "lush_green":          "Lush Green",
    "well_irrigated":      "Well Irrigated",
    "inconsistent_growth": "Inconsistent Growth",
    "soil_issues":         "Soil Issues",
    "disease":             "Disease",
    "pest":                "Pest",
}

HIDDEN_CLASSES: set = set()  # v3: tidak ada kelas yang disembunyikan

# Colors keyed by demo display label (BGR)
DEMO_COLORS = {
    "Lush Green":          (50,  205,  50),
    "Well Irrigated":      (200, 150,   2),
    "Inconsistent Growth": (0,   200, 255),
    "Soil Issues":         (55,   64,  93),
    "Disease":             (0,    60, 255),
    "Pest":                (0,   140, 255),
}

# Internal class colors (non-demo path) — sama dengan DEMO_COLORS untuk v3
INTERNAL_COLORS = {
    "lush_green":          (50,  205,  50),
    "well_irrigated":      (200, 150,   2),
    "inconsistent_growth": (0,   200, 255),
    "soil_issues":         (55,   64,  93),
    "disease":             (0,    60, 255),
    "pest":                (0,   140, 255),
}

# Short labels for non-demo overlay
SHORT_LABELS = {
    "lush_green":          "Lush Green",
    "well_irrigated":      "Well Irrigated",
    "inconsistent_growth": "Inconsistent",
    "soil_issues":         "Soil Issues",
    "disease":             "Disease",
    "pest":                "Pest",
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def emit(payload: dict) -> None:
    print(json.dumps(payload), flush=True)


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def scale_box(x1, y1, x2, y2, scale, w, h):
    cx = (x1 + x2) / 2.0
    cy = (y1 + y2) / 2.0
    bw = (x2 - x1) * scale
    bh = (y2 - y1) * scale
    return (
        int(clamp(cx - bw / 2.0, 0, w - 1)),
        int(clamp(cy - bh / 2.0, 0, h - 1)),
        int(clamp(cx + bw / 2.0, 0, w - 1)),
        int(clamp(cy + bh / 2.0, 0, h - 1)),
    )


# ---------------------------------------------------------------------------
# TASK-05 — Connected-component merging
# ---------------------------------------------------------------------------

def merge_grid_to_regions(detections, frame_w, frame_h, grid_rows, grid_cols):
    """BFS connected-component merge of adjacent same-class cells into merged boxes."""
    if not detections:
        return []

    cell_h = frame_h // grid_rows
    cell_w = frame_w // grid_cols

    cell_map: dict = {}
    for det in detections:
        x1, y1, _, _ = det["bbox"]
        col = min(x1 // max(cell_w, 1), grid_cols - 1)
        row = min(y1 // max(cell_h, 1), grid_rows - 1)
        cell_map[(row, col)] = det

    visited: set = set()
    regions = []

    for pos in list(cell_map.keys()):
        if pos in visited:
            continue
        cls = cell_map[pos]["class"]
        queue = [pos]
        cells = []
        while queue:
            r, c = queue.pop()
            if (r, c) in visited:
                continue
            if cell_map.get((r, c), {}).get("class") != cls:
                continue
            visited.add((r, c))
            cells.append(cell_map[(r, c)])
            for dr, dc in [(-1, 0), (1, 0), (0, -1), (0, 1)]:
                nb = (r + dr, c + dc)
                if nb not in visited and cell_map.get(nb, {}).get("class") == cls:
                    queue.append(nb)

        if not cells:
            continue

        bx1 = min(d["bbox"][0] for d in cells)
        by1 = min(d["bbox"][1] for d in cells)
        bx2 = max(d["bbox"][2] for d in cells)
        by2 = max(d["bbox"][3] for d in cells)
        avg_conf = sum(d["confidence"] for d in cells) / len(cells)

        regions.append({
            "bbox": (bx1, by1, bx2, by2),
            "class": cls,
            "class_id": cells[0]["class_id"],
            "confidence": avg_conf,
            "cell_count": len(cells),
        })

    return regions


# ---------------------------------------------------------------------------
# NMS — suppress overlapping regional boxes
# ---------------------------------------------------------------------------

def apply_nms(detections, iou_threshold=0.45):
    """IoU-based NMS on merged regional bounding boxes.

    BFS merge already prevents same-class overlaps; NMS here is the
    final pipeline stage matching proposal Figure 3, handling any
    residual cross-class overlaps and acting as a safety net.
    """
    if len(detections) <= 1:
        return detections

    boxes = []
    scores = []
    for d in detections:
        x1, y1, x2, y2 = d["bbox"]
        boxes.append([int(x1), int(y1), int(x2 - x1), int(y2 - y1)])
        scores.append(float(d["confidence"]))

    indices = cv2.dnn.NMSBoxes(boxes, scores, score_threshold=0.0, nms_threshold=iou_threshold)
    if len(indices) == 0:
        return []

    flat = indices.flatten() if hasattr(indices, "flatten") else list(indices)
    return [detections[i] for i in flat]


# ---------------------------------------------------------------------------
# Grid classification
# ---------------------------------------------------------------------------

def classify_grid(frame, model, grid_rows: int, grid_cols: int, min_conf: float):
    """Divide frame into grid cells, classify each, apply demo remapping."""
    height, width = frame.shape[:2]
    cell_h = height // grid_rows
    cell_w = width // grid_cols

    detections = []
    class_counts: dict = {}

    for row in range(grid_rows):
        for col in range(grid_cols):
            x1 = col * cell_w
            y1 = row * cell_h
            x2 = x1 + cell_w
            y2 = y1 + cell_h

            cell = frame[y1:y2, x1:x2]
            results = model(cell, verbose=False, imgsz=INFERENCE_SIZE, device=0)
            if not results:
                continue

            probs = results[0].probs
            class_id = int(probs.top1)
            conf = float(probs.top1conf)
            class_name = results[0].names[class_id]

            if conf < min_conf:
                continue

            if DEMO_MODE:
                if class_name in HIDDEN_CLASSES:
                    continue
                display_label = DISPLAY_MAP.get(class_name)
                if display_label is None:
                    continue
                class_name = display_label

            detections.append({
                "bbox": (x1, y1, x2, y2),
                "class": class_name,
                "class_id": class_id,
                "confidence": conf,
            })
            class_counts[class_name] = class_counts.get(class_name, 0) + 1

    total = sum(class_counts.values())
    return detections, total, class_counts


# ---------------------------------------------------------------------------
# Drawing
# ---------------------------------------------------------------------------

def draw_detections(frame, detections, box_scale: float):
    """Draw boxes on frame. In DEMO_MODE uses merged regional boxes without scaling."""
    annotated = frame.copy()
    height, width = frame.shape[:2]

    for det in detections:
        x1, y1, x2, y2 = det["bbox"]
        class_name = det["class"]
        conf = det["confidence"]

        if DEMO_MODE:
            sx1, sy1, sx2, sy2 = int(x1), int(y1), int(x2), int(y2)
            color = DEMO_COLORS.get(class_name, (255, 255, 255))
        else:
            sx1, sy1, sx2, sy2 = scale_box(x1, y1, x2, y2, box_scale, width, height)
            color = INTERNAL_COLORS.get(class_name, (255, 255, 255))

        cv2.rectangle(annotated, (sx1, sy1), (sx2, sy2), color, 2)

        label = f"{class_name} {conf:.2f}"
        (lw, lh), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.5, 1)
        cv2.rectangle(annotated, (sx1, sy1 - lh - 10), (sx1 + lw + 10, sy1), color, -1)
        cv2.putText(annotated, label, (sx1 + 5, sy1 - 5),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 255), 1, cv2.LINE_AA)

    return annotated


def add_statistics_overlay(frame, detections):
    """Draw detection summary overlay."""
    total = len(detections)
    if total == 0:
        return frame

    class_counts: dict = {}
    for det in detections:
        cls = det["class"]
        class_counts[cls] = class_counts.get(cls, 0) + 1

    dominant = max(class_counts, key=class_counts.get)
    overlay = frame.copy()
    n = len(class_counts)
    box_h = 110 + n * 22
    cv2.rectangle(overlay, (10, 10), (340, 10 + box_h), (0, 0, 0), -1)
    cv2.addWeighted(overlay, 0.55, frame, 0.45, 0, frame)

    y = 35
    cv2.putText(frame, "Detection Summary", (20, y),
                cv2.FONT_HERSHEY_SIMPLEX, 0.65, (255, 255, 255), 2, cv2.LINE_AA)
    y += 28
    total_cells = sum(d.get("cell_count", 1) for d in detections)
    cv2.putText(frame, f"Regions: {total}  Cells: {total_cells}", (20, y),
                cv2.FONT_HERSHEY_SIMPLEX, 0.5, (200, 200, 200), 1, cv2.LINE_AA)
    y += 18
    disp = dominant
    cv2.putText(frame, f"Dominant: {disp} ({class_counts[dominant]})", (20, y),
                cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 255, 200), 1, cv2.LINE_AA)
    y += 24

    colors = DEMO_COLORS if DEMO_MODE else INTERNAL_COLORS
    for cls_name in sorted(class_counts, key=lambda c: class_counts[c], reverse=True):
        count = class_counts[cls_name]
        pct = count / total * 100
        color = colors.get(cls_name, (255, 255, 255))
        prefix = ">" if cls_name == dominant else " "
        cv2.putText(frame, f"{prefix}{cls_name}: {count} ({pct:.0f}%)", (20, y),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.45, color, 1, cv2.LINE_AA)
        y += 22

    if DEMO_MODE:
        cv2.putText(frame, f"Inference: {INFERENCE_SIZE}x{INFERENCE_SIZE}", (20, y + 4),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.4, (180, 180, 180), 1, cv2.LINE_AA)

    return frame


def add_legend(frame):
    """Bottom-right legend for 6-class proposal Zone Analysis."""
    if not DEMO_MODE:
        return frame
    h, w = frame.shape[:2]
    entries = [
        ("Lush Green",          DEMO_COLORS["Lush Green"]),
        ("Well Irrigated",      DEMO_COLORS["Well Irrigated"]),
        ("Inconsistent Growth", DEMO_COLORS["Inconsistent Growth"]),
        ("Soil Issues",         DEMO_COLORS["Soil Issues"]),
        ("Disease",             DEMO_COLORS["Disease"]),
        ("Pest",                DEMO_COLORS["Pest"]),
    ]
    padding, row_h = 8, 20
    legend_h = padding * 2 + len(entries) * row_h
    legend_w = 130
    lx, ly = w - legend_w - 10, h - legend_h - 10
    overlay = frame.copy()
    cv2.rectangle(overlay, (lx, ly), (lx + legend_w, ly + legend_h), (0, 0, 0), -1)
    cv2.addWeighted(overlay, 0.6, frame, 0.4, 0, frame)
    for i, (name, color) in enumerate(entries):
        iy = ly + padding + i * row_h
        cv2.rectangle(frame, (lx + padding, iy + 2), (lx + padding + 14, iy + 14), color, -1)
        cv2.putText(frame, name, (lx + padding + 20, iy + 13),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.42, (255, 255, 255), 1, cv2.LINE_AA)
    return frame


# ---------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------

def main():
    global DEMO_MODE

    parser = argparse.ArgumentParser(
        description="Grid-based YOLO health classification streaming for GCS"
    )
    parser.add_argument("--source",     type=str,   required=True)
    parser.add_argument("--model",      type=str,   required=True)
    parser.add_argument("--grid-rows",  type=int,   default=5)
    parser.add_argument("--grid-cols",  type=int,   default=7)
    parser.add_argument("--min-conf",   type=float, default=0.3)
    parser.add_argument("--box-scale",  type=float, default=0.85)
    parser.add_argument("--max-fps",    type=float, default=15.0)
    parser.add_argument("--demo",       action="store_true",
                        help="Force demo mode (also triggered by MOONHARVEST_DEMO=1)")
    parser.add_argument("--no-overlay", action="store_true",
                        help="Emit raw frame without annotation (toggle vegetation overlay off)")
    args = parser.parse_args()

    if args.demo:
        DEMO_MODE = True

    signal.signal(signal.SIGINT, lambda s, f: sys.exit(0))
    signal.signal(signal.SIGTERM, lambda s, f: sys.exit(0))

    try:
        import torch as _torch
        model = YOLO(args.model)
        model.to("cuda" if _torch.cuda.is_available() else "cpu")
    except Exception as exc:
        emit({"type": "error", "data": f"Failed to load model: {exc}"})
        return 1

    source = args.source
    cap = cv2.VideoCapture(int(source) if source.isdigit() else source)
    if not cap.isOpened():
        emit({"type": "error", "data": f"Failed to open source: {source}"})
        return 1

    min_frame_interval = 1.0 / args.max_fps
    last_frame_time = 0.0

    try:
        while True:
            ret, frame = cap.read()
            if not ret or frame is None:
                if (DEMO_MODE or getattr(args, "demo", False)) and not isinstance(source, int):
                    cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
                    ret, frame = cap.read()
                    if not ret or frame is None:
                        try:
                            cap.release()
                            time.sleep(0.05)
                            cap = cv2.VideoCapture(source)
                            ret, frame = cap.read()
                        except Exception:
                            pass
                    if not ret or frame is None:
                        emit({"type": "end", "data": "Video stream ended"})
                        break
                else:
                    emit({"type": "end", "data": "Video stream ended"})
                    break

            now = time.time()
            if now - last_frame_time < min_frame_interval:
                continue
            last_frame_time = now

            height, width = frame.shape[:2]

            if args.no_overlay:
                # Vegetation overlay OFF — emit raw frame
                ok, buf = cv2.imencode(".jpg", frame, [cv2.IMWRITE_JPEG_QUALITY, 80])
                if ok:
                    emit({"type": "frame", "data": base64.b64encode(buf).decode()})
                continue

            # Classify grid cells
            detections, total, class_counts = classify_grid(
                frame, model, args.grid_rows, args.grid_cols, args.min_conf
            )

            # Merge adjacent cells → regional boxes, then NMS (DEMO_MODE only)
            if DEMO_MODE:
                merged = merge_grid_to_regions(
                    detections, width, height, args.grid_rows, args.grid_cols
                )
                render_dets = apply_nms(merged, iou_threshold=0.45)
            else:
                render_dets = detections

            # Render annotations
            annotated = draw_detections(frame, render_dets, args.box_scale)
            annotated = add_statistics_overlay(annotated, render_dets)
            if DEMO_MODE:
                annotated = add_legend(annotated)

            ok, buf = cv2.imencode(".jpg", annotated, [cv2.IMWRITE_JPEG_QUALITY, 80])
            if not ok:
                emit({"type": "error", "data": "JPEG encoding failed"})
                continue

            emit({"type": "frame", "data": base64.b64encode(buf).decode()})

            # Build summary using demo labels
            if total > 0:
                summary_parts = [
                    f"{cls}: {class_counts[cls]}"
                    for cls in sorted(class_counts, key=lambda c: class_counts[c], reverse=True)
                ]
                summary_str = " | ".join(summary_parts)
            else:
                summary_str = "No detections"

            emit({
                "type": "detection",
                "data": {
                    "count": total,
                    "summary": summary_str,
                    "classes": class_counts,
                },
            })

    except (BrokenPipeError, EOFError):
        pass
    except KeyboardInterrupt:
        pass
    except Exception as exc:
        emit({"type": "error", "data": str(exc)})
        return 1
    finally:
        cap.release()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
