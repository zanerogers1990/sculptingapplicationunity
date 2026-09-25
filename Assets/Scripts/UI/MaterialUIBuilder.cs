using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Sculpting.IO;

namespace Sculpting
{
    /// Builds the "Material" section: base PBR sliders (color, metallic, smoothness, normal
    /// detail), the screen-space cavity (Ridge/Valley), the Surface Shader presets (lure plastic,
    /// aged metal, sculptor's clay) and the matcap palette, all wired directly to SculptMaterialController.
    ///
    /// No longer builds its own canvas - StudioPanelUIBuilder merges this section together
    /// with Studio Lighting and Presentation into one panel with three collapsible headers, and
    /// calls BuildContent with that section's foldout content transform once the panel is up.
    public class MaterialUIBuilder : MonoBehaviour
    {
        // Four across fits the 260px panel (12px padding either side, 10px scrollbar strip)
        // at a thumbnail big enough to tell two grey metals apart.
        private const int PaletteColumns = 4;
        private const float PaletteButtonHeight = 50f;
        // Thumbnails decoded per frame while filling the palette. Decoding all fifty in one go
        // is a visible hitch; four a frame finishes in well under a second of scrolling.
        private const int ThumbnailsPerFrame = 4;

        private SculptMaterialController _material;

        private Toggle _matcapToggle;
        private Text _matcapStatus;
        private Transform _paletteRoot;
        // Keyed by entry.Path rather than entry.Name: two matcaps in different categories can
        // share a file name (e.g. a user-imported "Red" colliding with a bundled one), and Path
        // is the one thing MatcapLibrary already guarantees unique per entry.
        private readonly List<KeyValuePair<string, Image>> _paletteButtons = new List<KeyValuePair<string, Image>>();
        private bool _paletteFilled;
        private string _lastImportDirectory = string.Empty;
        // What the matcap controls are currently DISPLAYING, so Update can spot the controller
        // being changed from somewhere else (a scene load) without polling the whole UI.
        private bool _shownMatcapEnabled;
        private string _shownMatcapName;
        // Path of the entry actually selected in the palette. SculptMaterialController.MatcapName
        // is a bare name (that's the .sculpt save format), so it can't disambiguate two entries
        // that share a name - this field is the disambiguated identity, set directly whenever we
        // have the real Entry in hand (a click, an import) rather than re-derived from the name.
        private string _selectedPath;

        public void BuildContent(Transform panel)
        {
            _material = FindFirstObjectByType<SculptMaterialController>();
            if (_material == null) return;

            UIFactory.CreateColorPicker(panel, "Base Color", _material.BaseColor, c => _material.BaseColor = c);

            UIFactory.CreateLabel(panel, "Metallic", 12, FontStyle.Normal);
            UIFactory.CreateSlider(panel, 0f, 1f, _material.Metallic, v => _material.Metallic = v,
                "How metallic the surface looks - low keeps it a plain coloured material, high makes it reflect like bare metal.");

            UIFactory.CreateLabel(panel, "Smoothness", 12, FontStyle.Normal);
            UIFactory.CreateSlider(panel, 0f, 1f, _material.Smoothness, v => _material.Smoothness = v,
                "Sharpness of reflections/highlights - low is matte, high is glossy.");

            UIFactory.CreateLabel(panel, "Normal Detail Strength", 12, FontStyle.Normal);
            UIFactory.CreateSlider(panel, 0f, 2f, _material.NormalStrength, v => _material.NormalStrength = v,
                "Fake surface bumpiness in the shading only - doesn't change the actual mesh.");

            UIFactory.CreateLabel(panel, "Normal Detail Scale", 12, FontStyle.Normal);
            UIFactory.CreateSlider(panel, 1f, 300f, _material.NormalNoiseScale, v => _material.NormalNoiseScale = v,
                "Size of the Normal Detail bumps - low is large and gentle, high is fine grain.");

            UIFactory.CreateToggle(panel, "Flat Shading (Show Facets)", _material.FlatShading, v => _material.FlatShading = v,
                tooltip: "Shows each triangle as a flat facet instead of smoothly blended shading - good for checking mesh density.");

            Transform cavity = UIFactory.CreateFoldoutSection(panel, "Cavity", false);
            UIFactory.CreateToggle(cavity, "Enabled", _material.CavityEnabled, v => _material.CavityEnabled = v,
                tooltip: "Screen-space cavity, like Blender's: brightens ridges and darkens creases so surface detail pops. Measured on screen, so it stays crisp at any mesh density.");
            UIFactory.CreateLabel(cavity, "Ridge", 12, FontStyle.Normal);
            UIFactory.CreateSlider(cavity, 0f, 2f, _material.CavityRidge, v => _material.CavityRidge = v,
                "How much raised edges and ridges are brightened - 0 leaves them alone.");
            UIFactory.CreateLabel(cavity, "Valley", 12, FontStyle.Normal);
            UIFactory.CreateSlider(cavity, 0f, 2f, _material.CavityValley, v => _material.CavityValley = v,
                "How much creases and recesses are darkened - 0 leaves them alone.");

            BuildFinishSection(UIFactory.CreateFoldoutSection(panel, "Surface Shader", false));
            BuildMatcapSection(UIFactory.CreateFoldoutSection(panel, "Matcap", false));
        }

