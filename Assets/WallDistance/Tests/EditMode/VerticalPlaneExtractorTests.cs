using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class VerticalPlaneExtractorTests
    {
        static List<WallObservation> Run(SyntheticCorridor c, Pose pose, float noise = 0f, int seed = 1)
        {
            var cfg = new DetectionConfig();
            var img = SyntheticCorridor.ToNetworkOutput(c.Render(pose), relativeNoise: noise, seed: seed);
            var align = new FloorAlignedDepth(cfg).Align(img, SyntheticCorridor.Floor, null);
            Assert.IsTrue(align.success, align.reason);
            if (c.door)
            {
                Assert.AreEqual(2f, align.s, 1e-3f, "exact doorway frame scale");
                Assert.AreEqual(0.05f, align.t, 1e-3f, "exact doorway frame shift");
            }
            var list = new List<WallObservation>();
            new VerticalPlaneExtractor(cfg).Extract(img, align, SyntheticCorridor.Floor, 0.0, list);
            return list;
        }

        /// <summary>Some observation faces <paramref name="normal"/> within maxDeg and is <paramref name="distance"/> from the camera within tol.</summary>
        static void AssertWall(List<WallObservation> obs, Vector3 normal, float distance, Vector3 cam, float maxDeg, float tol)
        {
            string seen = "";
            foreach (var o in obs)
            {
                float ang = Vector3.Angle(o.normal, normal), dist = o.SignedDistance(cam);
                seen += $"[n={o.normal} d={dist:F3} ang={ang:F2} len={o.Length:F2} rms={o.rms:F4} points={o.inliers}] ";
                if (ang <= maxDeg && Mathf.Abs(dist - distance) <= tol) return;
            }
            Assert.Fail($"no wall with normal {normal} at {distance} m; saw {seen}");
        }

        [Test]
        public void CleanCorridor_FindsLeftRightAndEndWalls()
        {
            var pose = SyntheticCorridor.Camera();
            var obs = Run(new SyntheticCorridor(), pose);
            Assert.LessOrEqual(obs.Count, 4);
            AssertWall(obs, Vector3.right, 1.2f, pose.position, 1f, 0.02f);   // left wall faces +x
            AssertWall(obs, Vector3.left, 0.8f, pose.position, 1f, 0.02f);    // right wall faces -x
            AssertWall(obs, Vector3.back, 5.5f, pose.position, 1f, 0.02f);    // end wall faces -z
            foreach (var o in obs)
            {
                Assert.AreEqual(0f, Vector3.Dot(o.normal, Vector3.up), 1e-4f, "normals are horizontal");
                Assert.AreEqual(0f, o.origin.y, 1e-4f, "origin is on the base line");
                Assert.AreEqual(MeasurementSource.LearnedDepth, o.source);
            }
        }

        [Test]
        public void NoisyCorridor_StillWithin5cmAnd3Degrees()
        {
            // 3% depth noise is a plan-chosen stand-in for network error; spec §8 fixes only the clean-data bound.
            var pose = SyntheticCorridor.Camera();
            var obs = Run(new SyntheticCorridor(), pose, noise: 0.03f, seed: 9);
            AssertWall(obs, Vector3.right, 1.2f, pose.position, 3f, 0.05f);
            AssertWall(obs, Vector3.left, 0.8f, pose.position, 3f, 0.05f);
        }

        [Test]
        public void PersonInFrontOfRightWall_WallStillFound()
        {
            // Review Focus 2: a person standing still may add a short spurious segment, but must not
            // replace or shift the real right wall.
            var pose = SyntheticCorridor.Camera();
            var obs = Run(new SyntheticCorridor { person = true }, pose);
            AssertWall(obs, Vector3.left, 0.8f, pose.position, 1f, 0.02f);
        }

        [Test]
        public void DoorRecess_MainLeftWallStillFound()
        {
            var pose = SyntheticCorridor.Camera();
            var obs = Run(new SyntheticCorridor { door = true }, pose);
            AssertWall(obs, Vector3.right, 1.2f, pose.position, 1f, 0.02f);
        }

        [Test]
        public void FailedAlignment_ProducesNothing()
        {
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
            var list = new List<WallObservation> { null };
            int n = new VerticalPlaneExtractor(new DetectionConfig()).Extract(img, new AlignmentResult(), SyntheticCorridor.Floor, 0.0, list);
            Assert.AreEqual(0, n);
            Assert.AreEqual(0, list.Count, "output is cleared");
        }

        [Test]
        public void Observation_DirectionAndExtentConventions()
        {
            var o = new WallObservation
            {
                origin = new Vector3(1f, 0f, 3f), normal = Vector3.left, up = Vector3.up, extentMin = -1f, extentMax = 2f,
            };
            Assert.AreEqual(Vector3.back, o.direction, "Cross(normal, up)");
            Assert.AreEqual(3f, o.Length, 1e-6f);
            Assert.AreEqual(new Vector3(1f, 0f, 1f), o.PointAt(2f));
            Assert.AreEqual(0.8f, o.SignedDistance(new Vector3(0.2f, 1.4f, 0f)), 1e-6f);
            var c = o.Clone();
            c.origin = Vector3.zero;
            Assert.AreEqual(new Vector3(1f, 0f, 3f), o.origin, "Clone is a copy");
        }
    }
}
