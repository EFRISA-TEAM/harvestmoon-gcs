#!/usr/bin/env python3
"""
🌾 MoonHarvest Client UAV Stream Runner (Open Interface)
======================================================
Script ini adalah antarmuka terbuka (open-source) yang dapat Anda kustomisasi
sesuai kebutuhan integrasi, kamera UAV, atau sistem Ground Station Anda.

Pipeline Analisis & Engine Keamanan dimuat dari library biner terproteksi:
`moonharvest_core.so` (Linux) / `moonharvest_core.pyd` (Windows).
"""

import argparse
import sys
import time
import json
from pathlib import Path

# Cek keberadaan modul inti MoonHarvest
try:
    import cv2
    import numpy as np
    from moonharvest_core import HarvestVisionEngine, get_hardware_id
except ImportError as e:
    sys.stderr.write(f"[ERROR] Gagal memuat library inti MoonHarvest: {e}\n")
    sys.stderr.write("Pastikan file 'moonharvest_core.so' / 'moonharvest_core.pyd' berada di direktori ini.\n")
    sys.exit(1)


def parse_arguments():
    parser = argparse.ArgumentParser(description="MoonHarvest Client UAV Stream Runner")
    parser.add_argument("--source", type=str, default="0",
                        help="Sumber video (indeks webcam misal 0, path file .mp4, atau URL rtsp://)")
    parser.add_argument("--max-fps", type=float, default=15.0,
                        help="Batas maksimum FPS pemrosesan (default: 15)")
    parser.add_argument("--no-gui", action="store_true",
                        help="Jalankan dalam mode headless tanpa jendela tampilan OpenCV")
    parser.add_argument("--hwid", action="store_true",
                        help="Tampilkan Hardware ID perangkat ini untuk pendaftaran lisensi")
    return parser.parse_args()


def main():
    args = parse_arguments()

    if args.hwid:
        print(f"Perangkat Hardware ID: {get_hardware_id()}")
        return

    print("══════════════════════════════════════════════════════════════════")
    print("  🌾 MoonHarvest UAV Health Monitoring — Client Runner")
    print("  Inisialisasi engine analisis tanaman...")
    print("══════════════════════════════════════════════════════════════════")

    # Inisialisasi engine (Pemeriksaan lisensi akan dieksekusi di sini)
    try:
        engine = HarvestVisionEngine()
    except Exception as e:
        sys.stderr.write(f"\n[FATAL] Gagal memulai engine MoonHarvest: {e}\n")
        sys.exit(1)

    # Membuka input video / kamera
    source = args.source
    if source.isdigit():
        source = int(source)

    cap = cv2.VideoCapture(source)
    if not cap.isOpened():
        sys.stderr.write(f"[ERROR] Tidak dapat membuka sumber video: {args.source}\n")
        sys.exit(1)

    print(f"[✓] Berhasil membuka video source: {args.source}")
    print("[*] Menjalankan pemantauan... Tekan 'q' pada jendela video untuk keluar.\n")

    frame_delay = 1.0 / max(args.max_fps, 1.0)

    try:
        while True:
            t_start = time.time()
            ret, frame = cap.read()
            if not ret:
                print("\n[i] Video stream telah selesai.")
                break

            # 1. Jalankan Analisis melalui Engine Inti Tertutup
            try:
                analysis = engine.analyze_frame(frame)
            except PermissionError as pe:
                sys.stderr.write(f"\n{pe}\n")
                break

            fhi = analysis["fhi"]
            status = analysis["status"]
            counts = analysis["counts"]

            # 2. Output Data Telemetri (Format JSON ke stdout untuk integrasi C# GCS)
            telemetry = {
                "type": "telemetry",
                "fhi": round(fhi, 2),
                "status": status,
                "counts": counts,
                "timestamp": time.time()
            }
            print(json.dumps(telemetry), flush=True)

            # 3. Tampilkan Visual HUD jika tidak headless
            if not args.no_gui:
                annotated = engine.annotate_frame(frame, analysis)
                cv2.imshow("MoonHarvest Client UAV Monitor", annotated)
                if cv2.waitKey(1) & 0xFF == ord('q'):
                    break

            # Kontrol batas FPS
            elapsed = time.time() - t_start
            sleep_time = frame_delay - elapsed
            if sleep_time > 0:
                time.sleep(sleep_time)

    except KeyboardInterrupt:
        print("\n[*] Pemantauan dihentikan oleh pengguna.")
    finally:
        cap.release()
        if not args.no_gui:
            cv2.destroyAllWindows()
        print("[✓] MoonHarvest shutdown selesai.")


if __name__ == "__main__":
    main()
