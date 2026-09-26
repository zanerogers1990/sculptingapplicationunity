using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Sculpting
{
    /// Turns solved orientation + position fields into a quad-dominant polygon mesh.
    ///
    ///  1. Every edge of the finest graph is classified by the lattice offset between its two
    ///     vertices' lattice points: equal -> the two sample the same output vertex ("collapse");
    ///     one step apart -> they sample neighbouring output vertices (an output edge); anything
    ///     else (diagonals, longer jumps) is ignored.
    ///  2. Collapse edges are unioned into clusters - one per output vertex, positioned at the
    ///     area-weighted mean of its members' lattice points.
    ///  3. Output edges link clusters. Vertices left with fewer than two links are pruned (they
    ///     can't bound a face).
    ///  4. Each cluster's links are ordered by angle around its normal (a rotation system) and
    ///     faces are traced from it. Tracing a rotation system always yields an orientable
    ///     surface in which every link borders exactly two face sides, so the result is manifold
    ///     along edges by construction.
    ///  5. Faces are cleaned: a face whose corner angles sum like the OUTSIDE of a polygon is the
    ///     trace around an open boundary of the input and is dropped (the hole stays a hole);
    ///     faces of five or more sides are split into quads (and one triangle when odd) around
    ///     a centre vertex; a face that revisits a vertex is cut into its simple loops.
    ///
    /// The output positions are the fields' tangent-plane lattice points, not yet on the surface;
    /// QuadRemesher projects and relaxes them afterwards.
    internal static class QuadExtractor
    {
        internal struct Stats
        {
            public int CollapseEdges, OutputEdgeSamples, IgnoredEdges;
            public int Clusters, PrunedVertices, Links;
            public int Faces, Triangles, Quads, SplitPolygons, OuterFacesDropped, BadFacesDropped, SpursDropped;
        }

        private const int MaxFaceSize = 64;

        public static PolyMesh Extract(FieldLevel level, Vector3[] q, Vector3[] o, float scale, out Stats stats)
        {
            stats = default;
            int n = level.Count;
            int[] start = level.AdjacencyStart, adj = level.Adjacency;
            float inv = 1f / scale;

            // 1. Classify each edge once (from its lower endpoint): 0 ignore, 1 collapse, 2 output edge.
            var kind = new byte[adj.Length];
            Parallel.For(0, (n + 4095) / 4096, block =>
            {
                int vs = block * 4096, ve = Math.Min(n, vs + 4096);
                for (int v = vs; v < ve; v++)
                {
                    for (int k = start[v]; k < start[v + 1]; k++)
                    {
                        int u = adj[k];
                        if (u < v) continue;
                        FieldMath.CompatOrientation(q[v], level.Normal[v], q[u], level.Normal[u], out Vector3 q0, out Vector3 q1);
                        FieldMath.CompatPositionIndex(level.Position[v], level.Normal[v], q0, o[v],
                                                      level.Position[u], level.Normal[u], q1, o[u],
                                                      scale, inv, out int i0, out int j0, out int i1, out int j1);
                        int di = Math.Abs(i0 - i1), dj = Math.Abs(j0 - j1);
                        kind[k] = (byte)(di + dj == 0 ? 1 : di + dj == 1 ? 2 : 0);
                    }
                }
            });

            // 2. Clusters.
            var parent = new int[n];
            for (int v = 0; v < n; v++) parent[v] = v;
            for (int v = 0; v < n; v++)
                for (int k = start[v]; k < start[v + 1]; k++)
                {
                    if (adj[k] < v) continue;
                    if (kind[k] == 1) { stats.CollapseEdges++; Union(parent, v, adj[k]); }
                    else if (kind[k] == 2) stats.OutputEdgeSamples++;
                    else stats.IgnoredEdges++;
                }

            var cluster = new int[n];
            int clusterCount = 0;
            for (int v = 0; v < n; v++) if (Find(parent, v) == v) cluster[v] = clusterCount++;
            for (int v = 0; v < n; v++) cluster[v] = cluster[Find(parent, v)];
            stats.Clusters = clusterCount;

            var cPos = new Vector3[clusterCount];
            var cNormal = new Vector3[clusterCount];
            var cWeight = new float[clusterCount];
            for (int v = 0; v < n; v++)
            {
                int c = cluster[v];
                float w = Mathf.Max(level.Area[v], 1e-30f);
                cPos[c] += o[v] * w;
                cNormal[c] += level.Normal[v] * w;
                cWeight[c] += w;
            }
            for (int c = 0; c < clusterCount; c++)
            {
                cPos[c] /= cWeight[c];
                cNormal[c] = VectorMath.NormalizeOr(cNormal[c], Vector3.up);
            }

            // 3. Links between clusters, deduplicated.
            var linkKeys = new List<long>(stats.OutputEdgeSamples);
            for (int v = 0; v < n; v++)
                for (int k = start[v]; k < start[v + 1]; k++)
                {
                    if (adj[k] < v || kind[k] != 2) continue;
                    int a = cluster[v], b = cluster[adj[k]];
                    if (a == b) continue;
                    linkKeys.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a);
                }
            linkKeys.Sort();
            var neighbours = new List<int>[clusterCount];
            for (int c = 0; c < clusterCount; c++) neighbours[c] = new List<int>(4);
            for (int i = 0; i < linkKeys.Count; i++)
            {
                if (i > 0 && linkKeys[i] == linkKeys[i - 1]) continue;
                int a = (int)(linkKeys[i] >> 32), b = (int)(linkKeys[i] & 0xffffffffL);
                neighbours[a].Add(b);
                neighbours[b].Add(a);
            }

            // Prune vertices with fewer than two links, repeatedly (a dangling chain unravels).
            var alive = new bool[clusterCount];
            var queue = new Queue<int>();
            for (int c = 0; c < clusterCount; c++)
            {
                alive[c] = true;
                if (neighbours[c].Count < 2) queue.Enqueue(c);
            }
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                if (!alive[c]) continue;
                alive[c] = false;
                stats.PrunedVertices++;
                foreach (int u in neighbours[c])
                {
                    if (!alive[u]) continue;
                    neighbours[u].Remove(c);
                    if (neighbours[u].Count < 2) queue.Enqueue(u);
                }
                neighbours[c].Clear();
            }

            // 4. Rotation system: links sorted by angle, counter-clockwise about the normal.
            var angleOf = new float[clusterCount][];
            for (int c = 0; c < clusterCount; c++)
            {
                List<int> nb = neighbours[c];
                if (nb.Count == 0) continue;
                Vector3 nrm = cNormal[c];
                Vector3 s = FieldMath.AnyTangent(nrm), t = Vector3.Cross(nrm, s);
                var angles = new float[nb.Count];
                for (int i = 0; i < nb.Count; i++)
                {
                    Vector3 d = cPos[nb[i]] - cPos[c];
                    angles[i] = Mathf.Atan2(Vector3.Dot(d, t), Vector3.Dot(d, s));
                }
                int[] order = new int[nb.Count];
                for (int i = 0; i < order.Length; i++) order[i] = nb[i];
                Array.Sort(angles, order);
                nb.Clear();
                nb.AddRange(order);
                angleOf[c] = angles;
                stats.Links += nb.Count;
            }
            stats.Links /= 2;

            // Dart (c -> neighbours[c][i]) has index dartStart[c] + i.
            var dartStart = new int[clusterCount + 1];
            for (int c = 0; c < clusterCount; c++) dartStart[c + 1] = dartStart[c] + neighbours[c].Count;
            var used = new bool[dartStart[clusterCount]];

            var builder = new PolyMeshBuilder(clusterCount + clusterCount / 8, clusterCount);
            for (int c = 0; c < clusterCount; c++) builder.AddVertex(cPos[c]);

            var face = new List<int>(8);
            var loops = new List<List<int>>();
            for (int c0 = 0; c0 < clusterCount; c0++)
            {
                for (int i0 = 0; i0 < neighbours[c0].Count; i0++)
                {
                    if (used[dartStart[c0] + i0]) continue;

                    // Trace: arriving at u from c, leave along the link just BEFORE c in u's
                    // counter-clockwise order. That walks each face with the surface on the
                    // left of travel, i.e. wound so Cross(b - a, c - a) points outward.
                    face.Clear();
                    int c = c0, i = i0;
                    bool closed = false;
                    while (face.Count <= MaxFaceSize)
                    {
                        int dart = dartStart[c] + i;
                        if (used[dart]) { closed = c == c0 && i == i0; break; }
                        used[dart] = true;
                        face.Add(c);
                        int u = neighbours[c][i];
                        int back = neighbours[u].IndexOf(c);
                        int cnt = neighbours[u].Count;
                        c = u;
                        i = (back - 1 + cnt) % cnt;
                    }
                    stats.Faces++;
                    if (!closed) { stats.BadFacesDropped++; continue; }

                    // A face that passes through a vertex twice wraps around a spur or pinches
                    // at that vertex: emit each simple loop separately. Two-vertex loops are the
                    // spur itself (an edge walked there and back) and cover nothing.
                    loops.Clear();
                    SplitIntoSimpleLoops(face, loops);
                    foreach (List<int> loop in loops)
                    {
                        if (loop.Count < 3) { stats.SpursDropped++; continue; }
                        EmitFace(loop, cPos, neighbours, angleOf, builder, ref stats);
                    }
                }
            }

            return PolyMeshCleanup.RemoveUnusedVertices(builder.Build());
        }

        private static void EmitFace(List<int> face, Vector3[] cPos, List<int>[] neighbours, float[][] angleOf,
                                     PolyMeshBuilder builder, ref Stats stats)
        {
            // Interior of a planar k-gon: angles sum to (k - 2) * 180; the same loop traced
            // around its outside sums to (k + 2) * 180. Split the difference. Only for k >= 5:
            // a real opening in the sculpt is many quads around, while a triangle or quad that
            // reads as "outside" is a folded face on a closed surface (one showed up on a
            // sphere), and covering a hole that small is harmless anyway.
            int k = face.Count;
            if (k >= 5)
            {
                double angleSum = 0;
                for (int j = 0; j < k; j++)
                    angleSum += CornerAngle(face[(j + k - 1) % k], face[j], face[(j + 1) % k], neighbours, angleOf);
                if (angleSum > k * Math.PI) { stats.OuterFacesDropped++; return; }
            }

            if (k == 3) { builder.AddFace(face[0], face[1], face[2]); stats.Triangles++; return; }
            if (k == 4) { builder.AddFace(face[0], face[1], face[2], face[3]); stats.Quads++; return; }

            // Centre fan: quads (centre, f[2m], f[2m+1], f[2m+2]); odd k leaves one triangle.
            stats.SplitPolygons++;
            Vector3 centre = Vector3.zero;
            foreach (int fv in face) centre += cPos[fv];
            int cv = builder.AddVertex(centre / k);
            for (int m = 0; m + 2 <= k - (k & 1); m += 2)
            {
                builder.AddFace(cv, face[m], face[m + 1], face[(m + 2) % k]);
                stats.Quads++;
            }
            if ((k & 1) == 1) { builder.AddFace(cv, face[k - 1], face[0]); stats.Triangles++; }
        }

        /// Splits a closed walk into simple cycles: whenever a vertex repeats, the stretch
        /// between its two visits is a closed loop of its own and is cut out.
        private static void SplitIntoSimpleLoops(List<int> walk, List<List<int>> loops)
        {
            var stack = new List<int>(walk.Count);
            foreach (int v in walk)
            {
                int at = stack.LastIndexOf(v);
                if (at >= 0)
                {
                    loops.Add(stack.GetRange(at, stack.Count - at));
                    stack.RemoveRange(at + 1, stack.Count - at - 1);
                    continue;
                }
                stack.Add(v);
            }
            if (stack.Count > 0) loops.Add(stack);
        }

        /// Angle of the face corner at c between the link to `prev` and the link to `next`,
        /// swept clockwise from prev to next (the face side, given how faces are traced).
        private static double CornerAngle(int prev, int c, int next, List<int>[] neighbours, float[][] angleOf)
        {
            float[] ang = angleOf[c];
            List<int> nb = neighbours[c];
            double a = ang[nb.IndexOf(prev)], b = ang[nb.IndexOf(next)];
            double sweep = a - b;
            while (sweep <= 0) sweep += 2 * Math.PI;
            while (sweep > 2 * Math.PI) sweep -= 2 * Math.PI;
            return sweep;
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
