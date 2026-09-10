using System;
using System.Collections.Generic;

namespace Sculpting
{
    /// Per-vertex topology for one triangle list - direct-edge neighbours and incident triangles -
    /// flattened into CSR (compressed sparse row) arrays: the neighbours of vertex v are
    /// NeighborIndices[NeighborOffsets[v] .. NeighborOffsets[v + 1]), and its triangles are
    /// TriangleIndices[TriangleOffsets[v] .. TriangleOffsets[v + 1]).
    ///
    /// Replaces a pair of jagged int[][] arrays that were built through a HashSet&lt;int&gt; plus a
    /// List&lt;int&gt; per vertex. That layout kept one managed array object per vertex, twice over,
    /// for as long as the mesh was loaded, and the build threw away four more objects per vertex
    /// the moment it finished. Measured on a 1.31M-triangle sphere: 301 ms and 1.3M live arrays for
    /// the jagged build, against 39 ms and four arrays here, at half the memory. The live-object
    /// count matters as much as the time - every garbage collection has to walk every one of them,
    /// for as long as the mesh is loaded.
    ///
    /// Ordering is deliberately IDENTICAL to the build it replaced: incident triangles ascend, and
    /// neighbours appear in the order the HashSet-per-vertex build first inserted them (triangle
    /// order, then corner order). GetNeighborAverage and the Laplacian jobs sum neighbours in this
    /// order, and a different order would move their results in the last bits of a float.
    public sealed class MeshAdjacency
    {
        public readonly int[] NeighborOffsets;
        public readonly int[] NeighborIndices;
        public readonly int[] TriangleOffsets;
        public readonly int[] TriangleIndices;

        public int VertexCount => NeighborOffsets.Length - 1;

        // Above this many incident triangles a vertex's neighbours are de-duplicated through a set
        // instead of by scanning what has been written for it so far. The scan is quadratic in
        // valence: free at the 5-8 a remesh produces, a real cost at the apex of an imported fan.
        private const int LinearDedupLimit = 32;

        private MeshAdjacency(int[] neighborOffsets, int[] neighborIndices, int[] triangleOffsets, int[] triangleIndices)
        {
            NeighborOffsets = neighborOffsets;
            NeighborIndices = neighborIndices;
            TriangleOffsets = triangleOffsets;
            TriangleIndices = triangleIndices;
        }

        public static MeshAdjacency Build(int vertexCount, int[] triangles)
        {
            int cornerCount = triangles.Length - triangles.Length % 3;
            int triangleCount = cornerCount / 3;

            // Incident triangles: count, prefix-sum, fill. Filled in ascending triangle order.
            var triangleOffsets = new int[vertexCount + 1];
            for (int k = 0; k < cornerCount; k++) triangleOffsets[triangles[k] + 1]++;
            for (int v = 0; v < vertexCount; v++) triangleOffsets[v + 1] += triangleOffsets[v];

            var triangleIndices = new int[cornerCount];
            var cursor = new int[vertexCount];
            Array.Copy(triangleOffsets, cursor, vertexCount);
            for (int t = 0; t < triangleCount; t++)
            {
                int b = t * 3;
                triangleIndices[cursor[triangles[b]]++] = t;
                triangleIndices[cursor[triangles[b + 1]]++] = t;
                triangleIndices[cursor[triangles[b + 2]]++] = t;
            }

            // Neighbours, read off each vertex's own incident triangles. Sized at one entry per
            // corner, which is EXACT for a closed manifold mesh (sum of valences = 2E = 3T) - so
            // every remesh result fits with no resize at all.
            var neighborOffsets = new int[vertexCount + 1];
            var neighborIndices = new int[Math.Max(cornerCount, 16)];
            HashSet<int> wideSet = null;
            int w = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                neighborOffsets[v] = w;
                int start = w;
                int from = triangleOffsets[v], to = triangleOffsets[v + 1];
                bool wide = to - from > LinearDedupLimit;
                if (wide)
                {
                    if (wideSet == null) wideSet = new HashSet<int>();
                    else wideSet.Clear();
                }

                for (int k = from; k < to; k++)
                {
                    int b = triangleIndices[k] * 3;
                    int c0 = triangles[b], c1 = triangles[b + 1], c2 = triangles[b + 2];
                    // The two corners the old build added for v from this triangle, in its order.
                    int p, q;
                    if (c0 == v) { p = c1; q = c2; }
                    else if (c1 == v) { p = c0; q = c2; }
                    else { p = c0; q = c1; }

                    if (w + 2 > neighborIndices.Length) Array.Resize(ref neighborIndices, neighborIndices.Length * 2);
                    if (wide)
                    {
                        if (wideSet.Add(p)) neighborIndices[w++] = p;
                        if (wideSet.Add(q)) neighborIndices[w++] = q;
                    }
                    else
                    {
                        if (!Contains(neighborIndices, start, w, p)) neighborIndices[w++] = p;
                        if (!Contains(neighborIndices, start, w, q)) neighborIndices[w++] = q;
                    }
                }
            }
            neighborOffsets[vertexCount] = w;
            if (neighborIndices.Length != w) Array.Resize(ref neighborIndices, w);

            return new MeshAdjacency(neighborOffsets, neighborIndices, triangleOffsets, triangleIndices);
        }

        private static bool Contains(int[] values, int from, int to, int value)
        {
            for (int i = from; i < to; i++)
                if (values[i] == value) return true;
            return false;
        }
    }
}
