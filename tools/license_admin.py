#!/usr/bin/env python3
"""
MoonHarvest License & Lock Administration CLI
============================================
Tool resmi untuk pemilik aplikasi mengelola penguncian, lisensi multi-device, dan enkripsi model.
"""

import sys
import json
import argparse
from pathlib import Path

# Tambahkan root path ke sys.path
ROOT_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT_DIR))

from moonharvest_security import (
    get_hardware_id,
    save_offline_lease,
    verify_system_access,
    ModelCrypto,
    LEASE_FILE
)

LOCK_FILE = Path(__file__).resolve().parent / "moonharvest_lock.json"
if not LOCK_FILE.exists():
    LOCK_FILE = ROOT_DIR / "moonharvest_lock.json"


def _load_lock_data():
    if not LOCK_FILE.exists():
        return {
            "app": "MoonHarvest",
            "global_lock": false,
            "authorized_hardware": ["*"],
            "devices": {}
        }
    with open(LOCK_FILE, "r", encoding="utf-8") as f:
        return json.load(f)


GIST_ID = "fc07275e43ed0c1730f63807e163ae1b"


def _save_lock_data(data):
    candidate_locks = [
        Path(__file__).resolve().parent / "moonharvest_lock.json",
        ROOT_DIR / "moonharvest_lock.json",
    ]
    for p in candidate_locks:
        if p.exists() or p.parent.exists():
            try:
                with open(p, "w", encoding="utf-8") as f:
                    json.dump(data, f, indent=2)
            except Exception:
                pass

    # Otomatis sinkronisasi ke Cloud Server (GitHub Gist)
    try:
        import subprocess
        primary_file = candidate_locks[0]
        res = subprocess.run(
            ["gh", "gist", "edit", GIST_ID, "-f", "moonharvest_lock.json", str(primary_file)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=10
        )
        if res.returncode == 0:
            print("  [☁️ Cloud Sync] Perubahan berhasil otomatis disinkronkan ke server cloud!")
    except Exception:
        pass


def cmd_hwid(args):
    hwid = get_hardware_id()
    print("\n" + "=" * 50)
    print(f"  Hardware ID Perangkat Ini: {hwid}")
    print("=" * 50)
    print("Catatan: Minta klien menjalankan perintah ini untuk mendaftarkan perangkat mereka.\n")


def cmd_status(args):
    ok, msg = verify_system_access()
    status_str = "DIIZINKAN (NORMAL)" if ok else "TERKUNCI / DITOLAK"
    print("\n" + "=" * 60)
    print(f"  Status Akses Sistem : {status_str}")
    print(f"  Keterangan          : {msg}")
    print("=" * 60 + "\n")


def cmd_toggle_lock(lock_state: bool):
    data = _load_lock_data()
    data["global_lock"] = lock_state
    if lock_state:
        data["lock_message"] = "SISTEM TELAH DIMATIKAN OLEH PEMILIK KARENA LISENSI BERAKHIR / DICABUT."
    else:
        data["lock_message"] = "Sistem Aktif Normal."

    _save_lock_data(data)
    state_str = "TERKUNCI (GLOBAL LOCK = TRUE)" if lock_state else "DIBUKA KEMBALI (GLOBAL LOCK = FALSE)"
    print(f"[✓] Status global diubah menjadi: {state_str}")
    print("    Unggah file ini ke GitHub Gist / server cloud Anda agar seluruh aplikasi di luar sana ikut terkunci!")


def cmd_list_devices(args):
    data = _load_lock_data()
    devices = data.get("devices", {})

    print("\n" + "═" * 80)
    print(f"{'STATUS':<10} | {'NAMA PERANGKAT':<25} | {'HARDWARE ID':<26} | {'KEDALUWARSA'}")
    print("═" * 80)

    if not devices:
        print("  Belum ada perangkat yang terdaftar di tabel devices.")
    else:
        for hwid, info in devices.items():
            status = info.get("status", "ACTIVE")
            name = info.get("name", "Unknown")[:25]
            valid = info.get("valid_until", "Forever")[:10]
            status_display = f"\033[92m{status}\033[0m" if status == "ACTIVE" else f"\033[91m{status}\033[0m"
            print(f"{status:<10} | {name:<25} | {hwid:<26} | {valid}")

    print("═" * 80)
    print(f"Total Perangkat: {len(devices)}")
    print("Global Lock Status: " + ("\033[91mTERKUNCI (SEMUA MATI)\033[0m" if data.get("global_lock") else "\033[92mNORMAL\033[0m") + "\n")


def cmd_add_device(args):
    data = _load_lock_data()
    if "devices" not in data:
        data["devices"] = {}

    hwid = args.hwid.strip().upper()
    valid_until = args.valid if args.valid.endswith("Z") else f"{args.valid}T23:59:59Z"

    data["devices"][hwid] = {
        "name": args.name,
        "status": "ACTIVE",
        "valid_until": valid_until,
        "note": args.note or f"Ditambahkan pada {args.valid}"
    }

    _save_lock_data(data)
    print(f"\n[✓] Berhasil mendaftarkan perangkat!")
    print(f"    Nama        : {args.name}")
    print(f"    Hardware ID : {hwid}")
    print(f"    Status      : ACTIVE")
    print(f"    Kedaluwarsa : {valid_until}\n")


def cmd_set_device_status(hwid: str, new_status: str):
    data = _load_lock_data()
    devices = data.get("devices", {})
    hwid = hwid.strip().upper()

    if hwid not in devices:
        print(f"[!] Perangkat dengan Hardware ID '{hwid}' tidak ditemukan di tabel.")
        return

    dev_name = devices[hwid].get("name", hwid)
    devices[hwid]["status"] = new_status
    _save_lock_data(data)

    action_str = "DICABUT / DIMATIKAN" if new_status == "REVOKED" else "DIAKTIFKAN KEMBALI"
    print(f"\n[✓] Sukses: Perangkat '{dev_name}' ({hwid}) telah {action_str}!")
    print("    Unggah moonharvest_lock.json ke cloud/Gist Anda agar perubahan langsung berlaku di perangkat klien.\n")


def cmd_remove_device(args):
    data = _load_lock_data()
    devices = data.get("devices", {})
    hwid = args.hwid.strip().upper()

    if hwid in devices:
        del devices[hwid]
        _save_lock_data(data)
        print(f"[✓] Perangkat '{hwid}' telah dihapus dari daftar.")
    else:
        print(f"[!] Perangkat '{hwid}' tidak ditemukan.")


def cmd_encrypt_model(args):
    input_path = Path(args.input)
    output_path = Path(args.output)
    key = args.key
    hwid = args.hwid or "*"

    if not input_path.exists():
        print(f"[!] File sumber model tidak ditemukan: {input_path}")
        return

    print(f"[*] Mengenkripsi {input_path} dengan AES-256-GCM...")
    ModelCrypto.encrypt_model_file(input_path, output_path, key, target_hwid=hwid)
    print(f"[✓] Berhasil! Model terenkripsi tersimpan di: {output_path}")
    print(f"    Target HWID: {hwid}")
    print(f"    Ukuran file: {output_path.stat().st_size / (1024*1024):.2f} MB")


def cmd_grant_lease(args):
    hwid = get_hardware_id()
    hours = args.hours
    save_offline_lease(hwid, hours, f"Manual offline lease diberikan untuk {hours} jam.")
    print(f"[✓] Sukses menerbitkan lease offline lokal untuk {hours} jam ke depan.")


def cmd_revoke_lease(args):
    if LEASE_FILE.exists():
        LEASE_FILE.unlink()
        print("[✓] Lease offline lokal berhasil dihapus.")
    else:
        print("[i] Tidak ada lease lokal yang tersimpan.")


def main():
    parser = argparse.ArgumentParser(description="MoonHarvest Multi-Device Security & Licensing Tool")
    subparsers = parser.add_subparsers(dest="command", help="Perintah yang tersedia")

    # hwid & status
    subparsers.add_parser("hwid", help="Cek Hardware ID komputer ini")
    subparsers.add_parser("status", help="Cek status verifikasi lisensi sistem")

    # global lock / unlock
    subparsers.add_parser("lock", help="Kunci seluruh sistem secara global (Kill All Devices)")
    subparsers.add_parser("unlock", help="Buka kunci global sistem")

    # multi-device management
    subparsers.add_parser("list-devices", help="Tampilkan daftar seluruh perangkat klien yang terdaftar")

    add_dev = subparsers.add_parser("add-device", help="Daftarkan perangkat klien baru")
    add_dev.add_argument("hwid", help="Hardware ID klien (misal: HWID-BCCF-EA6E-7BCC-B560)")
    add_dev.add_argument("--name", required=True, help="Nama klien atau deskripsi laptop (misal: 'Laptop Pak Budi')")
    add_dev.add_argument("--valid", default="2027-12-31", help="Tanggal kedaluwarsa YYYY-MM-DD (default: 2027-12-31)")
    add_dev.add_argument("--note", default="", help="Catatan tambahan")

    rev_dev = subparsers.add_parser("revoke-device", help="Matikan / cabut lisensi perangkat tertentu saja")
    rev_dev.add_argument("hwid", help="Hardware ID yang ingin dimatikan")

    act_dev = subparsers.add_parser("activate-device", help="Aktifkan kembali perangkat tertentu")
    act_dev.add_argument("hwid", help="Hardware ID yang ingin diaktifkan")

    rem_dev = subparsers.add_parser("remove-device", help="Hapus perangkat dari daftar")
    rem_dev.add_argument("hwid", help="Hardware ID yang ingin dihapus")

    # encrypt-model
    enc_parser = subparsers.add_parser("encrypt-model", help="Enkripsi model ONNX atau PyTorch (.pt)")
    enc_parser.add_argument("input", help="Path file model sumber (.onnx / .pt)")
    enc_parser.add_argument("output", help="Path output file terenkripsi (.enc)")
    enc_parser.add_argument("--key", required=True, help="Kunci rahasia enkripsi")
    enc_parser.add_argument("--hwid", default="*", help="Kunci ke HWID tertentu (* untuk universal)")

    # lease
    lease_parser = subparsers.add_parser("grant-lease", help="Terbitkan sewa offline darurat")
    lease_parser.add_argument("--hours", type=int, default=72, help="Jumlah jam sewa offline")

    subparsers.add_parser("revoke-lease", help="Cabut sewa offline lokal seketika")

    args = parser.parse_args()

    if args.command == "hwid":
        cmd_hwid(args)
    elif args.command == "status":
        cmd_status(args)
    elif args.command == "lock":
        cmd_toggle_lock(True)
    elif args.command == "unlock":
        cmd_toggle_lock(False)
    elif args.command == "list-devices":
        cmd_list_devices(args)
    elif args.command == "add-device":
        cmd_add_device(args)
    elif args.command == "revoke-device":
        cmd_set_device_status(args.hwid, "REVOKED")
    elif args.command == "activate-device":
        cmd_set_device_status(args.hwid, "ACTIVE")
    elif args.command == "remove-device":
        cmd_remove_device(args)
    elif args.command == "encrypt-model":
        cmd_encrypt_model(args)
    elif args.command == "grant-lease":
        cmd_grant_lease(args)
    elif args.command == "revoke-lease":
        cmd_revoke_lease(args)
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
