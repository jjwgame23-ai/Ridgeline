using Godot;

namespace Ridgeline;

/// <summary>
/// `-- mode=window [seed=2] [day=N [hour=H] [bots=160] [join=0] [verbose]] [town=Name | at=x,z] [size=4096]`: a window
/// onto a Conquest island. It makes island <c>seed</c> (as mode=war does) and builds the ground round a town (by name), a
/// point (km east and south of the island's centre), or by default the town of a thousand or more nearest the middle.
/// With day=N the war is run headless to dawn that day (hour=H later), the window goes where the fighting is (unless
/// told otherwise), and the armies near its middle come in as squads of bots (ConquestWindow), the player among them
/// unless join=0. Without it the player walks the window alone.
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
        // day=N: the war, run headless to dawn on day N (and hour=H past that), with its armies in the window.
        int day = a.TryGetValue("day", out var dv) && int.TryParse(dv, out int d) ? d : 0;
        // assault=1: the embodied assault test, on the war as raised (ConquestWindow.EmbodyAssault).
        bool assault = a.TryGetValue("assault", out var asv) && asv != "0";
        if (assault) day = Math.Max(day, 1);
        War? war = null;
        if (day > 0)
        {
            t0 = Time.GetTicksMsec();
            war = new War(isl, seed);
            war.Raise(new Random(seed * 31 + 7));
            Artillery.Setup(war);
            Supply.Setup(war);
            double until = (day - 1) * 86400.0 + (a.TryGetValue("hour", out var hv) && double.TryParse(hv, out double h) ? (h - 6.0) * 3600.0 : 0.0);
            while (war.Time < until) war.Tick(60);
            GD.Print($"[window] the war run to day {war.Day} {war.Hour:00.0}h in {(Time.GetTicksMsec() - t0) / 1000.0:0.0}s: "
                     + string.Join(", ", war.Sides.Select(sd => $"{sd.Name} {war.Dead[sd.Index]} dead")) + $"; {war.Fights.Count(f => !f.Over)} fights going on");
        }
        float cx, cz;
        Objective? target = null;
        if (assault)
        {
            target = war!.Objectives.Where(o => o.Kind == ObjKind.Town).OrderBy(o => o.X * o.X + o.Z * o.Z).First();
            (cx, cz) = (target.X, target.Z);
            GD.Print($"[window] the assault test on {target.Name}");
        }
        else if (a.TryGetValue("at", out var at) && at.Split(',') is { Length: 2 } xy && float.TryParse(xy[0], out float ax) && float.TryParse(xy[1], out float az))
            (cx, cz) = (ax * 1000f, az * 1000f);
        else if (war != null && !a.ContainsKey("town"))
        {
            // Where the fighting is: the biggest fight going on, else midway between the two nearest enemy fighting companies.
            var fight = war.Fights.Where(f => !f.Over).OrderByDescending(f => f.Movers.Count).FirstOrDefault();
            if (fight != null) (cx, cz) = (fight.X, fight.Z);
            else
            {
                var front = war.MoverIds.Select(id => war.Units[id]).Where(u => u.People > 0 && Command.Manoeuvre(u)).ToList();
                var pair = front.SelectMany(p => front.Where(q => q.Side > p.Side).Select(q => (p, q, d: (p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z))))
                    .OrderBy(x => x.d).First();
                (cx, cz) = ((pair.p.X + pair.q.X) / 2f, (pair.p.Z + pair.q.Z) / 2f);
            }
            GD.Print($"[window] at the front, ({cx / 1000f:0.0}, {cz / 1000f:0.0}) km, {(fight != null ? $"fight {fight.Id} of {fight.Movers.Count} companies" : "between the nearest enemies")}");
        }
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
        main.AddChild(new NavReport { Map = map });
        if (war != null)
        {
            // The armies come in once the navmesh is up (ConquestWindow); the player with them, unless join=0.
            var hud = new Hud { HelpText = "A window onto the war.", ShowHelp = false };
            main.AddChild(hud);
            ConquestWindow.Verbose = a.ContainsKey("verbose");
            main.AddChild(new ConquestWindow
            {
                War = war, Map = map, PlayerHud = hud, CX = cx, CZ = cz,
                Cap = a.TryGetValue("bots", out var bv) && int.TryParse(bv, out int bn) ? bn : 160,
                PlayerJoins = assault ? a.TryGetValue("join", out var jv) && jv == "1" : !a.TryGetValue("join", out jv) || jv != "0",
                Calibrate = a.TryGetValue("calib", out var cv) && cv != "0",
                Assault = assault, Target = target,
                AttackPlatoons = a.TryGetValue("attack", out var atv) && int.TryParse(atv, out int an) ? an : 2,
                DefendPlatoons = a.TryGetValue("defend", out var dfv) && int.TryParse(dfv, out int dn) ? dn : 1,
                AssaultMinutes = a.TryGetValue("minutes", out var mv) && double.TryParse(mv, out double mn) ? mn : 45,
            });
            return;
        }
        var player = new Player();
        main.AddChild(player);
        var spawn = map.Sites.OrderBy(x => x.Center.LengthSquared()).Select(x => x.Center).FirstOrDefault();
        player.GlobalPosition = new Vector3(spawn.X + 6f, map.HeightAt(spawn.X + 6f, spawn.Z) + 0.3f, spawn.Z);
        main.AddChild(new Hud { P = player, HelpText = "A window onto the island: nobody else is here yet.", ShowHelp = false });
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
