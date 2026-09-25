using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// The two spatial indices (triangle grid for raycasts, vertex grid for brush footprints) and
    /// their lazy catch-up of moved vertices.
    public partial class SculptableMesh
    {
        // Accelerates SelectGrab/QueryNear so a brush stroke doesn't scan every vertex in the
        // mesh every frame - see VertexSpatialGrid. Prepared by SculptController at the start of
        // each stroke (PrepareSpatialIndex, which keeps the existing index when it still fits),
        // kept exact as vertices move (see QueueSpatialIndexUpdates), and invalidated here
        // whenever the vertex buffer is replaced/reset wholesale so a stale grid can never be
        // queried against the wrong positions; QueryNear/SelectGrab rebuild lazily with a default
        // cell size if nothing has built it yet.
        private VertexSpatialGrid _spatialGrid;

        // Cell size targets ~8 triangles per cell on average, sized off the CURRENT mesh's own
        // triangle density (bounds volume / triangle count) rather than any fixed constant -
        // learned from a prior bug in SignedDistanceField's triangle-binning grid, which reused
        // an unrelated cell size and bloated badly on a coarse source mesh (found during the
        // remesh performance work).
        /// How many times the triangle grid has been rebuilt from scratch. Diagnostic only - a
        /// rebuild is O(total triangle count) and lands inside whatever frame triggered it, so
        /// this is the number to look at when a stroke is smooth on average but hitches.
        public int TriangleGridRebuilds { get; private set; }

        private void RebuildTriangleGrid()
        {
            TriangleGridRebuilds++;
            GeometryVersion++;
            const float TargetTrianglesPerCell = 8f;
            Bounds b = _mesh.bounds;
            float volume = Mathf.Max(b.size.x * b.size.y * b.size.z, 1e-9f);
            int triCount = Mathf.Max(1, TriangleCount);
            float cellVolume = volume * TargetTrianglesPerCell / triCount;
            float cellSize = Mathf.Max(Mathf.Pow(cellVolume, 1f / 3f), 0.001f);

            // Padded 50% beyond the mesh's current bounds rather than an exact fit - the grid's
            // bounds are fixed until the next full rebuild (ApplyVerticesLocal's incremental
            // path only re-buckets triangles WITHIN them, see its remarks), so a stroke that
            // keeps pushing geometry further out in the same direction would otherwise re-trigger
            // this full O(triangle count) rebuild on every single frame once it reaches the edge.
            // Padding gives it room to keep going for a while before that happens again.
            Vector3 pad = b.size * 0.5f;
            b.SetMinMax(b.min - pad, b.max + pad);

            _triangleGrid = new TriangleSpatialGrid(_workingVertices, _vertexCount, _workingTriangles, _cornerCount,
                                                    b, cellSize);
            // A fresh build already reflects every position, so nothing queued before it applies.
            _pendingTriangleGridVertices.Clear();
        }

        /// True if every dirty vertex's incident triangles are still fully inside the triangle
        /// grid's (fixed-at-construction) bounds. ApplyVerticesLocal falls back to a full
        /// RebuildTriangleGrid() when this is false - see its remarks for why a stale, too-small
        /// bounds silently breaks raycasts against whatever moved past it.
        private bool MeshBoundsFitInsideTriangleGrid()
        {
            Bounds gridBounds = _triangleGrid.Bounds;
            Bounds meshBounds = _mesh.bounds;
            return gridBounds.min.x <= meshBounds.min.x && gridBounds.min.y <= meshBounds.min.y &&
                   gridBounds.min.z <= meshBounds.min.z && gridBounds.max.x >= meshBounds.max.x &&
                   gridBounds.max.y >= meshBounds.max.y && gridBounds.max.z >= meshBounds.max.z;
        }

        // ------------------------------------------------------------ lazy spatial index upkeep

        /// Vertices moved since one spatial index last caught up with them, de-duplicated with a
        /// flag per vertex. One of these per index, because the two catch up at different moments:
        /// the triangle grid when something raycasts, the vertex grid when something queries.
        private sealed class PendingVertexSet
        {
            private bool[] _queued;
            public readonly List<int> Items = new List<int>();
            public int Count => Items.Count;

            public void Add(List<int> vertices, int vertexCount)
            {
                // Grown, keeping what is already queued. A wholesale REPLACEMENT of the vertex set
                // (Remesh, Reset, an undo across one) does invalidate every queued index, but those
                // paths rebuild both grids outright and call Clear on the way through, so they
                // never arrive here holding stale entries. Growing the vertex set only ever
                // APPENDS, and an append leaves every existing index naming the same vertex it
                // always did - dropping the queue for it would silently strand whatever moved
                // earlier in the same frame, leaving those vertices bucketed at last frame's
                // positions.
                if (_queued == null)
                {
                    _queued = new bool[vertexCount];
                    Items.Clear();
                }
                else if (_queued.Length < vertexCount)
                {
                    Array.Resize(ref _queued, vertexCount);
                }

                bool[] queued = _queued;
                for (int k = 0; k < vertices.Count; k++)
                {
                    int vi = vertices[k];
                    if (queued[vi]) continue;
                    queued[vi] = true;
                    Items.Add(vi);
                }
            }

            public void Clear()
            {
                if (_queued != null)
                {
                    for (int k = 0; k < Items.Count; k++)
                    {
                        int vi = Items[k];
                        if ((uint)vi < (uint)_queued.Length) _queued[vi] = false;
                    }
                }
                Items.Clear();
            }
        }

        private readonly PendingVertexSet _pendingTriangleGridVertices = new PendingVertexSet();

        private readonly PendingVertexSet _pendingVertexGridVertices = new PendingVertexSet();

        /// Records that these vertices moved, for both spatial indices to catch up with the next time
        /// each is actually READ - SyncTriangleGrid from RaycastMesh, SyncVertexGrid from QueryNear -
        /// rather than re-bucketing them on every apply.
        ///
        /// The two are read at very different rates. A Clay or Smooth stroke raycasts and queries
        /// every frame, so for those this changes nothing but the moment the work happens. A Move or
        /// Pose drag does neither - it tracks a plane and drags a selection made on its first frame -
        /// yet it moves its whole footprint every frame, so every drag frame used to pay to keep two
        /// indices exact that nothing was going to look at until the mouse came up. Now a drag pays
        /// for one catch-up over its net movement, on the first hover after release. Queued only for
        /// an index that exists: one that doesn't is built from current positions when next needed.
        private void QueueSpatialIndexUpdates(List<int> movedVertices)
        {
            int vertexCount = _vertexCount;
            if (_triangleGrid != null) _pendingTriangleGridVertices.Add(movedVertices, vertexCount);
            if (_spatialGrid != null) _pendingVertexGridVertices.Add(movedVertices, vertexCount);
        }

        /// Brings the triangle-raycast grid up to date with every queued move, or builds it if there
        /// is none (a fresh object, or a mid-Play recompile nulling it).
        private void SyncTriangleGrid()
        {
            if (_triangleGrid == null) { RebuildTriangleGrid(); return; }
            if (_pendingTriangleGridVertices.Count == 0) return;

            using (TriangleGridSyncMarker.Auto())
            {
                if (!MeshBoundsFitInsideTriangleGrid())
                {
                    // A stroke moved geometry outside the region the triangle grid was built
                    // for - its bounds don't grow on their own (see RebuildTriangleGrid remarks),
                    // so an incremental update here would re-bucket the moved triangles using
                    // stale bounds, and every future raycast's ray-vs-bounds clip test would clip
                    // away the part of the ray that now needs to reach them. This is the fix for
                    // the "Move brush stops registering on the same spot after pushing it once"
                    // bug: RaycastMesh would silently return false for that area, forever, until
                    // whatever else happened to trigger a full rebuild.
                    RebuildTriangleGrid();
                }
                else
                {
                    MeshAdjacency topology = EnsureAdjacency();
                    _triangleGrid.UpdateFromMovedVertices(_pendingTriangleGridVertices.Items,
                        topology.TriangleStart, topology.TriangleCount, topology.TriangleIndices,
                        _workingVertices, _workingTriangles);
                    _pendingTriangleGridVertices.Clear();
                }
            }
        }

        /// The vertex index's counterpart of SyncTriangleGrid. Callers establish first that the index
        /// was built for the current positions array (see SpatialIndexIsCurrent).
        private void SyncVertexGrid()
        {
            if (_pendingVertexGridVertices.Count == 0) return;

            using (VertexGridSyncMarker.Auto())
            {
                _spatialGrid.UpdateVertices(_pendingVertexGridVertices.Items);
                _pendingVertexGridVertices.Clear();
            }
        }

        /// Rebuilds the spatial index used by SelectGrab/QueryNear over the current vertex
        /// positions, unconditionally. O(vertex count) - a stroke start should call
        /// PrepareSpatialIndex instead, which only rebuilds when it has to.
        public void RebuildSpatialIndex(float cellSize)
        {
            _spatialGrid = new VertexSpatialGrid(_workingVertices, _vertexCount, cellSize);
            // A fresh build already reflects every position, so nothing queued before it applies.
            _pendingVertexGridVertices.Clear();
        }

        // How far the existing index's cell size may sit from the one asked for, as a ratio either
        // way, before PrepareSpatialIndex rebuilds instead of reusing. Query cost rises both as cells
        // shrink relative to the query radius (more cells to visit) and as they grow past it (more
        // vertices per cell to test). This was 1.5, which kept every query near ideal but rebuilt
        // the index - 40-50ms at 1.3M triangles - on the first dab after almost any brush resize,
        // and resizing is something a sculptor does constantly. Clay and Crease now make one or two
        // queries per frame rather than one per dab (see SculptController.DabProgram), so a query a
        // few times off ideal costs a fraction of a millisecond while the rebuild it avoids is a
        // visible hitch. At 4x a cell is between an eighth and twice the brush radius.
        private const float SpatialIndexReuseRatio = 4f;

        /// Readies the vertex index for a stroke whose brush wants cells of about `cellSize`,
        /// rebuilding it only when it has to.
        ///
        /// This used to rebuild unconditionally on every mouse press. There were two reasons: an index
        /// bucketed against pre-stroke positions went stale as vertices moved, and cell size should
        /// track the brush. The first no longer holds - the index is kept exact as vertices move (see
        /// QueueSpatialIndexUpdates) - so an index already built for this positions array at a
        /// compatible cell size is exactly what a rebuild would produce, and the rebuild was pure cost
        /// landing in the first frame of every stroke: the frame a user watches most closely to see
        /// whether the brush responded.
        public void PrepareSpatialIndex(float cellSize)
        {
            if (SpatialIndexIsCurrent())
            {
                float ratio = _spatialGrid.CellSize / Mathf.Max(cellSize, 0.0001f);
                if (ratio <= SpatialIndexReuseRatio && ratio >= 1f / SpatialIndexReuseRatio)
                {
                    SyncVertexGrid();
                    return;
                }
            }
            RebuildSpatialIndex(cellSize);
        }

        /// True if the vertex index exists and was built over THIS positions array - a same-length
        /// replacement array describes a different shape, which the index would still answer for.
        private bool SpatialIndexIsCurrent() =>
            _spatialGrid != null && _spatialGrid.VertexCount == _vertexCount &&
            ReferenceEquals(_spatialGrid.Positions, _workingVertices);

        /// Vertex indices within `radius` of a local-space point - exact, see VertexSpatialGrid.Query.
        /// Lazily builds the index with a radius-derived cell size if nothing has prepared one, and
        /// catches it up with any queued vertex moves first (see QueueSpatialIndexUpdates).
        /// Hidden vertices are dropped from the result, which is what makes hidden geometry
        /// un-sculptable: every brush, the mask brush and SelectGrab all reach the mesh through
        /// this one method, so filtering here covers all of them at once and cannot be forgotten
        /// by a brush added later. Compacts the grid's own reused buffer in place rather than
        /// allocating a filtered copy per call.
        public List<int> QueryNear(Vector3 localPoint, float radius)
        {
            if (!SpatialIndexIsCurrent()) RebuildSpatialIndex(Mathf.Max(radius * 0.5f, 0.01f));
            else SyncVertexGrid();
            List<int> candidates = _spatialGrid.Query(localPoint, radius);
            if (!_anyHidden || _hiddenVertices == null) return candidates;

            int w = 0;
            for (int k = 0; k < candidates.Count; k++)
            {
                int i = candidates[k];
                if (i >= 0 && i < _vertexCount && _hiddenVertices[i]) continue;
                candidates[w++] = i;
            }
            candidates.RemoveRange(w, candidates.Count - w);
            return candidates;
        }
    }
}
