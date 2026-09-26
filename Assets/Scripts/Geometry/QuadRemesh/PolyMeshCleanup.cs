using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Small topology fixes for the extractor's output, the last step before projection.
    ///
    ///  - Interior vertices with only two edges are dissolved: such a vertex is a kink in an
    ///    edge shared by two faces and only makes both faces worse. Removing it from both faces
    ///    keeps the mesh consistent (each face loses one corner; a face left with two corners is
    ///    the sliver it formed and goes too).
    ///  - Adjacent triangles are paired into quads. Extraction leaves triangles at field
    ///    singularities, and most of them come in side-by-side pairs - a quad that was cut in
    ///    two. Pairs merge best-shape first (smallest worst-corner deviation from 90 degrees),
    ///    and never into a quad with a corner of 160 degrees or more.
    ///  - Small stray pieces split off from a larger one are dropped (RemoveFragments).
    internal static class PolyMeshCleanup
    {
        internal struct Stats
        {
            public int ValenceTwoDissolved, SliversRemoved, TrianglePairsMerged, FragmentsRemoved;
        }

        private const float MaxMergedCornerDeg = 160f;

        public static PolyMesh Clean(PolyMesh mesh, ref Stats stats)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                int before = stats.ValenceTwoDissolved;
                mesh = DissolveValenceTwo(mesh, ref stats);
                if (stats.ValenceTwoDissolved == before) break;
            }
            return RemoveUnusedVertices(PairTriangles(mesh, ref stats));
        }

        private const int FragmentMaxFaces = 32;

        /// Drops small stray pieces the extraction split off. Where a feature is thinner than a
        /// quad (an ear, a finger tip), a few faces can close up into a separate little
        /// polyhedron - measured 3 to 10 faces each on a real model that went in as one piece
        /// and came out as six. Each output piece is matched to the input piece it lies on
        /// (`inputPieceOf`, from a vertex position); per input piece the largest output piece
        /// is kept, and any other under FragmentMaxFaces is dropped. Big extra pieces stay -
        /// losing real surface would be worse than an unwanted split - and a genuinely separate
        /// small part (an eye) is its own input piece, so it is always kept.
        public static PolyMesh RemoveFragments(PolyMesh mesh, Func<Vector3, int> inputPieceOf, ref Stats stats)
        {
            int n = mesh.VertexCount;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            for (int f = 0; f < mesh.FaceCount; f++)
                for (int k = 1; k < mesh.FaceSize(f); k++)
                {
                    int a = Find(parent, mesh.Corner(f, 0)), b = Find(parent, mesh.Corner(f, k));
                    if (a != b) parent[a] = b;
                }

            var faces = new int[n];
            for (int f = 0; f < mesh.FaceCount; f++) faces[Find(parent, mesh.Corner(f, 0))]++;

            // Largest output piece per input piece.
            var pieceOf = new Dictionary<int, int>();   // output root -> input piece
            var largest = new Dictionary<int, int>();   // input piece -> output root with most faces
            for (int r = 0; r < n; r++)
            {
                if (faces[r] == 0) continue;
                int input = inputPieceOf(mesh.Vertices[r]);
                pieceOf[r] = input;
                if (!largest.TryGetValue(input, out int best) || faces[r] > faces[best]) largest[input] = r;
            }

            var drop = new bool[n];
            bool any = false;
            foreach (var kv in pieceOf)
            {
                int r = kv.Key;
                if (largest[kv.Value] == r || faces[r] >= FragmentMaxFaces) continue;
                drop[r] = true;
                any = true;
                stats.FragmentsRemoved++;
            }
            if (!any) return mesh;

            var builder = new PolyMeshBuilder(n, mesh.FaceCount);
            foreach (Vector3 v in mesh.Vertices) builder.AddVertex(v);
            var corners = new int[64];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                if (drop[Find(parent, mesh.Corner(f, 0))]) continue;
                int size = mesh.FaceSize(f);
                if (size > corners.Length) corners = new int[size * 2];
                Array.Copy(mesh.FaceIndices, mesh.FaceStart[f], corners, 0, size);
                builder.AddFace(corners, size);
            }
            return RemoveUnusedVertices(builder.Build());
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        /// Drops vertices no face references, renumbering the rest in order.
        public static PolyMesh RemoveUnusedVertices(PolyMesh m)
        {
            var map = new int[m.VertexCount];
            for (int i = 0; i < map.Length; i++) map[i] = -1;
            foreach (int v in m.FaceIndices) map[v] = 0;
            int count = 0;
            for (int i = 0; i < map.Length; i++) if (map[i] == 0) map[i] = count++;
            if (count == m.VertexCount) return m;
            var v2 = new Vector3[count];
            for (int i = 0; i < map.Length; i++) if (map[i] >= 0) v2[map[i]] = m.Vertices[i];
            var idx = new int[m.FaceIndices.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = map[m.FaceIndices[i]];
            return new PolyMesh(v2, m.FaceStart, idx);
        }

        private static PolyMesh DissolveValenceTwo(PolyMesh mesh, ref Stats stats)
        {
            PolyMeshEdges edges = PolyMeshEdges.Build(mesh);
            int n = mesh.VertexCount;
            var valence = new int[n];
            var onBoundary = new bool[n];
            for (int e = 0; e < edges.EdgeCount; e++)
            {
                valence[edges.EdgeA[e]]++;
                valence[edges.EdgeB[e]]++;
                if (edges.UseCount[e] != 2) onBoundary[edges.EdgeA[e]] = onBoundary[edges.EdgeB[e]] = true;
            }

            var remove = new bool[n];
            int count = 0;
            for (int v = 0; v < n; v++)
                if (valence[v] == 2 && !onBoundary[v]) { remove[v] = true; count++; }
            if (count == 0) return mesh;

            var b = new PolyMeshBuilder(n, mesh.FaceCount);
            foreach (Vector3 p in mesh.Vertices) b.AddVertex(p);
            var corners = new int[64];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                int k = 0;
                int size = mesh.FaceSize(f);
                if (size > corners.Length) corners = new int[size * 2];
                for (int c = 0; c < size; c++)
                {
                    int v = mesh.Corner(f, c);
                    if (!remove[v]) corners[k++] = v;
                }
                if (k < 3) { stats.SliversRemoved++; continue; }
                b.AddFace(corners, k);
            }
            stats.ValenceTwoDissolved += count;
            return b.Build();
        }

        private static PolyMesh PairTriangles(PolyMesh mesh, ref Stats stats)
        {
            PolyMeshEdges edges = PolyMeshEdges.Build(mesh);
            Vector3[] p = mesh.Vertices;

            // Faces on each edge: the extractor's output has at most two per edge.
            var faceA = new int[edges.EdgeCount];
            var faceB = new int[edges.EdgeCount];
            for (int e = 0; e < edges.EdgeCount; e++) faceA[e] = faceB[e] = -1;
            for (int f = 0; f < mesh.FaceCount; f++)
                for (int c = mesh.FaceStart[f]; c < mesh.FaceStart[f + 1]; c++)
                {
                    int e = edges.SideEdge[c];
                    if (e < 0) continue;
                    if (faceA[e] < 0) faceA[e] = f; else faceB[e] = f;
                }

            var candidates = new List<(float score, int edge)>();
            for (int e = 0; e < edges.EdgeCount; e++)
            {
                if (edges.UseCount[e] != 2 || faceB[e] < 0) continue;
                if (mesh.FaceSize(faceA[e]) != 3 || mesh.FaceSize(faceB[e]) != 3) continue;
                if (!MergedQuad(mesh, faceA[e], faceB[e], edges.EdgeA[e], edges.EdgeB[e], out int[] quad)) continue;
                float worst = WorstCornerDeg(p, quad);
                if (worst >= MaxMergedCornerDeg) continue;
                candidates.Add((Mathf.Abs(worst - 90f), e));
            }
            if (candidates.Count == 0) return mesh;
            candidates.Sort((x, y) => x.score.CompareTo(y.score));

            var merged = new int[mesh.FaceCount][];
            var consumed = new bool[mesh.FaceCount];
            foreach (var (_, e) in candidates)
            {
                int fa = faceA[e], fb = faceB[e];
                if (consumed[fa] || consumed[fb]) continue;
                MergedQuad(mesh, fa, fb, edges.EdgeA[e], edges.EdgeB[e], out int[] quad);
                consumed[fa] = consumed[fb] = true;
                merged[fa] = quad;
                stats.TrianglePairsMerged++;
            }

            var b = new PolyMeshBuilder(mesh.VertexCount, mesh.FaceCount);
            foreach (Vector3 v in p) b.AddVertex(v);
            var corners = new int[64];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                if (merged[f] != null) { b.AddFace(merged[f], 4); continue; }
                if (consumed[f]) continue;
                int size = mesh.FaceSize(f);
                if (size > corners.Length) corners = new int[size * 2];
                Array.Copy(mesh.FaceIndices, mesh.FaceStart[f], corners, 0, size);
                b.AddFace(corners, size);
            }
            return b.Build();
        }

        /// The quad formed by triangles fa and fb across their shared edge (u, w), keeping the
        /// winding. False if the triangles don't run the edge in opposite directions.
        private static bool MergedQuad(PolyMesh mesh, int fa, int fb, int u, int w, out int[] quad)
        {
            quad = null;
            // Rotate fa so its shared side is corner 0 -> corner 1.
            int s = -1;
            for (int k = 0; k < 3; k++)
            {
                int x = mesh.Corner(fa, k), y = mesh.Corner(fa, k + 1);
                if ((x == u && y == w) || (x == w && y == u)) { s = k; break; }
            }
            if (s < 0) return false;
            int x0 = mesh.Corner(fa, s), x1 = mesh.Corner(fa, s + 1), x2 = mesh.Corner(fa, s + 2);
            // fb must contain x1 -> x0.
            int t = -1;
            for (int k = 0; k < 3; k++)
                if (mesh.Corner(fb, k) == x1 && mesh.Corner(fb, k + 1) == x0) { t = k; break; }
            if (t < 0) return false;
            int y2 = mesh.Corner(fb, t + 2);
            if (y2 == x2) return false;
            quad = new[] { x1, x2, x0, y2 };
            return true;
        }

        private static float WorstCornerDeg(Vector3[] p, int[] quad)
        {
            float worst = 0f;
            for (int k = 0; k < 4; k++)
            {
                Vector3 c = p[quad[k]];
                Vector3 a = p[quad[(k + 3) & 3]] - c, b = p[quad[(k + 1) & 3]] - c;
                float la = a.magnitude, lb = b.magnitude;
                if (la <= 0f || lb <= 0f) return 180f;
                float cos = Mathf.Clamp(Vector3.Dot(a, b) / (la * lb), -1f, 1f);
                float deg = (float)(Math.Acos(cos) * 180.0 / Math.PI);
                if (deg > worst) worst = deg;
            }
            return worst;
        }
    }
}
