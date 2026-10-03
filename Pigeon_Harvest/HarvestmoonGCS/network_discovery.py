#!/usr/bin/env python3
"""
Network Discovery Module for MoonHarvest.
Automatically finds the Orange Pi (UAV) on the local network via router bridge.
Works with any router (TP-Link, etc.) by scanning the local subnet.
"""

import socket
import subprocess
import ipaddress
import time
import os
import platform
from concurrent.futures import ThreadPoolExecutor, as_completed
from typing import Optional, List

# Port where Orange Pi broadcasts MJPEG stream
ORANGE_PI_MJPEG_PORT = 8080
ORANGE_PI_STREAM_PATH = "/video_feed"

# MJPEG paths: Orange Pi (MoonHarvest) and RunCam WiFi Link 2
MJPEG_PATHS = ["/video_feed", "/?action=stream", "/stream"]

# Tuning: keep discovery fast (< ~3 s worst case)
PORT_SCAN_TIMEOUT = 0.25       # seconds per host during subnet scan
PORT_SCAN_WORKERS = 128        # parallel probes
MJPEG_TEST_TIMEOUT = 1.0       # seconds per MJPEG candidate
DISCOVER_BUDGET = 10.0         # overall budget for discover_orange_pi()

# Known router management subnet (TP-Link TL-MR3020 -> 192.168.1.1)
ROUTER_SUBNET = "192.168.1.0/24"

# Common IPs to try as fallback
FALLBACK_IPS = [
    "192.168.1.100",
    "192.168.1.10",
    "192.168.1.2",
    "192.168.1.1",
    "192.168.0.100",
    "192.168.0.10",
    "192.168.0.1",
    "10.42.0.100",
    "10.42.0.10",
    "10.42.0.2",
    "10.42.0.1",
    "10.235.141.127",
    "10.0.0.100",
]


def get_local_ip() -> Optional[str]:
    """Get the machine's own IP address on the local network."""
    try:
        # Create a UDP socket to determine the local IP
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.settimeout(1)
        # Connect to a public IP to determine the local interface
        s.connect(("8.8.8.8", 80))
        local_ip = s.getsockname()[0]
        s.close()
        return local_ip
    except Exception:
        pass
    
    # Fallback: try to get IP from hostname
    try:
        return socket.gethostbyname(socket.gethostname())
    except Exception:
        return None


def get_local_subnet() -> Optional[str]:
    """Get the local subnet CIDR based on the default network interface."""
    local_ip = get_local_ip()
    if not local_ip:
        return None
    
    try:
        # Parse IP and determine subnet
        ip_obj = ipaddress.ip_address(local_ip)
        if ip_obj.version == 4:
            # Default to /24 subnet (255 addresses)
            network = ipaddress.ip_network(f"{local_ip}/24", strict=False)
            return str(network)
    except Exception:
        pass
    
    # Try to get subnet from routing table
    try:
        if platform.system().lower().startswith("linux"):
            result = subprocess.run(
                ["ip", "route", "show", "default"],
                capture_output=True, text=True, timeout=3
            )
            if result.returncode == 0 and result.stdout.strip():
                # Parse output like: "default via 192.168.1.1 dev wlan0"
                parts = result.stdout.strip().split()
                if "via" in parts:
                    gateway_idx = parts.index("via")
                    gateway = parts[gateway_idx + 1]
                    gw_ip = ipaddress.ip_address(gateway)
                    network = ipaddress.ip_network(f"{gateway}/24", strict=False)
                    return str(network)
    except Exception:
        pass
    
    # Fallback: derive /24 from local IP (covers 10.42.0.x, 192.168.x.x, etc.)
    try:
        return str(ipaddress.ip_network(f"{local_ip}/24", strict=False))
    except Exception:
        pass

    return "192.168.1.0/24"  # Default subnet


