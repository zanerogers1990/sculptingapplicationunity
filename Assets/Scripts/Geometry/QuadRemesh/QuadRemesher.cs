using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

namespace Sculpting
{
    /// Basic quad remesh of a triangle sculpt: Instant Meshes' field-aligned method.
    ///
    ///   prepare (sanitize, split long edges, hierarchy)  -> QuadRemeshInput
    ///   orientation field, position field                -> FieldSolver
    ///   quad extraction                                  -> QuadExtractor
    ///   project onto the sculpt, light relaxation        -> here
    ///
    /// Deliberately basic (no curvature-guided flow, no symmetry): the goal is an even,
    /// quad-dominant mesh at roughly the requested quad count that sits on the sculpt's surface.
    /// Pure computation with no Unity objects, so it can run on a worker thread.
    internal static class QuadRemesher
    {
        internal sealed class Result
        {
            public PolyMesh Mesh;          // the quads (and the odd triangle), on the surface
            public int[] Triangles;        // Mesh triangulated for the sculpting engine
            public int[] TriangleFace;     // source face of each triangle
            public QuadRemeshInput Input;
            public QuadExtractor.Stats Extraction;
            public PolyMeshCleanup.Stats Cleanup;
            public double OrientationMs, PositionMs, ExtractMs, FinishMs, TotalMs;

            public string Summary()
            {
                var ci = CultureInfo.InvariantCulture;
                int quads = 0, others = 0;
                for (int f = 0; f < Mesh.FaceCount; f++) { if (Mesh.FaceSize(f) == 4) quads++; else others++; }
                return string.Format(ci, "{0:N0} quads + {1:N0} other faces ({2:P1} quads) from {3:N0} tris in {4:F1} s",
                    quads, others, Mesh.FaceCount > 0 ? (double)quads / Mesh.FaceCount : 0,
                    Input.Split.InputTriangles, TotalMs / 1000.0);
            }

            /// Per-stage wall times, for the console.
            public string TimingSummary() => string.Format(CultureInfo.InvariantCulture,
                "sanitize {0:F0} ms, split {1:F0} ms ({2:N0} -> {3:N0} verts), hierarchy {4:F0} ms, orientation {5:F0} ms, position {6:F0} ms, extract {7:F0} ms, finish {8:F0} ms",
                Input.SanitizeMs, Input.SplitMs, Input.Split.InputVertices, Input.Split.OutputVertices, Input.HierarchyMs,
                OrientationMs, PositionMs, ExtractMs, FinishMs);
        }

        public const int RelaxIterations = 3;

        public static Result Remesh(Vector3[] vertices, int[] triangles, int targetQuadCount)
        {
            var total = Stopwatch.StartNew();
            var result = new Result { Input = QuadRemeshInput.Prepare(vertices, triangles, targetQuadCount) };
            QuadRemeshInput input = result.Input;
            FieldHierarchy h = input.Hierarchy;
            float scale = input.QuadEdge;

            var sw = Stopwatch.StartNew();
            Vector3[][] q = FieldSolver.SolveOrientation(h);
            result.OrientationMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            Vector3[][] o = FieldSolver.SolvePosition(h, q, scale);
            result.PositionMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            PolyMesh extracted = QuadExtractor.Extract(h.Finest, q[0], o[0], scale, out result.Extraction);
            result.ExtractMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            result.Mesh = Finish(extracted, input, scale, ref result.Cleanup);
            result.Triangles = PolyMeshTriangulator.Triangulate(result.Mesh, out result.TriangleFace);
            result.FinishMs = sw.Elapsed.TotalMilliseconds;
            result.TotalMs = total.Elapsed.TotalMilliseconds;
            return result;
        }

        /// Connected-piece id of every triangle (union over shared vertices).
        private static int[] TrianglePieces(int vertexCount, int[] triangles)
        {
            var parent = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = Find(triangles[i]), b = Find(triangles[i + 1]), c = Find(triangles[i + 2]);
                if (a != b) parent[a] = b;
                b = Find(b);
                if (c != b) parent[c] = b;
            }
            var piece = new int[triangles.Length / 3];
            for (int t = 0; t < piece.Length; t++) piece[t] = Find(triangles[t * 3]);
            return piece;
        }

