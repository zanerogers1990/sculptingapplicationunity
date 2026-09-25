using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// One aged-metal finish for SculptPBR's metal mode (see MetalShade): a bare metal, a coat
    /// over it that gathers in recesses and wears off raised edges, and a dark wash pooled in the
    /// deepest recesses. Sizes are fractions of the model's size, like LurePlasticPreset's.
    public sealed class MetalFinishPreset
    {
        public string Id;
        public string Name;
        public string Description;

        /// The metal itself, showing where the coat has worn away.
        public Color MetalColor;
        public float MetalSmoothness;
        /// Below 1 for a satin metal paint or dry-brush: a fully metallic surface is dark except
        /// for what it reflects, so edges facing the darker half of the room would go black.
        public float MetalMetallic;
        /// The coat, mottled between two colours in broad patches (A on top, B toward recesses).
        public Color CoatA, CoatB;
        public float CoatMetallic;
        public float CoatSmoothness;
        /// Colour pooled in the recesses, and how strongly.
        public Color WashColor;
        public float Wash;
        /// How much of the surface the coat covers (0 = bare metal, 1 = nearly all of it).
        public float Coverage;
        /// How far raised edges are worn back to bare metal.
        public float EdgeWear;
        /// Width of the coat's patches, as a fraction of model size, and how hard their edges are.
        public float PatchSize;
        public float PatchContrast;
        /// 0..1: how much the coat breaks into random patches (1, rust) versus following the
        /// carving alone (low, a blackened antique).
        public float Patchiness;
        /// How strongly recesses gather the coat and raised areas shed it.
        public float FollowsRecesses;
        /// Pitting/grain in the coat: strength, and size as a fraction of model size.
        public float Grain;
        public float GrainSize;
    }

    public static class MetalFinishPresets
    {
        private static readonly List<MetalFinishPreset> _all = new List<MetalFinishPreset>
        {
            new MetalFinishPreset
            {
                Id = "rust", Name = "Rust",
                Description = "Ochre-orange rust over dark iron, worn back to grey metal on the high points.",
                MetalColor = new Color(0.40f, 0.41f, 0.44f), MetalSmoothness = 0.42f, MetalMetallic = 0.8f,
                CoatA = new Color(0.70f, 0.44f, 0.14f), CoatB = new Color(0.38f, 0.18f, 0.06f),
                CoatMetallic = 0f, CoatSmoothness = 0.15f,
                WashColor = new Color(0.10f, 0.055f, 0.03f), Wash = 0.65f,
                Coverage = 0.62f, EdgeWear = 0.9f,
                PatchSize = 0.09f, PatchContrast = 4f, Patchiness = 1f, FollowsRecesses = 0.5f,
                Grain = 0.55f, GrainSize = 0.004f,
            },
            new MetalFinishPreset
            {
                Id = "patina", Name = "Patina",
                Description = "Chalky blue-green verdigris over bronze, dark in the grooves, copper on the edges.",
                MetalColor = new Color(0.70f, 0.45f, 0.25f), MetalSmoothness = 0.55f, MetalMetallic = 0.85f,
                CoatA = new Color(0.36f, 0.64f, 0.58f), CoatB = new Color(0.17f, 0.38f, 0.36f),
                CoatMetallic = 0f, CoatSmoothness = 0.12f,
                WashColor = new Color(0.11f, 0.07f, 0.045f), Wash = 0.8f,
                Coverage = 0.8f, EdgeWear = 0.6f,
                PatchSize = 0.07f, PatchContrast = 3f, Patchiness = 0.8f, FollowsRecesses = 0.6f,
                Grain = 0.35f, GrainSize = 0.005f,
            },
            new MetalFinishPreset
            {
                Id = "red_wash", Name = "Red Wash",
                Description = "Deep oxblood red with a black wash in the recesses and a coppery sheen on the highs.",
                MetalColor = new Color(0.74f, 0.30f, 0.20f), MetalSmoothness = 0.6f, MetalMetallic = 0.6f,
                CoatA = new Color(0.40f, 0.07f, 0.045f), CoatB = new Color(0.26f, 0.045f, 0.03f),
                CoatMetallic = 0.35f, CoatSmoothness = 0.55f,
                WashColor = new Color(0.03f, 0.015f, 0.012f), Wash = 0.9f,
                Coverage = 0.9f, EdgeWear = 0.55f,
                PatchSize = 0.1f, PatchContrast = 2f, Patchiness = 0.5f, FollowsRecesses = 0.8f,
                Grain = 0.1f, GrainSize = 0.004f,
            },
            new MetalFinishPreset
            {
                Id = "antique_silver", Name = "Antique Silver",
                Description = "Blackened base with bright silver dry-brushed onto every raised detail.",
                MetalColor = new Color(0.86f, 0.86f, 0.88f), MetalSmoothness = 0.55f, MetalMetallic = 0.8f,
                CoatA = new Color(0.06f, 0.06f, 0.065f), CoatB = new Color(0.035f, 0.035f, 0.04f),
                CoatMetallic = 0.6f, CoatSmoothness = 0.45f,
                WashColor = new Color(0.015f, 0.015f, 0.018f), Wash = 0.9f,
                Coverage = 0.5f, EdgeWear = 1.2f,
                PatchSize = 0.08f, PatchContrast = 2f, Patchiness = 0.25f, FollowsRecesses = 2f,
                Grain = 0.05f, GrainSize = 0.004f,
            },
            new MetalFinishPreset
            {
                Id = "antique_gold", Name = "Antique Gold",
                Description = "Polished brass-gold with black worked into the recesses.",
                MetalColor = new Color(0.86f, 0.65f, 0.30f), MetalSmoothness = 0.6f, MetalMetallic = 0.85f,
                CoatA = new Color(0.08f, 0.06f, 0.035f), CoatB = new Color(0.04f, 0.03f, 0.02f),
                CoatMetallic = 0.5f, CoatSmoothness = 0.5f,
                WashColor = new Color(0.02f, 0.015f, 0.01f), Wash = 0.9f,
                Coverage = 0.25f, EdgeWear = 1f,
                PatchSize = 0.08f, PatchContrast = 2f, Patchiness = 0.2f, FollowsRecesses = 2f,
                Grain = 0.05f, GrainSize = 0.004f,
            },
        };

        public static IReadOnlyList<MetalFinishPreset> All => _all;

        public static MetalFinishPreset Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (MetalFinishPreset preset in _all)
                if (preset.Id == id) return preset;
            return null;
        }

        /// A small painted swatch for the palette button: a ball carved with rings, the coat in
        /// the flats, wash in the grooves and bare metal on the crests - the finish's three layers
        /// where the shader would put them. Drawn on the CPU once; recognisable, not exact.
        public static Texture2D CreateThumbnail(MetalFinishPreset preset, int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "MetalThumb_" + preset.Id,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color[size * size];
            float radius = size * 0.46f;
            Vector2 centre = new Vector2(size * 0.5f, size * 0.5f);
            var lightDir = new Vector3(-0.4f, 0.55f, 0.73f).normalized;
            var viewDir = Vector3.forward;
            Vector3 half = (lightDir + viewDir).normalized;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = (new Vector2(x + 0.5f, y + 0.5f) - centre) / radius;
                float r2 = p.sqrMagnitude;
                if (r2 >= 1f) { pixels[y * size + x] = Color.clear; continue; }
                var n = new Vector3(p.x, p.y, Mathf.Sqrt(1f - r2));

                // Rings wobbling around the ball: +1 on a crest, -1 in a groove.
                float relief = Mathf.Sin(Mathf.Sqrt(r2) * 16f + Mathf.Sin(Mathf.Atan2(p.y, p.x) * 3f) * 1.2f);
                float patch = Mathf.PerlinNoise(x * 0.09f + preset.Id.Length, y * 0.09f);
                float wear = Mathf.Clamp01((relief - 0.55f) * 3f) * Mathf.Clamp01(preset.EdgeWear);
                float patchLevel = Mathf.Lerp(0.5f, patch, preset.Patchiness);
                float coat = Mathf.Clamp01((preset.Coverage - relief * preset.FollowsRecesses - patchLevel) * preset.PatchContrast + 0.5f) * (1f - wear);
                float wash = Mathf.Clamp01((-relief - 0.3f) * 2f) * preset.Wash;

                Color coatColor = Color.Lerp(preset.CoatA, preset.CoatB, Mathf.PerlinNoise(x * 0.2f, y * 0.2f + 5f));
                Color albedo = Color.Lerp(preset.MetalColor, coatColor, coat);
                albedo = Color.Lerp(albedo, preset.WashColor, wash);
                float metallic = Mathf.Lerp(preset.MetalMetallic, preset.CoatMetallic, coat) * (1f - wash);
                float smooth = Mathf.Lerp(preset.MetalSmoothness, preset.CoatSmoothness, coat);

                float lambert = Mathf.Max(0f, Vector3.Dot(n, lightDir));
                // Metal is dark except for what it reflects; a crude sky-over-ground stands in.
                Color reflection = Color.Lerp(new Color(0.25f, 0.24f, 0.22f), new Color(0.95f, 0.95f, 0.97f), n.y * 0.5f + 0.5f);
                Color diffuse = albedo * (0.25f + 0.85f * lambert);
                Color metal = albedo * reflection * 1.1f;
                Color c = Color.Lerp(diffuse, metal, metallic);
                float spec = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(n, half)), Mathf.Lerp(8f, 90f, smooth));
                c += Color.Lerp(Color.white, albedo, metallic) * spec * smooth;
                c.a = Mathf.Clamp01((1f - Mathf.Sqrt(r2)) * radius);
                pixels[y * size + x] = c;
            }

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
