#!/usr/bin/env python3
"""
MoonHarvest Recommendation Bridge
==================================
Dipanggil oleh moonharvest_detect_stream.py setelah pipeline deteksi selesai.
Membaca class_percentages + FHI dari argumen, menjalankan RecommendationEngine,
dan menulis hasil ke recommendations.json di output_dir.

Usage:
    python3 recommendation_bridge.py \
        --healthy 45.2 --stress 32.1 --drought 15.3 --bare_soil 7.4 \
        --fhi 73.2 --area_ha 1.0 --days_after_transplant 30 \
        --output_path /path/to/recommendations.json

Output JSON schema:
{
  "timestamp": "2026-08-23T14:22:00",
  "fhi": 73.2,
  "field_status": "moderate_stress",
  "urgency": "moderate",
  "dominant_condition": "stressed_crop",
  "actions": [
    {"action": "...", "timing": "within_24_hours", "priority_score": 0.75}
  ],
  "top_recommendations": ["...", "...", "..."],
  "cost_estimate": {"low": 50000, "high": 150000},
  "journal_references": ["...", "..."]
}
"""

import argparse
import json
import os
import sys
from datetime import datetime
from pathlib import Path

# Resolve recommendation engine path
_THIS_DIR = Path(__file__).parent
_REC_DIR = _THIS_DIR / "recommendations"
if not _REC_DIR.is_dir():
    _REC_DIR = _THIS_DIR.parent / "recommendations"
if str(_REC_DIR) not in sys.path:
    sys.path.insert(0, str(_REC_DIR))

try:
    from recommendation_engine import RecommendationEngine
    _ENGINE_AVAILABLE = True
except ImportError:
    _ENGINE_AVAILABLE = False


def run_bridge(
    healthy: float,
    stress: float,
    drought: float,
    bare_soil: float,
    fhi: float,
    area_ha: float,
    days_after_transplant: int | None,
    output_path: str,
) -> dict:
    """Run recommendation engine and write JSON output file."""

    class_percentages = {
        "healthy_crop":  healthy,
        "stressed_crop": stress,
        "drought_stress": drought,
        "bare_soil":     bare_soil,
    }

    if _ENGINE_AVAILABLE:
        try:
            engine = RecommendationEngine()
            report = engine.analyze_field(
                class_percentages=class_percentages,
                fhi=fhi,
                field_area_ha=area_ha,
                days_after_transplant=days_after_transplant if days_after_transplant and days_after_transplant > 0 else None,
            )

            # Extract top 3 human-readable action strings
            actions = report.get("recommended_actions", [])
            top_recs = []
            for act in actions[:5]:
                desc = act.get("action") or act.get("description") or str(act)
                top_recs.append(desc)

            # Extract cost estimate
            cost = report.get("cost_estimate", {})

            # Extract journal refs
            refs = report.get("journal_references", [])
            ref_texts = [r.get("reference", str(r)) if isinstance(r, dict) else str(r) for r in refs[:3]]

            result = {
                "timestamp": datetime.now().isoformat(timespec="seconds"),
                "fhi": fhi,
                "field_status": report.get("summary", {}).get("field_status", "unknown"),
                "urgency": report.get("summary", {}).get("urgency_level", "moderate"),
                "dominant_condition": report.get("summary", {}).get("dominant_condition", "unknown"),
                "crop_distribution": {
                    "healthy": healthy,
                    "stress": stress,
                    "drought": drought,
                    "bare_soil": bare_soil,
                },
                "actions": actions[:6],
                "top_recommendations": top_recs[:3],
                "cost_estimate": cost,
                "journal_references": ref_texts,
                "engine": "RecommendationEngine v1",
            }
        except Exception as e:
            result = _fallback_result(class_percentages, fhi, str(e))
    else:
        result = _fallback_result(class_percentages, fhi, "RecommendationEngine not available")

    # Write to output path atomically
    out_path = Path(output_path)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = out_path.with_suffix(".tmp")
    with open(tmp_path, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=2)
    tmp_path.replace(out_path)

    return result


def _fallback_result(class_pct: dict, fhi: float, reason: str) -> dict:
    """Generate a basic fallback recommendation when engine is unavailable."""
    healthy = class_pct.get("healthy_crop", 0)
    stress  = class_pct.get("stressed_crop", 0)
    drought = class_pct.get("drought_stress", 0)
    bare    = class_pct.get("bare_soil", 0)

    if fhi >= 75:
        field_status = "healthy"
        urgency = "low"
        recs = [
            f"Field health index {fhi:.1f} — kondisi baik. Lanjutkan monitoring rutin.",
            f"Zona Lush Green {healthy:.1f}% — pertumbuhan optimal. Pertahankan irigasi saat ini.",
            "Jadwalkan inspeksi darat pada siklus berikutnya untuk konfirmasi.",
        ]
    elif fhi >= 55:
        field_status = "moderate_stress"
        urgency = "moderate"
        recs = [
            f"Stress Zone {stress:.1f}% terdeteksi — lakukan pemeriksaan irigasi dalam 24–48 jam.",
            f"Drought area {drought:.1f}% — pertimbangkan irigasi tambahan di area prioritas.",
            f"Bare soil {bare:.1f}% — inspeksi fisik untuk deteksi hama atau kerusakan mekanis.",
        ]
    else:
        field_status = "high_stress"
        urgency = "high"
        recs = [
            f"FHI rendah ({fhi:.1f}) — tindakan segera diperlukan. Periksa irigasi dan pupuk.",
            f"Stress {stress:.1f}% + Drought {drought:.1f}% — prioritaskan zona terburuk untuk intervensi.",
            "Pertimbangkan sampling tanah untuk analisis kekurangan hara.",
        ]

    return {
        "timestamp": datetime.now().isoformat(timespec="seconds"),
        "fhi": fhi,
        "field_status": field_status,
        "urgency": urgency,
        "dominant_condition": max(class_pct, key=class_pct.get),
        "crop_distribution": {
            "healthy": healthy, "stress": stress,
            "drought": drought, "bare_soil": bare,
        },
        "actions": [],
        "top_recommendations": recs,
        "cost_estimate": {},
        "journal_references": [],
        "engine": f"Fallback ({reason})",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="MoonHarvest Recommendation Bridge")
    parser.add_argument("--healthy",  type=float, default=50.0)
    parser.add_argument("--stress",   type=float, default=30.0)
    parser.add_argument("--drought",  type=float, default=10.0)
    parser.add_argument("--bare_soil", type=float, default=10.0)
    parser.add_argument("--fhi",      type=float, default=70.0)
    parser.add_argument("--area_ha",  type=float, default=1.0)
    parser.add_argument("--days_after_transplant", type=int, default=0)
    parser.add_argument("--output_path", type=str,
                        default=str(Path.home() / "HarvestmoonGCS" / "recommendations.json"))
    args = parser.parse_args()

    result = run_bridge(
        healthy=args.healthy,
        stress=args.stress,
        drought=args.drought,
        bare_soil=args.bare_soil,
        fhi=args.fhi,
        area_ha=args.area_ha,
        days_after_transplant=args.days_after_transplant,
        output_path=args.output_path,
    )
    print(json.dumps(result, ensure_ascii=False, indent=2))
