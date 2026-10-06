"""Phase 1: is the network output affine in inverse depth or in depth, against the floor?

For each dumped frame with a tracked floor, compute every content pixel's metric floor depth
from meta.json (floor plane + inference camera), robustly fit both models on floor-like pixels,
and report the median relative depth residual of each. Lower wins (spec §6). Run for each
variant (w8a16, float) to compare quantisation damage.
"""
import argparse
import json
from pathlib import Path

import numpy as np

SIZE = 518
rng = np.random.default_rng(1)


def quat_rotate(q, v):
    """Rotate vectors v (N,3) by quaternion q = (x, y, z, w). Same formula Unity uses."""
    x, y, z, w = q
    u = np.array([x, y, z])
    t = 2.0 * np.cross(u, v)
    return v + w * t + np.cross(u, t)


def floor_depth(meta):
    fx, fy, cx, cy = meta["intrinsics"]
    uu, vv = np.meshgrid(np.arange(SIZE, dtype=np.float64), np.arange(SIZE, dtype=np.float64))
    ray = np.stack([(uu - cx) / fx, -(vv - cy) / fy, np.ones_like(uu)], -1).reshape(-1, 3)
    d = quat_rotate(meta["cameraRotationXYZW"], ray)
    up = np.array(meta["floorUp"])
    h = np.dot(np.array(meta["cameraPosition"]) - np.array(meta["floorPoint"]), up)
    denom = d @ up
    z = np.full(SIZE * SIZE, np.nan)
    ok = denom < -1e-4
    z[ok] = -h / denom[ok]          # dir has unit camera z, so this is the Z-depth
    x0, y0, w, hgt = meta["content"]
    mask = np.zeros((SIZE, SIZE), bool)
    mask[int(y0):int(y0 + hgt), int(x0):int(x0 + w)] = True
    z[~mask.reshape(-1)] = np.nan
    z[(z < 0.3) | (z > 10)] = np.nan
    return z


def fit(d, z, inverse, iters=200, inlier=0.06):
    y = 1.0 / z if inverse else z
    best = None
    for _ in range(iters):
        i, j = rng.choice(len(d), 2, replace=False)
        if abs(d[i] - d[j]) < 1e-6:
            continue
        s = (y[i] - y[j]) / (d[i] - d[j])
        t = y[i] - s * d[i]
        pred = s * d + t
        with np.errstate(divide="ignore", invalid="ignore"):
            zp = 1.0 / pred if inverse else pred
        rel = np.abs(zp - z) / z
        n = np.sum(rel < inlier)
        if best is None or n > best[0]:
            best = (n, s, t)
    if best is None:
        return np.nan, np.nan, np.nan, 0
    _, s, t = best
    for _ in range(2):   # least-squares refits on inliers, as FloorAlignedDepth does
        pred = s * d + t
        with np.errstate(divide="ignore", invalid="ignore"):
            zp = 1.0 / pred if inverse else pred
        inl = np.abs(zp - z) / z < inlier
        if inl.sum() < 2:
            break
        A = np.stack([d[inl], np.ones(inl.sum())], 1)
        s, t = np.linalg.lstsq(A, y[inl], rcond=None)[0]
    pred = s * d + t
    with np.errstate(divide="ignore", invalid="ignore"):
        zp = 1.0 / pred if inverse else pred
    rel = np.abs(zp - z) / z
    inl = rel < inlier
    return s, t, float(np.median(rel[inl])) if inl.any() else np.nan, int(inl.sum())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dumps")
    ap.add_argument("outputs", help="qnn-net-run output dir (Result_<i>/...raw)")
    ap.add_argument("frames", help="frames.txt from make_inputs.py (maps Result_i to frame)")
    ap.add_argument("--layout", choices=["nchw", "nhwc"], default="nchw", help="output layout; any is fine for 1 channel")
    a = ap.parse_args()
    frames = Path(a.frames).read_text().split()
    rows = []
    for i, name in enumerate(frames):
        meta = json.loads((Path(a.dumps) / name / "meta.json").read_text())
        if not meta["hasFloor"]:
            continue
        raws = list((Path(a.outputs) / f"Result_{i}").glob("*.raw"))
        if len(raws) != 1:
            print(name, "missing output"); continue
        d = np.fromfile(raws[0], dtype="<f4")
        if d.size != SIZE * SIZE:
            print(name, "unexpected output size", d.size); continue
        z = floor_depth(meta)
        ok = np.isfinite(z) & np.isfinite(d)
        if ok.sum() < 500:
            continue
        idx = np.flatnonzero(ok)[::4]
        inv = fit(d[idx], z[idx], True)
        lin = fit(d[idx], z[idx], False)
        rows.append((name, inv, lin))
        print(f"{name}: inverse res={inv[2]:.4f} n={inv[3]}  depth res={lin[2]:.4f} n={lin[3]}  s_inv={inv[0]:+.4g}")
    if not rows:
        print("no usable frames"); return
    inv_med = np.nanmedian([r[1][2] for r in rows])
    lin_med = np.nanmedian([r[2][2] for r in rows])
    print(f"\nframes={len(rows)}  median residual: AffineInverseDepth={inv_med:.4f}  AffineDepth={lin_med:.4f}")
    print("winner:", "AffineInverseDepth" if inv_med <= lin_med else "AffineDepth")


if __name__ == "__main__":
    main()
