# Floor-free metric scale Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the learned wall detector measure in corridors where ARCore finds no floor plane, by deriving the floor from the depth network's own output and learning the camera height from sparse metric clues pooled over a few seconds.

**Architecture:** Three new pure-C# Core units: `FloorSelfAligner` (finds the inverse-depth shift by floor flatness), `HeightClues` (turns an ARCore floor or confident raw-depth samples into one camera-height clue per frame), `CameraHeightEstimator` (windowed weighted median of the floor's world height, with hold and carry-over across pose jumps). `FloorResolver` composes them and hands the existing pipeline an ordinary `FloorPlane`, so alignment, extraction, the wall map and every A–D fix run unchanged downstream.

**Tech Stack:** Unity 6000.3.5f1, C# (Core + AR assemblies), AR Foundation/ARCore, NUnit EditMode tests, `unity` CLI, adb.

**Spec:** `docs/superpowers/specs/2026-10-07-floor-free-scale-design.md` (amends `docs/superpowers/specs/2026-10-06-fast-wall-detection-design.md`). Read both.

## Global Constraints

- Camera height is **measured, never assumed**. No constant camera height anywhere in code or config.
- The ARCore floor always wins when present.
- `FailureReason` values are append-only: existing numbers never change (`ApiCompatibilityTests`).
- Readings stay NaN when invalid, never 0.
- Comment code heavily: explain **why**, not what (Rachit's standing rule).
- Do **not** commit unless Rachit asks for that commit. When asked: ordinary engineering voice, **no** `Co-Authored-By` trailer, no tool credits, no "Generated with" footer.
- Never edit `Assets/Scenes/WallMeasurement.unity` on disk. Nothing in this plan needs a scene change (components are created in `WallDistanceService.Awake`).
- Write long files with the Write tool; Git Bash heredocs mangle them.
- Use `py` (not `python`) for scripts: only `py` has numpy.
- adb: `"/c/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"` with `export MSYS_NO_PATHCONV=1`; local paths given to `adb pull` must be Windows-style (`C:\...`).

**Recompile and test (used by every task; run from the repo root `C:/Users/Rachit/AR_nav_CV`):**

```bash
unity command recompile --no-banner; for i in $(seq 1 60); do unity command recompile_status --result-only --no-banner | grep -q compiling || break; sleep 5; done; unity command recompile_status --result-only --no-banner
```

```bash
unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 500 --no-banner --result-only > "$TEMP/wd-tests.json"; py -c "import json,os; d=json.load(open(os.path.join(os.environ['TEMP'],'wd-tests.json'),encoding='utf-8-sig')); print(json.dumps(d.get('Summary', d), indent=1)[:3000])"
```

Run the whole assembly every time (223 tests pass before this plan); grep the JSON for the new test class names to read individual results.

## Review Focus

1. **Glossy-floor reflections**: the network sees mirror images as depth below the floor. Expected: self-alignment still recovers wall depth within 2%, or fails the frame; never a confidently wrong floor. Test: `FloorSelfAlignerTests.ReflectionsOnGlossyFloor_StillRecoverWallDepth` (Task 1).
2. **Close-up of a wall with a sliver of floor**: shift not identifiable. Expected: frame rejected, held estimate unaffected. Test: `FloorSelfAlignerTests.WallCloseUp_IsRejected` (Task 1).
3. **Raw-depth samples split between two surfaces that disagree** (e.g. a shiny door at the wrong depth). Expected: no clue that frame. Test: `HeightCluesTests.DisagreeingSamples_GiveNoClue` (Task 2).
4. **ARCore pose jump while the estimate is held**. Expected: the camera's height above the floor carries over, the floor is re-placed under the camera, no multi-second "calibrating". Test: `CameraHeightEstimatorTests.Discontinuity_CarriesCameraHeight` (Task 3).
5. **Walking start with no ARCore floor at all** (the run-150956 situation). Expected: "calibrating" for ~2 s, then walls within 5% from a derived floor. Test: `FloorResolverTests.NoArFloor_CorridorWalk_DerivedFloorThenWalls` (Task 4).

---

### Task 1: `FloorSelfAligner` (shift from floor flatness)

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/Selection.cs`
- Create: `Assets/WallDistance/Runtime/Core/FloorSelfAligner.cs`
- Modify: `Assets/WallDistance/Runtime/Core/DetectionConfig.cs` (new header block after "See-through removal", before "Staleness")
- Test: `Assets/WallDistance/Tests/EditMode/FloorSelfAlignerTests.cs`

**Interfaces:**
- Consumes: `InverseDepthImage` (`values`, `content`, `WorldRay`, `cameraPose`), `DetectionConfig.parameterisation`, `DetectionConfig.maxAlignResidual`.
- Produces:
  - `public static class Selection { public static float Kth(float[] a, int n, int k); }`
  - `public sealed class SelfAlignment { bool success; float r, hRel, residual, depthRatio; int candidates, inliers; double milliseconds; string reason; float MetricDepth(float d, float cameraHeightMeters); }`
  - `public sealed class FloorSelfAligner { FloorSelfAligner(DetectionConfig); SelfAlignment Align(InverseDepthImage img); SelfAlignment Result { get; } }`
  - `DetectionConfig` fields: `selfAlignStride, selfAlignMinDescentDeg, selfAlignHeightTolerance, selfAlignMinInliers, selfAlignMinDepthRatio, selfAlignCoarseSteps, selfAlignFineSteps, minClueSamples, maxClueWeight, maxClueSpread, heightPlaneWeight, heightWindowSeconds, minHeightWeight, minHeightClues, minHeightSpanSeconds, maxHeightSpreadMeters, heightHoldSeconds`.

- [ ] **Step 1: Add the config block to `DetectionConfig.cs`** (all of Tasks 1–3 read it; inserting it now keeps later tasks compiling)

Insert before `[Header("Staleness")]`:

```csharp
        [Header("Floor-free scale (2026-10-07 floor-free design)")]
        [Tooltip("Pixel stride when collecting downward-looking pixels for the flatness search.")]
        public int selfAlignStride = 4;
        [Tooltip("Rays closer to horizontal than this cannot reach the floor nearby; they only add far, noisy points.")]
        public float selfAlignMinDescentDeg = 3f;
        [Tooltip("A pixel is on the floor when its height is within this fraction of the camera height. Relative, so "
                 + "the score does not depend on the network's unknown scale.")]
        public float selfAlignHeightTolerance = 0.03f;
        public int selfAlignMinInliers = 500;
        [Tooltip("The floor must be seen over at least this far/near depth ratio. A floor seen over a narrow band "
                 + "(close-up of a wall) is flat for many shifts, so the shift is not identifiable.")]
        public float selfAlignMinDepthRatio = 2f;
        public int selfAlignCoarseSteps = 64;
        public int selfAlignFineSteps = 21;
        [Tooltip("Confident raw-depth samples needed for one camera-height clue. Run 150956 had a median of 35 per frame.")]
        public int minClueSamples = 10;
        [Tooltip("Cap on one frame's weight, so one sample-rich frame cannot outvote seconds of others.")]
        public int maxClueWeight = 200;
        [Tooltip("A frame's samples must agree: median absolute deviation / median height at most this.")]
        public float maxClueSpread = 0.15f;
        [Tooltip("Weight of an ARCore floor clue; equal to the strongest raw-depth clue.")]
        public float heightPlaneWeight = 200f;
        [Tooltip("Clues older than this are dropped. Short, because ARCore's vertical position drifts.")]
        public float heightWindowSeconds = 5f;
        public float minHeightWeight = 100f;
        public int minHeightClues = 5;
        [Tooltip("Clues must span this long before the first estimate is trusted: one moment's view can be biased.")]
        public float minHeightSpanSeconds = 2f;
        [Tooltip("Half of the ~14 cm that 10% of a 1.4 m camera height allows (Rachit's 10% target).")]
        public float maxHeightSpreadMeters = 0.07f;
        [Tooltip("How long a good estimate is kept while too few new clues arrive.")]
        public float heightHoldSeconds = 20f;
```

- [ ] **Step 2: Write the failing tests**

Create `Assets/WallDistance/Tests/EditMode/FloorSelfAlignerTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class FloorSelfAlignerTests
    {
        const float CameraHeight = 1.4f; // SyntheticCorridor.Camera default; the floor is y = 0

        static SelfAlignment Align(InverseDepthImage img, DetectionConfig cfg = null) =>
            new FloorSelfAligner(cfg ?? new DetectionConfig()).Align(img);

        /// <summary>
        /// Median relative error of the self-aligned depth on wall pixels, scaled by the TRUE
        /// camera height. That isolates what self-alignment is responsible for (the shift and
        /// the floor's relative height) from the metric clue, which later tasks supply.
        /// </summary>
        static float WallDepthError(SyntheticCorridor.Rendered r, SelfAlignment a)
        {
            var errs = new List<float>();
            var img = r.image;
            for (int i = 0; i < img.values.Length; i += 7)
            {
                if (img.luma[i] != SyntheticCorridor.WallLuma || float.IsNaN(r.depth[i])) continue;
                float z = a.MetricDepth(img.values[i], CameraHeight);
                errs.Add(Mathf.Abs(z - r.depth[i]) / r.depth[i]);
            }
            Assert.Greater(errs.Count, 100, "test needs wall pixels in view");
            errs.Sort();
            return errs[errs.Count / 2];
        }

        [Test]
        public void Selection_Kth_MatchesSort()
        {
            var rng = new System.Random(4);
            for (int trial = 0; trial < 20; trial++)
            {
                int n = 1 + rng.Next(200);
                var a = new float[n];
                // Few distinct values on purpose: duplicates are the classic quickselect failure.
                for (int i = 0; i < n; i++) a[i] = rng.Next(10);
                var sorted = (float[])a.Clone();
                System.Array.Sort(sorted);
                int k = rng.Next(n);
                Assert.AreEqual(sorted[k], Selection.Kth(a, n, k));
            }
        }

        [Test]
        public void ExactData_RecoversWallDepth()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var a = Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            Assert.LessOrEqual(WallDepthError(r, a), 0.005f);
            Assert.GreaterOrEqual(a.depthRatio, 2f);
        }

        [Test]
        public void UnknownAffineAndNoise_RecoversWallDepth()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f, relativeNoise: 0.01f, seed: 3);
            var a = Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            // Per-pixel noise alone gives a median |error| of ~0.7% (|N(0, 1%)|); 1.5% leaves ~0.8% for the shift.
            Assert.LessOrEqual(WallDepthError(r, a), 0.015f);
        }

        [TestCase(10f)]
        [TestCase(30f)]
        public void CameraPitch_RecoversWallDepth(float pitchDown)
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(pitchDown: pitchDown));
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var a = Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            Assert.LessOrEqual(WallDepthError(r, a), 0.01f);
        }

        [Test]
        public void NetworkScale_DoesNotChangeMetricDepth()
        {
            // Depth Anything's scale differs per frame; only the shift/scale ratio may matter.
            foreach (float scale in new[] { 0.5f, 2f, 7f })
            {
                var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
                SyntheticCorridor.ToNetworkOutput(r, scale: scale, shift: 0.05f);
                var a = Align(r.image);
                Assert.IsTrue(a.success, $"scale {scale}: {a.reason}");
                Assert.LessOrEqual(WallDepthError(r, a), 0.005f, $"scale {scale}");
            }
        }

        [Test]
        public void LookingUp_NoFloorInView_Fails()
        {
            // Pitched 35° up: every ray is above the horizon, so nothing can be floor.
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(pitchDown: -35f));
            SyntheticCorridor.ToNetworkOutput(r);
            var a = Align(r.image);
            Assert.IsFalse(a.success);
            StringAssert.Contains("downward", a.reason);
        }

        [Test]
        public void WallCloseUp_IsRejected()
        {
            // Facing the right wall from 0.5 m, pitched 55° down: the floor in view spans a narrow
            // depth band, which is flat for many shifts. The 10-07 dumps had such frames.
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(x: 0.5f, pitchDown: 55f, yaw: 90f));
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var a = Align(r.image);
            Assert.IsFalse(a.success, $"must not trust a shift fixed by a sliver of floor (ratio {a.depthRatio:F2})");
        }

        [Test]
        public void ReflectionsOnGlossyFloor_StillRecoverWallDepth()
        {
            // 15% of floor pixels read farther than the floor, as mirror images in a glossy floor
            // do. Random speckle is a weaker stand-in than real coherent reflections; the field
            // run is the real check (spec §8).
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var img = SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var rng = new System.Random(9);
            for (int i = 0; i < img.values.Length; i++)
                if (img.luma[i] == SyntheticCorridor.FloorLuma && rng.NextDouble() < 0.15) img.values[i] *= 0.6f;
            var a = Align(img);
            Assert.IsTrue(a.success, a.reason);
            Assert.LessOrEqual(WallDepthError(r, a), 0.02f);
        }

        [Test]
        public void DoorRecessAndPerson_DoNotBreakTheFloor()
        {
            var corridor = new SyntheticCorridor { door = true, person = true };
            var r = corridor.Render(SyntheticCorridor.Camera());
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var a = Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            Assert.LessOrEqual(WallDepthError(r, a), 0.01f);
        }

        [Test]
        public void AffineDepthParameterisation_IsUnsupported()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            SyntheticCorridor.ToNetworkOutput(r);
            var a = Align(r.image, new DetectionConfig { parameterisation = DepthParameterisation.AffineDepth });
            Assert.IsFalse(a.success);
            StringAssert.Contains("AffineInverseDepth", a.reason);
        }
    }
}
```

- [ ] **Step 3: Recompile and confirm it fails**

Run the recompile command. Expected: compile errors `Selection` / `FloorSelfAligner` / `SelfAlignment` not found.

- [ ] **Step 4: Write `Selection.cs`**

```csharp
namespace WallDistance.Core
{
    /// <summary>
    /// Order statistics on caller-owned scratch arrays, without allocating. Floor self-alignment
    /// takes percentiles of thousands of heights for each of ~85 trial shifts per frame; sorting
    /// every time would dominate the frame budget.
    /// </summary>
    public static class Selection
    {
        /// <summary>The k-th smallest (0-based) of a[0..n). Reorders a[0..n).</summary>
        public static float Kth(float[] a, int n, int k)
        {
            int lo = 0, hi = n - 1;
            while (lo < hi)
            {
                // Hoare partition around the middle element; with many duplicates (integer-like
                // heights) it still splits evenly, unlike a Lomuto partition.
                float pivot = a[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (a[i] < pivot) i++;
                    while (a[j] > pivot) j--;
                    if (i <= j)
                    {
                        float tmp = a[i];
                        a[i] = a[j];
                        a[j] = tmp;
                        i++;
                        j--;
                    }
                }
                // [lo..j] ≤ pivot ≤ [i..hi]; anything strictly between equals the pivot.
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return a[k];
            }
            return a[k];
        }
    }
}
```

- [ ] **Step 5: Write `FloorSelfAligner.cs`**

```csharp
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Outcome of fixing one frame's inverse-depth shift from floor flatness. Kept on failure so
    /// the CSV can say why a frame gave no camera-height clue.
    /// </summary>
    public sealed class SelfAlignment
    {
        public bool success;
        /// <summary>Shift in network units: metric depth is z = k / (d + r) for an unknown k.</summary>
        public float r = float.NaN;
        /// <summary>Camera height above the floor in the same units as 1/(d + r).</summary>
        public float hRel = float.NaN;
        public int candidates, inliers;
        /// <summary>Median |height − floor| / camera height over the floor inliers.</summary>
        public float residual = float.NaN;
        /// <summary>95th / 5th percentile depth of the floor inliers; low means the shift is not identifiable.</summary>
        public float depthRatio = float.NaN;
        public double milliseconds = double.NaN;
        public string reason = "";

        /// <summary>
        /// Metric Z-depth for a network value once the camera height is known: the floor sits
        /// hRel below the camera in relative units and H below it in metres, so k = H / hRel.
        /// </summary>
        public float MetricDepth(float d, float cameraHeightMeters)
        {
            float shifted = d + r;
            if (!success || !(shifted > 0f) || !(hRel > 0f)) return float.NaN;
            return cameraHeightMeters / hRel / shifted;
        }

        internal void Reset()
        {
            success = false;
            r = hRel = residual = depthRatio = float.NaN;
            candidates = inliers = 0;
            milliseconds = double.NaN;
            reason = "";
        }
    }

    /// <summary>
    /// Finds the floor in one network output without ARCore (floor-free design §4.1).
    ///
    /// Depth Anything gives d with 1/z = s·d + t, i.e. z = k / (d + r). For a wrong r the floor
    /// back-projects as a curved sheet; only the right r makes it one flat horizontal plane
    /// (gravity is known from the AR pose). So flatness fixes r, and the camera's height above
    /// the floor in network units, without any metric input. The single remaining unknown, k,
    /// is the camera height in metres: CameraHeightEstimator learns that from sparse clues.
    ///
    /// The 2026-10-07 corridors had no ARCore floor at all, which left the learned path with no
    /// scale; on the 9 dumped frames with an ARCore floor and the floor seen over a range of
    /// depths, this reproduced ARCore-floor depths within 1-7% (spike, spec §3).
    /// </summary>
    public sealed class FloorSelfAligner
    {
        readonly DetectionConfig _cfg;
        readonly List<float> _d = new List<float>(8192);
        // World-up component of each candidate's unit-camera-Z ray: height = z · _den.
        readonly List<float> _den = new List<float>(8192);
        float[] _y = new float[0];
        float[] _work = new float[0];
        readonly Stopwatch _watch = new Stopwatch();

        public SelfAlignment Result { get; } = new SelfAlignment();

        public FloorSelfAligner(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

        public SelfAlignment Align(InverseDepthImage img)
        {
            _watch.Restart();
            var res = Result;
            res.Reset();
            try { Run(img, res); }
            finally
            {
                _watch.Stop();
                res.milliseconds = _watch.Elapsed.TotalMilliseconds;
            }
            return res;
        }

        void Run(InverseDepthImage img, SelfAlignment res)
        {
            if (img == null) { res.reason = "no image"; return; }
            // z = k/(d + r) is the inverse-depth model; an affine-in-depth network would need a
            // different flatness search, and Phase 1 fixed the parameterisation to inverse depth.
            if (_cfg.parameterisation != DepthParameterisation.AffineInverseDepth)
            {
                res.reason = "self-alignment supports AffineInverseDepth only";
                return;
            }

            Collect(img, out float dMin, out float dMax);
            res.candidates = _d.Count;
            if (_d.Count < _cfg.selfAlignMinInliers)
            {
                res.reason = $"only {_d.Count} downward pixels (need {_cfg.selfAlignMinInliers})";
                return;
            }

            // r must keep every candidate in front of the camera: r > −min d. Offsets above that
            // bound are searched on a log scale because the right one can be anywhere from
            // "just above" (far points dominate) to "huge" (nearly constant depth).
            float range = Mathf.Max(dMax - dMin, 1e-6f);
            int coarse = Mathf.Max(8, _cfg.selfAlignCoarseSteps);
            int best = -1, bestScore = -1;
            for (int i = 0; i < coarse; i++)
            {
                int score = Score(-dMin + Offset(i, coarse, range), out _);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            // An optimum at either end means flatness never peaked: no floor-like surface.
            if (best <= 0 || best >= coarse - 1)
            {
                res.reason = "floor flatness has no interior optimum";
                return;
            }

            // A linear grid between the neighbours, not golden-section search: the score is an
            // integer count and has plateaus a bracketing search can stall on.
            float lo = Offset(best - 1, coarse, range), hi = Offset(best + 1, coarse, range);
            float bestR = -dMin + Offset(best, coarse, range);
            int fine = Mathf.Max(3, _cfg.selfAlignFineSteps);
            for (int j = 0; j < fine; j++)
            {
                float r = -dMin + Mathf.Lerp(lo, hi, j / (float)(fine - 1));
                int score = Score(r, out _);
                if (score > bestScore) { bestScore = score; bestR = r; }
            }

            Describe(bestR, res);
            if (res.inliers < _cfg.selfAlignMinInliers)
                res.reason = $"only {res.inliers} floor inliers (need {_cfg.selfAlignMinInliers})";
            else if (!(res.hRel > 0f))
                res.reason = "flattest surface is not below the camera";
            else if (res.residual > _cfg.maxAlignResidual)
                res.reason = $"floor residual {res.residual:P1} above {_cfg.maxAlignResidual:P1}";
            else if (!(res.depthRatio >= _cfg.selfAlignMinDepthRatio))
                res.reason = $"floor spans depth ratio {res.depthRatio:F2} (need {_cfg.selfAlignMinDepthRatio:F1}): shift not identifiable";
            else
                res.success = true;
        }

        static float Offset(int i, int steps, float range) =>
            range * 1e-4f * Mathf.Pow(10f, 7f * i / (steps - 1));

        /// <summary>Downward-looking pixels: only those can be floor.</summary>
        void Collect(InverseDepthImage img, out float dMin, out float dMax)
        {
            _d.Clear();
            _den.Clear();
            dMin = float.PositiveInfinity;
            dMax = float.NegativeInfinity;
            float minSin = Mathf.Sin(_cfg.selfAlignMinDescentDeg * Mathf.Deg2Rad);
            int stride = Mathf.Max(1, _cfg.selfAlignStride);
            RectInt c = img.content;
            for (int v = c.yMin + stride / 2; v < c.yMax; v += stride)
            for (int u = c.xMin + stride / 2; u < c.xMax; u += stride)
            {
                float d = img.values[v * img.width + u];
                if (float.IsNaN(d) || float.IsInfinity(d)) continue;
                // AR Foundation session space is gravity-aligned with +Y up.
                Vector3 ray = img.WorldRay(u, v);
                if (-ray.y < minSin * ray.magnitude) continue;
                _d.Add(d);
                _den.Add(ray.y);
                if (d < dMin) dMin = d;
                if (d > dMax) dMax = d;
            }
            if (_y.Length < _d.Count)
            {
                _y = new float[_d.Count];
                _work = new float[_d.Count];
            }
        }

        /// <summary>Heights relative to the camera for shift r; returns how many were valid.</summary>
        int Heights(float r)
        {
            int m = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                float shifted = _d[i] + r;
                if (shifted <= 0f) continue;
                _y[m++] = _den[i] / shifted;
            }
            return m;
        }

        /// <summary>Floor inlier count for shift r. Independent of the unknown scale: the band is relative.</summary>
        int Score(float r, out float y0)
        {
            y0 = float.NaN;
            int m = Heights(r);
            if (m < 3) return 0;
            System.Array.Copy(_y, _work, m);
            // Seed: the median of the lowest 60% (the 30th percentile). The floor is the lowest
            // large surface, so it dominates there even when walls fill most of the view.
            y0 = Selection.Kth(_work, m, (int)(0.3f * (m - 1)));
            if (!(y0 < 0f)) return 0;
            int n = Inliers(m, y0, _work);
            if (n == 0) return 0;
            // Re-centre on the inliers so the seed's bias toward low outliers does not cost inliers.
            y0 = Selection.Kth(_work, n, n / 2);
            if (!(y0 < 0f)) return 0;
            return Inliers(m, y0, null);
        }

        int Inliers(int m, float y0, float[] into)
        {
            float band = _cfg.selfAlignHeightTolerance * -y0;
            int n = 0;
            for (int i = 0; i < m; i++)
            {
                if (Mathf.Abs(_y[i] - y0) >= band) continue;
                if (into != null) into[n] = _y[i];
                n++;
            }
            return n;
        }

        /// <summary>Final statistics at the chosen shift, for the gates and the CSV.</summary>
        void Describe(float r, SelfAlignment res)
        {
            res.inliers = Score(r, out float y0);
            res.r = r;
            res.hRel = -y0;
            if (res.inliers == 0) return;
            float band = _cfg.selfAlignHeightTolerance * -y0;

            int n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                float shifted = _d[i] + r;
                if (shifted <= 0f) continue;
                float y = _den[i] / shifted;
                if (Mathf.Abs(y - y0) < band) _work[n++] = Mathf.Abs(y - y0) / -y0;
            }
            res.residual = Selection.Kth(_work, n, n / 2);

            // Identifiability: a floor seen only over a narrow depth band (close-up of a wall)
            // is flat for a whole range of shifts, so the chosen one means nothing. Percentiles,
            // not min/max, so a few stray far inliers cannot fake a wide band.
            n = 0;
            for (int i = 0; i < _d.Count; i++)
            {
                float shifted = _d[i] + r;
                if (shifted <= 0f) continue;
                if (Mathf.Abs(_den[i] / shifted - y0) < band) _work[n++] = 1f / shifted;
            }
            float near = Selection.Kth(_work, n, (int)(0.05f * (n - 1)));
            float far = Selection.Kth(_work, n, (int)(0.95f * (n - 1)));
            res.depthRatio = near > 0f ? far / near : float.NaN;
        }
    }
}
```

- [ ] **Step 6: Recompile, run all tests**

Expected: every `FloorSelfAlignerTests` test passes and the previous 223 still pass. If `WallCloseUp_IsRejected` passes for the wrong reason, that is acceptable (any rejection is the requirement); log `a.reason` in the assertion message as written. If a depth-error test fails, do **not** loosen the bound first: print `a.r`, `a.hRel`, `a.depthRatio` against the truth (`r = shift/scale`) and find the cause.

- [ ] **Step 7: Commit (only if Rachit asks)**

```bash
git add Assets/WallDistance/Runtime/Core/Selection.cs* Assets/WallDistance/Runtime/Core/FloorSelfAligner.cs* Assets/WallDistance/Runtime/Core/DetectionConfig.cs Assets/WallDistance/Tests/EditMode/FloorSelfAlignerTests.cs*
git commit -m "Find the inverse-depth shift from floor flatness"
```

---

### Task 2: `HeightClues` (one camera-height clue per frame)

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/HeightClues.cs`
- Test: `Assets/WallDistance/Tests/EditMode/HeightCluesTests.cs`

