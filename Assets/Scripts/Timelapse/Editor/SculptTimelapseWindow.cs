using System.IO;
using UnityEditor;
using UnityEngine;

namespace Sculpting.TimelapseEditor
{
    /// Settings and status for the sculpting timelapse. Window > Sculpting > Timelapse Recorder.
    ///
    /// A VIEW, and nothing more - TimelapseRecorderService owns the recording, the Recorder and
    /// the frame gate. That split is what lets the in-app Start/Stop button (see the Scene panel)
    /// work with this window closed, which matters because the app is used from a maximised Game
    /// view where a floating editor window is in the way.
    public class SculptTimelapseWindow : EditorWindow
    {
        private Vector2 _scroll;

        private static TimelapseOptions Options => TimelapseRecorderService.Options;
        private static bool IsRecording => TimelapseRecorderService.IsRecording;

        [MenuItem("Window/Sculpting/Timelapse Recorder")]
        public static void Open()
        {
            var window = GetWindow<SculptTimelapseWindow>("Timelapse");
            window.minSize = new Vector2(320f, 460f);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            TimelapseRecorderService.StateChanged += Repaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            TimelapseRecorderService.StateChanged -= Repaint;
            // Closing the window no longer stops the recording - the service outlives it, which
            // is the whole point of the split.
            TimelapseRecorderService.SavePrefs();
        }

        private void OnEditorUpdate()
        {
            if (IsRecording) Repaint();
        }

        // ------------------------------------------------------------------------------- UI

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.LabelField("Sculpt Timelapse", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Records only while the model is actually changing. Idling and undo/redo are " +
                "left out of the video.\n\n" +
                "The camera follows your own viewing angle and leans towards whatever you are " +
                "sculpting - smoothed, so it reads as a camera move rather than a replay of " +
                "your mouse.\n\n" +
                "Start/Stop is also on the Scene panel inside the app, so you can stay in the " +
                "Game view.",
                MessageType.None);

            EditorGUI.BeginChangeCheck();

            DrawOutputSettings();
            EditorGUILayout.Space();
            DrawCameraSettings();

            if (EditorGUI.EndChangeCheck()) TimelapseRecorderService.SavePrefs();

            EditorGUILayout.Space();
            DrawTransport();
            EditorGUILayout.Space();
            DrawStatus();

            EditorGUILayout.EndScrollView();
        }

