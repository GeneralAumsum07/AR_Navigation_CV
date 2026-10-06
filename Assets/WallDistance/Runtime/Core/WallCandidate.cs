using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// A detected vertical surface that MIGHT be a wall. Geometry alone cannot distinguish a wall
    /// from a cupboard or whiteboard, hence "candidate". This is a provider-agnostic snapshot of an
    /// AR plane: the AR adapter copies ARPlane data into this each frame so the engine and tests
    /// never touch AR Foundation types.
    /// </summary>
    public sealed class WallCandidate
    {
        /// <summary>Trackable id (stable while the plane exists; a merged plane gets a new one).</summary>
        public string id;
        /// <summary>Distinguishes user-selected floor geometry from automatically detected walls.</summary>
        public MeasurementSource source = MeasurementSource.PlaneOnly;
        /// <summary>
        /// Map walls only: a learned observation and an independent ARCore plane recently agreed
        /// within MeasurementConfig.crossCheckToleranceMeters. Always false for raw AR planes.
        /// </summary>
        public bool crossChecked;
        /// <summary>Learned metric scale used raw depth, so that raw depth cannot independently validate it.</summary>
        public bool calibratedFromRawDepth;

        /// <summary>True only while the provider reports the plane as actively tracked.</summary>
        public bool isTracked;

        /// <summary>Plane origin in session space.</summary>
        public Vector3 position;

        /// <summary>Plane rotation in session space. Plane-space Y is the normal; polygon lies in plane-space XZ.</summary>
        public Quaternion rotation;

        /// <summary>
        /// Bounded polygon in plane-local space (x, z) - matches ARPlane.boundary convention where
        /// each Vector2 is (localX, localZ). Convex for ARCore, but nothing here assumes convexity.
        /// </summary>
        public readonly List<Vector2> boundary = new List<Vector2>();

        public Vector3 normal => rotation * Vector3.up;

        public Vector3 LocalToWorld(Vector2 p) => position + rotation * new Vector3(p.x, 0f, p.y);

        public Vector3 WorldToLocal(Vector3 w) => Quaternion.Inverse(rotation) * (w - position);

        /// <summary>Copy-free reuse: the adapter refills the same instance each frame.</summary>
        public void Set(string id, bool tracked, Vector3 pos, Quaternion rot, IReadOnlyList<Vector2> polygon)
        {
            this.id = id;
            isTracked = tracked;
            position = pos;
            rotation = rot;
            boundary.Clear();
            if (polygon != null)
                for (int i = 0; i < polygon.Count; i++)
                    boundary.Add(polygon[i]);
        }

        /// <summary>Polygon area via the shoelace formula (absolute value).</summary>
        public float Area() => WallGeometry.PolygonArea(boundary);
    }
}
