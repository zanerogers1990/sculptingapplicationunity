using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// Owns a runtime-duplicated mesh so sculpting never touches the shared mesh asset.
    /// Brush raycasts hit-test directly against the live CPU vertex/triangle data via
    /// TriangleSpatialGrid rather than through a MeshCollider - see RaycastMesh/_triangleGrid.
    [RequireComponent(typeof(MeshFilter))]
    public partial class SculptableMesh : MonoBehaviour
    {
        // The MeshCollider is no longer on the brush hit-testing hot path (see RaycastMesh) -
        // kept optionally for any other system that might want a physics collider on the
        // sculpt, refreshed only on topology changes (Awake/Remesh/RestoreSnapshot), NOT every
        // ApplyVertices() call. Re-cooking it every frame during a held stroke used to be the
        // dominant per-frame cost by a wide margin (measured ~35ms/call at ~144k triangles,
        // ~95% of a brush-application frame, by timing each ApplyVertices() sub-step - and the
        // cost was the same whatever the brush size).
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
        // Direct-edge neighbours and incident triangles per vertex, derived from the current
        // mesh's triangles: neighbours drive every Laplacian (Smooth, relax, cavity), incident
        // triangles drive the normal recompute and the triangle grid's re-bucketing. Rebuilt
        // whenever topology changes (initial load, Remesh) - never touched by ordinary sculpting,
        // which only moves vertices. See MeshAdjacency for the flat layout and why it is flat.
        private MeshAdjacency _topology;

        // Pushes only the touched vertices' position/normal/color into the mesh's GPU vertex
        // buffer every ApplyVerticesLocal call, instead of Unity's Mesh.vertices/.normals/
        // .colors setters (which always reupload the WHOLE array regardless of footprint size -
        // see GpuVertexScatter's remarks). A plain C# class like _triangleGrid/_spatialGrid, so
        // it gets the same lazy-rebuild-if-null treatment for the mid-Play-recompile domain-
        // reload case (see EnsureGpuScatter) - it's never treated as a source of truth, only
        // ever (re)bound to whatever _mesh currently is.
        private GpuVertexScatter _gpuScatter;

        public Mesh Mesh => _mesh;

        /// The live vertex/normal/triangle buffers.
        ///
        /// THEIR .Length IS A CAPACITY, NOT A COUNT. VertexCount and TriangleCount are the
        /// authority; anything that walks these arrays has to bound itself by one of those, and
        /// anything that hands a whole array to code expecting an exact fit wants the Exact
        /// accessors below instead.
        ///
        /// In practice every path that replaces geometry hands over exact arrays, so capacity and
        /// count are equal today - the distinction is kept because it is what lets a buffer run
        /// ahead of what is in use without copying the whole mesh, and because every consumer in
        /// the app is already written to respect it.
        ///
        /// The spare tail is kept deliberately harmless rather than merely undefined - spare
        /// vertex slots hold a copy of vertex 0 and spare triangles are degenerate - so that a
        /// consumer reads something in-bounds and on-surface rather than garbage coordinates that
        /// would blow up a bounding box or an SDF.
        public Vector3[] Vertices => _workingVertices;
        public Vector3[] Normals => _workingNormals;
        public int[] Triangles => _workingTriangles;

        /// Vertices in use. Distinct from Vertices.Length - see its remarks.
        public int VertexCount => _vertexCount;

        /// Bumped whenever the positions or topology a raycast would test against may have
        /// changed - every apply (ApplyVertices, ApplyVerticesLocal) and every triangle-grid
        /// rebuild. The same cheap "did anything move" poll MaskVersion/VisibilityVersion serve:
        /// lets a caller that raycasts every frame (CameraOrbitController's depth probe) reuse
        /// its last answer while nothing it depends on has changed.
        public int GeometryVersion { get; private set; }

        // Vertices and triangle CORNERS actually in use. Everything at or past these is spare
        // capacity. Kept as plain fields (not derived from the arrays) because they are read on
        // the brush hot path and in every bounds guard in this class.
        private int _vertexCount;
        private int _cornerCount;

        /// The same buffers trimmed to exactly what is in use, for callers that hand a whole
        /// array to something with no count to go with it - Join, Boolean, Extract, Clone,
        /// Export, scene save. Returns the live array itself whenever capacity already equals
        /// count, so the common case costs nothing at all.
        public Vector3[] VerticesExact() => Exact(_workingVertices, _vertexCount);
        public Vector3[] NormalsExact() => Exact(_workingNormals, _vertexCount);
        public int[] TrianglesExact() => Exact(_workingTriangles, _cornerCount);
        public float[] MaskExact() => Exact(_mask, _vertexCount);

        private static T[] Exact<T>(T[] array, int count)
        {
            if (array == null || array.Length == count) return array;
            return CloneExact(array, count);
        }

        /// Exact's always-copy form, for callers that must OWN the result - undo snapshots above
        /// all, which are kept while the live buffer goes on being written.
        private static T[] CloneExact<T>(T[] array, int count)
        {
            if (array == null) return null;
            var copy = new T[count];
            Array.Copy(array, copy, count);
            return copy;
        }

        /// This object's live mirror copies (Nomad's Mirror), or null - set only by MirrorRepeater.
        /// The copies draw this very mesh through reflected transforms, so they need nothing from
        /// the edit paths: every change here is already a change there.
        public MirrorRepeater Repeater { get; internal set; }

        public bool Visible => _visible;

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_renderer != null) _renderer.enabled = visible;
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
            SetGeometryCounts(_workingVertices.Length, _workingTriangles.Length);

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
                ReseatCollider();
            }
        }

        /// PhysX's fast midphase (BVH34) has a known problem with meshes of more than 2^21
        /// triangles, and Unity warns on every such cook ("Source mesh has over 2,097,152 triangles
        /// in it, and is using the Fast Midphase option. This might cause certain collisions to not
        /// be detected correctly..."). A high-resolution remesh passes that easily, and this
        /// collider is not just decoration - SSphere attach raycasts it - so past the limit it
        /// cooks the way that warning recommends, without fast midphase.
        private const long FastMidphaseTriangleLimit = 1L << 21;

        // Switched off together past the limit. CookForFasterSimulation has to go with fast
        // midphase: on the older BVH33 midphase it is what makes cooking slow - measured on a
        // 2.53M-triangle remesh, 5.8 s with it against 0.76 s without, where the default BVH34 cook
        // took 0.80 s - and nothing simulates against this collider; it only answers raycasts.
        private const MeshColliderCookingOptions LargeMeshSuppressedOptions =
            MeshColliderCookingOptions.UseFastMidphase | MeshColliderCookingOptions.CookForFasterSimulation;

        // Which of those options ReseatCollider actually switched off, so dropping back under the
        // limit restores exactly them - leaving a collider configured without them alone.
        private MeshColliderCookingOptions _suppressedCookingOptions;

        /// (Re)cooks the collider against the current mesh - see FastMidphaseTriangleLimit. Counts
        /// the index buffer the collider actually cooks rather than _workingTriangles, since hidden
        /// geometry is left out of the former.
        private void ReseatCollider()
        {
            if (_meshCollider == null) return;

            long triangles = 0;
            for (int s = 0; s < _mesh.subMeshCount; s++) triangles += (long)_mesh.GetIndexCount(s) / 3;

            MeshColliderCookingOptions options = _meshCollider.cookingOptions;
            if (triangles > FastMidphaseTriangleLimit)
            {
                _suppressedCookingOptions |= options & LargeMeshSuppressedOptions;
                options &= ~LargeMeshSuppressedOptions;
            }
            else
            {
                options |= _suppressedCookingOptions;
                _suppressedCookingOptions = MeshColliderCookingOptions.None;
            }

            // Detached before the options change, so they only ever apply to one fresh cook of the
            // current mesh rather than to whatever the collider was still holding.
            _meshCollider.sharedMesh = null;
            _meshCollider.cookingOptions = options;
            _meshCollider.sharedMesh = _mesh;
        }

        /// Adds a SculptableMesh to `go` - whose MeshFilter must ALREADY be holding `source` -
        /// and then DESTROYS `source`.
        ///
        /// Awake instantiates its own copy of whatever sharedMesh it finds and repoints the
        /// filter at that copy, which leaves the original referenced by nothing. Unity never
        /// frees a Mesh on its own (see ReleaseReplacedMesh), so an object spawned from a mesh
        /// built at runtime used to strand that mesh for the rest of the session - one full
        /// vertex+index buffer per clone, mirror, extract, import and SSphere convert.
        ///
        /// ONLY for a source built specifically to seed this object - every caller here
        /// constructs one a few lines earlier and drops it. PrimitiveSpawner deliberately does
        /// NOT use this: GameObject.CreatePrimitive hands back Unity's built-in shared
        /// Sphere/Cube/Capsule asset, and destroying that would break every other user of it.
        public static SculptableMesh AddOwning(GameObject go, Mesh source)
        {
            // AddComponent runs Awake synchronously, so the copy exists before Destroy is even
            // reached; Destroy is itself deferred to end of frame, and the filter is already
            // pointed at that copy, so nothing is left rendering the source.
            SculptableMesh sculptable = go.AddComponent<SculptableMesh>();
            if (source != null) Destroy(source);
            return sculptable;
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
            // Nothing else frees this. A mesh a MeshFilter is holding is NOT destroyed when
            // its GameObject is - verified in-editor, and it is why Join used to leak the full
            // vertex+index buffers of every non-survivor it deleted. Same ownership rule as
            // the replace paths, so see ReleaseReplacedMesh for why destroying it is safe.
            // Strictly after ReleaseNativeResources: the GPU scatter holds a GraphicsBuffer
            // taken from this mesh and has to let go of it first.
            if (_mesh != null) Destroy(_mesh);
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
            DisposeNativeAdjacency();
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

        /// Raycasts directly against the live working mesh data (see TriangleSpatialGrid),
        /// instead of Physics.Raycast against a MeshCollider - see class remarks for why.
        /// worldRay/maxDistance/worldPoint/worldNormal are all in world space, matching what
        /// callers previously got from a RaycastHit.
        public bool RaycastMesh(Ray worldRay, float maxDistance, out Vector3 worldPoint, out Vector3 worldNormal) =>
            RaycastMesh(transform, worldRay, maxDistance, out worldPoint, out worldNormal);

        /// The nearest hit on this object OR any of its live mirror copies (see MirrorRepeater),
        /// with `frame` the transform of whichever was hit - this object's own, or a copy's. A
        /// copy draws this same mesh through its own transform, so mapping a world point through
        /// `frame` lands on the very vertices the copy shows there.
        public bool RaycastAnyCopy(Ray worldRay, float maxDistance, out Vector3 worldPoint, out Vector3 worldNormal,
                                   out Transform frame)
        {
            frame = transform;
            bool hit = RaycastMesh(transform, worldRay, maxDistance, out worldPoint, out worldNormal);
            MirrorRepeater repeater = Repeater;
            if (repeater == null) return hit;

            float best = hit ? (worldPoint - worldRay.origin).sqrMagnitude : float.PositiveInfinity;
            for (int i = 0; i < repeater.ViewCount; i++)
            {
                Transform view = repeater.ShownViewTransform(i);
                if (view == null) continue;
                if (!RaycastMesh(view, worldRay, maxDistance, out Vector3 p, out Vector3 n)) continue;
                float sqr = (p - worldRay.origin).sqrMagnitude;
                if (sqr >= best) continue;
                best = sqr;
                worldPoint = p;
                worldNormal = n;
                frame = view;
                hit = true;
            }
            return hit;
        }

        /// RaycastMesh through `frame` instead of this object's own transform - see
        /// RaycastAnyCopy.
        public bool RaycastMesh(Transform frame, Ray worldRay, float maxDistance, out Vector3 worldPoint, out Vector3 worldNormal)
        {
            worldPoint = default;
            worldNormal = default;
            // Catches the grid up with every vertex moved since the last raycast (see
            // QueueSpatialIndexUpdates), or builds it outright. _triangleGrid is a plain C# class
            // (not Unity-serializable), so like _topology (see EnsureAdjacency) it comes back null
            // after a script recompile during Play mode - rebuilt lazily here rather than leaving
            // raycasts silently returning false until the next topology change.
            SyncTriangleGrid();

            Transform t = frame;
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
            float localMaxDistance = maxDistance / Mathf.Max(0.0001f, MinScale(t));

            if (!_triangleGrid.Raycast(localOrigin, localDir, localMaxDistance, _workingVertices, _workingTriangles,
                    out float hitT, out Vector3 localNormal, _hiddenTriangles))
                return false;

            Vector3 localPoint = localOrigin + localDir * hitT;
            worldPoint = t.TransformPoint(localPoint);
            worldNormal = LocalToWorldNormal(t, localNormal);
            return true;
        }

        /// Normals do not transform like directions when scale is involved - they need the
        /// inverse transpose, or a stretched surface reports a normal that is no longer
        /// perpendicular to it (which the normal-driven brushes then push along). Transform's
        /// own TransformDirection/InverseTransformDirection are rotation-only and so are wrong
        /// for this on any non-uniformly scaled object - see RaycastMesh. The inverse transpose
        /// is right through a mirror copy's reflected transform too: an outward normal stays
        /// outward.
        public Vector3 LocalToWorldNormal(Vector3 localNormal) => LocalToWorldNormal(transform, localNormal);

        public static Vector3 LocalToWorldNormal(Transform frame, Vector3 localNormal) =>
            frame.worldToLocalMatrix.transpose.MultiplyVector(localNormal).normalized;

        /// Inverse of LocalToWorldNormal - what the brushes use to bring a world-space hit
        /// normal back into the local space they deform vertices in.
        public Vector3 WorldToLocalNormal(Vector3 worldNormal) => WorldToLocalNormal(transform, worldNormal);

        public static Vector3 WorldToLocalNormal(Transform frame, Vector3 worldNormal) =>
            frame.localToWorldMatrix.transpose.MultiplyVector(worldNormal).normalized;

        private static float MinScale(Transform t)
        {
            Vector3 s = t.lossyScale;
            return Mathf.Min(Mathf.Abs(s.x), Mathf.Min(Mathf.Abs(s.y), Mathf.Abs(s.z)));
        }

        // ------------------------------------------------------------------ growable buffers

        /// Adopts `vertexCount` vertices and `cornerCount` triangle corners as what is in use, and
        /// sizes every per-vertex and per-triangle buffer to exactly that. The replace paths
        /// (Awake, ReplaceGeometry, ReplaceMesh, RestoreSnapshot) all hand over exact arrays, so
        /// this is where capacity and count come back into step.
        private void SetGeometryCounts(int vertexCount, int cornerCount)
        {
            _vertexCount = vertexCount;
            _cornerCount = cornerCount;
        }

        /// Drops every buffer's spare capacity, so Vertices/Normals/Triangles/Mask are once again
        /// exactly VertexCount and TriangleCount long.
        ///
        /// For the whole-mesh tools - Make Symmetric, Cut &amp; Mirror, Trim - which read a buffer,
        /// compute over a parallel copy of it and write the result straight back. Those cannot use
        /// the Exact accessors: an exact COPY is not the live buffer, so the write-back would go
        /// nowhere, and handing one exact array and one padded array to the same routine invites
        /// it to walk off the end of the shorter. Compacting first lets them go on treating length
        /// as the count the way they always have.
        ///
        /// O(vertex count), so it belongs on an explicit, already-expensive user action and never
        /// on the brush path. A no-op (and free) whenever nothing has grown.
        public void CompactBuffers()
        {
            if (_workingVertices.Length == _vertexCount && _workingTriangles.Length == _cornerCount) return;

            _workingVertices = CloneExact(_workingVertices, _vertexCount);
            _workingNormals = CloneExact(_workingNormals, _vertexCount);
            _cavityColors = CloneExact(_cavityColors, _vertexCount);
            _cavityRaw = CloneExact(_cavityRaw, _vertexCount);
            _mask = CloneExact(_mask, _vertexCount);
            _originalVertices = CloneExact(_originalVertices, _vertexCount);
            int[] oldTriangles = _workingTriangles;
            _workingTriangles = CloneExact(_workingTriangles, _cornerCount);
            CarryQuadsAcrossCompaction(oldTriangles);
            if (_hiddenTriangles != null) _hiddenTriangles = CloneExact(_hiddenTriangles, TriangleCount);
            if (_hiddenVertices != null) _hiddenVertices = CloneExact(_hiddenVertices, _vertexCount);

            // Every cache keyed on a buffer's identity or length is now stale by construction.
            _syncedVertices = null;
            _syncedFor = null;
            _strokeRecordSlot = null;
            _affectedStamp = null;
            _normalAccumScratch = null;
            _spatialGrid = null;
            _pendingVertexGridVertices.Clear();
            _pendingTriangleGridVertices.Clear();

            _mesh.Clear();
            ConfigureGpuVertexLayout(_mesh, _vertexCount);
            _mesh.SetVertices(_workingVertices);
            _mesh.SetNormals(_workingNormals);
            _mesh.SetColors(_cavityColors);
            _mesh.SetTriangles(_workingTriangles, 0, _cornerCount, 0, true);
            BuildAdjacency();
            RebuildTriangleGrid();
            BindGpuScatter();
            RefreshVisibility();
        }

        /// Rebuilds the per-vertex neighbour and incident-triangle arrays from the current
        /// triangles. O(triangle count) - cheap enough to run once per topology change but not
        /// meant to run every frame. See MeshAdjacency.
        private void BuildAdjacency()
        {
            _topology = MeshAdjacency.Build(_vertexCount, _workingTriangles, _cornerCount);
        }

        // _topology is a plain C# class, which Unity's domain-reload serializer does not carry
        // across a script recompile while Play mode is active ("Recompile And Continue Playing") -
        // it silently comes back null. Guards every direct access so a mid-session recompile
        // rebuilds it instead of NullReference-ing on the next brush stroke - same lazy
        // rebuild-if-null pattern QueryNear uses for _spatialGrid. Returns it for convenience.
        private MeshAdjacency EnsureAdjacency()
        {
            if (_topology == null || _topology.VertexCount != _vertexCount)
                BuildAdjacency();
            return _topology;
        }

        // Native copy of _topology's neighbour arrays for the Laplacian Burst jobs
        // (SculptController's SmoothRelaxJob and SurfaceRelaxJob) - a job cannot touch a managed
        // array. Neighbours of vertex i live in
        // NeighborsFlat[StartsFlat[i] .. StartsFlat[i] + CountsFlat[i]), the layout MeshAdjacency
        // already holds, so building it is three bulk copies.
        //
        // Rebuilt whenever the managed topology it was copied from is REPLACED, tracked by
        // identity, OR MUTATED, tracked by MeshAdjacency.Version. Identity alone was enough while
        // the only topology change was a wholesale Remesh; a local remesh edits the same instance
        // in place, which identity cannot see - and a job reading a stale neighbour block would
        // smooth toward vertices that have since been re-wired. A mid-Play recompile nulls the
        // source reference, which forces a rebuild too - every non-serializable cache here has to
        // rebuild-if-null, not merely null-check.
        private NativeArray<int> _nativeAdjacencyStarts;
        private NativeArray<int> _nativeAdjacencyCounts;
        private NativeArray<int> _nativeAdjacencyNeighbors;
        private MeshAdjacency _nativeAdjacencySource;
        private int _nativeAdjacencyVersion = -1;

        public NativeArray<int> AdjacencyStarts { get { EnsureNativeAdjacency(); return _nativeAdjacencyStarts; } }
        public NativeArray<int> AdjacencyCounts { get { EnsureNativeAdjacency(); return _nativeAdjacencyCounts; } }
        public NativeArray<int> AdjacencyNeighbors { get { EnsureNativeAdjacency(); return _nativeAdjacencyNeighbors; } }

        private void EnsureNativeAdjacency()
        {
            MeshAdjacency topology = EnsureAdjacency();
            if (_nativeAdjacencyStarts.IsCreated && _nativeAdjacencyCounts.IsCreated &&
                _nativeAdjacencyNeighbors.IsCreated &&
                ReferenceEquals(_nativeAdjacencySource, topology) &&
                _nativeAdjacencyVersion == topology.Version)
                return;

            DisposeNativeAdjacency();

            _nativeAdjacencyStarts = new NativeArray<int>(topology.NeighborStart, Allocator.Persistent);
            _nativeAdjacencyCounts = new NativeArray<int>(topology.NeighborCount, Allocator.Persistent);
            _nativeAdjacencyNeighbors = new NativeArray<int>(topology.NeighborIndices, Allocator.Persistent);
            _nativeAdjacencySource = topology;
            _nativeAdjacencyVersion = topology.Version;
        }

        private void DisposeNativeAdjacency()
        {
            if (_nativeAdjacencyStarts.IsCreated) _nativeAdjacencyStarts.Dispose();
            if (_nativeAdjacencyCounts.IsCreated) _nativeAdjacencyCounts.Dispose();
            if (_nativeAdjacencyNeighbors.IsCreated) _nativeAdjacencyNeighbors.Dispose();
            _nativeAdjacencySource = null;
            _nativeAdjacencyVersion = -1;
        }

        /// The average position of a vertex's directly-connected neighbors (Laplacian
        /// smoothing target). Returns the vertex's own position, unchanged, if it has no
        /// neighbors (degenerate/isolated vertex).
        public Vector3 GetNeighborAverage(int vertexIndex)
        {
            MeshAdjacency topology = EnsureAdjacency();
            int from = topology.NeighborStart[vertexIndex], to = from + topology.NeighborCount[vertexIndex];
            if (to == from) return _workingVertices[vertexIndex];

            int[] neighbors = topology.NeighborIndices;
            // Double precision, and not for accuracy's sake: for mirror symmetry, exactly as
            // RecomputeNormalsRange does it. A vertex and its mirror twin average the SAME
            // neighbour positions, but MeshAdjacency lists them in each vertex's own triangle
            // order, which is not the mirror of its twin's - and float addition is not
            // associative, so the two averages differed by an ulp. Accumulating in double makes
            // the sum EXACT (a handful of 24-bit mantissas at similar magnitudes, far inside
            // double's 53), so it no longer depends on the order and both twins round to the
            // same float.
            //
            // That ulp was not harmless. Every normal-following brush amplifies a position
            // difference by roughly 1/edge when it re-derives normals, so it grows as the mesh
            // gets denser: at 1.3M triangles Inflate run over a crease turned this seed into
            // 1e-2 of mirror drift across a session (SymmetryDriftTests).
            double sx = 0d, sy = 0d, sz = 0d;
            for (int i = from; i < to; i++)
            {
                Vector3 q = _workingVertices[neighbors[i]];
                sx += q.x; sy += q.y; sz += q.z;
            }
            double inv = 1d / (to - from);
            return new Vector3((float)(sx * inv), (float)(sy * inv), (float)(sz * inv));
        }

        /// Re-uploads the three CPU arrays this class treats as authoritative into Unity's own
        /// managed mesh data, so the two agree again.
        ///
        /// Needed before ANY operation that makes Unity re-derive the mesh from its managed
        /// copy (SetTriangles, in practice), because ordinary sculpting writes moved vertices
        /// STRAIGHT into the GPU buffer via GpuVertexScatter and deliberately never syncs them
        /// back - so Unity's managed copy still holds the pre-sculpt shape. Letting it drive a
        /// reupload would silently revert the sculpt on screen (the same class of bug that once
        /// made Remesh rebuild from the stale pre-sculpt shape). Assigning
        /// vertices also recalculates the mesh bounds from the FULL vertex array, which is what
        /// keeps bounds correct while part of the index buffer is hidden.
        private void SyncMeshFromWorkingArrays()
        {
            _mesh.vertices = _workingVertices;
            _mesh.normals = _workingNormals;
            _mesh.colors = _cavityColors;
        }
    }
}
