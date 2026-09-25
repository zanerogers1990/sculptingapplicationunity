using System;
using System.IO;
using UnityEngine;

namespace Sculpting.IO
{
    /// The scene as a DOCUMENT: which file this session corresponds to, and the operations that
    /// read or write it - Save, Save As, Import, Load (replace), and the save-then-quit path.
    ///
    /// Split out of SceneGraphUIBuilder, which used to hold all of this alongside the panel that
    /// shows it. The panel still owns everything visual (the buttons, the Load and Exit prompts,
    /// the status line); this owns the state and the file work, so a hotkey can save without
    /// going through the panel (see SculptController.HandleSaveKeys).
    ///
    /// Lives on the scene-graph panel's GameObject, added by the panel at startup - the same
    /// self-installing pattern the rest of the project uses, since the scene file cannot carry
    /// wired object references.
    public class SceneDocumentController : MonoBehaviour
    {
        /// A line for the status display: the message, and whether it reports a success (held
        /// on screen for a while) or a failure/prompt (shown until replaced).
        public event Action<string, bool> Status;

        /// Where a typed path comes from on platforms with no OS file picker - the panel's
        /// fallback text field. Null or empty text means "nothing typed yet".
        public Func<string> FallbackPathText { get; set; }

        /// The file this session's scene currently corresponds to - null until the first
        /// successful Save As or a "Replace scene" Load, matching how Save/Save As are supposed
        /// to differ (see Save/SaveAs below). Deliberately NOT touched by "Add to current scene"
        /// imports, since those don't make the imported file the document Ctrl+S would overwrite.
        public string CurrentSavePath => _currentSavePath;
        private string _currentSavePath;

        private string _lastDirectory;

        /// Set right before actually closing the app, so a wantsToQuit handler's re-entry (Quit()
        /// itself raises wantsToQuit again) lets the second pass through instead of popping the
        /// exit prompt a second time over its own shutdown.
        public bool QuitConfirmed { get; private set; }

        private SculptController _controller;
        private SculptController Controller =>
            _controller != null ? _controller : (_controller = FindFirstObjectByType<SculptController>());

        /// Brings a single model in alongside whatever is already in the scene. Separate from
        /// Load Scene precisely because it never asks a question: adding a model to what you are
        /// working on is the only thing it can sensibly mean.
        public void ImportObject()
        {
            // SceneSerializer.ImportableExtensions, not a hard-coded "obj": ImportAny already
            // dispatches a .sculpt file to the whole-session importer, and the constant exists
            // to say so. Hard-coding the narrower list here meant the picker HID .sculpt files
            // from a button that has always been able to open them.
            string path = PickPath("Import object", SceneSerializer.ImportableExtensions);
            if (path == null) return;

            if (SceneSerializer.ImportAny(path, out int count, out string error))
                Status?.Invoke($"Imported {Path.GetFileName(path)}", true);
            else
                Status?.Invoke("Import failed: " + error, false);
        }

        /// Adds a saved scene's objects to the current one. Returns the object count.
        public bool AddFromScene(string path, out int count, out string error) =>
            SceneSerializer.ImportAny(path, out count, out error);

        /// Replaces the whole scene with a saved one, and makes that file the current document -
        /// same as opening a file in any other creative app: a Ctrl+S right after Load should
        /// overwrite THIS file, not ask where to save.
        public bool LoadReplacing(string path, out string error)
        {
            if (!SceneSerializer.Load(path, out error)) return false;
            _currentSavePath = path;
            return true;
        }

        /// Quick save: overwrites the current document with no prompt. Falls back to Save As
        /// the first time, when there is no current document yet to overwrite - matching how
        /// Ctrl+S behaves in most creative software. Also reachable via the Ctrl+S hotkey (see
        /// SculptController.HandleSaveKeys).
        public void Save()
        {
            if (string.IsNullOrEmpty(_currentSavePath))
            {
                SaveAs();
                return;
            }

            if (SceneSerializer.Save(_currentSavePath, out string error))
            {
                Status?.Invoke($"Saved {Path.GetFileName(_currentSavePath)} ({FileSizeMb(_currentSavePath)})", true);
                if (Controller != null) Controller.TriggerActionToast("Saved");
            }
            else
            {
                Status?.Invoke("Save failed: " + error, false);
            }
        }

        /// Always prompts for a location, then makes that the current document for subsequent
        /// quick Saves. Also reachable via the Ctrl+Shift+S hotkey (see
        /// SculptController.HandleSaveKeys).
        public void SaveAs()
        {
            string path = PickSavePath();
            if (path == null) return;

            if (SceneSerializer.Save(path, out string error))
            {
                _currentSavePath = path;
                Status?.Invoke($"Saved {Path.GetFileName(path)} ({FileSizeMb(path)})", true);
                if (Controller != null) Controller.TriggerActionToast("Saved As");
            }
            else
            {
                Status?.Invoke("Save failed: " + error, false);
            }
        }

        /// Same fallback-to-Save-As as Save, except it only actually closes the app once the file
        /// has been written - a failed save or a cancelled Save As leaves the app open rather than
        /// quitting over work that was never written to disk.
        public void ExitSaveAndQuit()
        {
            string path = _currentSavePath;
            if (string.IsNullOrEmpty(path))
            {
                path = PickSavePath();
                if (path == null) return; // cancelled - stay open
            }

            if (SceneSerializer.Save(path, out string error))
            {
                _currentSavePath = path;
                QuitNow();
            }
            else
            {
                Status?.Invoke("Save failed: " + error, false);
            }
        }

        public void QuitNow()
        {
            QuitConfirmed = true;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ----------------------------------------------------------------------- path picking

        /// The OS picker where there is one, the fallback field otherwise. Returns null when the
        /// user cancels, which every caller treats as "do nothing" - deliberately NOT an error,
        /// since cancelling is a normal thing to do.
        public string PickPath(string title, params string[] extensions)
        {
            if (FileDialog.IsSupported)
            {
                string chosen = FileDialog.OpenFile(title, StartDirectory(), extensions);
                if (!string.IsNullOrEmpty(chosen)) _lastDirectory = FileDialog.DirectoryFor(chosen);
                return string.IsNullOrEmpty(chosen) ? null : chosen;
            }

            // Typed paths are used verbatim - no extension is appended, because this same field
            // has to be able to name a model file (.obj/.stl) as well as a .sculpt.
            string typed = FallbackPathText?.Invoke()?.Trim().Trim('"');
            if (string.IsNullOrEmpty(typed)) { Status?.Invoke("Type a file path first.", false); return null; }
            return typed;
        }

        private string PickSavePath()
        {
            if (FileDialog.IsSupported)
            {
                string chosen = FileDialog.SaveFile("Save scene", StartDirectory(), "sculpt-session", "sculpt");
                if (!string.IsNullOrEmpty(chosen)) _lastDirectory = FileDialog.DirectoryFor(chosen);
                return string.IsNullOrEmpty(chosen) ? null : chosen;
            }

            // NormalizePath here (unlike PickPath) because a save target is always a .sculpt, so
            // a bare name can safely be completed into one.
            return SceneSerializer.NormalizePath(FallbackPathText?.Invoke());
        }

        private string StartDirectory() =>
            string.IsNullOrEmpty(_lastDirectory) ? SceneSerializer.DefaultDirectory : _lastDirectory;

        private static string FileSizeMb(string path)
        {
            try { return (new FileInfo(path).Length / 1024f / 1024f).ToString("F1") + " MB"; }
            catch { return "saved"; }
        }
    }
}
