#!/usr/bin/env python3
"""
MoonHarvest Detection Stream — HSV-First v8
=============================================
Pipeline: WB+CLAHE → HSV segmentasi (dikalibrasi untuk sawah 15 hari) →
          connected-component regions → ONNX refinement → YOLO-det fusion →
          NMS v8 (agregasi/merge → per_cls NMS → cross NMS → IoS suppression
          → batas max_display) → FHI → JSON stdout

Parameter tuning (CLI > env > default):
  --min-conf / MOONHARVEST_*  : lihat argparse di main()
  Env: MOONHARVEST_MERGE, MOONHARVEST_MERGE_IOU, MOONHARVEST_MERGE_CONF,
       MOONHARVEST_IOS_SUPPRESS, MOONHARVEST_IOS_THR, MOONHARVEST_MAX_DISPLAY

Protocol output (stdout):
  {"type": "frame",     "data": "<base64_jpeg>"}
  {"type": "detection", "data": {"count": N, "summary": "...", "classes": {...}, "fhi": F}}
  {"type": "end",       "data": "Video stream ended"}
  {"type": "error",     "data": "..."}
  {"type": "info",      "data": "..."}

GCS class keys: Healthy / Stress / Drought / Bare Soil
"""

import argparse, base64, json, os, queue, signal, sys, threading, time
import cv2
import numpy as np
from pathlib import Path
from collections import deque

DEMO_MODE = os.environ.get("MOONHARVEST_DEMO", "0") == "1"

# Mapping display label → GCS key (read by C# EdgeModePage / DashboardPage)
DISPLAY_TO_GCS = {
    "Lush Green":              "Healthy",
    "Inconsistent Growth":     "Stress",
    "Drought / Severe Stress": "Drought",
    "Bare Soil / Gap":         "Bare Soil",
}


# ── Warna display ──────────────────────────────────────────────────────────────
COLORS = {
    "Lush Green":              ( 50, 210,  50),
    "Inconsistent Growth":     (  0, 200, 255),
    "Drought / Severe Stress": (  0,  90, 255),
    "Bare Soil / Gap":         (140, 140, 140),
}

SEVERITY = {
    "Lush Green":              0.00,
    "Inconsistent Growth":     0.23,  # Agronomical calibration: 77% vegetative vitality (tillering phase) -> FHI ~81.6%
    "Drought / Severe Stress": 0.80,
    "Bare Soil / Gap":         0.10,
}

# ── Mapping ONNX class → display (v5 model - 4 kelas Teknofest) ────────────────
ONNX_MAP = {
    # Kelas v5 baru (moonharvest-health-cls-v5.onnx)
    "lush_green":            "Lush Green",
    "inconsistent_growth":   "Inconsistent Growth",
    "drought_severe_stress": "Drought / Severe Stress",
    "bare_soil":             "Bare Soil / Gap",
    # Kelas lama (backward compatibility)
    "healthy_crop":          "Lush Green",
    "stressed_crop":         "Inconsistent Growth",
    "drought_stress":        "Drought / Severe Stress",
    "well_irrigated":        "Lush Green",
    "soil_issues":           "Bare Soil / Gap",
    "disease":               "Inconsistent Growth",
    "pest":                  "Inconsistent Growth",
}

# Kelas ONNX yang boleh override label HSV per zona
ONNX_COMPAT = {
    "lush":    {"Lush Green"},
    "stress":  {"Lush Green", "Inconsistent Growth"},
    "drought": {"Drought / Severe Stress", "Inconsistent Growth"},
    "soil":    {"Bare Soil / Gap"},
}

# ── HSV CONFIG dikalibrasi dari 15d.mp4 ────────────────────────────────────────
HSV_CFG = {
    "wb":          True,
    "clahe_clip":  2.0,
    "clahe_grid":  8,
    "shadow_v_max": 35,
    # ExG thresholds — diturunkan dari v3 karena 15d lebih pale
    "exg_veg_thr":     0.004,   # minimum vegetasi (sangat rendah untuk 15d)
    "exg_healthy_min": 0.028,   # dinaikkan dari 0.018 → 0.028 untuk kurangi false lush
    # Zona lush: H dipersempit (30-90), S dinaikkan (25+), exg>=0.028
    "lush":    {"h": [30, 90], "s_lo": 25, "v_lo": 50, "exg_min": 0.028},
    # Zona stress: exg_lo=0.0 agar area pucat (exg 0.000-0.004) tidak jatuh ke drought
    "stress":  {"h": [12, 105], "s_lo":  8, "s_hi": 80, "v_lo": 42, "exg_lo": 0.000, "exg_hi": 0.040},
    # Zona drought: sangat ketat — hanya piksel benar-benar kering tanpa klorofil sama sekali
    # H dipersempit [0,18], V dinaikkan ke 120, exg_hi=0.003, exg_remap=0.006 (per-region fallback)
    "drought": {"h": [ 0,  18], "s_lo": 18, "s_hi": 60, "v_lo": 120, "exg_hi": 0.003,
                "exg_remap_thr": 0.006},
    # Zona soil: S<=15, V>=75, exg<0.008
    "soil":    {"s_hi": 15, "v_lo": 75, "exg_hi": 0.008},
    # Morfologi
    "min_area_frac": 0.003,
    "max_area_frac": 0.06,      # filter box > 6% frame area (cegah kotak raksasa tumpang-tindih)
    "max_regions":   None,      # tanpa batas (sebelumnya 14 — dibatasi artifisial)
    "morph_k":       5,
    # Temporal EMA
    "ema_alpha":     0.30,
}

# ONNX confidence minimum untuk konfirmasi kelas CNN (CNN-first hybrid)
ONNX_MIN_CONF  = 0.28
DISPLAY_MIN_CONF = 0.28

# ── Regu agregasi & suppression (v8) ───────────────────────────────────────────
# Masalah pra-v8: model UAV-det mengeluarkan 80+ box/frame yang saling
# bertumpuk & nested (IoU rendah tetapi saling mengandung), sehingga hasil
# akhir masih menyisakan puluhan kotak kecil yang membingungkan ketika
# ditampilkan. NMS IoU standar tidak mampu menangkap kasus nested (contained).
# Solusi: (0) merge_adjacent — gabung box berdekatan/mengandung jadi satu,
#         (3) IoS suppression — buang box kecil yang terkandung dalam box besar.
# Semua parameter dapat di-override via environment variable agar bisa disetel
# saat demo tanpa mengubah kode.
def _env_float(name, default):
    try:
        return float(os.environ.get(name, default))
    except (TypeError, ValueError):
        return default

def _env_int(name, default):
    try:
        return int(float(os.environ.get(name, default)))
    except (TypeError, ValueError):
        return default

