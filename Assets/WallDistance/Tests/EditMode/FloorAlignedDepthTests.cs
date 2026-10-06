using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class FloorAlignedDepthTests
    {
        static AlignmentResult Align(InverseDepthImage img, FloorPlane floor, IReadOnlyList<MetricSample> metric = null, DetectionConfig cfg = null) =>
            new FloorAlignedDepth(cfg ?? new DetectionConfig()).Align(img, floor, metric);

        [Test]
        public void RecoversScaleAndShift_Within1Percent_UnderNoise()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var img = SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f, relativeNoise: 0.01f, seed: 3);
            var a = Align(img, SyntheticCorridor.Floor);
            Assert.IsTrue(a.success, a.reason);
            Assert.AreEqual(2f, a.s, 0.02f, "1% of s");
            // t has no natural scale of its own; 0.004 m⁻¹ is 1% of the floor's typical inverse depth (~0.4 m⁻¹).
            Assert.AreEqual(0.05f, a.t, 0.004f);
            Assert.LessOrEqual(a.residual, 0.03f);
            Assert.GreaterOrEqual(a.inliers, 500);
            Assert.IsFalse(a.usedMetricSamples);
        }

        [Test]
        public void ExactData_RecoversExactly()
        {
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()), 1.5f, 0.1f);
            var a = Align(img, SyntheticCorridor.Floor);
            Assert.IsTrue(a.success, a.reason);
            Assert.AreEqual(1.5f, a.s, 1e-3f);
            Assert.AreEqual(0.1f, a.t, 1e-3f);
            Assert.AreEqual(1f / (1.5f * 0.2f + 0.1f), a.MetricDepth(0.2f), 1e-4f);
        }

        [Test]
        public void FewerThan500FloorPixels_IsAlignmentFailed()
        {
            // Pitched 35° up: the bottom of the view is above the horizon, so no floor is visible.
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera(pitchDown: -35f)));
            var a = Align(img, SyntheticCorridor.Floor);
            Assert.IsFalse(a.success);
            Assert.AreEqual(FailureReason.AlignmentFailed, a.failure);
        }

        [Test]
        public void NoFloorAndNoMetric_IsNoFloor()
        {
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
            var a = Align(img, default);
            Assert.IsFalse(a.success);
            Assert.AreEqual(FailureReason.NoFloor, a.failure);
        }

        [Test]
        public void NoFloor_With300ConfidentMetricSamples_Aligns()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var img = SyntheticCorridor.ToNetworkOutput(r, 2f, 0.05f);
            var rng = new System.Random(5);
            var samples = new List<MetricSample>();
            while (samples.Count < 300)
            {
                int u = rng.Next(65, 453), v = rng.Next(0, 518);
                float z = r.depth[v * 518 + u];
                if (!float.IsNaN(z)) samples.Add(new MetricSample { pixel = new Vector2(u, v), depthMeters = z });
            }
            var a = Align(img, default, samples);
            Assert.IsTrue(a.success, a.reason);
            Assert.IsTrue(a.usedMetricSamples);
            Assert.AreEqual(2f, a.s, 0.02f);
            Assert.IsFalse(a.hasFloorMask, "no floor plane, so no floor mask");
        }

        [Test]
        public void AffineDepthMode_RecoversDepthParameterisation()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var img = SyntheticCorridor.ToNetworkOutput(r, 2f, 0.05f, parameterisation: DepthParameterisation.AffineDepth);
            var a = Align(img, SyntheticCorridor.Floor, cfg: new DetectionConfig { parameterisation = DepthParameterisation.AffineDepth });
            Assert.IsTrue(a.success, a.reason);
            Assert.AreEqual(2f, a.s, 0.02f);
            Assert.AreEqual(0.05f, a.t, 0.01f);
        }

        [Test]
        public void FloorMask_CoversFloor_NotWalls()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var img = SyntheticCorridor.ToNetworkOutput(r);
            var a = Align(img, SyntheticCorridor.Floor);
            Assert.IsTrue(a.success && a.hasFloorMask);
            int floor = 0, floorMasked = 0, wall = 0, wallMasked = 0;
            for (int v = 0; v < 518; v++)
            for (int u = 65; u < 453; u++)
            {
                int i = v * 518 + u;
                if (float.IsNaN(r.depth[i])) continue;
                bool isFloor = SyntheticCorridor.Floor.TryIntersect(img.cameraPose.position, img.WorldRay(u, v), out _, out float t)
                               && Mathf.Abs(t - r.depth[i]) < 1e-3f * t;
                if (isFloor) { floor++; if (a.floorMask[i]) floorMasked++; }
                else { wall++; if (a.floorMask[i]) wallMasked++; }
            }
            Assert.Greater((float)floorMasked / floor, 0.95f);
            // Wall pixels within ~8 cm of the base are within the 6% tolerance of the floor behind them.
            Assert.Less((float)wallMasked / wall, 0.10f);
        }

        // ------------------------------------------------------------ MetricSampleCollector

        static InverseDepthImage InferenceCamera(Pose pose) => new InverseDepthImage(518, 518)
        {
            intrinsics = new DepthIntrinsics { fx = 391f, fy = 391f, cx = 258.5f, cy = 258.5f, width = 518, height = 518 },
            cameraPose = pose,
            content = new RectInt(0, 0, 518, 518),
        };

        [Test]
        public void Collector_MapsConfidentRawDepthIntoInferenceImage()
        {
            var pose = new Pose(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            var raw = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, timestamp: 1.0, confidence: 200);
            var list = new List<MetricSample>();
            int n = MetricSampleCollector.Collect(raw, InferenceCamera(pose), 128, 1.05, 0.5, 2, list);
            Assert.Greater(n, 1000);
            Assert.AreEqual(n, list.Count);
            foreach (var s in list) Assert.AreEqual(2f, s.depthMeters, 0.002f);
        }

        [Test]
        public void Collector_RejectsLowConfidence_Stale_AndMissingConfidence()
        {
            var pose = new Pose(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
            var img = InferenceCamera(pose);
            var list = new List<MetricSample>();
            var low = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, 1.0, confidence: 100);
            Assert.AreEqual(0, MetricSampleCollector.Collect(low, img, 128, 1.05, 0.5, 2, list), "below 0.5 confidence");
            var ok = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, 1.0, confidence: 200);
            Assert.AreEqual(0, MetricSampleCollector.Collect(ok, img, 128, 2.0, 0.5, 2, list), "stale");
            ok.confidence = null;
            Assert.AreEqual(0, MetricSampleCollector.Collect(ok, img, 128, 1.05, 0.5, 2, list), "no confidence image");
            Assert.AreEqual(0, MetricSampleCollector.Collect(null, img, 128, 1.05, 0.5, 2, list), "no frame");
        }
    }
}
