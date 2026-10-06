"""Field benchmark analysis (spec §8 field protocol, §10 proposed targets).

Usage:
    python tools/field/analyse_field.py <logs_dir> <references.csv> [--markdown report.md]

Each row of references.csv names one recording in logs_dir and its tape-measured distances,
taken from the camera position (protocol: docs/benchmarks/field-protocol.md).
Exit code 0 on success, 2 when inputs are missing or unreadable. Targets are reported,
not enforced: they are proposals for Rachit to confirm (spec §10).
"""
import argparse
import csv
import math
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bench"))
from summarize_bench import num, percentile, read_log  # noqa: E402

READINGS = ("aimed", "left", "right")
BANDS = [(0.0, 0.5, "<0.5"), (0.5, 1.0, "0.5-1"), (1.0, 1.5, "1-1.5"), (1.5, 2.0, "1.5-2"),
         (2.0, 3.0, "2-3"), (3.0, math.inf, ">=3")]
# A camera within 20° of level counts as "raised to the wall". Same angle as the side-wall
# no-heading rule, used here for the opposite purpose: knowing when the tester has aimed.
LEVEL_SIN = math.sin(math.radians(20))
# Spec §10: left/right valid fraction is judged after the first 2 m of a walk.
WALK_WARMUP_M = 2.0


def band(meters):
    for lo, hi, name in BANDS:
        if lo <= meters < hi:
            return name
    return "<0.5"


def forward_y(row):
    """Vertical component of the camera's forward axis from the logged quaternion.
    Rotating (0,0,1) by q=(x,y,z,w) gives forward.y = 2(yz - wx)."""
    x, y, z, w = (num(row.get(k)) for k in ("cam_qx", "cam_qy", "cam_qz", "cam_qw"))
    return 2.0 * (y * z - w * x)


def _valid(row, name):
    return row.get(f"{name}_valid") in ("1", "True", "true")


def _ref(ref, key):
    return num(ref.get(key))


def load_references(path):
    with open(path, encoding="utf-8", newline="") as f:
        return [r for r in csv.DictReader(f) if (r.get("file") or "").strip()]


def analyse_file(rows, ref):
    """One recording. Invalid readings lower the valid fraction; they never become an error
    value (a missing reading is not a zero-distance reading)."""
    t = [num(r.get("t")) for r in rows]
    refs = {"aimed": _ref(ref, "ref_aimed_m"), "left": _ref(ref, "ref_left_m"), "right": _ref(ref, "ref_right_m")}
    ref_width = _ref(ref, "ref_width_m")
    if math.isnan(ref_width) and not math.isnan(refs["left"]) and not math.isnan(refs["right"]):
        # Both lateral distances are taped from the same camera position, so they sum to the width there.
        ref_width = refs["left"] + refs["right"]
    walking = (ref.get("motion") or "").strip() == "walking"

    samples = []
    for r in rows:
        for name in READINGS:
            if math.isnan(refs[name]) or not _valid(r, name):
                continue
            raw, filt = num(r.get(f"{name}_raw_m")), num(r.get(f"{name}_filtered_m"))
            if math.isnan(raw):
                continue
            samples.append({"reading": name, "source": r.get(f"{name}_source") or "None", "band": band(refs[name]),
                            "err_raw": abs(raw - refs[name]),
                            "err_filtered": abs(filt - refs[name]) if not math.isnan(filt) else math.nan})
        width = num(r.get("corridor_width_m"))
        if not math.isnan(ref_width) and not math.isnan(width):
            samples.append({"reading": "width", "source": "width", "band": band(ref_width),
                            "err_raw": abs(width - ref_width), "err_filtered": abs(width - ref_width)})

    result = {"file": ref.get("file"), "motion": "walking" if walking else "standing", "samples": samples,
              "upd_p50": percentile([num(r.get("updRate")) for r in rows], 50),
              "valid_frac": {}, "acquisition_s": None, "both_sides_frac": math.nan}
    for name in READINGS:
        valid = sum(1 for r in rows if _valid(r, name))
        result["valid_frac"][name] = valid / len(rows) if rows else math.nan

    if walking:
        # Path length on the floor plane, so turning on the spot does not count as distance.
        dist, prev, after = 0.0, None, []
        for r in rows:
            p = (num(r.get("cam_x")), num(r.get("cam_z")))
            if prev is not None and not any(math.isnan(v) for v in p + prev):
                dist += math.hypot(p[0] - prev[0], p[1] - prev[1])
            prev = p
            if dist >= WALK_WARMUP_M:
                after.append(_valid(r, "left") and _valid(r, "right"))
        result["both_sides_frac"] = sum(after) / len(after) if after else math.nan
    else:
        # Acquisition proxy (inferred, see the protocol): the clock starts at the first level
        # row, i.e. when the tester has raised the phone from the floor to the wall.
        start = next((t[i] for i, r in enumerate(rows) if abs(forward_y(r)) < LEVEL_SIN), math.nan)
        result["acquisition_s"] = {}
        for name in READINGS:
            first = next((t[i] for i, r in enumerate(rows) if _valid(r, name)), math.nan)
            result["acquisition_s"][name] = math.nan if math.isnan(start) or math.isnan(first) else max(0.0, first - start)
    return result


