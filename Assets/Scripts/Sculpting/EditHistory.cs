using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// The scene-wide undo/redo ORDER. SculptHistory (one per object) still holds the payloads;
    /// this holds the single chronological list of which object was edited when, and is what an
    /// undo press actually walks.
    ///
    /// Why order cannot live in the per-object histories: undo used to call
    /// SculptableMesh.Undo() on whatever happened to be selected, so undoing after switching
    /// objects walked back through the NEW object's history instead of reversing what you last
    /// did - and no per-object stack can express an edit that is not about one object at all,
    /// like skinning a ZSphere rig into a brand new mesh. Sculpt three strokes, convert a rig,
    /// sculpt two more, and the only structure that can undo those five things in the order they
    /// happened is one list covering all of them.
    ///
    /// Two step shapes:
    /// - A MESH step names a SculptableMesh; its payload is that object's own SculptHistory
    ///   entry, so the cheap delta encoding there is untouched by any of this.
    /// - A SCENE step carries its own undo/redo closures, for things that create or remove
    ///   objects. `discard` is its third: called when the step falls off the end of history or
    ///   history is cleared, so an action holding onto something (ZSphere Convert parks the
    ///   object it made rather than destroying it) can finally let go.
    ///
    /// Static, matching how the rest of this project reaches shared state without a singleton
    /// GameObject. Static state is wiped by a domain reload, which for undo history is the right
    /// outcome anyway - the geometry those entries described was reloaded with it.
    public static class EditHistory
    {
        /// Steps kept before the oldest is evicted. Adjustable from the UI (see
        /// SculptUIBuilder's Undo Steps slider) because the right number is a per-user, per-
        /// machine tradeoff: deltas are tiny, so most sessions could hold hundreds, but a
        /// session full of high-resolution Remeshes cannot.
        public static int MaxSteps
        {
            get => _maxSteps;
            set { _maxSteps = Mathf.Clamp(value, MinSteps, HardMaxSteps); TrimToLimits(); }
        }
        private static int _maxSteps = 100;

        public const int MinSteps = 5;
        public const int HardMaxSteps = 500;

        /// Hard ceiling on retained history, enforced regardless of MaxSteps. This is the real
        /// answer to "how many steps can I have" - a step is anywhere from a few hundred bytes
        /// (a small brush stroke) to ~70MB (a full snapshot of a multi-million-vertex Remesh),
        /// so a step COUNT alone cannot bound memory, and a slider that silently traded a
        /// hundred strokes for a hundred remesh snapshots would be a way to exhaust memory
        /// rather than a setting. Steps are evicted oldest-first until history fits.
        public const long MaxBytes = 512L * 1024 * 1024;

        private sealed class Step
        {
            /// The edited object, or null for a scene action.
            public SculptableMesh Target;
            public string Label;
            public Action Undo;
            public Action Redo;
            public Action Discard;
            public long SceneBytes;
        }

        private static readonly List<Step> _undo = new List<Step>();
        private static readonly List<Step> _redo = new List<Step>();

        public static bool CanUndo => _undo.Count > 0;
        public static bool CanRedo => _redo.Count > 0;
        public static int UndoDepth => _undo.Count;
        public static int RedoDepth => _redo.Count;

        // -------------------------------------------------------------------------- recording

        /// Call straight after pushing an entry onto `target`'s own SculptHistory, so the two
        /// stay in lockstep. Every one of SculptableMesh's push sites does this.
        public static void RecordMeshEdit(SculptableMesh target)
        {
            if (target == null) return;
            DiscardRedo();
            _undo.Add(new Step { Target = target, Label = "Edit" });
            TrimToLimits();
        }

        /// Records a step that is not about one object's vertices - today, skinning a ZSphere rig
        /// into a new mesh. `approxBytes` is whatever the closures are holding alive, so the
        /// memory budget can see it; pass 0 for an action that retains nothing.
        public static void RecordSceneAction(string label, Action undo, Action redo, Action discard, long approxBytes)
        {
            if (undo == null || redo == null) return;
            DiscardRedo();
            _undo.Add(new Step { Label = label, Undo = undo, Redo = redo, Discard = discard, SceneBytes = approxBytes });
            TrimToLimits();
        }

        // ------------------------------------------------------------------- undo and redo

        public static bool Undo() => TakeStep(_undo, _redo, undoing: true);
        public static bool Redo() => TakeStep(_redo, _undo, undoing: false);

        /// Pops steps off `from` until one of them actually applies, moving it to `to`.
        ///
        /// The loop (rather than a single pop) is what keeps a press from dying silently. A step
        /// can turn out to be unapplicable through no fault of the user - its object was deleted
        /// from the scene panel, or its payload was evicted by the memory budget while the step
        /// itself survived. Skipping straight past those and undoing the next real thing is what
        /// the user meant by pressing undo; stopping dead on one would look like undo had broken.
        private static bool TakeStep(List<Step> from, List<Step> to, bool undoing)
        {
            while (from.Count > 0)
            {
                int last = from.Count - 1;
                Step step = from[last];
                from.RemoveAt(last);

                if (step.Undo == null) // a mesh step - only scene steps carry closures
                {
                    // Unity's overloaded == reports a destroyed object as null here, which is
                    // exactly the deleted-from-the-scene-panel case.
                    if (step.Target == null) continue;
                    if (!(undoing ? step.Target.ApplyUndoStep() : step.Target.ApplyRedoStep())) continue;
                }
                else
                {
                    if (undoing) step.Undo(); else step.Redo();
                }

                to.Add(step);
                return true;
            }
            return false;
        }

        // -------------------------------------------------------------------------- eviction

        /// Throws away the redo chain because a fresh edit just happened. Each discarded step's
        /// payload has to go with it: for a mesh step that is the newest entry on that object's
        /// redo stack (walking newest-first here matches that stack's LIFO order), and for a
        /// scene step it is whatever its Discard closure is holding.
        private static void DiscardRedo()
        {
            for (int i = _redo.Count - 1; i >= 0; i--)
            {
                Step step = _redo[i];
                if (step.Target != null) step.Target.DropNewestRedoEntry();
                else step.Discard?.Invoke();
            }
            _redo.Clear();
        }

        private static void TrimToLimits()
        {
            while (_undo.Count > _maxSteps) EvictOldest();

            // Never trims to nothing: a single edit big enough to blow the whole budget on its
            // own (a Remesh at extreme resolution) should still be undoable exactly once, which
            // is far more useful than refusing to remember it at all.
            while (_undo.Count > 1 && TotalBytes() > MaxBytes) EvictOldest();
        }

        private static void EvictOldest()
        {
            Step step = _undo[0];
            _undo.RemoveAt(0);
            if (step.Target != null) step.Target.DropOldestUndoEntry();
            else step.Discard?.Invoke();
        }

        // Reused across calls - TrimToLimits runs on every push, and this would otherwise
        // allocate a fresh set each time.
        private static readonly HashSet<SculptableMesh> _byteScanSeen = new HashSet<SculptableMesh>();

        /// Everything history is holding alive, across every object and scene action.
        ///
        /// Summed on demand rather than tracked incrementally, deliberately. An object's retained
        /// bytes move on undo and redo as well as on push (both stacks hold payloads, and a step
        /// moves between them), so an incremental counter would need every one of those paths to
        /// remember to adjust it - and a counter that drifts wrong either evicts history nobody
        /// asked it to or stops enforcing the budget at all, with nothing visible either way.
        /// Recomputing walks at most MaxSteps entries and only runs when something is pushed.
        public static long TotalBytes()
        {
            _byteScanSeen.Clear();
            long total = 0;
            total += SumBytes(_undo);
            total += SumBytes(_redo);
            return total;
        }

        private static long SumBytes(List<Step> steps)
        {
            long total = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                Step step = steps[i];
                if (step.Target != null)
                {
                    // ApproxBytes covers that object's WHOLE history, so it must only be counted
                    // once however many steps point at it.
                    if (_byteScanSeen.Add(step.Target)) total += step.Target.HistoryBytes;
                }
                else
                {
                    total += step.SceneBytes;
                }
            }
            return total;
        }

        /// Wipes history - called when the scene is replaced wholesale by a load, where every
        /// object an entry could refer to has just been destroyed.
        public static void Clear()
        {
            for (int i = _redo.Count - 1; i >= 0; i--)
                if (_redo[i].Target == null) _redo[i].Discard?.Invoke();
            for (int i = _undo.Count - 1; i >= 0; i--)
                if (_undo[i].Target == null) _undo[i].Discard?.Invoke();

            _undo.Clear();
            _redo.Clear();

            foreach (SculptableMesh mesh in UnityEngine.Object.FindObjectsByType<SculptableMesh>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                mesh.ClearHistory();
        }

        /// One-line summary for the UI - steps held and what they cost. Worth showing: the whole
        /// reason MaxSteps is adjustable is that its memory cost is invisible otherwise.
        public static string Summary()
        {
            long bytes = TotalBytes();
            float mb = bytes / (1024f * 1024f);
            return $"Undo: {_undo.Count}/{_maxSteps} steps, {mb:0.#} MB";
        }
    }
}
