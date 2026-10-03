# Ringkasan Lengkap Sistem Drone Companion: Orange Pi 3B & Pixhawk

Dokumen ini merupakan panduan master, catatan arsitektur, dan referensi operasional menyeluruh mengenai sistem **Companion Computer Drone** berbasis **Orange Pi 3B (Rockchip RK3566)** dan **Pixhawk Flight Controller**, yang terintegrasi secara mandiri (*fully autonomous*) dengan laptop Ground Control Station (GCS) dan sistem Computer Vision **MoonHarvest (Teknofest)**.

---

## 1. Ringkasan Eksekutif & Status Sistem Aktif

Sistem telah diuji secara *end-to-end* di jaringan Wi-Fi `mboh` dan berjalan secara permanen sebagai layanan systemd (*daemon*):

| Komponen | Status | Detail & Konfigurasi |
| :--- | :--- | :--- |
| **Koneksi Jaringan** |  **Aktif** | SSID: `mboh` (Orange Pi: `10.235.141.127`, Laptop: `10.235.141.37`) |
| **Autostart Booting** |  **Aktif** | Systemd Service: `drone-companion.service` (Status: *Enabled*) |
| **Akses Remote SSH** |  **Aktif** | `ssh orangepi@10.235.141.127` (Passwordless key terpasang dari laptop) |
| **Flight Controller** |  **Aktif** | Pixhawk pada `/dev/ttyACM0` @ 115200 bps (System ID: `1`, Heartbeat OK) |
| **Telemetri MAVLink** |  **Aktif** | Dua arah via **UDP Port 14550** (Mission Planner / QGC / HarvestmoonGCS) |
| **Kamera USB** |  **Aktif** | Terdeteksi di `/dev/video1` (**1280x720 @ 30 FPS**, Hardware MJPG) |
| **Livestream UDP** |  **Aktif** | `udp://10.235.141.37:5600` (RTP H.264 untuk QGroundControl) |
| **Livestream HTTP** |  **Aktif** | `http://10.235.141.127:8080/video_feed` (Untuk MoonHarvest / OpenCV) |
| **Web Browser HUD** |  **Aktif** | Buka di browser: `http://10.235.141.127:8080/` |
| **MoonHarvest Vision**|  **Teruji** | Pipeline HSV + YOLO di laptop memproses video live drone secara real-time |
| **Backup Sistem OPI** |  **Tersimpan** | Di laptop: `~/backup_opi_root/` (~7.6 GB sistem utuh SD Card) |

---

## 2. Arsitektur Komunikasi & Alur Data

Karena pemrosesan Computer Vision (YOLO) dilakukan di **Laptop GCS**, Orange Pi 3B bertindak sebagai **jembatan data (companion computer)** berkinerja tinggi, hemat daya, dan tahan panas:

```
┌────────────────────────────────────────────────────────────────────────┐
│                        WAHANA DRONE (AIR UNIT)                         │
│                                                                        │
│  [Pixhawk Flight Controller]                                           │
│         │ (USB /dev/ttyACM0 @ 115200 bps MAVLink)                      │
│         ▼                                                              │
│  [Orange Pi 3B Companion Computer (10.235.141.127)]                    │
│     ├── Systemd Service (drone-companion.service - Auto Run Saat Boot) │
│     ├── Auto-Detect Kamera USB (/dev/video1 @ 720p 30 FPS MJPG)        │
│     ├── Auto-Detect Pixhawk Serial & MAVLink Auto-Reconnect            │
│     ├── Telemetry Bridge: MAVLink ↔ UDP 14550                          │
│     ├── Dynamic GCS Latching: Mengunci otomatis ke IP Laptop GCS       │
│     ├── Video UDP Streamer: RTP H.264 ──► Port 5600                    │
│     ├── Video HTTP Server: MJPEG /video_feed ──► Port 8080             │
│     └── Camera Trigger Listener ──► Simpan Foto Mapping + Geotag GPS   │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
                         Jaringan Wi-Fi "mboh"
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                       LAPTOP GCS (GROUND UNIT)                         │
│                                                                        │
│  • Mission Planner / QGC   : Telemetri MAVLink UDP 14550               │
│  • QGroundControl Video    : UDP Port 5600 (RTP H.264)                 │
│  • MoonHarvest AI Vision   : http://10.235.141.127:8080/video_feed     │
│  • Web Browser Dashboard   : http://10.235.141.127:8080/               │
└────────────────────────────────────────────────────────────────────────┘
```

