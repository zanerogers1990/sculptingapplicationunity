using System;
using System.Collections.Generic;
using System.Diagnostics;
using Sculpting;
using UnityEngine;

namespace GeometryHarness
{
    /// Step 2 of the quad remesher: long-edge splitting and the field hierarchy.
    internal static class ConditioningSuite
    {
        public static void Run()
        {
            SplitterIsExactAndConforming(TriMesh(Fixtures.TriSphere(24)), 0.05f, "trisphere");
            SplitterIsExactAndConforming(Deform.MovePulls(TriMesh(Fixtures.TriSphere(40)), 6), 0.03f, "pulled sphere");
            SplitterNoOpWhenShort();
            SplitterRespectsVertexCap();
            HierarchyInvariants(TriMesh(Fixtures.TriSphere(40)), "trisphere");
            HierarchyInvariants(TwoPieces(), "two pieces");
            SanitizerRepairs();
            SanitizerLeavesCleanMeshAlone();
        }

        private static PolyMeshReport Sanitized(PolyMesh m, double minArea, out MeshSanitizer.Stats st)
        {
            TriMesh tm = TriMesh(m);
            int[] before = (int[])tm.Triangles.Clone();
            MeshSanitizer.Sanitize(tm.Vertices, tm.Triangles, minArea, out Vector3[] v, out int[] t, out st);
            bool untouched = true;
            for (int i = 0; i < before.Length; i++) untouched &= before[i] == tm.Triangles[i];
            Check.That(untouched, "sanitizer leaves the input triangles untouched");
            return PolyMeshAnalysis.Analyze(PolyMesh.FromTriangles(v, t));
        }

        private static void SanitizerRepairs()
        {
            PolyMesh grid = Fixtures.Grid(2, 2);

            // Pinch: second grid touching the first at one corner vertex.
            var extra = new[] { new Vector3(3, 0, 2), new Vector3(3, 0, 3), new Vector3(2, 0, 3) };
            int v0 = grid.VertexCount;
            PolyMeshReport pinch = Sanitized(Fixtures.Append(grid, extra, new[] { 8, v0 + 2, v0 + 1, v0 }), 0, out var st);
            Check.Equal(1, st.VerticesSplit, "pinch: one vertex copy");
            Check.Equal(0, pinch.NonManifoldVertexCount, "pinch: repaired");
            Check.Equal(2, pinch.ComponentCount, "pinch: now two pieces");

            // Fin: a third face on an interior edge.
            PolyMeshReport fin = Sanitized(Fixtures.Append(grid, new[] { new Vector3(0.5f, 1, 0.5f) }, new[] { 4, 1, grid.VertexCount }), 0, out st);
            Check.Equal(0, fin.NonManifoldEdgeCount, "fin: no non-manifold edge");
            Check.Equal(0, fin.NonManifoldVertexCount, "fin: no non-manifold vertex");
            Check.That(fin.IsManifold, "fin: manifold after repair");

            // Degenerate + duplicate faces.
            PolyMeshReport bad = Sanitized(Fixtures.Append(grid, new Vector3[0], new[] { 0, 1, 1 }, new[] { 0, 3, 4 }, new[] { 4, 0, 3 }), 0, out st);
            Check.Equal(1, st.DegenerateTrianglesRemoved, "degenerate removed");
            Check.That(st.DuplicateTrianglesRemoved >= 1, "duplicate removed");
            Check.That(bad.IsManifold, "degenerate/duplicate: manifold after repair");

            // Debris: a tiny sphere next to a big one.
            TriMesh big = TriMesh(Fixtures.QuadSphere(8));
            TriMesh crumb = TriMesh(Fixtures.QuadSphere(2, 0.01f));
            var verts = new System.Collections.Generic.List<Vector3>(big.Vertices);
            foreach (Vector3 p in crumb.Vertices) verts.Add(p + new Vector3(3, 0, 0));
            var tris = new int[big.Triangles.Length + crumb.Triangles.Length];
            big.Triangles.CopyTo(tris, 0);
            for (int i = 0; i < crumb.Triangles.Length; i++) tris[big.Triangles.Length + i] = crumb.Triangles[i] + big.VertexCount;
            MeshSanitizer.Sanitize(verts.ToArray(), tris, 0.1, out Vector3[] dv, out int[] dt, out st);
            Check.Equal(1, st.ComponentsRemoved, "debris component removed");
            Check.Equal(1, st.ComponentsKept, "main component kept");
            Check.Equal(big.VertexCount, dv.Length, "debris vertices compacted away");
            Check.Equal(big.Triangles.Length, dt.Length, "main triangles kept");
        }

