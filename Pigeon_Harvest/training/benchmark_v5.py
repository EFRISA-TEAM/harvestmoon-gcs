#!/usr/bin/env python3
"""
MoonHarvest Model v5 Benchmark
Cek FPS dan akurasi gabungan HSV + ONNX classifier v5
"""
import sys, time, cv2, ast, json, os
import numpy as np
from pathlib import Path

sys.path.insert(0, "/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/skrip")

# ── Config ────────────────────────────────────────────────────────────────────
MODEL_V5  = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models/moonharvest-health-cls-v5.onnx"
MODEL_OLD = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models/moonharvest-health-cls.onnx"
VIDEOS = [
    ("/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo.mp4",   "lush_green",           "Demo field (sehat)"),
    ("/home/fawwazfa/Program/Harvestmoon/vid/15d.mp4",             "drought_severe_stress","15d (drought)"),
    ("/home/fawwazfa/Program/Harvestmoon/vid/YDX_burned.mp4",      "bare_soil",            "Burned (bare soil)"),
    ("/home/fawwazfa/Program/Harvestmoon/vid/YDXJ0012_demo(1).mp4","inconsistent_growth",  "Demo variasi"),
]
CLASSES_V5  = ["lush_green","inconsistent_growth","drought_severe_stress","bare_soil"]
IMGSZ       = 416
MAX_FRAMES  = 50   # benchmark 50 frame per video

# ── HSV Config (dari moonharvest_detect_stream.py) ────────────────────────────
HSV_CFG = {
    "wb": True, "clahe_clip": 2.0, "clahe_grid": 8, "shadow_v_max": 35,
    "exg_healthy_min": 0.028,
    "lush":    {"h": [30, 90], "s_lo": 25, "v_lo": 50, "exg_min": 0.028},
    "stress":  {"h": [15, 45], "s_lo": 20, "v_lo": 40, "exg_lo": 0.0, "exg_hi": 0.028},
    "drought": {"h": [0, 18],  "s_lo": 18, "s_hi": 60, "v_lo": 120, "exg_hi": 0.003,
                "exg_remap_thr": 0.006},
    "min_area_px": 300, "max_area_frac": 0.20,
    "ema_alpha": 0.35,
}

ONNX_MAP = {
    "lush_green": "Lush Green",
    "inconsistent_growth": "Inconsistent Growth",
    "drought_severe_stress": "Drought / Severe Stress",
    "bare_soil": "Bare Soil / Gap",
    "healthy_crop": "Lush Green",
    "stressed_crop": "Inconsistent Growth",
    "drought_stress": "Drought / Severe Stress",
}

def log(msg): print(msg, flush=True)

# ── ONNX Inference ────────────────────────────────────────────────────────────
class ONNXClassifier:
    def __init__(self, path, imgsz=416):
        import onnxruntime as ort
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = 4
        opts.inter_op_num_threads = 2
        providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
        self.sess  = ort.InferenceSession(path, opts, providers=providers)
        self.iname = self.sess.get_inputs()[0].name
        self.imgsz = imgsz
        # Load class names from model metadata
        try:
            meta   = self.sess.get_modelmeta().custom_metadata_map
            raw    = meta.get("names", "{}")
            parsed = ast.literal_eval(raw)
            self.names = parsed if isinstance(parsed, dict) else {i:v for i,v in enumerate(parsed)}
        except:
            self.names = {i:c for i,c in enumerate(CLASSES_V5)}
        log(f"  [ONNX] Loaded: {Path(path).name} | classes={self.names} | provider={self.sess.get_providers()[0]}")

    def infer_single(self, crop):
        img = cv2.resize(crop, (self.imgsz, self.imgsz)).astype(np.float32) / 255.0
        arr = img.transpose(2,0,1)[np.newaxis]
        logits = self.sess.run(None, {self.iname: arr})[0]
        logits = logits.flatten()   # pastikan 1-D
        exp    = np.exp(logits - logits.max())
        probs  = exp / exp.sum()
        cid    = int(np.argmax(probs))
        return self.names.get(cid, str(cid)), float(probs[cid])

