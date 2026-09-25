using System;
using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// The ZSphere blockout section of the Scene panel: edit-mode buttons, rig undo/redo, radius,
    /// symmetry and preview toggles, attach, Convert, and the rig's status lines.
    ///
    /// Split out of SceneGraphUIBuilder (about 300 lines of it), following the LatheUIBuilder/
    /// TurntableUIBuilder pattern: the host panel adds this component, calls BuildContent with the
    /// panel to build into, and hands over the three things the section needs from the panel -
    /// switching the tool mode, refreshing the tool buttons, and writing the panel's status line -
    /// as callbacks, so this class does not depend on the panel.
    public class ZSphereUIBuilder : MonoBehaviour
    {
        private SelectionManager _selection;
        private TransformGizmo _gizmo;
        private Action<GizmoMode> _setToolMode;
        private Action _refreshToolButtons;
        private Action<string, bool> _panelStatus;
        private bool _built;

        /// Builds the section into `panel`. `setToolMode` switches the gizmo tool (and refreshes the
        /// host's tool buttons); `refreshToolButtons` only refreshes them; `panelStatus` writes the
        /// host panel's status line (true = success, held; false = error).
        public void BuildContent(Transform panel, Action<GizmoMode> setToolMode, Action refreshToolButtons,
                                 Action<string, bool> panelStatus)
        {
            _selection = FindFirstObjectByType<SelectionManager>();
            _gizmo = FindFirstObjectByType<TransformGizmo>();
            _zsphere = FindFirstObjectByType<ZSphereController>();
            _setToolMode = setToolMode;
            _refreshToolButtons = refreshToolButtons;
            _panelStatus = panelStatus;
            BuildZSphereSection(panel);
            _built = true;
        }

        private void Update()
        {
            if (_built) RefreshZSphereSection();
        }

        // ZSphere blockout section - see BuildZSphereSection. Held as fields only for the parts
        // Update has to keep current: the mode highlight, the toggles that can also change from
        // the viewport (A, undo restoring symmetry), the radius slider, and the status lines.
        private ZSphereController _zsphere;

        private readonly Image[] _zsphereModeImages = new Image[4];

        private Slider _zsphereRadiusSlider;

        private Toggle _zsphereSymmetryToggle;

        private Toggle _zspherePreviewToggle;

        private Text _zsphereStatusLabel;

        private Text _zsphereUndoLabel;

        private Text _zsphereAttachLabel;

        // One-off action results shown over the polled status line - see SetZSphereStatus.
        private const float ZSphereStickySeconds = 4f;

        private string _zsphereSticky;

        private float _zsphereStickyUntil;

        // What the section was last built from, so RefreshZSphereSection is a no-op on the
        // overwhelmingly common frames where nothing changed.
        private bool _zsphereLabelsValid;

        private int _lastZSphereVersion = -1;

        private int _lastZSphereNode = -2;

        private string _lastZSphereUndoLabel;

        private int _lastZSphereUndoDepth = -1;

        private string _lastZSphereAttachName;

        private bool _lastZSphereSnap;

        private bool _lastZSphereArmed;

        private int _lastZSphereTriCount = -1;

        private bool _lastZSphereFinal;

        private string _lastZSphereError;

        private bool _lastZSphereStickyShowing;

        private bool _lastZSphereSymmetry;

        private bool _lastZSpherePreview;

        private ZSphereEditMode _lastZSphereMode = (ZSphereEditMode)(-1);

        /// Arms the ZSphere tool and drops the first sphere in the middle of the view. Reports
        /// through the ZSphere section's own status line rather than the panel's, since that is
        /// where the user's attention is being sent.
        public void StartZSphereRig()
        {
            if (_zsphere == null)
            {
                _panelStatus?.Invoke("No ZSphereController in the scene.", false);
                return;
            }

            SetZSphereStatus(_zsphere.StartNewRig()
                ? "ZSphere rig started. Drag off the sphere to grow the next one."
                : "A rig is already up - drag off a sphere, or Clear ZSpheres to start over.");
            _refreshToolButtons?.Invoke();
            RefreshZSphereModeButtons();
        }

        // ----------------------------------------------------------------------------- zspheres

        /// The ZSphere blockout controls. Inert until ZSpheres is the active tool, and the status
        /// line says so. Deliberately short: the rebuilt rig derives symmetry and skin resolution
        /// for itself, so the knobs that only existed to work around the old one - centre snap,
        /// Mirror Rig Now, adaptive resolution, live preview, Update Skin - are gone, not hidden.
        private void BuildZSphereSection(Transform panel)
        {
            _zsphereLabelsValid = false;

            Transform foldout = UIFactory.CreateFoldoutSection(panel, "ZSpheres (Blockout)", false);

            if (_zsphere == null)
            {
                UIFactory.CreateLabel(foldout, "No ZSphereController in scene.", 11, FontStyle.Italic);
                return;
            }

            GameObject modeRow = UIFactory.CreateRow(foldout, 24f);
            _zsphereModeImages[0] = UIFactory.CreateButton(modeRow.transform, "Draw", () => SetZSphereMode(ZSphereEditMode.Draw),
                "Drag off a sphere to grow a new one; release and drag again to extend the chain. Drag a link to add a joint and bend it there.").GetComponent<Image>();
            _zsphereModeImages[1] = UIFactory.CreateButton(modeRow.transform, "Move", () => SetZSphereMode(ZSphereEditMode.Move),
                "Drag a sphere to move it together with everything below it. Shift+drag moves only that sphere.").GetComponent<Image>();
            _zsphereModeImages[2] = UIFactory.CreateButton(modeRow.transform, "Scale", () => SetZSphereMode(ZSphereEditMode.Scale),
                "Drag right or up to grow a sphere, left or down to shrink it. Shift+drag scales its whole branch.").GetComponent<Image>();
            _zsphereModeImages[3] = UIFactory.CreateButton(modeRow.transform, "Rotate", () => SetZSphereMode(ZSphereEditMode.Rotate),
                "Swing a sphere and everything below it around its parent joint, keeping every length.").GetComponent<Image>();

            GameObject historyRow = UIFactory.CreateRow(foldout, 24f);
            UIFactory.CreateButton(historyRow.transform, "Undo", () =>
                SetZSphereStatus(_zsphere.UndoRig() ? "Undid the last ZSphere edit." : "Nothing left to undo."),
                "Undoes the last rig edit (Z).");
            UIFactory.CreateButton(historyRow.transform, "Redo", () =>
                SetZSphereStatus(_zsphere.RedoRig() ? "Redid the last ZSphere edit." : "Nothing to redo."),
                "Redoes the last undone rig edit (Shift+Z).");
            UIFactory.CreateButton(historyRow.transform, "Clear", () =>
            {
                int had = _zsphere.SphereCount;
                _zsphere.ClearRig();
                SetZSphereStatus(had == 0 ? "The rig is already empty." : $"Cleared {had} spheres. Undo (Z) brings them back.");
            }, "Removes every sphere. Undoable.");
            _zsphereUndoLabel = UIFactory.CreateLabel(foldout, string.Empty, 10, FontStyle.Italic);

            _zsphereSymmetryToggle = UIFactory.CreateToggle(foldout, "Symmetry (X)", _zsphere.SymmetryX,
                v => SetZSphereStatus(_zsphere.SetSymmetry(v)),
                tooltip: "Mirrors the whole rig live across the red plane. Turning it off keeps both halves as real spheres you can edit separately.");
            UIFactory.CreateToggle(foldout, "Show Skin", _zsphere.ShowSkin, v => _zsphere.ShowSkin = v,
                tooltip: "Shows the generated mesh as a see-through shell that updates live while you edit.");
            _zspherePreviewToggle = UIFactory.CreateToggle(foldout, "Solid Preview (A)", _zsphere.PreviewMode, v => _zsphere.PreviewMode = v,
                tooltip: "Shows the solid skin with the spheres hidden, to judge the final shape. Limbs can still be dragged.");

            UIFactory.CreateLabel(foldout, "Selected Sphere Radius", 12, FontStyle.Normal);
            _zsphereRadiusSlider = UIFactory.CreateSlider(foldout, ZSphereController.MinNodeRadius, 2f,
                _zsphere.SelectedRadius, v => _zsphere.SelectedRadius = v,
                "Radius of the selected sphere. The mouse wheel over any sphere resizes it too.");

            UIFactory.CreateLabel(foldout, "New Sphere Size (of parent)", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0.2f, 1.5f, _zsphere.ChildTaper, v => _zsphere.ChildTaper = v,
                "Size of a newly drawn sphere relative to the sphere it grows from.");

            UIFactory.CreateLabel(foldout, "Skin Density", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, ZSphereSkinner.MinDensity, ZSphereSkinner.MaxDensity, _zsphere.Density,
                v => _zsphere.Density = v,
                "Mesh detail. 1 is always fine enough for the thinnest limb; higher is smoother but slower.");

            UIFactory.CreateLabel(foldout, "Joint Blend", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0f, 1f, _zsphere.Blend, v => _zsphere.Blend = v,
                "How softly limbs melt into each other where they meet.");

            UIFactory.CreateLabel(foldout, "Skin Smoothing", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0f, 8f, _zsphere.Smoothing, v => _zsphere.Smoothing = Mathf.RoundToInt(v),
                "Smoothing passes over the generated surface.");

            UIFactory.CreateLabel(foldout, "Attach to Object", 12, FontStyle.Normal);
            GameObject attachRow = UIFactory.CreateRow(foldout, 24f);
            UIFactory.CreateButton(attachRow.transform, "Attach Selected", () =>
            {
                SculptableMesh target = _selection != null ? _selection.PrimarySelection : null;
                if (target == null) { SetZSphereStatus("Select an object in the list first."); return; }
                SetZSphereStatus(_zsphere.AttachToObject(target)
                    ? $"Attached to {target.name}. Click its surface in Draw mode to start a limb there."
                    : "Could not attach to that object.");
            }, "Binds the rig to the selected object: clicks land on its surface, and the rig follows it.");
            UIFactory.CreateButton(attachRow.transform, "Detach", () =>
            {
                bool had = _zsphere.AttachTarget != null;
                _zsphere.DetachFromObject();
                SetZSphereStatus(had ? "Detached. Spheres left where they are." : "Nothing was attached.");
            }, "Unbinds the rig. Spheres stay where they are.");
            UIFactory.CreateToggle(foldout, "Snap Spheres to Surface", _zsphere.SnapToSurface,
                v => _zsphere.SnapToSurface = v, tooltip: "Keeps placed and moved spheres on the attached object's surface.");
            _zsphereAttachLabel = UIFactory.CreateLabel(foldout, string.Empty, 10, FontStyle.Italic);
            UIFactory.CreateButton(foldout, "Re-centre Mirror Plane", () =>
                SetZSphereStatus(_zsphere.ReanchorSymmetryPlane()
                    ? "Mirror plane moved onto the sculpt object. Spheres unchanged."
                    : "No sculpt object to centre on."),
                "Moves the mirror plane onto the selected or attached object without moving any sphere.");

            UIFactory.CreateToggle(foldout, "Keep Rig After Convert", _zsphere.KeepRigOnConvert,
                v => _zsphere.KeepRigOnConvert = v, tooltip: "Leaves the rig in place after converting, to keep iterating on it.");
            UIFactory.CreateButton(foldout, "Convert to Sculpt Mesh", ConvertZSpheres, "Bakes the skin into a real sculptable mesh object.");

            _zsphereStatusLabel = UIFactory.CreateLabel(foldout, string.Empty, 11, FontStyle.Italic);

            UIFactory.CreateLabel(foldout,
                "Draw: drag off a sphere to grow a limb, then drag off the new one to keep going.\n" +
                "Drag a link to add a joint and bend the limb there.\n" +
                "Any mode: wheel over a sphere resizes it; right-click deletes it and its branch.\n" +
                "Move carries the whole branch (Shift: one sphere). Scale with Shift: whole branch.\n" +
                "Esc cancels a drag. A toggles the solid preview. Z / Shift+Z undo and redo.",
                10, FontStyle.Italic);

            RefreshZSphereModeButtons();
        }

        private void SetZSphereMode(ZSphereEditMode mode)
        {
            if (_zsphere == null) return;
            _zsphere.EditMode = mode;
            // Picking a mode is an unambiguous statement of intent, so it arms the tool too.
            _setToolMode?.Invoke(GizmoMode.ZSphere);
            RefreshZSphereModeButtons();
        }

        private void RefreshZSphereModeButtons()
        {
            if (_zsphere == null) return;
            for (int i = 0; i < _zsphereModeImages.Length; i++)
            {
                if (_zsphereModeImages[i] == null) continue;
                _zsphereModeImages[i].color = (int)_zsphere.EditMode == i ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            }
        }

        /// Shows a one-off result on the ZSphere status line for a few seconds. Needs a timeout
        /// because RefreshZSphereSection rewrites that label from polled state.
        private void SetZSphereStatus(string message)
        {
            _zsphereSticky = message;
            _zsphereStickyUntil = Time.unscaledTime + ZSphereStickySeconds;
        }

        private void RefreshZSphereSection()
        {
            if (_zsphere == null || _zsphereStatusLabel == null) return;

            bool stickyShowing = _zsphereSticky != null && Time.unscaledTime < _zsphereStickyUntil;
            string undoLabel = _zsphere.NextRigUndoLabel;
            int undoDepth = _zsphere.RigUndoDepth;
            string attachName = _zsphere.AttachTargetName;
            bool snap = _zsphere.SnapToSurface;
            bool armed = _gizmo != null && _gizmo.Mode == GizmoMode.ZSphere;
            int triCount = _zsphere.PreviewTriangleCount;
            bool final = !_zsphere.SkinIsDraft;
            string error = _zsphere.Error;
            int node = _zsphere.SelectedNode;
            int version = _zsphere.Rig.Version;
            bool symmetry = _zsphere.SymmetryX;
            bool preview = _zsphere.PreviewMode;
            ZSphereEditMode mode = _zsphere.EditMode;

            if (_zsphereLabelsValid
                && undoDepth == _lastZSphereUndoDepth && undoLabel == _lastZSphereUndoLabel
                && attachName == _lastZSphereAttachName && snap == _lastZSphereSnap
                && armed == _lastZSphereArmed && triCount == _lastZSphereTriCount
                && final == _lastZSphereFinal && error == _lastZSphereError
                && stickyShowing == _lastZSphereStickyShowing && node == _lastZSphereNode
                && version == _lastZSphereVersion && symmetry == _lastZSphereSymmetry
                && preview == _lastZSpherePreview && mode == _lastZSphereMode)
                return;

            _zsphereLabelsValid = true;
            _lastZSphereUndoDepth = undoDepth;
            _lastZSphereUndoLabel = undoLabel;
            _lastZSphereAttachName = attachName;
            _lastZSphereSnap = snap;
            _lastZSphereArmed = armed;
            _lastZSphereTriCount = triCount;
            _lastZSphereFinal = final;
            _lastZSphereError = error;
            _lastZSphereStickyShowing = stickyShowing;
            _lastZSphereNode = node;
            _lastZSphereVersion = version;
            _lastZSphereSymmetry = symmetry;
            _lastZSpherePreview = preview;
            _lastZSphereMode = mode;

            // These can change from outside the panel - A in the viewport, an undo restoring
            // symmetry - and WithoutNotify so reflecting them never feeds back into the controller.
            if (_zsphereSymmetryToggle != null) _zsphereSymmetryToggle.SetIsOnWithoutNotify(symmetry);
            if (_zspherePreviewToggle != null) _zspherePreviewToggle.SetIsOnWithoutNotify(preview);
            RefreshZSphereModeButtons();

            // WithoutNotify: writing the slider normally would fire straight back into
            // SelectedRadius, so merely selecting a sphere would resize it to the slider's value.
            if (_zsphereRadiusSlider != null && _zsphere.SelectedRadius > 0f)
                _zsphereRadiusSlider.SetValueWithoutNotify(_zsphere.SelectedRadius);

            if (_zsphereUndoLabel != null)
            {
                _zsphereUndoLabel.text = undoLabel == null
                    ? "Rig history empty."
                    : $"Undo (Z): {undoLabel} - {undoDepth} step(s) held.";
                _zsphereUndoLabel.color = UIFactory.StatusHintColor;
            }

            if (_zsphereAttachLabel != null)
            {
                _zsphereAttachLabel.text = attachName == null
                    ? "Not attached - spheres land on the view plane."
                    : snap
                        ? $"Attached to {attachName} - clicks land on its surface."
                        : $"Attached to {attachName} - surface snap is off.";
                _zsphereAttachLabel.color = attachName == null ? UIFactory.StatusHintColor : UIFactory.StatusOkColor;
            }

            if (stickyShowing)
            {
                _zsphereStatusLabel.text = _zsphereSticky;
                _zsphereStatusLabel.color = UIFactory.StatusOkColor;
                return;
            }
            _zsphereSticky = null;

            int spheres = _zsphere.SphereCount;
            if (!armed)
            {
                _zsphereStatusLabel.text = spheres > 0
                    ? $"Rig hidden ({spheres} spheres). Pick ZSpheres in Tool to edit it."
                    : "Pick ZSpheres in Tool (or Add > ZSphere Rig), then click in the viewport.";
                _zsphereStatusLabel.color = UIFactory.StatusHintColor;
            }
            else if (spheres == 0)
            {
                _zsphereStatusLabel.text = "Click in the viewport to place the first sphere - drag to size it.";
                _zsphereStatusLabel.color = UIFactory.StatusHintColor;
            }
            else if (error != null)
            {
                _zsphereStatusLabel.text = error;
                _zsphereStatusLabel.color = UIFactory.StatusErrorColor;
            }
            else if (triCount > 0)
            {
                _zsphereStatusLabel.text = $"{spheres} spheres | skin {triCount:N0} tris{(final ? string.Empty : " (live draft)")}";
                _zsphereStatusLabel.color = UIFactory.StatusOkColor;
            }
            else
            {
                _zsphereStatusLabel.text = $"{spheres} spheres.";
                _zsphereStatusLabel.color = UIFactory.StatusHintColor;
            }
        }

        private void ConvertZSpheres()
        {
            if (_zsphere == null) return;
            SculptableMesh created = _zsphere.ConvertToSculptMesh();
            if (created != null)
            {
                _panelStatus?.Invoke("Skinned ZSpheres into " + created.name, true);
                _refreshToolButtons?.Invoke();
            }
            else
            {
                _panelStatus?.Invoke(_zsphere.Error ?? "Nothing to skin.", false);
            }
        }
    }
}
