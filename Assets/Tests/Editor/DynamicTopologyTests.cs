using System.Collections.Generic;
using NUnit.Framework;
using Sculpting.DynamicTopology;
using UnityEngine;

namespace Sculpting.Tests
{
    /// Dynamic topology edits the index buffer in place, mid-stroke, thousands of times a second.
    /// The failure mode that matters is not an exception - it is a mesh that still renders and
    /// still raycasts but is quietly no longer a surface: an edge with three faces on it, a
    /// triangle turned inside out, a vertex merged away that the mask was protecting. None of those
    /// announce themselves, and all of them are permanent by the time they are visible. So the
    /// assertions here are structural rather than visual.
    ///
    /// Driven on an icosphere rather than Unity's sphere primitive: the primitive duplicates
    /// vertices along its UV seam (515 vertices for 386 distinct positions), which makes those
    /// edges boundary edges and every closed-surface assertion below meaningless.
    public class DynamicTopologyTests
    {
        private const int Subdivisions = 4;

        private GameObject _meshObject;
        private Mesh _sourceMesh;
        private SculptableMesh _sculptable;
        private DynamicTopologySettings _settings;
        private DynamicTopologyRemesher _remesher;

        // ON the surface, not at unit distance: SymmetricTestMesh builds a radius-0.5 sphere with a
        // low-amplitude ripple on it, so the centre of a brush footprint has to be placed through
        // SurfaceRadius. A region floating clear of the surface refines nothing at all, and every
        // structural assertion below then passes vacuously.
        private static readonly Vector3 RegionDirection = new Vector3(0.2f, 0.3f, 1f).normalized;
        private Vector3 RegionCentre => RegionDirection * SymmetricTestMesh.SurfaceRadius(RegionDirection);
        private const float RegionRadius = 0.18f;

        [SetUp]
        public void CreateObject()
        {
            _sourceMesh = SymmetricTestMesh.BuildIcosphere(Subdivisions, out _);
            _meshObject = new GameObject("DynTopoTarget", typeof(MeshFilter), typeof(MeshRenderer));
            _meshObject.GetComponent<MeshFilter>().sharedMesh = _sourceMesh;
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            // Awake does not run on AddComponent outside Play mode.
            if (_sculptable.Vertices == null) TestReflection.Invoke(_sculptable, "Awake");

            _settings = new DynamicTopologySettings
            {
                Enabled = true,
                // Comfortably under the icosphere's own edge length at this subdivision (about
                // 0.033 at radius 0.5), so a refine has real work to do rather than deciding the
                // mesh is already at target - which would make every assertion here vacuous.
                DetailSize = 0.018f,
                Iterations = 2,
                MaxOperationsPerRefine = 20000,
            };
            _remesher = new DynamicTopologyRemesher(_settings);
            EditHistory.Clear();
        }

        [TearDown]
        public void DestroyObject()
        {
            EditHistory.Clear();
            if (_meshObject != null) Object.DestroyImmediate(_meshObject);
            if (_sourceMesh != null) Object.DestroyImmediate(_sourceMesh);
        }

        private TopologyPatch Refine()
        {
            TopologyPatch patch = _remesher.Refine(_sculptable, RegionCentre, RegionRadius);
            if (patch != null) _sculptable.ApplyTopologyPatch(patch);
            return patch;
        }

        /// A refine that does nothing passes every structural assertion in this file - the mesh is
        /// still manifold, nothing outside the region moved, no triangle inverted. That is exactly
        /// how the first version of these tests went green against a region floating clear of the
        /// surface. Every test that means to exercise the algorithm calls this first.
        private void RefineAndAssertItDidSomething()
        {
            int trianglesBefore = _sculptable.TriangleCount;
            TopologyPatch patch = Refine();
            Assert.That(patch, Is.Not.Null, "the refine produced no patch - is the region on the surface?");
            Assert.That(_sculptable.TriangleCount, Is.GreaterThan(trianglesBefore),
                        "the refine produced a patch but added no triangles");
        }

        // ------------------------------------------------------------------- manifoldness

