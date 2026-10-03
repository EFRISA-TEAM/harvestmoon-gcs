# MoonHarvest — Panduan Membuat Poster
## Untuk Pameran & Kompetisi TEKNOFEST 2026

---

## Daftar Isi

1. [Jenis Poster yang Perlu Dibuat](#1-jenis-poster-yang-perlu-dibuat)
2. [Poster 1 — Overview Sistem](#2-poster-1--overview-sistem)
3. [Poster 2 — Pipeline Teknis](#3-poster-2--pipeline-teknis)
4. [Poster 3 — Hasil & Performa](#4-poster-3--hasil--performa)
5. [Panduan Desain Visual](#5-panduan-desain-visual)
6. [Konten Wajib di Setiap Poster](#6-konten-wajib-di-setiap-poster)
7. [Tools yang Bisa Digunakan](#7-tools-yang-bisa-digunakan)
8. [Checklist Sebelum Cetak](#8-checklist-sebelum-cetak)

---

## 1. Jenis Poster yang Perlu Dibuat

Untuk kompetisi teknologi seperti TEKNOFEST, ada **3 jenis poster** yang disarankan:

| No | Jenis Poster | Ukuran | Tujuan |
|----|-------------|--------|--------|
| 1 | **Overview Sistem** | A1 (60×84cm) atau A0 | Gambar besar untuk dipasang di booth, menjelaskan sistem secara keseluruhan |
| 2 | **Pipeline Teknis** | A1 atau A2 | Untuk juri teknis, menjelaskan cara kerja AI dan deteksi |
| 3 | **Hasil & Performa** | A2 (42×60cm) | Menampilkan angka-angka kunci, grafik, dan perbandingan |

Jika hanya bisa membuat **1 poster**, pilih **Poster Overview Sistem** karena paling informatif untuk semua kalangan.

---

## 2. Poster 1 — Overview Sistem

**Judul:** MoonHarvest: UAV-Based Rice Field Health Monitoring System

### Layout yang Disarankan (Portrait A1)

```
┌─────────────────────────────────────────────────────────┐
│              HEADER: Logo + Judul + Tagline              │
├─────────────────────────────────────────────────────────┤
│                                                          │
│   MASALAH          SOLUSI          DAMPAK                │
│   (1 kolom)       (1 kolom)       (1 kolom)             │
│                                                          │
├─────────────────────────────────────────────────────────┤
│                                                          │
│   DIAGRAM ALUR SISTEM (gambar besar, tengah)             │
│   UAV → Video → Python Detection → GCS Dashboard        │
│                                                          │
├─────────────────────────────────────────────────────────┤
│                                                          │
│   4 KELAS DETEKSI (gambar + label + warna per kelas)     │
│   [Lush Green] [Inconsistent] [Drought] [Bare Soil]      │
│                                                          │
├─────────────────────────────────────────────────────────┤
│                                                          │
│   SCREENSHOT GCS          ANGKA KUNCI                   │
│   (Dashboard/Camera       - 82.2% aerial accuracy       │
│    Page)                  - 98.8% validation acc.       │
│                           - 2.5ms inference speed       │
│                           - FHI: 78.9 (demo video)      │
│                                                          │
├─────────────────────────────────────────────────────────┤
│         FOOTER: Tim + Institusi + Kontak + QR Code       │
└─────────────────────────────────────────────────────────┘
```

### Konten per Bagian

**HEADER**
- Nama proyek: **MoonHarvest**
- Subtitle: *Platform Monitoring Kesehatan Tanaman Padi Berbasis UAV dengan Computer Vision*
- Logo tim / institusi
- Badge teknologi: Python | .NET MAUI | YOLOv8 | OpenCV | MAVLink

**MASALAH** (singkat, poin)
- Inspeksi manual sawah luas = lambat & mahal
- Deteksi masalah terlambat → gagal panen
- Kamera multispektral terlalu mahal untuk petani

**SOLUSI** (singkat, poin)
- Drone + kamera RGB standar
- Deteksi otomatis dengan HSV + YOLOv8
- GCS real-time di laptop & Android tablet

**DAMPAK** (singkat, poin)
- Inspeksi lahan lebih cepat
- Prioritisasi area bermasalah lebih akurat
- Biaya lebih rendah dari solusi multispektral

**DIAGRAM ALUR** — buat diagram sederhana:
```
[Drone UAV]  →  [Video RGB]  →  [HSV Segmentasi]  ─┐
                                                    ├→ [Fusion] → [FHI]
                               [YOLO Classifier]  ─┘
                                        ↓
                          [GCS Dashboard - Real-time]
```

**4 KELAS DETEKSI** — buat 4 kotak berwarna:

| Kotak | Warna | Label | Deskripsi singkat |
|-------|-------|-------|------------------|
| 1 | Hijau (#32D232) | Lush Green | Tanaman sehat, tumbuh normal |
| 2 | Kuning (#FFC800) | Inconsistent Growth | Pertumbuhan tidak merata |
| 3 | Merah (#FF5A00) | Drought / Severe Stress | Kekeringan atau stres berat |
| 4 | Abu-abu (#8C8C8C) | Bare Soil / Gap | Tanah terbuka, tanpa tanaman |

**SCREENSHOT GCS** — ambil screenshot dari:
- Camera Page dengan bounding box aktif
- Dashboard dengan FHI gauge

**ANGKA KUNCI** — kotak besar dengan angka mencolok:
- **82.2%** — Aerial Detection Accuracy
- **98.8%** — Validation Accuracy
- **2.5ms** — Inference Speed per image
- **4 kelas** — Kondisi lahan terdeteksi
- **Multi-platform** — Linux / Windows / Android

---

## 3. Poster 2 — Pipeline Teknis

**Judul:** Technical Pipeline: HSV-First Fusion Detection (v7c)

**Target audiens:** Juri teknis / akademisi

### Layout yang Disarankan (Landscape A1)

```
┌──────────────────────────────────────────────────────────────┐
│                     JUDUL + SUBTITLE                          │
├───────────┬──────────────┬───────────────┬────────────────────┤
│ STAGE 1   │   STAGE 2    │    STAGE 3    │     STAGE 4        │
│           │              │               │                    │
│ Pre-      │ HSV          │ ONNX          │ Fusion +           │
│ processing│ Segmentation │ Classifier    │ NMS + FHI          │
│           │              │               │                    │
│ • WB      │ • ExG filter │ • 224×224 px  │ • Weighted         │
│ • CLAHE   │ • HSV range  │ • 4 classes   │   fusion           │
│ • Shadow  │ • Morph ops  │ • conf ≥0.70  │ • NMS v7c          │
│   removal │ • CC label   │ • ONNX RT     │ • EMA FHI          │
│           │              │               │                    │
│ [gambar   │ [gambar mask │ [gambar grid  │ [gambar output     │
│ sebelum/  │ HSV per      │ patch 224px]  │ final bounding     │
│ sesudah   │ kelas]       │               │ box]               │
│ CLAHE]    │              │               │                    │
├───────────┴──────────────┴───────────────┴────────────────────┤
│                    FUSION WEIGHT TABLE                        │
│  Kelas          | Alpha YOLO | Alpha HSV | Alasan             │
│  healthy_crop   |    0.20    |   0.80    | HSV reliable utk  │
│  stressed_crop  |    0.55    |   0.45    | YOLO lebih baik   │
│  drought_stress |    0.50    |   0.50    | Seimbang          │
│  bare_soil      |    0.45    |   0.55    | HSV sedikit lebih │
├────────────────────────────────────────────────────────────────┤
│   ARSITEKTUR MODEL: YOLOv8n-cls | 2.9MB | ONNX FP32/INT8     │
└────────────────────────────────────────────────────────────────┘
```

### Konten Detail untuk Poster Teknis

**Stage 1 — Preprocessing**
- Input: Frame BGR dari video UAV
- Gray World White Balance → koreksi warna
- CLAHE (clip=2.0, grid=8×8) → kontras lokal
- Shadow removal: pixel V < 45 → dibuang
- Output: Frame yang telah dinormalisasi

**Stage 2 — HSV Segmentation**
- Konversi BGR → HSV
- ExG (Excess Green = 2G-R-B) filter, threshold 0.0213
- Multi-threshold per kelas (lihat tabel HSV di bawah)
- Morphological: erode + dilate dengan kernel 5×5
- Connected Components → list region (min area 0.15% frame)

**Stage 3 — ONNX Classifier**
- Crop patch 224×224px per region dari HSV
- Inference: YOLOv8n-cls → 4 class probabilities
- Override HSV label jika: confidence ≥ 0.70 DAN kelas compatible (ONNX_COMPAT check)
- Kecepatan: 2.5ms/image (RTX 3050 CUDA)

**Stage 4 — Fusion + NMS + FHI**
- Fusion score = α_yolo × score_yolo + α_hsv × score_hsv
- NMS per-class: IoU threshold 0.20
- NMS cross-class: IoU threshold 0.30
- Min area filter: 600 px²
- FHI = 100 - weighted_stress_score
- EMA smoothing: FHI_t = 0.30 × FHI_raw + 0.70 × FHI_{t-1}

**Tabel HSV Threshold:**

| Kelas | Hue | Sat min | Val range |
|-------|-----|---------|-----------|
| Healthy | 25–100 | 18 | 60–255 |
| Stressed | 10–95 | 10 | 50–255 |
| Drought | 8–40 | 30 | 80–235 |
| Bare Soil | (any) | (max 12) | 90–240 |

---

## 4. Poster 3 — Hasil & Performa

**Judul:** MoonHarvest: Detection Results & Performance Metrics

**Target audiens:** Semua kalangan — fokus pada hasil nyata

### Layout yang Disarankan (Portrait A2)

```
┌──────────────────────────────────────────┐
│           JUDUL + SUBTITLE               │
├──────────────────────────────────────────┤
│   PERBANDINGAN AKURASI (bar chart)       │
│                                          │
│   HSV Only    ████░░░░░░  ~70%           │
│   YOLO v4     █░░░░░░░░░   ~7% (UAV!)   │
│   YOLO v5     ███████░░░  ~75%           │
│   Fusion v5   ████████░░  82.2% ★        │
│                                          │
├──────────────────────────────────────────┤
│   DISTRIBUSI KELAS (pie chart)           │
│   dari video demo gabung.mp4             │
│                                          │
│   Lush Green:    60%  (hijau)            │
│   Inconsistent:  25%  (kuning)           │
│   Drought:       10%  (merah)            │
│   Bare Soil:      5%  (abu)             │
│                                          │
├──────────────────────────────────────────┤
│   FIELD HEALTH INDEX: 78.9              │
│   ████████████████████░░░░  78.9/100    │
│   Status: PERHATIAN (50–75)             │
├──────────────────────────────────────────┤
│   PERFORMA SISTEM                        │
│   GPU  │ Fusion: 2 FPS  │ HSV: 8 FPS   │
│   CPU  │ Fusion: 0.5FPS │ HSV: 3 FPS   │
│   Model size: 2.9MB (FP32), 1.5MB (INT8)│
├──────────────────────────────────────────┤
│   SCREENSHOT SEBELUM vs SESUDAH         │
│   [Frame asli] → [Frame + bounding box] │
├──────────────────────────────────────────┤
│        FOOTER: Tim + Kontak             │
└──────────────────────────────────────────┘
```

### Penjelasan Mengapa YOLO v4 Hanya 7%

Wajib dijelaskan di poster ini karena angkanya mengejutkan:

> "Model YOLO v4 dilatih dominan dari data ground-level (foto dari tanah). Saat dipakai di video UAV ketinggian 60–80m, skala dan sudut pandang berbeda drastis. Ini disebut **domain mismatch**. Fusion dengan HSV mengoreksi masalah ini, menghasilkan 82.2%."

Ini justru menunjukkan **pemahaman mendalam** tentang keterbatasan model — nilai positif di mata juri.

---

## 5. Panduan Desain Visual

### Palet Warna Resmi MoonHarvest

Gunakan warna ini konsisten di semua poster:

| Nama | Hex | Penggunaan |
|------|-----|-----------|
| MoonHarvest Green | `#2ECC71` | Aksen utama, header, kelas Lush Green |
| Deep Green | `#1A7A45` | Background dark, teks pada area terang |
| Alert Orange | `#F39C12` | Kelas Inconsistent, highlight |
| Danger Red | `#E74C3C` | Kelas Drought, warning |
| Neutral Gray | `#8C8C8C` | Kelas Bare Soil, teks sekunder |
| Background Dark | `#1A1A2E` | Background poster (dark theme) |
| Background Card | `#16213E` | Card/panel pada dark theme |
| Text Primary | `#FFFFFF` | Teks utama pada dark bg |
| Text Secondary | `#B0BEC5` | Teks pendukung |

### Tipografi

- **Judul poster:** Font bold, besar (72–96pt), contoh: Montserrat Bold / Inter Black
- **Heading seksi:** 36–48pt Bold
- **Body text:** 18–24pt Regular (minimum di poster A1)
- **Caption/label:** 14–16pt, jangan lebih kecil (tidak terbaca dari jarak 1m)
- **Angka kunci:** 80–120pt Bold, warna aksen

### Prinsip Desain

1. **Satu pesan utama per poster** — jangan berlebihan informasi
2. **Rule of thirds** — bagi poster menjadi 3×3 grid, elemen penting di perpotongan
3. **Whitespace cukup** — jangan padatkan semua area
4. **Hierarki visual jelas** — judul > heading > body, ukuran berbeda signifikan
5. **Konsisten warna** — hijau = sehat, merah = masalah, di semua elemen
6. **Gambar nyata lebih baik dari ilustrasi** — pakai screenshot GCS asli + frame video

### Hal yang Harus Ada di Setiap Poster

- Logo tim / institusi (pojok atas kiri atau kanan)
- Nama proyek: **MoonHarvest**
- Nama kompetisi: TEKNOFEST 2026
- QR code (pojok bawah) yang mengarah ke repo GitHub atau demo video
- Nama anggota tim

---

## 6. Konten Wajib di Setiap Poster

### Angka-Angka yang Harus Muncul (setidaknya di 1 poster)

Selalu tampilkan angka-angka ini karena mudah diingat juri:

| Angka | Konteks |
|-------|---------|
| **98.8%** | Validation accuracy model klasifikasi |
| **82.2%** | Aerial accuracy (fusion HSV + YOLO di UAV nyata) |
| **2.5ms** | Inference speed per image (RTX 3050) |
| **4 kelas** | Jumlah kategori kondisi lahan |
| **78.9** | FHI dari video demo sawah 15 hari |
| **2.9 MB** | Ukuran model ONNX (compact) |

### Diagram yang Wajib Ada

1. **Alur sistem** (UAV → Video → Python → GCS) — di Poster 1
2. **4 kelas berwarna** dengan sample gambar — di Poster 1 dan 3
3. **Perbandingan akurasi** HSV vs YOLO vs Fusion — di Poster 3

### Screenshot GCS yang Disarankan

Ambil screenshot dari GCS saat running demo:

| Screenshot | Halaman | Yang ditampilkan |
|-----------|---------|-----------------|
| Screenshot 1 | Camera Page | Video frame + bounding box aktif per kelas |
| Screenshot 2 | Dashboard | FHI gauge + stats ringkasan |
| Screenshot 3 | Stats Page | Grafik distribusi kelas (pie/bar chart) |
| Screenshot 4 | Map Page | Tampilan peta dengan waypoint |

---

## 7. Tools yang Bisa Digunakan

### Untuk Membuat Poster

| Tool | Platform | Biaya | Keunggulan |
|------|----------|-------|-----------|
| **Canva** | Web/Desktop | Gratis (basic) | Template siap pakai, mudah |
| **Figma** | Web/Desktop | Gratis (basic) | Desain profesional, vector |
| **Adobe Illustrator** | Desktop | Berbayar | Standar industri |
| **Inkscape** | Desktop | Gratis | Open source, vector |
| **PowerPoint/LibreOffice Impress** | Desktop | Gratis/berbayar | Mudah, sudah familiar |

**Rekomendasi:** Canva untuk yang butuh cepat, Figma untuk hasil lebih profesional.

### Template Canva yang Dicari

Cari di Canva dengan keyword:
- "Research poster template"
- "Science fair poster"
- "Tech competition poster"
- "A1 poster dark theme"

### Untuk Membuat Diagram Alur

| Tool | Link | Keunggulan |
|------|------|-----------|
| **draw.io / diagrams.net** | diagrams.net | Gratis, bisa export SVG |
| **Mermaid** | mermaid.js.org | Diagram dari kode, mudah di-update |
| **Excalidraw** | excalidraw.com | Gaya hand-drawn, modern |
| **Lucidchart** | lucidchart.com | Profesional |

### Untuk Grafik/Chart

Buat chart dari data nyata, bukan ilustrasi:
- **Python + matplotlib** — generate chart langsung dari data deteksi
- **Canva Charts** — input data manual, auto-format
- **Google Sheets** — buat chart lalu screenshot

---

## 8. Checklist Sebelum Cetak

### Konten

- [ ] Judul jelas dan terbaca dari jarak 3 meter
- [ ] Angka kunci (98.8%, 82.2%, 2.5ms) tercantum
- [ ] 4 kelas dengan warna yang benar tercantum
- [ ] Diagram alur sistem ada
- [ ] Screenshot GCS nyata (bukan mockup kosong)
- [ ] Nama tim dan institusi ada
- [ ] Tanggal / tahun kompetisi ada

### Desain

- [ ] Ukuran file cukup besar untuk cetak (min 150 DPI untuk A1 = min 3508×4961px)
- [ ] Font tidak lebih kecil dari 14pt (di ukuran cetak asli)
- [ ] Kontras teks vs background cukup (AA accessibility standard: rasio 4.5:1)
- [ ] Tidak ada typo — minta orang lain proofread
- [ ] Warna konsisten dengan palet MoonHarvest
- [ ] Logo dalam resolusi tinggi (format SVG atau PNG 300DPI)

### Teknis

- [ ] Export ke PDF dengan profil warna CMYK (untuk cetak offset) atau RGB (untuk digital/inkjet)
- [ ] Resolusi minimum 150 DPI untuk poster besar (rekomendasi 300 DPI)
- [ ] Buat juga versi digital (1920×1080 atau 1080×1920) untuk tampilan layar
- [ ] Simpan file editable (Canva/Figma/Illustrator) untuk revisi

---

## Urutan Pembuatan yang Disarankan

Jika waktu terbatas, kerjakan dalam urutan ini:

1. **Poster Overview Sistem (A1)** — paling penting, wajib ada
2. **Versi digital Poster Overview** — untuk presentasi slide / layar
3. **Poster Hasil & Performa (A2)** — untuk booth yang lebih detail
4. **Poster Pipeline Teknis (A1)** — jika ada pertanyaan teknis mendalam dari juri

---

*Dokumen panduan poster ini dibuat untuk persiapan TEKNOFEST 2026.*
*Selalu gunakan data dan screenshot nyata dari sistem — hindari mockup yang tidak merepresentasikan sistem asli.*
