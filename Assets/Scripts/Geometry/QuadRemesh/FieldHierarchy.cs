using System;
using UnityEngine;

namespace Sculpting
{
    /// One resolution of the field hierarchy: a point cloud with a neighbour graph.
    ///
    /// Level 0 is the conditioned mesh's own vertices and edges. Each coarser level merges
    /// pairs of neighbours, so it is a graph over area-weighted clusters, not a mesh - the
    /// field solvers only ever need positions, normals, areas and neighbours.
    internal sealed class FieldLevel
    {
        public int Count;
        public Vector3[] Position;
        public Vector3[] Normal;     // unit length
        public float[] Area;         // surface area this vertex stands for (sums to the mesh area on every level)

        // Neighbours of v: Adjacency[AdjacencyStart[v] .. AdjacencyStart[v + 1]), symmetric,
        // no self-loops, no duplicates. Weight = how many finest-level edges the link stands for.
        public int[] AdjacencyStart;
        public int[] Adjacency;
        public float[] AdjacencyWeight;

        // Links between levels. ToCoarse is null on the coarsest level; FineA/FineB are null on
        // level 0. FineB is -1 where a vertex was carried up alone rather than merged.
        public int[] ToCoarse;
        public int[] FineA;
        public int[] FineB;

        // Graph colouring: the vertices of one phase share no edge, so a solver can update a
        // whole phase in parallel and still be Gauss-Seidel across phases.
        public int[] PhaseStart;
        public int[] PhaseVertices;

        public int PhaseCount => PhaseStart.Length - 1;
        public int EdgeCount => Adjacency.Length / 2;
    }

    /// The multi-resolution hierarchy the orientation and position fields are solved on,
    /// coarse to fine (Jakob et al. 2015, "Instant Field-Aligned Meshes", section 5).
    ///
    /// Solving on the finest graph alone would need thousands of smoothing sweeps for
    /// information to cross a 450k-vertex model; solved coarsest-first and handed down, each
    /// level only has to fix up local detail, so a few sweeps per level suffice.
    internal sealed class FieldHierarchy
    {
        public FieldLevel[] Levels;
        public FieldLevel Finest => Levels[0];
        public FieldLevel Coarsest => Levels[Levels.Length - 1];

        /// Coarsening stops at this many vertices, or when a level fails to shrink by at least
        /// 10% (a graph of isolated vertices can't merge further).
        public const int DefaultMinVertices = 64;

        public static FieldHierarchy Build(Vector3[] vertices, int[] triangles, int minVertices = DefaultMinVertices)
        {
            var levels = new System.Collections.Generic.List<FieldLevel> { BuildFinest(vertices, triangles) };
            while (levels[levels.Count - 1].Count > minVertices && levels.Count < 48)
            {
                FieldLevel fine = levels[levels.Count - 1];
                FieldLevel coarse = Coarsen(fine);
                if (coarse.Count > fine.Count * 0.9)
                {
                    fine.ToCoarse = null;
                    break;
                }
                levels.Add(coarse);
            }
            foreach (FieldLevel level in levels) Colour(level);
            return new FieldHierarchy { Levels = levels.ToArray() };
        }

        private static FieldLevel BuildFinest(Vector3[] vertices, int[] triangles)
        {
            int n = vertices.Length;
            var level = new FieldLevel
            {
                Count = n,
                Position = (Vector3[])vertices.Clone(),
                Normal = new Vector3[n],
                Area = new float[n],
            };

            var areaSum = new double[n];
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                Vector3 cross = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                // Area-weighted normals: the raw cross product is 2 x area along the normal.
                level.Normal[a] += cross;
                level.Normal[b] += cross;
                level.Normal[c] += cross;
                double third = cross.magnitude / 6.0;
                areaSum[a] += third;
                areaSum[b] += third;
                areaSum[c] += third;
            }
            for (int v = 0; v < n; v++)
            {
                level.Normal[v] = VectorMath.NormalizeOr(level.Normal[v], Vector3.up);
                level.Area[v] = (float)areaSum[v];
            }

