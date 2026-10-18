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

print("[*] 3. Waiting for GCS to fully initialize & auto-start demo (12s)...")
time.sleep(12)

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
                # Do not unmap desktop desktop icons/root panels if any
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
    os.makedirs(out_dir_docs, exist_ok=True)

    def capture_menu(menu_key, label):
        tmp_root = f"/tmp/{menu_key}_root.png"
        subprocess.run(["import", "-window", "root", tmp_root])
        im = Image.open(tmp_root)
        crop = im.crop((gx, gy, gx + gw, gy + gh))
        
        doc_file = f"{out_dir_docs}/{menu_key}_window.png"
        brain_file = f"{out_dir_brain}/{menu_key}_window.png"
        crop.save(doc_file)
        crop.save(brain_file)
        print(f"[✓] Saved {label}: {doc_file} ({crop.size[0]}x{crop.size[1]})")

    # Give flight test 4s to progress
    time.sleep(4)

    # ─────────────────────────────────────────────────────────────
    # 1. CAPTURE CONDITIONS ON DASHBOARD LIVE
    # ─────────────────────────────────────────────────────────────
    print("\n=== Capturing Condition 1: Sektor A - Survei Normal ===")
    capture_menu("kondisi1_survei_normal_sektor_A", "Kondisi 1: Survei Normal Sektor A")
    capture_menu("menu01_dashboard_live", "Menu 1: Dashboard Live Flight Test")

    print("\n=== Capturing Condition 2: Sektor B/C - Anomali Irigasi & Banking ===")
    time.sleep(5)
    capture_menu("kondisi2_anomali_irigasi_sektor_C", "Kondisi 2: Anomali Irigasi Sektor C")

    print("\n=== Capturing Condition 3: Sektor D - Defisit Kritis & Rekahan Tanah ===")
    time.sleep(5)
    capture_menu("kondisi3_defisit_kritis_tanah_sektor_D", "Kondisi 3: Defisit Kritis Tanah Sektor D")

    # ─────────────────────────────────────────────────────────────
    # 2. MENU 2: CAMERA FULL VISION
    # ─────────────────────────────────────────────────────────────
    print("\n=== [2/8] MENU 2: Camera YOLO Detection Stream ===")
    click_gcs(90, 207, delay=3.0)
    capture_menu("kondisi4_camera_vision_1080p_dense", "Kondisi 4: Camera 1080p Vision Dense Detection")
    capture_menu("menu02_camera_detection", "Menu 2: Camera Detection Window")

    # ─────────────────────────────────────────────────────────────
    # 3. MENU 3: PETA LAHAN / PETA & MISI 14 WAYPOINTS
    # ─────────────────────────────────────────────────────────────
    print("\n=== [3/8] MENU 3: Peta Lahan 14 Waypoints Fotogrametri PDP ===")
    click_gcs(90, 249, delay=2.5)
    click_gcs(1040, 24, delay=1.0) # Scenario
    click_gcs(1160, 24, delay=1.0) # Grid
    click_gcs(1280, 24, delay=1.5) # Telemetry
    capture_menu("kondisi5_peta_dan_misi_14_waypoints", "Kondisi 5: Peta Lahan 14 Waypoints Grid")
    capture_menu("menu03_peta_dan_misi_pdp", "Menu 3: Peta Lahan PDP")

    # ─────────────────────────────────────────────────────────────
    # 4. MENU 4: RENCANA MISI / MISSION PLANNER (14 WP)
    # ─────────────────────────────────────────────────────────────
    print("\n=== [4/8] MENU 4: Rencana Misi / Mission Planner (14 WP) ===")
    click_gcs(90, 291, delay=2.5)
    capture_menu("kondisi6_rencana_misi_tabel_waypoints", "Kondisi 6: Rencana Misi Tabel 14 Waypoints")
    capture_menu("menu04_rencana_misi_waypoints", "Menu 4: Rencana Misi Waypoints")

    # ─────────────────────────────────────────────────────────────
    # 5. MENU 5: CROP ANALYSIS / STATS
    # ─────────────────────────────────────────────────────────────
    print("\n=== [5/8] MENU 5: Crop Analysis & Statistik FHI ===")
    click_gcs(90, 333, delay=2.5)
    capture_menu("kondisi7_crop_analysis_statistik_lengkap", "Kondisi 7: Crop Analysis & Statistik FHI")
    capture_menu("menu05_crop_analysis_stats", "Menu 5: Crop Analysis Stats")

    # ─────────────────────────────────────────────────────────────
    # 6. MENU 6: EDGE MODE / ONNX RUNTIME BENCHMARK
    # ─────────────────────────────────────────────────────────────
    print("\n=== [6/8] MENU 6: Edge Mode & ONNX Benchmark ===")
    click_gcs(90, 375, delay=1.5)
    print("[*] Running ONNX Runtime benchmark...")
    click_gcs(350, 245, delay=2.5)
    capture_menu("kondisi8_edge_mode_onnx_benchmark", "Kondisi 8: Edge Mode ONNX Benchmark")
    capture_menu("menu06_edge_mode_onnx", "Menu 6: Edge Mode ONNX")

    # ─────────────────────────────────────────────────────────────
    # 7. MENU 7: AI SETTINGS
    # ─────────────────────────────────────────────────────────────
    print("\n=== [7/8] MENU 7: AI Settings & Kalibrasi ===")
    click_gcs(90, 417, delay=2.5)
    capture_menu("kondisi9_ai_settings_kalibrasi", "Kondisi 9: AI Settings Kalibrasi")
    capture_menu("menu07_ai_settings_config", "Menu 7: AI Settings Config")

    # ─────────────────────────────────────────────────────────────
    # 8. MENU 8: REPORTS & AGRONOMY AWD
    # ─────────────────────────────────────────────────────────────
    print("\n=== [8/10] MENU 8: Reports & Rekomendasi AWD ===")
    click_gcs(90, 459, delay=2.5)
    capture_menu("kondisi10_reports_agronomi_awd_lengkap", "Kondisi 10: Reports Agronomi & AWD")
    capture_menu("menu08_reports_agronomy_awd", "Menu 8: Reports Agronomi AWD")

    # Return back to Dashboard
    click_gcs(90, 165, delay=1.5)
    print("\n[✓] All menu and condition captures successfully completed!")

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
