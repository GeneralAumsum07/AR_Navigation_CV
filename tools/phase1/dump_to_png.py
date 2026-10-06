"""Convert Task 5 dumps (input.rgb, output.f32) to PNG for a visual check. Stdlib only."""
import json
import struct
import sys
import zlib
from pathlib import Path

SIZE = 518


def write_png(path, width, height, rows, channels):
    """Minimal PNG writer: 8-bit greyscale (channels=1) or RGB (channels=3)."""
    raw = b"".join(b"\x00" + bytes(r) for r in rows)
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    colour = 0 if channels == 1 else 2
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, colour, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b"")
    Path(path).write_bytes(png)


def convert(frame_dir):
    d = Path(frame_dir)
    rgb = (d / "input.rgb").read_bytes()
    write_png(d / "input.png", SIZE, SIZE, [rgb[y * SIZE * 3:(y + 1) * SIZE * 3] for y in range(SIZE)], 3)
    for name in ("output.f32", "out_w8a16.f32", "out_float.f32"):
        f = d / name
        if not f.exists():
            continue
        vals = struct.unpack("<%df" % (SIZE * SIZE), f.read_bytes())
        finite = [v for v in vals if v == v]
        lo, hi = min(finite), max(finite)
        scale = 255.0 / (hi - lo) if hi > lo else 0.0
        # Bright = large network value. If near surfaces are bright, the output is disparity-like.
        px = [0 if v != v else int((v - lo) * scale) for v in vals]
        write_png(d / (name + ".png"), SIZE, SIZE, [px[y * SIZE:(y + 1) * SIZE] for y in range(SIZE)], 1)
    meta = json.loads((d / "meta.json").read_text())
    print(d.name, "k=%d" % meta["quarterTurns"], "floor=%d" % meta["hasFloor"])


if __name__ == "__main__":
    for frame in sorted(Path(sys.argv[1]).glob("frame_*")):
        convert(frame)
