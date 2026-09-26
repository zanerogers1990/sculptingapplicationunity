using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Sculpting.Tests
{
    /// Focal Shift (BrushFalloff.Shift) and the cursor's inner ring, which reports where the
    /// current brush's falloff reaches half weight. Job/managed parity with a shift active is in
    /// SculptControllerJobParityTests.FocalShift.
    public class FocalShiftTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private GameObject _go;
        private SculptController _controller;

        [SetUp]
        public void CreateController()
        {
            _go = new GameObject("FocalShiftController") { hideFlags = HideFlags.HideAndDontSave };
            _controller = _go.AddComponent<SculptController>();
        }

        [TearDown]
        public void DestroyController()
        {
            BrushFalloff.SetActive(null);
            if (_go != null) Object.DestroyImmediate(_go);
        }

        [Test]
        public void ZeroShiftIsBitwiseIdentity()
        {
            BrushFalloff.SetActive(null, 0f);
            for (int i = 0; i <= 1000; i++)
            {
                float t = i / 1000f;
                Assert.That(BrushFalloff.Shift(t), Is.EqualTo(t));
                Assert.That(BrushFalloff.ShiftDistance(t), Is.EqualTo(t));
                Assert.That(BrushFalloff.Smoothstep(t), Is.EqualTo(t * t * (3f - 2f * t)));
            }
        }

        [Test]
        public void ShiftKeepsTheEndsAndStaysMonotone([Values(-1f, -0.5f, 0.3f, 1f)] float focal)
        {
            BrushFalloff.SetActive(null, focal);
            Assert.That(BrushFalloff.Shift(0f), Is.EqualTo(0f).Within(1e-6f));
            Assert.That(BrushFalloff.Shift(1f), Is.EqualTo(1f).Within(1e-6f));
            float prev = BrushFalloff.Shift(0f);
            for (int i = 1; i <= 200; i++)
            {
                float s = BrushFalloff.Shift(i / 200f);
                Assert.That(s, Is.GreaterThanOrEqualTo(prev));
                prev = s;
            }
        }

        [Test]
        public void NegativeIsHarderAtEveryDistance()
        {
            for (int i = 1; i < 100; i++)
            {
                float t = i / 100f;
                BrushFalloff.SetActive(null, -0.5f);
                float hard = BrushFalloff.Smoothstep(t);
                BrushFalloff.SetActive(null, 0f);
                float neutral = BrushFalloff.Smoothstep(t);
                BrushFalloff.SetActive(null, 0.5f);
                float soft = BrushFalloff.Smoothstep(t);
                Assert.That(hard, Is.GreaterThan(neutral), $"t={t}");
                Assert.That(soft, Is.LessThan(neutral), $"t={t}");
            }
        }

        // The smoothstep's half-weight radius is exactly 1 - b = 0.5 - 0.45 * focal.
        [Test]
        public void CursorRingTracksFocalShiftOnSmoothstepBrushes([Values(-1f, -0.4f, 0f, 0.6f, 1f)] float focal)
        {
            _controller.CurrentBrush = BrushType.Inflate;
            _controller.FocalShift = focal;
            Assert.That(Ring01(), Is.EqualTo(0.5f - BrushFalloff.MaxFocalBias * focal).Within(2e-3f));
        }

        [Test]
        public void FocalShiftIsPerBrush()
        {
            _controller.CurrentBrush = BrushType.Inflate;
            _controller.FocalShift = -0.6f;
            _controller.CurrentBrush = BrushType.Smooth;
            Assert.That(_controller.FocalShift, Is.EqualTo(0f));
            _controller.CurrentBrush = BrushType.Inflate;
            Assert.That(_controller.FocalShift, Is.EqualTo(-0.6f));
        }

        [Test]
        public void CursorRingFollowsACustomCurve()
        {
            _controller.CurrentBrush = BrushType.Inflate;
            _controller.SetFalloffPreset(FalloffPreset.Linear); // half weight at half radius
            Assert.That(Ring01(), Is.EqualTo(0.5f).Within(2e-3f));
            _controller.SetFalloffPreset(FalloffPreset.Plateau); // full weight to 0.6, 0.35 at 0.85
            float ring = Ring01();
            Assert.That(ring, Is.GreaterThan(0.6f).And.LessThan(0.85f));
        }

        [Test]
        public void CursorRingShowsMaskHardness([Values(0f, 0.5f, 1f)] float hardness)
        {
            _controller.MaskHardness = hardness;
            typeof(SculptController).GetField("_isMaskPaintMode", AnyInstance).SetValue(_controller, true);
            _controller.FocalShift = -1f; // must not leak into mask painting
            Assert.That(Ring01(), Is.EqualTo(hardness + (1f - hardness) * 0.5f).Within(1e-6f));
        }

        [Test]
        public void PoseHasNoRing()
        {
            _controller.CurrentBrush = BrushType.Pose;
            Assert.That(Ring01(), Is.LessThan(0f));
        }

        [Test]
        public void EveryOtherBrushHasARingThatMovesWithTheShift()
        {
            foreach (BrushType brush in System.Enum.GetValues(typeof(BrushType)))
            {
                if (brush == BrushType.Pose) continue;
                _controller.CurrentBrush = brush;
                _controller.FocalShift = -0.5f;
                float hard = Ring01();
                _controller.FocalShift = 0.5f;
                float soft = Ring01();
                Assert.That(hard, Is.GreaterThan(soft), brush.ToString());
                Assert.That(soft, Is.GreaterThan(0f), brush.ToString());
                Assert.That(hard, Is.LessThanOrEqualTo(1f), brush.ToString());
            }
        }

        [Test]
        public void SettingsRoundTripFocalShift()
        {
            _controller.CurrentBrush = BrushType.Clay;
            _controller.FocalShift = 0.25f;
            SculptController.Settings saved = _controller.CaptureSettings();
            string json = JsonUtility.ToJson(saved);

            _controller.FocalShift = -1f;
            _controller.ApplySettings(JsonUtility.FromJson<SculptController.Settings>(json));
            _controller.CurrentBrush = BrushType.Clay;
            Assert.That(_controller.FocalShift, Is.EqualTo(0.25f));
        }

        /// Syncs the active table as Update would, then reads the ring the cursor would draw.
        private float Ring01()
        {
            typeof(SculptController).GetMethod("SyncBrushFalloff", AnyInstance).Invoke(_controller, null);
            return (float)typeof(SculptController).GetMethod("FalloffHalfWeightRadius01", AnyInstance).Invoke(_controller, null);
        }
    }
}