            PolyMeshEdges edges = PolyMeshEdges.Build(PolyMesh.FromTriangles(vertices, triangles));
            var start = new int[n + 1];
            for (int e = 0; e < edges.EdgeCount; e++)
            {
                start[edges.EdgeA[e] + 1]++;
                start[edges.EdgeB[e] + 1]++;
            }
            for (int v = 0; v < n; v++) start[v + 1] += start[v];
            var fill = (int[])start.Clone();
            var adj = new int[start[n]];
            for (int e = 0; e < edges.EdgeCount; e++)
            {
                adj[fill[edges.EdgeA[e]]++] = edges.EdgeB[e];
                adj[fill[edges.EdgeB[e]]++] = edges.EdgeA[e];
            }
            var weight = new float[adj.Length];
            for (int i = 0; i < weight.Length; i++) weight[i] = 1f;

            level.AdjacencyStart = start;
            level.Adjacency = adj;
            level.AdjacencyWeight = weight;
            return level;
        }

        /// One matching step. Every edge is scored dot(n_i, n_j) * max(A_i/A_j, A_j/A_i) - merge
        /// neighbours that face the same way first, and among those prefer lopsided pairs, which
        /// evens out cluster sizes (a small cluster is absorbed before two big ones combine) -
        /// then taken greedily in score order, each vertex merging at most once.
        private static FieldLevel Coarsen(FieldLevel fine)
        {
            int n = fine.Count;
            int[] start = fine.AdjacencyStart, adj = fine.Adjacency;
            int edgeCount = 0;
            for (int v = 0; v < n; v++)
                for (int k = start[v]; k < start[v + 1]; k++)
                    if (adj[k] > v) edgeCount++;

            var keys = new float[edgeCount];
            var pairs = new long[edgeCount];
            int w = 0;
            for (int v = 0; v < n; v++)
            {
                for (int k = start[v]; k < start[v + 1]; k++)
                {
                    int u = adj[k];
                    if (u <= v) continue;
                    float av = Mathf.Max(fine.Area[v], 1e-30f), au = Mathf.Max(fine.Area[u], 1e-30f);
                    float ratio = av > au ? av / au : au / av;
                    keys[w] = -Vector3.Dot(fine.Normal[v], fine.Normal[u]) * ratio; // ascending sort = best first
                    pairs[w] = ((long)v << 32) | (uint)u;
                    w++;
                }
            }
            Array.Sort(keys, pairs);

            var toCoarse = new int[n];
            for (int v = 0; v < n; v++) toCoarse[v] = -1;
            var fineA = new int[n];
            var fineB = new int[n];
            int count = 0;
            for (int i = 0; i < edgeCount; i++)
            {
                int v = (int)(pairs[i] >> 32), u = (int)(pairs[i] & 0xffffffffL);
                if (toCoarse[v] >= 0 || toCoarse[u] >= 0) continue;
                toCoarse[v] = toCoarse[u] = count;
                fineA[count] = v;
                fineB[count] = u;
                count++;
            }
            for (int v = 0; v < n; v++)
            {
                if (toCoarse[v] >= 0) continue;
                toCoarse[v] = count;
                fineA[count] = v;
                fineB[count] = -1;
                count++;
            }
            Array.Resize(ref fineA, count);
            Array.Resize(ref fineB, count);
            fine.ToCoarse = toCoarse;

            var coarse = new FieldLevel
            {
                Count = count,
                Position = new Vector3[count],
                Normal = new Vector3[count],
                Area = new float[count],
                FineA = fineA,
                FineB = fineB,
            };

            for (int c = 0; c < count; c++)
            {
                int a = fineA[c], b = fineB[c];
                if (b < 0)
                {
                    coarse.Position[c] = fine.Position[a];
                    coarse.Normal[c] = fine.Normal[a];
                    coarse.Area[c] = fine.Area[a];
                    continue;
                }
                float wa = fine.Area[a], wb = fine.Area[b], sum = wa + wb;
                if (!(sum > 0f)) { wa = wb = 0.5f; sum = 1f; }
                coarse.Position[c] = (fine.Position[a] * wa + fine.Position[b] * wb) / sum;
                coarse.Normal[c] = VectorMath.NormalizeOr(fine.Normal[a] * wa + fine.Normal[b] * wb, fine.Normal[a]);
                coarse.Area[c] = sum;
            }

            // Coarse neighbours: the union of the children's neighbours, mapped up, minus the
            // cluster itself, with duplicate links merged and their weights summed. `slot`
            // remembers where each coarse neighbour already sits in the list being built.
            var cStart = new int[count + 1];
            var cAdj = new int[adj.Length];
            var cWeight = new float[adj.Length];
            var slot = new int[count];
            for (int c = 0; c < count; c++) slot[c] = -1;
            int fillPos = 0;
            for (int c = 0; c < count; c++)
            {
                cStart[c] = fillPos;
                for (int child = 0; child < 2; child++)
                {
                    int f = child == 0 ? fineA[c] : fineB[c];
                    if (f < 0) continue;
                    for (int k = start[f]; k < start[f + 1]; k++)
                    {
                        int nc = toCoarse[adj[k]];
                        if (nc == c) continue;
                        if (slot[nc] >= cStart[c] && slot[nc] < fillPos && cAdj[slot[nc]] == nc)
                        {
                            cWeight[slot[nc]] += fine.AdjacencyWeight[k];
                            continue;
                        }
                        slot[nc] = fillPos;
                        cAdj[fillPos] = nc;
                        cWeight[fillPos] = fine.AdjacencyWeight[k];
                        fillPos++;
                    }
                }
            }
            cStart[count] = fillPos;
            Array.Resize(ref cAdj, fillPos);
            Array.Resize(ref cWeight, fillPos);

            coarse.AdjacencyStart = cStart;
            coarse.Adjacency = cAdj;
            coarse.AdjacencyWeight = cWeight;
            return coarse;
        }

