using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Merges two or more selected objects' meshes into one - ZBrush "Merge Down"/Blender Join
    /// semantics (plain concatenation via Mesh.CombineMeshes, not a watertight boolean; users
    /// who want welded topology remesh afterward). Destructive (destroys the non-survivor
    /// GameObjects) with no dedicated undo system for that - SceneGraphUIBuilder shows a
    /// confirmation step before calling this, same mitigation as this codebase already uses
    /// for other undo-free destructive ops.
    public static class MeshJoiner
    {
        /// Combines every object in objects into objects[0] (the survivor - SceneGraphUIBuilder
        /// always passes the primary selection first), optionally remeshing the result
        /// afterward for uniform topology. Returns the survivor, or null if fewer than 2
        /// objects were given.
        public static SculptableMesh Join(IReadOnlyList<SculptableMesh> objects, SculptController controller, bool remeshAfter)
        {
            if (objects == null || objects.Count < 2) return null;

            SculptableMesh survivor = objects[0];
            var instances = new List<CombineInstance>(objects.Count);
            // Scratch meshes built purely to feed CombineMeshes below - destroyed right after,
            // never assigned to any GameObject.
            var scratchMeshes = new List<Mesh>(objects.Count);
            for (int i = 0; i < objects.Count; i++)
            {
                SculptableMesh src = objects[i];
                if (src == null || src.Vertices == null || src.Vertices.Length == 0) continue;

                // Build a PLAIN mesh from the working CPU arrays instead of handing src.Mesh
                // directly to CombineMeshes. src.Mesh's vertex buffer is reconfigured for
                // compute-shader scatter writes (GraphicsBuffer.Target.Raw, a minimal position/
                // normal/color-only layout - see SculptableMesh.ConfigureGpuVertexLayout), which
                // Mesh.CombineMeshes isn't built to read correctly - confirmed as the cause of a
                // real corrupted/warped join result on an actually-sculpted mesh (a fresh,
                // never-sculpted mesh happened not to show it, since its GPU buffer had never
                // diverged from the CPU-side copy). Same "managed Mesh API doesn't reliably
                // reflect GPU-buffer state" class of bug already documented for Remesh() - see
                // feedback_unity_gpu_buffer_verification memory. Vertices/Normals/Triangles are
                // always the authoritative CPU-side arrays regardless of GPU buffer state.
                var plain = new Mesh { name = src.name + " (JoinSource)" };
                if (src.Vertices.Length > 65000) plain.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                plain.vertices = src.Vertices;
                plain.normals = src.Normals;
                plain.triangles = src.Triangles;
                scratchMeshes.Add(plain);

                instances.Add(new CombineInstance
                {
                    mesh = plain,
                    transform = survivor.transform.worldToLocalMatrix * src.transform.localToWorldMatrix
                });
            }

            var combined = new Mesh { name = survivor.name + " (Joined)" };
            if (CountTotalVertices(instances) > 65000) combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            combined.CombineMeshes(instances.ToArray(), mergeSubMeshes: true, useMatrices: true);
            foreach (Mesh m in scratchMeshes) Object.Destroy(m);

            // SnapshotForUndo before the topology-changing ReplaceMesh, same convention every
            // other Remesh/Reset call site in this codebase follows - doesn't undo the whole
            // Join (the other GameObjects are gone for good), but does let the survivor's
            // existing Z/Shift+Z undo step back to its pre-join single-object shape.
            survivor.SnapshotForUndo();
            survivor.ReplaceMesh(combined);

            SelectionManager selection = Object.FindFirstObjectByType<SelectionManager>();
            for (int i = 1; i < objects.Count; i++)
            {
                SculptableMesh src = objects[i];
                if (src == null) continue;
                selection?.Unregister(src);
                Object.Destroy(src.gameObject);
            }

            selection?.Select(survivor, false);

            if (remeshAfter && controller != null)
            {
                survivor.SnapshotForUndo();
                survivor.Remesh(controller.RemeshResolution);
            }

            return survivor;
        }

        private static int CountTotalVertices(List<CombineInstance> instances)
        {
            int total = 0;
            foreach (CombineInstance ci in instances) total += ci.mesh.vertexCount;
            return total;
        }
    }
}
