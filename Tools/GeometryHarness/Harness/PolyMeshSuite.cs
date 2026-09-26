using System;
using System.Globalization;
using System.IO;
using Sculpting;
using UnityEngine;

namespace GeometryHarness
{
    /// Step 1 of the quad remesher: PolyMesh, its edge table, the analysis report, the
    /// triangulator and the OBJ writer - checked against fixtures with closed-form answers.
    internal static class PolyMeshSuite
    {
        public static void Run()
        {
            QuadSphereIsTheIdealAnswer();
            TorusHasNoPoles();
            OpenGridBoundary();
            DefectsAreDetected();
            MixedFacesAndUnusedVertices();
            AngleAndEdgeMetrics();
            TriangulatorKeepsTopologyAndWinding();
            TriangulatorSplitsShorterDiagonal();
            ObjRoundTrip();
        }

        private static void QuadSphereIsTheIdealAnswer()
        {
            const int n = 8;
            PolyMesh m = Fixtures.QuadSphere(n);
            PolyMeshReport r = PolyMeshAnalysis.Analyze(m);
            Check.Equal(6 * n * n + 2, r.UsedVertexCount, "sphere V");
            Check.Equal(6 * n * n, r.QuadCount, "sphere quads");
            Check.Equal(12 * n * n, r.EdgeCount, "sphere E");
            Check.That(r.IsClosedManifold, "sphere closed manifold");
            Check.Equal(2, r.EulerCharacteristic, "sphere chi");
            Check.Equal(0, r.Genus, "sphere genus");
            Check.Equal(1, r.ComponentCount, "sphere components");
            Check.Equal(8, r.InteriorValence[3], "sphere valence-3 poles");
            Check.Equal(6 * n * n + 2 - 8, r.InteriorValence[4], "sphere valence-4");
            Check.Equal(8, r.IrregularInteriorCount, "sphere irregular");
            Check.That(Fixtures.SignedVolume(m) > 0, "sphere wound outward");
            Check.Near(1.0, r.QuadFraction, 0, "sphere quad fraction");
            Output.Obj("quadsphere", m);
        }

        private static void TorusHasNoPoles()
        {
            PolyMesh m = Fixtures.Torus(24, 12);
            PolyMeshReport r = PolyMeshAnalysis.Analyze(m);
            Check.That(r.IsClosedManifold, "torus closed manifold");
            Check.Equal(0, r.EulerCharacteristic, "torus chi");
            Check.Equal(1, r.Genus, "torus genus");
            Check.Equal(0, r.IrregularInteriorCount, "torus poles");
            Check.Equal(24 * 12, r.InteriorValence[4], "torus valence-4");
        }

        private static void OpenGridBoundary()
        {
            PolyMeshReport r = PolyMeshAnalysis.Analyze(Fixtures.Grid(5, 3));
            Check.Equal(24, r.UsedVertexCount, "grid V");
            Check.Equal(15, r.QuadCount, "grid F");
            Check.Equal(5 * 4 + 6 * 3, r.EdgeCount, "grid E");
            Check.Equal(2 * (5 + 3), r.BoundaryEdgeCount, "grid boundary edges");
            Check.That(r.IsManifold && !r.IsClosedManifold, "grid manifold with boundary");
            Check.Equal(4, r.BoundaryValence[2], "grid corners");
            Check.Equal(2 * (4 + 2), r.BoundaryValence[3], "grid boundary valence 3");
            Check.Equal(4 * 2, r.InteriorValence[4], "grid interior valence 4");
            Check.Equal(4, r.IrregularBoundaryCount, "grid irregular boundary (corners)");
            Check.Equal(0, r.IrregularInteriorCount, "grid irregular interior");
            Check.Equal(-1, r.Genus, "grid genus n/a");
        }

