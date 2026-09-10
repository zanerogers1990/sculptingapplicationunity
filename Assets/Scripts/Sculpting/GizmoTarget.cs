using UnityEngine;

namespace Sculpting
{
    /// One thing a TransformGizmo drag moves.
    ///
    /// The gizmo used to drive the primary SculptableMesh's Transform directly, which works right
    /// up until something that is not a whole sculptable GameObject wants the same handles. A
    /// ZSphere rig node is an index into an array of rig-LOCAL positions with no GameObject at
    /// all; a scene light is a Transform with no mesh to size the gizmo against. Both want exactly
    /// the drag behaviour the gizmo already has, so it now drives a LIST of these and never learns
    /// what any of them actually is.
    ///
    /// Instances are built once per selection change (not per frame), so the indirection costs
    /// nothing on any hot path.
    public abstract class GizmoTarget
    {
        /// False once whatever this points at is gone - a deleted object, a rig node removed by an
        /// undo. The gizmo drops dead targets rather than dereferencing them: a selection easily
        /// outlives what it names, and an undo pressed mid-session is the simplest way to get
        /// there.
        public abstract bool IsAlive { get; }

        public abstract Vector3 Position { get; set; }

        /// Identity for a target with no meaningful orientation of its own (a rig node is a
        /// point). Rotating a SET of those still swings them around the shared pivot, which is the
        /// part that matters - see TransformGizmo.ApplyDragToTargets.
        public virtual Quaternion Rotation { get => Quaternion.identity; set { } }
        public virtual bool SupportsRotation => false;

        public virtual Vector3 LocalScale { get => Vector3.one; set { } }
        public virtual bool SupportsScale => false;

        /// Roughly how big this target is in world units. The gizmo sizes its arms from this so
        /// the handles stay grabbable against a pea and against a whole torso alike.
        public abstract float WorldRadius { get; }

        /// True when `other` names the same underlying thing. Lets a selection be deduped without
        /// the gizmo knowing what kind of thing it is holding, and lets a rebuilt target list be
        /// compared against the live one to decide whether a drag has been invalidated.
        public abstract bool SameAs(GizmoTarget other);
    }

    /// The ordinary case: a target backed by a real Transform.
    public sealed class TransformGizmoTarget : GizmoTarget
    {
        private readonly Transform _transform;
        // Explicit size for a Transform with nothing to measure - a Light has no Renderer, so
        // without this every light would size its gizmo off the fallback minimum and end up with
        // handles too small to grab. Zero means "measure it" (the Renderer path below).
        private readonly float _radiusOverride;

        public Transform Transform => _transform;

        public TransformGizmoTarget(Transform transform, float radiusOverride = 0f)
        {
            _transform = transform;
            _radiusOverride = radiusOverride;
        }

        // Unity's overloaded == reports a destroyed object as null, which is exactly the test
        // wanted here: the GameObject can be deleted out from under a live selection.
        public override bool IsAlive => _transform != null;

        public override Vector3 Position
        {
            get => _transform.position;
            set => _transform.position = value;
        }

        public override Quaternion Rotation
        {
            get => _transform.rotation;
            set => _transform.rotation = value;
        }

        public override bool SupportsRotation => true;

        public override Vector3 LocalScale
        {
            get => _transform.localScale;
            set => _transform.localScale = value;
        }

        public override bool SupportsScale => true;

        public override float WorldRadius
        {
            get
            {
                if (_radiusOverride > 0f) return _radiusOverride;

                // Renderer bounds rather than a MeshFilter's mesh bounds: this has to work for a
                // sculpt, an imported model and anything else that ends up selectable, and the
                // renderer's world-space bounds already fold in the whole parent scale chain.
                var renderer = _transform.GetComponentInChildren<Renderer>();
                if (renderer == null) return 0f;
                Vector3 e = renderer.bounds.extents;
                return (e.x + e.y + e.z) / 3f;
            }
        }

        public override bool SameAs(GizmoTarget other) =>
            other is TransformGizmoTarget t && t._transform == _transform;
    }

    /// One ZSphere rig node, addressed by index.
    ///
    /// A rig node has no GameObject of its own - the spheres on screen are drawn from the rig each
    /// frame - so this reads and writes through the controller instead, converting between the
    /// gizmo's world space and the rig's own local space on the way. Writes route through
    /// SetPositionSymmetric rather than the raw setter so dragging a node still carries its mirror
    /// twin with it, exactly as the free-drag path does.
    public sealed class ZSphereNodeTarget : GizmoTarget
    {
        private readonly ZSphereController _controller;
        private readonly int _nodeIndex;

        public int NodeIndex => _nodeIndex;

        public ZSphereNodeTarget(ZSphereController controller, int nodeIndex)
        {
            _controller = controller;
            _nodeIndex = nodeIndex;
        }

        public override bool IsAlive => _controller != null && _controller.Rig.Get(_nodeIndex) != null;

        public override Vector3 Position
        {
            get
            {
                ZSphereRig.Node node = _controller != null ? _controller.Rig.Get(_nodeIndex) : null;
                return node != null ? _controller.RigPointToWorld(node.Position) : Vector3.zero;
            }
            set
            {
                if (_controller == null) return;
                _controller.MoveNodeFromGizmo(_nodeIndex, value);
            }
        }

        // A sphere is rotationally symmetric and its radius is the Scale edit mode's job, so the
        // gizmo shows neither - see ZSphereController's GizmoHandleSet.Move push.
        public override bool SupportsRotation => false;
        public override bool SupportsScale => false;

        public override float WorldRadius
        {
            get
            {
                ZSphereRig.Node node = _controller != null ? _controller.Rig.Get(_nodeIndex) : null;
                return node != null ? _controller.RigRadiusToWorld(node.Radius) : 0f;
            }
        }

        public override bool SameAs(GizmoTarget other) =>
            other is ZSphereNodeTarget z && z._controller == _controller && z._nodeIndex == _nodeIndex;
    }
}
