#!/usr/bin/env bash
set -euo pipefail

python3 test_webcam.py \
  --model weights/best.pt \
  --names class_names.yaml \
  --source 0 \
  --imgsz 416 \
  --conf 0.5 \
  --device cpu
