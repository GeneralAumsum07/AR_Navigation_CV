using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class FloorPlaneTests
    {
        static readonly FloorPlane Floor = new FloorPlane(new Vector3(0f, 0.1f, 0f), Vector3.up);

        [Test]
        public void HeightAbove_IsSignedDistanceAlongUp()
        {
            Assert.AreEqual(1.4f, Floor.HeightAbove(new Vector3(1f, 1.5f, 2f)), 1e-6f);
            Assert.AreEqual(-0.1f, Floor.HeightAbove(new Vector3(0f, 0f, 0f)), 1e-6f);
        }

        [Test]
        public void Project_DropsPointOntoFloor()
        {
            Vector3 p = Floor.Project(new Vector3(1f, 2f, 3f));
            // Subtracting the height introduces float rounding; geometry is tested to a micrometre.
            Assert.That(Vector3.Distance(new Vector3(1f, 0.1f, 3f), p), Is.LessThan(1e-6f));
        }

        [Test]
        public void TryIntersect_DescendingRay_HitsAtZDepth()
        {
            // dir has unit z, so t equals the Z-depth: the convention every depth image here uses.
            var f = new FloorPlane(Vector3.zero, Vector3.up);
            Assert.IsTrue(f.TryIntersect(new Vector3(0f, 1.4f, 0f), new Vector3(0f, -1f, 1f), out Vector3 hit, out float t));
            Assert.AreEqual(1.4f, t, 1e-5f);
            Assert.AreEqual(0f, hit.y, 1e-5f);
            Assert.AreEqual(1.4f, hit.z, 1e-5f);
        }

        [Test]
        public void TryIntersect_RejectsRisingParallelAndBelowFloorRays()
        {
            var f = new FloorPlane(Vector3.zero, Vector3.up);
            Assert.IsFalse(f.TryIntersect(new Vector3(0f, 1.4f, 0f), new Vector3(0f, 1f, 1f), out _, out _), "rising");
            Assert.IsFalse(f.TryIntersect(new Vector3(0f, 1.4f, 0f), new Vector3(0f, 0f, 1f), out _, out _), "parallel");
            Assert.IsFalse(f.TryIntersect(new Vector3(0f, -0.5f, 0f), new Vector3(0f, -1f, 1f), out _, out _), "origin below floor");
        }

        [Test]
        public void Default_IsInvalid_AndUpIsNormalised()
        {
            Assert.IsFalse(default(FloorPlane).IsValid);
            var f = new FloorPlane(Vector3.zero, new Vector3(0f, 2f, 0f));
            Assert.IsTrue(f.IsValid);
            Assert.AreEqual(1f, f.up.magnitude, 1e-6f);
        }

        [Test]
        public void PlausibleCameraHeight_MatchesAssistedModeBounds()
        {
            var f = new FloorPlane(Vector3.zero, Vector3.up);
            Assert.IsFalse(f.PlausibleCameraHeight(new Vector3(0f, 0.3f, 0f)));
            Assert.IsTrue(f.PlausibleCameraHeight(new Vector3(0f, 1.4f, 0f)));
            Assert.IsFalse(f.PlausibleCameraHeight(new Vector3(0f, 2.6f, 0f)));
        }

        [Test]
        public void Horizontal_TryDirection_FlattensAndRejectsVertical()
        {
            Assert.IsTrue(Horizontal.TryDirection(new Vector3(1f, 1f, 0f), Vector3.up, out Vector3 d));
            Assert.AreEqual(Vector3.right, d);
            Assert.IsFalse(Horizontal.TryDirection(Vector3.up * 3f, Vector3.up, out _));
        }
    }
}