        // ------------------------------------------------------------------ surface shader

        private const float SwatchHeight = 60f;
        private static readonly string[] FinishOptions = { "None (Base Color)", "Lure Plastic", "Metal", "Clay" };

        private UIFactory.InlineDropdown _finishDropdown;
        private Text _finishStatus;
        private GameObject _finishMatcapNote;
        private GameObject _lureGroup, _metalGroup, _clayGroup;
        private Slider _lureSizeSlider, _lureAmountSlider, _lureSparkleSlider, _lureTranslucencySlider, _lureGlossSlider;
        private Slider _metalExposureSlider, _metalWearSlider, _metalWashSlider, _metalDetailSlider, _metalPatternSlider, _metalGlossSlider, _metalSeedSlider;
        private Slider _clayGlossSlider, _clayWetSlider, _claySubsurfaceSlider, _clayRecessSlider, _clayDetailSlider, _clayGrainSlider;
        private readonly List<KeyValuePair<string, Image>> _lureButtons = new List<KeyValuePair<string, Image>>();
        private readonly List<KeyValuePair<string, Image>> _metalButtons = new List<KeyValuePair<string, Image>>();
        private readonly List<KeyValuePair<string, Image>> _clayButtons = new List<KeyValuePair<string, Image>>();
        // What the section last showed, so a scene load (which writes the controller directly)
        // or a matcap toggle elsewhere gets reflected here. Value tuples rather than a formatted
        // string: this is compared every frame.
        private ((bool, SurfaceFinish, string, string, string), (float, float, float, float, float),
                 (float, float, float, float, float, float, float), (float, float, float, float, float, float)) _shownFinishState;

        /// One section for every shader that replaces plain Base Color shading, grouped by
        /// category in a dropdown: pick the category, then a swatch within it.
        private void BuildFinishSection(Transform section)
        {
            // A matcap replaces lighting outright, and every finish here is lighting - so while a
            // matcap is on none of them can show. Said here, with the way out one click away,
            // rather than switching matcap off behind the user's back.
            _finishMatcapNote = CreateGroup(section, "FinishMatcapNote");
            Text noteText = UIFactory.CreateLabel(_finishMatcapNote.transform, "Matcap is on - it hides these shaders.", 11, FontStyle.Italic);
            noteText.color = UIFactory.StatusWarnColor;
            UIFactory.CreateButton(_finishMatcapNote.transform, "Turn Off Matcap", () =>
            {
                _material.MatcapEnabled = false;
                RefreshMatcapUi();
                RefreshFinishUi();
            }, "Switch matcap off so the scene lights - and the surface shader - show.");

            _finishDropdown = UIFactory.CreateDropdown(section, FinishOptions, (int)_material.Finish, i =>
            {
                _material.Finish = (SurfaceFinish)i;
                RefreshFinishUi();
            }, "Shader category. Lure Plastic is translucent soft plastic with glitter; Metal is rust, patina, washes and antiqued metals; Clay is sculptor's oil clay - grey plasteline, terracotta and more.");

            _finishStatus = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);

            BuildLureGroup(section);
            BuildMetalGroup(section);
            BuildClayGroup(section);
            RefreshFinishUi();
        }

