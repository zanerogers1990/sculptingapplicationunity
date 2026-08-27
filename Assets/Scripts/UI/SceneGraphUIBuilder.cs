using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Sculpting.IO;

namespace Sculpting
{
    /// Builds the right-hand panel: scene-file actions (Import Object / Load Scene / Save
    /// Scene), add-primitive buttons, a live object list (select/visibility/delete), the
    /// Transpose/Scale gizmo mode toolbar, one-shot Mirror, Join, and - merged in further down -
    /// the collapsible Studio Lighting/Material/Presentation sections. Docked flush to the
    /// top-right corner at full window height, fixed there (no longer draggable), mirroring
    /// SculptUIBuilder's Sculpting Tools panel on the left.
    ///
    /// Two things used to be separate panels of their own: the scene-file actions (top-center
    /// SaveLoadUIBuilder) and Studio Lighting/Material/Presentation (top-right
    /// StudioPanelUIBuilder). Both are merged in here now, so this one scrollable column carries
    /// everything that isn't sculpting tools, instead of three panels the user had to
    /// separately find and reposition.
    public class SceneGraphUIBuilder : MonoBehaviour
    {
        // How long a save/load status line stays up before the hint returns. Failures ignore
        // this and stay until the next action - an error the user blinked past is worse than a
        // stale line.
        private const float StatusHoldSeconds = 5f;

        private static readonly Color OkColor = new Color(0.55f, 0.85f, 0.55f);
        private static readonly Color ErrorColor = new Color(0.95f, 0.45f, 0.4f);
        private static readonly Color HintColor = new Color(0.65f, 0.65f, 0.7f);

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

        // Scene-file (Import/Load/Save) state - see the old SaveLoadUIBuilder this was merged
        // from for the reasoning behind each piece.
        private Text _statusLabel;
        private float _statusClearAt = -1f;
        private InputField _fallbackField;
        private string _lastDirectory;

        // The panel's own Canvas GameObject, watched by Update so the panel can rebuild itself
        // if anything ever destroys it out from under this component. Root-level parenting (see
        // UIFactory.CreatePanelCanvas) is the actual fix for the Editor-undo case that used to
        // take the old standalone save/load panel out mid-session; this is the backstop,
        // because of every panel in the app this is the one whose disappearance can cost real
        // work - there is no other route to Save Scene.
        private GameObject _canvasRoot;

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
            if (_canvasRoot == null)
            {
                // Rebuild rather than log-and-limp: a missing panel is unrecoverable for the
                // user (no menu bar, no hotkey - the buttons ARE the feature), and rebuilding
                // costs one frame's worth of UI construction on a path that should never run.
                BuildUI();
                RefreshList();
                RefreshJoinButton();
                RefreshToolButtons();
                return;
            }

            if (_statusClearAt > 0f && Time.unscaledTime >= _statusClearAt) ShowHint();

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
            // Docked flush to the top-right corner, full window height, fixed there - no
            // longer draggable (see UIFactory's now-removed DraggablePanel). Sits opposite
            // SculptUIBuilder's Sculpting Tools panel, which docks the same way on the left.
            float maxHeight = Mathf.Max(300f, Screen.height);
            Transform panel = UIFactory.CreateScrollingPanelCanvas(
                "SceneGraphCanvas", new Vector2(1f, 1f), Vector2.zero, 260f, maxHeight);
            _canvasRoot = panel.root.gameObject;

            UIFactory.CreateLabel(panel, "Scene", 18, FontStyle.Bold);

            UIFactory.CreateButton(panel, "Import Object...", ImportObject);
            UIFactory.CreateButton(panel, "Load Scene...", LoadScene);
            UIFactory.CreateButton(panel, "Save Scene...", SaveScene);

            if (!FileDialog.IsSupported)
            {
                UIFactory.CreateLabel(panel, "File path", 11, FontStyle.Normal);
                _fallbackField = UIFactory.CreateInputField(panel, SceneSerializer.DefaultPath, null);
            }

            _statusLabel = UIFactory.CreateLabel(panel, string.Empty, 11, FontStyle.Normal);
            ShowHint();

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

            // Studio Lighting / Material / Presentation used to be three separate always-open
            // panels (top-right, bottom-center, bottom-right). Merged into this panel as three
            // collapsible sections - one panel to dock instead of four, and each section starts
            // collapsed so the panel stays small until the user opens the one they want. These
            // builders no longer build their own canvas - they just fill whatever content
            // transform they're handed (see LightingUIBuilder.BuildContent's remarks).
            var lighting = FindFirstObjectByType<LightingUIBuilder>();
            var material = FindFirstObjectByType<MaterialUIBuilder>();
            var presentation = FindFirstObjectByType<PostProcessingUIBuilder>();
            if (lighting != null) lighting.BuildContent(UIFactory.CreateFoldoutSection(panel, "Studio Lighting", false));
            if (material != null) material.BuildContent(UIFactory.CreateFoldoutSection(panel, "Material", false));
            if (presentation != null) presentation.BuildContent(UIFactory.CreateFoldoutSection(panel, "Presentation", false));
        }

        private void Spawn(PrimitiveShapeType type) => _spawner?.SpawnPrimitive(type);

        // -------------------------------------------------------------- scene file actions

        /// Brings a single model in alongside whatever is already in the scene. Separate from
        /// Load Scene precisely because it never asks a question: adding a model to what you are
        /// working on is the only thing it can sensibly mean.
        private void ImportObject()
        {
            string path = PickPath("Import object", "obj");
            if (path == null) return;

            if (SceneSerializer.ImportAny(path, out int count, out string error))
                SetStatus($"Imported {Path.GetFileName(path)}", OkColor, hold: true);
            else
                SetStatus("Import failed: " + error, ErrorColor, hold: false);
        }

