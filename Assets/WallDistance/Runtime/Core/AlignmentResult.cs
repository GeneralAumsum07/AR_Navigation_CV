using System;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>A metric depth sample in inference-image space (from confident ARCore raw depth).</summary>
    public struct MetricSample
    {
        public Vector2 pixel;
        /// <summary>Z-depth along the inference camera's optical axis, metres.</summary>
        public float depthMeters;
    }

    /// <summary>
    /// Outcome of fitting the network output to metric depth. Kept even on failure (s, t and the
    /// residual are logged in the CSV), but only a successful result may be used to make walls.
    /// </summary>
    public sealed class AlignmentResult
    {
        public bool success;
        public FailureReason failure;
        public string reason = "";
        public DepthParameterisation parameterisation;
        public float s = float.NaN, t = float.NaN;
        /// <summary>Median relative depth error of the inliers (0.03 = 3%).</summary>
        public float residual = float.NaN;
        public int inliers, candidates;
        public bool usedMetricSamples;
        /// <summary>True when <see cref="floorMask"/> is meaningful (a floor plane was available).</summary>
        public bool hasFloorMask;
        /// <summary>Per inference pixel: aligned depth agrees with the floor plane, so it is floor, not wall.</summary>
        public bool[] floorMask = Array.Empty<bool>();

        /// <summary>Metric Z-depth for a network value; NaN where the model gives a non-physical depth.</summary>
        public float MetricDepth(float d) => Predict(parameterisation, s, t, d);

        internal static float Predict(DepthParameterisation p, float s, float t, float d)
        {
            if (float.IsNaN(d)) return float.NaN;
            float y = s * d + t;
            if (p == DepthParameterisation.AffineInverseDepth) return y > 1e-4f ? 1f / y : float.NaN;
            return y > 0f ? y : float.NaN;
        }

        internal void Reset(int pixels, DepthParameterisation p)
        {
            success = false;
            failure = FailureReason.None;
            reason = "";
            parameterisation = p;
            s = t = residual = float.NaN;
            inliers = candidates = 0;
            usedMetricSamples = false;
            hasFloorMask = false;
            if (floorMask.Length != pixels) floorMask = new bool[pixels];
            else Array.Clear(floorMask, 0, pixels);
        }
    }
}
