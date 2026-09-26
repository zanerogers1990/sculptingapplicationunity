using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Sculpting
{
    /// Nomad-style turntable: spins the view around the model at an adjustable speed, can drop
    /// every panel and viewport helper so the model is shown with nothing but its lighting and
    /// background, and records exactly one revolution as a seamless loop.
    ///
    /// THE CAMERA ORBITS; THE MODEL NEVER MOVES. Spinning the objects themselves would write
    /// their transforms (undo history, mirror links, SSphere rigs and saved files would all see
    /// it), where orbiting is pure view state. It also looks right with the default lighting:
    /// the lighting presets are locked to the camera, so the lights ride along with the orbit
    /// and the model reads as turning under fixed studio lights. Matcaps are view-space and
    /// behave the same way.
    ///
    /// THE 360 LOOP. "Record 360 Loop" takes the current view as frame 0 and renders exactly
    /// one revolution at the turntable's speed: N = round(360 / speed * fps) frames, frame i at
    /// yaw0 + 360 * i / N. Frame N would be frame 0 again, so it is left out and playback wraps
    /// with one ordinary step of rotation - a perfect loop by construction rather than by
    /// trimming. Frames are stepped per rendered frame, not per second of wall clock, so the
    /// app's framerate has no effect on the file. Pitch, distance and pivot are captured at the
    /// start and re-applied every frame, so a stray drag can't break the loop either.
    ///
    /// Self-installed by SceneGraphUIBuilder (which also builds its panel section) and lives on
    /// the orbit rig's GameObject.
    [DefaultExecutionOrder(500)]
    public class TurntableController : MonoBehaviour
    {
        public const float MinSpeed = 5f;
        public const float MaxSpeed = 180f;

        // Degrees per second per second: how quickly a spin eases in and out when toggled. A
        // hard start reads as a glitch; about a third of a second to full speed reads as a
        // turntable motor.
        private const float SpinAcceleration = 120f;

        // A canvas moved to this display is not drawn in the Game view and - because
        // GraphicRaycaster checks the canvas's display - takes no clicks either. Used instead of
        // Canvas.enabled so it can't fight the panels that already toggle themselves.
        private const int HiddenDisplay = 7;

        private const float HintSeconds = 2.5f;
        private const float CursorIdleSeconds = 1.5f;

        private const string PrefsPrefix = "Sculpting.Turntable.";

        private static TurntableController _instance;

        /// True while the clean presentation view is up. Read by SculptController (no strokes,
        /// no brush ring), TransformGizmo and the other viewport helpers.
        public static bool CleanViewActive { get; private set; }

        /// True while a 360 loop is being written.
        public static bool IsRecordingLoop => _instance != null && _instance._capture != null;

        public static TurntableController Existing =>
            _instance != null ? _instance : (_instance = FindFirstObjectByType<TurntableController>());

        // ---------------------------------------------------------------------- settings

        /// Degrees per second, always positive; Reverse gives the direction.
        public float Speed
        {
            get => _speed;
            set { _speed = Mathf.Clamp(value, MinSpeed, MaxSpeed); SavePrefs(); }
        }

        /// Clockwise seen from above when false (the view orbits left, so the model appears to
        /// turn right - the way a potter's wheel is usually shown).
        public bool Reverse
        {
            get => _reverse;
            set { _reverse = value; SavePrefs(); }
        }

        public int LoopWidth { get; private set; } = 1920;
        public int LoopHeight { get; private set; } = 1080;
        public int LoopFrameRate { get; private set; } = 30;
        public TurntableLoopFormat LoopFormat { get; private set; } = TurntableLoopFormat.Mp4;

        public float SecondsPerTurn => 360f / Mathf.Max(MinSpeed, _speed);
        public int LoopFrameCount => Mathf.Max(2, Mathf.RoundToInt(SecondsPerTurn * LoopFrameRate));

        public bool Spinning => _spinning;
        public bool CleanView => CleanViewActive;

        /// One line for the panel: progress while recording, the saved path after.
        public string Status { get; private set; } = string.Empty;
        public string LastOutputPath { get; private set; }

        private float _speed = 30f;
        private bool _reverse;
        private bool _spinning;
        private float _currentSpeed;

        private CameraOrbitController _orbit;
        private Camera _camera;

        // Clean view.
        private readonly HashSet<Canvas> _hiddenCanvases = new HashSet<Canvas>();
        private readonly List<Renderer> _hiddenRenderers = new List<Renderer>();
        private Canvas _hintCanvas;
        private CanvasGroup _hintGroup;
        private Text _hintText;
        private float _hintUntil;
        private float _cursorIdle;
        private bool _cleanViewBeforeLoop;

        // 360 loop.
        private TurntableLoopCapture _capture;
        private int _loopFrame;
        private float _loopStartYaw, _loopPitch, _loopDistance;
        private Vector3 _loopPivot;
        private bool _spinningBeforeLoop;

        public static TurntableController Install()
        {
            if (Existing != null) return _instance;

            var orbit = FindFirstObjectByType<CameraOrbitController>();
            GameObject host = orbit != null ? orbit.gameObject : new GameObject("Turntable");
            _instance = host.AddComponent<TurntableController>();
            return _instance;
        }

        private void Awake()
        {
            _instance = this;
            LoadPrefs();
        }

        private void OnDisable()
        {
            if (_capture != null) CancelLoop("Recording cancelled.");
            SetCleanView(false);
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_hintCanvas != null) Destroy(_hintCanvas.gameObject);
        }

        private CameraOrbitController Orbit =>
            _orbit != null ? _orbit : (_orbit = FindFirstObjectByType<CameraOrbitController>());

        private Camera ViewCamera
        {
            get
            {
                if (_camera != null) return _camera;
                if (Orbit != null) _camera = Orbit.GetComponent<Camera>();
                if (_camera == null) _camera = Camera.main;
                return _camera;
            }
        }

        // ------------------------------------------------------------------------ public

        public void ToggleSpin() => SetSpinning(!_spinning);

        public void SetSpinning(bool spinning)
        {
            if (_capture != null) return;
            _spinning = spinning;
        }

        public void ToggleCleanView() => SetCleanView(!CleanViewActive);

        public void SetCleanView(bool clean)
        {
            if (_capture != null && !clean) return; // the loop owns it until it finishes
            if (CleanViewActive == clean) return;
            CleanViewActive = clean;

            if (clean)
            {
                HideSceneHelpers();
                ShowHint("Tab  show UI     O  spin     Esc  exit");
            }
            else
            {
                RestoreCanvases();
                RestoreSceneHelpers();
                if (_hintGroup != null) _hintGroup.alpha = 0f;
                Cursor.visible = true;
            }
        }

        public void SetLoopResolution(int width, int height)
        {
            if (_capture != null) return;
            LoopWidth = Mathf.Max(64, width);
            LoopHeight = Mathf.Max(64, height);
            SavePrefs();
        }

        public void SetLoopFrameRate(int fps)
        {
            if (_capture != null) return;
            LoopFrameRate = Mathf.Clamp(fps, 1, 120);
            SavePrefs();
        }

        public void SetLoopFormat(TurntableLoopFormat format)
        {
            if (_capture != null) return;
            LoopFormat = format;
            SavePrefs();
        }

        /// MP4 needs the editor's encoder; a standalone build only has PNG sequences.
        public static bool VideoAvailable => LoopVideoSink.Available;

        public void ToggleLoopRecording()
        {
            if (_capture != null) CancelLoop("Recording cancelled.");
            else StartLoop();
        }

        /// Records one revolution from the current view. See the class remarks.
        public void StartLoop()
        {
            if (_capture != null || Orbit == null || ViewCamera == null) return;

            var settings = new TurntableLoopSettings
            {
                width = LoopWidth,
                height = LoopHeight,
                frameRate = LoopFrameRate,
                frameCount = LoopFrameCount,
                format = LoopFormat,
                outputFolder = OutputFolder
            };

            _capture = TurntableLoopCapture.Create(settings, ViewCamera);
            if (_capture == null)
            {
                Status = "Could not start recording - see the console.";
                return;
            }

            Orbit.GetView(out _loopStartYaw, out _loopPitch, out _loopDistance, out _loopPivot);
            _loopFrame = 0;
            _spinningBeforeLoop = _spinning;
            _spinning = false;
            _currentSpeed = 0f;

            // The viewport shows exactly what is being written, so it gets the clean view too -
            // and no strokes can land on the model mid-revolution.
            _cleanViewBeforeLoop = CleanViewActive;
            SetCleanView(true);

            Status = $"Recording 0 / {_capture.Settings.frameCount}";
        }

        // ------------------------------------------------------------------------ frame

        private void Update()
        {
            HandleKeys();

            if (_capture != null)
            {
                StepLoop();
                return;
            }

            StepSpin(Time.unscaledDeltaTime);
        }

        private void LateUpdate()
        {
            if (!CleanViewActive) return;

            // Every frame rather than once on entry: panels can be (re)built while the clean
            // view is up - the tooltip and radial menus create their canvases lazily, and a
            // scene load rebuilds every panel.
            HideCanvases();
            UpdateHint();
            UpdateCursor();
        }

        private void HandleKeys()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            // Typing a name into a panel's text field must not spin the view or hide the panel
            // being typed into.
            if (InputFocus.IsTypingInText()) return;

            // Tab for the panels, O for "orbit" - T was taken (Trim's region cycle).
            if (kb.tabKey.wasPressedThisFrame && !AnyModifier(kb)) ToggleCleanView();
            if (kb.oKey.wasPressedThisFrame && !AnyModifier(kb)) ToggleSpin();
            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (_capture != null) CancelLoop("Recording cancelled.");
                else if (CleanViewActive) SetCleanView(false);
            }
        }

        private static bool AnyModifier(Keyboard kb) =>
            kb.ctrlKey.isPressed || kb.altKey.isPressed || kb.shiftKey.isPressed;

        private void StepSpin(float dt)
        {
            CameraOrbitController orbit = Orbit;
            if (orbit == null) return;

            // Yield to the user: an Alt-drag orbit or an axis snap owns the yaw while it runs,
            // and the spin carries on from wherever it is left rather than snapping back.
            bool yield = orbit.IsUserOrbiting || orbit.IsSnapping;

            float target = _spinning && !yield ? _speed : 0f;
            _currentSpeed = yield ? 0f : Mathf.MoveTowards(_currentSpeed, target, SpinAcceleration * dt);
            if (_currentSpeed <= 0f) return;

            // Negative yaw turns the view clockwise around the model seen from above, so the
            // model itself appears to turn anticlockwise... which reads as "turning right" from
            // the front. Reverse flips it.
            float direction = _reverse ? 1f : -1f;
            orbit.AddYaw(direction * _currentSpeed * dt);
        }

        // ------------------------------------------------------------------- 360 loop

        private void StepLoop()
        {
            // Frame i-1 was rendered at the end of last frame; write it out first.
            if (!_capture.CollectFrame())
            {
                CancelLoop("Recording failed - see the console.");
                return;
            }

            int total = _capture.Settings.frameCount;
            if (_capture.FramesWritten >= total)
            {
                FinishLoop();
                return;
            }

            // Exact fractions of a turn from the START yaw, never accumulated steps, so frame
            // N-1 lands exactly one step short of frame 0 however many frames there are.
            float direction = _reverse ? 1f : -1f;
            float yaw = _loopStartYaw + direction * 360f * _loopFrame / total;
            Orbit.SetView(yaw, _loopPitch, _loopDistance, _loopPivot);
            _capture.RenderFrame();
            _loopFrame++;

            Status = $"Recording {_capture.FramesWritten} / {total}";
        }

        private void FinishLoop()
        {
            TurntableLoopCapture capture = _capture;
            capture.Finish(true);
            LastOutputPath = capture.OutputPath;

            float seconds = (float)capture.Settings.frameCount / capture.Settings.frameRate;
            Status = $"Saved {capture.Settings.frameCount} frames ({seconds:0.0}s loop): {Path.GetFileName(LastOutputPath)}";
            Debug.Log($"[Turntable] Wrote a {seconds:0.0}s, {capture.Settings.frameCount}-frame 360 loop to {LastOutputPath}");

            EndLoop(capture);
        }

        private void CancelLoop(string status)
        {
            TurntableLoopCapture capture = _capture;
            if (capture == null) return;
            capture.Finish(false);
            Status = status;
            EndLoop(capture);
        }

        private void EndLoop(TurntableLoopCapture capture)
        {
            _capture = null;
            if (capture != null) Destroy(capture.gameObject);

            // Back to the exact starting view - which frame N would have been anyway.
            if (Orbit != null) Orbit.SetView(_loopStartYaw, _loopPitch, _loopDistance, _loopPivot);

            _spinning = _spinningBeforeLoop;
            if (!_cleanViewBeforeLoop) SetCleanView(false);
        }

        /// Next to the timelapse recordings: <project>/Recordings in the editor, beside the
        /// executable in a build.
        private static string OutputFolder =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings"));

        // ------------------------------------------------------------------- clean view

        private void HideCanvases()
        {
            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (canvas == null || canvas == _hintCanvas || !canvas.isRootCanvas) continue;
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
                if (canvas.targetDisplay == HiddenDisplay) continue;

                // Remembered with the display it had, so restoring puts back exactly that rather
                // than assuming display 0.
                _canvasDisplays[canvas] = canvas.targetDisplay;
                canvas.targetDisplay = HiddenDisplay;
                _hiddenCanvases.Add(canvas);
            }
        }

        private readonly Dictionary<Canvas, int> _canvasDisplays = new Dictionary<Canvas, int>();

        private void RestoreCanvases()
        {
            foreach (Canvas canvas in _hiddenCanvases)
            {
                if (canvas == null) continue;
                if (canvas.targetDisplay == HiddenDisplay)
                    canvas.targetDisplay = _canvasDisplays.TryGetValue(canvas, out int display) ? display : 0;
            }
            _hiddenCanvases.Clear();
            _canvasDisplays.Clear();
        }

        // Tool previews that live in the viewport as objects of their own. Each owner shows and
        // hides them with SetActive, never Renderer.enabled, so switching the renderers off here
        // sticks for as long as the clean view is up without fighting any of them - and switching
        // them back on restores exactly what the owner last asked for.
        //
        // Handled elsewhere, by a CleanViewActive check at the source, because their owners
        // re-assert visibility every frame: the transform gizmo, mirror planes, pose guide lines,
        // selection flashes. The SSphere skin preview is deliberately NOT here - in Preview mode
        // it is the model, as far as anyone looking at it is concerned.
        private static readonly HashSet<string> HelperObjectNames = new HashSet<string>
        {
            "SSphereArmature", "SSpherePlacementCursor", "SSphereSymmetryPlane",
            "MoldPartingSurface", "MoldUndercutTint", "MoldBlockOutline", "MoldFeaturePreview",
            "ExtractPreview"
        };

        private void HideSceneHelpers()
        {
            _hiddenRenderers.Clear();

            // FindObjectsOfTypeAll rather than FindObjectsByType: most of these are DontSave,
            // which FindObjectsByType does not return. Walked from the named roots so their
            // children (the SSphere armature's link meshes) go too. Once, on entry: every tool
            // that creates them is locked out of input while the clean view is up.
            foreach (Transform t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !HelperObjectNames.Contains(t.name)) continue;
                if (!t.gameObject.scene.IsValid()) continue; // an asset, not a live object
                foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
                {
                    if (!r.enabled) continue;
                    r.enabled = false;
                    _hiddenRenderers.Add(r);
                }
            }

            foreach (SelectionFlashEffect flash in FindObjectsByType<SelectionFlashEffect>(FindObjectsSortMode.None))
                Destroy(flash);

            SculptMaterialController material = FindFirstObjectByType<SculptMaterialController>();
            if (material != null) material.MaskTintVisible = false;
        }

        private void RestoreSceneHelpers()
        {
            foreach (Renderer r in _hiddenRenderers)
                if (r != null) r.enabled = true;
            _hiddenRenderers.Clear();

            SculptMaterialController material = FindFirstObjectByType<SculptMaterialController>();
            if (material != null) material.MaskTintVisible = true;
        }

        // A single line of help at the bottom of the screen for a moment after entering the
        // clean view - otherwise there is no visible way back out of a screen with no UI on it.
        private void ShowHint(string text)
        {
            EnsureHintCanvas();
            _hintText.text = text;
            _hintUntil = Time.unscaledTime + HintSeconds;
            _hintGroup.alpha = 1f;
        }

        private void UpdateHint()
        {
            if (_hintGroup == null) return;
            // Hidden outright while recording - it's overlay UI and never reaches the file, but
            // anyone screen-capturing the window alongside would get it.
            if (_capture != null) { _hintGroup.alpha = 0f; return; }
            float remaining = _hintUntil - Time.unscaledTime;
            _hintGroup.alpha = Mathf.Clamp01(remaining / 0.5f);
        }

        private void EnsureHintCanvas()
        {
            if (_hintCanvas != null) return;

            var go = new GameObject("TurntableHintCanvas", typeof(RectTransform));
            _hintCanvas = go.AddComponent<Canvas>();
            _hintCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _hintCanvas.sortingOrder = 1000;
            _hintGroup = go.AddComponent<CanvasGroup>();
            _hintGroup.blocksRaycasts = false;
            _hintGroup.interactable = false;

            var bg = new GameObject("Hint", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            bg.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
            bg.GetComponent<Image>().raycastTarget = false;
            var rect = bg.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 24f);
            rect.sizeDelta = new Vector2(420f, 30f);

            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(bg.transform, false);
            _hintText = textGO.AddComponent<Text>();
            _hintText.font = UIFactory.Font;
            _hintText.fontSize = 14;
            _hintText.alignment = TextAnchor.MiddleCenter;
            _hintText.color = new Color(1f, 1f, 1f, 0.9f);
            _hintText.raycastTarget = false;
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
        }

        /// The pointer disappears when the mouse rests, and comes back the moment it moves - a
        /// presentation view with an arrow parked in the middle of the model isn't one.
        private void UpdateCursor()
        {
            Mouse mouse = Mouse.current;
            bool moved = mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 0.01f
                                           || mouse.leftButton.isPressed || mouse.middleButton.isPressed);
            _cursorIdle = moved ? 0f : _cursorIdle + Time.unscaledDeltaTime;
            Cursor.visible = _capture == null && _cursorIdle < CursorIdleSeconds;
        }

        // ------------------------------------------------------------------------ prefs

        private void LoadPrefs()
        {
            _speed = Mathf.Clamp(PlayerPrefs.GetFloat(PrefsPrefix + "Speed", _speed), MinSpeed, MaxSpeed);
            _reverse = PlayerPrefs.GetInt(PrefsPrefix + "Reverse", 0) != 0;
            LoopWidth = PlayerPrefs.GetInt(PrefsPrefix + "Width", LoopWidth);
            LoopHeight = PlayerPrefs.GetInt(PrefsPrefix + "Height", LoopHeight);
            LoopFrameRate = PlayerPrefs.GetInt(PrefsPrefix + "Fps", LoopFrameRate);
            LoopFormat = (TurntableLoopFormat)PlayerPrefs.GetInt(PrefsPrefix + "Format", (int)LoopFormat);
        }

        private void SavePrefs()
        {
            PlayerPrefs.SetFloat(PrefsPrefix + "Speed", _speed);
            PlayerPrefs.SetInt(PrefsPrefix + "Reverse", _reverse ? 1 : 0);
            PlayerPrefs.SetInt(PrefsPrefix + "Width", LoopWidth);
            PlayerPrefs.SetInt(PrefsPrefix + "Height", LoopHeight);
            PlayerPrefs.SetInt(PrefsPrefix + "Fps", LoopFrameRate);
            PlayerPrefs.SetInt(PrefsPrefix + "Format", (int)LoopFormat);
        }
    }
}
