#!/bin/bash
# =============================================================================
# MoonHarvest - Setup Orange Pi 3B (UAV) untuk jaringan WiFi router/hotspot
#
# Tujuan:
#   1. Orange Pi join WiFi kandidat (bisa lebih dari satu, bertingkat)
#   2. Companion jalan otomatis saat boot (MAVLink + video ke GCS)
#
# Cara pakai (SD card Orange Pi tercolok ke laptop):
#   ./setup_orange_pi_tplink.sh "SSID1:PASSWORD1" ["SSID2:PASSWORD2" ...]
#
# Contoh (dua jaringan: utama + cadangan):
#   sudo ./setup_orange_pi_tplink.sh "EFRISA_TEAM:Bismillahjuara" "TP-Link_0CBA:03906910"
#
# Jaringan pertama = prioritas utama; berikutnya dicoba jika yang utama tidak ada.
# =============================================================================
set -euo pipefail

# --- Kumpulkan kandidat jaringan dari argumen --------------------------------
WIFI_NETWORKS=""
PARSED_SSIDS=()
PARSED_PASSWORDS=()

for arg in "$@"; do
    case "$arg" in
        --pin-gcs-ip) continue ;;
    esac

    if [[ "$arg" != *:* ]]; then
        echo "[ERROR] Format argumen harus \"SSID:PASSWORD\" (contoh: EFRISA_TEAM:Bismillahjuara)"
        echo "        Diterima: '$arg'"
        exit 1
    fi

    s="${arg%%:*}"     # sebelum titik dua pertama
    p="${arg#*:}"      # sesudahnya (password boleh mengandung ':')
    if [ -z "$s" ]; then
        echo "[ERROR] SSID kosong pada argumen '$arg'"
        exit 1
    fi

    PARSED_SSIDS+=("$s")
    PARSED_PASSWORDS+=("$p")
    WIFI_NETWORKS+="${s}"$'\t'"${p}"$'\n'
done

if [ "${#PARSED_SSIDS[@]}" -eq 0 ]; then
    echo "[INFO] Tidak ada argumen WiFi, menggunakan daftar prioritas default (TP-Link Router utama):"
    DEFAULTS=("TP-Link_0CBA:03906910" "SUMBER REJEKI 07. tp-link:sumber rejeki tplink" "EFRISA_TEAM:Bismillahjuara" "mboh:1122334455")
    for arg in "${DEFAULTS[@]}"; do
        s="${arg%%:*}"
        p="${arg#*:}"
        PARSED_SSIDS+=("$s")
        PARSED_PASSWORDS+=("$p")
        WIFI_NETWORKS+="${s}"$'\t'"${p}"$'\n'
    done
fi

# SSID utama (prioritas pertama) dipakai untuk nama file & service
SSID="${PARSED_SSIDS[0]}"
WIFI_PASSWORD="${PARSED_PASSWORDS[0]}"
GCS_IP=""

echo "[INFO] Kandidat jaringan WiFi (berurutan):"
for i in "${!PARSED_SSIDS[@]}"; do
    echo "        $((i+1)). ${PARSED_SSIDS[$i]}"
done

# --- Lokasi SD card Orange Pi (bisa disesuaikan) ------------------------------
# OPI_MOUNT bisa diisi manual. Jika kosong, dicari otomatis di /media/*/opi_root
# (mount point SD card biasanya dimiliki root, jadi skrip ini dijalankan dgn sudo).
find_mount() {
    if [ -n "${OPI_MOUNT:-}" ]; then
        if [ -d "$OPI_MOUNT/etc/NetworkManager" ] || [ -d "$OPI_MOUNT/home/orangepi" ]; then
            echo "$OPI_MOUNT"
            return 0
        fi
        return 1
    fi

    local cand
    for cand in /media/*/opi_root /media/*/rootfs /mnt/opi_root \
                "/media/$USER/opi_root" "/media/$USER/rootfs"; do
        if [ -d "$cand/etc/NetworkManager" ] || [ -d "$cand/home/orangepi" ]; then
            echo "$cand"
            return 0
        fi
    done
    return 1
}

MOUNT="$(find_mount || true)"
if [ -z "$MOUNT" ]; then
    echo "[ERROR] SD card Orange Pi tidak ditemukan."
    echo "        Pastikan SD card tercolok dan ter-mount, lalu jalankan ulang."
    echo "        Kandidat lokasi: /media/*/opi_root /mnt/opi_root (atau set OPI_MOUNT=...)"
    exit 1
fi

echo "[INFO] SD card Orange Pi: $MOUNT"

