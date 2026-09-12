using System.Collections.Generic;
using UnityEngine;

namespace Sculpting.DynamicTopology
{
    /// Local, incremental remeshing inside a brush footprint: split the edges that have been
    /// stretched too long, collapse the ones that have been squashed too short, flip toward regular
    /// valence, and relax the result tangentially. The same family of algorithm as Blender's
    /// Dyntopo and Botsch and Kobbelt's remeshing scheme, and the reason a stroke can keep adding
    /// detail without the mesh having to be rebuilt.
    ///
    /// Everything here is bounded by the FOOTPRINT, never by the mesh: the candidate edges come out
    /// of the vertex grid's radius query, each operation touches one edge's two triangles and their
    /// corners, and the work SculptableMesh does afterwards is scoped to the patch that comes back
    /// (see ApplyTopologyPatch). That is what makes it affordable mid-stroke on a model where a
    /// whole-mesh pass costs hundreds of milliseconds.
    ///
    /// Deliberately NOT a Burst job, unlike every brush. Topology surgery is branchy, its working
    /// set is dynamically sized, and it mutates shared structures that no two threads could touch
    /// independently - none of which the brushes' IJobParallelFor shape fits. The existing remesher
    /// (MeshRemesher/SparseRemesher) already sets the precedent of doing this kind of work as plain
    /// C#, and gets its speed from data layout and from not looking at geometry it does not have to.
    public sealed class DynamicTopologyRemesher
    {
        private readonly DynamicTopologySettings _settings;
        private readonly TopologyPatch _patch = new TopologyPatch();

        // Where the last refine ran, per object, so the throttle measures distance travelled since
        // then rather than since the stroke began.
        private SculptableMesh _throttleTarget;
        private Vector3 _lastRefineCentre;
        private bool _hasLastRefine;

        private readonly List<long> _edgeScratch = new List<long>();
        private readonly HashSet<long> _edgeSeen = new HashSet<long>(EdgeKeyComparer.Instance);
        private readonly List<int> _regionVertices = new List<int>();
        private readonly List<Vector3> _relaxTargets = new List<Vector3>();
        private readonly List<int> _relaxScratch = new List<int>();

        public DynamicTopologyRemesher(DynamicTopologySettings settings) => _settings = settings;

        /// The patch produced by the last Refine, for the caller to fold into undo.
        public TopologyPatch LastPatch => _patch;

        /// Whether a refine may run on this object at all, independent of where the brush is.
        ///
        /// The MirrorLink refusal is the important one. A live mirror pair keeps two objects in
        /// step by copying vertex i of one onto vertex i of the other, and any topology change on
        /// one half breaks that correspondence - which today finalizes the pair
        /// (MirrorLink.OnTopologyChanged). If a refine ran here, mirror-linked sculpting would
        /// finalize on the first stroke, which is not a trade worth making silently: suspending
        /// the refine costs the user detail they can add after finalizing, while the alternative
        /// costs them the live pairing they deliberately set up.
        public bool CanRefine(SculptableMesh mesh, BrushType brush)
        {
            if (mesh == null || !_settings.Enabled) return false;
            if (!_settings.AppliesTo(brush)) return false;
            if (mesh.LinkedMirror != null) return false;
            return true;
        }

        /// The counterpart for brushes that refine at the START and END of a gesture rather than
        /// throughout it - see DynamicTopologySettings.RefinesAtStrokeBounds.
        public bool CanRefineAtStrokeBounds(SculptableMesh mesh, BrushType brush)
        {
            if (mesh == null || !_settings.Enabled) return false;
            if (!_settings.RefinesAtStrokeBounds(brush)) return false;
            if (mesh.LinkedMirror != null) return false;
            return true;
        }

        /// True if the brush has travelled far enough since the last refine for another to be
        /// worth running. Distance rather than time so the cadence follows the stroke rather than
        /// the frame rate, and measured against the last REFINE rather than the last dab so a slow
        /// brush laying many dabs in one place does not refine repeatedly over settled geometry.
        public bool ShouldRefine(SculptableMesh mesh, Vector3 centreLocal, float radius)
        {
            if (!ReferenceEquals(_throttleTarget, mesh)) { _throttleTarget = mesh; _hasLastRefine = false; }
            if (!_hasLastRefine) return true;
            float threshold = radius * Mathf.Max(_settings.ThrottleDistanceFraction, 0.01f);
            return (centreLocal - _lastRefineCentre).sqrMagnitude >= threshold * threshold;
        }

        /// Marks a stroke boundary, so the first dab of the next stroke always refines rather than
        /// inheriting however far the previous one happened to end from its last refine.
        public void ResetThrottle() => _hasLastRefine = false;

