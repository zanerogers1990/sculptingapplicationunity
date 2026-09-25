using System.Collections.Generic;
using Sculpting.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// Builds the lighting controls: a "Lighting" section of ready-made presets, and an "HDRI
    /// Environment" section.
    ///
    /// Lighting is preset-only by design. Earlier versions had a slider-driven studio rig and then
    /// free-placed scene lights; both are gone in favour of picking a look that is already right
    /// (see LightingPresetController / LightingPresets). What is left to adjust is only what a
    /// preset cannot know: 3- or 5-point, overall brightness, which way round the rig faces, whether
    /// it follows the camera, and shadows.
    ///
    /// The "HDRI Environment" section: picking an image off disk, rotating it,
    /// and how strongly it lights and reflects. Whether that HDRI is also DRAWN behind the
    /// sculpt is also switchable here, next to the lighting switch, since "light with it" and
    /// "show it" are the two halves of the same decision and comparing them means flipping
    /// between them. It stays the same single setting underneath (BackgroundController.Mode) as
    /// the Flat/Gradient/HDRI row in Presentation > Background - the two views push a refresh at
    /// each other after any change so neither can sit showing a stale answer.
    ///
    /// Builds no canvas of its own - SceneGraphUIBuilder hands BuildContent the panel, and each
    /// section makes its own top-level foldout in it.
    public class LightingUIBuilder : MonoBehaviour
    {
        // The HDRI status line uses UIFactory's shared status colours, so a failure reads as a
        // failure here and on the Scene panel alike; this note colour is Lighting's own.
        private static readonly Color LightingNoteColor = new Color(0.95f, 0.75f, 0.4f);

        private Toggle _hdriEnabledToggle, _hdriBackgroundToggle;
        private Slider _hdriRotationSlider, _hdriExposureSlider, _hdriAmbientSlider, _hdriReflectionSlider;
        private Text _hdriStatusLabel;
        // The file button doubles as the "which HDRI is loaded" readout - see BuildHdriSection.
        private Text _hdriFileButtonLabel;
        // Static so the picker reopens where the user last was even after the panel is rebuilt
        // (which happens on every scene load).
        private static string _lastHdriDirectory;

        public void BuildContent(Transform panel)
        {
            BuildPresetSection(panel);
            BuildHdriSection(panel);
        }

        // -------------------------------------------------------------------------- presets

        private const int PresetsPerRow = 2;

        private LightingPresetController _lighting;
        private SculptMaterialController _material;
        private Image _threePointButton, _fivePointButton;
        private readonly List<KeyValuePair<string, Image>> _presetButtons = new List<KeyValuePair<string, Image>>();
        private Text _presetDescription, _lightingNote;
        private Slider _brightnessSlider, _rotationSlider;
        private Toggle _followCameraToggle, _shadowsToggle;
        private int _shownLightingVersion = -1;
        // Header + body of the Lighting foldout, so the whole section can be hidden as one.
        private GameObject _presetGroup;

        private void BuildPresetSection(Transform panel)
        {
            // A foldout is two siblings (header, content). Wrapping them lets the section be
            // hidden while a matcap is on - a matcap carries its own shading and ignores lights,
            // so the presets have nothing to act on there - without losing its open/closed state.
            _presetGroup = new GameObject("LightingPresetGroup", typeof(RectTransform));
            _presetGroup.transform.SetParent(panel, false);
            var group = _presetGroup.AddComponent<VerticalLayoutGroup>();
            var panelLayout = panel.GetComponent<VerticalLayoutGroup>();
            group.spacing = panelLayout != null ? panelLayout.spacing : 6f;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = true;
            group.childForceExpandHeight = false;

            Transform section = UIFactory.CreateFoldoutSection(_presetGroup.transform, "Lighting", true);
            _lighting = LightingPresetController.Instance;
            _material = FindFirstObjectByType<SculptMaterialController>();
            if (_lighting == null) return;

            UIFactory.CreateLabel(section, "Rig", 12, FontStyle.Normal);
            var rigRow = UIFactory.CreateRow(section);
            _threePointButton = UIFactory.CreateButton(rigRow.transform, "3-Point", () => SetFivePoint(false),
                "Key, fill and rim - the classic three-light setup.").GetComponent<Image>();
            _fivePointButton = UIFactory.CreateButton(rigRow.transform, "5-Point", () => SetFivePoint(true),
                "Adds a kicker along the far edge and a soft top light to the key, fill and rim.").GetComponent<Image>();

            UIFactory.CreateLabel(section, "Preset", 12, FontStyle.Normal);
            _presetButtons.Clear();
            IReadOnlyList<LightingPreset> presets = LightingPresets.All;
            Transform row = null;
            for (int i = 0; i < presets.Count; i++)
            {
                if (i % PresetsPerRow == 0) row = UIFactory.CreateRow(section, 24f).transform;
                LightingPreset preset = presets[i];
                Button button = UIFactory.CreateButton(row, preset.Name, () => SelectPreset(preset.Id), preset.Description);
                _presetButtons.Add(new KeyValuePair<string, Image>(preset.Id, button.GetComponent<Image>()));
            }

            _presetDescription = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);
            _lightingNote = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);
            _lightingNote.color = LightingNoteColor;

            UIFactory.CreateLabel(section, "Brightness", 12, FontStyle.Normal);
            _brightnessSlider = UIFactory.CreateSlider(section, 0f, LightingPresetController.MaxBrightness, _lighting.Brightness,
                v => _lighting.Brightness = v, "Scales every light and the ambient together - the preset's look is kept.");

            UIFactory.CreateLabel(section, "Rotation", 12, FontStyle.Normal);
            _rotationSlider = UIFactory.CreateSlider(section, -180f, 180f, _lighting.Rotation,
                v => _lighting.Rotation = v, "Swings the whole rig around the model, e.g. to bring the key in from the right.");

            _followCameraToggle = UIFactory.CreateToggle(section, "Lock to Camera", _lighting.FollowCamera,
                v => _lighting.FollowCamera = v,
                tooltip: "On: the lights turn with the view, so the model is always lit from the front as you orbit. " +
                         "Off: the lights stay fixed in the world where they are now.");

            _shadowsToggle = UIFactory.CreateToggle(section, "Shadows", _lighting.Shadows,
                v => _lighting.Shadows = v, tooltip: "Soft shadows from the key light.");

            RefreshLightingControls(true);
        }

        private void SetFivePoint(bool five)
        {
            _lighting.FivePoint = five;
            RefreshLightingControls(true);
        }

        private void SelectPreset(string id)
        {
            _lighting.PresetId = id;
            RefreshLightingControls(true);
        }

        // SetValueWithoutNotify throughout: pushing the controller's state into the widgets must
        // not fire the change handlers back at it.
        private void RefreshLightingControls(bool force)
        {
            if (_lighting == null || _presetDescription == null) return;
            bool lit = _material == null || !_material.MatcapEnabled;
            if (_presetGroup.activeSelf != lit) _presetGroup.SetActive(lit);
            if (!lit) return;
            RefreshLightingNote();
            if (!force && _shownLightingVersion == _lighting.Version) return;
            _shownLightingVersion = _lighting.Version;

            _threePointButton.color = _lighting.FivePoint ? UIFactory.InactiveColor : UIFactory.ActiveColor;
            _fivePointButton.color = _lighting.FivePoint ? UIFactory.ActiveColor : UIFactory.InactiveColor;

            string current = _lighting.PresetId;
            for (int i = 0; i < _presetButtons.Count; i++)
                _presetButtons[i].Value.color = _presetButtons[i].Key == current ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _presetDescription.text = _lighting.Current.Description;

            _brightnessSlider.SetValueWithoutNotify(_lighting.Brightness);
            _rotationSlider.SetValueWithoutNotify(_lighting.Rotation);
            _followCameraToggle.SetIsOnWithoutNotify(_lighting.FollowCamera);
            _shadowsToggle.SetIsOnWithoutNotify(_lighting.Shadows);
        }

        /// Explains, in place, when the preset is not the whole picture.
        private void RefreshLightingNote()
        {
            string note = string.Empty;
            if (HdriEnvironmentController.Existing?.IsActive ?? false)
                note = "The HDRI is supplying the ambient light; the preset's lights shape the form on top.";
            if (_lightingNote.text != note) _lightingNote.text = note;
        }

        // The controller can also change from a scene load, so the panel polls its version counter
        // rather than trying to be told.
        private void Update() => RefreshLightingControls(false);

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
            Button fileButton = UIFactory.CreateButton(section, FileButtonLabel(hdri), PickHdri,
                "Browse for an HDRI image file to light the scene with.");
            _hdriFileButtonLabel = fileButton.GetComponentInChildren<Text>();

            _hdriStatusLabel = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);
            RefreshHdriStatus();

            _hdriEnabledToggle = UIFactory.CreateToggle(section, "Use HDRI Lighting", hdri.Enabled,
                v => { HdriEnvironmentController.Instance.Enabled = v; SyncBackgroundUi(); },
                tooltip: "Uses the loaded HDRI image as the ambient and reflected light. The lighting preset's lights stay on top.");

            // Sits directly under the lighting switch because it is the other half of the same
            // question, and it is a TOGGLE rather than the one-way "show it" button it replaces:
            // switching the HDRI backdrop off has to give back the Flat/Gradient the scene was
            // already using, which is what BackgroundController.LastColorMode remembers.
            var backgroundController = FindFirstObjectByType<BackgroundController>();
            _hdriBackgroundToggle = UIFactory.CreateToggle(section, "Show HDRI as Background",
                backgroundController != null && backgroundController.Mode == BackgroundMode.Hdri,
                SetHdriBackground, tooltip: "Shows the HDRI image itself behind the sculpt, instead of the flat/gradient backdrop.");

            UIFactory.CreateLabel(section, "Rotation", 12, FontStyle.Normal);
            _hdriRotationSlider = UIFactory.CreateSlider(section, 0f, 360f, hdri.Rotation,
                v => HdriEnvironmentController.Instance.Rotation = v, "Spins the HDRI image around the scene.");

            UIFactory.CreateLabel(section, "Exposure", 12, FontStyle.Normal);
            _hdriExposureSlider = UIFactory.CreateSlider(section, 0f, 4f, hdri.Exposure,
                v => HdriEnvironmentController.Instance.Exposure = v, "Overall brightness of the HDRI lighting and backdrop.");

            UIFactory.CreateLabel(section, "Ambient Intensity", 12, FontStyle.Normal);
            _hdriAmbientSlider = UIFactory.CreateSlider(section, 0f, 3f, hdri.AmbientIntensity,
                v => HdriEnvironmentController.Instance.AmbientIntensity = v, "How much the HDRI fills in soft ambient light.");

            UIFactory.CreateLabel(section, "Reflections", 12, FontStyle.Normal);
            _hdriReflectionSlider = UIFactory.CreateSlider(section, 0f, 1f, hdri.ReflectionIntensity,
                v => HdriEnvironmentController.Instance.ReflectionIntensity = v, "How strongly the HDRI shows up in reflective/metallic surfaces.");

            UIFactory.CreateButton(section, "Clear HDRI", ClearHdri, "Removes the loaded HDRI image.");

            if (!FileDialog.IsSupported)
                UIFactory.CreateLabel(section, "No file picker on this platform.", 11, FontStyle.Italic);
        }

        private void PickHdri()
        {
            if (!FileDialog.IsSupported)
            {
                SetHdriStatus("No file picker available on this platform.", UIFactory.StatusErrorColor);
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
                SetHdriStatus("Load an HDRI and switch it on first.", UIFactory.StatusErrorColor);
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
            Color color = hdri.LastLoadFailed ? UIFactory.StatusErrorColor
                        : hdri.HasImage ? UIFactory.StatusOkColor
                        : UIFactory.StatusHintColor;
            SetHdriStatus(hdri.Status, color);
        }

        private void SetHdriStatus(string message, Color color)
        {
            if (_hdriStatusLabel == null) return;
            _hdriStatusLabel.text = message;
            _hdriStatusLabel.color = color;
        }

    }
}
