using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class AssistedWallTests
    {
        [Test]
        public void TwoFloorPoints_DefineVerticalWallAtOneMetre()
        {
            Assert.IsTrue(AssistedWallGeometry.TryCreate(new Vector3(-0.5f,0,1),
                new Vector3(0.5f,0,1), Vector3.up, new Vector3(0,1.2f,0),
                out var pose, out var width, out var reason), reason);
            Assert.AreEqual(1, width, 0.001f);
            Assert.AreEqual(1, Mathf.Abs(Vector3.Dot(new Vector3(0,1.2f,0)-pose.position,
                pose.rotation * Vector3.up)), 0.001f);
            Assert.Less(Mathf.Abs((pose.rotation * Vector3.up).y), 0.001f);
        }

        [Test]
        public void ShortBaseline_IsRejected()
        {
            Assert.IsFalse(AssistedWallGeometry.TryCreate(Vector3.zero, Vector3.right * 0.1f,
                Vector3.up, Vector3.back, out _, out _, out _));
        }

        [Test]
        public void DifferentFloorHeights_AreRejected()
        {
            Assert.IsFalse(AssistedWallGeometry.TryCreate(Vector3.zero, new Vector3(1,0.3f,0),
                Vector3.up, Vector3.back, out _, out _, out _));
        }

        [Test]
        public void ReversedEndpoints_StillFaceCamera()
        {
            Assert.IsTrue(AssistedWallGeometry.TryCreate(Vector3.right, Vector3.left,
                Vector3.up, Vector3.back, out var pose, out _, out _));
            Assert.Greater(Vector3.Dot(pose.rotation * Vector3.up, Vector3.back), 0.99f);
        }
    }
}