**Interfaces:**
- Consumes: `SelfAlignment` (`success`, `r`, `hRel`), `MetricSample` (`pixel`, `depthMeters`), `FloorPlane` (`PlausibleCameraHeight`, `HeightAbove`, `MinCameraHeight`, `MaxCameraHeight`), `Selection.Kth`.
- Produces:
  - `public enum HeightClueSource { ArPlane, RawDepth }`
  - `public struct HeightClue { double time; float heightMeters; float floorY; float weight; HeightClueSource source; }`
  - `public sealed class HeightClues { HeightClues(DetectionConfig); bool TryFromPlane(FloorPlane floor, Vector3 camera, double now, out HeightClue clue); bool TryFromDepth(InverseDepthImage img, SelfAlignment a, IReadOnlyList<MetricSample> samples, double now, out HeightClue clue, out int used); }`

- [ ] **Step 1: Write the failing tests**

Create `Assets/WallDistance/Tests/EditMode/HeightCluesTests.cs`:

```csharp
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class HeightCluesTests
    {
        /// <summary>Sparse, true-depth samples at random content pixels: what confident raw depth gives.</summary>
        internal static List<MetricSample> Samples(SyntheticCorridor.Rendered r, int count, System.Random rng, float depthFactor = 1f)
        {
            var samples = new List<MetricSample>();
            int size = r.image.width;
            RectInt c = r.image.content;
            while (samples.Count < count)
            {
                int u = rng.Next(c.xMin, c.xMax), v = rng.Next(c.yMin, c.yMax);
                float z = r.depth[v * size + u];
                if (!float.IsNaN(z)) samples.Add(new MetricSample { pixel = new Vector2(u, v), depthMeters = z * depthFactor });
            }
            return samples;
        }

        static (SyntheticCorridor.Rendered r, SelfAlignment a) Aligned(float cameraHeight = 1.4f)
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(h: cameraHeight));
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f, relativeNoise: 0.01f, seed: 2);
            var a = new FloorSelfAligner(new DetectionConfig()).Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            return (r, a);
        }

        [Test]
        public void RawDepthSamples_GiveCameraHeight_Within2Percent()
        {
            var (r, a) = Aligned();
            var clues = new HeightClues(new DetectionConfig());
            Assert.IsTrue(clues.TryFromDepth(r.image, a, Samples(r, 40, new System.Random(1)), 3.0, out var c, out int used));
            Assert.AreEqual(40, used);
            Assert.AreEqual(1.4f, c.heightMeters, 0.028f);
            Assert.AreEqual(0f, c.floorY, 0.028f, "floor at y = 0 in the synthetic corridor");
            Assert.AreEqual(40f, c.weight);
            Assert.AreEqual(3.0, c.time);
            Assert.AreEqual(HeightClueSource.RawDepth, c.source);
        }

        [Test]
        public void DisagreeingSamples_GiveNoClue()
        {
            // Half the samples 50% too deep (a shiny door read through its reflection, say):
            // the frame cannot say which half is right, so it says nothing.
            var (r, a) = Aligned();
            var rng = new System.Random(2);
            var samples = Samples(r, 20, rng);
            samples.AddRange(Samples(r, 20, rng, depthFactor: 1.5f));
            Assert.IsFalse(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, a, samples, 1.0, out _, out _));
        }

        [Test]
        public void TooFewSamples_GiveNoClue_ButReportCount()
        {
            var (r, a) = Aligned();
            Assert.IsFalse(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, a, Samples(r, 5, new System.Random(3)), 1.0, out _, out int used));
            Assert.AreEqual(5, used);
        }

        [Test]
        public void LetterboxSamples_AreSkipped()
        {
            var (r, a) = Aligned();
            var samples = Samples(r, 12, new System.Random(4));
            // x = 10 is in the letterbox (content starts at 65): the network has no real depth there.
            for (int i = 0; i < 8; i++) samples.Add(new MetricSample { pixel = new Vector2(10, 100 + i), depthMeters = 2f });
            Assert.IsTrue(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, a, samples, 1.0, out _, out int used));
            Assert.AreEqual(12, used);
        }

        [Test]
        public void ImplausibleHeight_GivesNoClue()
        {
            // 0.5 m is below the handheld band (FloorPlane.MinCameraHeight = 0.8 m).
            var (r, a) = Aligned(cameraHeight: 0.5f);
            Assert.IsFalse(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, a, Samples(r, 40, new System.Random(5)), 1.0, out _, out _));
        }

        [Test]
        public void FailedSelfAlignment_GivesNoClue()
        {
            var (r, _) = Aligned();
            var failed = new SelfAlignment();
            Assert.IsFalse(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, failed, Samples(r, 40, new System.Random(6)), 1.0, out _, out _));
        }

        [Test]
        public void ArFloor_GivesFullWeightClue_TableDoesNot()
        {
            var clues = new HeightClues(new DetectionConfig());
            var camera = new Vector3(0.3f, 1.4f, 2f);
            Assert.IsTrue(clues.TryFromPlane(new FloorPlane(Vector3.zero, Vector3.up), camera, 2.0, out var c));
            Assert.AreEqual(1.4f, c.heightMeters, 1e-5f);
            Assert.AreEqual(0f, c.floorY, 1e-5f);
            Assert.AreEqual(200f, c.weight);
            Assert.AreEqual(HeightClueSource.ArPlane, c.source);
            // A table 0.9 m up leaves the camera 0.5 m above it: not a floor.
            Assert.IsFalse(clues.TryFromPlane(new FloorPlane(new Vector3(0f, 0.9f, 0f), Vector3.up), camera, 2.0, out _));
        }
    }
}
```

