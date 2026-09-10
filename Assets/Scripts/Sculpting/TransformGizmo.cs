using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Which handles a gizmo shows. A flag set rather than a mode enum because the two existing
    /// modes are already combinations (Transpose is Move+Rotate, Scale is Scale+UniformScale) and
    /// the tools now pointing the gizmo at their own targets want their own mixes - a ZSphere node
    /// has no meaningful scale, a light has no meaningful size.
    [System.Flags]
    public enum GizmoHandleSet
    {
        None = 0,
        Move = 1,
        Rotate = 2,
        Scale = 4,
        UniformScale = 8,

        Transpose = Move | Rotate,
        Scaling = Scale | UniformScale,
    }

    /// A tool that points the gizmo at targets of its own rather than at the scene-graph
    /// selection - see TransformGizmo.SetExternalTargets.
    public interface IGizmoTargetSource
    {
        /// True when this source pushes its OWN undo entry for a gizmo drag, so the gizmo must not
        /// also record a scene-level transform step - one gesture would otherwise take two undo
        /// presses to reverse. A ZSphere rig says true (it keeps rig-local history); a source whose
        /// targets are plain Transforms says false and lets the gizmo record it, which is the right
        /// answer for anything that has no history of its own.
        bool RecordsOwnUndoStep { get; }

        /// A drag on this source's targets is about to start, before anything has moved. The
        /// source opens its own undo step here - a ZSphere rig keeps rig-local history separate
        /// from the scene's (see ZSphereController.BeginRigEdit), and it has to snapshot BEFORE
        /// the first frame of movement or the step records the already-moved state.
        void OnGizmoDragStarted();

        /// The matching end of that drag, always called - `changed` says whether anything actually
        /// moved, so a source can close a snapshot it opened either way while only pushing real
        /// history for a drag that did something.
        void OnGizmoDragEnded(bool changed);
    }

    /// Hand-rolled runtime Transpose (move+rotate) and Scale gizmo for the selected whole
    /// object's Transform - Unity's Handles class is Editor-only, so every handle here is a
    /// plain GameObject (cylinders/cubes, same primitives-plus-destroyed-collider idiom
    /// MirrorController already uses for its mirror planes, except gizmo handles deliberately
    /// KEEP their collider since Physics.Raycast is how they get picked). See GizmoMode for the
    /// three modes; SculptController.HandleSculptInput early-outs while Mode != Sculpt so
    /// brush strokes and gizmo dragging never fight over the same click.
    ///
    /// All handles are built once at Awake() at a canonical unit size, parented under a single
    /// "_root" GameObject whose position/rotation/uniform-scale is set fresh every frame from
    /// the selected object's transform (see ComputeArmLength) - this is what lets the whole
    /// gizmo reposition/resize itself with three field writes instead of touching every handle
    /// individually every frame.
    public class TransformGizmo : MonoBehaviour
    {
        private enum HandleKind { Move, Rotate, Scale, UniformScale }

        // Tags a handle's own collider (or its cap/shaft child) with which axis/tool it drags -
        // AddComponent'd purely in code, never referenced from the Inspector, so being a
        // private nested MonoBehaviour is fine (Unity only needs top-level-class-matches-
        // filename for Inspector/drag-and-drop attachment, not for AddComponent<T>() in code).
        private class GizmoHandleTag : MonoBehaviour
        {
            public HandleKind Kind { get; private set; }
            public int Axis { get; private set; }
            public void Init(HandleKind kind, int axis) { Kind = kind; Axis = axis; }
        }

        private static readonly Vector3[] AxisDirections = { Vector3.right, Vector3.up, Vector3.forward };
        // Matches Unity's axis-handle/gizmo convention (X red, Y green, Z blue) - same values
        // as MirrorController's own plane colors, just re-declared here since those are private
        // to that class.
        private static readonly Color[] AxisColors =
        {
            new Color(1f, 0.25f, 0.25f), new Color(0.35f, 1f, 0.35f), new Color(0.3f, 0.55f, 1f)
        };

        private const float ShaftLength = 1f; // unit length before _root's per-frame uniform scale
        private const float ShaftThickness = 0.05f;
        private const float CapSize = 0.14f; // Scale handles only - see CreateShaftHandle
        private const float ArrowRadius = 0.09f; // Move handles only - a cone reads as "direction" more naturally than a cube
        private const float ArrowHeight = 0.24f;
        private const int ArrowSegments = 16;
        private const float RingOuterRadius = 0.65f;
        // Inner radius as a fraction of outer - a thin band read as a circular line rather
        // than a filled disc. Kept wide enough to still be comfortably clickable.
        private const float RingInnerRatio = 0.88f;
        private const int RingSegments = 48;
        private const float UniformHandleSize = 0.16f;
        private const float ArmLengthFactor = 1.8f; // gizmo size relative to the object's own bounds
        private const float MinArmLength = 0.05f;
        private const float MinScaleAxis = 0.02f;

        private SelectionManager _selection;
        private SelectionManager Selection => _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());
        private SculptableMesh Target => Selection != null ? Selection.PrimarySelection : null;

        // Everything this drag moves. Built from the scene-graph selection, or from whatever a
        // tool has pushed via SetExternalTargets - the gizmo does not know or care which.
        // Rebuilt on a selection change rather than every frame (see RefreshTargets).
        private readonly List<GizmoTarget> _targets = new List<GizmoTarget>();
        private int _targetsBuiltForVersion = -1;

        private IGizmoTargetSource _externalSource;
        private readonly List<GizmoTarget> _externalTargets = new List<GizmoTarget>();
        private GizmoHandleSet _externalHandles = GizmoHandleSet.Transpose;

        /// Points the gizmo at `targets` instead of the scene-graph selection, until the same
        /// owner clears them. The owner is checked on clear so two tools cannot silently steal the
        /// gizmo from one another - whichever set it last owns it, and only that one can drop it.
        public void SetExternalTargets(IGizmoTargetSource owner, IReadOnlyList<GizmoTarget> targets,
                                       GizmoHandleSet handles = GizmoHandleSet.Transpose)
        {
            _externalSource = owner;
            _externalHandles = handles;
            _externalTargets.Clear();
            if (targets != null)
                for (int i = 0; i < targets.Count; i++)
                    if (targets[i] != null && targets[i].IsAlive) _externalTargets.Add(targets[i]);
            // Force a rebuild: an external push is not a SelectionManager change, so the version
            // check in RefreshTargets would not otherwise notice it.
            _targetsBuiltForVersion = -1;
        }

        public void ClearExternalTargets(IGizmoTargetSource owner)
        {
            if (_externalSource != owner) return;
            _externalSource = null;
            _externalTargets.Clear();
            _targetsBuiltForVersion = -1;
        }

        private Camera _cam;
        private GameObject _root;
        private readonly GameObject[] _moveGroups = new GameObject[3];
        private readonly GameObject[] _scaleGroups = new GameObject[3];
        private readonly GameObject[] _rotateHandles = new GameObject[3];
        private GameObject _uniformHandle;

        public GizmoMode Mode { get; private set; } = GizmoMode.Sculpt;
        public void SetMode(GizmoMode mode) => Mode = mode;

        /// True while a handle drag is running. Any other tool sharing the mouse has to stand down
        /// for the duration - see ZSphereController.HandleInput.
        public bool IsDragging => _dragging;

        /// Whether `ray` currently hits one of this gizmo's handles. Lets a tool that owns the
        /// same click give the gizmo first refusal on it, rather than both acting on one press.
        /// Returns false when the gizmo is not showing, so a put-away gizmo blocks nothing.
        public bool IsPointerOverHandle(Ray ray) =>
            _root != null && _root.activeSelf && TryPickHandle(ray, out _);

        // Drag state - captured once at mouse-press, reused every frame of the drag. The dragged
        // SET is snapshotted explicitly (not re-read from the selection each frame) so a selection
        // change mid-drag (e.g. clicking a different Scene Graph row) can't yank an in-progress
        // drag onto a different object.
        private bool _dragging;
        private readonly List<GizmoTarget> _dragTargets = new List<GizmoTarget>();
        private readonly List<TargetTransform> _dragStartStates = new List<TargetTransform>();
        private IGizmoTargetSource _dragSource;
        private HandleKind _dragKind;
        private int _dragAxis;
        // The point the whole drag pivots about, and the frame its axes are expressed in - the
        // single target's own origin/rotation, or the set's centroid with world axes. Frozen at
        // press, because a rotate that re-derived its own pivot from the positions it is moving
        // would chase itself.
        private Vector3 _dragPivot;
        private Quaternion _dragPivotRotation;
        // The gizmo's own on-screen size, frozen at press for the whole drag - see the remarks
        // where Update() applies it for why recomputing it per frame made the handles grow.
        private float _dragArmLength;
        private Vector3 _dragAxisWorld;
        private Vector3 _dragPlaneNormal;
        private float _dragStartValue; // meaning depends on _dragKind: signed offset along axis (Move/Scale) or angle in degrees (Rotate)

        /// One target's full TRS at drag start. Every handler re-derives its result from THESE
        /// rather than compounding onto last frame's output: compounding lets rounding drift
        /// accumulate over a long drag and (worse) makes dragging back to the start not actually
        /// return to the start - the same reasoning SculptableMesh's masked-transform base uses.
        private readonly struct TargetTransform
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly Vector3 Scale;

            public TargetTransform(GizmoTarget t)
            {
                Position = t.Position;
                Rotation = t.Rotation;
                Scale = t.LocalScale;
            }

            public TargetTransform(Vector3 position, Quaternion rotation, Vector3 scale)
            {
                Position = position;
                Rotation = rotation;
                Scale = scale;
            }

            public void ApplyTo(GizmoTarget t)
            {
                if (t == null || !t.IsAlive) return;
                t.Position = Position;
                if (t.SupportsRotation) t.Rotation = Rotation;
                if (t.SupportsScale) t.LocalScale = Scale;
            }

            public bool Matches(TargetTransform other) =>
                Position == other.Position && Rotation == other.Rotation && Scale == other.Scale;
        }

        // Non-null while this drag is deforming vertices around a mask instead of moving the
        // Transform (see SculptableMesh.BeginMaskedTransform). Captured at mouse-press for the
        // same reason _dragTarget is: a selection change mid-drag must not redirect it.
        private SculptableMesh _maskedTarget;
        // Uniform scale is the one handle with no absolute drag reference (see
        // DragUniformScale) - masked mode needs a total-since-drag-start factor, not a
        // per-frame one, so it accumulates here rather than compounding into the Transform.
        private float _uniformScaleAccum = 1f;

        private void Awake()
        {
            _cam = Camera.main;
            BuildHandles();
            _root.SetActive(false);
        }

        private void Update()
        {
            if (_cam == null) _cam = Camera.main;

            RefreshTargets();

            // An external source (ZSphere move, a selected light) owns the gizmo whenever it has
            // pushed targets, whatever Mode says - those tools run under their own GizmoMode and
            // would otherwise never see handles at all. Failing that, this is tested against this
            // gizmo's OWN two modes rather than `!= Sculpt`: GizmoMode also carries modes
            // belonging to other tools, and a blanket "anything but Sculpt" test showed these
            // handles on top of those tools.
            bool externallyOwned = _externalTargets.Count > 0;
            bool active = _cam != null && _targets.Count > 0 &&
                          (externallyOwned || Mode == GizmoMode.Transpose || Mode == GizmoMode.Scale);

            if (_root.activeSelf != active) _root.SetActive(active);
            if (!active) { EndDrag(); return; }

            // A Move drag is the one case where the gizmo travels with what it is dragging - the
            // handles are supposed to read as attached to the thing under them, and arrows left
            // behind while the object slides away read as a bug. Every other handle stays pinned
            // to where its drag started: a rotate ring that re-centred on its own output would
            // slide out from under the cursor mid-swing, and a scale that did so would chase its
            // own spread.
            bool followsTargets = _dragging && _dragKind == HandleKind.Move;
            Vector3 pivot = !_dragging ? ComputePivot(_targets)
                          : followsTargets ? ComputePivot(_dragTargets)
                          : _dragPivot;
            Quaternion pivotRotation = _dragging ? _dragPivotRotation : ComputePivotRotation(_targets);
            _root.transform.SetPositionAndRotation(pivot, pivotRotation);

            // Arm length is FROZEN for the whole drag rather than recomputed per frame. It is
            // derived from how far each target sits from the pivot (see ComputeArmLength), and the
            // pivot every non-Move drag measures against is the one it started at - so dragging a
            // target away from that point grew the arms by exactly the distance dragged. On a
            // ZSphere node, whose own radius is small next to the drag, that read as the arrows
            // ballooning off the sphere mid-drag and snapping back on release.
            _root.transform.localScale =
                Vector3.one * (_dragging ? _dragArmLength : ComputeArmLength(_targets, pivot));

            GizmoHandleSet handles = externallyOwned
                ? _externalHandles
                : (Mode == GizmoMode.Transpose ? GizmoHandleSet.Transpose : GizmoHandleSet.Scaling);
            ApplyHandleVisibility(handles);

            // The handles were just repositioned, and TryPickHandle picks them with
            // Physics.RaycastAll - which tests against the PHYSICS scene's copy of each collider's
            // transform, not the one just written. Unity only refreshes that copy at the next
            // physics step (Physics.autoSyncTransforms is off by default), so without this a click
            // in the same frame the gizmo moved is tested against where the handles USED to be:
            // every click on the frame the gizmo first appears, or the frame the selection jumps
            // it to another object, silently misses. Verified directly - RaycastAll returned 0
            // hits against handles plainly under the cursor until this call was added. Cheap: it
            // only walks transforms actually marked dirty, and only runs while the gizmo is up.
            Physics.SyncTransforms();

            HandleDragInput();
        }

        private void ApplyHandleVisibility(GizmoHandleSet handles)
        {
            bool move = (handles & GizmoHandleSet.Move) != 0;
            bool rotate = (handles & GizmoHandleSet.Rotate) != 0;
            bool scale = (handles & GizmoHandleSet.Scale) != 0;

            // A set that cannot scale must not show scale handles even if the caller asked for
            // them - a ZSphere node has no localScale to write, and a handle that visibly does
            // nothing is worse than an absent one.
            if (scale && !AnyTargetSupportsScale()) scale = false;
            if (rotate && !AnyTargetSupportsRotation() && _targets.Count == 1) rotate = false;

            for (int i = 0; i < 3; i++)
            {
                _moveGroups[i].SetActive(move);
                _rotateHandles[i].SetActive(rotate);
                _scaleGroups[i].SetActive(scale);
            }
            _uniformHandle.SetActive(scale && (handles & GizmoHandleSet.UniformScale) != 0);
        }

        private bool AnyTargetSupportsScale()
        {
            for (int i = 0; i < _targets.Count; i++)
                if (_targets[i].SupportsScale) return true;
            return false;
        }

        // A single point-like target (a ZSphere node) has nothing to rotate. A SET of them does:
        // rotating swings them around the shared pivot, which is a real and useful edit even
        // though no individual target's own orientation changes.
        private bool AnyTargetSupportsRotation()
        {
            for (int i = 0; i < _targets.Count; i++)
                if (_targets[i].SupportsRotation) return true;
            return false;
        }

        /// Rebuilds _targets when the thing it is derived from has changed. Skipped entirely
        /// mid-drag: the drag holds its own snapshot, and swapping the live list under it would
        /// only cost work, never change what the drag moves.
        private void RefreshTargets()
        {
            if (_dragging) return;

            if (_externalTargets.Count > 0)
            {
                // An external list is small and pushed only on change, so it is just copied -
                // and re-filtered for liveness, since the owner can't know when a target dies.
                if (_targetsBuiltForVersion != -1 && _targets.Count == _externalTargets.Count) return;
                _targets.Clear();
                for (int i = 0; i < _externalTargets.Count; i++)
                    if (_externalTargets[i].IsAlive) _targets.Add(_externalTargets[i]);
                _targetsBuiltForVersion = 0;
                return;
            }

            SelectionManager selection = Selection;
            int version = selection != null ? selection.SelectionVersion : -1;
            if (version == _targetsBuiltForVersion && !HasDeadTarget()) return;
            _targetsBuiltForVersion = version;

            _targets.Clear();
            if (selection == null) return;

            // The whole multi-selection, not just the primary - shift-clicking two objects and
            // dragging them together is the point. Falls back to the primary alone so a scene that
            // never touched the multi-select path behaves exactly as it always did.
            IReadOnlyList<SculptableMesh> selected = selection.SelectedSet;
            if (selected.Count > 0)
            {
                for (int i = 0; i < selected.Count; i++)
                    if (selected[i] != null && selected[i].Visible)
                        _targets.Add(new TransformGizmoTarget(selected[i].transform));
            }

            if (_targets.Count == 0)
            {
                SculptableMesh primary = selection.PrimarySelection;
                if (primary != null && primary.Visible) _targets.Add(new TransformGizmoTarget(primary.transform));
            }
        }

        private bool HasDeadTarget()
        {
            for (int i = 0; i < _targets.Count; i++)
                if (!_targets[i].IsAlive) return true;
            return false;
        }

        private static Vector3 ComputePivot(List<GizmoTarget> targets)
        {
            if (targets.Count == 1) return targets[0].Position;

            Vector3 sum = Vector3.zero;
            for (int i = 0; i < targets.Count; i++) sum += targets[i].Position;
            return sum / Mathf.Max(1, targets.Count);
        }

        // A single target's handles align to its OWN axes, which is what makes "move along the
        // object's X" mean anything on a rotated object. A multi-selection has no single local
        // frame to borrow, so it falls back to world axes - the same choice every DCC makes.
        private static Quaternion ComputePivotRotation(List<GizmoTarget> targets) =>
            targets.Count == 1 ? targets[0].Rotation : Quaternion.identity;

        private float ComputeArmLength(List<GizmoTarget> targets, Vector3 pivot)
        {
            float radius = 0f;
            for (int i = 0; i < targets.Count; i++)
            {
                // For a set, the arms have to clear the whole spread, not just the biggest member
                // - otherwise two objects far apart get a gizmo buried inside one of them.
                float reach = Vector3.Distance(targets[i].Position, pivot) + targets[i].WorldRadius;
                if (reach > radius) radius = reach;
            }
            return Mathf.Max(MinArmLength, radius * ArmLengthFactor);
        }

        // ------------------------------------------------------------------------- drag input

        private void HandleDragInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            if (!_dragging)
            {
                bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                bool altHeld = Keyboard.current != null && Keyboard.current.leftAltKey.isPressed;
                if (!overUI && !altHeld && mouse.leftButton.wasPressedThisFrame && !TryBeginDrag(mouse))
                    TryPickObject(mouse);
                return;
            }

            if (!mouse.leftButton.isPressed) { EndDrag(); return; }

            // A live handle drag is a real change to the model, but it only reaches history when
            // the drag ENDS (see EndDrag's RecordSceneAction) - so without this the timelapse
            // would pause through the whole move and then jump. Moving an object's transform
            // never touches its vertices, so none of SculptableMesh's own reports fire here.
            SculptActivity.ReportEdit();

            Ray ray = _cam.ScreenPointToRay(mouse.position.ReadValue());
            switch (_dragKind)
            {
                case HandleKind.Move: DragMove(ray); break;
                case HandleKind.Rotate: DragRotate(ray); break;
                case HandleKind.Scale: DragScale(ray); break;
                case HandleKind.UniformScale: DragUniformScale(mouse); break;
            }
        }

        /// Click-to-select while a transform tool is up, with Shift/Ctrl toggling into a
        /// multi-object selection the gizmo then moves as one.
        ///
        /// This lives here rather than alongside SculptController's own double-click pick because
        /// of a shortcut collision: while a brush is active, Shift is held-to-Smooth and Ctrl
        /// inverts the stroke, so neither is free as a selection modifier. A transform tool
        /// suppresses the brushes entirely (SculptController.HandleSculptInput early-outs on
        /// Mode != Sculpt), so exactly here both modifiers ARE free - and this is also the only
        /// mode in which a multi-selection does anything, which makes it the honest home for it.
        ///
        /// A click that hits nothing leaves the selection alone rather than clearing it: with the
        /// gizmo's own arms reaching well past the object, "missed the model" is far more often a
        /// missed handle grab than a deliberate deselect.
        private void TryPickObject(Mouse mouse)
        {
            // A ZSphere rig owns its own selection and drives the gizmo from it - picking a mesh
            // or a light out from under it would swap the gizmo onto something that tool has no
            // say over. Lights are exempt: THEY are what this method selects, so a light already
            // holding the gizmo must still be able to hand it to another one.
            if (_externalTargets.Count > 0 && !(_externalSource is SceneLightManager)) return;

            Ray ray = _cam.ScreenPointToRay(mouse.position.ReadValue());
            Keyboard kb = Keyboard.current;
            bool additive = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed ||
                                           kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);

            // Whichever is actually in front wins, so a light sitting between the camera and the
            // model is clickable and one behind it is not - the same "closest hit" rule the handle
            // picker uses.
            SceneLightManager lights = LightManager;
            float lightDist = float.MaxValue;
            SceneLight lightHit = lights != null ? lights.Raycast(ray, out lightDist) : null;
            if (lightHit == null) lightDist = float.MaxValue;

            SelectionManager selection = Selection;
            SculptableMesh meshHit = selection != null ? selection.Raycast(ray) : null;
            float meshDist = float.MaxValue;
            if (meshHit != null && meshHit.RaycastMesh(ray, 1000f, out Vector3 meshPoint, out _))
                meshDist = Vector3.Distance(ray.origin, meshPoint);

            if (lightHit != null && lightDist <= meshDist)
            {
                lights.Select(lightHit, additive);
                return;
            }

            if (meshHit == null) return;

            // Selecting a mesh gives up any light selection, so exactly one kind of thing is ever
            // under the gizmo - see SceneLightManager's class remarks.
            lights?.ClearSelection();

            if (additive)
            {
                selection.ToggleSelected(meshHit);
                SelectionFlashEffect.Play(meshHit.gameObject);
                return;
            }

            // Re-clicking the only selected object is a no-op rather than a flash: nothing changed.
            if (selection.SelectedSet.Count == 1 && selection.PrimarySelection == meshHit) return;
            selection.Select(meshHit, false);
            SelectionFlashEffect.Play(meshHit.gameObject);
        }

        // Found lazily and ADDED to this GameObject if the scene has none - the same
        // self-installing idiom SculptController uses for RegionSelectTool, and for the same
        // reason: this project's scene file is edited through Unity MCP, which cannot wire object
        // references, so a component that installs itself is the one that reliably exists at
        // runtime. It also means a scene saved before lights existed picks the feature up with no
        // scene edit at all.
        private SceneLightManager _lightManager;
        private SceneLightManager LightManager
        {
            get
            {
                if (_lightManager != null) return _lightManager;
                _lightManager = FindFirstObjectByType<SceneLightManager>();
                if (_lightManager == null) _lightManager = gameObject.AddComponent<SceneLightManager>();
                return _lightManager;
            }
        }

        /// The scene's light manager, self-installing on first use. Exposed so the lighting panel
        /// reaches the same instance rather than creating a second one.
        public SceneLightManager Lights => LightManager;

        private bool TryBeginDrag(Mouse mouse)
        {
            Ray ray = _cam.ScreenPointToRay(mouse.position.ReadValue());
            if (!TryPickHandle(ray, out GizmoHandleTag tag, out float handleDistance)) return false;

            // A handle beats a MESH regardless of depth - they are drawn always-on-top precisely
            // so a gizmo sitting inside the model stays grabbable, and the picker has to agree
            // with what is on screen. It must NOT beat another always-on-top control that is
            // genuinely in front of it, though: a light marker behind a handle is unreachable
            // otherwise, which is exactly what stopped a second light from ever being shift-added
            // to the selection - the handles around the first one swallowed the click.
            SceneLightManager lights = LightManager;
            if (lights != null)
            {
                lights.Raycast(ray, out float lightDistance);
                if (lightDistance < handleDistance) return false;
            }

            _dragging = true;
            _dragKind = tag.Kind;
            _dragAxis = tag.Axis;
            _dragSource = _externalTargets.Count > 0 ? _externalSource : null;
            _uniformScaleAccum = 1f;

            _dragTargets.Clear();
            _dragStartStates.Clear();
            for (int i = 0; i < _targets.Count; i++)
            {
                if (!_targets[i].IsAlive) continue;
                _dragTargets.Add(_targets[i]);
                _dragStartStates.Add(new TargetTransform(_targets[i]));
            }
            if (_dragTargets.Count == 0) { _dragging = false; return false; }

            _dragPivot = ComputePivot(_dragTargets);
            _dragPivotRotation = ComputePivotRotation(_dragTargets);
            _dragArmLength = ComputeArmLength(_dragTargets, _dragPivot);

            // With anything masked, this drag deforms the mesh around the frozen region rather
            // than moving the whole object - the "mask the body, Transpose out an arm" workflow.
            // Only ever for a lone SculptableMesh: the mask lives in one mesh's vertex array, so
            // there is no meaningful "deform the selection around its mask" for a set. Falls
            // straight back to the plain Transform drag when nothing is masked (see
            // SculptableMesh.BeginMaskedTransform).
            SculptableMesh candidateTarget = Target;
            _maskedTarget = _dragTargets.Count == 1 && _dragSource == null &&
                            candidateTarget != null &&
                            _dragTargets[0] is TransformGizmoTarget only &&
                            only.Transform == candidateTarget.transform &&
                            candidateTarget.BeginMaskedTransform() ? candidateTarget : null;

            // The handle's own axis, in the frame the gizmo is currently drawn in - which is the
            // single target's local axes, or world axes for a set (see ComputePivotRotation).
            _dragAxisWorld = _dragPivotRotation * AxisDirections[_dragAxis];

            if (_dragKind == HandleKind.Move || _dragKind == HandleKind.Scale)
            {
                _dragPlaneNormal = BuildCameraFacingPlaneNormal(_dragAxisWorld);
                _dragStartValue = ProjectRayOntoAxis(ray, _dragPivot, _dragAxisWorld, _dragPlaneNormal);
            }
            else if (_dragKind == HandleKind.Rotate)
            {
                if (SculptController.RayPlaneIntersect(ray, _dragPivot, _dragAxisWorld, out Vector3 hit))
                    _dragStartValue = AngleOnPlane(hit, _dragPivot, _dragAxisWorld);
            }

            // Last, so the source snapshots a state nothing has touched yet.
            _dragSource?.OnGizmoDragStarted();
            return true;
        }

        /// Physics.RaycastAll rather than a single Raycast, keeping only hits tagged as a
        /// gizmo handle and picking the closest one - the plain sculpted mesh's own (still-
        /// present, just no longer brush-hot-path) MeshCollider sits between the camera and a
        /// handle on the far side of the object for plenty of camera angles, and a single
        /// Raycast would report that closer, untagged hit instead of the handle the user is
        /// actually trying to click.
        private static bool TryPickHandle(Ray ray, out GizmoHandleTag tag) =>
            TryPickHandle(ray, out tag, out _);

        private static bool TryPickHandle(Ray ray, out GizmoHandleTag tag, out float distance)
        {
            tag = null;
            distance = float.MaxValue;
            foreach (RaycastHit hit in Physics.RaycastAll(ray, 1000f))
            {
                GizmoHandleTag candidate = hit.collider.GetComponentInParent<GizmoHandleTag>();
                if (candidate == null || hit.distance >= distance) continue;
                distance = hit.distance;
                tag = candidate;
            }
            return tag != null;
        }

        private void EndDrag()
        {
            if (!_dragging) return;
            _dragging = false;

            IGizmoTargetSource source = _dragSource;
            _dragSource = null;

            if (_maskedTarget != null)
            {
                // The masked path deformed vertices, and EndMaskedTransform commits the
                // vertex-delta undo entry BeginMaskedTransform opened - nothing to record here.
                _maskedTarget.EndMaskedTransform();
                _maskedTarget = null;
                _dragTargets.Clear();
                _dragStartStates.Clear();
                return;
            }

            // A source with its own history keeps it: recording a scene-level transform step for
            // the same drag would make one gesture take two undo presses to reverse. A source
            // WITHOUT one (scene lights, whose targets are plain Transforms) still needs the
            // gizmo's step, or its drags would not be undoable at all.
            bool changed = source != null && source.RecordsOwnUndoStep
                ? AnyTargetMoved()
                : RecordTransformUndo();
            _dragTargets.Clear();
            _dragStartStates.Clear();

            source?.OnGizmoDragEnded(changed);
        }

        /// Commits one whole-object Transpose/Scale drag as a single undo step, so it takes its
        /// turn in scene-wide order alongside brush strokes (see EditHistory).
        ///
        /// This used to record nothing at all, on the reasoning that a Transform drag is free to
        /// reverse by dragging back. That holds only while you can still see where it started -
        /// after a rotate you did not mean, or several drags later, there is no way back by hand,
        /// and undo would silently step past the drag to the stroke before it. The masked path
        /// was always undoable, which made the gap easy to miss.
        ///
        /// A whole TRS triple is captured regardless of which handle was dragged. It is 40 bytes
        /// either way, and recording all three means a step cannot be subtly wrong about what a
        /// drag touched (uniform scale, for one, is a scale drag that also clamps per-axis).
        private bool AnyTargetMoved()
        {
            for (int i = 0; i < _dragTargets.Count; i++)
                if (!_dragStartStates[i].Matches(new TargetTransform(_dragTargets[i]))) return true;
            return false;
        }

        /// Returns whether the drag actually changed anything - the caller uses that to decide
        /// whether an external source needs telling.
        private bool RecordTransformUndo()
        {
            var targets = new GizmoTarget[_dragTargets.Count];
            var before = new TargetTransform[_dragTargets.Count];
            var after = new TargetTransform[_dragTargets.Count];

            bool changed = false;
            for (int i = 0; i < _dragTargets.Count; i++)
            {
                targets[i] = _dragTargets[i];
                before[i] = _dragStartStates[i];
                after[i] = new TargetTransform(_dragTargets[i]);
                if (!before[i].Matches(after[i])) changed = true;
            }

            // A click that picked a handle without moving it is not an edit - recording it would
            // spend an undo press doing nothing visible, which reads exactly like undo is broken.
            if (!changed) return false;

            EditHistory.RecordSceneAction(
                _dragKind == HandleKind.Rotate ? "Rotate" : _dragKind == HandleKind.Move ? "Move" : "Scale",
                () => ApplyTransforms(targets, before),
                () => ApplyTransforms(targets, after),
                null, // holds nothing but the target references themselves - nothing to release
                TransformStepBytes * targets.Length);
            return true;
        }

        // Two Vector3s and a Quaternion per target - the whole payload a transform step retains.
        private const long TransformStepBytes = 40;

        // ApplyTo already no-ops on a dead target: the object a step describes can be deleted from
        // the Scene Graph panel after the fact, and a step that quietly does nothing is exactly
        // what EditHistory.TakeStep already expects of a stale entry.
        private static void ApplyTransforms(GizmoTarget[] targets, TargetTransform[] states)
        {
            for (int i = 0; i < targets.Length; i++) states[i].ApplyTo(targets[i]);
        }

        // Every handler below has the same shape: work out what the drag means, then either
        // write it to the Transform (nothing masked) or hand the equivalent LOCAL-space matrix
        // to the mesh so masked vertices can sit it out (see SculptableMesh.ApplyMaskedTransform).
        // Local space is the natural frame for the masked path: the gizmo pivots on the object's
        // own origin, which IS local zero, so "rotate/scale about the pivot" needs no
        // translate-to-pivot-and-back sandwich.

        private void DragMove(Ray ray)
        {
            float current = ProjectRayOntoAxis(ray, _dragPivot, _dragAxisWorld, _dragPlaneNormal);
            Vector3 worldDelta = _dragAxisWorld * (current - _dragStartValue);

            if (_maskedTarget != null)
            {
                _maskedTarget.ApplyMaskedTransform(
                    Matrix4x4.Translate(_maskedTarget.transform.InverseTransformVector(worldDelta)));
                return;
            }

            for (int i = 0; i < _dragTargets.Count; i++)
            {
                GizmoTarget t = _dragTargets[i];
                if (!t.IsAlive) continue;
                t.Position = _dragStartStates[i].Position + worldDelta;
            }
        }

        private void DragRotate(Ray ray)
        {
            if (!SculptController.RayPlaneIntersect(ray, _dragPivot, _dragAxisWorld, out Vector3 hit)) return;
            float current = AngleOnPlane(hit, _dragPivot, _dragAxisWorld);
            float deltaAngle = Mathf.DeltaAngle(_dragStartValue, current);

            if (_maskedTarget != null)
            {
                // _dragAxisWorld is this same local axis pushed through the object's rotation
                // (see TryBeginDrag), so rotating by the local axis is the same rotation
                // expressed in the frame the vertices actually live in.
                _maskedTarget.ApplyMaskedTransform(
                    Matrix4x4.Rotate(Quaternion.AngleAxis(deltaAngle, AxisDirections[_dragAxis])));
                return;
            }

            Quaternion spin = Quaternion.AngleAxis(deltaAngle, _dragAxisWorld);
            for (int i = 0; i < _dragTargets.Count; i++)
            {
                GizmoTarget t = _dragTargets[i];
                if (!t.IsAlive) continue;
                TargetTransform start = _dragStartStates[i];

                // Orbit the target's position about the shared pivot AND spin its own
                // orientation. For a lone target the pivot IS its position, so the orbit term is
                // exactly zero and this reduces to the plain "rotate in place" it has always been.
                t.Position = _dragPivot + spin * (start.Position - _dragPivot);
                if (t.SupportsRotation) t.Rotation = spin * start.Rotation;
            }
        }

        private void DragScale(Ray ray)
        {
            float current = ProjectRayOntoAxis(ray, _dragPivot, _dragAxisWorld, _dragPlaneNormal);
            float start = Mathf.Abs(_dragStartValue) < 0.01f ? 0.01f : _dragStartValue;
            float ratio = Mathf.Max(0.05f, current / start);

            if (_maskedTarget != null)
            {
                Vector3 axisScale = Vector3.one;
                axisScale[_dragAxis] = ratio;
                _maskedTarget.ApplyMaskedTransform(Matrix4x4.Scale(axisScale));
                return;
            }

            for (int i = 0; i < _dragTargets.Count; i++)
            {
                GizmoTarget t = _dragTargets[i];
                if (!t.IsAlive || !t.SupportsScale) continue;
                TargetTransform startState = _dragStartStates[i];

                // Spread the SET apart along the drag axis as well as scaling each member, so a
                // multi-selection scales as one body rather than each object growing in place.
                // Zero displacement for a lone target (pivot == its own position), which keeps
                // the single-object behaviour bit-for-bit what it was.
                Vector3 offset = startState.Position - _dragPivot;
                float alongAxis = Vector3.Dot(offset, _dragAxisWorld);
                t.Position = startState.Position + _dragAxisWorld * (alongAxis * (ratio - 1f));

                Vector3 scale = startState.Scale;
                scale[_dragAxis] = Mathf.Max(MinScaleAxis, startState.Scale[_dragAxis] * ratio);
                t.LocalScale = scale;
            }
        }

        // No natural plane/axis line to measure an absolute drag against for a uniform-scale
        // handle sitting right at the pivot - unlike Move/Rotate/Scale above, this one is
        // frame-to-frame incremental (vertical mouse delta), same "accumulate as you drag"
        // idiom CameraOrbitController already uses for its own yaw/pitch/zoom.
        private const float UniformScaleSensitivity = 0.004f;

        private void DragUniformScale(Mouse mouse)
        {
            float dy = mouse.delta.ReadValue().y;
            float factor = Mathf.Max(0.01f, 1f + dy * UniformScaleSensitivity);

            // ApplyMaskedTransform (and the multi-target path below) always re-derive from the
            // pre-drag state, so both need the TOTAL factor since drag start - accumulate the
            // incremental one rather than compounding it into the targets.
            _uniformScaleAccum = Mathf.Max(0.01f, _uniformScaleAccum * factor);

            if (_maskedTarget != null)
            {
                _maskedTarget.ApplyMaskedTransform(Matrix4x4.Scale(Vector3.one * _uniformScaleAccum));
                return;
            }

            for (int i = 0; i < _dragTargets.Count; i++)
            {
                GizmoTarget t = _dragTargets[i];
                if (!t.IsAlive || !t.SupportsScale) continue;
                TargetTransform start = _dragStartStates[i];

                t.Position = _dragPivot + (start.Position - _dragPivot) * _uniformScaleAccum;
                Vector3 s = start.Scale * _uniformScaleAccum;
                t.LocalScale = new Vector3(
                    Mathf.Max(MinScaleAxis, s.x), Mathf.Max(MinScaleAxis, s.y), Mathf.Max(MinScaleAxis, s.z));
            }
        }

        // ---------------------------------------------------------------------------- geometry

        /// A plane containing the axis line through pivot, oriented to face the camera as
        /// closely as possible - dragging feels like it's tracking the mouse rather than
        /// sliding along an arbitrary fixed plane. Shared by Move and Scale, which both then
        /// reduce the plane-hit point back down to a single scalar via ProjectRayOntoAxis.
        private Vector3 BuildCameraFacingPlaneNormal(Vector3 axisWorld)
        {
            Vector3 normal = Vector3.Cross(Vector3.Cross(axisWorld, _cam.transform.forward), axisWorld);
            if (normal.sqrMagnitude < 1e-6f) normal = Vector3.Cross(axisWorld, _cam.transform.up);
            return normal.normalized;
        }

        private static float ProjectRayOntoAxis(Ray ray, Vector3 pivot, Vector3 axisWorld, Vector3 planeNormal)
        {
            if (!SculptController.RayPlaneIntersect(ray, pivot, planeNormal, out Vector3 hit)) return 0f;
            return Vector3.Dot(hit - pivot, axisWorld);
        }

        /// Signed angle (degrees) of point around planeOrigin within the plane whose normal is
        /// planeNormal - used to turn a rotate ring's drag into a single scalar Mathf.DeltaAngle
        /// can diff between frames. A ring viewed nearly edge-on (camera looking close to
        /// perpendicular to its own axis) is inherently imprecise with this technique - a known,
        /// accepted limitation of ray/plane rotation gizmos in general, not specific to this one.
        private static float AngleOnPlane(Vector3 point, Vector3 planeOrigin, Vector3 planeNormal)
        {
            Vector3 tangent = Vector3.Cross(planeNormal, Vector3.up);
            if (tangent.sqrMagnitude < 1e-6f) tangent = Vector3.Cross(planeNormal, Vector3.right);
            tangent.Normalize();
            Vector3 bitangent = Vector3.Cross(planeNormal, tangent);
            Vector3 offset = point - planeOrigin;
            return Mathf.Atan2(Vector3.Dot(offset, bitangent), Vector3.Dot(offset, tangent)) * Mathf.Rad2Deg;
        }

        private void BuildHandles()
        {
            _root = new GameObject("TransformGizmoHandles");
            _root.transform.SetParent(transform, false);

            for (int axis = 0; axis < 3; axis++)
            {
                _moveGroups[axis] = CreateShaftHandle(axis, AxisColors[axis], HandleKind.Move, "Move");
                _rotateHandles[axis] = CreateRingHandle(axis, AxisColors[axis]);
                _scaleGroups[axis] = CreateShaftHandle(axis, AxisColors[axis], HandleKind.Scale, "Scale");
            }
            _uniformHandle = CreateUniformScaleHandle();
        }

        private static void ApplyAxisOrientation(Transform t, int axis)
        {
            t.localRotation = Quaternion.FromToRotation(Vector3.up, AxisDirections[axis]);
        }

        private static string AxisName(int axis) => axis == 0 ? "X" : axis == 1 ? "Y" : "Z";

        /// A shaft (thin cylinder from the pivot outward) plus a tip cap - a cone/arrowhead for
        /// Move (matches Blender/Maya's translate-gizmo convention: a directional arrow reads
        /// more naturally as "drag this way" than a cube) and a cube for Scale (matches the
        /// same tools' scale-gizmo convention instead). Both the shaft and the cap carry their
        /// own GizmoHandleTag (both raycastable) pointing at the same kind/axis.
        private GameObject CreateShaftHandle(int axis, Color color, HandleKind kind, string label)
        {
            var group = new GameObject($"{label}_{AxisName(axis)}");
            group.transform.SetParent(_root.transform, false);
            ApplyAxisOrientation(group.transform, axis);

            GameObject shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            shaft.name = "Shaft";
            shaft.transform.SetParent(group.transform, false);
            shaft.transform.localPosition = new Vector3(0f, ShaftLength * 0.5f, 0f);
            shaft.transform.localRotation = Quaternion.identity;
            shaft.transform.localScale = new Vector3(ShaftThickness, ShaftLength * 0.5f, ShaftThickness);
            ApplyUnlitColor(shaft, color);
            shaft.AddComponent<GizmoHandleTag>().Init(kind, axis);

            GameObject cap;
            if (kind == HandleKind.Move)
            {
                cap = new GameObject("Cap", typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
                Mesh arrow = BuildConeMesh(ArrowRadius, ArrowHeight, ArrowSegments);
                cap.GetComponent<MeshFilter>().sharedMesh = arrow;
                cap.GetComponent<MeshCollider>().sharedMesh = arrow;
            }
            else
            {
                cap = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cap.transform.localScale = Vector3.one * CapSize;
            }
            cap.name = "Cap";
            cap.transform.SetParent(group.transform, false);
            cap.transform.localPosition = new Vector3(0f, ShaftLength, 0f);
            cap.transform.localRotation = Quaternion.identity;
            ApplyUnlitColor(cap, color);
            cap.AddComponent<GizmoHandleTag>().Init(kind, axis);

            return group;
        }

        /// Cone with its base at local origin and apex at local (0, height, 0) - positioned so
        /// the base sits at the shaft's tip and the apex points further outward, like a real
        /// arrowhead. Built manually since Unity has no built-in cone primitive (same
        /// constraint the ring handle already worked around with BuildRingMesh).
        private static Mesh BuildConeMesh(float radius, float height, int segments)
        {
            var vertices = new Vector3[segments + 2];
            const int apex = 0, baseCenter = 1;
            vertices[apex] = new Vector3(0f, height, 0f);
            vertices[baseCenter] = Vector3.zero;
            for (int i = 0; i < segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                vertices[2 + i] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            }

            var triangles = new int[segments * 6];
            int ti = 0;
            for (int i = 0; i < segments; i++)
            {
                int a = 2 + i;
                int b = 2 + (i + 1) % segments;
                // Side face - (apex, b, a) order gives an outward-facing normal (verified via
                // Unity_RunCommand rather than assumed - see project memory).
                triangles[ti++] = apex; triangles[ti++] = b; triangles[ti++] = a;
                // Base cap - (baseCenter, a, b) order faces downward/outward, closing the cone.
                triangles[ti++] = baseCenter; triangles[ti++] = a; triangles[ti++] = b;
            }

            var mesh = new Mesh { name = "GizmoArrow" };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// A thin hollow ring (annulus) rather than a filled disc - a solid plane rendered
        /// always-on-top (see ApplyUnlitColor) blocked far too much of the view of whatever's
        /// behind it; a thin circular line only occludes the band it actually traces. Built as
        /// a real double-sided mesh (not a LineRenderer) so the existing collider-based
        /// TryPickHandle raycasting works unchanged - a MeshCollider on a concave shape is fine
        /// here since it's only ever used for raycasting, never physics simulation (no
        /// Rigidbody on these handles).
        private GameObject CreateRingHandle(int axis, Color color)
        {
            var go = new GameObject("Rotate_" + AxisName(axis), typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider));
            go.transform.SetParent(_root.transform, false);
            ApplyAxisOrientation(go.transform, axis);

            Mesh ring = BuildRingMesh(RingOuterRadius * RingInnerRatio, RingOuterRadius, RingSegments);
            go.GetComponent<MeshFilter>().sharedMesh = ring;
            var collider = go.GetComponent<MeshCollider>();
            collider.sharedMesh = ring;

            ApplyUnlitColor(go, color);
            go.AddComponent<GizmoHandleTag>().Init(HandleKind.Rotate, axis);
            return go;
        }

        /// Flat annulus lying in the local XZ plane (normal = local +Y, matching
        /// ApplyAxisOrientation's convention) - double-sided (both triangle windings per quad)
        /// so it stays visible from either side of the ring regardless of camera angle, since a
        /// single-sided disc would vanish from the "back" whenever the camera orbits past it.
        private static Mesh BuildRingMesh(float innerRadius, float outerRadius, int segments)
        {
            var vertices = new Vector3[segments * 2];
            var normals = new Vector3[segments * 2];
            for (int i = 0; i < segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                vertices[i * 2] = new Vector3(cos * innerRadius, 0f, sin * innerRadius);
                vertices[i * 2 + 1] = new Vector3(cos * outerRadius, 0f, sin * outerRadius);
                normals[i * 2] = Vector3.up;
                normals[i * 2 + 1] = Vector3.up;
            }

            var triangles = new int[segments * 12];
            int ti = 0;
            for (int i = 0; i < segments; i++)
            {
                int a = i * 2, b = i * 2 + 1;
                int next = (i + 1) % segments;
                int c = next * 2, d = next * 2 + 1;

                triangles[ti++] = a; triangles[ti++] = b; triangles[ti++] = c;
                triangles[ti++] = b; triangles[ti++] = d; triangles[ti++] = c;
                triangles[ti++] = c; triangles[ti++] = b; triangles[ti++] = a;
                triangles[ti++] = c; triangles[ti++] = d; triangles[ti++] = b;
            }

            var mesh = new Mesh { name = "GizmoRing" };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private GameObject CreateUniformScaleHandle()
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "ScaleUniform";
            go.transform.SetParent(_root.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one * UniformHandleSize;
            ApplyUnlitColor(go, Color.white);
            go.AddComponent<GizmoHandleTag>().Init(HandleKind.UniformScale, -1);
            return go;
        }

        private static readonly int ColorId = Shader.PropertyToID("_Color");

        // Deliberately KEEPS the primitive's default collider (unlike MirrorController's own
        // decorative planes, which destroy theirs) - Physics.RaycastAll/TryPickHandle is how
        // every handle here gets picked.
        //
        // Uses the same X-ray overlay shader (ZTest Always/ZWrite Off) the brush preview cursor
        // already uses, and for the identical reason: a handle is sized relative to the
        // SELECTED object, so a small object (e.g. a primitive spawned concentric with the much
        // bigger main sphere - see PrimitiveSpawner) puts every handle entirely inside the
        // bigger object's opaque geometry. A normal depth-tested material there renders but is
        // completely occluded - this fixes it by always drawing on top, same accepted tradeoff
        // (visible "through" geometry) the brush cursor already made.
        private static void ApplyUnlitColor(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            Shader overlayShader = Shader.Find("Custom/BrushPreviewOverlay");
            var mat = overlayShader != null ? new Material(overlayShader) : new Material(Shader.Find("Sprites/Default"));
            mat.SetColor(ColorId, color);
            if (overlayShader == null) mat.color = color; // Sprites/Default fallback path
            renderer.material = mat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }
    }
}
