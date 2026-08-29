using UnityEngine;

namespace Sculpting
{
    /// Applies SymmetryTools to a live SculptableMesh: builds the correspondence map from the
    /// object's working vertices, runs a repair, then takes the right rebuild path for what
    /// changed. A static helper rather than a MonoBehaviour, matching MeshMirror, MeshCloner and
    /// MeshJoiner - the settings that drive it (axis, tolerance) live on SculptController with
    /// every other UI-facing setting, and nothing here needs per-object state of its own.
    ///
    /// The one rule worth stating up front: a vertex-only repair finishes with ApplyVertices,
    /// while a weld changes topology and must go through ReplaceMesh, which rebuilds adjacency,
    /// the raycast grid, cavity, mask and the GPU scatter binding. Taking the cheap path after a
    /// topology change would leave every one of those describing a mesh that no longer exists.
    public static class SymmetryOps
    {
        /// Multiplier range offered in the UI, around SymmetryMap.DefaultTolerance. Wide enough
        /// to pair a coarsely-remeshed model (whose two halves land further apart) without being
        /// so wide that it starts pairing a vertex with its reflection's neighbour.
        public const float MinToleranceScale = 0.25f;
        public const float MaxToleranceScale = 8f;

        /// The most of a model that may be left UNPAIRED before MakeSymmetric refuses to run,
        /// as a fraction of the off-centreline vertices.
        ///
        /// This exists because a partial mirror is not a partial repair - it is a tear. Copying
        /// one side onto the other only moves vertices that HAVE a counterpart; every unmatched
        /// vertex stays exactly where it was. Where the two groups are interleaved across the
        /// same surface, half the vertices jump to their mirrored positions and their immediate
        /// neighbours do not, and the surface between them is pulled into ribbons. That is not a
        /// near-miss that a second press or a different direction can settle - running the other
        /// direction afterwards tears it again from the other side.
        ///
        /// Real-world case this is calibrated against: a body with two arms joined in, one of
        /// them rotated, measured at 71427 vertices - 21493 pairs, 4768 on the centreline, and
        /// 23673 unmatched (35% of the off-plane vertices). Every pair the map made was an exact
        /// reflection; the map was not the problem and no tolerance setting changed any of these
        /// numbers. There was simply no correspondence to repair through for a third of the
        /// model, and mirroring through the third that did pair shredded it.
        ///
        /// 5% leaves room for the drift this tool is FOR (two halves that pair almost everywhere
        /// and disagree slightly) while catching the case above, where the halves are not two
        /// versions of the same thing at all.
        public const float MaxUnmatchedFraction = 0.05f;

        /// MakeSymmetric's return value when the model is too asymmetric to repair by mirroring
        /// (see MaxUnmatchedFraction). Distinct from -1/"no geometry" because the two need
        /// completely different advice.
        public const int TooAsymmetric = -2;

        /// How close two points must be, as a fraction of SymmetryMap.DefaultTolerance, to count
        /// as literally the same point during a cleanup weld. Small enough that only vertices a
        /// mesh format split apart on purpose - UV seams, poles - land inside it; a default Unity
        /// sphere carries 129 such duplicates, and merging them is what lets it pair perfectly.
        /// Nothing a person sculpts ends up this close by accident.
        public const float CoincidentFraction = 0.02f;

        public static SymmetryMap BuildMap(SculptableMesh mesh, int axis, float toleranceScale)
        {
            if (mesh == null) return null;
            Vector3[] verts = mesh.Vertices;
            if (verts == null || verts.Length == 0) return null;

            float tolerance = SymmetryMap.DefaultTolerance(verts) * Mathf.Max(toleranceScale, 0.01f);
            return SymmetryMap.Build(verts, axis, tolerance);
        }

        /// One-line symmetry report for the UI. Rebuilt on demand rather than cached: the map is
        /// invalidated by literally every brush stroke, and a cached figure that silently
        /// described the mesh as it was several strokes ago would be worse than no figure at all.
        public static string Status(SculptableMesh mesh, int axis, float toleranceScale)
        {
            if (mesh == null) return "No object selected";
            SymmetryMap map = BuildMap(mesh, axis, toleranceScale);
            return map == null ? "No geometry" : map.Summary();
        }

