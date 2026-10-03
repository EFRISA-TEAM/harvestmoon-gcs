#!/bin/bash
# ═══════════════════════════════════════════════════════════════
#  MoonHarvest — Persiapan Presentasi TEKNOFEST 2026
#  Jalankan:  ./00_persiapan.sh
# ═══════════════════════════════════════════════════════════════

GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

HARVEST=/home/fawwazfa/Program/Harvestmoon
DIR="$(cd "$(dirname "$0")" && pwd)"

echo -e "${GREEN}══════════════════════════════════════════════════${NC}"
echo -e "${GREEN}  MoonHarvest — PERSIAPAN TEKNOFEST 2026          ${NC}"
echo -e "${GREEN}══════════════════════════════════════════════════${NC}"
echo ""

# ── 1. Cek Python venv ──
echo -e "${YELLOW}[1/5] Mengecek Python environment...${NC}"
if [ -x "$HARVEST/Pigeon_Harvest/.venv-yolo/bin/python3" ]; then
    echo -e "${GREEN}  ✓ Python venv tersedia${NC}"
else
    echo -e "${RED}  ✗ Python venv TIDAK ADA. Buat dengan:${NC}"
    echo "    python3 -m venv $HARVEST/Pigeon_Harvest/.venv-yolo"
    echo "    source $HARVEST/Pigeon_Harvest/.venv-yolo/bin/activate"
    echo "    pip install ultralytics opencv-python numpy onnxruntime"
fi

# ── 2. Cek model ONNX ──
echo -e "${YELLOW}[2/5] Mengecek model ONNX...${NC}"
for m in moonharvest-health-cls.onnx moonharvest-health-cls-int8.onnx \
         moonharvest-uav-det.onnx moonharvest-uav-det-int8.onnx; do
    if [ -f "$HARVEST/Pigeon_Harvest/HarvestmoonGCS/Assets/models/$m" ]; then
        sz=$(stat -c%s "$HARVEST/Pigeon_Harvest/HarvestmoonGCS/Assets/models/$m")
        if [ "$sz" -gt 1000 ]; then
            echo -e "${GREEN}  ✓ $m ($((sz/1024)) KB)${NC}"
        else
            echo -e "${RED}  ✗ $m ukuran kecil ($sz bytes) — file bermasalah${NC}"
        fi
    else
        echo -e "${RED}  ✗ $m TIDAK ADA${NC}"
    fi
done

# ── 3. Cek video demo ──
echo -e "${YELLOW}[3/5] Mengecek video demo...${NC}"
for v in stream_multisector_60s.mp4 stream_v7c_final.mp4 YDXJ_fused_only_detected.mp4 YDX_burned.mp4 stream_multisector_survey.mp4; do
    p="$HARVEST/Pigeon_Harvest/HarvestmoonGCS/Assets/demo_videos/$v"
    if [ -f "$p" ]; then
        sz=$(stat -c%s "$p")
        if [ "$sz" -gt 100000 ]; then
            echo -e "${GREEN}  ✓ $v ($((sz/1024/1024)) MB)${NC}"
        else
            echo -e "${RED}  ✗ $v ukuran kecil — GANTI dengan video sawah asli!${NC}"
        fi
    else
        echo -e "${RED}  ✗ $v TIDAK ADA${NC}"
    fi
done

# ── 4. Cek build GCS ──
echo -e "${YELLOW}[4/5] Mengecek aplikasi GCS...${NC}"
if [ -x "$HARVEST/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS" ]; then
    echo -e "${GREEN}  ✓ GCS desktop tersedia${NC}"
else
    echo -e "${RED}  ✗ GCS belum di-build. Jalankan:${NC}"
    echo "    cd $HARVEST/Pigeon_Harvest"
    echo "    dotnet build HarvestmoonGCS/HarvestmoonGCS.csproj -f net9.0-desktop -c Release"
fi

# ── 5. Cek dokumen ──
echo -e "${YELLOW}[5/5] Mengecek dokumen...${NC}"
for d in MANUAL_BOOK.docx PANDUAN_PRESENTASI.docx PANDUAN_POSTER.docx; do
    if [ -f "$DIR/dokumen/$d" ]; then
        echo -e "${GREEN}  ✓ $d${NC}"
    else
        echo -e "${RED}  ✗ $d TIDAK ADA di folder dokumen/${NC}"
    fi
done

echo ""
echo -e "${GREEN}══════════════════════════════════════════════════${NC}"
echo -e "${GREEN}  Selesai. Baca 00_START_HERE.txt untuk langkah ${NC}"
echo -e "${GREEN}  selanjutnya sebelum hari H.                      ${NC}"
echo -e "${GREEN}══════════════════════════════════════════════════${NC}"
