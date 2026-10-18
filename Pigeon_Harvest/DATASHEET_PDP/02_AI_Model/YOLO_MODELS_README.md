# YOLO Models for MoonHarvest

Folder ini berisi model YOLO dan class-name yang dipakai oleh `HarvestFunctionalService` untuk deteksi objek offline pada live camera dan image analysis.

## File yang sudah tersedia

- `yolov8n.onnx` (~12 MB): bobot resmi YOLOv8n 640x640 (pre-trained COCO, 80 kelas). Siap dipakai langsung.
- Opsional untuk Android/tablet: `yolov8n-agri-320.onnx` atau `yolov8n-320.onnx`. Jika salah satu file ini ada, aplikasi Android akan memilihnya lebih dulu.
- `classes-yolov8n-coco.txt`: daftar 80 kelas COCO untuk `yolov8n.onnx`.
- `classes-yolov8n-agri-basic.txt`: daftar 12 kelas agriculture (healthy_crop, crop_stress, dry_soil, water_stress, dst) untuk model custom agri.
- `coco.names`, `vegetation.names`: arsip label lama, di-preserve untuk referensi.

## Cara kerja auto-load

`HarvestFunctionalService.EnsureInitialized` mencoba model berikut secara berurutan dan memakai pasangan pertama yang ada:

Android/tablet mencoba pasangan ini lebih dulu:

1. `Assets/models/yolov8n-agri-320.onnx` + `classes-yolov8n-agri-basic.txt`
2. `Assets/models/yolov8n-320.onnx` + `classes-yolov8n-coco.txt`

Semua platform lalu fallback ke:

1. `Assets/models/yolov8n-agri.onnx` + `classes-yolov8n-agri-basic.txt`
2. `Assets/models/yolov8n.onnx` + `classes-yolov8n-coco.txt`
3. `yolov8n-agri.onnx` (base dir fallback)
4. `yolov8n.onnx` (base dir fallback)

Default yang aktif setelah build bersih: **yolov8n.onnx + classes-yolov8n-coco.txt**.
YOLO langsung aktif begitu toggle "Yolo" di sidebar dinyalakan dan camera feed masuk.

## Cara mengganti model

### Opsi A - Drop-in model agri custom

Taruh file berikut di folder ini (overwrite jika perlu):

- `yolov8n-agri.onnx` (model custom crop/pest)
- `classes-yolov8n-agri-basic.txt` (sudah ada; edit sesuai label model anda)

Rebuild atau restart aplikasi. `HarvestFunctionalService` otomatis pilih pasangan ini.

### Opsi B - Runtime switching via AISettingsPage

Buka menu **AI Settings** di sidebar. Pilih model `.onnx`, class file, confidence threshold, dan NMS threshold dari UI. Perubahan disimpan tanpa rebuild.

## Rekomendasi performance

- YOLOv8n input 640x640 berjalan ~15-30 FPS di CPU modern (Ryzen 5 / i5 ke atas).
- Untuk Android / perangkat low-end seperti Realme Pad Mini, pakai model 320x320 atau INT8. Target file yang direkomendasikan: `yolov8n-320.onnx` untuk COCO, atau `yolov8n-agri-320.onnx` untuk model agriculture custom.
- Live detection Android menjalankan YOLO tiap 8 frame sekali. Tujuh frame berikutnya memakai bounding box terakhir agar preview lebih ringan.
- Default Android performance mode: confidence `0.4`, NMS `0.4`, dan maksimal 20 box setelah NMS.
- Aktifkan CUDA otomatis jika tersedia (Windows + GPU NVIDIA + ONNX Runtime GPU build).
- Frame skipping dikontrol oleh `DashboardPage.AnalyzeDashboardFrameAsync` (default 2 detik per analisis). Turunkan untuk throughput lebih tinggi.
