using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Owns a runtime-duplicated mesh so sculpting never touches the shared mesh asset.
    /// Brush raycasts hit-test directly against the live CPU vertex/triangle data via
    /// TriangleSpatialGrid rather than through a MeshCollider - see RaycastMesh/_triangleGrid.
    [RequireComponent(typeof(MeshFilter))]
    public class SculptableMesh : MonoBehaviour
    {
        // The MeshCollider is no longer on the brush hit-testing hot path (see RaycastMesh) -
        // kept optionally for any other system that might want a physics collider on the
        // sculpt, refreshed only on topology changes (Awake/Remesh/RestoreSnapshot), NOT every
        // ApplyVertices() call. Re-cooking it every frame during a held stroke used to be the
        // dominant per-frame cost by a wide margin (measured ~35ms/call at ~144k triangles,
        // ~95% of a brush-application frame) - see [[project_sculpting_application]] memory for
        // the profiling that found this.
        [SerializeField] private bool useMeshCollider = true;

        private MeshFilter _meshFilter;
        private MeshCollider _meshCollider;
        private Mesh _mesh;
        private Vector3[] _originalVertices;
        private Vector3[] _workingVertices;
        // Cached copy of _mesh.triangles - like _workingVertices/_workingNormals, avoids
        // re-reading (and thus re-copying) the whole index buffer from the Mesh on every
        // raycast. Only refreshed when topology actually changes (Awake/Remesh/
        // RestoreSnapshot), never by ordinary sculpting.
        private int[] _workingTriangles;
        // Accelerates RaycastMesh so brush hover/hit-testing doesn't scan every triangle -
        // see TriangleSpatialGrid. Rebuilt every time geometry changes (ApplyVertices and the
        // topology-change points below) since a raycast must always test the exact current
        // surface, not a stale approximation.
        private TriangleSpatialGrid _triangleGrid;
        // Cached copy of _mesh.normals, refreshed once alongside every RecalculateNormals()
        // call (ApplyVertices/Remesh/RestoreSnapshot) rather than re-read from the mesh on
        // demand - Mesh.normals copies the whole array on every access, which would be a real
        // per-frame cost for brushes that sample normals (Clay's area-plane average) at
        // multi-million-vertex mesh sizes. See VertexSpatialGrid/_workingVertices for the same
        // reasoning applied to positions.
        private Vector3[] _workingNormals;
        // Direct-edge neighbors per vertex, derived from the current mesh's triangles, used
        // by the Smooth brush to relax vertices toward their local average. Rebuilt whenever
        // topology changes (initial load, Remesh) - never touched by ordinary sculpting since
        // that only moves vertices, it doesn't change which ones are connected.
        private int[][] _adjacency;
        // Per-vertex incident-triangle list, built alongside _adjacency (same triangle scan).
        // Used by ApplyVerticesLocal to translate "these vertices moved" into "these triangles
        // need re-bucketing in _triangleGrid" without touching the rest of the mesh.
        private int[][] _vertexTriangles;

        // Per-vertex concavity/convexity, recomputed after every stroke and written into the
        // mesh's vertex colors (.r) for SculptPBR's cavity coloring - see RecomputeCavity.
        private Color[] _cavityColors;
        private const float CavitySensitivity = 25f;

        // Per-vertex mask: 0 = fully sculptable (default), 1 = fully protected. Every brush
        // loop multiplies its falloff weight by (1 - Mask[i]), so a masked area simply doesn't
        // move under any brush. Reset to all-zero whenever topology changes (Awake/Remesh/
        // RestoreSnapshot) - a mask painted before a Remesh has no well-defined mapping onto
        // the remeshed vertex set, so starting fresh is the honest behavior rather than a
        // stale/misaligned carryover. Mirrored into _cavityColors' G channel (see PaintMask/
        // RecomputeCavityAt) for SculptPBR's mask tint - .r stays cavity, .g is mask, so the two
        // overlays are independent.
        private float[] _mask;

        public float[] Mask => _mask;

        // Accelerates SelectGrab/QueryNear so a brush stroke doesn't scan every vertex in the
        // mesh every frame - see VertexSpatialGrid. Rebuilt by SculptController at the start
        // of each stroke (RebuildSpatialIndex), and invalidated here whenever the vertex
        // buffer is replaced/reset wholesale so a stale grid can never be queried against the
        // wrong positions; QueryNear/SelectGrab rebuild lazily with a default cell size if
        // nothing has rebuilt it yet.
        private VertexSpatialGrid _spatialGrid;

        // See SculptHistory - snapshot-based undo/redo for brush strokes, Remesh, and Reset
        // Mesh. Owned here (not SculptController) since this class already owns all the mesh
        // state a snapshot needs to capture/restore.
        private readonly SculptHistory _history = new SculptHistory();

        public Mesh Mesh => _mesh;
        public Vector3[] Vertices => _workingVertices;
        public Vector3[] Normals => _workingNormals;
        public bool CanUndo => _history.CanUndo;
        public bool CanRedo => _history.CanRedo;

        /// A set of vertices captured by SelectGrab, with smoothstep falloff weights, that
        /// can be dragged as a unit via ApplyGrabDelta. Kept as an immutable value the
        /// caller holds onto (rather than internal mutable state) so multiple independent
        /// selections - e.g. one per mirrored brush instance - can be dragged in the same
        /// frame.
        public readonly struct GrabSelection
        {
            public readonly int[] Indices;
            public readonly float[] Weights;

            public GrabSelection(int[] indices, float[] weights)
            {
                Indices = indices;
                Weights = weights;
            }

            public bool IsValid => Indices != null && Indices.Length > 0;
        }

        private void Awake()
        {
            _meshFilter = GetComponent<MeshFilter>();

            _mesh = Instantiate(_meshFilter.sharedMesh);
            _mesh.name = _meshFilter.sharedMesh.name + " (Sculpt Instance)";
            _mesh.MarkDynamic();
            _meshFilter.mesh = _mesh;

            _originalVertices = _mesh.vertices;
            _workingVertices = (Vector3[])_originalVertices.Clone();
            _workingNormals = _mesh.normals;
            _workingTriangles = _mesh.triangles;
            BuildAdjacency();
            RebuildTriangleGrid();
            _cavityColors = new Color[_workingVertices.Length];
            _mask = new float[_workingVertices.Length];
            RecomputeCavity();
            _mesh.colors = _cavityColors;

            if (useMeshCollider)
            {
                _meshCollider = GetComponent<MeshCollider>();
                if (_meshCollider == null)
                    _meshCollider = gameObject.AddComponent<MeshCollider>();
                _meshCollider.sharedMesh = _mesh;
            }
        }

        // Cell size targets ~8 triangles per cell on average, sized off the CURRENT mesh's own
        // triangle density (bounds volume / triangle count) rather than any fixed constant -
        // learned from a prior bug in SignedDistanceField's triangle-binning grid, which reused
        // an unrelated cell size and bloated badly on a coarse source mesh (see
        // [[project_scene_graph_epic]] memory, remesh perf work).
        private void RebuildTriangleGrid()
        {
            const float TargetTrianglesPerCell = 8f;
            Bounds b = _mesh.bounds;
            float volume = Mathf.Max(b.size.x * b.size.y * b.size.z, 1e-9f);
            int triCount = Mathf.Max(1, _workingTriangles.Length / 3);
            float cellVolume = volume * TargetTrianglesPerCell / triCount;
            float cellSize = Mathf.Max(Mathf.Pow(cellVolume, 1f / 3f), 0.001f);

            // Padded 50% beyond the mesh's current bounds rather than an exact fit - the grid's
            // bounds are fixed until the next full rebuild (ApplyVerticesLocal's incremental
            // path only re-buckets triangles WITHIN them, see its remarks), so a stroke that
            // keeps pushing geometry further out in the same direction would otherwise re-trigger
            // this full O(triangle count) rebuild on every single frame once it reaches the edge.
            // Padding gives it room to keep going for a while before that happens again.
            Vector3 pad = b.size * 0.5f;
            b.SetMinMax(b.min - pad, b.max + pad);

            _triangleGrid = new TriangleSpatialGrid(_workingVertices, _workingTriangles, b, cellSize);
        }

        /// True if every dirty vertex's incident triangles are still fully inside the triangle
        /// grid's (fixed-at-construction) bounds. ApplyVerticesLocal falls back to a full
        /// RebuildTriangleGrid() when this is false - see its remarks for why a stale, too-small
        /// bounds silently breaks raycasts against whatever moved past it.
        private bool MeshBoundsFitInsideTriangleGrid()
        {
            Bounds gridBounds = _triangleGrid.Bounds;
            Bounds meshBounds = _mesh.bounds;
            return gridBounds.min.x <= meshBounds.min.x && gridBounds.min.y <= meshBounds.min.y &&
                   gridBounds.min.z <= meshBounds.min.z && gridBounds.max.x >= meshBounds.max.x &&
                   gridBounds.max.y >= meshBounds.max.y && gridBounds.max.z >= meshBounds.max.z;
        }

        /// Raycasts directly against the live working mesh data (see TriangleSpatialGrid),
        /// instead of Physics.Raycast against a MeshCollider - see class remarks for why.
        /// worldRay/maxDistance/worldPoint/worldNormal are all in world space, matching what
        /// callers previously got from a RaycastHit.
        public bool RaycastMesh(Ray worldRay, float maxDistance, out Vector3 worldPoint, out Vector3 worldNormal)
        {
            worldPoint = default;
            worldNormal = default;
            // _triangleGrid is a plain C# class (not Unity-serializable), so like _adjacency
            // (see EnsureAdjacency) it comes back null after a script recompile during Play
            // mode - rebuild lazily rather than leaving raycasts silently returning false
            // until the next topology change.
            if (_triangleGrid == null) RebuildTriangleGrid();

            Transform t = transform;
            Vector3 localOrigin = t.InverseTransformPoint(worldRay.origin);
            Vector3 localDir = t.InverseTransformDirection(worldRay.direction).normalized;
            float scale = AverageScale();
            float localMaxDistance = maxDistance / Mathf.Max(0.0001f, scale);

            if (!_triangleGrid.Raycast(localOrigin, localDir, localMaxDistance, _workingVertices, _workingTriangles,
                    out float hitT, out Vector3 localNormal))
                return false;

            Vector3 localPoint = localOrigin + localDir * hitT;
            worldPoint = t.TransformPoint(localPoint);
            worldNormal = t.TransformDirection(localNormal).normalized;
            return true;
        }

        private float AverageScale()
        {
            Vector3 s = transform.lossyScale;
            return (s.x + s.y + s.z) / 3f;
        }

        /// Rebuilds the per-vertex direct-edge neighbor lists from the current mesh's
        /// triangles. O(triangle count) - cheap enough to run once per topology change but
        /// not meant to run every frame.
        private void BuildAdjacency()
        {
            int vertCount = _workingVertices.Length;
            var neighborSets = new HashSet<int>[vertCount];
            var triangleSets = new List<int>[vertCount];
            for (int i = 0; i < vertCount; i++)
            {
                neighborSets[i] = new HashSet<int>();
                triangleSets[i] = new List<int>();
            }

            int[] tris = _workingTriangles;
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                neighborSets[a].Add(b); neighborSets[a].Add(c);
                neighborSets[b].Add(a); neighborSets[b].Add(c);
                neighborSets[c].Add(a); neighborSets[c].Add(b);

                int ti = t / 3;
                triangleSets[a].Add(ti);
                triangleSets[b].Add(ti);
                triangleSets[c].Add(ti);
            }

            _adjacency = new int[vertCount][];
            _vertexTriangles = new int[vertCount][];
            for (int i = 0; i < vertCount; i++)
            {
                var arr = new int[neighborSets[i].Count];
                neighborSets[i].CopyTo(arr);
                _adjacency[i] = arr;
                _vertexTriangles[i] = triangleSets[i].ToArray();
            }
        }

        // Unity's domain-reload serializer doesn't support jagged arrays (int[][]) - unlike
        // _workingVertices/_workingTriangles, _adjacency and _vertexTriangles silently come
        // back null after a script recompile while Play mode is active ("Recompile And
        // Continue Playing"). Guards every direct access to either array so a mid-session
        // recompile rebuilds them instead of NullReferenceException-ing on the next brush
        // stroke - same lazy-rebuild-if-null pattern QueryNear already uses for _spatialGrid.
        private void EnsureAdjacency()
        {
            if (_adjacency == null || _adjacency.Length != _workingVertices.Length ||
                _vertexTriangles == null || _vertexTriangles.Length != _workingVertices.Length)
                BuildAdjacency();
        }

        /// The average position of a vertex's directly-connected neighbors (Laplacian
        /// smoothing target). Returns the vertex's own position, unchanged, if it has no
        /// neighbors (degenerate/isolated vertex).
        public Vector3 GetNeighborAverage(int vertexIndex)
        {
            EnsureAdjacency();
            int[] neighbors = _adjacency[vertexIndex];
            if (neighbors.Length == 0) return _workingVertices[vertexIndex];

            Vector3 sum = Vector3.zero;
            for (int i = 0; i < neighbors.Length; i++) sum += _workingVertices[neighbors[i]];
            return sum / neighbors.Length;
        }

        /// Pushes the current working vertex buffer into the mesh, recomputes normals/bounds,
        /// and rebuilds the triangle spatial grid so the next raycast follows the sculpted
        /// surface. Does NOT touch the MeshCollider - see class remarks.
        public void ApplyVertices()
        {
            _mesh.vertices = _workingVertices;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _workingNormals = _mesh.normals;
            RebuildTriangleGrid();
            RecomputeCavity();
            _mesh.colors = _cavityColors;
        }

        // Reused across ApplyVerticesLocal calls so a brush stroke doesn't allocate a fresh
        // HashSet every frame - cleared and refilled each call, same "grow, don't reallocate"
        // pattern as the scratch buffers in SculptController.
        private readonly HashSet<int> _dirtyTriangleScratch = new HashSet<int>();

        /// Same effect as ApplyVertices(), but the caller guarantees only the vertices in
        /// dirtyVertices moved this frame - lets the triangle grid update just the triangles
        /// incident to those vertices instead of rebuilding from the whole mesh. This is what
        /// every brush's Apply*Brush wrapper calls; ApplyVertices() stays the safe full-rebuild
        /// default for callers that touch the whole mesh at once (ResetMesh, Undo/Redo,
        /// Remesh - see their call sites).
        public void ApplyVerticesLocal(IReadOnlyCollection<int> dirtyVertices)
        {
            _mesh.vertices = _workingVertices;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _workingNormals = _mesh.normals;

            if (_triangleGrid != null && dirtyVertices.Count > 0)
            {
                if (!MeshBoundsFitInsideTriangleGrid())
                {
                    // A stroke moved geometry outside the region the triangle grid was built
                    // for - its bounds don't grow on their own (see RebuildTriangleGrid remarks),
                    // so an incremental UpdateTriangles here would re-bucket the moved triangles
                    // using stale bounds, and every future raycast's ray-vs-bounds clip test
                    // would clip away the part of the ray that now needs to reach them. This is
                    // the fix for the "Move brush stops registering on the same spot after
                    // pushing it once" bug: RaycastMesh would silently return false for that
                    // area, forever, until whatever else happened to trigger a full rebuild.
                    RebuildTriangleGrid();
                }
                else
                {
                    EnsureAdjacency();
                    _dirtyTriangleScratch.Clear();
                    foreach (int vi in dirtyVertices)
                    {
                        int[] incident = _vertexTriangles[vi];
                        for (int i = 0; i < incident.Length; i++) _dirtyTriangleScratch.Add(incident[i]);
                    }
                    _triangleGrid.UpdateTriangles(_dirtyTriangleScratch, _workingVertices, _workingTriangles);
                }
            }

            RecomputeCavityLocal(dirtyVertices);
            _mesh.colors = _cavityColors;
        }

        /// Approximates per-vertex concavity/convexity from how far a vertex sits from its
        /// neighbors' average position along its own normal: a vertex recessed relative to
        /// its neighbors (a dent) has its neighbor average out ahead of it along the normal,
        /// a vertex proud of its neighbors (a peak) has the average behind it. Encoded into
        /// vertex color .r around a 0.5 "flat" baseline (>0.5 recess, <0.5 peak) for
        /// SculptPBR's cavity coloring. Uses the cached _workingNormals (refreshed by the
        /// caller right before this runs) rather than re-reading Mesh.normals, which copies the
        /// whole array on every access.
        private void RecomputeCavity()
        {
            for (int i = 0; i < _workingVertices.Length; i++)
                RecomputeCavityAt(i);
        }

        // Reused across RecomputeCavityLocal calls - see ApplyVerticesLocal.
        private readonly HashSet<int> _dirtyCavityScratch = new HashSet<int>();

        /// Same effect as RecomputeCavity(), but only for the given vertices plus their direct
        /// neighbors - a moved vertex changes not just its own cavity value but every
        /// neighbor's too, since their GetNeighborAverage includes it. Measured as the dominant
        /// remaining per-frame cost after the triangle-grid fix (this app's high-poly-brush-lag
        /// investigation) - see [[project_sculpting_application]] memory.
        private void RecomputeCavityLocal(IReadOnlyCollection<int> dirtyVertices)
        {
            EnsureAdjacency();
            _dirtyCavityScratch.Clear();
            foreach (int vi in dirtyVertices)
            {
                _dirtyCavityScratch.Add(vi);
                int[] neighbors = _adjacency[vi];
                for (int i = 0; i < neighbors.Length; i++) _dirtyCavityScratch.Add(neighbors[i]);
            }

            foreach (int i in _dirtyCavityScratch)
                RecomputeCavityAt(i);
        }

        private void RecomputeCavityAt(int i)
        {
            Vector3 avg = GetNeighborAverage(i);
            float curvature = Vector3.Dot(avg - _workingVertices[i], _workingNormals[i]);
            float normalized = Mathf.Clamp(curvature * CavitySensitivity, -1f, 1f);
            float encoded = 0.5f + normalized * 0.5f;
            // .r = cavity, .g = mask (see _mask remarks) - .b mirrors .r, unused by the shader
            // today but harmless to keep populated in case something else ever samples it.
            _cavityColors[i] = new Color(encoded, _mask[i], encoded, 1f);
        }

        /// Paints (amount > 0) or erases (amount < 0) mask over a local-space brush footprint -
        /// does not move any vertex or touch normals/bounds/the triangle-raycast grid, just the
        /// per-vertex Mask value and its vertex-color visualization. Every brush's weight
        /// calculation reads Mask[i] to skip masked vertices - see SculptController's
        /// Apply*BrushLocal methods and SelectGrab below.
        ///
        /// hardness (0-1) reshapes the falloff instead of just scaling it: 0 is a smoothstep
        /// across the WHOLE radius (gradual, light-at-the-edges - ZBrush/Blender's "soft"
        /// feel), 1 collapses the smoothstep band down to zero width so every vertex inside
        /// the radius gets the full weight immediately (a hard cutoff at the edge, "hard"
        /// feel) - matches how most sculpting apps' brush hardness works: an inner radius that
        /// grows from 0 to the full brush radius as hardness increases.
        public void PaintMask(Vector3 localPoint, float radius, float amount, float hardness)
        {
            List<int> candidates = QueryNear(localPoint, radius);
            float innerRadius = radius * Mathf.Clamp01(hardness);
            float falloffSpan = Mathf.Max(radius - innerRadius, 1e-5f);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(_workingVertices[i], localPoint);
                if (dist > radius) continue;

                float weight;
                if (dist <= innerRadius)
                {
                    weight = 1f;
                }
                else
                {
                    float t01 = 1f - (dist - innerRadius) / falloffSpan;
                    weight = t01 * t01 * (3f - 2f * t01); // smoothstep
                }
                _mask[i] = Mathf.Clamp01(_mask[i] + amount * weight);

                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
            }
            _mesh.colors = _cavityColors;
        }

        /// Flips every vertex's mask value (protected <-> sculptable), ZBrush Ctrl+I/Blender
        /// "Invert Mask" style. O(vertex count) - fine as a one-off button click, not something
        /// called per-frame like PaintMask.
        public void InvertMask()
        {
            for (int i = 0; i < _mask.Length; i++)
            {
                _mask[i] = 1f - _mask[i];
                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
            }
            _mesh.colors = _cavityColors;
        }

        public void ResetMesh()
        {
            Array.Copy(_originalVertices, _workingVertices, _originalVertices.Length);
            _spatialGrid = null;
            ApplyVertices();
        }

        /// Call before starting a discrete edit (a brush stroke, Remesh, Reset Mesh) so Undo
        /// can revert it. _workingVertices is cloned here since it's the live array brush
        /// strokes mutate in place every frame - Mesh.triangles doesn't need cloning too,
        /// Unity's getter already returns a fresh copy every time it's read.
        public void SnapshotForUndo()
        {
            _history.PushUndo((Vector3[])_workingVertices.Clone(), _mesh.triangles);
        }

        public void Undo()
        {
            if (_history.Undo((Vector3[])_workingVertices.Clone(), _mesh.triangles, out Vector3[] verts, out int[] tris))
                RestoreSnapshot(verts, tris);
        }

        public void Redo()
        {
            if (_history.Redo((Vector3[])_workingVertices.Clone(), _mesh.triangles, out Vector3[] verts, out int[] tris))
                RestoreSnapshot(verts, tris);
        }

        // Undoing/redoing a brush stroke never changes topology (only positions), so the
        // common case is as cheap as an ordinary stroke's own ApplyVertices() call - only
        // crossing a Remesh boundary needs the heavier full rebuild (adjacency, cavity
        // buffer, collider). Triangle array LENGTH is used as the same-topology check rather
        // than a full content comparison - in this app the only thing that ever changes
        // topology is Remesh, and two different remesh results coincidentally sharing an
        // exact triangle count is vanishingly unlikely, so this is a deliberate, cheap
        // approximation rather than an oversight.
        private void RestoreSnapshot(Vector3[] vertices, int[] triangles)
        {
            bool sameTopology = triangles.Length == _workingTriangles.Length && vertices.Length == _workingVertices.Length;
            if (sameTopology)
            {
                _workingVertices = vertices;
                ApplyVertices();
                return;
            }

            // Full rebuild, mirroring Remesh()'s tail. Note: unlike Remesh(), this doesn't
            // recompute the spherical UVs MeshRemesher assigns - harmless today since
            // SculptPBR's vertex shader has no TEXCOORD0 input at all, but flagging it here in
            // case a future shader starts sampling UV0.
            _mesh.Clear();
            _mesh.indexFormat = vertices.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            _mesh.vertices = vertices;
            _mesh.triangles = triangles;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _workingNormals = _mesh.normals;

            _originalVertices = (Vector3[])vertices.Clone();
            _workingVertices = vertices;
            _workingTriangles = triangles;
            _spatialGrid = null;
            BuildAdjacency();
            RebuildTriangleGrid();
            _cavityColors = new Color[_workingVertices.Length];
            _mask = new float[_workingVertices.Length];
            RecomputeCavity();
            _mesh.colors = _cavityColors;

            if (_meshCollider != null)
            {
                _meshCollider.sharedMesh = null;
                _meshCollider.sharedMesh = _mesh;
            }
        }

        /// Rebuilds the spatial index used by SelectGrab/QueryNear over the current vertex
        /// positions. Cheap relative to a full brush stroke (called once per stroke, not per
        /// frame) but still O(vertex count), so callers should call this at the start of a
        /// stroke/drag rather than every frame - see SculptController.
        public void RebuildSpatialIndex(float cellSize)
        {
            _spatialGrid = new VertexSpatialGrid(_workingVertices, cellSize);
        }

        /// Candidate vertex indices near a local-space point - callers still need to check
        /// exact distance themselves (see VertexSpatialGrid.Query). Lazily builds the index
        /// with a radius-derived cell size if nothing has called RebuildSpatialIndex yet.
        public List<int> QueryNear(Vector3 localPoint, float radius)
        {
            if (_spatialGrid == null) RebuildSpatialIndex(Mathf.Max(radius * 0.5f, 0.01f));
            return _spatialGrid.Query(localPoint, radius);
        }

        /// Selects every vertex within radius of a local-space point, weighted by
        /// smoothstep falloff, so the same region keeps moving together for the rest of a
        /// drag - even once later deltas are queried against a point far outside this
        /// radius. Returns an invalid (empty) selection if nothing was in range.
        public GrabSelection SelectGrab(Vector3 localPoint, float radius)
        {
            var indices = new System.Collections.Generic.List<int>();
            var weights = new System.Collections.Generic.List<float>();

            List<int> candidates = QueryNear(localPoint, radius);
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(_workingVertices[i], localPoint);
                if (dist > radius) continue;

                float t01 = 1f - dist / radius;
                float smooth = t01 * t01 * (3f - 2f * t01) * (1f - _mask[i]); // smoothstep, masked-out
                if (smooth <= 0f) continue;
                indices.Add(i);
                weights.Add(smooth);
            }

            return new GrabSelection(indices.ToArray(), weights.ToArray());
        }

        /// Drags the vertices captured in a GrabSelection by a local-space movement delta.
        /// Does not push the change to the mesh/collider - call ApplyVertices once after
        /// applying deltas to every selection for the frame.
        public void ApplyGrabDelta(GrabSelection selection, Vector3 localDelta)
        {
            if (!selection.IsValid) return;

            for (int i = 0; i < selection.Indices.Length; i++)
                _workingVertices[selection.Indices[i]] += localDelta * selection.Weights[i];
        }

        /// Rebuilds the mesh from scratch via voxel remeshing (MeshRemesher), giving even
        /// polygon density across the whole sculpted shape instead of the stretched/thin
        /// triangles heavy sculpting leaves in the original topology. Commits the new
        /// topology as the mesh's baseline, so ResetMesh afterwards reverts to this remeshed
        /// shape rather than the pre-sculpt original.
        public void Remesh(int resolution)
        {
            Mesh remeshed = MeshRemesher.Remesh(_mesh.vertices, _mesh.triangles, resolution);
            remeshed.name = _mesh.name;
            remeshed.MarkDynamic();

            _mesh = remeshed;
            _meshFilter.mesh = _mesh;

            _originalVertices = _mesh.vertices;
            _workingVertices = (Vector3[])_originalVertices.Clone();
            _workingNormals = _mesh.normals;
            _workingTriangles = _mesh.triangles;
            _spatialGrid = null;
            BuildAdjacency();
            RebuildTriangleGrid();
            _cavityColors = new Color[_workingVertices.Length];
            _mask = new float[_workingVertices.Length];
            RecomputeCavity();
            _mesh.colors = _cavityColors;

            if (_meshCollider != null)
            {
                _meshCollider.sharedMesh = null;
                _meshCollider.sharedMesh = _mesh;
            }
        }
    }
}
