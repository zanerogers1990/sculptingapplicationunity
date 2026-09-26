using UnityEngine;

namespace Sculpting
{
    /// The per-edge math of the quad remesher's fields (Jakob et al. 2015, "Instant Field-Aligned
    /// Meshes", extrinsic 4-RoSy / 4-PoSy variants).
    ///
    /// Each vertex carries an orientation q (a unit tangent; q and n x q are the two quad edge
    /// directions, each up to sign, so q is only defined up to 90-degree turns) and a position
    /// offset o (a point near the vertex that sits on the vertex's local quad lattice of spacing
    /// `scale`). Neighbours never compare their raw values - first they find the rotation or
    /// lattice shift under which the two agree best, which is what these functions compute.
    internal static class FieldMath
    {
        /// The pair of representatives of q0's and q1's 4-fold symmetry classes that are closest
        /// to each other: among {q0, n0 x q0} and {q1, n1 x q1}, the pair with the largest |dot|,
        /// with q1's sign flipped to match.
        public static void CompatOrientation(Vector3 q0, Vector3 n0, Vector3 q1, Vector3 n1,
                                             out Vector3 a, out Vector3 b)
        {
            Vector3 t0 = Vector3.Cross(n0, q0), t1 = Vector3.Cross(n1, q1);
            float d00 = Vector3.Dot(q0, q1), d01 = Vector3.Dot(q0, t1);
            float d10 = Vector3.Dot(t0, q1), d11 = Vector3.Dot(t0, t1);
            float a00 = Mathf.Abs(d00), a01 = Mathf.Abs(d01), a10 = Mathf.Abs(d10), a11 = Mathf.Abs(d11);

            if (a00 >= a01 && a00 >= a10 && a00 >= a11) { a = q0; b = d00 >= 0 ? q1 : -q1; }
            else if (a01 >= a10 && a01 >= a11) { a = q0; b = d01 >= 0 ? t1 : -t1; }
            else if (a10 >= a11) { a = t0; b = d10 >= 0 ? q1 : -q1; }
            else { a = t0; b = d11 >= 0 ? t1 : -t1; }
        }

        /// A point both tangent planes agree is "between" p0 and p1: the midpoint, pulled off the
        /// chord toward where the two planes meet, so on a curved surface the two lattices are
        /// compared near the surface rather than inside it.
        public static Vector3 MiddlePoint(Vector3 p0, Vector3 n0, Vector3 p1, Vector3 n1)
        {
            float n0p0 = Vector3.Dot(n0, p0), n0p1 = Vector3.Dot(n0, p1);
            float n1p0 = Vector3.Dot(n1, p0), n1p1 = Vector3.Dot(n1, p1);
            float n0n1 = Vector3.Dot(n0, n1);
            float denom = 1f / (1f - n0n1 * n0n1 + 1e-4f);
            float lambda0 = 2f * (n0p1 - n0p0 - n0n1 * (n1p0 - n1p1)) * denom;
            float lambda1 = 2f * (n1p0 - n1p1 - n0n1 * (n0p1 - n0p0)) * denom;
            return (p0 + p1) * 0.5f - (n0 * lambda0 + n1 * lambda1) * 0.25f;
        }

        /// The lattice point (of the lattice through o, axes q and n x q, spacing scale) nearest p.
        public static Vector3 PositionRound(Vector3 o, Vector3 q, Vector3 n, Vector3 p, float scale, float invScale)
        {
            Vector3 t = Vector3.Cross(n, q);
            Vector3 d = p - o;
            return o + q * (Mathf.Round(Vector3.Dot(q, d) * invScale) * scale)
                     + t * (Mathf.Round(Vector3.Dot(t, d) * invScale) * scale);
        }

        /// Integer lattice coordinates of the cell corner below p (floor in both axes).
        public static void PositionFloorIndex(Vector3 o, Vector3 q, Vector3 n, Vector3 p, float invScale,
                                              out int i, out int j)
        {
            Vector3 t = Vector3.Cross(n, q);
            Vector3 d = p - o;
            i = Mathf.FloorToInt(Vector3.Dot(q, d) * invScale);
            j = Mathf.FloorToInt(Vector3.Dot(t, d) * invScale);
        }

