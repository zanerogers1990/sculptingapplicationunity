using UnityEngine;

namespace Sculpting.Molding
{
    /// The rectangular box of mold material, in frame coordinates: the model's own extent grown
    /// by Padding across the parting plane and by Wall along the pull axis.
    ///
    /// Every stage measures against this - the parting field spans its footprint, the two halves
    /// fill it from the parting surface out to its flat faces, channels run from inside the model
    /// to its walls, and the viewport draws it as the wire outline you drag to resize. Kept as a
    /// value so a settings change just recomputes one, rather than mutating shared state that
    /// half the pipeline is holding a reference to.
    public readonly struct MoldBlock
    {
        public readonly float R0, R1, E0, E1, UBottom, UTop;
        /// The model's own frame-space bounds, before padding - what the channel placement aims at.
        public readonly Vector3 ModelMin, ModelMax;

        public MoldBlock(Vector3 modelMin, Vector3 modelMax, float padding, float wall)
        {
            ModelMin = modelMin;
            ModelMax = modelMax;
            R0 = modelMin.x - padding;
            R1 = modelMax.x + padding;
            E0 = modelMin.z - padding;
            E1 = modelMax.z + padding;
            UBottom = modelMin.y - wall;
            UTop = modelMax.y + wall;
        }

        public Vector3 ModelExtent => ModelMax - ModelMin;
        public float Width => R1 - R0;
        public float Depth => E1 - E0;
        public float Height => UTop - UBottom;
        public Vector3 Centre => new Vector3(0.5f * (R0 + R1), 0.5f * (UBottom + UTop), 0.5f * (E0 + E1));

        /// The eight corners of the box in WORLD space, in the order the wire overlay expects:
        /// [0..3] the bottom face going round, [4..7] the top face directly above each.
        public void Corners(MoldFrame frame, Vector3[] into)
        {
            into[0] = frame.ToWorld(new Vector3(R0, UBottom, E0));
            into[1] = frame.ToWorld(new Vector3(R1, UBottom, E0));
            into[2] = frame.ToWorld(new Vector3(R1, UBottom, E1));
            into[3] = frame.ToWorld(new Vector3(R0, UBottom, E1));
            into[4] = frame.ToWorld(new Vector3(R0, UTop, E0));
            into[5] = frame.ToWorld(new Vector3(R1, UTop, E0));
            into[6] = frame.ToWorld(new Vector3(R1, UTop, E1));
            into[7] = frame.ToWorld(new Vector3(R0, UTop, E1));
        }

        /// Frame-space bounds of the model, computed straight from world vertices.
        ///
        /// Split across cores: at 730k vertices the single loop was 58ms of every re-fit. A min
        /// and a max come out the same whatever order they are taken in, so the answer is
        /// identical to the sequential one.
        public static void MeasureModel(Vector3[] verts, int count, MoldFrame frame, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            if (verts == null || count <= 0) return;
            count = Mathf.Min(count, verts.Length);

            int parts = count >= 65536 ? Mathf.Clamp(System.Environment.ProcessorCount, 1, 16) : 1;
            int chunk = (count + parts - 1) / parts;
            var mins = new Vector3[parts];
            var maxs = new Vector3[parts];
            var used = new bool[parts];

            System.Threading.Tasks.Parallel.For(0, parts, p =>
            {
                int a = p * chunk, b = Mathf.Min(count, a + chunk);
                if (a >= b) return;
                Vector3 r = frame.Right, u = frame.Up, e = frame.Eye;
                Vector3 v0 = verts[a];
                Vector3 lo = new Vector3(Vector3.Dot(v0, r), Vector3.Dot(v0, u), Vector3.Dot(v0, e));
                Vector3 hi = lo;
                for (int i = a + 1; i < b; i++)
                {
                    Vector3 v = verts[i];
                    float x = Vector3.Dot(v, r), y = Vector3.Dot(v, u), z = Vector3.Dot(v, e);
                    // Written out as Vector3.Min/Max's own comparisons, so even the sign of a
                    // zero comes out as the old loop's did.
                    lo.x = lo.x < x ? lo.x : x; hi.x = hi.x > x ? hi.x : x;
                    lo.y = lo.y < y ? lo.y : y; hi.y = hi.y > y ? hi.y : y;
                    lo.z = lo.z < z ? lo.z : z; hi.z = hi.z > z ? hi.z : z;
                }
                mins[p] = lo;
                maxs[p] = hi;
                used[p] = true;
            });

            bool any = false;
            for (int p = 0; p < parts; p++)
            {
                if (!used[p]) continue;
                if (!any) { min = mins[p]; max = maxs[p]; any = true; continue; }
                min = Vector3.Min(min, mins[p]);
                max = Vector3.Max(max, maxs[p]);
            }
        }
    }
}
