using System;
using System.Collections.Generic;
using System.Linq;

namespace GeometryHarness
{
    internal static class Program
    {
        private static readonly (string Name, Action Run)[] Suites =
        {
            ("polymesh", PolyMeshSuite.Run),
            ("perf", PolyMeshPerfSuite.Run),
            ("conditioning", ConditioningSuite.Run),
            ("real", ConditioningRealSuite.Run),
            ("inspect", InspectSuite.Run),
            ("remesh", RemeshSuite.Run),
            ("remeshreal", RemeshRealSuite.Run),
            ("view", ViewSuite.Run),
            ("holes", HoleCheck.Run),
        };

        private static int Main(string[] args)
        {
            var selected = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-obj" && i + 1 < args.Length) Output.ObjDirectory = args[++i];
                else selected.Add(args[i].ToLowerInvariant());
            }

            foreach (var (name, run) in Suites)
            {
                if (selected.Count > 0 && !selected.Contains(name)) continue;
                Check.Suite = name;
                int failedBefore = Check.Failed;
                Console.WriteLine($"[{name}]");
                try { run(); }
                catch (Exception e)
                {
                    Check.Failed++;
                    Console.WriteLine($"  EXCEPTION [{name}] {e}");
                }
                Console.WriteLine(Check.Failed == failedBefore ? "  ok" : $"  {Check.Failed - failedBefore} failed");
            }

            Console.WriteLine($"{Check.Passed} passed, {Check.Failed} failed");
            return Check.Failed;
        }
    }
}
