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

    /// Adds up to three axes of local-space mirroring to sculpting brushes: each enabled
    /// axis reflects every brush stroke through the sculptable mesh's local origin, and
    /// any combination can be active at once (e.g. X+Y mirrors a stroke into all four
    /// quadrants). Draws a transparent, axis-colored plane per active axis - following
    /// Unity's gizmo convention of red/green/blue for X/Y/Z - so the mirror plane's
    /// position is visible in the scene. Plane visibility has its own toggle independent
    /// of whether mirroring is enabled, so it can be checked and then hidden again.
    [RequireComponent(typeof(SculptableMesh))]
    public class MirrorController : MonoBehaviour
    {
        [Header("Mirror Axes (local space, through object origin)")]
        [SerializeField] private bool mirrorX;
        [SerializeField] private bool mirrorY;
        [SerializeField] private bool mirrorZ;
        [SerializeField] private bool showPlanes = true;

        // Unity's axis-handle/gizmo convention: X red, Y green, Z blue.
        private static readonly Color XColor = new Color(1f, 0.25f, 0.25f);
        private static readonly Color YColor = new Color(0.35f, 1f, 0.35f);
        private static readonly Color ZColor = new Color(0.3f, 0.55f, 1f);
        private const float PlaneAlpha = 0.18f;
        // Planes are sized off the mesh's local bounds each frame, padded so the mirror
        // plane visibly extends past the silhouette instead of clipping it.
        private const float PlanePadding = 1.4f;

        private SculptableMesh _sculptableMesh;
        private Transform _planeX, _planeY, _planeZ;

        public bool MirrorX { get => mirrorX; set => mirrorX = value; }
        public bool MirrorY { get => mirrorY; set => mirrorY = value; }
        public bool MirrorZ { get => mirrorZ; set => mirrorZ = value; }
        public bool ShowPlanes { get => showPlanes; set => showPlanes = value; }

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

            // Unity's built-in Quad lies in the local XY plane (normal +Z) by default -
            // that's already the Z=0 plane. Rotating it 90 deg about Y swings its face into
            // the YZ plane (X=0); 90 deg about X swings it into the XZ plane (Y=0).
            _planeX = CreatePlane("MirrorPlane_X", XColor, Quaternion.Euler(0f, 90f, 0f));
            _planeY = CreatePlane("MirrorPlane_Y", YColor, Quaternion.Euler(90f, 0f, 0f));
            _planeZ = CreatePlane("MirrorPlane_Z", ZColor, Quaternion.identity);
        }

        private void Update()
        {
            UpdatePlane(_planeX, mirrorX);
            UpdatePlane(_planeY, mirrorY);
            UpdatePlane(_planeZ, mirrorZ);
        }

        private void UpdatePlane(Transform plane, bool axisActive)
        {
            // The clean-view check is here rather than a flip of showPlanes, which is saved into
            // scene files - a save made mid-presentation would otherwise lose the planes.
            bool visible = axisActive && showPlanes && _sculptableMesh.Visible && !TurntableController.CleanViewActive;
            if (plane.gameObject.activeSelf != visible) plane.gameObject.SetActive(visible);
            if (!visible) return;

            float size = PlaneSize();
            plane.localScale = new Vector3(size, size, 1f);
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

            // A ZSphere rig mirroring about THIS object's origin is sharing this exact plane (the
            // rig suppresses its own quad when this one is up - see ZSphereController.
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

        // The signs for the CURRENT axis flags, rebuilt only when those flags change. This used to
        // build a fresh List (plus one more per enabled axis, via Expand) on every call - and
        // every one of the eight brush apply sites calls it once per DAB, with Clay alone laying
        // up to ClayMaxDabsPerFrame dabs in a single frame. That is pure per-frame garbage on the
        // sculpting hot path, and GC pressure during a stroke is felt as exactly the intermittent
        // hitching between cursor and surface that a brush is judged on.
        //
        // Handing out the cached list rather than a copy is safe because every caller only ever
        // foreach-es it, and List<T>'s own struct enumerator keeps that allocation-free too.
        private readonly List<Vector3> _signs = new List<Vector3>(8);
        private bool _signsBuilt;
        private bool _signsX, _signsY, _signsZ;

        /// Local-space mirror sign combinations for every currently-enabled axis, always
        /// including the identity (1,1,1) so the original, unmirrored stroke is included.
        /// Scaling a local point/delta/normal by one of these reflects it through whichever
        /// axes are active - e.g. with X and Y both enabled this returns four signs
        /// covering all quadrants.
        ///
        /// The returned list is REUSED between calls - read it, do not keep or mutate it.
        public List<Vector3> GetMirrorSigns()
        {
            if (_signsBuilt && _signsX == mirrorX && _signsY == mirrorY && _signsZ == mirrorZ)
                return _signs;

            _signs.Clear();
            _signs.Add(Vector3.one);
            if (mirrorX) ExpandInPlace(_signs, true, false, false);
            if (mirrorY) ExpandInPlace(_signs, false, true, false);
            if (mirrorZ) ExpandInPlace(_signs, false, false, true);

            _signsBuilt = true;
            _signsX = mirrorX; _signsY = mirrorY; _signsZ = mirrorZ;
            return _signs;
        }

        /// Doubles the list in place, each existing sign followed immediately by its reflection.
        ///
        /// Filled BACKWARDS, which is what makes an in-place expansion safe: entry i moves to 2i,
        /// and 2i >= i for every i, so no slot is written before it has been read. The
        /// interleaved order is deliberate rather than incidental - it is the order the previous
        /// allocating version produced, and the brushes apply these signs in sequence against the
        /// live vertex array, so reordering them would quietly change what a mirrored stroke does
        /// where two footprints overlap near the plane.
        private static void ExpandInPlace(List<Vector3> signs, bool x, bool y, bool z)
        {
            int count = signs.Count;
            for (int i = 0; i < count; i++) signs.Add(Vector3.zero);
            for (int i = count - 1; i >= 0; i--)
            {
                Vector3 s = signs[i];
                signs[i * 2] = s;
                signs[i * 2 + 1] = new Vector3(x ? -s.x : s.x, y ? -s.y : s.y, z ? -s.z : s.z);
            }
        }
    }
}