def aggregate(results):
    errors = {}
    for res in results:
        for s in res["samples"]:
            errors.setdefault((s["reading"], s["source"], s["band"]), []).append(s)
    acq = {name: [res["acquisition_s"][name] for res in results
                  if res["acquisition_s"] is not None and not math.isnan(res["acquisition_s"][name])]
           for name in READINGS}
    standing = [r for r in results if r["motion"] == "standing"]
    return {
        "errors": {k: {"n": len(v),
                       "raw_p50": percentile([s["err_raw"] for s in v], 50),
                       "raw_p95": percentile([s["err_raw"] for s in v], 95),
                       "filt_p50": percentile([s["err_filtered"] for s in v], 50),
                       "filt_p95": percentile([s["err_filtered"] for s in v], 95)}
                   for k, v in sorted(errors.items())},
        "acquisition": {name: {"n": len(v), "p50": percentile(v, 50), "p95": percentile(v, 95)} for name, v in acq.items()},
        "valid_frac": {name: percentile([r["valid_frac"][name] for r in standing], 50) for name in READINGS},
        "both_sides_frac": [r["both_sides_frac"] for r in results if r["motion"] == "walking"],
        "upd_p50": percentile([r["upd_p50"] for r in results], 50),
        "files": len(results),
    }


def _cm(v):
    return "—" if math.isnan(v) else f"{v * 100:.1f}"


def _s(v):
    return "—" if math.isnan(v) else f"{v:.2f}"


def render(agg):
    lines = [f"# Field benchmark report ({agg['files']} recordings)", "",
             "## Absolute error (cm) by reading, source and reference distance", "",
             "| reading | source | band (m) | n | raw p50 | raw p95 | filtered p50 | filtered p95 |",
             "|---|---|---|---|---|---|---|---|"]
    for (reading, source, b), e in agg["errors"].items():
        lines.append(f"| {reading} | {source} | {b} | {e['n']} | {_cm(e['raw_p50'])} | {_cm(e['raw_p95'])} | "
                     f"{_cm(e['filt_p50'])} | {_cm(e['filt_p95'])} |")
    lines += ["", "n counts rows (~30 per second), not trials; rows within one trial are correlated.", "",
              "## Acquisition (s, standing trials)", "",
              "Inferred proxy: from the first row with the camera within 20° of level (phone raised) "
              "to the first valid reading. Spec §10 proposal: median ≤ 0.5 s, P95 ≤ 1.5 s.", "",
              "| reading | trials | p50 | p95 |", "|---|---|---|---|"]
    for name, a in agg["acquisition"].items():
        lines.append(f"| {name} | {a['n']} | {_s(a['p50'])} | {_s(a['p95'])} |")
    lines += ["", "## Valid fraction", "",
              "| measure | value |", "|---|---|"]
    for name, v in agg["valid_frac"].items():
        lines.append(f"| {name}, standing (median over trials) | {_s(v)} |")
    for i, v in enumerate(agg["both_sides_frac"], 1):
        lines.append(f"| both sides after first 2 m, walk {i} (spec §10 proposal ≥ 0.90) | {_s(v)} |")
    lines += [f"| update rate, median of trial medians (Hz) | {_s(agg['upd_p50'])} |", ""]
    return "\n".join(lines)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("logs_dir")
    ap.add_argument("references")
    ap.add_argument("--markdown", help="also write the report to this file")
    args = ap.parse_args(argv)
    # The report uses ≤, § and —; a Windows console (cp1252) cannot print them and would raise.
    # Replace them on the console only; the --markdown file is always written as UTF-8.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="replace")
    try:
        refs = load_references(args.references)
    except OSError as e:
        print(f"cannot read {args.references}: {e}", file=sys.stderr)
        return 2
    missing = [r["file"] for r in refs if not os.path.isfile(os.path.join(args.logs_dir, r["file"]))]
    if missing or not refs:
        print("missing log files: " + (", ".join(missing) if missing else "references.csv has no rows"), file=sys.stderr)
        return 2
    results = []
    for r in refs:
        _, rows = read_log(os.path.join(args.logs_dir, r["file"]))
        results.append(analyse_file(rows, r))
    text = render(aggregate(results))
    print(text)
    if args.markdown:
        with open(args.markdown, "w", encoding="utf-8") as f:
            f.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
