using Godot;

namespace Ridgeline;

/// <summary>
/// `-- mode=window [seed=2] [town=Name | at=x,z] [size=4096]`: walk a window onto a Conquest island. It makes island
/// <c>seed</c> (as mode=war does) and builds the ground round a town (by name), a point (km east and south of the
/// island's centre), or by default the town of a thousand or more nearest the middle of the island. The player stands
/// at its middle. Nobody else is there yet: the armies come in the next slice.
/// </summary>
public static partial class WindowMode
{
    public static void Build(Node3D main, GameSetup setup)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var a = new Dictionary<string, string>();
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var kv = arg.Split('=', 2);
            a[kv[0]] = kv.Length > 1 ? kv[1] : "";
        }
        int seed = a.TryGetValue("seed", out var sv) && int.TryParse(sv, out int s) ? s : 2;
        float size = a.TryGetValue("size", out var zv) && float.TryParse(zv, out float z) ? z : 4096f;
        ulong t0 = Time.GetTicksMsec();
        var isl = IslandGen.Generate(seed, 1280, 128_000f, IslandClimate.Mediterranean, _ => { });
        GD.Print($"[window] island {seed} made in {(Time.GetTicksMsec() - t0) / 1000.0:0.0}s");
        float cx, cz;
        if (a.TryGetValue("at", out var at) && at.Split(',') is { Length: 2 } xy && float.TryParse(xy[0], out float ax) && float.TryParse(xy[1], out float az))
            (cx, cz) = (ax * 1000f, az * 1000f);
        else
        {
            var town = a.TryGetValue("town", out var tn) ? isl.Towns.FirstOrDefault(t => t.Name.Equals(tn, StringComparison.OrdinalIgnoreCase)) : null;
            town ??= isl.Towns.Where(t => t.Population >= 1000).OrderBy(t => t.X * t.X + t.Z * t.Z).First();
            (cx, cz) = (town.X, town.Z);
            GD.Print($"[window] at {town.Name} ({town.Kind}, {town.Population} people)");
        }
        var map = new Valley();
        main.AddChild(map);
        map.BuildWindow(isl, cx, cz, size, setup.Seed);
        Effects.Ground = map;
        var player = new Player();
        main.AddChild(player);
        var spawn = map.Sites.OrderBy(x => x.Center.LengthSquared()).Select(x => x.Center).FirstOrDefault();
        player.GlobalPosition = new Vector3(spawn.X + 6f, map.HeightAt(spawn.X + 6f, spawn.Z) + 0.3f, spawn.Z);
        main.AddChild(new Hud { P = player, HelpText = "A window onto the island: nobody else is here yet.", ShowHelp = false });
        main.AddChild(new NavReport { Map = map });
    }

    /// <summary>Says when the window's navmeshes are baked and how big they are.</summary>
    sealed partial class NavReport : Node
    {
        public Valley Map = null!;
        bool _said;

        public override void _Process(double delta)
        {
            if (_said || !Map.Nav.Finished || !Map.VehicleNav.Finished) return;
            _said = true;
            GD.Print($"[window] navmesh: {Map.Nav.Polygons:N0} polygons in {Map.Nav.Total} tiles ({Map.Nav.EmptyTiles} empty, {Map.Nav.Barred.Count} stretches of deep water barred), {Map.Nav.Seconds:0.0}s; "
                     + $"vehicles {Map.VehicleNav.Polygons:N0} polygons ({Map.VehicleNav.EmptyTiles} empty), {Map.VehicleNav.Seconds:0.0}s");
        }
    }
}
