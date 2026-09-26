using System;
using UnityEngine;

namespace Sculpting
{
    /// Makes a triangle mesh safe for the quad remesher without moving any surface: the first
    /// stage, before LongEdgeSplitter.
    ///
    /// The app's own voxel-remeshed sculpts are clean, but imported OBJ/STL and meshes from other
    /// tools are not - measured on real files: a 3DCoat voxel sculpt with 90 pieces, 1,702 open
    /// edges, 2 non-manifold edges and 71 pinched vertices; a downloaded model with 12
    /// non-manifold edges and 6 degenerate faces. Every later stage assumes a manifold graph,
    /// and a degenerate face alone was enough to make the edge splitter change the topology.
    ///
    /// Repairs are local cuts, never a voxel pass (which would fuse nearby surfaces - see
    /// LongEdgeSplitter):
    ///   1. Faces with a repeated corner, and repeated copies of the same face, are removed.
    ///   2. Every vertex whose faces form more than one fan is split, one copy per fan. Fans are
    ///      joined only across proper manifold edges (two faces, opposite directions), so a
    ///      non-manifold or misoriented edge is cut open along with its vertices: the result
    ///      has open edges there instead, which the remesher handles as boundaries.
    ///   3. Pieces with less area than `minComponentArea` are dropped - debris smaller than about
    ///      a quad cannot be quad-meshed, and floating voxel crumbs are common.
    ///   4. Vertices no face uses are removed and the rest renumbered (order preserved).
    internal static class MeshSanitizer
    {
        internal struct Stats
        {
            public int DegenerateTrianglesRemoved;
            public int DuplicateTrianglesRemoved;
            public int VerticesSplit;          // extra vertex copies created by fan splitting
            public int ComponentsRemoved;
            public int TrianglesInRemovedComponents;
            public int UnusedVerticesRemoved;
            public int ComponentsKept;

            public bool ChangedTopology => DegenerateTrianglesRemoved + DuplicateTrianglesRemoved + VerticesSplit
                                           + ComponentsRemoved > 0;
        }

        public static void Sanitize(Vector3[] vertices, int[] triangles, double minComponentArea,
                                    out Vector3[] outVertices, out int[] outTriangles, out Stats stats)
        {
            stats = default;
            int[] t = RemoveBadTriangles(triangles, ref stats);
            if (ReferenceEquals(t, triangles)) t = (int[])triangles.Clone(); // SplitFans rewrites in place
            Vector3[] v = SplitFans(vertices, t, ref stats);
            t = RemoveSmallComponents(v, t, minComponentArea, ref stats);
            Compact(v, t, out outVertices, out outTriangles, ref stats);
        }

        private static int[] RemoveBadTriangles(int[] triangles, ref Stats stats)
        {
            int triCount = triangles.Length / 3;
            var keys = new long[triCount];
            var order = new int[triCount];
            var keep = new bool[triCount];
            int live = 0;

            for (int f = 0; f < triCount; f++)
            {
                int a = triangles[f * 3], b = triangles[f * 3 + 1], c = triangles[f * 3 + 2];
                if (a == b || b == c || c == a) { stats.DegenerateTrianglesRemoved++; continue; }
                keep[f] = true;
                // Sorted corners packed 21 bits each would overflow past 2M vertices; hash the
                // sorted triple instead and confirm candidates exactly below.
                Sort3(ref a, ref b, ref c);
                keys[live] = (long)(((ulong)(uint)a * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)b * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(uint)c * 0x165667B19E3779F9UL));
                order[live++] = f;
            }

            Array.Sort(keys, order, 0, live);
            for (int i = 0; i < live;)
            {
                int j = i + 1;
                while (j < live && keys[j] == keys[i]) j++;
                // Faces i..j-1 share a hash; drop any that repeats an earlier one's corner set.
                for (int x = i + 1; x < j; x++)
                    for (int y = i; y < x; y++)
                        if (keep[order[y]] && SameCorners(triangles, order[x], order[y]))
                        {
                            keep[order[x]] = false;
                            stats.DuplicateTrianglesRemoved++;
                            break;
                        }
                i = j;
            }

            int kept = 0;
            for (int f = 0; f < triCount; f++) if (keep[f]) kept++;
            if (kept == triCount) return triangles;
            var result = new int[kept * 3];
            int w = 0;
            for (int f = 0; f < triCount; f++)
            {
                if (!keep[f]) continue;
                result[w++] = triangles[f * 3];
                result[w++] = triangles[f * 3 + 1];
                result[w++] = triangles[f * 3 + 2];
            }
            return result;
        }