        /// Opens a saved scene, then asks how to bring it in. The prompt exists because both
        /// answers are reasonable and one of them is destructive: replacing discards everything
        /// currently in the scene, and there is no undo for that. Asking after the file is
        /// chosen (rather than offering two buttons up front) keeps the panel to one obvious
        /// action and puts the question at the moment it can be answered concretely - the file's
        /// own name and object count are in the prompt.
        private void LoadScene()
        {
            string path = PickPath("Load scene", "sculpt");
            if (path == null) return;

            string name = Path.GetFileName(path);
            UIFactory.ShowModal(
                $"\"{name}\"\n\nReplace everything in the scene, or add its objects to what you have?",
                null,
                new UIFactory.ModalChoice("Add to current scene", () =>
                {
                    if (SceneSerializer.ImportAny(path, out int count, out string error))
                        SetStatus($"Added {count} object{(count == 1 ? "" : "s")} from {name}", OkColor, hold: true);
                    else
                        SetStatus("Load failed: " + error, ErrorColor, hold: false);
                }),
                new UIFactory.ModalChoice("Replace scene (cannot be undone)", () =>
                {
                    if (SceneSerializer.Load(path, out string error))
                    {
                        // Only the replacing path needs this: it restores brush/material/
                        // lighting/camera wholesale, so the Studio Lighting/Material/
                        // Presentation sections merged into this panel are showing values that
                        // no longer apply. Adding objects changes no global setting. Rebuild
                        // FIRST, then set the status - RebuildOtherPanels rebuilds this panel's
                        // own _statusLabel too (see its remarks), which would otherwise reset
                        // straight back to the default hint right after this line ran.
                        RebuildOtherPanels();
                        SetStatus("Loaded " + name, OkColor, hold: true);
                    }
                    else
                    {
                        SetStatus("Load failed: " + error, ErrorColor, hold: false);
                    }
                }));
        }

        private void SaveScene()
        {
            string path = PickSavePath();
            if (path == null) return;

            if (SceneSerializer.Save(path, out string error))
                SetStatus($"Saved {Path.GetFileName(path)} ({FileSizeMb(path)})", OkColor, hold: true);
            else
                SetStatus("Save failed: " + error, ErrorColor, hold: false);
        }

        // ----------------------------------------------------------------------- path picking

        /// The OS picker where there is one, the fallback field otherwise. Returns null when the
        /// user cancels, which every caller treats as "do nothing" - deliberately NOT an error,
        /// since cancelling is a normal thing to do.
        private string PickPath(string title, params string[] extensions)
        {
            if (FileDialog.IsSupported)
            {
                string chosen = FileDialog.OpenFile(title, StartDirectory(), extensions);
                if (!string.IsNullOrEmpty(chosen)) _lastDirectory = FileDialog.DirectoryFor(chosen);
                return string.IsNullOrEmpty(chosen) ? null : chosen;
            }

            // Typed paths are used verbatim - no extension is appended, because this same field
            // has to be able to name a .obj as well as a .sculpt.
            string typed = _fallbackField != null ? _fallbackField.text?.Trim().Trim('"') : null;
            if (string.IsNullOrEmpty(typed)) { SetStatus("Type a file path first.", ErrorColor, hold: false); return null; }
            return typed;
        }

        private string PickSavePath()
        {
            if (FileDialog.IsSupported)
            {
                string chosen = FileDialog.SaveFile("Save scene", StartDirectory(), "sculpt-session", "sculpt");
                if (!string.IsNullOrEmpty(chosen)) _lastDirectory = FileDialog.DirectoryFor(chosen);
                return string.IsNullOrEmpty(chosen) ? null : chosen;
            }

            // NormalizePath here (unlike PickPath) because a save target is always a .sculpt, so
            // a bare name can safely be completed into one.
            string typed = _fallbackField != null ? _fallbackField.text : null;
            return SceneSerializer.NormalizePath(typed);
        }

        private string StartDirectory() =>
            string.IsNullOrEmpty(_lastDirectory) ? SceneSerializer.DefaultDirectory : _lastDirectory;

        private static string FileSizeMb(string path)
        {
            try { return (new FileInfo(path).Length / 1024f / 1024f).ToString("F1") + " MB"; }
            catch { return "saved"; }
        }

        private void SetStatus(string message, Color color, bool hold)
        {
            _statusLabel.text = message;
            _statusLabel.color = color;
            _statusClearAt = hold ? Time.unscaledTime + StatusHoldSeconds : -1f;
        }

        private void ShowHint()
        {
            _statusLabel.text = "Import adds a model (.obj). Load opens a saved scene (.sculpt).";
            _statusLabel.color = HintColor;
            _statusClearAt = -1f;
        }

        // Destroys and re-runs the Sculpting Tools panel's Start, plus this panel's own BuildUI,
        // so every control shows the loaded scene's values rather than the ones it was built
        // from at startup. This panel can no longer skip rebuilding itself the way the old
        // separate SaveLoadUIBuilder could: it carries the Studio Lighting/Material/
        // Presentation sections now (merged in), which DO go stale the same way brush/material/
        // lighting settings do elsewhere.
        //
        // SculptUIBuilder is driven via SendMessage("Start") rather than a direct call, since
        // this class holds no reference to it and Start is private - the same "invoke a private
        // MonoBehaviour method without reflection" idiom used elsewhere in this project.
        private void RebuildOtherPanels()
        {
            var sculptBuilder = FindFirstObjectByType<SculptUIBuilder>();
            if (sculptBuilder != null) sculptBuilder.gameObject.SendMessage("Start", SendMessageOptions.DontRequireReceiver);

            BuildUI();
            RefreshList();
            RefreshJoinButton();
            RefreshToolButtons();
        }

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
