using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class CameraHeightEstimatorTests
    {
        static readonly Vector3 Camera14 = new Vector3(0.2f, 1.4f, 0f);

        static HeightClue Clue(double t, float floorY, float weight = 40f, float cameraY = 1.4f) => new HeightClue
        {
            time = t, floorY = floorY, heightMeters = cameraY - floorY, weight = weight, source = HeightClueSource.RawDepth,
        };

        /// <summary>Clues every 0.1 s over [from, to] with the floor at floorY; TryFloor after each, as the resolver does.</summary>
        static bool Feed(CameraHeightEstimator e, double from, double to, float floorY, out FloorPlane floor)
        {
            floor = default;
            bool ok = false;
            // Integer steps: summing 0.1 twenty times gives 1.9999999999999996, which would miss
            // the 2 s span gate by float roundoff and fail for the wrong reason.
            for (int k = 0; from + k * 0.1 <= to + 1e-9; k++)
            {
                double t = from + k * 0.1;
                e.Add(Clue(t, floorY));
                ok = e.TryFloor(Camera14, t, out floor);
            }
            return ok;
        }

        [Test]
        public void NotReadyBeforeTwoSeconds_IsCalibrating()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsFalse(Feed(e, 0.0, 1.5, 0f, out _));
            Assert.IsFalse(e.Ready);
            Assert.IsTrue(e.IsCalibrating(1.5));
            Assert.AreEqual(FailureReason.Calibrating, e.Explain(FailureReason.NoFloor, 1.5));
            Assert.AreEqual(FailureReason.AlignmentFailed, e.Explain(FailureReason.AlignmentFailed, 1.5), "only NoFloor is refined");
        }

        [Test]
        public void ReadyAfterTwoSeconds_FloorUnderCamera()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0.01f, out var floor));
            Assert.AreEqual(1.39f, floor.HeightAbove(Camera14), 1e-4f);
            Assert.AreEqual(Vector3.up, floor.up);
            Assert.AreEqual(1.39f, e.HeightMeters, 1e-4f);
            Assert.AreEqual(FailureReason.NoFloor, e.Explain(FailureReason.NoFloor, 2.0), "ready: nothing to refine");
        }

        [Test]
        public void NoActivity_IsNoFloorNotCalibrating()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsFalse(e.IsCalibrating(10.0));
            Assert.AreEqual(FailureReason.NoFloor, e.Explain(FailureReason.NoFloor, 10.0));
            e.NoteActivity(9.0);
            Assert.AreEqual(FailureReason.Calibrating, e.Explain(FailureReason.NoFloor, 10.0));
        }

        [Test]
        public void WeightedMedian_RejectsThirtyPercentOutliers()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            FloorPlane floor = default;
            bool ok = false;
            for (int k = 0; k <= 25; k++)
            {
                double t = k * 0.1;
                e.Add(Clue(t, k % 10 < 3 ? 0.5f : 0.005f * (k % 3)));
                ok = e.TryFloor(Camera14, t, out floor);
            }
            Assert.IsTrue(ok);
            Assert.AreEqual(1.4f, floor.HeightAbove(Camera14), 0.02f);
        }

        [Test]
        public void DisagreeingEvidence_IsNotHeld()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0f, out _));
            // Then a full window of evidence smeared evenly over 0-0.4 m: no height is better
            // supported than its neighbours, so recalibrate rather than hold. (A 50/50 split
            // between two floors would NOT test this: a weighted median lands on one mode and the
            // MAD is 0, the same robustness that rejects a 30% minority in the test above.)
            bool ok = true;
            for (int k = 21; k <= 75; k++)
            {
                double t = k * 0.1;
                e.Add(Clue(t, 0.4f * (k % 9) / 8f));
                ok = e.TryFloor(Camera14, t, out _);
            }
            Assert.IsFalse(ok);
            Assert.Greater(e.SpreadMeters, 0.07f);
        }

        [Test]
        public void RaisingThePhone_KeepsTheFloor()
        {
            // Clues are stored as the floor's world height, so a hand moving up 0.3 m (tracked
            // by ARCore) moves the camera, not the floor.
            var e = new CameraHeightEstimator(new DetectionConfig());
            for (int k = 0; k <= 25; k++)
            {
                float cameraY = 1.4f + 0.3f * k / 25f;
                e.Add(Clue(k * 0.1, 0f, cameraY: cameraY));
                e.TryFloor(new Vector3(0f, cameraY, 0f), k * 0.1, out _);
            }
            Assert.IsTrue(e.TryFloor(new Vector3(0f, 1.7f, 0f), 2.5, out var floor));
            Assert.AreEqual(1.7f, floor.HeightAbove(new Vector3(0f, 1.7f, 0f)), 1e-4f);
        }

        [Test]
        public void Hold_KeepsEstimateForTwentySeconds_ThenExpires()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0f, out _));
            Assert.IsTrue(e.TryFloor(Camera14, 10.0, out var held), "no clues for 8 s: held");
            Assert.AreEqual(1.4f, held.HeightAbove(Camera14), 1e-4f);
            Assert.IsFalse(e.TryFloor(Camera14, 22.5, out _), "20.5 s since the last good estimate");
        }

        [Test]
        public void Discontinuity_CarriesCameraHeight()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0f, out _));
            e.MarkDiscontinuity();
            Assert.AreEqual(0, e.WindowClues, "clues from the old world frame are dropped");
            // ARCore's world moved: the same physical camera is now at y = 5.
            var jumped = new Vector3(3f, 5f, -1f);
            Assert.IsTrue(e.TryFloor(jumped, 2.5, out var floor), "no calibrating gap after a jump");
            Assert.AreEqual(1.4f, floor.HeightAbove(jumped), 1e-4f);
            Assert.AreEqual(3.6f, e.FloorY, 1e-4f);
        }

        [Test]
        public void Discontinuity_BeforeReady_CarriesNothing()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Feed(e, 0.0, 1.0, 0f, out _);
            e.MarkDiscontinuity();
            Assert.IsFalse(e.TryFloor(new Vector3(0f, 5f, 0f), 1.5, out _));
        }

        [Test]
        public void Reset_ForgetsEverything()
        {
            var e = new CameraHeightEstimator(new DetectionConfig());
            Assert.IsTrue(Feed(e, 0.0, 2.0, 0f, out _));
            e.Reset();
            Assert.IsFalse(e.TryFloor(Camera14, 2.1, out _));
            Assert.IsFalse(e.IsCalibrating(2.1));
            Assert.AreEqual(0, e.WindowClues);
        }
    }
}
