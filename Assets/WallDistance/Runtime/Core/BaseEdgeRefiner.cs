using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Snaps a learned wall's base line to the image edge where wall meets floor. The network's
    /// depth is smooth across that corner, so its base is soft; the image edge is sharp, and on
    /// the tracked floor it converts directly back to metres (spec §5.2).
    ///
    /// For each sample along the base line, take the luma profile across the line, inside a
    /// band capped at ±edgeMaxShiftMeters on the floor (deviation D8). Walk in from the FLOOR
    /// side and take the first strong local gradient maximum: the first edge met from the floor
    /// is the base, while skirting tops and posters sit above it.
    /// </summary>
    public sealed class BaseEdgeRefiner
    {
        readonly DetectionConfig _cfg;
        readonly List<Vector2> _pts = new List<Vector2>(64);   // (along, offset) in the observation's floor frame
        readonly List<int> _keep = new List<int>(64);
        float[] _profile = new float[32], _grad = new float[32];

        public BaseEdgeRefiner(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

        public bool TryRefine(InverseDepthImage img, FloorPlane floor, WallObservation obs, out WallObservation refined)
        {
            refined = obs;
            // Without a floor there is no metric back-projection for the edge, so nothing to refine.
            if (!floor.IsValid || obs == null || obs.Length <= 0f) return false;

            Vector3 up = floor.up, dir = obs.direction, n = obs.normal;
            // Put the base line exactly on the floor; with a floor-aligned observation it already is.
            Vector3 o = floor.Project(obs.origin);

            int count = Mathf.Clamp(Mathf.FloorToInt(obs.Length / _cfg.edgeSampleSpacingMeters) + 1, 2, _cfg.edgeMaxSamples);
            _pts.Clear();
            for (int s = 0; s < count; s++)
            {
                float along = Mathf.Lerp(obs.extentMin, obs.extentMax, s / (float)(count - 1));
                if (TrySnap(img, floor, o + dir * along, dir, n, out Vector3 hit))
                    _pts.Add(new Vector2(Vector3.Dot(hit - o, dir), Vector3.Dot(hit - o, n)));
            }
            float snapFraction = _pts.Count / (float)count;
            if (_pts.Count < 5 || snapFraction < _cfg.edgeMinSnapFraction) return false;

            // Robust line through the snapped floor points: fit, drop > 3 cm, refit.
            FitLine(_pts, null, out Vector2 c, out Vector2 axis, out _);
            _keep.Clear();
            for (int i = 0; i < _pts.Count; i++)
                if (Mathf.Abs(Cross2(axis, _pts[i] - c)) <= 0.03f) _keep.Add(i);
            if (_keep.Count < 5) return false;
            FitLine(_pts, _keep, out c, out axis, out float rms);
            if (rms > _cfg.edgeMaxRmsMeters) return false;

            // Keep the observation's sense of direction so extents stay meaningful.
            if (axis.x < 0f) axis = -axis;
            float angle = Mathf.Abs(Mathf.Atan2(axis.y, axis.x)) * Mathf.Rad2Deg;
            if (angle > _cfg.edgeMaxAngleChangeDeg) return false;

            Vector3 newDir = (dir * axis.x + n * axis.y).normalized;
            Vector3 linePoint = o + dir * c.x + n * c.y;
            var r = obs.Clone();
            // normal = Cross(up, direction) inverts direction = Cross(normal, up) for horizontal vectors.
            r.normal = Vector3.Cross(up, newDir).normalized;
            // New origin: the point on the refined line closest to the old origin, so extents carry over.
            r.origin = linePoint + newDir * Vector3.Dot(o - linePoint, newDir);
            r.source = MeasurementSource.FloorEdge;
            r.edgeSnapFraction = snapFraction;
            r.rms = rms;
            r.inliers = _keep.Count;
            refined = r;
            return true;
        }

        /// <summary>Find the base edge near floor point <paramref name="p"/>; return it on the floor.</summary>
        bool TrySnap(InverseDepthImage img, FloorPlane floor, Vector3 p, Vector3 dir, Vector3 n, out Vector3 hit)
        {
            hit = default;
            const float probe = 0.05f;
            if (!img.TryProject(p, out float pu, out float pv, out _)) return false;
            if (!img.TryProject(p + dir * probe, out float tu, out float tv, out _)) return false;
            if (!img.TryProject(p + n * _cfg.edgeMaxShiftMeters, out float fu, out float fv, out _)) return false;
            if (!img.TryProject(p - n * _cfg.edgeMaxShiftMeters, out float wu, out float wv, out _)) return false;

            var P = new Vector2(pu, pv);
            Vector2 tan = new Vector2(tu, tv) - P;
            if (tan.sqrMagnitude < 1e-6f) return false;
            tan.Normalize();
            // Image normal pointing to the floor side (toward the camera, in front of the wall).
            var nrm = new Vector2(-tan.y, tan.x);
            Vector2 floorSide = new Vector2(fu, fv) - P;
            if (Vector2.Dot(nrm, floorSide) < 0f) nrm = -nrm;

            // Band: the SHORTER of the two ±8 cm reaches, capped at edgeBandPixels. Taking the
            // shorter keeps the band inside 8 cm on both sides (D8).
            float reach = Mathf.Min(Mathf.Abs(Vector2.Dot(floorSide, nrm)), Mathf.Abs(Vector2.Dot(new Vector2(wu, wv) - P, nrm)));
            int band = Mathf.Clamp(Mathf.CeilToInt(reach), 2, _cfg.edgeBandPixels);
            int len = 2 * band + 1;
            if (_profile.Length < len) { _profile = new float[len]; _grad = new float[len]; }

            // Profile f(k), k = -band..band (index k + band), averaged over three tangent offsets
            // to suppress single-pixel noise along the edge.
            for (int k = -band; k <= band; k++)
            {
                float sum = 0f;
                for (int j = -1; j <= 1; j++)
                {
                    if (!TryLuma(img, P + nrm * k + tan * j, out float l)) return false;
                    sum += l;
                }
                _profile[k + band] = sum / 3f;
            }
            // g(i) = f(i+1) - f(i) is the step between profile index i and i+1.
            for (int i = 0; i < len - 1; i++) _grad[i] = Mathf.Abs(_profile[i + 1] - _profile[i]);

            // Walk from the floor end toward the wall; the first strong local maximum is the base.
            int found = -1;
            for (int i = len - 2; i >= 0; i--)
            {
                float g = _grad[i];
                if (g < _cfg.edgeMinContrast) continue;
                float prev = i + 1 <= len - 2 ? _grad[i + 1] : 0f, next = i - 1 >= 0 ? _grad[i - 1] : 0f;
                if (g >= prev && g >= next) { found = i; break; }
            }
            if (found < 0) return false;

            // Parabolic sub-pixel peak.
            float gm = found - 1 >= 0 ? _grad[found - 1] : 0f, g0 = _grad[found], gp = found + 1 <= len - 2 ? _grad[found + 1] : 0f;
            float den = gm - 2f * g0 + gp;
            float delta = Mathf.Abs(den) > 1e-6f ? Mathf.Clamp(0.5f * (gm - gp) / den, -0.5f, 0.5f) : 0f;
            // The step between indices found and found+1 sits at offset (found - band) + 0.5.
            float offset = found - band + 0.5f + delta;
            Vector2 q = P + nrm * offset;
            if (!floor.TryIntersect(img.cameraPose.position, img.WorldRay(q.x, q.y), out hit, out _)) return false;
            // Pixel rounding and the two-pixel sampling footprint can exceed the projected band,
            // especially at a grazing floor view. Enforce the real cap on the back-projected hit.
            return Mathf.Abs(Vector3.Dot(hit - p, n)) <= _cfg.edgeMaxShiftMeters + 1e-4f;
        }

        static bool TryLuma(InverseDepthImage img, Vector2 p, out float value)
        {
            value = 0f;
            int x0 = Mathf.FloorToInt(p.x), y0 = Mathf.FloorToInt(p.y);
            // The 2×2 bilinear footprint must lie inside the camera content; padding is not image.
            if (!img.InContent(x0, y0) || !img.InContent(x0 + 1, y0 + 1)) return false;
            float fx = p.x - x0, fy = p.y - y0;
            int i = y0 * img.width + x0;
            float top = img.luma[i] + (img.luma[i + 1] - img.luma[i]) * fx;
            float bot = img.luma[i + img.width] + (img.luma[i + img.width + 1] - img.luma[i + img.width]) * fx;
            value = top + (bot - top) * fy;
            return true;
        }

        static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        /// <summary>Total-least-squares line: centroid c, unit axis, RMS perpendicular residual.</summary>
        static void FitLine(List<Vector2> pts, List<int> idx, out Vector2 c, out Vector2 axis, out float rms)
        {
            int n = idx?.Count ?? pts.Count;
            c = Vector2.zero;
            for (int i = 0; i < n; i++) c += pts[idx?[i] ?? i];
            c /= n;
            float sxx = 0f, sxy = 0f, syy = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector2 d = pts[idx?[i] ?? i] - c;
                sxx += d.x * d.x; sxy += d.x * d.y; syy += d.y * d.y;
            }
            float theta = 0.5f * Mathf.Atan2(2f * sxy, sxx - syy);
            axis = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));
            float ss = 0f;
            for (int i = 0; i < n; i++) { float r = Cross2(axis, pts[idx?[i] ?? i] - c); ss += r * r; }
            rms = Mathf.Sqrt(ss / n);
        }
    }
}
