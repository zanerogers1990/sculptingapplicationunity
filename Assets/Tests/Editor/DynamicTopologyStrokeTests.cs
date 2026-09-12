using System;
using NUnit.Framework;
using UnityEngine;

namespace Sculpting.Tests
{
    /// End to end, through the production stroke path rather than by calling the remesher
    /// directly: a real Clay stroke, applied through the same per-frame entry point a held mouse
    /// drives, with dynamic topology switched on the way a user switches it on.
    ///
    /// This is the half DynamicTopologyTests cannot reach. That suite proves the ALGORITHM is
    /// correct; this one proves it is actually wired in - that the hook in PushDirtyVertices fires,
    /// that the region it derives from the dirty set lands on the geometry the brush just moved,
    /// that the throttle does not suppress everything, and that the stroke's undo entry carries the
    /// topology with it.
    public class DynamicTopologyStrokeTests
    {
        private const int Subdivisions = 4;
        private const int StrokeFrames = 12;

        private GameObject _meshObject, _controllerObject, _cameraObject;
        private Mesh _sourceMesh;
        private SculptableMesh _sculptable;
        private SculptController _controller;

        [SetUp]
        public void CreateObjects()
        {
            _sourceMesh = SymmetricTestMesh.BuildIcosphere(Subdivisions, out _);

            _meshObject = new GameObject("DynTopoStrokeMesh") { hideFlags = HideFlags.HideAndDontSave };
            _meshObject.AddComponent<MeshFilter>().sharedMesh = _sourceMesh;
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            TestReflection.SetField(_sculptable, "useMeshCollider", false);
            if (_sculptable.Vertices == null) TestReflection.Invoke(_sculptable, "Awake");

            _cameraObject = new GameObject("DynTopoStrokeCamera") { hideFlags = HideFlags.HideAndDontSave };
            _cameraObject.transform.position = new Vector3(1.2f, 0.35f, -1.6f);
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.enabled = false;

            _controllerObject = new GameObject("DynTopoStrokeController") { hideFlags = HideFlags.HideAndDontSave };
            _controller = _controllerObject.AddComponent<SculptController>();
            TestReflection.SetField(_controller, "sculptableMesh", _sculptable);
            TestReflection.SetField(_controller, "cam", camera);

            _controller.DynamicTopologyEnabled = true;
            _controller.DynamicTopologyDetailSize = 0.018f;
            EditHistory.Clear();
        }

        [TearDown]
        public void DestroyObjects()
        {
            EditHistory.Clear();
            if (_controller != null) TestReflection.Invoke(_controller, "ReleaseNativeResources");
            if (_sculptable != null)
            {
                TestReflection.Invoke(_sculptable, "ReleaseNativeResources");
                if (_sculptable.Mesh != null && _sculptable.Mesh != _sourceMesh) UnityEngine.Object.DestroyImmediate(_sculptable.Mesh);
            }
            if (_controllerObject != null) UnityEngine.Object.DestroyImmediate(_controllerObject);
            if (_cameraObject != null) UnityEngine.Object.DestroyImmediate(_cameraObject);
            if (_meshObject != null) UnityEngine.Object.DestroyImmediate(_meshObject);
            if (_sourceMesh != null) UnityEngine.Object.DestroyImmediate(_sourceMesh);
        }

        /// A Clay stroke with dynamic topology on has to end with more triangles than it started
        /// with, and one undo press has to put the mesh back exactly - shape AND count.
        [Test]
        public void ClayStrokeRefinesAndUndoesInOnePress()
        {
            int trianglesBefore = _sculptable.TriangleCount;
            int verticesBefore = _sculptable.VertexCount;
            var positionsBefore = (Vector3[])_sculptable.Vertices.Clone();

            RunClayStroke();

            Assert.That(_sculptable.TriangleCount, Is.GreaterThan(trianglesBefore),
                        "the stroke added no triangles - the hook in PushDirtyVertices did not fire");
            Assert.That(EditHistory.CanUndo, Is.True, "the stroke recorded nothing to undo");

            Assert.That(EditHistory.Undo(), Is.True);
            Assert.That(_sculptable.TriangleCount, Is.EqualTo(trianglesBefore), "undo left triangles behind");
            Assert.That(_sculptable.VertexCount, Is.EqualTo(verticesBefore), "undo left vertices behind");
            for (int v = 0; v < verticesBefore; v++)
                Assert.That(_sculptable.Vertices[v], Is.EqualTo(positionsBefore[v]), $"undo left vertex {v} moved");

            // And exactly one press: a stroke that refined a dozen times must not need a dozen
            // undos to take back.
            Assert.That(EditHistory.CanUndo, Is.False, "the stroke left more than one undo step");
        }

        /// With the feature off, the same stroke must leave topology completely alone - the switch
        /// has to be a real switch, not just a change of degree.
        [Test]
        public void StrokeLeavesTopologyAloneWhenDisabled()
        {
            _controller.DynamicTopologyEnabled = false;

            int trianglesBefore = _sculptable.TriangleCount;
            int verticesBefore = _sculptable.VertexCount;
            RunClayStroke();

            Assert.That(_sculptable.TriangleCount, Is.EqualTo(trianglesBefore), "triangles changed with the feature off");
            Assert.That(_sculptable.VertexCount, Is.EqualTo(verticesBefore), "vertices changed with the feature off");
        }

