using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Borderless fullscreen for the standalone build: F11 (or the Scene panel's button) toggles
    /// between a normal window and a window that covers the whole display, title bar and taskbar
    /// included.
    ///
    /// FullScreenWindow rather than ExclusiveFullScreen on purpose. Exclusive mode drops the
    /// desktop's display mode out from under every other app and makes Alt+Tab a slow mode switch,
    /// and it buys nothing here - the viewport isn't latency-bound the way a game is. Borderless
    /// at the display's native size looks identical and tabs away instantly.
    ///
    /// The choice is remembered in PlayerPrefs and re-applied at startup, rather than trusting
    /// Unity's own saved screen mode: the player keeps that in the registry under the product
    /// name and it silently overrides the Player Settings default from the second launch on, so a
    /// build that once ran windowed would stay windowed no matter what the project said.
    ///
    /// A no-op inside the editor, where Screen's mode setters do nothing to the Game view.
    public class FullscreenController : MonoBehaviour
    {
        private const string PrefKey = "Sculpting.Fullscreen";

        // Fraction of the display a restored window takes when there's no earlier windowed size to
        // go back to (the app started fullscreen).
        private const float FallbackWindowFraction = 0.8f;

        private static int _windowedWidth, _windowedHeight;
        private static bool _lastSeenFullscreen;

        // A requested mode lands a frame or two later; until then Screen still reports the old
        // one, and Update's Alt+Enter sync mustn't mistake that for the user switching back.
        private const int SettleFrames = 10;
        private static int _settleUntilFrame;

        public static bool IsFullscreen =>
            Screen.fullScreenMode == FullScreenMode.FullScreenWindow ||
            Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen;

        /// False in the editor, so the panel can say why its button does nothing there.
        public static bool IsSupported => !Application.isEditor;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var go = new GameObject(nameof(FullscreenController));
            DontDestroyOnLoad(go);
            go.AddComponent<FullscreenController>();

            if (!IsSupported) return;
            // Fullscreen unless the user last left it windowed.
            SetFullscreen(PlayerPrefs.GetInt(PrefKey, 1) != 0);
        }

        public static void Toggle() => SetFullscreen(!IsFullscreen);

        public static void SetFullscreen(bool on)
        {
            if (!IsSupported) return;

            if (on)
            {
                if (!IsFullscreen)
                {
                    _windowedWidth = Screen.width;
                    _windowedHeight = Screen.height;
                }
                // Native display size, not the window's current size - switching mode alone keeps
                // the old resolution and stretches it to fill the screen.
                Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, FullScreenMode.FullScreenWindow);
            }
            else
            {
                int w = _windowedWidth, h = _windowedHeight;
                if (w <= 0 || h <= 0 || w >= Display.main.systemWidth)
                {
                    w = Mathf.RoundToInt(Display.main.systemWidth * FallbackWindowFraction);
                    h = Mathf.RoundToInt(Display.main.systemHeight * FallbackWindowFraction);
                }
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
            }

            _lastSeenFullscreen = on;
            _settleUntilFrame = Time.frameCount + SettleFrames;
            PlayerPrefs.SetInt(PrefKey, on ? 1 : 0);
            PlayerPrefs.Save();
        }

        private void Update()
        {
            if (!IsSupported) return;

            var kb = Keyboard.current;
            if (kb != null && kb.f11Key.wasPressedThisFrame) Toggle();

            // Alt+Enter is handled by the player itself (allowFullscreenSwitch) and never passes
            // through SetFullscreen, so pick up its result here to keep the saved choice honest.
            if (Time.frameCount < _settleUntilFrame) return;
            bool now = IsFullscreen;
            if (now != _lastSeenFullscreen)
            {
                _lastSeenFullscreen = now;
                PlayerPrefs.SetInt(PrefKey, now ? 1 : 0);
                PlayerPrefs.Save();
            }
        }
    }
}
