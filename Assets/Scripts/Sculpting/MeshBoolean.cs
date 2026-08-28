using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Which way two solids are combined by MeshBoolean. Subtract is the one the UI exposes
    /// (cut a shape out of another - the operation 3D-printable parts and two-part molds are
    /// actually made of); Union and Intersect fall out of the same field combination for
    /// nothing extra and are here for callers that want them.
    public enum BooleanOp
    {
        /// Target minus every cutter - max(target, -cutter).
        Subtract,
        /// Everything either solid covers - min(target, cutter). Unlike MeshJoiner (which
        /// concatenates shells and leaves the interior walls inside the result), this welds
        /// them into one watertight surface with no geometry left in the overlap.
        Union,
        /// Only what all the solids share - max(target, cutter).
        Intersect,
    }

    /// Voxel CSG: samples every operand's signed distance field onto ONE shared grid, combines
    /// those fields with min/max, and re-extracts a surface from the result.
    ///
    /// Deliberately the same machinery as MeshRemesher rather than an exact/BSP boolean, for
    /// the same reason DynaMesh works this way: sculpted meshes self-intersect, have
    /// near-degenerate triangles and non-manifold pinches, and an exact boolean has to fail on
    /// those, while a field-based one just resamples whatever solid the input describes. The
    /// output is watertight and evenly tessellated - which is what a mesh headed for a slicer
    /// needs - at the cost of being resampled everywhere (the whole target is rebuilt at grid
    /// resolution, not just the cut region) and of rounding features finer than one cell.
    ///
    /// Everything here is pure geometry on plain arrays - no scene, no components - so it is
    /// testable outside Unity (see the shim harness in reference_dotnet_shim_harness) and can
    /// be reused for a cutter that is not a scene object. MeshBooleanTool is the scene-level
    /// half: transforms, undo, selection.
    public static class MeshBoolean
    {
        /// One solid taking part in the operation, already expressed in the TARGET's local
        /// space (MeshBooleanTool does that transform - the boolean itself has no opinion about
        /// where objects sit in the world).
        public readonly struct Operand
        {
            public readonly Vector3[] Vertices;
            public readonly int[] Triangles;
            public readonly string Name;

            public Operand(Vector3[] vertices, int[] triangles, string name)
            {
                Vertices = vertices;
                Triangles = triangles;
                Name = name;
            }

            public bool IsValid => Vertices != null && Vertices.Length > 0 && Triangles != null && Triangles.Length >= 3;
        }

        /// Samples ~1.5GB of float+bool grid at the very top end, which is already more than
        /// this is worth; past it the honest answer is "lower the resolution", not an
        /// out-of-memory crash halfway through with the user's mesh already snapshotted for
        /// undo. Checked before anything is allocated so the failure is clean.
        private const long MaxGridSamples = 96L * 1000 * 1000;

        /// Runs `op` between `targetVertices/Triangles` and every operand, returning a new mesh
        /// in the target's local space - or null with `error` set, in which case the caller must
        /// leave the target alone. Blocks until finished, like MeshRemesher.Remesh.
        ///
        /// `resolution` is voxels along the TARGET's longest bounding-box axis, matching what
        /// the Remesh Resolution slider means everywhere else in the app. That is also the
        /// feature-size floor: a cutter detail thinner than one cell rounds away, so cutting
        /// fine detail out of a big block wants a higher number than remeshing the block would.
        public static Mesh Build(Vector3[] targetVertices, int[] targetTriangles, IReadOnlyList<Operand> operands, BooleanOp op, int resolution, out string error)
        {
            error = null;
            if (targetVertices == null || targetVertices.Length == 0 || targetTriangles == null || targetTriangles.Length < 3)
            {
                error = "the target has no geometry";
                return null;
            }
            if (operands == null || operands.Count == 0)
            {
                error = "no second object to combine with";
                return null;
            }

            resolution = Mathf.Clamp(resolution, 4, 512);

            Bounds targetBounds = MeshRemesher.ComputeBounds(targetVertices);
            float targetExtent = Mathf.Max(targetBounds.size.x, targetBounds.size.y, targetBounds.size.z, 0.0001f);
            float cellSize = targetExtent / resolution;

            // Subtract and Intersect can only ever REMOVE material from the target, so their
            // result is contained in the target's own bounds and the grid needs to cover
            // nothing more - a cutter ten times the target's size costs exactly what remeshing
            // the target costs. Union genuinely adds material, so it has to cover everything.
            // (Sampling a cutter that extends past the grid is fine either way: its inside/
            // outside rays start from its own bounds, not the grid's - see
            // SignedDistanceField.ComputeColumn.)
            Bounds gridBounds = targetBounds;
            bool anyOverlap = false;
            int usable = 0;
            for (int i = 0; i < operands.Count; i++)
            {
                if (!operands[i].IsValid) continue;
                usable++;
                Bounds b = MeshRemesher.ComputeBounds(operands[i].Vertices);
                if (b.Intersects(targetBounds)) anyOverlap = true;
                if (op == BooleanOp.Union) gridBounds.Encapsulate(b);
            }

            if (usable == 0)
            {
                error = "no second object to combine with";
                return null;
            }
            if (!anyOverlap && op != BooleanOp.Union)
            {
                // Bailing out beats "succeeding" here: with no overlap the op is a no-op that
                // would still resample (Subtract) or erase (Intersect) the target, and the user
                // would be left wondering which of those they got. Bounds are conservative -
                // overlapping bounds with no overlapping solid still just resamples, which is
                // the honest outcome for "they touch but do not intersect".
                error = op == BooleanOp.Subtract
                    ? "the cutter does not overlap the target - nothing to subtract"
                    : "the objects do not overlap - the intersection would be empty";
                return null;
            }

            Vector3Int dims = MeshRemesher.GridDimensions(gridBounds, cellSize, out Vector3 origin);
            int sx = dims.x + 1, sy = dims.y + 1, sz = dims.z + 1;
            long sampleCount = (long)sx * sy * sz;
            if (sampleCount > MaxGridSamples)
            {
                // Report the resolution that WOULD fit rather than just refusing: the number
                // the user has to pick depends on the bounds, which they cannot see.
                int suggestion = Mathf.Max(4, Mathf.FloorToInt(resolution * Mathf.Pow(MaxGridSamples / (float)sampleCount, 1f / 3f)));
                error = $"resolution {resolution} needs too much memory for these bounds - try {suggestion} or lower";
                return null;
            }

            var sdf = new float[sampleCount];
            MeshRemesher.SampleSignedField(targetVertices, targetTriangles, origin, cellSize, sx, sy, sz, sdf);

            // One reused buffer for the operands rather than one per operand: fields are folded
            // in one at a time, and at these sizes a second allocation per cutter is real money.
            var operandSdf = new float[sampleCount];
            for (int i = 0; i < operands.Count; i++)
            {
                if (!operands[i].IsValid) continue;
                MeshRemesher.SampleSignedField(operands[i].Vertices, operands[i].Triangles, origin, cellSize, sx, sy, sz, operandSdf);
                Combine(sdf, operandSdf, op, sx, sy, sz);
            }

            Mesh mesh = MeshRemesher.BuildFromSdf(sdf, dims, origin, cellSize);
            if (mesh == null || mesh.vertexCount == 0)
            {
                Object.Destroy(mesh);
                error = op == BooleanOp.Subtract
                    ? "the cutter removed the whole target - nothing left"
                    : "the result is empty";
                return null;
            }

            return mesh;
        }

        /// Folds `b` into `a` in place. The three ops are the standard constructive-solid-
        /// geometry combinations of two distance fields: negating a field swaps its inside for
        /// its outside, so subtraction is just intersection with the cutter's complement.
        ///
        /// max/min of two true distance fields is only an APPROXIMATE distance near the seam
        /// where both surfaces meet (it under/over-estimates within about one cell of the
        /// crease). That is exactly where the sign is still correct, though, and Surface Nets
        /// only ever reads these values to place a crossing between two corners of one cell -
        /// so the seam lands in the right cell with the right topology, just with a slightly
        /// blunted crease. Sharpening it would mean a dual-contouring pass with QEF corner
        /// placement, which is a much bigger change than this feature needs.
        ///
        /// Parallel across z-slices, each writing a disjoint block, matching how every other
        /// grid pass in MeshRemesher is split.
        private static void Combine(float[] a, float[] b, BooleanOp op, int sx, int sy, int sz)
        {
            int sliceStride = sx * sy;
            System.Threading.Tasks.Parallel.For(0, sz, z =>
            {
                int start = z * sliceStride;
                int end = start + sliceStride;
                switch (op)
                {
                    case BooleanOp.Subtract:
                        for (int i = start; i < end; i++) a[i] = Mathf.Max(a[i], -b[i]);
                        break;
                    case BooleanOp.Union:
                        for (int i = start; i < end; i++) a[i] = Mathf.Min(a[i], b[i]);
                        break;
                    default:
                        for (int i = start; i < end; i++) a[i] = Mathf.Max(a[i], b[i]);
                        break;
                }
            });
        }
    }
}
