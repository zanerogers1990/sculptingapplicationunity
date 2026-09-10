namespace Sculpting
{
    /// What a left-drag on the armature does while GizmoMode.ZSphere is active. The same four
    /// ZBrush offers, and deliberately no more: every gesture that works in ALL modes (wheel to
    /// resize, right-click to delete, click to select) lives outside this enum, so switching mode
    /// only ever changes what a drag does - never what a click does.
    public enum ZSphereEditMode
    {
        /// Drag off a sphere to grow a child that follows the cursor; drag a link to insert a
        /// sphere there and place it in the same motion; click empty space to start a rig.
        Draw,
        /// Drag a sphere to move it together with everything below it, so a chain keeps its
        /// shape. Shift moves just that one sphere.
        Move,
        /// Drag to resize a sphere. Shift resizes its whole branch together.
        Scale,
        /// Swing a sphere and everything below it about its parent joint, keeping bone lengths.
        Rotate
    }
}
