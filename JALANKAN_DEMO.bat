@echo off
setlocal
cd /d "%~dp0"

echo ============================================================
echo   MOONHARVEST GCS - Windows Launcher
echo ============================================================

:: Auto-start VIRA Lite jika ada di folder bin
if exist "bin\vira-lite.exe" (
    tasklist /FI "IMAGENAME eq vira-lite.exe" 2>NUL | find /I /N "vira-lite.exe">NUL
    if "%ERRORLEVEL%"=="1" (
        echo [VIRA] Menjalankan VIRA Lite Agent di latar belakang...
        start "" /B "bin\vira-lite.exe"
    )
)

:: Jalankan GCS executable atau python stream
if exist "Pigeon_Harvest\HarvestmoonGCS\bin\Release\net9.0-desktop\HarvestmoonGCS.exe" (
    start "" "Pigeon_Harvest\HarvestmoonGCS\bin\Release\net9.0-desktop\HarvestmoonGCS.exe"
) else (
    python moonharvest_detect_stream.py %*
)
pause
