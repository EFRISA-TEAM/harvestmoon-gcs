# Laporan Sistem Companion Permanen & Otomatis (Orange Pi 3B)

Sistem companion drone pada **Orange Pi 3B** telah diatur secara **permanen, mandiri (fully autonomous), dan tahan gangguan (self-healing)**. Sistem ini akan otomatis berjalan setiap kali Orange Pi dinyalakan (*power-on / reboot*), tanpa memerlukan login SSH manual atau perintah terminal apa pun.

---

## 1. Arsitektur Otomatisasi Permanen

```
┌────────────────────────────────────────────────────────────────────────┐
│                        BOOTING ORANGE PI 3B                            │
│                                                                        │
│  1. Systemd Service Aktif (drone-companion.service)                    │
│  2. Auto-Connect Wi-Fi ("mboh" prioritas 100, cadangan "EAGLE")        │
│  3. Auto-Detect Kamera USB (/dev/v4l/by-id/ -> /dev/video1 @ 720p30)   │
│  4. Auto-Detect Pixhawk (/dev/ttyACM0 @ 115200 bps)                    │
│  5. Dynamic GCS Discovery (Kunci otomatis ke IP Laptop GCS)            │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
           Mengirim Otomatis & Real-Time via Wi-Fi "mboh"
                                    │
                                    ▼
┌────────────────────────────────────────────────────────────────────────┐
│                       LAPTOP GCS (GROUND UNIT)                         │
│                                                                        │
│  • MAVLink UDP 14550 : Telemetri Pixhawk otomatis terhubung            │
│  • Video UDP 5600    : RTP H.264 Livestream siap di QGroundControl     │
│  • Video HTTP 8080   : Livestream /video_feed siap di MoonHarvest      │
│  • Web Browser HUD   : Buka http://10.235.141.127:8080                 │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Rincian Fitur Otomatisasi (Zero-Config)

| Fitur | Mekanisme Kerja | Keunggulan Lapangan |
| :--- | :--- | :--- |
| **Auto-Start Booting** | Terdaftar di Systemd (`drone-companion.service` - Enabled). | Cukup tancapkan baterai drone, sistem langsung aktif dalam ~15 detik. |
| **Auto-Connect Wi-Fi** | NetworkManager autoconnect (`mboh` priority 100, fallback `EAGLE`). | Jika router mati atau drone sempat keluar jangkauan Wi-Fi, akan otomatis tersambung kembali. |
| **Auto-Detect Kamera** | Fungsi `find_working_camera()` melakukan tes pembacaan MJPG frame. | Tidak masalah jika port USB kamera dipindah atau terdeteksi di index berbeda. |
| **Auto-Reconnect Kamera** | Watchdog 30 frame fail mendeteksi frame macet/kabel lepas. | Jika kamera goyang/tercabut saat terbang, kamera langsung diinisialisasi ulang otomatis begitu terhubung kembali. |
| **Auto-Detect Pixhawk** | Scan otomatis `/dev/ttyACM*`, `/dev/ttyUSB*`, dan UART pin GPIO. | Mendukung koneksi kabel USB maupun kabel port TELEM serial secara otomatis. |
| **Dynamic GCS Latching**| MAVLink listener mendeteksi IP pengirim paket laptop secara dinamis. | **Jika IP laptop berubah (misal ganti hotspot), Orange Pi otomatis mengunci IP baru tanpa ubah konfigurasi!** |
| **Pengiriman Video Real-Time** | Dua jalur simultan: UDP RTP H.264 (5600) + HTTP MJPEG (8080). | Cocok untuk QGroundControl, Mission Planner, OpenCV, maupun web browser. |
| **Geotagging Foto Otomatis** | MAVLink `CAMERA_TRIGGER` menangkap foto resolusi penuh + koordinat GPS. | Hasil foto mapping langsung tersimpan rapi di `~/drone_companion/images/`. |

---

## 3. Status Layanan Systemd (Terverifikasi Aktif)

Layanan telah diuji dan berjalan normal di Orange Pi 3B:
```
● drone-companion.service - Orange Pi 3B Autonomous Drone Companion System
     Loaded: loaded (/etc/systemd/system/drone-companion.service; enabled)
     Active: active (running)
```

Perintah pengawasan service jika sewaktu-waktu ingin memeriksa log di Orange Pi:
```bash
# Cek status service
ssh orangepi@10.235.141.127 "sudo systemctl status drone-companion"

# Cek log streaming secara live (stream log)
ssh orangepi@10.235.141.127 "journalctl -u drone-companion -f"
```

---

## 4. Cara Penggunaan di Laptop

Saat drone dinyalakan di lapangan, Anda tinggal:

1. **Buka Browser Laptop**:
   Buka `http://10.235.141.127:8080` untuk melihat video live dan instrumen telemetri secara instan.
2. **Buka QGroundControl / Mission Planner**:
   Pilih koneksi **UDP Port 14550**. Data telemetri drone langsung masuk otomatis. Video UDP ada di port **5600**.
3. **Buka MoonHarvest GCS (Teknofest)**:
   Set sumber video ke `http://10.235.141.127:8080/video_feed`. Model AI vision di laptop akan langsung menganalisis tanaman/penyakit secara real-time.
