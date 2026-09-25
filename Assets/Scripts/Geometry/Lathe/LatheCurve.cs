using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Turns a LatheProfile's control points into a smooth curve, and resamples that curve at an
    /// even arc-length spacing for the mesh builder.
    ///
    /// Cubic Hermite through every point, with each tangent's DIRECTION taken from the bisector of
    /// the two neighbouring chords and its LENGTH from the segment's own chord. Two properties
    /// follow that plain Catmull-Rom does not have: unevenly spaced points never produce loops or
    /// cusps (a short segment next to a long one only gets a short tangent), and the overshoot past
    /// a point is bounded by the chord, so a curve that hugs the axis cannot swing far across it.
    ///
    /// The curve is split into SPANS at every sharp point (and at the ends of an open profile).
    /// Each span is resampled on its own with a spacing adjusted to divide it exactly, so a corner
    /// always lands on a sample - otherwise the resampler would round it off, and a flat base
    /// would come out bevelled.
    public static class LatheCurve
    {
        public struct Sample
        {
            public Vector2 Position;
            /// Sits on a sharp control point or an open end, where the curve's direction jumps.
            public bool Corner;
        }

        // Evaluations per control segment for arc-length measurement. Resampling reads positions
        // off this polyline, so it has to be fine enough that the chords are indistinguishable
        // from the curve at any spacing the mesh could ask for.
        private const int DenseStepsPerSegment = 48;

        // ------------------------------------------------------------------- evaluation

        public static int SegmentCount(IReadOnlyList<LatheProfile.Point> points, bool loop)
        {
            int n = points.Count;
            if (n < 2) return 0;
            return loop ? n : n - 1;
        }

        /// A point on control segment `segment` (from point segment to point segment+1, wrapping
        /// for a loop) at parameter t in [0, 1].
        public static Vector2 Evaluate(IReadOnlyList<LatheProfile.Point> points, bool loop, int segment, float t)
        {
            int n = points.Count;
            int a = segment;
            int b = loop ? (segment + 1) % n : segment + 1;

            Vector2 p0 = points[a].Position;
            Vector2 p1 = points[b].Position;
            float chord = (p1 - p0).magnitude;
            if (chord < 1e-9f) return p0;

            Vector2 m0 = OutgoingTangent(points, loop, a) * chord;
            Vector2 m1 = IncomingTangent(points, loop, b) * chord;

            float t2 = t * t;
            float t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * p0 + (t3 - 2f * t2 + t) * m0 +
                   (-2f * t3 + 3f * t2) * p1 + (t3 - t2) * m1;
        }

        /// Unit direction the curve LEAVES point i in.
        private static Vector2 OutgoingTangent(IReadOnlyList<LatheProfile.Point> points, bool loop, int i)
        {
            int n = points.Count;
            bool hasPrev = loop || i > 0;
            int next = loop ? (i + 1) % n : i + 1;
            Vector2 chordOut = Direction(points[i].Position, points[next].Position);

            if (points[i].Sharp) return chordOut;
            if (!hasPrev) return OpenEndTangent(points, i, next, chordOut, leaving: true);
            return SmoothTangent(points, loop, i, chordOut);
        }

        /// Unit direction the curve ARRIVES at point i in.
        private static Vector2 IncomingTangent(IReadOnlyList<LatheProfile.Point> points, bool loop, int i)
        {
            int n = points.Count;
            bool hasNext = loop || i < n - 1;
            int prev = loop ? (i - 1 + n) % n : i - 1;
            Vector2 chordIn = Direction(points[prev].Position, points[i].Position);

            if (points[i].Sharp) return chordIn;
            if (!hasNext) return OpenEndTangent(points, i, prev, chordIn, leaving: false);
            return SmoothTangent(points, loop, i, chordIn);
        }

        /// Bisector of the unit chords either side - the direction a smooth curve through the three
        /// points is heading in at the middle one.
        private static Vector2 SmoothTangent(IReadOnlyList<LatheProfile.Point> points, bool loop, int i, Vector2 fallback)
        {
            int n = points.Count;
            int prev = loop ? (i - 1 + n) % n : i - 1;
            int next = loop ? (i + 1) % n : i + 1;
            Vector2 sum = Direction(points[prev].Position, points[i].Position) +
                          Direction(points[i].Position, points[next].Position);
            float len = sum.magnitude;
            // A hairpin (the curve doubling straight back) has no bisector; the chord is as good
            // an answer as any, and this only ever happens mid-drag.
            return len > 1e-6f ? sum / len : fallback;
        }

        /// The free end of an open profile. On the axis it leaves horizontally, so the pole closes
        /// in a rounded dome instead of a cone point (mark the point Sharp for a point). Off the
        /// axis the end mirrors its neighbour's tangent across the chord, so the last segment is a
        /// symmetric arc rather than a curve that straightens out as it reaches the end.
        private static Vector2 OpenEndTangent(IReadOnlyList<LatheProfile.Point> points, int end, int neighbour,
                                              Vector2 chord, bool leaving)
        {
            Vector2 endPos = points[end].Position;
            if (endPos.x <= 0f)
            {
                // Travel direction at the end: away from the axis when leaving it, toward it when
                // arriving.
                return new Vector2(leaving ? 1f : -1f, 0f);
            }

            if (points.Count < 3) return chord;

            // The neighbour's own smooth tangent, reflected across this segment's chord.
            int n = points.Count;
            Vector2 neighbourTangent;
            if (points[neighbour].Sharp) return chord;
            if (leaving)
            {
                int after = neighbour + 1;
                if (after >= n) return chord;
                neighbourTangent = Direction(points[end].Position, points[neighbour].Position) +
                                   Direction(points[neighbour].Position, points[after].Position);
            }
            else
            {
                int before = neighbour - 1;
                if (before < 0) return chord;
                neighbourTangent = Direction(points[before].Position, points[neighbour].Position) +
                                   Direction(points[neighbour].Position, points[end].Position);
            }

            float len = neighbourTangent.magnitude;
            if (len < 1e-6f) return chord;
            neighbourTangent /= len;
            Vector2 reflected = 2f * Vector2.Dot(chord, neighbourTangent) * chord - neighbourTangent;
            float rlen = reflected.magnitude;
            return rlen > 1e-6f ? reflected / rlen : chord;
        }

        private static Vector2 Direction(Vector2 from, Vector2 to)
        {
            Vector2 d = to - from;
            float len = d.magnitude;
            return len > 1e-9f ? d / len : Vector2.zero;
        }

        // ------------------------------------------------------------------- resampling

        /// Resamples the whole curve at (close to) `spacing`, appending to `output`. An open
        /// profile yields its first and last point exactly; a loop yields no duplicate closing
        /// sample (the mesh builder wraps it).
        public static void Resample(IReadOnlyList<LatheProfile.Point> points, bool loop, float spacing, List<Sample> output)
        {
            int n = points.Count;
            if (n < 2 || spacing <= 0f) return;
            int segments = SegmentCount(points, loop);

            // Span boundaries: the control points where the curve may turn a corner. An open
            // profile's ends always are; a loop with no sharp points is one span that starts and
            // ends at point 0.
            var boundaries = new List<int>();
            for (int i = 0; i < n; i++)
            {
                bool end = !loop && (i == 0 || i == n - 1);
                if (end || points[i].Sharp) boundaries.Add(i);
            }

            bool seamless = loop && boundaries.Count == 0;
            if (seamless) boundaries.Add(0);

            var dense = new List<Vector2>(DenseStepsPerSegment * 8);
            var cumulative = new List<float>(DenseStepsPerSegment * 8);

            int spanCount = loop ? boundaries.Count : boundaries.Count - 1;
            for (int s = 0; s < spanCount; s++)
            {
                int startPoint = boundaries[s];
                int endPoint = loop ? boundaries[(s + 1) % boundaries.Count] : boundaries[s + 1];
                int spanSegments = loop
                    ? ((endPoint - startPoint + n) % n == 0 ? n : (endPoint - startPoint + n) % n)
                    : endPoint - startPoint;
                if (spanSegments <= 0) continue;

                // Dense polyline for this span.
                dense.Clear();
                cumulative.Clear();
                dense.Add(points[startPoint].Position);
                cumulative.Add(0f);
                for (int k = 0; k < spanSegments; k++)
                {
                    int seg = (startPoint + k) % (loop ? n : int.MaxValue);
                    if (seg >= segments) break;
                    for (int step = 1; step <= DenseStepsPerSegment; step++)
                    {
                        Vector2 p = Evaluate(points, loop, seg, step / (float)DenseStepsPerSegment);
                        cumulative.Add(cumulative[cumulative.Count - 1] + (p - dense[dense.Count - 1]).magnitude);
                        dense.Add(p);
                    }
                }

                float length = cumulative[cumulative.Count - 1];
                // A loop needs three samples at the very least to enclose anything.
                int minPieces = loop ? (3 + spanCount - 1) / spanCount : 1;
                int pieces = Mathf.Max(minPieces, Mathf.RoundToInt(length / spacing));
                bool corner = !seamless;

                // The span's start sample. Every later span starts where the previous one ended,
                // so only the very first span emits its start point.
                if (s == 0) output.Add(new Sample { Position = dense[0], Corner = corner });

                int cursor = 0;
                for (int k = 1; k <= pieces; k++)
                {
                    // Closing sample of a loop's last span is the first sample again - skipped.
                    if (loop && s == spanCount - 1 && k == pieces) break;

                    if (k == pieces)
                    {
                        // The span's end, exactly - it is a control point, and a corner.
                        output.Add(new Sample { Position = dense[dense.Count - 1], Corner = !seamless });
                        break;
                    }

                    float target = length * k / pieces;
                    while (cursor < cumulative.Count - 2 && cumulative[cursor + 1] < target) cursor++;
                    float segLen = cumulative[cursor + 1] - cumulative[cursor];
                    float t = segLen > 1e-12f ? (target - cumulative[cursor]) / segLen : 0f;
                    output.Add(new Sample { Position = Vector2.Lerp(dense[cursor], dense[cursor + 1], t) });
                }
            }
        }

        /// Chord length of the control polygon - a cheap upper-ish estimate of the curve's length,
        /// used to cap the sample count before resampling.
        public static float ControlPolygonLength(IReadOnlyList<LatheProfile.Point> points, bool loop)
        {
            int n = points.Count;
            float sum = 0f;
            for (int i = 0; i + 1 < n; i++) sum += (points[i + 1].Position - points[i].Position).magnitude;
            if (loop && n > 2) sum += (points[0].Position - points[n - 1].Position).magnitude;
            return sum;
        }
    }
}
