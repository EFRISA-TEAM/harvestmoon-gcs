# Panduan Penggunaan MoonHarvest

**Precision Agriculture Monitoring System — Cross-Platform Ground Control Station dengan Real-Time Computer Vision**

*Tim EFRISA · TEKNOFEST Agricultural Technologies Competition · Team ID 783316*

Panduan ini hanya menjelaskan fitur yang benar-benar ada dan berjalan di aplikasi MoonHarvest saat ini, mengikuti struktur sidebar aplikasi persis seperti yang dilihat pengguna: **Dashboard · Live → Camera → Peta & Misi → Crop Analysis → Edge Mode → AI Settings → Reports**.

- **Untuk**: Petani smallholder (1–10 ha) & menengah (10–50 ha), koperasi tani, konsultan agronomi
- **Protokol**: MAVLink · UDP / TCP / Serial / RunCam WiFi Link 2
- **Platform**: Windows, Linux, Android — satu basis kode

---

## Daftar Isi

1. [Tentang MoonHarvest](#1-tentang-moonharvest)
2. [Bilah Atas & Koneksi](#2-bilah-atas--koneksi)
3. [Dashboard · Live](#3-dashboard--live)
4. [Camera](#4-camera)
5. [Peta & Misi](#5-peta--misi)
6. [Crop Analysis](#6-crop-analysis)
7. [Edge Mode](#7-edge-mode)
8. [AI Settings](#8-ai-settings)
9. [Reports](#9-reports)
10. [Indikator Status YOLO](#10-indikator-status-yolo)
11. [Pemecahan Masalah](#11-pemecahan-masalah)
12. [Kesesuaian dengan Proposal Kompetisi](#12-kesesuaian-dengan-proposal-kompetisi)

---

## 1. Tentang MoonHarvest

MoonHarvest adalah *Ground Control Station* (GCS) lintas platform yang mengintegrasikan computer vision real-time dengan teknologi UAV untuk pemantauan tanaman. Sidebar aplikasi berisi tujuh halaman:

| Halaman | Fungsi Singkat |
|---|---|
| **Dashboard · Live** | HUD terbang, peta live, ringkasan deteksi, dan ekspor laporan. |
| **Camera** | Sumber video (kamera lokal, RTSP, file video, bridge Python) dengan overlay YOLO. |
| **Peta & Misi** | Perencanaan waypoint, geofence, dan upload misi ke UAV. |
| **Crop Analysis** | Analisis kesehatan tanaman mendalam dari gambar, rekomendasi, dan validasi lapangan. |
| **Edge Mode** | Pengaturan performa inferensi AI on-device (model, threshold, overlay). |
| **AI Settings** | Konfigurasi penyedia LLM, voice command, dan runtime vision. |
| **Reports** | Daftar misi, detail hasil panen, dan ekspor laporan. |

Di bawah sidebar terdapat indikator **status YOLO** yang bisa diketuk untuk mengaktifkan/menonaktifkan AI (lihat Bab 10).

---

## 2. Bilah Atas & Koneksi

Kontrol koneksi dan kendali penerbangan bersifat **global** — tersedia di bilah atas (top bar) pada setiap halaman, bukan hanya di satu halaman tertentu.

### Tombol di Bilah Atas

- **Connect / Disconnect** — status koneksi MAVLink (pill hijau "Connected" atau merah "Disconnected").
- **RTL** — perintah Return-to-Launch instan ke UAV.
- **Start Mission / Stop Mission** — memulai atau menghentikan eksekusi misi yang sudah di-upload.

### Menyambungkan UAV (Connect Dialog)

Klik pill status koneksi untuk membuka dialog Connect. Empat jenis koneksi tersedia:

| Jenis Koneksi | Yang Diisi | Catatan |
|---|---|---|
| **UDP** | Host + Port | Untuk telemetry radio/companion computer. |
| **TCP** | Host + Port | Untuk simulator (SITL) atau jaringan tetap. |
| **Serial** | Port + Baud rate | Deteksi otomatis baud rate dan hot-plug perangkat. |
| **RunCam WiFi Link 2** | Pemindaian jaringan | Menghubungkan video + MAVLink sekaligus lewat WiFi RunCam. |

Preset cepat tersedia untuk **SITL**, **GCS**, **TELEMETRY**, dan **RUNCAM** agar tidak perlu mengisi alamat manual setiap kali.

---

## 3. Dashboard · Live

Halaman utama untuk memantau kondisi UAV dan hasil analisis tanaman secara langsung.

### HUD & Telemetri

- Attitude, heading, dan airspeed pill.
- Altitude, speed, heading, GPS, dan level baterai.
- Peta live tertanam (berbagi data dengan halaman Peta & Misi — waypoint dan geofence otomatis sinkron).
- Progress bar misi beserta jumlah waypoint yang sudah dicapai.

### Kontrol Live Monitoring

- **Start Demo** — memutar video/deteksi demo untuk latihan tanpa UAV nyata.
- **Start Live** — mengaktifkan feed kamera dan YOLO sungguhan.
- Tombol **Record**, **Snapshot**, dan **Pause AI** pada panel HUD.

### Ringkasan Analisis (Analysis Summary)

Menampilkan persentase deteksi untuk empat kelas kesehatan tanaman:

- **Lush Green**
- **Inconsistent Growth**
- **Drought/Severe Stress**
- **Bare Soil / Gap**

### Rekomendasi & Ekspor Laporan

Panel **Recommendations & Report Export** menyediakan tombol **Export PDF**, **Export CSV**, dan **Export JSON** yang benar-benar menghasilkan laporan lengkap dengan snapshot peta, screenshot deteksi YOLO, path video, dan garis waktu insiden. Kartu **Field Report & Recommendations** menampilkan daftar tindakan yang direkomendasikan berdasarkan deteksi langsung.

---

## 4. Camera

Ruang kerja kamera yang berdiri sendiri, terpisah dari feed di Dashboard.

### Sumber Video

Pilih salah satu tab sumber:

- **Local Camera** — kamera yang terpasang di perangkat.
- **RTSP Stream** — video streaming dari alamat RTSP.
- **Video File** — memutar file video yang tersimpan.
- **Python Bridge** — sumber video melalui bridge Python (mis. untuk pemrosesan tambahan).

### Kontrol

- **Start Camera** / **Stop Camera**
- **Screenshot** — menyimpan gambar dari frame saat ini.
- **Record** — merekam sesi video.

Overlay deteksi YOLO tampil otomatis di atas video. Untuk sumber **Video File**, tersedia mode analisis kesehatan tanaman berbasis Python dengan **toggle overlay vegetasi** dan **slider confidence** tersendiri.

---

## 5. Peta & Misi

Halaman perencanaan misi (Mission Planner) untuk menyusun jalur survei di atas peta.

1. **Klik dua kali** pada peta untuk menambahkan waypoint baru.
2. Gunakan tombol **Add Waypoint**, **Remove Waypoint** (ikon ✕ pada tiap baris di Waypoint Queue), dan **Clear** untuk mengatur ulang daftar.
3. Atur radius **Geofence** dengan slider — batas lingkaran akan tergambar di sekitar waypoint pertama.
4. Pilih penyedia peta dari dropdown **Map Provider** (ArcGIS Topographic, Google, OSM, dan variannya).
5. Klik **Upload Mission** untuk mengirim seluruh waypoint ke flight controller via protokol misi MAVLink.

> **Penting.** Upload Mission memerlukan UAV yang sudah terhubung (lihat Bab 2). Jika belum terhubung, aplikasi menampilkan pesan **"Vehicle not connected."**

---

## 6. Crop Analysis

Halaman analisis kesehatan tanaman yang lebih mendalam dibanding ringkasan di Dashboard.

- **Total Detections**, **Average Confidence**, **Impact Area (ha)**, dan **High-Priority Count** ditampilkan sebagai ringkasan angka.
- Grafik distribusi kesehatan dengan empat batang: **Healthy, Stress, Disease, Pest**.
- Teks rekomendasi otomatis berdasarkan hasil deteksi terbaru.
- Daftar **Priority Zones** — area yang perlu ditindaklanjuti lebih dulu.
- **Browse Image** untuk memuat foto lahan, lalu **Run Analysis** untuk menjalankan deteksi pada gambar tersebut.
- **Export JSON** dan **Export CSV** untuk hasil analisis.
- Fitur **Validate** untuk membandingkan hasil deteksi dengan data ground-truth kelembapan tanah di lapangan.

---

## 7. Edge Mode

Halaman untuk mengatur performa inferensi AI langsung di perangkat.

- Toggle **YOLO**, **Vegetation Overlay**, **IMU Overlay**, dan **INT8 Quantization**.
- Slider **Confidence Threshold** dan **NMS Threshold**.
- **Model Picker** — tombol **Browse Model** untuk memilih file `.onnx`, lalu **Apply Model** untuk menerapkannya.

> **Catatan.** Angka FPS/latency yang ditampilkan per mode (mis. "30–60 FPS" untuk CUDA) adalah estimasi tetap per jenis perangkat, bukan hasil pengukuran langsung — gunakan sebagai indikasi kasar, bukan benchmark real-time.

---

## 8. AI Settings

Halaman konfigurasi AI yang paling lengkap di aplikasi.

### Penyedia LLM

- Pilih **Provider utama** (OpenRouter, Gemini, OpenAI, atau Grok) dan **provider cadangan (fallback)**, masing-masing dengan kolom API key.
- Tombol **Test Primary** / **Test Fallback** untuk memverifikasi koneksi ke provider yang dipilih.

### Pengaturan Lain

- Toggle **Voice Command** beserta ambang keyakinan dan pilihan bahasa.
- Toggle lapisan **Anomaly Detection**.
- Ambang **Telemetry Sampling**.
- Kolom nama model per-tugas (per-task model name).

### Vision Runtime

Bagian ini mengatur runtime YOLO secara langsung: path file model dan file kelas, serta ambang **confidence** dan **NMS** — perubahan di sini benar-benar mengonfigurasi ulang runtime deteksi yang sedang berjalan.

---

## 9. Reports

Halaman daftar misi dan detail hasil panen.

- Daftar misi ditampilkan di sisi kiri; enam contoh misi berlabel **"DEMO"** disediakan sebagai data sampel, sementara laporan asli dimuat dari riwayat penerbangan sungguhan.
- Klik satu misi untuk membuka detail hasilnya.
- Tombol **Export PDF**, **Export CSV**, **Export JSON** — berfungsi penuh dan menghasilkan file laporan.
- Tombol **Share** dan **Send to Cooperative** tersedia di halaman ini, namun saat ini hanya menampilkan notifikasi berhasil tanpa benar-benar mengirim data — anggap sebagai pratinjau fitur yang belum final.
- Log penerbangan (Tlog) ditulis otomatis di latar belakang selama misi berjalan dan path file-nya ditampilkan sebagai referensi pada detail misi.

---

## 10. Indikator Status YOLO

Di bagian bawah sidebar terdapat indikator kecil yang menampilkan salah satu dari tiga status:

- **Yolo Off** — deteksi AI dimatikan.
- **Yolo Active** — model berjalan normal.
- **Yolo Fallback** — runtime utama tidak siap dan sistem beralih ke mode cadangan.

**Ketuk indikator ini kapan saja untuk mengaktifkan/menonaktifkan YOLO** tanpa harus membuka halaman AI Settings atau Edge Mode.

---

## 11. Pemecahan Masalah

| Gejala | Langkah Pemeriksaan |
|---|---|
| Tidak bisa Connect | Periksa jenis koneksi (UDP/TCP/Serial/RunCam) dan alamat/port yang diisi; pastikan UAV menyala dan kabel/telemetry radio terpasang benar. |
| Upload Mission gagal, muncul "Vehicle not connected" | Sambungkan UAV terlebih dahulu lewat Connect Dialog sebelum membuka Peta & Misi. |
| Video kamera tidak muncul | Periksa tab sumber yang dipilih di halaman Camera (Local/RTSP/Video File/Python Bridge) sesuai perangkat yang tersedia. |
| Deteksi AI tidak berjalan | Cek indikator status di bawah sidebar — jika menunjukkan "Yolo Off", ketuk untuk mengaktifkan; jika "Yolo Fallback", periksa pengaturan model di Edge Mode / AI Settings. |
| Tes koneksi LLM gagal di AI Settings | Periksa kembali API key provider utama/cadangan, lalu klik Test Primary/Test Fallback untuk verifikasi ulang. |
| Share / Send to Cooperative di Reports tidak mengirim apa pun | Fitur ini masih berupa pratinjau — gunakan Export PDF/CSV/JSON untuk mendapatkan file laporan sungguhan. |

---

## 12. Kesesuaian dengan Proposal Kompetisi

| Klaim di Proposal/PPT | Dibuktikan di Manual Bab |
|---|---|
| Real-time CV analysis, YOLO built-in | Bab 3 (Dashboard), Bab 4 (Camera) |
| Cross-platform Windows/Linux/Android, satu codebase | Bab 1 — seluruh halaman berjalan identik di ketiga platform |
| MAVLink telemetry (attitude, GPS, altitude, airspeed, flight mode) | Bab 2 (Koneksi), Bab 3 (Dashboard) |
| Waypoint & geofence dengan boundary enforcement | Bab 5 (Peta & Misi) |
| 4-class detection (Lush Green, Inconsistent Growth, Drought/Severe Stress, Bare Soil/Gap) | Bab 3 — Ringkasan Analisis di Dashboard |
| Model & threshold dinamis (future-proof adaptability) | Bab 7 (Edge Mode), Bab 8 (AI Settings — Vision Runtime) |
| Analisis mendalam & validasi lapangan | Bab 6 (Crop Analysis) |
| Ekspor laporan hasil panen | Bab 3 & Bab 9 (Reports) |

---

*MoonHarvest — Panduan Pengguna. Disusun oleh Tim EFRISA berdasarkan fitur yang benar-benar berjalan di aplikasi.*