- [ ] **Step 2: Recompile and confirm it fails** (`HeightClues` not found).

- [ ] **Step 3: Write `HeightClues.cs`**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    public enum HeightClueSource { ArPlane, RawDepth }

    /// <summary>
    /// One frame's evidence for how high the camera is above the floor. Stored with the floor's
    /// world height (camera.y − height) so evidence from different hand heights can be pooled.
    /// </summary>
    public struct HeightClue
    {
        public double time;
        public float heightMeters;
        public float floorY;
        public float weight;
        public HeightClueSource source;
    }

    /// <summary>
    /// Turns what ARCore offers into camera-height clues (floor-free design §4.2).
    ///
    /// Confident raw depth on glossy corridors is sparse (median 35 pixels per frame in run
    /// 150956) but metric. Once self-alignment fixed the shape of the depth map, each such pixel
    /// votes for one number, the camera height, so even a handful per frame is useful. The
    /// per-frame fallback that needed 200 at once threw all of them away.
    /// </summary>
    public sealed class HeightClues
    {
        readonly DetectionConfig _cfg;
        float[] _h = new float[256];

        public HeightClues(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

        /// <summary>An ARCore floor is direct evidence; implausible planes (tables, sills) are not floors.</summary>
        public bool TryFromPlane(FloorPlane floor, Vector3 camera, double now, out HeightClue clue)
        {
            clue = default;
            if (!floor.PlausibleCameraHeight(camera)) return false;
            float h = floor.HeightAbove(camera);
            clue = new HeightClue
            {
                time = now,
                heightMeters = h,
                floorY = camera.y - h,
                weight = _cfg.heightPlaneWeight,
                source = HeightClueSource.ArPlane,
            };
            return true;
        }

        /// <summary>
        /// Median camera height voted by confident raw-depth samples. <paramref name="used"/> counts
        /// the samples that landed on valid network pixels, even when no clue results (CSV diagnostic).
        /// </summary>
        public bool TryFromDepth(InverseDepthImage img, SelfAlignment a, IReadOnlyList<MetricSample> samples,
            double now, out HeightClue clue, out int used)
        {
            clue = default;
            used = 0;
            if (img == null || a == null || !a.success || samples == null) return false;
            if (_h.Length < samples.Count) _h = new float[samples.Count];

            int n = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                int u = Mathf.RoundToInt(s.pixel.x), v = Mathf.RoundToInt(s.pixel.y);
                // Letterbox pixels hold no camera content, so the network's value there means nothing.
                if (!img.InContent(u, v) || !(s.depthMeters > 0f)) continue;
                float d = img.values[v * img.width + u];
                float shifted = d + a.r;
                if (float.IsNaN(d) || !(shifted > 0f)) continue;
                // z = k / (d + r) and H = k · hRel, so this sample votes H = z · (d + r) · hRel.
                _h[n++] = s.depthMeters * shifted * a.hRel;
            }
            used = n;
            if (n < _cfg.minClueSamples) return false;

            float median = Selection.Kth(_h, n, n / 2);
            for (int i = 0; i < n; i++) _h[i] = Mathf.Abs(_h[i] - median);
            float mad = Selection.Kth(_h, n, n / 2);
            // Samples that disagree (reflections, depth edges) make the median a coin toss
            // between surfaces; better no clue than a confident wrong one.
            if (!(median > 0f) || mad > _cfg.maxClueSpread * median) return false;
            if (median < FloorPlane.MinCameraHeight || median > FloorPlane.MaxCameraHeight) return false;

            float cameraY = img.cameraPose.position.y;
            clue = new HeightClue
            {
                time = now,
                heightMeters = median,
                floorY = cameraY - median,
                weight = Mathf.Min(n, _cfg.maxClueWeight),
                source = HeightClueSource.RawDepth,
            };
            return true;
        }
    }
}
```

- [ ] **Step 4: Recompile, run all tests.** Expected: all `HeightCluesTests` pass, nothing else regresses.

- [ ] **Step 5: Commit (only if Rachit asks)**

```bash
git add Assets/WallDistance/Runtime/Core/HeightClues.cs* Assets/WallDistance/Tests/EditMode/HeightCluesTests.cs*
git commit -m "Derive camera-height clues from ARCore floors and sparse raw depth"
```

---

### Task 3: `CameraHeightEstimator` (pooling, hold, carry-over)

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/CameraHeightEstimator.cs`
- Modify: `Assets/WallDistance/Runtime/Core/WallReading.cs` (append `FailureReason.Calibrating`; `Explain` returns it)
- Test: `Assets/WallDistance/Tests/EditMode/CameraHeightEstimatorTests.cs`

