using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Learns where the floor is from a few seconds of camera-height clues (floor-free design
    /// §4.3) and hands out a FloorPlane once the clues agree. Never assumes a height: until the
    /// evidence is in, it has no floor to give.
    ///
    /// The floor is stored as a world height, not as the camera height. ARCore's short-term
    /// vertical motion is metric, so a hand moving up or down moves the camera while the floor
    /// stays put. Over longer spans ARCore drifts and jumps (run 150956: steps of −0.5, −0.8,
    /// +0.46 and −1.46 m), which is why clues expire after a short window and why a jump carries
    /// over the camera's height above the floor instead of the floor's world height.
    /// </summary>
    public sealed class CameraHeightEstimator
    {
        readonly DetectionConfig _cfg;
        readonly List<HeightClue> _clues = new List<HeightClue>(128);
        float[] _v = new float[128];
        float[] _w = new float[128];

        bool _hasHeld;
        float _heldFloorY;
        double _heldAt;
        bool _carryPending;
        float _carryHeight;
        float _lastCameraY = float.NaN;
        double _lastActivity = double.NegativeInfinity;

        public bool Ready { get; private set; }
        /// <summary>World height of the floor in use; NaN when not ready.</summary>
        public float FloorY { get; private set; } = float.NaN;
        /// <summary>Camera height above that floor at the last <see cref="TryFloor"/>; NaN when not ready.</summary>
        public float HeightMeters { get; private set; } = float.NaN;
        /// <summary>Weighted median absolute deviation of the window's floor heights; NaN with no clues.</summary>
        public float SpreadMeters { get; private set; } = float.NaN;
        public float WindowWeight { get; private set; }
        public int WindowClues { get; private set; }

        public CameraHeightEstimator(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

        public void Add(in HeightClue c)
        {
            _clues.Add(c);
            if (c.time > _lastActivity) _lastActivity = c.time;
        }

        /// <summary>A frame self-aligned but gave no clue: still progress toward a floor, so "calibrating", not "no floor".</summary>
        public void NoteActivity(double now)
        {
            if (now > _lastActivity) _lastActivity = now;
        }

        /// <summary>
        /// ARCore's world frame may have moved (MapContinuityGuard cleared the map). Keep only the
        /// camera's height above the floor, measured in the old frame, and re-place the floor
        /// under the camera on the next <see cref="TryFloor"/>. Without this, each of run 150956's
        /// ten jumps would cost seconds of "calibrating".
        /// </summary>
        public void MarkDiscontinuity()
        {
            if (_hasHeld && !float.IsNaN(_lastCameraY) && !_carryPending)
            {
                _carryHeight = _lastCameraY - _heldFloorY;
                _carryPending = true;
            }
            _clues.Clear();
            _hasHeld = false;
            Ready = false;
            WindowClues = 0;
            WindowWeight = 0f;
        }

        public bool TryFloor(Vector3 camera, double now, out FloorPlane floor)
        {
            Prune(now);
            if (_carryPending)
            {
                _carryPending = false;
                _hasHeld = true;
                _heldFloorY = camera.y - _carryHeight;
                _heldAt = now;
            }
            _lastCameraY = camera.y;

            Evaluate(out bool enough, out float estimate, out float spread);
            SpreadMeters = spread;
            bool ok;
            if (enough && spread <= _cfg.maxHeightSpreadMeters)
            {
                _hasHeld = true;
                _heldFloorY = estimate;
                _heldAt = now;
                ok = true;
            }
            else if (!enough && _hasHeld && now - _heldAt <= _cfg.heightHoldSeconds)
            {
                // Too little new evidence (floor out of view, no confident depth): keep the last
                // good floor. Enough evidence that DISAGREES falls through to "not ready" instead.
                ok = true;
            }
            else ok = false;

            Ready = ok;
            if (!ok)
            {
                floor = default;
                FloorY = HeightMeters = float.NaN;
                return false;
            }
            FloorY = _heldFloorY;
            HeightMeters = camera.y - _heldFloorY;
            floor = new FloorPlane(new Vector3(camera.x, _heldFloorY, camera.z), Vector3.up);
            return true;
        }

        public bool IsCalibrating(double now) => !Ready && now - _lastActivity <= _cfg.heightWindowSeconds;

        /// <summary>Refines the pipeline's NoFloor into Calibrating while clues are being gathered.</summary>
        public FailureReason Explain(FailureReason detection, double now) =>
            detection == FailureReason.NoFloor && IsCalibrating(now) ? FailureReason.Calibrating : detection;

        public void Reset()
        {
            _clues.Clear();
            _hasHeld = _carryPending = false;
            _lastCameraY = float.NaN;
            _lastActivity = double.NegativeInfinity;
            Ready = false;
            FloorY = HeightMeters = SpreadMeters = float.NaN;
            WindowWeight = 0f;
            WindowClues = 0;
        }

        void Prune(double now)
        {
            // In-place compaction: RemoveAll with a lambda would allocate a closure per frame.
            int keep = 0;
            for (int i = 0; i < _clues.Count; i++)
                if (now - _clues[i].time <= _cfg.heightWindowSeconds) _clues[keep++] = _clues[i];
            _clues.RemoveRange(keep, _clues.Count - keep);
        }

        void Evaluate(out bool enough, out float estimate, out float spread)
        {
            int n = _clues.Count;
            if (_v.Length < n)
            {
                _v = new float[n * 2];
                _w = new float[n * 2];
            }
            float total = 0f;
            double first = double.PositiveInfinity, last = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var c = _clues[i];
                _v[i] = c.floorY;
                _w[i] = c.weight;
                total += c.weight;
                if (c.time < first) first = c.time;
                if (c.time > last) last = c.time;
            }
            WindowWeight = total;
            WindowClues = n;
            enough = n >= _cfg.minHeightClues && total >= _cfg.minHeightWeight && last - first >= _cfg.minHeightSpanSeconds;
            if (n == 0)
            {
                estimate = spread = float.NaN;
                return;
            }
            // Weighted median, not mean: a burst of biased clues (a reflective door) must not
            // drag the floor. The spread uses the same statistic on the deviations.
            estimate = WeightedMedian(n, total);
            for (int i = 0; i < n; i++) _v[i] = Mathf.Abs(_v[i] - estimate);
            spread = WeightedMedian(n, total);
        }

        /// <summary>Sorts _v[0..n) with _w alongside and returns the value at half the total weight.</summary>
        float WeightedMedian(int n, float total)
        {
            System.Array.Sort(_v, _w, 0, n);
            float half = 0.5f * total, acc = 0f;
            for (int i = 0; i < n; i++)
            {
                acc += _w[i];
                if (acc >= half) return _v[i];
            }
            return _v[n - 1];
        }
    }
}
