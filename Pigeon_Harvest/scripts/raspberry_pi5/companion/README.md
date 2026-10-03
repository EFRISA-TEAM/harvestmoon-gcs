# Drone Companion System (Orange Pi 3B)

Paket ini dirancang khusus untuk Orange Pi 3B (Rockchip RK3566) yang berfungsi sebagai companion computer di wahana drone/UAV.

## Fitur Utama:
1. **Bridge Telemetri Dua Arah (Bi-directional MAVLink Router)**:
   - Membaca data MAVLink dari Pixhawk dan meneruskannya ke Laptop GCS (UDP `14550`).
   - Menerima perintah/waypoint dari Mission Planner / QGroundControl dan mengirimkannya kembali ke Pixhawk.
   - Mendeteksi port Pixhawk secara otomatis (`/dev/ttyACM0`, `/dev/ttyUSB0`, `/dev/ttyS2`).
2. **Camera Trigger Otomatis**:
   - Mendengarkan sinyal `CAMERA_TRIGGER`, `CAMERA_FEEDBACK`, atau `MAV_CMD_DO_DIGICAM_CONTROL`.
   - Mengambil frame dari webcam dan menyimpannya di folder `images/MAPING_XXXX.jpg`.
3. **Livestream Video H.264 (Port 5600)**:
   - Mengirim stream video berlatensi rendah ke laptop GCS menggunakan hardware acceleration RK3566 (`mpph264enc`).

---

## Cara Penggunaan:

### 1. Sesuaikan IP Laptop GCS
Buka file `start_drone.sh` dan ubah alamat IP `GCS_IP` sesuai IP laptop Anda:
```bash
nano start_drone.sh
# Ubah: GCS_IP="192.168.1.100" (ganti dengan IP laptop Anda)
```

### 2. Jalankan Program Secara Manual
```bash
./start_drone.sh
```

### 3. Koneksi di Laptop GCS:
- **Mission Planner**:
  - Pilih koneksi **UDP** (Port: `14550`).
  - Klik **Connect**. Telemetri akan langsung terhubung!
- **QGroundControl**:
  - Otomatis langsung membaca telemetri di UDP `14550`.
  - Masuk ke *Settings* -> *General* -> *Video Stream*: Ubah ke **UDP H.264 Video Stream** (Port default `5600`). Video langsung tampil di layar!

### 4. Menjalankan Otomatis Saat Booting (Opsional)
Jika ingin program otomatis menyala ketika drone dihidupkan:
```bash
sudo ./install_service.sh
```
