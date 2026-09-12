using System.Collections.Generic;

namespace Sculpting.DynamicTopology
{
    /// What one local remesh changed, in the two shapes anybody needs it in: the set of appended
    /// vertices and rewritten triangle slots that SculptableMesh has to push into its buffers, the
    /// grids and the GPU; and the before-image of each of those, which is exactly what undo has to
    /// put back.
    ///
    /// Recorded as the edits happen rather than diffed afterwards. A diff would have to compare
    /// against a copy of the whole mesh, which is the whole-mesh cost this feature exists to avoid
    /// - and the edits already know precisely what they touched.
    ///
    /// FIRST-WRITE-WINS on the before-image, the same idiom
    /// SculptableMesh.RecordUndoBeforeIfNeeded uses for stroke deltas: a triangle slot rewritten
    /// four times across a stroke has to restore to what it held before the FIRST of those, not
    /// the third.
    public sealed class TopologyPatch
    {
        /// Counts as they were before this patch's first edit. Undo truncates back to these, which
        /// is what removes every vertex and triangle the patch appended - no free list, no index
        /// remapping, and nothing for the mask or the undo stack's own vertex indices to slip
        /// against.
        public int VertexCountBefore { get; private set; }
        public int CornerCountBefore { get; private set; }

        /// Triangle slots below CornerCountBefore that were overwritten, and the three corner
        /// indices each held beforehand. Slots at or above it are appended ones, restored by the
        /// truncation instead.
        public readonly List<int> ChangedTriangles = new List<int>();
        public readonly List<int> ChangedTriangleCorners = new List<int>();

        /// Every vertex index the patch appended or whose position it moved (the relax pass), for
        /// the apply path to upload and re-bucket. Not an undo record: positions of pre-existing
        /// vertices are already covered by the stroke's own vertex delta.
        public readonly List<int> TouchedVertices = new List<int>();

        public int VertexCountAfter { get; private set; }
        public int CornerCountAfter { get; private set; }

        public bool IsEmpty => ChangedTriangles.Count == 0 && TouchedVertices.Count == 0 &&
                               VertexCountAfter == VertexCountBefore && CornerCountAfter == CornerCountBefore;

        // Per-slot "already recorded in this patch" marks, so the first-write-wins test is an array
        // read rather than a set probe. Stamped by generation for the same reason
        // SculptController's DirtyVertexSet is: a refine runs many times a stroke and clearing a
        // mesh-sized array each time would be the cost of the whole operation.
        private int[] _triangleStamp;
        private int[] _vertexStamp;
        private int _generation;

        /// Opens a patch over a mesh currently holding these counts. Called once per refine.
        public void Begin(int vertexCount, int cornerCount, int vertexCapacity, int cornerCapacity)
        {
            VertexCountBefore = vertexCount;
            CornerCountBefore = cornerCount;
            VertexCountAfter = vertexCount;
            CornerCountAfter = cornerCount;
            ChangedTriangles.Clear();
            ChangedTriangleCorners.Clear();
            TouchedVertices.Clear();

            if (_triangleStamp == null || _triangleStamp.Length < cornerCapacity / 3 + 1)
                _triangleStamp = new int[cornerCapacity / 3 + 1];
            if (_vertexStamp == null || _vertexStamp.Length < vertexCapacity)
                _vertexStamp = new int[vertexCapacity];
            _generation++;
        }

        public void SetCounts(int vertexCount, int cornerCount)
        {
            VertexCountAfter = vertexCount;
            CornerCountAfter = cornerCount;
        }

        /// Call BEFORE overwriting a triangle slot. Slots the patch itself appended are skipped:
        /// undo removes those wholesale by truncation, and storing a before-image of a triangle
        /// that did not exist would make undo write it back into a slot it is about to discard.
        public void RecordTriangleBefore(int triangleIndex, int[] corners)
        {
            if (triangleIndex * 3 >= CornerCountBefore) return;
            if (triangleIndex < 0 || triangleIndex >= _triangleStamp.Length) return;
            if (_triangleStamp[triangleIndex] == _generation) return;

            _triangleStamp[triangleIndex] = _generation;
            ChangedTriangles.Add(triangleIndex);
            int b = triangleIndex * 3;
            ChangedTriangleCorners.Add(corners[b]);
            ChangedTriangleCorners.Add(corners[b + 1]);
            ChangedTriangleCorners.Add(corners[b + 2]);
        }

        public void RecordTouchedVertex(int vertexIndex)
        {
            if (vertexIndex < 0 || vertexIndex >= _vertexStamp.Length) return;
            if (_vertexStamp[vertexIndex] == _generation) return;
            _vertexStamp[vertexIndex] = _generation;
            TouchedVertices.Add(vertexIndex);
        }

        /// Grows the stamp arrays to match buffers that have just grown under this patch, keeping
        /// the marks already set - a vertex recorded before the growth must stay recorded.
        public void EnsureCapacity(int vertexCapacity, int cornerCapacity)
        {
            if (_vertexStamp.Length < vertexCapacity) System.Array.Resize(ref _vertexStamp, vertexCapacity);
            int triangleCapacity = cornerCapacity / 3 + 1;
            if (_triangleStamp.Length < triangleCapacity) System.Array.Resize(ref _triangleStamp, triangleCapacity);
        }
    }
}
