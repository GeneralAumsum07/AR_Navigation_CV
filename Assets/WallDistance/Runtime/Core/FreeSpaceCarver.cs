using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Removes mapped walls that the current depth frame sees straight past. The map only ever
    /// added walls: a wall stamped in the wrong place (during an ARCore pose jump, or from a bad
    /// frame) stayed for 30 s, even with the camera looking through it at the real wall behind.
    /// On 2026-10-07 that put a "wall" 0.1-0.3 m in front of the phone for eight seconds.
    ///
    /// Only seeing PAST a wall counts against it. A nearer surface in front of it (occlusion) is
    /// no evidence either way, and a sample that lands on it confirms it. Removal needs several
    /// consecutive frames, so one bad depth frame cannot delete a real wall.
    /// </summary>
    public sealed class FreeSpaceCarver
    {
        readonly DetectionConfig _cfg;
        readonly List<WallTrack> _doomed = new List<WallTrack>(4);

        /// <summary>Walls removed since construction (CSV diagnostic; survives map resets on purpose).</summary>
        public int Carved { get; private set; }

        public FreeSpaceCarver(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

        /// <summary>Judge every track against one aligned frame; removes the ones seen through too often.</summary>
        public void Carve(WallMap map, InverseDepthImage img, AlignmentResult align)
        {
            if (map == null || img == null || align == null || !align.success) return;
            _doomed.Clear();
            foreach (var t in map.Tracks)
            {
                Judge(t, img, align, out int through, out int support);
                int visible = through + support;
                // Too little of the wall in view (or all of it occluded): no verdict, keep the count.
                if (visible < _cfg.carveMinVisibleSamples) continue;
                if (through >= _cfg.carveMinThroughRatio * visible)
                {
                    if (++t.SeeThroughFrames >= _cfg.carveFramesToRemove) _doomed.Add(t);
                }
                else if (support > 0)
                {
                    // The camera can see the wall where the map says it is: start counting afresh.
                    t.SeeThroughFrames = 0;
                }
            }
            foreach (var t in _doomed)
                if (map.Remove(t)) Carved++;
        }

        /// <summary>
        /// Cast rays through a coarse pixel grid; where a ray meets the wall's face (inside its
        /// extent and height band), compare the depth the wall predicts with the depth measured.
        /// Sampling from the image side, not on the wall, matters for near walls: a phantom 0.25 m
        /// ahead spans more than the whole view, so points spread over its face (the first
        /// version) almost all fell outside the frame and it was never judged.
        /// </summary>
        void Judge(WallTrack t, InverseDepthImage img, AlignmentResult align, out int through, out int support)
        {
            through = support = 0;
            Vector3 cam = img.cameraPose.position;
            // Plane offset of the camera; the ray parameter to the plane is -offset / (n·ray).
            float camOffset = t.SignedDistance(cam);
            Vector3 dir = t.direction;
            int stride = Mathf.Max(1, _cfg.carvePixelStride);
            RectInt c = img.content;
            for (int v = c.yMin + stride / 2; v < c.yMax; v += stride)
            {
                for (int u = c.xMin + stride / 2; u < c.xMax; u += stride)
                {
                    // WorldRay has unit camera-Z, so the ray parameter at the hit IS the Z-depth.
                    Vector3 ray = img.WorldRay(u, v);
                    float denom = Vector3.Dot(t.normal, ray);
                    if (Mathf.Abs(denom) < 1e-4f) continue;
                    float expected = -camOffset / denom;
                    if (expected < _cfg.carveMinDepthMeters) continue;
                    Vector3 rel = cam + ray * expected - t.origin;
                    float along = Vector3.Dot(rel, dir);
                    if (along < t.extentMin || along > t.extentMax) continue;
                    // Height band: near the base the floor just behind a wall is barely farther
                    // than the wall, so low hits cannot tell the two apart.
                    float height = Vector3.Dot(rel, t.up);
                    if (height < _cfg.carveMinHeightMeters || height > _cfg.carveMaxHeightMeters) continue;
                    float measured = align.MetricDepth(img.values[v * img.width + u]);
                    if (float.IsNaN(measured)) continue;
                    float band = expected * _cfg.carveDepthFraction + _cfg.carveDepthMeters;
                    if (measured > expected + band) through++;
                    else if (measured >= expected - band) support++;
                    // else: something nearer hides the wall here; no evidence.
                }
            }
        }
    }
}