        private void BuildLureGroup(Transform section)
        {
            _lureGroup = CreateGroup(section, "LurePlastic");
            Transform group = _lureGroup.transform;

            _lureButtons.Clear();
            foreach (LurePlasticPreset preset in LurePlasticPresets.All)
                _lureButtons.Add(new KeyValuePair<string, Image>(preset.Id, null));
            BuildSwatchGrid(group, LurePlasticPresets.All.Count, i =>
            {
                LurePlasticPreset p = LurePlasticPresets.All[i];
                Image frame = CreateSwatchButton(p.Id, p.Name, p.Description, LurePlasticPresets.CreateThumbnail(p), () =>
                {
                    _material.SelectLurePreset(p.Id);
                    RefreshFinishUi();
                });
                _lureButtons[i] = new KeyValuePair<string, Image>(p.Id, frame);
                return frame.transform;
            });

            UIFactory.CreateLabel(group, "Flake Size", 12, FontStyle.Normal);
            _lureSizeSlider = UIFactory.CreateSlider(group, 0.25f, 3f, _material.LureFlakeSize, v => _material.LureFlakeSize = v,
                "Size of the glitter - 1 is the preset's own size.");
            UIFactory.CreateLabel(group, "Flake Amount", 12, FontStyle.Normal);
            _lureAmountSlider = UIFactory.CreateSlider(group, 0f, 2f, _material.LureFlakeAmount, v => _material.LureFlakeAmount = v,
                "How much glitter is packed into the plastic - 0 is clear plastic, 1 is the preset.");
            UIFactory.CreateLabel(group, "Sparkle", 12, FontStyle.Normal);
            _lureSparkleSlider = UIFactory.CreateSlider(group, 0f, 3f, _material.LureSparkle, v => _material.LureSparkle = v,
                "How brightly each flake flashes when it catches a light.");
            UIFactory.CreateLabel(group, "Translucency", 12, FontStyle.Normal);
            _lureTranslucencySlider = UIFactory.CreateSlider(group, 0.25f, 3f, _material.LureTranslucency, v => _material.LureTranslucency = v,
                "How far light gets into the plastic - higher lets thick parts show the lighter edge colour and deeper flakes.");
            UIFactory.CreateLabel(group, "Gloss", 12, FontStyle.Normal);
            _lureGlossSlider = UIFactory.CreateSlider(group, 0f, 1f, _material.LureGloss, v => _material.LureGloss = v,
                "Wetness of the plastic's surface - low is matte, high is a sharp shine.");
        }

        private void BuildMetalGroup(Transform section)
        {
            _metalGroup = CreateGroup(section, "Metal");
            Transform group = _metalGroup.transform;

            _metalButtons.Clear();
            foreach (MetalFinishPreset preset in MetalFinishPresets.All)
                _metalButtons.Add(new KeyValuePair<string, Image>(preset.Id, null));
            BuildSwatchGrid(group, MetalFinishPresets.All.Count, i =>
            {
                MetalFinishPreset p = MetalFinishPresets.All[i];
                Image frame = CreateSwatchButton(p.Id, p.Name, p.Description, MetalFinishPresets.CreateThumbnail(p), () =>
                {
                    _material.SelectMetalPreset(p.Id);
                    RefreshFinishUi();
                });
                _metalButtons[i] = new KeyValuePair<string, Image>(p.Id, frame);
                return frame.transform;
            });

            UIFactory.CreateLabel(group, "Exposed Metal", 12, FontStyle.Normal);
            _metalExposureSlider = UIFactory.CreateSlider(group, 0f, 2f, _material.MetalExposure, v => _material.MetalExposure = v,
                "How much bare metal shows through the rust / patina / paint in blotches - 1 is the preset, 0 is almost fully coated, 2 mostly bare.");
            UIFactory.CreateLabel(group, "Edge Wear", 12, FontStyle.Normal);
            _metalWearSlider = UIFactory.CreateSlider(group, 0f, 2f, _material.MetalEdgeWear, v => _material.MetalEdgeWear = v,
                "How far raised edges and high points are worn or dry-brushed back to bright metal.");
            UIFactory.CreateLabel(group, "Wash", 12, FontStyle.Normal);
            _metalWashSlider = UIFactory.CreateSlider(group, 0f, 1.5f, _material.MetalWash, v => _material.MetalWash = v,
                "How dark the wash pooled in the recesses is - 0 leaves recesses the coat colour.");
            UIFactory.CreateLabel(group, "Detail Contrast", 12, FontStyle.Normal);
            _metalDetailSlider = UIFactory.CreateSlider(group, 0.25f, 3f, _material.MetalDetail, v => _material.MetalDetail = v,
                "How strongly the carving drives wash and wear - low keeps them to the sharpest grooves and edges, high spreads them over gentler relief.");
            UIFactory.CreateLabel(group, "Pattern Size", 12, FontStyle.Normal);
            _metalPatternSlider = UIFactory.CreateSlider(group, 0.25f, 3f, _material.MetalPatternSize, v => _material.MetalPatternSize = v,
                "Size of the coat's patches and grain - 1 is the preset's own size.");
            UIFactory.CreateLabel(group, "Pattern Shift", 12, FontStyle.Normal);
            _metalSeedSlider = UIFactory.CreateSlider(group, 0f, 1f, _material.MetalPatternSeed, v => _material.MetalPatternSeed = v,
                "Moves the blotches around the model - drag to slide them, or Shuffle for a new layout.");
            UIFactory.CreateButton(group, "Shuffle Pattern", () =>
            {
                _material.ShuffleMetalPattern();
                RefreshFinishUi();
            }, "Jump to a random blotch layout.");
            UIFactory.CreateLabel(group, "Gloss", 12, FontStyle.Normal);
            _metalGlossSlider = UIFactory.CreateSlider(group, 0f, 2f, _material.MetalGloss, v => _material.MetalGloss = v,
                "Shine of the metal and coat - 1 is the preset, 0 is fully matte.");
        }

