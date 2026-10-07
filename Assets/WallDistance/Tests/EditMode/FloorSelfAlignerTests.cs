using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class FloorSelfAlignerTests
    {
        const float CameraHeight = 1.4f; // SyntheticCorridor.Camera default; the floor is y = 0

        static SelfAlignment Align(InverseDepthImage img, DetectionConfig cfg = null) =>
            new FloorSelfAligner(cfg ?? new DetectionConfig()).Align(img);

        /// <summary>
        /// Median relative error of the self-aligned depth on wall pixels, scaled by the TRUE
        /// camera height. That isolates what self-alignment is responsible for (the shift and
        /// the floor's relative height) from the metric clue, which later tasks supply.
        /// </summary>
        static float WallDepthError(SyntheticCorridor.Rendered r, SelfAlignment a)
        {
            var errs = new List<float>();
            var img = r.image;
            for (int i = 0; i < img.values.Length; i += 7)
            {
                if (img.luma[i] != SyntheticCorridor.WallLuma || float.IsNaN(r.depth[i])) continue;
                float z = a.MetricDepth(img.values[i], CameraHeight);
                errs.Add(Mathf.Abs(z - r.depth[i]) / r.depth[i]);
            }
            Assert.Greater(errs.Count, 100, "test needs wall pixels in view");
            errs.Sort();
            return errs[errs.Count / 2];
        }

        [Test]
        public void Selection_Kth_MatchesSort()
        {
            var rng = new System.Random(4);
            for (int trial = 0; trial < 20; trial++)
            {
                int n = 1 + rng.Next(200);
                var a = new float[n];
                // Few distinct values on purpose: duplicates are the classic quickselect failure.
                for (int i = 0; i < n; i++) a[i] = rng.Next(10);
                var sorted = (float[])a.Clone();
                System.Array.Sort(sorted);
                int k = rng.Next(n);
                Assert.AreEqual(sorted[k], Selection.Kth(a, n, k));
            }
        }

        [Test]
        public void ExactData_RecoversWallDepth()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var a = Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            Assert.LessOrEqual(WallDepthError(r, a), 0.005f);
            Assert.GreaterOrEqual(a.depthRatio, 2f);
        }

        [Test]
        public void UnknownAffineAndNoise_RecoversWallDepth()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f, relativeNoise: 0.01f, seed: 3);
            var a = Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            // Per-pixel noise alone gives a median |error| of ~0.7% (|N(0, 1%)|); 1.5% leaves ~0.8% for the shift.
            Assert.LessOrEqual(WallDepthError(r, a), 0.015f);
        }

        [TestCase(10f)]
        [TestCase(30f)]
        public void CameraPitch_RecoversWallDepth(float pitchDown)
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(pitchDown: pitchDown));
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var a = Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            Assert.LessOrEqual(WallDepthError(r, a), 0.01f);
        }

        [Test]
        public void NetworkScale_DoesNotChangeMetricDepth()
        {
            // Depth Anything's scale differs per frame; only the shift/scale ratio may matter.
            foreach (float scale in new[] { 0.5f, 2f, 7f })
            {
                var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
                SyntheticCorridor.ToNetworkOutput(r, scale: scale, shift: 0.05f);
                var a = Align(r.image);
                Assert.IsTrue(a.success, $"scale {scale}: {a.reason}");
                Assert.LessOrEqual(WallDepthError(r, a), 0.005f, $"scale {scale}");
            }
        }

        [Test]
        public void LookingUp_NoFloorInView_Fails()
        {
            // Pitched 35° up: every ray is above the horizon, so nothing can be floor.
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(pitchDown: -35f));
            SyntheticCorridor.ToNetworkOutput(r);
            var a = Align(r.image);
            Assert.IsFalse(a.success);
            StringAssert.Contains("downward", a.reason);
        }

        [Test]
        public void WallCloseUp_IsRejected()
        {
            // Facing the right wall from 0.5 m, pitched 55° down: the floor in view spans a narrow
            // depth band, which is flat for many shifts. The 10-07 dumps had such frames.
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(x: 0.5f, pitchDown: 55f, yaw: 90f));
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var a = Align(r.image);
            Assert.IsFalse(a.success, $"must not trust a shift fixed by a sliver of floor (ratio {a.depthRatio:F2})");
        }

        [Test]
        public void ReflectionsOnGlossyFloor_StillRecoverWallDepth()
        {
            // 15% of floor pixels read farther than the floor, as mirror images in a glossy floor
            // do. Random speckle is a weaker stand-in than real coherent reflections; the field
            // run is the real check (spec §8).
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var img = SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var rng = new System.Random(9);
            for (int i = 0; i < img.values.Length; i++)
                if (img.luma[i] == SyntheticCorridor.FloorLuma && rng.NextDouble() < 0.15) img.values[i] *= 0.6f;
            var a = Align(img);
            Assert.IsTrue(a.success, a.reason);
            Assert.LessOrEqual(WallDepthError(r, a), 0.02f);
        }

        [Test]
        public void DoorRecessAndPerson_DoNotBreakTheFloor()
        {
            var corridor = new SyntheticCorridor { door = true, person = true };
            var r = corridor.Render(SyntheticCorridor.Camera());
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            var a = Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            Assert.LessOrEqual(WallDepthError(r, a), 0.01f);
        }

        [Test]
        public void AffineDepthParameterisation_IsUnsupported()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            SyntheticCorridor.ToNetworkOutput(r);
            var a = Align(r.image, new DetectionConfig { parameterisation = DepthParameterisation.AffineDepth });
            Assert.IsFalse(a.success);
            StringAssert.Contains("AffineInverseDepth", a.reason);
        }
    }
}
