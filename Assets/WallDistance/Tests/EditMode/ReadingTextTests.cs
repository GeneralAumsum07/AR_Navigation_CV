using NUnit.Framework;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class ReadingTextTests
    {
        static WallReading Valid(MeasurementKind k, float d) => new WallReading { isValid = true, kind = k, distanceMeters = d };
        static WallReading Bad(MeasurementKind k, FailureReason f) => WallReading.Invalid(k, f, "s", 0, default);

        [Test]
        public void Quality_ExistingTextUnchanged_NewLabelsNamed()
        {
            Assert.AreEqual("Depth validated", ReadingText.Quality(QualityLabel.DepthValidated));
            Assert.AreEqual("Plane estimate", ReadingText.Quality(QualityLabel.PlaneEstimate));
            Assert.AreEqual("Depth estimate (no plane)", ReadingText.Quality(QualityLabel.DepthEstimate));
            Assert.AreEqual("Assisted floor geometry", ReadingText.Quality(QualityLabel.AssistedEstimate));
            Assert.AreEqual("Unreliable", ReadingText.Quality(QualityLabel.Unreliable));
            Assert.AreEqual("Out of tested range", ReadingText.Quality(QualityLabel.OutOfTestedRange));
            Assert.AreEqual("Learned depth estimate", ReadingText.Quality(QualityLabel.LearnedEstimate));
            Assert.AreEqual("Edge confirmed", ReadingText.Quality(QualityLabel.EdgeConfirmed));
            Assert.AreEqual("Cross-checked", ReadingText.Quality(QualityLabel.CrossChecked));
            Assert.AreEqual("Unavailable", ReadingText.Quality(QualityLabel.Unavailable));
        }

        [Test]
        public void Failure_TellsTheUserWhatToDo()
        {
            Assert.AreEqual("aim at a wall", ReadingText.Failure(FailureReason.NoWallUnderCrosshair));
            Assert.AreEqual("scan slowly", ReadingText.Failure(FailureReason.NoWallInView));
            Assert.AreEqual("tracking lost", ReadingText.Failure(FailureReason.TrackingLost));
            Assert.AreEqual("wall lost", ReadingText.Failure(FailureReason.CandidateLost));
            Assert.AreEqual("AR not tracking", ReadingText.Failure(FailureReason.SessionNotTracking));
            Assert.AreEqual("point at the floor for a moment", ReadingText.Failure(FailureReason.NoFloor));
            Assert.AreEqual("calibrating — keep walking", ReadingText.Failure(FailureReason.Calibrating));
            Assert.AreEqual("show more floor", ReadingText.Failure(FailureReason.AlignmentFailed));
            Assert.AreEqual("ML depth unavailable", ReadingText.Failure(FailureReason.InferenceUnavailable));
            Assert.AreEqual("detecting…", ReadingText.Failure(FailureReason.InferenceStale));
            Assert.AreEqual("hold the phone more upright", ReadingText.Failure(FailureReason.NoHeading));
            Assert.AreEqual("no wall seen on this side", ReadingText.Failure(FailureReason.NoWallOnSide));
            Assert.AreEqual("not ready", ReadingText.Failure(FailureReason.None));
        }

        [Test]
        public void Sides_FormatsBothWallsAndWidth()
        {
            Assert.AreEqual("L 0.84 m | R 1.12 m | W 1.96 m",
                ReadingText.Sides(Valid(MeasurementKind.CorridorLeft, 0.84f), Valid(MeasurementKind.CorridorRight, 1.12f), 1.96f));
            Assert.AreEqual("L — | R 1.12 m | W —",
                ReadingText.Sides(Bad(MeasurementKind.CorridorLeft, FailureReason.NoWallOnSide), Valid(MeasurementKind.CorridorRight, 1.12f), float.NaN));
        }

        [Test]
        public void SidesHint_NamesTheMissingSide()
        {
            var l = Valid(MeasurementKind.CorridorLeft, 1f);
            var r = Valid(MeasurementKind.CorridorRight, 1f);
            Assert.AreEqual("", ReadingText.SidesHint(l, r));
            var noHeadingL = Bad(MeasurementKind.CorridorLeft, FailureReason.NoHeading);
            var noHeadingR = Bad(MeasurementKind.CorridorRight, FailureReason.NoHeading);
            Assert.AreEqual("hold the phone more upright", ReadingText.SidesHint(noHeadingL, noHeadingR));
            Assert.AreEqual("right: no wall seen on this side", ReadingText.SidesHint(l, Bad(MeasurementKind.CorridorRight, FailureReason.NoWallOnSide)));
            Assert.AreEqual("left: tracking lost · right: no wall seen on this side",
                ReadingText.SidesHint(Bad(MeasurementKind.CorridorLeft, FailureReason.TrackingLost), Bad(MeasurementKind.CorridorRight, FailureReason.NoWallOnSide)));
        }
    }
}
