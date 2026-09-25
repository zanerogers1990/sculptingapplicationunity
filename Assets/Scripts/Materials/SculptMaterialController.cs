using UnityEngine;

namespace Sculpting
{
    /// Assigns a runtime instance of the Custom/SculptPBR shader to the sculpted mesh and
    /// exposes its parameters (base PBR sliders, a procedural normal-detail strength, matcap,
    /// and the screen-space cavity) so they're editable live from the Material UI panel
    /// instead of only through the Inspector.
    public class SculptMaterialController : MonoBehaviour
    {
        [SerializeField] private Color baseColor = new Color(0.65f, 0.65f, 0.68f);
        [SerializeField, Range(0f, 1f)] private float metallic = 0f;
        [SerializeField, Range(0f, 1f)] private float smoothness = 0.4f;
        [SerializeField, Range(0f, 2f)] private float normalStrength = 0f;
        [SerializeField, Range(1f, 300f)] private float normalNoiseScale = 60f;
        // Blender-style Shade Smooth (false, default) / Shade Flat (true) toggle - see
        // SculptPBR.shader's _FlatShading remarks for how this is done without touching
        // SculptableMesh's shared-vertex data model.
        [SerializeField] private bool flatShading = false;

        // Blender-style screen-space cavity (ScreenCavityFeature): brightens ridges and darkens
        // valleys by how fast the surface turns on screen. Ridge/Valley are Workbench's own
        // factors, same 0-2 range and 1.0 defaults. Named screenCavityEnabled rather than
        // reusing the old per-vertex tint's cavityEnabled, so a scene that serialized that one
        // (off) doesn't switch off a different feature.
        [SerializeField] private bool screenCavityEnabled = true;
        [SerializeField, Range(0f, 2f)] private float cavityRidge = 1f;
        [SerializeField, Range(0f, 2f)] private float cavityValley = 1f;

        // Matcap shading (see MatcapLibrary for where the images come from). Stored by NAME,
        // not by texture reference: names are what a .sculpt file can carry between machines,
        // and the texture itself is loaded on demand. Defaults to ON with the Ben Simonds pack's
        // first (brown clay) matcap rather than plain PBR - that's the look a fresh session
        // should open on; ResolveMatcap() in Awake() falls back to plain PBR on its own if this
        // name ever goes missing from the library (a stripped build, a user who deleted the
        // pack), so this default can't leave a new session looking broken.
        [SerializeField] private bool matcapEnabled = true;
        [SerializeField] private string matcapName = "MatCap_BS1";
        [SerializeField, Range(0f, 3f)] private float matcapIntensity = 1f;
        [SerializeField, Range(0f, 1f)] private float matcapTintStrength = 0f;

        // Lure plastic (see LurePlasticPresets / SculptPBR's PlasticShade): translucent soft
        // plastic with glitter, replacing Base Color/Metallic/Smoothness while it's on. Off by
        // default, and shown only when matcap is off - a matcap replaces lighting entirely. The
        // sliders are adjustments ON TOP of whichever preset is picked (1 = as the preset has
        // it), so switching preset keeps the user's taste for size/amount/sparkle.
        [SerializeField] private bool lureEnabled = false;
        [SerializeField] private string lurePresetId = "black_blue_flake";
        [SerializeField, Range(0.25f, 3f)] private float lureFlakeSize = 1f;
        [SerializeField, Range(0f, 2f)] private float lureFlakeAmount = 1f;
        [SerializeField, Range(0f, 3f)] private float lureSparkle = 1f;
        [SerializeField, Range(0.25f, 3f)] private float lureTranslucency = 1f;
        [SerializeField, Range(0f, 1f)] private float lureGloss = 0.8f;
        // World size the preset's fractions are measured against - the largest sculpt object's
        // extent when a preset was picked. Frozen rather than re-measured every frame: the flake
        // lattice is scaled by it, so a live value would make the glitter crawl as the model is
        // sculpted. Serialized (hidden) so a mid-Play recompile keeps the same flakes.
        [SerializeField, HideInInspector] private float lureReferenceSize;

        private Material _material;
        [System.NonSerialized] private Texture2D _matcapTexture;

