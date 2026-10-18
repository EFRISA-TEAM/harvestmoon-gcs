#!/usr/bin/env bash
# =============================================================================
# MoonHarvest — PANDUAN PENGERJAAN MANDIRI
# Jalankan script ini untuk melihat semua perintah yang perlu dikerjakan
# =============================================================================
#
# CARA BUKA:
#   cat /home/fawwazfa/Program/Harvestmoon/KERJA_SENDIRI.sh
#
# ATAU LANGSUNG JALANKAN STEP PER STEP:
#   bash /home/fawwazfa/Program/Harvestmoon/KERJA_SENDIRI.sh
# =============================================================================

set -e
MOONHARVEST="/home/fawwazfa/Program/Harvestmoon"

echo ""
echo "╔══════════════════════════════════════════════════════════════╗"
echo "║     🌾 MoonHarvest — Panduan Pengerjaan Mandiri              ║"
echo "║     Roadmap: Peringkat 9 → Juara 1 TEKNOFEST 2027           ║"
echo "╚══════════════════════════════════════════════════════════════╝"
echo ""
echo "Pilih langkah yang ingin dijalankan:"
echo ""
echo "  [1] Setup & Cek Environment"
echo "  [2] Audit Dataset (Proposal PDP 4-Kelas)"
echo "  [3] Training YOLOv11 Nano (untuk Android)"
echo "  [4] Training YOLOv11 Small (untuk Desktop)"
echo "  [5] Ablation Study (bandingan HSV vs YOLO vs Fusion)"
echo "  [6] Benchmark Model (ukur FPS & akurasi)"
echo "  [7] Jalankan MoonHarvest GCS Desktop"
echo "  [8] Full Pipeline Tinggal Tidur (Otomatis Semuanya)"
echo "  [0] Keluar"
echo ""
echo -n "Pilih [0-8]: "
read PILIH

cd "$MOONHARVEST"

# Aktivasi virtual environment
if [ -f ".venv/bin/activate" ]; then
    source .venv/bin/activate
    echo "✅ Virtual environment aktif"
else
    echo "⚠️  .venv tidak ditemukan, pakai Python system"
fi

case "$PILIH" in

# ─────────────────────────────────────────────────────────────────
1)
    echo ""
    echo "═══════════════════════════════════════"
    echo "🔍 STEP 1: Setup & Cek Environment"
    echo "═══════════════════════════════════════"
    echo ""

    echo "── Python version ──"
    python3 --version

    echo ""
    echo "── GPU check ──"
    python3 -c "
import torch
if torch.cuda.is_available():
    print(f'✅ GPU: {torch.cuda.get_device_name(0)}')
    print(f'   VRAM: {torch.cuda.get_device_properties(0).total_memory/1024**3:.1f} GB')
else:
    print('⚠️  GPU tidak tersedia — training akan lambat')
"

    echo ""
    echo "── Ultralytics version ──"
    python3 -c "
import ultralytics
print(f'Ultralytics: {ultralytics.__version__}')
major, minor = map(int, ultralytics.__version__.split('.')[:2])
if major > 8 or (major == 8 and minor >= 3):
    print('✅ Versi mendukung YOLOv11')
    try:
        from ultralytics import YOLO
        m = YOLO('yolo11n-cls.pt')
        print('✅ YOLOv11n berhasil dimuat!')
    except Exception as e:
        print(f'⚠️  {e}')
else:
    print('❌ Perlu upgrade: pip install ultralytics --upgrade')
" 2>/dev/null || echo "❌ Ultralytics belum terinstall: pip install ultralytics"

    echo ""
    echo "── Update ultralytics ke versi terbaru? ──"
    echo -n "Update sekarang? (y/n): "
    read UPDATE_ANS
    if [ "$UPDATE_ANS" = "y" ]; then
        pip install ultralytics --upgrade --quiet
        echo "✅ Ultralytics diupdate"
    fi

    echo ""
    echo "✅ Cek environment selesai!"
    ;;

