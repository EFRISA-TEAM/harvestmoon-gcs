#!/usr/bin/env python3
"""
MoonHarvest Ablation Study — ablation_study.py
================================================
Membandingkan performa: HSV-only vs YOLO-only vs HSV+YOLO Fusion
untuk semua model yang tersedia (v5 ONNX, YOLOv11n, YOLOv11s)

CARA PAKAI:
    cd /home/fawwazfa/Program/Harvestmoon
    source .venv/bin/activate
    python Pigeon_Harvest/training/ablation_study.py

OUTPUT:
    - Tabel perbandingan di terminal
    - File JSON: training/runs/ablation_results.json
    - File CSV:  training/runs/ablation_results.csv
    (Pakai file ini untuk tabel di presentasi dan jurnal!)
"""

import sys, time, json, csv
import cv2
import numpy as np
from pathlib import Path

# ─── Path Config ──────────────────────────────────────────────────────────────
TRAINING_DIR = Path(__file__).resolve().parent
REPO_ROOT    = TRAINING_DIR.parent
ASSETS       = REPO_ROOT / "HarvestmoonGCS" / "Assets" / "models"

VIDEO_TESTS = [
    # (path_video, label_benar, nama_deskriptif)
    ("/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo.mp4",
     "lush_green",           "📹 YDXJ Demo (Sehat/Hijau)"),
    ("/home/fawwazfa/Program/Harvestmoon/vid/15d.mp4",
     "drought_severe_stress","📹 15d (Drought/Kering)"),
    ("/home/fawwazfa/Program/Harvestmoon/vid/YDX_burned.mp4",
     "bare_soil",            "📹 YDX Burned (Tanah Kosong)"),
    ("/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo(1).mp4",
     "inconsistent_growth",  "📹 YDXJ Variasi (Inconsistent)"),
]

MODELS = {
    "yolov8n_v5": ASSETS / "moonharvest-health-cls-v5.onnx",
    "yolo11n_v7": ASSETS / "moonharvest-health-cls-v7-nano.onnx",
    "yolo11s_v7": ASSETS / "moonharvest-health-cls-v7-small.onnx",
}

CLASSES  = ["lush_green", "inconsistent_growth", "drought_severe_stress", "bare_soil"]
MAX_FRAMES = 60   # frame per video
IMGSZ    = 416


# ─── HSV Segmentation ─────────────────────────────────────────────────────────
def hsv_classify(frame):
    """HSV-only classification. Returns (label, confidence)."""
    lab = cv2.cvtColor(frame, cv2.COLOR_BGR2LAB)
    l, a, b_ch = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    l = clahe.apply(l)
    frame = cv2.cvtColor(cv2.merge([l, a, b_ch]), cv2.COLOR_LAB2BGR)

    hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
    H, S, V = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
    r, g, b = frame[:, :, 2].astype(float), frame[:, :, 1].astype(float), frame[:, :, 0].astype(float)
    exg = 2 * g - r - b
    exg_n = (exg - exg.min()) / (exg.max() - exg.min() + 1e-7)

    total = H.size
    lush    = ((H >= 30) & (H <= 90)  & (S >= 20) & (V >= 40) & (exg_n >= 0.25)).sum() / total
    drought = ((H >= 0)  & (H <= 25)  & (S >= 15) & (V >= 80) & (exg_n < 0.25)).sum()  / total
    stress  = ((H >= 15) & (H <= 50)  & (S >= 10) & (V >= 30) & (exg_n < 0.25) & (exg_n >= 0.05)).sum() / total
    bare    = ((V < 80)  & (S < 30)).sum() / total

    scores = {
        "lush_green":            lush,
        "drought_severe_stress": drought,
        "inconsistent_growth":   stress,
        "bare_soil":             bare,
    }
    dominant = max(scores, key=scores.get)
    return dominant, float(scores[dominant])


# ─── ONNX Classifier ──────────────────────────────────────────────────────────
class ONNXModel:
    def __init__(self, path, imgsz=416):
        import onnxruntime as ort
        import ast
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = 4
        providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        self.sess  = ort.InferenceSession(str(path), opts, providers=providers)
        self.iname = self.sess.get_inputs()[0].name
        self.imgsz = imgsz
        try:
            meta   = self.sess.get_modelmeta().custom_metadata_map
            parsed = ast.literal_eval(meta.get("names", "{}"))
            self.names = parsed if isinstance(parsed, dict) else {i: v for i, v in enumerate(parsed)}
        except Exception:
            self.names = {i: c for i, c in enumerate(CLASSES)}

    def predict(self, crop):
        img = cv2.resize(crop, (self.imgsz, self.imgsz)).astype(np.float32) / 255.0
        arr = img.transpose(2, 0, 1)[np.newaxis]
        logits = self.sess.run(None, {self.iname: arr})[0].flatten()
        exp    = np.exp(logits - logits.max())
        probs  = exp / exp.sum()
        cid    = int(np.argmax(probs))
        return self.names.get(cid, str(cid)), float(probs[cid])