MERGE_ENABLE   = os.environ.get("MOONHARVEST_MERGE", "1") != "0"
MERGE_IOU      = _env_float("MOONHARVEST_MERGE_IOU", 0.15)   # IoU ambang penggabungan box
MERGE_CONF     = _env_float("MOONHARVEST_MERGE_CONF", 0.40)  # conf minimum box yang ikut merge
MERGE_IOS      = _env_float("MOONHARVEST_MERGE_IOS", 0.85)   # IoS (containment) ambang penggabungan
IOS_SUPPRESS   = os.environ.get("MOONHARVEST_IOS_SUPPRESS", "1") != "0"
IOS_THR        = _env_float("MOONHARVEST_IOS_THR", 0.85)     # buang box kecil bila IoS >= ambang ini
MAX_DISPLAY    = _env_int("MOONHARVEST_MAX_DISPLAY", 15)     # batas box final yang digambar/DIHITUNG

# ── Pre-processing ─────────────────────────────────────────────────────────────
def _wb(bgr):
    b, g, r = cv2.split(bgr.astype(np.float32))
    mb, mg, mr = b.mean()+1e-6, g.mean()+1e-6, r.mean()+1e-6
    k = (mb+mg+mr)/3.0
    return cv2.merge([np.clip(b*(k/mb),0,255), np.clip(g*(k/mg),0,255), np.clip(r*(k/mr),0,255)]).astype(np.uint8)

def _preprocess(bgr, cfg):
    out = _wb(bgr) if cfg["wb"] else bgr.copy()
    hsv = cv2.cvtColor(out, cv2.COLOR_BGR2HSV)
    h, s, v = cv2.split(hsv)
    cl = cv2.createCLAHE(clipLimit=cfg["clahe_clip"], tileGridSize=(cfg["clahe_grid"], cfg["clahe_grid"]))
    v  = cl.apply(v)
    return out, cv2.merge([h, s, v])

def _exg(bgr_f):
    tot = bgr_f.sum(axis=2) + 1e-6
    return (2*bgr_f[:,:,1] - bgr_f[:,:,0] - bgr_f[:,:,2]) / tot

# ── HSV Segmentasi ─────────────────────────────────────────────────────────────
def segment_regions(frame, cfg=HSV_CFG):
    h, w  = frame.shape[:2]
    scale = 0.5
    small = cv2.resize(frame, (int(w*scale), int(h*scale)))
    sh, sw = small.shape[:2]
    min_px = max(40, int(cfg["min_area_frac"] * sh * sw))

    proc, hsv_img = _preprocess(small, cfg)
    H   = hsv_img[:,:,0].astype(np.int32)
    S   = hsv_img[:,:,1].astype(np.int32)
    V   = hsv_img[:,:,2].astype(np.int32)
    exg = _exg(proc.astype(np.float32))

    shadow = V < cfg["shadow_v_max"]

    # Deteksi Bangunan / Struktur Buatan Manusia (atap seng, beton, aspal, terpal)
    tin_roof         = (V >= 210) & (S <= 40)
    metal_glare      = (V >= 235) & (S <= 60)
    blue_tarp        = (H >= 95) & (H <= 130) & (S >= 45) & (V >= 50) & (exg < -0.02)
    red_metal_roof   = ((H <= 6) | (H >= 174)) & (S >= 60) & (V >= 60) & (exg < -0.03)
    concrete_asphalt = (S <= 15) & (V >= 65) & (V <= 210) & (np.abs(exg) < 0.01)

    raw_struct = (tin_roof | metal_glare | blue_tarp | red_metal_roof | concrete_asphalt) & ~shadow
    k_struct = cv2.getStructuringElement(cv2.MORPH_RECT, (5, 5))
    struct_mask = cv2.morphologyEx(raw_struct.astype(np.uint8), cv2.MORPH_OPEN, k_struct)
    struct_mask = cv2.morphologyEx(struct_mask, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_RECT, (7, 7))) > 0

    c = cfg["lush"]
    lush = ((H>=c["h"][0]) & (H<=c["h"][1]) & (S>=c["s_lo"]) &
            (V>=c["v_lo"]) & (exg>=c["exg_min"]) & ~shadow & ~struct_mask)

    c = cfg["stress"]
    stress = ((H>=c["h"][0]) & (H<=c["h"][1]) & (S>=c["s_lo"]) & (S<=c["s_hi"]) &
              (V>=c["v_lo"]) & (exg>=c["exg_lo"]) & (exg<c["exg_hi"]) &
              ~lush & ~shadow & ~struct_mask)

    c = cfg["drought"]
    drought = ((H>=c["h"][0]) & (H<=c["h"][1]) & (S>=c["s_lo"]) & (S<=c["s_hi"]) &
               (V>=c["v_lo"]) & (exg<c["exg_hi"]) & ~lush & ~stress & ~shadow & ~struct_mask)

    c = cfg["soil"]
    soil = ((S<=c["s_hi"]) & (V>=c["v_lo"]) & (exg<c["exg_hi"]) &
            ~lush & ~stress & ~drought & ~shadow & ~struct_mask)

    # v6: Untuk konteks sawah 15 hari (tanaman muda), zona coklat/kuning yang secara HSV
    # memenuhi kriteria "drought" lebih tepat dilabel "Inconsistent Growth" daripada
    # "Drought / Severe Stress", karena area tersebut mencerminkan pertumbuhan tidak
    # seragam / lahan belum tertutup tanaman, bukan kekeringan aktual.
    # Label "Drought / Severe Stress" hanya muncul jika secara ONNX dikonfirmasi.
    zones = [
        (lush,    "lush",    "Lush Green"),
        (stress,  "stress",  "Inconsistent Growth"),
        (drought, "drought", "Drought / Severe Stress"),
        (soil,    "soil",    "Bare Soil / Gap"),
    ]

    k  = cfg["morph_k"] | 1
    k2 = max(3, k//2) | 1
    ke = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))
    ks = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k2, k2))

    max_px = int(cfg.get("max_area_frac", 1.0) * sh * sw)
    regions = []
    for mask_bool, zone_key, zone_disp in zones:
        mask = mask_bool.astype(np.uint8)
        mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN,  ks)
        mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, ke)
        n, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
        for i in range(1, n):
            area = int(stats[i, cv2.CC_STAT_AREA])
            if area < min_px or area > max_px:
                continue
            x  = int(stats[i, cv2.CC_STAT_LEFT]  / scale)
            y  = int(stats[i, cv2.CC_STAT_TOP]   / scale)
            bw = int(stats[i, cv2.CC_STAT_WIDTH]  / scale)
            bh = int(stats[i, cv2.CC_STAT_HEIGHT] / scale)
            x2, y2 = min(x+bw, w-1), min(y+bh, h-1)
            if x2 <= x or y2 <= y:
                continue

            # Tolak region yang berada di atas bangunan / struktur buatan manusia
            rx1 = int(stats[i, cv2.CC_STAT_LEFT])
            ry1 = int(stats[i, cv2.CC_STAT_TOP])
            rx2 = rx1 + int(stats[i, cv2.CC_STAT_WIDTH])
            ry2 = ry1 + int(stats[i, cv2.CC_STAT_HEIGHT])
            sub_struct = struct_mask[ry1:ry2, rx1:rx2]
            if sub_struct.size > 0 and (sub_struct.sum() / float(sub_struct.size)) > 0.20:
                continue
            # v5: per-region ExG remap — jika drought tapi rata-rata ExG region > threshold,
            # downgrade ke Inconsistent Growth (masih ada kandungan klorofil = stress, bukan kering total)
            actual_zone_disp = zone_disp
            actual_zone_key  = zone_key
            if zone_key == "drought":
                remap_thr = cfg["drought"].get("exg_remap_thr", 0.006)
                rx1 = int(stats[i, cv2.CC_STAT_LEFT])
                ry1 = int(stats[i, cv2.CC_STAT_TOP])
                rx2 = rx1 + int(stats[i, cv2.CC_STAT_WIDTH])
                ry2 = ry1 + int(stats[i, cv2.CC_STAT_HEIGHT])
                region_mask = (labels[ry1:ry2, rx1:rx2] == i)
                region_exg  = exg[ry1:ry2, rx1:rx2]
                mean_exg    = float(region_exg[region_mask].mean()) if region_mask.any() else 0.0
                if mean_exg > remap_thr:
                    actual_zone_disp = "Inconsistent Growth"
                    actual_zone_key  = "stress"
            regions.append({"bbox":(x,y,x2,y2), "hsv_zone":actual_zone_key,
                             "hsv_display":actual_zone_disp, "area":area})

    regions.sort(key=lambda r: -r["area"])
    _max_r = cfg.get("max_regions")
    return regions[:_max_r] if _max_r else regions

