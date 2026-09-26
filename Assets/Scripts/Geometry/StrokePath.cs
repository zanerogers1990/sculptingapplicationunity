using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// A brush stroke's path through its input samples, walked by arc length to place dabs.
    ///
    /// The samples ("knots" - mesh-local hit points with their surface normal and pen pressure)
    /// are joined by a centripetal Catmull-Rom spline rather than straight lines. A fast curved
    /// stroke delivers its samples far apart, and joining them with chords turned it into a
    /// polygon with a visible corner at every sample. Centripetal (alpha = 0.5) rather than the
    /// uniform or chordal variants because it is the one that never forms a cusp or a loop within
    /// a segment, whatever the spacing of the samples - and input samples are spaced very
    /// unevenly (the pointer accelerates, the OS batches, the renderer hitches).
    ///
    /// A segment's shape depends on the knot AFTER it, so the newest segment is always one knot
    /// behind: it is walked when the next knot arrives, or by Flush when the stroke ends or
    /// pauses, which closes it with a phantom end knot (the last knot mirrored through the one
    /// before) - the curve still passes exactly through every knot, only its end direction is a
    /// guess. The segment a Flush closed is never walked again.
    ///
    /// Dabs are placed every `spacing` of travel, continuing from the previous segment's leftover
    /// (the carry), so dab density along the curve is uniform. Normal and pressure are
    /// interpolated between the segment's two knots by arc-length fraction.
    ///
    /// Not a Clay-only type: anything that places dabs along a stroke can drive it.
    public sealed class StrokePath
    {
        public struct Knot
        {
            public Vector3 Point;
            public Vector3 Normal;
            public float Pressure;
        }

        public struct Dab
        {
            public Vector3 Point;
            public Vector3 Normal;
            public float Pressure;
        }

        // Dense evaluations per segment used to measure arc length: enough that the chords are
        // indistinguishable from the curve at the dab spacing, bounded so a long segment (a flick)
        // stays cheap. The count scales with the segment's length in dabs.
        private const int MinStepsPerSegment = 4;
        private const int MaxStepsPerSegment = 64;
        private const int StepsPerDab = 4;

        // Knots closer together than this are one knot - the spline's parameter steps would divide
        // by (nearly) zero, and a knot that goes nowhere adds nothing.
        private const float MinKnotDistance = 1e-6f;

        // The last three knots: _k2 is the newest, and the segment _k1 -> _k2 is the pending one.
        // _k0 is a real knot only when _count >= 3; otherwise the segment's start is closed with a
        // phantom mirrored through _k1.
        private Knot _k0, _k1, _k2;
        private int _count;
        private bool _pendingWalked;
        private float _carry;

        private readonly Vector3[] _dense = new Vector3[MaxStepsPerSegment + 1];
        private readonly float[] _cumulative = new float[MaxStepsPerSegment + 1];

        /// How many knots this stroke has had (saturates at 3 - only "none", "one" and "more"
        /// matter to callers).
        public int KnotCount => _count;

        /// The newest knot. Only meaningful when KnotCount > 0.
        public Knot Newest => _k2;

        /// Travel since the last dab, carried into the next segment.
        public float Carry => _carry;

        /// Whether a segment is waiting for its next knot (or a Flush).
        public bool HasPendingSegment => _count >= 2 && !_pendingWalked;

        public void Reset()
        {
            _count = 0;
            _pendingWalked = false;
            _carry = 0f;
        }

        /// Adds the next knot. The first knot of a stroke places one dab on itself - a tap still
        /// marks the surface. Every later knot completes the segment before it, whose dabs are
        /// appended to `output`. `budget` is the most dabs still allowed (a per-frame cost ceiling
        /// shared by every call of the frame); once it runs out the remaining travel is DROPPED
        /// rather than banked, so a stroke that fell behind does not pile a burst of dabs onto one
        /// spot next frame. Returns false (and does nothing) for a knot on top of the newest one.
        public bool AddKnot(Knot knot, float spacing, ref int budget, List<Dab> output)
        {
            if (_count == 0)
            {
                _k2 = knot;
                _count = 1;
                _carry = 0f;
                _pendingWalked = false;
                if (budget > 0)
                {
                    output.Add(new Dab { Point = knot.Point, Normal = knot.Normal, Pressure = knot.Pressure });
                    budget--;
                }
                return true;
            }

            if ((knot.Point - _k2.Point).sqrMagnitude <= MinKnotDistance * MinKnotDistance) return false;

            if (HasPendingSegment)
                WalkSegment(_count >= 3 ? _k0.Point : Mirror(_k1.Point, _k2.Point), _k1, _k2, knot.Point,
                    spacing, ref budget, output);

            _k0 = _k1;
            _k1 = _k2;
            _k2 = knot;
            _count = Mathf.Min(_count + 1, 3);
            _pendingWalked = false;
            return true;
        }

        /// Walks the pending segment now, with a phantom end - for the end of a stroke, or a pause
        /// long enough that waiting for the next knot would leave the tip of the stroke visibly
        /// behind the brush. Idempotent until the next knot arrives.
        public void Flush(float spacing, ref int budget, List<Dab> output)
        {
            if (!HasPendingSegment) return;
            WalkSegment(_count >= 3 ? _k0.Point : Mirror(_k1.Point, _k2.Point), _k1, _k2, Mirror(_k2.Point, _k1.Point),
                spacing, ref budget, output);
            _pendingWalked = true;
        }

        /// `through` reflected through `about`: the phantom neighbour that makes an open end leave
        /// along its own chord.
        private static Vector3 Mirror(Vector3 about, Vector3 through) => about + (about - through);

        private void WalkSegment(Vector3 p0, Knot a, Knot b, Vector3 p3, float spacing, ref int budget, List<Dab> output)
        {
            Vector3 p1 = a.Point, p2 = b.Point;
            float chord = Vector3.Distance(p1, p2);
            int steps = Mathf.Clamp(Mathf.CeilToInt(chord / Mathf.Max(spacing, 1e-9f) * StepsPerDab),
                MinStepsPerSegment, MaxStepsPerSegment);

            // Dense polyline along the curve, for its arc length.
            _dense[0] = p1;
            _cumulative[0] = 0f;
            for (int s = 1; s <= steps; s++)
            {
                _dense[s] = s == steps ? p2 : EvaluateCentripetal(p0, p1, p2, p3, s / (float)steps);
                _cumulative[s] = _cumulative[s - 1] + Vector3.Distance(_dense[s - 1], _dense[s]);
            }
            float length = _cumulative[steps];
            if (length <= 0f) return;

            for (int s = 1; s <= steps; s++)
            {
                float piece = _cumulative[s] - _cumulative[s - 1];
                if (budget <= 0)
                {
                    // Out of dabs for this frame: the rest of the travel is dropped, not banked.
                    _carry = 0f;
                    continue;
                }

                // Dabs falling inside this piece, measured from its start.
                float into = spacing - _carry;
                while (into <= piece && budget > 0)
                {
                    float f = piece > 0f ? into / piece : 1f;
                    float arc = (_cumulative[s - 1] + into) / length;
                    output.Add(new Dab
                    {
                        Point = Vector3.Lerp(_dense[s - 1], _dense[s], f),
                        Normal = Vector3.Slerp(a.Normal, b.Normal, arc).normalized,
                        Pressure = Mathf.Lerp(a.Pressure, b.Pressure, arc),
                    });
                    budget--;
                    into += spacing;
                }
                _carry = budget > 0 ? piece - (into - spacing) : 0f;
            }
        }

        /// The centripetal Catmull-Rom segment from p1 (u = 0) to p2 (u = 1), p0 and p3 its outer
        /// neighbours - Barry and Goldman's pyramidal form, with knot intervals of sqrt(distance).
        public static Vector3 EvaluateCentripetal(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
        {
            float d01 = Mathf.Max(Mathf.Sqrt(Vector3.Distance(p0, p1)), 1e-5f);
            float d12 = Mathf.Max(Mathf.Sqrt(Vector3.Distance(p1, p2)), 1e-5f);
            float d23 = Mathf.Max(Mathf.Sqrt(Vector3.Distance(p2, p3)), 1e-5f);
            float t0 = 0f, t1 = d01, t2 = t1 + d12, t3 = t2 + d23;
            float t = Mathf.Lerp(t1, t2, u);

            Vector3 a1 = ((t1 - t) * p0 + (t - t0) * p1) / (t1 - t0);
            Vector3 a2 = ((t2 - t) * p1 + (t - t1) * p2) / (t2 - t1);
            Vector3 a3 = ((t3 - t) * p2 + (t - t2) * p3) / (t3 - t2);
            Vector3 b1 = ((t2 - t) * a1 + (t - t0) * a2) / (t2 - t0);
            Vector3 b2 = ((t3 - t) * a2 + (t - t1) * a3) / (t3 - t1);
            return ((t2 - t) * b1 + (t - t1) * b2) / (t2 - t1);
        }
    }
}
