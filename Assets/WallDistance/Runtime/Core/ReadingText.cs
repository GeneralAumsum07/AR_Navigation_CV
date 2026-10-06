using System.Globalization;

namespace WallDistance.Core
{
    /// <summary>
    /// Every user-facing string for readings, in one tested place. Failure texts are
    /// instructions ("point at the floor"), not diagnoses, because the person holding the
    /// phone is the only one who can fix them.
    /// </summary>
    public static class ReadingText
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Quality(QualityLabel q)
        {
            switch (q)
            {
                case QualityLabel.DepthValidated: return "Depth validated";
                case QualityLabel.PlaneEstimate: return "Plane estimate";
                case QualityLabel.DepthEstimate: return "Depth estimate (no plane)";
                case QualityLabel.AssistedEstimate: return "Assisted floor geometry";
                case QualityLabel.Unreliable: return "Unreliable";
                case QualityLabel.OutOfTestedRange: return "Out of tested range";
                case QualityLabel.LearnedEstimate: return "Learned depth estimate";
                case QualityLabel.EdgeConfirmed: return "Edge confirmed";
                case QualityLabel.CrossChecked: return "Cross-checked";
                default: return "Unavailable";
            }
        }

        public static string Failure(FailureReason f)
        {
            switch (f)
            {
                case FailureReason.NoWallUnderCrosshair: return "aim at a wall";
                case FailureReason.NoWallInView: return "scan slowly";
                case FailureReason.TrackingLost: return "tracking lost";
                case FailureReason.CandidateLost: return "wall lost";
                case FailureReason.SessionNotTracking: return "AR not tracking";
                case FailureReason.NoFloor: return "point at the floor for a moment";
                case FailureReason.AlignmentFailed: return "show more floor";
                case FailureReason.InferenceUnavailable: return "ML depth unavailable";
                case FailureReason.InferenceStale: return "detecting…";
                case FailureReason.NoHeading: return "hold the phone more upright";
                case FailureReason.NoWallOnSide: return "no wall seen on this side";
                default: return "not ready";
            }
        }

        /// <summary>"L 0.84 m | R 1.12 m | W 1.96 m"; an em dash for anything not measured.</summary>
        public static string Sides(WallReading left, WallReading right, float widthMeters) =>
            "L " + Metres(left) + " | R " + Metres(right) + " | W " +
            (float.IsNaN(widthMeters) ? "—" : widthMeters.ToString("F2", Inv) + " m");

        /// <summary>What to do about missing sides; empty when both are measured.</summary>
        public static string SidesHint(WallReading left, WallReading right)
        {
            if (left.isValid && right.isValid) return "";
            if (!left.isValid && !right.isValid)
                return left.failure == right.failure
                    ? Failure(left.failure)
                    : "left: " + Failure(left.failure) + " · right: " + Failure(right.failure);
            return !left.isValid ? "left: " + Failure(left.failure) : "right: " + Failure(right.failure);
        }

        static string Metres(WallReading r) => r.isValid && !float.IsNaN(r.distanceMeters)
            ? r.distanceMeters.ToString("F2", Inv) + " m"
            : "—";
    }
}
