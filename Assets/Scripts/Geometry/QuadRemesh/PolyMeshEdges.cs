using System;

namespace Sculpting
{
    /// The unique undirected edges of a PolyMesh, with how every face side maps onto them.
    ///
    /// Built by sorting packed (min, max) keys rather than hashing them: at the remesher's sizes
    /// (a million faces is four million face sides) a sort is both faster and far lighter than a
    /// Dictionary, and it produces edges in a deterministic order that doesn't depend on the
    /// hash layout.
    ///
    /// Face side k of face f runs from corner k to corner k + 1. A side whose two corners are
    /// the same vertex (a degenerate face) belongs to no edge and maps to -1.
    internal sealed class PolyMeshEdges
    {
        public int EdgeCount;
        public int[] EdgeA;          // lower vertex index
        public int[] EdgeB;          // higher vertex index
        public int[] UseCount;       // face sides lying on this edge: 1 = boundary, 2 = interior, 3+ = non-manifold
        public int[] ForwardCount;   // of those, how many run A -> B (a consistently oriented interior edge has exactly 1)
        public int[] SideEdge;       // per face side (indexed like FaceIndices): its edge, or -1 if degenerate
        public int DegenerateSideCount;

        public bool IsBoundary(int e) => UseCount[e] == 1;
        public bool IsNonManifold(int e) => UseCount[e] > 2;

        /// An interior edge whose two faces both run it the same way - one of them is flipped.
        public bool IsMisoriented(int e) => UseCount[e] == 2 && ForwardCount[e] != 1;

        public static PolyMeshEdges Build(PolyMesh mesh)
        {
            int corners = mesh.CornerCount;
            int faces = mesh.FaceCount;
            int[] start = mesh.FaceStart;
            int[] idx = mesh.FaceIndices;

            var keys = new long[corners];
            var sides = new int[corners];
            int live = 0;
            var result = new PolyMeshEdges { SideEdge = new int[corners] };

            for (int f = 0; f < faces; f++)
            {
                int s = start[f], e = start[f + 1];
                for (int c = s; c < e; c++)
                {
                    int a = idx[c];
                    int b = idx[c + 1 < e ? c + 1 : s];
                    if (a == b)
                    {
                        result.SideEdge[c] = -1;
                        result.DegenerateSideCount++;
                        continue;
                    }
                    int lo = a < b ? a : b, hi = a < b ? b : a;
                    keys[live] = ((long)lo << 32) | (uint)hi;
                    sides[live] = c;
                    live++;
                }
            }

            Array.Sort(keys, sides, 0, live);

            int edgeCount = 0;
            for (int i = 0; i < live; i++)
                if (i == 0 || keys[i] != keys[i - 1]) edgeCount++;

            result.EdgeCount = edgeCount;
            result.EdgeA = new int[edgeCount];
            result.EdgeB = new int[edgeCount];
            result.UseCount = new int[edgeCount];
            result.ForwardCount = new int[edgeCount];

            int edge = -1;
            for (int i = 0; i < live; i++)
            {
                if (i == 0 || keys[i] != keys[i - 1])
                {
                    edge++;
                    result.EdgeA[edge] = (int)(keys[i] >> 32);
                    result.EdgeB[edge] = (int)(keys[i] & 0xffffffffL);
                }
                int side = sides[i];
                result.SideEdge[side] = edge;
                result.UseCount[edge]++;
                if (idx[side] == result.EdgeA[edge]) result.ForwardCount[edge]++;
            }
            return result;
        }
    }
}
