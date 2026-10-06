using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Finds vertical walls in floor-aligned depth. A vertical plane projects onto the floor as a
    /// LINE, so the 3D plane search reduces to robust 2D line fitting on the floor projection
    /// (spec §5.2): cheaper, and verticality is built in rather than checked afterwards.
    /// People walking through view are rejected by the same test (non-planar, short).
    /// </summary>
    public sealed class VerticalPlaneExtractor
    {
        readonly DetectionConfig _cfg;
        readonly List<Vector2> _p2 = new List<Vector2>(20000);
        readonly List<float> _h = new List<float>(20000);
        readonly List<Vector3> _world = new List<Vector3>(20000);
        readonly List<int> _active = new List<int>(20000), _inl = new List<int>(20000);
        readonly List<float> _tmp = new List<float>(20000);
        bool[] _taken = new bool[0];

        public VerticalPlaneExtractor(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

        public int Extract(InverseDepthImage img, AlignmentResult align, FloorPlane floor, double now, List<WallObservation> output)
        {
            output.Clear();
            if (align == null || !align.success) return 0;

            Vector3 up = floor.IsValid ? floor.up : Vector3.up;
            // Floor-plane basis: e1 along the camera's horizontal heading, e2 to its side.
            if (!Horizontal.TryDirection(img.cameraPose.rotation * Vector3.forward, up, out Vector3 e1)
                && !Horizontal.TryDirection(img.cameraPose.rotation * Vector3.up, up, out e1))
                e1 = Vector3.forward;
            Vector3 e2 = Vector3.Cross(up, e1);
            float floorLevel = floor.IsValid ? Vector3.Dot(floor.point, up) : float.NaN;

            CollectPoints(img, align, floor, up, e1, e2, floorLevel);
            if (_taken.Length < _p2.Count) _taken = new bool[_p2.Count];
            System.Array.Clear(_taken, 0, _p2.Count);
            _active.Clear();
            for (int i = 0; i < _p2.Count; i++) _active.Add(i);

            var rng = new System.Random(7);
            int found = 0;
            for (int attempt = 0; attempt < _cfg.maxPlanes * 2 && found < _cfg.maxPlanes && _active.Count >= _cfg.minPlaneInliers; attempt++)
            {
                if (!RansacLine(rng, out Vector2 n, out float c)) break;
                float thr = _cfg.planeInlierMeters, rms = 0f;
                for (int k = 0; k < 2; k++)
                {
                    Collect(n, c, thr);
                    if (_inl.Count < 3) break;
                    // Door jambs and ceiling points can sit inside the broad RANSAC band. RMS
                    // grows with those outliers and keeps them in the next fit, rotating a clean
                    // wall. Tighten against the median residual before fitting the principal axis.
                    _tmp.Clear();
                    foreach (int i in _inl) _tmp.Add(Mathf.Abs(Vector2.Dot(n, _p2[i]) - c));
                    thr = Mathf.Clamp(_cfg.planeRefitResidualMultiplier * Percentile(_tmp, 0.5f),
                        _cfg.planeRefitMinMeters, _cfg.planeInlierMeters);
                    Collect(n, c, thr);
                    if (_inl.Count < 3) break;
                    FitLine(out n, out c, out rms);
                }
                Collect(n, c, thr);
                if (_inl.Count >= 3) FitLine(out n, out c, out rms);
                // Consume these points whether or not they pass: a rejected line (ceiling strip,
                // clutter) must not be found again by the next attempt.
                RemoveInliers();
                if (_inl.Count < _cfg.minPlaneInliers) continue;

                var obs = Build(n, c, rms, up, e1, e2, floor, floorLevel, img.cameraPose.position, now);
                if (obs == null) continue;
                output.Add(obs);
                found++;
            }
            return output.Count;
        }

        void CollectPoints(InverseDepthImage img, AlignmentResult align, FloorPlane floor, Vector3 up, Vector3 e1, Vector3 e2, float floorLevel)
        {
            _p2.Clear(); _h.Clear(); _world.Clear();
            int step = Mathf.Max(1, _cfg.extractStride);
            for (int v = img.content.yMin; v < img.content.yMax; v += step)
            for (int u = img.content.xMin; u < img.content.xMax; u += step)
            {
                int i = v * img.width + u;
                if (align.hasFloorMask && align.floorMask[i]) continue;
                float z = align.MetricDepth(img.values[i]);
                if (!(z >= _cfg.wallMinDepthMeters && z <= _cfg.wallMaxDepthMeters)) continue;   // also drops NaN
                Vector3 p = img.WorldPoint(u, v, z);
                float h = Vector3.Dot(p, up);
                if (floor.IsValid && h - floorLevel < _cfg.floorClearanceMeters) continue;
                _p2.Add(new Vector2(Vector3.Dot(p, e1), Vector3.Dot(p, e2)));
                _h.Add(h);
                _world.Add(p);
            }
        }

        bool RansacLine(System.Random rng, out Vector2 bestN, out float bestC)
        {
            int cnt = _active.Count, best = 0;
            bestN = default; bestC = 0f;
            // Score on a subsample of ≤ 2000 points; the refits and the final count use all of them.
            int stride = Mathf.Max(1, cnt / 2000);
            for (int it = 0; it < _cfg.planeRansacIterations; it++)
            {
                Vector2 a = _p2[_active[rng.Next(cnt)]], b = _p2[_active[rng.Next(cnt)]];
                Vector2 ab = b - a;
                float len = ab.magnitude;
                if (len < _cfg.minPairSeparationMeters) continue;
                var n = new Vector2(-ab.y, ab.x) / len;
                float c = Vector2.Dot(n, a);
                int count = 0;
                for (int k = 0; k < cnt; k += stride)
                    if (Mathf.Abs(Vector2.Dot(n, _p2[_active[k]]) - c) <= _cfg.planeInlierMeters) count++;
                if (count > best) { best = count; bestN = n; bestC = c; }
            }
            return best * stride >= _cfg.minPlaneInliers;
        }

        void Collect(Vector2 n, float c, float thr)
        {
            _inl.Clear();
            for (int k = 0; k < _active.Count; k++)
            {
                int i = _active[k];
                if (Mathf.Abs(Vector2.Dot(n, _p2[i]) - c) <= thr) _inl.Add(i);
            }
        }

        void FitLine(out Vector2 n, out float c, out float rms)
        {
            // Total least squares (PCA): the line direction is the principal axis of the inliers.
            double mx = 0, my = 0;
            foreach (int i in _inl) { mx += _p2[i].x; my += _p2[i].y; }
            mx /= _inl.Count; my /= _inl.Count;
            double sxx = 0, sxy = 0, syy = 0;
            foreach (int i in _inl)
            {
                double dx = _p2[i].x - mx, dy = _p2[i].y - my;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }
            double theta = 0.5 * System.Math.Atan2(2 * sxy, sxx - syy);
            n = new Vector2((float)-System.Math.Sin(theta), (float)System.Math.Cos(theta));
            c = (float)(n.x * mx + n.y * my);
            double ss = 0;
            foreach (int i in _inl) { double r = Vector2.Dot(n, _p2[i]) - c; ss += r * r; }
            rms = (float)System.Math.Sqrt(ss / _inl.Count);
        }

        void RemoveInliers()
        {
            foreach (int i in _inl) _taken[i] = true;
            int w = 0;
            for (int k = 0; k < _active.Count; k++)
                if (!_taken[_active[k]]) _active[w++] = _active[k];
            _active.RemoveRange(w, _active.Count - w);
        }

        WallObservation Build(Vector2 n, float c, float rms, Vector3 up, Vector3 e1, Vector3 e2,
            FloorPlane floor, float floorLevel, Vector3 cam, double now)
        {
            // A wall must span real height (rejects ceiling strips and low clutter) and real length.
            _tmp.Clear();
            foreach (int i in _inl) _tmp.Add(_h[i]);
            float h05 = Percentile(_tmp, 0.05f), h95 = Percentile(_tmp, 0.95f), h02 = Percentile(_tmp, 0.02f);
            if (h95 - h05 < _cfg.minHeightSpanMeters) return null;

            Vector3 normal = (n.x * e1 + n.y * e2).normalized;
            double cx = 0, cy = 0;
            foreach (int i in _inl) { cx += _p2[i].x; cy += _p2[i].y; }
            Vector3 linePoint = e1 * (float)(cx / _inl.Count) + e2 * (float)(cy / _inl.Count);
            // Face the camera; the dot ignores height because the normal is horizontal.
            if (Vector3.Dot(cam - linePoint, normal) < 0f) normal = -normal;
            // With a floor, the base IS the floor. Without one (raw-depth scale), use the lowest wall points.
            float baseLevel = floor.IsValid ? floorLevel : h02;

            var obs = new WallObservation
            {
                normal = normal, up = up, origin = linePoint + up * baseLevel,
                inliers = _inl.Count, rms = rms, source = MeasurementSource.LearnedDepth, timestamp = now,
            };
            Vector3 dir = obs.direction;
            _tmp.Clear();
            foreach (int i in _inl) _tmp.Add(Vector3.Dot(_world[i] - obs.origin, dir));
            float aMin = Percentile(_tmp, 0.02f), aMax = Percentile(_tmp, 0.98f);
            if (aMax - aMin < _cfg.minLengthMeters) return null;
            float mid = 0.5f * (aMin + aMax);
            obs.origin += dir * mid;
            obs.extentMin = aMin - mid;
            obs.extentMax = aMax - mid;
            return obs;
        }

        static float Percentile(List<float> v, float q)
        {
            v.Sort();
            return v[Mathf.Clamp(Mathf.RoundToInt(q * (v.Count - 1)), 0, v.Count - 1)];
        }
    }
}
