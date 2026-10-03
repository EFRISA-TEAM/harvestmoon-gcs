import subprocess, sys, json, base64, time
import numpy as np
import cv2

SRC = sys.argv[1] if len(sys.argv) > 1 else "/data/derr.mp4"
RAW = sys.argv[2] if len(sys.argv) > 2 else "/data/stream_raw.mp4"
FPS = 15

cmd = ["python3", "/data/mh_stream/moonharvest_detect_stream.py",
       "--source", SRC, "--max-fps", str(FPS)]
p = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                     text=True, bufsize=1)

writer = None
frames = 0
detections = 0
bad = 0
errors = []
pending_img = None

# Demo palette TEKNOFEST (4 kelas), urutan tampil
ORDER = ["Healthy", "Stress", "Disease", "Pest"]
PALETTE = {
    "Healthy": (0, 255,   0),
    "Stress":  (0, 255, 255),
    "Disease": (0,   0, 255),
    "Pest":    (0, 140, 255),
}


def burn(img, classes, count):
    h, w = img.shape[:2]
    # header bar
    ov = img.copy()
    cv2.rectangle(ov, (0, 0), (w, 30), (20, 20, 20), -1)
    cv2.addWeighted(ov, 0.6, img, 0.4, 0, img)
    cv2.putText(img, f"MoonHarvest AI   Boxes: {count}", (8, 21),
                cv2.FONT_HERSHEY_SIMPLEX, 0.6, (100, 255, 180), 1, cv2.LINE_AA)

    # panel legenda 4-kelas (kiri-bawah) dengan bar persen
    rowh = 26
    pw   = 200
    ph   = rowh * len(ORDER) + 12
    px   = 8
    py   = h - ph - 8
    ov2 = img.copy()
    cv2.rectangle(ov2, (px, py), (px + pw, py + ph), (15, 15, 15), -1)
    cv2.addWeighted(ov2, 0.65, img, 0.35, 0, img)
    for i, k in enumerate(ORDER):
        v = float(classes.get(k, 0.0))
        y = py + 8 + i * rowh
        col = PALETTE[k]
        cv2.putText(img, f"{k}: {v:.0f}%", (px + 10, y + 12),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, (235, 235, 235), 1, cv2.LINE_AA)
        cv2.rectangle(img, (px + 10, y + 16), (px + 10 + 170, y + 21), (60, 60, 60), -1)
        cv2.rectangle(img, (px + 10, y + 16), (px + 10 + int(170 * v / 100.0), y + 21), col, -1)
    return img


t0 = time.time()
for line in p.stdout:
    line = line.strip()
    if not line:
        continue
    try:
        msg = json.loads(line)
    except Exception:
        bad += 1
        continue
    t = msg.get("type")
    if t == "frame":
        raw = base64.b64decode(msg["data"])
        arr = np.frombuffer(raw, np.uint8)
        pending_img = cv2.imdecode(arr, cv2.IMREAD_COLOR)
        frames += 1
    elif t == "detection":
        d = msg["data"]
        cnt = d.get("count", 0)
        cls = d.get("classes", {})
        detections += 1
        if pending_img is not None:
            img = burn(pending_img, cls, cnt)
            if writer is None:
                hh, ww = img.shape[:2]
                writer = cv2.VideoWriter(RAW, cv2.VideoWriter_fourcc(*"mp4v"),
                                         FPS, (ww, hh))
            writer.write(img)
            pending_img = None
    elif t == "error":
        errors.append(msg.get("data"))
    elif t == "end":
        break

if writer is not None:
    writer.release()
rc = p.wait()
stderr_tail = p.stderr.read()[-1200:]
print("frames=", frames, "detections=", detections, "bad_json=", bad,
      "errors=", errors[:5], "rc=", rc, "elapsed=%.1fs" % (time.time() - t0))
print("STDERR_TAIL:", stderr_tail.strip())
