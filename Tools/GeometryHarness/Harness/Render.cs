using System;
using System.IO;
using System.IO.Compression;
using Sculpting;
using UnityEngine;

namespace GeometryHarness
{
    /// Minimal software renderer for looking at remesh output without Blender or Unity: flat
    /// shaded faces with a z-buffer, then the POLYGON edges (not triangulation diagonals) drawn
    /// over them, written as a PNG.
    internal static class Render
    {
        /// Renders `mesh` seen from direction (yawDeg, pitchDeg) and writes `path`. `zoom` > 1
        /// crops into the centre; `focus` (0..1 per axis of the bounds) picks the centre.
        public static void Png(string path, PolyMesh mesh, int size = 1000, float yawDeg = 30f, float pitchDeg = 20f,
                               float zoom = 1f, Vector3? focus = null)
        {
            Vector3 min = mesh.Vertices[0], max = mesh.Vertices[0];
            foreach (Vector3 p in mesh.Vertices) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            Vector3 f = focus ?? new Vector3(0.5f, 0.5f, 0.5f);
            Vector3 centre = new Vector3(min.x + (max.x - min.x) * f.x, min.y + (max.y - min.y) * f.y, min.z + (max.z - min.z) * f.z);
            float radius = (max - min).magnitude * 0.5f / zoom;

            double yaw = yawDeg * Math.PI / 180, pitch = pitchDeg * Math.PI / 180;
            // Camera looks along -forward; right/up span the image.
            var forward = new Vector3((float)(Math.Cos(pitch) * Math.Sin(yaw)), (float)Math.Sin(pitch), (float)(Math.Cos(pitch) * Math.Cos(yaw)));
            Vector3 right = VectorMath.NormalizeOr(Vector3.Cross(Vector3.up, forward), Vector3.right);
            Vector3 up = Vector3.Cross(forward, right);

            int n = mesh.VertexCount;
            var sx = new float[n]; var sy = new float[n]; var sz = new float[n];
            float half = size * 0.5f;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = mesh.Vertices[i] - centre;
                sx[i] = half + Vector3.Dot(d, right) / radius * half;
                sy[i] = half - Vector3.Dot(d, up) / radius * half;
                sz[i] = Vector3.Dot(d, forward); // larger = closer to camera
            }

            var depth = new float[size * size];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.NegativeInfinity;
            var rgb = new byte[size * size * 3];
            for (int i = 0; i < rgb.Length; i++) rgb[i] = 40;

            Vector3 light = VectorMath.NormalizeOr(forward + up * 0.6f + right * 0.3f, forward);
            for (int face = 0; face < mesh.FaceCount; face++)
            {
                int k = mesh.FaceSize(face);
                if (k < 3) continue;
                Vector3 a0 = mesh.Vertices[mesh.Corner(face, 0)];
                Vector3 nrm = Vector3.zero;
                for (int j = 1; j + 1 < k; j++)
                    nrm += Vector3.Cross(mesh.Vertices[mesh.Corner(face, j)] - a0, mesh.Vertices[mesh.Corner(face, j + 1)] - a0);
                nrm = VectorMath.NormalizeOr(nrm, forward);
                bool back = Vector3.Dot(nrm, forward) < 0;
                float shade = Math.Abs(Vector3.Dot(nrm, light));
                byte r = (byte)(back ? 90 + 120 * shade : 70 + 170 * shade);
                byte g = (byte)(back ? 40 + 50 * shade : 75 + 165 * shade);
                byte b = (byte)(back ? 40 + 50 * shade : 85 + 160 * shade);
                for (int j = 1; j + 1 < k; j++)
                    Triangle(mesh.Corner(face, 0), mesh.Corner(face, j), mesh.Corner(face, j + 1), sx, sy, sz, depth, rgb, size, r, g, b);
            }

            float bias = radius * 0.004f;
            for (int face = 0; face < mesh.FaceCount; face++)
            {
                int k = mesh.FaceSize(face);
                byte ec = (byte)(k == 4 ? 20 : 0);
                for (int j = 0; j < k; j++)
                {
                    int a = mesh.Corner(face, j), b = mesh.Corner(face, j + 1);
                    Line(sx[a], sy[a], sz[a], sx[b], sy[b], sz[b], depth, rgb, size, bias, k == 4 ? (byte)20 : (byte)230, ec, ec);
                }
            }

            WritePng(path, rgb, size, size);
        }

        private static void Triangle(int a, int b, int c, float[] sx, float[] sy, float[] sz, float[] depth, byte[] rgb, int size, byte r, byte g, byte bl)
        {
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(sx[a], Math.Min(sx[b], sx[c]))));
            int x1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(sx[a], Math.Max(sx[b], sx[c]))));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(sy[a], Math.Min(sy[b], sy[c]))));
            int y1 = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(sy[a], Math.Max(sy[b], sy[c]))));
            float area = (sx[b] - sx[a]) * (sy[c] - sy[a]) - (sx[c] - sx[a]) * (sy[b] - sy[a]);
            if (Math.Abs(area) < 1e-12f) return;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((sx[b] - px) * (sy[c] - py) - (sx[c] - px) * (sy[b] - py)) / area;
                    float w1 = ((sx[c] - px) * (sy[a] - py) - (sx[a] - px) * (sy[c] - py)) / area;
                    float w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * sz[a] + w1 * sz[b] + w2 * sz[c];
                    int i = y * size + x;
                    if (z <= depth[i]) continue;
                    depth[i] = z;
                    rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = bl;
                }
        }

        private static void Line(float x0, float y0, float z0, float x1, float y1, float z1, float[] depth, byte[] rgb, int size, float bias, byte r, byte g, byte b)
        {
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0))) + 1;
            for (int s = 0; s <= steps; s++)
            {
                float t = (float)s / steps;
                int x = (int)(x0 + (x1 - x0) * t), y = (int)(y0 + (y1 - y0) * t);
                if (x < 0 || y < 0 || x >= size || y >= size) continue;
                float z = z0 + (z1 - z0) * t;
                int i = y * size + x;
                if (z + bias < depth[i]) continue;
                rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
            }
        }

        private static void WritePng(string path, byte[] rgb, int w, int h)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var raw = new byte[(w * 3 + 1) * h];
            for (int y = 0; y < h; y++) Buffer.BlockCopy(rgb, y * w * 3, raw, y * (w * 3 + 1) + 1, w * 3);
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
                compressed = ms.ToArray();
            }
            using (var fs = File.Create(path))
            {
                fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                var ihdr = new byte[13];
                BigEndian(ihdr, 0, w); BigEndian(ihdr, 4, h);
                ihdr[8] = 8; ihdr[9] = 2;
                Chunk(fs, "IHDR", ihdr);
                Chunk(fs, "IDAT", compressed);
                Chunk(fs, "IEND", new byte[0]);
            }
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; BigEndian(len, 0, data.Length);
            s.Write(len);
            var td = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
            Buffer.BlockCopy(data, 0, td, 4, data.Length);
            s.Write(td);
            var crc = new byte[4]; BigEndian(crc, 0, (int)Crc(td));
            s.Write(crc);
        }

        private static uint Crc(byte[] d)
        {
            uint c = 0xffffffffu;
            foreach (byte b in d)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }
            return c ^ 0xffffffffu;
        }

        private static void BigEndian(byte[] a, int o, int v)
        {
            a[o] = (byte)(v >> 24); a[o + 1] = (byte)(v >> 16); a[o + 2] = (byte)(v >> 8); a[o + 3] = (byte)v;
        }
    }
}