        /// The two lattice points - one from each vertex's lattice, both near the pair's middle
        /// point - that are closest to each other. Returns them as integer coordinates relative
        /// to each vertex's own offset (index0 in o0's lattice, index1 in o1's). q0/q1 must
        /// already be rotation-matched (CompatOrientation). Equal indices mean o0 and o1 are the
        /// same lattice vertex; indices one step apart mean they are lattice neighbours.
        ///
        /// Written in scalar floats rather than Vector3 operators: this is the innermost loop of
        /// both the position solve and extraction (16 candidate pairs per edge per sweep), and
        /// Unity's Mono runs Vector3-operator code several times slower than the same scalar
        /// arithmetic - measured 7.6x slower than .NET on the Vector3 form, the single biggest
        /// cost of a remesh in the app.
        public static float CompatPositionIndex(Vector3 p0, Vector3 n0, Vector3 q0, Vector3 o0,
                                                Vector3 p1, Vector3 n1, Vector3 q1, Vector3 o1,
                                                float scale, float invScale,
                                                out int i0, out int j0, out int i1, out int j1)
        {
            // t = n x q for both frames.
            float t0x = n0.y * q0.z - n0.z * q0.y, t0y = n0.z * q0.x - n0.x * q0.z, t0z = n0.x * q0.y - n0.y * q0.x;
            float t1x = n1.y * q1.z - n1.z * q1.y, t1y = n1.z * q1.x - n1.x * q1.z, t1z = n1.x * q1.y - n1.y * q1.x;

            // Middle point (see MiddlePoint), inlined.
            float n0p0 = n0.x * p0.x + n0.y * p0.y + n0.z * p0.z, n0p1 = n0.x * p1.x + n0.y * p1.y + n0.z * p1.z;
            float n1p0 = n1.x * p0.x + n1.y * p0.y + n1.z * p0.z, n1p1 = n1.x * p1.x + n1.y * p1.y + n1.z * p1.z;
            float n0n1 = n0.x * n1.x + n0.y * n1.y + n0.z * n1.z;
            float denom = 1f / (1f - n0n1 * n0n1 + 1e-4f);
            float lambda0 = 2f * (n0p1 - n0p0 - n0n1 * (n1p0 - n1p1)) * denom;
            float lambda1 = 2f * (n1p0 - n1p1 - n0n1 * (n0p1 - n0p0)) * denom;
            float mx = (p0.x + p1.x) * 0.5f - (n0.x * lambda0 + n1.x * lambda1) * 0.25f;
            float my = (p0.y + p1.y) * 0.5f - (n0.y * lambda0 + n1.y * lambda1) * 0.25f;
            float mz = (p0.z + p1.z) * 0.5f - (n0.z * lambda0 + n1.z * lambda1) * 0.25f;

            // Cell below the middle point in each lattice.
            float d0x = mx - o0.x, d0y = my - o0.y, d0z = mz - o0.z;
            float d1x = mx - o1.x, d1y = my - o1.y, d1z = mz - o1.z;
            int fi0 = Mathf.FloorToInt((q0.x * d0x + q0.y * d0y + q0.z * d0z) * invScale);
            int fj0 = Mathf.FloorToInt((t0x * d0x + t0y * d0y + t0z * d0z) * invScale);
            int fi1 = Mathf.FloorToInt((q1.x * d1x + q1.y * d1y + q1.z * d1z) * invScale);
            int fj1 = Mathf.FloorToInt((t1x * d1x + t1y * d1y + t1z * d1z) * invScale);

            // Base corner of each cell, and the two lattice steps, pre-scaled.
            float b0x = o0.x + (q0.x * fi0 + t0x * fj0) * scale, b0y = o0.y + (q0.y * fi0 + t0y * fj0) * scale, b0z = o0.z + (q0.z * fi0 + t0z * fj0) * scale;
            float b1x = o1.x + (q1.x * fi1 + t1x * fj1) * scale, b1y = o1.y + (q1.y * fi1 + t1y * fj1) * scale, b1z = o1.z + (q1.z * fi1 + t1z * fj1) * scale;
            float u0x = q0.x * scale, u0y = q0.y * scale, u0z = q0.z * scale, v0x = t0x * scale, v0y = t0y * scale, v0z = t0z * scale;
            float u1x = q1.x * scale, u1y = q1.y * scale, u1z = q1.z * scale, v1x = t1x * scale, v1y = t1y * scale, v1z = t1z * scale;

            float best = float.MaxValue;
            int bestA = 0, bestB = 0;
            for (int a = 0; a < 4; a++)
            {
                float sa = a & 1, ta = a >> 1;
                float c0x = b0x + u0x * sa + v0x * ta, c0y = b0y + u0y * sa + v0y * ta, c0z = b0z + u0z * sa + v0z * ta;
                for (int b = 0; b < 4; b++)
                {
                    float sb = b & 1, tb = b >> 1;
                    float dx = c0x - (b1x + u1x * sb + v1x * tb);
                    float dy = c0y - (b1y + u1y * sb + v1y * tb);
                    float dz = c0z - (b1z + u1z * sb + v1z * tb);
                    float cost = dx * dx + dy * dy + dz * dz;
                    if (cost < best) { best = cost; bestA = a; bestB = b; }
                }
            }
            i0 = fi0 + (bestA & 1); j0 = fj0 + (bestA >> 1);
            i1 = fi1 + (bestB & 1); j1 = fj1 + (bestB >> 1);
            return best;
        }

        /// CompatPositionIndex, returning the two matched lattice points themselves.
        public static void CompatPosition(Vector3 p0, Vector3 n0, Vector3 q0, Vector3 o0,
                                          Vector3 p1, Vector3 n1, Vector3 q1, Vector3 o1,
                                          float scale, float invScale, out Vector3 a, out Vector3 b)
        {
            CompatPositionIndex(p0, n0, q0, o0, p1, n1, q1, o1, scale, invScale, out int i0, out int j0, out int i1, out int j1);
            a = o0 + (q0 * i0 + Vector3.Cross(n0, q0) * j0) * scale;
            b = o1 + (q1 * i1 + Vector3.Cross(n1, q1) * j1) * scale;
        }

        /// `v` projected into the plane with unit normal `n`, normalized; `fallback` if that
        /// leaves nothing (v parallel to n).
        public static Vector3 TangentUnit(Vector3 v, Vector3 n, Vector3 fallback)
        {
            return VectorMath.NormalizeOr(v - n * Vector3.Dot(n, v), fallback);
        }

        /// Some unit tangent of `n` - deterministic, for when nothing better is known.
        public static Vector3 AnyTangent(Vector3 n)
        {
            Vector3 axis = Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;
            return VectorMath.NormalizeOr(Vector3.Cross(n, axis), Vector3.forward);
        }
    }
}
