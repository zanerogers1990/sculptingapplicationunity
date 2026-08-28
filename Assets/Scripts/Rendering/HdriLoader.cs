using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sculpting
{
    /// Loads an equirectangular environment image off disk into a Texture2D at runtime.
    ///
    /// Unity has no runtime loader for high-dynamic-range image formats - ImageConversion
    /// handles PNG and JPG only - so the Radiance .hdr reader below is written out longhand.
    /// That is the format worth implementing: it is what nearly every free HDRI library ships
    /// alongside .exr, it is a genuinely simple RGBE encoding, and without it "pick an HDRI
    /// from disk" would only work for LDR images, which defeats the point of HDRI lighting
    /// (no values above 1, so no bright light sources).
    ///
    /// .exr is NOT decoded here: the format is a container with several compression schemes
    /// (PIZ in particular is a wavelet + Huffman codec) and writing a reader for it is a much
    /// larger job than this one feature justifies. In the Editor an .exr that already lives
    /// inside the project's Assets folder is loaded through the AssetDatabase instead, since
    /// Unity's own importer has already decoded it; anywhere else the caller gets a clear
    /// "convert it to .hdr" message rather than a silent failure.
    public static class HdriLoader
    {
        /// Extensions offered in the file dialog, bare and without dots.
        public static readonly string[] Extensions = { "hdr", "exr", "png", "jpg", "jpeg" };

        /// Returns the loaded texture, or null with `error` set to something worth showing the
        /// user. `isProjectAsset` is true when the result is an imported asset owned by the
        /// project rather than something allocated here - the caller must NOT Destroy those,
        /// only the ones it owns.
        public static Texture2D Load(string path, out string error, out bool isProjectAsset)
        {
            error = null;
            isProjectAsset = false;
            if (string.IsNullOrEmpty(path)) { error = "No file chosen."; return null; }
            if (!File.Exists(path)) { error = "File not found."; return null; }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                switch (ext)
                {
                    case ".hdr":
                        return LoadRadiance(path, out error);
                    case ".png":
                    case ".jpg":
                    case ".jpeg":
                        return LoadLdr(path, out error);
                    case ".exr":
                        return LoadViaAssetDatabase(path, out error, out isProjectAsset);
                    default:
                        error = "Unsupported format " + ext + ".";
                        return null;
                }
            }
            catch (Exception e)
            {
                // A malformed or truncated file must not take the app down - the previous
                // environment simply stays in place.
                error = "Could not read file: " + e.Message;
                return null;
            }
        }

        // ------------------------------------------------------------------------- LDR path

        private static Texture2D LoadLdr(string path, out string error)
        {
            error = null;
            byte[] bytes = File.ReadAllBytes(path);
            // linear:false - a PNG/JPG is sRGB-encoded, and the project renders in linear
            // space, so Unity has to do the conversion on sample.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear: false);
            if (!tex.LoadImage(bytes, markNonReadable: false))
            {
                UnityEngine.Object.Destroy(tex);
                error = "Not a valid PNG/JPG.";
                return null;
            }
            Configure(tex);
            return tex;
        }

        private static Texture2D LoadViaAssetDatabase(string path, out string error, out bool isProjectAsset)
        {
            isProjectAsset = false;
#if UNITY_EDITOR
            string projectRelative = ToProjectRelative(path);
            if (projectRelative != null)
            {
                var asset = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(projectRelative);
                if (asset != null)
                {
                    error = null;
                    isProjectAsset = true;
                    return asset;
                }
            }
            error = "EXR is only supported from inside this project's Assets folder. " +
                    "Copy it there, or use a .hdr file.";
            return null;
#else
            error = "EXR is not supported - please use a .hdr file.";
            return null;
#endif
        }

#if UNITY_EDITOR
        /// "D:/Proj/Assets/Env/sky.exr" -> "Assets/Env/sky.exr", or null if outside the project.
        private static string ToProjectRelative(string absolutePath)
        {
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            string full = Path.GetFullPath(absolutePath).Replace('\\', '/');
            if (!full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)) return null;
            return "Assets/" + full.Substring(assets.Length + 1);
        }
