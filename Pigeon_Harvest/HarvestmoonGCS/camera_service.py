#!/usr/bin/env python3
"""
Simple camera bridge for HarvestmoonGCS desktop.

Commands:
  list
  stream <source>
  picture <source> <output_file>
"""

from __future__ import annotations

import base64
import glob
import json
import os
import platform
import re
import sys
import threading
import time
from typing import Any

try:
    import cv2  # type: ignore
except Exception:
    cv2 = None

# Network discovery for auto-finding Orange Pi
try:
    from network_discovery import discover_orange_pi, get_network_info
    HAS_NETWORK_DISCOVERY = True
except ImportError:
    HAS_NETWORK_DISCOVERY = False


LOCAL_CAMERA_TYPE = 0
NETWORK_STREAM_TYPE = 1

# Orange Pi 3B drone companion (drone_telemetry_stream.py) mengirim video sebagai
# RTP/H.264 lewat UDP: mpph264enc ! h264parse ! rtph264pay pt=96 ! udpsink
DRONE_COMPANION_VIDEO_PORT = 5600
DRONE_COMPANION_SOURCE_ID = "udp-h264-drone"


def build_udp_h264_pipeline(port: int = DRONE_COMPANION_VIDEO_PORT) -> str:
    """
    Pipeline GStreamer untuk menerima H.264 RTP/UDP dari Orange Pi drone companion.

    Diperlukan karena stream-nya RTP (bukan MJPEG HTTP), sehingga udp:// biasa
    tidak bisa men-decode. OpenCV dipanggil dengan backend CAP_GSTREAMER.

    CATATAN PENTING: `timeout` (nanodetik) pada udpsrc WAJIB ada. Tanpanya,
    pipeline akan menggantung selamanya bila Orange Pi belum mengirim video —
    ini membuat halaman Camera membeku.
    """
    return (
        f"udpsrc port={port} timeout=5000000000 "
        'caps="application/x-rtp,media=(string)video,clock-rate=(int)90000,'
        'encoding-name=(string)H264,payload=(int)96" ! '
        "rtph264depay ! h264parse ! avdec_h264 ! videoconvert ! "
        "video/x-raw,format=BGR ! appsink drop=true max-buffers=2 sync=false"
    )


def emit(payload: dict[str, Any]) -> None:
    print(json.dumps(payload), flush=True)

def emit_error(message: str) -> int:
    emit({"error": message})
    return 1


def parse_index_from_device(path: str) -> int | None:
    match = re.search(r"video(\d+)$", path)
    if match is None:
        return None
    return int(match.group(1))


def open_capture(source: str, open_timeout_ms: int = 2000):
    if cv2 is None:
        return None

    if source.isdigit():
        index = int(source)
        if platform.system().lower().startswith("win") and hasattr(cv2, "CAP_DSHOW"):
            return cv2.VideoCapture(index, cv2.CAP_DSHOW)
        return cv2.VideoCapture(index)

    # Handle placeholder source IDs ('network', 'auto', etc.) by resolving to discovered stream or UDP
    if source in ("network", "auto", "drone", "drone-companion", "orange-pi"):
        discovered_url = None
        if HAS_NETWORK_DISCOVERY:
            try:
                import network_discovery as nd
                discovered_url = nd.discover_orange_pi()
            except Exception:
                pass
        if not discovered_url:
            fallback_urls = [
                "http://10.235.141.127:8080/video_feed",
                "http://192.168.1.100:8080/video_feed",
                "http://192.168.0.100:8080/video_feed",
                "http://10.0.0.100:8080/video_feed",
            ]
            discovered_url = _probe_mjpeg_urls_parallel(fallback_urls, per_url_timeout=2.0)

        if discovered_url:
            source = discovered_url
        else:
            source = DRONE_COMPANION_SOURCE_ID

    # Drone companion UDP H.264 (Orange Pi) — butuh pipeline GStreamer eksplisit.
    # Format id: "udp-h264-drone" atau "udp-h264-drone:5600".
    if source == DRONE_COMPANION_SOURCE_ID or source.startswith("udp-h264"):
        port = DRONE_COMPANION_VIDEO_PORT
        match = re.search(r":(\d+)$", source)
        if match:
            port = int(match.group(1))

        # Buka di thread terpisah dengan batas waktu: pipeline udpsrc akan
        # menggantung sampai ada paket masuk, dan kita tidak boleh memblokir UI.
        result: dict[str, Any] = {}

        def _open() -> None:
            try:
                pipeline = build_udp_h264_pipeline(port)
                if hasattr(cv2, "CAP_GSTREAMER"):
                    cap = cv2.VideoCapture(pipeline, cv2.CAP_GSTREAMER)
                    if cap is not None and cap.isOpened():
                        result["cap"] = cap
                        return
                    if cap is not None:
                        cap.release()
                result["cap"] = cv2.VideoCapture(f"udp://0.0.0.0:{port}")
            except Exception:
                result["cap"] = None

        thread = threading.Thread(target=_open, daemon=True)
        thread.start()
        thread.join(timeout=3.0)

        cap = result.get("cap")
        if cap is None:
            # Belum ada video masuk (Orange Pi belum streaming) — biarkan thread
            # latar melanjutkan; panggilan berikutnya akan memakai capture yang siap.
            return None
        return cap

    # Sumber jaringan biasa: pakai timeout buka/baca supaya tidak menggantung
    # lama saat host tidak ada (penyebab utama `list` lambat).
    cap = cv2.VideoCapture()
    try:
        if hasattr(cv2, "CAP_PROP_OPEN_TIMEOUT_MSEC"):
            cap.set(cv2.CAP_PROP_OPEN_TIMEOUT_MSEC, float(open_timeout_ms))
        if hasattr(cv2, "CAP_PROP_READ_TIMEOUT_MSEC"):
            cap.set(cv2.CAP_PROP_READ_TIMEOUT_MSEC, float(open_timeout_ms))
    except Exception:
        pass
    cap.open(source)
    return cap


