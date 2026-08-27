using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Sculpting.IO
{
    /// A native "open file" / "save file" dialog. Unity ships no runtime file picker, so this
    /// takes two routes and picks whichever is available:
    ///
    ///  - In the Editor, UnityEditor.EditorUtility's panels (guarded by UNITY_EDITOR so the
    ///    UnityEditor namespace never reaches a build, where it does not exist).
    ///  - In a Windows standalone build, the OS common dialog via comdlg32.dll. That is a
    ///    ~40-line P/Invoke against an API that has been stable since Win95, which is a much
    ///    smaller commitment than adding a third-party file-browser package for one button.
    ///
    /// Anywhere else `IsSupported` is false and callers fall back to the typed path field,
    /// which is why the field remains the primary input rather than being replaced by a button.
    public static class FileDialog
    {
        public static bool IsSupported
        {
            get
            {
#if UNITY_EDITOR
                return true;
#else
                return Application.platform == RuntimePlatform.WindowsPlayer;
#endif
            }
        }

        /// Returns the chosen path, or null if the user cancelled or no dialog is available.
        /// `extensions` are bare, without dots (e.g. "sculpt", "obj").
        public static string OpenFile(string title, string startDirectory, params string[] extensions)
        {
#if UNITY_EDITOR
            // The Editor panel takes ONE comma-separated extension list per filter entry, so
            // every accepted type is offered as a single "All Supported" row.
            string joined = string.Join(",", extensions);
            string chosen = UnityEditor.EditorUtility.OpenFilePanel(title, startDirectory, joined);
            return string.IsNullOrEmpty(chosen) ? null : chosen;
#else
            if (!IsSupported) return null;
            return WindowsOpenFile(title, startDirectory, extensions);
#endif
        }

        /// Returns the chosen path, or null if cancelled/unavailable. `defaultName` is the
        /// filename the dialog opens pre-filled with.
        public static string SaveFile(string title, string startDirectory, string defaultName, string extension)
        {
#if UNITY_EDITOR
            string chosen = UnityEditor.EditorUtility.SaveFilePanel(title, startDirectory, defaultName, extension);
            return string.IsNullOrEmpty(chosen) ? null : chosen;
#else
            if (!IsSupported) return null;
            return WindowsSaveFile(title, startDirectory, defaultName, extension);
#endif
        }

        // ------------------------------------------------------------------ Windows native

        // Laid out to match the Win32 OPENFILENAMEW struct exactly - field order and types are
        // load-bearing, since the OS reads this by offset.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileNameW
        {
            public int structSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public IntPtr filter;
            public IntPtr customFilter;
            public int maxCustFilter;
            public int filterIndex;
            public IntPtr file;
            public int maxFile;
            public IntPtr fileTitle;
            public int maxFileTitle;
            public IntPtr initialDir;
            public IntPtr title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public IntPtr defExt;
            public IntPtr custData;
            public IntPtr hook;
            public IntPtr templateName;
            public IntPtr reservedPtr;
            public int reservedInt;
            public int flagsEx;
        }

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileNameW(ref OpenFileNameW ofn);

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetSaveFileNameW(ref OpenFileNameW ofn);

        private const int OfnFileMustExist = 0x00001000;
        private const int OfnPathMustExist = 0x00000800;
        private const int OfnExplorer = 0x00080000;
        private const int OfnNoChangeDir = 0x00000008;   // leave the process CWD alone
        private const int OfnOverwritePrompt = 0x00000002;
        private const int MaxPathChars = 1024;

        private static string WindowsOpenFile(string title, string startDirectory, string[] extensions)
        {
            return RunDialog(title, startDirectory, null, BuildFilter(extensions), null,
                OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir, open: true);
        }

        private static string WindowsSaveFile(string title, string startDirectory, string defaultName, string extension)
        {
            return RunDialog(title, startDirectory, defaultName, BuildFilter(new[] { extension }), extension,
                OfnExplorer | OfnPathMustExist | OfnOverwritePrompt | OfnNoChangeDir, open: false);
        }

        private static string RunDialog(string title, string startDirectory, string defaultName,
                                        string filter, string defExt, int flags, bool open)
        {
            // Every buffer is unmanaged and explicitly freed. The widely-copied version of this
            // P/Invoke declares `lpstrFile` as a `string` and mutates it in place, which relies
            // on interned-string mutation the runtime does not guarantee; allocating real
            // buffers avoids that entirely.
            IntPtr fileBuffer = IntPtr.Zero, filterPtr = IntPtr.Zero, titlePtr = IntPtr.Zero,
                   dirPtr = IntPtr.Zero, defExtPtr = IntPtr.Zero;
            try
            {
                fileBuffer = Marshal.AllocHGlobal(MaxPathChars * sizeof(char));
                // Pre-fill with the default name (save) or an empty string (open), then
                // zero-terminate - the API reads this buffer as input as well as writing to it.
                string initial = defaultName ?? string.Empty;
                for (int i = 0; i < MaxPathChars; i++)
                {
                    char c = i < initial.Length ? initial[i] : '\0';
                    Marshal.WriteInt16(fileBuffer, i * sizeof(char), c);
                }

                filterPtr = Marshal.StringToHGlobalUni(filter);
                titlePtr = Marshal.StringToHGlobalUni(title);
                dirPtr = Marshal.StringToHGlobalUni(startDirectory ?? string.Empty);
                if (!string.IsNullOrEmpty(defExt)) defExtPtr = Marshal.StringToHGlobalUni(defExt);

                var ofn = new OpenFileNameW
                {
                    structSize = Marshal.SizeOf(typeof(OpenFileNameW)),
                    filter = filterPtr,
                    file = fileBuffer,
                    maxFile = MaxPathChars,
                    initialDir = dirPtr,
                    title = titlePtr,
                    defExt = defExtPtr,
                    filterIndex = 1,
                    flags = flags,
                };

                bool ok = open ? GetOpenFileNameW(ref ofn) : GetSaveFileNameW(ref ofn);
                // A false return is overwhelmingly just "user pressed Cancel", which is not an
                // error worth surfacing - so this reports nothing either way.
                if (!ok) return null;

                string result = Marshal.PtrToStringUni(fileBuffer);
                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch (Exception e)
            {
                // A missing comdlg32 or a marshalling mistake must not take the app down - the
                // typed path field still works, so degrade to it.
                Debug.LogWarning("Native file dialog unavailable: " + e.Message);
                return null;
            }
            finally
            {
                if (fileBuffer != IntPtr.Zero) Marshal.FreeHGlobal(fileBuffer);
                if (filterPtr != IntPtr.Zero) Marshal.FreeHGlobal(filterPtr);
                if (titlePtr != IntPtr.Zero) Marshal.FreeHGlobal(titlePtr);
                if (dirPtr != IntPtr.Zero) Marshal.FreeHGlobal(dirPtr);
                if (defExtPtr != IntPtr.Zero) Marshal.FreeHGlobal(defExtPtr);
            }
        }

        /// Win32 filter strings are a run of NUL-separated "label\0pattern\0" pairs ending in a
        /// DOUBLE NUL - not a normal C string. Built here rather than inline because getting
        /// the terminator wrong shows an empty or truncated file-type dropdown.
        private static string BuildFilter(string[] extensions)
        {
            var sb = new System.Text.StringBuilder();
            if (extensions != null && extensions.Length > 0)
            {
                var patterns = new string[extensions.Length];
                for (int i = 0; i < extensions.Length; i++) patterns[i] = "*." + extensions[i];
                string all = string.Join(";", patterns);

                sb.Append("Supported files (").Append(all).Append(')').Append('\0').Append(all).Append('\0');
                foreach (string ext in extensions)
                    sb.Append(ext.ToUpperInvariant()).Append(" files (*.").Append(ext).Append(')')
                      .Append('\0').Append("*.").Append(ext).Append('\0');
            }
            sb.Append("All files (*.*)").Append('\0').Append("*.*").Append('\0').Append('\0');
            return sb.ToString();
        }

        /// Sensible starting folder for a dialog: the directory of whatever path is currently in
        /// the field, falling back to the app's own save folder.
        public static string DirectoryFor(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
            catch { /* malformed path in the field - fall through */ }
            return SceneSerializer.DefaultDirectory;
        }
    }
}