def get_gateway_ip() -> Optional[str]:
    """Get the router/gateway IP address."""
    try:
        if platform.system().lower().startswith("linux"):
            result = subprocess.run(
                ["ip", "route", "show", "default"],
                capture_output=True, text=True, timeout=3
            )
            if result.returncode == 0 and result.stdout.strip():
                parts = result.stdout.strip().split()
                if "via" in parts:
                    gateway_idx = parts.index("via")
                    return parts[gateway_idx + 1]
    except Exception:
        pass
    
    # Fallback: derive gateway from local IP's subnet (.1)
    local_ip = get_local_ip()
    if local_ip:
        try:
            net = ipaddress.ip_network(f"{local_ip}/24", strict=False)
            return str(net.network_address + 1)
        except Exception:
            pass

    return None


def get_local_ipv4_networks() -> List[str]:
    """
    Enumerate ALL local IPv4 /24 networks from active interfaces.

    Needed because the UAV (Orange Pi) may sit on a different interface than the
    default route — e.g. laptop on WiFi (192.168.0.x) + LAN cable to the TP-Link
    router (192.168.1.x) where the Orange Pi actually lives.

    With fully-wireless setups (laptop + UAV both on the TP-Link SSID) the laptop
    often still has a second interface up, so we never rely on the default route
    alone. The default-route subnet is placed last so UAV LANs are scanned first.
    """
    networks: List[str] = []

    def add(cidr: str) -> None:
        try:
            net = ipaddress.ip_network(cidr, strict=False)
        except Exception:
            return
        if net.version != 4 or net.prefixlen < 24:
            return
        # Normalise to /24 so a full sweep is bounded and fast
        net24 = str(ipaddress.ip_network(f"{net.network_address}/24", strict=False))
        if net24 not in networks:
            networks.append(net24)

    # Linux: parse `ip -4 -o addr show`
    try:
        if platform.system().lower().startswith("linux"):
            result = subprocess.run(
                ["ip", "-4", "-o", "addr", "show"],
                capture_output=True, text=True, timeout=3
            )
            if result.returncode == 0:
                for line in result.stdout.splitlines():
                    parts = line.split()
                    if len(parts) < 4:
                        continue
                    iface = parts[1]
                    if iface == "lo" or iface.startswith(("docker", "virbr", "veth", "br-")):
                        continue
                    add(parts[3])
    except Exception:
        pass

    # Fallback / other OS: derive from default-route IP and hostname
    if not networks:
        for ip in (get_local_ip(), _safe_hostname_ip()):
            if ip:
                add(f"{ip}/24")

    # Scan the UAV-side LAN first: the TP-Link management subnet, then any other
    # subnet that is not the default route (default route is likely internet).
    priority: List[str] = []
    default_subnet = None
    local_ip = get_local_ip()
    if local_ip:
        try:
            default_subnet = str(ipaddress.ip_network(f"{local_ip}/24", strict=False))
        except Exception:
            default_subnet = None

    if ROUTER_SUBNET in networks:
        priority.append(ROUTER_SUBNET)

    for sn in networks:
        if sn in priority or sn == default_subnet:
            continue
        priority.append(sn)

    # Default-route subnet last: it is the least likely place for the UAV.
    for sn in networks:
        if sn not in priority:
            priority.append(sn)

    return priority


def _safe_hostname_ip() -> Optional[str]:
    """Best-effort hostname lookup for extra local IPv4 addresses."""
    try:
        infos = socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)
        for info in infos:
            sockaddr = info[4]
            if not sockaddr:
                continue
            ip = str(sockaddr[0])
            if not ip.startswith("127."):
                return ip
    except Exception:
        pass
    return None


def _test_port(ip: str, port: int = ORANGE_PI_MJPEG_PORT, timeout: float = PORT_SCAN_TIMEOUT) -> bool:
    """Test if a port is open on the given IP."""
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(timeout)
        result = sock.connect_ex((ip, port))
        sock.close()
        return result == 0
    except Exception:
        return False


