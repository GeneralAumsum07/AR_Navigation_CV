using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class CorridorSideSelectorTests
    {
        /// <summary>Corridor along +z: left wall x = leftX facing +x, right wall x = rightX facing -x, both observed at t.</summary>
        static WallMap Corridor(double t = 0, float leftX = -1f, float rightX = 1f, float z0 = -5f, float z1 = 10f)
        {
            var m = new WallMap(new MeasurementConfig());
            m.SetSession("s1");
            m.Observe(Fx.Obs(leftX, t, z0, z1, angleDeg: 180f), t);
            m.Observe(Fx.Obs(rightX, t, z0, z1), t);
            return m;
        }

        static Pose Cam(float x = 0.2f, float z = 0f, float yaw = 0f, float pitch = 10f) =>
            new Pose(new Vector3(x, 1.4f, z), Quaternion.Euler(pitch, yaw, 0f));

        /// <summary>Same projection*view a Unity camera builds (view space looks down -Z).</summary>
        static Matrix4x4 Clip(Pose p) =>
            Matrix4x4.Perspective(60f, 9f / 16f, 0.05f, 50f) * Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
            * Matrix4x4.TRS(p.position, p.rotation, Vector3.one).inverse;

        static WallReading L, R;
        static float W;

        static void Measure(CorridorSideSelector s, IReadOnlyList<WallTrack> walls, Pose cam, double now = 0.1, MeasurementConfig cfg = null) =>
            s.Measure("s1", now, cam, Clip(cam), Vector3.up, walls, cfg ?? new MeasurementConfig(), out L, out R, out W);

        [Test]
        public void HeadingWithin20Degrees_GivesLeftRightAndWidth([Values(-20f, 0f, 20f)] float yaw)
        {
            Measure(new CorridorSideSelector(), Corridor().Tracks, Cam(yaw: yaw));
            Assert.IsTrue(L.isValid && R.isValid);
            Assert.AreEqual(MeasurementKind.CorridorLeft, L.kind);
            Assert.AreEqual(MeasurementKind.CorridorRight, R.kind);
            // Perpendicular, so independent of where the phone points.
            Assert.AreEqual(1.2f, L.distanceMeters, 1e-4f);
            Assert.AreEqual(0.8f, R.distanceMeters, 1e-4f);
            Assert.AreEqual(2.0f, W, 1e-4f);
            Assert.AreEqual(1f, R.surfaceNormal.x * -1f, 1e-4f, "right wall's normal faces the camera (-x)");
        }

        [Test]
        public void TurnedAround_SidesSwap()
        {
            var map = Corridor();
            Measure(new CorridorSideSelector(), map.Tracks, Cam(yaw: 180f));
            Assert.AreEqual(0.8f, L.distanceMeters, 1e-4f, "the x = +1 wall is now on the left");
            Assert.AreEqual(1.2f, R.distanceMeters, 1e-4f);
        }

        [Test]
        public void Hysteresis_EnterAt30_LeaveAbove40()
        {
            var map = Corridor();
            Measure(new CorridorSideSelector(), map.Tracks, Cam(yaw: 35f));
            Assert.AreEqual(FailureReason.NoWallOnSide, R.failure, "35° is too far off to become a side wall");

            var s = new CorridorSideSelector();
            Measure(s, map.Tracks, Cam(yaw: 0f));
            string id = R.candidateId;
            Measure(s, map.Tracks, Cam(yaw: 35f));
            Assert.IsTrue(R.isValid, "an existing side wall is kept up to 40°");
            Assert.AreEqual(id, R.candidateId);
            Measure(s, map.Tracks, Cam(yaw: 45f));
            Assert.IsFalse(R.isValid);
            Assert.AreEqual(FailureReason.NoWallOnSide, R.failure);
        }

        [Test]
        public void WalkingSway_KeepsTheSameWalls()
        {
            var map = Corridor();
            var s = new CorridorSideSelector();
            Measure(s, map.Tracks, Cam());
            string l = L.candidateId, r = R.candidateId;
            foreach (float yaw in new[] { 15f, -15f, 12f, -15f, 15f })
            {
                Measure(s, map.Tracks, Cam(yaw: yaw));
                Assert.AreEqual(l, L.candidateId);
                Assert.AreEqual(r, R.candidateId);
            }
        }

        [Test]
        public void ExtentMargin_IsOneMetre()
        {
            var map = Corridor(z0: 3f, z1: 10f);
            Measure(new CorridorSideSelector(), map.Tracks, Cam(z: 1.9f));
            Assert.AreEqual(FailureReason.NoWallOnSide, R.failure, "1.1 m before the wall starts");
            Measure(new CorridorSideSelector(), map.Tracks, Cam(z: 2.1f));
            Assert.IsTrue(R.isValid, "0.9 m before the wall starts");
        }

        [Test]
        public void OldOrDistantWalls_AreIgnored()
        {
            var map = Corridor();
            Measure(new CorridorSideSelector(), map.Tracks, Cam(), now: 10.5);
            Assert.AreEqual(FailureReason.NoWallOnSide, R.failure, "last seen 10.5 s ago");
            Measure(new CorridorSideSelector(), map.Tracks, Cam(), now: 9.5);
            Assert.IsTrue(R.isValid);

            var wide = Corridor(rightX: 7.2f);
            Measure(new CorridorSideSelector(), wide.Tracks, Cam());
            Assert.IsTrue(L.isValid);
            Assert.AreEqual(FailureReason.NoWallOnSide, R.failure, "7 m away");
            Assert.IsTrue(float.IsNaN(W));
        }

        [Test]
        public void DoorRecess_NearestWallOnTheSideWins()
        {
            var map = Corridor();
            var main = map.Tracks[0];
            map.Observe(Fx.Obs(-1.3f, 0, 2f, 3f, angleDeg: 180f), 0);
            Assert.AreEqual(3, map.Tracks.Count, "the recess is its own wall");
            Measure(new CorridorSideSelector(), map.Tracks, Cam(z: 2.5f));
            Assert.AreEqual(main.id, L.candidateId);
            Assert.AreEqual(1.2f, L.distanceMeters, 1e-4f);
        }

        [Test]
        public void NonParallelWalls_HaveNoWidth()
        {
            var m = new WallMap(new MeasurementConfig());
            m.SetSession("s1");
            m.Observe(Fx.Obs(-1f, 0, -7.5f, 7.5f, angleDeg: 180f), 0);
            m.Observe(Fx.Obs(1f, 0, -7.5f, 7.5f, angleDeg: 15f), 0);
            Measure(new CorridorSideSelector(), m.Tracks, Cam());
            Assert.IsTrue(L.isValid && R.isValid, "15° is within the 30° entry angle");
            Assert.IsTrue(float.IsNaN(W), "but not parallel within 10°");
        }

        [Test]
        public void PhonePointingAtFloor_HasNoHeading()
        {
            var map = Corridor();
            Measure(new CorridorSideSelector(), map.Tracks, Cam(pitch: 75f));
            Assert.AreEqual(FailureReason.NoHeading, L.failure);
            Assert.AreEqual(FailureReason.NoHeading, R.failure);
            Assert.IsTrue(float.IsNaN(W));
            Measure(new CorridorSideSelector(), map.Tracks, Cam(pitch: 65f));
            Assert.IsTrue(L.isValid && R.isValid);
        }

        [Test]
        public void SwitchingToAnotherWall_DoesNotBlendDistances()
        {
            var cfg = new MeasurementConfig { filterTimeConstantSeconds = 1f };
            var map = Corridor();
            var s = new CorridorSideSelector();
            for (int i = 0; i < 5; i++) Measure(s, map.Tracks, Cam(x: 0f), 0.1 + 0.033 * i, cfg);
            Assert.AreEqual(1.0f, R.distanceMeters, 1e-4f);
            // The old right wall ages out (seen at 0, now 11 s); a new one 1.5 m away is seen at 11.
            var b = map.Observe(Fx.Obs(1.5f, 11.0, -5f, 10f), 11.0);
            Measure(s, map.Tracks, Cam(x: 0f), 11.0, cfg);
            Assert.AreEqual(b.id, R.candidateId);
            Assert.AreEqual(1.5f, R.distanceMeters, 1e-5f, "a new wall starts from its own raw distance");
        }

        [Test]
        public void OutOfViewWall_ReasonSaysWhenItWasLastSeen()
        {
            Measure(new CorridorSideSelector(), Corridor().Tracks, Cam(), now: 1.5);
            Assert.IsTrue(R.isValid, "out of view is still valid");
            StringAssert.Contains("last seen 1.5 s ago", R.qualityReason);
        }

        [Test]
        public void CloseSideWall_IsOutOfTestedRange()
        {
            Measure(new CorridorSideSelector(), Corridor().Tracks, Cam(x: 0.7f));
            Assert.IsTrue(R.isValid);
            Assert.AreEqual(QualityLabel.OutOfTestedRange, R.quality);
            StringAssert.StartsWith("0.30 m is outside", R.qualityReason);
        }

        [Test]
        public void Reading_InheritsSourceQualityAndChain()
        {
            Measure(new CorridorSideSelector(), Corridor().Tracks, Cam());
            Assert.AreEqual(MeasurementSource.LearnedDepth, R.source);
            Assert.AreEqual(QualityLabel.LearnedEstimate, R.quality);
            Assert.AreEqual("LearnedDepth", R.sourceChain);
        }

        [Test]
        public void QualityFor_MapsEverySource()
        {
            Assert.AreEqual(QualityLabel.CrossChecked, CorridorSideSelector.QualityFor(MeasurementSource.LearnedDepth, true));
            Assert.AreEqual(QualityLabel.EdgeConfirmed, CorridorSideSelector.QualityFor(MeasurementSource.FloorEdge, false));
            Assert.AreEqual(QualityLabel.LearnedEstimate, CorridorSideSelector.QualityFor(MeasurementSource.LearnedDepth, false));
            Assert.AreEqual(QualityLabel.PlaneEstimate, CorridorSideSelector.QualityFor(MeasurementSource.PlaneOnly, false));
            Assert.AreEqual(QualityLabel.AssistedEstimate, CorridorSideSelector.QualityFor(MeasurementSource.AssistedFloor, false));
            Assert.AreEqual(QualityLabel.DepthEstimate, CorridorSideSelector.QualityFor(MeasurementSource.DepthOnly, false));
            Assert.AreEqual(QualityLabel.DepthValidated, CorridorSideSelector.QualityFor(MeasurementSource.PlaneDepthValidated, false));
            Assert.AreEqual(QualityLabel.Unavailable, CorridorSideSelector.QualityFor(MeasurementSource.None, false));
        }
    }
}