        private void BuildClayGroup(Transform section)
        {
            _clayGroup = CreateGroup(section, "Clay");
            Transform group = _clayGroup.transform;

            _clayButtons.Clear();
            foreach (ClayPreset preset in ClayPresets.All)
                _clayButtons.Add(new KeyValuePair<string, Image>(preset.Id, null));
            BuildSwatchGrid(group, ClayPresets.All.Count, i =>
            {
                ClayPreset p = ClayPresets.All[i];
                Image frame = CreateSwatchButton(p.Id, p.Name, p.Description, ClayPresets.CreateThumbnail(p), () =>
                {
                    _material.SelectClayPreset(p.Id);
                    RefreshFinishUi();
                });
                _clayButtons[i] = new KeyValuePair<string, Image>(p.Id, frame);
                return frame.transform;
            });

            UIFactory.CreateLabel(group, "Sheen", 12, FontStyle.Normal);
            _clayGlossSlider = UIFactory.CreateSlider(group, 0f, 2f, _material.ClayGloss, v => _material.ClayGloss = v,
                "The clay's satin sheen - 1 is the preset, 0 is dry and chalky, 2 polished.");
            UIFactory.CreateLabel(group, "Wet Highlights", 12, FontStyle.Normal);
            _clayWetSlider = UIFactory.CreateSlider(group, 0f, 2f, _material.ClayWetness, v => _material.ClayWetness = v,
                "The sharp greasy glints worked oil clay gets along every stroke - 0 turns them off.");
            UIFactory.CreateLabel(group, "Subsurface Glow", 12, FontStyle.Normal);
            _claySubsurfaceSlider = UIFactory.CreateSlider(group, 0f, 2f, _material.ClaySubsurface, v => _material.ClaySubsurface = v,
                "Warm light bleeding just past the shadow edge, as light does inside real clay - strongest on the terracottas.");
            UIFactory.CreateLabel(group, "Recess Depth", 12, FontStyle.Normal);
            _clayRecessSlider = UIFactory.CreateSlider(group, 0f, 2f, _material.ClayRecess, v => _material.ClayRecess = v,
                "How dark and rich the creases and folds go - 0 keeps one flat colour.");
            UIFactory.CreateLabel(group, "Detail Contrast", 12, FontStyle.Normal);
            _clayDetailSlider = UIFactory.CreateSlider(group, 0.25f, 3f, _material.ClayDetail, v => _material.ClayDetail = v,
                "How much relief counts as a recess - low keeps the dark colour to the deepest creases, high spreads it over gentle forms.");
            UIFactory.CreateLabel(group, "Grain", 12, FontStyle.Normal);
            _clayGrainSlider = UIFactory.CreateSlider(group, 0f, 3f, _material.ClayGrain, v => _material.ClayGrain = v,
                "Fine grit in the clay's surface, shading only - 0 is perfectly smooth.");
        }

        private static GameObject CreateGroup(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return go;
        }

        /// Two swatches per row; `create(i)` builds swatch i (unparented) and returns it.
        private static void BuildSwatchGrid(Transform parent, int count, Func<int, Transform> create)
        {
            Transform row = null;
            for (int i = 0; i < count; i++)
            {
                if (i % 2 == 0) row = UIFactory.CreateRow(parent, SwatchHeight).transform;
                create(i).SetParent(row, false);
            }
            if (count % 2 == 1)
                new GameObject("Spacer", typeof(RectTransform)).transform.SetParent(row, false);
        }

