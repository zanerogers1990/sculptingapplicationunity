using UnityEngine;

namespace Sculpting
{
    /// Assigns a runtime instance of the Custom/SculptPBR shader to the sculpted mesh and
    /// exposes its parameters (base PBR sliders, a procedural normal-detail strength, and
    /// the single-colour cavity recess shading) so they're editable live from the Material UI panel
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

        // Cavity is ONE colour, and it only goes into recesses - see SculptPBR.shader's
        // ApplyCavity for why the near-white "peak" colour that used to sit alongside this was
        // removed rather than just turned down.
        [SerializeField] private bool cavityEnabled = false;
        [SerializeField] private Color recessColor = new Color(0.12f, 0.10f, 0.09f);
        [SerializeField, Range(0f, 2f)] private float cavityIntensity = 1f;
        // Upper bound is well past the 1.0 the encoded cavity value can ever reach, and that
        // is deliberate: the ramp is smoothstep(0.5, 0.5 + range, cavity), so a range wider
        // than the signal is the only way to ask for "deepest creases only". It used to stop
        // at 0.6, which still darkens a fully-saturated vertex by ~93% - on a dense imported
        // model, where a large share of vertices clamp to 1.0, that left no way to pull the
        // tint back off everything but the sharpest recesses. This is NOT a duplicate of
        // intensity: widening the ramp fades shallow curvature far faster than deep, so the
        // affected area shrinks rather than the whole tint dimming uniformly.
        [SerializeField, Range(0.05f, 2f)] private float cavityRange = 0.25f;

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

        private Material _material;
        [System.NonSerialized] private Texture2D _matcapTexture;

        public Color BaseColor { get => baseColor; set { baseColor = value; Push(); } }
        public float Metallic { get => metallic; set { metallic = Mathf.Clamp01(value); Push(); } }
        public float Smoothness { get => smoothness; set { smoothness = Mathf.Clamp01(value); Push(); } }
        public float NormalStrength { get => normalStrength; set { normalStrength = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float NormalNoiseScale { get => normalNoiseScale; set { normalNoiseScale = Mathf.Clamp(value, 1f, 300f); Push(); } }
        public bool FlatShading { get => flatShading; set { flatShading = value; Push(); } }
        public bool CavityEnabled { get => cavityEnabled; set { cavityEnabled = value; Push(); } }
        public Color RecessColor { get => recessColor; set { recessColor = value; Push(); } }
        public float CavityIntensity { get => cavityIntensity; set { cavityIntensity = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float CavityRange { get => cavityRange; set { cavityRange = Mathf.Clamp(value, 0.05f, 2f); Push(); } }

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
            if (_material == null) return;
            _material.SetColor("_BaseColor", baseColor);
            _material.SetFloat("_Metallic", metallic);
            _material.SetFloat("_Smoothness", smoothness);
            _material.SetFloat("_NormalStrength", normalStrength);
            _material.SetFloat("_NormalNoiseScale", normalNoiseScale);
            _material.SetFloat("_FlatShading", flatShading ? 1f : 0f);
            _material.SetFloat("_CavityEnabled", cavityEnabled ? 1f : 0f);
            _material.SetColor("_RecessColor", recessColor);
            _material.SetFloat("_CavityIntensity", cavityIntensity);
            _material.SetFloat("_CavityRange", cavityRange);

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
        }
    }
}
