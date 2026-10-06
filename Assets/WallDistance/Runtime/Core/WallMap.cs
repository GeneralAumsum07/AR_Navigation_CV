using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// World-anchored memory of walls (spec §5.2). Observations are associated with an existing
    /// track by angle, offset and along-wall gap, then fused with a two-scalar information
    /// filter (offset along the normal, yaw about up). Information is capped so a long-seen
    /// wall still follows anchor corrections and real changes, rather than freezing.
    /// </summary>
    public sealed class WallMap
    {
        readonly MeasurementConfig _cfg;
        readonly List<WallTrack> _tracks = new List<WallTrack>();
        readonly List<WallCandidate> _candidates = new List<WallCandidate>();
        readonly List<WallCandidate> _candidatePool = new List<WallCandidate>();
        readonly Vector2[] _poly = new Vector2[4];
        int _nextId;

        public IReadOnlyList<WallTrack> Tracks => _tracks;
        public string SessionId { get; private set; }

        public WallMap(MeasurementConfig cfg) { _cfg = cfg ?? new MeasurementConfig(); }

        DetectionConfig D => _cfg.detection;

        /// <summary>A new AR session is a new coordinate frame; walls from the old one are meaningless.</summary>
        public void SetSession(string sessionId)
        {
            if (sessionId == SessionId) return;
            SessionId = sessionId;
            Clear();
        }

        // Ids keep counting across sessions so a stale id can never alias a new wall.
        public void Clear() => _tracks.Clear();

        public WallTrack Observe(WallObservation obs, double now)
        {
            if (obs == null) return null;
            WallTrack best = null;
            float bestOffset = float.PositiveInfinity;
            foreach (var t in _tracks)
            {
                if (Vector3.Angle(t.normal, obs.normal) > D.associateMaxAngleDeg) continue;
                float off = Mathf.Abs(t.SignedDistance(obs.origin));
                if (off > D.associateMaxOffsetMeters) continue;
                if (AlongGap(t, obs) > D.associateMaxGapMeters) continue;
                if (off < bestOffset) { bestOffset = off; best = t; }
            }
            if (best == null) best = Create(obs, now);
            else Fuse(best, obs);
            Record(best, obs, now);
            return best;
        }

        WallTrack Create(WallObservation obs, double now)
        {
            var t = new WallTrack("w" + _nextId++)
            {
                origin = obs.origin, normal = obs.normal, up = obs.up,
                extentMin = obs.extentMin, extentMax = obs.extentMax,
                offsetInfo = Mathf.Min(OffsetInfo(obs), MaxOffsetInfo),
                angleInfo = Mathf.Min(AngleInfo(obs), MaxAngleInfo),
                firstSeen = now,
            };
            _tracks.Add(t);
            return t;
        }

        float MaxOffsetInfo => 1f / (D.maxOffsetInfoSigmaMeters * D.maxOffsetInfoSigmaMeters);
        float MaxAngleInfo { get { float s = D.maxAngleInfoSigmaDeg * Mathf.Deg2Rad; return 1f / (s * s); } }

        /// <summary>Offset information of one observation: σ² = rms²/N, floored at the minimum σ.</summary>
        float OffsetInfo(WallObservation o)
        {
            float var = o.rms * o.rms / Mathf.Max(1, o.inliers);
            return 1f / Mathf.Max(var, D.minOffsetSigmaMeters * D.minOffsetSigmaMeters);
        }

        /// <summary>
        /// Yaw information: a line fitted to N points spread over length L has slope variance
        /// ≈ 12·σ²/(N·L²). Floored, because points within a frame are correlated.
        /// </summary>
        float AngleInfo(WallObservation o)
        {
            float L = Mathf.Max(o.Length, 0.1f);
            float var = 12f * o.rms * o.rms / (Mathf.Max(1, o.inliers) * L * L);
            float min = D.minAngleSigmaDeg * Mathf.Deg2Rad;
            return 1f / Mathf.Max(var, min * min);
        }

        void Fuse(WallTrack t, WallObservation obs)
        {
            // Yaw first: rotate the track normal about up toward the observation's.
            float iObsA = AngleInfo(obs);
            float theta = Vector3.SignedAngle(t.normal, obs.normal, t.up) * Mathf.Deg2Rad;
            float wA = iObsA / (t.angleInfo + iObsA);
            t.normal = Quaternion.AngleAxis(wA * theta * Mathf.Rad2Deg, t.up) * t.normal;
            if (Horizontal.TryDirection(t.normal, t.up, out Vector3 n)) t.normal = n;
            t.angleInfo = Mathf.Min(t.angleInfo + iObsA, MaxAngleInfo);

            // Then offset: where the observation's line passes the track origin, along the new normal.
            float iObsD = OffsetInfo(obs);
            float delta = -obs.SignedDistance(t.origin) * Vector3.Dot(obs.normal, t.normal);
            float wD = iObsD / (t.offsetInfo + iObsD);
            t.origin += t.normal * (wD * delta);
            t.offsetInfo = Mathf.Min(t.offsetInfo + iObsD, MaxOffsetInfo);

            // Base: keep the lowest level seen (AR planes rarely reach the floor; learned bases do).
            float trackLevel = Vector3.Dot(t.origin, t.up), obsLevel = Vector3.Dot(obs.origin, t.up);
            if (obsLevel < trackLevel) t.origin += t.up * (obsLevel - trackLevel);

            // Extent: union, then re-centre the origin on it.
            Vector3 dir = t.direction;
            float a0 = Vector3.Dot(obs.PointAt(obs.extentMin) - t.origin, dir);
            float a1 = Vector3.Dot(obs.PointAt(obs.extentMax) - t.origin, dir);
            float lo = Mathf.Min(t.extentMin, Mathf.Min(a0, a1)), hi = Mathf.Max(t.extentMax, Mathf.Max(a0, a1));
            float mid = 0.5f * (lo + hi);
            t.origin += dir * mid;
            t.extentMin = lo - mid;
            t.extentMax = hi - mid;
        }

        void Record(WallTrack t, WallObservation obs, double now)
        {
            t.observations++;
            t.lastSeen = now;
            t.lastSource = obs.source;
            // Foot of the track origin on the observation's line, used for the cross-check.
            Vector3 foot = t.origin - obs.normal * obs.SignedDistance(t.origin);
            switch (obs.source)
            {
                case MeasurementSource.FloorEdge:
                    t.lastFloorEdgeTime = now;
                    t.lastLearnedTime = now;
                    t.lastLearnedPoint = foot;
                    t.edgeSnapFraction = obs.edgeSnapFraction;
                    break;
                case MeasurementSource.LearnedDepth:
                    t.lastLearnedTime = now;
                    t.lastLearnedPoint = foot;
                    break;
                case MeasurementSource.PlaneOnly:
                    t.lastArPlaneTime = now;
                    t.lastArPlanePoint = foot;
                    break;
            }
        }

        /// <summary>Gap between the observation's extent and the track's, along the track (0 when they overlap).</summary>
        static float AlongGap(WallTrack t, WallObservation obs)
        {
            Vector3 dir = t.direction;
            float a0 = Vector3.Dot(obs.PointAt(obs.extentMin) - t.origin, dir);
            float a1 = Vector3.Dot(obs.PointAt(obs.extentMax) - t.origin, dir);
            float lo = Mathf.Min(a0, a1), hi = Mathf.Max(a0, a1);
            return Mathf.Max(0f, Mathf.Max(lo - t.extentMax, t.extentMin - hi));
        }

        void RefreshCrossChecks(double now)
        {
            foreach (var t in _tracks)
            {
                // An independent ARCore plane and a learned observation both recent and within 5 cm.
                t.crossChecked = WallTrack.Recent(t.lastLearnedTime, now, D.sourceWindowSeconds)
                                 && WallTrack.Recent(t.lastArPlaneTime, now, D.sourceWindowSeconds)
                                 && Mathf.Abs(Vector3.Dot(t.lastLearnedPoint - t.lastArPlanePoint, t.normal)) <= _cfg.crossCheckToleranceMeters;
            }
        }

        public int Prune(double now)
        {
            int removed = _tracks.RemoveAll(t => now - t.lastSeen > D.trackMaxAgeSeconds);
            RefreshCrossChecks(now);
            return removed;
        }

        /// <summary>
        /// Map walls as WallCandidates, so the existing engine measures them exactly like AR planes.
        /// The list and its objects are reused; copy anything you keep past the next call.
        /// </summary>
        public IReadOnlyList<WallCandidate> Candidates(double now)
        {
            RefreshCrossChecks(now);
            _candidates.Clear();
            while (_candidatePool.Count < _tracks.Count) _candidatePool.Add(new WallCandidate());
            float h = D.nominalWallHeightMeters;
            for (int i = 0; i < _tracks.Count; i++)
            {
                var t = _tracks[i];
                _poly[0] = new Vector2(t.extentMin, 0f);
                _poly[1] = new Vector2(t.extentMax, 0f);
                _poly[2] = new Vector2(t.extentMax, h);
                _poly[3] = new Vector2(t.extentMin, h);
                var c = _candidatePool[i];
                // LookRotation(up, normal): local X = direction, local Y = normal, local Z = up.
                c.Set(t.id, true, t.origin, Quaternion.LookRotation(t.up, t.normal), _poly);
                c.source = t.Source(now, D.sourceWindowSeconds);
                c.crossChecked = t.crossChecked;
                _candidates.Add(c);
            }
            return _candidates;
        }

        /// <summary>
        /// Feed the tracked pose of this wall's ARAnchor. The first call stores a reference; later
        /// calls move the wall by the anchor's motion since the previous call (ARCore drift correction).
        /// </summary>
        public bool ApplyAnchorPose(string id, Pose anchor)
        {
            var t = Find(id);
            if (t == null) return false;
            if (t.hasAnchorReference)
            {
                Quaternion dq = anchor.rotation * Quaternion.Inverse(t.anchorReference.rotation);
                t.origin = anchor.position + dq * (t.origin - t.anchorReference.position);
                // Keep the wall vertical even if the anchor's correction carries a tiny tilt.
                if (Horizontal.TryDirection(dq * t.normal, t.up, out Vector3 n)) t.normal = n;
            }
            t.anchorReference = anchor;
            t.hasAnchorReference = true;
            return true;
        }

        public void ForgetAnchor(string id)
        {
            var t = Find(id);
            if (t != null) t.hasAnchorReference = false;
        }

        WallTrack Find(string id)
        {
            foreach (var t in _tracks) if (t.id == id) return t;
            return null;
        }
    }
}
