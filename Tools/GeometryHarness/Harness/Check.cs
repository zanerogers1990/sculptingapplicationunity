using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace GeometryHarness
{
    /// Tiny assertion collector: every check prints on failure and is tallied; the process
    /// exit code is the failure count, so scripts can gate on it.
    internal static class Check
    {
        public static int Passed;
        public static int Failed;
        public static string Suite = "";

        public static void That(bool condition, string what)
        {
            if (condition) { Passed++; return; }
            Failed++;
            Console.WriteLine($"  FAIL [{Suite}] {what}");
        }

        public static void Equal(long expected, long actual, string what) =>
            That(expected == actual, $"{what}: expected {expected}, got {actual}");

        public static void Near(double expected, double actual, double tolerance, string what) =>
            That(Math.Abs(expected - actual) <= tolerance,
                 string.Format(CultureInfo.InvariantCulture, "{0}: expected {1:G6} +- {2:G3}, got {3:G6}", what, expected, tolerance, actual));

        public static void AtMost(double limit, double actual, string what) =>
            That(actual <= limit, string.Format(CultureInfo.InvariantCulture, "{0}: expected <= {1:G6}, got {2:G6}", what, limit, actual));
    }

    /// Where optional OBJ output goes (null = don't write any). Set with `-obj <dir>`.
    internal static class Output
    {
        public static string ObjDirectory;

        public static void Obj(string name, Sculpting.PolyMesh mesh)
        {
            if (ObjDirectory == null) return;
            Directory.CreateDirectory(ObjDirectory);
            string path = Path.Combine(ObjDirectory, name + ".obj");
            using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(false), 1 << 16))
                Sculpting.PolyMeshObj.Write(w, mesh, name, toRightHanded: true);
            Console.WriteLine($"  wrote {path}");
        }
    }

    /// Min-of-N wall time, which is what's comparable run to run (means pick up GC and
    /// scheduler noise).
    internal static class Bench
    {
        public static double MinMs(int runs, Action action)
        {
            double best = double.MaxValue;
            for (int i = 0; i < runs; i++)
            {
                var sw = Stopwatch.StartNew();
                action();
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            return best;
        }
    }
}
