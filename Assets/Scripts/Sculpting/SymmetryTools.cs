using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// The repair half of the symmetry system: the operations that a vertex correspondence map
    /// makes possible and that positional stroke mirroring cannot do on its own (see
    /// SymmetryMap's remarks for that split).
    ///
    /// Every method here is pure geometry over plain arrays - no Mesh, no MonoBehaviour - both so
    /// they can be tested outside Unity against the shim harness and so the caller stays in
    /// charge of the undo snapshot and the mesh rebuild, which differ between a vertex-only edit
    /// (ApplyVertices) and a topology change (ReplaceMesh). SymmetryController is that caller.
    public static class SymmetryTools
    {
        /// Copies the source side of the model onto the other side, using the map's pairing so
        /// each vertex lands on its OWN counterpart rather than merely somewhere plausible.
        ///
        /// This is the operation positional mirroring cannot express. Mirrored brush strokes keep
        /// two sides looking alike while their topology matches, but after a Remesh (which
        /// re-tessellates each half independently) the two sides no longer share a vertex layout,
        /// and every later stroke leaves them a little further apart. Given a correspondence, the
        /// fix is exact and instant: write each source vertex's reflection into its partner.
        ///
        /// Unmatched vertices are deliberately left untouched rather than approximated HERE - the
        /// map found no counterpart for them, so any position written by this pass would be a
        /// guess, and a guess is how a "cleanup" tool ends up mangling geometry it was pointed at
        /// by mistake. Leaving them exactly where they were is not the finished answer either
        /// (they end up standing proud of a surface that moved out from under them), which is
        /// what CarryUnmatched is for - run it after this, as SymmetryOps.MakeSymmetric does.
        /// Returns how many vertices were rewritten.
        public static int MakeSymmetric(Vector3[] vertices, SymmetryMap map, bool sourceIsPositive)
        {
            if (vertices == null || map == null || map.VertexCount != vertices.Length) return 0;

            int axis = map.Axis;
            int changed = 0;

            for (int i = 0; i < vertices.Length; i++)
            {
                int partner = map.PartnerOf(i);
                if (partner == SymmetryMap.NoPartner) continue;

                // Only the SOURCE side drives. Reading the sign off the live array is safe even
                // though this loop also writes to it: reflecting a source vertex produces a
                // position on the destination side, so a vertex never crosses the plane here and
                // the source/destination roles cannot swap mid-pass.
                float side = SymmetryMap.Coord(vertices[i], axis);
                bool isSource = sourceIsPositive ? side > 0f : side < 0f;
                if (!isSource) continue;

                Vector3 reflected = SymmetryMap.Reflect(vertices[i], axis);

                // Counts vertices genuinely MOVED, not vertices written. Running this twice in a
                // row writes the same value the second time, and reporting "177 vertices" again
                // for a pass that changed nothing would both misdescribe what happened and - via
                // SymmetryOps, which commits only when something changed - leave an undo step
                // that undoes nothing. Exact comparison is right here rather than a tolerance:
                // the previous pass stored precisely this value, so an unchanged vertex compares
                // bit-equal, while a source that has since moved at all should be re-mirrored.
                if ((vertices[partner] - reflected).sqrMagnitude <= 0f) continue;

                vertices[partner] = reflected;
                changed++;
            }

            return changed;
        }

        /// Carries the vertices MakeSymmetric could not touch along with the ones it moved, so a
        /// repair leaves a continuous surface instead of a torn one.
        ///
        /// A vertex with no partner is not a vertex that should stay put - it is a vertex the map
        /// has nothing to say about. Leaving it exactly where it was while every vertex around it
        /// jumps onto its mirror is what makes a repair read as "nearly aligned": the two halves
        /// match everywhere except at a scatter of points that now stick out of the surface by
        /// the whole distance their neighbours just moved. Those points are precisely where the
        /// two sides are not tessellated alike (one side has a vertex the other does not), which
        /// no correspondence can fix and which the eye reads as pimples along the repaired half.
        ///
        /// The rule here is the one thing that can be said about such a vertex without guessing
        /// at geometry: it should move the way its surroundings moved. Each unmatched vertex
        /// takes the average of its known neighbours' displacements, which then becomes known
        /// itself, so a cluster of them fills inward from its rim. This deforms the surface
        /// exactly as much as the repair deformed it locally and no more - unlike projecting the
        /// vertex onto the mirrored surface, which would be a guess about where the model ought
        /// to be, and unlike leaving it, which is a guess that the model was already right there.
        ///
        /// The source side is never carried, only used as an anchor: MakeSymmetric leaves it
        /// untouched by definition, and a repair that quietly reshaped the half it was copying
        /// FROM would be a different operation than the one the button says it is.
        ///
        /// `before` is the vertex array as it stood before the repair, `vertices` the array being
        /// repaired; they must be the same length as the map. `sourceIsPositive` is the same flag
        /// MakeSymmetric was given. Needs a map built WITH triangles (SymmetryMap.HasTopology) -
        /// there is no notion of "its neighbours" without them. Returns how many vertices were
        /// carried.
        public static int CarryUnmatched(Vector3[] vertices, Vector3[] before, SymmetryMap map,
                                         bool sourceIsPositive)
        {
            if (vertices == null || before == null || map == null) return 0;
            if (map.VertexCount != vertices.Length || before.Length != vertices.Length) return 0;
            if (!map.HasTopology) return 0;

            int n = vertices.Length;
            int axis = map.Axis;
            var shift = new Vector3[n];
            var known = new bool[n];
            int unmatched = 0;

            for (int i = 0; i < n; i++)
            {
                float side = SymmetryMap.Coord(before[i], axis);
                bool isSource = sourceIsPositive ? side > 0f : side < 0f;
                bool assigned = map.IsOnPlane(i) || map.PartnerOf(i) != SymmetryMap.NoPartner;

                if (assigned || isSource)
                {
                    // Zero for anything MakeSymmetric did not move, which is exactly what makes
                    // an untouched region hold its unmatched vertices still.
                    shift[i] = vertices[i] - before[i];
                    known[i] = true;
                }
                else unmatched++;
            }
            if (unmatched == 0) return 0;

            // Rounds are batched - every vertex in a round reads only what was known when the
            // round started - so the result does not depend on vertex ordering. One round per
            // ring of unmatched vertices; the loop runs until nothing more can be reached, which
            // for a scatter of isolated vertices is a single pass.
            var pending = new Vector3[n];
            var pendingSet = new bool[n];
            int carried = 0;

            while (true)
            {
                int filled = 0;
                for (int i = 0; i < n; i++)
                {
                    if (known[i]) continue;

                    Vector3 sum = Vector3.zero;
                    int count = 0;
                    int neighbours = map.NeighbourCount(i);
                    for (int k = 0; k < neighbours; k++)
                    {
                        int nb = map.Neighbour(i, k);
                        if (!known[nb]) continue;
                        sum += shift[nb];
                        count++;
                    }
                    if (count == 0) continue;

                    pending[i] = sum / count;
                    pendingSet[i] = true;
                    filled++;
                }

                if (filled == 0) break;

                for (int i = 0; i < n; i++)
                {
                    if (!pendingSet[i]) continue;
                    pendingSet[i] = false;
                    shift[i] = pending[i];
                    known[i] = true;

                    if (pending[i].sqrMagnitude <= 0f) continue;
                    vertices[i] = before[i] + pending[i];
                    carried++;
                }
            }

            return carried;
        }

        /// Replaces one whole side of the model with a reflection of the other, cutting the mesh
        /// at the plane and rebuilding the discarded half from scratch. Topology-changing, which
        /// is the point: unlike MakeSymmetric it needs NO vertex correspondence, so it works on
        /// exactly the models MakeSymmetric has to refuse - two arms modelled or joined
        /// separately, one side remeshed, an imported asset whose halves never matched.
        ///
        /// MakeSymmetric and this are the two halves of "make it symmetric", and which one is
        /// right depends entirely on whether a correspondence exists. MakeSymmetric preserves the
        /// destination side's topology and only nudges vertices, so mask, resolution and every
        /// index into the mesh survive - but it can only move vertices that HAVE a counterpart,
        /// and mirroring through a partial correspondence tears the surface (see
        /// SymmetryOps.MaxUnmatchedFraction). This throws the destination side away entirely, so
        /// it always produces an exactly symmetric result, at the cost of rebuilding the mesh.
        ///
        /// The cut is decided per TRIANGLE by its centroid, not per vertex, so every triangle
        /// goes wholly one way or the other and none is left with a dangling corner. A kept
        /// triangle that straddles the plane then has its crossed-over corners pinned onto the
        /// plane, which is what makes the cut edge exactly planar instead of a ragged fringe
        /// following whatever the tessellation happened to do there. Because the boundary loop
        /// ends up exactly on the plane, the mirrored half can SHARE those vertices rather than
        /// duplicating them, and the two halves come out already joined - watertight, one shared
        /// edge loop, nothing left to weld afterward.
        ///
        /// `seamTolerance` is the band around the plane whose vertices are pinned onto it before
        /// anything is cut - the same "nearly on the centreline is what actually breaks a
        /// symmetric model" reasoning as SnapToPlane, applied up front so a centre vertex sitting
        /// a hair off the plane cannot decide a triangle's side by accident.
        ///
        /// Returns false (out params null) when there is nothing to work with: no geometry, or
        /// no triangle at all on the source side.
        public static bool MirrorAndWeld(Vector3[] vertices, int[] triangles, int axis,
                                         bool sourceIsPositive, float seamTolerance,
                                         out Vector3[] mirroredVertices, out int[] mirroredTriangles,
                                         out int keptTriangleCount, out int discardedTriangleCount)
        {
            mirroredVertices = null;
            mirroredTriangles = null;
            keptTriangleCount = 0;
            discardedTriangleCount = 0;
            if (vertices == null || triangles == null || vertices.Length == 0 || triangles.Length < 3)
                return false;

            float seam = Mathf.Max(seamTolerance, 1e-6f);
            float sign = sourceIsPositive ? 1f : -1f;

            // Everything works against a copy: this pins vertices onto the plane as it goes, and
            // the caller's array is the live mesh buffer.
            var work = (Vector3[])vertices.Clone();
            for (int i = 0; i < work.Length; i++)
                if (Mathf.Abs(SymmetryMap.Coord(work[i], axis)) <= seam) work[i] = Pin(work[i], axis);

            // Pass 1: which triangles survive the cut. By centroid, so a triangle spanning the
            // plane goes whichever way most of it lies rather than being split - this is a
            // rebuild, not a boolean, and a triangle-accurate cut would buy nothing here since
            // the surviving corners are pinned onto the plane immediately afterward anyway.
            int triCount = triangles.Length / 3;
            var keep = new bool[triCount];
            for (int t = 0; t < triCount; t++)
            {
                int b = t * 3;
                float centroid = (SymmetryMap.Coord(work[triangles[b]], axis) +
                                  SymmetryMap.Coord(work[triangles[b + 1]], axis) +
                                  SymmetryMap.Coord(work[triangles[b + 2]], axis)) / 3f;
                // Strictly greater than zero: a triangle lying exactly IN the plane would
                // otherwise be kept and then mirrored onto itself, giving two coincident faces.
                keep[t] = centroid * sign > 0f;
                if (keep[t]) keptTriangleCount++; else discardedTriangleCount++;
            }
            if (keptTriangleCount == 0) return false;

            // Pass 2: pin the kept triangles' crossed-over corners onto the plane. Only vertices
            // a KEPT triangle actually uses - one belonging solely to discarded triangles is
            // about to disappear, and moving it would be pointless work.
            var used = new bool[work.Length];
            for (int t = 0; t < triCount; t++)
            {
                if (!keep[t]) continue;
                int b = t * 3;
                used[triangles[b]] = true;
                used[triangles[b + 1]] = true;
                used[triangles[b + 2]] = true;
            }
            for (int i = 0; i < work.Length; i++)
                if (used[i] && SymmetryMap.Coord(work[i], axis) * sign < 0f) work[i] = Pin(work[i], axis);

            // Pass 3: compact the kept vertices, giving each off-plane one a reflected twin.
            // On-plane vertices get no twin - they ARE the join, and duplicating them is exactly
            // what would leave the result as two shells touching rather than one closed surface.
            var sourceIndex = new int[work.Length];
            var mirrorIndex = new int[work.Length];
            var outVerts = new List<Vector3>(keptTriangleCount * 2);
            for (int i = 0; i < work.Length; i++)
            {
                sourceIndex[i] = -1;
                mirrorIndex[i] = -1;
                if (!used[i]) continue;

                sourceIndex[i] = outVerts.Count;
                outVerts.Add(work[i]);
                if (SymmetryMap.Coord(work[i], axis) == 0f)
                {
                    mirrorIndex[i] = sourceIndex[i]; // shared - a seam vertex is its own twin
                }
                else
                {
                    mirrorIndex[i] = outVerts.Count;
                    outVerts.Add(SymmetryMap.Reflect(work[i], axis));
                }
            }

            var outTris = new List<int>(keptTriangleCount * 6);
            for (int t = 0; t < triCount; t++)
            {
                if (!keep[t]) continue;
                int b = t * 3;
                int i0 = triangles[b], i1 = triangles[b + 1], i2 = triangles[b + 2];

                // Pinning can collapse a triangle that had two corners just across the plane down
                // onto a line. Dropped rather than kept: a zero-area face has an undefined normal,
                // and both halves would carry one.
                int a0 = sourceIndex[i0], a1 = sourceIndex[i1], a2 = sourceIndex[i2];
                if (Degenerate(outVerts, a0, a1, a2)) continue;

                outTris.Add(a0);
                outTris.Add(a1);
                outTris.Add(a2);
                // Reflection flips handedness, so the mirrored copy needs its winding reversed or
                // the whole new half renders inside-out - the same fix, for the same reason, as
                // MeshMirror.MirrorAcross's odd-axis-count swap.
                outTris.Add(mirrorIndex[i0]);
                outTris.Add(mirrorIndex[i2]);
                outTris.Add(mirrorIndex[i1]);
            }

            if (outTris.Count == 0) return false;

            mirroredVertices = outVerts.ToArray();
            mirroredTriangles = outTris.ToArray();
            return true;
        }

        /// Zeroes the mirror-plane component of a point, putting it exactly on the plane.
        private static Vector3 Pin(Vector3 v, int axis)
        {
            switch (axis)
            {
                case SymmetryMap.AxisX: v.x = 0f; break;
                case SymmetryMap.AxisY: v.y = 0f; break;
                default: v.z = 0f; break;
            }
            return v;
        }

        private static bool Degenerate(List<Vector3> verts, int a, int b, int c)
        {
            if (a == b || b == c || a == c) return true;
            return Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]).sqrMagnitude <= 1e-20f;
        }

        /// Snaps every on-plane vertex exactly onto the mirror plane, zeroing the axis component
        /// that the tolerance band allowed to drift.
        ///
        /// Worth doing as its own step because "nearly on the centreline" is what actually breaks
        /// a symmetric model: a centre vertex sitting a hair off the plane is mirrored by
        /// MakeSymmetric onto a partner a hair off the OTHER way, so the seam develops a
        /// zig-zag that gets worse each time symmetry is enforced. Pinning them to zero first
        /// makes the centreline a fixed point of every later operation.
        /// Returns how many vertices moved.
        public static int SnapToPlane(Vector3[] vertices, SymmetryMap map)
        {
            if (vertices == null || map == null || map.VertexCount != vertices.Length) return 0;

            int axis = map.Axis;
            int changed = 0;

            for (int i = 0; i < vertices.Length; i++)
            {
                if (!map.IsOnPlane(i)) continue;
                if (Mathf.Approximately(SymmetryMap.Coord(vertices[i], axis), 0f)) continue;

                Vector3 v = vertices[i];
                switch (axis)
                {
                    case SymmetryMap.AxisX: v.x = 0f; break;
                    case SymmetryMap.AxisY: v.y = 0f; break;
                    default: v.z = 0f; break;
                }
                vertices[i] = v;
                changed++;
            }

            return changed;
        }

        /// Merges vertices that occupy the same point into single shared ones, remapping the
        /// triangles onto the survivors and dropping the triangles that collapse to nothing.
        ///
        /// Runs at TWO different radii, which is the whole design:
        ///
        /// - Within `seamTolerance` of the mirror plane, generously - SnapToPlane has just pulled
        ///   a band of vertices onto the plane, and the pair that used to straddle it now needs to
        ///   become one. This is the case joining two mirrored halves creates: MeshJoiner combines
        ///   without welding (by design), so the centreline carries two coincident vertices for
        ///   every one it should have, and the model only LOOKS closed - it is two shells touching,
        ///   which splits apart under smoothing and remeshes as a doubled surface.
        ///
        /// - Everywhere else only at `coincidentEpsilon`, small enough to catch vertices that are
        ///   literally the same point and nothing else. Meshes routinely carry these: a UV seam
        ///   splits one meridian so the two sides can hold different texture coordinates, and
        ///   poles are split once per surrounding triangle. Unity's own primitives are full of
        ///   them - a default sphere is 515 vertices at just 386 distinct positions - and they are
        ///   why an obviously symmetric sphere could not pair up cleanly: the split side has two
        ///   vertices where its reflection has one, so some of them are guaranteed to be left over.
        ///   Merging them makes every Unity primitive pair perfectly on all three axes, and
        ///   watertight as a bonus (a default sphere goes from 232 boundary edges to none).
        ///
        /// The two radii are what keeps this safe. A whole-mesh weld at the SEAM tolerance would
        /// also collapse genuinely fine sculpted detail that happens to sit closer together than
        /// that - destroying geometry far from the plane, which a symmetry tool has no business
        /// doing. Away from the plane this only ever merges points that are already identical.
        ///
        /// Returns false, leaving the out params null, when nothing needed merging - so the caller
        /// can skip a topology-changing mesh rebuild it does not need.
        public static bool Weld(Vector3[] vertices, int[] triangles, int axis,
                                float seamTolerance, float coincidentEpsilon,
                                out Vector3[] weldedVertices, out int[] weldedTriangles)
        {
            weldedVertices = null;
            weldedTriangles = null;
            if (vertices == null || triangles == null) return false;

            int n = vertices.Length;
            float seam = Mathf.Max(seamTolerance, 1e-6f);
            float coincident = Mathf.Clamp(coincidentEpsilon, 1e-7f, seam);

            // remap[i] is the vertex i has been merged into - itself until proven otherwise.
            var remap = new int[n];
            for (int i = 0; i < n; i++) remap[i] = i;

            // Bucketed at the LARGER radius so one 3x3x3 lookup covers both cases: the block
            // extends a full cell past the home cell in each direction, so it contains everything
            // within `seam`, and therefore everything within `coincident` too.
            var buckets = new Dictionary<Vector3Int, List<int>>();
            int merged = 0;

            for (int i = 0; i < n; i++)
            {
                // On the seam a vertex may merge with anything in the snapped band; off it, only
                // with something at its own position.
                bool onSeam = Mathf.Abs(SymmetryMap.Coord(vertices[i], axis)) <= seam;
                float radius = onSeam ? seam : coincident;
                float radiusSqr = radius * radius;

                Vector3Int home = CellOf(vertices[i], seam);
                int found = -1;

                for (int z = -1; z <= 1 && found < 0; z++)
                for (int y = -1; y <= 1 && found < 0; y++)
                for (int x = -1; x <= 1 && found < 0; x++)
                {
                    if (!buckets.TryGetValue(new Vector3Int(home.x + x, home.y + y, home.z + z),
                                             out List<int> list)) continue;

                    for (int k = 0; k < list.Count; k++)
                    {
                        if ((vertices[list[k]] - vertices[i]).sqrMagnitude > radiusSqr) continue;
                        found = list[k];
                        break;
                    }
                }

                if (found >= 0)
                {
                    remap[i] = found;
                    merged++;
                    continue;
                }

                if (!buckets.TryGetValue(home, out List<int> bucket))
                {
                    bucket = new List<int>();
                    buckets[home] = bucket;
                }
                bucket.Add(i);
            }

            if (merged == 0) return false;

            // Compact: survivors keep their order, merged-away vertices disappear.
            var newIndex = new int[n];
            var survivors = new List<Vector3>(n - merged);
            for (int i = 0; i < n; i++)
            {
                if (remap[i] != i) { newIndex[i] = -1; continue; }
                newIndex[i] = survivors.Count;
                survivors.Add(vertices[i]);
            }

            var outTris = new List<int>(triangles.Length);
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = newIndex[remap[triangles[t]]];
                int b = newIndex[remap[triangles[t + 1]]];
                int c = newIndex[remap[triangles[t + 2]]];

                // Two corners merged into one leaves a triangle with no area. Keeping those would
                // hand the mesh degenerate faces whose normals are undefined, which is exactly
                // the "welded/degenerate geometry does occur" case SculptableMesh already has to
                // defend against downstream.
                if (a == b || b == c || a == c) continue;

                outTris.Add(a);
                outTris.Add(b);
                outTris.Add(c);
            }

            weldedVertices = survivors.ToArray();
            weldedTriangles = outTris.ToArray();
            return true;
        }

        private static Vector3Int CellOf(Vector3 p, float cell) => new Vector3Int(
            Mathf.FloorToInt(p.x / cell),
            Mathf.FloorToInt(p.y / cell),
            Mathf.FloorToInt(p.z / cell));
    }
}
