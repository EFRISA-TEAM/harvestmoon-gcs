# 🌾 BUKU PANDUAN PENGGUNA & OPERASIONAL MOONHARVEST GCS
**Unified Smart Agriculture UAV Ground Control Station**  
*Siap Operasi Lapangan (Real Flight) & Presentasi Riset PDP SINTA 2 / Industri*

---

## 1. PENDAHULUAN & FITUR UTAMA

MoonHarvest Ground Control Station (GCS) adalah platform kendali UAV modern kelas profesional yang dikembangkan untuk pemantauan kesehatan tanaman padi, deteksi defisit air, dan otomasi irigasi AWD (*Alternate Wetting and Drying*) secara *real-time*.

### Keunggulan Sistem:
1. **Dual Mode Operasional**:
   - **Mode Real Flight**: Terhubung langsung ke autopilot (Pixhawk / Cube / ArduPilot) via MAVLink (USB Serial / UDP 14550), kamera live (USB/RTSP/RunCam/SIYI HM30), dan receiver LoRa sensor tanah IoT.
   - **Mode Demo / Simulasi**: 1-klik demonstrasi mandiri dengan video resolusi tinggi multi-sektor, telemetri tersinkronisasi, dan pembacaan 4 sektor sensor tanah ground truth.
2. **AI Detection & Clean HUD**:
   - Model AI YOLOv8n / YOLO11n dengan klasifikasi 5 kelas kesehatan tanaman (*Lush Green, Inconsistent, Drought, Severe Stress, Bare Soil*).
   - Bounding box dan badge label clean pill 12px tanpa lag, tanpa duplikasi, dan tanpa menghalangi visual penting.
   - Artificial Horizon HUD opsional yang dapat diaktifkan/dinonaktifkan sesuai kebutuhan.
3. **8 Halaman Terpadu**:
   Dashboard, Camera, Peta Lahan, Rencana Misi, Crop Analysis, Edge Mode, AI Settings, dan Reports.

---

## 2. CARA MENJALANKAN (1-CLICK LAUNCH)

### Pada Linux (Ubuntu 22.04 / 24.04):
Cukup buka terminal dan jalankan:
```bash
cd /home/fawwazfa/Program/Harvestmoon
./JALANKAN_MOONHARVEST.sh
```
Pilih opsi:
- `[1]` untuk **Mode Demo / Presentasi Riset**
- `[2]` untuk **Mode Real Flight (UAV Lapangan)**
- `[3]` untuk **Standalone Python YOLO Detector**
- `[4]` untuk **Recompile / Rebuild Binary**

Atau untuk langsung membuka mode demo 1-klik:
```bash
./JALANKAN_DEMO.sh
```

### Pada Windows (10 / 11):
Klik dua kali file:
```cmd
JALANKAN_MOONHARVEST.bat
```
atau
```cmd
JALANKAN_DEMO.bat
```

---

## 3. PANDUAN MODE DEMO / SIMULASI RISET (PDP SINTA 2)

Mode ini dirancang khusus untuk presentasi kepada investor, reviewer riset, petani, dan dinas pertanian tanpa memerlukan drone fisik.

1. Jalankan `./JALANKAN_MOONHARVEST.sh` lalu pilih `1`.
2. GCS akan otomatis memuat skenario terpilih (default: *Skenario 1 - Riset PDP 4-Sektor 1080p*).
3. Di halaman **Dashboard**, klik tombol **"Start Demo"**:
   - Video resolusi tinggi akan diputar dengan overlay deteksi AI yang bersih dan tajam.
   - UAV bergerak mengikuti rute survey transect pada peta lahan (*Peta Sukamerta Karawang / Subang*).
   - Telemetri sintetis (Ketinggian, Kecepatan, Heading, Baterai, GPS RTK 18 satelit) bergerak dinamis.
   - 4-node LoRa Ground Truth Sensor tanah mengirimkan data kelembapan dan tinggi muka air AWD secara simultan.
4. Anda dapat menguji fitur:
   - **Start/Pause Mission** pada TopBar untuk menjeda misi simulasi.
   - **RTL** untuk mensimulasikan kepulangan drone ke titik awal.
   - Buka menu **Reports** lalu klik **"PDF"** atau **"CSV"** untuk mengunduh laporan agronomi komprehensif.