        private static void SanitizerLeavesCleanMeshAlone()
        {
            TriMesh m = TriMesh(Fixtures.QuadSphere(8));
            MeshSanitizer.Sanitize(m.Vertices, m.Triangles, 1e-6, out Vector3[] v, out int[] t, out var st);
            Check.That(!st.ChangedTopology, "clean mesh: no topology change");
            bool same = v.Length == m.VertexCount && t.Length == m.Triangles.Length;
            for (int i = 0; same && i < t.Length; i++) same = t[i] == m.Triangles[i];
            Check.That(same, "clean mesh: identical output");
        }

        public static TriMesh TriMesh(PolyMesh m)
        {
            int[] tris = PolyMeshTriangulator.Triangulate(m, out _);
            return new TriMesh { Name = "fixture", Vertices = m.Vertices, Triangles = tris };
        }

        private static TriMesh TwoPieces()
        {
            PolyMesh a = Fixtures.QuadSphere(10);
            PolyMesh b = Fixtures.QuadSphere(6, 0.5f);
            var v = new List<Vector3>(a.Vertices);
            foreach (Vector3 p in b.Vertices) v.Add(p + new Vector3(3, 0, 0));
            int[] ta = PolyMeshTriangulator.Triangulate(a, out _), tb = PolyMeshTriangulator.Triangulate(b, out _);
            var t = new int[ta.Length + tb.Length];
            ta.CopyTo(t, 0);
            for (int i = 0; i < tb.Length; i++) t[ta.Length + i] = tb[i] + a.VertexCount;
            return new TriMesh { Name = "two", Vertices = v.ToArray(), Triangles = t };
        }

        public static void SplitterIsExactAndConforming(TriMesh m, float maxEdge, string name)
        {
            PolyMeshReport before = PolyMeshAnalysis.Analyze(PolyMesh.FromTriangles(m.Vertices, m.Triangles));
            LongEdgeSplitter.Split(m.Vertices, m.Triangles, maxEdge, int.MaxValue, out Vector3[] v, out int[] t, out var st);
            PolyMesh outMesh = PolyMesh.FromTriangles(v, t);
            PolyMeshReport after = PolyMeshAnalysis.Analyze(outMesh);

            Check.That(st.Rounds > 0, $"{name}: something was split");
            Check.AtMost(maxEdge * 1.0000001, after.MaxEdgeLength, $"{name}: max edge after split");
            Check.Equal(before.IsClosedManifold ? 1 : 0, after.IsClosedManifold ? 1 : 0, $"{name}: closed manifold preserved");
            Check.Equal(before.EulerCharacteristic, after.EulerCharacteristic, $"{name}: Euler characteristic preserved");
            Check.Equal(before.ComponentCount, after.ComponentCount, $"{name}: components preserved");
            Check.Equal(0, after.MisorientedEdgeCount, $"{name}: winding consistent");

            double v0 = Fixtures.SignedVolume(PolyMesh.FromTriangles(m.Vertices, m.Triangles));
            double v1 = Fixtures.SignedVolume(outMesh);
            Check.Near(v0, v1, Math.Abs(v0) * 1e-5, $"{name}: volume unchanged (same surface)");

            // The original vertices are untouched and come first.
            bool prefix = true;
            for (int i = 0; i < m.VertexCount && prefix; i++) prefix = v[i].x == m.Vertices[i].x && v[i].y == m.Vertices[i].y && v[i].z == m.Vertices[i].z;
            Check.That(prefix, $"{name}: original vertices kept bit-exact");
        }

        private static void SplitterNoOpWhenShort()
        {
            TriMesh m = TriMesh(Fixtures.TriSphere(10));
            LongEdgeSplitter.Split(m.Vertices, m.Triangles, 10f, int.MaxValue, out Vector3[] v, out int[] t, out var st);
            Check.That(ReferenceEquals(v, m.Vertices) && ReferenceEquals(t, m.Triangles), "no-op returns the input arrays");
            Check.Equal(0, st.Rounds, "no-op rounds");
        }

        private static void SplitterRespectsVertexCap()
        {
            TriMesh m = TriMesh(Fixtures.TriSphere(10));
            LongEdgeSplitter.Split(m.Vertices, m.Triangles, 0.001f, 5000, out Vector3[] v, out _, out var st);
            Check.That(st.HitVertexCap, "cap reported");
            Check.AtMost(5000, v.Length, "cap respected");
        }

