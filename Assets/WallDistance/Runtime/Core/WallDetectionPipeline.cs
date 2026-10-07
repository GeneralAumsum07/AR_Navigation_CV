using System;
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
        readonly FreeSpaceCarver _carver;
        readonly List<WallObservation> _obs = new List<WallObservation>(8);
        readonly Stopwatch _watch = new Stopwatch();
        readonly Action<WallMap> _beforeObservations;
        string _sessionId;
        double _lastArObserve = double.NegativeInfinity;

        public WallMap Map { get; }
        public AlignmentResult LastAlignment { get; private set; }
        public int LastObservationCount { get; private set; }
        public float LastEdgeSnapFraction { get; private set; } = float.NaN;
        public double LastProcessingMs { get; private set; } = double.NaN;
        /// <summary>Image arrival-to-map latency, including CPU processing, ms. Sensor-clock delay is not measured.</summary>
        public double LastDetectLatencyMs { get; private set; } = double.NaN;
        /// <summary>Image timestamp of the last processed frame; NaN before the first.</summary>
        public double LastFrameTime { get; private set; } = double.NaN;
        public FailureReason LastFailure { get; private set; } = FailureReason.None;
        /// <summary>
        /// Results discarded as too old, from another session, or stamped in the future, since
        /// construction. Discards are otherwise invisible (the map simply stops growing), which on
        /// the phone reads as a frozen distance; the CSV logs this so a recording can show it.
        /// Deliberately NOT cleared by <see cref="Reset"/>: a burst of discards just before a
        /// session reset is exactly the evidence a field log needs to keep.
        /// </summary>
        public int FramesDropped { get; private set; }
        /// <summary>
        /// Map walls removed because frames kept seeing past them, since construction. Like
        /// <see cref="FramesDropped"/> it survives <see cref="Reset"/>: it is field evidence.
        /// </summary>
        public int WallsCarved => _carver.Carved;

        public WallDetectionPipeline(MeasurementConfig cfg, Action<WallMap> beforeObservations = null)
        {
            _cfg = cfg ?? new MeasurementConfig();
            _beforeObservations = beforeObservations;
            _aligner = new FloorAlignedDepth(_cfg.detection);
            _extractor = new VerticalPlaneExtractor(_cfg.detection);
            _refiner = new BaseEdgeRefiner(_cfg.detection);
            _carver = new FreeSpaceCarver(_cfg.detection);
            Map = new WallMap(_cfg);
        }

        public void ProcessFrame(string sessionId, InverseDepthImage img, FloorPlane floor, IReadOnlyList<MetricSample> metric, double now)
        {
            SetSession(sessionId);
            // A worker may finish after reset or long after capture. Neither result is fresh
            // geometry in this frame. Null session stamps remain supported for offline replays.
            if (img == null) return;
            if ((!string.IsNullOrEmpty(img.sessionId) && img.sessionId != sessionId)
                || double.IsNaN(img.timestamp) || now < img.timestamp
                || now - img.timestamp > _cfg.detection.staleAfterSeconds)
            {
                // A null image is a caller bug, not a discarded result, so only these are counted.
                FramesDropped++;
                return;
            }
            _watch.Restart();
            _beforeObservations?.Invoke(Map);
            LastFrameTime = img.timestamp;

            var align = _aligner.Align(img, floor, metric);
            LastAlignment = align;
            LastObservationCount = 0;
            LastEdgeSnapFraction = float.NaN;
            if (!align.success)
            {
                // Keep the map: a frame that cannot be scaled says nothing about walls already found.
                LastFailure = align.failure;
                CompleteTiming(now, img.timestamp);
                return;
            }

            // Before this frame's walls go in: judge the walls already mapped against what this
            // frame can see. New observations are confirmed by the frame anyway.
            _carver.Carve(Map, img, align);

            _extractor.Extract(img, align, floor, now, _obs);
            float snapSum = 0f;
            int snapped = 0;
            int kept = 0;
            Vector3 viewer = img.cameraPose.position;
            for (int i = 0; i < _obs.Count; i++)
            {
                var o = _obs[i];
                // A wall cannot run through the person holding the phone. A segment whose extended
                // line does is a short or edge-on fit (the 10-07 bench) whose yaw is unreliable,
                // and every distance measured to its line would be near zero.
                if (Mathf.Abs(o.SignedDistance(viewer)) < _cfg.detection.minLinePassMeters) continue;
                o.viewer = viewer;
                o.hasViewer = true;
                o.calibratedFromRawDepth = align.usedMetricSamples;
                // Edges only refine walls scaled from the floor: without the floor plane there
                // is no metric back-projection for the edge.
                if (!align.usedMetricSamples && _refiner.TryRefine(img, floor, o, out var refined))
                {
                    o = refined;
                    snapSum += refined.edgeSnapFraction;
                    snapped++;
                }
                Map.Observe(o, now);
                kept++;
            }
            LastObservationCount = kept;
            if (snapped > 0) LastEdgeSnapFraction = snapSum / snapped;
            LastFailure = kept == 0 ? FailureReason.NoWallInView : FailureReason.None;
            CompleteTiming(now, img.timestamp);
        }

        void CompleteTiming(double now, double arrival)
        {
            _watch.Stop();
            LastProcessingMs = _watch.Elapsed.TotalMilliseconds;
            LastDetectLatencyMs = (now - arrival) * 1000.0 + LastProcessingMs;
        }

        public void SetSession(string sessionId)
        {
            if (_sessionId == sessionId) return;
            Reset();
            _sessionId = sessionId;
            Map.SetSession(sessionId);
        }

        /// <summary>
        /// Fold ARCore vertical planes into the map at ≤ 10 Hz. Planes change slowly, and
        /// observing them every frame would let them outweigh the learned walls.
        /// </summary>
        public void ObserveArPlanes(string sessionId, IReadOnlyList<WallCandidate> planes, Vector3 up, double now)
        {
            SetSession(sessionId);
            if (planes == null || now - _lastArObserve < _cfg.detection.arPlaneObserveIntervalSeconds) return;
            _lastArObserve = now;
            _beforeObservations?.Invoke(Map);
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
            _sessionId = null;
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
