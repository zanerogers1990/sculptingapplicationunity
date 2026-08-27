using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
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
            bool visible = axisActive && showPlanes && _sculptableMesh.Visible;
            if (plane.gameObject.activeSelf != visible) plane.gameObject.SetActive(visible);
            if (!visible) return;

            float size = PlaneSize();
            plane.localScale = new Vector3(size, size, 1f);
        }

        private float PlaneSize()
        {
            Mesh mesh = _sculptableMesh.Mesh;
            if (mesh == null) return 2f;

            Vector3 e = mesh.bounds.extents;
            float maxExtent = Mathf.Max(e.x, Mathf.Max(e.y, e.z));
            return Mathf.Max(0.01f, maxExtent) * 2f * PlanePadding;
        }

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

        /// Local-space mirror sign combinations for every currently-enabled axis, always
        /// including the identity (1,1,1) so the original, unmirrored stroke is included.
        /// Scaling a local point/delta/normal by one of these reflects it through whichever
        /// axes are active - e.g. with X and Y both enabled this returns four signs
        /// covering all quadrants.
        public List<Vector3> GetMirrorSigns()
        {
            var signs = new List<Vector3> { Vector3.one };
            if (mirrorX) signs = Expand(signs, true, false, false);
            if (mirrorY) signs = Expand(signs, false, true, false);
            if (mirrorZ) signs = Expand(signs, false, false, true);
            return signs;
        }

        private static List<Vector3> Expand(List<Vector3> input, bool x, bool y, bool z)
        {
            var result = new List<Vector3>(input.Count * 2);
            foreach (Vector3 s in input)
            {
                result.Add(s);
                result.Add(new Vector3(x ? -s.x : s.x, y ? -s.y : s.y, z ? -s.z : s.z));
            }
            return result;
        }
    }
}
