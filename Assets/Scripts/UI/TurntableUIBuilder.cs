using System.IO;
using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// The Turntable section of the right-hand Scene panel: spin on/off, speed and direction,
    /// the clean view, and the 360 loop recorder. Everything here is also on a key (O spins, Tab
    /// hides the panels, Esc leaves the clean view or cancels a recording) - the section is for
    /// setting it up, the keys are for using it.
    ///
    /// Filled into a foldout by SceneGraphUIBuilder, the same way the Mold Maker section is, and
    /// self-installed there if the scene predates the feature. Installs the TurntableController
    /// itself for the same reason.
    public class TurntableUIBuilder : MonoBehaviour
    {
        private static readonly Color HintColor = new Color(0.65f, 0.65f, 0.7f);
        private static readonly Color RecordingColor = new Color(0.95f, 0.45f, 0.4f);

        // Output presets. 1080 on the short side for the three shapes people post turntables in
        // (landscape video, square feed post, vertical reel), plus 4K landscape.
        private static readonly (string label, int width, int height)[] Resolutions =
        {
            ("16:9", 1920, 1080),
            ("1:1", 1080, 1080),
            ("9:16", 1080, 1920),
            ("4K", 3840, 2160)
        };

        private static readonly int[] FrameRates = { 24, 30, 60 };

        private TurntableController _turntable;

        private Text _spinLabel, _cleanLabel, _speedLabel, _recordLabel, _status;
        private Image _spinImage;
        private readonly Image[] _resolutionImages = new Image[Resolutions.Length];
        private readonly Image[] _fpsImages = new Image[FrameRates.Length];
        private Image _mp4Image, _pngImage;
        private Text _loopInfo;

        // What the labels were last built from, so Update is a handful of comparisons on the
        // frames where nothing changed.
        private bool _shownSpinning, _shownClean, _shownRecording, _labelsValid;
        private string _shownStatus;

        public void BuildContent(Transform section)
        {
            _turntable = TurntableController.Install();

            GameObject row = UIFactory.CreateRow(section, 26f);
            Button spin = UIFactory.CreateButton(row.transform, "Spin (O)", () => _turntable.ToggleSpin(),
                "Turns the view around the model, like a turntable. Alt-drag still orbits while it " +
                "spins, and the spin carries on from wherever you leave it.");
            _spinImage = spin.GetComponent<Image>();
            _spinLabel = spin.GetComponentInChildren<Text>();
            Button clean = UIFactory.CreateButton(row.transform, "Hide UI (Tab)", () => _turntable.ToggleCleanView(),
                "Hides every panel, gizmo and guide so the model is shown with just its lighting " +
                "and background. Tab or Esc brings the panels back. Sculpting is paused while hidden.");
            _cleanLabel = clean.GetComponentInChildren<Text>();

            _speedLabel = UIFactory.CreateLabel(section, string.Empty, 12, FontStyle.Normal);
            UIFactory.CreateSlider(section, TurntableController.MinSpeed, 120f, _turntable.Speed,
                v => { _turntable.Speed = Mathf.Round(v); RefreshSpeedLabel(); },
                "Degrees per second. Also sets the length of a recorded 360 loop.");
            UIFactory.CreateToggle(section, "Reverse direction", _turntable.Reverse, v => _turntable.Reverse = v,
                tooltip: "Spins the other way.");

            UIFactory.CreateLabel(section, "360 Loop", 13, FontStyle.Normal);

            GameObject resRow = UIFactory.CreateRow(section, 24f);
            for (int i = 0; i < Resolutions.Length; i++)
            {
                var r = Resolutions[i];
                _resolutionImages[i] = UIFactory.CreateButton(resRow.transform, r.label,
                    () => { _turntable.SetLoopResolution(r.width, r.height); RefreshOptionButtons(); },
                    $"{r.width} x {r.height}").GetComponent<Image>();
            }

            GameObject fpsRow = UIFactory.CreateRow(section, 24f);
            for (int i = 0; i < FrameRates.Length; i++)
            {
                int fps = FrameRates[i];
                _fpsImages[i] = UIFactory.CreateButton(fpsRow.transform, $"{fps} fps",
                    () => { _turntable.SetLoopFrameRate(fps); RefreshOptionButtons(); },
                    $"{fps} frames per second in the finished loop.").GetComponent<Image>();
            }

            // MP4 needs the editor's video encoder, so a build only offers the PNG sequence and
            // this row would be a choice of one.
            if (TurntableController.VideoAvailable)
            {
                GameObject formatRow = UIFactory.CreateRow(section, 24f);
                _mp4Image = UIFactory.CreateButton(formatRow.transform, "MP4",
                    () => { _turntable.SetLoopFormat(TurntableLoopFormat.Mp4); RefreshOptionButtons(); },
                    "One H.264 video file, ready to post or loop.").GetComponent<Image>();
                _pngImage = UIFactory.CreateButton(formatRow.transform, "PNG frames",
                    () => { _turntable.SetLoopFormat(TurntableLoopFormat.PngSequence); RefreshOptionButtons(); },
                    "A folder of numbered PNG frames, for editing or a GIF tool.").GetComponent<Image>();
            }

            _loopInfo = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);
            _loopInfo.color = HintColor;

            Button record = UIFactory.CreateButton(section, "Record 360° Loop", () => _turntable.ToggleLoopRecording(),
                "Records exactly one revolution starting from the current view, at the spin speed " +
                "above, so the last frame flows straight back into the first - a seamless loop. " +
                "The panels hide while it records. Esc cancels.");
            _recordLabel = record.GetComponentInChildren<Text>();

            _status = UIFactory.CreateLabel(section, string.Empty, 11, FontStyle.Italic);
            _status.color = HintColor;

            UIFactory.CreateButton(section, "Open Recordings Folder", OpenFolder,
                "Opens the folder turntable loops and timelapses are saved to.");

            _labelsValid = false;
            RefreshSpeedLabel();
            RefreshOptionButtons();
            Refresh();
        }

        private void Update() => Refresh();

        private void Refresh()
        {
            if (_turntable == null || _spinLabel == null) return;

            bool spinning = _turntable.Spinning;
            bool clean = _turntable.CleanView;
            bool recording = TurntableController.IsRecordingLoop;

            if (!_labelsValid || spinning != _shownSpinning)
            {
                _shownSpinning = spinning;
                _spinLabel.text = spinning ? "Stop Spin (O)" : "Spin (O)";
                _spinImage.color = spinning ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            }
            if (!_labelsValid || clean != _shownClean)
            {
                _shownClean = clean;
                _cleanLabel.text = clean ? "Show UI (Tab)" : "Hide UI (Tab)";
            }
            if (!_labelsValid || recording != _shownRecording)
            {
                _shownRecording = recording;
                _recordLabel.text = recording ? "Cancel Recording (Esc)" : "Record 360° Loop";
                _recordLabel.color = recording ? RecordingColor : Color.white;
            }
            _labelsValid = true;

            string status = _turntable.Status;
            if (status != _shownStatus)
            {
                _shownStatus = status;
                _status.text = status;
            }
        }

        private void RefreshSpeedLabel()
        {
            if (_speedLabel == null) return;
            _speedLabel.text = $"Speed  {_turntable.Speed:0}°/s  ·  {_turntable.SecondsPerTurn:0.0}s per turn";
            RefreshLoopInfo();
        }

        private void RefreshOptionButtons()
        {
            for (int i = 0; i < Resolutions.Length; i++)
                _resolutionImages[i].color = Resolutions[i].width == _turntable.LoopWidth && Resolutions[i].height == _turntable.LoopHeight
                    ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            for (int i = 0; i < FrameRates.Length; i++)
                _fpsImages[i].color = FrameRates[i] == _turntable.LoopFrameRate ? UIFactory.ActiveColor : UIFactory.InactiveColor;

            bool mp4 = _turntable.LoopFormat == TurntableLoopFormat.Mp4;
            if (_mp4Image != null) _mp4Image.color = mp4 ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            if (_pngImage != null) _pngImage.color = !mp4 ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            RefreshLoopInfo();
        }

        /// What pressing Record will produce - so the length and frame count are known before
        /// committing to it, and it's clear the speed slider is what sets them.
        private void RefreshLoopInfo()
        {
            if (_loopInfo == null) return;
            string format = _turntable.LoopFormat == TurntableLoopFormat.Mp4 && TurntableController.VideoAvailable
                ? "MP4" : "PNG frames";
            _loopInfo.text = $"{_turntable.LoopWidth}x{_turntable.LoopHeight}, {_turntable.SecondsPerTurn:0.0}s, " +
                             $"{_turntable.LoopFrameCount} frames, {format}";
        }

        private static void OpenFolder()
        {
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings"));
            Directory.CreateDirectory(folder);
            Application.OpenURL("file:///" + folder.Replace('\\', '/'));
        }
    }
}
