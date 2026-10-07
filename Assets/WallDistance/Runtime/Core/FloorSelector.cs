using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>One upward-facing tracked plane offered as a floor; the AR adapter builds these.</summary>
    public readonly struct FloorCandidate
    {
        public readonly string id;
        public readonly Vector3 point;
        public readonly Vector3 up;
        public readonly float area;

        public FloorCandidate(string id, Vector3 point, Vector3 up, float area)
        {
            this.id = id;
            this.point = point;
            this.up = up;
            this.area = area;
        }
    }

    /// <summary>
    /// Chooses THE floor among ARCore's horizontal planes and refuses to let it jump.
    ///
    /// The floor is the only absolute scale for learned depth, so a wrong floor does not fail
    /// loudly: every learned distance silently scales with it. In the 10-07 corridor log the
    /// chosen plane moved 0.67 m up mid-walk and the measured corridor width fell from ~2.0 m
    /// to 0.71 m. Two rules guard against that:
    ///  - Lowest level wins. Benches, sills and tables sit above the floor, never below it.
    ///  - Continuity. Camera height above the floor changes only as fast as a hand moves the
    ///    phone; a bigger step in one update is a floor (or pose) jump, and the floor is withheld
    ///    until the new level has held for a moment. No floor is better than a wrong one: the
    ///    pipeline then reports NoFloor or falls back to ARCore depth.
    /// </summary>
    public sealed class FloorSelector
    {
        readonly MeasurementConfig _cfg;
        bool _hasLast;
        float _lastHeight;
        double _lastTime;
        double _settleUntil = double.NegativeInfinity;

        public FloorPlane Current { get; private set; }
        /// <summary>Id of the chosen plane, also while it is being withheld; null when none qualifies.</summary>
        public string CurrentId { get; private set; }
        /// <summary>Floor jumps seen this session (CSV diagnostic).</summary>
        public int Jumps { get; private set; }

        public FloorSelector(MeasurementConfig cfg) { _cfg = cfg; }

        public void Reset()
        {
            Current = default;
            CurrentId = null;
            _hasLast = false;
            _settleUntil = double.NegativeInfinity;
            Jumps = 0;
        }

        /// <summary>Pick the floor for this update. Returns <see cref="Current"/> (invalid when withheld or absent).</summary>
        public FloorPlane Update(IReadOnlyList<FloorCandidate> candidates, Vector3 camera, double now)
        {
            int best = Choose(candidates, camera);
            if (best < 0)
            {
                // History is kept: if the same level comes back within one update it is not a jump.
                Current = default;
                CurrentId = null;
                return Current;
            }

            var c = candidates[best];
            var plane = new FloorPlane(c.point, c.up);
            float h = plane.HeightAbove(camera);
            // Only consecutive updates are compared. Over a longer gap the user may really have
            // changed how they hold the phone, so the first floor after a gap is taken as is.
            const double ConsecutiveSeconds = 0.5;
            if (_hasLast && now - _lastTime <= ConsecutiveSeconds && Mathf.Abs(h - _lastHeight) > _cfg.floorMaxHeightStepMeters)
            {
                Jumps++;
                _settleUntil = now + _cfg.floorSettleSeconds;
            }
            // Tracked even while withheld, so a new level that holds steady settles on schedule.
            _hasLast = true;
            _lastHeight = h;
            _lastTime = now;

            CurrentId = c.id;
            Current = now < _settleUntil ? default : plane;
            return Current;
        }

        /// <summary>Index of the largest plane at the lowest plausible level, or -1.</summary>
        int Choose(IReadOnlyList<FloorCandidate> candidates, Vector3 camera)
        {
            // Lowest = camera highest above it. Planes leaving the camera at an implausible height
            // (FloorPlane's handheld band) are not floors at all.
            float lowest = float.NegativeInfinity;
            for (int i = 0; i < candidates.Count; i++)
            {
                var p = new FloorPlane(candidates[i].point, candidates[i].up);
                if (p.PlausibleCameraHeight(camera)) lowest = Mathf.Max(lowest, p.HeightAbove(camera));
            }
            if (float.IsNegativeInfinity(lowest)) return -1;

            // Within the lowest level, area decides: a small, slightly-low fragment is noise, not a
            // deeper floor. Preferring the current plane on ties keeps the choice from flickering.
            int best = -1;
            float bestArea = -1f;
            for (int i = 0; i < candidates.Count; i++)
            {
                var p = new FloorPlane(candidates[i].point, candidates[i].up);
                if (!p.PlausibleCameraHeight(camera) || lowest - p.HeightAbove(camera) > _cfg.floorSameLevelMeters) continue;
                float area = candidates[i].area;
                if (area > bestArea || (area == bestArea && candidates[i].id == CurrentId))
                {
                    best = i;
                    bestArea = area;
                }
            }
            return best;
        }
    }
}
