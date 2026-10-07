using NUnit.Framework;
using WallDistance.Core;

namespace WallDistance.Tests
{
    /// <summary>
    /// The pipeline discards inference results that are too old or belong to another session
    /// without any visible sign. On the phone that looked like "the distance froze", so the
    /// discard count is logged; these tests pin what is and is not counted.
    /// </summary>
    public class DroppedFramesTests
    {
        static InverseDepthImage Frame(string session, double time)
        {
            var image = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
            image.timestamp = time;
            image.sessionId = session;
            return image;
        }

        [Test]
        public void FreshFrame_IsNotCounted()
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("A", Frame("A", 1.0), SyntheticCorridor.Floor, null, 1.03);
            Assert.AreEqual(0, p.FramesDropped);
        }

        [Test]
        public void ExpiredOtherSessionAndFutureFrames_AreEachCounted()
        {
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("A", Frame("A", 1.0), SyntheticCorridor.Floor, null, 3.0);   // 2 s old > 1 s
            p.ProcessFrame("A", Frame("B", 3.0), SyntheticCorridor.Floor, null, 3.01);  // other session
            p.ProcessFrame("A", Frame("A", 5.0), SyntheticCorridor.Floor, null, 4.0);   // clock went backwards
            Assert.AreEqual(3, p.FramesDropped);
        }

        [Test]
        public void Count_SurvivesSessionReset()
        {
            // Cumulative since start: a recording that spans a session reset must still show
            // every discard, or a drop burst right before the reset would vanish from the log.
            var p = new WallDetectionPipeline(new MeasurementConfig());
            p.ProcessFrame("A", Frame("A", 1.0), SyntheticCorridor.Floor, null, 3.0);
            p.SetSession("B");
            p.Reset();
            Assert.AreEqual(1, p.FramesDropped);
        }
    }
}
