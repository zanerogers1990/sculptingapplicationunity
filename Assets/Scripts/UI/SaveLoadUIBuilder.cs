using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Sculpting.IO;

namespace Sculpting
{
    /// Top-center panel: three actions - bring a model in, open a saved scene, save the current
    /// one. Top-center is the one screen edge the other panels left free (brush top-left,
    /// lighting top-right, presentation bottom-right, material bottom-center, scene graph
    /// left-middle - see MaterialUIBuilder's documented collision history).
    ///
    /// Each button opens the OS file picker rather than acting on a typed path, so there is no
    /// path field in the normal case. A field appears only where no picker exists (see
    /// FileDialog.IsSupported), which would otherwise leave the panel with no way to name a file
    /// at all.
    ///
    /// Start(), not Awake(), for the reason every builder here uses it: initial state is read
    /// off controllers that resolve through SelectionManager.PrimarySelection, which needs every
    /// SculptableMesh's OnEnable to have run (see SelectionManager's class remarks).
    public class SaveLoadUIBuilder : MonoBehaviour
    {
        private const float PanelWidth = 300f;
        // How long a success line stays up before the hint returns. Failures ignore this and
        // stay until the next action - an error the user blinked past is worse than a stale line.
        private const float StatusHoldSeconds = 5f;

        private static readonly Color OkColor = new Color(0.55f, 0.85f, 0.55f);
        private static readonly Color ErrorColor = new Color(0.95f, 0.45f, 0.4f);
        private static readonly Color HintColor = new Color(0.65f, 0.65f, 0.7f);

        private Text _statusLabel;
        private float _statusClearAt = -1f;

        // Only created when there's no OS picker; null otherwise. Every path-producing step goes
        // through PickPath/PickSavePath so the rest of the panel never has to care which it is.
        private InputField _fallbackField;

        // Remembered between actions so the picker reopens where the user last was, rather than
        // resetting to the app's save folder every time.
        private string _lastDirectory;

        private void Start()
        {
            Transform panel = UIFactory.CreatePanelCanvas(
                transform, "SaveLoadCanvas", new Vector2(0.5f, 1f), new Vector2(0, -12), PanelWidth);

            UIFactory.CreateLabel(panel, "Scene", 14, FontStyle.Bold);

            UIFactory.CreateButton(panel, "Import Object...", ImportObject);
            UIFactory.CreateButton(panel, "Load Scene...", LoadScene);
            UIFactory.CreateButton(panel, "Save Scene...", SaveScene);

            if (!FileDialog.IsSupported)
            {
                UIFactory.CreateLabel(panel, "File path", 11, FontStyle.Normal);
                _fallbackField = UIFactory.CreateInputField(panel, SceneSerializer.DefaultPath, null);
            }

            _statusLabel = UIFactory.CreateLabel(panel, string.Empty, 11, FontStyle.Normal);
            ShowHint();
        }

        private void Update()
        {
            if (_statusClearAt > 0f && Time.unscaledTime >= _statusClearAt) ShowHint();
        }

        // --------------------------------------------------------------------------- actions

        /// Brings a single model in alongside whatever is already in the scene. Separate from
        /// Load Scene precisely because it never asks a question: adding a model to what you are
        /// working on is the only thing it can sensibly mean.
        private void ImportObject()
        {
            string path = PickPath("Import object", "obj");
            if (path == null) return;

            if (SceneSerializer.ImportAny(path, out int count, out string error))
                SetStatus($"Imported {Path.GetFileName(path)}", OkColor, hold: true);
            else
                SetStatus("Import failed: " + error, ErrorColor, hold: false);
        }

        /// Opens a saved scene, then asks how to bring it in. The prompt exists because both
        /// answers are reasonable and one of them is destructive: replacing discards everything
        /// currently in the scene, and there is no undo for that. Asking after the file is
        /// chosen (rather than offering two buttons up front) keeps the panel to one obvious
        /// action and puts the question at the moment it can be answered concretely - the file's
        /// own name and object count are in the prompt.
        private void LoadScene()
        {
            string path = PickPath("Load scene", "sculpt");
            if (path == null) return;

            string name = Path.GetFileName(path);
            UIFactory.ShowModal(transform,
                $"\"{name}\"\n\nReplace everything in the scene, or add its objects to what you have?",
                null,
                new UIFactory.ModalChoice("Add to current scene", () =>
                {
                    if (SceneSerializer.ImportAny(path, out int count, out string error))
                        SetStatus($"Added {count} object{(count == 1 ? "" : "s")} from {name}", OkColor, hold: true);
                    else
                        SetStatus("Load failed: " + error, ErrorColor, hold: false);
                }),
                new UIFactory.ModalChoice("Replace scene (cannot be undone)", () =>
                {
                    if (SceneSerializer.Load(path, out string error))
                    {
                        SetStatus("Loaded " + name, OkColor, hold: true);
                        // Only the replacing path needs this: it restores brush/material/
                        // lighting/camera wholesale, so every other panel's controls are showing
                        // values that no longer apply. Adding objects changes no global setting.
                        RebuildOtherPanels();
                    }
                    else
                    {
                        SetStatus("Load failed: " + error, ErrorColor, hold: false);
                    }
                }));
        }

