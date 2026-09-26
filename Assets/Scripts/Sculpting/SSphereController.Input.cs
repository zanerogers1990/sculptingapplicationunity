using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Picking and gestures.
    ///
    /// The ground rules that make this predictable:
    ///   - every drag is computed ABSOLUTELY from state captured at the press (start positions,
    ///     start radii, the grab point), never by accumulating per-frame deltas - so nothing drifts,
    ///     twists or creeps however long or jittery the drag is, and Esc can put it all back;
    ///   - a drag moves the point you GRABBED, not the sphere's centre, so nothing jumps to the
    ///     cursor on the first frame;
    ///   - grabbing a reflection edits the node it reflects, with the cursor mapped back through
    ///     the mirror - you drag the right arm and the right arm follows your hand;
    ///   - clicks mean the same thing in every mode (select, right-click delete, wheel resize);
    ///     the mode only changes what a DRAG does.
    public partial class SSphereController
    {
        private const int NoNode = SSphereRig.NoNode;

        private const float DragThresholdPixels = 3f;

        /// Forgiveness around every sphere and link, in screen pixels, so a fingertip-sized sphere
        /// on a zoomed-out view is still grabbable. Only consulted when nothing is hit exactly.
        private const float PickSlopPixels = 7f;

        /// Must match SSphereArmatureView.LinkVisualScale, so a link is pickable exactly where it is
        /// drawn.
        private const float LinkPickScale = 0.6f;

        private const float WheelResizePerNotch = 0.08f;
        private const float ScaleDragPerPixel = 0.006f;

        private enum DragKind { None, PendingDraw, Draw, SizeRoot, Move, Scale, Rotate }
        private DragKind _drag = DragKind.None;

        private int _dragNode = NoNode;
        private int _dragParent = NoNode;
        private bool _dragMirrored;
        private Vector2 _dragStartMouse;
        private Vector3 _dragPlaneAnchor;
        private Vector3 _dragGrabPoint;
        private Vector3 _dragPivot;
        private float _dragStartRadius;
        private readonly List<int> _dragNodes = new List<int>();
        private readonly List<Vector3> _dragStartPositions = new List<Vector3>();
        private readonly List<float> _dragStartRadii = new List<float>();

        private int _hoveredLink = NoNode;

        private bool _cursorVisible;
        private Vector3 _cursorLocal;
        private float _cursorRadius;

        private readonly List<SSphereRig.SphereInstance> _pickSpheres = new List<SSphereRig.SphereInstance>();
        private readonly List<SSphereRig.LinkInstance> _pickLinks = new List<SSphereRig.LinkInstance>();
        private int _pickVersion = -1;
        private bool _pickSymmetry;

        private struct Pick
        {
            public int Node;
            public int LinkChild;
            public bool Mirrored;
            /// Where the picked instance is DRAWN, rig-local - a node's centre, or the point on a
            /// link's centre line. Drag planes are anchored here, so they sit under the cursor.
            public Vector3 DrawnPoint;
            /// Parameter along a picked link, 0 at the parent end.
            public float LinkT;

            public bool IsNode => Node != NoNode;
            public bool IsLink => LinkChild != NoNode;

            public static Pick None => new Pick { Node = NoNode, LinkChild = NoNode };
        }

        // -------------------------------------------------------------------------- input

        /// Whether the cursor is on a sphere's body - asked by TransformGizmo before it takes a
        /// press on one of its handles. The Move-mode arrows start at the selected sphere's centre,
        /// so without this every grab of that sphere became a one-axis drag. The sphere body is the
        /// sphere; the arrows are the arrows only where they stick out past it.
        public bool ClaimsPointer(Ray ray) => _cam != null && !_rig.IsEmpty && PickAt(ray).IsNode;

        private void HandleInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;
            // The turntable's clean view is look-only.
            if (TurntableController.CleanViewActive) return;

            Keyboard kb = Keyboard.current;
            bool shift = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
            bool ctrl = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            // Alt belongs to the camera: orbiting must never also edit the rig.
            bool alt = kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed);
            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

            Vector2 mousePos = mouse.position.ReadValue();
            Ray ray = _cam.ScreenPointToRay(mousePos);

            if (HandleKeys(kb, shift)) return;

            if (_drag != DragKind.None)
            {
                _cursorVisible = false;
                IsHoveringNode = _dragNode != NoNode;
                HandleDragWheel(mouse);
                if (mouse.leftButton.isPressed) UpdateDrag(ray, mousePos);
                else EndDrag();
                return;
            }

            TransformGizmo gizmo = Gizmo;
            if (gizmo != null && gizmo.IsDragging)
            {
                ClearHover();
                return;
            }

            bool blocked = overUI || alt;
            Pick pick = blocked ? Pick.None : PickAt(ray);

            // The axis gizmo gets the press only where the cursor is NOT on a sphere - the same
            // rule TransformGizmo applies through ClaimsPointer, so exactly one of the two acts.
            bool gizmoHasPointer = !blocked && !pick.IsNode && gizmo != null && gizmo.IsPointerOverHandle(ray);

            HoveredNode = pick.Node;
            _hoveredLink = !pick.IsNode && !gizmoHasPointer && EditMode == SSphereEditMode.Draw ? pick.LinkChild : NoNode;
            IsHoveringNode = pick.IsNode;

            UpdatePlacementCursor(blocked || gizmoHasPointer, pick, ray);
            if (blocked || gizmoHasPointer) return;

            HandleHoverWheel(mouse, pick);

            if (pick.IsNode && (mouse.rightButton.wasPressedThisFrame || (ctrl && mouse.leftButton.wasPressedThisFrame)))
            {
                DeleteNode(pick.Node);
                return;
            }

            if (mouse.leftButton.wasPressedThisFrame && !ctrl) BeginDrag(pick, ray, mousePos, shift);
        }

        /// Returns true when a key press consumed the frame.
        private bool HandleKeys(Keyboard kb, bool shift)
        {
            if (kb == null || InputFocus.IsTypingInText()) return false;

            if (_drag != DragKind.None)
            {
                if (kb.escapeKey.wasPressedThisFrame)
                {
                    CancelDrag();
                    return true;
                }
                return false;
            }

            // Z is not read here: SculptController.HandleUndoRedoKeys dispatches it, asking
            // HandlesUndoKey first - see its remarks.

            // A toggles the solid skin preview, as in ZBrush - the quickest way to check the real
            // shape and get straight back to the spheres.
            if (kb.aKey.wasPressedThisFrame)
            {
                PreviewMode = !PreviewMode;
                return true;
            }

            if (_rig.IsAlive(SelectedNode) && (kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame))
            {
                DeleteNode(SelectedNode);
                return true;
            }

            return false;
        }

        private void ClearHover()
        {
            HoveredNode = NoNode;
            _hoveredLink = NoNode;
            IsHoveringNode = false;
            _cursorVisible = false;
        }

        private void HandleHoverWheel(Mouse mouse, Pick pick)
        {
            if (!pick.IsNode) return;
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f) return;

            SSphereRig.Node node = _rig.Get(pick.Node);
            if (node == null) return;
            // Coalesced: a resize is a run of notches, and one undo step per notch would take a
            // dozen presses to walk back.
            BeginRigEdit("Resize Sphere", CoalesceSeconds);
            _rig.SetRadius(pick.Node, Mathf.Clamp(node.Radius * (1f + Mathf.Sign(scroll) * WheelResizePerNotch),
                                                  MinNodeRadius, MaxNodeRadius));
        }

        /// The wheel keeps resizing the sphere being dragged - so a limb can be drawn out and
        /// sized in one motion, without letting go.
        private void HandleDragWheel(Mouse mouse)
        {
            if (_drag == DragKind.Scale || _drag == DragKind.PendingDraw) return;
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f) return;
            SSphereRig.Node node = _rig.Get(_dragNode);
            if (node == null) return;
            _rig.SetRadius(_dragNode, Mathf.Clamp(node.Radius * (1f + Mathf.Sign(scroll) * WheelResizePerNotch),
                                                  MinNodeRadius, MaxNodeRadius));
        }

        // -------------------------------------------------------------------------- drags

        private void BeginDrag(Pick pick, Ray ray, Vector2 mousePos, bool shift)
        {
            _dragStartMouse = mousePos;
            _dragMirrored = pick.Mirrored;
            _dragPlaneAnchor = RigToWorld(pick.DrawnPoint);

            if (pick.IsNode || (pick.IsLink && EditMode != SSphereEditMode.Draw))
            {
                // A link grabbed outside Draw mode is a handle on the bone's END - the child it runs
                // into - which is what "move/scale/rotate this limb" means.
                int node = pick.IsNode ? pick.Node : pick.LinkChild;
                SelectedNode = node;

                switch (EditMode)
                {
                    case SSphereEditMode.Draw:
                        // Opened now so the snapshot predates the child; the commit throws it away
                        // again if the press turns out to be a plain selection click.
                        BeginRigEdit("Draw Sphere");
                        _dragParent = node;
                        _dragNode = NoNode;
                        _drag = DragKind.PendingDraw;
                        break;

                    case SSphereEditMode.Move:
                        BeginMove(node, singleNode: shift, ray, "Move Sphere");
                        break;

                    case SSphereEditMode.Scale:
                        BeginRigEdit("Scale Sphere");
                        CaptureDragNodes(node, subtree: shift);
                        _dragNode = node;
                        _drag = DragKind.Scale;
                        break;

                    case SSphereEditMode.Rotate:
                        BeginRotate(node, ray);
                        break;
                }
                return;
            }

            if (pick.IsLink)
            {
                InsertOnLink(pick, ray);
                return;
            }

            // Empty space. Starts a rig when there is none; on an attached body it starts another
            // limb root wherever the body is clicked. Anything else is a click in the void, which
            // deselects - and never spawns a stray disconnected sphere.
            bool onSurface = TrySurfacePoint(ray, out _);
            if (_rig.IsEmpty || (EditMode == SSphereEditMode.Draw && onSurface))
            {
                PlaceRoot(ray);
                return;
            }
            SelectedNode = NoNode;
        }

        private void UpdateDrag(Ray ray, Vector2 mousePos)
        {
            bool pastThreshold = (mousePos - _dragStartMouse).magnitude >= DragThresholdPixels;

            switch (_drag)
            {
                case DragKind.PendingDraw:
                {
                    if (!pastThreshold || !TryCanonicalDragPoint(ray, out Vector3 point)) return;
                    SSphereRig.Node parent = _rig.Get(_dragParent);
                    if (parent == null) { EndDrag(); return; }

                    float radius = Mathf.Clamp(parent.Radius * _childTaper, MinNodeRadius, MaxNodeRadius);
                    _dragNode = _rig.AddChild(_dragParent, SnapIfSymmetric(point, radius), radius);
                    SelectedNode = _dragNode;
                    _drag = DragKind.Draw;
                    break;
                }

                case DragKind.Draw:
                {
                    SSphereRig.Node node = _rig.Get(_dragNode);
                    if (node == null) { EndDrag(); return; }
                    if (TryCanonicalDragPoint(ray, out Vector3 point))
                        _rig.SetPosition(_dragNode, SnapIfSymmetric(point, node.Radius));
                    break;
                }

                case DragKind.SizeRoot:
                {
                    // Until the cursor actually moves, the sphere keeps the ghost's size - so a
                    // plain click gives exactly what the ghost promised.
                    if (!pastThreshold) return;
                    var plane = new Plane(-_cam.transform.forward, _dragPlaneAnchor);
                    if (!plane.Raycast(ray, out float enter)) return;
                    float radius = Vector3.Distance(ray.GetPoint(enter), _dragPlaneAnchor);
                    _rig.SetRadius(_dragNode, Mathf.Clamp(radius, MinNodeRadius, MaxNodeRadius));
                    break;
                }

                case DragKind.Move:
                    UpdateMove(ray);
                    break;

                case DragKind.Scale:
                {
                    Vector2 delta = mousePos - _dragStartMouse;
                    // Exponential, so the same hand movement is the same PROPORTIONAL change at any
                    // size, and a radius can never be dragged through zero. Right or up grows.
                    float factor = Mathf.Exp((delta.x + delta.y) * ScaleDragPerPixel);
                    for (int i = 0; i < _dragNodes.Count; i++)
                        _rig.SetRadius(_dragNodes[i], Mathf.Clamp(_dragStartRadii[i] * factor, MinNodeRadius, MaxNodeRadius));
                    break;
                }

                case DragKind.Rotate:
                {
                    if (!TryCanonicalDragPoint(ray, out Vector3 point)) return;
                    Vector3 from = _dragGrabPoint - _dragPivot;
                    Vector3 to = point - _dragPivot;
                    if (from.sqrMagnitude < 1e-10f || to.sqrMagnitude < 1e-10f) return;
                    Quaternion rotation = Quaternion.FromToRotation(from, to);
                    for (int i = 0; i < _dragNodes.Count; i++)
                        _rig.SetPosition(_dragNodes[i], _dragPivot + rotation * (_dragStartPositions[i] - _dragPivot));
                    break;
                }
            }
        }

        private void EndDrag()
        {
            if (_drag == DragKind.None) return;
            _drag = DragKind.None;
            _dragNode = NoNode;
            _dragParent = NoNode;
            _dragNodes.Clear();
            _dragStartPositions.Clear();
            _dragStartRadii.Clear();
            // One gesture, one undo step - closed here rather than per frame.
            CommitRigEdit();
        }

        /// Esc mid-drag: put the rig back exactly as it was at the press.
        private void CancelDrag()
        {
            _drag = DragKind.None;
            _dragNode = NoNode;
            _dragParent = NoNode;
            _dragNodes.Clear();
            _dragStartPositions.Clear();
            _dragStartRadii.Clear();
            CancelRigEdit();
        }

        private void BeginMove(int node, bool singleNode, Ray ray, string label)
        {
            BeginRigEdit(label);
            CaptureDragNodes(node, subtree: !singleNode);
            _dragNode = node;
            _drag = DragKind.Move;
            if (!TryCanonicalDragPoint(ray, out _dragGrabPoint)) _dragGrabPoint = _rig.Get(node).Position;
        }

        private void UpdateMove(Ray ray)
        {
            SSphereRig.Node node = _rig.Get(_dragNode);
            if (node == null || _dragNodes.Count == 0) { EndDrag(); return; }

            Vector3 target;
            // On an attached body the sphere rides its surface - a shoulder dragged across a torso
            // stays ON the torso, which no view-aligned plane can do as the camera orbits.
            if (TrySurfacePoint(ray, out Vector3 surface))
                target = Canonical(WorldToRig(surface));
            else if (TryCanonicalDragPoint(ray, out Vector3 point))
                target = _dragStartPositions[0] + (point - _dragGrabPoint);
            else
                return;

            target = SnapIfSymmetric(target, node.Radius);
            Vector3 delta = target - _dragStartPositions[0];
            for (int i = 0; i < _dragNodes.Count; i++)
                _rig.SetPosition(_dragNodes[i], _dragStartPositions[i] + delta);
        }

        private void BeginRotate(int node, Ray ray)
        {
            SSphereRig.Node n = _rig.Get(node);
            // A root has no joint to swing about, so rotating it moves the whole rig instead - the
            // only useful meaning, and it saves a trip to Move mode.
            if (n == null || !_rig.IsAlive(n.Parent))
            {
                BeginMove(node, singleNode: false, ray, "Move Rig");
                return;
            }

            BeginRigEdit("Rotate Branch");
            CaptureDragNodes(node, subtree: true);
            _dragNode = node;
            _dragPivot = _rig.Nodes[n.Parent].Position;
            if (!TryCanonicalDragPoint(ray, out _dragGrabPoint)) _dragGrabPoint = n.Position;
            _drag = DragKind.Rotate;
        }

        private void CaptureDragNodes(int node, bool subtree)
        {
            _dragNodes.Clear();
            _dragStartPositions.Clear();
            _dragStartRadii.Clear();
            if (subtree) _rig.CollectSubtree(node, _dragNodes);
            else if (_rig.IsAlive(node)) _dragNodes.Add(node);
            for (int i = 0; i < _dragNodes.Count; i++)
            {
                _dragStartPositions.Add(_rig.Nodes[_dragNodes[i]].Position);
                _dragStartRadii.Add(_rig.Nodes[_dragNodes[i]].Radius);
            }
        }

        /// The cursor on the camera-facing plane through the grabbed point, rig-local, mapped back
        /// through the mirror when the grab was on a reflection.
        private bool TryCanonicalDragPoint(Ray ray, out Vector3 point)
        {
            var plane = new Plane(-_cam.transform.forward, _dragPlaneAnchor);
            if (plane.Raycast(ray, out float enter))
            {
                point = Canonical(WorldToRig(ray.GetPoint(enter)));
                return true;
            }
            point = default;
            return false;
        }

        private Vector3 Canonical(Vector3 rigPoint) => _dragMirrored ? SSphereRig.Mirror(rigPoint) : rigPoint;

        // ---------------------------------------------------------------------- creation

        /// Drops a root where the cursor is (on the attached body if it is over it) and leaves the
        /// press open as a sizing drag: click for the ghost's size, or drag out to size it by hand.
        private void PlaceRoot(Ray ray)
        {
            Vector3 world = RootPlacementPoint(ray);
            BeginRigEdit("Place Sphere");
            AnchorRigRoot(world);

            float radius = DefaultRootRadius();
            _dragNode = _rig.AddRoot(SnapIfSymmetric(WorldToRig(world), radius), radius);
            SelectedNode = _dragNode;
            _dragMirrored = false;
            _dragPlaneAnchor = RigToWorld(_rig.Nodes[_dragNode].Position);
            _dragStartRadius = radius;
            _drag = DragKind.SizeRoot;
        }

        /// Splices a joint into the clicked bone, sized to blend with its neighbours, and keeps the
        /// press going as a single-sphere Move - so a click adds a joint and a drag bends the limb
        /// at it, in one motion.
        private void InsertOnLink(Pick pick, Ray ray)
        {
            SSphereRig.Node child = _rig.Get(pick.LinkChild);
            SSphereRig.Node parent = child != null ? _rig.Get(child.Parent) : null;
            if (parent == null) return;

            // Interpolated between the CANONICAL ends, which is the canonical point whether the
            // drawn link was the original or its reflection.
            Vector3 position = Vector3.Lerp(parent.Position, child.Position, pick.LinkT);
            float radius = Mathf.Lerp(parent.Radius, child.Radius, pick.LinkT);

            BeginRigEdit("Insert Sphere");
            int inserted = _rig.InsertBetween(pick.LinkChild, SnapIfSymmetric(position, radius), radius);
            if (inserted == NoNode)
            {
                CommitRigEdit();
                return;
            }

            SelectedNode = inserted;
            BeginMove(inserted, singleNode: true, ray, "Insert Sphere");
        }

        private Vector3 RootPlacementPoint(Ray ray) =>
            TrySurfacePoint(ray, out Vector3 surface) ? surface : ViewPlanePoint(ray);

        /// The camera-facing plane through the orbit pivot - where the artist is already looking,
        /// rather than the world origin, which may be off screen.
        private Vector3 ViewPlanePoint(Ray ray)
        {
            Vector3 viewAnchor = Vector3.zero;
            CameraOrbitController orbit = Orbit;
            if (orbit != null) orbit.GetView(out _, out _, out _, out viewAnchor);

            var viewPlane = new Plane(-_cam.transform.forward, viewAnchor);
            return viewPlane.Raycast(ray, out float enter) ? ray.GetPoint(enter) : viewAnchor;
        }

        /// A ghost of the sphere a click would place, shown only where a click WOULD place one: in
        /// empty space with no rig yet, or over the attached body in Draw mode.
        private void UpdatePlacementCursor(bool blocked, Pick pick, Ray ray)
        {
            _cursorVisible = false;
            if (blocked || pick.IsNode || pick.IsLink || PreviewMode) return;

            bool onSurface = TrySurfacePoint(ray, out Vector3 surface);
            if (!_rig.IsEmpty && !(EditMode == SSphereEditMode.Draw && onSurface)) return;

            _cursorLocal = WorldToRig(onSurface ? surface : ViewPlanePoint(ray));
            _cursorRadius = DefaultRootRadius();
            _cursorVisible = true;
        }

        // ------------------------------------------------------------------------ picking

        private void RefreshPickGeometry()
        {
            if (_rig.Version == _pickVersion && _symmetryX == _pickSymmetry) return;
            _pickVersion = _rig.Version;
            _pickSymmetry = _symmetryX;
            _rig.CollectGeometry(_symmetryX, _pickSpheres, _pickLinks);
        }

        /// What is under the cursor, tested analytically against exactly the instances the armature
        /// view draws. Precedence: a sphere hit squarely, then a link hit squarely, then anything
        /// within the pixel slop - so the forgiveness that makes tiny spheres grabbable never steals
        /// a click from something the cursor is genuinely on.
        private Pick PickAt(Ray worldRay)
        {
            if (_rigRoot == null || _rig.IsEmpty) return Pick.None;
            RefreshPickGeometry();

            Vector3 origin = WorldToRig(worldRay.origin);
            Vector3 dir = _rigRoot.InverseTransformDirection(worldRay.direction).normalized;
            Vector3 camForward = _rigRoot.InverseTransformDirection(_cam.transform.forward);
            Vector3 camPos = WorldToRig(_cam.transform.position);

            Pick hardSphere = Pick.None, softSphere = Pick.None, hardLink = Pick.None, softLink = Pick.None;
            float hardSphereT = float.MaxValue, softSphereT = float.MaxValue;
            float hardLinkT = float.MaxValue, softLinkT = float.MaxValue;

            for (int i = 0; i < _pickSpheres.Count; i++)
            {
                SSphereRig.SphereInstance s = _pickSpheres[i];
                float slop = PickSlopPixels * PixelSizeAt(s.Centre, camPos, camForward);

                if (RaySphere(origin, dir, s.Centre, s.Radius, out float t))
                {
                    if (t < hardSphereT) { hardSphereT = t; hardSphere = NodePick(s); }
                }
                else if (RaySphere(origin, dir, s.Centre, s.Radius + slop, out t) && t < softSphereT)
                {
                    softSphereT = t;
                    softSphere = NodePick(s);
                }
            }
            if (hardSphere.IsNode) return hardSphere;

            for (int i = 0; i < _pickLinks.Count; i++)
            {
                SSphereRig.LinkInstance l = _pickLinks[i];
                RaySegment(origin, dir, l.A, l.B, out float t, out float u, out Vector3 onAxis, out float distance);
                float radius = Mathf.Lerp(l.RadiusA, l.RadiusB, u) * LinkPickScale;
                var pick = new Pick
                {
                    Node = NoNode, LinkChild = l.Child, Mirrored = l.Mirrored, DrawnPoint = onAxis, LinkT = u
                };

                if (distance <= radius)
                {
                    if (t < hardLinkT) { hardLinkT = t; hardLink = pick; }
                }
                else if (distance <= radius + PickSlopPixels * PixelSizeAt(onAxis, camPos, camForward) && t < softLinkT)
                {
                    softLinkT = t;
                    softLink = pick;
                }
            }

            if (hardLink.IsLink) return hardLink;
            if (softSphere.IsNode) return softSphere;
            return softLink;
        }

        private static Pick NodePick(SSphereRig.SphereInstance s) =>
            new Pick { Node = s.Node, LinkChild = NoNode, Mirrored = s.Mirrored, DrawnPoint = s.Centre };

        /// Rig-local size of one screen pixel at `point`.
        private float PixelSizeAt(Vector3 point, Vector3 camPos, Vector3 camForward)
        {
            float height = Mathf.Max(1f, _cam.pixelHeight);
            if (_cam.orthographic) return 2f * _cam.orthographicSize / height;
            float depth = Mathf.Max(Vector3.Dot(point - camPos, camForward), _cam.nearClipPlane);
            return 2f * depth * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / height;
        }

        /// Nearest non-negative ray parameter at which a unit-direction ray meets the sphere.
        private static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 centre, float radius, out float t)
        {
            Vector3 oc = origin - centre;
            float b = Vector3.Dot(oc, dir);
            float c = Vector3.Dot(oc, oc) - radius * radius;
            float disc = b * b - c;
            t = 0f;
            if (disc < 0f) return false;
            float root = Mathf.Sqrt(disc);
            t = -b - root;
            if (t < 0f) t = -b + root;
            return t >= 0f;
        }

        /// Closest approach between a unit-direction ray and the segment a-b: the ray parameter,
        /// the segment parameter, the segment point, and the gap between them.
        private static void RaySegment(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b,
                                       out float t, out float u, out Vector3 onSegment, out float distance)
        {
            Vector3 ab = b - a;
            float abLen2 = Vector3.Dot(ab, ab);
            Vector3 ao = origin - a;

            if (abLen2 < 1e-12f)
            {
                u = 0f;
                t = Mathf.Max(0f, -Vector3.Dot(ao, dir));
            }
            else
            {
                float dab = Vector3.Dot(dir, ab);
                float dao = Vector3.Dot(dir, ao);
                float abao = Vector3.Dot(ab, ao);
                float denom = abLen2 - dab * dab;
                u = denom > 1e-10f ? Mathf.Clamp01((abao - dao * dab) / denom) : 0f;
                t = u * dab - dao;
                if (t < 0f)
                {
                    t = 0f;
                    u = Mathf.Clamp01(abao / abLen2);
                }
                else
                {
                    u = Mathf.Clamp01(Vector3.Dot(origin + dir * t - a, ab) / abLen2);
                }
            }

            onSegment = a + ab * u;
            distance = (origin + dir * t - onSegment).magnitude;
        }
    }
}
