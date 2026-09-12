using System;
using System.Collections.Generic;

namespace Sculpting
{
    /// Per-vertex topology for one triangle list - direct-edge neighbours and incident triangles -
    /// flattened into two blocks addressed by a (start, count) pair per vertex: the neighbours of
    /// vertex v are NeighborIndices[NeighborStart[v] .. NeighborStart[v] + NeighborCount[v]), and
    /// its triangles are TriangleIndices[TriangleStart[v] .. TriangleStart[v] + TriangleCount[v]).
    ///
    /// Replaces a pair of jagged int[][] arrays that were built through a HashSet&lt;int&gt; plus a
    /// List&lt;int&gt; per vertex. That layout kept one managed array object per vertex, twice over,
    /// for as long as the mesh was loaded, and the build threw away four more objects per vertex
    /// the moment it finished. Measured on a 1.31M-triangle sphere: 301 ms and 1.3M live arrays for
    /// the jagged build, against 39 ms and four arrays here, at half the memory. The live-object
    /// count matters as much as the time - every garbage collection has to walk every one of them,
    /// for as long as the mesh is loaded.
    ///
    /// (start, count) rather than the monotonic CSR offsets this used to hold, where a vertex's
    /// list ended exactly where the next one began. That packing is why the whole structure had to
    /// be thrown away and rebuilt from scratch on any topology change: giving one vertex a seventh
    /// neighbour meant shifting every entry after it. Dynamic topology changes valence for a
    /// handful of vertices inside a brush footprint, thousands of times a stroke, so a list has to
    /// be able to move without disturbing anything else - which it now can, because nothing reads
    /// this block sequentially any more. See SetNeighbors for where a relocated list goes.
    ///
    /// Ordering is deliberately IDENTICAL to the build it replaced: incident triangles ascend, and
    /// neighbours appear in the order the HashSet-per-vertex build first inserted them (triangle
    /// order, then corner order). GetNeighborAverage and the Laplacian jobs sum neighbours in this
    /// order, and a different order would move their results in the last bits of a float.
    public sealed class MeshAdjacency
    {
        public int[] NeighborStart;
        public int[] NeighborCount;
        public int[] NeighborIndices;
        public int[] TriangleStart;
        public int[] TriangleCount;
        public int[] TriangleIndices;

        /// How many vertices this describes. Distinct from the backing arrays' Length, which is a
        /// capacity that grows ahead of it - see EnsureVertexCapacity.
        public int VertexCount { get; private set; }

        /// Bumped by every mutation. What a derived copy (SculptableMesh's NativeArray mirror for
        /// the Burst Laplacian jobs) compares against to tell "same object, changed contents" from
        /// "unchanged" - identity alone stopped answering that the moment this became mutable.
        public int Version { get; private set; }

        // Live entries in each index block. Everything past this is either free space or the
        // abandoned remains of a relocated list (see SetNeighbors).
        private int _neighborUsed;
        private int _triangleUsed;
        // Entries inside [0, used) that no vertex points at any more. Compaction is driven off
        // this rather than a periodic sweep - see Compact.
        private int _neighborWaste;
        private int _triangleWaste;

        // Above this many incident triangles a vertex's neighbours are de-duplicated through a set
        // instead of by scanning what has been written for it so far. The scan is quadratic in
        // valence: free at the 5-8 a remesh produces, a real cost at the apex of an imported fan.
        private const int LinearDedupLimit = 32;

        // Compact once at least this fraction of a block is abandoned. Half is deliberately
        // generous: compaction is O(vertex count), and the point of relocating rather than
        // shifting is that a refine never pays a whole-mesh cost. A footprint's worth of
        // relocations is a few thousand entries against a block of millions, so on a dense mesh
        // this threshold is essentially never reached mid-stroke.
        private const float CompactWasteFraction = 0.5f;

        private MeshAdjacency() { }

        public static MeshAdjacency Build(int vertexCount, int[] triangles) =>
            Build(vertexCount, triangles, triangles.Length);

