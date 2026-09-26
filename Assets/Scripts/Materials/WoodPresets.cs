using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// One carved-wood look for SculptPBR's wood mode (see WoodShade): growth rings and fibre
    /// streaks around a log axis, a dark patina in the recesses, raised edges rubbed back to
    /// lighter wood, and a dull wax sheen. Sizes are fractions of the model's size, like
    /// ClayPreset's.
    public sealed class WoodPreset
    {
        public string Id;
        public string Name;
        public string Description;

        /// The pale, wide part of each growth ring, and the dark band that ends it.
        public Color EarlyColor;
        public Color LateColor;
        /// What raised edges are rubbed back to, and the grime that settles in the recesses.
        public Color WornColor;
        public Color RecessColor;
        /// Light scattered inside polished wood, showing warm just past the shadow line.
        public Color ScatterColor;
        public float Subsurface;

        /// Growth ring spacing, as a fraction of model size; how dark the latewood band reads;
        /// what fraction of each ring it takes; how far the rings wander (in ring widths).
        public float RingSize;
        public float RingContrast;
        public float Latewood;
        public float Wobble;
        /// Size of the wandering that bends the rings into flames, as a fraction of model size.
        public float FigureSize;
        /// Fibre streaks along the grain: strength, and width as a fraction of model size.
        public float Streaks;
        public float StreakSize;
        /// Open pores cut as dark dashes in the earlywood - oak and other ring-porous woods.
        public float Pores;
        /// How much darker end grain (looking down the log) takes the finish.
        public float EndGrain;

        /// How far the recess grime and the edge wear reach.
        public float Patina;
        public float EdgeWear;
        /// The satin sheen of the finish (PBR smoothness), and the broad wax highlight on top.
        public float Smoothness;
        public float Wax;
        public float WaxSharpness;
        /// Seasoning cracks along the grain: 0 none, 1 in every direction from the pith.
        public float Checks;
    }

    public static class WoodPresets
    {
        private static readonly List<WoodPreset> _all = new List<WoodPreset>
        {
            new WoodPreset
            {
                Id = "antique_fruitwood", Name = "Antique Fruitwood",
                Description = "Old waxed carving: warm red-brown, near-black grime in the recesses, edges rubbed back to glowing orange, a dull shine and the odd seasoning crack.",
                EarlyColor = new Color(0.36f, 0.16f, 0.07f), LateColor = new Color(0.25f, 0.10f, 0.042f),
                WornColor = new Color(0.76f, 0.38f, 0.14f), RecessColor = new Color(0.04f, 0.018f, 0.008f),
                ScatterColor = new Color(0.85f, 0.35f, 0.12f), Subsurface = 0.2f,
                RingSize = 0.012f, RingContrast = 0.4f, Latewood = 0.3f, Wobble = 1.4f, FigureSize = 0.25f,
                Streaks = 0.35f, StreakSize = 0.0012f, Pores = 0f, EndGrain = 0.3f,
                Patina = 1f, EdgeWear = 1.2f, Smoothness = 0.45f, Wax = 0.25f, WaxSharpness = 30f, Checks = 0.3f,
            },
            new WoodPreset
            {
                Id = "walnut", Name = "Black Walnut",
                Description = "Deep chocolate walnut with strong wavy figure and a soft oiled sheen.",
                EarlyColor = new Color(0.30f, 0.18f, 0.10f), LateColor = new Color(0.17f, 0.09f, 0.05f),
                WornColor = new Color(0.50f, 0.30f, 0.16f), RecessColor = new Color(0.04f, 0.025f, 0.015f),
                ScatterColor = new Color(0.60f, 0.30f, 0.15f), Subsurface = 0.15f,
                RingSize = 0.018f, RingContrast = 0.8f, Latewood = 0.35f, Wobble = 1.2f, FigureSize = 0.3f,
                Streaks = 0.5f, StreakSize = 0.0015f, Pores = 0.3f, EndGrain = 0.3f,
                Patina = 0.8f, EdgeWear = 0.8f, Smoothness = 0.45f, Wax = 0.22f, WaxSharpness = 35f, Checks = 0f,
            },
            new WoodPreset
            {
                Id = "oak", Name = "Golden Oak",
                Description = "Honey-coloured oak: bold rings, open pores, a matte oiled finish.",
                EarlyColor = new Color(0.62f, 0.42f, 0.22f), LateColor = new Color(0.45f, 0.28f, 0.13f),
                WornColor = new Color(0.75f, 0.55f, 0.32f), RecessColor = new Color(0.12f, 0.07f, 0.035f),
                ScatterColor = new Color(0.80f, 0.50f, 0.25f), Subsurface = 0.15f,
                RingSize = 0.025f, RingContrast = 0.9f, Latewood = 0.4f, Wobble = 1f, FigureSize = 0.3f,
                Streaks = 0.5f, StreakSize = 0.002f, Pores = 1f, EndGrain = 0.35f,
                Patina = 0.7f, EdgeWear = 0.6f, Smoothness = 0.4f, Wax = 0.2f, WaxSharpness = 30f, Checks = 0.15f,
            },
            new WoodPreset
            {
                Id = "mahogany", Name = "Mahogany",
                Description = "Red-brown mahogany, fine and even, with a French-polished glow.",
                EarlyColor = new Color(0.42f, 0.14f, 0.08f), LateColor = new Color(0.30f, 0.09f, 0.05f),
                WornColor = new Color(0.65f, 0.25f, 0.12f), RecessColor = new Color(0.07f, 0.02f, 0.012f),
                ScatterColor = new Color(0.80f, 0.20f, 0.10f), Subsurface = 0.35f,
                RingSize = 0.02f, RingContrast = 0.35f, Latewood = 0.35f, Wobble = 0.6f, FigureSize = 0.3f,
                Streaks = 0.5f, StreakSize = 0.0015f, Pores = 0.25f, EndGrain = 0.3f,
                Patina = 0.8f, EdgeWear = 0.7f, Smoothness = 0.6f, Wax = 0.4f, WaxSharpness = 70f, Checks = 0f,
            },
            new WoodPreset
            {
                Id = "limewood", Name = "Limewood",
                Description = "Pale, close-grained limewood (linden) - the classic carver's wood - left nearly bare.",
                EarlyColor = new Color(0.78f, 0.66f, 0.48f), LateColor = new Color(0.72f, 0.58f, 0.40f),
                WornColor = new Color(0.86f, 0.74f, 0.55f), RecessColor = new Color(0.35f, 0.25f, 0.15f),
                ScatterColor = new Color(0.90f, 0.70f, 0.45f), Subsurface = 0.3f,
                RingSize = 0.02f, RingContrast = 0.35f, Latewood = 0.3f, Wobble = 0.6f, FigureSize = 0.3f,
                Streaks = 0.2f, StreakSize = 0.0012f, Pores = 0f, EndGrain = 0.25f,
                Patina = 0.6f, EdgeWear = 0.4f, Smoothness = 0.3f, Wax = 0.1f, WaxSharpness = 25f, Checks = 0f,
            },
            new WoodPreset
            {
                Id = "ebonized", Name = "Ebonized",
                Description = "Blackened wood, waxed to a dull gleam, with warm brown showing through on the worn edges.",
                EarlyColor = new Color(0.07f, 0.05f, 0.04f), LateColor = new Color(0.04f, 0.03f, 0.025f),
                WornColor = new Color(0.28f, 0.15f, 0.07f), RecessColor = new Color(0.015f, 0.012f, 0.01f),
                ScatterColor = new Color(0.30f, 0.12f, 0.05f), Subsurface = 0.05f,
                RingSize = 0.02f, RingContrast = 0.5f, Latewood = 0.3f, Wobble = 0.8f, FigureSize = 0.3f,
                Streaks = 0.3f, StreakSize = 0.0015f, Pores = 0.2f, EndGrain = 0.2f,
                Patina = 0.6f, EdgeWear = 1f, Smoothness = 0.52f, Wax = 0.35f, WaxSharpness = 45f, Checks = 0f,
            },
        };

        public static IReadOnlyList<WoodPreset> All => _all;

        public static WoodPreset Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (WoodPreset preset in _all)
                if (preset.Id == id) return preset;
            return null;
        }

        /// A small painted swatch for the palette button: a lumpy ball cut from a vertical log,
        /// so its face shows the rings as flames, with grime in the dips, worn highlights on the
        /// bumps and a soft wax highlight. Drawn on the CPU once; recognisable, not exact.
        public static Texture2D CreateThumbnail(WoodPreset preset, int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "WoodThumb_" + preset.Id,
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
            // The swatch is a fixed size, so its rings are too - a little coarser than on a model,
            // or they'd shimmer at 64px.
            const float ringWidth = 0.13f;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = (new Vector2(x + 0.5f, y + 0.5f) - centre) / radius;
                float r2 = p.sqrMagnitude;
                if (r2 >= 1f) { pixels[y * size + x] = Color.clear; continue; }

                const float e = 0.02f;
                float h0 = h(p.x, p.y);
                var n = new Vector3(p.x - (h(p.x + e, p.y) - h0) / e * 0.1f,
                                    p.y - (h(p.x, p.y + e) - h0) / e * 0.1f,
                                    Mathf.Sqrt(1f - r2)).normalized;
                float relief = (h0 - 0.5f) * 2f;

                // Pith runs vertically behind the ball, so the front shows arcs.
                float z = Mathf.Sqrt(1f - r2);
                float rr = new Vector2(p.x + 0.25f, z + 0.9f).magnitude
                         + (Mathf.PerlinNoise(p.y * 1.5f + 2f, p.x * 2f) - 0.5f) * ringWidth * preset.Wobble * 3f;
                float t = Mathf.Repeat(rr / ringWidth, 1f);
                float late = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1f - preset.Latewood * 1.8f, 1f - preset.Latewood * 0.3f, t));
                Color wood = Color.Lerp(preset.EarlyColor, preset.LateColor, Mathf.Clamp01(late * preset.RingContrast));
                float streak = Mathf.PerlinNoise(p.x * 40f, p.y * 2f);
                wood *= 1f + (streak - 0.5f) * 0.7f * preset.Streaks;

                float wear = Mathf.Clamp01(relief * 1.8f) * Mathf.Clamp01(preset.EdgeWear);
                float woodLum = wood.grayscale / Mathf.Max(preset.EarlyColor.grayscale, 1e-3f);
                Color albedo = Color.Lerp(wood, preset.WornColor * woodLum, wear);
                float recess = Mathf.Clamp01(-relief * 1.6f) * Mathf.Clamp01(preset.Patina);
                albedo = Color.Lerp(albedo, preset.RecessColor, recess);

                float ndl = Vector3.Dot(n, lightDir);
                float lambert = Mathf.Max(0f, ndl);
                float wrapped = Mathf.Clamp01((ndl + 0.4f) / 1.4f);
                Color c = albedo * (0.22f + 0.95f * lambert) * (1f - 0.35f * recess);
                c += preset.ScatterColor * (wrapped - lambert) * preset.Subsurface * 0.8f;
                float nh = Mathf.Max(0f, Vector3.Dot(n, half));
                c += Color.white * Mathf.Pow(nh, Mathf.Lerp(10f, 40f, preset.Smoothness)) * preset.Smoothness * 0.12f;
                c += Color.white * Mathf.Pow(nh, preset.WaxSharpness) * preset.Wax * 0.5f * (1f - recess);
                c.a = Mathf.Clamp01((1f - Mathf.Sqrt(r2)) * radius);
                pixels[y * size + x] = c;
            }

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
