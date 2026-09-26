using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Sculpting
{
    /// Solves the quad remesher's two fields on a FieldHierarchy, coarsest level first.
    ///
    /// Orientation (4-RoSy): per vertex, a tangent direction q, smoothed so neighbours agree up to
    /// 90-degree turns. Position (4-PoSy): per vertex, a lattice offset o, smoothed so
    /// neighbours' lattices of spacing `scale` line up. Both are plain Instant Meshes smoothing -
    /// no curvature or boundary alignment, by design (basic remesh).
    ///
    /// Each sweep is Gauss-Seidel: a vertex is replaced by the running compatible average of its
    /// neighbours, reading the newest values. Vertices of one colour phase share no edge, so a
    /// phase runs in parallel and the result is identical to the serial order whatever the
    /// thread scheduling.
    internal static class FieldSolver
    {
        public const int DefaultIterations = 6;
        private const int Block = 1024;

        /// Orientation field for every level (index = level). Coarse levels hold the finest
        /// solution averaged back up, ready for the position solve.
        public static Vector3[][] SolveOrientation(FieldHierarchy h, int iterations = DefaultIterations)
        {
            int levels = h.Levels.Length;
            var q = new Vector3[levels][];

            FieldLevel top = h.Coarsest;
            q[levels - 1] = new Vector3[top.Count];
            for (int v = 0; v < top.Count; v++)
            {
                Vector3 n = top.Normal[v];
                q[levels - 1][v] = FieldMath.TangentUnit(RandomUnit(v), n, FieldMath.AnyTangent(n));
            }

            for (int l = levels - 1; l >= 0; l--)
            {
                FieldLevel level = h.Levels[l];
                if (l < levels - 1)
                {
                    q[l] = new Vector3[level.Count];
                    FieldLevel coarse = h.Levels[l + 1];
                    Vector3[] qc = q[l + 1], qf = q[l];
                    for (int c = 0; c < coarse.Count; c++)
                    {
                        Down(level, qf, qc[c], coarse.FineA[c]);
                        if (coarse.FineB[c] >= 0) Down(level, qf, qc[c], coarse.FineB[c]);
                    }
                }
                Vector3[] ql = q[l];
                for (int it = 0; it < iterations; it++)
                    ForEachPhase(level, v => ql[v] = SmoothOrientation(level, ql, v));
            }

            // Fine -> coarse, so every level carries the converged field.
            for (int l = 0; l + 1 < levels; l++)
            {
                FieldLevel fine = h.Levels[l], coarse = h.Levels[l + 1];
                for (int c = 0; c < coarse.Count; c++)
                {
                    Vector3 n = coarse.Normal[c];
                    Vector3 qa = FieldMath.TangentUnit(q[l][coarse.FineA[c]], n, FieldMath.AnyTangent(n));
                    if (coarse.FineB[c] < 0) { q[l + 1][c] = qa; continue; }
                    Vector3 qb = FieldMath.TangentUnit(q[l][coarse.FineB[c]], n, qa);
                    FieldMath.CompatOrientation(qa, n, qb, n, out Vector3 x, out Vector3 y);
                    q[l + 1][c] = FieldMath.TangentUnit(x * fine.Area[coarse.FineA[c]] + y * fine.Area[coarse.FineB[c]], n, qa);
                }
            }
            return q;
        }

        /// Position field for every level, given the orientation field from SolveOrientation.
        public static Vector3[][] SolvePosition(FieldHierarchy h, Vector3[][] q, float scale, int iterations = DefaultIterations)
        {
            int levels = h.Levels.Length;
            var o = new Vector3[levels][];
            float inv = 1f / scale;

            FieldLevel top = h.Coarsest;
            o[levels - 1] = (Vector3[])top.Position.Clone();

            for (int l = levels - 1; l >= 0; l--)
            {
                FieldLevel level = h.Levels[l];
                if (l < levels - 1)
                {
                    o[l] = new Vector3[level.Count];
                    FieldLevel coarse = h.Levels[l + 1];
                    for (int c = 0; c < coarse.Count; c++)
                    {
                        Vector3 oc = o[l + 1][c];
                        int a = coarse.FineA[c], b = coarse.FineB[c];
                        o[l][a] = oc - level.Normal[a] * Vector3.Dot(level.Normal[a], oc - level.Position[a]);
                        if (b >= 0) o[l][b] = oc - level.Normal[b] * Vector3.Dot(level.Normal[b], oc - level.Position[b]);
                    }
                }
                Vector3[] ol = o[l], ql = q[l];
                for (int it = 0; it < iterations; it++)
                    ForEachPhase(level, v => ol[v] = SmoothPosition(level, ql, ol, v, scale, inv));
            }
            return o;
        }

        private static void Down(FieldLevel fine, Vector3[] qf, Vector3 qc, int f)
        {
            Vector3 n = fine.Normal[f];
            qf[f] = FieldMath.TangentUnit(qc, n, FieldMath.AnyTangent(n));
        }

        private static Vector3 SmoothOrientation(FieldLevel level, Vector3[] q, int v)
        {
            Vector3 n = level.Normal[v];
            Vector3 sum = q[v];
            float weightSum = 0f;
            int[] adj = level.Adjacency;
            float[] w = level.AdjacencyWeight;
            for (int k = level.AdjacencyStart[v]; k < level.AdjacencyStart[v + 1]; k++)
            {
                int u = adj[k];
                FieldMath.CompatOrientation(sum, n, q[u], level.Normal[u], out Vector3 a, out Vector3 b);
                sum = a * weightSum + b * w[k];
                sum -= n * Vector3.Dot(n, sum);
                weightSum += w[k];
                float len = sum.magnitude;
                if (len > 1e-20f) sum /= len;
            }
            return FieldMath.TangentUnit(sum, n, q[v]);
        }

        private static Vector3 SmoothPosition(FieldLevel level, Vector3[] q, Vector3[] o, int v, float scale, float inv)
        {
            Vector3 n = level.Normal[v], p = level.Position[v], qv = q[v];
            Vector3 sum = o[v];
            float weightSum = 0f;
            int[] adj = level.Adjacency;
            float[] w = level.AdjacencyWeight;
            for (int k = level.AdjacencyStart[v]; k < level.AdjacencyStart[v + 1]; k++)
            {
                int u = adj[k];
                // No rotation matching needed: a square lattice is the same set of points
                // whichever of its four directions q names.
                FieldMath.CompatPosition(p, n, qv, sum, level.Position[u], level.Normal[u], q[u], o[u], scale, inv, out Vector3 a, out Vector3 b);
                sum = a * weightSum + b * w[k];
                weightSum += w[k];
                if (weightSum > 1e-20f) sum /= weightSum;
                sum -= n * Vector3.Dot(n, sum - p);
            }
            return FieldMath.PositionRound(sum, qv, n, p, scale, inv);
        }

        private static void ForEachPhase(FieldLevel level, Action<int> update)
        {
            for (int ph = 0; ph < level.PhaseCount; ph++)
            {
                int start = level.PhaseStart[ph], end = level.PhaseStart[ph + 1];
                int[] verts = level.PhaseVertices;
                int count = end - start;
                if (count < Block * 2)
                {
                    for (int i = start; i < end; i++) update(verts[i]);
                    continue;
                }
                int blocks = (count + Block - 1) / Block;
                Parallel.For(0, blocks, b =>
                {
                    int s = start + b * Block, e = Math.Min(end, s + Block);
                    for (int i = s; i < e; i++) update(verts[i]);
                });
            }
        }

        /// Deterministic pseudo-random unit vector from an index (splitmix-style hash).
        private static Vector3 RandomUnit(int i)
        {
            ulong x = (ulong)i * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
            float Next()
            {
                x ^= x >> 30; x *= 0xbf58476d1ce4e5b9UL;
                x ^= x >> 27; x *= 0x94d049bb133111ebUL;
                x ^= x >> 31;
                return (x >> 40) * (1f / (1 << 24)) * 2f - 1f;
            }
            return VectorMath.NormalizeOr(new Vector3(Next(), Next(), Next()), Vector3.right);
        }
    }
}
