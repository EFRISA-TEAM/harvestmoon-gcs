# Panduan Operasional & Demo MoonHarvest GCS — Hibah Riset PDP (SINTA 2)

Dokumen ini adalah panduan lengkap dan *checklist* operasional untuk demonstrasi **MoonHarvest GCS** pada evaluasi/monev **Penelitian Dosen Pemula (PDP)** dan pengujian sistem menuju publikasi **Jurnal Nasional Terakreditasi SINTA 2**.

---

## 1. Quick Start: Cara Menjalankan Aplikasi

Aplikasi dapat dijalankan dengan **One-Click Script** atau langsung dari terminal:

```bash
# Opsi 1: One-Click Demo Launcher (Otomatis memeriksa venv, model, dan dependensi)
cd /home/fawwazfa/Program/Harvestmoon
./JALANKAN_DEMO.sh

# Opsi 2: Eksekusi Biner Langsung
cd /home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop
./HarvestmoonGCS
```

---

## 2. Fitur-Fitur Demo Utama untuk Penguji / Reviewer PDP

### A. Dashboard Live Monitoring & Skenario Demo
1. Buka menu **Dashboard · Live** pada sidebar kiri.
2. Pada toolbar kanan atas card monitoring, pilih skenario utama:
   - **★ Riset PDP 4-Sektor 1080p (Subang · 5 WP)**: Kompilasi video 4 sektor Full HD 1080p (60 detik), sinkron dengan 4 petak & 4 probe LoRa PDP (A1: Defisit Tanah, B2: Defisit Kanopi, C3: Ketidakmerataan Irigasi, D4: Normal).
   - **Sawah 15 HST (Karawang · 4 WP)**: Fase vegetatif awal anakan aktif (*tillering*), rekaman asli 1080p kamera UAV.
   - **Lahan Irigasi (Majalengka · 5 WP)**: Simulasi zona irigasi optimal, rekaman multi-spektral RGB.
   - **Lahan Kering (Bogor · 4 WP)**: Skenario defisit air parah / jerami kering.
3. Klik tombol **`Start Demo`**:
   - UAV virtual otomatis terbang melintasi waypoint rute misi.
   - Stream video AI aktif menampilkan *bounding box* klasifikasi kanopi padi secara real-time (YOLO11n ONNX).
   - Panel kanan (*Incident Timeline* & *Farmer Advisory*) mengkalkulasi rekomendasi agronomi berbasis AI secara otomatis.
   - Card **Pre-flight Checklist** memverifikasi 8 parameter kesiapan terbang (*GO FOR FLIGHT*).

---

### B. Menu Peta & Misi (Standar Kartografi SINTA 2)
1. Buka menu **Peta & Misi** pada sidebar.
2. Peta akan terbuka dalam kondisi bersih (*Clean Startup*).
3. **Simulasi Riset PDP (4 Petak & Ground-Truth)**:
   - Klik tombol **`🌾 Demo PDP`**: Menampilkan 4 petak poligon sawah tematik (Petak A1 Defisit Tanah, B2 Defisit Kanopi, C3 Ketidakmerataan Irigasi, D4 Normal), 4 node sensor IoT tanah (`SOIL-01` s/d `SOIL-04`), dan 24 titik deteksi spasial UAV.
   - Klik tombol **`📊 Monitor PDP`**: Membuka kartu telemetri floating berisi detail persentase FHI tiap petak, rekomendasi volume air irigasi AWD (Liter), serta telemetri sensor IoT tanah (VWC %, suhu °C, tegangan V).
   - **Sinkronisasi Sensor LoRa Otomatis**: Background service `lora_soil_bridge.py --simulate` otomatis menyuplai data telemetri tanah via UDP 14552.
4. **Perencanaan Survei Fotogrametri Udara**:
   - Klik tombol **`📐 Grid Survey`**: Sistem otomatis membangkitkan pola transek bolak-balik (*lawnmower photogrammetry pattern*) 9 waypoint di atas petak sawah dengan ketinggian 50m AGL dan GSD ~1.72 cm/px.
   - Dock tabel aeronautika di bawah akan terbuka otomatis menampilkan parameter 7 kolom (`WP#`, `COMMAND`, `LATITUDE`, `LONGITUDE`, `ALT AGL`, `SPEED`, `ACTION`).
5. **Ekspor Data untuk Manuskrip Ilmiah**:
   - Klik tombol **`📊 CSV`** pada header tabel misi: Berkas koordinat dan jarak segmen kumulatif otomatis diekspor ke:
     `/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/mission_plan_pdp_sinta2.csv`.

---

## 3. Panduan Koneksi Perangkat Keras UAV (Live Flight / HM30)

Untuk menghubungkan GCS dengan drone nyata saat demo lapangan atau uji terbang:

1. Klik tombol status **`DISCONNECTED`** di pojok kanan atas (*ModernTopBar*).
2. Dialog koneksi **MAVLink Connection** akan muncul dengan 4 pilihan:
   - **Mode 1 — SIYI HM30 Full Link (Dual Stream Video + MAVLink)**:
     - Klik tab/preset **`SIYI HM30`** (Host: `192.168.144.25`, RTSP Port: `8554`, MAVLink UDP: `14550`).
     - Video Full HD 1080p RTSP dan telemetri penerbangan terhubung serentak dalam 1 kali klik.
   - **Mode 2 — UDP Listen (Orange Pi / Companion Computer)**:
     - Klik preset **`UAV LISTEN`** (Host: `0.0.0.0`, Port: `14550`).
     - Digunakan saat companion computer menyiarkan telemetri MAVLink ke jaringan lokal laptop.
   - **Mode 3 — Telemetri Radio USB (SiK / Holybro / 433MHz / 915MHz)**:
     - Klik tab **`SERIAL`** atau preset **`TELEMETRY`**.
     - Mendeteksi otomatis `/dev/ttyUSB0` atau `/dev/ttyACM0` dengan Baud Rate `57600`.
   - **Mode 4 — RunCam WiFi Link 2**:
     - Klik tab **`RUNCAM`** (Host: `192.168.1.100` atau `10.235.141.127`).
3. Setelah terhubung:
   - Status di TopBar berubah hijau **`CONNECTED`**.
   - Indikator baterai drone, satelit GPS, dan mode terbang (AUTO/LOITER/RTL) aktif.
   - Tombol **`Start Mission`** dan **`RTL`** dapat digunakan untuk mengendalikan penerbangan otonom.

---

## 4. Model AI yang Aktif

- **Model Utama**: `moonharvest-health-cls.onnx` / `yolo11n_proposal_pdp.onnx`
- **Arsitektur**: YOLO11n Custom Trained (4 Kelas Proposal PDP: Zona Normal, Ketidakmerataan Irigasi, Defisit Kanopi, Defisit Tanah).
- **Akurasi Validasi**: Top-1 Accuracy: **95.75%**, Top-5: 100%.
- **Engine Inferensi**: ONNX Runtime (C# WinUI) + OpenCV Stream Pipeline (.venv-yolo Python).
