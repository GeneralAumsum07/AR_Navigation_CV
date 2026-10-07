using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    /// <summary>
    /// Pins the integer value of every enum member. Scenes, CSV analysis and any consumer that
    /// stored an enum as an int depend on these never shifting, so new values are appended only.
    /// </summary>
    public class ApiCompatibilityTests
    {
        [Test]
        public void ExistingEnumValues_KeepTheirIntegers_NewOnesAreAppended()
        {
            Assert.AreEqual(0, (int)MeasurementKind.Aimed);
            Assert.AreEqual(1, (int)MeasurementKind.NearestObserved);
            Assert.AreEqual(2, (int)MeasurementKind.CorridorLeft);
            Assert.AreEqual(3, (int)MeasurementKind.CorridorRight);

            Assert.AreEqual(4, (int)MeasurementSource.AssistedFloor);
            Assert.AreEqual(5, (int)MeasurementSource.LearnedDepth);
            Assert.AreEqual(6, (int)MeasurementSource.FloorEdge);

            Assert.AreEqual(6, (int)QualityLabel.AssistedEstimate);
            Assert.AreEqual(7, (int)QualityLabel.LearnedEstimate);
            Assert.AreEqual(8, (int)QualityLabel.EdgeConfirmed);
            Assert.AreEqual(9, (int)QualityLabel.CrossChecked);

            Assert.AreEqual(5, (int)FailureReason.CandidateLost);
            Assert.AreEqual(6, (int)FailureReason.NoFloor);
            Assert.AreEqual(7, (int)FailureReason.AlignmentFailed);
            Assert.AreEqual(8, (int)FailureReason.InferenceUnavailable);
            Assert.AreEqual(9, (int)FailureReason.InferenceStale);
            Assert.AreEqual(10, (int)FailureReason.NoHeading);
            Assert.AreEqual(11, (int)FailureReason.NoWallOnSide);
            Assert.AreEqual(12, (int)FailureReason.Calibrating);
        }

        [Test]
        public void Invalid_IsNaNNotZero_AndHasEmptySourceChain()
        {
            var r = WallReading.Invalid(MeasurementKind.CorridorLeft, FailureReason.NoWallOnSide, "s", 1.0, default);
            Assert.IsFalse(r.isValid);
            Assert.IsTrue(float.IsNaN(r.distanceMeters));
            Assert.IsTrue(float.IsNaN(r.rawDistanceMeters));
            Assert.AreEqual(MeasurementKind.CorridorLeft, r.kind);
            Assert.AreEqual("", r.sourceChain);
            Assert.AreEqual("NoWallOnSide", r.qualityReason);
        }

        [Test]
        public void SideDefaults_MatchSpecSection55()
        {
            var c = new MeasurementConfig();
            Assert.AreEqual(20f, c.sideNoHeadingDeg);
            Assert.AreEqual(30f, c.sideEnterAngleDeg);
            Assert.AreEqual(40f, c.sideExitAngleDeg);
            Assert.AreEqual(1.0f, c.sideExtentMarginMeters);
            Assert.AreEqual(10f, c.sideMaxAgeSeconds);
            Assert.AreEqual(6f, c.sideMaxDistanceMeters);
            Assert.AreEqual(10f, c.corridorParallelToleranceDeg);
            Assert.AreEqual(0.05f, c.crossCheckToleranceMeters);
        }

        [Test]
        public void DetectionDefaults_MatchSpecSections52And6()
        {
            var d = new MeasurementConfig().detection;
            Assert.IsNotNull(d);
            Assert.AreEqual(DepthParameterisation.AffineInverseDepth, d.parameterisation);
            Assert.AreEqual(500, d.minFloorInliers);
            Assert.AreEqual(0.03f, d.maxAlignResidual);
            Assert.AreEqual(200, d.minMetricSamples);
            Assert.AreEqual((byte)128, d.metricMinConfidence);
            Assert.AreEqual(4, d.maxPlanes);
            Assert.AreEqual(12, d.edgeBandPixels);
            Assert.AreEqual(0.6f, d.edgeMinSnapFraction);
            Assert.AreEqual(8f, d.associateMaxAngleDeg);
            Assert.AreEqual(0.15f, d.associateMaxOffsetMeters);
            Assert.AreEqual(2.4f, d.nominalWallHeightMeters);
            Assert.AreEqual(1f, d.staleAfterSeconds);
        }

        [Test]
        public void ConfigJson_IncludesDetectionBlock_SoCsvHeadersLogIt()
        {
            string json = JsonUtility.ToJson(new MeasurementConfig());
            StringAssert.Contains("\"detection\"", json);
            StringAssert.Contains("\"minFloorInliers\"", json);
            StringAssert.Contains("\"sideEnterAngleDeg\"", json);
        }

        [Test]
        public void Candidate_CrossCheckedDefaultsFalse()
        {
            Assert.IsFalse(new WallCandidate().crossChecked);
        }
    }
}
