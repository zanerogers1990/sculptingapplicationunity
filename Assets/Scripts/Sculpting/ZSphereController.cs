using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Drives the whole ZSphere blockout workflow: owns the rig, draws it, owns the mouse while
    /// GizmoMode.ZSphere is active, previews the skin live, and finally bakes the skin into an
    /// ordinary SculptableMesh.
    ///
    /// Owning the mouse works exactly the way TransformGizmo already does it - SculptController
    /// early-outs whenever Gizmo.Mode != Sculpt, so brush strokes and rig editing can never
    /// fight over the same click, and the mode toolbar in the Scene panel is the one place that
    /// switches between them.
    ///
    /// Lives on SceneSystems alongside the other controllers, found by SceneGraphUIBuilder via
    /// FindFirstObjectByType, matching how every other controller in this project is reached.
    ///
    /// The rig deliberately does NOT participate in SelectionManager, save files or undo. It is a
    /// scaffold, not an object: it produces one, and requirement-7's "the ZSphere rig disappears"
    /// is the normal end of its life. Convert is therefore the point at which the work becomes
    /// real, savable, undoable geometry - which is also why KeepRigOnConvert exists, for the
    /// blockout you want to keep iterating on after taking one skin off it.
    public class ZSphereController : MonoBehaviour
    {
        // ------------------------------------------------------------------------- constants

        /// How often the live preview may re-skin while the rig is changing. Skinning is far
        /// heavier than MaskExtractController's extract (a whole voxel grid, not a vertex walk),
        /// so this is a good deal slower than that controller's 0.08s - and drags suppress it
        /// entirely (see RefreshPreview), which is what actually keeps dragging smooth.
        private const float SkinRebuildInterval = 0.25f;

        /// Pixels of mouse travel before a press-on-a-sphere becomes a drag. Below this a press
        /// and release is a plain selection click - the distinction the whole Add mode rests on
        /// ("click to place, drag to grow a child").
        private const float DragThresholdPixels = 4f;

        /// Link tubes are drawn at this fraction of the spheres' radii. Deliberately thinner than
        /// the skin they describe: at full radius the tube swallows its own end spheres and the
        /// rig reads as one featureless sausage, with no way to see where the joints you can
        /// actually grab are. The rig is a schematic; the preview skin is the truth.
        private const float LinkVisualScale = 0.7f;
        private const int LinkSegments = 12;

        private const float RadiusScrollPercentPerNotch = 0.1f;
        private const float ScaleDragSensitivity = 0.005f; // radius fraction per pixel
        public const float MinNodeRadius = 0.005f;
        public const float MaxNodeRadius = 5f;

        /// Steps of rig history kept. A snapshot is the whole tree at ~64 bytes a node, so even a
        /// large blockout is a few kilobytes and this entire stack costs less than one brush
        /// stroke's mesh delta - there is no reason to be stingy with it.
        private const int MaxRigUndoSteps = 96;

        /// How long a coalescing edit (a run of scroll-wheel notches, a slider being dragged)
        /// stays open before it closes as ONE undo step.
        private const float CoalesceSeconds = 0.4f;

        private static readonly Color NodeColor = new Color(0.35f, 0.72f, 0.95f);
        private static readonly Color SelectedNodeColor = new Color(1f, 0.62f, 0.2f);
        private static readonly Color LinkColor = new Color(0.22f, 0.45f, 0.62f);
        // Paler than a placed sphere so the ghost reads as "this is where it WILL go" rather than
        // as a node that is already part of the rig.
        private static readonly Color PlacementCursorColor = new Color(0.6f, 0.85f, 1f, 0.45f);
        private static readonly Color PreviewColor = new Color(0.55f, 0.85f, 0.6f, 0.38f);
        private static readonly Color PreviewRimColor = new Color(0.85f, 1f, 0.9f, 1f);

        // ------------------------------------------------------------------------- rig state

        private readonly ZSphereRig _rig = new ZSphereRig();
        public ZSphereRig Rig => _rig;

        private ZSphereSkinner.SkinSettings _settings = ZSphereSkinner.SkinSettings.Default;

        public int SelectedNode { get; private set; } = ZSphereRig.NoNode;

        /// Whether the cursor is over a rig sphere right now. Read by CameraOrbitController so
        /// the wheel resizes the sphere under the cursor instead of zooming the view - the same
        /// deferral SculptController.IsHoveringSculptSurface already arranges for the brush.
        public static bool IsHoveringNode { get; private set; }

        /// Last skin attempt's outcome, for the UI's status line. Non-null means the preview is
        /// empty and Convert would do nothing.
        public string Error { get; private set; }
        public int PreviewTriangleCount { get; private set; }

        // -------------------------------------------------------------------- user settings

        public ZSphereEditMode EditMode { get; set; } = ZSphereEditMode.Add;

        /// Mirror new spheres (and edits to mirrored ones) across the rig's local YZ plane. On by
        /// default because the overwhelming majority of what ZSpheres get used for - creatures,
        /// characters, limbs - is bilaterally symmetric, and building one side twice is exactly
        /// the tedium this feature exists to remove.
        public bool SymmetryX { get; set; } = true;

        /// A new child's radius as a fraction of its parent's - the "adaptive radius" rule.
        /// Slightly under 1 by default so a chain of spheres tapers naturally as it extends,
        /// which is what makes a limb pulled out of a torso read as a limb.
        public float ChildTaper
        {
            get => _childTaper;
            set => _childTaper = Mathf.Clamp(value, 0.2f, 1.5f);
        }
        private float _childTaper = 0.85f;

        public bool LivePreview { get; set; } = true;

        /// Leave the rig in place after Convert instead of clearing it. Off by default (the
        /// ZBrush behaviour, and the one that keeps the viewport honest about what is real
        /// geometry), on for the blockout you want to keep iterating.
        public bool KeepRigOnConvert { get; set; }

        public int Resolution
        {
            get => _settings.Resolution;
            set { _settings.Resolution = Mathf.Clamp(value, ZSphereSkinner.MinResolution, ZSphereSkinner.MaxResolution); InvalidatePreview(); }
        }

        public bool AdaptiveResolution
        {
            get => _settings.AdaptiveResolution;
            set { _settings.AdaptiveResolution = value; InvalidatePreview(); }
        }

        public float Blend
        {
            get => _settings.Blend;
            set { _settings.Blend = Mathf.Clamp01(value); InvalidatePreview(); }
        }

        public int Smoothing
        {
            get => _settings.Smoothing;
            set { _settings.Smoothing = Mathf.Clamp(value, 0, 12); InvalidatePreview(); }
        }

        /// The resolution the next skin will actually run at - Adaptive can pick a much higher
        /// number than the slider shows, and that is worth seeing before pressing Convert.
        public int EffectiveResolution => ZSphereSkinner.PreviewResolution(_rig, _settings);

        public int SphereCount => _rig.AliveCount;

        /// How close to the mirror plane a sphere has to be - as a fraction of its own radius -
        /// to count as sitting ON it, and to be pulled exactly onto it as it is placed or dragged.
        ///
        /// Why a snap and not just a tolerance: a sphere extruded straight DOWN out of a central
        /// torso sphere wanders a few thousandths in x as the mouse moves, and the instant it
        /// wandered past a bare tolerance TryCreateTwin fired - so the single spine the user was
        /// drawing silently became a mirrored PAIR straddling the centre line. Pinning x to
        /// exactly zero inside this band keeps a centre-line chain a centre-line chain (what
        /// ZBrush does with symmetry on), and leaving the band then becomes a deliberate act that
        /// splits the chain into two limbs. Set to 0 for the old always-split behaviour.
        public float CentreSnap
        {
            get => _centreSnap;
            set => _centreSnap = Mathf.Clamp(value, 0f, 1f);
        }
        private float _centreSnap = 0.3f;

        /// Lay new spheres (and Move-mode drags) on the attach target's surface rather than on
        /// the view plane - see AttachToObject. Inert with nothing attached.
        public bool SnapToSurface { get; set; } = true;

        // ---------------------------------------------------------------------- scene refs

        private Camera _cam;

        private SelectionManager _selection;
        private SelectionManager Selection =>
            _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());

        private TransformGizmo _gizmo;
        private TransformGizmo Gizmo =>
            _gizmo != null ? _gizmo : (_gizmo = FindFirstObjectByType<TransformGizmo>());

        // ------------------------------------------------------------------ handle plumbing

        /// Tags a rig sphere's collider with which node it draws, so RaycastAll can turn a hit
        /// into a node index. Same idiom (and same reason for being a private nested
        /// MonoBehaviour) as TransformGizmo.GizmoHandleTag.
        private class ZSphereNodeTag : MonoBehaviour
        {
            public int Index;
        }

        /// The same idea for the tapered tube BETWEEN two spheres, so a click on a limb can be
        /// turned into "insert here". A link is identified by its CHILD node: every non-root node
        /// has exactly one link running up to its parent, so the child names the link uniquely
        /// with nothing extra to keep in step.
        private class ZSphereLinkTag : MonoBehaviour
        {
            public int ChildIndex;
        }

        private Transform _rigRoot;
        // The translucent quad showing where the rig mirrors - see UpdateSymmetryPlane.
        private Transform _symmetryPlane;
        // Ghost of the first sphere before it is placed - see UpdatePlacementCursor.
        private Transform _placementCursor;
        private Transform _handleRoot;
        private Material _nodeMaterial, _selectedMaterial, _linkMaterial;

        private readonly List<GameObject> _nodeHandles = new List<GameObject>();
        private readonly List<GameObject> _linkHandles = new List<GameObject>();
        private readonly List<Mesh> _linkMeshes = new List<Mesh>();

        private int _lastDrawnVersion = -1;
        private int _lastDrawnSelection = ZSphereRig.NoNode;
        private bool _lastDrawnSymmetry;
        private bool _lastDrawnAnchorPlane;

        // Preview state
        private GameObject _previewGO;
        private Mesh _previewMesh;
        private Material _previewMaterial;
        private int _lastSkinnedVersion = -1;
        private float _nextAllowedSkin;

        // Drag state, captured at press
        private enum DragKind { None, PendingChild, MoveNode, ScaleNode, PoseNode }
        private DragKind _drag = DragKind.None;
        private int _dragParent = ZSphereRig.NoNode; // PendingChild: the sphere being grown from
        private int _dragNode = ZSphereRig.NoNode;   // the sphere actually being edited
        private Vector2 _dragStartMouse;
        private float _dragStartRadius;
        private Vector3 _dragPlanePoint;

        private bool _wasActive;

        // Rig-local undo - see BeginRigEdit.
        private struct RigUndoStep
        {
            public ZSphereRig.Node[] Rig;
            public int Selection;
            public string Label;
        }

        private readonly List<RigUndoStep> _rigUndo = new List<RigUndoStep>();
        private readonly List<RigUndoStep> _rigRedo = new List<RigUndoStep>();

        private ZSphereRig.Node[] _pendingSnapshot;
        private int _pendingSelection;
        private int _pendingVersion;
        private string _pendingLabel;
        private float _pendingCommitAt = float.MaxValue;

        // Attach state - see AttachToObject.
        private SculptableMesh _attachTarget;
        private Vector3 _attachLastPos;
        private Quaternion _attachLastRot = Quaternion.identity;

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

            // Both of these run even with the tool put away. A coalescing edit (a radius slider
            // drag) can still be open when the user switches tools, and the rig has to keep
            // following the object it is attached to while that object is being moved with the
            // Transpose gizmo - which is a mode in which this controller is otherwise asleep.
            TickPendingRigEdit();
            FollowAttachTarget();

            bool active = Gizmo != null && Gizmo.Mode == GizmoMode.ZSphere && _cam != null;
            if (active != _wasActive)
            {
                _wasActive = active;
                SetRigVisible(active);
                if (!active) EndDrag();
            }

            if (!active)
            {
                IsHoveringNode = false;
                UpdatePlacementCursor(false);
                return;
            }

            RefreshHandles();
            // Before HandleInput, so the ghost shown this frame is the position the click landing
            // this frame will actually use.
            UpdatePlacementCursor(true);
            HandleInput();
            RefreshPreview();
        }

        private void OnDisable()
        {
            IsHoveringNode = false;
            // Anything half-recorded is committed rather than dropped: the edit it describes has
            // already happened to the rig, and throwing the snapshot away would leave that edit
            // permanently un-undoable.
            CommitRigEdit();
            DestroyPreview();
        }

        private void OnDestroy()
        {
            // Everything below is created in code at runtime, so nothing else will collect it -
            // the per-link meshes and the handle materials in particular are per-instance and
            // would leak for the rest of the session (and, in the Editor, past Play mode ending).
            for (int i = 0; i < _linkMeshes.Count; i++)
                if (_linkMeshes[i] != null) Destroy(_linkMeshes[i]);
            _linkMeshes.Clear();
            _nodeHandles.Clear();
            _linkHandles.Clear();

            if (_nodeMaterial != null) Destroy(_nodeMaterial);
            if (_selectedMaterial != null) Destroy(_selectedMaterial);
            if (_linkMaterial != null) Destroy(_linkMaterial);
            if (_rigRoot != null) Destroy(_rigRoot.gameObject);
        }

        // -------------------------------------------------------------------------- input

        private void HandleInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            Keyboard kb = Keyboard.current;
            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            // Alt is the camera's - orbiting must never also edit the rig, the same guard
            // SculptController applies to brush strokes.
            bool altHeld = kb != null && kb.leftAltKey.isPressed;
            bool ctrlHeld = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            bool blocked = overUI || altHeld;

            Ray ray = _cam.ScreenPointToRay(mouse.position.ReadValue());
            int hovered = blocked ? ZSphereRig.NoNode : PickNode(ray);
            IsHoveringNode = hovered != ZSphereRig.NoNode && _drag == DragKind.None;

            HandleRadiusScroll(mouse, hovered);

            // Z / Shift+Z step the RIG's own history while this tool is active - see UndoRig.
            // Guarded by HandlesUndoKey, the same predicate SculptController asks before running
            // the scene-wide undo, so exactly one of the two responds to a given press.
            if (kb != null && _drag == DragKind.None && kb.zKey.wasPressedThisFrame)
            {
                bool redo = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                if (HandlesUndoKey(redo))
                {
                    if (redo) RedoRig(); else UndoRig();
                    return;
                }
            }

            if (kb != null && SelectedNode != ZSphereRig.NoNode && _drag == DragKind.None &&
                (kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame))
            {
                DeleteNode(SelectedNode);
                return;
            }

            // Right-click, and Ctrl+left-click, delete a sphere and its subtree in ANY edit mode -
            // the same "the other button inverts what this tool does" convention the brushes use,
            // so pruning a limb never costs a trip to the mode buttons.
            //
            // Never mid-drag: DeleteNode opens and closes an undo step of its own, which would
            // close the one the in-flight drag opened and split that drag across two entries.
            if (!blocked && hovered != ZSphereRig.NoNode && _drag == DragKind.None &&
                (mouse.rightButton.wasPressedThisFrame ||
                 (ctrlHeld && mouse.leftButton.wasPressedThisFrame)))
            {
                DeleteNode(hovered);
                return;
            }

            if (!blocked && mouse.leftButton.wasPressedThisFrame && !ctrlHeld)
                BeginDrag(hovered, ray, mouse.position.ReadValue());

            if (_drag != DragKind.None)
            {
                if (mouse.leftButton.isPressed) UpdateDrag(ray, mouse.position.ReadValue());
                else EndDrag();
            }
        }

        private void HandleRadiusScroll(Mouse mouse, int hovered)
        {
            if (hovered == ZSphereRig.NoNode || _drag != DragKind.None) return;
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f) return;

            ZSphereRig.Node node = _rig.Get(hovered);
            if (node == null) return;
            // Coalesced: a resize is a run of wheel notches, and one undo step per notch would
            // mean a dozen presses to get back to where the sphere started.
            BeginRigEdit("Resize Sphere", CoalesceSeconds);
            SetRadiusSymmetric(hovered, node.Radius * (1f + Mathf.Sign(scroll) * RadiusScrollPercentPerNotch));
        }

        private void BeginDrag(int hovered, Ray ray, Vector2 mousePos)
        {
            if (hovered == ZSphereRig.NoNode)
            {
                if (EditMode == ZSphereEditMode.Add)
                {
                    // A click that missed every sphere may still have landed on a LIMB, which
                    // means "give me a sphere here" - the way to add volume partway along a
                    // finished chain without disturbing the joints at either end.
                    int link = PickLink(ray, out Vector3 linkPoint);
                    if (link != ZSphereRig.NoNode) { InsertOnLink(link, linkPoint); return; }

                    // Clicking genuine empty space only means anything when there is no rig yet -
                    // that is the "click to place a sphere" that starts one. Once a rig exists, a
                    // stray click in the void placing a second, disconnected root would be a
                    // surprise, not a feature, so it does nothing.
                    //
                    // Clicking the ATTACH TARGET's surface is the exception, and the whole point
                    // of attaching: each such click drops a fresh root ON the surface, which is
                    // how a second appendage (the other pair of limbs, a tail, a horn) gets
                    // started on a body that is already there. Still not a stray click - it has
                    // to land on the one object the user explicitly attached to.
                    if (_rig.IsEmpty || TrySurfacePoint(ray, out _)) PlaceRoot(ray);
                }
                return;
            }

            SelectedNode = hovered;
            _dragStartMouse = mousePos;

            ZSphereRig.Node node = _rig.Get(hovered);
            if (node == null) return;

            switch (EditMode)
            {
                case ZSphereEditMode.Add:
                    // Opened here rather than on the first movement so the snapshot predates the
                    // child; CommitRigEdit throws it away again if the press turns out to be a
                    // plain selection click that changed nothing.
                    BeginRigEdit("Add Sphere");
                    _drag = DragKind.PendingChild;
                    _dragParent = hovered;
                    _dragNode = ZSphereRig.NoNode; // created on the first real movement
                    _dragPlanePoint = RigToWorld(node.Position);
                    break;

                case ZSphereEditMode.Move:
                    BeginRigEdit("Move Sphere");
                    _drag = DragKind.MoveNode;
                    _dragNode = hovered;
                    _dragPlanePoint = RigToWorld(node.Position);
                    break;

                case ZSphereEditMode.Scale:
                    BeginRigEdit("Scale Sphere");
                    _drag = DragKind.ScaleNode;
                    _dragNode = hovered;
                    _dragStartRadius = node.Radius;
                    break;

                case ZSphereEditMode.Pose:
                    // Posing the root has no joint to swing about, so it slides the whole rig
                    // instead - which is the only thing "pose the root" can usefully mean, and
                    // saves the user switching modes to reposition the whole blockout.
                    BeginRigEdit("Pose Branch");
                    _drag = DragKind.PoseNode;
                    _dragNode = hovered;
                    _dragPlanePoint = RigToWorld(node.Position);
                    break;

                case ZSphereEditMode.Delete:
                    DeleteNode(hovered);
                    break;
            }
        }

        private void UpdateDrag(Ray ray, Vector2 mousePos)
        {
            if (_drag == DragKind.ScaleNode)
            {
                float delta = mousePos.x - _dragStartMouse.x;
                SetRadiusSymmetric(_dragNode, _dragStartRadius * (1f + delta * ScaleDragSensitivity));
                return;
            }

            if ((mousePos - _dragStartMouse).magnitude < DragThresholdPixels && _drag == DragKind.PendingChild)
                return;

            if (!TryDragPoint(ray, out Vector3 worldPoint)) return;
            Vector3 rigPoint = WorldToRig(worldPoint);

            switch (_drag)
            {
                case DragKind.PendingChild:
                {
                    ZSphereRig.Node parent = _rig.Get(_dragParent);
                    if (parent == null) { EndDrag(); return; }

                    if (_dragNode == ZSphereRig.NoNode)
                    {
                        float radius = Mathf.Clamp(parent.Radius * _childTaper, MinNodeRadius, MaxNodeRadius);
                        _dragNode = AddChildSymmetric(_dragParent, rigPoint, radius);
                        SelectedNode = _dragNode;
                    }
                    SetPositionSymmetric(_dragNode, rigPoint);
                    // See TryCreateTwin: the child is usually still on the mirror plane at birth,
                    // so its twin can only be decided once the drag has carried it off to one side.
                    TryCreateTwin(_dragNode);
                    break;
                }

                case DragKind.MoveNode:
                    // Surface snap wins over the view plane while something is attached: dragging
                    // a shoulder sphere across a torso should keep it ON the torso, which is the
                    // one thing a view-aligned plane cannot do as the camera is orbited.
                    if (TrySurfacePoint(ray, out Vector3 surface))
                        rigPoint = WorldToRig(surface);
                    SetPositionSymmetric(_dragNode, rigPoint);
                    break;

                case DragKind.PoseNode:
                    PoseTowards(_dragNode, rigPoint);
                    break;
            }
        }

        private void EndDrag()
        {
            _drag = DragKind.None;
            _dragParent = ZSphereRig.NoNode;
            _dragNode = ZSphereRig.NoNode;
            // The whole drag is one undo step, so it closes here rather than per frame.
            CommitRigEdit();
        }

        /// The plane a drag reads its position off: through the anchor point, facing the camera.
        /// A view-aligned plane is the only choice that lets a single 2D drag place a point in 3D
        /// without a modifier key, and it is what every sculpting app's grab tools use.
        private bool TryDragPoint(Ray ray, out Vector3 worldPoint)
        {
            var plane = new Plane(-_cam.transform.forward, _dragPlanePoint);
            if (plane.Raycast(ray, out float enter))
            {
                worldPoint = ray.GetPoint(enter);
                return true;
            }
            worldPoint = default;
            return false;
        }

        private int PickNode(Ray ray)
        {
            RaycastHit[] hits = Physics.RaycastAll(ray, 1000f);
            int best = ZSphereRig.NoNode;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                // Filters by tag rather than by layer: every sculptable object in the scene
                // carries a MeshCollider too, and a rig sphere is not on a layer of its own.
                var tag = hits[i].collider.GetComponent<ZSphereNodeTag>();
                if (tag == null || !_rig.IsAlive(tag.Index)) continue;
                if (hits[i].distance >= bestDistance) continue;
                bestDistance = hits[i].distance;
                best = tag.Index;
            }
            return best;
        }

        /// The nearest link under the cursor, as the CHILD node naming it, plus where on it the
        /// ray landed. Only consulted once PickNode has come up empty, so a sphere always wins
        /// over the tube running into it - the spheres are the things you grab, and a link is a
        /// large target that would otherwise steal clicks meant for the joint at its end.
        private int PickLink(Ray ray, out Vector3 worldPoint)
        {
            worldPoint = Vector3.zero;
            RaycastHit[] hits = Physics.RaycastAll(ray, 1000f);
            int best = ZSphereRig.NoNode;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < hits.Length; i++)
            {
                var tag = hits[i].collider.GetComponent<ZSphereLinkTag>();
                if (tag == null || !_rig.IsAlive(tag.ChildIndex)) continue;
                if (hits[i].distance >= bestDistance) continue;
                bestDistance = hits[i].distance;
                best = tag.ChildIndex;
                worldPoint = hits[i].point;
            }
            return best;
        }

        /// Adds a sphere partway along the limb the user clicked, sized to blend with the two it
        /// sits between, and selects it so the radius slider (and scroll-wheel) act on it
        /// immediately - adding mass to a forearm means adding a sphere AND growing it, and
        /// leaving the selection on whatever was selected before would make that second half
        /// silently act on the wrong sphere.
        private void InsertOnLink(int childIndex, Vector3 worldPoint)
        {
            ZSphereRig.Node child = _rig.Get(childIndex);
            if (child == null) return;
            ZSphereRig.Node parent = _rig.Get(child.Parent);
            if (parent == null) return;

            // Projected onto the segment rather than used raw: the ray hits the SURFACE of the
            // tube, which is a radius off the centre line, so inserting there would kink the limb
            // sideways by exactly the amount the tube is thick.
            Vector3 rigPoint = WorldToRig(worldPoint);
            Vector3 axis = child.Position - parent.Position;
            float lengthSqr = axis.sqrMagnitude;
            float t = lengthSqr > 1e-12f
                ? Mathf.Clamp01(Vector3.Dot(rigPoint - parent.Position, axis) / lengthSqr)
                : 0.5f;

            Vector3 position = parent.Position + axis * t;
            float radius = Mathf.Lerp(parent.Radius, child.Radius, t);

            BeginRigEdit("Insert Sphere");
            int inserted = InsertBetweenSymmetric(childIndex, position, radius);
            if (inserted != ZSphereRig.NoNode) SelectedNode = inserted;
            CommitRigEdit();
        }

        // ------------------------------------------------------------------ rig operations

        /// Places the first sphere, on the view plane through the camera's own orbit pivot - so it
        /// lands in the middle of the view at the depth the user is already looking at, rather
        /// than at the world origin, which may be off screen entirely.
        ///
        /// Anchors the rig - and so its mirror plane - to the object being sculpted, NOT to the
        /// click. Every symmetry op reflects through rig-local x=0 (MirrorPosition), so wherever
        /// this transform sits IS the mirror plane, and the plane the user is already looking at
        /// belongs to the sculpt object (MirrorController draws it at that object's own origin).
        /// Anchoring anywhere else invents a second, competing X axis a few centimetres from the
        /// first - two red planes on screen and a rig mirrored about the wrong one.
        ///
        /// (The original bug was worse: the transform stayed at the WORLD origin while the root
        /// landed wherever you clicked, so IsCentral(root) was false, TryCreateTwin bailed at its
        /// twinParent check, and symmetry silently did nothing at all.)
        private void PlaceRoot(Ray ray)
        {
            Vector3 worldPoint = RootPlacementPoint(ray);
            BeginRigEdit("Place Sphere");

            // Only ever moved for the FIRST sphere. Every stored position is rig-local, so
            // re-seating the transform once anything exists would drag the whole blockout with
            // it - that case needs the compensation ReanchorSymmetryPlane does, and an appendage
            // root dropped onto an attached body must not disturb the rig it is joining.
            if (_rig.IsEmpty)
            {
                Transform anchor = SymmetryAnchor();
                if (anchor != null) _rigRoot.SetPositionAndRotation(anchor.position, anchor.rotation);
                else _rigRoot.SetPositionAndRotation(worldPoint, Quaternion.identity);
                SyncAttachReference();
            }

            // Snapped only within CentreSnap of the plane, not onto it from anywhere: a blockout
            // does not have to be built outwards from the centreline, and a sphere that jumped to
            // the middle from wherever it was clicked would feel like the tool fighting the user.
            // Inside the band it is what keeps a spine-first rig single instead of instantly
            // twinning. Off-centre, symmetry still applies - TryCreateTwin mirrors a root into a
            // second root, so an arm started out at the shoulder gets its opposite number.
            float radius = DefaultRootRadius();
            SelectedNode = _rig.AddRoot(SnapToCentre(WorldToRig(worldPoint), radius), radius);
            TryCreateTwin(SelectedNode);
            CommitRigEdit();
        }

        /// Where a click would put the first sphere: on the view plane through the camera's own
        /// orbit pivot, so it lands in the middle of the view at the depth the user is already
        /// looking at rather than at the world origin, which may be off screen entirely. Shared
        /// with the placement cursor so what is previewed is exactly what a click produces.
        private Vector3 RootPlacementPoint(Ray ray)
        {
            // On the attached body if the ray lands on it - "click the torso, get a sphere on the
            // torso" is the whole reason to attach, and no view-plane depth guess can match it.
            if (TrySurfacePoint(ray, out Vector3 surface)) return surface;
            return ViewPlanePoint(ray);
        }

        /// The fallback half of RootPlacementPoint: the view plane through the camera's orbit
        /// pivot. Split out so the placement cursor can reuse a surface hit it has already paid
        /// for without re-running the whole decision.
        private Vector3 ViewPlanePoint(Ray ray)
        {
            Vector3 viewAnchor = Vector3.zero;
            CameraOrbitController orbit = Orbit;
            if (orbit != null) orbit.GetView(out _, out _, out _, out viewAnchor);

            var viewPlane = new Plane(-_cam.transform.forward, viewAnchor);
            return viewPlane.Raycast(ray, out float enter) ? ray.GetPoint(enter) : viewAnchor;
        }

        // Cached rather than found per frame - the placement cursor asks for this every frame the
        // rig is empty, and the reference never changes.
        private CameraOrbitController _orbit;
        private CameraOrbitController Orbit =>
            _orbit != null ? _orbit : (_orbit = FindFirstObjectByType<CameraOrbitController>());

        /// The transform whose origin and orientation define the mirror plane: the object being
        /// sculpted. Prefers the current selection - that is the object whose mirror plane is on
        /// screen - and falls back to the scene's anchor sphere, the same object MeshMirror
        /// reflects across for "Mirror Selected Across Sphere". Null when the scene is empty, in
        /// which case the rig falls back to standing on its own.
        private Transform SymmetryAnchor()
        {
            // An explicit attach beats the selection: the user named the body this rig belongs to,
            // and clicking some other object in the scene panel must not silently move the plane
            // the limbs are being mirrored about.
            if (_attachTarget != null) return _attachTarget.transform;

            SculptableMesh primary = Selection != null ? Selection.PrimarySelection : null;
            if (primary != null) return primary.transform;

            var spawner = FindFirstObjectByType<PrimitiveSpawner>();
            SculptableMesh main = spawner != null ? spawner.MainObject : null;
            return main != null ? main.transform : null;
        }

        /// Re-seats an EXISTING rig's mirror plane on the sculpt object, leaving every sphere
        /// exactly where it is in the world.
        ///
        /// The compensation is the whole point: node positions are rig-local, so moving the
        /// transform alone would drag the entire blockout across the scene. Capturing world
        /// positions first and writing them back afterwards moves only the plane. Without this a
        /// rig started before the plane was anchored correctly - or started with no object in the
        /// scene - could only be fixed by clearing it and beginning again.
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
            InvalidatePreview();
            CommitRigEdit();
            return true;
        }

        /// Sized off whatever is already in the scene, the same way PrimitiveSpawner sizes a new
        /// primitive - a rig whose spheres arrive a hundredth or a hundred times the size of the
        /// model next to them is unusable before it starts.
        private float DefaultRootRadius()
        {
            // An existing rig is the better reference than anything in the scene: an appendage
            // root dropped onto an attached body wants to match the spheres it will sit beside,
            // not the body it is landing on.
            if (!_rig.IsEmpty) return Mathf.Clamp(_rig.MeanRadius(), MinNodeRadius, MaxNodeRadius);

            var spawner = FindFirstObjectByType<PrimitiveSpawner>();
            SculptableMesh main = spawner != null ? spawner.MainObject : null;
            if (main == null || main.Mesh == null) return 0.25f;

            Vector3 e = main.Mesh.bounds.extents;
            Vector3 s = main.transform.lossyScale;
            float avg = ((e.x + e.y + e.z) / 3f) * ((s.x + s.y + s.z) / 3f);
            return Mathf.Clamp(avg * 0.35f, 0.02f, 1f);
        }

        /// Deletes a sphere, its subtree, and - under symmetry - the matching subtree on the other
        /// side. Deleting only one arm of a symmetric pair would leave the rig in a state the
        /// symmetric editing rules can no longer describe, and is never what the user meant by
        /// "remove this limb".
        public void DeleteNode(int index)
        {
            if (!_rig.IsAlive(index)) return;

            BeginRigEdit("Delete Sphere");
            int twin = _rig.TwinOf(index);
            _rig.Remove(index);
            if (SymmetryX && _rig.IsAlive(twin)) _rig.Remove(twin);

            if (!_rig.IsAlive(SelectedNode)) SelectedNode = ZSphereRig.NoNode;
            InvalidatePreview();
            CommitRigEdit();
        }

        /// Throws the whole blockout away. Undoable (Z, or the panel's Undo button) - this is the
        /// single most destructive thing in the tool, and it is one click away from the buttons
        /// next to it.
        public void ClearRig()
        {
            if (_rig.IsEmpty) return;
            BeginRigEdit("Clear ZSpheres");
            ClearRigInternal();
            CommitRigEdit();
        }

        /// The clear itself, with no undo step. Convert uses this: the step it records is its own
        /// (see RecordConvertUndo), which puts the rig back as part of un-converting, and a
        /// second, rig-local step describing half of that would undo the two out of sync.
        private void ClearRigInternal()
        {
            _rig.Clear();
            SelectedNode = ZSphereRig.NoNode;
            EndDrag();
            InvalidatePreview();
        }

        /// Radius of the selected sphere, for the UI's slider. Returns 0 with nothing selected,
        /// which the UI reads as "nothing to scale".
        public float SelectedRadius
        {
            get
            {
                ZSphereRig.Node node = _rig.Get(SelectedNode);
                return node != null ? node.Radius : 0f;
            }
            set
            {
                // Coalesced for the same reason the scroll wheel is: this is bound to a UI slider,
                // which fires continuously while it is dragged.
                if (_rig.IsAlive(SelectedNode)) BeginRigEdit("Scale Sphere", CoalesceSeconds);
                SetRadiusSymmetric(SelectedNode, value);
            }
        }

        // -------------------------------------------------------------------- symmetry ops

        private static Vector3 MirrorPosition(Vector3 p) => new Vector3(-p.x, p.y, p.z);

        /// Reflection of a rotation through the x=0 plane. A reflected rotation turns about the
        /// reflected axis in the OPPOSITE direction, which for a quaternion is exactly negating
        /// the two components perpendicular to the mirror normal - so posing a left arm downward
        /// swings the right one downward too, not upward.
        private static Quaternion MirrorRotation(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        /// Whether a sphere sits ON the mirror plane, and so is its own reflection - a spine
        /// sphere, as against a shoulder. Judged relative to the sphere's own radius rather than
        /// against a fixed epsilon, because rigs are built at wildly different scales and a
        /// tolerance that is generous for a torso is meaningless for a fingertip.
        private bool IsCentral(ZSphereRig.Node node) => IsCentralPoint(node.Position, node.Radius);

        /// Whether a point is inside the centre band for a sphere of this radius. Judged relative
        /// to the sphere's own radius rather than against a fixed epsilon, because rigs are built
        /// at wildly different scales and a tolerance that is generous for a torso is meaningless
        /// for a fingertip.
        private bool IsCentralPoint(Vector3 position, float radius) =>
            Mathf.Abs(position.x) <= radius * Mathf.Max(_centreSnap, 0.001f);

        /// Pulls a position exactly onto the mirror plane when it is inside the centre band, so a
        /// centre-line sphere is EXACTLY central rather than a hair off it. See CentreSnap.
        private Vector3 SnapToCentre(Vector3 position, float radius)
        {
            if (!SymmetryX || _centreSnap <= 0f) return position;
            if (Mathf.Abs(position.x) > radius * _centreSnap) return position;
            position.x = 0f;
            return position;
        }

        /// Merges a mirrored pair back into one central sphere when the user drags it onto the
        /// plane - the other half of CentreSnap, and what makes a split that WAS wanted reversible
        /// by simply pushing the sphere back to the middle.
        ///
        /// Deliberately narrow. It only fires when the twin is a childless leaf hanging off the
        /// same parent, because those are the only two spheres whose merge is unambiguous: with
        /// children below, or with the pair descending from two different (mirrored) parents,
        /// dropping one side does not produce the pair's shape minus a duplicate - it silently
        /// deletes a branch. Everything else keeps the pair, and the user can delete a side.
        private bool TryDissolveTwin(int index, int twin)
        {
            ZSphereRig.Node node = _rig.Get(index);
            ZSphereRig.Node twinNode = _rig.Get(twin);
            if (node == null || twinNode == null) return false;
            if (twinNode.Children.Count > 0 || node.Children.Count > 0) return false;
            if (twinNode.Parent != node.Parent) return false;

            _rig.Remove(twin); // unlinks the pair on its way out
            if (SelectedNode == twin) SelectedNode = index;
            return true;
        }

        /// Adds a child and, under symmetry, its reflection under the parent's own reflection.
        /// Returns the child the user is actually dragging.
        private int AddChildSymmetric(int parent, Vector3 position, float radius)
        {
            int child = _rig.AddChild(parent, SnapToCentre(position, radius), radius);
            if (child == ZSphereRig.NoNode) return child;
            TryCreateTwin(child);
            return child;
        }

        /// Gives `index` a mirror twin if symmetry is on, it has not got one, and it has moved far
        /// enough off the mirror plane to have a distinct reflection. Returns quietly otherwise.
        ///
        /// Called on every frame of a create-drag, not just at creation, and that is the point. A
        /// child is born the instant the drag passes the few-pixel threshold, which when pulling a
        /// limb off a CENTRAL sphere (a spine, a chest - the overwhelmingly common case) is still
        /// practically on the mirror plane. Deciding symmetry once at that instant would mean arms
        /// pulled off a torso essentially never got mirrored, which is the single thing the
        /// symmetry toggle exists to do. Deferring until the sphere is genuinely off-centre gets
        /// it right, and costs one cheap check per drag frame.
        ///
        /// The reverse is deliberately NOT done: dragging a twinned sphere back onto the plane
        /// does not dissolve the pair. Spheres silently vanishing under the cursor mid-drag is far
        /// more startling than a redundant pair the user can simply delete.
        private void TryCreateTwin(int index)
        {
            if (!SymmetryX) return;
            ZSphereRig.Node node = _rig.Get(index);
            if (node == null || _rig.TwinOf(index) != ZSphereRig.NoNode) return;
            if (IsCentral(node)) return;

            ZSphereRig.Node parentNode = _rig.Get(node.Parent);
            if (parentNode == null)
            {
                // An off-centre ROOT becomes a mirrored pair of roots. Without this a rig started
                // anywhere but the centreline could never mirror at all: a root has no parent to
                // hang a copy under, so it stayed single, and every child then failed the
                // twinParent test below and stayed single too. Two disconnected trees are exactly
                // right for the case that produces them - an arm placed out at the shoulder wants
                // an opposite arm, not a bridge across the chest.
                int rootTwin = _rig.AddRoot(MirrorPosition(node.Position), node.Radius);
                if (rootTwin != ZSphereRig.NoNode) _rig.LinkTwins(index, rootTwin);
                return;
            }

            // A child of a central sphere mirrors under that same sphere (both arms hang off the
            // one chest sphere); a child of an off-centre sphere mirrors under that sphere's twin.
            int twinParent = _rig.TwinOf(node.Parent);
            if (twinParent == ZSphereRig.NoNode) twinParent = IsCentral(parentNode) ? node.Parent : ZSphereRig.NoNode;
            if (twinParent == ZSphereRig.NoNode) return;

            int twin = _rig.AddChild(twinParent, MirrorPosition(node.Position), node.Radius);
            if (twin != ZSphereRig.NoNode) _rig.LinkTwins(index, twin);
        }

        /// Inserts a sphere into the link above `child`, and - under symmetry - the matching one
        /// into the mirrored chain, keeping the two sides structurally identical. Returns the new
        /// node so the caller can select it, since the whole point of adding it is to then scale
        /// it.
        ///
        /// The twin is inserted with the reflected position rather than by mirroring afterwards,
        /// because TryCreateTwin can only hang a copy under a parent that already has one - and
        /// the node this creates is brand new on both sides at once.
        private int InsertBetweenSymmetric(int child, Vector3 position, float radius)
        {
            position = SnapToCentre(position, radius);
            int inserted = _rig.InsertBetween(child, position, radius);
            if (inserted == ZSphereRig.NoNode) return ZSphereRig.NoNode;

            // A snapped-to-centre insertion gets NO twin: its reflection is itself, and inserting
            // one anyway would stack a second sphere in exactly the same place.
            int childTwin = _rig.TwinOf(child);
            if (SymmetryX && !IsCentralPoint(position, radius) && _rig.IsAlive(childTwin))
            {
                int twin = _rig.InsertBetween(childTwin, MirrorPosition(position), radius);
                if (twin != ZSphereRig.NoNode) _rig.LinkTwins(inserted, twin);
            }

            InvalidatePreview();
            return inserted;
        }

        /// Retroactively twins every off-centre sphere that has not got a twin, so a rig built
        /// with symmetry off - or built before the mirror plane was anchored to the root - can be
        /// made symmetric in one press instead of being rebuilt from scratch.
        ///
        /// Walks in index order, which is exactly the order the twins need: a node is always
        /// appended after its parent, so by the time this reaches a child its parent has already
        /// been given whatever twin it was going to get, and TryCreateTwin's "hang the copy under
        /// the parent's twin" rule can find it. Iterating to the ORIGINAL count keeps the pass off
        /// the twins it is itself appending - they already have a twin (each other), so visiting
        /// them would be harmless but pointless.
        ///
        /// Not recorded in undo, matching every other rig edit - the rig is a blockout that is not
        /// itself undoable until it becomes a mesh at Convert (see RecordConvertUndo).
        public int MirrorRig()
        {
            if (!SymmetryX) SymmetryX = true;

            BeginRigEdit("Mirror Rig");
            int originalCount = _rig.Count;
            int created = 0;

            for (int i = 0; i < originalCount; i++)
            {
                if (!_rig.IsAlive(i)) continue;
                if (_rig.TwinOf(i) != ZSphereRig.NoNode) continue;

                int before = _rig.Count;
                TryCreateTwin(i);
                if (_rig.Count > before) created++;
            }

            if (created > 0) InvalidatePreview();
            CommitRigEdit();
            return created;
        }

        /// How many spheres are off the mirror plane with no counterpart - what MirrorRig would
        /// act on. Lets the UI say whether pressing it will do anything before it is pressed.
        public int UnmirroredCount()
        {
            int count = 0;
            for (int i = 0; i < _rig.Count; i++)
            {
                ZSphereRig.Node node = _rig.Get(i);
                if (node == null || IsCentral(node)) continue;
                if (_rig.TwinOf(i) == ZSphereRig.NoNode) count++;
            }
            return count;
        }

        private void SetPositionSymmetric(int index, Vector3 position)
        {
            ZSphereRig.Node node = _rig.Get(index);
            if (node == null) return;

            if (SymmetryX)
            {
                int existing = _rig.TwinOf(index);
                if (existing == ZSphereRig.NoNode)
                {
                    // Untwinned and near the middle: pin it there, which is what stops a chain
                    // extruded down a torso from splitting the moment the mouse wanders.
                    position = SnapToCentre(position, node.Radius);
                }
                else if (IsCentralPoint(position, node.Radius) && TryDissolveTwin(index, existing))
                {
                    position.x = 0f;
                }
            }

            _rig.SetPosition(index, position);
            int twin = _rig.TwinOf(index);
            if (SymmetryX && twin != ZSphereRig.NoNode) _rig.SetPosition(twin, MirrorPosition(position));
            InvalidatePreview();
        }

        private void SetRadiusSymmetric(int index, float radius)
        {
            radius = Mathf.Clamp(radius, MinNodeRadius, MaxNodeRadius);
            _rig.SetRadius(index, radius);
            int twin = _rig.TwinOf(index);
            if (SymmetryX && twin != ZSphereRig.NoNode) _rig.SetRadius(twin, radius);
            InvalidatePreview();
        }

        /// Swings `index` (and everything below it) so it points at `target`, about its parent
        /// joint - bone length preserved, because the whole subtree is rotated rather than moved.
        /// A root has no joint, so it translates instead.
        private void PoseTowards(int index, Vector3 target)
        {
            ZSphereRig.Node node = _rig.Get(index);
            if (node == null) return;

            if (!_rig.IsAlive(node.Parent))
            {
                Vector3 delta = target - node.Position;
                _rig.TranslateSubtree(index, delta);
                int rootTwin = _rig.TwinOf(index);
                if (SymmetryX && rootTwin != ZSphereRig.NoNode)
                    _rig.TranslateSubtree(rootTwin, MirrorPosition(delta));
                InvalidatePreview();
                return;
            }

            Vector3 pivot = _rig.Nodes[node.Parent].Position;
            Vector3 from = node.Position - pivot;
            Vector3 to = target - pivot;
            if (from.sqrMagnitude < 1e-10f || to.sqrMagnitude < 1e-10f) return;

            Quaternion rotation = Quaternion.FromToRotation(from, to);
            _rig.RotateSubtree(index, rotation, pivot);

            int twin = _rig.TwinOf(index);
            if (SymmetryX && twin != ZSphereRig.NoNode)
            {
                ZSphereRig.Node twinNode = _rig.Get(twin);
                if (twinNode != null && _rig.IsAlive(twinNode.Parent))
                    _rig.RotateSubtree(twin, MirrorRotation(rotation), _rig.Nodes[twinNode.Parent].Position);
            }

            InvalidatePreview();
        }

        // ------------------------------------------------------------------------- rig undo

        /// The rig gets its own undo stack, separate from the scene-wide EditHistory, and this is
        /// deliberate rather than a shortcut.
        ///
        /// EditHistory's steps are about OBJECTS - a mesh delta on a named SculptableMesh, or a
        /// scene action that creates or parks one. The rig is neither: it is a scaffold that does
        /// not exist in the scene list, is not saved, and produces an object exactly once, at
        /// Convert (which IS an EditHistory step, and stays one). Pushing forty sphere-drags onto
        /// the shared stack would bury the mesh edits either side of them under scaffold churn
        /// nobody thinks of as scene history, and would give a rig snapshot a claim on the global
        /// memory budget it has no business competing for.
        ///
        /// The two stacks never both answer a press: Z runs the rig's history while the ZSphere
        /// tool is up and has something to undo, and the scene's otherwise - see HandlesUndoKey,
        /// which SculptController asks before running its own.

        public bool CanUndoRig => _rigUndo.Count > 0 || HasOpenEdit;
        public bool CanRedoRig => _rigRedo.Count > 0;
        public int RigUndoDepth => _rigUndo.Count + (HasOpenEdit ? 1 : 0);

        /// What Undo would reverse, for the panel's button label. Null with nothing to undo.
        public string NextRigUndoLabel =>
            HasOpenEdit ? _pendingLabel :
            _rigUndo.Count > 0 ? _rigUndo[_rigUndo.Count - 1].Label : null;

        /// An edit that has been snapshotted AND has actually changed the rig, but has not closed
        /// yet - a drag still in flight, or a coalescing resize inside its window. It counts as
        /// undoable because from the user's point of view it has already happened.
        private bool HasOpenEdit => _pendingSnapshot != null && _rig.Version != _pendingVersion;

        /// Whether this tool - not the scene-wide history - should answer a Z press right now.
        /// Asked by SculptController.HandleUndoRedoKeys as well as by our own HandleInput, so
        /// exactly one of the two responds. Falls through to the scene when the rig has nothing
        /// left, which is what makes Z keep working after the last sphere edit is undone.
        public bool HandlesUndoKey(bool redo)
        {
            if (Gizmo == null || Gizmo.Mode != GizmoMode.ZSphere) return false;
            return redo ? CanRedoRig : CanUndoRig;
        }

        /// Opens an undo step: snapshots the rig now, to be pushed by CommitRigEdit if - and only
        /// if - the rig actually changed in between.
        ///
        /// Lazy commit is what makes this safe to call on every mouse press. An Add-mode press is
        /// a plain selection click far more often than it is a drag, and a stack full of
        /// no-op "Add Sphere" entries would mean pressing Z several times before anything moved.
        ///
        /// Re-entrant by design: a nested call extends the open step rather than starting a
        /// second one, so a resize that runs into a drag lands as one entry instead of two halves.
        /// `coalesceSeconds` above zero also lets the step close on a timer (see
        /// TickPendingRigEdit) for input that arrives as a stream - wheel notches, a slider.
        private void BeginRigEdit(string label, float coalesceSeconds = 0f)
        {
            if (_pendingSnapshot != null)
            {
                if (coalesceSeconds > 0f) _pendingCommitAt = Time.unscaledTime + coalesceSeconds;
                return;
            }

            _pendingSnapshot = _rig.Snapshot();
            _pendingSelection = SelectedNode;
            _pendingVersion = _rig.Version;
            _pendingLabel = label;
            _pendingCommitAt = coalesceSeconds > 0f ? Time.unscaledTime + coalesceSeconds : float.MaxValue;
        }

        private void CommitRigEdit()
        {
            if (_pendingSnapshot == null) return;

            if (_rig.Version != _pendingVersion)
            {
                _rigUndo.Add(new RigUndoStep
                {
                    Rig = _pendingSnapshot,
                    Selection = _pendingSelection,
                    Label = _pendingLabel
                });
                while (_rigUndo.Count > MaxRigUndoSteps) _rigUndo.RemoveAt(0);
                // A fresh edit invalidates the redo chain, exactly as EditHistory does.
                _rigRedo.Clear();
            }

            _pendingSnapshot = null;
            _pendingCommitAt = float.MaxValue;
        }

        /// Closes a coalescing edit whose window has run out. Never closes one belonging to a
        /// drag: a slow drag can easily sit still for longer than the window, and splitting it
        /// there would leave half of it unreachable behind an extra Z press.
        private void TickPendingRigEdit()
        {
            if (_pendingSnapshot == null || _drag != DragKind.None) return;
            if (Time.unscaledTime >= _pendingCommitAt) CommitRigEdit();
        }

        /// Reverses the last rig operation. Returns false with nothing to undo, which is the
        /// signal HandlesUndoKey uses to hand the key back to the scene-wide history.
        public bool UndoRig()
        {
            // Anything still open is closed first, so the operation the user just finished is
            // what gets reversed rather than the one before it.
            CommitRigEdit();
            return TakeRigStep(_rigUndo, _rigRedo);
        }

        public bool RedoRig() => TakeRigStep(_rigRedo, _rigUndo);

        /// Pops `from`, pushes the CURRENT state onto `to`, and restores the popped one. Symmetric
        /// in both directions, so undo and redo are the same code walked opposite ways.
        private bool TakeRigStep(List<RigUndoStep> from, List<RigUndoStep> to)
        {
            if (from.Count == 0) return false;

            int last = from.Count - 1;
            RigUndoStep step = from[last];
            from.RemoveAt(last);

            to.Add(new RigUndoStep { Rig = _rig.Snapshot(), Selection = SelectedNode, Label = step.Label });

            EndDrag();
            _rig.Restore(step.Rig);
            // Indices survive a Restore (the rig tombstones rather than compacts), so the stored
            // selection still names the same sphere - but it may have been a sphere that only
            // exists in the OTHER direction, hence the liveness check.
            SelectedNode = _rig.IsAlive(step.Selection) ? step.Selection : ZSphereRig.NoNode;
            InvalidatePreview();
            return true;
        }

        // -------------------------------------------------------------------------- attaching

        /// The object this rig is building onto, or null. See AttachToObject.
        public SculptableMesh AttachTarget => _attachTarget;
        public string AttachTargetName => _attachTarget != null ? _attachTarget.name : null;

        /// Binds the rig to an existing object, so limbs can be blocked out ON a body that is
        /// already sculpted.
        ///
        /// Three things follow from it, and they are the whole feature:
        /// - clicks land on that object's SURFACE rather than on a guessed view-plane depth, so a
        ///   shoulder sphere goes exactly where the shoulder is (SnapToSurface, TrySurfacePoint);
        /// - a click on the surface starts a NEW root even with a rig already up, which is how the
        ///   second pair of limbs gets added (see BeginDrag);
        /// - the rig follows that object around the scene (FollowAttachTarget), and mirrors about
        ///   ITS plane rather than the selection's (SymmetryAnchor).
        ///
        /// Attaching does NOT make the skin part of that object - Convert still produces a
        /// separate, independently sculptable mesh, the same as it always did. The attachment is
        /// about where the blockout is built, not about what it becomes.
        public bool AttachToObject(SculptableMesh target)
        {
            if (target == null) return false;

            _attachTarget = target;
            SyncAttachReference();

            // An empty rig has no geometry to preserve, so its plane is simply seated on the new
            // host; an existing one is re-anchored, which moves the plane while leaving every
            // sphere exactly where it is in the world.
            if (_rig.IsEmpty)
            {
                EnsureRigRoot();
                _rigRoot.SetPositionAndRotation(target.transform.position, target.transform.rotation);
                SyncAttachReference();
            }
            else
            {
                ReanchorSymmetryPlane();
            }

            InvalidatePreview();
            return true;
        }

        /// Cuts the rig loose. Leaves every sphere where it is - detaching is about what the rig
        /// tracks from now on, not about undoing the blockout built while it was attached.
        public void DetachFromObject() => _attachTarget = null;

        private void SyncAttachReference()
        {
            if (_attachTarget == null) return;
            _attachLastPos = _attachTarget.transform.position;
            _attachLastRot = _attachTarget.transform.rotation;
        }

        /// Carries the rig along when the object it is attached to is moved or turned.
        ///
        /// A rigid delta applied to the rig ROOT, rather than parenting the root to that
        /// transform. Parenting would put the object's scale into rig space - every radius, every
        /// bound the skinner sizes its voxel grid from, and the extent the mirror plane is drawn
        /// at are rig-local lengths that assume unit scale - and would take the whole rig with the
        /// object if it were ever deleted. Following costs one transform write on the frames the
        /// object actually moves and has neither problem. Scale is deliberately not followed: the
        /// blockout keeps its own size.
        private void FollowAttachTarget()
        {
            if (_attachTarget == null || _rigRoot == null) return;

            Transform target = _attachTarget.transform;
            if (target.position == _attachLastPos && target.rotation == _attachLastRot) return;

            Quaternion delta = target.rotation * Quaternion.Inverse(_attachLastRot);
            _rigRoot.SetPositionAndRotation(
                target.position + delta * (_rigRoot.position - _attachLastPos),
                delta * _rigRoot.rotation);

            SyncAttachReference();
        }

        /// Where `ray` meets the attach target's surface, if it meets it at all. False whenever
        /// there is nothing attached or surface snapping is off, which is what makes every caller
        /// fall back to its old view-plane behaviour without a second branch.
        private bool TrySurfacePoint(Ray ray, out Vector3 worldPoint)
        {
            worldPoint = default;
            if (_attachTarget == null || !SnapToSurface) return false;

            Transform target = _attachTarget.transform;
            RaycastHit[] hits = Physics.RaycastAll(ray, 1000f);
            float bestDistance = float.MaxValue;
            bool found = false;

            for (int i = 0; i < hits.Length; i++)
            {
                // Compared by transform rather than by collider, so it still matches if the object
                // ever grows a second collider - and so nothing else in the scene can answer for
                // it, which a layer mask alone would not guarantee here (rig handles, the sculpt
                // objects and the attach target all share the default layer).
                if (hits[i].collider.transform != target) continue;
                if (hits[i].distance >= bestDistance) continue;
                bestDistance = hits[i].distance;
                worldPoint = hits[i].point;
                found = true;
            }

            // The sphere centre goes ON the surface rather than being pushed out along the normal,
            // so it sits half-buried in the body - which is what makes the skinner's smooth
            // minimum fuse the limb into the torso instead of leaving it balanced on top.
            return found;
        }

        // ---------------------------------------------------------------------- rig lifecycle

        /// Starts a blockout from nothing - the ZSphere entry in Add Primitive. Arms the tool and
        /// drops the first sphere at the centre of the view (or on the attached body's surface),
        /// so a model can be built entirely out of ZSpheres with no object in the scene to click
        /// on first.
        ///
        /// Never clears an existing rig. A button sitting among Cube/Sphere/Cylinder reads as
        /// "add one", and having it silently destroy a blockout would be the single worst thing in
        /// the tool. With a rig already up it just arms the tool and says so - returns false.
        public bool StartNewRig()
        {
            EnsureRigRoot();
            EditMode = ZSphereEditMode.Add;
            Gizmo?.SetMode(GizmoMode.ZSphere);
            if (!_rig.IsEmpty) return false;

            // The same point a click in the middle of the viewport would produce - the orbit
            // pivot's depth - so the sphere lands where the user is already looking.
            Vector3 world = Vector3.zero;
            CameraOrbitController orbit = Orbit;
            if (orbit != null) orbit.GetView(out _, out _, out _, out world);

            Transform anchor = SymmetryAnchor();
            if (anchor != null) _rigRoot.SetPositionAndRotation(anchor.position, anchor.rotation);
            else _rigRoot.SetPositionAndRotation(world, Quaternion.identity);
            SyncAttachReference();

            BeginRigEdit("New ZSphere Rig");
            float radius = DefaultRootRadius();
            SelectedNode = _rig.AddRoot(SnapToCentre(WorldToRig(world), radius), radius);
            InvalidatePreview();
            CommitRigEdit();
            return true;
        }

        /// Pulls the selected sphere onto the mirror plane, merging it with its twin where that is
        /// unambiguous - the repair for a chain that already split before CentreSnap was raised.
        /// Returns what happened, for the panel's status line.
        public string CentreSelected()
        {
            ZSphereRig.Node node = _rig.Get(SelectedNode);
            if (node == null) return "Select a sphere first.";
            if (Mathf.Approximately(node.Position.x, 0f) && _rig.TwinOf(SelectedNode) == ZSphereRig.NoNode)
                return "That sphere is already on the centre line.";

            BeginRigEdit("Centre Sphere");
            int twin = _rig.TwinOf(SelectedNode);
            bool merged = twin != ZSphereRig.NoNode && TryDissolveTwin(SelectedNode, twin);

            Vector3 centred = node.Position;
            centred.x = 0f;
            _rig.SetPosition(SelectedNode, centred);

            int remaining = _rig.TwinOf(SelectedNode);
            if (remaining != ZSphereRig.NoNode) _rig.SetPosition(remaining, centred);

            InvalidatePreview();
            CommitRigEdit();

            if (merged) return "Merged the mirrored pair into one centre-line sphere.";
            return remaining != ZSphereRig.NoNode
                ? "Moved to the centre line - its twin has children, so both were kept. Delete one to merge."
                : "Moved onto the centre line.";
        }

        // ------------------------------------------------------------------------- skinning

        /// Re-skins now, regardless of the live-preview throttle - the UI's explicit "Update
        /// Skin" button, and the way to see a result at all with LivePreview off.
        public void RebuildSkinNow()
        {
            _lastSkinnedVersion = _rig.Version;
            _nextAllowedSkin = Time.unscaledTime + SkinRebuildInterval;
            Skin();
        }

        /// Forces the next allowed RefreshPreview to re-skin. Needed because the SETTINGS
        /// (resolution, blend, smoothing) change what a skin looks like without touching the rig,
        /// so RefreshPreview's Rig.Version poll alone would leave a slider drag showing a preview
        /// built from the old values. Rig edits route through here too - harmless, since forcing a
        /// rebuild is what the version divergence was going to do anyway.
        private void InvalidatePreview() => _lastSkinnedVersion = -1;

        private void RefreshPreview()
        {
            if (!LivePreview)
            {
                // A stale preview left hanging around after the toggle goes off would be actively
                // misleading, since it describes a rig that has since been edited.
                if (_previewGO != null && _rig.Version != _lastSkinnedVersion) DestroyPreview();
                return;
            }

            // Never re-skin mid-drag. Skinning takes tens of milliseconds even at modest
            // resolutions, and paying that on every frame of a drag would make placing a sphere -
            // the single most common action in the whole workflow - feel broken. The release
            // frame picks up whatever the drag ended on.
            if (_drag != DragKind.None) return;

            if (_rig.Version == _lastSkinnedVersion) return;
            if (Time.unscaledTime < _nextAllowedSkin) return;

            _lastSkinnedVersion = _rig.Version;
            _nextAllowedSkin = Time.unscaledTime + SkinRebuildInterval;
            Skin();
        }

        private void Skin()
        {
            Mesh mesh = ZSphereSkinner.Skin(_rig, _settings, out int triangleCount, out string error);
            Error = error;
            PreviewTriangleCount = triangleCount;

            if (mesh == null)
            {
                DestroyPreview();
                return;
            }

            if (_previewMesh != null) Destroy(_previewMesh);
            _previewMesh = mesh;

            EnsurePreviewObject();
            _previewGO.GetComponent<MeshFilter>().sharedMesh = _previewMesh;
        }

        private void EnsurePreviewObject()
        {
            if (_previewGO != null) return;

            // Parented under the rig root, so the skin - which the skinner produces in rig-local
            // space - lands exactly on the spheres that generated it with no transform maths.
            // Carries no collider and no SculptableMesh: it must not be pickable by PickNode, and
            // it must not register with SelectionManager as a real object.
            _previewGO = new GameObject("ZSpherePreview", typeof(MeshFilter), typeof(MeshRenderer));
            _previewGO.hideFlags = HideFlags.DontSave;
            _previewGO.transform.SetParent(_rigRoot, false);

            var renderer = _previewGO.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // Reuses the mask-extract preview shader - the requirement is identical (a
            // depth-tested translucent shell you judge the shape of while the rig shows through
            // it), so a second near-identical shader would be pure duplication. A different tint
            // keeps the two previews distinguishable if both are ever on screen.
            Shader shader = Shader.Find("Custom/ExtractPreview");
            if (shader == null)
            {
                Debug.LogWarning("[ZSphere] Custom/ExtractPreview shader not found; preview will render opaque.");
                return;
            }

            _previewMaterial = new Material(shader) { name = "ZSphere Preview (Runtime)" };
            _previewMaterial.SetColor("_Color", PreviewColor);
            _previewMaterial.SetColor("_RimColor", PreviewRimColor);
            renderer.material = _previewMaterial;
        }

        private void DestroyPreview()
        {
            if (_previewGO != null)
            {
                // Deactivated before Destroy for the same reason MaskExtractController does it:
                // Destroy is deferred to the end of the frame, and a preview that keeps rendering
                // over the real object that just replaced it reads as the conversion having done
                // nothing.
                _previewGO.SetActive(false);
                Destroy(_previewGO);
            }
            if (_previewMesh != null) Destroy(_previewMesh);
            if (_previewMaterial != null) Destroy(_previewMaterial);
            _previewGO = null;
            _previewMesh = null;
            _previewMaterial = null;
            PreviewTriangleCount = 0;
        }

        /// Bakes the skin into a real, independent SculptableMesh - the same "make it a brand new
        /// object" contract MeshCloner, MeshMirror and MaskExtractController already established,
        /// so the result is immediately sculptable, maskable, joinable and savable with no
        /// special-casing anywhere downstream. Returns null (with Error set) if there is nothing
        /// to skin.
        public SculptableMesh ConvertToSculptMesh()
        {
            Mesh skin = ZSphereSkinner.Skin(_rig, _settings, out int triangleCount, out string error);
            Error = error;
            if (skin == null) return null;

            // Captured BEFORE anything is cleared - this is what undo puts back.
            ZSphereRig.Node[] rigBefore = _rig.Snapshot();

            // Re-origin the mesh about its own centre and put that offset into the Transform
            // instead. Rig-local coordinates can sit far from the rig root, and an object whose
            // geometry is nowhere near its pivot mirrors through the wrong plane
            // (MirrorController reflects through localPosition zero) and gets a wildly
            // mis-sized transform gizmo - both real bugs, not cosmetic ones.
            Vector3 centre = skin.bounds.center;
            Vector3[] verts = skin.vertices;
            for (int i = 0; i < verts.Length; i++) verts[i] -= centre;
            skin.vertices = verts;
            skin.RecalculateBounds();

            var go = new GameObject(ObjectNaming.Unique("ZSphere Mesh"), typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetPositionAndRotation(_rigRoot.TransformPoint(centre), _rigRoot.rotation);
            go.transform.localScale = _rigRoot.lossyScale;
            go.GetComponent<MeshFilter>().sharedMesh = skin;

            // AddComponent runs SculptableMesh.Awake synchronously, so the object is fully built
            // (working buffers, adjacency, blank mask) by the time this returns.
            SculptableMesh sculptable = go.AddComponent<SculptableMesh>();
            go.AddComponent<MirrorController>();

            FindFirstObjectByType<SculptMaterialController>()?.ApplyTo(go.GetComponent<Renderer>());

            bool clearedRig = !KeepRigOnConvert;
            if (clearedRig) ClearRigInternal();
            DestroyPreview();

            Selection?.Select(sculptable, false);
            // Straight into sculpting on the thing that was just made - the blockout is finished,
            // and leaving the user in a rig-editing mode with no rig to edit is a dead end.
            Gizmo?.SetMode(GizmoMode.Sculpt);

            RecordConvertUndo(sculptable, rigBefore, clearedRig);
            return sculptable;
        }

        /// Makes Convert reversible, so a skin you do not like costs one undo press instead of
        /// rebuilding the rig from scratch.
        ///
        /// Undo PARKS the created object - unregistered from the scene list and deactivated -
        /// rather than destroying it. Destroying would take that object's own sculpt history with
        /// it, so redoing the convert and then trying to redo forward through the strokes you had
        /// made on it would find nothing there. Parking keeps the object, its mesh and its whole
        /// history intact for a redo, at the cost of holding the memory until the step falls out
        /// of history - which is what `discard` is for.
        ///
        /// The rig is only put back if Convert actually cleared it (see KeepRigOnConvert):
        /// restoring a snapshot over a rig the user has gone on editing would silently throw
        /// those edits away, and they are not something this step ever took from them.
        private void RecordConvertUndo(SculptableMesh created, ZSphereRig.Node[] rigBefore, bool clearedRig)
        {
            Mesh createdMesh = created.Mesh;
            long bytes = ZSphereRig.SnapshotBytes(rigBefore);
            if (createdMesh != null)
                bytes += (long)createdMesh.vertexCount * 12 + (long)createdMesh.triangles.Length * 4;

            EditHistory.RecordSceneAction("Skin ZSpheres",
                undo: () =>
                {
                    if (created != null)
                    {
                        // Unregister explicitly: SculptableMesh registers in OnEnable but only
                        // unregisters in OnDestroy, so deactivating alone would leave a ghost row
                        // in the scene list pointing at an object nobody can see or select.
                        Selection?.Unregister(created);
                        created.gameObject.SetActive(false);
                    }
                    if (clearedRig)
                    {
                        _rig.Restore(rigBefore);
                        SelectedNode = ZSphereRig.NoNode;
                        EndDrag();
                        InvalidatePreview();
                    }
                    Gizmo?.SetMode(GizmoMode.ZSphere);
                },
                redo: () =>
                {
                    if (created != null)
                    {
                        created.gameObject.SetActive(true); // OnEnable re-registers it
                        Selection?.Select(created, false);
                    }
                    if (clearedRig) ClearRigInternal();
                    Gizmo?.SetMode(GizmoMode.Sculpt);
                },
                discard: () =>
                {
                    // Only frees a PARKED object. Reaching here with it still active means the
                    // step aged off the undo stack with the convert standing - the object is real,
                    // in the scene, and quite possibly sculpted on since.
                    if (created != null && !created.gameObject.activeSelf) Destroy(created.gameObject);
                },
                approxBytes: bytes);
        }

        // ------------------------------------------------------------------ handle drawing

        private void EnsureRigRoot()
        {
            if (_rigRoot != null) return;

            var root = new GameObject("ZSphere Rig");
            root.hideFlags = HideFlags.DontSave;
            _rigRoot = root.transform;

            var handles = new GameObject("Handles");
            handles.transform.SetParent(_rigRoot, false);
            _handleRoot = handles.transform;

            _nodeMaterial = CreateHandleMaterial(NodeColor);
            _selectedMaterial = CreateHandleMaterial(SelectedNodeColor);
            _linkMaterial = CreateHandleMaterial(LinkColor);
        }

        /// Ordinary depth-tested lit material, NOT the X-ray overlay TransformGizmo's handles use.
        /// A gizmo handle is a control that must always be clickable; a ZSphere rig is a form
        /// being judged in 3D, and dozens of overlapping X-ray spheres with no depth ordering
        /// between them read as noise rather than as a creature.
        private static Material CreateHandleMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                            ?? Shader.Find("Standard")
                            ?? Shader.Find("Sprites/Default");
            var material = new Material(shader) { name = "ZSphere Handle (Runtime)" };
            material.color = color;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.25f);
            return material;
        }

        private void SetRigVisible(bool visible)
        {
            EnsureRigRoot();
            if (_rigRoot.gameObject.activeSelf != visible) _rigRoot.gameObject.SetActive(visible);
            UpdateSymmetryPlane(visible);
        }

        /// Draws the rig's mirror plane, in the same translucent red MirrorController uses for its
        /// own X plane so the two read as the same idea.
        ///
        /// Worth drawing at all because this plane is invisible state that silently decides
        /// whether a drag produces one limb or two, and it is NOT the plane MirrorController
        /// shows - that one belongs to the selected SculptableMesh and sits at that object's
        /// origin, while this one sits at the rig root. With only the mesh's plane on screen there
        /// was no way to tell where the rig would actually mirror, which is exactly how a rig ends
        /// up built entirely on one side without any indication that anything is wrong.
        private void UpdateSymmetryPlane(bool rigVisible)
        {
            bool show = rigVisible && SymmetryX && !_rig.IsEmpty;

            // Now that the rig anchors to the sculpt object, its plane and that object's own
            // MirrorController plane are the SAME plane. Drawing both puts two translucent quads
            // in exactly the same place - they z-fight, and the doubled alpha reads as a second,
            // slightly different axis, which is precisely the "it made its own X axis" confusion
            // this is meant to remove. So ours only appears when the object is not already
            // showing one.
            if (show && AnchorShowsOwnPlane()) show = false;

            if (!show)
            {
                if (_symmetryPlane != null) _symmetryPlane.gameObject.SetActive(false);
                return;
            }

            if (_symmetryPlane == null)
            {
                // Built from scratch rather than via CreatePrimitive(Quad), which always brings a
                // MeshCollider that would then have to be destroyed. PickNode and PickLink both
                // RaycastAll through this hierarchy, and a full-size plane sitting through the
                // middle of the rig is the last thing that should be absorbing hits - filtering it
                // out by tag works, but not creating the collider at all cannot go wrong.
                var go = new GameObject("ZSphereSymmetryPlane", typeof(MeshFilter), typeof(MeshRenderer));
                go.GetComponent<MeshFilter>().sharedMesh = BuildQuadMesh();

                var renderer = go.GetComponent<MeshRenderer>();
                var mat = new Material(Shader.Find("Sprites/Default"));
                mat.color = new Color(1f, 0.25f, 0.25f, 0.14f);
                renderer.sharedMaterial = mat;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;

                _symmetryPlane = go.transform;
                _symmetryPlane.SetParent(_rigRoot, false);
                // Unity's Quad faces +Z; swinging it 90 degrees about Y puts its face in the YZ
                // plane, which is the x=0 plane every symmetry op reflects through.
                _symmetryPlane.localRotation = Quaternion.Euler(0f, 90f, 0f);
            }

            _symmetryPlane.gameObject.SetActive(true);
            _symmetryPlane.localPosition = Vector3.zero;

            // Sized to comfortably contain the rig so the plane reads as cutting through it,
            // rather than as a small card floating somewhere inside.
            Bounds b = _rig.ComputeBounds();
            float extent = Mathf.Max(b.extents.magnitude, _rig.MeanRadius() * 2f);
            float size = Mathf.Max(0.05f, extent * 2.2f);
            _symmetryPlane.localScale = new Vector3(size, size, 1f);
        }

        /// Shows a ghost of the first sphere at the depth a click would place it, and hides it the
        /// moment a rig exists.
        ///
        /// Placing the root is the one action in this tool with nothing to aim at - every other
        /// click targets a sphere or a limb that is already on screen, but the first one lands on
        /// an invisible view plane at the orbit pivot's depth. Without a cursor there is no way to
        /// tell where that is until after committing to it, which reads as the sphere appearing in
        /// an arbitrary place. Drawn at DefaultRootRadius so its SIZE is previewed too, not just
        /// its position, and deliberately translucent so it is legible as a preview rather than as
        /// a sphere that has already been placed.
        private void UpdatePlacementCursor(bool rigActive)
        {
            bool show = rigActive && EditMode == ZSphereEditMode.Add && _cam != null;

            if (show)
            {
                Mouse mouse = Mouse.current;
                bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                if (mouse == null || overUI) show = false;
                else
                {
                    Ray ray = _cam.ScreenPointToRay(mouse.position.ReadValue());
                    bool onSurface = TrySurfacePoint(ray, out Vector3 surfacePoint);

                    // With a rig already up, the only click that still places a root is one on the
                    // attach target's surface (see BeginDrag), and a sphere already under the
                    // cursor outranks that - so the ghost appears in exactly the cases a click
                    // would actually produce a new root, and nowhere else.
                    if (!_rig.IsEmpty)
                        show = onSurface && PickNode(ray) == ZSphereRig.NoNode;

                    if (show)
                    {
                        // The surface hit is reused rather than asking RootPlacementPoint, which
                        // would raycast for it a second time - this runs every frame the tool is up.
                        Vector3 point = onSurface ? surfacePoint : ViewPlanePoint(ray);
                        float radius = DefaultRootRadius();

                        if (_placementCursor == null)
                        {
                            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                            go.name = "ZSpherePlacementCursor";
                            // No collider: PickNode/PickLink RaycastAll through the scene, and a
                            // ghost that swallowed its own placement click would be self-defeating.
                            Collider col = go.GetComponent<Collider>();
                            if (col != null) Destroy(col);

                            var renderer = go.GetComponent<MeshRenderer>();
                            renderer.sharedMaterial = CreateHandleMaterial(PlacementCursorColor);
                            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                            renderer.receiveShadows = false;
                            _placementCursor = go.transform;
                            _placementCursor.SetParent(_rigRoot, true);
                        }

                        _placementCursor.position = point;
                        // Unity's sphere primitive is 1 unit ACROSS at unit scale.
                        _placementCursor.localScale = Vector3.one * (radius * 2f);
                    }
                }
            }

            if (_placementCursor != null && _placementCursor.gameObject.activeSelf != show)
                _placementCursor.gameObject.SetActive(show);
        }

        /// World-space half-size of the rig as currently drawn, for a mirror plane sitting at
        /// `planeOrigin`. Zero unless there is a visible, non-empty rig mirroring about that very
        /// point - a plane belonging to some other object must not be stretched to fit a rig it
        /// has nothing to do with, and comparing origins is exactly the "are we the same plane"
        /// test, since both reflect through their own transform's x=0.
        ///
        /// Read by MirrorController to size the plane the two of them share. A blockout is
        /// normally grown well past the sphere it started from, so sizing that plane off the mesh
        /// alone left a small card floating inside a much larger rig.
        public float WorldExtentForPlaneAt(Vector3 planeOrigin)
        {
            if (_rigRoot == null || _rig.IsEmpty || !_rigRoot.gameObject.activeSelf) return 0f;
            if ((_rigRoot.position - planeOrigin).sqrMagnitude > 1e-6f) return 0f;

            // Rig space is unit-scaled, so a rig-local extent is already a world distance.
            Bounds bounds = _rig.ComputeBounds();
            return Mathf.Max(bounds.extents.magnitude, _rig.MeanRadius() * 2f);
        }

        /// Whether the object the rig is anchored to is already drawing this same X plane. Cheap
        /// enough to poll every frame, unlike the rig-bounds sizing in UpdateSymmetryPlane, which
        /// is why it is asked here and folded into RefreshHandles' change check - toggling
        /// Mirror X on the sculpt object touches nothing the rig would otherwise notice.
        private bool AnchorShowsOwnPlane()
        {
            Transform anchor = SymmetryAnchor();
            var mirror = anchor != null ? anchor.GetComponent<MirrorController>() : null;
            return mirror != null && mirror.MirrorX && mirror.ShowPlanes;
        }

        /// A 1x1 quad in the XY plane facing +Z, matching Unity's own Quad primitive so the
        /// existing "rotate 90 about Y to face the YZ plane" placement still reads the same way.
        private static Mesh BuildQuadMesh()
        {
            var mesh = new Mesh { name = "ZSphere Symmetry Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f), new Vector3(0.5f,  0.5f, 0f)
            };
            // Both windings, so the plane is visible from either side - it is a reference overlay,
            // and one that vanishes when you orbit past it would be worse than useless.
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// Rebuilds the handle GameObjects when the rig (or the selection highlight) changed.
        /// Pools rather than recreating: during a drag this runs every frame, and destroying and
        /// re-creating a few dozen colliders per frame would churn the physics scene for nothing.
        private void RefreshHandles()
        {
            // SymmetryX joins the change check because the mirror plane is drawn from here, and
            // toggling symmetry does not touch the rig's version - without this the plane would
            // not appear until the next edit happened to bump it.
            bool anchorPlane = AnchorShowsOwnPlane();
            if (_rig.Version == _lastDrawnVersion && SelectedNode == _lastDrawnSelection
                && SymmetryX == _lastDrawnSymmetry && anchorPlane == _lastDrawnAnchorPlane) return;
            _lastDrawnVersion = _rig.Version;
            _lastDrawnSelection = SelectedNode;
            _lastDrawnSymmetry = SymmetryX;
            _lastDrawnAnchorPlane = anchorPlane;

            EnsureRigRoot();
            UpdateSymmetryPlane(true);

            int nodeSlot = 0;
            int linkSlot = 0;

            for (int i = 0; i < _rig.Count; i++)
            {
                if (!_rig.IsAlive(i)) continue;
                ZSphereRig.Node node = _rig.Nodes[i];

                GameObject handle = NodeHandleAt(nodeSlot++);
                handle.SetActive(true);
                handle.GetComponent<ZSphereNodeTag>().Index = i;
                handle.transform.localPosition = node.Position;
                // Unity's sphere primitive is 1 unit ACROSS at unit scale, so a radius maps to a
                // scale of twice it.
                handle.transform.localScale = Vector3.one * (node.Radius * 2f);
                handle.GetComponent<MeshRenderer>().sharedMaterial =
                    i == SelectedNode ? _selectedMaterial : _nodeMaterial;

                if (!_rig.IsAlive(node.Parent)) continue;
                ZSphereRig.Node parent = _rig.Nodes[node.Parent];
                UpdateLinkHandle(linkSlot++, i, parent.Position, node.Position,
                                 parent.Radius * LinkVisualScale, node.Radius * LinkVisualScale);
            }

            for (int i = nodeSlot; i < _nodeHandles.Count; i++) _nodeHandles[i].SetActive(false);
            for (int i = linkSlot; i < _linkHandles.Count; i++) _linkHandles[i].SetActive(false);

            // The handles just moved, and PickNode raycasts against their colliders LATER IN THIS
            // SAME Update. Physics.autoSyncTransforms defaults to off, so without this the queries
            // would see wherever the colliders were as of the last physics step - which at a high
            // frame rate is several frames stale, and shows up as spheres that have to be clicked
            // slightly behind where they are drawn right after a drag. Only runs when the rig
            // actually changed, since the whole method early-outs otherwise.
            Physics.SyncTransforms();
        }

        private GameObject NodeHandleAt(int slot)
        {
            while (_nodeHandles.Count <= slot)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "ZSphereNode";
                sphere.transform.SetParent(_handleRoot, false);
                sphere.AddComponent<ZSphereNodeTag>();
                // KEEPS its collider, unlike MirrorController's decorative planes - RaycastAll
                // against it is how PickNode turns a click into a node index.
                var renderer = sphere.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                _nodeHandles.Add(sphere);
            }
            return _nodeHandles[slot];
        }

        private void UpdateLinkHandle(int slot, int childIndex, Vector3 from, Vector3 to,
                                      float radiusFrom, float radiusTo)
        {
            while (_linkHandles.Count <= slot)
            {
                var go = new GameObject("ZSphereLink", typeof(MeshFilter), typeof(MeshRenderer),
                                        typeof(MeshCollider));
                go.transform.SetParent(_handleRoot, false);
                var renderer = go.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = _linkMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                go.AddComponent<ZSphereLinkTag>();

                // One Mesh per slot, REFILLED rather than reallocated. A tapered tube's shape
                // depends on both end radii, so unlike the node spheres it cannot be one shared
                // mesh scaled per instance - and allocating a fresh Mesh per link per frame of a
                // drag would leak until the next GC.
                var mesh = new Mesh { name = "ZSphere Link" };
                mesh.MarkDynamic();
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                _linkMeshes.Add(mesh);
                _linkHandles.Add(go);
            }

            GameObject handle = _linkHandles[slot];
            handle.SetActive(true);
            handle.GetComponent<ZSphereLinkTag>().ChildIndex = childIndex;

            Vector3 axis = to - from;
            float length = axis.magnitude;
            if (length < 1e-6f) { handle.SetActive(false); return; }

            handle.transform.localPosition = from;
            handle.transform.localRotation = Quaternion.LookRotation(axis);
            Mesh tube = _linkMeshes[slot];
            FillTaperedTube(tube, radiusFrom, radiusTo, length);

            // Re-seat the collider on the rebuilt mesh. Unity does not notice a sharedMesh being
            // refilled in place, so without the null-then-set the link would stay clickable only
            // at whatever shape it had when the collider was first assigned.
            var collider = handle.GetComponent<MeshCollider>();
            collider.sharedMesh = null;
            collider.sharedMesh = tube;
        }

        // Reused across every link rebuild - see MeshRemesher's own scratch buffers for the same
        // reasoning. Main thread only, and rebuilds run strictly one after another.
        private static readonly List<Vector3> _tubeVerts = new List<Vector3>();
        private static readonly List<int> _tubeTris = new List<int>();

        /// A cone frustum along +Z from the origin: radius `r1` at z=0, `r2` at z=length. Open at
        /// both ends - the node spheres sit exactly on the caps and hide them.
        private static void FillTaperedTube(Mesh mesh, float r1, float r2, float length)
        {
            _tubeVerts.Clear();
            _tubeTris.Clear();

            for (int ring = 0; ring < 2; ring++)
            {
                float radius = ring == 0 ? r1 : r2;
                float z = ring == 0 ? 0f : length;
                for (int i = 0; i < LinkSegments; i++)
                {
                    float angle = i / (float)LinkSegments * Mathf.PI * 2f;
                    _tubeVerts.Add(new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, z));
                }
            }

            for (int i = 0; i < LinkSegments; i++)
            {
                int a = i;
                int b = (i + 1) % LinkSegments;
                int c = LinkSegments + i;
                int d = LinkSegments + b;
                _tubeTris.Add(a); _tubeTris.Add(b); _tubeTris.Add(c);
                _tubeTris.Add(b); _tubeTris.Add(d); _tubeTris.Add(c);
            }

            mesh.Clear();
            mesh.SetVertices(_tubeVerts);
            mesh.SetTriangles(_tubeTris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }

        // ------------------------------------------------------------------------ transforms

        private Vector3 RigToWorld(Vector3 rigPoint) => _rigRoot.TransformPoint(rigPoint);
        private Vector3 WorldToRig(Vector3 worldPoint) => _rigRoot.InverseTransformPoint(worldPoint);
    }
}
