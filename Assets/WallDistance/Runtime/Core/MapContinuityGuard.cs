namespace WallDistance.Core
{
    /// <summary>
    /// Decides when the wall map's coordinate frame can no longer be trusted. Losing tracking,
    /// including a pose jump that <see cref="PoseJumpGuard"/> turns into lost tracking, means the
    /// walls already mapped may sit in a different frame from the camera that comes back. On
    /// 2026-10-07 a wall stamped during eight jumps in three seconds stayed 0.1-0.3 m in front of
    /// the phone for the next eight seconds.
    ///
    /// The map is cheap to rebuild (about ten frames a second), so the safe answer is to clear it
    /// and to refuse inference results captured before tracking came back, which were posed in
    /// the old frame even though they finish in the new one.
    /// </summary>
    public sealed class MapContinuityGuard
    {
        bool _tracking;
        // Capture times before this are from an untrusted frame. +∞ while not tracking.
        double _trustedSince = double.PositiveInfinity;

        /// <summary>Map clears since the last reset (CSV diagnostic).</summary>
        public int Clears { get; private set; }

        /// <summary>Feed the session state once per update. True exactly when the map must be cleared now.</summary>
        public bool Update(bool tracking, double now)
        {
            bool lost = _tracking && !tracking;
            if (!_tracking && tracking) _trustedSince = now;
            if (!tracking) _trustedSince = double.PositiveInfinity;
            _tracking = tracking;
            if (lost) Clears++;
            return lost;
        }

        /// <summary>Whether a frame captured at <paramref name="captureTime"/> is posed in the current, trusted frame.</summary>
        public bool Accepts(double captureTime) => captureTime >= _trustedSince;

        public void Reset()
        {
            _tracking = false;
            _trustedSince = double.PositiveInfinity;
            Clears = 0;
        }
    }
}
