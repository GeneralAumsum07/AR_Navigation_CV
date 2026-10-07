using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class HeightCluesTests
    {
        /// <summary>Sparse, true-depth samples at random content pixels: what confident raw depth gives.</summary>
        internal static List<MetricSample> Samples(SyntheticCorridor.Rendered r, int count, System.Random rng, float depthFactor = 1f)
        {
            var samples = new List<MetricSample>();
            int size = r.image.width;
            RectInt c = r.image.content;
            while (samples.Count < count)
            {
                int u = rng.Next(c.xMin, c.xMax), v = rng.Next(c.yMin, c.yMax);
                float z = r.depth[v * size + u];
                if (!float.IsNaN(z)) samples.Add(new MetricSample { pixel = new Vector2(u, v), depthMeters = z * depthFactor });
            }
            return samples;
        }

        static (SyntheticCorridor.Rendered r, SelfAlignment a) Aligned(float cameraHeight = 1.4f)
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(h: cameraHeight));
            SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f, relativeNoise: 0.01f, seed: 2);
            var a = new FloorSelfAligner(new DetectionConfig()).Align(r.image);
            Assert.IsTrue(a.success, a.reason);
            return (r, a);
        }

        [Test]
        public void RawDepthSamples_GiveCameraHeight_Within2Percent()
        {
            var (r, a) = Aligned();
            var clues = new HeightClues(new DetectionConfig());
            Assert.IsTrue(clues.TryFromDepth(r.image, a, Samples(r, 40, new System.Random(1)), 3.0, out var c, out int used));
            Assert.AreEqual(40, used);
            Assert.AreEqual(1.4f, c.heightMeters, 0.028f);
            Assert.AreEqual(0f, c.floorY, 0.028f, "floor at y = 0 in the synthetic corridor");
            Assert.AreEqual(40f, c.weight);
            Assert.AreEqual(3.0, c.time);
            Assert.AreEqual(HeightClueSource.RawDepth, c.source);
        }

        [Test]
        public void DisagreeingSamples_GiveNoClue()
        {
            // Half the samples 50% too deep (a shiny door read through its reflection, say):
            // the frame cannot say which half is right, so it says nothing.
            var (r, a) = Aligned();
            var rng = new System.Random(2);
            var samples = Samples(r, 20, rng);
            samples.AddRange(Samples(r, 20, rng, depthFactor: 1.5f));
            Assert.IsFalse(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, a, samples, 1.0, out _, out _));
        }

        [TestCase(19, 21, false)]
        [TestCase(21, 19, false)]
        [TestCase(32, 8, true)]
        public void CompetingSurfaces_NeedClearAgreement(int correct, int reflected, bool accepted)
        {
            // Two surfaces can each be internally consistent. A one-pixel majority must not
            // make a 50% scale error look certain; an 80% consensus can tolerate sparse outliers.
            var img = new InverseDepthImage(correct + reflected, 1)
            {
                content = new RectInt(0, 0, correct + reflected, 1),
                cameraPose = new Pose(new Vector3(0, 1.4f, 0), Quaternion.identity),
            };
            var samples = new List<MetricSample>();
            for (int i = 0; i < img.width; i++)
            {
                img.values[i] = 1f;
                samples.Add(new MetricSample { pixel = new Vector2(i, 0), depthMeters = i < correct ? 1.4f : 2.1f });
            }
            var alignment = new SelfAlignment { success = true, r = 0, hRel = 1 };
            bool ok = new HeightClues(new DetectionConfig()).TryFromDepth(img, alignment, samples, 1, out var clue, out int used);
            Assert.AreEqual(accepted, ok);
            Assert.AreEqual(40, used);
            if (accepted) Assert.AreEqual(1.4f, clue.heightMeters, 1e-5f);
        }

        [Test]
        public void TooFewSamples_GiveNoClue_ButReportCount()
        {
            var (r, a) = Aligned();
            Assert.IsFalse(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, a, Samples(r, 5, new System.Random(3)), 1.0, out _, out int used));
            Assert.AreEqual(5, used);
        }

        [Test]
        public void LetterboxSamples_AreSkipped()
        {
            var (r, a) = Aligned();
            var samples = Samples(r, 12, new System.Random(4));
            // x = 10 is in the letterbox (content starts at 65): the network has no real depth there.
            for (int i = 0; i < 8; i++) samples.Add(new MetricSample { pixel = new Vector2(10, 100 + i), depthMeters = 2f });
            Assert.IsTrue(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, a, samples, 1.0, out _, out int used));
            Assert.AreEqual(12, used);
        }

        [Test]
        public void ImplausibleHeight_GivesNoClue()
        {
            // 0.5 m is below the handheld band (FloorPlane.MinCameraHeight = 0.8 m).
            var (r, a) = Aligned(cameraHeight: 0.5f);
            Assert.IsFalse(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, a, Samples(r, 40, new System.Random(5)), 1.0, out _, out _));
        }

        [Test]
        public void FailedSelfAlignment_GivesNoClue()
        {
            var (r, _) = Aligned();
            var failed = new SelfAlignment();
            Assert.IsFalse(new HeightClues(new DetectionConfig()).TryFromDepth(r.image, failed, Samples(r, 40, new System.Random(6)), 1.0, out _, out _));
        }

        [Test]
        public void ArFloor_GivesFullWeightClue_TableDoesNot()
        {
            var clues = new HeightClues(new DetectionConfig());
            var camera = new Vector3(0.3f, 1.4f, 2f);
            Assert.IsTrue(clues.TryFromPlane(new FloorPlane(Vector3.zero, Vector3.up), camera, 2.0, out var c));
            Assert.AreEqual(1.4f, c.heightMeters, 1e-5f);
            Assert.AreEqual(0f, c.floorY, 1e-5f);
            Assert.AreEqual(200f, c.weight);
            Assert.AreEqual(HeightClueSource.ArPlane, c.source);
            // A table 0.9 m up leaves the camera 0.5 m above it: not a floor.
            Assert.IsFalse(clues.TryFromPlane(new FloorPlane(new Vector3(0f, 0.9f, 0f), Vector3.up), camera, 2.0, out _));
        }
    }
}
