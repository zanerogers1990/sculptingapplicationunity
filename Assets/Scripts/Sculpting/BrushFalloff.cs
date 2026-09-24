using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;

namespace Sculpting
{
    /// A user-drawn brush falloff - ZBrush's brush Curve: how strongly the brush acts at each
    /// distance from its centre. Control points are (distance, weight), distance 0 at the brush
    /// centre and 1 at its edge, both in [0, 1]. The first point always sits at distance 0 and the
    /// last at distance 1; the ones between can go anywhere.
    ///
    /// Interpolated with a monotone cubic (Fritsch-Carlson), so the curve passes through every
    /// point and never overshoots between two of them - a plain spline rings below 0 or above 1
    /// next to a sharp step, and a negative weight would sculpt backwards.
    [Serializable]
    public sealed class BrushFalloffCurve
    {
        public List<Vector2> Points = new List<Vector2>();

        /// Bumped on every edit, so whoever caches the baked table knows to rebake.
        [NonSerialized] public int Version;

        public BrushFalloffCurve Clone()
        {
            var copy = new BrushFalloffCurve();
            copy.Points.AddRange(Points);
            return copy;
        }

        public static BrushFalloffCurve Preset(FalloffPreset preset)
        {
            var c = new BrushFalloffCurve();
            switch (preset)
            {
                case FalloffPreset.Sharp:
                    c.Points.AddRange(new[] { new Vector2(0f, 1f), new Vector2(0.2f, 0.45f), new Vector2(0.55f, 0.1f), new Vector2(1f, 0f) });
                    break;
                case FalloffPreset.Plateau:
                    c.Points.AddRange(new[] { new Vector2(0f, 1f), new Vector2(0.6f, 1f), new Vector2(0.85f, 0.35f), new Vector2(1f, 0f) });
                    break;
                case FalloffPreset.Linear:
                    c.Points.AddRange(new[] { new Vector2(0f, 1f), new Vector2(1f, 0f) });
                    break;
                default: // Smooth - a smoothstep, what most brushes use by default
                    c.Points.AddRange(new[] { new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(1f, 0f) });
                    break;
            }
            return c;
        }

        /// Keeps the points valid after an edit: sorted by distance, the ends pinned to 0 and 1,
        /// every weight in [0, 1].
        public void Normalize()
        {
            for (int i = 0; i < Points.Count; i++)
                Points[i] = new Vector2(Mathf.Clamp01(Points[i].x), Mathf.Clamp01(Points[i].y));
            Points.Sort((a, b) => a.x.CompareTo(b.x));
            if (Points.Count == 0) Points.Add(new Vector2(0f, 1f));
            if (Points.Count == 1) Points.Add(new Vector2(1f, 0f));
            Points[0] = new Vector2(0f, Points[0].y);
            Points[Points.Count - 1] = new Vector2(1f, Points[Points.Count - 1].y);
            Version++;
        }

        public float Evaluate(float distance)
        {
            int n = Points.Count;
            if (n == 0) return 1f - distance;
            if (distance <= Points[0].x) return Points[0].y;
            if (distance >= Points[n - 1].x) return Points[n - 1].y;

            int k = 0;
            while (k < n - 2 && distance > Points[k + 1].x) k++;
            Vector2 a = Points[k], b = Points[k + 1];
            float h = b.x - a.x;
            if (h <= 1e-6f) return b.y;
            float t = (distance - a.x) / h;
            float m0 = Tangent(k), m1 = Tangent(k + 1);
            float t2 = t * t, t3 = t2 * t;
            float y = (2f * t3 - 3f * t2 + 1f) * a.y + (t3 - 2f * t2 + t) * h * m0
                    + (-2f * t3 + 3f * t2) * b.y + (t3 - t2) * h * m1;
            return Mathf.Clamp01(y);
        }

        // Fritsch-Carlson tangents: zero at a local extremum or on a flat run, otherwise the
        // harmonic mean of the two neighbouring slopes - which is what keeps each span monotone.
        private float Tangent(int i)
        {
            int n = Points.Count;
            float Slope(int j) => (Points[j + 1].y - Points[j].y) / Mathf.Max(Points[j + 1].x - Points[j].x, 1e-6f);
            if (i == 0) return n > 1 ? Slope(0) : 0f;
            if (i == n - 1) return Slope(n - 2);
            float d0 = Slope(i - 1), d1 = Slope(i);
            if (d0 * d1 <= 0f) return 0f;
            return 2f / (1f / d0 + 1f / d1);
        }
    }

    public enum FalloffPreset { Smooth, Sharp, Plateau, Linear }

    /// The ACTIVE brush's custom falloff, baked to a table every brush reads from - Burst jobs and
    /// managed paths alike. A Burst SharedStatic rather than a field on each job: every brush has
    /// its own job struct and its own managed twin, and the falloff is read from a dozen places
    /// inside them, several of them static helpers shared by two jobs. Threading a table through
    /// all of those would touch every signature for a value that is the same for the whole frame.
    ///
    /// Written only from the main thread between strokes (SculptController syncs it before any
    /// brush runs), while no job is in flight - every brush job here completes before its apply
    /// returns.
    public static class BrushFalloff
    {
        public const int TableSize = 64; // spans; TableSize + 1 samples

        private struct Table
        {
            public FixedList512Bytes<float> Samples;
            public int Enabled;
        }

        private abstract class Context { }
        private sealed class TableKey { }

        private static readonly SharedStatic<Table> Shared = SharedStatic<Table>.GetOrCreate<Context, TableKey>();

        public static bool Enabled => Shared.Data.Enabled != 0;

        /// Installs `curve` as the active falloff, or restores every brush's built-in falloff for null.
        public static void SetActive(BrushFalloffCurve curve)
        {
            ref Table table = ref Shared.Data;
            if (curve == null)
            {
                table.Enabled = 0;
                return;
            }
            table.Samples.Clear();
            for (int i = 0; i <= TableSize; i++) table.Samples.Add(curve.Evaluate(i / (float)TableSize));
            table.Enabled = 1;
        }

        /// The weight a brush should use at `t01` (1 at its centre, 0 at its edge - the form every
        /// brush already computes): the custom curve if one is active, else the brush's own.
        public static float Apply(float t01, float builtIn)
        {
            ref Table table = ref Shared.Data;
            if (table.Enabled == 0) return builtIn;
            float x = Mathf.Clamp01(1f - t01) * TableSize;
            int i = (int)x;
            if (i >= TableSize) return table.Samples[TableSize];
            float f = x - i;
            return table.Samples[i] + (table.Samples[i + 1] - table.Samples[i]) * f;
        }
    }
}