# ── Simple HSV Segmentation ───────────────────────────────────────────────────
def hsv_classify_frame(frame):
    """
    Klasifikasikan seluruh frame dengan HSV → return label + confidence dominan.
    Sama seperti pipeline di moonharvest_detect_stream.py.
    """
    h, w = frame.shape[:2]

    # CLAHE white balance
    lab = cv2.cvtColor(frame, cv2.COLOR_BGR2LAB)
    l,a,b_ch = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8,8))
    l = clahe.apply(l)
    frame = cv2.cvtColor(cv2.merge([l,a,b_ch]), cv2.COLOR_LAB2BGR)

    hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
    H,S,V = hsv[:,:,0], hsv[:,:,1], hsv[:,:,2]
    rr = frame[:,:,2].astype(float)
    gg = frame[:,:,1].astype(float)
    bb = frame[:,:,0].astype(float)
    exg = 2*gg - rr - bb
    exg_n = (exg - exg.min()) / (exg.max() - exg.min() + 1e-7)

    total_px = h * w

    # Lush green — exg tinggi, hijau
    lush    = (H>=30) & (H<=90) & (S>=20) & (V>=40) & (exg_n>=0.25)
    # Drought — kuning/coklat kering
    drought = (H>=0)  & (H<=25) & (S>=15) & (V>=80) & (exg_n<0.25) & ~lush
    # Stress — antara lush dan drought
    stress  = (H>=15) & (H<=50) & (S>=10) & (V>=30) & ~lush & ~drought
    # Bare soil — gelap, sangat sedikit warna
    bare    = (V<80) & (S<30) & ~lush & ~drought & ~stress

    scores = {
        "lush_green":            lush.sum()    / total_px,
        "drought_severe_stress": drought.sum() / total_px,
        "inconsistent_growth":   stress.sum()  / total_px,
        "bare_soil":             bare.sum()     / total_px,
    }
    dominant = max(scores, key=scores.get)
    conf = float(scores[dominant])

    # Buat regions untuk ONNX (grid 2x2)
    regions = []
    gh, gw = h//2, w//2
    zone_map = {
        "lush_green": "lush", "drought_severe_stress": "drought",
        "inconsistent_growth": "stress", "bare_soil": "soil"
    }
    display_map = {
        "lush_green": "Lush Green", "drought_severe_stress": "Drought / Severe Stress",
        "inconsistent_growth": "Inconsistent Growth", "bare_soil": "Bare Soil / Gap"
    }
    for gi in range(2):
        for gj in range(2):
            x1,y1 = gj*gw, gi*gh
            x2,y2 = x1+gw, y1+gh
            # Tentukan kelas dominan untuk cell ini
            cell_scores = {}
            for cls, mask in [("lush_green",lush),("drought_severe_stress",drought),
                              ("inconsistent_growth",stress),("bare_soil",bare)]:
                cell_mask = mask[y1:y2, x1:x2]
                cell_scores[cls] = cell_mask.sum()
            cell_dom = max(cell_scores, key=cell_scores.get)
            if cell_scores[cell_dom] > 100:  # minimal 100 pixel
                regions.append({
                    "bbox": (x1,y1,x2,y2),
                    "hsv_zone": zone_map[cell_dom],
                    "hsv_display": display_map[cell_dom],
                    "hsv_cls": cell_dom,
                    "hsv_conf": min(0.95, cell_scores[cell_dom] / (gw*gh)),
                })

    return dominant, conf, regions