**Interfaces:**
- Consumes: `HeightClue`, `DetectionConfig` height fields, `FloorPlane`.
- Produces:
  - `FailureReason.Calibrating` (value 12, appended after `NoWallOnSide`).
  - `public sealed class CameraHeightEstimator { CameraHeightEstimator(DetectionConfig); void Add(in HeightClue c); void NoteActivity(double now); bool TryFloor(Vector3 camera, double now, out FloorPlane floor); void MarkDiscontinuity(); bool IsCalibrating(double now); FailureReason Explain(FailureReason detection, double now); void Reset(); bool Ready; float FloorY, HeightMeters, SpreadMeters, WindowWeight; int WindowClues; }`

- [ ] **Step 1: Append the failure reason** in `WallReading.cs`, after `NoWallOnSide,`:

```csharp
        /// <summary>
        /// No ARCore floor, and the camera height is still being learned from sparse depth clues
        /// (floor-free design §4.3). Distinct from NoFloor because the user's action differs:
        /// keep walking, not "point at the floor".
        /// </summary>
        Calibrating,
```

- [ ] **Step 2: Write the failing tests**

Create `Assets/WallDistance/Tests/EditMode/CameraHeightEstimatorTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class CameraHeightEstimatorTests
    {
        static readonly Vector3 Camera14 = new Vector3(0.2f, 1.4f, 0f);

        static HeightClue Clue(double t, float floorY, float weight = 40f, float cameraY = 1.4f) => new HeightClue
        {
            time = t, floorY = floorY, heightMeters = cameraY - floorY, weight = weight, source = HeightClueSource.RawDepth,
        };

        /// <summary>Clues every 0.1 s over [from, to] with the floor at floorY; TryFloor after each, as the resolver does.</summary>
        static bool Feed(CameraHeightEstimator e, double from, double to, float floorY, out FloorPlane floor)
        {
            floor = default;
            bool ok = false;
            // Integer steps: summing 0.1 twenty times gives 1.9999999999999996, which would miss
            // the 2 s span gate by float roundoff and fail for the wrong reason.
            for (int k = 0; from + k * 0.1 <= to + 1e-9; k++)
            {
                double t = from + k * 0.1;
                e.Add(Clue(t, floorY));
                ok = e.TryFloor(Camera14, t, out floor);
            }
            return ok;
        }

        [Test]
        public void NotReadyBeforeTwoSeconds_IsCalibrating()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsFalse(Feed(e, 0.0, 1.5, 0f, out _));
            Assert.IsFalse(e.Ready);
            Assert.IsTrue(e.IsCalibrating(1.5));
            Assert.AreEqual(FailureReason.Calibrating, e.Explain(FailureReason.NoFloor, 1.5));
            Assert.AreEqual(FailureReason.AlignmentFailed, e.Explain(FailureReason.AlignmentFailed, 1.5), "only NoFloor is refined");
        }

        [Test]
        public void ReadyAfterTwoSeconds_FloorUnderCamera()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0.01f, out var floor));
            Assert.AreEqual(1.39f, floor.HeightAbove(Camera14), 1e-4f);
            Assert.AreEqual(Vector3.up, floor.up);
            Assert.AreEqual(1.39f, e.HeightMeters, 1e-4f);
            Assert.AreEqual(FailureReason.NoFloor, e.Explain(FailureReason.NoFloor, 2.0), "ready: nothing to refine");
        }

        [Test]
        public void NoActivity_IsNoFloorNotCalibrating()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsFalse(e.IsCalibrating(10.0));
            Assert.AreEqual(FailureReason.NoFloor, e.Explain(FailureReason.NoFloor, 10.0));
            e.NoteActivity(9.0);
            Assert.AreEqual(FailureReason.Calibrating, e.Explain(FailureReason.NoFloor, 10.0));
        }

        [Test]
        public void WeightedMedian_RejectsThirtyPercentOutliers()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            FloorPlane floor = default;
            bool ok = false;
            for (int k = 0; k <= 25; k++)
            {
                double t = k * 0.1;
                e.Add(Clue(t, k % 10 < 3 ? 0.5f : 0.005f * (k % 3)));
                ok = e.TryFloor(Camera14, t, out floor);
            }
            Assert.IsTrue(ok);
            Assert.AreEqual(1.4f, floor.HeightAbove(Camera14), 0.02f);
        }

        [Test]
        public void DisagreeingEvidence_IsNotHeld()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0f, out _));
            // Then a full window of evidence smeared evenly over 0-0.4 m: no height is better
            // supported than its neighbours, so recalibrate rather than hold. (A 50/50 split
            // between two floors would NOT test this: a weighted median lands on one mode and the
            // MAD is 0, the same robustness that rejects a 30% minority in the test above.)
            bool ok = true;
            for (int k = 21; k <= 75; k++)
            {
                double t = k * 0.1;
                e.Add(Clue(t, 0.4f * (k % 9) / 8f));
                ok = e.TryFloor(Camera14, t, out _);
            }
            Assert.IsFalse(ok);
            Assert.Greater(e.SpreadMeters, 0.07f);
        }

        [Test]
        public void RaisingThePhone_KeepsTheFloor()
        {
            // Clues are stored as the floor's world height, so a hand moving up 0.3 m (tracked
            // by ARCore) moves the camera, not the floor.
            var e = new CameraHeightEstimator(new DetectionConfig());
            for (int k = 0; k <= 25; k++)
            {
                float cameraY = 1.4f + 0.3f * k / 25f;
                e.Add(Clue(k * 0.1, 0f, cameraY: cameraY));
                e.TryFloor(new Vector3(0f, cameraY, 0f), k * 0.1, out _);
            }
            Assert.IsTrue(e.TryFloor(new Vector3(0f, 1.7f, 0f), 2.5, out var floor));
            Assert.AreEqual(1.7f, floor.HeightAbove(new Vector3(0f, 1.7f, 0f)), 1e-4f);
        }

        [Test]
        public void Hold_KeepsEstimateForTwentySeconds_ThenExpires()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0f, out _));
            Assert.IsTrue(e.TryFloor(Camera14, 10.0, out var held), "no clues for 8 s: held");
            Assert.AreEqual(1.4f, held.HeightAbove(Camera14), 1e-4f);
            Assert.IsFalse(e.TryFloor(Camera14, 22.5, out _), "20.5 s since the last good estimate");
        }

        [Test]
        public void Discontinuity_CarriesCameraHeight()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0f, out _));
            e.MarkDiscontinuity();
            Assert.AreEqual(0, e.WindowClues, "clues from the old world frame are dropped");
            // ARCore's world moved: the same physical camera is now at y = 5.
            var jumped = new Vector3(3f, 5f, -1f);
            Assert.IsTrue(e.TryFloor(jumped, 2.5, out var floor), "no calibrating gap after a jump");
            Assert.AreEqual(1.4f, floor.HeightAbove(jumped), 1e-4f);
            Assert.AreEqual(3.6f, e.FloorY, 1e-4f);
        }

        [Test]
        public void Discontinuity_BeforeReady_CarriesNothing()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Feed(e, 0.0, 1.0, 0f, out _);
            e.MarkDiscontinuity();
            Assert.IsFalse(e.TryFloor(new Vector3(0f, 5f, 0f), 1.5, out _));
        }

        [Test]
        public void Reset_ForgetsEverything()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0f, out _));
            e.Reset();
            Assert.IsFalse(e.TryFloor(Camera14, 2.1, out _));
            Assert.IsFalse(e.IsCalibrating(2.1));
            Assert.AreEqual(0, e.WindowClues);
        }
    }
}
```

