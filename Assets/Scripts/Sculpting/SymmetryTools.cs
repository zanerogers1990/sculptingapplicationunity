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
        /// Unmatched vertices are deliberately left untouched rather than approximated - the map
        /// found no counterpart for them, so any position written there would be a guess, and a
        /// guess is how a "cleanup" tool ends up mangling geometry it was pointed at by mistake.
        /// The count of what was skipped is reported back so the UI can say so.
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