# ── Benchmark ─────────────────────────────────────────────────────────────────
def benchmark_video(video_path, expected_cls, desc, model):
    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        log(f"  [WARN] Tidak bisa buka: {video_path}")
        return None

    fps_src = cap.get(cv2.CAP_PROP_FPS)
    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))

    times_hsv   = []
    times_onnx  = []
    times_total = []
    correct_hsv   = 0
    correct_onnx  = 0
    correct_fused = 0
    n_frames = 0
    n_dets   = 0

    for fi in range(min(MAX_FRAMES, total_frames)):
        cap.set(cv2.CAP_PROP_POS_FRAMES, fi * max(1, total_frames // MAX_FRAMES))
        ret, frame = cap.read()
        if not ret or frame is None: continue

        # Resize ke 640 untuk processing
        fh, fw = frame.shape[:2]
        if fw > 640:
            frame = cv2.resize(frame, (640, int(fh*640/fw)))

        t0 = time.perf_counter()

        # HSV classification
        t_hsv0 = time.perf_counter()
        hsv_cls, hsv_conf, regions = hsv_classify_frame(frame)
        t_hsv1 = time.perf_counter()
        times_hsv.append(t_hsv1 - t_hsv0)

        # ONNX inference per region (grid 2x2)
        t_onnx0 = time.perf_counter()
        onnx_results = []
        for r in regions:
            x1,y1,x2,y2 = r["bbox"]
            crop = frame[y1:y2, x1:x2]
            if crop.size == 0: continue
            onnx_cls, onnx_conf = model.infer_single(crop)
            onnx_results.append({
                "hsv_cls": r["hsv_cls"],
                "onnx_cls": onnx_cls,
                "onnx_conf": onnx_conf,
                "bbox": r["bbox"],
            })
            n_dets += 1
        t_onnx1 = time.perf_counter()
        times_onnx.append(t_onnx1 - t_onnx0)

        t1 = time.perf_counter()
        times_total.append(t1 - t0)
        n_frames += 1

        # ── Accuracy check ──
        # HSV accuracy
        if expected_cls.lower() in hsv_cls.lower() or hsv_cls.lower() in expected_cls.lower():
            correct_hsv += 1

        # ONNX accuracy (weighted confidence vote dari regions)
        if onnx_results:
            # Weighted vote berdasarkan confidence
            vote_scores = {}
            for r in onnx_results:
                cls = r["onnx_cls"]
                vote_scores[cls] = vote_scores.get(cls, 0) + r["onnx_conf"]
            onnx_dominant = max(vote_scores, key=vote_scores.get)
            if expected_cls.lower() in onnx_dominant.lower() or onnx_dominant.lower() in expected_cls.lower():
                correct_onnx += 1

            # Fused: ONNX primary (weighted vote), HSV sebagai tiebreaker
            # ONNX lebih dipercaya karena trained dengan 5556 gambar nyata
            max_onnx_conf = max(r["onnx_conf"] for r in onnx_results)
            if max_onnx_conf >= 0.4:
                # ONNX cukup confident → pakai ONNX dominant
                fused_cls = onnx_dominant
            else:
                # ONNX tidak confident → gabungkan HSV + ONNX (50/50)
                # Tambahkan HSV ke vote_scores
                hsv_vote = {hsv_cls: 0.5}  # HSV weight 0.5
                combined = dict(vote_scores)
                for cls, score in hsv_vote.items():
                    combined[cls] = combined.get(cls, 0) + score
                fused_cls = max(combined, key=combined.get)

            if expected_cls.lower() in fused_cls.lower() or fused_cls.lower() in expected_cls.lower():
                correct_fused += 1
        else:
            # Tidak ada ONNX region → pakai HSV
            if expected_cls.lower() in hsv_cls.lower() or hsv_cls.lower() in expected_cls.lower():
                correct_fused += 1

    cap.release()

    if n_frames == 0:
        return None

    avg_total = np.mean(times_total) if times_total else 0
    avg_hsv   = np.mean(times_hsv)   if times_hsv else 0
    avg_onnx  = np.mean(times_onnx)  if times_onnx else 0
    fps       = 1.0 / avg_total if avg_total > 0 else 0
    acc_hsv   = correct_hsv   / n_frames * 100
    acc_onnx  = correct_onnx  / n_frames * 100
    acc_fused = correct_fused / n_frames * 100
    dets_per_frame = n_dets / n_frames

    return {
        "desc": desc, "fps_src": fps_src, "n_frames": n_frames,
        "fps": fps,
        "ms_hsv": avg_hsv*1000, "ms_onnx": avg_onnx*1000,
        "ms_total": avg_total*1000,
        "acc_hsv": acc_hsv, "acc_onnx": acc_onnx, "acc_fused": acc_fused,
        "dets_per_frame": dets_per_frame,
    }

def main():
    log("=" * 65)
    log("MoonHarvest Benchmark v5 — HSV + ONNX Classifier")
    log(f"Model : {Path(MODEL_V5).name}")
    log(f"imgsz : {IMGSZ}px | FP32")
    log("=" * 65)

    # Load model v5
    log("\nLoading model v5...")
    try:
        model_v5 = ONNXClassifier(MODEL_V5, imgsz=IMGSZ)
    except Exception as e:
        log(f"[ERR] Gagal load model v5: {e}")
        return

    log("\n--- Benchmark per video ---\n")
    results = []
    for vid_path, expected, desc in VIDEOS:
        if not Path(vid_path).exists():
            log(f"[SKIP] {desc}: file tidak ditemukan")
            continue
        log(f"Benchmarking: {desc} ({Path(vid_path).name})...")
        r = benchmark_video(vid_path, expected, desc, model_v5)
        if r:
            results.append(r)
            log(f"  FPS     : {r['fps']:.1f} fps (src: {r['fps_src']:.0f} fps)")
            log(f"  Latency : {r['ms_total']:.1f}ms total "
                f"(HSV:{r['ms_hsv']:.1f}ms + ONNX:{r['ms_onnx']:.1f}ms)")
            log(f"  Accuracy: HSV={r['acc_hsv']:.1f}% | ONNX={r['acc_onnx']:.1f}% | Fused={r['acc_fused']:.1f}%")
            log(f"  Dets/frame: {r['dets_per_frame']:.1f}")
            log("")

    if not results:
        log("[ERR] Tidak ada video yang berhasil di-benchmark")
        return

    # Summary
    log("=" * 65)
    log("SUMMARY")
    log("=" * 65)
    avg_fps      = np.mean([r["fps"]       for r in results])
    avg_acc_hsv  = np.mean([r["acc_hsv"]   for r in results])
    avg_acc_onnx = np.mean([r["acc_onnx"]  for r in results])
    avg_acc_fused= np.mean([r["acc_fused"] for r in results])
    avg_ms       = np.mean([r["ms_total"]  for r in results])
    avg_dets     = np.mean([r["dets_per_frame"] for r in results])

    log(f"  Rata-rata FPS     : {avg_fps:.1f} fps")
    log(f"  Rata-rata latency : {avg_ms:.1f} ms/frame")
    log(f"  Akurasi HSV-only  : {avg_acc_hsv:.1f}%")
    log(f"  Akurasi ONNX-only : {avg_acc_onnx:.1f}%")
    log(f"  Akurasi HSV+ONNX  : {avg_acc_fused:.1f}%  ← GABUNGAN")
    log(f"  Deteksi/frame     : {avg_dets:.1f}")
    log("")
    log(f"  Target FPS (≥15)  : {'PASS' if avg_fps >= 15 else 'FAIL'} ({avg_fps:.1f})")
    log(f"  Target Acc (≥80%) : {'PASS' if avg_acc_fused >= 80 else 'FAIL'} ({avg_acc_fused:.1f}%)")
    log("=" * 65)

    # Save result
    out = {
        "model": Path(MODEL_V5).name,
        "imgsz": IMGSZ,
        "avg_fps": round(avg_fps,1),
        "avg_latency_ms": round(avg_ms,1),
        "acc_hsv": round(avg_acc_hsv,1),
        "acc_onnx": round(avg_acc_onnx,1),
        "acc_fused": round(avg_acc_fused,1),
        "dets_per_frame": round(avg_dets,1),
        "per_video": results,
    }
    out_path = "/tmp/moonharvest_benchmark_v5.json"
    with open(out_path, "w") as f:
        json.dump(out, f, indent=2)
    log(f"\nHasil disimpan ke: {out_path}")

if __name__ == "__main__":
    main()
