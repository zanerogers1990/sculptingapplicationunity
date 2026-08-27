using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Alt+left-drag orbits the camera around a pivot (mirrors the navigation scheme of most
    /// sculpting apps); middle-drag pans; scroll zooms, unless the cursor is over the
    /// sculptable surface, where SculptController takes the wheel to resize the brush instead.
    /// Ctrl+Alt+left-drag zooms too (drag-based alternative for stylus/trackpad input). Right
    /// mouse is not used here - it's reserved by SculptController for inverted sculpting.
    /// SculptController checks the same Alt/Ctrl state so these combos orbit/zoom instead of
    /// sculpting.
    public class CameraOrbitController : MonoBehaviour
    {
        public Transform target;
        [SerializeField] private float orbitSpeed = 0.25f;
        [SerializeField] private float panSpeed = 0.01f;
        // Fraction of the current distance covered per wheel notch. Using a percentage rather
        // than a fixed step keeps zoom speed consistent near and far, and - crucially - makes it
        // independent of how large a "notch" is reported as (raw wheel delta units vary a lot
        // across platforms/Input System versions), which is what made zoom feel too slow before.
        [SerializeField, Range(0.01f, 0.5f)] private float zoomPercentPerNotch = 0.18f;
        // How much Ctrl+Alt+left-drag zooms per pixel of vertical mouse movement, as a
        // percentage of the current distance (same feel as zoomPercentPerNotch, just driven by
        // drag distance instead of wheel notches) - a drag-based alternative for stylus/trackpad
        // input where a precise scroll wheel isn't available. Dragging up zooms in, matching
        // Maya's Alt+Ctrl dolly gesture.
        [SerializeField, Range(0.0005f, 0.02f)] private float dragZoomSensitivity = 0.004f;
        [SerializeField] private float minDistance = 0.5f;
        [SerializeField] private float maxDistance = 20f;

        private float _yaw;
        private float _pitch;
        private float _distance;
        private Vector3 _pivot;

        private void Start()
        {
            if (target == null)
            {
                var sculptable = FindFirstObjectByType<SculptableMesh>();
                if (sculptable != null) target = sculptable.transform;
            }

            _pivot = target != null ? target.position : Vector3.zero;
            _distance = Vector3.Distance(transform.position, _pivot);

            Vector3 angles = transform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x;

            UpdateTransform();
        }

        /// The orbit rig's whole persistable state (see SceneSerializer). Exposed as one
        /// get/set pair rather than four properties because the four are only ever meaningful
        /// together - and because SetView has to call UpdateTransform to actually move the
        /// camera, which a plain auto-property set would silently skip.
        public void GetView(out float yaw, out float pitch, out float distance, out Vector3 pivot)
        {
            yaw = _yaw; pitch = _pitch; distance = _distance; pivot = _pivot;
        }

        public void SetView(float yaw, float pitch, float distance, Vector3 pivot)
        {
            _yaw = yaw;
            // Same clamps Update() applies to live input, so a hand-edited or corrupt save
            // can't put the rig somewhere the controls could never have reached.
            _pitch = Mathf.Clamp(pitch, -89f, 89f);
            _distance = Mathf.Clamp(distance, minDistance, maxDistance);
            _pivot = pivot;
            UpdateTransform();
        }

        private void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            Vector2 delta = mouse.delta.ReadValue();
            var kb = Keyboard.current;
            bool altHeld = kb != null && kb.leftAltKey.isPressed;
            bool ctrlHeld = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);

            if (altHeld && ctrlHeld && mouse.leftButton.isPressed)
            {
                float dragFactor = Mathf.Max(0.1f, 1f - delta.y * dragZoomSensitivity);
                _distance = Mathf.Clamp(_distance * dragFactor, minDistance, maxDistance);
            }
            else if (altHeld && mouse.leftButton.isPressed)
            {
                _yaw += delta.x * orbitSpeed;
                _pitch -= delta.y * orbitSpeed;
                _pitch = Mathf.Clamp(_pitch, -89f, 89f);
            }

            // Suppressed while a UI panel is being middle-dragged (see DraggablePanel) - without
            // this, starting a panel drag would also pan the camera underneath it every frame.
            if (mouse.middleButton.isPressed && !DraggablePanel.IsAnyDragging)
            {
                _pivot -= transform.right * (delta.x * panSpeed) + transform.up * (delta.y * panSpeed);
            }

            // Deferred to SculptController while the cursor is over the sculptable surface -
            // there, the same wheel resizes the active brush instead (see
            // SculptController.HandleBrushSizeScroll/IsHoveringSculptSurface).
            if (!SculptController.IsHoveringSculptSurface)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    float factor = 1f - Mathf.Sign(scroll) * zoomPercentPerNotch;
                    _distance = Mathf.Clamp(_distance * factor, minDistance, maxDistance);
                }
            }

            UpdateTransform();
        }

        private void UpdateTransform()
        {
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 pos = _pivot + rot * new Vector3(0f, 0f, -_distance);
            transform.SetPositionAndRotation(pos, rot);
        }
    }
}
