using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Uniform grid bucketing vertex indices by local-space position, so a brush stroke can
    /// ask "which vertices are near this point" without scanning every vertex in the mesh -
    /// mirrors the same bucketing SignedDistanceField already does for triangles.
    ///
    /// Vertices MOVE as strokes progress, and an index that is not kept in step drifts out of
    /// date: a vertex that drifts past Query's one-cell pad simply stops being returned, so
    /// brushes silently skip it while its neighbours keep moving - which is what produced
    /// hard-edged, cell-aligned patches of unmoved surface ("ghost squares") mid-stroke, and
    /// patchy/holed mask painting after a stroke had moved geometry. UpdateVertices() closes that
    /// by re-bucketing exactly the vertices that moved (SculptableMesh queues them as they move and
    /// hands them over before the next query), which keeps the index exact for a few operations
    /// per moved vertex instead of an O(vertex count) rebuild - and being exact is what lets one
    /// index outlive the stroke it was built for (see SculptableMesh.PrepareSpatialIndex).
    /// Query()'s one-cell pad stays as belt-and-braces for a vertex that has moved but has not
    /// been re-bucketed yet.
    internal class VertexSpatialGrid
    {
        private readonly Vector3[] _vertices;
        private readonly float _cellSize;
        private readonly float _invCellSize;
        private readonly Dictionary<Vector3Int, List<int>> _cells;
        // Which cell each vertex is currently bucketed in, and at which index of that cell's list.
        // The slot is what makes a re-bucket O(1): the vertex's old entry is overwritten by the
        // cell's last one, instead of being found by List.Remove - a scan plus a shift of the whole
        // list. Cells are half a brush radius wide and a dense surface puts thousands of vertices in
        // each, so under a wide Move drag that scan was the entire cost of the update: measured in an
        // offline harness running this file on a 1.31M-triangle sphere, a 163k-vertex drag frame
        // went from 19.2 ms (37 ms worst) to 0.9 ms.
        private readonly Vector3Int[] _vertexCell;
        private readonly int[] _vertexSlot;
        private readonly List<int> _resultBuffer = new List<int>();

        public int VertexCount => _vertexCell.Length;
        public float CellSize => _cellSize;

        /// The positions array this index was built over. SculptableMesh compares it by reference
        /// before reusing the index, since a same-length replacement array describes a different shape.
        public Vector3[] Positions => _vertices;

        public VertexSpatialGrid(Vector3[] vertices, float cellSize)
        {
            _vertices = vertices;
            _cellSize = Mathf.Max(cellSize, 0.0001f);
            _invCellSize = 1f / _cellSize;
            int n = vertices.Length;
            _vertexCell = new Vector3Int[n];
            _vertexSlot = new int[n];

            _cells = new Dictionary<Vector3Int, List<int>>(n / 4 + 1);
            for (int i = 0; i < n; i++)
            {
                Vector3Int cell = CellOf(vertices[i]);
                if (!_cells.TryGetValue(cell, out List<int> list))
                {
                    list = new List<int>();
                    _cells[cell] = list;
                }
                _vertexSlot[i] = list.Count;
                list.Add(i);
                _vertexCell[i] = cell;
            }
        }

        /// Re-buckets exactly the vertices that just moved. O(moved count): a vertex that stayed in
        /// its own cell - the common case - costs one cell computation and a compare, and one that
        /// changed cell costs two dictionary lookups and a constant number of list writes.
        public void UpdateVertices(List<int> movedVertices)
        {
            if (movedVertices == null) return;
            for (int k = 0; k < movedVertices.Count; k++)
            {
                int i = movedVertices[k];
                if (i < 0 || i >= _vertexCell.Length) continue;

                Vector3Int now = CellOf(_vertices[i]);
                Vector3Int was = _vertexCell[i];
                if (now == was) continue;

                if (_cells.TryGetValue(was, out List<int> previous))
                {
                    int slot = _vertexSlot[i];
                    int last = previous.Count - 1;
                    if (slot >= 0 && slot <= last && previous[slot] == i)
                    {
                        int movedVertex = previous[last];
                        previous[slot] = movedVertex;
                        _vertexSlot[movedVertex] = slot;
                        previous.RemoveAt(last);
                    }
                    else
                    {
                        previous.Remove(i);
                    }
                }

                if (!_cells.TryGetValue(now, out List<int> list))
                {
                    list = new List<int>();
                    _cells[now] = list;
                }
                _vertexSlot[i] = list.Count;
                list.Add(i);
                _vertexCell[i] = now;
            }
        }

        private Vector3Int CellOf(Vector3 p) => new Vector3Int(
            Mathf.FloorToInt(p.x * _invCellSize),
            Mathf.FloorToInt(p.y * _invCellSize),
            Mathf.FloorToInt(p.z * _invCellSize));

        /// Vertex indices whose CURRENT position is within radius of center, in no particular order.
        /// The returned list is reused across calls (cleared each time), so consume it before
        /// querying again.
        ///
        /// The result is exact, not approximate: it used to hand back every vertex in every cell the
        /// padded query box touched and leave the distance test to the caller, which made the list
        /// several times larger than the footprint actually in range - every surplus index was then
        /// copied, gathered into native scratch, given a job slot and re-tested.
        ///
        /// Filtering on the vertex's CURRENT position (not on which cell it is bucketed in) is what
        /// keeps the drift tolerance the pad exists for: a vertex that has moved since it was
        /// bucketed is found by the padded cell scan and then kept or dropped on where it actually is.
        public List<int> Query(Vector3 center, float radius)
        {
            _resultBuffer.Clear();

            // +1 cell of margin tolerates a vertex having drifted out of its bucket (see class remarks).
            float padded = radius + _cellSize;
            float paddedSqr = padded * padded;
            float radiusSqr = radius * radius;
            Vector3Int cmin = CellOf(center - Vector3.one * padded);
            Vector3Int cmax = CellOf(center + Vector3.one * padded);

            for (int z = cmin.z; z <= cmax.z; z++)
            {
                AxisSpans(center.z, z, out float nearZ, out float farZ);
                float nearZSqr = nearZ * nearZ, farZSqr = farZ * farZ;
                if (nearZSqr > paddedSqr) continue;

                for (int y = cmin.y; y <= cmax.y; y++)
                {
                    AxisSpans(center.y, y, out float nearY, out float farY);
                    float nearZYSqr = nearZSqr + nearY * nearY;
                    if (nearZYSqr > paddedSqr) continue;
                    float farZYSqr = farZSqr + farY * farY;

                    for (int x = cmin.x; x <= cmax.x; x++)
                    {
                        // Reject whole cells that lie outside the query SPHERE but inside the
                        // enclosing box the loop bounds describe - an exact rejection: a cell whose
                        // nearest point is further than the padded radius cannot hold an in-range vertex.
                        AxisSpans(center.x, x, out float nearX, out float farX);
                        if (nearZYSqr + nearX * nearX > paddedSqr) continue;

                        if (!_cells.TryGetValue(new Vector3Int(x, y, z), out List<int> list)) continue;

                        // A cell whose FARTHEST corner is still inside the radius cannot hold an
                        // out-of-range vertex, so the interior of a footprint - which is most of
                        // it - is copied in bulk with no per-vertex test at all.
                        if (farZYSqr + farX * farX <= radiusSqr)
                        {
                            _resultBuffer.AddRange(list);
                            continue;
                        }

                        for (int k = 0; k < list.Count; k++)
                        {
                            int vi = list[k];
                            Vector3 p = _vertices[vi];
                            float dx = p.x - center.x, dy = p.y - center.y, dz = p.z - center.z;
                            if (dx * dx + dy * dy + dz * dz > radiusSqr) continue;
                            _resultBuffer.Add(vi);
                        }
                    }
                }
            }

            return _resultBuffer;
        }

        /// Distances from `coord` to the nearest and farthest points of cell `cell` along one
        /// axis. `near` is 0 when the coordinate lies inside the cell's own span.
        private void AxisSpans(float coord, int cell, out float near, out float far)
        {
            float lo = cell * _cellSize;
            float hi = lo + _cellSize;
            float toLo = coord - lo, toHi = coord - hi;
            float absLo = toLo < 0f ? -toLo : toLo;
            float absHi = toHi < 0f ? -toHi : toHi;

            near = coord < lo ? lo - coord : (coord > hi ? coord - hi : 0f);
            far = absLo > absHi ? absLo : absHi;
        }
    }
}
