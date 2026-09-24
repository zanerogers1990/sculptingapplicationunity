using System;
using System.IO;
using UnityEditor;
using UnityEditor.Media;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Encoder;
using UnityEditor.Recorder.Input;
using UnityEngine;

namespace Sculpting.TimelapseEditor
{
    /// Owns the timelapse recording: the Recorder, the rig, the settings and the frame gate.
    ///
    /// This used to live in the window, which meant recording could only be started from a window
    /// that was open. It isn't a window's job: the app is driven from a maximised Game view, so
    /// the in-app button (see TimelapseControl) needs to work with no editor window on screen at
    /// all. `[InitializeOnLoad]` gets this listening from the moment the editor loads, and
    /// SculptTimelapseWindow is now just a view onto it.
    ///
    /// HOW THE ACTIVITY GATE WORKS, because it is the one non-obvious thing in here:
    ///
    /// Recorder has no pause. It does have a documented per-frame skip - Recorder.SkipFrame -
    /// but the only levers on it reachable from outside the package are the recording mode's
    /// start bound and the mode itself, both public settable properties on RecorderSettings. So
    /// the recorder is put in Time Interval mode, and its StartTime is moved: 0 while the user is
    /// sculpting (nothing is skipped), and parked far in the future while they are not (every
    /// frame is skipped, because the session's frame index will never reach that start frame).
    ///
    /// The reason skipping is enough - rather than merely producing a video with frozen stretches
    /// - is the constant frame rate. In constant-rate mode Recorder hands the encoder no
    /// timestamps at all (MovieRecorder.ComputeMediaTime returns an invalid time and the encoder
    /// appends frames back to back at the output rate), so a skipped frame is not a gap in the
    /// video. It simply never existed. Idle time is removed, not frozen.
    ///
    /// EndTime is moved along with StartTime for a duller reason: Recorder ends a Time Interval
    /// recording once it has written (EndTime - StartTime) * FrameRate frames, so the span has to
    /// stay large whatever StartTime is doing.
    [InitializeOnLoad]
    public static class TimelapseRecorderService
    {
        // Large enough that no session reaches it (a hundred days of footage at 30fps) and small
        // enough that StartTime * FrameRate stays comfortably inside an int, which is what
        // Recorder converts it to.
        private const float GateSpanSeconds = 1e6f;

        private const string PrefsPrefix = "Sculpting.Timelapse.";

        public static TimelapseOptions Options { get; private set; } = new TimelapseOptions();
        public static string OutputFolder { get; set; }
        public static string LastOutputPath { get; private set; }
        public static TimelapseRig Rig { get; private set; }

        public static bool IsRecording => _controller != null && _controller.IsRecording();

        /// Raised whenever recording starts or stops, so an open window repaints immediately
        /// rather than on its next poll.
        public static event Action StateChanged;

        private static RecorderController _controller;
        private static RecorderControllerSettings _controllerSettings;
        private static MovieRecorderSettings _movieSettings;
        private static bool? _gateOpen;

        static TimelapseRecorderService()
        {
            LoadPrefs();
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;

            LoopVideoSink.Begin = BeginLoopVideo;
            LoopVideoSink.AddFrame = AddLoopFrame;
            LoopVideoSink.End = EndLoopVideo;
        }

        // ------------------------------------------------------------ turntable 360 loop video

        // The turntable's 360 loop is encoded here rather than through Recorder: a seamless loop
        // needs exactly one revolution's worth of frames in the file, and MediaEncoder writes
        // precisely the frames it is handed - see LoopVideoSink.
        private static MediaEncoder _loopEncoder;
        private static string _loopPath;

        /// Where the last 360 loop went (MP4 or PNG folder), for the window's status block.
        public static string LastLoopPath { get; private set; }