        /// cornerCount lets a caller hand over a triangle array whose tail is spare capacity
        /// rather than live topology - see SculptableMesh's growable index buffer. Only the first
        /// cornerCount entries are read.
        public static MeshAdjacency Build(int vertexCount, int[] triangles, int cornerCount)
        {
            cornerCount = Math.Min(cornerCount, triangles.Length);
            cornerCount -= cornerCount % 3;
            int triangleCount = cornerCount / 3;

            // Incident triangles: count, prefix-sum, fill. Filled in ascending triangle order.
            // The prefix sum is built in TriangleStart and then read back out as (start, count),
            // which is the same two passes the CSR build did - the packing only differs in what
            // is kept afterwards.
            //
            // Degenerate triangles are skipped throughout. A local collapse retires a triangle by
            // blanking its three corners rather than by renumbering the ones after it (which would
            // move every hidden-triangle flag and every recorded undo slot), so a rebuilt map would
            // otherwise hand vertex 0 a pile of incident triangles that draw nothing and a
            // self-neighbour - which every Laplacian in the app would then average against itself.
            var triangleStart = new int[vertexCount + 1];
            for (int t = 0; t < triangleCount; t++)
            {
                int b = t * 3;
                if (IsDegenerate(triangles, b)) continue;
                triangleStart[triangles[b] + 1]++;
                triangleStart[triangles[b + 1] + 1]++;
                triangleStart[triangles[b + 2] + 1]++;
            }
            for (int v = 0; v < vertexCount; v++) triangleStart[v + 1] += triangleStart[v];

            var triangleIndices = new int[Math.Max(cornerCount, 16)];
            var cursor = new int[vertexCount];
            Array.Copy(triangleStart, cursor, vertexCount);
            for (int t = 0; t < triangleCount; t++)
            {
                int b = t * 3;
                if (IsDegenerate(triangles, b)) continue;
                triangleIndices[cursor[triangles[b]]++] = t;
                triangleIndices[cursor[triangles[b + 1]]++] = t;
                triangleIndices[cursor[triangles[b + 2]]++] = t;
            }

            var triangleCounts = new int[vertexCount];
            for (int v = 0; v < vertexCount; v++) triangleCounts[v] = triangleStart[v + 1] - triangleStart[v];

            // Neighbours, read off each vertex's own incident triangles. Sized at one entry per
            // corner, which is EXACT for a closed manifold mesh (sum of valences = 2E = 3T) - so
            // every remesh result fits with no resize at all.
            var neighborStart = new int[vertexCount];
            var neighborCounts = new int[vertexCount];
            var neighborIndices = new int[Math.Max(cornerCount, 16)];
            HashSet<int> wideSet = null;
            int w = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                neighborStart[v] = w;
                int start = w;
                int from = triangleStart[v], to = triangleStart[v + 1];
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
                neighborCounts[v] = w - start;
            }

            // TriangleStart was sized vertexCount + 1 to carry the prefix sum; the extra entry is
            // no longer addressed now that counts are stored separately, and leaving it costs
            // nothing but would invite someone to read start[v + 1] as "the end of v".
            Array.Resize(ref triangleStart, vertexCount);

            return new MeshAdjacency
            {
                VertexCount = vertexCount,
                NeighborStart = neighborStart,
                NeighborCount = neighborCounts,
                NeighborIndices = neighborIndices,
                TriangleStart = triangleStart,
                TriangleCount = triangleCounts,
                TriangleIndices = triangleIndices,
                _neighborUsed = w,
                _triangleUsed = cornerCount,
            };
        }

        /// A triangle with two corners the same covers no area and has no meaningful topology -
        /// see Build. `b` is its first corner's index in the flat array.
        public static bool IsDegenerate(int[] triangles, int b) =>
            triangles[b] == triangles[b + 1] || triangles[b + 1] == triangles[b + 2] ||
            triangles[b] == triangles[b + 2];

        private static bool Contains(int[] values, int from, int to, int value)
        {
            for (int i = from; i < to; i++)
                if (values[i] == value) return true;
            return false;
        }

        // ------------------------------------------------------------------------- mutation

