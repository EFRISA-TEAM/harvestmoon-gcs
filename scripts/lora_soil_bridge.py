#!/usr/bin/env python3
"""
MoonHarvest GCS — LoRa Soil Sensor Bridge & Automated Checker
============================================================
Program ini bertindak sebagai jembatan (*gateway bridge*) dan engine pengecekan
otomatis untuk data sensor tanah lapangan (Ground Truth Probes) yang dikirimkan via LoRa.

Mendukung:
1. Pembacaan Serial USB UART langsung dari modul LoRa (ESP32, SX1276/78, Ebyte, Heltec)
2. Parsing format JSON, +LORA: CSV, dan Key-Value
3. Pengecekan otomatis ambang batas agronomi (Defisit Air <20%, Stres 20-35%, Optimal 35-70%)
4. Penerusan paket data telemetri ke MoonHarvest GCS via UDP port 14552
5. Mode simulasi 4 node PDP (P-01, P-02, P-03, P-04) untuk uji lab & demo
"""

import sys
import time
import json
import socket
import argparse
import random
from datetime import datetime, timezone

# ANSI Color Codes untuk terminal
CLR_RESET  = "\033[0m"
CLR_RED    = "\033[91m"
CLR_YELLOW = "\033[93m"
CLR_GREEN  = "\033[92m"
CLR_CYAN   = "\033[96m"
CLR_BOLD   = "\033[1m"

DEFAULT_UDP_HOST = "127.0.0.1"
DEFAULT_UDP_PORT = 14552

NODES_CONFIG = {
    "P-01": {"name": "Blok A1", "base_vwc": 14.8, "base_temp": 32.5, "base_volt": 4.10, "target": "Kritis Defisit Air"},
    "P-02": {"name": "Blok B2", "base_vwc": 23.4, "base_temp": 31.0, "base_volt": 4.05, "target": "Stres Irigasi"},
    "P-03": {"name": "Blok C3", "base_vwc": 38.2, "base_temp": 29.4, "base_volt": 4.20, "target": "Variatif / Waspada"},
    "P-04": {"name": "Blok D4", "base_vwc": 68.1, "base_temp": 27.6, "base_volt": 4.22, "target": "Optimal Macak-macak"},
}

def evaluate_node_health(node_id: str, vwc: float, temp: float, volt: float, rssi: int):
    """Mengevaluasi kondisi agronomi dan integritas link perangkat secara otomatis."""
    status = "OPTIMAL"
    color = CLR_GREEN
    action = "Pertahankan kondisi macak-macak."
    alert = False

    if vwc < 20.0:
        status = "KRITIS_DEFISIT"
        color = CLR_RED
        action = "Penggenangan macak-macak 3–5 cm segera!"
        alert = True
    elif vwc < 35.0:
        status = "WASPADA_STRES"
        color = CLR_YELLOW
        action = "Buka pintu air tersier 30%."
        alert = True
    elif vwc <= 70.0:
        status = "OPTIMAL_MACAK"
        color = CLR_GREEN
        action = "Kondisi terhidrasi optimal."
        alert = False
    else:
        status = "TERGENANG"
        color = CLR_CYAN
        action = "Buka saluran pembuang/drainase."
        alert = False

    hw_warning = ""
    if volt < 3.4:
        hw_warning = " [BATERAI RENDAH <3.4V]"
        alert = True
    if rssi < -115:
        hw_warning += " [SINYAL MARGINAL]"

    return {
        "node_id": node_id,
        "vwc": vwc,
        "temp": temp,
        "volt": volt,
        "rssi": rssi,
        "status": status,
        "color": color,
        "action": action,
        "hw_warning": hw_warning,
        "alert": alert
    }

def send_udp_packet(sock, payload_dict, host, port):
    """Mengirim payload JSON ke MoonHarvest GCS via UDP."""
    try:
        raw_bytes = json.dumps(payload_dict).encode("utf-8")
        sock.sendto(raw_bytes, (host, port))
    except Exception as e:
        print(f"Error sending UDP: {e}", file=sys.stderr)

def parse_raw_line(line: str):
    """Mendukung format JSON, +LORA: CSV, dan CSV sederhana."""
    clean = line.strip()
    if not clean:
        return None

    # 1. JSON
    if clean.startswith("{") and clean.endswith("}"):
        try:
            data = json.loads(clean)
            node = data.get("node") or data.get("id") or data.get("nodeId") or "P-01"
            vwc = float(data.get("moisture") or data.get("vwc") or data.get("soil") or 0.0)
            temp = float(data.get("temp") or data.get("temperature") or 28.0)
            volt = float(data.get("volt") or data.get("bat") or data.get("battery") or 4.1)
            rssi = int(data.get("rssi") or -75)
            return node.upper(), vwc, temp, volt, rssi
        except Exception:
            pass

    # 2. +LORA: atau LORA: CSV
    if clean.upper().startswith("+LORA:") or clean.upper().startswith("LORA:"):
        clean = clean.split(":", 1)[1].strip()

    # 3. CSV
    if "," in clean:
        parts = [p.strip() for p in clean.split(",")]
        if len(parts) >= 2:
            node = parts[0].upper()
            try:
                vwc = float(parts[1])
                temp = float(parts[2]) if len(parts) > 2 else 29.0
                volt = float(parts[3]) if len(parts) > 3 else 4.10
                rssi = int(parts[4]) if len(parts) > 4 else -78
                return node, vwc, temp, volt, rssi
            except ValueError:
                pass

    return None

