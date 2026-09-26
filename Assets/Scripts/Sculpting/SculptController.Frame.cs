using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// The brush frame: which transform the brush maps world space into the target's local space
    /// through. That is the target's own transform, unless the cursor is over one of its live
    /// mirror copies (see MirrorRepeater) - a copy draws the very same mesh through a reflected
    /// transform, so sculpting through the copy's transform edits exactly the vertices the copy
    /// shows under the cursor, and the original follows by construction.
    ///
    /// Chosen per hover, then LOCKED for as long as a button is held: the copy and the original
    /// meet at the mirror plane, and a stroke that crossed it must not flip frames mid-stroke,
    /// where the same local point would suddenly be addressed from the other side.
    public partial class SculptController
    {
        private Transform _brushFrame;
        private bool _brushFrameLocked;

        /// The transform every brush maps world <-> local through. Unity's null check covers a
        /// copy destroyed mid-hover (its mirror removed), falling back to the object itself.
        private Transform Frame => _brushFrame != null ? _brushFrame : sculptableMesh.transform;

        /// The brush raycast: this object and every live mirror copy of it while hovering, only the
        /// locked frame while a stroke is held.
        private bool RaycastTarget(Ray ray, out Vector3 point, out Vector3 normal)
        {
            if (_brushFrameLocked) return sculptableMesh.RaycastMesh(Frame, ray, 1000f, out point, out normal);

            if (!sculptableMesh.RaycastAnyCopy(ray, 1000f, out point, out normal, out Transform frame)) return false;
            _brushFrame = frame == sculptableMesh.transform ? null : frame;
            Mouse mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed)) _brushFrameLocked = true;
            return true;
        }

        /// Once per frame before any brush runs: a released stroke frees the frame again.
        private void ReleaseBrushFrameIfIdle()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || (!mouse.leftButton.isPressed && !mouse.rightButton.isPressed)) _brushFrameLocked = false;
        }

        private void ResetBrushFrame()
        {
            _brushFrame = null;
            _brushFrameLocked = false;
        }

        private Vector3 WorldToLocalNormal(Vector3 worldNormal) => SculptableMesh.WorldToLocalNormal(Frame, worldNormal);
    }
}
