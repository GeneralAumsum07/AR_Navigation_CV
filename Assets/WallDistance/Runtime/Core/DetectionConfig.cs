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
        [Tooltip("Along-wall support gaps larger than this stay as separate observed sections.")]
        public float planeSplitGapMeters = 0.5f;
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
        [Tooltip("For learned walls the offset gate grows to this fraction of the viewing range. Monocular "
                 + "depth jitters with range (the 10-07 orange wall read 2.53-2.95 m frame to frame), so a fixed "
                 + "15 cm gate stored one wall as dozens of copies.")]
        public float associateOffsetFractionOfRange = 0.12f;
        [Tooltip("Drop observations whose base line passes closer than this to the camera: a wall cannot run "
                 + "through the person holding the phone. On 10-07 a bench seen end-on did, and read 0.04 m.")]
        public float minLinePassMeters = 0.3f;
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

        [Header("See-through removal")]
        [Tooltip("Pixel grid spacing for see-through rays in the 518² image (16 → ~1000 rays per wall).")]
        public int carvePixelStride = 16;
        [Tooltip("Only wall hits in this band above the floor count. Low hits are skipped: just behind a "
                 + "wall's base the floor is barely farther than the wall, so they cannot tell.")]
        public float carveMinHeightMeters = 0.5f;
        public float carveMaxHeightMeters = 2.0f;
        [Tooltip("Lower than wallMinDepthMeters on purpose: the phantom walls of 10-07 sat 0.1-0.3 m ahead.")]
        public float carveMinDepthMeters = 0.1f;
        [Tooltip("Seeing past the wall means measured depth > expected·(1 + fraction) + metres; within that "
                 + "band either side the wall is confirmed. Wide, because learned depth jitters ~15%.")]
        public float carveDepthFraction = 0.25f;
        public float carveDepthMeters = 0.2f;
        public int carveMinVisibleSamples = 6;
        [Tooltip("Fraction of the visible samples that must see past the wall for the frame to count against it.")]
        public float carveMinThroughRatio = 0.7f;
        [Tooltip("Consecutive see-through frames before removal, so one bad depth frame deletes nothing.")]
        public int carveFramesToRemove = 3;

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

        [Header("Staleness")]
        public float staleAfterSeconds = 1f;
    }
}