try:
    import cv2  # type: ignore
except Exception:
    cv2 = None


def _test_mjpeg_stream(ip: str, timeout: float = 2.0) -> Optional[str]:
    """
    Test MJPEG paths on the given IP.
    Returns the first working stream URL, or None.

    Handles both MoonHarvest/Orange Pi (/video_feed) and
    RunCam WiFi Link 2 (/?action=stream).

    Catatan kecepatan: OpenCV mengabaikan CAP_PROP_OPEN_TIMEOUT_MSEC untuk HTTP,
    jadi kita pre-check port 8080 lewat socket ber-timeout sebelum membuka stream.
    Tanpa ini, host mati membuat deteksi menggantung puluhan detik.
    """
    # Pre-check cepat: jika port MJPEG tertutup, tidak perlu coba OpenCV sama sekali.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.settimeout(timeout)
        if s.connect_ex((ip, ORANGE_PI_MJPEG_PORT)) != 0:
            return None

    if cv2 is None:
        return None
    for path in MJPEG_PATHS:
        url = f"http://{ip}:{ORANGE_PI_MJPEG_PORT}{path}"
        try:
            cap = cv2.VideoCapture(url)
            if cap is not None and cap.isOpened():
                ret, frame = cap.read()
                cap.release()
                if ret and frame is not None:
                    return url
            if cap is not None:
                cap.release()
        except Exception:
            continue
    return None


def scan_network(subnet: str, port: int = ORANGE_PI_MJPEG_PORT,
                 timeout: float = PORT_SCAN_TIMEOUT) -> List[str]:
    """Scan the subnet for the given port in parallel. Returns responsive IPs."""
    responsive_ips = []
    try:
        network = ipaddress.ip_network(subnet, strict=False)
        hosts = [str(ip) for ip in network.hosts()]
        with ThreadPoolExecutor(max_workers=PORT_SCAN_WORKERS) as pool:
            futures = {pool.submit(_test_port, ip, port, timeout): ip for ip in hosts}
            for fut in as_completed(futures, timeout=timeout * 4 + 2):
                try:
                    if fut.result():
                        responsive_ips.append(futures[fut])
                except Exception:
                    continue
    except Exception:
        pass
    return responsive_ips


def _first_working_mjpeg(ips: List[str], timeout: float = MJPEG_TEST_TIMEOUT) -> Optional[str]:
    """
    Cek beberapa IP sekaligus (paralel) dan kembalikan URL MJPEG pertama yang hidup.

    Versi sekuensial memakan timeout penuh per IP, sehingga host yang mati
    (umum di setup router) membuat discovery menggantung sampai belasan detik.
    """
    if not ips:
        return None
    try:
        with ThreadPoolExecutor(max_workers=min(len(ips), 32)) as pool:
            futures = {pool.submit(_test_mjpeg_stream, ip, timeout): ip for ip in ips}
            try:
                for fut in as_completed(futures, timeout=timeout + 2.0):
                    try:
                        url = fut.result()
                        if url:
                            return url
                    except Exception:
                        continue
            except Exception:
                pass
    except Exception:
        pass
    return None


