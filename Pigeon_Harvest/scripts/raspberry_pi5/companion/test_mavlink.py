#!/usr/bin/env python3
"""
Test Utility: Cek Koneksi Telemetri Pixhawk Langsung di Terminal
"""
import sys
import os
import time
from pymavlink import mavutil

CANDIDATES = [
    "/dev/ttyACM0",
    "/dev/ttyACM1",
    "/dev/ttyUSB0",
    "/dev/ttyS2",
    "/dev/ttyS0",
]

port = sys.argv[1] if len(sys.argv) > 1 else None

if not port:
    for c in CANDIDATES:
        if os.path.exists(c):
            port = c
            break

if not port:
    print("[ERROR] Tidak menemukan port Pixhawk yang aktif!")
    print("        Pastikan kabel USB atau pin TELEM sudah terhubung ke Orange Pi.")
    sys.exit(1)

baud = int(sys.argv[2]) if len(sys.argv) > 2 else (115200 if "ACM" in port or "USB" in port else 57600)

print(f"[*] Menghubungkan ke Pixhawk pada {port} @ {baud} bps...")
try:
    master = mavutil.mavlink_connection(port, baud=baud)
    print("[*] Menunggu Heartbeat dari Pixhawk (maksimal 10 detik)...")
    hb = master.wait_heartbeat(timeout=10)
    if not hb:
        print("[TIMEOUT] Tidak menerima heartbeat dari Pixhawk!")
        print("          Tips: Periksa baud rate (115200 untuk USB, 57600 untuk TELEM) atau kabel RX/TX.")
        sys.exit(1)

    print("\n=======================================================")
    print(f"[SUKSES] Terhubung ke Pixhawk! (Target System ID: {master.target_system})")
    print("=======================================================")
    print("Membaca data sensor & status drone selama 10 detik...\n")

    start_t = time.time()
    while time.time() - start_t < 10:
        msg = master.recv_match(blocking=True, timeout=1.0)
        if not msg:
            continue
        mtype = msg.get_type()
        if mtype == 'SYS_STATUS':
            volt = msg.voltage_battery / 1000.0
            curr = msg.current_battery / 100.0
            rem = msg.battery_remaining
            print(f"  [BATERAI] Tegangan: {volt:.2f}V | Arus: {curr:.2f}A | Sisa: {rem}%")
        elif mtype == 'GPS_RAW_INT':
            fix = msg.fix_type
            sats = msg.satellites_visible
            lat = msg.lat / 1e7
            lon = msg.lon / 1e7
            print(f"  [GPS] Fix Type: {fix} | Satelit: {sats} | Koordinat: ({lat:.6f}, {lon:.6f})")
        elif mtype == 'ATTITUDE':
            import math
            roll = math.degrees(msg.roll)
            pitch = math.degrees(msg.pitch)
            yaw = math.degrees(msg.yaw)
            print(f"  [ATTITUDE] Roll: {roll:+.1f}° | Pitch: {pitch:+.1f}° | Yaw: {yaw:+.1f}°")
            time.sleep(0.5)

    print("\n[OK] Komunikasi MAVLink dengan Pixhawk berfungsi sempurna!\n")
except Exception as e:
    print(f"[ERROR] Terjadi kesalahan: {e}")
