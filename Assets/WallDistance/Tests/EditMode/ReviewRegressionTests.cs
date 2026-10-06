using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class ReviewRegressionTests
    {
        static InverseDepthImage Frame(string session, double time = 1.0)
        {
            var image = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
            image.timestamp = time;
            // Reflection keeps these regressions runnable against the pre-fix API.
            typeof(InverseDepthImage).GetField("sessionId")?.SetValue(image, session);
            return image;
        }

        [Test]
        public void OldSessionFrame_CannotRepopulateTheNewMap()
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            var old = Frame("A");
            p.ProcessFrame("A", old, SyntheticCorridor.Floor, null, 1.03);
            Assert.Greater(p.Map.Tracks.Count, 0);
            p.ProcessFrame("B", old, SyntheticCorridor.Floor, null, 1.06);
            Assert.AreEqual(0, p.Map.Tracks.Count, "a result from A is not geometry in B");
            Assert.IsTrue(double.IsNaN(p.LastFrameTime));
        }

        [Test]
        public void ExpiredResult_DoesNotCreateFreshWalls()
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("A", Frame("A"), SyntheticCorridor.Floor, null, 3.0);
            Assert.AreEqual(0, p.Map.Tracks.Count);
            Assert.AreEqual(FailureReason.InferenceStale, p.DetectionFailure(3.0, true));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Latency_IncludesProcessing_OnSuccessAndFailure(bool floor)
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("A", Frame("A"), floor ? SyntheticCorridor.Floor : default, null, 1.03);
            Assert.Greater(p.LastProcessingMs, 0);
            Assert.AreEqual(30.0 + p.LastProcessingMs, p.LastDetectLatencyMs, 1e-5);
        }

        [Test]
        public void AnchorCorrection_PrecedesNewObservations_AndMovesSourceHistory()
        {
            WallTrack wall = null;
            Action<WallMap> correct = map =>
            {
                if (wall != null) map.ApplyAnchorPose(wall.id, new Pose(new Vector3(1.1f, 0f, 1.5f), Quaternion.identity));
            };
            var ctor = typeof(WallDetectionPipeline).GetConstructor(new[] { typeof(MeasurementConfig), typeof(Action<WallMap>) });
            var p = ctor == null ? new WallDetectionPipeline(new MeasurementConfig())
                : (WallDetectionPipeline)ctor.Invoke(new object[] { new MeasurementConfig(), correct });
            p.ObserveArPlanes("s1", Array.Empty<WallCandidate>(), Vector3.up, 0);
            wall = p.Map.Observe(Fx.Obs(1f, 0), 0);
            p.Map.ApplyAnchorPose(wall.id, new Pose(new Vector3(1f, 0f, 1.5f), Quaternion.identity));
            var plane = new WallCandidate();
            plane.Set("ar", true, new Vector3(1.1f, 0f, 1.5f), Quaternion.LookRotation(Vector3.up, Vector3.left),
                new[] { new Vector2(-1.5f, 0), new Vector2(1.5f, 0), new Vector2(1.5f, 2.4f), new Vector2(-1.5f, 2.4f) });
            p.ObserveArPlanes("s1", new[] { plane }, Vector3.up, 0.2);
            Assert.AreEqual(1, p.Map.Tracks.Count);
            Assert.AreSame(wall, p.Map.Tracks[0]);
            Assert.AreEqual(1.1f, wall.origin.x, 1e-5f, "one correction, no double movement");
            Assert.AreEqual(1.1f, wall.lastLearnedPoint.x, 1e-5f);
            Assert.IsTrue(p.Map.Candidates(0.2)[0].crossChecked);
        }

        [Test]
        public void FullHeightOpening_IsNotFilledByOneCollinearWallExtent()
        {
            var corridor = new SyntheticCorridor { endWallZ = 12f };
            var rendered = corridor.Render(SyntheticCorridor.Camera(x: 0, yaw: -90f), vFovDeg: 110f, contentAspect: 1f);
            var image = SyntheticCorridor.ToNetworkOutput(rendered);
            for (int v = 0; v < image.height; v++)
            for (int u = 0; u < image.width; u++)
            {
                int i = v * image.width + u;
                if (float.IsNaN(rendered.depth[i])) continue;
                Vector3 point = image.WorldPoint(u, v, rendered.depth[i]);
                // No wall support throughout a 2.6 m full-height opening, beyond both 1 m margins.
                if (Mathf.Abs(point.x + 1f) < 1e-3f && Mathf.Abs(point.z) < 1.3f) image.values[i] = float.NaN;
            }
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("s1", image, SyntheticCorridor.Floor, null, 0.03);
            Assert.GreaterOrEqual(p.Map.Tracks.Count, 2, "separate observed sections survive extraction");
            var cam = Fx.MakeCamera(new Vector3(0, 1.4f, 0), Quaternion.identity);
            try
            {
                var input = Fx.Input(cam, p.Map.Candidates(0.1), 0.1);
                input.mapWalls = p.Map.Tracks;
                var snapshot = new WallMeasurementEngine(new MeasurementConfig()).Update(input);
                Assert.IsFalse(snapshot.left.isValid, "the opening has no left wall");
                Assert.IsTrue(float.IsNaN(snapshot.corridorWidthMeters));
            }
            finally { UnityEngine.Object.DestroyImmediate(cam.gameObject); }
        }

        [Test]
        public void GrazingBase_AcceptedSnapsStayInsideTheMetricBand()
        {
            var corridor = new SyntheticCorridor { width = 6f, endWallZ = 30f };
            corridor.groutLinesX.Add(2.8f);
            var image = corridor.Render(SyntheticCorridor.Camera(x: 0, h: 0.4f, pitchDown: 1f)).image;
            var refiner = new BaseEdgeRefiner(new DetectionConfig());
            var snap = typeof(BaseEdgeRefiner).GetMethod("TrySnap", BindingFlags.Instance | BindingFlags.NonPublic);
            for (float z = 6.2f; z <= 15f; z += 0.05f)
            {
                var point = new Vector3(2.94f, 0, z);
                object[] args = { image, SyntheticCorridor.Floor, point, Vector3.back, Vector3.left, default(Vector3) };
                if (!(bool)snap.Invoke(refiner, args)) continue;
                Vector3 hit = (Vector3)args[5];
                Assert.LessOrEqual(Mathf.Abs(hit.x - point.x), 0.0801f, $"metric cap at z={z:F2}");
            }
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public void RawCalibrationOrMissingConfidence_CannotSupplyAnIndependentCheck(bool calibrated, bool confidence)
        {
            var cam = Fx.MakeCamera(new Vector3(0, 1.4f, 0), Quaternion.identity);
            try
            {
                var candidate = Fx.WallAtZ(2f, yCenter: 1.4f);
                candidate.source = MeasurementSource.LearnedDepth;
                typeof(WallCandidate).GetField("calibratedFromRawDepth")?.SetValue(candidate, calibrated);
                var raw = Fx.DepthForPlane(new Pose(cam.transform.position, cam.transform.rotation), new Vector3(0, 0, 2), Vector3.back, 1.0);
                if (!confidence) raw.confidence = null;
                var result = new WallMeasurementEngine(new MeasurementConfig()).Update(Fx.Input(cam, new[] { candidate }, 1.0, raw));
                Assert.AreEqual(QualityLabel.LearnedEstimate, result.aimed.quality);
            }
            finally { UnityEngine.Object.DestroyImmediate(cam.gameObject); }
        }

        [Test]
        public void RawScaleProvenance_SurvivesPipelineAndMap()
        {
            var rendered = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var image = SyntheticCorridor.ToNetworkOutput(rendered);
            var samples = new List<MetricSample>();
            for (int v = 0; v < image.height; v += 8)
            for (int u = image.content.xMin; u < image.content.xMax; u += 8)
            {
                float z = rendered.depth[v * image.width + u];
                if (!float.IsNaN(z)) samples.Add(new MetricSample { pixel = new Vector2(u, v), depthMeters = z });
            }
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("s1", image, default, samples, 0.03);
            Assert.IsTrue(p.LastAlignment.success && p.LastAlignment.usedMetricSamples);
            Assert.Greater(p.Map.Tracks.Count, 0);
            var field = typeof(WallCandidate).GetField("calibratedFromRawDepth");
            Assert.IsNotNull(field, "calibration provenance must reach the measurement engine");
            foreach (var candidate in p.Map.Candidates(0.1)) Assert.IsTrue((bool)field.GetValue(candidate));
        }

        [Test]
        public void PendingInitializer_DisposalCleansUpWithoutAnyCoroutine()
        {
            var type = typeof(WallMeasurementEngine).Assembly.GetType("WallDistance.Core.AsyncInitialization");
            Assert.IsNotNull(type, "initialization needs a cleanup owner independent of the host coroutine");
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int disposed = 0;
            Func<int> init = () => { started.Set(); if (!release.Wait(3000)) throw new TimeoutException(); return 0; };
            Action cleanup = () => Interlocked.Increment(ref disposed);
            var owner = (IDisposable)Activator.CreateInstance(type, init, cleanup);
            try
            {
                Assert.IsTrue(started.Wait(3000));
                owner.Dispose();
                release.Set();
                var completion = (Task<int>)type.GetProperty("Completion").GetValue(owner);
                Assert.IsTrue(completion.Wait(3000));
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref disposed) == 1, 3000));
                owner.Dispose();
                Assert.AreEqual(1, disposed);
            }
            finally { release.Set(); owner.Dispose(); }
        }
    }
}
