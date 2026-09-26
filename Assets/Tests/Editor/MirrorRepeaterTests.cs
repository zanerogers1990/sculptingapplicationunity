using NUnit.Framework;
using Sculpting;
using UnityEngine;

namespace Sculpting.Tests
{
    /// World-space symmetry (SymmetryOp offsets, MirrorController.Space) and Nomad-style live
    /// mirror copies (MirrorRepeater).
    public class MirrorRepeaterTests
    {
        private GameObject _meshObject;
        private SculptableMesh _sculptable;

        private static readonly Vector3 Position = new Vector3(0.7f, 0.2f, -0.3f);
        private static readonly Quaternion Rotation = Quaternion.Euler(10f, 30f, -20f);
        private const float Scale = 0.8f;

        [OneTimeSetUp]
        public void CreateObjects()
        {
            GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh shared = primitive.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(primitive);

            _meshObject = new GameObject("MirrorRepeaterTestsMesh") { hideFlags = HideFlags.HideAndDontSave };
            _meshObject.AddComponent<MeshFilter>().sharedMesh = shared;
            _meshObject.AddComponent<MeshRenderer>();
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            TestReflection.SetField(_sculptable, "useMeshCollider", false);
            if (_sculptable.Vertices == null) TestReflection.Invoke(_sculptable, "Awake");
            _meshObject.AddComponent<MirrorController>();
        }

        [OneTimeTearDown]
        public void DestroyObjects()
        {
            if (_sculptable != null)
            {
                MirrorRepeater.Set(_sculptable, 0, Vector3.zero, recordUndo: false);
                TestReflection.Invoke(_sculptable, "ReleaseNativeResources");
                if (_sculptable.Mesh != null) Object.DestroyImmediate(_sculptable.Mesh);
            }
            if (_meshObject != null) Object.DestroyImmediate(_meshObject);
        }

        [SetUp]
        public void ResetState()
        {
            _meshObject.transform.SetPositionAndRotation(Position, Rotation);
            _meshObject.transform.localScale = Vector3.one * Scale;
            MirrorRepeater.Set(_sculptable, 0, Vector3.zero, recordUndo: false);
            var mirror = _meshObject.GetComponent<MirrorController>();
            mirror.MirrorX = mirror.MirrorY = mirror.MirrorZ = false;
            mirror.Radial = false;
            mirror.Space = SymmetrySpace.Local;
        }

        private static Vector3 ReflectWorld(Vector3 p, Vector3 signs) => Vector3.Scale(p, signs);

        // ------------------------------------------------------------------ world symmetry

        [Test]
        public void LocalSpaceOpsHaveNoOffset()
        {
            var mirror = _meshObject.GetComponent<MirrorController>();
            mirror.MirrorX = true;
            SymmetryGroup group = mirror.GetSymmetry();
            Assert.That(group.Count, Is.EqualTo(2));
            Assert.That(group[1].HasOffset, Is.False);
            Assert.That(group[1].Diagonal, Is.True);
            Vector3 p = new Vector3(0.1f, 0.2f, 0.3f);
            Assert.That(group[1].ApplyPoint(p), Is.EqualTo(group[1].Apply(p)), "a local op's point map is its linear map, exactly");
        }

        [TestCase(true, false, false, 1)]
        [TestCase(false, true, false, 1)]
        [TestCase(true, false, true, 1)]
        [TestCase(false, false, false, 6)]
        [TestCase(true, false, false, 4)]
        public void WorldSpaceOpsReflectThroughTheWorldOrigin(bool x, bool y, bool z, int radial)
        {
            var mirror = _meshObject.GetComponent<MirrorController>();
            mirror.MirrorX = x; mirror.MirrorY = y; mirror.MirrorZ = z;
            mirror.Radial = radial > 1;
            mirror.RadialCount = Mathf.Max(2, radial);
            mirror.RadialAxisChoice = RadialAxis.Y;
            mirror.Space = SymmetrySpace.World;

            SymmetryGroup local = mirror.GetSymmetry();
            SymmetryGroup world = SymmetryGroup.Build(x, y, z, radial, Vector3.up);
            Assert.That(local.Count, Is.EqualTo(world.Count));
            Assert.That(local.IsGroup, Is.EqualTo(world.IsGroup));

            Transform t = _meshObject.transform;
            Vector3[] verts = _sculptable.Vertices;
            Vector3[] normals = _sculptable.Normals;
            for (int k = 0; k < local.Count; k++)
            {
                for (int i = 0; i < _sculptable.VertexCount; i += 17)
                {
                    // The op in local space must land where the world op sends the world point.
                    Vector3 expected = world[k].ApplyPoint(t.TransformPoint(verts[i]));
                    Vector3 actual = t.TransformPoint(local[k].ApplyPoint(verts[i]));
                    Assert.That(Vector3.Distance(expected, actual), Is.LessThan(1e-5f), $"op {k} vertex {i}");

                    Vector3 expectedN = world[k].Apply(_sculptable.LocalToWorldNormal(normals[i]));
                    Vector3 actualN = _sculptable.LocalToWorldNormal(local[k].Apply(normals[i]));
                    Assert.That(Vector3.Distance(expectedN, actualN), Is.LessThan(1e-5f), $"op {k} normal {i}");

                    Assert.That(Vector3.Distance(local[k].ApplyInversePoint(local[k].ApplyPoint(verts[i])), verts[i]),
                                Is.LessThan(1e-5f), $"op {k} inverse");
                }
            }
        }