        public static void HierarchyInvariants(TriMesh m, string name)
        {
            FieldHierarchy h = FieldHierarchy.Build(m.Vertices, m.Triangles);
            FieldLevel l0 = h.Finest;
            double area0 = Sum(l0.Area);
            int components0 = Components(l0);

            Check.That(h.Levels.Length >= 2, $"{name}: has coarse levels");
            Check.AtMost(FieldHierarchy.DefaultMinVertices * 2 + 8, h.Coarsest.Count, $"{name}: coarsest small");

            for (int li = 0; li < h.Levels.Length; li++)
            {
                FieldLevel l = h.Levels[li];
                string tag = $"{name} L{li}";
                Check.Near(area0, Sum(l.Area), area0 * 1e-4, $"{tag}: area conserved");
                Check.Equal(components0, Components(l), $"{tag}: components preserved");

                bool adjOk = true, normalsOk = true, colourOk = true;
                for (int v = 0; v < l.Count && adjOk; v++)
                {
                    float nl = l.Normal[v].magnitude;
                    normalsOk &= Math.Abs(nl - 1f) < 1e-4f;
                    for (int k = l.AdjacencyStart[v]; k < l.AdjacencyStart[v + 1]; k++)
                    {
                        int u = l.Adjacency[k];
                        adjOk &= u != v && u >= 0 && u < l.Count && l.AdjacencyWeight[k] > 0;
                        // symmetric and not duplicated
                        int back = 0, dup = 0;
                        for (int j = l.AdjacencyStart[u]; j < l.AdjacencyStart[u + 1]; j++) if (l.Adjacency[j] == v) back++;
                        for (int j = l.AdjacencyStart[v]; j < l.AdjacencyStart[v + 1]; j++) if (l.Adjacency[j] == u) dup++;
                        adjOk &= back == 1 && dup == 1;
                    }
                }
                Check.That(adjOk, $"{tag}: adjacency symmetric, simple, in range");
                Check.That(normalsOk, $"{tag}: unit normals");

                var colourOf = new int[l.Count];
                var seen = new bool[l.Count];
                for (int p = 0; p < l.PhaseCount; p++)
                    for (int i = l.PhaseStart[p]; i < l.PhaseStart[p + 1]; i++)
                    {
                        int v = l.PhaseVertices[i];
                        colourOk &= !seen[v];
                        seen[v] = true;
                        colourOf[v] = p;
                    }
                for (int v = 0; v < l.Count; v++)
                {
                    colourOk &= seen[v];
                    for (int k = l.AdjacencyStart[v]; k < l.AdjacencyStart[v + 1]; k++) colourOk &= colourOf[l.Adjacency[k]] != colourOf[v];
                }
                Check.That(colourOk, $"{tag}: phases partition vertices with no edge inside a phase");

                if (li + 1 < h.Levels.Length)
                {
                    FieldLevel c = h.Levels[li + 1];
                    Check.AtMost(0.9 * l.Count, c.Count, $"{tag}: next level shrinks");
                    bool linkOk = l.ToCoarse != null;
                    for (int v = 0; v < l.Count && linkOk; v++)
                    {
                        int cv = l.ToCoarse[v];
                        linkOk &= cv >= 0 && cv < c.Count && (c.FineA[cv] == v || c.FineB[cv] == v);
                    }
                    for (int cv = 0; cv < c.Count && linkOk; cv++)
                        linkOk &= l.ToCoarse[c.FineA[cv]] == cv && (c.FineB[cv] < 0 || l.ToCoarse[c.FineB[cv]] == cv);
                    Check.That(linkOk, $"{tag}: ToCoarse / FineA / FineB consistent");

                    // Every fine edge between different clusters exists in the coarse graph.
                    bool imageOk = true;
                    for (int v = 0; v < l.Count && imageOk; v++)
                        for (int k = l.AdjacencyStart[v]; k < l.AdjacencyStart[v + 1]; k++)
                        {
                            int a = l.ToCoarse[v], b = l.ToCoarse[l.Adjacency[k]];
                            if (a == b) continue;
                            bool found = false;
                            for (int j = c.AdjacencyStart[a]; j < c.AdjacencyStart[a + 1]; j++) found |= c.Adjacency[j] == b;
                            imageOk &= found;
                        }
                    Check.That(imageOk, $"{tag}: coarse graph contains every fine link");
                }
                else Check.That(l.ToCoarse == null, $"{tag}: coarsest has no ToCoarse");
            }
        }

