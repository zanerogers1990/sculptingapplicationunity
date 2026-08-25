using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Uniform grid bucketing vertex indices by local-space position, so a brush stroke can
    /// ask "which vertices are near this point" without scanning every vertex in the mesh -
    /// mirrors the same bucketing SignedDistanceField already does for triangles. Built once
    /// per stroke (see SculptController's stroke-start rebuild) rather than every frame, since
    /// rebuilding is itself O(vertex count) - the whole point is to avoid paying that cost on
    /// every frame of a drag. Vertices do drift within their bucketed cell as a stroke
    /// progresses, so Query() pads its cell coverage by one extra cell in every direction to
    /// tolerate that drift; combined with the exact per-vertex distance check every brush
    /// still does on the returned candidates, a little over-inclusion here is harmless - it
    /// only costs a few redundant candidates, never a wrong result.
    internal class VertexSpatialGrid
    {
        private readonly Vector3[] _vertices;
        private readonly float _cellSize;
        private readonly Dictionary<Vector3Int, List<int>> _cells;
        private readonly List<int> _resultBuffer = new List<int>();

        public VertexSpatialGrid(Vector3[] vertices, float cellSize)
        {
            _vertices = vertices;
            _cellSize = Mathf.Max(cellSize, 0.0001f);
            _cells = new Dictionary<Vector3Int, List<int>>(vertices.Length / 4 + 1);

            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3Int cell = CellOf(vertices[i]);
                if (!_cells.TryGetValue(cell, out List<int> list))
                {
                    list = new List<int>();
                    _cells[cell] = list;
                }
                list.Add(i);
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
