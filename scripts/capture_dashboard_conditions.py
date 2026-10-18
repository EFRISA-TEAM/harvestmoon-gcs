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

print("[*] 1. Checking LoRa telemetry bridge...")
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
        pdp_file = f"{out_dir_pdp}/{key}_window.png"
        brain_file = f"{out_dir_brain}/{key}_window.png"
        crop.save(doc_file)
        crop.save(pdp_file)
        crop.save(brain_file)
        print(f"[✓] Captured {label}: {doc_file} ({crop.size[0]}x{crop.size[1]})")

    # =========================================================================
    # KONDISI 1: DASHBOARD - SURVEI NORMAL & TAKEOFF (FASE 1)
    # =========================================================================
    print("\n--- [Kondisi 1: Survei Normal & Takeoff (Fase 1 - Sektor A)] ---")
    time.sleep(2)
    capture_case("kondisi1a_dashboard_survei_normal_takeoff", "Kondisi 1A: Takeoff & Orientasi Sektor A")
    capture_case("kondisi1_survei_normal_sektor_A", "Kondisi 1: Survei Normal Sektor A (Primary)")
    capture_case("menu01_dashboard_live", "Menu 1: Dashboard Live Flight Test")
    time.sleep(4)
    capture_case("kondisi1b_dashboard_survei_normal_transek", "Kondisi 1B: Jelajah Transek 50m AGL Kanopi Sehat FHI 95%")

    # =========================================================================
    # KONDISI 2: DASHBOARD - KETIDAKMERATAAN IRIGASI (FASE 2)
    # =========================================================================
    print("\n--- [Kondisi 2: Ketidakmerataan Irigasi (Fase 2 - Sektor B)] ---")
    time.sleep(6) # advances to step 12-14
    capture_case("kondisi2a_dashboard_anomali_irigasi_approach", "Kondisi 2A: Deteksi Anomali Saluran Irigasi Cacing Sektor B")
    capture_case("kondisi2_anomali_irigasi_sektor_C", "Kondisi 2: Anomali Irigasi (Primary)")
    time.sleep(4) # advances to step 16-18
    capture_case("kondisi2b_dashboard_anomali_irigasi_banking_turn", "Kondisi 2B: Belok Terkoordinasi Roll Bank Angle di atas Irigasi Tersumbat")

    # =========================================================================
    # KONDISI 3: DASHBOARD - DEFISIT AIR KANOPI (FASE 3)
    # =========================================================================
    print("\n--- [Kondisi 3: Defisit Air Kanopi (Fase 3 - Sektor C)] ---")
    time.sleep(6) # advances to step 23-25
    capture_case("kondisi3a_dashboard_defisit_kanopi_deteksi", "Kondisi 3A: Deteksi Bounding Box Oranye Kanopi Menguning FHI 66%")
    time.sleep(4) # advances to step 27-29
    capture_case("kondisi3b_dashboard_defisit_kanopi_kluster", "Kondisi 3B: Kluster Defisit Air Kanopi Sektor C & Rekomendasi AWD")

    # =========================================================================
    # KONDISI 4: DASHBOARD - DEFISIT KRITIS AIR TANAH & RETAKAN (FASE 4)
    # =========================================================================
    print("\n--- [Kondisi 4: Defisit Kritis Air Tanah & Retakan (Fase 4 - Sektor D)] ---")
    time.sleep(6) # advances to step 34-36
    capture_case("kondisi4a_dashboard_defisit_tanah_retakan", "Kondisi 4A: Spot Alert Retakan Tanah Parah Merah VWC < 15%")
    capture_case("kondisi3_defisit_kritis_tanah_sektor_D", "Kondisi 3: Defisit Kritis Tanah Sektor D (Primary)")
    time.sleep(4) # advances to step 38-40
    capture_case("kondisi4b_dashboard_defisit_tanah_rekomendasi_pompa", "Kondisi 4B: Peringatan Kritis AWD & Instruksi Pompa 1.8 L/s")

    # =========================================================================
    # KONDISI 5: DASHBOARD - MISI TRANSEK & RTL APPROACH (FASE 5)
    # =========================================================================
    print("\n--- [Kondisi 5: Misi Transek & RTL Approach (Fase 5 - Waypoint 13-14)] ---")
    time.sleep(6) # advances to step 45-47
    capture_case("kondisi5a_dashboard_misi_transek_tengah", "Kondisi 5A: Sebaran 4 Kelas Seimbang 14 WP Transek Leg Selesai")
    time.sleep(4) # advances to step 49-51
    capture_case("kondisi5b_dashboard_misi_rtl_approach", "Kondisi 5B: Prosedur RTL Landing Approach & Descent Telemetri")

finally:
    # 5. Restore all windows
    print("\n[*] 5. Restoring all unmapped windows...")
    for w in unmapped_windows:
        try:
            w.map()
        except Exception:
            pass
    d.sync()
    time.sleep(1)

print("\n[✓] All Dashboard conditions captured successfully!")
