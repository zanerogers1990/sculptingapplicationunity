using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// One soft-plastic lure colourway for SculptPBR's lure plastic mode: the plastic's colour at
    /// its thinnest and thickest, and the glitter cast into it. Sizes are fractions of the model's
    /// size (see SculptMaterialController.LureReferenceSize), so a preset looks the same on a
    /// 2-unit sphere and a 40-unit import.
    public sealed class LurePlasticPreset
    {
        public string Id;
        public string Name;
        public string Description;

        /// Plastic seen through a thin edge (a claw tip) - lighter and more saturated.
        public Color ThinColor;
        /// Plastic seen through the thick body.
        public Color ThickColor;
        /// Thickness, as a fraction of model size, over which thin colour gives way to thick.
        public float Depth;
        /// How strongly light glows through thin parts.
        public float Transmission;

        /// Main glitter colours; alpha is each colour's share of the flakes.
        public Color FlakeA, FlakeB, FlakeC;
        /// Fraction of lattice cells holding a flake.
        public float FlakeDensity;
        /// Mean flake width as a fraction of model size.
        public float FlakeSize;
        /// 0 = hexagonal glitter, 1 = square.
        public float FlakeShape;
        /// Fine "pepper" flake, one colour.
        public Color MicroColor;
        public float MicroDensity;
        public float MicroSize;
    }

    public static class LurePlasticPresets
    {
        /// Tilt of flakes away from the skin. Molding lays glitter mostly along the surface, which
        /// is why the photos show mostly full-face hexes and only the odd sliver.
        public const float FlakeTilt = 0.35f;

        private static readonly List<LurePlasticPreset> _all = new List<LurePlasticPreset>
        {
            new LurePlasticPreset
            {
                Id = "black_blue_flake", Name = "Black & Blue Flake",
                Description = "Smoky black plastic packed with bright blue hex glitter.",
                ThinColor = new Color(0.13f, 0.15f, 0.22f), ThickColor = new Color(0.035f, 0.04f, 0.055f),
                Depth = 0.05f, Transmission = 0.35f,
                FlakeA = new Color(0.16f, 0.52f, 1.00f, 0.6f),
                FlakeB = new Color(0.06f, 0.32f, 0.95f, 0.4f),
                FlakeC = new Color(0f, 0f, 0f, 0f),
                FlakeDensity = 0.55f, FlakeSize = 0.014f, FlakeShape = 0f,
                MicroColor = new Color(0.10f, 0.42f, 0.95f), MicroDensity = 0.3f, MicroSize = 0.0045f,
            },
            new LurePlasticPreset
            {
                Id = "pumpkin_flake", Name = "Pumpkin Flake",
                Description = "Green pumpkin: olive body, amber claws, green and purple glitter.",
                ThinColor = new Color(0.80f, 0.60f, 0.16f), ThickColor = new Color(0.33f, 0.30f, 0.14f),
                Depth = 0.05f, Transmission = 0.9f,
                FlakeA = new Color(0.45f, 0.95f, 0.30f, 0.55f),
                FlakeB = new Color(0.55f, 0.25f, 0.95f, 0.30f),
                FlakeC = new Color(0.95f, 0.30f, 0.55f, 0.15f),
                FlakeDensity = 0.5f, FlakeSize = 0.012f, FlakeShape = 0f,
                MicroColor = new Color(0.95f, 0.82f, 0.50f), MicroDensity = 0.15f, MicroSize = 0.004f,
            },
            new LurePlasticPreset
            {
                Id = "blue_flake", Name = "Blue Flake",
                Description = "Translucent purple-blue plastic with teal glitter.",
                ThinColor = new Color(0.52f, 0.52f, 1.00f), ThickColor = new Color(0.20f, 0.10f, 0.64f),
                Depth = 0.07f, Transmission = 1.0f,
                FlakeA = new Color(0.10f, 0.85f, 0.75f, 0.7f),
                FlakeB = new Color(0.05f, 0.55f, 0.50f, 0.3f),
                FlakeC = new Color(0f, 0f, 0f, 0f),
                FlakeDensity = 0.55f, FlakeSize = 0.012f, FlakeShape = 0f,
                MicroColor = new Color(0.08f, 0.06f, 0.30f), MicroDensity = 0.2f, MicroSize = 0.004f,
            },
            new LurePlasticPreset
            {
                Id = "amber_flake", Name = "Amber Flake",
                Description = "Amber plastic with black square flake and fine red pepper.",
                ThinColor = new Color(0.88f, 0.74f, 0.22f), ThickColor = new Color(0.28f, 0.24f, 0.10f),
                Depth = 0.05f, Transmission = 0.9f,
                FlakeA = new Color(0.02f, 0.02f, 0.02f, 1f),
                FlakeB = new Color(0f, 0f, 0f, 0f),
                FlakeC = new Color(0f, 0f, 0f, 0f),
                FlakeDensity = 0.4f, FlakeSize = 0.013f, FlakeShape = 1f,
                MicroColor = new Color(0.85f, 0.16f, 0.08f), MicroDensity = 0.35f, MicroSize = 0.0035f,
            },
        };

        public static IReadOnlyList<LurePlasticPreset> All => _all;

        public static LurePlasticPreset Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (LurePlasticPreset preset in _all)
                if (preset.Id == id) return preset;
            return null;
        }

        /// A small painted swatch for the palette button: a ball that's thin-coloured at its rim
        /// and thick-coloured in the middle, sprinkled with the preset's flakes. Drawn on the CPU
        /// once - it only has to be recognisable, not match the shader pixel for pixel.
        public static Texture2D CreateThumbnail(LurePlasticPreset preset, int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "LureThumb_" + preset.Id,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color[size * size];
            float radius = size * 0.46f;
            Vector2 centre = new Vector2(size * 0.5f, size * 0.5f);
            var lightDir = new Vector3(-0.4f, 0.55f, 0.73f).normalized;

            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = (new Vector2(x + 0.5f, y + 0.5f) - centre) / radius;
                float r2 = p.sqrMagnitude;
                if (r2 >= 1f) { pixels[y * size + x] = Color.clear; continue; }
                float z = Mathf.Sqrt(1f - r2);
                float through = Mathf.Exp(-z * 3f);
                Color body = Color.Lerp(preset.ThickColor, preset.ThinColor, through);
                float lambert = Mathf.Max(0f, Vector3.Dot(new Vector3(p.x, p.y, z), lightDir));
                Color c = body * (0.45f + 0.75f * lambert);
                float spec = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(new Vector3(p.x, p.y, z), lightDir)), 40f);
                c += Color.white * spec * 0.6f;
                c.a = Mathf.Clamp01((1f - Mathf.Sqrt(r2)) * radius);
                pixels[y * size + x] = c;
            }

            var rng = new System.Random(preset.Id.GetHashCode());
            float totalWeight = Mathf.Max(preset.FlakeA.a + preset.FlakeB.a + preset.FlakeC.a, 1e-4f);
            int flakes = Mathf.RoundToInt(90 * preset.FlakeDensity);
            for (int i = 0; i < flakes; i++)
            {
                float pick = (float)rng.NextDouble() * totalWeight;
                Color col = pick < preset.FlakeA.a ? preset.FlakeA
                          : pick < preset.FlakeA.a + preset.FlakeB.a ? preset.FlakeB : preset.FlakeC;
                Stamp(pixels, size, centre, radius, rng, col, 1);
            }
            int micro = Mathf.RoundToInt(160 * preset.MicroDensity);
            for (int i = 0; i < micro; i++)
                Stamp(pixels, size, centre, radius, rng, preset.MicroColor, 0);

            tex.SetPixels(pixels);
            tex.Apply(false, true);
            return tex;
        }

        private static void Stamp(Color[] pixels, int size, Vector2 centre, float radius, System.Random rng,
                                  Color color, int halfWidth)
        {
            int cx = Mathf.RoundToInt(centre.x + ((float)rng.NextDouble() * 2f - 1f) * radius);
            int cy = Mathf.RoundToInt(centre.y + ((float)rng.NextDouble() * 2f - 1f) * radius);
            for (int y = cy - halfWidth; y <= cy + halfWidth; y++)
            for (int x = cx - halfWidth; x <= cx + halfWidth; x++)
            {
                if (x < 0 || y < 0 || x >= size || y >= size) continue;
                int i = y * size + x;
                if (pixels[i].a < 0.99f) continue;
                pixels[i] = new Color(color.r, color.g, color.b, 1f);
            }
        }
    }
}
