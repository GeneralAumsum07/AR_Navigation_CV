using System.Collections.Generic;
using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Turns confident ARCore raw-depth pixels into metric samples in the inference image: the
    /// no-floor fallback for alignment (spec §6). Samples are re-projected through the inference
    /// camera, so their depth is that camera's Z-depth, the quantity the aligner fits.
    /// </summary>
    public static class MetricSampleCollector
    {
        public static int Collect(DepthFrame raw, InverseDepthImage img, byte minConfidence, double now,
            double maxAgeSeconds, int stride, List<MetricSample> output)
        {
            output.Clear();
            if (raw == null || !raw.IsUsable || now - raw.timestamp > maxAgeSeconds) return 0;
            // Without a confidence image there is no way to tell a measured return from a guess,
            // and on plain walls most raw values are guesses (spec §2). Refuse rather than trust them.
            if (raw.confidence == null || raw.confidence.Length < raw.width * raw.height) return 0;
            int step = Mathf.Max(1, stride);
            for (int v = 0; v < raw.height; v += step)
            for (int u = 0; u < raw.width; u += step)
            {
                if (raw.ConfidenceAt(u, v) < minConfidence) continue;
                float z = raw.DepthAt(u, v);
                if (float.IsNaN(z)) continue;
                Vector3 world = raw.Unproject(u, v, z);
                if (!img.TryProject(world, out float pu, out float pv, out float pz)) continue;
                if (!img.InContent(Mathf.RoundToInt(pu), Mathf.RoundToInt(pv))) continue;
                output.Add(new MetricSample { pixel = new Vector2(pu, pv), depthMeters = pz });
            }
            return output.Count;
        }
    }
}
