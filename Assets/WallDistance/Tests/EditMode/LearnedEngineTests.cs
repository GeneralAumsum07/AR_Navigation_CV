using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class LearnedEngineTests
    {
        Camera _cam;

        [SetUp]
        public void SetUp() => _cam = Fx.MakeCamera(new Vector3(0f, 1.4f, 0f), Quaternion.identity);

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_cam.gameObject);

        EngineInput In(IReadOnlyList<WallCandidate> candidates, double now, IReadOnlyList<WallTrack> map = null,
            DepthFrame depth = null, FailureReason detection = FailureReason.None, bool tracking = true)
        {
            var i = Fx.Input(_cam, candidates, now, depth, tracking: tracking);
            i.mapWalls = map;
            i.up = Vector3.up;
            i.detectionFailure = detection;
            return i;
        }

        static WallCandidate Learned(float z, MeasurementSource src = MeasurementSource.LearnedDepth, bool crossChecked = false)
        {
            var c = Fx.WallAtZ(z, yCenter: 1.4f);
            c.source = src;
            c.crossChecked = crossChecked;
            return c;
        }

        static WallMap Corridor(float leftX = -1f, float rightX = 1f)
        {
            var m = new WallMap(new MeasurementConfig());
            m.SetSession("s1");
            m.Observe(Fx.Obs(leftX, 0, -5f, 10f, angleDeg: 180f), 0);
            m.Observe(Fx.Obs(rightX, 0, -5f, 10f), 0);
            return m;
        }

        [Test]
        public void LearnedWall_IsLearnedEstimate_EdgeIsEdgeConfirmed_AgreementIsCrossChecked()
        {
            var e = new WallMeasurementEngine(new MeasurementConfig());
            var s = e.Update(In(new[] { Learned(2f) }, 1.0));
            Assert.AreEqual(QualityLabel.LearnedEstimate, s.aimed.quality);
            Assert.AreEqual(MeasurementSource.LearnedDepth, s.aimed.source);
            Assert.AreEqual("LearnedDepth", s.aimed.sourceChain);
            Assert.AreEqual(2f, s.aimed.distanceMeters, 1e-4f);

            s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f, MeasurementSource.FloorEdge) }, 1.0));
            Assert.AreEqual(QualityLabel.EdgeConfirmed, s.aimed.quality);

            s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f, crossChecked: true) }, 1.0));
            Assert.AreEqual(QualityLabel.CrossChecked, s.aimed.quality);
            Assert.AreEqual("LearnedDepth+ARPlane", s.aimed.sourceChain);
        }

        [Test]
        public void RawDepthAgreeing_CrossChecks_Disagreeing_OnlyAddsAReason()
        {
            var pose = new Pose(_cam.transform.position, _cam.transform.rotation);
            var agree = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, 1.0);
            var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f) }, 1.0, depth: agree));
            Assert.AreEqual(QualityLabel.CrossChecked, s.aimed.quality);
            Assert.AreEqual("LearnedDepth+RawDepth", s.aimed.sourceChain);

            // Plan deviation D10: raw depth 30 cm off does not demote a learned wall.
            var off = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, 1.0, biasMeters: 0.3f);
            s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f) }, 1.0, depth: off));
            Assert.AreEqual(QualityLabel.LearnedEstimate, s.aimed.quality);
            StringAssert.Contains("raw depth", s.aimed.qualityReason);
        }

        [Test]
        public void LearnedWallBeyond3m_IsOutOfTestedRange()
        {
            var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(4f) }, 1.0));
            Assert.IsTrue(s.aimed.isValid);
            Assert.AreEqual(QualityLabel.OutOfTestedRange, s.aimed.quality);
            Assert.AreEqual(MeasurementSource.LearnedDepth, s.aimed.source);
        }

        [Test]
        public void DetectionFailure_ReplacesGenericNoWallReasons()
        {
            var e = new WallMeasurementEngine(new MeasurementConfig());
            var s = e.Update(In(new WallCandidate[0], 1.0, new List<WallTrack>(), detection: FailureReason.NoFloor));
            Assert.AreEqual(FailureReason.NoFloor, s.aimed.failure);
            Assert.AreEqual(FailureReason.NoFloor, s.nearest.failure);
            Assert.AreEqual(FailureReason.NoFloor, s.left.failure);
            Assert.AreEqual(FailureReason.NoFloor, s.right.failure);
        }

        [Test]
        public void DetectionFailure_NeverInvalidatesAValidReading_OrStampsUnavailable()
        {
            var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f) }, 1.0, detection: FailureReason.NoFloor));
            Assert.IsTrue(s.aimed.isValid, "walls already in the map are still measured");

            s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new WallCandidate[0], 1.0, detection: FailureReason.InferenceUnavailable));
            Assert.AreEqual(FailureReason.NoWallUnderCrosshair, s.aimed.failure, "deviation D9");
        }

        [Test]
        public void MapWalls_GiveSidesAndWidth()
        {
            var map = Corridor(rightX: 1.2f);
            var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(map.Candidates(0.1), 0.1, map.Tracks));
            Assert.AreEqual(1.0f, s.left.distanceMeters, 1e-4f);
            Assert.AreEqual(1.2f, s.right.distanceMeters, 1e-4f);
            Assert.AreEqual(2.2f, s.corridorWidthMeters, 1e-4f);
        }

        [Test]
        public void TrackingLossThenRecovery_SidesResumeFromMapWithoutBlend()
        {
            // Review Focus 3. While tracking is lost, ARCore corrects the right wall's anchor by
            // 10 cm. On recovery the reading must be the corrected 1.1 m at once. A slow filter
            // (τ = 1 s) would show ~1.003 m if pre-loss history leaked through.
            var cfg = new MeasurementConfig { filterTimeConstantSeconds = 1f };
            var e = new WallMeasurementEngine(cfg);
            var map = Corridor();
            var right = map.Tracks[1];
            map.ApplyAnchorPose(right.id, new Pose(new Vector3(1f, 0f, 0f), Quaternion.identity));
            WallDistanceSnapshot s = default;
            for (int i = 0; i < 5; i++) s = e.Update(In(map.Candidates(0.1 + i * 0.033), 0.1 + i * 0.033, map.Tracks));
            Assert.AreEqual(1.0f, s.right.distanceMeters, 1e-4f);

            s = e.Update(In(map.Candidates(0.3), 0.3, map.Tracks, tracking: false));
            Assert.AreEqual(FailureReason.TrackingLost, s.left.failure);
            Assert.AreEqual(FailureReason.TrackingLost, s.right.failure);
            Assert.IsTrue(float.IsNaN(s.corridorWidthMeters));

            map.ApplyAnchorPose(right.id, new Pose(new Vector3(1.1f, 0f, 0f), Quaternion.identity));
            s = e.Update(In(map.Candidates(0.4), 0.4, map.Tracks));
            Assert.IsTrue(s.right.isValid, "the map was kept through the loss");
            Assert.AreEqual(1.1f, s.right.distanceMeters, 1e-4f);
            Assert.AreEqual(s.right.rawDistanceMeters, s.right.distanceMeters);
        }

        [Test]
        public void NoMap_SidesAreNoWallOnSide_WidthNaN()
        {
            var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new WallCandidate[0], 1.0));
            Assert.AreEqual(FailureReason.NoWallOnSide, s.left.failure);
            Assert.AreEqual(MeasurementKind.CorridorRight, s.right.kind);
            Assert.IsTrue(float.IsNaN(s.corridorWidthMeters));
        }

        [Test]
        public void ArPlaneReading_GetsItsSourceAsChain()
        {
            var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Fx.WallAtZ(2f, yCenter: 1.4f) }, 1.0));
            Assert.AreEqual(MeasurementSource.PlaneOnly, s.aimed.source);
            Assert.AreEqual("PlaneOnly", s.aimed.sourceChain);
        }
    }
}
