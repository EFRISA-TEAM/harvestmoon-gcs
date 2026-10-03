#!/bin/bash
# ═══════════════════════════════════════════════════════════════════════════
#  MOONHARVEST — PEMUTAR VIDEO HASIL DETEKSI AI & BOUNDING BOX
#  TEKNOFEST 2026 | Evaluasi Visual Deteksi Kesehatan Tanaman
# ═══════════════════════════════════════════════════════════════════════════

set -e

CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[1;34m'
BOLD='\033[1m'
NC='\033[0m'

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEMO_DIR="$ROOT_DIR/TEKNOFEST_SIAP/demo_video"

# Pastikan player tersedia
PLAYER="mpv"
if ! command -v mpv &> /dev/null; then
    if command -v ffplay &> /dev/null; then
        PLAYER="ffplay -loop 0"
    else
        PLAYER="xdg-open"
    fi
fi

show_menu() {
    clear
    echo -e "${CYAN}╔══════════════════════════════════════════════════════════════════════╗${NC}"
    echo -e "${CYAN}║${BOLD}          EVALUASI VIDEO HASIL DETEKSI AI MOONHARVEST                 ${NC}${CYAN}║${NC}"
    echo -e "${CYAN}║${NC}   Semua video di bawah ini sudah dilengkapi Bounding Box & HUD FHI   ${CYAN}║${NC}"
    echo -e "${CYAN}║${YELLOW}                   TEKNOFEST 2026 — Smart Agriculture                ${NC}${CYAN}║${NC}"
    echo -e "${CYAN}╚══════════════════════════════════════════════════════════════════════╝${NC}"
    echo ""
    echo -e "${BOLD}Pilih Video yang Ingin Anda Nilai/Putar:${NC}"
    echo ""
    echo -e "  ${CYAN}[1]${NC} ${BOLD}Multi-Sektor 60s (Subang · 4 Sektor A-D)${NC}"
    echo -e "      ↳ File: stream_multisector_60s_detected.mp4 (FHI: 85.4% · 60 detik)"
    echo ""
    echo -e "  ${CYAN}[2]${NC} ${BOLD}Sawah 15 HST (Karawang · Padi Muda Bertunas)${NC}"
    echo -e "      ↳ File: stream_v7c_final_detected.mp4 (FHI: 81.6% · 15.5 detik)"
    echo ""
    echo -e "  ${CYAN}[3]${NC} ${BOLD}Hamparan Irigasi & Sawah (Majalengka · Vegetasi Prima)${NC}"
    echo -e "      ↳ File: YDXJ_irigasi_detected.mp4 (FHI: 94.2% · 60 detik)"
    echo ""
    echo -e "  ${CYAN}[4]${NC} ${BOLD}Lahan Kering & Jerami Terbakar (Bogor · Stres Parah)${NC}"
    echo -e "      ↳ File: YDX_burned_detected.mp4 (FHI: 42.5% · 33 detik)"
    echo ""
    echo -e "  ${CYAN}[5]${NC} ${GREEN}${BOLD}Putar Semua Video Berurutan (Playlist Otomatis)${NC}"
    echo -e "      ↳ Memutar Skenario 1 s/d 4 secara bergantian tanpa henti"
    echo ""
    echo -e "  ${CYAN}[6]${NC} ${BLUE}Buka Folder Video di File Manager (Nautilus)${NC}"
    echo ""
    echo -e "  ${CYAN}[0]${NC} Keluar"
    echo ""
    echo -n "Masukkan pilihan [0-6]: "
}

while true; do
    show_menu
    read -r choice
    case $choice in
        1)
            echo -e "${GREEN}Memutar Skenario 1: Multi-Sektor Subang...${NC}"
            $PLAYER "$DEMO_DIR/stream_multisector_60s_detected.mp4"
            ;;
        2)
            echo -e "${GREEN}Memutar Skenario 2: Sawah 15 HST Karawang...${NC}"
            $PLAYER "$DEMO_DIR/stream_v7c_final_detected.mp4"
            ;;
        3)
            echo -e "${GREEN}Memutar Skenario 3: Hamparan Irigasi Majalengka...${NC}"
            $PLAYER "$DEMO_DIR/YDXJ_irigasi_detected.mp4"
            ;;
        4)
            echo -e "${GREEN}Memutar Skenario 4: Lahan Kering Bogor...${NC}"
            $PLAYER "$DEMO_DIR/YDX_burned_detected.mp4"
            ;;
        5)
            echo -e "${GREEN}Memutar Seluruh Video Skenario (Playlist)...${NC}"
            $PLAYER "$DEMO_DIR/stream_multisector_60s_detected.mp4" \
                    "$DEMO_DIR/stream_v7c_final_detected.mp4" \
                    "$DEMO_DIR/YDXJ_irigasi_detected.mp4" \
                    "$DEMO_DIR/YDX_burned_detected.mp4"
            ;;
        6)
            echo -e "${BLUE}Membuka folder video di File Manager...${NC}"
            xdg-open "$DEMO_DIR"
            ;;
        0)
            echo -e "${CYAN}Sampai jumpa!${NC}"
            exit 0
            ;;
        *)
            echo -e "${YELLOW}Pilihan tidak valid. Silakan coba lagi.${NC}"
            sleep 1
            ;;
    esac
done
