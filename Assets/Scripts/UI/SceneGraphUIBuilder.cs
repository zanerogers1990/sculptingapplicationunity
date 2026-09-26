using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Sculpting.IO;

namespace Sculpting
{
    /// Builds the right-hand Scene panel. Pinned at the top: the tool row (Sculpt / Move-Rotate /
    /// Scale / SSpheres) and the status line every action here reports to. Below, one UICategory
    /// each: File, Objects (add primitives, the live object list, rename/clone/reset), Blockout
    /// (SSpheres, Lathe), Mirror & Combine (mirror copies, booleans, Join), Environment
    /// (lighting, HDRI, material, presentation, turntable, timelapse) and Mold Maker. Docked
    /// flush to the top-right corner at full window height, fixed there, mirroring
    /// SculptUIBuilder's Sculpt panel on the left.
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

        // Object-list text for a live-mirrored object and its mirror copy rows (see MirrorRepeater).
        private static readonly Color MirroredTextColor = new Color(1f, 0.82f, 0.45f);

        private SelectionManager _selection;
        private PrimitiveSpawner _spawner;
        private SculptController _controller;
        private TransformGizmo _gizmo;

        private Transform _listContent;
        private readonly List<ListRow> _listRows = new List<ListRow>();

        /// One object's row in the list, kept so a refresh that changes nothing but what the rows
        /// show (selection, names, visibility, mirror links) can update them in place.
        private sealed class ListRow
        {
            public SculptableMesh Obj;
            // -1 for the object's own row, otherwise the index of the live mirror copy this row
            // stands for (see MirrorRepeater) - listed indented under the object.
            public int View = -1;
            public GameObject Go;
            public bool Renaming;
            public Button NameButton;
            public Text NameText;
            public Toggle Visible;
        }
        private int _lastShownSelectionVersion = -1;

        private Image _sculptModeImg, _transposeModeImg, _scaleModeImg, _ssphereModeImg;
        private readonly Image[] _orientationImgs = new Image[3];
        private GizmoMode _lastShownToolMode = GizmoMode.Sculpt;
        private Button _joinButton;
        private Button _subtractButton, _unionButton, _intersectButton;
        private GameObject _confirmModalGO;

        // Timelapse transport - see BuildTimelapseSection. The button's own label is retargeted
        // between Start and Stop rather than swapping two buttons, so the control never moves
        // under the cursor mid-session.
        private Text _timelapseButtonLabel, _timelapseStatus;
        private bool _timelapseWasRecording;

        private static readonly Color RecordingColor = UIFactory.StatusErrorColor;

        // Follows the ACTUAL screen mode, not button presses - F11 and Alt+Enter change it too.
        private Text _fullscreenButtonLabel;
        private bool _fullscreenWasOn;

        // Rename field for the primary selection. Kept out of the per-object rows: at this
        // panel's width a row already carries a name button, a visibility toggle and a delete
        // button, and a fourth control per row would leave none of them comfortably clickable.
        // One field that follows the selection also matches how the Material/Lighting panels
        // already work - they edit whatever is selected rather than repeating themselves per
        // object.
        private InputField _renameField;
        private Button _cloneButton, _resetShapeButton;

        // The object whose row is currently showing an inline rename field instead of its name
        // button - set by double-clicking a row (see AddDoubleClickHandler/BeginInlineRename),
        // cleared once that edit commits or is cancelled. At most one row is ever in this state.
        private SculptableMesh _renamingObject;

        // Defaults to X only - the common bilateral symmetry axis for character parts (left/
        // right limbs either side of a centered torso), matching the user's own "remove an arm
        // to see the torso" framing. Follows the selection's live mirror when it has one (see
        // RefreshMirrorControls).
        private bool _mirrorX = true, _mirrorY, _mirrorZ;
        private Toggle _mirrorXToggle, _mirrorYToggle, _mirrorZToggle;

        // The live-mirror controls that follow the selection (see RefreshMirrorControls).
        private Button _mirrorButton, _validateMirrorButton, _removeMirrorButton;
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

        // The SSphere section - see SSphereUIBuilder.
        private SSphereUIBuilder _ssphereUI;

