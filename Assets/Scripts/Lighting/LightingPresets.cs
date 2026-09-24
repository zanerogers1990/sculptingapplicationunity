using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// One light of a preset, described the way a photographer would: which side of the subject
    /// it comes from and how high, rather than as a transform. Azimuth is measured from the
    /// viewer - 0 is straight from the camera, +90 from screen-right, -90 from screen-left, 180
    /// from directly behind the subject. Elevation is degrees above the horizon.
    public readonly struct PresetLight
    {
        public readonly float Azimuth;
        public readonly float Elevation;
        public readonly Color Color;
        public readonly float Intensity;

        public PresetLight(float azimuth, float elevation, Color color, float intensity)
        {
            Azimuth = azimuth;
            Elevation = elevation;
            Color = color;
            Intensity = intensity;
        }
    }

    /// A complete, self-contained look: every light the rig will create plus the ambient it sits
    /// in. Nothing from the scene's own lighting survives underneath it - see
    /// LightingPresetController.
    ///
    /// Each preset carries all five slots; 3-point uses Key/Fill/Rim and 5-point adds Kicker/Top.
    /// The two extras are placed so they add to the silhouette and the top planes rather than the
    /// front face (a kicker from behind-side, a top light nearly overhead), which is why switching
    /// 3 -> 5 point does not need the front lights re-balanced to avoid blowing out.
    public sealed class LightingPreset
    {
        public readonly string Id;
        public readonly string Name;
        public readonly string Description;

        public readonly PresetLight Key, Fill, Rim, Kicker, Top;

        // Trilight ambient - sky from above, equator from the sides, ground from below. A
        // gradient rather than one flat colour so shadowed undersides still read darker than
        // shadowed tops, which is what keeps the form legible in the fill-less presets.
        public readonly Color AmbientSky, AmbientEquator, AmbientGround;

        /// How dark the key light's shadow gets (Light.shadowStrength).
        public readonly float ShadowStrength;
        /// RenderSettings.reflectionIntensity - dims the environment reflection on low-key looks
        /// so a glossy material does not light itself up from a sky the mood says isn't there.
        public readonly float Reflection;

        public LightingPreset(string id, string name, string description,
            PresetLight key, PresetLight fill, PresetLight rim, PresetLight kicker, PresetLight top,
            Color ambientSky, Color ambientEquator, Color ambientGround,
            float shadowStrength, float reflection)
        {
            Id = id; Name = name; Description = description;
            Key = key; Fill = fill; Rim = rim; Kicker = kicker; Top = top;
            AmbientSky = ambientSky; AmbientEquator = ambientEquator; AmbientGround = ambientGround;
            ShadowStrength = shadowStrength; Reflection = reflection;
        }
    }

    /// The built-in preset library.
    public static class LightingPresets
    {
        public const string DefaultId = "studio";

        public static readonly string[] SlotNames = { "Key", "Fill", "Rim", "Kicker", "Top" };

        private static PresetLight L(float az, float el, float r, float g, float b, float intensity)
            => new PresetLight(az, el, new Color(r, g, b), intensity);

        private static Color C(float r, float g, float b) => new Color(r, g, b);

        public static readonly IReadOnlyList<LightingPreset> All = new[]
        {
            new LightingPreset("studio", "Studio",
                "Balanced neutral studio light - the all-round default for judging form.",
                key:    L( -40f, 40f, 1.00f, 0.97f, 0.92f, 1.56f),
                fill:   L(  50f, 10f, 0.86f, 0.91f, 1.00f, 0.4f),
                rim:    L( 150f, 35f, 1.00f, 1.00f, 1.00f, 1.08f),
                kicker: L(-140f,  5f, 1.00f, 0.97f, 0.92f, 0.54f),
                top:    L(   0f, 85f, 1.00f, 1.00f, 1.00f, 0.3f),
                ambientSky: C(0.42f, 0.44f, 0.48f), ambientEquator: C(0.30f, 0.30f, 0.31f), ambientGround: C(0.16f, 0.15f, 0.14f),
                shadowStrength: 0.75f, reflection: 0.6f),

            new LightingPreset("softclay", "Soft Clay",
                "Low contrast and soft shadows - easy on the eyes for long detailing sessions.",
                key:    L( -30f, 45f, 1.00f, 0.98f, 0.95f, 1.14f),
                fill:   L(  60f, 20f, 0.95f, 0.97f, 1.00f, 0.66f),
                rim:    L( 170f, 40f, 1.00f, 1.00f, 1.00f, 0.6f),
                kicker: L(-120f, 10f, 1.00f, 0.98f, 0.95f, 0.36f),
                top:    L(   0f, 85f, 1.00f, 1.00f, 1.00f, 0.42f),
                ambientSky: C(0.42f, 0.43f, 0.45f), ambientEquator: C(0.33f, 0.33f, 0.34f), ambientGround: C(0.21f, 0.20f, 0.19f),
                shadowStrength: 0.35f, reflection: 0.4f),

            new LightingPreset("highkey", "High Key",
                "Bright, airy and nearly shadowless - clean product-shot presentation.",
                key:    L( -25f, 35f, 1.00f, 1.00f, 1.00f, 1.32f),
                fill:   L(  45f,  5f, 1.00f, 1.00f, 1.00f, 0.7f),
                rim:    L( 180f, 30f, 1.00f, 1.00f, 1.00f, 1.32f),
                kicker: L(-130f,  0f, 1.00f, 1.00f, 1.00f, 0.72f),
                top:    L(   0f, 80f, 1.00f, 1.00f, 1.00f, 0.6f),
                ambientSky: C(0.58f, 0.59f, 0.61f), ambientEquator: C(0.48f, 0.48f, 0.49f), ambientGround: C(0.36f, 0.35f, 0.34f),
                shadowStrength: 0.25f, reflection: 0.8f),

            new LightingPreset("vibrant", "Vibrant",
                "Saturated complementary colours - warm key, blue fill, magenta and teal edges.",
                key:    L( -45f, 35f, 1.00f, 0.82f, 0.55f, 1.56f),
                fill:   L(  55f,  5f, 0.30f, 0.55f, 1.00f, 0.84f),
                rim:    L( 145f, 25f, 1.00f, 0.25f, 0.65f, 1.8f),
                kicker: L(-140f,  0f, 0.15f, 0.95f, 0.85f, 1.32f),
                top:    L(   0f, 80f, 0.75f, 0.55f, 1.00f, 0.48f),
                ambientSky: C(0.35f, 0.30f, 0.50f), ambientEquator: C(0.25f, 0.22f, 0.32f), ambientGround: C(0.15f, 0.10f, 0.15f),
                shadowStrength: 0.7f, reflection: 0.7f),

            new LightingPreset("cinematic", "Cinematic",
                "Teal and orange film look - warm key against cool rim and shadows.",
                key:    L( -50f, 30f, 1.00f, 0.72f, 0.42f, 1.68f),
                fill:   L(  60f,  0f, 0.25f, 0.60f, 0.70f, 0.42f),
                rim:    L( 160f, 25f, 0.35f, 0.85f, 1.00f, 1.56f),
                kicker: L(-135f,  5f, 0.30f, 0.75f, 0.85f, 0.84f),
                top:    L(   0f, 80f, 0.90f, 0.80f, 0.70f, 0.18f),
                ambientSky: C(0.18f, 0.26f, 0.30f), ambientEquator: C(0.12f, 0.16f, 0.18f), ambientGround: C(0.06f, 0.06f, 0.06f),
                shadowStrength: 0.9f, reflection: 0.5f),

            new LightingPreset("golden", "Golden Hour",
                "Low warm sun raking across the form, with a cool sky fill.",
                key:    L( -70f, 12f, 1.00f, 0.62f, 0.30f, 1.8f),
                fill:   L(  60f, 25f, 0.45f, 0.50f, 0.85f, 0.48f),
                rim:    L( 140f, 15f, 1.00f, 0.78f, 0.45f, 1.44f),
                kicker: L(-150f,  5f, 1.00f, 0.55f, 0.35f, 0.72f),
                top:    L(   0f, 80f, 0.55f, 0.60f, 0.95f, 0.3f),
                ambientSky: C(0.40f, 0.42f, 0.62f), ambientEquator: C(0.35f, 0.27f, 0.30f), ambientGround: C(0.18f, 0.12f, 0.08f),
                shadowStrength: 0.85f, reflection: 0.6f),

            new LightingPreset("moody", "Moody",
                "Cool low-key light - deep shadows, a bright blue rim, very little fill.",
                key:    L( -60f, 45f, 0.85f, 0.90f, 1.00f, 1.56f),
                fill:   L(  60f,  0f, 0.35f, 0.45f, 0.70f, 0.11f),
                rim:    L( 155f, 30f, 0.55f, 0.75f, 1.00f, 1.44f),
                kicker: L(-140f,  0f, 0.45f, 0.55f, 0.85f, 0.48f),
                top:    L(   0f, 85f, 0.70f, 0.80f, 1.00f, 0.15f),
                ambientSky: C(0.10f, 0.12f, 0.18f), ambientEquator: C(0.05f, 0.06f, 0.08f), ambientGround: C(0.02f, 0.02f, 0.02f),
                shadowStrength: 1f, reflection: 0.25f),

            new LightingPreset("dramatic", "Dramatic",
                "Hard side key and a hot rim out of near-black - chiaroscuro.",
                key:    L( -85f, 30f, 1.00f, 0.95f, 0.88f, 2.04f),
                fill:   L(  80f, 10f, 0.80f, 0.85f, 1.00f, 0.05f),
                rim:    L( 165f, 35f, 1.00f, 1.00f, 1.00f, 1.8f),
                kicker: L(-150f,  0f, 1.00f, 0.90f, 0.80f, 0.6f),
                top:    L(   0f, 85f, 1.00f, 1.00f, 1.00f, 0.12f),
                ambientSky: C(0.06f, 0.06f, 0.07f), ambientEquator: C(0.03f, 0.03f, 0.03f), ambientGround: C(0.01f, 0.01f, 0.01f),
                shadowStrength: 1f, reflection: 0.2f),
        };

        /// The preset with `id`, or the default for an unknown/empty id - which is what a scene
        /// saved before presets existed (or naming one since removed) comes back as.
        public static LightingPreset Find(string id)
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i].Id == id) return All[i];
            for (int i = 0; i < All.Count; i++)
                if (All[i].Id == DefaultId) return All[i];
            return All[0];
        }
    }
}
