using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Lets a middle-mouse-drag anywhere inside this panel reposition it, ZBrush/Blender-style
    /// floating-panel dragging. Attach to a panel's own RectTransform (the "Panel" GameObject
    /// each UI builder creates - see UIFactory.CreatePanelCanvas and SculptUIBuilder.BuildUI),
    /// not its parent Canvas.
    ///
    /// Polls Mouse.current directly in Update() rather than using uGUI's IPointerDownHandler/
    /// IDragHandler interfaces - this project already polls Mouse.current everywhere else
    /// (SculptController, CameraOrbitController), and multi-button drag tracking through
    /// PointerInputModule is easy to get subtly wrong, so matching the existing style is both
    /// more consistent and less risky here.
    [RequireComponent(typeof(RectTransform))]
    [DefaultExecutionOrder(-100)]
    public class DraggablePanel : MonoBehaviour
    {
        // Every panel in this project sits on its own ScreenSpaceOverlay Canvas at
        // scaleFactor 1 (see UIFactory.CreatePanelCanvas/SculptUIBuilder.BuildUI) - a raw mouse
        // screen-pixel delta maps 1:1 onto anchoredPosition, so no canvas-scale conversion is
        // needed when applying drag deltas below.
        private RectTransform _rect;
        private bool _isDragging;
        private Vector2 _dragStartMouse;
        private Vector2 _dragStartAnchoredPos;

        // Shared across every DraggablePanel instance (there's one mouse) so
        // CameraOrbitController can suppress its own middle-drag pan for the whole duration of
        // a panel drag, not just while the cursor happens to still be over the panel - the drag
        // should keep tracking the mouse even if it wanders off the panel's own rect mid-drag.
        // [DefaultExecutionOrder(-100)] guarantees this updates before CameraOrbitController
        // (default order 0) reads it on the same frame a drag starts.
        public static bool IsAnyDragging { get; private set; }

        private void Awake()
        {
            _rect = (RectTransform)transform;
        }

        private void OnDisable()
        {
            if (_isDragging) { _isDragging = false; IsAnyDragging = false; }
        }

        private void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            if (_isDragging)
            {
                if (!mouse.middleButton.isPressed)
                {
                    _isDragging = false;
                    IsAnyDragging = false;
                    return;
                }

                Vector2 mouseDelta = mouse.position.ReadValue() - _dragStartMouse;
                _rect.anchoredPosition = _dragStartAnchoredPos + mouseDelta;
                return;
            }

            if (!mouse.middleButton.wasPressedThisFrame) return;

            Vector2 mousePos = mouse.position.ReadValue();
            // Every panel's Canvas is ScreenSpaceOverlay (see CreatePanelCanvas/BuildUI), which
            // RectTransformUtility expects a null camera for.
            if (!RectTransformUtility.RectangleContainsScreenPoint(_rect, mousePos, null)) return;

            _isDragging = true;
            IsAnyDragging = true;
            _dragStartMouse = mousePos;
            _dragStartAnchoredPos = _rect.anchoredPosition;
        }
    }
}
