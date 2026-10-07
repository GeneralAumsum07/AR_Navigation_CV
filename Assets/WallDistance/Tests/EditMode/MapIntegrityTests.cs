using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    /// <summary>
    /// The wall map must not keep walls that are not there. Every case here comes from the
    /// 2026-10-07 balcony recording (walldist_20261007_140605), where readings of 0.01-0.3 m
    /// came from (A) walls stamped during ARCore pose jumps, (B) nothing ever removing a wall the
    /// camera could see through, (C) a bench segment whose extended line ran through the phone,
    /// and (D) one real wall stored as many copies because monocular depth jitters > 15 cm.
    /// </summary>
    public class MapIntegrityTests
    {
        // ------------------------------------------------------------ A: tracking continuity

        [Test]
        public void LosingTracking_ClearsOnce_AndIsCounted()
        {
            var g = new MapContinuityGuard();
            Assert.IsFalse(g.Update(true, 0.0), "starting to track has nothing stale to clear");
            Assert.IsFalse(g.Update(true, 0.1));
            Assert.IsTrue(g.Update(false, 0.2), "tracking lost (or a pose jump): the map's frame is suspect");
            Assert.IsFalse(g.Update(false, 0.3), "already cleared");
            Assert.IsFalse(g.Update(true, 0.4));
            Assert.AreEqual(1, g.Clears);
        }

        [Test]
        public void FramesCapturedBeforeTrackingResumed_AreRejected()
        {
            var g = new MapContinuityGuard();
            Assert.IsFalse(g.Accepts(0.0), "nothing is accepted before tracking first starts");
            g.Update(true, 0.0);
            Assert.IsTrue(g.Accepts(0.5));
            g.Update(false, 1.0);
            Assert.IsFalse(g.Accepts(1.2), "captured while lost");
            g.Update(true, 1.5);
            Assert.IsFalse(g.Accepts(0.9), "captured before the loss, finished after recovery");
            Assert.IsFalse(g.Accepts(1.2));
            Assert.IsTrue(g.Accepts(1.5));
        }

        [Test]
        public void ContinuityReset_ForgetsHistory()
        {
            var g = new MapContinuityGuard();
            g.Update(true, 0.0);
            g.Update(false, 1.0);
            g.Reset();
            Assert.AreEqual(0, g.Clears);
            Assert.IsFalse(g.Accepts(2.0));
            Assert.IsFalse(g.Update(true, 2.0));
            Assert.IsTrue(g.Accepts(2.0));
        }

        // ------------------------------------------------------------ B: see-through removal

        static InverseDepthImage Frame(Pose pose, double timestamp)
        {
            var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(pose));
            img.timestamp = timestamp;
            return img;
        }

        /// <summary>A wall across the corridor at <paramref name="z"/>, facing the camera at z = 0.</summary>
        static WallObservation Across(float z, float halfX = 0.8f) => new WallObservation
        {
            origin = new Vector3(0f, 0f, z), normal = Vector3.back, up = Vector3.up,
            extentMin = -halfX, extentMax = halfX, rms = 0.01f, inliers = 500,
        };

        static bool Has(WallMap m, string id)
        {
            foreach (var t in m.Tracks) if (t.id == id) return true;
            return false;
        }

        static void Run(WallDetectionPipeline p, Pose pose, int frames, double t0 = 1.0)
        {
            for (int i = 0; i < frames; i++)
                p.ProcessFrame("s1", Frame(pose, t0 + 0.1 * i), SyntheticCorridor.Floor, null, t0 + 0.1 * i + 0.03);
        }

        [Test]
        public void PhantomWallTheCameraSeesThrough_IsRemovedAfterConsecutiveFrames()
        {
            // The 10-07 case: a wall in the map 0.2-1.5 m ahead while the camera sees the real
            // wall metres behind it.
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.SetSession("s1");
            string ghost = p.Map.Observe(Across(1.5f), 0.9).id;
            var pose = SyntheticCorridor.Camera();

            Run(p, pose, 2);
            Assert.IsTrue(Has(p.Map, ghost), "two frames are not enough: one bad depth frame must not delete walls");
            Run(p, pose, 1, 1.2);
            Assert.IsFalse(Has(p.Map, ghost), "three consecutive see-through frames remove it");
            Assert.AreEqual(1, p.WallsCarved);

            bool right = false;
            foreach (var t in p.Map.Tracks)
                if (Vector3.Angle(t.normal, Vector3.left) < 1f && Mathf.Abs(t.SignedDistance(pose.position) - 0.8f) <= 0.02f) right = true;
            Assert.IsTrue(right, "real walls the camera keeps seeing survive");
        }

        [Test]
        public void PhantomRightInFrontOfThePhone_IsAlsoRemoved()
        {
            // 0.25 m ahead it spans more than the whole view. The first carver sampled points on
            // the wall's face, almost none of which projected into the frame; the replayed 10-07
            // frames caught that, so the near case is pinned here.
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.SetSession("s1");
            string ghost = p.Map.Observe(Across(0.25f), 0.9).id;
            Run(p, SyntheticCorridor.Camera(), 3);
            Assert.IsFalse(Has(p.Map, ghost));
        }

        [Test]
        public void WallHiddenBehindANearerSurface_IsKept()
        {
            // No evidence either way: the end wall at 5.5 m occludes it. Only seeing PAST a wall removes it.
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.SetSession("s1");
            string hidden = p.Map.Observe(Across(7f), 0.9).id;
            Run(p, SyntheticCorridor.Camera(), 6);
            Assert.IsTrue(Has(p.Map, hidden));
            Assert.AreEqual(0, p.WallsCarved);
        }

        [Test]
        public void ARealWallSeenAgain_ResetsItsSeeThroughCount()
        {
            // Two see-through frames, then one where the wall is confirmed, then two more: never three in a row.
            var cfg = new MeasurementConfig();
            var p = new WallDetectionPipeline(cfg);
            p.SetSession("s1");
            var track = p.Map.Observe(Across(1.5f), 0.9);
            var pose = SyntheticCorridor.Camera();
            Run(p, pose, 2);
            // A corridor whose end wall really is at 1.5 m confirms the track.
            var closed = new SyntheticCorridor { endWallZ = 1.5f };
            var img = SyntheticCorridor.ToNetworkOutput(closed.Render(pose));
            img.timestamp = 1.2;
            p.ProcessFrame("s1", img, SyntheticCorridor.Floor, null, 1.23);
            Run(p, pose, 2, 1.3);
            Assert.IsTrue(Has(p.Map, track.id));
        }

        // ------------------------------------------------------------ C: lines through the phone

        static bool AnyLineWithin(WallMap m, Vector3 cam, float meters)
        {
            foreach (var t in m.Tracks) if (Mathf.Abs(t.SignedDistance(cam)) < meters) return true;
            return false;
        }

        [Test]
        public void WallWhoseLinePassesThroughThePhone_IsNotMapped()
        {
            // The bench on 10-07: a real surface 1-1.7 m away whose extended line passed 0.04-0.45 m
            // from the phone. A wall cannot run through the person holding the phone.
            var pose = SyntheticCorridor.Camera(x: 0.8f);

            var control = new MeasurementConfig();
            control.detection.minLinePassMeters = 0f;
            var pc = new WallDetectionPipeline(control);
            pc.ProcessFrame("s1", Frame(pose, 1.0), SyntheticCorridor.Floor, null, 1.03);
            Assert.IsTrue(AnyLineWithin(pc.Map, pose.position, 0.3f), "control: the extractor does find the right wall 0.2 m away");

            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("s1", Frame(pose, 1.0), SyntheticCorridor.Floor, null, 1.03);
            Assert.IsFalse(AnyLineWithin(p.Map, pose.position, 0.3f));
            Assert.Greater(p.Map.Tracks.Count, 0, "the other walls are still mapped");
        }

        [Test]
        public void SideWallAheadWithinTheMargin_MeasuresToItsNearestEnd_NotItsExtendedLine()
        {
            // Right wall line 0.1 m from the camera, but the wall itself starts 0.9 m ahead.
            var map = new WallMap(new MeasurementConfig());
            map.SetSession("s1");
            map.Observe(Fx.Obs(0.3f, 0, 1.1f, 3f), 0);
            var cam = new Pose(new Vector3(0.2f, 1.4f, 0.2f), Quaternion.Euler(10f, 0f, 0f));
            Matrix4x4 clip = Matrix4x4.Perspective(60f, 9f / 16f, 0.05f, 50f) * Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
                             * Matrix4x4.TRS(cam.position, cam.rotation, Vector3.one).inverse;
            new CorridorSideSelector().Measure("s1", 0.1, cam, clip, Vector3.up, map.Tracks, new MeasurementConfig(),
                out _, out var right, out _);
            Assert.IsTrue(right.isValid);
            Assert.AreEqual(Mathf.Sqrt(0.1f * 0.1f + 0.9f * 0.9f), right.rawDistanceMeters, 1e-3f);
            Assert.AreEqual(1.4f, right.surfacePoint.y, 1e-4f, "foot is at camera height, like the perpendicular case");
            Assert.AreEqual(1.1f, right.surfacePoint.z, 1e-3f, "foot is on the wall's near end");
        }

        // ------------------------------------------------------------ D: one wall, one track

        static WallObservation Seen(float x, double t, Vector3 viewer, float z0 = 0f, float z1 = 3f)
        {
            var o = Fx.Obs(x, t, z0, z1);
            o.viewer = viewer;
            o.hasViewer = true;
            return o;
        }

        static WallMap NewMap()
        {
            var m = new WallMap(new MeasurementConfig());
            m.SetSession("s1");
            return m;
        }

        [Test]
        public void FarWallJitteringWithDepthNoise_StaysOneTrack()
        {
            // Replayed 10-07 frames put the orange wall at 2.53-2.95 m; the fixed 15 cm gate split it.
            var map = NewMap();
            float[] xs = { 2.8f, 2.6f, 2.95f, 2.7f, 2.9f, 2.65f };
            for (int i = 0; i < xs.Length; i++) map.Observe(Seen(xs[i], i * 0.1, Vector3.zero), i * 0.1);
            Assert.AreEqual(1, map.Tracks.Count);
        }

        [Test]
        public void NearRecess_StaysSeparate()
        {
            // At 1 m the gate stays at 15 cm, so a 30 cm door recess is still its own wall.
            var map = NewMap();
            map.Observe(Seen(1f, 0, Vector3.zero), 0);
            map.Observe(Seen(1.3f, 0.1, Vector3.zero), 0.1);
            Assert.AreEqual(2, map.Tracks.Count);
        }

        [Test]
        public void ObservationsWithoutAViewer_KeepTheFixedGate()
        {
            // ARCore plane observations have no viewing range; their 2 cm-scale geometry keeps 15 cm.
            var map = NewMap();
            map.Observe(Fx.Obs(2.8f, 0), 0);
            map.Observe(Fx.Obs(2.6f, 0.1), 0.1);
            Assert.AreEqual(2, map.Tracks.Count);
        }

        [Test]
        public void ObservationBridgingTwoCopies_MergesThem()
        {
            var map = NewMap();
            map.Observe(Fx.Obs(3f, 0, 0f, 2f), 0);
            map.Observe(Fx.Obs(3.25f, 0.1, 1.5f, 4f), 0.1);
            Assert.AreEqual(2, map.Tracks.Count, "precondition: two copies of one wall");

            map.Observe(Seen(3.12f, 0.2, Vector3.zero, 1f, 3f), 0.2);
            Assert.AreEqual(1, map.Tracks.Count);
            var w = map.Tracks[0];
            Assert.AreEqual(3, w.observations, "the survivor carries every observation");
            Assert.AreEqual(4f, w.Length, 0.05f, "extent is the union, 0-4 m");
            Assert.AreEqual(0.2, w.lastSeen, 1e-9);
        }

        [Test]
        public void Merge_KeepsTheMostObservedCopy()
        {
            var map = NewMap();
            for (int i = 0; i < 4; i++) map.Observe(Fx.Obs(3f, i * 0.1), i * 0.1);
            string seasoned = map.Tracks[0].id;
            map.Observe(Fx.Obs(3.25f, 0.5), 0.5);
            Assert.AreEqual(2, map.Tracks.Count);
            map.Observe(Seen(3.2f, 0.6, Vector3.zero), 0.6);
            Assert.AreEqual(1, map.Tracks.Count);
            Assert.AreEqual(seasoned, map.Tracks[0].id, "the long-seen copy survives, so its anchor and filter history do too");
        }
    }
}
