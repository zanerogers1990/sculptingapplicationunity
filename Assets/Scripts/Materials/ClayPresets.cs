using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// One sculptor's-clay look for SculptPBR's clay mode (see ClayShade): a satin body colour, a
    /// wet highlight on top, tinted light bleeding past the shadow line, and a darker, richer
    /// colour in the recesses. Sizes are fractions of the model's size, like MetalFinishPreset's.
    public sealed class ClayPreset
    {
        public string Id;
        public string Name;
        public string Description;

        /// The clay on the flats, and what it deepens to in the recesses.
        public Color Color;
        public Color RecessColor;
        /// How far into the recesses that deeper colour reaches (0 = none, ~1 = well into the flats).
        public float RecessDepth;
        /// Light scattered inside the clay, showing just past the shadow line, and how much of it.
        public Color ScatterColor;
        public float Subsurface;
        /// The broad satin sheen of the waxy binder (PBR smoothness).
        public float Smoothness;
        /// The thin wet highlight on top of it: strength, and sharpness (Blinn-Phong exponent).
        public float Wetness;
        public float WetSharpness;
        /// How much lighter and glossier raised forms are where tools and fingers burnish them.
        public float RidgeBurnish;
        /// Broad tonal drift through the clay: strength, and patch size as a fraction of model size.
        public float Mottle;
        public float MottleSize;
        /// Fine grit in the surface: strength, and size as a fraction of model size.
        public float Grain;
        public float GrainSize;
    }

    public static class ClayPresets
    {
        private static readonly List<ClayPreset> _all = new List<ClayPreset>
        {
            new ClayPreset
            {
                Id = "grey_plasteline", Name = "Grey Plasteline",
                Description = "Mid-grey oil clay with a wet, greasy shine that catches every tool stroke.",
                Color = new Color(0.40f, 0.40f, 0.39f), RecessColor = new Color(0.13f, 0.13f, 0.13f), RecessDepth = 0.85f,
                ScatterColor = new Color(0.55f, 0.50f, 0.46f), Subsurface = 0.25f,
                Smoothness = 0.5f, Wetness = 1f, WetSharpness = 350f, RidgeBurnish = 0.35f,
                Mottle = 0.04f, MottleSize = 0.12f, Grain = 0.25f, GrainSize = 0.0015f,
            },
            new ClayPreset
            {
                Id = "charcoal", Name = "Charcoal Clay",
                Description = "Near-black clay: dark satin body, bright wet glints on the forms, lighter where it's worked.",
                Color = new Color(0.14f, 0.14f, 0.145f), RecessColor = new Color(0.035f, 0.035f, 0.038f), RecessDepth = 0.7f,
                ScatterColor = new Color(0.25f, 0.24f, 0.24f), Subsurface = 0.15f,
                Smoothness = 0.52f, Wetness = 1.2f, WetSharpness = 260f, RidgeBurnish = 0.5f,
                Mottle = 0.1f, MottleSize = 0.1f, Grain = 0.4f, GrainSize = 0.0015f,
            },
            new ClayPreset
            {
                Id = "warm_grey", Name = "Warm Grey",
                Description = "Soft taupe-grey clay, low shine, deep shadows in every fold.",
                Color = new Color(0.36f, 0.34f, 0.31f), RecessColor = new Color(0.09f, 0.08f, 0.07f), RecessDepth = 1f,
                ScatterColor = new Color(0.60f, 0.45f, 0.35f), Subsurface = 0.3f,
                Smoothness = 0.38f, Wetness = 0.4f, WetSharpness = 200f, RidgeBurnish = 0.3f,
                Mottle = 0.06f, MottleSize = 0.12f, Grain = 0.4f, GrainSize = 0.0015f,
            },
            new ClayPreset
            {
                Id = "terracotta", Name = "Terracotta",
                Description = "Orange-brown modelling clay (Chavant-style) with a satin sheen and a warm glow at the shadow edge.",
                Color = new Color(0.55f, 0.28f, 0.16f), RecessColor = new Color(0.25f, 0.09f, 0.045f), RecessDepth = 0.75f,
                ScatterColor = new Color(0.85f, 0.32f, 0.16f), Subsurface = 0.55f,
                Smoothness = 0.42f, Wetness = 0.45f, WetSharpness = 220f, RidgeBurnish = 0.3f,
                Mottle = 0.07f, MottleSize = 0.12f, Grain = 0.35f, GrainSize = 0.0015f,
            },
            new ClayPreset
            {
                Id = "red_oil", Name = "Red Oil Clay",
                Description = "Deep red-brown oil clay, waxy and warm, with rich dark-red recesses.",
                Color = new Color(0.44f, 0.20f, 0.12f), RecessColor = new Color(0.18f, 0.055f, 0.03f), RecessDepth = 0.8f,
                ScatterColor = new Color(0.80f, 0.28f, 0.14f), Subsurface = 0.5f,
                Smoothness = 0.48f, Wetness = 0.35f, WetSharpness = 180f, RidgeBurnish = 0.35f,
                Mottle = 0.06f, MottleSize = 0.12f, Grain = 0.3f, GrainSize = 0.0015f,
            },
        };

        public static IReadOnlyList<ClayPreset> All => _all;

        public static ClayPreset Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (ClayPreset preset in _all)
                if (preset.Id == id) return preset;
            return null;
        }

        /// A small painted swatch for the palette button: a lumpy ball with the recess colour in
        /// the dips, the scatter glow along the shadow edge and the wet highlight - where the shader
        /// would put them. Drawn on the CPU once; recognisable, not exact.
        public static Texture2D CreateThumbnail(ClayPreset preset, int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ClayThumb_" + preset.Id,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color[size * size];
            float radius = size * 0.46f;
            Vector2 centre = new Vector2(size * 0.5f, size * 0.5f);
            var lightDir = new Vector3(-0.55f, 0.5f, 0.67f).normalized;
            var viewDir = Vector3.forward;
            Vector3 half = (lightDir + viewDir).normalized;
            float h(float u, float v) => Mathf.PerlinNoise(u * 3.2f + 7f, v * 3.2f + 3f);

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = (new Vector2(x + 0.5f, y + 0.5f) - centre) / radius;
                float r2 = p.sqrMagnitude;
                if (r2 >= 1f) { pixels[y * size + x] = Color.clear; continue; }

                // Lumps: tilt the sphere normal by the gradient of a noise height field.
                const float e = 0.02f;
                float h0 = h(p.x, p.y);
                var n = new Vector3(p.x - (h(p.x + e, p.y) - h0) / e * 0.12f,
                                    p.y - (h(p.x, p.y + e) - h0) / e * 0.12f,
                                    Mathf.Sqrt(1f - r2)).normalized;
                float relief = (h0 - 0.5f) * 2f;

                float recess = Mathf.Clamp01(-relief * 1.6f) * Mathf.Clamp01(preset.RecessDepth);
                Color albedo = Color.Lerp(preset.Color, preset.RecessColor, recess);

                float ndl = Vector3.Dot(n, lightDir);
                float lambert = Mathf.Max(0f, ndl);
                float wrapped = Mathf.Clamp01((ndl + 0.5f) / 1.5f);
                Color c = albedo * (0.22f + 0.95f * lambert);
                c += preset.ScatterColor * (wrapped - lambert) * preset.Subsurface * 0.8f;
                float nh = Mathf.Max(0f, Vector3.Dot(n, half));
                c += Color.white * Mathf.Pow(nh, Mathf.Lerp(10f, 40f, preset.Smoothness)) * preset.Smoothness * 0.12f;
                c += Color.white * Mathf.Pow(nh, preset.WetSharpness * 0.4f) * preset.Wetness * 0.7f * (1f - recess);
                c.a = Mathf.Clamp01((1f - Mathf.Sqrt(r2)) * radius);
                pixels[y * size + x] = c;
            }

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
