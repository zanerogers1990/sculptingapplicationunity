using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Sculpting
{
    /// The vertex apply path every brush and tool funnels through: ApplyVertices/ApplyVerticesLocal,
    /// the affected set, normals, bounds, the drift filter, and the GPU scatter upload.
    public partial class SculptableMesh
    {
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
            // The whole-mesh counterpart of the report in ApplyDirtyVertexList - this is the path
            // Remesh, Trim, boolean subtract, mirror and the masked transforms take, and the
            // timelapse would otherwise sit paused through all of them.
            SculptActivity.ReportEdit(this);
            GeometryVersion++;

            _mesh.vertices = _workingVertices;
            // Always derived from the full topology, never Mesh.RecalculateNormals().
            //
            // Two reasons. While something is HIDDEN, RecalculateNormals() derives normals from
            // the INDEX BUFFER, which no longer references the hidden vertices at all - it would
            // hand them a zero normal, and they would come back black the moment they were shown
            // again.
            //
            // And even with nothing hidden, RecalculateNormals() sums each vertex's incident
            // face normals in float, in index-buffer order. A vertex and its mirror twin get the
            // same faces in a different order, so their normals differed by an ulp before a
            // stroke had touched anything. Inflate pushes along those normals, which turns that
            // ulp straight back into asymmetric geometry, and the rest of the session compounds
            // it: this was the largest remaining seed of mirror drift at 1.3M triangles
            // (SymmetryDriftTests). The pass below is the same computation in double, where it is
            // exact and the order stops mattering.
            RecomputeAllNormalsFromTopology();
            _mesh.normals = _workingNormals;
            _mesh.RecalculateBounds();
            if (fullRebuild)
            {
                // A wholesale vertex reassignment invalidates any index built over the old
                // positions - QueryNear/SelectGrab rebuild lazily from null (see their remarks),
                // whereas a kept-but-stale grid is silently wrong.
                _spatialGrid = null;
                _pendingVertexGridVertices.Clear();
                RebuildTriangleGrid();
                RecomputeCavity();
            }
            // Reassigned even on the cheap path: Mesh.vertices= reuploads from Unity's own
            // CPU-side mesh data, which does NOT include the colors GpuVertexScatter wrote
            // straight into the GPU buffer (see GpuVertexScatter/_cavityColors remarks), so
            // skipping this would revert the mask/cavity tint to whatever Unity last knew.
            _mesh.colors = _cavityColors;

            // This path uploaded the WHOLE vertex buffer, so every vertex is now in sync - the
            // drift baseline ApplyVerticesLocal measures against has to say so, or the first
            // stroke afterwards would skip vertices it wrongly believed were still stale.
            EnsureSyncBuffer();
            Array.Copy(_workingVertices, _syncedVertices, _workingVertices.Length);
            _syncFilterSuspended = false;
        }

        // The dirty vertices plus their direct one-ring neighbors - the set of vertices whose
        // normal AND cavity value a frame's movement can change, and exactly what gets uploaded
        // to the GPU. Built ONCE per ApplyVerticesLocal (see BuildAffectedSet) and then read by
        // the normal pass, the cavity pass and the scatter; normals and cavity each used to
        // build their own private copy of this identical set, which meant walking every dirty
        // vertex's adjacency twice for no difference in the result.
        // Stamp-marking rather than a HashSet: this set is built once and then walked three times
        // (normals, cavity, GPU scatter), and at brush-footprint sizes on a dense mesh the hash
        // was costing real time on both ends - measured 2.0ms to BUILD a 35,696-entry set on a
        // 270k-triangle mesh, before any of the three walks over its scattered buckets. A
        // per-vertex "which build was I last added in" stamp gives the same
        // add-once-if-not-present semantics with an array write, and leaves the members in a
        // flat List that the three consumers walk in order. The generation counter avoids having
        // to clear the stamp array between calls.
        private readonly List<int> _affectedList = new List<int>();

        private int[] _affectedStamp;

        private int _affectedGeneration;

        private void BeginAffectedSet()
        {
            if (_affectedStamp == null || _affectedStamp.Length != _workingVertices.Length)
            {
                _affectedStamp = new int[_workingVertices.Length];
                _affectedGeneration = 0;
            }
            _affectedGeneration++;
            _affectedList.Clear();
        }

        /// Fills _affectedList with the dirty vertices and their direct neighbors. That
        /// scope is already exactly right for both consumers, no wider walk needed: a vertex's
        /// normal only changes when one of its incident triangles changes shape, and two
        /// vertices share a triangle iff they're adjacent; a vertex's cavity value only changes
        /// when it moves or one of its neighbors does, since it is derived from the offsets to
        /// those neighbors.
        private void BuildAffectedSet(List<int> dirtyVertices)
        {
            MeshAdjacency topology = EnsureAdjacency();
            BeginAffectedSet();

            // AddAffected's body inlined with the stamp array, generation and member list hoisted
            // into locals. This runs (1 + valence) times per dirty vertex - roughly seven calls
            // each, so under a wide stroke it is the single most-executed statement in the apply
            // path, and every call was reloading three fields to do two array touches.
            int[] stamp = _affectedStamp;
            int generation = _affectedGeneration;
            List<int> affected = _affectedList;
            int[] starts = topology.NeighborStart;
            int[] counts = topology.NeighborCount;
            int[] neighbors = topology.NeighborIndices;

            for (int k = 0; k < dirtyVertices.Count; k++)
            {
                int vi = dirtyVertices[k];
                if (stamp[vi] != generation) { stamp[vi] = generation; affected.Add(vi); }

                for (int i = starts[vi], end = i + counts[vi]; i < end; i++)
                {
                    int ni = neighbors[i];
                    if (stamp[ni] == generation) continue;
                    stamp[ni] = generation;
                    affected.Add(ni);
                }
            }
        }

        /// Recomputes normals for exactly the affected vertices instead of Mesh.
        /// RecalculateNormals()'s full-mesh scan. Sums each incident triangle's raw
        /// (unnormalized) face-normal cross product - its magnitude is proportional to the
        /// triangle's area, so this naturally area-weights the average, matching what
        /// RecalculateNormals() itself does - just scoped to the affected set instead of the
        /// whole mesh. Reads the set BuildAffectedSet just filled. See ApplyVerticesLocal.
        /// Recomputes the CPU-side normal and raw curvature of exactly these vertices - no neighbour
        /// expansion, no upload, no cavity encode, no drift bookkeeping. For vertices that moved by
        /// less than the drift filter reports (see FilterToDrifted) but that brushes will read again:
        /// Inflate pushes along the normal, Front Facing Only and Clay's plane weigh by it, and
        /// Surface Relax gates on the curvature. Curvature reads only the vertex's own normal, so
        /// normals first is the whole ordering. Must not be called from inside ApplyDirtyVertexList
        /// before its upload, whose affected set it overwrites.
        public void RefreshNormalsAndCurvature(List<int> vertices)
        {
            if (vertices == null || vertices.Count == 0) return;
            EnsureAdjacency();
            EnsureCavityBuffers();
            _affectedList.Clear();
            _affectedList.AddRange(vertices);
            RecomputeNormalsLocal();
            ParallelPass.ForRange(_affectedList.Count,
                _cavityCurvatureRange ?? (_cavityCurvatureRange = CavityCurvatureRange));
        }

        /// RefreshNormalsAndCurvature over everything the current stroke has written - the undo
        /// accumulator already lists exactly that. Run once at release (see
        /// SculptController.HandleStrokeEndCommit), so the next stroke never starts from normals and
        /// curvature the drift filter left stale on one half and not the other.
        public void RefreshStrokeNormalsAndCurvature()
        {
            RefreshNormalsAndCurvature(_strokeDeltaIndices);
        }

        private void RecomputeNormalsLocal()
        {
            // Split across cores above a footprint of a thousand or so vertices. Each entry writes
            // only its OWN normal and reads nothing any other entry writes, so the result is
            // identical either way - see ParallelPass, which also holds the measurements. The
            // delegate is cached rather than rebuilt per frame: this runs on every brush frame.
            ParallelPass.ForRange(_affectedList.Count,
                _recomputeNormalsRange ?? (_recomputeNormalsRange = RecomputeNormalsRange));
        }

        // Cached so the closure-free delegate is allocated once, not per brush frame. Null after
        // a mid-Play recompile, like every other cache on this class - hence the ?? above rather
        // than an initializer (see the domain-reload note on _topology).
        private Action<int, int> _recomputeNormalsRange;

        private void RecomputeNormalsRange(int start, int end)
        {
            // Every array this needs is loaded once and the per-vertex body is inlined, rather
            // than calling RecomputeNormalAt per entry: the affected set runs to six figures under
            // a wide brush, and each call was re-loading four fields before it did any work.
            Vector3[] verts = _workingVertices;
            Vector3[] normals = _workingNormals;
            int[] tris = _workingTriangles;
            int[] triangleStart = _topology.TriangleStart;
            int[] triangleCount = _topology.TriangleCount;
            int[] vertexTris = _topology.TriangleIndices;
            List<int> affected = _affectedList;

            for (int k = start; k < end; k++)
            {
                int i = affected[k];
                // Double precision, and not for accuracy's sake: for mirror symmetry. A vertex and its
                // mirror twin sum the same faces, but in a different order and with each face's edges
                // taken from a different corner, so in float their normals differed by a few ulps.
                // Front Facing Only's silhouette ramp turns a normal difference into a weight
                // difference with a large gain, and mirrored strokes amplified that noise into
                // visible drift over a session (SymmetryDriftTests). In double every face's cross
                // product of float coordinates is EXACT - the differences need at most 25 bits and
                // their products 51 - so both twins sum identical terms and normalize to the same
                // float. Same cost on x64.
                double sx = 0d, sy = 0d, sz = 0d;
                for (int t = triangleStart[i], tEnd = t + triangleCount[i]; t < tEnd; t++)
                {
                    int baseIndex = vertexTris[t] * 3;
                    Vector3 a = verts[tris[baseIndex]];
                    Vector3 b = verts[tris[baseIndex + 1]];
                    Vector3 c = verts[tris[baseIndex + 2]];
                    double e1x = (double)b.x - a.x, e1y = (double)b.y - a.y, e1z = (double)b.z - a.z;
                    double e2x = (double)c.x - a.x, e2y = (double)c.y - a.y, e2z = (double)c.z - a.z;
                    sx += e1y * e2z - e1z * e2y;
                    sy += e1z * e2x - e1x * e2z;
                    sz += e1x * e2y - e1y * e2x;
                }

                // Degenerate (zero-area) triangles can null out the sum for an isolated vertex -
                // keep the previous normal rather than collapsing it to zero, the same "leave it
                // alone" behavior GetNeighborAverage uses for a neighborless vertex.
                //
                // Normalized by hand rather than via Vector3.normalized, which has its OWN epsilon
                // (magnitude < 1e-5, i.e. sqrMagnitude < 1e-10) and silently returns the ZERO
                // vector below it. That threshold is a hundred times looser than this guard, so any
                // sum landing in the gap between them passed the guard and then got assigned zero -
                // exactly the collapse the guard exists to prevent, and a black-shaded vertex on
                // screen. It takes genuinely sliver-thin triangles to reach, but a dense mesh's
                // triangles are small enough in absolute terms to get there (measured: 1124
                // vertices zeroed around a 157k-vertex sphere's pole, where the triangles
                // degenerate).
                double sqrMag = sx * sx + sy * sy + sz * sz;
                if (sqrMag <= 1e-12) continue;
                double inv = 1d / Math.Sqrt(sqrMag);
                normals[i] = new Vector3((float)(sx * inv), (float)(sy * inv), (float)(sz * inv));
            }
        }

        // Face-normal accumulator for RecomputeAllNormalsFromTopology. Its own array rather than
        // summing into _workingNormals directly, so a vertex whose faces cancel out to nothing
        // (a degenerate sliver - see RecomputeNormalsLocal) can keep its PREVIOUS normal instead of
        // being handed an arbitrary one. Allocated only if that path is ever taken, i.e. only on
        // a whole-mesh reapply while part of the mesh is hidden.
        // Three doubles per vertex rather than a Vector3: see RecomputeAllNormalsFromTopology
        // for why this pass has to be order-independent, which in float it is not.
        private double[] _normalAccumScratch;

        /// Whole-mesh version of RecomputeNormalsLocal. Matches Mesh.RecalculateNormals' own
        /// area-weighted average (a face's raw cross product has magnitude proportional to its
        /// area), but reads the FULL triangle list rather than the mesh's current index buffer -
        /// see ApplyVertices for why that distinction matters while geometry is hidden.
        private void RecomputeAllNormalsFromTopology()
        {
            if (_workingNormals == null || _workingNormals.Length != _workingVertices.Length)
                _workingNormals = new Vector3[_workingVertices.Length];
            if (_normalAccumScratch == null || _normalAccumScratch.Length != _workingVertices.Length * 3)
                _normalAccumScratch = new double[_workingVertices.Length * 3];
            Array.Clear(_normalAccumScratch, 0, _normalAccumScratch.Length);

            // Accumulated in double, for the same reason RecomputeNormalsRange is: a vertex and
            // its mirror twin receive the SAME set of face normals, but the index buffer visits
            // them in a different order, and in float the two sums differed by an ulp. Every
            // face's cross product of float coordinates is exact in double and a handful of them
            // sum exactly, so the order stops mattering and both twins round to the same float.
            for (int b = 0; b + 2 < _cornerCount; b += 3)
            {
                int i0 = _workingTriangles[b], i1 = _workingTriangles[b + 1], i2 = _workingTriangles[b + 2];
                Vector3 a = _workingVertices[i0], p1 = _workingVertices[i1], p2 = _workingVertices[i2];
                double e1x = (double)p1.x - a.x, e1y = (double)p1.y - a.y, e1z = (double)p1.z - a.z;
                double e2x = (double)p2.x - a.x, e2y = (double)p2.y - a.y, e2z = (double)p2.z - a.z;
                double fx = e1y * e2z - e1z * e2y;
                double fy = e1z * e2x - e1x * e2z;
                double fz = e1x * e2y - e1y * e2x;
                int a0 = i0 * 3, a1 = i1 * 3, a2 = i2 * 3;
                _normalAccumScratch[a0] += fx; _normalAccumScratch[a0 + 1] += fy; _normalAccumScratch[a0 + 2] += fz;
                _normalAccumScratch[a1] += fx; _normalAccumScratch[a1 + 1] += fy; _normalAccumScratch[a1 + 2] += fz;
                _normalAccumScratch[a2] += fx; _normalAccumScratch[a2 + 1] += fy; _normalAccumScratch[a2 + 2] += fz;
            }

            for (int i = 0; i < _vertexCount; i++)
            {
                // Same hand-rolled normalize, epsilon and keep-the-old-value fallback
                // RecomputeNormalsLocal uses - see its remarks for why Vector3.normalized's own
                // epsilon is a hundred times too loose to be safe here.
                int o = i * 3;
                double sx = _normalAccumScratch[o], sy = _normalAccumScratch[o + 1], sz = _normalAccumScratch[o + 2];
                double sqrMag = sx * sx + sy * sy + sz * sz;
                if (sqrMag <= 1e-12) continue;
                double inv = 1d / Math.Sqrt(sqrMag);
                _workingNormals[i] = new Vector3((float)(sx * inv), (float)(sy * inv), (float)(sz * inv));
            }
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

            // Accumulated into plain locals and compared ONCE at the end, rather than calling
            // Bounds.Encapsulate per vertex. Encapsulate recomputes the box's centre and extents
            // from min/max on every call (and this loop read a fresh Bounds struct back out of
            // the property each time), which is a surprising amount of arithmetic for what is
            // really six min/max comparisons - measured at 2.6ms of a 30ms apply on a 270k-
            // triangle mesh, for a step that in the common case changes nothing at all.
            Bounds b = _mesh.bounds;
            Vector3 min = b.min, max = b.max;
            Vector3 newMin = min, newMax = max;

            for (int k = 0; k < dirtyVertices.Count; k++)
            {
                Vector3 p = _workingVertices[dirtyVertices[k]];
                if (p.x < newMin.x) newMin.x = p.x; else if (p.x > newMax.x) newMax.x = p.x;
                if (p.y < newMin.y) newMin.y = p.y; else if (p.y > newMax.y) newMax.y = p.y;
                if (p.z < newMin.z) newMin.z = p.z; else if (p.z > newMax.z) newMax.z = p.z;
            }

            if (newMin == min && newMax == max) return;
            b.SetMinMax(newMin, newMax);
            _mesh.bounds = b;
        }

        /// Same effect as ApplyVertices(), but the caller guarantees only the vertices in
        /// dirtyVertices moved this frame - lets the triangle grid update just the triangles
        /// incident to those vertices instead of rebuilding from the whole mesh. This is what
        /// every brush's Apply*Brush wrapper calls; ApplyVertices() stays the safe full-rebuild
        /// default for callers that touch the whole mesh at once (ResetMesh, Undo/Redo,
        /// Remesh - see their call sites).
        public void ApplyVerticesLocal(IReadOnlyCollection<int> dirtyVertices)
        {
            // Copied into a concrete List once, and every step below iterates THAT. Callers on
            // this overload pass an int[] (undo's RestoreDelta), so walking the parameter directly
            // means an interface-dispatched enumerator per step. Five such walks became one.
            _dirtyVertexList.Clear();
            foreach (int vi in dirtyVertices) _dirtyVertexList.Add(vi);
            ApplyDirtyVertexList();
        }

        /// List overload, taken by every brush (see SculptController's dirty set, which is a flat
        /// List behind a stamp array for exactly this reason). Copies with one Array.Copy instead
        /// of walking an interface-dispatched enumerator element by element - at wide-brush
        /// footprints the dirty set runs to six figures, where the difference is real.
        public void ApplyVerticesLocal(List<int> dirtyVertices)
        {
            if (!ReferenceEquals(dirtyVertices, _dirtyVertexList))
            {
                _dirtyVertexList.Clear();
                _dirtyVertexList.AddRange(dirtyVertices);
            }
            ApplyDirtyVertexList();
        }

        /// Roughly the middle of what just moved, in local space - see the SculptActivity report
        /// in ApplyDirtyVertexList.
        ///
        /// SAMPLED, not summed. The dirty set runs to six figures at wide brush footprints and
        /// this sits on the brush hot path, while the only consumer is a camera that wants to
        /// know which end of the model the work is at. A fixed sample cap makes this O(1) whatever
        /// the footprint, and a stride rather than a prefix so a long stroke's centroid doesn't
        /// stick to whichever end of the list the dirty set happened to be built from.
        private Vector3 DirtyCentroidLocal()
        {
            const int MaxSamples = 64;

            int count = _dirtyVertexList.Count;
            int stride = Mathf.Max(1, count / MaxSamples);

            Vector3 sum = Vector3.zero;
            int taken = 0;
            for (int i = 0; i < count; i += stride)
            {
                sum += _workingVertices[_dirtyVertexList[i]];
                taken++;
            }

            // count is guaranteed non-zero by the caller's early-out, so taken is at least 1.
            return sum / taken;
        }

        private void ApplyDirtyVertexList()
        {
            // Before the drift filter: the positions have already been written, filtered or not,
            // and raycasts read the working positions directly.
            if (_dirtyVertexList.Count > 0) GeometryVersion++;

            using (ApplyMarker.Auto())
            {
                // Everything below is proportional to how much of the mesh is reported dirty, so the
                // list is first cut down to the vertices that actually went anywhere - see
                // FilterToDrifted for the measurements that make this the single biggest win on the
                // brush hot path.
                EnsureSyncBuffer();
                FilterToDrifted(_dirtyVertexList);
                if (_dirtyVertexList.Count == 0) { RefreshDroppedVertices(); return; }

                // Past FilterToDrifted, geometry demonstrably moved - which is exactly the timelapse
                // recorder's definition of "the user is sculpting". Reported here rather than from
                // the brush handlers because every brush funnels through this one method, and
                // because the filter above has already thrown out the strokes that touched nothing.
                // Undo/redo also reach this path and are silenced at the source - see EditHistory.
                // The centroid rides along so the timelapse camera can lean towards the part of the
                // model being worked on rather than just its middle.
                SculptActivity.ReportEdit(this, DirtyCentroidLocal());

                BuildAffectedSet(_dirtyVertexList);
                RecomputeNormalsLocal();
                ExpandBoundsLocal(_dirtyVertexList);

                // Neither spatial index is re-bucketed here: the moved vertices are queued, and each
                // index catches up the next time something reads it - see QueueSpatialIndexUpdates.
                QueueSpatialIndexUpdates(_dirtyVertexList);

                RecomputeCavityLocal();

                // Replaces the full _mesh.vertices=/.colors= reassignment (and the .normals=
                // assignment removed above) with a compute-shader scatter write scoped to just the
                // affected vertices - see GpuVertexScatter remarks. _affectedList is exactly
                // that "dirty ∪ neighbors" set (built once by BuildAffectedSet above and shared with
                // both the normal and cavity passes) - position is redundant-but-harmless for
                // neighbor-only entries whose position didn't change, only their normal/cavity
                // color did.
                using (ScatterMarker.Auto())
                {
                    EnsureGpuScatter();
                    _gpuScatter.ScatterDirty(_affectedList, _affectedList.Count, _workingVertices, _workingNormals, _cavityColors);
                }

                // The scatter just made the GPU match the CPU for exactly this set, so this is what
                // the next call's drift test measures against. The affected set (not just the dirty
                // list) is right: the scatter uploads a position for every entry in it, including the
                // neighbours pulled in only for their normal/colour. Split across cores the same way
                // as the normal and cavity passes - each entry writes only its own slot.
                ParallelPass.ForRange(_affectedList.Count, _markSyncedRange ?? (_markSyncedRange = MarkSyncedRange));

                // Last, because it reuses _affectedList - see RefreshDroppedVertices.
                RefreshDroppedVertices();
            }
        }

        private Action<int, int> _markSyncedRange;

        private void MarkSyncedRange(int start, int end)
        {
            Vector3[] verts = _workingVertices;
            Vector3[] synced = _syncedVertices;
            List<int> affected = _affectedList;
            for (int k = start; k < end; k++)
            {
                int vi = affected[k];
                synced[vi] = verts[vi];
            }
        }

        // Profiler markers for the stages of the apply path that are worth seeing separately in the
        // Unity Profiler. Negligible cost when nothing is recording.
        private static readonly ProfilerMarker ApplyMarker = new ProfilerMarker("SculptableMesh.ApplyVerticesLocal");

        private static readonly ProfilerMarker ScatterMarker = new ProfilerMarker("SculptableMesh.GpuScatter");

        private static readonly ProfilerMarker TriangleGridSyncMarker = new ProfilerMarker("SculptableMesh.SyncTriangleGrid");

        private static readonly ProfilerMarker VertexGridSyncMarker = new ProfilerMarker("SculptableMesh.SyncVertexGrid");

        // Reused across ApplyVerticesLocal calls - see its remarks for why the dirty set is
        // flattened into a concrete List before anything walks it.
        private readonly List<int> _dirtyVertexList = new List<int>();

        // Where each vertex was the last time ApplyVerticesLocal actually pushed it - i.e. what
        // the GPU vertex buffer, _workingNormals and _cavityColors currently describe. Every
        // downstream step in ApplyVerticesLocal (normals, cavity, triangle re-bucketing, GPU
        // upload) costs the same whether a vertex moved a millimetre or a millionth of one, so
        // the dirty set is filtered against this before any of them run - see FilterToDrifted.
        private Vector3[] _syncedVertices;

        // A freshly allocated sync buffer describes positions from BEFORE this call's movement
        // for vertices this call is about to touch, so the first call after a topology change or
        // a mid-Play domain reload skips the filter entirely rather than mistaking "no record
        // yet" for "hasn't moved".
        private bool _syncFilterSuspended;

        /// How far a vertex has to drift from its last-pushed position before pushing it again is
        /// worth doing, as a fraction of the object's own half-extent. Scaled to object size
        /// rather than to edge length: what decides whether anyone can see the difference is how
        /// big the error is relative to the object on screen, and that ratio is set by the
        /// object's extent regardless of how finely it happens to be tessellated.
        ///
        /// 1/4000 is off a measured sweep, not picked. On a 270k-triangle sphere under a 30-dab
        /// Clay stroke, measured against a control run with the filter disabled entirely, it
        /// leaves the rendered surface at most 0.025% of the object's extent behind the real one
        /// (comfortably under a pixel at any framing) and the rendered NORMALS at most 0.91
        /// degrees off, with not one vertex of 139,814 past a full degree - while taking the mean
        /// dab from 32.1ms to 15.8ms. 1/20000 gave back 10ms of that for error nobody could see
        /// either way; 1/2000 bought only 3ms more and started putting vertices past a degree of
        /// normal error, which is where a matcap would begin to show it.
        private const float ResyncDivisor = 4000f;

        // Derived from _cavityLengthScale, cached rather than recomputed per read: HasVisiblyDrifted
        // is called once per candidate per dab (hundreds of thousands of times a frame under a wide
        // brush), and the scale it depends on only moves on a full recompute - see
        // UpdateCavityLengthScale, which is the one place that writes both.
        private float _resyncSqrThreshold = (1f / ResyncDivisor) * (1f / ResyncDivisor);

        private float ResyncSqrThreshold => _resyncSqrThreshold;

        // Which _workingVertices INSTANCE the sync buffer was built against. A length check alone
        // isn't enough: RestoreSnapshot and the replace paths swap in a whole new positions array
        // that can happen to be the same length as the old one, and a sync buffer carried across
        // that would be describing a shape the mesh no longer has - which the filter would read
        // as "nothing moved" and skip. Same rebuild-if-stale treatment _topology and
        // _triangleGrid get for the domain-reload case.
        private Vector3[] _syncedFor;

        private void EnsureSyncBuffer()
        {
            if (_syncedVertices != null && _syncedVertices.Length == _workingVertices.Length &&
                ReferenceEquals(_syncedFor, _workingVertices)) return;

            _syncedVertices = (Vector3[])_workingVertices.Clone();
            _syncedFor = _workingVertices;
            _syncFilterSuspended = true;
        }

        /// The drift test for one vertex, exposed so a brush that already knows it moves most of
        /// its footprint imperceptibly can decline to report those vertices dirty at all - which
        /// skips the HashSet insert as well as everything downstream of it. Clay's surface-relax
        /// shell is the case this exists for; every other brush is covered by FilterToDrifted
        /// below, which applies the identical rule to whatever it is handed.
        public bool HasVisiblyDrifted(int index)
        {
            // EnsureSyncBuffer's test inlined rather than called: this runs once per candidate per
            // dab, and in the ordinary "buffer is already current" case the whole method is now
            // three compares and a squared distance with no call at all.
            if (_syncedVertices == null || _syncedVertices.Length != _workingVertices.Length ||
                !ReferenceEquals(_syncedFor, _workingVertices))
            {
                EnsureSyncBuffer();
                return true;
            }
            if (_syncFilterSuspended) return true;

            Vector3 now = _workingVertices[index], last = _syncedVertices[index];
            float dx = now.x - last.x, dy = now.y - last.y, dz = now.z - last.z;
            return dx * dx + dy * dy + dz * dz >= _resyncSqrThreshold;
        }

        /// Drops from the dirty list every vertex whose position still matches what was last
        /// pushed for it, to within a threshold far below what can be seen - see
        /// ResyncSqrThreshold. Compares against the last PUSHED position rather than against
        /// this frame's movement, so error can never accumulate: a vertex nudged a hundredth of
        /// a threshold per dab still crosses it (and gets fully resynced) after a hundred dabs,
        /// and the rendered surface is never further than one threshold from the real one.
        ///
        /// This exists because of what a Clay dab actually reports as dirty. Clay's surface-relax
        /// pass (see SculptController.ApplySurfaceRelaxLocal) deliberately reaches 2.5x the brush
        /// radius to close seams between neighbouring dabs, and by design applies a small residual
        /// everywhere in that shell. Measured on a 270k-triangle sphere at a 0.25 brush radius: of
        /// the 101,771 vertices the relax pass touches, 160 move as much as 1% of an edge length
        /// and the other 99.8% move less than that - yet every one of them was entering the dirty
        /// set and paying for a normal recompute, a cavity recompute, a triangle re-bucket and a
        /// GPU upload. Filtering here rather than in the relax pass covers every brush's footprint
        /// rim with one rule.
        private void FilterToDrifted(List<int> dirty)
        {
            _driftDropped.Clear();
            if (_syncFilterSuspended) { _syncFilterSuspended = false; return; }

            float sqrThreshold = _resyncSqrThreshold;
            Vector3[] verts = _workingVertices;
            Vector3[] synced = _syncedVertices;
            int w = 0;
            for (int k = 0; k < dirty.Count; k++)
            {
                int vi = dirty[k];
                Vector3 now = verts[vi], last = synced[vi];
                float dx = now.x - last.x, dy = now.y - last.y, dz = now.z - last.z;
                if (dx * dx + dy * dy + dz * dz < sqrThreshold) { _driftDropped.Add(vi); continue; }
                dirty[w++] = vi;
            }
            dirty.RemoveRange(w, dirty.Count - w);
        }

        // What the last FilterToDrifted dropped - see RefreshDroppedVertices.
        private readonly List<int> _driftDropped = new List<int>();

        /// The drift filter skips the upload, the cavity encode and the spatial re-bucket for a
        /// vertex that has not visibly moved - but not its CPU normal and raw curvature, which is
        /// what this keeps current. Those are not display state: brushes read them. And whether a
        /// vertex counted as "not visibly moved" is a knife edge that the two halves of a mirrored
        /// stroke land on differently by float rounding, so leaving them stale handed one half a
        /// stale normal and the other a fresh one - measured as a 2.3e-3 mirror error when Inflate
        /// (which pushes along the normal) followed Clay on a 1.3M-triangle model
        /// (SymmetryDriftTests). Dropped vertices are a footprint's faint rim, so this is a small
        /// fraction of the normal pass the filter saves.
        private void RefreshDroppedVertices()
        {
            if (_driftDropped.Count == 0) return;
            RefreshNormalsAndCurvature(_driftDropped);
            _driftDropped.Clear();
        }
    }
}
