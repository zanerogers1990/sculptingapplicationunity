using System;
using UnityEngine;

namespace Sculpting
{
    /// One element of the symmetry a stroke is repeated under: an orthogonal linear map of the
    /// object's local space - a mirror reflection, a rotation about an axis through the origin, or
    /// a composition of the two. It replaces the component-wise mirror SIGN every brush used to
    /// carry, which could only express reflections across the three coordinate planes.
    ///
    /// Orthogonal, so the same map transforms points, directions and normals alike (a normal's
    /// transform is the inverse transpose, which for an orthogonal matrix is the matrix itself),
    /// lengths are preserved, and the inverse is the transpose.
    ///
    /// A map that is a pure sign flip - every mirror-only element - is flagged Diagonal and
    /// applied as exactly the Vector3.Scale it replaces, so mirrored sculpting stays bit for bit
    /// what it was (see SymmetryDriftTests: mirrored halves are held BITWISE identical, and a
    /// 3x3 multiply, although it would round to the same values, is not something to lean on).
    /// Quarter-turn rotations about a coordinate axis come out as exact 0/+-1 matrices for the
    /// same reason, and a half turn about one is itself diagonal.
    ///
    /// Plain fields, so it is blittable and usable from Burst jobs (see SculptController.DabOp).
    public struct SymmetryOp : IEquatable<SymmetryOp>
    {
        public Vector3 Row0, Row1, Row2;

        /// True when the map is a pure per-axis sign flip (identity included) - see the type's
        /// remarks.
        public bool Diagonal;

        public static readonly SymmetryOp Identity = FromSign(Vector3.one);

        /// The per-axis signs of a Diagonal op (the mirror sign it stands for).
        public Vector3 Sign => new Vector3(Row0.x, Row1.y, Row2.z);

        public bool IsIdentity => Diagonal && Row0.x > 0f && Row1.y > 0f && Row2.z > 0f;

        /// True for a map that reverses handedness (an odd number of reflections).
        public bool Reflects => Determinant < 0f;

        public float Determinant =>
            Row0.x * (Row1.y * Row2.z - Row1.z * Row2.y)
          - Row0.y * (Row1.x * Row2.z - Row1.z * Row2.x)
          + Row0.z * (Row1.x * Row2.y - Row1.y * Row2.x);

        public static SymmetryOp FromSign(Vector3 sign) => new SymmetryOp
        {
            Row0 = new Vector3(sign.x, 0f, 0f),
            Row1 = new Vector3(0f, sign.y, 0f),
            Row2 = new Vector3(0f, 0f, sign.z),
            Diagonal = true,
        };

        /// Rotation by step/count of a full turn about `axis` (need not be normalized).
        ///
        /// Built in double and rounded once, so every step is as close to its true rotation as a
        /// float matrix can be - never a power of the one-step matrix, whose error would grow with
        /// the step. Values within 1e-12 of 0 or +-1 are snapped: that makes quarter turns about a
        /// coordinate axis exact (cos 90 is 0, not 6e-17), so N = 2 and N = 4 about X, Y or Z are
        /// exactly representable symmetries, like the mirrors.
        public static SymmetryOp Rotation(Vector3 axis, int step, int count)
        {
            double ax = axis.x, ay = axis.y, az = axis.z;
            double length = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (count <= 0 || length < 1e-12) return Identity;
            ax /= length; ay /= length; az /= length;

            // Reduced to [0, count) so every equivalent step gets the identical matrix.
            int k = ((step % count) + count) % count;
            if (k == 0) return Identity;
            double angle = 2.0 * Math.PI * k / count;
            double c = Snap(Math.Cos(angle)), s = Snap(Math.Sin(angle)), t = 1.0 - c;

            // Rodrigues. Snapped per entry, so an axis-aligned rotation's off-axis entries (whose
            // terms all carry a factor of a zero axis component) stay exactly 0.
            var op = new SymmetryOp
            {
                Row0 = new Vector3((float)Snap(t * ax * ax + c), (float)Snap(t * ax * ay - s * az), (float)Snap(t * ax * az + s * ay)),
                Row1 = new Vector3((float)Snap(t * ax * ay + s * az), (float)Snap(t * ay * ay + c), (float)Snap(t * ay * az - s * ax)),
                Row2 = new Vector3((float)Snap(t * ax * az - s * ay), (float)Snap(t * ay * az + s * ax), (float)Snap(t * az * az + c)),
            };
            op.Diagonal = IsSignMatrix(op);
            return op;
        }