---

## 4. PANDUAN MODE REAL FLIGHT (OPERASI LAPANGAN)

Mode ini digunakan saat menerbangkan drone di sawah/kebun secara langsung.

### 4.1. Persiapan Perangkat Keras
1. **Autopilot**: Pixhawk 4 / Cube Orange / Matek running ArduPilot (ArduPlane / ArduCopter) atau PX4.
2. **Telemetri Radio**: Holybro SiK Radio 433/915 MHz, RFD900, atau modul Wi-Fi ESP32/HM30.
3. **Kamera**: Kamera payload USB, kamera RunCam Wi-Fi, atau kamera IP RTSP (misal SIYI HM30 / Raspberry Pi Camera).
4. **Sensor Tanah**: Receiver LoRa USB terpasang di laptop ground station.

### 4.2. Langkah Koneksi Telemetri MAVLink
1. Colokkan kabel USB Telemetri Radio ke komputer ground station.
2. Buka aplikasi MoonHarvest GCS via `./JALANKAN_MOONHARVEST.sh` lalu pilih opsi `2`.
3. Di sudut kanan atas TopBar, klik tombol **"DISCONNECTED"**. Jendela **MAVLink Connection** akan terbuka.
4. Pilih metode sambungan:
   - **SERIAL**: Pilih port COM (Windows) atau `/dev/ttyUSB0` / `/dev/ttyACM0` (Linux), set baudrate ke `57600` (default SiK Radio) atau `115200`.
   - **UDP**: Port default `14550` (jika menggunakan telemetri Wi-Fi, Companion Pi, atau SITL Simulator).
   - **SIYI HM30 / RunCam**: Masukkan IP kamera/transceiver untuk menerima data MAVLink sekaligus video HD.
   - **LoRa Sensor**: Pilih tab "LORA SENSOR", tentukan port receiver USB untuk sensor tanah IoT.
5. Klik **"CONNECT"**. Jika tersambung, indikator TopBar berubah hijau menjadi **"CONNECTED"** dan menampilkan status baterai, satelit GPS, serta mode terbang aktual.

### 4.3. Menghubungkan Kamera Video Feed
1. Masuk ke menu **Camera**.
2. Pilih tab:
   - **Webcam**: Pilih nomor kamera (0 untuk kamera internal/USB utama, 1 untuk USB capture card).
   - **RTSP**: Masukkan URL video stream (contoh: `rtsp://192.168.144.25:8554/main.264`).
3. Klik tombol **"Start Stream"**.
4. Aktifkan sakelar **"AI Overlay"** jika ingin menjalankan inferensi YOLO langsung pada video stream lapangan.

### 4.4. Perencanaan Misi & Eksekusi Terbang
1. Masuk ke menu **Rencana Misi (Mission Planner)**.
2. Buat waypoint dengan klik pada peta lahan atau gunakan fitur auto-grid survey lawnmower.
3. Klik tombol **"Upload Mission"** untuk mengirim rute waypoint ke flight controller UAV via MAVLink.
4. Klik **"Send Geofence"** untuk mengunci batas aman penerbangan agar drone tidak keluar perimeter sawah.
5. Pada menu **Dashboard** atau **TopBar**, klik **"Start Mission"** untuk memulai navigasi otomatis.
6. Apabila terjadi keadaan darurat, tekan tombol merah **"RTL"** di TopBar untuk memerintahkan UAV segera kembali dan mendarat otomatis di *Home Point*.

---

## 5. PANDUAN LENGKAP 8 MENU GCS

