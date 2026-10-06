using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// The metric floor in session space. It is the only source of absolute scale for learned
    /// depth, so it is deliberately strict: an invalid plane is never replaced by an assumed
    /// camera height (spec §6).
    /// </summary>
    public readonly struct FloorPlane
    {
        /// <summary>Camera heights outside this band mean the "floor" is a table, a shelf or a mis-tracked plane.</summary>
        public const float MinCameraHeight = 0.4f, MaxCameraHeight = 2.5f;

        public readonly Vector3 point;
        /// <summary>Unit normal pointing away from the floor (gravity up). Zero for an invalid plane.</summary>
        public readonly Vector3 up;

        public FloorPlane(Vector3 point, Vector3 up)
        {
            this.point = point;
            this.up = up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.zero;
        }

        /// <summary>default(FloorPlane) has a zero normal, so "no floor" needs no extra flag.</summary>
        public bool IsValid => up.sqrMagnitude > 0.5f;

        public float HeightAbove(Vector3 p) => Vector3.Dot(p - point, up);

        public Vector3 Project(Vector3 p) => p - up * HeightAbove(p);

        public bool PlausibleCameraHeight(Vector3 camera)
        {
            float h = HeightAbove(camera);
            return IsValid && h >= MinCameraHeight && h <= MaxCameraHeight;
        }

        /// <summary>
        /// Intersect a ray with the floor. Rays that do not descend (or barely descend, which
        /// would put the hit tens of metres away with huge error) are rejected.
        /// </summary>
        public bool TryIntersect(Vector3 origin, Vector3 dir, out Vector3 hit, out float t)
        {
            hit = default;
            t = 0f;
            if (!IsValid) return false;
            float denom = Vector3.Dot(dir, up);
            if (denom > -1e-4f) return false;
            t = -HeightAbove(origin) / denom;
            if (t <= 0f) return false;
            hit = origin + dir * t;
            return true;
        }
    }
}
