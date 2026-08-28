namespace Sculpting
{
    /// What a click/drag on the ZSphere rig does while GizmoMode.ZSphere is active - see
    /// ZSphereController, which owns the mouse in that mode the same way TransformGizmo owns
    /// it in Transpose/Scale.
    ///
    /// Add is the mode the whole workflow is built around (click a sphere to select it, drag
    /// off one to grow the next); the other four exist because every one of them would
    /// otherwise have to overload that same drag with a modifier key, and a blockout session
    /// spends long stretches doing only one of them.
    public enum ZSphereEditMode
    {
        /// Click empty space to place the root; drag off an existing sphere to grow a child.
        Add,
        /// Drag a sphere to reposition just that sphere, leaving its children where they are.
        Move,
        /// Drag left/right on a sphere to scrub its radius.
        Scale,
        /// Drag a sphere to swing it (and everything below it) about its parent joint,
        /// preserving bone length - the spheres-are-bones posing pass.
        Pose,
        /// Click a sphere to delete it and everything below it.
        Delete
    }
}