        private static void DefectsAreDetected()
        {
            PolyMesh sphere = Fixtures.QuadSphere(4);

            PolyMeshReport flipped = PolyMeshAnalysis.Analyze(Fixtures.FlipFace(sphere, 7));
            Check.Equal(4, flipped.MisorientedEdgeCount, "flipped face -> 4 misoriented edges");
            Check.That(!flipped.IsManifold, "flipped face not manifold");

            // Two grids pinched together at one shared corner vertex.
            PolyMesh grid = Fixtures.Grid(2, 2);
            int shared = 8; // grid corner (2, 2)
            var extra = new[] { new Vector3(3, 0, 2), new Vector3(3, 0, 3), new Vector3(2, 0, 3) };
            int v0 = grid.VertexCount;
            PolyMeshReport pinched = PolyMeshAnalysis.Analyze(Fixtures.Append(grid, extra, new[] { shared, v0 + 2, v0 + 1, v0 }));
            Check.Equal(1, pinched.NonManifoldVertexCount, "pinch -> 1 non-manifold vertex");
            Check.Equal(0, pinched.NonManifoldEdgeCount, "pinch -> no non-manifold edge");
            Check.Equal(1, pinched.ComponentCount, "pinch -> one component");

            // A fin: a third face on an interior edge (4-1 is interior on a 2x2 grid).
            PolyMeshReport fin = PolyMeshAnalysis.Analyze(Fixtures.Append(grid, new[] { new Vector3(0.5f, 1, 0.5f) }, new[] { 4, 1, grid.VertexCount }));
            Check.Equal(1, fin.NonManifoldEdgeCount, "fin -> 1 non-manifold edge");

            PolyMeshReport degenerate = PolyMeshAnalysis.Analyze(Fixtures.Append(grid, new Vector3[0], new[] { 0, 1, 0, 3 }));
            Check.Equal(1, degenerate.DegenerateFaceCount, "repeated corner -> degenerate face");
            Check.That(!degenerate.IsManifold, "degenerate face not manifold");
        }

        private static void MixedFacesAndUnusedVertices()
        {
            PolyMesh sphere = Fixtures.QuadSphere(6);
            PolyMesh mixed = Fixtures.SplitQuads(sphere, 10);
            PolyMeshReport r = PolyMeshAnalysis.Analyze(mixed);
            Check.Equal(20, r.TriangleCount, "mixed tris");
            Check.Equal(sphere.FaceCount - 10, r.QuadCount, "mixed quads");
            Check.Equal(sphere.FaceCount + 10, r.FaceCount, "mixed F");
            Check.That(r.IsClosedManifold, "mixed closed manifold");
            Check.Equal(2, r.EulerCharacteristic, "mixed chi");

            PolyMeshReport unused = PolyMeshAnalysis.Analyze(Fixtures.Append(sphere, new[] { new Vector3(5, 5, 5) }));
            Check.Equal(sphere.VertexCount, unused.UsedVertexCount, "unused vertex not counted as used");
            Check.Equal(sphere.VertexCount + 1, unused.VertexCount, "unused vertex still in V");
            Check.That(unused.IsClosedManifold, "unused vertex doesn't break manifoldness");
        }

        private static void AngleAndEdgeMetrics()
        {
            PolyMeshReport square = PolyMeshAnalysis.Analyze(Fixtures.Grid(4, 4));
            Check.Near(0, square.MeanAngleDeviationDeg, 1e-4, "square grid angle dev");
            Check.Near(1, square.CornersWithin10DegFraction, 0, "square grid within 10 deg");
            Check.Near(1, square.MeanEdgeLength, 1e-6, "square grid mean edge");
            Check.Near(0, square.EdgeLengthCV, 1e-6, "square grid edge CV");

            PolyMeshReport sheared = PolyMeshAnalysis.Analyze(Fixtures.Grid(4, 4, 30f));
            Check.Near(30, sheared.MeanAngleDeviationDeg, 1e-3, "sheared grid mean dev");
            Check.Near(30, sheared.MaxAngleDeviationDeg, 1e-3, "sheared grid max dev");
            Check.Near(0, sheared.CornersWithin10DegFraction, 0, "sheared grid within 10 deg");
        }

        private static void TriangulatorKeepsTopologyAndWinding()
        {
            PolyMesh mesh = Fixtures.SplitQuads(Fixtures.QuadSphere(6), 5);
            int[] tris = PolyMeshTriangulator.Triangulate(mesh, out int[] triFace);
            Check.Equal(PolyMeshTriangulator.TriangleCountOf(mesh), triFace.Length, "tri count");
            Check.Equal(2 * (mesh.FaceCount - 10) + 10, triFace.Length, "tri count formula");

            var perFace = new int[mesh.FaceCount];
            foreach (int f in triFace) perFace[f]++;
            bool mapOk = true;
            for (int f = 0; f < mesh.FaceCount; f++) mapOk &= perFace[f] == mesh.FaceSize(f) - 2;
            Check.That(mapOk, "every face maps to size - 2 triangles");

            PolyMesh triMesh = PolyMesh.FromTriangles(mesh.Vertices, tris);
            PolyMeshReport r = PolyMeshAnalysis.Analyze(triMesh);
            Check.That(r.IsClosedManifold, "triangulation closed manifold");
            Check.Equal(2, r.EulerCharacteristic, "triangulation chi");
            Check.That(Fixtures.SignedVolume(triMesh) > 0, "triangulation keeps winding");
        }

