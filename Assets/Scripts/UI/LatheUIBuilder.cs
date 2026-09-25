using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// The Lathe section of the right-hand Scene panel. The shaping itself happens in the
    /// viewport (see LatheController.Input); this section is for starting shapes, the knobs that
    /// have no natural gesture (detail, closure, overall size), and Create.
    ///
    /// Filled into a foldout by SceneGraphUIBuilder and self-installed there, the same way the
    /// Turntable and Mold Maker sections are; installs the LatheController itself for the same
    /// reason. The foldout opens by itself whenever the lathe becomes the active tool, since that
    /// is exactly when its controls are needed.
    public class LatheUIBuilder : MonoBehaviour
    {
        private static readonly Color WarnColor = new Color(0.98f, 0.72f, 0.35f);

        private const float StickySeconds = 5f;

        private LatheController _lathe;
        private TransformGizmo _gizmo;
        private Transform _section;

        private Text _editLabel, _statusLabel, _statsLabel, _undoLabel;
        private Image _editImage;
        private Text _segmentsLabel, _radiusLabel, _heightLabel, _scaleLabel, _cornerLabel;
        private Slider _segmentsSlider, _radiusSlider, _heightSlider, _scaleSlider;
        private Toggle _loopToggle, _capToggle, _evenToggle;

        private int _shownStatusVersion = -1;
        private float _stickyUntil;
        private bool _wasActive;

        public void BuildContent(Transform section)
        {
            _lathe = LatheController.Install();
            _gizmo = FindFirstObjectByType<TransformGizmo>();
            _section = section;

            GameObject topRow = UIFactory.CreateRow(section, 26f);
            Button edit = UIFactory.CreateButton(topRow.transform, "Edit Profile", ToggleEditing,
                "Shows the lathe in the viewport and hands it the mouse. Drag the handles on the " +
                "outline to shape it; the solid updates as you drag.");
            _editLabel = edit.GetComponentInChildren<Text>();
            _editImage = edit.GetComponent<Image>();
            UIFactory.CreateButton(topRow.transform, "Create Mesh", Create,
                "Turns the shape into a real sculptable object (Enter). The profile is kept, so you " +
                "can make variations.");

            // Three lines tall - labels are sized by their initial line count (see the help below).
            _statusLabel = UIFactory.CreateLabel(section, "\n\n", 11, FontStyle.Italic);

            UIFactory.CreateLabel(section, "Start From", 12, FontStyle.Normal);
            GameObject presetRow1 = UIFactory.CreateRow(section, 24f);
            GameObject presetRow2 = UIFactory.CreateRow(section, 24f);
            for (int i = 0; i < LathePresets.All.Length; i++)
            {
                LathePreset preset = LathePresets.All[i];
                UIFactory.CreateButton(i < 3 ? presetRow1.transform : presetRow2.transform, preset.ToString(),
                    () => { Arm(); _lathe.ApplyPreset(preset); },
                    $"Replaces the profile with a {preset.ToString().ToLowerInvariant()} to shape from. Undoable (Z).");
            }

            GameObject editRow = UIFactory.CreateRow(section, 24f);
            UIFactory.CreateButton(editRow.transform, "Draw New", () => { Arm(); _lathe.ClearProfile(); },
                "Empties the profile so you can draw your own: click beside the dashed axis to place " +
                "points from bottom to top, or Ctrl-drag to sketch the outline in one stroke.");
            UIFactory.CreateButton(editRow.transform, "Undo", () => _lathe.Undo(), "Undoes the last profile edit (Z).");
            UIFactory.CreateButton(editRow.transform, "Redo", () => _lathe.Redo(), "Redoes it (Shift+Z).");
            _undoLabel = UIFactory.CreateLabel(section, string.Empty, 10, FontStyle.Italic);
            _undoLabel.color = UIFactory.StatusHintColor;

            _loopToggle = UIFactory.CreateToggle(section, "Closed Loop (ring)", _lathe.ClosedLoop,
                v => _lathe.ClosedLoop = v,
                tooltip: "Joins the last point back to the first. A loop that stays clear of the axis " +
                         "revolves into a ring or torus.");
            _capToggle = UIFactory.CreateToggle(section, "Cap Open Ends", _lathe.CapEnds, v => _lathe.CapEnds = v,
                tooltip: "Closes an end that stops short of the axis with a flat disc, so the result is a " +
                         "watertight solid - what sculpting, Boolean and the mold maker want. Off leaves " +
                         "it open, like a tube.");
            _evenToggle = UIFactory.CreateToggle(section, "Even Triangles", _lathe.EvenTriangles,
                v => _lathe.EvenTriangles = v,
                tooltip: "Uses fewer vertices on rings near the axis so every triangle is about the same " +
                         "size - no slivers at the poles, which brushes handle much better.");

            _segmentsLabel = UIFactory.CreateLabel(section, string.Empty, 12, FontStyle.Normal);
            _segmentsSlider = UIFactory.CreateSlider(section, LatheMeshBuilder.MinSegments, LatheMeshBuilder.MaxSegments,
                _lathe.RadialSegments, v => _lathe.RadialSegments = Mathf.RoundToInt(v),
                "Vertices around the widest part - the level of detail. The profile is divided to " +
                "match, so faces stay square.");

            _radiusLabel = UIFactory.CreateLabel(section, string.Empty, 12, FontStyle.Normal);
            _radiusSlider = UIFactory.CreateSlider(section, 0.02f, 3f, Mathf.Max(0.02f, _lathe.Radius),
                v => _lathe.Radius = v, "Radius of the widest point. Stretches the outline sideways, keeping its shape.");

            _heightLabel = UIFactory.CreateLabel(section, string.Empty, 12, FontStyle.Normal);
            _heightSlider = UIFactory.CreateSlider(section, 0.02f, 5f, Mathf.Max(0.02f, _lathe.Height),
                v => _lathe.Height = v, "Height of the shape. Stretches the outline up from its base.");

            _scaleLabel = UIFactory.CreateLabel(section, string.Empty, 12, FontStyle.Normal);
            _scaleSlider = UIFactory.CreateSlider(section, LatheController.MinScale, LatheController.MaxScale, _lathe.Scale,
                v => _lathe.Scale = v, "Overall size - scales everything together.");

            UIFactory.CreateLabel(section, "Selected Point", 12, FontStyle.Normal);
            GameObject pointRow = UIFactory.CreateRow(section, 24f);
            Button corner = UIFactory.CreateButton(pointRow.transform, "Make Corner", () => _lathe.ToggleSelectedSharp(),
                "Switches the selected point between smooth and a sharp corner (double-click a handle does the same).");
            _cornerLabel = corner.GetComponentInChildren<Text>();
            UIFactory.CreateButton(pointRow.transform, "Delete Point", () => _lathe.DeleteSelectedPoint(),
                "Removes the selected point (Delete, or right-click a handle).");

            UIFactory.CreateButton(section, "Move Axis to View Centre", () => { Arm(); _lathe.MoveAxisToViewCentre(); },
                "Moves the lathe's axis to the middle of the view, keeping the shape - to build it " +
                "where it will be used.");

            _statsLabel = UIFactory.CreateLabel(section, "\n\n", 11, FontStyle.Normal);

            // One short line per gesture: labels here are sized by their line count and do not
            // grow, so a long line that wraps would be cut off.
            UIFactory.CreateLabel(section,
                "Drag a handle: reshape (Shift: straight)\n" +
                "Click the curve: add a point\n" +
                "Click beside the axis: extend the outline\n" +
                "Ctrl-drag: sketch a whole new outline\n" +
                "Double-click a handle: corner / smooth\n" +
                "Right-click a handle: delete it\n" +
                "Drop an end on the axis to close it\n" +
                "Alt-drag orbits. Esc cancels a drag.\n" +
                "Z / Shift+Z undo. Enter creates the mesh.",
                10, FontStyle.Italic);

            Refresh(true);
        }

        private void Update() => Refresh(false);

        private void Arm()
        {
            if (_gizmo == null) _gizmo = FindFirstObjectByType<TransformGizmo>();
            if (_gizmo != null && _gizmo.Mode != GizmoMode.Lathe) _gizmo.SetMode(GizmoMode.Lathe);
        }

        private void ToggleEditing()
        {
            if (_gizmo == null) _gizmo = FindFirstObjectByType<TransformGizmo>();
            if (_gizmo == null) return;
            _gizmo.SetMode(_gizmo.Mode == GizmoMode.Lathe ? GizmoMode.Sculpt : GizmoMode.Lathe);
        }

        private void Create()
        {
            Arm();
            _lathe.CreateMesh();
        }

        /// Opens this section's foldout if it is closed - by clicking its own header, so the
        /// header's open/closed arrow stays right. The header is the sibling just above the content.
        private void RevealSection()
        {
            if (_section == null || _section.gameObject.activeSelf) return;
            int index = _section.GetSiblingIndex();
            if (index == 0) return;
            Button header = _section.parent.GetChild(index - 1).GetComponent<Button>();
            if (header != null) header.onClick.Invoke();
        }

        private void Refresh(bool force)
        {
            if (_lathe == null || _statusLabel == null) return;

            bool active = _lathe.IsActive;
            if (force || active != _wasActive)
            {
                _wasActive = active;
                _editLabel.text = active ? "Done Editing" : "Edit Profile";
                _editImage.color = active ? UIFactory.ActiveColor : UIFactory.InactiveColor;
                if (active) RevealSection();
            }

            // Everything below only matters while the section is open.
            if (!force && (_section == null || !_section.gameObject.activeInHierarchy)) return;

            SyncToggle(_loopToggle, _lathe.ClosedLoop);
            SyncToggle(_capToggle, _lathe.CapEnds);
            SyncToggle(_evenToggle, _lathe.EvenTriangles);

            // WithoutNotify: writing a slider normally fires straight back into the setter.
            SyncSlider(_segmentsSlider, _lathe.RadialSegments);
            SyncSlider(_radiusSlider, _lathe.Radius);
            SyncSlider(_heightSlider, _lathe.Height);
            SyncSlider(_scaleSlider, _lathe.Scale);

            SetText(_segmentsLabel, $"Radial Segments: {_lathe.RadialSegments}");
            SetText(_radiusLabel, $"Radius: {_lathe.Radius:0.###}");
            SetText(_heightLabel, $"Height: {_lathe.Height:0.###}");
            SetText(_scaleLabel, $"Scale: {_lathe.Scale:0.00}x");
            SetText(_cornerLabel, _lathe.SelectedIsSharp ? "Make Smooth" : "Make Corner");

            string undo = _lathe.NextUndoLabel;
            SetText(_undoLabel, undo == null ? "Profile history empty." : $"Undo (Z): {undo}");

            RefreshStats();
            RefreshStatus(force);
        }

        private void RefreshStats()
        {
            LatheBuildResult build = _lathe.LastBuild;
            string text;
            Color color;
            if (!_lathe.HasMesh)
            {
                text = build.Error ?? "No shape yet.";
                color = UIFactory.StatusHintColor;
            }
            else
            {
                string closure = build.Watertight ? "closed solid" : "open surface - ends not closed";
                text = $"{build.VertexCount:N0} verts | {build.TriangleCount:N0} tris | {closure}" +
                       (_lathe.IsDraft ? " (draft while dragging)" : string.Empty);
                color = build.Watertight ? UIFactory.StatusOkColor : WarnColor;
                if (build.SelfIntersecting)
                {
                    text += "\nThe outline crosses itself, so the surface passes through itself.";
                    color = UIFactory.StatusErrorColor;
                }
                else if (build.TouchesAxis)
                {
                    text += "\nThe curve swings across the axis between points - held just off it (a thin neck).";
                    color = WarnColor;
                }
            }
            SetText(_statsLabel, text);
            if (_statsLabel.color != color) _statsLabel.color = color;
        }

        private void RefreshStatus(bool force)
        {
            if (_lathe.StatusVersion != _shownStatusVersion)
            {
                _shownStatusVersion = _lathe.StatusVersion;
                if (!string.IsNullOrEmpty(_lathe.Status))
                {
                    SetText(_statusLabel, _lathe.Status);
                    _statusLabel.color = UIFactory.StatusOkColor;
                    _stickyUntil = Time.unscaledTime + StickySeconds;
                    return;
                }
            }
            if (!force && Time.unscaledTime < _stickyUntil) return;

            string hint = _lathe.IsActive
                ? "Shaping - drag the handles on the outline in the viewport."
                : "Pick Edit Profile (or Add Primitive > Lathe) to shape a turned solid.";
            SetText(_statusLabel, hint);
            _statusLabel.color = UIFactory.StatusHintColor;
        }

        private static void SyncToggle(Toggle toggle, bool value)
        {
            if (toggle != null && toggle.isOn != value) toggle.SetIsOnWithoutNotify(value);
        }

        private static void SyncSlider(Slider slider, float value)
        {
            if (slider == null || Mathf.Approximately(slider.value, value)) return;
            slider.SetValueWithoutNotify(value);
        }

        private static void SetText(Text label, string text)
        {
            if (label != null && label.text != text) label.text = text;
        }
    }
}
