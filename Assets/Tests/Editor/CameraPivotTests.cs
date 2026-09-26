using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// CameraOrbitController's stroke pivot: an anchored orbit or zoom must leave the anchor on
    /// the same pixel, in both projections, and an unanchored one must behave exactly as before.
    public class CameraPivotTests
    {
        private GameObject _rig;
        private Camera _cam;
        private CameraOrbitController _orbit;

        // Off-centre and off the orbit pivot on purpose: an anchor AT the pivot would pass even
        // with the anchoring doing nothing.
        private static readonly Vector3 Anchor = new Vector3(0.35f, 0.2f, -0.3f);
        private const float PixelTolerance = 1e-4f; // in viewport units

        [SetUp]
        public void CreateRig()
        {
            _rig = new GameObject("CameraPivotTestRig") { hideFlags = HideFlags.HideAndDontSave };
            _cam = _rig.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.fieldOfView = 60f;
            _cam.aspect = 16f / 9f;
            _orbit = _rig.AddComponent<CameraOrbitController>();
            _orbit.SetView(20f, 15f, 4f, Vector3.zero);
        }

        [TearDown]
        public void DestroyRig()
        {
            if (_rig != null) Object.DestroyImmediate(_rig);
        }

        /// Pushes the rig's state into the transform - SetView ends in UpdateTransform.
        private void Apply()
        {
            _orbit.GetView(out float yaw, out float pitch, out float distance, out Vector3 pivot);
            _orbit.SetView(yaw, pitch, distance, pivot);
        }

        private void AssertAnchorHeld(Vector3 before, string step)
        {
            Vector3 after = _cam.WorldToViewportPoint(Anchor);
            Assert.That(Mathf.Abs(after.x - before.x), Is.LessThan(PixelTolerance), $"{step}: x moved {before} -> {after}");
            Assert.That(Mathf.Abs(after.y - before.y), Is.LessThan(PixelTolerance), $"{step}: y moved {before} -> {after}");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnchoredOrbitKeepsAnchorOnItsPixel(bool orthographic)
        {
            _orbit.Orthographic = orthographic;
            Apply();
            Vector3 before = _cam.WorldToViewportPoint(Anchor);

            float[][] steps = { new[] { 35f, 0f }, new[] { 0f, 25f }, new[] { -60f, -40f }, new[] { 170f, 80f } };
            foreach (float[] s in steps)
            {
                _orbit.Orbit(s[0], s[1], true, Anchor);
                Apply();
                AssertAnchorHeld(before, $"orbit {s[0]},{s[1]}");
            }

            // And it did actually turn: the view direction changed.
            Assert.That(Vector3.Angle(_rig.transform.forward, Quaternion.Euler(15f, 20f, 0f) * Vector3.forward),
                Is.GreaterThan(10f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnchoredZoomKeepsAnchorOnItsPixel(bool orthographic)
        {
            _orbit.Orthographic = orthographic;
            Apply();
            Vector3 before = _cam.WorldToViewportPoint(Anchor);

            _orbit.GetView(out _, out _, out float startDistance, out _);
            for (int i = 0; i < 6; i++)
            {
                _orbit.GetView(out _, out _, out float d, out _);
                _orbit.ZoomAbout(0.82f, d, true, Anchor);
                Apply();
                AssertAnchorHeld(before, $"zoom in {i}");
            }
            _orbit.GetView(out _, out _, out float zoomedDistance, out _);
            Assert.That(zoomedDistance, Is.LessThan(startDistance * 0.7f), "zooming in didn't zoom");

            for (int i = 0; i < 3; i++)
            {
                _orbit.GetView(out _, out _, out float d, out _);
                _orbit.ZoomAbout(1.18f, d, true, Anchor);
                Apply();
                AssertAnchorHeld(before, $"zoom out {i}");
            }
        }

        /// Perspective zoom-in never carries the lens past the anchor, however many notches.
        [Test]
        public void AnchoredZoomApproachesButNeverPassesAnchor()
        {
            for (int i = 0; i < 200; i++)
            {
                _orbit.GetView(out _, out _, out float d, out _);
                _orbit.ZoomAbout(0.82f, d, true, Anchor);
                Apply();
                float depth = Vector3.Dot(Anchor - _rig.transform.position, _rig.transform.forward);
                Assert.That(depth, Is.GreaterThan(0f), $"notch {i}: lens reached the anchor");
            }
        }

        /// Off, the pivot is exactly where the old code left it.
        [Test]
        public void UnanchoredOrbitAndZoomLeaveThePivotAlone()
        {
            _orbit.Orbit(40f, 10f, false, Anchor);
            _orbit.ZoomAbout(0.82f, 4f, false, Anchor);
            _orbit.GetView(out float yaw, out float pitch, out float distance, out Vector3 pivot);
            Assert.That(pivot, Is.EqualTo(Vector3.zero));
            Assert.That(yaw, Is.EqualTo(60f).Within(1e-4f));
            Assert.That(pitch, Is.EqualTo(25f).Within(1e-4f));
            Assert.That(distance, Is.LessThan(4f));
        }
    }
}
