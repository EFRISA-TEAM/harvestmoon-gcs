# 🚁 Panduan Setup Drone Companion di Raspberry Pi 5
### Kompatibilitas Sistem MoonHarvest (Telemetri MAVLink + Dual Video Streaming + YOLO)

Panduan ini berisi langkah-langkah untuk menyiapkan **Raspberry Pi 5** sebagai komputer pendamping (Companion Computer) drone, menggantikan Orange Pi 3B dengan fungsionalitas dan konfigurasi yang identik.

---

## 📦 Lokasi File Backup & Paket Instalasi di Laptop

Semua data dari SD Card sebelumnya telah dibackup dan paket instalasi Raspberry Pi 5 sudah disiapkan:

| Berkas / Direktori | Keterangan |
|---|---|
| `/home/fawwazfa/BACKUP_SDCARD_OPI_RPI/` | **Backup Lengkap** SD Card (Home, Model, WiFi, Boot, Systemd) |
| `/home/fawwazfa/Program/Harvestmoon/raspberry_pi5_companion_complete.tar.gz` | **Arsip Siap Pakai (25MB)** untuk Raspberry Pi 5 |
| `/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/scripts/raspberry_pi5/` | Folder script & installer RPi 5 |

---

## 🛠️ Langkah 1: Persiapan SD Card untuk Raspberry Pi 5

