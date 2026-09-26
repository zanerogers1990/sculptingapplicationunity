using System;
using UnityEngine;

namespace Sculpting
{
    /// Splits every triangle edge longer than a limit, until none is. The quad remesher's input
    /// conditioning step.
    ///
    /// The field solve (Instant Meshes style) treats mesh edges as the graph it smooths over and
    /// extracts from, so an edge longer than about half a quad spans lattice cells it never
    /// samples, and extraction there reads garbage. Sculpting makes exactly those edges: a Move
    /// or Snake Hook drag stretches the triangles on its flanks into long needles while the rest
    /// of the model stays dense.
    ///
    /// Deliberately split-only, never collapse and never voxelise:
    ///   - The result is the SAME surface, bit for bit. Every new vertex is an edge midpoint on
    ///     a flat triangle, so it lies exactly on the input; volume, silhouette and detail are
    ///     untouched and the final reprojection has nothing to recover.
    ///   - Topology is untouched too. A voxel pass at quad scale fuses anything closer than a
    ///     voxel - fingers, lips, an ear against the head - which is the one failure a quad
    ///     remesher must never have. Instant Meshes conditions its input the same way.
    ///   - Density needs no evening out here: the field hierarchy merges dense regions level by
    ///     level, so only the too-LONG edges are a problem.
    ///
    /// Each round is red-green refinement: every over-long edge gets a midpoint, and each
    /// triangle is replaced by the 2, 3 or 4 triangles its marked sides call for. Both
    /// triangles on an edge read the same mark from one edge table, so the result is always
    /// conforming (no T-junctions), and a round halves every over-long edge - a 20x stretch
    /// clears in about five rounds.
    internal static class LongEdgeSplitter
    {
        internal struct Stats
        {
            public int Rounds;
            public int EdgesSplit;
            public int InputVertices, OutputVertices;
            public int InputTriangles, OutputTriangles;
            public bool HitVertexCap;
        }

        /// The edge limit Instant Meshes uses: half the target quad edge, but never more than
        /// twice the mesh's mean edge, so stretched regions are also brought down toward the
        /// density of the rest of the model rather than only to the lattice minimum.
        public static float DefaultMaxEdge(float quadEdge, double meanEdgeLength) =>
            (float)Math.Min(quadEdge * 0.5, meanEdgeLength * 2.0);

        public static double MeanEdgeLength(Vector3[] vertices, int[] triangles)
        {
            PolyMeshEdges edges = PolyMeshEdges.Build(PolyMesh.FromTriangles(vertices, triangles));
            if (edges.EdgeCount == 0) return 0;
            double sum = 0;
            for (int e = 0; e < edges.EdgeCount; e++)
                sum += (vertices[edges.EdgeB[e]] - vertices[edges.EdgeA[e]]).magnitude;
            return sum / edges.EdgeCount;
        }