        /// Runs one refine over the sphere (centreLocal, radius) in the mesh's local space.
        /// Returns the patch describing what changed, or null if nothing did.
        public TopologyPatch Refine(SculptableMesh mesh, Vector3 centreLocal, float radius)
        {
            _lastRefineCentre = centreLocal;
            _throttleTarget = mesh;
            _hasLastRefine = true;

            float regionRadius = radius * (1f + Mathf.Max(_settings.RegionMargin, 0f));
            MeshAdjacency topology = mesh.EnsureAdjacencyForTopologyEdit();
            _patch.Begin(mesh.VertexCount, mesh.TriangleCount * 3,
                         mesh.Vertices.Length, mesh.Triangles.Length);

            var editor = new LocalTopologyEditor(mesh, topology, _patch);
            float splitSqr = _settings.SplitLength * _settings.SplitLength;
            float collapseSqr = _settings.CollapseLength * _settings.CollapseLength;

            // Gathered ONCE, outside the loop. Re-querying per iteration looks harmless and is not:
            // the query goes through the vertex spatial index, which the splits of the previous
            // iteration have just made stale by appending vertices - so QueryNear rebuilds it, and
            // that rebuild is O(vertex count). Measured on a 1.31M-triangle mesh, that single line
            // was most of a 69 ms refine.
            //
            // Nothing is lost by hoisting it. Every vertex a split creates is the midpoint of an
            // edge whose endpoints were already in the region, so it is inside the region by
            // construction, and SplitPass adds it as it goes; a vertex a collapse retires is parked
            // with no neighbours, which every pass below skips on its own.
            GatherRegion(mesh, centreLocal, regionRadius);

            // Splitting gets only part of the budget, so exhausting it cannot starve the two passes
            // that clean up after it. That is not a refinement: a split turns one triangle into two
            // long thin ones and relies on flip and relax to make them well shaped again, so a
            // refine that spends everything on splitting leaves the surface measurably WORSE than
            // one that splits half as much and finishes the job. On a coarse mesh at a fine detail
            // size the cap is reached every single refine, which made that the normal case rather
            // than the exceptional one.
            int splitBudget = Mathf.Max(1, _settings.MaxOperationsPerRefine * 3 / 5);
            int total = _settings.MaxOperationsPerRefine;

            for (int iteration = 0; iteration < Mathf.Max(_settings.Iterations, 1); iteration++)
            {
                if (editor.Operations >= total) break;
                if (_regionVertices.Count == 0) break;

                CollectEdges(mesh, topology);
                // Split first, then collapse, then flip. Order matters: splitting before
                // collapsing means a long edge becomes two of the right length rather than being
                // considered for collapse in the same pass, and flipping last lets it clean up the
                // valence damage the other two just did rather than the other way round.
                SplitPass(mesh, editor, splitSqr, splitBudget);

                CollectEdges(mesh, topology);
                CollapsePass(mesh, editor, collapseSqr, total);

                CollectEdges(mesh, topology);
                FlipPass(editor, total);
            }

            RelaxPass(mesh, topology, centreLocal, regionRadius);

            _patch.SetCounts(mesh.VertexCount, mesh.TriangleCount * 3);
            return _patch.IsEmpty ? null : _patch;
        }

        // --------------------------------------------------------------------- region

        private void GatherRegion(SculptableMesh mesh, Vector3 centreLocal, float radius)
        {
            _regionVertices.Clear();
            List<int> candidates = mesh.QueryNear(centreLocal, radius);
            for (int i = 0; i < candidates.Count; i++)
            {
                int v = candidates[i];
                // A fully masked vertex is protected from every brush, and refining around it
                // would carve detail into geometry the user has explicitly frozen.
                if (mesh.Mask[v] >= 0.999f) continue;
                _regionVertices.Add(v);
            }
        }

        /// Every edge with at least one endpoint in the region, de-duplicated.
        ///
        /// Keyed through EdgeKeyComparer rather than the default long comparer, which for a packed
        /// (min, max) index pair collapses to `min ^ max` - catastrophic for mesh edges, whose
        /// endpoints are always nearby numbers. That mistake has already cost this project a
        /// remesh that took a minute instead of half a second (see EdgeKeyComparer).
        private void CollectEdges(SculptableMesh mesh, MeshAdjacency topology)
        {
            _edgeScratch.Clear();
            _edgeSeen.Clear();
            for (int i = 0; i < _regionVertices.Count; i++)
            {
                int v = _regionVertices[i];
                if (v >= topology.VertexCount) continue;
                int start = topology.NeighborStart[v], end = start + topology.NeighborCount[v];
                for (int k = start; k < end; k++)
                {
                    int n = topology.NeighborIndices[k];
                    long key = EdgeKey(v, n);
                    if (_edgeSeen.Add(key)) _edgeScratch.Add(key);
                }
            }
        }

