using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>Result of fitting a plane to the raw depth patch under the crosshair.</summary>
    public struct DepthPlaneFit
    {
        public bool success;
        /// <summary>Perpendicular distance from the camera to the fitted plane (metres). NaN on failure.</summary>
        public float perpendicularDistanceMeters;
        /// <summary>Unit normal of the fitted plane in session space, facing the camera.</summary>
        public Vector3 worldNormal;
        /// <summary>Foot of the perpendicular from the camera onto the fitted plane (session space).</summary>
        public Vector3 footPoint;
        /// <summary>RMS residual (metres) of the inliers used in the final fit.</summary>
        public float rmsResidualMeters;
        public int inlierCount;
        public int sampleCount;
        public string reason;

        public static DepthPlaneFit Fail(string why) => new DepthPlaneFit
        {
            success = false,
            perpendicularDistanceMeters = float.NaN,
            rmsResidualMeters = float.NaN,
            reason = why,
        };
    }

    /// <summary>
    /// Depth-only fallback for when ARCore has produced no plane polygon - the common case on
    /// plain painted walls at close range, where the feature-point-based plane detector has
    /// nothing to latch onto but the ToF/stereo depth is still perfectly usable.
    ///
    /// Fits z = a·x + b·y + c to the camera-space points of a square pixel patch around the
    /// crosshair by least squares, drops residual outliers and refits, then converts the plane
    /// to a session-space normal and a perpendicular distance. The reading this produces is
    /// labelled <see cref="MeasurementSource.DepthOnly"/>: it has no cross-check, so it ranks
    /// below a depth-validated plane and must never be presented as equivalent.
    ///
    /// Why a z-on-(x,y) fit rather than PCA: walls are, by definition of the crosshair use case,
    /// roughly facing the camera, so z is single-valued over the patch and the linear solve is
    /// exact and allocation-free. PCA would only matter for surfaces near edge-on, which the
    /// tilt/RMS checks reject anyway.
    /// </summary>
    public sealed class DepthPlaneFitter
    {
        // Scratch buffers reused across frames; sized on first use for the configured patch.
        float[] _x, _y, _z;
        bool[] _inlier;

        public DepthPlaneFit Fit(DepthFrame frame, int centreU, int centreV, MeasurementConfig cfg)
        {
            if (frame == null || !frame.IsUsable) return DepthPlaneFit.Fail("no depth image");

            int r = Mathf.Max(1, cfg.depthOnlyPatchRadiusPixels);
            int side = 2 * r + 1;
            int cap = side * side;
            if (_x == null || _x.Length < cap)
            {
                _x = new float[cap]; _y = new float[cap]; _z = new float[cap]; _inlier = new bool[cap];
            }

            // Gather camera-space points from the patch. Invalid, out-of-range or low-confidence
            // pixels are simply skipped - a zero depth is "no data", never "zero metres".
            int n = 0;
            int rejected = 0, zeroDepth = 0, outOfRange = 0, lowConf = 0;
            for (int dv = -r; dv <= r; dv++)
            {
                int v = centreV + dv;
                if (v < 0 || v >= frame.height) continue;
                for (int du = -r; du <= r; du++)
                {
                    int u = centreU + du;
                    if (u < 0 || u >= frame.width) continue;
                    float z = frame.DepthAt(u, v);
                    if (float.IsNaN(z)) { zeroDepth++; rejected++; continue; }
                    if (z < cfg.depthMinValidMeters || z > cfg.depthMaxValidMeters) { outOfRange++; rejected++; continue; }
                    if (frame.ConfidenceAt(u, v) < cfg.depthMinConfidence) { lowConf++; rejected++; continue; }
                    _x[n] = (u - frame.intrinsics.cx) / frame.intrinsics.fx * z;
                    _y[n] = -(v - frame.intrinsics.cy) / frame.intrinsics.fy * z;
                    _z[n] = z;
                    _inlier[n] = true;
                    n++;
                }
            }

            if (n < cfg.depthOnlyMinInliers)
                return DepthPlaneFit.Fail($"only {n} valid depth samples under crosshair ({zeroDepth} no-data, {outOfRange} out-of-range, {lowConf} low-confidence)");

            // Iteratively reweighted least squares, hard-threshold variant: fit, drop samples whose
            // residual exceeds the inlier threshold, refit. Two rounds are enough to shed a light
            // switch or a hand edge; a genuinely non-planar patch fails the RMS check instead.
            float a = 0, b = 0, c = 0;
            int inliers = n;
            for (int round = 0; round <= cfg.depthOnlyRefitRounds; round++)
            {
                if (!SolveLeastSquares(n, out a, out b, out c))
                    return DepthPlaneFit.Fail("degenerate depth patch");
                if (round == cfg.depthOnlyRefitRounds) break;

                // Residual along the plane normal, not along z, so oblique walls are judged fairly.
                float normScale = 1f / Mathf.Sqrt(a * a + b * b + 1f);
                inliers = 0;
                for (int i = 0; i < n; i++)
                {
                    float res = (a * _x[i] + b * _y[i] + c - _z[i]) * normScale;
                    _inlier[i] = Mathf.Abs(res) <= cfg.depthInlierThresholdMeters;
                    if (_inlier[i]) inliers++;
                }
                if (inliers < cfg.depthOnlyMinInliers)
                    return DepthPlaneFit.Fail($"only {inliers}/{n} depth samples fit one plane");
            }

            // Final residual statistics on the inlier set.
            float invNorm = 1f / Mathf.Sqrt(a * a + b * b + 1f);
            float sumSq = 0f;
            inliers = 0;
            for (int i = 0; i < n; i++)
            {
                if (!_inlier[i]) continue;
                float res = (a * _x[i] + b * _y[i] + c - _z[i]) * invNorm;
                sumSq += res * res;
                inliers++;
            }
            float rms = Mathf.Sqrt(sumSq / Mathf.Max(1, inliers));
            if (rms > cfg.depthOnlyMaxRmsMeters)
                return DepthPlaneFit.Fail($"depth patch is not planar (rms {rms * 100f:F1} cm)");
            if ((float)inliers / n < cfg.depthMinInlierFraction)
                return DepthPlaneFit.Fail($"only {inliers * 100 / n}% of depth samples on one plane");

            // Plane a·x + b·y − z + c = 0 in camera space. Normal (a, b, −1) normalised; the camera
            // sits at the origin so the perpendicular distance is |c| / |n|.
            Vector3 nCam = new Vector3(a, b, -1f) * invNorm;
            float dist = Mathf.Abs(c) * invNorm;
            // Orient the normal toward the camera: the plane point (0,0,c) is in front (+z), so the
            // normal must have negative z to point back at the origin.
            if (nCam.z > 0f) nCam = -nCam;

            Vector3 nWorld = frame.cameraPose.rotation * nCam;
            float tiltDeg = Mathf.Abs(Mathf.Asin(Mathf.Clamp(nWorld.y, -1f, 1f))) * Mathf.Rad2Deg;
            if (tiltDeg > cfg.depthOnlyMaxTiltFromVerticalDeg)
                return DepthPlaneFit.Fail($"surface tilt {tiltDeg:F0}° from vertical - floor/ceiling/desk, not a wall");

            // Foot of the perpendicular: camera minus normal (normal faces the camera).
            Vector3 foot = frame.cameraPose.position - nWorld * dist;

            return new DepthPlaneFit
            {
                success = true,
                perpendicularDistanceMeters = dist,
                worldNormal = nWorld,
                footPoint = foot,
                rmsResidualMeters = rms,
                inlierCount = inliers,
                sampleCount = n,
                reason = $"depth-only fit: {inliers}/{n} samples, rms {rms * 100f:F1} cm",
            };
        }

        /// <summary>
        /// Normal equations for z = a·x + b·y + c over the current inlier set, solved by Cramer's
        /// rule on the 3×3 system. Returns false when the patch is degenerate (collinear samples).
        /// </summary>
        bool SolveLeastSquares(int n, out float a, out float b, out float c)
        {
            double sxx = 0, sxy = 0, sx = 0, syy = 0, sy = 0, s1 = 0, sxz = 0, syz = 0, sz = 0;
            for (int i = 0; i < n; i++)
            {
                if (!_inlier[i]) continue;
                double x = _x[i], y = _y[i], z = _z[i];
                sxx += x * x; sxy += x * y; sx += x;
                syy += y * y; sy += y; s1 += 1;
                sxz += x * z; syz += y * z; sz += z;
            }
            // | sxx sxy sx | |a|   |sxz|
            // | sxy syy sy | |b| = |syz|
            // | sx  sy  s1 | |c|   |sz |
            double det = sxx * (syy * s1 - sy * sy) - sxy * (sxy * s1 - sy * sx) + sx * (sxy * sy - syy * sx);
            a = b = c = 0f;
            if (System.Math.Abs(det) < 1e-12) return false;
            double da = sxz * (syy * s1 - sy * sy) - sxy * (syz * s1 - sy * sz) + sx * (syz * sy - syy * sz);
            double db = sxx * (syz * s1 - sy * sz) - sxz * (sxy * s1 - sy * sx) + sx * (sxy * sz - syz * sx);
            double dc = sxx * (syy * sz - syz * sy) - sxy * (sxy * sz - syz * sx) + sxz * (sxy * sy - syy * sx);
            a = (float)(da / det); b = (float)(db / det); c = (float)(dc / det);
            return true;
        }
    }
}
