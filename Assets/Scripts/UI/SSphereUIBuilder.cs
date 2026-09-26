using System;
using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// The SSphere blockout section of the Scene panel: edit-mode buttons, rig undo/redo, radius,
    /// symmetry and preview toggles, attach, Convert, and the rig's status lines.
    ///
    /// Split out of SceneGraphUIBuilder (about 300 lines of it), following the LatheUIBuilder/
    /// TurntableUIBuilder pattern: the host panel adds this component, calls BuildContent with the
    /// panel to build into, and hands over the three things the section needs from the panel -
    /// switching the tool mode, refreshing the tool buttons, and writing the panel's status line -
    /// as callbacks, so this class does not depend on the panel.
    public class SSphereUIBuilder : MonoBehaviour
    {
        private SelectionManager _selection;
        private TransformGizmo _gizmo;
        private Action<GizmoMode> _setToolMode;
        private Action _refreshToolButtons;
        private Action<string, bool> _panelStatus;
        private bool _built;

        /// The foldout the controls are built into - the host reveals it when SSpheres becomes the tool.
        public Transform Section { get; private set; }

        /// Builds the section into `panel`. `setToolMode` switches the gizmo tool (and refreshes the
        /// host's tool buttons); `refreshToolButtons` only refreshes them; `panelStatus` writes the
        /// host panel's status line (true = success, held; false = error).
        public void BuildContent(Transform panel, Action<GizmoMode> setToolMode, Action refreshToolButtons,
                                 Action<string, bool> panelStatus)
        {
            _selection = FindFirstObjectByType<SelectionManager>();
            _gizmo = FindFirstObjectByType<TransformGizmo>();
            _ssphere = FindFirstObjectByType<SSphereController>();
            _setToolMode = setToolMode;
            _refreshToolButtons = refreshToolButtons;
            _panelStatus = panelStatus;
            BuildSSphereSection(panel);
            _built = true;
        }

        private void Update()
        {
            if (_built) RefreshSSphereSection();
        }

        // SSphere blockout section - see BuildSSphereSection. Held as fields only for the parts
        // Update has to keep current: the mode highlight, the toggles that can also change from
        // the viewport (A, undo restoring symmetry), the radius slider, and the status lines.
        private SSphereController _ssphere;

        private readonly Image[] _ssphereModeImages = new Image[4];

        private Slider _ssphereRadiusSlider;

        private Toggle _ssphereSymmetryToggle;

        private Toggle _sspherePreviewToggle;

        private Text _ssphereStatusLabel;

        private Text _ssphereUndoLabel;

        private Text _ssphereAttachLabel;

        // One-off action results shown over the polled status line - see SetSSphereStatus.
        private const float SSphereStickySeconds = 4f;

        private string _ssphereSticky;

        private float _ssphereStickyUntil;

        // What the section was last built from, so RefreshSSphereSection is a no-op on the
        // overwhelmingly common frames where nothing changed.
        private bool _ssphereLabelsValid;

        private int _lastSSphereVersion = -1;

        private int _lastSSphereNode = -2;

        private string _lastSSphereUndoLabel;

        private int _lastSSphereUndoDepth = -1;

        private string _lastSSphereAttachName;

        private bool _lastSSphereSnap;

        private bool _lastSSphereArmed;

        private int _lastSSphereTriCount = -1;

        private bool _lastSSphereFinal;

        private string _lastSSphereError;

        private bool _lastSSphereStickyShowing;

        private bool _lastSSphereSymmetry;

        private bool _lastSSpherePreview;

        private SSphereEditMode _lastSSphereMode = (SSphereEditMode)(-1);

        /// Arms the SSphere tool and drops the first sphere in the middle of the view. Reports
        /// through the SSphere section's own status line rather than the panel's, since that is
        /// where the user's attention is being sent.
        public void StartSSphereRig()
        {
            if (_ssphere == null)
            {
                _panelStatus?.Invoke("No SSphereController in the scene.", false);
                return;
            }

            SetSSphereStatus(_ssphere.StartNewRig()
                ? "SSphere rig started. Drag off the sphere to grow the next one."
                : "A rig is already up - drag off a sphere, or Clear SSpheres to start over.");
            _refreshToolButtons?.Invoke();
            RefreshSSphereModeButtons();
        }

        // ----------------------------------------------------------------------------- sspheres

        /// The SSphere blockout controls. Inert until SSpheres is the active tool, and the status
        /// line says so. Deliberately short: the rebuilt rig derives symmetry and skin resolution
        /// for itself, so the knobs that only existed to work around the old one - centre snap,
        /// Mirror Rig Now, adaptive resolution, live preview, Update Skin - are gone, not hidden.
        private void BuildSSphereSection(Transform panel)
        {
            _ssphereLabelsValid = false;

            Transform foldout = UIFactory.CreateFoldoutSection(panel, "SSpheres - Shape Spheres (Blockout)", false);
            Section = foldout;

            if (_ssphere == null)
            {
                UIFactory.CreateLabel(foldout, "No SSphereController in scene.", 11, FontStyle.Italic);
                return;
            }

            GameObject modeRow = UIFactory.CreateRow(foldout, 24f);
            _ssphereModeImages[0] = UIFactory.CreateButton(modeRow.transform, "Draw", () => SetSSphereMode(SSphereEditMode.Draw),
                "Drag off a sphere to grow a new one; release and drag again to extend the chain. Drag a link to add a joint and bend it there.").GetComponent<Image>();
            _ssphereModeImages[1] = UIFactory.CreateButton(modeRow.transform, "Move", () => SetSSphereMode(SSphereEditMode.Move),
                "Drag a sphere to move it together with everything below it. Shift+drag moves only that sphere.").GetComponent<Image>();
            _ssphereModeImages[2] = UIFactory.CreateButton(modeRow.transform, "Scale", () => SetSSphereMode(SSphereEditMode.Scale),
                "Drag right or up to grow a sphere, left or down to shrink it. Shift+drag scales its whole branch.").GetComponent<Image>();
            _ssphereModeImages[3] = UIFactory.CreateButton(modeRow.transform, "Rotate", () => SetSSphereMode(SSphereEditMode.Rotate),
                "Swing a sphere and everything below it around its parent joint, keeping every length.").GetComponent<Image>();

            GameObject historyRow = UIFactory.CreateRow(foldout, 24f);
            UIFactory.CreateButton(historyRow.transform, "Undo", () =>
                SetSSphereStatus(_ssphere.UndoRig() ? "Undid the last SSphere edit." : "Nothing left to undo."),
                "Undoes the last rig edit (Z).");
            UIFactory.CreateButton(historyRow.transform, "Redo", () =>
                SetSSphereStatus(_ssphere.RedoRig() ? "Redid the last SSphere edit." : "Nothing to redo."),
                "Redoes the last undone rig edit (Shift+Z).");
            UIFactory.CreateButton(historyRow.transform, "Clear", () =>
            {
                int had = _ssphere.SphereCount;
                _ssphere.ClearRig();
                SetSSphereStatus(had == 0 ? "The rig is already empty." : $"Cleared {had} spheres. Undo (Z) brings them back.");
            }, "Removes every sphere. Undoable.");
            _ssphereUndoLabel = UIFactory.CreateLabel(foldout, string.Empty, 10, FontStyle.Italic);

            _ssphereSymmetryToggle = UIFactory.CreateToggle(foldout, "Symmetry (X)", _ssphere.SymmetryX,
                v => SetSSphereStatus(_ssphere.SetSymmetry(v)),
                tooltip: "Mirrors the whole rig live across the red plane. Turning it off keeps both halves as real spheres you can edit separately.");
            UIFactory.CreateToggle(foldout, "Show Skin", _ssphere.ShowSkin, v => _ssphere.ShowSkin = v,
                tooltip: "Shows the generated mesh as a see-through shell that updates live while you edit.");
            _sspherePreviewToggle = UIFactory.CreateToggle(foldout, "Solid Preview (A)", _ssphere.PreviewMode, v => _ssphere.PreviewMode = v,
                tooltip: "Shows the solid skin with the spheres hidden, to judge the final shape. Limbs can still be dragged.");

            UIFactory.CreateLabel(foldout, "Selected Sphere Radius", 12, FontStyle.Normal);
            _ssphereRadiusSlider = UIFactory.CreateSlider(foldout, SSphereController.MinNodeRadius, 2f,
                _ssphere.SelectedRadius, v => _ssphere.SelectedRadius = v,
                "Radius of the selected sphere. The mouse wheel over any sphere resizes it too.");

            UIFactory.CreateLabel(foldout, "New Sphere Size (of parent)", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0.2f, 1.5f, _ssphere.ChildTaper, v => _ssphere.ChildTaper = v,
                "Size of a newly drawn sphere relative to the sphere it grows from.");

            UIFactory.CreateLabel(foldout, "Skin Density", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, SSphereSkinner.MinDensity, SSphereSkinner.MaxDensity, _ssphere.Density,
                v => _ssphere.Density = v,
                "Mesh detail. 1 is always fine enough for the thinnest limb; higher is smoother but slower.");

            UIFactory.CreateLabel(foldout, "Joint Blend", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0f, 1f, _ssphere.Blend, v => _ssphere.Blend = v,
                "How softly limbs melt into each other where they meet.");

            UIFactory.CreateLabel(foldout, "Skin Smoothing", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0f, 8f, _ssphere.Smoothing, v => _ssphere.Smoothing = Mathf.RoundToInt(v),
                "Smoothing passes over the generated surface.");

            UIFactory.CreateLabel(foldout, "Attach to Object", 12, FontStyle.Normal);
            GameObject attachRow = UIFactory.CreateRow(foldout, 24f);
            UIFactory.CreateButton(attachRow.transform, "Attach Selected", () =>
            {
                SculptableMesh target = _selection != null ? _selection.PrimarySelection : null;
                if (target == null) { SetSSphereStatus("Select an object in the list first."); return; }
                SetSSphereStatus(_ssphere.AttachToObject(target)
                    ? $"Attached to {target.name}. Click its surface in Draw mode to start a limb there."
                    : "Could not attach to that object.");
            }, "Binds the rig to the selected object: clicks land on its surface, and the rig follows it.");
            UIFactory.CreateButton(attachRow.transform, "Detach", () =>
            {
                bool had = _ssphere.AttachTarget != null;
                _ssphere.DetachFromObject();
                SetSSphereStatus(had ? "Detached. Spheres left where they are." : "Nothing was attached.");
            }, "Unbinds the rig. Spheres stay where they are.");
            UIFactory.CreateToggle(foldout, "Snap Spheres to Surface", _ssphere.SnapToSurface,
                v => _ssphere.SnapToSurface = v, tooltip: "Keeps placed and moved spheres on the attached object's surface.");
            _ssphereAttachLabel = UIFactory.CreateLabel(foldout, string.Empty, 10, FontStyle.Italic);
            UIFactory.CreateButton(foldout, "Re-centre Mirror Plane", () =>
                SetSSphereStatus(_ssphere.ReanchorSymmetryPlane()
                    ? "Mirror plane moved onto the sculpt object. Spheres unchanged."
                    : "No sculpt object to centre on."),
                "Moves the mirror plane onto the selected or attached object without moving any sphere.");

            UIFactory.CreateToggle(foldout, "Keep Rig After Convert", _ssphere.KeepRigOnConvert,
                v => _ssphere.KeepRigOnConvert = v, tooltip: "Leaves the rig in place after converting, to keep iterating on it.");
            UIFactory.CreateButton(foldout, "Convert to Sculpt Mesh", ConvertSSpheres, "Bakes the skin into a real sculptable mesh object.");

            _ssphereStatusLabel = UIFactory.CreateLabel(foldout, string.Empty, 11, FontStyle.Italic);

            UIFactory.CreateLabel(foldout,
                "Draw: drag off a sphere to grow a limb, then drag off the new one to keep going.\n" +
                "Drag a link to add a joint and bend the limb there.\n" +
                "Any mode: wheel over a sphere resizes it; right-click deletes it and its branch.\n" +
                "Move carries the whole branch (Shift: one sphere). Scale with Shift: whole branch.\n" +
                "Esc cancels a drag. A toggles the solid preview. Z / Shift+Z undo and redo.",
                10, FontStyle.Italic);

            RefreshSSphereModeButtons();
        }

        private void SetSSphereMode(SSphereEditMode mode)
        {
            if (_ssphere == null) return;
            _ssphere.EditMode = mode;
            // Picking a mode is an unambiguous statement of intent, so it arms the tool too.
            _setToolMode?.Invoke(GizmoMode.SSphere);
            RefreshSSphereModeButtons();
        }

        private void RefreshSSphereModeButtons()
        {
            if (_ssphere == null) return;
            for (int i = 0; i < _ssphereModeImages.Length; i++)
            {
                if (_ssphereModeImages[i] == null) continue;
                _ssphereModeImages[i].color = (int)_ssphere.EditMode == i ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            }
        }

        /// Shows a one-off result on the SSphere status line for a few seconds. Needs a timeout
        /// because RefreshSSphereSection rewrites that label from polled state.
        private void SetSSphereStatus(string message)
        {
            _ssphereSticky = message;
            _ssphereStickyUntil = Time.unscaledTime + SSphereStickySeconds;
        }

        private void RefreshSSphereSection()
        {
            if (_ssphere == null || _ssphereStatusLabel == null) return;

            bool stickyShowing = _ssphereSticky != null && Time.unscaledTime < _ssphereStickyUntil;
            string undoLabel = _ssphere.NextRigUndoLabel;
            int undoDepth = _ssphere.RigUndoDepth;
            string attachName = _ssphere.AttachTargetName;
            bool snap = _ssphere.SnapToSurface;
            bool armed = _gizmo != null && _gizmo.Mode == GizmoMode.SSphere;
            int triCount = _ssphere.PreviewTriangleCount;
            bool final = !_ssphere.SkinIsDraft;
            string error = _ssphere.Error;
            int node = _ssphere.SelectedNode;
            int version = _ssphere.Rig.Version;
            bool symmetry = _ssphere.SymmetryX;
            bool preview = _ssphere.PreviewMode;
            SSphereEditMode mode = _ssphere.EditMode;

            if (_ssphereLabelsValid
                && undoDepth == _lastSSphereUndoDepth && undoLabel == _lastSSphereUndoLabel
                && attachName == _lastSSphereAttachName && snap == _lastSSphereSnap
                && armed == _lastSSphereArmed && triCount == _lastSSphereTriCount
                && final == _lastSSphereFinal && error == _lastSSphereError
                && stickyShowing == _lastSSphereStickyShowing && node == _lastSSphereNode
                && version == _lastSSphereVersion && symmetry == _lastSSphereSymmetry
                && preview == _lastSSpherePreview && mode == _lastSSphereMode)
                return;

            _ssphereLabelsValid = true;
            _lastSSphereUndoDepth = undoDepth;
            _lastSSphereUndoLabel = undoLabel;
            _lastSSphereAttachName = attachName;
            _lastSSphereSnap = snap;
            _lastSSphereArmed = armed;
            _lastSSphereTriCount = triCount;
            _lastSSphereFinal = final;
            _lastSSphereError = error;
            _lastSSphereStickyShowing = stickyShowing;
            _lastSSphereNode = node;
            _lastSSphereVersion = version;
            _lastSSphereSymmetry = symmetry;
            _lastSSpherePreview = preview;
            _lastSSphereMode = mode;

            // These can change from outside the panel - A in the viewport, an undo restoring
            // symmetry - and WithoutNotify so reflecting them never feeds back into the controller.
            if (_ssphereSymmetryToggle != null) _ssphereSymmetryToggle.SetIsOnWithoutNotify(symmetry);
            if (_sspherePreviewToggle != null) _sspherePreviewToggle.SetIsOnWithoutNotify(preview);
            RefreshSSphereModeButtons();

            // WithoutNotify: writing the slider normally would fire straight back into
            // SelectedRadius, so merely selecting a sphere would resize it to the slider's value.
            if (_ssphereRadiusSlider != null && _ssphere.SelectedRadius > 0f)
                _ssphereRadiusSlider.SetValueWithoutNotify(_ssphere.SelectedRadius);

            if (_ssphereUndoLabel != null)
            {
                _ssphereUndoLabel.text = undoLabel == null
                    ? "Rig history empty."
                    : $"Undo (Z): {undoLabel} - {undoDepth} step(s) held.";
                _ssphereUndoLabel.color = UIFactory.StatusHintColor;
            }

            if (_ssphereAttachLabel != null)
            {
                _ssphereAttachLabel.text = attachName == null
                    ? "Not attached - spheres land on the view plane."
                    : snap
                        ? $"Attached to {attachName} - clicks land on its surface."
                        : $"Attached to {attachName} - surface snap is off.";
                _ssphereAttachLabel.color = attachName == null ? UIFactory.StatusHintColor : UIFactory.StatusOkColor;
            }

            if (stickyShowing)
            {
                _ssphereStatusLabel.text = _ssphereSticky;
                _ssphereStatusLabel.color = UIFactory.StatusOkColor;
                return;
            }
            _ssphereSticky = null;

            int spheres = _ssphere.SphereCount;
            if (!armed)
            {
                _ssphereStatusLabel.text = spheres > 0
                    ? $"Rig hidden ({spheres} spheres). Pick SSpheres in Tool to edit it."
                    : "Pick SSpheres in Tool (or Add > SSphere Rig), then click in the viewport.";
                _ssphereStatusLabel.color = UIFactory.StatusHintColor;
            }
            else if (spheres == 0)
            {
                _ssphereStatusLabel.text = "Click in the viewport to place the first sphere - drag to size it.";
                _ssphereStatusLabel.color = UIFactory.StatusHintColor;
            }
            else if (error != null)
            {
                _ssphereStatusLabel.text = error;
                _ssphereStatusLabel.color = UIFactory.StatusErrorColor;
            }
            else if (triCount > 0)
            {
                _ssphereStatusLabel.text = $"{spheres} spheres | skin {triCount:N0} tris{(final ? string.Empty : " (live draft)")}";
                _ssphereStatusLabel.color = UIFactory.StatusOkColor;
            }
            else
            {
                _ssphereStatusLabel.text = $"{spheres} spheres.";
                _ssphereStatusLabel.color = UIFactory.StatusHintColor;
            }
        }

        private void ConvertSSpheres()
        {
            if (_ssphere == null) return;
            SculptableMesh created = _ssphere.ConvertToSculptMesh();
            if (created != null)
            {
                _panelStatus?.Invoke("Skinned SSpheres into " + created.name, true);
                _refreshToolButtons?.Invoke();
            }
            else
            {
                _panelStatus?.Invoke(_ssphere.Error ?? "Nothing to skin.", false);
            }
        }
    }
}
