using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class WallMeasurementEngineTests
    {
        Camera _cam;
        MeasurementConfig _cfg;
        WallMeasurementEngine _engine;

        [SetUp]
        public void SetUp()
        {
            _cam = Fx.MakeCamera(Vector3.zero, Quaternion.identity);
            _cfg = new MeasurementConfig { filterTimeConstantSeconds = 0f }; // no smoothing unless a test opts in
            _engine = new WallMeasurementEngine(_cfg);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_cam.gameObject);

        Pose CamPose => new Pose(_cam.transform.position, _cam.transform.rotation);

        [Test]
        public void Aimed_ReportsPerpendicular_AtKnownDistances()
        {
            foreach (float d in new[] { 0.5f, 1f, 1.5f, 2f, 3f })
            {
                var snap = _engine.Update(Fx.Input(_cam, new[] { Fx.WallAtZ(d) }, 0));
                Assert.IsTrue(snap.aimed.isValid);
                Assert.AreEqual(d, snap.aimed.distanceMeters, 1e-4f);
                Assert.AreEqual(MeasurementKind.Aimed, snap.aimed.kind);
                Assert.AreEqual(QualityLabel.PlaneEstimate, snap.aimed.quality, "no depth -> plane estimate");
                Assert.AreEqual(MeasurementSource.PlaneOnly, snap.aimed.source);
            }
        }

        [TestCase(30f)]
        [TestCase(60f)]
        public void Aimed_IsPerpendicular_NotRayLength_AtAngle(float yaw)
        {
            _cam.transform.rotation = Quaternion.Euler(0, yaw, 0);
            var wall = Fx.WallAtZ(2f, halfWidth: 20f, halfHeight: 5f);
            var snap = _engine.Update(Fx.Input(_cam, new[] { wall }, 0));
            Assert.IsTrue(snap.aimed.isValid);
            Assert.AreEqual(2f, snap.aimed.distanceMeters, 1e-4f);
            Assert.AreEqual(0f, snap.aimed.surfacePoint.x, 1e-4f, "foot of perpendicular is straight ahead of the camera position");
        }

        [Test]
        public void Aimed_PicksNearestPolygonAlongRay()
        {
            var far = Fx.WallAtZ(3f, id: "far");
            var near = Fx.WallAtZ(1f, id: "near");
            var snap = _engine.Update(Fx.Input(_cam, new[] { far, near }, 0));
            Assert.AreEqual("near", snap.aimed.candidateId);
            Assert.AreEqual(1f, snap.aimed.distanceMeters, 1e-4f);
        }

        [Test]
        public void Aimed_InvalidWithNaN_WhenNothingUnderCrosshair()
        {
            // Wall exists but is off to the side.
            var snap = _engine.Update(Fx.Input(_cam, new[] { Fx.WallAtZ(2f, halfWidth: 0.3f, xCenter: 3f) }, 0));
            Assert.IsFalse(snap.aimed.isValid);
            Assert.AreEqual(FailureReason.NoWallUnderCrosshair, snap.aimed.failure);
            Assert.IsTrue(float.IsNaN(snap.aimed.distanceMeters), "missing data must never read as 0 m");
        }

        [Test]
        public void Nearest_ChoosesBoundedEdge_NotInfinitePlane_AtDoorway()
        {
            // Two wall segments either side of a doorway, both at z=1; the gap is x in [-0.3,0.3]
            // (the jambs sit just inside the ~18 deg horizontal half-FOV of a portrait camera).
            // Infinite-plane logic would say 1.0 m; bounded logic says sqrt(1 + 0.3^2).
            var left = Fx.WallAtZ(1f, halfWidth: 1f, xCenter: -1.3f, id: "L");
            var right = Fx.WallAtZ(1f, halfWidth: 1f, xCenter: 1.3f, id: "R");
            var snap = _engine.Update(Fx.Input(_cam, new[] { left, right }, 0));
            Assert.IsTrue(snap.nearest.isValid, snap.nearest.failure.ToString());
            Assert.AreEqual(Mathf.Sqrt(1.09f), snap.nearest.distanceMeters, 1e-3f);
            Assert.IsFalse(snap.aimed.isValid, "crosshair looks through the doorway gap");
        }

        [Test]
        public void Nearest_IgnoresWallsOutsideView()
        {
            var behind = Fx.WallAtZ(-0.3f, id: "behind"); // closest, but behind the camera
            var ahead = Fx.WallAtZ(2f, id: "ahead");
            var snap = _engine.Update(Fx.Input(_cam, new[] { behind, ahead }, 0));
            Assert.AreEqual("ahead", snap.nearest.candidateId);
            Assert.AreEqual(2f, snap.nearest.distanceMeters, 1e-4f);
        }

        [Test]
        public void Nearest_AtCorner_PicksCloserOfTwoWalls()
        {
            // Wall ahead at z=2 and a side wall at x=+0.8 (facing -X), both in view of a camera
            // yawed 20 deg right.
            _cam.transform.rotation = Quaternion.Euler(0, 20f, 0);
            var ahead = Fx.WallAtZ(2f, halfWidth: 5f, id: "ahead");
            var side = new WallCandidate();
            side.Set("side", true, new Vector3(0.8f, 0f, 2f), Quaternion.LookRotation(Vector3.up, Vector3.left) /* normal -X */,
                new[] { new Vector2(-3, -2), new Vector2(3, -2), new Vector2(3, 2), new Vector2(-3, 2) });
            var snap = _engine.Update(Fx.Input(_cam, new[] { ahead, side }, 0));
            Assert.AreEqual("side", snap.nearest.candidateId);
            Assert.AreEqual(0.8f, snap.nearest.distanceMeters, 1e-3f);
        }

        [Test]
        public void UntrackedAndTinyCandidates_AreIgnored()
        {
            var lost = Fx.WallAtZ(1f, id: "lost");
            lost.isTracked = false;
            var tiny = Fx.WallAtZ(1.2f, halfWidth: 0.1f, halfHeight: 0.1f, id: "tiny"); // 0.04 m^2 < 0.2
            var good = Fx.WallAtZ(2f, id: "good");
            var snap = _engine.Update(Fx.Input(_cam, new[] { lost, tiny, good }, 0));
            Assert.AreEqual("good", snap.aimed.candidateId);
            Assert.AreEqual("good", snap.nearest.candidateId);
        }

        [Test]
        public void TrackingLoss_InvalidatesImmediately_AndResetsFilter()
        {
            _cfg.filterTimeConstantSeconds = 1f;
            var wall = Fx.WallAtZ(2f);
            _engine.Update(Fx.Input(_cam, new[] { wall }, 0));
            var lost = _engine.Update(Fx.Input(_cam, new[] { wall }, 0.1, tracking: false));
            Assert.IsFalse(lost.aimed.isValid);
            Assert.IsFalse(lost.nearest.isValid);
            Assert.AreEqual(FailureReason.TrackingLost, lost.aimed.failure);
            Assert.IsTrue(float.IsNaN(lost.aimed.distanceMeters));

            // On recovery the wall has moved (plane re-estimated); the filter must not blend
            // the old 2 m with the new 3 m.
            var moved = Fx.WallAtZ(3f);
            var back = _engine.Update(Fx.Input(_cam, new[] { moved }, 0.2));
            Assert.AreEqual(3f, back.aimed.distanceMeters, 1e-4f);
        }

        [Test]
        public void Filter_ResetsOnTargetSwitch_AndOnSessionReset()
        {
            _cfg.filterTimeConstantSeconds = 1f;
            var a = Fx.WallAtZ(2f, id: "A");
            _engine.Update(Fx.Input(_cam, new[] { a }, 0));
            _engine.Update(Fx.Input(_cam, new[] { a }, 0.05));

            // Plane replaced by a merged one with a new id at a different distance: raw value wins.
            var b = Fx.WallAtZ(2.5f, id: "B");
            var s1 = _engine.Update(Fx.Input(_cam, new[] { b }, 0.1));
            Assert.AreEqual(2.5f, s1.aimed.distanceMeters, 1e-4f);

            // Same id but a session reset: also a clean slate.
            var b2 = Fx.WallAtZ(1.0f, id: "B");
            var s2 = _engine.Update(Fx.Input(_cam, new[] { b2 }, 0.15, session: "s2"));
            Assert.AreEqual(1.0f, s2.aimed.distanceMeters, 1e-4f);
            Assert.AreEqual("s2", s2.aimed.sessionId);
        }

        [Test]
        public void Filter_SmoothsTowardRaw_WithTimeConstant()
        {
            _cfg.filterTimeConstantSeconds = 0.25f;
            var w2 = Fx.WallAtZ(2f);
            _engine.Update(Fx.Input(_cam, new[] { w2 }, 0));
            // Same candidate id, plane jumps to 3 m. After one time constant the filter should
            // have covered ~63% of the step.
            var w3 = Fx.WallAtZ(3f);
            var s = _engine.Update(Fx.Input(_cam, new[] { w3 }, 0.25));
            Assert.AreEqual(3f, s.aimed.rawDistanceMeters, 1e-4f, "raw is unfiltered");
            Assert.AreEqual(2f + (1f - Mathf.Exp(-1f)), s.aimed.distanceMeters, 1e-3f);
        }

        [Test]
        public void DepthAgreement_LabelsDepthValidated()
        {
            var wall = Fx.WallAtZ(2f);
            var depth = Fx.DepthForPlane(CamPose, wall.position, wall.normal, timestamp: 0);
            var s = _engine.Update(Fx.Input(_cam, new[] { wall }, 0.1, depth));
            Assert.AreEqual(QualityLabel.DepthValidated, s.aimed.quality, s.aimed.qualityReason);
            Assert.AreEqual(MeasurementSource.PlaneDepthValidated, s.aimed.source);
            Assert.AreEqual(2f, s.aimed.distanceMeters, 1e-4f, "depth validates, it does not replace the plane distance");
        }

        [Test]
        public void DepthDisagreement_LabelsUnreliable()
        {
            var wall = Fx.WallAtZ(2f);
            var depth = Fx.DepthForPlane(CamPose, wall.position, wall.normal, 0, biasMeters: 0.4f);
            var s = _engine.Update(Fx.Input(_cam, new[] { wall }, 0, depth));
            Assert.IsTrue(s.aimed.isValid, "still a reading, but flagged");
            Assert.AreEqual(QualityLabel.Unreliable, s.aimed.quality);
            StringAssert.Contains("disagrees", s.aimed.qualityReason);
        }

        [Test]
        public void StaleDepth_FallsBackToPlaneEstimate()
        {
            var wall = Fx.WallAtZ(2f);
            var depth = Fx.DepthForPlane(CamPose, wall.position, wall.normal, timestamp: 0);
            var s = _engine.Update(Fx.Input(_cam, new[] { wall }, 1.0, depth));
            Assert.AreEqual(QualityLabel.PlaneEstimate, s.aimed.quality);
        }

        [Test]
        public void OutOfRange_IsFlagged()
        {
            var s = _engine.Update(Fx.Input(_cam, new[] { Fx.WallAtZ(4f) }, 0));
            Assert.IsTrue(s.aimed.isValid);
            Assert.AreEqual(QualityLabel.OutOfTestedRange, s.aimed.quality);
            var s2 = _engine.Update(Fx.Input(_cam, new[] { Fx.WallAtZ(0.3f) }, 0));
            Assert.AreEqual(QualityLabel.OutOfTestedRange, s2.aimed.quality);
        }

        [Test]
        public void Reading_CarriesPoseAndNormalTowardCamera()
        {
            _cam.transform.position = new Vector3(0.4f, 1.2f, -0.5f);
            var s = _engine.Update(Fx.Input(_cam, new[] { Fx.WallAtZ(2f) }, 7.5));
            Assert.AreEqual(_cam.transform.position, s.aimed.cameraPose.position);
            Assert.AreEqual(7.5, s.aimed.timestamp);
            Assert.AreEqual(Vector3.back.ToString(), s.aimed.surfaceNormal.ToString());
            Assert.AreEqual(new Vector3(0.4f, 1.2f, 2f).ToString("F4"), s.aimed.surfacePoint.ToString("F4"));
        }
    }
}
