using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Sculpting.IO
{
    /// Reads an STL (binary or ASCII) into a Mesh ready to be sculpted - the sibling of
    /// ObjImporter, and the format everything in the 3D-printing and CAD world speaks. Both
    /// encodings share the extension and are routinely mislabelled, so the format is sniffed
    /// from the file's own bytes rather than trusted from the header text (see LooksBinary).
    ///
    /// Two things separate this from ObjImporter, and both matter:
    ///
    ///  - STL IS A TRIANGLE SOUP. There are no shared vertices in the format at all: every
    ///    facet writes out its own three corners in full, so a closed model arrives with three
    ///    times as many vertices as it has triangles and not one of them connected to another.
    ///    Handing that to SculptableMesh unchanged would look correct standing still and then
    ///    explode on the first brush stroke - adjacency, smoothing, remeshing and symmetry all
    ///    assume neighbouring triangles share vertices, so a soup tears apart along EVERY edge.
    ///    Welding coincident corners back together is therefore not an optimisation here, it is
    ///    what makes the mesh sculptable (see Weld).
    ///
    ///  - STL IS Z-UP. OBJ files arrive already converted to Y-up because every exporter does
    ///    that conversion on the way out, which is why ObjImporter only has to undo a handedness
    ///    flip. STL has no such convention: it is written in the authoring app's own right-handed
    ///    Z-up space, which is what CAD, slicers and Blender's STL exporter all emit. So the axes
    ///    are remapped on the way in (see ToUnity) rather than left alone.
    ///
    /// Facet normals are read but only used to fix winding (see Import); the shading normals are
    /// recalculated from the welded geometry, for the same reason ObjImporter recalculates them -
    /// a normal carried over from the soup would describe the unwelded surface.
    public static class StlImporter
    {
        /// Returns null and sets `error` on failure. `mesh` is left unattached to any
        /// GameObject - the caller decides what to do with it.
        public static Mesh Import(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) { error = "No file at " + path; return null; }

                // Corner stream: three consecutive entries per triangle, already wound and
                // axis-converted for Unity but NOT yet welded. Kept flat rather than as a list
                // of triangles because welding wants one linear pass over it.
                List<Vector3> corners = LooksBinary(path) ? ReadBinary(path, out error)
                                                          : ReadAscii(path, out error);
                if (corners == null) return null;
                if (corners.Count < 3)
                {
                    error = "No triangles found - is this really an .stl?";
                    return null;
                }

                Weld(corners, out Vector3[] vertices, out int[] triangles);
                if (triangles.Length == 0)
                {
                    // Every facet collapsed during the weld, which means the file is a pile of
                    // zero-area slivers. Nothing useful can be sculpted out of that.
                    error = "Every triangle in the file is degenerate.";
                    return null;
                }

                var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
                // Required above 65535 vertices. An STL of anything organic is past that before
                // it is interesting, so this is the normal case here rather than the exception.
                if (vertices.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = vertices;
                mesh.triangles = triangles;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        // ------------------------------------------------------------------ axis conversion

        /// STL's right-handed Z-up space to Unity's left-handed Y-up: (x, y, z) -> (-x, z, -y).
        ///
        /// That is deliberately not a conversion of its own - it is the composition of the two
        /// this codebase already relies on, which is what makes an OBJ and an STL of the same
        /// model land in the same orientation:
        ///
        ///   1. Z-up right-handed to Y-UP right-handed: (x, y, z) -> (x, z, -y). Exactly the
        ///      change of basis a DCC app applies on its way OUT to an OBJ, and the step STL
        ///      files never get because the format has no Y-up convention to convert to.
        ///   2. Y-up right-handed to Unity: negate X. The identical flip ObjImporter applies,
        ///      for the identical reason - see its remarks.
        ///
        /// Pick either half on its own and the model still stands upright and unmirrored, but a
        /// half-turn away from where the OBJ importer would have put it, so the two paths would
        /// quietly disagree about which way a face points.
        ///
        /// The map is exact: negating and swapping move float bits without touching them, so a
        /// model that was welded stays welded and one that was symmetric stays bit-for-bit
        /// symmetric. It has determinant -1 (a handedness flip, which is the point), so triangle
        /// winding is reversed at the call site to keep faces pointing outward.
        ///
        /// X maps to X (negated), which matters beyond orientation: the mirror/symmetry plane is
        /// local X (see MirrorController), and negating an axis leaves its zero plane exactly
        /// where it was. A model built symmetric about its authoring app's X=0 plane is still
        /// symmetric about the plane this app mirrors across.
        ///
        /// If a file ever arrives from a Y-up source (ZBrush writes STL in its own Y-up space,
        /// unlike CAD, slicers and Blender), it lands on its back and the Transpose gizmo
        /// rotates it upright. That is the trade: one convention has to be assumed, and Z-up is
        /// what the overwhelming majority of STLs in the wild are written in.
        private static Vector3 ToUnity(float x, float y, float z) => new Vector3(-x, z, -y);

        // ------------------------------------------------------------------ format sniffing

        /// True if this is a binary STL.
        ///
        /// The header is NOT a usable signal, which is the trap this method exists for: the spec
        /// says a binary file's 80-byte header is arbitrary text, and plenty of writers fill it
        /// with the word "solid" - which is also exactly how an ASCII file starts. Sniffing on
        /// that word alone reads a binary file as text and imports an empty mesh.
        ///
        /// So the primary test is the file's LENGTH. A binary STL is 80 header bytes, a 4-byte
        /// triangle count, and then exactly 50 bytes per triangle with nothing else in it; an
        /// ASCII file satisfying that arithmetic by coincidence is not a thing that happens.
        ///
        /// When the arithmetic does not land the file is damaged or padded rather than
        /// ambiguous - a truncated download, a writer that appended a colour table, a count
        /// field that disagrees with the body. Those are still binary, and ReadBinary already
        /// clamps to whatever is actually there, so the question becomes "is this text?" rather
        /// than "does the count fit?". It is answered by looking at the bytes: real ASCII STL is
        /// printable characters the whole way down and says "facet" within its first line or
        /// two, whereas binary hits raw float bytes immediately after the header. Anything that
        /// fails that test is treated as binary, because binary is the only other thing it can
        /// be, and a partial import beats refusing the file outright.
        private static bool LooksBinary(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                long length = stream.Length;
                if (length < HeaderBytes + 4) return false; // too short to be binary at all

                var head = new byte[HeaderBytes + 4];
                if (!ReadFully(stream, head, head.Length)) return false;

                uint count = BitConverter.ToUInt32(head, HeaderBytes);
                if (HeaderBytes + 4L + FacetBytes * (long)count == length) return true;

                stream.Position = 0;
                var probe = new byte[(int)Math.Min(512, length)];
                if (!ReadFully(stream, probe, probe.Length)) return true;
                return !LooksLikeAsciiStl(probe);
            }
        }

        /// Whether a leading chunk of the file reads as ASCII STL source. Both halves matter:
        /// the printable check rules out a binary file whose header happens to contain the word
        /// (a description like "facets generated by ..." is exactly the kind of thing writers
        /// put there), and the keyword rules out an unrelated text file.
        private static bool LooksLikeAsciiStl(byte[] probe)
        {
            foreach (byte b in probe)
            {
                bool printable = b >= 0x20 && b <= 0x7E;
                bool whitespace = b == 0x09 || b == 0x0A || b == 0x0D; // tab, LF, CR
                if (!printable && !whitespace) return false;
            }

            string text = System.Text.Encoding.ASCII.GetString(probe);
            return text.IndexOf("facet", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------ binary

        private const int HeaderBytes = 80;
        private const int FacetBytes = 50;   // 12 floats (normal + 3 corners) + a 2-byte attribute

        private static List<Vector3> ReadBinary(string path, out string error)
        {
            error = null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                var head = new byte[HeaderBytes + 4];
                if (!ReadFully(stream, head, head.Length)) { error = "File ended inside the header."; return null; }

                long declared = BitConverter.ToUInt32(head, HeaderBytes);
                // Trust the file length over the declared count where they disagree: a count
                // field that has been corrupted (or that counts something else) would otherwise
                // either truncate a good model or allocate gigabytes for a bad one.
                long available = (stream.Length - stream.Position) / FacetBytes;
                long count = Math.Min(declared, available);
                if (count <= 0) { error = "The file declares no triangles."; return null; }

                var corners = new List<Vector3>(checked((int)(count * 3)));

                // Read a block of facets at a time rather than field by field - a BinaryReader
                // call per float is the difference between a fast import and a visible stall on
                // a multi-million-triangle print file.
                const int FacetsPerBlock = 4096;
                var block = new byte[FacetBytes * FacetsPerBlock];

                for (long done = 0; done < count; )
                {
                    int batch = (int)Math.Min(FacetsPerBlock, count - done);
                    if (!ReadFully(stream, block, batch * FacetBytes)) break; // truncated file: keep what was read
                    for (int f = 0; f < batch; f++)
                    {
                        int o = f * FacetBytes;
                        // Every value is a little-endian IEEE-754 single, which is what
                        // BitConverter reads on every platform Unity ships a player for.
                        AddFacet(corners,
                            BitConverter.ToSingle(block, o + 0),  BitConverter.ToSingle(block, o + 4),  BitConverter.ToSingle(block, o + 8),
                            BitConverter.ToSingle(block, o + 12), BitConverter.ToSingle(block, o + 16), BitConverter.ToSingle(block, o + 20),
                            BitConverter.ToSingle(block, o + 24), BitConverter.ToSingle(block, o + 28), BitConverter.ToSingle(block, o + 32),
                            BitConverter.ToSingle(block, o + 36), BitConverter.ToSingle(block, o + 40), BitConverter.ToSingle(block, o + 44));
                    }
                    done += batch;
                }

                return corners;
            }
        }

        private static bool ReadFully(Stream stream, byte[] into, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = stream.Read(into, got, count - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }

        // ------------------------------------------------------------------ ASCII

        /// The grammar is "solid / facet normal nx ny nz / outer loop / vertex x y z (x3) /
        /// endloop / endfacet / endsolid", but it is parsed permissively: only "facet" and
        /// "vertex" lines carry data, and everything else is structure that can be inferred.
        /// Files with a stray blank line, CRLF endings, or several solids concatenated into one
        /// file all parse without special cases that way.
        private static List<Vector3> ReadAscii(string path, out string error)
        {
            error = null;
            var corners = new List<Vector3>();
            var loop = new List<Vector3>(4);
            Vector3 normal = Vector3.zero;

            using (var reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    if (parts[0].Equals("vertex", StringComparison.OrdinalIgnoreCase))
                    {
                        if (parts.Length >= 4 &&
                            TryFloat(parts[1], out float x) && TryFloat(parts[2], out float y) && TryFloat(parts[3], out float z))
                            loop.Add(new Vector3(x, y, z));
                    }
                    else if (parts[0].Equals("facet", StringComparison.OrdinalIgnoreCase))
                    {
                        // "facet normal nx ny nz". A facet with no usable normal is legal and
                        // common (writers that do not bother emit zeroes); AddFacet copes.
                        normal = Vector3.zero;
                        if (parts.Length >= 5 &&
                            TryFloat(parts[2], out float nx) && TryFloat(parts[3], out float ny) && TryFloat(parts[4], out float nz))
                            normal = new Vector3(nx, ny, nz);
                        loop.Clear();
                    }
                    else if (parts[0].Equals("endloop", StringComparison.OrdinalIgnoreCase) ||
                             parts[0].Equals("endfacet", StringComparison.OrdinalIgnoreCase))
                    {
                        // Fan-triangulate. A loop is three vertices in every legal STL, so the
                        // fan is a no-op almost always - it is here so that a writer that emitted
                        // a quad loop produces geometry instead of being silently dropped.
                        for (int i = 1; i + 1 < loop.Count; i++)
                            AddFacet(corners, normal, loop[0], loop[i], loop[i + 1]);
                        loop.Clear();
                    }
                }
            }

            return corners;
        }

        private static bool TryFloat(string s, out float f) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

        private static readonly char[] SplitChars = { ' ', '\t', '\r' };

        // ------------------------------------------------------------------ facet assembly

        private static void AddFacet(List<Vector3> corners,
                                     float nx, float ny, float nz,
                                     float ax, float ay, float az,
                                     float bx, float by, float bz,
                                     float cx, float cy, float cz) =>
            AddFacet(corners, new Vector3(nx, ny, nz),
                     new Vector3(ax, ay, az), new Vector3(bx, by, bz), new Vector3(cx, cy, cz));

        /// Appends one triangle's three corners, converted to Unity space and wound so the face
        /// points outward.
        ///
        /// The winding correction is the reason the facet normal is read at all. STL stores both
        /// the normal AND the corner order, and they are allowed to disagree - a fair number of
        /// files in circulation (CAD exports and repaired meshes especially) have facets whose
        /// corner order is inverted and rely on readers honouring the stored normal. Those
        /// import as holes in the surface otherwise: black inside-out patches that will not take
        /// a brush stroke because they face away from the camera. Tested in the file's own
        /// right-handed space, before the axis swap, so the arithmetic is plain cross-product
        /// handedness with no conversion folded in.
        private static void AddFacet(List<Vector3> corners, Vector3 normal, Vector3 a, Vector3 b, Vector3 c)
        {
            if (Vector3.Dot(normal, Vector3.Cross(b - a, c - a)) < 0f)
            {
                Vector3 swap = b; b = c; c = swap;
            }

            // Reversed on the way out (a, c, b): the axis swap in ToUnity flips handedness, and
            // a handedness flip inverts face orientation, so the winding has to flip back or
            // every triangle in the model is inside-out. Same correction ObjImporter applies for
            // the same reason.
            corners.Add(ToUnity(a.x, a.y, a.z));
            corners.Add(ToUnity(c.x, c.y, c.z));
            corners.Add(ToUnity(b.x, b.y, b.z));
        }

        // ------------------------------------------------------------------ welding

        /// Collapses the triangle soup into a shared-vertex mesh: corners that sit at the same
        /// point become one vertex that every triangle touching it indexes.
        ///
        /// Matching is by a quantised position key, one hash lookup per corner, so this stays
        /// linear over a file with millions of facets. The grid is sized RELATIVE to the model
        /// (a millionth of its bounding diagonal) rather than as a fixed epsilon, because STL
        /// carries no units: the same part is exported at 0.05 across in metres and at 50 across
        /// in millimetres, and a fixed tolerance is either useless on one or destructive on the
        /// other.
        ///
        /// Corners that genuinely came from the same authored vertex hold bit-identical floats -
        /// the writer copied one value into each facet - so they quantise to the same cell and
        /// always merge. The tolerance only earns its keep on files that have been through a
        /// lossy round trip; there, two values a hair apart merge unless they happen to straddle
        /// a cell boundary. Probing the neighbouring cells would close that last gap at 8x the
        /// lookups on every corner of every file, which is not a trade worth making for a case
        /// the exact match already covers.
        private static void Weld(List<Vector3> corners, out Vector3[] vertices, out int[] triangles)
        {
            int n = corners.Count;
            Vector3 min = corners[0], max = corners[0];
            for (int i = 1; i < n; i++)
            {
                Vector3 p = corners[i];
                if (p.x < min.x) min.x = p.x; else if (p.x > max.x) max.x = p.x;
                if (p.y < min.y) min.y = p.y; else if (p.y > max.y) max.y = p.y;
                if (p.z < min.z) min.z = p.z; else if (p.z > max.z) max.z = p.z;
            }

            // Cell size is the diagonal over Resolution, so no axis index can exceed Resolution
            // and the three of them pack into a long without colliding (see the key below).
            const float Resolution = 1000000f;
            float cell = Mathf.Max((max - min).magnitude / Resolution, 1e-9f);
            float inverseCell = 1f / cell;

            var lookup = new Dictionary<long, int>(n / 2);
            var verts = new List<Vector3>(n / 2);
            var tris = new List<int>(n);

            int degenerate = 0;
            var triangle = new int[3];

            for (int i = 0; i + 2 < n; i += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    Vector3 p = corners[i + k];
                    long ix = (long)((p.x - min.x) * inverseCell);
                    long iy = (long)((p.y - min.y) * inverseCell);
                    long iz = (long)((p.z - min.z) * inverseCell);
                    long key = (ix << 42) ^ (iy << 21) ^ iz;

                    if (!lookup.TryGetValue(key, out int index))
                    {
                        index = verts.Count;
                        lookup.Add(key, index);
                        // The FIRST corner to claim a cell sets the surviving position, so
                        // vertices keep coordinates the file actually contained rather than
                        // being snapped to the middle of a grid cell.
                        verts.Add(p);
                    }
                    triangle[k] = index;
                }

                // A facet whose corners welded into fewer than three distinct vertices had zero
                // area to begin with. Tessellators emit these constantly along curved seams, and
                // a zero-area triangle produces a NaN normal that spreads to every vertex it
                // touches the moment the mesh is smoothed.
                if (triangle[0] == triangle[1] || triangle[1] == triangle[2] || triangle[0] == triangle[2])
                {
                    degenerate++;
                    continue;
                }

                tris.Add(triangle[0]);
                tris.Add(triangle[1]);
                tris.Add(triangle[2]);
            }

            if (degenerate > 0)
                Debug.Log($"STL import: dropped {degenerate} degenerate triangle{(degenerate == 1 ? "" : "s")}.");

            vertices = verts.ToArray();
            triangles = tris.ToArray();
        }
    }
}
