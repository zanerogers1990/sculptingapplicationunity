using System;
using System.IO;
using Sculpting;
using UnityEngine;

namespace GeometryHarness
{
    /// Renders remesh results to out/*.png for eyeballing (red edges = non-quad faces).
    internal static class ViewSuite
    {
        public static void Run()
        {
            string dir = Output.ObjDirectory ?? "out";
            foreach (var (file, target, pulls) in new[] { ("coat_sculpt_900k.obj", 10000, 0), ("lobster_187k.obj", 8000, 0), ("startup_sphere.obj", 2000, 5) })
            {
                if (!MeshIO.TryLoadData(file, out TriMesh m)) continue;
                if (pulls > 0) m = Deform.MovePulls(m, pulls);
                QuadRemesher.Result r = QuadRemesher.Remesh(m.Vertices, m.Triangles, target);
                Console.WriteLine($"  {m.Name}: {r.Summary()}");
                string name = Path.Combine(dir, $"view_{m.Name}_{target}");
                Render.Png(name + "_a.png", r.Mesh, 1000, 30f, 15f);
                Render.Png(name + "_b.png", r.Mesh, 1000, 210f, 15f);
                Render.Png(name + "_zoom.png", r.Mesh, 1000, 30f, 15f, 3f);
                Render.Png(name + "_src.png", PolyMesh.FromTriangles(m.Vertices, m.Triangles), 1000, 30f, 15f);
                Console.WriteLine($"    wrote {name}_*.png");
            }
        }
    }
}
