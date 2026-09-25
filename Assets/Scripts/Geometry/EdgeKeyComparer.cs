using System.Collections.Generic;

namespace Sculpting
{
    /// Hash comparer for the packed `(long)a &lt;&lt; 32 | (uint)b` vertex-index pairs this project
    /// uses as edge keys (MeshRemesher.EdgeKey, MeshExtractor.EdgeKey).
    ///
    /// Exists purely for speed - equality is plain `==`, exactly as the default comparer does,
    /// so swapping this in cannot change any lookup's RESULT. Only the bucket distribution
    /// changes.
    ///
    /// Why it's needed: `EqualityComparer&lt;long&gt;.Default.GetHashCode` is
    /// `(int)v ^ (int)(v >> 32)`, which for a packed edge key is exactly `a ^ b` - the XOR of
    /// the two vertex indices. That is catastrophic for mesh edges specifically, because the
    /// two endpoints of an edge are always NEARBY indices: Surface Nets numbers its vertices in
    /// grid scan order, so an edge's endpoints differ by 1, by the row stride, or by the slice
    /// stride, and `a ^ b` collapses to a tiny set of values. Measured on a real remesh output
    /// of 306,816 triangles: 460,224 distinct edge keys produced only 5,678 distinct hashes -
    /// a ~81:1 collision ratio, turning every Dictionary bucket into a long linear scan and
    /// every O(n) edge pass into an O(n^2) one.
    ///
    /// The splitmix64 finalizer below mixes every input bit into the output, so adjacent index
    /// pairs land in unrelated buckets. This was first added to fix MeshRemesher.PatchHoles,
    /// where it took remesh at resolution 192 from 66.1s to 0.42s and at resolution 256 from
    /// 235.6s to 0.74s on a 202,800-triangle source, output verified bit-identical. PatchHoles
    /// has since stopped hashing on its hot path entirely (it counting-sorts edges into flat
    /// arrays instead) and only reaches this comparer for the small set of edges around an
    /// actual hole; MeshExtractor is now the main user, on its live preview path.
    internal sealed class EdgeKeyComparer : IEqualityComparer<long>
    {
        public static readonly EdgeKeyComparer Instance = new EdgeKeyComparer();

        private EdgeKeyComparer() { }

        public bool Equals(long a, long b) => a == b;

        public int GetHashCode(long value)
        {
            // splitmix64 finalizer (Steele et al., "Fast Splittable Pseudorandom Number
            // Generators") - an avalanche mix, not a random number generator here.
            ulong x = (ulong)value;
            x ^= x >> 30;
            x *= 0xbf58476d1ce4e5b9UL;
            x ^= x >> 27;
            x *= 0x94d049bb133111ebUL;
            x ^= x >> 31;
            return (int)x;
        }
    }
}
