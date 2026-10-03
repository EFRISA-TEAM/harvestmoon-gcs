#!/usr/bin/env python3
"""
=============================================================================
Orange Pi 3B (Rockchip RK3566) - Autonomous Drone Companion System
=============================================================================
Fitur:
  1. Auto-detect Serial Port Pixhawk (/dev/ttyACM*, /dev/ttyUSB*, /dev/serial/by-id/*)
     dengan auto-baudrate probe (115200 & 57600) dan auto-reconnect.
  2. Auto-detect Kamera USB (v4l2 /dev/video* dan /dev/v4l/by-id/*)
     dengan filter ISP node Rockchip dan auto-recovery jika kabel terlepas.
  3. MAVLink Telemetry Bridge 2-arah (Pixhawk <-> GCS UDP 14550) dengan
     dukungan Subnet Broadcast (SO_BROADCAST) dan Dynamic GCS Latching.
  4. Dual Livestream Video:
     - HTTP MJPEG Server di port 8080 (/video_feed, /snapshot, HUD /)
     - UDP RTP H.264 Streamer di port 5600 (GStreamer mpph264enc / x264enc)
  5. Geotagging & Camera Trigger listener (CAMERA_TRIGGER -> MAPING_xxxx.jpg).
=============================================================================
"""

from __future__ import annotations

import argparse
import glob
import io
import json
import os
import re
import select
import signal
import socket
import subprocess
import sys
import threading
import time
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Optional, Tuple

try:
    import cv2  # type: ignore
except ImportError:
    cv2 = None

try:
    from pymavlink import mavutil  # type: ignore
except ImportError:
    mavutil = None

# ANSI Colors for terminal logs
C_GREEN  = "\033[92m"
C_BLUE   = "\033[94m"
C_YELLOW = "\033[93m"
C_RED    = "\033[91m"
C_CYAN   = "\033[96m"
C_BOLD   = "\033[1m"
C_RESET  = "\033[0m"

running = True


def log(tag: str, msg: str, color: str = C_RESET) -> None:
    ts = datetime.now().strftime("%H:%M:%S")
    print(f"{C_CYAN}[{ts}]{C_RESET} {color}[{tag}]{C_RESET} {msg}", flush=True)


def signal_handler(sig: int, frame: Any) -> None:
    global running
    log("SYSTEM", "Menerima sinyal stop (SIGINT/SIGTERM). Menghentikan layanan...", C_YELLOW)
    running = False


signal.signal(signal.SIGINT, signal_handler)
signal.signal(signal.SIGTERM, signal_handler)


# =============================================================================
# 1. AUTO-DETECT SERIAL /dev/usb & /dev/ttyACM UNTUK MAVLINK
# =============================================================================
def get_candidate_serial_ports() -> list[str]:
    """
    Kumpulkan semua kandidat port serial USB & UART di sistem.
    Prioritas:
      1. /dev/serial/by-id/* (mengidentifikasi Pixhawk / FTDI / CP2102)
      2. /dev/ttyACM* (Pixhawk native USB CDC-ACM)
      3. /dev/ttyUSB* (Radio telemetri SiK / FTDI / CH340 / CP210x)
      4. /dev/ttyS2, /dev/ttyS0 (UART hardware GPIO Orange Pi)
    """
    candidates: list[str] = []

    # 1. Symlink /dev/serial/by-id/
    by_id = sorted(glob.glob("/dev/serial/by-id/*"))
    for p in by_id:
        try:
            real_path = os.path.realpath(p)
            if real_path not in candidates:
                candidates.append(real_path)
        except Exception:
            candidates.append(p)

    # 2. Native USB CDC-ACM (/dev/ttyACM0, /dev/ttyACM1, ...)
    for p in sorted(glob.glob("/dev/ttyACM*")):
        if p not in candidates:
            candidates.append(p)

    # 3. USB-Serial adapters (/dev/ttyUSB0, /dev/ttyUSB1, ...)
    for p in sorted(glob.glob("/dev/ttyUSB*")):
        if p not in candidates:
            candidates.append(p)

    # 4. Orange Pi UART pins
    for p in ["/dev/ttyS2", "/dev/ttyS0", "/dev/ttyS1", "/dev/ttyS3"]:
        if os.path.exists(p) and p not in candidates:
            candidates.append(p)

    return candidates


def probe_mavlink_port(port: str, baudrates: list[int], timeout_s: float = 3.0) -> Optional[Tuple[str, int, Any]]:
    """
    Coba buka port serial dan periksa apakah ada MAVLink heartbeat masuk.
    """
    if mavutil is None:
        log("MAVLINK ERROR", "Modul pymavlink belum terinstall! Jalankan: pip install pymavlink", C_RED)
        return None

    for baud in baudrates:
        conn = None
        try:
            log("SERIAL PROBE", f"Mencoba {port} @ {baud} bps...", C_CYAN)
            conn = mavutil.mavlink_connection(port, baud=baud, timeout=timeout_s)
            hb = conn.wait_heartbeat(timeout=timeout_s)
            if hb:
                sys_id = getattr(conn, "target_system", 1)
                log("SERIAL", f"✓ Pixhawk terdeteksi di {port} @ {baud} bps (System ID: {sys_id})", C_GREEN)
                return (port, baud, conn)
            else:
                conn.close()
        except Exception as e:
            if conn:
                try:
                    conn.close()
                except Exception:
                    pass
            continue

    return None


