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

print("[*] Checking / Starting LoRa bridge...")
subprocess.Popen(["python3", "/home/fawwazfa/Program/Harvestmoon/scripts/lora_soil_bridge.py", "--simulate"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

# Kill any existing GCS first to have fresh clean state
subprocess.run(["pkill", "-f", "HarvestmoonGCS"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
time.sleep(1)

print("[*] Launching HarvestmoonGCS...")
gcs_exe = "/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS"
gcs_proc = subprocess.Popen([gcs_exe], cwd=os.path.dirname(gcs_exe))

print("[*] Waiting for GCS window to map (5s)...")
time.sleep(5)

# Reconnect display to catch new window
d = Xlib.display.Display(':1')
root = d.screen().root

def find_gcs_window(win):
    children = win.query_tree().children
    for c in children:
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
    print("[!] Could not find GCS window, listing all top windows:")
    for c in root.query_tree().children:
        try:
            n = c.get_wm_name()
            if n:
                print("  Window:", hex(c.id), n)
        except Exception:
            pass
    exit(1)

print(f"[+] Found GCS Window: {hex(gcs_win.id)}")

# Bring GCS to front via _NET_ACTIVE_WINDOW
net_active = d.intern_atom('_NET_ACTIVE_WINDOW')
cm = Xlib.protocol.event.ClientMessage(
    window=gcs_win,
    client_type=net_active,
    data=(32, [1, Xlib.X.CurrentTime, 0, 0, 0])
)
root.send_event(cm, event_mask=Xlib.X.SubstructureRedirectMask | Xlib.X.SubstructureNotifyMask)
d.sync()
time.sleep(1)

# Get window geometry & screen coordinates
geom = gcs_win.get_geometry()
coords = gcs_win.translate_coords(root, 0, 0)
# Note: translate_coords returns coordinates relative to root
win_x = -coords.x
win_y = -coords.y
win_w = geom.width
win_h = geom.height
print(f"[+] Window Geometry: {win_w}x{win_h} at screen pos ({win_x}, {win_y})")

def click_relative(rel_x, rel_y):
    abs_x = win_x + rel_x
    abs_y = win_y + rel_y
    print(f"[*] Clicking relative ({rel_x}, {rel_y}) -> screen ({abs_x}, {abs_y})")
    xtest.fake_input(d, Xlib.X.MotionNotify, x=abs_x, y=abs_y)
    d.sync()
    time.sleep(0.1)
    xtest.fake_input(d, Xlib.X.ButtonPress, 1)
    d.sync()
    time.sleep(0.1)
    xtest.fake_input(d, Xlib.X.ButtonRelease, 1)
    d.sync()
    time.sleep(0.3)

# 1. Click Start Demo button in Dashboard
# In 1280x800 window, Start Demo button is located at around rel_x=900, rel_y=105
click_relative(900, 105)

print("[*] Waiting for video & AI detection pipeline to run (4s)...")
time.sleep(4)

# Capture Dashboard Screenshot
out_dash = "/home/fawwazfa/.gemini/antigravity-cli/brain/f2f6fbc8-4544-4642-8759-2fbe09b67273/bukti_gcs_live_dashboard.png"
subprocess.run(["import", "-window", "root", out_dash])
print(f"[+] Captured Dashboard screenshot: {out_dash}")

# 2. Click Sidebar: 'Peta Lahan'
# Sidebar button 3 ('Peta Lahan') is around rel_x=90, rel_y=230
click_relative(90, 230)
time.sleep(2)

# Click 'Field Scenario' / '🌾 Demo PDP' in top toolbar of MapPage
# MapPage toolbar starts around rel_x=650, rel_y=45
click_relative(650, 45)
time.sleep(1)

# Click 'Field Telemetry' / '📊 Monitor PDP' in top toolbar
# Around rel_x=750, rel_y=45
click_relative(750, 45)
time.sleep(2)

# Capture Map Screenshot
out_map = "/home/fawwazfa/.gemini/antigravity-cli/brain/f2f6fbc8-4544-4642-8759-2fbe09b67273/bukti_gcs_peta_pdp.png"
subprocess.run(["import", "-window", "root", out_map])
print(f"[+] Captured Map screenshot: {out_map}")

print("[✓] Automation finished successfully!")
