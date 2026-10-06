using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class WallDetectionPipelineTests
    {
        static InverseDepthImage Frame(Pose pose, double timestamp)
        {
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(pose));
            img.timestamp = timestamp;
            return img;
        }

        [Test]
        public void CorridorFrame_AddsWalls_WithRightWallAt0_8()
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            var pose = SyntheticCorridor.Camera();
            p.ProcessFrame("s1", Frame(pose, 1.0), SyntheticCorridor.Floor, null, 1.03);
            Assert.AreEqual(FailureReason.None, p.LastFailure);
            Assert.GreaterOrEqual(p.Map.Tracks.Count, 3);
            Assert.AreEqual(30.0, p.LastDetectLatencyMs, 1e-6);
            bool found = false;
            foreach (var t in p.Map.Tracks)
                if (Vector3.Angle(t.normal, Vector3.left) < 1f && Mathf.Abs(t.SignedDistance(pose.position) - 0.8f) <= 0.02f) found = true;
            Assert.IsTrue(found, "right wall at 0.8 m");
        }

        [Test]
        public void NoFloorAndNoRawDepth_ReportsNoFloorAndAddsNoWalls()
        {
            // Review Focus 4: at app start there is no floor plane and no confident raw depth.
            // No wall may be made from an assumed camera height.
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.0), default, null, 1.03);
            Assert.AreEqual(FailureReason.NoFloor, p.LastFailure);
            Assert.AreEqual(0, p.Map.Tracks.Count);
            Assert.AreEqual(FailureReason.NoFloor, p.DetectionFailure(1.1, inferenceAvailable: true));
            p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.1), default, new List<MetricSample>(), 1.13);
            Assert.AreEqual(FailureReason.NoFloor, p.LastFailure, "an empty sample list is not a fallback");
        }

        [Test]
        public void DetectionFailure_StaleAndUnavailable()
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            Assert.AreEqual(FailureReason.InferenceStale, p.DetectionFailure(0.0, true), "no frame yet");
            Assert.AreEqual(FailureReason.InferenceUnavailable, p.DetectionFailure(0.0, false));
            p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.0), SyntheticCorridor.Floor, null, 1.03);
            Assert.AreEqual(FailureReason.None, p.DetectionFailure(1.9, true));
            Assert.AreEqual(FailureReason.InferenceStale, p.DetectionFailure(2.1, true), "over 1 s since the last frame");
        }

        [Test]
        public void FailedFrame_KeepsExistingWalls()
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.0), SyntheticCorridor.Floor, null, 1.0);
            int walls = p.Map.Tracks.Count;
            p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(pitchDown: -35f), 1.1), SyntheticCorridor.Floor, null, 1.1);
            Assert.AreEqual(FailureReason.AlignmentFailed, p.LastFailure);
            Assert.AreEqual(walls, p.Map.Tracks.Count);
        }

        [Test]
        public void NewSession_ClearsTheMap_AndResetClearsEverything()
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.0), SyntheticCorridor.Floor, null, 1.0);
            p.ProcessFrame("s2", Frame(SyntheticCorridor.Camera(pitchDown: -35f), 1.1), SyntheticCorridor.Floor, null, 1.1);
            Assert.AreEqual(0, p.Map.Tracks.Count);
            p.Reset();
            Assert.IsTrue(double.IsNaN(p.LastFrameTime));
            Assert.AreEqual(FailureReason.InferenceStale, p.DetectionFailure(1.2, true));
        }

        [Test]
        public void ArPlanes_AreThrottledTo10Hz_AndOnlyTrackedVerticalLargeOnesCount()
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            var near = Fx.WallAtZ(2f, 1f, 1f, "a", yCenter: 1f);
            var far = Fx.WallAtZ(4f, 1f, 1f, "b", yCenter: 1f);
            var tiny = Fx.WallAtZ(6f, 0.1f, 0.1f, "c", yCenter: 1f);
            var lost = Fx.WallAtZ(8f, 1f, 1f, "d", yCenter: 1f);
            lost.isTracked = false;
            p.ObserveArPlanes("s1", new[] { near }, Vector3.up, 0.0);
            p.ObserveArPlanes("s1", new[] { far }, Vector3.up, 0.05);
            Assert.AreEqual(1, p.Map.Tracks.Count, "second call within 0.1 s is skipped");
            p.ObserveArPlanes("s1", new[] { far, tiny, lost }, Vector3.up, 0.11);
            Assert.AreEqual(2, p.Map.Tracks.Count);
            Assert.AreEqual(MeasurementSource.PlaneOnly, p.Map.Tracks[1].lastSource);
        }
    }
}