def detect_pixhawk_connection(requested_port: str = "auto", preferred_baud: int = 0) -> Optional[Tuple[str, int, Any]]:
    """
    Deteksi port dan koneksi Pixhawk. Jika requested_port tertentu, coba itu dulu;
    jika 'auto' atau gagal, scan semua /dev/ttyACM*, /dev/ttyUSB*, dsb.
    """
    if mavutil is None:
        return None

    baudrates_to_try = [115200, 57600, 921600, 38400, 19200, 9600]
    if preferred_baud and preferred_baud > 0:
        baudrates_to_try = [preferred_baud] + [b for b in baudrates_to_try if b != preferred_baud]

    # Jika user meminta port spesifik dan bukan "auto"
    if requested_port and requested_port.lower() != "auto":
        if os.path.exists(requested_port):
            result = probe_mavlink_port(requested_port, baudrates_to_try, timeout_s=4.0)
            if result:
                return result
            # Jika probe heartbeat gagal tetapi port ada, tetap coba buka langsung
            try:
                baud = baudrates_to_try[0]
                conn = mavutil.mavlink_connection(requested_port, baud=baud)
                log("SERIAL", f"Membuka {requested_port} @ {baud} bps (menunggu data nanti)", C_YELLOW)
                return (requested_port, baud, conn)
            except Exception as e:
                log("SERIAL ERROR", f"Gagal membuka {requested_port}: {e}", C_RED)
        else:
            log("SERIAL", f"Port {requested_port} tidak ditemukan, beralih ke scan otomatis...", C_YELLOW)

    # Scan otomatis port serial yang tersedia
    candidates = get_candidate_serial_ports()
    if not candidates:
        return None

    log("SERIAL", f"Kandidat port serial terdeteksi: {candidates}", C_BLUE)
    for port in candidates:
        result = probe_mavlink_port(port, baudrates_to_try, timeout_s=2.5)
        if result:
            return result

    # Jika tidak ada heartbeat seketika, pilih port USB pertama yang ada sebagai fallback
    usb_candidates = [p for p in candidates if "ttyACM" in p or "ttyUSB" in p]
    fallback_port = usb_candidates[0] if usb_candidates else candidates[0]
    fallback_baud = baudrates_to_try[0]
    try:
        conn = mavutil.mavlink_connection(fallback_port, baud=fallback_baud)
        log("SERIAL", f"Fallback membuka port {fallback_port} @ {fallback_baud} bps", C_YELLOW)
        return (fallback_port, fallback_baud, conn)
    except Exception as e:
        log("SERIAL ERROR", f"Gagal fallback ke {fallback_port}: {e}", C_RED)
        return None


# =============================================================================
# 2. AUTO-DETECT KAMERA USB (V4L2)
# =============================================================================
def get_candidate_camera_devices() -> list[str]:
    """
    Cari semua device kamera potensial.
    Filter node ISP/metadata Rockchip (rkisp, cif, metadata, dsb.).
    """
    candidates: list[str] = []

    # 1. Device di /dev/v4l/by-id/ (kamera USB fisik selalu terdaftar di sini)
    for p in sorted(glob.glob("/dev/v4l/by-id/*")):
        try:
            real_path = os.path.realpath(p)
            if real_path not in candidates:
                candidates.append(real_path)
        except Exception:
            candidates.append(p)

    # 2. Cek semua /dev/video*
    for p in sorted(glob.glob("/dev/video*")):
        # Ambil nama device dari sysfs untuk memfilter node metadata Rockchip
        dev_name = os.path.basename(p)
        sys_name_path = f"/sys/class/video4linux/{dev_name}/name"
        name = ""
        if os.path.exists(sys_name_path):
            try:
                with open(sys_name_path, "r", encoding="utf-8") as f:
                    name = f.read().strip().lower()
            except Exception:
                pass

        # Abaikan node internal ISP Rockchip dan Hardware Codec (VPU/RGA)
        if any(skip in name for skip in ["rkisp", "cif", "metadata", "statistics", "input_params", "stream_cif", "vpu", "vepu", "rga"]):
            continue

        if p not in candidates:
            candidates.append(p)

    return candidates


def test_camera_device(dev: str | int, width: int = 1280, height: int = 720, fps: int = 30) -> bool:
    """
    Uji apakah device kamera bisa dibuka dan menghasilkan frame valid.
    """
    if cv2 is None:
        return os.path.exists(dev) if isinstance(dev, str) else True

    cap = None
    try:
        # Coba buka dengan V4L2
        cap = cv2.VideoCapture(dev, cv2.CAP_V4L2)
        if not cap.isOpened():
            cap = cv2.VideoCapture(dev)

        if not cap.isOpened():
            return False

        # Set MJPG codec untuk efisiensi USB bandwidth
        try:
            cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*'MJPG'))
        except Exception:
            pass
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
        cap.set(cv2.CAP_PROP_FPS, fps)

        # Coba baca 1 frame
        for _ in range(3):
            ret, frame = cap.read()
            if ret and frame is not None and frame.size > 0:
                return True
            time.sleep(0.05)
        return False
    except Exception:
        return False
    finally:
        if cap:
            try:
                cap.release()
            except Exception:
                pass