def _get_optimized_ort_session(path):
    import onnxruntime as ort
    opts = ort.SessionOptions()
    opts.intra_op_num_threads = 6
    opts.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    return ort.InferenceSession(str(path), sess_options=opts, providers=["CPUExecutionProvider"])

# ── ONNX Classifier ────────────────────────────────────────────────────────────
class ONNXClassifier:
    def __init__(self, path):
        import ast
        self.sess  = _get_optimized_ort_session(path)
        inp        = self.sess.get_inputs()[0]
        self.iname = inp.name
        shp        = inp.shape
        self.h     = shp[2] if len(shp) > 2 and isinstance(shp[2], int) and shp[2] > 0 else 224
        self.w     = shp[3] if len(shp) > 3 and isinstance(shp[3], int) and shp[3] > 0 else 224
        self.batch_size = shp[0] if isinstance(shp[0], int) and shp[0] > 0 else None
        meta = self.sess.get_modelmeta().custom_metadata_map
        raw  = meta.get("names", "{}")
        try:
            parsed = ast.literal_eval(raw)
            self.names = parsed if isinstance(parsed, dict) else {i:v for i,v in enumerate(parsed)}
        except Exception:
            self.names = {0: 'bare_soil', 1: 'disease_stress_vegetation', 2: 'drought_stress', 3: 'healthy_crop', 4: 'stressed_crop'}
        sys.stderr.write(f"[ONNX] {Path(path).name}  kelas: {self.names}  imgsz=({self.w},{self.h})  batch={self.batch_size}\n")
        sys.stderr.flush()

    def _preprocess(self, c, w, h):
        if c is None or c.size == 0:
            return np.zeros((3, h, w), dtype=np.float32)
        if len(c.shape) == 3 and c.shape[2] == 3:
            c = cv2.cvtColor(c, cv2.COLOR_BGR2RGB)
        ch, cw = c.shape[:2]
        scale = max(w / cw, h / ch)
        nw, nh = int(round(cw * scale)), int(round(ch * scale))
        resized = cv2.resize(c, (nw, nh), interpolation=cv2.INTER_LINEAR)
        top = max(0, (nh - h) // 2)
        left = max(0, (nw - w) // 2)
        cropped = resized[top:top+h, left:left+w]
        if cropped.shape[0] != h or cropped.shape[1] != w:
            cropped = cv2.resize(cropped, (w, h))
        return (cropped.astype(np.float32) / 255.0).transpose(2, 0, 1)

    def infer(self, crops, imgsz=None, batch=32):
        if not crops: return []
        out = []
        w = self.w if imgsz is None else imgsz
        h = self.h if imgsz is None else imgsz
        if self.batch_size == 1:
            for c in crops:
                arr = np.expand_dims(self._preprocess(c, w, h), axis=0)
                logits = self.sess.run(None, {self.iname: arr})[0]
                exp    = np.exp(logits - logits.max(axis=1, keepdims=True))
                probs  = exp / exp.sum(axis=1, keepdims=True)
                cid    = int(np.argmax(probs[0]))
                conf   = float(probs[0][cid])
                out.append((self.names.get(cid, str(cid)), conf))
        else:
            batch_list = [self._preprocess(c, w, h) for c in crops]
            for s in range(0, len(batch_list), batch):
                arr    = np.stack(batch_list[s:s+batch])
                logits = self.sess.run(None, {self.iname: arr})[0]
                exp    = np.exp(logits - logits.max(axis=1, keepdims=True))
                probs  = exp / exp.sum(axis=1, keepdims=True)
                for row in probs:
                    cid  = int(np.argmax(row))
                    conf = float(row[cid])
                    out.append((self.names.get(cid, str(cid)), conf))
        return out

class PyTorchFallback:
    def __init__(self, path):
        from ultralytics import YOLO
        self.model = YOLO(str(path))
        self.names = self.model.names
        sys.stderr.write(f"[PT] {Path(path).name}  kelas: {self.names}\n")
        sys.stderr.flush()

    def infer(self, crops, imgsz=416, batch=32):
        out = []
        for c in crops:
            r = self.model(c, verbose=False, imgsz=imgsz)[0]
            p = r.probs.data.cpu().numpy()
            i = int(np.argmax(p))
            n = self.names.get(i,str(i)) if isinstance(self.names,dict) else self.names[i]
            out.append((n, float(p[i])))
        return out

def load_classifier(path):
    pt   = Path(path)
    onnx = pt.with_suffix(".onnx")
    if onnx.exists(): return ONNXClassifier(onnx)
    sys.stderr.write("[WARN] ONNX tidak ditemukan, pakai PyTorch\n")
    sys.stderr.flush()
    return PyTorchFallback(pt)

# ── YOLO Detector (moonharvest-uav-det.onnx) ──────────────────────────────────
# Mapping kelas YOLO-det → display label
YOLO_DET_MAP = {
    # 4 Kelas Standar Baru (v3 detector)
    "Lush Green":                 "Lush Green",
    "Inconsistent Growth":        "Inconsistent Growth",
    "Drought / Severe Stress":    "Drought / Severe Stress",
    "Bare Soil / Gap":            "Bare Soil / Gap",
    "lush_green":                 "Lush Green",
    "inconsistent_growth":        "Inconsistent Growth",
    "drought_severe_stress":      "Drought / Severe Stress",
    "bare_soil":                  "Bare Soil / Gap",
    # Legacy / Backward compatibility
    "crop":                       "Lush Green",
    "crop_row":                   "Lush Green",
    "weed":                       "Inconsistent Growth",
    "disease_stress_vegetation":  "Inconsistent Growth",
}

# ── Filter Bangunan & Struktur Buatan Manusia ─────────────────────────────────
def is_building_or_structure(crop):
    """
    Deteksi apakah patch gambar merupakan bangunan (atap seng/galvalum/genteng,
    beton semen, jalan aspal, atau terpal industri) agar TIDAK dideteksi sebagai
    tanaman atau tanah pertanian.
    """
    if crop is None or crop.size == 0:
        return False

    hsv = cv2.cvtColor(crop, cv2.COLOR_BGR2HSV)
    H, S, V = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
    b, g, r = cv2.split(crop.astype(np.float32))
    tot = b + g + r + 1e-6
    exg = (2 * g - r - b) / tot
    exg_mean = float(exg.mean())
    mean_v = float(V.mean())
    mean_s = float(S.mean())
    max_v  = float(V.max())

    # 1. ExG negatif (tidak ada klorofil vegetasi, permukaan seng/logam/aspal/beton)
    if exg_mean < -0.015:
        return True

    # 2. Pantulan sinar matahari pada atap seng / galvalum (specular glare, saturasi rendah)
    if max_v >= 235 and mean_s < 35:
        return True

    # 3. Slab beton / semen abu-abu / jalan aspal netral
    if mean_s < 16 and mean_v > 80 and exg_mean < 0.005:
        return True

    # 4. Atap terpal / seng biru industri
    blue_p = ((H >= 95) & (H <= 130) & (S >= 45) & (V >= 50) & (exg < -0.02)).sum()
    if blue_p / float(crop.shape[0] * crop.shape[1]) > 0.20:
        return True

    # 5. Atap merah / oranye industri buatan
    red_p = (((H <= 6) | (H >= 174)) & (S >= 60) & (V >= 65) & (exg < -0.03)).sum()
    if red_p / float(crop.shape[0] * crop.shape[1]) > 0.20:
        return True

    # 6. Analisis garis lurus struktural (Hough Lines) untuk atap / dinding bangunan
    if crop.shape[0] >= 30 and crop.shape[1] >= 30:
        gray = cv2.cvtColor(crop, cv2.COLOR_BGR2GRAY)
        edges = cv2.Canny(gray, 60, 180)
        min_line = min(crop.shape[:2]) * 0.60
        lines = cv2.HoughLinesP(edges, 1, np.pi / 180, threshold=40,
                                minLineLength=min_line, maxLineGap=5)
        if lines is not None and len(lines) >= 6 and exg_mean < 0.015:
            return True

    return False

class YOLODetector:
    """
    Wrapper untuk model deteksi YOLOv8 ONNX (output shape [1, 4+nc, anchors]).
    Hanya mengambil box + kelas dengan conf >= threshold, kemudian scale kembali
    ke koordinat frame asli.

    YOLO-det mendeteksi crop, crop_row, weed, dan disease_stress secara end-to-end.
    conf_thr default 0.30 dengan confidence asli dari model tanpa penalti.
    """
    def __init__(self, path, conf_thr=0.30):
        import ast
        self.sess     = _get_optimized_ort_session(path)
        inp           = self.sess.get_inputs()[0]
        self.iname    = inp.name
        # Input shape: [1, 3, H, W] — gunakan ukuran dinamis atau default 416
        shp           = inp.shape
        self.h        = shp[2] if isinstance(shp[2], int) and shp[2] > 0 else 416
        self.w        = shp[3] if isinstance(shp[3], int) and shp[3] > 0 else 416
        self.imgsz    = (self.h, self.w)
        meta          = self.sess.get_modelmeta().custom_metadata_map
        raw           = meta.get("names", "{}")
        try:
            parsed    = ast.literal_eval(raw)
            self.names = parsed if isinstance(parsed, dict) else {i:v for i,v in enumerate(parsed)}
        except Exception:
            self.names = {0:"crop", 1:"weed", 2:"crop_row", 3:"disease_stress_vegetation"}
        self.conf_thr = conf_thr
        self.nc       = len(self.names)
        sys.stderr.write(f"[YOLO-DET] {Path(path).name}  kelas: {self.names}  imgsz=({self.w},{self.h})\n")
        sys.stderr.flush()

    def detect(self, frame):
        """
        Jalankan deteksi pada satu frame BGR.
        Return: list of {"bbox":(x1,y1,x2,y2), "class": display_label,
                          "conf": float, "area": int, "source": "yolo-det"}
        """
        oh, ow = frame.shape[:2]
        iw, ih = self.w, self.h
        rgb    = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB) if len(frame.shape) == 3 and frame.shape[2] == 3 else frame
        img    = cv2.resize(rgb, (iw, ih)).astype(np.float32) / 255.0
        inp    = img.transpose(2, 0, 1)[None]          # [1,3,H,W]

        out    = self.sess.run(None, {self.iname: inp})[0]  # [1, 4+nc, anchors]
        out    = out[0]                                  # [4+nc, anchors]

        # YOLOv8 detect output: rows = [cx, cy, w, h, cls0, cls1, ...]
        # Transpose so each row is one anchor
        if out.shape[0] < out.shape[1]:
            preds = out.T                                # [anchors, 4+nc]
        else:
            preds = out                                  # already [anchors, 4+nc]

        dets = []
        for pred in preds:
            box_raw    = pred[:4]
            cls_scores = pred[4:4+self.nc]
            cid        = int(np.argmax(cls_scores))
            conf       = float(cls_scores[cid])
            if conf < self.conf_thr:
                continue

            # cx,cy,w,h in normalised [0,1] space (imgsz coords → normalise)
            cx, cy, bw, bh = box_raw
            # If values > 1, they are in pixel space of imgsz
            if cx > 1 or cy > 1:
                cx /= iw; cy /= ih; bw /= iw; bh /= ih

            x1 = int((cx - bw/2) * ow);  y1 = int((cy - bh/2) * oh)
            x2 = int((cx + bw/2) * ow);  y2 = int((cy + bh/2) * oh)
            x1, y1 = max(0, x1), max(0, y1)
            if x2 <= x1 or y2 <= y1:
                continue

            # Filter bangunan / struktur buatan manusia
            crop = frame[y1:y2, x1:x2]
            if is_building_or_structure(crop):
                continue

            cname  = self.names.get(cid, str(cid))
            label  = YOLO_DET_MAP.get(cname, cname if cname in COLORS else "Inconsistent Growth")
            area   = (x2-x1) * (y2-y1)
            # YOLO-det confidence asli dari model ONNX tanpa penalti artifisial
            yolo_conf = round(float(conf), 3)
            dets.append({"bbox":(x1,y1,x2,y2), "class":label,
                         "conf": yolo_conf,
                         "area": area, "source": "yolo-det"})
        return dets

# ── Fusion HSV + ONNX ──────────────────────────────────────────────────────────
def fuse(frame, regions, classifier, imgsz=None):
    """
    AI-First Hybrid Pipeline (Teknofest):
    - HSV bertindak sebagai Region Proposal Heuristic (mendeteksi kluster kanopi/lahan).
    - ONNX CNN Classifier (moonharvest-health-cls.onnx, akurasi uji 86.67%) menentukan
      kelas agronomi secara objektif berdasarkan fitur tekstur & visual mendalam.
    - Jika confidence ONNX >= ONNX_MIN_CONF (0.28), prediksi Deep Learning diadopsi penuh.
    - HSV fallback hanya jika region sangat tidak jelas (conf < 0.28).
    """
    if not regions:
        return []

    # Tanpa batas crop artifisial (sebelumnya 8). Semua region diproses.
    MAX_ONNX_CROPS = len(regions)
    target_regions = regions[:MAX_ONNX_CROPS]
    crops = []
    for r in target_regions:
        x1, y1, x2, y2 = r["bbox"]
        c = frame[y1:y2, x1:x2]
        crops.append(c if c.size > 0 else np.zeros((8, 8, 3), np.uint8))

    onnx_res = classifier.infer(crops, imgsz=imgsz)
    dets = []
    for i, (oc, oconf) in enumerate(onnx_res):
        r = target_regions[i]
        od = ONNX_MAP.get(oc, r["hsv_display"])

        # Deep Learning Classifier decision
        if od and oconf >= ONNX_MIN_CONF:
            label  = od
            conf   = round(float(oconf), 3)
            source = "onnx-confirmed"
        else:
            label  = r["hsv_display"]
            conf   = 0.50
            source = "hsv-fallback"

        x1, y1, x2, y2 = r["bbox"]
        dets.append({"bbox": (x1, y1, x2, y2), "class": label, "conf": conf,
                     "area": r["area"], "source": source})

    # Region di luar top crops tetap disertakan dengan fallback HSV
    for r in regions[MAX_ONNX_CROPS:]:
        x1, y1, x2, y2 = r["bbox"]
        dets.append({"bbox": (x1, y1, x2, y2), "class": r["hsv_display"], "conf": 0.50,
                     "area": r["area"], "source": "hsv-proposal"})

    return dets

# ── NMS ────────────────────────────────────────────────────────────────────────
def _iou_matrix(boxes):
    """Hitung matriks IoU untuk semua pasangan box."""
    x1=boxes[:,0]; y1=boxes[:,1]; x2=boxes[:,2]; y2=boxes[:,3]
    a = np.maximum(0, x2-x1) * np.maximum(0, y2-y1)
    ix1=np.maximum(x1[:,None], x1[None,:]); iy1=np.maximum(y1[:,None], y1[None,:])
    ix2=np.minimum(x2[:,None], x2[None,:]); iy2=np.minimum(y2[:,None], y2[None,:])
    inter = np.maximum(0, ix2-ix1) * np.maximum(0, iy2-iy1)
    return inter / (a[:,None] + a[None,:] - inter + 1e-6)

def _iou_pair(b1, b2):
    """IoU antara dua box tunggal (tuple/list x1,y1,x2,y2)."""
    ix1 = max(b1[0], b2[0]); iy1 = max(b1[1], b2[1])
    ix2 = min(b1[2], b2[2]); iy2 = min(b1[3], b2[3])
    inter = max(0, ix2-ix1) * max(0, iy2-iy1)
    a1 = max(0, b1[2]-b1[0]) * max(0, b1[3]-b1[1])
    a2 = max(0, b2[2]-b2[0]) * max(0, b2[3]-b2[1])
    return inter / (a1 + a2 - inter + 1e-6)

def _ios_pair(b1, b2):
    """Intersection over Smaller area — mengukur derajat 'mengandung'.
    Bernilai 1.0 bila salah satu box sepenuhnya berada di dalam box lain,
    terlepas dari IoU-nya (kasus nested yang gagal ditangkap NMS IoU)."""
    ix1 = max(b1[0], b2[0]); iy1 = max(b1[1], b2[1])
    ix2 = min(b1[2], b2[2]); iy2 = min(b1[3], b2[3])
    inter = max(0, ix2-ix1) * max(0, iy2-iy1)
    a1 = max(0, b1[2]-b1[0]) * max(0, b1[3]-b1[1])
    a2 = max(0, b2[2]-b2[0]) * max(0, b2[3]-b2[1])
    return inter / (min(a1, a2) + 1e-6)

def _merge_adjacent(dets, iou_thr=MERGE_IOU, ios_thr=MERGE_IOS, conf_thr=MERGE_CONF,
                    min_area=200):
    """
    Agregasi (Tahap 0): kelompokkan box yang saling berdekatan (IoU > iou_thr)
    ATAU saling mengandung (IoS > ios_thr) menjadi satu box union. Conf kelas
    yang dipakai adalah dari anggota ber-conf tertinggi. Mencegah puluhan kotak
    kecil yang sebenarnya mewakili satu hamparan kanopi yang sama.

    Mengembalikan daftar box hasil gabungan (belum melalui hard NMS).
    """
    cand = [d for d in dets if d["conf"] >= conf_thr and d.get("area", 0) >= min_area]
    if not cand:
        return []
    cand.sort(key=lambda d: -d["conf"])
    n = len(cand)
    used = [False] * n
    out = []
    for i in range(n):
        if used[i]:
            continue
        used[i] = True
        cluster = [cand[i]]
        for j in range(i + 1, n):
            if used[j]:
                continue
            # Bandingkan terhadap box perwakilan cluster (yang ber-conf tertinggi)
            if (_iou_pair(cand[i]["bbox"], cand[j]["bbox"]) > iou_thr or
                    _ios_pair(cand[i]["bbox"], cand[j]["bbox"]) > ios_thr):
                cluster.append(cand[j])
                used[j] = True
        x1 = min(c["bbox"][0] for c in cluster)
        y1 = min(c["bbox"][1] for c in cluster)
        x2 = max(c["bbox"][2] for c in cluster)
        y2 = max(c["bbox"][3] for c in cluster)
        rep = max(cluster, key=lambda c: c["conf"])
        out.append({
            "bbox":  (x1, y1, x2, y2),
            "class": rep["class"],
            "conf":  rep["conf"],
            "area":  int((x2 - x1) * (y2 - y1)),
            "source": "merged" if len(cluster) > 1 else rep.get("source", "yolo-det"),
        })
    return out

def _ios_suppress(dets, ios_thr=IOS_THR):
    """
    Buang box kecil yang sebagian besar terkandung di dalam box yang lebih besar
    dan ber-confidence lebih tinggi (IoS >= ios_thr). Melengkapi hard NMS yang
    hanya melihat IoU — khusus menangani kasus box nested.
    """
    if len(dets) < 2:
        return dets
    order = sorted(range(len(dets)), key=lambda i: -dets[i]["conf"])
    kept = []
    for i in order:
        b = dets[i]["bbox"]
        b_area = max(0, b[2]-b[0]) * max(0, b[3]-b[1])
        drop = False
        for j in kept:
            bj = dets[j]["bbox"]
            j_area = max(0, bj[2]-bj[0]) * max(0, bj[3]-bj[1])
            # Hanya buang box yang lebih kecil; jangan sampai membuang box besar.
            if b_area > j_area:
                continue
            if _ios_pair(b, bj) >= ios_thr:
                drop = True
                break
        if not drop:
            kept.append(i)
    return [dets[i] for i in kept]

def _hard_nms(dets, iou_thr):
    """Standard greedy NMS — hapus box ber-IoU > iou_thr dengan box conf lebih tinggi."""
    if not dets: return []
    sc   = np.array([d["conf"] for d in dets], np.float32)
    order = np.argsort(-sc)
    iou  = _iou_matrix(np.array([d["bbox"] for d in dets], np.float32))
    suppressed = np.zeros(len(dets), bool)
    keep = []
    for i in order:
        if suppressed[i]: continue
        keep.append(i)
        suppressed |= iou[i] > iou_thr
        suppressed[i] = False
    return [dets[i] for i in keep]

def _soft_nms(dets, iou_thr, sigma=0.5, min_conf=DISPLAY_MIN_CONF):
    """
    Soft-NMS (Gaussian decay) — daripada menghapus box langsung, turunkan
    confidence-nya secara eksponensial berdasarkan overlap. Cocok untuk cross-class
    suppression agar box di area perbatasan tidak hilang sama sekali.
    """
    if not dets: return []
    boxes = [dict(d) for d in dets]  # copy agar tidak ubah original
    result = []
    while boxes:
        # Ambil box conf tertinggi
        idx = int(np.argmax([b["conf"] for b in boxes]))
        best = boxes.pop(idx)
        result.append(best)
        # Decay conf box lain berdasarkan overlap
        remaining = []
        for b in boxes:
            iou = _iou_pair(best["bbox"], b["bbox"])
            if iou > iou_thr:
                b = dict(b)
                b["conf"] = float(b["conf"]) * np.exp(-(iou**2) / sigma)
            if b["conf"] >= min_conf:
                remaining.append(b)
        boxes = remaining
    return result

def nms(dets, per_cls_iou=0.20, cross_iou=0.30, min_conf=DISPLAY_MIN_CONF,
        min_area=600, merge=None, ios_suppress=None, max_display=None):
    """
    NMS multi-tahap (v8):
    0. Agregasi (merge_adjacent) — gabung box berdekatan / saling mengandung
       menjadi satu box union (dapat dimatikan via MERGE_ENABLE).
    1. Hard NMS per kelas (IoU=0.20) — ketat untuk kelas sama.
    2. Hard NMS cross kelas (IoU=0.30).
    3. IoS suppression — buang box kecil yang terkandung di box besar.
    4. min_conf & min_area — filter noise box kecil / conf rendah.

    v7c (per=0.20, cross=0.30, area=600) sudah baik secara IoU, namun model
    UAV-det mengeluarkan 80+ box/frame dengan banyak box nested (IoU rendah,
    IoS tinggi) sehingga tampilan masih dipenuhi kotak kecil bertumpuk. v8
    menambah tahap agregasi & IoS suppression untuk menanganinya.
    Parameter merge/ios/max_display dapat di-override dari pemanggil.
    """
    if not dets:
        return []

    # Ambang configurable (fallback ke konstanta env jika tidak diberikan)
    _merge_en = MERGE_ENABLE if merge is None else merge
    _ios_en   = IOS_SUPPRESS if ios_suppress is None else ios_suppress
    _max_disp = MAX_DISPLAY if max_display is None else max_display

    # Filter confidence dan area minimum (early reject murah)
    dets = [d for d in dets if d["conf"] >= min_conf and d.get("area", 0) >= min_area]
    if not dets:
        return []

    # Tahap 0: Agregasi box berdekatan / nested
    if _merge_en:
        merged = _merge_adjacent(dets, conf_thr=min_conf, min_area=min_area)
        if merged:
            dets = merged

    # Tahap 1: Hard NMS per kelas
    by_cls = {}
    for d in dets:
        by_cls.setdefault(d["class"], []).append(d)
    after_per_cls = []
    for lst in by_cls.values():
        after_per_cls.extend(_hard_nms(lst, per_cls_iou))

    # Tahap 2: Hard NMS cross kelas
    result = _hard_nms(after_per_cls, cross_iou)

    # Tahap 3: IoS suppression (buang box nested kecil)
    if _ios_en:
        result = _ios_suppress(result)

    # Tahap 4: batasi jumlah box final yang ditampilkan/dihitung
    if _max_disp and _max_disp > 0 and len(result) > _max_disp:
        result = sorted(result, key=lambda d: -d["conf"])[:_max_disp]

    return result

# ── FHI ────────────────────────────────────────────────────────────────────────
def compute_fhi(dets, frame_area):
    if not dets: return 50.0
    total_w = sum(d["area"] for d in dets) + 1e-6
    base = 0.0
    for d in dets:
        w = d["area"] / total_w
        sev = SEVERITY.get(d["class"], 0.5)
        base += w * (1.0 - sev)
    return round(base * 100.0, 1)

# ── EMA temporal smoothing ─────────────────────────────────────────────────────
class EMASmooth:
    def __init__(self, alpha=0.30):
        self.alpha = alpha
        self.state = {}

    def update(self, key, val):
        if key not in self.state:
            self.state[key] = val
        else:
            self.state[key] = self.alpha*val + (1-self.alpha)*self.state[key]
        return self.state[key]

# ── Visualisasi ────────────────────────────────────────────────────────────────
def draw_stream(frame, dets, fhi):
    """Render bounding boxes onto frame. FHI sidebar dihapus."""
    out = frame.copy()
    for d in dets:
        x1, y1, x2, y2 = d["bbox"]
        col = COLORS.get(d["class"], (200, 200, 200))
        thick = 3 if d.get("source", "").startswith("yolo") else 2
        cv2.rectangle(out, (x1, y1), (x2, y2), col, thick)
        label = f"{d['class']} {d['conf']:.2f}"
        (tw, th), _ = cv2.getTextSize(label, cv2.FONT_HERSHEY_SIMPLEX, 0.45, 1)
        ty = max(y1, th + 6)
        cv2.rectangle(out, (x1, ty - th - 6), (x1 + tw + 4, ty), col, -1)
        cv2.putText(out, label, (x1 + 2, ty - 3),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.45, (0, 0, 0), 1, cv2.LINE_AA)
    return out

# ── Build GCS detection summary ────────────────────────────────────────────────
def build_gcs_counts(dets, fhi):
    counts = {"Healthy": 0.0, "Stress": 0.0, "Drought": 0.0, "Bare Soil": 0.0}
    total_area = sum(d["area"] for d in dets) or 1
    for d in dets:
        gcs_key = DISPLAY_TO_GCS.get(d["class"])
        if gcs_key:
            counts[gcs_key] += d["area"] / total_area * 100.0
    visible = {k: round(v, 1) for k, v in counts.items() if v > 0.5}
    parts   = sorted(visible.items(), key=lambda x: -x[1])
    summary = " | ".join(f"{k}: {v:.0f}%" for k, v in parts)
    return visible, summary

# ── Recommendation Bridge ───────────────────────────────────────────────────────
def _run_recommendation_bridge(healthy: float, stress: float, drought: float,
                                bare_soil: float, fhi: float) -> None:
    """
    Spawn recommendation_bridge.py in background after stream ends.
    Output goes to ~/HarvestmoonGCS/recommendations.json which is
    watched by RecommendationService.cs in the C# GCS.
    """
    import subprocess, sys, os
    from pathlib import Path

    # Locate bridge script relative to this file or the GCS directory
    candidates = [
        Path(__file__).parent.parent.parent / "Pigeon_Harvest" / "HarvestmoonGCS" / "recommendation_bridge.py",
        Path(__file__).parent / "recommendation_bridge.py",
        Path.home() / "Program" / "Harvestmoon" / "Pigeon_Harvest" / "HarvestmoonGCS" / "recommendation_bridge.py",
    ]
    bridge = next((str(p) for p in candidates if p.exists()), None)
    if bridge is None:
        emit({"type": "info", "data": "Recommendation bridge not found — skipping"})
        return

    out_path = str(Path.home() / "HarvestmoonGCS" / "recommendations.json")
    cmd = [
        sys.executable, bridge,
        "--healthy",   str(round(healthy,  2)),
        "--stress",    str(round(stress,   2)),
        "--drought",   str(round(drought,  2)),
        "--bare_soil", str(round(bare_soil,2)),
        "--fhi",       str(round(fhi,      2)),
        "--output_path", out_path,
    ]
    try:
        subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         start_new_session=True)
        emit({"type": "info", "data": f"Recommendation engine started → {out_path}"})
    except Exception as e:
        emit({"type": "info", "data": f"Recommendation bridge error: {e}"})