        /// Copies one side onto the other through the correspondence map, pinning the centreline
        /// first so the seam cannot drift (see SymmetryTools.SnapToPlane for why that order
        /// matters). Vertex-only - the topology is untouched, so this can never punch a hole in
        /// the model, which is what makes it safe to offer as a one-click repair.
        ///
        /// Returns the number of vertices rewritten, -1 if there was nothing to work on, or
        /// TooAsymmetric if mirroring would tear the model rather than repair it (see
        /// MaxUnmatchedFraction - nothing is modified in that case).
        ///
        /// `pairCount` and `unmatchedCount` come back alongside because the outcomes need
        /// completely different advice, and the count alone cannot distinguish them: the map
        /// paired nothing (tolerance too tight, or the halves really are unrelated), versus the
        /// map paired plenty and the model was already symmetric, versus the map paired plenty
        /// and left a third of the model with no counterpart at all. Reporting one case's message
        /// for another sends the user off adjusting a slider that will not change anything.
        public static int MakeSymmetric(SculptableMesh mesh, int axis, float toleranceScale,
                                        bool sourceIsPositive, out int pairCount, out int unmatchedCount)
        {
            pairCount = 0;
            unmatchedCount = 0;

            SymmetryMap map = BuildMap(mesh, axis, toleranceScale);
            if (map == null) return -1;
            pairCount = map.PairCount;
            unmatchedCount = map.UnmatchedCount;

            // Refuse rather than half-apply. Measured against off-plane vertices only: the
            // centreline is symmetric by definition and would otherwise dilute the fraction on a
            // model with a dense seam, making a torn result look acceptable.
            int offPlane = map.VertexCount - map.OnPlaneCount;
            if (offPlane > 0 && unmatchedCount > offPlane * MaxUnmatchedFraction) return TooAsymmetric;

            // Computed against a copy and only committed if it changes something - see Cleanup
            // for why a no-op must not leave an undo step behind.
            Vector3[] live = mesh.Vertices;
            var working = (Vector3[])live.Clone();

            int snapped = SymmetryTools.SnapToPlane(working, map);
            int changed = SymmetryTools.MakeSymmetric(working, map, sourceIsPositive);
            if (snapped == 0 && changed == 0) return 0;

            // A topology-preserving edit still needs a full snapshot: nothing here goes through
            // the stroke-delta path that ordinary brushing uses.
            mesh.SnapshotForUndo();
            System.Array.Copy(working, live, live.Length);
            mesh.ApplyVertices();
            return changed;
        }

        /// Cuts the model at the mirror plane and rebuilds the discarded side as a reflection of
        /// the kept one - SymmetryTools.MirrorAndWeld applied to a live object.
        ///
        /// This is the answer for everything MakeSymmetric refuses. MakeSymmetric can only move
        /// vertices that already have a counterpart across the plane, so on a model whose two
        /// halves were built separately - two arms joined in, one side remeshed, an imported mesh
        /// that never matched - it correctly declines rather than tearing the surface, and from
        /// the outside that reads as "symmetry does nothing". Nothing about a correspondence map
        /// can fix that case: the correspondence genuinely is not there. Throwing one side away
        /// and reflecting the other always can, because it never needs to know which vertex was
        /// which.
        ///
        /// Goes through ReplaceMesh, not ApplyVertices - the vertex count and the triangle list
        /// both change, so adjacency, the raycast grid, cavity, mask and the GPU scatter binding
        /// all have to be rebuilt (see this class's remarks).
        ///
        /// Returns false only when there was nothing to do: no geometry, or nothing at all on the
        /// side being kept. `keptTriangles`/`discardedTriangles` describe the cut, and
        /// `vertexCount` is the size of the result.
        public static bool MirrorAndWeld(SculptableMesh mesh, int axis, float toleranceScale,
                                         bool sourceIsPositive,
                                         out int keptTriangles, out int discardedTriangles, out int vertexCount)
        {
            keptTriangles = 0;
            discardedTriangles = 0;
            vertexCount = 0;
            if (mesh == null) return false;

            Vector3[] verts = mesh.Vertices;
            if (verts == null || verts.Length == 0) return false;

            // Same seam band the pairing uses, so the "Match Tolerance" slider means one
            // consistent thing across the whole panel: how far off the centreline a vertex may
            // sit and still count as being on it.
            float seam = SymmetryMap.DefaultTolerance(verts) * Mathf.Max(toleranceScale, 0.01f);

            if (!SymmetryTools.MirrorAndWeld(verts, mesh.Triangles, axis, sourceIsPositive, seam,
                                             out Vector3[] newVerts, out int[] newTris,
                                             out keptTriangles, out discardedTriangles))
                return false;

            mesh.SnapshotForUndo();

            var rebuilt = new Mesh();
            // Same threshold the rest of this file uses - a 16-bit index buffer silently wraps
            // past 65k vertices instead of failing loudly.
            if (newVerts.Length > 65000) rebuilt.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            rebuilt.vertices = newVerts;
            rebuilt.triangles = newTris;
            rebuilt.RecalculateNormals();
            rebuilt.RecalculateBounds();

            mesh.ReplaceMesh(rebuilt);
            vertexCount = newVerts.Length;
            return true;
        }