        private static Image CreateSwatchButton(string id, string name, string description, Texture2D thumb, Action onClick)
        {
            var go = new GameObject("Swatch_" + id, typeof(RectTransform), typeof(Image));
            var frame = go.GetComponent<Image>();
            frame.color = UIFactory.InactiveColor;
            var button = go.AddComponent<Button>();
            button.targetGraphic = frame;
            button.onClick.AddListener(() => onClick());
            TooltipSystem.Attach(go, description);

            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(go.transform, false);
            var iconRect = iconGO.GetComponent<RectTransform>();
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(4f, 0f);
            iconRect.sizeDelta = new Vector2(SwatchHeight - 8f, SwatchHeight - 8f);
            iconGO.GetComponent<Image>().sprite =
                Sprite.Create(thumb, new Rect(0, 0, thumb.width, thumb.height), new Vector2(0.5f, 0.5f));

            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(SwatchHeight, 2f);
            textRect.offsetMax = new Vector2(-3f, -2f);
            var text = textGO.AddComponent<Text>();
            text.font = UIFactory.Font;
            text.fontSize = 11;
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.color = Color.white;
            text.text = name;
            text.raycastTarget = false;

            return frame;
        }

        private ((bool, SurfaceFinish, string, string, string), (float, float, float, float, float),
                 (float, float, float, float, float, float, float), (float, float, float, float, float, float)) FinishStateSignature() =>
            ((_material.MatcapEnabled, _material.Finish, _material.LurePresetId, _material.MetalPresetId, _material.ClayPresetId),
             (_material.LureFlakeSize, _material.LureFlakeAmount, _material.LureSparkle, _material.LureTranslucency, _material.LureGloss),
             (_material.MetalExposure, _material.MetalEdgeWear, _material.MetalWash, _material.MetalDetail,
              _material.MetalPatternSize, _material.MetalGloss, _material.MetalPatternSeed),
             (_material.ClayGloss, _material.ClayWetness, _material.ClaySubsurface, _material.ClayRecess,
              _material.ClayDetail, _material.ClayGrain));

        private void RefreshFinishUi()
        {
            if (_finishDropdown == null) return;
            _shownFinishState = FinishStateSignature();

            bool matcapShowing = _material.MatcapEnabled && _material.HasMatcap;
            if (_finishMatcapNote.activeSelf != matcapShowing) _finishMatcapNote.SetActive(matcapShowing);

            SurfaceFinish finish = _material.Finish;
            _finishDropdown.SetValueWithoutNotify((int)finish);
            if (_lureGroup.activeSelf != (finish == SurfaceFinish.LurePlastic)) _lureGroup.SetActive(finish == SurfaceFinish.LurePlastic);
            if (_metalGroup.activeSelf != (finish == SurfaceFinish.Metal)) _metalGroup.SetActive(finish == SurfaceFinish.Metal);
            if (_clayGroup.activeSelf != (finish == SurfaceFinish.Clay)) _clayGroup.SetActive(finish == SurfaceFinish.Clay);

            _lureSizeSlider.SetValueWithoutNotify(_material.LureFlakeSize);
            _lureAmountSlider.SetValueWithoutNotify(_material.LureFlakeAmount);
            _lureSparkleSlider.SetValueWithoutNotify(_material.LureSparkle);
            _lureTranslucencySlider.SetValueWithoutNotify(_material.LureTranslucency);
            _lureGlossSlider.SetValueWithoutNotify(_material.LureGloss);
            _metalExposureSlider.SetValueWithoutNotify(_material.MetalExposure);
            _metalWearSlider.SetValueWithoutNotify(_material.MetalEdgeWear);
            _metalWashSlider.SetValueWithoutNotify(_material.MetalWash);
            _metalDetailSlider.SetValueWithoutNotify(_material.MetalDetail);
            _metalPatternSlider.SetValueWithoutNotify(_material.MetalPatternSize);
            _metalGlossSlider.SetValueWithoutNotify(_material.MetalGloss);
            _metalSeedSlider.SetValueWithoutNotify(_material.MetalPatternSeed);
            _clayGlossSlider.SetValueWithoutNotify(_material.ClayGloss);
            _clayWetSlider.SetValueWithoutNotify(_material.ClayWetness);
            _claySubsurfaceSlider.SetValueWithoutNotify(_material.ClaySubsurface);
            _clayRecessSlider.SetValueWithoutNotify(_material.ClayRecess);
            _clayDetailSlider.SetValueWithoutNotify(_material.ClayDetail);
            _clayGrainSlider.SetValueWithoutNotify(_material.ClayGrain);

            HighlightSwatches(_lureButtons, finish == SurfaceFinish.LurePlastic ? _material.LurePresetId : null);
            HighlightSwatches(_metalButtons, finish == SurfaceFinish.Metal ? _material.MetalPresetId : null);
            HighlightSwatches(_clayButtons, finish == SurfaceFinish.Clay ? _material.ClayPresetId : null);

            string presetName = finish == SurfaceFinish.LurePlastic ? LurePlasticPresets.Find(_material.LurePresetId)?.Name
                              : finish == SurfaceFinish.Metal ? MetalFinishPresets.Find(_material.MetalPresetId)?.Name
                              : finish == SurfaceFinish.Clay ? ClayPresets.Find(_material.ClayPresetId)?.Name
                              : null;
            if (finish == SurfaceFinish.None)
            {
                _finishStatus.text = "Off - plain Base Color. Pick a category above.";
                _finishStatus.color = UIFactory.StatusHintColor;
            }
            else if (matcapShowing)
            {
                _finishStatus.text = (presetName ?? "Surface shader") + " - hidden while matcap is on.";
                _finishStatus.color = UIFactory.StatusWarnColor;
            }
            else
            {
                _finishStatus.text = (presetName ?? "Surface shader") + " - replaces Base Color.";
                _finishStatus.color = UIFactory.StatusOkColor;
            }
        }