        private static double Snap(double v)
        {
            if (Math.Abs(v) < 1e-12) return 0.0;
            if (Math.Abs(v - 1.0) < 1e-12) return 1.0;
            if (Math.Abs(v + 1.0) < 1e-12) return -1.0;
            return v;
        }

        private static bool IsSignMatrix(SymmetryOp op) =>
            op.Row0.y == 0f && op.Row0.z == 0f && op.Row1.x == 0f && op.Row1.z == 0f &&
            op.Row2.x == 0f && op.Row2.y == 0f &&
            Mathf.Abs(op.Row0.x) == 1f && Mathf.Abs(op.Row1.y) == 1f && Mathf.Abs(op.Row2.z) == 1f;

        /// Maps a local point, direction or normal.
        public Vector3 Apply(Vector3 v)
        {
            if (Diagonal) return new Vector3(v.x * Row0.x, v.y * Row1.y, v.z * Row2.z); // == Vector3.Scale
            return new Vector3(
                Row0.x * v.x + Row0.y * v.y + Row0.z * v.z,
                Row1.x * v.x + Row1.y * v.y + Row1.z * v.z,
                Row2.x * v.x + Row2.y * v.y + Row2.z * v.z);
        }

        /// The inverse map (the transpose - see the type's remarks).
        public Vector3 ApplyInverse(Vector3 v)
        {
            if (Diagonal) return new Vector3(v.x * Row0.x, v.y * Row1.y, v.z * Row2.z); // a sign flip is its own inverse
            return new Vector3(
                Row0.x * v.x + Row1.x * v.y + Row2.x * v.z,
                Row0.y * v.x + Row1.y * v.y + Row2.y * v.z,
                Row0.z * v.x + Row1.z * v.y + Row2.z * v.z);
        }

        /// a * b: the map that applies b first and then a.
        public static SymmetryOp Compose(SymmetryOp a, SymmetryOp b)
        {
            if (a.Diagonal && b.Diagonal) return FromSign(Vector3.Scale(a.Sign, b.Sign));

            // Columns of b, each mapped through a.
            Vector3 c0 = a.Apply(new Vector3(b.Row0.x, b.Row1.x, b.Row2.x));
            Vector3 c1 = a.Apply(new Vector3(b.Row0.y, b.Row1.y, b.Row2.y));
            Vector3 c2 = a.Apply(new Vector3(b.Row0.z, b.Row1.z, b.Row2.z));
            var op = new SymmetryOp
            {
                Row0 = new Vector3(c0.x, c1.x, c2.x),
                Row1 = new Vector3(c0.y, c1.y, c2.y),
                Row2 = new Vector3(c0.z, c1.z, c2.z),
            };
            op.Diagonal = IsSignMatrix(op);
            return op;
        }

        /// Largest entry-wise difference - how two ops that should be the same element (say a
        /// rotation reached two ways) are recognised despite rounding.
        public static float MaxDifference(SymmetryOp a, SymmetryOp b)
        {
            Vector3 d0 = a.Row0 - b.Row0, d1 = a.Row1 - b.Row1, d2 = a.Row2 - b.Row2;
            return Mathf.Max(MaxAbs(d0), Mathf.Max(MaxAbs(d1), MaxAbs(d2)));
        }

        private static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        // Exact, component by component - Vector3's == is a 1e-5 approximate comparison.
        public bool Equals(SymmetryOp other) =>
            Row0.Equals(other.Row0) && Row1.Equals(other.Row1) && Row2.Equals(other.Row2) && Diagonal == other.Diagonal;

        public override bool Equals(object obj) => obj is SymmetryOp other && Equals(other);

        public override int GetHashCode() => Row0.GetHashCode() ^ (Row1.GetHashCode() * 397) ^ (Row2.GetHashCode() * 7919);

        public override string ToString() => Diagonal ? $"Sign{Sign}" : $"[{Row0} {Row1} {Row2}]";
    }
}
