using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Stamp shapes the Clay brush can modulate its falloff with - a lightweight stand-in
    /// for ZBrush/Blender's alpha textures, generated procedurally at runtime rather than
    /// requiring imported image assets.
    public enum BrushAlphaType
    {
        SoftCircle,
        Noise,
        Bumps,
        Ridges,
        HardSquare
    }

    /// Generates and caches small grayscale stamp textures for BrushAlphaType, both as a
    /// fast-to-sample float array (used by SculptController every sculpt frame) and as a
    /// Texture2D (used by the UI for alpha-picker button thumbnails). Generation is lazy and
    /// memoized per type - nothing is built until a brush or the UI actually asks for it.
    public static class BrushAlphaLibrary
    {
        private const int Resolution = 64;
        private static readonly Dictionary<BrushAlphaType, AlphaData> _cache = new Dictionary<BrushAlphaType, AlphaData>();

        public readonly struct AlphaData
        {
            public readonly float[] Samples;
            public readonly int Size;
            public readonly Texture2D Preview;

            public AlphaData(float[] samples, int size, Texture2D preview)
            {
                Samples = samples;
                Size = size;
                Preview = preview;
            }
        }

        public static AlphaData Get(BrushAlphaType type)
        {
            if (_cache.TryGetValue(type, out AlphaData data)) return data;
            data = Generate(type);
            _cache[type] = data;
            return data;
        }

        /// Bilinear sample at normalized (u,v) in [0,1]; out-of-range values are clamped to
        /// the stamp's edge rather than wrapping, so alphaScale > 1 fades into "no stamp"
        /// at the brush radius instead of tiling.
        public static float Sample(in AlphaData data, float u, float v)
        {
            u = Mathf.Clamp01(u);
            v = Mathf.Clamp01(v);
            float fx = u * (data.Size - 1);
            float fy = v * (data.Size - 1);
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            int x1 = Mathf.Min(x0 + 1, data.Size - 1);
            int y1 = Mathf.Min(y0 + 1, data.Size - 1);
            float tx = fx - x0;
            float ty = fy - y0;

            float s00 = data.Samples[y0 * data.Size + x0];
            float s10 = data.Samples[y0 * data.Size + x1];
            float s01 = data.Samples[y1 * data.Size + x0];
            float s11 = data.Samples[y1 * data.Size + x1];
            float a = Mathf.Lerp(s00, s10, tx);
            float b = Mathf.Lerp(s01, s11, tx);
            return Mathf.Lerp(a, b, ty);
        }

        private static AlphaData Generate(BrushAlphaType type)
        {
            int size = Resolution;
            var samples = new float[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)(size - 1) * 2f - 1f;
                    float v = y / (float)(size - 1) * 2f - 1f;
                    samples[y * size + x] = Mathf.Clamp01(Evaluate(type, u, v));
                }
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "BrushAlpha_" + type,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var colors = new Color32[size * size];
            for (int i = 0; i < samples.Length; i++)
            {
                byte g = (byte)(samples[i] * 255f);
                colors[i] = new Color32(g, g, g, 255);
            }
            tex.SetPixels32(colors);
            tex.Apply(false, true);

            return new AlphaData(samples, size, tex);
        }

        private static float Evaluate(BrushAlphaType type, float u, float v)
        {
            float r = Mathf.Sqrt(u * u + v * v);
            float circle = Mathf.Clamp01(1f - r);
            circle = circle * circle * (3f - 2f * circle);

            switch (type)
            {
                case BrushAlphaType.SoftCircle:
                    return circle;

                case BrushAlphaType.HardSquare:
                {
                    float edge = Mathf.Max(Mathf.Abs(u), Mathf.Abs(v));
                    return edge < 0.9f ? 1f : Mathf.Clamp01((1f - edge) / 0.1f);
                }

                case BrushAlphaType.Noise:
                {
                    float n = FractalNoise(u * 4f + 10f, v * 4f + 10f, 4);
                    return circle * Mathf.Clamp01(0.35f + 0.65f * n);
                }

                case BrushAlphaType.Bumps:
                {
                    const float cell = 0.35f;
                    int cx = Mathf.FloorToInt(u / cell);
                    int cy = Mathf.FloorToInt(v / cell);
                    float best = 0f;
                    for (int oy = -1; oy <= 1; oy++)
                    {
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            int gx = cx + ox, gy = cy + oy;
                            Vector2 jitter = Hash2(gx, gy);
                            Vector2 center = new Vector2((gx + jitter.x) * cell, (gy + jitter.y) * cell);
                            float d = Vector2.Distance(new Vector2(u, v), center);
                            float bump = Mathf.Clamp01(1f - d / (cell * 0.6f));
                            bump = bump * bump * (3f - 2f * bump);
                            best = Mathf.Max(best, bump);
                        }
                    }
                    return circle * best;
                }

                case BrushAlphaType.Ridges:
                {
                    float stripe = 0.5f + 0.5f * Mathf.Sin(u * 18f);
                    return circle * Mathf.Lerp(0.4f, 1f, stripe);
                }
            }

            return circle;
        }

        private static float FractalNoise(float x, float y, int octaves)
        {
            float sum = 0f, amp = 0.5f, freq = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amp * Mathf.PerlinNoise(x * freq, y * freq);
                norm += amp;
                amp *= 0.5f;
                freq *= 2f;
            }
            return norm > 0f ? sum / norm : 0f;
        }

        private static Vector2 Hash2(int x, int y)
        {
            float h1 = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
            float h2 = Mathf.Sin(x * 269.5f + y * 183.3f) * 43758.5453f;
            return new Vector2(Frac(h1), Frac(h2));
        }

        private static float Frac(float v) => v - Mathf.Floor(v);
    }
}
