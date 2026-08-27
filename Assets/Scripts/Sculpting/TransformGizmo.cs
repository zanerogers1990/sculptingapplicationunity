using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Sculpting
{
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

        private Camera _cam;
        private GameObject _root;
        private readonly GameObject[] _moveGroups = new GameObject[3];
        private readonly GameObject[] _scaleGroups = new GameObject[3];
        private readonly GameObject[] _rotateHandles = new GameObject[3];
        private GameObject _uniformHandle;

        public GizmoMode Mode { get; private set; } = GizmoMode.Sculpt;
        public void SetMode(GizmoMode mode) => Mode = mode;

        // Drag state - captured once at mouse-press, reused every frame of the drag. The
        // dragged Transform is cached explicitly (not re-read from Target each frame) so a
        // selection change mid-drag (e.g. clicking a different Scene Graph row) can't yank an
        // in-progress drag onto a different object.
        private Transform _dragTarget;
        private HandleKind _dragKind;
        private int _dragAxis;
        private Vector3 _dragStartPos;
        private Quaternion _dragStartRot;
        private Vector3 _dragStartScale;
        private Vector3 _dragAxisWorld;
        private Vector3 _dragPlaneNormal;
        private float _dragStartValue; // meaning depends on _dragKind: signed offset along axis (Move/Scale) or angle in degrees (Rotate)

        private void Awake()
        {
            _cam = Camera.main;
            BuildHandles();
            _root.SetActive(false);
        }

        private void Update()
        {
            if (_cam == null) _cam = Camera.main;
            SculptableMesh target = Target;
            bool active = Mode != GizmoMode.Sculpt && target != null && _cam != null;

            if (_root.activeSelf != active) _root.SetActive(active);
            if (!active) { EndDrag(); return; }

            Transform t = target.transform;
            _root.transform.SetPositionAndRotation(t.position, t.rotation);
            _root.transform.localScale = Vector3.one * ComputeArmLength(target);

            bool transpose = Mode == GizmoMode.Transpose;
            for (int i = 0; i < 3; i++)
            {
                _moveGroups[i].SetActive(transpose);
                _rotateHandles[i].SetActive(transpose);
                _scaleGroups[i].SetActive(!transpose);
            }
            _uniformHandle.SetActive(!transpose);

            HandleDragInput(t);
        }

        private float ComputeArmLength(SculptableMesh target)
        {
            Mesh mesh = target.Mesh;
            if (mesh == null) return MinArmLength;
            Vector3 e = mesh.bounds.extents;
            float avgLocalExtent = (e.x + e.y + e.z) / 3f;
            Vector3 s = target.transform.lossyScale;
            float worldScale = (s.x + s.y + s.z) / 3f;
            return Mathf.Max(MinArmLength, avgLocalExtent * worldScale * ArmLengthFactor);
        }

        // ------------------------------------------------------------------------- drag input

        private void HandleDragInput(Transform liveTarget)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            if (_dragTarget == null)
            {
                bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
                bool altHeld = Keyboard.current != null && Keyboard.current.leftAltKey.isPressed;
                if (!overUI && !altHeld && mouse.leftButton.wasPressedThisFrame)
                    TryBeginDrag(mouse, liveTarget);
                return;
            }

            if (!mouse.leftButton.isPressed) { EndDrag(); return; }

            Ray ray = _cam.ScreenPointToRay(mouse.position.ReadValue());
            switch (_dragKind)
            {
                case HandleKind.Move: DragMove(ray); break;
                case HandleKind.Rotate: DragRotate(ray); break;
                case HandleKind.Scale: DragScale(ray); break;
                case HandleKind.UniformScale: DragUniformScale(mouse); break;
            }
        }

        private void TryBeginDrag(Mouse mouse, Transform liveTarget)
        {
            Ray ray = _cam.ScreenPointToRay(mouse.position.ReadValue());
            if (!TryPickHandle(ray, out GizmoHandleTag tag)) return;

            _dragTarget = liveTarget;
            _dragKind = tag.Kind;
            _dragAxis = tag.Axis;
            _dragStartPos = liveTarget.position;
            _dragStartRot = liveTarget.rotation;
            _dragStartScale = liveTarget.localScale;

            if (_dragKind == HandleKind.Move || _dragKind == HandleKind.Scale)
            {
                _dragAxisWorld = liveTarget.TransformDirection(AxisDirections[_dragAxis]);
                _dragPlaneNormal = BuildCameraFacingPlaneNormal(_dragAxisWorld);
                _dragStartValue = ProjectRayOntoAxis(ray, _dragStartPos, _dragAxisWorld, _dragPlaneNormal);
            }
            else if (_dragKind == HandleKind.Rotate)
            {
                _dragAxisWorld = liveTarget.TransformDirection(AxisDirections[_dragAxis]);
                if (SculptController.RayPlaneIntersect(ray, _dragStartPos, _dragAxisWorld, out Vector3 hit))
                    _dragStartValue = AngleOnPlane(hit, _dragStartPos, _dragAxisWorld);
            }
        }

        /// Physics.RaycastAll rather than a single Raycast, keeping only hits tagged as a
        /// gizmo handle and picking the closest one - the plain sculpted mesh's own (still-
        /// present, just no longer brush-hot-path) MeshCollider sits between the camera and a
        /// handle on the far side of the object for plenty of camera angles, and a single
        /// Raycast would report that closer, untagged hit instead of the handle the user is
        /// actually trying to click.
        private static bool TryPickHandle(Ray ray, out GizmoHandleTag tag)
        {
            tag = null;
            float bestDist = float.MaxValue;
            foreach (RaycastHit hit in Physics.RaycastAll(ray, 1000f))
            {
                GizmoHandleTag candidate = hit.collider.GetComponentInParent<GizmoHandleTag>();
                if (candidate == null || hit.distance >= bestDist) continue;
                bestDist = hit.distance;
                tag = candidate;
            }
            return tag != null;
        }

        private void EndDrag()
        {
            _dragTarget = null;
        }

        private void DragMove(Ray ray)
        {
            float current = ProjectRayOntoAxis(ray, _dragStartPos, _dragAxisWorld, _dragPlaneNormal);
            _dragTarget.position = _dragStartPos + _dragAxisWorld * (current - _dragStartValue);
        }

        private void DragRotate(Ray ray)
        {
            if (!SculptController.RayPlaneIntersect(ray, _dragStartPos, _dragAxisWorld, out Vector3 hit)) return;
            float current = AngleOnPlane(hit, _dragStartPos, _dragAxisWorld);
            float deltaAngle = Mathf.DeltaAngle(_dragStartValue, current);
            _dragTarget.rotation = Quaternion.AngleAxis(deltaAngle, _dragAxisWorld) * _dragStartRot;
        }

        private void DragScale(Ray ray)
        {
            float current = ProjectRayOntoAxis(ray, _dragStartPos, _dragAxisWorld, _dragPlaneNormal);
            float start = Mathf.Abs(_dragStartValue) < 0.01f ? 0.01f : _dragStartValue;
            float ratio = Mathf.Max(0.05f, current / start);

            Vector3 scale = _dragStartScale;
            switch (_dragAxis)
            {
                case 0: scale.x = Mathf.Max(MinScaleAxis, _dragStartScale.x * ratio); break;
                case 1: scale.y = Mathf.Max(MinScaleAxis, _dragStartScale.y * ratio); break;
                default: scale.z = Mathf.Max(MinScaleAxis, _dragStartScale.z * ratio); break;
            }
            _dragTarget.localScale = scale;
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
            Vector3 s = _dragTarget.localScale * factor;
            _dragTarget.localScale = new Vector3(
                Mathf.Max(MinScaleAxis, s.x), Mathf.Max(MinScaleAxis, s.y), Mathf.Max(MinScaleAxis, s.z));
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
