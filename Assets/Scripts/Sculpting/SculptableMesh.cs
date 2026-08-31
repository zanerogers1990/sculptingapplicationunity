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
        private Renderer _renderer;
        // Visibility toggle for the Scene Graph panel (see SelectionManager.SetVisible) -
        // toggles Renderer.enabled rather than GameObject.SetActive so the object stays
        // registered/selectable and its MonoBehaviours keep running while hidden, just
        // invisible - matches "hide an arm to see the torso" without the object disappearing
        // from the scene list.
        private bool _visible = true;
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
        // Raw (unsmoothed, pre-sensitivity) curvature per vertex, kept as its own array so the
        // one-ring blur in EncodeCavityAt has unsmoothed neighbour values to average - blurring
        // in place would feed already-blurred values back in and diffuse far more than intended.
        private float[] _cavityRaw;
        // Scales CurvatureAt's dimensionless output into the -1..1 encoded range. Was 25 back
        // when curvature was a RAW DISTANCE (see CurvatureAt for why that was wrong); a
        // size-relative input needs a far smaller multiplier. A sphere reads about -0.5 by
        // construction, so this leaves a plain ball comfortably inside the range and lets
        // genuine creases saturate.
        private const float CavitySensitivity = 1.2f;

        /// Raw (dimensionless, resolution/scale-independent - see CurvatureAt), UNCLAMPED
        /// departure from this mesh's own mean curvature at a vertex: 0 is exactly baseline,
        /// larger is more extreme in EITHER direction (a recess or a ridge). Deliberately NOT
        /// _cavityColors.r - that value exists for VISUAL cavity tinting and is tuned
        /// (CavitySensitivity, clamped to +-1) to saturate quickly for contrast, which erases
        /// exactly the distinction a gating threshold needs: a deliberately rounded lobe tip
        /// and a genuinely sharp crease both clamp to the same saturated extreme, even though
        /// the crease's raw curvature can be many times larger. Exposed read-only so a brush
        /// can scale its own effect by how extreme the EXISTING curvature already is at a
        /// vertex - see SculptController's surface-relax pass, the first caller. Stale within
        /// a single dab exactly like Normals is (see ApplyVerticesLocal) - refreshed once per
        /// dab-application, not recomputed mid-dab.
        public float CurvatureDeviationAt(int index) => Mathf.Abs(_cavityRaw[index] - _cavityMean);

        // Per-vertex mask: 0 = fully sculptable (default), 1 = fully protected. Every brush
        // loop multiplies its falloff weight by (1 - Mask[i]), so a masked area simply doesn't
        // move under any brush. Reset to all-zero whenever topology changes (Awake/Remesh/
        // RestoreSnapshot) - a mask painted before a Remesh has no well-defined mapping onto
        // the remeshed vertex set, so starting fresh is the honest behavior rather than a
        // stale/misaligned carryover. Mirrored into _cavityColors' G channel (see PaintMask/
        // EncodeCavityAt) for SculptPBR's mask tint - .r stays cavity, .g is mask, so the two
        // overlays are independent.
        private float[] _mask;

        public float[] Mask => _mask;

        /// Bumped by every operation that changes _mask (painting, inverting, restoring, and the
        /// wholesale reset a topology change forces). Lets a watcher tell "the mask moved" from
        /// "nothing happened" without diffing an array that can be millions of entries long -
        /// same cheap-poll idiom SelectionManager.SelectionVersion already serves for the UI.
        /// Read by MaskExtractController to keep a live extract preview following the brush.
        public int MaskVersion { get; private set; }

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
        public int[] Triangles => _workingTriangles;
        // Undo/redo ORDER lives in EditHistory, not here - a per-object stack cannot say
        // whether the last thing the user did was on THIS object (see EditHistory's remarks).
        // These are the hooks it drives this object's own payload stack through.
        public long HistoryBytes => _history.ApproxBytes;
        public void ClearHistory() => _history.Clear();
        public bool DropOldestUndoEntry() => _history.DropOldestUndo();
        public bool DropNewestRedoEntry() => _history.DropNewestRedo();

        public bool Visible => _visible;

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_renderer != null) _renderer.enabled = visible;
        }

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
            _renderer = GetComponent<Renderer>();

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
            _cavityRaw = new float[_workingVertices.Length];
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

        // Registers with the scene's SelectionManager (see its class remarks for why this
        // deliberately doesn't happen in Awake - a component that needs Register to have
        // already run should read it from Start(), not Awake, since OnEnable order between
        // separate GameObjects isn't guaranteed either). Also covers a runtime-spawned object
        // (PrimitiveSpawner/MeshMirror AddComponent<SculptableMesh>()), whose Awake+OnEnable
        // fire synchronously the moment the component is added.
        private void OnEnable()
        {
            FindFirstObjectByType<SelectionManager>()?.Register(this);
        }

        private void OnDestroy()
        {
            // Idempotent alongside SelectionManager.DeleteObject's own explicit Unregister
            // (List.Remove on an already-removed item is a harmless no-op) - this is the
            // fallback for any OTHER path that destroys this GameObject directly (e.g.
            // MeshJoiner destroying a non-survivor), so it can never be left stuck in
            // AllObjects.
            FindFirstObjectByType<SelectionManager>()?.Unregister(this);
            ReleaseNativeResources();
        }

        /// Frees every native/GPU allocation this component owns and leaves the managed side in
        /// the same "not built yet" state a fresh instance has, so EnsureGpuScatter and
        /// EnsureNativeAdjacency simply rebuild on next use. Called from OnDestroy, and from
        /// NativeReloadGuard before an editor domain reload - which wipes these fields WITHOUT
        /// calling OnDestroy, orphaning whatever they pointed at (see that class for the full
        /// story).
        internal void ReleaseNativeResources()
        {
            _gpuScatter?.Dispose();
            _gpuScatter = null;
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
            // InverseTransformVector, NOT InverseTransformDirection: Direction applies only the
            // inverse ROTATION and deliberately ignores scale, so on a non-uniformly scaled
            // object it hands back a local direction that no longer points where the cursor
            // does. The origin (InverseTransformPoint) is un-scaled correctly, so the two
            // disagree and the ray sweeps off the surface - the whole object goes unresponsive
            // to brushing, and wherever the skewed ray does still clip geometry the hover
            // indicator lands somewhere other than the cursor, jumping around as the camera
            // orbits. Uniform scale hides the bug entirely (a uniform scale only changes the
            // direction's length, which the normalize below removes anyway).
            Vector3 localDir = t.InverseTransformVector(worldRay.direction).normalized;
            // Divided by the SMALLEST scale component rather than the average: a local step of
            // length 1 covers at least minScale of world distance, so this is the longest the
            // local ray could need to be to cover maxDistance in world space. The average could
            // under-estimate it on a stretched object and clip the ray short.
            float localMaxDistance = maxDistance / Mathf.Max(0.0001f, MinScale());

            if (!_triangleGrid.Raycast(localOrigin, localDir, localMaxDistance, _workingVertices, _workingTriangles,
                    out float hitT, out Vector3 localNormal, _hiddenTriangles))
                return false;

            Vector3 localPoint = localOrigin + localDir * hitT;
            worldPoint = t.TransformPoint(localPoint);
            worldNormal = LocalToWorldNormal(localNormal);
            return true;
        }

        /// Normals do not transform like directions when scale is involved - they need the
        /// inverse transpose, or a stretched surface reports a normal that is no longer
        /// perpendicular to it (which the normal-driven brushes then push along). Transform's
        /// own TransformDirection/InverseTransformDirection are rotation-only and so are wrong
        /// for this on any non-uniformly scaled object - see RaycastMesh.
        public Vector3 LocalToWorldNormal(Vector3 localNormal) =>
            transform.worldToLocalMatrix.transpose.MultiplyVector(localNormal).normalized;

        /// Inverse of LocalToWorldNormal - what the brushes use to bring a world-space hit
        /// normal back into the local space they deform vertices in.
        public Vector3 WorldToLocalNormal(Vector3 worldNormal) =>
            transform.localToWorldMatrix.transpose.MultiplyVector(worldNormal).normalized;

        private float MinScale()
        {
            Vector3 s = transform.lossyScale;
            return Mathf.Min(Mathf.Abs(s.x), Mathf.Min(Mathf.Abs(s.y), Mathf.Abs(s.z)));
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
        public void ApplyVertices() => ApplyVertices(true);

        /// fullRebuild:false skips the triangle-raycast grid rebuild and the cavity recompute -
        /// the two O(vertex count) passes in here that a LIVE whole-mesh drag doesn't need on
        /// every frame (nothing raycasts the mesh while a gizmo handle is being dragged, and a
        /// cavity tint that re-derived itself every frame of the drag would read as flicker).
        /// Callers using it must finish with a full ApplyVertices() when the drag ends, or the
        /// next brush raycast tests against the pre-drag surface - see BeginMaskedTransform/
        /// EndMaskedTransform, the only user today.
        public void ApplyVertices(bool fullRebuild)
        {
            _mesh.vertices = _workingVertices;
            if (_anyHidden)
            {
                // Mesh.RecalculateNormals() derives normals from the INDEX BUFFER, which while
                // something is hidden no longer references the hidden vertices at all - it would
                // hand them a zero normal, and they would come back black the moment they were
                // shown again. Deriving from the full topology instead keeps every vertex's
                // normal correct whether or not it is currently drawn.
                RecomputeAllNormalsFromTopology();
                _mesh.normals = _workingNormals;
            }
            else
            {
                _mesh.RecalculateNormals();
                _workingNormals = _mesh.normals;
            }
            _mesh.RecalculateBounds();
            if (fullRebuild)
            {
                // A wholesale vertex reassignment invalidates any index built over the old
                // positions - QueryNear/SelectGrab rebuild lazily from null (see their remarks),
                // whereas a kept-but-stale grid is silently wrong.
                _spatialGrid = null;
                RebuildTriangleGrid();
                RecomputeCavity();
            }
            // Reassigned even on the cheap path: Mesh.vertices= reuploads from Unity's own
            // CPU-side mesh data, which does NOT include the colors GpuVertexScatter wrote
            // straight into the GPU buffer (see GpuVertexScatter/_cavityColors remarks), so
            // skipping this would revert the mask/cavity tint to whatever Unity last knew.
            _mesh.colors = _cavityColors;
        }

        // The dirty vertices plus their direct one-ring neighbors - the set of vertices whose
        // normal AND cavity value a frame's movement can change, and exactly what gets uploaded
        // to the GPU. Built ONCE per ApplyVerticesLocal (see BuildAffectedSet) and then read by
        // the normal pass, the cavity pass and the scatter; normals and cavity each used to
        // build their own private copy of this identical set, which meant walking every dirty
        // vertex's adjacency twice for no difference in the result.
        private readonly HashSet<int> _dirtyNormalScratch = new HashSet<int>();

        /// Fills _dirtyNormalScratch with the dirty vertices and their direct neighbors. That
        /// scope is already exactly right for both consumers, no wider walk needed: a vertex's
        /// normal only changes when one of its incident triangles changes shape, and two
        /// vertices share a triangle iff they're adjacent; a vertex's cavity value only changes
        /// when it moves or one of its neighbors does, since it is derived from the offsets to
        /// those neighbors.
        private void BuildAffectedSet(List<int> dirtyVertices)
        {
            EnsureAdjacency();
            _dirtyNormalScratch.Clear();
            for (int k = 0; k < dirtyVertices.Count; k++)
            {
                int vi = dirtyVertices[k];
                _dirtyNormalScratch.Add(vi);
                int[] neighbors = _adjacency[vi];
                for (int i = 0; i < neighbors.Length; i++) _dirtyNormalScratch.Add(neighbors[i]);
            }
        }

        /// Recomputes normals for exactly the affected vertices instead of Mesh.
        /// RecalculateNormals()'s full-mesh scan. Sums each incident triangle's raw
        /// (unnormalized) face-normal cross product - its magnitude is proportional to the
        /// triangle's area, so this naturally area-weights the average, matching what
        /// RecalculateNormals() itself does - just scoped to the affected set instead of the
        /// whole mesh. Reads the set BuildAffectedSet just filled. See ApplyVerticesLocal.
        private void RecomputeNormalsLocal()
        {
            foreach (int i in _dirtyNormalScratch)
                RecomputeNormalAt(i);
        }

        // Face-normal accumulator for RecomputeAllNormalsFromTopology. Its own array rather than
        // summing into _workingNormals directly, so a vertex whose faces cancel out to nothing
        // (a degenerate sliver - see RecomputeNormalAt) can keep its PREVIOUS normal instead of
        // being handed an arbitrary one. Allocated only if that path is ever taken, i.e. only on
        // a whole-mesh reapply while part of the mesh is hidden.
        private Vector3[] _normalAccumScratch;

        /// Whole-mesh version of RecomputeNormalAt. Matches Mesh.RecalculateNormals' own
        /// area-weighted average (a face's raw cross product has magnitude proportional to its
        /// area), but reads the FULL triangle list rather than the mesh's current index buffer -
        /// see ApplyVertices for why that distinction matters while geometry is hidden.
        private void RecomputeAllNormalsFromTopology()
        {
            if (_workingNormals == null || _workingNormals.Length != _workingVertices.Length)
                _workingNormals = new Vector3[_workingVertices.Length];
            if (_normalAccumScratch == null || _normalAccumScratch.Length != _workingVertices.Length)
                _normalAccumScratch = new Vector3[_workingVertices.Length];
            Array.Clear(_normalAccumScratch, 0, _normalAccumScratch.Length);

            for (int b = 0; b + 2 < _workingTriangles.Length; b += 3)
            {
                int i0 = _workingTriangles[b], i1 = _workingTriangles[b + 1], i2 = _workingTriangles[b + 2];
                Vector3 a = _workingVertices[i0];
                Vector3 face = Vector3.Cross(_workingVertices[i1] - a, _workingVertices[i2] - a);
                _normalAccumScratch[i0] += face;
                _normalAccumScratch[i1] += face;
                _normalAccumScratch[i2] += face;
            }

            for (int i = 0; i < _workingNormals.Length; i++)
            {
                // Same hand-rolled normalize, epsilon and keep-the-old-value fallback
                // RecomputeNormalAt uses - see its remarks for why Vector3.normalized's own
                // epsilon is a hundred times too loose to be safe here.
                float sqrMag = _normalAccumScratch[i].sqrMagnitude;
                if (sqrMag > 1e-12f) _workingNormals[i] = _normalAccumScratch[i] / Mathf.Sqrt(sqrMag);
            }
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
            //
            // Normalized by hand rather than via Vector3.normalized, which has its OWN epsilon
            // (magnitude < 1e-5, i.e. sqrMagnitude < 1e-10) and silently returns the ZERO vector
            // below it. That threshold is a hundred times looser than this guard, so any sum
            // landing in the gap between them passed the guard and then got assigned zero -
            // exactly the collapse the guard exists to prevent, and a black-shaded vertex on
            // screen. It takes genuinely sliver-thin triangles to reach, but a dense mesh's
            // triangles are small enough in absolute terms to get there (measured: 1124 vertices
            // zeroed around a 157k-vertex sphere's pole, where the triangles degenerate).
            float sqrMag = sum.sqrMagnitude;
            if (sqrMag > 1e-12f) _workingNormals[i] = sum / Mathf.Sqrt(sqrMag);
        }

        /// Grows the mesh's bounds to include the given vertices' current positions - O(dirty
        /// count) instead of Mesh.RecalculateBounds()'s O(total vertex count) full scan. Bounds
        /// only ever need to grow to stay valid for culling; a stroke that moves geometry inward
        /// leaves bounds slightly loose rather than exactly tight, which is harmless - the same
        /// approximation any incremental-bounds scheme makes. ApplyVertices() (Remesh/Reset/
        /// topology-crossing Undo's full-rebuild path) keeps calling the real
        /// RecalculateBounds() - already-infrequent full-mesh operations that don't need this.
        private void ExpandBoundsLocal(List<int> dirtyVertices)
        {
            if (dirtyVertices.Count == 0) return;
            Bounds b = _mesh.bounds;
            for (int k = 0; k < dirtyVertices.Count; k++)
                b.Encapsulate(_workingVertices[dirtyVertices[k]]);
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
            // Copied into a concrete List once, and every step below iterates THAT. Callers pass
            // a HashSet (the brushes' per-frame dirty set) or an int[] (undo's RestoreDelta), so
            // walking the parameter directly means an interface-dispatched enumerator per step -
            // and for the HashSet case a boxed one, i.e. a fresh heap allocation per step per
            // frame of a held stroke. Five such walks became one.
            _dirtyVertexList.Clear();
            foreach (int vi in dirtyVertices) _dirtyVertexList.Add(vi);

            BuildAffectedSet(_dirtyVertexList);
            RecomputeNormalsLocal();
            ExpandBoundsLocal(_dirtyVertexList);

            // Re-bucket the moved vertices in the vertex index so it stays exact for the rest
            // of this stroke and for whatever queries it next (mask painting in particular,
            // which reuses whatever index the last sculpt stroke left behind). Without this
            // the index only ever tolerated one cell of drift, and anything past that dropped
            // out of every future candidate list - see VertexSpatialGrid's class remarks for
            // the artifacts that caused.
            if (_spatialGrid != null && _spatialGrid.VertexCount == _workingVertices.Length)
                _spatialGrid.UpdateVertices(_dirtyVertexList);

            if (_triangleGrid != null && _dirtyVertexList.Count > 0)
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
                    for (int k = 0; k < _dirtyVertexList.Count; k++)
                    {
                        int[] incident = _vertexTriangles[_dirtyVertexList[k]];
                        for (int i = 0; i < incident.Length; i++) _dirtyTriangleScratch.Add(incident[i]);
                    }
                    _triangleGrid.UpdateTriangles(_dirtyTriangleScratch, _workingVertices, _workingTriangles);
                }
            }

            RecomputeCavityLocal();

            // Replaces the full _mesh.vertices=/.colors= reassignment (and the .normals=
            // assignment removed above) with a compute-shader scatter write scoped to just the
            // affected vertices - see GpuVertexScatter remarks. _dirtyNormalScratch is exactly
            // that "dirty ∪ neighbors" set (built once by BuildAffectedSet above and shared with
            // both the normal and cavity passes) - position is redundant-but-harmless for
            // neighbor-only entries whose position didn't change, only their normal/cavity
            // color did.
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
            // Every caller of the full recompute (Awake / Remesh / ReplaceMesh) is exactly the
            // case where the object's size can have changed, so the scale is refreshed here
            // rather than at each of those call sites.
            UpdateCavityLengthScale();
            EnsureAdjacency();
            EnsureCavityBuffers();
            // Two passes, because the encode step blurs across neighbours: every raw value has
            // to exist before any of them is read.
            double sum = 0.0; // double, not float - this accumulates millions of terms
            for (int i = 0; i < _workingVertices.Length; i++)
            {
                _cavityRaw[i] = CurvatureAt(i);
                sum += _cavityRaw[i];
            }
            // The DC term EncodeCavityAt subtracts. Computed only on a full recompute, so a
            // stroke never shifts the whole mesh's tint out from under itself - a brush changes
            // the average curvature of a whole object negligibly, and a mean that drifted every
            // frame would make untouched geometry flicker.
            _cavityMean = _workingVertices.Length > 0 ? (float)(sum / _workingVertices.Length) : 0f;

            for (int i = 0; i < _workingVertices.Length; i++) EncodeCavityAt(i);
        }

        private float _cavityMean;

        /// Rebuild-if-null, the same treatment _adjacency/_triangleGrid/_gpuScatter get: a
        /// mid-Play script recompile triggers a domain reload that does not preserve these
        /// caches, and a stroke immediately afterward would otherwise NullReference. Also covers
        /// a length mismatch, which would mean the buffers survived a topology change they
        /// should not have.
        private void EnsureCavityBuffers()
        {
            int n = _workingVertices.Length;
            if (_cavityRaw == null || _cavityRaw.Length != n) _cavityRaw = new float[n];
            if (_cavityColors == null || _cavityColors.Length != n) _cavityColors = new Color[n];
        }

        // Reused across ApplyVerticesLocal calls - see its remarks for why the dirty set is
        // flattened into a concrete List before anything walks it.
        private readonly List<int> _dirtyVertexList = new List<int>();

        /// Same effect as RecomputeCavity(), but only for the given vertices plus their direct
        /// neighbors - a moved vertex changes not just its own cavity value but every
        /// neighbor's too, since their GetNeighborAverage includes it. Measured as the dominant
        /// remaining per-frame cost after the triangle-grid fix (this app's high-poly-brush-lag
        /// investigation) - see [[project_sculpting_application]] memory.
        private void RecomputeCavityLocal()
        {
            EnsureCavityBuffers();

            // Same two-pass split as the full recompute. The encode pass reads raw values one
            // ring beyond this set, which are left over from before the stroke and so are very
            // slightly stale - that only softens the blur at the footprint's rim by a fraction
            // of a vertex, and widening the recompute by another ring every frame would cost
            // far more than it could possibly be worth.
            foreach (int i in _dirtyNormalScratch) _cavityRaw[i] = CurvatureAt(i);
            foreach (int i in _dirtyNormalScratch) EncodeCavityAt(i);
        }

        /// Discrete mean curvature at a vertex, expressed relative to the object's own size:
        /// mean over neighbours of dot(direction to neighbour, normal) / |direction|^2, scaled
        /// by _cavityLengthScale. 0 on a flat surface, positive in a concave valley, negative
        /// on a convex ridge.
        ///
        /// Both divisions matter, and each fixes a different half of the same bug. The original
        /// version measured dot(neighbourAverage - vertex, normal) - a raw DISTANCE, which for a
        /// sphere of radius R with edge length e scales as e^2/R, so it collapsed toward zero as
        /// a mesh got denser. It had been tuned against a ~500-vertex sphere; on a 442k-vertex
        /// imported model (edges ~100x shorter) the identical shape produced values ~10,000x
        /// smaller, flattening the whole mesh to a uniform 0.5 and making the cavity controls
        /// look broken on exactly the dense models they matter most for.
        ///
        /// Dividing by |d| once gives dot(unit, normal), which still scales as e/R - measurably
        /// better but still density-dependent (verified: mean drifted 0.20 -> 0.44 -> 0.48
        /// across the same sphere at 515 / 10.7k / 91.5k vertices). Dividing by |d|^2 yields
        /// true curvature ~1/R, which is density-INdependent but now scales with object size;
        /// multiplying by the object's own extent cancels that too. The result is a pure shape
        /// measure: the same sphere reads the same at any tessellation and any scale, while a
        /// crease far sharper than the object is large saturates and pops, which is what cavity
        /// shading is for.
        private float CurvatureAt(int i)
        {
            EnsureAdjacency();
            int[] neighbors = _adjacency[i];
            if (neighbors.Length == 0) return 0f;

            Vector3 p = _workingVertices[i];
            Vector3 n = _workingNormals[i];

            // Accumulate first, divide ONCE - not dot(d,n)/|d|^2 per neighbour. Both give the
            // same answer on a regular mesh, but the per-edge form divides by each individual
            // edge length, so one unusually short edge produces a huge term. Surface Nets output
            // is full of those (its one-vertex-per-cell placement puts neighbours at wildly
            // varying distances), and the per-edge version turned that into visible speckle:
            // measured stdev 0.19 on a remeshed sphere against 0.007 on the authored one, with
            // values pinned at both 0 and 1. Averaging the offsets and the squared lengths
            // separately keeps the same curvature estimate while letting a stray short edge
            // barely move it.
            Vector3 offsetSum = Vector3.zero;
            float sqrLenSum = 0f;
            int counted = 0;
            for (int k = 0; k < neighbors.Length; k++)
            {
                Vector3 d = _workingVertices[neighbors[k]] - p;
                float sqrLen = d.sqrMagnitude;
                // Skip coincident vertices - welded/degenerate geometry does occur, and a NaN
                // here would propagate into the vertex colours and the rendered mesh.
                if (sqrLen < 1e-18f) continue;
                offsetSum += d;
                sqrLenSum += sqrLen;
                counted++;
            }
            if (counted == 0 || sqrLenSum <= 0f) return 0f;

            // dot(meanOffset, normal) has units of length; dividing by the mean SQUARED edge
            // length gives 1/length (true curvature); multiplying by the object's extent makes
            // it dimensionless. No square roots anywhere on this path, which matters because it
            // runs per touched vertex on every stroke.
            float meanSqrLen = sqrLenSum / counted;
            return Vector3.Dot(offsetSum / counted, n) / meanSqrLen * _cavityLengthScale;
        }

        /// Characteristic size of the object in LOCAL space, used to make CurvatureAt's true
        /// curvature (units of 1/length) dimensionless. Refreshed on a full recompute only -
        /// Awake, Remesh and ReplaceMesh - not per stroke: a brush changes the silhouette far
        /// too little to be worth an O(n) bounds pass every frame, and a cavity tint that
        /// subtly rescaled itself mid-stroke would read as flicker.
        private float _cavityLengthScale = 1f;

        private void UpdateCavityLengthScale()
        {
            if (_workingVertices == null || _workingVertices.Length == 0) { _cavityLengthScale = 1f; return; }

            Vector3 min = _workingVertices[0], max = _workingVertices[0];
            for (int i = 1; i < _workingVertices.Length; i++)
            {
                min = Vector3.Min(min, _workingVertices[i]);
                max = Vector3.Max(max, _workingVertices[i]);
            }
            Vector3 extents = (max - min) * 0.5f;
            _cavityLengthScale = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
            if (_cavityLengthScale < 1e-6f) _cavityLengthScale = 1f;
        }

        /// Turns raw curvature into the encoded 0..1 vertex-colour value, blurring across the
        /// vertex's one-ring on the way.
        ///
        /// The blur is not cosmetic polish - without it the measure is unusable on remeshed
        /// geometry. Surface Nets places one vertex per grid cell, so its output is genuinely
        /// bumpy at the cell scale, and true curvature (which is what CurvatureAt now reports)
        /// faithfully reports that bumpiness as very high: measured stdev 0.185 with 21% of
        /// vertices pinned at 0 or 1 on a remeshed sphere, against 0.007 on the same shape as
        /// authored. That reads as speckle rather than shading. Averaging over the one-ring
        /// suppresses per-vertex noise while leaving real creases - which span many vertices -
        /// essentially untouched.
        private void EncodeCavityAt(int i)
        {
            int[] neighbors = _adjacency[i];
            float sum = _cavityRaw[i];
            for (int k = 0; k < neighbors.Length; k++) sum += _cavityRaw[neighbors[k]];
            float smoothed = sum / (neighbors.Length + 1);

            // Subtracting the mesh-wide mean makes this a high-pass of curvature, which is what
            // "cavity" actually means: tint where the surface departs from its own overall
            // curvature, not wherever it is curved at all. Without it every convex object is
            // uniformly peak-tinted - a plain sphere measured a flat 0.199 across its whole
            // surface, which the shader renders as a solid peak colour rather than the neutral
            // it should be. Now a smooth ball sits at ~0.5 (neutral), while a crease or ridge,
            // whose curvature departs sharply from the body it sits on, still swings hard.
            float normalized = Mathf.Clamp((smoothed - _cavityMean) * CavitySensitivity, -1f, 1f);
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
                RecordMaskBeforeIfNeeded(i);
                _mask[i] = Mathf.Clamp01(_mask[i] + amount * weight);

                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
                _paintMaskScratch.Add(i);
            }
            MaskVersion++;

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
            // Payload-free undo entry: inverting is its own inverse, so there is nothing to
            // store. Worth the special case rather than reusing a mask delta - that would be a
            // whole-mesh array (8MB at a million vertices) for a button people press repeatedly
            // while dialling a selection in.
            _history.PushMaskInvert();
            EditHistory.RecordMeshEdit(this);
            InvertMaskWithoutUndo();
        }

        /// Restores a saved mask (see SceneSerializer). Deliberately pushes no undo entry,
        /// unlike InvertMask: its only caller is scene loading, which wipes history wholesale
        /// (EditHistory.Clear) because every object an entry could name was just destroyed.
        ///
        /// Mirrors InvertMask's body exactly: the
        /// mask is stored twice - in _mask (what the brushes read) and in _cavityColors[i].g
        /// (what the shader reads to tint protected areas) - so writing only _mask would restore
        /// the behaviour with no visual feedback at all. Silently ignores a length mismatch
        /// rather than throwing: that means the file's geometry and mask disagree, and a mesh
        /// with no mask is a far better failure than a half-applied one.
        public void SetMask(float[] mask)
        {
            if (mask == null || _mask == null || mask.Length != _mask.Length) return;
            for (int i = 0; i < _mask.Length; i++)
            {
                _mask[i] = Mathf.Clamp01(mask[i]);
                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
            }
            _mesh.colors = _cavityColors;
            MaskVersion++;
        }

        // Vertices whose mask a region op is about to clear, gathered once rather than
        // allocated per call - see ClearMask.
        private readonly List<int> _maskClearScratch = new List<int>();

        /// Sets a HARD mask value over an explicit set of vertices as one undoable step - what
        /// box/lasso masking uses (see RegionSelectTool), as against PaintMask's soft, ramped
        /// brush footprint. Routes through the same mask-stroke accumulator a painted stroke
        /// uses, so it lands in history in exactly the same shape and needs no new entry kind.
        /// Vertices already at `value` are skipped entirely: a second drag over the same region
        /// then records nothing instead of pushing an undo step that visibly does nothing.
        public void SetMaskOnVertices(IReadOnlyList<int> indices, float value)
        {
            if (_mask == null || indices == null || indices.Count == 0) return;
            value = Mathf.Clamp01(value);

            BeginMaskStroke();
            _paintMaskScratch.Clear();
            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                if (i < 0 || i >= _mask.Length || Mathf.Approximately(_mask[i], value)) continue;
                RecordMaskBeforeIfNeeded(i);
                _mask[i] = value;
                Color c = _cavityColors[i];
                c.g = value;
                _cavityColors[i] = c;
                _paintMaskScratch.Add(i);
            }
            if (_paintMaskScratch.Count == 0) return;

            MaskVersion++;
            UploadMaskColors();
            EndStrokeUndo();
        }

        /// Unmasks everything, as one undoable step. The complement of InvertMask for a mask
        /// built up by dragging regions, where "start over" is a far more common wish than
        /// "flip what I have".
        public void ClearMask()
        {
            if (_mask == null) return;
            _maskClearScratch.Clear();
            for (int i = 0; i < _mask.Length; i++)
                if (_mask[i] > 0f) _maskClearScratch.Add(i);
            SetMaskOnVertices(_maskClearScratch, 0f);
        }

        // Above this fraction of the mesh, a scatter write is the wrong tool: it stages one
        // index/position/normal/color entry per vertex into GPU buffers sized for the whole
        // mesh, which is strictly more work than the single full-array upload it exists to
        // avoid. Region ops routinely touch most of the mesh at once (masking everything but a
        // limb, clearing the mask), unlike the brush footprints ScatterDirty was written for.
        private const float FullUploadVertexFraction = 0.25f;

        /// Pushes whatever is in _paintMaskScratch to the GPU - the scatter path for a small
        /// footprint, a whole-mesh reupload past FullUploadVertexFraction. See
        /// SyncMeshFromWorkingArrays for why the big path reassigns positions and normals too
        /// rather than colors alone.
        private void UploadMaskColors()
        {
            if (_paintMaskScratch.Count > _workingVertices.Length * FullUploadVertexFraction)
            {
                SyncMeshFromWorkingArrays();
                return;
            }
            EnsureGpuScatter();
            _gpuScatter.ScatterDirty(_paintMaskScratch, _paintMaskScratch.Count, _workingVertices, _workingNormals, _cavityColors);
        }

        /// Re-uploads the three CPU arrays this class treats as authoritative into Unity's own
        /// managed mesh data, so the two agree again.
        ///
        /// Needed before ANY operation that makes Unity re-derive the mesh from its managed
        /// copy (SetTriangles, in practice), because ordinary sculpting writes moved vertices
        /// STRAIGHT into the GPU buffer via GpuVertexScatter and deliberately never syncs them
        /// back - so Unity's managed copy still holds the pre-sculpt shape. Letting it drive a
        /// reupload would silently revert the sculpt on screen (the same class of bug the
        /// [[feedback_unity_gpu_buffer_verification]] memory records for Remesh). Assigning
        /// vertices also recalculates the mesh bounds from the FULL vertex array, which is what
        /// keeps bounds correct while part of the index buffer is hidden.
        private void SyncMeshFromWorkingArrays()
        {
            _mesh.vertices = _workingVertices;
            _mesh.normals = _workingNormals;
            _mesh.colors = _cavityColors;
        }

        // ------------------------------------------------------------------ hidden geometry

        // Per-TRIANGLE visibility (true = hidden), the store behind box/lasso hide - see
        // RegionSelectTool. Per-triangle rather than per-vertex because that is what hiding a
        // region of a mesh actually means: a vertex on the border of the region is shared with
        // triangles that stay visible, so "is this vertex hidden" has no single right answer,
        // while "is this polygon drawn" always does. Null until something is first hidden, so
        // an ordinary sculpt pays nothing at all for this.
        private bool[] _hiddenTriangles;
        // Derived from _hiddenTriangles (see RefreshVisibility): a vertex is hidden only when
        // EVERY triangle using it is. This is what the brushes read, via QueryNear - border
        // vertices stay sculptable, so the visible edge of a partly-hidden mesh behaves like
        // ordinary surface rather than a frozen wall.
        private bool[] _hiddenVertices;
        // One-bool "is any of this in play" test, so the hot paths (QueryNear, RaycastMesh,
        // ApplyVertices) skip the visibility logic entirely in the normal case.
        private bool _anyHidden;
        // The visible-only index buffer handed to the Mesh, rebuilt in place on each visibility
        // change rather than reallocated.
        private int[] _visibleTriangleScratch;
        private readonly List<int> _visibilityChangedScratch = new List<int>();

        /// True while any part of this mesh is hidden - what the UI reads to enable Show All.
        public bool AnyHidden => _anyHidden;

        /// Bumped by every visibility change, for the same cheap "did anything move" polling
        /// MaskVersion serves.
        public int VisibilityVersion { get; private set; }

        /// Per-triangle hidden flags, or null when nothing is hidden. Handed to
        /// TriangleSpatialGrid.Raycast so every hit test passes through hidden geometry.
        public bool[] HiddenTriangles => _hiddenTriangles;

        /// Number of triangles in the CURRENT topology - what a caller building a per-triangle
        /// selection needs to size its own buffers.
        public int TriangleCount => _workingTriangles != null ? _workingTriangles.Length / 3 : 0;

        /// True if this vertex sits strictly inside a hidden region (see _hiddenVertices).
        public bool IsVertexHidden(int index) =>
            _anyHidden && _hiddenVertices != null && index >= 0 && index < _hiddenVertices.Length && _hiddenVertices[index];

        /// Hides (or shows) the given triangles as one undoable step.
        public void SetTrianglesHidden(IReadOnlyList<int> triangleIndices, bool hidden)
        {
            if (triangleIndices == null || triangleIndices.Count == 0 || _workingTriangles == null) return;
            EnsureVisibilityBuffer();

            _visibilityChangedScratch.Clear();
            for (int k = 0; k < triangleIndices.Count; k++)
            {
                int t = triangleIndices[k];
                if (t < 0 || t >= _hiddenTriangles.Length || _hiddenTriangles[t] == hidden) continue;
                _visibilityChangedScratch.Add(t);
            }
            CommitVisibilityChange(_visibilityChangedScratch, hidden);
        }

        /// Brings every hidden triangle back, as one undoable step. No-op (and no undo entry)
        /// when nothing is hidden.
        public void ShowAllGeometry()
        {
            if (!_anyHidden || _hiddenTriangles == null) return;

            _visibilityChangedScratch.Clear();
            for (int t = 0; t < _hiddenTriangles.Length; t++)
                if (_hiddenTriangles[t]) _visibilityChangedScratch.Add(t);
            CommitVisibilityChange(_visibilityChangedScratch, false);
        }

        /// Swaps hidden for visible across the whole mesh (ZBrush's Ctrl+Shift+I). Payload-free
        /// in history - inverting is its own inverse, exactly as InvertMask is, and at a couple
        /// of million triangles a stored delta for a button people press repeatedly while
        /// dialling in a selection would be megabytes a press.
        public void InvertVisibleGeometry()
        {
            if (!_anyHidden || _hiddenTriangles == null) return;
            _history.PushVisibilityInvert();
            EditHistory.RecordMeshEdit(this);
            InvertVisibilityWithoutUndo();
        }

        private void InvertVisibilityWithoutUndo()
        {
            EnsureVisibilityBuffer();
            for (int t = 0; t < _hiddenTriangles.Length; t++) _hiddenTriangles[t] = !_hiddenTriangles[t];
            RefreshVisibility();
        }

        private void EnsureVisibilityBuffer()
        {
            int triCount = TriangleCount;
            if (_hiddenTriangles == null || _hiddenTriangles.Length != triCount)
                _hiddenTriangles = new bool[triCount];
        }

        // Records the undo entry for `changed` (whose flags are still at their OLD values),
        // writes the new value, and rebuilds everything derived from visibility. Every hide/
        // show entry point funnels through here so none of them can forget the undo push, the
        // version bump or the index-buffer rebuild.
        private void CommitVisibilityChange(List<int> changed, bool newValue)
        {
            if (changed.Count == 0) return;

            int[] indices = changed.ToArray();
            var before = new bool[indices.Length];
            for (int k = 0; k < indices.Length; k++)
            {
                before[k] = _hiddenTriangles[indices[k]];
                _hiddenTriangles[indices[k]] = newValue;
            }

            _history.PushVisibilityDelta(indices, before);
            EditHistory.RecordMeshEdit(this);
            RefreshVisibility();
        }

        /// Undo/redo counterpart of CommitVisibilityChange - writes the stored flags back with
        /// no history push of its own (going through the public path would push a fresh entry
        /// from inside an undo, which is how an undo stack ends up unable to reach past the last
        /// thing you undid - same reasoning as InvertMaskWithoutUndo).
        private void RestoreVisibilityDelta(int[] indices, bool[] flags)
        {
            if (indices == null || flags == null) return;
            EnsureVisibilityBuffer();
            for (int k = 0; k < indices.Length; k++)
            {
                int t = indices[k];
                if (t < 0 || t >= _hiddenTriangles.Length) continue;
                _hiddenTriangles[t] = flags[k];
            }
            RefreshVisibility();
        }

        /// Rebuilds _anyHidden, _hiddenVertices and the mesh's index buffer from
        /// _hiddenTriangles. O(triangle count), paid once per hide/show gesture - never per
        /// frame.
        ///
        /// The vertex buffer keeps every vertex, hidden ones included: only the INDEX buffer
        /// shrinks. That keeps every vertex index in this class (undo deltas, mask, symmetry
        /// maps, the spatial grids) valid and stable across a hide, which is what lets hiding
        /// be a display-only concern that no other system has to know about.
        private void RefreshVisibility()
        {
            VisibilityVersion++;
            int triCount = TriangleCount;

            _anyHidden = false;
            if (_hiddenTriangles != null)
                for (int t = 0; t < triCount; t++)
                    if (_hiddenTriangles[t]) { _anyHidden = true; break; }

            if (!_anyHidden)
            {
                _hiddenTriangles = null;
                _hiddenVertices = null;
                SyncMeshFromWorkingArrays();
                _mesh.SetTriangles(_workingTriangles, 0, false);
                BindGpuScatter();
                return;
            }

            // Start every vertex hidden and clear it on the first VISIBLE triangle that uses
            // it - that is exactly the "hidden only if all its triangles are" rule, in one
            // pass. A vertex no triangle references at all stays flagged hidden, which is
            // honest: nothing draws it either way.
            if (_hiddenVertices == null || _hiddenVertices.Length != _workingVertices.Length)
                _hiddenVertices = new bool[_workingVertices.Length];
            for (int i = 0; i < _hiddenVertices.Length; i++) _hiddenVertices[i] = true;

            int visibleCount = 0;
            for (int t = 0; t < triCount; t++)
            {
                if (_hiddenTriangles[t]) continue;
                visibleCount++;
                int b = t * 3;
                _hiddenVertices[_workingTriangles[b]] = false;
                _hiddenVertices[_workingTriangles[b + 1]] = false;
                _hiddenVertices[_workingTriangles[b + 2]] = false;
            }

            if (_visibleTriangleScratch == null || _visibleTriangleScratch.Length != visibleCount * 3)
                _visibleTriangleScratch = new int[visibleCount * 3];
            int w = 0;
            for (int t = 0; t < triCount; t++)
            {
                if (_hiddenTriangles[t]) continue;
                int b = t * 3;
                _visibleTriangleScratch[w++] = _workingTriangles[b];
                _visibleTriangleScratch[w++] = _workingTriangles[b + 1];
                _visibleTriangleScratch[w++] = _workingTriangles[b + 2];
            }

            // calculateBounds:false, because SyncMeshFromWorkingArrays just recalculated them
            // over every vertex by assigning positions - letting SetTriangles redo it would
            // shrink the bounds to the visible part alone, and both the culling volume and
            // MeshBoundsFitInsideTriangleGrid's check want the whole mesh.
            SyncMeshFromWorkingArrays();
            _mesh.SetTriangles(_visibleTriangleScratch, 0, false);
            // The MeshCollider is deliberately NOT re-cooked here - it keeps the full mesh.
            // Nothing hit-tests through it (RaycastMesh goes to _triangleGrid, which does honor
            // visibility), and re-cooking runs into tens of milliseconds at high polycounts,
            // which would turn every hide drag into a visible hitch for no observable gain.
            BindGpuScatter();
        }

        // A topology change (Remesh/Join/an undo that crosses one) leaves the old per-triangle
        // flags describing triangles that no longer exist, so visibility starts fresh - the same
        // choice, for the same reason, that the mask already makes at those points.
        private void ResetVisibility()
        {
            _hiddenTriangles = null;
            _hiddenVertices = null;
            _visibleTriangleScratch = null;
            _anyHidden = false;
            VisibilityVersion++;
        }

        /// True if anything at all is masked - what TransformGizmo checks to decide whether a
        /// Transpose/Scale drag should move the whole object's Transform (nothing masked, the
        /// original behaviour) or deform the vertices around the frozen masked region instead
        /// (see BeginMaskedTransform). Early-outs on the first masked vertex rather than
        /// scanning the whole array, since it's called on every gizmo mouse-press.
        public bool HasMask
        {
            get
            {
                if (_mask == null) return false;
                for (int i = 0; i < _mask.Length; i++)
                    if (_mask[i] > 0.001f) return true;
                return false;
            }
        }

        // Pre-drag vertex positions for a masked Transpose/Scale drag. Every frame of the drag
        // re-derives the whole result from THESE rather than compounding onto last frame's
        // output - compounding a per-frame delta would let rounding drift accumulate over a
        // long drag, and (worse) makes dragging back to the start not actually return to the
        // start. Non-null exactly while such a drag is in progress; see BeginMaskedTransform.
        private Vector3[] _maskedTransformBase;

        /// Starts a mask-aware whole-object transform: instead of moving the Transform (which
        /// would drag the masked region along with everything else), the drag deforms the
        /// vertex buffer, holding fully-masked vertices exactly where they are and blending
        /// smoothly through partially-masked ones. This is what makes "mask the torso, then
        /// Transpose-drag" pull a limb out of the surface - ZBrush's core Transpose-with-mask
        /// behaviour - rather than sliding the whole mesh sideways.
        ///
        /// Returns false (and starts nothing) when nothing is masked, so the caller can fall
        /// back to the plain Transform drag - with no mask, deforming every vertex by the same
        /// matrix and moving the Transform are visually identical, and the Transform is both
        /// free and undoable by simply dragging back.
        ///
        /// Records undo up front for every vertex the drag can touch (mask &lt; 1), reusing the
        /// ordinary stroke-delta accumulator - at drag start _workingVertices still holds the
        /// pre-drag values, which is exactly what RecordUndoBeforeIfNeeded captures.
        public bool BeginMaskedTransform()
        {
            if (_workingVertices == null || _mask == null || !HasMask) return false;

            _maskedTransformBase = (Vector3[])_workingVertices.Clone();

            BeginStrokeUndo();
            for (int i = 0; i < _mask.Length; i++)
                if (_mask[i] < 0.999f) RecordUndoBeforeIfNeeded(i);

            return true;
        }

        /// Applies one frame of a masked transform drag. localDelta is the drag's accumulated
        /// transform expressed in THIS object's local space (the gizmo builds it there - the
        /// object's own origin is the gizmo pivot, so a rotation/scale about the pivot is just
        /// a rotation/scale about local zero). Per-vertex weight is 1 - mask, so a fully masked
        /// vertex is pinned and a half-masked one travels half as far, which is what gives the
        /// pulled limb a smooth root instead of a torn ring.
        public void ApplyMaskedTransform(Matrix4x4 localDelta)
        {
            if (_maskedTransformBase == null) return;

            for (int i = 0; i < _workingVertices.Length; i++)
            {
                Vector3 basePos = _maskedTransformBase[i];
                float weight = 1f - _mask[i];
                if (weight <= 0f) { _workingVertices[i] = basePos; continue; }

                Vector3 moved = localDelta.MultiplyPoint3x4(basePos);
                _workingVertices[i] = weight >= 1f ? moved : Vector3.LerpUnclamped(basePos, moved, weight);
            }

            // Cheap path - see ApplyVertices(bool). EndMaskedTransform does the full one.
            ApplyVertices(false);
        }

        /// Ends a masked transform drag: one full ApplyVertices so the triangle-raycast grid,
        /// cavity tint and collider catch up with the deformed surface, then commits the undo
        /// entry BeginMaskedTransform opened.
        public void EndMaskedTransform()
        {
            if (_maskedTransformBase == null) return;
            _maskedTransformBase = null;

            ApplyVertices();
            EndStrokeUndo();

            if (_meshCollider != null)
            {
                _meshCollider.sharedMesh = null;
                _meshCollider.sharedMesh = _mesh;
            }
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
            EditHistory.RecordMeshEdit(this);
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

        // Slot into _strokeDeltaBefore per vertex, or -1 for "not touched this stroke". Turns
        // the undo accumulator above into an O(1)-readable record of where the surface was when
        // this stroke began, which is what StrokeStartPosition serves - see its remarks for why
        // a brush needs that. _strokeRecordedIndices already answers "was it touched", but not
        // "where was it", and a HashSet lookup plus a linear scan of the parallel lists would be
        // far too slow for something read once per candidate per dab.
        private int[] _strokeRecordSlot;

        /// Where a vertex was when the CURRENT stroke started, or its live position if this
        /// stroke hasn't moved it yet (which is the same thing - an untouched vertex is still
        /// exactly where the stroke found it). Lets a brush measure against the surface it began
        /// with rather than the surface its own earlier dabs already deposited; see
        /// SculptController's Clay area-plane, which would otherwise chase its own output.
        public Vector3 StrokeStartPosition(int index)
        {
            if (_strokeRecordSlot == null || _strokeRecordSlot.Length != _workingVertices.Length)
                return _workingVertices[index];
            int slot = _strokeRecordSlot[index];
            return slot >= 0 ? _strokeDeltaBefore[slot] : _workingVertices[index];
        }

        // The mask equivalent of the three lists above, filled by RecordMaskBeforeIfNeeded and
        // committed by the same EndStrokeUndo. No slot array to go with it: nothing needs to read
        // "what was the mask here when this stroke started" mid-stroke the way Clay's area-plane
        // needs StrokeStartPosition for geometry.
        private readonly List<int> _maskStrokeIndices = new List<int>();
        private readonly List<float> _maskStrokeBefore = new List<float>();
        private readonly HashSet<int> _maskRecordedIndices = new HashSet<int>();

        /// Call once on stroke start (mouse-press) to clear the previous stroke's accumulator.
        public void BeginStrokeUndo()
        {
            ReleaseStrokeSlots();
            _strokeDeltaIndices.Clear();
            _strokeDeltaBefore.Clear();
            _strokeRecordedIndices.Clear();
        }

        /// The mask-paint equivalent, called on mouse-press in mask mode. Separate from
        /// BeginStrokeUndo because mask painting takes its own path through SculptController and
        /// never reaches that one - the two modes are mutually exclusive, so exactly one
        /// accumulator is ever live at a time.
        public void BeginMaskStroke()
        {
            _maskStrokeIndices.Clear();
            _maskStrokeBefore.Clear();
            _maskRecordedIndices.Clear();
        }

        /// Call from PaintMask BEFORE overwriting _mask[index], so the FIRST touch during this
        /// stroke captures the true pre-stroke value - mask paint ramps a vertex over many frames
        /// of a held drag, and recording every frame would make one undo press step back a single
        /// frame's worth of paint instead of the whole stroke.
        private void RecordMaskBeforeIfNeeded(int index)
        {
            if (!_maskRecordedIndices.Add(index)) return;
            _maskStrokeIndices.Add(index);
            _maskStrokeBefore.Add(_mask[index]);
        }

        /// Resets only the slots this stroke actually used, rather than refilling the whole
        /// per-vertex array with -1 on every stroke - O(touched) instead of O(vertex count),
        /// which matters because a stroke can be a click that touches nothing at all.
        private void ReleaseStrokeSlots()
        {
            if (_strokeRecordSlot == null) return;
            for (int k = 0; k < _strokeDeltaIndices.Count; k++)
            {
                int vi = _strokeDeltaIndices[k];
                if (vi >= 0 && vi < _strokeRecordSlot.Length) _strokeRecordSlot[vi] = -1;
            }
        }

        /// Call from a brush's per-candidate write site, BEFORE overwriting
        /// _workingVertices[index], so the FIRST touch during this stroke captures the true
        /// pre-stroke value - a vertex touched across multiple frames of the same held stroke
        /// only records once (its value from before the very first touch, not the most recent).
        public void RecordUndoBeforeIfNeeded(int index)
        {
            if (_strokeRecordedIndices.Add(index))
            {
                // Reallocated (and reset) whenever topology changed under us - a Remesh
                // mid-session leaves the old array sized to the old vertex count, and indexing
                // it would either throw or, worse, silently return another vertex's slot.
                if (_strokeRecordSlot == null || _strokeRecordSlot.Length != _workingVertices.Length)
                {
                    _strokeRecordSlot = new int[_workingVertices.Length];
                    for (int i = 0; i < _strokeRecordSlot.Length; i++) _strokeRecordSlot[i] = -1;
                }

                _strokeRecordSlot[index] = _strokeDeltaIndices.Count;
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
            // Commits whichever accumulator actually ran. In practice never both - sculpting and
            // mask painting are separate input modes - but handling them independently means the
            // one call site SculptController already has (HandleStrokeEndCommit, which fires on
            // every mouse release regardless of mode) covers both without knowing which is live.
            if (_strokeDeltaIndices.Count > 0)
            {
                _history.PushVertexDelta(_strokeDeltaIndices.ToArray(), _strokeDeltaBefore.ToArray());
                EditHistory.RecordMeshEdit(this);
                ReleaseStrokeSlots();
                _strokeDeltaIndices.Clear();
                _strokeDeltaBefore.Clear();
                _strokeRecordedIndices.Clear();
            }

            if (_maskStrokeIndices.Count > 0)
            {
                _history.PushMaskDelta(_maskStrokeIndices.ToArray(), _maskStrokeBefore.ToArray());
                EditHistory.RecordMeshEdit(this);
                _maskStrokeIndices.Clear();
                _maskStrokeBefore.Clear();
                _maskRecordedIndices.Clear();
            }
        }

        /// Steps this object's own history back one entry. Called by EditHistory, which owns the
        /// decision of WHICH object to step - never call it directly, or undo stops following the
        /// order the edits actually happened in. Returns false when this object has nothing left
        /// to undo, which tells EditHistory to skip this step and try the one before it.
        public bool ApplyUndoStep()
        {
            if (!_history.TryUndo(ReadVertex, ReadMask, ReadVisibility, CaptureFull, out SculptHistory.Restore restore)) return false;
            ApplyRestore(restore);
            return true;
        }

        public bool ApplyRedoStep()
        {
            if (!_history.TryRedo(ReadVertex, ReadMask, ReadVisibility, CaptureFull, out SculptHistory.Restore restore)) return false;
            ApplyRestore(restore);
            return true;
        }

        private Vector3 ReadVertex(int index) => _workingVertices[index];
        private float ReadMask(int index) => _mask[index];
        // Null _hiddenTriangles means nothing is hidden, so every triangle reads back visible -
        // which is exactly right for the reciprocal of an entry that is about to re-hide them.
        private bool ReadVisibility(int triangleIndex) =>
            _hiddenTriangles != null && triangleIndex >= 0 && triangleIndex < _hiddenTriangles.Length
            && _hiddenTriangles[triangleIndex];

        private void CaptureFull(out Vector3[] vertices, out int[] triangles)
        {
            vertices = (Vector3[])_workingVertices.Clone();
            triangles = _mesh.triangles;
        }

        private void ApplyRestore(SculptHistory.Restore restore)
        {
            switch (restore.Kind)
            {
                case SculptHistory.EntryKind.Full:
                    RestoreSnapshot(restore.FullVertices, restore.FullTriangles);
                    break;
                case SculptHistory.EntryKind.VertexDelta:
                    RestoreDelta(restore.Indices, restore.Positions);
                    break;
                case SculptHistory.EntryKind.MaskDelta:
                    RestoreMaskDelta(restore.Indices, restore.MaskValues);
                    break;
                case SculptHistory.EntryKind.MaskInvert:
                    InvertMaskWithoutUndo();
                    break;
                case SculptHistory.EntryKind.VisibilityDelta:
                    RestoreVisibilityDelta(restore.Indices, restore.Flags);
                    break;
                case SculptHistory.EntryKind.VisibilityInvert:
                    InvertVisibilityWithoutUndo();
                    break;
            }
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

        /// Mask counterpart of RestoreDelta. Writes both places the mask is stored - _mask, which
        /// the brushes read, and _cavityColors[i].g, which the shader reads - then scatters just
        /// those vertices to the GPU, the same footprint-scoped upload PaintMask itself uses.
        ///
        /// Bounds-checked per index rather than trusting the entry: a Remesh between painting the
        /// mask and undoing it resizes _mask (see RestoreSnapshot), and while the full snapshot
        /// for that Remesh is a NEWER entry and so is always undone first, restoring stale indices
        /// into a shorter array would be an exception rather than a visible mistake, so it is
        /// worth the cheap guard.
        private void RestoreMaskDelta(int[] indices, float[] values)
        {
            if (_mask == null) return;

            _paintMaskScratch.Clear();
            for (int k = 0; k < indices.Length; k++)
            {
                int i = indices[k];
                if (i < 0 || i >= _mask.Length) continue;
                _mask[i] = Mathf.Clamp01(values[k]);
                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
                _paintMaskScratch.Add(i);
            }
            MaskVersion++;

            EnsureGpuScatter();
            _gpuScatter.ScatterDirty(_paintMaskScratch, _paintMaskScratch.Count, _workingVertices, _workingNormals, _cavityColors);
        }

        /// The body of InvertMask without the history push - what undoing (or redoing) a
        /// MaskInvert entry runs. Going back through InvertMask itself would push a fresh entry
        /// from inside an undo, which is how an undo stack ends up unable to reach past the last
        /// thing you undid.
        private void InvertMaskWithoutUndo()
        {
            for (int i = 0; i < _mask.Length; i++)
            {
                _mask[i] = 1f - _mask[i];
                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
            }
            _mesh.colors = _cavityColors;
            MaskVersion++;
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
            _cavityRaw = new float[_workingVertices.Length];
            _mask = new float[_workingVertices.Length];
            // The new topology has no mapping onto the old mask, so it starts blank - which is
            // itself a mask change any watcher needs to hear about (a live extract preview built
            // from the pre-undo mask is describing geometry that no longer exists). Matches what
            // ReplaceMesh already does for the same reason.
            MaskVersion++;
            ResetVisibility();
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
        /// Hidden vertices are dropped from the result, which is what makes hidden geometry
        /// un-sculptable: every brush, the mask brush and SelectGrab all reach the mesh through
        /// this one method, so filtering here covers all of them at once and cannot be forgotten
        /// by a brush added later. Compacts the grid's own reused buffer in place rather than
        /// allocating a filtered copy per call.
        public List<int> QueryNear(Vector3 localPoint, float radius)
        {
            if (_spatialGrid == null) RebuildSpatialIndex(Mathf.Max(radius * 0.5f, 0.01f));
            List<int> candidates = _spatialGrid.Query(localPoint, radius);
            if (!_anyHidden || _hiddenVertices == null) return candidates;

            int w = 0;
            for (int k = 0; k < candidates.Count; k++)
            {
                int i = candidates[k];
                if (i >= 0 && i < _hiddenVertices.Length && _hiddenVertices[i]) continue;
                candidates[w++] = i;
            }
            candidates.RemoveRange(w, candidates.Count - w);
            return candidates;
        }

        /// Selects every vertex within radius of a local-space point, weighted by
        /// smoothstep falloff, so the same region keeps moving together for the rest of a
        /// drag - even once later deltas are queried against a point far outside this
        /// radius. Returns an invalid (empty) selection if nothing was in range.
        /// frontFacingOnly/cameraLocalPos gate the grab the same way every other brush's
        /// footprint is gated (see SculptController.FrontFacingWeight) - a vertex whose own
        /// normal faces away from the camera never enters the selection at all, so a Move drag
        /// on one side of a thin fold can't also drag the far side along with it.
        public GrabSelection SelectGrab(Vector3 localPoint, float radius, bool frontFacingOnly, Vector3 cameraLocalPos)
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
                if (frontFacingOnly && Vector3.Dot(_workingNormals[i], cameraLocalPos - _workingVertices[i]) <= 0f) continue;
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

        // ------------------------------------------------------------------------ Pose brush

        /// A temporary FK-style chain built once at Pose-drag start (see SelectPose) and
        /// re-evaluated every frame of the drag (see ApplyPoseDelta) - the "simple rig" the
        /// Pose brush poses with. Nothing here persists past one stroke: it lives only in the
        /// caller's local variables (SculptController), and the vertex buffer is the only thing
        /// left once the drag ends. Immutable/struct for the same reason as GrabSelection - one
        /// per mirrored brush instance, dragged independently in the same frame.
        public readonly struct PoseSelection
        {
            public readonly int[] Indices;
            /// Per-vertex 0..1: how much of the drag's rotation this vertex inherits. 0 at the
            /// anchor (mask boundary), ramping to 1 by the point the user actually grabbed -
            /// see SelectPose's grabT remarks - and staying at 1 beyond it, so anything past
            /// your grip follows rigidly rather than fading back out toward the free end.
            public readonly float[] RotationWeight;
            /// Local-space pivot the whole chain rotates around - the centroid of the anchor
            /// (mask) boundary, not the click point. Never moves.
            public readonly Vector3 RootPosition;
            /// Where the grabbed vertex was, in local space, at selection time - ApplyPoseDelta
            /// measures the drag's rotation FROM this, not from wherever it's ended up mid-drag.
            public readonly Vector3 OriginalGrabPosition;
            /// The root->tip geodesic shortest path, local space, at selection time - purely for
            /// the guide-line visual (see SculptController.UpdatePoseChainVisual); nothing in
            /// ApplyPoseDelta reads this. Null/empty is a valid "don't draw anything" state, not
            /// an error - IsValid doesn't depend on it.
            public readonly Vector3[] ChainPoints;

            public PoseSelection(int[] indices, float[] rotationWeight, Vector3 rootPosition,
                Vector3 originalGrabPosition, Vector3[] chainPoints)
            {
                Indices = indices;
                RotationWeight = rotationWeight;
                RootPosition = rootPosition;
                OriginalGrabPosition = originalGrabPosition;
                ChainPoints = chainPoints;
            }

            public bool IsValid => Indices != null && Indices.Length > 0;
        }

        // Masked vertices ARE the anchor for Pose (opposite of the ApplyMaskedTransform-style
        // "masked = frozen" reading everywhere else being flipped in polarity - it's the SAME
        // reading, mask means protected, Pose just puts the anchor where the mask is instead of
        // painting the limb itself). Vertex-level threshold for island/boundary membership,
        // which needs a hard yes/no unlike the continuous (1-mask) weighting used once inside
        // the selected island - see ApplyPoseDelta.
        private const float PoseMaskThreshold = 0.5f;

        // Hard backstop against the accidental-huge-island case: little or nothing masked, so a
        // click flood-fills most of the mesh. Cheap to hit (a click that misses this is rare;
        // the whole point is that it should never make the Editor stutter when it does) - see
        // SelectPose's early-out when this is exceeded.
        private const int PoseMaxIslandVertices = 50000;

        // brushRadius -> geodesic reach, in world/local units - 1 for the simplest, most
        // predictable "brush size directly sets how far up the limb the line/influence reaches"
        // mapping (see SelectPose's effectiveReach remarks). A single named multiplier rather
        // than a bare 1 so it's a one-line tune if that reads as too short/long in practice
        // rather than a hunt through the method.
        private const float PoseReachScale = 1f;

        /// Builds the temporary chain for a Pose Brush drag: flood-fills the connected UNMASKED
        /// island touching localClickPoint, finds its anchor (every island vertex adjacent to a
        /// masked one - the "socket" where the limb attaches to whatever's frozen), and measures
        /// every island vertex's geodesic distance from that anchor via one multi-source
        /// Dijkstra (real edge lengths, not hop count - needed so brushRadius, a world-unit
        /// value, can be compared against it directly for the reach cap below). Returns an
        /// invalid selection if the click missed the mesh, landed on masked geometry, or the
        /// island has no anchor at all (nothing masked/reachable) - Pose has nothing sensible to
        /// pose against without one, same as clicking empty space does for Move. rigidity (0..1,
        /// see SculptController.PoseRigidity) controls how concentrated the weight ramp is near
        /// the anchor - see its own remarks for why 0 alone reads as weak on a long reach.
        /// segments (see SculptController.PoseSegments) divides the true anchor->true tip
        /// distance into that many joints - see segmentIndex's remarks for why a click doesn't
        /// always pivot from the true anchor.
        public PoseSelection SelectPose(Vector3 localClickPoint, float brushRadius, float rigidity, int segments)
        {
            if (_workingVertices == null || _mask == null) return default;

            int clickVertex = NearestVertexIndex(localClickPoint, brushRadius);
            if (clickVertex < 0 || _mask[clickVertex] > PoseMaskThreshold) return default;

            EnsureAdjacency();

            // Pass 1: flood-fill the connected unmasked island containing the click, collecting
            // every island vertex that touches a masked neighbor as we go - that set becomes the
            // multi-source seed for pass 2 below. Topology only (no distances needed yet), so a
            // plain BFS is enough here regardless of the Dijkstra pass 2 needs.
            var island = new List<int>();
            var boundary = new List<int>();
            var visited = new HashSet<int> { clickVertex };
            var floodQueue = new Queue<int>();
            floodQueue.Enqueue(clickVertex);
            while (floodQueue.Count > 0)
            {
                if (island.Count >= PoseMaxIslandVertices) break; // see PoseMaxIslandVertices
                int v = floodQueue.Dequeue();
                island.Add(v);
                int[] neighbors = _adjacency[v];
                bool touchesMasked = false;
                for (int k = 0; k < neighbors.Length; k++)
                {
                    int n = neighbors[k];
                    if (_mask[n] > PoseMaskThreshold) { touchesMasked = true; continue; }
                    if (visited.Add(n)) floodQueue.Enqueue(n);
                }
                if (touchesMasked) boundary.Add(v);
            }
            if (boundary.Count == 0) return default; // nothing masked/reachable to anchor against

            // Pass 2: multi-source Dijkstra from every boundary vertex at once (all start at
            // distance 0, regardless of which is nearest the click), edge-length distance
            // restricted to the same island. Bounded by PoseMaxIslandVertices only, not by
            // brushRadius - the click vertex must always be reached regardless of brush size
            // (see effectiveReach below), so there's no cheaper bound to apply up front without
            // already knowing how far away the click turns out to be. No predecessor tracking -
            // the distance field alone is enough for both the deformation weights AND the guide
            // line (see the bucket-and-centroid pass below); reconstructing the actual shortest-
            // path vertex CHAIN was tried first for the line and looked visibly zigzagged even
            // though it was genuinely the minimal-length path - a true shortest path on a mesh
            // graph hops between whichever edges are marginally shorter and has no reason to
            // trace a visually smooth curve, which a raw polyline through its vertices makes
            // obvious in a way the total length number never would.
            var dist = new Dictionary<int, float>();
            var heapVerts = new List<int>();
            var heapDist = new List<float>();
            foreach (int b in boundary)
            {
                dist[b] = 0f;
                heapVerts.Add(b);
                heapDist.Add(0f);
            }
            int processed = 0;
            while (heapVerts.Count > 0 && processed < PoseMaxIslandVertices)
            {
                // Pop the minimum - swap-with-last then sift down, standard array-heap removal.
                int v = heapVerts[0];
                float d = heapDist[0];
                int lastIdx = heapVerts.Count - 1;
                heapVerts[0] = heapVerts[lastIdx];
                heapDist[0] = heapDist[lastIdx];
                heapVerts.RemoveAt(lastIdx);
                heapDist.RemoveAt(lastIdx);
                int hi = 0;
                while (true)
                {
                    int l = 2 * hi + 1, r = 2 * hi + 2, smallest = hi;
                    if (l < heapVerts.Count && heapDist[l] < heapDist[smallest]) smallest = l;
                    if (r < heapVerts.Count && heapDist[r] < heapDist[smallest]) smallest = r;
                    if (smallest == hi) break;
                    (heapVerts[smallest], heapVerts[hi]) = (heapVerts[hi], heapVerts[smallest]);
                    (heapDist[smallest], heapDist[hi]) = (heapDist[hi], heapDist[smallest]);
                    hi = smallest;
                }

                if (d > dist[v] + 1e-6f) continue; // stale heap entry from an earlier, worse push
                processed++;

                int[] neighbors = _adjacency[v];
                for (int k = 0; k < neighbors.Length; k++)
                {
                    int n = neighbors[k];
                    if (!visited.Contains(n)) continue; // outside the island - never cross the mask boundary
                    float nd = d + Vector3.Distance(_workingVertices[v], _workingVertices[n]);
                    if (dist.TryGetValue(n, out float existing) && existing <= nd) continue;
                    dist[n] = nd;
                    // Push (not decrease-key) - simplest correct approach for a heap built from
                    // plain lists; the stale-entry check above skips the resulting duplicates
                    // cheaply rather than needing an indexed/decrease-key heap.
                    heapVerts.Add(n);
                    heapDist.Add(nd);
                    int ci = heapVerts.Count - 1;
                    while (ci > 0)
                    {
                        int parent = (ci - 1) / 2;
                        if (heapDist[parent] <= heapDist[ci]) break;
                        (heapVerts[parent], heapVerts[ci]) = (heapVerts[ci], heapVerts[parent]);
                        (heapDist[parent], heapDist[ci]) = (heapDist[ci], heapDist[parent]);
                        ci = parent;
                    }
                }
            }

            // True tip = the island vertex geodesically farthest from the anchor - the limb's
            // actual free end, independent of brush size or where along it the user clicked.
            int trueTip = clickVertex;
            float trueMaxDist = 0f;
            foreach (int v in island)
            {
                if (dist.TryGetValue(v, out float d) && d > trueMaxDist) { trueMaxDist = d; trueTip = v; }
            }
            if (trueMaxDist <= 1e-6f) return default; // the island IS the boundary - nothing to bend

            // clickDist is always present: the click vertex seeded pass 1, so it's always
            // reachable within the island regardless of PoseMaxIslandVertices.
            float clickDist = dist.TryGetValue(clickVertex, out float cd) ? cd : 0f;

            // segmentIndex/localRootDist: which of the `segments` evenly-spaced joints along
            // true anchor->true tip the click falls into, and the joint boundary just below it.
            // Without this, EVERY click anywhere on the limb pivoted from the same single point
            // (the true anchor) - user-reported this read as the tool "snapping to one area"
            // with no other places to grab from, unlike Blender's segmented chain. segmentIndex
            // 0 (a click in the first segment, nearest the true anchor) reduces this to exactly
            // the old single-root behavior - localRootDist is 0, so localRootPos below resolves
            // to the true boundary centroid rather than an approximated one.
            int clampedSegments = Mathf.Max(segments, 1);
            float segmentLength = trueMaxDist / clampedSegments;
            int segmentIndex = Mathf.Clamp(Mathf.FloorToInt(clickDist / Mathf.Max(segmentLength, 1e-6f)), 0, clampedSegments - 1);
            float localRootDist = segmentIndex * segmentLength;

            Vector3 rootPos;
            if (segmentIndex == 0)
            {
                rootPos = Vector3.zero;
                foreach (int b in boundary) rootPos += _workingVertices[b];
                rootPos /= boundary.Count;
            }
            else
            {
                // No mask/topology feature marks this joint - it's a computed distance, not a
                // real boundary - so its pivot is the CENTROID of every island vertex whose
                // distance-from-anchor falls in a band around it (a cross-section through the
                // limb at that point), not any single vertex, which could sit anywhere around
                // the limb's circumference rather than on its centerline.
                float band = segmentLength * 0.25f;
                Vector3 sum = Vector3.zero;
                int count = 0;
                foreach (int v in island)
                {
                    if (dist.TryGetValue(v, out float dv0) && Mathf.Abs(dv0 - localRootDist) <= band)
                    {
                        sum += _workingVertices[v];
                        count++;
                    }
                }
                if (count > 0)
                {
                    rootPos = sum / count;
                }
                else
                {
                    // Band caught nothing (sparse/irregular mesh at this particular distance) -
                    // fall back to the single closest vertex rather than leaving rootPos at the
                    // true anchor, which would silently undo the segment this click chose.
                    int nearest = clickVertex;
                    float bestDelta = float.MaxValue;
                    foreach (int v in island)
                    {
                        if (!dist.TryGetValue(v, out float dv0)) continue;
                        float delta = Mathf.Abs(dv0 - localRootDist);
                        if (delta < bestDelta) { bestDelta = delta; nearest = v; }
                    }
                    rootPos = _workingVertices[nearest];
                }
            }

            // Everything below here is measured from the LOCAL root (localRootDist), not the
            // true anchor - localClickDist/localMaxDist are what were plain clickDist/trueMaxDist
            // before segments existed, shifted by the joint boundary this click landed past.
            float localClickDist = clickDist - localRootDist;
            float localMaxDist = trueMaxDist - localRootDist;

            // effectiveReach is the actual point of brushRadius for Pose: how far PAST the click
            // the chain/line/influence extends, in the SAME units brushRadius is already in (see
            // UI's Brush Size slider) - "behave like Blender: brush size determines the reach".
            // Additive with localClickDist, not Max() against it - a click always produces a
            // usable pose regardless of brush size (effectiveReach >= localClickDist always,
            // since brushRadius can't be negative), but critically the reach still SCALES with
            // brush size no matter where you clicked, including right at the limb's own tip -
            // Max() against clickDist alone was tried first and silently ignored brushRadius
            // entirely whenever clickDist already exceeded it, which is most clicks. Clamp
            // against localMaxDist means cranking the brush up saturates at the limb's actual
            // tip rather than extrapolating past it.
            float effectiveReach = Mathf.Clamp(localClickDist + brushRadius * PoseReachScale, 0.0001f, localMaxDist);

            // grabT: where along the local root->effectiveReach chain the click landed, as a
            // 0..1 fraction. This is what makes "root stays stable, mid bends smoothly, tip
            // follows strongly" true without a separate falloff curve to tune - see
            // RotationWeight's remarks below. Approaches 1 as brushRadius shrinks toward 0 (the
            // click point IS essentially the tip - a tiny brush collapses the whole ramp into a
            // sharp bend right at the local root), and drops well below 1 for a large brush (the
            // ramp keeps most of its length as smooth bend, with only a short rigid follow-
            // through past the click). Floored above 0 (rather than
            // clamped at 0) so a click landing exactly ON the local root - localClickDist == 0 -
            // can't divide dv/grabT by zero for other vertices right at that root below, which
            // produces NaN (0/0), not a merely-wrong weight - see ClayFalloff's own edgeSoftness
            // floor for the same reasoning applied to a different divide-by-zero.
            float grabT = Mathf.Max(localClickDist / effectiveReach, 0.001f);

            // rampT: the actual width of the 0->1 weight transition, as a fraction of
            // effectiveReach - see poseRigidity's own remarks for why grabT alone (rigidity 0,
            // the transition spanning the ENTIRE local-root-to-click distance) reads as weak: on
            // a long reach, most of the chain only ever gets partial rotation weight, which is a
            // small fraction of an already-modest swing angle. Shrinking the transition toward
            // the local root (rigidity -> 1) means everything from shortly past it onward moves
            // with weight close to 1 - most of the reached limb actually follows the drag, with
            // the softness concentrated in a thin band right at the local root instead of
            // smeared across the whole reach. Floored the same way grabT is, for the same
            // divide-by-zero reason.
            float rampT = Mathf.Max(grabT * (1f - rigidity), 0.001f);

            // Final selection = island vertices between the local root and effectiveReach - this
            // is the brush-size cap AND the segment choice both actually taking effect: anything
            // on the anchor side of the local root (dv < localRootDist) stays completely fixed
            // for this gesture regardless of mask, and a smaller brush leaves more of the far
            // side untouched too, not just weighted toward 0.
            var indices = new List<int>(island.Count);
            var weights = new List<float>();
            int effectiveTip = clickVertex;
            float effectiveTipDist = -1f;
            foreach (int v in island)
            {
                if (!dist.TryGetValue(v, out float rawDist)) continue;
                float dv = rawDist - localRootDist;
                if (dv < 0f || dv > effectiveReach) continue;
                indices.Add(v);
                // Smoothstep rather than a linear ramp: root and tip both get a brief flat
                // plateau (root reads as genuinely pinned rather than "very slightly wobbly",
                // tip's rigid follow-through starts smoothly rather than with a visible kink at
                // the end of the transition) instead of a uniform mechanical gradient.
                weights.Add(Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(dv / effectiveReach / rampT)));
                if (dv > effectiveTipDist) { effectiveTipDist = dv; effectiveTip = v; }
            }

            // Guide-line visual (see PoseSelection.ChainPoints) - a straight line from the
            // anchor to the effective tip, Blender-style, not a curve traced along the limb's
            // surface. A curve following the actual shortest-path vertex chain was tried first
            // and looked visibly zigzagged (a true shortest path on a mesh graph hops between
            // whichever edges are marginally shorter - genuinely minimal total length, but no
            // reason to trace a smooth line); a straight segment through empty space sidesteps
            // that entirely and is what the reference actually shows.
            var chainPoints = new[] { rootPos, _workingVertices[effectiveTip] };

            return new PoseSelection(indices.ToArray(), weights.ToArray(), rootPos,
                _workingVertices[clickVertex], chainPoints);
        }

        /// Applies one frame of a Pose Brush drag. Every vertex is re-derived from
        /// StrokeStartPosition (where this stroke found it), not incrementally from last frame's
        /// output - same reasoning as ApplyMaskedTransform: avoids compounding rotation error
        /// over a long drag, and dragging back to where you started actually returns there.
        /// targetLocalPos is where the GRABBED point (not necessarily the anatomical tip -
        /// see PoseSelection.RotationWeight) should now be, in local space.
        public void ApplyPoseDelta(PoseSelection selection, Vector3 targetLocalPos)
        {
            if (!selection.IsValid) return;

            Vector3 fromDir = selection.OriginalGrabPosition - selection.RootPosition;
            Vector3 toDir = targetLocalPos - selection.RootPosition;
            if (fromDir.sqrMagnitude < 1e-10f || toDir.sqrMagnitude < 1e-10f) return; // degenerate - root and grab point coincide
            Quaternion swing = Quaternion.FromToRotation(fromDir, toDir);

            for (int k = 0; k < selection.Indices.Length; k++)
            {
                int i = selection.Indices[k];
                // (1 - mask[i]), not mask[i] - same polarity every other brush already uses
                // (masked = protected). A vertex right at the anchor boundary is fully masked
                // and stays exactly at its StrokeStartPosition; the soft edge of a feathered
                // mask blends smoothly into the pose rather than tearing at a hard seam.
                float maskWeight = 1f - _mask[i];
                float rotWeight = selection.RotationWeight[k];
                if (maskWeight <= 0f || rotWeight <= 0f) continue;

                Vector3 basePos = StrokeStartPosition(i);
                Quaternion vertexRotation = Quaternion.Slerp(Quaternion.identity, swing, rotWeight);
                Vector3 posed = selection.RootPosition + vertexRotation * (basePos - selection.RootPosition);
                Vector3 newPos = maskWeight >= 1f ? posed : Vector3.LerpUnclamped(basePos, posed, maskWeight);

                RecordUndoBeforeIfNeeded(i);
                _workingVertices[i] = newPos;
            }
        }

        /// Closest vertex to a local-space point within searchRadius, or -1 if none are in
        /// range. Only ever called once per Pose click (not per frame), so re-querying the
        /// spatial grid here rather than threading a candidate list through from the caller
        /// costs nothing worth avoiding.
        private int NearestVertexIndex(Vector3 localPoint, float searchRadius)
        {
            List<int> candidates = QueryNear(localPoint, Mathf.Max(searchRadius, 0.01f));
            int best = -1;
            float bestSqrDist = float.MaxValue;
            for (int k = 0; k < candidates.Count; k++)
            {
                int i = candidates[k];
                float sqrDist = (_workingVertices[i] - localPoint).sqrMagnitude;
                if (sqrDist < bestSqrDist) { bestSqrDist = sqrDist; best = i; }
            }
            return best;
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
            ReplaceMesh(MeshRemesher.Remesh(_workingVertices, _workingTriangles, resolution));
        }

        /// Swaps in an entirely new mesh (different topology/vertex count) and rebuilds every
        /// piece of derived state from it - adjacency, triangle-raycast grid, cavity/mask
        /// buffers, GPU scatter binding, collider. Extracted from Remesh()'s own tail so
        /// MeshJoiner can reuse the identical rebuild after Mesh.CombineMeshes without
        /// duplicating it. Same tradeoff Remesh() already accepted: drops whatever UVs the
        /// source mesh had (harmless - SculptPBR's Attributes struct has no TEXCOORD0 input).
        public void ReplaceMesh(Mesh newMesh)
        {
            newMesh.name = _mesh.name;
            newMesh.MarkDynamic();

            _mesh = newMesh;
            _meshFilter.mesh = _mesh;

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
            _cavityRaw = new float[_workingVertices.Length];
            _mask = new float[_workingVertices.Length];
            // The new topology has no mapping onto the old mask, so it starts blank - which is
            // itself a mask change any watcher needs to hear about (a live extract preview
            // built from the pre-remesh mask is describing geometry that no longer exists).
            MaskVersion++;
            ResetVisibility();
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
