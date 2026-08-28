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
            return new Vector3Int(
                Mathf.CeilToInt(bounds.size.x / cellSize) + pad * 2,
                Mathf.CeilToInt(bounds.size.y / cellSize) + pad * 2,
                Mathf.CeilToInt(bounds.size.z / cellSize) + pad * 2);
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

            for (int z = 0; z < nz; z++)
            for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                int cellIndex = x + nx * (y + ny * z);
                if (!cellHasVertex[cellIndex]) { cellVertexIndex[cellIndex] = -1; continue; }

                Vector3 worldPos = origin + (new Vector3(x, y, z) + cellLocalPos[cellIndex]) * cellSize;
                cellVertexIndex[cellIndex] = verts.Count;
                verts.Add(worldPos);
            }

            var tris = _scratchTris;
            tris.Clear();
            EmitQuads(sdf, cellVertexIndex, verts, dims, sx, sy, tris, origin, cellSize, axis: 0);
            EmitQuads(sdf, cellVertexIndex, verts, dims, sx, sy, tris, origin, cellSize, axis: 1);
            EmitQuads(sdf, cellVertexIndex, verts, dims, sx, sy, tris, origin, cellSize, axis: 2);

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

        // Walks grid-lattice edges along `axis`; wherever the SDF changes sign across one,
        // the four cells sharing that edge each hold a Surface Nets vertex - stitch them into a quad.
        //
        // This loop runs O(resolution^3) times per axis (called once per axis, 3 total), so
        // it used to allocate six small int[3] arrays (`sample`, `sampleNext`, `c0`..`c3`) on
        // every single iteration - at high resolutions those billions of tiny heap allocations
        // (not the arithmetic itself) were the dominant remaining cost after the SDF-sampling
        // and BuildSurface-cell-loop fixes above stopped moving the needle any further.
        // SampleAt/CellXYZ below map (axis-coordinate, u-coordinate, v-coordinate) straight to
        // a flat index with no allocation at all.
        private static void EmitQuads(float[] sdf, int[] cellVertexIndex, List<Vector3> verts, Vector3Int dims, int sx, int sy, List<int> tris, Vector3 origin, float cellSize, int axis)
        {
            int nx = dims.x, ny = dims.y, nz = dims.z;
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;
            int dimAxis = axis == 0 ? nx : axis == 1 ? ny : nz;
            int dimU = u == 0 ? nx : u == 1 ? ny : nz;
            int dimV = v == 0 ? nx : v == 1 ? ny : nz;

            int SampleAt(int a, int b, int c)
            {
                int x = axis == 0 ? a : u == 0 ? b : c;
                int y = axis == 1 ? a : u == 1 ? b : c;
                int z = axis == 2 ? a : u == 2 ? b : c;
                return SampleIndex(x, y, z, sx, sy);
            }
            Vector3Int CellXYZ(int a, int b, int c)
            {
                int x = axis == 0 ? a : u == 0 ? b : c;
                int y = axis == 1 ? a : u == 1 ? b : c;
                int z = axis == 2 ? a : u == 2 ? b : c;
                return new Vector3Int(x, y, z);
            }

            for (int ea = 0; ea < dimAxis; ea++)
            for (int bI = 1; bI < dimU; bI++)
            for (int cI = 1; cI < dimV; cI++)
            {
                float va = sdf[SampleAt(ea, bI, cI)];
                float vb = sdf[SampleAt(ea + 1, bI, cI)];
                bool signA = va < 0f, signB = vb < 0f;
                if (signA == signB) continue;

                // The sign flip just confirmed above means all four of these cells MUST
                // contain a crossing (this exact edge is one of each cell's 12) and so should
                // already hold a Surface Nets vertex from BuildSurface's pass 1 - but on rare
                // sculpted geometry (two close/near-touching features meeting near the same
                // cell) that independent per-cell mask check can disagree with this direct
                // edge check by one cell, which used to silently drop the whole quad here and
                // leave a permanent hole/crack in the output mesh (a genuine missing face -
                // no amount of later smoothing or clay can weld it back, since there's no
                // vertex-position fix for a triangle that was never emitted). Falling back to
                // synthesizing the missing corner's vertex on demand - it's still a genuine
                // Surface Nets vertex for that cell, just computed lazily instead of during
                // the up-front parallel pass - keeps every crossing edge closed.
                Vector3Int p0 = CellXYZ(ea, bI - 1, cI - 1);
                Vector3Int p1 = CellXYZ(ea, bI, cI - 1);
                Vector3Int p2 = CellXYZ(ea, bI, cI);
                Vector3Int p3 = CellXYZ(ea, bI - 1, cI);

                int i0 = GetOrCreateCellVertex(sdf, cellVertexIndex, verts, p0, nx, ny, sx, sy, origin, cellSize);
                int i1 = GetOrCreateCellVertex(sdf, cellVertexIndex, verts, p1, nx, ny, sx, sy, origin, cellSize);
                int i2 = GetOrCreateCellVertex(sdf, cellVertexIndex, verts, p2, nx, ny, sx, sy, origin, cellSize);
                int i3 = GetOrCreateCellVertex(sdf, cellVertexIndex, verts, p3, nx, ny, sx, sy, origin, cellSize);

                if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0) continue; // truly degenerate (out of grid bounds) - not the hole-causing case above

                if (signA)
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

        // Packs an ordered pair of vertex indices into one key. Used two ways below: as a
        // DIRECTED key (a,b distinct from b,a) when counting triangle-edge occurrences isn't
        // needed, and via UndirectedEdgeKey (always packing the smaller index first) when it is.
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
            var edgeCount = new Dictionary<long, int>(triCount * 3 / 2);
            for (int t = 0; t < triCount; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                IncrementEdge(edgeCount, a, b);
                IncrementEdge(edgeCount, b, c);
                IncrementEdge(edgeCount, c, a);
            }

            // boundaryNext[a] = b means the directed edge a->b (as some triangle listed it) has
            // no partner triangle traversing it b->a - the classic definition of a mesh
            // boundary edge, and its direction is exactly the "walk the hole's rim consistently
            // with the surrounding surface's winding" direction.
            var boundaryNext = new Dictionary<int, int>();
            for (int t = 0; t < triCount; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                RecordIfBoundary(edgeCount, boundaryNext, a, b);
                RecordIfBoundary(edgeCount, boundaryNext, b, c);
                RecordIfBoundary(edgeCount, boundaryNext, c, a);
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

        private static void IncrementEdge(Dictionary<long, int> counts, int a, int b)
        {
            long key = UndirectedEdgeKey(a, b);
            counts.TryGetValue(key, out int existing);
            counts[key] = existing + 1;
        }

        private static void RecordIfBoundary(Dictionary<long, int> counts, Dictionary<int, int> boundaryNext, int a, int b)
        {
            if (counts[UndirectedEdgeKey(a, b)] != 1) return;
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
