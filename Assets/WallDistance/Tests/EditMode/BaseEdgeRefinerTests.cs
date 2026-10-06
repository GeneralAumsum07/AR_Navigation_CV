using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class BaseEdgeRefinerTests
    {
        /// <summary>
        /// The corridor's right wall (x = 1, facing -x) as a learned observation, shifted toward
        /// the camera by <paramref name="offset"/> and rotated by <paramref name="angleDeg"/>.
        /// Extent covers z 2..5, the part a camera at z = 0 pitched 30° down sees with its base.
        /// </summary>
        static WallObservation RightWall(float offset, float angleDeg = 0f) => new WallObservation
        {
            origin = new Vector3(1f - offset, 0f, 3.5f),
            normal = Quaternion.AngleAxis(angleDeg, Vector3.up) * Vector3.left,
            up = Vector3.up, extentMin = -1.5f, extentMax = 1.5f,
            inliers = 500, rms = 0.02f, source = MeasurementSource.LearnedDepth,
        };

        static InverseDepthImage Image(SyntheticCorridor c, Pose pose) => c.Render(pose).image;

        static void AssertOnTrueBase(WallObservation r)
        {
            // The true base is x = 1 along the whole extent.
            for (float a = -1.4f; a <= 1.41f; a += 0.7f)
                Assert.AreEqual(1f, r.PointAt(a).x, 0.015f, $"base at along={a}");
            Assert.LessOrEqual(Vector3.Angle(r.normal, Vector3.left), 0.5f);
        }

        [Test]
        public void PerturbedBase_SnapsToTrueBase([Values(-0.05f, 0.05f)] float offset)
        {
            var img = Image(new SyntheticCorridor(), SyntheticCorridor.Camera(pitchDown: 30f));
            var obs = RightWall(offset, angleDeg: 1f);
            Assert.IsTrue(new BaseEdgeRefiner(new DetectionConfig()).TryRefine(img, SyntheticCorridor.Floor, obs, out var r));
            AssertOnTrueBase(r);
            Assert.AreEqual(MeasurementSource.FloorEdge, r.source);
            Assert.GreaterOrEqual(r.edgeSnapFraction, 0.6f);
            Assert.AreEqual(obs.extentMin, r.extentMin);
            Assert.AreEqual(obs.extentMax, r.extentMax);
            Assert.AreEqual(MeasurementSource.LearnedDepth, obs.source, "input is not modified");
        }

        [Test]
        public void GroutLineParallelToWall_DoesNotCaptureSnap()
        {
            // Review Focus 1: a dark grout line 20 cm from the wall base. The learned base is 6 cm
            // short of the wall, i.e. 14 cm from the grout. The ±8 cm band (D8) excludes the grout;
            // with a plain ±12 px band it would be inside the band at this range.
            var corridor = new SyntheticCorridor();
            corridor.groutLinesX.Add(0.80f);
            var img = Image(corridor, SyntheticCorridor.Camera(pitchDown: 30f));
            Assert.IsTrue(new BaseEdgeRefiner(new DetectionConfig()).TryRefine(img, SyntheticCorridor.Floor, RightWall(0.06f), out var r));
            AssertOnTrueBase(r);
        }

        [Test]
        public void BaseOutOfView_RefinerDeclinesAndLeavesObservationUnchanged()
        {
            // Review Focus 5: level phone 0.4 m from the right wall; the lowest visible wall point
            // is ~1.1 m up, so there is no base edge to snap to.
            var img = Image(new SyntheticCorridor(), SyntheticCorridor.Camera(x: 0.6f, pitchDown: 0f, yaw: 90f));
            var obs = new WallObservation
            {
                origin = new Vector3(1f, 0f, 0f), normal = Vector3.left, up = Vector3.up,
                extentMin = -0.3f, extentMax = 0.3f, inliers = 400, rms = 0.015f, source = MeasurementSource.LearnedDepth,
            };
            Assert.IsFalse(new BaseEdgeRefiner(new DetectionConfig()).TryRefine(img, SyntheticCorridor.Floor, obs, out var r));
            Assert.AreSame(obs, r);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), obs.origin);
            Assert.AreEqual(MeasurementSource.LearnedDepth, obs.source);
            Assert.IsTrue(float.IsNaN(obs.edgeSnapFraction));
        }

        [Test]
        public void NoFloor_Declines()
        {
            var img = Image(new SyntheticCorridor(), SyntheticCorridor.Camera(pitchDown: 30f));
            var obs = RightWall(0.05f);
            Assert.IsFalse(new BaseEdgeRefiner(new DetectionConfig()).TryRefine(img, default, obs, out var r));
            Assert.AreSame(obs, r);
        }
    }
}
