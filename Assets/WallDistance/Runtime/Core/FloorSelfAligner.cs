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

            // The count's tolerance band also admits a thin strip of wall at the floor base.
            // Maximising that count alone biases even a noiseless floor toward a curved fit.
            // Once the grid has found the floor, its inliers constrain ray.y = y0 * (d + r):
            // fit that line twice to remove the grid quantisation and the boundary-strip bias.
            // The broad search and all acceptance gates still decide whether this is a floor.
            for (int pass = 0; pass < 2; pass++)
                bestR = Refine(bestR, dMin);
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

        float Refine(float r, float dMin)
        {
            if (Score(r, out float y0) < _cfg.selfAlignMinInliers) return r;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            int n = 0;
            float band = _cfg.selfAlignHeightTolerance * -y0;
            for (int i = 0; i < _d.Count; i++)
            {
                float shifted = _d[i] + r;
                if (!(shifted > 0f) || Mathf.Abs(_den[i] / shifted - y0) >= band) continue;
                double x = _d[i], y = _den[i];
                n++;
                sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double variance = n * sxx - sx * sx;
            if (!(variance > 1e-12)) return r;
            double slope = (n * sxy - sx * sy) / variance;
            if (!(slope < 0)) return r;
            float refined = (float)((sy - slope * sx) / n / slope);
            // A refinement must preserve positive depths for every candidate. Degenerate fits
            // retain the grid result and are judged by the same identifiability/residual gates.
            return !float.IsInfinity(refined) && refined > -dMin ? refined : r;
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