# --- 1. Deteksi IP laptop (GCS) -----------------------------------------------
# Catatan: runner memakai ALAMAT BROADCAST subnet secara otomatis, sehingga IP
# laptop boleh berubah. GCS_IP di sini hanya untuk start_drone.sh (jalur manual).
if [ -z "$GCS_IP" ]; then
    for iface in $(ls /sys/class/net/ | grep -vE '^(lo|docker|virbr|veth|br-)'); do
        ip="$(ip -4 -o addr show "$iface" 2>/dev/null | awk '{print $4}' | cut -d/ -f1 | head -1 || true)"
        [ -n "$ip" ] && { GCS_IP="$ip"; break; }
    done
fi
[ -z "$GCS_IP" ] && GCS_IP="0.0.0.0"

echo "[INFO] SSID utama     : $SSID"
echo "[INFO] GCS_IP (laptop): $GCS_IP"

# --- 2. Tulis profil WiFi NetworkManager ke SD card ---------------------------
# Semua kandidat ditulis sebagai profil terpisah. Prioritas menurun sesuai urutan
# argumen, sehingga NetworkManager memilih yang pertama tersedia.
NM_DIR="$MOUNT/etc/NetworkManager/system-connections"
if [ ! -d "$NM_DIR" ]; then
    echo "[WARN] $NM_DIR tidak ada - membuat direktori."
    mkdir -p "$NM_DIR"
fi

priority=100
for i in "${!PARSED_SSIDS[@]}"; do
    c_ssid="${PARSED_SSIDS[$i]}"
    c_pass="${PARSED_PASSWORDS[$i]}"
    c_uuid="$(cat /proc/sys/kernel/random/uuid)"
    c_safe="$(echo "$c_ssid" | tr ' ' '_')"
    c_file="$NM_DIR/${c_safe}.nmconnection"

    cat << CONN_EOF > "$c_file"
[connection]
id=$c_ssid
uuid=$c_uuid
type=wifi
autoconnect=true
autoconnect-priority=$priority

[wifi]
mode=infrastructure
ssid=$c_ssid

[wifi-security]
auth-alg=open
key-mgmt=wpa-psk
psk=$c_pass

[ipv4]
method=auto

[ipv6]
addr-gen-mode=default
method=auto
CONN_EOF

    chmod 600 "$c_file"
    echo "[OK] Profil WiFi ditulis: $c_ssid (priority=$priority)"
    priority=$((priority - 10))
done
sync

# Nonaktifkan profil hotspot lama "mboh" agar Orange Pi tidak balik ke sana
OLD_MBOH="$NM_DIR/mboh.nmconnection"
if [ -f "$OLD_MBOH" ]; then
    mv "$OLD_MBOH" "${OLD_MBOH}.disabled"
    echo "[OK] Profil lama 'mboh' dinonaktifkan (rename .disabled)."
fi

# --- 3. Pasang autostart companion (MAVLink + video saat boot) ----------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_AUTOSTART="$SCRIPT_DIR/orange_pi/run_companion_autostart.sh"
SRC_SERVICE="$SCRIPT_DIR/orange_pi/drone-companion.service"
SRC_STREAMER="$SCRIPT_DIR/orange_pi/drone_telemetry_stream.py"

DRONE_DIR="$MOUNT/home/orangepi/drone_companion"
mkdir -p "$DRONE_DIR"

if [ -f "$SRC_STREAMER" ]; then
    cp "$SRC_STREAMER" "$DRONE_DIR/drone_telemetry_stream.py"
    chmod 755 "$DRONE_DIR/drone_telemetry_stream.py"
    echo "[OK] Script companion dipasang: drone_telemetry_stream.py"
fi

if [ -f "$SRC_AUTOSTART" ]; then
    # Salin runner + isi daftar kandidat jaringan.
    # WIFI_NETWORKS berisi "SSID<TAB>PASSWORD" per baris.
    python3 - "$SRC_AUTOSTART" "$DRONE_DIR/run_companion_autostart.sh" "${PARSED_SSIDS[@]}" -- "${PARSED_PASSWORDS[@]}" <<'PY'
import sys

src, dst = sys.argv[1], sys.argv[2]
rest = sys.argv[3:]
sep = rest.index("--")
ssids, passes = rest[:sep], rest[sep+1:]

lines = "".join(f"{s}\t{p}\n" for s, p in zip(ssids, passes))

with open(src, "r", encoding="utf-8") as f:
    content = f.read()
content = content.replace("__WIFI_NETWORKS__", lines.rstrip("\n"))
with open(dst, "w", encoding="utf-8") as f:
    f.write(content)