        [Test]
        public void WorldSpaceAtTheOriginMatchesLocalExactly()
        {
            _meshObject.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _meshObject.transform.localScale = Vector3.one;
            var mirror = _meshObject.GetComponent<MirrorController>();
            mirror.MirrorX = true;
            mirror.MirrorZ = true;
            SymmetryGroup localGroup = mirror.GetSymmetry();
            var localOps = new SymmetryOp[localGroup.Count];
            for (int k = 0; k < localOps.Length; k++) localOps[k] = localGroup[k];

            mirror.Space = SymmetrySpace.World;
            SymmetryGroup worldGroup = mirror.GetSymmetry();
            for (int k = 0; k < localOps.Length; k++)
                Assert.That(worldGroup[k].Equals(localOps[k]), Is.True, $"op {k}: {worldGroup[k]} vs {localOps[k]}");
        }

        [Test]
        public void WorldSymmetryFollowsTheObject()
        {
            var mirror = _meshObject.GetComponent<MirrorController>();
            mirror.MirrorX = true;
            mirror.Space = SymmetrySpace.World;
            SymmetryOp before = mirror.GetSymmetry()[1];
            _meshObject.transform.position += new Vector3(0.25f, 0f, 0f);
            SymmetryOp after = mirror.GetSymmetry()[1];
            Assert.That(after.Offset, Is.Not.EqualTo(before.Offset), "a moved object must get a rebuilt world group");
        }

        // ------------------------------------------------------------------ live mirror copies

        [Test]
        public void CopiesAreTheWorldReflectionOfTheObject()
        {
            MirrorRepeater.Set(_sculptable, 1 | 4, Vector3.zero, recordUndo: false);
            MirrorRepeater repeater = _sculptable.Repeater;
            Assert.That(repeater, Is.Not.Null);
            Assert.That(repeater.ViewCount, Is.EqualTo(3), "X, Z and XZ");

            Transform t = _meshObject.transform;
            for (int v = 0; v < repeater.ViewCount; v++)
            {
                MirrorRepeaterView view = repeater.View(v);
                Assert.That(view.Filter.sharedMesh, Is.SameAs(_sculptable.Mesh), "a copy draws the object's own mesh");
                Assert.That(view.gameObject.hideFlags, Is.EqualTo(_meshObject.hideFlags));
                for (int i = 0; i < _sculptable.VertexCount; i += 13)
                {
                    Vector3 expected = ReflectWorld(t.TransformPoint(_sculptable.Vertices[i]), view.Signs);
                    Vector3 actual = view.transform.TransformPoint(_sculptable.Vertices[i]);
                    Assert.That(Vector3.Distance(expected, actual), Is.LessThan(1e-5f), $"copy {view.AxisName} vertex {i}");
                }
            }
        }

        [Test]
        public void CopiesFollowAMovedObject()
        {
            MirrorRepeater.Set(_sculptable, 1, Vector3.zero, recordUndo: false);
            _meshObject.transform.position = new Vector3(1.5f, -0.4f, 0.2f);
            _meshObject.transform.rotation = Quaternion.Euler(0f, 70f, 5f);
            _sculptable.Repeater.SyncViews();

            MirrorRepeaterView view = _sculptable.Repeater.View(0);
            Vector3 p = _sculptable.Vertices[5];
            Vector3 expected = ReflectWorld(_meshObject.transform.TransformPoint(p), view.Signs);
            Assert.That(Vector3.Distance(expected, view.transform.TransformPoint(p)), Is.LessThan(1e-5f));
        }

        [Test]
        public void RaycastHitsTheCopyAndMapsBackToTheSameVertices()
        {
            MirrorRepeater.Set(_sculptable, 1, Vector3.zero, recordUndo: false);
            Transform view = _sculptable.Repeater.View(0).transform;
            Vector3 copyCentre = view.position;

            // Straight at the copy's centre from its outer side.
            var ray = new Ray(copyCentre + new Vector3(-5f, 0f, 0f), Vector3.right);
            Assert.That(_sculptable.RaycastAnyCopy(ray, 100f, out Vector3 hit, out Vector3 normal, out Transform frame), Is.True);
            Assert.That(frame, Is.SameAs(view), "the copy is nearer along this ray");
            Assert.That(normal.x, Is.LessThan(0f), "the copy's surface faces back along the ray");

            // The local hit point, seen through the object's own transform, is the reflection.
            Vector3 local = frame.InverseTransformPoint(hit);
            Vector3 original = _meshObject.transform.TransformPoint(local);
            Assert.That(Vector3.Distance(ReflectWorld(original, new Vector3(-1f, 1f, 1f)), hit), Is.LessThan(1e-4f));

            // And the object itself still wins where it is nearer.
            var near = new Ray(Position + new Vector3(5f, 0f, 0f), Vector3.left);
            Assert.That(_sculptable.RaycastAnyCopy(near, 100f, out _, out _, out Transform own), Is.True);
            Assert.That(own, Is.SameAs(_meshObject.transform));
        }