# ─────────────────────────────────────────────────────────────────
2)
    echo ""
    echo "═══════════════════════════════════════"
    echo "📊 STEP 2: Audit Dataset"
    echo "═══════════════════════════════════════"
    echo ""
    echo "Mengecek distribusi kelas, gambar rusak, dan duplikat..."
    echo ""
    python3 Pigeon_Harvest/training/audit_dataset.py
    ;;

# ─────────────────────────────────────────────────────────────────
3)
    echo ""
    echo "═══════════════════════════════════════"
    echo "🚀 STEP 3: Training YOLOv11 NANO"
    echo "   (Untuk Android — kecil & cepat)"
    echo "═══════════════════════════════════════"
    echo ""
    echo "⏱️  Estimasi waktu: 3-5 jam (GPU RTX 3050)"
    echo "   Training bisa dibiarkan berjalan di background."
    echo ""
    echo -n "Mulai training? (y/n): "
    read TRAIN_ANS
    if [ "$TRAIN_ANS" = "y" ]; then
        python3 Pigeon_Harvest/training/train_yolo11.py --model nano
    else
        echo "Training dibatalkan."
    fi
    ;;

# ─────────────────────────────────────────────────────────────────
4)
    echo ""
    echo "═══════════════════════════════════════"
    echo "🚀 STEP 4: Training YOLOv11 SMALL"
    echo "   (Untuk Desktop — lebih akurat)"
    echo "═══════════════════════════════════════"
    echo ""
    echo "⏱️  Estimasi waktu: 6-9 jam (GPU RTX 3050)"
    echo ""
    echo -n "Mulai training? (y/n): "
    read TRAIN_ANS
    if [ "$TRAIN_ANS" = "y" ]; then
        python3 Pigeon_Harvest/training/train_yolo11.py --model small
    else
        echo "Training dibatalkan."
    fi
    ;;

# ─────────────────────────────────────────────────────────────────
5)
    echo ""
    echo "═══════════════════════════════════════"
    echo "🔬 STEP 5: Ablation Study"
    echo "   Bandingan: HSV vs YOLO vs Fusion"
    echo "   Bandingan: YOLOv8n vs YOLOv11n vs YOLOv11s"
    echo "═══════════════════════════════════════"
    echo ""
    echo "⚠️  Pastikan sudah training YOLOv11 dulu (Step 3 atau 4)"
    echo ""
    python3 Pigeon_Harvest/training/ablation_study.py
    ;;

# ─────────────────────────────────────────────────────────────────
6)
    echo ""
    echo "═══════════════════════════════════════"
    echo "📈 STEP 6: Benchmark Model"
    echo "   Ukur FPS & akurasi model saat ini"
    echo "═══════════════════════════════════════"
    echo ""
    python3 Pigeon_Harvest/training/benchmark_v5.py
    ;;

7)
    echo ""
    echo "═══════════════════════════════════════"
    echo "🖥️  STEP 7: Jalankan MoonHarvest GCS Desktop"
    echo "═══════════════════════════════════════"
    echo ""
    echo "Menjalankan GCS Desktop..."
    cd "$MOONHARVEST/Pigeon_Harvest"
    dotnet run --project HarvestmoonGCS/HarvestmoonGCS.csproj -f net9.0-desktop
    ;;

8)
    echo ""
    echo "═══════════════════════════════════════"
    echo "🌙 STEP 8: Full Pipeline Tinggal Tidur"
    echo "═══════════════════════════════════════"
    echo ""
    bash "$MOONHARVEST/FULL_TINGGAL_TIDUR.sh"
    ;;

0)
    echo "Keluar."
    exit 0
    ;;

*)
    echo "Pilihan tidak valid."
    exit 1
    ;;
esac

echo ""
echo "─────────────────────────────────────────────────"
echo "Selesai! Jalankan lagi untuk langkah berikutnya:"
echo "  bash $MOONHARVEST/KERJA_SENDIRI.sh"
echo "─────────────────────────────────────────────────"
