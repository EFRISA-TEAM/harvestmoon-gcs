#!/bin/bash
# =============================================================================
# MoonHarvest - One-Click Installer untuk Raspberry Pi 5
# Drone Companion System (Telemetry MAVLink + Dual Video Streaming)
# =============================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Cek apakah dijalankan dengan root/sudo
if [ "$EUID" -ne 0 ]; then
    echo "❌ Harap jalankan script ini dengan sudo:"
    echo "   sudo bash $0"
    exit 1
fi

TARGET_USER="${SUDO_USER:-$USER}"
TARGET_HOME="$(eval echo "~$TARGET_USER")"
INSTALL_DIR="$TARGET_HOME/drone_companion"

echo "=========================================================="
echo "  MOONHARVEST COMPANION INSTALLER - RASPBERRY PI 5"
echo "  Target User: $TARGET_USER"
echo "  Install Dir: $INSTALL_DIR"
echo "=========================================================="

# 1. Update paket dan install dependensi sistem
echo ""
echo "[1/7] Menginstall paket dependensi Raspberry Pi OS (apt)..."
apt-get update -y
apt-get install -y \
    python3 \
    python3-pip \
    python3-venv \
    python3-opencv \
    python3-numpy \
    python3-serial \
    python3-flask \
    python3-requests \
    python3-yaml \
    gstreamer1.0-tools \
    gstreamer1.0-plugins-base \
    gstreamer1.0-plugins-good \
    gstreamer1.0-plugins-bad \
    gstreamer1.0-plugins-ugly \
    gstreamer1.0-x264 \
    v4l-utils \
    network-manager \
    libcamera-tools \
    rsync

# 2. Salin folder companion ke home user
echo ""
echo "[2/7] Menyiapkan direktori companion di $INSTALL_DIR..."
mkdir -p "$INSTALL_DIR/images"
rsync -a "$SCRIPT_DIR/companion/" "$INSTALL_DIR/"

# Salin juga folder YOLO jika ada
if [ -d "$SCRIPT_DIR/companion/yolo_detection" ]; then
    echo "  ✓ Model YOLO & bobot best.pt tersalin."
fi

chown -R "$TARGET_USER:$TARGET_USER" "$INSTALL_DIR"
chmod +x "$INSTALL_DIR"/*.sh "$INSTALL_DIR"/*.py 2>/dev/null || true

# 3. Buat Python Virtual Environment dengan system-site-packages
echo ""
echo "[3/7] Membuat Python Virtual Environment ($INSTALL_DIR/venv)..."
if [ ! -d "$INSTALL_DIR/venv" ]; then
    sudo -u "$TARGET_USER" python3 -m venv --system-site-packages "$INSTALL_DIR/venv"
fi

echo "  Menginstall library Python tambahan (pymavlink, pyserial)..."
sudo -u "$TARGET_USER" "$INSTALL_DIR/venv/bin/pip" install --upgrade pip
sudo -u "$TARGET_USER" "$INSTALL_DIR/venv/bin/pip" install pymavlink pyserial flask requests

# 4. Konfigurasi WiFi NetworkManager
echo ""
echo "[4/7] Memasang profil WiFi NetworkManager..."
NM_DIR="/etc/NetworkManager/system-connections"
mkdir -p "$NM_DIR"

if [ -d "$SCRIPT_DIR/wifi_profiles" ]; then
    cp -f "$SCRIPT_DIR/wifi_profiles"/*.nmconnection "$NM_DIR/" 2>/dev/null || true
    chmod 600 "$NM_DIR"/*.nmconnection 2>/dev/null || true
    echo "  ✓ Profil WiFi TP-Link_0CBA dan mboh terpasang."
    nmcli connection reload 2>/dev/null || true
fi

# 5. User permissions (dialout untuk serial Pixhawk, video untuk kamera)
echo ""
echo "[5/7] Mengatur hak akses user ($TARGET_USER)..."
usermod -a -G dialout,video,netdev "$TARGET_USER"

# 6. Aktifkan Hardware UART di Raspberry Pi (/boot/firmware/config.txt)
echo ""
echo "[6/7] Memeriksa konfigurasi UART di /boot/firmware/config.txt..."
CONFIG_TXT="/boot/firmware/config.txt"
[ ! -f "$CONFIG_TXT" ] && CONFIG_TXT="/boot/config.txt"

if [ -f "$CONFIG_TXT" ]; then
    if ! grep -q "^enable_uart=1" "$CONFIG_TXT"; then
        echo "enable_uart=1" >> "$CONFIG_TXT"
        echo "  ✓ UART diaktifkan (enable_uart=1)."
    fi
    if ! grep -q "^dtoverlay=disable-bt" "$CONFIG_TXT" && ! grep -q "^dtoverlay=miniuart-bt" "$CONFIG_TXT"; then
        echo "dtparam=uart0=on" >> "$CONFIG_TXT"
    fi
fi

# 7. Daftarkan Systemd Service drone-companion.service
echo ""
echo "[7/7] Mendaftarkan systemd service drone-companion..."
SERVICE_FILE="/etc/systemd/system/drone-companion.service"

cat > "$SERVICE_FILE" << EOF
[Unit]
Description=MoonHarvest - Raspberry Pi 5 Drone Companion (Telemetry & Video Stream)
After=network.target network-online.target NetworkManager.service
Wants=network-online.target

[Service]
Type=simple
User=$TARGET_USER
Group=$TARGET_USER
WorkingDirectory=$INSTALL_DIR
ExecStart=/bin/bash $INSTALL_DIR/run_companion_autostart.sh
Restart=always
RestartSec=3
StandardOutput=journal
StandardError=journal
Environment=PYTHONUNBUFFERED=1

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable drone-companion.service

echo ""
echo "=========================================================="
echo "  ✅ INSTALASI RASPBERRY PI 5 BERHASIL SELESAI!"
echo "=========================================================="
echo "Perintah berguna:"
echo "  - Mulai companion sekarang : sudo systemctl start drone-companion"
echo "  - Cek status/log live      : journalctl -u drone-companion -f"
echo "  - Hentikan companion       : sudo systemctl stop drone-companion"
echo "  - Tes kamera langsung      : python3 $INSTALL_DIR/test_camera.py"
echo "  - Tes Pixhawk langsung     : python3 $INSTALL_DIR/test_mavlink.py"
echo "  - Buka Web HUD di browser  : http://<IP_RPI5>:8080/"
echo "=========================================================="
