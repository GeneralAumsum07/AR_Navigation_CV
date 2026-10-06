using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Gives the network's relative depth a metric scale by fitting it to the tracked floor: every
    /// floor pixel's true Z-depth is known from the floor plane and the camera pose, so the fit is
    /// a robust line fit (spec §5.2). Without a usable floor, confident ARCore raw-depth samples
    /// stand in (spec §6); with neither, the frame is refused - never an assumed camera height.
    /// </summary>
    public sealed class FloorAlignedDepth
    {
        readonly DetectionConfig _cfg;
        readonly List<float> _d = new List<float>(20000), _z = new List<float>(20000), _rel = new List<float>(20000);
        readonly List<int> _inl = new List<int>(20000);

        public AlignmentResult Result { get; } = new AlignmentResult();

        public FloorAlignedDepth(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

        public AlignmentResult Align(InverseDepthImage img, FloorPlane floor, IReadOnlyList<MetricSample> metric)
        {
            var r = Result;
            r.Reset(img.width * img.height, _cfg.parameterisation);
            _d.Clear();
            _z.Clear();

            bool floorUsable = floor.IsValid && floor.PlausibleCameraHeight(img.cameraPose.position);
            int minInliers = _cfg.minFloorInliers;
            if (floorUsable) CollectFloorPairs(img, floor);

            if (_d.Count < _cfg.minFloorInliers)
            {
                int floorPairs = _d.Count;
                bool metricOk = metric != null && metric.Count >= _cfg.minMetricSamples;
                if (metricOk)
                {
                    _d.Clear();
                    _z.Clear();
                    CollectMetricPairs(img, metric);
                    r.usedMetricSamples = true;
                    minInliers = _cfg.minMetricSamples;
                }
                if (!metricOk || _d.Count < _cfg.minMetricSamples)
                {
                    r.failure = floorUsable ? FailureReason.AlignmentFailed : FailureReason.NoFloor;
                    r.reason = floorUsable
                        ? $"only {floorPairs} floor pixels in view (need {_cfg.minFloorInliers})"
                        : "no floor plane and too few confident raw-depth samples";
                    return r;
                }
            }
            r.candidates = _d.Count;

            if (!Ransac(out float s, out float t))
            {
                r.failure = FailureReason.AlignmentFailed;
                r.reason = "no consistent depth scale";
                return r;
            }
            // Two least-squares refits on the inlier set tighten the RANSAC estimate.
            for (int k = 0; k < 2; k++)
            {
                CountInliers(s, t);
                // The broad RANSAC band also admits wall pixels just above the base. Refitting all
                // of them biases even an exact floor. Tighten to the current residual spread so
                // clean floor pixels dominate, while noisy frames retain a proportionate band.
                float refitBand = Mathf.Clamp(_cfg.alignRefitResidualMultiplier * Median(_rel),
                    _cfg.alignRefitMinRelative, _cfg.alignInlierRelative);
                CountInliers(s, t, refitBand);
                if (_inl.Count < 2 || !LeastSquares(out s, out t)) break;
            }
            CountInliers(s, t);
            r.s = s;
            r.t = t;
            r.inliers = _inl.Count;
            r.residual = Median(_rel);

            if (r.inliers < minInliers)
            {
                r.failure = FailureReason.AlignmentFailed;
                r.reason = $"{r.inliers} inliers (need {minInliers})";
                return r;
            }
            if (!(r.residual <= _cfg.maxAlignResidual))
            {
                r.failure = FailureReason.AlignmentFailed;
                r.reason = $"floor residual {r.residual * 100f:F1}% (max {_cfg.maxAlignResidual * 100f:F0}%)";
                return r;
            }

            r.success = true;
            r.reason = r.usedMetricSamples ? "scaled from raw depth" : "scaled from floor";
            if (floorUsable) BuildMask(img, floor, r);
            return r;
        }

        void CollectFloorPairs(InverseDepthImage img, FloorPlane floor)
        {
            int step = Mathf.Max(1, _cfg.alignStride);
            Vector3 o = img.cameraPose.position;
            for (int v = img.content.yMin; v < img.content.yMax; v += step)
            for (int u = img.content.xMin; u < img.content.xMax; u += step)
            {
                float d = img.values[v * img.width + u];
                if (float.IsNaN(d)) continue;
                // WorldRay has unit camera-Z, so t from the floor intersection is the Z-depth.
                if (!floor.TryIntersect(o, img.WorldRay(u, v), out _, out float z)) continue;
                if (z < _cfg.floorMinDepthMeters || z > _cfg.floorMaxDepthMeters) continue;
                _d.Add(d);
                _z.Add(z);
            }
        }

        void CollectMetricPairs(InverseDepthImage img, IReadOnlyList<MetricSample> metric)
        {
            for (int i = 0; i < metric.Count; i++)
            {
                int u = Mathf.RoundToInt(metric[i].pixel.x), v = Mathf.RoundToInt(metric[i].pixel.y);
                if (!img.InContent(u, v)) continue;
                float d = img.values[v * img.width + u];
                float z = metric[i].depthMeters;
                if (float.IsNaN(d) || !(z >= _cfg.floorMinDepthMeters && z <= _cfg.floorMaxDepthMeters)) continue;
                _d.Add(d);
                _z.Add(z);
            }
        }

        float Target(float z) => _cfg.parameterisation == DepthParameterisation.AffineInverseDepth ? 1f / z : z;

        bool Ransac(out float bestS, out float bestT)
        {
            // Fixed seed: identical frames give identical fits, which keeps tests and replays deterministic.
            var rng = new System.Random(17);
            int n = _d.Count, bestCount = 0;
            bestS = bestT = float.NaN;
            // Score on at most ~4000 pairs; the refits below use all of them.
            int stride = Mathf.Max(1, n / 4000);
            for (int it = 0; it < _cfg.alignRansacIterations; it++)
            {
                int i = rng.Next(n), j = rng.Next(n);
                float dd = _d[i] - _d[j];
                if (Mathf.Abs(dd) < 1e-6f) continue;
                float s = (Target(_z[i]) - Target(_z[j])) / dd;
                float t = Target(_z[i]) - s * _d[i];
                // Larger network values must mean nearer in inverse-depth mode; a negative scale is a degenerate pair.
                if (_cfg.parameterisation == DepthParameterisation.AffineInverseDepth && s <= 0f) continue;
                int count = 0;
                for (int k = 0; k < n; k += stride)
                {
                    float zp = AlignmentResult.Predict(_cfg.parameterisation, s, t, _d[k]);
                    if (Mathf.Abs(zp - _z[k]) <= _cfg.alignInlierRelative * _z[k]) count++;
                }
                if (count > bestCount) { bestCount = count; bestS = s; bestT = t; }
            }
            return bestCount >= 2;
        }

        void CountInliers(float s, float t, float relativeBand = -1f)
        {
            _inl.Clear();
            _rel.Clear();
            for (int k = 0; k < _d.Count; k++)
            {
                float zp = AlignmentResult.Predict(_cfg.parameterisation, s, t, _d[k]);
                float rel = Mathf.Abs(zp - _z[k]) / _z[k];
                if (rel <= (relativeBand >= 0f ? relativeBand : _cfg.alignInlierRelative)) { _inl.Add(k); _rel.Add(rel); }
            }
        }

        bool LeastSquares(out float s, out float t)
        {
            // Ordinary least squares of target(z) on d over the current inliers.
            double sd = 0, sy = 0, sdd = 0, sdy = 0;
            int n = _inl.Count;
            for (int i = 0; i < n; i++)
            {
                int k = _inl[i];
                double d = _d[k], y = Target(_z[k]);
                sd += d; sy += y; sdd += d * d; sdy += d * y;
            }
            double den = n * sdd - sd * sd;
            s = t = float.NaN;
            if (Math.Abs(den) < 1e-12) return false;
            s = (float)((n * sdy - sd * sy) / den);
            t = (float)((sy - s * sd) / n);
            return true;
        }

        static float Median(List<float> v)
        {
            if (v.Count == 0) return float.NaN;
            v.Sort();
            return v[v.Count / 2];
        }

        void BuildMask(InverseDepthImage img, FloorPlane floor, AlignmentResult r)
        {
            r.hasFloorMask = true;
            Vector3 o = img.cameraPose.position;
            for (int v = img.content.yMin; v < img.content.yMax; v++)
            for (int u = img.content.xMin; u < img.content.xMax; u++)
            {
                int i = v * img.width + u;
                float z = r.MetricDepth(img.values[i]);
                if (float.IsNaN(z)) continue;
                if (!floor.TryIntersect(o, img.WorldRay(u, v), out _, out float zf)) continue;
                r.floorMask[i] = Mathf.Abs(z - zf) <= _cfg.alignInlierRelative * zf;
            }
        }
    }
}
