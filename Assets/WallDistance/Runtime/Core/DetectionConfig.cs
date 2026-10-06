using System;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// How the network output relates to metric depth. Depth Anything V2 is trained as relative
    /// inverse depth, but the quantised export may not preserve that; Phase 1 measures both on
    /// real frames and fixes this once (spec §6) - it is never switched at runtime.
    /// </summary>
    public enum DepthParameterisation
    {
        /// <summary>1/z = s·d + t.</summary>
        AffineInverseDepth,
        /// <summary>z = s·d + t.</summary>
        AffineDepth,
    }

    /// <summary>
    /// Every threshold of the learned-depth wall detector. Nested inside MeasurementConfig so the
    /// CSV header (JsonUtility dump) records the exact values a benchmark ran with. Defaults are
    /// the spec's starting points, not validated constants.
    /// </summary>
    [Serializable]
    public class DetectionConfig
    {
        [Header("Floor alignment (spec §5.2, §6)")]
        public DepthParameterisation parameterisation = DepthParameterisation.AffineInverseDepth;
        [Tooltip("Pixel stride when sampling floor pixels for the scale fit.")]
        public int alignStride = 4;
        public float floorMinDepthMeters = 0.3f;
        public float floorMaxDepthMeters = 10f;
        public int alignRansacIterations = 64;
        [Tooltip("A floor pixel is an inlier when its aligned depth is within this fraction of the floor-plane depth. " +
                 "Deliberately wider than maxAlignResidual so the median residual check below is meaningful.")]
        public float alignInlierRelative = 0.06f;
        [Tooltip("Refit band is this multiple of the median residual, capped by alignInlierRelative.")]
        public float alignRefitResidualMultiplier = 3f;
        [Tooltip("Numerical floor for the adaptive refit band; keeps exact floor pixels despite float roundoff.")]
        public float alignRefitMinRelative = 0.0001f;
        [Tooltip("Fewer floor inliers than this discards the frame (spec §6).")]
        public int minFloorInliers = 500;
        [Tooltip("Median relative residual of the inliers above this discards the frame (spec §6: 3%).")]
        public float maxAlignResidual = 0.03f;
        [Tooltip("No-floor fallback: confident raw-depth samples needed (spec §6).")]
        public int minMetricSamples = 200;
        [Tooltip("Raw-depth confidence byte for the fallback; 128/255 is the spec's 0.5.")]
        public byte metricMinConfidence = 128;

        [Header("Vertical planes")]
        public int extractStride = 4;
        public float wallMinDepthMeters = 0.3f;
        public float wallMaxDepthMeters = 8f;
        [Tooltip("Points lower than this above the floor are floor clutter, not wall.")]
        public float floorClearanceMeters = 0.05f;
        public int maxPlanes = 4;
        public int planeRansacIterations = 128;
        public float planeInlierMeters = 0.08f;
        [Tooltip("Numerical refit floor; noisy observations use the larger median-residual band.")]
        public float planeRefitMinMeters = 0.001f;
        public float planeRefitResidualMultiplier = 2.5f;
        public int minPlaneInliers = 150;
        public float minHeightSpanMeters = 0.3f;
        public float minLengthMeters = 0.4f;
        [Tooltip("RANSAC pairs closer than this give unstable line directions.")]
        public float minPairSeparationMeters = 0.2f;

        [Header("Base edge")]
        public int edgeBandPixels = 12;
        [Tooltip("Search band is also capped to the image length of this floor distance (plan deviation D8).")]
        public float edgeMaxShiftMeters = 0.08f;
        [Tooltip("Minimum luma step (0-255) across the base line.")]
        public float edgeMinContrast = 12f;
        public float edgeSampleSpacingMeters = 0.1f;
        public int edgeMaxSamples = 64;
        public float edgeMinSnapFraction = 0.6f;
        public float edgeMaxRmsMeters = 0.02f;
        public float edgeMaxAngleChangeDeg = 10f;

        [Header("Wall map")]
        public float associateMaxAngleDeg = 8f;
        public float associateMaxOffsetMeters = 0.15f;
        public float associateMaxGapMeters = 0.5f;
        public float trackMaxAgeSeconds = 30f;
        public float minOffsetSigmaMeters = 0.01f;
        public float minAngleSigmaDeg = 0.5f;
        [Tooltip("Track information is capped at 1/σ² of this, so the map keeps adapting to anchor corrections.")]
        public float maxOffsetInfoSigmaMeters = 0.005f;
        public float maxAngleInfoSigmaDeg = 0.25f;
        [Tooltip("UI extent of map walls; a display height, not a measurement (spec §5.2).")]
        public float nominalWallHeightMeters = 2.4f;
        [Tooltip("A source counts as current for a track if it observed it within this window.")]
        public float sourceWindowSeconds = 2f;
        public float arPlaneObserveIntervalSeconds = 0.1f;

        [Header("Staleness")]
        public float staleAfterSeconds = 1f;
    }
}
