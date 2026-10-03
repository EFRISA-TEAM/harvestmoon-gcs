#!/bin/bash
# =============================================================================
# MoonHarvest - Autostart runner untuk Orange Pi 3B (drone companion)
#
# Dijalankan otomatis oleh drone-companion.service saat boot:
#   1. Tunggu NetworkManager siap
#   2. Sambungkan ke WiFi (beberapa kandidat, dicoba berurutan)
#   3. Tentukan target GCS secara OTOMATIS
#   4. Jalankan companion: MAVLink (14550) + video H.264 (5600) + MJPEG (8080)
#
# JARINGAN WIFI (dicoba dari kandidat teratas):
#   __WIFI_NETWORKS__
#
# TARGET GCS OTOMATIS (tidak perlu tahu IP laptop):
#   a. Jika /etc/moonharvest/gcs_ip.conf ada dan berisi IP -> pakai itu.
#   b. Jika tidak -> pakai ALAMAT BROADCAST subnet (mis. 192.168.1.255).
#      Laptop GCS cukup mendengarkan di 0.0.0.0:14550 / udpsrc:5600, sehingga
#      IP laptop boleh berubah kapan saja (DHCP) tanpa perlu setup ulang.
#
# Tidak butuh login desktop — berjalan sebagai service systemd.
# =============================================================================

set -u

COMPANION_DIR="/home/orangepi/drone_companion"
GCS_CONF="/etc/moonharvest/gcs_ip.conf"

# Kandidat jaringan WiFi: "SSID<TAB>PASSWORD" (urutan = prioritas utama ke bawah)
WIFI_NETWORKS="TP-Link_0CBA	03906910
SUMBER REJEKI 07. tp-link	sumber rejeki tplink
EFRISA_TEAM	Bismillahjuara
mboh	1122334455"

GCS_PORT="14550"
VIDEO_PORT="5600"
CAM_INDEX="auto"
CAM_WIDTH="1280"
CAM_HEIGHT="720"
CAM_FPS="30"

log() { echo "[autostart] $*"; }

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
# Coba setiap kandidat sampai salah satu berhasil.
connected_ssid=""

# Sudah terhubung ke salah satu kandidat?
CURRENT_SSID="$(nmcli -t -f active,ssid dev wifi 2>/dev/null | grep '^yes' | cut -d: -f2 | head -1)"
while IFS=$'\t' read -r ssid pass; do
    [ -n "$ssid" ] || continue
    if [ "$CURRENT_SSID" = "$ssid" ]; then
        log "Sudah terhubung ke '$ssid'."
        connected_ssid="$ssid"
        break
    fi
done <<< "$WIFI_NETWORKS"

# Jika belum, coba sambungkan ke tiap kandidat
if [ -z "$connected_ssid" ]; then
    while IFS=$'\t' read -r ssid pass; do
        [ -n "$ssid" ] || continue
        log "Mencoba WiFi '$ssid'..."

        ok=0
        for i in $(seq 1 3); do
            if nmcli dev wifi connect "$ssid" password "$pass" >/dev/null 2>&1; then
                ok=1
                break
            fi
            log "  percobaan ke-$i gagal, ulangi..."
            sleep 2
        done

        if [ "$ok" = "1" ]; then
            log "Terhubung ke '$ssid'."
            connected_ssid="$ssid"
            break
        fi
        log "  '$ssid' tidak tersedia, lanjut ke kandidat berikutnya."
    done <<< "$WIFI_NETWORKS"
fi

if [ -z "$connected_ssid" ]; then
    log "PERINGATAN: tidak bisa terhubung ke kandidat WiFi mana pun."
    log "           Mengandalkan profil NetworkManager yang tersimpan."
fi

# Beri waktu DHCP menyelesaikan alamat IP
sleep 3
LOCAL_IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
log "IP Orange Pi: ${LOCAL_IP:-belum ada}"

# --- 3. Tentukan target GCS otomatis ------------------------------------------
GCS_IP=""

# a) Konfigurasi manual bila ada
if [ -f "$GCS_CONF" ]; then
    GCS_IP="$(grep -oE '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+' "$GCS_CONF" | head -1)"
    [ -n "$GCS_IP" ] && log "GCS_IP dari $GCS_CONF: $GCS_IP"
fi

# b) Fallback: alamat broadcast subnet (tahan perubahan IP laptop)
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
            log "Interface $IFACE ($LOCAL_CIDR) -> broadcast $GCS_IP"
        fi
    fi
fi

if [ -z "$GCS_IP" ]; then
    log "PERINGATAN: tidak bisa menentukan target GCS. Memakai 255.255.255.255"
    GCS_IP="255.255.255.255"
fi

log "Target GCS: $GCS_IP (MAVLink :$GCS_PORT, Video :$VIDEO_PORT)"

# --- 4. Siapkan environment Python --------------------------------------------
cd "$COMPANION_DIR" || { log "ERROR: $COMPANION_DIR tidak ditemukan"; exit 1; }

if [ -d "$HOME/orange_pi3b_yolov8n_webcam_test/.venv" ]; then
    # shellcheck disable=SC1091
    source "$HOME/orange_pi3b_yolov8n_webcam_test/.venv/bin/activate" 2>/dev/null || true
elif [ -d "$HOME/venv" ]; then
    # shellcheck disable=SC1091
    source "$HOME/venv/bin/activate" 2>/dev/null || true
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
    --baud 0 \
    --gcs-port "$GCS_PORT" \
    --video-port "$VIDEO_PORT" \
    --cam "$CAM_INDEX" \
    --width "$CAM_WIDTH" \
    --height "$CAM_HEIGHT" \
    --fps "$CAM_FPS"
