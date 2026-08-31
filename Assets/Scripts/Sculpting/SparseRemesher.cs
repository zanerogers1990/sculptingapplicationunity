using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Sculpting
{
    /// The remesh pipeline's extraction stage, restructured so its cost and its memory both
    /// scale with the SURFACE (resolution squared) instead of with the volume (resolution
    /// cubed).
    ///
    /// Why it had to change: the previous pipeline allocated six whole-lattice arrays - a float
    /// distance per sample, two booleans per sample, and a bool/Vector3/int per cell, 23 bytes
    /// per lattice site - and scanned all of them. Measured, that is 388 MB of live arrays at
    /// resolution 256, 2.8 GB at 500, and (extrapolating the same cubic) 22 GB at 1024. The
    /// OUTPUT over that same range only goes from 0.7M to 2.7M to ~6.6M triangles, because a
    /// surface is two-dimensional: at resolution 500 more than 99.5% of those cells contain no
    /// surface at all and exist only to be skipped. That mismatch, not the extraction work, is
    /// what put a ceiling on the triangle count - the pipeline ran out of address space and
    /// allocator patience long before it ran out of time.
    ///
    /// How this version avoids it:
    ///
    ///   1. The set of cells that can hold surface is read off the SOURCE MESH, not discovered
    ///      by scanning the lattice. Every source triangle is split until it is at most one
    ///      brick across and its (dilated) bounds mark the 8x8x8 bricks it touches. That is a
    ///      strict superset of the active cells - a sign change between two neighbouring
    ///      samples means the segment between them crosses a triangle, and that triangle's
    ///      bounds therefore overlap the same brick - so nothing can be missed, and it costs
    ///      O(source area), never O(resolution^3).
    ///
    ///   2. There is no whole-grid sign array. Signs come from the same winding-number rays as
    ///      before, but each brick casts the 81 columns it needs, when it needs them, and keeps
    ///      729 bits. Bricks are independent, so this is the most parallel part of the pipeline
    ///      rather than a shared array every thread writes into.
    ///
    ///   3. Nothing else is per-cell either. The only retained per-brick state is 512 vertex
    ///      indices and 96 bytes of sign bits; the distances and nearest-triangle indices live
    ///      in per-THREAD scratch that is reused brick after brick, so the working set for that
    ///      stage is a few kilobytes per core regardless of resolution.
    ///
    /// Vertex placement is DualContourSolver's, not the average of the edge crossings - see
    /// there for why the average is what made every remesh round off sharp detail.
    ///
    /// Runs synchronously on the calling thread (its passes are parallel internally), same
    /// contract as the rest of the remesher.
    internal static class SparseRemesher
    {
        /// Cells along one brick edge. 8 keeps a brick's corner samples (9^3 = 729) inside a
        /// few cache lines per axis and its retained cell-index table at 2 KB, while being
        /// coarse enough that a surface brick is still mostly full. Larger bricks waste more of
        /// that table on empty interior cells (a surface brick holds about 8x8 active cells out
        /// of 8x8x8); smaller ones multiply the shared-face sample duplication.
        public const int BrickSize = 8;

        private const int BrickCells = BrickSize * BrickSize * BrickSize;          // 512
        private const int BrickSpan = BrickSize + 1;                               // corner samples per axis
        private const int BrickSamples = BrickSpan * BrickSpan * BrickSpan;        // 729
        private const int SignWords = (BrickSamples + 63) / 64;                    // 12

        /// Cell corners in the same order MeshRemesher uses: corner i is (i&1, (i>>1)&1, (i>>2)&1).
        private static readonly int[] CornerX = { 0, 1, 0, 1, 0, 1, 0, 1 };
        private static readonly int[] CornerY = { 0, 0, 1, 1, 0, 0, 1, 1 };
        private static readonly int[] CornerZ = { 0, 0, 0, 0, 1, 1, 1, 1 };

        /// The 12 cube edges as corner-index pairs - every pair differing in exactly one bit.
        private static readonly int[] EdgeA = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
        private static readonly int[] EdgeB = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };

        /// Counts worth surfacing to a caller that wants to report or test them.
        public struct Stats
        {
            public int ActiveBricks;
            public int TotalBricks;
            public int Vertices;
            public int Triangles;
            /// Quads skipped because one of the four cells around a sign-flipping edge had no
            /// vertex. Should be zero; a non-zero value means brick activation missed a cell,
            /// and whatever hole it leaves is closed by MeshRemesher's hole patching.
            public int SkippedQuads;
            public long RetainedBytes;
        }

        public static void Build(Vector3[] sourceVertices, int[] sourceTriangles,
                                 Vector3 origin, float cellSize, Vector3Int dims,
                                 MeshGeometryBuffer output, out Stats stats)
        {
            stats = default;
            output.Reset();

            int nx = dims.x, ny = dims.y, nz = dims.z;
            int bnx = (nx + BrickSize - 1) / BrickSize;
            int bny = (ny + BrickSize - 1) / BrickSize;
            int bnz = (nz + BrickSize - 1) / BrickSize;
            int brickCount = bnx * bny * bnz;
            stats.TotalBricks = brickCount;
            if (brickCount <= 0) return;

            // ---- 1. Which bricks can hold surface, straight from the source triangles ----
            var brickActive = new byte[brickCount];
            MarkBricks(sourceVertices, sourceTriangles, origin, cellSize, bnx, bny, bnz, brickActive);

            var brickSlot = new int[brickCount];
            int slotCount = 0;
            for (int i = 0; i < brickCount; i++) brickSlot[i] = brickActive[i] != 0 ? slotCount++ : -1;
            stats.ActiveBricks = slotCount;
            if (slotCount == 0) return;

            var slotBrick = new int[slotCount];
            for (int i = 0; i < brickCount; i++) if (brickSlot[i] >= 0) slotBrick[brickSlot[i]] = i;

            var signBits = new ulong[(long)slotCount * SignWords];
            var cellVertex = new int[(long)slotCount * BrickCells];
            stats.RetainedBytes = (long)signBits.Length * 8 + (long)cellVertex.Length * 4
                                + brickSlot.Length * 4L + slotBrick.Length * 4L + brickActive.Length;

            // The triangle-lookup accelerator is sized off the SOURCE mesh's own triangle
            // density (about one triangle per bin), never off the output resolution. Sizing it
            // off the output made a coarse source at a fine target spread every large triangle
            // across thousands of tiny bins, which is a cost that grows with a number that has
            // nothing to do with the input.
            Bounds sourceBounds = MeshRemesher.ComputeBounds(sourceVertices);
            float sourceExtent = Mathf.Max(sourceBounds.size.x, sourceBounds.size.y, sourceBounds.size.z, 0.0001f);
            float triCount = Mathf.Max(1, sourceTriangles.Length / 3);
            float binCellSize = Mathf.Clamp(sourceExtent / Mathf.Pow(triCount, 1f / 3f), sourceExtent * 0.001f, sourceExtent);
            var field = new SignedDistanceField(sourceVertices, sourceTriangles, binCellSize);
            float rayStartX = Mathf.Min(origin.x, field.MinX);

            // ---- 2. Signs, per brick, from its own winding-number rays ----
            var perBrickVertices = new int[slotCount];
            Parallel.For(0, slotCount, () => new Scratch(), (slot, _, scratch) =>
            {
                int brick = slotBrick[slot];
                DecodeBrick(brick, bnx, bny, out int bx, out int by, out int bz);
                ComputeSigns(field, signBits, slot, bx, by, bz, origin, cellSize, rayStartX, scratch);
                perBrickVertices[slot] = CountActiveCells(signBits, slot, bx, by, bz, nx, ny, nz);
                return scratch;
            }, _ => { });

            // ---- 3. Exclusive prefix sum: every brick's vertex block, in brick order ----
            var vertexBase = new int[slotCount + 1];
            int running = 0;
            for (int i = 0; i < slotCount; i++) { vertexBase[i] = running; running += perBrickVertices[i]; }
            vertexBase[slotCount] = running;

            stats.Vertices = running;
            if (running == 0) return;
            output.EnsureVertexCapacity(running + 1024); // headroom for hole-patch centroids
            output.VertexCount = running;

            // ---- 4. Distances, dual vertices and normals - the only stage that queries the
            //         source mesh, and it runs once per ACTIVE CELL rather than per lattice site
            Parallel.For(0, slotCount, () => new Scratch(), (slot, _, scratch) =>
            {
                int brick = slotBrick[slot];
                DecodeBrick(brick, bnx, bny, out int bx, out int by, out int bz);
                SolveBrickVertices(field, signBits, cellVertex, slot, bx, by, bz, nx, ny, nz,
                                   origin, cellSize, vertexBase[slot], output, scratch);
                return scratch;
            }, _ => { });

            // ---- 5. Quads. Counted first so the emit pass can write disjoint index ranges ----
            var perBrickIndices = new int[slotCount];
            Parallel.For(0, slotCount, slot =>
            {
                int brick = slotBrick[slot];
                DecodeBrick(brick, bnx, bny, out int bx, out int by, out int bz);
                perBrickIndices[slot] = EmitQuads(signBits, cellVertex, brickSlot, slot, bx, by, bz,
                                                  nx, ny, nz, bnx, bny, bnz, output, -1, out _);
            });

            var indexBase = new int[slotCount + 1];
            running = 0;
            for (int i = 0; i < slotCount; i++) { indexBase[i] = running; running += perBrickIndices[i]; }
            indexBase[slotCount] = running;

            output.EnsureIndexCapacity(running);
            output.IndexCount = running;
            stats.Triangles = running / 3;

            int skipped = 0;
            Parallel.For(0, slotCount, slot =>
            {
                int brick = slotBrick[slot];
                DecodeBrick(brick, bnx, bny, out int bx, out int by, out int bz);
                EmitQuads(signBits, cellVertex, brickSlot, slot, bx, by, bz,
                          nx, ny, nz, bnx, bny, bnz, output, indexBase[slot], out int localSkipped);
                if (localSkipped != 0) System.Threading.Interlocked.Add(ref skipped, localSkipped);
            });
            stats.SkippedQuads = skipped;
        }

        private static void DecodeBrick(int brick, int bnx, int bny, out int bx, out int by, out int bz)
        {
            int slice = bnx * bny;
            bz = brick / slice;
            int rem = brick - bz * slice;
            by = rem / bnx;
            bx = rem - by * bnx;
        }

        // ---------------------------------------------------------------- brick occupancy

        /// A triangle split down until it is at most one brick across, so the bricks it touches
        /// can be marked from its bounding box without that box being a wild over-estimate.
        private struct SplitTri
        {
            public Vector3 A, B, C;
            public int Depth;
        }

        /// Marks every brick the source surface can pass through.
        ///
        /// The bounding box of a triangle is a fine proxy for the bricks it occupies only while
        /// the triangle is small. A big diagonal one - a coarse primitive remeshed at high
        /// density is exactly that - has a box spanning most of the grid, and marking it whole
        /// would activate a volume's worth of bricks and give back the O(resolution^3) cost this
        /// class exists to avoid. Splitting at the midpoints until a triangle is brick-sized
        /// bounds the total work by the surface AREA in bricks instead, which is the same order
        /// as the answer itself.
        private static void MarkBricks(Vector3[] verts, int[] tris, Vector3 origin, float cellSize,
                                       int bnx, int bny, int bnz, byte[] brickActive)
        {
            float brickWorld = BrickSize * cellSize;
            int triangleCount = tris.Length / 3;

            Parallel.For(0, triangleCount, () => new Stack<SplitTri>(), (t, _, stack) =>
            {
                stack.Push(new SplitTri
                {
                    A = verts[tris[t * 3]],
                    B = verts[tris[t * 3 + 1]],
                    C = verts[tris[t * 3 + 2]],
                    Depth = 0
                });

                while (stack.Count > 0)
                {
                    SplitTri s = stack.Pop();
                    Vector3 mn = Vector3.Min(s.A, Vector3.Min(s.B, s.C));
                    Vector3 mx = Vector3.Max(s.A, Vector3.Max(s.B, s.C));
                    Vector3 span = mx - mn;

                    // Depth cap is a guard against degenerate input (a NaN vertex makes every
                    // comparison false and the span never shrinks), not something a well-formed
                    // mesh reaches: 16 levels is a 65536-fold reduction in edge length.
                    if (s.Depth < 16 && Mathf.Max(span.x, span.y, span.z) > brickWorld)
                    {
                        Vector3 ab = (s.A + s.B) * 0.5f, bc = (s.B + s.C) * 0.5f, ca = (s.C + s.A) * 0.5f;
                        int d = s.Depth + 1;
                        stack.Push(new SplitTri { A = s.A, B = ab, C = ca, Depth = d });
                        stack.Push(new SplitTri { A = ab, B = s.B, C = bc, Depth = d });
                        stack.Push(new SplitTri { A = ca, B = bc, C = s.C, Depth = d });
                        stack.Push(new SplitTri { A = ab, B = bc, C = ca, Depth = d });
                        continue;
                    }

                    // Dilated by a cell so the samples one step OUTSIDE the surface - the other
                    // end of every sign-flipping edge - are covered too.
                    MarkRange(mn - Vector3.one * cellSize, mx + Vector3.one * cellSize,
                              origin, cellSize, bnx, bny, bnz, brickActive);
                }

                return stack;
            }, _ => { });
        }

        private static void MarkRange(Vector3 mn, Vector3 mx, Vector3 origin, float cellSize,
                                      int bnx, int bny, int bnz, byte[] brickActive)
        {
            float brickWorld = BrickSize * cellSize;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((mn.x - origin.x) / brickWorld), 0, bnx - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((mx.x - origin.x) / brickWorld), 0, bnx - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((mn.y - origin.y) / brickWorld), 0, bny - 1);
            int y1 = Mathf.Clamp(Mathf.FloorToInt((mx.y - origin.y) / brickWorld), 0, bny - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((mn.z - origin.z) / brickWorld), 0, bnz - 1);
            int z1 = Mathf.Clamp(Mathf.FloorToInt((mx.z - origin.z) / brickWorld), 0, bnz - 1);

            // Concurrent triangles overlapping the same brick all write the same 1 here, so the
            // race is benign - a byte store cannot tear, and there is no read-modify-write.
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            {
                int row = bnx * (y + bny * z);
                for (int x = x0; x <= x1; x++) brickActive[row + x] = 1;
            }
        }

        // ---------------------------------------------------------------- per-brick work

        /// Per-thread reusable buffers. Everything a brick needs while it is being processed
        /// lives here and is handed to the next brick on the same thread, so the pipeline's
        /// transient footprint is a few kilobytes per core no matter how large the grid is.
        private sealed class Scratch
        {
            public readonly List<SignedDistanceField.Crossing> Crossings = new List<SignedDistanceField.Crossing>();
            public readonly float[] Distance = new float[BrickSamples];
            public readonly int[] Triangle = new int[BrickSamples];
            public readonly ulong[] HasDistance = new ulong[SignWords];
            public readonly Vector3[] Points = new Vector3[12];
            public readonly Vector3[] Normals = new Vector3[12];
        }

        private static int SampleIndex(int lx, int ly, int lz) => lx + BrickSpan * (ly + BrickSpan * lz);
        private static int CellIndex(int cx, int cy, int cz) => cx + BrickSize * (cy + BrickSize * cz);

        private static bool GetBit(ulong[] bits, long wordBase, int i)
            => (bits[wordBase + (i >> 6)] & (1UL << (i & 63))) != 0UL;

        private static void SetBit(ulong[] bits, long wordBase, int i)
            => bits[wordBase + (i >> 6)] |= 1UL << (i & 63);

        /// Inside/outside for this brick's 729 corner samples, one +X winding ray per (y,z)
        /// column. Identical in meaning to the dense pipeline's whole-grid inside mask - same
        /// rays, same sample positions, same nudge off the sample line so a grid-aligned mesh
        /// doesn't graze triangle edges along a whole column - just computed where it is needed
        /// instead of everywhere.
        private static void ComputeSigns(SignedDistanceField field, ulong[] signBits, int slot,
                                         int bx, int by, int bz, Vector3 origin, float cellSize,
                                         float rayStartX, Scratch scratch)
        {
            long wordBase = (long)slot * SignWords;
            for (int w = 0; w < SignWords; w++) signBits[wordBase + w] = 0UL;

            int x0 = bx * BrickSize, y0 = by * BrickSize, z0 = bz * BrickSize;
            var crossings = scratch.Crossings;

            for (int lz = 0; lz < BrickSpan; lz++)
            for (int ly = 0; ly < BrickSpan; ly++)
            {
                float wy = origin.y + (y0 + ly) * cellSize + cellSize * 0.0173f;
                float wz = origin.z + (z0 + lz) * cellSize + cellSize * 0.0091f;
                field.ColumnCrossings(wy, wz, rayStartX, crossings);
                if (crossings.Count == 0) continue;

                int ci = 0, winding = 0;
                for (int lx = 0; lx < BrickSpan; lx++)
                {
                    float wx = origin.x + (x0 + lx) * cellSize;
                    while (ci < crossings.Count && crossings[ci].X < wx)
                    {
                        winding += crossings[ci].Winding;
                        ci++;
                    }
                    if (winding != 0) SetBit(signBits, wordBase, SampleIndex(lx, ly, lz));
                }
            }
        }

        /// How many of this brick's cells straddle the surface - i.e. how many output vertices
        /// it owns. Pure bit reads; no distance query and no source-mesh access.
        private static int CountActiveCells(ulong[] signBits, int slot, int bx, int by, int bz,
                                            int nx, int ny, int nz)
        {
            long wordBase = (long)slot * SignWords;
            int x0 = bx * BrickSize, y0 = by * BrickSize, z0 = bz * BrickSize;
            int count = 0;

            for (int cz = 0; cz < BrickSize; cz++)
            {
                if (z0 + cz >= nz) break;
                for (int cy = 0; cy < BrickSize; cy++)
                {
                    if (y0 + cy >= ny) break;
                    for (int cx = 0; cx < BrickSize; cx++)
                    {
                        if (x0 + cx >= nx) break;
                        if (IsActiveCell(signBits, wordBase, cx, cy, cz)) count++;
                    }
                }
            }
            return count;
        }

        private static bool IsActiveCell(ulong[] signBits, long wordBase, int cx, int cy, int cz)
        {
            bool first = GetBit(signBits, wordBase, SampleIndex(cx, cy, cz));
            for (int c = 1; c < 8; c++)
                if (GetBit(signBits, wordBase, SampleIndex(cx + CornerX[c], cy + CornerY[c], cz + CornerZ[c])) != first)
                    return true;
            return false;
        }

        /// The stage that actually costs something: for each active cell, the eight corner
        /// distances (cached per brick, so a corner shared by up to eight cells is queried
        /// once), the edge crossings, and the feature-preserving vertex solve.
        private static void SolveBrickVertices(SignedDistanceField field, ulong[] signBits, int[] cellVertex,
                                               int slot, int bx, int by, int bz, int nx, int ny, int nz,
                                               Vector3 origin, float cellSize, int vertexBase,
                                               MeshGeometryBuffer output, Scratch scratch)
        {
            long wordBase = (long)slot * SignWords;
            long cellBase = (long)slot * BrickCells;
            int x0 = bx * BrickSize, y0 = by * BrickSize, z0 = bz * BrickSize;

            for (int i = 0; i < BrickCells; i++) cellVertex[cellBase + i] = -1;
            for (int w = 0; w < SignWords; w++) scratch.HasDistance[w] = 0UL;

            int next = vertexBase;
            Span<float> corner = stackalloc float[8];
            Span<bool> cornerInside = stackalloc bool[8];

            for (int cz = 0; cz < BrickSize; cz++)
            {
                if (z0 + cz >= nz) break;
                for (int cy = 0; cy < BrickSize; cy++)
                {
                    if (y0 + cy >= ny) break;
                    for (int cx = 0; cx < BrickSize; cx++)
                    {
                        if (x0 + cx >= nx) break;
                        if (!IsActiveCell(signBits, wordBase, cx, cy, cz)) continue;

                        for (int c = 0; c < 8; c++)
                        {
                            int s = SampleIndex(cx + CornerX[c], cy + CornerY[c], cz + CornerZ[c]);
                            EnsureDistance(field, signBits, wordBase, s, x0, y0, z0, origin, cellSize, scratch);
                            corner[c] = scratch.Distance[s];
                            cornerInside[c] = GetBit(signBits, wordBase, s);
                        }

                        int crossings = 0;
                        for (int e = 0; e < 12; e++)
                        {
                            int a = EdgeA[e], b = EdgeB[e];
                            // Which side each corner is on comes from the SIGN BITS, never from
                            // the sign of the stored distance. They are almost always the same
                            // thing, and the exception is not exotic: a sample lying exactly ON
                            // the surface has distance 0, and an inside one then stores -0.0f,
                            // for which `< 0f` is FALSE. Any mesh with a face flush against a
                            // sample plane - an unrotated box, a fresh primitive, anything
                            // snapped to the grid - has thousands of those, and reading the two
                            // in different ways made this pass disagree with the pass that
                            // decides which cells are active, which left holes exactly there.
                            // Measured on an axis-aligned box at resolution 128: 50,022 quads
                            // dropped and the surface broken into 841 pieces.
                            bool ia = cornerInside[a], ib = cornerInside[b];
                            if (ia == ib) continue;

                            float va = corner[a], vb = corner[b];
                            // Both endpoints sitting exactly on the surface leaves nothing to
                            // interpolate; the midpoint is the only unbiased answer.
                            float denom = va - vb;
                            float t = Mathf.Abs(denom) > 1e-20f ? Mathf.Clamp01(va / denom) : 0.5f;
                            scratch.Points[crossings] = new Vector3(
                                CornerX[a] + (CornerX[b] - CornerX[a]) * t,
                                CornerY[a] + (CornerY[b] - CornerY[a]) * t,
                                CornerZ[a] + (CornerZ[b] - CornerZ[a]) * t);

                            // The surface normal AT the crossing, taken from the source
                            // triangle nearest whichever end of the edge is closer to it. That
                            // triangle fell out of the distance query already made for that
                            // corner, so the normal is free - and it is a TRUE face normal, not
                            // a finite difference of the sampled field, which is what lets the
                            // solver reconstruct a crease instead of a smoothed approximation
                            // of one.
                            int nearest = Mathf.Abs(va) <= Mathf.Abs(vb)
                                ? scratch.Triangle[SampleIndex(cx + CornerX[a], cy + CornerY[a], cz + CornerZ[a])]
                                : scratch.Triangle[SampleIndex(cx + CornerX[b], cy + CornerY[b], cz + CornerZ[b])];
                            scratch.Normals[crossings] = nearest >= 0 ? field.TriangleNormal(nearest) : Vector3.zero;
                            crossings++;
                        }

                        if (crossings == 0) continue; // corners disagree but no edge does: nothing to place

                        Vector3 local = DualContourSolver.Solve(scratch.Points, scratch.Normals, crossings);
                        Vector3 world = origin + (new Vector3(x0 + cx, y0 + cy, z0 + cz) + local) * cellSize;

                        Vector3 normal = Vector3.zero;
                        for (int i = 0; i < crossings; i++) normal += scratch.Normals[i];
                        normal = normal.sqrMagnitude > 1e-12f ? normal.normalized : Vector3.up;

                        output.Vertices[next] = world;
                        output.Normals[next] = normal;
                        cellVertex[cellBase + CellIndex(cx, cy, cz)] = next;
                        next++;
                    }
                }
            }
        }

        private static void EnsureDistance(SignedDistanceField field, ulong[] signBits, long wordBase, int s,
                                           int x0, int y0, int z0, Vector3 origin, float cellSize, Scratch scratch)
        {
            if (GetBit(scratch.HasDistance, 0, s)) return;
            SetBit(scratch.HasDistance, 0, s);

            int lx = s % BrickSpan;
            int ly = (s / BrickSpan) % BrickSpan;
            int lz = s / (BrickSpan * BrickSpan);
            var p = new Vector3(
                origin.x + (x0 + lx) * cellSize,
                origin.y + (y0 + ly) * cellSize,
                origin.z + (z0 + lz) * cellSize);

            float d = field.NearestUnsignedDistance(p, out int triangle);
            scratch.Distance[s] = GetBit(signBits, wordBase, s) ? -d : d;
            scratch.Triangle[s] = triangle;
        }

        // ---------------------------------------------------------------- stitching

        /// Emits (or, with `indexBase` negative, only counts) the quads this brick owns.
        ///
        /// Ownership follows the same rule the dense pipeline settled on: a lattice edge the
        /// field changes sign across is shared by four cells, and the cell at the maximum end in
        /// both cross-axis directions is its single owner. So walking active cells and testing
        /// each one's three owned edges reaches every quad exactly once - and reaches nothing
        /// else, which is the point.
        ///
        /// Returns the number of INDICES written (or that would be written).
        private static int EmitQuads(ulong[] signBits, int[] cellVertex, int[] brickSlot,
                                     int slot, int bx, int by, int bz,
                                     int nx, int ny, int nz, int bnx, int bny, int bnz,
                                     MeshGeometryBuffer output, int indexBase, out int skipped)
        {
            long wordBase = (long)slot * SignWords;
            int x0 = bx * BrickSize, y0 = by * BrickSize, z0 = bz * BrickSize;
            int written = 0;
            int cursor = indexBase;
            // Plain local rather than the out parameter directly: a local function cannot
            // capture an out/ref parameter, and Stitch below needs to bump it.
            int missing = 0;

            for (int cz = 0; cz < BrickSize; cz++)
            {
                int Z = z0 + cz; if (Z >= nz) break;
                for (int cy = 0; cy < BrickSize; cy++)
                {
                    int Y = y0 + cy; if (Y >= ny) break;
                    for (int cx = 0; cx < BrickSize; cx++)
                    {
                        int X = x0 + cx; if (X >= nx) break;
                        if (!IsActiveCell(signBits, wordBase, cx, cy, cz)) continue;

                        bool signA = GetBit(signBits, wordBase, SampleIndex(cx, cy, cz));

                        // Edge along +X. Its four cells step back in Y and Z.
                        if (Y >= 1 && Z >= 1 && GetBit(signBits, wordBase, SampleIndex(cx + 1, cy, cz)) != signA)
                            Stitch(X, Y - 1, Z - 1, X, Y, Z - 1, X, Y, Z, X, Y - 1, Z);

                        // Edge along +Y. Its four cells step back in Z and X.
                        if (Z >= 1 && X >= 1 && GetBit(signBits, wordBase, SampleIndex(cx, cy + 1, cz)) != signA)
                            Stitch(X - 1, Y, Z - 1, X - 1, Y, Z, X, Y, Z, X, Y, Z - 1);

                        // Edge along +Z. Its four cells step back in X and Y.
                        if (X >= 1 && Y >= 1 && GetBit(signBits, wordBase, SampleIndex(cx, cy, cz + 1)) != signA)
                            Stitch(X - 1, Y - 1, Z, X, Y - 1, Z, X, Y, Z, X - 1, Y, Z);

                        void Stitch(int ax, int ay, int az, int bx2, int by2, int bz2,
                                    int cx2, int cy2, int cz2, int dx, int dy, int dz)
                        {
                            int i0 = VertexAt(ax, ay, az);
                            int i1 = VertexAt(bx2, by2, bz2);
                            int i2 = VertexAt(cx2, cy2, cz2);
                            int i3 = VertexAt(dx, dy, dz);
                            if (i0 < 0 || i1 < 0 || i2 < 0 || i3 < 0) { missing++; return; }

                            written += 6;
                            if (indexBase < 0) return;

                            int[] idx = output.Indices;
                            if (signA)
                            {
                                idx[cursor] = i0; idx[cursor + 1] = i1; idx[cursor + 2] = i2;
                                idx[cursor + 3] = i0; idx[cursor + 4] = i2; idx[cursor + 5] = i3;
                            }
                            else
                            {
                                idx[cursor] = i0; idx[cursor + 1] = i2; idx[cursor + 2] = i1;
                                idx[cursor + 3] = i0; idx[cursor + 4] = i3; idx[cursor + 5] = i2;
                            }
                            cursor += 6;
                        }
                    }
                }
            }

            skipped = missing;
            return written;

            int VertexAt(int X, int Y, int Z)
            {
                if ((uint)X >= (uint)nx || (uint)Y >= (uint)ny || (uint)Z >= (uint)nz) return -1;
                int b = (X / BrickSize) + bnx * ((Y / BrickSize) + bny * (Z / BrickSize));
                int s = brickSlot[b];
                if (s < 0) return -1;
                return cellVertex[(long)s * BrickCells
                                  + CellIndex(X % BrickSize, Y % BrickSize, Z % BrickSize)];
            }
        }
    }
}