        private static double Sum(float[] a) { double s = 0; foreach (float x in a) s += x; return s; }

        private static int Components(FieldLevel l)
        {
            var parent = new int[l.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            for (int v = 0; v < l.Count; v++)
                for (int k = l.AdjacencyStart[v]; k < l.AdjacencyStart[v + 1]; k++)
                {
                    int a = Find(v), b = Find(l.Adjacency[k]);
                    if (a != b) parent[a] = b;
                }
            int n = 0;
            for (int v = 0; v < l.Count; v++) if (Find(v) == v) n++;
            return n;
        }
    }

    /// Step 2 on real sculpt-density meshes: conditioning + hierarchy numbers and timings.
    internal static class ConditioningRealSuite
    {
        public static void Run()
        {
            foreach (string file in new[] { "startup_sphere.obj", "gremlin_25k.obj", "lobster_187k.obj", "coat_sculpt_900k.obj" })
            {
                if (!MeshIO.TryLoadData(file, out TriMesh m)) continue;
                Report(m, 5000);
                QuadRemeshInput pulled = Report(Deform.MovePulls(m, 5), 5000);
                ConditioningSuite.HierarchyInvariants(new TriMesh { Vertices = pulled.Vertices, Triangles = pulled.Triangles }, m.Name + " prepared");
            }
        }

        /// Runs QuadRemeshInput.Prepare on `m` and prints what each stage did.
        public static QuadRemeshInput Report(TriMesh m, int targetQuads)
        {
            PolyMeshReport src = PolyMeshAnalysis.Analyze(PolyMesh.FromTriangles(m.Vertices, m.Triangles));
            QuadRemeshInput input = QuadRemeshInput.Prepare(m.Vertices, m.Triangles, targetQuads);
            PolyMeshReport prepared = PolyMeshAnalysis.Analyze(PolyMesh.FromTriangles(input.Vertices, input.Triangles));
            var s = input.Sanitize;
            var st = input.Split;
            FieldHierarchy h = input.Hierarchy;

            Console.WriteLine($"  {m.Name}: {m.TriangleCount:N0} tris, {src.ComponentCount} pieces, open edges {src.BoundaryEdgeCount}, non-manifold e/v {src.NonManifoldEdgeCount}/{src.NonManifoldVertexCount}, degenerate {src.DegenerateFaceCount}; edge max/mean x{src.MaxEdgeLength / src.MeanEdgeLength:F1}, CV {src.EdgeLengthCV:F2}");
            Console.WriteLine($"    sanitize {input.SanitizeMs:F0} ms: -{s.DegenerateTrianglesRemoved} degenerate, -{s.DuplicateTrianglesRemoved} duplicate, +{s.VerticesSplit} fan copies, -{s.ComponentsRemoved} debris pieces ({s.TrianglesInRemovedComponents} tris), {s.ComponentsKept} kept");
            Console.WriteLine($"    target {targetQuads} quads -> quad edge {input.QuadEdge:G3}, split limit {input.MaxEdge:G3}");
            Console.WriteLine($"    split {input.SplitMs:F0} ms: {st.Rounds} rounds, {st.InputVertices:N0} -> {st.OutputVertices:N0} verts; edge max/mean x{prepared.MaxEdgeLength / prepared.MeanEdgeLength:F1}, CV {prepared.EdgeLengthCV:F2}");
            Console.Write($"    hierarchy {input.HierarchyMs:F0} ms: {h.Levels.Length} levels");
            foreach (FieldLevel l in h.Levels) Console.Write($" {l.Count}");
            Console.WriteLine($"  | total {input.SanitizeMs + input.SplitMs + input.HierarchyMs:F0} ms");

            Check.AtMost(input.MaxEdge * 1.0000001, prepared.MaxEdgeLength, $"{m.Name}: split reached the limit");
            Check.Equal(0, prepared.NonManifoldEdgeCount, $"{m.Name}: no non-manifold edges");
            Check.Equal(0, prepared.NonManifoldVertexCount, $"{m.Name}: no non-manifold vertices");
            Check.Equal(0, prepared.DegenerateFaceCount, $"{m.Name}: no degenerate faces");
            Check.Equal(0, prepared.MisorientedEdgeCount, $"{m.Name}: no misoriented edges");
            Check.Equal(s.ComponentsKept, prepared.ComponentCount, $"{m.Name}: kept pieces intact");
            return input;
        }
    }
}
