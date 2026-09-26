using System;
using Sculpting;

namespace GeometryHarness
{
    /// Open edges before vs after remesh: holes the remesher added, versus holes the input had.
    internal static class HoleCheck
    {
        public static void Run()
        {
            foreach (var (file, target) in new[] { ("coat_sculpt_900k.obj", 10000), ("gremlin_25k.obj", 3000), ("lobster_187k.obj", 8000) })
            {
                if (!MeshIO.TryLoadData(file, out TriMesh m)) continue;
                QuadRemeshInput input = QuadRemeshInput.Prepare(m.Vertices, m.Triangles, target);
                PolyMeshReport before = PolyMeshAnalysis.Analyze(PolyMesh.FromTriangles(input.Vertices, input.Triangles));
                QuadRemesher.Result r = QuadRemesher.Remesh(m.Vertices, m.Triangles, target);
                PolyMeshReport after = PolyMeshAnalysis.Analyze(r.Mesh);
                // Piece sizes (faces per connected piece).
                var parent = new int[r.Mesh.VertexCount];
                for (int i = 0; i < parent.Length; i++) parent[i] = i;
                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                for (int f = 0; f < r.Mesh.FaceCount; f++)
                    for (int k = 1; k < r.Mesh.FaceSize(f); k++)
                    { int a = Find(r.Mesh.Corner(f, 0)), b = Find(r.Mesh.Corner(f, k)); if (a != b) parent[a] = b; }
                var sizes = new System.Collections.Generic.Dictionary<int, int>();
                for (int f = 0; f < r.Mesh.FaceCount; f++) { int root = Find(r.Mesh.Corner(f, 0)); sizes[root] = sizes.TryGetValue(root, out int c) ? c + 1 : 1; }
                var list = new System.Collections.Generic.List<int>(sizes.Values); list.Sort(); list.Reverse();
                Console.WriteLine($"    piece face counts: {string.Join(", ", list)}");
                Console.WriteLine($"  {m.Name}: prepared input open edges {before.BoundaryEdgeCount} (pieces {before.ComponentCount}); quad result open edges {after.BoundaryEdgeCount} (pieces {after.ComponentCount}); outer faces dropped {r.Extraction.OuterFacesDropped}, bad {r.Extraction.BadFacesDropped}");
            }
        }
    }
}
