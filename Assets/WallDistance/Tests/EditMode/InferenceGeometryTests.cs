using NUnit.Framework;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    public class InferenceGeometryTests
    {
        static readonly DepthIntrinsics Sensor = new DepthIntrinsics
        {
            fx = 500f, fy = 505f, cx = 318.5f, cy = 241.2f, width = 640, height = 480,
        };
        static readonly Pose SensorPose = new Pose(new Vector3(0.3f, 1.4f, -0.2f), Quaternion.Euler(20f, 35f, 5f));

        /// <summary>
        /// The whole point of InferenceGeometry: mapping a sensor pixel into the 518² image must
        /// give the same pixel as projecting the world point through the rotated, letterboxed
        /// camera. If the rotation, intrinsics or pose disagree by one convention, this fails.
        /// </summary>
        [Test]
        public void SensorMapping_AgreesWithInferenceCamera_ForAllQuarterTurns(
            [Values(0, 1, 2, 3)] int k)
        {
            var g = InferenceGeometry.Create(640, 480, k);
            var intr = g.Intrinsics(Sensor);
            var camPose = g.CameraPose(SensorPose);
            foreach (float z in new[] { 1f, 3f })
            for (float x = -0.4f; x <= 0.4f; x += 0.2f)
            for (float y = -0.3f; y <= 0.3f; y += 0.15f)
            {
                var camPoint = new Vector3(x * z, y * z, z);
                Vector3 world = SensorPose.position + SensorPose.rotation * camPoint;
                float su = Sensor.fx * camPoint.x / z + Sensor.cx;
                float sv = Sensor.fy * -camPoint.y / z + Sensor.cy;
                Vector2 mapped = g.SensorToInference(new Vector2(su, sv));

                Vector3 c = Quaternion.Inverse(camPose.rotation) * (world - camPose.position);
                float iu = intr.fx * c.x / c.z + intr.cx;
                float iv = intr.fy * -c.y / c.z + intr.cy;
                Assert.AreEqual(iu, mapped.x, 2e-3f, $"u, k={k}");
                Assert.AreEqual(iv, mapped.y, 2e-3f, $"v, k={k}");
                Assert.AreEqual(c.z, z, 1e-4f, "rotation about the optical axis keeps Z-depth");
            }
        }

        [Test]
        public void RoundTrip_SensorToInferenceToSensor([Values(0, 1, 2, 3)] int k)
        {
            var g = InferenceGeometry.Create(640, 480, k);
            var p = new Vector2(123.25f, 77.5f);
            Vector2 back = g.InferenceToSensor(g.SensorToInference(p));
            Assert.AreEqual(p.x, back.x, 1e-3f);
            Assert.AreEqual(p.y, back.y, 1e-3f);
        }

        [Test]
        public void PortraitLetterbox_ContentIsCentredAndFullHeight()
        {
            var g = InferenceGeometry.Create(640, 480, 1);
            Assert.AreEqual(518f / 640f, g.scale, 1e-6f);
            Assert.AreEqual(new RectInt(65, 0, 388, 518), g.content);
        }

        [Test]
        public void ChooseQuarterTurns_PicksTheTurnThatMakesTheImageUpright([Values(0, 1, 2, 3)] int expected)
        {
            // Display (upright) camera is the sensor camera turned by `expected` quarter turns.
            Quaternion display = Quaternion.Euler(10f, 30f, 0f);
            Quaternion sensor = display * Quaternion.Inverse(Quaternion.AngleAxis(90f * expected, Vector3.forward));
            Assert.AreEqual(expected, InferenceGeometry.ChooseQuarterTurns(sensor, display * Vector3.up));
        }

        [Test]
        public void InverseDepthImage_ProjectAndBackProject_RoundTrip()
        {
            var img = new InverseDepthImage(100, 80)
            {
                intrinsics = new DepthIntrinsics { fx = 90f, fy = 90f, cx = 49.5f, cy = 39.5f, width = 100, height = 80 },
                cameraPose = SensorPose,
                content = new RectInt(10, 0, 80, 80),
            };
            Vector3 w = img.WorldPoint(30.25f, 20.75f, 2.5f);
            Assert.IsTrue(img.TryProject(w, out float u, out float v, out float z));
            Assert.AreEqual(30.25f, u, 1e-3f);
            Assert.AreEqual(20.75f, v, 1e-3f);
            Assert.AreEqual(2.5f, z, 1e-4f);
            Assert.IsTrue(float.IsNaN(img.values[0]), "values start as NaN, never 0");
            Assert.IsFalse(img.InContent(5, 5));
            Assert.IsTrue(img.InContent(10, 0));
            Assert.IsFalse(img.InContent(90, 0), "RectInt max is exclusive");
        }
    }
}
