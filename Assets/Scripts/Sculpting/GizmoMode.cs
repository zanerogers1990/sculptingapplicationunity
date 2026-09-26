namespace Sculpting
{
    /// Which whole-object tool is active. Sculpt means no tool is up and SculptController's
    /// brushes get mouse input as normal; every other value hides the brushes and hands mouse
    /// input to that tool instead (see SculptController.HandleSculptInput's early-out, which
    /// keys off `!= Sculpt` rather than off any specific mode, so it already covers all of them).
    ///
    /// Transpose and Scale are TransformGizmo's; SSphere is SSphereController's. They share this
    /// one enum rather than each owning a flag because they are mutually exclusive by nature -
    /// they all want the same click - and one enum is what makes that impossible to get wrong.
    public enum GizmoMode
    {
        Sculpt,
        Transpose,
        Scale,
        /// Blockout mode: TransformGizmo hides itself and SSphereController owns the mouse for
        /// placing, posing and skinning an SSphere rig.
        SSphere,
        /// Mold mode: MoldController owns the mouse for placing pins, sprues and vents on the
        /// parting surface, and pushes its own targets to the gizmo for dragging them (and the
        /// parting surface itself) around.
        Mold,
        /// Lathe mode: LatheController owns the mouse for shaping a profile curve that is revolved
        /// into a solid, live, until Create bakes it into a sculptable object.
        Lathe
    }
}
