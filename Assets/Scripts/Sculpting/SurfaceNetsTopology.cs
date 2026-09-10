using System;
using System.Collections.Generic;

namespace Sculpting
{
    /// Per-cell surface topology for the dual (Surface Nets) extractors - MeshRemesher's dense path
    /// and SparseRemesher - so that their output is 2-manifold.
    ///
    /// Plain Surface Nets places ONE vertex per active cell and stitches a quad around every lattice
    /// edge the field changes sign across. That is only right when the surface crosses the cell as
    /// a single patch. Two cases break it:
    ///
    ///   - An AMBIGUOUS FACE: the four corners of a cell face alternate inside/outside. All four
    ///     face edges then emit quads, and all four quads share the edge between the two cells on
    ///     either side of that face - an edge used by four triangles. This is what tight concave
    ///     creases produce (two limbs nearly touching across a thin wedge of outside).
    ///   - Several separate patches inside one cell (two diagonally opposite inside corners), which
    ///     collapses separate sheets onto one shared vertex.
    ///
    /// The fix is one vertex per surface PATCH rather than per cell. A patch is found from the cube's
    /// corner signs alone: corners of the same sign joined by a cube edge are one region, an
    /// ambiguous face additionally joins one of its two diagonals, and on the sphere that is the
    /// cube's surface each boundary loop separates exactly one inside region from one outside region
    /// - so a crossing edge's patch is simply the (inside region, outside region) pair it runs
    /// between.
    ///
    /// Which diagonal an ambiguous face joins must be decided identically by both cells that share
    /// it, and must not re-create the problem: if the diagonal left SEPARATED on the face is still
    /// connected through the rest of BOTH cells, the two patches it bounds are one patch on each
    /// side and the shared edge is used four times again (a sub-voxel tube collapsed to an edge).
    /// By planarity at most one diagonal can be connected around a single cell, so joining the
    /// diagonal that is connected through both cells always avoids it. JoinsInside applies that rule
    /// from the two cells' own corners, falling back to the asymptotic decider (join the diagonal
    /// whose corner values have the larger product - the side the bilinear saddle lies on); that is
    /// exact for a face whose cells have no other ambiguous face, which is almost every crease in
    /// real geometry. AmbiguityResolver handles the rest, where decisions interact.
    ///
    /// Nothing here reads positions or indices - only signs and values - so a field that is symmetric
    /// under a reflection extracts to a symmetric surface.
    ///
    /// Conventions shared with both extractors: corner i is (i & 1, (i >> 1) & 1, (i >> 2) & 1); an
    /// inside mask has bit i set when corner i is inside; edges are numbered 0-3 along X, 4-7 along
    /// Y, 8-11 along Z (see EdgeA/EdgeB).
    internal static class SurfaceNetsTopology
    {
        public static readonly int[] EdgeA = { 0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3 };
        public static readonly int[] EdgeB = { 1, 3, 5, 7, 2, 3, 6, 7, 4, 5, 6, 7 };

