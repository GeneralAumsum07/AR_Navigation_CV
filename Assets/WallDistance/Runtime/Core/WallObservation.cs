using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// One vertical wall segment seen in one frame (or one ARCore plane snapshot), described by
    /// its base line on the floor. Base line rather than plane centre because the base is what
    /// the edge refiner can confirm and what "distance to the wall" means for navigation.
    /// </summary>
    public sealed class WallObservation
    {
        /// <summary>On the base line at floor height, at the middle of the observed extent.</summary>
        public Vector3 origin;
        /// <summary>Horizontal unit normal pointing from the wall toward the observing camera.</summary>
        public Vector3 normal;
        public Vector3 up = Vector3.up;
        /// <summary>Observed extent along <see cref="direction"/>, relative to origin.</summary>
        public float extentMin, extentMax;
        public int inliers;
        /// <summary>RMS distance of supporting points from the fitted line, metres.</summary>
        public float rms;
        public MeasurementSource source = MeasurementSource.LearnedDepth;
        public float edgeSnapFraction = float.NaN;
        public double timestamp;

        /// <summary>Along-wall unit vector; same convention as a candidate built with LookRotation(up, normal).</summary>
        public Vector3 direction => Vector3.Cross(normal, up);
        public float Length => extentMax - extentMin;
        public float SignedDistance(Vector3 p) => Vector3.Dot(p - origin, normal);
        public Vector3 PointAt(float along) => origin + direction * along;
        public WallObservation Clone() => (WallObservation)MemberwiseClone();

        /// <summary>
        /// An ARCore vertical plane as an observation, so the map can fuse and cross-check it.
        /// Null when the plane tilts more than <paramref name="maxTiltDeg"/> from vertical.
        /// </summary>
        public static WallObservation FromArPlane(WallCandidate c, Vector3 up, double now, float maxTiltDeg = 10f)
        {
            if (c == null || c.boundary.Count < 3) return null;
            if (Mathf.Abs(Vector3.Dot(c.normal, up)) > Mathf.Sin(maxTiltDeg * Mathf.Deg2Rad)) return null;
            if (!Horizontal.TryDirection(c.normal, up, out Vector3 n)) return null;
            var o = new WallObservation
            {
                normal = n, up = up, timestamp = now, source = MeasurementSource.PlaneOnly,
                // ARCore gives no per-plane error; 2 cm is a nominal figure so plane observations
                // weigh about the same as one good learned frame in the map.
                rms = 0.02f, inliers = 1,
            };
            float baseLevel = float.PositiveInfinity;
            for (int i = 0; i < c.boundary.Count; i++)
                baseLevel = Mathf.Min(baseLevel, Vector3.Dot(c.LocalToWorld(c.boundary[i]), up));
            Vector3 p0 = c.position;
            o.origin = p0 + up * (baseLevel - Vector3.Dot(p0, up));
            Vector3 dir = o.direction;
            float aMin = float.PositiveInfinity, aMax = float.NegativeInfinity;
            for (int i = 0; i < c.boundary.Count; i++)
            {
                float a = Vector3.Dot(c.LocalToWorld(c.boundary[i]) - o.origin, dir);
                aMin = Mathf.Min(aMin, a);
                aMax = Mathf.Max(aMax, a);
            }
            float mid = 0.5f * (aMin + aMax);
            o.origin += dir * mid;
            o.extentMin = aMin - mid;
            o.extentMax = aMax - mid;
            return o;
        }
    }
}
