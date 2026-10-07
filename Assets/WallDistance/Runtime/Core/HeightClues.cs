using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    public enum HeightClueSource { ArPlane, RawDepth }

    /// <summary>
    /// One frame's evidence for how high the camera is above the floor. Stored with the floor's
    /// world height (camera.y − height) so evidence from different hand heights can be pooled.
    /// </summary>
    public struct HeightClue
    {
        public double time;
        public float heightMeters;
        public float floorY;
        public float weight;
        public HeightClueSource source;
    }

    /// <summary>
    /// Turns what ARCore offers into camera-height clues (floor-free design §4.2).
    ///
    /// Confident raw depth on glossy corridors is sparse (median 35 pixels per frame in run
    /// 150956) but metric. Once self-alignment fixed the shape of the depth map, each such pixel
    /// votes for one number, the camera height, so even a handful per frame is useful. The
    /// per-frame fallback that needed 200 at once threw all of them away.
    /// </summary>
    public sealed class HeightClues
    {
        readonly DetectionConfig _cfg;
        float[] _h = new float[256];

        public HeightClues(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

        /// <summary>An ARCore floor is direct evidence; implausible planes (tables, sills) are not floors.</summary>
        public bool TryFromPlane(FloorPlane floor, Vector3 camera, double now, out HeightClue clue)
        {
            clue = default;
            if (!floor.PlausibleCameraHeight(camera)) return false;
            float h = floor.HeightAbove(camera);
            clue = new HeightClue
            {
                time = now,
                heightMeters = h,
                floorY = camera.y - h,
                weight = _cfg.heightPlaneWeight,
                source = HeightClueSource.ArPlane,
            };
            return true;
        }

        /// <summary>
        /// Median camera height voted by confident raw-depth samples. <paramref name="used"/> counts
        /// the samples that landed on valid network pixels, even when no clue results (CSV diagnostic).
        /// </summary>
        public bool TryFromDepth(InverseDepthImage img, SelfAlignment a, IReadOnlyList<MetricSample> samples,
            double now, out HeightClue clue, out int used)
        {
            clue = default;
            used = 0;
            if (img == null || a == null || !a.success || samples == null) return false;
            if (_h.Length < samples.Count) _h = new float[samples.Count];

            int n = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                int u = Mathf.RoundToInt(s.pixel.x), v = Mathf.RoundToInt(s.pixel.y);
                // Letterbox pixels hold no camera content, so the network's value there means nothing.
                if (!img.InContent(u, v) || !(s.depthMeters > 0f)) continue;
                float d = img.values[v * img.width + u];
                float shifted = d + a.r;
                if (float.IsNaN(d) || !(shifted > 0f)) continue;
                // z = k / (d + r) and H = k · hRel, so this sample votes H = z · (d + r) · hRel.
                _h[n++] = s.depthMeters * shifted * a.hRel;
            }
            used = n;
            if (n < _cfg.minClueSamples) return false;

            float median = Selection.Kth(_h, n, n / 2);
            for (int i = 0; i < n; i++) _h[i] = Mathf.Abs(_h[i] - median);
            float mad = Selection.Kth(_h, n, n / 2);
            // Samples that disagree (reflections, depth edges) make the median a coin toss
            // between surfaces; better no clue than a confident wrong one.
            if (!(median > 0f) || mad > _cfg.maxClueSpread * median) return false;
            if (median < FloorPlane.MinCameraHeight || median > FloorPlane.MaxCameraHeight) return false;

            float cameraY = img.cameraPose.position.y;
            clue = new HeightClue
            {
                time = now,
                heightMeters = median,
                floorY = cameraY - median,
                weight = Mathf.Min(n, _cfg.maxClueWeight),
                source = HeightClueSource.RawDepth,
            };
            return true;
        }
    }
}
