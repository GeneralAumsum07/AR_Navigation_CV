using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>Everything the engine needs for one frame, supplied by the AR adapter (or a test).</summary>
    public struct EngineInput
    {
        public string sessionId;
        public bool sessionTracking;
        public double now;
        public Pose cameraPose;
        /// <summary>Camera projection * view; used for "is this polygon in view" tests.</summary>
        public Matrix4x4 worldToClip;
        /// <summary>Ray through the crosshair (usually the screen centre), session space.</summary>
        public Ray crosshairRay;
        public IReadOnlyList<WallCandidate> candidates;
        /// <summary>Latest depth frame, or null. May be stale; the validator checks age.</summary>
        public DepthFrame depth;
        /// <summary>Latest DENSE depth frame for the depth-only fallback, or null. Raw depth is sparse on textureless walls.</summary>
        public DepthFrame denseDepth;
        public bool depthSupported;
        /// <summary>Wall-map tracks for the left/right readings. Null when no map is in use (assisted mode).</summary>
        public IReadOnlyList<WallTrack> mapWalls;
        /// <summary>Gravity up in session space; left at zero it means Vector3.up.</summary>
        public Vector3 up;
        /// <summary>Why learned detection is not producing walls (WallDetectionPipeline.DetectionFailure); None when it is.</summary>
        public FailureReason detectionFailure;
    }

    /// <summary>
    /// Pure orchestration: candidates + camera + depth in, two readings out. Holds only the two
    /// filters. No AR Foundation types, so PlayMode/EditMode tests drive it with fabricated
    /// geometry and the same code runs unchanged under an iOS provider later.
    /// </summary>
    public sealed class WallMeasurementEngine
    {
        public MeasurementConfig config;

        readonly MeasurementFilter _aimedFilter = new MeasurementFilter();
        readonly MeasurementFilter _nearestFilter = new MeasurementFilter();
        readonly DepthValidator _validator = new DepthValidator();
        readonly DepthPlaneFitter _fitter = new DepthPlaneFitter();
        readonly CorridorSideSelector _sides = new CorridorSideSelector();
        string _lastSessionId;

        public WallMeasurementEngine(MeasurementConfig cfg)
        {
            config = cfg ?? new MeasurementConfig();
        }

        /// <summary>Forget all filter state, e.g. on session reset.</summary>
        public void Reset()
        {
            _aimedFilter.Reset();
            _nearestFilter.Reset();
            _sides.Reset();
            _lastSessionId = null;
        }

        public WallDistanceSnapshot Update(in EngineInput input)
        {
            if (input.sessionId != _lastSessionId)
            {
                // Session frame identity changed - nothing measured before is comparable.
                Reset();
                _lastSessionId = input.sessionId;
            }

            var snap = new WallDistanceSnapshot
            {
                timestamp = input.now,
                sessionTracking = input.sessionTracking,
                depthSupported = input.depthSupported,
                // NaN, not 0: a zero-width corridor is a value, "no width" is not.
                corridorWidthMeters = float.NaN,
            };

            if (!input.sessionTracking)
            {
                // Tracking loss invalidates immediately; the filters are cleared so the first
                // reading after recovery is not blended with pre-loss history. The wall MAP is
                // kept (it lives outside the engine), so sides come straight back on recovery.
                _aimedFilter.Reset();
                _nearestFilter.Reset();
                _sides.Reset();
                snap.aimed = WallReading.Invalid(MeasurementKind.Aimed, FailureReason.TrackingLost, input.sessionId, input.now, input.cameraPose);
                snap.nearest = WallReading.Invalid(MeasurementKind.NearestObserved, FailureReason.TrackingLost, input.sessionId, input.now, input.cameraPose);
                snap.left = WallReading.Invalid(MeasurementKind.CorridorLeft, FailureReason.TrackingLost, input.sessionId, input.now, input.cameraPose);
                snap.right = WallReading.Invalid(MeasurementKind.CorridorRight, FailureReason.TrackingLost, input.sessionId, input.now, input.cameraPose);
                return snap;
            }

            snap.aimed = MeasureAimed(input);
            snap.nearest = MeasureNearest(input);
            if (config.allowDenseDetection) MeasureNearestDepth(input, ref snap.nearest);
            MeasureSides(input, ref snap);

            Substitute(ref snap.aimed, input.detectionFailure);
            Substitute(ref snap.nearest, input.detectionFailure);
            Substitute(ref snap.left, input.detectionFailure);
            Substitute(ref snap.right, input.detectionFailure);
            FillSourceChain(ref snap.aimed);
            FillSourceChain(ref snap.nearest);
            return snap;
        }

        void MeasureSides(in EngineInput input, ref WallDistanceSnapshot snap)
        {
            if (input.mapWalls == null)
            {
                _sides.Reset();
                snap.left = WallReading.Invalid(MeasurementKind.CorridorLeft, FailureReason.NoWallOnSide, input.sessionId, input.now, input.cameraPose);
                snap.right = WallReading.Invalid(MeasurementKind.CorridorRight, FailureReason.NoWallOnSide, input.sessionId, input.now, input.cameraPose);
                snap.corridorWidthMeters = float.NaN;
                return;
            }
            Vector3 up = input.up.sqrMagnitude > 0.5f ? input.up.normalized : Vector3.up;
            _sides.Measure(input.sessionId, input.now, input.cameraPose, input.worldToClip, up, input.mapWalls, config,
                out snap.left, out snap.right, out snap.corridorWidthMeters);
        }

        /// <summary>
        /// A generic "no wall" is replaced by WHY learned detection has none: the user can act on
        /// "point at the floor", not on "aim at a wall". Valid readings are never touched, and
        /// InferenceUnavailable is never stamped on readings (deviation D9).
        /// </summary>
        static void Substitute(ref WallReading r, FailureReason detection)
        {
            if (r.isValid) return;
            if (detection != FailureReason.NoFloor && detection != FailureReason.AlignmentFailed
                && detection != FailureReason.InferenceStale && detection != FailureReason.Calibrating) return;
            if (r.failure != FailureReason.NoWallUnderCrosshair && r.failure != FailureReason.NoWallInView && r.failure != FailureReason.NoWallOnSide) return;
            r.failure = detection;
            r.qualityReason = detection.ToString();
        }

        static void FillSourceChain(ref WallReading r)
        {
            if (r.isValid && string.IsNullOrEmpty(r.sourceChain)) r.sourceChain = r.source.ToString();
        }

        // ---------------------------------------------------------------- aimed

        WallReading MeasureAimed(in EngineInput input)
        {
            // Choose the eligible candidate whose polygon the crosshair ray hits FIRST along the
            // ray. Ray travel distance selects the target; perpendicular distance is what we
            // report - the two differ by 1/cos(view angle).
            WallCandidate best = null;
            float bestT = float.PositiveInfinity;
            Vector3 bestHit = default;
            for (int i = 0; i < input.candidates.Count; i++)
            {
                var c = input.candidates[i];
                if (!IsEligible(c)) continue;
                if (WallGeometry.RaycastPolygon(c, input.crosshairRay, out Vector3 hit, out float t) && t < bestT)
                {
                    best = c;
                    bestT = t;
                    bestHit = hit;
                }
            }

            if (best == null)
                return MeasureAimedDepthOnly(input);

            float raw = WallGeometry.PerpendicularDistance(best, input.cameraPose.position);
            float filtered = _aimedFilter.Update(input.sessionId, best.id, raw, input.now, config.filterTimeConstantSeconds);
            Vector3 foot = WallGeometry.ProjectOntoPlane(best, input.cameraPose.position);

            var reading = new WallReading
            {
                isValid = true,
                kind = MeasurementKind.Aimed,
                distanceMeters = filtered,
                rawDistanceMeters = raw,
                sessionId = input.sessionId,
                candidateId = best.id,
                timestamp = input.now,
                cameraPose = input.cameraPose,
                surfacePoint = foot,
                surfaceNormal = WallGeometry.NormalFacing(best, input.cameraPose.position),
            };
            ApplyQuality(ref reading, best, input);
            return reading;
        }

        /// <summary>
        /// Fallback when no AR plane polygon is under the crosshair: fit confidence-filtered RAW depth.
        /// ARCore's plane detector needs texture and parallax; a plain painted wall at close
        /// range often never yields one, while depth is still streaming. The reading is
        /// labelled DepthOnly/DepthEstimate so consumers can weight it accordingly.
        /// </summary>
        WallReading MeasureAimedDepthOnly(in EngineInput input)
        {
            // A dense image may be smooth but wrong in absolute scale. The 1 m sweep on
            // 2026-09-13 returned 7981 mm dense depth with no raw return at the centre.
            // Fit measured raw samples only; keep dense depth exclusively for diagnostics.
            var frame = config.allowDenseDetection && input.denseDepth != null &&
                input.now-input.denseDepth.timestamp <= config.depthMaxAgeSeconds ? input.denseDepth : input.depth;
            bool fresh = frame != null && frame.IsUsable && (input.now - frame.timestamp) <= config.depthMaxAgeSeconds;
            if (!fresh)
            {
                _aimedFilter.Reset();
                return WallReading.Invalid(MeasurementKind.Aimed, FailureReason.NoWallUnderCrosshair, input.sessionId, input.now, input.cameraPose);
            }

            if (!config.allowDenseDetection && (frame.confidence == null || frame.confidence.Length < frame.width * frame.height))
            {
                _aimedFilter.Reset();
                var missing = WallReading.Invalid(MeasurementKind.Aimed, FailureReason.NoWallUnderCrosshair, input.sessionId, input.now, input.cameraPose);
                missing.qualityReason = "Raw depth confidence unavailable; scan a textured wall or its edges";
                return missing;
            }

            // Locate the crosshair in depth-pixel space. The ray is in session space; project a
            // point along it with the depth frame's own camera pose so a frame-old pose is honoured.
            Vector3 probe = input.crosshairRay.origin + input.crosshairRay.direction * 1f;
            if (!frame.TryProject(probe, out int u, out int v, out _))
            {
                _aimedFilter.Reset();
                return WallReading.Invalid(MeasurementKind.Aimed, FailureReason.NoWallUnderCrosshair, input.sessionId, input.now, input.cameraPose);
            }

            var fit = _fitter.Fit(frame, u, v, config);
            if (!fit.success)
            {
                _aimedFilter.Reset();
                var inv = WallReading.Invalid(MeasurementKind.Aimed, FailureReason.NoWallUnderCrosshair, input.sessionId, input.now, input.cameraPose);
                inv.qualityReason = fit.reason;
                return inv;
            }

            // The fit is relative to the depth frame's pose; re-express the distance from the
            // CURRENT camera position so a moving user is not shown a stale number.
            float raw = Mathf.Abs(Vector3.Dot(input.cameraPose.position - fit.footPoint, fit.worldNormal));
            float filtered = _aimedFilter.Update(input.sessionId, DepthOnlyCandidateId, raw, input.now, config.filterTimeConstantSeconds);

            var reading = new WallReading
            {
                isValid = true,
                kind = MeasurementKind.Aimed,
                distanceMeters = filtered,
                rawDistanceMeters = raw,
                source = MeasurementSource.DepthOnly,
                quality = QualityLabel.DepthEstimate,
                sessionId = input.sessionId,
                candidateId = DepthOnlyCandidateId,
                timestamp = input.now,
                cameraPose = input.cameraPose,
                surfacePoint = input.cameraPose.position - fit.worldNormal * raw,
                surfaceNormal = fit.worldNormal,
                depthResidualMeters = fit.rmsResidualMeters,
                depthInlierFraction = fit.sampleCount > 0 ? (float)fit.inlierCount / fit.sampleCount : float.NaN,
                qualityReason = fit.reason,
            };
            ApplyRangeLabel(ref reading);
            return reading;
        }

        /// <summary>Synthetic candidate id for the depth-only path, so the filter resets on the switch to/from a real plane.</summary>
        const string DepthOnlyCandidateId = "depth-only";

        void MeasureNearestDepth(in EngineInput input, ref WallReading nearest)
        {
            var frame = input.denseDepth;
            if (frame == null || !frame.IsUsable || input.now-frame.timestamp > config.depthMaxAgeSeconds) return;
            // Compare a grid of observed patches, not an imaginary complete room. A fitted
            // patch is only a possible wall; dense-depth scale errors also affect this ranking.
            for (int y=1; y<=3; y++) for (int x=1; x<=5; x++)
            {
                int u=frame.width*x/6, v=frame.height*y/4;
                var fit=_fitter.Fit(frame,u,v,config);
                if (!fit.success) continue;
                var point=frame.Unproject(u,v,frame.depthMillimeters[v*frame.width+u]*0.001f);
                var clip=input.worldToClip*new Vector4(point.x,point.y,point.z,1);
                if (clip.w<=0 || Mathf.Abs(clip.x)>clip.w || Mathf.Abs(clip.y)>clip.w) continue;
                float distance=Vector3.Distance(input.cameraPose.position,point);
                if (nearest.isValid && nearest.rawDistanceMeters<=distance) continue;
                nearest=new WallReading { isValid=true, kind=MeasurementKind.NearestObserved,
                    distanceMeters=distance,rawDistanceMeters=distance,source=MeasurementSource.DepthOnly,
                    quality=QualityLabel.DepthEstimate,sessionId=input.sessionId,candidateId=$"depth-patch-{x}-{y}",
                    timestamp=input.now,cameraPose=input.cameraPose,surfacePoint=point,surfaceNormal=fit.worldNormal,
                    qualityReason="Closest sampled wall patch; distance and ranking approximate" };
            }
        }

        // -------------------------------------------------------------- nearest

        WallReading MeasureNearest(in EngineInput input)
        {
            // "Nearest OBSERVED": only candidates currently intersecting the view. Walls behind
            // the user cannot be ruled out, and the label in the UI says so.
            WallCandidate best = null;
            float bestD = float.PositiveInfinity;
            Vector3 bestPoint = default;
            for (int i = 0; i < input.candidates.Count; i++)
            {
                var c = input.candidates[i];
                if (!IsEligible(c)) continue;
                if (!WallGeometry.IntersectsView(c, input.worldToClip)) continue;
                float d = WallGeometry.NearestPointOnPolygon(c, input.cameraPose.position, out Vector3 p);
                if (d < bestD)
                {
                    best = c;
                    bestD = d;
                    bestPoint = p;
                }
            }

            if (best == null)
            {
                _nearestFilter.Reset();
                return WallReading.Invalid(MeasurementKind.NearestObserved, FailureReason.NoWallInView, input.sessionId, input.now, input.cameraPose);
            }

            float filtered = _nearestFilter.Update(input.sessionId, best.id, bestD, input.now, config.filterTimeConstantSeconds);
            var reading = new WallReading
            {
                isValid = true,
                kind = MeasurementKind.NearestObserved,
                distanceMeters = filtered,
                rawDistanceMeters = bestD,
                sessionId = input.sessionId,
                candidateId = best.id,
                timestamp = input.now,
                cameraPose = input.cameraPose,
                surfacePoint = bestPoint,
                surfaceNormal = WallGeometry.NormalFacing(best, input.cameraPose.position),
            };
            ApplyQuality(ref reading, best, input);
            return reading;
        }

        // -------------------------------------------------------------- shared

        bool IsEligible(WallCandidate c)
        {
            return c != null && c.isTracked && c.boundary.Count >= 3 && c.Area() >= config.minCandidateAreaSquareMeters;
        }

        /// <summary>
        /// Decide source + quality label. Order matters: an unreliable depth disagreement beats
        /// every other label because it is the one that must stop the user trusting the number.
        /// </summary>
        void ApplyQuality(ref WallReading r, WallCandidate c, in EngineInput input)
        {
            if (c.source == MeasurementSource.AssistedFloor)
            {
                // The user supplied the wall boundary; depth must not silently promote it
                // into an automatically detected wall or invalidate a known floor constraint.
                r.source = MeasurementSource.AssistedFloor;
                r.quality = QualityLabel.AssistedEstimate;
                r.depthResidualMeters = r.depthInlierFraction = float.NaN;
                r.qualityReason = "User-selected wall base on tracked floor; verify against tape measure";
                ApplyRangeLabel(ref r);
                return;
            }
            if (c.source == MeasurementSource.LearnedDepth || c.source == MeasurementSource.FloorEdge)
            {
                ApplyLearnedQuality(ref r, c, input);
                return;
            }
            var v = _validator.Validate(c, input.depth, config, input.now);
            r.depthResidualMeters = v.medianResidualMeters;
            r.depthInlierFraction = v.inlierFraction;

            if (v.depthUsed)
            {
                bool disagree = Mathf.Abs(v.medianResidualMeters) > config.depthDisagreementThresholdMeters;
                bool enoughInliers = v.inlierFraction >= config.depthMinInlierFraction;
                if (disagree || !enoughInliers)
                {
                    r.source = MeasurementSource.PlaneOnly;
                    r.quality = QualityLabel.Unreliable;
                    r.qualityReason = disagree
                        ? $"depth disagrees with plane by {Mathf.Abs(v.medianResidualMeters) * 100f:F0} cm"
                        : $"only {v.inlierFraction * 100f:F0}% of depth samples on plane";
                    return;
                }
                r.source = MeasurementSource.PlaneDepthValidated;
                r.quality = QualityLabel.DepthValidated;
                r.qualityReason = v.reason;
            }
            else
            {
                r.source = MeasurementSource.PlaneOnly;
                r.quality = QualityLabel.PlaneEstimate;
                r.qualityReason = v.reason;
            }

            ApplyRangeLabel(ref r);
        }

        /// <summary>
        /// Map walls from learned depth. Raw depth can only PROMOTE them (to CrossChecked) when it
        /// agrees within the cross-check tolerance. Disagreement is noted in the reason, never a
        /// demotion (deviation D10): on plain walls raw depth is the unreliable party (spec §2).
        /// </summary>
        void ApplyLearnedQuality(ref WallReading r, WallCandidate c, in EngineInput input)
        {
            var v = _validator.Validate(c, input.depth, config, input.now);
            r.depthResidualMeters = v.medianResidualMeters;
            r.depthInlierFraction = v.inlierFraction;
            bool confidentRaw = input.depth != null && input.depth.confidence != null
                && input.depth.confidence.Length >= input.depth.width * input.depth.height;
            // A calibration source agreeing with its own fitted scale is not a second source.
            // Missing confidence also cannot supply evidence (DepthFrame's null default is legacy).
            bool rawAgrees = !c.calibratedFromRawDepth && confidentRaw && v.depthUsed
                             && Mathf.Abs(v.medianResidualMeters) <= config.crossCheckToleranceMeters
                             && v.inlierFraction >= config.depthMinInlierFraction;
            r.source = c.source;
            r.sourceChain = c.source.ToString() + (c.crossChecked ? "+ARPlane" : "") + (rawAgrees ? "+RawDepth" : "");
            if (c.crossChecked || rawAgrees)
            {
                r.quality = QualityLabel.CrossChecked;
                r.qualityReason = rawAgrees
                    ? $"raw depth agrees within {Mathf.Abs(v.medianResidualMeters) * 100f:F0} cm"
                    : $"ARCore plane agrees within {config.crossCheckToleranceMeters * 100f:F0} cm";
            }
            else
            {
                r.quality = c.source == MeasurementSource.FloorEdge ? QualityLabel.EdgeConfirmed : QualityLabel.LearnedEstimate;
                r.qualityReason = v.depthUsed
                    ? $"raw depth differs by {Mathf.Abs(v.medianResidualMeters) * 100f:F0} cm (not used on plain walls)"
                    : c.source == MeasurementSource.FloorEdge ? "base snapped to the floor edge"
                    : c.calibratedFromRawDepth ? "learned depth scaled from raw depth (not an independent cross-check)"
                    : "learned depth scaled to the floor";
            }
            ApplyRangeLabel(ref r);
        }

        void ApplyRangeLabel(ref WallReading r)
        {
            // Small epsilon so a wall at exactly the boundary (0.5 m) is not flagged by float noise.
            const float rangeEps = 1e-4f;
            if (r.distanceMeters < config.minTestedRangeMeters - rangeEps || r.distanceMeters > config.maxTestedRangeMeters + rangeEps)
            {
                r.quality = QualityLabel.OutOfTestedRange;
                r.qualityReason = $"{r.distanceMeters:F2} m is outside the {config.minTestedRangeMeters}-{config.maxTestedRangeMeters} m validated range";
            }
        }
    }
}
