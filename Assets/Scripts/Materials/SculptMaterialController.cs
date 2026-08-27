using UnityEngine;

namespace Sculpting
{
    /// Assigns a runtime instance of the Custom/SculptPBR shader to the sculpted mesh and
    /// exposes its parameters (base PBR sliders, a procedural normal-detail strength, and
    /// the cavity recess/peak coloring) so they're editable live from the Material UI panel
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

        [SerializeField] private bool cavityEnabled = false;
        [SerializeField] private Color recessColor = new Color(0.12f, 0.10f, 0.09f);
        [SerializeField] private Color peakColor = new Color(1f, 0.96f, 0.86f);
        [SerializeField, Range(0f, 2f)] private float cavityIntensity = 1f;
        [SerializeField, Range(0.05f, 0.6f)] private float cavityRange = 0.25f;

        private Material _material;

        public Color BaseColor { get => baseColor; set { baseColor = value; Push(); } }
        public float Metallic { get => metallic; set { metallic = Mathf.Clamp01(value); Push(); } }
        public float Smoothness { get => smoothness; set { smoothness = Mathf.Clamp01(value); Push(); } }
        public float NormalStrength { get => normalStrength; set { normalStrength = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float NormalNoiseScale { get => normalNoiseScale; set { normalNoiseScale = Mathf.Clamp(value, 1f, 300f); Push(); } }
        public bool FlatShading { get => flatShading; set { flatShading = value; Push(); } }
        public bool CavityEnabled { get => cavityEnabled; set { cavityEnabled = value; Push(); } }
        public Color RecessColor { get => recessColor; set { recessColor = value; Push(); } }
        public Color PeakColor { get => peakColor; set { peakColor = value; Push(); } }
        public float CavityIntensity { get => cavityIntensity; set { cavityIntensity = Mathf.Clamp(value, 0f, 2f); Push(); } }
        public float CavityRange { get => cavityRange; set { cavityRange = Mathf.Clamp(value, 0.05f, 0.6f); Push(); } }

        private void Awake()
        {
            Shader shader = Shader.Find("Custom/SculptPBR");
            if (shader == null)
            {
                Debug.LogWarning("[SculptMaterial] Custom/SculptPBR shader not found.");
                return;
            }

            _material = new Material(shader) { name = "Sculpt PBR (Runtime)" };
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
            _material.SetColor("_PeakColor", peakColor);
            _material.SetFloat("_CavityIntensity", cavityIntensity);
            _material.SetFloat("_CavityRange", cavityRange);
        }
    }
}
