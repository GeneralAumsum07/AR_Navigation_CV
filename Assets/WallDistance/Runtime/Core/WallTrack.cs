using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// One fused wall in the map. Geometry follows WallObservation: origin on the base line at
    /// the extent midpoint, horizontal normal toward where it was seen from, direction = Cross(normal, up).
    /// </summary>
    public sealed class WallTrack
    {
        public readonly string id;
        public Vector3 origin, normal, up = Vector3.up;
        public float extentMin, extentMax;
        /// <summary>Information (1/σ²) of the offset along the normal, 1/m².</summary>
        public float offsetInfo;
        /// <summary>Information (1/σ²) of the yaw about up, 1/rad².</summary>
        public float angleInfo;
        public int observations;
        public double firstSeen, lastSeen;
        public MeasurementSource lastSource;
        public double lastFloorEdgeTime = double.NaN, lastLearnedTime = double.NaN, lastArPlaneTime = double.NaN;
        /// <summary>Where the latest learned (or edge) and AR-plane observations put the base, near this track's origin.</summary>
        public Vector3 lastLearnedPoint, lastArPlanePoint;
        /// <summary>Learned and ARCore sources agreed recently; refreshed by WallMap.Candidates/Prune.</summary>
        public bool crossChecked;
        public bool calibratedFromRawDepth;
        public float edgeSnapFraction = float.NaN;

        internal bool hasAnchorReference;
        internal Pose anchorReference;

        public WallTrack(string id) { this.id = id; }

        public Vector3 direction => Vector3.Cross(normal, up);
        public float Length => extentMax - extentMin;
        public float SignedDistance(Vector3 p) => Vector3.Dot(p - origin, normal);
        public Vector3 PointAt(float along) => origin + direction * along;

        /// <summary>
        /// The best source that observed this wall within <paramref name="window"/> seconds:
        /// edge-confirmed beats learned beats ARCore plane. Falls back to the last source when
        /// nothing is recent (the wall is being remembered, not seen).
        /// </summary>
        public MeasurementSource Source(double now, float window)
        {
            if (Recent(lastFloorEdgeTime, now, window)) return MeasurementSource.FloorEdge;
            if (Recent(lastLearnedTime, now, window)) return MeasurementSource.LearnedDepth;
            if (Recent(lastArPlaneTime, now, window)) return MeasurementSource.PlaneOnly;
            return lastSource;
        }

        internal static bool Recent(double t, double now, float window) => !double.IsNaN(t) && now - t <= window;
    }
}
