using UnityEngine;

namespace Sculpting
{
    /// One-shot duplicate of a sculptable object: bakes its CURRENT shape into a brand new, fully
    /// independent object. Used by SceneGraphUIBuilder's "Clone Selected" button, and - with a
    /// reflection folded in - by both of MeshMirror's copies, which share every step of Duplicate.
    ///
    /// The clone lands exactly on top of the original rather than at some invented offset:
    /// where a duplicate belongs is a modelling decision (a second horn goes somewhere quite
    /// different from a second finger), and every offset this could pick would be wrong often
    /// enough to be worse than none. The clone is selected on creation, so Transpose moves it
    /// straight away.
    public static class MeshCloner
    {
        public static SculptableMesh Clone(SculptableMesh source)
        {
            if (source == null) return null;
            Transform srcT = source.transform;
            return Duplicate(source, source.name + " Copy", Vector3.one, srcT.position, srcT.rotation);
        }

        /// Builds the new object from source's live working arrays, with every local vertex and
        /// normal scaled by `localSigns` (all +1 for a plain clone - see MeshMirror.ReflectGeometry),
        /// placed at `position`/`rotation` with source's own scale.
        internal static SculptableMesh Duplicate(SculptableMesh source, string desiredName, Vector3 localSigns,
                                                 Vector3 position, Quaternion rotation)
        {
            // Live working arrays, not the mesh asset: the point is to copy what the object looks
            // like now, after however much sculpting, not what it was loaded as.
            MeshMirror.ReflectGeometry(source.VerticesExact(), source.NormalsExact(), source.TrianglesExact(), localSigns,
                                       out Vector3[] vertices, out Vector3[] normals, out int[] triangles);

            string name = ObjectNaming.Unique(desiredName);
            var mesh = new Mesh { name = name + " (Source)" };
            if (vertices.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            // Carried over, not recalculated: RecalculateNormals splits every duplicated seam vertex
            // into a hard crease (see MeshMirror's remarks). Only a source with no usable normals
            // falls back to it.
            if (normals != null) mesh.normals = normals;
            else mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = source.transform.localScale;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;

            // AddComponent runs SculptableMesh.Awake synchronously, so everything below this
            // line is operating on a fully built copy (see PrimitiveSpawner, which relies on the
            // same guarantee).
            SculptableMesh copy = SculptableMesh.AddOwning(go, mesh);
            var copyMirror = go.AddComponent<MirrorController>();

            // Valid for a mirrored copy too: its local frame is the source's reflected, and every
            // symmetry plane runs through the origin along an axis the reflection only negates, so
            // the same flags describe the same planes.
            var sourceMirror = source.GetComponent<MirrorController>();
            if (sourceMirror != null)
            {
                copyMirror.MirrorX = sourceMirror.MirrorX;
                copyMirror.MirrorY = sourceMirror.MirrorY;
                copyMirror.MirrorZ = sourceMirror.MirrorZ;
                copyMirror.ShowPlanes = sourceMirror.ShowPlanes;
            }

            // Vertex indices are identical (the triangles were copied index for index), so the
            // mask transfers one-to-one - carrying it over means a copy made mid-workflow stays
            // usable with masked Transpose right away instead of silently losing the masking
            // work that set it up.
            copy.SetMask(source.MaskExact());

            SculptMaterialController materialController = Object.FindFirstObjectByType<SculptMaterialController>();
            materialController?.ApplyTo(go.GetComponent<Renderer>());

            SelectionManager selection = Object.FindFirstObjectByType<SelectionManager>();
            selection?.Select(copy, false);

            return copy;
        }
    }
}
