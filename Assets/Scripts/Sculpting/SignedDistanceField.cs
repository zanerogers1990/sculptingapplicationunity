using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Sculpting
{
    /// Accelerated signed distance queries against a static triangle soup, used by
    /// MeshRemesher to sample its voxel grid. Distance magnitude comes from the nearest
    /// triangle; sign comes from a winding-number ray cast, which (unlike a nearest-face-normal
    /// heuristic) stays correct across concave folds and overhangs that sculpting produces.
    internal class SignedDistanceField
    {
        private readonly Vector3[] vertices;
        private readonly int[] triangles;
        private readonly Bounds bounds;
        private readonly float cellSize;
        private readonly Vector3Int binDims;
        private readonly List<int>[] bins;

        public SignedDistanceField(Vector3[] verts, int[] tris, float cellSize)
        {
            vertices = verts;
            triangles = tris;
            this.cellSize = Mathf.Max(cellSize, 0.0001f);

            int triCount = tris.Length / 3;

            Vector3 min = verts[0], max = verts[0];
            for (int i = 1; i < verts.Length; i++)
            {
                min = Vector3.Min(min, verts[i]);
                max = Vector3.Max(max, verts[i]);
            }
            min -= Vector3.one * this.cellSize;
            max += Vector3.one * this.cellSize;
            bounds = new Bounds();
            bounds.SetMinMax(min, max);

            binDims = new Vector3Int(
                Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / this.cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(bounds.size.y / this.cellSize)),
                Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / this.cellSize)));

            bins = new List<int>[binDims.x * binDims.y * binDims.z];

            for (int t = 0; t < triCount; t++)
            {
                Vector3 a = verts[tris[t * 3]];
                Vector3 b = verts[tris[t * 3 + 1]];
                Vector3 c = verts[tris[t * 3 + 2]];
                Vector3Int bmin = CellOf(Vector3.Min(a, Vector3.Min(b, c)));
                Vector3Int bmax = CellOf(Vector3.Max(a, Vector3.Max(b, c)));

                for (int z = bmin.z; z <= bmax.z; z++)
                for (int y = bmin.y; y <= bmax.y; y++)
                for (int x = bmin.x; x <= bmax.x; x++)
                {
                    int idx = BinIndex(x, y, z);
                    if (bins[idx] == null) bins[idx] = new List<int>();
                    bins[idx].Add(t);
                }
            }
        }

        private Vector3Int CellOf(Vector3 p)
        {
            Vector3 local = p - bounds.min;
            int x = Mathf.Clamp(Mathf.FloorToInt(local.x / cellSize), 0, binDims.x - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(local.y / cellSize), 0, binDims.y - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt(local.z / cellSize), 0, binDims.z - 1);
            return new Vector3Int(x, y, z);
        }

        private int BinIndex(int x, int y, int z) => x + binDims.x * (y + binDims.y * z);

        // ComputeColumn runs once per (y,z) column, concurrently across Parallel.For
        // iterations in ComputeInsideMask - a resolution^2-scaling number of calls per remesh.
        // Thread-local + Clear() instead of a fresh HashSet/List per call avoids that many
        // allocations while staying safe under concurrent access (a single shared buffer
        // would not be).
        private static readonly ThreadLocal<HashSet<int>> _testedPool = new ThreadLocal<HashSet<int>>(() => new HashSet<int>());
        private static readonly ThreadLocal<List<Crossing>> _crossingsPool = new ThreadLocal<List<Crossing>>(() => new List<Crossing>());

        /// One ray/surface crossing along a column's +X ray: where it happened, and whether the
        /// ray was entering (+1) or leaving (-1) the surface there. Implements IComparable so
        /// List.Sort() orders by X through the default comparer - no Comparison delegate, since
        /// this sorts once per (y,z) column and that is a resolution^2-scaling call count.
        private readonly struct Crossing : IComparable<Crossing>
        {
            public readonly float X;
            public readonly int Winding;
            public Crossing(float x, int winding) { X = x; Winding = winding; }
            public int CompareTo(Crossing other) => X.CompareTo(other.X);
        }

        public float NearestUnsignedDistance(Vector3 p)
        {
            Vector3Int center = CellOf(p);
            int maxRadius = Mathf.Max(binDims.x, binDims.y, binDims.z);

            float bestSqrDist = float.MaxValue;
            int foundAtRadius = -1;

            for (int radius = 0; radius <= maxRadius; radius++)
            {
                ForEachCellInShell(center, radius, binDims, (x, y, z) =>
                {
                    var list = bins[BinIndex(x, y, z)];
                    if (list == null) return;
                    for (int k = 0; k < list.Count; k++)
                    {
                        int t = list[k];
                        Vector3 a = vertices[triangles[t * 3]];
                        Vector3 b = vertices[triangles[t * 3 + 1]];
                        Vector3 c = vertices[triangles[t * 3 + 2]];
                        Vector3 closest = ClosestPointOnTriangle(p, a, b, c);
                        float sqr = (closest - p).sqrMagnitude;
                        if (sqr < bestSqrDist) bestSqrDist = sqr;
                    }
                });

                if (bestSqrDist < float.MaxValue && foundAtRadius < 0) foundAtRadius = radius;
                // Keep searching a couple of shells past the first hit - the true nearest
                // triangle can sit in a farther bin than the one containing its closest point.
                if (foundAtRadius >= 0 && radius >= foundAtRadius + 2) break;
            }

            return bestSqrDist < float.MaxValue ? Mathf.Sqrt(bestSqrDist) : float.MaxValue;
        }

        // Fills insideOut (flattened x + sx*(y + sy*z), matching MeshRemesher's sdf layout)
        // with the inside/outside sign for every sample on the grid. Casts one +X ray per
        // (y,z) column instead of one per sample point: the crossings along that column are
        // shared by every sample on it, so a single sorted sweep gives every sample's winding
        // in one pass instead of a full column walk per sample. Columns are independent, so
        // they run in parallel across cores.
        public void ComputeInsideMask(Vector3 origin, float cellSize, int sx, int sy, int sz, bool[] insideOut)
        {
            System.Threading.Tasks.Parallel.For(0, sy * sz, columnIndex =>
            {
                int y = columnIndex % sy;
                int z = columnIndex / sy;
                ComputeColumn(origin, cellSize, sx, sy, y, z, insideOut);
            });
        }

        private void ComputeColumn(Vector3 origin, float cellSize, int sx, int sy, int y, int z, bool[] insideOut)
        {
            // Nudge off the sample line so an exactly grid-aligned mesh (e.g. a fresh
            // primitive) doesn't graze triangle edges/vertices along the whole column.
            float wy = origin.y + y * cellSize + cellSize * 0.0173f;
            float wz = origin.z + z * cellSize + cellSize * 0.0091f;
            // Starts the ray at whichever is further out: the sample grid's own -X edge, or
            // this mesh's -X bound. They are the same thing when the grid was built around this
            // mesh (Remesh), but NOT when a caller samples one mesh on another's grid - a
            // boolean cutter can extend past the target's bounds (MeshBoolean), and a ray that
            // starts INSIDE the cutter counts none of the crossings behind it, inverting the
            // whole column's inside/outside. The sample loop below already consumes every
            // crossing before the first sample's x, so the extra span costs nothing but the
            // crossings it correctly picks up.
            Vector3 rayOrigin = new Vector3(Mathf.Min(origin.x, bounds.min.x), wy, wz);

            Vector3Int cell = CellOf(rayOrigin);
            var tested = _testedPool.Value;
            tested.Clear();
            var crossings = _crossingsPool.Value;
            crossings.Clear();

            for (int bx = 0; bx < binDims.x; bx++)
            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            {
                int by = cell.y + dy, bz = cell.z + dz;
                if (by < 0 || by >= binDims.y || bz < 0 || bz >= binDims.z) continue;

                var list = bins[BinIndex(bx, by, bz)];
                if (list == null) continue;
                for (int k = 0; k < list.Count; k++)
                {
                    int t = list[k];
                    if (!tested.Add(t)) continue;

                    Vector3 a = vertices[triangles[t * 3]];
                    Vector3 b = vertices[triangles[t * 3 + 1]];
                    Vector3 c = vertices[triangles[t * 3 + 2]];
                    if (RayIntersectsTriangleX(rayOrigin, a, b, c, out float hitX, out int winding))
                        crossings.Add(new Crossing(hitX, winding));
                }
            }

            crossings.Sort();

            // Accumulates a WINDING NUMBER rather than flipping an even-odd parity bit.
            // Parity is only correct for a single closed manifold: MeshJoiner concatenates two
            // (or more) closed shells without welding them (by design - it's Merge Down, not a
            // boolean), so a ray through the region where those shells OVERLAP crosses four
            // surfaces and parity reports "outside" there. That carved a hollow out of exactly
            // the intersection volume - the visible breakage when remeshing joined objects.
            // Parity computes the shells' XOR; summing signed crossings computes their union.
            // Same fix also covers a SINGLE mesh sculpted until it self-intersects (a fold
            // pushed through its own surface), which parity broke identically.
            //
            // Tested against `!= 0` rather than `> 0` deliberately: a shell whose winding runs
            // opposite the rest (a mirrored/negatively-scaled object baked in by Join, say)
            // still reads as solid instead of vanishing. The tradeoff is that inverted winding
            // can't be used to express a boolean SUBTRACTION - if boolean ops land later, that
            // wants its own explicit per-shell sign, not an accident of triangle order here.
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
        }

        // Moller-Trumbore ray-triangle intersection with a fixed +X direction. Also reports
        // which way the ray passed through the face, for ComputeColumn's winding sum:
        // det = e1 . (dir x e2) = -dir . (e1 x e2), and (e1 x e2) is exactly the triangle
        // normal Unity's own RecalculateNormals builds - so it points OUTWARD on a correctly
        // wound mesh. dir . normal < 0 means the ray is going against the outward normal, i.e.
        // ENTERING the solid, and that is det > 0. Falling out of the same det the intersection
        // test already needs means the facing test costs nothing extra.
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

        private delegate void CellVisitor(int x, int y, int z);

        private static void ForEachCellInShell(Vector3Int center, int radius, Vector3Int dims, CellVisitor visit)
        {
            int x0 = center.x - radius, x1 = center.x + radius;
            int y0 = center.y - radius, y1 = center.y + radius;
            int z0 = center.z - radius, z1 = center.z + radius;

            for (int z = z0; z <= z1; z++)
            {
                if (z < 0 || z >= dims.z) continue;
                for (int y = y0; y <= y1; y++)
                {
                    if (y < 0 || y >= dims.y) continue;
                    for (int x = x0; x <= x1; x++)
                    {
                        if (x < 0 || x >= dims.x) continue;
                        bool onShell = x == x0 || x == x1 || y == y0 || y == y1 || z == z0 || z == z1;
                        if (radius > 0 && !onShell) continue;
                        visit(x, y, z);
                    }
                }
            }
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
