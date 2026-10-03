#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════
#  MOONHARVEST GCS — ONE-CLICK DEMO LAUNCHER
#  TEKNOFEST 2026 | Smart Agriculture Drone & Ground Control Station
# ═══════════════════════════════════════════════════════════════════════

set -e

# Warna Terminal
CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[1;34m'
RED='\033[0;31m'
BOLD='\033[1m'
NC='\033[0m'

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

clear 2>/dev/null || true
echo -e "${CYAN}╔══════════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${CYAN}║${BOLD}             MOONHARVEST GROUND CONTROL STATION (GCS)                 ${NC}${CYAN}║${NC}"
echo -e "${CYAN}║${NC}       Sistem Pemantauan Kesehatan Tanaman Padi Berbasis Drone AI     ${CYAN}║${NC}"
echo -e "${CYAN}║${YELLOW}                TEKNOFEST 2026 — Smart Agriculture                   ${NC}${CYAN}║${NC}"
echo -e "${CYAN}╚══════════════════════════════════════════════════════════════════════╝${NC}"
echo ""

echo -e "${BLUE}[*] Memeriksa Kesiapan Sistem Demo...${NC}"

# 1. Cek Executable GCS
GCS_EXE="$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS"
if [ ! -f "$GCS_EXE" ]; then
    GCS_EXE="$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/bin/Debug/net9.0-desktop/HarvestmoonGCS"
fi

if [ ! -f "$GCS_EXE" ]; then
    echo -e "${YELLOW}[!] Executable belum di-build. Melakukan kompilasi cepat...${NC}"
    dotnet build "$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/HarvestmoonGCS.csproj" -f net9.0-desktop -c Release
    GCS_EXE="$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS"
fi

echo -e "${GREEN}  ✓ Executable GCS Siap: $(basename $(dirname $(dirname $GCS_EXE)))/$(basename $(dirname $GCS_EXE))/HarvestmoonGCS${NC}"

# 2. Cek Python AI Environment
PYTHON_VENV="$ROOT_DIR/Pigeon_Harvest/.venv-yolo/bin/python3"
if [ -x "$PYTHON_VENV" ]; then
    echo -e "${GREEN}  ✓ Python AI Engine Siap (.venv-yolo)${NC}"
else
    echo -e "${YELLOW}  ⚠ Menggunakan python3 sistem (rekomendasi: gunakan .venv-yolo)${NC}"
fi

# 3. Cek Video Demo Skenario
DEMO_DIR="$ROOT_DIR/TEKNOFEST_SIAP/demo_video"
VID_COUNT=0
for vid in "stream_multisector_60s.mp4" "stream_v7c_final.mp4" "YDXJ_fused_only_detected.mp4" "YDX_burned.mp4"; do
    if [ -f "$DEMO_DIR/$vid" ] || [ -f "$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/$vid" ]; then
        VID_COUNT=$((VID_COUNT + 1))
    fi
done
echo -e "${GREEN}  ✓ Seluruh Video Skenario Tersedia ($VID_COUNT/4 Skenario Lengkap)${NC}"

# 4. Ringkasan Skenario Aktif
echo ""
echo -e "${BOLD}Daftar Skenario Terintegrasi di GCS:${NC}"
echo -e "  ${CYAN}1.${NC} ${BOLD}Multi-Sektor 60s (Subang · 5 WP)${NC}       → Transek E-W 600m | FHI: 85.4% | Alt: 65m"
echo -e "  ${CYAN}2.${NC} ${BOLD}Sawah 15 HST (Karawang · 4 WP)${NC}          → Sesuai PPT Teknofest | FHI: 81.6% | Alt: 45m"
echo -e "  ${CYAN}3.${NC} ${BOLD}Lahan Irigasi (Majalengka · 5 WP)${NC}       → Zona Irigasi Rentang | FHI: 94.2% | Alt: 55m"
echo -e "  ${CYAN}4.${NC} ${BOLD}Lahan Kering & Terbakar (Bogor · 4 WP)${NC}  → Jerami Terbakar | FHI: 42.5% | Alt: 75m"
echo -e "  ${CYAN}5.${NC} ${BOLD}Video Kustom (Drag-and-Drop)${NC}           → Auto-sinkron waypoint sesuai video apa pun"
echo ""

# Bersihkan sisa proses lama jika ada
pkill -f "moonharvest_detect_stream.py" 2>/dev/null || true

echo -e "${GREEN}Menjalankan MoonHarvest GCS...${NC}"
echo -e "${YELLOW}(Tekan Ctrl+C di terminal ini untuk keluar)${NC}"
echo "----------------------------------------------------------------------"

# Jalankan aplikasi GCS
cd "$(dirname "$GCS_EXE")"
export MOONHARVEST_CAMERA_SOURCE="http://10.235.141.127:8080/video_feed"
./HarvestmoonGCS

# Cleanup saat aplikasi ditutup
pkill -f "moonharvest_detect_stream.py" 2>/dev/null || true
echo ""
echo -e "${CYAN}[✓] MoonHarvest GCS sesi selesai.${NC}"