        /// Faces 0-5 are x-, x+, y-, y+, z-, z+. Corners are listed in cyclic order, and aligned
        /// across each opposite pair: corner k of face 2a and corner k of face 2a+1 differ only along
        /// axis a, so the neighbour across a face sees the same samples at the same positions.
        private static readonly int[][] FaceCorners =
        {
            new[] { 0, 2, 6, 4 }, new[] { 1, 3, 7, 5 },
            new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 },
            new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 },
        };

        private static readonly byte[] Region = new byte[256 * 8];
        private static readonly byte[] Ambiguous = new byte[256];
        private static readonly bool[] Simple = new bool[256];

        static SurfaceNetsTopology()
        {
            Span<int> parent = stackalloc int[8];
            for (int mask = 0; mask < 256; mask++)
            {
                for (int c = 0; c < 8; c++) parent[c] = c;
                for (int e = 0; e < 12; e++)
                    if ((mask >> EdgeA[e] & 1) == (mask >> EdgeB[e] & 1)) Union(parent, EdgeA[e], EdgeB[e]);
                for (int c = 0; c < 8; c++) Region[mask * 8 + c] = (byte)Find(parent, c);

                int ambiguous = 0;
                for (int f = 0; f < 6; f++)
                {
                    int[] fc = FaceCorners[f];
                    int s0 = mask >> fc[0] & 1, s1 = mask >> fc[1] & 1;
                    int s2 = mask >> fc[2] & 1, s3 = mask >> fc[3] & 1;
                    if (s0 == s2 && s1 == s3 && s0 != s1) ambiguous |= 1 << f;
                }
                Ambiguous[mask] = (byte)ambiguous;
            }

            for (int mask = 1; mask < 255; mask++)
                Simple[mask] = Ambiguous[mask] == 0 && Components(mask, 0, out _) == 1;
        }

        /// True when the cell holds exactly one patch and has no ambiguous face - the overwhelmingly
        /// common case, which the extractors keep handling exactly as before.
        public static bool IsSimple(int insideMask) => Simple[insideMask];

        /// Bit f is set when face f of the cell is ambiguous.
        public static int AmbiguousFaces(int insideMask) => Ambiguous[insideMask];

        /// The axis (0 = x, 1 = y, 2 = z) face f is perpendicular to, and which way its neighbour lies.
        public static int FaceAxis(int face) => face >> 1;
        public static int FaceStep(int face) => (face & 1) == 0 ? -1 : 1;

        /// Corner k (0-3, cyclic) of face f, as a corner index of the cell.
        public static int FaceCorner(int face, int k) => FaceCorners[face][k];

        /// Whether ambiguous face `face` of a cell joins its INSIDE diagonal, judged from the two cells
        /// sharing it alone. `neighbourMask` is the inside mask of the cell across the face, or -1 when
        /// there is none; v0..v3 are the face's corner values in FaceCorner order. The neighbour
        /// evaluating the same face (as face ^ 1, with the masks swapped) gets the same answer.
        public static bool JoinsInside(int face, int cellMask, int neighbourMask, float v0, float v1, float v2, float v3)
        {
            int[] fa = FaceCorners[face];
            int inPos = (cellMask >> fa[0] & 1) != 0 ? 0 : 1;
            int outPos = 1 - inPos;

            if (neighbourMask >= 0)
            {
                int[] fb = FaceCorners[face ^ 1];
                bool insideLinked = Connected(cellMask, fa[inPos], fa[inPos + 2]) &&
                                    Connected(neighbourMask, fb[inPos], fb[inPos + 2]);
                bool outsideLinked = Connected(cellMask, fa[outPos], fa[outPos + 2]) &&
                                     Connected(neighbourMask, fb[outPos], fb[outPos + 2]);
                if (insideLinked != outsideLinked) return insideLinked;
            }

            float insideProduct = inPos == 0 ? v0 * v2 : v1 * v3;
            float outsideProduct = inPos == 0 ? v1 * v3 : v0 * v2;
            return insideProduct > outsideProduct;
        }

        /// How decisively the asymptotic decider picks a side of this face: 0 is a coin toss, 1 is
        /// certain. Used to decide which of two interacting faces should give way.
        public static float DeciderConfidence(int insideMask, int face, float v0, float v1, float v2, float v3)
        {
            int inPos = (insideMask >> FaceCorners[face][0] & 1) != 0 ? 0 : 1;
            float insideProduct = inPos == 0 ? v0 * v2 : v1 * v3;
            float outsideProduct = inPos == 0 ? v1 * v3 : v0 * v2;
            return Math.Abs(insideProduct - outsideProduct) / (insideProduct + outsideProduct + 1e-30f);
        }

        /// Whether one diagonal of `face` is connected through the REST of the cell: every same-sign
        /// edge and every other ambiguous face's join, but not `face`'s own.
        public static bool DiagonalLinked(int insideMask, int joinInsideBits, int face, bool insideDiagonal)
        {
            Span<int> parent = stackalloc int[8];
            JoinRegions(parent, insideMask, Ambiguous[insideMask] & ~(1 << face), joinInsideBits);

            int[] fc = FaceCorners[face];
            int inPos = (insideMask >> fc[0] & 1) != 0 ? 0 : 1;
            int pos = insideDiagonal ? inPos : 1 - inPos;
            return Find(parent, fc[pos]) == Find(parent, fc[pos + 2]);
        }

        /// The cell's surface patches. Returns how many there are (1-4) and, two bits per edge in
        /// edge order, which patch each crossing edge belongs to. `joinInsideBits` has bit f set when
        /// ambiguous face f joins its inside diagonal; bits for unambiguous faces are ignored.
        public static int Components(int insideMask, int joinInsideBits, out int edgeComponents)
        {
            Span<int> parent = stackalloc int[8];
            JoinRegions(parent, insideMask, Ambiguous[insideMask], joinInsideBits);

            Span<int> keys = stackalloc int[8];
            int count = 0;
            edgeComponents = 0;
            for (int e = 0; e < 12; e++)
            {
                int a = EdgeA[e], b = EdgeB[e];
                bool insideA = (insideMask >> a & 1) != 0;
                if (insideA == ((insideMask >> b & 1) != 0)) continue;

                int key = insideA ? Find(parent, a) * 8 + Find(parent, b) : Find(parent, b) * 8 + Find(parent, a);
                int index = 0;
                while (index < count && keys[index] != key) index++;
                if (index == count) keys[count++] = key;
                edgeComponents |= index << (2 * e);
            }
            return count;
        }

        /// Same-sign edge regions of the cell, plus the chosen diagonal of each face in `faces`.
        private static void JoinRegions(Span<int> parent, int insideMask, int faces, int joinInsideBits)
        {
            int regionBase = insideMask * 8;
            for (int c = 0; c < 8; c++) parent[c] = Region[regionBase + c];

            for (int f = 0; f < 6; f++)
            {
                if ((faces >> f & 1) == 0) continue;
                int[] fc = FaceCorners[f];
                int inPos = (insideMask >> fc[0] & 1) != 0 ? 0 : 1;
                int pos = (joinInsideBits >> f & 1) != 0 ? inPos : 1 - inPos;
                Union(parent, fc[pos], fc[pos + 2]);
            }
        }

        private static bool Connected(int mask, int a, int b) => Region[mask * 8 + a] == Region[mask * 8 + b];

        private static int Find(Span<int> parent, int x)
        {
            while (parent[x] != x) x = parent[x];
            return x;
        }

        private static void Union(Span<int> parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a == b) return;
            if (a < b) parent[b] = a; else parent[a] = b;
        }
    }

    /// Decides every ambiguous face of one extraction together.
    ///
    /// SurfaceNetsTopology.JoinsInside is exact for a face whose two cells have no OTHER ambiguous
    /// face. When a cell has several, each decision changes what is connected around the cell for
    /// the others, and the local rule - which assumes the other faces join nothing - can pick the
    /// collapsing side. So after the local pass every face is re-checked against the actual
    /// decisions of its cells' other faces, and a collapsing face is flipped, which always cures that
    /// face (same planarity argument). Flips run in rounds, each decided from the previous round's
    /// state; where collapsing faces share a cell, only the one the decider was least sure about
    /// flips, so neighbours do not flip each other back and forth. Every choice reads signs and
    /// values only, so mirror-symmetric input still resolves symmetrically.
    ///
    /// Only cells that are not single clean patches are added - the neighbour across an ambiguous
    /// face has that face too, so it is always among them - which keeps this proportional to the
    /// number of creases, not to the mesh. Not thread-safe while being filled or resolved; JoinsFor
    /// may be called concurrently once Resolve has returned.
    internal sealed class AmbiguityResolver
    {
        private const int MaxRounds = 16;

        private int _nx, _ny, _nz;
        private readonly Dictionary<long, int> _index = new Dictionary<long, int>();
        private readonly List<int> _x = new List<int>();
        private readonly List<int> _y = new List<int>();
        private readonly List<int> _z = new List<int>();
        private readonly List<int> _mask = new List<int>();
        private readonly List<float> _values = new List<float>();
        private readonly List<int> _collapsing = new List<int>();
        private int[] _joins = new int[0];
        private int[] _neighbour = new int[0];
        private float[] _priority = new float[0];

        public int CellCount => _mask.Count;

        /// Faces still collapsing when the last round ran out; 0 whenever resolution converged.
        public int ResidualCollapses { get; private set; }

        public void Reset(int nx, int ny, int nz)
        {
            _nx = nx; _ny = ny; _nz = nz;
            _index.Clear();
            _x.Clear(); _y.Clear(); _z.Clear();
            _mask.Clear();
            _values.Clear();
            ResidualCollapses = 0;
        }

        public void Add(int x, int y, int z, int insideMask, ReadOnlySpan<float> cornerValues)
        {
            long key = Key(x, y, z);
            if (_index.ContainsKey(key)) return;
            _index.Add(key, _mask.Count);
            _x.Add(x); _y.Add(y); _z.Add(z);
            _mask.Add(insideMask);
            for (int c = 0; c < 8; c++) _values.Add(cornerValues[c]);
        }

        /// The join-inside bits Resolve settled on for a cell (0 for a cell never added).
        public int JoinsFor(int x, int y, int z) => _index.TryGetValue(Key(x, y, z), out int i) ? _joins[i] : 0;

        public void Resolve()
        {
            int n = _mask.Count;
            ResidualCollapses = 0;
            if (n == 0) return;
            if (_joins.Length < n)
            {
                _joins = new int[n];
                _priority = new float[n];
                _neighbour = new int[n * 6];
            }

            for (int i = 0; i < n; i++)
            {
                int mask = _mask[i];
                int ambiguous = SurfaceNetsTopology.AmbiguousFaces(mask);
                int joins = 0;
                for (int f = 0; f < 6; f++)
                {
                    int j = -1;
                    if ((ambiguous >> f & 1) != 0)
                    {
                        int axis = SurfaceNetsTopology.FaceAxis(f), step = SurfaceNetsTopology.FaceStep(f);
                        int ax = _x[i] + (axis == 0 ? step : 0);
                        int ay = _y[i] + (axis == 1 ? step : 0);
                        int az = _z[i] + (axis == 2 ? step : 0);
                        if (ax >= 0 && ay >= 0 && az >= 0 && ax < _nx && ay < _ny && az < _nz &&
                            _index.TryGetValue(Key(ax, ay, az), out int found))
                            j = found;

                        if (SurfaceNetsTopology.JoinsInside(f, mask, j >= 0 ? _mask[j] : -1,
                                FaceValue(i, f, 0), FaceValue(i, f, 1), FaceValue(i, f, 2), FaceValue(i, f, 3)))
                            joins |= 1 << f;
                    }
                    _neighbour[i * 6 + f] = j;
                }
                _joins[i] = joins;
            }

            for (int round = 0; round < MaxRounds; round++)
            {
                _collapsing.Clear();
                for (int i = 0; i < n; i++)
                {
                    int mask = _mask[i];
                    int ambiguous = SurfaceNetsTopology.AmbiguousFaces(mask);
                    // Each interior face once, from the cell on its minus side.
                    for (int f = 1; f < 6; f += 2)
                    {
                        int j = _neighbour[i * 6 + f];
                        if ((ambiguous >> f & 1) == 0 || j < 0) continue;
                        bool separatedInside = (_joins[i] >> f & 1) == 0;
                        if (SurfaceNetsTopology.DiagonalLinked(mask, _joins[i], f, separatedInside) &&
                            SurfaceNetsTopology.DiagonalLinked(_mask[j], _joins[j], f ^ 1, separatedInside))
                            _collapsing.Add(i * 6 + f);
                    }
                }

                ResidualCollapses = _collapsing.Count;
                if (_collapsing.Count == 0) return;

                foreach (int entry in _collapsing)
                {
                    _priority[entry / 6] = float.MaxValue;
                    _priority[_neighbour[entry]] = float.MaxValue;
                }
                foreach (int entry in _collapsing)
                {
                    int i = entry / 6, j = _neighbour[entry];
                    float p = Confidence(i, entry % 6);
                    if (p < _priority[i]) _priority[i] = p;
                    if (p < _priority[j]) _priority[j] = p;
                }
                foreach (int entry in _collapsing)
                {
                    int i = entry / 6, f = entry % 6, j = _neighbour[entry];
                    float p = Confidence(i, f);
                    if (p > _priority[i] || p > _priority[j]) continue;
                    _joins[i] ^= 1 << f;
                    _joins[j] ^= 1 << (f ^ 1);
                }
            }
        }

        private float Confidence(int cell, int face) =>
            SurfaceNetsTopology.DeciderConfidence(_mask[cell], face,
                FaceValue(cell, face, 0), FaceValue(cell, face, 1), FaceValue(cell, face, 2), FaceValue(cell, face, 3));

        private float FaceValue(int cell, int face, int k) => _values[cell * 8 + SurfaceNetsTopology.FaceCorner(face, k)];

        private long Key(int x, int y, int z) => x + (long)_nx * (y + (long)_ny * z);
    }
}
