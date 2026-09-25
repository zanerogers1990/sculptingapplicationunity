using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Sculpting;
using UnityEngine;

/// Pins the properties LatheMeshBuilder promises: every build is 2-manifold with consistent
/// outward winding, closed builds are watertight with the right Euler characteristic, and the mesh
/// is EXACTLY mirror-symmetric across x = 0 and z = 0 in positions and topology alike.
public class LatheMeshBuilderTests
{
    private static LatheProfile Preset(LathePreset preset)
    {
        var profile = new LatheProfile();
        profile.SetPoints(LathePresets.Build(preset, 1.3f, out bool loop), loop);
        return profile;
    }

    private static LatheBuildResult Build(LatheProfile profile, int segments, bool even, bool caps)
    {
        var result = new LatheBuildResult();
        bool ok = LatheMeshBuilder.Build(profile,
            new LatheSettings { RadialSegments = segments, EvenTriangles = even, CapEnds = caps }, result);
        Assert.IsTrue(ok, result.Error);
        return result;
    }

    private static double SignedVolume(LatheBuildResult r)
    {
        double volume = 0;
        for (int t = 0; t < r.Triangles.Count; t += 3)
            volume += Vector3.Dot(r.Vertices[r.Triangles[t]],
                Vector3.Cross(r.Vertices[r.Triangles[t + 1]], r.Vertices[r.Triangles[t + 2]])) / 6.0;
        return volume;
    }

    [Test]
    public void EveryPresetIsManifoldWatertightAndOutward(
        [Values(LathePreset.Vase, LathePreset.Bowl, LathePreset.Cup, LathePreset.Bottle, LathePreset.Egg, LathePreset.Ring)] LathePreset preset,
        [Values(8, 32, 128)] int segments,
        [Values(true, false)] bool even)
    {
        LatheBuildResult r = Build(Preset(preset), segments, even, caps: true);

        var directed = new HashSet<(int, int)>();
        var undirected = new HashSet<(int, int)>();
        for (int t = 0; t < r.Triangles.Count; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = r.Triangles[t + e], b = r.Triangles[t + (e + 1) % 3];
                Assert.AreNotEqual(a, b, "degenerate triangle");
                Assert.IsTrue(directed.Add((a, b)), $"directed edge {a}->{b} used twice (bad winding or non-manifold)");
                undirected.Add(a < b ? (a, b) : (b, a));
            }

        foreach ((int a, int b) in directed)
            Assert.IsTrue(directed.Contains((b, a)), $"boundary edge {a}->{b} on a capped preset");

        Assert.IsTrue(r.Watertight);
        int euler = r.VertexCount - undirected.Count + r.TriangleCount;
        Assert.AreEqual(r.ProfileIsLoop ? 0 : 2, euler, "Euler characteristic");
        Assert.Greater(SignedVolume(r), 0.0, "faces point inward");
    }

    [Test]
    public void MeshIsBitExactlyMirrorSymmetric([Values(LathePreset.Vase, LathePreset.Ring)] LathePreset preset,
                                                [Values(true, false)] bool even)
    {
        LatheBuildResult r = Build(Preset(preset), 64, even, caps: true);

        var index = new Dictionary<Vector3, int>();
        for (int i = 0; i < r.VertexCount; i++) index[r.Vertices[i] + Vector3.zero] = i;

        (int, int, int) Canon(int a, int b, int c) =>
            a <= b && a <= c ? (a, b, c) : b <= a && b <= c ? (b, c, a) : (c, a, b);
        var tris = new HashSet<(int, int, int)>();
        for (int t = 0; t < r.Triangles.Count; t += 3)
            tris.Add(Canon(r.Triangles[t], r.Triangles[t + 1], r.Triangles[t + 2]));

        foreach (Vector3 flip in new[] { new Vector3(-1, 1, 1), new Vector3(1, 1, -1) })
        {
            var map = new int[r.VertexCount];
            for (int i = 0; i < r.VertexCount; i++)
            {
                Vector3 m = Vector3.Scale(r.Vertices[i], flip);
                // Dictionary<Vector3> hashes the exact floats; -0 and 0 compare equal via ==.
                Assert.IsTrue(index.TryGetValue(m, out map[i]) || index.TryGetValue(m + Vector3.zero, out map[i]),
                    $"no exact mirror for {r.Vertices[i]:F6} across {flip}");
            }
            for (int t = 0; t < r.Triangles.Count; t += 3)
                Assert.IsTrue(tris.Contains(Canon(map[r.Triangles[t]], map[r.Triangles[t + 2]], map[r.Triangles[t + 1]])),
                    $"triangle {t / 3} has no mirror image across {flip}");
        }
    }

    [Test]
    public void DrawingDirectionDoesNotChangeTheSolid()
    {
        LatheProfile forward = Preset(LathePreset.Vase);
        var reversed = new LatheProfile();
        reversed.SetPoints(forward.Snapshot().Reverse().ToList(), false);

        double a = SignedVolume(Build(forward, 64, true, true));
        double b = SignedVolume(Build(reversed, 64, true, true));
        Assert.Greater(a, 0.0);
        Assert.AreEqual(a, b, a * 1e-4);
    }

    [Test]
    public void UncappedTubeIsOpenAndSphereVolumeConverges()
    {
        var tube = new LatheProfile();
        tube.SetPoints(new[]
        {
            new LatheProfile.Point(new Vector2(0.5f, 0f), true),
            new LatheProfile.Point(new Vector2(0.5f, 2f), true)
        }, false);
        LatheBuildResult open = Build(tube, 128, true, caps: false);
        Assert.IsFalse(open.Watertight);

        var sphere = new LatheProfile();
        var points = new List<LatheProfile.Point>();
        for (int i = 0; i <= 8; i++)
        {
            double phi = System.Math.PI * i / 8;
            float x = i == 0 || i == 8 ? 0f : (float)System.Math.Sin(phi);
            points.Add(new LatheProfile.Point(new Vector2(x, (float)-System.Math.Cos(phi))));
        }
        sphere.SetPoints(points, false);
        double volume = SignedVolume(Build(sphere, 256, true, true));
        Assert.AreEqual(4.0 / 3.0 * System.Math.PI, volume, 0.01 * 4.19);
    }

    [Test]
    public void InteriorPointsAreHeldOffTheAxis()
    {
        var profile = new LatheProfile();
        profile.SetPoints(new[]
        {
            new LatheProfile.Point(new Vector2(0f, 0f)),
            new LatheProfile.Point(new Vector2(0f, 0.5f)), // interior on the axis - must be pushed off
            new LatheProfile.Point(new Vector2(0.4f, 1f)),
            new LatheProfile.Point(new Vector2(0f, 1.5f))
        }, false);
        Assert.Greater(profile[1].Position.x, 0f);
        Assert.AreEqual(0f, profile[0].Position.x);
        Assert.AreEqual(0f, profile[3].Position.x);
        Assert.IsTrue(Build(profile, 64, true, true).Watertight);
    }
}
