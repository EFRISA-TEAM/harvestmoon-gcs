#!/bin/bash
# =============================================================================
# Auto-Connect to Wi-Fi "mboh" with password "1122334455"
# =============================================================================

# Tunggu layanan NetworkManager siap
sleep 2

SSID="mboh"
PASSWORD="1122334455"

for i in {1..15}; do
    # Cek apakah sudah terhubung ke SSID mboh
    CURRENT_SSID=$(nmcli -t -f active,ssid dev wifi | grep '^yes' | cut -d: -f2)
    if [ "$CURRENT_SSID" == "$SSID" ]; then
        echo "[WIFI] Sudah terhubung ke $SSID!"
        nmcli con mod "$SSID" connection.autoconnect yes 2>/dev/null
        exit 0
    fi

    echo "[WIFI] Mencoba menghubungkan ke $SSID (percobaan ke-$i)..."
    if nmcli dev wifi connect "$SSID" password "$PASSWORD"; then
        echo "[WIFI] Berhasil terhubung ke $SSID!"
        nmcli con mod "$SSID" connection.autoconnect yes 2>/dev/null
        exit 0
    fi
    sleep 2
done