        /// Area-weighted vertex normals of a triangle list (normalized by hand - see VectorMath
        /// for why not Vector3.normalized).
        public static Vector3[] VertexNormals(Vector3[] vertices, int[] triangles)
        {
            var n = new Vector3[vertices.Length];
            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                Vector3 fn = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                n[a] += fn;
                n[b] += fn;
                n[c] += fn;
            }
            for (int i = 0; i < n.Length; i++) n[i] = VectorMath.NormalizeOr(n[i], Vector3.up);
            return n;
        }

        public static Bounds BoundsOf(Vector3[] vertices)
        {
            if (vertices.Length == 0) return new Bounds();
            Vector3 min = vertices[0], max = vertices[0];
            foreach (Vector3 p in vertices) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }

        /// Projects the extracted vertices onto the sculpt, cleans the topology
        /// (PolyMeshCleanup), then relaxes: each interior vertex moves halfway to the mean of its
        /// edge neighbours and is projected back, which evens out the lattice's small jitter
        /// without leaving the surface. Boundary vertices are only projected, so open edges keep
        /// their line.
        private static PolyMesh Finish(PolyMesh extracted, QuadRemeshInput input, float scale, ref PolyMeshCleanup.Stats cleanup)
        {
            var projector = new SurfaceProjector(input.Vertices, input.Triangles, scale);
            var projected = (Vector3[])extracted.Vertices.Clone();
            Parallel.For(0, projected.Length, i => projected[i] = projector.Project(projected[i], out _));
            PolyMesh mesh = PolyMeshCleanup.Clean(new PolyMesh(projected, extracted.FaceStart, extracted.FaceIndices), ref cleanup);
            int[] inputPiece = TrianglePieces(input.Vertices.Length, input.Triangles);
            mesh = PolyMeshCleanup.RemoveFragments(mesh, pos =>
            {
                projector.Project(pos, out _, out int tri);
                return tri >= 0 ? inputPiece[tri] : -1;
            }, ref cleanup);

            int n = mesh.VertexCount;
            var p = (Vector3[])mesh.Vertices.Clone();

            PolyMeshEdges edges = PolyMeshEdges.Build(mesh);
            var nbStart = new int[n + 1];
            var boundary = new bool[n];
            for (int e = 0; e < edges.EdgeCount; e++)
            {
                nbStart[edges.EdgeA[e] + 1]++;
                nbStart[edges.EdgeB[e] + 1]++;
                if (edges.UseCount[e] != 2) boundary[edges.EdgeA[e]] = boundary[edges.EdgeB[e]] = true;
            }
            for (int i = 0; i < n; i++) nbStart[i + 1] += nbStart[i];
            var fill = (int[])nbStart.Clone();
            var nb = new int[nbStart[n]];
            for (int e = 0; e < edges.EdgeCount; e++)
            {
                nb[fill[edges.EdgeA[e]]++] = edges.EdgeB[e];
                nb[fill[edges.EdgeB[e]]++] = edges.EdgeA[e];
            }

            var next = new Vector3[n];
            for (int it = 0; it < RelaxIterations; it++)
            {
                Vector3[] cur = p;
                Parallel.For(0, n, i =>
                {
                    int s = nbStart[i], e = nbStart[i + 1];
                    if (boundary[i] || e == s) { next[i] = cur[i]; return; }
                    Vector3 avg = Vector3.zero;
                    for (int k = s; k < e; k++) avg += cur[nb[k]];
                    avg /= e - s;
                    next[i] = projector.Project((cur[i] + avg) * 0.5f, out _);
                });
                (p, next) = (next, p);
            }
            return new PolyMesh(p, mesh.FaceStart, mesh.FaceIndices);
        }
    }
}