        private static string BeginLoopVideo(TurntableLoopSettings settings)
        {
            EndLoopVideo(false);

            // The turntable names its folder (Recordings, the timelapse's default too); the
            // timelapse's own setting is only a fallback.
            string folder = string.IsNullOrEmpty(settings.outputFolder) ? OutputFolder : settings.outputFolder;
            Directory.CreateDirectory(folder);
            _loopPath = Path.Combine(folder, $"Turntable_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

            // Bitrate scaled to the pixel rate: ~0.2 bits per pixel per frame is visually
            // lossless for a smooth-shaded turntable, where the default "high" preset still
            // bands the gradients on the background and the matcap.
            long bitRate = (long)(settings.width * (long)settings.height * settings.frameRate * 0.2f);
            var attributes = new VideoTrackEncoderAttributes(new H264EncoderAttributes
            {
                // A keyframe every second and no B-frames: players scrub and loop cleanly, and
                // the first frame is always a keyframe, which is what a looping player jumps to.
                gopSize = (uint)Mathf.Max(1, settings.frameRate),
                numConsecutiveBFrames = 0,
                profile = VideoEncodingProfile.H264High
            })
            {
                frameRate = new MediaRational(settings.frameRate),
                width = (uint)settings.width,
                height = (uint)settings.height,
                targetBitRate = (uint)Mathf.Clamp(bitRate, 2_000_000L, 100_000_000L),
                bitRateMode = VideoBitrateMode.High
            };

            try
            {
                _loopEncoder = new MediaEncoder(_loopPath, attributes);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Turntable] Could not open the video encoder: {e.Message}");
                _loopEncoder = null;
                return null;
            }
            return _loopPath;
        }

        private static void AddLoopFrame(Texture2D frame) => _loopEncoder?.AddFrame(frame);

        private static void EndLoopVideo(bool completed)
        {
            if (_loopEncoder == null) return;
            _loopEncoder.Dispose();
            _loopEncoder = null;

            if (completed)
            {
                LastLoopPath = _loopPath;
            }
            else
            {
                try { if (File.Exists(_loopPath)) File.Delete(_loopPath); } catch (Exception) { }
            }
            StateChanged?.Invoke();
        }

        // ------------------------------------------------------------------------- the loop

        private static void OnEditorUpdate()
        {
            // The in-app button's request. Checked here rather than in the window so it works
            // with every editor window closed.
            if (TimelapseControl.ConsumeToggleRequest()) Toggle();

            TimelapseControl.PublishState(IsRecording, BuildStatusLine());
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode && IsRecording) Stop();
        }

        /// What the in-app button shows under itself, and what the window echoes.
        private static string BuildStatusLine()
        {
            if (!IsRecording || Rig == null) return string.Empty;

            float seconds = Rig.CapturedFrames / Mathf.Max(1f, Options.frameRate);
            return Rig.IsSculpting
                ? $"Recording  {seconds:0.0}s  ({Rig.CapturedFrames} frames)"
                : $"Paused - idle  {seconds:0.0}s  ({Rig.CapturedFrames} frames)";
        }

        // ------------------------------------------------------------------- start and stop

        public static void Toggle()
        {
            if (IsRecording) Stop(); else Start();
        }

        public static void Start()
        {
            if (IsRecording) return;

            if (!Application.isPlaying)
            {
                Debug.LogWarning("[Timelapse] Recording can only start in Play mode.");
                return;
            }

            Directory.CreateDirectory(OutputFolder);

            DestroyStrayRigs();

            // The rig first: its render texture is what the recorder is pointed at, and Recorder
            // rejects a render texture input whose texture is missing at PrepareRecording.
            Rig = TimelapseRig.Create(Options);

            // Recorder appends the extension itself, so this is the path WITHOUT one.
            string stem = Path.Combine(OutputFolder, $"Timelapse_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");

            _movieSettings = ScriptableObject.CreateInstance<MovieRecorderSettings>();
            _movieSettings.name = "Sculpt Timelapse";
            _movieSettings.Enabled = true;
            _movieSettings.OutputFile = stem;
            _movieSettings.EncoderSettings = new CoreEncoderSettings
            {
                Codec = CoreEncoderSettings.OutputCodec.MP4,
                EncodingQuality = CoreEncoderSettings.VideoEncodingQuality.High
            };
            _movieSettings.CaptureAudio = false;
            _movieSettings.ImageInputSettings = new RenderTextureInputSettings
            {
                RenderTexture = Rig.Output
            };

            _controllerSettings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            _controllerSettings.AddRecorderSettings(_movieSettings);
            // Constant, not Variable: constant is what makes a skipped frame vanish from the
            // video rather than freeze it. See the class remarks.
            _controllerSettings.FrameRatePlayback = FrameRatePlayback.Constant;
            _controllerSettings.FrameRate = Mathf.Max(1f, Options.frameRate);
            _controllerSettings.CapFrameRate = Options.capFrameRate;
            _controllerSettings.SetRecordModeToTimeInterval(0f, GateSpanSeconds);

            _controller = new RecorderController(_controllerSettings);

            try
            {
                // PrepareRecording is where the controller-level settings above are stamped onto
                // the movie recorder, so the gate below has to come after it or it is overwritten.
                _controller.PrepareRecording();
                if (!_controller.StartRecording())
                {
                    Debug.LogError("[Timelapse] Recorder refused to start - see the console above.");
                    Cleanup();
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Timelapse] Could not start recording: {e.Message}");
                Cleanup();
                return;
            }

            _gateOpen = null;      // forces the first ApplyGate call to actually write
            ApplyGate(false);      // start closed - nothing has been sculpted yet
            Rig.GateCapture = ApplyGate;

            LastOutputPath = stem + ".mp4";
            SavePrefs();
            StateChanged?.Invoke();
        }