        // Start(), not Awake() - reads/uses SelectionManager.AllObjects (via RefreshList),
        // which needs every SculptableMesh's OnEnable to have already registered - see
        // SculptUIBuilder's own Start() remarks for the full reasoning.
        private void Start()
        {
            _selection = FindFirstObjectByType<SelectionManager>();
            _spawner = FindFirstObjectByType<PrimitiveSpawner>();
            _controller = FindFirstObjectByType<SculptController>();
            _gizmo = FindFirstObjectByType<TransformGizmo>();

            _document = GetComponent<SceneDocumentController>();
            if (_document == null) _document = gameObject.AddComponent<SceneDocumentController>();
            _document.FallbackPathText = () => _fallbackField != null ? _fallbackField.text : null;
            _document.Status += (message, ok) => SetStatus(message, ok ? UIFactory.StatusOkColor : UIFactory.StatusErrorColor, hold: ok);

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

            RefreshTimelapseSection(false);

            // Tools switch modes on their own too (SSphere Convert and Lathe Create drop into
            // Sculpt, the Lathe section arms itself), so the highlight follows the gizmo rather
            // than only this panel's own clicks.
            GizmoMode shownMode = _gizmo != null ? _gizmo.Mode : GizmoMode.Sculpt;
            if (shownMode != _lastShownToolMode)
            {
                _lastShownToolMode = shownMode;
                RefreshToolButtons();
                // Its controls live in the Blockout category, which may well be closed.
                if (shownMode == GizmoMode.SSphere && _ssphereUI != null) UICategory.Reveal(_ssphereUI.Section);
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

        // Category accents - see SculptUIBuilder's, which the left panel uses the same way.
        private static readonly Color FileAccent = new Color(0.6f, 0.72f, 0.9f);
        private static readonly Color ObjectsAccent = new Color(0.3f, 0.6f, 1f);
        private static readonly Color BlockoutAccent = new Color(0.95f, 0.68f, 0.3f);
        private static readonly Color CombineAccent = new Color(0.95f, 0.45f, 0.62f);
        private static readonly Color EnvironmentAccent = new Color(0.95f, 0.85f, 0.4f);
        private static readonly Color MoldAccent = new Color(0.62f, 0.62f, 0.68f);

        private void BuildUI()
        {
            // Docked flush to the top-right corner, full window height, fixed there - no
            // longer draggable (see UIFactory's now-removed DraggablePanel). Sits opposite
            // SculptUIBuilder's Sculpting Tools panel, which docks the same way on the left.
            Transform panel = UIFactory.CreateScrollingPanelCanvas(
                "SceneGraphCanvas", new Vector2(1f, 1f), Vector2.zero, 260f);
            _canvasRoot = panel.root.gameObject;

            // ------------------------------------------ always visible: title, tool, status line
            UIFactory.CreateLabel(panel, "Scene", 20, FontStyle.Bold);

            // The tool decides what the mouse does in the viewport, so it stays on screen rather
            // than living in a category that might be closed.
            GameObject toolRow = UIFactory.CreateRow(panel, 26f);
            _sculptModeImg = UIFactory.CreateButton(toolRow.transform, "Sculpt", () => SetGizmoMode(GizmoMode.Sculpt),
                "Brush sculpting on the selected object.").GetComponent<Image>();
            _transposeModeImg = UIFactory.CreateButton(toolRow.transform, "Move / Rotate", () => SetGizmoMode(GizmoMode.Transpose),
                "Move/rotate gizmo (Transpose) for repositioning the selected object.").GetComponent<Image>();
            GameObject toolRow2 = UIFactory.CreateRow(panel, 26f);
            _scaleModeImg = UIFactory.CreateButton(toolRow2.transform, "Scale", () => SetGizmoMode(GizmoMode.Scale),
                "Scale gizmo for resizing the selected object.").GetComponent<Image>();
            _ssphereModeImg = UIFactory.CreateButton(toolRow2.transform, "SSpheres", () => SetGizmoMode(GizmoMode.SSphere),
                "Edits the selected object's SSphere (Shape Sphere) rig, if it has one.").GetComponent<Image>();

            // Which way the Move/Rotate handles point - see GizmoOrientation.
            Text axesLabel = UIFactory.CreateLabel(panel, "Gizmo axes (Move / Rotate)", 10, FontStyle.Italic);
            axesLabel.color = UIFactory.StatusHintColor;
            GameObject axesRow = UIFactory.CreateRow(panel, 22f);
            string[] orientationNames = { "Auto", "Local", "Global" };
            string[] orientationTips =
            {
                "Move/Rotate handles follow the object's own axes, or the world axes when several objects are selected.",
                "Move/Rotate handles always follow the selected object's own axes.",
                "Move/Rotate handles always follow the world axes.",
            };
            for (int i = 0; i < 3; i++)
            {
                var orientation = (GizmoOrientation)i; // captured per iteration
                _orientationImgs[i] = UIFactory.CreateButton(axesRow.transform, orientationNames[i],
                    () => SetGizmoOrientation(orientation), orientationTips[i] + " Scale always uses the object's own axes.")
                    .GetComponent<Image>();
            }

            // Every operation on this panel reports here (save/load, boolean, SSpheres...), so it
            // is pinned too - a result must not land inside a closed category.
            _statusLabel = UIFactory.CreateLabel(panel, "\n", 11, FontStyle.Normal);
            ShowHint();

            BuildFileCategory(panel);
            BuildObjectsCategory(panel);
            BuildBlockoutCategory(panel);
            BuildCombineCategory(panel);
            BuildEnvironmentCategory(panel);
            BuildMoldCategory(panel);
        }

        private void BuildFileCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "scene.file", "File", FileAccent, true,
                () => _document != null && !string.IsNullOrEmpty(_document.CurrentSavePath)
                    ? Path.GetFileNameWithoutExtension(_document.CurrentSavePath) : "Unsaved",
                "Open, save, import and export.");
            category.Keywords = "file open load save import export obj stl sculpt exit quit fullscreen";
            Transform c = category.Content;

            GameObject row1 = UIFactory.CreateRow(c, 26f);
            UIFactory.CreateButton(row1.transform, "Load...", LoadScene, "Opens a saved .sculpt scene file - replacing the scene, or adding its objects to it.");
            UIFactory.CreateButton(row1.transform, "Save", Save, "Saves over the current scene file. Prompts for a location the first time.");
            UIFactory.CreateButton(row1.transform, "Save As...", SaveAs, "Saves the current scene to a new file.");
            GameObject row2 = UIFactory.CreateRow(c, 26f);
            UIFactory.CreateButton(row2.transform, "Import...", ImportObject, "Brings in a mesh file (.obj, .stl) from disk as a new sculptable object.");
            UIFactory.CreateButton(row2.transform, "Export OBJ...", ExportSelected, "Saves the selected mesh as an OBJ file - opens a dialog to pick the folder and name.");
            GameObject row3 = UIFactory.CreateRow(c, 26f);
            var fullscreenButton = UIFactory.CreateButton(row3.transform, "Fullscreen (F11)", FullscreenController.Toggle,
                "Fills the whole screen, hiding the window's title bar and the taskbar. F11 or Alt+Enter toggles it too.");
            _fullscreenButtonLabel = fullscreenButton.GetComponentInChildren<Text>();
            RefreshFullscreenButton(true);
            UIFactory.CreateButton(row3.transform, "Exit", ShowExitConfirm, "Closes the app. Offers to save first.");

            if (!FileDialog.IsSupported)
            {
                UIFactory.CreateLabel(c, "File path", 11, FontStyle.Normal);
                _fallbackField = UIFactory.CreateInputField(c, SceneSerializer.DefaultPath, null);
            }
        }