- [ ] **Step 3: Recompile and confirm it fails** (`CameraHeightEstimator` not found).

- [ ] **Step 4: Write `CameraHeightEstimator.cs`**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Learns where the floor is from a few seconds of camera-height clues (floor-free design
    /// §4.3) and hands out a FloorPlane once the clues agree. Never assumes a height: until the
    /// evidence is in, it has no floor to give.
    ///
    /// The floor is stored as a world height, not as the camera height. ARCore's short-term
    /// vertical motion is metric, so a hand moving up or down moves the camera while the floor
    /// stays put. Over longer spans ARCore drifts and jumps (run 150956: steps of −0.5, −0.8,
    /// +0.46 and −1.46 m), which is why clues expire after a short window and why a jump carries
    /// over the camera's height above the floor instead of the floor's world height.
    /// </summary>
    public sealed class CameraHeightEstimator
    {
        readonly DetectionConfig _cfg;
        readonly List<HeightClue> _clues = new List<HeightClue>(128);
        float[] _v = new float[128];
        float[] _w = new float[128];

        bool _hasHeld;
        float _heldFloorY;
        double _heldAt;
        bool _carryPending;
        float _carryHeight;
        float _lastCameraY = float.NaN;
        double _lastActivity = double.NegativeInfinity;

        public bool Ready { get; private set; }
        /// <summary>World height of the floor in use; NaN when not ready.</summary>
        public float FloorY { get; private set; } = float.NaN;
        /// <summary>Camera height above that floor at the last <see cref="TryFloor"/>; NaN when not ready.</summary>
        public float HeightMeters { get; private set; } = float.NaN;
        /// <summary>Weighted median absolute deviation of the window's floor heights; NaN with no clues.</summary>
        public float SpreadMeters { get; private set; } = float.NaN;
        public float WindowWeight { get; private set; }
        public int WindowClues { get; private set; }

        public CameraHeightEstimator(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

        public void Add(in HeightClue c)
        {
            _clues.Add(c);
            if (c.time > _lastActivity) _lastActivity = c.time;
        }

        /// <summary>A frame self-aligned but gave no clue: still progress toward a floor, so "calibrating", not "no floor".</summary>
        public void NoteActivity(double now)
        {
            if (now > _lastActivity) _lastActivity = now;
        }

        /// <summary>
        /// ARCore's world frame may have moved (MapContinuityGuard cleared the map). Keep only the
        /// camera's height above the floor, measured in the old frame, and re-place the floor
        /// under the camera on the next <see cref="TryFloor"/>. Without this, each of run 150956's
        /// ten jumps would cost seconds of "calibrating".
        /// </summary>
        public void MarkDiscontinuity()
        {
            if (_hasHeld && !float.IsNaN(_lastCameraY) && !_carryPending)
            {
                _carryHeight = _lastCameraY - _heldFloorY;
                _carryPending = true;
            }
            _clues.Clear();
            _hasHeld = false;
            Ready = false;
            WindowClues = 0;
            WindowWeight = 0f;
        }

        public bool TryFloor(Vector3 camera, double now, out FloorPlane floor)
        {
            Prune(now);
            if (_carryPending)
            {
                _carryPending = false;
                _hasHeld = true;
                _heldFloorY = camera.y - _carryHeight;
                _heldAt = now;
            }
            _lastCameraY = camera.y;

            Evaluate(out bool enough, out float estimate, out float spread);
            SpreadMeters = spread;
            bool ok;
            if (enough && spread <= _cfg.maxHeightSpreadMeters)
            {
                _hasHeld = true;
                _heldFloorY = estimate;
                _heldAt = now;
                ok = true;
            }
            else if (!enough && _hasHeld && now - _heldAt <= _cfg.heightHoldSeconds)
            {
                // Too little new evidence (floor out of view, no confident depth): keep the last
                // good floor. Enough evidence that DISAGREES falls through to "not ready" instead.
                ok = true;
            }
            else ok = false;

            Ready = ok;
            if (!ok)
            {
                floor = default;
                FloorY = HeightMeters = float.NaN;
                return false;
            }
            FloorY = _heldFloorY;
            HeightMeters = camera.y - _heldFloorY;
            floor = new FloorPlane(new Vector3(camera.x, _heldFloorY, camera.z), Vector3.up);
            return true;
        }

        public bool IsCalibrating(double now) => !Ready && now - _lastActivity <= _cfg.heightWindowSeconds;

        /// <summary>Refines the pipeline's NoFloor into Calibrating while clues are being gathered.</summary>
        public FailureReason Explain(FailureReason detection, double now) =>
            detection == FailureReason.NoFloor && IsCalibrating(now) ? FailureReason.Calibrating : detection;

        public void Reset()
        {
            _clues.Clear();
            _hasHeld = _carryPending = false;
            _lastCameraY = float.NaN;
            _lastActivity = double.NegativeInfinity;
            Ready = false;
            FloorY = HeightMeters = SpreadMeters = float.NaN;
            WindowWeight = 0f;
            WindowClues = 0;
        }

        void Prune(double now)
        {
            // In-place compaction: RemoveAll with a lambda would allocate a closure per frame.
            int keep = 0;
            for (int i = 0; i < _clues.Count; i++)
                if (now - _clues[i].time <= _cfg.heightWindowSeconds) _clues[keep++] = _clues[i];
            _clues.RemoveRange(keep, _clues.Count - keep);
        }

        void Evaluate(out bool enough, out float estimate, out float spread)
        {
            int n = _clues.Count;
            if (_v.Length < n)
            {
                _v = new float[n * 2];
                _w = new float[n * 2];
            }
            float total = 0f;
            double first = double.PositiveInfinity, last = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var c = _clues[i];
                _v[i] = c.floorY;
                _w[i] = c.weight;
                total += c.weight;
                if (c.time < first) first = c.time;
                if (c.time > last) last = c.time;
            }
            WindowWeight = total;
            WindowClues = n;
            enough = n >= _cfg.minHeightClues && total >= _cfg.minHeightWeight && last - first >= _cfg.minHeightSpanSeconds;
            if (n == 0)
            {
                estimate = spread = float.NaN;
                return;
            }
            // Weighted median, not mean: a burst of biased clues (a reflective door) must not
            // drag the floor. The spread uses the same statistic on the deviations.
            estimate = WeightedMedian(n, total);
            for (int i = 0; i < n; i++) _v[i] = Mathf.Abs(_v[i] - estimate);
            spread = WeightedMedian(n, total);
        }

        /// <summary>Sorts _v[0..n) with _w alongside and returns the value at half the total weight.</summary>
        float WeightedMedian(int n, float total)
        {
            System.Array.Sort(_v, _w, 0, n);
            float half = 0.5f * total, acc = 0f;
            for (int i = 0; i < n; i++)
            {
                acc += _w[i];
                if (acc >= half) return _v[i];
            }
            return _v[n - 1];
        }
    }
}
```

- [ ] **Step 5: Extend `ApiCompatibilityTests`**: after `Assert.AreEqual(11, (int)FailureReason.NoWallOnSide);` add

```csharp
            Assert.AreEqual(12, (int)FailureReason.Calibrating);
