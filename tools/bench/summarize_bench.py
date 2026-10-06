"""Summarise a 10-minute on-device bench log against the spec's rate targets.

Spec §8 ("On-device bench scene") asks for infer_ms p50/p95, end-to-end detection latency,
frame rate and thermal state over 10 minutes; §10 proposes the rate targets checked here.

Usage:
    python tools/bench/summarize_bench.py <walldist_*.csv[.gz]> [--markdown out.md]
Exit code 0 when every check passes, 1 otherwise (2 for a bad file).

Standard library only, so it runs on any Python 3.9+ without installing anything.
"""
import argparse
import csv
import gzip
import math
import os
import sys


def _open_text(path):
    # Unity's StreamWriter emits a UTF-8 BOM. Consume it before checking '#' so the
    # first metadata line never becomes the CSV header; plain UTF-8 also remains valid.
    if path.endswith(".gz"):
        return gzip.open(path, "rt", encoding="utf-8-sig", newline="")
    return open(path, encoding="utf-8-sig", newline="")


def read_log(path):
    """Return (comment header lines, data rows as dicts). Comment lines start with '#'
    (revision, inference status, device, condition, config) and precede the column row."""
    header, body = [], []
    with _open_text(path) as f:
        for line in f:
            if line.startswith("#"):
                header.append(line.rstrip("\r\n"))
            else:
                body.append(line)
    return header, list(csv.DictReader(body))


def num(value):
    """CSV cell -> float. The recorder writes NaN as an empty cell, so blank means 'no value'."""
    try:
        return float(value)
    except (TypeError, ValueError):
        return math.nan


def _finite(values):
    return [v for v in values if not math.isnan(v)]


def percentile(values, q):
    """Nearest-rank percentile of the finite values (NaN when there are none). Nearest rank
    always returns a value that was actually observed, which keeps small samples honest."""
    xs = sorted(_finite(values))
    if not xs:
        return math.nan
    k = max(1, math.ceil(q / 100.0 * len(xs)))
    return xs[k - 1]


def distinct_runs(values):
    """Collapse runs of the same value into one sample. The CSV repeats the latest inference
    result on every ~30 Hz row until the next one arrives, so raw rows would weight each
    inference by how long it stayed latest; slow inferences would count more than fast ones."""
    out, prev = [], None
    for v in values:
        if math.isnan(v):
            prev = None
            continue
        if v != prev:
            out.append(v)
        prev = v
    return out


