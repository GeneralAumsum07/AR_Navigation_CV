using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>Floor-plane projections shared by the extractor, the map and the side selector.</summary>
    public static class Horizontal
    {
        /// <summary>
        /// Project <paramref name="v"/> onto the plane perpendicular to <paramref name="up"/> and
        /// normalise. False when v is (nearly) parallel to up, where the direction is meaningless.
        /// </summary>
        public static bool TryDirection(Vector3 v, Vector3 up, out Vector3 dir, float minLength = 1e-3f)
        {
            Vector3 p = v - up * Vector3.Dot(v, up);
            float m = p.magnitude;
            if (m < minLength)
            {
                dir = Vector3.zero;
                return false;
            }
            dir = p / m;
            return true;
        }
    }
}