def find_working_camera(requested_cam: str | int = "auto", width: int = 1280, height: int = 720, fps: int = 30) -> Optional[str | int]:
    """
    Temukan kamera yang benar-benar aktif dan menghasilkan frame.
    """
    # Jika user minta spesifik dan bukan "auto"
    if str(requested_cam).lower() != "auto":
        target = int(requested_cam) if str(requested_cam).isdigit() else str(requested_cam)
        log("CAMERA", f"Menguji kamera yang diminta: {target}...", C_BLUE)
        if test_camera_device(target, width, height, fps):
            log("CAMERA", f"✓ Kamera {target} berfungsi dengan baik!", C_GREEN)
            return target
        log("CAMERA", f"Kamera {target} tidak merespons, beralih ke scan otomatis...", C_YELLOW)

    candidates = get_candidate_camera_devices()
    log("CAMERA", f"Memindai device kamera kandidat: {candidates}", C_BLUE)

    for dev in candidates:
        log("CAMERA", f"Menguji device {dev}...", C_CYAN)
        if test_camera_device(dev, width, height, fps):
            log("CAMERA", f"✓ Kamera aktif ditemukan: {dev}", C_GREEN)
            return dev

    # Fallback uji index 0..5
    for idx in range(6):
        if test_camera_device(idx, width, height, fps):
            log("CAMERA", f"✓ Kamera aktif ditemukan di index {idx}", C_GREEN)
            return idx

    return None