        public static void Stop()
        {
            if (_controller != null && _controller.IsRecording())
            {
                // Reopening the gate first: StopRecording finalises the file, and leaving the
                // recorder parked in the future while it does so is a needless thing to hand it.
                ApplyGate(true);
                _controller.StopRecording();
            }

            int frames = Rig != null ? Rig.CapturedFrames : 0;
            Cleanup();

            if (frames > 0)
                Debug.Log($"[Timelapse] Wrote {frames} frames " +
                          $"({frames / Mathf.Max(1f, Options.frameRate):0.0}s) to {LastOutputPath}");
            else
                Debug.LogWarning("[Timelapse] Nothing was captured - no sculpting happened while recording.");

            StateChanged?.Invoke();
        }

        /// Destroys rigs left over from a recording that never got a clean Stop - which is a real
        /// case, not a theoretical one: recompiling a script mid-Play triggers a domain reload
        /// that wipes this class's static reference to the rig while the rig itself carries on
        /// living, holding its render texture open. Two such orphans were found in a single
        /// afternoon of testing.
        ///
        /// The tests below are the whole point. An orphan has NO scene (scene.IsValid() is
        /// false) - the same thing a prefab asset reports, which is why this can't live in
        /// TimelapseRig: only EditorUtility.IsPersistent separates "leaked at runtime" from "a
        /// prefab on disk I must not touch", and that is editor-only.
        private static void DestroyStrayRigs()
        {
            foreach (TimelapseRig rig in Resources.FindObjectsOfTypeAll<TimelapseRig>())
            {
                if (rig == null) continue;
                if (EditorUtility.IsPersistent(rig.gameObject)) continue;
                UnityEngine.Object.DestroyImmediate(rig.gameObject);
            }
        }

        private static void Cleanup()
        {
            if (Rig != null)
            {
                Rig.GateCapture = null;
                UnityEngine.Object.DestroyImmediate(Rig.gameObject);
                Rig = null;
            }

            if (_movieSettings != null) { UnityEngine.Object.DestroyImmediate(_movieSettings); _movieSettings = null; }
            if (_controllerSettings != null) { UnityEngine.Object.DestroyImmediate(_controllerSettings); _controllerSettings = null; }

            _controller = null;
            _gateOpen = null;
        }

        /// The gate itself - see the class remarks for why moving StartTime is what pauses a
        /// recording that has no pause. Only writes on a change of state, so the common case
        /// (frame after frame of the same answer) costs a comparison.
        private static void ApplyGate(bool capture)
        {
            if (_movieSettings == null) return;
            if (_gateOpen.HasValue && _gateOpen.Value == capture) return;
            _gateOpen = capture;

            if (capture)
            {
                _movieSettings.StartTime = 0f;
                _movieSettings.EndTime = GateSpanSeconds;
            }
            else
            {
                // A start frame the session will never reach, so every frame is skipped. Divided
                // by the frame rate because Recorder turns StartTime into a frame index by
                // multiplying by it, and the product has to stay inside an int.
                float parked = 1e9f / Mathf.Max(1f, _movieSettings.FrameRate);
                _movieSettings.StartTime = parked;
                _movieSettings.EndTime = parked + GateSpanSeconds;
            }
        }

