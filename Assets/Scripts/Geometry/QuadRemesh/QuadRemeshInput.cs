using System;
using System.Diagnostics;
using UnityEngine;

namespace Sculpting
{
    /// The quad remesher's prepared input: the sculpt made safe (MeshSanitizer), its long edges
    /// split to the lattice scale (LongEdgeSplitter), and the field hierarchy built on top.
    /// Everything downstream - curvature, both fields, extraction - reads from this.
    ///
    /// The surface itself is never moved here, so the source sculpt and this mesh describe the
    /// same shape; the only differences are removed debris and cut-open non-manifold spots.
    internal sealed class QuadRemeshInput
    {
        public Vector3[] Vertices;
        public int[] Triangles;
        public FieldHierarchy Hierarchy;

        public int TargetQuadCount;
        public double SurfaceArea;   // of the kept surface
        public float QuadEdge;       // target quad edge length: sqrt(area / quads)
        public float MaxEdge;        // split limit used

        public MeshSanitizer.Stats Sanitize;
        public LongEdgeSplitter.Stats Split;
        public double SanitizeMs, SplitMs, HierarchyMs;

        /// Hard ceiling on the conditioned vertex count, whatever the target asks for - memory
        /// and time protection for a tiny target on a huge model or a huge one on a tiny model.
        public const int DefaultMaxVertices = 3_000_000;

        public static QuadRemeshInput Prepare(Vector3[] vertices, int[] triangles, int targetQuadCount,
                                              int maxVertices = DefaultMaxVertices)
        {
            if (targetQuadCount < 1) throw new ArgumentOutOfRangeException(nameof(targetQuadCount));
            var input = new QuadRemeshInput { TargetQuadCount = targetQuadCount };
            var sw = Stopwatch.StartNew();

            // Debris threshold from the whole model's quad size: a piece under one quad of area
            // can't hold a quad.
            double rawArea = SurfaceAreaOf(vertices, triangles);
            MeshSanitizer.Sanitize(vertices, triangles, rawArea / targetQuadCount,
                out Vector3[] v, out int[] t, out input.Sanitize);
            input.SanitizeMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            input.SurfaceArea = SurfaceAreaOf(v, t);
            input.QuadEdge = (float)Math.Sqrt(input.SurfaceArea / targetQuadCount);
            input.MaxEdge = LongEdgeSplitter.DefaultMaxEdge(input.QuadEdge, LongEdgeSplitter.MeanEdgeLength(v, t));
            LongEdgeSplitter.Split(v, t, input.MaxEdge, maxVertices, out input.Vertices, out input.Triangles, out input.Split);
            input.SplitMs = sw.Elapsed.TotalMilliseconds;

            sw.Restart();
            input.Hierarchy = FieldHierarchy.Build(input.Vertices, input.Triangles);
            input.HierarchyMs = sw.Elapsed.TotalMilliseconds;
            return input;
        }

        public static double SurfaceAreaOf(Vector3[] v, int[] t)
        {
            double area = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = v[t[i]];
                area += 0.5 * Vector3.Cross(v[t[i + 1]] - a, v[t[i + 2]] - a).magnitude;
            }
            return area;
        }
    }
}
