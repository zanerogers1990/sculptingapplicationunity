using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Turns an SSphereRig into a mesh - the "skinning" step, and the part of the feature that
    /// earns the workflow its reputation.
    ///
    /// Approach: build an analytic signed distance field out of the rig, then extract its
    /// isosurface with the same Surface Nets pass MeshRemesher uses for Remesh. That one decision
    /// buys most of the requirements at once, and is why this is a field-based skinner rather than
    /// tube-extrusion-plus-stitching:
    ///
    ///   - each parent-child link is a ROUND CONE, the exact distance to the convex hull of two
    ///     spheres, so a fat-parent/thin-child link is a truly tapered tube;
    ///   - primitives are unioned with a smooth minimum, so limbs meeting at a joint fair into
    ///     each other with a fillet instead of creasing - and three-plus-way joints, the worst
    ///     case for a stitched skinner, need no special handling at all;
    ///   - an isosurface of a continuous field cannot self-intersect however tangled the rig is,
    ///     and overlapping tubes are the NORMAL case in a blockout;
    ///   - Surface Nets emits one vertex per active cell, so the output is an even, quad-derived
    ///     grid whose density comes from the resolution rather than from how many spheres were
    ///     placed - which is what makes the result sculptable rather than merely watertight.
    ///
    /// What the rebuild added, and why:
    ///
    ///   - THREE QUALITY TIERS (see SkinQuality), so a live drag, a settled viewport and a Convert
    ///     each pay only for what that moment needs.
    ///   - PERSISTENT BUFFERS and a caller-owned Mesh refilled in place: skinning many times a
    ///     second cannot allocate a fresh grid and a fresh Mesh each time.
    ///   - EXACT SYMMETRY. The rig hands over reflections with the originals, the grid is anchored
    ///     so x = 0 is a sample plane, and only the +x half is sampled and then copied across - so
    ///     extraction is mirror-exact. Fairing is where that used to be lost: Surface Nets splits
    ///     every quad along the same diagonal on both sides, so mirrored vertices have different
    ///     neighbour sets and any Laplacian drifts them apart by up to half a voxel. The fairing
    ///     here pairs every vertex with its reflection first and re-symmetrises after every pass.
    ///     This matters beyond looks: the sculpting side's mirror tools and symmetry repair all
    ///     assume a symmetric object has symmetric vertices.
    ///
    /// Main thread only, synchronously: it shares MeshRemesher's static scratch buffers, and a
    /// finished mesh on return is what lets the caller treat this as "redraw the skin".
    public static class SSphereSkinner
    {
        public struct SkinSettings
        {
            /// Voxel density as a multiple of "fine enough for the thinnest sphere in the rig".
            /// 1 is the sensible default at any scale, which is the point: the resolution a rig
            /// needs depends on how small its smallest limb is relative to the whole, and that
            /// ratio is knowable without asking.
            public float Density;

            /// Fillet width at a joint, as a fraction of the local primitive's own radius. 0 gives
            /// a hard crease at every junction; ~0.45 is the organic look.
            public float Blend;

            /// Taubin fairing iterations. Taubin rather than Laplacian so voxel stair-stepping goes
            /// without the limbs shrinking away from the radii the artist specified.
            public int Smoothing;

            public static SkinSettings Default => new SkinSettings
            {
                Density = 1f,
                Blend = 0.45f,
                Smoothing = 3
            };
        }

        public enum SkinQuality
        {
            /// While the rig is being dragged: coarse, cheap, good enough to judge a shape by.
            Draft,
            /// The settled viewport skin: full density, but capped so a detailed creature does not
            /// hitch the viewport for half a second on every mouse release.
            Preview,
            /// Convert: the full resolution the rig asks for.
            Final
        }

        public struct SkinStats
        {
            public int TriangleCount;
            public int Resolution;
            public float Milliseconds;
            public float SampleMs;
            public float ExtractMs;
            public float SmoothMs;
            public float UploadMs;
            /// Vertices fairing could not pair with a reflection (-1 when not symmetric). Zero is
            /// the expected value; anything else means the output is not exactly symmetric.
            public int UnpairedVertices;
        }

        public const float MinDensity = 0.35f;
        public const float MaxDensity = 2.5f;

        private const int MinResolution = 16;

        /// Padding around the rig's bounds, in cells, so the surface always closes inside the
        /// grid. Larger than MeshRemesher's 2 because the smooth union pushes the surface OUTWARD
        /// past the primitives that generated it, by up to about the blend radius.
        private const int GridPadCells = 3;

        /// Voxels wanted across the smallest sphere's RADIUS. Below about 2 a sphere starts
        /// reading as a cube; much above it costs resolution everywhere for detail only the
        /// thinnest limb can use.
        private const float CellsPerMinRadius = 2.5f;

        private const float TaubinLambda = 0.5f;
        private const float TaubinMu = -0.53f;
        private const int SmoothBlock = 2048;

        /// Per-tier limits. The sample budget matters as much as the resolution cap: resolution
        /// counts along ONE axis, so a long thin rig at 200 is a modest grid while a compact one is
        /// 8M+ samples. The resolution is walked down to fit rather than letting a slider lock the
        /// app up.
        private static void TierLimits(SkinQuality quality, out float scale, out int maxResolution, out long sampleBudget)
        {
            switch (quality)
            {
                case SkinQuality.Draft:
                    scale = 0.55f; maxResolution = 96; sampleBudget = 1_500_000;
                    break;
                case SkinQuality.Preview:
                    scale = 1f; maxResolution = 150; sampleBudget = 4_000_000;
                    break;
                default:
                    scale = 1f; maxResolution = 224; sampleBudget = 14_000_000;
                    break;
            }
        }

        /// A tapered capsule between two rig spheres, or a lone sphere. Precomputed once per skin
        /// so the inner sampling loop does no rig lookups.
        private readonly struct Primitive
        {
            public readonly Vector3 A, B;
            public readonly float RA, RB;
            /// Fillet width for THIS primitive, in world units. Scaled off the primitive's own size
            /// so a thick torso joint blends wide while a finger joint blends tight.
            public readonly float BlendK;
            public readonly bool IsSphere;

            public Primitive(Vector3 a, Vector3 b, float ra, float rb, float blendK, bool isSphere)
            {
                A = a; B = b; RA = ra; RB = rb; BlendK = blendK; IsSphere = isSphere;
            }

            public float MaxRadius => Mathf.Max(RA, RB);
        }

        // Reused across skins rather than reallocated. Safe for the same reason MeshRemesher's own
        // scratch is: every entry point runs synchronously to completion on the main thread.
        private static float[] _sdf = new float[0];
        private static readonly List<Primitive> _primitives = new List<Primitive>();
        private static readonly List<SSphereRig.SphereInstance> _spheres = new List<SSphereRig.SphereInstance>();
        private static readonly List<SSphereRig.LinkInstance> _links = new List<SSphereRig.LinkInstance>();
        private static Vector3Int[] _primLo = new Vector3Int[0];
        private static Vector3Int[] _primHi = new Vector3Int[0];

        private static int[] _adjOffsets = new int[0];
        private static int[] _adjCount = new int[0];
        private static int[] _adjNeighbours = new int[0];
        private static Vector3[] _smoothScratch = new Vector3[0];
        private static int[] _mirrorPartner = new int[0];
        private static int[] _bucketNext = new int[0];
        private static readonly Dictionary<long, int> _bucketHead = new Dictionary<long, int>();

        /// Skins into a brand new Mesh, for callers that want to own one (Convert). Returns null
        /// with `error` set when there is nothing to skin.
        public static Mesh Skin(SSphereRig rig, bool symmetry, SkinSettings settings,
                                SkinQuality quality, out SkinStats stats, out string error)
        {
            var mesh = new Mesh { name = "SSphere Skin", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            if (SkinInto(rig, symmetry, settings, quality, mesh, out stats, out error)) return mesh;
            Object.Destroy(mesh);
            return null;
        }

        /// Refills `target` with the rig's skin. False (with `error` set) when there is nothing to
        /// skin or the field produced no surface; `target` is left untouched then, so a live
        /// preview keeps its last good skin rather than flickering to empty.
        public static bool SkinInto(SSphereRig rig, bool symmetry, SkinSettings settings,
                                    SkinQuality quality, Mesh target, out SkinStats stats, out string error)
        {
            stats = default;
            stats.UnpairedVertices = -1;
            error = null;

            if (rig == null || rig.IsEmpty)
            {
                error = "No SSpheres placed.";
                return false;
            }
            if (target == null)
            {
                error = "No mesh to skin into.";
                return false;
            }

            long t0 = Now();

            float blend = Mathf.Max(0f, settings.Blend);
            BuildPrimitives(rig, symmetry, blend);
            if (_primitives.Count == 0)
            {
                error = "No SSpheres placed.";
                return false;
            }

            // Bounds must cover the SMOOTHED surface, which bulges past the primitives by about
            // the blend radius.
            float maxBlend = 0f;
            for (int i = 0; i < _primitives.Count; i++) maxBlend = Mathf.Max(maxBlend, _primitives[i].BlendK);

            Bounds bounds = rig.ComputeBounds(symmetry);
            bounds.Expand(maxBlend * 2f);

            float maxExtent = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.0001f);
            TierLimits(quality, out _, out _, out long budget);
            int resolution = ResolveResolution(rig, settings, quality, maxExtent);

            float cellSize = maxExtent / resolution;
            LayOutGrid(bounds, cellSize, symmetry, out Vector3 origin, out Vector3Int dims);
            while (SampleCount(dims) > budget && resolution > MinResolution)
            {
                resolution = Mathf.Max(MinResolution, Mathf.FloorToInt(resolution * 0.8f));
                cellSize = maxExtent / resolution;
                LayOutGrid(bounds, cellSize, symmetry, out origin, out dims);
            }

            int sx = dims.x + 1, sy = dims.y + 1, sz = dims.z + 1;
            int sampleCount = sx * sy * sz;
            if (_sdf.Length < sampleCount) _sdf = new float[sampleCount];

            // The sample column on the mirror plane exists by construction (see LayOutGrid), so the
            // +x half is sampled and reflected rather than both halves being computed.
            int centreColumn = symmetry ? Mathf.RoundToInt(-origin.x / cellSize) : 0;
            bool halfGrid = symmetry && centreColumn > 0 && centreColumn <= sx - 1;

            SampleField(origin, cellSize, sx, sy, sz, halfGrid ? centreColumn : 0);
            if (halfGrid) MirrorField(sx, sy, sz, centreColumn);
            long t1 = Now();

            MeshRemesher.RemeshResult result = MeshRemesher.BuildFromSdfGeometry(_sdf, dims, origin, cellSize);
            long t2 = Now();
            if (result.IsEmpty)
            {
                error = "Skinning produced no surface - try a higher skin density.";
                return false;
            }

            int smoothing = quality == SkinQuality.Draft
                ? Mathf.Min(settings.Smoothing, 2)
                : Mathf.Max(settings.Smoothing, 0);

            if (smoothing > 0)
                stats.UnpairedVertices = Fair(result.Vertices, result.Triangles, smoothing, halfGrid, origin, cellSize);
            long t3 = Now();

            target.Clear();
            target.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            target.SetVertices(result.Vertices);
            target.SetTriangles(result.Triangles, 0);
            // Recomputed rather than reusing result.Normals: those came from the field gradient and
            // describe the surface BEFORE fairing moved every vertex.
            target.RecalculateNormals();
            target.RecalculateBounds();
            long t4 = Now();

            stats.TriangleCount = result.Triangles.Length / 3;
            stats.Resolution = resolution;
            stats.SampleMs = Ms(t0, t1);
            stats.ExtractMs = Ms(t1, t2);
            stats.SmoothMs = Ms(t2, t3);
            stats.UploadMs = Ms(t3, t4);
            stats.Milliseconds = Ms(t0, t4);
            return true;
        }

        /// The resolution a Convert will run at, for the UI. An estimate: it does not model the
        /// sample-budget walk-down, so a very lopsided rig can come out a step or two coarser.
        public static int PreviewResolution(SSphereRig rig, bool symmetry, SkinSettings settings)
        {
            if (rig == null || rig.IsEmpty) return MinResolution;
            Bounds bounds = rig.ComputeBounds(symmetry);
            bounds.Expand(Mathf.Max(0f, settings.Blend) * rig.MeanRadius() * 2f);
            float maxExtent = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.0001f);
            return ResolveResolution(rig, settings, SkinQuality.Final, maxExtent);
        }

        private static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();

        private static float Ms(long from, long to) =>
            (float)((to - from) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

        private static long SampleCount(Vector3Int dims) =>
            (long)(dims.x + 1) * (dims.y + 1) * (dims.z + 1);

        private static int ResolveResolution(SSphereRig rig, SkinSettings settings, SkinQuality quality, float maxExtent)
        {
            TierLimits(quality, out float scale, out int maxResolution, out _);
            float minRadius = rig.MinRadius();
            float density = Mathf.Clamp(settings.Density, MinDensity, MaxDensity);

            // Cells small enough that the thinnest sphere spans CellsPerMinRadius of them, scaled
            // by the artist's density - which is what makes one default right for a thumb-sized rig
            // and a building-sized one alike.
            int resolution = minRadius > 0f
                ? Mathf.CeilToInt(maxExtent / (minRadius / CellsPerMinRadius) * density * scale)
                : Mathf.CeilToInt(64f * density * scale);

            return Mathf.Clamp(resolution, MinResolution, maxResolution);
        }

        /// Places the sample grid over `bounds`. With symmetry on, the grid is made EXACTLY
        /// symmetric about x = 0 - half-width rounded up to whole cells, origin at minus that - so
        /// sample columns are mirror images and the column on the plane exists. Anchoring to
        /// bounds.min instead puts the two halves on different sub-cell offsets and destroys the
        /// symmetry by construction, however exact the field is.
        private static void LayOutGrid(Bounds bounds, float cellSize, bool symmetry,
                                       out Vector3 origin, out Vector3Int dims)
        {
            float pad = GridPadCells * cellSize;
            Vector3 min = bounds.min - Vector3.one * pad;
            Vector3 max = bounds.max + Vector3.one * pad;

            if (symmetry)
            {
                float half = Mathf.Max(Mathf.Abs(min.x), Mathf.Abs(max.x));
                int halfCells = Mathf.Max(1, Mathf.CeilToInt(half / cellSize));
                min.x = -halfCells * cellSize;
                max.x = halfCells * cellSize;
            }

            origin = min;
            dims = new Vector3Int(
                Mathf.Max(1, Mathf.CeilToInt((max.x - min.x) / cellSize)),
                Mathf.Max(1, Mathf.CeilToInt((max.y - min.y) / cellSize)),
                Mathf.Max(1, Mathf.CeilToInt((max.z - min.z) / cellSize)));
        }

        // --------------------------------------------------------------------- field building

        private static void BuildPrimitives(SSphereRig rig, bool symmetry, float blend)
        {
            _primitives.Clear();
            rig.CollectGeometry(symmetry, _spheres, _links);

            for (int i = 0; i < _spheres.Count; i++)
            {
                SSphereRig.SphereInstance s = _spheres[i];
                // Every sphere contributes even though a link's round cone includes both endcaps:
                // it is cheap, and it is what makes a lone root skin into something.
                _primitives.Add(new Primitive(s.Centre, s.Centre, s.Radius, s.Radius,
                                              blend * s.Radius, isSphere: true));
            }

            for (int i = 0; i < _links.Count; i++)
            {
                SSphereRig.LinkInstance l = _links[i];
                _primitives.Add(new Primitive(l.A, l.B, l.RadiusA, l.RadiusB,
                                              blend * Mathf.Max(l.RadiusA, l.RadiusB), isSphere: false));
            }
        }

        /// Fills the sample grid with the smooth union of every primitive, from column `fromX` up.
        ///
        /// SCATTERS per primitive rather than gathering per sample, so the work scales with the
        /// volume the rig occupies rather than with samples x primitives. Still EXACT:
        /// SmoothUnion(a, b, k) differs from min(a, b) only where the two are within k, so
        /// expanding each primitive's box by (k + 2 cells) covers every sample whose value could
        /// change. Parallelised over z-SLICES, not primitives, so no two threads touch a cell.
        private static void SampleField(Vector3 origin, float cellSize, int sx, int sy, int sz, int fromX)
        {
            int count = _primitives.Count;
            if (_primLo.Length < count)
            {
                _primLo = new Vector3Int[count];
                _primHi = new Vector3Int[count];
            }

            for (int p = 0; p < count; p++)
            {
                Primitive prim = _primitives[p];
                float reach = prim.MaxRadius + prim.BlendK + 2f * cellSize;
                Vector3 min = Vector3.Min(prim.A, prim.B) - Vector3.one * reach;
                Vector3 max = Vector3.Max(prim.A, prim.B) + Vector3.one * reach;
                Vector3Int lo = ClampToGrid(min, origin, cellSize, sx, sy, sz, floor: true);
                _primLo[p] = new Vector3Int(Mathf.Max(lo.x, fromX), lo.y, lo.z);
                _primHi[p] = ClampToGrid(max, origin, cellSize, sx, sy, sz, floor: false);
            }

            float[] sdf = _sdf;

            // Outside the scattered boxes only the sign is ever read, so the magnitude just has to
            // be unambiguously "far outside".
            const float FarSentinel = 1e6f;

            System.Threading.Tasks.Parallel.For(0, sz, z =>
            {
                int sliceBase = sx * sy * z;
                int sliceEnd = sliceBase + sx * sy;
                for (int i = sliceBase; i < sliceEnd; i++) sdf[i] = FarSentinel;

                for (int p = 0; p < count; p++)
                {
                    if (z < _primLo[p].z || z > _primHi[p].z) continue;
                    int loX = _primLo[p].x, hiX = _primHi[p].x;
                    if (loX > hiX) continue;
                    Primitive prim = _primitives[p];
                    float wz = origin.z + z * cellSize;

                    for (int y = _primLo[p].y; y <= _primHi[p].y; y++)
                    {
                        float wy = origin.y + y * cellSize;
                        int rowBase = sx * (y + sy * z);
                        for (int x = loX; x <= hiX; x++)
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
        }

        /// Reflects the sampled +x half of the grid into the -x half about `centre`. Cheaper than
        /// sampling it, and exact where sampling could not be: the polynomial smooth minimum is not
        /// associative, so two mirrored samples folding the same primitives in a different order
        /// get slightly different numbers.
        private static void MirrorField(int sx, int sy, int sz, int centre)
        {
            float[] sdf = _sdf;
            System.Threading.Tasks.Parallel.For(0, sz, z =>
            {
                for (int y = 0; y < sy; y++)
                {
                    int rowBase = sx * (y + sy * z);
                    for (int x = 0; x < centre; x++)
                    {
                        int source = 2 * centre - x;
                        if (source <= sx - 1) sdf[rowBase + x] = sdf[rowBase + source];
                    }
                }
            });
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

        /// Polynomial smooth minimum. More than k apart this is exactly min(a, b); within k it
        /// subtracts a quadratic bulge - the fillet that makes a junction read as one organic form.
        private static float SmoothUnion(float a, float b, float k)
        {
            if (k <= 0f) return Mathf.Min(a, b);
            float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
            return Mathf.Lerp(b, a, h) - k * h * (1f - h);
        }

        /// Exact signed distance to a round cone - the convex hull of sphere(a, r1) and
        /// sphere(b, r2) - after Inigo Quilez's branch-per-region formulation. The taper is the
        /// exact conical surface tangent to both spheres, not a lerped radius (which leaves a kink
        /// where the tube meets each sphere).
        private static float SdRoundCone(Vector3 p, Vector3 a, Vector3 b, float r1, float r2)
        {
            Vector3 ba = b - a;
            float l2 = Vector3.Dot(ba, ba);
            float rr = r1 - r2;
            float a2 = l2 - rr * rr;

            // Coincident centres, or one sphere swallowing the other: the hull IS the bigger sphere,
            // and min of the two is exact near the surface, the only place accuracy matters.
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

        /// Three-valued sign, NOT Mathf.Sign (which returns +1 for zero). The region tests above
        /// rely on sign(0) == 0; with Mathf.Sign a cylindrical link takes the wrong branch along
        /// its whole length.
        private static float Sign(float v) => v > 0f ? 1f : (v < 0f ? -1f : 0f);

        // --------------------------------------------------------------------------- fairing

        /// Taubin fairing, parallel, with exact symmetry preserved when `symmetric`. Returns the
        /// number of vertices that found no reflection, or -1 when symmetry was not requested.
        ///
        /// Written here rather than reusing MeshExtractor.Smooth for both reasons that came out of
        /// measuring it: that version builds adjacency through a HashSet of edge keys and smooths
        /// single-threaded, which was more than half of a settled skin's time, and it has no way to
        /// keep mirrored vertices mirrored.
        private static int Fair(Vector3[] verts, int[] tris, int iterations, bool symmetric, Vector3 origin, float cellSize)
        {
            int n = verts.Length;
            BuildAdjacency(n, tris);
            if (_smoothScratch.Length < n) _smoothScratch = new Vector3[n];

            // Paired BEFORE any vertex moves, while extraction's exact symmetry still holds.
            int unpaired = symmetric ? PairMirrorVertices(verts, n, origin, cellSize) : -1;

            for (int iter = 0; iter < iterations; iter++)
            {
                SmoothPass(verts, _smoothScratch, n, TaubinLambda);
                if (symmetric) Symmetrize(_smoothScratch, n);
                SmoothPass(_smoothScratch, verts, n, TaubinMu);
                if (symmetric) Symmetrize(verts, n);
            }
            return unpaired;
        }

        /// CSR vertex adjacency from triangles, with no hashing: each triangle writes both of its
        /// other corners into each corner's range, and each range is then sorted and de-duplicated
        /// in place. Ranges are disjoint, so the de-duplication runs in parallel.
        private static void BuildAdjacency(int n, int[] tris)
        {
            if (_adjOffsets.Length < n + 1) _adjOffsets = new int[n + 1];
            if (_adjCount.Length < n) _adjCount = new int[n];
            int[] offsets = _adjOffsets, counts = _adjCount;

            System.Array.Clear(counts, 0, n);
            for (int t = 0; t < tris.Length; t += 3)
            {
                counts[tris[t]] += 2;
                counts[tris[t + 1]] += 2;
                counts[tris[t + 2]] += 2;
            }

            int cursor = 0;
            for (int i = 0; i < n; i++)
            {
                offsets[i] = cursor;
                cursor += counts[i];
                counts[i] = 0;
            }
            offsets[n] = cursor;

            if (_adjNeighbours.Length < cursor) _adjNeighbours = new int[cursor];
            int[] nbs = _adjNeighbours;

            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                int oa = offsets[a] + counts[a]; nbs[oa] = b; nbs[oa + 1] = c; counts[a] += 2;
                int ob = offsets[b] + counts[b]; nbs[ob] = a; nbs[ob + 1] = c; counts[b] += 2;
                int oc = offsets[c] + counts[c]; nbs[oc] = a; nbs[oc + 1] = b; counts[c] += 2;
            }

            int blocks = (n + SmoothBlock - 1) / SmoothBlock;
            System.Threading.Tasks.Parallel.For(0, blocks, block =>
            {
                int end = Mathf.Min(n, (block + 1) * SmoothBlock);
                for (int i = block * SmoothBlock; i < end; i++)
                {
                    int start = offsets[i], stop = start + counts[i];
                    for (int x = start + 1; x < stop; x++)
                    {
                        int key = nbs[x];
                        int y = x - 1;
                        while (y >= start && nbs[y] > key) { nbs[y + 1] = nbs[y]; y--; }
                        nbs[y + 1] = key;
                    }
                    int unique = 0;
                    for (int x = start; x < stop; x++)
                        if (unique == 0 || nbs[start + unique - 1] != nbs[x]) nbs[start + unique++] = nbs[x];
                    counts[i] = unique;
                }
            });
        }

        private static void SmoothPass(Vector3[] src, Vector3[] dst, int n, float factor)
        {
            int[] offsets = _adjOffsets, counts = _adjCount, nbs = _adjNeighbours;
            int blocks = (n + SmoothBlock - 1) / SmoothBlock;
            System.Threading.Tasks.Parallel.For(0, blocks, block =>
            {
                int end = Mathf.Min(n, (block + 1) * SmoothBlock);
                for (int i = block * SmoothBlock; i < end; i++)
                {
                    Vector3 p = src[i];
                    int start = offsets[i], count = counts[i];
                    if (count == 0) { dst[i] = p; continue; }

                    float ax = 0f, ay = 0f, az = 0f;
                    for (int k = start; k < start + count; k++)
                    {
                        Vector3 q = src[nbs[k]];
                        ax += q.x; ay += q.y; az += q.z;
                    }
                    float inv = 1f / count;
                    dst[i] = new Vector3(
                        p.x + (ax * inv - p.x) * factor,
                        p.y + (ay * inv - p.y) * factor,
                        p.z + (az * inv - p.z) * factor);
                }
            });
        }

        /// Pairs every vertex with the vertex at its exact reflection, through a spatial hash of
        /// cell-sized buckets. The home bucket almost always holds the partner; the 26 neighbours
        /// are only searched on a miss, which happens when a coordinate sits within float noise of
        /// a bucket boundary. Returns the number left unpaired.
        private static int PairMirrorVertices(Vector3[] verts, int n, Vector3 origin, float cellSize)
        {
            if (_mirrorPartner.Length < n)
            {
                _mirrorPartner = new int[n];
                _bucketNext = new int[n];
            }

            float inv = 1f / cellSize;
            _bucketHead.Clear();
            for (int i = 0; i < n; i++)
            {
                long key = BucketKey(verts[i], origin, inv, 0, 0, 0);
                _bucketNext[i] = _bucketHead.TryGetValue(key, out int head) ? head : -1;
                _bucketHead[key] = i;
            }

            // Extraction is symmetric to ~1e-7; a thousandth of a cell is far above that noise and
            // far below any real vertex spacing.
            float tolerance = cellSize * 1e-3f;
            float tolerance2 = tolerance * tolerance;
            int unpaired = 0;

            for (int i = 0; i < n; i++)
            {
                var mirrored = new Vector3(-verts[i].x, verts[i].y, verts[i].z);
                int partner = SearchBucket(verts, mirrored, BucketKey(mirrored, origin, inv, 0, 0, 0), tolerance2);
                for (int dz = -1; dz <= 1 && partner < 0; dz++)
                for (int dy = -1; dy <= 1 && partner < 0; dy++)
                for (int dx = -1; dx <= 1 && partner < 0; dx++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    partner = SearchBucket(verts, mirrored, BucketKey(mirrored, origin, inv, dx, dy, dz), tolerance2);
                }

                _mirrorPartner[i] = partner;
                if (partner < 0) unpaired++;
            }
            return unpaired;
        }

        private static int SearchBucket(Vector3[] verts, Vector3 point, long key, float tolerance2)
        {
            if (!_bucketHead.TryGetValue(key, out int i)) return -1;
            int best = -1;
            float bestDistance = tolerance2;
            for (; i >= 0; i = _bucketNext[i])
            {
                float d = (verts[i] - point).sqrMagnitude;
                if (d > bestDistance) continue;
                bestDistance = d;
                best = i;
            }
            return best;
        }

        private static long BucketKey(Vector3 p, Vector3 origin, float inv, int dx, int dy, int dz)
        {
            long x = Mathf.FloorToInt((p.x - origin.x) * inv) + dx + 1048576;
            long y = Mathf.FloorToInt((p.y - origin.y) * inv) + dy + 1048576;
            long z = Mathf.FloorToInt((p.z - origin.z) * inv) + dz + 1048576;
            return (x << 42) | (y << 21) | z;
        }

        /// Replaces each mutual pair with the symmetric average of the two - exact reflections of
        /// one another afterwards - and pins a vertex that is its own reflection onto the plane.
        private static void Symmetrize(Vector3[] verts, int n)
        {
            int[] partner = _mirrorPartner;
            for (int i = 0; i < n; i++)
            {
                int j = partner[i];
                if (j < 0) continue;
                if (j == i)
                {
                    verts[i].x = 0f;
                    continue;
                }
                if (j < i || partner[j] != i) continue;

                Vector3 a = verts[i], b = verts[j];
                float x = 0.5f * (a.x - b.x);
                float y = 0.5f * (a.y + b.y);
                float z = 0.5f * (a.z + b.z);
                verts[i] = new Vector3(x, y, z);
                verts[j] = new Vector3(-x, y, z);
            }
        }
    }
}