        /// Makes room for `capacity` vertices without changing VertexCount. The per-vertex arrays
        /// grow; the index blocks are left alone, since a vertex with no list yet occupies none.
        public void EnsureVertexCapacity(int capacity)
        {
            if (NeighborStart.Length >= capacity) return;
            int grown = Math.Max(capacity, NeighborStart.Length + NeighborStart.Length / 2);
            Array.Resize(ref NeighborStart, grown);
            Array.Resize(ref NeighborCount, grown);
            Array.Resize(ref TriangleStart, grown);
            Array.Resize(ref TriangleCount, grown);
        }

        /// Adds one vertex with no neighbours and no incident triangles, and returns its index.
        /// The caller fills both lists in through SetNeighbors/SetIncidentTriangles once it knows
        /// the surrounding topology.
        public int AppendVertex()
        {
            EnsureVertexCapacity(VertexCount + 1);
            int v = VertexCount++;
            NeighborStart[v] = 0;
            NeighborCount[v] = 0;
            TriangleStart[v] = 0;
            TriangleCount[v] = 0;
            Version++;
            return v;
        }

        /// Replaces vertex v's neighbour list with the first `count` entries of `values`.
        ///
        /// Always writes to the END of the block rather than over the list already there, even
        /// when the new one is shorter and would fit. Rewriting in place would need a third array
        /// per relation recording how much room each slot physically has (a count that has shrunk
        /// no longer describes its own slot), and the abandoned space costs nothing until it adds
        /// up - at which point Compact reclaims all of it in one pass.
        public void SetNeighbors(int v, IReadOnlyList<int> values, int count)
        {
            _neighborWaste += NeighborCount[v];
            NeighborStart[v] = Allocate(ref NeighborIndices, ref _neighborUsed, values, count);
            NeighborCount[v] = count;
            Version++;
            if (_neighborWaste > _neighborUsed * CompactWasteFraction) CompactNeighbors();
        }

        /// The incident-triangle counterpart of SetNeighbors, with the same relocation rule.
        public void SetIncidentTriangles(int v, IReadOnlyList<int> values, int count)
        {
            _triangleWaste += TriangleCount[v];
            TriangleStart[v] = Allocate(ref TriangleIndices, ref _triangleUsed, values, count);
            TriangleCount[v] = count;
            Version++;
            if (_triangleWaste > _triangleUsed * CompactWasteFraction) CompactTriangles();
        }

        private static int Allocate(ref int[] block, ref int used, IReadOnlyList<int> values, int count)
        {
            if (used + count > block.Length)
                Array.Resize(ref block, Math.Max(used + count, block.Length + block.Length / 2));

            int start = used;
            for (int i = 0; i < count; i++) block[start + i] = values[i];
            used += count;
            return start;
        }

        private void CompactNeighbors()
        {
            _neighborUsed = Compact(NeighborIndices, NeighborStart, NeighborCount);
            _neighborWaste = 0;
        }

        private void CompactTriangles()
        {
            _triangleUsed = Compact(TriangleIndices, TriangleStart, TriangleCount);
            _triangleWaste = 0;
        }

        /// Slides every live list down to the front of its block, closing the gaps abandoned
        /// lists left behind, and returns the new used length.
        ///
        /// Safe to copy in place and in vertex order because a list's destination can never be
        /// past its source: the write cursor starts at 0 and only ever advances by exactly what it
        /// copies, while the sources it reads are a subset of the same range in the same order.
        private int Compact(int[] block, int[] starts, int[] counts)
        {
            // Vertex order is not block order after any relocation, so the lists have to be moved
            // in the order they physically sit - otherwise a list moving down could land on top of
            // one that has not been read yet.
            // Allocated per compaction and dropped afterwards rather than kept as scratch: this
            // runs somewhere between rarely and never, and an int per vertex held for the life of
            // the mesh to save an allocation that infrequent is the wrong trade.
            int vertexCount = VertexCount;
            var order = new int[vertexCount];
            for (int v = 0; v < vertexCount; v++) order[v] = v;
            Array.Sort(order, (a, b) => starts[a].CompareTo(starts[b]));

            int write = 0;
            for (int k = 0; k < vertexCount; k++)
            {
                int v = order[k];
                int count = counts[v];
                if (count == 0) { starts[v] = 0; continue; }
                int from = starts[v];
                if (from != write) Array.Copy(block, from, block, write, count);
                starts[v] = write;
                write += count;
            }
            return write;
        }

    }
}