def discover_orange_pi(timeout: float = PORT_SCAN_TIMEOUT) -> Optional[str]:
    """
    Automatically discover the Orange Pi (UAV) on the local network.
    Returns a working MJPEG stream URL, or None.

    Works with a TP-Link router bridging UAV and GCS (same as a phone hotspot):
    1. Enumerate ALL local subnets (LAN 192.168.1.x, WiFi 192.168.0.x, ...)
    2. Also include the router subnet 192.168.1.0/24
    3. Parallel scan for port 8080
    4. Verify MJPEG on both /video_feed and /?action=stream
    5. Fall back to known IPs / gateway
    """
    deadline = time.monotonic() + DISCOVER_BUDGET
    local_ip = get_local_ip()

    def _try_mjpeg(ip: str) -> Optional[str]:
        return _test_mjpeg_stream(ip, timeout=MJPEG_TEST_TIMEOUT)

    # Step 1: collect every candidate subnet
    subnets: List[str] = []
    for sn in get_local_ipv4_networks():
        if sn not in subnets:
            subnets.append(sn)
    if ROUTER_SUBNET not in subnets:
        subnets.append(ROUTER_SUBNET)

    # Step 2+3: parallel scan for the MJPEG port, then verify the stream
    responsive_ips: List[str] = []
    for sn in subnets:
        if time.monotonic() >= deadline:
            break
        responsive_ips.extend(scan_network(sn, ORANGE_PI_MJPEG_PORT, timeout=timeout))

    # Step 4: verify MJPEG stream on responsive IPs (paralel)
    if responsive_ips and time.monotonic() < deadline:
        url = _first_working_mjpeg(responsive_ips)
        if url:
            return url

    # Step 5: try fallback IPs (router subnet candidates first, paralel)
    ordered_fallbacks = list(FALLBACK_IPS)
    if ROUTER_SUBNET in subnets:
        try:
            net = ipaddress.ip_network(ROUTER_SUBNET, strict=False)
            router_ips = [str(net.network_address + i) for i in (100, 101, 102, 10, 2)]
            ordered_fallbacks = router_ips + [ip for ip in ordered_fallbacks
                                              if ip not in router_ips]
        except Exception:
            pass

    if ordered_fallbacks and time.monotonic() < deadline:
        url = _first_working_mjpeg(ordered_fallbacks)
        if url:
            return url

    # Step 6: try gateway IP
    gateway = get_gateway_ip()
    if gateway and gateway != local_ip and time.monotonic() < deadline:
        url = _try_mjpeg(gateway)
        if url:
            return url

    return None


def get_orange_pi_url() -> str:
    """
    Get the Orange Pi MJPEG stream URL.
    Automatically discovers if not found, returns the URL or a default.
    """
    url = discover_orange_pi()
    if url:
        return url
    # Return default for manual entry: router subnet host first (.100), else local /24
    try:
        net = ipaddress.ip_network(ROUTER_SUBNET, strict=False)
        return f"http://{net.network_address + 100}:8080/video_feed"
    except Exception:
        return "http://192.168.1.100:8080/video_feed"


def get_network_info() -> dict:
    """Get all network information as a dictionary."""
    return {
        "local_ip": get_local_ip(),
        "subnet": get_local_subnet(),
        "subnets": get_local_ipv4_networks(),
        "gateway": get_gateway_ip(),
        "orange_pi_url": get_orange_pi_url(),
    }


if __name__ == "__main__":
    import sys

    if len(sys.argv) > 1 and sys.argv[1] in ("scan", "--scan", "diag"):
        # Diagnostic: tunjukkan subnet yang dipindai dan host dengan port MJPEG terbuka
        print("=== MoonHarvest network diagnostic ===")
        print(f"Local IP (default route) : {get_local_ip()}")
        print(f"All local /24 subnets    : {get_local_ipv4_networks()}")
        print(f"Gateway                  : {get_gateway_ip()}")
        print()
        for sn in get_local_ipv4_networks():
            hosts = scan_network(sn, ORANGE_PI_MJPEG_PORT)
            print(f"[{sn}] port {ORANGE_PI_MJPEG_PORT} open on: {hosts or 'none'}")
        print()
        url = discover_orange_pi()
        print(f"discover_orange_pi()     : {url or 'NOT FOUND'}")
        sys.exit(0 if url else 1)

    info = get_network_info()
    print(f"Local IP: {info['local_ip']}")
    print(f"Subnet: {info['subnet']}")
    print(f"Subnets (all interfaces): {info['subnets']}")
    print(f"Gateway: {info['gateway']}")
    print(f"Orange Pi URL: {info['orange_pi_url']}")
