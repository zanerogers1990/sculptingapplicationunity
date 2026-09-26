using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Sculpting
{
    /// Every pointer position the Input System received during a frame, instead of only where the
    /// pointer ended up.
    ///
    /// A mouse reports at 125-1000 Hz and a pen at 100-200 Hz, several times a rendered frame, but
    /// a brush that reads `Mouse.current.position` once per Update sees one point per frame - so a
    /// fast curved stroke reached the brush as a polygon with a corner at every frame, and pen
    /// pressure stepped once per frame. This listens to InputSystem.onEvent and keeps each Mouse or
    /// Pen state event's position (and pen pressure/tip) with its timestamp; BeginFrame hands the
    /// frame's samples over, oldest first, always ending with the pointer's current state.
    ///
    /// The Input System merges consecutive pointer events that differ only in position ("redundant
    /// event merging", on by default) - exactly the samples wanted here - so the sampler switches
    /// that off while enabled and restores it afterwards.
    ///
    /// Brush-agnostic: Clay consumes it today; any stroke brush can read the same samples.
    public sealed class PointerSampler
    {
        public struct Sample
        {
            /// Screen pixels, same space as Pointer.position.
            public Vector2 Position;
            /// The pen's raw 0-1 pressure axis; 1 for a mouse.
            public float RawPressure;
            public bool FromPen;
            /// Input System clock (the same one InputEvent.time and InputState.currentTime use).
            public double Time;
        }

        // Only reached if nothing drains the buffer for a long time (e.g. the owner disabled
        // mid-stroke without calling Disable); dropping the backlog is harmless then.
        private const int MaxPending = 4096;

        private readonly List<Sample> _pending = new List<Sample>(64);
        private readonly List<Sample> _frame = new List<Sample>(64);
        private bool _enabled;
        private bool _mergingWasDisabled;

        /// This frame's samples, oldest first; the last one is always the pointer's current state.
        /// Empty only when there is no pointer at all.
        public IReadOnlyList<Sample> Samples => _frame;

        public void Enable()
        {
            if (_enabled) return;
            _enabled = true;
            _pending.Clear();
            InputSystem.onEvent += OnEvent;
            InputSettings settings = InputSystem.settings;
            if (settings != null)
            {
                _mergingWasDisabled = settings.disableRedundantEventsMerging;
                settings.disableRedundantEventsMerging = true;
            }
        }

        public void Disable()
        {
            if (!_enabled) return;
            _enabled = false;
            InputSystem.onEvent -= OnEvent;
            InputSettings settings = InputSystem.settings;
            if (settings != null) settings.disableRedundantEventsMerging = _mergingWasDisabled;
            _pending.Clear();
            _frame.Clear();
        }

        private void OnEvent(InputEventPtr eventPtr, InputDevice device)
        {
            // Mouse and Pen only - a touchscreen is also a Pointer, but nothing here drives a
            // brush with touch.
            bool pen = device is Pen;
            if (!pen && !(device is Mouse)) return;
            if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>()) return;

            var pointer = (Pointer)device;
            // A delta event that carries no position (a button, a scroll) is not a sample.
            if (!pointer.position.ReadValueFromEvent(eventPtr, out Vector2 position)) return;

            var sample = new Sample { Position = position, RawPressure = 1f, FromPen = pen, Time = eventPtr.time };
            if (pen && !((Pen)device).pressure.ReadValueFromEvent(eventPtr, out sample.RawPressure))
                sample.RawPressure = ((Pen)device).pressure.ReadValue();

            if (_pending.Count >= MaxPending) _pending.Clear();
            _pending.Add(sample);
        }

        /// Moves the samples received since the last call into Samples. With the pen tip down only
        /// pen samples are kept - Windows also moves the mouse cursor from the pen, and those
        /// emulated mouse events would duplicate every sample without pressure - otherwise only
        /// mouse samples. A final sample at the pointer's CURRENT state is always appended, so a
        /// frame with no events (a pointer that isn't moving) still has one, stamped with the
        /// current time.
        public void BeginFrame(Mouse mouse, Pen pen)
        {
            _frame.Clear();
            bool penActive = pen != null && pen.tip.isPressed;
            for (int i = 0; i < _pending.Count; i++)
                if (_pending[i].FromPen == penActive) _frame.Add(_pending[i]);
            _pending.Clear();

            if (penActive)
            {
                _frame.Add(new Sample
                {
                    Position = pen.position.ReadValue(), RawPressure = pen.pressure.ReadValue(),
                    FromPen = true, Time = InputState.currentTime,
                });
            }
            else if (mouse != null)
            {
                _frame.Add(new Sample
                {
                    Position = mouse.position.ReadValue(), RawPressure = 1f, Time = InputState.currentTime,
                });
            }
        }
    }
}
