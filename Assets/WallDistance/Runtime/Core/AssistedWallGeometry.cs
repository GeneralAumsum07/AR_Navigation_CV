using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>Construct a vertical wall section from two user-selected points on a tracked floor.</summary>
    public static class AssistedWallGeometry
    {
        public static bool TryCreate(Vector3 a, Vector3 b, Vector3 up, Vector3 camera,
            out Pose pose, out float width, out string reason)
        {
            pose = default; width = 0; reason = "";
            if (!Finite(a) || !Finite(b) || !Finite(up) || up.sqrMagnitude < 0.5f)
            { reason = "Invalid floor geometry"; return false; }
            up.Normalize();
            var delta = b-a;
            if (Mathf.Abs(Vector3.Dot(delta,up)) > 0.05f)
            { reason = "Select both points on the same floor level"; return false; }
            delta = Vector3.ProjectOnPlane(delta,up);
            width = delta.magnitude;
            if (width < 0.4f)
            { reason = "Choose points at least 40 cm apart along the wall base"; return false; }
            if (width > 8f) { reason = "Choose a shorter wall section"; return false; }
            var centre = (a+b)*0.5f;
            var normal = Vector3.Cross(delta.normalized,up).normalized;
            if (Vector3.Dot(camera-centre,normal)<0) normal = -normal;
            // Plane-space X spans the wall; Z goes upward; Y is its normal.
            pose = new Pose(centre,Quaternion.LookRotation(up,normal));
            return true;
        }
        static bool Finite(Vector3 p) => !float.IsNaN(p.sqrMagnitude) && !float.IsInfinity(p.sqrMagnitude);
    }
}
