using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// The lathe tool: shape a 2D profile in the viewport and see it revolved into a solid live,
    /// like pulling clay up on a wheel, then Create it as an ordinary sculptable object.
    ///
    /// While GizmoMode.Lathe is up this owns the mouse. The profile is edited ON the preview's own
    /// silhouette: the handles sit in the plane through the axis that faces the camera, on both
    /// sides, so dragging a handle drags the outline you are looking at. The view can still orbit
    /// (Alt-drag) - the handles follow round, because a solid of revolution has the same silhouette
    /// from every side.
    ///
    /// Split across partial files:
    ///   LatheController.cs         state, lifecycle, preview mesh, overlay, operations, undo, Create
    ///   LatheController.Input.cs   picking and every mouse/keyboard gesture
    ///
    /// Like the ZSphere rig, the profile is a scaffold rather than a scene object: it is not in
    /// SelectionManager or save files, and it keeps its own undo history. Create is where the work
    /// becomes real geometry, and Create is one step on the scene's EditHistory.
    public partial class LatheController : MonoBehaviour
    {
        public const float MinScale = 0.1f;
        public const float MaxScale = 5f;

        private const int MaxUndoSteps = 128;
        private const float CoalesceSeconds = 0.4f;

        /// A rebuild this slow while dragging switches the drag to half the radial detail, so the
        /// handle keeps up with the cursor. The full mesh is rebuilt the moment the drag ends.
        private const double DraftThresholdMs = 12.0;

        // ------------------------------------------------------------------------ install

        private static LatheController _instance;

        /// Finds the scene's lathe controller, adding one beside the transform gizmo if the scene
        /// predates the feature - the same self-install the turntable and mold maker use, so the
        /// scene file does not have to change.
        public static LatheController Install()
        {
            if (_instance != null) return _instance;
            _instance = FindFirstObjectByType<LatheController>();
            if (_instance != null) return _instance;

            var gizmo = FindFirstObjectByType<TransformGizmo>();
            GameObject host = gizmo != null ? gizmo.gameObject : new GameObject("Lathe Tool");
            _instance = host.AddComponent<LatheController>();
            return _instance;
        }

        // -------------------------------------------------------------------------- state

        private readonly LatheProfile _profile = new LatheProfile();
        public LatheProfile Profile => _profile;

        private LatheSettings _settings = LatheSettings.Default;
        private bool _settingsDirty = true;

        /// Vertices around the widest ring - the level of detail.
        public int RadialSegments
        {
            get => _settings.RadialSegments;
            set
            {
                int v = LatheMeshBuilder.RoundSegments(value);
                if (v == _settings.RadialSegments) return;
                _settings.RadialSegments = v;
                _settingsDirty = true;
            }
        }

        public bool CapEnds
        {
            get => _settings.CapEnds;
            set
            {
                if (value == _settings.CapEnds) return;
                _settings.CapEnds = value;
                _settingsDirty = true;
            }
        }

        public bool EvenTriangles
        {
            get => _settings.EvenTriangles;
            set
            {
                if (value == _settings.EvenTriangles) return;
                _settings.EvenTriangles = value;
                _settingsDirty = true;
            }
        }

        /// The profile closes on itself - a ring or torus. Part of the shape, so undoable.
        public bool ClosedLoop
        {
            get => _profile.ClosedLoop;
            set
            {
                if (value == _profile.ClosedLoop) return;
                if (value && _profile.Count < 3)
                {
                    SetStatus("A closed loop needs at least 3 points.");
                    return;
                }
                EndDrag();
                BeginEdit(value ? "Close Loop" : "Open Loop");
                _profile.ClosedLoop = value;
                SelectedPoint = -1;
                CommitEdit();
            }
        }

        /// Uniform size of the whole shape, applied to the lathe's own transform.
        public float Scale
        {
            get => _root != null ? _root.localScale.x : 1f;
            set
            {
                EnsureRoot();
                float v = Mathf.Clamp(value, MinScale, MaxScale);
                if (Mathf.Approximately(v, _root.localScale.x)) return;
                BeginEdit("Scale", CoalesceSeconds);
                _root.localScale = Vector3.one * v;
                _transformVersion++;
            }
        }

        /// World-space radius of the widest point. Setting it stretches the profile away from (or
        /// toward) the axis, keeping its shape.
        public float Radius
        {
            get => _profile.MaxRadius * Scale;
            set
            {
                float current = Radius;
                if (current < 1e-5f || value <= 0f || Mathf.Approximately(value, current)) return;
                BeginEdit("Radius", CoalesceSeconds);
                _profile.ScaleRadius(value / current);
            }
        }

        /// World-space height. Setting it stretches the profile along the axis about its base.
        public float Height
        {
            get => _profile.Height * Scale;
            set
            {
                float current = Height;
                if (current < 1e-5f || value <= 0f || Mathf.Approximately(value, current)) return;
                BeginEdit("Height", CoalesceSeconds);
                _profile.ScaleHeight(value / current);
            }
        }

        public int SelectedPoint { get; private set; } = -1;

        public bool SelectedIsSharp =>
            SelectedPoint >= 0 && SelectedPoint < _profile.Count && _profile[SelectedPoint].Sharp;

        /// What the last build produced - the panel's triangle count and warnings.
        public LatheBuildResult LastBuild => _build;
        public bool HasMesh => _hasMesh;
        public bool IsDraft => _builtDraft;

        /// A one-off message from the last operation, for the panel. Cleared by the panel.
        public string Status { get; private set; }
        public int StatusVersion { get; private set; }

        private void SetStatus(string message)
        {
            Status = message;
            StatusVersion++;
        }

        public bool IsActive => Gizmo != null && Gizmo.Mode == GizmoMode.Lathe;

        // ---------------------------------------------------------------------- scene refs

        private Camera _cam;

        private SelectionManager _selection;
        private SelectionManager Selection =>
            _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());

        private TransformGizmo _gizmo;
        private TransformGizmo Gizmo =>
            _gizmo != null ? _gizmo : (_gizmo = FindFirstObjectByType<TransformGizmo>());

        private CameraOrbitController _orbit;
        private CameraOrbitController Orbit =>
            _orbit != null ? _orbit : (_orbit = FindFirstObjectByType<CameraOrbitController>());

        // ---------------------------------------------------------------- preview objects

        private Transform _root;
        private MeshRenderer _previewRenderer;
        private Mesh _previewMesh;
        private Material _fallbackMaterial;
        private bool _placed;

        private LatheOverlayGraphic _overlay;
        private GameObject _overlayCanvas;

        private readonly LatheBuildResult _build = new LatheBuildResult();
        private bool _hasMesh;
        private bool _builtDraft;
        private int _builtVersion = -1;
        private int _transformVersion;
        private double _lastBuildMs;

        private bool _wasActive;

        // ---------------------------------------------------------------------- lifecycle

        private void Awake()
        {
            _instance = this;
            _cam = Camera.main;
        }

        private void Update()
        {
            if (_cam == null) _cam = Camera.main;
            TickPendingEdit();

            bool active = IsActive && _cam != null;
            if (active != _wasActive)
            {
                _wasActive = active;
                if (active) OnActivated();
                else
                {
                    EndDrag();
                    SetPreviewVisible(false);
                }
            }

            if (!active) return;
            HandleInput();
            RefreshMesh();
        }

        // After every Update, so the camera has already moved this frame and the handles never
        // trail the view by a frame while orbiting.
        private void LateUpdate()
        {
            bool show = _wasActive && !TurntableController.CleanViewActive;
            SetOverlayVisible(show);
            if (show) DrawOverlay();
        }

        private void OnDisable()
        {
            // Committed rather than dropped - see ZSphereController.OnDisable.
            CommitEdit();
            SetOverlayVisible(false);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_previewMesh != null) Destroy(_previewMesh);
            if (_fallbackMaterial != null) Destroy(_fallbackMaterial);
            if (_root != null) Destroy(_root.gameObject);
            if (_overlayCanvas != null) Destroy(_overlayCanvas);
        }

        private void OnActivated()
        {
            EnsureRoot();
            // A first visit gets a starting shape; an empty profile the artist cleared on purpose is left
            // empty for them to draw into.
            if (!_placed || (_profile.Count == 0 && _undo.Count == 0)) StartNewShape(LathePreset.Vase);
            ApplyPreviewMaterial();
            SetPreviewVisible(true);
            _builtVersion = -1;
        }

        private void EnsureRoot()
        {
            if (_root != null) return;

            // DontSave: a scaffold, not scene content - it must never end up in the scene file,
            // and FindObjectsByType skipping it keeps it out of every "all objects" sweep.
            var root = new GameObject("Lathe") { hideFlags = HideFlags.DontSave };
            _root = root.transform;

            var preview = new GameObject("Lathe Preview", typeof(MeshFilter), typeof(MeshRenderer))
            {
                hideFlags = HideFlags.DontSave
            };
            preview.transform.SetParent(_root, false);
            _previewMesh = new Mesh { name = "Lathe Preview" };
            _previewMesh.MarkDynamic();
            preview.GetComponent<MeshFilter>().sharedMesh = _previewMesh;
            _previewRenderer = preview.GetComponent<MeshRenderer>();
            root.SetActive(false);
        }

        private void SetPreviewVisible(bool visible)
        {
            if (_root != null && _root.gameObject.activeSelf != visible) _root.gameObject.SetActive(visible);
        }

        /// The sculpt material, so the preview looks exactly like what Create will produce - under
        /// the same matcap or lighting preset. Re-applied on every activation because the material
        /// can be swapped while the tool is put away.
        private void ApplyPreviewMaterial()
        {
            var materials = FindFirstObjectByType<SculptMaterialController>();
            if (materials != null) materials.ApplyTo(_previewRenderer);
            if (_previewRenderer.sharedMaterial != null) return;

            if (_fallbackMaterial == null)
                _fallbackMaterial = ZSphereArmatureView.CreateLit("Lathe Preview", new Color(0.72f, 0.72f, 0.74f), 0.35f);
            _previewRenderer.sharedMaterial = _fallbackMaterial;
        }

        // -------------------------------------------------------------------- the mesh

        /// Rebuilds the preview when the profile, the detail settings or the scale changed. While a
        /// handle is being dragged and a full rebuild has been slow, builds at half the radial
        /// detail instead - responsiveness first, then the real thing on release.
        private void RefreshMesh()
        {
            bool dragging = _drag != DragKind.None;
            bool wantDraft = dragging && _lastBuildMs > DraftThresholdMs;
            bool stale = _profile.Version != _builtVersion || _settingsDirty || (_builtDraft && !wantDraft);
            if (!stale) return;

            var watch = Stopwatch.StartNew();
            LatheSettings settings = _settings;
            if (wantDraft) settings.RadialSegments = LatheMeshBuilder.RoundSegments(settings.RadialSegments / 2);

            _hasMesh = LatheMeshBuilder.Build(_profile, settings, _build);
            if (_hasMesh) LatheMeshBuilder.ToMesh(_build, _previewMesh);
            else _previewMesh.Clear();

            watch.Stop();
            if (!wantDraft) _lastBuildMs = watch.Elapsed.TotalMilliseconds;

            _builtVersion = _profile.Version;
            _builtDraft = wantDraft;
            _settingsDirty = false;
        }

        // ------------------------------------------------------------------- placement

        /// Replaces the profile with a preset, scaled to the scene, and - the first time - seats
        /// the lathe beside whatever is already in the scene with the view centred on it. Beside,
        /// not at the view centre: the scene's starting sphere sits there, and a vase built inside
        /// it would be invisible.
        public void StartNewShape(LathePreset preset)
        {
            EnsureRoot();
            if (!_placed) PlaceBesideScene();
            ApplyPreset(preset);
        }

        public void ApplyPreset(LathePreset preset)
        {
            EnsureRoot();
            EndDrag();
            BeginEdit("Preset: " + preset);
            List<LatheProfile.Point> points = LathePresets.Build(preset, DefaultHeight() / Scale, out bool loop);
            _profile.SetPoints(points, loop);
            SelectedPoint = -1;
            CommitEdit();
            SetStatus(loop
                ? $"{preset}: a closed loop, revolved into a ring."
                : $"{preset}: drag the handles to reshape it.");
        }

        /// Empties the profile so the next clicks draw a new one from scratch. Undoable.
        public void ClearProfile()
        {
            EndDrag();
            BeginEdit("Clear Profile");
            _profile.Clear();
            SelectedPoint = -1;
            CommitEdit();
            SetStatus("Cleared. Click beside the dashed axis to place points bottom to top, or Ctrl-drag to sketch.");
        }

        /// Moves the axis to the centre of the current view, keeping the shape.
        public void MoveAxisToViewCentre()
        {
            EnsureRoot();
            CameraOrbitController orbit = Orbit;
            if (orbit == null) return;
            orbit.GetView(out _, out _, out _, out Vector3 pivot);
            BeginEdit("Move Axis");
            float half = _profile.Count > 0 ? (_profile.MinHeight + _profile.MaxHeight) * 0.5f * Scale : 0f;
            _root.position = pivot - Vector3.up * half;
            _transformVersion++;
            CommitEdit();
            SetStatus("Axis moved to the centre of the view.");
        }

        private void PlaceBesideScene()
        {
            _placed = true;
            _root.rotation = Quaternion.identity;
            _root.localScale = Vector3.one;

            float height = DefaultHeight();
            Vector3 pivot = Vector3.zero;
            float yaw = 0f, pitch = 15f, distance = height * 3f;
            CameraOrbitController orbit = Orbit;
            if (orbit != null) orbit.GetView(out yaw, out pitch, out distance, out pivot);

            Vector3 centre;
            if (TryVisibleSceneBounds(out Bounds scene))
            {
                Vector3 right = _cam != null ? _cam.transform.right : Vector3.right;
                right.y = 0f;
                right = right.sqrMagnitude > 1e-6f ? right.normalized : Vector3.right;
                // The scene's half-width along `right`, then clear it by the lathe's own radius
                // (presets are at most ~0.7 x height wide) and a gap.
                float sceneHalf = Mathf.Abs(scene.extents.x * right.x) + Mathf.Abs(scene.extents.z * right.z);
                centre = scene.center + right * (sceneHalf + height * 0.75f);
                centre.y = scene.center.y;
            }
            else
            {
                centre = pivot;
            }

            _root.position = centre - Vector3.up * (height * 0.5f);

            // Centre the view on the new shape, backing off only if it would not fit.
            if (orbit != null) orbit.SetView(yaw, pitch, Mathf.Max(distance, height * 2.6f), centre);
        }

        /// Sized off what is already in the scene, so the first shape is neither a speck nor a
        /// giant next to the model - the same reference PrimitiveSpawner uses.
        private float DefaultHeight()
        {
            var spawner = FindFirstObjectByType<PrimitiveSpawner>();
            SculptableMesh main = spawner != null ? spawner.MainObject : null;
            if (main == null || main.Mesh == null) return 1f;
            Vector3 e = Vector3.Scale(main.Mesh.bounds.extents, main.transform.lossyScale);
            return Mathf.Clamp((e.x + e.y + e.z) / 3f * 2f, 0.05f, 20f);
        }

        private bool TryVisibleSceneBounds(out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            SelectionManager selection = Selection;
            if (selection == null) return false;
            foreach (SculptableMesh obj in selection.AllObjects)
            {
                if (obj == null || !obj.Visible) continue;
                var renderer = obj.GetComponent<Renderer>();
                if (renderer == null) continue;
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            return any;
        }

        // ------------------------------------------------------------------- point edits

        public void DeletePoint(int index)
        {
            if (index < 0 || index >= _profile.Count) return;
            EndDrag();
            BeginEdit("Delete Point");
            _profile.RemoveAt(index);
            if (_profile.ClosedLoop && _profile.Count < 3) _profile.ClosedLoop = false;
            SelectedPoint = -1;
            CommitEdit();
        }

        public void DeleteSelectedPoint()
        {
            if (SelectedPoint < 0) { SetStatus("Click a handle to select a point first."); return; }
            DeletePoint(SelectedPoint);
        }

        /// Corner vs smooth. A corner is how a flat base meets a wall, or a lid meets a rim.
        public void ToggleSharp(int index)
        {
            if (index < 0 || index >= _profile.Count) return;
            BeginEdit("Toggle Corner");
            bool sharp = !_profile[index].Sharp;
            _profile.SetSharp(index, sharp);
            CommitEdit();
            SetStatus(sharp ? "Corner point - the curve turns sharply here." : "Smooth point.");
        }

        public void ToggleSelectedSharp()
        {
            if (SelectedPoint < 0) { SetStatus("Click a handle to select a point first."); return; }
            ToggleSharp(SelectedPoint);
        }

        // ------------------------------------------------------------------------- create

        /// Bakes the current shape into a real, independent SculptableMesh at full detail - the same
        /// "brand new object" contract ZSphere Convert, Clone and Mirror use, so the result is
        /// immediately sculptable, maskable, joinable, savable and exportable. The profile is kept,
        /// so the next Create can be a variation. Null (with Status set) when there is nothing to
        /// build.
        public SculptableMesh CreateMesh()
        {
            EndDrag();
            CommitEdit();
            EnsureRoot();

            var result = new LatheBuildResult();
            if (!LatheMeshBuilder.Build(_profile, _settings, result))
            {
                SetStatus(result.Error);
                return null;
            }

            // Scale baked into the vertices, so the new object has a unit transform (what OBJ/STL
            // export and the gizmo expect), and re-origined to its vertical centre on the axis, so
            // its pivot - and its mirror plane - sit in the middle of it. Multiplying both halves of
            // a mirror pair by the same factor keeps them exact negations of each other.
            float scale = Scale;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < result.Vertices.Count; i++)
            {
                float y = result.Vertices[i].y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            float centreY = (minY + maxY) * 0.5f;
            for (int i = 0; i < result.Vertices.Count; i++)
            {
                Vector3 v = result.Vertices[i];
                result.Vertices[i] = new Vector3(v.x * scale, (v.y - centreY) * scale, v.z * scale);
            }

            Mesh mesh = LatheMeshBuilder.ToMesh(result);
            var go = new GameObject(ObjectNaming.Unique("Lathe"), typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetPositionAndRotation(_root.TransformPoint(new Vector3(0f, centreY, 0f)), Quaternion.identity);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;

            SculptableMesh sculptable = SculptableMesh.AddOwning(go, mesh);
            go.AddComponent<MirrorController>();
            FindFirstObjectByType<SculptMaterialController>()?.ApplyTo(go.GetComponent<Renderer>());

            Selection?.Select(sculptable, false);
            // Straight into sculpting what was just made.
            Gizmo?.SetMode(GizmoMode.Sculpt);

            RecordCreateUndo(sculptable);

            string closure = result.Watertight ? "closed solid" : "open surface";
            SetStatus($"Created {go.name} ({result.TriangleCount:N0} tris, {closure}). The profile is kept for another.");
            return sculptable;
        }

        /// One undo press takes the created object away and puts you back on the lathe - the same
        /// park-don't-destroy contract as ZSphereController.RecordConvertUndo, so a redo finds the
        /// object, and anything sculpted on it since, intact.
        private void RecordCreateUndo(SculptableMesh created)
        {
            Mesh createdMesh = created.Mesh;
            long bytes = createdMesh != null ? (long)createdMesh.vertexCount * 12 + (long)createdMesh.triangles.Length * 4 : 0;

            EditHistory.RecordSceneAction("Create Lathe",
                undo: () =>
                {
                    if (created != null)
                    {
                        // SculptableMesh registers in OnEnable but only unregisters in OnDestroy,
                        // so deactivating alone would leave a ghost row in the scene list.
                        Selection?.Unregister(created);
                        created.gameObject.SetActive(false);
                    }
                    Gizmo?.SetMode(GizmoMode.Lathe);
                },
                redo: () =>
                {
                    if (created != null)
                    {
                        created.gameObject.SetActive(true);
                        Selection?.Select(created, false);
                    }
                    Gizmo?.SetMode(GizmoMode.Sculpt);
                },
                discard: () =>
                {
                    if (created != null && !created.gameObject.activeSelf) Destroy(created.gameObject);
                },
                approxBytes: bytes);
        }

        // ---------------------------------------------------------------- profile undo

        /// The profile keeps its own undo stack, like the ZSphere rig and for the same reason: forty
        /// handle drags on the scene's history would bury the mesh edits either side of them. Z
        /// answers here while the tool is up and has something to undo, and falls through to the
        /// scene's history otherwise (see HandlesUndoKey), which is how Z right after Create
        /// un-creates.
        private struct UndoStep
        {
            public LatheProfile.Point[] Points;
            public bool Loop;
            public float Scale;
            public Vector3 RootPosition;
            public string Label;
        }

        private readonly List<UndoStep> _undo = new List<UndoStep>();
        private readonly List<UndoStep> _redo = new List<UndoStep>();

        private bool _pendingOpen;
        private UndoStep _pending;
        private int _pendingVersion;
        private int _pendingTransformVersion;
        private float _pendingCommitAt = float.MaxValue;

        private bool HasOpenEdit =>
            _pendingOpen && (_profile.Version != _pendingVersion || _transformVersion != _pendingTransformVersion);

        public bool CanUndo => _undo.Count > 0 || HasOpenEdit;
        public bool CanRedo => _redo.Count > 0;

        public string NextUndoLabel =>
            HasOpenEdit ? _pending.Label : _undo.Count > 0 ? _undo[_undo.Count - 1].Label : null;

        /// Whether the lathe, not the scene-wide history, should answer a Z press. Asked by
        /// SculptController.HandleUndoRedoKeys, so exactly one of the two responds.
        public bool HandlesUndoKey(bool redo)
        {
            if (!IsActive) return false;
            bool can = redo ? CanRedo : CanUndo;
            if (!can) return false;
            if (redo) Redo(); else Undo();
            return true;
        }

        private UndoStep Capture(string label) => new UndoStep
        {
            Points = _profile.Snapshot(),
            Loop = _profile.ClosedLoop,
            Scale = _root != null ? _root.localScale.x : 1f,
            RootPosition = _root != null ? _root.position : Vector3.zero,
            Label = label
        };

        private void Apply(UndoStep step)
        {
            _profile.SetPoints(step.Points, step.Loop);
            if (_root != null)
            {
                _root.localScale = Vector3.one * step.Scale;
                _root.position = step.RootPosition;
            }
            _transformVersion++;
            if (SelectedPoint >= _profile.Count) SelectedPoint = -1;
        }

        /// Opens an undo step - snapshots now, pushed by CommitEdit only if something actually
        /// changed in between (lazy, so it is safe to open on every press). A streamed edit (a
        /// slider) stays open for CoalesceSeconds after its last change and closes as one step; a
        /// discrete edit that arrives inside that window commits it first rather than joining it,
        /// so Esc on a drag can never also revert the slider move before it.
        private void BeginEdit(string label, float coalesceSeconds = 0f)
        {
            if (_pendingOpen)
            {
                bool openIsStreamed = _pendingCommitAt != float.MaxValue;
                bool sameStream = openIsStreamed && coalesceSeconds > 0f && _pending.Label == label;
                if (sameStream || (!openIsStreamed && coalesceSeconds <= 0f))
                {
                    if (coalesceSeconds > 0f) _pendingCommitAt = Time.unscaledTime + coalesceSeconds;
                    return;
                }
                CommitEdit();
            }

            _pendingOpen = true;
            _pending = Capture(label);
            _pendingVersion = _profile.Version;
            _pendingTransformVersion = _transformVersion;
            _pendingCommitAt = coalesceSeconds > 0f ? Time.unscaledTime + coalesceSeconds : float.MaxValue;
        }

        private void CommitEdit()
        {
            if (!_pendingOpen) return;
            if (HasOpenEdit)
            {
                _undo.Add(_pending);
                while (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
                _redo.Clear();
            }
            _pendingOpen = false;
            _pendingCommitAt = float.MaxValue;
        }

        /// Throws the open step away AND puts the profile back - Esc mid-drag.
        private void CancelEdit()
        {
            if (!_pendingOpen) return;
            if (HasOpenEdit) Apply(_pending);
            _pendingOpen = false;
            _pendingCommitAt = float.MaxValue;
        }

        private void TickPendingEdit()
        {
            if (!_pendingOpen || _drag != DragKind.None) return;
            if (Time.unscaledTime >= _pendingCommitAt) CommitEdit();
        }

        public bool Undo()
        {
            EndDrag();
            CommitEdit();
            return Step(_undo, _redo);
        }

        public bool Redo()
        {
            EndDrag();
            CommitEdit();
            return Step(_redo, _undo);
        }

        private bool Step(List<UndoStep> from, List<UndoStep> to)
        {
            if (from.Count == 0) return false;
            UndoStep step = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            to.Add(Capture(step.Label));
            Apply(step);
            SetStatus((from == _undo ? "Undid: " : "Redid: ") + step.Label);
            return true;
        }

        // --------------------------------------------------------------------- overlay

        private static readonly Color AxisColor = new Color(0.8f, 0.82f, 0.9f, 0.55f);
        private static readonly Color CurveColor = new Color(1f, 0.64f, 0.22f, 0.95f);
        private static readonly Color MirrorCurveColor = new Color(1f, 0.64f, 0.22f, 0.35f);
        private static readonly Color WarnCurveColor = new Color(0.95f, 0.3f, 0.3f, 0.95f);
        private static readonly Color HandleColor = new Color(0.97f, 0.97f, 0.97f, 1f);
        private static readonly Color HandleOutline = new Color(0.1f, 0.1f, 0.12f, 0.9f);
        private static readonly Color SelectedColor = new Color(1f, 0.64f, 0.22f, 1f);
        private static readonly Color PoleColor = new Color(0.45f, 0.8f, 1f, 1f);
        private static readonly Color MirrorHandleColor = new Color(0.97f, 0.97f, 0.97f, 0.4f);
        private static readonly Color InsertColor = new Color(0.5f, 0.95f, 0.55f, 1f);
        private static readonly Color SketchColor = new Color(1f, 0.9f, 0.35f, 0.95f);

        private readonly List<Vector2> _screenScratch = new List<Vector2>();

        private void EnsureOverlay()
        {
            if (_overlay != null) return;

            var canvasGO = new GameObject("LatheOverlayCanvas", typeof(RectTransform)) { hideFlags = HideFlags.DontSave };
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Under the docked panels (sortingOrder 0), so a handle dragged behind a panel slides
            // under it rather than drawing over its buttons. No GraphicRaycaster: the overlay must
            // never take a click.
            canvas.sortingOrder = -5;
            _overlayCanvas = canvasGO;

            // CanvasRenderer listed explicitly - see RegionMarqueeGraphic's remarks.
            var go = new GameObject("LatheOverlay", typeof(RectTransform), typeof(CanvasRenderer), typeof(LatheOverlayGraphic));
            go.transform.SetParent(canvasGO.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = Vector2.zero;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            _overlay = go.GetComponent<LatheOverlayGraphic>();
            _overlay.raycastTarget = false;
        }

        private void SetOverlayVisible(bool visible)
        {
            if (!visible && _overlayCanvas == null) return;
            EnsureOverlay();
            if (_overlayCanvas.activeSelf != visible) _overlayCanvas.SetActive(visible);
        }

        private void DrawOverlay()
        {
            if (_cam == null || _root == null) return;
            UpdateProfileBasis();
            _overlay.Clear();

            // The axis, a little past the shape at both ends.
            float lo = _profile.Count > 0 ? _profile.MinHeight : 0f;
            float hi = _profile.Count > 0 ? _profile.MaxHeight : DefaultHeight() / Scale;
            float pad = Mathf.Max((hi - lo) * 0.15f, DefaultHeight() * 0.1f / Scale);
            if (TryScreen(new Vector2(0f, lo - pad), 1f, out Vector2 a0) && TryScreen(new Vector2(0f, hi + pad), 1f, out Vector2 a1))
                _overlay.AddDashedLine(a0, a1, 1.5f, AxisColor, 8f, 5f);

            // The curve, on both silhouettes - as tessellated, so it traces the mesh's actual edge.
            bool warn = _hasMesh && (_build.SelfIntersecting || _build.TouchesAxis);
            if (_hasMesh) DrawProfileCurve(_build.Profile, _build.ProfileIsLoop, warn);
            else if (_profile.Count >= 2) DrawControlCurve();

            // Handles: mirror side first, so the canonical ones sit on top where they overlap.
            for (int side = -1; side <= 1; side += 2)
            {
                for (int i = 0; i < _profile.Count; i++)
                {
                    LatheProfile.Point p = _profile[i];
                    if (!TryScreen(p.Position, side, out Vector2 s)) continue;
                    bool onAxis = p.Position.x <= 0f;
                    if (onAxis && side < 0) continue; // same place as the canonical one

                    bool selected = i == SelectedPoint;
                    bool hovered = i == _hoverPoint && _drag == DragKind.None;
                    float radius = side > 0 ? (hovered || selected ? 6.5f : 5f) : 3.5f;
                    Color fill = side < 0 ? MirrorHandleColor
                        : selected ? SelectedColor
                        : onAxis ? PoleColor
                        : HandleColor;
                    _overlay.AddDot(s, radius, fill, HandleOutline, p.Sharp);
                }
            }

            if (_hoverInsert.Valid && _drag == DragKind.None && TryScreen(_hoverInsert.Position, _hoverInsert.Side, out Vector2 ins))
                _overlay.AddDot(ins, 4f, InsertColor, HandleOutline);

            if (_drag == DragKind.Sketch && _sketch.Count > 1)
            {
                _screenScratch.Clear();
                for (int i = 0; i < _sketch.Count; i++)
                    if (TryScreen(_sketch[i], 1f, out Vector2 s)) _screenScratch.Add(s);
                _overlay.AddPolyline(_screenScratch, 2f, SketchColor);
            }

            _overlay.Commit();
        }

        private void DrawProfileCurve(List<Vector2> profile, bool loop, bool warn)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                _screenScratch.Clear();
                for (int i = 0; i < profile.Count; i++)
                    if (TryScreen(profile[i], side, out Vector2 s)) _screenScratch.Add(s);
                Color color = warn ? WarnCurveColor : side > 0 ? CurveColor : MirrorCurveColor;
                if (warn && side < 0) color.a = 0.4f;
                _overlay.AddPolyline(_screenScratch, side > 0 ? 2f : 1.5f, color, loop);
            }
        }

        /// The smooth curve straight from the control points - drawn when there is no mesh to
        /// trace (e.g. every point on the axis), so the artist still sees what they are drawing.
        private void DrawControlCurve()
        {
            int segments = LatheCurve.SegmentCount(_profile.Points, _profile.ClosedLoop);
            _screenScratch.Clear();
            for (int seg = 0; seg < segments; seg++)
                for (int step = seg == 0 ? 0 : 1; step <= 16; step++)
                    if (TryScreen(LatheCurve.Evaluate(_profile.Points, _profile.ClosedLoop, seg, step / 16f), 1f, out Vector2 s))
                        _screenScratch.Add(s);
            _overlay.AddPolyline(_screenScratch, 2f, CurveColor);
        }
    }
}
