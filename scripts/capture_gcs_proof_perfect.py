import time
import os
import subprocess
import Xlib.display
import Xlib.X
import Xlib.protocol.event
from Xlib.ext import xtest

os.environ['DISPLAY'] = ':1'
d = Xlib.display.Display(':1')
root = d.screen().root

print("[*] Starting LoRa bridge in background...")
subprocess.Popen(["python3", "/home/fawwazfa/Program/Harvestmoon/scripts/lora_soil_bridge.py", "--simulate"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

print("[*] Killing any old GCS instance...")
subprocess.run(["pkill", "-f", "HarvestmoonGCS"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
time.sleep(1)

print("[*] Launching HarvestmoonGCS...")
gcs_exe = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS"
gcs_proc = subprocess.Popen([gcs_exe], cwd=os.path.dirname(gcs_exe))

print("[*] Waiting for GCS to fully initialize & preload (6s)...")
time.sleep(6)

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
time.sleep(1)

def click_abs(x, y, delay=0.3):
    print(f"[*] Clicking screen coordinate ({x}, {y})")
    xtest.fake_input(d, Xlib.X.MotionNotify, x=x, y=y)
    d.sync()
    time.sleep(0.1)
    xtest.fake_input(d, Xlib.X.ButtonPress, 1)
    d.sync()
    time.sleep(0.1)
    xtest.fake_input(d, Xlib.X.ButtonRelease, 1)
    d.sync()
    time.sleep(delay)

# 1. Click Start Demo button in Dashboard
print("[*] Triggering 'Start Demo'...")
click_abs(954, 215, delay=0.5)

print("[*] Waiting for video streaming & AI inference to run (5s)...")
time.sleep(5)

# Capture Dashboard screenshot with live AI detection
out_dash = "/home/fawwazfa/.gemini/antigravity-cli/brain/f2f6fbc8-4544-4642-8759-2fbe09b67273/bukti_gcs_live_dashboard.png"
subprocess.run(["import", "-window", "root", out_dash])
print(f"[+] Saved live dashboard screenshot: {out_dash}")

# 2. Click Sidebar: 'Peta Lahan'
print("[*] Navigating to 'Peta Lahan'...")
click_abs(90, 380, delay=1.5)

# Click 'Field Scenario' / 'Demo PDP'
# On 1308 width window, toolbar is at Y ~ 125
print("[*] Triggering 'Demo PDP'...")
click_abs(730, 125, delay=1.0)

# Click 'Field Telemetry' / 'Monitor PDP'
print("[*] Triggering 'Monitor PDP'...")
click_abs(850, 125, delay=1.0)
time.sleep(2)

# Capture Map screenshot with PDP polygons & LoRa telemetry
out_map = "/home/fawwazfa/.gemini/antigravity-cli/brain/f2f6fbc8-4544-4642-8759-2fbe09b67273/bukti_gcs_peta_pdp.png"
subprocess.run(["import", "-window", "root", out_map])
print(f"[+] Saved map screenshot: {out_map}")

# 3. Return to Dashboard so user sees the active live video
click_abs(90, 260, delay=1.0)

print("[✓] Proof screenshots captured! Keeping GCS running in foreground.")
