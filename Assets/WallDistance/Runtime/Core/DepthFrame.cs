using System;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Pinhole intrinsics expressed in the pixel grid of a specific image. When a depth image is
    /// smaller than the colour image the intrinsics came from, use <see cref="ScaledTo"/>.
    /// </summary>
    [Serializable]
    public struct DepthIntrinsics
    {
        public float fx, fy, cx, cy;
        public int width, height;

        public DepthIntrinsics ScaledTo(int newWidth, int newHeight)
        {
            float sx = (float)newWidth / width;
            float sy = (float)newHeight / height;
            return new DepthIntrinsics
            {
                fx = fx * sx, fy = fy * sy, cx = cx * sx, cy = cy * sy,
                width = newWidth, height = newHeight,
            };
        }
    }

    /// <summary>
    /// A CPU copy of one depth image plus everything needed to turn a pixel into a session-space
    /// point. The AR adapter fills this; the validator only reads it. Keeping it a plain managed
    /// buffer means tests can fabricate frames and iOS can supply its own later.
    ///
    /// Conventions (ARCore-compatible):
    ///  - Pixel (0,0) is the top-left of the SENSOR image (landscape), row-major.
    ///  - <see cref="depthMillimeters"/>[v*width+u] is the Z-depth (distance along the optical
    ///    axis, not the ray length) in millimetres; 0 means "no data".
    ///  - <see cref="confidence"/>[v*width+u] is 0..255, 255 = most confident; may be null.
    ///  - Camera space is Unity's: +X right, +Y up, +Z forward. Image v grows DOWN, so Y is
    ///    negated during projection.
    /// </summary>
    public sealed class DepthFrame
    {
        public int width;
        public int height;
        public ushort[] depthMillimeters;
        public byte[] confidence;
        public DepthIntrinsics intrinsics;
        /// <summary>Camera pose (session space) at the moment the image was acquired.</summary>
        public Pose cameraPose;
        /// <summary>Seconds since app start when the frame was acquired; used for staleness.</summary>
        public double timestamp;
        /// <summary>Provider image clock, in seconds. Its epoch is platform-specific.</summary>
        public double providerTimestamp = double.NaN;

        // Fingerprint of the depth buffer as last stamped. Only meaningful while providerTimestamp
        // is set: clearing that (a session reset) also forgets which content was already seen.
        ulong _stampedContentHash;

        /// <summary>
        /// Stamp a newly observed provider image once. Re-acquiring the same image must not
        /// extend its lifetime or attach a later camera pose to old depth geometry.
        /// Arrival time is a conservative local freshness proxy, not an absolute sensor clock.
        ///
        /// "Same image" is judged by content as well as by timestamp: on the OnePlus 13R, ARCore
        /// handed back one raw depth image for seconds at a time while stamping it with each new
        /// camera frame's time, so a timestamp-only check took it as fresh and the distance froze.
        /// Call this after copying the new pixels into <see cref="depthMillimeters"/>.
        /// </summary>
        public bool TryStamp(double sourceSeconds, double arrivalSeconds, Pose pose)
        {
            if (double.IsNaN(sourceSeconds) || double.IsInfinity(sourceSeconds) ||
                (!double.IsNaN(providerTimestamp) && sourceSeconds <= providerTimestamp)) return false;
            ulong hash = ContentHash(depthMillimeters, width * height);
            // Real depth-from-motion output is noisy, so two genuinely new images are never
            // bit-identical across thousands of pixels; identical content is a replay.
            if (!double.IsNaN(providerTimestamp) && depthMillimeters != null && hash == _stampedContentHash) return false;
            providerTimestamp = sourceSeconds;
            timestamp = arrivalSeconds;
            cameraPose = pose;
            _stampedContentHash = hash;
            return true;
        }

        /// <summary>
        /// FNV-1a over the first <paramref name="count"/> samples. A full pass, not a sample: a
        /// replay check that looked at a few pixels would call a mostly-static scene a replay.
        /// ~14k samples for ARCore's 160x90 raw depth, well under a millisecond.
        /// </summary>
        static ulong ContentHash(ushort[] mm, int count)
        {
            if (mm == null) return 0;
            ulong h = 14695981039346656037UL;
            int n = Math.Min(count, mm.Length);
            for (int i = 0; i < n; i++)
            {
                h = (h ^ (byte)mm[i]) * 1099511628211UL;
                h = (h ^ (byte)(mm[i] >> 8)) * 1099511628211UL;
            }
            return h;
        }

        public bool IsUsable => width > 0 && height > 0 && depthMillimeters != null
                                && depthMillimeters.Length >= width * height && intrinsics.fx > 0f;

        /// <summary>Project a session-space point to pixel coordinates. False if behind the camera.</summary>
        public bool TryProject(Vector3 worldPoint, out int u, out int v, out float depthMeters)
        {
            Vector3 cam = Quaternion.Inverse(cameraPose.rotation) * (worldPoint - cameraPose.position);
            u = v = 0;
            depthMeters = cam.z;
            if (cam.z <= 1e-4f) return false;
            float uf = intrinsics.fx * cam.x / cam.z + intrinsics.cx;
            float vf = intrinsics.fy * (-cam.y) / cam.z + intrinsics.cy;
            u = Mathf.RoundToInt(uf);
            v = Mathf.RoundToInt(vf);
            return u >= 0 && u < width && v >= 0 && v < height;
        }

        /// <summary>Back-project a pixel with a known Z-depth to a session-space point.</summary>
        public Vector3 Unproject(int u, int v, float depthMeters)
        {
            float x = (u - intrinsics.cx) / intrinsics.fx * depthMeters;
            float y = -(v - intrinsics.cy) / intrinsics.fy * depthMeters;
            Vector3 cam = new Vector3(x, y, depthMeters);
            return cameraPose.position + cameraPose.rotation * cam;
        }

        /// <summary>Depth at a pixel in metres, or NaN when the sensor reported no data.</summary>
        public float DepthAt(int u, int v)
        {
            ushort mm = depthMillimeters[v * width + u];
            return mm == 0 ? float.NaN : mm * 0.001f;
        }

        public byte ConfidenceAt(int u, int v)
        {
            return confidence == null ? (byte)255 : confidence[v * width + u];
        }
    }
}
