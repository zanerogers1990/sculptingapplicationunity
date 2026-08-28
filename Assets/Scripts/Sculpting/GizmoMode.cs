namespace Sculpting
{
    /// Which whole-object tool is active. Sculpt means no tool is up and SculptController's
    /// brushes get mouse input as normal; every other value hides the brushes and hands mouse
    /// input to that tool instead (see SculptController.HandleSculptInput's early-out, which
    /// keys off `!= Sculpt` rather than off any specific mode, so it already covers all of them).
    ///
    /// Transpose and Scale are TransformGizmo's; ZSphere is ZSphereController's. They share this
    /// one enum rather than each owning a flag because they are mutually exclusive by nature -
    /// they all want the same click - and one enum is what makes that impossible to get wrong.
    public enum GizmoMode
    {
        Sculpt,
        Transpose,
        Scale,
        /// Blockout mode: TransformGizmo hides itself and ZSphereController owns the mouse for
        /// placing, posing and skinning a ZSphere rig.
        ZSphere
    }
}
