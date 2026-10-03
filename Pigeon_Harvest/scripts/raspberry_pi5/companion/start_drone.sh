#!/bin/bash
# =============================================================================
# Launcher Mandiri: Orange Pi 3B Autonomous Drone Companion
# MoonHarvest - Updated by Antigravity
# =============================================================================

# Port Konfigurasi
GCS_PORT="14550"        # Port Telemetri MAVLink UDP (Dua Arah)
VIDEO_PORT="5600"       # Port Livestream Video UDP RTP H.264
HTTP_PORT="8080"        # Port Web HUD & MJPEG /video_feed

# Kamera & Serial (Auto-detect aktif)
CAM_INDEX="auto"        # auto = scan /dev/video* dan uji MJPG
CAM_WIDTH="1280"
CAM_HEIGHT="720"
CAM_FPS="30"
PIXHAWK_PORT="auto"     # auto = scan /dev/ttyACM*, /dev/ttyUSB*, /dev/ttyS2
BAUDRATE="0"            # 0 = auto-probe 115200 & 57600

# WiFi priority (TP-Link utama, mboh cadangan)
WIFI_LIST=(
    "mboh|1122334455"
    "TP-Link_0CBA|03906910"
    "EFRISA_TEAM|Bismillahjuara"
    "EAGLE|"
)

# =============================================================================
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

log() { echo "[companion] $(date '+%H:%M:%S') $*"; }

# Fix WiFi config files jika perlu (jalankan dengan sudo)
fix_wifi_config() {
    CONN_DIR="/etc/NetworkManager/system-connections"

    # Hapus file mboh dengan suffix yang salah
    for f in "$CONN_DIR"/mboh.nmconnection.*; do
        [ -f "$f" ] && sudo rm -f "$f" && log "Hapus duplikat: $f"
    done

    # Buat mboh.nmconnection yang benar jika belum ada atau rusak
    if ! grep -q "psk=1122334455" "$CONN_DIR/mboh.nmconnection" 2>/dev/null; then
        sudo tee "$CONN_DIR/mboh.nmconnection" > /dev/null << 'NMEOF'
[connection]
id=mboh
uuid=a1b2c3d4-0001-0001-0001-000000000001
type=wifi
autoconnect=true
autoconnect-priority=10

[wifi]
mode=infrastructure
ssid=mboh

[wifi-security]
auth-alg=open
key-mgmt=wpa-psk
psk=1122334455

[ipv4]
method=auto

[ipv6]
addr-gen-mode=default
method=auto
NMEOF
        sudo chmod 600 "$CONN_DIR/mboh.nmconnection"
        log "mboh.nmconnection diperbaiki."
    fi

    # Buat TP-Link_0CBA.nmconnection jika belum ada atau salah password
    if ! grep -q "psk=03906910" "$CONN_DIR/TP-Link_0CBA.nmconnection" 2>/dev/null; then
        sudo tee "$CONN_DIR/TP-Link_0CBA.nmconnection" > /dev/null << 'NMEOF'
[connection]
id=TP-Link_0CBA
uuid=a1b2c3d4-0002-0002-0002-000000000002
type=wifi
autoconnect=true
autoconnect-priority=20

[wifi]
mode=infrastructure
ssid=TP-Link_0CBA

[wifi-security]
auth-alg=open
key-mgmt=wpa-psk
psk=03906910

[ipv4]
method=auto

[ipv6]
addr-gen-mode=default
method=auto
NMEOF
        sudo chmod 600 "$CONN_DIR/TP-Link_0CBA.nmconnection"
        log "TP-Link_0CBA.nmconnection diperbaiki."
    fi

    # Reload NetworkManager agar baca file baru
    sudo nmcli connection reload 2>/dev/null || true
}

# Sambungkan ke WiFi (coba semua kandidat berurutan)
ensure_wifi() {
    ACTIVE_SSID=$(nmcli -t -f active,ssid dev wifi 2>/dev/null | grep '^yes:' | cut -d: -f2 | head -1)
    if [ -n "$ACTIVE_SSID" ]; then
        log "WiFi aktif: '$ACTIVE_SSID'"
        return 0
    fi

    log "Belum terhubung WiFi, mencoba kandidat..."
    for entry in "${WIFI_LIST[@]}"; do
        ssid="${entry%%|*}"
        pass="${entry##*|}"
        log "Mencoba '$ssid'..."
        if [ -n "$pass" ]; then
            nmcli dev wifi connect "$ssid" password "$pass" >/dev/null 2>&1 && log "✅ Terhubung ke '$ssid'" && return 0
        else
            nmcli con up "$ssid" >/dev/null 2>&1 && log "✅ Terhubung ke '$ssid'" && return 0
        fi
        log "  '$ssid' gagal, lanjut..."
    done

    log "⚠️ Tidak bisa terhubung ke WiFi mana pun."
}

# Tentukan GCS_IP dari subnet yang aktif (broadcast)
get_gcs_ip() {
    IFACE="$(ip -o -4 route show default 2>/dev/null | awk '{print $5}' | head -1)"
    if [ -z "$IFACE" ]; then
        IFACE="$(nmcli -t -f DEVICE,TYPE,STATE dev 2>/dev/null | awk -F: '$2=="wifi"&&$3=="connected"{print $1;exit}')"
    fi
    if [ -n "$IFACE" ]; then
        LOCAL_CIDR="$(ip -o -4 addr show dev "$IFACE" 2>/dev/null | awk '{print $4}' | head -1)"
        if [ -n "$LOCAL_CIDR" ]; then
            python3 -c "
import ipaddress, sys
try:
    iface = ipaddress.ip_interface(sys.argv[1])
    print(iface.network.broadcast_address)
except: pass
" "$LOCAL_CIDR"
            return
        fi
    fi
    echo "255.255.255.255"
}

# Aktifkan Python Virtual Environment
if [ -d "$HOME/orange_pi3b_yolov8n_webcam_test/.venv" ]; then
    source "$HOME/orange_pi3b_yolov8n_webcam_test/.venv/bin/activate" 2>/dev/null || true
elif [ -d "$HOME/venv" ]; then
    source "$HOME/venv/bin/activate" 2>/dev/null || true
fi

# ---- MAIN ----
log "=== MoonHarvest Drone Companion Start ==="

# Tunggu NetworkManager siap
for i in $(seq 1 20); do
    nmcli -t -f RUNNING general status 2>/dev/null | grep -q "running" && break
    sleep 1
done

# Fix konfigurasi WiFi
fix_wifi_config

# Sambungkan WiFi
ensure_wifi
sleep 3

# Tentukan target GCS
GCS_IP="$(get_gcs_ip)"
LOCAL_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
log "Orange Pi IP: ${LOCAL_IP:-belum ada} → GCS broadcast: $GCS_IP"

# Jalankan companion (dengan restart otomatis)
while true; do
    log "======================================================"
    log "  MENJALANKAN DRONE COMPANION - GCS=$GCS_IP"
    log "======================================================"

    python3 drone_telemetry_stream.py \
        --gcs-ip "$GCS_IP" \
        --pixhawk-port "$PIXHAWK_PORT" \
        --baud "$BAUDRATE" \
        --gcs-port "$GCS_PORT" \
        --video-port "$VIDEO_PORT" \
        --http-port "$HTTP_PORT" \
        --cam "$CAM_INDEX" \
        --width "$CAM_WIDTH" \
        --height "$CAM_HEIGHT" \
        --fps "$CAM_FPS"

    log "Companion berhenti, restart dalam 3 detik..."
    sleep 3
    ensure_wifi
    GCS_IP="$(get_gcs_ip)"
done
