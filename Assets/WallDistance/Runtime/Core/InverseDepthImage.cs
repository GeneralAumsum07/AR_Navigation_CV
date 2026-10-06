using System;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// One network output in the 518² inference image, with the camera that image belongs to.
    /// Same conventions as DepthFrame: pixel index = pixel-centre coordinate, v grows down,
    /// camera +X right / +Y up / +Z forward. <see cref="values"/> holds the raw network output d
    /// (relative inverse depth before alignment) and NaN in the letterbox padding.
    /// </summary>
    public sealed class InverseDepthImage
    {
        public readonly int width;
        public readonly int height;
        public readonly float[] values;
        /// <summary>Grey image at the same scale; the base-edge refiner snaps against it.</summary>
        public readonly byte[] luma;
        public DepthIntrinsics intrinsics;
        /// <summary>Pose of the (rotated, upright) inference camera when the image was captured.</summary>
        public Pose cameraPose;
        /// <summary>Seconds since app start when the camera image arrived.</summary>
        public double timestamp;
        /// <summary>Pixels that come from the camera; everything else is letterbox padding.</summary>
        public RectInt content;
        public double inferenceMilliseconds = double.NaN;

        public InverseDepthImage(int width, int height)
        {
            this.width = width;
            this.height = height;
            values = new float[width * height];
            luma = new byte[width * height];
            Array.Fill(values, float.NaN);
        }

        public bool InContent(int u, int v) => content.Contains(new Vector2Int(u, v));

        /// <summary>Camera-space ray with unit Z, so a distance along it IS the Z-depth.</summary>
        public Vector3 CameraRay(float u, float v) =>
            new Vector3((u - intrinsics.cx) / intrinsics.fx, -(v - intrinsics.cy) / intrinsics.fy, 1f);

        public Vector3 WorldRay(float u, float v) => cameraPose.rotation * CameraRay(u, v);

        public Vector3 WorldPoint(float u, float v, float z) => cameraPose.position + WorldRay(u, v) * z;

        public bool TryProject(Vector3 world, out float u, out float v, out float z)
        {
            Vector3 c = Quaternion.Inverse(cameraPose.rotation) * (world - cameraPose.position);
            z = c.z;
            u = v = 0f;
            if (z <= 1e-4f) return false;
            u = intrinsics.fx * c.x / z + intrinsics.cx;
            v = intrinsics.fy * -c.y / z + intrinsics.cy;
            return true;
        }
    }
}
