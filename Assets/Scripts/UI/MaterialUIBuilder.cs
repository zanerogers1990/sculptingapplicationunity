using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Sculpting.IO;

namespace Sculpting
{
    /// Builds the "Material" section: base PBR sliders (color, metallic, smoothness, normal
    /// detail), the single-colour cavity recess controls, and the matcap palette, all wired
    /// directly to SculptMaterialController.
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

        private static readonly Color ErrorColor = new Color(0.95f, 0.65f, 0.4f);
        private static readonly Color InfoColor = new Color(0.65f, 0.65f, 0.7f);

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
            UIFactory.CreateLabel(cavity, "Cavity Intensity", 12, FontStyle.Normal);
            UIFactory.CreateSlider(cavity, 0f, 2f, _material.CavityIntensity, v => _material.CavityIntensity = v);
            UIFactory.CreateLabel(cavity, "Cavity Range", 12, FontStyle.Normal);
            UIFactory.CreateSlider(cavity, 0.05f, 0.6f, _material.CavityRange, v => _material.CavityRange = v);

            BuildMatcapSection(UIFactory.CreateFoldoutSection(panel, "Matcap", false));
        }

        private void BuildMatcapSection(Transform section)
        {
            _matcapToggle = UIFactory.CreateToggle(section, "Enabled", _material.MatcapEnabled, v =>
            {
                _material.MatcapEnabled = v;
                RefreshMatcapUi();
            });

            _matcapStatus = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);

            UIFactory.CreateLabel(section, "Intensity", 12, FontStyle.Normal);
            UIFactory.CreateSlider(section, 0f, 3f, _material.MatcapIntensity, v => _material.MatcapIntensity = v);

            // Named for what it does rather than "Tint": a matcap already carries a colour, and
            // this is specifically how much of the Base Color above gets multiplied through it.
            UIFactory.CreateLabel(section, "Tint By Base Color", 12, FontStyle.Normal);
            UIFactory.CreateSlider(section, 0f, 1f, _material.MatcapTintStrength, v => _material.MatcapTintStrength = v);

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

            UIFactory.CreateButton(section, "Import Matcap...", ImportMatcap);
            UIFactory.CreateButton(section, "Rescan Folder", RescanMatcaps);

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
                empty.color = InfoColor;
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
                    heading.color = InfoColor;
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
                    _matcapStatus.color = new Color(0.55f, 0.85f, 0.55f);
                }
                else if (_material.MatcapEnabled)
                {
                    _matcapStatus.text = "Matcap image missing - pick one below.";
                    _matcapStatus.color = ErrorColor;
                }
                else
                {
                    _matcapStatus.text = "Off - lit by the scene lights. Cavity and mask apply either way.";
                    _matcapStatus.color = InfoColor;
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
            _matcapStatus.color = ErrorColor;
        }
    }
}
