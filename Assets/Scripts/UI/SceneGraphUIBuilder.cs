using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
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

        private Image _sculptModeImg, _transposeModeImg, _scaleModeImg, _zsphereModeImg;
        private Button _joinButton;
        private Button _subtractButton, _unionButton, _intersectButton;
        private GameObject _confirmModalGO;

        // ZSphere blockout section - see BuildZSphereSection. Held as fields only for the parts
        // Update has to keep current: the edit-mode toolbar's highlight, the radius slider (which
        // follows whichever sphere is selected in the viewport), and the status line.
        private ZSphereController _zsphere;
        private readonly Image[] _zsphereModeImages = new Image[5];
        private Slider _zsphereRadiusSlider;
        private Text _zsphereStatusLabel;
        // One-off action results shown over the polled status line - see SetZSphereStatus.
        private const float ZSphereStickySeconds = 4f;
        private string _zsphereSticky;
        private float _zsphereStickyUntil;
        private int _lastShownZSphereVersion = -1;
        private int _lastShownZSphereNode = -1;
        // Last inputs the labels below were actually built from. Without these,
        // RefreshZSphereSection ran in full on EVERY frame: three interpolated strings plus
        // a whole-rig walk (EffectiveResolution -> ZSphereSkinner.PreviewResolution, which
        // calls ComputeBounds and MeanRadius), all to redraw text that changes a handful of
        // times in a session. Rig.Version covers everything derived from the geometry,
        // EffectiveResolution included.
        //
        // AttachTargetName is still read every frame, because UnityEngine.Object.name is
        // the only signal that the attach target changed and it marshals a fresh string on
        // each get. That one small allocation is what buys skipping all of the above, and
        // adding a version counter purely to dodge it was not worth the extra state.
        private bool _zsphereLabelsValid;
        private string _lastZSphereUndoLabel;
        private int _lastZSphereUndoDepth = -1;
        private string _lastZSphereAttachName;
        private bool _lastZSphereSnap;
        private bool _lastZSphereArmed;
        private int _lastZSphereTriCount = -1;
        private string _lastZSphereError;
        private bool _lastZSphereStickyShowing;
        // Follow live state that no other control reflects: what Undo would reverse, and which
        // object the rig is attached to.
        private Text _zsphereUndoLabel;
        private Text _zsphereAttachLabel;

        // Rename field for the primary selection. Kept out of the per-object rows: at this
        // panel's width a row already carries a name button, a visibility toggle and a delete
        // button, and a fourth control per row would leave none of them comfortably clickable.
        // One field that follows the selection also matches how the Material/Lighting panels
        // already work - they edit whatever is selected rather than repeating themselves per
        // object.
        private InputField _renameField;
        private Button _cloneButton;

        // The object whose row is currently showing an inline rename field instead of its name
        // button - set by double-clicking a row (see AddDoubleClickHandler/BeginInlineRename),
        // cleared once that edit commits or is cancelled. At most one row is ever in this state.
        private SculptableMesh _renamingObject;

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
            _zsphere = FindFirstObjectByType<ZSphereController>();
            BuildUI();
            RefreshList();
            RefreshMultiObjectButtons();
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
                RefreshMultiObjectButtons();
                RefreshToolButtons();
                return;
            }

            if (_statusClearAt > 0f && Time.unscaledTime >= _statusClearAt) ShowHint();

            RefreshZSphereSection();

            if (_selection == null) return;
            // Cheap once-per-frame poll, same idiom SculptUIBuilder already uses for brush
            // state - only rebuilds the list when something actually changed (spawn/delete/
            // select/visibility/mirror/join all bump SelectionVersion).
            if (_selection.SelectionVersion != _lastShownSelectionVersion)
            {
                _lastShownSelectionVersion = _selection.SelectionVersion;
                RefreshList();
                RefreshMultiObjectButtons();
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

            // A ZSphere rig belongs among the primitives even though it is not one: this is the
            // "start a model from nothing" row, and a blockout is a perfectly ordinary way to
            // start one - it just happens to become geometry at Convert rather than immediately.
            // Without an entry here the tool could only be reached by first spawning a primitive
            // to click near, which is exactly backwards for building a figure out of ZSpheres.
            GameObject addRow3 = UIFactory.CreateRow(panel, 26f);
            UIFactory.CreateButton(addRow3.transform, "ZSphere Rig", StartZSphereRig);

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
            GameObject toolRow2 = UIFactory.CreateRow(panel, 26f);
            _scaleModeImg = UIFactory.CreateButton(toolRow2.transform, "Scale", () => SetGizmoMode(GizmoMode.Scale)).GetComponent<Image>();
            _zsphereModeImg = UIFactory.CreateButton(toolRow2.transform, "ZSpheres", () => SetGizmoMode(GizmoMode.ZSphere)).GetComponent<Image>();

            BuildZSphereSection(panel);

            UIFactory.CreateLabel(panel, "Mirror Selected Across Sphere", 13, FontStyle.Normal);
            GameObject mirrorRow = UIFactory.CreateRow(panel, 22f);
            UIFactory.CreateToggle(mirrorRow.transform, "X", _mirrorX, v => _mirrorX = v);
            UIFactory.CreateToggle(mirrorRow.transform, "Y", _mirrorY, v => _mirrorY = v);
            UIFactory.CreateToggle(mirrorRow.transform, "Z", _mirrorZ, v => _mirrorZ = v);
            UIFactory.CreateButton(panel, "Mirror Selected", DoMirror);

            UIFactory.CreateLabel(panel, "Join (destructive)", 13, FontStyle.Normal);
            _joinButton = UIFactory.CreateButton(panel, "Join Selected", ShowJoinConfirm);

            // One row of three rather than a button each: they are the same gesture with the
            // same prompt, and the panel already spends a lot of vertical space above this.
            UIFactory.CreateLabel(panel, "Boolean (watertight)", 13, FontStyle.Normal);
            GameObject booleanRow = UIFactory.CreateRow(panel, 26f);
            _subtractButton = UIFactory.CreateButton(booleanRow.transform, "Subtract", () => ShowBooleanConfirm(BooleanOp.Subtract));
            _unionButton = UIFactory.CreateButton(booleanRow.transform, "Union", () => ShowBooleanConfirm(BooleanOp.Union));
            _intersectButton = UIFactory.CreateButton(booleanRow.transform, "Intersect", () => ShowBooleanConfirm(BooleanOp.Intersect));

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

        /// Arms the ZSphere tool and drops the first sphere in the middle of the view. Reports
        /// through the ZSphere section's own status line rather than the panel's, since that is
        /// where the user's attention is being sent.
        private void StartZSphereRig()
        {
            if (_zsphere == null)
            {
                SetStatus("No ZSphereController in the scene.", ErrorColor, hold: false);
                return;
            }

            SetZSphereStatus(_zsphere.StartNewRig()
                ? "ZSphere rig started. Drag off the sphere to grow the next one."
                : "A rig is already up - drag off a sphere, or Clear ZSpheres to start over.");
            RefreshToolButtons();
            RefreshZSphereModeButtons();
        }

        // -------------------------------------------------------------- scene file actions

        /// Brings a single model in alongside whatever is already in the scene. Separate from
        /// Load Scene precisely because it never asks a question: adding a model to what you are
        /// working on is the only thing it can sensibly mean.
        private void ImportObject()
        {
            // SceneSerializer.ImportableExtensions, not a hard-coded "obj": ImportAny already
            // dispatches a .sculpt file to the whole-session importer, and the constant exists
            // to say so. Hard-coding the narrower list here meant the picker HID .sculpt files
            // from a button that has always been able to open them.
            string path = PickPath("Import object", SceneSerializer.ImportableExtensions);
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
            RefreshMultiObjectButtons();
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

                if (obj == _renamingObject)
                {
                    InputField renameField = UIFactory.CreateInputField(row.transform, obj.name,
                        newName => CommitInlineRename(obj, newName));
                    FocusRenameField(renameField);
                }
                else
                {
                    Button nameBtn = UIFactory.CreateButton(row.transform, obj.name, () => OnRowClicked(obj));
                    nameBtn.GetComponent<Image>().color = _selection.PrimarySelection == obj ? UIFactory.ActiveColor
                        : _selection.IsSelected(obj) ? new Color(0.4f, 0.4f, 0.45f) : UIFactory.InactiveColor;
                    AddDoubleClickHandler(nameBtn.gameObject, () => BeginInlineRename(obj));
                }

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

        /// Double-click on a row's name swaps that row's button for an inline InputField -
        /// quicker than hunting for the "Selected Object" field below the list, and matches how
        /// double-click-to-rename works in most file browsers / scene outliners.
        private void BeginInlineRename(SculptableMesh obj)
        {
            _renamingObject = obj;
            RefreshList();
        }

        /// Commits (or, for an unchanged/blank result, silently discards) the inline rename and
        /// puts the row back to its normal button. Takes the object explicitly rather than
        /// reading PrimarySelection - double-click doesn't require the row to be selected first,
        /// so this can fire for an object that isn't the current selection.
        private void CommitInlineRename(SculptableMesh obj, string newName)
        {
            _renamingObject = null;
            if (obj == null) { RefreshList(); return; }

            string trimmed = (newName ?? string.Empty).Trim();
            if (trimmed.Length > 0 && trimmed != obj.name)
            {
                obj.name = trimmed;
                _selection.NotifyChanged();
            }
            RefreshList();
        }

        /// Selects the field's text and drops the caret into it immediately, so the user can
        /// start typing (or hit Ctrl+A/just type over the selection) without an extra click -
        /// double-clicking a name is a clear enough statement of intent to skip that step.
        private static void FocusRenameField(InputField field)
        {
            EventSystem.current?.SetSelectedGameObject(field.gameObject);
            field.Select();
            field.ActivateInputField();
            field.selectionAnchorPosition = 0;
            field.selectionFocusPosition = field.text.Length;
        }

        /// Legacy uGUI Button has no double-click event, so this adds an EventTrigger alongside
        /// it that watches PointerClick's clickCount - Unity already tracks double-click timing/
        /// distance itself, this just reads the result. Runs alongside the Button's own onClick
        /// (both fire per click), which is fine: a double-click's first click still selects the
        /// row like a single click normally would, and the second click additionally enters
        /// rename mode.
        private static void AddDoubleClickHandler(GameObject go, System.Action onDoubleClick)
        {
            var trigger = go.AddComponent<EventTrigger>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            entry.callback.AddListener(data =>
            {
                if (((PointerEventData)data).clickCount == 2) onDoubleClick();
            });
            trigger.triggers.Add(entry);
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
            _zsphereModeImg.color = mode == GizmoMode.ZSphere ? UIFactory.ActiveColor : UIFactory.InactiveColor;
        }

        // ----------------------------------------------------------------------------- zspheres

        /// The ZSphere blockout controls. Sits under the Tool toolbar because that toolbar is what
        /// arms them - every control here is inert until ZSpheres is the active tool, and the
        /// status line says so rather than leaving the user to wonder why clicking does nothing.
        ///
        /// Collapsed by default like the Studio sections below it: it is the largest section in
        /// this panel, and a blockout is something you do at the start of a model and then not
        /// again for hours.
        private void BuildZSphereSection(Transform panel)
        {
            // The labels below are about to be recreated empty, so the cached signature
            // RefreshZSphereSection skips on no longer describes what is on screen.
            _zsphereLabelsValid = false;

            Transform foldout = UIFactory.CreateFoldoutSection(panel, "ZSpheres (Blockout)", false);

            if (_zsphere == null)
            {
                UIFactory.CreateLabel(foldout, "No ZSphereController in scene.", 11, FontStyle.Italic);
                return;
            }

            UIFactory.CreateLabel(foldout, "Edit Mode", 12, FontStyle.Normal);
            GameObject modeRow1 = UIFactory.CreateRow(foldout, 24f);
            _zsphereModeImages[0] = UIFactory.CreateButton(modeRow1.transform, "Add", () => SetZSphereMode(ZSphereEditMode.Add)).GetComponent<Image>();
            _zsphereModeImages[1] = UIFactory.CreateButton(modeRow1.transform, "Move", () => SetZSphereMode(ZSphereEditMode.Move)).GetComponent<Image>();
            _zsphereModeImages[2] = UIFactory.CreateButton(modeRow1.transform, "Scale", () => SetZSphereMode(ZSphereEditMode.Scale)).GetComponent<Image>();
            GameObject modeRow2 = UIFactory.CreateRow(foldout, 24f);
            _zsphereModeImages[3] = UIFactory.CreateButton(modeRow2.transform, "Pose", () => SetZSphereMode(ZSphereEditMode.Pose)).GetComponent<Image>();
            _zsphereModeImages[4] = UIFactory.CreateButton(modeRow2.transform, "Delete", () => SetZSphereMode(ZSphereEditMode.Delete)).GetComponent<Image>();

            // History and Clear sit right under the mode buttons, above everything else: they are
            // what makes the modes safe to experiment with, and a Clear buried at the bottom of a
            // long section is a Clear the user only finds by scrolling past everything they were
            // afraid of pressing.
            GameObject historyRow = UIFactory.CreateRow(foldout, 24f);
            UIFactory.CreateButton(historyRow.transform, "Undo", () =>
                SetZSphereStatus(_zsphere.UndoRig() ? "Undid the last ZSphere edit." : "Nothing left to undo."));
            UIFactory.CreateButton(historyRow.transform, "Redo", () =>
                SetZSphereStatus(_zsphere.RedoRig() ? "Redid the last ZSphere edit." : "Nothing to redo."));
            UIFactory.CreateButton(historyRow.transform, "Clear", () =>
            {
                int had = _zsphere.SphereCount;
                _zsphere.ClearRig();
                SetZSphereStatus(had == 0 ? "The rig is already empty." : $"Cleared {had} spheres. Undo (Z) brings them back.");
            });
            _zsphereUndoLabel = UIFactory.CreateLabel(foldout, string.Empty, 10, FontStyle.Italic);

            UIFactory.CreateToggle(foldout, "Symmetry (X)", _zsphere.SymmetryX, v => _zsphere.SymmetryX = v);

            // The fix for "extruding down a torso split my spine into two spheres" - see
            // ZSphereController.CentreSnap. Exposed rather than hard-coded because the right band
            // depends on how the user drags: a steady hand wants it small so limbs split readily,
            // a tablet-and-wrist one wants it wide.
            UIFactory.CreateLabel(foldout, "Centre Snap (of radius)", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0f, 1f, _zsphere.CentreSnap, v => _zsphere.CentreSnap = v);
            UIFactory.CreateButton(foldout, "Snap Selected to Centre",
                () => SetZSphereStatus(_zsphere.CentreSelected()));

            // Symmetry only twins spheres AS THEY ARE CREATED, so a rig built with it off - or
            // built before the mirror plane was anchored to the root sphere - stays one-sided
            // forever with no way back short of rebuilding it. This is that way back.
            UIFactory.CreateButton(foldout, "Mirror Rig Now", () =>
            {
                int made = _zsphere.MirrorRig();
                SetZSphereStatus(made == 0
                    ? "Nothing to mirror - every off-centre sphere already has a twin."
                    : $"Mirrored {made} spheres across the X plane.");
            });

            // A rig started before there was an object to anchor to - or started when a different
            // object was selected - keeps whatever plane it was given. This re-seats it on the
            // current object without moving a single sphere, so an existing blockout does not
            // have to be thrown away to fix its symmetry.
            UIFactory.CreateButton(foldout, "Centre Plane on Sculpt Object", () =>
                SetZSphereStatus(_zsphere.ReanchorSymmetryPlane()
                    ? "Symmetry plane moved onto the sculpt object. Spheres unchanged."
                    : "No sculpt object to centre on."));

            // Attach - see ZSphereController.AttachToObject. Placed after the symmetry controls
            // because attaching re-seats the mirror plane onto the object it binds to, so the two
            // are one thought: "this rig belongs to that body".
            UIFactory.CreateLabel(foldout, "Attach to Object", 12, FontStyle.Normal);
            GameObject attachRow = UIFactory.CreateRow(foldout, 24f);
            UIFactory.CreateButton(attachRow.transform, "Attach Selected", () =>
            {
                SculptableMesh target = _selection != null ? _selection.PrimarySelection : null;
                if (target == null) { SetZSphereStatus("Select an object in the list first."); return; }
                SetZSphereStatus(_zsphere.AttachToObject(target)
                    ? $"Attached to {target.name}. Click its surface to place spheres on it."
                    : "Could not attach to that object.");
            });
            UIFactory.CreateButton(attachRow.transform, "Detach", () =>
            {
                bool had = _zsphere.AttachTarget != null;
                _zsphere.DetachFromObject();
                SetZSphereStatus(had ? "Detached. Spheres left where they are." : "Nothing was attached.");
            });
            UIFactory.CreateToggle(foldout, "Snap Spheres to Surface", _zsphere.SnapToSurface,
                v => _zsphere.SnapToSurface = v);
            _zsphereAttachLabel = UIFactory.CreateLabel(foldout, string.Empty, 10, FontStyle.Italic);

            UIFactory.CreateLabel(foldout, "Child Size (of parent)", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0.2f, 1.5f, _zsphere.ChildTaper, v => _zsphere.ChildTaper = v);

            UIFactory.CreateLabel(foldout, "Selected Sphere Radius", 12, FontStyle.Normal);
            _zsphereRadiusSlider = UIFactory.CreateSlider(foldout, ZSphereController.MinNodeRadius, 2f,
                _zsphere.SelectedRadius, v => _zsphere.SelectedRadius = v);

            UIFactory.CreateLabel(foldout, "Skin Resolution", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, ZSphereSkinner.MinResolution, ZSphereSkinner.MaxResolution,
                _zsphere.Resolution, v => _zsphere.Resolution = Mathf.RoundToInt(v));
            UIFactory.CreateToggle(foldout, "Adaptive Resolution", _zsphere.AdaptiveResolution,
                v => _zsphere.AdaptiveResolution = v);

            UIFactory.CreateLabel(foldout, "Joint Blend", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0f, 1f, _zsphere.Blend, v => _zsphere.Blend = v);

            UIFactory.CreateLabel(foldout, "Skin Smoothing", 12, FontStyle.Normal);
            UIFactory.CreateSlider(foldout, 0f, 12f, _zsphere.Smoothing, v => _zsphere.Smoothing = Mathf.RoundToInt(v));

            UIFactory.CreateToggle(foldout, "Live Skin Preview", _zsphere.LivePreview, v => _zsphere.LivePreview = v);
            UIFactory.CreateButton(foldout, "Update Skin", () => _zsphere.RebuildSkinNow());

            UIFactory.CreateToggle(foldout, "Keep Rig After Convert", _zsphere.KeepRigOnConvert,
                v => _zsphere.KeepRigOnConvert = v);
            UIFactory.CreateButton(foldout, "Convert to Sculpt Mesh", ConvertZSpheres);

            _zsphereStatusLabel = UIFactory.CreateLabel(foldout, string.Empty, 11, FontStyle.Italic);

            UIFactory.CreateLabel(foldout,
                "Add: click empty space for the first sphere, then DRAG off a sphere to grow the next.\nClick a LIMB between two spheres to insert one there - for adding volume mid-bone.\nScroll over a sphere resizes it. RMB or Ctrl+click deletes it and its branch.\nPose swings a branch about its parent joint, keeping bone lengths.\nZ undoes the last rig edit, Shift+Z redoes it - the rig has its own history.\nSymmetry mirrors across the red plane through the rig root; spheres inside the\ncentre snap band are pinned to the middle instead of splitting into a pair.\nAttach binds the rig to an object: clicks land on its surface, and each click on\nit starts a new limb root. Convert still makes a separate object.",
                10, FontStyle.Italic);

            RefreshZSphereModeButtons();
        }

        private void SetZSphereMode(ZSphereEditMode mode)
        {
            if (_zsphere == null) return;
            _zsphere.EditMode = mode;
            // Picking a ZSphere edit mode is an unambiguous statement of intent, so it arms the
            // tool too rather than silently doing nothing until the user also finds the ZSpheres
            // button in the toolbar above.
            SetGizmoMode(GizmoMode.ZSphere);
            RefreshZSphereModeButtons();
        }

        private void RefreshZSphereModeButtons()
        {
            if (_zsphere == null || _zsphereModeImages[0] == null) return;
            for (int i = 0; i < _zsphereModeImages.Length; i++)
            {
                if (_zsphereModeImages[i] == null) continue;
                _zsphereModeImages[i].color = (int)_zsphere.EditMode == i ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            }
        }

        /// Once-per-frame poll of the controller's state, same idiom as the object-list refresh
        /// above. Two things here can change without this panel being touched: the selected sphere
        /// (picked in the viewport, not in this panel) and the skin preview's triangle count.
        /// Shows a one-off result on the ZSphere status line for a few seconds.
        ///
        /// Needs a timeout rather than a plain assignment because RefreshZSphereSection rewrites
        /// that label EVERY frame from polled state, so anything written directly would be gone
        /// before it could be read. Actions whose whole result is a number - "mirrored 6 spheres",
        /// or equally "nothing to mirror" - have nowhere else to report, and silently doing
        /// nothing is indistinguishable from a broken button.
        private void SetZSphereStatus(string message)
        {
            _zsphereSticky = message;
            _zsphereStickyUntil = Time.unscaledTime + ZSphereStickySeconds;
        }

        private void RefreshZSphereSection()
        {
            if (_zsphere == null || _zsphereStatusLabel == null) return;

            // A sticky message expires on a clock, so whether one is showing has to be
            // re-evaluated every frame - it is part of the signature below precisely so the
            // frame it lapses is the frame the normal status line comes back.
            bool stickyShowing = _zsphereSticky != null && Time.unscaledTime < _zsphereStickyUntil;

            string undoLabel = _zsphere.NextRigUndoLabel;
            int undoDepth = _zsphere.RigUndoDepth;
            string attachName = _zsphere.AttachTargetName;
            bool snap = _zsphere.SnapToSurface;
            bool gizmoArmed = _gizmo != null && _gizmo.Mode == GizmoMode.ZSphere;
            int triCount = _zsphere.PreviewTriangleCount;
            string zsError = _zsphere.Error;
            int node = _zsphere.SelectedNode;
            int rigVersion = _zsphere.Rig.Version;

            if (_zsphereLabelsValid
                && undoDepth == _lastZSphereUndoDepth
                && undoLabel == _lastZSphereUndoLabel
                && attachName == _lastZSphereAttachName
                && snap == _lastZSphereSnap
                && gizmoArmed == _lastZSphereArmed
                && triCount == _lastZSphereTriCount
                && zsError == _lastZSphereError
                && stickyShowing == _lastZSphereStickyShowing
                && node == _lastShownZSphereNode
                && rigVersion == _lastShownZSphereVersion)
                return;

            // Recorded here rather than at the end: the body below has its own early return
            // on the sticky path, and this pass is about to render THESE inputs either way.
            _zsphereLabelsValid = true;
            _lastZSphereUndoLabel = undoLabel;
            _lastZSphereUndoDepth = undoDepth;
            _lastZSphereAttachName = attachName;
            _lastZSphereSnap = snap;
            _lastZSphereArmed = gizmoArmed;
            _lastZSphereTriCount = triCount;
            _lastZSphereError = zsError;
            _lastZSphereStickyShowing = stickyShowing;

            // Above the sticky-message early-out below: these two labels are not the status line,
            // and freezing them for the four seconds a one-off message is up would leave the
            // attach label contradicting the very message that just replaced it.
            if (_zsphereUndoLabel != null)
            {
                _zsphereUndoLabel.text = undoLabel == null
                    ? "Rig history empty."
                    : $"Undo (Z): {undoLabel} - {undoDepth} step(s) held.";
                _zsphereUndoLabel.color = HintColor;
            }

            if (_zsphereAttachLabel != null)
            {
                _zsphereAttachLabel.text = attachName == null
                    ? "Not attached - spheres land on the view plane."
                    : snap
                        ? $"Attached to {attachName} - clicks land on its surface."
                        : $"Attached to {attachName} - surface snap is off.";
                _zsphereAttachLabel.color = attachName == null ? HintColor : OkColor;
            }

            if (_zsphereSticky != null)
            {
                if (Time.unscaledTime < _zsphereStickyUntil)
                {
                    _zsphereStatusLabel.text = _zsphereSticky;
                    _zsphereStatusLabel.color = OkColor;
                    return;
                }
                _zsphereSticky = null;
            }

            if (node != _lastShownZSphereNode || rigVersion != _lastShownZSphereVersion)
            {
                _lastShownZSphereNode = node;
                _lastShownZSphereVersion = rigVersion;

                // SetValueWithoutNotify, not `value`: writing the slider normally would fire its
                // onChange straight back into SelectedRadius, so simply SELECTING a sphere would
                // rewrite its radius to whatever the slider happened to be showing.
                if (_zsphereRadiusSlider != null && _zsphere.SelectedRadius > 0f)
                    _zsphereRadiusSlider.SetValueWithoutNotify(_zsphere.SelectedRadius);
            }

            // SphereCount needs no entry of its own in the signature above: every ZSphereRig
            // mutation that moves AliveCount bumps Version in the same breath.
            int spheres = _zsphere.SphereCount;

            if (!gizmoArmed)
            {
                _zsphereStatusLabel.text = spheres > 0
                    ? $"Rig hidden ({spheres} spheres). Pick ZSpheres in Tool to edit it."
                    : "Pick ZSpheres in Tool, then click in the viewport to start.";
                _zsphereStatusLabel.color = HintColor;
            }
            else if (spheres == 0)
            {
                _zsphereStatusLabel.text = "Click in the viewport to place the first sphere.";
                _zsphereStatusLabel.color = HintColor;
            }
            else if (triCount > 0)
            {
                _zsphereStatusLabel.text =
                    $"{spheres} spheres | skin {triCount:N0} tris @ res {_zsphere.EffectiveResolution}";
                _zsphereStatusLabel.color = OkColor;
            }
            else
            {
                _zsphereStatusLabel.text = zsError ?? $"{spheres} spheres. Update Skin to preview.";
                _zsphereStatusLabel.color = zsError != null ? ErrorColor : HintColor;
            }
        }

        private void ConvertZSpheres()
        {
            if (_zsphere == null) return;
            SculptableMesh created = _zsphere.ConvertToSculptMesh();
            if (created != null)
            {
                SetStatus("Skinned ZSpheres into " + created.name, OkColor, hold: true);
                RefreshToolButtons();
            }
            else
            {
                SetStatus(_zsphere.Error ?? "Nothing to skin.", ErrorColor, hold: false);
            }
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

        private void RefreshMultiObjectButtons()
        {
            bool multi = _selection != null && _selection.SelectedSet.Count >= 2;
            if (_joinButton != null) _joinButton.interactable = multi;
            if (_subtractButton != null) _subtractButton.interactable = multi;
            if (_unionButton != null) _unionButton.interactable = multi;
            if (_intersectButton != null) _intersectButton.interactable = multi;
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

        // ---------------------------------------------------------------------------- boolean

        /// The boolean ops, in the same place and shape as Join because it is the same gesture -
        /// pick the object you are keeping, Ctrl+click the others - and putting them anywhere
        /// else would mean explaining the selection rule twice.
        private void ShowBooleanConfirm(BooleanOp op)
        {
            if (_selection == null || _selection.SelectedSet.Count < 2) return;

            SculptableMesh target = _selection.PrimarySelection;
            string targetName = target != null ? target.name : "the first selected object";
            int others = _selection.SelectedSet.Count - 1;
            string plural = others == 1 ? "" : "s";

            // Same defaulting as Join: start from the Remesh Resolution the user already has,
            // since a boolean rebuilds the target on that same kind of grid. Deliberately NOT
            // written back to the controller afterwards though - a subtraction often wants a
            // much higher number than everyday remeshing (fine cutter detail in a big block),
            // and silently moving the brush panel's slider up there would make the next
            // ordinary Remesh far more expensive than the user asked for.
            int resolution = _controller != null ? _controller.RemeshResolution : 24;
            bool deleteOthers = false;

            // Union names Join explicitly: the two sit next to each other and sound alike, but
            // Join concatenates shells (leaving the walls inside the overlap) while Union welds
            // them into one surface. Which one someone wants is the whole question, so the
            // prompt is where to answer it.
            string question =
                op == BooleanOp.Subtract ? $"Cut {others} object{plural} out of \"{targetName}\"? " :
                op == BooleanOp.Union ? $"Weld {others} object{plural} into \"{targetName}\" as one solid? " +
                                        "Unlike Join, this leaves no geometry inside the overlap. " :
                                        $"Keep only the volume \"{targetName}\" shares with the other {others} object{plural}? ";

            ShowConfirm(question +
                        "The other objects are hidden, not deleted - re-show them from the list " +
                        "above. Undo (Z) restores the shape.", extraContent =>
            {
                UIFactory.CreateLabel(extraContent, "Voxel Resolution (across the target)", 12, FontStyle.Normal);
                UIFactory.CreateSlider(extraContent, 4f, 500f, resolution, v => resolution = Mathf.RoundToInt(v));
                UIFactory.CreateToggle(extraContent, "Delete the other objects instead of hiding", deleteOthers, v => deleteOthers = v);
            }, () => DoBoolean(op, resolution, deleteOthers));
        }

        private void DoBoolean(BooleanOp op, int resolution, bool deleteOthers)
        {
            if (_selection == null) return;
            SculptableMesh target = _selection.PrimarySelection;
            var others = new List<SculptableMesh>(_selection.SelectedSet);
            others.Remove(target);

            bool ok = MeshBooleanTool.Apply(target, others, op, resolution,
                                            hideOthers: true, deleteOthers: deleteOthers, out string message);
            SetStatus(message, ok ? OkColor : ErrorColor, hold: ok);
            RefreshList();
        }

        // ------------------------------------------------------------------- confirmation modal

        /// Small blocking overlay (dim backdrop + centered panel) for this panel's two
        /// whole-object actions. Join is destructive with no undo, so the prompt is the
        /// mitigation this codebase already uses elsewhere for undo-free destructive ops;
        /// The boolean ops ARE undoable, and prompt because they need a resolution picked before
        /// committing to a slow, mesh-replacing rebuild. buildExtraContent (optional) fills the
        /// space between the message and the Cancel/Confirm row with exactly those settings.
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
