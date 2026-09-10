using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Sculpting
{
    /// Hover-and-wait tooltips for UIFactory controls (and any other GameObject that wants one -
    /// see Attach). Hovering a control that was given tooltip text for HoverDelaySeconds shows a
    /// small popup near the cursor explaining what it does; moving the pointer off the control,
    /// the control disabling itself, or the global switch below going off all hide it again.
    ///
    /// Event-driven for the "wait and show" half (OnPointerEnter/Exit) - only the actually-
    /// hovered control's own Update needs to run a timer. But OnPointerExit is NOT trusted alone
    /// to hide it again: uGUI only fires it when the pointer crosses this rect's edge on a frame
    /// it processes input, which misses a hovered control that scrolls/resizes/reparents out from
    /// under a stationary cursor (a foldout collapsing above it, a slider's own value-driven
    /// layout change) - the pointer never "moved" so no exit event fires, and the popup would be
    /// left pointing at empty space. Once shown, Update() below re-checks every frame that the
    /// cursor is still actually within this control's own rect and self-hides the moment it
    /// isn't, independent of whatever uGUI's enter/exit bookkeeping thinks happened.
    public sealed class TooltipTrigger : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private const float HoverDelaySeconds = 0.6f;

        public string Text;

        private RectTransform _rect;
        private float _hoverStartTime = -1f;
        private bool _shown;
        private Vector2 _pointerPos;

        private void Awake() => _rect = transform as RectTransform;

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hoverStartTime = Time.unscaledTime;
            _pointerPos = eventData.position;
        }

        public void OnPointerExit(PointerEventData eventData) => EndHover();

        private void Update()
        {
            if (_hoverStartTime < 0f) return;

            if (!_shown)
            {
                if (Time.unscaledTime - _hoverStartTime < HoverDelaySeconds) return;
                _shown = true;
                TooltipSystem.Show(this, Text, _pointerPos);
                return;
            }

            // Defensive re-check - see class remarks. Mouse.current rather than the event
            // system's own pointer position: this runs independently of whether uGUI
            // dispatched any pointer event this frame at all.
            Mouse mouse = Mouse.current;
            if (mouse == null || _rect == null ||
                !RectTransformUtility.RectangleContainsScreenPoint(_rect, mouse.position.ReadValue(), null))
            {
                EndHover();
            }
        }

        // Covers everything that should make the popup go away short of the delay simply not
        // having elapsed yet: the pointer actually leaving (OnPointerExit), the defensive rect
        // check above catching a silent exit, and the control being disabled outright (OnDisable
        // below) - e.g. its whole foldout/panel closing while the pointer sits still over where
        // it used to be.
        private void EndHover()
        {
            _hoverStartTime = -1f;
            _shown = false;
            TooltipSystem.Hide(this);
        }

        private void OnDisable() => EndHover();
    }

    /// Owns the shared popup box every TooltipTrigger shows into, and the global on/off switch
    /// (persisted - a user who turns these off once shouldn't see them come back next session).
    /// Static rather than a scene singleton so UIFactory can call it from anywhere without a
    /// reference to hand around, matching how UIFactory itself already publishes shared state
    /// (Font, PanelColor, ...) as statics.
    public static class TooltipSystem
    {
        private const string EnabledPrefKey = "Sculpting.TooltipsEnabled";
        private static bool? _enabledCache;

        public static bool Enabled
        {
            get => _enabledCache ??= PlayerPrefs.GetInt(EnabledPrefKey, 1) != 0;
            set
            {
                if (_enabledCache == value) return;
                _enabledCache = value;
                PlayerPrefs.SetInt(EnabledPrefKey, value ? 1 : 0);
                if (!value) Hide(null);
                if (_enabledToggleLabel != null)
                    _enabledToggleLabel.text = value ? "Tooltips: On" : "Tooltips: Off";
            }
        }

        /// Adds (or retargets) a TooltipTrigger on `go`. `text` may be null/empty to mean "no
        /// tooltip" - callers pass whatever they have without needing their own null check.
        public static void Attach(GameObject go, string text)
        {
            if (go == null || string.IsNullOrEmpty(text)) return;
            var trigger = go.GetComponent<TooltipTrigger>();
            if (trigger == null) trigger = go.AddComponent<TooltipTrigger>();
            trigger.Text = text;
        }

        // --- Popup box -------------------------------------------------------------------

        private static RectTransform _boxRect;
        private static Text _label;
        private static object _owner;

        private const float PreferredTextWidth = 200f;
        private static readonly Vector2 CursorOffset = new Vector2(18f, -18f);
        private const float ScreenMargin = 6f;

        public static void Show(object owner, string text, Vector2 screenPos)
        {
            if (!Enabled || string.IsNullOrEmpty(text)) return;
            EnsureBuilt();
            _owner = owner;
            _label.text = text;
            _boxRect.gameObject.SetActive(true);

            // Force the box to resize to the new text NOW rather than at next layout pass, so
            // the clamp below reads this frame's size instead of last tooltip's leftover one.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_boxRect);

            float w = _boxRect.rect.width;
            float h = _boxRect.rect.height;
            float x = Mathf.Clamp(screenPos.x + CursorOffset.x, ScreenMargin, Mathf.Max(ScreenMargin, Screen.width - w - ScreenMargin));
            float y = Mathf.Clamp(screenPos.y + CursorOffset.y, h + ScreenMargin, Screen.height - ScreenMargin);
            _boxRect.position = new Vector3(x, y, 0f);
        }

        /// owner == null clears unconditionally (used by Enabled = false); otherwise only the
        /// trigger that currently owns the popup can dismiss it, so a stale OnDisable from a
        /// control the pointer already left can't hide a different control's live tooltip.
        public static void Hide(object owner)
        {
            if (_boxRect == null) return;
            if (owner != null && owner != _owner) return;
            _owner = null;
            _boxRect.gameObject.SetActive(false);
        }

        private static void EnsureBuilt()
        {
            if (_boxRect != null) return;

            var canvasGO = new GameObject("TooltipCanvas", typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000; // above every docked panel canvas
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            // No GraphicRaycaster: the popup must never itself count as "pointer over UI", or
            // it would immediately steal the hover that is showing it and flicker-hide.
            UnityEngine.Object.DontDestroyOnLoad(canvasGO);

            var boxGO = new GameObject("Box", typeof(RectTransform), typeof(Image));
            boxGO.transform.SetParent(canvasGO.transform, false);
            boxGO.GetComponent<Image>().color = new Color(0.05f, 0.05f, 0.06f, 0.95f);
            _boxRect = boxGO.GetComponent<RectTransform>();
            _boxRect.pivot = new Vector2(0f, 1f); // position we set is the box's top-left corner

            var fitter = boxGO.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var vlg = boxGO.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(6, 6, 4, 4);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;

            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(boxGO.transform, false);
            _label = textGO.AddComponent<Text>();
            _label.font = UIFactory.Font;
            _label.fontSize = 11;
            _label.color = new Color(0.92f, 0.92f, 0.94f);
            _label.horizontalOverflow = HorizontalWrapMode.Wrap;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            _label.raycastTarget = false;
            textGO.AddComponent<LayoutElement>().preferredWidth = PreferredTextWidth;

            boxGO.GetComponent<Image>().raycastTarget = false;
            boxGO.SetActive(false);
        }

        // --- Global on/off switch ----------------------------------------------------------

        private static bool _toggleBuilt;
        private static Text _enabledToggleLabel;

        // Matches SculptUIBuilder's own reference/floor width for the Sculpting Tools panel
        // docked at the top-left (see UIFactory.ResponsivePanelWidth) - that panel carries by
        // far the most controls of any panel in the app, so it's routinely tall enough to reach
        // the bottom of the screen, which is exactly where this toggle used to sit at a bare
        // (12, 12): always directly behind that panel, never actually visible. Run through
        // ResponsivePanelWidth rather than a fixed offset so this stays clear of the panel's
        // width on any screen, not just the one it was placed on.
        private const float LeftPanelWidth = 270f;
        private const float ToggleMargin = 12f;

        /// Builds the persistent "Tooltips: On/Off" switch, clear of the docked side panels, the
        /// view gizmo (top-right), and the action toast (bottom-center). Idempotent and safe to
        /// call from more than one builder's Start() - only the first call in a session actually
        /// builds anything.
        public static void EnsureToggleBuilt()
        {
            if (_toggleBuilt) return;
            _toggleBuilt = true;

            var canvasGO = new GameObject("TooltipToggleCanvas", typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasGO.AddComponent<GraphicRaycaster>();
            UnityEngine.Object.DontDestroyOnLoad(canvasGO);

            var rowGO = new GameObject("Row", typeof(RectTransform));
            rowGO.transform.SetParent(canvasGO.transform, false);
            var rowRect = rowGO.GetComponent<RectTransform>();
            rowRect.anchorMin = rowRect.anchorMax = Vector2.zero;
            rowRect.pivot = Vector2.zero;
            rowRect.sizeDelta = new Vector2(120f, 22f);
            rowGO.AddComponent<PanelClearRepositioner>().Rect = rowRect;
            var hlg = rowGO.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;

            Toggle toggle = UIFactory.CreateToggle(rowGO.transform, Enabled ? "Tooltips: On" : "Tooltips: Off", Enabled, v => Enabled = v);
            _enabledToggleLabel = toggle.transform.Find("Label")?.GetComponent<Text>();
            Attach(toggle.gameObject, "Turns the hover explanations on every slider, toggle and button off (or back on). Your choice is remembered next time you open the app.");
        }

        /// Keeps the toggle clear of the left panel as ResponsivePanelWidth scales it with the
        /// screen - a fixed offset baked in at build time would just get covered again on a
        /// wider screen, or the moment the now-resizable window is dragged to one live.
        private sealed class PanelClearRepositioner : MonoBehaviour
        {
            public RectTransform Rect;

            private void LateUpdate()
            {
                if (Rect == null) return;
                float x = UIFactory.ResponsivePanelWidth(LeftPanelWidth) + ToggleMargin;
                Rect.anchoredPosition = new Vector2(x, ToggleMargin);
            }
        }
    }
}