# =============================================================================
# 3. CAMERA & DUAL STREAM HANDLER (HTTP MJPEG :8080 + UDP RTP :5600)
# =============================================================================
class CameraStreamHandler:
    def __init__(
        self,
        cam_source: str | int = "auto",
        width: int = 1280,
        height: int = 720,
        fps: int = 30,
        gcs_ip: str = "255.255.255.255",
        video_port: int = 5600,
        http_port: int = 8080,
        enable_udp_stream: bool = True,
    ):
        self.cam_source = cam_source
        self.width = width
        self.height = height
        self.fps = fps
        self.gcs_ip = gcs_ip
        self.video_port = video_port
        self.http_port = http_port
        self.enable_udp_stream = enable_udp_stream

        self.cap: Optional[Any] = None
        self.current_frame: Optional[Any] = None
        self.latest_jpeg: Optional[bytes] = None
        self.stream_proc: Optional[subprocess.Popen] = None
        self.lock = threading.Lock()
        self.img_counter = 1
        self.active_dev: Optional[str | int] = None

        # Telemetri telemetry telemetry snapshot untuk Web HUD
        self.telemetry_data: dict[str, Any] = {
            "connected": False,
            "roll": 0.0,
            "pitch": 0.0,
            "yaw": 0.0,
            "alt": 0.0,
            "speed": 0.0,
            "battery": 0,
            "voltage": 0.0,
            "gps_fix": 0,
            "satellites": 0,
            "lat": 0.0,
            "lon": 0.0,
            "mode": "DISCONNECTED",
        }

        os.makedirs("images", exist_ok=True)

    def update_telemetry(self, key: str, value: Any) -> None:
        with self.lock:
            self.telemetry_data[key] = value

    def update_gcs_ip(self, new_ip: str) -> None:
        if new_ip and new_ip != self.gcs_ip:
            log("STREAM", f"Target IP GCS diperbarui menjadi: {new_ip}", C_BLUE)
            self.gcs_ip = new_ip
            # Restart GStreamer jika target IP berubah
            if self.enable_udp_stream:
                self._restart_streamer()

    def start(self) -> None:
        if cv2 is None:
            log("CAMERA", "PERINGATAN: Modul cv2 tidak ditemukan. Streaming kamera nonaktif.", C_RED)
            return

        # Start HTTP MJPEG Streaming Server di thread latar
        t_http = threading.Thread(target=self._run_http_server, daemon=True)
        t_http.start()

        # Start thread pembaca kamera utama (dengan auto-detect dan watchdog)
        t_cam = threading.Thread(target=self._capture_watchdog_loop, daemon=True)
        t_cam.start()

    def _open_camera(self) -> bool:
        dev = find_working_camera(self.cam_source, self.width, self.height, self.fps)
        if dev is None:
            return False

        self.active_dev = dev
        log("CAMERA", f"Membuka kamera {dev} ({self.width}x{self.height} @ {self.fps}fps)...", C_BLUE)

        cap = cv2.VideoCapture(dev, cv2.CAP_V4L2)
        if not cap.isOpened():
            cap = cv2.VideoCapture(dev)

        if not cap.isOpened():
            log("CAMERA ERROR", f"Gagal membuka device {dev}!", C_RED)
            return False

        try:
            cap.set(cv2.CAP_PROP_FOURCC, cv2.VideoWriter_fourcc(*'MJPG'))
        except Exception:
            pass
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, self.width)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, self.height)
        cap.set(cv2.CAP_PROP_FPS, self.fps)

        self.cap = cap
        log("CAMERA", f"✓ Kamera {dev} BERHASIL dibuka!", C_GREEN)
        return True

    def _start_streamer(self) -> None:
        if not self.enable_udp_stream or not self.gcs_ip:
            return

        # Cek apakah GCS_IP adalah broadcast atau unicast
        log("STREAM", f"Memulai UDP H.264 stream ke {self.gcs_ip}:{self.video_port}...", C_GREEN)

        check_mpp = subprocess.run(["which", "gst-inspect-1.0"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if check_mpp.returncode != 0:
            log("STREAM", "gst-inspect-1.0 tidak ditemukan, streaming UDP dinonaktifkan.", C_YELLOW)
            return

        mpp_test = subprocess.run(["gst-inspect-1.0", "mpph264enc"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if mpp_test.returncode == 0:
            encoder_str = "mpph264enc ! h264parse"
            log("STREAM", "Menggunakan Rockchip Hardware Encoder (mpph264enc)!", C_GREEN)
        else:
            encoder_str = "x264enc tune=zerolatency speed-preset=ultrafast bitrate=1500 ! h264parse"
            log("STREAM", "mpph264enc tidak ditemukan, fallback ke CPU x264enc", C_YELLOW)

        pipeline_cmd = [
            "gst-launch-1.0", "-q",
            "fdsrc", "!",
            f"video/x-raw,format=BGR,width={self.width},height={self.height},framerate={self.fps}/1", "!",
            "videoconvert", "!",
            *encoder_str.split(), "!",
            "rtph264pay", "config-interval=1", "pt=96", "!",
            "udpsink", f"host={self.gcs_ip}", f"port={self.video_port}", "sync=false"
        ]

        try:
            self.stream_proc = subprocess.Popen(pipeline_cmd, stdin=subprocess.PIPE, stderr=subprocess.DEVNULL)
            log("STREAM", f"GStreamer aktif (UDP Port {self.video_port})", C_GREEN)
        except Exception as e:
            log("STREAM ERROR", f"Gagal menjalankan GStreamer: {e}", C_RED)

    def _restart_streamer(self) -> None:
        if self.stream_proc:
            try:
                self.stream_proc.terminate()
                self.stream_proc.wait(timeout=1.0)
            except Exception:
                pass
            self.stream_proc = None
        self._start_streamer()

    def _capture_watchdog_loop(self) -> None:
        global running
        fail_count = 0

        while running:
            if self.cap is None or not self.cap.isOpened():
                log("CAMERA", "Mencari kamera aktif...", C_YELLOW)
                if not self._open_camera():
                    time.sleep(2.0)
                    continue
                # Start GStreamer saat kamera pertama kali terbuka
                if self.enable_udp_stream and self.stream_proc is None:
                    self._start_streamer()

            ret, frame = self.cap.read()
            if ret and frame is not None and frame.size > 0:
                fail_count = 0
                # Encode JPEG kualitas tinggi untuk HTTP MJPEG & snapshot
                ok, jpeg = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), 80])

                with self.lock:
                    self.current_frame = frame
                    if ok:
                        self.latest_jpeg = jpeg.tobytes()

                # Kirim ke stdin GStreamer untuk UDP H.264
                if self.stream_proc and self.stream_proc.stdin:
                    try:
                        self.stream_proc.stdin.write(frame.tobytes())
                    except Exception:
                        pass

                # Delay halus sesuai FPS
                time.sleep(1.0 / max(10, self.fps) * 0.8)
            else:
                fail_count += 1
                if fail_count > 30:
                    log("CAMERA WATCHDOG", "Frame gagal terbaca berulang kali. Inisialisasi ulang kamera...", C_YELLOW)
                    if self.cap:
                        try:
                            self.cap.release()
                        except Exception:
                            pass
                        self.cap = None
                    fail_count = 0
                time.sleep(0.05)

        # Cleanup saat exit
        if self.cap:
            try:
                self.cap.release()
            except Exception:
                pass
        if self.stream_proc:
            try:
                self.stream_proc.terminate()
            except Exception:
                pass

    def capture_trigger(self) -> Optional[str]:
        """
        Ambil foto mapping beresolusi penuh saat menerima sinyal CAMERA_TRIGGER.
        Sertakan metadata geotagging.
        """
        with self.lock:
            frame = self.current_frame.copy() if self.current_frame is not None else None
            telem = dict(self.telemetry_data)

        if frame is not None:
            filename = f"images/MAPING_{self.img_counter:04d}.jpg"
            json_file = f"images/MAPING_{self.img_counter:04d}.json"
            cv2.imwrite(filename, frame)

            # Simpan geotagging JSON
            geotag_info = {
                "filename": filename,
                "timestamp": datetime.now().isoformat(),
                "latitude": telem.get("lat", 0.0),
                "longitude": telem.get("lon", 0.0),
                "altitude_m": telem.get("alt", 0.0),
                "battery_pct": telem.get("battery", 0),
                "yaw_deg": telem.get("yaw", 0.0),
            }
            try:
                with open(json_file, "w", encoding="utf-8") as f:
                    json.dump(geotag_info, f, indent=2)
            except Exception:
                pass

            log("TRIGGER", f"✓ Foto Berhasil Disimpan: {filename} (Lat: {geotag_info['latitude']:.6f}, Lon: {geotag_info['longitude']:.6f})", C_GREEN)
            self.img_counter += 1
            return filename
        else:
            log("TRIGGER ERROR", "Gagal menyimpan foto: frame kamera belum siap!", C_RED)
            return None

    def _run_http_server(self) -> None:
        handler = create_mjpeg_handler(self)
        try:
            server = ThreadingHTTPServer(("0.0.0.0", self.http_port), handler)
            log("HTTP SERVER", f"✓ MJPEG Stream & Web HUD aktif di http://0.0.0.0:{self.http_port}/", C_GREEN)
            log("HTTP SERVER", f"   - MJPEG Stream : http://<ip>:{self.http_port}/video_feed", C_GREEN)
            log("HTTP SERVER", f"   - Snapshot     : http://<ip>:{self.http_port}/snapshot", C_GREEN)
            log("HTTP SERVER", f"   - Status JSON  : http://<ip>:{self.http_port}/status", C_GREEN)
            server.serve_forever()
        except Exception as e:
            log("HTTP ERROR", f"Gagal menjalankan HTTP server di port {self.http_port}: {e}", C_RED)


# =============================================================================
# 4. HTTP MJPEG & WEB HUD HANDLER
# =============================================================================
def create_mjpeg_handler(stream_handler: CameraStreamHandler):
    class MJPEGHandler(BaseHTTPRequestHandler):
        def log_message(self, format: str, *args: Any) -> None:
            # Matikan log request biasa agar tidak mengotori terminal
            pass

        def do_GET(self) -> None:
            # 1. Continuous MJPEG Stream
            if self.path.startswith("/video_feed") or self.path.startswith("/stream") or self.path.startswith("/?action=stream"):
                self.send_response(200)
                self.send_header("Age", "0")
                self.send_header("Cache-Control", "no-cache, private")
                self.send_header("Pragma", "no-cache")
                self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=FRAME")
                self.send_header("Access-Control-Allow-Origin", "*")
                self.end_headers()

                try:
                    while running:
                        jpeg_bytes = None
                        with stream_handler.lock:
                            jpeg_bytes = stream_handler.latest_jpeg

                        if jpeg_bytes:
                            self.wfile.write(b"--FRAME\r\n")
                            self.send_header("Content-Type", "image/jpeg")
                            self.send_header("Content-Length", str(len(jpeg_bytes)))
                            self.end_headers()
                            self.wfile.write(jpeg_bytes)
                            self.wfile.write(b"\r\n")
                        time.sleep(0.033)  # ~30 FPS
                except Exception:
                    pass
                return

            # 2. Single JPEG Snapshot (zero-delay buffer swap)
            if self.path.startswith("/snapshot"):
                jpeg_bytes = None
                with stream_handler.lock:
                    jpeg_bytes = stream_handler.latest_jpeg

                if jpeg_bytes:
                    self.send_response(200)
                    self.send_header("Content-Type", "image/jpeg")
                    self.send_header("Content-Length", str(len(jpeg_bytes)))
                    self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
                    self.send_header("Access-Control-Allow-Origin", "*")
                    self.end_headers()
                    self.wfile.write(jpeg_bytes)
                else:
                    self.send_response(503)
                    self.send_header("Content-Type", "text/plain")
                    self.end_headers()
                    self.wfile.write(b"Camera warming up...")
                return

            # 3. Status JSON Endpoint
            if self.path.startswith("/status"):
                with stream_handler.lock:
                    data = dict(stream_handler.telemetry_data)
                    data["camera_active"] = stream_handler.cap is not None and stream_handler.cap.isOpened()
                    data["camera_device"] = str(stream_handler.active_dev)

                resp_bytes = json.dumps(data).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(resp_bytes)))
                self.send_header("Access-Control-Allow-Origin", "*")
                self.end_headers()
                self.wfile.write(resp_bytes)
                return

            # 4. Trigger Capture via HTTP
            if self.path.startswith("/trigger"):
                saved_file = stream_handler.capture_trigger()
                resp = {"status": "ok" if saved_file else "error", "file": saved_file or ""}
                resp_bytes = json.dumps(resp).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(resp_bytes)
                return

            # 5. Web HUD Dashboard HTML
            html = f"""<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <title>MoonHarvest Drone HUD - Orange Pi 3B</title>
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0f172a; color: #f8fafc; margin: 0; padding: 20px; }}
        .header {{ display: flex; justify-content: space-between; align-items: center; border-bottom: 2px solid #334155; padding-bottom: 12px; margin-bottom: 20px; }}
        .hud-grid {{ display: grid; grid-template-columns: 2fr 1fr; gap: 20px; }}
        @media(max-width: 800px) {{ .hud-grid {{ grid-template-columns: 1fr; }} }}
        .video-box {{ background: #000; border-radius: 12px; overflow: hidden; position: relative; border: 1px solid #334155; }}
        .video-box img {{ width: 100%; height: auto; display: block; }}
        .card {{ background: #1e293b; border-radius: 12px; padding: 16px; border: 1px solid #334155; margin-bottom: 16px; }}
        .card h3 {{ margin-top: 0; color: #38bdf8; font-size: 14px; text-transform: uppercase; letter-spacing: 1px; }}
        .stat-row {{ display: flex; justify-content: space-between; padding: 8px 0; border-bottom: 1px solid #334155; }}
        .stat-val {{ font-weight: bold; color: #f1f5f9; }}
        .badge {{ display: inline-block; padding: 4px 10px; border-radius: 20px; font-size: 12px; font-weight: bold; }}
        .badge-green {{ background: #15803d; color: #fff; }}
        .badge-blue {{ background: #0369a1; color: #fff; }}
        .btn {{ background: #0284c7; color: white; border: none; padding: 10px 16px; border-radius: 8px; font-weight: bold; cursor: pointer; }}
        .btn:hover {{ background: #0369a1; }}
    </style>
</head>
<body>
    <div class="header">
        <h2>🛸 MoonHarvest Drone Companion (Orange Pi 3B)</h2>
        <div>
            <span class="badge badge-green" id="connBadge">MAVLink UDP 14550</span>
            <span class="badge badge-blue">MJPEG 8080</span>
        </div>
    </div>
    <div class="hud-grid">
        <div class="video-box">
            <img src="/video_feed" alt="Live Stream" id="videoFeed">
        </div>
        <div>
            <div class="card">
                <h3>Instrumen Terbang</h3>
                <div class="stat-row"><span>Roll / Pitch</span><span class="stat-val" id="valAttitude">0.0° / 0.0°</span></div>
                <div class="stat-row"><span>Yaw / Heading</span><span class="stat-val" id="valYaw">0.0°</span></div>
                <div class="stat-row"><span>Altitude (MSL)</span><span class="stat-val" id="valAlt">0.0 m</span></div>
                <div class="stat-row"><span>Ground Speed</span><span class="stat-val" id="valSpeed">0.0 m/s</span></div>
            </div>
            <div class="card">
                <h3>GPS &amp; Baterai</h3>
                <div class="stat-row"><span>Status GPS</span><span class="stat-val" id="valGps">Mencari Satelit...</span></div>
                <div class="stat-row"><span>Koordinat</span><span class="stat-val" id="valCoords">0.000000, 0.000000</span></div>
                <div class="stat-row"><span>Baterai</span><span class="stat-val" id="valBat">0% (0.0 V)</span></div>
                <div class="stat-row"><span>Mode Wahana</span><span class="stat-val" id="valMode">DISCONNECTED</span></div>
            </div>
            <div class="card">
                <h3>Aksi Cepat</h3>
                <button class="btn" onclick="fetch('/trigger').then(r=>r.json()).then(d=>alert('Foto: '+d.file))">📸 Ambil Foto Mapping</button>
            </div>
        </div>
    </div>
    <script>
        setInterval(() => {{
            fetch('/status').then(r => r.json()).then(d => {{
                document.getElementById('valAttitude').textContent = d.roll.toFixed(1) + '° / ' + d.pitch.toFixed(1) + '°';
                document.getElementById('valYaw').textContent = d.yaw.toFixed(1) + '°';
                document.getElementById('valAlt').textContent = d.alt.toFixed(1) + ' m';
                document.getElementById('valSpeed').textContent = d.speed.toFixed(1) + ' m/s';
                document.getElementById('valGps').textContent = 'Fix ' + d.gps_fix + ' (' + d.satellites + ' Sat)';
                document.getElementById('valCoords').textContent = d.lat.toFixed(6) + ', ' + d.lon.toFixed(6);
                document.getElementById('valBat').textContent = d.battery + '% (' + d.voltage.toFixed(1) + ' V)';
                document.getElementById('valMode').textContent = d.mode;
            }}).catch(() => {{}});
        }}, 500);
    </script>
</body>
</html>
"""
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(html.encode("utf-8"))))
            self.end_headers()
            self.wfile.write(html.encode("utf-8"))

    return MJPEGHandler


