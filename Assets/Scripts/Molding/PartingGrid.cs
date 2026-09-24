using UnityEngine;

namespace Sculpting.Molding
{
    /// Turns one density setting into the two node counts PartingField is built from.
    ///
    /// The grid used to be two raw node COUNTS - 64 across, 40 deep - spread over whatever
    /// footprint the block happened to have. That is the same mistake MoldSettings.FinalResolution
    /// was fixed for: a count cannot mean anything on its own, because the same 64 is a fine grid
    /// on a thumbnail and a coarse one on a lure. Worse, two independent counts over two different
    /// spans give RECTANGULAR cells, so the parting line resolved detail several times better
    /// along one axis than the other and the error depended on the model's aspect ratio.
    ///
    /// Density is a cell SIZE instead, in millimetres, like every other physical size in
    /// MoldSettings. One number, the same meaning on every model, and the cells come out square.
    public static class PartingGrid
    {
        /// Nodes the field may hold. The field itself is one float per node, but the column map
        /// built alongside it is two ints per node plus the crossings, so a node costs roughly
        /// 12 bytes before any surface lands in it - about 12MB at this ceiling. The real limit
        /// is the fit's sequential per-cell loop, not the memory.
        public const int MaxNodes = 1_000_000;

        /// Below this a cell stops resolving anything and only adds cost.
        public const int MinNodesPerAxis = 9;

        /// Node counts for a block at the settings' density.
        ///
        /// Both counts are forced ODD, which is what puts a node exactly on the model's centre
        /// line: the block is the model's bounds grown by the SAME padding on both sides, so its
        /// footprint centre is the model's footprint centre, and node (N - 1) / 2 of an odd grid
        /// lands on it exactly. With an even count the centre falls between two nodes, and a
        /// mirror-symmetric model then gets a parting surface that is not mirror-symmetric -
        /// every pooled sample on one side is offset half a cell differently from its twin, and
        /// Smooth spreads that asymmetry over the whole sheet.
        public static void NodesForBlock(in MoldBlock block, MoldSettings settings,
                                         out int nr, out int ne)
        {
            if (settings == null || !settings.AutoGridDensity)
            {
                nr = MakeOdd(settings == null ? 65 : settings.GridAcross);
                ne = MakeOdd(settings == null ? 65 : settings.GridDepth);
                ApplyBudget(ref nr, ref ne);
                return;
            }

            float cell = Mathf.Max(settings.ToUnits(settings.GridCellMm), 1e-7f);
            nr = MakeOdd(Mathf.RoundToInt(Mathf.Max(block.Width, 0f) / cell) + 1);
            ne = MakeOdd(Mathf.RoundToInt(Mathf.Max(block.Depth, 0f) / cell) + 1);
            ApplyBudget(ref nr, ref ne);
        }

        /// The cell size the counts actually deliver, in millimetres - what the panel reports.
        /// Not the requested size: rounding to a whole number of odd cells, and the node budget,
        /// both move it. Reporting the request instead of the result is how a slider ends up
        /// quietly lying about a grid that hit its ceiling.
        public static float EffectiveCellMm(in MoldBlock block, MoldSettings settings, int nr, int ne)
        {
            if (settings == null) return 0f;
            float dr = nr > 1 ? block.Width / (nr - 1) : block.Width;
            float de = ne > 1 ? block.Depth / (ne - 1) : block.Depth;
            return settings.ToMillimetres(Mathf.Max(dr, de));
        }

        /// Largest odd value at or above n, never below MinNodesPerAxis.
        private static int MakeOdd(int n)
        {
            n = Mathf.Max(n, MinNodesPerAxis);
            return (n & 1) == 0 ? n + 1 : n;
        }

        /// Scales both axes down together until the grid fits the budget, so a clamped grid
        /// keeps its square cells rather than being squashed on one axis.
        private static void ApplyBudget(ref int nr, ref int ne)
        {
            long total = (long)nr * ne;
            if (total <= MaxNodes) return;

            float scale = Mathf.Sqrt(MaxNodes / (float)total);
            nr = MakeOdd(Mathf.FloorToInt(nr * scale));
            ne = MakeOdd(Mathf.FloorToInt(ne * scale));

            // Rounding up to odd can push it back over; step down one cell at a time on the
            // longer axis until it fits. At most a couple of iterations.
            while ((long)nr * ne > MaxNodes && (nr > MinNodesPerAxis || ne > MinNodesPerAxis))
            {
                if (nr >= ne && nr > MinNodesPerAxis) nr -= 2;
                else if (ne > MinNodesPerAxis) ne -= 2;
                else break;
            }
        }
    }
}