        [Test]
        public void GizmoOnTheCopyMovesTheObjectMirrored()
        {
            MirrorRepeater.Set(_sculptable, 1, Vector3.zero, recordUndo: false);
            var target = new MirrorViewGizmoTarget(_sculptable.Repeater, 0);
            Assert.That(Vector3.Distance(target.Position, _sculptable.Repeater.View(0).transform.position), Is.LessThan(1e-6f));

            target.Position += new Vector3(-0.5f, 0.1f, 0f);
            Assert.That(Vector3.Distance(_meshObject.transform.position, Position + new Vector3(0.5f, 0.1f, 0f)), Is.LessThan(1e-5f),
                "moving the copy left moves the object right");

            Quaternion spun = Quaternion.AngleAxis(25f, Vector3.up) * target.Rotation;
            target.Rotation = spun;
            _sculptable.Repeater.SyncViews();
            Assert.That(Quaternion.Angle(_sculptable.Repeater.View(0).transform.rotation, spun), Is.LessThan(1e-3f));
            Assert.That(target.LocalScale.x, Is.GreaterThan(0f), "reported as the object's positive scale");
        }

        private delegate bool RaycastTargetFn(Ray ray, out Vector3 point, out Vector3 normal);

        [Test]
        public void BrushOnTheCopySculptsTheVerticesTheCopyShows()
        {
            MirrorRepeater.Set(_sculptable, 1, Vector3.zero, recordUndo: false);
            Transform view = _sculptable.Repeater.View(0).transform;

            var cameraObject = new GameObject("MirrorRepeaterTestsCamera") { hideFlags = HideFlags.HideAndDontSave };
            var controllerObject = new GameObject("MirrorRepeaterTestsController") { hideFlags = HideFlags.HideAndDontSave };
            Vector3[] start = (Vector3[])_sculptable.Vertices.Clone();
            try
            {
                cameraObject.transform.position = view.position + new Vector3(-4f, 0f, 0f);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                var controller = controllerObject.AddComponent<SculptController>();
                TestReflection.SetField(controller, "sculptableMesh", _sculptable);
                TestReflection.SetField(controller, "mirrorController", _meshObject.GetComponent<MirrorController>());
                TestReflection.SetField(controller, "cam", camera);
                controller.BrushRadius = 0.15f;
                controller.BrushStrength = 0.5f;

                // Hover: the brush raycast finds the copy and takes its frame.
                var raycast = TestReflection.Bind<RaycastTargetFn>(controller, "RaycastTarget");
                var ray = new Ray(cameraObject.transform.position, Vector3.right);
                Assert.That(raycast(ray, out Vector3 hit, out Vector3 normal), Is.True);
                Assert.That(TestReflection.GetField(controller, "_brushFrame"), Is.SameAs(view), "the brush sculpts through the copy");

                // One Inflate dab there.
                _sculptable.BeginStrokeUndo();
                var mirrored = TestReflection.Bind<System.Action<Vector3, Vector3, bool, float, System.Action<Vector3, Vector3, bool, float>>>(controller, "ApplyMirroredBrush");
                var inflate = TestReflection.Bind<System.Action<Vector3, Vector3, bool, float>>(controller, "ApplyInflateBrushLocal");
                mirrored(hit, normal, true, 1f / 30f, inflate);

                // What moved is what the copy shows under the brush: the original's vertices around
                // the copy's LOCAL hit point, and nothing anywhere else on the mesh.
                Vector3 viewLocal = view.InverseTransformPoint(hit);
                float movedNear = 0f, farthestMoved = 0f;
                for (int i = 0; i < _sculptable.VertexCount; i++)
                {
                    float d = (_sculptable.Vertices[i] - start[i]).magnitude;
                    if (d <= 0f) continue;
                    float fromHit = (start[i] - viewLocal).magnitude;
                    farthestMoved = Mathf.Max(farthestMoved, fromHit);
                    if (fromHit < 0.1f) movedNear = Mathf.Max(movedNear, d);
                }
                Assert.That(movedNear, Is.GreaterThan(1e-4f), "the dab reached the vertices under the copy");
                Assert.That(farthestMoved, Is.LessThan(0.35f), "only vertices around the copy's hit point moved");
                TestReflection.Invoke(controller, "ReleaseNativeResources");
            }
            finally
            {
                System.Array.Copy(start, _sculptable.Vertices, start.Length);
                _sculptable.ApplyVertices();
                Object.DestroyImmediate(controllerObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void RemoveDestroysTheCopies()
        {
            MirrorRepeater.Set(_sculptable, 2, Vector3.zero, recordUndo: false);
            GameObject copy = _sculptable.Repeater.View(0).gameObject;
            MirrorRepeater.Set(_sculptable, 0, Vector3.zero, recordUndo: false);
            Assert.That(_sculptable.Repeater, Is.Null);
            Assert.That(copy == null, Is.True, "the copy's GameObject is gone");
        }
    }
}
