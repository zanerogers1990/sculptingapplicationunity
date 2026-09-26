using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// Draws the armature - every sphere and link the rig puts in the world, reflections included
    /// - as ONE mesh, plus the placement ghost and the mirror plane.
    ///
    /// The old controller drew the rig as a GameObject per sphere (a CreatePrimitive with a
    /// collider) and a GameObject per link (a MeshCollider re-cooked on every frame of a drag), and
    /// picked by raycasting the physics scene. That is where a lot of the "finicky" came from: the
    /// physics copy of every collider lags the transform until Physics.SyncTransforms, so clicks
    /// landed where spheres USED to be, and cooking dozens of mesh colliders per frame made drags
    /// stutter. Here nothing is a physics object. The controller picks analytically against the
    /// same SSphereRig.CollectGeometry output this class draws, so the thing under the cursor is
    /// exactly the thing on screen, on the frame it is on screen.
    ///
    /// State is shown through SUBMESHES rather than vertex colours, so ordinary URP/Lit materials
    /// work - no custom shader to keep in step with the pipeline. A vertex can belong to
    /// triangles in several submeshes, so alternating link bands share their boundary rings.
    internal sealed class SSphereArmatureView
    {
        private const int SphereSegments = 18;
        private const int SphereRings = 12;
        private const int TubeSides = 14;

        /// Links are drawn thinner than the skin they describe. At full radius a tube swallows its
        /// own end spheres and the armature reads as one featureless sausage with no way to see
        /// where the grabbable joints are. The armature is a schematic; the skin is the truth.
        private const float LinkVisualScale = 0.6f;

        /// Band length along a link, as a multiple of the link's mean radius. The alternating
        /// bands are the ZBrush SSphere look, and they earn their place: on a smooth tube there is
        /// nothing to tell you which way it is twisting or how long it is in depth.
        private const float BandLengthPerRadius = 0.55f;
        private const int MaxBandsPerLink = 40;

        private const int SubNode = 0;
        private const int SubSelected = 1;
        private const int SubHovered = 2;
        private const int SubLinkLight = 3;
        private const int SubLinkDark = 4;
        private const int SubLinkHovered = 5;
        private const int SubmeshCount = 6;

        private static readonly Color NodeColor = new Color(0.78f, 0.79f, 0.82f);
        private static readonly Color SelectedColor = new Color(1f, 0.6f, 0.18f);
        private static readonly Color HoveredColor = new Color(0.62f, 0.86f, 1f);
        private static readonly Color LinkLightColor = new Color(0.66f, 0.67f, 0.71f);
        private static readonly Color LinkDarkColor = new Color(0.5f, 0.51f, 0.55f);
        private static readonly Color LinkHoveredColor = new Color(0.52f, 0.8f, 0.98f);
        private static readonly Color CursorColor = new Color(0.62f, 0.86f, 1f, 0.35f);
        private static readonly Color CursorRimColor = new Color(0.85f, 0.95f, 1f, 1f);
        private static readonly Color PlaneColor = new Color(1f, 0.25f, 0.25f, 0.14f);

        private readonly GameObject _armature;
        private readonly Mesh _mesh;
        private readonly Material[] _materials;

        private readonly GameObject _cursor;
        private readonly Material _cursorMaterial;
        private readonly Mesh _sphereMesh;

        private readonly GameObject _plane;
        private readonly Material _planeMaterial;
        private readonly Mesh _planeMesh;

        private readonly List<Vector3> _verts = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<int>[] _tris = new List<int>[SubmeshCount];
        private readonly List<SSphereRig.SphereInstance> _spheres = new List<SSphereRig.SphereInstance>();
        private readonly List<SSphereRig.LinkInstance> _links = new List<SSphereRig.LinkInstance>();

        private static Vector3[] _unitSphereVerts;
        private static int[] _unitSphereTris;

        public SSphereArmatureView(Transform parent)
        {
            BuildUnitSphere();
            for (int i = 0; i < SubmeshCount; i++) _tris[i] = new List<int>();

            _mesh = new Mesh { name = "SSphere Armature", indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();

            _materials = new Material[SubmeshCount];
            _materials[SubNode] = CreateLit("SSphere Node", NodeColor, 0.35f);
            _materials[SubSelected] = CreateLit("SSphere Selected", SelectedColor, 0.4f);
            _materials[SubHovered] = CreateLit("SSphere Hovered", HoveredColor, 0.4f);
            _materials[SubLinkLight] = CreateLit("SSphere Link Light", LinkLightColor, 0.25f);
            _materials[SubLinkDark] = CreateLit("SSphere Link Dark", LinkDarkColor, 0.25f);
            _materials[SubLinkHovered] = CreateLit("SSphere Link Hovered", LinkHoveredColor, 0.3f);

            _armature = CreateRendererObject("SSphereArmature", parent, _mesh, _materials);

            _sphereMesh = new Mesh { name = "SSphere Cursor" };
            _sphereMesh.SetVertices(_unitSphereVerts);
            _sphereMesh.SetNormals(_unitSphereVerts);
            _sphereMesh.SetTriangles(_unitSphereTris, 0);
            _sphereMesh.RecalculateBounds();

            _cursorMaterial = CreateTranslucent("SSphere Cursor", CursorColor, CursorRimColor);
            _cursor = CreateRendererObject("SSpherePlacementCursor", parent, _sphereMesh, new[] { _cursorMaterial });
            _cursor.SetActive(false);

            _planeMesh = BuildQuadMesh();
            Shader spriteShader = Shader.Find("Sprites/Default");
            _planeMaterial = new Material(spriteShader) { name = "SSphere Symmetry Plane (Runtime)", color = PlaneColor };
            _plane = CreateRendererObject("SSphereSymmetryPlane", parent, _planeMesh, new[] { _planeMaterial });
            // The quad faces +Z; a quarter turn about Y lays it in the YZ plane - rig-local x = 0,
            // the plane every reflection goes through.
            _plane.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            _plane.SetActive(false);
        }

        public void SetArmatureVisible(bool visible)
        {
            if (_armature.activeSelf != visible) _armature.SetActive(visible);
        }

        /// Rebuilds the armature mesh. The caller decides WHEN (it polls rig version, selection and
        /// hover), so this does the work unconditionally.
        public void Rebuild(SSphereRig rig, bool symmetry, int selectedNode, int hoveredNode, int hoveredLink)
        {
            _verts.Clear();
            _normals.Clear();
            for (int i = 0; i < SubmeshCount; i++) _tris[i].Clear();

            rig.CollectGeometry(symmetry, _spheres, _links);

            for (int i = 0; i < _links.Count; i++)
            {
                SSphereRig.LinkInstance link = _links[i];
                bool hovered = link.Child == hoveredLink;
                AppendTube(link.A, link.B, link.RadiusA * LinkVisualScale, link.RadiusB * LinkVisualScale, hovered);
            }

            for (int i = 0; i < _spheres.Count; i++)
            {
                SSphereRig.SphereInstance sphere = _spheres[i];
                // Both a node and its reflection light up together: they are one node, and an edit
                // to either lands on both, so showing only the half under the cursor would be lying
                // about what a drag is about to do.
                int sub = sphere.Node == selectedNode ? SubSelected
                        : sphere.Node == hoveredNode ? SubHovered
                        : SubNode;
                AppendSphere(sphere.Centre, sphere.Radius, sub);
            }

            _mesh.Clear();
            _mesh.subMeshCount = SubmeshCount;
            _mesh.SetVertices(_verts);
            _mesh.SetNormals(_normals);
            for (int i = 0; i < SubmeshCount; i++) _mesh.SetTriangles(_tris[i], i, false);
            _mesh.RecalculateBounds();
        }

        /// The translucent ghost showing where (and how big) the next placed sphere will be.
        public void SetCursor(bool visible, Vector3 localPosition, float radius)
        {
            if (_cursor.activeSelf != visible) _cursor.SetActive(visible);
            if (!visible) return;
            _cursor.transform.localPosition = localPosition;
            _cursor.transform.localScale = Vector3.one * radius;
        }

        public void SetSymmetryPlane(bool visible, float size)
        {
            if (_plane.activeSelf != visible) _plane.SetActive(visible);
            if (!visible) return;
            _plane.transform.localPosition = Vector3.zero;
            _plane.transform.localScale = new Vector3(size, size, 1f);
        }

        public void Destroy()
        {
            Object.Destroy(_mesh);
            Object.Destroy(_sphereMesh);
            Object.Destroy(_planeMesh);
            for (int i = 0; i < _materials.Length; i++) Object.Destroy(_materials[i]);
            Object.Destroy(_cursorMaterial);
            Object.Destroy(_planeMaterial);
            if (_armature != null) Object.Destroy(_armature);
            if (_cursor != null) Object.Destroy(_cursor);
            if (_plane != null) Object.Destroy(_plane);
        }

        // ------------------------------------------------------------------------ geometry

        private void AppendSphere(Vector3 centre, float radius, int submesh)
        {
            int baseIndex = _verts.Count;
            for (int i = 0; i < _unitSphereVerts.Length; i++)
            {
                _verts.Add(centre + _unitSphereVerts[i] * radius);
                _normals.Add(_unitSphereVerts[i]);
            }
            List<int> tris = _tris[submesh];
            for (int i = 0; i < _unitSphereTris.Length; i++) tris.Add(baseIndex + _unitSphereTris[i]);
        }

        private void AppendTube(Vector3 a, Vector3 b, float radiusA, float radiusB, bool hovered)
        {
            Vector3 axis = b - a;
            float length = axis.magnitude;
            if (length < 1e-6f) return;
            Vector3 d = axis / length;

            // Any perpendicular will do for the ring frame; the reference just has to not be
            // parallel to the axis. (u, v, d) keeps the same handedness for every link, which is
            // what lets one winding rule below face every tube outward.
            Vector3 reference = Mathf.Abs(d.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 u = Vector3.Cross(d, reference).normalized;
            Vector3 v = Vector3.Cross(d, u);

            float meanRadius = Mathf.Max(0.5f * (radiusA + radiusB), 1e-5f);
            int bands = Mathf.Clamp(Mathf.RoundToInt(length / (meanRadius * BandLengthPerRadius)), 1, MaxBandsPerLink);

            // Cone slope folded into the normal, so a tapering link shades as a cone rather than
            // as a cylinder with its ends pinched.
            float slope = (radiusA - radiusB) / length;

            int baseIndex = _verts.Count;
            for (int ring = 0; ring <= bands; ring++)
            {
                float t = ring / (float)bands;
                Vector3 centre = a + axis * t;
                float radius = Mathf.Lerp(radiusA, radiusB, t);
                for (int side = 0; side < TubeSides; side++)
                {
                    float angle = side / (float)TubeSides * Mathf.PI * 2f;
                    Vector3 radial = u * Mathf.Cos(angle) + v * Mathf.Sin(angle);
                    _verts.Add(centre + radial * radius);
                    _normals.Add((radial + d * slope).normalized);
                }
            }

            for (int ring = 0; ring < bands; ring++)
            {
                List<int> tris = _tris[hovered ? SubLinkHovered : (ring % 2 == 0 ? SubLinkLight : SubLinkDark)];
                int r0 = baseIndex + ring * TubeSides;
                int r1 = r0 + TubeSides;
                for (int side = 0; side < TubeSides; side++)
                {
                    int next = (side + 1) % TubeSides;
                    int p0 = r0 + side, p1 = r0 + next, p2 = r1 + side, p3 = r1 + next;
                    tris.Add(p1); tris.Add(p3); tris.Add(p2);
                    tris.Add(p1); tris.Add(p2); tris.Add(p0);
                }
            }
        }

        private static void BuildUnitSphere()
        {
            if (_unitSphereVerts != null) return;

            var verts = new List<Vector3>();
            for (int r = 0; r <= SphereRings; r++)
            {
                float phi = Mathf.PI * r / SphereRings;
                float y = Mathf.Cos(phi), s = Mathf.Sin(phi);
                for (int c = 0; c <= SphereSegments; c++)
                {
                    float theta = Mathf.PI * 2f * c / SphereSegments;
                    verts.Add(new Vector3(s * Mathf.Cos(theta), y, s * Mathf.Sin(theta)));
                }
            }

            var tris = new List<int>();
            int stride = SphereSegments + 1;
            for (int r = 0; r < SphereRings; r++)
            {
                for (int c = 0; c < SphereSegments; c++)
                {
                    int a = r * stride + c, b = a + stride;
                    tris.Add(a); tris.Add(a + 1); tris.Add(b);
                    tris.Add(a + 1); tris.Add(b + 1); tris.Add(b);
                }
            }

            _unitSphereVerts = verts.ToArray();
            _unitSphereTris = tris.ToArray();
        }

        private static Mesh BuildQuadMesh()
        {
            var mesh = new Mesh { name = "SSphere Symmetry Quad" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f), new Vector3(0.5f,  0.5f, 0f)
            };
            // Both windings, so the plane is visible from either side.
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1, 0, 1, 2, 2, 1, 3 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ----------------------------------------------------------------------- materials

        private static GameObject CreateRendererObject(string name, Transform parent, Mesh mesh, Material[] materials)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        internal static Material CreateLit(string name, Color color, float smoothness)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                            ?? Shader.Find("Standard")
                            ?? Shader.Find("Sprites/Default");
            var material = new Material(shader) { name = name + " (Runtime)", color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            return material;
        }

        /// Depth-tested translucent shell with a rim - the mask-extract preview shader, whose job
        /// (judge a shape without hiding what is behind it) is exactly the ghost's.
        internal static Material CreateTranslucent(string name, Color color, Color rim)
        {
            Shader shader = Shader.Find("Custom/ExtractPreview");
            if (shader == null)
            {
                var fallback = new Material(Shader.Find("Sprites/Default")) { name = name + " (Runtime)", color = color };
                return fallback;
            }
            var material = new Material(shader) { name = name + " (Runtime)" };
            material.SetColor("_Color", color);
            material.SetColor("_RimColor", rim);
            return material;
        }
    }
}