        private static void HighlightSwatches(List<KeyValuePair<string, Image>> buttons, string selectedId)
        {
            foreach (KeyValuePair<string, Image> pair in buttons)
                if (pair.Value != null)
                    pair.Value.color = pair.Key == selectedId ? UIFactory.ActiveColor : UIFactory.InactiveColor;
        }

        private void BuildMatcapSection(Transform section)
        {
            _matcapToggle = UIFactory.CreateToggle(section, "Enabled", _material.MatcapEnabled, v =>
            {
                _material.MatcapEnabled = v;
                RefreshMatcapUi();
            }, tooltip: "Replaces scene lighting with a pre-baked shading image, so the surface looks lit consistently without setting up scene lights.");

            _matcapStatus = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);

            UIFactory.CreateLabel(section, "Intensity", 12, FontStyle.Normal);
            UIFactory.CreateSlider(section, 0f, 3f, _material.MatcapIntensity, v => _material.MatcapIntensity = v,
                "Brightness of the matcap shading - 1 is as baked, higher brightens it.");

            // Named for what it does rather than "Tint": a matcap already carries a colour, and
            // this is specifically how much of the Base Color above gets multiplied through it.
            UIFactory.CreateLabel(section, "Tint By Base Color", 12, FontStyle.Normal);
            UIFactory.CreateSlider(section, 0f, 1f, _material.MatcapTintStrength, v => _material.MatcapTintStrength = v,
                "How much Base Color tints the matcap - 0 leaves it untouched, 1 fully tints it.");

            // Palette lives in its own container so Rescan can clear and refill just this part
            // of the section without disturbing the controls around it.
            var paletteGO = new GameObject("MatcapPalette", typeof(RectTransform));
            paletteGO.transform.SetParent(section, false);
            var vlg = paletteGO.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            paletteGO.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _paletteRoot = paletteGO.transform;
            BuildPalette();

            UIFactory.CreateButton(section, "Import Matcap...", ImportMatcap, "Add a matcap image file to the palette.");
            UIFactory.CreateButton(section, "Rescan Folder", RescanMatcaps, "Refresh the palette from the Matcaps folder on disk.");

            if (!FileDialog.IsSupported)
                UIFactory.CreateLabel(section, "No file picker - drop images in the Matcaps folder instead.",
                                      11, FontStyle.Italic);