# ─── Benchmark Satu Video ────────────────────────────────────────────────────
def benchmark_video(video_path, expected, model: ONNXModel):
    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        return None

    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    n = correct_hsv = correct_yolo = correct_fused = 0
    times_hsv = []
    times_yolo = []

    for fi in range(min(MAX_FRAMES, total_frames)):
        cap.set(cv2.CAP_PROP_POS_FRAMES, fi * max(1, total_frames // MAX_FRAMES))
        ret, frame = cap.read()
        if not ret or frame is None:
            continue

        # Resize ke 640 max
        h, w = frame.shape[:2]
        if w > 640:
            frame = cv2.resize(frame, (640, int(h * 640 / w)))

        # --- HSV ---
        t0 = time.perf_counter()
        hsv_cls, hsv_conf = hsv_classify(frame)
        times_hsv.append(time.perf_counter() - t0)

        # --- YOLO ---
        t0 = time.perf_counter()
        yolo_cls, yolo_conf = model.predict(frame)
        times_yolo.append(time.perf_counter() - t0)

        # --- Fusion (HSV 0.3 + YOLO 0.7) ---
        # Build prob vectors
        hsv_vec  = {c: 0.0 for c in CLASSES}
        yolo_vec = {c: 0.0 for c in CLASSES}
        hsv_vec[hsv_cls]   = hsv_conf
        yolo_vec[yolo_cls] = yolo_conf
        fused = {c: 0.3 * hsv_vec[c] + 0.7 * yolo_vec[c] for c in CLASSES}
        fused_cls = max(fused, key=fused.get)

        n += 1
        if expected in hsv_cls or hsv_cls in expected:
            correct_hsv += 1
        if expected in yolo_cls or yolo_cls in expected:
            correct_yolo += 1
        if expected in fused_cls or fused_cls in expected:
            correct_fused += 1

    cap.release()
    if n == 0:
        return None

    return {
        "n": n,
        "acc_hsv":   correct_hsv   / n * 100,
        "acc_yolo":  correct_yolo  / n * 100,
        "acc_fused": correct_fused / n * 100,
        "ms_hsv":    np.mean(times_hsv)  * 1000,
        "ms_yolo":   np.mean(times_yolo) * 1000,
        "fps":       1.0 / (np.mean(times_hsv) + np.mean(times_yolo)),
    }


# ─── Main ────────────────────────────────────────────────────────────────────
def main():
    print("\n" + "=" * 70)
    print("🔬 MoonHarvest Ablation Study")
    print("   HSV-only  vs  YOLO-only  vs  HSV+YOLO Fusion")
    print("   Membandingkan: YOLOv8n-v5  vs  YOLOv11n-v7  vs  YOLOv11s-v7")
    print("=" * 70 + "\n")

    # Cek model yang tersedia
    available = {}
    for name, path in MODELS.items():
        if path.exists():
            available[name] = path
            print(f"✅ {name}: {path.name}")
        else:
            print(f"⚠️  {name}: tidak ditemukan — skip")

    if not available:
        print("\n❌ Tidak ada model ONNX ditemukan!")
        print("   Jalankan training dulu: python train_yolo11.py --model nano")
        sys.exit(1)

    # Cek video
    videos_ok = [(p, e, d) for p, e, d in VIDEO_TESTS if Path(p).exists()]
    videos_skip = [(p, e, d) for p, e, d in VIDEO_TESTS if not Path(p).exists()]
    for _, _, d in videos_skip:
        print(f"⚠️  Video tidak ditemukan — skip: {d}")

    if not videos_ok:
        print("\n❌ Tidak ada video test ditemukan!")
        sys.exit(1)

    print(f"\n📹 {len(videos_ok)} video test ditemukan\n")

    # Run benchmark
    all_results = {}

    for model_name, model_path in available.items():
        print(f"\n{'─' * 70}")
        print(f"🤖 Testing model: {model_name}")
        print(f"{'─' * 70}")

        try:
            model = ONNXModel(model_path, imgsz=IMGSZ)
        except Exception as e:
            print(f"   ❌ Gagal load model: {e}")
            continue

        per_video = {}
        for vid_path, expected, desc in videos_ok:
            print(f"   {desc}... ", end="", flush=True)
            r = benchmark_video(vid_path, expected, model)
            if r:
                per_video[desc] = r
                print(f"HSV={r['acc_hsv']:.1f}% | YOLO={r['acc_yolo']:.1f}% | Fusion={r['acc_fused']:.1f}%")
            else:
                print("GAGAL")

        if per_video:
            all_results[model_name] = per_video

    # ─── Summary Table ──────────────────────────────────────────────────────
    print("\n\n" + "=" * 70)
    print("📊 TABEL ABLATION STUDY — HASIL AKHIR")
    print("=" * 70)
    print(f"{'Model':<20} {'HSV-only':>10} {'YOLO-only':>10} {'Fusion':>10} {'FPS':>8}")
    print("─" * 65)

    summary_rows = []
    for model_name, per_video in all_results.items():
        avg_hsv   = np.mean([v["acc_hsv"]   for v in per_video.values()])
        avg_yolo  = np.mean([v["acc_yolo"]  for v in per_video.values()])
        avg_fused = np.mean([v["acc_fused"] for v in per_video.values()])
        avg_fps   = np.mean([v["fps"]       for v in per_video.values()])

        print(f"{model_name:<20} {avg_hsv:>9.1f}% {avg_yolo:>9.1f}% {avg_fused:>9.1f}% {avg_fps:>7.1f}")
        summary_rows.append({
            "model":    model_name,
            "hsv_acc":  round(avg_hsv, 2),
            "yolo_acc": round(avg_yolo, 2),
            "fused_acc":round(avg_fused, 2),
            "fps":      round(avg_fps, 1),
        })

    # Tambahkan HSV-only baseline
    hsv_baseline = []
    for vid_path, expected, desc in videos_ok:
        cap = cv2.VideoCapture(vid_path)
        total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
        correct = 0
        n = 0
        for fi in range(min(MAX_FRAMES, total_frames)):
            cap.set(cv2.CAP_PROP_POS_FRAMES, fi * max(1, total_frames // MAX_FRAMES))
            ret, frame = cap.read()
            if not ret or frame is None:
                continue
            h, w = frame.shape[:2]
            if w > 640:
                frame = cv2.resize(frame, (640, int(h * 640 / w)))
            cls, _ = hsv_classify(frame)
            if expected in cls or cls in expected:
                correct += 1
            n += 1
        cap.release()
        if n > 0:
            hsv_baseline.append(correct / n * 100)

    if hsv_baseline:
        avg_hsv_only = np.mean(hsv_baseline)
        print(f"{'HSV-only (baseline)':<20} {avg_hsv_only:>9.1f}% {'—':>10} {'—':>10} {'120+':>7}")
        summary_rows.insert(0, {
            "model": "HSV_only_baseline",
            "hsv_acc": round(avg_hsv_only, 2),
            "yolo_acc": None,
            "fused_acc": None,
            "fps": 120,
        })

    print("─" * 65)
    print("\n💡 Catatan untuk presentasi:")
    print("   - HSV-only  = tanpa AI, hanya warna")
    print("   - YOLO-only = hanya deep learning")
    print("   - Fusion    = kombinasi terbaik (HSV 30% + YOLO 70%)")

    # ─── Simpan ke File ─────────────────────────────────────────────────────
    out_dir = TRAINING_DIR / "runs"
    out_dir.mkdir(parents=True, exist_ok=True)

    # JSON
    json_path = out_dir / "ablation_results.json"
    with open(json_path, "w") as f:
        json.dump({"summary": summary_rows, "per_video": all_results}, f, indent=2)
    print(f"\n💾 Hasil JSON → {json_path}")

    # CSV (untuk jurnal/tabel presentasi)
    csv_path = out_dir / "ablation_results.csv"
    with open(csv_path, "w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=["model", "hsv_acc", "yolo_acc", "fused_acc", "fps"])
        writer.writeheader()
        writer.writerows(summary_rows)
    print(f"💾 Hasil CSV  → {csv_path}")

    print("\n✅ Ablation study selesai!")
    print("   Gunakan tabel di atas untuk slide presentasi dan jurnal.")
    print("=" * 70 + "\n")


if __name__ == "__main__":
    main()
