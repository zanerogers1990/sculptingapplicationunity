using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Sculpting
{
    /// Topology and quality numbers for a PolyMesh - the yardstick every quad remesher stage is
    /// judged by, in tests and in the app's remesh report.
    internal sealed class PolyMeshReport
    {
        public const int MaxHistogramValence = 12; // valences above this land in the last bucket

        // Counts
        public int VertexCount;
        public int UsedVertexCount;
        public int FaceCount;
        public int TriangleCount;
        public int QuadCount;
        public int NgonCount;             // five or more sides
        public int DegenerateFaceCount;   // under three sides, or a vertex repeated within the face
        public int EdgeCount;
        public int ComponentCount;        // connected pieces (over used vertices)

        // Manifoldness
        public int BoundaryEdgeCount;
        public int NonManifoldEdgeCount;  // three or more face sides on one edge
        public int MisorientedEdgeCount;  // interior edge run the same way by both faces
        public int NonManifoldVertexCount; // faces around the vertex form more than one fan

        // Singularities. Interior valence 4 and boundary valence 3 are regular; everything else
        // is a pole. Boundary corners (valence 2) are counted as irregular too - a remesher that
        // adds them on a smooth boundary is doing something wrong.
        public int[] InteriorValence = new int[MaxHistogramValence + 1];
        public int[] BoundaryValence = new int[MaxHistogramValence + 1];
        public int IrregularInteriorCount;
        public int IrregularBoundaryCount;

        // Quad shape: deviation of every quad corner angle from 90 degrees.
        public double MeanAngleDeviationDeg;
        public double RmsAngleDeviationDeg;
        public double MaxAngleDeviationDeg;
        public double CornersWithin10DegFraction;

        // Edge lengths over unique edges.
        public double MeanEdgeLength;
        public double EdgeLengthStdDev;
        public double MinEdgeLength;
        public double MaxEdgeLength;

        public double QuadFraction => FaceCount > 0 ? (double)QuadCount / FaceCount : 0.0;
        public double EdgeLengthCV => MeanEdgeLength > 0 ? EdgeLengthStdDev / MeanEdgeLength : 0.0;
        public int EulerCharacteristic => UsedVertexCount - EdgeCount + FaceCount;

        public bool IsManifold => NonManifoldEdgeCount == 0 && NonManifoldVertexCount == 0
                                  && MisorientedEdgeCount == 0 && DegenerateFaceCount == 0;

        public bool IsClosedManifold => IsManifold && BoundaryEdgeCount == 0;

        /// Genus of a closed orientable manifold (chi = 2c - 2g summed over components), or -1
        /// when the mesh isn't one and the formula doesn't apply.
        public int Genus => IsClosedManifold ? (2 * ComponentCount - EulerCharacteristic) / 2 : -1;

        public string Summary()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.AppendFormat(ci, "V {0} (used {1})  F {2}  E {3}  components {4}  chi {5}",
                VertexCount, UsedVertexCount, FaceCount, EdgeCount, ComponentCount, EulerCharacteristic);
            if (Genus >= 0) sb.AppendFormat(ci, "  genus {0}", Genus);
            sb.AppendLine();
            sb.AppendFormat(ci, "faces: {0} quads ({1:P2}), {2} tris, {3} n-gons, {4} degenerate",
                QuadCount, QuadFraction, TriangleCount, NgonCount, DegenerateFaceCount);
            sb.AppendLine();
            sb.AppendFormat(ci, "manifold: boundary edges {0}, non-manifold edges {1}, misoriented {2}, non-manifold verts {3}",
                BoundaryEdgeCount, NonManifoldEdgeCount, MisorientedEdgeCount, NonManifoldVertexCount);
            sb.AppendLine();
            sb.AppendFormat(ci, "poles: interior {0}, boundary {1}   interior valence ", IrregularInteriorCount, IrregularBoundaryCount);
            AppendHistogram(sb, InteriorValence);
            sb.AppendLine();
            sb.AppendFormat(ci, "quad angles: mean dev {0:F2} deg, rms {1:F2}, max {2:F1}, within 10 deg {3:P1}",
                MeanAngleDeviationDeg, RmsAngleDeviationDeg, MaxAngleDeviationDeg, CornersWithin10DegFraction);
            sb.AppendLine();
            sb.AppendFormat(ci, "edges: mean {0:G4}, CV {1:F3}, min {2:G4}, max {3:G4}",
                MeanEdgeLength, EdgeLengthCV, MinEdgeLength, MaxEdgeLength);
            return sb.ToString();
        }

        private static void AppendHistogram(StringBuilder sb, int[] hist)
        {
            bool first = true;
            for (int v = 0; v < hist.Length; v++)
            {
                if (hist[v] == 0) continue;
                if (!first) sb.Append(' ');
                sb.Append(v).Append(v == hist.Length - 1 ? "+" : "").Append(':').Append(hist[v]);
                first = false;
            }
            if (first) sb.Append('-');
        }
    }

    internal static class PolyMeshAnalysis
    {
        public static PolyMeshReport Analyze(PolyMesh mesh) => Analyze(mesh, PolyMeshEdges.Build(mesh));

        public static PolyMeshReport Analyze(PolyMesh mesh, PolyMeshEdges edges)
        {
            var r = new PolyMeshReport
            {
                VertexCount = mesh.VertexCount,
                FaceCount = mesh.FaceCount,
                EdgeCount = edges.EdgeCount,
            };

            CountFaces(mesh, r);
            CountEdges(edges, r);
            AnalyzeVertices(mesh, edges, r);
            MeasureQuadAngles(mesh, r);
            MeasureEdgeLengths(mesh, edges, r);
            return r;
        }

        private static void CountFaces(PolyMesh mesh, PolyMeshReport r)
        {
            int[] start = mesh.FaceStart, idx = mesh.FaceIndices;
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                int s = start[f], n = start[f + 1] - s;
                bool degenerate = n < 3;
                for (int i = 0; i < n && !degenerate; i++)
                    for (int j = i + 1; j < n; j++)
                        if (idx[s + i] == idx[s + j]) { degenerate = true; break; }

                if (degenerate) r.DegenerateFaceCount++;
                else if (n == 3) r.TriangleCount++;
                else if (n == 4) r.QuadCount++;
                else r.NgonCount++;
            }
        }

        private static void CountEdges(PolyMeshEdges edges, PolyMeshReport r)
        {
            for (int e = 0; e < edges.EdgeCount; e++)
            {
                if (edges.IsBoundary(e)) r.BoundaryEdgeCount++;
                else if (edges.IsNonManifold(e)) r.NonManifoldEdgeCount++;
                else if (edges.IsMisoriented(e)) r.MisorientedEdgeCount++;
            }
        }

        /// Valence, fan count (non-manifold vertices) and connected components.
        private static void AnalyzeVertices(PolyMesh mesh, PolyMeshEdges edges, PolyMeshReport r)
        {
            int vCount = mesh.VertexCount;
            int[] start = mesh.FaceStart, idx = mesh.FaceIndices;

            var valence = new int[vCount];
            var onBoundary = new bool[vCount];
            var parent = new int[vCount];
            for (int v = 0; v < vCount; v++) parent[v] = v;

            for (int e = 0; e < edges.EdgeCount; e++)
            {
                int a = edges.EdgeA[e], b = edges.EdgeB[e];
                valence[a]++;
                valence[b]++;
                if (edges.IsBoundary(e)) { onBoundary[a] = true; onBoundary[b] = true; }
                Union(parent, a, b);
            }

            // Vertex -> incident corners, CSR.
            var cornerStart = new int[vCount + 1];
            for (int c = 0; c < idx.Length; c++) cornerStart[idx[c] + 1]++;
            for (int v = 0; v < vCount; v++) cornerStart[v + 1] += cornerStart[v];
            var cornerFill = (int[])cornerStart.Clone();
            var corners = new int[idx.Length];
            for (int c = 0; c < idx.Length; c++) corners[cornerFill[idx[c]]++] = c;

            // Face of each corner, so the previous side of a corner can be found.
            var cornerFace = new int[idx.Length];
            for (int f = 0; f < mesh.FaceCount; f++)
                for (int c = start[f]; c < start[f + 1]; c++) cornerFace[c] = f;

            // Fans: around vertex v, two incident corners belong to the same fan when they share
            // one of their two sides at v. More than one fan = the surface pinches through v.
            int[] local = new int[16];
            int[] localParent = new int[16];
            int[] localEdge = new int[32];
            int[] localOwner = new int[32];

            for (int v = 0; v < vCount; v++)
            {
                int cs = cornerStart[v], ce = cornerStart[v + 1];
                int n = ce - cs;
                if (n == 0) continue;
                r.UsedVertexCount++;

                int bucket = Math.Min(valence[v], PolyMeshReport.MaxHistogramValence);
                if (onBoundary[v])
                {
                    r.BoundaryValence[bucket]++;
                    if (valence[v] != 3) r.IrregularBoundaryCount++;
                }
                else
                {
                    r.InteriorValence[bucket]++;
                    if (valence[v] != 4) r.IrregularInteriorCount++;
                }

                if (n > local.Length)
                {
                    local = new int[n * 2];
                    localParent = new int[n * 2];
                    localEdge = new int[n * 4];
                    localOwner = new int[n * 4];
                }

                int sideCount = 0;
                for (int i = 0; i < n; i++)
                {
                    int c = corners[cs + i];
                    local[i] = c;
                    localParent[i] = i;
                    int f = cornerFace[c];
                    int prevSide = c == start[f] ? start[f + 1] - 1 : c - 1;
                    localEdge[sideCount] = edges.SideEdge[c]; localOwner[sideCount++] = i;
                    localEdge[sideCount] = edges.SideEdge[prevSide]; localOwner[sideCount++] = i;
                }

                int fans = n;
                for (int i = 0; i < sideCount; i++)
                {
                    if (localEdge[i] < 0) continue;
                    for (int j = i + 1; j < sideCount; j++)
                    {
                        if (localEdge[j] != localEdge[i]) continue;
                        int ra = LocalFind(localParent, localOwner[i]);
                        int rb = LocalFind(localParent, localOwner[j]);
                        if (ra != rb) { localParent[ra] = rb; fans--; }
                    }
                }
                if (fans > 1) r.NonManifoldVertexCount++;
            }

            for (int v = 0; v < vCount; v++)
                if (cornerStart[v + 1] > cornerStart[v] && Find(parent, v) == v) r.ComponentCount++;
        }

        private static void MeasureQuadAngles(PolyMesh mesh, PolyMeshReport r)
        {
            Vector3[] p = mesh.Vertices;
            int[] start = mesh.FaceStart, idx = mesh.FaceIndices;
            double sum = 0, sumSq = 0, max = 0;
            long count = 0, within = 0;

            for (int f = 0; f < mesh.FaceCount; f++)
            {
                int s = start[f];
                if (start[f + 1] - s != 4) continue;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 cur = p[idx[s + k]];
                    Vector3 prev = p[idx[s + ((k + 3) & 3)]];
                    Vector3 next = p[idx[s + ((k + 1) & 3)]];
                    double dev = Math.Abs(AngleDeg(prev - cur, next - cur) - 90.0);
                    sum += dev;
                    sumSq += dev * dev;
                    if (dev > max) max = dev;
                    if (dev <= 10.0) within++;
                    count++;
                }
            }

            if (count == 0) return;
            r.MeanAngleDeviationDeg = sum / count;
            r.RmsAngleDeviationDeg = Math.Sqrt(sumSq / count);
            r.MaxAngleDeviationDeg = max;
            r.CornersWithin10DegFraction = (double)within / count;
        }

        private static void MeasureEdgeLengths(PolyMesh mesh, PolyMeshEdges edges, PolyMeshReport r)
        {
            if (edges.EdgeCount == 0) return;
            Vector3[] p = mesh.Vertices;
            double sum = 0, sumSq = 0, min = double.MaxValue, max = 0;
            for (int e = 0; e < edges.EdgeCount; e++)
            {
                Vector3 d = p[edges.EdgeB[e]] - p[edges.EdgeA[e]];
                double len = Math.Sqrt((double)d.x * d.x + (double)d.y * d.y + (double)d.z * d.z);
                sum += len;
                sumSq += len * len;
                if (len < min) min = len;
                if (len > max) max = len;
            }
            int n = edges.EdgeCount;
            r.MeanEdgeLength = sum / n;
            r.EdgeLengthStdDev = Math.Sqrt(Math.Max(0.0, sumSq / n - r.MeanEdgeLength * r.MeanEdgeLength));
            r.MinEdgeLength = min;
            r.MaxEdgeLength = max;
        }

        /// Angle between two vectors in degrees, in double precision; 90 for a zero-length side,
        /// so a collapsed corner reads as "no information" rather than a perfect or awful angle.
        private static double AngleDeg(Vector3 a, Vector3 b)
        {
            double ax = a.x, ay = a.y, az = a.z, bx = b.x, by = b.y, bz = b.z;
            double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
            double cross = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            double dot = ax * bx + ay * by + az * bz;
            if (cross == 0.0 && dot == 0.0) return 90.0;
            return Math.Atan2(cross, dot) * (180.0 / Math.PI);
        }

        private static int Find(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }

        private static int LocalFind(int[] parent, int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
    }
}
