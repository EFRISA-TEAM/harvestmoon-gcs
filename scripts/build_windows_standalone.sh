#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
output_dir="$repo_dir/dist/MoonHarvest-GCS-Windows"
cd "$repo_dir/Pigeon_Harvest"
if [[ ! -f HarvestmoonGCS/PortableRuntime/python/python.exe || ! -f HarvestmoonGCS/PortableRuntime/ffmpeg.exe ]]; then
  echo 'Prepare the bundled Windows runtime first: bash scripts/prepare_windows_python.sh' >&2
  exit 1
fi
# MSBuild compares directory names without case sensitivity, even on Linux.
mkdir -p HarvestmoonGCS/bin/Release/net9.0-desktop/win-x64/Assets/{icons,Icons}

dotnet publish HarvestmoonGCS/HarvestmoonGCS.csproj \
  -c Release -f net9.0-desktop -r win-x64 --self-contained true \
  -p:DesktopOnly=true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -p:PublishTrimmed=false \
  -p:DebugType=embedded -p:DebugSymbols=true \
  -o "$output_dir"

mv -f "$output_dir/HarvestmoonGCS.exe" "$output_dir/MoonHarvest.exe"
rm -f "$output_dir"/*.pdb
printf 'EXE: %s\n' "$output_dir/MoonHarvest.exe"
