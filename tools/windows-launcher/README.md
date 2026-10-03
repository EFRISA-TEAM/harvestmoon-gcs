# MoonHarvest Windows launcher

Launcher Windows 10/11 x64 native, tanpa kebutuhan instalasi .NET untuk launcher.
Build pertama berukuran 101.888 byte (0,102 MB). Batas build: 20.000.000 byte.

Launcher mengunduh aplikasi GCS lengkap dari URL HTTPS di `payload.h`, memeriksa
SHA-256, lalu menyimpannya di `%LOCALAPPDATA%\MoonHarvest\<SHA256>\MoonHarvest.exe`.
Pembukaan berikutnya memeriksa cache dan menjalankannya tanpa unduhan ulang.
File unduhan yang gagal atau tidak cocok tidak dijalankan. Launcher tidak meminta
hak administrator. Tutup jendela untuk membatalkan; permintaan jaringan aktif
dapat memerlukan hingga batas waktu jaringan sebelum berhenti.

Unduhan pertama sekitar 362 MB (345 MiB). Sediakan setidaknya 1,5 GB ruang kosong
untuk paket dan ekstraksi runtime. Koneksi internet tetap diperlukan oleh fitur
online aplikasi. Layanan Python eksternal tidak ikut dibundel; keterbatasan paket
GCS sebelumnya tetap berlaku. Tampilan GCS belum terverifikasi di Windows asli.

## Build

Pasang MinGW-w64 lalu jalankan `bash scripts/build_windows_launcher.sh` dari root.
Compiler dapat ditentukan lewat `LAUNCHER_CC` dan `LAUNCHER_RC`.
Output: `dist/MoonHarvest-Launcher/MoonHarvest.exe`.

## Aktivasi unduhan

Release publik belum dibuat. URL yang tertanam menargetkan repository
`Nobody-Just-Me/harvestmoon-gcs`, tag `moonharvest-portable-20260916`, dan asset
`MoonHarvest-Full.exe`. Upload file dari
`dist/MoonHarvest-GCS-Windows/MoonHarvest.exe` dengan nama asset tersebut setelah
publikasi disetujui. Jangan mengubah payload pada URL ini: launcher mengunci hash.
Untuk versi baru, ubah URL dan SHA-256 di `payload.h`, kemudian build ulang.

Sebelum release tersedia, launcher menampilkan pesan bahwa paket belum tersedia.
Jangan membagikan launcher sebagai siap pakai sebelum URL unduhan aktif.

`MoonHarvest.exe --verify-cache` memeriksa cache tanpa UI atau mengunduh:
exit 0 untuk hash cocok, exit 2 untuk cache hilang/rusak.
