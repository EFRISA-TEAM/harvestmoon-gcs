#!/usr/bin/env python3
"""
MoonHarvest Security & Licensing Core
=====================================
Menyediakan proteksi menyeluruh untuk MoonHarvest:
1. Hardware Fingerprinting (HWID) lintas platform (Linux, Win, Raspberry Pi).
2. Remote Master Switch (Kendali jarak jauh dari cloud / URL / GitHub Gist).
3. Dead-Man's Switch (Offline lease timer untuk mencegah bypass offline).
4. Enkripsi & Dekripsi Model AI di memori RAM (AES-256-GCM).
5. Fail-Fast Enforcement yang mematikan program jika lisensi tidak sah / dikunci.
"""

import os
import sys
import json
import time
import hmac
import hashlib
import platform
import base64
import urllib.request
import urllib.error
from pathlib import Path
from datetime import datetime, timezone

# ── Konfigurasi Default ────────────────────────────────────────────────────────
DEFAULT_CONFIG_PATH = Path(__file__).resolve().parent / "moonharvest_security.json"
STORAGE_DIR = Path.home() / ".moonharvest"
LEASE_FILE = STORAGE_DIR / "lease.v1"
DEFAULT_LEASE_HOURS = 48  # Maksimal boleh offline 48 jam sebelum wajib konek lagi

# Default fallback remote URL (Tertanam permanen di dalam binary program)
DEFAULT_REMOTE_URL = os.environ.get(
    "MOONHARVEST_LOCK_URL",
    "https://gist.githubusercontent.com/FawwazFA/fc07275e43ed0c1730f63807e163ae1b/raw/moonharvest_lock.json"
)

# Kunci salt internal untuk enkripsi lease lokal
_INTERNAL_SALT = b"MoonHarvest-DefisitAir-Padi-2026-SecretSalt"


def get_hardware_id() -> str:
    """
    Menghasilkan Hardware ID unik berbasis CPU, Motherboard, dan Machine-ID.
    Konsisten pada Linux, Windows, dan Raspberry Pi.
    """
    components = []

    # 1. Platform & Node
    components.append(platform.system())
    components.append(platform.machine())

    # 2. Linux / Raspberry Pi machine-id
    machine_id_paths = [
        Path("/etc/machine-id"),
        Path("/var/lib/dbus/machine-id"),
        Path("/sys/class/dmi/id/product_uuid"),
    ]
    for p in machine_id_paths:
        try:
            if p.exists():
                text = p.read_text().strip()
                if text:
                    components.append(text)
                    break
        except Exception:
            pass

    # 3. MAC address fallback / supplement
    try:
        import uuid
        node = uuid.getnode()
        components.append(f"{node:012x}")
    except Exception:
        pass

    # 4. Username & Hostname
    components.append(os.environ.get("USER", os.environ.get("USERNAME", "default_user")))
    components.append(platform.node())

    raw_id = "|".join(components)
    digest = hashlib.sha256(raw_id.encode("utf-8")).hexdigest()
    # Format agar mudah dibaca: HWID-XXXX-XXXX-XXXX-XXXX
    return f"HWID-{digest[:4].upper()}-{digest[4:8].upper()}-{digest[8:12].upper()}-{digest[12:16].upper()}"


def load_security_config() -> dict:
    """Membaca konfigurasi keamanan lokal jika ada."""
    config = {
        "remote_switch_url": DEFAULT_REMOTE_URL,
        "offline_lease_hours": DEFAULT_LEASE_HOURS,
        "license_key": "MH-COMMUNITY-EVAL",
        "allow_emergency_offline": True,
    }

    if DEFAULT_CONFIG_PATH.exists():
        try:
            with open(DEFAULT_CONFIG_PATH, "r", encoding="utf-8") as f:
                loaded = json.load(f)
                config.update(loaded)
        except Exception as e:
            sys.stderr.write(f"[Security] Gagal membaca konfigurasi: {e}\n")

    # Override dari environment variable jika ada
    if "MOONHARVEST_LOCK_URL" in os.environ:
        config["remote_switch_url"] = os.environ["MOONHARVEST_LOCK_URL"]
    if "MOONHARVEST_LICENSE_KEY" in os.environ:
        config["license_key"] = os.environ["MOONHARVEST_LICENSE_KEY"]

    return config


def _derive_lease_key(hwid: str) -> bytes:
    """Menurunkan kunci HMAC 256-bit dari HWID dan Salt internal."""
    return hashlib.pbkdf2_hmac("sha256", hwid.encode("utf-8"), _INTERNAL_SALT, 10000)


