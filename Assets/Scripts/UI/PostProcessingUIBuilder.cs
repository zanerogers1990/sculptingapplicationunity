using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace Sculpting
{
    /// Builds the "Presentation" section: collapsible sub-sections for the post-processing
    /// effects used to show the sculpt off (Bloom, Vignette, Depth of Field, Color,
    /// Tonemapping) plus the scene background (flat color or two-color gradient). Background
    /// lives in this section rather than its own since both are about the final presented look
    /// rather than the sculpting/lighting workflow itself.
    ///
    /// No longer builds its own canvas - StudioPanelUIBuilder merges this section together
    /// with Studio Lighting and Material into one panel with three collapsible headers, and
    /// calls BuildContent with that section's foldout content transform once the panel is up.
    public class PostProcessingUIBuilder : MonoBehaviour
    {
        private PostProcessingController _post;
        private BackgroundController _background;

        private readonly Image[] _tonemapButtons = new Image[3];
        private readonly Image[] _bgModeButtons = new Image[2];
        private static readonly TonemappingMode[] TonemapModes = { TonemappingMode.None, TonemappingMode.Neutral, TonemappingMode.ACES };

        // Resolved here rather than Start/Awake: PostProcessingController resolves its Volume
        // overrides in its own Awake, and Unity doesn't guarantee Awake order across different
        // GameObjects, so reading HasVolume any earlier than this (called from
        // StudioPanelUIBuilder.Start) could race it.
        public void BuildContent(Transform panel)
        {
            _post = FindFirstObjectByType<PostProcessingController>();
            _background = FindFirstObjectByType<BackgroundController>();

            if (_post != null && _post.HasVolume) BuildPostProcessingSections(panel);
            else UIFactory.CreateLabel(panel, "No Volume found in scene.", 12, FontStyle.Italic);

            BuildBackgroundSection(panel);
        }

        private void BuildPostProcessingSections(Transform panel)
        {
            Transform bloom = UIFactory.CreateFoldoutSection(panel, "Bloom", false);
            UIFactory.CreateToggle(bloom, "Enabled", _post.BloomEnabled, v => _post.BloomEnabled = v);
            UIFactory.CreateLabel(bloom, "Intensity", 12, FontStyle.Normal);
            UIFactory.CreateSlider(bloom, 0f, 3f, _post.BloomIntensity, v => _post.BloomIntensity = v);
            UIFactory.CreateLabel(bloom, "Threshold", 12, FontStyle.Normal);
            UIFactory.CreateSlider(bloom, 0f, 2f, _post.BloomThreshold, v => _post.BloomThreshold = v);

            Transform vignette = UIFactory.CreateFoldoutSection(panel, "Vignette", false);
            UIFactory.CreateToggle(vignette, "Enabled", _post.VignetteEnabled, v => _post.VignetteEnabled = v);
            UIFactory.CreateLabel(vignette, "Intensity", 12, FontStyle.Normal);
            UIFactory.CreateSlider(vignette, 0f, 1f, _post.VignetteIntensity, v => _post.VignetteIntensity = v);
            UIFactory.CreateLabel(vignette, "Smoothness", 12, FontStyle.Normal);
            UIFactory.CreateSlider(vignette, 0f, 1f, _post.VignetteSmoothness, v => _post.VignetteSmoothness = v);

            Transform dof = UIFactory.CreateFoldoutSection(panel, "Depth of Field", false);
            UIFactory.CreateToggle(dof, "Enabled", _post.DofEnabled, v => _post.DofEnabled = v);
            UIFactory.CreateLabel(dof, "Focus Distance", 12, FontStyle.Normal);
            UIFactory.CreateSlider(dof, 0.1f, 10f, _post.DofFocusDistance, v => _post.DofFocusDistance = v);
            UIFactory.CreateLabel(dof, "Aperture (lower = blurrier)", 12, FontStyle.Normal);
            UIFactory.CreateSlider(dof, 1f, 32f, _post.DofAperture, v => _post.DofAperture = v);

            Transform color = UIFactory.CreateFoldoutSection(panel, "Color", false);
            UIFactory.CreateToggle(color, "Enabled", _post.ColorAdjustmentsEnabled, v => _post.ColorAdjustmentsEnabled = v);
            UIFactory.CreateLabel(color, "Saturation", 12, FontStyle.Normal);
            UIFactory.CreateSlider(color, -100f, 100f, _post.Saturation, v => _post.Saturation = v);
            UIFactory.CreateLabel(color, "Contrast", 12, FontStyle.Normal);
            UIFactory.CreateSlider(color, -100f, 100f, _post.Contrast, v => _post.Contrast = v);

            Transform tonemap = UIFactory.CreateFoldoutSection(panel, "Tonemapping", false);
            var tonemapRow = UIFactory.CreateRow(tonemap);
            string[] tonemapLabels = { "None", "Neutral", "ACES" };
            for (int i = 0; i < TonemapModes.Length; i++)
            {
                TonemappingMode mode = TonemapModes[i];
                Button btn = UIFactory.CreateButton(tonemapRow.transform, tonemapLabels[i], () => { _post.CurrentTonemappingMode = mode; RefreshTonemapButtons(); });
                _tonemapButtons[i] = btn.GetComponent<Image>();
            }
            RefreshTonemapButtons();
        }

        private void RefreshTonemapButtons()
        {
            for (int i = 0; i < TonemapModes.Length; i++)
                _tonemapButtons[i].color = _post.CurrentTonemappingMode == TonemapModes[i] ? UIFactory.ActiveColor : UIFactory.InactiveColor;
        }

        private void BuildBackgroundSection(Transform panel)
        {
            Transform background = UIFactory.CreateFoldoutSection(panel, "Background", false);
            if (_background == null)
            {
                UIFactory.CreateLabel(background, "No BackgroundController found.", 12, FontStyle.Italic);
                return;
            }

            var modeRow = UIFactory.CreateRow(background);
            Button flatBtn = UIFactory.CreateButton(modeRow.transform, "Flat", () => { _background.Mode = BackgroundMode.Flat; RefreshBgModeButtons(); });
            Button gradientBtn = UIFactory.CreateButton(modeRow.transform, "Gradient", () => { _background.Mode = BackgroundMode.Gradient; RefreshBgModeButtons(); });
            _bgModeButtons[0] = flatBtn.GetComponent<Image>();
            _bgModeButtons[1] = gradientBtn.GetComponent<Image>();
            RefreshBgModeButtons();

            UIFactory.CreateColorPicker(background, "Color A (flat / bottom)", _background.ColorA, c => _background.ColorA = c);
            UIFactory.CreateColorPicker(background, "Color B (gradient top)", _background.ColorB, c => _background.ColorB = c);
            UIFactory.CreateLabel(background, "Gradient Bias", 12, FontStyle.Normal);
            UIFactory.CreateSlider(background, 0.2f, 4f, _background.GradientBias, v => _background.GradientBias = v);
        }

        private void RefreshBgModeButtons()
        {
            _bgModeButtons[0].color = _background.Mode == BackgroundMode.Flat ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _bgModeButtons[1].color = _background.Mode == BackgroundMode.Gradient ? UIFactory.ActiveColor : UIFactory.InactiveColor;
        }
    }
}
