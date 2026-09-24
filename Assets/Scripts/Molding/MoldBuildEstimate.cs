using UnityEngine;

namespace Sculpting.Molding
{
    /// What a build at a given resolution will actually cost and actually resolve.
    ///
    /// Exists because "resolution" on its own is not a number anyone can reason about here. It
    /// counts cells along the longest axis of the mold BLOCK, so the same value means wildly
    /// different things depending on the model's proportions: 512 puts 27 cells across a lure
    /// 0.12 thick but 512 across a sphere of the same length. The detail that has to transfer
    /// lives on the model's SHORT axis and at the scale of its own triangles, and neither is
    /// visible in the slider. So the panel shows this instead of the raw count.
    ///
    /// Everything here is closed-form arithmetic on box dimensions - no sampling, no allocation -
    /// so it is cheap enough to recompute while a slider is being dragged.
    public readonly struct MoldBuildEstimate
    {
        public readonly int Resolution;
        /// World size of one voxel. The cavity's feature-size floor: detail finer than this
        /// rounds away, whatever the model has.
        public readonly float CellSize;
        public readonly long Samples;
        /// Peak transient allocation for one half, in GB. See MeshBoolean.MaxGridSamples for
        /// the 10-bytes-per-sample breakdown.
        public readonly float PeakGigabytes;
        public readonly int TrianglesPerHalf;

        /// Cells across the model's thinnest extent - for a flat model this is the number that
        /// decides whether the cavity is a shape or a slab, and the one the resolution slider
        /// says nothing about.
        public readonly float CellsAcrossThinnestAxis;

        /// Cells per mean source triangle edge. Below 1 the voxel grid is coarser than the
        /// model's own tessellation, so sculpted detail is being discarded before it can reach
        /// the cavity - raising the resolution is the only thing that will help. Far above 1
        /// the extra cells only resolve the model's faceting more smoothly, at four times the
        /// samples per doubling, and a denser source mesh is what would actually help.
        public readonly float CellsPerSourceEdge;

        public bool StarvedBySource => CellsPerSourceEdge < 1f;
        public bool WastingCells => CellsPerSourceEdge > 3f;

        private MoldBuildEstimate(int resolution, float cellSize, long samples, float peakGb,
                                  int trisPerHalf, float acrossThin, float perEdge)
        {
            Resolution = resolution;
            CellSize = cellSize;
            Samples = samples;
            PeakGigabytes = peakGb;
            TrianglesPerHalf = trisPerHalf;
            CellsAcrossThinnestAxis = acrossThin;
            CellsPerSourceEdge = perEdge;
        }

        /// `modelArea` and `meanTriangleEdge` describe the source mesh; both come from
        /// MeasureSource. `block` is the whole mold, and one half is taken as its bottom or top
        /// portion - close enough for an estimate, since the parting sheet sits near the middle.
        public static MoldBuildEstimate For(in MoldBlock block, float modelArea, float meanTriangleEdge,
                                            int resolution)
        {
            resolution = Mathf.Max(4, resolution);

            float w = Mathf.Max(block.Width, 1e-5f);
            float d = Mathf.Max(block.Depth, 1e-5f);
            float h = Mathf.Max(0.5f * block.Height, 1e-5f);

            float longest = Mathf.Max(w, Mathf.Max(d, h));
            float cell = longest / resolution;

            // Matches MeshRemesher.GridDimensions: a cell count per axis plus padding cells.
            long sx = Mathf.CeilToInt(w / cell) + 5;
            long sy = Mathf.CeilToInt(h / cell) + 5;
            long sz = Mathf.CeilToInt(d / cell) + 5;
            long samples = sx * sy * sz;

            // Surface Nets puts about one vertex in every cell the surface crosses and about
            // two triangles per vertex, so the output scales with area over cell squared. The
            // half's area is its two big faces, its four sides, and its share of the cavity.
            // Checked against a real draft build: this predicts 95k triangles where the app
            // produced 91k and 105k for the two halves.
            float area = 2f * w * d + 2f * (w + d) * h + 0.5f * Mathf.Max(modelArea, 0f);
            float tris = 2f * area / Mathf.Max(cell * cell, 1e-12f);

            Vector3 extent = block.ModelExtent;
            float thinnest = Mathf.Max(Mathf.Min(extent.x, Mathf.Min(extent.y, extent.z)), 1e-6f);

            return new MoldBuildEstimate(
                resolution, cell, samples,
                samples * 10f / 1073741824f,
                (int)Mathf.Min(tris, int.MaxValue),
                thinnest / cell,
                meanTriangleEdge > 1e-9f ? meanTriangleEdge / cell : 0f);
        }

        /// The resolution at which one voxel is `cellsPerEdge` cells per source triangle edge -
        /// the point where the cavity stops discarding detail the model actually has.
        ///
        /// This is what the panel's "Match Model" does. It is the only principled resolution
        /// available: below it real sculpted detail is lost, and above it the extra cells are
        /// resolving triangles rather than shapes.
        public static int MatchModelResolution(in MoldBlock block, float meanTriangleEdge,
                                               float cellsPerEdge = 1.5f)
        {
            if (meanTriangleEdge <= 1e-9f) return 256;

            float longest = Mathf.Max(block.Width, Mathf.Max(block.Depth, 0.5f * Mathf.Max(block.Height, 1e-5f)));
            return Mathf.Clamp(Mathf.RoundToInt(cellsPerEdge * longest / meanTriangleEdge), 64, 2048);
        }

        /// Total area and mean triangle edge of a world-space triangle soup, in one pass.
        ///
        /// The mean EDGE rather than the mean area: it is the length the cell size has to be
        /// compared against, and taking it directly avoids assuming the triangles are anywhere
        /// near equilateral (a decimated import is full of slivers that would make an
        /// area-derived edge far too optimistic).
        public static void MeasureSource(Vector3[] verts, int[] tris, int triangleCount,
                                         out float area, out float meanEdge)
        {
            area = 0f;
            meanEdge = 0f;
            if (verts == null || tris == null || triangleCount <= 0) return;

            int limit = Mathf.Min(triangleCount * 3, tris.Length);

            // Strided sample on a dense mesh: a few thousand triangles pin both averages far
            // tighter than the slider's granularity needs, and this runs on a settings change.
            int stride = Mathf.Max(1, triangleCount / 4000);
            double areaSum = 0.0, edgeSum = 0.0;
            int counted = 0;

            for (int t = 0; t + 2 < limit; t += 3 * stride)
            {
                int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                if ((uint)ia >= verts.Length || (uint)ib >= verts.Length || (uint)ic >= verts.Length) continue;

                Vector3 a = verts[ia], b = verts[ib], c = verts[ic];
                areaSum += 0.5f * Vector3.Cross(b - a, c - a).magnitude;
                edgeSum += ((b - a).magnitude + (c - b).magnitude + (a - c).magnitude) / 3.0;
                counted++;
            }

            if (counted == 0) return;
            // Scale the sampled area back up to the whole mesh; the edge is already a mean.
            area = (float)(areaSum / counted) * triangleCount;
            meanEdge = (float)(edgeSum / counted);
        }
    }
}
