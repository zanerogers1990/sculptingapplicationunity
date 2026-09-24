using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Uniform grid bucketing triangle indices by cell, used to raycast brush hit-testing
    /// directly against the sculpt's own live vertex data instead of through a MeshCollider -
    /// replaces Physics.Raycast against a MeshCollider whose sharedMesh has to be reassigned
    /// every time geometry changes to keep hit-testing following the sculpted surface, forcing
    /// a full PhysX re-cook (measured ~35ms/call at ~144k triangles, ~95% of a brush-application
    /// frame - see SculptableMesh.ApplyVertices).
    ///
    /// Mutable and incrementally updatable (UpdateFromMovedVertices), NOT rebuilt from scratch
    /// every frame: a brush stroke only ever touches a tiny fraction of a mesh's triangles, so an
    /// O(total triangles) step per frame is the wrong shape for this problem. Only triangles whose
    /// vertices actually changed cell are re-bucketed.
    ///
    /// Storage is flat on purpose. The previous version kept a List of (cell, slot) records PER
    /// TRIANGLE so removal could be O(1) - two managed objects per triangle. Measured in an offline
    /// harness running the real file on a 1.31M-triangle sphere: 165 MB retained against 46 MB for
    /// this layout, and a full garbage collection with it (and the jagged adjacency it replaced
    /// alongside) alive took 55 ms against 4.6 ms. Now a triangle is described by its packed cell
    /// range plus the slot it occupies in the FIRST cell of that range - and since 70-75% of
    /// triangles on a dense mesh sit in exactly one cell, that single slot is the whole record for
    /// most of them. The minority that straddle a boundary find their remaining registrations with a
    /// scan of the (short) cell list.
    ///
    /// The per-element halves of every update - which cell each moved vertex is in now, which cell
    /// range each affected triangle covers now - are pure functions of the positions, so they are
    /// split across cores (see ParallelPass); only the re-bucketing itself, which mutates shared
    /// cell lists, stays on one thread.
    internal class TriangleSpatialGrid
    {
        private readonly float _cellSize;
        // 1/_cellSize, kept alongside it because the per-point cell lookup is the hottest arithmetic
        // in this class and a float divide is several times the cost of a multiply.
        private readonly float _invCellSize;
        private readonly Bounds _bounds;
        // _bounds.min, cached: Bounds.min is a computed property (centre minus extents).
        private readonly Vector3 _boundsMin;
        private readonly Vector3Int _dims;
        private readonly List<int>[] _cellContents;
        // The inclusive cell range each triangle is registered over, packed so an unchanged range is
        // one integer compare - see UpdateTriangles.
        private long[] _triangleCellRange;
        // Where each triangle sits inside the list of the FIRST cell of its range (min x, y, z).
        // See the class remarks and RemoveRegistration.
        private int[] _firstCellSlot;
        // Which cell each VERTEX currently sits in, packed. A triangle's range is the bounding box
        // of its vertices' cells, so it cannot change unless one of those vertices changed cell -
        // the cheap early test UpdateFromMovedVertices runs before looking at any triangle.
        private int[] _vertexCell;

        // Per-triangle "already visited in operation N" marks, shared by the moved-triangle dedup and
        // the raycast's candidate dedup. Both run to completion on the main thread before anything
        // else touches the grid, so one array and one counter serve both.
        private int[] _triangleStamp;
        private int _stampGeneration;

        private readonly List<int> _movedTriangleScratch = new List<int>();
        // Results of the parallel halves of an update, indexed by position in the list being processed.
        private int[] _movedCellScratch = Array.Empty<int>();
        private long[] _rangeScratch = Array.Empty<long>();
        // Inputs of the pass in flight, held in fields so ParallelPass can split it without a closure
        // allocation per call. Cleared as soon as the pass returns.
        private Vector3[] _passVertices;
        private int[] _passTriangles;
        private List<int> _passItems;
        private Action<int, int> _vertexCellPass;
        private Action<int, int> _triangleRangePass;

        // Fixed at construction. Exposed so SculptableMesh can detect when a stroke has pushed
        // geometry outside this box and fall back to a full rebuild (a stale box silently breaks
        // raycasts against whatever moved past it).
        public Bounds Bounds => _bounds;

        public TriangleSpatialGrid(Vector3[] vertices, int[] triangles, Bounds bounds, float cellSize)
            : this(vertices, vertices.Length, triangles, triangles.Length, bounds, cellSize) { }

        /// vertexCount/cornerCount bound what is bucketed - both arrays can carry spare capacity
        /// (see SculptableMesh.Vertices), and the spare triangles are degenerate ones on vertex 0
        /// that would be registered and ray-tested forever.
        public TriangleSpatialGrid(Vector3[] vertices, int vertexCount, int[] triangles, int cornerCount,
                                   Bounds bounds, float cellSize)
        {
            _bounds = bounds;
            _boundsMin = bounds.min;
            _cellSize = Mathf.Max(cellSize, 0.0001f);
            _invCellSize = 1f / _cellSize;

            Vector3 size = bounds.size;
            _dims = new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(size.x / _cellSize), 1, 256),
                Mathf.Clamp(Mathf.CeilToInt(size.y / _cellSize), 1, 256),
                Mathf.Clamp(Mathf.CeilToInt(size.z / _cellSize), 1, 256));

            int triCount = Mathf.Clamp(cornerCount, 0, triangles.Length) / 3;
            int vertCount = Mathf.Clamp(vertexCount, 0, vertices.Length);
            _triangleCount = triCount;
            _vertexCellCount = vertCount;
            _cellContents = new List<int>[_dims.x * _dims.y * _dims.z];
            _triangleCellRange = new long[triCount];
            _firstCellSlot = new int[triCount];
            _triangleStamp = new int[triCount];
            _vertexCell = new int[vertCount];

            // Ranges and vertex cells are computed across cores; the registration that follows writes
            // shared cell lists, so it runs on this thread in triangle order.
            _passVertices = vertices;
            _passTriangles = triangles;
            ParallelPass.ForRange(triCount, BuildRangePass);
            ParallelPass.ForRange(vertCount, BuildVertexCellPass);
            _passVertices = null;
            _passTriangles = null;

            for (int ti = 0; ti < triCount; ti++) InsertTriangle(ti, _triangleCellRange[ti]);
        }

        private void BuildRangePass(int start, int end)
        {
            Vector3[] vertices = _passVertices;
            int[] triangles = _passTriangles;
            long[] ranges = _triangleCellRange;
            for (int ti = start; ti < end; ti++) ranges[ti] = RangeOf(ti, vertices, triangles);
        }

        private void BuildVertexCellPass(int start, int end)
        {
            Vector3[] vertices = _passVertices;
            int[] cells = _vertexCell;
            for (int i = start; i < end; i++)
            {
                Vector3 p = vertices[i];
                cells[i] = PackedCellOf(p.x, p.y, p.z);
            }
        }

        // Cell lookups are the hottest arithmetic in this class - one per moved vertex and two per
        // affected triangle - so they avoid Mathf.Floor (a round trip through Math.Floor and double)
        // and Vector3.Min/Max (struct copies through calls the editor's JIT does not inline). Results
        // are identical to those for every finite coordinate.
        private static int FloorToInt(float v)
        {
            int i = (int)v;
            return v < i ? i - 1 : i;
        }

        // Every cell coordinate fits in 8 bits - _dims is clamped to 256 per axis at construction.
        private int PackedCellOf(float x, float y, float z)
        {
            int cx = ClampAxis(FloorToInt((x - _boundsMin.x) * _invCellSize), _dims.x);
            int cy = ClampAxis(FloorToInt((y - _boundsMin.y) * _invCellSize), _dims.y);
            int cz = ClampAxis(FloorToInt((z - _boundsMin.z) * _invCellSize), _dims.z);
            return (cx << 16) | (cy << 8) | cz;
        }

        /// The packed inclusive cell range of a triangle's bounding box: min cell in the high half,
        /// max cell in the low half, so an unchanged range is one compare.
        private long RangeOf(int ti, Vector3[] vertices, int[] triangles)
        {
            int b = ti * 3;
            Vector3 p0 = vertices[triangles[b]];
            Vector3 p1 = vertices[triangles[b + 1]];
            Vector3 p2 = vertices[triangles[b + 2]];

            float minX = p0.x, maxX = p0.x, minY = p0.y, maxY = p0.y, minZ = p0.z, maxZ = p0.z;
            if (p1.x < minX) minX = p1.x; else if (p1.x > maxX) maxX = p1.x;
            if (p1.y < minY) minY = p1.y; else if (p1.y > maxY) maxY = p1.y;
            if (p1.z < minZ) minZ = p1.z; else if (p1.z > maxZ) maxZ = p1.z;
            if (p2.x < minX) minX = p2.x; else if (p2.x > maxX) maxX = p2.x;
            if (p2.y < minY) minY = p2.y; else if (p2.y > maxY) maxY = p2.y;
            if (p2.z < minZ) minZ = p2.z; else if (p2.z > maxZ) maxZ = p2.z;

            return ((long)PackedCellOf(minX, minY, minZ) << 32) | (uint)PackedCellOf(maxX, maxY, maxZ);
        }

        private int FlatIndexOfPacked(int packed) => FlatIndex(packed >> 16, (packed >> 8) & 0xFF, packed & 0xFF);

        private List<int> CellFor(int flat)
        {
            List<int> cell = _cellContents[flat];
            if (cell == null) { cell = new List<int>(8); _cellContents[flat] = cell; }
            return cell;
        }

        private void InsertTriangle(int ti, long range)
        {
            _triangleCellRange[ti] = range;
            int minPacked = (int)(range >> 32), maxPacked = (int)(range & 0xFFFFFFFF);

            if (minPacked == maxPacked)
            {
                // One cell: the common case, without the loop.
                List<int> only = CellFor(FlatIndexOfPacked(minPacked));
                _firstCellSlot[ti] = only.Count;
                only.Add(ti);
                return;
            }

            int x0 = minPacked >> 16, y0 = (minPacked >> 8) & 0xFF, z0 = minPacked & 0xFF;
            int x1 = maxPacked >> 16, y1 = (maxPacked >> 8) & 0xFF, z1 = maxPacked & 0xFF;
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                List<int> cell = CellFor(FlatIndex(x, y, z));
                if (x == x0 && y == y0 && z == z0) _firstCellSlot[ti] = cell.Count;
                cell.Add(ti);
            }
        }

        /// Unregisters a triangle from every cell of its CURRENT (stored) range.
        private void RemoveTriangle(int ti)
        {
            long range = _triangleCellRange[ti];
            int minPacked = (int)(range >> 32), maxPacked = (int)(range & 0xFFFFFFFF);
            int firstFlat = FlatIndexOfPacked(minPacked);

            if (minPacked == maxPacked)
            {
                RemoveRegistration(ti, firstFlat, _firstCellSlot[ti]);
                return;
            }

            int x0 = minPacked >> 16, y0 = (minPacked >> 8) & 0xFF, z0 = minPacked & 0xFF;
            int x1 = maxPacked >> 16, y1 = (maxPacked >> 8) & 0xFF, z1 = maxPacked & 0xFF;
            for (int z = z0; z <= z1; z++)
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int flat = FlatIndex(x, y, z);
                RemoveRegistration(ti, flat, flat == firstFlat ? _firstCellSlot[ti] : -1);
            }
        }

        /// Removes one registration by moving the cell's last entry into its slot and popping the
        /// tail - cell order carries no meaning (Raycast tests every candidate it collects). That move
        /// is the one thing to correct afterwards: if the entry that moved was sitting in ITS first
        /// cell, its recorded slot just changed. `slotHint` is the known slot, or -1 to scan for it.
        private void RemoveRegistration(int ti, int flat, int slotHint)
        {
            List<int> cell = _cellContents[flat];
            if (cell == null) return;

            int slot = slotHint;
            if (slot < 0 || slot >= cell.Count || cell[slot] != ti)
            {
                slot = cell.IndexOf(ti);
                if (slot < 0) return;
            }

            int lastSlot = cell.Count - 1;
            int movedTi = cell[lastSlot];
            cell[slot] = movedTi;
            cell.RemoveAt(lastSlot);

            if (movedTi != ti && FlatIndexOfPacked((int)(_triangleCellRange[movedTi] >> 32)) == flat)
                _firstCellSlot[movedTi] = slot;
        }

        // Registered counts, as against the per-element arrays' lengths, which run ahead of them so
        // an append does not reallocate mesh-sized arrays - see AppendTriangles.
        private int _triangleCount;
        private int _vertexCellCount;

        /// How many triangles this index currently holds registrations for. AppendTriangles adds
        /// past this; anything at or beyond it has not been bucketed yet.
        public int TriangleCount => _triangleCount;

        /// Registers `count` triangles starting at triangle index `from` that did not exist when
        /// this index was built, and buckets the vertices they introduced.
        ///
        /// O(count), the whole point: triangles added inside one brush footprint would otherwise
        /// cost a pass over every triangle in the mesh to bucket. The
        /// grid's BOUNDS and cell size are fixed at construction either way - new geometry outside
        /// the box is handled the way moved geometry already is, by SculptableMesh noticing the
        /// mesh no longer fits and rebuilding (see MeshBoundsFitInsideTriangleGrid).
        public void AppendTriangles(int from, int count, int vertexCount, Vector3[] vertices, int[] triangles)
        {
            if (count <= 0) return;

            int needed = from + count;
            if (_triangleCellRange.Length < needed)
            {
                // Half again rather than to fit - see VertexSpatialGrid.AppendVertices for why
                // resizing to the exact count turns a footprint-sized operation back into a
                // mesh-sized one.
                int grown = Math.Max(needed, _triangleCellRange.Length + _triangleCellRange.Length / 2);
                Array.Resize(ref _triangleCellRange, grown);
                Array.Resize(ref _firstCellSlot, grown);
                Array.Resize(ref _triangleStamp, grown);
            }
            // Vertices the new triangles reference may themselves be new. Their cells feed
            // UpdateFromMovedVertices' "did this vertex change cell" test, so a missing entry would
            // read as cell 0 and make the first move of a new vertex look like a jump across the
            // grid.
            if (_vertexCellCount < vertexCount)
            {
                if (_vertexCell.Length < vertexCount)
                    Array.Resize(ref _vertexCell, Math.Max(vertexCount, _vertexCell.Length + _vertexCell.Length / 2));
                // From the registered COUNT, not from the array's old length: the array runs ahead
                // of the count, and starting from its length would leave the vertices in between
                // with no cell recorded at all.
                for (int i = _vertexCellCount; i < vertexCount; i++)
                {
                    Vector3 p = vertices[i];
                    _vertexCell[i] = PackedCellOf(p.x, p.y, p.z);
                }
                _vertexCellCount = vertexCount;
            }

            for (int ti = from; ti < needed; ti++) InsertTriangle(ti, RangeOf(ti, vertices, triangles));
            _triangleCount = needed;
        }

        /// Re-buckets exactly the given triangles to wherever their (already-moved) vertices put them
        /// now. A triangle whose packed range is unchanged is already registered in exactly the right
        /// cells and is skipped with one compare.
        public void UpdateTriangles(List<int> dirtyTriangles, Vector3[] vertices, int[] triangles)
        {
            int count = dirtyTriangles.Count;
            if (count == 0) return;
            if (_rangeScratch.Length < count) _rangeScratch = new long[Math.Max(count, _rangeScratch.Length * 2)];

            _passItems = dirtyTriangles;
            _passVertices = vertices;
            _passTriangles = triangles;
            ParallelPass.ForRange(count, _triangleRangePass ?? (_triangleRangePass = TriangleRangePass));
            _passItems = null;
            _passVertices = null;
            _passTriangles = null;

            long[] ranges = _rangeScratch;
            long[] current = _triangleCellRange;
            for (int k = 0; k < count; k++)
            {
                int ti = dirtyTriangles[k];
                long range = ranges[k];
                if (current[ti] == range) continue;

                RemoveTriangle(ti);
                InsertTriangle(ti, range);
            }
        }

        private void TriangleRangePass(int start, int end)
        {
            List<int> items = _passItems;
            Vector3[] vertices = _passVertices;
            int[] triangles = _passTriangles;
            long[] ranges = _rangeScratch;
            for (int k = start; k < end; k++) ranges[k] = RangeOf(items[k], vertices, triangles);
        }

        /// The same job as UpdateTriangles, driven from the vertices that moved: a triangle's range
        /// can only change if one of its vertices changed cell, which costs one position load and one
        /// integer compare per moved vertex to rule out - and in the common case where no vertex
        /// crossed a boundary, no triangle is looked at at all. `vertexTriangleStart`,
        /// `vertexTriangleCount` and `vertexTriangles` are MeshAdjacency's incident-triangle arrays.
        public void UpdateFromMovedVertices(List<int> movedVertices, int[] vertexTriangleStart, int[] vertexTriangleCount,
            int[] vertexTriangles, Vector3[] vertices, int[] triangles)
        {
            int movedCount = movedVertices.Count;
            if (movedCount == 0) return;
            if (_movedCellScratch.Length < movedCount)
                _movedCellScratch = new int[Math.Max(movedCount, _movedCellScratch.Length * 2)];

            _passItems = movedVertices;
            _passVertices = vertices;
            ParallelPass.ForRange(movedCount, _vertexCellPass ?? (_vertexCellPass = VertexCellPass));
            _passItems = null;
            _passVertices = null;

            _movedTriangleScratch.Clear();
            int generation = NextStampGeneration();
            int[] stamp = _triangleStamp;
            int[] vertexCell = _vertexCell;
            int[] nowCells = _movedCellScratch;

            for (int k = 0; k < movedCount; k++)
            {
                int vi = movedVertices[k];
                int now = nowCells[k];
                if (now == vertexCell[vi]) continue;
                vertexCell[vi] = now;

                for (int i = vertexTriangleStart[vi], end = i + vertexTriangleCount[vi]; i < end; i++)
                {
                    int ti = vertexTriangles[i];
                    if (stamp[ti] == generation) continue;
                    stamp[ti] = generation;
                    _movedTriangleScratch.Add(ti);
                }
            }

            if (_movedTriangleScratch.Count > 0)
                UpdateTriangles(_movedTriangleScratch, vertices, triangles);
        }

        private void VertexCellPass(int start, int end)
        {
            List<int> items = _passItems;
            Vector3[] vertices = _passVertices;
            int[] cells = _movedCellScratch;
            for (int k = start; k < end; k++)
            {
                Vector3 p = vertices[items[k]];
                cells[k] = PackedCellOf(p.x, p.y, p.z);
            }
        }

        private int NextStampGeneration()
        {
            // Wraps after ~2 billion operations; clearing then is what keeps a stale mark from ever
            // reading as current.
            if (_stampGeneration == int.MaxValue)
            {
                Array.Clear(_triangleStamp, 0, _triangleStamp.Length);
                _stampGeneration = 0;
            }
            return ++_stampGeneration;
        }

        private static int ClampAxis(int v, int dim) => v < 0 ? 0 : (v >= dim ? dim - 1 : v);

        private int FlatIndex(int x, int y, int z) => x + _dims.x * (y + _dims.y * z);

        /// Closest ray-triangle intersection along the ray, within maxDistance. hitNormal is the
        /// intersected triangle's flat face normal - matches what Physics.Raycast against a
        /// MeshCollider returned before this replaced it. `hiddenTriangles` (optional, one bool per
        /// triangle - see SculptableMesh's box/lasso hide) removes those triangles from consideration
        /// entirely, so a ray passes straight through hidden geometry to whatever is behind it.
        /// Filtering here rather than at the caller is what makes hidden geometry consistently
        /// un-hoverable, un-sculptable and un-clickable: every hit test in the app goes through here.
        public bool Raycast(Vector3 origin, Vector3 dir, float maxDistance, Vector3[] vertices, int[] triangles,
            out float hitT, out Vector3 hitNormal, bool[] hiddenTriangles = null)
        {
            hitNormal = default;
            if (!RaycastTriangle(origin, dir, maxDistance, vertices, triangles, hiddenTriangles,
                    out hitT, out int hitTriangle, out _, out _))
                return false;

            int b = hitTriangle * 3;
            Vector3 a = vertices[triangles[b]];
            // Not Vector3.normalized: its fixed 1e-5 length cutoff zeroed the normal of every
            // triangle under 5e-6 square units - most of a dense sculpt's - and a brush has no
            // direction to push along without one (see VectorMath). The fallback, facing back up
            // the ray, only matters for a sliver the ray still managed to hit.
            hitNormal = VectorMath.NormalizeOr(
                Vector3.Cross(vertices[triangles[b + 1]] - a, vertices[triangles[b + 2]] - a), -dir);
            return true;
        }

        /// Raycast, reporting WHICH triangle was hit and where on it (barycentric u, v - the hit point
        /// is a + u(b - a) + v(c - a)), for a caller that wants more than the flat face normal.
        ///
        /// Walks the grid with Amanatides &amp; Woo's 3D-DDA - every cell the ray passes through,
        /// exactly once, in order along the ray - and stops as soon as the best hit so far is nearer
        /// than the far side of the current cell. That early stop is exact, not a heuristic: a
        /// triangle is registered in every cell its bounding box overlaps, and the point where a ray
        /// hits it lies inside that box, so a hit at distance t is always found in the cell the ray
        /// occupies at t. Nothing in a cell further along can therefore be nearer.
        ///
        /// It replaces fixed quarter-cell marching, which had two costs. It could step over the
        /// corner of a cell a ray only grazes, so near a silhouette it occasionally returned the
        /// surface BEHIND the one under the cursor (measured: 6 of 3000 test rays wrong or missed on a
        /// 1.31M-triangle sphere, 0 now). And it gathered every triangle along the WHOLE clipped ray -
        /// through the model and out the back - into a HashSet before testing any of them.
        public bool RaycastTriangle(Vector3 origin, Vector3 dir, float maxDistance, Vector3[] vertices, int[] triangles,
            bool[] hiddenTriangles, out float hitT, out int hitTriangle, out float hitU, out float hitV)
        {
            hitT = 0f;
            hitTriangle = -1;
            hitU = 0f;
            hitV = 0f;

            if (!RayBoundsClip(origin, dir, _bounds, maxDistance, out float tEnter, out float tExit))
                return false;

            Vector3 entry = origin + dir * tEnter;
            int x = ClampAxis(FloorToInt((entry.x - _boundsMin.x) * _invCellSize), _dims.x);
            int y = ClampAxis(FloorToInt((entry.y - _boundsMin.y) * _invCellSize), _dims.y);
            int z = ClampAxis(FloorToInt((entry.z - _boundsMin.z) * _invCellSize), _dims.z);

            AxisSetup(origin.x, dir.x, _boundsMin.x, x, out int stepX, out float tDeltaX, out float tMaxX);
            AxisSetup(origin.y, dir.y, _boundsMin.y, y, out int stepY, out float tDeltaY, out float tMaxY);
            AxisSetup(origin.z, dir.z, _boundsMin.z, z, out int stepZ, out float tDeltaZ, out float tMaxZ);

            int generation = NextStampGeneration();
            int[] stamp = _triangleStamp;
            bool anyHidden = hiddenTriangles != null;
            float bestT = maxDistance;
            int bestTriangle = -1;
            float bestU = 0f, bestV = 0f;
            // Hits this close to a cell boundary are re-checked against one more cell, so float
            // rounding in the boundary distances can never end the walk a cell early.
            float boundarySlack = _cellSize * 1e-4f;
            // A ray visits at most one cell per boundary crossed; the +3 covers the start cell. Only
            // ever reached if the arithmetic above produced something non-finite.
            int maxCells = _dims.x + _dims.y + _dims.z + 3;

            for (int visited = 0; visited < maxCells; visited++)
            {
                List<int> cell = _cellContents[FlatIndex(x, y, z)];
                if (cell != null)
                {
                    for (int i = 0; i < cell.Count; i++)
                    {
                        int ti = cell[i];
                        if (stamp[ti] == generation) continue;
                        stamp[ti] = generation;
                        if (anyHidden && ti < hiddenTriangles.Length && hiddenTriangles[ti]) continue;

                        int b = ti * 3;
                        if (RayTriangleIntersect(origin, dir, vertices[triangles[b]], vertices[triangles[b + 1]],
                                vertices[triangles[b + 2]], out float t, out float u, out float v) && t < bestT)
                        {
                            bestT = t;
                            bestTriangle = ti;
                            bestU = u;
                            bestV = v;
                        }
                    }
                }

                float cellExit = tMaxX < tMaxY ? (tMaxX < tMaxZ ? tMaxX : tMaxZ) : (tMaxY < tMaxZ ? tMaxY : tMaxZ);
                if (bestTriangle >= 0 && bestT <= cellExit - boundarySlack) break;
                if (cellExit >= tExit) break;

                if (tMaxX < tMaxY)
                {
                    if (tMaxX < tMaxZ) { x += stepX; if ((uint)x >= (uint)_dims.x) break; tMaxX += tDeltaX; }
                    else { z += stepZ; if ((uint)z >= (uint)_dims.z) break; tMaxZ += tDeltaZ; }
                }
                else
                {
                    if (tMaxY < tMaxZ) { y += stepY; if ((uint)y >= (uint)_dims.y) break; tMaxY += tDeltaY; }
                    else { z += stepZ; if ((uint)z >= (uint)_dims.z) break; tMaxZ += tDeltaZ; }
                }
            }

            if (bestTriangle < 0) return false;
            hitT = bestT;
            hitTriangle = bestTriangle;
            hitU = bestU;
            hitV = bestV;
            return true;
        }

        /// One axis of the DDA: which way the cell index steps, the ray distance between successive
        /// boundaries on this axis, and the ray distance at which the first one is crossed. An axis
        /// the ray is (numerically) parallel to never steps.
        private void AxisSetup(float origin, float dir, float gridMin, int cell, out int step, out float tDelta, out float tMax)
        {
            if (dir > 1e-12f)
            {
                step = 1;
                tDelta = _cellSize / dir;
                tMax = (gridMin + (cell + 1) * _cellSize - origin) / dir;
            }
            else if (dir < -1e-12f)
            {
                step = -1;
                tDelta = -_cellSize / dir;
                tMax = (gridMin + cell * _cellSize - origin) / dir;
            }
            else
            {
                step = 0;
                tDelta = float.PositiveInfinity;
                tMax = float.PositiveInfinity;
            }
        }

        // Standard slab method. Guards near-zero direction components so an axis-aligned ray
        // (a real possibility for a straight-on camera angle) doesn't produce NaN from 0/0.
        private static bool RayBoundsClip(Vector3 origin, Vector3 dir, Bounds bounds, float maxDistance,
            out float tEnter, out float tExit)
        {
            Vector3 min = bounds.min, max = bounds.max;
            float t0 = 0f, t1 = maxDistance;

            for (int axis = 0; axis < 3; axis++)
            {
                float o = origin[axis], d = dir[axis];
                if (Mathf.Abs(d) < 1e-9f) d = d >= 0f ? 1e-9f : -1e-9f;
                float invD = 1f / d;
                float tNear = (min[axis] - o) * invD;
                float tFar = (max[axis] - o) * invD;
                if (tNear > tFar) (tNear, tFar) = (tFar, tNear);
                t0 = Mathf.Max(t0, tNear);
                t1 = Mathf.Min(t1, tFar);
                if (t0 > t1) { tEnter = 0f; tExit = 0f; return false; }
            }

            tEnter = t0;
            tExit = t1;
            return true;
        }

        // Moller-Trumbore, double-sided.
        private static bool RayTriangleIntersect(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2,
            out float t, out float u, out float v)
        {
            const float Eps = 1e-7f;
            t = 0f;
            u = 0f;
            v = 0f;
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 pvec = Vector3.Cross(dir, edge2);
            float det = Vector3.Dot(edge1, pvec);
            if (det > -Eps && det < Eps) return false;

            float invDet = 1f / det;
            Vector3 tvec = origin - v0;
            u = Vector3.Dot(tvec, pvec) * invDet;
            if (u < 0f || u > 1f) return false;

            Vector3 qvec = Vector3.Cross(tvec, edge1);
            v = Vector3.Dot(dir, qvec) * invDet;
            if (v < 0f || u + v > 1f) return false;

            t = Vector3.Dot(edge2, qvec) * invDet;
            return t > Eps;
        }
    }
}
