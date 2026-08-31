using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Accelerated signed distance queries against a static triangle soup, used by
    /// MeshRemesher to sample its voxel grid. Distance magnitude comes from the nearest
    /// triangle; sign comes from a winding-number ray cast, which (unlike a nearest-face-normal
    /// heuristic) stays correct across concave folds and overhangs that sculpting produces.
    ///
    /// Two independent acceleration structures, because the two queries have different shapes:
    ///
    ///   - A 3D bin grid for nearest-triangle distance, which is a genuinely spatial search.
    ///   - A 2D bin grid over (y,z) for the sign rays, which are all parallel to +X. Binning
    ///     those in 3D was making every column walk the whole x extent of the 3D grid and
    ///     de-duplicate the triangles it met with a HashSet; projected to 2D, a column maps to
    ///     exactly ONE bin holding exactly the triangles that can possibly be on its ray, so
    ///     the walk and the de-duplication both disappear.
    ///
    /// Both grids are stored CSR-style (a start offset per bin into one flat item array) rather
    /// than as an array of Lists. On a dense source mesh the List-per-bin form allocated one
    /// object per occupied bin - hundreds of thousands of them for a sculpted mesh - and spread
    /// the triangle indices across the heap; the flat form allocates twice, total, and keeps
    /// each bin's indices contiguous.
    internal class SignedDistanceField
    {
        private readonly Vector3[] _vertices;
        private readonly int[] _triangles;
        private readonly Bounds _bounds;
        private readonly float _cellSize;
        private readonly Vector3Int _binDims;

        // 3D bins, CSR: triangles of bin i are _binItems[_binStart[i] .. _binStart[i+1]).
        private readonly int[] _binStart;
        private readonly int[] _binItems;

        // 2D bins over (y,z) for the +X sign rays, same CSR layout.
        private readonly float _colCellSize;
        private readonly int _colDimY, _colDimZ;
        private readonly int[] _colStart;
        private readonly int[] _colItems;

        public SignedDistanceField(Vector3[] verts, int[] tris, float cellSize)
        {
            _vertices = verts;
            _triangles = tris;
            _cellSize = Mathf.Max(cellSize, 0.0001f);

            int triCount = tris.Length / 3;

            Vector3 min = verts[0], max = verts[0];
            for (int i = 1; i < verts.Length; i++)
            {
                min = Vector3.Min(min, verts[i]);
                max = Vector3.Max(max, verts[i]);
            }
            min -= Vector3.one * _cellSize;
            max += Vector3.one * _cellSize;
            _bounds = new Bounds();
            _bounds.SetMinMax(min, max);

            _binDims = new Vector3Int(
                Mathf.Max(1, Mathf.CeilToInt(_bounds.size.x / _cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(_bounds.size.y / _cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(_bounds.size.z / _cellSize)));

            BuildBins3D(triCount, out _binStart, out _binItems);

            // The 2D grid is sized off the source mesh's own triangle density, deliberately
            // COARSER than the 3D one (a bin gathers triangles from the front and the back of
            // the shell, so equal spacing would leave it far denser). A column reads a single
            // bin, so what this trades is a slightly longer per-column triangle list against a
            // much smaller build - and the column count scales with the OUTPUT resolution
            // while this build scales with the input, so keeping the build cheap is the right
            // side to err on.
            float area = Mathf.Max(_bounds.size.y * _bounds.size.z, 1e-8f);
            _colCellSize = Mathf.Max(Mathf.Sqrt(area / Mathf.Max(1, triCount)) * 2f, 1e-6f);
            _colDimY = Mathf.Max(1, Mathf.CeilToInt(_bounds.size.y / _colCellSize));
            _colDimZ = Mathf.Max(1, Mathf.CeilToInt(_bounds.size.z / _colCellSize));
            BuildBins2D(triCount, out _colStart, out _colItems);
        }

        private void BuildBins3D(int triCount, out int[] start, out int[] items)
        {
            int binCount = _binDims.x * _binDims.y * _binDims.z;
            start = new int[binCount + 1];

            // Counting sort: one pass to size every bin, a prefix sum, then one pass to fill.
            for (int t = 0; t < triCount; t++)
            {
                TriangleBinRange(t, out Vector3Int bmin, out Vector3Int bmax);
                for (int z = bmin.z; z <= bmax.z; z++)
                for (int y = bmin.y; y <= bmax.y; y++)
                for (int x = bmin.x; x <= bmax.x; x++)
                    start[BinIndex(x, y, z)]++;
            }

            int running = 0;
            for (int i = 0; i <= binCount; i++)
            {
                int c = i < binCount ? start[i] : 0;
                start[i] = running;
                running += c;
            }

            items = new int[running];
            var cursor = (int[])start.Clone();
            for (int t = 0; t < triCount; t++)
            {
                TriangleBinRange(t, out Vector3Int bmin, out Vector3Int bmax);
                for (int z = bmin.z; z <= bmax.z; z++)
                for (int y = bmin.y; y <= bmax.y; y++)
                for (int x = bmin.x; x <= bmax.x; x++)
                    items[cursor[BinIndex(x, y, z)]++] = t;
            }
        }

        private void TriangleBinRange(int t, out Vector3Int bmin, out Vector3Int bmax)
        {
            Vector3 a = _vertices[_triangles[t * 3]];
            Vector3 b = _vertices[_triangles[t * 3 + 1]];
            Vector3 c = _vertices[_triangles[t * 3 + 2]];
            bmin = CellOf(Vector3.Min(a, Vector3.Min(b, c)));
            bmax = CellOf(Vector3.Max(a, Vector3.Max(b, c)));
        }

        private void BuildBins2D(int triCount, out int[] start, out int[] items)
        {
            int binCount = _colDimY * _colDimZ;
            start = new int[binCount + 1];

            for (int t = 0; t < triCount; t++)
            {
                ColumnBinRange(t, out int y0, out int y1, out int z0, out int z1);
                for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    start[y + _colDimY * z]++;
            }

            int running = 0;
            for (int i = 0; i <= binCount; i++)
            {
                int c = i < binCount ? start[i] : 0;
                start[i] = running;
                running += c;
            }

            items = new int[running];
            var cursor = (int[])start.Clone();
            for (int t = 0; t < triCount; t++)
            {
                ColumnBinRange(t, out int y0, out int y1, out int z0, out int z1);
                for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    items[cursor[y + _colDimY * z]++] = t;
            }
        }

        private void ColumnBinRange(int t, out int y0, out int y1, out int z0, out int z1)
        {
            Vector3 a = _vertices[_triangles[t * 3]];
            Vector3 b = _vertices[_triangles[t * 3 + 1]];
            Vector3 c = _vertices[_triangles[t * 3 + 2]];
            y0 = ColumnCell(Mathf.Min(a.y, b.y, c.y) - _bounds.min.y, _colDimY);
            y1 = ColumnCell(Mathf.Max(a.y, b.y, c.y) - _bounds.min.y, _colDimY);
            z0 = ColumnCell(Mathf.Min(a.z, b.z, c.z) - _bounds.min.z, _colDimZ);
            z1 = ColumnCell(Mathf.Max(a.z, b.z, c.z) - _bounds.min.z, _colDimZ);
        }

        private int ColumnCell(float local, int dim) => Mathf.Clamp(Mathf.FloorToInt(local / _colCellSize), 0, dim - 1);

        private Vector3Int CellOf(Vector3 p)
        {
            Vector3 local = p - _bounds.min;
            int x = Mathf.Clamp(Mathf.FloorToInt(local.x / _cellSize), 0, _binDims.x - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(local.y / _cellSize), 0, _binDims.y - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt(local.z / _cellSize), 0, _binDims.z - 1);
            return new Vector3Int(x, y, z);
        }

        private int BinIndex(int x, int y, int z) => x + _binDims.x * (y + _binDims.y * z);

        /// Where the mesh's own geometry starts along -X. A sign ray has to begin at or before
        /// this to count every crossing in front of it (see ColumnCrossings).
        public float MinX => _bounds.min.x;

        /// One ray/surface crossing along a column's +X ray: where it happened, and whether the
        /// ray was entering (+1) or leaving (-1) the surface there.
        public readonly struct Crossing : IComparable<Crossing>
        {
            public readonly float X;
            public readonly int Winding;
            public Crossing(float x, int winding) { X = x; Winding = winding; }
            public int CompareTo(Crossing other) => X.CompareTo(other.X);
        }

        public float NearestUnsignedDistance(Vector3 p) => NearestUnsignedDistance(p, out _);

        /// Distance to the nearest point of the triangle soup, and the index of the triangle
        /// that point lies on.
        ///
        /// The triangle index is what lets the remesher put a real surface NORMAL on every
        /// edge crossing, which is what its feature-preserving vertex placement needs
        /// (DualContourSolver). It falls straight out of the search that was already running,
        /// so it costs one register - the alternative, re-querying the mesh per crossing, would
        /// have roughly tripled the distance work.
        public float NearestUnsignedDistance(Vector3 p, out int triangle)
        {
            Vector3Int center = CellOf(p);
            int maxRadius = Mathf.Max(_binDims.x, _binDims.y, _binDims.z);

            float bestSqrDist = float.MaxValue;
            int best = -1;
            int foundAtRadius = -1;

            for (int radius = 0; radius <= maxRadius; radius++)
            {
                int x0 = center.x - radius, x1 = center.x + radius;
                int y0 = center.y - radius, y1 = center.y + radius;
                int z0 = center.z - radius, z1 = center.z + radius;

                for (int z = z0; z <= z1; z++)
                {
                    if (z < 0 || z >= _binDims.z) continue;
                    for (int y = y0; y <= y1; y++)
                    {
                        if (y < 0 || y >= _binDims.y) continue;
                        for (int x = x0; x <= x1; x++)
                        {
                            if (x < 0 || x >= _binDims.x) continue;
                            bool onShell = x == x0 || x == x1 || y == y0 || y == y1 || z == z0 || z == z1;
                            if (radius > 0 && !onShell) continue;

                            int bin = BinIndex(x, y, z);
                            int from = _binStart[bin], to = _binStart[bin + 1];
                            if (from == to) continue;

                            // Reject the whole bin when even its NEAREST point is farther than
                            // a triangle already found. Exact - it can only skip triangles that
                            // could not have won anyway - but it removes most of the work in
                            // the outer shells, which the walk still has to visit because it
                            // cannot know in advance which of them holds the true nearest
                            // triangle. One AABB test replaces a full pass over that bin.
                            if (best >= 0 && SqrDistanceToBin(p, x, y, z) >= bestSqrDist) continue;

                            for (int k = from; k < to; k++)
                            {
                                int t = _binItems[k];
                                Vector3 a = _vertices[_triangles[t * 3]];
                                Vector3 b = _vertices[_triangles[t * 3 + 1]];
                                Vector3 c = _vertices[_triangles[t * 3 + 2]];
                                Vector3 closest = ClosestPointOnTriangle(p, a, b, c);
                                float sqr = (closest - p).sqrMagnitude;
                                if (sqr < bestSqrDist) { bestSqrDist = sqr; best = t; }
                            }
                        }
                    }
                }

                if (best >= 0 && foundAtRadius < 0) foundAtRadius = radius;
                // Keep searching a couple of shells past the first hit - the true nearest
                // triangle can sit in a farther bin than the one containing its closest point.
                if (foundAtRadius >= 0 && radius >= foundAtRadius + 2) break;
            }

            triangle = best;
            return best >= 0 ? Mathf.Sqrt(bestSqrDist) : float.MaxValue;
        }

        /// Outward geometric normal of triangle `t`, matching the winding Unity's own
        /// RecalculateNormals assumes. Not normalised by area - callers normalise once after
        /// combining several.
        public Vector3 TriangleNormal(int t)
        {
            Vector3 a = _vertices[_triangles[t * 3]];
            Vector3 b = _vertices[_triangles[t * 3 + 1]];
            Vector3 c = _vertices[_triangles[t * 3 + 2]];
            return Vector3.Cross(b - a, c - a).normalized;
        }

        /// Squared distance from `p` to the axis-aligned box of bin (x,y,z) - zero when p is
        /// inside it. Standard point/AABB distance: per axis, the overshoot past whichever
        /// face is nearer, or zero between them.
        private float SqrDistanceToBin(Vector3 p, int x, int y, int z)
        {
            float minX = _bounds.min.x + x * _cellSize, maxX = minX + _cellSize;
            float minY = _bounds.min.y + y * _cellSize, maxY = minY + _cellSize;
            float minZ = _bounds.min.z + z * _cellSize, maxZ = minZ + _cellSize;

            float dx = p.x < minX ? minX - p.x : (p.x > maxX ? p.x - maxX : 0f);
            float dy = p.y < minY ? minY - p.y : (p.y > maxY ? p.y - maxY : 0f);
            float dz = p.z < minZ ? minZ - p.z : (p.z > maxZ ? p.z - maxZ : 0f);
            return dx * dx + dy * dy + dz * dz;
        }

        /// Every crossing the +X ray through (wy, wz) starting at `rayStartX` makes with the
        /// surface, sorted by X, appended into `into` (which is cleared first).
        ///
        /// Caller-owned buffer and no instance state touched, so this is safe to call
        /// concurrently from as many worker threads as there are.
        ///
        /// The ray must START at or before the mesh's own -X bound (see MinX): a ray beginning
        /// INSIDE the solid counts none of the crossings behind it and inverts the whole
        /// column's inside/outside. That is not hypothetical - a boolean cutter can extend past
        /// the target's grid, and the sparse remesher starts columns at brick boundaries.
        public void ColumnCrossings(float wy, float wz, float rayStartX, List<Crossing> into)
        {
            into.Clear();

            int cy = ColumnCell(wy - _bounds.min.y, _colDimY);
            int cz = ColumnCell(wz - _bounds.min.z, _colDimZ);
            if (wy < _bounds.min.y || wy > _bounds.max.y || wz < _bounds.min.z || wz > _bounds.max.z) return;

            int bin = cy + _colDimY * cz;
            int from = _colStart[bin], to = _colStart[bin + 1];
            if (from == to) return;

            var rayOrigin = new Vector3(rayStartX, wy, wz);
            for (int k = from; k < to; k++)
            {
                int t = _colItems[k];
                Vector3 a = _vertices[_triangles[t * 3]];
                Vector3 b = _vertices[_triangles[t * 3 + 1]];
                Vector3 c = _vertices[_triangles[t * 3 + 2]];
                if (RayIntersectsTriangleX(rayOrigin, a, b, c, out float hitX, out int winding))
                    into.Add(new Crossing(hitX, winding));
            }

            into.Sort();
        }

        // ComputeColumn runs once per (y,z) column, concurrently across Parallel.For
        // iterations in ComputeInsideMask. Thread-local + Clear() instead of a fresh List per
        // call avoids that many allocations while staying safe under concurrent access.
        private static readonly System.Threading.ThreadLocal<List<Crossing>> _crossingsPool =
            new System.Threading.ThreadLocal<List<Crossing>>(() => new List<Crossing>());

        /// Fills insideOut (flattened x + sx*(y + sy*z), matching MeshRemesher's dense sdf
        /// layout) with the inside/outside sign for every sample on the grid. One +X ray per
        /// (y,z) column instead of one per sample point: the crossings along a column are
        /// shared by every sample on it, so a single sorted sweep gives every sample's winding
        /// in one pass. Columns are independent, so they run in parallel across cores.
        ///
        /// Used by the DENSE path (MeshBoolean, and BuildFromSdf's callers). The sparse
        /// remesher does not call this - it queries ColumnCrossings per brick instead, which
        /// is what lets it avoid ever materialising a whole-grid array.
        public void ComputeInsideMask(Vector3 origin, float cellSize, int sx, int sy, int sz, bool[] insideOut)
        {
            System.Threading.Tasks.Parallel.For(0, sy * sz, columnIndex =>
            {
                int y = columnIndex % sy;
                int z = columnIndex / sy;

                // Nudge off the sample line so an exactly grid-aligned mesh (e.g. a fresh
                // primitive) doesn't graze triangle edges/vertices along the whole column.
                float wy = origin.y + y * cellSize + cellSize * 0.0173f;
                float wz = origin.z + z * cellSize + cellSize * 0.0091f;

                var crossings = _crossingsPool.Value;
                ColumnCrossings(wy, wz, Mathf.Min(origin.x, MinX), crossings);

                // Accumulates a WINDING NUMBER rather than flipping an even-odd parity bit.
                // Parity is only correct for a single closed manifold: MeshJoiner concatenates
                // two (or more) closed shells without welding them (by design - it's Merge
                // Down, not a boolean), so a ray through the region where those shells OVERLAP
                // crosses four surfaces and parity reports "outside" there. That carved a
                // hollow out of exactly the intersection volume. Parity computes the shells'
                // XOR; summing signed crossings computes their union. Same fix also covers a
                // SINGLE mesh sculpted until it self-intersects, which parity broke identically.
                //
                // Tested against `!= 0` rather than `> 0` deliberately: a shell whose winding
                // runs opposite the rest (a mirrored/negatively-scaled object baked in by Join)
                // still reads as solid instead of vanishing.
                int ci = 0;
                int windingNumber = 0;
                for (int x = 0; x < sx; x++)
                {
                    float wx = origin.x + x * cellSize;
                    while (ci < crossings.Count && crossings[ci].X < wx)
                    {
                        windingNumber += crossings[ci].Winding;
                        ci++;
                    }
                    insideOut[x + sx * (y + sy * z)] = windingNumber != 0;
                }
            });
        }

        // Moller-Trumbore ray-triangle intersection with a fixed +X direction. Also reports
        // which way the ray passed through the face, for the winding sum: det = e1 . (dir x e2)
        // = -dir . (e1 x e2), and (e1 x e2) is exactly the triangle normal Unity's own
        // RecalculateNormals builds - so it points OUTWARD on a correctly wound mesh.
        // dir . normal < 0 means the ray is going against the outward normal, i.e. ENTERING the
        // solid, and that is det > 0. Falling out of the same det the intersection test already
        // needs means the facing test costs nothing extra.
        private static bool RayIntersectsTriangleX(Vector3 origin, Vector3 a, Vector3 b, Vector3 c, out float hitX, out int winding)
        {
            hitX = 0f;
            winding = 0;
            Vector3 dir = new Vector3(1f, 0f, 0f);
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 h = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, h);
            if (Mathf.Abs(det) < 1e-9f) return false;

            float invDet = 1f / det;
            Vector3 s = origin - a;
            float u = Vector3.Dot(s, h) * invDet;
            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(dir, q) * invDet;
            if (v < 0f || u + v > 1f) return false;

            float t = Vector3.Dot(e2, q) * invDet;
            if (t < 0f) return false;
            hitX = origin.x + t;
            winding = det > 0f ? 1 : -1;
            return true;
        }

        // Ericson, "Real-Time Collision Detection" 5.1.5.
        private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float t = d1 / (d1 - d3);
                return a + t * ab;
            }

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float t = d2 / (d2 - d6);
                return a + t * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + t * (c - b);
            }

            float denom = 1f / (va + vb + vc);
            float v = vb * denom;
            float w = vc * denom;
            return a + ab * v + ac * w;
        }
    }
}
