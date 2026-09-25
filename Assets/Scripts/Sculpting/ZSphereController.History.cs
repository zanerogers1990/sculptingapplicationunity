using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Rig-local undo, and attaching the rig to an existing object.
    public partial class ZSphereController
    {
        private const int MaxRigUndoSteps = 128;

        /// How long a coalescing edit (a run of wheel notches, a slider being dragged) stays open
        /// before it closes as ONE undo step.
        private const float CoalesceSeconds = 0.4f;

        // ------------------------------------------------------------------------ rig undo

        /// The rig keeps its own undo stack, separate from EditHistory, on purpose. EditHistory's
        /// steps are about scene OBJECTS; the rig is a scaffold that produces one at Convert (which
        /// IS an EditHistory step). Forty sphere drags on the shared stack would bury the mesh edits
        /// either side of them. Z answers here while the tool is up and has something to undo, and
        /// falls through to the scene's history otherwise - see HandlesUndoKey.
        private struct RigUndoStep
        {
            public ZSphereRig.Node[] Rig;
            public int Selection;
            /// Symmetry is part of the rig's shape - a derived reflection appears and disappears
            /// with it - so it is restored with the nodes. Undoing "Symmetry Off" must bring back
            /// the mirrored rig, not a one-sided one.
            public bool Symmetry;
            public string Label;
        }

        private readonly List<RigUndoStep> _rigUndo = new List<RigUndoStep>();
        private readonly List<RigUndoStep> _rigRedo = new List<RigUndoStep>();

        private ZSphereRig.Node[] _pendingSnapshot;
        private int _pendingSelection;
        private int _pendingVersion;
        private bool _pendingSymmetry;
        private string _pendingLabel;
        private float _pendingCommitAt = float.MaxValue;

        public bool CanUndoRig => _rigUndo.Count > 0 || HasOpenEdit;
        public bool CanRedoRig => _rigRedo.Count > 0;
        public int RigUndoDepth => _rigUndo.Count + (HasOpenEdit ? 1 : 0);

        public string NextRigUndoLabel =>
            HasOpenEdit ? _pendingLabel :
            _rigUndo.Count > 0 ? _rigUndo[_rigUndo.Count - 1].Label : null;

        /// Snapshotted AND actually changed, but not closed yet - a drag in flight, or a resize in
        /// its coalescing window. From the artist's point of view it has already happened.
        private bool HasOpenEdit =>
            _pendingSnapshot != null && (_rig.Version != _pendingVersion || _symmetryX != _pendingSymmetry);

        /// Whether this tool, not the scene-wide history, answers a Z press - and if so, answers
        /// it. Called only by SculptController.HandleUndoRedoKeys, the one place Z is read, so
        /// exactly one history responds to each press. (This tool used to read Z itself as well,
        /// which made the answer depend on which component's Update ran first: popping the last
        /// rig step first left CanUndoRig false by the time SculptController asked, and the same
        /// press then undid a scene step too.) Same perform-and-report shape as
        /// LatheController.HandlesUndoKey.
        public bool HandlesUndoKey(bool redo)
        {
            if (Gizmo == null || Gizmo.Mode != GizmoMode.ZSphere) return false;
            if (!(redo ? CanRedoRig : CanUndoRig)) return false;
            // Mid-drag the press is swallowed, not acted on, as it always was: stepping the rig
            // back under a live drag would leave the drag holding start state for nodes that moved.
            if (_drag != DragKind.None) return true;
            if (redo) RedoRig(); else UndoRig();
            return true;
        }

        /// Opens an undo step: snapshots now, pushed by CommitRigEdit only if the rig actually
        /// changed in between. Lazy commit is what makes it safe to open on every press - most
        /// presses are selection clicks. Re-entrant: a nested call extends the open step.
        private void BeginRigEdit(string label, float coalesceSeconds = 0f)
        {
            if (_pendingSnapshot != null)
            {
                // A streamed edit (wheel, slider) left open must not absorb a discrete one that
                // follows inside its window: the drag would share the resize's snapshot, and
                // cancelling the drag with Esc would revert the resize too.
                bool openIsStreamed = _pendingCommitAt != float.MaxValue;
                if (!openIsStreamed || coalesceSeconds > 0f)
                {
                    if (coalesceSeconds > 0f) _pendingCommitAt = Time.unscaledTime + coalesceSeconds;
                    return;
                }
                CommitRigEdit();
            }

            _pendingSnapshot = _rig.Snapshot();
            _pendingSelection = SelectedNode;
            _pendingVersion = _rig.Version;
            _pendingSymmetry = _symmetryX;
            _pendingLabel = label;
            _pendingCommitAt = coalesceSeconds > 0f ? Time.unscaledTime + coalesceSeconds : float.MaxValue;
        }

        private void CommitRigEdit()
        {
            if (_pendingSnapshot == null) return;

            if (HasOpenEdit)
            {
                _rigUndo.Add(new RigUndoStep
                {
                    Rig = _pendingSnapshot,
                    Selection = _pendingSelection,
                    Symmetry = _pendingSymmetry,
                    Label = _pendingLabel
                });
                while (_rigUndo.Count > MaxRigUndoSteps) _rigUndo.RemoveAt(0);
                _rigRedo.Clear();
            }

            _pendingSnapshot = null;
            _pendingCommitAt = float.MaxValue;
        }

        /// Throws the open step away AND puts the rig back to its snapshot - Esc mid-drag.
        private void CancelRigEdit()
        {
            if (_pendingSnapshot == null) return;
            if (HasOpenEdit)
            {
                _rig.Restore(_pendingSnapshot);
                _symmetryX = _pendingSymmetry;
                SelectedNode = _rig.IsAlive(_pendingSelection) ? _pendingSelection : NoNode;
            }
            _pendingSnapshot = null;
            _pendingCommitAt = float.MaxValue;
        }

        /// Closes a coalescing edit whose window has run out - but never one belonging to a drag,
        /// which can easily sit still longer than the window.
        private void TickPendingRigEdit()
        {
            if (_pendingSnapshot == null || _drag != DragKind.None) return;
            if (Time.unscaledTime >= _pendingCommitAt) CommitRigEdit();
        }

        public bool UndoRig()
        {
            // The operation just finished is what gets reversed, not the one before it.
            if (_drag != DragKind.None) EndDrag();
            CommitRigEdit();
            return TakeRigStep(_rigUndo, _rigRedo);
        }

        public bool RedoRig()
        {
            if (_drag != DragKind.None) EndDrag();
            // An open edit is newer than anything on the redo stack, and committing it clears that
            // stack - which is right: redo after a fresh edit has nothing left to mean.
            CommitRigEdit();
            return TakeRigStep(_rigRedo, _rigUndo);
        }

        private bool TakeRigStep(List<RigUndoStep> from, List<RigUndoStep> to)
        {
            if (from.Count == 0) return false;

            int last = from.Count - 1;
            RigUndoStep step = from[last];
            from.RemoveAt(last);

            to.Add(new RigUndoStep
            {
                Rig = _rig.Snapshot(),
                Selection = SelectedNode,
                Symmetry = _symmetryX,
                Label = step.Label
            });

            _rig.Restore(step.Rig);
            _symmetryX = step.Symmetry;
            // Indices survive a Restore (tombstoned, never compacted), but the stored selection may
            // be a sphere that only exists in the other direction.
            SelectedNode = _rig.IsAlive(step.Selection) ? step.Selection : NoNode;
            HoveredNode = NoNode;
            return true;
        }

        // ------------------------------------------------------------------------ attaching

        private SculptableMesh _attachTarget;
        private Vector3 _attachLastPos;
        private Quaternion _attachLastRot = Quaternion.identity;

        public SculptableMesh AttachTarget => _attachTarget;
        public string AttachTargetName => _attachTarget != null ? _attachTarget.name : null;

        /// Binds the rig to an existing object so limbs can be blocked out ON a body: clicks land on
        /// its surface, each click on it starts another limb root, the rig follows it around, and
        /// the mirror plane is its plane. Convert still produces a separate mesh.
        public bool AttachToObject(SculptableMesh target)
        {
            if (target == null) return false;

            _attachTarget = target;
            EnsureRigRoot();

            if (_rig.IsEmpty)
            {
                _rigRoot.SetPositionAndRotation(target.transform.position, target.transform.rotation);
                SyncAttachReference();
            }
            else
            {
                ReanchorSymmetryPlane();
            }
            return true;
        }

        public void DetachFromObject() => _attachTarget = null;

        private void SyncAttachReference()
        {
            if (_attachTarget == null) return;
            _attachLastPos = _attachTarget.transform.position;
            _attachLastRot = _attachTarget.transform.rotation;
        }

        /// Carries the rig along when its body moves or turns - a rigid delta on the rig root, not
        /// parenting. Parenting would put the body's scale into rig space, where every radius and
        /// the skinner's voxel bounds assume unit scale.
        private void FollowAttachTarget()
        {
            if (_attachTarget == null || _rigRoot == null) return;

            Transform target = _attachTarget.transform;
            if (target.position == _attachLastPos && target.rotation == _attachLastRot) return;

            Quaternion delta = target.rotation * Quaternion.Inverse(_attachLastRot);
            _rigRoot.SetPositionAndRotation(
                target.position + delta * (_rigRoot.position - _attachLastPos),
                delta * _rigRoot.rotation);

            SyncAttachReference();
        }

        /// Where `ray` meets the attach target's surface. False with nothing attached or surface
        /// snapping off, which is what lets every caller fall back to the view plane unbranched.
        /// The sphere centre goes ON the surface, half-buried, so the skin's smooth union fuses the
        /// limb into the body rather than balancing it on top.
        private bool TrySurfacePoint(Ray ray, out Vector3 worldPoint)
        {
            worldPoint = default;
            if (_attachTarget == null || !SnapToSurface) return false;

            // The mesh's own raycast, not its MeshCollider: the collider is deliberately never
            // re-cooked while sculpting (see SculptableMesh.useMeshCollider's remarks), so it has the
            // pre-sculpt shape and spheres snapped to a surface that was no longer there. This is
            // the same live test the brushes use - current vertices, hidden regions skipped, and
            // the transform read directly, so a gizmo move this frame needs no physics sync.
            return _attachTarget.RaycastMesh(ray, 10000f, out worldPoint, out _);
        }
    }
}