        private static long EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b, hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        private static void UnpackEdge(long key, out int a, out int b)
        {
            a = (int)(key >> 32);
            b = (int)(key & 0xFFFFFFFF);
        }

        // ----------------------------------------------------------------------- passes

        private void SplitPass(SculptableMesh mesh, LocalTopologyEditor editor, float splitSqr, int budget)
        {
            for (int i = 0; i < _edgeScratch.Count; i++)
            {
                if (editor.Operations >= budget) return;
                UnpackEdge(_edgeScratch[i], out int a, out int b);

                // Re-read the positions array on every edge rather than hoisting it: a split
                // appends a vertex, and an append that crosses the capacity boundary REPLACES this
                // array (see SculptableMesh.EnsureVertexCapacity). A hoisted reference would go on
                // reading the old one, so every edge considered after the first growth would be
                // measured against pre-stroke positions.
                Vector3[] verts = mesh.Vertices;
                if ((verts[a] - verts[b]).sqrMagnitude <= splitSqr) continue;

                int added = editor.SplitEdge(a, b);
                // A vertex the split just created is itself a region vertex - the next iteration's
                // gather would find it anyway, and adding it now lets the relax pass reach it.
                if (added >= 0) _regionVertices.Add(added);
            }
        }

        private void CollapsePass(SculptableMesh mesh, LocalTopologyEditor editor, float collapseSqr, int budget)
        {
            Vector3[] verts = mesh.Vertices;
            float[] mask = mesh.Mask;
            for (int i = 0; i < _edgeScratch.Count; i++)
            {
                if (editor.Operations >= budget) return;
                UnpackEdge(_edgeScratch[i], out int a, out int b);
                if (a >= mesh.VertexCount || b >= mesh.VertexCount) continue;
                if ((verts[a] - verts[b]).sqrMagnitude >= collapseSqr) continue;
                // Never merge a protected vertex away: the mask is a promise that this geometry
                // does not move, and a collapse would delete it outright.
                if (mask[a] >= 0.999f || mask[b] >= 0.999f) continue;

                // Either direction is a valid merge; the first is refused on a boundary that the
                // second may satisfy, so both are tried before giving up on the edge.
                if (!editor.CollapseEdge(a, b)) editor.CollapseEdge(b, a);
            }
        }

        private void FlipPass(LocalTopologyEditor editor, int budget)
        {
            for (int i = 0; i < _edgeScratch.Count; i++)
            {
                if (editor.Operations >= budget) return;
                UnpackEdge(_edgeScratch[i], out int a, out int b);
                editor.FlipEdge(a, b);
            }
        }

        /// One tangential Laplacian pass over the region, to even out the triangle shapes the three
        /// topology passes leave behind.
        ///
        /// TANGENTIAL: each vertex's move is projected onto the plane of its own normal before it
        /// is applied, so the pass slides vertices ALONG the surface rather than into it. A plain
        /// Laplacian is a smoothing operator - it would shrink every convex feature the stroke just
        /// built, which is the opposite of what a remesher is for.
        ///
        /// Jacobi, not Gauss-Seidel: every target is computed against the surface as the topology
        /// passes left it, and only then is anything written. Updating in place makes the result
        /// depend on the order the region happened to be gathered in, which for a mirrored stroke
        /// is not the mirror of itself - the same trap ApplyPostStrokeUnifyPass documents.
        private void RelaxPass(SculptableMesh mesh, MeshAdjacency topology, Vector3 centreLocal, float radius)
        {
            float strength = Mathf.Clamp01(_settings.RelaxStrength);

            // Over what the topology passes actually TOUCHED, not over the whole footprint. Relax
            // exists to make the triangles a split or flip just created well shaped; running it on
            // settled geometry that no operation went near only accumulates tangential drift, once
            // per refine, for as long as the brush stays in the area - and a stroke refines many
            // times over the same ground.
            //
            // Snapshotted by count because the loop below records into the same list; everything it
            // can move is already in there (it only moves what an operation touched), so this is
            // belt and braces rather than a live concern.
            _relaxScratch.Clear();
            List<int> touched = _patch.TouchedVertices;
            for (int i = 0, n = touched.Count; i < n; i++) _relaxScratch.Add(touched[i]);
            if (strength <= 0f || _relaxScratch.Count == 0) return;

            Vector3[] verts = mesh.Vertices;
            Vector3[] normals = mesh.Normals;
            float[] mask = mesh.Mask;
            float radiusSqr = radius * radius;
            int vertexCount = mesh.VertexCount;

            _relaxTargets.Clear();
            for (int i = 0; i < _relaxScratch.Count; i++)
            {
                int v = _relaxScratch[i];
                if (v >= vertexCount) { _relaxTargets.Add(Vector3.zero); continue; }

                int start = topology.NeighborStart[v], count = topology.NeighborCount[v];
                if (count == 0 || (verts[v] - centreLocal).sqrMagnitude > radiusSqr)
                {
                    _relaxTargets.Add(Vector3.zero);
                    continue;
                }

                Vector3 sum = Vector3.zero;
                for (int k = start; k < start + count; k++) sum += verts[topology.NeighborIndices[k]];
                Vector3 delta = sum / count - verts[v];

                // Strip the component along the normal - what makes this a reparameterisation of
                // the surface rather than a smoothing of it.
                //
                // The normal is derived from the vertex's CURRENT triangles rather than read from
                // the cached array, which at this point in a refine describes the one-ring as it
                // was before the split, collapse and flip passes rewired it (the cache is not
                // refreshed until ApplyTopologyPatch, after this returns). Projecting against a
                // stale normal leaves part of the step pointing into the surface, so the relax
                // stops being tangential and starts eating the shape it is supposed to preserve.
                Vector3 n = CurrentNormal(mesh, topology, v, normals[v]);
                delta -= n * Vector3.Dot(delta, n);
                _relaxTargets.Add(delta * (strength * (1f - mask[v])));
            }

            for (int i = 0; i < _relaxScratch.Count; i++)
            {
                int v = _relaxScratch[i];
                if (v >= vertexCount) continue;
                Vector3 delta = _relaxTargets[i];
                if (delta.sqrMagnitude <= 0f) continue;
                if (!RelaxKeepsTrianglesValid(mesh, topology, v, verts[v] + delta)) continue;

                // Recorded like any other vertex move, so one undo press takes the relax back with
                // the stroke that caused it.
                mesh.RecordUndoBeforeIfNeeded(v);
                verts[v] += delta;
                _patch.RecordTouchedVertex(v);
            }
        }

