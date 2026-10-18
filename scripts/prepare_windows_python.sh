#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
runtime_dir="$repo_dir/Pigeon_Harvest/HarvestmoonGCS/PortableRuntime"
archive="$(mktemp --suffix=.zip)"
trap 'rm -f "$archive"' EXIT
curl --fail --location --retry 2 https://www.python.org/ftp/python/3.11.9/python-3.11.9-embed-amd64.zip -o "$archive"
python3 - "$archive" "$runtime_dir" <<'PY'
import hashlib, pathlib, sys, zipfile
archive, root = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
assert hashlib.sha256(archive.read_bytes()).hexdigest() == '009d6bf7e3b2ddca3d784fa09f90fe54336d5b60f0e0f305c37f400bf83cfd3b'
python = root / 'python'
if python.exists():
    raise SystemExit('Runtime already exists; use a clean staging directory before preparing it again.')
python.mkdir(parents=True)
with zipfile.ZipFile(archive) as z:
    z.extractall(python)
(python / 'python311._pth').write_text('python311.zip\n.\nLib/site-packages\n..\nimport site\n')
PY
pip_options=(--target "$runtime_dir/python/Lib/site-packages" --platform win_amd64 --python-version 3.11 --implementation cp --only-binary=:all: --no-compile --no-cache-dir --no-deps)
# Explicit Windows dependency lock avoids pip evaluating Linux-only CUDA markers.
python3 -m pip install "${pip_options[@]}" -r "$repo_dir/tools/windows-launcher/python-requirements.txt"
python3 -m pip install "${pip_options[@]}" --index-url https://download.pytorch.org/whl/cpu 'torch==2.5.1+cpu' 'torchvision==0.20.1+cpu'
python3 - "$runtime_dir" <<'PY'
import pathlib, shutil, sys
root = pathlib.Path(sys.argv[1])
source = next((root / 'python/Lib/site-packages/imageio_ffmpeg/binaries').glob('*.exe'))
shutil.copyfile(source, root / 'ffmpeg.exe')
PY
