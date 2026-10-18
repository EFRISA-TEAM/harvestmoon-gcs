# Pre-Flight & Demo Readiness Checklist — MoonHarvest GCS
**PDP Hibah Nasional | Politeknik Elektronika Negeri Surabaya (PENS)**  
Peneliti: Fawwaz Fathin Al Kautsar | Target: Demo UAV Multispectral  
Lokasi Demo: Pabuaran Rice Estate, Subang  
Tanggal terakhir update: Oktober 2026

---

## A. Ground Station (Laptop GCS)

| # | Item | Status | Catatan |
|---|------|--------|---------|
| A1 | Aplikasi MoonHarvest GCS terinstal & dapat dibuka | ☐ | `dotnet run --project HarvestmoonGCS` |
| A2 | Model YOLO `yolo11n_proposal_pdp.onnx` ada di `Assets/models/` | ☐ | 4 kelas: healthy, drought, waterlogged, moderate |
| A3 | Offline map tile (MBTiles) tersedia | ☐ | `~/.local/share/HarvestmoonGCS/offline_maps/` |
| A4 | Demo video tersimpan di `Assets/demo_videos/` | ☐ | Format `.mp4` atau `.webm` |
| A5 | Laptop terhubung ke hotspot / Wi-Fi `mboh` | ☐ | SSID: `mboh`, IP laptop: cek via `ip addr` |
| A6 | Port UDP 14550 tidak diblok firewall | ☐ | `sudo ufw allow 14550/udp` |
| A7 | Port UDP 5600 untuk video stream terbuka | ☐ | `sudo ufw allow 5600/udp` |
| A8 | Fitur Reports & Ground Truth berjalan | ☐ | Tes dari menu Reports |
| A9 | Crop Analysis & Edge Mode berfungsi | ☐ | Tes dengan demo video |
| A10 | Build Release berhasil 0 error | ☐ | `dotnet build -c Release -f net9.0-desktop` |

---

## A.1 Fitur Otomatis Pre-Flight Engine GCS (Real-Time Telemetri)

Dashboard MoonHarvest GCS kini dilengkapi dengan sistem **Pre-Flight Readiness Check** otomatis yang dievaluasi secara dinamis setiap detak telemetri MAVLink (bukan teks statis/dummy):

1. **Telemetry Link (14550)**: Memantau detak denyut nadi paket MAVLink UDP port 14550 secara langsung.
2. **GPS 3D Fix**: Membaca paket MAVLink `GPS_RAW_INT` (memerlukan `fix_type >= 3` dan satelit `≥ 6`).
3. **Baterai UAV**: Mengukur tegangan dan persentase ADC Pixhawk (`battery_remaining > 20%`).
4. **Camera Feed**: Memverifikasi kesiapan RTSP / UDP port 5600 video capture engine.
5. **AI Vision (YOLO11n-cls)**: Memvalidasi ketersediaan session ONNX Runtime (`yolo11n_proposal_pdp.onnx`).
6. **Geofence Active**: Memverifikasi polygon pembatas geofence aktif dan terkunci pada koordinat aman.
7. **Mission Waypoints**: Memastikan rute survei sawah (`Waypoints.Count > 0`) telah terunggah ke autopilot.
8. **Blackbox Armed**: Memastikan blackbox telemetry logger siap merekam flight bundle.

Tombol **"Self-Test Diagnostics"** di samping judul card melakukan audit hardware instan dan mencatat hasilnya langsung ke Notification Log. Status kartu dihitung secara otomatis:
- `STANDBY / NO-LINK`: Menunggu link drone terhubung.
- `PRE-FLIGHT HOLD`: Terdapat item yang belum memenuhi kriteria keselamatan penerbangan.
- `HOLD: FAULT DETECTED`: Ditemukan anomali kritis (misal baterai di bawah 20% atau geofence terlanggar).
- `GO FOR FLIGHT`: Seluruh 8 indikator telah terverifikasi hijau (8/8 Passed).


---

## B. Companion Computer (Orange Pi 3B)

| # | Item | Status | Catatan |
|---|------|--------|---------|
| B1 | Orange Pi menyala & terkoneksi Wi-Fi `mboh` | ☐ | Cek LED biru berkedip |
| B2 | Service `drone-companion.service` running | ☐ | `systemctl status drone-companion` |
| B3 | Pixhawk terdeteksi (`/dev/ttyACM0`) | ☐ | Cek via `ls /dev/ttyACM*` di Orange Pi |
| B4 | Kamera USB terdeteksi (`/dev/video1`) | ☐ | `v4l2-ctl --list-devices` |
| B5 | MAVLink bridge aktif ke UDP 14550 GCS | ☐ | Tunggu ~30 detik setelah boot |
| B6 | Video stream aktif ke UDP 5600 | ☐ | Cek di GCS: Camera tab |
| B7 | IP Orange Pi: `10.235.141.127` terjangkau | ☐ | `ping 10.235.141.127` dari laptop |

