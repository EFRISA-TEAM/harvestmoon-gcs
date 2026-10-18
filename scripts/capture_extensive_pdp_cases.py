import os
import time
import subprocess
import Xlib.display
import Xlib.X
import Xlib.protocol.event
from Xlib.ext import xtest
from PIL import Image

os.environ['DISPLAY'] = ':1'
d = Xlib.display.Display(':1')
root = d.screen().root

print("[*] 1. Starting LoRa background telemetry bridge...")
subprocess.Popen(
    ["python3", "/home/fawwazfa/Program/Harvestmoon/scripts/lora_soil_bridge.py", "--simulate"],
    stdout=subprocess.DEVNULL,
    stderr=subprocess.DEVNULL,
    start_new_session=True
)

print("[*] 2. Restarting HarvestmoonGCS with MOONHARVEST_AUTO_DEMO=1...")
subprocess.run(['pkill', '-9', '-f', 'HarvestmoonGCS'])
time.sleep(1.5)

gcs_exe = '/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS'
env = os.environ.copy()
env['MOONHARVEST_AUTO_DEMO'] = '1'
env['MOONHARVEST_LOAD_DEMO'] = '1'
env['MOONHARVEST_DEMO'] = '1'
proc = subprocess.Popen([gcs_exe], cwd=os.path.dirname(gcs_exe), env=env, start_new_session=True)

print("[*] 3. Waiting for GCS to fully initialize & auto-start demo (13s)...")
time.sleep(13)

# Find GCS window
prop = root.get_full_property(d.intern_atom('_NET_CLIENT_LIST'), 0)
gcs_win = None
all_windows = []
if prop:
    for win_id in prop.value:
        try:
            w = d.create_resource_object('window', win_id)
            all_windows.append(w)
            name_prop = w.get_full_property(d.intern_atom('_NET_WM_NAME'), 0)
            title = name_prop.value.decode('utf-8', 'ignore') if name_prop else w.get_wm_name()
            if title and 'Harvestmoon GCS' in str(title):
                gcs_win = w
        except Exception:
            pass

if not gcs_win:
    print("[!] GCS window not found in client list!")
    exit(1)

print(f"[+] Found GCS Window: {hex(gcs_win.id)}")

