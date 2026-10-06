using System;
using System.Collections.Generic;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.Tests
{
    /// <summary>
    /// Ray-cast corridor renderer: exact metric depth plus a grey image, so detection code can be
    /// tested against ground truth instead of eyeballed on a phone. Axis-aligned quads only;
    /// that is all a corridor needs and keeps the truth easy to reason about.
    ///
    /// Layout: corridor along +Z, floor y = 0, walls at x = ±width/2, end wall at endWallZ
    /// facing -Z. An optional door recess sits in the LEFT wall; an optional standing person
    /// is a box in front of the RIGHT wall.
    /// </summary>
    sealed class SyntheticCorridor
    {
        public float width = 2f, height = 2.6f, startZ = -6f, endWallZ = 5.5f;
        public bool door;
        public float doorZ0 = 2f, doorZ1 = 3f, doorDepth = 0.3f, doorHeight = 2.1f;
        public float skirtingHeight = 0.1f;
        public bool person;
        public Vector3 personCentre = new Vector3(0.6f, 0f, 3f);
        public float personHalfX = 0.2f, personHalfZ = 0.15f, personHeight = 1.7f;
        public readonly List<float> groutLinesX = new List<float>();
        public float groutWidth = 0.02f;

        public const byte WallLuma = 200, FloorLuma = 110, SkirtingLuma = 60, CeilingLuma = 230,
            EndLuma = 180, PersonLuma = 90, GroutLuma = 70;

        public static readonly FloorPlane Floor = new FloorPlane(Vector3.zero, Vector3.up);

        public float LeftX => -width * 0.5f;
        public float RightX => width * 0.5f;

        public sealed class Rendered
        {
            public InverseDepthImage image;
            /// <summary>True metric Z-depth per pixel; NaN in padding or where nothing is hit.</summary>
            public float[] depth;
        }

        enum Kind { Wall, Floor, Ceiling, End, Person }

        /// <summary>Axis-aligned rectangle. (a, b) are the other two axes: x-plane → (y, z); y-plane → (x, z); z-plane → (x, y).</summary>
        struct Quad
        {
            public int axis;
            public float coord, aMin, aMax, bMin, bMax;
            public Kind kind;
            public bool hasDoorHole;
        }

        public static Pose Camera(float x = 0.2f, float h = 1.4f, float z = 0f, float pitchDown = 20f, float yaw = 0f, float roll = 0f) =>
            new Pose(new Vector3(x, h, z), Quaternion.Euler(pitchDown, yaw, roll));

        public Rendered Render(Pose pose, int size = 518, float vFovDeg = 67f, float contentAspect = 0.75f)
        {
            var img = new InverseDepthImage(size, size);
            float f = 0.5f * size / Mathf.Tan(vFovDeg * 0.5f * Mathf.Deg2Rad);
            float c = (size - 1) * 0.5f;
            img.intrinsics = new DepthIntrinsics { fx = f, fy = f, cx = c, cy = c, width = size, height = size };
            img.cameraPose = pose;
            // 0.75 of 518 rounds (to even) to 388 → x 65..452, the same rectangle as the device's portrait letterbox.
            int cw = Mathf.RoundToInt(size * contentAspect);
            img.content = new RectInt((size - cw) / 2, 0, cw, size);

            var depth = new float[size * size];
            Array.Fill(depth, float.NaN);
            var quads = Build();
            for (int v = 0; v < size; v++)
            for (int u = 0; u < size; u++)
            {
                if (!img.InContent(u, v)) continue;
                int i = v * size + u;
                // WorldRay has unit camera-Z, so the hit distance t IS the Z-depth.
                if (Cast(quads, pose.position, img.WorldRay(u, v), out float t, out byte luma))
                {
                    depth[i] = t;
                    img.luma[i] = luma;
                }
            }
            return new Rendered { image = img, depth = depth };
        }

        /// <summary>
        /// Network stand-in: d = (y − shift)/scale with y = 1/z (or z), plus an optional quadratic
        /// distortion and multiplicative Gaussian noise. Alignment should recover s = scale, t = shift.
        /// </summary>
        public static InverseDepthImage ToNetworkOutput(Rendered r, float scale = 2f, float shift = 0.05f,
            float relativeNoise = 0f, float quadratic = 0f, int seed = 1,
            DepthParameterisation parameterisation = DepthParameterisation.AffineInverseDepth)
        {
            var rng = new System.Random(seed);
            var values = r.image.values;
            for (int i = 0; i < values.Length; i++)
            {
                float z = r.depth[i];
                if (float.IsNaN(z)) { values[i] = float.NaN; continue; }
                float y = parameterisation == DepthParameterisation.AffineInverseDepth ? 1f / z : z;
                float d = (y - shift) / scale;
                d += quadratic * d * d;
                if (relativeNoise > 0f) d *= 1f + relativeNoise * Gaussian(rng);
                values[i] = d;
            }
            return r.image;
        }

        static float Gaussian(System.Random rng)
        {
            // Box–Muller; 1 - NextDouble() keeps log() away from 0.
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        List<Quad> Build()
        {
            float recess = door ? doorDepth : 0f;
            float xMin = LeftX - recess, xMax = RightX;
            var q = new List<Quad>
            {
                new Quad { axis = 1, coord = 0f, aMin = xMin, aMax = xMax, bMin = startZ, bMax = endWallZ, kind = Kind.Floor },
                new Quad { axis = 1, coord = height, aMin = xMin, aMax = xMax, bMin = startZ, bMax = endWallZ, kind = Kind.Ceiling },
                new Quad { axis = 0, coord = LeftX, aMin = 0f, aMax = height, bMin = startZ, bMax = endWallZ, kind = Kind.Wall, hasDoorHole = door },
                new Quad { axis = 0, coord = RightX, aMin = 0f, aMax = height, bMin = startZ, bMax = endWallZ, kind = Kind.Wall },
                new Quad { axis = 2, coord = endWallZ, aMin = xMin, aMax = xMax, bMin = 0f, bMax = height, kind = Kind.End },
                new Quad { axis = 2, coord = startZ, aMin = xMin, aMax = xMax, bMin = 0f, bMax = height, kind = Kind.End },
            };
            if (door)
            {
                float back = LeftX - doorDepth;
                q.Add(new Quad { axis = 0, coord = back, aMin = 0f, aMax = doorHeight, bMin = doorZ0, bMax = doorZ1, kind = Kind.Wall });
                q.Add(new Quad { axis = 2, coord = doorZ0, aMin = back, aMax = LeftX, bMin = 0f, bMax = doorHeight, kind = Kind.Wall });
                q.Add(new Quad { axis = 2, coord = doorZ1, aMin = back, aMax = LeftX, bMin = 0f, bMax = doorHeight, kind = Kind.Wall });
            }
            if (person)
            {
                Vector3 p = personCentre;
                float x0 = p.x - personHalfX, x1 = p.x + personHalfX, z0 = p.z - personHalfZ, z1 = p.z + personHalfZ;
                q.Add(new Quad { axis = 0, coord = x0, aMin = 0f, aMax = personHeight, bMin = z0, bMax = z1, kind = Kind.Person });
                q.Add(new Quad { axis = 0, coord = x1, aMin = 0f, aMax = personHeight, bMin = z0, bMax = z1, kind = Kind.Person });
                q.Add(new Quad { axis = 2, coord = z0, aMin = x0, aMax = x1, bMin = 0f, bMax = personHeight, kind = Kind.Person });
                q.Add(new Quad { axis = 2, coord = z1, aMin = x0, aMax = x1, bMin = 0f, bMax = personHeight, kind = Kind.Person });
                q.Add(new Quad { axis = 1, coord = personHeight, aMin = x0, aMax = x1, bMin = z0, bMax = z1, kind = Kind.Person });
            }
            return q;
        }

        bool Cast(List<Quad> quads, Vector3 o, Vector3 d, out float best, out byte luma)
        {
            best = float.PositiveInfinity;
            int hit = -1;
            Vector3 hitPoint = default;
            const float eps = 1e-5f;
            for (int i = 0; i < quads.Count; i++)
            {
                var q = quads[i];
                float dn = d[q.axis];
                if (Mathf.Abs(dn) < 1e-9f) continue;
                float t = (q.coord - o[q.axis]) / dn;
                if (t <= eps || t >= best) continue;
                Vector3 p = o + d * t;
                float a, b;
                switch (q.axis)
                {
                    case 0: a = p.y; b = p.z; break;
                    case 1: a = p.x; b = p.z; break;
                    default: a = p.x; b = p.y; break;
                }
                if (a < q.aMin - eps || a > q.aMax + eps || b < q.bMin - eps || b > q.bMax + eps) continue;
                if (q.hasDoorHole && p.z > doorZ0 && p.z < doorZ1 && p.y < doorHeight) continue;
                best = t;
                hit = i;
                hitPoint = p;
            }
            luma = 0;
            if (hit < 0) return false;
            luma = Shade(quads[hit].kind, hitPoint);
            return true;
        }

        byte Shade(Kind kind, Vector3 p)
        {
            switch (kind)
            {
                case Kind.Floor:
                    foreach (float gx in groutLinesX)
                        if (Mathf.Abs(p.x - gx) < groutWidth * 0.5f) return GroutLuma;
                    return FloorLuma;
                case Kind.Wall: return p.y < skirtingHeight ? SkirtingLuma : WallLuma;
                case Kind.End: return p.y < skirtingHeight ? SkirtingLuma : EndLuma;
                case Kind.Ceiling: return CeilingLuma;
                default: return PersonLuma;
            }
        }
    }
}
