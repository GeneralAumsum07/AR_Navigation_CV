using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    public enum FloorSourceKind { None, ArPlane, Derived }

    /// <summary>
    /// Chooses the floor each inference frame is scaled against (floor-free design §4.4):
    /// ARCore's floor when it has one, otherwise a floor derived from the depth network's own
    /// flatness plus a pooled camera height. Either way the pipeline receives an ordinary
    /// FloorPlane, so nothing downstream changes. Lives in Core so the whole decision is
    /// testable without AR Foundation.
    /// </summary>
    public sealed class FloorResolver
    {
        readonly DetectionConfig _cfg;
        readonly FloorSelfAligner _aligner;
        readonly HeightClues _clues;

        public CameraHeightEstimator Estimator { get; }
        public FloorSourceKind LastSource { get; private set; } = FloorSourceKind.None;
        public bool LastSelfAlignOk { get; private set; }
        public double LastSelfAlignMs { get; private set; } = double.NaN;
        /// <summary>Raw-depth samples that landed on valid network pixels in the last frame; -1 when not collected.</summary>
        public int LastClueSamples { get; private set; } = -1;

        public FloorResolver(DetectionConfig cfg)
        {
            _cfg = cfg ?? new DetectionConfig();
            _aligner = new FloorSelfAligner(_cfg);
            _clues = new HeightClues(_cfg);
            Estimator = new CameraHeightEstimator(_cfg);
        }

        public FloorPlane Resolve(InverseDepthImage img, FloorPlane arFloor, IReadOnlyList<MetricSample> samples, double now)
        {
            // Match the pipeline's capture-age rules before changing any calibration state or
            // diagnostics. Stale/future frames supply no fresh evidence, even with an AR floor.
            if (img == null || !img.IsCurrent(img.sessionId, now, _cfg.staleAfterSeconds)) return default;
            LastSelfAlignOk = false;
            LastSelfAlignMs = double.NaN;
            LastClueSamples = -1;
            Vector3 camera = img.cameraPose.position;

            if (arFloor.IsValid)
            {
                // ARCore's floor is measured, so it wins outright. It also feeds the estimator, so
                // a derived floor is ready the moment the plane disappears (ARCore floors flicker).
                if (_clues.TryFromPlane(arFloor, camera, now, out var planeClue)) Estimator.Add(planeClue);
                Estimator.TryFloor(camera, now, out _);
                LastSource = FloorSourceKind.ArPlane;
                return arFloor;
            }

            var a = _aligner.Align(img);
            LastSelfAlignOk = a.success;
            LastSelfAlignMs = a.milliseconds;
            if (a.success)
            {
                Estimator.NoteActivity(now);
                if (_clues.TryFromDepth(img, a, samples, now, out var depthClue, out int used)) Estimator.Add(depthClue);
                LastClueSamples = used;
            }

            if (Estimator.TryFloor(camera, now, out var floor))
            {
                LastSource = FloorSourceKind.Derived;
                return floor;
            }
            LastSource = FloorSourceKind.None;
            return default;
        }

        public void MarkDiscontinuity() => Estimator.MarkDiscontinuity();

        public void Reset()
        {
            Estimator.Reset();
            LastSource = FloorSourceKind.None;
            LastSelfAlignOk = false;
            LastSelfAlignMs = double.NaN;
            LastClueSamples = -1;
        }
    }
}
