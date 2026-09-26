using System;
using UnityEngine;

namespace Sculpting
{
    /// Closest point on a triangle mesh, through a uniform grid of triangle bounding boxes.
    ///
    /// Used to put the quad remesher's output back onto the sculpt: extracted vertices sit on
    /// tangent-plane lattice points, slightly off a curved surface, and relaxation moves them
    /// further. Queries are expected to start near the surface (within a cell or two), which is
    /// what makes the expanding-ring search cheap.
    internal sealed class SurfaceProjector
    {
        private readonly Vector3[] _v;
        private readonly int[] _t;
        private readonly Vector3 _origin;
        private readonly float _cell, _invCell;
        private readonly int _nx, _ny, _nz;
        private readonly int[] _cellStart;
        private readonly int[] _cellTris;

        /// Cells are sized from `cellSize` but grown if the grid would exceed `maxCells`.
        public SurfaceProjector(Vector3[] vertices, int[] triangles, float cellSize, int maxCells = 4_000_000)
        {
            _v = vertices;
            _t = triangles;
            Vector3 min = vertices[0], max = vertices[0];
            foreach (Vector3 p in vertices) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            Vector3 size = max - min;
            float cell = Mathf.Max(cellSize, 1e-6f);
            while ((long)(size.x / cell + 1) * (long)(size.y / cell + 1) * (long)(size.z / cell + 1) > maxCells) cell *= 1.25f;
            _cell = cell;
            _invCell = 1f / cell;
            _origin = min;
            _nx = (int)(size.x * _invCell) + 1;
            _ny = (int)(size.y * _invCell) + 1;
            _nz = (int)(size.z * _invCell) + 1;

            int triCount = triangles.Length / 3;
            var count = new int[_nx * _ny * _nz + 1];
            for (int pass = 0; pass < 2; pass++)
            {
                int[] fill = pass == 1 ? (int[])count.Clone() : null;
                if (pass == 1) _cellTris = new int[count[count.Length - 1]];
                for (int f = 0; f < triCount; f++)
                {
                    Cells(f, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1);
                    for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int c = (z * _ny + y) * _nx + x;
                        if (pass == 0) count[c + 1]++;
                        else _cellTris[fill[c]++] = f;
                    }
                }
                if (pass == 0) for (int c = 0; c + 1 < count.Length; c++) count[c + 1] += count[c];
            }
            _cellStart = count;
        }

        /// Closest point on the surface to `p` (and that point's triangle normal, unnormalised
        /// direction by winding).
        public Vector3 Project(Vector3 p, out Vector3 normal) => Project(p, out normal, out _);

        /// Project, also returning the index of the triangle the closest point lies on (-1 if
        /// the mesh is empty).
        public Vector3 Project(Vector3 p, out Vector3 normal, out int triangle)
        {
            int cx = Clamp((int)((p.x - _origin.x) * _invCell), _nx);
            int cy = Clamp((int)((p.y - _origin.y) * _invCell), _ny);
            int cz = Clamp((int)((p.z - _origin.z) * _invCell), _nz);

            float best = float.MaxValue;
            Vector3 bestPoint = p;
            int bestTri = -1;
            int maxRing = Math.Max(_nx, Math.Max(_ny, _nz));
            for (int ring = 0; ring <= maxRing; ring++)
            {
                // Everything within `ring` cells has been seen; stop once the best hit is
                // closer than any unseen cell could be.
                if (bestTri >= 0 && ring > 0 && (ring - 1) * _cell > Mathf.Sqrt(best)) break;
                for (int z = cz - ring; z <= cz + ring; z++)
                {
                    if (z < 0 || z >= _nz) continue;
                    for (int y = cy - ring; y <= cy + ring; y++)
                    {
                        if (y < 0 || y >= _ny) continue;
                        bool yzShell = z == cz - ring || z == cz + ring || y == cy - ring || y == cy + ring;
                        for (int x = cx - ring; x <= cx + ring; x += (yzShell || ring == 0) ? 1 : 2 * ring)
                        {
                            if (x < 0 || x >= _nx) continue;
                            int c = (z * _ny + y) * _nx + x;
                            for (int i = _cellStart[c]; i < _cellStart[c + 1]; i++)
                            {
                                int f = _cellTris[i];
                                Vector3 q = ClosestOnTriangle(p, _v[_t[f * 3]], _v[_t[f * 3 + 1]], _v[_t[f * 3 + 2]]);
                                float d = (q - p).sqrMagnitude;
                                if (d < best) { best = d; bestPoint = q; bestTri = f; }
                            }
                        }
                    }
                }
            }

            triangle = bestTri;
            if (bestTri < 0) { normal = Vector3.up; return p; }
            Vector3 a = _v[_t[bestTri * 3]];
            normal = VectorMath.NormalizeOr(Vector3.Cross(_v[_t[bestTri * 3 + 1]] - a, _v[_t[bestTri * 3 + 2]] - a), Vector3.up);
            return bestPoint;
        }

        private void Cells(int f, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
        {
            Vector3 a = _v[_t[f * 3]], b = _v[_t[f * 3 + 1]], c = _v[_t[f * 3 + 2]];
            Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)) - _origin, hi = Vector3.Max(a, Vector3.Max(b, c)) - _origin;
            x0 = Clamp((int)(lo.x * _invCell), _nx); x1 = Clamp((int)(hi.x * _invCell), _nx);
            y0 = Clamp((int)(lo.y * _invCell), _ny); y1 = Clamp((int)(hi.y * _invCell), _ny);
            z0 = Clamp((int)(lo.z * _invCell), _nz); z1 = Clamp((int)(hi.z * _invCell), _nz);
        }

        private static int Clamp(int i, int n) => i < 0 ? 0 : i >= n ? n - 1 : i;

        /// Closest point on triangle abc to p (Ericson, Real-Time Collision Detection, 5.1.5).
        public static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
                return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

            float denom = 1f / (va + vb + vc);
            float v = vb * denom, w = vc * denom;
            return a + ab * v + ac * w;
        }
    }
}
