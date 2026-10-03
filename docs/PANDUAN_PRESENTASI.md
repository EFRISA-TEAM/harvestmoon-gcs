# MoonHarvest — Panduan Pembelajaran & Presentasi
## Untuk Persiapan TEKNOFEST 2026

**Tujuan dokumen ini:** Membantu kamu memahami sistem dari akar ke daun, sehingga bisa presentasi dengan percaya diri dan menjawab pertanyaan juri dengan tepat.

---

## Daftar Isi

1. [Konteks Masalah — Mengapa MoonHarvest Dibuat](#1-konteks-masalah)
2. [Solusi yang Ditawarkan](#2-solusi-yang-ditawarkan)
3. [Arsitektur Sistem — Cara Kerjanya](#3-arsitektur-sistem)
4. [Pipeline Deteksi — Penjelasan Mendalam](#4-pipeline-deteksi)
5. [Model AI — Apa, Mengapa, Bagaimana](#5-model-ai)
6. [Aplikasi GCS — Komponen & Fungsinya](#6-aplikasi-gcs)
7. [Teknologi yang Digunakan](#7-teknologi-yang-digunakan)
8. [Hasil & Metrik Performa](#8-hasil--metrik-performa)
9. [Panduan Presentasi](#9-panduan-presentasi)
10. [Daftar Pertanyaan Juri & Jawaban](#10-daftar-pertanyaan-juri--jawaban)
11. [Hal yang Boleh & Tidak Boleh Diklaim](#11-hal-yang-boleh--tidak-boleh-diklaim)
12. [Kosakata Teknis Penting](#12-kosakata-teknis-penting)

---

## 1. Konteks Masalah

### Masalah yang Diselesaikan

Indonesia adalah salah satu produsen padi terbesar dunia. Namun petani menghadapi tantangan besar:

- **Lahan sawah luas** — sulit diinspeksi manual satu per satu
- **Deteksi masalah terlambat** — penyakit, kekeringan, atau pertumbuhan tidak merata sering baru diketahui saat sudah parah
- **Biaya tenaga ahli mahal** — tidak semua petani punya akses ke agronomis
- **Waktu inspeksi manual panjang** — inspeksi per hektar bisa memakan berjam-jam

### Pendekatan Tradisional & Kelemahannya

| Metode Lama | Kelemahan |
|-------------|-----------|
| Inspeksi manual (jalan di sawah) | Lambat, tidak skalabel, subjektif |
| Foto udara dari pesawat sewaan | Mahal, tidak real-time |
| Sensor NDVI multispektral | Kamera khusus sangat mahal |
| Sampling daun di lab | Hasil lambat (hari/minggu) |

### Peluang yang Dimanfaatkan

- Harga drone/UAV komersial sudah terjangkau (<5 juta rupiah)
- Kamera RGB standar sudah memadai untuk analisis visual
- YOLOv8 & model ONNX bisa berjalan di laptop standar
- Computer vision modern bisa mengolah video real-time

---

## 2. Solusi yang Ditawarkan

MoonHarvest adalah **sistem monitoring kesehatan padi berbasis UAV** yang:

1. Terbang di atas sawah dengan drone standar
2. Merekam video real-time dengan kamera RGB biasa
3. Menganalisis video secara otomatis menggunakan computer vision
4. Menampilkan hasil ke operator melalui aplikasi GCS
5. Menghasilkan laporan kondisi lahan per sesi inspeksi

### Keunggulan Dibanding Solusi Lain

| Aspek | MoonHarvest | Kompetitor |
|-------|-------------|-----------|
| Kamera | RGB standar | Multispektral (mahal) |
| Processing | Real-time onboard laptop | Cloud/post-processing |
| Metode | Fusion HSV + YOLO | Single model saja |
| Platform | Desktop + Android | Desktop saja |
| Komunikasi UAV | MAVLink (standar) | Proprietary |
| Open source | MIT License | Closed source |

---

## 3. Arsitektur Sistem

### Gambaran Besar (untuk dijelaskan ke juri)

```
┌─────────────────────────────────────────────────────────┐
│                    LAPANGAN (SAWAH)                      │
│                                                          │
│   [UAV/Drone] ──── video feed ────▶ [Laptop Operator]   │
│       │                                    │             │
│    terbang                          [GCS Application]    │
│    di 50-80m                              │             │
│                                    [Python Detection]    │
└─────────────────────────────────────────────────────────┘
```

### Detail Arsitektur Software

```
moonharvest_detect_stream.py          HarvestmoonGCS (.NET/Uno)
─────────────────────────────         ─────────────────────────
1. Terima frame dari video/kamera     PythonCameraService.cs
2. White Balance + CLAHE              └─ spawn subprocess Python
3. Hitung ExG vegetation index        └─ baca JSON dari stdout
4. Segmentasi HSV → 4 kelas           └─ kirim ke UI via event
5. ONNX Classifier (224x224 patch)    
6. Fusion HSV + YOLO adaptif          Views/
7. NMS (buang duplikat)               ├─ DashboardPage (status)
8. Hitung FHI                         ├─ CameraPage (video)
9. Output JSON → stdout               ├─ MapPage (peta)
                                      ├─ MissionPlannerPage
                                      ├─ StatsPage (grafik)
                                      └─ ReportsPage (laporan)
```

### Alur Data Lengkap

```
[Frame video]
     │
     ▼
[Preprocessing]
  White balance (Gray World Assumption)
  CLAHE (Contrast Limited Adaptive Histogram Equalization)
     │
     ▼
[HSV Segmentasi]
  Konversi BGR → HSV
  Masking per kelas dengan threshold warna
  Filter vegetasi dengan ExG index
  Remove bayangan (V < 45)
  Morphological operations (erode/dilate)
  Connected components → list region
     │
     ▼
[ONNX Classifier] (jika model tersedia)
  Crop patch 224×224px per region
  Inference model (2.5ms/image di RTX 3050)
  Override label HSV jika confidence ≥ 0.70
     │
     ▼
[YOLO Detector] (jika det-model tersedia)
  Deteksi objek additional dari frame penuh
  Merge dengan region HSV
     │
     ▼
[Fusion & NMS]
  Weighted fusion score per region
  NMS: buang overlap (IoU > 0.20 per kelas, 0.30 cross)
  Filter region kecil (< 600px)
     │
     ▼
[FHI + EMA Smoothing]
  Hitung Field Health Index
  Smooth dengan EMA (α=0.30)
     │
     ▼
[Output JSON ke stdout]
  {"type":"frame", "data":"<base64 JPEG>"}
  {"type":"detection", "data":{count, summary, classes, fhi}}
```

---

## 4. Pipeline Deteksi — Penjelasan Mendalam

### 4.1 Preprocessing — Mengapa Perlu?

Video dari drone sering mengalami:
- **Variasi pencahayaan** — pagi vs siang vs sore
- **Bayangan** — awan, pohon, bangunan
- **Kontras rendah** — saat berawan

Solusi yang dipakai:

**White Balance (Gray World Assumption)**
- Asumsi: rata-rata warna gambar seharusnya netral (abu-abu)
- Koreksi setiap channel R, G, B proporsional
- Hasil: warna lebih konsisten antar kondisi cahaya

**CLAHE (Contrast Limited Adaptive Histogram Equalization)**
- Meningkatkan kontras secara lokal, bukan global
- Parameter: clip=2.0, grid=8×8
- Hasil: detail tekstur lebih jelas tanpa overexpose area terang

### 4.2 HSV Segmentasi — Logika Utama

HSV (Hue-Saturation-Value) dipilih karena:
- Lebih stabil terhadap perubahan cahaya dibanding RGB
- Hue merepresentasikan "warna sebenarnya" tanaman

Threshold per kelas (dikalibrasi dari video sawah 15 hari):

| Kelas | H (Hue) | S (Saturation) min | V (Value) |
|-------|---------|-------------------|-----------|
| Healthy | 25–100 | 18 | 60–255 |
| Stressed | 10–95 | 10 | 50–255 |
| Drought | 8–40 | 30 | 80–235 |
| Bare Soil | (s_max < 12) | - | 90–240 |

**ExG (Excess Green) Index:**
```
ExG = 2×G - R - B  (dinormalisasi)
```
- Filter vegetasi: ExG > 0.0213
- Area non-vegetasi dibuang sebelum HSV segmentasi

**Shadow Removal:**
- Pixel dengan Value < 45 dianggap bayangan → dikeluarkan

### 4.3 ONNX Classifier — Neural Network

Model: YOLOv8n-cls (YOLOv8 nano classification variant)
- Input: 224×224 pixel patch
- Output: probabilitas 4 kelas
- Ukuran model: ~2.9MB
- Kecepatan: 2.5ms/image (GPU), ~20ms (CPU)

**Cara Kerjanya:**
1. Setiap region dari HSV di-crop menjadi patch 224×224
2. Patch di-resize jika perlu
3. Di-infer ke model → dapat confidence per kelas
4. Jika confidence ≥ 0.70: override label dari HSV
5. Jika confidence < 0.70: pertahankan label HSV

**ONNX_COMPAT** — Aturan Override:
- Model YOLO hanya boleh override HSV dengan label yang compatible:
  - `lush` → hanya bisa override ke "Lush Green"
  - `stress` → bisa ke "Lush Green" atau "Inconsistent Growth"
  - `drought` → bisa ke "Drought" atau "Inconsistent Growth"
  - `soil` → hanya ke "Bare Soil"

### 4.4 Fusion Adaptif

Fusion menggabungkan score dari HSV dan YOLO per region:

```
fusion_score = alpha_yolo × score_yolo + alpha_hsv × score_hsv
```

Bobot bervariasi per kelas karena:
- Untuk `healthy_crop`: HSV sudah sangat reliable (hijau = sehat) → HSV 80%
- Untuk `stressed_crop`: YOLO lebih baik menangkap nuansa → YOLO 55%
- Untuk `drought_stress`: keduanya seimbang → 50/50

### 4.5 NMS (Non-Maximum Suppression)

Masalah: banyak region yang overlap mendeteksi hal yang sama.

NMS versi MoonHarvest (v7c):
- **Per-class NMS**: threshold 0.20 — buang region kelas sama yang overlap >20%
- **Cross-class NMS**: threshold 0.30 — buang region kelas berbeda yang overlap >30%
- **Min area filter**: region < 600px dibuang (terlalu kecil, kemungkinan noise)

---

## 5. Model AI

### Model yang Digunakan

| Model | Tipe | Format | Ukuran | Fungsi |
|-------|------|--------|--------|--------|
| moonharvest-health-cls.onnx | Classifier | ONNX FP32 | ~2.9MB | Klasifikasi 4 kelas kesehatan |
| moonharvest-health-cls-int8.onnx | Classifier | ONNX INT8 | ~1.5MB | Sama, lebih cepat di CPU |
| moonharvest-uav-det.onnx | Detector | ONNX FP32 | ~6MB | Deteksi objek tambahan |
| moonharvest-uav-det-int8.onnx | Detector | ONNX INT8 | ~3MB | Sama, lebih cepat |

### Performa Model

- **Validation accuracy (lab):** 98.8% pada dataset validasi
- **Aerial accuracy (real UAV video):** 82.2% dengan fusion HSV + YOLO
- **Inference speed:** 2.5ms/image (RTX 3050), ~20ms (CPU only)
- **Jumlah kelas:** 4 (bare_soil, drought_stress, healthy_crop, stressed_crop)

### Training Dataset

Model dilatih menggunakan dataset yang mengandung:
- Frame dari video UAV sawah (330+ frame berlabel manual)
- Dataset public crop health (Kaggle, Roboflow)
- Augmentasi: flip horizontal/vertical, brightness adjustment, crop

**Penting:** Model v5 (yang dipakai sekarang) dilatih ulang dari v4 karena v4 mengalami domain mismatch — terlalu banyak data ground-level, sehingga buruk di footage UAV.

### Kenapa ONNX dan Bukan .pt Langsung?

- **ONNX** (Open Neural Network Exchange) adalah format universal
- Bisa dijalankan di berbagai platform: CPU, GPU NVIDIA, GPU ARM
- Runtime lebih efisien dari PyTorch di inference-only
- Bisa dipakai di C# (via Microsoft.ML.OnnxRuntime) langsung
- Tidak butuh instalasi PyTorch yang berat

---

## 6. Aplikasi GCS

### Teknologi: Uno Platform

GCS dibangun dengan **Uno Platform** di atas .NET 9 MAUI:
- Satu codebase → compile ke Desktop (Linux/Windows) DAN Android
- Target perangkat Android: Realme Pad Mini 8.7 (Android 14, ARM64)
- UI menggunakan XAML (sama seperti WinUI/UWP)
- Rendering via Skia (cepat, konsisten di semua platform)

### Komponen Utama

**PythonCameraService.cs** — Jembatan C# ↔ Python
- Spawn subprocess Python (`moonharvest_detect_stream.py`)
- Baca stdout line per line
- Parse JSON → distribusi ke event handler
- Kelola lifecycle: start, stop, restart, cleanup

**MavLinkService.cs** — Komunikasi UAV
- Implementasi MAVLink v1 dan v2
- Support: Serial (USB), TCP, UDP
- Decode pesan telemetri (GPS, IMU, baterai, flight mode)
- Kirim perintah (ARM, TAKEOFF, LAND, RTL, dll.)
- Upload/download mission waypoint

**VegetationAnalyzerMobile.cs** — Analisis Ringan di Android
- Versi C# dari pipeline HSV sederhana
- Menggunakan OpenCvSharp
- Untuk perangkat yang tidak bisa jalankan Python (Android)

**HarvestFunctionalService.cs** — Laporan & Analitik
- Simpan data deteksi per sesi ke SQLite
- Generate ringkasan laporan
- Export data ke format laporan

### Halaman GCS dan Fungsinya

```
MainPage_Modern.xaml
    └── ModernSidebar (navigasi)
        ├── DashboardPage    ← landing utama
        ├── CameraPage       ← tampilan video + AI
        ├── FlightPage       ← kontrol penerbangan
        ├── MapPage          ← peta offline
        ├── MissionPlannerPage ← waypoint
        ├── StatsPage        ← grafik kesehatan
        ├── ReportsPage      ← riwayat laporan
        ├── AISettingsPage   ← konfigurasi model
        ├── EdgeModePage     ← mode tablet lapangan
        └── SettingsPage     ← pengaturan umum
```

---

## 7. Teknologi yang Digunakan

Hafal daftar ini — juri sering bertanya "menggunakan apa saja?"

### Backend / Processing (Python)

| Library | Versi | Fungsi |
|---------|-------|--------|
| Python | 3.10+ | Runtime skrip deteksi |
| OpenCV (cv2) | 4.x | Pemrosesan citra, HSV, draw |
| NumPy | 1.x | Operasi array/matrix |
| ONNX Runtime | 1.x | Inference model ONNX |
| Ultralytics YOLOv8 | 8.x | Framework training + inference YOLO |
| ffmpeg | - | Re-encode video output ke H.264 |

### Frontend / GCS (.NET)

| Library | Fungsi |
|---------|--------|
| Uno Platform (SDK) | Cross-platform UI framework |
| .NET 9 MAUI | Runtime |
| CommunityToolkit.Mvvm | MVVM pattern, data binding |
| Mapsui | Peta offline (raster/vector tile) |
| LiveChartsCore.SkiaSharpView | Grafik statistik |
| Microsoft.ML.OnnxRuntime | Inference ONNX di C# |
| Microsoft.Data.Sqlite | Database lokal |
| OpenCvSharp | Computer vision di C# |
| Serilog | Logging |

### Protokol & Standar

| Protokol | Fungsi |
|----------|--------|
| MAVLink v1/v2 | Komunikasi GCS ↔ UAV |
| JSON (stdout) | Komunikasi Python ↔ C# |
| WebSocket | Komunikasi alternatif serial |
| ONNX | Format model AI universal |

---

## 8. Hasil & Metrik Performa

### Akurasi Deteksi

| Mode | Akurasi Validasi | Akurasi Aerial (UAV) |
|------|-----------------|---------------------|
| HSV Only | ~70–75% | ~65% |
| YOLO Only (v4) | 95.0% (lab) | ~7% (domain mismatch!) |
| YOLO Only (v5) | 98.8% (lab) | ~75% |
| **Fusion HSV + YOLO v5** | **98.8% (lab)** | **82.2% (UAV)** |

**Catatan penting:** YOLO v4 saat dipakai di footage UAV nyata hanya 7% karena dilatih dominan data ground-level. Ini adalah contoh **domain mismatch** — model bagus di lab, buruk di lapangan. Fusion dengan HSV mengoreksi ini.

### Kecepatan Pemrosesan

| Hardware | Mode | FPS |
|----------|------|-----|
| RTX 3050 (CUDA) | Fusion | ~2 FPS |
| RTX 3050 (CUDA) | HSV Only | ~8 FPS |
| CPU Only (i7) | Fusion | ~0.5 FPS |
| CPU Only (i7) | HSV Only | ~3 FPS |

### Ukuran Model

| Model | Format | Ukuran |
|-------|--------|--------|
| YOLOv8n-cls (training) | .pt | ~5.9MB |
| moonharvest-health-cls | ONNX FP32 | ~2.9MB |
| moonharvest-health-cls-int8 | ONNX INT8 | ~1.5MB |

### Field Health Index — Hasil pada Video Demo

Pada video `gabung.mp4` (sawah 15 hari):
- **FHI Fusion:** 78.9 → Status "Perhatian" (50–75 range)
- Distribusi: Lush Green ~60%, Inconsistent ~25%, Drought ~10%, Bare Soil ~5%

---

## 9. Panduan Presentasi

### Struktur Presentasi yang Disarankan (10–15 menit)

**Pembukaan (1–2 menit)**
> "MoonHarvest adalah sistem monitoring kesehatan tanaman padi berbasis UAV yang menggabungkan deteksi warna HSV dengan deep learning YOLOv8 untuk mengidentifikasi kondisi lahan secara real-time."

Sampaikan 3 poin utama:
1. Masalah nyata (inspeksi sawah lambat & mahal)
2. Solusi kami (UAV + computer vision + GCS)
3. Keunggulan (fusion adaptif, multi-platform, open)

**Demo Sistem (5–7 menit)**
1. Tunjukkan GCS berjalan di laptop/tablet
2. Buka Camera Page → play video demo
3. Tunjukkan bounding box per kelas muncul real-time
4. Tunjukkan FHI berubah seiring video
5. Buka Stats Page → tunjukkan grafik distribusi
6. Buka Reports Page → tunjukkan laporan tersimpan

**Penjelasan Teknis (3–4 menit)**
- Gambar alur pipeline (gunakan diagram di README)
- Jelaskan HSV → ONNX → Fusion → FHI
- Tunjukkan angka akurasi 82.2% aerial

**Penutup (1 menit)**
- Kesimpulan + FHI lahan demo = 78.9
- Rencana pengembangan (multispektral, GPS-tagging, cloud)

### Tips Presentasi

- **Jangan klaim lebih dari yang bisa kamu buktikan** — juri menghargai kejujuran
- **Siapkan demo video offline** — jangan andalkan koneksi internet
- **Hafalkan 3 angka kunci:** 98.8% (validasi), 82.2% (aerial), 2.5ms (inference)
- **Jika ditanya sesuatu yang tidak tahu** — jawab "saat ini belum, tapi rencana pengembangannya adalah..."
- **Gunakan istilah teknis dengan benar** — jangan sebut YOLO sebagai "AI biasa"

---

## 10. Daftar Pertanyaan Juri & Jawaban

### Pertanyaan Umum

**Q: Apa bedanya MoonHarvest dengan aplikasi drone monitoring yang sudah ada?**

A: Kebanyakan solusi yang ada menggunakan kamera multispektral yang harganya puluhan juta. MoonHarvest dirancang untuk kamera RGB standar yang sudah ada di drone konsumer. Selain itu, kami menggunakan pendekatan fusion dua metode — HSV dan YOLO — yang saling melengkapi. HSV cepat dan tidak butuh GPU, YOLO lebih akurat menangkap pola kompleks. Digabungkan, akurasi aerial mencapai 82.2% tanpa kamera khusus.

**Q: Mengapa menggunakan HSV, bukan NDVI?**

A: NDVI membutuhkan kamera Near-Infrared yang harganya jauh lebih mahal. HSV adalah representasi warna yang lebih intuitif untuk threshold warna tanaman, dan lebih stabil terhadap variasi pencahayaan dibanding RGB langsung. Untuk drone konsumer dengan kamera RGB, HSV adalah pendekatan yang paling praktis. Di masa depan, kami berencana menambahkan dukungan kamera multispektral.

**Q: Seberapa akurat sistemnya?**

A: Di dataset validasi, model mencapai 98.8%. Di kondisi UAV nyata, dengan fusion HSV + YOLO, kami mencapai 82.2%. Ada gap karena kondisi lapangan lebih bervariasi — bayangan, pantulan air, variasi umur tanaman. Kami juga terbuka tentang ini: model YOLO saja tanpa fusion hanya mencapai 7% di video UAV karena masalah domain mismatch, yang kemudian kami atasi dengan fusion.

**Q: Apakah bisa digunakan real-time saat drone terbang?**

A: Saat ini sistem berjalan pada ~2 FPS dalam mode fusion di laptop dengan RTX 3050. Untuk inspeksi lapangan, 2 FPS sudah cukup karena drone terbang lambat (±5 m/s). Untuk meningkatkan ke real-time penuh, bisa menggunakan model INT8 yang 2× lebih cepat, atau GPU yang lebih kuat.

### Pertanyaan Teknis

**Q: Bagaimana cara kerja HSV segmentasi?**

A: Frame video dikonversi dari BGR ke ruang warna HSV. Kemudian kami definisikan range Hue-Saturation-Value untuk setiap kelas. Misalnya, tanaman sehat memiliki Hue 25–100 (hijau) dengan Saturation minimal 18. Sebelum segmentasi, kami juga filter menggunakan ExG (Excess Green Index = 2G-R-B) untuk memastikan hanya area vegetasi yang diproses. Bayangan dihilangkan dengan membuang pixel dengan Value < 45.

**Q: Apa itu Fusion dan bagaimana cara kerjanya?**

A: Fusion menggabungkan dua sumber informasi: label dari HSV dan prediksi dari model YOLO, dengan bobot yang berbeda per kelas. Misalnya untuk kelas "sehat", kami percaya HSV 80% karena hijau = sehat itu sederhana dan reliable. Untuk kelas "stressed", kami percaya YOLO 55% karena model lebih baik menangkap pola warna kuning yang kompleks. Bobot ini dikalibrasi secara empiris dari percobaan di lapangan.

**Q: Mengapa menggunakan ONNX dan bukan model PyTorch langsung?**

A: ONNX adalah format model universal yang bisa dijalankan di berbagai platform tanpa butuh PyTorch. Ini penting karena GCS kami bisa berjalan di Android, yang tidak bisa menjalankan PyTorch. ONNX Runtime juga lebih efisien untuk inference-only karena tidak membawa overhead training framework. Selain itu, model ONNX bisa diintegrasikan langsung ke kode C# via Microsoft.ML.OnnxRuntime.

**Q: Bagaimana sistem komunikasi dengan drone?**

A: Kami menggunakan protokol MAVLink — standar industri open-source untuk komunikasi GCS-UAV yang dipakai ArduPilot, PX4, dan Mission Planner. Support koneksi via Serial (kabel USB), TCP (WiFi), dan UDP (untuk simulasi SITL). GCS bisa menampilkan telemetri (GPS, altitude, heading, baterai), mengirim perintah (ARM, TAKEOFF, LAND, RTL), dan upload/download misi waypoint.

**Q: Mengapa menggunakan Uno Platform, bukan Flutter atau React Native?**

A: Uno Platform memungkinkan satu codebase C# XAML dikompilasi ke desktop Linux/Windows DAN Android. Ini penting karena banyak library yang kami butuhkan (OpenCvSharp, MAVLink.NET, Microsoft.ML.OnnxRuntime) sudah ada di ekosistem .NET. Jika pakai Flutter, kami harus port ulang semua integrasi tersebut.

**Q: Bagaimana menangani variasi pencahayaan saat terbang?**

A: Dua cara utama. Pertama, preprocessing: White Balance menggunakan Gray World Assumption mengoreksi cast warna, dan CLAHE meningkatkan kontras lokal. Kedua, filter ExG dan threshold HSV yang sengaja dibuat agak toleran (range lebar) agar robust terhadap variasi cahaya. EMA smoothing pada FHI juga membantu stabilitas temporal saat ada perubahan cahaya tiba-tiba.

**Q: Apa keterbatasan utama sistem ini?**

A: Tiga keterbatasan utama. Pertama, akurasi 82.2% di kondisi aerial — masih ada 18% error, terutama pada area transisi antar kelas. Kedua, sistem dikalibrasi untuk padi umur 15 hari; pada fase lain akurasi bisa berbeda. Ketiga, hanya menggunakan RGB, jadi tidak bisa mendeteksi kondisi yang hanya terlihat di near-infrared seperti beberapa jenis stres awal. Kami menyajikan ini sebagai alat monitoring awal, bukan pengganti inspeksi lapangan.

**Q: Bagaimana cara training modelnya?**

A: Dataset dikumpulkan dari video UAV sawah yang di-label manual frame per frame, ditambah dataset publik dari Kaggle dan Roboflow. Training menggunakan framework Ultralytics YOLOv8 dengan arsitektur YOLOv8n-cls (nano classifier). Augmentasi meliputi flip, brightness, crop random. Model dieksport ke ONNX setelah training selesai.

**Q: Apakah bisa untuk tanaman selain padi?**

A: Secara teknis pipeline-nya bisa, tapi parameter HSV dan model ONNX saat ini dikalibrasi khusus untuk padi. Untuk tanaman lain butuh dua hal: kalibrasi ulang threshold HSV sesuai warna tanaman tersebut, dan pelatihan ulang model ONNX dengan dataset tanaman yang baru. Arsitekturnya memang didesain agar mudah dikonfigurasi ulang melalui `FINAL_CONFIG.json`.

**Q: Bagaimana dengan keamanan data dan privasi?**

A: Semua processing berjalan lokal di laptop operator — tidak ada data yang dikirim ke server eksternal. Laporan disimpan di SQLite lokal. Tidak ada dependensi cloud. Ini membuatnya cocok untuk penggunaan di area dengan internet terbatas.

**Q: Apa rencana pengembangan ke depan?**

A: Ada tiga prioritas utama. Pertama, memperluas dataset UAV untuk meningkatkan akurasi aerial. Kedua, menambahkan GPS-tagging pada hasil deteksi agar bisa di-overlay ke peta. Ketiga, menjajaki dukungan kamera multispektral untuk deteksi kondisi yang tidak terlihat di RGB. Jangka panjang, kami ingin menambahkan tracking temporal — membandingkan kondisi lahan dari penerbangan berbeda.

### Pertanyaan Tentang Hasil

**Q: FHI 78.9 itu artinya apa?**

A: FHI 78.9 berada di range 75–100 yang kami definisikan sebagai "Baik", tapi mendekati batas bawah. Artinya sekitar 79% dari area lahan yang terdeteksi dalam kondisi baik. Sisanya sekitar 21% perlu perhatian — campuran area stres, kekeringan, dan tanah terbuka. Untuk sawah 15 hari di video demo kami, ini adalah hasil yang realistis.

**Q: Mengapa tidak 100% akurat?**

A: Tidak ada sistem computer vision yang 100% akurat di kondisi nyata. Tantangan utamanya: (1) warna tanaman padi sehat dan stres ringan bisa sangat mirip terutama di kondisi cahaya tertentu, (2) bayangan dan pantulan air menciptakan ambiguitas warna, (3) variasi genetik padi menghasilkan warna yang berbeda antar varietas. 82.2% untuk RGB-only di kondisi UAV adalah hasil yang kompetitif dibanding solusi sejenis.

---

## 11. Hal yang Boleh & Tidak Boleh Diklaim

### BOLEH Diklaim

- "Sistem monitoring visual kesehatan padi berbasis UAV dengan RGB camera"
- "Akurasi 82.2% dalam kondisi aerial menggunakan fusion HSV + YOLO"
- "Real-time processing pada ~2 FPS dengan GPU NVIDIA RTX 3050"
- "Mendukung komunikasi MAVLink standar industri"
- "Multi-platform: desktop Linux/Windows dan Android"
- "Menggunakan pendekatan fusion dua metode untuk mengatasi kelemahan masing-masing"
- "Field Health Index sebagai indikator kondisi lahan"

### JANGAN Diklaim (Bisa Jadi Boomerang)

- ❌ "Mendeteksi penyakit padi" — sistem mendeteksi *kondisi visual*, bukan diagnosa penyakit spesifik
- ❌ "Lebih akurat dari NDVI" — tidak bisa dibandingkan langsung, beda metode
- ❌ "Bisa dipakai untuk semua jenis tanaman" — belum divalidasi
- ❌ "Real-time 30 FPS" — faktanya 2 FPS di mode fusion
- ❌ "Model 98.8% akurat di lapangan" — 98.8% adalah akurasi validasi lab, bukan lapangan
- ❌ "Menggantikan agronomis/ahli pertanian" — ini alat bantu, bukan pengganti

### Narasi Aman untuk Juri

> "MoonHarvest adalah alat monitoring visual berbasis UAV yang membantu operator mengidentifikasi area lahan yang perlu diprioritaskan untuk inspeksi lebih lanjut. Dengan Field Health Index sebagai panduan, petani atau agronomis dapat mengalokasikan waktu inspeksi lapangan secara lebih efisien."

---

## 12. Kosakata Teknis Penting

Hafal definisi ini agar bisa menjawab dengan lancar:

| Istilah | Definisi Singkat |
|---------|-----------------|
| **GCS (Ground Control Station)** | Aplikasi di laptop/tablet untuk monitoring dan kontrol UAV |
| **UAV (Unmanned Aerial Vehicle)** | Drone/wahana terbang tanpa awak |
| **HSV** | Hue-Saturation-Value — ruang warna yang dipakai untuk segmentasi |
| **YOLOv8** | You Only Look Once versi 8 — framework deep learning untuk deteksi objek |
| **ONNX** | Open Neural Network Exchange — format model AI universal |
| **Fusion** | Penggabungan hasil dua metode dengan bobot tertentu |
| **NMS (Non-Maximum Suppression)** | Teknik menghilangkan deteksi duplikat yang saling overlap |
| **FHI (Field Health Index)** | Angka 0–100 representasi kondisi kesehatan lahan |
| **EMA (Exponential Moving Average)** | Teknik smoothing temporal untuk stabilisasi nilai |
| **CLAHE** | Contrast Limited Adaptive Histogram Equalization — peningkatan kontras lokal |
| **ExG (Excess Green Index)** | Indeks vegetasi sederhana: 2G-R-B, untuk memilah area tanaman |
| **MAVLink** | Micro Air Vehicle Link — protokol komunikasi standar UAV |
| **Domain Mismatch** | Model yang dilatih di satu kondisi tapi dipakai di kondisi berbeda |
| **Confidence Score** | Tingkat keyakinan model (0–1) terhadap prediksinya |
| **Bounding Box** | Kotak persegi yang menandai area deteksi |
| **INT8 Quantization** | Kompresi model dari FP32 ke 8-bit integer → lebih cepat, sedikit lebih rendah akurasi |
| **Patch** | Potongan kecil dari frame video yang di-crop untuk di-infer ke classifier |
| **Connected Components** | Algoritma untuk menemukan area yang terhubung dari hasil segmentasi biner |
| **SITL** | Software In The Loop — simulasi UAV tanpa hardware fisik |
| **Uno Platform** | Framework .NET untuk build app cross-platform dari satu codebase |

---

*Dokumen ini dibuat untuk persiapan presentasi TEKNOFEST 2026.*
*Update terakhir berdasarkan kode versi demo-teknofest branch.*