# =============================================================================
# 5. TELEMETRY MAVLINK BRIDGE & DYNAMIC GCS LATCHING
# =============================================================================
class TelemetryTriggerBridge:
    def __init__(
        self,
        serial_port: str = "auto",
        baudrate: int = 0,
        gcs_ip: str = "255.255.255.255",
        gcs_port: int = 14550,
        cam_handler: Optional[CameraStreamHandler] = None,
    ):
        self.requested_port = serial_port
        self.requested_baud = baudrate
        self.gcs_ip = gcs_ip
        self.gcs_port = gcs_port
        self.cam_handler = cam_handler

        self.master: Optional[Any] = None
        self.udp_sock: Optional[socket.socket] = None
        self.gcs_addr = (self.gcs_ip, self.gcs_port)
        self.is_latched = False

    def start(self) -> None:
        # Inisialisasi UDP Socket untuk GCS
        self.udp_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        # Aktifkan broadcast agar pengiriman ke alamat broadcast subnet berhasil tanpa error
        try:
            self.udp_sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        except Exception:
            pass

        self.udp_sock.bind(("0.0.0.0", self.gcs_port))
        self.udp_sock.setblocking(False)

        log("MAVLINK", f"Mendengarkan MAVLink UDP di 0.0.0.0:{self.gcs_port}", C_BLUE)
        log("MAVLINK", f"Target Telemetri Awal : {self.gcs_ip}:{self.gcs_port}", C_BLUE)

        # Thread untuk membaca dari GCS dan meneruskan ke Pixhawk (serta dynamic latching)
        t_gcs_to_pixhawk = threading.Thread(target=self._worker_gcs_to_pixhawk, daemon=True)
        t_gcs_to_pixhawk.start()

        # Loop utama membaca dari Pixhawk dan meneruskan ke GCS
        self._worker_pixhawk_to_gcs()

    def _connect_pixhawk(self) -> bool:
        global running
        while running:
            result = detect_pixhawk_connection(self.requested_port, self.requested_baud)
            if not result:
                log("MAVLINK", "Menunggu kabel Pixhawk terhubung ke USB (/dev/ttyACM* atau /dev/ttyUSB*)...", C_YELLOW)
                time.sleep(2.0)
                continue

            port, baud, conn = result
            self.master = conn
            log("MAVLINK", f"✓ Terhubung ke Pixhawk di {port} @ {baud} bps!", C_GREEN)
            if self.cam_handler:
                self.cam_handler.update_telemetry("connected", True)
            return True
        return False

    def _worker_pixhawk_to_gcs(self) -> None:
        global running
        while running:
            if not self.master:
                if not self._connect_pixhawk():
                    break

            try:
                msg = self.master.recv_match(blocking=False)
                if msg:
                    # 1. Forward raw buffer ke GCS
                    msg_buf = msg.get_msgbuf()
                    if msg_buf and self.udp_sock:
                        try:
                            self.udp_sock.sendto(msg_buf, self.gcs_addr)
                        except Exception as se:
                            pass

                    # 2. Parse data untuk Web HUD & Geotagging
                    msg_type = msg.get_type()

                    if msg_type == 'ATTITUDE':
                        import math
                        roll_deg = math.degrees(msg.roll)
                        pitch_deg = math.degrees(msg.pitch)
                        yaw_deg = math.degrees(msg.yaw)
                        if self.cam_handler:
                            self.cam_handler.update_telemetry("roll", roll_deg)
                            self.cam_handler.update_telemetry("pitch", pitch_deg)
                            self.cam_handler.update_telemetry("yaw", yaw_deg)

                    elif msg_type == 'GLOBAL_POSITION_INT':
                        lat = msg.lat / 1e7
                        lon = msg.lon / 1e7
                        alt = msg.relative_alt / 1000.0
                        vx, vy = msg.vx / 100.0, msg.vy / 100.0
                        speed = (vx*vx + vy*vy) ** 0.5
                        if self.cam_handler:
                            self.cam_handler.update_telemetry("lat", lat)
                            self.cam_handler.update_telemetry("lon", lon)
                            self.cam_handler.update_telemetry("alt", alt)
                            self.cam_handler.update_telemetry("speed", speed)

                    elif msg_type == 'SYS_STATUS':
                        v_bat = msg.voltage_battery / 1000.0
                        bat_pct = msg.battery_remaining
                        if self.cam_handler:
                            self.cam_handler.update_telemetry("voltage", v_bat)
                            self.cam_handler.update_telemetry("battery", bat_pct)

                    elif msg_type == 'GPS_RAW_INT':
                        if self.cam_handler:
                            self.cam_handler.update_telemetry("gps_fix", msg.fix_type)
                            self.cam_handler.update_telemetry("satellites", msg.satellites_visible)

                    elif msg_type == 'HEARTBEAT':
                        mode = mavutil.mode_string_v10(msg)
                        if self.cam_handler:
                            self.cam_handler.update_telemetry("mode", mode)

                    # 3. Intersep pesan Trigger Kamera untuk pemotretan otomatis
                    if msg_type in ['CAMERA_TRIGGER', 'CAMERA_FEEDBACK'] or \
                       (msg_type == 'COMMAND_LONG' and getattr(msg, 'command', 0) == getattr(mavutil.mavlink, 'MAV_CMD_DO_DIGICAM_CONTROL', 203)):
                        log("TRIGGER", f"Sinyal Trigger Diterima dari MAVLink ({msg_type})!", C_GREEN)
                        if self.cam_handler:
                            self.cam_handler.capture_trigger()
                else:
                    time.sleep(0.002)
            except Exception as e:
                log("MAVLINK ERROR", f"Komunikasi serial Pixhawk terputus: {e}", C_RED)
                if self.master:
                    try:
                        self.master.close()
                    except Exception:
                        pass
                self.master = None
                if self.cam_handler:
                    self.cam_handler.update_telemetry("connected", False)
                time.sleep(1.0)

    def _worker_gcs_to_pixhawk(self) -> None:
        global running
        while running:
            try:
                if not self.udp_sock:
                    time.sleep(0.1)
                    continue

                r, _, _ = select.select([self.udp_sock], [], [], 0.1)
                if r:
                    data, addr = self.udp_sock.recvfrom(4096)
                    if data:
                        # Dynamic GCS Discovery: Latch IP & Port pengirim GCS
                        if addr != self.gcs_addr:
                            self.gcs_addr = addr
                            if not self.is_latched or self.gcs_ip != addr[0]:
                                self.is_latched = True
                                log("DYNAMIC GCS", f"✓ Mengunci target GCS ke: {addr[0]}:{addr[1]}", C_GREEN)
                                if self.cam_handler:
                                    self.cam_handler.update_gcs_ip(addr[0])

                        # Teruskan data GCS (Heartbeat/Mission/Command) ke Pixhawk
                        if self.master:
                            self.master.write(data)
            except Exception:
                time.sleep(0.02)