### Keunggulan Orange Pi 3B Dibanding Raspberry Pi 5:
1. **Konsumsi Daya Sangat Irit**: Hanya 2.5 – 4 Watt (RPi 5 memakan 8 – 12 Watt). Baterai drone jauh lebih awet.
2. **Suhu Kerja Dingin**: Cukup pendingin pasif aluminium tipis tanpa perlu kipas pendingin berat yang membebani drone.
3. **Hardware Video Encoding (RK3566)**: Kompresi video ditangani langsung tanpa membebani CPU (penggunaan CPU Orange Pi < 15%).

---

## 3. Fitur Otomatisasi Permanen (Zero-Config / Plug-and-Play)

Sistem telah diatur agar **100% mandiri**:

1. **Auto-Start Saat Booting**:
   - Dijalankan oleh systemd melalui [`drone-companion.service`](file:///home/fawwazfa/drone_companion/drone-companion.service).
   - Begitu baterai drone dicolokkan, layanan langsung aktif dalam ~15 detik tanpa perlu remote SSH atau monitor.
   - Konfigurasi `Restart=always` memastikan jika terjadi error, layanan otomatis bangkit kembali dalam 3 detik.

2. **Auto-Connect Wi-Fi & Watchdog**:
   - Profil Wi-Fi `mboh` dikonfigurasi dengan `connection.autoconnect: yes` dan prioritas tertinggi (`100`).
   - Terdapat script watchdog: jika drone sempat keluar jangkauan sinyal lalu masuk kembali, Wi-Fi otomatis tersambung ulang.
   - Profil cadangan `EAGLE` (hotspot HP saat di lapangan) otomatis dipakai jika `mboh` tidak terdeteksi.

3. **Auto-Detect Kamera & Self-Healing**:
   - Fungsi `find_working_camera()` secara mandiri menguji dev node (`/dev/v4l/by-id/`, `/dev/video1`, `/dev/video0`) dan memilih kamera USB yang aktif menghasilkan frame MJPG 720p 30 FPS.
   - **Tahan Guncangan**: Jika kabel kamera kendor atau tercabut saat getaran terbang, sistem mendeteksi frame macet dan otomatis melakukan inisialisasi ulang begitu kamera tercolok kembali tanpa perlu restart board.

4. **Auto-Detect Pixhawk & Serial Auto-Reconnect**:
   - Memindai otomatis port serial USB (`/dev/ttyACM*`, `/dev/ttyUSB*`) serta pin UART GPIO (`/dev/ttyS2`).
   - Jika flight controller di-restart di lapangan, jembatan telemetri otomatis menyambung kembali begitu heartbeat Pixhawk terbaca.

5. **Dynamic GCS Discovery / Latching (Anti-Repot Ganti IP)**:
   - Orange Pi membroadcast telemetri ke target laptop default (`10.235.141.37`) dan subnet broadcast.
   - **Fitur Cerdas**: Setiap kali laptop Anda mengirimkan data/heartbeat ke port 14550 (misal saat membuka QGC atau Mission Planner di jaringan baru/hotspot), Orange Pi **langsung mengunci IP baru laptop Anda** dan mengalihkan seluruh streaming video serta telemetri ke IP tersebut. Anda tidak perlu mengubah script sama sekali.

6. **Geotagging Foto Pemetaan Otomatis**:
   - Setiap kali Pixhawk mengirim sinyal MAVLink `CAMERA_TRIGGER` atau `DO_DIGICAM_CONTROL`, sistem langsung mengambil frame resolusi penuh dan menyimpannya di folder `images/`.
   - Disertai file metadata JSON berisi koordinat GPS (Latitude, Longitude, Altitude) dan persentase baterai untuk keperluan orthophoto / photogrammetry.

---

## 4. Cara Penggunaan di Laptop GCS

### A. Membuka Web Dashboard HUD di Browser Laptop
Buka browser (Chrome / Firefox / Edge) di laptop:
```
http://10.235.141.127:8080
```
> [!IMPORTANT]
> **Penting**: Jika sebelumnya Anda membuka web ini, lakukan **Hard Refresh** (`Ctrl + F5` atau `Shift + Reload`) di browser Anda agar browser memuat antarmuka Double-Buffered Player terbaru dan tidak menggunakan cache lama.

#### Fitur Web HUD Real-Time:
1. **Video Streaming Real-Time (Zero-Lag Engine)**:
   - **Mode Real-time (<50ms)**: Menggunakan teknologi *Double-Buffered Image Pump* berbasis elemen native `<img>` dengan swap memori instan. **Tidak ada jeda buffer 2 detik** dan **tidak ada masalah layar gelap** (tanpa manipulasi canvas).
   - **Indikator Latensi Nyata**: Menampilkan waktu bolak-balik jaringan secara presisi dalam milidetik (misal `⚡ 35 ms (Realtime)`).
   - **Tombol Pengganti Mode**: Tersedia tombol `[⚡ Realtime (<50ms)]` dan `[🎥 MJPEG]` untuk berpindah antar mode langsung dari halaman web.
2. **Sikap Terbang Drone (Attitude 10 Hz)**:
   - Nilai presisi **ROLL**, **PITCH**, dan **YAW** dalam derajat (°).
   - **Artificial Horizon Gauge Interaktif**: Menampilkan garis horizon langit dan daratan yang berputar dan bergerak naik-turun sesuai orientasi drone secara instan.
   - **Heading Kompas**, **Ketinggian (Altitude)**, dan **Kecepatan Gerak (Groundspeed)**.
3. **Baterai & GPS Pixhawk**:
   - Status koneksi Pixhawk (`TERHUBUNG (ID 1)`).
   - Tegangan baterai dan persentase baterai dengan indikator bar dinamis.
   - Status sinyal GPS (`Fix 3 (3D Fix)` dan jumlah satelit).
   - Koordinat Latitude dan Longitude.
4. **Kontrol Kamera Pemetaan**:
   - Tombol **"📸 Ambil Foto Mapping"** untuk mengambil foto resolusi penuh geotagged langsung ke penyimpanan Orange Pi.

---

### B. Analisis Teknis & Solusi Delay 2 Detik & Layar Gelap

| Gejala Sebelumnya | Akar Masalah (Root Cause) | Solusi Permanen yang Diterapkan |
| :--- | :--- | :--- |
| **Delay 2 Detik pada Video** | 1. Arsitektur push MJPEG standar memenuhi antrian TCP kernel Linux (*bufferbloat*) lewat Wi-Fi.<br>2. GStreamer software encoder H.264 memakan 100% satu core CPU, membuat pembacaan frame tersendat (hanya ~8 FPS). | 1. Menerapkan *Double-Buffered Request Pump*: Browser meminta frame terbaru langsung (`/snapshot?t=now`), sehingga antrian buffer adalah 0 ms.<br>2. Socket disetel ke `TCP_NODELAY` dan buffer kecil.<br>3. Opsi streaming UDP diatur hemat CPU sehingga idle CPU naik menjadi **>80%**. |
| **Layar Menjadi Gelap (Dark Screen)** | Upaya sebelumnya menggunakan HTML5 `<canvas>` yang mereset lebar canvas setiap frame sehingga terhapus menjadi transparan/hitam (`rgba(0,0,0,0)`). | Dihapus total penggunaan canvas. Diganti dengan elemen gambar hardware-accelerated `<img>` dengan swap buffer memori instan (`next.onload`). Gambar selalu terang, tajam, dan normal. |

---

### C. Menghubungkan ke Mission Planner atau QGroundControl
1. **Telemetri MAVLink**:
   - Buka Mission Planner atau QGroundControl.
   - Pilih jenis koneksi **UDP**, port **`14550`**, lalu klik **Connect**.
   - Telemetri dan instrumen kompas/artifisial horizon langsung bergerak real-time.
2. **Video Livestream di QGroundControl**:
   - Buka menu **Application Settings** -> **Video**.
   - **Video Source**: Pilih **MJPEG Video Stream** dengan URL `http://10.235.141.127:8080/video_feed`.
   - *(Alternatif H.264)*: Set `ENABLE_UDP_STREAM="true"` di `start_drone.sh` jika ingin menggunakan port UDP 5600.

---

### D. Menghubungkan ke MoonHarvest AI Vision (Harvestmoon GCS Desktop)
Aplikasi desktop **Harvestmoon GCS** (.NET 9 / Avalonia) di laptop telah dikonfigurasi dan terintegrasi penuh:
1. **Preset Kamera Otomatis**:
   - Di menu dropdown Kamera, telah ditambahkan preset:
     `🚁 Drone Camera (Orange Pi 10.235.141.127)` (`http://10.235.141.127:8080/video_feed`).
   - Aplikasi otomatis mendeteksi dan menghubungkan live stream tanpa konfigurasi manual.
2. **MAVLink Bi-directional UDP**:
   - Transport UDP MAVLink di Harvestmoon GCS (`14550`) didukung dengan routing port dinamis di Orange Pi. Status telemetri: **ONLINE / CONNECTED**.
3. **Pipeline AI Vision Terintegrasi**:
   - Pipeline Python `moonharvest_detect_stream.py` terpasang menggunakan environment `.venv-yolo`.
   - Menjalankan model klasifikasi kesehatan tanaman (`moonharvest-health-cls-v5.onnx`) dan deteksi UAV (`moonharvest-uav-det.onnx`) secara real-time di atas video live dari Orange Pi.
4. **Cara Menjalankan Harvestmoon GCS**:
   ```bash
   cd ~/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS
   dotnet run -c Release
   ```

---

## 5. Lokasi Berkas & Perintah Pemeliharaan

### A. Di Orange Pi 3B (`orangepi@10.235.141.127`)
- **Folder Utama**: `/home/orangepi/drone_companion/`
  - `drone_telemetry_stream.py`: Skrip inti python (Telemetri + Dual Video Stream + Camera Trigger).
  - `start_drone.sh`: Launcher mandiri dan watchdog Wi-Fi.
  - `drone-companion.service`: File konfigurasi systemd di `/etc/systemd/system/`.
  - `images/`: Folder penyimpanan foto mapping dan file metadata geotag JSON.
- **Python Virtual Environment**: `/home/orangepi/orange_pi3b_yolov8n_webcam_test/.venv` (lengkap dengan `pymavlink`, `pyserial`, `opencv-python`).

### B. Di Laptop GCS (`/home/fawwazfa/`)
- **`~/RINGKASAN_DRONE_ORANGEPI.md`**: Dokumen master ringkasan ini.
- **`~/LAPORAN_VERIFIKASI_COMPANION.md`**: Laporan pengujian subsistem live.
- **`~/backup_opi_root/`**: Backup utuh sistem operasi SD Card Orange Pi 3B (~7.6 GB).
- **`~/Program/Harvestmoon/`**: Proyek GCS MoonHarvest Teknofest.

### C. Perintah Cek Layanan via Terminal Laptop
Kunci SSH sudah terpasang sehingga tidak memerlukan password:
```bash
# Cek status layanan drone companion
ssh orangepi@10.235.141.127 "sudo systemctl status drone-companion"

# Melihat log streaming telemetri & video secara langsung
ssh orangepi@10.235.141.127 "journalctl -u drone-companion -f"

# Memulai ulang layanan jika diperlukan
ssh orangepi@10.235.141.127 "sudo systemctl restart drone-companion"
```