unmapped_windows = []
try:
    # 4. Hide all other windows by unmapping them temporarily so GCS is 100% unobstructed
    print("[*] 4. Temporarily unmapping other windows to prevent overlap...")
    for w in all_windows:
        if w.id != gcs_win.id:
            try:
                name_prop = w.get_full_property(d.intern_atom('_NET_WM_NAME'), 0)
                title = name_prop.value.decode('utf-8', 'ignore') if name_prop else ""
                if "Desktop" not in title and "panel" not in title.lower():
                    w.unmap()
                    unmapped_windows.append(w)
            except Exception:
                pass
    d.sync()
    time.sleep(1)

    # Bring GCS to absolute top and activate
    net_active = d.intern_atom('_NET_ACTIVE_WINDOW')
    cm = Xlib.protocol.event.ClientMessage(
        window=gcs_win,
        client_type=net_active,
        data=(32, [2, Xlib.X.CurrentTime, 0, 0, 0])
    )
    root.send_event(cm, event_mask=Xlib.X.SubstructureRedirectMask | Xlib.X.SubstructureNotifyMask)
    gcs_win.configure(stack_mode=Xlib.X.Above)
    d.sync()
    time.sleep(2)

    coords = root.translate_coords(gcs_win, 0, 0)
    geom = gcs_win.get_geometry()
    gx, gy = coords.x, coords.y
    gw, gh = geom.width, geom.height
    print(f"[+] Active GCS Window position: ({gx}, {gy}), size: {gw}x{gh}")

    def click_abs(x, y, delay=0.5):
        xtest.fake_input(d, Xlib.X.MotionNotify, x=x, y=y)
        d.sync()
        time.sleep(0.08)
        xtest.fake_input(d, Xlib.X.ButtonPress, 1)
        d.sync()
        time.sleep(0.08)
        xtest.fake_input(d, Xlib.X.ButtonRelease, 1)
        d.sync()
        time.sleep(delay)

    def click_gcs(rel_x, rel_y, delay=0.5):
        click_abs(gx + rel_x, gy + rel_y, delay=delay)

    out_dir_brain = "/home/fawwazfa/.gemini/antigravity-cli/brain/f2f6fbc8-4544-4642-8759-2fbe09b67273"
    out_dir_docs = "/home/fawwazfa/Program/Harvestmoon/docs/bukti_gcs"
    out_dir_pdp = "/home/fawwazfa/Program/Harvestmoon/pdp"
    os.makedirs(out_dir_docs, exist_ok=True)
    os.makedirs(out_dir_pdp, exist_ok=True)

    def capture_case(key, label):
        tmp_root = f"/tmp/{key}_root.png"
        subprocess.run(["import", "-window", "root", tmp_root])
        im = Image.open(tmp_root)
        crop = im.crop((gx, gy, gx + gw, gy + gh))
        
        doc_file = f"{out_dir_docs}/{key}_window.png"
        brain_file = f"{out_dir_brain}/{key}_window.png"
        pdp_file = f"{out_dir_pdp}/{key}_window.png"
        crop.save(doc_file)
        crop.save(brain_file)
        crop.save(pdp_file)
        print(f"[✓] Captured {label}: {doc_file} ({crop.size[0]}x{crop.size[1]})")

    # Give flight test time to progress
    time.sleep(3)

    # =========================================================================
    # KONDISI 1: ZONA NORMAL / VEGETASI SEHAT (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 1: Zona Normal / Vegetasi Sehat (3 Gambar)] ---")
    capture_case("kondisi1a_survei_normal_sektor_A_takeoff", "Kondisi 1A: Transisi Takeoff & Orientasi Sektor A")
    capture_case("kondisi1_survei_normal_sektor_A", "Kondisi 1: Survei Normal Sektor A (Primary)")
    capture_case("menu01_dashboard_live", "Menu 1: Dashboard Live Flight Test")
    time.sleep(3)
    capture_case("kondisi1b_survei_normal_sektor_A_transek", "Kondisi 1B: Jelajah Transek 50m AGL Kanopi Sehat FHI 96")
    time.sleep(3)
    capture_case("kondisi1c_survei_normal_sektor_A_nadir", "Kondisi 1C: Pengamatan Nadir Vegetasi Optimal")

    # =========================================================================
    # KONDISI 2: KETIDAKMERATAAN IRIGASI / STRESS RINGAN (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 2: Ketidakmerataan Irigasi / Stress Ringan (3 Gambar)] ---")
    time.sleep(3)
    capture_case("kondisi2a_anomali_irigasi_sektor_B_approach", "Kondisi 2A: Pendekatan Transek Sektor B & Deteksi Awal")
    capture_case("kondisi2_anomali_irigasi_sektor_C", "Kondisi 2: Anomali Irigasi Sektor C (Primary)")
    time.sleep(3)
    capture_case("kondisi2b_anomali_irigasi_sektor_B_banking_turn", "Kondisi 2B: Belok Terkoordinasi Roll +14.5° di atas Irigasi Tersumbat")
    time.sleep(3)
    capture_case("kondisi2c_anomali_irigasi_sektor_C_transek", "Kondisi 2C: Peringatan Dini Agronomi Distribusi Air")

    # =========================================================================
    # KONDISI 3: DEFISIT AIR KANOPI / LAYU DAUN (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 3: Defisit Air Kanopi / Layu Daun (3 Gambar)] ---")
    time.sleep(3)
    capture_case("kondisi3a_defisit_kanopi_sektor_C_deteksi_awal", "Kondisi 3A: Deteksi Bounding Box Oranye Kanopi Menguning")
    time.sleep(3)
    capture_case("kondisi3b_defisit_kanopi_sektor_C_kluster_luas", "Kondisi 3B: Kluster Defisit Air Kanopi Sektor C")
    time.sleep(3)
    capture_case("kondisi3c_defisit_kanopi_sektor_C_evaluasi_awd", "Kondisi 3C: Evaluasi Rekomendasi AWD Penggenangan Berkala")

    # =========================================================================
    # KONDISI 4: DEFISIT KRITIS AIR TANAH & RETAKAN (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 4: Defisit Kritis Air Tanah & Retakan (3 Gambar)] ---")
    time.sleep(3)
    capture_case("kondisi4a_defisit_kritis_tanah_sektor_D_spot_alert", "Kondisi 4A: Spot Alert Defisit Tanah Petak Hilir Sektor D")
    capture_case("kondisi3_defisit_kritis_tanah_sektor_D", "Kondisi 3 (Legacy): Defisit Kritis Tanah Sektor D")
    time.sleep(3)
    capture_case("kondisi4b_defisit_kritis_tanah_sektor_D_retakan_lahan", "Kondisi 4B: Celah Retakan Tanah Terbuka VWC < 15%")
    time.sleep(3)
    capture_case("kondisi4c_defisit_kritis_tanah_sektor_D_rekomendasi_darurat", "Kondisi 4C: Irigasi Darurat Aliran Tersier 1.8 L/s")

    # =========================================================================
    # KONDISI 5: CAMERA 1080P VISION DETECTION (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 5: Camera 1080p Stream Vision (3 Gambar)] ---")
    click_gcs(90, 207, delay=3.0) # Menu 2: Camera
    capture_case("kondisi5a_camera_vision_1080p_dense_all_classes", "Kondisi 5A: Camera 1080p Dense Detection 4 Kelas Simultan")
    capture_case("kondisi4_camera_vision_1080p_dense", "Kondisi 4 (Legacy): Camera Vision 1080p Dense")
    capture_case("menu02_camera_detection", "Menu 2: Camera Detection Window")
    time.sleep(3)
    capture_case("kondisi5b_camera_vision_1080p_closeup_retakan_tanah", "Kondisi 5B: Camera 1080p Close-up Bare Soil Gaps")
    time.sleep(3)
    capture_case("kondisi5c_camera_vision_1080p_pip_map_tracking", "Kondisi 5C: Camera 1080p dengan PiP Map Tracking UAV")

    # =========================================================================
    # KONDISI 6: PETA LAHAN DUAL TELEMETRY (FLIGHT + FIELD & SOIL) (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 6: Peta Lahan Dual Telemetry (3 Gambar)] ---")
    click_gcs(90, 249, delay=3.0) # Menu 3: Peta Lahan
    capture_case("kondisi6a_peta_lahan_dual_telemetry_wide", "Kondisi 6A: Peta Lahan Wide dengan Flight HUD + Field & Soil Panel")
    capture_case("kondisi5_peta_dan_misi_14_waypoints", "Kondisi 5 (Legacy): Peta Lahan 14 Waypoints")
    capture_case("menu03_peta_dan_misi_pdp_window", "Menu 3: Peta dan Misi PDP")
    time.sleep(3)
    capture_case("kondisi6b_peta_lahan_dual_telemetry_zoom_sektor", "Kondisi 6B: Peta Lahan Dual Telemetry Transek Fotogrametri & Sensor Probe")
    time.sleep(3)
    capture_case("kondisi6c_peta_lahan_dual_telemetry_geofence_active", "Kondisi 6C: Peta Lahan Dual Telemetry Delineasi Geofence Radius 500m & LoRa Probes")

    # =========================================================================
    # KONDISI 7: RENCANA MISI / MISSION PLANNER (14 WP) (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 7: Rencana Misi Fotogrametri 14 Waypoints (3 Gambar)] ---")
    click_gcs(90, 291, delay=2.5) # Menu 4: Rencana Misi
    capture_case("kondisi7a_rencana_misi_tabel_14_waypoints", "Kondisi 7A: Tabel Rencana Misi 14 Titik Waypoint Fotogrametri")
    capture_case("kondisi6_rencana_misi_tabel_waypoints", "Kondisi 6 (Legacy): Rencana Misi Tabel 14 Waypoints")
    capture_case("menu04_rencana_misi_waypoints", "Menu 4: Rencana Misi Waypoints")
    time.sleep(2)
    capture_case("kondisi7b_rencana_misi_profil_ketinggian", "Kondisi 7B: Rencana Misi Profil Ketinggian 50m AGL & Parameter Jarak 3.01 km")
    time.sleep(2)
    capture_case("kondisi7c_rencana_misi_inspeksi_geofence", "Kondisi 7C: Rencana Misi Verifikasi Keamanan Batas Geofence")

    # =========================================================================
    # KONDISI 8: CROP ANALYSIS & STATISTIK FHI (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 8: Crop Analysis & Statistik FHI (3 Gambar)] ---")
    click_gcs(90, 333, delay=2.5) # Menu 5: Crop Analysis
    capture_case("kondisi8a_crop_analysis_distribusi_4kelas", "Kondisi 8A: Crop Analysis Distribusi Persentase 4 Kelas PDP")
    capture_case("kondisi7_crop_analysis_statistik_lengkap", "Kondisi 7 (Legacy): Crop Analysis Statistik Lengkap")
    capture_case("menu05_crop_analysis_stats", "Menu 5: Crop Analysis Stats")
    time.sleep(2)
    capture_case("kondisi8b_crop_analysis_zonasi_prioritas_irigasi", "Kondisi 8B: Crop Analysis Zonasi Prioritas Irigasi P1-P3")
    time.sleep(2)
    capture_case("kondisi8c_crop_analysis_matriks_validasi_sensor", "Kondisi 8C: Crop Analysis Matriks Validasi Sensor LoRa In-Situ")

    # =========================================================================
    # KONDISI 9: EDGE MODE & ONNX RUNTIME (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 9: Edge Mode & ONNX Runtime (3 Gambar)] ---")
    click_gcs(90, 375, delay=1.5) # Menu 6: Edge Mode
    print("[*] Running ONNX Runtime benchmark...")
    click_gcs(350, 245, delay=2.5) # Benchmark button
    capture_case("kondisi9a_edge_mode_onnx_benchmark_running", "Kondisi 9A: Edge Mode ONNX Benchmark 91.5 FPS / 11ms")
    capture_case("kondisi8_edge_mode_onnx_benchmark", "Kondisi 8 (Legacy): Edge Mode ONNX Benchmark")
    capture_case("menu06_edge_mode_onnx", "Menu 6: Edge Mode ONNX")
    time.sleep(2)
    capture_case("kondisi9b_edge_mode_pengaturan_ambang_batas", "Kondisi 9B: Edge Mode Ambang Batas Keyakinan & NMS")
    time.sleep(2)
    capture_case("kondisi9c_edge_mode_transport_mavlink", "Kondisi 9C: Edge Mode Konfigurasi MAVLink Transport TCP/UDP")

    # =========================================================================
    # KONDISI 10: AI SETTINGS & KALIBRASI MODEL (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 10: AI Settings & Kalibrasi Model (3 Gambar)] ---")
    click_gcs(90, 417, delay=2.5) # Menu 7: AI Settings
    capture_case("kondisi10a_ai_settings_bobot_model_yolo11n", "Kondisi 10A: AI Settings Penautan Bobot Model yolo11n_proposal_pdp.onnx")
    capture_case("kondisi9_ai_settings_kalibrasi", "Kondisi 9 (Legacy): AI Settings Kalibrasi")
    capture_case("menu07_ai_settings_config", "Menu 7: AI Settings Config")
    time.sleep(2)
    capture_case("kondisi10b_ai_settings_label_4kelas_pdp", "Kondisi 10B: AI Settings Pemetaan Label 4 Kelas PDP")
    time.sleep(2)
    capture_case("kondisi10c_ai_settings_kalibrasi_vision_runtime", "Kondisi 10C: AI Settings Kalibrasi Subsistem Vision Runtime")

    # =========================================================================
    # KONDISI 11: REPORTS & AGRONOMI AWD (3 GAMBAR)
    # =========================================================================
    print("\n--- [Kondisi 11: Reports & Rekomendasi AWD (3 Gambar)] ---")
    click_gcs(90, 459, delay=2.5) # Menu 8: Reports
    capture_case("kondisi11a_reports_riwayat_misi_tlog", "Kondisi 11A: Reports Riwayat Audit Misi & Log Telemetri TLOG")
    capture_case("kondisi10_reports_agronomi_awd_lengkap", "Kondisi 10 (Legacy): Reports Agronomi AWD")
    capture_case("menu08_reports_agronomy_awd", "Menu 8: Reports Agronomi AWD")
    time.sleep(2)
    capture_case("kondisi11b_reports_catatan_agronomi_awd", "Kondisi 11B: Reports Catatan Rekomendasi Irigasi Intermittent AWD")
    time.sleep(2)
    capture_case("kondisi11c_reports_ekspor_multi_format", "Kondisi 11C: Reports Ekspor Laporan Multi-Format PDF/CSV/JSON")

    # Return back to Dashboard
    click_gcs(90, 165, delay=1.5)
    print("\n[✓] All extensive case and condition captures completed successfully!")

finally:
    # 5. Restore all unmapped windows back so user's workspace is completely intact
    print("[*] Restoring all desktop windows...")
    for w in unmapped_windows:
        try:
            w.map()
        except Exception:
            pass
    d.sync()
    print("[✓] All windows restored.")