# =============================================================================
# 6. MAIN LAUNCHER
# =============================================================================
def main() -> None:
    parser = argparse.ArgumentParser(description="Orange Pi 3B Autonomous Drone Companion System")
    parser.add_argument("--gcs-ip", default="auto", help="Alamat IP Laptop GCS atau 'auto' untuk broadcast subnet")
    parser.add_argument("--pixhawk-port", default="auto", help="Port Serial Pixhawk (/dev/ttyACM0, /dev/ttyUSB0, atau 'auto')")
    parser.add_argument("--baud", type=int, default=0, help="Baudrate Pixhawk (0 = auto-probe 115200 & 57600)")
    parser.add_argument("--gcs-port", type=int, default=14550, help="Port Telemetri MAVLink UDP (default: 14550)")
    parser.add_argument("--video-port", type=int, default=5600, help="Port Livestream Video UDP H.264 (default: 5600)")
    parser.add_argument("--http-port", type=int, default=8080, help="Port HTTP MJPEG Streamer & HUD (default: 8080)")
    parser.add_argument("--cam", default="auto", help="Device Kamera ('auto', 0, 1, atau /dev/video*)")
    parser.add_argument("--width", type=int, default=1280, help="Resolusi Lebar Kamera (default: 1280)")
    parser.add_argument("--height", type=int, default=720, help="Resolusi Tinggi Kamera (default: 720)")
    parser.add_argument("--fps", type=int, default=30, help="Framerate Video (default: 30)")
    parser.add_argument("--no-udp-stream", action="store_true", help="Nonaktifkan streaming video UDP (HTTP MJPEG tetap aktif)")

    args = parser.parse_args()

    # Tentukan Target GCS jika 'auto'
    target_gcs = args.gcs_ip
    if target_gcs == "auto" or not target_gcs:
        target_gcs = "255.255.255.255"

    print(f"""{C_CYAN}
=============================================================================
   ORANGE PI 3B - AUTONOMOUS DRONE COMPANION SYSTEM
=============================================================================
{C_RESET}""")
    log("CONFIG", f"GCS Target IP     : {target_gcs} (Dynamic Latching Aktif)", C_BLUE)
    log("CONFIG", f"MAVLink UDP Port  : 0.0.0.0:{args.gcs_port} (Bi-directional)", C_BLUE)
    log("CONFIG", f"Video HTTP Port   : http://0.0.0.0:{args.http_port}/ (MJPEG + HUD)", C_BLUE)
    log("CONFIG", f"Video UDP Port    : UDP {args.video_port} (H.264 RTP, enabled={not args.no_udp_stream})", C_BLUE)
    log("CONFIG", f"Pixhawk Port      : {args.pixhawk_port} (Baud: {'auto' if args.baud == 0 else args.baud})", C_BLUE)
    log("CONFIG", f"Kamera USB        : {args.cam} ({args.width}x{args.height} @ {args.fps}fps)", C_BLUE)
    print("-----------------------------------------------------------------------------")

    # Inisialisasi Kamera & Dual Streamer
    cam_handler = CameraStreamHandler(
        cam_source=args.cam,
        width=args.width,
        height=args.height,
        fps=args.fps,
        gcs_ip=target_gcs,
        video_port=args.video_port,
        http_port=args.http_port,
        enable_udp_stream=not args.no_udp_stream,
    )
    cam_handler.start()

    # Inisialisasi Telemetry Bridge
    bridge = TelemetryTriggerBridge(
        serial_port=args.pixhawk_port,
        baudrate=args.baud,
        gcs_ip=target_gcs,
        gcs_port=args.gcs_port,
        cam_handler=cam_handler,
    )
    bridge.start()


if __name__ == "__main__":
    main()