        private static void TriangulatorSplitsShorterDiagonal()
        {
            // a-c is 4 long, b-d is 2: the split must use b-d.
            var v = new[] { new Vector3(-2, 0, 0), new Vector3(0, 0, -1), new Vector3(2, 0, 0), new Vector3(0, 0, 1) };
            int[] tris = PolyMeshTriangulator.Triangulate(PolyMesh.FromQuads(v, new[] { 0, 1, 2, 3 }), out _);
            bool hasBD = false, hasAC = false;
            for (int t = 0; t < 2; t++)
            {
                int[] c = { tris[t * 3], tris[t * 3 + 1], tris[t * 3 + 2] };
                bool b = Array.IndexOf(c, 1) >= 0, d = Array.IndexOf(c, 3) >= 0;
                bool a = Array.IndexOf(c, 0) >= 0, cc = Array.IndexOf(c, 2) >= 0;
                hasBD |= b && d;
                hasAC |= a && cc;
            }
            Check.That(hasBD && !hasAC, "kite splits along its short diagonal");
        }

        private static void ObjRoundTrip()
        {
            PolyMesh mesh = Fixtures.SplitQuads(Fixtures.QuadSphere(2), 1);
            var sw = new StringWriter();
            PolyMeshObj.Write(sw, mesh, "rt", toRightHanded: true);
            string[] lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            int vi = 0, fi = 0;
            bool positionsOk = true, facesOk = true;
            foreach (string line in lines)
            {
                string[] tok = line.Split(' ');
                if (tok[0] == "v")
                {
                    Vector3 p = mesh.Vertices[vi++];
                    positionsOk &= Math.Abs(float.Parse(tok[1], CultureInfo.InvariantCulture) + p.x) < 1e-5f
                                && Math.Abs(float.Parse(tok[2], CultureInfo.InvariantCulture) - p.y) < 1e-5f
                                && Math.Abs(float.Parse(tok[3], CultureInfo.InvariantCulture) - p.z) < 1e-5f;
                }
                else if (tok[0] == "f")
                {
                    int n = mesh.FaceSize(fi);
                    facesOk &= tok.Length - 1 == n;
                    for (int k = 0; k < n && facesOk; k++)
                        facesOk &= int.Parse(tok[1 + k]) - 1 == mesh.Corner(fi, n - 1 - k);
                    fi++;
                }
            }
            Check.Equal(mesh.VertexCount, vi, "obj v lines");
            Check.Equal(mesh.FaceCount, fi, "obj f lines");
            Check.That(positionsOk, "obj positions X-negated");
            Check.That(facesOk, "obj faces kept as polygons, winding reversed");
        }
    }

    /// Timing of the step-1 machinery at the remesher's target size.
    internal static class PolyMeshPerfSuite
    {
        public static void Run()
        {
            const int n = 365; // 6 * 365^2 = 799,350 quads
            PolyMesh m = Fixtures.QuadSphere(n);
            PolyMeshReport r = null;
            double edgesMs = Bench.MinMs(3, () => PolyMeshEdges.Build(m));
            double analyzeMs = Bench.MinMs(3, () => r = PolyMeshAnalysis.Analyze(m));
            double triMs = Bench.MinMs(3, () => PolyMeshTriangulator.Triangulate(m, out _));
            Console.WriteLine($"  {m.FaceCount:N0} quads: edges {edgesMs:F0} ms, full analysis {analyzeMs:F0} ms, triangulate {triMs:F0} ms");
            Check.Equal(8, r.IrregularInteriorCount, "800k sphere poles");
            Check.That(r.IsClosedManifold, "800k sphere closed manifold");
            Check.AtMost(3000, analyzeMs, "800k analysis ms");
        }
    }
}