        /// The symmetry cleanup pass: snap near-centreline vertices exactly onto the plane, then
        /// weld the duplicates that leaves behind, so a seam that was two coincident shells
        /// becomes one shared edge loop.
        ///
        /// `snappedCount` is how many vertices were pulled onto the plane, `weldedCount` how many
        /// were then merged away. Both zero means the model was already clean. Returns false only
        /// when there was no geometry to work on at all.
        public static bool Cleanup(SculptableMesh mesh, int axis, float toleranceScale,
                                   out int snappedCount, out int weldedCount)
        {
            snappedCount = 0;
            weldedCount = 0;

            SymmetryMap map = BuildMap(mesh, axis, toleranceScale);
            if (map == null) return false;

            Vector3[] live = mesh.Vertices;

            // Everything is computed against a COPY first, and nothing is committed until we know
            // the model will actually change. Snapshotting up front would push an undo step for a
            // Cleanup that turned out to have nothing to do - and an Undo press that visibly does
            // nothing is exactly the failure EditHistory goes out of its way to avoid elsewhere
            // (see its TakeStep remarks). The clone costs one array copy, against a weld that
            // allocates whole new vertex and index arrays anyway.
            var working = (Vector3[])live.Clone();
            snappedCount = SymmetryTools.SnapToPlane(working, map);

            // Deliberately derived from the mesh alone rather than from map.Tolerance: the Match
            // Tolerance slider widens what counts as a PAIR, which is a judgement about how far
            // apart two halves may have drifted and still mean the same thing. How close two
            // points must be to be the SAME point is not that judgement, and letting the slider
            // scale it would turn a loose pairing setting into a licence to collapse real
            // geometry (see SymmetryTools.Weld on why the second radius stays small).
            float coincident = SymmetryMap.DefaultTolerance(working) * CoincidentFraction;

            bool didWeld = SymmetryTools.Weld(working, mesh.Triangles, axis, map.Tolerance, coincident,
                                              out Vector3[] weldedVerts, out int[] weldedTris);

            if (snappedCount == 0 && !didWeld) return true; // already clean - nothing committed

            mesh.SnapshotForUndo();

            if (!didWeld)
            {
                // Snapping alone moved geometry, so the mesh still has to be pushed even though
                // no vertex was merged.
                System.Array.Copy(working, live, live.Length);
                mesh.ApplyVertices();
                return true;
            }

            weldedCount = working.Length - weldedVerts.Length;

            var welded = new Mesh();
            // Same threshold MeshMirror and MeshRemesher already use - a 16-bit index buffer
            // silently wraps past 65k vertices instead of failing loudly.
            if (weldedVerts.Length > 65000) welded.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            welded.vertices = weldedVerts;
            welded.triangles = weldedTris;
            welded.RecalculateNormals();
            welded.RecalculateBounds();

            mesh.ReplaceMesh(welded);
            return true;
        }

        public static string AxisName(int axis) =>
            axis == SymmetryMap.AxisX ? "X" : (axis == SymmetryMap.AxisY ? "Y" : "Z");
    }
}
