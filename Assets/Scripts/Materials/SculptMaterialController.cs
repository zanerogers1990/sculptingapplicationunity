using UnityEngine;

namespace Sculpting
{
    /// The Surface Shader categories that replace plain Base Color shading. At most one is on.
    public enum SurfaceFinish { None, LurePlastic, Metal, Clay }

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

        // Aged metal (see MetalFinishPresets / SculptPBR's MetalShade): rust, patina, washes and
        // antiqued metals. Another Surface Shader category next to lure plastic - at most one
        // category is on. Sliders are adjustments on top of the preset (1 = as the preset has it),
        // same as the lure's.
        [SerializeField] private bool metalEnabled = false;
        [SerializeField] private string metalPresetId = "rust";
        // How much bare metal shows through the coat in blotches - 1 is the preset, higher more.
        [SerializeField, Range(0f, 2f)] private float metalExposure = 1f;
        [SerializeField, Range(0f, 2f)] private float metalEdgeWear = 1f;
        [SerializeField, Range(0f, 1.5f)] private float metalWash = 1f;
        [SerializeField, Range(0.25f, 3f)] private float metalDetail = 1f;
        [SerializeField, Range(0.25f, 3f)] private float metalPatternSize = 1f;
        [SerializeField, Range(0f, 2f)] private float metalGloss = 1f;
        // Where the blotches fall: a position along a path through the patch noise. Sliding it
        // drifts the blotches across the model; Shuffle jumps it somewhere random.
        [SerializeField, Range(0f, 1f)] private float metalPatternSeed = 0f;
        // Same frozen-at-pick model size as lureReferenceSize, for the same reason: the coat's
        // patches and grain scale with it, and a live value would crawl while sculpting.
        [SerializeField, HideInInspector] private float metalReferenceSize;

