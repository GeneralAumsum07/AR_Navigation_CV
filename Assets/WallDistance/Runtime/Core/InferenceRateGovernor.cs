namespace WallDistance.Core
{
    /// <summary>
    /// Thermal step-down for the NPU + camera load (spec §6, §12). Sustained slow inferences are
    /// the observable symptom of throttling, so the rule keys on latency rather than on Android
    /// thermal APIs, which vary by vendor. The 30 s recovery rule is a plan proposal (D5).
    /// </summary>
    public sealed class InferenceRateGovernor
    {
        public float normalHz = 15f;
        public float throttledHz = 5f;
        public float slowMilliseconds = 60f;
        public float slowForSeconds = 5f;
        public float recoverAfterSeconds = 30f;

        public bool Throttled { get; private set; }
        public int Transitions { get; private set; }
        public double MinIntervalSeconds => 1.0 / (Throttled ? throttledHz : normalHz);
        public string State => Throttled ? "throttled" : "normal";

        double _slowSince = double.NaN;
        double _fastSince = double.NaN;

        public void Record(double milliseconds, double now)
        {
            // Tiny tolerance so float clocks such as 50·0.1 still count as 5 s.
            const double eps = 1e-9;
            if (milliseconds > slowMilliseconds)
            {
                _fastSince = double.NaN;
                if (double.IsNaN(_slowSince)) _slowSince = now;
                if (!Throttled && now - _slowSince >= slowForSeconds - eps)
                {
                    Throttled = true;
                    Transitions++;
                }
            }
            else
            {
                _slowSince = double.NaN;
                if (!Throttled) return;
                if (double.IsNaN(_fastSince)) _fastSince = now;
                if (now - _fastSince >= recoverAfterSeconds - eps)
                {
                    Throttled = false;
                    Transitions++;
                    _fastSince = double.NaN;
                }
            }
        }

        public void Reset()
        {
            Throttled = false;
            _slowSince = _fastSince = double.NaN;
        }
    }
}