            RefreshMatcapUi();
        }

        /// Rebuilds the thumbnail grid from whatever the library last found. Only lays the
        /// buttons out - the images themselves are decoded lazily by FillThumbnails, once the
        /// section is actually opened.
        private void BuildPalette()
        {
            for (int i = _paletteRoot.childCount - 1; i >= 0; i--)
                Destroy(_paletteRoot.GetChild(i).gameObject);
            _paletteButtons.Clear();
            _paletteFilled = false;

            IReadOnlyList<MatcapLibrary.Entry> entries = MatcapLibrary.Entries;
            if (entries.Count == 0)
            {
                Text empty = UIFactory.CreateLabel(_paletteRoot, "No matcaps found in the Matcaps folder.",
                                                   11, FontStyle.Italic);
                empty.color = UIFactory.StatusHintColor;
                return;
            }

            string category = null;
            Transform row = null;
            int inRow = 0;

            foreach (MatcapLibrary.Entry entry in entries)
            {
                // Entries arrive sorted by category, so a change of category is the heading.
                if (entry.Category != category)
                {
                    category = entry.Category;
                    Text heading = UIFactory.CreateLabel(_paletteRoot, category, 11, FontStyle.Bold);
                    heading.color = UIFactory.StatusHintColor;
                    row = null;
                    inRow = 0;
                }

                if (row == null || inRow == PaletteColumns)
                {
                    row = UIFactory.CreateRow(_paletteRoot, PaletteButtonHeight).transform;
                    inRow = 0;
                }

                _paletteButtons.Add(new KeyValuePair<string, Image>(entry.Path, CreateMatcapButton(row, entry)));
                inRow++;
            }

            // A part-full last row would otherwise stretch its buttons to fill the width, so the
            // final row's thumbnails end up wider than every other row's.
            if (row != null)
                for (int i = inRow; i < PaletteColumns; i++)
                    new GameObject("Spacer", typeof(RectTransform)).transform.SetParent(row, false);

            RefreshPaletteHighlight();
        }

        /// Square icon button showing the matcap itself - the same idea as the brush alpha
        /// palette, and the same one ZBrush/Nomad use: a matcap can only really be judged by
        /// looking at it, so a list of names would be useless.
        private Image CreateMatcapButton(Transform parent, MatcapLibrary.Entry entry)
        {
            var go = new GameObject("Matcap_" + entry.Name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var frame = go.GetComponent<Image>();
            frame.color = UIFactory.InactiveColor;
            var button = go.AddComponent<Button>();
            button.targetGraphic = frame;
            button.onClick.AddListener(() =>
            {
                // Picking one turns matcap shading on: clicking a matcap and having nothing
                // change is indistinguishable from the click not having registered. Goes through
                // the entry directly rather than by name - MatcapLibrary.Find only takes a name
                // and would resolve ambiguously if another entry elsewhere shares this one.
                _material.SetMatcap(entry);
                _selectedPath = entry.Path;
                RefreshMatcapUi();
            });

            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(go.transform, false);
            var iconRect = iconGO.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.1f, 0.1f);
            iconRect.anchorMax = new Vector2(0.9f, 0.9f);
            iconRect.sizeDelta = Vector2.zero;
            iconGO.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0f); // filled in by FillThumbnails

            return frame;
        }

        private void Update()
        {
            if (_material == null) return;

            // Loading a .sculpt file writes the material controller directly, with no route back
            // to this panel. Without this the toggle and the highlighted swatch would go on
            // describing the scene that was open before the load.
            if (_material.MatcapEnabled != _shownMatcapEnabled || _material.MatcapName != _shownMatcapName)
            {
                _shownMatcapEnabled = _material.MatcapEnabled;
                _shownMatcapName = _material.MatcapName;
                RefreshMatcapUi();
            }

            // Same for the surface shaders - and they also have to notice matcap going on or off,
            // which decides whether they can show at all.
            if (_finishDropdown != null && !FinishStateSignature().Equals(_shownFinishState))
                RefreshFinishUi();

            // The section starts collapsed, so the palette's GameObject starts inactive. Nothing
            // is decoded until it is first opened - opening it is the only signal available that
            // the user actually wants to look at matcaps, and decoding fifty images for a section
            // nobody opened is pure startup cost.
            if (_paletteFilled || _paletteRoot == null || !_paletteRoot.gameObject.activeInHierarchy) return;
            _paletteFilled = true;
            StartCoroutine(FillThumbnails());
        }

        private IEnumerator FillThumbnails()
        {
            int decoded = 0;
            foreach (MatcapLibrary.Entry entry in MatcapLibrary.Entries)
            {
                Image frame = FindPaletteButton(entry.Path);
                if (frame == null) continue;

                Texture2D thumbnail = MatcapLibrary.GetThumbnail(entry);
                if (thumbnail != null)
                {
                    var icon = frame.transform.GetChild(0).GetComponent<Image>();
                    icon.sprite = Sprite.Create(thumbnail, new Rect(0, 0, thumbnail.width, thumbnail.height),
                                                new Vector2(0.5f, 0.5f));
                    icon.color = Color.white;
                }

                if (++decoded % ThumbnailsPerFrame == 0) yield return null;
            }
        }

        private Image FindPaletteButton(string path)
        {
            foreach (KeyValuePair<string, Image> pair in _paletteButtons)
                if (string.Equals(pair.Key, path, StringComparison.OrdinalIgnoreCase))
                    return pair.Value != null ? pair.Value : null;
            return null;
        }

        private void RefreshPaletteHighlight()
        {
            foreach (KeyValuePair<string, Image> pair in _paletteButtons)
            {
                if (pair.Value == null) continue;
                bool selected = _material.MatcapEnabled &&
                                string.Equals(pair.Key, _selectedPath, StringComparison.OrdinalIgnoreCase);
                pair.Value.color = selected ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            }
        }

        /// True if the entry at `path` (as of the last scan) still has the given Name. Used to
        /// tell "the selection we recorded is still the one the material is showing" apart from
        /// "the material's name changed out from under us" (a scene load, a rescan that dropped
        /// the file, MatcapEnabled auto-picking the first entry) - only the latter needs to fall
        /// back to a name-only re-resolution.
        private static bool PathMatchesName(string path, string name)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (MatcapLibrary.Entry entry in MatcapLibrary.Entries)
                if (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                    return string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        private void RefreshMatcapUi()
        {
            _shownMatcapEnabled = _material.MatcapEnabled;
            _shownMatcapName = _material.MatcapName;

            // If the entry we last recorded no longer matches the controller's name, the change
            // came from somewhere that only had a name to give us (scene load, rescan, the
            // toggle's own auto-pick) - re-resolve by name as a best effort. When it still
            // matches (the common case: we just set it ourselves from a click or import), leave
            // it alone so a same-named entry in another category never overwrites the real pick.
            if (!PathMatchesName(_selectedPath, _material.MatcapName))
            {
                MatcapLibrary.Entry resolved = MatcapLibrary.Find(_material.MatcapName);
                _selectedPath = resolved?.Path;
            }

            if (_matcapToggle != null)
            {
                // Set through the field, not the property: the toggle's own onChange handler is
                // what put us here, and letting it fire again would loop.
                _matcapToggle.SetIsOnWithoutNotify(_material.MatcapEnabled);
            }

            if (_matcapStatus != null)
            {
                if (_material.MatcapEnabled && _material.HasMatcap)
                {
                    _matcapStatus.text = _material.MatcapName + " - replaces scene lighting.";
                    _matcapStatus.color = UIFactory.StatusOkColor;
                }
                else if (_material.MatcapEnabled)
                {
                    _matcapStatus.text = "Matcap image missing - pick one below.";
                    _matcapStatus.color = UIFactory.StatusWarnColor;
                }
                else
                {
                    _matcapStatus.text = "Off - lit by the scene lights. Cavity and mask apply either way.";
                    _matcapStatus.color = UIFactory.StatusHintColor;
                }
            }

            RefreshPaletteHighlight();
        }

        private void ImportMatcap()
        {
            if (!FileDialog.IsSupported)
            {
                SetMatcapError("No file picker available on this platform.");
                return;
            }

            string chosen = FileDialog.OpenFile("Import Matcap", _lastImportDirectory,
                                                "png", "jpg", "jpeg", "tga", "bmp");
            // Cancelling is a normal thing to do, not an error.
            if (string.IsNullOrEmpty(chosen)) return;
            _lastImportDirectory = FileDialog.DirectoryFor(chosen);

            MatcapLibrary.Entry imported = MatcapLibrary.Import(chosen, out string error);
            BuildPalette();
            if (imported == null)
            {
                SetMatcapError(error ?? "Could not import that image.");
                return;
            }

            // Select what was just imported. Importing a matcap and then having to hunt for it
            // in the palette is the kind of step that makes a feature feel unfinished. Direct
            // from the Entry, not by name - an import that happens to collide with an existing
            // entry's name must still select the file that was just imported, not that one.
            _material.SetMatcap(imported);
            _selectedPath = imported.Path;
            RefreshMatcapUi();
        }

        private void RescanMatcaps()
        {
            MatcapLibrary.Rescan();
            BuildPalette();
            // The selection may have just gone missing (its file deleted between scans), which
            // resolves by falling back to plain PBR shading rather than to a white surface.
            _material.RefreshMatcap();
            RefreshMatcapUi();
        }

        private void SetMatcapError(string message)
        {
            if (_matcapStatus == null) return;
            _matcapStatus.text = message;
            _matcapStatus.color = UIFactory.StatusWarnColor;
        }
    }
}
