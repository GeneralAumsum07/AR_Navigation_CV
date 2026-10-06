using System.Collections.Generic;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    /// <summary>Builders for synthetic walls, cameras and depth frames shared across the tests.</summary>
    static class Fx
    {
        /// <summary>
        /// A vertical wall in the plane z = <paramref name="z"/>, facing -Z (toward a camera at
        /// the origin looking +Z). Plane-local: X = world X, local Z (= boundary.y) = world Y.
        /// </summary>
        public static WallCandidate WallAtZ(float z, float halfWidth = 2f, float halfHeight = 1.5f, string id = "wall", float xCenter = 0f, float yCenter = 0f)
        {
            var c = new WallCandidate();
            // Rotate so plane-space +Y (normal) points to -Z world: rotating Vector3.up by -90 deg about X gives -Z... we want
            // rotation * up = (0,0,-1). Quaternion.FromToRotation handles it, then fix X-axis to stay world X.
            var rot = Quaternion.LookRotation(Vector3.up, Vector3.back); // forward=+Y(world), up=-Z(world)
            c.Set(id, true, new Vector3(xCenter, yCenter, z), rot, new[]
            {
                new Vector2(-halfWidth, -halfHeight),
                new Vector2( halfWidth, -halfHeight),
                new Vector2( halfWidth,  halfHeight),
                new Vector2(-halfWidth,  halfHeight),
            });
            return c;
        }

        public static Camera MakeCamera(Vector3 pos, Quaternion rot, float fov = 60f)
        {
            var go = new GameObject("TestCam");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.aspect = 9f / 16f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            go.transform.SetPositionAndRotation(pos, rot);
            return cam;
        }

        public static EngineInput Input(Camera cam, IReadOnlyList<WallCandidate> walls, double now, DepthFrame depth = null, string session = "s1", bool tracking = true)
        {
            return new EngineInput
            {
                sessionId = session,
                sessionTracking = tracking,
                now = now,
                cameraPose = new Pose(cam.transform.position, cam.transform.rotation),
                worldToClip = cam.projectionMatrix * cam.worldToCameraMatrix,
                crosshairRay = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)),
                candidates = walls,
                depth = depth,
                denseDepth = depth, // synthetic depth is dense; real raw depth is sparse
                depthSupported = depth != null,
            };
        }

        /// <summary>
        /// Synthesise a depth image by ray-casting every pixel against an analytic plane. The
        /// result is exactly what a perfect sensor would return, so the validator's mapping is
        /// tested end-to-end: if projection or unprojection were wrong, residuals would not be ~0.
        /// </summary>
        public static DepthFrame DepthForPlane(Pose camPose, Vector3 planePoint, Vector3 planeNormal, double timestamp,
            int w = 160, int h = 90, float fovDeg = 60f, float biasMeters = 0f, byte confidence = 255)
        {
            var f = new DepthFrame { width = w, height = h, timestamp = timestamp, cameraPose = camPose };
            float fy = 0.5f * h / Mathf.Tan(fovDeg * 0.5f * Mathf.Deg2Rad);
            f.intrinsics = new DepthIntrinsics { fx = fy, fy = fy, cx = w * 0.5f, cy = h * 0.5f, width = w, height = h };
            f.depthMillimeters = new ushort[w * h];
            f.confidence = new byte[w * h];
            for (int v = 0; v < h; v++)
            {
                for (int u = 0; u < w; u++)
                {
                    // Camera-space ray for this pixel (unit z).
                    var dir = new Vector3((u - f.intrinsics.cx) / f.intrinsics.fx, -(v - f.intrinsics.cy) / f.intrinsics.fy, 1f);
                    Vector3 worldDir = camPose.rotation * dir;
                    float denom = Vector3.Dot(planeNormal, worldDir);
                    if (Mathf.Abs(denom) < 1e-6f) continue;
                    float t = Vector3.Dot(planePoint - camPose.position, planeNormal) / denom;
                    if (t <= 0f) continue;
                    float z = t; // dir has unit z, so t IS the Z-depth
                    z += biasMeters;
                    f.depthMillimeters[v * w + u] = (ushort)Mathf.RoundToInt(z * 1000f);
                    f.confidence[v * w + u] = confidence;
                }
            }
            return f;
        }
        /// <summary>
        /// A wall observation whose base line runs along z from <paramref name="z0"/> to <paramref name="z1"/>
        /// at x = <paramref name="x"/>. angleDeg = 0 faces -x (a RIGHT wall of a corridor along +z);
        /// angleDeg = 180 faces +x (a LEFT wall). Other angles yaw the wall about its origin.
        /// </summary>
        public static WallObservation Obs(float x, double t, float z0 = 0f, float z1 = 3f, float angleDeg = 0f,
            MeasurementSource src = MeasurementSource.LearnedDepth, float rms = 0.01f, int inliers = 500, float baseY = 0f)
        {
            float half = 0.5f * (z1 - z0);
            return new WallObservation
            {
                origin = new Vector3(x, baseY, 0.5f * (z0 + z1)),
                normal = Quaternion.AngleAxis(angleDeg, Vector3.up) * Vector3.left,
                up = Vector3.up, extentMin = -half, extentMax = half,
                rms = rms, inliers = inliers, source = src, timestamp = t,
            };
        }
    }
}
