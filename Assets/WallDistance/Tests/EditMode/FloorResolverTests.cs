using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class FloorResolverTests
    {
        [Test]
        public void ArFloor_Wins_AndWarmsTheEstimator()
        {
            var resolver = new FloorResolver(new DetectionConfig());
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var img = SyntheticCorridor.ToNetworkOutput(r);
            var floor = resolver.Resolve(img, SyntheticCorridor.Floor, null, 1.0);
            Assert.AreEqual(SyntheticCorridor.Floor.point, floor.point);
            Assert.AreEqual(FloorSourceKind.ArPlane, resolver.LastSource);
            Assert.AreEqual(200f, resolver.Estimator.WindowWeight, "the ARCore floor is also a clue");
            Assert.IsFalse(resolver.LastSelfAlignOk, "self-alignment is skipped when ARCore has the floor");
        }

        [Test]
        public void NoArFloor_CorridorWalk_DerivedFloorThenWalls()
        {
            // The run-150956 situation: no ARCore floor at any point, ~40 confident raw-depth
            // pixels per frame (median there: 35), walking down the corridor at 1 m/s, 10 Hz.
            var cfg = new MeasurementConfig();
            var resolver = new FloorResolver(cfg.detection);
            var pipe = new WallDetectionPipeline(cfg);
            var rng = new System.Random(11);
            FloorPlane floor = default;
            Pose pose = default;
            for (int k = 0; k <= 25; k++)
            {
                double t = k * 0.1;
                pose = SyntheticCorridor.Camera(z: -2.5f + 0.1f * k);
                var r = new SyntheticCorridor().Render(pose);
                var img = SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f, relativeNoise: 0.01f, seed: k + 1);
                img.timestamp = t;
                var samples = HeightCluesTests.Samples(r, 40, rng);
                floor = resolver.Resolve(img, default, samples, t);
                pipe.ProcessFrame("s1", img, floor, samples, t);
                if (k == 10)
                {
                    Assert.AreEqual(FloorSourceKind.None, resolver.LastSource, "1 s in: still calibrating");
                    Assert.AreEqual(FailureReason.Calibrating, resolver.Estimator.Explain(pipe.DetectionFailure(t, true), t));
                }
            }
            Assert.AreEqual(FloorSourceKind.Derived, resolver.LastSource);
            Assert.IsTrue(resolver.LastSelfAlignOk);
            Assert.AreEqual(40, resolver.LastClueSamples);
            Assert.AreEqual(1.4f, floor.HeightAbove(pose.position), 0.03f);

            // Side walls within 5% (spec §6): right at 0.8 m, left at 1.2 m from x = 0.2.
            bool right = false, left = false;
            foreach (var tr in pipe.Map.Tracks)
            {
                float dist = Mathf.Abs(tr.SignedDistance(pose.position));
                if (Vector3.Angle(tr.normal, Vector3.left) < 3f && Mathf.Abs(dist - 0.8f) <= 0.04f) right = true;
                if (Vector3.Angle(tr.normal, Vector3.right) < 3f && Mathf.Abs(dist - 1.2f) <= 0.06f) left = true;
            }
            Assert.IsTrue(right, "right wall at 0.8 m ± 5%");
            Assert.IsTrue(left, "left wall at 1.2 m ± 5%");
        }

        [Test]
        public void MarkDiscontinuity_ReachesTheEstimator()
        {
            var resolver = new FloorResolver(new DetectionConfig());
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
            for (int k = 0; k <= 20; k++) resolver.Resolve(img, SyntheticCorridor.Floor, null, k * 0.1);
            resolver.MarkDiscontinuity();
            Assert.AreEqual(0, resolver.Estimator.WindowClues);
        }

        [Test]
        public void Reset_ForgetsTheFloor()
        {
            var resolver = new FloorResolver(new DetectionConfig());
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
            for (int k = 0; k <= 20; k++) resolver.Resolve(img, SyntheticCorridor.Floor, null, k * 0.1);
            resolver.Reset();
            Assert.AreEqual(FloorSourceKind.None, resolver.LastSource);
            Assert.AreEqual(0, resolver.Estimator.WindowClues);
        }
    }
}
