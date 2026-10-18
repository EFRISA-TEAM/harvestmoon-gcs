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

print("[*] 2. Minimizing background desktop windows (VSCode, browsers)...")
prop = root.get_full_property(d.intern_atom('_NET_CLIENT_LIST'), 0)
if prop:
    for win_id in prop.value:
        try:
            w = d.create_resource_object('window', win_id)
            name_prop = w.get_full_property(d.intern_atom('_NET_WM_NAME'), 0)
            title = name_prop.value.decode('utf-8', 'ignore') if name_prop else w.get_wm_name()
            if title and any(x in str(title) for x in ['Code', 'Brave', 'Firefox', 'AnyDesk']):
                print(f"    Minimizing: {title[:40]}...")
                wm_change = d.intern_atom('WM_CHANGE_STATE')
                cm = Xlib.protocol.event.ClientMessage(
                    window=w,
                    client_type=wm_change,
                    data=(32, [3, 0, 0, 0, 0])
                )
                root.send_event(cm, event_mask=Xlib.X.SubstructureRedirectMask | Xlib.X.SubstructureNotifyMask)
        except Exception:
            pass
d.sync()
time.sleep(1)

print("[*] 3. Starting HarvestmoonGCS detached...")
subprocess.run(['pkill', '-f', 'HarvestmoonGCS'])
time.sleep(1)

gcs_exe = '/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS'
proc = subprocess.Popen([gcs_exe], cwd=os.path.dirname(gcs_exe), start_new_session=True)

print("[*] 4. Waiting for GCS to fully initialize & preload all views (8s)...")
time.sleep(8)

prop = root.get_full_property(d.intern_atom('_NET_CLIENT_LIST'), 0)
gcs_win = None
if prop:
    for win_id in prop.value:
        try:
            w = d.create_resource_object('window', win_id)
            name_prop = w.get_full_property(d.intern_atom('_NET_WM_NAME'), 0)
            title = name_prop.value.decode('utf-8', 'ignore') if name_prop else w.get_wm_name()
            if title and 'Harvestmoon GCS' in str(title):
                gcs_win = w
                break
        except Exception:
            pass

if not gcs_win:
    print("[!] GCS window not found in client list!")
    exit(1)

# Bring GCS to absolute top
net_active = d.intern_atom('_NET_ACTIVE_WINDOW')
cm = Xlib.protocol.event.ClientMessage(
    window=gcs_win,
    client_type=net_active,
    data=(32, [2, Xlib.X.CurrentTime, 0, 0, 0])
)
root.send_event(cm, event_mask=Xlib.X.SubstructureRedirectMask | Xlib.X.SubstructureNotifyMask)
d.sync()
time.sleep(2)

coords = root.translate_coords(gcs_win, 0, 0)
geom = gcs_win.get_geometry()
gx, gy = coords.x, coords.y
gw, gh = geom.width, geom.height
print(f"[+] Active GCS Window: {hex(gcs_win.id)} at ({gx}, {gy}), size: {gw}x{gh}")

def click_abs(x, y, delay=0.5):
    print(f"[*] Click screen ({x}, {y})")
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

# ─────────────────────────────────────────────────────────────
# 1. MENU 1: DASHBOARD · LIVE
# ─────────────────────────────────────────────────────────────
print("\n=== [1/8] MENU 1: Dashboard · Live Flight Test ===")
# Start Demo Button at (1370, 100)
click_gcs(1370, 100, delay=1.0)
print("[*] Streaming PDP 1080p video & running YOLO inference loop (8s)...")
time.sleep(8)
capture_menu("menu01_dashboard_live", "Menu 1: Dashboard Live Flight Test")

# ─────────────────────────────────────────────────────────────
# 2. MENU 2: CAMERA
# ─────────────────────────────────────────────────────────────
print("\n=== [2/8] MENU 2: Camera Live Stream & HUD ===")
# Sidebar button 2 at (90, 207)
click_gcs(90, 207, delay=2.5)
capture_menu("menu02_camera_detection", "Menu 2: Camera YOLO Detection Stream")

# ─────────────────────────────────────────────────────────────
# 3. MENU 3: PETA LAHAN / PETA & MISI PDP
# ─────────────────────────────────────────────────────────────
print("\n=== [3/8] MENU 3: Peta Lahan 4-Sektor PDP ===")
# Sidebar button 3 at (90, 249)
click_gcs(90, 249, delay=2.0)
# Click Field Scenario (around rel_x 1040, rel_y 24)
click_gcs(1040, 24, delay=1.2)
# Click Survey Grid (around rel_x 1160, rel_y 24)
click_gcs(1160, 24, delay=1.2)
# Click Field Telemetry (around rel_x 1280, rel_y 24)
click_gcs(1280, 24, delay=1.8)
capture_menu("menu03_peta_dan_misi_pdp", "Menu 3: Peta Lahan & Telemetri PDP")

# ─────────────────────────────────────────────────────────────
# 4. MENU 4: RENCANA MISI / MISSION PLANNER
# ─────────────────────────────────────────────────────────────
print("\n=== [4/8] MENU 4: Rencana Misi / Mission Planner ===")
# Sidebar button 4 at (90, 291)
click_gcs(90, 291, delay=2.5)
capture_menu("menu04_rencana_misi_waypoints", "Menu 4: Rencana Misi Waypoints")

# ─────────────────────────────────────────────────────────────
# 5. MENU 5: CROP ANALYSIS / STATS
# ─────────────────────────────────────────────────────────────
print("\n=== [5/8] MENU 5: Crop Analysis & FHI Statistics ===")
# Sidebar button 5 at (90, 333)
click_gcs(90, 333, delay=2.5)
capture_menu("menu05_crop_analysis_stats", "Menu 5: Crop Analysis & FHI Statistics")

# ─────────────────────────────────────────────────────────────
# 6. MENU 6: EDGE MODE / ONNX RUNTIME
# ─────────────────────────────────────────────────────────────
print("\n=== [6/8] MENU 6: Edge Mode & Model Benchmark ===")
# Sidebar button 6 at (90, 375)
click_gcs(90, 375, delay=1.5)
# Click Run Benchmark Button at (350, 245)
print("[*] Running ONNX Runtime benchmark...")
click_gcs(350, 245, delay=2.5)
capture_menu("menu06_edge_mode_onnx", "Menu 6: Edge Mode ONNX Benchmark")

# ─────────────────────────────────────────────────────────────
# 7. MENU 7: AI SETTINGS
# ─────────────────────────────────────────────────────────────
print("\n=== [7/8] MENU 7: AI Settings & Class Confidence ===")
# Sidebar button 7 at (90, 417)
click_gcs(90, 417, delay=2.5)
capture_menu("menu07_ai_settings_config", "Menu 7: AI Settings Calibration")

# ─────────────────────────────────────────────────────────────
# 8. MENU 8: REPORTS & AGRONOMY AWD
# ─────────────────────────────────────────────────────────────
print("\n=== [8/8] MENU 8: Reports & Agronomy AWD Recommendations ===")
# Sidebar button 8 at (90, 459)
click_gcs(90, 459, delay=2.5)
capture_menu("menu08_reports_agronomy_awd", "Menu 8: Reports Agronomi & AWD")

# Return back to Dashboard for live flight display
print("\n[*] Navigating back to Dashboard Live...")
click_gcs(90, 165, delay=1.0)

print("\n[🎉] ALL 8 GCS MENUS CAPTURED IN CRYSTAL-CLEAR QUALITY!")
