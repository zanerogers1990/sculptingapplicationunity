using System;
using Sculpting;

namespace GeometryHarness
{
    /// Prints the full analysis report of every data mesh - for looking, not asserting.
    internal static class InspectSuite
    {
        public static void Run()
        {
            foreach (string file in new[] { "startup_sphere.obj", "gremlin_25k.obj", "lobster_187k.obj", "coat_sculpt_900k.obj" })
            {
                if (!MeshIO.TryLoadData(file, out TriMesh m)) continue;
                Console.WriteLine($"  --- {m.Name}");
                Console.WriteLine("  " + PolyMeshAnalysis.Analyze(PolyMesh.FromTriangles(m.Vertices, m.Triangles)).Summary().Replace("\n", "\n  "));
            }
        }
    }
}
