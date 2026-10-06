using System;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>Which of the two distances a reading describes.</summary>
    public enum MeasurementKind
    {
        /// <summary>Perpendicular distance from the camera to the wall under the crosshair.</summary>
        Aimed,
        /// <summary>Shortest distance to any eligible wall currently inside the camera's view.</summary>
        NearestObserved,
        /// <summary>Horizontal perpendicular distance to the corridor wall left of the phone's heading (spec §5.5).</summary>
        CorridorLeft,
        /// <summary>Horizontal perpendicular distance to the corridor wall right of the phone's heading.</summary>
        CorridorRight,
    }

    /// <summary>
    /// Where the number came from. Values are appended to keep stored integers stable, so the
    /// order is NOT a trust ranking - each value documents its own trust.
    /// </summary>
    public enum MeasurementSource
    {
        /// <summary>Nothing usable this frame.</summary>
        None,
        /// <summary>AR plane geometry only; depth was unavailable or stale.</summary>
        PlaneOnly,
        /// <summary>Plane geometry, cross-checked against raw depth samples that agreed with it.</summary>
        PlaneDepthValidated,
        /// <summary>
        /// No AR plane under the crosshair; a plane was fitted to the raw depth patch instead.
        /// Single-source, no cross-check - ranks BELOW PlaneOnly in trust, despite being useful.
        /// </summary>
        DepthOnly,
        /// <summary>Wall base selected by the user on an AR-tracked floor; not automatic wall recognition.</summary>
        AssistedFloor,
        /// <summary>Wall plane from floor-aligned monocular network depth only.</summary>
        LearnedDepth,
        /// <summary>Learned wall whose base line was snapped to image edges on the tracked floor. Geometric; ranks above LearnedDepth.</summary>
        FloorEdge,
    }

    /// <summary>
    /// Heuristic reliability label shown to the user. These are NOT statistical accuracy
    /// guarantees - they encode which checks passed, and the thresholds live in
    /// <see cref="MeasurementConfig"/> so a benchmark session can log them.
    /// </summary>
    public enum QualityLabel
    {
        /// <summary>No reading. See <see cref="FailureReason"/>.</summary>
        Unavailable,
        /// <summary>Plane-only reading; treat as a lower-quality estimate.</summary>
        PlaneEstimate,
        /// <summary>Depth samples inside the polygon agreed with the plane.</summary>
        DepthValidated,
        /// <summary>Depth and plane disagreed substantially, or too few valid samples. Do not trust.</summary>
        Unreliable,
        /// <summary>Reading exists but lies outside the validated 0.5-3 m range.</summary>
        OutOfTestedRange,
        /// <summary>Depth-only fit under the crosshair (no AR plane). Usable, but un-cross-checked.</summary>
        DepthEstimate,
        AssistedEstimate,
        /// <summary>Learned-depth wall only; scale comes from the floor fit.</summary>
        LearnedEstimate,
        /// <summary>Wall base found as an image edge on the tracked floor.</summary>
        EdgeConfirmed,
        /// <summary>Two independent sources (learned vs ARCore plane or raw depth) agree within crossCheckToleranceMeters.</summary>
        CrossChecked,
    }

    /// <summary>Why a reading is invalid. Only meaningful when <see cref="WallReading.isValid"/> is false.</summary>
    public enum FailureReason
    {
        None,
        /// <summary>AR session not running (permission denied, unsupported device, still initialising).</summary>
        SessionNotTracking,
        /// <summary>Tracking was lost; the last value must not be shown.</summary>
        TrackingLost,
        /// <summary>The crosshair ray hits no detected vertical plane polygon.</summary>
        NoWallUnderCrosshair,
        /// <summary>No tracked vertical plane intersects the camera view.</summary>
        NoWallInView,
        /// <summary>The candidate under the crosshair is no longer tracked or was merged away.</summary>
        CandidateLost,
        /// <summary>No floor plane and too little confident raw depth to fix the depth scale.</summary>
        NoFloor,
        /// <summary>Floor fit rejected: too few floor inliers or residual above 3%.</summary>
        AlignmentFailed,
        /// <summary>ML depth backend could not start on this device.</summary>
        InferenceUnavailable,
        /// <summary>No detection for over 1 s and the map has no wall for this reading.</summary>
        InferenceStale,
        /// <summary>Phone points within 20° of straight up/down, so left/right are undefined.</summary>
        NoHeading,
        /// <summary>No map wall qualifies on this side (spec §5.5).</summary>
        NoWallOnSide,
    }

    /// <summary>
    /// One distance measurement. Immutable value type so consumers can cache snapshots safely.
    /// Distances are metres, poses are in the AR session's coordinate frame - NOT campus-map
    /// coordinates. A session reset changes <see cref="sessionId"/> and the frame identity.
    /// </summary>
    [Serializable]
    public struct WallReading
    {
        public bool isValid;
        public MeasurementKind kind;
        /// <summary>Filtered perpendicular distance in metres. Never zero when invalid - check <see cref="isValid"/>.</summary>
        public float distanceMeters;
        /// <summary>Unfiltered distance this frame, for logging/benchmarks.</summary>
        public float rawDistanceMeters;
        public MeasurementSource source;
        public QualityLabel quality;
        public FailureReason failure;
        /// <summary>Identifies the AR session; changes on session reset.</summary>
        public string sessionId;
        /// <summary>Identifies the wall candidate (AR trackable id). Changes when planes merge or are replaced.</summary>
        public string candidateId;
        /// <summary>Seconds since app start when the measurement was produced.</summary>
        public double timestamp;
        /// <summary>Camera pose used for the measurement (session frame).</summary>
        public Pose cameraPose;
        /// <summary>Foot of the perpendicular on the wall (aimed) or nearest polygon point (nearest).</summary>
        public Vector3 surfacePoint;
        /// <summary>Unit wall normal, pointing toward the camera side.</summary>
        public Vector3 surfaceNormal;
        /// <summary>Median signed depth-vs-plane residual in metres (NaN when depth was not used).</summary>
        public float depthResidualMeters;
        /// <summary>Fraction of depth samples that were valid and within the inlier threshold (NaN when depth not used).</summary>
        public float depthInlierFraction;
        /// <summary>Human-readable reason for the quality label, e.g. "depth disagreed by 0.31 m".</summary>
        public string qualityReason;
        /// <summary>Sources behind the number, e.g. "FloorEdge+ARPlane" (CSV aimed_source_chain). Empty when invalid.</summary>
        public string sourceChain;

        public static WallReading Invalid(MeasurementKind kind, FailureReason reason, string sessionId, double timestamp, Pose cameraPose)
        {
            return new WallReading
            {
                isValid = false,
                kind = kind,
                distanceMeters = float.NaN,
                rawDistanceMeters = float.NaN,
                source = MeasurementSource.None,
                quality = QualityLabel.Unavailable,
                failure = reason,
                sessionId = sessionId,
                candidateId = null,
                timestamp = timestamp,
                cameraPose = cameraPose,
                depthResidualMeters = float.NaN,
                depthInlierFraction = float.NaN,
                qualityReason = reason.ToString(),
                sourceChain = "",
            };
        }
    }

    /// <summary>All readings for one frame plus session context.</summary>
    [Serializable]
    public struct WallDistanceSnapshot
    {
        public WallReading aimed;
        public WallReading nearest;
        /// <summary>Corridor wall to the left of the phone's floor heading (from the wall map).</summary>
        public WallReading left;
        /// <summary>Corridor wall to the right of the phone's floor heading (from the wall map).</summary>
        public WallReading right;
        /// <summary>left + right when both are valid and parallel within 10°; NaN otherwise. Always set by the engine.</summary>
        public float corridorWidthMeters;
        public bool sessionTracking;
        public bool depthSupported;
        public double timestamp;
    }

    /// <summary>
    /// Every tunable threshold in one place. Values are logged with each CSV benchmark session so
    /// results can be reproduced. Defaults are starting points, not validated constants.
    /// </summary>
    [Serializable]
    public class MeasurementConfig
    {
        // Explicit compatibility mode: restores dense detections without implying metric accuracy.
        public bool allowDenseDetection;
        [Header("Range")]
        [Tooltip("Readings below this (metres) are flagged OutOfTestedRange.")]
        public float minTestedRangeMeters = 0.5f;
        [Tooltip("Readings above this (metres) are flagged OutOfTestedRange.")]
        public float maxTestedRangeMeters = 3.0f;

        [Header("Depth validation")]
        [Tooltip("Depth image older than this (seconds) no longer validates a reading.")]
        public float depthMaxAgeSeconds = 0.5f;
        [Tooltip("Confidence byte (0-255) below which a depth sample is rejected.")]
        public byte depthMinConfidence = 128;
        [Tooltip("A depth sample whose |residual| to the plane is under this (metres) is an inlier.")]
        public float depthInlierThresholdMeters = 0.05f;
        [Tooltip("Minimum inlier fraction to label DepthValidated.")]
        public float depthMinInlierFraction = 0.6f;
        [Tooltip("Minimum number of valid depth samples inside the polygon to use depth at all.")]
        public int depthMinValidSamples = 12;
        [Tooltip("|median residual| above this (metres) marks the reading Unreliable.")]
        public float depthDisagreementThresholdMeters = 0.10f;
        [Tooltip("Depth pixels sampled per image axis (grid) for validation; each is ray-cast onto the candidate polygon.")]
        public int depthSampleGridSize = 12;
        [Tooltip("Depth samples nearer than this (metres) are considered invalid sensor returns.")]
        public float depthMinValidMeters = 0.1f;
        [Tooltip("Depth samples beyond this (metres) are considered invalid sensor returns.")]
        public float depthMaxValidMeters = 8.0f;

        [Header("Depth-only fallback (no AR plane under crosshair)")]
        [Tooltip("Half-side of the square depth-pixel patch around the crosshair used for the plane fit.")]
        public int depthOnlyPatchRadiusPixels = 10;
        [Tooltip("Minimum valid samples (and inliers after refit) for a depth-only fit to be reported.")]
        public int depthOnlyMinInliers = 60;
        [Tooltip("Outlier-rejection refit rounds after the initial least-squares fit.")]
        public int depthOnlyRefitRounds = 2;
        [Tooltip("Maximum RMS residual (metres) of inliers for the patch to count as one plane.")]
        public float depthOnlyMaxRmsMeters = 0.02f;
        [Tooltip("Fitted normals tilted more than this (degrees) from vertical are floors/ceilings/desks, not walls.")]
        public float depthOnlyMaxTiltFromVerticalDeg = 20f;

        [Header("Filtering")]
        [Tooltip("Time constant (seconds) of the per-candidate exponential filter.")]
        public float filterTimeConstantSeconds = 0.25f;

        [Header("Candidate eligibility")]
        [Tooltip("Minimum polygon area (m^2) for a vertical plane to count as a wall candidate.")]
        public float minCandidateAreaSquareMeters = 0.2f;
        [Header("Corridor sides (spec §5.5)")]
        [Tooltip("Within this many degrees of straight up/down there is no heading.")]
        public float sideNoHeadingDeg = 20f;
        [Tooltip("A wall becomes a side wall when its floor line is within this angle of the heading.")]
        public float sideEnterAngleDeg = 30f;
        [Tooltip("A current side wall stops being one above this angle (hysteresis).")]
        public float sideExitAngleDeg = 40f;
        public float sideExtentMarginMeters = 1.0f;
        public float sideMaxAgeSeconds = 10f;
        public float sideMaxDistanceMeters = 6f;
        public float corridorParallelToleranceDeg = 10f;

        [Header("Learned detection")]
        [Tooltip("Two sources agreeing within this distance earn CrossChecked.")]
        public float crossCheckToleranceMeters = 0.05f;
        public DetectionConfig detection = new DetectionConfig();
    }
}