def _probe_one_mjpeg(url: str, timeout_s: float) -> bool:
    """
    Buka URL dan cek apakah ada frame.

    OpenCV 4.6 mengabaikan CAP_PROP_OPEN_TIMEOUT_MSEC untuk HTTP, sehingga
    membuka host yang mati bisa menggantung sangat lama. Karena itu kita
    pre-check port TCP dulu dengan socket ber-timeout — jauh lebih cepat.
    """
    try:
        import socket as _socket
        from urllib.parse import urlparse

        parsed = urlparse(url)
        host = parsed.hostname
        port = parsed.port or 80
        if not host:
            return False

        with _socket.socket(_socket.AF_INET, _socket.SOCK_STREAM) as s:
            s.settimeout(timeout_s)
            if s.connect_ex((host, port)) != 0:
                return False

        cap = open_capture(url, open_timeout_ms=int(timeout_s * 1000))
        if cap is None:
            return False
        try:
            if not cap.isOpened():
                return False
            ok, frame = cap.read()
            return bool(ok and frame is not None)
        finally:
            cap.release()
    except Exception:
        return False


def _probe_mjpeg_urls_parallel(urls: list[str], per_url_timeout: float = 3.0) -> str | None:
    """
    Cek beberapa URL MJPEG sekaligus (paralel), kembalikan yang pertama hidup.

    Probe sekuensial membuat `list` (dipakai UI) menggantung puluhan detik saat
    host tidak ada; versi paralel + timeout keras ini menyelesaikannya cepat.
    """
    if cv2 is None or not urls:
        return None
    try:
        from concurrent.futures import ThreadPoolExecutor, as_completed

        with ThreadPoolExecutor(max_workers=min(len(urls), 6)) as pool:
            futures = {pool.submit(_probe_one_mjpeg, u, per_url_timeout): u for u in urls}
            try:
                for fut in as_completed(futures, timeout=per_url_timeout + 2.0):
                    try:
                        if fut.result():
                            return futures[fut]
                    except Exception:
                        continue
            except Exception:
                pass
    except Exception:
        pass
    return None


