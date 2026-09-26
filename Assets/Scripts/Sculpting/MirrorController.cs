using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Something other than the mesh that can share a mirror plane and wants the drawn plane big
    /// enough to cover it - see MirrorController.RegisterPlaneExtentProvider.
    public interface IMirrorPlaneExtentProvider
    {
        /// World-space half-size to cover for a mirror plane through `planeOrigin`, or 0 when
        /// this provider does not share that plane.
        float WorldExtentForPlaneAt(Vector3 planeOrigin);
    }

    /// The axis radial symmetry repeats around - a local axis through the object's origin.
    /// Stored by value in save files, so new members go on the end.
    public enum RadialAxis { X, Y, Z, Custom }

    /// Where an object's symmetry lives (Nomad's Local/World): through the object's own origin and
    /// along its own axes, moving with it - or fixed at the world origin along the world axes,
    /// with the object free to move through it. Stored by value in save files.
    public enum SymmetrySpace { Local, World }

    /// The sculpting symmetry of one object: up to three axes of mirroring, plus N-fold radial
    /// symmetry around an axis - through the sculptable mesh's local origin along its own axes
    /// (Local space), or through the world origin along the world axes (World space, see
    /// SymmetrySpace).
    /// Each enabled mirror axis reflects every brush stroke, any combination can be active at
    /// once (e.g. X+Y mirrors a stroke into all four quadrants), and radial symmetry repeats it
    /// at N evenly spaced angles - combined with the mirrors when both are on (see
    /// SymmetryGroup, which turns these settings into the list of ops a stroke is applied under).
    ///
    /// Draws a transparent, axis-colored plane per active mirror axis - following Unity's gizmo
    /// convention of red/green/blue for X/Y/Z - and, for radial symmetry, the axis line with one
    /// spoke per repeat, so where the symmetry sits is visible in the scene. Guide visibility has
    /// its own toggle independent of whether symmetry is enabled, so it can be checked and then
    /// hidden again.
    [RequireComponent(typeof(SculptableMesh))]
    public class MirrorController : MonoBehaviour
    {
        [Header("Mirror Axes (local space, through object origin)")]
        [SerializeField] private bool mirrorX;
        [SerializeField] private bool mirrorY;
        [SerializeField] private bool mirrorZ;
        [SerializeField] private bool showPlanes = true;

        // Y by default: the axis a Lathe object is revolved around (see LatheController.Create),
        // and the usual "up" of a bust or a vase.
        [Header("Radial Symmetry (around a local axis through object origin)")]
        [SerializeField] private bool radial;
        [SerializeField, Range(SymmetryGroup.MinRadialCount, SymmetryGroup.MaxRadialCount)] private int radialCount = 8;
        [SerializeField] private RadialAxis radialAxis = RadialAxis.Y;
        [SerializeField] private Vector3 radialCustomAxis = Vector3.up;

        [SerializeField] private SymmetrySpace space = SymmetrySpace.Local;

        // Unity's axis-handle/gizmo convention: X red, Y green, Z blue.
        private static readonly Color XColor = new Color(1f, 0.25f, 0.25f);
        private static readonly Color YColor = new Color(0.35f, 1f, 0.35f);
        private static readonly Color ZColor = new Color(0.3f, 0.55f, 1f);
        private const float PlaneAlpha = 0.18f;
        // Planes are sized off the mesh's local bounds each frame, padded so the mirror
        // plane visibly extends past the silhouette instead of clipping it.
        private const float PlanePadding = 1.4f;

        // Radial guide colour for a custom axis - none of the three axis colours, so it never reads
        // as one of them.
        private static readonly Color CustomAxisColor = new Color(1f, 0.8f, 0.25f);
        private const float RadialGuideAlpha = 0.6f;

        private SculptableMesh _sculptableMesh;
        private Transform _planeX, _planeY, _planeZ;

        // Unity's built-in Quad lies in the local XY plane (normal +Z) by default - that's already
        // the Z=0 plane. Rotating it 90 deg about Y swings its face into the YZ plane (X=0); 90 deg
        // about X swings it into the XZ plane (Y=0). Local rotations under this object in Local
        // space, world rotations at the world origin in World space.
        private static readonly Quaternion PlaneXRotation = Quaternion.Euler(0f, 90f, 0f);
        private static readonly Quaternion PlaneYRotation = Quaternion.Euler(90f, 0f, 0f);
        private static readonly Quaternion PlaneZRotation = Quaternion.identity;
        private Transform _radialGuide;
        private Mesh _radialGuideMesh;
        private Material _radialGuideMaterial;
        private int _radialGuideCount;

        public bool MirrorX { get => mirrorX; set => mirrorX = value; }
        public bool MirrorY { get => mirrorY; set => mirrorY = value; }
        public bool MirrorZ { get => mirrorZ; set => mirrorZ = value; }
        public bool ShowPlanes { get => showPlanes; set => showPlanes = value; }

        public bool Radial { get => radial; set => radial = value; }

        public int RadialCount
        {
            get => radialCount;
            set => radialCount = Mathf.Clamp(value, SymmetryGroup.MinRadialCount, SymmetryGroup.MaxRadialCount);
        }

        public RadialAxis RadialAxisChoice { get => radialAxis; set => radialAxis = value; }

        public SymmetrySpace Space { get => space; set => space = value; }

        /// The direction used when RadialAxisChoice is Custom. Stored as given; a zero vector
        /// falls back to Y when used (see RadialAxisVector).
        public Vector3 RadialCustomAxis { get => radialCustomAxis; set => radialCustomAxis = value; }

        /// The radial axis as a unit local-space direction.
        public Vector3 RadialAxisVector
        {
            get
            {
                switch (radialAxis)
                {
                    case RadialAxis.X: return Vector3.right;
                    case RadialAxis.Z: return Vector3.forward;
                    case RadialAxis.Custom:
                        return radialCustomAxis.sqrMagnitude > 1e-12f ? radialCustomAxis.normalized : Vector3.up;
                    default: return Vector3.up;
                }
            }
        }

        /// Every symmetry setting of `source`, for a copy of it. `localSigns` is the reflection the
        /// copy's geometry was built with (all +1 for a plain clone - see MeshCloner.Duplicate).
        /// Mirror flags and an X/Y/Z radial axis describe the same planes and axis either way (a
        /// reflection only negates along them); a custom axis is reflected with the geometry.
        public void CopySettingsFrom(MirrorController source, Vector3 localSigns)
        {
            mirrorX = source.mirrorX;
            mirrorY = source.mirrorY;
            mirrorZ = source.mirrorZ;
            showPlanes = source.showPlanes;
            radial = source.radial;
            radialCount = source.radialCount;
            radialAxis = source.radialAxis;
            radialCustomAxis = Vector3.Scale(source.radialCustomAxis, localSigns);
            space = source.space;
        }

        /// Applies a plane-visibility choice to EVERY object in the scene, not just the
        /// selected one. Mirroring itself is deliberately per-object (each object reflects
        /// through its own origin, so two objects can't share one setting), but plane
        /// VISIBILITY is a display preference about the viewport as a whole - scoping it per
        /// object meant switching selection and unticking "Show Mirror Planes" hid the plane
        /// of the newly-selected object while some other object's plane, enabled earlier and
        /// out of reach of the toggle, stayed on screen. That reads as a toggle that doesn't
        /// work: the plane is still there after you turned it off. See SculptUIBuilder, which
        /// calls this rather than writing ShowPlanes on the selection alone.
        public static void SetShowPlanesForAll(bool show)
        {
            foreach (MirrorController controller in
                     FindObjectsByType<MirrorController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                controller.showPlanes = show;
        }

        private void Awake()
        {
            _sculptableMesh = GetComponent<SculptableMesh>();

            _planeX = CreatePlane("MirrorPlane_X", XColor, PlaneXRotation);
            _planeY = CreatePlane("MirrorPlane_Y", YColor, PlaneYRotation);
            _planeZ = CreatePlane("MirrorPlane_Z", ZColor, PlaneZRotation);
            _radialGuide = CreateRadialGuide();
        }

        private void OnDestroy()
        {
            // Runtime-built, so nothing else ever frees them - see the Mesh lifetime remarks on
            // SculptableMesh.
            if (_radialGuideMesh != null) Destroy(_radialGuideMesh);
            if (_radialGuideMaterial != null) Destroy(_radialGuideMaterial);
        }

        private void Update()
        {
            UpdatePlane(_planeX, mirrorX, PlaneXRotation);
            UpdatePlane(_planeY, mirrorY, PlaneYRotation);
            UpdatePlane(_planeZ, mirrorZ, PlaneZRotation);
            UpdateRadialGuide();
        }

        private void UpdatePlane(Transform plane, bool axisActive, Quaternion rotation)
        {
            // The clean-view check is here rather than a flip of showPlanes, which is saved into
            // scene files - a save made mid-presentation would otherwise lose the planes.
            bool visible = axisActive && showPlanes && _sculptableMesh.Visible && !TurntableController.CleanViewActive;
            if (plane.gameObject.activeSelf != visible) plane.gameObject.SetActive(visible);
            if (!visible) return;

            if (space == SymmetrySpace.World)
            {
                plane.SetPositionAndRotation(Vector3.zero, rotation);
                float size = WorldGuideSize() / ParentScale();
                plane.localScale = new Vector3(size, size, 1f);
            }
            else
            {
                plane.localPosition = Vector3.zero;
                plane.localRotation = rotation;
                float size = PlaneSize();
                plane.localScale = new Vector3(size, size, 1f);
            }
        }

        /// Full width of a World-space guide: it sits at the world origin and has to reach past the
        /// object wherever the object has been moved to.
        private float WorldGuideSize()
        {
            var r = _sculptableMesh.GetComponent<Renderer>();
            if (r == null) return 2f;
            Bounds b = r.bounds;
            float extent = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
            return Mathf.Max(0.01f, b.center.magnitude + extent) * 2f * PlanePadding;
        }

        // Guides are children of this object, so a world size is divided by its scale.
        private float ParentScale()
        {
            Vector3 s = transform.lossyScale;
            return Mathf.Max(1e-6f, Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))));
        }

        /// The radial guide: the axis line through the object, and a ring around it with one spoke
        /// per repeat, so both the axis and where the sectors fall are visible. Built as a unit-size
        /// line mesh around local +Y, then rotated onto the axis and scaled like the mirror planes.
        private void UpdateRadialGuide()
        {
            bool visible = radial && showPlanes && _sculptableMesh.Visible && !TurntableController.CleanViewActive;
            if (_radialGuide.gameObject.activeSelf != visible) _radialGuide.gameObject.SetActive(visible);
            if (!visible) return;

            if (_radialGuideCount != radialCount) RebuildRadialGuideMesh();

            Color color = radialAxis == RadialAxis.X ? XColor
                        : radialAxis == RadialAxis.Z ? ZColor
                        : radialAxis == RadialAxis.Custom ? CustomAxisColor : YColor;
            color.a = RadialGuideAlpha;
            if (_radialGuideMaterial.color != color) _radialGuideMaterial.color = color;

            if (space == SymmetrySpace.World)
            {
                _radialGuide.SetPositionAndRotation(Vector3.zero, Quaternion.FromToRotation(Vector3.up, RadialAxisVector));
                float worldSize = WorldGuideSize() * 0.5f / ParentScale();
                _radialGuide.localScale = new Vector3(worldSize, worldSize, worldSize);
                return;
            }

            _radialGuide.localPosition = Vector3.zero;
            _radialGuide.localRotation = Quaternion.FromToRotation(Vector3.up, RadialAxisVector);
            float size = PlaneSize() * 0.5f;
            _radialGuide.localScale = new Vector3(size, size, size);
        }

        private Transform CreateRadialGuide()
        {
            var go = new GameObject("RadialSymmetryGuide");
            Transform t = go.transform;
            t.SetParent(transform, false);

            _radialGuideMesh = new Mesh { name = "RadialSymmetryGuide" };
            go.AddComponent<MeshFilter>().sharedMesh = _radialGuideMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            _radialGuideMaterial = new Material(Shader.Find("Sprites/Default"));
            renderer.sharedMaterial = _radialGuideMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            go.SetActive(false);
            return t;
        }

        private void RebuildRadialGuideMesh()
        {
            const int ringSegments = 64;
            int n = radialCount;
            var vertices = new List<Vector3>(2 + ringSegments + 1 + n);
            var indices = new List<int>(2 + ringSegments * 2 + n * 2);

            // The axis, end to end.
            vertices.Add(Vector3.down);
            vertices.Add(Vector3.up);
            indices.Add(0); indices.Add(1);

            // The ring.
            int ringStart = vertices.Count;
            for (int i = 0; i < ringSegments; i++)
            {
                float a = 2f * Mathf.PI * i / ringSegments;
                vertices.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
                indices.Add(ringStart + i);
                indices.Add(ringStart + (i + 1) % ringSegments);
            }

            // One spoke per repeat, from the axis out to the ring.
            int centre = vertices.Count;
            vertices.Add(Vector3.zero);
            for (int k = 0; k < n; k++)
            {
                float a = 2f * Mathf.PI * k / n;
                vertices.Add(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)));
                indices.Add(centre);
                indices.Add(vertices.Count - 1);
            }

            _radialGuideMesh.Clear();
            _radialGuideMesh.SetVertices(vertices);
            _radialGuideMesh.SetIndices(indices, MeshTopology.Lines, 0);
            _radialGuideMesh.RecalculateBounds();
            _radialGuideCount = n;
        }

        private float PlaneSize()
        {
            Mesh mesh = _sculptableMesh.Mesh;

            float meshExtent = 0f;
            if (mesh != null)
            {
                Vector3 e = mesh.bounds.extents;
                meshExtent = Mathf.Max(e.x, Mathf.Max(e.y, e.z));
            }

            // An SSphere rig mirroring about THIS object's origin is sharing this exact plane (the
            // rig suppresses its own quad when this one is up - see SSphereController.
            // UpdateSymmetryPlane), and a blockout is routinely grown well past the sphere it was
            // started from. Sizing to the mesh alone left the plane as a small card floating
            // inside a much larger rig, which is no more use than not drawing it. The rig reaches
            // this through the provider list rather than this class looking it up: a per-object
            // component should not know about a tool.
            float sharedWorld = 0f;
            for (int i = 0; i < s_planeExtentProviders.Count; i++)
                sharedWorld = Mathf.Max(sharedWorld, s_planeExtentProviders[i].WorldExtentForPlaneAt(transform.position));

            float sharedExtent = 0f;
            if (sharedWorld > 0f)
            {
                // Providers measure in world units while this quad is a child of a transform
                // that may be scaled, and localScale is read in THAT transform's units.
                Vector3 s = transform.lossyScale;
                float scale = Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                sharedExtent = sharedWorld / Mathf.Max(scale, 1e-6f);
            }

            float extent = Mathf.Max(meshExtent, sharedExtent);
            if (extent <= 0f) return 2f;
            return Mathf.Max(0.01f, extent) * 2f * PlanePadding;
        }

        // Everything registered to share mirror planes - see IMirrorPlaneExtentProvider. Providers
        // add themselves while enabled, so PlaneSize (per visible plane, per frame) walks a short
        // list instead of scanning the scene. The old lazy FindFirstObjectByType re-ran that scan
        // every frame in a scene with no rig controller at all, since it re-resolved while null.
        private static readonly List<IMirrorPlaneExtentProvider> s_planeExtentProviders = new List<IMirrorPlaneExtentProvider>();

        public static void RegisterPlaneExtentProvider(IMirrorPlaneExtentProvider provider)
        {
            if (provider != null && !s_planeExtentProviders.Contains(provider)) s_planeExtentProviders.Add(provider);
        }

        public static void UnregisterPlaneExtentProvider(IMirrorPlaneExtentProvider provider) =>
            s_planeExtentProviders.Remove(provider);

        // Enter Play Mode without a domain reload keeps statics; a provider from the last session
        // would be a destroyed object.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetProviders() => s_planeExtentProviders.Clear();

        private Transform CreatePlane(string name, Color color, Quaternion localRotation)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;

            // CreatePrimitive(Quad) adds a MeshCollider; without removing it the plane
            // would block brush raycasts against the sculptable mesh behind it.
            Collider col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            Transform t = go.transform;
            t.SetParent(transform, false);
            t.localPosition = Vector3.zero;
            t.localRotation = localRotation;

            var renderer = go.GetComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Sprites/Default"));
            Color c = color;
            c.a = PlaneAlpha;
            mat.color = c;
            renderer.material = mat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            go.SetActive(false);
            return t;
        }

        // The group for the CURRENT settings, rebuilt only when they change. Every brush apply
        // site asks for it once per DAB, with Clay alone laying up to ClayMaxDabsPerFrame dabs in a
        // single frame, so building it per call would be per-frame garbage on the sculpting hot
        // path - and GC pressure during a stroke is felt as exactly the intermittent hitching
        // between cursor and surface that a brush is judged on.
        private SymmetryGroup _symmetry;
        private bool _builtX, _builtY, _builtZ;
        private int _builtRadialCount;
        private Vector3 _builtRadialAxis;

        /// Every symmetry op a stroke on this object is applied under, identity first - the
        /// enabled mirror reflections times the radial rotations (see SymmetryGroup). Mapping a
        /// local point, delta or normal through op k gives its k-th symmetric copy.
        ///
        /// Shared and immutable; cached until a setting changes - and, in World space, until the
        /// object moves.
        public SymmetryGroup GetSymmetry()
        {
            int count = radial ? radialCount : 1;
            Vector3 axis = RadialAxisVector;
            if (_symmetry == null || _builtX != mirrorX || _builtY != mirrorY || _builtZ != mirrorZ ||
                _builtRadialCount != count || (count != 1 && !_builtRadialAxis.Equals(axis)))
            {
                _symmetry = !mirrorX && !mirrorY && !mirrorZ && count == 1
                    ? SymmetryGroup.Trivial
                    : SymmetryGroup.Build(mirrorX, mirrorY, mirrorZ, count, axis);
                _builtX = mirrorX; _builtY = mirrorY; _builtZ = mirrorZ;
                _builtRadialCount = count;
                _builtRadialAxis = axis;
                _worldSymmetry = null;
            }

            if (space == SymmetrySpace.Local || _symmetry.Count == 1) return _symmetry;

            // World space: the same group built about the world origin, carried into this
            // object's local frame. Rebuilt only when the object has moved - exactly compared, so
            // a gizmo drag rebuilds it once per frame and a stroke never does.
            Matrix4x4 localToWorld = transform.localToWorldMatrix;
            if (_worldSymmetry == null || !_worldSymmetryMatrix.Equals(localToWorld))
            {
                _worldSymmetry = _symmetry.InObjectFrame(transform.rotation, transform.position, transform.worldToLocalMatrix);
                _worldSymmetryMatrix = localToWorld;
            }
            return _worldSymmetry;
        }

        private SymmetryGroup _worldSymmetry;
        private Matrix4x4 _worldSymmetryMatrix;
    }
}