def save_offline_lease(hwid: str, lease_hours: int, status_msg: str):
    """Menyimpan lease lokal terenkripsi/terautentikasi."""
    try:
        STORAGE_DIR.mkdir(parents=True, exist_ok=True)
        expires_at = time.time() + (lease_hours * 3600)
        payload = {
            "hwid": hwid,
            "expires_at": expires_at,
            "created_at": time.time(),
            "status": "ACTIVE",
            "message": status_msg
        }
        json_bytes = json.dumps(payload).encode("utf-8")
        key = _derive_lease_key(hwid)
        sig = hmac.new(key, json_bytes, hashlib.sha256).digest()

        # Simpan format: SIG(32 byte) + JSON
        with open(LEASE_FILE, "wb") as f:
            f.write(sig + json_bytes)
    except Exception as e:
        sys.stderr.write(f"[Security] Gagal menyimpan lease: {e}\n")


def check_offline_lease(hwid: str) -> tuple[bool, str]:
    """Mengecek apakah lease offline lokal masih berlaku."""
    if not LEASE_FILE.exists():
        return False, "Tidak ada data lisensi offline. Perangkat harus terhubung ke internet."

    try:
        with open(LEASE_FILE, "rb") as f:
            raw = f.read()

        if len(raw) < 32:
            return False, "Data lisensi offline rusak atau telah dimanipulasi."

        sig = raw[:32]
        json_bytes = raw[32:]

        key = _derive_lease_key(hwid)
        expected_sig = hmac.new(key, json_bytes, hashlib.sha256).digest()

        if not hmac.compare_digest(sig, expected_sig):
            return False, "Integritas lisensi offline tidak valid (Deteksi perubahan data/perangkat)."

        data = json.loads(json_bytes.decode("utf-8"))
        if data.get("hwid") != hwid:
            return False, "Lisensi ini tidak cocok dengan Hardware ID perangkat ini."

        expires_at = data.get("expires_at", 0)
        remaining_seconds = expires_at - time.time()

        if remaining_seconds <= 0:
            return False, "Masa sewa offline telah kedaluwarsa. Sambungkan ke internet untuk memperbarui izin."

        remaining_hours = int(remaining_seconds // 3600)
        return True, f"Mode offline aktif (Tersisa {remaining_hours} jam sewa)"

    except Exception as e:
        return False, f"Kesalahan verifikasi lisensi offline: {e}"


def check_remote_switch(url: str, hwid: str, license_key: str, timeout: int = 4) -> tuple[bool, str, dict | None]:
    """
    Menghubungi endpoint remote switch (Saklar Jarak Jauh).
    Mendukung URL https://, http://, atau file:// (untuk pengujian lokal).
    Mengembalikan: (is_allowed, reason_message, raw_data)
    """
    if not url:
        return False, "URL remote switch belum dikonfigurasi.", None

    try:
        # Dukungan pengujian lokal (file:// atau path langsung)
        if url.startswith("file://") or (Path(url).is_file() and not url.startswith("http")):
            local_path = Path(url.replace("file://", ""))
            if not local_path.exists():
                return False, f"File switch lokal tidak ditemukan: {local_path}", None
            body = local_path.read_text(encoding="utf-8")
        elif url.startswith(("http://", "https://")):
            # Tambahkan cache-buster agar respons selalu real-time tanpa delay CDN
            sep = "&" if "?" in url else "?"
            live_url = f"{url}{sep}_t={int(time.time())}"
            headers = {
                "User-Agent": f"MoonHarvest-Security/1.0 ({platform.system()}; HWID={hwid})",
                "X-Hardware-ID": hwid,
                "X-License-Key": license_key
            }
            req = urllib.request.Request(live_url, headers=headers)
            with urllib.request.urlopen(req, timeout=timeout) as response:
                if response.status != 200:
                    return False, f"Server lisensi mengembalikan status HTTP {response.status}", None
                body = response.read().decode("utf-8")
        else:
            return False, f"Format URL remote switch tidak dikenali: {url}", None

        data = json.loads(body)

        # 1. Cek Saklar Global Master (Kill-Switch)
        if data.get("global_lock", False) is True:
            msg = data.get("lock_message", "Sistem telah dikunci secara global oleh pemilik program.")
            return False, f"[TERKUNCI] {msg}", data

        # 2. Cek Tanggal Kedaluwarsa Global
        valid_until = data.get("valid_until")
        if valid_until:
            try:
                exp_dt = datetime.fromisoformat(valid_until).replace(tzinfo=timezone.utc)
                now_dt = datetime.now(timezone.utc)
                if now_dt > exp_dt:
                    return False, f"[KEDALUWARSA] Masa berlaku sistem telah berakhir pada {valid_until}", data
            except Exception:
                pass

        # 3. Cek Kunci Lisensi Paket Spesifik (Jika Ada)
        licenses = data.get("licenses", {})
        if licenses and license_key in licenses:
            lic_info = licenses[license_key]
            lic_status = lic_info.get("status", "ACTIVE").upper()
            if lic_status != "ACTIVE":
                return False, f"[LISENSI DICABUT] Kunci lisensi '{license_key}' ({lic_info.get('holder', '')}) berstatus {lic_status}. Akses dinonaktifkan oleh pemilik.", data

            lic_exp = lic_info.get("valid_until")
            if lic_exp:
                try:
                    exp_dt = datetime.fromisoformat(lic_exp).replace(tzinfo=timezone.utc)
                    if datetime.now(timezone.utc) > exp_dt:
                        return False, f"[LISENSI HABIS] Kunci lisensi '{license_key}' telah kedaluwarsa pada {lic_exp}.", data
                except Exception:
                    pass

        # 4. Cek Tabel Multi-Device Granular (Per-Device Status & Expiry)
        devices = data.get("devices", {})
        if devices:
            if hwid in devices:
                dev = devices[hwid]
                dev_name = dev.get("name", hwid)
                dev_status = dev.get("status", "ACTIVE").upper()

                if dev_status != "ACTIVE":
                    return False, f"[LISENSI DICABUT] Perangkat '{dev_name}' ({hwid}) berstatus {dev_status}. Akses dinonaktifkan oleh pemilik.", data

                dev_exp = dev.get("valid_until")
                if dev_exp:
                    try:
                        exp_dt = datetime.fromisoformat(dev_exp).replace(tzinfo=timezone.utc)
                        if datetime.now(timezone.utc) > exp_dt:
                            return False, f"[LISENSI HABIS] Lisensi perangkat '{dev_name}' telah kedaluwarsa pada {dev_exp}.", data
                    except Exception:
                        pass

                return True, f"Perangkat '{dev_name}' terverifikasi aktif.", data
            else:
                # Perangkat tidak terdaftar di tabel devices
                authorized_hwids = data.get("authorized_hardware", [])
                if "*" not in authorized_hwids:
                    return False, f"[PERANGKAT BELUM TERDAFTAR] Hardware ID ({hwid}) belum diaktivasi oleh pemilik sistem. Salin ID ini dan kirimkan ke admin.", data

        # 5. Cek Whitelist Hardware Global (fallback)
        authorized_hwids = data.get("authorized_hardware", ["*"])
        if "*" not in authorized_hwids and hwid not in authorized_hwids:
            return False, f"[TIDAK DIIZINKAN] Perangkat ({hwid}) tidak terdaftar dalam lisensi sah.", data

        return True, "Lisensi valid dan terverifikasi secara online.", data

    except (urllib.error.URLError, urllib.error.HTTPError, TimeoutError, OSError) as e:
        return False, f"Tidak dapat menghubungi server lisensi: {e}", None
    except json.JSONDecodeError:
        return False, "Format respons server lisensi bukan JSON yang valid.", None


def verify_system_access() -> tuple[bool, str]:
    """
    Fungsi Utama Verifikasi Akses Sistem MoonHarvest:
    1. Cek remote switch secara online.
    2. Jika online sukses -> simpan offline lease -> izinkan.
    3. Jika offline/gagal koneksi -> cek offline lease -> izinkan jika masih dalam masa sewa.
    4. Jika saklar menyatakan LOCKED -> tolak mutlak & hapus lease lokal!
    """
    hwid = get_hardware_id()
    cfg = load_security_config()
    remote_url = cfg.get("remote_switch_url", "")
    lic_key = cfg.get("license_key", "")
    lease_hours = cfg.get("offline_lease_hours", DEFAULT_LEASE_HOURS)

    # 1. Percobaan Verifikasi Online
    online_ok, online_msg, remote_data = check_remote_switch(remote_url, hwid, lic_key, timeout=3)

    if online_ok:
        # Sukses online -> perbarui sewa lokal
        save_offline_lease(hwid, lease_hours, online_msg)
        return True, f"Online OK: {online_msg}"

    # Jika remote menyatakan secara eksplisit bahwa program TERKUNCI (bukan karena koneksi gagal)
    if remote_data is not None and (remote_data.get("global_lock") is True or "TERKUNCI" in online_msg or "DICABUT" in online_msg):
        # Hapus lease lokal seketika agar tidak bisa dipakai lagi lewat offline!
        if LEASE_FILE.exists():
            try:
                LEASE_FILE.unlink()
            except Exception:
                pass
        return False, online_msg

    # 2. Jika koneksi gagal (misal tidak ada internet di sawah / lapangan)
    if cfg.get("allow_emergency_offline", True):
        offline_ok, offline_msg = check_offline_lease(hwid)
        if offline_ok:
            return True, f"Offline Fallback OK: {offline_msg}"
        else:
            return False, f"Akses Gagal ({online_msg}). {offline_msg}"

    return False, f"Akses Ditolak: {online_msg}"


def enforce_security(exit_on_failure: bool = True) -> bool:
    """
    Eksekusi pemeriksaan lisensi.
    Jika gagal: menampilkan pesan error MoonHarvest JSON protocol dan mematikan proses.
    """
    is_allowed, reason = verify_system_access()
    if is_allowed:
        # Mengirim log info via stderr / stdout format MoonHarvest
        sys.stderr.write(f"[MoonHarvest Security] Verifikasi Berhasil: {reason}\n")
        sys.stderr.flush()
        return True

    # Jika akses ditolak / terkunci
    error_payload = {
        "type": "error",
        "data": f"AKSES DITOLAK: {reason}"
    }
    print(json.dumps(error_payload), flush=True)
    sys.stderr.write(f"\n======================================================\n")
    sys.stderr.write(f"[CRITICAL SECURITY LOCKOUT]\n")
    sys.stderr.write(f"Pesan: {reason}\n")
    sys.stderr.write(f"Hardware ID: {get_hardware_id()}\n")
    sys.stderr.write(f"Waktu: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")
    sys.stderr.write(f"======================================================\n\n")
    sys.stderr.flush()

    if exit_on_failure:
        sys.exit(403)

    return False


# ── Utilitas Enkripsi Model AI (AES-256-GCM) ──────────────────────────────────
class ModelCrypto:
    """
    Utilitas enkripsi dan dekripsi model AI (ONNX / PyTorch .pt)
    Model didekripsi langsung ke memori (RAM) tanpa pernah menulis file mentah ke disk.
    """

    @staticmethod
    def _get_cipher_key(secret_passphrase: str, hwid: str) -> bytes:
        combined = f"{secret_passphrase}::{hwid}::{_INTERNAL_SALT.decode('utf-8')}"
        return hashlib.sha256(combined.encode("utf-8")).digest()

    @classmethod
    def encrypt_model_file(cls, input_file: Path, output_file: Path, secret_passphrase: str, target_hwid: str = "*"):
        """Mengenkripsi file model AI menjadi ciphertext biner terproteksi."""
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM

        if not input_file.exists():
            raise FileNotFoundError(f"File model sumber tidak ditemukan: {input_file}")

        key = cls._get_cipher_key(secret_passphrase, target_hwid)
        aesgcm = AESGCM(key)
        nonce = os.urandom(12)

        data = input_file.read_bytes()
        ciphertext = aesgcm.encrypt(nonce, data, target_hwid.encode("utf-8"))

        # Format file: MAGIC(8B: 'MHENC01\0') + NONCE(12B) + CIPHERTEXT
        with open(output_file, "wb") as f:
            f.write(b"MHENC01\0" + nonce + ciphertext)

    @classmethod
    def decrypt_model_to_memory(cls, enc_file: Path, secret_passphrase: str, current_hwid: str = "*") -> bytes:
        """
        Mendekripsi file model terenkripsi langsung menjadi bytes di RAM.
        Tidak ada file sementara yang dibuat di disk!
        """
        from cryptography.hazmat.primitives.ciphers.aead import AESGCM

        if not enc_file.exists():
            raise FileNotFoundError(f"File model terenkripsi tidak ditemukan: {enc_file}")

        with open(enc_file, "rb") as f:
            magic = f.read(8)
            if magic != b"MHENC01\0":
                raise ValueError("Format file model terenkripsi tidak dikenal atau rusak.")
            nonce = f.read(12)
            ciphertext = f.read()

        key = cls._get_cipher_key(secret_passphrase, current_hwid)
        aesgcm = AESGCM(key)

        try:
            return aesgcm.decrypt(nonce, ciphertext, current_hwid.encode("utf-8"))
        except Exception as e:
            raise PermissionError(f"Gagal mendekripsi model. Kunci rahasia atau Hardware ID tidak cocok: {e}")


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="MoonHarvest Security CLI")
    parser.add_argument("--hwid", action="store_true", help="Cetak Hardware ID perangkat ini")
    parser.add_argument("--verify", action="store_true", help="Uji verifikasi lisensi sekarang")
    args = parser.parse_args()

    if args.hwid:
        print(f"Perangkat Hardware ID: {get_hardware_id()}")
    elif args.verify:
        ok, msg = verify_system_access()
        print(f"Hasil Verifikasi: {'DIIZINKAN' if ok else 'DITOLAK'} -> {msg}")
    else:
        parser.print_help()
