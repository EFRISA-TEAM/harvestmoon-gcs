import time
import os
import subprocess
from PIL import Image
import Xlib.display
import Xlib.X
import Xlib.protocol.event
from Xlib.ext import xtest

os.environ['DISPLAY'] = ':1'
d = Xlib.display.Display(':1')
root = d.screen().root

print("[*] 1. Starting LoRa background bridge...")
subprocess.Popen(["python3", "/home/fawwazfa/Program/Harvestmoon/scripts/lora_soil_bridge.py", "--simulate"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

print("[*] 2. Restarting HarvestmoonGCS...")
subprocess.run(["pkill", "-f", "HarvestmoonGCS"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
time.sleep(1)

gcs_exe = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS"
gcs_proc = subprocess.Popen([gcs_exe], cwd=os.path.dirname(gcs_exe))

print("[*] 3. Waiting for GCS to fully initialize & preload (8s)...")
time.sleep(8)

d = Xlib.display.Display(':1')
root = d.screen().root

def find_gcs_window(win):
    for c in win.query_tree().children:
        try:
            name = c.get_wm_name()
            if name and "Harvestmoon GCS" in name:
                return c
        except Exception:
            pass
        res = find_gcs_window(c)
        if res:
            return res
    return None

gcs_win = find_gcs_window(root)
if not gcs_win:
    print("[!] GCS window not found!")
    exit(1)

print(f"[+] Found GCS window: {hex(gcs_win.id)}")

# Bring GCS to front
net_active = d.intern_atom('_NET_ACTIVE_WINDOW')
cm = Xlib.protocol.event.ClientMessage(
    window=gcs_win,
    client_type=net_active,
    data=(32, [1, Xlib.X.CurrentTime, 0, 0, 0])
)
root.send_event(cm, event_mask=Xlib.X.SubstructureRedirectMask | Xlib.X.SubstructureNotifyMask)
d.sync()
time.sleep(1.5)

win_x, win_y = 36, 72
win_w, win_h = 1308, 859

def click_abs(x, y, delay=0.5):
    print(f"[*] Click ({x}, {y})")
    xtest.fake_input(d, Xlib.X.MotionNotify, x=x, y=y)
    d.sync()
    time.sleep(0.08)
    xtest.fake_input(d, Xlib.X.ButtonPress, 1)
    d.sync()
    time.sleep(0.08)
    xtest.fake_input(d, Xlib.X.ButtonRelease, 1)
    d.sync()
    time.sleep(delay)

out_dir_brain = "/home/fawwazfa/.gemini/antigravity-cli/brain/f2f6fbc8-4544-4642-8759-2fbe09b67273"
out_dir_docs = "/home/fawwazfa/Program/Harvestmoon/docs/bukti_gcs"
os.makedirs(out_dir_docs, exist_ok=True)

def capture_page(menu_key, title):
    full_path = f"{out_dir_brain}/{menu_key}.png"
    win_path = f"{out_dir_brain}/{menu_key}_window.png"
    doc_path = f"{out_dir_docs}/{menu_key}_window.png"
    
    # Capture root X11
    subprocess.run(["import", "-window", "root", full_path])
    # Crop to GCS window
    im = Image.open(full_path)
    crop = im.crop((win_x, win_y, win_x + win_w, win_y + win_h))
    crop.save(win_path)
    crop.save(doc_path)
    print(f"[✓] Captured {title}: {doc_path}")

# ── MENU 1: Dashboard · Live (Trigger Flight Demo) ──
print("\n=== [1/8] Dashboard · Live Flight Test ===")
# Click Start Demo (pos: 954, 215)
click_abs(954, 215, delay=0.5)
print("[*] Waiting for video streaming & telemetry loop (6s)...")
time.sleep(6)
capture_page("menu01_dashboard_live", "Menu 1: Dashboard Live")

# ── MENU 2: Camera (pos: 90, 292) ──
print("\n=== [2/8] Camera Page ===")
click_abs(90, 292, delay=2.5)
capture_page("menu02_camera_detection", "Menu 2: Camera Full Stream")

# ── MENU 3: Peta Lahan / Peta & Misi (pos: 90, 334) ──
print("\n=== [3/8] Peta & Misi PDP ===")
click_abs(90, 334, delay=2.0)
# Click 'Field Scenario' / 'Demo PDP'
click_abs(730, 125, delay=1.0)
# Click 'Survey Grid'
click_abs(830, 125, delay=1.0)
# Click 'Field Telemetry' / 'Monitor PDP'
click_abs(940, 125, delay=1.5)
capture_page("menu03_peta_dan_misi_pdp", "Menu 3: Peta & Misi PDP 4-Sektor")

# ── MENU 4: Rencana Misi / Mission Planner (pos: 90, 376) ──
print("\n=== [4/8] Rencana Misi / Mission Planner ===")
click_abs(90, 376, delay=2.5)
capture_page("menu04_rencana_misi_waypoints", "Menu 4: Rencana Misi Waypoints")

# ── MENU 5: Crop Analysis / Stats (pos: 90, 418) ──
print("\n=== [5/8] Crop Analysis / Stats ===")
click_abs(90, 418, delay=2.5)
capture_page("menu05_crop_analysis_stats", "Menu 5: Crop Analysis & FHI Stats")

# ── MENU 6: Edge Mode (pos: 90, 460) ──
print("\n=== [6/8] Edge Mode ===")
click_abs(90, 460, delay=2.5)
capture_page("menu06_edge_mode_onnx", "Menu 6: Edge Mode ONNX Runtime")

# ── MENU 7: AI Settings (pos: 90, 502) ──
print("\n=== [7/8] AI Settings ===")
click_abs(90, 502, delay=2.5)
capture_page("menu07_ai_settings_config", "Menu 7: AI Settings & Calibration")

# ── MENU 8: Reports / Tlog (pos: 90, 544) ──
print("\n=== [8/8] Reports (Agronomy & AWD Recommendations) ===")
click_abs(90, 544, delay=2.5)
capture_page("menu08_reports_agronomy_awd", "Menu 8: Reports Agronomi & AWD")

# Return to Dashboard so user sees the live flight
print("\n[*] Returning to Dashboard...")
click_abs(90, 250, delay=1.0)

print("\n[🎉] ALL 8 MENUS CAPTURED & SAVED SUCCESSFULLY!")
