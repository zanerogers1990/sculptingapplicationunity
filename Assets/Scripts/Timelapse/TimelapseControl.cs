using UnityEngine;

namespace Sculpting
{
    /// The one-way bridge that lets the app's own UI start and stop a timelapse.
    ///
    /// It has to be a bridge rather than a direct call because Unity Recorder is editor-only -
    /// its runtime assembly holds bindings and nothing else, and `RecorderController` lives in
    /// `UnityEditor.Recorder`. A button in a uGUI panel (which compiles into the RUNTIME
    /// assembly) therefore cannot start a recording itself, however much it looks like it should
    /// be able to. So the button raises a request here, and the editor side
    /// (TimelapseRecorderService) picks it up on its next editor tick and acts on it.
    ///
    /// Traffic runs both ways: the service also publishes what is actually happening, so the
    /// button can say "Stop" while recording and show a live frame count instead of guessing.
    ///
    /// A request is a ONE-SHOT toggle rather than a desired-state bool on purpose. A bool would
    /// have the button and the editor window fighting over the same variable - press Stop in the
    /// window and the button's stale `true` would immediately restart the recording.
    public static class TimelapseControl
    {
        private static bool _togglePending;

        /// True while a recording is running. Published by the editor service; always false in a
        /// standalone build, where nothing is listening.
        public static bool IsRecording { get; private set; }

        /// Short human-readable state for the button's status line - frames captured, video
        /// length, whether the gate is currently open.
        public static string Status { get; private set; } = string.Empty;

        /// Raised by the in-app button. Harmless if nothing is listening.
        public static void RequestToggle() => _togglePending = true;

        /// Editor side only. Returns true at most once per request.
        public static bool ConsumeToggleRequest()
        {
            if (!_togglePending) return false;
            _togglePending = false;
            return true;
        }

        /// Editor side only.
        public static void PublishState(bool recording, string status)
        {
            IsRecording = recording;
            Status = status ?? string.Empty;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession()
        {
            // Deliberately does NOT clear IsRecording/Status: the editor service survives entering
            // Play mode and republishes on its next tick anyway, and blanking them here would
            // make the button flash "Start" for a frame during a recording that is still running.
            _togglePending = false;
        }
    }
}
