using Sculpting.IO;
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
    /// Also carries the "HDRI Environment" sub-section: picking an image off disk, rotating it,
    /// and how strongly it lights and reflects. Whether that HDRI is also DRAWN behind the
    /// sculpt is also switchable here, next to the lighting switch, since "light with it" and
    /// "show it" are the two halves of the same decision and comparing them means flipping
    /// between them. It stays the same single setting underneath (BackgroundController.Mode) as
    /// the Flat/Gradient/HDRI row in Presentation > Background - the two views push a refresh at
    /// each other after any change so neither can sit showing a stale answer.
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

        // Same palette the Scene panel's status line uses, so a failure reads as a failure in
        // both places.
        private static readonly Color HdriOkColor = new Color(0.55f, 0.85f, 0.55f);
        private static readonly Color HdriErrorColor = new Color(0.95f, 0.45f, 0.4f);
        private static readonly Color HdriHintColor = new Color(0.65f, 0.65f, 0.7f);

        private Toggle _hdriEnabledToggle, _hdriBackgroundToggle;
        private Slider _hdriRotationSlider, _hdriExposureSlider, _hdriAmbientSlider, _hdriReflectionSlider;
        private Text _hdriStatusLabel;
        // The file button doubles as the "which HDRI is loaded" readout - see BuildHdriSection.
        private Text _hdriFileButtonLabel;
        // Static so the picker reopens where the user last was even after the panel is rebuilt
        // (which happens on every scene load).
        private static string _lastHdriDirectory;

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

            BuildHdriSection(panel);

            RefreshSlotButtons();
            RefreshSliders();
        }

        // ------------------------------------------------------------------------------ HDRI

        private void BuildHdriSection(Transform panel)
        {
            Transform section = UIFactory.CreateFoldoutSection(panel, "HDRI Environment", false);
            HdriEnvironmentController hdri = HdriEnvironmentController.Instance;

            // One control, not a button plus a separate readout: the button's own label IS the
            // loaded file's name, and clicking it browses for a different one. That way the
            // panel always shows which HDRI is in play without a second widget to keep in sync,
            // and there is no state where the button says "Load HDRI..." over an image that is
            // already lighting the scene.
            Button fileButton = UIFactory.CreateButton(section, FileButtonLabel(hdri), PickHdri);
            _hdriFileButtonLabel = fileButton.GetComponentInChildren<Text>();

            _hdriStatusLabel = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);
            RefreshHdriStatus();

            _hdriEnabledToggle = UIFactory.CreateToggle(section, "Use HDRI Lighting", hdri.Enabled,
                v => { HdriEnvironmentController.Instance.Enabled = v; SyncBackgroundUi(); });

            // Sits directly under the lighting switch because it is the other half of the same
            // question, and it is a TOGGLE rather than the one-way "show it" button it replaces:
            // switching the HDRI backdrop off has to give back the Flat/Gradient the scene was
            // already using, which is what BackgroundController.LastColorMode remembers.
            var backgroundController = FindFirstObjectByType<BackgroundController>();
            _hdriBackgroundToggle = UIFactory.CreateToggle(section, "Show HDRI as Background",
                backgroundController != null && backgroundController.Mode == BackgroundMode.Hdri,
                SetHdriBackground);

            UIFactory.CreateLabel(section, "Rotation", 12, FontStyle.Normal);
            _hdriRotationSlider = UIFactory.CreateSlider(section, 0f, 360f, hdri.Rotation,
                v => HdriEnvironmentController.Instance.Rotation = v);

            UIFactory.CreateLabel(section, "Exposure", 12, FontStyle.Normal);
            _hdriExposureSlider = UIFactory.CreateSlider(section, 0f, 4f, hdri.Exposure,
                v => HdriEnvironmentController.Instance.Exposure = v);

            UIFactory.CreateLabel(section, "Ambient Intensity", 12, FontStyle.Normal);
            _hdriAmbientSlider = UIFactory.CreateSlider(section, 0f, 3f, hdri.AmbientIntensity,
                v => HdriEnvironmentController.Instance.AmbientIntensity = v);

            UIFactory.CreateLabel(section, "Reflections", 12, FontStyle.Normal);
            _hdriReflectionSlider = UIFactory.CreateSlider(section, 0f, 1f, hdri.ReflectionIntensity,
                v => HdriEnvironmentController.Instance.ReflectionIntensity = v);

            UIFactory.CreateButton(section, "Clear HDRI", ClearHdri);

            if (!FileDialog.IsSupported)
                UIFactory.CreateLabel(section, "No file picker on this platform.", 11, FontStyle.Italic);
        }

        private void PickHdri()
        {
            if (!FileDialog.IsSupported)
            {
                SetHdriStatus("No file picker available on this platform.", HdriErrorColor);
                return;
            }

            string start = string.IsNullOrEmpty(_lastHdriDirectory)
                ? FileDialog.DirectoryFor(HdriEnvironmentController.Instance.Path)
                : _lastHdriDirectory;

            string chosen = FileDialog.OpenFile("Open HDRI", start, HdriLoader.Extensions);
            // Cancelling is a normal thing to do, not an error - leave the current HDRI alone.
            if (string.IsNullOrEmpty(chosen)) return;
            _lastHdriDirectory = FileDialog.DirectoryFor(chosen);

            bool loaded = HdriEnvironmentController.Instance.LoadFrom(chosen);
            // Show what was just picked. Picking an image and having the view not change at all
            // is indistinguishable from the pick having failed - which is exactly how this read
            // before. The background mode is still free to be moved back to Flat/Gradient
            // afterwards; this only decides what happens at the moment of choosing.
            if (loaded)
            {
                var background = FindFirstObjectByType<BackgroundController>();
                if (background != null) background.Mode = BackgroundMode.Hdri;
            }
            SyncBackgroundUi();
        }

        private void ClearHdri()
        {
            var background = FindFirstObjectByType<BackgroundController>();
            HdriEnvironmentController.Instance.Clear();
            // Dropping the image while it is also the backdrop would leave the background stuck
            // on a mode with nothing behind it, showing the gradient while every control still
            // said HDRI. Put the colour backdrop back explicitly.
            if (background != null && background.Mode == BackgroundMode.Hdri)
                background.Mode = background.LastColorMode;
            SyncBackgroundUi();
        }

        private void SetHdriBackground(bool show)
        {
            var background = FindFirstObjectByType<BackgroundController>();
            if (background == null) return;

            if (show && !HdriEnvironmentController.Instance.IsActive)
            {
                // Refuse rather than switch to a mode that would silently fall back to the
                // gradient anyway, and put the toggle back so it never claims a state the
                // scene is not in.
                SetHdriStatus("Load an HDRI and switch it on first.", HdriErrorColor);
                _hdriBackgroundToggle.SetIsOnWithoutNotify(false);
                return;
            }

            background.Mode = show ? BackgroundMode.Hdri : background.LastColorMode;
            SyncBackgroundUi();
        }

        /// Re-syncs BOTH views of the background setting after something changed it. The two
        /// Refresh methods only touch their own widgets - if either called the other from inside
        /// itself, the pair would recurse.
        private void SyncBackgroundUi()
        {
            RefreshHdriControls();
            FindFirstObjectByType<PostProcessingUIBuilder>()?.RefreshBackgroundModeButtons();
        }

        /// Re-syncs every HDRI control to the controller. Public so a scene load, which replaces
        /// all of these settings at once, can bring the panel back in step.
        public void RefreshHdriControls()
        {
            if (_hdriEnabledToggle == null) return;
            HdriEnvironmentController hdri = HdriEnvironmentController.Instance;

            _hdriEnabledToggle.SetIsOnWithoutNotify(hdri.Enabled);
            _hdriRotationSlider.SetValueWithoutNotify(hdri.Rotation);
            _hdriExposureSlider.SetValueWithoutNotify(hdri.Exposure);
            _hdriAmbientSlider.SetValueWithoutNotify(hdri.AmbientIntensity);
            _hdriReflectionSlider.SetValueWithoutNotify(hdri.ReflectionIntensity);

            var background = FindFirstObjectByType<BackgroundController>();
            if (_hdriBackgroundToggle != null && background != null)
                _hdriBackgroundToggle.SetIsOnWithoutNotify(background.Mode == BackgroundMode.Hdri);

            RefreshHdriStatus();
        }

        private static string FileButtonLabel(HdriEnvironmentController hdri) =>
            hdri.HasImage ? hdri.FileName : "Load HDRI...";

        /// Colours the status line by outcome. A rejected file used to say so in the same grey
        /// italic as "No HDRI loaded.", which is why a refused pick looked like nothing at all
        /// having happened.
        private void RefreshHdriStatus()
        {
            HdriEnvironmentController hdri = HdriEnvironmentController.Instance;
            if (_hdriFileButtonLabel != null) _hdriFileButtonLabel.text = FileButtonLabel(hdri);
            Color color = hdri.LastLoadFailed ? HdriErrorColor
                        : hdri.HasImage ? HdriOkColor
                        : HdriHintColor;
            SetHdriStatus(hdri.Status, color);
        }

        private void SetHdriStatus(string message, Color color)
        {
            if (_hdriStatusLabel == null) return;
            _hdriStatusLabel.text = message;
            _hdriStatusLabel.color = color;
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