1. Buka aplikasi **Raspberry Pi Imager** di laptop (atau unduh dari https://www.raspberrypi.com/software/).
2. **Device**: Pilih `Raspberry Pi 5`.
3. **Operating System**: Pilih **Raspberry Pi OS (64-bit)** (Debian 12 Bookworm).
4. **Storage**: Pilih SD Card Anda.
5. Klik **NEXT**, kemudian pada opsi konfigurasi OS (OS Customisation):
   - Atur Username (misal: `pi` atau `fawwazfa`) & Password.
   - Atur WiFi (bisa langsung masukkan `TP-Link_0CBA` / pass: `03906910` atau `mboh` / pass: `1122334455`).
   - Aktifkan **SSH** (dengan password).
6. Tulis ke SD Card hingga selesai, lalu masukkan SD Card ke Raspberry Pi 5 dan nyalakan.

---

## 🚀 Langkah 2: Salin Paket & Jalankan Installer Otomatis

Setelah Raspberry Pi 5 menyala dan terhubung ke WiFi atau LAN:

### Opsi A: Menggunakan USB Flashdisk
1. Salin file `/home/fawwazfa/Program/Harvestmoon/raspberry_pi5_companion_complete.tar.gz` ke flashdisk.
2. Tancapkan flashdisk ke port USB 3.0 (warna biru) di Raspberry Pi 5.
3. Di Raspberry Pi 5, ekstrak dan jalankan:
   ```bash
   tar -xzvf raspberry_pi5_companion_complete.tar.gz -C ~/rpi5_setup/
   cd ~/rpi5_setup
   sudo bash setup_rpi5_companion.sh
   ```

### Opsi B: Transfer via Jaringan (SCP / SSH)
Dari laptop, jalankan perintah ini (ganti `<IP_RPI5>` dengan IP Raspberry Pi 5):
```bash
scp /home/fawwazfa/Program/Harvestmoon/raspberry_pi5_companion_complete.tar.gz pi@<IP_RPI5>:~/
ssh pi@<IP_RPI5>
```
Di dalam terminal Raspberry Pi 5:
```bash
mkdir -p ~/rpi5_setup
tar -xzvf ~/raspberry_pi5_companion_complete.tar.gz -C ~/rpi5_setup/
cd ~/rpi5_setup
sudo bash setup_rpi5_companion.sh
```

---

## ⚡ Apa Saja yang Dilakukan oleh `setup_rpi5_companion.sh`?

Script installer otomatis akan menangani semua konfigurasi sistem:
1. **Instalasi Paket Sistem**:
   - `python3-opencv`, `gstreamer1.0-*`, `x264`, `network-manager`, `v4l-utils`, `libcamera-tools`.
2. **Konfigurasi WiFi Otomatis**:
   - Memasang profil WiFi `TP-Link_0CBA` (prioritas 20) dan `mboh` (prioritas 10) ke `/etc/NetworkManager/system-connections/`.
3. **Penyusunan Virtual Environment**:
   - Menyiapkan `~/drone_companion/venv` lengkap dengan `pymavlink`, `pyserial`, `opencv`, dan `flask`.
4. **Pemasangan Model YOLO**:
   - Model bobot `best.pt` dari Orange Pi disalin ke `~/drone_companion/yolo_detection/weights/best.pt`.
5. **Aktivasi Port Hardware UART**:
   - Mengaktifkan `enable_uart=1` pada `/boot/firmware/config.txt` agar GPIO pin serial (Pin 8/10) siap terhubung ke Pixhawk.
6. **Autostart Service**:
   - Memasang dan mengaktifkan service systemd `drone-companion.service`. Sistem akan langsung berjalan otomatis setiap kali Raspberry Pi 5 dinyalakan tanpa perlu login!

---

## 🔌 Koneksi Perangkat Keras (Wiring)

### 1. Flight Controller (Pixhawk / Cube / dsb)
- **Opsi USB (Disarankan)**: Tancapkan kabel Micro-USB / USB-C dari TELEM/USB Pixhawk langsung ke port USB Raspberry Pi 5. Otomatis terdeteksi di `/dev/ttyACM0` atau `/dev/ttyUSB0`.
- **Opsi GPIO Serial**:
  - Pixhawk TELEM TX -> Raspberry Pi 5 GPIO 15 (RX / Pin 10)
  - Pixhawk TELEM RX -> Raspberry Pi 5 GPIO 14 (TX / Pin 8)
  - Pixhawk GND -> Raspberry Pi 5 GND (Pin 6)
  - Otomatis terdeteksi di `/dev/serial0` atau `/dev/ttyAMA0`.

### 2. Kamera
- **Kamera USB**: Tancapkan ke port USB 3.0 Raspberry Pi 5. Otomatis terdeteksi via V4L2 (`/dev/video*`).
- **Kamera Ribbon CSI (Raspberry Pi Camera v2 / v3 / HQ)**: Tancapkan ribbon cable ke slot `CAM/DISP 0` atau `CAM/DISP 1`.

---

## 🖥️ Koneksi ke MoonHarvest GCS

Setelah Raspberry Pi 5 menyala:

1. **Koneksi Telemetri (MAVLink)**:
   - Buka MoonHarvest GCS -> Klik **Connect**.
   - Pilih tab **UDP**.
   - Host: `0.0.0.0`
   - Port: `14550`
   - Klik **Connect**. MAVLink data langsung terhubung dua arah.

2. **Koneksi Kamera**:
   - Buka halaman **Camera** di MoonHarvest GCS.
   - Pilih sumber di dropdown:
     - **UDP H.264 :5600** (Streaming H.264 real-time latency rendah)
     - ATAU **Network Stream (MJPEG)**: `http://<IP_RPI5>:8080/video_feed`
   - Klik **Start Stream**.

3. **Web Browser HUD**:
   - Anda juga bisa membuka dashboard HUD langsung di browser laptop:
     `http://<IP_RPI5>:8080/`

---

## 🔍 Perintah Berguna untuk Monitoring

Di Raspberry Pi 5:
```bash
# Pantau log drone companion secara live:
journalctl -u drone-companion -f

# Cek status layanan:
sudo systemctl status drone-companion

# Restart layanan:
sudo systemctl restart drone-companion

# Tes kamera saja:
python3 ~/drone_companion/test_camera.py

# Tes koneksi Pixhawk saja:
python3 ~/drone_companion/test_mavlink.py
```
