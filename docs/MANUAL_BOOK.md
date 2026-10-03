# MoonHarvest — Manual Book
## Panduan Penggunaan Sistem Monitoring Kesehatan Tanaman Padi Berbasis UAV

**Versi:** 2.0 (TEKNOFEST 2026)
**Platform:** .NET 9 / Uno Platform + Python 3 + YOLOv8 + OpenCV
**Tim:** MoonHarvest Team — EFRISA TEKNO

---

## Daftar Isi

1. [Pengenalan Sistem](#1-pengenalan-sistem)
2. [Spesifikasi & Persyaratan](#2-spesifikasi--persyaratan)
3. [Instalasi & Setup](#3-instalasi--setup)
4. [Menjalankan Aplikasi GCS](#4-menjalankan-aplikasi-gcs)
5. [Halaman-Halaman GCS](#5-halaman-halaman-gcs)
6. [Menjalankan Deteksi Video](#6-menjalankan-deteksi-video)
7. [Memahami Output Deteksi](#7-memahami-output-deteksi)
8. [Kelas Deteksi & Maknanya](#8-kelas-deteksi--maknanya)
9. [Field Health Index (FHI)](#9-field-health-index-fhi)
10. [Konfigurasi Parameter](#10-konfigurasi-parameter)
11. [Troubleshooting](#11-troubleshooting)
12. [Keterbatasan Sistem](#12-keterbatasan-sistem)

---

## 1. Pengenalan Sistem

MoonHarvest adalah sistem Ground Control Station (GCS) untuk UAV pertanian yang menggabungkan dua pendekatan deteksi kondisi lahan padi secara visual:

```
UAV (Drone) — merekam video sawah dari ketinggian
       │
       ▼
Python Detection Script (moonharvest_detect_stream.py)
   ├── HSV Segmentasi Warna       ← cepat, tidak butuh GPU
   ├── ONNX Classifier (YOLOv8)   ← akurat, butuh GPU/CPU
   └── Fusion Adaptif             ← gabungkan keduanya
       │
       ▼ JSON stream (stdout)
       │
HarvestmoonGCS (.NET / Uno Platform)
   ├── Dashboard · Live ← status UAV + ringkasan kesehatan
   ├── Camera           ← video live + bounding box deteksi
   ├── Peta & Misi      ← peta waypoint & geofence
   ├── Crop Analysis    ← grafik distribusi kelas
   ├── Edge Mode        ← tampilan ringkas untuk resource terbatas
   ├── AI Settings      ← konfigurasi model deteksi
   └── Reports          ← laporan hasil sesi deteksi
```

### Tujuan Sistem

Membantu operator UAV dan petani untuk:
- Mengidentifikasi area sawah yang bermasalah secara visual
- Mendapatkan Field Health Index (FHI) sebagai indikator kesehatan lahan
- Merencanakan misi terbang dan monitoring real-time
- Menyimpan laporan hasil inspeksi per sesi

---

## 2. Spesifikasi & Persyaratan

### Hardware Minimum

| Komponen | Minimum | Rekomendasi |
|----------|---------|-------------|
| CPU | 4-core 2.0 GHz | 8-core 3.0 GHz |
| RAM | 8 GB | 16 GB |
| GPU | CPU-only (lambat) | NVIDIA dengan CUDA (RTX 3050+) |
| Storage | 10 GB bebas | 20 GB bebas |
| OS | Linux / Windows 10+ | Ubuntu 22.04 / Windows 11 |

### Software yang Dibutuhkan

**Untuk GCS (.NET):**
- .NET 9 SDK
- Uno Platform SDK
- Android SDK (jika target Android)

**Untuk deteksi Python:**
- Python 3.10 atau lebih baru
- Virtual environment `.venv-yolo` dengan:
  - `ultralytics` (YOLOv8)
  - `opencv-python`
  - `numpy`
  - `onnxruntime` (atau `onnxruntime-gpu` untuk CUDA)
- `ffmpeg` (untuk re-encode video output)

### File Model yang Dibutuhkan

Letakkan di `Pigeon_Harvest/HarvestmoonGCS/Assets/models/`:

| File | Fungsi |
|------|--------|
| `moonharvest-health-cls.onnx` | Classifier utama (FP32) |
| `moonharvest-health-cls-int8.onnx` | Classifier INT8 (lebih cepat) |
| `moonharvest-uav-det.onnx` | Detektor UAV (FP32) |
| `moonharvest-uav-det-int8.onnx` | Detektor UAV INT8 |
| `classes-moonharvest-health.txt` | File kelas deteksi |

---

## 3. Instalasi & Setup

### 3.1 Setup Python Environment

```bash
# Masuk ke folder proyek
cd /home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest

# Buat virtual environment untuk YOLO
python3 -m venv .venv-yolo

# Aktifkan
source .venv-yolo/bin/activate

# Install dependencies
pip install ultralytics opencv-python numpy onnxruntime
# Jika punya GPU NVIDIA dengan CUDA:
# pip install onnxruntime-gpu

# Verifikasi
python3 -c "import cv2; import ultralytics; print('OK')"
```

### 3.2 Build GCS (.NET)

```bash
cd /home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest

# Build untuk desktop (Linux/Windows)
dotnet build HarvestmoonGCS/HarvestmoonGCS.csproj \
    -f net9.0-desktop \
    -c Release

# Build untuk Android (APK)
dotnet build HarvestmoonGCS/HarvestmoonGCS.csproj \
    -f net9.0-android \
    -c Release
```

### 3.3 Verifikasi Instalasi

```bash
# Tes script deteksi langsung
source .venv-yolo/bin/activate
python3 HarvestmoonGCS/moonharvest_detect_stream.py --help
```

---

## 4. Menjalankan Aplikasi GCS

### 4.1 Mode Desktop (Linux/Windows)

```bash
cd /home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest

# Jalankan dari hasil build
./HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS

# Atau langsung dengan dotnet run
dotnet run --project HarvestmoonGCS/HarvestmoonGCS.csproj \
    -f net9.0-desktop
```

### 4.2 Mode Demo (Tanpa UAV)

Jika tidak ada UAV, sistem mendukung mode demo menggunakan video yang sudah diproses:

- Video demo tersedia di `HarvestmoonGCS/Assets/demo_videos/`
- Mode demo diaktifkan secara otomatis saat tidak ada koneksi MAVLink
- Atau set environment variable:

```bash
export MOONHARVEST_DEMO=1
```

### 4.3 Urutan Startup yang Benar

1. Pastikan file model `.onnx` tersedia di folder `Assets/models/`
2. Pastikan `.venv-yolo` sudah terinstall
3. Jalankan GCS
4. Buka **AI Settings** → konfigurasi model deteksi (ONNX model path, class file path, threshold)
5. Buka **Camera** → klik tombol "Start Detection"

### 4.4 Navigasi Sidebar

GCS memiliki sidebar navigasi dengan menu berikut:

| Menu | Keterangan |
|------|------------|
| **Dashboard · Live** | Halaman utama — status UAV, telemetri, FHI, ringkasan kelas |
| **Camera** | Tampilan video live + overlay bounding box deteksi |
| **Peta & Misi** | Peta offline, posisi UAV real-time, waypoint misi, geofence |
| **Crop Analysis** | Grafik distribusi kelas kesehatan, statistik FHI historis |
| **Edge Mode** | Tampilan ringkas untuk tablet/resource terbatas |
| **AI Settings** | Konfigurasi model deteksi YOLO, ONNX path, class file |
| **Reports** | Laporan per sesi inspeksi, riwayat deteksi |
| **Yolo Option** | Opsi cepat pengaturan model YOLO (di sidebar bawah) |

---

## 5. Halaman-Halaman GCS

### 5.1 Dashboard · Live

Halaman utama yang menampilkan:
- **Status koneksi UAV** — connected/disconnected + protokol
- **Telemetri real-time** — altitude, heading, kecepatan, GPS
- **Field Health Index** — angka 0–100 + status (Baik/Perhatian/Kritis)
- **Ringkasan kelas** — persentase Healthy/Stress/Drought/Bare Soil
- **Battery warning** — peringatan baterai rendah
- **Yolo Option status** — indikator status model YOLO di sidebar bawah

### 5.2 Camera Page

Halaman tampilan video + hasil deteksi:
- Stream video live dengan overlay bounding box per kelas
- Setiap bounding box berwarna sesuai kelas:
  - Hijau `(50,210,50)` → Lush Green (Sehat)
  - Kuning `(0,200,255)` → Inconsistent Growth
  - Merah/oranye `(0,90,255)` → Drought/Severe Stress
  - Abu-abu `(140,140,140)` → Bare Soil/Gap
- Tombol **Start/Stop Detection**
- Pilihan model (ONNX cls atau YOLO)
- Status inference di panel samping

### 5.3 Peta & Misi

- Peta offline berbasis **Mapsui**
- Tampilkan posisi UAV real-time
- Overlay waypoint misi terbang
- Batas geofence
- Buat dan edit waypoint misi
- Fitur **Undo/Redo** untuk pengeditan waypoint
- Export ke format standar

### 5.4 Crop Analysis

- Grafik distribusi kelas kesehatan (LiveCharts)
- Persentase area per kelas dari sesi deteksi terakhir
- Field Health Index historis
- Zona prioritas inspeksi
- Detail per-frame detection log

### 5.5 Edge Mode

Mode khusus untuk perangkat dengan resource terbatas (Android tablet):
- Tampilan ringkas untuk monitoring di lapangan
- Menampilkan hasil deteksi dari stream Python
- Parsing JSON output dari `moonharvest_detect_stream.py`
- Tampilan performance metrics (inference time, FPS)
- Status model dan ONNX runtime

### 5.6 AI Settings

Konfigurasi sistem deteksi model:
- **Detection Settings** (bagian utama):
  - ONNX Model Path — pilih file model classifier (`.onnx`)
  - Class File Path — pilih file kelas deteksi (`.txt`)
  - Detection threshold — atur confidence threshold
  - Cloud AI integration tersedia di mode Advanced
- **Vision Runtime**:
  - Konfigurasi model ONNX untuk deteksi kesehatan tanaman
  - Pengaturan path model dan kelas
  - Preview konfigurasi deteksi

### 5.7 Reports (Tlog)

- Laporan per sesi inspeksi
- Riwayat deteksi dengan timestamp
- Detail per-frame detection results
- Export laporan (partial)
- Visualisasi data deteksi lengkap

---

## 6. Menjalankan Deteksi Video

### 6.1 Deteksi Video Standalone (tanpa GCS)

```bash
cd /home/fawwazfa/Program/Harvestmoon
source Pigeon_Harvest/.venv-yolo/bin/activate

# Mode HSV + ONNX Fusion (rekomendasi)
python3 run_detection_video.py \
    --source path/ke/video.mp4 \
    --model Pigeon_Harvest/HarvestmoonGCS/Assets/models/moonharvest-health-cls.onnx \
    --det-model Pigeon_Harvest/HarvestmoonGCS/Assets/models/moonharvest-uav-det.onnx \
    --out out/hasil_deteksi.mp4

# HSV saja (tanpa model, lebih cepat)
python3 run_detection_video.py \
    --source path/ke/video.mp4 \
    --out out/hasil_hsv.mp4
```

### 6.2 Stream Langsung ke GCS

Script `moonharvest_detect_stream.py` berjalan sebagai subprocess yang dipanggil oleh `PythonCameraService.cs`:

```bash
# Cara manual (untuk testing)
python3 Pigeon_Harvest/HarvestmoonGCS/moonharvest_detect_stream.py \
    --source path/ke/video.mp4 \
    --model Pigeon_Harvest/HarvestmoonGCS/Assets/models/moonharvest-health-cls.onnx
```

Output JSON ke stdout:
```json
{"type": "frame",     "data": "<base64_jpeg>"}
{"type": "detection", "data": {"count": 12, "summary": "...", "classes": {...}, "fhi": 78.5}}
{"type": "end",       "data": "Video stream ended"}
{"type": "error",     "data": "pesan error"}
{"type": "info",      "data": "pesan informasi"}
```

### 6.3 Parameter Penting

| Parameter | Default | Keterangan |
|-----------|---------|------------|
| `--source` | (wajib) | Path video atau index kamera (0, 1, ...) |
| `--model` | (kosong) | Path model ONNX classifier |
| `--det-model` | (kosong) | Path model ONNX detektor |
| `--fps` | 2.0 | Target FPS pemrosesan |
| `--out` | `out/stream_v7c_final.mp4` | Path output video |
| `--max-fps` | 15.0 | FPS maksimum output |

---

## 7. Memahami Output Deteksi

### 7.1 Overlay Video

Setiap bounding box menampilkan:
```
[Nama Kelas] conf:XX%
```
Contoh: `Lush Green conf:87%`

Panel kanan frame (480px) menampilkan:
- Nama kelas + warna
- Jumlah region per kelas
- Field Health Index (FHI)
- Status lapangan (Baik / Perhatian / Kritis)
- Inference time per frame

### 7.2 Struktur JSON Detection

```json
{
  "count": 12,
  "summary": "Healthy:8 Stress:3 Drought:1",
  "classes": {
    "Healthy": 8,
    "Stress": 3,
    "Drought": 1,
    "Bare Soil": 0
  },
  "fhi": 78.5
}
```

### 7.3 File Output

Setelah menjalankan `run_detection_video.py`:

| File | Lokasi | Isi |
|------|--------|-----|
| `stream_v7c_final.mp4` | `out/` | Video dengan overlay deteksi (H.264) |
| Log CSV | `sync_out/` | Per-frame detection log |
| JSON summary | `fusion_out/` | Ringkasan per sesi |

---

## 8. Kelas Deteksi & Maknanya

Sistem mendeteksi 4 kelas kondisi lahan:

### Lush Green (Sehat)
- **Warna bounding box:** Hijau
- **Makna:** Area sawah hijau lebat, tanaman tumbuh normal
- **Indikasi visual:** Warna hijau pekat, daun rapat, tidak ada bercak kuning/coklat
- **Tingkat keparahan:** 0.00 (tidak ada masalah)
- **Tindakan:** Tidak perlu tindakan khusus

### Inconsistent Growth (Pertumbuhan Tidak Merata)
- **Warna bounding box:** Kuning/orange
- **Makna:** Area dengan pertumbuhan tidak seragam, campuran tanaman sehat dan bermasalah
- **Indikasi visual:** Warna hijau tidak merata, ada bercak kuning atau area lebih pucat
- **Tingkat keparahan:** 0.45 (perlu perhatian)
- **Tindakan:** Inspeksi lapangan, cek nutrisi/irigasi

### Drought / Severe Stress (Kekeringan/Stres Berat)
- **Warna bounding box:** Merah/oranye
- **Makna:** Area mengalami kekeringan atau tekanan berat
- **Indikasi visual:** Warna kuning-coklat dominan, daun mengering, pertumbuhan terhambat
- **Tingkat keparahan:** 0.80 (prioritas tinggi)
- **Tindakan:** Segera periksa sistem irigasi, pertimbangkan penanganan darurat

### Bare Soil / Gap (Tanah Terbuka)
- **Warna bounding box:** Abu-abu
- **Makna:** Area tanah terbuka, tidak ada tanaman atau tanaman sangat jarang
- **Indikasi visual:** Warna coklat/tanah, tidak ada vegetasi
- **Tingkat keparahan:** 0.10 (informasi saja)
- **Catatan:** Kelas ini disembunyikan di tampilan default karena sering false positive

---

## 9. Field Health Index (FHI)

FHI adalah angka 0–100 yang merepresentasikan kondisi keseluruhan lahan dalam satu frame.

### Cara Hitung

```
FHI = 100 - (jumlah_region_stres × 0.45 + jumlah_region_drought × 0.80) / total_region × 100
```

Lebih tepatnya, FHI dihitung dengan EMA (Exponential Moving Average) untuk stabilitas:
- α = 0.30 → FHI baru = 0.30 × FHI_frame + 0.70 × FHI_sebelumnya

### Interpretasi FHI

| Nilai FHI | Status | Warna | Arti |
|-----------|--------|-------|------|
| 75 – 100 | Baik | Hijau | Sebagian besar lahan sehat |
| 50 – 75 | Perhatian | Kuning | Ada area bermasalah, perlu dipantau |
| 0 – 50 | Kritis | Merah | Banyak area bermasalah, perlu tindakan |

### Catatan Penting

FHI adalah **proxy visual** berbasis distribusi area kelas. Nilai ini **bukan pengukuran agronomis langsung** seperti NDVI atau pengambilan sampel daun. Gunakan sebagai panduan awal untuk prioritisasi inspeksi lapangan.

---

## 10. Konfigurasi Parameter

File konfigurasi utama: `FINAL_CONFIG.json`

### Parameter YOLO

```json
"yolo": {
    "weights": "path/ke/model.pt",
    "imgsz": 224,         // ukuran input patch (224x224px)
    "min_conf": 0.40,     // confidence minimum untuk menerima prediksi
    "min_patch_px": 48,   // ukuran minimum patch (lebih kecil diabaikan)
    "max_regions": 80     // maksimum region per frame
}
```

### Parameter HSV

Parameter ini dikalibrasi khusus untuk sawah padi umur 15 hari:

| Parameter | Nilai | Fungsi |
|-----------|-------|--------|
| `clahe_clip` | 2.0 | Batas CLAHE untuk peningkatan kontras |
| `shadow_v_max` | 45 | Threshold brightness untuk filter bayangan |
| `exg_veg_thr` | 0.0213 | Threshold ExG untuk memilah vegetasi |
| `ema_alpha` | 0.4 | Faktor smoothing temporal |
| `morph_kernel` | 5 | Ukuran kernel untuk operasi morfologi |

### Fusion Weights

Bobot menentukan seberapa besar kontribusi masing-masing metode per kelas:

| Kelas | Alpha YOLO | Alpha HSV | Penjelasan |
|-------|-----------|----------|-----------|
| healthy_crop | 0.20 | 0.80 | HSV dominan untuk kelas sehat |
| stressed_crop | 0.55 | 0.45 | Seimbang, YOLO sedikit lebih dominan |
| drought_stress | 0.50 | 0.50 | Bobot sama |
| bare_soil | 0.45 | 0.55 | HSV sedikit lebih dominan |

---

## 11. Troubleshooting

### Problem: GCS tidak bisa menemukan Python

**Gejala:** Error "Python runtime not found"

**Solusi:**
```bash
# Pastikan .venv-yolo ada
ls /home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/.venv-yolo/bin/python3

# Jika tidak ada, buat ulang
cd /home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest
python3 -m venv .venv-yolo
source .venv-yolo/bin/activate
pip install ultralytics opencv-python numpy onnxruntime
```

### Problem: moonharvest_detect_stream.py tidak ditemukan

**Gejala:** Error "moonharvest_detect_stream.py not found"

**Solusi:** Pastikan file ada di path yang benar:
```
Pigeon_Harvest/HarvestmoonGCS/moonharvest_detect_stream.py
```

### Problem: Video output korup / tidak bisa diputar

**Gejala:** File MP4 tidak bisa dibuka

**Penyebab:** Proses Python diinterrupt sebelum selesai menulis, atau stdout di-pipe ke `| head`

**Solusi:**
- Jangan interrupt script saat menulis video
- Jangan gunakan `| head -N` saat menjalankan script
- Jika ffmpeg tidak tersedia, install: `sudo apt install ffmpeg`

### Problem: Deteksi sangat lambat

**Gejala:** Hanya 0.5–1 FPS

**Solusi:**
- Kurangi `max_regions` di config (default 80, coba 30)
- Gunakan model INT8: `moonharvest-health-cls-int8.onnx`
- Kurangi resolusi processing (edit `PROC_W` di script dari 640 ke 320)

### Problem: Model tidak ditemukan

**Gejala:** Error "Model file not found" di AI Settings

**Solusi:**
- Pastikan file `.onnx` ada di `Pigeon_Harvest/HarvestmoonGCS/Assets/models/`
- Pastikan file `classes-moonharvest-health.txt` juga ada di folder yang sama
- Atur ONNX Model Path dan Class File Path di AI Settings dengan benar

### Problem: Edge Mode tidak tampil

**Gejala:** Edge Mode halaman kosong atau error

**Solusi:**
- Pastikan stream Python berjalan dan menghasilkan JSON output
- Cek koneksi antara Python subprocess dan GCS
- Pastikan `PythonCameraService.cs` terkonfigurasi dengan benar

### Problem: Deteksi sangat banyak false positive

**Gejala:** Bounding box muncul di area yang tidak relevan (pematang, jalan, dsb)

**Solusi:**
- Naikkan `min_conf` dari 0.40 ke 0.55 di AI Settings
- Aktifkan `suppress_structures: true` di config HSV
- Naikkan `min_region_area_frac` dari 0.0015 ke 0.003

---

## 12. Keterbatasan Sistem

### Yang Perlu Diketahui Operator

1. **Bukan pengganti pengukuran lapangan** — FHI dan label kelas adalah indikasi visual awal, bukan diagnosa agronomis. Selalu lakukan konfirmasi lapangan untuk area yang terdeteksi bermasalah.

2. **Performa terbaik pada ketinggian 50–80m** — Sistem dikalibrasi untuk footage UAV dari ketinggian tersebut. Foto dari ground level atau ketinggian >100m mungkin menghasilkan deteksi yang kurang akurat.

3. **Dikalibrasi untuk padi umur 15 hari** — Parameter HSV dioptimalkan untuk fase pertumbuhan ini. Pada fase lain (persemaian, panen), threshold mungkin perlu disesuaikan.

4. **~2 FPS dalam mode fusion** — Pada RTX 3050, sistem mampu memproses sekitar 2 frame per detik. Cukup untuk monitoring, tapi bukan video real-time mulus.

5. **Hanya visible spectrum (RGB)** — Tidak menggunakan kamera multispektral atau NDVI. Akurasi lebih rendah dibandingkan analisis multispektral.

6. **bare_soil disembunyikan di UI default** — Untuk melihatnya, ubah konfigurasi di `moonharvest_detect_stream.py` baris `DISPLAY_MAP`.

7. **Edge Mode untuk Android tablet** — Tampilan dioptimalkan untuk perangkat dengan resource terbatas. Performa mungkin lebih lambat di desktop.

---

*Manual Book ini dibuat untuk TEKNOFEST 2026.*
*Untuk referensi teknis lebih detail, lihat `MOONHARVEST_DOCS.md`.*
*Untuk isu yang diketahui, lihat `KNOWN_ISSUES.md`.*