```

- [ ] **Step 6: Recompile, run all tests.** Expected: all `CameraHeightEstimatorTests` pass; the ApiCompatibility test passes.

- [ ] **Step 7: Commit (only if Rachit asks)**

```bash
git add Assets/WallDistance/Runtime/Core/CameraHeightEstimator.cs* Assets/WallDistance/Runtime/Core/WallReading.cs Assets/WallDistance/Tests/EditMode/CameraHeightEstimatorTests.cs* Assets/WallDistance/Tests/EditMode/ApiCompatibilityTests.cs
git commit -m "Pool camera-height clues into a held floor estimate"
```

---

### Task 4: `FloorResolver` + "calibrating" readings, end to end in Core

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/FloorResolver.cs`
- Modify: `Assets/WallDistance/Runtime/Core/WallMeasurementEngine.cs:131` (`Substitute` also passes `Calibrating`)
- Modify: `Assets/WallDistance/Runtime/Core/ReadingText.cs:40` (text for `Calibrating`)
- Modify: `Assets/WallDistance/Tests/EditMode/ReadingTextTests.cs`, `Assets/WallDistance/Tests/EditMode/LearnedEngineTests.cs`
- Test: `Assets/WallDistance/Tests/EditMode/FloorResolverTests.cs`

**Interfaces:**
- Consumes: `FloorSelfAligner`, `HeightClues`, `CameraHeightEstimator`, `WallDetectionPipeline.ProcessFrame(string, InverseDepthImage, FloorPlane, IReadOnlyList<MetricSample>, double)`, `HeightCluesTests.Samples(...)` (internal test helper from Task 2).
- Produces:
  - `public enum FloorSourceKind { None, ArPlane, Derived }`
  - `public sealed class FloorResolver { FloorResolver(DetectionConfig); FloorPlane Resolve(InverseDepthImage img, FloorPlane arFloor, IReadOnlyList<MetricSample> samples, double now); void MarkDiscontinuity(); void Reset(); CameraHeightEstimator Estimator { get; } FloorSourceKind LastSource; bool LastSelfAlignOk; double LastSelfAlignMs; int LastClueSamples; }`

- [ ] **Step 1: Write the failing tests**

Create `Assets/WallDistance/Tests/EditMode/FloorResolverTests.cs`:

```csharp
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class FloorResolverTests
    {
        [Test]
        public void ArFloor_Wins_AndWarmsTheEstimator()
        {
            var resolver = new FloorResolver(new DetectionConfig());
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var img = SyntheticCorridor.ToNetworkOutput(r);
            var floor = resolver.Resolve(img, SyntheticCorridor.Floor, null, 1.0);
            Assert.AreEqual(SyntheticCorridor.Floor.point, floor.point);
            Assert.AreEqual(FloorSourceKind.ArPlane, resolver.LastSource);
            Assert.AreEqual(200f, resolver.Estimator.WindowWeight, "the ARCore floor is also a clue");
            Assert.IsFalse(resolver.LastSelfAlignOk, "self-alignment is skipped when ARCore has the floor");
        }

        [Test]
        public void NoArFloor_CorridorWalk_DerivedFloorThenWalls()
        {
            // The run-150956 situation: no ARCore floor at any point, ~40 confident raw-depth
            // pixels per frame (median there: 35), walking down the corridor at 1 m/s, 10 Hz.
            var cfg = new MeasurementConfig();
            var resolver = new FloorResolver(cfg.detection);
            var pipe = new WallDetectionPipeline(cfg);
            var rng = new System.Random(11);
            FloorPlane floor = default;
            Pose pose = default;
            for (int k = 0; k <= 25; k++)
            {
                double t = k * 0.1;
                pose = SyntheticCorridor.Camera(z: -2.5f + 0.1f * k);
                var r = new SyntheticCorridor().Render(pose);
                var img = SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f, relativeNoise: 0.01f, seed: k + 1);
                img.timestamp = t;
                var samples = HeightCluesTests.Samples(r, 40, rng);
                floor = resolver.Resolve(img, default, samples, t);
                pipe.ProcessFrame("s1", img, floor, samples, t);
                if (k == 10)
                {
                    Assert.AreEqual(FloorSourceKind.None, resolver.LastSource, "1 s in: still calibrating");
                    Assert.AreEqual(FailureReason.Calibrating, resolver.Estimator.Explain(pipe.DetectionFailure(t, true), t));
                }
            }
            Assert.AreEqual(FloorSourceKind.Derived, resolver.LastSource);
            Assert.IsTrue(resolver.LastSelfAlignOk);
            Assert.AreEqual(40, resolver.LastClueSamples);
            Assert.AreEqual(1.4f, floor.HeightAbove(pose.position), 0.03f);

            // Side walls within 5% (spec §6): right at 0.8 m, left at 1.2 m from x = 0.2.
            bool right = false, left = false;
            foreach (var tr in pipe.Map.Tracks)
            {
                float dist = Mathf.Abs(tr.SignedDistance(pose.position));
                if (Vector3.Angle(tr.normal, Vector3.left) < 3f && Mathf.Abs(dist - 0.8f) <= 0.04f) right = true;
                if (Vector3.Angle(tr.normal, Vector3.right) < 3f && Mathf.Abs(dist - 1.2f) <= 0.06f) left = true;
            }
            Assert.IsTrue(right, "right wall at 0.8 m ± 5%");
            Assert.IsTrue(left, "left wall at 1.2 m ± 5%");
        }

        [Test]
        public void MarkDiscontinuity_ReachesTheEstimator()
        {
            var resolver = new FloorResolver(new DetectionConfig());
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
            for (int k = 0; k <= 20; k++) resolver.Resolve(img, SyntheticCorridor.Floor, null, k * 0.1);
            resolver.MarkDiscontinuity();
            Assert.AreEqual(0, resolver.Estimator.WindowClues);
        }

        [Test]
        public void Reset_ForgetsTheFloor()
        {
            var resolver = new FloorResolver(new DetectionConfig());
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
            for (int k = 0; k <= 20; k++) resolver.Resolve(img, SyntheticCorridor.Floor, null, k * 0.1);
            resolver.Reset();
            Assert.AreEqual(FloorSourceKind.None, resolver.LastSource);
            Assert.AreEqual(0, resolver.Estimator.WindowClues);
        }
    }
}
```

Add to `ReadingTextTests.cs` (next to the NoFloor assertion at line 34, inside the same test):

```csharp
            Assert.AreEqual("calibrating — keep walking", ReadingText.Failure(FailureReason.Calibrating));
```

Add to `LearnedEngineTests.cs` after `DetectionFailure_ReplacesGenericNoWallReasons`:

