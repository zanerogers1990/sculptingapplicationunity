namespace Sculpting
{
    /// Which whole-object tool is active - see TransformGizmo. Sculpt means the gizmo is
    /// hidden and SculptController's brushes get mouse input as normal; Transpose/Scale hide
    /// the brushes and hand mouse input to the gizmo instead (see
    /// SculptController.HandleSculptInput's early-out).
    public enum GizmoMode
    {
        Sculpt,
        Transpose,
        Scale
    }
}
