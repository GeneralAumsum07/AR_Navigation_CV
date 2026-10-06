using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>Outcome of checking a candidate plane against a depth image.</summary>
    public struct DepthValidationResult
    {
        /// <summary>False when depth could not be used at all (stale, unsupported, too few samples).</summary>
        public bool depthUsed;
        /// <summary>Signed median residual (depth point minus plane, along the normal) in metres.</summary>
        public float medianResidualMeters;
        /// <summary>Fraction of accepted samples whose |residual| is under the inlier threshold.</summary>
        public float inlierFraction;
        public int validSamples;
        public int rejectedSamples;
        public string reason;
    }

    /// <summary>
    /// Uses raw depth to VALIDATE plane geometry rather than to replace it. The plane gives a
    /// clean perpendicular distance; depth tells us whether that plane actually coincides with
    /// the surface the camera sees. Averaging the two would blend correlated estimates and hide
    /// the failure modes (glass, doors, a cupboard front) we want to surface.
    /// </summary>
    public sealed class DepthValidator
    {
        readonly List<float> _residuals = new List<float>(128);

        public DepthValidationResult Validate(WallCandidate c, DepthFrame frame, MeasurementConfig cfg, double now)
        {
            var result = new DepthValidationResult { medianResidualMeters = float.NaN, inlierFraction = float.NaN };

            if (frame == null || !frame.IsUsable)
            {
                result.reason = "no depth image";
                return result;
            }
            if (now - frame.timestamp > cfg.depthMaxAgeSeconds)
            {
                result.reason = $"depth stale ({now - frame.timestamp:F2}s)";
                return result;
            }
            if (c.boundary.Count < 3)
            {
                result.reason = "degenerate polygon";
                return result;
            }

            // Sample a grid of depth PIXELS and cast each one onto the candidate plane. Sampling
            // in image space (rather than over the polygon's bounding box) gives samples that are
            // uniform across what the camera actually sees, so a huge wall viewed obliquely still
            // yields enough samples, and every sample is inside the view by construction.
            _residuals.Clear();
            int rejected = 0;
            int n = Mathf.Max(2, cfg.depthSampleGridSize);
            Vector3 camPos = frame.cameraPose.position;
            Quaternion camRot = frame.cameraPose.rotation;
            for (int iy = 0; iy < n; iy++)
            {
                for (int ix = 0; ix < n; ix++)
                {
                    int u = Mathf.Clamp(Mathf.RoundToInt((ix + 0.5f) / n * frame.width), 0, frame.width - 1);
                    int v = Mathf.Clamp(Mathf.RoundToInt((iy + 0.5f) / n * frame.height), 0, frame.height - 1);

                    // Ray through this pixel, in the depth frame's own camera pose.
                    var dirCam = new Vector3(
                        (u - frame.intrinsics.cx) / frame.intrinsics.fx,
                        -(v - frame.intrinsics.cy) / frame.intrinsics.fy,
                        1f);
                    Vector3 dir = camRot * dirCam;
                    float denom = Vector3.Dot(c.normal, dir);
                    if (Mathf.Abs(denom) < 1e-6f) continue;
                    float t = Vector3.Dot(c.position - camPos, c.normal) / denom;
                    if (t <= 0f) continue;
                    Vector3 onPlane = camPos + dir * t;
                    Vector3 local = c.WorldToLocal(onPlane);
                    if (!WallGeometry.PointInPolygon(c.boundary, new Vector2(local.x, local.z))) continue;

                    float d = frame.DepthAt(u, v);
                    if (float.IsNaN(d) || d < cfg.depthMinValidMeters || d > cfg.depthMaxValidMeters)
                    {
                        rejected++;
                        continue;
                    }
                    if (frame.ConfidenceAt(u, v) < cfg.depthMinConfidence)
                    {
                        rejected++;
                        continue;
                    }

                    // Reconstruct the observed surface point from the depth pixel and measure how
                    // far it sits off the plane along the plane normal. Sign is kept so a
                    // systematic bias (plane floating in front of the wall) is visible in logs.
                    Vector3 observed = frame.Unproject(u, v, d);
                    float residual = Vector3.Dot(observed - c.position, c.normal);
                    _residuals.Add(residual);
                }
            }

            result.validSamples = _residuals.Count;
            result.rejectedSamples = rejected;
            if (_residuals.Count < cfg.depthMinValidSamples)
            {
                result.reason = $"only {_residuals.Count} valid depth samples (need {cfg.depthMinValidSamples})";
                return result;
            }

            _residuals.Sort();
            int mid = _residuals.Count / 2;
            float median = (_residuals.Count % 2 == 0)
                ? 0.5f * (_residuals[mid - 1] + _residuals[mid])
                : _residuals[mid];

            int inliers = 0;
            for (int i = 0; i < _residuals.Count; i++)
                if (Mathf.Abs(_residuals[i]) <= cfg.depthInlierThresholdMeters) inliers++;

            result.depthUsed = true;
            result.medianResidualMeters = median;
            result.inlierFraction = (float)inliers / _residuals.Count;
            result.reason = $"median residual {median * 100f:F1} cm, {inliers}/{_residuals.Count} inliers";
            return result;
        }

    }
}
