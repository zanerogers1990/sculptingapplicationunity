using System.Collections.Generic;
using Sculpting.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// Builds the lighting controls: a "Scene Lights" section for placing and editing lights
    /// directly in the scene, and an "HDRI Environment" section.
    ///
    /// It used to lead with the fixed studio rig - a master enable, 3-point/5-point mode buttons
    /// and per-slot intensity/yaw/pitch/distance/colour sliders, all wrapped in a "Studio
    /// Lighting" foldout that these two sections then sat inside. That is gone: lights are added
    /// and moved in the scene instead, which does everything the rig did and is one less concept.
    /// LightingRigController survives without a UI - see BuildContent.
    ///
    /// The "HDRI Environment" section: picking an image off disk, rotating it,
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
            // The fixed studio rig's own controls (master enable, 3-point/5-point, and the
            // per-slot intensity/yaw/pitch/distance/colour sliders) are GONE - lights are placed
            // and moved in the scene now, which is both more direct and one less thing to learn.
            // LightingRigController itself is deliberately still here: scenes saved before this
            // change carry its settings (see SculptSaveData), and it is what re-enables the
            // scene's own directional sun when the rig is off. It just has no UI any more.
            //
            // What is left are two things that were only ever nested under "Studio Lighting"
            // because the rig was their parent. They now make their own top-level foldouts in
            // whatever panel they are handed.
            BuildSceneLightsSection(panel);
            BuildHdriSection(panel);
        }

        // --------------------------------------------------------------------- scene lights

        // The free-placement light system, as against the fixed rig above - see
        // SceneLightManager. Its own foldout rather than a replacement for the rig's controls:
        // the rig is still what a scene saved before this existed comes back as, and "turn the
        // rig off, add your own lights" is the migration rather than a hard cutover.
        private SceneLightManager _sceneLights;
        private Transform _sceneLightList;
        private Slider _sceneIntensitySlider, _sceneRangeSlider, _sceneAngleSlider;
        private Toggle _sceneShadowToggle;
        private UIFactory.ColorPickerHandle _sceneColorPicker;
        private Text _sceneLightStatus;
        private int _shownLightVersion = -1;

        private SceneLight SelectedSceneLight =>
            _sceneLights != null && _sceneLights.Selected.Count > 0 ? _sceneLights.Selected[0] : null;

        private void BuildSceneLightsSection(Transform panel)
        {
            Transform section = UIFactory.CreateFoldoutSection(panel, "Scene Lights", false);

            // Reached through the gizmo so both get the SAME self-installed instance - asking the
            // scene directly would create a second manager in a scene that has none yet.
            var gizmo = FindFirstObjectByType<TransformGizmo>();
            _sceneLights = gizmo != null ? gizmo.Lights : FindFirstObjectByType<SceneLightManager>();
            if (_sceneLights == null) return;

            UIFactory.CreateLabel(section,
                "Add lights and place them directly. Switch to Transpose to move or aim them; " +
                "click a light to select, Shift+click for several.", 11, FontStyle.Italic);

            var addRow = UIFactory.CreateRow(section);
            UIFactory.CreateButton(addRow.transform, "+ Point", () => AddSceneLight(LightType.Point),
                "Adds a point light - shines equally in every direction.");
            UIFactory.CreateButton(addRow.transform, "+ Spot", () => AddSceneLight(LightType.Spot),
                "Adds a spot light - a cone you can aim with the Transpose gizmo's rotate rings.");
            UIFactory.CreateButton(addRow.transform, "+ Sun", () => AddSceneLight(LightType.Directional),
                "Adds a directional light - parallel rays, position irrelevant, only the aim matters.");

            _sceneLightStatus = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);
            _sceneLightList = UIFactory.CreateRow(section, 0f).transform;

            UIFactory.CreateLabel(section, "Intensity", 12, FontStyle.Normal);
            _sceneIntensitySlider = UIFactory.CreateSlider(section, 0f, 20f, 6f,
                v => ApplyToSelectedLights(l => l.intensity = v), "Brightness of the selected light(s).");

            UIFactory.CreateLabel(section, "Range", 12, FontStyle.Normal);
            _sceneRangeSlider = UIFactory.CreateSlider(section, 0.5f, 40f, 10f,
                v => ApplyToSelectedLights(l => l.range = v),
                "How far a point or spot light reaches. Directional lights ignore this.");

            UIFactory.CreateLabel(section, "Spot Angle", 12, FontStyle.Normal);
            _sceneAngleSlider = UIFactory.CreateSlider(section, 5f, 170f, 70f,
                v => ApplyToSelectedLights(l => { l.spotAngle = v; l.innerSpotAngle = v * 0.45f; }),
                "Width of a spot light's cone. Ignored by the other types.");

            _sceneShadowToggle = UIFactory.CreateToggle(section, "Casts Shadows", false,
                v => ApplyToSelectedLights(l => l.shadows = v ? LightShadows.Soft : LightShadows.None),
                tooltip: "Shadows read the form better but cost more - off by default, as on the studio rig.");

            _sceneColorPicker = UIFactory.CreateColorPicker(section, "Light Color", Color.white,
                c => ApplyToSelectedLights(l => l.color = c));

            UIFactory.CreateButton(section, "Delete Selected", () => { _sceneLights.DeleteSelected(); RefreshSceneLights(true); },
                "Removes the selected light(s). Delete/Backspace does the same while Transpose is active.");

            RefreshSceneLights(true);
        }

        private void AddSceneLight(LightType type)
        {
            if (_sceneLights == null) return;
            _sceneLights.AddLight(type);

            // A new light is placed and selected, but nothing can be dragged until a transform
            // tool is up - switching here saves the user working that out from an inert gizmo.
            var gizmo = FindFirstObjectByType<TransformGizmo>();
            if (gizmo != null && gizmo.Mode == GizmoMode.Sculpt) gizmo.SetMode(GizmoMode.Transpose);

            RefreshSceneLights(true);
        }

        /// Applies an edit to every selected light, so a multi-selection can be dialled in as one.
        private void ApplyToSelectedLights(System.Action<Light> edit)
        {
            if (_sceneLights == null) return;
            IReadOnlyList<SceneLight> selected = _sceneLights.Selected;
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] == null || selected[i].Light == null) continue;
                edit(selected[i].Light);
                // The marker draws in the light's own colour, so it has to be told when that
                // colour changes or it keeps describing the old one.
                selected[i].RefreshMarker();
            }
        }

        private void RefreshSceneLights(bool force)
        {
            if (_sceneLights == null || _sceneLightList == null) return;
            if (!force && _shownLightVersion == _sceneLights.Version) return;
            _shownLightVersion = _sceneLights.Version;

            for (int i = _sceneLightList.childCount - 1; i >= 0; i--)
                Destroy(_sceneLightList.GetChild(i).gameObject);

            IReadOnlyList<SceneLight> lights = _sceneLights.Lights;
            _sceneLightStatus.text = lights.Count == 0
                ? "No scene lights yet."
                : lights.Count + (lights.Count == 1 ? " light" : " lights") + ", "
                  + _sceneLights.Selected.Count + " selected";

            for (int i = 0; i < lights.Count; i++)
            {
                SceneLight light = lights[i];
                if (light == null) continue;
                bool selected = _sceneLights.IsSelected(light);
                UIFactory.CreateButton(_sceneLightList, (selected ? "> " : "   ") + light.name,
                    () => { _sceneLights.Select(light, ShiftHeld()); RefreshSceneLights(true); },
                    "Selects this light. Shift-click to add it to the selection.");
            }

            SyncSceneLightSliders();
        }

        private static bool ShiftHeld()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
        }

        // SetValueWithoutNotify throughout, for the same reason the rig's own sliders use it:
        // pushing the selected light's values into the widgets must not fire the change handlers
        // back at the light and overwrite the other selected lights with them.
        private void SyncSceneLightSliders()
        {
            SceneLight light = SelectedSceneLight;
            if (light == null || light.Light == null) return;
            Light l = light.Light;

            _sceneIntensitySlider.SetValueWithoutNotify(l.intensity);
            _sceneRangeSlider.SetValueWithoutNotify(Mathf.Clamp(l.range, 0.5f, 40f));
            _sceneAngleSlider.SetValueWithoutNotify(Mathf.Clamp(l.spotAngle, 5f, 170f));
            _sceneShadowToggle.SetIsOnWithoutNotify(l.shadows != LightShadows.None);
            _sceneColorPicker?.SetValueWithoutNotify(l.color);
        }

        // The gizmo and the viewport can both change which light is selected, so the panel polls
        // the manager's version counter rather than trying to be told - the same cheap-poll idiom
        // the Scene Graph list already uses for SelectionManager.
        private void Update() => RefreshSceneLights(false);

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
                tooltip: "Lights the scene from the loaded HDRI image instead of the studio rig.");

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

    }
}
