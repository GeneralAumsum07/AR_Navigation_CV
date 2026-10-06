using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class DepthValidatorTests
    {
        Camera _cam;
        Matrix4x4 _clip;
        MeasurementConfig _cfg;

        [SetUp]
        public void SetUp()
        {
            _cam = Fx.MakeCamera(Vector3.zero, Quaternion.identity, fov: 60f);
            _clip = _cam.projectionMatrix * _cam.worldToCameraMatrix;
            _cfg = new MeasurementConfig();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_cam.gameObject);

        Pose CamPose => new Pose(_cam.transform.position, _cam.transform.rotation);

        [Test]
        public void Projection_RoundTrips_ThroughUnproject()
        {
            var f = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 2f), Vector3.back, 0);
            var world = new Vector3(0.3f, -0.2f, 2f);
            Assert.IsTrue(f.TryProject(world, out int u, out int v, out float z));
            Assert.AreEqual(2f, z, 1e-5f);
            var back = f.Unproject(u, v, z);
            // Pixel quantisation limits the round-trip; at 160x90 over 60 deg that is ~1.5 cm at 2 m.
            Assert.Less(Vector3.Distance(world, back), 0.02f);
        }

        [Test]
        public void Projection_HandlesRotatedCamera()
        {
            // Camera turned 30 deg left, wall still at z=2. A point on the wall must project to
            // the right of image centre.
            _cam.transform.rotation = Quaternion.Euler(0, -30f, 0);
            var f = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 2f), Vector3.back, 0);
            Assert.IsTrue(f.TryProject(new Vector3(0f, 0f, 2f), out int u, out int v, out _));
            Assert.Greater(u, f.width / 2);
            Assert.AreEqual(f.height / 2, v, 1);
        }

        [Test]
        public void DepthUnits_AreMillimetres()
        {
            var f = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 1.234f), Vector3.back, 0);
            Assert.AreEqual(1234, f.depthMillimeters[(f.height / 2) * f.width + f.width / 2]);
            Assert.AreEqual(1.234f, f.DepthAt(f.width / 2, f.height / 2), 1e-4f);
        }

        [Test]
        public void ZeroDepth_IsInvalid_NotZeroMetres()
        {
            var f = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 2f), Vector3.back, 0);
            f.depthMillimeters[0] = 0;
            Assert.IsTrue(float.IsNaN(f.DepthAt(0, 0)));
        }

        [Test]
        public void PerfectDepth_ValidatesPlane_WithNearZeroResidual()
        {
            var wall = Fx.WallAtZ(2f);
            var f = Fx.DepthForPlane(CamPose, wall.position, wall.normal, timestamp: 10.0);
            var r = new DepthValidator().Validate(wall, f, _cfg, now: 10.1);
            Assert.IsTrue(r.depthUsed, r.reason);
            Assert.AreEqual(0f, r.medianResidualMeters, 0.01f);
            Assert.AreEqual(1f, r.inlierFraction, 1e-3f);
            Assert.GreaterOrEqual(r.validSamples, _cfg.depthMinValidSamples);
        }

        [TestCase(30f)]
        [TestCase(60f)]
        public void PerfectDepth_ValidatesPlane_AtObliqueAngles(float yawDeg)
        {
            // Rotate the camera; the wall is wide enough to stay in view.
            _cam.transform.rotation = Quaternion.Euler(0, yawDeg, 0);
            _clip = _cam.projectionMatrix * _cam.worldToCameraMatrix;
            var wall = Fx.WallAtZ(2f, halfWidth: 10f, halfHeight: 3f);
            var f = Fx.DepthForPlane(CamPose, wall.position, wall.normal, timestamp: 1.0);
            var r = new DepthValidator().Validate(wall, f, _cfg, now: 1.0);
            Assert.IsTrue(r.depthUsed, r.reason);
            Assert.AreEqual(0f, r.medianResidualMeters, 0.02f);
            Assert.Greater(r.inlierFraction, 0.9f);
        }

        [Test]
        public void BiasedDepth_ReportsSignedResidual()
        {
            // Sensor says the surface is 30 cm further than the plane (e.g. plane on a glass door).
            var wall = Fx.WallAtZ(2f);
            var f = Fx.DepthForPlane(CamPose, wall.position, wall.normal, 0, biasMeters: 0.30f);
            var r = new DepthValidator().Validate(wall, f, _cfg, 0);
            Assert.IsTrue(r.depthUsed);
            // Residual is measured along the plane normal, which points toward the camera (-Z),
            // so "further from camera" is negative.
            Assert.AreEqual(-0.30f, r.medianResidualMeters, 0.02f);
            Assert.AreEqual(0f, r.inlierFraction, 1e-3f);
        }

        [Test]
        public void StaleDepth_IsNotUsed()
        {
            var wall = Fx.WallAtZ(2f);
            var f = Fx.DepthForPlane(CamPose, wall.position, wall.normal, timestamp: 0.0);
            var r = new DepthValidator().Validate(wall, f, _cfg, now: _cfg.depthMaxAgeSeconds + 0.01);
            Assert.IsFalse(r.depthUsed);
            StringAssert.Contains("stale", r.reason);
        }

        [Test]
        public void LowConfidence_SamplesAreRejected()
        {
            var wall = Fx.WallAtZ(2f);
            var f = Fx.DepthForPlane(CamPose, wall.position, wall.normal, 0, confidence: 10);
            var r = new DepthValidator().Validate(wall, f, _cfg, 0);
            Assert.IsFalse(r.depthUsed);
            Assert.AreEqual(0, r.validSamples);
            Assert.Greater(r.rejectedSamples, 0);
        }

        [Test]
        public void InvalidSamples_AreRejected_NotTreatedAsZeroDistance()
        {
            var wall = Fx.WallAtZ(2f);
            var f = Fx.DepthForPlane(CamPose, wall.position, wall.normal, 0);
            System.Array.Clear(f.depthMillimeters, 0, f.depthMillimeters.Length); // sensor returned nothing
            var r = new DepthValidator().Validate(wall, f, _cfg, 0);
            Assert.IsFalse(r.depthUsed);
            Assert.AreEqual(0, r.validSamples);
        }

        [Test]
        public void NullDepth_IsNotUsed()
        {
            var r = new DepthValidator().Validate(Fx.WallAtZ(2f), null, _cfg, 0);
            Assert.IsFalse(r.depthUsed);
            Assert.IsTrue(float.IsNaN(r.medianResidualMeters));
        }

        [Test]
        public void ScaledIntrinsics_PreserveFieldOfView()
        {
            var full = new DepthIntrinsics { fx = 1000, fy = 1000, cx = 640, cy = 360, width = 1280, height = 720 };
            var small = full.ScaledTo(160, 90);
            Assert.AreEqual(125f, small.fx, 1e-4f);
            Assert.AreEqual(125f, small.fy, 1e-4f);
            Assert.AreEqual(80f, small.cx, 1e-4f);
            Assert.AreEqual(45f, small.cy, 1e-4f);
        }
    }
}
