"""Turn dumped input.rgb frames into qnn-net-run raw inputs plus input lists.

The model takes float32 RGB in [0, 1] (normalisation happens inside the graph). The layout
(NCHW or NHWC) comes from the DLC info recorded in Step 2 - pass it with --layout.
"""
import argparse
from pathlib import Path

import numpy as np

SIZE = 518

ap = argparse.ArgumentParser()
ap.add_argument("dumps")
ap.add_argument("out")
ap.add_argument("--layout", choices=["nchw", "nhwc"], required=True)
ap.add_argument("--input-name", required=True, help="input tensor name from dlc-info")
ap.add_argument("--device-dir", default="/data/local/tmp/qnn/inputs")
ap.add_argument("--bench-repeats", type=int, default=5)
a = ap.parse_args()

out = Path(a.out)
out.mkdir(parents=True, exist_ok=True)
lines = []
for frame in sorted(Path(a.dumps).glob("frame_*")):
    rgb = np.frombuffer((frame / "input.rgb").read_bytes(), dtype=np.uint8).reshape(SIZE, SIZE, 3)
    x = rgb.astype(np.float32) / 255.0
    if a.layout == "nchw":
        x = np.transpose(x, (2, 0, 1))
    name = frame.name + ".raw"
    x.astype("<f4").tofile(out / name)
    lines.append(f"{a.input_name}:={a.device_dir}/{name}")

(out / "input_list.txt").write_text("\n".join(lines) + "\n")
# Latency list: every frame repeated, so the profile holds hundreds of inferences.
(out / "input_list_bench.txt").write_text("\n".join(lines * a.bench_repeats) + "\n")
(out / "frames.txt").write_text("\n".join(l.split("/")[-1][:-4] for l in lines) + "\n")
print(len(lines), "frames")
