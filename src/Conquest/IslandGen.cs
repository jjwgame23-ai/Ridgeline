using System.Diagnostics;
using Godot;

namespace Ridgeline;

/// <summary>
/// Makes a Conquest island from a seed, in order: the ground, its climate, the rivers, what grows, the towns, the
/// farmland, the roads, the sides' starting areas and the resource nodes, then the names.
/// Each stage draws from its own random stream, so changing one doesn't reshuffle the others.
/// </summary>
public static class IslandGen
{
    public static Island Generate(int seed, int n, float extentM, IslandClimate climate, Action<string>? log = null)
    {
        var isl = new Island(seed, n, extentM, climate);
        var names = new PlaceNames(seed ^ 0x5EED);
        void Stage(string name, Action a)
        {
            var sw = Stopwatch.StartNew();
            a();
            isl.Timings.Add((name, sw.Elapsed.TotalMilliseconds));
            log?.Invoke($"worldgen: {name} {sw.Elapsed.TotalSeconds:0.0} s");
        }
        Stage("ground", () => Landform.Build(isl, new Random(seed + 1)));
        Stage("climate", () => IslandClimate.Apply(isl));
        Stage("rivers", () => Hydrology.Apply(isl));
        Stage("cover", () =>
        {
            LandCover.Slopes(isl);
            LandCover.Natural(isl);
        });
        Stage("towns", () =>
        {
            TownPlanner.Apply(isl, new Random(seed + 2), names);
            LandCover.Urban(isl);
        });
        Stage("farmland", () => LandCover.Farmland(isl));
        Stage("roads", () => RoadBuilder.Apply(isl, new Random(seed + 4)));
        Stage("sides", () => Holdings.Apply(isl, new Random(seed + 5)));
        Stage("names", () =>
        {
            isl.Name = names.Island();
            foreach (var r in isl.Rivers) r.Name = names.River();
            Peaks.Find(isl, names);
        });
        return isl;
    }
}

/// <summary>
/// `-- mode=worldgen [seed=1] [seeds=4] [size=1280] [out=worldgen]`: make islands headless, write each one's map,
/// layers, report and page to the out folder (under the project unless absolute), then quit.
/// </summary>
public static class WorldGenMode
{
    public static bool RunIfRequested(Node from)
    {
        var a = new Dictionary<string, string>();
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var kv = arg.Split('=', 2);
            a[kv[0]] = kv.Length > 1 ? kv[1] : "";
        }
        if (!a.TryGetValue("mode", out var mode) || mode != "worldgen") return false;
        // Reports and pages use '.' for decimals whatever the machine's locale.
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        int seed = a.TryGetValue("seed", out var s) && int.TryParse(s, out var sv) ? sv : 1;
        int seeds = a.TryGetValue("seeds", out var ss) && int.TryParse(ss, out var ssv) ? Math.Max(1, ssv) : 1;
        // The grid must halve twice for the coarse-to-fine erosion.
        int n = a.TryGetValue("size", out var z) && int.TryParse(z, out var zv) ? Math.Max(64, zv / 4 * 4) : 1280;
        string dir = a.TryGetValue("out", out var o) && o != "" ? o : "worldgen";
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(ProjectSettings.GlobalizePath("res://"), dir);
        Directory.CreateDirectory(dir);
        var climate = IslandClimate.Get(a.TryGetValue("climate", out var cl) ? cl : "mediterranean");
        for (int k = 0; k < seeds; k++)
        {
            var total = Stopwatch.StartNew();
            var isl = IslandGen.Generate(seed + k, n, 128_000f, climate, msg => GD.Print(msg));
            string stem = Path.Combine(dir, $"island-{seed + k}");
            var draw = Stopwatch.StartNew();
            IslandRender.Map(isl, stem + ".png");
            IslandRender.Layers(isl, stem + "-layers.png");
            isl.Timings.Add(("drawing", draw.Elapsed.TotalMilliseconds));
            string report = IslandReport.Text(isl);
            File.WriteAllText(stem + "-report.txt", report);
            IslandRender.Page(isl, stem + ".png", report, stem + ".html");
            GD.Print(report);
            GD.Print($"worldgen: wrote {stem}.png, -layers.png, -report.txt and .html in {total.Elapsed.TotalSeconds:0.0} s");
        }
        from.GetTree().Quit();
        return true;
    }
}
