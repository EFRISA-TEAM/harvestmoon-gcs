#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
#  MOONHARVEST GROUND CONTROL STATION (GCS)
#  Commercial & Flight Operation Launcher
#  Dual Mode: Real Flight (UAV Lapangan) & Demo Presentation (PDP SINTA 2)
# ═══════════════════════════════════════════════════════════════════════

set -e

CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[1;34m'
MAGENTA='\033[1;35m'
RED='\033[0;31m'
BOLD='\033[1m'
NC='\033[0m'

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

clear 2>/dev/null || true
echo -e "${CYAN}╔══════════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${CYAN}║${BOLD}             MOONHARVEST GROUND CONTROL STATION (GCS)                 ${NC}${CYAN}║${NC}"
echo -e "${CYAN}║${NC}       Sistem Pemantauan Defisit Air & Kesehatan Tanaman Padi          ${CYAN}║${NC}"
echo -e "${CYAN}║${YELLOW}           Dual Mode: Real Flight Operation & Riset Demo              ${NC}${CYAN}║${NC}"
echo -e "${CYAN}╚══════════════════════════════════════════════════════════════════════╝${NC}"
echo ""

# Periksa binary
GCS_EXE="$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS"
if [ ! -f "$GCS_EXE" ]; then
    GCS_EXE="$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/bin/Debug/net9.0-desktop/HarvestmoonGCS"
fi

if [ ! -f "$GCS_EXE" ]; then
    echo -e "${YELLOW}[!] Binary GCS belum tersedia. Melakukan kompilasi...${NC}"
    dotnet build "$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/HarvestmoonGCS.csproj" -f net9.0-desktop -c Release
    GCS_EXE="$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS"
fi

echo -e "${GREEN}✓ Binary GCS Siap:${NC} $GCS_EXE"

# Menu Pilihan
echo ""
echo -e "${BOLD}Pilih Mode Operasi:${NC}"
echo -e "  ${CYAN}[1]${NC} ${BOLD}Mode Demo / Presentasi (1-Klik Siap Skenario PDP SINTA 2)${NC}"
echo -e "      • Video Skenario Multi-Sektor (Subang, Karawang, Majalengka, Bogor)"
echo -e "      • Telemetri UAV Sintetik & LoRa IoT Sensor Tanah Otomatis"
echo -e "      • AI YOLOv8/YOLO11 Terbakar & HUD Tanpa Lag"
echo ""
echo -e "  ${GREEN}[2]${NC} ${BOLD}Mode Real Flight (Operasi Lapangan dengan Drone Asli)${NC}"
echo -e "      • Telemetri MAVLink Langsung (Serial USB / UDP 14550 / SIYI HM30)"
echo -e "      • Stream Kamera Live (USB WebCam / RTSP / Companion Computer)"
echo -e "      • Dongle Receiver LoRa Ground Truth USB"
echo -e "      • Upload/Download Misi Autopilot & Geofence ke Pixhawk"
echo ""
echo -e "  ${MAGENTA}[3]${NC} ${BOLD}Jalankan Python YOLO Stream Detector Standalone${NC}"
echo -e "  ${BLUE}[4]${NC} ${BOLD}Recompile / Rebuild Ulang Binary GCS${NC}"
echo -e "  ${RED}[0]${NC} Keluar"
echo ""

read -p "Masukkan pilihan [1/2/3/4/0] (default: 1): " PILIHAN
PILIHAN=${PILIHAN:-1}

case "$PILIHAN" in
    1)
        echo ""
        echo -e "${CYAN}==> Menjalankan Mode Demo Presentasi...${NC}"
        
        # Bersihkan sisa proses lama
        pkill -f "moonharvest_detect_stream.py" 2>/dev/null || true
        
        # Jalankan LoRa soil simulator di background jika belum berjalan
        LORA_SCRIPT="$ROOT_DIR/scripts/lora_soil_bridge.py"
        if [ -f "$LORA_SCRIPT" ] && ! pgrep -f "lora_soil_bridge.py" >/dev/null 2>&1; then
            echo -e "${CYAN}[LoRa] Menjalankan Simulator 4-Node Sensor Tanah PDP (UDP:14552)...${NC}"
            (python3 "$LORA_SCRIPT" --simulate >/dev/null 2>&1 &)
        fi
        
        # Jalankan VIRA Lite jika ada
        VIRA_BIN="$ROOT_DIR/bin/vira-lite"
        if [ -f "$VIRA_BIN" ] && ! pgrep -f "vira-lite" >/dev/null 2>&1; then
            echo -e "${CYAN}[VIRA] Menjalankan VIRA Lite Agent di background...${NC}"
            (cd "$ROOT_DIR/bin" && nohup "$VIRA_BIN" >/dev/null 2>&1 &)
        fi
        
        cd "$(dirname "$GCS_EXE")"
        ./HarvestmoonGCS
        
        # Cleanup
        pkill -f "lora_soil_bridge.py" 2>/dev/null || true
        pkill -f "moonharvest_detect_stream.py" 2>/dev/null || true
        ;;
        
    2)
        echo ""
        echo -e "${GREEN}==> Menjalankan Mode Real Flight (Penerbangan Lapangan)...${NC}"
        echo -e "${YELLOW}Petunjuk Operasi Lapangan:${NC}"
        echo -e " 1. Hubungkan kabel USB Telemetri Radio (Holybro/SiK) atau hubungkan ke WiFi drone."
        echo -e " 2. Di layar GCS, klik tombol 'DISCONNECTED' di kanan atas untuk membuka Connect Dialog."
        echo -e " 3. Pilih 'UDP' (port 14550) atau 'SERIAL' (/dev/ttyACM0 atau /dev/ttyUSB0)."
        echo -e " 4. Untuk kamera langsung, buka menu 'Camera' dan pilih sumber video atau masukkan URL RTSP."
        echo ""
        
        # Pastikan izin serial port dialout
        if groups | grep -q "dialout"; then
            echo -e "${GREEN}✓ Akses port serial (dialout) aktif.${NC}"
        else
            echo -e "${YELLOW}⚠ Catatan: Jika port serial terkunci, jalankan: sudo usermod -a -G dialout \$USER${NC}"
        fi
        
        read -p "Masukkan URL kamera RTSP/HTTP (kosongkan untuk default USB camera 0): " CAM_INPUT
        if [ -n "$CAM_INPUT" ]; then
            export MOONHARVEST_CAMERA_SOURCE="$CAM_INPUT"
        fi
        
        cd "$(dirname "$GCS_EXE")"
        ./HarvestmoonGCS
        ;;
        
    3)
        echo ""
        echo -e "${MAGENTA}==> Menjalankan Standalone Python YOLO Stream Detector...${NC}"
        if [ -f "$ROOT_DIR/Pigeon_Harvest/.venv-yolo/bin/python3" ]; then
            "$ROOT_DIR/Pigeon_Harvest/.venv-yolo/bin/python3" "$ROOT_DIR/moonharvest_detect_stream.py" "$@"
        else
            python3 "$ROOT_DIR/moonharvest_detect_stream.py" "$@"
        fi
        ;;
        
    4)
        echo ""
        echo -e "${BLUE}==> Melakukan Rebuild Binary GCS (Release Desktop)...${NC}"
        dotnet build "$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/HarvestmoonGCS.csproj" -c Release -f net9.0-desktop
        echo -e "${GREEN}✓ Rebuild Selesai! Jalankan kembali skrip ini.${NC}"
        ;;
        
    0)
        echo "Keluar."
        exit 0
        ;;
        
    *)
        echo "Pilihan tidak valid."
        exit 1
        ;;
esac
