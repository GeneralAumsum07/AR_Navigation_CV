using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Per-inference-frame orchestration: floor alignment → vertical planes → base-edge refinement
    /// → wall map. Also folds ARCore's own vertical planes into the same map, so they can
    /// cross-check learned walls (spec §5.2). Pure C#: the AR layer feeds it frames and planes.
    /// </summary>
    public sealed class WallDetectionPipeline
    {
        readonly MeasurementConfig _cfg;
        readonly FloorAlignedDepth _aligner;
        readonly VerticalPlaneExtractor _extractor;
        readonly BaseEdgeRefiner _refiner;
        readonly List<WallObservation> _obs = new List<WallObservation>(8);
        readonly Stopwatch _watch = new Stopwatch();
        double _lastArObserve = double.NegativeInfinity;

        public WallMap Map { get; }
        public AlignmentResult LastAlignment { get; private set; }
        public int LastObservationCount { get; private set; }
        public float LastEdgeSnapFraction { get; private set; } = float.NaN;
        public double LastProcessingMs { get; private set; } = double.NaN;
        /// <summary>Camera-image-to-map latency of the last processed frame, ms.</summary>
        public double LastDetectLatencyMs { get; private set; } = double.NaN;
        /// <summary>Image timestamp of the last processed frame; NaN before the first.</summary>
        public double LastFrameTime { get; private set; } = double.NaN;
        public FailureReason LastFailure { get; private set; } = FailureReason.None;

        public WallDetectionPipeline(MeasurementConfig cfg)
        {
            _cfg = cfg ?? new MeasurementConfig();
            _aligner = new FloorAlignedDepth(_cfg.detection);
            _extractor = new VerticalPlaneExtractor(_cfg.detection);
            _refiner = new BaseEdgeRefiner(_cfg.detection);
            Map = new WallMap(_cfg);
        }

        public void ProcessFrame(string sessionId, InverseDepthImage img, FloorPlane floor, IReadOnlyList<MetricSample> metric, double now)
        {
            _watch.Restart();
            Map.SetSession(sessionId);
            LastFrameTime = img.timestamp;
            LastDetectLatencyMs = (now - img.timestamp) * 1000.0;

            var align = _aligner.Align(img, floor, metric);
            LastAlignment = align;
            LastObservationCount = 0;
            LastEdgeSnapFraction = float.NaN;
            if (!align.success)
            {
                // Keep the map: a frame that cannot be scaled says nothing about walls already found.
                LastFailure = align.failure;
                LastProcessingMs = _watch.Elapsed.TotalMilliseconds;
                return;
            }

            _extractor.Extract(img, align, floor, now, _obs);
            float snapSum = 0f;
            int snapped = 0;
            for (int i = 0; i < _obs.Count; i++)
            {
                var o = _obs[i];
                // Edges only refine walls scaled from the floor: without the floor plane there
                // is no metric back-projection for the edge.
                if (!align.usedMetricSamples && _refiner.TryRefine(img, floor, o, out var refined))
                {
                    o = refined;
                    snapSum += refined.edgeSnapFraction;
                    snapped++;
                }
                Map.Observe(o, now);
            }
            LastObservationCount = _obs.Count;
            if (snapped > 0) LastEdgeSnapFraction = snapSum / snapped;
            LastFailure = _obs.Count == 0 ? FailureReason.NoWallInView : FailureReason.None;
            LastProcessingMs = _watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Fold ARCore vertical planes into the map at ≤ 10 Hz. Planes change slowly, and
        /// observing them every frame would let them outweigh the learned walls.
        /// </summary>
        public void ObserveArPlanes(string sessionId, IReadOnlyList<WallCandidate> planes, Vector3 up, double now)
        {
            if (planes == null || now - _lastArObserve < _cfg.detection.arPlaneObserveIntervalSeconds) return;
            _lastArObserve = now;
            Map.SetSession(sessionId);
            for (int i = 0; i < planes.Count; i++)
            {
                var c = planes[i];
                if (c == null || !c.isTracked || c.source != MeasurementSource.PlaneOnly) continue;
                if (c.boundary.Count < 3 || c.Area() < _cfg.minCandidateAreaSquareMeters) continue;
                var o = WallObservation.FromArPlane(c, up, now);
                if (o != null) Map.Observe(o, now);
            }
        }

        /// <summary>
        /// Why learned detection is not producing walls right now; None when it is working.
        /// Unavailable is for the HUD and CSV only (deviation D9); the engine never stamps it on readings.
        /// </summary>
        public FailureReason DetectionFailure(double now, bool inferenceAvailable)
        {
            if (!inferenceAvailable) return FailureReason.InferenceUnavailable;
            if (double.IsNaN(LastFrameTime) || now - LastFrameTime > _cfg.detection.staleAfterSeconds)
                return FailureReason.InferenceStale;
            return LastFailure;
        }

        public void Reset()
        {
            Map.Clear();
            LastAlignment = null;
            LastObservationCount = 0;
            LastEdgeSnapFraction = float.NaN;
            LastProcessingMs = LastDetectLatencyMs = LastFrameTime = double.NaN;
            LastFailure = FailureReason.None;
            _lastArObserve = double.NegativeInfinity;
        }
    }
}