        private void DrawOutputSettings()
        {
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            // Everything here is baked into the file and the encoder when recording starts, so it
            // cannot be changed underneath a running recording.
            using (new EditorGUI.DisabledScope(IsRecording))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Folder", GUILayout.Width(60f));
                EditorGUILayout.SelectableLabel(TimelapseRecorderService.OutputFolder, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("...", GUILayout.Width(30f)))
                {
                    string picked = EditorUtility.SaveFolderPanel("Timelapse output folder",
                        TimelapseRecorderService.OutputFolder, "");
                    if (!string.IsNullOrEmpty(picked)) TimelapseRecorderService.OutputFolder = picked;
                }
                EditorGUILayout.EndHorizontal();

                Options.width = EditorGUILayout.IntField("Width", Options.width);
                Options.height = EditorGUILayout.IntField("Height", Options.height);
                Options.frameRate = EditorGUILayout.FloatField("Video FPS", Options.frameRate);
                Options.capFrameRate = EditorGUILayout.Toggle(
                    new GUIContent("Cap App To Video FPS",
                        "Throttles the whole app to the video frame rate while recording - " +
                        "measured as a 10x loss of responsiveness (291fps to 30). Leave it off: " +
                        "the app's clock is kept honest without it. Only useful if you want the " +
                        "app running in exact lockstep with the video."),
                    Options.capFrameRate);
                Options.hideUiLayer = EditorGUILayout.Toggle(
                    new GUIContent("Hide UI Layer", "Leaves the tool panels out of the video."),
                    Options.hideUiLayer);
            }
        }

        private void DrawCameraSettings()
        {
            EditorGUILayout.LabelField("Camera & Pacing", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Adjustable while recording.", EditorStyles.miniLabel);

            Options.framing = (TimelapseFraming)EditorGUILayout.EnumPopup(
                new GUIContent("Framing",
                    "Follow Artist shoots from your own viewing angle and leans towards where " +
                    "you are sculpting - smoothed, so it is a camera move rather than a replay " +
                    "of your mouse. Orbit is a turntable that ignores your camera."),
                Options.framing);

            if (Options.framing == TimelapseFraming.FollowArtist)
                DrawFollowArtistSettings();
            else
                DrawOrbitSettings();

            Options.cameraSmoothingSeconds = EditorGUILayout.Slider(
                new GUIContent("Camera Smoothing",
                    "How long the camera takes to settle on a new framing, in seconds of VIDEO - " +
                    "so it looks the same however fast the app ran or how high Speed is set."),
                Options.cameraSmoothingSeconds, 0f, 3f);

            DrawSpeedControl();

            Options.idleGraceSeconds = EditorGUILayout.Slider(
                new GUIContent("Idle Grace (s)",
                    "Keeps recording this long after the last change, so the natural gaps " +
                    "between dabs don't turn into hard cuts."),
                Options.idleGraceSeconds, 0f, 5f);
        }

        private void DrawFollowArtistSettings()
        {
            Options.focusStrength = EditorGUILayout.Slider(
                new GUIContent("Focus On Work",
                    "How far the shot recentres from your pivot onto the spot you are actually " +
                    "sculpting. 0 reproduces your framing exactly; 1 keeps the brush dead centre " +
                    "and lets the model swing around it."),
                Options.focusStrength, 0f, 1f);

            Options.focusZoom = EditorGUILayout.Slider(
                new GUIContent("Zoom",
                    "Multiplier on your own viewing distance. 1 shows what you see; below 1 " +
                    "pushes in on the work; above 1 leaves air for the recentring to move in."),
                Options.focusZoom, 0.5f, 2f);
        }

        private void DrawOrbitSettings()
        {
            Options.orbitDegreesPerSecond = EditorGUILayout.Slider(
                new GUIContent("Orbit Speed (deg/s)",
                    "Degrees per second of finished video, so the turntable stays smooth however " +
                    "stop-start the session was."),
                Options.orbitDegreesPerSecond, 0f, 90f);

            Options.distanceMultiplier = EditorGUILayout.Slider(
                new GUIContent("Distance", "1 fills the frame with the model; higher pulls back."),
                Options.distanceMultiplier, 0.5f, 4f);

            Options.orbitPitch = EditorGUILayout.Slider(
                new GUIContent("Height (deg)", "Camera elevation above the model."),
                Options.orbitPitch, -80f, 80f);

            Options.followSubject = EditorGUILayout.Toggle(
                new GUIContent("Follow Subject", "Re-frames as the model grows or you switch objects."),
                Options.followSubject);
        }

        /// Playback speed, as a number you can type AND a slider you can scrub.
        ///
        /// The slider is logarithmic because the useful range isn't: the interesting choices are
        /// 2x, 4x, 8x, 16x, and a linear 1-240 slider spends nine tenths of its travel on speeds
        /// nobody picks while cramming all of those into the first centimetre. The field beside it
        /// is there because "exactly 10x" is a thing people want and no slider gives it reliably.
        private void DrawSpeedControl()
        {
            EditorGUILayout.BeginHorizontal();
            Options.speed = Mathf.Clamp(
                EditorGUILayout.FloatField(
                    new GUIContent("Speed (x realtime)",
                        "Seconds of sculpting per second of video, on top of dropping idle time."),
                    Options.speed),
                MinSpeed, MaxSpeed);

            EditorGUI.BeginChangeCheck();
            float t = GUILayout.HorizontalSlider(SpeedToSlider(Options.speed), 0f, 1f,
                GUILayout.MinWidth(60f));
            if (EditorGUI.EndChangeCheck())
            {
                float scrubbed = SliderToSpeed(t);
                // Whole numbers below 10x, halves above - a slider that lands on 7.3183x is
                // needlessly precise for something whose only job is "roughly this fast".
                Options.speed = scrubbed < 10f
                    ? Mathf.Round(scrubbed)
                    : Mathf.Round(scrubbed * 2f) * 0.5f;
                Options.speed = Mathf.Clamp(Options.speed, MinSpeed, MaxSpeed);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(" ",
                $"one minute of sculpting → {60f / Mathf.Max(MinSpeed, Options.speed):0.#}s of video",
                EditorStyles.miniLabel);
        }

        private const float MinSpeed = 1f;
        private const float MaxSpeed = 240f;

        private static float SpeedToSlider(float speed) =>
            Mathf.Log(Mathf.Clamp(speed, MinSpeed, MaxSpeed), 2f) / Mathf.Log(MaxSpeed, 2f);

        private static float SliderToSpeed(float t) => Mathf.Pow(MaxSpeed, Mathf.Clamp01(t));

        private void DrawTransport()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play mode to record.", MessageType.Info);
                return;
            }

            using (new EditorGUI.DisabledScope(IsRecording))
                if (GUILayout.Button("Start Recording", GUILayout.Height(32f)))
                    TimelapseRecorderService.Start();

            using (new EditorGUI.DisabledScope(!IsRecording))
                if (GUILayout.Button("Stop Recording", GUILayout.Height(32f)))
                    TimelapseRecorderService.Stop();
        }

        private void DrawStatus()
        {
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);

            TimelapseRig rig = TimelapseRecorderService.Rig;
            if (IsRecording && rig != null)
            {
                EditorGUILayout.LabelField("State", rig.IsSculpting ? "Capturing" : "Paused (idle)");
                EditorGUILayout.LabelField("Captured frames", rig.CapturedFrames.ToString());
                EditorGUILayout.LabelField("Video length",
                    $"{rig.CapturedFrames / Mathf.Max(1f, Options.frameRate):0.0}s");
                EditorGUILayout.LabelField("Sculpting time", $"{rig.SculptingSeconds:0.0}s");
                EditorGUILayout.LabelField("Changes seen", SculptActivity.EditCount.ToString());
            }
            else
            {
                EditorGUILayout.LabelField("State", "Not recording");
            }

            string last = TimelapseRecorderService.LastOutputPath;
            if (!string.IsNullOrEmpty(last))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Last file", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(last, EditorStyles.wordWrappedMiniLabel,
                    GUILayout.Height(32f));
                using (new EditorGUI.DisabledScope(!File.Exists(last)))
                    if (GUILayout.Button("Show In Explorer"))
                        EditorUtility.RevealInFinder(last);
            }

            // The turntable's 360 loop is recorded from the app's Scene panel (Turntable), but
            // encoded by the same service, so its output is listed here too.
            string loop = TimelapseRecorderService.LastLoopPath;
            if (!string.IsNullOrEmpty(loop))
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Last 360 turntable loop", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(loop, EditorStyles.wordWrappedMiniLabel,
                    GUILayout.Height(32f));
                using (new EditorGUI.DisabledScope(!File.Exists(loop)))
                    if (GUILayout.Button("Show Loop In Explorer"))
                        EditorUtility.RevealInFinder(loop);
            }
        }
    }
}
