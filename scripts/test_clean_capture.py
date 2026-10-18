import os
import time
import subprocess
import Xlib.display
import Xlib.X
import Xlib.protocol.event
from PIL import Image

os.environ['DISPLAY'] = ':1'
d = Xlib.display.Display(':1')
root = d.screen().root

print("[*] 1. Minimizing other windows...")
prop = root.get_full_property(d.intern_atom('_NET_CLIENT_LIST'), 0)
if prop:
    for win_id in prop.value:
        try:
            w = d.create_resource_object('window', win_id)
            name_prop = w.get_full_property(d.intern_atom('_NET_WM_NAME'), 0)
            title = name_prop.value.decode('utf-8', 'ignore') if name_prop else w.get_wm_name()
            if title and any(x in str(title) for x in ['Code', 'Brave', 'Firefox', 'AnyDesk']):
                print(f"[*] Minimizing: {title}")
                wm_change = d.intern_atom('WM_CHANGE_STATE')
                cm = Xlib.protocol.event.ClientMessage(
                    window=w,
                    client_type=wm_change,
                    data=(32, [3, 0, 0, 0, 0])
                )
                root.send_event(cm, event_mask=Xlib.X.SubstructureRedirectMask | Xlib.X.SubstructureNotifyMask)
        except Exception as e:
            pass
d.sync()
time.sleep(1)

print("[*] 2. Restarting HarvestmoonGCS...")
subprocess.run(['pkill', '-f', 'HarvestmoonGCS'])
time.sleep(1)

gcs_exe = '/home/fawwazfa/Program/Harvestmoon/Pigeon_Harvest/HarvestmoonGCS/bin/Release/net9.0-desktop/HarvestmoonGCS'
proc = subprocess.Popen([gcs_exe], cwd=os.path.dirname(gcs_exe))
print("[*] 3. Waiting 8s for preloading...")
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
    print("[!] GCS window not found!")
    exit(1)

# Bring to top
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
print(f"[+] GCS at ({coords.x}, {coords.y}), size: {geom.width}x{geom.height}")

subprocess.run(['import', '-window', 'root', '/tmp/full_desktop.png'])
im = Image.open('/tmp/full_desktop.png')
crop = im.crop((coords.x, coords.y, coords.x + geom.width, coords.y + geom.height))
crop.save('/tmp/clean_gcs.png')
print("[+] Saved /tmp/clean_gcs.png successfully!")