        /// Every edge of a closed surface belongs to exactly two faces. One means a tear, three or
        /// more a fin - the two shapes a bad split or an illegal collapse produce.
        [Test]
        public void RefinedMeshStaysTwoManifold()
        {
            AssertTwoManifold("before refining");
            RefineAndAssertItDidSomething();
            AssertTwoManifold("after refining");

            // Repeatedly, because the interesting corruptions come from operating on geometry a
            // previous operation already rewired, not from the first edit on a pristine mesh.
            for (int i = 0; i < 4; i++) Refine();
            AssertTwoManifold("after five refines");
        }

        private void AssertTwoManifold(string when)
        {
            int[] tris = _sculptable.Triangles;
            int triangleCount = _sculptable.TriangleCount;
            var edgeUse = new Dictionary<long, int>(EdgeKeyComparer.Instance);

            for (int t = 0; t < triangleCount; t++)
            {
                int b = t * 3;
                // Retired slots are blanked rather than removed - see LocalTopologyEditor.
                if (MeshAdjacency.IsDegenerate(tris, b)) continue;

                for (int k = 0; k < 3; k++)
                {
                    int a = tris[b + k], c = tris[b + (k + 1) % 3];
                    long key = a < c ? ((long)a << 32) | (uint)c : ((long)c << 32) | (uint)a;
                    edgeUse.TryGetValue(key, out int count);
                    edgeUse[key] = count + 1;
                }
            }

            int boundary = 0, nonManifold = 0;
            foreach (KeyValuePair<long, int> pair in edgeUse)
            {
                if (pair.Value == 2) continue;
                if (pair.Value == 1) boundary++;
                else nonManifold++;
            }

            Assert.That(nonManifold, Is.Zero, $"{when}: {nonManifold} edges with more than two faces");
            Assert.That(boundary, Is.Zero, $"{when}: {boundary} edges with only one face - the surface is torn");
        }

        /// A triangle turned inside out still draws, as a black hole in the surface, and flips the
        /// inside/outside sign every voxel operation in this project depends on. On a sphere the
        /// test is exact: every face normal must point away from the centre.
        [Test]
        public void RefineNeverInvertsATriangle()
        {
            RefineAndAssertItDidSomething();
            for (int i = 0; i < 2; i++) Refine();

            Vector3[] verts = _sculptable.Vertices;
            int[] tris = _sculptable.Triangles;
            int triangleCount = _sculptable.TriangleCount;

            int inverted = 0, degenerate = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                int b = t * 3;
                if (MeshAdjacency.IsDegenerate(tris, b)) continue;

                Vector3 p0 = verts[tris[b]], p1 = verts[tris[b + 1]], p2 = verts[tris[b + 2]];
                Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);
                if (normal.sqrMagnitude <= 1e-20f) { degenerate++; continue; }
                if (Vector3.Dot(normal, (p0 + p1 + p2) / 3f) <= 0f) inverted++;
            }