        public Color BaseColor { get => baseColor; set { baseColor = value; Push(); } }
        public float Metallic { get => metallic; set { metallic = Mathf.Clamp01(value); Push(); } }
        public float Smoothness { get => smoothness; set { smoothness = Mathf.Clamp01(value); Push(); } }
        public float NormalStrength { get => normalStrength; set { normalStrength = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float NormalNoiseScale { get => normalNoiseScale; set { normalNoiseScale = Mathf.Clamp(value, 1f, 300f); Push(); } }
        public bool FlatShading { get => flatShading; set { flatShading = value; Push(); } }
        public bool CavityEnabled { get => screenCavityEnabled; set { screenCavityEnabled = value; Push(); } }
        public float CavityRidge { get => cavityRidge; set { cavityRidge = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float CavityValley { get => cavityValley; set { cavityValley = Mathf.Clamp(value, 0f, 2f); Push(); } }

        /// Whether matcap shading replaces the lit PBR result. Turning it on with no matcap
        /// picked selects the first one in the library rather than showing a flat white sphere -
        /// the toggle is the user asking to SEE a matcap, and an empty texture slot answers that
        /// with something that looks broken.
        public bool MatcapEnabled
        {
            get => matcapEnabled;
            set
            {
                matcapEnabled = value;
                if (matcapEnabled && _matcapTexture == null)
                {
                    var entries = MatcapLibrary.Entries;
                    if (entries.Count > 0) { matcapName = entries[0].Name; ResolveMatcap(); }
                    // Nothing in the folder at all - refuse rather than render flat white.
                    else matcapEnabled = false;
                }
                Push();
            }
        }

        /// File name (no extension) of the selected matcap. Setting it to a name the library
        /// doesn't have clears the selection instead of failing - that's the "saved scene refers
        /// to a matcap this machine doesn't have" case, and it should degrade to plain PBR
        /// shading rather than to a broken-looking surface.
        public string MatcapName
        {
            get => matcapName;
            set { matcapName = value ?? string.Empty; ResolveMatcap(); Push(); }
        }

        /// Selects a specific library entry directly, bypassing MatcapLibrary.Find's name-only
        /// lookup - for callers (a palette click, an import) that already hold the exact Entry.
        /// Going through the MatcapName setter instead would re-resolve by name and, if another
        /// entry elsewhere shares that name, could silently apply a different image than the one
        /// actually picked. matcapName is still recorded from the entry for the .sculpt format
        /// and for a plain-name re-resolution to survive things like a mid-Play domain reload.
        public void SetMatcap(MatcapLibrary.Entry entry)
        {
            matcapName = entry != null ? entry.Name : string.Empty;
            _matcapTexture = entry != null ? MatcapLibrary.GetFull(entry) : null;
            matcapEnabled = _matcapTexture != null;
            Push();
        }

        public bool LureEnabled
        {
            get => lureEnabled;
            set
            {
                lureEnabled = value && LurePlasticPresets.Find(lurePresetId) != null;
                if (lureEnabled && lureReferenceSize <= 0f) lureReferenceSize = MeasureReferenceSize();
                Push();
            }
        }

        /// Id of the selected LurePlasticPreset. An id this build doesn't know (a file from a
        /// newer version) switches lure plastic off rather than guessing at a substitute.
        public string LurePresetId
        {
            get => lurePresetId;
            set
            {
                lurePresetId = value ?? string.Empty;
                if (LurePlasticPresets.Find(lurePresetId) == null) lureEnabled = false;
                Push();
            }
        }

        /// Picks a preset and turns lure plastic on - a palette click. Re-measures the model,
        /// since picking a preset is the moment the user is looking at what they want it on.
        public void SelectLurePreset(string id)
        {
            if (LurePlasticPresets.Find(id) == null) return;
            lurePresetId = id;
            lureEnabled = true;
            lureReferenceSize = MeasureReferenceSize();
            Push();
        }

        public float LureFlakeSize { get => lureFlakeSize; set { lureFlakeSize = Mathf.Clamp(value, 0.25f, 3f); Push(); } }
        public float LureFlakeAmount { get => lureFlakeAmount; set { lureFlakeAmount = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float LureSparkle { get => lureSparkle; set { lureSparkle = Mathf.Clamp(value, 0f, 3f); Push(); } }
        public float LureTranslucency { get => lureTranslucency; set { lureTranslucency = Mathf.Clamp(value, 0.25f, 3f); Push(); } }
        public float LureGloss { get => lureGloss; set { lureGloss = Mathf.Clamp01(value); Push(); } }

        /// See lureReferenceSize. Settable for the .sculpt loader, so a saved lure reopens with the
        /// exact flakes it was saved with; 0 means "measure it".
        public float LureReferenceSize
        {
            get => lureReferenceSize;
            set { lureReferenceSize = Mathf.Max(0f, value); Push(); }
        }

        /// Largest world-space extent across the sculpt objects - what "model size" means for
        /// the preset fractions. The default 2 is the startup sphere, for an empty scene.
        private static float MeasureReferenceSize()
        {
            float size = 0f;
            foreach (SculptableMesh sm in FindObjectsByType<SculptableMesh>(FindObjectsSortMode.None))
            {
                var r = sm.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                Vector3 s = r.bounds.size;
                size = Mathf.Max(size, Mathf.Max(s.x, Mathf.Max(s.y, s.z)));
            }
            return size > 1e-4f ? size : 2f;
        }

        public float MatcapIntensity { get => matcapIntensity; set { matcapIntensity = Mathf.Clamp(value, 0f, 3f); Push(); } }
        public float MatcapTintStrength { get => matcapTintStrength; set { matcapTintStrength = Mathf.Clamp01(value); Push(); } }

        /// True when a matcap is both selected and actually loaded - i.e. when the shader is
        /// really running the matcap path. The UI needs this to tell "matcap off" apart from
        /// "matcap on but its image went missing".
        public bool HasMatcap => _matcapTexture != null;

        /// Re-resolves the selected matcap against the library and pushes the result. For after
        /// a rescan, where the selected image may have appeared, changed, or gone away without
        /// the selected NAME having changed at all.
        public void RefreshMatcap()
        {
            ResolveMatcap();
            Push();
        }

        private void ResolveMatcap()
        {
            MatcapLibrary.Entry entry = MatcapLibrary.Find(matcapName);
            _matcapTexture = entry != null ? MatcapLibrary.GetFull(entry) : null;
            if (_matcapTexture == null) matcapEnabled = false;
        }

        private void Awake()
        {
            Shader shader = Shader.Find("Custom/SculptPBR");
            if (shader == null)
            {
                Debug.LogWarning("[SculptMaterial] Custom/SculptPBR shader not found.");
                return;
            }

            _material = new Material(shader) { name = "Sculpt PBR (Runtime)" };
            ResolveMatcap();
            Push();

            // Every sculptable object shares this one material instance - applies to whatever
            // exists at scene start; PrimitiveSpawner/MeshMirror call ApplyTo directly for
            // anything spawned afterward.
            foreach (SculptableMesh sm in FindObjectsByType<SculptableMesh>(FindObjectsSortMode.None))
                ApplyTo(sm.GetComponent<Renderer>());
        }

        /// Whether masked areas are darkened. Off for the turntable's clean view, which shows the
        /// model as it will look, not what is currently protected from the brushes. Not part of
        /// Push: nothing else writes the tint, so the material's own value is the one to restore.
        public bool MaskTintVisible
        {
            get => _maskTintHiddenFrom < 0f;
            set
            {
                if (_material == null || value == MaskTintVisible) return;
                if (value)
                {
                    _material.SetFloat(MaskTintStrengthId, _maskTintHiddenFrom);
                    _maskTintHiddenFrom = -1f;
                }
                else
                {
                    _maskTintHiddenFrom = _material.GetFloat(MaskTintStrengthId);
                    _material.SetFloat(MaskTintStrengthId, 0f);
                }
            }
        }

        private static readonly int MaskTintStrengthId = Shader.PropertyToID("_MaskTintStrength");
        // The strength the tint had before it was hidden; negative while it is showing.
        [System.NonSerialized] private float _maskTintHiddenFrom = -1f;

        /// Applies the shared runtime material to a renderer - called for every existing
        /// object in Awake() above, and by PrimitiveSpawner/MeshMirror for objects created
        /// after startup, so newly spawned/mirrored objects render with the same live-editable
        /// material instead of Unity's default.
        public void ApplyTo(Renderer renderer)
        {
            if (renderer == null || _material == null) return;
            renderer.material = _material;
        }

        private void Push()
        {
            // Cavity is global render state rather than a material property, so it's pushed
            // even if the material failed to build.
            ScreenCavity.Enabled = screenCavityEnabled;
            ScreenCavity.Ridge = cavityRidge;
            ScreenCavity.Valley = cavityValley;

            if (_material == null) return;
            _material.SetColor("_BaseColor", baseColor);
            _material.SetFloat("_Metallic", metallic);
            _material.SetFloat("_Smoothness", smoothness);
            _material.SetFloat("_NormalStrength", normalStrength);
            _material.SetFloat("_NormalNoiseScale", normalNoiseScale);
            _material.SetFloat("_FlatShading", flatShading ? 1f : 0f);

            // A recompile mid-Play drops _matcapTexture (it's [NonSerialized], and the library's
            // statics go with it), which would leave the material pointing at a destroyed
            // texture and the shader reading the "white" default - a blown-out white sculpt.
            // Re-resolving here rather than only on selection keeps that from surviving a Push.
            if (matcapEnabled && _matcapTexture == null) ResolveMatcap();

            bool useMatcap = matcapEnabled && _matcapTexture != null;
            _material.SetFloat("_MatcapEnabled", useMatcap ? 1f : 0f);
            if (_matcapTexture != null) _material.SetTexture("_MatcapTex", _matcapTexture);
            _material.SetFloat("_MatcapIntensity", matcapIntensity);
            _material.SetFloat("_MatcapTintStrength", matcapTintStrength);

            PushLurePlastic(useMatcap);
        }

        private void PushLurePlastic(bool matcapShowing)
        {
            LurePlasticPreset preset = LurePlasticPresets.Find(lurePresetId);
            bool on = lureEnabled && preset != null;
            // The back-face depth pass only earns its draw while the plastic is actually on screen.
            LurePlasticRender.Enabled = on && !matcapShowing;
            _material.SetFloat("_PlasticEnabled", on ? 1f : 0f);
            if (!on) return;

            // Measured lazily: an old scene or file enabled lure plastic without ever measuring.
            if (lureReferenceSize <= 0f) lureReferenceSize = MeasureReferenceSize();
            float size = lureReferenceSize;
            _material.SetColor("_PlasticThinColor", preset.ThinColor);
            _material.SetColor("_PlasticThickColor", preset.ThickColor);
            _material.SetFloat("_PlasticDepth", preset.Depth * size * lureTranslucency);
            // Translucency drives both how deep light gets (depth, above) and how much of it
            // makes it back out (transmission) - one "how see-through" dial.
            _material.SetFloat("_PlasticTransmission", preset.Transmission * lureTranslucency);
            _material.SetFloat("_PlasticGloss", lureGloss);
            _material.SetColor("_FlakeColorA", preset.FlakeA);
            _material.SetColor("_FlakeColorB", preset.FlakeB);
            _material.SetColor("_FlakeColorC", preset.FlakeC);
            // A cell holds one flake averaging 0.67 of the cell's width (radius 0.22-0.45 of it).
            _material.SetFloat("_FlakeCellSize", preset.FlakeSize * size * lureFlakeSize / 0.67f);
            _material.SetFloat("_FlakeDensity", Mathf.Clamp01(preset.FlakeDensity * lureFlakeAmount));
            _material.SetFloat("_FlakeShape", preset.FlakeShape);
            _material.SetFloat("_FlakeTilt", LurePlasticPresets.FlakeTilt);
            _material.SetFloat("_FlakeSparkle", lureSparkle);
            _material.SetColor("_MicroFlakeColor", preset.MicroColor);
            _material.SetFloat("_MicroFlakeCellSize", preset.MicroSize * size * lureFlakeSize / 0.67f);
            _material.SetFloat("_MicroFlakeDensity", Mathf.Clamp01(preset.MicroDensity * lureFlakeAmount));
        }
    }
}