def window_means(times, values, seconds=60.0):
    """Mean of `values` in each complete `seconds` window from the first row. A window with no
    finite value scores 0: a minute without any inference is a minute without detection."""
    if not times:
        return []
    t0 = times[0]
    n = int((times[-1] - t0) // seconds)
    sums, counts = [0.0] * n, [0] * n
    for t, v in zip(times, values):
        k = int((t - t0) // seconds)
        if k < n and not math.isnan(v):
            sums[k] += v
            counts[k] += 1
    return [s / c if c else 0.0 for s, c in zip(sums, counts)]


def _col(rows, name):
    return [num(r.get(name)) for r in rows]


def summarize(rows):
    t = _col(rows, "t")
    s = {
        "rows": len(rows),
        "duration_s": (t[-1] - t[0]) if len(t) > 1 else 0.0,
        "fps_p50": percentile(_col(rows, "fps"), 50),
        "upd_p50": percentile(_col(rows, "updRate"), 50),
    }
    for name in ("infer_ms", "detect_latency_ms", "prep_ms"):
        samples = distinct_runs(_col(rows, name))
        s[name + "_n"] = len(samples)
        s[name + "_p50"] = percentile(samples, 50)
        s[name + "_p95"] = percentile(samples, 95)
    hz = _col(rows, "infer_hz")
    windows = window_means(t, hz)
    s["infer_hz_p50"] = percentile(hz, 50)
    s["infer_hz_window_min"] = min(windows) if windows else math.nan
    # Inferred, not logged: the governor's 5 Hz step-down shows up as infer_hz near 5, so
    # anything under 7.5 Hz (halfway to the 10 Hz target) is counted as reduced-rate time.
    finite_hz = _finite(hz)
    s["reduced_rate_frac"] = sum(1 for v in finite_hz if v < 7.5) / len(finite_hz) if finite_hz else math.nan
    thermal = [v for v in _col(rows, "thermal_state") if not math.isnan(v) and v >= 0]
    s["thermal_max"] = max(thermal) if thermal else math.nan
    s["thermal_last"] = thermal[-1] if thermal else math.nan
    return s


# (summary key, label, comparison, threshold, where the threshold comes from)
TARGETS = [
    ("duration_s", "run length >= 600 s", ">=", 600.0, "spec §8 (10-minute run)"),
    ("upd_p50", "readings at ~30 Hz (median updRate >= 28)", ">=", 28.0,
     "spec §10; the 28 Hz tolerance is this plan's proposal"),
    ("infer_hz_window_min", "detection >= 10 Hz in every minute", ">=", 10.0, "spec §10 (sustained for 10 min)"),
    ("infer_ms_p50", "NPU inference p50 <= 35 ms", "<=", 35.0, "spec §9 Phase 1 gate, re-measured in-app (D7)"),
]


def checks(summary):
    """[(label, passed, value, source)]. A NaN value fails: no data is not a pass."""
    out = []
    for key, label, op, threshold, source in TARGETS:
        v = summary[key]
        ok = not math.isnan(v) and (v >= threshold if op == ">=" else v <= threshold)
        out.append((label, ok, v, source))
    return out


def _fmt(v):
    return "—" if isinstance(v, float) and math.isnan(v) else (f"{v:.1f}" if isinstance(v, float) else str(v))


def render(path, header, summary, results):
    lines = [f"# Bench summary: `{os.path.basename(path)}`", "", "## Log header", ""]
    # The config line is one long JSON object; it stays in the CSV, not in the summary.
    lines += [f"    {h}" for h in header if not h.startswith("# config")]
    lines += ["", "## Measurements", "", "| metric | value |", "|---|---|"]
    for key in ("rows", "duration_s", "fps_p50", "upd_p50",
                "infer_ms_n", "infer_ms_p50", "infer_ms_p95",
                "detect_latency_ms_p50", "detect_latency_ms_p95",
                "prep_ms_p50", "prep_ms_p95",
                "infer_hz_p50", "infer_hz_window_min", "reduced_rate_frac",
                "thermal_max", "thermal_last"):
        lines.append(f"| {key} | {_fmt(summary[key])} |")
    lines += ["", "`reduced_rate_frac` is inferred from `infer_hz < 7.5`, not logged directly.",
              "`thermal_*` is Android's PowerManager status, 0 (none) to 6 (shutdown); blank before Android 10.",
              "", "## Checks", "", "| check | result | value | threshold source |", "|---|---|---|---|"]
    for label, ok, v, source in results:
        lines.append(f"| {label} | {'PASS' if ok else 'FAIL'} | {_fmt(v)} | {source} |")
    return "\n".join(lines) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("log")
    ap.add_argument("--markdown", help="also write the summary to this file")
    args = ap.parse_args(argv)
    # The report uses ≤, § and —; a Windows console (cp1252) cannot print them and would raise.
    # Replace them on the console only; the --markdown file is always written as UTF-8.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="replace")
    try:
        header, rows = read_log(args.log)
    except OSError as e:
        print(f"cannot read {args.log}: {e}", file=sys.stderr)
        return 2
    if not rows or "infer_ms" not in rows[0]:
        print("no rows, or not a floor-aligned-v1 log (no infer_ms column)", file=sys.stderr)
        return 2
    summary = summarize(rows)
    results = checks(summary)
    text = render(args.log, header, summary, results)
    print(text)
    if args.markdown:
        with open(args.markdown, "w", encoding="utf-8") as f:
            f.write(text)
    return 0 if all(ok for _, ok, _, _ in results) else 1


if __name__ == "__main__":
    sys.exit(main())