        // Sculptor's clay (see ClayPresets / SculptPBR's ClayShade): grey plasteline, terracotta
        // and friends - the third Surface Shader category, exclusive with the other two. Sliders
        // are adjustments on top of the preset (1 = as the preset has it).
        [SerializeField] private bool clayEnabled = false;
        [SerializeField] private string clayPresetId = "terracotta";
        [SerializeField, Range(0f, 2f)] private float clayGloss = 1f;
        [SerializeField, Range(0f, 2f)] private float clayWetness = 1f;
        [SerializeField, Range(0f, 2f)] private float claySubsurface = 1f;
        [SerializeField, Range(0f, 2f)] private float clayRecess = 1f;
        [SerializeField, Range(0.25f, 3f)] private float clayDetail = 1f;
        [SerializeField, Range(0f, 3f)] private float clayGrain = 1f;
        // Frozen-at-pick model size, as metalReferenceSize: the grain and mottling scale with it.
        [SerializeField, HideInInspector] private float clayReferenceSize;

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
                if (lureEnabled) { metalEnabled = false; clayEnabled = false; }
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
            metalEnabled = false;
            clayEnabled = false;
            lureReferenceSize = MeasureReferenceSize();
            Push();
        }

        public bool MetalEnabled
        {
            get => metalEnabled;
            set
            {
                metalEnabled = value && MetalFinishPresets.Find(metalPresetId) != null;
                if (metalEnabled) { lureEnabled = false; clayEnabled = false; }
                if (metalEnabled && metalReferenceSize <= 0f) metalReferenceSize = MeasureReferenceSize();
                Push();
            }
        }

        /// Id of the selected MetalFinishPreset; an unknown one switches the finish off.
        public string MetalPresetId
        {
            get => metalPresetId;
            set
            {
                metalPresetId = value ?? string.Empty;
                if (MetalFinishPresets.Find(metalPresetId) == null) metalEnabled = false;
                Push();
            }
        }

        /// Picks a metal finish and turns it on - a palette click. Re-measures the model, like
        /// SelectLurePreset.
        public void SelectMetalPreset(string id)
        {
            if (MetalFinishPresets.Find(id) == null) return;
            metalPresetId = id;
            metalEnabled = true;
            lureEnabled = false;
            clayEnabled = false;
            metalReferenceSize = MeasureReferenceSize();
            Push();
        }

        public float MetalExposure { get => metalExposure; set { metalExposure = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float MetalEdgeWear { get => metalEdgeWear; set { metalEdgeWear = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float MetalWash { get => metalWash; set { metalWash = Mathf.Clamp(value, 0f, 1.5f); Push(); } }
        public float MetalDetail { get => metalDetail; set { metalDetail = Mathf.Clamp(value, 0.25f, 3f); Push(); } }
        public float MetalPatternSize { get => metalPatternSize; set { metalPatternSize = Mathf.Clamp(value, 0.25f, 3f); Push(); } }
        public float MetalGloss { get => metalGloss; set { metalGloss = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float MetalPatternSeed { get => metalPatternSeed; set { metalPatternSeed = Mathf.Clamp01(value); Push(); } }

        /// A random blotch layout - the Shuffle button.
        public void ShuffleMetalPattern() => MetalPatternSeed = Random.value;

        /// See metalReferenceSize; settable for the .sculpt loader, 0 means "measure it".
        public float MetalReferenceSize
        {
            get => metalReferenceSize;
            set { metalReferenceSize = Mathf.Max(0f, value); Push(); }
        }

        public bool ClayEnabled
        {
            get => clayEnabled;
            set
            {
                clayEnabled = value && ClayPresets.Find(clayPresetId) != null;
                if (clayEnabled) { lureEnabled = false; metalEnabled = false; }
                if (clayEnabled && clayReferenceSize <= 0f) clayReferenceSize = MeasureReferenceSize();
                Push();
            }
        }

        /// Id of the selected ClayPreset; an unknown one switches the finish off.
        public string ClayPresetId
        {
            get => clayPresetId;
            set
            {
                clayPresetId = value ?? string.Empty;
                if (ClayPresets.Find(clayPresetId) == null) clayEnabled = false;
                Push();
            }
        }

        /// Picks a clay and turns it on - a palette click. Re-measures the model, like
        /// SelectLurePreset.
        public void SelectClayPreset(string id)
        {
            if (ClayPresets.Find(id) == null) return;
            clayPresetId = id;
            clayEnabled = true;
            lureEnabled = false;
            metalEnabled = false;
            clayReferenceSize = MeasureReferenceSize();
            Push();
        }

        public float ClayGloss { get => clayGloss; set { clayGloss = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float ClayWetness { get => clayWetness; set { clayWetness = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float ClaySubsurface { get => claySubsurface; set { claySubsurface = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float ClayRecess { get => clayRecess; set { clayRecess = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float ClayDetail { get => clayDetail; set { clayDetail = Mathf.Clamp(value, 0.25f, 3f); Push(); } }
        public float ClayGrain { get => clayGrain; set { clayGrain = Mathf.Clamp(value, 0f, 3f); Push(); } }

        /// See clayReferenceSize; settable for the .sculpt loader, 0 means "measure it".
        public float ClayReferenceSize
        {
            get => clayReferenceSize;
            set { clayReferenceSize = Mathf.Max(0f, value); Push(); }
        }

        /// Which Surface Shader category is on - the Material panel's dropdown. Choosing a
        /// category turns on its last-picked preset.
        public SurfaceFinish Finish
        {
            get => lureEnabled ? SurfaceFinish.LurePlastic
                 : metalEnabled ? SurfaceFinish.Metal
                 : clayEnabled ? SurfaceFinish.Clay
                 : SurfaceFinish.None;
            set
            {
                switch (value)
                {
                    case SurfaceFinish.LurePlastic: LureEnabled = true; break;
                    case SurfaceFinish.Metal: MetalEnabled = true; break;
                    case SurfaceFinish.Clay: ClayEnabled = true; break;
                    default: lureEnabled = false; metalEnabled = false; clayEnabled = false; Push(); break;
                }
            }
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
            PushMetal();
            PushClay();
        }

        private void PushClay()
        {
            ClayPreset preset = ClayPresets.Find(clayPresetId);
            bool on = clayEnabled && preset != null;
            _material.SetFloat("_ClayEnabled", on ? 1f : 0f);
            if (!on) return;

            if (clayReferenceSize <= 0f) clayReferenceSize = MeasureReferenceSize();
            float size = clayReferenceSize;
            _material.SetColor("_ClayColor", preset.Color);
            _material.SetColor("_ClayRecessColor", preset.RecessColor);
            _material.SetColor("_ClayScatterColor", preset.ScatterColor);
            _material.SetFloat("_ClaySmoothness", Mathf.Clamp01(preset.Smoothness * clayGloss));
            // Gloss also takes the wet film with it: a matte clay has no wet shine either.
            _material.SetFloat("_ClayWetness", preset.Wetness * clayWetness * Mathf.Min(clayGloss, 1f));
            _material.SetFloat("_ClayWetPower", preset.WetSharpness);
            _material.SetFloat("_ClaySubsurface", preset.Subsurface * claySubsurface);
            _material.SetFloat("_ClayRecess", preset.RecessDepth * clayRecess);
            _material.SetFloat("_ClayRidge", preset.RidgeBurnish);
            _material.SetFloat("_ClayMottle", preset.Mottle);
            _material.SetFloat("_ClayMottleSize", preset.MottleSize * size);
            _material.SetFloat("_ClayGrain", preset.Grain * clayGrain);
            _material.SetFloat("_ClayGrainSize", preset.GrainSize * size);
            _material.SetFloat("_ClayDetail", clayDetail);
        }

        private void PushMetal()
        {
            MetalFinishPreset preset = MetalFinishPresets.Find(metalPresetId);
            bool on = metalEnabled && preset != null;
            _material.SetFloat("_MetalEnabled", on ? 1f : 0f);
            if (!on) return;

            if (metalReferenceSize <= 0f) metalReferenceSize = MeasureReferenceSize();
            float size = metalReferenceSize;
            _material.SetColor("_MetalColor", preset.MetalColor);
            _material.SetFloat("_MetalSmoothness", Mathf.Clamp01(preset.MetalSmoothness * metalGloss));
            _material.SetFloat("_MetalMetallic", preset.MetalMetallic);
            _material.SetColor("_CoatColorA", preset.CoatA);
            _material.SetColor("_CoatColorB", preset.CoatB);
            _material.SetFloat("_CoatMetallic", preset.CoatMetallic);
            _material.SetFloat("_CoatSmoothness", Mathf.Clamp01(preset.CoatSmoothness * metalGloss));
            _material.SetColor("_WashColor", preset.WashColor);
            _material.SetFloat("_MetalWash", Mathf.Clamp01(preset.Wash * metalWash));
            // Additive rather than a multiplier on the preset's coverage, so the slider has the same
            // reach on a thinly coated finish (gold, 0.25) as on a heavily coated one (patina, 0.8).
            _material.SetFloat("_MetalCoverage", preset.Coverage - (metalExposure - 1f) * ExposureReach);
            _material.SetFloat("_MetalEdgeWear", preset.EdgeWear * metalEdgeWear);
            _material.SetFloat("_MetalPatchSize", preset.PatchSize * size * metalPatternSize);
            _material.SetFloat("_MetalPatchContrast", preset.PatchContrast);
            _material.SetFloat("_MetalPatchAmount", preset.Patchiness);
            _material.SetFloat("_MetalCoatCurvature", preset.FollowsRecesses);
            _material.SetFloat("_MetalGrain", preset.Grain);
            _material.SetFloat("_MetalGrainSize", preset.GrainSize * size * metalPatternSize);
            _material.SetFloat("_MetalDetail", metalDetail);
            _material.SetVector("_MetalPatternOffset", PatternPath * (metalPatternSeed * PatternPathLength));
        }

        // Coverage shift at each end of the Exposed Metal slider: enough to take any preset from
        // almost fully coated to mostly bare.
        private const float ExposureReach = 0.6f;
        // The seed slider walks this far through the patch noise, in patch widths: plenty of
        // distinct layouts end to end, while a small drag still moves the blotches a fraction of
        // their size, so it reads as drifting rather than jumping. Off-axis, so the walk doesn't
        // run along a row of the noise lattice.
        private const float PatternPathLength = 40f;
        private static readonly Vector4 PatternPath = new Vector3(0.53f, 0.29f, 0.80f).normalized;

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

        // ------------------------------------------------------------------- save/load state

        /// Every material setting a scene file records, as a flat JsonUtility-serializable block
        /// (see SceneSerializer) - owned here, like SculptController.Settings, so a new setting is
        /// remembered by editing this class rather than the serializer as well. Field names and
        /// initializers are the file format: a key missing from an older file loads with the
        /// initializer here, so they must not change.
        [System.Serializable]
        public class Settings
        {
            public Color baseColor = Color.grey;
            public float metallic;
            public float smoothness = 0.4f;
            public float normalStrength;
            public float normalNoiseScale = 60f;
            public bool flatShading;
            // Screen-space cavity (Blender-style ridge/valley). Older files carry the retired
            // per-vertex tint's cavityEnabled/recessColor/cavityIntensity/cavityRange (and, older
            // still, peakColor); JsonUtility drops keys it has no member for, so those files load
            // with these defaults instead of carrying an unrelated on/off state across.
            public bool screenCavityEnabled = true;
            public float cavityRidge = 1f;
            public float cavityValley = 1f;

            // Matcap by file name rather than by path: the image lives in the app's own Matcaps
            // folder, so a name still resolves on a machine where the app is installed somewhere
            // else. A name this machine doesn't have falls back to lit shading (see
            // SculptMaterialController.MatcapName).
            public bool matcapEnabled;
            public string matcapName = string.Empty;
            public float matcapIntensity = 1f;
            public float matcapTintStrength;

            // Lure plastic by preset id (LurePlasticPresets). Older files have none of these and
            // load with it off. lureReferenceSize keeps the flakes exactly where they were; 0
            // re-measures the model.
            public bool lureEnabled;
            public string lurePresetId = string.Empty;
            public float lureFlakeSize = 1f;
            public float lureFlakeAmount = 1f;
            public float lureSparkle = 1f;
            public float lureTranslucency = 1f;
            public float lureGloss = 0.8f;
            public float lureReferenceSize;

            // Aged metal finish by preset id (MetalFinishPresets), same scheme as the lure's.
            // Older files have none of these and load with it off.
            public bool metalEnabled;
            public string metalPresetId = string.Empty;
            public float metalExposure = 1f;
            public float metalEdgeWear = 1f;
            public float metalWash = 1f;
            public float metalDetail = 1f;
            public float metalPatternSize = 1f;
            public float metalGloss = 1f;
            public float metalPatternSeed;
            public float metalReferenceSize;

            // Sculptor's clay by preset id (ClayPresets), same scheme again. Older files have none
            // of these and load with it off.
            public bool clayEnabled;
            public string clayPresetId = string.Empty;
            public float clayGloss = 1f;
            public float clayWetness = 1f;
            public float claySubsurface = 1f;
            public float clayRecess = 1f;
            public float clayDetail = 1f;
            public float clayGrain = 1f;
            public float clayReferenceSize;
        }

        public Settings CaptureSettings()
        {
            var s = new Settings();
            s.baseColor = BaseColor;
            s.metallic = Metallic;
            s.smoothness = Smoothness;
            s.normalStrength = NormalStrength;
            s.normalNoiseScale = NormalNoiseScale;
            s.flatShading = FlatShading;
            s.screenCavityEnabled = CavityEnabled;
            s.cavityRidge = CavityRidge;
            s.cavityValley = CavityValley;
            s.matcapEnabled = MatcapEnabled;
            s.matcapName = MatcapName;
            s.matcapIntensity = MatcapIntensity;
            s.matcapTintStrength = MatcapTintStrength;
            s.lureEnabled = LureEnabled;
            s.lurePresetId = LurePresetId;
            s.lureFlakeSize = LureFlakeSize;
            s.lureFlakeAmount = LureFlakeAmount;
            s.lureSparkle = LureSparkle;
            s.lureTranslucency = LureTranslucency;
            s.lureGloss = LureGloss;
            s.lureReferenceSize = LureReferenceSize;
            s.metalEnabled = MetalEnabled;
            s.metalPresetId = MetalPresetId;
            s.metalExposure = MetalExposure;
            s.metalEdgeWear = MetalEdgeWear;
            s.metalWash = MetalWash;
            s.metalDetail = MetalDetail;
            s.metalPatternSize = MetalPatternSize;
            s.metalGloss = MetalGloss;
            s.metalPatternSeed = MetalPatternSeed;
            s.metalReferenceSize = MetalReferenceSize;
            s.clayEnabled = ClayEnabled;
            s.clayPresetId = ClayPresetId;
            s.clayGloss = ClayGloss;
            s.clayWetness = ClayWetness;
            s.claySubsurface = ClaySubsurface;
            s.clayRecess = ClayRecess;
            s.clayDetail = ClayDetail;
            s.clayGrain = ClayGrain;
            s.clayReferenceSize = ClayReferenceSize;
            return s;
        }

        /// Restores a captured block through the public setters, so every clamp and side effect
        /// runs exactly as for a UI change. The ORDER below is load-bearing - see each remark.
        public void ApplySettings(Settings s)
        {
            BaseColor = s.baseColor;
            Metallic = s.metallic;
            Smoothness = s.smoothness;
            NormalStrength = s.normalStrength;
            NormalNoiseScale = s.normalNoiseScale;
            FlatShading = s.flatShading;
            CavityEnabled = s.screenCavityEnabled;
            CavityRidge = s.cavityRidge;
            CavityValley = s.cavityValley;
            MatcapIntensity = s.matcapIntensity;
            MatcapTintStrength = s.matcapTintStrength;
            // Name before the toggle: MatcapEnabled with nothing selected picks the first
            // matcap in the library, which would override what the file actually asked for.
            MatcapName = s.matcapName;
            // ...and only enable if that name actually resolved. MatcapEnabled with nothing
            // selected falls back to the first matcap in the library, which for a file
            // naming a matcap this machine doesn't have would silently substitute a
            // different one - lit shading is the honest answer there.
            MatcapEnabled = s.matcapEnabled && HasMatcap;

            LureFlakeSize = s.lureFlakeSize;
            LureFlakeAmount = s.lureFlakeAmount;
            LureSparkle = s.lureSparkle;
            LureTranslucency = s.lureTranslucency;
            LureGloss = s.lureGloss;
            // Size before the toggle, so enabling doesn't measure a model the file already
            // measured. Objects are loaded by now, so a 0 here measures the right model.
            LureReferenceSize = s.lureReferenceSize;
            if (!string.IsNullOrEmpty(s.lurePresetId)) LurePresetId = s.lurePresetId;
            LureEnabled = s.lureEnabled;

            MetalExposure = s.metalExposure;
            MetalEdgeWear = s.metalEdgeWear;
            MetalWash = s.metalWash;
            MetalDetail = s.metalDetail;
            MetalPatternSize = s.metalPatternSize;
            MetalGloss = s.metalGloss;
            MetalPatternSeed = s.metalPatternSeed;
            MetalReferenceSize = s.metalReferenceSize;
            if (!string.IsNullOrEmpty(s.metalPresetId)) MetalPresetId = s.metalPresetId;
            // After the lure: switching metal on turns the lure off, switching it off leaves
            // the lure alone, so a file can't come back with both on.
            MetalEnabled = s.metalEnabled;

            ClayGloss = s.clayGloss;
            ClayWetness = s.clayWetness;
            ClaySubsurface = s.claySubsurface;
            ClayRecess = s.clayRecess;
            ClayDetail = s.clayDetail;
            ClayGrain = s.clayGrain;
            ClayReferenceSize = s.clayReferenceSize;
            if (!string.IsNullOrEmpty(s.clayPresetId)) ClayPresetId = s.clayPresetId;
            // Last, for the same reason as the metal: on turns the others off, off leaves them.
            ClayEnabled = s.clayEnabled;
        }
    }
}