        /// Returns the refined mesh; the inputs are not modified. When no edge exceeds the limit
        /// the input arrays themselves are returned. `maxVertices` bounds the growth (a limit
        /// far below the mesh's own scale would otherwise explode); refinement stops before the
        /// round that would cross it, and Stats.HitVertexCap says so.
        public static void Split(Vector3[] vertices, int[] triangles, float maxEdge, int maxVertices,
                                 out Vector3[] outVertices, out int[] outTriangles, out Stats stats)
        {
            stats = new Stats
            {
                InputVertices = vertices.Length,
                InputTriangles = triangles.Length / 3,
            };
            float maxSq = maxEdge * maxEdge;
            Vector3[] v = vertices;
            int[] t = triangles;

            for (int round = 0; round < 32; round++)
            {
                PolyMeshEdges edges = PolyMeshEdges.Build(PolyMesh.FromTriangles(v, t));

                // Midpoint per over-long edge (index into the grown vertex array), else -1.
                var mid = new int[edges.EdgeCount];
                int marked = 0;
                for (int e = 0; e < edges.EdgeCount; e++)
                {
                    bool longEdge = (v[edges.EdgeB[e]] - v[edges.EdgeA[e]]).sqrMagnitude > maxSq;
                    mid[e] = longEdge ? v.Length + marked++ : -1;
                }
                if (marked == 0) break;
                if (v.Length + marked > maxVertices) { stats.HitVertexCap = true; break; }

                var nv = new Vector3[v.Length + marked];
                Array.Copy(v, nv, v.Length);
                for (int e = 0; e < edges.EdgeCount; e++)
                    if (mid[e] >= 0)
                    {
                        // Written as a + (b - a) * 0.5 from the LOWER index: the same edge
                        // always yields the same bits whichever triangle asks.
                        Vector3 a = v[edges.EdgeA[e]], b = v[edges.EdgeB[e]];
                        nv[mid[e]] = new Vector3(a.x + (b.x - a.x) * 0.5f, a.y + (b.y - a.y) * 0.5f, a.z + (b.z - a.z) * 0.5f);
                    }

                int triCount = t.Length / 3;
                int outTris = 0;
                for (int f = 0; f < triCount; f++)
                {
                    int m = 0;
                    for (int k = 0; k < 3; k++) if (edges.SideEdge[f * 3 + k] >= 0 && mid[edges.SideEdge[f * 3 + k]] >= 0) m++;
                    outTris += m + 1;
                }

                var nt = new int[outTris * 3];
                int w = 0;
                for (int f = 0; f < triCount; f++)
                {
                    int s = f * 3;
                    int m0 = MidOf(edges, mid, s), m1 = MidOf(edges, mid, s + 1), m2 = MidOf(edges, mid, s + 2);
                    int count = (m0 >= 0 ? 1 : 0) + (m1 >= 0 ? 1 : 0) + (m2 >= 0 ? 1 : 0);

                    if (count == 0)
                    {
                        Put(nt, ref w, t[s], t[s + 1], t[s + 2]);
                        continue;
                    }

                    if (count == 3)
                    {
                        int a = t[s], b = t[s + 1], c = t[s + 2];
                        Put(nt, ref w, a, m0, m2);
                        Put(nt, ref w, m0, b, m1);
                        Put(nt, ref w, m2, m1, c);
                        Put(nt, ref w, m0, m1, m2);
                        continue;
                    }

                    // Rotate so the marked sides come first: side k runs corner k -> k + 1.
                    int r = count == 1
                        ? (m0 >= 0 ? 0 : m1 >= 0 ? 1 : 2)          // the one marked side
                        : (m0 < 0 ? 1 : m1 < 0 ? 2 : 0);           // the side after the unmarked one
                    int ca = t[s + r], cb = t[s + (r + 1) % 3], cc = t[s + (r + 2) % 3];
                    int ma = MidOf(edges, mid, s + r), mb = MidOf(edges, mid, s + (r + 1) % 3);

                    if (count == 1)
                    {
                        Put(nt, ref w, ca, ma, cc);
                        Put(nt, ref w, ma, cb, cc);
                    }
                    else
                    {
                        // Sides a-b and b-c split: corner triangle at b, then the quad
                        // (a, ma, mb, c) along its shorter diagonal.
                        Put(nt, ref w, ma, cb, mb);
                        if ((nv[ca] - nv[mb]).sqrMagnitude <= (nv[ma] - nv[cc]).sqrMagnitude)
                        {
                            Put(nt, ref w, ca, ma, mb);
                            Put(nt, ref w, ca, mb, cc);
                        }
                        else
                        {
                            Put(nt, ref w, ca, ma, cc);
                            Put(nt, ref w, ma, mb, cc);
                        }
                    }
                }

                stats.Rounds++;
                stats.EdgesSplit += marked;
                v = nv;
                t = nt;
            }

            outVertices = v;
            outTriangles = t;
            stats.OutputVertices = v.Length;
            stats.OutputTriangles = t.Length / 3;
        }

        private static int MidOf(PolyMeshEdges edges, int[] mid, int side)
        {
            int e = edges.SideEdge[side];
            return e >= 0 ? mid[e] : -1;
        }

        private static void Put(int[] t, ref int w, int a, int b, int c)
        {
            t[w++] = a;
            t[w++] = b;
            t[w++] = c;
        }
    }
}
