#!/usr/bin/env python3
"""
Test Utility: Cek Webcam & Tangkap 1 Foto
"""
import cv2
import sys
import os

CAM_INDEX = int(sys.argv[1]) if len(sys.argv) > 1 else 0

print(f"[*] Mencoba membuka webcam pada index: {CAM_INDEX} (/dev/video{CAM_INDEX})...")
cap = cv2.VideoCapture(CAM_INDEX, cv2.CAP_V4L2)
if not cap.isOpened():
    cap = cv2.VideoCapture(CAM_INDEX)

if not cap.isOpened():
    print(f"[ERROR] Gagal membuka kamera pada index {CAM_INDEX}!")
    print("        Pastikan webcam sudah dicolok ke port USB Orange Pi.")
    sys.exit(1)

ret, frame = cap.read()
if ret:
    h, w, c = frame.shape
    filename = "test_cam.jpg"
    cv2.imwrite(filename, frame)
    print(f"[SUKSES] Kamera Berfungsi Normal!")
    print(f"         Resolusi Terdeteksi: {w} x {h}")
    print(f"         Hasil foto disimpan: {os.path.abspath(filename)}")
else:
    print("[ERROR] Kamera terdeteksi tetapi gagal membaca frame!")

cap.release()