---

## C. Airframe & Autopilot (Pixhawk)

| # | Item | Status | Catatan |
|---|------|--------|---------|
| C1 | Firmware ArduPilot terkini terinstal | ☐ | Versi 4.5+ untuk ArduCopter |
| C2 | GPS fix diperoleh (≥6 satelit, HDOP ≤2.0) | ☐ | Tunggu di area terbuka |
| C3 | Kompas terkalibrasi (tanpa interference) | ☐ | Jauh dari besi/magnet |
| C4 | Akselerometer terkalibrasi (leveling) | ☐ | Drone di permukaan datar |
| C5 | Radio failsafe dikonfigurasi | ☐ | RTL jika sinyal hilang >2 detik |
| C6 | Battery 1: charged ≥95% | ☐ | Cek tegangan per cell ≥4.15V |
| C7 | Battery 2 (cadangan): charged ≥95% | ☐ | Bawa minimal 2 baterai |
| C8 | Baling-baling terpasang benar & kencang | ☐ | Putar manual, tidak goyang |
| C9 | Arm test di tanah berhasil (tanpa terbang) | ☐ | Mode Stabilize, throttle bawah |
| C10 | Waypoint missi terkirim ke Pixhawk via GCS | ☐ | Klik "WRITE UAV" di GCS |

---

## D. Kesiapan Demo (PDP Presentation)

| # | Item | Status | Catatan |
|---|------|--------|---------|
| D1 | Slide presentasi siap | ☐ | Sertakan diagram sistem & hasil mAP |
| D2 | Demo scenario ditetapkan (survei petak sawah) | ☐ | Waypoints sudah diprogram |
| D3 | Geofence aktif & radius sesuai area demo | ☐ | Toggle "Geofence Active" di GCS |
| D4 | Sensor tanah (probe) dihubungkan & terbaca | ☐ | Tampil di panel telemetri GCS |
| D5 | Panel Field Monitor menampilkan data real | ☐ | VWC, suhu, RSSI probe |
| D6 | Fitur Ground Truth dicatat sebelum survei | ☐ | Menu Reports → Ground Truth |
| D7 | Dokumentasi (foto/video) sudah disiapkan | ☐ | Untuk luaran PDP |
| D8 | Backup hotspot smartphone aktif | ☐ | Jika Wi-Fi lapangan tidak stabil |
| D9 | Power bank untuk Orange Pi (cadangan) | ☐ | Kapasitas ≥20.000 mAh |
| D10 | Lembar informed consent penonton (jika ada) | ☐ | Standar demo publik |

---

## E. Koneksi & Jaringan

```
Laptop GCS  ←──Wi-Fi "mboh"──→  Orange Pi 3B
                                      │
                                  USB Serial
                                      │
                                  Pixhawk FC
                                      │
                                  MAVLink 115200
```

| Parameter | Nilai |
|-----------|-------|
| Wi-Fi SSID | `mboh` |
| IP Orange Pi | `10.235.141.127` |
| MAVLink UDP | `14550` |
| Video UDP | `5600` |
| HTTP Video | `http://10.235.141.127:8080` |

---

## F. Prosedur Emergency

1. **Kehilangan koneksi GCS → Drone**: Drone auto-RTL setelah 2 detik (failsafe aktif)
2. **Low battery warning**: Landing segera, jangan teruskan misi
3. **Geofence breach**: Drone otomatis hover, kembali ke dalam batas
4. **Crash landing**: Matikan motor via Emergency Stop, laporkan ke PI

---

## G. Kontak Darurat

| Peran | Nama | Kontak |
|-------|------|--------|
| Pilot UAV (Remote PIC) | — | — |
| Ground Observer | — | — |
| Peneliti Utama | Fawwaz Fathin | — |
| Supervisor PI | — | — |

---

*Checklist ini wajib diisi dan ditandatangani pilot sebelum setiap penerbangan.*  
*Dokumen ini bagian dari luaran PDP "Sistem UAV Multispectral untuk Monitoring Irigasi Sawah Berbasis AI"*
