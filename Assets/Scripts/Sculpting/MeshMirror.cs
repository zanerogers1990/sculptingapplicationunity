using UnityEngine;

namespace Sculpting
{
    /// One-shot duplicate-and-reflect, confirmed with the user as a discrete action (like
    /// Blender's Duplicate + Mirror) rather than a live-linked modifier: MirrorAcross bakes a
    /// brand new, fully independent sculptable object from the source's CURRENT shape and never
    /// touches the source again afterward. Used by SceneGraphUIBuilder's "Mirror Selected"
    /// button, always reflecting across PrimitiveSpawner.MainObject's world position (the
    /// scene's anchor sphere).
    public static class MeshMirror
    {
        public static SculptableMesh MirrorAcross(SculptableMesh source, Vector3 centerWorld, bool axisX, bool axisY, bool axisZ)
        {
            if (source == null || (!axisX && !axisY && !axisZ)) return null;

            Transform srcT = source.transform;
            // Read the source's live WORKING vertices/triangles, not the mesh asset - same
            // "capture the actual sculpted shape, not a stale copy" reasoning already
            // documented on SculptableMesh.Remesh().
            Vector3[] localVerts = source.Vertices;
            int[] srcTriangles = source.Triangles;

            var worldVerts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
                worldVerts[i] = Reflect(srcT.TransformPoint(localVerts[i]), centerWorld, axisX, axisY, axisZ);

            // Clone before touching winding - srcTriangles is SculptableMesh's own live array,
            // must not be mutated in place.
            int[] triangles = (int[])srcTriangles.Clone();
            int axisCount = (axisX ? 1 : 0) + (axisY ? 1 : 0) + (axisZ ? 1 : 0);
            if ((axisCount & 1) == 1)
            {
                // An odd number of axis reflections flips handedness (2 reflections compose
                // into a plain 180-degree rotation, which needs no fix) - swap each triangle's
                // last two indices so RecalculateNormals() below comes out pointing outward
                // instead of inverted.
                for (int t = 0; t < triangles.Length; t += 3)
                {
                    int tmp = triangles[t + 1];
                    triangles[t + 1] = triangles[t + 2];
                    triangles[t + 2] = tmp;
                }
            }

            // Bakes the whole mirrored shape into a fresh mesh at an identity-rotation/scale
            // transform placed at the reflected pivot - avoids any need for negative-scale
            // rendering tricks, consistent with how this codebase always treats geometry as
            // baked local vertex buffers (SculptableMesh, MeshRemesher, MeshJoiner).
            Vector3 newPosition = Reflect(srcT.position, centerWorld, axisX, axisY, axisZ);
            var localVertsNew = new Vector3[worldVerts.Length];
            for (int i = 0; i < worldVerts.Length; i++)
                localVertsNew[i] = worldVerts[i] - newPosition;

            var mesh = new Mesh { name = source.name + " Mirror (Source)" };
            if (localVertsNew.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = localVertsNew;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(source.name + " Mirror", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.position = newPosition;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;

            SculptableMesh mirrored = go.AddComponent<SculptableMesh>();
            go.AddComponent<MirrorController>();

            SculptMaterialController materialController = Object.FindFirstObjectByType<SculptMaterialController>();
            materialController?.ApplyTo(go.GetComponent<Renderer>());

            SelectionManager selection = Object.FindFirstObjectByType<SelectionManager>();
            selection?.Select(mirrored, false);

            return mirrored;
        }

        private static Vector3 Reflect(Vector3 point, Vector3 center, bool axisX, bool axisY, bool axisZ)
        {
            if (axisX) point.x = 2f * center.x - point.x;
            if (axisY) point.y = 2f * center.y - point.y;
            if (axisZ) point.z = 2f * center.z - point.z;
            return point;
        }
    }
}
