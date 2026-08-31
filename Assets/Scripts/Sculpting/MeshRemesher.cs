using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Voxel-based remesher: samples a signed distance field around the input mesh on a
    /// uniform grid, then extracts a new, evenly-tessellated surface. Unlike sculpting a
    /// fixed-topology mesh, this redistributes polygons evenly over whatever shape resulted,
    /// instead of leaving stretched/thin triangles behind. Resolution controls voxel count
    /// along the mesh's largest bounding-box axis.
    ///
    /// There are two extraction paths, and they exist for different inputs, not as alternatives:
    ///
    ///   - Remesh() goes through SparseRemesher, which has a SOURCE MESH and so can find the
    ///     cells holding surface directly from it. Its cost and memory scale with the surface,
    ///     which is what lets it reach several million triangles; see that class for the
    ///     measurements behind the change.
    ///   - BuildFromSdf() takes a grid the caller filled in itself - ZSphereSkinner's analytic
    ///     field, MeshBoolean's combined one - where there is no source mesh to read occupancy
    ///     from, so the grid is dense by construction and the extraction walks it.
    ///
    /// Both place their vertices with DualContourSolver rather than at the average of the edge
    /// crossings, which is what stops sharp detail being rounded off (see there).
    ///
    /// Sampling and extraction are parallelized across cores, but the call itself blocks the
    /// calling thread until it finishes - it feeds an interactive edit, where a finished mesh
    /// on return is worth more than a background job.
    public static class MeshRemesher
    {
        private static readonly int[][] CubeEdges = BuildCubeEdges();
        private static readonly Vector3Int[] CubeCorners = BuildCubeCorners();

        /// Highest grid resolution the extraction will attempt. Well past what the UI offers -
        /// this is the structural limit, and it is here so a saved scene or a script asking for
        /// something absurd is clamped rather than trusted.
        public const int MaxResolution = 2048;

        /// Vertices, normals and indices of a remesh, before any of it becomes a Unity Mesh.
        ///
        /// Returned instead of a Mesh because SculptableMesh needs these arrays anyway (they
        /// become its working buffers) and because it re-specifies the vertex buffer layout the
        /// moment it takes ownership. Handing it a Mesh meant building one, reading it straight
        /// back out through Mesh.vertices/.normals/.triangles - three full managed copies, over
        /// 100 MB of them at three million triangles - and then overwriting the buffer that had
        /// just been uploaded.
        internal struct RemeshResult
        {
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public int[] Triangles;
            public Bounds Bounds;

            public bool IsEmpty => Vertices == null || Vertices.Length == 0 || Triangles == null || Triangles.Length < 3;
        }

        // Reused across remesh calls instead of allocating fresh each time. Safe because the
        // remesh entry points are always called synchronously to completion from the main
        // thread only - never concurrently or re-entrantly - so there is no aliasing hazard.
        private static readonly MeshGeometryBuffer _buffer = new MeshGeometryBuffer();

        public static Mesh Remesh(Vector3[] sourceVertices, int[] sourceTriangles, int resolution)
            => BuildMesh(RemeshGeometry(sourceVertices, sourceTriangles, resolution));

        /// The remesh proper. See RemeshResult for why this, and not a Mesh, is the primary form.
        internal static RemeshResult RemeshGeometry(Vector3[] sourceVertices, int[] sourceTriangles, int resolution)
        {
            resolution = Mathf.Clamp(resolution, 4, MaxResolution);

            Bounds bounds = ComputeBounds(sourceVertices);
            float maxExtent = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z, 0.0001f);
            float cellSize = maxExtent / resolution;
            Vector3Int dims = GridDimensions(bounds, cellSize, out Vector3 origin);

            _buffer.Reset();
            SparseRemesher.Build(sourceVertices, sourceTriangles, origin, cellSize, dims, _buffer, out _);
            PatchHoles(_buffer);

            return Finish(_buffer);
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
            // arbitrary fraction of a cell, and the extraction then places each half's vertices
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

        /// Smallest distance magnitude a sampled field is allowed to carry, so that the sign of
        /// a stored value is always readable. See SampleSignedField's use of it. Far below any
        /// cell size the remesher works at, so it cannot move a crossing anywhere visible.
        internal const float MinSignedMagnitude = 1e-20f;

        /// Fills `sdf` (layout x + sx*(y + sy*z), negative inside) with the signed distance
        /// field of one triangle soup, sampled on a grid the CALLER chose.
        ///
        /// This is the DENSE sampler, and MeshBoolean is what it is for: a boolean is this pass
        /// run per operand and then min/max'd together, so the operands have to land on one
        /// shared grid before anything can be extracted from the combination. Remesh does not
        /// use it - it has a single source mesh and goes through SparseRemesher instead, which
        /// never materialises a whole-grid array at all.
        internal static void SampleSignedField(Vector3[] verts, int[] tris, Vector3 origin, float cellSize, int sx, int sy, int sz, float[] sdf)
        {
            // The triangle-lookup accelerator is sized off the SOURCE mesh's own triangle
            // density (about one triangle per bin), never off the output resolution. Sizing it
            // off the output grid meant remeshing a coarse source mesh (few, large triangles)
            // at a fine target resolution made every large triangle's bounding box span
            // thousands of tiny bins, each insertion bloating every bin it touched and
            // degrading every later lookup against it too.
            Bounds sourceBounds = ComputeBounds(verts);
            float sourceExtent = Mathf.Max(sourceBounds.size.x, sourceBounds.size.y, sourceBounds.size.z, 0.0001f);
            float triCount = Mathf.Max(1, tris.Length / 3);
            float binCellSize = Mathf.Clamp(sourceExtent / Mathf.Pow(triCount, 1f / 3f), sourceExtent * 0.001f, sourceExtent);
            var field = new SignedDistanceField(verts, tris, binCellSize);

            // Sign: one winding-number ray per (y,z) column, shared by every sample on it.
            var inside = new bool[sx * sy * sz];
            field.ComputeInsideMask(origin, cellSize, sx, sy, sz, inside);

            // Narrow band: extraction only ever interpolates a vertex position using an ACTIVE
            // cell's own corners (one whose 8 corners aren't all the same inside/outside sign) -
            // every other sample only needs its correct sign, which `inside[]` already gives for
            // free. Without this, every one of the res^3 grid samples paid for an expensive
            // nearest-triangle query even though only the O(res^2) samples actually near the
            // surface are ever used for anything beyond their sign.
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
                        // A sample lying exactly ON the surface has distance 0, and an inside
                        // one would then store -0.0f - for which `< 0f` is FALSE, so every
                        // later reader of this array silently disagrees with the winding mask
                        // that produced it. That is not an exotic input: any face flush against
                        // a sample plane (an unrotated box, a fresh primitive, anything snapped
                        // to the grid) produces thousands of them. Floor the magnitude to a
                        // negligible but genuinely non-zero distance so the sign always survives.
                        if (dist < MinSignedMagnitude) dist = MinSignedMagnitude;
                        sdf[idx] = inside[idx] ? -dist : dist;
                    }
                    else
                    {
                        sdf[idx] = inside[idx] ? -FarSentinel : FarSentinel;
                    }
                }
            });
        }

        /// Extracts a surface from a signed distance grid the caller filled in itself, instead
        /// of one sampled from an existing mesh.
        ///
        /// Exists for ZSphereSkinner, which has an ANALYTIC field (a smooth union of tapered
        /// capsules) rather than a triangle soup, and for MeshBoolean, whose field is several
        /// sampled fields folded together. Neither has a source mesh to read cell occupancy
        /// from, so neither can use the sparse path; both want everything below that line -
        /// even tessellation, mostly-quad output, feature-preserving vertex placement, and the
        /// watertightness guarantee hole patching gives.
        ///
        /// `sdf` is laid out x + sx*(y + sy*z) with sx/sy/sz one MORE than the cell dims (corner
        /// samples, not cell centres). Negative is inside. Main thread only, like Remesh, since
        /// both share this class's static scratch buffers.
        internal static Mesh BuildFromSdf(float[] sdf, Vector3Int dims, Vector3 origin, float cellSize)
            => BuildMesh(BuildFromSdfGeometry(sdf, dims, origin, cellSize));

        internal static RemeshResult BuildFromSdfGeometry(float[] sdf, Vector3Int dims, Vector3 origin, float cellSize)
        {
            _buffer.Reset();
            BuildDenseSurface(sdf, dims, dims.x + 1, dims.y + 1, origin, cellSize, _buffer);
            PatchHoles(_buffer);
            return Finish(_buffer);
        }

        private static int SampleIndex(int x, int y, int z, int sx, int sy) => x + sx * (y + sy * z);

        // Reused across BuildDenseSurface calls for the same reason as the geometry buffer -
        // avoids a fresh multi-million-element allocation on every call. Sized up (never down)
        // on demand.
        private static bool[] _scratchCellHasVertex = new bool[0];
        private static Vector3[] _scratchCellLocalPos = new Vector3[0];
        private static Vector3[] _scratchCellNormal = new Vector3[0];
        private static int[] _scratchCellVertexIndex = new int[0];

        // The cells pass 1 found a crossing in, in pass 2's scan order - i.e. exactly the cells
        // that own a vertex, and so the only cells the quad pass has any reason to look at.
        private static readonly List<int> _scratchActiveCells = new List<int>();

        private static void BuildDenseSurface(float[] sdf, Vector3Int dims, int sx, int sy, Vector3 origin, float cellSize, MeshGeometryBuffer output)
        {
            int nx = dims.x, ny = dims.y, nz = dims.z;
            int cellCount = nx * ny * nz;

            if (_scratchCellHasVertex.Length < cellCount)
            {
                _scratchCellHasVertex = new bool[cellCount];
                _scratchCellLocalPos = new Vector3[cellCount];
                _scratchCellNormal = new Vector3[cellCount];
                _scratchCellVertexIndex = new int[cellCount];
            }
            bool[] cellHasVertex = _scratchCellHasVertex;
            Vector3[] cellLocalPos = _scratchCellLocalPos;
            Vector3[] cellNormal = _scratchCellNormal;
            int[] cellVertexIndex = _scratchCellVertexIndex;

            // Pass 1 (parallel): work out whether each cell is active and, if so, where its dual
            // vertex goes. Each cell only reads sdf[] and writes its own slot, so this is
            // embarrassingly parallel across cores.
            System.Threading.Tasks.Parallel.For(0, nz, z =>
            {
                Span<float> corner = stackalloc float[8];
                var points = new Vector3[12];
                var normals = new Vector3[12];

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

                    if (mask == 0 || mask == 255) { cellHasVertex[cellIndex] = false; continue; } // no crossing

                    int crossings = 0;
                    for (int e = 0; e < CubeEdges.Length; e++)
                    {
                        int a = CubeEdges[e][0], b = CubeEdges[e][1];
                        float va = corner[a], vb = corner[b];
                        if ((va < 0f) == (vb < 0f)) continue;

                        float t = va / (va - vb);
                        Vector3 p = Vector3.Lerp(CubeCorners[a], CubeCorners[b], t);
                        points[crossings] = p;
                        // There is no source mesh here to take a true face normal from, so the
                        // normal comes from the gradient of the trilinear interpolant of this
                        // cell's OWN eight corners. Staying inside the cell matters: the narrow
                        // band only guarantees real distances at an active cell's own corners,
                        // and a central difference would reach into neighbours that may hold
                        // nothing but the far sentinel.
                        normals[crossings] = TrilinearGradient(corner, p);
                        crossings++;
                    }

                    if (crossings == 0) { cellHasVertex[cellIndex] = false; continue; }

                    cellLocalPos[cellIndex] = DualContourSolver.Solve(points, normals, crossings);

                    Vector3 n = Vector3.zero;
                    for (int i = 0; i < crossings; i++) n += normals[i];
                    cellNormal[cellIndex] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
                    cellHasVertex[cellIndex] = true;
                }
            });

            // Pass 2 (sequential, but cheap - pure array reads, no per-cell math): compacts
            // pass 1's per-cell results into the final vertex list and cell->index map.
            var activeCells = _scratchActiveCells;
            activeCells.Clear();

            for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                int cellIndex = x + nx * (y + ny * z);
                if (!cellHasVertex[cellIndex]) { cellVertexIndex[cellIndex] = -1; continue; }

                Vector3 worldPos = origin + (new Vector3(x, y, z) + cellLocalPos[cellIndex]) * cellSize;
                cellVertexIndex[cellIndex] = output.AddVertex(worldPos, cellNormal[cellIndex]);
                activeCells.Add(cellIndex);
            }

            EmitDenseQuads(sdf, cellVertexIndex, activeCells, dims, sx, sy, output);
        }

        /// Gradient of the trilinear interpolant of a cell's eight corner values, at a point in
        /// the cell's own [0,1]^3 coordinates, normalised. For a distance field this is the
        /// surface normal; the sign convention (negative inside) makes it point outward.
        private static Vector3 TrilinearGradient(Span<float> corner, Vector3 p)
        {
            float u = p.x, v = p.y, w = p.z;
            float gx = 0f, gy = 0f, gz = 0f;
            for (int c = 0; c < 8; c++)
            {
                int ix = c & 1, iy = (c >> 1) & 1, iz = (c >> 2) & 1;
                float wx = ix == 1 ? u : 1f - u;
                float wy = iy == 1 ? v : 1f - v;
                float wz = iz == 1 ? w : 1f - w;
                float sx = ix == 1 ? 1f : -1f;
                float sy = iy == 1 ? 1f : -1f;
                float sz = iz == 1 ? 1f : -1f;
                gx += corner[c] * sx * wy * wz;
                gy += corner[c] * wx * sy * wz;
                gz += corner[c] * wx * wy * sz;
            }
            var g = new Vector3(gx, gy, gz);
            return g.sqrMagnitude > 1e-20f ? g.normalized : Vector3.zero;
        }

        // Emits the quad for every grid-lattice edge the SDF changes sign across: the four
        // cells sharing such an edge each hold a vertex, and stitching those four together
        // gives one quad (two triangles).
        //
        // Driven by the list of ACTIVE cells rather than by scanning the lattice. The previous
        // version walked every edge of the whole grid once per axis - O(resolution^3) x 3 - and
        // benchmarking showed 99.4% of that was wasted: at resolution 256 it tested 45,687,370
        // edges to find 289,750 sign flips, and those three scans cost more than the extraction
        // they feed.
        //
        // Skipping straight to the active cells is exact, not an approximation. A sign-flipping
        // edge is one of the twelve edges of each of the four cells around it, so all four of
        // those cells have mixed corner signs and are active by definition. Taking the cell at
        // the maximum end of the edge in both cross-axis directions as that edge's single owner
        // gives exactly one owner per edge, so walking active cells and testing each one's three
        // owned edges reaches every quad exactly once - and reaches nothing else.
        private static void EmitDenseQuads(float[] sdf, int[] cellVertexIndex, List<int> activeCells, Vector3Int dims, int sx, int sy, MeshGeometryBuffer output)
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
                    StitchDenseQuad(cellVertexIndex, output, signA,
                        x, y - 1, z - 1, x, y, z - 1, x, y, z, x, y - 1, z, nx, ny);
                }

                // Edge along +Y. Its four cells step back in Z and X.
                if (z >= 1 && x >= 1 && (sdf[SampleIndex(x, y + 1, z, sx, sy)] < 0f) != signA)
                {
                    StitchDenseQuad(cellVertexIndex, output, signA,
                        x - 1, y, z - 1, x - 1, y, z, x, y, z, x, y, z - 1, nx, ny);
                }

                // Edge along +Z. Its four cells step back in X and Y.
                if (x >= 1 && y >= 1 && (sdf[SampleIndex(x, y, z + 1, sx, sy)] < 0f) != signA)
                {
                    StitchDenseQuad(cellVertexIndex, output, signA,
                        x - 1, y - 1, z, x, y - 1, z, x, y, z, x - 1, y, z, nx, ny);
                }
            }
        }

        // Turns the four cells around one sign-flipping edge into two triangles, wound so the
        // face points out of the solid (`insideFirst` is the sign at the edge's start sample).
        private static void StitchDenseQuad(int[] cellVertexIndex, MeshGeometryBuffer output, bool insideFirst,
                                            int ax, int ay, int az, int bx, int by, int bz,
                                            int cx, int cy, int cz, int dx, int dy, int dz, int nx, int ny)
        {
            int i0 = cellVertexIndex[ax + nx * (ay + ny * az)];
            int i1 = cellVertexIndex[bx + nx * (by + ny * bz)];
            int i2 = cellVertexIndex[cx + nx * (cy + ny * cz)];
            int i3 = cellVertexIndex[dx + nx * (dy + ny * dz)];

            if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0) return; // hole patching closes whatever this leaves

            if (insideFirst)
            {
                output.AddTriangle(i0, i1, i2);
                output.AddTriangle(i0, i2, i3);
            }
            else
            {
                output.AddTriangle(i0, i2, i1);
                output.AddTriangle(i0, i3, i2);
            }
        }

        // Packs a pair of vertex indices into one key. PatchHoles uses only the UNDIRECTED
        // form (smaller index packed first, so an edge has one key whichever triangle names
        // it); EdgeKey itself is the raw pack the undirected form is built from.
        private static long EdgeKey(int a, int b) => ((long)a << 32) | (uint)b;
        private static long UndirectedEdgeKey(int a, int b) => a < b ? EdgeKey(a, b) : EdgeKey(b, a);

        // PatchHoles' counting-sort buffers (see there). Same grow-on-demand reuse as the cell
        // buffers above - these are the largest transient allocations left in the pipeline, and
        // a remesh at maximum resolution would otherwise churn tens of megabytes per call.
        private static int[] _scratchEdgeStart = new int[0];
        private static int[] _scratchEdgeCursor = new int[0];
        private static int[] _scratchEdgeOther = new int[0];
        private static readonly List<long> _scratchBoundaryEdges = new List<long>();

        /// Finds every boundary edge the extraction left open - used by exactly one triangle,
        /// with no matching triangle on the other side - walks each into a closed loop, and
        /// caps it with a fan of triangles from a new centroid vertex. This is what makes the
        /// output watertight the way DynaMesh/Blender's Voxel Remesh guarantee, rather than
        /// leaving a permanent hole: one vertex per active grid cell means a genuinely concave
        /// pinch where two close sculpted features pass through the SAME cell as two distinct
        /// surface sheets can't be represented there. That one-vertex-per-cell ambiguity isn't
        /// fixable at the per-cell level; patching the resulting hole afterward is. A missing
        /// face has no vertex-position fix, which is why this couldn't be solved by
        /// smoothing/sculpting after the fact before this pass existed.
        ///
        /// No-ops (after one cheap O(triangle count) scan) on the overwhelmingly common
        /// watertight case - this only does real work on the rare geometry that actually needs
        /// it, and even then only touches the small boundary loops themselves.
        private static void PatchHoles(MeshGeometryBuffer buffer)
        {
            int triCount = buffer.TriangleCount;
            if (triCount == 0) return;
            int[] tris = buffer.Indices;

            // Finding the boundary edges is the only part of this method that costs anything on
            // the normal, watertight output - the patching below almost never runs at all. So it
            // is done with a counting sort into flat arrays rather than a hash map: every
            // undirected edge is bucketed by its LOWER vertex index, which leaves each vertex's
            // handful of edges (a dual vertex has ~4-6 neighbours) in one short contiguous run
            // that can be scanned directly. No hashing and no per-entry objects, and the common
            // "nothing to patch" answer comes back without building a map at all.
            int vertCount = buffer.VertexCount;
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
                Vector3 normal = Vector3.zero;
                for (int i = 0; i < loop.Count; i++)
                {
                    centroid += buffer.Vertices[loop[i]];
                    normal += buffer.Normals[loop[i]];
                }
                centroid /= loop.Count;
                int centroidIndex = buffer.AddVertex(centroid,
                    normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up);

                for (int i = 0; i < loop.Count; i++)
                {
                    int a = loop[i];
                    int b = loop[(i + 1) % loop.Count];
                    // Any two triangles sharing a manifold edge always traverse it in opposite
                    // directions - since the boundary edge itself is a->b, the cap triangle
                    // filling the gap on the other side must list it b->a to keep the new
                    // face's normal pointing the same way as the surrounding surface.
                    buffer.AddTriangle(b, a, centroidIndex);
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

        /// Trims the shared buffer down to arrays the caller owns, and computes the bounds
        /// while the vertices are still hot in cache (Mesh.RecalculateBounds would walk them
        /// again from managed memory).
        private static RemeshResult Finish(MeshGeometryBuffer buffer)
        {
            var result = new RemeshResult
            {
                Vertices = buffer.CopyVertices(),
                Normals = buffer.CopyNormals(),
                Triangles = buffer.CopyIndices()
            };

            if (result.Vertices.Length > 0)
            {
                Vector3 min = result.Vertices[0], max = result.Vertices[0];
                for (int i = 1; i < result.Vertices.Length; i++)
                {
                    min = Vector3.Min(min, result.Vertices[i]);
                    max = Vector3.Max(max, result.Vertices[i]);
                }
                var b = new Bounds();
                b.SetMinMax(min, max);
                result.Bounds = b;
            }

            // The copies above are the caller's now, so anything the shared buffer is still
            // holding beyond what the next remesh is likely to need is dead weight.
            buffer.TrimExcess();
            return result;
        }

        /// Wraps a result up as a Mesh, for callers that want one directly.
        ///
        /// Deliberately does NOT generate UVs or tangents, and does not call
        /// RecalculateNormals. The normals are already exact - they come from the source
        /// surface itself rather than from re-averaging the discretized triangles - and the
        /// other two were pure waste: SculptableMesh re-specifies the vertex buffer as
        /// position/normal/colour the moment it takes the mesh, and SculptPBR's vertex input
        /// has no TEXCOORD0 or TANGENT, so a spherical UV projection (an Atan2 and an Asin per
        /// vertex, single-threaded) and a full tangent solve were both computed and then
        /// immediately discarded, on every remesh, at millions of vertices.
        internal static Mesh BuildMesh(RemeshResult result)
        {
            var mesh = new Mesh
            {
                indexFormat = result.Vertices.Length > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16
            };
            mesh.SetVertices(result.Vertices);
            mesh.SetNormals(result.Normals);
            mesh.SetTriangles(result.Triangles, 0);
            mesh.bounds = result.Bounds;
            return mesh;
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
