using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Sculpting.Molding
{
    /// What an edit changed, which decides the least work that brings everything back in step.
    public enum MoldChange
    {
        /// Nothing geometric - a build setting, a resolution.
        None,
        /// Something the parting surface is FITTED from: pull direction, grid, rounding, the
        /// block's size. Needs a re-fit.
        Fit,
        /// The pins, sprue or vents, or a setting only they read. Redraws their preview.
        Features,
        /// Which overlays are drawn.
        Overlays,
        /// The view mode: which of model and halves are shown, and how far apart.
        View,
    }

    /// Drives the whole mold-making workflow as a live session: fit or drag a parting surface,
    /// watch the model recolour itself by which half it lands in, drop pins and channels onto the
    /// surface and drag or rotate them around, then build the two halves - as draft first, final
    /// when you are happy.
    ///
    /// The Blender addon this is a port of is a one-shot operator: set a dozen properties, press
    /// Generate, wait, look at the result, adjust, press Generate again. Every decision in here is
    /// aimed at removing that loop, which means three things are held to hard budgets:
    ///
    /// - Nothing re-fits the parting surface unless the surface can actually change. Moving a pin
    ///   used to run a full fit per frame (179ms each on a 2.9M-triangle lure) because every edit
    ///   went through one "dirty" flag; now a feature edit redraws the features, a surface drag
    ///   re-offsets the field it already has, and only a pull-direction or fit setting re-fits.
    /// - Builds never block. The booleans run on a worker against a proxy of the model sized to
    ///   the build's own grid (see MoldCutterProxy), and the halves are swapped in when they land.
    ///   A draft that used to freeze the app for 5.9s on that lure is now a background job.
    /// - Every edit made in the workspace is one undo step - see Edit/BeginEdit and MoldState.
    ///
    /// Lives on SceneSystems alongside the other controllers, found via FindFirstObjectByType,
    /// matching how every other controller in this project is reached. Self-installs from
    /// MoldUIBuilder if the scene has not got one.
    public class MoldController : MonoBehaviour, IGizmoTargetSource, IGizmoPointerClaim
    {
        /// Fastest the live recompute will run while something is being dragged. 20Hz keeps a
        /// drag feeling continuous while leaving the rest of the frame budget to the renderer.
        /// Matches MaskExtractController's reasoning for its own throttle.
        private const float RecomputeInterval = 0.05f;

        /// Grid coarsening used while a drag is actually in flight: a rotation of the pull
        /// direction or a fit slider re-fits continuously at half the density (twice the cell
        /// size), and the full-resolution fit runs once on release.
        private const float DragResolutionScale = 0.5f;

        /// A click this close to a feature's body, in pixels, selects it even when it lands just
        /// off a thin vent.
        private const float FeaturePickPixels = 10f;

        /// How long after the last change a live rebuild fires. Long enough that dragging a
        /// slider or a feature does not queue a build behind every frame, short enough that
        /// letting go of the mouse and looking at the halves shows the change.
        private const float LiveBuildDelay = 0.45f;

        // ---------------------------------------------------------------------- the session

        private SculptableMesh _model;
        /// Bumped on every BeginSession. Undo steps capture it, and a step from a closed session
        /// reports itself as not applying rather than editing a session that no longer exists.
        private int _sessionSerial;

        private MoldFit _fit;
        /// The fitted surface as it came out of the fit, BEFORE the manual nudge - kept so a
        /// surface drag can re-offset it instead of re-fitting. The fit's own Field is always
        /// this plus the nudge, clamped.
        private PartingField _rawField;
        private bool _rawClipped;

        private MoldFrame _frame = MoldFrame.Identity;
        private readonly List<MoldFeature> _features = new List<MoldFeature>();
        private MoldFeature _selected;
        private int _nextFeatureId = 1;
        private readonly MoldOverlay _overlay = new MoldOverlay();

        /// Height the user has dragged the whole parting surface by, on top of whatever the fit
        /// produced. Kept separate from the field so a re-fit does not throw the nudge away.
        private float _manualOffset;

        // ------------------------------------------------------------------------- dirtiness

        private bool _fitDirty;
        /// The fit on screen came from a drag-resolution pass (or the proxy) and has to be
        /// replaced by a full one when the drag or slider that caused it lets go.
        private bool _fitIsInteractive;
        private bool _overlaysDirty;
        private bool _analysisDirty;
        private bool _needsAxisSearch;
        private float _nextRecompute;
        private bool _gizmoDragging;
        private bool _hadFocus;

        // ------------------------------------------------------------ the model, in world space

        private Vector3[] _worldBuffer;
        private int _worldCount;
        private Vector3[] _worldSource;
        private Matrix4x4 _worldMatrix;
        private ulong _geometryKey;
        private bool _worldValid;
        /// World-space size of the model, kept with the vertices. ResolveFrame needs it on every
        /// re-fit, and walking 730k vertices for it was 44ms of each one.
        private Vector3 _worldExtent = Vector3.one;

        /// Source measurements behind the build estimate. Refreshed on a full re-fit rather than
        /// on every throttled one: they only move when the model's geometry does.
        private float _modelArea;
        private float _modelMeanEdge;

        // ---------------------------------------------------------------------------- proxies

        private sealed class Proxy
        {
            public ulong Key;
            public float Cell;
            public Vector3[] Verts;
            public int[] Tris;
        }

        private Proxy _draftProxy, _finalProxy, _fitProxy;
        private Task<Proxy> _fitProxyTask;

        // ------------------------------------------------------------------ halves and builds

        private SculptableMesh _lowerHalf, _upperHalf;
        private bool _modelWasVisible = true;

        private sealed class BuildJob
        {
            public Task<(MoldBuilder.Geometry geometry, Proxy proxy)> Task;
            public int Version;
            public bool Draft;
            public bool Live;
            public float StartedAt;
            public int Serial;
        }

        private BuildJob _job;
        private bool _pendingExplicitBuild;
        private bool _pendingExplicitDraft;

        private int _featureVersion;
        private int _builtVersion = -1;
        private float _nextLiveBuild;

        // ------------------------------------------------------------------------------ undo

        private MoldState _editBefore;
        private string _editLabel;
        private bool _editInteractive;
        /// The open step belongs to a slider, and closes when the mouse is let go.
        private bool _sliderEditOpen;

        private readonly List<GizmoTarget> _gizmoTargets = new List<GizmoTarget>(1);
        private readonly List<MoldGeometry.Spine> _spineScratch = new List<MoldGeometry.Spine>(4);

        /// Settings whose change needs the surface re-fitted. Everything else is cheaper - see
        /// ApplyStep, which reads these to do the least an undo needs.
        private static readonly HashSet<string> FitFields = new HashSet<string>
        {
            nameof(MoldSettings.PullMode), nameof(MoldSettings.Axis), nameof(MoldSettings.SnapPullToAxis),
            nameof(MoldSettings.FittedSurface), nameof(MoldSettings.GridCellMm), nameof(MoldSettings.AutoGridDensity),
            nameof(MoldSettings.GridAcross), nameof(MoldSettings.GridDepth), nameof(MoldSettings.CentreOnVolume),
            nameof(MoldSettings.Rounding), nameof(MoldSettings.BlendRadius), nameof(MoldSettings.MinSamplesPerCell),
            nameof(MoldSettings.SplitOffset), nameof(MoldSettings.MillimetresPerUnit), nameof(MoldSettings.PaddingMm),
            nameof(MoldSettings.WallMm),
        };

        private static readonly HashSet<string> ViewFields = new HashSet<string>
        {
            nameof(MoldSettings.ViewMode), nameof(MoldSettings.ExplodeGap), nameof(MoldSettings.ShowUndercutTint),
            nameof(MoldSettings.ShowPartingSurface), nameof(MoldSettings.ShowBlockOutline),
        };

        /// Settings that only decide how or whether halves are built - nothing to redraw.
        private static readonly HashSet<string> BuildFields = new HashSet<string>
        {
            nameof(MoldSettings.LiveHalves), nameof(MoldSettings.DraftResolution),
            nameof(MoldSettings.FinalResolution), nameof(MoldSettings.AutoResolution),
        };

        // ------------------------------------------------------------------------- services

        private SelectionManager _selection;
        private SelectionManager Selection =>
            _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());

        private TransformGizmo _gizmo;
        private TransformGizmo Gizmo =>
            _gizmo != null ? _gizmo : (_gizmo = FindFirstObjectByType<TransformGizmo>());

        private SculptController _sculpt;
        private SculptController Sculpt =>
            _sculpt != null ? _sculpt : (_sculpt = FindFirstObjectByType<SculptController>());

        private Camera ActiveCamera
        {
            get
            {
                Camera fromController = Sculpt != null ? Sculpt.ActiveCamera : null;
                return fromController != null ? fromController : Camera.main;
            }
        }

        // --------------------------------------------------------------------- public state

        public MoldSettings Settings { get; } = new MoldSettings();

        /// True while a session is open - the model is chosen, overlays are up and the panel
        /// shows the full set of controls.
        public bool IsActive => _model != null;

        /// True while the session also owns the mouse. A session stays open if the user switches
        /// to Transpose to nudge the model, but it stands down from input until they come back.
        public bool HasInputFocus => IsActive && Gizmo != null && Gizmo.Mode == GizmoMode.Mold;

        public SculptableMesh Model => _model;
        public MoldFit Fit => _fit;
        public MoldFrame Frame => _frame;
        public IReadOnlyList<MoldFeature> Features => _features;
        public MoldFeature SelectedFeature => _selected;
        public UndercutAnalysis.Report Analysis => _overlay.LastReport;
        public bool HasHalves => IsLiveHalf(_lowerHalf) || IsLiveHalf(_upperHalf);

        /// True while halves are being built in the background.
        public bool IsBuilding => _job != null;
        /// What the builder is doing right now, for the panel. Null when idle.
        public string BuildActivity =>
            _job == null ? null
            : $"Building {(_job.Draft ? "draft" : "final")} halves in the background... {Time.unscaledTime - _job.StartedAt:0.0}s";

        /// True when the halves on screen came from a Final build and nothing has changed since -
        /// the only state worth exporting for a real print.
        public bool HalvesAreFinal { get; private set; }
        /// Resolution the halves on screen were built at.
        public int HalvesResolution { get; private set; }

        /// Bumped whenever something the panel shows may have changed underneath it - an undo, a
        /// redo, a placement - so the panel re-reads every control rather than trusting its own.
        public int StateVersion { get; private set; }

        /// What the next viewport click places. Stays armed so a run of pins can be dropped in
        /// one go; Escape or pressing the same button again disarms it. A sprue disarms itself
        /// after one click - there is only ever one.
        ///
        /// Arming from a solid-half view switches back to the model, because a placement click
        /// lands on the parting SURFACE and those views bury it inside the built halves.
        public MoldPlacement Placement
        {
            get => _placement;
            set
            {
                _placement = value;
                if (value == MoldPlacement.None || _model == null) return;

                if (Settings.ViewMode == MoldViewMode.Halves || Settings.ViewMode == MoldViewMode.Exploded)
                {
                    Settings.ViewMode = MoldViewMode.Model;
                    ApplyViewMode();
                    _overlaysDirty = true;
                    Status = "Switched to Model view - click the parting surface to place a "
                             + value.ToString().ToLowerInvariant() + ".";
                }
            }
        }

        private MoldPlacement _placement = MoldPlacement.None;

        /// What a build at `resolution` would cost and what it would resolve. Cheap enough to
        /// call from a slider callback - see MoldBuildEstimate.
        public MoldBuildEstimate EstimateAt(int resolution) =>
            _fit != null && _fit.IsValid
                ? MoldBuildEstimate.For(_fit.Block, _modelArea, _modelMeanEdge, resolution)
                : default;

        /// The resolution at which the cavity stops throwing away detail the model actually has.
        public int MatchModelResolution() =>
            _fit != null && _fit.IsValid
                ? MoldBuildEstimate.MatchModelResolution(_fit.Block, _modelMeanEdge)
                : Settings.FinalResolution;

        /// Mean edge length of the source mesh's triangles - the scale of the finest thing the
        /// model has to transfer.
        public float ModelMeanEdge => _modelMeanEdge;

        /// One line for the panel, describing the last thing that happened.
        public string Status { get; private set; } = "No mold session.";
        /// Non-null when the current surface cannot be built from, with the reason.
        public string Error { get; private set; }

        // ---------------------------------------------------------------------- the session

        /// Opens a session on the current selection. Safe to call while one is already open - it
        /// re-aims at whatever is selected now, which is what the panel's button does.
        public void BeginSession()
        {
            SculptableMesh target = Selection != null ? Selection.PrimarySelection : null;
            if (target == null)
            {
                Status = "Select the model you want a mold of first.";
                return;
            }

            if (_model != null && _model != target) EndSession(keepHalves: true);

            _model = target;
            _sessionSerial++;
            _modelWasVisible = target.Visible;
            Settings.InitialiseForBounds(WorldBounds(target));
            _manualOffset = 0f;
            _features.Clear();
            _selected = null;
            _worldValid = false;
            _draftProxy = _finalProxy = _fitProxy = null;
            _fitProxyTask = null;
            HalvesAreFinal = false;
            HalvesResolution = 0;
            CancelOpenEdit();

            // Pick up this model's halves from an earlier session, so a rebuild replaces them in
            // place - rather than leaving the previous session's pair behind as duplicates, or
            // worse, keeping ANOTHER model's halves and overwriting them with this one's mold.
            _lowerHalf = FindHalf(target.name + " Mold Lower");
            _upperHalf = FindHalf(target.name + " Mold Upper");

            Gizmo?.SetMode(GizmoMode.Mold);
            if (Settings.PullMode == PullMode.FromView || Settings.PullMode == PullMode.Manual)
                Settings.PullMode = PullMode.Auto;
            _needsAxisSearch = Settings.PullMode == PullMode.Auto;
            RefitSurface(interactive: false);

            // Size the final build to the model rather than leaving it at a constant. A fixed
            // number cannot be right for both a sphere and a lure - it counts cells along the
            // block's longest axis, which on a flat model is the axis the detail is NOT on - so
            // the default is derived from the source's own triangle size instead.
            Settings.FinalResolution = MatchModelResolution();

            // The suggestions are the addon's automatic pins/sprue/vent, offered up front rather
            // than baked in: they land as ordinary features the user can drag, rotate, resize or
            // delete. Part of opening the session, so not an undo step of its own.
            if (_fit != null && _fit.IsValid) SuggestAllInternal();

            StartFitProxy();
            StateVersion++;
            Status = $"Mold session on {target.name}. Drag the surface, place pins, then build.";
        }

        public void EndSession(bool keepHalves = true)
        {
            if (_model == null) return;

            CancelOpenEdit();
            if (Selection != null && _model != null && !_model.Equals(null) && _model.Visible != _modelWasVisible)
                Selection.SetVisible(_model, _modelWasVisible);

            if (!keepHalves) ClearHalves();

            _overlay.Dispose();
            Gizmo?.ClearExternalTargets(this);
            if (Gizmo != null && Gizmo.Mode == GizmoMode.Mold) Gizmo.SetMode(GizmoMode.Sculpt);

            _model = null;
            _fit = null;
            _rawField = null;
            _selected = null;
            _features.Clear();
            _worldValid = false;
            _draftProxy = _finalProxy = _fitProxy = null;
            _fitProxyTask = null;
            // A running build finishes on its worker and is dropped when it lands - see PollBuild.
            _placement = MoldPlacement.None;
            StateVersion++;
            Status = "Mold session closed.";
            Error = null;
        }

        /// Re-resolves the pull direction from the current settings and re-fits, throwing away
        /// any nudge the surface was dragged by. The only path that runs the three-axis search,
        /// because it is the only expensive one - every other edit reuses the frame it settled
        /// on. In From View mode this is also what re-reads the camera.
        public void Refit()
        {
            if (_model == null) return;
            Edit("Re-fit Parting Surface", MoldChange.None, () =>
            {
                _needsAxisSearch = Settings.PullMode == PullMode.Auto;
                if (Settings.PullMode == PullMode.FromView)
                    _frame = MoldFrame.FromCamera(ActiveCamera, Settings.SnapPullToAxis);
                _manualOffset = 0f;
                RefreshWorld(verify: true);
                RefitSurface(interactive: false);
            });
        }

        /// Sets the pull mode (and, for FixedAxis, the axis) as one undoable step.
        public void SetPullMode(PullMode mode, int axis = -1)
        {
            Edit(mode == PullMode.FixedAxis ? "Pull Along " + "XYZ"[Mathf.Clamp(axis, 0, 2)] : "Pull " + mode, MoldChange.None, () =>
            {
                Settings.PullMode = mode;
                if (axis >= 0) Settings.Axis = axis;
                if (mode == PullMode.FromView) _frame = MoldFrame.FromCamera(ActiveCamera, Settings.SnapPullToAxis);
                _needsAxisSearch = mode == PullMode.Auto;
                _manualOffset = 0f;
                RefitSurface(interactive: false);
            });
        }

        // ------------------------------------------------------------------------ edits + undo

        /// Runs `apply` as one undoable step labelled `label`, then does whatever `change` needs.
        ///
        /// Every control in the workspace goes through here, which is what makes "everything in
        /// the mold maker is undoable" true by construction rather than by remembering. A drag
        /// that is already open (a slider being held, a gizmo being dragged) absorbs the change
        /// into its own step instead of recording one per frame.
        public void Edit(string label, MoldChange change, System.Action apply)
        {
            if (_model == null) { apply(); return; }

            // A slider step whose mouse has already been let go is closed first, so a toggle
            // clicked straight after a slider drag gets its own step instead of joining it.
            if (_sliderEditOpen && !MouseHeld()) EndEdit();

            bool owns = _editBefore == null;
            if (owns) BeginEdit(label, interactive: false);
            apply();
            Changed(change);
            if (owns) EndEdit();
        }

        /// A slider's change. The first change of a drag opens a step - capturing the state
        /// BEFORE it - later ones join it, and the step closes when the mouse is let go (see
        /// Update). Opened by the change rather than by the press, because a click on the track
        /// jumps the value before any press hook would have run: a step opened on the press
        /// would have missed the jump and recorded the drag as two steps.
        public void SliderEdit(string label, MoldChange change, System.Action apply)
        {
            if (_model == null) { apply(); return; }
            if (_editBefore == null)
            {
                BeginEdit(label, interactive: true);
                _sliderEditOpen = true;
            }
            apply();
            Changed(change);
        }

        /// Opens a step that stays open across many changes - a slider drag, a gizmo drag - and
        /// is recorded once by EndEdit. `interactive` edits re-fit at drag resolution while open.
        public void BeginEdit(string label, bool interactive = true)
        {
            if (_model == null) return;
            if (_editBefore != null) EndEdit();
            _editBefore = Capture();
            _editLabel = label;
            _editInteractive = interactive;
        }

        /// Closes the open step and records it if anything actually changed. A drag that ends
        /// where it started is not an edit - recording it would spend an undo press doing
        /// nothing visible, which reads exactly like undo is broken.
        public void EndEdit()
        {
            MoldState before = _editBefore;
            string label = _editLabel;
            _editBefore = null;
            _editInteractive = false;
            _sliderEditOpen = false;
            if (before == null || _model == null) return;

            // Anything a drag left pending - or left at drag quality - is finished at full
            // quality before the "after" is captured, so the step records the settled state.
            if (_fitDirty || _fitIsInteractive) RefitSurface(interactive: false);

            MoldState after = Capture();
            MoldState.Delta delta = MoldState.Compare(before, after);
            if (!delta.Any) return;

            int serial = _sessionSerial;
            EditHistory.RecordSceneAction(label,
                undo: () => ApplyStep(before, delta, serial),
                redo: () => ApplyStep(after, delta, serial),
                discard: null,
                approxBytes: before.ApproxBytes + after.ApproxBytes);
            StateVersion++;
        }

        private void CancelOpenEdit()
        {
            _editBefore = null;
            _editInteractive = false;
            _sliderEditOpen = false;
        }

        private MoldState Capture() => MoldState.Capture(Settings, _features, _manualOffset, _frame);

        /// Does the least work that brings the session in line with a changed setting or feature.
        private void Changed(MoldChange change)
        {
            switch (change)
            {
                case MoldChange.Fit:
                    _fitDirty = true;
                    MarkHalvesStale();
                    break;
                case MoldChange.Features:
                    RefreshFeatureOverlay();
                    break;
                case MoldChange.Overlays:
                    _overlaysDirty = true;
                    _analysisDirty = true;
                    break;
                case MoldChange.View:
                    ApplyViewMode();
                    _overlaysDirty = true;
                    _analysisDirty = true;
                    break;
            }
        }

        /// Undo and redo land here. Returns false - "this step no longer applies" - once the
        /// session it was recorded in is gone, so EditHistory moves on to the step before.
        private bool ApplyStep(MoldState target, MoldState.Delta delta, int serial)
        {
            if (_model == null || _model.Equals(null) || serial != _sessionSerial) return false;

            CancelOpenEdit();
            int selectedId = _selected != null ? _selected.Id : -1;
            target.ApplyTo(delta, Settings, _features, ref _manualOffset, ref _frame);
            _selected = _features.Find(f => f.Id == selectedId);

            bool refit = delta.Frame || delta.TouchesAny(FitFields);
            bool view = delta.TouchesAny(ViewFields);

            if (refit)
            {
                _needsAxisSearch = false;
                RefitSurface(interactive: false);
            }
            else
            {
                if (delta.Offset) ApplyOffsetToFit();
                if (view) ApplyViewMode();
                RefreshAllOverlays(analysis: delta.Offset || view);
            }

            if (!delta.OnlySettingsIn(ViewFields) && !delta.OnlySettingsIn(BuildFields)) MarkHalvesStale();

            PushGizmoTargets();
            StateVersion++;
            return true;
        }

        // --------------------------------------------------------------------------- update

        private void Update()
        {
            if (_model == null) return;

            // The model can be deleted (or the scene reloaded) with a session open.
            if (_model.Equals(null))
            {
                EndSession();
                return;
            }

            PollBuild();
            PollFitProxy();

            if (_sliderEditOpen)
            {
                Mouse held = Mouse.current;
                if (held == null || !held.leftButton.isPressed) EndEdit();
            }

            if (!HasInputFocus)
            {
                // Another tool owns the gizmo now - let go of it rather than fighting for the
                // same handles, the same carve-out every other tool in this project makes. The
                // overlays stay up so the session is visibly still there.
                Gizmo?.ClearExternalTargets(this);
                _hadFocus = false;
                return;
            }

            if (!_hadFocus)
            {
                // Back from another tool, which may have moved or sculpted the model.
                _hadFocus = true;
                if (RefreshWorld(verify: true)) _fitDirty = true;
            }

            // The turntable's clean view is look-only - no handles, no clicks, no keys.
            if (TurntableController.CleanViewActive) return;

            PushGizmoTargets();
            HandleKeys();
            HandleMouse();

            if (Time.unscaledTime >= _nextRecompute)
            {
                if (_fitDirty)
                {
                    _nextRecompute = Time.unscaledTime + RecomputeInterval;
                    RefitSurface(interactive: _gizmoDragging || _editInteractive);
                }
                else if (_overlaysDirty || _analysisDirty)
                {
                    _nextRecompute = Time.unscaledTime + RecomputeInterval;
                    RefreshAllOverlays(analysis: _analysisDirty);
                }
            }

            TickLiveHalves();
        }

        private void OnDisable() => EndSession();

        // ---------------------------------------------------------------------- recompute

        /// Re-fits the parting surface. `interactive` runs at drag resolution against the fit
        /// proxy when there is one, which is what keeps a rotating pull direction or a held fit
        /// slider continuous; everything else runs at full quality against the real mesh.
        private void RefitSurface(bool interactive)
        {
            _fitDirty = false;
            _fitIsInteractive = interactive;
            if (_model == null) return;

            RefreshWorld(verify: false);
            Vector3[] world = _worldBuffer;
            int vertexCount = _worldCount;
            int[] tris = _model.TrianglesExact();
            if (vertexCount == 0 || tris == null || tris.Length < 3)
            {
                Error = "the model has no geometry";
                return;
            }

            if (!interactive)
            {
                MoldBuildEstimate.MeasureSource(world, tris, tris.Length / 3, out _modelArea, out _modelMeanEdge);
            }

            MoldSettings pass = Settings;
            Vector3[] fitVerts = world;
            int[] fitTris = tris;
            int fitCount = vertexCount;
            if (interactive)
            {
                pass = Settings.Clone();
                // Coarser is a BIGGER cell, so the density scale divides rather than multiplies.
                pass.GridCellMm = Settings.GridCellMm / Mathf.Max(DragResolutionScale, 1e-3f);
                pass.GridAcross = Mathf.Max(8, Mathf.RoundToInt(Settings.GridAcross * DragResolutionScale));
                pass.GridDepth = Mathf.Max(8, Mathf.RoundToInt(Settings.GridDepth * DragResolutionScale));

                Proxy proxy = _fitProxy;
                if (proxy != null && proxy.Key == _geometryKey && proxy.Verts.Length > 0)
                {
                    fitVerts = proxy.Verts;
                    fitTris = proxy.Tris;
                    fitCount = proxy.Verts.Length;
                }
            }

            if (_needsAxisSearch && Settings.PullMode == PullMode.Auto)
            {
                _fit = PartingSurfaceFitter.FitBestAxis(fitVerts, fitTris, fitCount, pass, out int axis);
                if (_fit.IsValid)
                {
                    _frame = _fit.Frame;
                    Settings.Axis = axis;
                    _needsAxisSearch = false;
                }
            }
            else
            {
                _frame = ResolveFrame();
                _fit = Settings.FittedSurface
                    ? PartingSurfaceFitter.Fit(fitVerts, fitTris, fitCount, _frame, pass)
                    : PartingSurfaceFitter.Flat(fitVerts, fitTris, fitCount, _frame, pass);
            }

            Error = _fit != null ? _fit.Error : "the fit failed";
            if (_fit == null || !_fit.IsValid)
            {
                _rawField = null;
                _overlay.HideAll();
                return;
            }

            // Track the mesh rather than the slider: the resolution the cavity needs is a
            // property of how dense the model is.
            if (!interactive && Settings.AutoResolution)
                Settings.FinalResolution = MoldBuildEstimate.MatchModelResolution(_fit.Block, _modelMeanEdge);

            _rawField = _fit.Field.Clone();
            _rawClipped = _fit.Clipped;
            ApplyOffsetToFit();

            ClampFeaturesToBlock();
            RefreshAllOverlays(analysis: true);
            ApplyViewMode();
            MarkHalvesStale();

            // Keep the drag-time proxy in step with the model and the grid it fits at.
            if (!interactive && (_fitProxy == null || _fitProxy.Key != _geometryKey ||
                                 !Mathf.Approximately(_fitProxy.Cell, FitProxyCell)) && _fitProxyTask == null)
                StartFitProxy();
        }

        /// Puts the manual nudge on top of the raw fitted field. Cheap - a copy and an add over
        /// the grid - which is why a surface drag goes through here rather than through a re-fit:
        /// sliding the sheet along the pull axis cannot change the fit, only where it sits.
        ///
        /// The nudge is held to the range the clamp can actually express, so dragging the sheet
        /// past the block and back does not first have to unwind an overshoot nobody can see.
        private void ApplyOffsetToFit()
        {
            if (_fit == null || _rawField == null) return;

            float margin = 0.3f * Settings.Wall;
            float lo = _fit.Block.UBottom + margin, hi = _fit.Block.UTop - margin;
            _rawField.MinMaxHeight(out float rawMin, out float rawMax);
            float minOffset = lo - rawMin, maxOffset = Mathf.Max(minOffset, hi - rawMax);
            _manualOffset = Mathf.Clamp(_manualOffset, minOffset, maxOffset);

            PartingField field = _rawField.Clone();
            bool clipped = _rawClipped;
            if (Mathf.Abs(_manualOffset) > 1e-7f)
            {
                field.Offset(_manualOffset);
                clipped |= field.ClampHeights(lo, hi);
            }
            _fit.Field = field;
            _fit.Clipped = clipped;
            _fit.BlockedFraction = PartingSurfaceFitter.BlockedFraction(field, _fit.Columns);
        }

        private MoldFrame ResolveFrame()
        {
            switch (Settings.PullMode)
            {
                // From View captured the camera once, when it was chosen (or re-fitted) - it is
                // not re-read on every slider move, or orbiting the camera would quietly re-aim
                // the mold the next time anything was touched.
                case PullMode.FromView:
                case PullMode.Manual:
                    return _frame;
                case PullMode.FixedAxis:
                    return MoldFrame.FromWorldAxis(Mathf.Clamp(Settings.Axis, 0, 2), _worldExtent);
                default:
                    // Auto, after its search has already run: keep the axis it settled on rather
                    // than re-searching on every slider move.
                    return MoldFrame.FromWorldAxis(Mathf.Clamp(Settings.Axis, 0, 2), _worldExtent);
            }
        }

        /// Redraws the sheet, the box and the features, and re-runs the undercut analysis when
        /// `analysis` - the part that walks every vertex, so only when the surface moved.
        private void RefreshAllOverlays(bool analysis)
        {
            _overlaysDirty = false;
            if (_fit == null || !_fit.IsValid) { _overlay.HideAll(); return; }

            _overlay.UpdateSurface(_fit.Field, Settings.ShowPartingSurface);
            _overlay.UpdateBox(_fit.Block, _frame, Settings.ShowBlockOutline);
            _overlay.UpdateFeatures(_features, _fit.Field, _fit.Block, _fit.Columns, Settings, _selected);

            if (analysis || _analysisDirty) UpdateAnalysis();
        }

        /// The undercut tint and the report behind it. The report is needed whether or not the
        /// tint is on screen - the build checks it and the panel reads it - so it is refreshed
        /// either way; only the colour upload depends on visibility.
        private void UpdateAnalysis()
        {
            _analysisDirty = false;
            if (_fit == null || !_fit.IsValid || _model == null) return;

            RefreshWorld(verify: false);
            bool showModelOverlays = Settings.ViewMode == MoldViewMode.Model ||
                                     Settings.ViewMode == MoldViewMode.Assembled;
            // The seam band is a fixed fraction of the model's height along the pull axis, so it
            // reads the same width whatever scale the sculpt is at.
            float seam = 0.004f * Mathf.Max(_fit.Block.ModelExtent.y, 1e-4f);
            _overlay.UpdateTint(_model, _worldBuffer, _worldCount, _geometryKey, _fit.Field, _fit.Columns,
                                Settings.ShowUndercutTint && showModelOverlays && _model.Visible, seam);
        }

        /// Redraws just the pin/sprue/vent preview, without re-fitting - a feature cannot change
        /// the parting surface, which is fitted from the MODEL.
        private void RefreshFeatureOverlay()
        {
            if (_fit != null && _fit.IsValid)
                _overlay.UpdateFeatures(_features, _fit.Field, _fit.Block, _fit.Columns, Settings, _selected);
            MarkHalvesStale();
        }

        /// Keeps every feature inside the block after a padding change or a re-fit - a pin left
        /// outside the footprint would be built into thin air.
        private void ClampFeaturesToBlock()
        {
            if (_fit == null || !_fit.IsValid) return;
            MoldBlock b = _fit.Block;
            foreach (MoldFeature f in _features)
            {
                f.R = Mathf.Clamp(f.R, b.R0, b.R1);
                f.E = Mathf.Clamp(f.E, b.E0, b.E1);
            }
        }

        // ----------------------------------------------------------- the model in world space

        /// Brings the world-space copy of the model up to date. Returns true when it changed.
        ///
        /// `verify` fingerprints the vertex positions even when nothing obvious moved, which is
        /// what catches a sculpt stroke (it moves vertices in place, leaving the count, the array
        /// and the transform all identical). That costs a couple of milliseconds, so it runs on
        /// explicit re-fits, builds and returning from another tool - not on every drag frame.
        private bool RefreshWorld(bool verify)
        {
            if (_model == null) return false;
            int n = _model.VertexCount;
            Vector3[] local = _model.Vertices;
            Matrix4x4 m = _model.transform.localToWorldMatrix;

            bool same = _worldValid && n == _worldCount && ReferenceEquals(local, _worldSource) && m == _worldMatrix;
            if (same && !verify) return false;

            ulong key = MoldCutterProxy.Fingerprint(local, n) ^ MatrixHash(m);
            if (same && key == _geometryKey) return false;

            // Exactly sized, never merely "big enough": the column map and the fitter measure the
            // model's bounds over the whole array, and a stale tail left over from a denser mesh
            // would stretch them.
            if (_worldBuffer == null || _worldBuffer.Length != n) _worldBuffer = new Vector3[n];
            Vector3[] outBuffer = _worldBuffer;
            int parts = n >= 65536 ? Mathf.Clamp(System.Environment.ProcessorCount, 1, 16) : 1;
            int chunk = (n + parts - 1) / parts;
            var mins = new Vector3[parts];
            var maxs = new Vector3[parts];
            Parallel.For(0, parts, p =>
            {
                int a = p * chunk, b = Mathf.Min(n, a + chunk);
                if (a >= b) return;
                Vector3 lo = m.MultiplyPoint3x4(local[a]), hi = lo;
                outBuffer[a] = lo;
                for (int i = a + 1; i < b; i++)
                {
                    Vector3 w = m.MultiplyPoint3x4(local[i]);
                    outBuffer[i] = w;
                    lo.x = lo.x < w.x ? lo.x : w.x; hi.x = hi.x > w.x ? hi.x : w.x;
                    lo.y = lo.y < w.y ? lo.y : w.y; hi.y = hi.y > w.y ? hi.y : w.y;
                    lo.z = lo.z < w.z ? lo.z : w.z; hi.z = hi.z > w.z ? hi.z : w.z;
                }
                mins[p] = lo;
                maxs[p] = hi;
            });
            Vector3 min = mins[0], max = maxs[0];
            for (int p = 1; p < parts; p++)
            {
                if (p * chunk >= n) break;
                min = Vector3.Min(min, mins[p]);
                max = Vector3.Max(max, maxs[p]);
            }
            _worldExtent = n > 0 ? max - min : Vector3.one;

            _worldCount = n;
            _worldSource = local;
            _worldMatrix = m;
            _geometryKey = key;
            _worldValid = true;
            return true;
        }

        private static ulong MatrixHash(Matrix4x4 m)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < 16; i++)
                h = (h ^ (uint)System.BitConverter.SingleToInt32Bits(m[i])) * 1099511628211UL;
            return h;
        }

        private Vector3[] WorldCopy()
        {
            var copy = new Vector3[_worldCount];
            System.Array.Copy(_worldBuffer, copy, _worldCount);
            return copy;
        }

        /// Cluster size for the drag-time fit proxy: half the cell a drag fits at.
        private float FitProxyCell => 0.5f * Settings.ToUnits(Settings.GridCellMm / Mathf.Max(DragResolutionScale, 1e-3f));

        /// Starts clustering the model for drag-time fits in the background, so the first drag
        /// of a session does not pay for it.
        private void StartFitProxy()
        {
            if (_model == null || !_worldValid || _fitProxyTask != null) return;
            ulong key = _geometryKey;
            float cell = FitProxyCell;
            if (_fitProxy != null && _fitProxy.Key == key && Mathf.Approximately(_fitProxy.Cell, cell)) return;

            Vector3[] verts = WorldCopy();
            int[] tris = _model.TrianglesExact();
            _fitProxyTask = Task.Run(() =>
            {
                MoldCutterProxy.Cluster(verts, verts.Length, tris, tris.Length, cell, out Vector3[] pv, out int[] pt);
                return new Proxy { Key = key, Cell = cell, Verts = pv, Tris = pt };
            });
        }

        private void PollFitProxy()
        {
            if (_fitProxyTask == null || !_fitProxyTask.IsCompleted) return;
            Task<Proxy> task = _fitProxyTask;
            _fitProxyTask = null;
            if (task.Status == TaskStatus.RanToCompletion && task.Result != null && task.Result.Key == _geometryKey)
                _fitProxy = task.Result;
        }

        // ------------------------------------------------------------------------- features

        public void SuggestAll()
        {
            if (_fit == null || !_fit.IsValid) return;
            Edit("Suggest Mold Features", MoldChange.Features, SuggestAllInternal);
        }

        private void SuggestAllInternal()
        {
            _features.RemoveAll(f => f.Suggested);
            if (Settings.Pins) AddFeatures(MoldGeometry.SuggestPins(_fit.Block, Settings, _fit.Field));
            AddFeatures(MoldGeometry.SuggestChannels(_fit.Block, Settings, _fit.Field, _fit.Columns));
            _selected = null;
            RefreshFeatureOverlay();
        }

        private void AddFeatures(List<MoldFeature> list)
        {
            foreach (MoldFeature f in list)
            {
                f.Id = _nextFeatureId++;
                _features.Add(f);
            }
        }

        public void ClearFeatures()
        {
            if (_features.Count == 0) return;
            Edit("Clear Mold Features", MoldChange.Features, () =>
            {
                _features.Clear();
                _selected = null;
            });
        }

        public void DeleteSelectedFeature()
        {
            if (_selected == null) return;
            MoldFeature doomed = _selected;
            Edit("Delete " + doomed.Kind, MoldChange.Features, () =>
            {
                _features.Remove(doomed);
                _selected = null;
            });
        }

        public void SelectFeature(MoldFeature feature)
        {
            if (feature != null && feature.IsMirror) feature = feature.Source;
            if (feature != null && !_features.Contains(feature)) feature = null;
            if (_selected == feature) return;
            _selected = feature;
            // The gizmo has to be re-pointed immediately rather than on the next Update, so the
            // handles appear on the feature the same frame it was clicked.
            PushGizmoTargets();
            if (_fit != null && _fit.IsValid)
                _overlay.UpdateFeatures(_features, _fit.Field, _fit.Block, _fit.Columns, Settings, _selected);
            StateVersion++;
        }

        /// "Pin 2", "Vent 1", "Sprue" - what the panel lists each feature as.
        public string FeatureLabel(MoldFeature feature)
        {
            if (feature == null) return "";
            if (feature.Kind == MoldFeatureKind.Sprue) return "Sprue";
            int n = 0;
            foreach (MoldFeature f in _features)
            {
                if (f.Kind == feature.Kind) n++;
                if (f == feature) break;
            }
            return feature.Kind + " " + n;
        }

        /// Where a channel's parts actually are right now, for the panel's readouts.
        public MoldGeometry.ChannelLayout LayoutOf(MoldFeature feature) =>
            _fit != null && _fit.IsValid && feature != null
                ? MoldGeometry.LayoutChannel(feature, _fit.Field, _fit.Block, _fit.Columns, Settings)
                : default;

        // ------------------------------------------------------ edits the panel's controls make

        /// "This model is `mm` millimetres long." Rescales every physical size at once - the
        /// settings are millimetres already, and every placed feature is rescaled with them so
        /// a 14mm sprue stays 14mm whatever the model is declared to be.
        public void SetModelLengthMm(float mm, float modelLongestAxisUnits)
        {
            Edit("Set Model Length", MoldChange.Fit, () =>
            {
                float before = Settings.MillimetresPerUnit;
                Settings.SetModelLengthMm(mm, modelLongestAxisUnits);
                float factor = before / Mathf.Max(Settings.MillimetresPerUnit, 1e-6f);
                if (Mathf.Abs(factor - 1f) > 1e-6f)
                    foreach (MoldFeature f in _features) f.ScaleSizes(factor);
            });
        }

        /// Sets one of the channel/pin size settings and applies it to every feature of that
        /// kind already placed - the panel's size section is the mold's sizes, not only the next
        /// placement's defaults. The Selected Feature section overrides one feature at a time.
        public void SetSizeMm(MoldSize size, float mm)
        {
            Edit(SizeLabel(size), MoldChange.Features, () =>
            {
                MoldSettings s = Settings;
                switch (size)
                {
                    case MoldSize.SprueHub: s.SprueDiameterMm = mm; break;
                    case MoldSize.SprueFunnel: s.SprueFunnelDiameterMm = mm; break;
                    case MoldSize.Runner: s.RunnerDiameterMm = mm; break;
                    case MoldSize.Vent: s.VentDiameterMm = mm; break;
                    case MoldSize.PinRadius: s.PinRadiusMm = mm; break;
                    case MoldSize.PinHeight: s.PinHeightMm = mm; break;
                }

                foreach (MoldFeature f in _features)
                {
                    switch (size)
                    {
                        case MoldSize.SprueHub when f.Kind == MoldFeatureKind.Sprue:
                            f.RadiusInner = s.SprueRadiusInner;
                            f.RadiusOuter = Mathf.Max(f.RadiusOuter, f.RadiusInner);
                            break;
                        case MoldSize.SprueFunnel when f.Kind == MoldFeatureKind.Sprue:
                            f.RadiusOuter = Mathf.Max(s.SprueRadiusOuter, f.RadiusInner);
                            break;
                        case MoldSize.Runner when f.Kind == MoldFeatureKind.Sprue:
                            f.RunnerRadius = s.RunnerRadius;
                            break;
                        case MoldSize.Vent when f.Kind == MoldFeatureKind.Vent:
                            f.RadiusInner = f.RadiusOuter = s.VentRadius;
                            break;
                        case MoldSize.PinRadius when f.Kind == MoldFeatureKind.Pin:
                            f.RadiusInner = s.PinRadius;
                            break;
                        case MoldSize.PinHeight when f.Kind == MoldFeatureKind.Pin:
                            f.Height = s.PinHeight;
                            break;
                    }
                }
            });
        }

        private static string SizeLabel(MoldSize size)
        {
            switch (size)
            {
                case MoldSize.SprueHub: return "Sprue Diameter";
                case MoldSize.SprueFunnel: return "Sprue Funnel Diameter";
                case MoldSize.Runner: return "Runner Diameter";
                case MoldSize.Vent: return "Vent Diameter";
                case MoldSize.PinRadius: return "Pin Radius";
                default: return "Pin Height";
            }
        }

        /// Edits the selected feature through one of its own sizes, in millimetres.
        public void SetSelectedMm(MoldSize size, float mm)
        {
            MoldFeature f = _selected;
            if (f == null) return;
            Edit("Resize " + FeatureLabel(f), MoldChange.Features, () =>
            {
                float units = Settings.ToUnits(mm);
                switch (size)
                {
                    case MoldSize.PinRadius: f.RadiusInner = units; break;
                    case MoldSize.PinHeight: f.Height = units; break;
                    case MoldSize.Vent:
                        f.RadiusInner = f.RadiusOuter = 0.5f * units;
                        break;
                    case MoldSize.SprueHub:
                        f.RadiusInner = 0.5f * units;
                        f.RadiusOuter = Mathf.Max(f.RadiusOuter, f.RadiusInner);
                        break;
                    case MoldSize.SprueFunnel:
                        f.RadiusOuter = Mathf.Max(0.5f * units, f.RadiusInner);
                        break;
                    case MoldSize.Runner:
                        f.RunnerRadius = 0.5f * units;
                        break;
                }
            });
        }

        /// Points the selected channel a new way, in degrees in the parting plane.
        public void SetSelectedAngle(float degrees)
        {
            MoldFeature f = _selected;
            if (f == null || !f.IsChannel) return;
            Edit("Rotate " + FeatureLabel(f), MoldChange.Features, () => f.AngleDeg = MoldFeature.WrapAngle(degrees));
        }

        /// Sets how far the selected sprue's funnel stands off the model, in millimetres - the
        /// length of its runner. Moves the hub along the sprue's own line.
        public void SetSelectedSprueGapMm(float mm)
        {
            MoldFeature f = _selected;
            if (f == null || f.Kind != MoldFeatureKind.Sprue || _fit == null || !_fit.IsValid) return;
            MoldGeometry.ChannelLayout layout = LayoutOf(f);
            if (!layout.Connected) return;

            Edit("Move " + FeatureLabel(f), MoldChange.Features, () =>
            {
                Vector2 hub = layout.Gate + f.Direction * Settings.ToUnits(Mathf.Max(mm, 0f));
                f.R = Mathf.Clamp(hub.x, _fit.Block.R0, _fit.Block.R1);
                f.E = Mathf.Clamp(hub.y, _fit.Block.E0, _fit.Block.E1);
            });
        }

        // ----------------------------------------------------------------------- the build

        /// Builds both halves in the background. `draft` uses the low resolution meant for
        /// iterating; the final build is the same code at the resolution the cavity needs.
        ///
        /// The halves already on screen stay up until the new ones land, and the final build
        /// replaces the same two objects rather than making new ones, so pin placement, camera
        /// and selection all survive it.
        public void BuildHalves(bool draft) => RequestBuild(draft, live: false);

        private void RequestBuild(bool draft, bool live)
        {
            if (_model == null || _fit == null || !_fit.IsValid)
            {
                if (!live) Status = "Fit a parting surface first.";
                return;
            }

            if (_job != null)
            {
                // One build at a time: the extraction's scratch is shared, and two booleans on a
                // dense model at once would only both be slower. An explicit press waits its
                // turn; a live one is simply re-evaluated when the running build lands.
                if (!live)
                {
                    _pendingExplicitBuild = true;
                    _pendingExplicitDraft = draft;
                    Status = "Queued - a build is already running.";
                }
                return;
            }

            if (_analysisDirty) UpdateAnalysis();
            if (!UndercutAnalysis.CutsThroughModel(_overlay.LastReport))
            {
                if (!live)
                    Status = "The parting surface does not cut through the model - move it into the model first.";
                // Marked as built anyway, or the live path retries this every frame forever.
                _builtVersion = _featureVersion;
                return;
            }

            RefreshWorld(verify: !live);
            int resolution = draft ? Settings.DraftResolution : Settings.FinalResolution;
            MoldBlock block = _fit.Block;
            float extent = Mathf.Max(block.Width, Mathf.Max(block.Depth, block.Height));
            float cell = extent / Mathf.Max(resolution, 4) *
                         (draft ? MoldCutterProxy.DraftCellFraction : MoldCutterProxy.FinalCellFraction);

            Proxy cached = draft ? _draftProxy : _finalProxy;
            bool hit = cached != null && cached.Key == _geometryKey && Mathf.Approximately(cached.Cell, cell);

            // Everything the worker reads is either immutable or a private copy - it must not see
            // the session change under it.
            Vector3[] worldCopy = hit ? null : WorldCopy();
            int[] tris = _model.TrianglesExact();
            PartingField field = _fit.Field.Clone();
            PullColumnMap columns = _fit.Columns;
            var features = new List<MoldFeature>(_features.Count);
            foreach (MoldFeature f in _features) features.Add(f.Clone());
            MoldSettings settings = Settings.Clone();
            ulong key = _geometryKey;

            var job = new BuildJob
            {
                Version = _featureVersion,
                Draft = draft,
                Live = live,
                StartedAt = Time.unscaledTime,
                Serial = _sessionSerial,
            };
            job.Task = Task.Run(() =>
            {
                Proxy proxy = cached;
                if (!hit)
                {
                    MoldCutterProxy.Cluster(worldCopy, worldCopy.Length, tris, tris.Length, cell,
                                            out Vector3[] pv, out int[] pt);
                    proxy = new Proxy { Key = key, Cell = cell, Verts = pv, Tris = pt };
                }
                MoldBuilder.Geometry g = MoldBuilder.BuildGeometry(proxy.Verts, proxy.Tris, field, block, columns,
                                                                   features, settings, resolution);
                return (g, proxy);
            });
            _job = job;
            if (!live) Status = $"Building {(draft ? "draft" : "final")} halves at resolution {resolution}...";
        }

        /// Lands a finished build on the main thread - the only part that has to be here,
        /// because it makes Unity Meshes.
        private void PollBuild()
        {
            if (_job == null || !_job.Task.IsCompleted) return;
            BuildJob job = _job;
            _job = null;

            if (job.Task.IsFaulted || job.Task.IsCanceled)
            {
                ReportBuildFailure(job.Task.Exception);
                _builtVersion = job.Version;
            }
            else if (_model != null && job.Serial == _sessionSerial)
            {
                (MoldBuilder.Geometry geometry, Proxy proxy) = job.Task.Result;
                if (proxy != null && proxy.Key == _geometryKey)
                {
                    if (job.Draft) _draftProxy = proxy; else _finalProxy = proxy;
                }
                LandBuild(job, geometry);
            }

            if (_pendingExplicitBuild)
            {
                _pendingExplicitBuild = false;
                RequestBuild(_pendingExplicitDraft, live: false);
            }
        }

        private void ReportBuildFailure(System.AggregateException e)
        {
            System.Exception inner = e != null && e.InnerException != null ? e.InnerException : e;
            Status = "Build failed - " + (inner != null ? inner.Message : "unknown error") + ".";
            if (inner != null) Debug.LogException(inner);
        }

        private void LandBuild(BuildJob job, MoldBuilder.Geometry geometry)
        {
            // Recorded whatever happened, so a build that cannot succeed is not retried on every
            // frame by the live path. A failure the user can act on still reports itself.
            _builtVersion = job.Version;

            MoldBuilder.Result result = MoldBuilder.Finish(geometry);
            if (!result.Success)
            {
                Status = "Nothing built - " + result.Error + ".";
                return;
            }

            // A live rebuild keeps the halves in step with the session; it is not something the
            // user did, so it records no undo step. That matters: an undo triggers a live
            // rebuild, and a rebuild that recorded a step would wipe the redo chain the moment
            // it landed - redo would work for half a second and then stop.
            ApplyHalf(ref _lowerHalf, result.LowerHalf, _model.name + " Mold Lower", record: !job.Live);
            ApplyHalf(ref _upperHalf, result.UpperHalf, _model.name + " Mold Upper", record: !job.Live);
            HalvesAreFinal = !job.Draft && job.Version == _featureVersion;
            HalvesResolution = geometry.Resolution;

            // A live rebuild must not move the user's view out from under them; only a build
            // they pressed for does that.
            if (!job.Live && Settings.ViewMode == MoldViewMode.Model) Settings.ViewMode = MoldViewMode.Halves;
            ApplyViewMode();
            _overlaysDirty = true;
            _analysisDirty = true;
            StateVersion++;

            Status = (job.Live ? "Live: " : job.Draft ? "Draft: " : "Final: ") + string.Join("  |  ", result.Report);
        }

        /// The built halves no longer match the session. Bumping a version rather than setting a
        /// flag so a rebuild that starts before the last edit lands cannot mark itself current.
        ///
        /// The deadline is pushed forward on every call, so a slider being dragged rebuilds once
        /// when it settles rather than once per frame.
        internal void MarkHalvesStale()
        {
            _featureVersion++;
            _nextLiveBuild = Time.unscaledTime + LiveBuildDelay;
            HalvesAreFinal = false;
        }

        /// Kept for callers that only know "something changed": re-fits.
        public void MarkDirty() => Changed(MoldChange.Fit);

        /// Keeps the built halves matching the session, so what is on screen is the mold as it
        /// currently stands rather than as it stood at the last button press.
        ///
        /// Only ever runs once halves already exist: the first build stays an explicit choice.
        /// Never while anything is being dragged, and never while a build is already running -
        /// the running one lands first and this re-checks.
        private void TickLiveHalves()
        {
            if (!Settings.LiveHalves || !HasHalves || _job != null) return;
            if (_builtVersion == _featureVersion) return;
            if (_gizmoDragging || _editInteractive || Time.unscaledTime < _nextLiveBuild) return;

            RequestBuild(draft: true, live: true);
        }

        /// Writes both halves as binary .stl at the session's real-world scale.
        ///
        /// The halves are moved back to the origin for the write and put back afterwards:
        /// Exploded view translates the upper half by ExplodeGap purely to let you see into the
        /// cavity, and baking that display offset into the file would hand the slicer two halves
        /// that do not close. Returns a report for the panel.
        public string ExportStl(string folder)
        {
            if (!HasHalves) return "Build the halves first - there is nothing to export.";
            if (string.IsNullOrEmpty(folder)) return "No folder chosen.";

            string baseName = _model != null ? _model.name : "Mold";
            int written = 0;
            var names = new List<string>(2);

            foreach (var pair in new[]
            {
                (half: _lowerHalf, suffix: " Lower"),
                (half: _upperHalf, suffix: " Upper"),
            })
            {
                if (!IsLiveHalf(pair.half)) continue;

                Vector3 parked = pair.half.transform.position;
                pair.half.transform.position = Vector3.zero;
                try
                {
                    string file = System.IO.Path.Combine(folder, SafeFileName(baseName + pair.suffix) + ".stl");
                    if (StlExporter.Export(pair.half, file, Settings.MillimetresPerUnit) != null)
                    {
                        written++;
                        names.Add(System.IO.Path.GetFileName(file));
                    }
                }
                finally
                {
                    pair.half.transform.position = parked;
                }
            }

            if (written == 0) return "Nothing was written.";

            float mm = Settings.MillimetresPerUnit;
            MoldBlock b = _fit != null && _fit.IsValid ? _fit.Block : default;
            string quality = HalvesAreFinal ? "" : $" Note: these are DRAFT halves (resolution {HalvesResolution}) - press Final for full detail.";
            return $"Exported {string.Join(" and ", names)} at {mm:0.##} mm/unit " +
                   $"({b.Width * mm:0.#} x {b.Depth * mm:0.#} x {b.Height * mm:0.#} mm overall)." + quality;
        }

        /// An object name is whatever the user typed, and a scene allows characters a path does
        /// not. Anything illegal becomes an underscore rather than failing the write.
        private static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Mold";
            var chars = name.ToCharArray();
            char[] illegal = System.IO.Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
                if (System.Array.IndexOf(illegal, chars[i]) >= 0) chars[i] = '_';
            return new string(chars).Trim();
        }

        private static SculptableMesh FindHalf(string name)
        {
            foreach (SculptableMesh mesh in FindObjectsByType<SculptableMesh>(FindObjectsSortMode.None))
                if (mesh != null && mesh.name == name && mesh.gameObject.activeInHierarchy) return mesh;
            return null;
        }

        /// A half that is still in the scene and not parked by an undo.
        private static bool IsLiveHalf(SculptableMesh half) =>
            half != null && !half.Equals(null) && half.gameObject.activeInHierarchy;

        /// Creates the half's object the first time and replaces its mesh every time after.
        ///
        /// Replacing rather than recreating is what makes a rebuild cheap to live with: the
        /// object keeps its name, its place in the object list and its material. An explicit
        /// build goes through SculptableMesh's own snapshot, so a final build over a draft is one
        /// Z press away from the draft; a live rebuild does not (see LandBuild).
        private void ApplyHalf(ref SculptableMesh slot, Mesh mesh, string name, bool record)
        {
            if (IsLiveHalf(slot))
            {
                if (record) slot.SnapshotForUndo();
                slot.ReplaceMesh(mesh);
                slot.name = name;
                return;
            }

            var go = new GameObject(ObjectNaming.Unique(name), typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;

            slot = SculptableMesh.AddOwning(go, mesh);
            go.AddComponent<MirrorController>();
            FindFirstObjectByType<SculptMaterialController>()?.ApplyTo(go.GetComponent<Renderer>());

            SculptableMesh created = slot;
            long bytes = (long)mesh.vertexCount * 12 + (long)mesh.triangles.Length * 4;
            // Parked rather than destroyed on undo, exactly like SelectionManager.DeleteObject -
            // so undoing a build and redoing it does not lose whatever was sculpted on the half
            // in between.
            EditHistory.RecordSceneAction("Build Mold Half",
                undo: () =>
                {
                    if (created == null) return;
                    Selection?.Unregister(created);
                    created.gameObject.SetActive(false);
                },
                redo: () =>
                {
                    if (created == null) return;
                    created.gameObject.SetActive(true);
                },
                discard: () =>
                {
                    if (created != null && !created.gameObject.activeSelf) Destroy(created.gameObject);
                },
                approxBytes: bytes);
        }

        public void ClearHalves()
        {
            if (IsLiveHalf(_lowerHalf)) Selection?.DeleteObject(_lowerHalf);
            if (IsLiveHalf(_upperHalf)) Selection?.DeleteObject(_upperHalf);
            _lowerHalf = null;
            _upperHalf = null;
            HalvesAreFinal = false;
            Status = "Mold halves removed.";
        }

        // ------------------------------------------------------------------------ view mode

        /// Shows and hides the model and the halves, and pulls the upper half apart. Same camera,
        /// same lighting, same materials - which is the point of doing this inside the sculpting
        /// app rather than exporting to look at it.
        public void ApplyViewMode()
        {
            if (Selection == null) return;

            bool showModel = Settings.ViewMode == MoldViewMode.Model || Settings.ViewMode == MoldViewMode.Assembled;
            bool showHalves = Settings.ViewMode != MoldViewMode.Model;

            if (_model != null && !_model.Equals(null) && _model.Visible != showModel)
                Selection.SetVisible(_model, showModel);

            SetHalfVisible(_lowerHalf, showHalves);
            SetHalfVisible(_upperHalf, showHalves);

            float gap = Settings.ViewMode == MoldViewMode.Exploded ? Settings.ExplodeGap : 0f;
            if (IsLiveHalf(_upperHalf)) _upperHalf.transform.position = _frame.Up * gap;
            if (IsLiveHalf(_lowerHalf)) _lowerHalf.transform.position = Vector3.zero;
        }

        private void SetHalfVisible(SculptableMesh half, bool visible)
        {
            if (!IsLiveHalf(half)) return;
            if (half.Visible != visible) Selection.SetVisible(half, visible);
        }

        // ---------------------------------------------------------------------------- input

        private void HandleKeys()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null || IsTypingInUI()) return;

            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (Placement != MoldPlacement.None) Placement = MoldPlacement.None;
                else if (_selected != null) SelectFeature(null);
            }

            if ((kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame) && _selected != null)
                DeleteSelectedFeature();
        }

        /// A focused text field (the model length) owns the keyboard - Backspace there edits the
        /// number, it must not also delete the selected pin.
        private static bool IsTypingInUI()
        {
            EventSystem es = EventSystem.current;
            GameObject focused = es != null ? es.currentSelectedGameObject : null;
            if (focused == null) return false;
            var field = focused.GetComponent<UnityEngine.UI.InputField>();
            return field != null && field.isFocused;
        }

        private static bool MouseHeld()
        {
            Mouse mouse = Mouse.current;
            return mouse != null && mouse.leftButton.isPressed;
        }

        private static bool AltHeld()
        {
            Keyboard kb = Keyboard.current;
            return kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed);
        }

        private void HandleMouse()
        {
            Mouse mouse = Mouse.current;
            Camera cam = ActiveCamera;
            if (mouse == null || cam == null) return;
            if (!mouse.leftButton.wasPressedThisFrame) return;
            // Alt + left drag is the camera's orbit (and Ctrl+Alt its zoom) - the start of an
            // orbit must not also drop a pin or throw away the selection.
            if (AltHeld()) return;
            if (Gizmo != null && Gizmo.IsDragging) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

            Vector2 screen = mouse.position.ReadValue();
            Ray ray = cam.ScreenPointToRay(screen);

            if (Placement != MoldPlacement.None)
            {
                PlaceAt(ray);
                return;
            }

            MoldFeature hit = PickFeature(screen, cam);
            if (hit != null)
            {
                SelectFeature(hit);
                return;
            }

            // A press on one of the gizmo's own handles belongs to the gizmo, whatever order the
            // two components' Updates happen to run in this frame.
            if (Gizmo != null && Gizmo.IsPointerOverHandle(ray)) return;

            // A click on empty space drops the feature selection and hands the gizmo back to the
            // parting surface, rather than leaving handles floating on something the user has
            // visibly stopped working on.
            SelectFeature(null);
        }

        private void PlaceAt(Ray ray)
        {
            if (_fit == null || !_fit.IsValid) return;

            float reach = 4f * Mathf.Max(_fit.Block.Width, Mathf.Max(_fit.Block.Depth, _fit.Block.Height)) + 1000f;
            if (!_fit.Field.RaycastSurface(ray, reach, out Vector3 point))
            {
                Status = "Click on the parting surface to place that.";
                return;
            }

            Vector3 f = _frame.ToFrame(point);
            MoldPlacement what = Placement;
            Edit("Place " + what, MoldChange.Features, () => PlaceResolved(what, f));
            if (what == MoldPlacement.Sprue) Placement = MoldPlacement.None;
        }

        private void PlaceResolved(MoldPlacement what, Vector3 f)
        {
            MoldBlock block = _fit.Block;
            float r = Mathf.Clamp(f.x, block.R0, block.R1);
            float e = Mathf.Clamp(f.z, block.E0, block.E1);

            MoldFeature feature;
            switch (what)
            {
                case MoldPlacement.Pin:
                    feature = new MoldFeature(MoldFeatureKind.Pin, r, e)
                    {
                        RadiusInner = Settings.PinRadius,
                        Height = Settings.PinHeight,
                    };
                    break;
                case MoldPlacement.Sprue:
                    // Only one pour hole makes sense, so placing a second replaces the first
                    // instead of quietly giving the mold two sprues.
                    _features.RemoveAll(x => x.Kind == MoldFeatureKind.Sprue);
                    feature = new MoldFeature(MoldFeatureKind.Sprue, r, e)
                    {
                        AngleDeg = MoldFeature.NearestWallAngle(r, e, block),
                        RadiusInner = Settings.SprueRadiusInner,
                        RadiusOuter = Settings.SprueRadiusOuter,
                        RunnerRadius = Settings.RunnerRadius,
                    };
                    // The click is where the pour should ENTER the model; the funnel goes out in
                    // the padding, halfway to the wall, joined back to the click by the runner.
                    MoldGeometry.SeatSprueHub(feature, _fit.Field, block, _fit.Columns, Settings);
                    break;
                default:
                    feature = new MoldFeature(MoldFeatureKind.Vent, r, e)
                    {
                        AngleDeg = MoldFeature.NearestWallAngle(r, e, block),
                        RadiusInner = Settings.VentRadius,
                        RadiusOuter = Settings.VentRadius,
                    };
                    break;
            }

            feature.Id = _nextFeatureId++;
            _features.Add(feature);
            _selected = feature;
            PushGizmoTargets();
            Status = $"Placed a {what.ToString().ToLowerInvariant()}. Drag the arrows to move it" +
                     (feature.IsChannel ? ", the ring to rotate it" : "") + "; Delete removes it.";
        }

        /// The feature under `screen`, found against each feature's actual body - a pin's peg, a
        /// vent's whole tube, a sprue's runner and funnel - rather than a point at its anchor, so
        /// a click anywhere on what is drawn selects it. Mirror copies select their original.
        private MoldFeature PickFeature(Vector2 screen, Camera cam)
        {
            if (_fit == null || !_fit.IsValid) return null;

            MoldFeature best = null;
            float bestScore = float.MaxValue;
            float bestDepth = float.MaxValue;
            Vector3 camRight = cam.transform.right;

            foreach (MoldFeature f in MoldGeometry.ExpandAll(_features, _fit.Block, Settings))
            {
                _spineScratch.Clear();
                MoldGeometry.Spines(f, _fit.Field, _fit.Block, _fit.Columns, Settings, _spineScratch);
                foreach (MoldGeometry.Spine s in _spineScratch)
                {
                    Vector3 a = cam.WorldToScreenPoint(s.A);
                    Vector3 b = cam.WorldToScreenPoint(s.B);
                    if (a.z <= 0f && b.z <= 0f) continue;

                    float radiusPx = Mathf.Max(
                        ((Vector2)cam.WorldToScreenPoint(s.A + camRight * s.Radius) - (Vector2)a).magnitude,
                        ((Vector2)cam.WorldToScreenPoint(s.B + camRight * s.Radius) - (Vector2)b).magnitude);

                    float distance = DistanceToSegment(screen, a, b, out float t);
                    if (distance > Mathf.Max(radiusPx, FeaturePickPixels)) continue;

                    // Inside the body scores below zero; ties go to whatever is nearer the camera.
                    float score = distance - radiusPx;
                    float depth = Mathf.Lerp(a.z, b.z, t);
                    if (score < bestScore - 0.5f || (score < bestScore + 0.5f && depth < bestDepth))
                    {
                        bestScore = score;
                        bestDepth = depth;
                        best = f.IsMirror ? f.Source : f;
                    }
                }
            }
            return best;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            t = len2 > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        // ------------------------------------------------------------------- gizmo plumbing

        private void PushGizmoTargets()
        {
            if (Gizmo == null || _fit == null || !_fit.IsValid) return;

            _gizmoTargets.Clear();
            GizmoHandleSet handles;
            if (_selected != null)
            {
                _gizmoTargets.Add(new MoldFeatureTarget(this, _selected));
                // Channels also rotate - about the pull axis only, see MoldFeatureTarget. A pin
                // is rotationally symmetric, so it gets arrows alone.
                handles = _selected.IsChannel ? GizmoHandleSet.Transpose : GizmoHandleSet.Move;
            }
            else
            {
                _gizmoTargets.Add(new PartingSurfaceTarget(this));
                // Move AND rotate: dragging slides the whole sheet along the pull axis, rotating
                // swings the pull direction itself and re-fits continuously as it goes.
                handles = GizmoHandleSet.Transpose;
            }

            Gizmo.SetExternalTargets(this, _gizmoTargets, handles);
        }

        // The gizmo must not record a scene transform step for these drags: nothing here is a
        // Transform, and every drag is recorded as a mold step - see OnGizmoDragStarted.
        public bool RecordsOwnUndoStep => true;

        public void OnGizmoDragStarted()
        {
            _gizmoDragging = true;
            bool rotating = Gizmo != null && Gizmo.IsRotating;
            string label = _selected != null
                ? (rotating ? "Rotate " : "Move ") + FeatureLabel(_selected)
                : rotating ? "Rotate Pull Direction" : "Move Parting Surface";
            // One step for the whole drag, captured before anything has moved.
            BeginEdit(label, interactive: true);
        }

        public void OnGizmoDragEnded(bool changed)
        {
            _gizmoDragging = false;
            // Full-resolution fit on release (if the drag re-aimed the pull), then one step.
            EndEdit();
        }

        /// A click that lands on a feature, or anywhere while something is armed for placement,
        /// belongs to this tool even if a gizmo handle happens to be under the cursor - except a
        /// click on the SELECTED feature, whose own handles sit on top of it and have to stay
        /// grabbable.
        public bool ClaimsPointer(Ray ray)
        {
            if (AltHeld()) return false;
            if (Placement != MoldPlacement.None) return true;

            Camera cam = ActiveCamera;
            Mouse mouse = Mouse.current;
            if (cam == null || mouse == null) return false;
            MoldFeature hit = PickFeature(mouse.position.ReadValue(), cam);
            return hit != null && hit != _selected;
        }

        // ---------------------------------------------------------------- internal plumbing

        /// Slides the whole parting surface along the pull axis. Called by PartingSurfaceTarget.
        ///
        /// Applied to the field IMMEDIATELY, not on the next throttled recompute. The gizmo sets
        /// positions absolutely (drag start + delta) and this turns them into a delta against
        /// the CURRENT position - so while the old throttled path left the position stale
        /// between recomputes, the same delta was added again every frame and the sheet overshot
        /// the cursor several times over.
        internal void NudgeSurface(float deltaAlongUp)
        {
            if (Mathf.Abs(deltaAlongUp) < 1e-9f) return;
            _manualOffset += deltaAlongUp;
            ApplyOffsetToFit();
            _overlaysDirty = true;
            _analysisDirty = true;
            MarkHalvesStale();
        }

        /// Points the pull direction somewhere new. Switches the mode to Manual so a later
        /// recompute does not snap it back to whatever axis the settings name.
        internal void SetFrameFromRotation(Quaternion rotation)
        {
            _frame = MoldFrame.FromRightUp(rotation * Vector3.right, rotation * Vector3.up);
            Settings.PullMode = PullMode.Manual;
            _needsAxisSearch = false;
            _fitDirty = true;
            MarkHalvesStale();
        }

        /// A feature was moved or turned from the gizmo.
        internal void FeatureMoved() => RefreshFeatureOverlay();

        internal bool GizmoIsRotating => Gizmo != null && Gizmo.IsRotating;

        internal MoldFit CurrentFit => _fit;

        private static Bounds WorldBounds(SculptableMesh mesh)
        {
            var renderer = mesh.GetComponent<Renderer>();
            if (renderer != null) return renderer.bounds;
            var b = new Bounds(mesh.transform.position, Vector3.one);
            return b;
        }

    }

    /// The size controls the panel exposes, for SetSizeMm / SetSelectedMm.
    public enum MoldSize
    {
        SprueHub,
        SprueFunnel,
        Runner,
        Vent,
        PinRadius,
        PinHeight,
    }

    /// The gizmo target for the parting surface itself.
    ///
    /// Only the component of a move along the pull axis means anything - the sheet spans the
    /// whole block footprint, so sliding it sideways would do nothing - so only that arrow is
    /// shown. A rotation re-aims the pull direction, which is the live version of the addon's
    /// axis search.
    internal sealed class PartingSurfaceTarget : GizmoTarget
    {
        private readonly MoldController _controller;

        public PartingSurfaceTarget(MoldController controller) => _controller = controller;

        public override bool IsAlive => _controller != null && _controller.Fit != null && _controller.Fit.IsValid;

        public override Vector3 Position
        {
            get
            {
                MoldFit fit = _controller.Fit;
                if (fit == null || !fit.IsValid) return Vector3.zero;
                Vector3 c = fit.Block.Centre;
                return fit.Frame.ToWorld(new Vector3(c.x, fit.Field.Sample(c.x, c.z), c.z));
            }
            set
            {
                // A rotate drag writes the pivot back into Position every frame; that is not a
                // request to slide the sheet, and treating it as one moved the surface up and
                // down while the pull direction was being turned.
                if (_controller.GizmoIsRotating) return;
                MoldFit fit = _controller.Fit;
                if (fit == null || !fit.IsValid) return;
                Vector3 delta = value - Position;
                _controller.NudgeSurface(Vector3.Dot(delta, _controller.Frame.Up));
            }
        }

        public override Quaternion Rotation
        {
            get => Quaternion.LookRotation(_controller.Frame.Eye, _controller.Frame.Up);
            set => _controller.SetFrameFromRotation(value);
        }

        public override bool SupportsRotation => true;
        public override bool SupportsScale => false;

        // Local Y of LookRotation(Eye, Up) is Up - the only direction a slide means anything.
        public override int MoveAxisMask => 0b010;

        // The gizmo draws its arms at 1.8x this radius. A tenth of the block keeps the arms clear
        // of the centre without engulfing the parting surface they are meant to steer.
        private const float BlockFractionForRadius = 0.1f;

        public override float WorldRadius
        {
            get
            {
                MoldFit fit = _controller.Fit;
                if (fit == null || !fit.IsValid) return 1f;
                MoldBlock b = fit.Block;
                return BlockFractionForRadius * Mathf.Max(b.Width, Mathf.Max(b.Depth, b.Height));
            }
        }

        public override bool SameAs(GizmoTarget other) =>
            other is PartingSurfaceTarget t && t._controller == _controller;
    }

    /// The gizmo target for one placed pin, sprue or vent.
    ///
    /// The handles are laid out in the feature's own frame, which is what makes them mean
    /// something: a channel's arrows run along it and across it, and its one ring turns it
    /// about the pull axis. Only the handles that DO something are shown -
    /// - Pin: slide it across the sheet; its up arrow sets its HEIGHT, the only reading of "up"
    ///   that makes sense for something that has to stand on the surface.
    /// - Vent: slide it sideways (its gate follows the model's outline) and turn it. Along its
    ///   own length it would only slide along itself - the gate is found, not stored - so that
    ///   arrow is hidden rather than left doing nothing.
    /// - Sprue: the handle sits on the HUB. Slide it sideways, slide it along its length to
    ///   stand the funnel further off the model or closer to it, and turn it.
    internal sealed class MoldFeatureTarget : GizmoTarget
    {
        private readonly MoldController _controller;
        private readonly MoldFeature _feature;

        public MoldFeatureTarget(MoldController controller, MoldFeature feature)
        {
            _controller = controller;
            _feature = feature;
        }

        public override bool IsAlive =>
            _controller != null && _feature != null && _controller.Fit != null && _controller.Fit.IsValid;

        public override Vector3 Position
        {
            get
            {
                MoldFit fit = _controller.Fit;
                if (fit == null || !fit.IsValid) return Vector3.zero;
                switch (_feature.Kind)
                {
                    case MoldFeatureKind.Pin:
                        // At the pin's TIP, where the user can see it, rather than buried in the
                        // sheet at its base.
                        return _feature.WorldAnchor(fit.Field) + fit.Frame.Up * _feature.Height;
                    case MoldFeatureKind.Vent:
                    {
                        MoldGeometry.ChannelLayout c = _controller.LayoutOf(_feature);
                        return MoldGeometry.OnSheet(fit.Field, c.Connected ? c.Gate : new Vector2(_feature.R, _feature.E));
                    }
                    default:
                        return MoldGeometry.OnSheet(fit.Field, _controller.LayoutOf(_feature).Hub);
                }
            }
            set
            {
                // A rotate drag writes the pivot back here every frame; the pivot IS this
                // position, so taking it would only snap the anchor onto it - skip it rather than
                // risk moving a feature while it is being turned.
                if (_controller.GizmoIsRotating) return;
                MoldFit fit = _controller.Fit;
                if (fit == null || !fit.IsValid) return;

                Vector3 f = fit.Frame.ToFrame(value);
                _feature.R = Mathf.Clamp(f.x, fit.Block.R0, fit.Block.R1);
                _feature.E = Mathf.Clamp(f.z, fit.Block.E0, fit.Block.E1);

                if (_feature.Kind == MoldFeatureKind.Pin)
                {
                    float surface = fit.Field.Sample(_feature.R, _feature.E);
                    float room = Mathf.Min(surface - fit.Block.UBottom, fit.Block.UTop - surface);
                    _feature.Height = Mathf.Clamp(f.y - surface, 0.01f * Mathf.Max(room, 1e-4f), 0.6f * Mathf.Max(room, 1e-4f));
                }

                _controller.FeatureMoved();
            }
        }

        public override Quaternion Rotation
        {
            get
            {
                MoldFrame frame = _controller.Frame;
                Vector3 forward = _feature.IsChannel ? _feature.WorldDirection(frame) : frame.Eye;
                return Quaternion.LookRotation(forward, frame.Up);
            }
            set
            {
                if (!_feature.IsChannel) return;
                MoldFrame frame = _controller.Frame;
                // Whatever the ring did, only the part of the new heading that lies IN the
                // parting plane means anything: a channel has to stay on the sheet.
                Vector3 forward = value * Vector3.forward;
                float r = Vector3.Dot(forward, frame.Right), e = Vector3.Dot(forward, frame.Eye);
                if (r * r + e * e < 1e-8f) return;
                _feature.AngleDeg = MoldFeature.WrapAngle(Mathf.Atan2(e, r) * Mathf.Rad2Deg);
                _controller.FeatureMoved();
            }
        }

        public override bool SupportsRotation => _feature.IsChannel;
        public override bool SupportsScale => false;

        public override int MoveAxisMask
        {
            get
            {
                switch (_feature.Kind)
                {
                    case MoldFeatureKind.Pin: return 0b111;
                    case MoldFeatureKind.Vent: return 0b001;
                    default: return 0b101;
                }
            }
        }

        // Local Y is the pull axis - the only axis a channel on the sheet can turn about.
        public override int RotateAxisMask => _feature.IsChannel ? 0b010 : 0;

        public override float WorldRadius
        {
            get
            {
                MoldFit fit = _controller.Fit;
                float block = fit != null && fit.IsValid
                    ? Mathf.Max(fit.Block.Width, Mathf.Max(fit.Block.Depth, fit.Block.Height))
                    : 1f;
                // Held between a thirtieth and a twelfth of the block: a 1mm vent's own radius would
                // give arms too small to grab, and an 18mm funnel's would give arms half the lure long.
                float own = 2f * Mathf.Max(_feature.RadiusInner, _feature.RadiusOuter);
                return Mathf.Clamp(own, 0.033f * block, 0.083f * block);
            }
        }

        public override bool SameAs(GizmoTarget other) =>
            other is MoldFeatureTarget t && t._feature == _feature;
    }
}
