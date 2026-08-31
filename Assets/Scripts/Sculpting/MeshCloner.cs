using UnityEngine;

namespace Sculpting
{
    /// One-shot duplicate of a sculptable object - the same "bake the CURRENT shape into a
    /// brand new, fully independent object" contract MeshMirror already established, minus the
    /// reflection. Used by SceneGraphUIBuilder's "Clone Selected" button.
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
            // Live working arrays, not the mesh asset - same reasoning as MeshMirror's own
            // read of Vertices/Triangles: the point is to copy what the object looks like now,
            // after however much sculpting, not what it was loaded as.
            Vector3[] localVerts = (Vector3[])source.Vertices.Clone();
            int[] triangles = (int[])source.Triangles.Clone();

            var mesh = new Mesh { name = source.name + " Clone (Source)" };
            if (localVerts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = localVerts;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(ObjectNaming.Unique(source.name + " Copy"), typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetPositionAndRotation(srcT.position, srcT.rotation);
            go.transform.localScale = srcT.localScale;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;

            // AddComponent runs SculptableMesh.Awake synchronously, so everything below this
            // line is operating on a fully built clone (see PrimitiveSpawner/MeshMirror, which
            // rely on the same guarantee).
            SculptableMesh clone = SculptableMesh.AddOwning(go, mesh);
            var cloneMirror = go.AddComponent<MirrorController>();

            var sourceMirror = source.GetComponent<MirrorController>();
            if (sourceMirror != null)
            {
                cloneMirror.MirrorX = sourceMirror.MirrorX;
                cloneMirror.MirrorY = sourceMirror.MirrorY;
                cloneMirror.MirrorZ = sourceMirror.MirrorZ;
                cloneMirror.ShowPlanes = sourceMirror.ShowPlanes;
            }

            // Vertex indices are identical (the triangle array was copied verbatim), so the
            // mask transfers one-to-one - carrying it over means a clone made mid-workflow
            // stays usable with masked Transpose right away instead of silently losing the
            // masking work that set it up.
            clone.SetMask(source.Mask);

            SculptMaterialController materialController = Object.FindFirstObjectByType<SculptMaterialController>();
            materialController?.ApplyTo(go.GetComponent<Renderer>());

            SelectionManager selection = Object.FindFirstObjectByType<SelectionManager>();
            selection?.Select(clone, false);

            return clone;
        }
    }
}
