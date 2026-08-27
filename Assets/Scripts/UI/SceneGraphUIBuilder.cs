using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Sculpting
{
    /// Builds the Scene Graph panel: add-primitive buttons, a live object list (select/
    /// visibility/delete), the Transpose/Scale gizmo mode toolbar, one-shot Mirror, and Join -
    /// same "build once from code" approach as SculptUIBuilder/the other *UIBuilder classes.
    /// Anchored left-middle edge, the one corner/edge the other panels (documented in
    /// MaterialUIBuilder) don't already occupy.
    public class SceneGraphUIBuilder : MonoBehaviour
    {
        private SelectionManager _selection;
        private PrimitiveSpawner _spawner;
        private SculptController _controller;
        private TransformGizmo _gizmo;

        private Transform _listContent;
        private readonly List<GameObject> _listRows = new List<GameObject>();
        private int _lastShownSelectionVersion = -1;

        private Image _sculptModeImg, _transposeModeImg, _scaleModeImg;
        private Button _joinButton;
        private GameObject _confirmModalGO;

        // Rename field for the primary selection. Kept out of the per-object rows: at this
        // panel's width a row already carries a name button, a visibility toggle and a delete
        // button, and a fourth control per row would leave none of them comfortably clickable.
        // One field that follows the selection also matches how the Material/Lighting panels
        // already work - they edit whatever is selected rather than repeating themselves per
        // object.
        private InputField _renameField;
        private Button _cloneButton;

        // Defaults to X only - the common bilateral symmetry axis for character parts (left/
        // right limbs either side of a centered torso), matching the user's own "remove an arm
        // to see the torso" framing.
        private bool _mirrorX = true, _mirrorY, _mirrorZ;

        // Start(), not Awake() - reads/uses SelectionManager.AllObjects (via RefreshList),
        // which needs every SculptableMesh's OnEnable to have already registered - see
        // SculptUIBuilder's own Start() remarks for the full reasoning.
        private void Start()
        {
            _selection = FindFirstObjectByType<SelectionManager>();
            _spawner = FindFirstObjectByType<PrimitiveSpawner>();
            _controller = FindFirstObjectByType<SculptController>();
            _gizmo = FindFirstObjectByType<TransformGizmo>();
            BuildUI();
            RefreshList();
            RefreshJoinButton();
            RefreshToolButtons();
        }

        private void Update()
        {
            if (_selection == null) return;
            // Cheap once-per-frame poll, same idiom SculptUIBuilder already uses for brush
            // state - only rebuilds the list when something actually changed (spawn/delete/
            // select/visibility/mirror/join all bump SelectionVersion).
            if (_selection.SelectionVersion != _lastShownSelectionVersion)
            {
                _lastShownSelectionVersion = _selection.SelectionVersion;
                RefreshList();
                RefreshJoinButton();
            }
        }

        private void BuildUI()
        {
            Transform panel = UIFactory.CreatePanelCanvas("SceneGraphCanvas", new Vector2(0f, 0.5f), new Vector2(12, 0), 220f);

            UIFactory.CreateLabel(panel, "Scene", 18, FontStyle.Bold);

            UIFactory.CreateLabel(panel, "Add Primitive", 13, FontStyle.Normal);
            GameObject addRow1 = UIFactory.CreateRow(panel, 26f);
            UIFactory.CreateButton(addRow1.transform, "Cube", () => Spawn(PrimitiveShapeType.Cube));
            UIFactory.CreateButton(addRow1.transform, "Sphere", () => Spawn(PrimitiveShapeType.Sphere));
            GameObject addRow2 = UIFactory.CreateRow(panel, 26f);
            UIFactory.CreateButton(addRow2.transform, "Cylinder", () => Spawn(PrimitiveShapeType.Cylinder));
            UIFactory.CreateButton(addRow2.transform, "Capsule", () => Spawn(PrimitiveShapeType.Capsule));

            UIFactory.CreateLabel(panel, "Objects (click=select, Ctrl+click=multi)", 12, FontStyle.Normal);
            var listGO = new GameObject("ObjectList", typeof(RectTransform));
            listGO.transform.SetParent(panel, false);
            var vlg = listGO.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            listGO.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _listContent = listGO.transform;

            UIFactory.CreateLabel(panel, "Selected Object", 13, FontStyle.Normal);
            _renameField = UIFactory.CreateInputField(panel, string.Empty, RenameSelected);
            _cloneButton = UIFactory.CreateButton(panel, "Clone Selected", CloneSelected);

            UIFactory.CreateLabel(panel, "Tool", 13, FontStyle.Normal);
            GameObject toolRow = UIFactory.CreateRow(panel, 26f);
            _sculptModeImg = UIFactory.CreateButton(toolRow.transform, "Sculpt", () => SetGizmoMode(GizmoMode.Sculpt)).GetComponent<Image>();
            _transposeModeImg = UIFactory.CreateButton(toolRow.transform, "Transpose", () => SetGizmoMode(GizmoMode.Transpose)).GetComponent<Image>();
            _scaleModeImg = UIFactory.CreateButton(toolRow.transform, "Scale", () => SetGizmoMode(GizmoMode.Scale)).GetComponent<Image>();

            UIFactory.CreateLabel(panel, "Mirror Selected Across Sphere", 13, FontStyle.Normal);
            GameObject mirrorRow = UIFactory.CreateRow(panel, 22f);
            UIFactory.CreateToggle(mirrorRow.transform, "X", _mirrorX, v => _mirrorX = v);
            UIFactory.CreateToggle(mirrorRow.transform, "Y", _mirrorY, v => _mirrorY = v);
            UIFactory.CreateToggle(mirrorRow.transform, "Z", _mirrorZ, v => _mirrorZ = v);
            UIFactory.CreateButton(panel, "Mirror Selected", DoMirror);

            UIFactory.CreateLabel(panel, "Join (destructive)", 13, FontStyle.Normal);
            _joinButton = UIFactory.CreateButton(panel, "Join Selected", ShowJoinConfirm);
        }

        private void Spawn(PrimitiveShapeType type) => _spawner?.SpawnPrimitive(type);

        // -------------------------------------------------------------------------- object list

        private void RefreshList()
        {
            foreach (GameObject row in _listRows) Destroy(row);
            _listRows.Clear();
            if (_selection == null) return;

            foreach (SculptableMesh obj in _selection.AllObjects)
            {
                if (obj == null) continue;
                GameObject row = UIFactory.CreateRow(_listContent, 24f);
                _listRows.Add(row);

                Button nameBtn = UIFactory.CreateButton(row.transform, obj.name, () => OnRowClicked(obj));
                nameBtn.GetComponent<Image>().color = _selection.PrimarySelection == obj ? UIFactory.ActiveColor
                    : _selection.IsSelected(obj) ? new Color(0.4f, 0.4f, 0.45f) : UIFactory.InactiveColor;

                UIFactory.CreateToggle(row.transform, "Vis", obj.Visible, v => _selection.SetVisible(obj, v));
                UIFactory.CreateButton(row.transform, "X", () => _selection.DeleteObject(obj));
            }

            RefreshSelectedObjectControls();
        }

        // ------------------------------------------------------------------ rename and clone

        private void RefreshSelectedObjectControls()
        {
            SculptableMesh primary = _selection != null ? _selection.PrimarySelection : null;

            if (_renameField != null)
            {
                _renameField.interactable = primary != null;
                // SetTextWithoutNotify, not .text: assigning .text fires onEndEdit on some
                // uGUI paths, which would feed the name straight back into RenameSelected -
                // harmless today but exactly the kind of loop the toggles elsewhere in this
                // codebase already use the without-notify setters to avoid.
                _renameField.SetTextWithoutNotify(primary != null ? primary.name : string.Empty);
            }
            if (_cloneButton != null) _cloneButton.interactable = primary != null;
        }

        /// Renames the primary selection. Trims, and ignores an empty result rather than
        /// letting an object end up with a blank row in the list that can't be told apart from
        /// any other blank one; RefreshList then puts the old name back in the field.
        private void RenameSelected(string newName)
        {
            SculptableMesh primary = _selection != null ? _selection.PrimarySelection : null;
            if (primary == null) { RefreshSelectedObjectControls(); return; }

            string trimmed = (newName ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed == primary.name)
            {
                RefreshSelectedObjectControls();
                return;
            }

            primary.name = trimmed;
            _selection.NotifyChanged(); // redraws the list row, which shows the name
        }

        private void CloneSelected()
        {
            SculptableMesh primary = _selection != null ? _selection.PrimarySelection : null;
            if (primary == null) return;
            // Clone selects itself (see MeshCloner), which bumps SelectionVersion and gets the
            // list rebuilt on the next Update poll.
            MeshCloner.Clone(primary);
        }

        private void OnRowClicked(SculptableMesh obj)
        {
            var kb = Keyboard.current;
            bool additive = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed ||
                                            kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
            _selection.Select(obj, additive);
        }

        // ------------------------------------------------------------------------------ gizmo

        private void SetGizmoMode(GizmoMode mode)
        {
            _gizmo?.SetMode(mode);
            RefreshToolButtons();
        }

        private void RefreshToolButtons()
        {
            GizmoMode mode = _gizmo != null ? _gizmo.Mode : GizmoMode.Sculpt;
            _sculptModeImg.color = mode == GizmoMode.Sculpt ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _transposeModeImg.color = mode == GizmoMode.Transpose ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _scaleModeImg.color = mode == GizmoMode.Scale ? UIFactory.ActiveColor : UIFactory.InactiveColor;
        }

        // ------------------------------------------------------------------------------ mirror

        private void DoMirror()
        {
            if (_selection == null || _spawner == null) return;
            if (!_mirrorX && !_mirrorY && !_mirrorZ) return;

            SculptableMesh target = _selection.PrimarySelection;
            SculptableMesh main = _spawner.MainObject;
            if (target == null || main == null) return;

            MeshMirror.MirrorAcross(target, main.transform.position, _mirrorX, _mirrorY, _mirrorZ);
        }

        // -------------------------------------------------------------------------------- join

        private void RefreshJoinButton()
        {
            if (_joinButton != null) _joinButton.interactable = _selection != null && _selection.SelectedSet.Count >= 2;
        }

        private void ShowJoinConfirm()
        {
            if (_selection == null || _selection.SelectedSet.Count < 2) return;
            int count = _selection.SelectedSet.Count;

            // Captured and mutated by the toggle/slider below, read back when Confirm is
            // clicked - defaults mirror the brush panel's own current Remesh Resolution so the
            // prompt starts wherever the user already had it, rather than a fixed constant.
            bool remeshAfter = true;
            int resolution = _controller != null ? _controller.RemeshResolution : 24;

            // Names the survivor rather than saying "into one": the merged result keeps that
            // object's pivot, and therefore its mirror/symmetry plane (see
            // SelectionManager.Select's additive branch). Which object that is used to be
            // invisible until after the merge, when an off-center symmetry plane gave it away.
            SculptableMesh survivor = _selection.PrimarySelection;
            string survivorName = survivor != null ? survivor.name : "the first selected object";

            ShowConfirm($"Join {count} objects into \"{survivorName}\"? It keeps that object's " +
                        "center and symmetry plane. This cannot be undone.", extraContent =>
            {
                UIFactory.CreateToggle(extraContent, "Remesh after Join", remeshAfter, v => remeshAfter = v);
                UIFactory.CreateLabel(extraContent, "Remesh Resolution", 12, FontStyle.Normal);
                UIFactory.CreateSlider(extraContent, 4f, 500f, resolution, v => resolution = Mathf.RoundToInt(v));
            }, () => DoJoin(remeshAfter, resolution));
        }

        private void DoJoin(bool remeshAfter, int resolution)
        {
            if (_selection == null) return;
            var objects = new List<SculptableMesh>(_selection.SelectedSet);
            SculptableMesh primary = _selection.PrimarySelection;
            // MeshJoiner treats objects[0] as the survivor - make sure that's the primary
            // selection, not just whichever object happened to be Ctrl-clicked first.
            if (primary != null && objects.Remove(primary)) objects.Insert(0, primary);
            // Push the chosen resolution into the shared controller setting too, so the brush
            // panel's own Remesh Resolution slider reflects what was actually used.
            if (remeshAfter && _controller != null) _controller.RemeshResolution = resolution;
            MeshJoiner.Join(objects, _controller, remeshAfter);
        }

        // ------------------------------------------------------------------- confirmation modal

        /// Small blocking overlay (dim backdrop + centered panel) for the one destructive
        /// action in this panel (Join) - no undo exists for it, so this is the mitigation this
        /// codebase already uses elsewhere for undo-free destructive ops. buildExtraContent
        /// (optional) can add controls between the message and the Cancel/Confirm row - used by
        /// Join to expose the Remesh-after-Join toggle/resolution slider before committing.
        private void ShowConfirm(string message, System.Action<Transform> buildExtraContent, System.Action onConfirm)
        {
            // Body moved to UIFactory.ShowModal once the save/load panel needed a prompt of its
            // own - see its remarks. This keeps tracking _confirmModalGO so a second Join press
            // replaces the open prompt rather than stacking another on top of it.
            if (_confirmModalGO != null) Destroy(_confirmModalGO);

            _confirmModalGO = UIFactory.ShowModal(message, buildExtraContent,
                new UIFactory.ModalChoice("Confirm", () =>
                {
                    // ShowModal has already destroyed the overlay by the time this runs; just
                    // drop the stale reference so the guard above doesn't re-destroy it.
                    _confirmModalGO = null;
                    onConfirm();
                }));
        }
    }
}
