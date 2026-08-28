using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sculpting
{
    /// The set of matcap images the Material panel can offer, discovered from disk rather than
    /// compiled in, so a user can drop their own in and have them show up.
    ///
    /// Images live under StreamingAssets/Matcaps (see the README written there): that folder is
    /// copied verbatim into a build and is readable with ordinary file IO at runtime, which is
    /// what makes "bring your own" work in a shipped player and not just in the editor. They are
    /// deliberately NOT imported as Unity texture assets - an imported asset can't be added to
    /// after the build, which is the whole point here.
    ///
    /// Loading is two-tier because 50-odd 512x512 images decompress to ~50MB if they're all
    /// held at full size: every entry gets a small thumbnail for the palette, and only the
    /// SELECTED matcap is kept at full resolution.
    public static class MatcapLibrary
    {
        /// Size of the palette thumbnails. 64px is comfortably more than the ~34px buttons need
        /// on a high-DPI display, and keeps the whole palette under a megabyte.
        private const int ThumbnailSize = 64;

        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp" };

        /// One matcap on disk. Textures hang off the entry so a rescan that finds the same file
        /// again doesn't re-decode it.
        public class Entry
        {
            public string Path;
            /// Folder name under Matcaps/, or "Matcaps" for images sitting directly in it. Used
            /// as the palette's grouping heading.
            public string Category;
            /// File name without extension - what the UI shows and what the save file stores.
            public string Name;

            [NonSerialized] public Texture2D Thumbnail;
            [NonSerialized] public Texture2D Full;
        }

        private static List<Entry> _entries;
        private static Entry _loadedFull;

        /// Where bundled and user matcaps live. Created on demand so an "Import Matcap..." into a
        /// player that shipped without the folder still has somewhere to put the file.
        public static string RootFolder => Path.Combine(Application.streamingAssetsPath, "Matcaps");

        /// Every matcap found on the last scan, scanning once if nothing has yet. Cheap - a
        /// scan only walks the directory, it decodes nothing.
        public static IReadOnlyList<Entry> Entries
        {
            get
            {
                // Rebuild-if-null, not build-once: a script recompile mid-Play drops statics,
                // and this list is read every time the palette is rebuilt.
                if (_entries == null) Rescan();
                return _entries;
            }
        }

        /// Re-walks the folder, keeping already-decoded textures for files that are still there.
        /// Called on first use and from the panel's "Rescan Folder" button, so a user can drop an
        /// image in and pick it up without restarting.
        public static void Rescan()
        {
            var previous = _entries;
            var found = new List<Entry>();

            string root = RootFolder;
            if (Directory.Exists(root))
            {
                foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (!IsImage(path)) continue;

                    string dir = Path.GetDirectoryName(path);
                    string category = string.Equals(Path.GetFullPath(dir), Path.GetFullPath(root),
                                                    StringComparison.OrdinalIgnoreCase)
                        ? "Matcaps"
                        : Path.GetFileName(dir);

                    found.Add(new Entry
                    {
                        Path = path,
                        Category = category,
                        Name = Path.GetFileNameWithoutExtension(path)
                    });
                }
            }

            // Category first, then name, so the palette's order is stable across runs -
            // Directory.EnumerateFiles makes no ordering guarantee of its own.
            found.Sort((a, b) =>
            {
                int byCategory = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                return byCategory != 0
                    ? byCategory
                    : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            if (previous != null)
            {
                // Carry decoded textures across from the matching old entry. Without this a
                // rescan would silently orphan every thumbnail it had already paid for, and
                // (worse) drop the full-size texture out from under the live material.
                var byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (Entry old in previous) byPath[old.Path] = old;

                foreach (Entry entry in found)
                {
                    if (!byPath.TryGetValue(entry.Path, out Entry old)) continue;
                    entry.Thumbnail = old.Thumbnail;
                    entry.Full = old.Full;
                    byPath.Remove(entry.Path);
                    if (old == _loadedFull) _loadedFull = entry;
                }

                // Whatever is left in byPath is a file that has gone away since the last scan.
                foreach (Entry gone in byPath.Values)
                {
                    if (gone == _loadedFull) _loadedFull = null;
                    if (gone.Thumbnail != null) UnityEngine.Object.Destroy(gone.Thumbnail);
                    if (gone.Full != null) UnityEngine.Object.Destroy(gone.Full);
                }
            }

            _entries = found;
        }

        /// The entry whose Name matches, or null. Names come from file names, so this is how a
        /// saved scene finds its matcap again - by name rather than by full path, so a .sculpt
        /// file still resolves on a machine where the app is installed somewhere else.
        public static Entry Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Entry entry in Entries)
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    return entry;
            return null;
        }

        /// Decodes the palette thumbnail for one entry if it hasn't been decoded yet. Returns
        /// null if the file can't be read. Callers driving the whole palette should spread these
        /// across frames - decoding fifty 512x512 PNGs back to back is a visible hitch.
        public static Texture2D GetThumbnail(Entry entry)
        {
            if (entry == null) return null;
            if (entry.Thumbnail != null) return entry.Thumbnail;

            Texture2D full = Decode(entry.Path);
            if (full == null) return null;

            entry.Thumbnail = Downsample(full, ThumbnailSize);
            entry.Thumbnail.name = "MatcapThumb_" + entry.Name;
            // The full-size decode was only ever scratch space for the thumbnail. Selecting this
            // matcap later re-decodes it; that costs one image decode at click time, against
            // holding ~50MB of matcaps nobody picked.
            UnityEngine.Object.Destroy(full);
            return entry.Thumbnail;
        }

        /// Decodes the full-resolution texture to hand to the shader, releasing whichever matcap
        /// was previously selected. Only one is ever held at full size.
        public static Texture2D GetFull(Entry entry)
        {
            if (entry == null) return null;
            if (entry.Full != null) return entry.Full;

            Texture2D texture = Decode(entry.Path);
            if (texture == null) return null;
            texture.name = "Matcap_" + entry.Name;

            if (_loadedFull != null && _loadedFull != entry && _loadedFull.Full != null)
            {
                UnityEngine.Object.Destroy(_loadedFull.Full);
                _loadedFull.Full = null;
            }
            entry.Full = texture;
            _loadedFull = entry;
            return texture;
        }

        /// Copies an image from anywhere on disk into the Matcaps folder and returns its new
        /// entry - the "Import Matcap..." path. Copying rather than referencing the original is
        /// what makes an imported matcap outlive the session: a saved scene stores only the name,
        /// and a path into the user's Downloads folder is not one that survives.
        /// Returns null with the reason in `error` on failure.
        public static Entry Import(string sourcePath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                error = "File not found.";
                return null;
            }
            if (!IsImage(sourcePath))
            {
                error = "Not an image (need .png, .jpg, .tga or .bmp).";
                return null;
            }

            // Decode before copying, so an unreadable or corrupt image is refused rather than
            // left sitting in the folder as an entry that can never be selected.
            Texture2D probe = Decode(sourcePath);
            if (probe == null)
            {
                error = "Could not read that image.";
                return null;
            }
            UnityEngine.Object.Destroy(probe);

            string destination;
            try
            {
                string folder = Path.Combine(RootFolder, "Imported");
                Directory.CreateDirectory(folder);
                destination = UniquePath(folder, Path.GetFileNameWithoutExtension(sourcePath),
                                         Path.GetExtension(sourcePath));
                File.Copy(sourcePath, destination);
            }
            catch (Exception e)
            {
                error = "Could not copy into the Matcaps folder: " + e.Message;
                return null;
            }

            Rescan();
            Entry imported = null;
            foreach (Entry entry in _entries)
                if (string.Equals(entry.Path, destination, StringComparison.OrdinalIgnoreCase))
                    imported = entry;

            if (imported == null) error = "Copied the file but could not find it again.";
            return imported;
        }

        /// A file name in `folder` that isn't taken, by appending " 2", " 3"... Importing two
        /// different images that happen to share a file name has to give two matcaps, not one
        /// overwriting the other - and names are how saved scenes refer to matcaps.
        private static string UniquePath(string folder, string baseName, string extension)
        {
            string candidate = Path.Combine(folder, baseName + extension);
            int suffix = 2;
            while (File.Exists(candidate))
                candidate = Path.Combine(folder, $"{baseName} {suffix++}{extension}");
            return candidate;
        }

        private static bool IsImage(string path)
        {
            string extension = Path.GetExtension(path);
            foreach (string allowed in ImageExtensions)
                if (string.Equals(extension, allowed, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static Texture2D Decode(string path)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Matcap] Could not read {path}: {e.Message}");
                return null;
            }

            // linear:false - a matcap is authored colour, so it wants the sRGB->linear
            // conversion on sample that any other albedo-ish texture gets.
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                Debug.LogWarning($"[Matcap] Not a readable image: {path}");
                return null;
            }

            // Clamp, not repeat: the shader's UV reaches the very edge of the image at the
            // silhouette, and wrapping there fringes the outline with the opposite edge of the
            // matcap - a bright rim on a dark sphere, which reads as a rendering bug.
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.Apply(true, false);
            return texture;
        }

        /// Box-filtered downscale to `size` square. Mip-biased bilinear sampling would be a
        /// one-liner but leaves thumbnails visibly aliased at this reduction ratio (512 -> 64).
        private static Texture2D Downsample(Texture2D source, int size)
        {
            var result = new Texture2D(size, size, TextureFormat.RGBA32, false, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color32[] src = source.GetPixels32();
            var dst = new Color32[size * size];
            int sw = source.width, sh = source.height;

            for (int y = 0; y < size; y++)
            {
                int y0 = y * sh / size, y1 = Mathf.Max(y0 + 1, (y + 1) * sh / size);
                for (int x = 0; x < size; x++)
                {
                    int x0 = x * sw / size, x1 = Mathf.Max(x0 + 1, (x + 1) * sw / size);
                    int r = 0, g = 0, b = 0, a = 0, n = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        int row = sy * sw;
                        for (int sx = x0; sx < x1; sx++)
                        {
                            Color32 c = src[row + sx];
                            r += c.r; g += c.g; b += c.b; a += c.a;
                            n++;
                        }
                    }
                    dst[y * size + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            }

            result.SetPixels32(dst);
            result.Apply(false, false);
            return result;
        }
    }
}
