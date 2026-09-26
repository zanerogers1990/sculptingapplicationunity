using System;
using Sculpting;
using UnityEngine;

namespace GeometryHarness
{
    /// The basic quad remesh end to end.
    internal static class RemeshSuite
    {
        public static void Run()
        {
            Sphere(1500);
            Sphere(6000);
            TorusCase();
        }

        private static void Sphere(int target)
        {
            TriMesh m = ConditioningSuite.TriMesh(Fixtures.TriSphere(48));
            QuadRemesher.Result r = QuadRemesher.Remesh(m.Vertices, m.Triangles, target);
            PolyMeshReport rep = Print($"sphere/{target}", r);

            double maxRadiusErr = 0;
            foreach (Vector3 p in r.Mesh.Vertices) maxRadiusErr = Math.Max(maxRadiusErr, Math.Abs(p.magnitude - 1.0));
            // The input is a 48-per-face polyhedron, so its facets sit up to ~0.1% inside the sphere.
            Check.AtMost(2e-3, maxRadiusErr, $"sphere/{target}: vertices on the surface");
            Check.That(rep.IsClosedManifold, $"sphere/{target}: closed manifold");
            Check.Equal(2, rep.EulerCharacteristic, $"sphere/{target}: genus 0");
            Check.That(rep.QuadFraction > 0.9, $"sphere/{target}: >90% quads (got {rep.QuadFraction:P1})");
            Check.Near(target, rep.QuadCount, target * 0.3, $"sphere/{target}: quad count near target");
            Check.AtMost(15, rep.MeanAngleDeviationDeg, $"sphere/{target}: mean corner angle deviation");
            Check.That(Fixtures.SignedVolume(r.Mesh) > 0, $"sphere/{target}: wound outward");
            Output.Obj($"remesh_sphere_{target}", r.Mesh);
        }

        private static void TorusCase()
        {
            PolyMesh t = Fixtures.Torus(160, 60);
            TriMesh m = ConditioningSuite.TriMesh(t);
            QuadRemesher.Result r = QuadRemesher.Remesh(m.Vertices, m.Triangles, 2000);
            PolyMeshReport rep = Print("torus/2000", r);
            Check.That(rep.IsClosedManifold, "torus: closed manifold");
            Check.Equal(1, rep.Genus, "torus: genus 1");
            Check.That(rep.QuadFraction > 0.9, $"torus: >90% quads (got {rep.QuadFraction:P1})");
            Output.Obj("remesh_torus_2000", r.Mesh);
        }

        public static PolyMeshReport Print(string name, QuadRemesher.Result r)
        {
            PolyMeshReport rep = PolyMeshAnalysis.Analyze(r.Mesh);
            QuadExtractor.Stats x = r.Extraction;
            Console.WriteLine($"  {name}: {r.Summary()}");
            Console.WriteLine($"    times: prepare {r.Input.SanitizeMs + r.Input.SplitMs + r.Input.HierarchyMs:F0} ms, orientation {r.OrientationMs:F0}, position {r.PositionMs:F0}, extract {r.ExtractMs:F0}, finish {r.FinishMs:F0}");
            Console.WriteLine($"    extract: clusters {x.Clusters}, pruned {x.PrunedVertices}, links {x.Links}, faces {x.Faces} (tri {x.Triangles}, quad {x.Quads}, split {x.SplitPolygons}, outer dropped {x.OuterFacesDropped}, bad dropped {x.BadFacesDropped}, spurs {x.SpursDropped})");
            Console.WriteLine($"    cleanup: {r.Cleanup.ValenceTwoDissolved} valence-2 dissolved, {r.Cleanup.SliversRemoved} slivers, {r.Cleanup.TrianglePairsMerged} triangle pairs merged, {r.Cleanup.FragmentsRemoved} fragments removed");
            Console.WriteLine("    " + rep.Summary().Replace("\n", "\n    "));
            return rep;
        }
    }

    internal static class RemeshRealSuite
    {
        public static void Run()
        {
            foreach (var (file, target) in new[] { ("startup_sphere.obj", 2000), ("gremlin_25k.obj", 3000), ("lobster_187k.obj", 8000), ("coat_sculpt_900k.obj", 10000) })
            {
                if (!MeshIO.TryLoadData(file, out TriMesh m)) continue;
                foreach (TriMesh input in new[] { m, Deform.MovePulls(m, 5) })
                {
                    QuadRemesher.Result r = QuadRemesher.Remesh(input.Vertices, input.Triangles, target);
                    PolyMeshReport rep = RemeshSuite.Print($"{input.Name}/{target}", r);
                    Check.Equal(0, rep.NonManifoldEdgeCount, $"{input.Name}: no non-manifold edges");
                    Check.Equal(0, rep.MisorientedEdgeCount, $"{input.Name}: consistent winding");
                    PolyMeshReport src = PolyMeshAnalysis.Analyze(PolyMesh.FromTriangles(r.Input.Vertices, r.Input.Triangles));
                    Check.AtMost(src.ComponentCount, rep.ComponentCount, $"{input.Name}: no extra pieces");
                    Output.Obj($"remesh_{input.Name}_{target}", r.Mesh);
                }
            }
        }
    }
}