# ── Emit helper ────────────────────────────────────────────────────────────────
def emit(payload):
    print(json.dumps(payload), flush=True)

# ── Main streaming loop ────────────────────────────────────────────────────────
def main():
    global DEMO_MODE, DISPLAY_MIN_CONF
    global MERGE_ENABLE, MERGE_IOU, MERGE_CONF, MERGE_IOS, IOS_SUPPRESS, IOS_THR, MAX_DISPLAY

    parser = argparse.ArgumentParser(description="MoonHarvest HSV-First stream v8")
    parser.add_argument("--source",       required=True, help="Video path or camera index")
    parser.add_argument("--model",        default="",    help="ONNX/PT classifier (opsional)")
    parser.add_argument("--det-model",    default="",    help="YOLO-det ONNX (opsional)")
    parser.add_argument("--max-fps",      type=float, default=15.0)
    parser.add_argument("--playback-rate",type=float, default=1.0)
    parser.add_argument("--demo",         action="store_true")
    parser.add_argument("--no-yolo",      action="store_true",
                        help="Disable YOLO detector entirely (HSV/ONNX only)")
    parser.add_argument("--no-overlay",   action="store_true",
                        help="Disable bounding box and HUD overlay on video frames (passthrough clean video)")
    # ── v8: tuning agregasi & suppression box (opsional; override env) ──
    parser.add_argument("--min-conf",     type=float, default=None,
                        help=f"Confidence minimum box ditampilkan (default {DISPLAY_MIN_CONF} / env)")
    parser.add_argument("--merge-iou",    type=float, default=None,
                        help=f"Ambang IoU penggabungan box berdekatan (default {MERGE_IOU})")
    parser.add_argument("--merge-conf",   type=float, default=None,
                        help=f"Confidence minimum box yang ikut digabung (default {MERGE_CONF})")
    parser.add_argument("--ios-thr",      type=float, default=None,
                        help=f"Ambang IoS suppression box nested (default {IOS_THR})")
    parser.add_argument("--max-display",  type=int,   default=None,
                        help=f"Batas jumlah box final (default {MAX_DISPLAY})")
    parser.add_argument("--no-merge",     action="store_true",
                        help="Matikan tahap agregasi/merge box")
    parser.add_argument("--no-ios",       action="store_true",
                        help="Matikan tahap IoS suppression")
    args = parser.parse_args()

    # Terapkan override CLI ke konstanta modul (CLI > env > default)
    if args.min_conf    is not None: DISPLAY_MIN_CONF = args.min_conf
    if args.merge_iou   is not None: MERGE_IOU       = args.merge_iou
    if args.merge_conf  is not None: MERGE_CONF      = args.merge_conf
    if args.ios_thr     is not None: IOS_THR         = args.ios_thr
    if args.max_display is not None: MAX_DISPLAY     = args.max_display
    if args.no_merge:                MERGE_ENABLE    = False
    if args.no_ios:                  IOS_SUPPRESS    = False

    # --no-yolo flag disables YOLO detector entirely
    no_yolo = args.no_yolo

    if args.demo:
        DEMO_MODE = True

    signal.signal(signal.SIGINT,  lambda s, f: sys.exit(0))
    signal.signal(signal.SIGTERM, lambda s, f: sys.exit(0))

    src = int(args.source) if args.source.isdigit() else args.source
    cap = cv2.VideoCapture(src)
    if not cap.isOpened():
        emit({"type": "error", "data": f"Cannot open: {args.source}"})
        return 1

    # Send preview frame immediately before loading any heavy models
    ret_preview, preview = cap.read()
    if ret_preview and preview is not None:
        ok, buf = cv2.imencode(".jpg", preview, [cv2.IMWRITE_JPEG_QUALITY, 93])
        if ok:
            emit({"type": "frame", "data": base64.b64encode(buf).decode()})
        if not isinstance(src, int) and not str(src).startswith(("http://", "https://", "rtsp://", "udp://")):
            cap.set(cv2.CAP_PROP_POS_FRAMES, 0)

    # Load ONNX classifier
    classifier = None
    if args.model and os.path.isfile(args.model):
        try:
            classifier = load_classifier(args.model)
            emit({"type": "info", "data": f"Classifier: {Path(args.model).name}"})
        except Exception as e:
            emit({"type": "info", "data": f"Classifier load error ({e}), HSV-only"})
    else:
        emit({"type": "info", "data": "HSV-only mode (no classifier)"})

    # Load YOLO-det
    detector = None
    if not no_yolo:
        det_path = getattr(args, "det_model", "")
        if not det_path:
            det_candidates = [
                (Path(args.model).parent / "moonharvest-uav-det.onnx") if args.model else None,
                Path(__file__).parent.parent / "model" / "moonharvest-uav-det.onnx",
                Path(__file__).parent.parent.parent / "Pigeon_Harvest" / "HarvestmoonGCS" / "Assets" / "models" / "moonharvest-uav-det.onnx",
                Path("/home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/model/moonharvest-uav-det.onnx"),
                Path("/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/Assets/models/moonharvest-uav-det.onnx"),
            ]
            det_path = next((str(p) for p in det_candidates if p and p.exists()), "")
        if det_path and os.path.isfile(det_path):
            try:
                detector = YOLODetector(det_path)
                emit({"type": "info", "data": f"YOLO-det: {Path(det_path).name}"})
            except Exception as e:
                emit({"type": "info", "data": f"YOLO-det load error ({e})"})
    else:
        emit({"type": "info", "data": "YOLO disabled via --no-yolo (HSV/ONNX only)"})

    is_live_stream = not isinstance(src, int) and str(src).startswith(("http://", "https://", "rtsp://", "udp://"))
    src_fps        = cap.get(cv2.CAP_PROP_FPS) or 30.0
    if is_live_stream:
        # Live stream jaringan: jangan skip frame secara artifisial, ikuti rate kedatangan paket live
        target_fps  = float(args.max_fps)
        step        = 1
        frame_delay = 0.0
    elif DEMO_MODE and not isinstance(src, int):
        target_fps = float(args.max_fps)
        step       = max(1, int(round(src_fps / target_fps))) if target_fps <= src_fps else 1
        playback_rate = max(0.25, min(2.0, float(args.playback_rate)))
        frame_delay = (1.0 / target_fps) / playback_rate
    else:
        target_fps  = min(float(args.max_fps), src_fps)
        step        = max(1, int(round(src_fps / target_fps))) if target_fps <= src_fps else 1
        playback_rate = max(0.25, min(2.0, float(args.playback_rate)))
        frame_delay = (1.0 / target_fps) / playback_rate

    PROC_W = 640
    _emit_q: queue.Queue = queue.Queue(maxsize=1)
    _stop   = threading.Event()

    # Session-level accumulators for recommendation engine
    _session_classes = {"Healthy": [], "Stress": [], "Drought": [], "Bare Soil": []}
    _session_fhi     = []

    def compute_loop():
        nonlocal cap
        ema    = EMASmooth(HSV_CFG["ema_alpha"])
        fidx   = 0
        eof_retries = 0

        cached_dets = []
        cached_fhi  = 50.0

        while not _stop.is_set():
            ret, frame = cap.read()
            if not ret or frame is None:
                # Live network stream (drone HTTP/RTSP): coba baca lagi jika terjadi glitch sesaat
                if not isinstance(src, int) and str(src).startswith(("http://", "https://", "rtsp://", "udp://")):
                    eof_retries += 1
                    if eof_retries > 10:
                        try:
                            cap.release()
                            time.sleep(0.5)
                            cap = cv2.VideoCapture(src)
                        except Exception:
                            pass
                        eof_retries = 0
                    else:
                        time.sleep(0.03)
                    continue

                if DEMO_MODE and not isinstance(src, int):
                    # Demo mode: loop video selamanya tanpa batas
                    eof_retries += 1
                    cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
                    fidx = 0
                    if eof_retries > 3:
                        try:
                            cap.release()
                            time.sleep(0.05)
                            cap = cv2.VideoCapture(src)
                        except Exception:
                            pass
                        eof_retries = 0
                    continue
                _emit_q.put(None)
                break
            eof_retries = 0
            fidx += 1
            if fidx % step != 0:
                continue

            fh, fw = frame.shape[:2]
            if fw > PROC_W:
                frame = cv2.resize(frame, (PROC_W, int(fh * PROC_W / fw)))

            try:
                if getattr(args, "no_overlay", False) and no_yolo:
                    # Pure clean video stream (pre-annotated video: no double boxes, no CPU overhead)
                    vis = frame
                    dets = []
                    fhi = 85.0
                else:
                    # ── DETECTION PIPELINE ──
                    if detector:
                        dets = detector.detect(frame)
                        # Fallback HSV HANYA jika YOLO benar-benar 0 deteksi
                        if len(dets) == 0:
                            regions = segment_regions(frame, HSV_CFG)
                            dets += [{"bbox": r["bbox"], "class": r["hsv_display"],
                                      "conf": 0.50, "area": r["area"], "source": "hsv-fallback"}
                                     for r in regions]
                    elif classifier:
                        regions = segment_regions(frame, HSV_CFG)
                        dets = fuse(frame, regions, classifier)
                    else:
                        regions = segment_regions(frame, HSV_CFG)
                        dets = [{"bbox": r["bbox"], "class": r["hsv_display"],
                                 "conf": 0.60, "area": r["area"], "source": "hsv-only"}
                                for r in regions]

                    dets = nms(dets, per_cls_iou=0.25, cross_iou=0.40, min_area=200,
                               min_conf=DISPLAY_MIN_CONF)
                    fhi_raw = compute_fhi(dets, frame.shape[0] * frame.shape[1])
                    fhi     = ema.update("fhi", fhi_raw)
                    if getattr(args, "no_overlay", False):
                        vis = frame
                    else:
                        vis = draw_stream(frame, dets, fhi)
                    cached_dets = dets
                    cached_fhi  = fhi
            except Exception as exc:
                emit({"type": "error", "data": str(exc)})
                continue

            ok, buf = cv2.imencode(".jpg", vis, [cv2.IMWRITE_JPEG_QUALITY, 93])
            if not ok:
                continue

            visible, summary = build_gcs_counts(dets, fhi)
            # Accumulate per-frame class data for end-of-session recommendation
            for k in _session_classes:
                _session_classes[k].append(visible.get(k, 0.0))
            _session_fhi.append(round(fhi, 1))
            payload = {
                "jpg":     base64.b64encode(buf).decode(),
                "count":   len(dets),
                "summary": summary,
                "classes": visible,
                "fhi":     round(fhi, 1),
            }
            if is_live_stream:
                try:
                    _emit_q.put_nowait(payload)
                except queue.Full:
                    pass
            else:
                # Video file / demo playback: do NOT drop frames, wait for consumer to pace at target FPS
                while not _stop.is_set():
                    try:
                        _emit_q.put(payload, timeout=0.1)
                        break
                    except queue.Full:
                        continue

    t_compute = threading.Thread(target=compute_loop, daemon=True)
    t_compute.start()

    t_last = time.time()
    try:
        while True:
            try:
                item = _emit_q.get(timeout=5.0)
            except queue.Empty:
                if not t_compute.is_alive():
                    break
                continue

            if item is None:
                emit({"type": "end", "data": "Video stream ended"})
                # Run recommendation engine with session averages
                if _session_fhi:
                    avg_fhi     = sum(_session_fhi) / len(_session_fhi)
                    avg_healthy = sum(_session_classes["Healthy"])   / len(_session_fhi)
                    avg_stress  = sum(_session_classes["Stress"])    / len(_session_fhi)
                    avg_drought = sum(_session_classes["Drought"])   / len(_session_fhi)
                    avg_bare    = sum(_session_classes["Bare Soil"]) / len(_session_fhi)
                    _run_recommendation_bridge(avg_healthy, avg_stress,
                                               avg_drought, avg_bare, avg_fhi)
                break

            emit({"type": "frame",     "data": item["jpg"]})
            emit({"type": "detection", "data": {
                "count":   item["count"],
                "summary": item["summary"],
                "classes": item["classes"],
                "fhi":     item["fhi"],
            }})

            elapsed = time.time() - t_last
            sleep_t = frame_delay - elapsed
            if sleep_t > 0.004:
                time.sleep(sleep_t)
            t_last = time.time()

    except Exception as exc:
        emit({"type": "error", "data": str(exc)})
    finally:
        _stop.set()
        cap.release()
        try:
            while not _emit_q.empty():
                _emit_q.get_nowait()
        except Exception:
            pass
        t_compute.join(timeout=2.0)

    return 0


if __name__ == "__main__":
    sys.exit(main())
