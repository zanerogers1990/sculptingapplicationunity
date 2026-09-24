using System.Collections.Generic;
using NUnit.Framework;
using Sculpting.IO;
using Sculpting.Molding;
using UnityEngine;

namespace Sculpting.Tests
{
    /// Covers the mold pipeline's geometry, which is the half of it that cannot be checked by
    /// looking at the screen.
    ///
    /// The winding tests are the load-bearing ones. MeshBoolean reads inside/outside from a
    /// winding number, so a mold half built inside-out does not look wrong - it reads to the
    /// voxel boolean as "everything except the block", and what comes back is a mold with the
    /// world subtracted from it. The sign convention is pinned against a Unity primitive rather
    /// than asserted from first principles, so the tests agree with whatever Unity actually does
    /// instead of with what this code thinks it does.
    public class MoldMakerTests
    {
        private const float Tolerance = 1e-4f;

        // ------------------------------------------------------------------------- fixtures

        private static void PrimitiveMesh(PrimitiveType type, out Vector3[] verts, out int[] tris)
        {
            var go = GameObject.CreatePrimitive(type);
            Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
            verts = mesh.vertices;
            tris = mesh.triangles;
            Object.DestroyImmediate(go);
        }

        /// An axis-aligned box as world-space arrays, wound the way Unity's own cube is.
        private static void Box(Vector3 centre, Vector3 halfSize, out Vector3[] verts, out int[] tris)
        {
            PrimitiveMesh(PrimitiveType.Cube, out verts, out tris);
            for (int i = 0; i < verts.Length; i++)
                verts[i] = centre + Vector3.Scale(verts[i] * 2f, halfSize);
        }

        /// Two separate boxes side by side along X.
        ///
        /// Every column along Y or Z passes through exactly one box and crosses its surface
        /// twice, so those directions release cleanly. A column along X passes through BOTH and
        /// crosses four times, two of which no half can reach - so this is the shape that tells
        /// the axis search apart from a coin flip. Deliberately not joined by a waist: overlapping
        /// shells leave interior walls that the column map correctly reports as extra crossings,
        /// which would muddy what the test is actually measuring.
        private static void TwoBoxes(out Vector3[] verts, out int[] tris)
        {
            Box(new Vector3(-0.6f, 0f, 0f), new Vector3(0.3f, 0.3f, 0.3f), out Vector3[] aV, out int[] aT);
            Box(new Vector3(0.6f, 0f, 0f), new Vector3(0.3f, 0.3f, 0.3f), out Vector3[] bV, out int[] bT);

            var v = new List<Vector3>(aV);
            var t = new List<int>(aT);
            Append(v, t, bV, bT);
            verts = v.ToArray();
            tris = t.ToArray();
        }

        private static void Append(List<Vector3> v, List<int> t, Vector3[] addV, int[] addT)
        {
            int offset = v.Count;
            v.AddRange(addV);
            foreach (int i in addT) t.Add(i + offset);
        }

        private static MoldSettings DefaultSettings()
        {
            var s = new MoldSettings
            {
                // Pinned counts, not the mm density: these cases assert on geometry, and a
                // grid derived from a physical cell size would change with every block size.
                AutoGridDensity = false,
                GridAcross = 49,
                GridDepth = 33,
                Rounding = 2,
                BlendRadius = 1,
                MinSamplesPerCell = 2,
                Padding = 0.25f,
                Wall = 0.25f,
            };
            return s;
        }

        // -------------------------------------------------------------------------- winding

        /// Unity's own cube is the reference for what "outward-facing" means in this project's
        /// convention, so everything else is measured against its volume's SIGN rather than
        /// against an assumption about handedness.
        private static float ReferenceVolumeSign()
        {
            PrimitiveMesh(PrimitiveType.Cube, out Vector3[] v, out int[] t);
            return Mathf.Sign(MoldBuilder.SignedVolume(v, t));
        }

        [Test]
        public void HalfSolidIsWoundOutward()
        {
            float expected = ReferenceVolumeSign();
            var frame = MoldFrame.Identity;
            var field = PartingField.Flat(frame, -1f, 1f, -1f, 1f, 12, 9, 0f);

            field.BuildHalfSolid(-0.5f, out Vector3[] lowerV, out int[] lowerT);
            field.BuildHalfSolid(0.5f, out Vector3[] upperV, out int[] upperT);

            float lower = MoldBuilder.SignedVolume(lowerV, lowerT);
            float upper = MoldBuilder.SignedVolume(upperV, upperT);

            Assert.AreEqual(expected, Mathf.Sign(lower), "lower half is inside-out");
            Assert.AreEqual(expected, Mathf.Sign(upper), "upper half is inside-out");

            // A 2x2 footprint, half a unit thick, is exactly 2 units of volume each way.
            Assert.AreEqual(2f, Mathf.Abs(lower), 1e-3f);
            Assert.AreEqual(2f, Mathf.Abs(upper), 1e-3f);
        }

        [Test]
        public void HalfSolidVolumeFollowsACurvedSurface()
        {
            var frame = MoldFrame.Identity;
            var field = PartingField.Flat(frame, -1f, 1f, -1f, 1f, 33, 33, 0f);

            // A tilted sheet: the solid below it is a wedge, whose volume is the average height
            // over the footprint - which for a linear ramp is just the mid height.
            for (int i = 0; i < field.Nr; i++)
                for (int j = 0; j < field.Ne; j++)
                    field.H[i * field.Ne + j] = 0.25f * field.RAt(i);

            field.BuildHalfSolid(-1f, out Vector3[] v, out int[] t);
            // Footprint 2x2, mean height above -1 is 1 (the ramp averages to zero).
            Assert.AreEqual(4f, Mathf.Abs(MoldBuilder.SignedVolume(v, t)), 1e-3f);
        }

        /// The two halves have to share the parting sheet exactly, or the mold does not close.
        /// Built from the same nodes, so this is a construction guarantee - and this test is what
        /// keeps it one.
        [Test]
        public void BothHalvesShareThePartingSheetExactly()
        {
            var frame = MoldFrame.FromRightUp(Vector3.right, new Vector3(0.2f, 1f, 0.1f));
            var field = PartingField.Flat(frame, -1f, 1f, -0.5f, 0.5f, 17, 11, 0.1f);
            for (int k = 0; k < field.H.Length; k++) field.H[k] += 0.2f * Mathf.Sin(k * 0.7f);

            field.BuildHalfSolid(-2f, out Vector3[] lowerV, out _);
            field.BuildHalfSolid(2f, out Vector3[] upperV, out _);

            int n = field.Nr * field.Ne;
            // BuildSlab writes the upper sheet first, then the lower. For the lower half the
            // parting sheet IS the upper sheet; for the upper half it is the lower one.
            for (int k = 0; k < n; k++)
            {
                Vector3 a = lowerV[k];
                Vector3 b = upperV[n + k];
                Assert.Less(Vector3.Distance(a, b), 1e-5f, $"seam node {k} does not match");
            }
        }

        [Test]
        public void FrustumIsWoundOutward()
        {
            float expected = ReferenceVolumeSign();
            var verts = new List<Vector3>();
            var tris = new List<int>();
            MoldGeometry.AddFrustum(verts, tris, Vector3.zero, Vector3.up * 2f, 0.5f, 0.5f, 64);

            float volume = MoldBuilder.SignedVolume(verts.ToArray(), tris.ToArray());
            Assert.AreEqual(expected, Mathf.Sign(volume), "frustum is inside-out");
            // A cylinder of r=0.5, h=2 is pi/2. A 64-gon is a shade under.
            Assert.AreEqual(Mathf.PI * 0.5f, Mathf.Abs(volume), 0.01f);
        }