PY
    chmod 755 "$DRONE_DIR/run_companion_autostart.sh"
    echo "[OK] Runner autostart dipasang: run_companion_autostart.sh"
else
    echo "[WARN] $SRC_AUTOSTART tidak ditemukan - autostart tidak dipasang."
fi

# Service systemd
SYSTEMD_DIR="$MOUNT/etc/systemd/system"
mkdir -p "$SYSTEMD_DIR"
if [ -f "$SRC_SERVICE" ]; then
    cp "$SRC_SERVICE" "$SYSTEMD_DIR/drone-companion.service"
    echo "[OK] Service dipasang: drone-companion.service"

    # Enable agar jalan otomatis saat boot (tanpa login desktop)
    WANTS_DIR="$SYSTEMD_DIR/multi-user.target.wants"
    mkdir -p "$WANTS_DIR"
    ln -sfn /etc/systemd/system/drone-companion.service \
            "$WANTS_DIR/drone-companion.service"
    echo "[OK] drone-companion.service di-ENABLE (aktif saat boot)."
else
    echo "[WARN] $SRC_SERVICE tidak ditemukan - service tidak dipasang."
fi

# Nonaktifkan autostart desktop lama (butuh login GUI, tidak andal untuk headless)
OLD_DESKTOP="$MOUNT/home/orangepi/.config/autostart/connect-mboh.desktop"
if [ -f "$OLD_DESKTOP" ]; then
    mv "$OLD_DESKTOP" "${OLD_DESKTOP}.disabled"
    echo "[OK] Autostart desktop lama 'connect-mboh.desktop' dinonaktifkan."
fi

# Target GCS opsional: tulis IP unicast bila diminta eksplisit.
# Bila TIDAK ditulis, runner memakai alamat broadcast subnet secara otomatis,
# sehingga IP laptop boleh berubah tanpa setup ulang.
PIN_GCS=0
for a in "$@"; do
    [ "$a" = "--pin-gcs-ip" ] && PIN_GCS=1
done
if [ "$PIN_GCS" = "1" ]; then
    mkdir -p "$MOUNT/etc/moonharvest"
    echo "$GCS_IP" > "$MOUNT/etc/moonharvest/gcs_ip.conf"
    echo "[OK] GCS IP di-pin ke $GCS_IP (/etc/moonharvest/gcs_ip.conf)."
else
    echo "[INFO] GCS target = broadcast subnet otomatis (IP laptop boleh berubah)."
fi

# Tetap perbarui start_drone.sh agar konsisten bila dijalankan manual
START_SH="$DRONE_DIR/start_drone.sh"
if [ -f "$START_SH" ]; then
    cp "$START_SH" "${START_SH}.bak"
    sed -i -E "s|^GCS_IP=.*|GCS_IP=\"$GCS_IP\"|" "$START_SH"
    sed -i -E "s|nmcli dev wifi connect \"[^\"]*\" password \"[^\"]*\"|nmcli dev wifi connect \"$SSID\" password \"$WIFI_PASSWORD\"|g" "$START_SH" || true
    echo "[OK] start_drone.sh (manual) diperbarui."
fi

sync

# --- 4. Ringkasan -------------------------------------------------------------
WIFI_LIST=""
for i in "${!PARSED_SSIDS[@]}"; do
    WIFI_LIST+="      $((i+1)). ${PARSED_SSIDS[$i]}"$'\n'
done

cat << SUMMARY

===================================================================
 SELESAI - sekarang SEMUA OTOMATIS saat Orange Pi dinyalakan:
===================================================================
 1. Cabut SD card dengan AMAN (sudo umount /dev/sda1 /dev/sda2 && sync),
    lalu pasang ke Orange Pi.
 2. Nyalakan Orange Pi. Yang terjadi otomatis:
      - Join WiFi kandidat berikut (berurutan):
$WIFI_LIST      - drone-companion.service jalan saat boot (tanpa login)
      - MAVLink telemetri dikirim ke UDP 14550
      - Video H.264 dikirim ke UDP 5600 (+ MJPEG di http://<ip>:8080/video_feed)
      - Target GCS = broadcast subnet, jadi IP laptop boleh berubah
 3. Di laptop, sambungkan ke salah satu jaringan yang sama, lalu buka MoonHarvest:
      - Telemetri: AUTO-CONNECT (mendengarkan UDP 14550)
      - Video: halaman Camera -> "Drone Companion (UDP H.264 :5600)"
 4. Verifikasi kapan saja dari laptop:
      cd Pigeon_Harvest/HarvestmoonGCS && python3 network_discovery.py scan
===================================================================
SUMMARY

