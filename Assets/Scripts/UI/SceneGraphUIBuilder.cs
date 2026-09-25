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
    /// Transpose/Scale gizmo mode toolbar, separate/linked Mirror, Join, and - merged in further down -
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
        // Object-list text for either half of a live mirror pair (see MirrorLink). The name can't
        // say it: a separate mirror copy is named exactly the same way.
        private static readonly Color LinkedTextColor = new Color(1f, 0.82f, 0.45f);

        private SelectionManager _selection;
        private PrimitiveSpawner _spawner;
        private SculptController _controller;
        private TransformGizmo _gizmo;

        private Transform _listContent;
        private readonly List<GameObject> _listRows = new List<GameObject>();
        private int _lastShownSelectionVersion = -1;

        private Image _sculptModeImg, _transposeModeImg, _scaleModeImg, _zsphereModeImg;
        private GizmoMode _lastShownToolMode = GizmoMode.Sculpt;
        private Button _joinButton;
        private Button _subtractButton, _unionButton, _intersectButton;
        private GameObject _confirmModalGO;

        // Timelapse transport - see BuildTimelapseSection. The button's own label is retargeted
        // between Start and Stop rather than swapping two buttons, so the control never moves
        // under the cursor mid-session.
        private Text _timelapseButtonLabel, _timelapseStatus;
        private bool _timelapseWasRecording;

        private static readonly Color RecordingColor = new Color(0.95f, 0.45f, 0.4f);

        // Follows the ACTUAL screen mode, not button presses - F11 and Alt+Enter change it too.
        private Text _fullscreenButtonLabel;
        private bool _fullscreenWasOn;

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

        // The mirror controls that follow the selection: Mirror Linked is refused for an object
        // already in a pair, Finalize needs one, and the note says which (see
        // RefreshMirrorControls).
        private Button _mirrorLinkedButton, _finalizeMirrorButton;
        private Text _mirrorNote;

        // Scene-file (Import/Load/Save) display - see the old SaveLoadUIBuilder this was merged
        // from for the reasoning behind each piece. The document itself (current file, the
        // save/load work) is SceneDocumentController's.
        private Text _statusLabel;
        private float _statusClearAt = -1f;
        private InputField _fallbackField;
        private SceneDocumentController _document;

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

            _document = GetComponent<SceneDocumentController>();
            if (_document == null) _document = gameObject.AddComponent<SceneDocumentController>();
            _document.FallbackPathText = () => _fallbackField != null ? _fallbackField.text : null;
            _document.Status += (message, ok) => SetStatus(message, ok ? OkColor : ErrorColor, hold: ok);

            BuildUI();
            RefreshList();
            RefreshMultiObjectButtons();
            RefreshToolButtons();

            // Now that the build runs in a real OS window (see the Fullscreen Mode player
            // setting), it has a native title-bar close button too - without this, that button
            // would bypass the Exit prompt entirely and close over unsaved work. Skipped in the
            // Editor: Play Mode has no OS window of its own, and this would instead fire every
            // time Play is stopped.
#if !UNITY_EDITOR
            Application.wantsToQuit += OnWantsToQuit;
#endif
        }

#if !UNITY_EDITOR
        private void OnDestroy()
        {
            Application.wantsToQuit -= OnWantsToQuit;
        }

        private bool OnWantsToQuit()
        {
            if (_document.QuitConfirmed) return true;
            ShowExitConfirm();
            return false;
        }
