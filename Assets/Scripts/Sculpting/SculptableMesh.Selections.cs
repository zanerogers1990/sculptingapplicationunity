using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Grab (Move brush) and Pose brush vertex selections - which vertices a drag moves, and by how much.
    public partial class SculptableMesh
    {
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

        /// Selects every vertex within radius of a local-space point, weighted by
        /// smoothstep falloff, so the same region keeps moving together for the rest of a
        /// drag - even once later deltas are queried against a point far outside this
        /// radius. Returns an invalid (empty) selection if nothing was in range.
        /// frontFacingOnly/cameraLocalPos gate the grab the same way every other brush's
        /// footprint is gated (see BrushMath.FrontFacingWeight) - a vertex whose own
        /// normal faces away from the camera never enters the selection at all, so a Move drag
        /// on one side of a thin fold can't also drag the far side along with it.
        ///
        /// connectedOnly (Move's "Connected Only", ZBrush's Move Topological) keeps only the
        /// vertices reachable from the one nearest the grab point by walking mesh edges without
        /// leaving the selection - so a grab on one finger leaves the finger beside it alone even
        /// though both sit inside the radius.
        public GrabSelection SelectGrab(Vector3 localPoint, float radius, bool frontFacingOnly, Vector3 cameraLocalPos,
            bool connectedOnly = false)
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
                float smooth = BrushFalloff.Apply(t01, t01 * t01 * (3f - 2f * t01)) * (1f - _mask[i]); // smoothstep, masked-out
                if (smooth <= 0f) continue;
                // Multiplied into the weight rather than used to reject outright, and through the
                // SAME helper every other brush uses: the old hard test gave the grabbed region a
                // sawtooth edge wherever the silhouette crossed it, so a drag tore at exactly the
                // thin, strongly-curved geometry this option exists to protect.
                smooth *= BrushMath.FrontFacingWeight(
                    frontFacingOnly, _workingNormals[i], _workingVertices[i], cameraLocalPos);
                if (smooth <= 0f) continue;
                indices.Add(i);
                weights.Add(smooth);
            }

            if (connectedOnly && indices.Count > 1) KeepConnectedToNearest(localPoint, indices, weights);
            return new GrabSelection(indices.ToArray(), weights.ToArray());
        }

        // Per-vertex stamps for KeepConnectedToNearest: "in the selection" and "reached", by
        // generation, so neither needs clearing between grabs.
        private int[] _grabSelectStamp;

        private int _grabSelectGeneration;

        private readonly List<int> _grabFloodQueue = new List<int>();

        private void KeepConnectedToNearest(Vector3 localPoint, List<int> indices, List<float> weights)
        {
            if (_grabSelectStamp == null || _grabSelectStamp.Length < _vertexCount * 2)
            {
                _grabSelectStamp = new int[_vertexCount * 2];
                _grabSelectGeneration = 0;
            }
            int generation = ++_grabSelectGeneration;
            int n = _vertexCount;

            int seed = -1;
            float best = float.MaxValue;
            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                _grabSelectStamp[i] = generation;
                float d = (_workingVertices[i] - localPoint).sqrMagnitude;
                if (d < best) { best = d; seed = i; }
            }

            MeshAdjacency topology = EnsureAdjacency();
            int[] starts = topology.NeighborStart, counts = topology.NeighborCount, neighbors = topology.NeighborIndices;
            _grabFloodQueue.Clear();
            _grabFloodQueue.Add(seed);
            _grabSelectStamp[n + seed] = generation;
            for (int head = 0; head < _grabFloodQueue.Count; head++)
            {
                int v = _grabFloodQueue[head];
                for (int k = starts[v], end = starts[v] + counts[v]; k < end; k++)
                {
                    int u = neighbors[k];
                    if (_grabSelectStamp[u] != generation || _grabSelectStamp[n + u] == generation) continue;
                    _grabSelectStamp[n + u] = generation;
                    _grabFloodQueue.Add(u);
                }
            }

            int w = 0;
            for (int k = 0; k < indices.Count; k++)
            {
                if (_grabSelectStamp[n + indices[k]] != generation) continue;
                indices[w] = indices[k];
                weights[w] = weights[k];
                w++;
            }
            indices.RemoveRange(w, indices.Count - w);
            weights.RemoveRange(w, weights.Count - w);
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

            MeshAdjacency topology = EnsureAdjacency();
            int[] neighborStart = topology.NeighborStart;
            int[] neighborCount = topology.NeighborCount;
            int[] neighborIndices = topology.NeighborIndices;

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
                bool touchesMasked = false;
                for (int k = neighborStart[v], kEnd = k + neighborCount[v]; k < kEnd; k++)
                {
                    int n = neighborIndices[k];
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

                for (int k = neighborStart[v], kEnd = k + neighborCount[v]; k < kEnd; k++)
                {
                    int n = neighborIndices[k];
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
    }
}
