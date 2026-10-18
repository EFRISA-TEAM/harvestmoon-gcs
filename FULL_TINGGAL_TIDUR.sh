#!/usr/bin/env bash
# =============================================================================
# MOONHARVEST - FULL PIPELINE (SESUAI PROPOSAL PDP)
# Script ini dirancang agar 100% otomatis (Tinggal Tidur)
# =============================================================================

set -e

MOONHARVEST="/home/fawwazfa/Program/Harvestmoon"
TRAINING_DIR="$MOONHARVEST/Pigeon_Harvest/training"
OLD_DATASET="$TRAINING_DIR/datasets/moonharvest-uav/classifier_v4_teknofest"
NEW_DATASET="$TRAINING_DIR/datasets/moonharvest-uav/dataset_proposal"
ASSETS_DIR="$MOONHARVEST/Pigeon_Harvest/HarvestmoonGCS/Assets/models"

echo "======================================================================="
echo "🌾 MOONHARVEST PIPELINE OTOMATIS: MODE PROPOSAL PDP"
echo "======================================================================="
echo "Waktu mulai: $(date)"
echo ""

cd "$MOONHARVEST"

# 1. AKTIVASI ENVIRONMENT & UPDATE
echo "[1/5] Mempersiapkan Environment & YOLOv11..."
if [ -f ".venv/bin/activate" ]; then
    source .venv/bin/activate
fi
pip install ultralytics --upgrade --quiet
echo "✅ Ultralytics YOLOv11 siap."
echo ""

# 2. SETUP DATASET BERSIH SESUAI ISTILAH PROPOSAL
echo "[2/5] Menyiapkan Dataset Proposal PDP Bebas Duplikat & Kontaminasi..."
python3 "$TRAINING_DIR/build_proposal_dataset_clean.py"
echo ""

# 3. TRAINING YOLOv11 NANO OPTIMAL (TARGET >90% AKURASI SEMUA KELAS)
echo "[3/5] Memulai Training YOLOv11 Nano Opsi 1 (Target Akurasi >90%)..."
echo "Parameter: Optimizer=AdamW, cos_lr=True, degrees=15, flipud=0.5, batch=32, device=0"
echo "Output log akan langsung terlihat di bawah ini:"
echo "-----------------------------------------------------------------------"

yolo classify train \
    data="$NEW_DATASET" \
    model="yolo11n-cls.pt" \
    epochs=100 \
    imgsz=224 \
    batch=32 \
    workers=4 \
    optimizer=AdamW \
    lr0=0.001 \
    cos_lr=True \
    degrees=15 \
    flipud=0.5 \
    fliplr=0.5 \
    scale=0.3 \
    hsv_s=0.4 \
    hsv_v=0.3 \
    erasing=0.1 \
    patience=30 \
    project="$TRAINING_DIR/runs" \
    name="yolo11n_proposal_pdp" \
    exist_ok=True \
    device=0

echo "-----------------------------------------------------------------------"
echo "✅ Training Selesai!"
echo ""

echo "📊 EVALUASI PER KELAS MODEL TERBAIK..."
python3 -c "
from ultralytics import YOLO
import numpy as np

model = YOLO('$TRAINING_DIR/runs/yolo11n_proposal_pdp/weights/best.pt')
res = model.val(data='$NEW_DATASET')
print('=' * 65)
print('HASIL VALIDASI PROPOSAL PDP YOLO11 NANO:')
print(f'Top-1 Accuracy Global: {res.top1 * 100:.2f}%')
cm = res.confusion_matrix.matrix
classes = ['0_zona_normal', '1_ketidakmerataan_irigasi', '2_defisit_air_kanopi', '3_defisit_air_tanah']
col_sums = cm.sum(axis=0)
for idx, cname in enumerate(classes):
    correct = cm[idx, idx]
    total = col_sums[idx]
    pct = (correct / total * 100) if total > 0 else 0
    print(f'  {cname:<25}: {pct:.2f}% ({int(correct)}/{int(total)})')
print('=' * 65)
"
echo ""

# 4. EXPORT KE ONNX
echo "[4/5] Meng-export model ke format ONNX (Untuk GCS)..."
BEST_MODEL="$TRAINING_DIR/runs/yolo11n_proposal_pdp/weights/best.pt"

if [ -f "$BEST_MODEL" ]; then
    yolo export model="$BEST_MODEL" format=onnx imgsz=224 simplify=True
    echo "✅ Export ONNX Selesai."
else
    echo "❌ Gagal: best.pt tidak ditemukan!"
    exit 1
fi
echo ""

# 5. COPY KE ASSETS GCS
echo "[5/5] Menyalin model ke GCS Assets..."
mkdir -p "$ASSETS_DIR"
ONNX_FILE="$TRAINING_DIR/runs/yolo11n_proposal_pdp/weights/best.onnx"
if [ -f "$ONNX_FILE" ]; then
    cp "$ONNX_FILE" "$ASSETS_DIR/yolo11n_proposal_pdp.onnx"
    cp "$ONNX_FILE" "$ASSETS_DIR/moonharvest-health-cls.onnx"
    cat << 'EOF' > "$ASSETS_DIR/classes-moonharvest-health.txt"
0_zona_normal
1_ketidakmerataan_irigasi
2_defisit_air_kanopi
3_defisit_air_tanah
EOF
    echo "✅ Model tersimpan di: $ASSETS_DIR/yolo11n_proposal_pdp.onnx"
    echo "✅ Primary ONNX diperbarui di: $ASSETS_DIR/moonharvest-health-cls.onnx"
    echo "✅ Classes diperbarui di: $ASSETS_DIR/classes-moonharvest-health.txt"
fi

echo ""
echo "======================================================================="
echo "🎉 PIPELINE SELESAI DENGAN SUKSES!"
echo "Waktu selesai: $(date)"
echo "Anda bisa langsung menggunakan model ini di GCS besok pagi."
echo "======================================================================="
