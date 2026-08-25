using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Alt+left-drag orbits the camera around a pivot (mirrors the navigation scheme of most
    /// sculpting apps); middle-drag pans; scroll zooms. Right mouse is not used here - it's
    /// reserved by SculptController for inverted sculpting.
    /// SculptController checks the same Alt state so Alt+left-drag orbits instead of sculpting.
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

        private void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            Vector2 delta = mouse.delta.ReadValue();
            bool altHeld = Keyboard.current != null && Keyboard.current.leftAltKey.isPressed;

            if (altHeld && mouse.leftButton.isPressed)
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

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                float factor = 1f - Mathf.Sign(scroll) * zoomPercentPerNotch;
                _distance = Mathf.Clamp(_distance * factor, minDistance, maxDistance);
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