        /// Smooth is not on the opt-in list, so a Smooth stroke must not refine even when the
        /// feature is on - see DynamicTopologySettings.AppliesTo.
        [Test]
        public void SmoothStrokeDoesNotRefine()
        {
            _controller.CurrentBrush = BrushType.Smooth;
            int trianglesBefore = _sculptable.TriangleCount;

            var smooth = TestReflection.Bind<Action<Vector3, float>>(_controller, "ApplySmoothBrush");
            BeginStroke();
            for (int f = 0; f < StrokeFrames; f++) smooth(StrokePoint(f), 1f / 60f);

            Assert.That(_sculptable.TriangleCount, Is.EqualTo(trianglesBefore), "Smooth refined the mesh");
        }

        /// The surface a refined stroke leaves has to still be a surface. This is the assertion
        /// that would catch a hook firing at the wrong moment - mid-dab, say, with the dirty set
        /// half built - which the algorithm's own tests cannot see because they never run it from
        /// inside a stroke.
        [Test]
        public void RefinedStrokeLeavesATwoManifoldSurface()
        {
            RunClayStroke();

            int[] tris = _sculptable.Triangles;
            var edgeUse = new System.Collections.Generic.Dictionary<long, int>(EdgeKeyComparer.Instance);
            for (int t = 0; t < _sculptable.TriangleCount; t++)
            {
                int b = t * 3;
                if (MeshAdjacency.IsDegenerate(tris, b)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[b + k], c = tris[b + (k + 1) % 3];
                    long key = a < c ? ((long)a << 32) | (uint)c : ((long)c << 32) | (uint)a;
                    edgeUse.TryGetValue(key, out int count);
                    edgeUse[key] = count + 1;
                }
            }

            int bad = 0;
            foreach (System.Collections.Generic.KeyValuePair<long, int> pair in edgeUse)
                if (pair.Value != 2) bad++;
            Assert.That(bad, Is.Zero, $"{bad} edges are not shared by exactly two faces after a refined stroke");
        }

        /// The regression test for the bug that made refined strokes tear the model into spikes.
        ///
        /// Clay's surface-relax pass is a Burst job, and it reads a neighbour that is outside its
        /// own candidate list out of a full-mesh POSITION MIRROR. A refine appends vertices into the
        /// spare capacity of the array the mirror was copied from, so the array reference does not
        /// change and the mirror was not being refreshed - leaving the new vertices at the mirror's
        /// allocated value, which is zero. Every relaxation that touched one averaged toward the
        /// ORIGIN, and the surface grew long spikes converging on the middle of the object.
        ///
        /// Asserted as distance from the origin rather than as displacement, because that is the
        /// shape of the failure: on a sphere of radius ~0.5 a corrupted vertex lands near 0 while a
        /// legitimately sculpted one stays near the surface.
        [Test]
        public void RefinedStrokeDoesNotPullVerticesTowardTheOrigin([Values(false, true)] bool burstJobs)
        {
            _controller.UseBurstJobs = burstJobs;
            _controller.BrushStrength = 0.6f;

            RunClayStroke();

            float nearest = float.MaxValue;
            int worst = -1;
            for (int v = 0; v < _sculptable.VertexCount; v++)
            {
                float radius = _sculptable.Vertices[v].magnitude;
                if (radius >= nearest) continue;
                nearest = radius;
                worst = v;
            }

            // Half the sphere's radius. A stroke cannot plausibly push the surface that far in, and
            // a vertex averaged toward the origin lands one or two orders of magnitude closer.
            Assert.That(nearest, Is.GreaterThan(SymmetricTestMesh.SphereRadius * 0.5f),
                        $"vertex {worst} sits {nearest:E3} from the origin after a refined stroke " +
                        $"(jobs={burstJobs}) - the full-mesh position mirror is stale");
        }

        // --------------------------------------------------------------------------- harness

        private void BeginStroke()
        {
            // What HandleSculptInput does on the press frame, before the first dab.
            _sculptable.PrepareSpatialIndex(Mathf.Max(_controller.BrushRadius * 0.5f, 0.01f));
            _sculptable.BeginStrokeUndo();
            TestReflection.Invoke(TestReflection.GetField(_controller, "_strokeDirtyVertexScratch"),
                                  "Clear", _sculptable.VertexCount);
            TestReflection.SetField(_controller, "_lastClayStrokeLocal", null);
            TestReflection.SetField(_controller, "_lastClayStrokeNormalLocal", null);
            TestReflection.SetField(_controller, "_lastCarveStrokeLocal", null);
        }

        private void RunClayStroke()
        {
            _controller.CurrentBrush = BrushType.Clay;
            BeginStroke();

            var clay = TestReflection.Bind<Action<Vector3, Vector3, bool>>(_controller, "ApplyClayStroke");
            for (int f = 0; f < StrokeFrames; f++)
            {
                Vector3 dir = StrokeDirection(f);
                clay(dir * SymmetricTestMesh.SurfaceRadius(dir), dir, true);
            }

            // What HandleStrokeEndCommit does on release.
            TestReflection.Invoke(_controller, "ApplyPostStrokeUnifyPass");
            _sculptable.RefreshStrokeNormalsAndCurvature();
            _sculptable.ReseatColliderIfTopologyChanged();
            _sculptable.EndStrokeUndo();
        }

        // A band around the sphere, far enough per frame that the refine throttle lets several
        // refines through over the stroke.
        private static Vector3 StrokeDirection(int frame)
        {
            float angle = -0.9f + 0.16f * frame;
            return new Vector3(0.25f, Mathf.Sin(angle) * 0.6f, Mathf.Cos(angle)).normalized;
        }

        private Vector3 StrokePoint(int frame)
        {
            Vector3 dir = StrokeDirection(frame);
            return dir * SymmetricTestMesh.SurfaceRadius(dir);
        }
    }
}
