using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// A live mirror pair: two sculptable objects kept exact mirror images of each other across a
    /// world-space plane until the user finalizes them - Nomad's mirror, or Blender's Mirror
    /// modifier, made by SceneGraphUIBuilder's "Mirror Linked" button (see MeshMirror). The point is
    /// placing paired features - eyes, ears - by moving either one and watching the other answer,
    /// rather than positioning one and mirroring it afterwards.
    ///
    /// Both halves stay ordinary SculptableMeshes - selectable, sculptable, saved like anything
    /// else. This component sits on the original (Source), and both meshes point back at it through
    /// SculptableMesh.LinkedMirror. What is kept in step:
    /// - Transforms, every LateUpdate: whichever half moved drives the other (see SyncTransforms).
    /// - Vertices, as each edit is applied: brushes, masked Transpose, Reset, Make Symmetric, and
    ///   undo/redo of any of those. Local vertex i of one half is always the reflection of local
    ///   vertex i of the other, so following an edit is a copy of positions, never a replay of the
    ///   brush.
    ///
    /// Not kept in step: mask, hidden geometry and symmetry settings, which are working state rather
    /// than shape. And a TOPOLOGY change - Remesh, Trim, Boolean, Join, Cut & Mirror, or an undo
    /// that crosses one - finalizes the link instead of rebuilding the other half. That is for
    /// undo's sake: history names vertices by index, and a rebuild the other half never took a
    /// snapshot of would leave its older entries pointing into a vertex set it no longer has, the
    /// moment the two stopped being linked.
    public class MirrorLink : MonoBehaviour
    {
        [SerializeField] private SculptableMesh source;
        [SerializeField] private SculptableMesh twin;
        // A world point every mirror plane passes through, and -1 on each mirrored axis (see
        // MeshMirror.AxisSigns). Serialized so a mid-Play script reload keeps the pair.
        [SerializeField] private Vector3 center;
        [SerializeField] private Vector3 signs = Vector3.one;

        private bool _unlinked;
        // True while this link is writing into one half. That half's apply path reports back here
        // like any other edit, and must not bounce the copy straight back.
        private bool _applying;

        // Both transforms as this link last left them - how SyncTransforms tells which half moved.
        // Not serialized: after a reload the first sync just treats Source as the one that moved,
        // which re-derives a twin that was already in place.
        private TransformState _sourceState, _twinState;
        private bool _statesValid;

        private SelectionManager _selection;
        private SelectionManager Selection => _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());

        public SculptableMesh Source => source;
        public SculptableMesh Twin => twin;
        public Vector3 Center => center;
        public Vector3 Signs => signs;

        /// The other half of the pair, or null if `half` is not in it.
        public SculptableMesh PartnerOf(SculptableMesh half)
        {
            if (half == null) return null;
            if (half == source) return twin;
            return half == twin ? source : null;
        }

        /// Links two objects that are ALREADY mirror images of each other across `center`, on the
        /// axes `mirrorSigns` negates - MeshMirror builds such a pair, SceneSerializer restores one.
        /// Returns null if either object already belongs to a pair: a third object could only stay
        /// consistent through a chain of links, and "which one follows" would stop having an answer.
        public static MirrorLink Create(SculptableMesh source, SculptableMesh twin, Vector3 center, Vector3 mirrorSigns)
        {
            if (source == null || twin == null || source == twin) return null;
            if (source.LinkedMirror != null || twin.LinkedMirror != null) return null;

            var normalized = new Vector3(mirrorSigns.x < 0f ? -1f : 1f, mirrorSigns.y < 0f ? -1f : 1f,
                                         mirrorSigns.z < 0f ? -1f : 1f);
            if (normalized == Vector3.one) return null;

            var link = source.gameObject.AddComponent<MirrorLink>();
            link.source = source;
            link.twin = twin;
            link.center = center;
            link.signs = normalized;
            // Explicitly rather than from OnEnable: that already ran inside AddComponent, before any
            // field above was set - and Edit mode, where the tests run, never calls it at all.
            link.Hook();
            link.CaptureStates();
            return link;
        }

        /// Ends the link, leaving both halves exactly as and where they are - two unrelated objects
        /// from here on.
        public void Unlink()
        {
            if (_unlinked) return;
            _unlinked = true;
            Unhook();
            // Destroy is refused outside Play mode, which is where the tests drive links directly.
            if (Application.isPlaying) Destroy(this);
            else DestroyImmediate(this);
        }

        // Also what re-points both meshes after a mid-Play script reload, which clears their
        // non-serialized back-references but keeps this component's serialized fields.
        private void OnEnable() => Hook();

        // OnDestroy, not OnDisable. Deleting an object from the Scene panel only PARKS it -
        // deactivated, so Z can bring it back (see SelectionManager.DeleteObject) - and a pair that
        // stays hooked through that keeps receiving edits, so an undone delete returns both halves
        // still exact mirror images. A parked Source only pauses transform syncing until it is
        // back; the link really ends once either half is destroyed.
        private void OnDestroy() => Unhook();

        private void Hook()
        {
            if (_unlinked || source == null || twin == null) return;
            source.LinkedMirror = this;
            twin.LinkedMirror = this;
            // The Scene panel redraws on SelectionVersion, and it tints linked rows and enables
            // Finalize from what it finds.
            Selection?.NotifyChanged();
        }

        private void Unhook()
        {
            bool changed = false;
            if (source != null && source.LinkedMirror == this) { source.LinkedMirror = null; changed = true; }
            if (twin != null && twin.LinkedMirror == this) { twin.LinkedMirror = null; changed = true; }
            if (changed) Selection?.NotifyChanged();
        }

        private void LateUpdate()
        {
            if (_unlinked) return;
            // Unity's == reports a destroyed object as null: Join consuming one half, or a deleted
            // half finally freed as its undo step leaves history. Either way there is no pair left.
            if (source == null || twin == null) { Unlink(); return; }
            SyncTransforms();
        }

        // ------------------------------------------------------------------ transform following

        /// Brings the half that did not move into line with the one that did. Runs from LateUpdate,
        /// after every Update that can move an object - a gizmo drag, an undo - so the follower is
        /// placed in the same frame, before anything renders.
        ///
        /// When BOTH moved - the halves dragged together as one multi-selection, or an undo
        /// restoring a step that recorded both - the plane is re-centred between them first. Moving
        /// the pair together is what that gesture means, and a fixed plane would instead push the
        /// follower the opposite way. The primary selection then drives, being the half the user is
        /// working through.
        public void SyncTransforms()
        {
            if (_unlinked || source == null || twin == null) return;
            Transform a = source.transform, b = twin.transform;

            bool sourceMoved = !_statesValid || !_sourceState.Matches(a);
            bool twinMoved = _statesValid && !_twinState.Matches(b);
            if (!sourceMoved && !twinMoved) return;

            Transform driver = sourceMoved ? a : b;
            if (sourceMoved && twinMoved)
            {
                center = CenterBetween(a.position, b.position);
                SelectionManager selection = Selection;
                if (selection != null && selection.PrimarySelection == twin) driver = b;
            }
            Transform follower = driver == a ? b : a;

            follower.SetPositionAndRotation(MeshMirror.ReflectPoint(driver.position, center, signs),
                                            MeshMirror.ReflectRotation(driver.rotation, signs));
            follower.localScale = driver.localScale;
            CaptureStates();
        }

        // Only along the mirrored axes - a plane has no position along the axes it runs parallel to.
        private Vector3 CenterBetween(Vector3 p, Vector3 q)
        {
            Vector3 c = center;
            if (signs.x < 0f) c.x = (p.x + q.x) * 0.5f;
            if (signs.y < 0f) c.y = (p.y + q.y) * 0.5f;
            if (signs.z < 0f) c.z = (p.z + q.z) * 0.5f;
            return c;
        }

        private void CaptureStates()
        {
            _sourceState = new TransformState(source.transform);
            _twinState = new TransformState(twin.transform);
            _statesValid = true;
        }

        private readonly struct TransformState
        {
            private readonly Vector3 _position;
            private readonly Quaternion _rotation;
            private readonly Vector3 _scale;

            public TransformState(Transform t)
            {
                _position = t.position;
                _rotation = t.rotation;
                _scale = t.localScale;
            }

            // Exact, deliberately not Vector3/Quaternion ==. Both of those are approximate, and the
            // quaternion one ignores differences under about 0.16 degrees, so a slow rotate would
            // leave the follower catching up in visible steps.
            public bool Matches(Transform t) =>
                _position.Equals(t.position) && _rotation.Equals(t.rotation) && _scale.Equals(t.localScale);
        }

        // -------------------------------------------------------------------- vertex following

        /// One half is applying `dirty` through ApplyVerticesLocal. Copies those positions,
        /// reflected, into the other half and applies them there the same way. Called BEFORE the
        /// reporting half's drift filter trims the list (see SculptableMesh.ApplyDirtyVertexList),
        /// so the other half is handed every reported position, not just the visible ones.
        internal void OnVerticesApplied(SculptableMesh changed, List<int> dirty)
        {
            if (!TryBeginFollow(changed, out SculptableMesh follower, out Vector3[] from, out Vector3[] to)) return;
            try
            {
                for (int k = 0; k < dirty.Count; k++)
                {
                    int i = dirty[k];
                    to[i] = Vector3.Scale(from[i], signs);
                }
                follower.ApplyVerticesLocal(dirty);
            }
            finally { _applying = false; }
        }

        /// The whole-mesh counterpart, from ApplyVertices: masked Transpose, Reset, Make Symmetric
        /// and whole-snapshot undo steps.
        internal void OnAllVerticesApplied(SculptableMesh changed, bool fullRebuild)
        {
            if (!TryBeginFollow(changed, out SculptableMesh follower, out Vector3[] from, out Vector3[] to)) return;
            try
            {
                for (int i = 0, n = changed.VertexCount; i < n; i++) to[i] = Vector3.Scale(from[i], signs);
                follower.ApplyVertices(fullRebuild);
            }
            finally { _applying = false; }
        }

        /// A stroke on one half has ended. Brushes don't report moves under the drift threshold at
        /// all (see SculptableMesh.HasVisiblyDrifted), so those never came through
        /// OnVerticesApplied. This copies everything the stroke touched, once, and refreshes the
        /// other half's normals and curvature over it the way the stroked half just did - so the
        /// next stroke finds both halves in the same state whichever one it lands on.
        internal void OnStrokeEnded(SculptableMesh changed, List<int> touched)
        {
            if (touched.Count == 0) return;
            if (!TryBeginFollow(changed, out SculptableMesh follower, out Vector3[] from, out Vector3[] to)) return;
            try
            {
                for (int k = 0; k < touched.Count; k++)
                {
                    int i = touched[k];
                    to[i] = Vector3.Scale(from[i], signs);
                }
                follower.ApplyVerticesLocal(touched);
                follower.RefreshNormalsAndCurvature(touched);
            }
            finally { _applying = false; }
        }

        /// One half's vertex set was just rebuilt - see the class remarks for why that finalizes
        /// the pair rather than rebuilding the other half to match.
        internal void OnTopologyChanged(SculptableMesh changed)
        {
            if (_unlinked) return;
            Unlink();
            FindFirstObjectByType<SculptController>()?.TriggerActionToast("Mirror Link Finalized");
        }

        private bool TryBeginFollow(SculptableMesh changed, out SculptableMesh follower, out Vector3[] from, out Vector3[] to)
        {
            follower = null;
            from = to = null;
            if (_applying || _unlinked) return false;

            follower = PartnerOf(changed);
            if (follower == null) return false;
            from = changed.Vertices;
            to = follower.Vertices;
            if (from == null || to == null) return false;
            // Only reachable if something rebuilt one half without reporting it. Copying by index
            // into a different vertex set would scramble it, so the pair ends instead. Compared by
            // VertexCount rather than by the buffers' lengths, which are capacities that the two
            // halves can reach at different moments (see SculptableMesh.Vertices) - though in
            // practice neither half ever grows one, since dynamic topology stays suspended for as
            // long as a pair is linked.
            if (changed.VertexCount != follower.VertexCount) { Unlink(); return false; }

            _applying = true;
            return true;
        }
    }
}
