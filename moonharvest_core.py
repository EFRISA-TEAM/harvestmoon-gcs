#!/usr/bin/env python3
"""
MoonHarvest Proprietary Vision & Licensing Core
===============================================
PERINGATAN HAK CIPTA:
Modul ini berisi proprietary computer vision pipeline, agronomy logic,
dan sistem lisensi hak cipta MoonHarvest.

Modul ini dikompilasi menjadi library biner mesin C (moonharvest_core.so / .pyd)
sehingga kode sumber dan algoritma terlindungi dari dekompilasi.
"""

from __future__ import annotations

import os
import sys
import time
import json
from pathlib import Path
from typing import Any, Dict, List, Tuple

import cv2
import numpy as np

# Import modul keamanan terpadu
from moonharvest_security import (
    enforce_security,
    verify_system_access,
    get_hardware_id,
    ModelCrypto
)

# ── Konfigurasi Agronomi & Klasifikasi Sawah 15 Hari ───────────────────────────
DEFAULT_COLORS = {
    "Lush Green":              ( 50, 210,  50),
    "Inconsistent Growth":     (  0, 200, 255),
    "Drought / Severe Stress": (  0,  90, 255),
    "Bare Soil / Gap":         (140, 140, 140),
}

DEFAULT_SEVERITY = {
    "Lush Green":              0.00,
    "Inconsistent Growth":     0.23,
    "Drought / Severe Stress": 0.80,
    "Bare Soil / Gap":         0.10,
}

DEFAULT_HSV_CFG = {
    "wb":              True,
    "clahe_clip":      2.0,
    "clahe_grid":      8,
    "shadow_v_max":    35,
    "exg_veg_thr":     0.004,
    "exg_healthy_min": 0.028,
    "lush":    {"h": [30, 90], "s_lo": 25, "v_lo": 50, "exg_min": 0.028},
    "stress":  {"h": [12, 105], "s_lo":  8, "s_hi": 80, "v_lo": 42, "exg_lo": 0.000, "exg_hi": 0.040},
    "drought": {"h": [ 0,  18], "s_lo": 18, "s_hi": 60, "v_lo": 120, "exg_hi": 0.003, "exg_remap_thr": 0.006},
    "soil":    {"s_hi": 15, "v_lo": 75, "exg_hi": 0.008},
    "min_area_frac":   0.003,
    "max_area_frac":   0.06,
    "morph_k":         5,
    "ema_alpha":       0.30,
}


def compute_exg_index(bgr: np.ndarray) -> np.ndarray:
    """Menghitung Excess Green Vegetation Index (2G - R - B) / (R + G + B + eps)."""
    b = bgr[:, :, 0].astype(np.float32)
    g = bgr[:, :, 1].astype(np.float32)
    r = bgr[:, :, 2].astype(np.float32)
    denom = r + g + b + 1e-6
    return (2.0 * g - r - b) / denom


def apply_white_balance_and_clahe(bgr: np.ndarray, clip_limit: float = 2.0, grid_size: int = 8) -> np.ndarray:
    """Normalisasi pencahayaan kanopi sawah dengan White Balance dan CLAHE."""
    # Gray world white balance
    avg_b = np.mean(bgr[:, :, 0]) + 1e-5
    avg_g = np.mean(bgr[:, :, 1]) + 1e-5
    avg_r = np.mean(bgr[:, :, 2]) + 1e-5
    avg_gray = (avg_b + avg_g + avg_r) / 3.0

    b = np.clip(bgr[:, :, 0] * (avg_gray / avg_b), 0, 255).astype(np.uint8)
    g = np.clip(bgr[:, :, 1] * (avg_gray / avg_g), 0, 255).astype(np.uint8)
    r = np.clip(bgr[:, :, 2] * (avg_gray / avg_r), 0, 255).astype(np.uint8)
    balanced = cv2.merge([b, g, r])

    # CLAHE pada channel Luminance (LAB)
    lab = cv2.cvtColor(balanced, cv2.COLOR_BGR2LAB)
    clahe = cv2.createCLAHE(clipLimit=clip_limit, tileGridSize=(grid_size, grid_size))
    lab[:, :, 0] = clahe.apply(lab[:, :, 0])
    return cv2.cvtColor(lab, cv2.COLOR_LAB2BGR)


def compute_field_health_index(class_counts: Dict[str, int]) -> float:
    """
    Menghitung skor FHI (Field Health Index) agronomis tanaman padi (0 - 100%).
    FHI = 100 - (Sigma(Count_i * Severity_i) / Total * 100)
    """
    total = sum(class_counts.values())
    if total == 0:
        return 100.0

    penalty = sum(class_counts.get(cls_name, 0) * DEFAULT_SEVERITY.get(cls_name, 0.0) for cls_name in class_counts)
    fhi = 100.0 * (1.0 - (penalty / total))
    return float(np.clip(fhi, 0.0, 100.0))