        [Test]
        public void SweptChannelIsWoundOutward()
        {
            float expected = ReferenceVolumeSign();
            var frame = MoldFrame.Identity;
            var field = PartingField.Flat(frame, -1f, 1f, -1f, 1f, 9, 9, 0f);

            var verts = new List<Vector3>();
            var tris = new List<int>();
            MoldGeometry.AddSweptChannel(verts, tris, field, new Vector2(-0.8f, 0f), new Vector2(0.8f, 0f),
                                         0.1f, 0.2f, 12, 32);

            float volume = MoldBuilder.SignedVolume(verts.ToArray(), tris.ToArray());
            Assert.AreEqual(expected, Mathf.Sign(volume), "swept channel is inside-out");
            Assert.Greater(Mathf.Abs(volume), 0.01f);
        }

        // ----------------------------------------------------------------------- column map

        [Test]
        public void ColumnMapFindsTwoCrossingsThroughASolidBox()
        {
            Box(Vector3.zero, new Vector3(0.5f, 0.25f, 0.5f), out Vector3[] v, out int[] t);
            PullColumnMap map = PullColumnMap.Build(v, t, MoldFrame.Identity, -0.6f, 0.6f, -0.6f, 0.6f, 24, 24);

            int interior = 0;
            for (int i = 0; i < map.Nr; i++)
            {
                float r = map.ColumnCentreR(i);
                for (int j = 0; j < map.Ne; j++)
                {
                    float e = map.ColumnCentreE(j);
                    // Well inside the box's footprint, so the column is unambiguous.
                    if (Mathf.Abs(r) > 0.4f || Mathf.Abs(e) > 0.4f) continue;
                    interior++;

                    Assert.AreEqual(2, map.CrossingsIn(i, j), $"column ({i},{j}) should cross the box twice");
                    Assert.AreEqual(-0.25f, map.Crossing(i, j, 0), Tolerance);
                    Assert.AreEqual(0.25f, map.Crossing(i, j, 1), Tolerance);
                }
            }
            Assert.Greater(interior, 50, "the fixture should cover plenty of interior columns");
        }

        [Test]
        public void ColumnMapReportsBlockedDirections()
        {
            Box(Vector3.zero, new Vector3(0.5f, 0.25f, 0.5f), out Vector3[] v, out int[] t);
            PullColumnMap map = PullColumnMap.Build(v, t, MoldFrame.Identity, -0.6f, 0.6f, -0.6f, 0.6f, 24, 24);
            map.Locate(0f, 0f, out int i, out int j);

            // The bottom face can drop freely but cannot lift - the top face is above it.
            Assert.IsTrue(map.BlockedAbove(i, j, -0.25f, 1e-5f));
            Assert.IsFalse(map.BlockedBelow(i, j, -0.25f, 1e-5f));

            Assert.IsFalse(map.BlockedAbove(i, j, 0.25f, 1e-5f));
            Assert.IsTrue(map.BlockedBelow(i, j, 0.25f, 1e-5f));
        }

        // ------------------------------------------------------------------------ best level

        [Test]
        public void BestLevelSplitsInsideASimpleColumn()
        {
            // Two crossings: the lower one is blocked looking up, the upper one looking down.
            var us = new List<float> { -1f, 1f };
            var up = new List<bool> { true, false };
            var down = new List<bool> { false, true };

            float h = PartingSurfaceFitter.BestLevel(us, up, down, out int cost);
            Assert.AreEqual(0, cost, "a convex column should trap nothing");
            Assert.Greater(h, -1f - Tolerance);
            Assert.Less(h, 1f + Tolerance);
        }

        [Test]
        public void BestLevelKeepsCostMinimalThroughAnUndercut()
        {
            // Four crossings: a shell with a void in it. Two surfaces are trapped whatever
            // height is chosen, which is what the cost should report.
            var us = new List<float> { -2f, -1f, 1f, 2f };
            var up = new List<bool> { true, true, true, false };
            var down = new List<bool> { false, true, true, true };

            PartingSurfaceFitter.BestLevel(us, up, down, out int cost);
            Assert.AreEqual(2, cost);
        }

        // ------------------------------------------------------------------------- the fit

        [Test]
        public void FlatSplitOfASphereTrapsNothing()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, DefaultSettings());

