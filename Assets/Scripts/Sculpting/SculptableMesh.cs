using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

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

        // Pushes only the touched vertices' position/normal/color into the mesh's GPU vertex
        // buffer every ApplyVerticesLocal call, instead of Unity's Mesh.vertices/.normals/
        // .colors setters (which always reupload the WHOLE array regardless of footprint size -
        // see GpuVertexScatter's remarks). A plain C# class like _triangleGrid/_spatialGrid, so
        // it gets the same lazy-rebuild-if-null treatment for the mid-Play-recompile domain-
        // reload case (see EnsureGpuScatter) - it's never treated as a source of truth, only
        // ever (re)bound to whatever _mesh currently is.
        private GpuVertexScatter _gpuScatter;

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

            // Read the source mesh's own data BEFORE redefining its vertex layout below -
            // ConfigureGpuVertexLayout resets the buffer to a smaller, GPU-compute-writable
            // layout (position/normal/color only, dropping whatever else the source asset had
            // - e.g. UV0), so anything we still need has to be captured first.
            _originalVertices = _mesh.vertices;
            _workingVertices = (Vector3[])_originalVertices.Clone();
            _workingNormals = _mesh.normals;
            _workingTriangles = _mesh.triangles;

            ConfigureGpuVertexLayout(_mesh, _workingVertices.Length);
            _mesh.vertices = _workingVertices;
            _mesh.normals = _workingNormals;

            BuildAdjacency();
            RebuildTriangleGrid();
            _cavityColors = new Color[_workingVertices.Length];
            _mask = new float[_workingVertices.Length];
            RecomputeCavity();
            _mesh.colors = _cavityColors;
            BindGpuScatter();

            if (useMeshCollider)
            {
                _meshCollider = GetComponent<MeshCollider>();
                if (_meshCollider == null)
                    _meshCollider = gameObject.AddComponent<MeshCollider>();
                _meshCollider.sharedMesh = _mesh;
            }
        }

        private void OnDestroy()
        {
            _gpuScatter?.Dispose();
            if (_nativeAdjacencyOffsets.IsCreated) _nativeAdjacencyOffsets.Dispose();
            if (_nativeAdjacencyNeighbors.IsCreated) _nativeAdjacencyNeighbors.Dispose();
        }

        // Marks the mesh's vertex buffer as compute-shader-writable (GraphicsBuffer.Target.Raw)
        // and pins its layout to exactly position/normal/color in one stream, with no UV0/
        // tangent - matches what SculptPBR.shader's Attributes struct actually reads (no
        // TEXCOORD0 at all), and mirrors the existing, already-verified-harmless "UV0 doesn't
        // survive a full mesh rebuild" outcome RestoreSnapshot's full-rebuild path already has.
        // Resetting the layout clears whatever data was there - callers must reassign vertices/
        // normals/colors immediately afterward.
        private static void ConfigureGpuVertexLayout(Mesh mesh, int vertexCount)
        {
            mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            mesh.SetVertexBufferParams(vertexCount,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4));
        }

        // _gpuScatter is a plain C# class (not Unity-serializable), so like _triangleGrid/
        // _spatialGrid it can come back null after a script recompile during Play mode -
        // lazily recreate rather than assuming it always survives. Rebinding is cheap and must
        // happen unconditionally whenever _mesh itself changes identity (Remesh/RestoreSnapshot),
        // not just on the null/recompile path, since a stale buffer handle from a replaced mesh
        // is silently wrong, not absent.
        private void EnsureGpuScatter()
        {
            if (_gpuScatter == null) BindGpuScatter();
        }

        private void BindGpuScatter()
        {
            _gpuScatter ??= new GpuVertexScatter();
            _gpuScatter.BindMesh(_mesh);
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

        // CSR (compressed sparse row) flattening of _adjacency for the Smooth brush's Burst job
        // (SculptController.SmoothRelaxJob) - jagged int[][] arrays can't be used inside a Burst-
        // compiled job at all, so this is a NativeArray-backed copy of the same data: neighbors
        // of vertex i live in NeighborsFlat[OffsetsFlat[i] .. OffsetsFlat[i+1]). Rebuilt whenever
        // the managed _adjacency itself is rebuilt (topology change, or a mid-Play-recompile
        // domain reload nulling it - see EnsureAdjacency/[[project_domain_reload_null_fields]]) -
        // a plain length check can't distinguish "still valid" from "silently rebuilt with
        // identical content" after a same-topology domain reload, but that distinction is
        // harmless here since BuildAdjacency is deterministic - rebuilding from unchanged source
        // data just reproduces the same content.
        private NativeArray<int> _nativeAdjacencyOffsets;
        private NativeArray<int> _nativeAdjacencyNeighbors;

        public NativeArray<int> AdjacencyOffsets { get { EnsureNativeAdjacency(); return _nativeAdjacencyOffsets; } }
        public NativeArray<int> AdjacencyNeighbors { get { EnsureNativeAdjacency(); return _nativeAdjacencyNeighbors; } }

        private void EnsureNativeAdjacency()
        {
            EnsureAdjacency();
            if (_nativeAdjacencyOffsets.IsCreated && _nativeAdjacencyOffsets.Length == _workingVertices.Length + 1)
                return;

            if (_nativeAdjacencyOffsets.IsCreated) _nativeAdjacencyOffsets.Dispose();
            if (_nativeAdjacencyNeighbors.IsCreated) _nativeAdjacencyNeighbors.Dispose();

            int vertCount = _workingVertices.Length;
            int totalNeighbors = 0;
            for (int i = 0; i < vertCount; i++) totalNeighbors += _adjacency[i].Length;

            _nativeAdjacencyOffsets = new NativeArray<int>(vertCount + 1, Allocator.Persistent);
            _nativeAdjacencyNeighbors = new NativeArray<int>(totalNeighbors, Allocator.Persistent);

            int cursor = 0;
            for (int i = 0; i < vertCount; i++)
            {
                _nativeAdjacencyOffsets[i] = cursor;
                int[] neighbors = _adjacency[i];
                for (int k = 0; k < neighbors.Length; k++) _nativeAdjacencyNeighbors[cursor++] = neighbors[k];
            }
            _nativeAdjacencyOffsets[vertCount] = cursor;
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
        /// surface. Does NOT touch the MeshCollider - see class remarks. Full-mesh cost is fine
        /// here since callers (ResetMesh/Undo/Redo across a topology change/Remesh) already
        /// touch the whole mesh at once - see ApplyVerticesLocal for the footprint-scoped path
        /// every ordinary brush stroke uses instead.
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

        // Reused across RecomputeNormalsLocal calls - same "grow, don't reallocate" pattern as
        // _dirtyCavityScratch/_dirtyTriangleScratch.
        private readonly HashSet<int> _dirtyNormalScratch = new HashSet<int>();

        /// Recomputes normals for exactly the affected vertices (dirty vertices plus their
        /// direct neighbors, mirroring RecomputeCavityLocal's scope) instead of Mesh.
        /// RecalculateNormals()'s full-mesh scan - a vertex's normal only changes when one of
        /// its incident triangles changes shape, and two vertices share a triangle iff they're
        /// adjacent, so this scope is already exactly correct, no wider walk needed. Sums each
        /// incident triangle's raw (unnormalized) face-normal cross product - its magnitude is
        /// proportional to the triangle's area, so this naturally area-weights the average,
        /// matching what RecalculateNormals() itself does - just scoped to the affected set
        /// instead of the whole mesh. See ApplyVerticesLocal.
        private void RecomputeNormalsLocal(IReadOnlyCollection<int> dirtyVertices)
        {
            EnsureAdjacency();
            _dirtyNormalScratch.Clear();
            foreach (int vi in dirtyVertices)
            {
                _dirtyNormalScratch.Add(vi);
                int[] neighbors = _adjacency[vi];
                for (int i = 0; i < neighbors.Length; i++) _dirtyNormalScratch.Add(neighbors[i]);
            }

            foreach (int i in _dirtyNormalScratch)
                RecomputeNormalAt(i);
        }

        private void RecomputeNormalAt(int i)
        {
            int[] incidentTris = _vertexTriangles[i];
            Vector3 sum = Vector3.zero;
            for (int t = 0; t < incidentTris.Length; t++)
            {
                int baseIndex = incidentTris[t] * 3;
                Vector3 a = _workingVertices[_workingTriangles[baseIndex]];
                Vector3 b = _workingVertices[_workingTriangles[baseIndex + 1]];
                Vector3 c = _workingVertices[_workingTriangles[baseIndex + 2]];
                sum += Vector3.Cross(b - a, c - a);
            }
            // Degenerate (zero-area) triangles can null out the sum for an isolated vertex -
            // keep the previous normal rather than collapsing it to zero, same "leave it alone"
            // behavior GetNeighborAverage uses for a neighborless vertex.
            if (sum.sqrMagnitude > 1e-12f) _workingNormals[i] = sum.normalized;
        }

        /// Grows the mesh's bounds to include the given vertices' current positions - O(dirty
        /// count) instead of Mesh.RecalculateBounds()'s O(total vertex count) full scan. Bounds
        /// only ever need to grow to stay valid for culling; a stroke that moves geometry inward
        /// leaves bounds slightly loose rather than exactly tight, which is harmless - the same
        /// approximation any incremental-bounds scheme makes. ApplyVertices() (Remesh/Reset/
        /// topology-crossing Undo's full-rebuild path) keeps calling the real
        /// RecalculateBounds() - already-infrequent full-mesh operations that don't need this.
        private void ExpandBoundsLocal(IReadOnlyCollection<int> dirtyVertices)
        {
            if (dirtyVertices.Count == 0) return;
            Bounds b = _mesh.bounds;
            foreach (int i in dirtyVertices)
                b.Encapsulate(_workingVertices[i]);
            _mesh.bounds = b;
        }

        // Reused across ApplyVerticesLocal calls so a brush stroke doesn't allocate a fresh
        // HashSet every frame - cleared and refilled each call, same "grow, don't reallocate"
        // pattern as the scratch buffers in SculptController.
        private readonly HashSet<int> _dirtyTriangleScratch = new HashSet<int>();

        // Reused across PaintMask calls so a held mask-paint drag doesn't allocate a fresh
        // HashSet every frame - same pattern as _dirtyTriangleScratch. Holds exactly the
        // candidates that passed PaintMask's own dist &lt;= radius check, i.e. the vertices
        // actually touched this call (QueryNear's candidate list is a superset - see its
        // remarks).
        private readonly HashSet<int> _paintMaskScratch = new HashSet<int>();

        /// Same effect as ApplyVertices(), but the caller guarantees only the vertices in
        /// dirtyVertices moved this frame - lets the triangle grid update just the triangles
        /// incident to those vertices instead of rebuilding from the whole mesh. This is what
        /// every brush's Apply*Brush wrapper calls; ApplyVertices() stays the safe full-rebuild
        /// default for callers that touch the whole mesh at once (ResetMesh, Undo/Redo,
        /// Remesh - see their call sites).
        public void ApplyVerticesLocal(IReadOnlyCollection<int> dirtyVertices)
        {
            RecomputeNormalsLocal(dirtyVertices);
            ExpandBoundsLocal(dirtyVertices);

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

            // Replaces the full _mesh.vertices=/.colors= reassignment (and the .normals=
            // assignment removed above) with a compute-shader scatter write scoped to just the
            // affected vertices - see GpuVertexScatter remarks. _dirtyNormalScratch is exactly
            // that "dirty ∪ neighbors" set (already computed by RecomputeNormalsLocal above,
            // and identical to what RecomputeCavityLocal just used) - position is redundant-but-
            // harmless for neighbor-only entries whose position didn't change, only their
            // normal/cavity color did.
            EnsureGpuScatter();
            _gpuScatter.ScatterDirty(_dirtyNormalScratch, _dirtyNormalScratch.Count, _workingVertices, _workingNormals, _cavityColors);
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

            _paintMaskScratch.Clear();
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
                _paintMaskScratch.Add(i);
            }

            // Held mask-paint drags call this every frame (see SculptController.ApplyMaskPaint),
            // so at high polycounts this needs the same footprint-scoped GPU write
            // ApplyVerticesLocal uses instead of a full _mesh.colors= reassignment - see
            // GpuVertexScatter remarks. Position/normal are unchanged by mask painting; scattering
            // them anyway alongside the updated color is the same accepted redundant-write pattern
            // ApplyVerticesLocal already relies on for its neighbor-only entries.
            EnsureGpuScatter();
            _gpuScatter.ScatterDirty(_paintMaskScratch, _paintMaskScratch.Count, _workingVertices, _workingNormals, _cavityColors);
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

        /// Call before Remesh/Reset Mesh (topology-changing edits) so Undo can revert them - a
        /// full clone is unavoidable here since nothing less can describe a topology change.
        /// _workingVertices is cloned since it's the live array brush strokes mutate in place -
        /// Mesh.triangles doesn't need cloning too, Unity's getter already returns a fresh copy.
        /// Ordinary brush strokes use BeginStrokeUndo/EndStrokeUndo instead - see their remarks.
        public void SnapshotForUndo()
        {
            _history.PushFullUndo((Vector3[])_workingVertices.Clone(), _mesh.triangles);
        }

        // Accumulates each touched vertex's PRE-stroke position the first time a held stroke
        // touches it, committed as one delta undo entry when the stroke ends - see
        // RecordUndoBeforeIfNeeded/EndStrokeUndo. Replaces the old up-front full-mesh clone on
        // every stroke START (paid regardless of what the stroke ends up touching, or even if it
        // misses the mesh entirely) with a cost proportional to what actually moved. Two
        // parallel lists rather than a Dictionary<int,Vector3> - insertion order doesn't matter
        // here and this avoids dictionary overhead for what's typically hundreds-to-thousands of
        // entries per stroke.
        private readonly List<int> _strokeDeltaIndices = new List<int>();
        private readonly List<Vector3> _strokeDeltaBefore = new List<Vector3>();
        // Which indices are already recorded THIS stroke - separate from _dirtyVertexScratch-
        // style per-frame scratch since this has to persist across every frame of a held stroke,
        // not just one.
        private readonly HashSet<int> _strokeRecordedIndices = new HashSet<int>();

        /// Call once on stroke start (mouse-press) to clear the previous stroke's accumulator.
        public void BeginStrokeUndo()
        {
            _strokeDeltaIndices.Clear();
            _strokeDeltaBefore.Clear();
            _strokeRecordedIndices.Clear();
        }

        /// Call from a brush's per-candidate write site, BEFORE overwriting
        /// _workingVertices[index], so the FIRST touch during this stroke captures the true
        /// pre-stroke value - a vertex touched across multiple frames of the same held stroke
        /// only records once (its value from before the very first touch, not the most recent).
        public void RecordUndoBeforeIfNeeded(int index)
        {
            if (_strokeRecordedIndices.Add(index))
            {
                _strokeDeltaIndices.Add(index);
                _strokeDeltaBefore.Add(_workingVertices[index]);
            }
        }

        /// Call once when a stroke ends (mouse-up) to commit whatever was recorded as one undo
        /// entry - a no-op if the stroke touched nothing (e.g. a click that missed the mesh),
        /// which now costs nothing instead of the old unconditional full-mesh clone up front.
        /// Idempotent: clears the accumulator after pushing, so calling this more than once
        /// without an intervening BeginStrokeUndo (e.g. a caller's release-detection firing from
        /// more than one place) harmlessly no-ops on the second call instead of pushing the same
        /// delta twice.
        public void EndStrokeUndo()
        {
            if (_strokeDeltaIndices.Count == 0) return;
            _history.PushDeltaUndo(_strokeDeltaIndices.ToArray(), _strokeDeltaBefore.ToArray());
            _strokeDeltaIndices.Clear();
            _strokeDeltaBefore.Clear();
            _strokeRecordedIndices.Clear();
        }

        public void Undo()
        {
            if (_history.TryUndoDelta(i => _workingVertices[i], out int[] indices, out Vector3[] positions))
            {
                RestoreDelta(indices, positions);
                return;
            }
            if (_history.TryUndoFull((Vector3[])_workingVertices.Clone(), _mesh.triangles, out Vector3[] verts, out int[] tris))
                RestoreSnapshot(verts, tris);
        }

        public void Redo()
        {
            if (_history.TryRedoDelta(i => _workingVertices[i], out int[] indices, out Vector3[] positions))
            {
                RestoreDelta(indices, positions);
                return;
            }
            if (_history.TryRedoFull((Vector3[])_workingVertices.Clone(), _mesh.triangles, out Vector3[] verts, out int[] tris))
                RestoreSnapshot(verts, tris);
        }

        /// Fast-path restore for a delta undo/redo entry - writes the given positions directly
        /// into _workingVertices at the given indices (no full-array reassignment) and reuses
        /// the exact same incremental update path (ApplyVerticesLocal) a live brush stroke
        /// already goes through for normals/bounds/triangle-grid/cavity/GPU upload - no separate
        /// propagation logic needed.
        private void RestoreDelta(int[] indices, Vector3[] positions)
        {
            for (int k = 0; k < indices.Length; k++)
                _workingVertices[indices[k]] = positions[k];
            ApplyVerticesLocal(indices);
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
            // SculptPBR's vertex shader has no TEXCOORD0 input at all (ConfigureGpuVertexLayout
            // below drops it from the buffer entirely regardless, same as Remesh()'s tail).
            _mesh.Clear();
            _mesh.indexFormat = vertices.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            ConfigureGpuVertexLayout(_mesh, vertices.Length);
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
            BindGpuScatter();

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
            {
                int idx = selection.Indices[i];
                RecordUndoBeforeIfNeeded(idx);
                _workingVertices[idx] += localDelta * selection.Weights[i];
            }
        }

        /// Rebuilds the mesh from scratch via voxel remeshing (MeshRemesher), giving even
        /// polygon density across the whole sculpted shape instead of the stretched/thin
        /// triangles heavy sculpting leaves in the original topology. Commits the new
        /// topology as the mesh's baseline, so ResetMesh afterwards reverts to this remeshed
        /// shape rather than the pre-sculpt original.
        public void Remesh(int resolution)
        {
            // Must read _workingVertices, not _mesh.vertices - ordinary sculpting now writes
            // touched vertices straight into the mesh's GPU buffer via GpuVertexScatter
            // (ApplyVerticesLocal), which Unity's managed Mesh.vertices getter does NOT
            // reliably reflect (see feedback_unity_gpu_buffer_verification memory). Reading
            // _mesh.vertices here silently remeshed from the stale pre-sculpt shape instead of
            // the actual sculpted one. _workingTriangles is topology, unaffected either way,
            // but reading it avoids the same needless full-array copy _mesh.triangles would do.
            Mesh remeshed = MeshRemesher.Remesh(_workingVertices, _workingTriangles, resolution);
            remeshed.name = _mesh.name;
            remeshed.MarkDynamic();

            _mesh = remeshed;
            _meshFilter.mesh = _mesh;

            // Read before ConfigureGpuVertexLayout resets the buffer - drops the spherical UVs
            // MeshRemesher assigned, same already-accepted tradeoff as RestoreSnapshot's
            // full-rebuild path (harmless: SculptPBR's Attributes struct has no TEXCOORD0).
            _originalVertices = _mesh.vertices;
            _workingVertices = (Vector3[])_originalVertices.Clone();
            _workingNormals = _mesh.normals;
            _workingTriangles = _mesh.triangles;

            ConfigureGpuVertexLayout(_mesh, _workingVertices.Length);
            _mesh.vertices = _workingVertices;
            _mesh.normals = _workingNormals;

            _spatialGrid = null;
            BuildAdjacency();
            RebuildTriangleGrid();
            _cavityColors = new Color[_workingVertices.Length];
            _mask = new float[_workingVertices.Length];
            RecomputeCavity();
            _mesh.colors = _cavityColors;
            BindGpuScatter();

            if (_meshCollider != null)
            {
                _meshCollider.sharedMesh = null;
                _meshCollider.sharedMesh = _mesh;
            }
        }
    }
}