class HarvestVisionEngine:
    """
    Engine Analisis Kesehatan Tanaman MoonHarvest.
    Klien memanggil kelas ini dari kode Python terbuka mereka.
    """

    def __init__(self, config: Dict[str, Any] | None = None, auto_enforce_license: bool = True):
        # 1. Pengecekan Keamanan dan Lisensi Mandatori
        if auto_enforce_license:
            enforce_security(exit_on_failure=True)

        self.config = DEFAULT_HSV_CFG.copy()
        if config:
            self.config.update(config)

        self.colors = DEFAULT_COLORS
        self.frame_index = 0
        self.last_fhi = 100.0

    def analyze_frame(self, frame: np.ndarray) -> Dict[str, Any]:
        """
        Analisis satu frame gambar UAV:
        1. Preprocessing (WB + CLAHE)
        2. Ekstraksi ExG dan segmentasi HSV Multi-Zona
        3. Deteksi wilayah terdampak & FHI
        """
        # Periodic security heartbeat setiap 100 frame
        self.frame_index += 1
        if self.frame_index % 100 == 0:
            is_ok, reason = verify_system_access()
            if not is_ok:
                raise PermissionError(f"[MoonHarvest Lockout] Sesi dihentikan: {reason}")

        h, w = frame.shape[:2]
        processed_bgr = apply_white_balance_and_clahe(
            frame,
            clip_limit=self.config.get("clahe_clip", 2.0),
            grid_size=self.config.get("clahe_grid", 8)
        )
        hsv = cv2.cvtColor(processed_bgr, cv2.COLOR_BGR2HSV)
        exg = compute_exg_index(processed_bgr)

        H, S, V = hsv[:, :, 0], hsv[:, :, 1], hsv[:, :, 2]
        shadow = V < self.config.get("shadow_v_max", 35)

        # Segmentasi Zona Agronomi
        c = self.config["lush"]
        lush_mask = ((H >= c["h"][0]) & (H <= c["h"][1]) & (S >= c["s_lo"]) &
                     (V >= c["v_lo"]) & (exg >= c["exg_min"]) & ~shadow)

        c = self.config["stress"]
        stress_mask = ((H >= c["h"][0]) & (H <= c["h"][1]) & (S >= c["s_lo"]) & (S <= c["s_hi"]) &
                       (V >= c["v_lo"]) & (exg >= c["exg_lo"]) & (exg < c["exg_hi"]) &
                       ~lush_mask & ~shadow)

        c = self.config["drought"]
        drought_mask = ((H >= c["h"][0]) & (H <= c["h"][1]) & (S >= c["s_lo"]) & (S <= c["s_hi"]) &
                        (V >= c["v_lo"]) & (exg < c["exg_hi"]) & ~lush_mask & ~stress_mask & ~shadow)

        c = self.config["soil"]
        soil_mask = ((S <= c["s_hi"]) & (V >= c["v_lo"]) & (exg < c["exg_hi"]) &
                     ~lush_mask & ~stress_mask & ~drought_mask & ~shadow)

        # Hitung statistik area
        counts = {
            "Lush Green": int(np.sum(lush_mask)),
            "Inconsistent Growth": int(np.sum(stress_mask)),
            "Drought / Severe Stress": int(np.sum(drought_mask)),
            "Bare Soil / Gap": int(np.sum(soil_mask)),
        }

        fhi = compute_field_health_index(counts)
        self.last_fhi = fhi

        return {
            "frame_index": self.frame_index,
            "fhi": fhi,
            "counts": counts,
            "status": "Healthy" if fhi >= 75 else ("Warning" if fhi >= 50 else "Critical"),
            "hardware_id": get_hardware_id()
        }

    def annotate_frame(self, frame: np.ndarray, analysis: Dict[str, Any]) -> np.ndarray:
        """Membuat overlay visual HUD pada frame citra."""
        annotated = frame.copy()
        fhi = analysis.get("fhi", 100.0)
        status = analysis.get("status", "Unknown")

        # HUD Panel atas
        hud_color = (0, 200, 0) if fhi >= 75 else ((0, 165, 255) if fhi >= 50 else (0, 0, 255))
        cv2.rectangle(annotated, (10, 10), (320, 80), (20, 20, 20), -1)
        cv2.rectangle(annotated, (10, 10), (320, 80), hud_color, 2)

        cv2.putText(annotated, f"MoonHarvest FHI: {fhi:.1f}%", (20, 40),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, (255, 255, 255), 2)
        cv2.putText(annotated, f"Status: {status}", (20, 68),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, hud_color, 2)

        return annotated