        // ---------------------------------------------------------------------------- prefs

        private static void LoadPrefs()
        {
            OutputFolder = EditorPrefs.GetString(PrefsPrefix + "Folder",
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings")));

            var o = Options;
            o.width = EditorPrefs.GetInt(PrefsPrefix + "Width", o.width);
            o.height = EditorPrefs.GetInt(PrefsPrefix + "Height", o.height);
            o.frameRate = EditorPrefs.GetFloat(PrefsPrefix + "Fps", o.frameRate);
            // Deliberately a NEW key. This setting's default flipped from on to off once the
            // clock correction made the throttle unnecessary, and a saved `true` under the old
            // key would have quietly kept every existing install at 30fps - the exact problem the
            // change exists to fix. A new key lets the new default win, once.
            o.capFrameRate = EditorPrefs.GetBool(PrefsPrefix + "CapFps2", o.capFrameRate);
            o.hideUiLayer = EditorPrefs.GetBool(PrefsPrefix + "HideUi", o.hideUiLayer);
            o.speed = EditorPrefs.GetFloat(PrefsPrefix + "Speed", o.speed);
            o.orbitDegreesPerSecond = EditorPrefs.GetFloat(PrefsPrefix + "Orbit", o.orbitDegreesPerSecond);
            o.orbitPitch = EditorPrefs.GetFloat(PrefsPrefix + "Pitch", o.orbitPitch);
            o.distanceMultiplier = EditorPrefs.GetFloat(PrefsPrefix + "Distance", o.distanceMultiplier);
            o.idleGraceSeconds = EditorPrefs.GetFloat(PrefsPrefix + "Grace", o.idleGraceSeconds);
            o.followSubject = EditorPrefs.GetBool(PrefsPrefix + "Follow", o.followSubject);
            o.cameraSmoothingSeconds = EditorPrefs.GetFloat(PrefsPrefix + "Smoothing", o.cameraSmoothingSeconds);
            o.framing = (TimelapseFraming)EditorPrefs.GetInt(PrefsPrefix + "Framing", (int)o.framing);
            o.focusStrength = EditorPrefs.GetFloat(PrefsPrefix + "FocusStrength", o.focusStrength);
            o.focusZoom = EditorPrefs.GetFloat(PrefsPrefix + "FocusZoom", o.focusZoom);
        }

        public static void SavePrefs()
        {
            var o = Options;
            EditorPrefs.SetString(PrefsPrefix + "Folder", OutputFolder);
            EditorPrefs.SetInt(PrefsPrefix + "Width", o.width);
            EditorPrefs.SetInt(PrefsPrefix + "Height", o.height);
            EditorPrefs.SetFloat(PrefsPrefix + "Fps", o.frameRate);
            EditorPrefs.SetBool(PrefsPrefix + "CapFps2", o.capFrameRate);
            EditorPrefs.SetBool(PrefsPrefix + "HideUi", o.hideUiLayer);
            EditorPrefs.SetFloat(PrefsPrefix + "Speed", o.speed);
            EditorPrefs.SetFloat(PrefsPrefix + "Orbit", o.orbitDegreesPerSecond);
            EditorPrefs.SetFloat(PrefsPrefix + "Pitch", o.orbitPitch);
            EditorPrefs.SetFloat(PrefsPrefix + "Distance", o.distanceMultiplier);
            EditorPrefs.SetFloat(PrefsPrefix + "Grace", o.idleGraceSeconds);
            EditorPrefs.SetBool(PrefsPrefix + "Follow", o.followSubject);
            EditorPrefs.SetFloat(PrefsPrefix + "Smoothing", o.cameraSmoothingSeconds);
            EditorPrefs.SetInt(PrefsPrefix + "Framing", (int)o.framing);
            EditorPrefs.SetFloat(PrefsPrefix + "FocusStrength", o.focusStrength);
            EditorPrefs.SetFloat(PrefsPrefix + "FocusZoom", o.focusZoom);
        }
    }
}
