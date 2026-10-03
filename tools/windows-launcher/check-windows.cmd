@echo off
setlocal
if not exist "%~dp0MoonHarvest.exe" (
  echo Letakkan file ini bersama launcher MoonHarvest.exe.
  pause
  exit /b 1
)
echo Memeriksa runtime MoonHarvest. Tunggu hingga selesai...
start /wait "" "%~dp0MoonHarvest.exe" --check-runtime
echo Hasil tersimpan sebagai MoonHarvest-check.json di Desktop.
echo Pemeriksaan ini belum menguji tampilan, kamera fisik, atau koneksi drone.
pause