        private void ExportSelected()
        {
            if (_controller == null) return;
            string path = _controller.Export(out bool cancelled);
            if (path != null) SetStatus("Exported to " + path, UIFactory.StatusOkColor, hold: true);
            else if (cancelled) SetStatus("Export cancelled", UIFactory.StatusHintColor, hold: true);
            else SetStatus("Export failed - nothing selected", UIFactory.StatusErrorColor, hold: false);
        }

        private void BuildObjectsCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "scene.objects", "Objects", ObjectsAccent, true,
                ObjectsSummary, "Add shapes, pick what you're working on, rename, clone and hide objects.");
            category.Keywords = "object add new primitive cube sphere cylinder capsule list select rename clone duplicate hide show visible delete reset";
            Transform c = category.Content;

            UIFactory.CreateLabel(c, "Add", 12, FontStyle.Bold);
            GameObject addRow = UIFactory.CreateRow(c, 26f);
            UIFactory.CreateButton(addRow.transform, "Cube", () => Spawn(PrimitiveShapeType.Cube), "Spawns a new sculptable cube.");
            UIFactory.CreateButton(addRow.transform, "Sphere", () => Spawn(PrimitiveShapeType.Sphere), "Spawns a new sculptable sphere.");
            UIFactory.CreateButton(addRow.transform, "Cylinder", () => Spawn(PrimitiveShapeType.Cylinder), "Spawns a new sculptable cylinder.");
            UIFactory.CreateButton(addRow.transform, "Capsule", () => Spawn(PrimitiveShapeType.Capsule), "Spawns a new sculptable capsule.");

            Text listHint = UIFactory.CreateLabel(c, "Ctrl+click adds to selection. Double-click renames.", 10, FontStyle.Italic);
            listHint.color = UIFactory.StatusHintColor;
            var listGO = new GameObject("ObjectList", typeof(RectTransform));
            listGO.transform.SetParent(c, false);
            var vlg = listGO.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            listGO.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _listContent = listGO.transform;

            UIFactory.CreateLabel(c, "Selected Object", 12, FontStyle.Bold);
            _renameField = UIFactory.CreateInputField(c, string.Empty, RenameSelected);
            TooltipSystem.Attach(_renameField.gameObject, "The selected object's name - type to rename it.");
            GameObject selectedRow = UIFactory.CreateRow(c, 26f);
            _cloneButton = UIFactory.CreateButton(selectedRow.transform, "Clone", CloneSelected, "Duplicates the selected object as a new, independent copy.");
            _resetShapeButton = UIFactory.CreateButton(selectedRow.transform, "Reset Shape", () => _controller?.ResetMesh(),
                "Puts the selected object back to the shape it had before any sculpting. Undo (Z) brings the sculpt back.");
        }

        private string ObjectsSummary()
        {
            if (_selection == null) return string.Empty;
            int n = 0;
            foreach (SculptableMesh obj in _selection.AllObjects) if (obj != null) n++;
            return n == 1 ? "1 object" : n + " objects";
        }

        private void BuildBlockoutCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "scene.blockout", "Blockout", BlockoutAccent, false,
                tooltip: "Start a model from nothing: an SSphere (Shape Sphere) skeleton for figures, or a lathe for turned shapes.");
            category.Keywords = "ssphere sspheres rig skeleton armature figure lathe turned vase bowl bottle revolve blockout start";
            Transform c = category.Content;

            // Neither is a primitive, but both are ways to START a model - they just become
            // geometry at Convert/Create rather than immediately. Without an entry here the
            // SSphere tool could only be reached by first spawning a primitive to click near.
            GameObject startRow = UIFactory.CreateRow(c, 26f);
            UIFactory.CreateButton(startRow.transform, "New SSphere Rig", () => _ssphereUI?.StartSSphereRig(),
                "Starts a jointed skeleton of spheres you can pose and grow, then convert into a sculptable mesh - good for blocking out a figure from scratch.");
            UIFactory.CreateButton(startRow.transform, "New Lathe", StartLathe,
                "Shapes a turned solid - a vase, bowl, bottle, knob or ring - by dragging its outline, like clay on a wheel.");

            // SSpheres: its own builder, the same BuildContent pattern as the Lathe's below.
            _ssphereUI = GetComponent<SSphereUIBuilder>();
            if (_ssphereUI == null) _ssphereUI = gameObject.AddComponent<SSphereUIBuilder>();
            _ssphereUI.BuildContent(c, SetGizmoMode, RefreshToolButtons,
                (message, ok) => SetStatus(message, ok ? UIFactory.StatusOkColor : UIFactory.StatusErrorColor, hold: ok));

            // Lathe: its own builder, self-installed since the scene predates it.
            var lathe = FindFirstObjectByType<LatheUIBuilder>();
            if (lathe == null) lathe = gameObject.AddComponent<LatheUIBuilder>();
            lathe.BuildContent(UIFactory.CreateFoldoutSection(c, "Lathe (Turned Solids)", false));
        }

        private void BuildCombineCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "scene.combine", "Mirror & Combine", CombineAccent, false,
                () => _selection != null && _selection.SelectedSet.Count >= 2 ? _selection.SelectedSet.Count + " selected" : string.Empty,
                "Mirror copies of objects, and merge or cut objects together.");
            category.Keywords = "mirror copy instance live validate bake join merge boolean subtract union intersect cut combine";
            Transform c = category.Content;

            UIFactory.CreateLabel(c, "Mirror (across the world origin)", 12, FontStyle.Bold);
            GameObject mirrorRow = UIFactory.CreateRow(c, 22f);
            _mirrorXToggle = UIFactory.CreateToggle(mirrorRow.transform, "X", _mirrorX, v => SetMirrorAxis(0, v), tooltip: "Mirror across the world X plane.");
            _mirrorYToggle = UIFactory.CreateToggle(mirrorRow.transform, "Y", _mirrorY, v => SetMirrorAxis(1, v), tooltip: "Mirror across the world Y plane.");
            _mirrorZToggle = UIFactory.CreateToggle(mirrorRow.transform, "Z", _mirrorZ, v => SetMirrorAxis(2, v), tooltip: "Mirror across the world Z plane.");
            GameObject mirrorButtonRow = UIFactory.CreateRow(c, 26f);
            _mirrorButton = UIFactory.CreateButton(mirrorButtonRow.transform, "Mirror", ApplyMirror,
                "Adds a live mirrored copy of the selected object across the checked world planes. It is the same " +
                "mesh drawn again, so sculpting, remeshing or trimming either side shows on both, and moving one " +
                "moves the other. Select the copy in the list or the viewport to work from its side.");
            _validateMirrorButton = UIFactory.CreateButton(mirrorButtonRow.transform, "Validate", ValidateMirror,
                "Turns the live mirror copies into real, separate objects, exactly where they are.");
            _removeMirrorButton = UIFactory.CreateButton(mirrorButtonRow.transform, "Remove", RemoveMirror,
                "Removes the selected object's live mirror copies. Undo (Z) brings them back.");
            // Built with two lines because CreateLabel sizes itself from the newlines it starts with,
            // and every note RefreshMirrorControls writes is two lines.
            _mirrorNote = UIFactory.CreateLabel(c, "\n", 11, FontStyle.Italic);

            UIFactory.CreateLabel(c, "Combine (select 2+ objects)", 12, FontStyle.Bold);
            Text combineHint = UIFactory.CreateLabel(c, "The first-selected object is the one kept.", 10, FontStyle.Italic);
            combineHint.color = UIFactory.StatusHintColor;
            // One row of three: the same gesture with the same prompt.
            GameObject booleanRow = UIFactory.CreateRow(c, 26f);
            _subtractButton = UIFactory.CreateButton(booleanRow.transform, "Subtract", () => ShowBooleanConfirm(BooleanOp.Subtract),
                "Cuts the other selected object(s) out of the first.");
            _unionButton = UIFactory.CreateButton(booleanRow.transform, "Union", () => ShowBooleanConfirm(BooleanOp.Union),
                "Merges the selected objects into one solid shape.");
            _intersectButton = UIFactory.CreateButton(booleanRow.transform, "Intersect", () => ShowBooleanConfirm(BooleanOp.Intersect),
                "Keeps only the volume where the selected objects overlap.");
            _joinButton = UIFactory.CreateButton(c, "Join (no undo)", ShowJoinConfirm,
                "Merges every selected object into one mesh, deleting the originals. Unlike Union it keeps every shell as-is. Cannot be undone.");
        }

        /// Everything about how the model is SHOWN rather than shaped: lights, the HDRI, the
        /// material, camera effects and background, the turntable, and recording.
        private void BuildEnvironmentCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "scene.environment", "Environment", EnvironmentAccent, false,
                tooltip: "Lighting, HDRI, material, presentation effects, turntable and recording.");
            category.Keywords = "environment lighting light preset hdri sky material color colour shader matcap cavity " +
                                "presentation bloom vignette depth of field background turntable spin record timelapse render";
            Transform c = category.Content;

            // These builders never build a canvas of their own - they fill whatever content
            // transform they're handed. Lighting is handed the category ITSELF: its two parts,
            // the Lighting presets and HDRI Environment, make their own sub-sections.
            var lighting = FindFirstObjectByType<LightingUIBuilder>();
            var material = FindFirstObjectByType<MaterialUIBuilder>();
            var presentation = FindFirstObjectByType<PostProcessingUIBuilder>();
            if (lighting != null) lighting.BuildContent(c);
            if (material != null) material.BuildContent(UIFactory.CreateFoldoutSection(c, "Material", false));
            if (presentation != null) presentation.BuildContent(UIFactory.CreateFoldoutSection(c, "Presentation", false));

            // Turntable: spin, clean view and the 360 loop recorder. Self-installed like the lathe.
            var turntable = FindFirstObjectByType<TurntableUIBuilder>();
            if (turntable == null) turntable = gameObject.AddComponent<TurntableUIBuilder>();
            turntable.BuildContent(UIFactory.CreateFoldoutSection(c, "Turntable", false));

            BuildTimelapseSection(c);
        }

        private void BuildMoldCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "scene.mold", "Mold Maker", MoldAccent, false,
                tooltip: "Turn the model into a two-part injection mold (experimental).");
            category.Keywords = "mold mould cavity lure injection print vents pins sprue";

            // A whole workflow of its own, so what sits here is only the door into it - a button
            // and a line of state. The tools open as a full-screen workspace in front of this
            // panel. See MoldUIBuilder. Self-installed if the scene predates the feature.
            var mold = FindFirstObjectByType<Sculpting.Molding.MoldUIBuilder>();
            if (mold == null) mold = gameObject.AddComponent<Sculpting.Molding.MoldUIBuilder>();
            mold.BuildContent(category.Content);
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

            panel = UIFactory.CreateFoldoutSection(panel, "Timelapse", false);

            Button button = UIFactory.CreateButton(panel, "Start Timelapse", TimelapseControl.RequestToggle,
                "Records a timelapse that follows your viewing angle and the part you are " +
                "sculpting, capturing only while the model is actually changing. Settings live " +
                "in Window > Sculpting > Timelapse Recorder.");
            _timelapseButtonLabel = button.GetComponentInChildren<Text>();

            _timelapseStatus = UIFactory.CreateLabel(panel, string.Empty, 11, FontStyle.Italic);
            _timelapseStatus.color = UIFactory.StatusHintColor;

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
                        SetStatus($"Added {count} object{(count == 1 ? "" : "s")} from {name}", UIFactory.StatusOkColor, hold: true);
                    else
                        SetStatus("Load failed: " + error, UIFactory.StatusErrorColor, hold: false);
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
                        SetStatus("Loaded " + name, UIFactory.StatusOkColor, hold: true);
                    }
                    else
                    {
                        SetStatus("Load failed: " + error, UIFactory.StatusErrorColor, hold: false);
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
            _statusLabel.text = "Import adds a model (.obj / .stl). Load opens a saved .sculpt scene.";
            _statusLabel.color = UIFactory.StatusHintColor;
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

        /// Brings the object list up to date. Updates the existing rows in place when they are
        /// still the same objects in the same order; rebuilds them only when that changed.
        ///
        /// It used to rebuild every row on every refresh, and a refresh follows every selection
        /// change - including the one a click on a row makes. So the second click of a
        /// double-click landed on a freshly built button, a different GameObject from the first,
        /// and uGUI counts clicks per GameObject: clickCount never reached 2 and double-click to
        /// rename never fired. Updating in place keeps the clicked button alive between the two.
        private void RefreshList()
        {
            if (_selection != null && RowsMatchObjects())
            {
                foreach (ListRow row in _listRows) UpdateRow(row);
                RefreshSelectedObjectControls();
                return;
            }

            foreach (ListRow row in _listRows) if (row.Go != null) Destroy(row.Go);
            _listRows.Clear();
            if (_selection == null) return;

            foreach (SculptableMesh obj in _selection.AllObjects)
            {
                if (obj == null) continue;
                GameObject rowGO = UIFactory.CreateRow(_listContent, 24f);
                // The name gets the room; the visibility tick and delete button take only what they need.
                rowGO.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;
                var row = new ListRow { Obj = obj, Go = rowGO, Renaming = obj == _renamingObject };
                _listRows.Add(row);

                if (row.Renaming)
                {
                    InputField renameField = UIFactory.CreateInputField(rowGO.transform, obj.name,
                        newName => CommitInlineRename(obj, newName));
                    FocusRenameField(renameField);
                    renameField.GetComponent<LayoutElement>().flexibleWidth = 1;
                }
                else
                {
                    row.NameButton = UIFactory.CreateButton(rowGO.transform, obj.name, () => OnRowClicked(obj));
                    row.NameText = row.NameButton.GetComponentInChildren<Text>();
                    AddDoubleClickHandler(row.NameButton.gameObject, () => BeginInlineRename(obj));
                    row.NameButton.GetComponent<LayoutElement>().flexibleWidth = 1;
                }

                row.Visible = UIFactory.CreateToggle(rowGO.transform, "Vis", obj.Visible, v => _selection.SetVisible(obj, v),
                    tooltip: "Shows or hides this object in the viewport.");
                row.Visible.GetComponent<LayoutElement>().preferredWidth = 44;
                Button delete = UIFactory.CreateButton(rowGO.transform, "X", () => ShowDeleteConfirm(obj), "Deletes this object (asks to confirm).");
                delete.GetComponent<LayoutElement>().preferredWidth = 26;
                UpdateRow(row);

                // Its live mirror copies, indented beneath it. Selecting one puts the gizmo on
                // that side; visibility and deletion belong to the object itself.
                MirrorRepeater repeater = obj.Repeater;
                for (int v = 0; repeater != null && v < repeater.ViewCount; v++)
                {
                    GameObject viewGO = UIFactory.CreateRow(_listContent, 22f);
                    var layout = viewGO.GetComponent<HorizontalLayoutGroup>();
                    layout.childForceExpandWidth = false;
                    layout.padding.left = 18;
                    int view = v; // captured per iteration
                    var viewRow = new ListRow { Obj = obj, View = view, Go = viewGO };
                    viewRow.NameButton = UIFactory.CreateButton(viewGO.transform, string.Empty, () => _selection.SelectView(obj, view));
                    viewRow.NameText = viewRow.NameButton.GetComponentInChildren<Text>();
                    viewRow.NameButton.GetComponent<LayoutElement>().flexibleWidth = 1;
                    _listRows.Add(viewRow);
                    UpdateRow(viewRow);
                }
            }

            RefreshSelectedObjectControls();
        }

        /// Whether _listRows is still one row per live object (plus one per live mirror copy under
        /// it), in AllObjects order, each in the right state (inline rename or name button).
        private bool RowsMatchObjects()
        {
            int r = 0;
            foreach (SculptableMesh obj in _selection.AllObjects)
            {
                if (obj == null) continue;
                if (r >= _listRows.Count) return false;
                ListRow row = _listRows[r++];
                if (row.Go == null || row.Obj != obj || row.View != -1 || row.Renaming != (obj == _renamingObject)) return false;

                int views = obj.Repeater != null ? obj.Repeater.ViewCount : 0;
                for (int v = 0; v < views; v++)
                {
                    if (r >= _listRows.Count) return false;
                    ListRow viewRow = _listRows[r++];
                    if (viewRow.Go == null || viewRow.Obj != obj || viewRow.View != v) return false;
                }
            }
            return r == _listRows.Count;
        }

        /// Everything a row shows about its object: name, selection highlight, live mirror and
        /// visibility.
        private void UpdateRow(ListRow row)
        {
            SculptableMesh obj = row.Obj;
            if (row.NameButton != null)
            {
                MirrorRepeater repeater = obj.Repeater;
                bool primary = _selection.PrimarySelection == obj;
                bool thisSide = primary && _selection.PrimaryView == row.View;

                string tooltip;
                if (row.View >= 0)
                {
                    MirrorRepeaterView view = repeater != null ? repeater.View(row.View) : null;
                    row.NameText.text = "Mirror " + (view != null ? view.AxisName : string.Empty);
                    tooltip = $"The live mirror copy of \"{obj.name}\". Click to select it - the gizmo then moves it, " +
                              "and the original follows on the other side.";
                }
                else
                {
                    row.NameButton.gameObject.name = "Button_" + obj.name;
                    row.NameText.text = obj.name;
                    tooltip = "Click to select, Ctrl+click to add to selection, double-click to rename.";
                    if (repeater != null)
                        tooltip = $"Mirrored live across {MirrorRepeater.AxisNameOf(repeater.AxisMask)} - its copies are listed below it. " + tooltip;
                }

                // A mirrored object and its copies share the amber text; the highlight marks the
                // side the gizmo is on, and the other side of the same object reads as selected.
                row.NameText.color = repeater != null ? MirroredTextColor : Color.white;
                row.NameButton.GetComponent<Image>().color = thisSide ? UIFactory.ActiveColor
                    : primary || _selection.IsSelected(obj) ? new Color(0.4f, 0.4f, 0.45f) : UIFactory.InactiveColor;
                TooltipSystem.Attach(row.NameButton.gameObject, tooltip);
            }
            if (row.Visible != null) row.Visible.SetIsOnWithoutNotify(obj.Visible);
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
            if (_resetShapeButton != null) _resetShapeButton.interactable = primary != null;
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
            _ssphereModeImg.color = mode == GizmoMode.SSphere ? UIFactory.ActiveColor : UIFactory.InactiveColor;

            GizmoOrientation orientation = _gizmo != null ? _gizmo.Orientation : GizmoOrientation.Auto;
            for (int i = 0; i < _orientationImgs.Length; i++)
                if (_orientationImgs[i] != null)
                    _orientationImgs[i].color = (int)orientation == i ? UIFactory.ActiveColor : UIFactory.InactiveColor;
        }

        private void SetGizmoOrientation(GizmoOrientation orientation)
        {
            if (_gizmo != null) _gizmo.Orientation = orientation;
            RefreshToolButtons();
        }

        // ------------------------------------------------------------------------------ mirror

        private int CheckedMirrorMask => (_mirrorX ? 1 : 0) | (_mirrorY ? 2 : 0) | (_mirrorZ ? 4 : 0);

        /// A tick on an already-mirrored object changes its mirror straight away - the toggles
        /// then describe the live copies, like Nomad's repeater panel. On an unmirrored object
        /// they only pick the axes for the next Mirror press.
        private void SetMirrorAxis(int axis, bool on)
        {
            switch (axis)
            {
                case 0: _mirrorX = on; break;
                case 1: _mirrorY = on; break;
                default: _mirrorZ = on; break;
            }

            SculptableMesh primary = _selection != null ? _selection.PrimarySelection : null;
            if (primary == null || primary.Repeater == null) return;
            // Unticking the last axis would silently delete the mirror; Remove is for that.
            if (CheckedMirrorMask == 0) { RefreshMirrorControls(primary); return; }
            MirrorRepeater.Set(primary, CheckedMirrorMask, primary.Repeater.Center, recordUndo: true);
        }

        /// Nothing to refresh by hand in any of these: a mirror change bumps SelectionVersion,
        /// which redraws the list and this section on the next Update.
        private void ApplyMirror()
        {
            SculptableMesh primary = _selection != null ? _selection.PrimarySelection : null;
            if (primary == null) return;
            if (CheckedMirrorMask == 0)
            {
                SetStatus("Tick at least one axis to mirror across.", UIFactory.StatusErrorColor, hold: false);
                return;
            }
            Vector3 center = primary.Repeater != null ? primary.Repeater.Center : Vector3.zero;
            MirrorRepeater.Set(primary, CheckedMirrorMask, center, recordUndo: true);
            SetStatus($"Mirrored \"{primary.name}\" across {MirrorRepeater.AxisNameOf(CheckedMirrorMask)}.", UIFactory.StatusOkColor, hold: true);
        }

        private void ValidateMirror()
        {
            SculptableMesh primary = _selection != null ? _selection.PrimarySelection : null;
            MirrorRepeater repeater = primary != null ? primary.Repeater : null;
            if (repeater == null) return;
            int count = repeater.Bake().Count;
            SetStatus($"Validated: {count} mirror cop{(count == 1 ? "y is" : "ies are")} now separate objects.",
                UIFactory.StatusOkColor, hold: true);
        }

        private void RemoveMirror()
        {
            SculptableMesh primary = _selection != null ? _selection.PrimarySelection : null;
            if (primary == null || primary.Repeater == null) return;
            MirrorRepeater.Set(primary, 0, Vector3.zero, recordUndo: true);
        }

        private void RefreshMirrorControls(SculptableMesh primary)
        {
            MirrorRepeater repeater = primary != null ? primary.Repeater : null;

            // The toggles show the live mirror's own axes whenever there is one.
            if (repeater != null && _mirrorXToggle != null)
            {
                int mask = repeater.AxisMask;
                _mirrorX = (mask & 1) != 0;
                _mirrorY = (mask & 2) != 0;
                _mirrorZ = (mask & 4) != 0;
                _mirrorXToggle.SetIsOnWithoutNotify(_mirrorX);
                _mirrorYToggle.SetIsOnWithoutNotify(_mirrorY);
                _mirrorZToggle.SetIsOnWithoutNotify(_mirrorZ);
            }

            if (_mirrorButton != null) _mirrorButton.interactable = primary != null && repeater == null;
            if (_validateMirrorButton != null) _validateMirrorButton.interactable = repeater != null;
            if (_removeMirrorButton != null) _removeMirrorButton.interactable = repeater != null;
            if (_mirrorNote == null) return;

            if (repeater == null)
            {
                _mirrorNote.text = "A live copy: sculpt or move either side\nand the other follows.";
                _mirrorNote.color = UIFactory.StatusHintColor;
                return;
            }

            int copies = repeater.ViewCount;
            _mirrorNote.text = $"\"{primary.name}\" is mirrored across {MirrorRepeater.AxisNameOf(repeater.AxisMask)}\n" +
                               $"({copies} live cop{(copies == 1 ? "y" : "ies")}). Validate to make them real.";
            _mirrorNote.color = UIFactory.StatusOkColor;
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
            SetStatus(message, ok ? UIFactory.StatusOkColor : UIFactory.StatusErrorColor, hold: ok);
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