| No | Menu | Fungsi & Fitur Utama |
|---|---|---|
| **1** | **Dashboard** | Tampilan utama operasi: Live Video Stream, clean AI badges, telemetri card dinamis, kompas heading, status MAVLink, tombol cepat misi, checklist kesiapan pre-flight, dan timeline insiden. |
| **2** | **Camera** | Monitoring kamera penuh: Multi-source input (Webcam, RTSP, Video File), Picture-in-Picture (PiP) mini-map, perekaman video misi, snapshot foto beresolusi tinggi, dan sakelar inferensi YOLO. |
| **3** | **Peta Lahan** | Visualisasi spasial sawah: Peta offline MBTiles / OSM, pelacakan posisi UAV *real-time*, jalur lintasan terbang (*flight trail*), lingkaran geofence, zona sektor sawah (A, B, C, D), dan pin penanda sensor tanah LoRa. |
| **4** | **Rencana Misi** | Pembuat misi penerbangan: Tambah/edit/hapus waypoint, pengaturan altitude & groundspeed, generator pola survey zig-zag, tombol *Upload Mission* ke drone, tombol *Download Mission* dari drone, dan *Send Geofence*. |
| **5** | **Crop Analysis** | Diagnostik vegetasi: Indeks kesehatan vegetatif ExG & HSV, persentase tutupan kanopi (*canopy coverage*), grafik distribusi 5 kelas kesehatan, dan rekomendasi aksi AWD. |
| **6** | **Edge Mode** | Manajemen komputasi cerdas pendamping (Raspberry Pi 5 / NVIDIA Jetson): Metrik latensi telemetri, utilisasi CPU/GPU edge, temperatur SoC, FPS inferensi lokal, dan sinkronisasi data cloud. |
| **7** | **AI Settings** | Konfigurasi model deteksi: Pemilihan checkpoint model YOLOv8 / YOLO11, slider *Confidence Threshold*, *NMS IoU Threshold*, pemilihan warna bouding box, dan aktivasi akselerasi GPU. |
| **8** | **Reports** | Pelaporan & Ekspor: Riwayat lengkap sesi survey, kalkulasi kebutuhan air AWD, catatan agronomi operator, dan ekspor 1-klik ke format **PDF**, **CSV**, dan **JSON**. |

---

## 6. PEMECAHAN MASALAH (TROUBLESHOOTING)

### Masalah 1: "Permission Denied" saat membuka port serial di Linux (`/dev/ttyUSB0`)
**Penyebab**: Pengguna Linux belum masuk ke grup `dialout`.  
**Solusi**: Jalankan perintah berikut di terminal, lalu logout dan login kembali:
```bash
sudo usermod -a -G dialout $USER
```

### Masalah 2: Video stream tidak tampil di Dashboard
**Penyebab**: Belum menekan tombol "Start Demo" (pada mode demo) atau URL RTSP kamera belum tersambung.  
**Solusi**:
- Pada mode demo: Klik tombol **"Start Demo"** di panel kanan Dashboard.
- Pada mode real flight: Buka menu **Camera**, pastikan URL RTSP atau device index Webcam sesuai dan klik "Start Stream".

### Masalah 3: MAVLink Connection Timeout
**Penyebab**: Autopilot belum menyala, kabel longgar, atau baudrate salah.  
**Solusi**:
- Cek baudrate telemetry radio: biasanya `57600` untuk radio 3DR/SiK atau `115200` untuk koneksi USB langsung ke Pixhawk (`SERIAL0`).
- Pastikan parameter ArduPilot `SERIALx_PROTOCOL = 2` (MAVLink2).

---

## 7. SPESIFIKASI TEKNIS SISTEM

- **Kerangka Kerja GCS**: C# 13, .NET 9.0 Desktop (MAUI / WinUI / SkiaSharp)
- **Komputer Visi**: Python 3.10+, PyTorch, Ultralytics YOLOv8 / YOLO11, OpenCV 4.x
- **Protokol Telemetri UAV**: MAVLink v2.0 (Heartbeat, Global Position Int, Attitude, VFR HUD, Sys Status)
- **Protokol IoT Tanah**: LoRa P2P 915 MHz / 433 MHz, UDP Bridge JSON Telemetry
- **Format Peta Offline**: SQLite MBTiles (Raster Satellite Tiles Zoom 14–19)
- **Format Ekspor Laporan**: Dokumen PDF Berlogo EFRISA/PENS, Lembar Kerja CSV, Data GeoJSON

---
*© 2026 Tim EFRISA — Politeknik Elektronika Negeri Surabaya (PENS)*  
*MoonHarvest: Smart Agriculture Aerial Monitoring & Irrigation Control System*
