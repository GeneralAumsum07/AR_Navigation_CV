"""Tests for analyse_field.py. Run: python -m unittest discover -s tools/field -v"""
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyse_field as af  # noqa: E402

# Camera quaternions (x, y, z, w). Unity: +x rotation tilts the forward axis (+z) downwards.
LEVEL = ("0", "0", "0", "1")
DOWN_60 = (repr(math.sin(math.radians(30))), "0", "0", repr(math.cos(math.radians(30))))
READINGS = ("aimed", "left", "right")


def row(t, q=LEVEL, z=0.0, width="", **readings):
    """readings: name=(valid, raw, filtered, source); missing readings are invalid."""
    r = {"t": repr(t), "updRate": "30", "cam_x": "0", "cam_y": "1.4", "cam_z": repr(z),
         "cam_qx": q[0], "cam_qy": q[1], "cam_qz": q[2], "cam_qw": q[3], "corridor_width_m": width}
    for name in READINGS:
        valid, raw, filt, src = readings.get(name, (0, "", "", "None"))
        r.update({f"{name}_valid": str(valid), f"{name}_raw_m": raw, f"{name}_filtered_m": filt, f"{name}_source": src})
    return r


def ref(**kw):
    base = {"file": "a.csv", "site": "S", "condition": "plain", "point": "P1", "trial": "1", "motion": "standing",
            "ref_aimed_m": "", "ref_left_m": "", "ref_right_m": "", "ref_width_m": "", "notes": ""}
    base.update({k: str(v) for k, v in kw.items()})
    return base


class HelperTests(unittest.TestCase):
    def test_band_edges(self):
        self.assertEqual(af.band(0.3), "<0.5")
        self.assertEqual(af.band(0.5), "0.5-1")
        self.assertEqual(af.band(2.99), "2-3")
        self.assertEqual(af.band(3.0), ">=3")

    def test_forward_y_level_and_pitched_down(self):
        self.assertAlmostEqual(af.forward_y(row(0)), 0.0)
        self.assertAlmostEqual(af.forward_y(row(0, q=DOWN_60)), -math.sin(math.radians(60)), places=6)


class StandingTrialTests(unittest.TestCase):
    def test_acquisition_runs_from_raise_to_first_valid_and_errors_use_reference(self):
        rows = [row(i / 30, q=DOWN_60) for i in range(30)]                       # 0-1 s: at the floor
        rows += [row(1 + i / 30) for i in range(9)]                               # raised, not yet valid
        rows += [row(1.3 + i / 30, aimed=(1, "2.03", "2.01", "FloorEdge")) for i in range(60)]
        res = af.analyse_file(rows, ref(ref_aimed_m=2.0))
        self.assertAlmostEqual(res["acquisition_s"]["aimed"], 0.3, places=6)
        errs = [s for s in res["samples"] if s["reading"] == "aimed"]
        self.assertEqual(len(errs), 60)
        self.assertEqual({(s["source"], s["band"]) for s in errs}, {("FloorEdge", "2-3")})
        self.assertAlmostEqual(errs[0]["err_raw"], 0.03, places=6)
        self.assertAlmostEqual(errs[0]["err_filtered"], 0.01, places=6)

    def test_valid_before_the_raise_counts_as_zero_acquisition(self):
        rows = [row(i / 30, q=DOWN_60, aimed=(1, "1.0", "1.0", "LearnedDepth")) for i in range(10)]
        rows += [row(1 + i / 30, aimed=(1, "1.0", "1.0", "LearnedDepth")) for i in range(10)]
        self.assertEqual(af.analyse_file(rows, ref(ref_aimed_m=1.0))["acquisition_s"]["aimed"], 0.0)

    def test_invalid_rows_lower_valid_fraction_and_never_become_errors(self):
        rows = []
        for i in range(40):
            ok = i % 2 == 0
            rows.append(row(i / 30, aimed=(1, "1.5", "1.5", "FloorEdge") if ok else (0, "", "", "None")))
        res = af.analyse_file(rows, ref(ref_aimed_m=1.5))
        self.assertAlmostEqual(res["valid_frac"]["aimed"], 0.5)
        self.assertEqual(len([s for s in res["samples"] if s["reading"] == "aimed"]), 20)

    def test_unmeasured_reference_produces_no_error_samples(self):
        rows = [row(i / 30, left=(1, "0.9", "0.9", "FloorEdge")) for i in range(10)]
        res = af.analyse_file(rows, ref(ref_aimed_m=2.0))         # ref_left_m blank
        self.assertEqual([s for s in res["samples"] if s["reading"] == "left"], [])

    def test_width_reference_defaults_to_left_plus_right(self):
        rows = [row(i / 30, width="2.45") for i in range(10)]
        res = af.analyse_file(rows, ref(ref_left_m=1.2, ref_right_m=1.3))
        w = [s for s in res["samples"] if s["reading"] == "width"]
        self.assertEqual(len(w), 10)
        self.assertAlmostEqual(w[0]["err_raw"], 0.05, places=6)


class WalkingTrialTests(unittest.TestCase):
    def test_both_sides_fraction_ignores_the_first_two_metres(self):
        rows = []
        for i in range(300):                                          # 10 s at 1 m/s along +z
            t = i / 30
            ok = t >= 1.5 and not (5.0 <= t < 5.5)
            sides = {"left": (1, "1.1", "1.1", "FloorEdge"), "right": (1, "1.2", "1.2", "FloorEdge")} if ok else {}
            rows.append(row(t, z=t, **sides))
        res = af.analyse_file(rows, ref(motion="walking"))
        # After 2 m (t >= 2 s) there are 240 rows, of which 15 (5.0-5.5 s) lack both sides.
        self.assertAlmostEqual(res["both_sides_frac"], 225 / 240, places=6)
        self.assertIsNone(res["acquisition_s"])


class EndToEndTests(unittest.TestCase):
    def _write(self, d, name, rows):
        cols = list(rows[0].keys())
        with open(os.path.join(d, name), "w", encoding="utf-8", newline="") as f:
            f.write("# WallDistance benchmark log; measurement revision=floor-aligned-v1\n")
            f.write(",".join(cols) + "\n")
            for r in rows:
                f.write(",".join(r[c] for c in cols) + "\n")

    def _refs(self, d, refs):
        path = os.path.join(d, "references.csv")
        cols = list(refs[0].keys())
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(",".join(cols) + "\n")
            for r in refs:
                f.write(",".join(r[c] for c in cols) + "\n")
        return path

    def test_main_writes_report(self):
        with tempfile.TemporaryDirectory() as d:
            rows = [row(i / 30, aimed=(1, "2.04", "2.02", "FloorEdge")) for i in range(30)]
            self._write(d, "a.csv", rows)
            out = os.path.join(d, "report.md")
            self.assertEqual(af.main([d, self._refs(d, [ref(ref_aimed_m=2.0)]), "--markdown", out]), 0)
            with open(out, encoding="utf-8") as f:
                text = f.read()
            self.assertIn("| aimed | FloorEdge | 2-3 | 30 |", text)

    def test_main_refuses_missing_log_files(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual(af.main([d, self._refs(d, [ref(file="missing.csv")])]), 2)


if __name__ == "__main__":
    unittest.main()
