using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Uniform grid bucketing vertex indices by local-space position, so a brush stroke can
    /// ask "which vertices are near this point" without scanning every vertex in the mesh -
    /// mirrors the same bucketing SignedDistanceField already does for triangles. Built once
    /// per stroke (see SculptController's stroke-start rebuild) rather than every frame, since
    /// rebuilding is itself O(vertex count) - the whole point is to avoid paying that cost on
    /// every frame of a drag.
    ///
    /// Vertices MOVE as strokes progress, which used to make this index drift out of date: the
    /// one-cell Query() pad below tolerates a little of that, but a stroke (or a whole series
    /// of strokes, since the index survives until the NEXT stroke's rebuild) can easily push a
    /// vertex further than one cell from where it was bucketed. A vertex that drifts past the
    /// pad simply stops being returned as a candidate, so brushes silently skip it while its
    /// neighbours keep moving - which is what produced hard-edged, cell-aligned patches of
    /// unmoved surface ("ghost squares") mid-stroke, and patchy/holed mask painting after a
    /// stroke had moved geometry. UpdateVertices() closes that: SculptableMesh.
    /// ApplyVerticesLocal re-buckets exactly the vertices it just moved, so the index stays
    /// exact for the cost of a few dictionary touches per moved vertex instead of an O(vertex
    /// count) rebuild. Query()'s one-cell pad stays as belt-and-braces for the same-frame case
    /// (a brush moves vertices and re-queries before ApplyVerticesLocal runs).
    internal class VertexSpatialGrid
    {
        private readonly Vector3[] _vertices;
        private readonly float _cellSize;
        private readonly Dictionary<Vector3Int, List<int>> _cells;
        // Which cell each vertex is currently bucketed in - without this, UpdateVertices would
        // have no way to find and remove a moved vertex's OLD entry short of scanning every
        // bucket, and re-adding it without removing would leave a duplicate behind that keeps
        // reporting the vertex near its old position forever.
        private readonly Vector3Int[] _vertexCell;
        private readonly List<int> _resultBuffer = new List<int>();

        public int VertexCount => _vertexCell.Length;

        public VertexSpatialGrid(Vector3[] vertices, float cellSize)
        {
            _vertices = vertices;
            _cellSize = Mathf.Max(cellSize, 0.0001f);
            _cells = new Dictionary<Vector3Int, List<int>>(vertices.Length / 4 + 1);
            _vertexCell = new Vector3Int[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3Int cell = CellOf(vertices[i]);
                if (!_cells.TryGetValue(cell, out List<int> list))
                {
                    list = new List<int>();
                    _cells[cell] = list;
                }
                list.Add(i);
                _vertexCell[i] = cell;
            }
        }

        /// Re-buckets exactly the vertices that just moved, keeping this index exact for the
        /// rest of the stroke instead of letting drift accumulate (see class remarks). O(moved
        /// count) with a small constant: a vertex that stayed inside its own cell - the common
        /// case, since cell size tracks the brush radius - costs one CellOf and a compare.
        /// The List.Remove below is O(bucket size), which is fine precisely because cell size
        /// is chosen relative to the brush footprint, keeping buckets to a handful of entries.
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

                if (_cells.TryGetValue(was, out List<int> previous)) previous.Remove(i);
                if (!_cells.TryGetValue(now, out List<int> list))
                {
                    list = new List<int>();
                    _cells[now] = list;
                }
                list.Add(i);
                _vertexCell[i] = now;
            }
        }

        private Vector3Int CellOf(Vector3 p) => new Vector3Int(
            Mathf.FloorToInt(p.x / _cellSize),
            Mathf.FloorToInt(p.y / _cellSize),
            Mathf.FloorToInt(p.z / _cellSize));

        /// Vertex indices whose CURRENT position is within radius of center. The returned list is
        /// reused across calls (cleared each time), so consume it before querying again.
        ///
        /// The result is exact, not approximate: it used to hand back every vertex in every cell
        /// the padded query box touched and leave the distance test to the caller, which meant the
        /// list was several times larger than the footprint actually in range - the cell pad below
        /// is a whole cell wide, cells are sized at half a brush radius, and the vertices of a
        /// mesh sit on a SURFACE, so the overshoot goes as the square of the radius ratio: about
        /// 3.6x for an ordinary brush footprint and 1.9x for Clay's much wider relax reach. Every
        /// one of those surplus indices was then copied into the result, gathered into native
        /// scratch, given a job slot and re-tested. Testing here instead moves one distance
        /// computation the callers were already doing anyway and drops everything downstream of it.
        ///
        /// Filtering on the vertex's CURRENT position (not on which cell it is bucketed in) is what
        /// keeps the drift tolerance the pad exists for: a vertex that has moved since it was
        /// bucketed is found by the padded cell scan and then kept or dropped on where it actually
        /// is now, which is exactly the right answer.
        public List<int> Query(Vector3 center, float radius)
        {
            _resultBuffer.Clear();

            // +1 cell of margin tolerates a vertex having drifted out of its original bucket
            // since this grid was built (see class remarks).
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
                        // enclosing box the loop bounds describe - there is a lot of box outside
                        // the sphere, and this is an exact rejection: a cell whose nearest point is
                        // further than the padded radius cannot hold an in-range vertex.
                        AxisSpans(center.x, x, out float nearX, out float farX);
                        if (nearZYSqr + nearX * nearX > paddedSqr) continue;

                        if (!_cells.TryGetValue(new Vector3Int(x, y, z), out List<int> list)) continue;

                        // A cell whose FARTHEST corner is still inside the radius cannot hold an
                        // out-of-range vertex, so the interior of a footprint - which is most of
                        // it - is copied in bulk with no per-vertex test at all. Only the rim
                        // cells, the ones the sphere actually cuts through, pay for one.
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
