using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class WallMapTests
    {
        static WallMap NewMap()
        {
            var m = new WallMap(new MeasurementConfig());
            m.SetSession("s1");
            return m;
        }

        [Test]
        public void RepeatedNoisyObservations_ConvergeAndCapInformation()
        {
            var map = NewMap();
            var rng = new System.Random(11);
            for (int i = 0; i < 30; i++)
            {
                // Triangular noise (sum of two uniforms): up to ±1.7 cm and ±0.85°, σ ≈ 0.7 cm and 0.35°.
                float dx = 0.01f * (float)(rng.NextDouble() + rng.NextDouble() - 1.0) * 1.7f;
                float da = 0.5f * (float)(rng.NextDouble() + rng.NextDouble() - 1.0) * 1.7f;
                map.Observe(Fx.Obs(1f + dx, i * 0.1, angleDeg: da), i * 0.1);
            }
            Assert.AreEqual(1, map.Tracks.Count);
            var w = map.Tracks[0];
            // Once capped (after 4 frames), each frame gets weight 0.2, so the estimate's spread is
            // a third of the input's: ~0.23 cm and ~0.12°. The bounds below are > 5σ.
            Assert.AreEqual(1f, w.origin.x, 0.015f);
            Assert.LessOrEqual(Vector3.Angle(w.normal, Vector3.left), 0.6f);
            Assert.AreEqual(1f / (0.005f * 0.005f), w.offsetInfo, 1f, "offset information is capped");
            Assert.AreEqual(30, w.observations);
        }

        [Test]
        public void Association_OffsetAngleAndGapRules()
        {
            var a = NewMap(); a.Observe(Fx.Obs(1f, 0), 0); a.Observe(Fx.Obs(1.1f, 0.1), 0.1);
            Assert.AreEqual(1, a.Tracks.Count, "10 cm apart: same wall");

            var b = NewMap(); b.Observe(Fx.Obs(1f, 0), 0); b.Observe(Fx.Obs(1.3f, 0.1), 0.1);
            Assert.AreEqual(2, b.Tracks.Count, "30 cm apart: a recess or another wall");

            var c = NewMap(); c.Observe(Fx.Obs(1f, 0), 0); c.Observe(Fx.Obs(1f, 0.1, angleDeg: 12f), 0.1);
            Assert.AreEqual(2, c.Tracks.Count, "12° apart: different wall");

            var d = NewMap(); d.Observe(Fx.Obs(1f, 0, 0f, 3f), 0); d.Observe(Fx.Obs(1f, 0.1, 4f, 6f), 0.1);
            Assert.AreEqual(2, d.Tracks.Count, "1 m along-wall gap: separate segments");

            var e = NewMap(); e.Observe(Fx.Obs(1f, 0, 0f, 3f), 0); e.Observe(Fx.Obs(1f, 0.1, 3.3f, 5f), 0.1);
            Assert.AreEqual(1, e.Tracks.Count, "30 cm gap: same wall");
            Assert.AreEqual(5f, e.Tracks[0].Length, 1e-3f, "extent is the union");
        }

        [Test]
        public void Prune_DropsTracksUnseenFor30Seconds()
        {
            var map = NewMap();
            map.Observe(Fx.Obs(1f, 0), 0);
            map.Observe(Fx.Obs(-1f, 20, angleDeg: 180f), 20);
            Assert.AreEqual(0, map.Prune(29.9));
            Assert.AreEqual(1, map.Prune(30.5));
            Assert.AreEqual(1, map.Tracks.Count);
            // Component check: NUnit compares Vector3 with exact Equals, and AngleAxis(180) leaves ~1e-7 residue.
            Assert.AreEqual(1f, map.Tracks[0].normal.x, 1e-5f, "the left wall survived");
        }

        [Test]
        public void NewSession_ClearsTheMap()
        {
            var map = NewMap();
            map.Observe(Fx.Obs(1f, 0), 0);
            map.SetSession("s1");
            Assert.AreEqual(1, map.Tracks.Count, "same session keeps walls");
            map.SetSession("s2");
            Assert.AreEqual(0, map.Tracks.Count);
        }

        [Test]
        public void Base_KeepsTheLowestObservedLevel()
        {
            var map = NewMap();
            map.Observe(Fx.Obs(1f, 0, baseY: 0.3f, src: MeasurementSource.PlaneOnly), 0);
            map.Observe(Fx.Obs(1f, 0.1, baseY: 0f), 0.1);
            map.Observe(Fx.Obs(1f, 0.2, baseY: 0.2f, src: MeasurementSource.PlaneOnly), 0.2);
            Assert.AreEqual(0f, map.Tracks[0].origin.y, 1e-5f);
        }

        [Test]
        public void CrossCheck_NeedsBothSourcesWithin5cmAnd2Seconds()
        {
            var pass = NewMap();
            pass.Observe(Fx.Obs(1f, 0), 0);
            pass.Observe(Fx.Obs(1.03f, 0.5, src: MeasurementSource.PlaneOnly), 0.5);
            Assert.IsTrue(pass.Candidates(1.0)[0].crossChecked, "3 cm apart");
            Assert.IsFalse(pass.Candidates(2.6)[0].crossChecked, "learned observation older than 2 s");

            var fail = NewMap();
            fail.Observe(Fx.Obs(1f, 0), 0);
            fail.Observe(Fx.Obs(1.08f, 0.5, src: MeasurementSource.PlaneOnly), 0.5);
            Assert.AreEqual(1, fail.Tracks.Count, "8 cm still associates (≤ 15 cm)");
            Assert.IsFalse(fail.Candidates(1.0)[0].crossChecked, "8 cm apart");
        }

        [Test]
        public void Source_PrefersEdgeThenLearnedThenPlane_WithinWindow()
        {
            var map = NewMap();
            var w = map.Observe(Fx.Obs(1f, 0, src: MeasurementSource.PlaneOnly), 0);
            Assert.AreEqual(MeasurementSource.PlaneOnly, w.Source(0.1, 2f));
            map.Observe(Fx.Obs(1f, 0.2), 0.2);
            Assert.AreEqual(MeasurementSource.LearnedDepth, w.Source(0.3, 2f));
            map.Observe(Fx.Obs(1f, 0.4, src: MeasurementSource.FloorEdge), 0.4);
            Assert.AreEqual(MeasurementSource.FloorEdge, w.Source(0.5, 2f));
            map.Observe(Fx.Obs(1f, 3.0, src: MeasurementSource.PlaneOnly), 3.0);
            Assert.AreEqual(MeasurementSource.PlaneOnly, w.Source(3.1, 2f), "edge and learned are older than 2 s");
            Assert.AreEqual(MeasurementSource.PlaneOnly, w.Source(20.0, 2f), "nothing recent: last source");
        }

        [Test]
        public void Candidate_MatchesTrackGeometry()
        {
            var map = NewMap();
            var w = map.Observe(Fx.Obs(1f, 0, 2f, 5f), 0);
            var c = map.Candidates(0.1)[0];
            Assert.AreEqual(w.id, c.id);
            Assert.IsTrue(c.isTracked);
            Assert.AreEqual(MeasurementSource.LearnedDepth, c.source);
            Assert.AreEqual(Vector3.left.x, c.normal.x, 1e-5f);
            Assert.AreEqual(0.8f, WallGeometry.PerpendicularDistance(c, new Vector3(0.2f, 1.4f, 3f)), 1e-4f);
            Assert.AreEqual(3f * 2.4f, c.Area(), 1e-3f, "length × nominal height");
            Assert.AreEqual(1f, c.LocalToWorld(c.boundary[0]).x, 1e-4f);
            Assert.AreEqual(0f, c.LocalToWorld(c.boundary[0]).y, 1e-4f, "polygon starts at the base");
        }

        [Test]
        public void AnchorPose_AppliesOnlyTheDeltaSinceTheLastCall()
        {
            var map = NewMap();
            var w = map.Observe(Fx.Obs(1f, 0), 0);
            var anchor = new Pose(new Vector3(1f, 0f, 1.5f), Quaternion.identity);
            Assert.IsTrue(map.ApplyAnchorPose(w.id, anchor), "first call stores the reference");
            Assert.AreEqual(1f, w.origin.x, 1e-5f);
            Assert.IsTrue(map.ApplyAnchorPose(w.id, new Pose(anchor.position + new Vector3(0.05f, 0f, 0f), Quaternion.identity)));
            Assert.AreEqual(1.05f, w.origin.x, 1e-5f, "ARCore moved the anchor 5 cm, so the wall moves 5 cm");
            Assert.IsFalse(map.ApplyAnchorPose("nope", anchor));
        }

        [Test]
        public void FromArPlane_VerticalPlaneBecomesPlaneOnlyObservation()
        {
            var c = Fx.WallAtZ(2f, halfWidth: 1f, halfHeight: 1f, yCenter: 1.2f);
            var o = WallObservation.FromArPlane(c, Vector3.up, 5.0);
            Assert.IsNotNull(o);
            Assert.AreEqual(MeasurementSource.PlaneOnly, o.source);
            Assert.AreEqual(0.2f, o.origin.y, 1e-4f, "base = lowest boundary point");
            Assert.AreEqual(2f, o.Length, 1e-4f);
            Assert.AreEqual(2f, o.SignedDistance(Vector3.zero), 1e-4f, "normal faces the camera at the origin");
            var floorLike = new WallCandidate();
            floorLike.Set("f", true, Vector3.zero, Quaternion.identity, new[] { Vector2.zero, Vector2.right, Vector2.up });
            Assert.IsNull(WallObservation.FromArPlane(floorLike, Vector3.up, 5.0));
        }
    }
}
