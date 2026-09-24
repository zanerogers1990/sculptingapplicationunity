using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// SculptableMesh.FilterMask: Grow/Shrink move the mask edge by edge rings, Blur softens it,
    /// Sharpen tightens it, a symmetric mask stays symmetric, and each call is one undo step.
    public class MaskFilterTests
    {
        private GameObject _meshObject;
        private Mesh _sourceMesh;
        private SculptableMesh _sculptable;
        private int[][] _partners;

        [OneTimeSetUp]
        public void CreateObjects()
        {
            _sourceMesh = SymmetricTestMesh.BuildIcosphere(4, out _partners);
            _meshObject = new GameObject("MaskFilterMesh") { hideFlags = HideFlags.HideAndDontSave };
            _meshObject.AddComponent<MeshFilter>().sharedMesh = _sourceMesh;
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            TestReflection.SetField(_sculptable, "useMeshCollider", false);
            if (_sculptable.Vertices == null) TestReflection.Invoke(_sculptable, "Awake");
        }

        [OneTimeTearDown]
        public void DestroyObjects()
        {
            if (_sculptable != null)
            {
                TestReflection.Invoke(_sculptable, "ReleaseNativeResources");
                if (_sculptable.Mesh != null && _sculptable.Mesh != _sourceMesh) Object.DestroyImmediate(_sculptable.Mesh);
            }
            if (_meshObject != null) Object.DestroyImmediate(_meshObject);
            if (_sourceMesh != null) Object.DestroyImmediate(_sourceMesh);
        }

        /// A hard-edged cap: everything above y = 0.2 fully masked.
        private float[] MaskCap()
        {
            int n = _sculptable.VertexCount;
            var mask = new float[n];
            Vector3[] verts = _sculptable.Vertices;
            for (int i = 0; i < n; i++) mask[i] = verts[i].y > 0.2f ? 1f : 0f;
            _sculptable.SetMask(mask);
            return mask;
        }

        private static int CountAbove(float[] mask, int n, float threshold)
        {
            int count = 0;
            for (int i = 0; i < n; i++) if (mask[i] > threshold) count++;
            return count;
        }

        private static int CountSoft(float[] mask, int n)
        {
            int count = 0;
            for (int i = 0; i < n; i++) if (mask[i] > 0.02f && mask[i] < 0.98f) count++;
            return count;
        }

        [Test]
        public void GrowThenShrinkMovesTheEdgeAndComesBack()
        {
            float[] start = MaskCap();
            int n = _sculptable.VertexCount;
            int before = CountAbove(start, n, 0.5f);

            _sculptable.FilterMask(SculptableMesh.MaskFilter.Grow, 2);
            int grown = CountAbove(_sculptable.Mask, n, 0.5f);
            Assert.That(grown, Is.GreaterThan(before), "Grow did not add masked vertices.");

            _sculptable.FilterMask(SculptableMesh.MaskFilter.Shrink, 2);
            // A morphological closing: never loses any of the original, and on a smooth cap gives
            // back all but a few vertices in the nooks of its jagged edge.
            for (int i = 0; i < n; i++) Assert.That(_sculptable.Mask[i], Is.GreaterThanOrEqualTo(start[i]), $"vertex {i}");
            Assert.That(CountAbove(_sculptable.Mask, n, 0.5f) - before, Is.LessThan(before / 50 + 1));
        }

        [Test]
        public void BlurSoftensAndSharpenTightens()
        {
            MaskCap();
            int n = _sculptable.VertexCount;
            Assert.That(CountSoft(_sculptable.Mask, n), Is.EqualTo(0));

            _sculptable.FilterMask(SculptableMesh.MaskFilter.Blur, 4);
            int soft = CountSoft(_sculptable.Mask, n);
            Assert.That(soft, Is.GreaterThan(0), "Blur left the edge hard.");

            _sculptable.FilterMask(SculptableMesh.MaskFilter.Sharpen, 4);
            int sharpened = CountSoft(_sculptable.Mask, n);
            Assert.That(sharpened, Is.LessThan(soft), "Sharpen did not narrow the soft band.");
        }

        [Test]
        public void FiltersKeepASymmetricMaskSymmetric()
        {
            // A cap tilted toward +X and its mirror image: symmetric about x = 0, but not aligned
            // with any vertex ordering.
            int n = _sculptable.VertexCount;
            Vector3[] verts = _sculptable.Vertices;
            var mask = new float[n];
            for (int i = 0; i < n; i++) mask[i] = Mathf.Abs(verts[i].x) * 0.8f + verts[i].y > 0.15f ? 1f : 0f;
            _sculptable.SetMask(mask);

            foreach (SculptableMesh.MaskFilter filter in Enum.GetValues(typeof(SculptableMesh.MaskFilter)))
                _sculptable.FilterMask(filter, 3);

            float[] m = _sculptable.Mask;
            for (int i = 0; i < n; i++)
                if (_partners[0][i] >= 0)
                    Assert.That(m[_partners[0][i]], Is.EqualTo(m[i]), $"vertex {i} vs its mirror {_partners[0][i]}");
        }

        [Test]
        public void EachFilterIsOneUndoStep()
        {
            float[] start = MaskCap();
            _sculptable.FilterMask(SculptableMesh.MaskFilter.Grow, 3);
            Assert.That(_sculptable.ApplyUndoStep(), Is.True);
            for (int i = 0; i < _sculptable.VertexCount; i++)
                Assert.That(_sculptable.Mask[i], Is.EqualTo(start[i]), $"vertex {i}");
        }
    }
}
