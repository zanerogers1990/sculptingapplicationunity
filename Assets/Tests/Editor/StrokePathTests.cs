using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Sculpting.Tests
{
    /// StrokePath - the curve a Clay stroke's dabs are spaced along, through every input sample of
    /// the frame rather than one straight line per frame.
    public class StrokePathTests
    {
        private static StrokePath.Knot Knot(Vector3 point, float pressure = 1f) =>
            new StrokePath.Knot { Point = point, Normal = Vector3.up, Pressure = pressure };

        private static List<StrokePath.Dab> Walk(IList<StrokePath.Knot> knots, float spacing, bool flush = true)
        {
            var path = new StrokePath();
            var dabs = new List<StrokePath.Dab>();
            int budget = int.MaxValue;
            foreach (StrokePath.Knot knot in knots) path.AddKnot(knot, spacing, ref budget, dabs);
            if (flush) path.Flush(spacing, ref budget, dabs);
            return dabs;
        }

        /// Samples 30 degrees apart round a circle - what a fast circular stroke delivers. Joined by
        /// chords, the dabs would cut inside the circle by up to R(1 - cos 15deg), 3.4% of the radius;
        /// along the spline they stay on it, evenly spaced.
        [Test]
        public void DabsFollowTheCurveThroughTheSamplesNotItsChords()
        {
            const float radius = 0.2f, spacing = 0.005f;
            var knots = new List<StrokePath.Knot>();
            for (int k = 0; k <= 16; k++)
            {
                float a = k * 30f * Mathf.Deg2Rad;
                knots.Add(Knot(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius));
            }
            List<StrokePath.Dab> dabs = Walk(knots, spacing);

            float worst = 0f;
            int checkedDabs = 0;
            for (int i = 0; i < dabs.Count; i++)
            {
                Vector3 p = dabs[i].Point;
                float angle = Mathf.Atan2(p.z, p.x) * Mathf.Rad2Deg;
                if (angle < 0f) angle += 360f;
                // The end segments are closed with phantom knots; judge the interior.
                float travelled = i * spacing / radius * Mathf.Rad2Deg;
                if (travelled < 30f || travelled > 16 * 30f - 30f) continue;
                worst = Mathf.Max(worst, Mathf.Abs(p.magnitude - radius));
                checkedDabs++;
            }
            float chordSag = radius * (1f - Mathf.Cos(15f * Mathf.Deg2Rad));
            TestContext.WriteLine($"worst radial error {worst / radius:P3} of R over {checkedDabs} dabs (chords: {chordSag / radius:P2})");
            Assert.That(checkedDabs, Is.GreaterThan(250));
            Assert.That(worst, Is.LessThan(chordSag * 0.1f), "Dabs cut the corners between samples.");

            for (int i = 1; i < dabs.Count; i++)
                Assert.That(Vector3.Distance(dabs[i - 1].Point, dabs[i].Point), Is.EqualTo(spacing).Within(spacing * 0.01f),
                    $"Dab {i} is not one spacing from the last.");
        }

        /// Pressure changes linearly between samples along the path, dab by dab - not once per
        /// frame, and not in steps at the samples.
        [Test]
        public void PressureIsInterpolatedPerDab()
        {
            var knots = new List<StrokePath.Knot>
            {
                Knot(new Vector3(0f, 0f, 0f), 0.2f),
                Knot(new Vector3(0.1f, 0f, 0f), 0.6f),
                Knot(new Vector3(0.2f, 0f, 0f), 1.0f),
            };
            List<StrokePath.Dab> dabs = Walk(knots, 0.01f);

            Assert.That(dabs.Count, Is.InRange(20, 21), "one tap dab and one every 0.01 of 0.2");
            for (int i = 0; i < dabs.Count; i++)
            {
                float expected = 0.2f + 4f * dabs[i].Point.x;
                Assert.That(dabs[i].Pressure, Is.EqualTo(expected).Within(1e-3f), $"dab {i} at x={dabs[i].Point.x:F3}");
                if (i > 0) Assert.That(dabs[i].Pressure, Is.GreaterThan(dabs[i - 1].Pressure), $"dab {i} did not rise");
            }
        }

        [Test]
        public void ATapPlacesOneDabAndAStillPointerNone()
        {
            var path = new StrokePath();
            var dabs = new List<StrokePath.Dab>();
            int budget = 100;
            path.AddKnot(Knot(Vector3.one), 0.01f, ref budget, dabs);
            Assert.That(dabs.Count, Is.EqualTo(1), "A tap must still mark the surface.");

            Assert.That(path.AddKnot(Knot(Vector3.one), 0.01f, ref budget, dabs), Is.False);
            path.Flush(0.01f, ref budget, dabs);
            Assert.That(dabs.Count, Is.EqualTo(1), "A pointer that never moved deposited more.");
        }

        /// The newest segment waits for the knot after it; Flush closes it, once.
        [Test]
        public void TheNewestSegmentWaitsForItsNextKnotOrAFlush()
        {
            var path = new StrokePath();
            var dabs = new List<StrokePath.Dab>();
            int budget = 100;
            path.AddKnot(Knot(Vector3.zero), 0.01f, ref budget, dabs);
            path.AddKnot(Knot(new Vector3(0.1f, 0f, 0f)), 0.01f, ref budget, dabs);
            Assert.That(dabs.Count, Is.EqualTo(1), "The first segment was walked before its next knot arrived.");
            Assert.That(path.HasPendingSegment, Is.True);

            path.Flush(0.01f, ref budget, dabs);
            Assert.That(dabs.Count, Is.EqualTo(11));
            path.Flush(0.01f, ref budget, dabs);
            Assert.That(dabs.Count, Is.EqualTo(11), "A flushed segment was walked twice.");

            // The next knot continues from the flushed end without re-walking it.
            path.AddKnot(Knot(new Vector3(0.2f, 0f, 0f)), 0.01f, ref budget, dabs);
            path.Flush(0.01f, ref budget, dabs);
            Assert.That(dabs.Count, Is.EqualTo(21));
        }

        /// Once the per-frame budget is spent the rest of the travel is dropped, not banked.
        [Test]
        public void ASpentBudgetDropsTheRestOfTheTravel()
        {
            var path = new StrokePath();
            var dabs = new List<StrokePath.Dab>();
            int budget = 4;
            path.AddKnot(Knot(Vector3.zero), 0.01f, ref budget, dabs);
            path.AddKnot(Knot(new Vector3(1f, 0f, 0f)), 0.01f, ref budget, dabs);
            path.Flush(0.01f, ref budget, dabs);
            Assert.That(dabs.Count, Is.EqualTo(4));
            Assert.That(path.Carry, Is.EqualTo(0f));
        }
    }
}
