#!/bin/bash
# Fix WiFi nmconnection files - dijalankan sekali saat boot oleh drone-companion.service

CONN_DIR="/etc/NetworkManager/system-connections"
LOG="/tmp/fix_wifi.log"
echo "[fix_wifi] Mulai fix WiFi config - $(date)" > "$LOG"

# Buat mboh.nmconnection yang benar
cat > "$CONN_DIR/mboh.nmconnection" << 'NMEOF'
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
chmod 600 "$CONN_DIR/mboh.nmconnection"

# Buat TP-Link_0CBA.nmconnection (prioritas lebih tinggi)
cat > "$CONN_DIR/TP-Link_0CBA.nmconnection" << 'NMEOF'
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
chmod 600 "$CONN_DIR/TP-Link_0CBA.nmconnection"

# Hapus file duplikat mboh dengan suffix
for f in "$CONN_DIR"/mboh.nmconnection.*; do
    [ -f "$f" ] && rm -f "$f" && echo "[fix_wifi] Hapus: $f" >> "$LOG"
done

# Reload NetworkManager
systemctl reload NetworkManager 2>>"$LOG" || nmcli connection reload 2>>"$LOG" || true
echo "[fix_wifi] Selesai." >> "$LOG"
