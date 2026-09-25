using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// Whole-mesh replacement: Remesh, ReplaceGeometry/ReplaceMesh, and rebuilding everything derived
    /// from the topology afterwards.
    public partial class SculptableMesh
    {
        /// Rebuilds the mesh from scratch via voxel remeshing (MeshRemesher), giving even
        /// polygon density across the whole sculpted shape instead of the stretched/thin
        /// triangles heavy sculpting leaves in the original topology. Commits the new
        /// topology as the mesh's baseline, so ResetMesh afterwards reverts to this remeshed
        /// shape rather than the pre-sculpt original.
        public void Remesh(int resolution)
        {
            // Must read _workingVertices, not _mesh.vertices - ordinary sculpting now writes
            // touched vertices straight into the mesh's GPU buffer via GpuVertexScatter
            // (ApplyVerticesLocal), which Unity's managed Mesh.vertices getter does NOT
            // reliably reflect (see feedback_unity_gpu_buffer_verification memory). Reading
            // _mesh.vertices here silently remeshed from the stale pre-sculpt shape instead of
            // the actual sculpted one. _workingTriangles is topology, unaffected either way,
            // but reading it avoids the same needless full-array copy _mesh.triangles would do.
            MeshRemesher.RemeshResult result = MeshRemesher.RemeshGeometry(_workingVertices, _workingTriangles, resolution);
            if (result.IsEmpty) return; // nothing extracted - leave the object as it was
            ReplaceGeometry(result.Vertices, result.Normals, result.Triangles, result.Bounds);
        }

        /// Remesh as one undo step: a full snapshot first, so Z steps back to the pre-remesh
        /// shape. What every user-facing Remesh wants; plain Remesh is for callers that manage
        /// history themselves.
        public void RemeshUndoable(int resolution)
        {
            SnapshotForUndo();
            Remesh(resolution);
        }

        /// ReplaceMesh as one undo step: a full snapshot of the current geometry first, so Z
        /// steps the object back to it. Every topology-changing tool (Trim, Boolean, Join, the
        /// symmetry welds) wants exactly this pair; plain ReplaceMesh is for callers that manage
        /// history themselves. Same ownership rules as ReplaceMesh.
        public void ReplaceMeshUndoable(Mesh newMesh)
        {
            SnapshotForUndo();
            ReplaceMesh(newMesh);
        }

        /// Swaps in geometry the caller already holds as arrays, without a Mesh in between.
        ///
        /// This is what Remesh actually needs. Going through a Mesh meant the remesher wrote
        /// its arrays into one, and ReplaceMesh then read them straight back out through
        /// Mesh.vertices / .normals / .triangles - three managed copies of the whole model,
        /// well over a hundred megabytes at a few million triangles - only to re-specify the
        /// vertex buffer and upload them again. At the resolutions this pipeline now reaches,
        /// that round trip cost more than the extraction did.
        ///
        /// Takes ownership of the arrays passed in; callers must not keep writing to them.
        public void ReplaceGeometry(Vector3[] vertices, Vector3[] normals, int[] triangles, Bounds bounds)
        {
            var mesh = new Mesh
            {
                name = _mesh.name,
                indexFormat = vertices.Length > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };
            mesh.MarkDynamic();

            Mesh replaced = _mesh;
            _mesh = mesh;
            _meshFilter.mesh = _mesh;

            _originalVertices = vertices;
            _workingVertices = (Vector3[])vertices.Clone();
            _workingNormals = normals;
            _workingTriangles = triangles;
            SetGeometryCounts(vertices.Length, triangles.Length);

            // Layout first, then contents: ConfigureGpuVertexLayout re-specifies the buffer as
            // position/normal/colour and would discard anything uploaded before it.
            ConfigureGpuVertexLayout(_mesh, _workingVertices.Length);
            _mesh.SetVertices(_workingVertices);
            _mesh.SetNormals(_workingNormals);
            _mesh.SetTriangles(_workingTriangles, 0, _cornerCount, 0, false);
            _mesh.bounds = bounds;

            RebuildDerivedState();
            ReleaseReplacedMesh(replaced, _mesh);
        }

        /// Swaps in an entirely new mesh (different topology/vertex count) and rebuilds every
        /// piece of derived state from it - adjacency, triangle-raycast grid, cavity/mask
        /// buffers, GPU scatter binding, collider. Extracted from Remesh()'s own tail so
        /// MeshJoiner can reuse the identical rebuild after Mesh.CombineMeshes without
        /// duplicating it. Same tradeoff Remesh() already accepted: drops whatever UVs the
        /// source mesh had (harmless - SculptPBR's Attributes struct has no TEXCOORD0 input).
        ///
        /// TAKES OWNERSHIP of newMesh, and destroys the mesh it displaces (see
        /// ReleaseReplacedMesh). Pass a mesh built for this call and then drop the reference -
        /// never a shared project asset, and never a mesh another object is still rendering.
        /// Every current caller (MeshJoiner, MeshBooleanTool, SymmetryOps x2) already builds a
        /// throwaway mesh from CPU arrays specifically to hand over here.
        public void ReplaceMesh(Mesh newMesh)
        {
            Mesh replaced = _mesh;
            newMesh.name = _mesh.name;
            newMesh.MarkDynamic();

            _mesh = newMesh;
            _meshFilter.mesh = _mesh;

            _originalVertices = _mesh.vertices;
            _workingVertices = (Vector3[])_originalVertices.Clone();
            _workingNormals = _mesh.normals;
            _workingTriangles = _mesh.triangles;
            SetGeometryCounts(_workingVertices.Length, _workingTriangles.Length);

            ConfigureGpuVertexLayout(_mesh, _workingVertices.Length);
            _mesh.vertices = _workingVertices;
            _mesh.normals = _workingNormals;

            RebuildDerivedState();
            ReleaseReplacedMesh(replaced, _mesh);
        }

        /// Frees the Mesh a replace path just swapped out.
        ///
        /// Unity never garbage-collects a Mesh, and nothing here was cleaning these up: a mesh
        /// handed to MeshFilter.mesh is not freed when the filter is pointed at a different
        /// one, so every Remesh / Join / Boolean / symmetry rebuild used to strand the previous
        /// mesh's CPU and GPU buffers for the rest of the session. That was survivable while
        /// the remesher capped out under a million triangles; at the ~11M this pipeline now
        /// reaches (roughly 270 MB of vertex+index data per mesh) a few high-resolution
        /// remeshes in a row can exhaust memory on their own.
        ///
        /// Safe to destroy unconditionally because this component owns every mesh _mesh ever
        /// points at. There are exactly three assignment sites and all three produce a
        /// runtime-created mesh nothing else retains: Awake's Instantiate(sharedMesh) copy (the
        /// shared asset itself is never touched - that is the whole point of instantiating),
        /// ReplaceGeometry's new Mesh, and whatever a caller hands ReplaceMesh, which is
        /// documented as an ownership transfer. Audited against every other holder of a
        /// SculptableMesh's mesh, none of which outlives a replace: SculptHistory/EditHistory
        /// snapshot CPU arrays and SculptableMesh references, never Mesh objects;
        /// MaskExtractController builds and destroys its own preview mesh; MeshJoiner,
        /// MeshBoolean, SymmetryOps and SceneSerializer all read the CPU arrays
        /// (Vertices/Normals/Triangles) rather than the Mesh;
        /// MirrorController, TransformGizmo, ObjExporter and SculptController only ever take
        /// .Mesh into a local; and the MeshCollider is re-seated by RebuildDerivedState.
        ///
        /// Called AFTER RebuildDerivedState, never before: GpuVertexScatter.BindMesh holds a
        /// GraphicsBuffer taken from the outgoing mesh's vertex buffer and only releases it
        /// when it rebinds to the new one, so freeing the mesh any earlier would leave that
        /// buffer aimed at released GPU memory.
        private static void ReleaseReplacedMesh(Mesh replaced, Mesh replacement)
        {
            // A caller re-installing the mesh already in place is a no-op, not a reason to
            // destroy the mesh this object is about to render.
            if (replaced == null || replaced == replacement) return;
            Destroy(replaced);
        }

        /// Everything that has to be rebuilt after the mesh's topology changes identity -
        /// adjacency, the triangle-raycast grid, cavity/mask buffers, GPU scatter binding and
        /// the collider. Shared by both replace paths so they cannot drift apart.
        private void RebuildDerivedState()
        {
            _spatialGrid = null;
            BuildAdjacency();
            RebuildTriangleGrid();
            _cavityColors = new Color[_workingVertices.Length];
            _cavityRaw = new float[_workingVertices.Length];
            _mask = new float[_workingVertices.Length];
            // The new topology has no mapping onto the old mask, so it starts blank - which is
            // itself a mask change any watcher needs to hear about (a live extract preview
            // built from the pre-remesh mask is describing geometry that no longer exists).
            MaskVersion++;
            ResetVisibility();
            RecomputeCavity();
            _mesh.colors = _cavityColors;
            BindGpuScatter();

            ReseatCollider();

            // Remesh, Trim, Boolean, Join and Cut & Mirror all end up here - see
            // MirrorLink.OnTopologyChanged for why that finalizes a linked mirror pair.
            if (LinkedMirror != null) LinkedMirror.OnTopologyChanged(this);
        }
    }
}
