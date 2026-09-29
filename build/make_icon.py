"""Generates assets/tickshift.ico (16-256 px, PNG-compressed entries) without third-party libraries.
Design: graphite/navy rounded tile with an electric-teal rising 'tick' line — a price move that shifts up."""
import math, struct, zlib, os

def seg_dist(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
    cx, cy = ax + t * dx, ay + t * dy
    return math.hypot(px - cx, py - cy)

def rounded_rect_inside(x, y, r):
    # unit square [0.04, 0.96] with corner radius r
    lo, hi = 0.04, 0.96
    cx = min(max(x, lo + r), hi - r)
    cy = min(max(y, lo + r), hi - r)
    return math.hypot(x - cx, y - cy) <= r and lo <= x <= hi and lo <= y <= hi

POINTS = [(0.20, 0.68), (0.40, 0.50), (0.54, 0.60), (0.80, 0.32)]
SEGS = list(zip(POINTS, POINTS[1:]))

def render(size):
    ss = 4 if size <= 64 else 3
    stroke = 0.075 if size > 24 else 0.10
    rows = []
    for j in range(size):
        row = bytearray()
        for i in range(size):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sj in range(ss):
                for si in range(ss):
                    x = (i + (si + 0.5) / ss) / size
                    y = (j + (sj + 0.5) / ss) / size
                    if not rounded_rect_inside(x, y, 0.22):
                        continue
                    # background gradient graphite -> navy
                    t = (x + y) / 2
                    r = 16 + (12 - 16) * t; g = 20 + (32 - 20) * t; b = 28 + (52 - 28) * t
                    # subtle grid line for the 'terminal' feel on large sizes
                    if size >= 64 and abs(y - 0.80) < 0.006:
                        r, g, b = r + 14, g + 22, b + 30
                    d = min(seg_dist(x, y, *a, *bb) for a, bb in SEGS)
                    end = math.hypot(x - 0.80, y - 0.32)
                    if d <= stroke / 2 or end <= stroke * 0.95:
                        # teal with a cyan highlight towards the end of the move
                        k = min(1.0, max(0.0, (x - 0.2) / 0.6))
                        r, g, b = 20 + 70 * k, 200 + 30 * k, 190 + 45 * k
                    elif d <= stroke * 1.6:
                        glow = 1 - (d - stroke / 2) / (stroke * 1.1)
                        r, g, b = r + 10 * glow, g + 60 * glow, b + 55 * glow
                    acc[0] += r; acc[1] += g; acc[2] += b; acc[3] += 255
            n = ss * ss
            a = acc[3] / n
            if a > 0:
                cov = acc[3] / 255
                row += bytes([int(acc[0] / cov), int(acc[1] / cov), int(acc[2] / cov), int(a)])
            else:
                row += bytes([0, 0, 0, 0])
        rows.append(bytes(row))
    raw = b"".join(b"\x00" + r for r in rows)
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = [render(s) for s in sizes]
header = struct.pack("<HHH", 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
entries = b""
for s, img in zip(sizes, images):
    entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(img), offset)
    offset += len(img)
os.makedirs("assets", exist_ok=True)
with open("assets/tickshift.ico", "wb") as f:
    f.write(header + entries + b"".join(images))
with open("assets/tickshift-256.png", "wb") as f:
    f.write(images[-1])
print("ok", os.path.getsize("assets/tickshift.ico"))