def run_simulation(host: str, port: int, interval: float = 1.5):
    """Menjalankan siklus transmisi 4 node LoRa sintetis dengan fluktuasi alami."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print(f"\n{CLR_BOLD}{CLR_GREEN}=== Memulai Mode Simulasi LoRa Sensor Tanah (4 Node PDP) ==={CLR_RESET}")
    print(f"Target UDP GCS : {host}:{port}")
    print(f"Interval       : {interval} detik")
    print(f"Daftar Node    : P-01 (Kritis), P-02 (Stres), P-03 (Variatif), P-04 (Optimal)\n")

    step = 0
    node_keys = ["P-01", "P-02", "P-03", "P-04"]

    try:
        while True:
            node_id = node_keys[step % len(node_keys)]
            cfg = NODES_CONFIG[node_id]

            # Variasi realistis ±0.2%
            jitter = (random.random() * 0.4) - 0.2
            vwc = round(cfg["base_vwc"] + jitter, 1)
            temp = round(cfg["base_temp"] + (random.random() * 0.5 - 0.25), 1)
            volt = round(cfg["base_volt"] + (random.random() * 0.04 - 0.02), 2)
            rssi = -75 - random.randint(0, 8)

            eval_res = evaluate_node_health(node_id, vwc, temp, volt, rssi)

            packet = {
                "node": node_id,
                "nodeName": f"Probe {cfg['name']}",
                "moisture": vwc,
                "vwc": vwc,
                "temp": temp,
                "volt": volt,
                "rssi": rssi,
                "timestamp": datetime.now(timezone.utc).isoformat()
            }

            send_udp_packet(sock, packet, host, port)

            timestamp_str = datetime.now().strftime("%H:%M:%S")
            print(f"[{timestamp_str}] {eval_res['color']}{node_id} ({cfg['name']}){CLR_RESET} | "
                  f"VWC: {eval_res['color']}{vwc:5.1f}%{CLR_RESET} | Temp: {temp:4.1f}°C | "
                  f"Volt: {volt:.2f}V | RSSI: {rssi:3d}dBm | "
                  f"Status: {eval_res['color']}{eval_res['status']}{eval_res['hw_warning']}{CLR_RESET} -> {eval_res['action']}")

            step += 1
            time.sleep(interval)
    except KeyboardInterrupt:
        print(f"\n{CLR_YELLOW}Simulasi dihentikan oleh pengguna.{CLR_RESET}")
    finally:
        sock.close()

def run_serial_bridge(port_name: str, baud_rate: int, host: str, port: int):
    """Membaca port serial fisik LoRa dan meneruskan ke GCS."""
    import serial

    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print(f"\n{CLR_BOLD}{CLR_GREEN}=== Membuka Koneksi Port Serial LoRa: {port_name} @ {baud_rate} bps ==={CLR_RESET}")
    print(f"Meneruskan ke MoonHarvest GCS: {host}:{port}\n")

    try:
        ser = serial.Serial(port_name, baud_rate, timeout=1)
        while True:
            raw_line = ser.readline().decode("utf-8", errors="ignore").strip()
            if not raw_line:
                continue

            parsed = parse_raw_line(raw_line)
            if parsed:
                node_id, vwc, temp, volt, rssi = parsed
                eval_res = evaluate_node_health(node_id, vwc, temp, volt, rssi)

                packet = {
                    "node": node_id,
                    "moisture": vwc,
                    "vwc": vwc,
                    "temp": temp,
                    "volt": volt,
                    "rssi": rssi,
                    "raw": raw_line,
                    "timestamp": datetime.now(timezone.utc).isoformat()
                }

                send_udp_packet(sock, packet, host, port)

                timestamp_str = datetime.now().strftime("%H:%M:%S")
                print(f"[{timestamp_str}] {eval_res['color']}{node_id}{CLR_RESET} | "
                      f"VWC: {eval_res['color']}{vwc:5.1f}%{CLR_RESET} | Temp: {temp:4.1f}°C | "
                      f"Volt: {volt:.2f}V | RSSI: {rssi:3d}dBm | "
                      f"Auto-Check: {eval_res['color']}{eval_res['status']}{eval_res['hw_warning']}{CLR_RESET}")
            else:
                print(f"[RAW LoRa] {raw_line}")

    except KeyboardInterrupt:
        print(f"\n{CLR_YELLOW}Bridge dihentikan.{CLR_RESET}")
    except Exception as e:
        print(f"\n{CLR_RED}Gagal mengakses port serial {port_name}: {e}{CLR_RESET}")
    finally:
        sock.close()

def main():
    parser = argparse.ArgumentParser(description="MoonHarvest LoRa Soil Sensor Receiver & Auto-Checker")
    parser.add_argument("--serial", type=str, default=None, help="Port Serial USB LoRa (misal: /dev/ttyUSB0 atau COM3)")
    parser.add_argument("--baud", type=int, default=115200, help="Baud rate Serial LoRa (default: 115200)")
    parser.add_argument("--udp-host", type=str, default=DEFAULT_UDP_HOST, help="Target GCS UDP Host (default: 127.0.0.1)")
    parser.add_argument("--udp-port", type=int, default=DEFAULT_UDP_PORT, help="Target GCS UDP Port (default: 14552)")
    parser.add_argument("--simulate", action="store_true", help="Jalankan mode simulasi transmisi 4 node lapangan")
    parser.add_argument("--interval", type=float, default=1.5, help="Interval pengiriman simulasi dalam detik")

    args = parser.parse_args()

    if args.simulate or not args.serial:
        run_simulation(args.udp_host, args.udp_port, args.interval)
    else:
        run_serial_bridge(args.serial, args.baud, args.udp_host, args.udp_port)

if __name__ == "__main__":
    main()
