@echo off
setlocal
cd /d "%~dp0"

echo ======================================================================
echo             MOONHARVEST GROUND CONTROL STATION (GCS)
echo       Sistem Pemantauan Defisit Air ^& Kesehatan Tanaman Padi
echo           Dual Mode: Real Flight Operation ^& Riset Demo
echo ======================================================================
echo.

:: Auto-start VIRA Lite jika ada di folder bin
if exist "bin\vira-lite.exe" (
    tasklist /FI "IMAGENAME eq vira-lite.exe" 2>NUL | find /I /N "vira-lite.exe">NUL
    if "%ERRORLEVEL%"=="1" (
        echo [VIRA] Menjalankan VIRA Lite Agent di latar belakang...
        start "" /B "bin\vira-lite.exe"
    )
)

echo Pilih Mode Operasi:
echo   [1] Mode Demo / Presentasi (Skenario PDP SINTA 2 Otomatis)
echo   [2] Mode Real Flight (Koneksi UAV MAVLink COM Port / UDP 14550)
echo   [3] Jalankan Python YOLO Stream Detector
echo   [0] Keluar
echo.

set /p PILIHAN="Masukkan pilihan [1/2/3/0] (default: 1): "
if "%PILIHAN%"=="" set PILIHAN=1

if "%PILIHAN%"=="1" goto DEMO
if "%PILIHAN%"=="2" goto FLIGHT
if "%PILIHAN%"=="3" goto YOLO
if "%PILIHAN%"=="0" goto END

:DEMO
echo.
echo [DEMO] Menjalankan MoonHarvest GCS Mode Demo...
start "" "Pigeon_Harvest\HarvestmoonGCS\bin\Release\net9.0-desktop\HarvestmoonGCS.exe"
goto END

:FLIGHT
echo.
echo [REAL FLIGHT] Menjalankan MoonHarvest GCS Mode Real Flight...
echo Pastikan telemetri USB / radio MAVLink terhubung ke komputer.
start "" "Pigeon_Harvest\HarvestmoonGCS\bin\Release\net9.0-desktop\HarvestmoonGCS.exe"
goto END

:YOLO
echo.
echo [YOLO] Menjalankan Standalone Python YOLO Detector...
python moonharvest_detect_stream.py
goto END

:END
pause
