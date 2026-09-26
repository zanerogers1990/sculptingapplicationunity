using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace GeometryHarness
{
    /// A triangle mesh in the engine's space (Unity: left-handed, Cross(b - a, c - a) outward).
    internal sealed class TriMesh
    {
        public string Name;
        public Vector3[] Vertices;
        public int[] Triangles;
        public int VertexCount => Vertices.Length;
        public int TriangleCount => Triangles.Length / 3;
    }

    internal static class MeshIO
    {
        public static string DataDirectory =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "data"));

        public static bool TryLoadData(string fileName, out TriMesh mesh)
        {
            string path = Path.Combine(DataDirectory, fileName);
            mesh = File.Exists(path) ? ReadObj(path) : null;
            if (mesh == null) Console.WriteLine($"  (skipped: {path} not found)");
            return mesh != null;
        }

        /// Reads a right-handed OBJ (as Blender/ZBrush/ObjExporter write them) into engine space
        /// the way ObjImporter does: X negated, winding reversed. Polygons fan-triangulate;
        /// normals/UVs are ignored.
        public static TriMesh ReadObj(string path)
        {
            var verts = new List<Vector3>(1 << 16);
            var tris = new List<int>(1 << 17);
            var ci = CultureInfo.InvariantCulture;
            var face = new List<int>(8);

            foreach (string raw in File.ReadLines(path))
            {
                ReadOnlySpan<char> line = raw.AsSpan().Trim();
                if (line.Length < 2) continue;
                if (line[0] == 'v' && line[1] == ' ')
                {
                    line = line.Slice(2);
                    float x = NextFloat(ref line, ci), y = NextFloat(ref line, ci), z = NextFloat(ref line, ci);
                    verts.Add(new Vector3(-x, y, z));
                }
                else if (line[0] == 'f' && line[1] == ' ')
                {
                    line = line.Slice(2);
                    face.Clear();
                    while (true)
                    {
                        line = line.TrimStart();
                        if (line.Length == 0) break;
                        int end = line.IndexOf(' ');
                        ReadOnlySpan<char> tok = end < 0 ? line : line.Slice(0, end);
                        int slash = tok.IndexOf('/');
                        if (slash >= 0) tok = tok.Slice(0, slash);
                        int idx = int.Parse(tok, NumberStyles.Integer, ci);
                        face.Add(idx < 0 ? verts.Count + idx : idx - 1);
                        if (end < 0) break;
                        line = line.Slice(end);
                    }
                    for (int k = 1; k + 1 < face.Count; k++)
                    {
                        // reversed winding for the X flip
                        tris.Add(face[k + 1]);
                        tris.Add(face[k]);
                        tris.Add(face[0]);
                    }
                }
            }
            return new TriMesh { Name = Path.GetFileNameWithoutExtension(path), Vertices = verts.ToArray(), Triangles = tris.ToArray() };
        }

        private static float NextFloat(ref ReadOnlySpan<char> s, CultureInfo ci)
        {
            s = s.TrimStart();
            int end = s.IndexOf(' ');
            ReadOnlySpan<char> tok = end < 0 ? s : s.Slice(0, end);
            s = end < 0 ? ReadOnlySpan<char>.Empty : s.Slice(end);
            return float.Parse(tok, NumberStyles.Float, ci);
        }
    }

    /// Sculpt-like damage for fixtures: the Move brush's signature is a region dragged along
    /// its normal with a smooth falloff, which stretches the triangles on the flanks into long
    /// needles while the interior keeps its density. That is the input a remesher has to cope
    /// with, and synthetic pulls reproduce it deterministically on any real mesh.
    internal static class Deform
    {
        /// `pulls` Move-brush drags. Each picks a vertex (evenly spread through the index range,
        /// so it is deterministic), and drags everything within `radius` (fraction of the
        /// bounding-box diagonal) along that vertex's normal by `strength` x radius, with a
        /// smoothstep falloff.
        public static TriMesh MovePulls(TriMesh src, int pulls, float radius = 0.12f, float strength = 1.2f)
        {
            Vector3[] v = (Vector3[])src.Vertices.Clone();
            Vector3 min = v[0], max = v[0];
            foreach (Vector3 p in v) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            float r = (max - min).magnitude * radius;
            Vector3[] normals = VertexNormals(v, src.Triangles);

            for (int k = 0; k < pulls; k++)
            {
                int centre = (int)((k + 0.5) * v.Length / pulls);
                Vector3 c = v[centre], dir = normals[centre] * (strength * r);
                for (int i = 0; i < v.Length; i++)
                {
                    float d = (v[i] - c).magnitude / r;
                    if (d >= 1f) continue;
                    float t = 1f - d;
                    v[i] += dir * (t * t * (3f - 2f * t));
                }
            }
            return new TriMesh { Name = src.Name + $"_pulled{pulls}", Vertices = v, Triangles = src.Triangles };
        }

        public static Vector3[] VertexNormals(Vector3[] v, int[] t)
        {
            var n = new Vector3[v.Length];
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 fn = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                n[t[i]] += fn; n[t[i + 1]] += fn; n[t[i + 2]] += fn;
            }
            for (int i = 0; i < n.Length; i++) n[i] = Sculpting.VectorMath.NormalizeOr(n[i], Vector3.up);
            return n;
        }
    }
}
