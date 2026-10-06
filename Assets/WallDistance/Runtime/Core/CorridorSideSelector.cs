using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Left/right corridor walls and corridor width from the wall map (spec §5.5).
    ///
    /// "Side" is relative to the phone's FLOOR heading (its forward vector flattened onto the
    /// floor), not its screen: holding the phone at 45° still has a clear forward, while
    /// pointing it at the floor does not (NoHeading). Entry/exit angles differ (hysteresis),
    /// so walking sway does not flicker the choice. Each side has its own filter keyed by wall id.
    /// </summary>
    public sealed class CorridorSideSelector
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        readonly MeasurementFilter _leftFilter = new MeasurementFilter(), _rightFilter = new MeasurementFilter();
        string _leftId, _rightId;

        public void Reset()
        {
            _leftFilter.Reset();
            _rightFilter.Reset();
            _leftId = _rightId = null;
        }

        public void Measure(string sessionId, double now, Pose camera, Matrix4x4 worldToClip, Vector3 up,
            IReadOnlyList<WallTrack> walls, MeasurementConfig cfg, out WallReading left, out WallReading right, out float width)
        {
            width = float.NaN;
            Vector3 fwd = camera.rotation * Vector3.forward;
            if (Mathf.Abs(Vector3.Dot(fwd, up)) > Mathf.Cos(cfg.sideNoHeadingDeg * Mathf.Deg2Rad)
                || !Horizontal.TryDirection(fwd, up, out Vector3 heading))
            {
                // Pointing at the floor or ceiling: left and right are undefined. Forget the
                // current walls so the next valid heading starts cleanly.
                Reset();
                left = WallReading.Invalid(MeasurementKind.CorridorLeft, FailureReason.NoHeading, sessionId, now, camera);
                right = WallReading.Invalid(MeasurementKind.CorridorRight, FailureReason.NoHeading, sessionId, now, camera);
                return;
            }

            WallTrack bestL = null, bestR = null;
            float dL = float.PositiveInfinity, dR = float.PositiveInfinity;
            if (walls != null)
            {
                for (int i = 0; i < walls.Count; i++)
                {
                    var w = walls[i];
                    if (now - w.lastSeen > cfg.sideMaxAgeSeconds) continue;
                    float signed = w.SignedDistance(camera.position);
                    float dist = Mathf.Abs(signed);
                    if (dist > cfg.sideMaxDistanceMeters || dist < 1e-4f) continue;
                    // Normal pointing at the camera; -toward points from the camera to the wall.
                    Vector3 toward = signed >= 0f ? w.normal : -w.normal;
                    bool isRight = Vector3.Dot(up, Vector3.Cross(heading, -toward)) > 0f;
                    // The current wall on this side may stay until the wider exit angle.
                    float limit = w.id == (isRight ? _rightId : _leftId) ? cfg.sideExitAngleDeg : cfg.sideEnterAngleDeg;
                    float cosLimit = Mathf.Cos(limit * Mathf.Deg2Rad);
                    if (Mathf.Abs(Vector3.Dot(w.direction, heading)) < cosLimit) continue;
                    // The camera must be alongside the wall (within its extent plus a margin).
                    float along = Vector3.Dot(camera.position - w.origin, w.direction);
                    if (along < w.extentMin - cfg.sideExtentMarginMeters || along > w.extentMax + cfg.sideExtentMarginMeters) continue;
                    if (isRight) { if (dist < dR) { dR = dist; bestR = w; } }
                    else if (dist < dL) { dL = dist; bestL = w; }
                }
            }

            left = Read(sessionId, now, camera, worldToClip, bestL, dL, MeasurementKind.CorridorLeft, _leftFilter, cfg);
            right = Read(sessionId, now, camera, worldToClip, bestR, dR, MeasurementKind.CorridorRight, _rightFilter, cfg);
            _leftId = bestL?.id;
            _rightId = bestR?.id;

            if (left.isValid && right.isValid
                && Mathf.Abs(Vector3.Dot(left.surfaceNormal, right.surfaceNormal)) >= Mathf.Cos(cfg.corridorParallelToleranceDeg * Mathf.Deg2Rad))
                width = left.distanceMeters + right.distanceMeters;
        }

        static WallReading Read(string sessionId, double now, Pose camera, Matrix4x4 worldToClip, WallTrack w, float dist,
            MeasurementKind kind, MeasurementFilter filter, MeasurementConfig cfg)
        {
            if (w == null)
            {
                filter.Reset();
                return WallReading.Invalid(kind, FailureReason.NoWallOnSide, sessionId, now, camera);
            }
            Vector3 toward = w.SignedDistance(camera.position) >= 0f ? w.normal : -w.normal;
            // The filter resets itself when the wall id changes, so two walls are never blended.
            float filtered = filter.Update(sessionId, w.id, dist, now, cfg.filterTimeConstantSeconds);
            var source = w.Source(now, cfg.detection.sourceWindowSeconds);
            Vector3 foot = camera.position - toward * dist;
            var r = new WallReading
            {
                isValid = true,
                kind = kind,
                distanceMeters = filtered,
                rawDistanceMeters = dist,
                source = source,
                quality = QualityFor(source, w.crossChecked),
                failure = FailureReason.None,
                sessionId = sessionId,
                candidateId = w.id,
                timestamp = now,
                cameraPose = camera,
                surfacePoint = foot,
                surfaceNormal = toward,
                depthResidualMeters = float.NaN,
                depthInlierFraction = float.NaN,
                sourceChain = source + (w.crossChecked ? "+ARPlane" : ""),
                // Side walls are usually out of view: say how old the evidence is.
                qualityReason = InView(foot, worldToClip)
                    ? string.Format(Inv, "{0} observations", w.observations)
                    : string.Format(Inv, "out of view, last seen {0:F1} s ago", now - w.lastSeen),
            };
            // Plan deviation D4: the same tested-range rule as aimed/nearest, prepended to the reason.
            const float eps = 1e-4f;
            if (r.distanceMeters < cfg.minTestedRangeMeters - eps || r.distanceMeters > cfg.maxTestedRangeMeters + eps)
            {
                r.quality = QualityLabel.OutOfTestedRange;
                r.qualityReason = string.Format(Inv, "{0:F2} m is outside the {1}-{2} m validated range; {3}",
                    r.distanceMeters, cfg.minTestedRangeMeters, cfg.maxTestedRangeMeters, r.qualityReason);
            }
            return r;
        }

        static bool InView(Vector3 p, Matrix4x4 worldToClip)
        {
            Vector4 c = worldToClip * new Vector4(p.x, p.y, p.z, 1f);
            return c.w > 0f && Mathf.Abs(c.x) <= c.w && Mathf.Abs(c.y) <= c.w;
        }

        /// <summary>The quality label a reading from this source earns (spec §5.4).</summary>
        public static QualityLabel QualityFor(MeasurementSource source, bool crossChecked)
        {
            if (crossChecked) return QualityLabel.CrossChecked;
            switch (source)
            {
                case MeasurementSource.FloorEdge: return QualityLabel.EdgeConfirmed;
                case MeasurementSource.LearnedDepth: return QualityLabel.LearnedEstimate;
                case MeasurementSource.PlaneOnly: return QualityLabel.PlaneEstimate;
                case MeasurementSource.AssistedFloor: return QualityLabel.AssistedEstimate;
                case MeasurementSource.DepthOnly: return QualityLabel.DepthEstimate;
                case MeasurementSource.PlaneDepthValidated: return QualityLabel.DepthValidated;
                default: return QualityLabel.Unavailable;
            }
        }
    }
}
