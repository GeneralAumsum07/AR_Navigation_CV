using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Flags camera poses that move faster than a hand-held phone can. ARCore keeps reporting
    /// "Tracking" through these corrections, and anything measured or mapped during one mixes two
    /// different coordinate frames, so readings are withheld for a short hold afterwards.
    ///
    /// Evidence (2026-10-07, OnePlus 13R): steps of up to 0.9 m in 15 ms that never returned to
    /// the previous position, i.e. real frame shifts, not one-frame glitches.
    /// </summary>
    public sealed class PoseJumpGuard
    {
        readonly MeasurementConfig _cfg;
        bool _hasLast;
        Vector3 _last;
        double _lastTime;
        double _unreliableUntil = double.NegativeInfinity;

        /// <summary>Pose jumps seen since the last reset (CSV diagnostic).</summary>
        public int Jumps { get; private set; }

        public PoseJumpGuard(MeasurementConfig cfg) { _cfg = cfg; }

        public void Reset()
        {
            _hasLast = false;
            _unreliableUntil = double.NegativeInfinity;
            Jumps = 0;
        }

        /// <summary>Feed the camera position once per update.</summary>
        public void Observe(Vector3 position, double now)
        {
            if (_hasLast)
            {
                double dt = now - _lastTime;
                // Same-frame repeats say nothing. After a long gap (pause, hitch) distance over the
                // gap is not a speed, so the baseline is simply moved.
                const double MinDt = 1e-4, MaxDt = 0.5;
                if (dt < MinDt) return;
                if (dt <= MaxDt)
                {
                    float step = Vector3.Distance(position, _last);
                    if (step >= _cfg.poseJumpMinStepMeters && step / dt >= _cfg.poseJumpMinSpeedMetersPerSecond)
                    {
                        Jumps++;
                        _unreliableUntil = now + _cfg.poseJumpHoldSeconds;
                    }
                }
            }
            _hasLast = true;
            _last = position;
            _lastTime = now;
        }

        public bool IsReliable(double now) => now >= _unreliableUntil;
    }
}
