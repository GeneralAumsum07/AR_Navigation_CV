"""Tests for summarize_bench.py. Run: python -m unittest discover -s tools/bench -v"""
import gzip
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import summarize_bench as sb  # noqa: E402

COLUMNS = ["t", "fps", "updRate", "infer_ms", "infer_hz", "detect_latency_ms", "prep_ms", "thermal_state"]


def healthy_rows(seconds=601.0, rate=30.0, slow_minute=None):
    """A synthetic log at `rate` rows/s. Inference values alternate 20/24 ms, changing every
    2 rows, the way the real CSV repeats the latest inference on every row until the next."""
    rows = []
    for i in range(int(seconds * rate)):
        t = i / rate
        hz = 4.0 if slow_minute is not None and slow_minute * 60 <= t < (slow_minute + 1) * 60 else 15.0
        rows.append({
            "t": repr(t), "fps": "30", "updRate": "30",
            "infer_ms": "20" if (i // 2) % 2 == 0 else "24",
            "infer_hz": repr(hz),
            "detect_latency_ms": "40" if (i // 2) % 2 == 0 else "45",
            "prep_ms": "3",
            "thermal_state": "1",
        })
    return rows


def write_log(path, rows, gz=False):
    text = "# WallDistance benchmark log; measurement revision=floor-aligned-v1\n"
    text += "# inference=QNN HTP: test\n"
    text += ",".join(COLUMNS) + "\n"
    text += "".join(",".join(r.get(c, "") for c in COLUMNS) + "\n" for r in rows)
    opener = gzip.open if gz else open
    with opener(path, "wt", encoding="utf-8", newline="") as f:
        f.write(text)


class HelperTests(unittest.TestCase):
    def test_num_reads_blank_as_nan(self):
        self.assertTrue(math.isnan(sb.num("")))
        self.assertTrue(math.isnan(sb.num(None)))
        self.assertEqual(sb.num("2.5"), 2.5)

    def test_percentile_is_nearest_rank_and_ignores_nan(self):
        self.assertEqual(sb.percentile([4, 1, 3, 2, math.nan], 50), 2)
        self.assertEqual(sb.percentile([1, 2, 3, 4], 95), 4)
        self.assertTrue(math.isnan(sb.percentile([math.nan], 50)))

    def test_distinct_runs_gives_one_sample_per_inference(self):
        self.assertEqual(sb.distinct_runs([22, 22, 22, 25, 25, math.nan, 25]), [22, 25, 25])

    def test_window_means_counts_a_minute_without_inference_as_zero(self):
        times = [float(t) for t in range(180)]
        values = [15.0 if t < 60 or t >= 120 else math.nan for t in range(180)]
        # 0..179 s holds two complete 60 s windows; the partial third is dropped.
        self.assertEqual(sb.window_means(times, values), [15.0, 0.0])

    def test_read_log_skips_comment_lines_and_reads_gzip(self):
        with tempfile.TemporaryDirectory() as d:
            for gz in (False, True):
                path = os.path.join(d, "log.csv" + (".gz" if gz else ""))
                write_log(path, healthy_rows(seconds=1.0), gz=gz)
                header, rows = sb.read_log(path)
                self.assertEqual(len(header), 2)
                self.assertEqual(len(rows), 30)
                self.assertEqual(rows[0]["fps"], "30")


class SummaryTests(unittest.TestCase):
    def test_healthy_run_passes_every_check(self):
        s = sb.summarize(healthy_rows())
        self.assertAlmostEqual(s["duration_s"], 601.0, delta=0.1)
        self.assertEqual(s["infer_ms_p50"], 20)
        self.assertEqual(s["infer_ms_p95"], 24)
        self.assertEqual(s["detect_latency_ms_p95"], 45)
        self.assertEqual(s["prep_ms_p50"], 3)
        self.assertEqual(s["thermal_max"], 1)
        self.assertEqual(s["infer_hz_window_min"], 15.0)
        self.assertTrue(all(ok for _, ok, _, _ in sb.checks(s)))

    def test_one_slow_minute_fails_the_sustained_rate_check(self):
        s = sb.summarize(healthy_rows(slow_minute=3))
        self.assertEqual(s["infer_hz_window_min"], 4.0)
        self.assertAlmostEqual(s["reduced_rate_frac"], 60 / 601, places=2)
        failed = [label for label, ok, _, _ in sb.checks(s) if not ok]
        self.assertEqual(len(failed), 1)
        self.assertIn("10 Hz", failed[0])

    def test_short_run_fails_the_length_check(self):
        failed = [label for label, ok, _, _ in sb.checks(sb.summarize(healthy_rows(seconds=120.0))) if not ok]
        self.assertTrue(any("600" in label for label in failed))

    def test_cli_writes_markdown_and_returns_zero_on_pass(self):
        with tempfile.TemporaryDirectory() as d:
            log, out = os.path.join(d, "bench.csv"), os.path.join(d, "bench.md")
            write_log(log, healthy_rows())
            self.assertEqual(sb.main([log, "--markdown", out]), 0)
            with open(out, encoding="utf-8") as f:
                text = f.read()
            self.assertIn("PASS", text)
            self.assertNotIn("FAIL", text)
            self.assertIn("revision=floor-aligned-v1", text)


if __name__ == "__main__":
    unittest.main()