        /// Greedy colouring in index order: each vertex takes the lowest colour none of its
        /// already-coloured neighbours has. Uses at most max-degree + 1 colours.
        private static void Colour(FieldLevel level)
        {
            int n = level.Count;
            int[] start = level.AdjacencyStart, adj = level.Adjacency;
            var colour = new int[n];
            var seenBy = new int[64];
            for (int i = 0; i < seenBy.Length; i++) seenBy[i] = -1;
            int colours = 0;

            for (int v = 0; v < n; v++)
            {
                for (int k = start[v]; k < start[v + 1]; k++)
                {
                    int u = adj[k];
                    if (u < v)
                    {
                        int cu = colour[u];
                        if (cu >= seenBy.Length)
                        {
                            int old = seenBy.Length;
                            Array.Resize(ref seenBy, Math.Max(cu + 1, old * 2));
                            for (int i = old; i < seenBy.Length; i++) seenBy[i] = -1;
                        }
                        seenBy[cu] = v;
                    }
                }
                int c = 0;
                while (c < seenBy.Length && seenBy[c] == v) c++;
                colour[v] = c;
                if (c + 1 > colours) colours = c + 1;
            }

            var phaseStart = new int[colours + 1];
            for (int v = 0; v < n; v++) phaseStart[colour[v] + 1]++;
            for (int c = 0; c < colours; c++) phaseStart[c + 1] += phaseStart[c];
            var fill = (int[])phaseStart.Clone();
            var phaseVertices = new int[n];
            for (int v = 0; v < n; v++) phaseVertices[fill[colour[v]]++] = v;

            level.PhaseStart = phaseStart;
            level.PhaseVertices = phaseVertices;
        }
    }
}