#endif

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
            RefreshTimelapseSection(false);

            // Tools switch modes on their own too (ZSphere Convert and Lathe Create drop into
            // Sculpt, the Lathe section arms itself), so the highlight follows the gizmo rather
            // than only this panel's own clicks.
            GizmoMode shownMode = _gizmo != null ? _gizmo.Mode : GizmoMode.Sculpt;
            if (shownMode != _lastShownToolMode)
            {
                _lastShownToolMode = shownMode;
                RefreshToolButtons();
            }
            RefreshFullscreenButton(false);

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
            Transform panel = UIFactory.CreateScrollingPanelCanvas(
                "SceneGraphCanvas", new Vector2(1f, 1f), Vector2.zero, 260f);
            _canvasRoot = panel.root.gameObject;

            UIFactory.CreateLabel(panel, "Scene", 18, FontStyle.Bold);

            UIFactory.CreateButton(panel, "Import Object...", ImportObject, "Brings in a mesh file (OBJ/FBX/etc.) from disk as a new sculptable object.");
            UIFactory.CreateButton(panel, "Load Scene...", LoadScene, "Opens a saved .sculpt scene file, replacing everything currently in the scene.");
            UIFactory.CreateButton(panel, "Save", Save, "Saves over the current scene file. Prompts for a location the first time.");
            UIFactory.CreateButton(panel, "Save As...", SaveAs, "Saves the current scene to a new file.");
            UIFactory.CreateButton(panel, "Exit", ShowExitConfirm, "Closes the app. Offers to save first.");
            var fullscreenButton = UIFactory.CreateButton(panel, "Fullscreen (F11)", FullscreenController.Toggle,
                "Fills the whole screen, hiding the window's title bar and the taskbar. F11 or Alt+Enter toggles it too.");
            _fullscreenButtonLabel = fullscreenButton.GetComponentInChildren<Text>();
            RefreshFullscreenButton(true);

            if (!FileDialog.IsSupported)
            {
                UIFactory.CreateLabel(panel, "File path", 11, FontStyle.Normal);
                _fallbackField = UIFactory.CreateInputField(panel, SceneSerializer.DefaultPath, null);
            }

            _statusLabel = UIFactory.CreateLabel(panel, string.Empty, 11, FontStyle.Normal);
            ShowHint();

            UIFactory.CreateLabel(panel, "Add Primitive", 13, FontStyle.Normal);
            GameObject addRow1 = UIFactory.CreateRow(panel, 26f);
            UIFactory.CreateButton(addRow1.transform, "Cube", () => Spawn(PrimitiveShapeType.Cube), "Spawns a new sculptable cube.");
            UIFactory.CreateButton(addRow1.transform, "Sphere", () => Spawn(PrimitiveShapeType.Sphere), "Spawns a new sculptable sphere.");
            GameObject addRow2 = UIFactory.CreateRow(panel, 26f);
            UIFactory.CreateButton(addRow2.transform, "Cylinder", () => Spawn(PrimitiveShapeType.Cylinder), "Spawns a new sculptable cylinder.");
            UIFactory.CreateButton(addRow2.transform, "Capsule", () => Spawn(PrimitiveShapeType.Capsule), "Spawns a new sculptable capsule.");

            // A ZSphere rig belongs among the primitives even though it is not one: this is the
            // "start a model from nothing" row, and a blockout is a perfectly ordinary way to
            // start one - it just happens to become geometry at Convert rather than immediately.
            // Without an entry here the tool could only be reached by first spawning a primitive
            // to click near, which is exactly backwards for building a figure out of ZSpheres.
            GameObject addRow3 = UIFactory.CreateRow(panel, 26f);
            UIFactory.CreateButton(addRow3.transform, "ZSphere Rig", StartZSphereRig,
                "Starts a jointed skeleton of spheres you can pose and grow, then convert into a sculptable mesh - good for blocking out a figure from scratch.");
            // The lathe is the same kind of entry: a way to start a model, which becomes geometry
            // at Create. Its controls open in the Lathe section below.
            UIFactory.CreateButton(addRow3.transform, "Lathe", StartLathe,
                "Shapes a turned solid - a vase, bowl, bottle, knob or ring - by dragging its outline, like clay on a wheel.");

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
            _cloneButton = UIFactory.CreateButton(panel, "Clone Selected", CloneSelected, "Duplicates the selected object as a new, independent copy.");

            UIFactory.CreateLabel(panel, "Tool", 13, FontStyle.Normal);
            GameObject toolRow = UIFactory.CreateRow(panel, 26f);
            _sculptModeImg = UIFactory.CreateButton(toolRow.transform, "Sculpt", () => SetGizmoMode(GizmoMode.Sculpt),
                "Brush sculpting on the selected object.").GetComponent<Image>();
            _transposeModeImg = UIFactory.CreateButton(toolRow.transform, "Transpose", () => SetGizmoMode(GizmoMode.Transpose),
                "Move/rotate gizmo for repositioning the selected object.").GetComponent<Image>();
            GameObject toolRow2 = UIFactory.CreateRow(panel, 26f);
            _scaleModeImg = UIFactory.CreateButton(toolRow2.transform, "Scale", () => SetGizmoMode(GizmoMode.Scale),
                "Scale gizmo for resizing the selected object.").GetComponent<Image>();
            _zsphereModeImg = UIFactory.CreateButton(toolRow2.transform, "ZSpheres", () => SetGizmoMode(GizmoMode.ZSphere),
                "Edits the selected object's ZSphere rig, if it has one.").GetComponent<Image>();

            BuildZSphereSection(panel);

            // Lathe: its own builder, filled into a foldout like the Turntable's and self-installed
            // the same way, since the scene predates it.
            var lathe = FindFirstObjectByType<LatheUIBuilder>();
            if (lathe == null) lathe = gameObject.AddComponent<LatheUIBuilder>();
            lathe.BuildContent(UIFactory.CreateFoldoutSection(panel, "Lathe (Turned Solids)", false));

            UIFactory.CreateLabel(panel, "Mirror Selected Across Sphere", 13, FontStyle.Normal);
            GameObject mirrorRow = UIFactory.CreateRow(panel, 22f);
            UIFactory.CreateToggle(mirrorRow.transform, "X", _mirrorX, v => _mirrorX = v, tooltip: "Mirror across the X axis.");
            UIFactory.CreateToggle(mirrorRow.transform, "Y", _mirrorY, v => _mirrorY = v, tooltip: "Mirror across the Y axis.");
            UIFactory.CreateToggle(mirrorRow.transform, "Z", _mirrorZ, v => _mirrorZ = v, tooltip: "Mirror across the Z axis.");
            GameObject mirrorButtonRow = UIFactory.CreateRow(panel, 26f);
            UIFactory.CreateButton(mirrorButtonRow.transform, "Mirror Separate", () => DoMirror(false),
                "Creates a mirrored copy across the checked axes. The copy is its own object from then on.");
            _mirrorLinkedButton = UIFactory.CreateButton(mirrorButtonRow.transform, "Mirror Linked", () => DoMirror(true),
                "Creates a mirrored copy that stays linked: move, rotate, scale or sculpt either one and the other " +
                "follows, mirrored, until you finalize. Remesh, Trim, Boolean and Join finalize it too.");
            _finalizeMirrorButton = UIFactory.CreateButton(panel, "Finalize Mirror", FinalizeMirror,
                "Ends the selected object's mirror link, leaving two separate objects exactly where they are.");
            // Built with two lines because CreateLabel sizes itself from the newlines it starts with,
            // and every note RefreshMirrorControls writes is two lines.
            _mirrorNote = UIFactory.CreateLabel(panel, "\n", 11, FontStyle.Italic);

            UIFactory.CreateLabel(panel, "Join (destructive)", 13, FontStyle.Normal);
            _joinButton = UIFactory.CreateButton(panel, "Join Selected", ShowJoinConfirm,
                "Merges every selected object into one mesh, deleting the originals.");

            // One row of three rather than a button each: they are the same gesture with the
            // same prompt, and the panel already spends a lot of vertical space above this.
            UIFactory.CreateLabel(panel, "Boolean (watertight)", 13, FontStyle.Normal);
            GameObject booleanRow = UIFactory.CreateRow(panel, 26f);
            _subtractButton = UIFactory.CreateButton(booleanRow.transform, "Subtract", () => ShowBooleanConfirm(BooleanOp.Subtract),
                "Cuts the other selected object(s) out of the first.");
            _unionButton = UIFactory.CreateButton(booleanRow.transform, "Union", () => ShowBooleanConfirm(BooleanOp.Union),
                "Merges the selected objects into one solid shape.");
            _intersectButton = UIFactory.CreateButton(booleanRow.transform, "Intersect", () => ShowBooleanConfirm(BooleanOp.Intersect),
                "Keeps only the volume where the selected objects overlap.");

            // Mold Maker: a whole workflow of its own, so what sits here is only the door into
            // it - a button and a line of state. The tools themselves open as a full-screen
            // workspace that stands in front of this panel, because molding is a mode rather
            // than a tool and every control it needs would not fit in this column beside the
            // scene list. See MoldUIBuilder. Self-installed if the scene predates the feature.
            var mold = FindFirstObjectByType<Sculpting.Molding.MoldUIBuilder>();
            if (mold == null) mold = gameObject.AddComponent<Sculpting.Molding.MoldUIBuilder>();
            mold.BuildContent(UIFactory.CreateFoldoutSection(panel, "Mold Maker", true));

            BuildTimelapseSection(panel);

            // Turntable: spin, clean view and the 360 loop recorder. Its own builder, filled into
            // a foldout like the Mold Maker's, and self-installed the same way.
            var turntable = FindFirstObjectByType<TurntableUIBuilder>();
            if (turntable == null) turntable = gameObject.AddComponent<TurntableUIBuilder>();
            turntable.BuildContent(UIFactory.CreateFoldoutSection(panel, "Turntable", false));

            // Material / Presentation used to be separate always-open panels (bottom-center,
            // bottom-right). Merged into this panel as collapsible sections - one panel to dock
            // instead of several, and each section starts collapsed so the panel stays small
            // until the user opens the one they want. These builders no longer build their own
            // canvas - they just fill whatever content transform they're handed (see
            // LightingUIBuilder.BuildContent's remarks).
            //
            // Lighting is handed the panel ITSELF rather than a section of its own: its two
            // parts - the Lighting presets and HDRI Environment - make their own top-level
            // foldouts.
            var lighting = FindFirstObjectByType<LightingUIBuilder>();
            var material = FindFirstObjectByType<MaterialUIBuilder>();
            var presentation = FindFirstObjectByType<PostProcessingUIBuilder>();
            if (lighting != null) lighting.BuildContent(panel);
            if (material != null) material.BuildContent(UIFactory.CreateFoldoutSection(panel, "Material", false));
            if (presentation != null) presentation.BuildContent(UIFactory.CreateFoldoutSection(panel, "Presentation", false));
        }

        /// Start/Stop for the sculpting timelapse, so a recording can be driven without leaving
        /// the app for an editor window.
        ///
        /// Editor-only, and hidden rather than disabled in a build: Unity Recorder ships no
        /// runtime recording API at all (see TimelapseControl), so in a player this button could
        /// never do anything, and a permanently dead control is worse than no control.
        private void BuildTimelapseSection(Transform panel)
        {
            if (!Application.isEditor) return;

            UIFactory.CreateLabel(panel, "Timelapse", 13, FontStyle.Normal);

            Button button = UIFactory.CreateButton(panel, "Start Timelapse", TimelapseControl.RequestToggle,
                "Records a timelapse that follows your viewing angle and the part you are " +
                "sculpting, capturing only while the model is actually changing. Settings live " +
                "in Window > Sculpting > Timelapse Recorder.");
            _timelapseButtonLabel = button.GetComponentInChildren<Text>();

            _timelapseStatus = UIFactory.CreateLabel(panel, string.Empty, 11, FontStyle.Italic);
            _timelapseStatus.color = HintColor;

            RefreshTimelapseSection(true);
        }

        private void RefreshFullscreenButton(bool force)
        {
            if (_fullscreenButtonLabel == null) return;
            bool on = FullscreenController.IsFullscreen;
            if (!force && on == _fullscreenWasOn) return;
            _fullscreenWasOn = on;
            _fullscreenButtonLabel.text = on ? "Exit Fullscreen (F11)" : "Fullscreen (F11)";
        }

        /// Reflects what the recorder is ACTUALLY doing rather than what was last asked of it -
        /// the editor window can start and stop the same recording, and the button has to follow
        /// that rather than track its own presses.
        private void RefreshTimelapseSection(bool force)
        {
            if (_timelapseButtonLabel == null) return;

            bool recording = TimelapseControl.IsRecording;
            if (force || recording != _timelapseWasRecording)
            {
                _timelapseWasRecording = recording;
                _timelapseButtonLabel.text = recording ? "Stop Timelapse" : "Start Timelapse";
                _timelapseButtonLabel.color = recording ? RecordingColor : Color.white;
            }

            // Not gated on the flag above: the frame count inside it changes every frame while
            // recording, which is exactly the reassurance the line exists to give.
            string status = recording
                ? TimelapseControl.Status
                : (Application.isPlaying ? string.Empty : "Enter Play mode to record.");
            if (_timelapseStatus.text != status) _timelapseStatus.text = status;
        }

        private void Spawn(PrimitiveShapeType type) => _spawner?.SpawnPrimitive(type);

        /// Arms the lathe. Its first activation seats a starting shape beside the scene and centres
        /// the view on it (see LatheController.StartNewShape); later ones pick up the profile where
        /// it was left.
        private void StartLathe()
        {
            LatheController.Install();
            SetGizmoMode(GizmoMode.Lathe);
        }

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

        // Thin forwarders onto SceneDocumentController: the panel's buttons bind to these.
        private void ImportObject() => _document.ImportObject();
        private void Save() => _document.Save();
        private void SaveAs() => _document.SaveAs();

        /// Opens a saved scene, then asks how to bring it in. The prompt exists because both
        /// answers are reasonable and one of them is destructive: replacing discards everything
        /// currently in the scene, and there is no undo for that. Asking after the file is
        /// chosen (rather than offering two buttons up front) keeps the panel to one obvious
        /// action and puts the question at the moment it can be answered concretely - the file's
        /// own name and object count are in the prompt.
        private void LoadScene()
        {
            string path = _document.PickPath("Load scene", "sculpt");
            if (path == null) return;

            string name = Path.GetFileName(path);
            UIFactory.ShowModal(
                $"\"{name}\"\n\nReplace everything in the scene, or add its objects to what you have?",
                null,
                new UIFactory.ModalChoice("Add to current scene", () =>
                {
                    if (_document.AddFromScene(path, out int count, out string error))
                        SetStatus($"Added {count} object{(count == 1 ? "" : "s")} from {name}", OkColor, hold: true);
                    else
                        SetStatus("Load failed: " + error, ErrorColor, hold: false);
                }),
                new UIFactory.ModalChoice("Replace scene (cannot be undone)", () =>
                {
                    if (_document.LoadReplacing(path, out string error))
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

        /// The Exit button's prompt - also what the OS window's own close button triggers (see
        /// OnWantsToQuit). Cancel is added automatically by ShowModal, so closing the app always
        /// has a way back.
        private void ShowExitConfirm()
        {
            UIFactory.ShowModal(
                "Exit the app?\n\nSave your work first?",
                null,
                new UIFactory.ModalChoice("Save and Exit", _document.ExitSaveAndQuit),
                new UIFactory.ModalChoice("Exit Without Saving", _document.QuitNow));
        }

        private void SetStatus(string message, Color color, bool hold)
        {
            _statusLabel.text = message;
            _statusLabel.color = color;
            _statusClearAt = hold ? Time.unscaledTime + StatusHoldSeconds : -1f;
        }

        private void ShowHint()
        {
            _statusLabel.text = "Import adds a model (.obj, .stl). Load opens a saved scene (.sculpt).";
            _statusLabel.color = HintColor;
            _statusClearAt = -1f;
        }

        // Rebuilds the Sculpting Tools panel, plus this panel's own BuildUI, so every control
        // shows the loaded scene's values rather than the ones it was built from at startup.
        // This panel can no longer skip rebuilding itself the way the old separate
        // SaveLoadUIBuilder could: it carries the Studio Lighting/Material/Presentation sections
        // now (merged in), which DO go stale the same way brush/material/lighting settings do
        // elsewhere.
        private void RebuildOtherPanels()
        {
            var sculptBuilder = FindFirstObjectByType<SculptUIBuilder>();
            if (sculptBuilder != null) sculptBuilder.RebuildForLoadedScene();

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
                    // Either half of a live mirror pair says so in its text colour and tooltip.
                    MirrorLink link = obj.LinkedMirror;
                    SculptableMesh partner = link != null ? link.PartnerOf(obj) : null;
                    string tooltip = "Click to select, Ctrl+click to add to selection, double-click to rename.";
                    if (partner != null)
                        tooltip = $"Mirror-linked with \"{partner.name}\": moving or sculpting either one mirrors onto the other. " + tooltip;

                    Button nameBtn = UIFactory.CreateButton(row.transform, obj.name, () => OnRowClicked(obj), tooltip);
                    nameBtn.GetComponent<Image>().color = _selection.PrimarySelection == obj ? UIFactory.ActiveColor
                        : _selection.IsSelected(obj) ? new Color(0.4f, 0.4f, 0.45f) : UIFactory.InactiveColor;
                    if (partner != null) nameBtn.GetComponentInChildren<Text>().color = LinkedTextColor;
                    AddDoubleClickHandler(nameBtn.gameObject, () => BeginInlineRename(obj));
                }

                UIFactory.CreateToggle(row.transform, "Vis", obj.Visible, v => _selection.SetVisible(obj, v),
                    tooltip: "Shows or hides this object in the viewport.");
                UIFactory.CreateButton(row.transform, "X", () => ShowDeleteConfirm(obj), "Deletes this object (asks to confirm).");
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
            RefreshMirrorControls(primary);
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
            SetGizmoMode(GizmoMode.ZSphere);
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

            if (stickyShowing)
            {
                _zsphereStatusLabel.text = _zsphereSticky;
                _zsphereStatusLabel.color = OkColor;
                return;
            }
            _zsphereSticky = null;

            int spheres = _zsphere.SphereCount;
            if (!armed)
            {
                _zsphereStatusLabel.text = spheres > 0
                    ? $"Rig hidden ({spheres} spheres). Pick ZSpheres in Tool to edit it."
                    : "Pick ZSpheres in Tool (or Add > ZSphere Rig), then click in the viewport.";
                _zsphereStatusLabel.color = HintColor;
            }
            else if (spheres == 0)
            {
                _zsphereStatusLabel.text = "Click in the viewport to place the first sphere - drag to size it.";
                _zsphereStatusLabel.color = HintColor;
            }
            else if (error != null)
            {
                _zsphereStatusLabel.text = error;
                _zsphereStatusLabel.color = ErrorColor;
            }
            else if (triCount > 0)
            {
                _zsphereStatusLabel.text = $"{spheres} spheres | skin {triCount:N0} tris{(final ? string.Empty : " (live draft)")}";
                _zsphereStatusLabel.color = OkColor;
            }
            else
            {
                _zsphereStatusLabel.text = $"{spheres} spheres.";
                _zsphereStatusLabel.color = HintColor;
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

        private void DoMirror(bool linked)
        {
            if (_selection == null || _spawner == null) return;
            if (!_mirrorX && !_mirrorY && !_mirrorZ) return;

            SculptableMesh target = _selection.PrimarySelection;
            SculptableMesh main = _spawner.MainObject;
            if (target == null || main == null) return;

            MeshMirror.MirrorAcross(target, main.transform.position, _mirrorX, _mirrorY, _mirrorZ, linked);
        }

        /// Nothing to refresh here: ending a link bumps SelectionVersion, which redraws the list and
        /// this section on the next Update.
        private void FinalizeMirror()
        {
            SculptableMesh primary = _selection != null ? _selection.PrimarySelection : null;
            MirrorLink link = primary != null ? primary.LinkedMirror : null;
            if (link != null) link.Unlink();
        }

        private void RefreshMirrorControls(SculptableMesh primary)
        {
            MirrorLink link = primary != null ? primary.LinkedMirror : null;
            // One pair per object (see MirrorLink.Create); a Separate copy of a linked object is fine.
            if (_mirrorLinkedButton != null) _mirrorLinkedButton.interactable = primary != null && link == null;
            if (_finalizeMirrorButton != null) _finalizeMirrorButton.interactable = link != null;
            if (_mirrorNote == null) return;

            SculptableMesh partner = link != null ? link.PartnerOf(primary) : null;
            if (partner == null)
            {
                _mirrorNote.text = "Linked copies follow each other's moves\nand sculpting until you finalize.";
                _mirrorNote.color = HintColor;
                return;
            }

            // A deleted half is only parked until its undo step expires (see
            // SelectionManager.DeleteObject), and the pair holds through that - so it is still
            // named, just flagged.
            string deleted = partner.gameObject.activeInHierarchy ? string.Empty : " (deleted)";
            _mirrorNote.text = $"Linked with \"{partner.name}\"{deleted} across {AxisNames(link.Signs)}.\n" +
                               "Moves and sculpting mirror live.";
            _mirrorNote.color = OkColor;
        }

        private static string AxisNames(Vector3 signs) =>
            (signs.x < 0f ? "X" : string.Empty) + (signs.y < 0f ? "Y" : string.Empty) + (signs.z < 0f ? "Z" : string.Empty);

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
                UIFactory.CreateToggle(extraContent, "Remesh after Join", remeshAfter, v => remeshAfter = v,
                    tooltip: "Rebuilds the joined result on a clean voxel grid, welding away the seams where the objects met.");
                UIFactory.CreateLabel(extraContent, "Remesh Resolution", 12, FontStyle.Normal);
                UIFactory.CreateSlider(extraContent, 4f, 500f, resolution, v => resolution = Mathf.RoundToInt(v),
                    "Voxel density of the post-join remesh - higher is more detailed but slower.");
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
                UIFactory.CreateSlider(extraContent, 4f, 500f, resolution, v => resolution = Mathf.RoundToInt(v),
                    "Voxel density the boolean is computed at - higher captures finer cutter detail but is slower.");
                UIFactory.CreateToggle(extraContent, "Delete the other objects instead of hiding", deleteOthers, v => deleteOthers = v,
                    tooltip: "Permanently deletes the other selected objects instead of just hiding them (hidden ones can be re-shown from the list).");
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

        // ----------------------------------------------------------------------------- delete

        /// Entry point for the Delete key (see SculptController.Input.HandleDeleteObjectKey).
        /// Targets the primary selection, matching what the Delete key deletes in every other
        /// DCC.
        internal void ShowDeleteSelectedConfirm()
        {
            if (_selection != null) ShowDeleteConfirm(_selection.PrimarySelection);
        }

        /// Confirms before calling SelectionManager.DeleteObject. DeleteObject is undoable (see
        /// its remarks - it parks the object rather than destroying it outright), but an
        /// accidental press - the Delete key especially, one row of keys from Backspace - still
        /// shouldn't yank a model out of the viewport unasked; the prompt names the plain Z key,
        /// not Ctrl+Z, since Ctrl+Z is intercepted by the Unity Editor's own global Undo during
        /// development (see HandleUndoRedoKeys's remarks) rather than reaching this app's history.
        /// Shared by both the row's X button and the Delete key so there's exactly one place a
        /// deletion can happen without this prompt.
        private void ShowDeleteConfirm(SculptableMesh obj)
        {
            if (obj == null || _selection == null) return;
            ShowConfirm($"Delete \"{obj.name}\"? Undo (Z) restores it.", null, () => _selection.DeleteObject(obj));
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
