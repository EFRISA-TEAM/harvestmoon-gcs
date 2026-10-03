#!/usr/bin/env python3
"""Simple YOLOv8 webcam test for Orange Pi / Linux SBC."""

from __future__ import annotations

import argparse
import time
from pathlib import Path
import cv2
import yaml
from ultralytics import YOLO


def parse_source(value: str) -> int | str:
    return int(value) if value.isdigit() else value


def load_names(path: Path) -> dict[int, str] | None:
    if not path.exists():
        return None

    with path.open("r", encoding="utf-8") as file:
        data = yaml.safe_load(file) or {}

    names = data.get("names", data)
    if isinstance(names, list):
        return {idx: str(name) for idx, name in enumerate(names)}
    if isinstance(names, dict):
        return {int(idx): str(name) for idx, name in names.items()}
    return None


def build_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run trained YOLOv8n model on webcam.")
    parser.add_argument("--model", default="weights/best.pt", help="Path to trained .pt model.")
    parser.add_argument("--names", default="class_names.yaml", help="Optional class-name YAML.")
    parser.add_argument("--source", default="0", help="Webcam index, video path, or stream URL.")
    parser.add_argument("--imgsz", type=int, default=416, help="Inference image size.")
    parser.add_argument("--conf", type=float, default=0.35, help="Confidence threshold.")
    parser.add_argument("--device", default="cpu", help="Use cpu on Orange Pi. Example: cpu")
    parser.add_argument("--width", type=int, default=640, help="Requested webcam width.")
    parser.add_argument("--height", type=int, default=480, help="Requested webcam height.")
    parser.add_argument("--save", action="store_true", help="Save annotated video to output folder.")
    parser.add_argument("--output", default="output/webcam_result.mp4", help="Output video path.")
    parser.add_argument("--no-window", action="store_true", help="Run headless without display window.")
    return parser.parse_args()


def main() -> int:
    args = build_args()

    model_path = Path(args.model)
    if not model_path.exists():
        raise FileNotFoundError(f"Model tidak ditemukan: {model_path}")

    model = YOLO(str(model_path))
    names = load_names(Path(args.names))
    if names:
        model.names = names

    source = parse_source(args.source)
    cap = cv2.VideoCapture(source, cv2.CAP_V4L2 if isinstance(source, int) else cv2.CAP_ANY)
    if not cap.isOpened():
        raise RuntimeError(f"Tidak bisa membuka source kamera/video: {args.source}")

    cap.set(cv2.CAP_PROP_FRAME_WIDTH, args.width)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, args.height)
    cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)

    writer = None
    output_path = Path(args.output)
    if args.save:
        output_path.parent.mkdir(parents=True, exist_ok=True)
        fps = cap.get(cv2.CAP_PROP_FPS)
        fps = fps if fps and fps > 1 else 20
        fourcc = cv2.VideoWriter_fourcc(*"mp4v")
        writer = cv2.VideoWriter(str(output_path), fourcc, fps, (args.width, args.height))

    fps_smooth = 0.0
    print("Tekan q untuk keluar.")

    while True:
        ok, frame = cap.read()
        if not ok:
            print("Frame kamera tidak terbaca.")
            break

        start = time.perf_counter()
        result = model.predict(
            frame,
            imgsz=args.imgsz,
            conf=args.conf,
            device=args.device,
            verbose=False,
        )[0]
        elapsed = max(time.perf_counter() - start, 1e-6)
        fps_now = 1.0 / elapsed
        fps_smooth = fps_now if fps_smooth == 0 else (0.9 * fps_smooth + 0.1 * fps_now)

        annotated = result.plot()
        cv2.putText(
            annotated,
            f"FPS: {fps_smooth:.1f} | conf: {args.conf:.2f} | imgsz: {args.imgsz}",
            (10, 28),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.7,
            (0, 255, 0),
            2,
            cv2.LINE_AA,
        )

        if writer:
            writer.write(cv2.resize(annotated, (args.width, args.height)))

        if not args.no_window:
            cv2.imshow("YOLOv8n Webcam Test", annotated)
            if cv2.waitKey(1) & 0xFF == ord("q"):
                break

    cap.release()
    if writer:
        writer.release()
        print(f"Video tersimpan: {output_path}")
    cv2.destroyAllWindows()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
