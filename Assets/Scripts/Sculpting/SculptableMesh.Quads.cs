using UnityEngine;

namespace Sculpting
{
    /// Quad remesh, and the polygon faces it leaves behind.
    ///
    /// The engine only ever sculpts triangles. After a quad remesh the triangles are the quads
    /// split in two, and the quads themselves are kept here - as face lists over this mesh's own
    /// vertex indices, so they follow every brush stroke for free - for OBJ export.
    public partial class SculptableMesh
    {
        private int[] _quadFaceStart;
        private int[] _quadFaceIndices;

        // The triangle array the quads were built for. Every topology change - remesh, trim,
        // boolean, join, undo across one - installs a NEW triangle array, so an identity check
        // retires the quads on all of those paths without each one having to remember to.
        private int[] _quadTriangles;

        /// True while the mesh is still the topology the last quad remesh produced (brush strokes
        /// only move vertices, so they keep it; anything that rebuilds the triangles ends it).
        public bool HasQuadTopology => _quadFaceStart != null && ReferenceEquals(_quadTriangles, _workingTriangles);

        /// Polygon faces over this mesh's vertex indices: face f's corners are
        /// QuadFaceIndices[QuadFaceStart[f] .. QuadFaceStart[f + 1]). Null unless HasQuadTopology.
        public int[] QuadFaceStart => HasQuadTopology ? _quadFaceStart : null;
        public int[] QuadFaceIndices => HasQuadTopology ? _quadFaceIndices : null;

        /// Quad remesh as one undo step (full snapshot first, like RemeshUndoable). Returns the
        /// remesh summary for the panel, or null if it produced nothing and the mesh was left
        /// as it was.
        public string QuadRemeshUndoable(int targetQuadCount)
        {
            // Run first, snapshot only on success: a failed remesh must not leave an undo step
            // that does nothing.
            QuadRemesher.Result result = QuadRemesher.Remesh(VerticesExact(), TrianglesExact(), targetQuadCount);
            if (result.Mesh.FaceCount == 0 || result.Triangles.Length == 0) return null;

            SnapshotForUndo();
            Vector3[] vertices = result.Mesh.Vertices;
            ReplaceGeometry(vertices, QuadRemesher.VertexNormals(vertices, result.Triangles), result.Triangles,
                            QuadRemesher.BoundsOf(vertices));

            _quadFaceStart = result.Mesh.FaceStart;
            _quadFaceIndices = result.Mesh.FaceIndices;
            _quadTriangles = _workingTriangles;
            string summary = result.Summary();
            Debug.Log($"[QuadRemesh] {summary}\n{result.TimingSummary()}");
            return summary;
        }

        /// CompactBuffers swaps in a trimmed copy of the SAME triangles; keep the quads with it.
        private void CarryQuadsAcrossCompaction(int[] oldTriangles)
        {
            if (_quadFaceStart != null && ReferenceEquals(_quadTriangles, oldTriangles)) _quadTriangles = _workingTriangles;
        }
    }
}
