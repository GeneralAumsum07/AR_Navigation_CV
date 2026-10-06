using UnityEngine;

namespace WallDistance.Core
{
    /// <summary>
    /// Maps the landscape camera sensor image into the square inference image: rotate by k
    /// quarter turns clockwise so the content is display-upright, then scale and letterbox to
    /// size². The network was trained on upright images, so feeding it a sideways corridor would
    /// cost accuracy. Every pixel, intrinsic and pose conversion lives here so the CPU resampler,
    /// the native input and the geometry can never disagree.
    ///
    /// Pixel convention: index = pixel-centre coordinate. Scaling is done on pixel EDGES
    /// ((p + 0.5)·scale − 0.5), which keeps both images' centres aligned.
    /// </summary>
    public readonly struct InferenceGeometry
    {
        public readonly int sensorWidth, sensorHeight, quarterTurns, size;
        public readonly float scale, offsetX, offsetY;
        public readonly RectInt content;

        InferenceGeometry(int w, int h, int k, int size)
        {
            sensorWidth = w;
            sensorHeight = h;
            quarterTurns = ((k % 4) + 4) % 4;
            this.size = size;
            int rw = quarterTurns % 2 == 0 ? w : h;
            int rh = quarterTurns % 2 == 0 ? h : w;
            scale = Mathf.Min((float)size / rw, (float)size / rh);
            float cw = rw * scale, ch = rh * scale;
            offsetX = (size - cw) * 0.5f;
            offsetY = (size - ch) * 0.5f;
            // Small epsilons absorb float error at exact fits (e.g. 518/640·640 = 518).
            int x0 = Mathf.CeilToInt(offsetX - 1e-3f), y0 = Mathf.CeilToInt(offsetY - 1e-3f);
            int x1 = Mathf.Min(size, Mathf.FloorToInt(offsetX + cw + 1e-3f));
            int y1 = Mathf.Min(size, Mathf.FloorToInt(offsetY + ch + 1e-3f));
            content = new RectInt(x0, y0, x1 - x0, y1 - y0);
        }

        public static InferenceGeometry Create(int sensorW, int sensorH, int quarterTurnsCW, int size = 518) =>
            new InferenceGeometry(sensorW, sensorH, quarterTurnsCW, size);

        public Vector2 SensorToInference(Vector2 s)
        {
            Vector2 r = Rotate(s);
            return new Vector2((r.x + 0.5f) * scale - 0.5f + offsetX, (r.y + 0.5f) * scale - 0.5f + offsetY);
        }

        public Vector2 InferenceToSensor(Vector2 p)
        {
            var r = new Vector2((p.x + 0.5f - offsetX) / scale - 0.5f, (p.y + 0.5f - offsetY) / scale - 0.5f);
            return Unrotate(r);
        }

        Vector2 Rotate(Vector2 s)
        {
            int W = sensorWidth, H = sensorHeight;
            switch (quarterTurns)
            {
                case 1: return new Vector2(H - 1 - s.y, s.x);          // 90° clockwise
                case 2: return new Vector2(W - 1 - s.x, H - 1 - s.y);
                case 3: return new Vector2(s.y, W - 1 - s.x);          // 90° counter-clockwise
                default: return s;
            }
        }

        Vector2 Unrotate(Vector2 r)
        {
            int W = sensorWidth, H = sensorHeight;
            switch (quarterTurns)
            {
                case 1: return new Vector2(r.y, H - 1 - r.x);
                case 2: return new Vector2(W - 1 - r.x, H - 1 - r.y);
                case 3: return new Vector2(W - 1 - r.y, r.x);
                default: return r;
            }
        }

        /// <summary>Intrinsics of the inference image. <paramref name="s"/> must be at sensor resolution.</summary>
        public DepthIntrinsics Intrinsics(DepthIntrinsics s)
        {
            int W = sensorWidth, H = sensorHeight;
            float fx, fy, cx, cy;
            switch (quarterTurns)
            {
                // Derived from Rotate(): e.g. k=1 gives u' = H-1-v, so u' = fy·y/z + (H-1-cy).
                case 1: fx = s.fy; fy = s.fx; cx = H - 1 - s.cy; cy = s.cx; break;
                case 2: fx = s.fx; fy = s.fy; cx = W - 1 - s.cx; cy = H - 1 - s.cy; break;
                case 3: fx = s.fy; fy = s.fx; cx = s.cy; cy = W - 1 - s.cx; break;
                default: fx = s.fx; fy = s.fy; cx = s.cx; cy = s.cy; break;
            }
            return new DepthIntrinsics
            {
                fx = fx * scale, fy = fy * scale,
                cx = (cx + 0.5f) * scale - 0.5f + offsetX,
                cy = (cy + 0.5f) * scale - 0.5f + offsetY,
                width = size, height = size,
            };
        }

        /// <summary>
        /// Rotating the image k quarter turns clockwise equals rotating the camera about its optical
        /// axis so that its new +X is the old +Y (for k=1). AngleAxis(90, forward) maps right→up.
        /// </summary>
        public Pose CameraPose(Pose sensorPose) =>
            new Pose(sensorPose.position, sensorPose.rotation * Quaternion.AngleAxis(90f * quarterTurns, Vector3.forward));

        /// <summary>
        /// Pick the turn whose camera "up" best matches the display's up. Uses the display (not
        /// gravity) so the choice stays stable when the phone points straight at the floor.
        /// </summary>
        public static int ChooseQuarterTurns(Quaternion sensorRotation, Vector3 displayUp)
        {
            int best = 0;
            float bestDot = float.NegativeInfinity;
            for (int k = 0; k < 4; k++)
            {
                Vector3 up = sensorRotation * (Quaternion.AngleAxis(90f * k, Vector3.forward) * Vector3.up);
                float d = Vector3.Dot(up, displayUp);
                if (d > bestDot) { bestDot = d; best = k; }
            }
            return best;
        }
    }
}
