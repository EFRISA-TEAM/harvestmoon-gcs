#!/bin/bash
# Jalankan dengan sudo: sudo ./install_service.sh
if [ "$EUID" -ne 0 ]; then
  echo "Harap jalankan dengan sudo: sudo ./install_service.sh"
  exit 1
fi

cp drone-companion.service /etc/systemd/system/
systemctl daemon-reload
systemctl enable drone-companion.service
echo "Service drone-companion berhasil didaftarkan dan diaktifkan saat boot!"
echo "Untuk memulai sekarang: sudo systemctl start drone-companion"
echo "Untuk melihat log     : journalctl -u drone-companion -f"
