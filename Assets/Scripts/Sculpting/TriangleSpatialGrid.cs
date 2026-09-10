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
    /// Mutable and incrementally updatable (UpdateTriangles), NOT rebuilt from scratch every
    /// frame. Two earlier attempts both rebuilt fully on every geometry change and both lost to
    /// the PhysX baseline they were meant to beat once actually measured: a
    /// Dictionary&lt;Vector3Int, List&lt;int&gt;&gt; layout (~40.6ms/call, worse than PhysX) and
    /// a flat CSR/counting-sort layout (~27.6ms/call, barely better) - both still O(triangle
    /// count) per rebuild, and a brush stroke only ever touches a tiny fraction of a mesh's
    /// triangles regardless of total mesh size, so an O(total triangles) step every single frame
    /// was always going to be the wrong shape for this problem (every OTHER per-frame brush cost
    /// in this codebase is already scoped to the brush footprint via VertexSpatialGrid/QueryNear
    /// - this grid was the one holdout still touching the whole mesh). UpdateTriangles takes the
    /// exact set of triangles whose vertices actually moved this frame (from
    /// SculptableMesh.ApplyVerticesLocal, driven by each brush's own already-computed dirty
    /// vertex set) and only re-buckets those - cost scales with brush footprint, not mesh size.
    internal class TriangleSpatialGrid
    {
        private readonly float _cellSize;
        // 1/_cellSize, kept alongside it because the per-triangle cell lookup is the hottest
        // arithmetic in this class and a float divide is several times the cost of a multiply.
        // UpdateTriangles does six of these per dirty triangle for a test that, measured on a
        // 270k-triangle sphere, said "unchanged" for 100% of them - so the divides were the
        // entire cost of the step.
        private readonly float _invCellSize;
        private readonly Bounds _bounds;
        // _bounds.min, cached: Bounds.min is a computed property (centre minus extents), and
        // ClampedCellOf reads it for every corner of every triangle it buckets.
        private readonly Vector3 _boundsMin;
        private readonly Vector3Int _dims;
        private readonly List<int>[] _cellContents;
        // Per triangle, where it's currently registered - needed so UpdateTriangles knows
        // exactly which cells to remove a moved triangle from before re-inserting it at its
        // new position, without a wider search. Stores the SLOT inside each cell's list
        // alongside the cell id so removal is O(1) - see CellSlot/RemoveTriangle.
        private readonly List<CellSlot>[] _triangleCells;
        // The cell RANGE each triangle currently spans, packed so it can be compared in one
        // integer test - see UpdateTriangles, which skips a triangle whose range is unchanged.
        private readonly long[] _triangleCellRange;
        // Which cell each VERTEX currently sits in, packed the same way. A triangle's cell range
        // is the bounding box of its three vertices' cells, so it cannot have changed unless at
        // least one of those vertices changed cell - which makes this the cheap early test that
        // UpdateFromMovedVertices runs before it looks at a single triangle. See that method for
        // why the per-triangle test alone was not enough.
        private readonly int[] _vertexCell;

        /// One registration of a triangle: which cell's list it sits in, and at which index
        /// within that list. The index is what makes RemoveTriangle O(1): the previous version
        /// stored only the cell id and removed via List.Remove(ti), a linear scan plus a
        /// memmove over the whole cell. That reads as cheap because cells are sized for ~8
        /// triangles each, but the sizing is derived from the grid's BOX volume while triangles
        /// only occupy its surface SHELL, so occupied cells really hold 100+ - measured at
        /// 16.6ms of a 23.4ms brush frame (71%) on a 157k-vertex sphere, the single dominant
        /// per-frame cost of every brush. Swap-with-last plus a slot fixup replaces both the
        /// scan and the memmove.
        private readonly struct CellSlot
        {
            public readonly int Cell;
            public readonly int Slot;
            public CellSlot(int cell, int slot) { Cell = cell; Slot = slot; }
        }

        private readonly HashSet<int> _visitedCellIdScratch = new HashSet<int>();
        private readonly HashSet<int> _candidateScratch = new HashSet<int>();

        // Fixed at construction, unlike VertexSpatialGrid (a Dictionary-backed grid with no
        // bounding-box limit at all). Exposed so SculptableMesh.ApplyVerticesLocal can detect
        // when a stroke has pushed geometry outside this box and fall back to a full rebuild -
        // see its remarks for why that matters (a stale box silently breaks raycasts against
        // whatever moved past it).
        public Bounds Bounds => _bounds;

        public TriangleSpatialGrid(Vector3[] vertices, int[] triangles, Bounds bounds, float cellSize)
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

            int triCount = triangles.Length / 3;
            int cellCount = _dims.x * _dims.y * _dims.z;
            _cellContents = new List<int>[cellCount];
            _triangleCells = new List<CellSlot>[triCount];
            _triangleCellRange = new long[triCount];
            _movedTriangleStamp = new int[triCount];

            for (int ti = 0; ti < triCount; ti++)
            {
                CellRangeOf(ti, vertices, triangles, out Vector3Int cmin, out Vector3Int cmax);
                InsertTriangle(ti, cmin, cmax);
            }

            _vertexCell = new int[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) _vertexCell[i] = PackCell(ClampedCellOf(vertices[i]));
        }

        private static int PackCell(Vector3Int c) => (c.x << 16) | (c.y << 8) | c.z;

        /// The inclusive cell range the triangle's bounding box covers. Split out from
        /// InsertTriangle so UpdateTriangles can compute it once and use it both to decide
        /// whether anything changed and, if so, to do the re-insert.
        private void CellRangeOf(int ti, Vector3[] vertices, int[] triangles, out Vector3Int cmin, out Vector3Int cmax)
        {
            Vector3 a = vertices[triangles[ti * 3]];
            Vector3 b = vertices[triangles[ti * 3 + 1]];
            Vector3 c = vertices[triangles[ti * 3 + 2]];

            cmin = ClampedCellOf(Vector3.Min(a, Vector3.Min(b, c)));
            cmax = ClampedCellOf(Vector3.Max(a, Vector3.Max(b, c)));
        }

        // Both cell coordinates fit in 8 bits each - _dims is clamped to 256 per axis at
        // construction - so a whole min/max range packs into one long and compares in one
        // instruction.
        private static long PackRange(Vector3Int cmin, Vector3Int cmax) =>
            ((long)((cmin.x << 16) | (cmin.y << 8) | cmin.z) << 32) |
            (uint)((cmax.x << 16) | (cmax.y << 8) | cmax.z);

        private void InsertTriangle(int ti, Vector3Int cmin, Vector3Int cmax)
        {
            _triangleCellRange[ti] = PackRange(cmin, cmax);

            List<CellSlot> membership = _triangleCells[ti];
            if (membership == null) { membership = new List<CellSlot>(4); _triangleCells[ti] = membership; }

            for (int z = cmin.z; z <= cmax.z; z++)
            for (int y = cmin.y; y <= cmax.y; y++)
            for (int x = cmin.x; x <= cmax.x; x++)
            {
                int flat = FlatIndex(x, y, z);
                List<int> cell = _cellContents[flat];
                if (cell == null) { cell = new List<int>(4); _cellContents[flat] = cell; }
                cell.Add(ti);
                membership.Add(new CellSlot(flat, cell.Count - 1));
            }
        }

        /// Unregisters a triangle from every cell it currently sits in, in O(cells it spans)
        /// regardless of how many triangles those cells hold. Each registration is removed by
        /// overwriting its slot with the cell's last entry and popping the tail - which moves
        /// that last triangle to a new slot, so its own membership record has to be corrected
        /// to match. That fixup scans only the MOVED triangle's membership list (the handful of
        /// cells one triangle spans, typically 1-4), never a cell's contents. Cell order is not
        /// meaningful here - Raycast collects candidates into a set and tests them all - so
        /// swapping the tail into the hole is free.
        private void RemoveTriangle(int ti)
        {
            List<CellSlot> membership = _triangleCells[ti];
            if (membership == null) return;

            for (int i = 0; i < membership.Count; i++)
            {
                CellSlot entry = membership[i];
                List<int> cell = _cellContents[entry.Cell];
                if (cell == null) continue;

                int lastSlot = cell.Count - 1;
                int movedTi = cell[lastSlot];
                cell[entry.Slot] = movedTi;
                cell.RemoveAt(lastSlot);

                // ti was itself the tail - nothing moved, so nothing to correct. (A triangle is
                // registered at most once per cell, so movedTi == ti implies exactly this.)
                if (movedTi == ti) continue;

                List<CellSlot> movedMembership = _triangleCells[movedTi];
                for (int k = 0; k < movedMembership.Count; k++)
                {
                    if (movedMembership[k].Cell != entry.Cell || movedMembership[k].Slot != lastSlot) continue;
                    movedMembership[k] = new CellSlot(entry.Cell, entry.Slot);
                    break;
                }
            }

            membership.Clear();
        }

        /// Re-buckets exactly the given triangles from their current cell registrations to
        /// wherever their (already-moved) vertices put them now - O(dirty triangle count), not
        /// O(total triangle count). Callers pass the exact triangles incident to whichever
        /// vertices moved this frame (see SculptableMesh.ApplyVerticesLocal).
        ///
        /// A triangle whose cell range didn't actually change is already registered in exactly
        /// the right cells, so it's skipped outright - the same "did it even leave its cell"
        /// test VertexSpatialGrid.UpdateVertices makes. That's the overwhelmingly common case:
        /// cell size is chosen so a cell holds several triangles, while one frame of a stroke
        /// moves a vertex by a small fraction of a cell, so a dirty triangle usually sits
        /// exactly where it already was and only the ones near a cell boundary really move.
        public void UpdateTriangles(List<int> dirtyTriangles, Vector3[] vertices, int[] triangles)
        {
            for (int k = 0; k < dirtyTriangles.Count; k++)
            {
                int ti = dirtyTriangles[k];
                CellRangeOf(ti, vertices, triangles, out Vector3Int cmin, out Vector3Int cmax);
                if (_triangleCells[ti] != null && _triangleCellRange[ti] == PackRange(cmin, cmax)) continue;

                RemoveTriangle(ti);
                InsertTriangle(ti, cmin, cmax);
            }
        }

        /// The same job as UpdateTriangles, driven from the vertices that moved rather than from
        /// their incident triangles - which is both less work and a cheaper kind of work.
        ///
        /// UpdateTriangles has to visit every triangle touching a moved vertex (roughly twice as
        /// many triangles as there are vertices) and, for each, load three indices and three
        /// positions and dereference its membership list, just to conclude that nothing changed.
        /// It nearly always does conclude that: cell size here targets ~8 triangles per cell,
        /// while one frame of a stroke moves a vertex a small fraction of a cell, so on a
        /// 270k-triangle sphere this measured 0% of dirty triangles actually re-bucketed and
        /// 3.3ms per dab spent proving it - half the entire cost of applying a brush frame. The
        /// scattered loads are what make it expensive rather than the arithmetic.
        ///
        /// A triangle's cell range is the bounding box of its three vertices' cells, so it can
        /// only change if one of those vertices changed cell. Testing that first costs one
        /// position load and one integer compare per moved VERTEX, and in the common case where
        /// no vertex crossed a boundary no triangle is looked at at all.
        public void UpdateFromMovedVertices(List<int> movedVertices, int[][] vertexTriangles,
            Vector3[] vertices, int[] triangles)
        {
            _movedTriangleScratch.Clear();
            _movedTriangleGeneration++;

            for (int k = 0; k < movedVertices.Count; k++)
            {
                int vi = movedVertices[k];
                int now = PackCell(ClampedCellOf(vertices[vi]));
                if (now == _vertexCell[vi]) continue;
                _vertexCell[vi] = now;

                int[] incident = vertexTriangles[vi];
                for (int i = 0; i < incident.Length; i++)
                {
                    int ti = incident[i];
                    if (_movedTriangleStamp[ti] == _movedTriangleGeneration) continue;
                    _movedTriangleStamp[ti] = _movedTriangleGeneration;
                    _movedTriangleScratch.Add(ti);
                }
            }

            if (_movedTriangleScratch.Count > 0)
                UpdateTriangles(_movedTriangleScratch, vertices, triangles);
        }

        // Triangles whose cell range might have changed this call, deduped by generation stamp -
        // same scheme SculptableMesh uses for its own dirty sets, and for the same reason.
        private readonly List<int> _movedTriangleScratch = new List<int>();
        private readonly int[] _movedTriangleStamp;
        private int _movedTriangleGeneration;

        private Vector3Int ClampedCellOf(Vector3 p)
        {
            return new Vector3Int(
                ClampAxis((int)Mathf.Floor((p.x - _boundsMin.x) * _invCellSize), _dims.x),
                ClampAxis((int)Mathf.Floor((p.y - _boundsMin.y) * _invCellSize), _dims.y),
                ClampAxis((int)Mathf.Floor((p.z - _boundsMin.z) * _invCellSize), _dims.z));
        }

        private static int ClampAxis(int v, int dim) => v < 0 ? 0 : (v >= dim ? dim - 1 : v);

        private int FlatIndex(int x, int y, int z) => x + _dims.x * (y + _dims.y * z);

        /// Closest ray-triangle intersection along the ray, within maxDistance. First clips the
        /// ray against the mesh's local bounds (cheap O(1) rejection for the common case of the
        /// cursor not being over the model at all), then walks only the cells the clipped
        /// segment passes through. hitNormal is the intersected triangle's flat face normal -
        /// matches what Physics.Raycast against a MeshCollider returned before this replaced it
        /// (Clay already area-averages its own plane normal over the brush footprint regardless,
        /// see SculptController.ApplyClayBrushLocal, so this doesn't affect Clay's flattening).
        /// `hiddenTriangles` (optional, one bool per triangle - see SculptableMesh's box/lasso
        /// hide) removes those triangles from consideration entirely, so a ray passes straight
        /// through hidden geometry to whatever is behind it. Filtering here rather than at the
        /// caller is what makes hidden geometry consistently un-hoverable, un-sculptable and
        /// un-clickable: every hit test in the app goes through this one method.
        public bool Raycast(Vector3 origin, Vector3 dir, float maxDistance, Vector3[] vertices, int[] triangles,
            out float hitT, out Vector3 hitNormal, bool[] hiddenTriangles = null)
        {
            hitT = 0f;
            hitNormal = default;

            if (!RayBoundsClip(origin, dir, _bounds, maxDistance, out float tEnter, out float tExit))
                return false;

            _visitedCellIdScratch.Clear();
            _candidateScratch.Clear();

            // Quarter-cell steps rather than half: this is fixed-step marching, not true
            // voxel/DDA traversal, so a ray that clips a cell only near a corner (common at
            // grazing/oblique angles) can pass through without any sample point landing inside
            // it. A smaller step doesn't mathematically guarantee catching every such sliver,
            // but it substantially narrows the miss window for the same reason - cheap to
            // afford since candidate cells are still bounded by the ray's local footprint, not
            // the whole mesh.
            float step = _cellSize * 0.25f;
            int steps = Mathf.Max(1, Mathf.CeilToInt((tExit - tEnter) / step)) + 1;
            int lastFlat = -1;

            for (int s = 0; s <= steps; s++)
            {
                float dist = Mathf.Min(tEnter + s * step, tExit);
                Vector3 p = origin + dir * dist;
                Vector3Int cell = ClampedCellOf(p);
                int flat = FlatIndex(cell.x, cell.y, cell.z);
                if (flat != lastFlat)
                {
                    lastFlat = flat;
                    if (_visitedCellIdScratch.Add(flat))
                    {
                        List<int> cell2 = _cellContents[flat];
                        if (cell2 != null)
                            for (int i = 0; i < cell2.Count; i++) _candidateScratch.Add(cell2[i]);
                    }
                }
                if (dist >= tExit) break;
            }

            bool found = false;
            float bestT = maxDistance;
            Vector3 bestNormal = default;

            foreach (int ti in _candidateScratch)
            {
                if (hiddenTriangles != null && ti < hiddenTriangles.Length && hiddenTriangles[ti]) continue;
                Vector3 a = vertices[triangles[ti * 3]];
                Vector3 b = vertices[triangles[ti * 3 + 1]];
                Vector3 c = vertices[triangles[ti * 3 + 2]];

                if (RayTriangleIntersect(origin, dir, a, b, c, out float t) && t < bestT)
                {
                    bestT = t;
                    bestNormal = Vector3.Cross(b - a, c - a).normalized;
                    found = true;
                }
            }

            if (found)
            {
                hitT = bestT;
                hitNormal = bestNormal;
            }
            return found;
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

        private static bool RayTriangleIntersect(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            const float Eps = 1e-7f;
            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 pvec = Vector3.Cross(dir, edge2);
            float det = Vector3.Dot(edge1, pvec);
            if (det > -Eps && det < Eps) { t = 0f; return false; }

            float invDet = 1f / det;
            Vector3 tvec = origin - v0;
            float u = Vector3.Dot(tvec, pvec) * invDet;
            if (u < 0f || u > 1f) { t = 0f; return false; }

            Vector3 qvec = Vector3.Cross(tvec, edge1);
            float v = Vector3.Dot(dir, qvec) * invDet;
            if (v < 0f || u + v > 1f) { t = 0f; return false; }

            t = Vector3.Dot(edge2, qvec) * invDet;
            return t > Eps;
        }
    }
}
