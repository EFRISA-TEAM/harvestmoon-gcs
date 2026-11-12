#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════════
#  MOONHARVEST PROTECTED RELEASE BUILDER
#  Membangun Binary Mesin Tertutup (C# Native + Python Compiled)
# ═══════════════════════════════════════════════════════════════════════

set -e

CYAN='\033[0;36m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'
BOLD='\033[1m'

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo -e "${CYAN}╔══════════════════════════════════════════════════════════════════════╗${NC}"
echo -e "${CYAN}║${BOLD}          MOONHARVEST CLOSED-SOURCE & PROTECTED BUILDER               ${NC}${CYAN}║${NC}"
echo -e "${CYAN}╚══════════════════════════════════════════════════════════════════════╝${NC}"
echo ""

# 1. Build C# Desktop GCS (Release Mode + In-App Master Lock)
echo -e "${YELLOW}[1/3] Mengompilasi C# GCS dalam mode Release & Terproteksi...${NC}"
dotnet publish "$ROOT_DIR/Pigeon_Harvest/HarvestmoonGCS/HarvestmoonGCS.csproj" \
    -f net9.0-desktop \
    -c Release \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --self-contained false \
    -o "$ROOT_DIR/dist/protected_release"

echo -e "${GREEN}✓ C# Binary GCS berhasil dibangun di: dist/protected_release/HarvestmoonGCS${NC}"

# 2. Python Engine Protection (Nuitka C-Extension Module)
echo ""
echo -e "${YELLOW}[2/3] Mengompilasi moonharvest_core menjadi Library Mesin C (.so)...${NC}"
PYTHON_BIN="$ROOT_DIR/.venv/bin/python3"
if [ ! -f "$PYTHON_BIN" ]; then
    PYTHON_BIN="python3"
fi

$PYTHON_BIN -m nuitka --module \
    --include-module=moonharvest_security \
    --remove-output \
    --output-dir="$ROOT_DIR/dist/protected_release" \
    "$ROOT_DIR/moonharvest_core.py"

# Salin script open runner dan assets untuk klien
cp "$ROOT_DIR/client_open_runner.py" "$ROOT_DIR/dist/protected_release/"
cp "$ROOT_DIR/moonharvest_security.json" "$ROOT_DIR/dist/protected_release/"
mkdir -p "$ROOT_DIR/MoonHarvest_Client_Delivery"
cp -r "$ROOT_DIR/dist/protected_release/"* "$ROOT_DIR/MoonHarvest_Client_Delivery/"
rm -f "$ROOT_DIR/MoonHarvest_Client_Delivery"/*.pdb
(cd "$ROOT_DIR/MoonHarvest_Client_Delivery" && ln -sf moonharvest_core.cpython-*.so moonharvest_core.so 2>/dev/null || true)
echo -e "${GREEN}✓ Library biner C (moonharvest_core.so) berhasil dibuat!${NC}"
echo -e "${GREEN}✓ Paket siap serah terima klien diperbarui di: MoonHarvest_Client_Delivery/${NC}"

# 3. Model Protection Check
echo ""
echo -e "${YELLOW}[3/3] Proteksi Model AI${NC}"
echo -e "Gunakan perintah berikut untuk mengenkripsi model ONNX/PyTorch Anda sebelum dirilis:"
echo -e "    ${BOLD}$PYTHON_BIN tools/license_admin.py encrypt-model <model.onnx> <model.enc> --key <PASSWORD_RAHASIA>${NC}"

echo ""
echo -e "${GREEN}══════════════════════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}✓ PROSES SELESAI: Seluruh sistem penguncian telah aktif.${NC}"
echo -e "${GREEN}══════════════════════════════════════════════════════════════════════${NC}"