def detect_sources() -> list[dict[str, Any]]:
    sources: list[dict[str, Any]] = []

    candidates: list[int] = []
    for device_path in sorted(glob.glob("/dev/video*")):
        index = parse_index_from_device(device_path)
        if index is not None:
            candidates.append(index)

    if not candidates:
        candidates = list(range(0, 6))

    tested: set[int] = set()
    for index in candidates:
        if index in tested:
            continue
        tested.add(index)

        if cv2 is None:
            # cv2 not available, still expose detected device ids for Linux
            if os.path.exists(f"/dev/video{index}"):
                sources.append(
                    {
                        "id": str(index),
                        "name": f"Camera {index}",
                        "description": f"Local Camera {index}",
                        "type": LOCAL_CAMERA_TYPE,
                        "isAvailable": True,
                    }
                )
            continue

        cap = open_capture(str(index))
        if cap is None:
            continue

        try:
            if not cap.isOpened():
                continue
            ok, frame = cap.read()
            if not ok or frame is None:
                continue

            width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
            height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
            fps = int(cap.get(cv2.CAP_PROP_FPS) or 0)
            fps_suffix = f" @ {fps}fps" if fps > 0 else ""

            sources.append(
                {
                    "id": str(index),
                    "name": f"Camera {index}",
                    "description": f"Local Camera {index} ({width}x{height}{fps_suffix})",
                    "type": LOCAL_CAMERA_TYPE,
                    "isAvailable": True,
                }
            )
        finally:
            cap.release()

    # Auto-discover Orange Pi using network discovery module.
    # Panggil TANPA override timeout: nilai default modul sudah dioptimasi
    # (0.25s/host, paralel 128) — override yang besar membuat `list` lambat.
    orange_pi_url = None
    try:
        if HAS_NETWORK_DISCOVERY:
            import network_discovery as nd
            orange_pi_url = nd.discover_orange_pi()
    except Exception:
        pass

    # Fallback: probe IP umum SECARA PARALEL dengan timeout keras.
    # Penting: probing sekuensial membuat `list` (dipakai UI) menggantung
    # sampai puluhan detik saat host tidak ada.
    if not orange_pi_url and cv2 is not None:
        fallback_urls = [
            "http://10.235.141.127:8080/video_feed",
            "http://192.168.1.100:8080/video_feed",
            "http://192.168.0.100:8080/video_feed",
            "http://10.0.0.100:8080/video_feed",
            "http://192.168.1.10:8080/video_feed",
            "http://192.168.0.10:8080/video_feed",
        ]
        orange_pi_url = _probe_mjpeg_urls_parallel(fallback_urls, per_url_timeout=3.0)
    
    if orange_pi_url:
        sources.append(
            {
                "id": orange_pi_url,
                "name": "🚁 Drone Camera (Orange Pi)",
                "description": f"Live MJPEG Stream — auto-discovered via {orange_pi_url}",
                "type": NETWORK_STREAM_TYPE,
                "isAvailable": True,
            }
        )
    else:
        sources.append(
            {
                "id": "network",
                "name": "🚁 Drone Camera (Orange Pi - Auto)",
                "description": "Orange Pi not found. Will auto-discover on next connection. Common IPs: 192.168.1.100, 10.235.141.127",
                "type": NETWORK_STREAM_TYPE,
                "isAvailable": True,
            }
        )

    # Drone companion video: Orange Pi 3B mengirim RTP/H.264 ke UDP 5600.
    # Tidak perlu tahu IP Orange Pi — GCS hanya mendengarkan port tersebut.
    sources.append(
        {
            "id": DRONE_COMPANION_SOURCE_ID,
            "name": f"🚁 Drone Companion (UDP H.264 :{DRONE_COMPANION_VIDEO_PORT})",
            "description": (
                f"Video H.264 langsung dari Orange Pi drone companion (UDP {DRONE_COMPANION_VIDEO_PORT}). "
                "Jalankan start_drone.sh di Orange Pi dengan GCS_IP = IP laptop ini."
            ),
            "type": NETWORK_STREAM_TYPE,
            "isAvailable": True,
        }
    )

    # Also add network discovery info
    # Catatan: jangan panggil get_network_info() di sini — fungsi itu memanggil
    # discover_orange_pi() lagi (scan ulang seluruh subnet), sehingga `list`
    # jadi dua kali lebih lambat. Ambil datanya langsung tanpa scan.
    try:
        if HAS_NETWORK_DISCOVERY:
            import network_discovery as nd
            local_ip = nd.get_local_ip()
            subnets = nd.get_local_ipv4_networks()
            gateway = nd.get_gateway_ip()
            sources.append(
                {
                    "id": "discovery_info",
                    "name": "📶 Network Info",
                    "description": (
                        f"Local IP: {local_ip} | Subnets: {', '.join(str(s) for s in subnets)} "
                        f"| Gateway: {gateway}"
                    ),
                    "type": NETWORK_STREAM_TYPE,
                    "isAvailable": True,
                }
            )
    except Exception:
        pass

    sources.append(
        {
            "id": "network",
            "name": "Network Stream (RTSP/HTTP)",
            "description": "Enter custom network stream URL (e.g. http://192.168.1.100:8080/video_feed)",
            "type": NETWORK_STREAM_TYPE,
            "isAvailable": True,
        }
    )
    return sources


