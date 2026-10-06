using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class WallGeometryTests
    {
        [Test]
        public void PerpendicularDistance_IsIndependentOfViewAngle()
        {
            var wall = Fx.WallAtZ(2f);
            var camPos = new Vector3(0f, 0f, 0f);
            // Perpendicular distance does not depend on which way the camera looks.
            Assert.AreEqual(2f, WallGeometry.PerpendicularDistance(wall, camPos), 1e-5f);
            // ... and moving sideways along the wall does not change it either.
            Assert.AreEqual(2f, WallGeometry.PerpendicularDistance(wall, new Vector3(1.2f, 0.3f, 0f)), 1e-5f);
        }

        [TestCase(0f)]
        [TestCase(30f)]
        [TestCase(60f)]
        public void RayDistance_ExceedsPerpendicular_ByOneOverCos(float angleDeg)
        {
            var wall = Fx.WallAtZ(2f, halfWidth: 10f, halfHeight: 10f);
            var dir = Quaternion.Euler(0f, angleDeg, 0f) * Vector3.forward;
            Assert.IsTrue(WallGeometry.RaycastPolygon(wall, new Ray(Vector3.zero, dir), out _, out float t));
            float expected = 2f / Mathf.Cos(angleDeg * Mathf.Deg2Rad);
            Assert.AreEqual(expected, t, 1e-4f, "ray travel distance");
            Assert.AreEqual(2f, WallGeometry.PerpendicularDistance(wall, Vector3.zero), 1e-5f, "perpendicular is still 2 m");
        }

        [Test]
        public void RaycastPolygon_RejectsHitsOutsideBoundary()
        {
            // 1 m wide wall; a ray aimed 2 m to the side hits the infinite plane but not the polygon.
            var wall = Fx.WallAtZ(2f, halfWidth: 0.5f);
            var dir = (new Vector3(2f, 0f, 2f)).normalized;
            Assert.IsFalse(WallGeometry.RaycastPolygon(wall, new Ray(Vector3.zero, dir), out _, out _));
            Assert.IsTrue(WallGeometry.RaycastPolygon(wall, new Ray(Vector3.zero, Vector3.forward), out _, out _));
        }

        [Test]
        public void RaycastPolygon_RejectsPlaneBehindRay()
        {
            var wall = Fx.WallAtZ(-2f);
            Assert.IsFalse(WallGeometry.RaycastPolygon(wall, new Ray(Vector3.zero, Vector3.forward), out _, out _));
        }

        [Test]
        public void NearestPointOnPolygon_UsesEdge_WhenFootIsOutsidePolygon()
        {
            // Doorway case: wall spans x in [-3,-1] at z=1. Camera at origin. The infinite plane is
            // 1 m away, but the bounded wall ends 1 m to the left, so nearest is sqrt(1^2+1^2).
            var wall = Fx.WallAtZ(1f, halfWidth: 1f, xCenter: -2f);
            float d = WallGeometry.NearestPointOnPolygon(wall, Vector3.zero, out Vector3 p);
            Assert.AreEqual(Mathf.Sqrt(2f), d, 1e-4f);
            Assert.AreEqual(-1f, p.x, 1e-4f);
            Assert.AreEqual(1f, p.z, 1e-4f);
        }

        [Test]
        public void NearestPointOnPolygon_UsesPerpendicular_WhenFootIsInside()
        {
            var wall = Fx.WallAtZ(1.5f);
            float d = WallGeometry.NearestPointOnPolygon(wall, new Vector3(0.5f, 0.2f, 0f), out Vector3 p);
            Assert.AreEqual(1.5f, d, 1e-4f);
            Assert.AreEqual(new Vector3(0.5f, 0.2f, 1.5f).ToString("F4"), p.ToString("F4"));
        }

        [Test]
        public void PointInPolygon_HandlesConcaveShapes()
        {
            // L-shape: the notch at (1.5, 1.5) must be outside.
            var poly = new[]
            {
                new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 1),
                new Vector2(1, 1), new Vector2(1, 2), new Vector2(0, 2),
            };
            Assert.IsTrue(WallGeometry.PointInPolygon(poly, new Vector2(0.5f, 0.5f)));
            Assert.IsTrue(WallGeometry.PointInPolygon(poly, new Vector2(0.5f, 1.5f)));
            Assert.IsFalse(WallGeometry.PointInPolygon(poly, new Vector2(1.5f, 1.5f)));
        }

        [Test]
        public void PolygonArea_Shoelace()
        {
            var wall = Fx.WallAtZ(1f, halfWidth: 1f, halfHeight: 0.5f);
            Assert.AreEqual(2f, wall.Area(), 1e-5f);
        }

        [Test]
        public void IntersectsView_TrueForWallAhead_FalseForWallBehind()
        {
            var cam = Fx.MakeCamera(Vector3.zero, Quaternion.identity);
            try
            {
                var m = cam.projectionMatrix * cam.worldToCameraMatrix;
                Assert.IsTrue(WallGeometry.IntersectsView(Fx.WallAtZ(2f), m));
                Assert.IsFalse(WallGeometry.IntersectsView(Fx.WallAtZ(-2f), m));
                // Off to the side, outside a 60 deg vertical / ~36 deg horizontal FOV.
                Assert.IsFalse(WallGeometry.IntersectsView(Fx.WallAtZ(2f, halfWidth: 0.2f, halfHeight: 0.2f, xCenter: 5f), m));
            }
            finally { Object.DestroyImmediate(cam.gameObject); }
        }

        [Test]
        public void IntersectsView_TrueWhenOnlyInteriorIsVisible()
        {
            // Huge wall very close: every vertex is off-screen but the middle fills the view.
            var cam = Fx.MakeCamera(Vector3.zero, Quaternion.identity);
            try
            {
                var m = cam.projectionMatrix * cam.worldToCameraMatrix;
                Assert.IsTrue(WallGeometry.IntersectsView(Fx.WallAtZ(0.5f, halfWidth: 20f, halfHeight: 20f), m));
            }
            finally { Object.DestroyImmediate(cam.gameObject); }
        }
    }
}
