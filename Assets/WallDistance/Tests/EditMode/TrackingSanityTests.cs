using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    /// <summary>
    /// Regressions from the 2026-10-07 field logs (OnePlus 13R): a camera lifted 1.12 m above
    /// ARCore's planes, depth images replayed under fresh timestamps, an ARCore "floor" that
    /// jumped 0.67 m mid-walk, and camera poses that jumped up to 0.9 m in one frame.
    /// </summary>
    public class SceneOriginTests
    {
        [Test]
        public void XrOrigin_HasNoCameraYOffset()
        {
            // The offset lifts only the camera; ARCore's planes live under a sibling "Trackables"
            // object, so any non-zero value puts the camera that far above every floor and wall.
            // Read as text: EditMode tests should not load the AR scene just to check one field.
            string scene = File.ReadAllText(Path.Combine(Application.dataPath, "Scenes", "WallMeasurement.unity"));
            var offsets = Regex.Matches(scene, @"m_CameraYOffset:\s*(\S+)");
            Assert.AreEqual(1, offsets.Count, "expected exactly one XR Origin in the scene");
            Assert.AreEqual(0f, float.Parse(offsets[0].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                "camera must share ARCore's session frame with the planes");
        }
    }

    public class DepthContentFreshnessTests
    {
        static DepthFrame Frame(params ushort[] mm) => new DepthFrame { width = mm.Length, height = 1, depthMillimeters = mm };

        [Test]
        public void SameContentUnderNewerTimestamp_IsNotRestamped()
        {
            // ARCore served one raw depth image for ~3.4 s while advancing its timestamp.
            var frame = Frame(1000, 1200, 0, 1500);
            Assert.IsTrue(frame.TryStamp(10.0, 5.0, new Pose(Vector3.zero, Quaternion.identity)));
            Assert.IsFalse(frame.TryStamp(10.1, 5.1, new Pose(Vector3.right, Quaternion.identity)));
            Assert.AreEqual(5.0, frame.timestamp, "a replayed image must keep aging");
            Assert.AreEqual(Vector3.zero, frame.cameraPose.position, "a replayed image must keep its original pose");
        }

        [Test]
        public void ChangedContent_IsStamped()
        {
            var frame = Frame(1000, 1200, 0, 1500);
            Assert.IsTrue(frame.TryStamp(10.0, 5.0, new Pose()));
            frame.depthMillimeters[2] = 900;
            Assert.IsTrue(frame.TryStamp(10.1, 5.1, new Pose()));
            Assert.AreEqual(5.1, frame.timestamp);
        }

        [Test]
        public void AfterReset_IdenticalContentIsAcceptedAgain()
        {
            // A new session must not inherit the previous session's "already seen" image.
            var frame = Frame(1000, 1200);
            Assert.IsTrue(frame.TryStamp(10.0, 5.0, new Pose()));
            frame.providerTimestamp = double.NaN;
            Assert.IsTrue(frame.TryStamp(20.0, 9.0, new Pose()));
        }
    }

    public class FloorSelectorTests
    {
        static FloorCandidate Plane(string id, float y, float area) => new FloorCandidate(id, new Vector3(0f, y, 0f), Vector3.up, area);
        static FloorSelector Selector() => new FloorSelector(new MeasurementConfig());

        [Test]
        public void HandheldBand_RejectsKneeHeightAndOverheadCameras()
        {
            var f = new FloorPlane(Vector3.zero, Vector3.up);
            // The 10-07 corridor's wrong "floor" left the camera ~0.5 m above it.
            Assert.IsFalse(f.PlausibleCameraHeight(new Vector3(0f, 0.5f, 0f)));
            Assert.IsTrue(f.PlausibleCameraHeight(new Vector3(0f, 0.9f, 0f)));
            Assert.IsTrue(f.PlausibleCameraHeight(new Vector3(0f, 1.9f, 0f)));
            Assert.IsFalse(f.PlausibleCameraHeight(new Vector3(0f, 2.3f, 0f)));
        }

        [Test]
        public void LowestPlane_WinsOverLargerHigherPlane()
        {
            // Camera at 2.0 m: a big bench top at 0.6 m is still a plausible "floor" by height.
            var s = Selector();
            var floor = s.Update(new List<FloorCandidate> { Plane("bench", 0.6f, 4f), Plane("floor", 0f, 2f) }, new Vector3(0f, 2f, 0f), 0.0);
            Assert.IsTrue(floor.IsValid);
            Assert.AreEqual("floor", s.CurrentId);
        }

        [Test]
        public void SmallFragmentSlightlyLower_DoesNotDisplaceTheFloor()
        {
            var s = Selector();
            s.Update(new List<FloorCandidate> { Plane("floor", 0f, 3f), Plane("fragment", -0.05f, 0.2f) }, new Vector3(0f, 1.3f, 0f), 0.0);
            Assert.AreEqual("floor", s.CurrentId);
        }

        [Test]
        public void FloorHeightJump_SuspendsTheFloorUntilItSettles()
        {
            var s = Selector();
            var cam = new Vector3(0f, 1.6f, 0f);
            Assert.IsTrue(s.Update(new List<FloorCandidate> { Plane("a", 0f, 3f) }, cam, 0.0).IsValid);
            // Same plane re-estimated 0.6 m higher one frame later: no hand moves that fast.
            Assert.IsFalse(s.Update(new List<FloorCandidate> { Plane("a", 0.6f, 3f) }, cam, 0.033).IsValid);
            Assert.AreEqual(1, s.Jumps);
            Assert.IsFalse(s.Update(new List<FloorCandidate> { Plane("a", 0.6f, 3f) }, cam, 0.3).IsValid);
            Assert.IsTrue(s.Update(new List<FloorCandidate> { Plane("a", 0.6f, 3f) }, cam, 0.6).IsValid, "a level that holds is accepted");
        }

        [Test]
        public void SteadyHandMotion_KeepsTheFloor()
        {
            var s = Selector();
            for (int i = 0; i < 60; i++)
            {
                // Raising the phone 0.6 m over 2 s is ordinary handling, not a jump.
                var cam = new Vector3(0f, 1.0f + 0.01f * i, 0f);
                Assert.IsTrue(s.Update(new List<FloorCandidate> { Plane("a", 0f, 3f) }, cam, i / 30.0).IsValid, $"frame {i}");
            }
            Assert.AreEqual(0, s.Jumps);
        }

        [Test]
        public void NoUsablePlane_MeansNoFloor_AndResetForgetsHistory()
        {
            var s = Selector();
            Assert.IsFalse(s.Update(new List<FloorCandidate>(), new Vector3(0f, 1.4f, 0f), 0.0).IsValid);
            s.Update(new List<FloorCandidate> { Plane("a", 0f, 3f) }, new Vector3(0f, 1.4f, 0f), 1.0);
            s.Reset();
            Assert.IsFalse(s.Current.IsValid);
            Assert.IsNull(s.CurrentId);
            // After a reset the first floor is taken at face value, whatever the old level was.
            Assert.IsTrue(s.Update(new List<FloorCandidate> { Plane("b", 0.5f, 3f) }, new Vector3(0f, 1.6f, 0f), 1.033).IsValid);
        }
    }

    public class PoseJumpGuardTests
    {
        static PoseJumpGuard Guard() => new PoseJumpGuard(new MeasurementConfig());

        [Test]
        public void WalkingPace_StaysReliable()
        {
            var g = Guard();
            for (int i = 0; i < 90; i++)
            {
                g.Observe(new Vector3(0f, 1.4f, 1.5f * i / 30f), i / 30.0);
                Assert.IsTrue(g.IsReliable(i / 30.0), $"frame {i}");
            }
            Assert.AreEqual(0, g.Jumps);
        }

        [Test]
        public void OneFrameTeleport_IsUnreliableForTheHoldThenRecovers()
        {
            var g = Guard();
            g.Observe(Vector3.zero, 0.0);
            // 0.9 m in 15 ms: the largest single step in the 10-07 corridor log.
            g.Observe(new Vector3(0.9f, 0f, 0f), 0.015);
            Assert.AreEqual(1, g.Jumps);
            Assert.IsFalse(g.IsReliable(0.015));
            Assert.IsFalse(g.IsReliable(0.4));
            Assert.IsTrue(g.IsReliable(0.6));
        }

        [Test]
        public void FastButSmallJitter_IsNotAJump()
        {
            // 8 cm in 20 ms is 4 m/s, but a flick of the wrist does that; only big steps count.
            var g = Guard();
            g.Observe(Vector3.zero, 0.0);
            g.Observe(new Vector3(0.08f, 0f, 0f), 0.02);
            Assert.AreEqual(0, g.Jumps);
            Assert.IsTrue(g.IsReliable(0.02));
        }

        [Test]
        public void LongGap_RebaselinesInsteadOfJudging()
        {
            // After a pause (app backgrounded, frame hitch) distance over the gap says nothing about speed.
            var g = Guard();
            g.Observe(Vector3.zero, 0.0);
            g.Observe(new Vector3(3f, 0f, 0f), 2.0);
            Assert.AreEqual(0, g.Jumps);
            Assert.IsTrue(g.IsReliable(2.0));
        }
    }
}
