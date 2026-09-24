using System;
using UnityEngine;

namespace Sculpting.Molding
{
    /// Where the true centre of the model is along the pull axis.
    ///
    /// The flat split used to sit at the midpoint of the model's BOUNDING BOX. That is the
    /// centre of the box, not of the object, and the two are only the same on a shape that
    /// happens to be balanced: one spike, one fin, one stray vertex left behind by a boolean
    /// moves the box without moving any material, and the split moves with it. On a lure with a
    /// tail the cut lands visibly off-centre, which is exactly the complaint.
    ///
    /// What "exactly in half" actually asks for is the plane with the same amount of MATERIAL on
    /// each side - the median of the volume, not the mean of the extremes. A spike contributes
    /// almost no volume, so it barely moves this; a real lobe of material moves it exactly as
    /// much as it should.
    ///
    /// It costs nothing extra to compute. PullColumnMap already holds, for every column parallel
    /// to the pull direction, the sorted heights at which the surface crosses it - so the solid
    /// occupies the spans between crossing pairs, and the volume below any height is a sum over
    /// those spans. No new sampling, no ray casts, and it gets more precise as the grid gets
    /// finer, because the columns ARE the grid.
    ///
    /// Note on what this is not: a mold's parting plane often wants to sit at the WIDEST
    /// silhouette rather than at the centre of mass, because that is the plane both halves draw
    /// from. That is what the fitted (curved) surface solves. This is the answer to "cut it in
    /// half", asked and answered literally.
    public static class PartingCentre
    {
        /// The height that puts half the model's volume on each side, in frame coordinates.
        ///
        /// Exact rather than bisected. Sorting every span endpoint turns the volume-below-h
        /// curve into a sequence of straight segments - between two consecutive endpoints the
        /// number of columns currently inside the solid cannot change, so the volume grows at a
        /// constant rate - and the half point is then solved directly on whichever segment
        /// contains it. One sort and one sweep, instead of repeatedly guessing a height and
        /// re-measuring.
        ///
        /// `fallback` (the bounding-box midpoint) comes back when the model has no closed
        /// spans to measure at all, which is the one case where there is no volume to halve.
        public static float HalfVolumeHeight(PullColumnMap columns, float fallback, out bool exact)
        {
            exact = false;
            if (columns == null) return fallback;

            int m = CollectEndpoints(columns, out float[] keys, out int[] delta);
            if (m < 2) return fallback;

            Array.Sort(keys, delta, 0, m);

            // Totals in double: this adds one term per span per column, which is hundreds of
            // thousands of terms on a fine grid, and every one of them is a small difference
            // between two heights that may themselves be large if the model sits far from the
            // origin. Accumulating that in float is how a centre drifts off a model that is
            // merely positioned somewhere unusual.
            double total = 0.0;
            int active = 0;
            for (int k = 0; k + 1 < m; k++)
            {
                active += delta[k];
                if (active > 0) total += (double)(keys[k + 1] - keys[k]) * active;
            }
            if (total <= 0.0) return fallback;

            double target = 0.5 * total;
            double acc = 0.0;
            active = 0;
            for (int k = 0; k + 1 < m; k++)
            {
                active += delta[k];
                if (active <= 0) continue;

                double slab = (double)(keys[k + 1] - keys[k]) * active;
                if (acc + slab >= target)
                {
                    exact = true;
                    // Constant rate across this segment, so the crossing point is closed-form.
                    return keys[k] + (float)((target - acc) / active);
                }
                acc += slab;
            }

            exact = true;
            return keys[m - 1];
        }

        /// Solid length below `h`, summed over every column - proportional to the volume below a
        /// flat split there, with the shared column cross-section cancelling out.
        ///
        /// Public because it is how the result gets CHECKED. "The number looks about right" is
        /// not a test of a centre; "this comes back at half the total" is.
        public static double SolidExtentBelow(PullColumnMap columns, float h)
        {
            if (columns == null) return 0.0;

            double sum = 0.0;
            for (int i = 0; i < columns.Nr; i++)
            {
                for (int j = 0; j < columns.Ne; j++)
                {
                    int pairs = columns.CrossingsIn(i, j) / 2;
                    for (int k = 0; k < pairs; k++)
                    {
                        float a = columns.Crossing(i, j, 2 * k);
                        // Crossings are sorted, so once a span starts above h every later span
                        // in this column does too.
                        if (h <= a) break;

                        float b = columns.Crossing(i, j, 2 * k + 1);
                        if (b > a) sum += Mathf.Min(h, b) - a;
                    }
                }
            }
            return sum;
        }

        /// Every solid span's start and end, as sort keys plus an enter/leave marker.
        ///
        /// Crossings are paired in ORDER - first in, first out - which is the same rule
        /// PullColumnMap's own remarks settle on and the only one that survives a sample line
        /// grazing the surface. A column with an odd number of crossings has been grazed or sits
        /// over a hole in the mesh; its last crossing is dropped rather than paired with nothing,
        /// since inventing a span that never closes would swallow the whole model above it.
        private static int CollectEndpoints(PullColumnMap columns, out float[] keys, out int[] delta)
        {
            // Two endpoints per pair, so never more entries than there are crossings.
            int cap = Mathf.Max(columns.CrossingCount, 0);
            keys = new float[cap];
            delta = new int[cap];
            int w = 0;

            for (int i = 0; i < columns.Nr; i++)
            {
                for (int j = 0; j < columns.Ne; j++)
                {
                    int pairs = columns.CrossingsIn(i, j) / 2;
                    for (int k = 0; k < pairs && w + 2 <= cap; k++)
                    {
                        float a = columns.Crossing(i, j, 2 * k);
                        float b = columns.Crossing(i, j, 2 * k + 1);
                        if (b <= a) continue;

                        keys[w] = a; delta[w] = 1; w++;
                        keys[w] = b; delta[w] = -1; w++;
                    }
                }
            }
            return w;
        }
    }
}