            Assert.That(inverted, Is.Zero, $"{inverted} triangles face inwards after refining");
            Assert.That(degenerate, Is.Zero, $"{degenerate} zero-area triangles after refining");
        }

        // ------------------------------------------------------------------------ locality

        /// The whole justification for this feature is that it costs the footprint, not the mesh.
        /// A vertex well outside the refined region must come back BIT-identical - not merely
        /// close, since "close" is how a whole-mesh relaxation would also read.
        [Test]
        public void RefineLeavesGeometryOutsideTheRegionUntouched()
        {
            var before = (Vector3[])_sculptable.Vertices.Clone();
            int countBefore = _sculptable.VertexCount;

            RefineAndAssertItDidSomething();

            // Outside the region plus its margin, plus one target edge length of slack for the
            // relax pass's one-ring reach.
            float outside = RegionRadius * (1f + _settings.RegionMargin) + _settings.TargetEdgeLength;
            int moved = 0;
            for (int v = 0; v < countBefore; v++)
            {
                if ((before[v] - RegionCentre).magnitude <= outside) continue;
                if (before[v] != _sculptable.Vertices[v]) moved++;
            }

            Assert.That(moved, Is.Zero, $"{moved} vertices outside the refined region moved");
        }

        /// Masking is a promise that geometry does not move. Refining through it would break that
        /// promise in the most destructive way available - by deleting the vertices outright via a
        /// collapse.
        [Test]
        public void FullyMaskedRegionIsNotRefined()
        {
            float[] mask = _sculptable.Mask;
            for (int v = 0; v < _sculptable.VertexCount; v++) mask[v] = 1f;

            int verticesBefore = _sculptable.VertexCount;
            int trianglesBefore = _sculptable.TriangleCount;
            TopologyPatch patch = Refine();

            Assert.That(_sculptable.VertexCount, Is.EqualTo(verticesBefore), "a masked region gained vertices");
            Assert.That(_sculptable.TriangleCount, Is.EqualTo(trianglesBefore), "a masked region gained triangles");
            Assert.That(patch, Is.Null, "a masked region produced a patch");
        }

        /// Mirror-linked halves are kept in step by copying vertex i of one onto vertex i of the
        /// other, which a topology change on one side breaks - so the refine has to stand down
        /// rather than finalize the pair out from under the user.
        [Test]
        public void RefineIsSuspendedWhileMirrorLinked()
        {
            Assert.That(_remesher.CanRefine(_sculptable, BrushType.Clay), Is.True, "baseline");

            var twinObject = new GameObject("Twin", typeof(MeshFilter), typeof(MeshRenderer));
            twinObject.GetComponent<MeshFilter>().sharedMesh = _sourceMesh;
            SculptableMesh twin = twinObject.AddComponent<SculptableMesh>();
            if (twin.Vertices == null) TestReflection.Invoke(twin, "Awake");

            MirrorLink link = MirrorLink.Create(_sculptable, twin, Vector3.zero, new Vector3(-1f, 1f, 1f));
            Assert.That(link, Is.Not.Null, "test setup: the pair should link");
            Assert.That(_remesher.CanRefine(_sculptable, BrushType.Clay), Is.False,
                        "dynamic topology must not run on a mirror-linked half");
            Assert.That(_sculptable.LinkedMirror, Is.Not.Null, "and must not finalize the pair either");

            Object.DestroyImmediate(twinObject);
        }

        /// Move and Pose both capture an immutable index array at drag start and re-derive the
        /// whole drag from it every frame, so changing the vertex count under them aims those
        /// indices at different vertices. Smooth is excluded for a different reason - see
        /// DynamicTopologySettings.AppliesTo.
        [Test]
        public void RefineIsLimitedToTheOptedInBrushes()
        {
            Assert.That(_remesher.CanRefine(_sculptable, BrushType.Move), Is.False, "Move");
            Assert.That(_remesher.CanRefine(_sculptable, BrushType.Pose), Is.False, "Pose");
            Assert.That(_remesher.CanRefine(_sculptable, BrushType.Smooth), Is.False, "Smooth");
            Assert.That(_remesher.CanRefine(_sculptable, BrushType.Clay), Is.True, "Clay");
            Assert.That(_remesher.CanRefine(_sculptable, BrushType.Crease), Is.True, "Crease");
            Assert.That(_remesher.CanRefine(_sculptable, BrushType.Inflate), Is.True, "Inflate");
            Assert.That(_remesher.CanRefine(_sculptable, BrushType.Flatten), Is.True, "Flatten");
        }

        /// Move and Pose refine at the edges of the gesture instead - see
        /// DynamicTopologySettings.RefinesAtStrokeBounds. The two sets must be disjoint: a brush in
        /// both would refine on press AND on every dab, and the per-dab one is exactly what its
        /// captured vertex indices cannot survive.
        [Test]
        public void StrokeBoundRefinementCoversExactlyTheGrabBrushes()
        {
            Assert.That(_remesher.CanRefineAtStrokeBounds(_sculptable, BrushType.Move), Is.True, "Move");
            Assert.That(_remesher.CanRefineAtStrokeBounds(_sculptable, BrushType.Pose), Is.True, "Pose");

            foreach (BrushType brush in System.Enum.GetValues(typeof(BrushType)))
            {
                bool continuous = _remesher.CanRefine(_sculptable, brush);
                bool atBounds = _remesher.CanRefineAtStrokeBounds(_sculptable, brush);
                Assert.That(continuous && atBounds, Is.False, $"{brush} is in both refinement sets");
            }
        }

        /// And neither kind runs on a mirror-linked half.
        [Test]
        public void StrokeBoundRefinementIsAlsoSuspendedWhileMirrorLinked()
        {
            var twinObject = new GameObject("Twin", typeof(MeshFilter), typeof(MeshRenderer));
            twinObject.GetComponent<MeshFilter>().sharedMesh = _sourceMesh;
            SculptableMesh twin = twinObject.AddComponent<SculptableMesh>();
            if (twin.Vertices == null) TestReflection.Invoke(twin, "Awake");

            MirrorLink link = MirrorLink.Create(_sculptable, twin, Vector3.zero, new Vector3(-1f, 1f, 1f));
            Assert.That(link, Is.Not.Null, "test setup: the pair should link");
            Assert.That(_remesher.CanRefineAtStrokeBounds(_sculptable, BrushType.Move), Is.False);

            Object.DestroyImmediate(twinObject);
        }

        // ---------------------------------------------------------------------- convergence

        /// Edge lengths inside the region have to end up between the collapse and split thresholds
        /// and STAY there. If they did not, the two operations would be undoing each other, which
        /// shows up as a mesh whose triangle count oscillates for as long as the brush is held.
        [Test]
        public void EdgeLengthsConvergeAndStopChanging()
        {
            RefineAndAssertItDidSomething();
            for (int i = 0; i < 5; i++) Refine();

            float split = _settings.SplitLength, collapse = _settings.CollapseLength;
            int tooLong = 0, tooShort = 0, considered = 0;

            Vector3[] verts = _sculptable.Vertices;
            int[] tris = _sculptable.Triangles;
            for (int t = 0; t < _sculptable.TriangleCount; t++)
            {
                int b = t * 3;
                if (MeshAdjacency.IsDegenerate(tris, b)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[b + k], c = tris[b + (k + 1) % 3];
                    // Only edges well inside the region: the rim is where refined meets unrefined,
                    // and edges straddling it are meant to be long.
                    if ((verts[a] - RegionCentre).magnitude > RegionRadius * 0.6f) continue;
                    if ((verts[c] - RegionCentre).magnitude > RegionRadius * 0.6f) continue;

                    considered++;
                    float length = (verts[a] - verts[c]).magnitude;
                    if (length > split) tooLong++;
                    else if (length < collapse) tooShort++;
                }
            }

            Assert.That(considered, Is.GreaterThan(50), "test setup: too few interior edges to judge");
            // A small fraction is expected at any moment - the operation cap and the region rim
            // both leave work part-done - but the interior should be overwhelmingly in band.
            Assert.That(tooLong, Is.LessThan(considered * 0.05f), $"{tooLong} of {considered} interior edges still too long");
            Assert.That(tooShort, Is.LessThan(considered * 0.05f), $"{tooShort} of {considered} interior edges still too short");
        }

        /// Refining ground that has already converged has to be nearly free, or a held brush would
        /// go on paying full price for a mesh it is no longer changing.
        [Test]
        public void RepeatedRefineOverTheSameGroundSettles()
        {
            RefineAndAssertItDidSomething();
            int afterFirst = _sculptable.TriangleCount;
            for (int i = 0; i < 5; i++) Refine();
            int afterSix = _sculptable.TriangleCount;

            int growth = afterSix - afterFirst;
            Assert.That(Mathf.Abs(growth), Is.LessThan(afterFirst * 0.25f),
                        $"triangle count is still moving: {afterFirst} -> {afterSix}");
        }

        // ----------------------------------------------------------------------------- undo

        /// One undo press has to take back the stroke AND the topology it created, restoring both
        /// the counts and the positions - and redo has to put both back, which means the vertices
        /// the refine added have to survive somewhere across the undo.
        [Test]
        public void UndoAndRedoRoundTripTopology()
        {
            var verticesBefore = (Vector3[])_sculptable.Vertices.Clone();
            int vertexCountBefore = _sculptable.VertexCount;
            int triangleCountBefore = _sculptable.TriangleCount;

            _sculptable.BeginStrokeUndo();
            // A vertex move alongside the topology change, so the entry exercises both halves.
            _sculptable.RecordUndoBeforeIfNeeded(0);
            _sculptable.Vertices[0] += Vector3.up * 0.01f;

            TopologyPatch patch = _remesher.Refine(_sculptable, RegionCentre, RegionRadius);
            Assert.That(patch, Is.Not.Null, "test setup: the refine should have changed something");
            _sculptable.ApplyTopologyPatch(patch);
            _sculptable.AccumulateStrokeTopology(patch);
            _sculptable.EndStrokeUndo();

            int vertexCountAfter = _sculptable.VertexCount;
            int triangleCountAfter = _sculptable.TriangleCount;
            Assert.That(vertexCountAfter, Is.GreaterThan(vertexCountBefore), "test setup: no vertices added");
            var verticesAfter = (Vector3[])_sculptable.Vertices.Clone();

            Assert.That(EditHistory.Undo(), Is.True, "undo should have something to do");
            Assert.That(_sculptable.VertexCount, Is.EqualTo(vertexCountBefore), "undo left the vertex count wrong");
            Assert.That(_sculptable.TriangleCount, Is.EqualTo(triangleCountBefore), "undo left the triangle count wrong");
            for (int v = 0; v < vertexCountBefore; v++)
                Assert.That(_sculptable.Vertices[v], Is.EqualTo(verticesBefore[v]), $"undo left vertex {v} moved");

            Assert.That(EditHistory.Redo(), Is.True, "redo should have something to do");
            Assert.That(_sculptable.VertexCount, Is.EqualTo(vertexCountAfter), "redo left the vertex count wrong");
            Assert.That(_sculptable.TriangleCount, Is.EqualTo(triangleCountAfter), "redo left the triangle count wrong");
            for (int v = 0; v < vertexCountAfter; v++)
                Assert.That(_sculptable.Vertices[v], Is.EqualTo(verticesAfter[v]), $"redo left vertex {v} wrong");
        }

        /// Undoing a topology stroke leaves a mesh that is still a surface. Truncation is the one
        /// operation here that can leave a triangle pointing at a vertex that no longer exists.
        [Test]
        public void UndoLeavesAValidSurface()
        {
            _sculptable.BeginStrokeUndo();
            TopologyPatch patch = _remesher.Refine(_sculptable, RegionCentre, RegionRadius);
            Assert.That(patch, Is.Not.Null, "test setup: the refine should have changed something");
            _sculptable.ApplyTopologyPatch(patch);
            _sculptable.AccumulateStrokeTopology(patch);
            _sculptable.EndStrokeUndo();

            Assert.That(EditHistory.Undo(), Is.True);
            AssertTwoManifold("after undo");

            int[] tris = _sculptable.Triangles;
            for (int t = 0; t < _sculptable.TriangleCount; t++)
            {
                int b = t * 3;
                for (int k = 0; k < 3; k++)
                    Assert.That(tris[b + k], Is.LessThan(_sculptable.VertexCount),
                                $"triangle {t} references a vertex the undo removed");
            }
        }

        /// The mask is indexed by vertex, and a refine that shifted what an index meant would move
        /// protection onto different geometry. Nothing here renumbers, so a mask painted before a
        /// refine has to describe exactly the same surface afterwards.
        [Test]
        public void MaskSurvivesARefine()
        {
            float[] mask = _sculptable.Mask;
            var marked = new List<int>();
            Vector3[] verts = _sculptable.Vertices;
            // The far side, well outside the refined region, so this is about index stability
            // rather than about the refine's own mask handling.
            for (int v = 0; v < _sculptable.VertexCount; v++)
            {
                if ((verts[v] - RegionCentre).magnitude < 0.6f) continue;
                mask[v] = 0.75f;
                marked.Add(v);
            }
            Assert.That(marked.Count, Is.GreaterThan(20), "test setup: nothing masked");

            var positionsBefore = new Vector3[marked.Count];
            for (int i = 0; i < marked.Count; i++) positionsBefore[i] = verts[marked[i]];

            RefineAndAssertItDidSomething();

            for (int i = 0; i < marked.Count; i++)
            {
                int v = marked[i];
                Assert.That(_sculptable.Mask[v], Is.EqualTo(0.75f), $"vertex {v} lost its mask value");
                Assert.That(_sculptable.Vertices[v], Is.EqualTo(positionsBefore[i]),
                            $"vertex {v} is no longer the vertex the mask was painted on");
            }
        }
    }
}
