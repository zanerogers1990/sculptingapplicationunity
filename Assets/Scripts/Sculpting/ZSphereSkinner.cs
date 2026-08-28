using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Turns a ZSphereRig into a mesh - the "skinning" step, and the part of the feature that
    /// actually earns the ZSphere workflow its reputation.
    ///
    /// Approach: build an analytic signed distance field out of the rig, then extract its
    /// isosurface with the same Surface Nets pass MeshRemesher already uses for Remesh (see
    /// MeshRemesher.BuildFromSdf). That single decision buys most of the requirements at once,
    /// and is why this is a field-based skinner rather than the tube-extrusion-plus-stitching
    /// one the description "generate tubes between spheres" first suggests:
    ///
    ///   - Tubes between spheres: each parent-child link is a ROUND CONE - the exact distance
    ///     to the convex hull of the two spheres - so a link with a fat parent and a thin child
    ///     is a smoothly tapered tube by construction, not an approximation of one.
    ///   - Blending at joints: primitives are unioned with a smooth minimum, so where several
    ///     limbs meet, the surfaces fair into each other with a fillet instead of creasing.
    ///     Explicit tube geometry would need every joint stitched by hand, and three-plus-way
    ///     joints are exactly where hand-stitched tube skinners produce their worst artefacts.
    ///   - Manifold output: an isosurface of a continuous field cannot self-intersect, however
    ///     tangled the rig is, and PatchHoles (inside BuildFromSdf) closes the rare cell-level
    ///     ambiguity Surface Nets can leave. Overlapping tubes are the normal case in a ZSphere
    ///     blockout, and they would be the FAILURE case for an extrusion skinner.
    ///   - Mostly-quads and adaptive topology: Surface Nets emits one vertex per active cell
    ///     and stitches quads between them, so the output is an even, quad-derived grid whose
    ///     density is set by Resolution rather than by how many spheres were placed.
    ///   - Volume preservation: the final fairing pass is Taubin, not Laplacian, so it removes
    ///     voxel stair-stepping without shrinking the limbs the radii just specified (see
    ///     MeshExtractor.Smooth, shared with this class).
    ///
    /// Runs synchronously on the calling thread (sampling itself is parallel across z-slices)
    /// - same contract as MeshRemesher.Remesh, and for the same reason: it feeds a live preview
    /// the user is dragging, so a finished mesh on return is worth more than a background job.
    public static class ZSphereSkinner
    {
        public struct SkinSettings
        {
            /// Voxels along the rig's longest bounding-box axis. Low is blocky, high is smooth -
            /// the artist-facing resolution control.
            public int Resolution;

            /// Ignore Resolution and derive one from the rig itself, so the SMALLEST sphere
            /// present still gets enough voxels across it to survive. A blockout whose fingers
            /// are a tenth the torso's radius needs a much finer grid than one big torso does,
            /// and that ratio is knowable without asking the user.
            public bool AdaptiveResolution;

            /// How wide the fillet at a joint is, as a fraction of the local primitive's own
            /// radius. 0 gives a hard crease at every junction (a plain union); ~0.5 is the
            /// organic ZSphere look.
            public float Blend;

            /// Taubin fairing iterations over the extracted surface.
            public int Smoothing;

            public static SkinSettings Default => new SkinSettings
            {
                Resolution = 64,
                AdaptiveResolution = true,
                Blend = 0.45f,
                Smoothing = 3
            };
        }

        public const int MinResolution = 12;
        public const int MaxResolution = 256;

        /// Ceiling on total grid samples, independent of Resolution. Resolution alone is a poor
        /// guard because it counts along ONE axis: a long thin rig (a snake) at resolution 200
        /// is a modest grid, while a compact one at the same number is 8M+ samples. Skinning
        /// blocks the main thread, so an accidental slider drag must not be able to lock the app
        /// up for a minute - the resolution is quietly walked down to fit instead. 12M samples is
        /// roughly a second of sampling on a normal machine.
        private const int MaxGridSamples = 12_000_000;

        /// Padding around the rig's own bounds, in cells, so the surface always closes inside the
        /// grid. Larger than MeshRemesher's 2 because the smooth union pushes the surface OUTWARD
        /// past the primitives that generated it, by up to roughly the blend radius.
        private const int GridPadCells = 3;

        /// Aimed-for voxels across the smallest sphere's RADIUS when AdaptiveResolution is on.
        /// Below about 2 a sphere starts reading as a cube; much above it costs resolution
        /// everywhere for detail only the thinnest limb can use.
        private const float AdaptiveCellsPerMinRadius = 2.5f;

        /// A tapered capsule between two rig spheres, or (when Kind is Sphere) a single sphere.
        /// Precomputed once per skin so the inner sampling loop does no rig lookups.
        private readonly struct Primitive
        {
            public readonly Vector3 A, B;
            public readonly float RA, RB;
            /// Fillet width for THIS primitive's contribution to the union, in world units - see
            /// SmoothUnion. Scaled off the primitive's own size rather than a single global
            /// number so a thick torso joint blends wide while a finger joint blends tight,
            /// which is what makes multi-scale rigs look right.
            public readonly float BlendK;
            public readonly bool IsSphere;

            public Primitive(Vector3 a, Vector3 b, float ra, float rb, float blendK, bool isSphere)
            {
                A = a; B = b; RA = ra; RB = rb; BlendK = blendK; IsSphere = isSphere;
            }

            public float MaxRadius => Mathf.Max(RA, RB);
        }

        /// Skins `rig` into a fresh Mesh in RIG-LOCAL space. Returns null with `error` set when
        /// there is nothing to skin; `error` is null on success.
        public static Mesh Skin(ZSphereRig rig, SkinSettings settings, out int triangleCount, out string error)
        {
            triangleCount = 0;
            error = null;

            if (rig == null || rig.IsEmpty)
            {
                error = "No ZSpheres placed.";
                return null;
            }

            List<Primitive> primitives = BuildPrimitives(rig, Mathf.Max(0f, settings.Blend));
            if (primitives.Count == 0)
            {
                error = "No ZSpheres placed.";
                return null;
            }

            // Bounds must cover the SMOOTHED surface, which bulges past the primitives by about
            // the blend radius - not just the spheres themselves.
            float maxBlend = 0f;
            for (int i = 0; i < primitives.Count; i++) maxBlend = Mathf.Max(maxBlend, primitives[i].BlendK);

            Bounds bounds = rig.ComputeBounds();
            bounds.Expand(maxBlend * 2f);

            float maxExtent = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.0001f);
            int resolution = ResolveResolution(rig, settings, maxExtent);
            float cellSize = maxExtent / resolution;

            Vector3Int dims = new Vector3Int(
                Mathf.CeilToInt(bounds.size.x / cellSize) + GridPadCells * 2,
                Mathf.CeilToInt(bounds.size.y / cellSize) + GridPadCells * 2,
                Mathf.CeilToInt(bounds.size.z / cellSize) + GridPadCells * 2);

            // Walk the grid back down if this rig's aspect ratio blew the sample budget - see
            // MaxGridSamples. Iterative rather than solved in closed form because dims round up
            // per axis and the pad is a fixed cell count, so one division does not land exactly.
            while ((long)(dims.x + 1) * (dims.y + 1) * (dims.z + 1) > MaxGridSamples && resolution > MinResolution)
            {
                resolution = Mathf.Max(MinResolution, Mathf.FloorToInt(resolution * 0.8f));
                cellSize = maxExtent / resolution;
                dims = new Vector3Int(
                    Mathf.CeilToInt(bounds.size.x / cellSize) + GridPadCells * 2,
                    Mathf.CeilToInt(bounds.size.y / cellSize) + GridPadCells * 2,
                    Mathf.CeilToInt(bounds.size.z / cellSize) + GridPadCells * 2);
            }

            Vector3 origin = bounds.min - Vector3.one * (GridPadCells * cellSize);
            int sx = dims.x + 1, sy = dims.y + 1, sz = dims.z + 1;

            float[] sdf = SampleField(primitives, origin, cellSize, sx, sy, sz);

            Mesh mesh = MeshRemesher.BuildFromSdf(sdf, dims, origin, cellSize);
            if (mesh == null || mesh.vertexCount == 0)
            {
                if (mesh != null) Object.Destroy(mesh);
                error = "Skinning produced no surface - try a higher resolution.";
                return null;
            }

            int[] tris = mesh.triangles;
            if (settings.Smoothing > 0)
            {
                Vector3[] verts = mesh.vertices;
                MeshExtractor.Smooth(verts, tris, settings.Smoothing);
                mesh.vertices = verts;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
            }

            mesh.name = "ZSphere Skin";
            triangleCount = tris.Length / 3;
            return mesh;
        }

        /// The resolution a skin will run at, given these settings - exposed so the UI can show
        /// it before the user commits to a slow one, since Adaptive can pick a much higher number
        /// than the slider shows. An ESTIMATE: it approximates the blend-driven bounds expansion
        /// from the rig's mean radius rather than rebuilding every primitive, and it does not
        /// model the MaxGridSamples walk-down, so a very lopsided rig can end up skinning one or
        /// two steps coarser than this reports.
        public static int PreviewResolution(ZSphereRig rig, SkinSettings settings)
        {
            if (rig == null || rig.IsEmpty) return Mathf.Clamp(settings.Resolution, MinResolution, MaxResolution);
            Bounds bounds = rig.ComputeBounds();
            bounds.Expand(Mathf.Max(0f, settings.Blend) * rig.MeanRadius() * 2f);
            float maxExtent = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.0001f);
            return ResolveResolution(rig, settings, maxExtent);
        }

        private static int ResolveResolution(ZSphereRig rig, SkinSettings settings, float maxExtent)
        {
            int requested = Mathf.Clamp(settings.Resolution, MinResolution, MaxResolution);
            if (!settings.AdaptiveResolution) return requested;

            float minRadius = rig.MinRadius();
            if (minRadius <= 0f) return requested;

            // Cells small enough that the thinnest sphere spans AdaptiveCellsPerMinRadius of
            // them. Never goes BELOW the slider - Adaptive exists to rescue detail the slider
            // would lose, not to override a deliberately coarse, blocky look.
            int needed = Mathf.CeilToInt(maxExtent / (minRadius / AdaptiveCellsPerMinRadius));
            return Mathf.Clamp(Mathf.Max(requested, needed), MinResolution, MaxResolution);
        }

        // --------------------------------------------------------------------- field building

        private static List<Primitive> BuildPrimitives(ZSphereRig rig, float blend)
        {
            var primitives = new List<Primitive>();

            for (int i = 0; i < rig.Count; i++)
            {
                if (!rig.IsAlive(i)) continue;
                ZSphereRig.Node node = rig.Nodes[i];

                // Every node contributes its own sphere, even though a link's round cone already
                // includes both endcaps. Cheap, and it is what makes a lone root sphere (or a
                // node whose parent was deleted) skin into something instead of vanishing.
                primitives.Add(new Primitive(node.Position, node.Position, node.Radius, node.Radius,
                                             blend * node.Radius, isSphere: true));

                if (!rig.IsAlive(node.Parent)) continue;
                ZSphereRig.Node parent = rig.Nodes[node.Parent];
                primitives.Add(new Primitive(parent.Position, node.Position, parent.Radius, node.Radius,
                                             blend * Mathf.Max(parent.Radius, node.Radius), isSphere: false));
            }

            return primitives;
        }

        /// Fills the sample grid with the smooth union of every primitive.
        ///
        /// SCATTERS per primitive rather than gathering per sample: the obvious loop (for each
        /// sample, min over every primitive) is O(samples x primitives), which for a 60-sphere
        /// creature at a useful resolution is tens of millions of round-cone evaluations, most of
        /// them against a limb on the far side of the body. Each primitive instead writes only
        /// into its own bounding box, so total work scales with the volume the rig actually
        /// occupies rather than with the product of the two counts.
        ///
        /// This stays EXACT rather than being an approximation, which is the part worth being
        /// careful about. SmoothUnion(a, b, k) differs from plain min(a, b) only where the two
        /// distances are within k of each other, so a primitive can only influence a sample it is
        /// within k of "winning". Expanding each box by (k + 2 cells) past the primitive's surface
        /// therefore covers every sample whose value could change - beyond that the sample is
        /// either already claimed by something nearer (so unchanged) or is more than two cells
        /// outside everything, where Surface Nets reads the sign and nothing else.
        ///
        /// Parallelised over z-SLICES, not over primitives: a slice is written by exactly one
        /// thread, so no two threads ever touch the same cell. Doing it the other way round would
        /// race on every cell where two primitives overlap - which for a ZSphere rig is every
        /// interesting cell in the grid.
        private static float[] SampleField(List<Primitive> primitives, Vector3 origin, float cellSize, int sx, int sy, int sz)
        {
            var sdf = new float[sx * sy * sz];

            // Never read for anything but a sign check outside the scattered boxes (see above),
            // so the magnitude only has to be unambiguously "far outside".
            const float FarSentinel = 1e6f;
            for (int i = 0; i < sdf.Length; i++) sdf[i] = FarSentinel;

            // Per-primitive sample-index ranges, precomputed so each z-slice can skip primitives
            // that do not reach it with three integer comparisons instead of a bounds rebuild.
            int count = primitives.Count;
            var lo = new Vector3Int[count];
            var hi = new Vector3Int[count];
            for (int p = 0; p < count; p++)
            {
                Primitive prim = primitives[p];
                float reach = prim.MaxRadius + prim.BlendK + 2f * cellSize;
                Vector3 min = Vector3.Min(prim.A, prim.B) - Vector3.one * reach;
                Vector3 max = Vector3.Max(prim.A, prim.B) + Vector3.one * reach;
                lo[p] = ClampToGrid(min, origin, cellSize, sx, sy, sz, floor: true);
                hi[p] = ClampToGrid(max, origin, cellSize, sx, sy, sz, floor: false);
            }

            System.Threading.Tasks.Parallel.For(0, sz, z =>
            {
                for (int p = 0; p < count; p++)
                {
                    if (z < lo[p].z || z > hi[p].z) continue;
                    Primitive prim = primitives[p];
                    float wz = origin.z + z * cellSize;

                    for (int y = lo[p].y; y <= hi[p].y; y++)
                    {
                        float wy = origin.y + y * cellSize;
                        int rowBase = sx * (y + sy * z);
                        for (int x = lo[p].x; x <= hi[p].x; x++)
                        {
                            var sample = new Vector3(origin.x + x * cellSize, wy, wz);
                            float d = prim.IsSphere
                                ? (sample - prim.A).magnitude - prim.RA
                                : SdRoundCone(sample, prim.A, prim.B, prim.RA, prim.RB);

                            int idx = rowBase + x;
                            sdf[idx] = SmoothUnion(sdf[idx], d, prim.BlendK);
                        }
                    }
                }
            });

            return sdf;
        }

        private static Vector3Int ClampToGrid(Vector3 world, Vector3 origin, float cellSize, int sx, int sy, int sz, bool floor)
        {
            Vector3 local = (world - origin) / cellSize;
            int x = floor ? Mathf.FloorToInt(local.x) : Mathf.CeilToInt(local.x);
            int y = floor ? Mathf.FloorToInt(local.y) : Mathf.CeilToInt(local.y);
            int z = floor ? Mathf.FloorToInt(local.z) : Mathf.CeilToInt(local.z);
            return new Vector3Int(
                Mathf.Clamp(x, 0, sx - 1),
                Mathf.Clamp(y, 0, sy - 1),
                Mathf.Clamp(z, 0, sz - 1));
        }

        /// Polynomial smooth minimum. Where the two distances are more than k apart this is
        /// exactly min(a, b); within k it interpolates and subtracts a quadratic bulge, which is
        /// the fillet that makes a junction of tubes read as one organic form rather than as
        /// intersecting pipes.
        ///
        /// Not associative, so the result depends slightly on the order primitives are unioned
        /// in. That is standard for blobby unions and invisible in practice at these blend
        /// widths - and the alternative (an exact n-way blend) has no closed form.
        private static float SmoothUnion(float a, float b, float k)
        {
            if (k <= 0f) return Mathf.Min(a, b);
            float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
            return Mathf.Lerp(b, a, h) - k * h * (1f - h);
        }

        /// Exact signed distance to a round cone - the convex hull of sphere(a, r1) and
        /// sphere(b, r2) - after Inigo Quilez's branch-per-region formulation. This is the
        /// tapered tube: it is a true distance field, so the smooth union above blends it
        /// predictably, and the taper between two different radii is the exact conical surface
        /// tangent to both spheres rather than a lerped tube radius (which would leave a visible
        /// kink where the tube meets each sphere).
        private static float SdRoundCone(Vector3 p, Vector3 a, Vector3 b, float r1, float r2)
        {
            Vector3 ba = b - a;
            float l2 = Vector3.Dot(ba, ba);
            float rr = r1 - r2;
            float a2 = l2 - rr * rr;

            // Degenerate cases the formula divides by zero on, or has no cone for: coincident
            // centres, and one sphere swallowing the other (|r1-r2| >= |b-a|, so the hull IS the
            // bigger sphere). min of the two spheres is exact for both near the surface, which is
            // the only place accuracy matters here.
            if (l2 < 1e-12f || a2 <= 1e-12f)
                return Mathf.Min((p - a).magnitude - r1, (p - b).magnitude - r2);

            float il2 = 1f / l2;
            Vector3 pa = p - a;
            float y = Vector3.Dot(pa, ba);
            float z = y - l2;
            Vector3 x = pa * l2 - ba * y;
            float x2 = Vector3.Dot(x, x);
            float y2 = y * y * l2;
            float z2 = z * z * l2;

            float k = Sign(rr) * rr * rr * x2;
            if (Sign(z) * a2 * z2 > k) return Mathf.Sqrt(x2 + z2) * il2 - r2;
            if (Sign(y) * a2 * y2 < k) return Mathf.Sqrt(x2 + y2) * il2 - r1;
            return (Mathf.Sqrt(x2 * a2 * il2) + y * rr) * il2 - r1;
        }

        /// Three-valued sign, NOT Mathf.Sign - which returns +1 for zero. The region tests above
        /// rely on sign(0) being 0 to pick the barrel branch exactly at the cap boundaries; with
        /// Mathf.Sign a cylindrical link (rr == 0) takes the wrong branch along its whole length.
        private static float Sign(float v) => v > 0f ? 1f : (v < 0f ? -1f : 0f);
    }
}