        /// Rewrites `t` in place so every vertex has a single fan; returns the (possibly grown)
        /// vertex array.
        private static Vector3[] SplitFans(Vector3[] vertices, int[] t, ref Stats stats)
        {
            int vCount = vertices.Length;
            PolyMeshEdges edges = PolyMeshEdges.Build(PolyMesh.FromTriangles(vertices, t));

            var cornerStart = new int[vCount + 1];
            for (int c = 0; c < t.Length; c++) cornerStart[t[c] + 1]++;
            for (int i = 0; i < vCount; i++) cornerStart[i + 1] += cornerStart[i];
            var fill = (int[])cornerStart.Clone();
            var corners = new int[t.Length];
            for (int c = 0; c < t.Length; c++) corners[fill[t[c]]++] = c;

            int[] parent = new int[16];
            int[] sideEdge = new int[32];
            int[] sideOwner = new int[32];
            int[] rootCopy = new int[16];
            Vector3[] v = vertices;
            int next = vCount;

            for (int vert = 0; vert < vCount; vert++)
            {
                int cs = cornerStart[vert], n = cornerStart[vert + 1] - cs;
                if (n <= 1) continue;
                if (n > parent.Length)
                {
                    parent = new int[n * 2];
                    rootCopy = new int[n * 2];
                    sideEdge = new int[n * 4];
                    sideOwner = new int[n * 4];
                }

                int sides = 0;
                for (int i = 0; i < n; i++)
                {
                    int c = corners[cs + i];
                    parent[i] = i;
                    int f = c / 3;
                    int prev = f * 3 + (c - f * 3 + 2) % 3;
                    sideEdge[sides] = edges.SideEdge[c]; sideOwner[sides++] = i;
                    sideEdge[sides] = edges.SideEdge[prev]; sideOwner[sides++] = i;
                }

                int fans = n;
                for (int i = 0; i < sides; i++)
                {
                    int e = sideEdge[i];
                    if (e < 0 || edges.UseCount[e] != 2 || edges.ForwardCount[e] != 1) continue;
                    for (int j = i + 1; j < sides; j++)
                    {
                        if (sideEdge[j] != e) continue;
                        int ra = Find(parent, sideOwner[i]), rb = Find(parent, sideOwner[j]);
                        if (ra != rb) { parent[ra] = rb; fans--; }
                    }
                }
                if (fans == 1) continue;

                // First fan (by corner order) keeps the vertex; each other fan gets a copy.
                for (int i = 0; i < n; i++) rootCopy[i] = -2;
                for (int i = 0; i < n; i++)
                {
                    int root = Find(parent, i);
                    if (rootCopy[root] == -2)
                    {
                        if (i == 0 || Find(parent, 0) == root) rootCopy[root] = vert;
                        else
                        {
                            if (next == v.Length) Array.Resize(ref v, Math.Max(v.Length + 16, v.Length + v.Length / 64));
                            v[next] = vertices[vert];
                            rootCopy[root] = next++;
                            stats.VerticesSplit++;
                        }
                    }
                    t[corners[cs + i]] = rootCopy[root];
                }
            }

            if (next != v.Length) Array.Resize(ref v, next);
            return v;
        }

        private static int[] RemoveSmallComponents(Vector3[] v, int[] t, double minArea, ref Stats stats)
        {
            int triCount = t.Length / 3;
            var parent = new int[v.Length];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            for (int f = 0; f < triCount; f++)
            {
                Union(parent, t[f * 3], t[f * 3 + 1]);
                Union(parent, t[f * 3], t[f * 3 + 2]);
            }

            var area = new double[v.Length];
            for (int f = 0; f < triCount; f++)
            {
                Vector3 a = v[t[f * 3]];
                area[Find(parent, t[f * 3])] += 0.5 * Vector3.Cross(v[t[f * 3 + 1]] - a, v[t[f * 3 + 2]] - a).magnitude;
            }

            var hasFace = new bool[v.Length];
            for (int f = 0; f < triCount; f++) hasFace[Find(parent, t[f * 3])] = true;
            for (int r = 0; r < v.Length; r++)
            {
                if (!hasFace[r] || parent[r] != r) continue;
                if (area[r] < minArea) stats.ComponentsRemoved++;
                else stats.ComponentsKept++;
            }
            if (stats.ComponentsRemoved == 0) return t;

            int kept = 0;
            for (int f = 0; f < triCount; f++) if (area[Find(parent, t[f * 3])] >= minArea) kept++;
            stats.TrianglesInRemovedComponents = triCount - kept;
            var result = new int[kept * 3];
            int w = 0;
            for (int f = 0; f < triCount; f++)
            {
                if (area[Find(parent, t[f * 3])] < minArea) continue;
                result[w++] = t[f * 3];
                result[w++] = t[f * 3 + 1];
                result[w++] = t[f * 3 + 2];
            }
            return result;
        }

        private static void Compact(Vector3[] v, int[] t, out Vector3[] outV, out int[] outT, ref Stats stats)
        {
            var map = new int[v.Length];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            foreach (int idx in t) map[idx] = 0;
            int count = 0;
            for (int i = 0; i < map.Length; i++) if (map[i] == 0) map[i] = count++;
            stats.UnusedVerticesRemoved = v.Length - count;
            if (count == v.Length) { outV = v; outT = t; return; }

            outV = new Vector3[count];
            for (int i = 0; i < map.Length; i++) if (map[i] >= 0) outV[map[i]] = v[i];
            outT = new int[t.Length];
            for (int i = 0; i < t.Length; i++) outT[i] = map[t[i]];
        }

        private static bool SameCorners(int[] t, int f, int g)
        {
            int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
            int x = t[g * 3], y = t[g * 3 + 1], z = t[g * 3 + 2];
            Sort3(ref a, ref b, ref c);
            Sort3(ref x, ref y, ref z);
            return a == x && b == y && c == z;
        }

        private static void Sort3(ref int a, ref int b, ref int c)
        {
            if (a > b) (a, b) = (b, a);
            if (b > c) (b, c) = (c, b);
            if (a > b) (a, b) = (b, a);
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }
    }
}
