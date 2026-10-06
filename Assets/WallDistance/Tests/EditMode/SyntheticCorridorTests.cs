using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    /// <summary>The fixture is the ground truth for every algorithm test, so it is tested first.</summary>
    public class SyntheticCorridorTests
    {
        [Test]
        public void FloorPixel_DepthMatchesAnalyticFloorIntersection()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            int u = 259, v = 450, i = v * 518 + u;
            Assert.IsTrue(SyntheticCorridor.Floor.TryIntersect(r.image.cameraPose.position, r.image.WorldRay(u, v), out _, out float t));
            Assert.AreEqual(t, r.depth[i], 1e-4f);
            Assert.AreEqual(SyntheticCorridor.FloorLuma, r.image.luma[i]);
        }

        [Test]
        public void Padding_IsNaNDepthAndBlack()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            Assert.AreEqual(new RectInt(65, 0, 388, 518), r.image.content, "matches the device's portrait letterbox");
            Assert.IsTrue(float.IsNaN(r.depth[0]));
            Assert.AreEqual(0, r.image.luma[0]);
        }

        [Test]
        public void FacingRightWall_CentreDepthIs0_8()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(pitchDown: 0f, yaw: 90f));
            Assert.AreEqual(0.8f, r.depth[259 * 518 + 259], 1e-4f);
        }

        [Test]
        public void DoorRecess_IsDeeperThanWall()
        {
            var pose = SyntheticCorridor.Camera(z: 2.5f, pitchDown: 0f, yaw: -90f);
            var plain = new SyntheticCorridor().Render(pose);
            var withDoor = new SyntheticCorridor { door = true }.Render(pose);
            int c = 259 * 518 + 259;
            Assert.AreEqual(1.2f, plain.depth[c], 1e-4f);
            Assert.AreEqual(1.5f, withDoor.depth[c], 1e-4f);
        }

        [Test]
        public void Skirting_IsDarkerThanWall_AndGroutIsDrawn()
        {
            var corridor = new SyntheticCorridor();
            corridor.groutLinesX.Add(0.8f);
            var r = corridor.Render(SyntheticCorridor.Camera(pitchDown: 45f, yaw: 90f));
            Assert.AreEqual(SyntheticCorridor.SkirtingLuma, LumaAt(r, new Vector3(1f, 0.05f, 0.2f)));
            Assert.AreEqual(SyntheticCorridor.WallLuma, LumaAt(r, new Vector3(1f, 0.5f, 0.2f)));
            var g = corridor.Render(SyntheticCorridor.Camera());
            Assert.AreEqual(SyntheticCorridor.GroutLuma, LumaAt(g, new Vector3(0.8f, 0f, 2f)));
        }

        [Test]
        public void NetworkOutput_IsAffineInInverseDepth()
        {
            var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
            var img = SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
            int i = 450 * 518 + 259;
            Assert.AreEqual((1f / r.depth[i] - 0.05f) / 2f, img.values[i], 1e-6f);
            Assert.IsTrue(float.IsNaN(img.values[0]));
        }

        static byte LumaAt(SyntheticCorridor.Rendered r, Vector3 world)
        {
            Assert.IsTrue(r.image.TryProject(world, out float u, out float v, out _));
            int iu = Mathf.RoundToInt(u), iv = Mathf.RoundToInt(v);
            Assert.IsTrue(r.image.InContent(iu, iv), $"({iu},{iv}) outside content");
            return r.image.luma[iv * r.image.width + iu];
        }
    }
}
