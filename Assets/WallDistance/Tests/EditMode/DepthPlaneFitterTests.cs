using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    /// <summary>
    /// Depth-only fallback: fit a plane to the raw depth patch under the crosshair when ARCore
    /// has produced no plane polygon (plain painted walls, no parallax). Same synthetic depth
    /// generator as the validator tests, so the geometry is exact and failures are real.
    /// </summary>
    public class DepthPlaneFitterTests
    {
        Camera _cam;
        MeasurementConfig _cfg;

        [SetUp]
        public void SetUp()
        {
            _cam = Fx.MakeCamera(Vector3.zero, Quaternion.identity, fov: 60f);
            _cfg = new MeasurementConfig();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_cam.gameObject);

        Pose CamPose => new Pose(_cam.transform.position, _cam.transform.rotation);

        DepthPlaneFit FitCentre(DepthFrame f) =>
            new DepthPlaneFitter().Fit(f, f.width / 2, f.height / 2, _cfg);

        [TestCase(0.5f)]
        [TestCase(1.0f)]
        [TestCase(2.0f)]
        [TestCase(3.0f)]
        public void FacingWall_RecoversPerpendicularDistance(float dist)
        {
            var f = Fx.DepthForPlane(CamPose, new Vector3(0, 0, dist), Vector3.back, 0);
            var r = FitCentre(f);
            Assert.IsTrue(r.success, r.reason);
            Assert.AreEqual(dist, r.perpendicularDistanceMeters, 0.01f);
            Assert.AreEqual(1f, Vector3.Dot(r.worldNormal, Vector3.back), 1e-3f);
        }

        [TestCase(30f)]
        [TestCase(60f)]
        public void ObliqueWall_ReportsPerpendicularNotRayLength(float yawDeg)
        {
            // Wall at z=2 facing the camera; camera turned yawDeg. The crosshair ray hits the
            // wall at 2/cos(yaw) but the perpendicular distance stays 2.
            _cam.transform.rotation = Quaternion.Euler(0, yawDeg, 0);
            var f = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 2f), Vector3.back, 0);
            var r = FitCentre(f);
            Assert.IsTrue(r.success, r.reason);
            Assert.AreEqual(2f, r.perpendicularDistanceMeters, 0.02f);
            Assert.Greater(Vector3.Dot(r.worldNormal, Vector3.back), 0.99f);
        }

        [Test]
        public void Floor_IsRejected_NotReportedAsWall()
        {
            // Camera at 1.2 m looking 45 deg down at the floor: depth is perfectly planar but
            // the surface is horizontal, so it must not be reported as a wall.
            _cam.transform.position = new Vector3(0, 1.2f, 0);
            _cam.transform.rotation = Quaternion.Euler(45f, 0, 0);
            var f = Fx.DepthForPlane(CamPose, Vector3.zero, Vector3.up, 0);
            var r = FitCentre(f);
            Assert.IsFalse(r.success);
            StringAssert.Contains("tilt", r.reason);
        }

        [Test]
        public void NoDepthUnderCrosshair_Fails_WithoutZero()
        {
            var f = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 2f), Vector3.back, 0);
            System.Array.Clear(f.depthMillimeters, 0, f.depthMillimeters.Length);
            var r = FitCentre(f);
            Assert.IsFalse(r.success);
            Assert.IsTrue(float.IsNaN(r.perpendicularDistanceMeters));
        }

        [Test]
        public void LowConfidence_IsRejected()
        {
            var f = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 2f), Vector3.back, 0, confidence: 10);
            Assert.IsFalse(FitCentre(f).success);
        }

        [Test]
        public void NoisyNonPlanarPatch_IsRejected()
        {
            // Alternate pixels between two surfaces 40 cm apart: a checkerboard is not a wall.
            var near = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 1.6f), Vector3.back, 0);
            var far = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 2.0f), Vector3.back, 0);
            for (int i = 0; i < near.depthMillimeters.Length; i++)
                if ((i + i / near.width) % 2 == 0) near.depthMillimeters[i] = far.depthMillimeters[i];
            var r = FitCentre(near);
            Assert.IsFalse(r.success, "checkerboard depth must not fit as a single wall");
        }

        [Test]
        public void SmallOutlierCluster_IsRejectedByRefit()
        {
            // A wall at 2 m with a 3x3 blob of pixels (a light switch) 5 cm proud of it near the
            // crosshair. The refit should drop the blob and keep the wall distance.
            var f = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 2f), Vector3.back, 0);
            int cu = f.width / 2 + 3, cv = f.height / 2 + 3;
            for (int dv = -1; dv <= 1; dv++)
                for (int du = -1; du <= 1; du++)
                    f.depthMillimeters[(cv + dv) * f.width + (cu + du)] = 1950;
            var r = FitCentre(f);
            Assert.IsTrue(r.success, r.reason);
            Assert.AreEqual(2f, r.perpendicularDistanceMeters, 0.01f);
        }
    }

    public class EngineDepthFallbackTests
    {
        [Test]
        public void ReacquiredProviderImage_DoesNotRenewAgeOrPose()
        {
            var frame = new DepthFrame();
            Assert.IsTrue(frame.TryStamp(1000, 5, new Pose(Vector3.zero, Quaternion.identity)));
            Assert.IsFalse(frame.TryStamp(1000, 6, new Pose(Vector3.right, Quaternion.identity)));
            Assert.AreEqual(5, frame.timestamp);
            Assert.AreEqual(Vector3.zero, frame.cameraPose.position);
            Assert.IsFalse(frame.TryStamp(999, 6, new Pose()));
            Assert.IsTrue(frame.TryStamp(1001, 7, new Pose(Vector3.right, Quaternion.identity)));
            Assert.AreEqual(7, frame.timestamp);
        }

        [Test]
        public void DenseEightMetresWithoutRawSupport_MustNotBecomeWallReading()
        {
            // Reproduces the sweep: dense centre ~7981 mm, raw patch missing.
            var raw = Fx.DepthForPlane(CamPose, Vector3.forward, Vector3.back, 5);
            System.Array.Clear(raw.depthMillimeters, 0, raw.depthMillimeters.Length);
            var input = Fx.Input(_cam, new System.Collections.Generic.List<WallCandidate>(), 5, raw);
            input.denseDepth = Fx.DepthForPlane(CamPose, Vector3.forward * 7.981f, Vector3.back, 5);
            Assert.IsFalse(_engine.Update(input).aimed.isValid,
                "A planar dense estimate is not evidence of a measured wall.");
        }

        [Test]
        public void ReliableRawOneMetre_WinsOverWrongDenseEightMetres()
        {
            var raw = Fx.DepthForPlane(CamPose, Vector3.forward, Vector3.back, 5);
            var input = Fx.Input(_cam, new System.Collections.Generic.List<WallCandidate>(), 5, raw);
            input.denseDepth = Fx.DepthForPlane(CamPose, Vector3.forward * 7.981f, Vector3.back, 5);
            var reading = _engine.Update(input).aimed;
            Assert.IsTrue(reading.isValid);
            Assert.AreEqual(1f, reading.rawDistanceMeters, 0.02f);
        }

        [Test]
        public void RawWithoutConfidence_MustNotProduceDepthOnlyMeasurement()
        {
            var raw = Fx.DepthForPlane(CamPose, Vector3.forward, Vector3.back, 5);
            raw.confidence = null;
            var input = Fx.Input(_cam, new System.Collections.Generic.List<WallCandidate>(), 5, raw);
            Assert.IsFalse(_engine.Update(input).aimed.isValid);
        }

        Camera _cam;
        MeasurementConfig _cfg;
        WallMeasurementEngine _engine;

        [SetUp]
        public void SetUp()
        {
            _cam = Fx.MakeCamera(Vector3.zero, Quaternion.identity, fov: 60f);
            _cfg = new MeasurementConfig();
            _engine = new WallMeasurementEngine(_cfg);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_cam.gameObject);

        Pose CamPose => new Pose(_cam.transform.position, _cam.transform.rotation);

        [Test]
        public void NoPlanes_WithDepth_GivesDepthOnlyAimedReading()
        {
            var depth = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 1.5f), Vector3.back, timestamp: 5.0);
            var input = Fx.Input(_cam, new System.Collections.Generic.List<WallCandidate>(), now: 5.0, depth: depth);
            var snap = _engine.Update(input);

            Assert.IsTrue(snap.aimed.isValid, snap.aimed.qualityReason);
            Assert.AreEqual(MeasurementSource.DepthOnly, snap.aimed.source);
            Assert.AreEqual(QualityLabel.DepthEstimate, snap.aimed.quality);
            Assert.AreEqual(1.5f, snap.aimed.rawDistanceMeters, 0.01f);
            Assert.AreEqual(1.5f, snap.aimed.distanceMeters, 0.01f);
            // Nearest has no depth fallback - it stays honest about needing a plane.
            Assert.IsFalse(snap.nearest.isValid);
            Assert.AreEqual(FailureReason.NoWallInView, snap.nearest.failure);
        }

        [Test]
        public void NoPlanes_StaleDepth_StaysInvalid()
        {
            var depth = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 1.5f), Vector3.back, timestamp: 0.0);
            var input = Fx.Input(_cam, new System.Collections.Generic.List<WallCandidate>(), now: 1.0, depth: depth);
            var snap = _engine.Update(input);
            Assert.IsFalse(snap.aimed.isValid);
            Assert.AreEqual(FailureReason.NoWallUnderCrosshair, snap.aimed.failure);
        }

        [Test]
        public void PlaneUnderCrosshair_TakesPrecedenceOverDepthOnly()
        {
            var wall = Fx.WallAtZ(2f);
            var depth = Fx.DepthForPlane(CamPose, wall.position, wall.normal, timestamp: 0);
            var input = Fx.Input(_cam, new System.Collections.Generic.List<WallCandidate> { wall }, now: 0, depth: depth);
            var snap = _engine.Update(input);
            Assert.AreEqual(MeasurementSource.PlaneDepthValidated, snap.aimed.source);
        }

        [Test]
        public void DepthOnly_OutOfRange_IsLabelled()
        {
            var depth = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 4f), Vector3.back, timestamp: 0);
            var input = Fx.Input(_cam, new System.Collections.Generic.List<WallCandidate>(), now: 0, depth: depth);
            var snap = _engine.Update(input);
            Assert.IsTrue(snap.aimed.isValid);
            Assert.AreEqual(QualityLabel.OutOfTestedRange, snap.aimed.quality);
            Assert.AreEqual(MeasurementSource.DepthOnly, snap.aimed.source);
        }

        [Test]
        public void SwitchingFromDepthOnlyToPlane_ResetsFilter()
        {
            // Depth-only says 1.5 m; then a plane appears at 2.0 m. The filtered value must jump
            // to the plane, not blend from 1.5 - the candidate identity changed.
            var d1 = Fx.DepthForPlane(CamPose, new Vector3(0, 0, 1.5f), Vector3.back, timestamp: 0);
            _engine.Update(Fx.Input(_cam, new System.Collections.Generic.List<WallCandidate>(), now: 0, depth: d1));
            var wall = Fx.WallAtZ(2f);
            var d2 = Fx.DepthForPlane(CamPose, wall.position, wall.normal, timestamp: 0.03);
            var snap = _engine.Update(Fx.Input(_cam, new System.Collections.Generic.List<WallCandidate> { wall }, now: 0.03, depth: d2));
            Assert.AreEqual(2f, snap.aimed.distanceMeters, 0.01f);
        }
    }
}
