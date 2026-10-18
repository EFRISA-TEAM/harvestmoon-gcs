#!/usr/bin/env bash
# =============================================================================
# MoonHarvest Demo Runner
# Jalankan:
#   ./run_demo.sh              -> Menjalankan Full GCS GUI Demo (JALANKAN_DEMO.sh)
#   ./run_demo.sh stream [vid] -> Menjalankan Python YOLO Stream Detection
#   ./run_demo.sh video [vid]  -> Menjalankan deteksi video offline
# =============================================================================

set -e
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

MODE="${1:-gui}"
VIDEO="${2:-TEKNOFEST_SIAP/demo_video/pdp_sawah_4sektor_1080p.mp4}"

# Warna terminal
CYAN='\033[0;36m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; RED='\033[0;31m'; NC='\033[0m'

case "$MODE" in
  gui|demo)
    echo -e "${CYAN}==> Mengalihkan ke GCS One-Click Demo Launcher...${NC}"
    exec bash "$ROOT_DIR/JALANKAN_DEMO.sh"
    ;;

  stream)
    echo -e "${YELLOW}==> Menjalankan MoonHarvest YOLO Stream Detector...${NC}"
    VENV="$ROOT_DIR/Pigeon_Harvest/.venv-yolo"
    if [ -f "$VENV/bin/activate" ]; then
      source "$VENV/bin/activate"
    elif [ -f "$ROOT_DIR/.venv/bin/activate" ]; then
      source "$ROOT_DIR/.venv/bin/activate"
    fi
    python3 "$ROOT_DIR/moonharvest_detect_stream.py" "$@"
    ;;

  video)
    echo -e "${YELLOW}==> Menjalankan Deteksi Video Offline: $VIDEO...${NC}"
    if [ ! -f "$VIDEO" ]; then
      echo -e "${RED}[ERROR] Video tidak ditemukan: $VIDEO${NC}"
      exit 1
    fi
    python3 "$ROOT_DIR/run_detection_video.py" -i "$VIDEO" --no-display
    ;;

  *)
    echo -e "${CYAN}Penggunaan: ./run_demo.sh [gui|stream|video] [path_video]${NC}"
    echo "Default (tanpa argumen): Menjalankan Full GCS GUI Demo."
    exec bash "$ROOT_DIR/JALANKAN_DEMO.sh"
    ;;
esac
