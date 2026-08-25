using UnityEngine;

namespace Sculpting
{
    /// Builds the bottom-left "Material" panel: base PBR sliders (color, metallic,
    /// smoothness, normal detail) plus the cavity recess/peak coloring controls, all wired
    /// directly to SculptMaterialController.
    public class MaterialUIBuilder : MonoBehaviour
    {
        private SculptMaterialController _material;

        // Start (not Awake), for consistency with the other new UI panels - see
        // LightingUIBuilder for why cross-component Awake order can't be relied on.
        private void Start()
        {
            _material = FindFirstObjectByType<SculptMaterialController>();
            if (_material == null) return;
            BuildUI();
        }

        private void BuildUI()
        {
            // Bottom-CENTER rather than bottom-left: the brush panel (top-left) and this
            // panel both grow with their content, and stacking two panels in the same
            // screen column risked them meeting in the middle on tall content (see
            // 2026-08-25 UI overlap fix). Centering removes the collision entirely rather
            // than just making it less likely.
            Transform panel = UIFactory.CreatePanelCanvas(transform, "MaterialCanvas", new Vector2(0.5f, 0f), new Vector2(0, 12), 250f);
            UIFactory.CreateLabel(panel, "Material", 20, FontStyle.Bold);

            UIFactory.CreateColorPicker(panel, "Base Color", _material.BaseColor, c => _material.BaseColor = c);

            UIFactory.CreateLabel(panel, "Metallic", 12, FontStyle.Normal);
            UIFactory.CreateSlider(panel, 0f, 1f, _material.Metallic, v => _material.Metallic = v);

            UIFactory.CreateLabel(panel, "Smoothness", 12, FontStyle.Normal);
            UIFactory.CreateSlider(panel, 0f, 1f, _material.Smoothness, v => _material.Smoothness = v);

            UIFactory.CreateLabel(panel, "Normal Detail Strength", 12, FontStyle.Normal);
            UIFactory.CreateSlider(panel, 0f, 2f, _material.NormalStrength, v => _material.NormalStrength = v);

            UIFactory.CreateLabel(panel, "Normal Detail Scale", 12, FontStyle.Normal);
            UIFactory.CreateSlider(panel, 1f, 300f, _material.NormalNoiseScale, v => _material.NormalNoiseScale = v);

            UIFactory.CreateToggle(panel, "Flat Shading (Show Facets)", _material.FlatShading, v => _material.FlatShading = v);

            Transform cavity = UIFactory.CreateFoldoutSection(panel, "Cavity", false);
            UIFactory.CreateToggle(cavity, "Enabled", _material.CavityEnabled, v => _material.CavityEnabled = v);
            UIFactory.CreateColorPicker(cavity, "Recess Color", _material.RecessColor, c => _material.RecessColor = c);
            UIFactory.CreateColorPicker(cavity, "Peak Color", _material.PeakColor, c => _material.PeakColor = c);
            UIFactory.CreateLabel(cavity, "Cavity Intensity", 12, FontStyle.Normal);
            UIFactory.CreateSlider(cavity, 0f, 2f, _material.CavityIntensity, v => _material.CavityIntensity = v);
            UIFactory.CreateLabel(cavity, "Cavity Range", 12, FontStyle.Normal);
            UIFactory.CreateSlider(cavity, 0.05f, 0.6f, _material.CavityRange, v => _material.CavityRange = v);
        }
    }
}
