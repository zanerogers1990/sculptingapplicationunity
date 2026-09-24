using System;
using System.Collections.Generic;
using Sculpting.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Sculpting.Molding
{
    /// The mold tools' interface, in two pieces: a one-button launcher that lives in the scene
    /// panel, and a full-screen workspace that replaces the normal panels while a session is open.
    ///
    /// Molding is a mode, not a tool: brushes and region tools already stand down the moment
    /// GizmoMode leaves Sculpt, and MoldController already brackets the whole thing with
    /// BeginSession/EndSession. So the workspace takes the screen for as long as that session
    /// lasts and gives it straight back afterwards - the sculpting panels are only hidden, never
    /// rebuilt, so closing a session returns to exactly the panel state it interrupted.
    ///
    /// Every control that changes the mold goes through MoldController.Edit (buttons, toggles,
    /// the scale field) or SliderEdit (sliders), which is what makes each one exactly one undo
    /// step - a slider drag included. Undo can then change any setting behind the panel's back,
    /// so every control also registers a way to re-read itself, and the whole panel re-syncs
    /// whenever the controller's StateVersion moves.
    public class MoldUIBuilder : MonoBehaviour
    {
        private static readonly Color WarnColor = new Color(0.95f, 0.65f, 0.4f);
        private static readonly Color InfoColor = new Color(0.65f, 0.65f, 0.7f);
        private static readonly Color GoodColor = new Color(0.55f, 0.85f, 0.6f);
        private static readonly Color HeadingColor = new Color(0.45f, 0.7f, 1f);
        private static readonly Color ValueColor = new Color(0.85f, 0.85f, 0.9f);

        /// The panels the workspace stands in front of. Hidden by disabling their Canvas rather
        /// than deactivating the GameObject: these canvases are built once at startup and carry
        /// live state (scroll position, which foldouts are open, the scene list's selection), and
        /// deactivating them would run every builder's OnDisable on the way past.
        private static readonly string[] SculptingCanvases = { "SculptCanvas", "SceneGraphCanvas" };

        private const string WorkspaceCanvas = "MoldWorkspaceCanvas";

        private MoldController _mold;

        /// Self-installing, like TransformGizmo's light manager: the mold tools are new, and a
        /// scene saved before they existed has no MoldController on it. Putting one on this
        /// same GameObject means no scene edit is needed for the feature to work, and a scene
        /// that DOES carry one is found first and reused.
        private MoldController Mold
        {
            get
            {
                if (_mold == null) _mold = FindFirstObjectByType<MoldController>();
                if (_mold == null) _mold = gameObject.AddComponent<MoldController>();
                return _mold;
            }
        }

        // The compact launcher, in the scene panel.
        private Text _launcherStatus;
        private Button _launcherButton;

        // The workspace.
        private GameObject _workspace;
        private Text _status, _activity, _analysis, _selectedLabel, _selectedDetail, _blockReadout, _qualityReadout, _placeHint;
        private Button _refitButton, _draftButton, _finalButton, _clearHalvesButton, _undoButton, _redoButton;
        private Image _autoImg, _viewImg, _axisXImg, _axisYImg, _axisZImg;
        private Image _pinImg, _sprueImg, _ventImg;
        private Image _mirrorRImg, _mirrorEImg;
        private Image[] _viewModeImages;
        private Text _scaleReadout, _exportReadout;
        private Button _exportButton;
        private InputField _lengthField;
        private float _nextRefresh;

        /// Re-reads every control from the controller - run after anything that may have
        /// changed a setting without going through the control itself (undo, redo, a session
        /// starting). SetValueWithoutNotify throughout, so a re-sync never records a step.
        private readonly List<Action> _syncers = new List<Action>();
        private int _syncedVersion = -1;

        // The component list and the per-feature controls.
        private Transform _componentList;
        private string _componentSignature;
        private readonly List<(Button button, MoldFeature feature)> _componentButtons = new List<(Button, MoldFeature)>();
        private LabeledSlider _selRadius, _selHeight, _selVent, _selHub, _selFunnel, _selRunner, _selGap, _selAngle;
        private Button _deleteSelectedButton;

        // ------------------------------------------------------------------------- launcher

        /// The entry in the scene panel: a single button and a line of state. Deliberately small -
        /// everything else lives in the workspace.
        public void BuildContent(Transform panel)
        {
            _launcherStatus = UIFactory.CreateLabel(panel, "No mold session.", 11, FontStyle.Italic);
            _launcherStatus.color = InfoColor;

            _launcherButton = UIFactory.CreateButton(panel, "Open Mold Maker", ToggleSession,
                "Takes the selected object into the mold workspace: the sculpting panels step aside and the " +
                "mold tools take the screen until you close the session. Any halves you build stay in the scene.");

            RefreshLauncher();
        }

        // ------------------------------------------------------------------------ workspace

        private void BuildWorkspace()
        {
            MoldController mold = Mold;
            MoldSettings s = mold.Settings;

            // Same guard every other canvas in the project carries: a scene reload re-runs the
            // builders, and an un-destroyed previous canvas would leave two stacked workspaces
            // with the dead one on top swallowing the clicks. See UIFactory.DestroyStaleCanvas.
            GameObject stale = GameObject.Find(WorkspaceCanvas);
            if (stale != null) DestroyImmediate(stale);

            _syncers.Clear();
            _componentButtons.Clear();
            _componentSignature = null;

            var canvasGO = new GameObject(WorkspaceCanvas, typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above the sculpting panels so that if anything does fail to hide, the workspace is
            // still the thing being clicked rather than a panel behind it.
            canvas.sortingOrder = 10;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasGO.AddComponent<GraphicRaycaster>();
            _workspace = canvasGO;

            Transform left = AddColumn(canvasGO.transform, "Tools", new Vector2(0f, 1f), 310f);
            Transform right = AddColumn(canvasGO.transform, "View", new Vector2(1f, 1f), 290f);

            BuildToolsColumn(left, mold, s);
            BuildViewColumn(right, mold, s);

            SetSculptingPanelsVisible(false);
            _syncedVersion = -1;
            Refresh();
        }

        /// One full-height scrolling column docked to a screen edge. Built by hand rather than
        /// through CreateScrollingPanelCanvas because both columns share one canvas - two canvases
        /// would be two draw batches and two raycasters for what is one panel set.
        private static Transform AddColumn(Transform canvas, string name, Vector2 anchor, float width)
        {
            var panelGO = new GameObject(name, typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvas, false);
            panelGO.GetComponent<Image>().color = UIFactory.PanelColor;

            var rect = panelGO.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(width, 0f);

            return UIFactory.AddScrollingContent(rect, new RectOffset(12, 12, 12, 12), 8f);
        }

        private void BuildToolsColumn(Transform panel, MoldController mold, MoldSettings s)
        {
            Text heading = UIFactory.CreateLabel(panel, "MOLD MAKER", 15, FontStyle.Bold);
            heading.color = HeadingColor;

            UIFactory.CreateButton(panel, "Close Mold Session", ToggleSession,
                "Leaves the mold workspace and brings the sculpting panels back. Halves you have built stay " +
                "in the scene as ordinary objects.");

            // The sculpting panel's undo buttons are hidden while the workspace is up, so the
            // workspace carries its own. Z and Shift+Z work too.
            GameObject undoRow = UIFactory.CreateRow(panel, 24f);
            _undoButton = UIFactory.CreateButton(undoRow.transform, "Undo", Undo,
                "Steps back through everything you do here - placements, drags, rotations, every slider and toggle. Z does the same.");
            _redoButton = UIFactory.CreateButton(undoRow.transform, "Redo", Redo,
                "Steps forward again. Shift+Z does the same.");

            _status = UIFactory.CreateLabel(panel, "\n", 11, FontStyle.Italic);
            _status.color = InfoColor;
            _activity = UIFactory.CreateLabel(panel, " ", 11, FontStyle.Italic);
            _activity.color = GoodColor;

            // --------------------------------------------------------------------------- scale
            // First, because it governs everything below it: every physical size in this panel
            // is a millimetre value divided by the scale set here.
            UIFactory.CreateLabel(panel, "Scale", 13);
            _lengthField = UIFactory.CreateInputField(panel, ModelLengthMm(mold).ToString("0.#"), SetModelLength);
            UIFactory.CreateLabel(panel, "Model length along its longest axis, in mm.", 10, FontStyle.Italic)
                     .color = InfoColor;
            _scaleReadout = UIFactory.CreateLabel(panel, "\n", 11, FontStyle.Italic);
            _scaleReadout.color = InfoColor;

            // ------------------------------------------------------------------ pull direction
            UIFactory.CreateLabel(panel, "Pull Direction", 13);
            GameObject pullRow = UIFactory.CreateRow(panel, 24f);
            _autoImg = UIFactory.CreateButton(pullRow.transform, "Auto", () => Mold.SetPullMode(PullMode.Auto),
                "Tries pulling along X, Y and Z and keeps whichever traps the least surface.").GetComponent<Image>();
            _viewImg = UIFactory.CreateButton(pullRow.transform, "From View", () => Mold.SetPullMode(PullMode.FromView),
                "Pulls along the way the camera is facing up, snapped to the nearest world axis. Taken once, when you " +
                "press it - orbiting afterwards does not move the mold. Re-fit reads the camera again.").GetComponent<Image>();

            GameObject axisRow = UIFactory.CreateRow(panel, 24f);
            _axisXImg = UIFactory.CreateButton(axisRow.transform, "X", () => Mold.SetPullMode(PullMode.FixedAxis, 0), "Always pull along world X.").GetComponent<Image>();
            _axisYImg = UIFactory.CreateButton(axisRow.transform, "Y", () => Mold.SetPullMode(PullMode.FixedAxis, 1), "Always pull along world Y.").GetComponent<Image>();
            _axisZImg = UIFactory.CreateButton(axisRow.transform, "Z", () => Mold.SetPullMode(PullMode.FixedAxis, 2), "Always pull along world Z.").GetComponent<Image>();

            Toggle(panel, "Snap pull to axis", () => s.SnapPullToAxis,
                v => Mold.Edit("Snap Pull To Axis", MoldChange.Fit, () => s.SnapPullToAxis = v),
                "Rounds the pull direction to the nearest world axis, so a roughly-front view still gives an upright mold.");

            _refitButton = UIFactory.CreateButton(panel, "Re-fit Parting Surface", () => Mold.Refit(),
                "Re-runs the fit from scratch, including the axis search when Auto is on. Also throws away any " +
                "nudge you have dragged the surface by.");

            // -------------------------------------------------------------------- the surface
            Transform surface = UIFactory.CreateFoldoutSection(panel, "Parting Surface", false);
            Toggle(surface, "Fitted (curved) surface", () => s.FittedSurface,
                v => Mold.Edit("Fitted Surface", MoldChange.Fit, () => s.FittedSurface = v),
                "On: the surface follows the model's silhouette so less gets trapped. Off: a plain flat split, " +
                "which is what a convex shape wants and what you drag around to try a plane by hand.");

            Slider(surface, "Rounding", 0f, 30f, "0", () => s.Rounding,
                v => Mold.SliderEdit("Rounding", MoldChange.Fit, () => s.Rounding = Mathf.RoundToInt(v)),
                "Smoothing passes over the fitted surface. Higher is rounder and easier to print, but drifts off the ideal split.",
                wholeNumbers: true);
            Slider(surface, "Blend radius", 0f, 6f, "0", () => s.BlendRadius,
                v => Mold.SliderEdit("Blend Radius", MoldChange.Fit, () => s.BlendRadius = Mathf.RoundToInt(v)),
                "How many neighbouring cells each column pools when choosing its split height. 0 is noisy, 2+ is smoother but blunter.",
                wholeNumbers: true);
            Slider(surface, "Grid cell (mm)", 0.25f, 4f, "0.00", () => s.GridCellMm,
                v => Mold.SliderEdit("Parting Grid Cell", MoldChange.Fit, () => s.GridCellMm = v),
                "Parting grid cell size in MILLIMETRES - the one density control. Smaller is a finer parting line and " +
                "a more exact centre; larger is faster. Cells are square, so this means the same thing on every model.");
            Toggle(surface, "Split at true centre (equal volume)", () => s.CentreOnVolume,
                v => Mold.Edit("Split At True Centre", MoldChange.Fit, () => s.CentreOnVolume = v),
                "Flat split only. On: the plane with the same amount of material on each side. Off: the " +
                "midpoint of the bounding box, which a tail or a spike pulls off-centre.");
            Slider(surface, "Min samples / cell", 1f, 40f, "0", () => s.MinSamplesPerCell,
                v => Mold.SliderEdit("Min Samples Per Cell", MoldChange.Fit, () => s.MinSamplesPerCell = Mathf.RoundToInt(v)),
                "Cells with fewer surface samples than this are filled in from their neighbours instead of guessed at.",
                wholeNumbers: true);

            // ---------------------------------------------------------------------- the block
            Transform block = UIFactory.CreateFoldoutSection(panel, "Mold Block", true);

            // Everything here is MILLIMETRES, so the range is a physical one rather than a
            // fraction of the model.
            float maxMm = Mathf.Max(0.5f * ModelLengthMm(mold), 30f);
            Slider(block, "Padding (mm)", 0f, maxMm, "0.0", () => s.PaddingMm,
                v => Mold.SliderEdit("Mold Padding", MoldChange.Fit, () => s.PaddingMm = v),
                "Mold material around the model in the parting plane, in mm. The wire box in the viewport updates " +
                "as you drag. The addon's default is 15 mm - the sprue's funnel sits in this space.");
            Slider(block, "Wall (mm)", 0f, maxMm, "0.0", () => s.WallMm,
                v => Mold.SliderEdit("Mold Wall", MoldChange.Fit, () => s.WallMm = v),
                "Mold thickness behind the model on each half, in mm. This is the material the cavity is cut into - " +
                "too little and the half is a shell that flexes instead of holding detail. The addon's default is 15 mm.");

            _blockReadout = UIFactory.CreateLabel(block, "\n", 11, FontStyle.Italic);
            _blockReadout.color = InfoColor;

            // ------------------------------------------------------------------------ features
            UIFactory.CreateLabel(panel, "Place on the Surface", 13);
            GameObject placeRow = UIFactory.CreateRow(panel, 24f);
            _pinImg = UIFactory.CreateButton(placeRow.transform, "Pin", () => Arm(MoldPlacement.Pin),
                "Click the parting surface to drop a registration pin. Stays armed so you can place several; Esc stops.").GetComponent<Image>();
            _sprueImg = UIFactory.CreateButton(placeRow.transform, "Sprue", () => Arm(MoldPlacement.Sprue),
                "Click the model's edge where the pour should go IN. The funnel is placed out in the padding, " +
                "joined back to that spot by a thin runner. Placing a second one replaces the first.").GetComponent<Image>();
            _ventImg = UIFactory.CreateButton(placeRow.transform, "Vent", () => Arm(MoldPlacement.Vent),
                "Click the parting surface to add an air vent. Vents go where air would otherwise be trapped - " +
                "the far end of the cavity from the sprue, and any high point of it.").GetComponent<Image>();

            _placeHint = UIFactory.CreateLabel(panel, " ", 11, FontStyle.Italic);
            _placeHint.color = InfoColor;

            GameObject featureRow = UIFactory.CreateRow(panel, 24f);
            UIFactory.CreateButton(featureRow.transform, "Suggest", () => Mold.SuggestAll(),
                "Puts four corner pins, a sprue and a vent where they usually want to go. They are ordinary features " +
                "afterwards - move, rotate, resize or delete any of them.");
            UIFactory.CreateButton(featureRow.transform, "Clear", () => Mold.ClearFeatures(),
                "Removes every placed pin, sprue and vent.");

            // ------------------------------------------------------------------------ mirroring
            UIFactory.CreateLabel(panel, "Mirror Vents and Pins", 13);
            GameObject mirrorRow = UIFactory.CreateRow(panel, 24f);
            _mirrorRImg = UIFactory.CreateButton(mirrorRow.transform, "Across Length",
                () => Mold.Edit("Mirror Across Length", MoldChange.Features,
                                () => Mold.Settings.MirrorAcrossRight = !Mold.Settings.MirrorAcrossRight),
                "Every vent and pin you place gets a mirrored copy on the other side of the model's midpoint, along " +
                "the parting plane's long axis. The copy follows the original when you move, rotate or resize it.")
                .GetComponent<Image>();
            _mirrorEImg = UIFactory.CreateButton(mirrorRow.transform, "Across Width",
                () => Mold.Edit("Mirror Across Width", MoldChange.Features,
                                () => Mold.Settings.MirrorAcrossEye = !Mold.Settings.MirrorAcrossEye),
                "The same across the parting plane's short axis. Both on gives four of everything.")
                .GetComponent<Image>();
            UIFactory.CreateLabel(panel, "The sprue is never mirrored - one pour hole.", 10, FontStyle.Italic)
                     .color = InfoColor;

            // -------------------------------------------------------------- channel dimensions
            Transform dims = UIFactory.CreateFoldoutSection(panel, "Sizes (mm) - all features", false);
            UIFactory.CreateLabel(dims, "These set every feature of a kind at once. Select one feature to size it on its own.",
                                  10, FontStyle.Italic).color = InfoColor;
            Slider(dims, "Sprue diameter", 1f, 40f, "0.0", () => s.SprueDiameterMm,
                v => Mold.SliderEdit("Sprue Diameter", MoldChange.Features, () => Mold.SetSizeMm(MoldSize.SprueHub, v)),
                "Width of the pour hole where the runner joins it, in mm. The addon pours through 14 mm.");
            Slider(dims, "Sprue funnel diameter", 1f, 60f, "0.0", () => s.SprueFunnelDiameterMm,
                v => Mold.SliderEdit("Sprue Funnel Diameter", MoldChange.Features, () => Mold.SetSizeMm(MoldSize.SprueFunnel, v)),
                "Sprue diameter at the outside of the block, in mm. Larger than the sprue diameter makes the pour hole " +
                "a funnel. The addon's is 18 mm.");
            Slider(dims, "Runner diameter", 0.3f, 12f, "0.0", () => s.RunnerDiameterMm,
                v => Mold.SliderEdit("Runner Diameter", MoldChange.Features, () => Mold.SetSizeMm(MoldSize.Runner, v)),
                "The neck that carries the pour from the sprue into the cavity, in mm. Thin on purpose - it is where " +
                "the casting snaps off the sprue. The addon's is 3 mm.");
            Slider(dims, "Vent diameter", 0.1f, 10f, "0.0", () => s.VentDiameterMm,
                v => Mold.SliderEdit("Vent Diameter", MoldChange.Features, () => Mold.SetSizeMm(MoldSize.Vent, v)),
                "Vent diameter in mm. The addon's is 1 mm - a vent only has to let air out, and a wide one fills " +
                "with the pour instead.");
            Slider(dims, "Channel overlap", 0f, 20f, "0.0", () => s.ChannelOverlapMm,
                v => Mold.SliderEdit("Channel Overlap", MoldChange.Features, () => s.ChannelOverlapMm = v),
                "How far a vent or runner reaches inside the model, in mm, so it actually breaks into the cavity " +
                "instead of stopping a hair short and leaving a sealed mold. The addon's is 4 mm.");
            Slider(dims, "Pin radius", 0.5f, 15f, "0.0", () => s.PinRadiusMm,
                v => Mold.SliderEdit("Pin Radius", MoldChange.Features, () => Mold.SetSizeMm(MoldSize.PinRadius, v)),
                "Registration pin radius in mm. The addon's is 3.5 mm.");
            Slider(dims, "Pin height", 1f, 30f, "0.0", () => s.PinHeightMm,
                v => Mold.SliderEdit("Pin Height", MoldChange.Features, () => Mold.SetSizeMm(MoldSize.PinHeight, v)),
                "Pin height in mm. The addon's is 7 mm.");
            Slider(dims, "Pin clearance", 0f, 1.5f, "0.00", () => s.PinClearanceMm,
                v => Mold.SliderEdit("Pin Clearance", MoldChange.Features, () => s.PinClearanceMm = v),
                "Extra room cut into every socket, in mm, so the two halves actually close instead of jamming on the pins.");

            Toggle(panel, "Build registration pins", () => s.Pins,
                v => Mold.Edit("Build Pins", MoldChange.Features, () => s.Pins = v),
                "Off leaves the pegs and sockets out of the build. The placed pins stay where they are.");
            Toggle(panel, "Build sprue", () => s.Sprue,
                v => Mold.Edit("Build Sprue", MoldChange.Features, () => s.Sprue = v),
                "Off leaves the pour hole out of the build. Vents are unaffected.");

            // ------------------------------------------------------------------------- the build
            UIFactory.CreateLabel(panel, "Build", 13);
            GameObject buildRow = UIFactory.CreateRow(panel, 26f);
            _draftButton = UIFactory.CreateButton(buildRow.transform, "Draft", () => Mold.BuildHalves(true),
                "Builds both halves quickly at the draft resolution, in the background - keep working while it runs.");
            _finalButton = UIFactory.CreateButton(buildRow.transform, "Final", () => Mold.BuildHalves(false),
                "Rebuilds the same two halves at full resolution, in the background. Replaces the draft in place, so " +
                "nothing is lost. Build this before you export.");

            Toggle(panel, "Live update halves", () => s.LiveHalves,
                v => Mold.Edit("Live Update Halves", MoldChange.None, () => s.LiveHalves = v),
                "Rebuilds both halves at draft resolution, in the background, shortly after anything changes them, " +
                "so what you are looking at is the mold as it stands now. Only ever runs once you have built once.");

            Transform quality = UIFactory.CreateFoldoutSection(panel, "Build Quality", true);
            Slider(quality, "Draft resolution", 32f, 512f, "0", () => s.DraftResolution,
                v => Mold.SliderEdit("Draft Resolution", MoldChange.None, () => s.DraftResolution = Mathf.RoundToInt(v)),
                "Voxels along the longest axis for a draft build. Lower is faster and blunter.",
                wholeNumbers: true);
            Slider(quality, "Final resolution", 64f, 2048f, "0", () => s.FinalResolution,
                v => Mold.SliderEdit("Final Resolution", MoldChange.None, () =>
                {
                    s.FinalResolution = Mathf.RoundToInt(v);
                    s.AutoResolution = false;
                }),
                "Voxels along the longest axis for the final build. This counts cells along the BLOCK's longest " +
                "axis, so what it means for the cavity depends on the model's proportions - read the lines below, " +
                "not the number. Dragging this turns off Match Density.",
                wholeNumbers: true);

            Toggle(quality, "Match the model's density", () => s.AutoResolution,
                v => Mold.Edit("Match Model Density", MoldChange.None, () =>
                {
                    s.AutoResolution = v;
                    if (v) s.FinalResolution = Mold.MatchModelResolution();
                }),
                "Keeps the final resolution at the point where the voxel grid stops being coarser than the " +
                "model's own triangles, and re-derives it whenever the model is measured.");

            UIFactory.CreateButton(quality, "Match Now",
                () => Mold.Edit("Match Model Density", MoldChange.None, () => s.FinalResolution = Mold.MatchModelResolution()),
                "Sets the final resolution to the point where the voxel grid stops being coarser than the model's " +
                "own triangles. Below that, sculpted detail is discarded before it can reach the cavity; far above " +
                "it, the extra cells only resolve the model's faceting at four times the cost per doubling.");

            _qualityReadout = UIFactory.CreateLabel(quality, "\n\n\n", 11, FontStyle.Italic);
            _qualityReadout.color = InfoColor;

            // --------------------------------------------------------------------------- export
            UIFactory.CreateLabel(panel, "Export", 13);
            _exportButton = UIFactory.CreateButton(panel, "Export Both Halves (.stl)", ExportStl,
                "Writes both halves as binary .stl at the scale set at the top of this panel, ready for a slicer. " +
                "The Exploded view offset is not baked in - the two halves are written so they close.");
            _exportReadout = UIFactory.CreateLabel(panel, " ", 11, FontStyle.Italic);
            _exportReadout.color = InfoColor;

            _clearHalvesButton = UIFactory.CreateButton(panel, "Remove Halves", () => Mold.ClearHalves(),
                "Deletes the built halves. They are ordinary objects, so Z brings them back.");
        }

        private void BuildViewColumn(Transform panel, MoldController mold, MoldSettings s)
        {
            Text heading = UIFactory.CreateLabel(panel, "VIEW", 15, FontStyle.Bold);
            heading.color = HeadingColor;

            GameObject viewRow = UIFactory.CreateRow(panel, 24f);
            _viewModeImages = new Image[4];
            _viewModeImages[0] = UIFactory.CreateButton(viewRow.transform, "Model", () => SetView(MoldViewMode.Model),
                "Shows the model alone, with the parting surface and undercut tint over it. This is the view to " +
                "place pins, sprues and vents in - the others bury the parting surface inside solid halves.").GetComponent<Image>();
            _viewModeImages[1] = UIFactory.CreateButton(viewRow.transform, "Halves", () => SetView(MoldViewMode.Halves),
                "Hides the model so you can look straight into the cavity.").GetComponent<Image>();
            GameObject viewRow2 = UIFactory.CreateRow(panel, 24f);
            _viewModeImages[2] = UIFactory.CreateButton(viewRow2.transform, "Assembled", () => SetView(MoldViewMode.Assembled),
                "Shows the halves closed around the model.").GetComponent<Image>();
            _viewModeImages[3] = UIFactory.CreateButton(viewRow2.transform, "Exploded", () => SetView(MoldViewMode.Exploded),
                "Pulls the upper half off along the pull axis by the gap below.").GetComponent<Image>();

            Slider(panel, "Exploded gap", 0f, 3f, "0.00", () => s.ExplodeGap,
                v => Mold.SliderEdit("Exploded Gap", MoldChange.View, () => s.ExplodeGap = v),
                "How far apart the two halves sit in Exploded view. Display only - it does not move anything on export.");

            Transform overlays = UIFactory.CreateFoldoutSection(panel, "Overlays", true);
            Toggle(overlays, "Undercut tint", () => s.ShowUndercutTint,
                v => Mold.Edit("Undercut Tint", MoldChange.Overlays, () => s.ShowUndercutTint = v),
                "Colours the model by which half it lands in - blue below the split, amber above, red where " +
                "neither half can release from it, and yellow along the seam.");
            Toggle(overlays, "Parting surface", () => s.ShowPartingSurface,
                v => Mold.Edit("Show Parting Surface", MoldChange.Overlays, () => s.ShowPartingSurface = v),
                "Draws the parting sheet itself, through the model.");
            Toggle(overlays, "Block outline", () => s.ShowBlockOutline,
                v => Mold.Edit("Show Block Outline", MoldChange.Overlays, () => s.ShowBlockOutline = v),
                "Draws the wire box of the mold material, which the padding and wall sliders resize.");

            // ---------------------------------------------------------------------- components
            Transform components = UIFactory.CreateFoldoutSection(panel, "Components", true);
            UIFactory.CreateLabel(components, "Click one here or in the viewport to select it.", 10, FontStyle.Italic)
                     .color = InfoColor;
            var listGO = new GameObject("ComponentList", typeof(RectTransform));
            listGO.transform.SetParent(components, false);
            var vlg = listGO.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 3;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            listGO.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _componentList = listGO.transform;

            // ---------------------------------------------------------------- selected feature
            Transform selected = UIFactory.CreateFoldoutSection(panel, "Selected Feature", true);
            _selectedLabel = UIFactory.CreateLabel(selected, "Nothing selected.", 12, FontStyle.Bold);
            _selectedLabel.color = InfoColor;
            _selectedDetail = UIFactory.CreateLabel(selected, " ", 10, FontStyle.Italic);
            _selectedDetail.color = InfoColor;

            _selRadius = Slider(selected, "Pin radius (mm)", 0.5f, 15f, "0.0", () => SelectedMm(f => f.RadiusInner),
                v => Mold.SliderEdit("Resize Pin", MoldChange.Features, () => Mold.SetSelectedMm(MoldSize.PinRadius, v)),
                "Radius of this pin, in mm.");
            _selHeight = Slider(selected, "Pin height (mm)", 0.5f, 30f, "0.0", () => SelectedMm(f => f.Height),
                v => Mold.SliderEdit("Resize Pin", MoldChange.Features, () => Mold.SetSelectedMm(MoldSize.PinHeight, v)),
                "How far this pin stands above the parting surface, in mm. Dragging the gizmo's up arrow does the same.");
            _selVent = Slider(selected, "Vent diameter (mm)", 0.1f, 10f, "0.0", () => SelectedMm(f => 2f * f.RadiusInner),
                v => Mold.SliderEdit("Resize Vent", MoldChange.Features, () => Mold.SetSelectedMm(MoldSize.Vent, v)),
                "Diameter of this vent, in mm.");
            _selHub = Slider(selected, "Sprue diameter (mm)", 1f, 40f, "0.0", () => SelectedMm(f => 2f * f.RadiusInner),
                v => Mold.SliderEdit("Resize Sprue", MoldChange.Features, () => Mold.SetSelectedMm(MoldSize.SprueHub, v)),
                "Width of this pour hole where its runner joins it, in mm.");
            _selFunnel = Slider(selected, "Funnel diameter (mm)", 1f, 60f, "0.0", () => SelectedMm(f => 2f * f.RadiusOuter),
                v => Mold.SliderEdit("Resize Sprue", MoldChange.Features, () => Mold.SetSelectedMm(MoldSize.SprueFunnel, v)),
                "Width of this pour hole at the outside of the block, in mm.");
            _selRunner = Slider(selected, "Runner diameter (mm)", 0.3f, 12f, "0.0", () => SelectedMm(f => 2f * f.RunnerRadius),
                v => Mold.SliderEdit("Resize Runner", MoldChange.Features, () => Mold.SetSelectedMm(MoldSize.Runner, v)),
                "Width of the neck between this sprue and the cavity, in mm.");
            _selGap = Slider(selected, "Distance from model (mm)", 0f, 40f, "0.0", SelectedSprueGapMm,
                v => Mold.SliderEdit("Move Sprue", MoldChange.Features, () => Mold.SetSelectedSprueGapMm(v)),
                "How far the funnel stands off the model - the length of the runner. The gizmo's arrow along the " +
                "sprue does the same.");
            _selAngle = Slider(selected, "Direction (degrees)", 0f, 360f, "0", () => Mold.SelectedFeature != null ? Mold.SelectedFeature.AngleDeg : 0f,
                v => Mold.SliderEdit("Rotate Channel", MoldChange.Features, () => Mold.SetSelectedAngle(v)),
                "Which way this channel runs out to the block wall, in the parting plane. The gizmo's ring does the same.");

            _deleteSelectedButton = UIFactory.CreateButton(selected, "Delete Selected", () => Mold.DeleteSelectedFeature(),
                "Removes the selected feature. The Delete key does the same while the mold tool is active.");

            UIFactory.CreateLabel(panel, "Analysis", 13);
            _analysis = UIFactory.CreateLabel(panel, "\n\n", 11, FontStyle.Italic);
            _analysis.color = InfoColor;
        }

        private void DestroyWorkspace()
        {
            SetSculptingPanelsVisible(true);

            if (_workspace != null) Destroy(_workspace);
            _workspace = null;
            _status = _activity = _analysis = _selectedLabel = _selectedDetail = _blockReadout = _qualityReadout = _placeHint = null;
            _scaleReadout = _exportReadout = null;
            _refitButton = _draftButton = _finalButton = _clearHalvesButton = _exportButton = _undoButton = _redoButton = null;
            _deleteSelectedButton = null;
            _viewModeImages = null;
            _mirrorRImg = _mirrorEImg = null;
            _lengthField = null;
            _componentList = null;
            _componentButtons.Clear();
            _componentSignature = null;
            _selRadius = _selHeight = _selVent = _selHub = _selFunnel = _selRunner = _selGap = _selAngle = null;
            _syncers.Clear();
        }

        private static void SetSculptingPanelsVisible(bool visible)
        {
            foreach (string name in SculptingCanvases)
            {
                GameObject go = GameObject.Find(name);
                if (go == null) continue;

                var canvas = go.GetComponent<Canvas>();
                if (canvas != null) canvas.enabled = visible;
                // The raycaster is disabled too: a disabled Canvas stops drawing, but leaving
                // the raycaster live would keep an invisible panel swallowing clicks meant for
                // the viewport behind it.
                var raycaster = go.GetComponent<GraphicRaycaster>();
                if (raycaster != null) raycaster.enabled = visible;
            }
        }

        // -------------------------------------------------------------------- control helpers

        /// A slider with a label that shows its current value, registered for re-sync.
        private sealed class LabeledSlider
        {
            public GameObject Root;
            public Text Label;
            public Slider Slider;
        }

        private LabeledSlider Slider(Transform parent, string name, float min, float max, string format,
                                     Func<float> get, Action<float> set, string tooltip, bool wholeNumbers = false)
        {
            var root = new GameObject("Slider_" + name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var vlg = root.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 1;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;

            Text label = UIFactory.CreateLabel(root.transform, name, 11);
            label.color = ValueColor;

            var entry = new LabeledSlider { Root = root, Label = label };
            float initial = SafeGet(get);
            entry.Slider = UIFactory.CreateSlider(root.transform, min, max, Mathf.Clamp(initial, min, max), v =>
            {
                set(v);
                entry.Label.text = name + ":  " + v.ToString(format);
            }, tooltip);
            entry.Slider.wholeNumbers = wholeNumbers;
            label.text = name + ":  " + initial.ToString(format);

            _syncers.Add(() =>
            {
                float v = SafeGet(get);
                entry.Slider.SetValueWithoutNotify(Mathf.Clamp(v, min, max));
                entry.Label.text = name + ":  " + v.ToString(format);
            });
            return entry;
        }

        private void Toggle(Transform parent, string label, Func<bool> get, Action<bool> set, string tooltip)
        {
            Toggle toggle = UIFactory.CreateToggle(parent, label, get(), set, tooltip: tooltip);
            _syncers.Add(() => toggle.SetIsOnWithoutNotify(get()));
        }

        private static float SafeGet(Func<float> get)
        {
            try { return get(); }
            catch (Exception) { return 0f; }
        }

        /// A size of the selected feature, in millimetres - or 0 with nothing selected.
        private float SelectedMm(Func<MoldFeature, float> units)
        {
            MoldFeature f = Mold.SelectedFeature;
            return f != null ? Mold.Settings.ToMillimetres(units(f)) : 0f;
        }

        private float SelectedSprueGapMm()
        {
            MoldFeature f = Mold.SelectedFeature;
            if (f == null || f.Kind != MoldFeatureKind.Sprue) return 0f;
            MoldGeometry.ChannelLayout layout = Mold.LayoutOf(f);
            return layout.Connected ? Mold.Settings.ToMillimetres(Vector2.Distance(layout.Gate, layout.Hub)) : 0f;
        }

        /// The model's longest axis in world units - what the millimetre scale is set against.
        private static float ModelLongestAxis(MoldController mold)
        {
            if (mold.Fit == null || !mold.Fit.IsValid) return 1f;
            Vector3 e = mold.Fit.Block.ModelExtent;
            return Mathf.Max(e.x, Mathf.Max(e.y, e.z));
        }

        private static float ModelLengthMm(MoldController mold) =>
            mold.Settings.ModelLengthMm(ModelLongestAxis(mold));

        /// "This lure is 90mm long." Rescales the whole mold around the model, since every
        /// physical size in the panel is read through the scale this sets.
        private void SetModelLength(string text)
        {
            if (!float.TryParse(text, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out float mm) || mm <= 0f)
            {
                if (_lengthField != null) _lengthField.text = ModelLengthMm(Mold).ToString("0.#");
                return;
            }

            if (Mathf.Abs(mm - ModelLengthMm(Mold)) < 1e-3f) return;
            Mold.SetModelLengthMm(mm, ModelLongestAxis(Mold));
            MarkReadoutStale();
        }

        private void ExportStl()
        {
            if (_exportReadout == null) return;

            string folder = FileDialog.IsSupported
                ? FileDialog.SaveFile("Export mold halves", null,
                                      SafeName(Mold.Model != null ? Mold.Model.name : "Mold"), "stl")
                : null;

            if (string.IsNullOrEmpty(folder))
            {
                _exportReadout.text = "Export cancelled.";
                _exportReadout.color = InfoColor;
                return;
            }

            // The dialog asks for a file, but two halves are written - so the directory it
            // landed in is what matters, and each half is named after the model.
            string report = Mold.ExportStl(FileDialog.DirectoryFor(folder));
            _exportReadout.text = report;
            _exportReadout.color = report.StartsWith("Exported") ? (Mold.HalvesAreFinal ? GoodColor : WarnColor) : WarnColor;
        }

        private static string SafeName(string s) => string.IsNullOrEmpty(s) ? "Mold" : s;

        // ------------------------------------------------------------------------ callbacks

        private void ToggleSession()
        {
            if (Mold.IsActive) Mold.EndSession();
            else Mold.BeginSession();
            RefreshLauncher();
        }

        private void Undo()
        {
            SculptController sculpt = FindFirstObjectByType<SculptController>();
            if (sculpt != null) sculpt.Undo(); else EditHistory.Undo();
            MarkReadoutStale();
        }

        private void Redo()
        {
            SculptController sculpt = FindFirstObjectByType<SculptController>();
            if (sculpt != null) sculpt.Redo(); else EditHistory.Redo();
            MarkReadoutStale();
        }

        private void Arm(MoldPlacement placement)
        {
            // Pressing the armed button again disarms it, so the same button is both "start
            // placing" and "stop placing" - there is no separate cancel to hunt for.
            Mold.Placement = Mold.Placement == placement ? MoldPlacement.None : placement;
            Refresh();
        }

        private void SetView(MoldViewMode mode)
        {
            if (Mold.Settings.ViewMode == mode) return;
            Mold.Edit("View " + mode, MoldChange.View, () => Mold.Settings.ViewMode = mode);
            Refresh();
        }

        /// Forces the next Update to rebuild the readouts.
        private void MarkReadoutStale() => _nextRefresh = 0f;

        // -------------------------------------------------------------------------- refresh

        private void Update()
        {
            // The workspace follows the session rather than a button: EndSession can also be
            // reached by deleting the model, switching tools or reloading the scene, and every
            // one of those has to give the sculpting panels back.
            bool active = Mold.IsActive;
            if (active && _workspace == null) BuildWorkspace();
            else if (!active && _workspace != null) DestroyWorkspace();

            // Something changed the settings behind the controls' backs - an undo, a redo, a
            // selection. Re-read every control now rather than at the next throttled refresh,
            // so a slider never shows a stale value for a moment after Z.
            if (_workspace != null && Mold.StateVersion != _syncedVersion)
            {
                _syncedVersion = Mold.StateVersion;
                SyncControls();
                _nextRefresh = 0f;
            }

            // Throttled: every label here is derived from state that only changes on a recompute,
            // and rebuilding a dozen strings per frame is pure waste.
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.15f;
            Refresh();
        }

        private void OnDisable()
        {
            // Never leave the sculpting panels hidden behind a workspace that is going away.
            if (_workspace != null) DestroyWorkspace();
        }

        private void SyncControls()
        {
            foreach (Action sync in _syncers)
            {
                try { sync(); }
                catch (Exception e) { Debug.LogException(e); }
            }
            if (_lengthField != null && !_lengthField.isFocused)
                _lengthField.text = ModelLengthMm(Mold).ToString("0.#");
        }

        private void RefreshLauncher()
        {
            if (_launcherStatus == null) return;
            MoldController mold = Mold;

            _launcherStatus.text = mold.IsActive
                ? "Mold session open on " + (mold.Model != null ? mold.Model.name : "?") + "."
                : "Select an object, then open the mold workspace.";
            _launcherStatus.color = mold.IsActive ? GoodColor : InfoColor;
            SetLabel(_launcherButton, mold.IsActive ? "Close Mold Maker" : "Open Mold Maker");
        }

        private void Refresh()
        {
            RefreshLauncher();
            if (_status == null) return;

            MoldController mold = Mold;
            MoldSettings s = mold.Settings;

            _status.text = mold.Status;
            _status.color = mold.Error != null ? WarnColor : InfoColor;
            string activity = mold.BuildActivity;
            _activity.text = activity ?? " ";

            if (_undoButton != null) _undoButton.interactable = EditHistory.CanUndo;
            if (_redoButton != null) _redoButton.interactable = EditHistory.CanRedo;

            _autoImg.color = Tint(s.PullMode == PullMode.Auto);
            _viewImg.color = Tint(s.PullMode == PullMode.FromView);
            bool fixedAxis = s.PullMode == PullMode.FixedAxis;
            _axisXImg.color = Tint(fixedAxis && s.Axis == 0);
            _axisYImg.color = Tint(fixedAxis && s.Axis == 1);
            _axisZImg.color = Tint(fixedAxis && s.Axis == 2);

            _pinImg.color = Tint(mold.Placement == MoldPlacement.Pin);
            _sprueImg.color = Tint(mold.Placement == MoldPlacement.Sprue);
            _ventImg.color = Tint(mold.Placement == MoldPlacement.Vent);

            if (_mirrorRImg != null) _mirrorRImg.color = Tint(s.MirrorAcrossRight);
            if (_mirrorEImg != null) _mirrorEImg.color = Tint(s.MirrorAcrossEye);
            if (_exportButton != null) _exportButton.interactable = mold.HasHalves;

            for (int i = 0; i < _viewModeImages.Length; i++)
                _viewModeImages[i].color = Tint((int)s.ViewMode == i);

            if (_refitButton != null) _refitButton.interactable = mold.IsActive;
            if (_draftButton != null) _draftButton.interactable = mold.IsActive;
            if (_finalButton != null) _finalButton.interactable = mold.IsActive;
            if (_clearHalvesButton != null) _clearHalvesButton.interactable = mold.HasHalves;

            RefreshPlaceHint(mold);
            RefreshScale(mold);
            RefreshBlock(mold);
            RefreshQuality(mold);
            RefreshComponents(mold);
            RefreshSelectedFeature(mold);
            RefreshAnalysis(mold);
            RefreshExport(mold);
        }

        /// Placement clicks land on the parting SURFACE, which the solid-half views bury inside
        /// geometry - so an armed button with nothing visible to aim at is worth saying out loud.
        private void RefreshPlaceHint(MoldController mold)
        {
            if (_placeHint == null) return;

            if (mold.Placement == MoldPlacement.None)
            {
                _placeHint.text = "Click a pin, sprue or vent to select it: arrows move it, the ring rotates it.";
                _placeHint.color = InfoColor;
                return;
            }

            bool surfaceVisible = mold.Settings.ViewMode == MoldViewMode.Model ||
                                  mold.Settings.ViewMode == MoldViewMode.Assembled;
            string what = mold.Placement.ToString().ToLowerInvariant();

            _placeHint.text = !surfaceVisible
                ? "Switch to Model view - the parting surface is inside the halves right now."
                : mold.Placement == MoldPlacement.Sprue
                    ? "Click the model's edge where the pour should go in. Esc stops."
                    : "Click the parting surface to drop a " + what + ". Esc stops.";
            _placeHint.color = surfaceVisible ? GoodColor : WarnColor;
        }

        private void RefreshScale(MoldController mold)
        {
            if (_scaleReadout == null) return;
            if (mold.Fit == null || !mold.Fit.IsValid) { _scaleReadout.text = "\n"; return; }

            MoldSettings s = mold.Settings;
            Vector3 e = mold.Fit.Block.ModelExtent;
            _scaleReadout.text =
                $"Model {s.ToMillimetres(e.x):0.#} x {s.ToMillimetres(e.y):0.#} x {s.ToMillimetres(e.z):0.#} mm\n" +
                $"{s.MillimetresPerUnit:0.##} mm per world unit";
            _scaleReadout.color = InfoColor;

            // Left alone while it has focus, or typing "9" of "90" would be overwritten on the
            // next refresh before the second digit arrives.
            if (_lengthField != null && !_lengthField.isFocused)
                _lengthField.text = ModelLengthMm(mold).ToString("0.#");
        }

        private void RefreshBlock(MoldController mold)
        {
            if (_blockReadout == null) return;
            if (mold.Fit == null || !mold.Fit.IsValid) { _blockReadout.text = "\n"; return; }

            MoldSettings s = mold.Settings;
            MoldBlock b = mold.Fit.Block;
            float halfHeight = 0.5f * b.Height;
            float cavityDepth = Mathf.Max(0.5f * b.ModelExtent.y, 1e-5f);
            float ratio = s.Wall / cavityDepth;

            _blockReadout.text =
                $"Each half {s.ToMillimetres(b.Width):0.#} x {s.ToMillimetres(b.Depth):0.#} x " +
                $"{s.ToMillimetres(halfHeight):0.#} mm\n" +
                $"Wall {s.WallMm:0.#} mm is {ratio:0.0}x the cavity depth";
            // Under about one-to-one the mold is a shell around the cavity rather than a block
            // with a cavity in it, and it will flex before it transfers anything crisply.
            _blockReadout.color = ratio < 1f ? WarnColor : ratio < 1.5f ? InfoColor : GoodColor;
        }

        private void RefreshQuality(MoldController mold)
        {
            if (_qualityReadout == null) return;
            if (mold.Fit == null || !mold.Fit.IsValid) { _qualityReadout.text = "\n\n\n"; return; }

            MoldBuildEstimate e = mold.EstimateAt(mold.Settings.FinalResolution);

            string verdict;
            Color color;
            if (e.StarvedBySource)
            {
                verdict = $"Coarser than the model's own triangles ({e.CellsPerSourceEdge:0.00} cells/edge)\n" +
                          "- detail is being lost before it can transfer. Try Match Model.";
                color = WarnColor;
            }
            else if (e.WastingCells)
            {
                verdict = $"{e.CellsPerSourceEdge:0.0} cells per triangle edge - finer than the model has\n" +
                          "detail for. A denser source mesh would help more than more cells.";
                color = InfoColor;
            }
            else
            {
                verdict = $"{e.CellsPerSourceEdge:0.00} cells per triangle edge - resolves the model.";
                color = GoodColor;
            }

            _qualityReadout.text =
                $"Final: cell {e.CellSize:0.0000}, {e.CellsAcrossThinnestAxis:0} across the thinnest axis\n" +
                $"~{e.TrianglesPerHalf:n0} tris per half, {e.PeakGigabytes:0.00} GB peak\n" +
                verdict;
            _qualityReadout.color = color;
        }

        /// One button per placed feature, rebuilt only when the list or the selection changes.
        private void RefreshComponents(MoldController mold)
        {
            if (_componentList == null) return;

            var sig = new System.Text.StringBuilder();
            foreach (MoldFeature f in mold.Features) sig.Append(f.Id).Append(':').Append((int)f.Kind).Append(',');
            sig.Append('|').Append(mold.SelectedFeature != null ? mold.SelectedFeature.Id : -1);
            string signature = sig.ToString();
            if (signature == _componentSignature) return;
            _componentSignature = signature;

            foreach (Transform child in _componentList) Destroy(child.gameObject);
            _componentButtons.Clear();

            if (mold.Features.Count == 0)
            {
                Text none = UIFactory.CreateLabel(_componentList, "Nothing placed yet.", 11, FontStyle.Italic);
                none.color = InfoColor;
                return;
            }

            foreach (MoldFeature f in mold.Features)
            {
                MoldFeature feature = f;
                string label = mold.FeatureLabel(feature) + (feature.Suggested ? "  (suggested)" : "");
                Button b = UIFactory.CreateButton(_componentList, label, () => Mold.SelectFeature(feature),
                    "Selects this feature and puts the gizmo on it.");
                b.GetComponent<Image>().color = Tint(feature == mold.SelectedFeature);
                _componentButtons.Add((b, feature));
            }
        }

        private void RefreshSelectedFeature(MoldController mold)
        {
            if (_selectedLabel == null) return;
            MoldFeature f = mold.SelectedFeature;

            bool pin = f != null && f.Kind == MoldFeatureKind.Pin;
            bool vent = f != null && f.Kind == MoldFeatureKind.Vent;
            bool sprue = f != null && f.Kind == MoldFeatureKind.Sprue;
            SetShown(_selRadius, pin);
            SetShown(_selHeight, pin);
            SetShown(_selVent, vent);
            SetShown(_selHub, sprue);
            SetShown(_selFunnel, sprue);
            SetShown(_selRunner, sprue);
            SetShown(_selGap, sprue);
            SetShown(_selAngle, vent || sprue);
            if (_deleteSelectedButton != null) _deleteSelectedButton.interactable = f != null;

            if (f == null)
            {
                _selectedLabel.text = mold.IsActive
                    ? "Nothing selected. Click a pin, sprue or vent."
                    : "No mold session.";
                _selectedLabel.color = InfoColor;
                _selectedDetail.text = "With nothing selected the gizmo drives the parting surface: the arrow slides it, the rings turn the pull direction.";
                _selectedDetail.color = InfoColor;
                return;
            }

            _selectedLabel.text = mold.FeatureLabel(f) + (f.Suggested ? " (suggested)" : "");
            _selectedLabel.color = GoodColor;

            if (pin)
            {
                _selectedDetail.text = "Arrows slide it across the parting surface; the up arrow sets its height.";
                _selectedDetail.color = InfoColor;
                return;
            }

            MoldGeometry.ChannelLayout layout = mold.LayoutOf(f);
            if (!layout.Connected)
            {
                _selectedDetail.text = "Does NOT reach the cavity - slide it (or turn it) so its line crosses the model.";
                _selectedDetail.color = WarnColor;
            }
            else if (sprue)
            {
                float gap = mold.Settings.ToMillimetres(Vector2.Distance(layout.Gate, layout.Hub));
                _selectedDetail.text = $"Funnel stands {gap:0.0} mm off the model on a {mold.Settings.ToMillimetres(2f * layout.ChannelRadius):0.0} mm runner. " +
                                       "The side arrow slides it, the long arrow moves the funnel in or out, the ring turns it.";
                _selectedDetail.color = GoodColor;
            }
            else
            {
                _selectedDetail.text = "Reaches the cavity. The arrow slides it along the model's edge; the ring turns it.";
                _selectedDetail.color = GoodColor;
            }
        }

        private static void SetShown(LabeledSlider slider, bool shown)
        {
            if (slider != null && slider.Root.activeSelf != shown) slider.Root.SetActive(shown);
        }

        private void RefreshAnalysis(MoldController mold)
        {
            if (_analysis == null) return;

            if (!mold.IsActive || mold.Fit == null || !mold.Fit.IsValid)
            {
                _analysis.text = "\n\n";
                return;
            }

            UndercutAnalysis.Report r = mold.Analysis;
            string axis = MoldFrame.AxisLabel(mold.Frame.Up);
            float trapped = 100f * r.TrappedFraction;

            _analysis.text =
                $"Pull {axis}   |   {mold.Fit.Field.Nr}x{mold.Fit.Field.Ne} grid\n" +
                $"Trapped surface {trapped:0.0}%   (worst depth {100f * r.WorstSeverity:0}%)\n" +
                (mold.Fit.Clipped ? "Surface was clipped to stay inside the block." : " ");
            _analysis.color = trapped > 5f ? WarnColor : trapped > 0.5f ? InfoColor : GoodColor;
        }

        private void RefreshExport(MoldController mold)
        {
            if (_exportReadout == null || !mold.HasHalves) return;
            // Only overwrite the readout while it is not showing an export's own report.
            if (_exportReadout.text.StartsWith("Exported") || _exportReadout.text.StartsWith("Nothing") ||
                _exportReadout.text.StartsWith("Export cancelled") || _exportReadout.text.StartsWith("Build the"))
                return;
            string res = mold.HalvesResolution > 0 ? $" (resolution {mold.HalvesResolution})" : "";
            _exportReadout.text = mold.HalvesAreFinal
                ? $"Halves are FINAL quality{res} - ready to export."
                : $"Halves are a draft{res}. Press Final before exporting for a print.";
            _exportReadout.color = mold.HalvesAreFinal ? GoodColor : WarnColor;
        }

        private static Color Tint(bool on) => on ? UIFactory.ActiveColor : UIFactory.InactiveColor;

        private static void SetLabel(Button button, string label)
        {
            if (button == null) return;
            var text = button.GetComponentInChildren<Text>();
            if (text != null) text.text = label;
        }
    }
}
