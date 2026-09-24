using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting.Molding
{
    /// Everything one fit produced: the surface, the accelerator it was fitted with, and how
    /// well it did. The column map is kept because the live overlay re-queries it every time the
    /// surface moves, and rebuilding it per drag frame is the one cost worth avoiding.
    public sealed class MoldFit
    {
        public MoldFrame Frame;
        public MoldBlock Block;
        public PartingField Field;
        public PullColumnMap Columns;

        /// Share of the model's sampled surface that neither half can release from. Zero for
        /// anything convex along the pull axis; the number the axis search minimises.
        public float BlockedFraction;
        /// Grid nodes that had real samples under them (the rest were filled in by diffusion).
        public int KnownCells;
        public bool Clipped;
        public string Error;

        public bool IsValid => Field != null && Error == null;
    }

    /// Fits the parting surface: for every column of the model, find the height that traps the
    /// least surface, then fill, smooth and clamp that into a printable sheet.
    ///
    /// This is the addon's fit_surface / best_level / best_frame, with the sampling swapped for
    /// PullColumnMap (see its remarks - same information, no ray casts, no RNG). The cost
    /// function is unchanged and is the heart of the thing: a surface point ends up in the upper
    /// half if it is above the split, and it is TRAPPED if something blocks the direction its
    /// half pulls. Minimising the count of trapped points per column, independently, and then
    /// smoothing the result, is what produces a curved parting line that follows a lure's
    /// silhouette instead of slicing through it.
    public static class PartingSurfaceFitter
    {
        /// Column grid offset half a cell outward so that column i is centred exactly on field
        /// node i. Without it the two grids are staggered and every pooled sample is biased half
        /// a cell along both axes - invisible at high resolution, a visible lean at low.
        public static PullColumnMap BuildColumns(Vector3[] verts, int[] tris, MoldFrame frame, PartingField field)
        {
            float halfR = 0.5f * field.Dr, halfE = 0.5f * field.De;
            return PullColumnMap.Build(verts, tris, frame,
                                       field.R0 - halfR, field.R1 + halfR,
                                       field.E0 - halfE, field.E1 + halfE,
                                       field.Nr, field.Ne);
        }

        /// Fits a surface for one given pull frame.
        public static MoldFit Fit(Vector3[] verts, int[] tris, int vertexCount, MoldFrame frame, MoldSettings settings)
        {
            var fit = new MoldFit { Frame = frame };

            if (verts == null || tris == null || vertexCount <= 0 || tris.Length < 3)
            {
                fit.Error = "the model has no geometry";
                return fit;
            }

            MoldBlock.MeasureModel(verts, vertexCount, frame, out Vector3 lo, out Vector3 hi);
            var block = new MoldBlock(lo, hi, settings.Padding, settings.Wall);
            fit.Block = block;

            PartingGrid.NodesForBlock(block, settings, out int nr, out int ne);
            var field = new PartingField(frame, block.R0, block.R1, block.E0, block.E1, nr, ne);
            PullColumnMap columns = BuildColumns(verts, tris, frame, field);
            fit.Columns = columns;

            if (columns.CrossingCount == 0)
            {
                fit.Error = "the model does not cover any of the parting grid - lower the grid resolution";
                return fit;
            }

            var known = new bool[field.H.Length];
            var pooledU = new List<float>(256);
            var pooledUp = new List<bool>(256);
            var pooledDown = new List<bool>(256);
            int radius = Mathf.Max(0, settings.BlendRadius);
            int minSamples = Mathf.Max(1, settings.MinSamplesPerCell);
            int knownCount = 0;
            var scratch = new Scratch();

            // Sequential rather than parallel: the pooled buffers are reused across cells (a
            // 64x40 grid would otherwise churn 2,560 temporary lists) and a fit is already only
            // a few milliseconds. The expensive half of the work is the column build above, and
            // that IS parallel.
            for (int i = 0; i < field.Nr; i++)
            {
                for (int j = 0; j < field.Ne; j++)
                {
                    pooledU.Clear(); pooledUp.Clear(); pooledDown.Clear();
                    columns.GatherPool(i, j, radius, pooledU, pooledUp, pooledDown);
                    if (pooledU.Count < minSamples) continue;

                    field.H[i * field.Ne + j] = BestLevel(pooledU, pooledUp, pooledDown, scratch, out _);
                    known[i * field.Ne + j] = true;
                    knownCount++;
                }
            }

            if (knownCount == 0)
            {
                fit.Error = "not enough surface under the parting grid - lower the grid resolution or Min samples/cell";
                return fit;
            }

            field.FillHoles(known);
            field.Smooth(settings.Rounding);

            // Leave a real floor and roof: a surface allowed to touch the block's outer faces
            // would produce a half with nothing behind the cavity.
            float margin = 0.3f * settings.Wall;
            fit.Clipped = field.ClampHeights(block.UBottom + margin, block.UTop - margin);

            fit.Field = field;
            fit.KnownCells = knownCount;
            fit.BlockedFraction = BlockedFraction(field, columns);
            return fit;
        }

        /// A plain flat split at the model's mid-height, plus SplitOffset. Same MoldFit shape as
        /// a real fit - block, field and column map - so nothing downstream has to know which of
        /// the two it is looking at, and the live overlays work identically in both.
        public static MoldFit Flat(Vector3[] verts, int[] tris, int vertexCount, MoldFrame frame, MoldSettings settings)
        {
            var fit = new MoldFit { Frame = frame };

            if (verts == null || tris == null || vertexCount <= 0 || tris.Length < 3)
            {
                fit.Error = "the model has no geometry";
                return fit;
            }

            MoldBlock.MeasureModel(verts, vertexCount, frame, out Vector3 lo, out Vector3 hi);
            var block = new MoldBlock(lo, hi, settings.Padding, settings.Wall);
            fit.Block = block;

            PartingGrid.NodesForBlock(block, settings, out int nr, out int ne);
            var field = new PartingField(frame, block.R0, block.R1, block.E0, block.E1, nr, ne);

            // The column map is built BEFORE the field is filled, because the split height is
            // now measured off it. Same map the curved fit uses and the overlays already query,
            // so the true centre costs one sort on top of work that was happening anyway.
            PullColumnMap columns = BuildColumns(verts, tris, frame, field);
            fit.Columns = columns;

            float boundsMid = 0.5f * (lo.y + hi.y);
            float centre = settings.CentreOnVolume
                ? PartingCentre.HalfVolumeHeight(columns, boundsMid, out _)
                : boundsMid;
            field.Fill(centre + settings.SplitOffset);

            float margin = 0.3f * settings.Wall;
            fit.Clipped = field.ClampHeights(block.UBottom + margin, block.UTop - margin);

            fit.Field = field;
            fit.KnownCells = field.H.Length;
            fit.BlockedFraction = BlockedFraction(field, fit.Columns);
            return fit;
        }

        /// Tries pulling along each world axis and keeps whichever traps the least - the addon's
        /// best_frame. The tiny bias term breaks ties toward pulling along the model's THIN
        /// direction, which is almost always the one that gives a shallower, easier-to-fill
        /// cavity when two axes score the same.
        public static MoldFit FitBestAxis(Vector3[] verts, int[] tris, int vertexCount, MoldSettings settings,
                                          out int chosenAxis)
        {
            chosenAxis = settings.Axis;

            Bounds worldBounds = MeasureWorld(verts, vertexCount);
            Vector3 ext = worldBounds.size;
            float maxExt = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z), 1e-6f);

            MoldFit best = null;
            float bestKey = float.MaxValue;
            MoldFit lastFailure = null;

            for (int axis = 0; axis < 3; axis++)
            {
                MoldFrame frame = MoldFrame.FromWorldAxis(axis, ext);
                MoldFit fit = Fit(verts, tris, vertexCount, frame, settings);
                if (!fit.IsValid) { lastFailure = fit; continue; }

                float key = fit.BlockedFraction + 0.002f * (ext[axis] / maxExt);
                if (key < bestKey)
                {
                    bestKey = key;
                    best = fit;
                    chosenAxis = axis;
                }
            }

            return best ?? lastFailure ?? new MoldFit { Error = "could not fit a parting surface on any axis" };
        }

        /// Share of the model's surface samples that their own half cannot release from, for a
        /// given surface. The addon's blocked_fraction.
        public static float BlockedFraction(PartingField field, PullColumnMap columns)
        {
            if (columns.CrossingCount == 0) return 0f;

            float eps = 1e-4f * Mathf.Max(columns.ModelMax.y - columns.ModelMin.y, 1e-6f);
            int blocked = 0, total = 0;

            for (int i = 0; i < columns.Nr; i++)
            {
                // The column's real sample line, not its geometric centre - see
                // PullColumnMap.ColumnCentreR.
                float r = columns.ColumnCentreR(i);
                for (int j = 0; j < columns.Ne; j++)
                {
                    int n = columns.CrossingsIn(i, j);
                    if (n == 0) continue;
                    float e = columns.ColumnCentreE(j);
                    float h = field.Sample(r, e);

                    for (int k = 0; k < n; k++)
                    {
                        float u = columns.Crossing(i, j, k);
                        total++;
                        // Above the split it belongs to the upper half and has to lift along
                        // +Up; below, it drops along -Up. Blocked either way and the mold will
                        // not open without tearing.
                        bool trapped = u > h ? k < n - 1 : k > 0;
                        if (trapped) blocked++;
                    }
                }
            }

            _ = eps;
            return total == 0 ? 0f : blocked / (float)total;
        }

        /// The split height in one column that leaves the least surface trapped - the addon's
        /// best_level, ported exactly.
        ///
        /// Sort the pooled samples by height. For a split placed just above sample k, the trapped
        /// count is (blocked-looking-up samples ABOVE k) + (blocked-looking-down samples at or
        /// BELOW k); both are running totals, so one sweep finds the minimum. Two details matter:
        /// splitting below everything is the fallback when nothing beats it (that is what a
        /// column with a hole straight through it wants), and the answer is the MEDIAN of all
        /// heights that tie for the minimum, not the first - the cost is flat across the whole
        /// interior of a simple column, and taking the median centres the split in the material
        /// instead of pinning it to the surface.
        public static float BestLevel(List<float> us, List<bool> up, List<bool> down, out int cost)
            => BestLevel(us, up, down, null, out cost);

        /// `scratch` is optional; passing one lets a whole-grid fit reuse four arrays instead of
        /// allocating four per cell (2,560 cells on the default grid, every recompute, which at
        /// 20Hz is what would turn a live drag into a GC sawtooth).
        public static float BestLevel(List<float> us, List<bool> up, List<bool> down, Scratch scratch, out int cost)
        {
            int n = us.Count;
            cost = 0;
            if (n == 0) return 0f;

            if (scratch == null) scratch = new Scratch();
            scratch.Ensure(n);
            int[] order = scratch.Order;
            float[] keys = scratch.Keys;
            int[] costs = scratch.Costs;

            for (int i = 0; i < n; i++) { order[i] = i; keys[i] = us[i]; }
            Array.Sort(keys, order, 0, n);

            int upTotal = 0;
            for (int i = 0; i < n; i++) if (up[i]) upTotal++;

            int bestCost = int.MaxValue;
            int cumUp = 0, cumDown = 0;
            for (int k = 0; k < n; k++)
            {
                int src = order[k];
                if (up[src]) cumUp++;
                if (down[src]) cumDown++;
                int c = (upTotal - cumUp) + cumDown;
                costs[k] = c;
                if (c < bestCost) bestCost = c;
            }

            if (bestCost >= upTotal)
            {
                cost = upTotal;
                return keys[0];
            }

            // Median of the tied heights. keys is sorted, so walking it in order collects the
            // ties in height order and the middle one is the median directly - counted first so
            // no list has to be built for it.
            int ties = 0;
            for (int k = 0; k < n; k++) if (costs[k] <= bestCost) ties++;
            int want = ties / 2;
            for (int k = 0; k < n; k++)
            {
                if (costs[k] > bestCost) continue;
                if (want-- == 0) { cost = bestCost; return keys[k]; }
            }

            cost = bestCost;
            return keys[0];
        }

        /// Reusable working arrays for BestLevel. Grown, never shrunk; one instance per fit.
        public sealed class Scratch
        {
            public int[] Order = Array.Empty<int>();
            public float[] Keys = Array.Empty<float>();
            public int[] Costs = Array.Empty<int>();

            public void Ensure(int n)
            {
                if (Order.Length >= n) return;
                int size = Mathf.NextPowerOfTwo(Mathf.Max(n, 16));
                Order = new int[size];
                Keys = new float[size];
                Costs = new int[size];
            }
        }

        private static Bounds MeasureWorld(Vector3[] verts, int count)
        {
            var b = new Bounds();
            if (verts == null || count <= 0) return b;
            Vector3 min = verts[0], max = verts[0];
            for (int i = 1; i < count; i++) { min = Vector3.Min(min, verts[i]); max = Vector3.Max(max, verts[i]); }
            b.SetMinMax(min, max);
            return b;
        }
    }
}