        /// The area-weighted normal of a vertex's current incident triangles - the same average
        /// Mesh.RecalculateNormals produces, over the topology as it stands right now. Falls back
        /// to `cached` for a vertex whose faces cancel out, matching what the rest of this project
        /// does with a degenerate fan (see SculptableMesh.RecomputeNormalsRange).
        private static Vector3 CurrentNormal(SculptableMesh mesh, MeshAdjacency topology, int v, Vector3 cached)
        {
            Vector3[] verts = mesh.Vertices;
            int[] tris = mesh.Triangles;
            int start = topology.TriangleStart[v], end = start + topology.TriangleCount[v];

            Vector3 sum = Vector3.zero;
            for (int k = start; k < end; k++)
            {
                int b = topology.TriangleIndices[k] * 3;
                Vector3 p0 = verts[tris[b]];
                sum += Vector3.Cross(verts[tris[b + 1]] - p0, verts[tris[b + 2]] - p0);
            }
            return VectorMath.NormalizeOr(sum, cached);
        }

        /// Would moving `v` to `target` turn any of its triangles inside out?
        ///
        /// The relax pass is the only one of the four that MOVES geometry rather than rewiring it,
        /// and for a while it was the only one without a validity test - split cannot invert
        /// anything, and collapse and flip both check. It was wrong to leave out: a tangential
        /// Laplacian step is bounded by the one-ring, not by the triangles inside it, so on a
        /// sliver it can carry a vertex clean past its opposite edge. That produced exactly one
        /// inverted triangle in a three-refine run - which is how this class of bug always shows
        /// up, as a single black facet nobody can reproduce.
        ///
        /// Tested against the live positions, so a vertex moved earlier in this pass is accounted
        /// for. That makes which moves get rejected depend on the order the region was gathered in,
        /// and so not exactly mirror-symmetric - an acceptable trade here, where the topology
        /// itself already diverges between the two halves of a mirrored stroke, and the alternative
        /// is letting two adjacent vertices invert a triangle between them.
        private static bool RelaxKeepsTrianglesValid(SculptableMesh mesh, MeshAdjacency topology, int v, Vector3 target)
        {
            Vector3[] verts = mesh.Vertices;
            int[] tris = mesh.Triangles;
            int start = topology.TriangleStart[v], end = start + topology.TriangleCount[v];

            for (int k = start; k < end; k++)
            {
                int b = topology.TriangleIndices[k] * 3;
                int i0 = tris[b], i1 = tris[b + 1], i2 = tris[b + 2];
                Vector3 p0 = verts[i0], p1 = verts[i1], p2 = verts[i2];
                Vector3 before = Vector3.Cross(p1 - p0, p2 - p0);

                if (i0 == v) p0 = target;
                else if (i1 == v) p1 = target;
                else p2 = target;
                Vector3 after = Vector3.Cross(p1 - p0, p2 - p0);

                if (after.sqrMagnitude <= 1e-20f) return false;
                if (Vector3.Dot(before, after) <= 0f) return false;
            }
            return true;
        }

    }
}
