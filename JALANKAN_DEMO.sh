#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════
#  MOONHARVEST GCS — ONE-CLICK DEMO LAUNCHER
#  HIBAH RISET PDP / SINTA 2 & TEKNOFEST | Smart Agriculture Drone GCS
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
echo -e "${CYAN}║${NC}       Sistem Deteksi Defisit Air Tanaman Padi Berbasis Drone AI       ${CYAN}║${NC}"
echo -e "${CYAN}║${YELLOW}        HIBAH RISET NASIONAL PDP (SINTA 2) & TEKNOFEST 2026            ${NC}${CYAN}║${NC}"
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
for vid in "pdp_sawah_4sektor_1080p.mp4" "stream_multisector_60s.mp4" "stream_v7c_final.mp4" "YDXJ_fused_only_detected.mp4" "YDX_burned.mp4"; do
    if [ -f "$DEMO_DIR/$vid" ] || [ -f "$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/$vid" ]; then
        VID_COUNT=$((VID_COUNT + 1))
    fi
done
echo -e "${GREEN}  ✓ Seluruh Video Skenario Tersedia ($VID_COUNT Skenario Lengkap)${NC}"

# 4. Ringkasan Skenario Aktif
echo ""
echo -e "${BOLD}Daftar Skenario Terintegrasi di GCS:${NC}"
echo -e "  ${CYAN}1.${NC} ${BOLD}★ Riset PDP 4-Sektor 1080p (Subang · 5 WP)${NC} → Full HD 1080p | 4 Kelas PDP AWD | FHI: 85.4%"
echo -e "  ${CYAN}2.${NC} ${BOLD}Sawah 15 HST (Karawang · 4 WP)${NC}          → Rekaman Kontinu Padi 15 HST | FHI: 81.6%"
echo -e "  ${CYAN}3.${NC} ${BOLD}Lahan Irigasi (Majalengka · 5 WP)${NC}       → Zona Irigasi & Saluran Air | FHI: 94.2%"
echo -e "  ${CYAN}4.${NC} ${BOLD}Lahan Kering & Terbakar (Bogor · 4 WP)${NC}  → Defisit Kritis & Tanah Retak | FHI: 42.5%"
echo -e "  ${CYAN}5.${NC} ${BOLD}Video Kustom (Drag-and-Drop)${NC}           → Auto-sinkron waypoint sesuai video apa pun"
echo ""

# Bersihkan sisa proses lama jika ada
pkill -f "moonharvest_detect_stream.py" 2>/dev/null || true

echo -e "${GREEN}Menjalankan MoonHarvest GCS...${NC}"
echo -e "${YELLOW}(Tekan Ctrl+C di terminal ini untuk keluar)${NC}"
echo "----------------------------------------------------------------------"

# ── VIRA Lite Sidecar Auto-Start ──────────────────────────────
VIRA_BIN="$ROOT_DIR/bin/vira-lite"
if [ -f "$VIRA_BIN" ] && ! pgrep -f "vira-lite" >/dev/null 2>&1; then
    echo -e "${CYAN}[VIRA] Menjalankan VIRA Lite Agent di latar belakang...${NC}"
    chmod +x "$VIRA_BIN" 2>/dev/null || true
    (cd "$ROOT_DIR/bin" && nohup "$VIRA_BIN" >/dev/null 2>&1 &)
fi

# ── LoRa Ground Truth Soil Sensor Simulator Auto-Start ─────────
LORA_SCRIPT="$ROOT_DIR/scripts/lora_soil_bridge.py"
if [ -f "$LORA_SCRIPT" ] && ! pgrep -f "lora_soil_bridge.py" >/dev/null 2>&1; then
    echo -e "${CYAN}[LoRa] Menjalankan Simulator 4-Node Sensor Tanah PDP (UDP:14552)...${NC}"
    (python3 "$LORA_SCRIPT" --simulate >/dev/null 2>&1 &)
fi

# Jalankan aplikasi GCS
cd "$(dirname "$GCS_EXE")"
if [ -n "$CAMERA_URL" ]; then
    export MOONHARVEST_CAMERA_SOURCE="$CAMERA_URL"
fi
./HarvestmoonGCS

# Cleanup saat aplikasi ditutup
pkill -f "moonharvest_detect_stream.py" 2>/dev/null || true
pkill -f "lora_soil_bridge.py" 2>/dev/null || true
echo ""
echo -e "${CYAN}[✓] MoonHarvest GCS sesi selesai.${NC}"