#endif

        private static void Configure(Texture2D tex)
        {
            // Repeat horizontally / clamp vertically is what an equirect projection wants:
            // longitude wraps, latitude does not. Getting V wrong mirrors the poles into each
            // other along the top and bottom rows.
            tex.wrapModeU = TextureWrapMode.Repeat;
            tex.wrapModeV = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
        }

        // -------------------------------------------------------------------- Radiance .hdr

        private static Texture2D LoadRadiance(string path, out string error)
        {
            error = null;
            byte[] data = File.ReadAllBytes(path);
            int p = 0;

            string magic = ReadLine(data, ref p);
            if (magic == null || !magic.StartsWith("#?"))
            {
                error = "Not a Radiance .hdr file.";
                return null;
            }

            // Header runs until a blank line. The only field that matters to us is FORMAT;
            // everything else (EXPOSURE, PRIMARIES, comments) is skipped deliberately - honouring
            // EXPOSURE would rescale the image away from the values the author intended, and the
            // controller exposes its own exposure slider on top.
            string format = null;
            while (true)
            {
                string line = ReadLine(data, ref p);
                if (line == null) { error = "Truncated header."; return null; }
                if (line.Length == 0) break;
                if (line.StartsWith("FORMAT=")) format = line.Substring(7).Trim();
            }

            if (format != null && format != "32-bit_rle_rgbe" && format != "32-bit_rle_xyze")
            {
                error = "Unsupported .hdr encoding: " + format;
                return null;
            }

            string resolution = ReadLine(data, ref p);
            if (resolution == null) { error = "Missing resolution line."; return null; }
            string[] parts = resolution.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // Only the overwhelmingly common "-Y height +X width" orientation is handled. The
            // format permits eight of these; supporting the rest would mean transposing and
            // flipping the decoded image for files that essentially never occur in the wild.
            if (parts.Length != 4 || parts[0] != "-Y" || parts[2] != "+X")
            {
                error = "Unsupported .hdr orientation: " + resolution;
                return null;
            }
            if (!int.TryParse(parts[1], out int height) || !int.TryParse(parts[3], out int width) ||
                width <= 0 || height <= 0)
            {
                error = "Bad .hdr resolution: " + resolution;
                return null;
            }
            if ((long)width * height > 64L * 1024 * 1024)
            {
                error = $"HDRI is too large ({width}x{height}).";
                return null;
            }

            // Half-precision rather than full float: an 8k HDRI is 33M pixels, which is 268MB
            // as RGBAHalf and 536MB as RGBAFloat. Half carries ~3 decimal digits and a huge
            // exponent range, which is more than enough for a light probe source.
            var halfPixels = new ushort[width * height * 4];
            var rgbe = new byte[width * 4];

            for (int y = 0; y < height; y++)
            {
                if (!ReadScanline(data, ref p, width, rgbe)) { error = "Truncated pixel data."; return null; }

                // Radiance stores top row first; Texture2D's raw data starts at the BOTTOM row,
                // so the destination row is mirrored. Skipping this flips the sky underground.
                int row = (height - 1 - y) * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int s = x * 4;
                    int e = rgbe[s + 3];
                    float r = 0f, g = 0f, b = 0f;
                    if (e != 0)
                    {
                        // ldexp(1, e - 128 - 8): the shared exponent is biased by 128, and the
                        // mantissas are 8-bit. The +0.5 is the reference implementation's
                        // round-to-centre-of-bucket.
                        float f = Mathf.Pow(2f, e - 136);
                        r = (rgbe[s] + 0.5f) * f;
                        g = (rgbe[s + 1] + 0.5f) * f;
                        b = (rgbe[s + 2] + 0.5f) * f;
                    }

                    int d = row + x * 4;
                    halfPixels[d] = Mathf.FloatToHalf(r);
                    halfPixels[d + 1] = Mathf.FloatToHalf(g);
                    halfPixels[d + 2] = Mathf.FloatToHalf(b);
                    halfPixels[d + 3] = Mathf.FloatToHalf(1f);
                }
            }

            // linear:true - .hdr values are already linear radiance, so no sRGB decode.
            var tex = new Texture2D(width, height, TextureFormat.RGBAHalf, mipChain: true, linear: true);
            tex.SetPixelData(halfPixels, 0);
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            Configure(tex);
            return tex;
        }

        /// Decodes one scanline into `rgbe` (4 bytes per pixel, interleaved). Handles the
        /// adaptive-RLE form, the old-style repeat form, and flat uncompressed data.
        private static bool ReadScanline(byte[] data, ref int p, int width, byte[] rgbe)
        {
            if (p + 4 > data.Length) return false;

            bool adaptiveRle = width >= 8 && width < 32768 &&
                               data[p] == 2 && data[p + 1] == 2 &&
                               ((data[p + 2] << 8) | data[p + 3]) == width;

            if (adaptiveRle)
            {
                p += 4;
                // Components are stored in separate planes (all reds, then all greens, ...),
                // each run-length encoded on its own.
                for (int c = 0; c < 4; c++)
                {
                    int x = 0;
                    while (x < width)
                    {
                        if (p >= data.Length) return false;
                        int count = data[p++];
                        if (count > 128)
                        {
                            count -= 128;
                            if (p >= data.Length || x + count > width) return false;
                            byte value = data[p++];
                            while (count-- > 0) rgbe[(x++) * 4 + c] = value;
                        }
                        else
                        {
                            if (count == 0 || p + count > data.Length || x + count > width) return false;
                            while (count-- > 0) rgbe[(x++) * 4 + c] = data[p++];
                        }
                    }
                }
                return true;
            }

            // Old style: 4 bytes per pixel, where an (1,1,1,n) pixel means "repeat the previous
            // pixel n << (8 * runsSoFar) times". The shift chains consecutive markers together
            // to encode runs longer than 255.
            int shift = 0;
            for (int x = 0; x < width; )
            {
                if (p + 4 > data.Length) return false;
                byte r = data[p], g = data[p + 1], b = data[p + 2], e = data[p + 3];
                p += 4;

                if (r == 1 && g == 1 && b == 1 && x > 0)
                {
                    int repeat = e << shift;
                    int prev = (x - 1) * 4;
                    for (int i = 0; i < repeat && x < width; i++, x++)
                    {
                        int d = x * 4;
                        rgbe[d] = rgbe[prev];
                        rgbe[d + 1] = rgbe[prev + 1];
                        rgbe[d + 2] = rgbe[prev + 2];
                        rgbe[d + 3] = rgbe[prev + 3];
                    }
                    shift += 8;
                }
                else
                {
                    int d = x * 4;
                    rgbe[d] = r; rgbe[d + 1] = g; rgbe[d + 2] = b; rgbe[d + 3] = e;
                    x++;
                    shift = 0;
                }
            }
            return true;
        }

        /// Reads one '\n'-terminated ASCII line, tolerating CRLF. Returns null at end of data.
        private static string ReadLine(byte[] data, ref int p)
        {
            if (p >= data.Length) return null;
            var sb = new StringBuilder(64);
            while (p < data.Length)
            {
                byte c = data[p++];
                if (c == '\n') return sb.ToString();
                if (c != '\r') sb.Append((char)c);
            }
            return sb.ToString();
        }
    }
}