def cmd_list() -> int:
    print(json.dumps(detect_sources()))
    return 0


def cmd_stream(source: str) -> int:
    if cv2 is None:
        return emit_error("opencv-python (cv2) is not installed")

    # Determine if source is a network stream (HTTP/RTSP/UDP, termasuk drone companion)
    is_network = (
        (not source.isdigit() and (source.startswith(("http://", "https://", "rtsp://", "udp://")) or source in ("network", "auto", "drone", "drone-companion", "orange-pi")))
        or source == DRONE_COMPANION_SOURCE_ID
        or source.startswith("udp-h264")
    )

    # Retry logic for network streams
    cap = None
    max_retries = 3 if is_network else 1
    for attempt in range(max_retries):
        cap = open_capture(source)
        if cap is not None and cap.isOpened():
            break
        if attempt < max_retries - 1:
            time.sleep(1.0)  # Wait before retry
    
    if cap is None or not cap.isOpened():
        return emit_error(f"Failed to open camera source: {source}. Check network connection to Orange Pi.")

    is_file_source = os.path.isfile(source)
    src_fps = cap.get(cv2.CAP_PROP_FPS) or 15
    if is_file_source:
        frame_delay = (1.0 / max(1.0, src_fps)) * 1.10
    else:
        # Keep live cameras/network streams capped for UI stability.
        frame_delay = 1.0 / max(1.0, min(15.0, src_fps))

    eof_retries = 0
    try:
        while True:
            ok, frame = cap.read()
            if not ok or frame is None:
                if is_file_source:
                    eof_retries += 1
                    cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
                    if eof_retries > 3:
                        try:
                            cap.release()
                            time.sleep(0.05)
                            cap = cv2.VideoCapture(source)
                        except Exception:
                            pass
                        eof_retries = 0
                    time.sleep(0.05)
                    continue
                time.sleep(0.05)
                continue
            eof_retries = 0

            ok, encoded = cv2.imencode(".jpg", frame, [int(cv2.IMWRITE_JPEG_QUALITY), 93])
            if not ok:
                continue

            payload = base64.b64encode(encoded.tobytes()).decode("ascii")
            emit({"type": "frame", "data": payload})
            time.sleep(frame_delay)
    except (BrokenPipeError, KeyboardInterrupt):
        return 0
    finally:
        cap.release()


def cmd_picture(source: str, output_file: str) -> int:
    if cv2 is None:
        return emit_error("opencv-python (cv2) is not installed")

    cap = open_capture(source)
    if cap is None or not cap.isOpened():
        return emit_error(f"Failed to open camera source: {source}")

    try:
        ok, frame = cap.read()
        if not ok or frame is None:
            return emit_error("Failed to capture frame")

        output_dir = os.path.dirname(os.path.abspath(output_file))
        if output_dir:
            os.makedirs(output_dir, exist_ok=True)

        if not cv2.imwrite(output_file, frame):
            return emit_error(f"Failed to save image: {output_file}")

        print(json.dumps({"success": True, "filename": output_file}))
        return 0
    finally:
        cap.release()


def main() -> int:
    if len(sys.argv) < 2:
        return emit_error("Missing command. Use: list | stream <source> | picture <source> <output>")

    cmd = sys.argv[1].strip().lower()
    if cmd == "list":
        return cmd_list()
    if cmd == "stream":
        if len(sys.argv) < 3:
            return emit_error("Missing source for stream command")
        return cmd_stream(sys.argv[2])
    if cmd == "picture":
        if len(sys.argv) < 4:
            return emit_error("Missing source/output for picture command")
        return cmd_picture(sys.argv[2], sys.argv[3])

    return emit_error(f"Unknown command: {cmd}")


if __name__ == "__main__":
    raise SystemExit(main())
