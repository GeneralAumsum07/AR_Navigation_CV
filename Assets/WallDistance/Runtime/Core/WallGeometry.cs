using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Pure geometry. No state, no AR types, so EditMode tests can pin every formula down with
    /// known inputs. All distances are metres in whatever frame the inputs share.
    /// </summary>
    public static class WallGeometry
    {
        /// <summary>
        /// Perpendicular (not along-ray) distance from a point to the candidate's infinite plane.
        /// This is what "distance from the wall" means for navigation: it does not grow when the
        /// user turns the phone. Always non-negative.
        /// </summary>
        public static float PerpendicularDistance(WallCandidate c, Vector3 point)
        {
            return Mathf.Abs(Vector3.Dot(point - c.position, c.normal));
        }

        /// <summary>Foot of the perpendicular from <paramref name="point"/> onto the plane.</summary>
        public static Vector3 ProjectOntoPlane(WallCandidate c, Vector3 point)
        {
            float signed = Vector3.Dot(point - c.position, c.normal);
            return point - c.normal * signed;
        }

        /// <summary>Normal flipped so it points toward the viewer.</summary>
        public static Vector3 NormalFacing(WallCandidate c, Vector3 viewer)
        {
            return Vector3.Dot(viewer - c.position, c.normal) >= 0f ? c.normal : -c.normal;
        }

        /// <summary>
        /// Ray/plane intersection restricted to the bounded polygon. Returns false if the ray is
        /// parallel, hits behind the origin, or hits the infinite plane outside the polygon -
        /// the last case is how we avoid "measuring" a wall through a doorway gap.
        /// </summary>
        public static bool RaycastPolygon(WallCandidate c, Ray ray, out Vector3 hitPoint, out float rayDistance)
        {
            hitPoint = default;
            rayDistance = 0f;
            float denom = Vector3.Dot(c.normal, ray.direction);
            if (Mathf.Abs(denom) < 1e-6f) return false;
            float t = Vector3.Dot(c.position - ray.origin, c.normal) / denom;
            if (t < 0f) return false;
            Vector3 p = ray.origin + ray.direction * t;
            Vector3 local = c.WorldToLocal(p);
            if (!PointInPolygon(c.boundary, new Vector2(local.x, local.z))) return false;
            hitPoint = p;
            rayDistance = t;
            return true;
        }

        /// <summary>Even-odd rule point-in-polygon test in plane-local (x, z) coordinates.</summary>
        public static bool PointInPolygon(IReadOnlyList<Vector2> poly, Vector2 p)
        {
            int n = poly.Count;
            if (n < 3) return false;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = poly[i], b = poly[j];
                // Does the horizontal ray from p cross edge (a,b)?
                bool crosses = (a.y > p.y) != (b.y > p.y);
                if (crosses)
                {
                    float x = (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x;
                    if (p.x < x) inside = !inside;
                }
            }
            return inside;
        }

        public static float PolygonArea(IReadOnlyList<Vector2> poly)
        {
            int n = poly.Count;
            if (n < 3) return 0f;
            float s = 0f;
            for (int i = 0, j = n - 1; i < n; j = i++)
                s += poly[j].x * poly[i].y - poly[i].x * poly[j].y;
            return Mathf.Abs(s) * 0.5f;
        }

        /// <summary>
        /// Shortest distance from <paramref name="point"/> to the BOUNDED polygon (not the infinite
        /// plane). If the perpendicular foot lands inside the polygon that is the answer; otherwise
        /// the nearest point is on an edge, so a wall that ends at a doorway or corner is not
        /// extended past its detected extent.
        /// </summary>
        public static float NearestPointOnPolygon(WallCandidate c, Vector3 point, out Vector3 nearest)
        {
            Vector3 foot = ProjectOntoPlane(c, point);
            Vector3 local = c.WorldToLocal(foot);
            var local2 = new Vector2(local.x, local.z);
            if (PointInPolygon(c.boundary, local2))
            {
                nearest = foot;
                return Vector3.Distance(point, foot);
            }

            float best = float.PositiveInfinity;
            nearest = foot;
            int n = c.boundary.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector3 a = c.LocalToWorld(c.boundary[j]);
                Vector3 b = c.LocalToWorld(c.boundary[i]);
                Vector3 q = ClosestPointOnSegment(a, b, point);
                float d = Vector3.Distance(point, q);
                if (d < best)
                {
                    best = d;
                    nearest = q;
                }
            }
            return best;
        }

        public static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-9f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return a + ab * t;
        }

        /// <summary>
        /// Is any part of the polygon inside the camera frustum? Cheap conservative test: a polygon
        /// counts as "observed" if its centre or any boundary vertex projects inside the viewport
        /// with positive depth. A large wall whose vertices are all off-screen but whose middle
        /// fills the view is caught by the extra interior samples along the edges to the centre.
        /// </summary>
        public static bool IntersectsView(WallCandidate c, Matrix4x4 worldToClip)
        {
            if (c.boundary.Count < 3) return false;
            Vector2 centre = Vector2.zero;
            for (int i = 0; i < c.boundary.Count; i++) centre += c.boundary[i];
            centre /= c.boundary.Count;
            if (InClip(worldToClip, c.LocalToWorld(centre))) return true;
            const int edgeSteps = 8;
            int n = c.boundary.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 v = c.boundary[i];
                if (InClip(worldToClip, c.LocalToWorld(v))) return true;
                // Midpoints between vertex and centre catch the "vertices off-screen" case.
                if (InClip(worldToClip, c.LocalToWorld((v + centre) * 0.5f))) return true;
                // Points along each edge catch a wall edge (e.g. a doorway jamb) that crosses a
                // narrow portrait view between two far-apart vertices.
                Vector2 prev = c.boundary[j];
                for (int s = 1; s < edgeSteps; s++)
                    if (InClip(worldToClip, c.LocalToWorld(Vector2.Lerp(prev, v, (float)s / edgeSteps)))) return true;
            }
            return false;
        }

        static bool InClip(Matrix4x4 m, Vector3 world)
        {
            Vector4 clip = m * new Vector4(world.x, world.y, world.z, 1f);
            if (clip.w <= 1e-6f) return false;   // behind the camera
            float x = clip.x / clip.w, y = clip.y / clip.w, z = clip.z / clip.w;
            return x >= -1f && x <= 1f && y >= -1f && y <= 1f && z >= -1f && z <= 1f;
        }
    }
}
