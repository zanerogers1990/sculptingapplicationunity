using System.Diagnostics;
using NUnit.Framework;
using Sculpting.DynamicTopology;
using UnityEngine;

namespace Sculpting.Tests
{
    /// The whole justification for local remeshing is that a refine costs the BRUSH FOOTPRINT
    /// rather than the mesh. That is a claim about scaling, so it is measured at two densities an
    /// order of magnitude apart and compared, rather than asserted against an absolute millisecond
    /// budget that would differ on every machine.
    ///
    /// Run at real density on purpose. This project has a standing rule about it: constants get
    /// tuned to whatever shape the test happens to use, and a few-thousand-triangle fixture proves
    /// nothing about a model someone is actually sculpting.
    [Explicit("Builds multi-hundred-thousand-triangle meshes; run deliberately, not on every suite pass.")]
    public class DynamicTopologyPerformanceTests
    {
        private static readonly Vector3 RegionDirection = new Vector3(0.2f, 0.3f, 1f).normalized;

        private struct Measurement
        {
            public int Triangles;
            public double FirstMs;
            public double SteadyMs;
            public int TrianglesAdded;
        }

        [Test]
        public void RefineCostTracksTheFootprintNotTheMesh()
        {
            // 327,680 and 1,310,720 triangles - the two densities this project's quality battery
            // is meant to be run at.
            Measurement coarse = Measure(7);
            Measurement dense = Measure(8);

            TestContext.WriteLine(
                $"{coarse.Triangles:n0} tris: first refine {coarse.FirstMs:F1} ms, steady {coarse.SteadyMs:F1} ms, +{coarse.TrianglesAdded:n0} tris\n" +
                $"{dense.Triangles:n0} tris: first refine {dense.FirstMs:F1} ms, steady {dense.SteadyMs:F1} ms, +{dense.TrianglesAdded:n0} tris\n" +
                $"density x{(float)dense.Triangles / coarse.Triangles:F1}, steady-refine cost x{dense.SteadyMs / Mathf.Max((float)coarse.SteadyMs, 0.01f):F1}");

            // Four times the mesh must not mean four times the refine. The bound is loose on
            // purpose: one genuinely mesh-proportional step remains on this path - the index
            // buffer is re-uploaded whole, because Unity's managed SetTriangles has no
            // partial-range form (see SculptableMesh.ApplyTopologyPatch) - so the ratio is
            // expected to be above 1, just nowhere near the 4x that a mesh-bound algorithm would
            // give.
            double ratio = dense.SteadyMs / Mathf.Max((float)coarse.SteadyMs, 0.01f);
            Assert.That(ratio, Is.LessThan(3.0),
                        $"refine cost scaled x{ratio:F1} for a x4 mesh - that is mesh-bound, not footprint-bound");
        }

        private Measurement Measure(int subdivisions)
        {
            Mesh source = SymmetricTestMesh.BuildIcosphere(subdivisions, out _);
            var go = new GameObject("PerfTarget", typeof(MeshFilter), typeof(MeshRenderer));
            go.GetComponent<MeshFilter>().sharedMesh = source;
            SculptableMesh mesh = go.AddComponent<SculptableMesh>();
            if (mesh.Vertices == null) TestReflection.Invoke(mesh, "Awake");

            var settings = new DynamicTopologySettings
            {
                Enabled = true,
                // Fixed in WORLD units regardless of density, so both meshes are refined toward
                // the same target and the comparison is about mesh size alone.
                DetailSize = 0.004f,
                Iterations = 2,
                MaxOperationsPerRefine = 20000,
            };
            var remesher = new DynamicTopologyRemesher(settings);

            Vector3 centre = RegionDirection * SymmetricTestMesh.SurfaceRadius(RegionDirection);
            const float radius = 0.06f;

            int trianglesBefore = mesh.TriangleCount;

            var watch = Stopwatch.StartNew();
            RunOne(mesh, remesher, centre, radius);
            double firstMs = watch.Elapsed.TotalMilliseconds;

            // Steady state: the footprint has converged, so this is what a held brush actually pays
            // frame after frame - the number that decides whether a stroke feels smooth.
            const int steadyRuns = 8;
            watch.Restart();
            for (int i = 0; i < steadyRuns; i++) RunOne(mesh, remesher, centre, radius);
            double steadyMs = watch.Elapsed.TotalMilliseconds / steadyRuns;

            int added = mesh.TriangleCount - trianglesBefore;

            Object.DestroyImmediate(go);
            Object.DestroyImmediate(source);

            return new Measurement
            {
                Triangles = trianglesBefore,
                FirstMs = firstMs,
                SteadyMs = steadyMs,
                TrianglesAdded = added,
            };
        }

        private static void RunOne(SculptableMesh mesh, DynamicTopologyRemesher remesher, Vector3 centre, float radius)
        {
            TopologyPatch patch = remesher.Refine(mesh, centre, radius);
            if (patch != null) mesh.ApplyTopologyPatch(patch);
        }
    }
}
