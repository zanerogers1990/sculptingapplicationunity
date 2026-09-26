using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// The symmetric-dab walk: one brush dab repeated under every element of the object's
    /// symmetry group (mirror reflections and radial rotations - see SymmetryGroup), with the
    /// per-element camera and tangent frame, and the ordering that keeps symmetric copies equal:
    /// bit-exact for mirrors, to rounding for rotations.
    public partial class SculptController
    {
        /// The symmetry group for the CURRENT target, off the reference SyncSelectionTarget
        /// already resolved once this frame.
        ///
        /// Every brush apply site used to reach this through the Mirror property, which resolves
        /// Target (a scene-manager lookup) and does a GetComponent - once per DAB, and Clay/Crease
        /// place up to ClayMaxDabsPerFrame/CreaseMaxDabsPerFrame of those in a single frame. The
        /// property's self-healing AddComponent path is still the fallback for a target that
        /// genuinely has no MirrorController, and the result is written back to the cached field so
        /// that only ever happens once per such target rather than once per dab.
        private SymmetryGroup Symmetry()
        {
            if (mirrorController == null) mirrorController = Mirror;
            return mirrorController != null ? mirrorController.GetSymmetry() : SymmetryGroup.Trivial;
        }

        /// The camera's local-space position, mapped through the same symmetry op (mirror plane(s),
        /// radial rotation) as the dab currently being applied. Every brush's symmetry loop sets it
        /// once per op (see BeginMirroredDab) and every Front Facing Only test downstream reads it
        /// instead of re-deriving the raw camera position, so a mirrored or rotated dab is judged
        /// from the mirrored or rotated viewpoint. The identity leaves it as the real camera, which
        /// is what an unmirrored session sees.
        ///
        /// Front Facing Only asks "is this vertex facing the viewer", and a MIRRORED dab is not
        /// being viewed from where the real camera is - it is the same stroke seen from the mirrored
        /// viewpoint. Testing it against the unmirrored camera throws away roughly half of every
        /// mirrored footprint the moment the model is turned off dead-centre. Measured on a
        /// 145k-triangle sculpt with Mirror X on and the camera swept around it: at 0 degrees the
        /// two footprints match (639 of 1280 against 635 of 1281), but at 30 degrees the near side
        /// keeps 649 of 1166 while the far side keeps 350 of 1161, and the deficit holds at roughly
        /// 2:1 across the whole orbit, closing again only at a dead-on 180. That is one half of a
        /// symmetric model quietly receiving twice the material of the other for a whole session -
        /// the reported "one ear was taking detail and the other was not" - and because the test is
        /// a hard 0/1 cut rather than a ramp, the surviving part of the far footprint is stamped in
        /// with a sharp edge straight through it, which is an artifact appearing on the side you are
        /// NOT working on. Reflecting the camera with the dab restores parity (measured 776 against
        /// the near side's 780 at 60 degrees) and costs the option nothing: each dab still rejects
        /// everything facing away from its OWN viewpoint, which is the whole point of the setting.
        private Vector3 _dabCameraLocal;

        /// Points the per-dab frame at one element of the symmetry group. Call once per element,
        /// before applying that element's dab - the loops below all do, including the identity, so
        /// no apply path can read a viewpoint left behind by the previous dab.
        private void BeginMirroredDab(SymmetryGroup group, int index)
        {
            _dabOpIndex = index;
            _dabOp = group[index];
            _dabCameraLocal = _dabOp.ApplyPoint(Frame.InverseTransformPoint(cam.transform.position));
        }

        /// The symmetry op the dab being applied is mapped through, and its index in the group.
        /// Identity - the default, and what a path called without a symmetry loop sees - is the
        /// stroke the user actually made. Set by BeginMirroredDab alongside _dabCameraLocal, for
        /// the same reason: anything a dab derives from a DIRECTION has to be mapped with it, not
        /// rebuilt on the far side.
        private SymmetryOp _dabOp = SymmetryOp.Identity;

        private int _dabOpIndex;

        /// BuildTangentBasis for the dab being applied, mapped (reflected, rotated) along with it.
        ///
        /// BuildTangentBasis crosses the normal with a FIXED world axis, and a reflection does not
        /// commute with that: fed a mirrored normal it hands back a frame whose tangent points the
        /// opposite way along the mirror image of the primary one. Clay's alpha stamp is read in
        /// that frame, so the far side of every mirrored stroke got the stamp flipped in its own
        /// frame instead of the mirror image of the near side's - invisible on a round soft
        /// circle, 11% of the stroke's displacement with the Noise alpha (15% rotated) three brush
        /// radii from the plane (SymmetryDriftTests). Building the frame from the normal reflected
        /// back to the primary side, then reflecting the frame forward, gives the mirrored dab the
        /// mirror image of the primary frame exactly, so it samples the stamp at identical
        /// coordinates. The primary dab (the identity) is unchanged bit for bit. A rotation does not
        /// commute with the fixed axis either, so a radial copy gets the ROTATED primary frame the
        /// same way, and its stamp turns with it instead of staying locked to world axes.
        private void BuildDabTangentBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent) =>
            DabTangentBasis(normal, _dabOp, out tangent, out bitangent);

        /// BuildDabTangentBasis for an explicit op - what a batched dab program uses, since it runs
        /// after _dabOp has moved on (see SculptController.DabProgram).
        private static void DabTangentBasis(Vector3 normal, SymmetryOp op, out Vector3 tangent, out Vector3 bitangent)
        {
            if (op.IsIdentity)
            {
                BuildTangentBasis(normal, out tangent, out bitangent);
                return;
            }

            BuildTangentBasis(op.ApplyInverse(normal), out tangent, out bitangent);
            tangent = op.Apply(tangent);
            bitangent = op.Apply(bitangent);
        }

        // ------------------------------------------------------------ order-symmetric mirrored dabs

        /// A dab closer than this many of its own reach to a mirror plane (or its radial copy) is
        /// treated as able to see its copy's output. 1 would be exact for dabs that only read and
        /// write inside their reach; the rest covers Smooth reading one ring of neighbours past its
        /// footprint, and a dab's own displacement carrying a vertex over the boundary mid-frame.
        private const float MirrorInteractionMargin = 1.5f;

        /// Walks the symmetry group for one dab (see BeginMirroredDabs / NextMirroredDab).
        ///
        /// Far from every mirror plane and radial axis this is exactly the old loop - each element
        /// once, in group order. Where copies' footprints meet the order is not innocent: the copies
        /// are applied one after another against the live vertex array, so the second dab reads
        /// positions (fits its plane, measures "deepest carve so far", weighs by distance) through
        /// vertices the first one has just moved, while the first saw the untouched surface. The
        /// copies were given different operations, always in the same order, so the difference did
        /// not average out - it accumulated stroke after stroke into the "one side slowly becomes
        /// different" drift. Measured on a bitwise-symmetric sphere a third of a radius off the
        /// plane: mirror error of 65% of the stroke's own displacement for Inflate, 12% for Clay
        /// (Accumulate), 8% for Dam Standard, with centreline vertices pushed well off the plane
        /// (SymmetryDriftTests). Radial copies near their axis meet in exactly the same way.
        ///
        /// There, the copies that can meet form a subgroup H (see SymmetryGroup.
        /// InteractingSubgroup); copies in different cosets of it never meet and are walked apart.
        /// Within a coset c H, ordering o applies c h_o h_0, c h_o h_1, ... - left-multiplying the
        /// group's own enumeration - each from the same starting surface, restored in between, and
        /// the results are averaged. A symmetry of the model maps that SET of orderings onto itself,
        /// so the average is symmetric by construction, while each ordering is still the existing
        /// brush behaviour (including the doubled strength a dab centred ON a plane has always had),
        /// so no brush changes character. One X plane through the dab costs two applications of the
        /// pair instead of one, and only for dabs that near the plane. For a mirror-only group this
        /// is the same sequence of signs the walk has always produced.
        ///
        /// Averaging costs orderings^2 applications, which a mirror group can afford (at most 8 of
        /// 8, and only where three planes meet) but radial symmetry cannot: near its axis EVERY
        /// copy meets its neighbours, so a large brush there paid 36 applications per dab for 6
        /// repeats - measured 52ms a frame for Clay and 202ms for Inflate at 330k triangles, against
        /// 16ms and 29ms applied once each. Applying one ordering instead (rotated from dab to dab)
        /// was fast but left 1-8% of a stroke's displacement between sectors near the axis, which
        /// Inflate then amplified through the normals to 166% at the pole with 8 repeats.
        ///
        /// So a group with rotations in it is walked Simultaneously instead: every copy of the
        /// coset is applied to the SAME starting surface, restored in between, and the displacements
        /// are combined - a sum, which does not care about order, so it is symmetric by construction
        /// at the plain walk's cost. Where copies do not overlap that sum is exactly the plain walk.
        /// Where they do, a sum would stack them (Crease carving N times as deep at the pole), so
        /// the sum is divided by the copies' multiplicity - how many of them sit on top of each
        /// other (see CopyMultiplicity): N coincident copies at the pole act as exactly one dab,
        /// profile and all, rather than piling up N-fold the way sequential radial symmetry does in
        /// other sculpting apps. One scalar for the whole coset, so it cannot favour a copy.
        ///
        /// Dividing each VERTEX by the number of copies covering it was tried first. It turned N
        /// coincident dabs into a flat-topped mesa with a cliff where the coverage fell below one,
        /// and Inflate amplified float noise on that cliff to 4% of the stroke at the pole.
        private struct MirroredDabWalk
        {
            public Vector3 Point;
            public float Reach;
            public SymmetryGroup Group;
            public bool Symmetric;
            /// Every copy applied once to the same surface, combined by multiplicity - see above. Set
            /// for any group with rotations; mirror-only groups keep the averaged orderings, bit for bit.
            public bool Simultaneous;
            public int Index, Count;              // plain walk over the group
            public int Coset, CosetCount;         // current coset of the interacting subgroup
            public int Ordering, Step, Orderings; // orderings over the subgroup, and position in one
        }

        // The interacting subgroup and its coset representatives for the walk in progress. Walks
        // never nest - each runs to completion inside one dab - so one pair serves them all.
        private readonly List<int> _walkSubgroup = new List<int>(8);
        private readonly List<int> _walkCosets = new List<int>(8);

        private readonly List<int> _mirrorGroupVertices = new List<int>();

        private int[] _mirrorGroupStamp;

        private int _mirrorGroupGeneration;

        private Vector3[] _mirrorGroupBefore = Array.Empty<Vector3>();

        private Vector3[] _mirrorGroupDeltaSum = Array.Empty<Vector3>();

        /// True while a repeat ordering is being applied. Per-dab bookkeeping that is not geometry
        /// (Clay's relax centres) must be recorded once, on the first ordering only.
        private bool _mirrorRepeatOrdering;

        /// `reach` is the widest radius the brush's per-copy apply reads or writes vertices within.
        private MirroredDabWalk BeginMirroredDabs(Vector3 localPoint, float reach)
        {
            SymmetryGroup group = Symmetry();
            var walk = new MirroredDabWalk
            {
                Point = localPoint, Reach = reach, Group = group, Index = -1, Count = group.Count, Step = -1,
            };
            if (group.Count <= 1) return walk;

            // Two copies interact when they land within twice the margin of each other - for a
            // mirror, when the dab is within the margin of the plane.
            group.InteractingSubgroup(localPoint, 2f * reach * MirrorInteractionMargin, _walkSubgroup);
            if (_walkSubgroup.Count <= 1) return walk;
            group.CosetRepresentatives(_walkSubgroup, _walkCosets);

            walk.Symmetric = true;
            walk.Orderings = _walkSubgroup.Count;
            walk.CosetCount = _walkCosets.Count;
            walk.Simultaneous = group.RadialCount > 1;
            return walk;
        }

        /// Advances the walk and points the per-dab frame at the next copy (BeginMirroredDab).
        private bool NextMirroredDab(ref MirroredDabWalk walk, out SymmetryOp op)
        {
            if (!walk.Symmetric)
            {
                _mirrorRepeatOrdering = false;
                if (++walk.Index >= walk.Count) { op = SymmetryOp.Identity; return false; }
                BeginMirroredDab(walk.Group, walk.Index);
                op = _dabOp;
                return true;
            }

            if (walk.Step < 0)
            {
                walk.Step = 0;
                BeginMirrorGroup(ref walk);
            }
            else if (walk.Simultaneous)
            {
                // Each copy's displacement is banked and the surface put back for the next one.
                AccumulateMirrorOrdering();
                if (++walk.Step < walk.Orderings)
                {
                    RestoreMirrorGroup();
                }
                else
                {
                    CommitMirrorGroup(1f / CopyMultiplicity(walk));
                    walk.Step = 0;
                    if (++walk.Coset == walk.CosetCount)
                    {
                        _mirrorRepeatOrdering = false;
                        op = SymmetryOp.Identity;
                        return false;
                    }
                    BeginMirrorGroup(ref walk);
                }
            }
            else if (++walk.Step == walk.Orderings)
            {
                walk.Step = 0;
                AccumulateMirrorOrdering();
                if (++walk.Ordering < walk.Orderings)
                {
                    RestoreMirrorGroup();
                }
                else
                {
                    // Exact for a mirror group (a power of two).
                    CommitMirrorGroup(1f / walk.Orderings);
                    walk.Ordering = 0;
                    if (++walk.Coset == walk.CosetCount)
                    {
                        _mirrorRepeatOrdering = false;
                        op = SymmetryOp.Identity;
                        return false;
                    }
                    BeginMirrorGroup(ref walk);
                }
            }

            // Ordering o applies c h_o h_0, c h_o h_1, ... - one row of the subgroup's own table,
            // which is what makes the SET of orderings map onto itself under any symmetry. The
            // simultaneous walk only ever runs ordering 0 (h_0 is the identity): c h_0, c h_1, ...
            _mirrorRepeatOrdering = walk.Ordering > 0;
            SymmetryGroup group = walk.Group;
            int h = group.Product(_walkSubgroup[walk.Ordering], _walkSubgroup[walk.Step]);
            BeginMirroredDab(group, group.Product(_walkCosets[walk.Coset], h));
            op = _dabOp;
            return true;
        }

        /// Snapshots every vertex the current coset's dabs can write, before the first of them runs.
        private void BeginMirrorGroup(ref MirroredDabWalk walk)
        {
            // Recording a batched program (see SculptController.DabProgram): every vertex takes its
            // own snapshot when it runs the op, so there is nothing to gather here.
            if (_dabProgramRecording) { RecordDabOp(DabOpKind.GroupBegin); return; }

            Vector3[] verts = sculptableMesh.Vertices;
            if (_mirrorGroupStamp == null || _mirrorGroupStamp.Length != verts.Length)
            {
                _mirrorGroupStamp = new int[verts.Length];
                _mirrorGroupGeneration = 0;
            }
            int generation = ++_mirrorGroupGeneration;
            _mirrorGroupVertices.Clear();

            SymmetryGroup group = walk.Group;
            int representative = _walkCosets[walk.Coset];
            for (int h = 0; h < walk.Orderings; h++)
            {
                Vector3 centre = group[group.Product(representative, _walkSubgroup[h])].ApplyPoint(walk.Point);
                // The spatial grid's shared buffer - consumed fully before the next query.
                List<int> found = sculptableMesh.QueryNear(centre, walk.Reach);
                for (int k = 0; k < found.Count; k++)
                {
                    int vi = found[k];
                    if ((uint)vi >= (uint)verts.Length || _mirrorGroupStamp[vi] == generation) continue;
                    _mirrorGroupStamp[vi] = generation;
                    _mirrorGroupVertices.Add(vi);
                }
            }

            int count = _mirrorGroupVertices.Count;
            if (_mirrorGroupBefore.Length < count)
            {
                _mirrorGroupBefore = new Vector3[count];
                _mirrorGroupDeltaSum = new Vector3[count];
            }
            for (int u = 0; u < count; u++)
            {
                _mirrorGroupBefore[u] = verts[_mirrorGroupVertices[u]];
                _mirrorGroupDeltaSum[u] = Vector3.zero;
            }
        }

        private void AccumulateMirrorOrdering()
        {
            if (_dabProgramRecording) { RecordDabOp(DabOpKind.Accumulate); return; }
            Vector3[] verts = sculptableMesh.Vertices;
            for (int u = 0; u < _mirrorGroupVertices.Count; u++)
                _mirrorGroupDeltaSum[u] += verts[_mirrorGroupVertices[u]] - _mirrorGroupBefore[u];
        }

        private void RestoreMirrorGroup()
        {
            if (_dabProgramRecording) { RecordDabOp(DabOpKind.Restore); return; }
            Vector3[] verts = sculptableMesh.Vertices;
            for (int u = 0; u < _mirrorGroupVertices.Count; u++) verts[_mirrorGroupVertices[u]] = _mirrorGroupBefore[u];
            MarkPositionMirrorStale();
        }

        /// Writes each vertex's snapshot plus its summed displacement times `scale`: 1 / orderings
        /// for the averaged walk, 1 / multiplicity for the simultaneous one. Deltas rather than
        /// positions, so a vertex no copy touched gets its own position back bit for bit.
        private void CommitMirrorGroup(float scale)
        {
            if (_dabProgramRecording) { RecordDabOp(DabOpKind.Commit, scale); return; }
            Vector3[] verts = sculptableMesh.Vertices;
            for (int u = 0; u < _mirrorGroupVertices.Count; u++)
                verts[_mirrorGroupVertices[u]] = _mirrorGroupBefore[u] + _mirrorGroupDeltaSum[u] * scale;
            MarkPositionMirrorStale();
        }

        /// How many copies of the dab sit on top of each other, for the simultaneous walk (see
        /// MirroredDabWalk): each copy of the coset counts by how much of its footprint it shares
        /// with the first one - the overlap fraction of two discs of the dab's reach - so the
        /// result is N when all N copies coincide (the dab on the axis), 1 when none overlap, and
        /// moves smoothly between as the dab travels off the axis. Copy k is |p - h_k p| from the
        /// first whichever coset it is in (the symmetries are rigid), so every copy and every coset
        /// gets the same number.
        private float CopyMultiplicity(in MirroredDabWalk walk)
        {
            double multiplicity = 0.0;
            for (int h = 0; h < walk.Orderings; h++)
            {
                float separation = (walk.Point - walk.Group[_walkSubgroup[h]].ApplyPoint(walk.Point)).magnitude;
                multiplicity += DiscOverlapFraction(separation / (2f * walk.Reach));
            }
            return (float)System.Math.Max(1.0, multiplicity);
        }

        /// Shared area of two equal discs whose centres are `u` diameters apart, as a fraction of
        /// one disc: 1 at u = 0, 0 from u = 1.
        private static double DiscOverlapFraction(double u)
        {
            if (u >= 1.0) return 0.0;
            if (u <= 0.0) return 1.0;
            return 2.0 / System.Math.PI * (System.Math.Acos(u) - u * System.Math.Sqrt(1.0 - u * u));
        }
    }
}
