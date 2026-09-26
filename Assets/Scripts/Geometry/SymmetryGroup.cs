using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Every symmetry an object is sculpted under, as one list of SymmetryOps: the mirror
    /// reflections that are switched on, times N rotations about the radial axis, duplicates
    /// removed. A brush dab is applied once per element (see SculptController.MirroredDabs).
    ///
    /// Element 0 is always the identity - the stroke the user actually made. Mirror-only lists come
    /// out in flip-mask order (X = 1, Y = 2, Z = 4, counting up over the enabled axes), which is
    /// the enumeration the order-symmetric dab walk used before rotations existed, so mirrored
    /// sculpting walks exactly the same sequence it always did.
    ///
    /// Combining works as a real group whenever each enabled mirror plane either contains the
    /// radial axis or is perpendicular to it - always true for an X/Y/Z axis. Mirror X with radial
    /// about Y, for instance, is the dihedral group of 2N elements; Mirror X and Z with an even N
    /// about Y adds nothing Mirror X had not (X then Z IS the half turn), which is why duplicates
    /// are removed rather than applied twice. A custom axis tilted against an enabled mirror plane
    /// generates an infinite group; the list is then just the product, IsGroup is false, and the
    /// dab walk falls back to applying it in plain sequence (see InteractingSubgroup).
    public sealed class SymmetryGroup
    {
        public const int MinRadialCount = 2;
        public const int MaxRadialCount = 32;

        // Two ops closer than this entry for entry are the same element reached two ways.
        private const float MatchTolerance = 1e-4f;

        private readonly SymmetryOp[] _ops;
        private readonly int[] _product; // [a * Count + b] = index of a * b, or -1 when not closed

        // Scratch for the walk-planning queries below. Main thread only, like the brushes that ask.
        private readonly bool[] _member;
        private readonly int[] _cosetStamp;
        private int _cosetGeneration;

        public int Count => _ops.Length;
        public SymmetryOp this[int index] => _ops[index];

        /// Closed under composition, so the order-symmetric walk can average over it.
        public bool IsGroup { get; }

        /// Rotations about the radial axis (1 when radial symmetry is off).
        public int RadialCount { get; }

        /// One stroke, no repeats - what an object without symmetry, or with no target, uses.
        public static readonly SymmetryGroup Trivial = Build(false, false, false, 1, Vector3.up);

        private SymmetryGroup(List<SymmetryOp> ops, int radialCount)
        {
            _ops = ops.ToArray();
            RadialCount = radialCount;
            int n = _ops.Length;
            _product = new int[n * n];
            _member = new bool[n];
            _cosetStamp = new int[n];

            bool closed = true;
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                int index = IndexOf(SymmetryOp.Compose(_ops[a], _ops[b]));
                _product[a * n + b] = index;
                if (index < 0) closed = false;
            }
            IsGroup = closed;
        }

        /// `radialCount` below 2 means no radial symmetry; it is clamped to MaxRadialCount.
        /// `radialAxis` is a local-space direction through the object's origin.
        public static SymmetryGroup Build(bool mirrorX, bool mirrorY, bool mirrorZ, int radialCount, Vector3 radialAxis)
        {
            int n = radialCount < MinRadialCount ? 1 : Mathf.Min(radialCount, MaxRadialCount);
            int active = (mirrorX ? 1 : 0) | (mirrorY ? 2 : 0) | (mirrorZ ? 4 : 0);
            var ops = new List<SymmetryOp>(n * 8);
            for (int k = 0; k < n; k++)
            {
                SymmetryOp rotation = SymmetryOp.Rotation(radialAxis, k, n);
                for (int mask = 0; mask < 8; mask++)
                {
                    if ((mask & ~active) != 0) continue;
                    SymmetryOp op = SymmetryOp.Compose(SymmetryOp.FromSign(SignOfFlipMask(mask)), rotation);
                    if (IndexIn(ops, op) < 0) ops.Add(op);
                }
            }
            return new SymmetryGroup(ops, n);
        }

        /// This group - built about the WORLD origin and axes - as the same symmetry of an
        /// object's local space (see SymmetryOp.InObjectFrame). Conjugation keeps the product
        /// table, so the result walks exactly like this one.
        public SymmetryGroup InObjectFrame(Quaternion rotation, Vector3 position, Matrix4x4 worldToLocal)
        {
            var ops = new List<SymmetryOp>(_ops.Length);
            for (int i = 0; i < _ops.Length; i++)
                ops.Add(SymmetryOp.InObjectFrame(_ops[i], rotation, position, worldToLocal));
            return new SymmetryGroup(ops, RadialCount);
        }

        public static Vector3 SignOfFlipMask(int mask) =>
            new Vector3((mask & 1) != 0 ? -1f : 1f, (mask & 2) != 0 ? -1f : 1f, (mask & 4) != 0 ? -1f : 1f);

        /// Index of a * b, or -1 when the list is not closed and the product falls outside it.
        public int Product(int a, int b) => _product[a * _ops.Length + b];

        /// Index of the element matching `op` to within rounding, or -1.
        public int IndexOf(SymmetryOp op)
        {
            for (int i = 0; i < _ops.Length; i++)
                if (SymmetryOp.MaxDifference(_ops[i], op) <= MatchTolerance) return i;
            return -1;
        }

        private static int IndexIn(List<SymmetryOp> ops, SymmetryOp op)
        {
            for (int i = 0; i < ops.Count; i++)
                if (SymmetryOp.MaxDifference(ops[i], op) <= MatchTolerance) return i;
            return -1;
        }

        // ------------------------------------------------------------------ dab-walk planning

        /// The subgroup generated by every element that maps `point` to within `distance` of
        /// itself, as ascending indices (identity first). Those are the copies of a dab at `point`
        /// that can reach each other's footprint: g p and g' p are |p - g^-1 g' p| apart, so two
        /// copies interact exactly when g^-1 g' is one of the generators - and copies in different
        /// cosets of this subgroup never do.
        ///
        /// Just the identity for a list that is not a group: nothing can be averaged over then.
        public void InteractingSubgroup(Vector3 point, float distance, List<int> subgroup)
        {
            subgroup.Clear();
            subgroup.Add(0);
            if (!IsGroup || _ops.Length == 1) return;

            float limitSqr = distance * distance;
            System.Array.Clear(_member, 0, _member.Length);
            _member[0] = true;
            int generators = 0;
            for (int g = 1; g < _ops.Length; g++)
            {
                if ((point - _ops[g].ApplyPoint(point)).sqrMagnitude >= limitSqr) continue;
                _member[g] = true;
                subgroup.Add(g);
                generators++;
            }
            if (generators == 0) return;

            // Closure: every element found so far times every generator, until nothing new appears.
            // The generators are subgroup[1..generators], which stay put as the list grows.
            for (int i = 0; i < subgroup.Count; i++)
            for (int j = 1; j <= generators; j++)
            {
                int product = Product(subgroup[i], subgroup[j]);
                if (_member[product]) continue;
                _member[product] = true;
                subgroup.Add(product);
            }
            subgroup.Sort();
        }

        /// The left cosets of `subgroup` (ascending indices, identity first), each named by its
        /// lowest-index element, ascending. With a mirror-only list that representative is the
        /// coset's "far" flips alone, which is the enumeration the dab walk has always used.
        public void CosetRepresentatives(List<int> subgroup, List<int> representatives)
        {
            representatives.Clear();
            int generation = ++_cosetGeneration;
            for (int g = 0; g < _ops.Length; g++)
            {
                if (_cosetStamp[g] == generation) continue;
                representatives.Add(g);
                for (int h = 0; h < subgroup.Count; h++)
                {
                    int member = Product(g, subgroup[h]);
                    if (member >= 0) _cosetStamp[member] = generation;
                }
            }
        }
    }
}
