using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// Builds the "Studio Lighting" section: a master enable toggle, 3-point/5-point mode
    /// buttons, a row to pick which light's sliders are currently shown, and
    /// intensity/yaw/pitch/distance/color controls for whichever light is selected. Only one
    /// set of sliders is built (rather than one per light) - switching the selected light
    /// re-syncs the same sliders to that light's stored values via SetValueWithoutNotify.
    ///
    /// No longer builds its own canvas - StudioPanelUIBuilder merges this section together
    /// with Material and Presentation into one panel with three collapsible headers, and calls
    /// BuildContent with that section's foldout content transform once the panel is up.
    public class LightingUIBuilder : MonoBehaviour
    {
        private static readonly string[] SlotLabels = { "Key", "Fill", "Rim", "Kick1", "Kick2" };

        private LightingRigController _controller;
        private LightSlot _selectedSlot = LightSlot.Key;

        private readonly Image[] _slotButtonImages = new Image[5];
        private Toggle _lightOnToggle;
        private Slider _intensitySlider, _yawSlider, _pitchSlider, _distanceSlider;
        private UIFactory.ColorPickerHandle _colorPicker;

        // Resolved here rather than Start/Awake: LightingRigController builds its rig array in
        // its own Awake, and Unity doesn't guarantee Awake order across different GameObjects,
        // so reading GetConfig() any earlier than this (called from StudioPanelUIBuilder.Start)
        // could race it.
        public void BuildContent(Transform panel)
        {
            _controller = FindFirstObjectByType<LightingRigController>();
            if (_controller == null) return;

            UIFactory.CreateToggle(panel, "Enabled", _controller.StudioLightingEnabled, v => _controller.StudioLightingEnabled = v);

            var modeRow = UIFactory.CreateRow(panel);
            UIFactory.CreateButton(modeRow.transform, "3-Point", () => { _controller.Mode = LightingMode.ThreePoint; RefreshSlotButtons(); });
            UIFactory.CreateButton(modeRow.transform, "5-Point", () => { _controller.Mode = LightingMode.FivePoint; RefreshSlotButtons(); });

            UIFactory.CreateLabel(panel, "Light", 13, FontStyle.Normal);
            var slotRow = UIFactory.CreateRow(panel);
            for (int i = 0; i < SlotLabels.Length; i++)
            {
                var slot = (LightSlot)i;
                Button btn = UIFactory.CreateButton(slotRow.transform, SlotLabels[i], () => SelectSlot(slot));
                _slotButtonImages[i] = btn.GetComponent<Image>();
            }

            _lightOnToggle = UIFactory.CreateToggle(panel, "Light On", true, v => _controller.GetConfig(_selectedSlot).enabled = v);

            UIFactory.CreateLabel(panel, "Intensity", 12, FontStyle.Normal);
            _intensitySlider = UIFactory.CreateSlider(panel, 0f, 15f, 5f, v => _controller.GetConfig(_selectedSlot).intensity = v);

            UIFactory.CreateLabel(panel, "Yaw", 12, FontStyle.Normal);
            _yawSlider = UIFactory.CreateSlider(panel, 0f, 360f, 0f, v => _controller.GetConfig(_selectedSlot).yaw = v);

            UIFactory.CreateLabel(panel, "Pitch", 12, FontStyle.Normal);
            _pitchSlider = UIFactory.CreateSlider(panel, -89f, 89f, 0f, v => _controller.GetConfig(_selectedSlot).pitch = v);

            UIFactory.CreateLabel(panel, "Distance", 12, FontStyle.Normal);
            _distanceSlider = UIFactory.CreateSlider(panel, 0.5f, 10f, 3f, v => _controller.GetConfig(_selectedSlot).distance = v);

            _colorPicker = UIFactory.CreateColorPicker(panel, "Color", Color.white, c => _controller.GetConfig(_selectedSlot).color = c);

            RefreshSlotButtons();
            RefreshSliders();
        }

        private void SelectSlot(LightSlot slot)
        {
            _selectedSlot = slot;
            RefreshSlotButtons();
            RefreshSliders();
        }

        private void RefreshSlotButtons()
        {
            for (int i = 0; i < _slotButtonImages.Length; i++)
            {
                var slot = (LightSlot)i;
                bool isSelected = slot == _selectedSlot;
                bool isAvailable = _controller.IsSlotAvailable(slot);
                _slotButtonImages[i].color = isSelected ? UIFactory.ActiveColor : UIFactory.InactiveColor;
                _slotButtonImages[i].canvasRenderer.SetAlpha(isAvailable ? 1f : 0.4f);
            }
        }

        private void RefreshSliders()
        {
            LightingRigController.RigLight cfg = _controller.GetConfig(_selectedSlot);
            _lightOnToggle.SetIsOnWithoutNotify(cfg.enabled);
            _intensitySlider.SetValueWithoutNotify(cfg.intensity);
            _yawSlider.SetValueWithoutNotify(cfg.yaw);
            _pitchSlider.SetValueWithoutNotify(cfg.pitch);
            _distanceSlider.SetValueWithoutNotify(cfg.distance);
            _colorPicker.SetValueWithoutNotify(cfg.color);
        }
    }
}
