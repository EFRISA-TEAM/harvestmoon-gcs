# INDEX — MoonHarvest GCS Datasheet PDP
**Sistem UAV Multispectral untuk Monitoring Irigasi Sawah Berbasis AI**  
Luaran PDP Hibah Nasional | PENS | Oktober 2026

---

## Struktur Folder

```
DATASHEET_PDP/
├── 01_GCS_Software/          ← Dokumentasi aplikasi MoonHarvest GCS
│   ├── PANDUAN_PENGGUNAAN_MOONHARVEST.docx   (panduan lengkap pengguna)
│   ├── PANDUAN_PENGGUNAAN_MOONHARVEST.md     (versi markdown)
│   ├── DOKUMEN_PIA_TEKNIS_BERDASARKAN_KODE.md (arsitektur kode teknis)
│   └── BOOTH_RUNBOOK_TEKNOFEST.docx          (runbook operasional booth)
│
├── 02_AI_Model/              ← Model YOLO & spesifikasi visi komputer
│   ├── VISION_DATASHEET_MOONHARVEST.docx    (datasheet utama model AI)
│   └── YOLO_MODELS_README.md                (panduan model & class files)
│
├── 03_Companion_System/      ← Dokumentasi Orange Pi companion drone
│   ├── COMPANION_SYSTEM_README.md           (cara setup & penggunaan)
│   ├── LAPORAN_VERIFIKASI_COMPANION.md      (hasil verifikasi sistem)
│   └── RINGKASAN_DRONE_ORANGEPI.md          (ringkasan konfigurasi)
│
├── 04_Flight_Checklist/      ← Checklist kesiapan penerbangan & demo
│   └── PRE_FLIGHT_CHECKLIST_PDP.md         ← ★ WAJIB ISI SEBELUM TERBANG
│
└── 05_Dokumen_Resmi/         ← Dokumen resmi proyek EFRISA
    ├── EFRISA - MOONHARVEST-1.pdf           (proposal/dokumen utama)
    ├── EFRISA TEKNO 26 - PROJECT PELIMINARY EVALUATION REPORT.pdf
    ├── MANUAL_BOOK.docx                     (buku manual lengkap)
    └── PANDUAN_PRESENTASI.docx              (panduan presentasi)
```

---

## Dokumen Prioritas untuk Demo PDP

| Prioritas | File | Kegunaan |
|-----------|------|---------|
| ★★★ | `02_AI_Model/VISION_DATASHEET_MOONHARVEST.docx` | Datasheet utama model AI untuk reviewer |
| ★★★ | `04_Flight_Checklist/PRE_FLIGHT_CHECKLIST_PDP.md` | Wajib sebelum setiap penerbangan |
| ★★ | `01_GCS_Software/PANDUAN_PENGGUNAAN_MOONHARVEST.docx` | Manual lengkap GCS |
| ★★ | `03_Companion_System/LAPORAN_VERIFIKASI_COMPANION.md` | Bukti sistem companion berjalan |
| ★ | `05_Dokumen_Resmi/EFRISA - MOONHARVEST-1.pdf` | Untuk dokumen resmi ke reviewer |

---

## Lokasi Sumber File di Repositori

| Folder Datasheet | Sumber Asli |
|-----------------|-------------|
| `01_GCS_Software/` | `Pigeon_Harvest/docs/` |
| `02_AI_Model/` | `Pigeon_Harvest/docs/` + `HarvestmoonGCS/Assets/models/` |
| `03_Companion_System/` | `Pigeon_Harvest/scripts/raspberry_pi5/companion/` |
| `05_Dokumen_Resmi/` | `Harvestmoon/docs/` + `Harvestmoon/TEKNOFEST_SIAP/dokumen/` |

> **Catatan:** File di folder ini adalah *salinan*. File master tetap di lokasi sumber asli.

---

*Terakhir diperbarui: Oktober 2026*
