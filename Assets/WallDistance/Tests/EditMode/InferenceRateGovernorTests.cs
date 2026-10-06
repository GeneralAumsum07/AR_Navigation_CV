using NUnit.Framework;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class InferenceRateGovernorTests
    {
        [Test]
        public void StartsAt15Hz()
        {
            var g = new InferenceRateGovernor();
            Assert.IsFalse(g.Throttled);
            Assert.AreEqual(1.0 / 15.0, g.MinIntervalSeconds, 1e-9);
            Assert.AreEqual("normal", g.State);
        }

        [Test]
        public void SlowForFiveSeconds_DropsTo5Hz()
        {
            var g = new InferenceRateGovernor();
            for (int i = 0; i < 50; i++) g.Record(70, i * 0.1);   // t = 0 … 4.9
            Assert.IsFalse(g.Throttled, "4.9 s is not yet 5 s");
            g.Record(70, 5.0);
            Assert.IsTrue(g.Throttled);
            Assert.AreEqual(0.2, g.MinIntervalSeconds, 1e-9);
            Assert.AreEqual("throttled", g.State);
            Assert.AreEqual(1, g.Transitions);
        }

        [Test]
        public void OneFastInference_RestartsTheSlowClock()
        {
            var g = new InferenceRateGovernor();
            for (int i = 0; i <= 30; i++) g.Record(70, i * 0.1);   // 0 … 3.0
            g.Record(20, 3.1);
            for (int i = 32; i <= 70; i++) g.Record(70, i * 0.1);  // 3.2 … 7.0: only 3.8 s slow
            Assert.IsFalse(g.Throttled);
        }

        [Test]
        public void ThirtySecondsFast_RecoversTo15Hz()
        {
            var g = new InferenceRateGovernor();
            for (int i = 0; i <= 50; i++) g.Record(70, i * 0.1);
            Assert.IsTrue(g.Throttled);
            for (int i = 0; i < 150; i++) g.Record(20, 6.0 + i * 0.2);   // 6.0 … 35.8
            Assert.IsTrue(g.Throttled, "29.8 s fast is not yet 30 s");
            g.Record(20, 36.0);
            Assert.IsFalse(g.Throttled);
            Assert.AreEqual(2, g.Transitions);
        }
    }
}