```csharp
        [Test]
        public void Calibrating_ReplacesGenericNoWallReasons()
        {
            var e = new WallMeasurementEngine(new MeasurementConfig());
            var s = e.Update(In(new WallCandidate[0], 1.0, new List<WallTrack>(), detection: FailureReason.Calibrating));
            Assert.AreEqual(FailureReason.Calibrating, s.aimed.failure);
            Assert.AreEqual(FailureReason.Calibrating, s.nearest.failure);
            Assert.AreEqual(FailureReason.Calibrating, s.left.failure);
            Assert.AreEqual(FailureReason.Calibrating, s.right.failure);
        }
```

- [ ] **Step 2: Recompile and confirm it fails** (`FloorResolver` not found).

- [ ] **Step 3: Write `FloorResolver.cs`**

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    public enum FloorSourceKind { None, ArPlane, Derived }

    /// <summary>
    /// Chooses the floor each inference frame is scaled against (floor-free design §4.4):
    /// ARCore's floor when it has one, otherwise a floor derived from the depth network's own
    /// flatness plus a pooled camera height. Either way the pipeline receives an ordinary
    /// FloorPlane, so nothing downstream changes. Lives in Core so the whole decision is
    /// testable without AR Foundation.
    /// </summary>
    public sealed class FloorResolver
    {
        readonly FloorSelfAligner _aligner;
        readonly HeightClues _clues;

        public CameraHeightEstimator Estimator { get; }
        public FloorSourceKind LastSource { get; private set; } = FloorSourceKind.None;
        public bool LastSelfAlignOk { get; private set; }
        public double LastSelfAlignMs { get; private set; } = double.NaN;
        /// <summary>Raw-depth samples that landed on valid network pixels in the last frame; -1 when not collected.</summary>
        public int LastClueSamples { get; private set; } = -1;

        public FloorResolver(DetectionConfig cfg)
        {
            _aligner = new FloorSelfAligner(cfg);
            _clues = new HeightClues(cfg);
            Estimator = new CameraHeightEstimator(cfg);
        }

        public FloorPlane Resolve(InverseDepthImage img, FloorPlane arFloor, IReadOnlyList<MetricSample> samples, double now)
        {
            LastSelfAlignOk = false;
            LastSelfAlignMs = double.NaN;
            LastClueSamples = -1;
            Vector3 camera = img.cameraPose.position;

            if (arFloor.IsValid)
            {
                // ARCore's floor is measured, so it wins outright. It also feeds the estimator, so
                // a derived floor is ready the moment the plane disappears (ARCore floors flicker).
                if (_clues.TryFromPlane(arFloor, camera, now, out var planeClue)) Estimator.Add(planeClue);
                Estimator.TryFloor(camera, now, out _);
                LastSource = FloorSourceKind.ArPlane;
                return arFloor;
            }

            var a = _aligner.Align(img);
            LastSelfAlignOk = a.success;
            LastSelfAlignMs = a.milliseconds;
            if (a.success)
            {
                Estimator.NoteActivity(now);
                if (_clues.TryFromDepth(img, a, samples, now, out var depthClue, out int used)) Estimator.Add(depthClue);
                LastClueSamples = used;
            }

            if (Estimator.TryFloor(camera, now, out var floor))
            {
                LastSource = FloorSourceKind.Derived;
                return floor;
            }
            LastSource = FloorSourceKind.None;
            return default;
        }

        public void MarkDiscontinuity() => Estimator.MarkDiscontinuity();

        public void Reset()
        {
            Estimator.Reset();
            LastSource = FloorSourceKind.None;
            LastSelfAlignOk = false;
            LastSelfAlignMs = double.NaN;
            LastClueSamples = -1;
        }
    }
}
```

- [ ] **Step 4: Readings show "calibrating"**

In `WallMeasurementEngine.Substitute`, replace the detection filter line with:

```csharp
            if (detection != FailureReason.NoFloor && detection != FailureReason.AlignmentFailed
                && detection != FailureReason.InferenceStale && detection != FailureReason.Calibrating) return;
```

In `ReadingText.Failure`, after the `NoFloor` case:

```csharp
                case FailureReason.Calibrating: return "calibrating — keep walking";
```

- [ ] **Step 5: Recompile, run all tests.** Expected: `FloorResolverTests`, the new `LearnedEngineTests` and `ReadingTextTests` assertions pass; nothing regresses. If `NoArFloor_CorridorWalk_DerivedFloorThenWalls` fails on wall distance, check `floor.HeightAbove` first (the scale), then the map (association).

- [ ] **Step 6: Commit (only if Rachit asks)**

```bash
git add Assets/WallDistance/Runtime/Core/FloorResolver.cs* Assets/WallDistance/Runtime/Core/WallMeasurementEngine.cs Assets/WallDistance/Runtime/Core/ReadingText.cs Assets/WallDistance/Tests/EditMode/FloorResolverTests.cs* Assets/WallDistance/Tests/EditMode/ReadingTextTests.cs Assets/WallDistance/Tests/EditMode/LearnedEngineTests.cs
git commit -m "Resolve a derived floor when ARCore has none"
```

---

### Task 5: AR wiring, CSV columns, spec amendments

**Files:**
- Modify: `Assets/WallDistance/Runtime/AR/WallDistanceService.cs` (fields near line 72, `Awake`, `OnInferenceFrame` lines 168–183, `NewSessionId`, `Update` continuity block and `detectionFailure`)
- Modify: `Assets/WallDistance/Runtime/AR/MeasurementCsvRecorder.cs` (`floor_h_m` source; append 7 values after `pipe.WallsCarved`)
- Modify: `Assets/WallDistance/Runtime/Core/CsvSchema.cs`
- Modify: `Assets/WallDistance/Tests/EditMode/CsvSchemaTests.cs`
- Modify: `docs/superpowers/specs/2026-10-06-fast-wall-detection-design.md` (§3, §5.1, §5.2, §5.4, §6, §7, §9 per floor-free spec §9)

**Interfaces:**
- Consumes: `FloorResolver` (Task 4).
- Produces: `WallDistanceService.FloorResolver { get; }`, `WallDistanceService.ActiveFloor { get; }`; CSV learned columns `floor_source, height_m, height_spread_m, height_weight, selfalign_ok, selfalign_ms, clue_samples`.

- [ ] **Step 1: Update the schema test first**

In `CsvSchemaTests.AppendedColumns_SpecSection7ThenSidesThenWidth`: change `20 + 10 + 10 + 1` to `27 + 10 + 10 + 1`; append to the expected `LearnedColumns` after `"map_clears", "walls_carved",`:

```csharp
                // Floor-free scale: which floor scaled the frame and how sure the height estimate is.
                "floor_source", "height_m", "height_spread_m", "height_weight", "selfalign_ok", "selfalign_ms", "clue_samples",
```

and change the index assertions to `cols[27]` (`left_valid`), `cols[46]` (`right_reason`), `cols[47]` (`corridor_width_m`).

- [ ] **Step 2: Recompile, run tests; confirm `AppendedColumns_SpecSection7ThenSidesThenWidth` fails** (column count 41 vs 48).

- [ ] **Step 3: `CsvSchema.cs`**: append after `"map_clears", "walls_carved",`:

```csharp
            // Floor-free scale (2026-10-07): ARPlane / Derived / None, the pooled camera height,
            // its spread and evidence weight, whether this frame self-aligned and how long that
            // took, and how many confident raw-depth samples it could use.
            "floor_source", "height_m", "height_spread_m", "height_weight", "selfalign_ok", "selfalign_ms", "clue_samples",
```

- [ ] **Step 4: `MeasurementCsvRecorder.cs`**

Replace the `floor_h_m` value (the `service.floorSource != null && service.floorSource.HasFloor ? ...` expression) with:

```csharp
            // The floor that actually scaled the last frame, ARCore's or derived; NaN when none.
            Append(service.ActiveFloor.IsValid ? service.ActiveFloor.HeightAbove(s.aimed.cameraPose.position) : float.NaN);
```

After `Append(pipe.WallsCarved);` add:

```csharp
            var fr = service.FloorResolver;
            Append(fr != null ? fr.LastSource.ToString() : "None");
            Append(fr != null ? fr.Estimator.HeightMeters : float.NaN);
            Append(fr != null ? fr.Estimator.SpreadMeters : float.NaN);
            Append(fr != null ? fr.Estimator.WindowWeight : float.NaN);
            Append(fr != null && fr.LastSelfAlignOk ? 1 : 0);
            Append(fr != null ? fr.LastSelfAlignMs : double.NaN);
            Append(fr != null ? fr.LastClueSamples : -1);
```

- [ ] **Step 5: `WallDistanceService.cs`**

5a. Replace the `_metric` stride comment and constant (lines 72–74) with:

```csharp
        readonly List<MetricSample> _metric = new List<MetricSample>(4096);
        // Raw depth is ~160x90 = 14 400 pixels; every one is read. On glossy corridors only ~35 of
        // them are confident (run 150956), and each is a camera-height clue for the derived floor.
        const int MetricStride = 1;
        FloorResolver _floorResolver;
        /// <summary>Chooses ARCore's floor or a derived one for each inference frame (floor-free design).</summary>
        public FloorResolver FloorResolver => _floorResolver;
        /// <summary>The floor the last inference frame was scaled against; invalid when there was none.</summary>
        public FloorPlane ActiveFloor { get; private set; }
```

5b. In `Awake`, immediately before `NewSessionId();`:

```csharp
            // Before NewSessionId, which resets it.
            _floorResolver = new FloorResolver(config.detection);
```

5c. In `NewSessionId`, after `_continuity.Reset();`:

```csharp
            // A floor height from the old coordinate frame means nothing in the new one.
            _floorResolver?.Reset();
            ActiveFloor = default;
