using System;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Time-based exponential smoothing keyed to one candidate. A wall-clock time constant
    /// (rather than a per-frame alpha) keeps the lag identical at 30 and 60 fps, which matters
    /// when the benchmark reports filter lag. Any change of target resets the state so a
    /// reading never blends two different walls.
    /// </summary>
    public sealed class MeasurementFilter
    {
        string _candidateId;
        string _sessionId;
        float _value = float.NaN;
        double _lastTime;

        public bool HasValue => !float.IsNaN(_value);
        public string CandidateId => _candidateId;

        public void Reset()
        {
            _candidateId = null;
            _sessionId = null;
            _value = float.NaN;
            _lastTime = 0;
        }

        /// <summary>
        /// Feed one raw measurement. Returns the filtered value. Switching candidate or session
        /// discards history and returns the raw value unchanged.
        /// </summary>
        public float Update(string sessionId, string candidateId, float raw, double now, float timeConstantSeconds)
        {
            bool targetChanged = candidateId != _candidateId || sessionId != _sessionId;
            if (targetChanged || !HasValue)
            {
                _candidateId = candidateId;
                _sessionId = sessionId;
                _value = raw;
                _lastTime = now;
                return raw;
            }

            float dt = (float)Math.Max(0.0, now - _lastTime);
            _lastTime = now;
            // alpha -> 1 as dt grows past the time constant, so a long stall does not keep
            // reporting a stale value for many frames afterwards.
            float alpha = timeConstantSeconds <= 0f ? 1f : 1f - Mathf.Exp(-dt / timeConstantSeconds);
            _value += alpha * (raw - _value);
            return _value;
        }
    }
}