        private void SaveScene()
        {
            string path = PickSavePath();
            if (path == null) return;

            if (SceneSerializer.Save(path, out string error))
                SetStatus($"Saved {Path.GetFileName(path)} ({FileSizeMb(path)})", OkColor, hold: true);
            else
                SetStatus("Save failed: " + error, ErrorColor, hold: false);
        }

        // ----------------------------------------------------------------------- path picking

        /// The OS picker where there is one, the fallback field otherwise. Returns null when the
        /// user cancels, which every caller treats as "do nothing" - deliberately NOT an error,
        /// since cancelling is a normal thing to do.
        private string PickPath(string title, params string[] extensions)
        {
            if (FileDialog.IsSupported)
            {
                string chosen = FileDialog.OpenFile(title, StartDirectory(), extensions);
                if (!string.IsNullOrEmpty(chosen)) _lastDirectory = FileDialog.DirectoryFor(chosen);
                return string.IsNullOrEmpty(chosen) ? null : chosen;
            }

            // Typed paths are used verbatim - no extension is appended, because this same field
            // has to be able to name a .obj as well as a .sculpt.
            string typed = _fallbackField != null ? _fallbackField.text?.Trim().Trim('"') : null;
            if (string.IsNullOrEmpty(typed)) { SetStatus("Type a file path first.", ErrorColor, hold: false); return null; }
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
            string typed = _fallbackField != null ? _fallbackField.text : null;
            return SceneSerializer.NormalizePath(typed);
        }

        private string StartDirectory() =>
            string.IsNullOrEmpty(_lastDirectory) ? SceneSerializer.DefaultDirectory : _lastDirectory;

        // ------------------------------------------------------------------------------- misc

        // Destroys and re-runs every other UI builder's panel so their controls show the loaded
        // values rather than the ones they were built from at startup.
        //
        // Works per HOST GAMEOBJECT, not per builder component, because four builders
        // (Sculpt/Material/Lighting/PostProcessing) share the single "SculptUI" object.
        // SendMessage hits every component on the object, so driving this per-component would
        // rebuild those four panels four times each. SendMessage is used at all because Start is
        // private on each builder - the same "invoke a private MonoBehaviour method without
        // reflection" idiom used elsewhere in this project.
        private void RebuildOtherPanels()
        {
            var hosts = new System.Collections.Generic.List<GameObject>();
            void AddHost(MonoBehaviour b)
            {
                // Excluded by GameObject rather than by component so this panel survives.
                if (b == null || b.gameObject == gameObject) return;
                if (!hosts.Contains(b.gameObject)) hosts.Add(b.gameObject);
            }

            AddHost(FindFirstObjectByType<SculptUIBuilder>());
            AddHost(FindFirstObjectByType<SceneGraphUIBuilder>());
            AddHost(FindFirstObjectByType<MaterialUIBuilder>());
            AddHost(FindFirstObjectByType<LightingUIBuilder>());
            AddHost(FindFirstObjectByType<PostProcessingUIBuilder>());

            foreach (GameObject host in hosts)
            {
                // Destroys ONLY direct children carrying a Canvas - i.e. the panels these
                // builders created. A blanket "destroy all children" would be wrong: the
                // scene-graph builder shares its GameObject with TransformGizmo, which parents
                // its live handle hierarchy to that same transform, so clearing everything would
                // delete the gizmo out from under the user.
                //
                // DestroyImmediate, and rebuilt synchronously right after, rather than Destroy
                // plus a next-frame coroutine. Destroy is deferred to end of frame, so a
                // same-frame rebuild would leave the old panels alive alongside the new ones -
                // which forced the rebuild into a coroutine, and made the whole thing depend on
                // a frame elapsing. Doing it immediately removes that ordering hazard entirely
                // and, just as usefully, makes the result observable the moment this returns.
                for (int i = host.transform.childCount - 1; i >= 0; i--)
                {
                    Transform child = host.transform.GetChild(i);
                    if (child.GetComponent<Canvas>() != null) DestroyImmediate(child.gameObject);
                }
                host.SendMessage("Start", SendMessageOptions.DontRequireReceiver);
            }
        }

        private static string FileSizeMb(string path)
        {
            try { return (new FileInfo(path).Length / 1024f / 1024f).ToString("F1") + " MB"; }
            catch { return "saved"; }
        }

        private void SetStatus(string message, Color color, bool hold)
        {
            _statusLabel.text = message;
            _statusLabel.color = color;
            _statusClearAt = hold ? Time.unscaledTime + StatusHoldSeconds : -1f;
        }

        private void ShowHint()
        {
            _statusLabel.text = "Import adds a model (.obj). Load opens a saved scene (.sculpt).";
            _statusLabel.color = HintColor;
            _statusClearAt = -1f;
        }
    }
}
