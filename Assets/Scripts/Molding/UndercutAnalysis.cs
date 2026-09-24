using System.Threading.Tasks;
using UnityEngine;

namespace Sculpting.Molding
{
    /// Reads the model against a parting surface and says, per vertex, what is going to happen
    /// to it - which half it lands in, whether it is trapped, and how badly.
    ///
    /// This is the feedback that replaces "generate, look at the result, adjust, regenerate".
    /// Everything it needs is already in the column map, so re-running it after the surface
    /// moves is a pass over the vertex array with a binary search each - fast enough to run
    /// while a plane is being dragged, which is the whole point.
    public static class UndercutAnalysis
    {
        /// What the model is tinted with. Deliberately not red/green: red is reserved for the
        /// one thing that is actually wrong (trapped surface), and two cool/warm neutrals read
        /// as "two sides of a split" without implying either is bad.
        public static readonly Color32 LowerHalfColor = new Color32(70, 150, 210, 255);
        public static readonly Color32 UpperHalfColor = new Color32(225, 165, 85, 255);
        /// Surface neither half can release from.
        public static readonly Color32 TrappedColor = new Color32(235, 55, 55, 255);
        /// The band where the parting surface actually crosses the model - the seam line the
        /// two halves will meet on.
        public static readonly Color32 SeamColor = new Color32(250, 245, 120, 255);

        public struct Report
        {
            public int VertexCount;
            public int Trapped;
            public int Upper;
            public int Lower;
            /// Worst trap depth seen, as a fraction of the model's extent along the pull axis.
            public float WorstSeverity;

            public float TrappedFraction => VertexCount == 0 ? 0f : Trapped / (float)VertexCount;
        }

        /// Fills `colors` (which must be at least `count` long) and returns the tally.
        ///
        /// `seamBand` is the half-width, in world units, of the yellow band drawn where the
        /// surface crosses the model. Passing zero turns it off.
        public static Report Evaluate(Vector3[] worldVertices, int count, PartingField field,
                                      PullColumnMap columns, float seamBand, Color32[] colors)
        {
            var report = new Report { VertexCount = count };
            if (worldVertices == null || colors == null || count <= 0 || field == null || columns == null)
                return report;

            float span = Mathf.Max(columns.ModelMax.y - columns.ModelMin.y, 1e-6f);

            // Tallies are per-partition and summed at the end rather than interlocked per vertex:
            // at a million vertices the contention on four shared counters costs more than the
            // classification does.
            int partitions = Mathf.Clamp(System.Environment.ProcessorCount, 1, 16);
            var trapped = new int[partitions];
            var upper = new int[partitions];
            var lower = new int[partitions];
            var worst = new float[partitions];

            int chunk = (count + partitions - 1) / partitions;
            Parallel.For(0, partitions, p =>
            {
                int from = p * chunk;
                int to = Mathf.Min(count, from + chunk);
                for (int v = from; v < to; v++)
                {
                    Vector3 f = field.Frame.ToFrame(worldVertices[v]);
                    float h = field.Sample(f.x, f.z);
                    float gap = f.y - h;
                    bool above = gap > 0f;

                    columns.Locate(f.x, f.z, out int i, out int j);

                    // Trapped means blocked in the direction this vertex's own half pulls.
                    // Counted by ORDER rather than by height - see PullColumnMap.NearestCrossing
                    // for why a height tolerance cannot work here.
                    int n = columns.CrossingsIn(i, j);
                    int own = columns.NearestCrossing(i, j, f.y);
                    bool blocked = own >= 0 && (above ? own < n - 1 : own > 0);

                    if (above) upper[p]++; else lower[p]++;

                    if (blocked)
                    {
                        trapped[p]++;
                        // How deep the trap is: the distance to the far side of whatever is in
                        // the way. A vertex under a thin lip scores low and is usually fine to
                        // leave; one buried under the whole body scores high and means the pull
                        // direction is wrong.
                        float far = above ? columns.Crossing(i, j, n - 1) : columns.Crossing(i, j, 0);
                        float severity = Mathf.Clamp01(Mathf.Abs(far - f.y) / span);
                        if (severity > worst[p]) worst[p] = severity;

                        // Blended rather than flat red so the readout distinguishes "a hair
                        // under an edge" from "sealed inside the model" at a glance.
                        Color32 baseColor = above ? UpperHalfColor : LowerHalfColor;
                        colors[v] = Color32.Lerp(baseColor, TrappedColor, 0.35f + 0.65f * severity);
                    }
                    else if (seamBand > 0f && Mathf.Abs(gap) <= seamBand)
                    {
                        colors[v] = SeamColor;
                    }
                    else
                    {
                        colors[v] = above ? UpperHalfColor : LowerHalfColor;
                    }
                }
            });

            for (int p = 0; p < partitions; p++)
            {
                report.Trapped += trapped[p];
                report.Upper += upper[p];
                report.Lower += lower[p];
                report.WorstSeverity = Mathf.Max(report.WorstSeverity, worst[p]);
            }
            return report;
        }

        /// Whether the surface actually cuts THROUGH the model. A parting sheet that passes
        /// entirely above or below it produces one empty half and one half with the model
        /// rattling around inside it - worth catching before spending a boolean on it, which is
        /// what the addon's "the parting surface does not cut through the model" check does.
        public static bool CutsThroughModel(in Report report) => report.Upper > 0 && report.Lower > 0;
    }
}