```

5d. In `Update`, inside the `if (_continuity.Update(...))` block, after `_anchoring?.Clear();`:

```csharp
                // The floor's world height went with the old frame; the camera's height above it did not.
                _floorResolver.MarkDiscontinuity();
```

5e. In `Update`, replace the `detectionFailure = ...` initialiser with:

```csharp
                detectionFailure = automatic
                    ? _floorResolver.Estimator.Explain(Pipeline.DetectionFailure(now, InferenceAvailable), now)
                    : FailureReason.None,
```

5f. In `OnInferenceFrame`, replace from `FloorPlane floor = ...` through `Pipeline.ProcessFrame(...)` with:

```csharp
            FloorPlane arFloor = floorSource != null && floorSource.HasFloor ? floorSource.CurrentPlane : default;
            _metric.Clear();
            // Without an ARCore floor, confident raw depth is both the derived floor's camera-height
            // clue and the old per-frame fallback (≥ 200 samples). With one, neither is needed.
            if (!arFloor.IsValid && depthSource != null)
                MetricSampleCollector.Collect(depthSource.Latest, img, config.detection.metricMinConfidence, now,
                    config.depthMaxAgeSeconds, MetricStride, _metric);
            FloorPlane floor = _floorResolver.Resolve(img, arFloor, _metric, now);
            ActiveFloor = floor;
            Pipeline.ProcessFrame(SessionId, img, floor, _metric, now);
```

- [ ] **Step 6: Recompile, run all tests.** Expected: all pass, including `CsvSchemaTests` and `RecorderLifecycleTests`.

- [ ] **Step 7: Apply the spec amendments** listed in floor-free spec §9 to `docs/superpowers/specs/2026-10-06-fast-wall-detection-design.md`, with the exact wording given there: §3 first sentence; §5.1 the `FloorPlane` input line; §5.2 two new table rows; §5.4 `Calibrating`; §6 the "No floor plane yet" row; §7 the seven columns; §9 the Phase 0 result note. Set the floor-free spec's **Status** to "Implemented 2026-10-07, awaiting field verification".

- [ ] **Step 8: Commit (only if Rachit asks)**

```bash
git add Assets/WallDistance/Runtime/AR/WallDistanceService.cs Assets/WallDistance/Runtime/AR/MeasurementCsvRecorder.cs Assets/WallDistance/Runtime/Core/CsvSchema.cs Assets/WallDistance/Tests/EditMode/CsvSchemaTests.cs docs/superpowers/specs/2026-10-06-fast-wall-detection-design.md docs/superpowers/specs/2026-10-07-floor-free-scale-design.md
git commit -m "Scale learned depth from a derived floor when ARCore finds none"
```

---

### Task 6: Real-data replay, device build, field check

**Files:**
- Create (temporary, deleted in Step 3): `Assets/WallDistance/Tests/EditMode/ZzSelfAlignReplayProbe.cs`

**Interfaces:**
- Consumes: `FloorSelfAligner`, `FloorAlignedDepth`, dump folders `Builds/logs/device-2026-10-07*/InferenceDumps/frame_*` (`meta.json`, `output.f32`).

- [ ] **Step 1: Write the replay probe**

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    /// <summary>
    /// TEMPORARY (floor-free plan Task 6): replays the C# self-aligner on real 10-07 dumps.
    /// Delete after reading the log; it depends on untracked files under Builds/logs.
    /// </summary>
    public class ZzSelfAlignReplayProbe
    {
        [System.Serializable]
        class Meta
        {
            public int size;
            public double imageTimestamp;
            public float[] intrinsics, cameraPosition, cameraRotationXYZW, floorPoint, floorUp;
            public int[] content;
            public int hasFloor;
        }

        [Test]
        public void Replay()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "logs"));
            var dirs = Directory.GetDirectories(root, "device-2026-10-07*")
                .SelectMany(d => Directory.Exists(Path.Combine(d, "InferenceDumps")) ? Directory.GetDirectories(Path.Combine(d, "InferenceDumps")) : new string[0]);
            var seen = new HashSet<double>();
            var self = new FloorSelfAligner(new DetectionConfig());
            var ar = new FloorAlignedDepth(new DetectionConfig());
            int frames = 0, ok = 0;
            var ms = new List<double>();
            var report = new System.Text.StringBuilder();
            foreach (var d in dirs)
            {
                string outPath = Path.Combine(d, "output.f32");
                if (!File.Exists(outPath)) continue;
                var m = JsonUtility.FromJson<Meta>(File.ReadAllText(Path.Combine(d, "meta.json")));
                if (!seen.Add(m.imageTimestamp)) continue;
                var img = new InverseDepthImage(m.size, m.size);
                var bytes = File.ReadAllBytes(outPath);
                System.Buffer.BlockCopy(bytes, 0, img.values, 0, bytes.Length);
                img.intrinsics = new DepthIntrinsics { fx = m.intrinsics[0], fy = m.intrinsics[1], cx = m.intrinsics[2], cy = m.intrinsics[3], width = m.size, height = m.size };
                img.cameraPose = new Pose(new Vector3(m.cameraPosition[0], m.cameraPosition[1], m.cameraPosition[2]),
                    new Quaternion(m.cameraRotationXYZW[0], m.cameraRotationXYZW[1], m.cameraRotationXYZW[2], m.cameraRotationXYZW[3]));
                img.content = new RectInt(m.content[0], m.content[1], m.content[2], m.content[3]);
                img.MaskOutsideContent();

                var a = self.Align(img);
                frames++;
                ms.Add(a.milliseconds);
                if (a.success) ok++;
                if (m.hasFloor == 0) continue;

                // Frames with an ARCore floor: compare depths off the floor, as the Python spike did.
                var floor = new FloorPlane(new Vector3(m.floorPoint[0], m.floorPoint[1], m.floorPoint[2]),
                    new Vector3(m.floorUp[0], m.floorUp[1], m.floorUp[2]));
                var aa = ar.Align(img, floor, null);
                float H = floor.HeightAbove(img.cameraPose.position);
                var errs = new List<float>();
                if (a.success && aa.success)
                    for (int i = 0; i < img.values.Length; i += 4)
                    {
                        if (float.IsNaN(img.values[i]) || (aa.hasFloorMask && aa.floorMask[i])) continue;
                        float zAr = aa.MetricDepth(img.values[i]), zSelf = a.MetricDepth(img.values[i], H);
                        if (zAr > 0.3f && zAr < 6f && !float.IsNaN(zSelf)) errs.Add(Mathf.Abs(zSelf - zAr) / zAr);
                    }
                errs.Sort();
                report.AppendLine($"{Path.GetFileName(d)} H={H:F2} self={(a.success ? "ok" : a.reason)} ratio={a.depthRatio:F2} " +
                                  $"arAlign={(aa.success ? "ok" : aa.reason)} diff={(errs.Count > 0 ? errs[errs.Count / 2] * 100f : float.NaN):F1}%");
            }
            ms.Sort();
            Debug.Log($"[SelfAlignReplay] {ok}/{frames} frames self-aligned; ms p50={ms[ms.Count / 2]:F1} p95={ms[(int)(ms.Count * 0.95)]:F1}\n{report}");
            Assert.Pass($"{ok}/{frames} self-aligned");
        }
    }
}
```

- [ ] **Step 2: Run it and compare with the spike**

Recompile and run the tests, then read the `[SelfAlignReplay]` log line (Unity Editor log, `%LOCALAPPDATA%\Unity\Editor\Editor.log`). Expected, from the Python spike: on the 9 frames `frame_1847*`, `diff` between 0.8% and 7.4% (median ~5%); the four wall close-ups `frame_17316[2-4]*` rejected. Report to Rachit: the C# vs Python agreement, the self-aligned fraction of **all** dumps (the corridor frames of run 150956 included), and `ms` p50/p95. If more than ~2 percentage points off the spike on any of the 9 frames, stop and find the difference (ray convention, content rect) before continuing.

- [ ] **Step 3: Delete the probe**

```bash
rm Assets/WallDistance/Tests/EditMode/ZzSelfAlignReplayProbe.cs Assets/WallDistance/Tests/EditMode/ZzSelfAlignReplayProbe.cs.meta
```

Recompile; run all tests once more. Expected: all pass.

- [ ] **Step 4: Build and install**

```bash
unity command build --target Android --outputPath Builds/Android/WallDistanceDemo_QNN.apk --options '["Development"]' --confirm true --no-banner
```

Poll `unity command build_status --no-banner` until it no longer matches `building|queued`, then:

```bash
export MSYS_NO_PATHCONV=1; "/c/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe" -s f90a1f7b install -r Builds/Android/WallDistanceDemo_QNN.apk
```

- [ ] **Step 5: Field check (Rachit runs it; spec §6 pass criteria)**

Ask Rachit to record in the grey glossy corridor of run 150956: tape-measured side and end walls at 1, 2 and 3 m, three trials each, plus five fresh starts walking from a standstill. Pull the newest CSV from `/sdcard/Android/data/com.arnav.walldistance/files/WallDistanceLogs` and report, separating measured from inferred:

- share of rows with `floor_source` = `Derived`, `ARPlane`, `None`, and time to the first `Derived` row after each start;
- `height_m` over time (drift, steps at `map_clears` increments) and `height_spread_m`;
- `selfalign_ok` rate and `selfalign_ms` p50/p95 (budget ≤ 5 ms p95);
- aimed readings against the taped distances: median error per distance (pass: ≤ 10%);
- longest gap between valid readings while tracking (pass: ≤ 2 s).

If time spent in `Calibrating` while walking exceeds 20%, raise the out-of-scope feature-point clue (spec §7) with Rachit; do not add it unasked.
