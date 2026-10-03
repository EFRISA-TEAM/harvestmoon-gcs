================================================================================
  MOONHARVEST GCS — KATALOG LENGKAP SEMUA VIDEO PROYEK & DEMO TEKNOFEST 2026
================================================================================
Lokasi Folder:
  /home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/

Semua berkas video (baik video olahan AI demo maupun sumber rekaman asli UAV)
telah dikumpulkan dan dapat diakses langsung di sini:

--------------------------------------------------------------------------------
A. VIDEO OLAHAN DEMO & HASIL DETEKSI AI (SIAP DINILAI / DIPUTAR)
--------------------------------------------------------------------------------
1. stream_multisector_60s_detected.mp4 (11.6 MB · 60s · 15 FPS)
   - Video hasil olahan utama multi-sektor lengkap dengan bounding box AI, label
     kelas (Lush Green, Inconsistent, Drought, Bare Soil), dan banner HUD FHI.
   - Detik 00-15: Sektor A · Vegetatif Subur (FHI 96.5%)
   - Detik 15-30: Sektor B · Padi 15 HST (FHI 82.1% · PPT Baseline)
   - Detik 30-45: Sektor C · Anomali Stres Kekeringan (FHI 78.3%)
   - Detik 45-60: Sektor D · Pemulihan Irigasi (FHI 93.4%)
   * Perintah Putar:
     mpv --loop=inf stream_multisector_60s_detected.mp4

2. stream_multisector_60s.mp4 (5.2 MB · 60s · 15 FPS)
   - Video gabungan raw (tanpa anotasi) yang digunakan oleh software MoonHarvest GCS
     untuk menjalankan inferensi AI YOLOv8 on-the-fly saat demo live.

3. stream_v7c_final.mp4 (2.7 MB · 15.5s · 15 FPS)
   - Video khusus Sawah Padi 15 HST untuk pembuktian ilmiah dokumen/PPT Slide 751
     ("mean FHI 81.6").

4. YDXJ_fused_only_detected.mp4 (3.9 MB · 35s · 10 FPS)
   - Skenario uji stres kekeringan berat dan tanah terbuka (memunculkan Caution Alert).

5. stream_multisector_survey.mp4 (7.7 MB · 32s · 15 FPS)
   - Video gabungan survei multi-sektor versi 32 detik sebelumnya.

6. test_deteksi.mp4 (100 KB)
   - Video sampel pengujian awal model.

--------------------------------------------------------------------------------
B. REKAMAN ASLI KAMERA DRONE UAV (RAW SOURCE FOOTAGE)
   (Tersedia di subfolder: sumber_rekaman_asli/)
--------------------------------------------------------------------------------
1. sumber_rekaman_asli/15d_asli_sawah_15hst.mp4 (11 MB)
   - Rekaman drone asli saat survei hamparan sawah umur 15 HST di Karawang.

2. sumber_rekaman_asli/YDX_burned_asli_lahan_kering.mp4 (27 MB)
   - Rekaman drone asli petakan sawah yang mengalami kekeringan dan klorosis daun.

3. sumber_rekaman_asli/YDXJ0012_asli_vegetasi_irigasi_full.mp4 (203 MB)
   - Rekaman drone asli berdurasi penuh (~3 menit 11 detik) mencakup hamparan
     vegetasi subur, area permukiman/pekarangan tepi sawah, dan saluran sungai irigasi.

================================================================================
Cara Buka Folder di GUI Desktop Ubuntu:
  xdg-open /home/fawwazfa/Program/Harvestmoon/TEKNOFEST_SIAP/demo_video/
================================================================================
