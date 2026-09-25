using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// The mirrored-dab walk: one brush dab repeated at every mirror sign, with the per-sign
    /// camera, flip mask and tangent frame, and the ordering that keeps mirrored halves bit-exact.
    public partial class SculptController
    {
        /// The mirror sign list for the CURRENT target, off the reference SyncSelectionTarget
        /// already resolved once this frame.
        ///
        /// Every brush apply site used to reach this through the Mirror property, which resolves
        /// Target (a scene-manager lookup) and does a GetComponent - once per DAB, and Clay/Crease
        /// place up to ClayMaxDabsPerFrame/CreaseMaxDabsPerFrame of those in a single frame. The
        /// property's self-healing AddComponent path is still the fallback for a target that
        /// genuinely has no MirrorController, and the result is written back to the cached field so
        /// that only ever happens once per such target rather than once per dab.
        private List<Vector3> MirrorSigns()
        {
            if (mirrorController == null) mirrorController = Mirror;
            return mirrorController != null ? mirrorController.GetMirrorSigns() : IdentityMirrorSigns;
        }

        // Stand-in for a target with no MirrorController at all (Mirror returns null only when
        // there is no target). One unmirrored stroke, which is what "no mirroring" means.
        private static readonly List<Vector3> IdentityMirrorSigns = new List<Vector3> { Vector3.one };

        /// The camera's local-space position, reflected through the same mirror plane(s) as the dab
        /// currently being applied. Every brush's mirror loop sets it once per sign (see
        /// BeginMirroredDab) and every Front Facing Only test downstream reads it instead of
        /// re-deriving the raw camera position, so a mirrored dab is judged from the mirrored
        /// viewpoint. Identity sign leaves it as the real camera, which is what an unmirrored
        /// session sees.
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

        /// Points the per-dab frame at one mirror sign. Call once per sign, before applying that
        /// sign's dab - the mirror loops below all do, including the identity sign, so no apply path
        /// can read a viewpoint left behind by the previous dab.
        private void BeginMirroredDab(Vector3 sign)
        {
            _dabCameraLocal = Vector3.Scale(
                sculptableMesh.transform.InverseTransformPoint(cam.transform.position), sign);
            _dabFlipMask = FlipMaskOf(sign);
        }

        /// Which axes the dab being applied is reflected across: bit 0 X, bit 1 Y, bit 2 Z. 0 - the
        /// default, and what a path called without a mirror loop sees - is the unmirrored dab.
        /// Set by BeginMirroredDab alongside _dabCameraLocal, for the same reason: anything a dab
        /// derives from a DIRECTION has to be reflected with it, not rebuilt on the far side.
        private int _dabFlipMask;

        private static int FlipMaskOf(Vector3 sign) =>
            (sign.x < 0f ? 1 : 0) | (sign.y < 0f ? 2 : 0) | (sign.z < 0f ? 4 : 0);

        private static Vector3 SignOfFlipMask(int mask) =>
            new Vector3((mask & 1) != 0 ? -1f : 1f, (mask & 2) != 0 ? -1f : 1f, (mask & 4) != 0 ? -1f : 1f);

        /// BuildTangentBasis for the dab being applied, reflected along with it.
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
        /// coordinates. The primary dab (mask 0) is unchanged bit for bit.
        private void BuildDabTangentBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent) =>
            DabTangentBasis(normal, _dabFlipMask, out tangent, out bitangent);

        /// BuildDabTangentBasis for an explicit flip mask - what a batched dab program uses, since it
        /// runs after _dabFlipMask has moved on (see SculptController.DabProgram).
        private static void DabTangentBasis(Vector3 normal, int flipMask, out Vector3 tangent, out Vector3 bitangent)
        {
            if (flipMask == 0)
            {
                BuildTangentBasis(normal, out tangent, out bitangent);
                return;
            }

            Vector3 sign = SignOfFlipMask(flipMask);
            BuildTangentBasis(Vector3.Scale(normal, sign), out tangent, out bitangent);
            tangent = Vector3.Scale(tangent, sign);
            bitangent = Vector3.Scale(bitangent, sign);
        }

        // ------------------------------------------------------------ order-symmetric mirrored dabs

        /// A dab closer than this many of its own reach to a mirror plane is treated as able to see
        /// its mirror image's output. 1 would be exact for dabs that only read and write inside their
        /// reach; the rest covers Smooth reading one ring of neighbours past its footprint, and a
        /// dab's own displacement carrying a vertex over the boundary mid-frame.
        private const float MirrorInteractionMargin = 1.5f;

        /// Walks the mirror signs for one dab (see BeginMirroredDabs / NextMirroredDab).
        ///
        /// Far from every mirror plane this is exactly the old loop - each sign once, in MirrorSigns
        /// order. Where a plane runs through the footprint the order is not innocent: the signs are
        /// applied one after another against the live vertex array, so the second dab reads positions
        /// (fits its plane, measures "deepest carve so far", weighs by distance) through vertices the
        /// first one has just moved, while the first saw the untouched surface. The two halves were
        /// given different operations, always in the same order, so the difference did not average
        /// out - it accumulated stroke after stroke into the "one side slowly becomes different" drift.
        /// Measured on a bitwise-symmetric sphere a third of a radius off the plane: mirror error of
        /// 65% of the stroke's own displacement for Inflate, 12% for Clay (Accumulate), 8% for Dam
        /// Standard, with centreline vertices pushed well off the plane (SymmetryDriftTests).
        ///
        /// There, every ordering that the mirror group maps onto another is applied - each from the
        /// same starting surface, restored in between - and the results are averaged. Reflecting the
        /// model only permutes those orderings, so the average is mirror-symmetric by construction,
        /// while each ordering is still the existing brush behaviour (including the doubled strength a
        /// dab centred ON the plane has always had), so no brush changes character. Signs whose
        /// footprints cannot meet are grouped apart and never repeated: one X plane through the dab
        /// costs two applications of the pair instead of one, and only for dabs that near the plane.
        private struct MirroredDabWalk
        {
            public Vector3 Point;
            public float Reach;
            public bool Symmetric;
            public int Index, Count;              // plain walk over MirrorSigns
            public int Near, Far;                 // flip bits whose planes do / do not reach the footprint
            public int Group, GroupCount;         // current coset of the far flips
            public int Ordering, Step, Orderings; // orderings over the near flips, and position in one
        }

        private readonly List<int> _mirrorGroupVertices = new List<int>();

        private int[] _mirrorGroupStamp;

        private int _mirrorGroupGeneration;

        private Vector3[] _mirrorGroupBefore = Array.Empty<Vector3>();

        private Vector3[] _mirrorGroupDeltaSum = Array.Empty<Vector3>();

        /// True while a repeat ordering is being applied. Per-dab bookkeeping that is not geometry
        /// (Clay's relax centres) must be recorded once, on the first ordering only.
        private bool _mirrorRepeatOrdering;

        /// `reach` is the widest radius the brush's per-sign apply reads or writes vertices within.
        private MirroredDabWalk BeginMirroredDabs(Vector3 localPoint, float reach)
        {
            List<Vector3> signs = MirrorSigns();
            var walk = new MirroredDabWalk { Point = localPoint, Reach = reach, Index = -1, Count = signs.Count, Step = -1 };
            if (signs.Count <= 1) return walk;

            int active = 0;
            for (int k = 0; k < signs.Count; k++) active |= FlipMaskOf(signs[k]);
            float limit = reach * MirrorInteractionMargin;
            int near = 0;
            if ((active & 1) != 0 && Mathf.Abs(localPoint.x) < limit) near |= 1;
            if ((active & 2) != 0 && Mathf.Abs(localPoint.y) < limit) near |= 2;
            if ((active & 4) != 0 && Mathf.Abs(localPoint.z) < limit) near |= 4;
            if (near == 0) return walk;

            walk.Symmetric = true;
            walk.Near = near;
            walk.Far = active & ~near;
            walk.Orderings = 1 << BitCount(near);
            walk.GroupCount = 1 << BitCount(walk.Far);
            return walk;
        }

        /// Advances the walk and points the per-dab frame at the next sign (BeginMirroredDab).
        private bool NextMirroredDab(ref MirroredDabWalk walk, out Vector3 sign)
        {
            if (!walk.Symmetric)
            {
                _mirrorRepeatOrdering = false;
                if (++walk.Index >= walk.Count) { sign = Vector3.one; return false; }
                sign = MirrorSigns()[walk.Index];
                BeginMirroredDab(sign);
                return true;
            }

            if (walk.Step < 0)
            {
                walk.Step = 0;
                BeginMirrorGroup(ref walk);
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
                    CommitMirrorGroup(walk.Orderings);
                    walk.Ordering = 0;
                    if (++walk.Group == walk.GroupCount)
                    {
                        _mirrorRepeatOrdering = false;
                        sign = Vector3.one;
                        return false;
                    }
                    BeginMirrorGroup(ref walk);
                }
            }

            // Ordering o applies near-flips h_o ^ h_0, h_o ^ h_1, ... - one row of the group's own
            // table, which is what makes the SET of orderings map onto itself under any reflection.
            _mirrorRepeatOrdering = walk.Ordering > 0;
            sign = SignOfFlipMask(NthSubmask(walk.Far, walk.Group)
                                  ^ NthSubmask(walk.Near, walk.Ordering) ^ NthSubmask(walk.Near, walk.Step));
            BeginMirroredDab(sign);
            return true;
        }

        /// Snapshots every vertex the current group's dabs can write, before the first of them runs.
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

            int far = NthSubmask(walk.Far, walk.Group);
            for (int h = 0; h < walk.Orderings; h++)
            {
                Vector3 centre = Vector3.Scale(walk.Point, SignOfFlipMask(far ^ NthSubmask(walk.Near, h)));
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

        /// Writes the average of every ordering's displacement. Deltas rather than positions, so a
        /// vertex no ordering touched gets its own position back bit for bit.
        private void CommitMirrorGroup(int orderings)
        {
            if (_dabProgramRecording) { RecordDabOp(DabOpKind.Commit, 1f / orderings); return; }
            Vector3[] verts = sculptableMesh.Vertices;
            float inv = 1f / orderings; // a power of two, so exact
            for (int u = 0; u < _mirrorGroupVertices.Count; u++)
                verts[_mirrorGroupVertices[u]] = _mirrorGroupBefore[u] + _mirrorGroupDeltaSum[u] * inv;
            MarkPositionMirrorStale();
        }

        private static int BitCount(int value)
        {
            int count = 0;
            for (; value != 0; value &= value - 1) count++;
            return count;
        }

        /// The k-th subset of `mask`'s bits, counting in binary over those bits (k's bit 0 selects
        /// the lowest set bit of mask, and so on). Gives every group a fixed enumeration.
        private static int NthSubmask(int mask, int k)
        {
            int result = 0, bit = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                if ((mask & (1 << axis)) == 0) continue;
                if ((k & (1 << bit)) != 0) result |= 1 << axis;
                bit++;
            }
            return result;
        }
    }
}
