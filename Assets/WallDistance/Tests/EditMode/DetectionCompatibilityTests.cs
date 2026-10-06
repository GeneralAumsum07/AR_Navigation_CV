using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class DetectionCompatibilityTests
    {
        [TestCase(264,"264° W")]
        [TestCase(360,"000° N")]
        [TestCase(90,"090° E")]
        public void BearingLabels(float degrees,string expected) => Assert.AreEqual(expected,WallBearing.Format(degrees));

        [Test]
        public void BearingUsesDirectionTowardWallRatherThanItsNormal()
        {
            Assert.AreEqual(10,WallBearing.TowardWall(280,Vector3.forward,Vector3.right),0.001);
            Assert.IsTrue(float.IsNaN(WallBearing.TowardWall(float.NaN,Vector3.forward,Vector3.right)));
        }

        [Test]
        public void DenseCompatibilityRestoresAimedAndClosestWithoutRawConfidence()
        {
            var cam=Fx.MakeCamera(Vector3.zero,Quaternion.identity);
            try {
                var dense=Fx.DepthForPlane(new Pose(Vector3.zero,Quaternion.identity),Vector3.forward*2,Vector3.back,0);
                dense.confidence=null;
                var input=Fx.Input(cam,new WallCandidate[0],0);
                input.denseDepth=dense;
                var engine=new WallMeasurementEngine(new MeasurementConfig { allowDenseDetection=true });
                var snapshot=engine.Update(input);
                Assert.IsTrue(snapshot.aimed.isValid);
                Assert.IsTrue(snapshot.nearest.isValid);
                Assert.AreEqual(2,snapshot.nearest.rawDistanceMeters,0.02);
                input.now=2;
                Assert.IsFalse(engine.Update(input).nearest.isValid,"Stale frames must not leave a ghost wall");
                input.now=0; input.sessionTracking=false;
                Assert.IsFalse(engine.Update(input).aimed.isValid);
            } finally { Object.DestroyImmediate(cam.gameObject); }
        }
    }
}
