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
        public void UpdateVertices(IReadOnlyCollection<int> movedVertices)
        {
            if (movedVertices == null) return;
            foreach (int i in movedVertices)
            {
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

        /// Candidate vertex indices within (approximately) radius of center - callers must
        /// still check exact distance themselves, same as before this class existed. The
        /// returned list is reused across calls (cleared each time), so consume it before
        /// querying again.
        public List<int> Query(Vector3 center, float radius)
        {
            _resultBuffer.Clear();

            // +1 cell of margin tolerates a vertex having drifted out of its original bucket
            // since this grid was built (see class remarks).
            float padded = radius + _cellSize;
            Vector3Int cmin = CellOf(center - Vector3.one * padded);
            Vector3Int cmax = CellOf(center + Vector3.one * padded);

            for (int z = cmin.z; z <= cmax.z; z++)
            for (int y = cmin.y; y <= cmax.y; y++)
            for (int x = cmin.x; x <= cmax.x; x++)
            {
                if (_cells.TryGetValue(new Vector3Int(x, y, z), out List<int> list))
                    _resultBuffer.AddRange(list);
            }

            return _resultBuffer;
        }
    }
}
