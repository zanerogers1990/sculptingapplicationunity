using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Voxel-based remesher: samples a signed distance field around the input mesh on a
    /// uniform grid, then extracts a new, evenly-tessellated surface with Surface Nets.
    /// Unlike sculpting a fixed-topology mesh, this redistributes polygons evenly over
    /// whatever shape resulted, instead of leaving stretched/thin triangles behind.
    /// Resolution controls voxel count along the mesh's largest bounding-box axis - higher
    /// gives more detail everywhere, at higher cost. Sampling is parallelized across cores,
    /// but the call itself still blocks the calling thread until it finishes, so very high
    /// resolutions will still cause a hitch.
    public static class MeshRemesher
    {
        private static readonly int[][] CubeEdges = BuildCubeEdges();
        private static readonly Vector3Int[] CubeCorners = BuildCubeCorners();

        // Reused across remesh calls instead of allocating fresh each time (List.Clear() keeps
        // capacity, so repeated remeshes at similar resolutions settle into zero growth).
        // Safe because Remesh() is always called synchronously to completion from the main
        // thread only - never concurrently or re-entrantly - so there's no aliasing hazard.
        private static readonly List<Vector3> _scratchVerts = new List<Vector3>();
        private static readonly List<int> _scratchTris = new List<int>();

        public static Mesh Remesh(Vector3[] sourceVertices, int[] sourceTriangles, int resolution)
        {
            resolution = Mathf.Clamp(resolution, 4, 512);

            Bounds bounds = ComputeBounds(sourceVertices);
            float maxExtent = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.0001f);
            float cellSize = maxExtent / resolution;

            Vector3Int dims = GridDimensions(bounds, cellSize, out Vector3 origin);
            int sx = dims.x + 1, sy = dims.y + 1, sz = dims.z + 1;

            var sdf = new float[sx * sy * sz];
            SampleSignedField(sourceVertices, sourceTriangles, origin, cellSize, sx, sy, sz, sdf);

            return BuildSurface(sdf, dims, sx, sy, origin, cellSize);
        }

        /// Cell dimensions (and, via `origin`, the corner sample the grid starts at) of a
        /// sample grid covering `bounds` at `cellSize`. Padded by 2 cells on every side so the
        /// surface always closes inside the grid, even where sculpting pushed geometry right up
        /// against the source mesh's bounds. Shared with MeshBoolean so a boolean's grid is laid
        /// out exactly like a remesh's.
        internal static Vector3Int GridDimensions(Bounds bounds, float cellSize, out Vector3 origin)
        {
            const int pad = 2;
            origin = bounds.min - Vector3.one * (pad * cellSize);

            // Snap the lattice onto whole multiples of cellSize, so sample planes land on
            // x=0, y=0 and z=0 - which is exactly where MirrorController puts its mirror
            // planes (they pass through the object's local origin).
            //
            // Anchored to bounds.min alone, the lattice straddles the mirror plane by an
            // arbitrary fraction of a cell, and Surface Nets then places each half's vertices
            // at different offsets within their cells. A perfectly symmetric input came back
            // asymmetric BY CONSTRUCTION: measured on an exactly mirrored ellipsoid, 92% of
            // output vertices moved, with a mean error of 0.32 of a cell at every resolution -
            // constant in CELLS, which is what says the cause is the lattice and not the
            // surface extraction. Whether a model survived a remesh was pure luck: it came out
            // symmetric only when its X extent happened to be its largest, which makes the
            // offset land on a whole number of cells.
            //
            // Downstream this is what broke symmetry repair. After a remesh the two halves no
            // longer share a vertex layout, so SymmetryMap cannot pair them, MakeSymmetric has
            // no correspondence to repair through, and mirrored brush strokes land on
            // differently-tessellated surfaces and leave one side faceted.
            //
            // Snapping only ever moves the origin OUTWARD (Floor), so coverage is never lost;
            // the extra cell per axis below restores the padding that shift consumes.
            origin = new Vector3(
                Mathf.Floor(origin.x / cellSize),
                Mathf.Floor(origin.y / cellSize),
                Mathf.Floor(origin.z / cellSize)) * cellSize;

            return new Vector3Int(
                Mathf.CeilToInt(bounds.size.x / cellSize) + pad * 2 + 1,
                Mathf.CeilToInt(bounds.size.y / cellSize) + pad * 2 + 1,
                Mathf.CeilToInt(bounds.size.z / cellSize) + pad * 2 + 1);
        }

        /// Stand-in distance for a sample outside the narrow band (see SampleSignedField).
        /// Never read for anything but a sign check, so its exact magnitude doesn't matter as
        /// long as it reads unambiguously as "far". Internal because MeshBoolean combines
        /// fields carrying it and relies on it being a symmetric, negatable stand-in.
        internal const float FarSentinel = 1e6f;

        /// Fills `sdf` (layout x + sx*(y + sy*z), negative inside) with the signed distance
        /// field of one triangle soup, sampled on a grid the CALLER chose. Split out of Remesh
        /// so MeshBoolean can sample several meshes onto one shared grid and combine them - a
        /// boolean is exactly this pass run per operand and then min/max'd together, and having
        /// it in one place keeps the sign/narrow-band subtleties below from being reimplemented
        /// slightly differently there.
        internal static void SampleSignedField(Vector3[] verts, int[] tris, Vector3 origin, float cellSize, int sx, int sy, int sz, float[] sdf)
        {
            // SignedDistanceField's own binning grid is a triangle-lookup accelerator for the
            // SOURCE mesh - it has nothing to do with the OUTPUT sampling resolution, so it
            // must not reuse `cellSize` (that was a correctness-preserving but performance-
            // pathological shortcut). Sizing it off the output grid meant remeshing a coarse
            // source mesh (few, large triangles) at a fine target resolution made every large
            // triangle's bounding box span thousands of tiny bins, each insertion bloating
            // every bin it touched and degrading every later lookup against it too - this was
            // the actual reason high resolutions were unusably slow (28s+ at res=128 on a
            // ~768-triangle source), not the sampling/extraction work itself. Sizing bins off
            // the source mesh's own triangle density (~1 triangle per bin on average) keeps
            // insertion and lookup cost roughly constant regardless of target resolution.
            Bounds sourceBounds = ComputeBounds(verts);
            float sourceExtent = Mathf.Max(sourceBounds.size.x, sourceBounds.size.y, sourceBounds.size.z, 0.0001f);
            float triCount = Mathf.Max(1, tris.Length / 3);
            float binCellSize = Mathf.Clamp(sourceExtent / Mathf.Pow(triCount, 1f / 3f), sourceExtent * 0.001f, sourceExtent);
            var field = new SignedDistanceField(verts, tris, binCellSize);

            // Sign: one winding-number ray per (y,z) column, shared by every sample on it.
            var inside = new bool[sx * sy * sz];
            field.ComputeInsideMask(origin, cellSize, sx, sy, sz, inside);

            // Narrow band: BuildSurface only ever interpolates a vertex position using an
            // ACTIVE cell's own corners (one whose 8 corners aren't all the same inside/
            // outside sign) - every other sample only needs its correct sign, which `inside[]`
            // already gives for free. Without this, every one of the res^3 grid samples paid
            // for an expensive nearest-triangle query even though only the O(res^2) samples
            // actually near the surface are ever used for anything beyond their sign - that
            // was the real reason very high resolutions were intractable (found by
            // benchmarking: res=400 took 187s despite the triangle-binning fix above). This
            // cell scan itself is O(res^3) too, but cheap (plain bool comparisons, no
            // triangle queries), so it doesn't reintroduce the cost it's removing.
            //
            // Combining two such fields (MeshBoolean) keeps this exact rather than approximate:
            // a sample only holds a sentinel where its own mesh's sign is uniform across every
            // cell it belongs to, and any cell where that mesh's sign DOES flip is by
            // definition in its band with all 8 corners carrying real distances. So a sentinel
            // never ends up on the interpolated side of a crossing, whichever operand dominates.
            int nx = sx - 1, ny = sy - 1, nz = sz - 1;
            var needsDistance = new bool[sx * sy * sz];
            System.Threading.Tasks.Parallel.For(0, nz, z =>
            {
                for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    bool first = inside[SampleIndex(x, y, z, sx, sy)];
                    bool mixed = false;
                    for (int c = 1; c < 8 && !mixed; c++)
                    {
                        Vector3Int co = CubeCorners[c];
                        if (inside[SampleIndex(x + co.x, y + co.y, z + co.z, sx, sy)] != first) mixed = true;
                    }
                    if (!mixed) continue;

                    // Concurrent cells sharing a corner may all write `true` here - always the
                    // same value, so this is a benign race, not a correctness issue.
                    for (int c = 0; c < 8; c++)
                    {
                        Vector3Int co = CubeCorners[c];
                        needsDistance[SampleIndex(x + co.x, y + co.y, z + co.z, sx, sy)] = true;
                    }
                }
            });

            // Magnitude: nearest-triangle distance, independent per sample - parallelize
            // across z-slices (each slice writes a disjoint block of sdf, so no races).
            System.Threading.Tasks.Parallel.For(0, sz, z =>
            {
                for (int y = 0; y < sy; y++)
                for (int x = 0; x < sx; x++)
                {
                    int idx = SampleIndex(x, y, z, sx, sy);
                    if (needsDistance[idx])
                    {
                        Vector3 p = origin + new Vector3(x * cellSize, y * cellSize, z * cellSize);
                        float dist = field.NearestUnsignedDistance(p);
                        sdf[idx] = inside[idx] ? -dist : dist;
                    }
                    else
                    {
                        sdf[idx] = inside[idx] ? -FarSentinel : FarSentinel;
                    }
                }
            });
        }

        /// Runs only the second half of the pipeline above - Surface Nets extraction, quad
        /// stitching and hole patching - over a signed distance grid the caller filled in
        /// itself, instead of one sampled from an existing mesh.
        ///
        /// Exists for ZSphereSkinner, which has an ANALYTIC field (a smooth union of tapered
        /// capsules) rather than a triangle soup, so everything Remesh does above this line -
        /// SignedDistanceField's triangle bins, the winding-number inside mask, the narrow-band
        /// pass - is not just unnecessary but inapplicable. Everything below it is exactly what
        /// that skinner needs and would otherwise be a second, subtly-different copy of: the
        /// even tessellation, the mostly-quad output, and PatchHoles' watertightness guarantee.
        ///
        /// `sdf` is laid out x + sx*(y + sy*z) with sx/sy/sz one MORE than the cell dims (corner
        /// samples, not cell centres) - the same layout Remesh builds above. Negative is inside.
        /// Main thread only, like Remesh, since both share this class's static scratch buffers.
        internal static Mesh BuildFromSdf(float[] sdf, Vector3Int dims, Vector3 origin, float cellSize)
            => BuildSurface(sdf, dims, dims.x + 1, dims.y + 1, origin, cellSize);

        private static int SampleIndex(int x, int y, int z, int sx, int sy) => x + sx * (y + sy * z);

        // Reused across BuildSurface calls for the same reason as _scratchVerts/_scratchTris -
        // avoids a fresh multi-million-element allocation on every remesh. Sized up (never
        // down) on demand.
        private static bool[] _scratchCellHasVertex = new bool[0];
        private static Vector3[] _scratchCellLocalPos = new Vector3[0];
        private static int[] _scratchCellVertexIndex = new int[0];

        // The cells pass 1 found a crossing in, in pass 2's scan order - i.e. exactly the cells
        // that own a Surface Nets vertex, and so the only cells EmitQuads has any reason to
        // look at. Same reuse rationale as the buffers above.
        private static readonly List<int> _scratchActiveCells = new List<int>();

        // PatchHoles' counting-sort buffers (see there). Same grow-on-demand reuse as the cell
        // buffers above - these are the largest transient allocations left in the pipeline, and
        // a remesh at maximum resolution would otherwise churn tens of megabytes per call.
        private static int[] _scratchEdgeStart = new int[0];
        private static int[] _scratchEdgeCursor = new int[0];
        private static int[] _scratchEdgeOther = new int[0];
        private static readonly List<long> _scratchBoundaryEdges = new List<long>();

        private static Mesh BuildSurface(float[] sdf, Vector3Int dims, int sx, int sy, Vector3 origin, float cellSize)
        {
            int nx = dims.x, ny = dims.y, nz = dims.z;
            int cellCount = nx * ny * nz;

            if (_scratchCellHasVertex.Length < cellCount)
            {
                _scratchCellHasVertex = new bool[cellCount];
                _scratchCellLocalPos = new Vector3[cellCount];
                _scratchCellVertexIndex = new int[cellCount];
            }
            bool[] cellHasVertex = _scratchCellHasVertex;
            Vector3[] cellLocalPos = _scratchCellLocalPos;
            int[] cellVertexIndex = _scratchCellVertexIndex;

            // Pass 1 (parallel): work out whether each cell is active and, if so, its local
            // Surface Nets vertex position. Each cell only reads sdf[] and writes its own
            // slot, so - unlike the single shared List<Vector3> this used to append straight
            // into - this part is embarrassingly parallel across cores. This was the last
            // remaining single-threaded O(resolution^3) pass in the whole remesh pipeline
            // (found by benchmarking: still 45-86s at 1-2M output triangles even after the
            // triangle-binning fix and the narrow-band SDF sampling above).
            System.Threading.Tasks.Parallel.For(0, nz, z =>
            {
                Span<float> corner = stackalloc float[8];
                for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                {
                    int cellIndex = x + nx * (y + ny * z);
                    int mask = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        Vector3Int co = CubeCorners[c];
                        float v = sdf[SampleIndex(x + co.x, y + co.y, z + co.z, sx, sy)];
                        corner[c] = v;
                        if (v < 0f) mask |= 1 << c;
                    }

                    if (mask == 0 || mask == 255) { cellHasVertex[cellIndex] = false; continue; } // all-inside or all-outside: no crossing

                    Vector3 sum = Vector3.zero;
                    int crossings = 0;
                    for (int e = 0; e < CubeEdges.Length; e++)
                    {
                        int a = CubeEdges[e][0], b = CubeEdges[e][1];
                        float va = corner[a], vb = corner[b];
                        if ((va < 0f) == (vb < 0f)) continue;

                        float t = va / (va - vb);
                        sum += Vector3.Lerp(CubeCorners[a], CubeCorners[b], t);
                        crossings++;
                    }

                    cellLocalPos[cellIndex] = sum / crossings;
                    cellHasVertex[cellIndex] = true;
                }
            });

            // Pass 2 (sequential, but cheap - pure array reads + list appends, no per-cell
            // math): compacts pass 1's per-cell results into the final vertex list and
            // cell->index map, walking cells in the same fixed order the old single-threaded
            // loop used so output vertex ordering/indexing is unchanged.
            var verts = _scratchVerts;
            verts.Clear();

            var activeCells = _scratchActiveCells;
            activeCells.Clear();

            for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                int cellIndex = x + nx * (y + ny * z);
                if (!cellHasVertex[cellIndex]) { cellVertexIndex[cellIndex] = -1; continue; }

                Vector3 worldPos = origin + (new Vector3(x, y, z) + cellLocalPos[cellIndex]) * cellSize;
                cellVertexIndex[cellIndex] = verts.Count;
                verts.Add(worldPos);
                activeCells.Add(cellIndex);
            }

            var tris = _scratchTris;
            tris.Clear();
            EmitQuads(sdf, cellVertexIndex, verts, activeCells, dims, sx, sy, tris, origin, cellSize);

            PatchHoles(verts, tris);

            var mesh = new Mesh
            {
                indexFormat = verts.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var uvs = new Vector2[verts.Count];
            Vector3 center = mesh.bounds.center;
            for (int i = 0; i < verts.Count; i++)
            {
                Vector3 n = (verts[i] - center).normalized;
                uvs[i] = new Vector2(
                    0.5f + Mathf.Atan2(n.z, n.x) / (2f * Mathf.PI),
                    0.5f - Mathf.Asin(Mathf.Clamp(n.y, -1f, 1f)) / Mathf.PI);
            }
            mesh.SetUVs(0, uvs);
            mesh.RecalculateTangents();

            return mesh;
        }

        // Emits the quad for every grid-lattice edge the SDF changes sign across: the four
        // cells sharing such an edge each hold a Surface Nets vertex, and stitching those four
        // together gives one quad (two triangles).
        //
        // Driven by the list of ACTIVE cells rather than by scanning the lattice. The previous
        // version walked every edge of the whole grid once per axis - O(resolution^3) x 3 - and
        // benchmarking showed 99.4% of that was wasted: at resolution 256 it tested 45,687,370
        // edges to find 289,750 sign flips, and those three scans cost more than the Surface
        // Nets extraction they feed.
        //
        // Skipping straight to the active cells is exact, not an approximation. A sign-flipping
        // edge is one of the twelve edges of each of the four cells around it, so all four of
        // those cells have mixed corner signs and are active by definition. Taking the cell at
        // the maximum end of the edge in both cross-axis directions (`p2` in each block below)
        // as that edge's single owner gives exactly one owner per edge, so walking active cells
        // and testing each one's three owned edges reaches every quad exactly once - and reaches
        // nothing else, which is the whole point.
        //
        // Triangle ORDER differs from the old axis-by-axis walk: the same triangles come out in
        // a different sequence. Nothing downstream depends on it (a mesh's triangle list is a
        // soup, and vertex numbering is unchanged - that still comes from pass 2's scan order).
        // The one visible consequence is that PatchHoles walks boundary vertices in first-seen
        // order, so on the rare non-watertight output it may pick a different - equally valid -
        // triangulation for the same cap.
        private static void EmitQuads(float[] sdf, int[] cellVertexIndex, List<Vector3> verts, List<int> activeCells, Vector3Int dims, int sx, int sy, List<int> tris, Vector3 origin, float cellSize)
        {
            int nx = dims.x, ny = dims.y;
            int slice = nx * ny;

            for (int i = 0; i < activeCells.Count; i++)
            {
                int cellIndex = activeCells[i];
                int z = cellIndex / slice;
                int rem = cellIndex - z * slice;
                int y = rem / nx;
                int x = rem - y * nx;

                // All three of this cell's owned edges start at its minimum corner sample.
                float va = sdf[SampleIndex(x, y, z, sx, sy)];
                bool signA = va < 0f;

                // Edge along +X. Its four cells step back in Y and Z.
                if (y >= 1 && z >= 1 && (sdf[SampleIndex(x + 1, y, z, sx, sy)] < 0f) != signA)
                {
                    StitchQuad(sdf, cellVertexIndex, verts, tris, signA,
                        new Vector3Int(x, y - 1, z - 1), new Vector3Int(x, y, z - 1),
                        new Vector3Int(x, y, z), new Vector3Int(x, y - 1, z),
                        nx, ny, sx, sy, origin, cellSize);
                }

                // Edge along +Y. Its four cells step back in Z and X.
                if (z >= 1 && x >= 1 && (sdf[SampleIndex(x, y + 1, z, sx, sy)] < 0f) != signA)
                {
                    StitchQuad(sdf, cellVertexIndex, verts, tris, signA,
                        new Vector3Int(x - 1, y, z - 1), new Vector3Int(x - 1, y, z),
                        new Vector3Int(x, y, z), new Vector3Int(x, y, z - 1),
                        nx, ny, sx, sy, origin, cellSize);
                }

                // Edge along +Z. Its four cells step back in X and Y.
                if (x >= 1 && y >= 1 && (sdf[SampleIndex(x, y, z + 1, sx, sy)] < 0f) != signA)
                {
                    StitchQuad(sdf, cellVertexIndex, verts, tris, signA,
                        new Vector3Int(x - 1, y - 1, z), new Vector3Int(x, y - 1, z),
                        new Vector3Int(x, y, z), new Vector3Int(x - 1, y, z),
                        nx, ny, sx, sy, origin, cellSize);
                }
            }
        }

        // Turns the four cells around one sign-flipping edge into two triangles, wound so the
        // face points out of the solid (`insideFirst` is the sign at the edge's start sample).
        //
        // The four cells MUST already hold Surface Nets vertices - see EmitQuads' remarks - but
        // this still goes through GetOrCreateCellVertex rather than reading cellVertexIndex
        // directly, keeping the pre-existing safety net for geometry where the per-cell mask
        // check and this direct edge check disagree. Measured across wrinkled, pinched and
        // overlapping-shell test meshes at resolutions 128 and 256, that fallback fired zero
        // times; it costs one array read and a branch when it doesn't.
        private static void StitchQuad(float[] sdf, int[] cellVertexIndex, List<Vector3> verts, List<int> tris, bool insideFirst, Vector3Int p0, Vector3Int p1, Vector3Int p2, Vector3Int p3, int nx, int ny, int sx, int sy, Vector3 origin, float cellSize)
        {
            int i0 = GetOrCreateCellVertex(sdf, cellVertexIndex, verts, p0, nx, ny, sx, sy, origin, cellSize);
            int i1 = GetOrCreateCellVertex(sdf, cellVertexIndex, verts, p1, nx, ny, sx, sy, origin, cellSize);
            int i2 = GetOrCreateCellVertex(sdf, cellVertexIndex, verts, p2, nx, ny, sx, sy, origin, cellSize);
            int i3 = GetOrCreateCellVertex(sdf, cellVertexIndex, verts, p3, nx, ny, sx, sy, origin, cellSize);

            if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0) return; // truly degenerate (out of grid bounds)

            if (insideFirst)
            {
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                tris.Add(i0); tris.Add(i2); tris.Add(i3);
            }
            else
            {
                tris.Add(i0); tris.Add(i2); tris.Add(i1);
                tris.Add(i0); tris.Add(i3); tris.Add(i2);
            }
        }

        // See EmitQuads' fallback remarks above - lazily computes (and caches in
        // cellVertexIndex, so a second lookup for the same cell from a different axis/edge is
        // free) a Surface Nets vertex for a cell, mirroring BuildSurface's pass-1 math exactly.
        // Returns -1 only if this cell's 8 corners turn out to be genuinely uniform (all-inside
        // or all-outside) despite the caller having just observed a sign flip on one of this
        // cell's edges - defensive; a plain skip is safer than fabricating a wrong position for
        // a case that (per EmitQuads' remarks) shouldn't occur.
        private static int GetOrCreateCellVertex(float[] sdf, int[] cellVertexIndex, List<Vector3> verts, Vector3Int cell, int nx, int ny, int sx, int sy, Vector3 origin, float cellSize)
        {
            int cellIndex = cell.x + nx * (cell.y + ny * cell.z);
            int existing = cellVertexIndex[cellIndex];
            if (existing >= 0) return existing;

            Span<float> corner = stackalloc float[8];
            int mask = 0;
            for (int c = 0; c < 8; c++)
            {
                Vector3Int co = CubeCorners[c];
                float val = sdf[SampleIndex(cell.x + co.x, cell.y + co.y, cell.z + co.z, sx, sy)];
                corner[c] = val;
                if (val < 0f) mask |= 1 << c;
            }
            if (mask == 0 || mask == 255) return -1;

            Vector3 sum = Vector3.zero;
            int crossings = 0;
            for (int e = 0; e < CubeEdges.Length; e++)
            {
                int a = CubeEdges[e][0], b = CubeEdges[e][1];
                float va = corner[a], vb = corner[b];
                if ((va < 0f) == (vb < 0f)) continue;
                float t = va / (va - vb);
                sum += Vector3.Lerp(CubeCorners[a], CubeCorners[b], t);
                crossings++;
            }

            Vector3 localPos = sum / crossings;
            Vector3 worldPos = origin + (new Vector3(cell.x, cell.y, cell.z) + localPos) * cellSize;
            int newIndex = verts.Count;
            verts.Add(worldPos);
            cellVertexIndex[cellIndex] = newIndex;
            return newIndex;
        }

        // Packs a pair of vertex indices into one key. PatchHoles uses only the UNDIRECTED
        // form (smaller index packed first, so an edge has one key whichever triangle names
        // it); EdgeKey itself is the raw pack the undirected form is built from.
        private static long EdgeKey(int a, int b) => ((long)a << 32) | (uint)b;
        private static long UndirectedEdgeKey(int a, int b) => a < b ? EdgeKey(a, b) : EdgeKey(b, a);

        /// Finds every boundary edge Surface Nets left open - used by exactly one triangle,
        /// with no matching triangle on the other side - walks each into a closed loop, and
        /// caps it with a fan of triangles from a new centroid vertex. This is what makes the
        /// output watertight the way DynaMesh/Blender's Voxel Remesh guarantee, rather than
        /// leaving a permanent hole: naive Surface Nets places exactly one vertex per active
        /// grid cell, so a genuinely concave pinch where two close/near-touching sculpted
        /// features pass through the SAME cell as two distinct surface sheets can't be
        /// represented there - EmitQuads already has a fallback for a related edge case
        /// (GetOrCreateCellVertex), but the underlying one-vertex-per-cell ambiguity itself
        /// isn't fixable at the per-cell level; patching the resulting hole afterward is. A
        /// missing face has no vertex-position fix, which is why this couldn't be solved by
        /// smoothing/sculpting after the fact before this pass existed - see
        /// [[project_scene_graph_epic]] memory for the original investigation.
        ///
        /// No-ops (after one cheap O(triangle count) scan) on the overwhelmingly common
        /// watertight case - this only does real work on the rare geometry that actually needs
        /// it, and even then only touches the small boundary loops themselves, not the mesh at
        /// large.
        private static void PatchHoles(List<Vector3> verts, List<int> tris)
        {
            int triCount = tris.Count / 3;
            if (triCount == 0) return;

            // Finding the boundary edges is the only part of this method that costs anything on
            // the normal, watertight output - the patching below almost never runs at all. So it
            // is done with a counting sort into flat arrays rather than a hash map: every
            // undirected edge is bucketed by its LOWER vertex index, which leaves each vertex's
            // handful of edges (a Surface Nets vertex has ~4-6 neighbours) in one short
            // contiguous run that can be scanned directly. No hashing and no per-entry objects,
            // and the common "nothing to patch" answer comes back without building a map at all.
            int vertCount = verts.Count;
            int edgeSlots = triCount * 3;
            if (_scratchEdgeStart.Length < vertCount + 2) _scratchEdgeStart = new int[vertCount + 2];
            if (_scratchEdgeCursor.Length < vertCount + 2) _scratchEdgeCursor = new int[vertCount + 2];
            if (_scratchEdgeOther.Length < edgeSlots) _scratchEdgeOther = new int[edgeSlots];
            int[] runStart = _scratchEdgeStart;
            int[] cursor = _scratchEdgeCursor;
            int[] other = _scratchEdgeOther;
            Array.Clear(runStart, 0, vertCount + 2);

            for (int t = 0; t < triCount; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                runStart[a < b ? a : b]++;
                runStart[b < c ? b : c]++;
                runStart[c < a ? c : a]++;
            }

            int running = 0;
            for (int i = 0; i <= vertCount; i++)
            {
                int count = runStart[i];
                runStart[i] = running;
                cursor[i] = running;
                running += count;
            }

            for (int t = 0; t < triCount; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                BucketEdge(cursor, other, a, b);
                BucketEdge(cursor, other, b, c);
                BucketEdge(cursor, other, c, a);
            }

            // Within one run, duplicates are found by a direct O(k^2) scan - k is a single digit,
            // so this beats anything cleverer. Entries are struck out (-1) as they are consumed
            // so each distinct edge is judged exactly once. An edge seen once and only once is a
            // boundary edge; one seen three or more times is non-manifold and is deliberately
            // NOT collected, matching the old count-based test that only accepted a count of 1.
            var boundary = _scratchBoundaryEdges;
            boundary.Clear();
            for (int u = 0; u < vertCount; u++)
            {
                int runFrom = runStart[u], runTo = runStart[u + 1];
                for (int i = runFrom; i < runTo; i++)
                {
                    int v = other[i];
                    if (v < 0) continue;
                    bool duplicated = false;
                    for (int j = i + 1; j < runTo; j++)
                        if (other[j] == v) { other[j] = -1; duplicated = true; }
                    if (!duplicated) boundary.Add(EdgeKey(u, v)); // u <= v by construction
                }
            }

            if (boundary.Count == 0) return; // watertight - the overwhelmingly common case

            // From here on the mesh genuinely has holes, so the remaining work is proportional to
            // their (small) size rather than to the mesh. boundaryNext[a] = b means the directed
            // edge a->b, as some triangle listed it, has no partner triangle traversing it b->a,
            // and that direction is exactly the "walk the hole's rim consistently with the
            // surrounding surface's winding" direction.
            var boundarySet = new HashSet<long>(boundary.Count * 2, EdgeKeyComparer.Instance);
            for (int i = 0; i < boundary.Count; i++) boundarySet.Add(boundary[i]);

            var boundaryNext = new Dictionary<int, int>();
            for (int t = 0; t < triCount; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                RecordIfBoundary(boundarySet, boundaryNext, a, b);
                RecordIfBoundary(boundarySet, boundaryNext, b, c);
                RecordIfBoundary(boundarySet, boundaryNext, c, a);
            }

            if (boundaryNext.Count == 0) return;

            var visited = new HashSet<int>();
            var loop = new List<int>();
            foreach (int startVertex in boundaryNext.Keys)
            {
                if (visited.Contains(startVertex) || boundaryNext[startVertex] < 0) continue;

                loop.Clear();
                int current = startVertex;
                bool closed = false;
                int guard = boundaryNext.Count + 1;
                while (guard-- > 0)
                {
                    if (!visited.Add(current)) break; // shouldn't happen before closing - bail out safely
                    loop.Add(current);
                    if (!boundaryNext.TryGetValue(current, out int next) || next < 0) break;
                    if (next == startVertex) { closed = true; break; }
                    current = next;
                }

                // An unclosed or degenerate walk means a non-manifold branch or a malformed
                // loop this simple algorithm can't safely fill - leave it as an open edge
                // rather than fabricate a wrong cap.
                if (!closed || loop.Count < 3) continue;

                Vector3 centroid = Vector3.zero;
                for (int i = 0; i < loop.Count; i++) centroid += verts[loop[i]];
                centroid /= loop.Count;
                int centroidIndex = verts.Count;
                verts.Add(centroid);

                for (int i = 0; i < loop.Count; i++)
                {
                    int a = loop[i];
                    int b = loop[(i + 1) % loop.Count];
                    // Any two triangles sharing a manifold edge always traverse it in opposite
                    // directions - since the boundary edge itself is a->b, the cap triangle
                    // filling the gap on the other side must list it b->a to keep the new
                    // face's normal pointing the same way as the surrounding surface.
                    tris.Add(b); tris.Add(a); tris.Add(centroidIndex);
                }
            }
        }

        // Files one undirected edge into the run belonging to its lower-index endpoint, storing
        // only the other endpoint - the run it lands in already identifies the first one.
        private static void BucketEdge(int[] cursor, int[] other, int a, int b)
        {
            int lo = a < b ? a : b;
            other[cursor[lo]++] = a < b ? b : a;
        }

        private static void RecordIfBoundary(HashSet<long> boundary, Dictionary<int, int> boundaryNext, int a, int b)
        {
            if (!boundary.Contains(UndirectedEdgeKey(a, b))) return;
            // More than one boundary edge starting at the same vertex means 3+ surface sheets
            // meet there (a non-manifold branch) - exactly the kind of case a simple loop-walk
            // can't represent. Mark it unpatchable (-1) rather than silently picking one branch.
            if (boundaryNext.ContainsKey(a)) boundaryNext[a] = -1;
            else boundaryNext[a] = b;
        }

        internal static Bounds ComputeBounds(Vector3[] verts)
        {
            Vector3 min = verts[0], max = verts[0];
            for (int i = 1; i < verts.Length; i++)
            {
                min = Vector3.Min(min, verts[i]);
                max = Vector3.Max(max, verts[i]);
            }
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        private static int[][] BuildCubeEdges()
        {
            var edges = new List<int[]>();
            for (int i = 0; i < 8; i++)
                for (int j = i + 1; j < 8; j++)
                {
                    int diff = i ^ j;
                    if (diff != 0 && (diff & (diff - 1)) == 0)
                        edges.Add(new[] { i, j });
                }
            return edges.ToArray();
        }

        private static Vector3Int[] BuildCubeCorners()
        {
            var corners = new Vector3Int[8];
            for (int i = 0; i < 8; i++)
                corners[i] = new Vector3Int(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            return corners;
        }
    }
}