            Assert.IsTrue(fit.IsValid, fit.Error);
            Assert.Less(fit.BlockedFraction, 0.02f, "a sphere split through its equator should release cleanly");
        }

        [Test]
        public void PullingAcrossTheGapTrapsFarLessThanPullingAlongIt()
        {
            TwoBoxes(out Vector3[] v, out int[] t);
            MoldSettings s = DefaultSettings();

            // Pulling along X is the bad choice: each box hides behind the other.
            MoldFrame bad = MoldFrame.FromWorldAxis(0, new Vector3(1.8f, 0.6f, 0.6f));
            MoldFit fitted = PartingSurfaceFitter.Fit(v, t, v.Length, bad, s);
            Assert.IsTrue(fitted.IsValid, fitted.Error);
            Assert.Greater(fitted.BlockedFraction, 0.25f, "pulling along the pair should trap about half the surface");

            // Pulling along Y is the good one - every column is a simple slab.
            MoldFrame good = MoldFrame.FromWorldAxis(1, new Vector3(1.8f, 0.6f, 0.6f));
            MoldFit alternative = PartingSurfaceFitter.Fit(v, t, v.Length, good, s);
            Assert.IsTrue(alternative.IsValid, alternative.Error);
            Assert.Less(alternative.BlockedFraction, fitted.BlockedFraction);
        }

        [Test]
        public void AxisSearchPicksTheDirectionThatTrapsLeast()
        {
            TwoBoxes(out Vector3[] v, out int[] t);
            MoldFit fit = PartingSurfaceFitter.FitBestAxis(v, t, v.Length, DefaultSettings(), out int axis);

            Assert.IsTrue(fit.IsValid, fit.Error);
            Assert.AreNotEqual(0, axis, "the search should reject pulling along the line of the two boxes");
            Assert.Less(fit.BlockedFraction, 0.05f);
        }

        [Test]
        public void FittedSurfaceStaysInsideTheBlock()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldSettings s = DefaultSettings();
            MoldFit fit = PartingSurfaceFitter.Fit(v, t, v.Length, MoldFrame.Identity, s);
            Assert.IsTrue(fit.IsValid, fit.Error);

            float lo = fit.Block.UBottom + 0.3f * s.Wall - Tolerance;
            float hi = fit.Block.UTop - 0.3f * s.Wall + Tolerance;
            foreach (float h in fit.Field.H)
            {
                Assert.GreaterOrEqual(h, lo);
                Assert.LessOrEqual(h, hi);
            }
        }

        // --------------------------------------------------------------------- undercut read

        [Test]
        public void UndercutTintSplitsASphereInHalfAndTrapsNothing()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, DefaultSettings());

            var colors = new Color32[v.Length];
            UndercutAnalysis.Report report = UndercutAnalysis.Evaluate(v, v.Length, fit.Field, fit.Columns, 0f, colors);

            Assert.IsTrue(UndercutAnalysis.CutsThroughModel(report));
            Assert.Less(report.TrappedFraction, 0.05f);
            // Roughly even, since the sphere is split through the middle. Generous bounds - the
            // primitive's poles are more densely tessellated than its equator.
            Assert.Greater(report.Upper, v.Length / 4);
            Assert.Greater(report.Lower, v.Length / 4);
        }

        [Test]
        public void UndercutTintFlagsASurfaceThatMissesTheModel()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldSettings s = DefaultSettings();
            s.SplitOffset = 5f; // way above the sphere; gets clamped to the block's roof
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);

            var colors = new Color32[v.Length];
            UndercutAnalysis.Report report = UndercutAnalysis.Evaluate(v, v.Length, fit.Field, fit.Columns, 0f, colors);

            Assert.IsFalse(UndercutAnalysis.CutsThroughModel(report),
                           "a surface entirely above the model must not count as cutting it");
        }

        // ------------------------------------------------------------------------ the build

        [Test]
        public void BuildProducesTwoSolidHalvesWithACavity()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldSettings s = DefaultSettings();
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);
            Assert.IsTrue(fit.IsValid, fit.Error);

            MoldBuilder.Result result = MoldBuilder.Build(v, t, v.Length, fit.Field, fit.Block, fit.Columns,
                                                          new List<MoldFeature>(), s, 64);
            try
            {
                Assert.IsTrue(result.Success, result.Error);
                Assert.Greater(result.LowerHalf.vertexCount, 0);
                Assert.Greater(result.UpperHalf.vertexCount, 0);

                // The cavity has to have actually been removed: both halves together must be
                // meaningfully smaller than the two raw blocks.
                fit.Field.BuildHalfSolid(fit.Block.UBottom, out Vector3[] lv, out int[] lt);
                fit.Field.BuildHalfSolid(fit.Block.UTop, out Vector3[] uv, out int[] ut);
                float blocks = Mathf.Abs(MoldBuilder.SignedVolume(lv, lt)) + Mathf.Abs(MoldBuilder.SignedVolume(uv, ut));
                float built = Mathf.Abs(MoldBuilder.SignedVolume(result.LowerHalf.vertices, result.LowerHalf.triangles))
                            + Mathf.Abs(MoldBuilder.SignedVolume(result.UpperHalf.vertices, result.UpperHalf.triangles));
                float sphere = Mathf.Abs(MoldBuilder.SignedVolume(v, t));

                Assert.Less(built, blocks, "nothing was cut out of the blocks");
                // At 64 voxels the cavity is approximate, so this is a sanity band, not equality.
                Assert.AreEqual(sphere, blocks - built, sphere * 0.35f);
            }
            finally
            {
                if (result.LowerHalf != null) Object.DestroyImmediate(result.LowerHalf);
                if (result.UpperHalf != null) Object.DestroyImmediate(result.UpperHalf);
            }
        }

        [Test]
        public void PegsAddMaterialToTheLowerHalf()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldSettings s = DefaultSettings();
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);

            // Deliberately fat pins at a deliberately high resolution.
            //
            // This is a property test, and the property is only observable if the boolean can
            // actually resolve a peg: the voxel grid rounds away anything thinner than a cell or
            // two, and at the default pin size a peg is about one and a half cells across at
            // resolution 64, which came out as no measurable change at all. That is correct
            // behaviour for the boolean and a useless fixture for this test - and it is also why
            // the app's own draft resolution is a draft: small pins really do not survive it.
            const int resolution = 96;
            s.PinRadius = 0.09f;
            s.PinHeight = 0.18f;
            var pins = new List<MoldFeature>(MoldGeometry.SuggestPins(fit.Block, s, fit.Field));
            Assert.AreEqual(4, pins.Count);
            Assert.AreEqual(0.09f, pins[0].RadiusInner, 1e-5f, "the pins should not have been capped by the padding");

            MoldBuilder.Result bare = MoldBuilder.Build(v, t, v.Length, fit.Field, fit.Block, fit.Columns,
                                                        new List<MoldFeature>(), s, resolution);
            MoldBuilder.Result pinned = MoldBuilder.Build(v, t, v.Length, fit.Field, fit.Block, fit.Columns, pins, s, resolution);
            try
            {
                Assert.IsTrue(bare.Success, bare.Error);
                Assert.IsTrue(pinned.Success, pinned.Error);

                float grew = Volume(pinned.LowerHalf) - Volume(bare.LowerHalf);
                float shrank = Volume(bare.UpperHalf) - Volume(pinned.UpperHalf);

                // Each peg stands PinHeight proud of the sheet; the part below it was already
                // inside the block, so only the proud part is new material.
                float analytic = 4f * Mathf.PI * 0.09f * 0.09f * 0.18f * 0.77f;
                Assert.Greater(grew, 0.5f * analytic, "the pegs should add material to the lower half");
                Assert.Greater(shrank, 0.5f * analytic, "the sockets should remove material from the upper half");
            }
            finally
            {
                Dispose(bare, pinned);
            }
        }

        [Test]
        public void ChannelsCutIntoBothHalves()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldSettings s = DefaultSettings();
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);

            var channels = new List<MoldFeature>(MoldGeometry.SuggestChannels(fit.Block, s, fit.Field, fit.Columns));
            Assert.AreEqual(2, channels.Count, "a sprue and a vent");

            MoldBuilder.Result bare = MoldBuilder.Build(v, t, v.Length, fit.Field, fit.Block, fit.Columns,
                                                        new List<MoldFeature>(), s, 64);
            MoldBuilder.Result cut = MoldBuilder.Build(v, t, v.Length, fit.Field, fit.Block, fit.Columns, channels, s, 64);
            try
            {
                Assert.IsTrue(bare.Success, bare.Error);
                Assert.IsTrue(cut.Success, cut.Error);

                // A channel sits ON the parting surface, so each half gets its own half-groove.
                Assert.Less(Volume(cut.LowerHalf), Volume(bare.LowerHalf), "the lower half should lose a groove");
                Assert.Less(Volume(cut.UpperHalf), Volume(bare.UpperHalf), "the upper half should lose a groove");
            }
            finally
            {
                Dispose(bare, cut);
            }
        }

        private static float Volume(Mesh mesh) =>
            Mathf.Abs(MoldBuilder.SignedVolume(mesh.vertices, mesh.triangles));

        private static void Dispose(params MoldBuilder.Result[] results)
        {
            foreach (MoldBuilder.Result r in results)
            {
                if (r.LowerHalf != null) Object.DestroyImmediate(r.LowerHalf);
                if (r.UpperHalf != null) Object.DestroyImmediate(r.UpperHalf);
            }
        }

        // ---------------------------------------------------------------------- ray marching

        [Test]
        public void RaycastFindsTheSurfaceAtTheRightHeight()
        {
            var frame = MoldFrame.Identity;
            var field = PartingField.Flat(frame, -1f, 1f, -1f, 1f, 9, 9, 0.35f);

            var ray = new Ray(new Vector3(0.2f, 3f, -0.1f), Vector3.down);
            Assert.IsTrue(field.RaycastSurface(ray, 10f, out Vector3 hit));
            Assert.AreEqual(0.35f, hit.y, 1e-3f);
            Assert.AreEqual(0.2f, hit.x, 1e-3f);
            Assert.AreEqual(-0.1f, hit.z, 1e-3f);
        }

        [Test]
        public void RaycastMissesWhenTheRayNeverCrossesTheSheet()
        {
            var frame = MoldFrame.Identity;
            var field = PartingField.Flat(frame, -1f, 1f, -1f, 1f, 9, 9, 0f);

            // Parallel to the sheet and well above it.
            var ray = new Ray(new Vector3(-0.9f, 0.5f, 0f), Vector3.right);
            Assert.IsFalse(field.RaycastSurface(ray, 2f, out _));
        }

        /// The reach MoldController.PlaceAt actually passes, on a block the size of a real
        /// sculpt - which is the case every click in the app takes and the one the two tests
        /// above miss by choosing a tidy maxDistance.
        ///
        /// The march used to divide maxDistance into a fixed 192 steps, so this reach gave a
        /// step of 5.25 world units across a footprint 2.24 long: consecutive samples landed
        /// either side of the entire mold, no crossing was ever bracketed, and placing a pin,
        /// sprue or vent silently did nothing. Sized off the lure this was found on.
        [Test]
        public void RaycastFindsTheSurfaceAtThePlacementReach()
        {
            var frame = MoldFrame.Identity;
            // The lure's mold: 2.24 x 0.97 footprint, parting sheet up at y = 1.
            var field = PartingField.Flat(frame, -1.118f, 1.118f, -0.483f, 0.483f, 64, 40, 1f);

            float blockWidth = 2.235f, blockDepth = 0.965f, blockHeight = 0.404f;
            float reach = 4f * Mathf.Max(blockWidth, Mathf.Max(blockDepth, blockHeight)) + 1000f;

            // A camera a few units out, looking down at the sheet from an ordinary orbit angle.
            var origin = new Vector3(0.6f, 4f, -2.5f);
            var ray = new Ray(origin, (new Vector3(0.35f, 1f, -0.2f) - origin).normalized);

            Assert.IsTrue(field.RaycastSurface(ray, reach, out Vector3 hit),
                          "a placement click at the reach PlaceAt uses has to find the sheet");
            Assert.AreEqual(1f, hit.y, 1e-3f);
            Assert.AreEqual(0.35f, hit.x, 1e-2f);
            Assert.AreEqual(-0.2f, hit.z, 1e-2f);
        }

        /// A huge reach must not turn into a hit on a ray that leaves the footprint before it
        /// ever reaches the sheet - the clip tightens the march, it does not loosen the test.
        [Test]
        public void RaycastStillMissesBesideTheFootprintAtALargeReach()
        {
            var frame = MoldFrame.Identity;
            var field = PartingField.Flat(frame, -1f, 1f, -1f, 1f, 33, 33, 0f);

            // Aimed straight down, but well outside the grid in x.
            var ray = new Ray(new Vector3(6f, 3f, 0f), Vector3.down);
            Assert.IsFalse(field.RaycastSurface(ray, 1010f, out _));
        }

        // ------------------------------------------------------------------ millimetre scale

        /// The whole point of the millimetre rewrite: the sprue that comes out is the sprue the
        /// Blender addon pours through, at whatever scale the model happens to be modelled at.
        /// The port used to size it as a fraction of the model's bounds, which on the lure this
        /// was found on gave a 1.3mm bore where the addon uses 14mm.
        [Test]
        public void PhysicalSizesMatchTheAddonAtAnyModelScale()
        {
            // Two models of wildly different world size, both declared to be 90mm long.
            foreach (float unitsLong in new[] { 0.05f, 1.9942f, 400f })
            {
                var s = new MoldSettings();
                s.InitialiseForBounds(new Bounds(Vector3.zero, new Vector3(unitsLong, 0.12f, 0.72f)));
                s.SetModelLengthMm(90f, unitsLong);

                Assert.AreEqual(90f, s.ModelLengthMm(unitsLong), 1e-2f, "declared length must round-trip");

                // The addon's numbers, read back out through the scale as world units.
                Assert.AreEqual(14f, 2f * s.ToMillimetres(s.SprueRadiusInner), 1e-3f, "sprue bore");
                Assert.AreEqual(18f, 2f * s.ToMillimetres(s.SprueRadiusOuter), 1e-3f, "sprue funnel");
                Assert.AreEqual(1f, 2f * s.ToMillimetres(s.VentRadius), 1e-3f, "vent");
                Assert.AreEqual(3.5f, s.ToMillimetres(s.PinRadius), 1e-3f, "pin radius");
                Assert.AreEqual(7f, s.ToMillimetres(s.PinHeight), 1e-3f, "pin height");
                Assert.AreEqual(15f, s.ToMillimetres(s.Padding), 1e-3f, "padding");
                Assert.AreEqual(15f, s.ToMillimetres(s.Wall), 1e-3f, "wall");
                Assert.AreEqual(4f, s.ToMillimetres(s.ChannelOverlap), 1e-3f, "channel overlap");

                // And in WORLD units they really do scale with the declared length.
                Assert.AreEqual(7f * unitsLong / 90f, s.SprueRadiusInner, 1e-6f * Mathf.Max(1f, unitsLong));
            }
        }

        /// Setting the scale must resize the mold, not just relabel it.
        [Test]
        public void ChangingTheDeclaredLengthRescalesTheMold()
        {
            var s = new MoldSettings();
            s.SetModelLengthMm(90f, 2f);
            float wallAt90 = s.Wall;

            s.SetModelLengthMm(180f, 2f);
            // Twice as long a model, same 15mm wall - so the wall is HALF as big in world units.
            Assert.AreEqual(0.5f * wallAt90, s.Wall, 1e-6f);
            Assert.AreEqual(15f, s.WallMm, 1e-4f, "the physical size itself must not drift");
        }

        // ---------------------------------------------------------------------- mirroring

        [Test]
        public void MirrorsVentsButNeverTheSprue()
        {
            var block = new MoldBlock(new Vector3(-1f, -0.1f, -0.5f), new Vector3(1f, 0.1f, 0.5f), 0.2f, 0.2f);
            var s = new MoldSettings { MirrorAcrossRight = true };

            // Model centre is the origin, so a vent at r = 0.4 mirrors to r = -0.4.
            var vent = new MoldFeature(MoldFeatureKind.Vent, 0.4f, 0.25f);
            var into = new List<MoldFeature>();
            MoldGeometry.ExpandMirrors(vent, block, s, into);

            Assert.AreEqual(2, into.Count, "one vent plus its mirror");
            Assert.AreEqual(-0.4f, into[1].R, 1e-5f);
            Assert.AreSame(vent, into[1].Source, "a mirror has to know its original, so clicking it selects that");
            Assert.AreEqual(0.25f, into[1].E, 1e-5f, "the un-mirrored axis must not move");
            Assert.IsTrue(into[1].IsMirror);
            Assert.IsFalse(into[0].IsMirror);

            var sprue = new MoldFeature(MoldFeatureKind.Sprue, 0.4f, 0f);
            into.Clear();
            MoldGeometry.ExpandMirrors(sprue, block, s, into);
            Assert.AreEqual(1, into.Count, "a sprue is never mirrored - one pour hole");
        }

        [Test]
        public void BothMirrorAxesGiveFourAndAFeatureOnThePlaneIsNotDuplicated()
        {
            var block = new MoldBlock(new Vector3(-1f, -0.1f, -0.5f), new Vector3(1f, 0.1f, 0.5f), 0.2f, 0.2f);
            var s = new MoldSettings { MirrorAcrossRight = true, MirrorAcrossEye = true };

            var into = new List<MoldFeature>();
            MoldGeometry.ExpandMirrors(new MoldFeature(MoldFeatureKind.Pin, 0.4f, 0.25f), block, s, into);
            Assert.AreEqual(4, into.Count);

            // Sitting exactly on both mirror planes: every copy would land on the original.
            into.Clear();
            MoldGeometry.ExpandMirrors(new MoldFeature(MoldFeatureKind.Pin, 0f, 0f), block, s, into);
            Assert.AreEqual(1, into.Count, "coincident copies must be skipped, not stacked");
        }

        // ------------------------------------------------------------------- stl round trip

        /// The export's axis map has to be StlImporter's exactly inverted, or a half comes back
        /// into the app rotated and a lure exported from the Blender addon does not line up with
        /// one exported from here. Writing and re-reading is the only test that pins that down.
        [Test]
        public void StlExportRoundTripsThroughTheImporter()
        {
            // A cube offset off the origin, as plain world-space arrays. Deliberately NOT via
            // SculptableMesh: that cannot initialise outside Play mode, so the scene overload is
            // untestable here and the arrays are where the geometry lives anyway.
            var offset = new Vector3(0.3f, -0.2f, 0.7f);
            Box(offset, new Vector3(0.5f, 0.5f, 0.5f), out Vector3[] verts, out int[] tris);

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                 "mold_stl_roundtrip_" + System.Guid.NewGuid().ToString("N") + ".stl");
            try
            {
                // 10 mm per unit, the addon's own export scale.
                Assert.IsNotNull(StlExporter.Export(verts, tris, path, 10f), "export must write a file");

                Mesh back = StlImporter.Import(path, out string error);
                Assert.IsNull(error, "import error: " + error);
                Assert.IsNotNull(back);

                // The cube is 1 unit; at 10 mm/unit it must come back 10 across.
                Bounds b = back.bounds;
                Assert.AreEqual(10f, b.size.x, 1e-2f);
                Assert.AreEqual(10f, b.size.y, 1e-2f);
                Assert.AreEqual(10f, b.size.z, 1e-2f);

                // And in the SAME place, scaled - which is what proves the axis map inverts
                // rather than merely being self-consistent. The importer welds, so compare the
                // centre rather than the vertex list.
                // Export applies ToStl, import applies ToUnity, so the pair is the identity and
                // the cube comes back in UNITY space, merely scaled: ToUnity(ToStl(u) * 10) is
                // u * 10. Asserting that is what pins the two maps as true inverses - a pair
                // that were each self-consistent but not inverse would fail right here.
                Vector3 expected = offset * 10f;
                Assert.AreEqual(expected.x, b.center.x, 1e-2f, "x");
                Assert.AreEqual(expected.y, b.center.y, 1e-2f, "y");
                Assert.AreEqual(expected.z, b.center.z, 1e-2f, "z");

                // Outward-facing: a closed shell wound correctly has positive signed volume of
                // the right magnitude. A flipped winding would give the same magnitude negated,
                // which is exactly the failure this guards.
                float volume = MoldBuilder.SignedVolume(back.vertices, back.triangles);
                Assert.AreEqual(1000f, Mathf.Abs(volume), 5f, "10mm cube is 1000 cubic mm");
                Assert.Greater(volume, 0f, "winding must survive the handedness flip");

                Object.DestroyImmediate(back);
            }
            finally
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }

        // ---------------------------------------------------------------------------- frame

        [Test]
        public void FrameRoundTripsAndPreservesHandedness()
        {
            var frame = MoldFrame.FromRightUp(new Vector3(1f, 0.3f, -0.2f), new Vector3(0.1f, 1f, 0.4f));

            var probe = new Vector3(0.37f, -1.2f, 2.8f);
            Vector3 back = frame.ToWorld(frame.ToFrame(probe));
            Assert.Less(Vector3.Distance(probe, back), 1e-4f, "frame round trip lost the point");

            // Determinant +1 is what lets the slab builder reason about winding in frame space
            // and have it hold in world space.
            float det = Vector3.Dot(frame.Right, Vector3.Cross(frame.Up, frame.Eye));
            Assert.AreEqual(1f, det, 1e-4f, "unexpected handedness - see MoldFrame.FromRightUp");
        }

        [Test]
        public void ChannelsExitTowardsTheNearestWall()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, DefaultSettings());
            MoldBlock block = fit.Block;

            Assert.AreEqual(0f, MoldFeature.NearestWallAngle(block.R1 - 0.05f, 0f, block), "right end leaves through +Right");
            Assert.AreEqual(180f, MoldFeature.NearestWallAngle(block.R0 + 0.05f, 0f, block), "left end leaves through -Right");
            Assert.AreEqual(270f, MoldFeature.NearestWallAngle(0f, block.E0 + 0.05f, block), "front leaves through -Eye");
            Assert.AreEqual(90f, MoldFeature.NearestWallAngle(0f, block.E1 - 0.05f, block), "back leaves through +Eye");
        }

        // -------------------------------------------------------------- the parting grid

        /// The grid is derived from a cell SIZE now, so cells come out square whatever shape the
        /// model is. Two independent node counts over two different spans could not do that -
        /// the cell aspect ratio was the model's aspect ratio, and the parting line resolved one
        /// axis several times better than the other.
        [Test]
        public void GridCellsAreSquareOnALopsidedModel()
        {
            Box(Vector3.zero, new Vector3(2.5f, 0.2f, 0.35f), out Vector3[] v, out int[] t);

            var s = DefaultSettings();
            s.AutoGridDensity = true;
            s.GridCellMm = 1f;

            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);
            Assert.IsTrue(fit.IsValid, fit.Error);

            float aspect = Mathf.Max(fit.Field.Dr, fit.Field.De) /
                           Mathf.Max(Mathf.Min(fit.Field.Dr, fit.Field.De), 1e-9f);
            Assert.Less(aspect, 1.05f,
                        "cells should be square; got " + fit.Field.Dr + " x " + fit.Field.De);
        }

        /// Odd node counts put a node exactly on the model's centre line. The block is the
        /// model's bounds grown by the same padding on both sides, so its footprint centre IS
        /// the model's - and with an even count that centre falls between two nodes, which is
        /// enough to make a mirror-symmetric model fit an asymmetric surface.
        [Test]
        public void AGridNodeLandsExactlyOnTheCentreLine()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);

            var s = DefaultSettings();
            s.AutoGridDensity = true;
            s.GridCellMm = 1f;

            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);
            Assert.IsTrue(fit.IsValid, fit.Error);

            Assert.AreEqual(1, fit.Field.Nr & 1, "node count across should be odd");
            Assert.AreEqual(1, fit.Field.Ne & 1, "node count deep should be odd");

            Assert.AreEqual(0.5f * (fit.Block.R0 + fit.Block.R1), fit.Field.RAt((fit.Field.Nr - 1) / 2), Tolerance);
            Assert.AreEqual(0.5f * (fit.Block.E0 + fit.Block.E1), fit.Field.EAt((fit.Field.Ne - 1) / 2), Tolerance);
        }

        /// One knob, and it means the same thing on every model: halve the cell, double the nodes.
        [Test]
        public void HalvingTheCellSizeDoublesTheNodeCount()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);

            var coarse = DefaultSettings();
            coarse.AutoGridDensity = true;
            coarse.GridCellMm = 2f;
            MoldFit a = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, coarse);

            var fine = DefaultSettings();
            fine.AutoGridDensity = true;
            fine.GridCellMm = 1f;
            MoldFit b = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, fine);

            Assert.IsTrue(a.IsValid && b.IsValid);
            float ratio = b.Field.Nr / (float)a.Field.Nr;
            Assert.That(ratio, Is.EqualTo(2f).Within(0.15f), "got " + a.Field.Nr + " -> " + b.Field.Nr);
        }

        /// An absurd density must clamp rather than allocate its way out of memory, and the
        /// clamped counts must still be odd.
        [Test]
        public void AnAbsurdDensityIsClampedByTheNodeBudget()
        {
            Box(Vector3.zero, new Vector3(1f, 1f, 1f), out Vector3[] v, out int[] t);

            var s = DefaultSettings();
            s.AutoGridDensity = true;
            s.GridCellMm = 0.0005f;

            MoldBlock.MeasureModel(v, v.Length, MoldFrame.Identity, out Vector3 lo, out Vector3 hi);
            var block = new MoldBlock(lo, hi, s.Padding, s.Wall);
            PartingGrid.NodesForBlock(block, s, out int nr, out int ne);

            Assert.LessOrEqual((long)nr * ne, PartingGrid.MaxNodes);
            Assert.AreEqual(1, nr & 1);
            Assert.AreEqual(1, ne & 1);
        }

        // ------------------------------------------------------------------- true centre

        /// The bounding box is not the object. A slab with a thin spike on top has its box
        /// midpoint up inside the spike, where there is almost no material; the true centre sits
        /// in the slab. Closed-form: half of 4.14 is 2.07, all of it inside the slab, so
        /// 4 * (h + 1) = 2.07.
        ///
        /// The two boxes are DISJOINT on purpose. Sharing a face leaves coincident crossings,
        /// PullColumnMap welds them, and the welded column reads as an odd count with the spike
        /// dropped - which would quietly remove the very feature under test.
        [Test]
        public void FlatSplitLandsAtTheTrueCentreNotTheBoundsMidpoint()
        {
            Box(new Vector3(0f, -0.5f, 0f), new Vector3(1f, 0.5f, 1f), out Vector3[] slabV, out int[] slabT);
            Box(new Vector3(0f, 2.25f, 0f), new Vector3(0.1f, 1.75f, 0.1f), out Vector3[] spikeV, out int[] spikeT);

            var v = new List<Vector3>(slabV);
            var t = new List<int>(slabT);
            Append(v, t, spikeV, spikeT);
            Vector3[] verts = v.ToArray();
            int[] tris = t.ToArray();

            var s = DefaultSettings();
            s.AutoGridDensity = true;
            s.GridCellMm = 1f;
            s.CentreOnVolume = true;

            MoldFit fit = PartingSurfaceFitter.Flat(verts, tris, verts.Length, MoldFrame.Identity, s);
            Assert.IsTrue(fit.IsValid, fit.Error);

            const float expected = 2.07f / 4f - 1f;    // -0.4825
            const float boundsMid = 0.5f * (-1f + 4f); //  1.5
            Assert.AreEqual(expected, fit.Field.H[0], 0.02f,
                            "split should sit in the slab, not up in the spike");

            // The control: the old behaviour has to be clearly wrong here, or this proves nothing.
            Assert.Greater(Mathf.Abs(boundsMid - expected), 1.9f);
        }

        /// The honest test of "centred": half the material really is below it.
        [Test]
        public void TheSplitPutsHalfTheMaterialOnEachSide()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] sphereV, out int[] sphereT);
            // Fin clear of the sphere - overlapping shells would leave interior crossings.
            Box(new Vector3(0f, 1.3f, 0f), new Vector3(0.08f, 0.75f, 0.08f), out Vector3[] finV, out int[] finT);

            var v = new List<Vector3>(sphereV);
            var t = new List<int>(sphereT);
            Append(v, t, finV, finT);
            Vector3[] verts = v.ToArray();
            int[] tris = t.ToArray();

            var s = DefaultSettings();
            s.AutoGridDensity = true;
            s.GridCellMm = 1f;
            s.CentreOnVolume = true;

            MoldFit fit = PartingSurfaceFitter.Flat(verts, tris, verts.Length, MoldFrame.Identity, s);
            Assert.IsTrue(fit.IsValid, fit.Error);

            double total = PartingCentre.SolidExtentBelow(fit.Columns, fit.Columns.ModelMax.y + 1f);
            double below = PartingCentre.SolidExtentBelow(fit.Columns, fit.Field.H[0]);
            Assert.That(below / total, Is.EqualTo(0.5).Within(1e-3));

            // Control: the bounding-box midpoint does not halve this shape.
            float mid = 0.5f * (fit.Columns.ModelMin.y + fit.Columns.ModelMax.y);
            double midFrac = PartingCentre.SolidExtentBelow(fit.Columns, mid) / total;
            Assert.Greater(Mathf.Abs((float)midFrac - 0.5f), 0.05f);
        }

        /// Rotated and moved anywhere, the split lands on the same material. Frame heights pick
        /// up the translation projected onto the pull axis and nothing else.
        [Test]
        public void TheCentreSurvivesRotationAndTranslation()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] sphereV, out int[] sphereT);
            Box(new Vector3(0f, 1.3f, 0f), new Vector3(0.1f, 0.75f, 0.1f), out Vector3[] finV, out int[] finT);

            var v = new List<Vector3>(sphereV);
            var t = new List<int>(sphereT);
            Append(v, t, finV, finT);
            Vector3[] upright = v.ToArray();
            int[] tris = t.ToArray();

            var s = DefaultSettings();
            s.AutoGridDensity = true;
            s.GridCellMm = 1f;
            s.CentreOnVolume = true;

            MoldFit a = PartingSurfaceFitter.Flat(upright, tris, upright.Length, MoldFrame.Identity, s);

            Quaternion rot = Quaternion.Euler(23f, -41f, 17f);
            Vector3 shift = new Vector3(84.5f, -37.25f, 61.75f);
            var moved = new Vector3[upright.Length];
            for (int i = 0; i < upright.Length; i++) moved[i] = rot * upright[i] + shift;

            MoldFrame frame = MoldFrame.FromRightUp(rot * Vector3.right, rot * Vector3.up);
            MoldFit b = PartingSurfaceFitter.Flat(moved, tris, moved.Length, frame, s);

            Assert.IsTrue(a.IsValid && b.IsValid);
            Assert.AreEqual(a.Field.H[0] + Vector3.Dot(shift, frame.Up), b.Field.H[0], 0.02f);

            double total = PartingCentre.SolidExtentBelow(b.Columns, b.Columns.ModelMax.y + 1f);
            double below = PartingCentre.SolidExtentBelow(b.Columns, b.Field.H[0]);
            Assert.That(below / total, Is.EqualTo(0.5).Within(5e-3));
        }
            // ------------------------------------------------------------ sprue, runner, gates

        /// A fine UV sphere as world arrays - fine enough that clustering it is a real test.
        private static void FineSphere(float radius, int rings, int segs, out Vector3[] verts, out int[] tris)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            for (int i = 0; i <= rings; i++)
            {
                float phi = Mathf.PI * i / rings;
                for (int j = 0; j <= segs; j++)
                {
                    float th = 2f * Mathf.PI * j / segs;
                    v.Add(new Vector3(radius * Mathf.Sin(phi) * Mathf.Cos(th), radius * Mathf.Cos(phi),
                                      radius * Mathf.Sin(phi) * Mathf.Sin(th)));
                }
            }
            for (int i = 0; i < rings; i++)
                for (int j = 0; j < segs; j++)
                {
                    int a = i * (segs + 1) + j, d = a + segs + 1;
                    t.Add(a); t.Add(a + 1); t.Add(d);
                    t.Add(a + 1); t.Add(d + 1); t.Add(d);
                }
            verts = v.ToArray();
            tris = t.ToArray();
            // Wound to match Unity's own primitives, whatever the loop above produced.
            if (Mathf.Sign(MoldBuilder.SignedVolume(verts, tris)) != ReferenceVolumeSign())
                for (int k = 0; k < tris.Length; k += 3) { int x = tris[k + 1]; tris[k + 1] = tris[k + 2]; tris[k + 2] = x; }
        }

        /// Settings at a real-world scale: a 1-unit sphere declared 100mm across, so the addon's
        /// millimetre sizes (14mm sprue, 3mm runner, 15mm padding) land where a user's would.
        private static MoldSettings PhysicalSettings()
        {
            var s = new MoldSettings { AutoGridDensity = false, GridAcross = 81, GridDepth = 81, Rounding = 2,
                                       BlendRadius = 1, MinSamplesPerCell = 2 };
            s.MillimetresPerUnit = 100f;
            return s;
        }

        /// The fix for the sprue "mushed up against the head": the funnel has to stand off the
        /// model, joined to it only by the thin runner, and the runner has to actually break in.
        [Test]
        public void SprueFunnelStandsOffTheModelOnARunner()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldSettings s = PhysicalSettings();
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);
            Assert.IsTrue(fit.IsValid, fit.Error);

            MoldFeature sprue = MoldGeometry.SuggestChannels(fit.Block, s, fit.Field, fit.Columns)
                                            .Find(f => f.Kind == MoldFeatureKind.Sprue);
            Assert.IsNotNull(sprue);
            MoldGeometry.ChannelLayout c = MoldGeometry.LayoutChannel(sprue, fit.Field, fit.Block, fit.Columns, s);

            Assert.IsTrue(c.Connected, "the sprue's line has to meet the model");
            Assert.IsTrue(c.HasFunnel);
            // The gate is the sphere's outline, to within the parting grid.
            float cell = fit.Block.Width / 80f;
            Assert.AreEqual(0.5f, c.Gate.magnitude, 1.5f * cell, "the gate should sit on the model's outline");
            // The runner reaches INSIDE the model by the overlap...
            Assert.Less(c.Inner.magnitude, 0.5f - 0.5f * s.ChannelOverlap, "the runner has to break into the cavity");
            // ...while the funnel starts clear of it, by at least the runner gap.
            float gap = MoldGeometry.RunnerGap(sprue.RunnerRadius, s);
            Assert.Greater(Vector2.Distance(c.Gate, c.Hub), 0.99f * gap, "the funnel must stand off the model");
            Assert.Greater(c.Hub.magnitude, 0.5f + 0.99f * gap);
            Assert.Less(c.ChannelRadius, c.FunnelInner, "the runner is the thin part");

            // And the funnel's actual solid never touches the model: every vertex of it lies
            // outside the sphere.
            var fv = new List<Vector3>();
            var ft = new List<int>();
            MoldGeometry.AddSweptChannel(fv, ft, fit.Field, c.Hub, c.Exit, c.FunnelInner, c.FunnelOuter);
            foreach (Vector3 p in fv)
                Assert.Greater(p.magnitude, 0.5f, "a funnel vertex is inside the model - the plug would fuse to it");
        }

        /// A sprue dragged INTO the model is held back out of it: the gizmo cannot push the funnel
        /// into the head.
        [Test]
        public void SprueHubIsHeldClearOfTheModel()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldSettings s = PhysicalSettings();
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);

            var sprue = new MoldFeature(MoldFeatureKind.Sprue, 0.2f, 0f)
            {
                AngleDeg = 0f,
                RadiusInner = s.SprueRadiusInner,
                RadiusOuter = s.SprueRadiusOuter,
                RunnerRadius = s.RunnerRadius,
            };
            MoldGeometry.ChannelLayout c = MoldGeometry.LayoutChannel(sprue, fit.Field, fit.Block, fit.Columns, s);
            Assert.IsTrue(c.Connected);
            Assert.Greater(c.Hub.x, 0.5f, "a hub asked for inside the model must be pushed back outside it");
        }

        /// A vent anchored off the model (in the padding past its tail) still reaches the cavity -
        /// the gate is found along its line, not taken from where it was dropped.
        [Test]
        public void VentAnchoredOffTheModelStillReachesTheCavity()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldSettings s = PhysicalSettings();
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);

            var vent = new MoldFeature(MoldFeatureKind.Vent, fit.Block.R0 + 0.02f, 0f)
            {
                AngleDeg = 180f,
                RadiusInner = s.VentRadius,
                RadiusOuter = s.VentRadius,
            };
            MoldGeometry.ChannelLayout c = MoldGeometry.LayoutChannel(vent, fit.Field, fit.Block, fit.Columns, s);
            Assert.IsTrue(c.Connected);
            Assert.AreEqual(-0.5f, c.Gate.x, fit.Block.Width / 40f, "gate should be the sphere's left edge");
            Assert.Greater(c.Inner.x, -0.5f + 0.5f * s.ChannelOverlap, "the vent has to reach inside");
            Assert.Less(c.Exit.x, fit.Block.R0, "and open past the wall");

            // A vent whose line never crosses the model says so instead of cutting a dead hole.
            var dead = new MoldFeature(MoldFeatureKind.Vent, fit.Block.R1 - 0.02f, fit.Block.E1 - 0.02f)
            {
                AngleDeg = 90f,
                RadiusInner = s.VentRadius,
                RadiusOuter = s.VentRadius,
            };
            Assert.IsFalse(MoldGeometry.LayoutChannel(dead, fit.Field, fit.Block, fit.Columns, s).Connected);
        }

        /// A rotated channel leaves through the wall it points at, not the one nearest its anchor.
        [Test]
        public void RotatedChannelExitsWhereItPoints()
        {
            PrimitiveMesh(PrimitiveType.Sphere, out Vector3[] v, out int[] t);
            MoldSettings s = PhysicalSettings();
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);
            MoldBlock b = fit.Block;

            var vent = new MoldFeature(MoldFeatureKind.Vent, 0.45f, 0f) { AngleDeg = 90f, RadiusInner = s.VentRadius };
            MoldGeometry.ChannelLayout c = MoldGeometry.LayoutChannel(vent, fit.Field, b, fit.Columns, s);
            Assert.Greater(c.Exit.y, b.E1, "pointing +Eye, it has to leave through the +Eye wall");
            Assert.AreEqual(0.45f, c.Exit.x, 1e-4f);

            vent.AngleDeg = 45f;
            c = MoldGeometry.LayoutChannel(vent, fit.Field, b, fit.Columns, s);
            Vector2 along = (c.Exit - c.Gate).normalized;
            Assert.AreEqual(Mathf.Cos(45f * Mathf.Deg2Rad), along.x, 1e-3f, "the channel has to run the way it points");
            Assert.AreEqual(Mathf.Sin(45f * Mathf.Deg2Rad), along.y, 1e-3f);
        }

        [Test]
        public void MirroredChannelsMirrorTheirDirection()
        {
            var block = new MoldBlock(new Vector3(-1f, -0.1f, -0.5f), new Vector3(1f, 0.1f, 0.5f), 0.2f, 0.2f);
            var s = new MoldSettings { MirrorAcrossRight = true, MirrorAcrossEye = true };
            var vent = new MoldFeature(MoldFeatureKind.Vent, 0.4f, 0.25f) { AngleDeg = 30f };
            var into = new List<MoldFeature>();
            MoldGeometry.ExpandMirrors(vent, block, s, into);

            Assert.AreEqual(4, into.Count);
            var angles = new HashSet<float>();
            foreach (MoldFeature f in into) angles.Add(Mathf.Round(f.AngleDeg));
            Assert.IsTrue(angles.SetEquals(new[] { 30f, 150f, 330f, 210f }),
                          "mirrored across R: 180-a; across E: -a; both: a+180");
        }

        /// The addon's rule: the sprue aims at the CENTRE of the head's cross-section, not at the
        /// bounding box's midline - which on an off-centre head misses it.
        [Test]
        public void SuggestedSprueCentresOnAnOffCentreHead()
        {
            // A body, and a head standing off its centre line at the +R end. Disjoint - two
            // boxes sharing a face lose one of them in the column map (see the round-4 notes).
            Box(new Vector3(0f, 0f, 0f), new Vector3(0.5f, 0.1f, 0.2f), out Vector3[] aV, out int[] aT);
            Box(new Vector3(0.61f, 0f, 0.2f), new Vector3(0.09f, 0.1f, 0.1f), out Vector3[] bV, out int[] bT);
            var v = new List<Vector3>(aV);
            var t = new List<int>(aT);
            Append(v, t, bV, bT);

            MoldSettings s = PhysicalSettings();
            MoldFit fit = PartingSurfaceFitter.Flat(v.ToArray(), t.ToArray(), v.Count, MoldFrame.Identity, s);
            Assert.IsTrue(fit.IsValid, fit.Error);

            MoldFeature sprue = MoldGeometry.SuggestChannels(fit.Block, s, fit.Field, fit.Columns)
                                            .Find(f => f.Kind == MoldFeatureKind.Sprue);
            float midline = 0.5f * (fit.Block.ModelMin.z + fit.Block.ModelMax.z);
            float cell = fit.Block.Depth / 80f;
            Assert.AreEqual(0.2f, sprue.E, 1.5f * cell, "the sprue should line up with the head's centre");
            Assert.Greater(Mathf.Abs(sprue.E - midline), 0.1f, "not with the bounding box's midline");
            MoldGeometry.ChannelLayout c = MoldGeometry.LayoutChannel(sprue, fit.Field, fit.Block, fit.Columns, s);
            Assert.IsTrue(c.Connected);
            Assert.AreEqual(0.7f, c.Gate.x, 1.5f * cell, "and feed the head's tip");
        }

        // ------------------------------------------------------------------ the cutter proxy

        /// Clustering has to keep a closed surface closed - it is what the winding-number boolean
        /// needs - and keep its volume: every edge used once each way, and the solid barely moved.
        [Test]
        public void CutterProxyStaysClosedAndKeepsItsVolume()
        {
            FineSphere(0.5f, 96, 192, out Vector3[] v, out int[] t);
            MoldCutterProxy.Cluster(v, v.Length, t, t.Length, 0.04f, out Vector3[] pv, out int[] pt);

            Assert.Less(pt.Length, t.Length / 4, "the proxy should be much lighter than the source");
            Assert.Greater(pt.Length, 300, "but still a sphere");

            var balance = new Dictionary<long, int>();
            for (int k = 0; k < pt.Length; k += 3)
                for (int e = 0; e < 3; e++)
                {
                    int a = pt[k + e], b = pt[k + (e + 1) % 3];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    balance.TryGetValue(key, out int n);
                    balance[key] = n + (a < b ? 1 : -1);
                }
            foreach (KeyValuePair<long, int> kv in balance)
                Assert.AreEqual(0, kv.Value, "an edge is used more one way than the other - the proxy has a hole");

            float before = Mathf.Abs(MoldBuilder.SignedVolume(v, t));
            float after = Mathf.Abs(MoldBuilder.SignedVolume(pv, pt));
            Assert.AreEqual(before, after, 0.03f * before, "clustering moved the surface too far");
        }

        /// The proxy exists to make builds fast without changing them. A build against the proxy
        /// has to come out the same as a build against the full model.
        [Test]
        public void BuildAgainstTheProxyMatchesTheFullModel()
        {
            FineSphere(0.5f, 96, 192, out Vector3[] v, out int[] t);
            MoldSettings s = DefaultSettings();
            MoldFit fit = PartingSurfaceFitter.Flat(v, t, v.Length, MoldFrame.Identity, s);
            const int resolution = 64;
            float extent = Mathf.Max(fit.Block.Width, Mathf.Max(fit.Block.Depth, fit.Block.Height));
            MoldCutterProxy.Cluster(v, v.Length, t, t.Length, extent / resolution * MoldCutterProxy.DraftCellFraction,
                                    out Vector3[] pv, out int[] pt);

            var none = new List<MoldFeature>();
            MoldBuilder.Result full = MoldBuilder.Build(v, t, v.Length, fit.Field, fit.Block, fit.Columns, none, s, resolution);
            MoldBuilder.Result proxy = MoldBuilder.Build(pv, pt, pv.Length, fit.Field, fit.Block, fit.Columns, none, s, resolution);
            try
            {
                Assert.IsTrue(full.Success, full.Error);
                Assert.IsTrue(proxy.Success, proxy.Error);
                float a = Volume(full.LowerHalf) + Volume(full.UpperHalf);
                float b = Volume(proxy.LowerHalf) + Volume(proxy.UpperHalf);
                Assert.AreEqual(a, b, 0.005f * a, "the proxy changed the mold");
            }
            finally
            {
                Dispose(full, proxy);
            }
        }

        // ---------------------------------------------------------------- the fit's fill pass

        /// FillHoles was rewritten for speed (parallel Jacobi, swapped buffers, indexed cells) on
        /// the promise that the output is bit-for-bit the old one's - the padding it extrapolates
        /// is where the halves seal, so "close" is not good enough. This is the old loop, kept
        /// verbatim, run against the new one on random fields large enough to take the parallel
        /// path.
        [Test]
        public void FillHolesIsBitIdenticalToTheReferenceRelaxation()
        {
            var rng = new System.Random(1234);
            foreach (var dims in new[] { (21, 13), (161, 97), (301, 121) })
            {
                var a = new PartingField(MoldFrame.Identity, -1f, 1f, -0.5f, 0.5f, dims.Item1, dims.Item2);
                var known = new bool[a.H.Length];
                for (int k = 0; k < a.H.Length; k++)
                {
                    known[k] = rng.NextDouble() < 0.2;
                    a.H[k] = known[k] ? (float)(rng.NextDouble() * 2.0 - 1.0) : 0f;
                }
                PartingField b = a.Clone();

                a.FillHoles(known);
                ReferenceFillHoles(b, known);

                for (int k = 0; k < a.H.Length; k++)
                    Assert.AreEqual(System.BitConverter.SingleToInt32Bits(b.H[k]), System.BitConverter.SingleToInt32Bits(a.H[k]),
                                    $"node {k} of a {dims.Item1}x{dims.Item2} grid differs from the reference");
            }
        }

        private static void ReferenceFillHoles(PartingField f, bool[] known)
        {
            int n = f.H.Length, nr = f.Nr, ne = f.Ne;
            float[] h = f.H;
            int knownCount = 0;
            for (int k = 0; k < n; k++) if (known[k]) knownCount++;
            var picked = new float[knownCount];
            int w = 0;
            for (int k = 0; k < n; k++) if (known[k]) picked[w++] = h[k];
            System.Array.Sort(picked);
            float median = picked[knownCount / 2];
            for (int k = 0; k < n; k++) if (!known[k]) h[k] = median;

            var tmp = new float[n];
            int iterations = Mathf.Max(nr, ne) * 2;
            for (int it = 0; it < iterations; it++)
            {
                float maxDelta = 0f;
                for (int i = 0; i < nr; i++)
                {
                    int im = Mathf.Max(i - 1, 0) * ne, ip = Mathf.Min(i + 1, nr - 1) * ne, ic = i * ne;
                    for (int j = 0; j < ne; j++)
                    {
                        int at = ic + j;
                        if (known[at]) { tmp[at] = h[at]; continue; }
                        int jm = Mathf.Max(j - 1, 0), jp = Mathf.Min(j + 1, ne - 1);
                        float avg = (h[im + j] + h[ip + j] + h[ic + jm] + h[ic + jp]) * 0.25f;
                        maxDelta = Mathf.Max(maxDelta, Mathf.Abs(avg - h[at]));
                        tmp[at] = avg;
                    }
                }
                System.Array.Copy(tmp, h, n);
                if (maxDelta <= 1e-6f) break;
            }
        }

        // ------------------------------------------------------------------------------ undo

        /// A step restores only what it changed: undoing a feature edit must not also revert a
        /// setting the user changed in a different step.
        [Test]
        public void UndoStepsRestoreOnlyWhatTheyChanged()
        {
            var settings = new MoldSettings();
            var features = new List<MoldFeature>();
            float offset = 0f;
            MoldFrame frame = MoldFrame.Identity;

            // Step 1 changes the padding.
            MoldState s0 = MoldState.Capture(settings, features, offset, frame);
            settings.PaddingMm = 22f;
            MoldState s1 = MoldState.Capture(settings, features, offset, frame);
            // Step 2 places a vent.
            features.Add(new MoldFeature(MoldFeatureKind.Vent, 0.3f, 0.1f) { Id = 7, AngleDeg = 90f });
            MoldState s2 = MoldState.Capture(settings, features, offset, frame);

            MoldState.Delta step1 = MoldState.Compare(s0, s1);
            MoldState.Delta step2 = MoldState.Compare(s1, s2);
            Assert.IsFalse(step1.Features);
            Assert.AreEqual(1, step1.SettingsFields.Count);
            Assert.IsTrue(step2.Features);
            Assert.AreEqual(0, step2.SettingsFields.Count);

            // Meanwhile the view changes outside either step, then step 2 is undone.
            settings.ViewMode = MoldViewMode.Exploded;
            s1.ApplyTo(step2, settings, features, ref offset, ref frame);
            Assert.AreEqual(0, features.Count, "the vent should be gone");
            Assert.AreEqual(22f, settings.PaddingMm, "undoing the vent must not touch the padding");
            Assert.AreEqual(MoldViewMode.Exploded, settings.ViewMode, "or the view");

            // Undo step 1 as well, then redo both.
            s0.ApplyTo(step1, settings, features, ref offset, ref frame);
            Assert.AreEqual(15f, settings.PaddingMm);
            s1.ApplyTo(step1, settings, features, ref offset, ref frame);
            s2.ApplyTo(step2, settings, features, ref offset, ref frame);
            Assert.AreEqual(22f, settings.PaddingMm);
            Assert.AreEqual(1, features.Count);
            Assert.AreEqual(7, features[0].Id, "a restored feature keeps its identity, so the selection can follow it");
            Assert.AreEqual(90f, features[0].AngleDeg);
        }

        [Test]
        public void RescalingTheModelKeepsPlacedFeaturesTheirPhysicalSize()
        {
            var s = new MoldSettings();
            s.SetModelLengthMm(100f, 2f); // 50 mm per unit
            var sprue = new MoldFeature(MoldFeatureKind.Sprue, 0f, 0f) { RadiusInner = s.SprueRadiusInner, RunnerRadius = s.RunnerRadius };
            float before = s.ToMillimetres(2f * sprue.RadiusInner);

            float old = s.MillimetresPerUnit;
            s.SetModelLengthMm(60f, 2f);
            sprue.ScaleSizes(old / s.MillimetresPerUnit);

            Assert.AreEqual(before, s.ToMillimetres(2f * sprue.RadiusInner), 1e-3f, "a 14mm sprue has to stay 14mm");
            Assert.AreEqual(3f, s.ToMillimetres(2f * sprue.RunnerRadius), 1e-3f);
        }
    }
}
