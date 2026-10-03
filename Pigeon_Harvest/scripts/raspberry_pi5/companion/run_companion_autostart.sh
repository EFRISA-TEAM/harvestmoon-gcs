#!/bin/bash
# =============================================================================
# MoonHarvest - Universal Autostart Runner (Raspberry Pi 5 & Orange Pi 3B)
# =============================================================================
# Dijalankan otomatis oleh drone-companion.service saat boot:
#   1. Tunggu NetworkManager siap
#   2. Sambungkan ke WiFi (prioritas: TP-Link_0CBA > mboh > cadangan)
#   3. Tentukan target GCS secara OTOMATIS (broadcast subnet atau IP tersimpan)
#   4. Jalankan companion: MAVLink (14550) + video H.264 (5600) + MJPEG (8080)
# =============================================================================

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPANION_DIR="${SCRIPT_DIR}"
GCS_CONF="/etc/moonharvest/gcs_ip.conf"

# Prioritas jaringan WiFi: "SSID<TAB>PASSWORD" (urutan atas = prioritas utama)
WIFI_NETWORKS="TP-Link_0CBA	03906910
mboh	1122334455
SUMBER REJEKI 07. tp-link	sumber rejeki tplink
EFRISA_TEAM	Bismillahjuara"

GCS_PORT="14550"
VIDEO_PORT="5600"
HTTP_PORT="8080"
CAM_INDEX="auto"
CAM_WIDTH="1280"
CAM_HEIGHT="720"
CAM_FPS="30"
BAUDRATE="0"

log() { echo "[autostart] $(date '+%H:%M:%S') $*"; }

# --- 1. Tunggu NetworkManager siap --------------------------------------------
log "Menunggu NetworkManager siap..."
for i in $(seq 1 30); do
    if nmcli -t -f RUNNING general status 2>/dev/null | grep -q "running"; then
        log "NetworkManager siap (percobaan ke-$i)."
        break
    fi
    sleep 1
done

# --- 2. Sambungkan ke WiFi ----------------------------------------------------
connected_ssid=""

CURRENT_SSID="$(nmcli -t -f active,ssid dev wifi 2>/dev/null | grep '^yes' | cut -d: -f2 | head -1)"
while IFS=$'\t' read -r ssid pass; do
    [ -n "$ssid" ] || continue
    if [ "$CURRENT_SSID" = "$ssid" ]; then
        log "Sudah terhubung ke '$ssid'."
        connected_ssid="$ssid"
        break
    fi
done <<< "$WIFI_NETWORKS"

if [ -z "$connected_ssid" ]; then
    while IFS=$'\t' read -r ssid pass; do
        [ -n "$ssid" ] || continue
        log "Mencoba WiFi '$ssid'..."

        ok=0
        for i in $(seq 1 3); do
            if [ -n "$pass" ]; then
                nmcli dev wifi connect "$ssid" password "$pass" >/dev/null 2>&1 && ok=1 && break
            else
                nmcli con up "$ssid" >/dev/null 2>&1 && ok=1 && break
            fi
            sleep 2
        done

        if [ "$ok" = "1" ]; then
            log "✅ Terhubung ke '$ssid'."
            connected_ssid="$ssid"
            break
        fi
        log "  '$ssid' tidak tersedia, lanjut ke kandidat berikutnya."
    done <<< "$WIFI_NETWORKS"
fi

if [ -z "$connected_ssid" ]; then
    log "PERINGATAN: Tidak ada WiFi yang terhubung langsung. Mengandalkan profil NetworkManager."
fi

# Beri waktu DHCP menyelesaikan alamat IP
sleep 3
LOCAL_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
log "IP Perangkat: ${LOCAL_IP:-belum ada}"

# --- 3. Tentukan target GCS otomatis ------------------------------------------
GCS_IP=""

# a) Konfigurasi manual bila ada
if [ -f "$GCS_CONF" ]; then
    GCS_IP="$(grep -oE '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' "$GCS_CONF" | head -1)"
    [ -n "$GCS_IP" ] && log "GCS_IP dari $GCS_CONF: $GCS_IP"
fi

# b) Fallback: alamat broadcast subnet
if [ -z "$GCS_IP" ]; then
    IFACE="$(ip -o -4 route show default 2>/dev/null | awk '{print $5}' | head -1)"
    if [ -z "$IFACE" ]; then
        IFACE="$(nmcli -t -f DEVICE,TYPE,STATE dev status 2>/dev/null | awk -F: '$2=="wifi" && $3=="connected"{print $1; exit}')"
    fi
    if [ -z "$IFACE" ]; then
        IFACE="$(ip -o -4 addr show 2>/dev/null | awk '$2!="lo"{print $2; exit}')"
    fi

    if [ -n "$IFACE" ]; then
        LOCAL_CIDR="$(ip -o -4 addr show dev "$IFACE" 2>/dev/null | awk '{print $4}' | head -1)"
        if [ -n "$LOCAL_CIDR" ]; then
            GCS_IP="$(python3 - "$LOCAL_CIDR" <<'PY'
import ipaddress, sys
try:
    iface = ipaddress.ip_interface(sys.argv[1])
    print(iface.network.broadcast_address)
except Exception:
    pass
PY
)"
            log "Interface $IFACE ($LOCAL_CIDR) -> broadcast GCS: $GCS_IP"
        fi
    fi
fi

if [ -z "$GCS_IP" ]; then
    GCS_IP="255.255.255.255"
fi

log "Target GCS: $GCS_IP (MAVLink :$GCS_PORT, Video UDP :$VIDEO_PORT, Web HUD :$HTTP_PORT)"

# --- 4. Siapkan environment Python --------------------------------------------
cd "$COMPANION_DIR" || { log "ERROR: $COMPANION_DIR tidak ditemukan"; exit 1; }

if [ -d "$COMPANION_DIR/venv" ]; then
    source "$COMPANION_DIR/venv/bin/activate" 2>/dev/null || true
elif [ -d "$COMPANION_DIR/.venv" ]; then
    source "$COMPANION_DIR/.venv/bin/activate" 2>/dev/null || true
elif [ -d "$HOME/orange_pi3b_yolov8n_webcam_test/.venv" ]; then
    source "$HOME/orange_pi3b_yolov8n_webcam_test/.venv/bin/activate" 2>/dev/null || true
fi

if [ ! -f drone_telemetry_stream.py ]; then
    log "ERROR: drone_telemetry_stream.py tidak ditemukan di $COMPANION_DIR"
    exit 1
fi

# --- 5. Jalankan companion ----------------------------------------------------
log "Menjalankan drone_telemetry_stream.py..."
exec python3 drone_telemetry_stream.py \
    --gcs-ip "$GCS_IP" \
    --pixhawk-port auto \
    --baud "$BAUDRATE" \
    --gcs-port "$GCS_PORT" \
    --video-port "$VIDEO_PORT" \
    --http-port "$HTTP_PORT" \
    --cam "$CAM_INDEX" \
    --width "$CAM_WIDTH" \
    --height "$CAM_HEIGHT" \
    --fps "$CAM_FPS"
