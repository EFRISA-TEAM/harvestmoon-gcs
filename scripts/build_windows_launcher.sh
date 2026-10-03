#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
launcher_cc="${LAUNCHER_CC:-x86_64-w64-mingw32-gcc}"
launcher_rc="${LAUNCHER_RC:-x86_64-w64-mingw32-windres}"
output_dir="$repo_dir/dist/MoonHarvest-Launcher"
mkdir -p "$output_dir"
cd "$repo_dir/tools/windows-launcher"
python3 - <<'PY'
import hashlib, pathlib, re
payload = pathlib.Path('../../dist/MoonHarvest-GCS-Windows/MoonHarvest.exe')
expected = re.search(r'PAYLOAD_SHA256 L"([a-f0-9]{64})"', pathlib.Path('payload.h').read_text()).group(1)
assert hashlib.sha256(payload.read_bytes()).hexdigest() == expected, 'Payload changed: update payload.h and release URL before rebuilding'
PY
"$launcher_rc" launcher.rc -o "$output_dir/launcher-res.o"
"$launcher_cc" -Os -s -municode -mwindows -Wall -Wextra -Werror \
  launcher.c "$output_dir/launcher-res.o" -o "$output_dir/MoonHarvest.exe" \
  -lwinhttp -lbcrypt -lshell32
rm "$output_dir/launcher-res.o"
python3 - "$output_dir/MoonHarvest.exe" <<'PY'
import pathlib, sys
p = pathlib.Path(sys.argv[1])
assert p.stat().st_size <= 20_000_000, 'Launcher exceeds 20 MB'
print(f'{p}: {p.stat().st_size:,} bytes')
PY
