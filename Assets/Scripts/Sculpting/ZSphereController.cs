using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Drives the ZSphere blockout: owns the armature, draws it, owns the mouse while
    /// GizmoMode.ZSphere is active, keeps a live skin on it, and bakes that skin into an ordinary
    /// SculptableMesh on Convert.
    ///
    /// Split across partial files by concern:
    ///   ZSphereController.cs          state, lifecycle, drawing, rig-level operations, gizmo
    ///   ZSphereController.Input.cs    analytic picking and every mouse/keyboard gesture
    ///   ZSphereController.Skin.cs     the live skin and Convert
    ///   ZSphereController.History.cs  rig-local undo, and attaching the rig to an object
    ///
    /// The rig is a scaffold, not a scene object: it is not in SelectionManager or save files,
    /// and it keeps its own undo history. Convert is where the work becomes real geometry.
    // Before the default order for the same reason as SculptController: CameraOrbitController
    // (order 0) reads IsHoveringNode to leave the wheel to the rig's resize.
    [DefaultExecutionOrder(-10)]
    public partial class ZSphereController : MonoBehaviour, IGizmoTargetSource, IGizmoPointerClaim, IMirrorPlaneExtentProvider
    {
        public const float MinNodeRadius = 0.005f;
        public const float MaxNodeRadius = 5f;

        private readonly ZSphereRig _rig = new ZSphereRig();
        public ZSphereRig Rig => _rig;

        public int SelectedNode { get; private set; } = ZSphereRig.NoNode;
        public int HoveredNode { get; private set; } = ZSphereRig.NoNode;

        /// Whether the cursor is over a rig sphere. Read by CameraOrbitController so the wheel
        /// resizes the sphere under the cursor instead of zooming.
        public static bool IsHoveringNode { get; private set; }

        public ZSphereEditMode EditMode
        {
            get => _editMode;
            set
            {
                if (_editMode == value) return;
                EndDrag();
                _editMode = value;
            }
        }
        private ZSphereEditMode _editMode = ZSphereEditMode.Draw;

        /// Mirror across the rig's x = 0 plane. On by default: creatures and characters are
        /// overwhelmingly bilateral, and building one side twice is the tedium this tool removes.
        public bool SymmetryX
        {
            get => _symmetryX;
            set => SetSymmetry(value);
        }
        private bool _symmetryX = true;

        /// A new child's radius as a fraction of its parent's. Slightly under 1 so a chain tapers
        /// naturally as it extends, which is what makes a limb pulled out of a torso read as one.
        public float ChildTaper
        {
            get => _childTaper;
            set => _childTaper = Mathf.Clamp(value, 0.2f, 1.5f);
        }
        private float _childTaper = 0.85f;

        /// Leave the rig in place after Convert instead of clearing it.
        public bool KeepRigOnConvert { get; set; }

        /// Lay spheres on the attach target's surface rather than on the view plane. Inert with
        /// nothing attached - see AttachToObject.
        public bool SnapToSurface { get; set; } = true;

        public int SphereCount => _rig.AliveCount;

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

        private Transform _rigRoot;
        private ZSphereArmatureView _view;
        private bool _wasActive;

        // What the armature mesh was last built from - see RefreshView.
        private int _drawnVersion = -1;
        private int _drawnSelection = -2;
        private int _drawnHover = -2;
        private int _drawnHoverLink = -2;
        private bool _drawnSymmetry;
        private bool _drawnArmature;
        private bool _drawnPlane;

        // ---------------------------------------------------------------------- lifecycle

        private void Awake()
        {
            _cam = Camera.main;
            EnsureRigRoot();
            SetRigVisible(false);
        }

        private void Update()
        {
            if (_cam == null) _cam = Camera.main;

            // Both run with the tool put away: a coalescing edit (a slider drag) can still be open
            // when the tool is switched, and an attached rig has to keep following its body while
            // the Transpose gizmo moves it - a mode in which this controller is otherwise asleep.
            TickPendingRigEdit();
            FollowAttachTarget();

            bool active = Gizmo != null && Gizmo.Mode == GizmoMode.ZSphere && _cam != null;
            if (active != _wasActive)
            {
                _wasActive = active;
                if (!active) EndDrag();
                SetRigVisible(active);
            }

            if (!active)
            {
                IsHoveringNode = false;
                HoveredNode = ZSphereRig.NoNode;
                _hoveredLink = ZSphereRig.NoNode;
                _cursorVisible = false;
                SyncGizmoTargets();
                return;
            }

            HandleInput();
            SyncGizmoTargets();
            RefreshView();
            RefreshSkin();
        }

        // Shares object mirror planes (see WorldExtentForPlaneAt).
        private void OnEnable() => MirrorController.RegisterPlaneExtentProvider(this);

        private void OnDisable()
        {
            MirrorController.UnregisterPlaneExtentProvider(this);
            IsHoveringNode = false;
            // Committed rather than dropped: the edit has already happened to the rig, and
            // throwing its snapshot away would leave it permanently un-undoable.
            CommitRigEdit();
        }

        private void OnDestroy()
        {
            DestroySkin();
            _view?.Destroy();
            _view = null;
            if (_rigRoot != null) Destroy(_rigRoot.gameObject);
        }

        // ------------------------------------------------------------------------ drawing

        private void EnsureRigRoot()
        {
            if (_rigRoot != null) return;
            var root = new GameObject("ZSphere Rig") { hideFlags = HideFlags.DontSave };
            _rigRoot = root.transform;
            _view = new ZSphereArmatureView(_rigRoot);
        }

        private void SetRigVisible(bool visible)
        {
            EnsureRigRoot();
            if (_rigRoot.gameObject.activeSelf != visible) _rigRoot.gameObject.SetActive(visible);
            _drawnVersion = -1;
        }

        /// Rebuilds the armature mesh when anything it shows has changed. Picking is analytic
        /// (see ZSphereController.Input), so unlike the old collider-per-sphere rig there is no
        /// physics copy to resync afterwards - what was just drawn is what the next click tests.
        private void RefreshView()
        {
            bool armature = !PreviewMode;
            bool plane = SymmetryX && !_rig.IsEmpty && !PreviewMode && !AnchorShowsOwnPlane();
            int hover = _drag == DragKind.None ? HoveredNode : ZSphereRig.NoNode;
            int hoverLink = _drag == DragKind.None ? _hoveredLink : ZSphereRig.NoNode;

            if (_rig.Version != _drawnVersion || SelectedNode != _drawnSelection || hover != _drawnHover ||
                hoverLink != _drawnHoverLink || SymmetryX != _drawnSymmetry || armature != _drawnArmature ||
                plane != _drawnPlane)
            {
                _drawnVersion = _rig.Version;
                _drawnSelection = SelectedNode;
                _drawnHover = hover;
                _drawnHoverLink = hoverLink;
                _drawnSymmetry = SymmetryX;
                _drawnArmature = armature;
                _drawnPlane = plane;

                _view.SetArmatureVisible(armature);
                if (armature) _view.Rebuild(_rig, SymmetryX, SelectedNode, hover, hoverLink);
                _view.SetSymmetryPlane(plane, PlaneSize());
            }

            _view.SetCursor(_cursorVisible && !PreviewMode, _cursorLocal, _cursorRadius);
        }

        private float PlaneSize()
        {
            Bounds b = _rig.ComputeBounds(SymmetryX);
            float extent = Mathf.Max(b.extents.magnitude, _rig.MeanRadius() * 2f);
            return Mathf.Max(0.05f, extent * 2.2f);
        }

        /// Whether the object the rig is anchored to already draws this same X plane. Drawing a
        /// second quad in the same place z-fights, and the doubled alpha reads as a second axis.
        private bool AnchorShowsOwnPlane()
        {
            Transform anchor = SymmetryAnchor();
            var mirror = anchor != null ? anchor.GetComponent<MirrorController>() : null;
            return mirror != null && mirror.MirrorX && mirror.ShowPlanes && _rigRoot != null &&
                   (anchor.position - _rigRoot.position).sqrMagnitude < 1e-6f;
        }

        private Vector3 RigToWorld(Vector3 rigPoint) => _rigRoot.TransformPoint(rigPoint);
        private Vector3 WorldToRig(Vector3 worldPoint) => _rigRoot.InverseTransformPoint(worldPoint);

        // ----------------------------------------------------------------- rig operations

        /// Toggles symmetry without ever making spheres vanish or double up. Returns a line for the
        /// panel's status, since what happened (how much was baked or folded) is not otherwise
        /// visible.
        ///
        /// Off BAKES the derived reflection into real nodes - otherwise half the blockout would
        /// disappear with the toggle. On folds exact reflections back up (MergeMirrorPairs), so an
        /// off-then-on round trip with no edits in between is a no-op. Both are one undo step, and
        /// the step records the toggle's state too, so undoing a bake brings back the symmetric rig
        /// rather than a one-sided one.
        public string SetSymmetry(bool on)
        {
            if (on == _symmetryX) return on ? "Symmetry is already on." : "Symmetry is already off.";

            EndDrag();
            CommitRigEdit();
            BeginRigEdit(on ? "Symmetry On" : "Symmetry Off");

            string report;
            if (on)
            {
                int folded = _rig.MergeMirrorPairs();
                _rig.SnapCentralToAxis();
                _symmetryX = true;
                report = folded > 0
                    ? $"Symmetry on - folded {folded} mirrored sphere(s) back into one half."
                    : "Symmetry on - the rig now mirrors across the red plane.";
            }
            else
            {
                int baked = _rig.BakeSymmetry();
                _symmetryX = false;
                report = baked > 0
                    ? $"Symmetry off - the mirrored half is now {baked} real sphere(s) you can edit separately."
                    : "Symmetry off.";
            }

            if (!_rig.IsAlive(SelectedNode)) SelectedNode = ZSphereRig.NoNode;
            CommitRigEdit();
            return report;
        }

        /// Starts a blockout from nothing - the ZSphere entry in Add Primitive. Never clears an
        /// existing rig: a button among Cube/Sphere/Cylinder reads as "add one", and silently
        /// destroying a blockout would be the worst thing in the tool. Returns false (having just
        /// armed the tool) when a rig is already up.
        public bool StartNewRig()
        {
            EnsureRigRoot();
            EditMode = ZSphereEditMode.Draw;
            Gizmo?.SetMode(GizmoMode.ZSphere);
            if (!_rig.IsEmpty) return false;

            Vector3 world = Vector3.zero;
            CameraOrbitController orbit = Orbit;
            if (orbit != null) orbit.GetView(out _, out _, out _, out world);

            AnchorRigRoot(world);

            BeginRigEdit("New ZSphere Rig");
            float radius = DefaultRootRadius();
            SelectedNode = _rig.AddRoot(SnapIfSymmetric(WorldToRig(world), radius), radius);
            CommitRigEdit();
            return true;
        }

        /// Deletes a sphere and everything below it. Under symmetry the reflection goes too, by
        /// construction - it was never a separate node.
        public void DeleteNode(int index)
        {
            if (!_rig.IsAlive(index)) return;
            BeginRigEdit("Delete Sphere");
            _rig.Remove(index);
            if (!_rig.IsAlive(SelectedNode)) SelectedNode = ZSphereRig.NoNode;
            if (!_rig.IsAlive(HoveredNode)) HoveredNode = ZSphereRig.NoNode;
            CommitRigEdit();
        }

        /// Throws the whole blockout away. Undoable - it is the most destructive thing in the tool
        /// and sits one click from the buttons next to it.
        public void ClearRig()
        {
            if (_rig.IsEmpty) return;
            EndDrag();
            BeginRigEdit("Clear ZSpheres");
            ClearRigInternal();
            CommitRigEdit();
        }

        /// The clear itself, with no undo step of its own - Convert records its own step, which
        /// puts the rig back as part of un-converting.
        private void ClearRigInternal()
        {
            _drag = DragKind.None;
            _rig.Clear();
            SelectedNode = ZSphereRig.NoNode;
            HoveredNode = ZSphereRig.NoNode;
        }

        /// Radius of the selected sphere, for the panel's slider. 0 with nothing selected.
        public float SelectedRadius
        {
            get
            {
                ZSphereRig.Node node = _rig.Get(SelectedNode);
                return node != null ? node.Radius : 0f;
            }
            set
            {
                if (!_rig.IsAlive(SelectedNode)) return;
                // Coalesced: a slider fires continuously while dragged.
                BeginRigEdit("Resize Sphere", CoalesceSeconds);
                _rig.SetRadius(SelectedNode, Mathf.Clamp(value, MinNodeRadius, MaxNodeRadius));
            }
        }

        private Vector3 SnapIfSymmetric(Vector3 position, float radius) =>
            _symmetryX ? ZSphereRig.SnapToAxis(position, radius) : position;

        // ------------------------------------------------------------------- anchoring

        /// Seats an EMPTY rig's root - and so its mirror plane - on the sculpt object, falling back
        /// to `fallbackWorld`. The plane the artist can already see belongs to that object, so the
        /// rig must share it; anchoring anywhere else invents a second, competing X axis.
        private void AnchorRigRoot(Vector3 fallbackWorld)
        {
            if (!_rig.IsEmpty) return;
            Transform anchor = SymmetryAnchor();
            if (anchor != null) _rigRoot.SetPositionAndRotation(anchor.position, anchor.rotation);
            else _rigRoot.SetPositionAndRotation(fallbackWorld, Quaternion.identity);
            SyncAttachReference();
        }

        /// The transform whose origin and orientation define the mirror plane: the attach target
        /// if there is one, else the selection, else the scene's main object. Null in an empty
        /// scene, in which case the rig stands on its own.
        private Transform SymmetryAnchor()
        {
            if (_attachTarget != null) return _attachTarget.transform;

            SculptableMesh primary = Selection != null ? Selection.PrimarySelection : null;
            if (primary != null) return primary.transform;

            var spawner = FindFirstObjectByType<PrimitiveSpawner>();
            SculptableMesh main = spawner != null ? spawner.MainObject : null;
            return main != null ? main.transform : null;
        }

        /// Re-seats an EXISTING rig's mirror plane on the sculpt object while leaving every
        /// sphere where it is in the world - capture world positions, move the root, write them
        /// back rig-local. Without the compensation the whole blockout would jump with the plane.
        public bool ReanchorSymmetryPlane()
        {
            Transform anchor = SymmetryAnchor();
            if (anchor == null || _rigRoot == null) return false;

            BeginRigEdit("Re-centre Plane");
            var world = new Vector3[_rig.Count];
            for (int i = 0; i < _rig.Count; i++)
                if (_rig.IsAlive(i)) world[i] = RigToWorld(_rig.Nodes[i].Position);

            _rigRoot.SetPositionAndRotation(anchor.position, anchor.rotation);

            for (int i = 0; i < _rig.Count; i++)
                if (_rig.IsAlive(i)) _rig.SetPosition(i, WorldToRig(world[i]));

            SyncAttachReference();
            CommitRigEdit();
            return true;
        }

        /// Sized off what is already in the scene, so the first sphere is neither a speck nor a
        /// planet next to the model. An existing rig is the better reference when there is one.
        private float DefaultRootRadius()
        {
            if (!_rig.IsEmpty) return Mathf.Clamp(_rig.MeanRadius(), MinNodeRadius, MaxNodeRadius);

            var spawner = FindFirstObjectByType<PrimitiveSpawner>();
            SculptableMesh main = spawner != null ? spawner.MainObject : null;
            if (main == null || main.Mesh == null) return 0.25f;

            Vector3 e = main.Mesh.bounds.extents;
            Vector3 s = main.transform.lossyScale;
            float avg = ((e.x + e.y + e.z) / 3f) * ((s.x + s.y + s.z) / 3f);
            return Mathf.Clamp(avg * 0.35f, 0.02f, 1f);
        }

        /// World-space half-size of the rig for a mirror plane at `planeOrigin`, or 0 unless a
        /// visible, non-empty rig mirrors about that very point. Read by MirrorController to size
        /// the plane the two share.
        public float WorldExtentForPlaneAt(Vector3 planeOrigin)
        {
            if (_rigRoot == null || _rig.IsEmpty || !_rigRoot.gameObject.activeSelf) return 0f;
            if ((_rigRoot.position - planeOrigin).sqrMagnitude > 1e-6f) return 0f;
            Bounds bounds = _rig.ComputeBounds(SymmetryX);
            return Mathf.Max(bounds.extents.magnitude, _rig.MeanRadius() * 2f);
        }

        // --------------------------------------------------------------- transform gizmo

        public Vector3 RigPointToWorld(Vector3 rigPoint) => _rigRoot != null ? RigToWorld(rigPoint) : rigPoint;

        public float RigRadiusToWorld(float rigRadius)
        {
            if (_rigRoot == null) return rigRadius;
            Vector3 s = _rigRoot.lossyScale;
            return rigRadius * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        }

        /// Moves a node for the transform gizmo. Carries the subtree, exactly as a Move-mode drag
        /// does, so the two ways of moving a sphere never disagree about what moves with it.
        public void MoveNodeFromGizmo(int nodeIndex, Vector3 worldPosition)
        {
            ZSphereRig.Node node = _rig.Get(nodeIndex);
            if (_rigRoot == null || node == null) return;
            Vector3 target = SnapIfSymmetric(WorldToRig(worldPosition), node.Radius);
            _rig.TranslateSubtree(nodeIndex, target - node.Position);
        }

        private readonly List<GizmoTarget> _gizmoTargets = new List<GizmoTarget>();
        private int _gizmoTargetNode = ZSphereRig.NoNode;

        /// Points the axis gizmo at the selected sphere in Move mode, for the moment a limb needs
        /// moving straight down one axis. Grabbing the sphere itself still free-drags it.
        private void SyncGizmoTargets()
        {
            TransformGizmo gizmo = Gizmo;
            if (gizmo == null) return;

            bool wants = _wasActive && EditMode == ZSphereEditMode.Move && !PreviewMode &&
                         _rig.IsAlive(SelectedNode) && _drag == DragKind.None;

            if (!wants)
            {
                if (_gizmoTargetNode != ZSphereRig.NoNode)
                {
                    gizmo.ClearExternalTargets(this);
                    _gizmoTargetNode = ZSphereRig.NoNode;
                    _gizmoTargets.Clear();
                }
                return;
            }

            if (_gizmoTargetNode == SelectedNode) return;

            _gizmoTargetNode = SelectedNode;
            _gizmoTargets.Clear();
            _gizmoTargets.Add(new ZSphereNodeTarget(this, SelectedNode));
            gizmo.SetExternalTargets(this, _gizmoTargets, GizmoHandleSet.Move);
        }

        public bool RecordsOwnUndoStep => true;

        public void OnGizmoDragStarted() => BeginRigEdit("Move Sphere");

        public void OnGizmoDragEnded(bool changed) => CommitRigEdit();
    }
}
