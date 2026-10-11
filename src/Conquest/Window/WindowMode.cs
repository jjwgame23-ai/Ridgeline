using Godot;

namespace Ridgeline;

/// <summary>
/// What a Conquest window is to be, wherever it comes from (the command line, the Conquest menu, the window moving round
/// the player, a replacement arriving): the war, where, who the player is, and the options. Kept across the scene
/// reload that builds it (Main.Launch), with the war in it, so the war goes on from where it was.
/// </summary>
public sealed class ConquestPlan
{
    /// <summary>A war already going (a move, a replacement); null: make island <c>Seed</c> and run its war to <c>Day</c>, <c>Hour</c>.</summary>
    public War? War;
    public int Seed = 2, Day = 3;
    /// <summary>The hour on that day (dawn, 6, if not given).</summary>
    public double Hour = 6.0;
    public float Size = 4096f;
    /// <summary>Where (m east and south of the island's centre), if given; else a town by name, else where the fighting is.</summary>
    public (float X, float Z)? At;
    public string Town = "";
    /// <summary>The side the player fights for (-1: whichever has a rifle squad nearest the window's middle, ALPHA first).</summary>
    public int Side = -1;
    /// <summary>The soldier the player is (a move, a replacement), and his wounds; -1: a rifleman of the squad nearest the middle.</summary>
    public int Soldier = -1;
    public Body? Body;
    /// <summary>A replacement on his way to his unit: the war runs on until he gets there (Medical.Rejoin).</summary>
    public bool Joining;
    /// <summary>A moved window: the player stands at its middle, facing as he was (else with his unit).</summary>
    public bool AtMiddle;
    public float Yaw;
    /// <summary>Shown on the screen when the player arrives.</summary>
    public string Arrival = "";
    public int Cap = 160;
    public bool Join = true, Live = true, Verbose;
    // The tests.
    public bool Assault, Calibrate;
    public int Attack = 2, Defend = 1;
    public double Minutes = 120;
    /// <summary>A window with no war in it: the ground alone (day=0 on the command line).</summary>
    public bool Empty;
}

/// <summary>
/// `-- mode=window [seed=2] [day=N [hour=H] [side=S] [bots=160] [join=0] [live=0] [verbose]] [town=Name | at=x,z] [size=4096]`: a window
/// onto a Conquest island. It makes island <c>seed</c> (as mode=war does) and builds the ground round a town (by name), a
/// point (km east and south of the island's centre), or by default the town of a thousand or more nearest the middle.
/// With day=N the war is run headless to dawn that day (hour=H later), the window goes where the fighting is (unless
/// told otherwise), and the armies near its middle come in as squads of bots (ConquestWindow), the player among them
/// unless join=0. Without it the player walks the window alone.
/// </summary>
public static partial class WindowMode
{
    /// <summary>The window to build at the next scene load (Main.Launch); null: from the command line.</summary>
    public static ConquestPlan? Next;

    /// <summary>Build a window now: the scene reloads into it.</summary>
    public static void Launch(Node from, ConquestPlan plan)
    {
        Next = plan;
        // The window's sky keeps the war's hour, at the war's pace (a minute of the war is a minute here), and a window
        // that moves keeps its weather and moon.
        var setup = new GameSetup { Map = "window", Hour = plan.War?.Hour ?? plan.Hour, TimeScale = 1f };
        if (plan.War != null)
        {
            setup.Weather = Conditions.Weather.ToString();
            setup.Moon = Conditions.MoonPhase;
        }
        Main.Launch(from, setup);
    }

    /// <summary>The plan from the command line (see the class summary).</summary>
    static ConquestPlan FromArgs()
    {
        var a = new Dictionary<string, string>();
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var kv = arg.Split('=', 2);
            a[kv[0]] = kv.Length > 1 ? kv[1] : "";
        }
        var p = new ConquestPlan
        {
            Seed = a.TryGetValue("seed", out var sv) && int.TryParse(sv, out int s) ? s : 2,
            Size = a.TryGetValue("size", out var zv) && float.TryParse(zv, out float z) ? z : 4096f,
            Day = a.TryGetValue("day", out var dv) && int.TryParse(dv, out int d) ? d : 0,
            Hour = a.TryGetValue("hour", out var hv) && double.TryParse(hv, out double h) ? h : 6.0,
            Assault = a.TryGetValue("assault", out var asv) && asv != "0",
            Calibrate = a.TryGetValue("calib", out var cv) && cv != "0",
            Verbose = a.ContainsKey("verbose"),
            Cap = a.TryGetValue("bots", out var bv) && int.TryParse(bv, out int bn) ? bn : 160,
            Attack = a.TryGetValue("attack", out var atv) && int.TryParse(atv, out int an) ? an : 2,
            Defend = a.TryGetValue("defend", out var dfv) && int.TryParse(dfv, out int dn) ? dn : 1,
            Minutes = a.TryGetValue("minutes", out var mv) && double.TryParse(mv, out double mn) ? mn : 120,
            Town = a.TryGetValue("town", out var tn) ? tn : "",
            Side = a.TryGetValue("side", out var sd) && int.TryParse(sd, out int si) ? si : -1,
        };
        if (p.Assault) p.Day = Math.Max(p.Day, 1);
        p.Empty = p.Day <= 0;
        p.Join = p.Assault ? a.TryGetValue("join", out var jv) && jv == "1" : !a.TryGetValue("join", out jv) || jv != "0";
        // The war runs on round the window (slice 3), unless it's a test that wants it still.
        p.Live = !p.Assault && !p.Calibrate && !(a.TryGetValue("live", out var lv) && lv == "0");
        if (a.TryGetValue("at", out var at) && at.Split(',') is { Length: 2 } xy && float.TryParse(xy[0], out float ax) && float.TryParse(xy[1], out float az))
            p.At = (ax * 1000f, az * 1000f);
        return p;
    }

    public static void Build(Node3D main, GameSetup setup)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var plan = Next ?? FromArgs();
        Next = null;
        ulong t0 = Time.GetTicksMsec();
        var war = plan.War;
        Island isl;
        if (war != null) isl = war.Isl;
        else
        {
            isl = IslandGen.Generate(plan.Seed, 1280, 128_000f, IslandClimate.Mediterranean, _ => { });
            GD.Print($"[window] island {plan.Seed} made in {(Time.GetTicksMsec() - t0) / 1000.0:0.0}s");
        }
        // The war, run headless to the day and hour asked for, with its armies in the window.
        if (war == null && !plan.Empty)
        {
            t0 = Time.GetTicksMsec();
            war = new War(isl, plan.Seed);
            war.Raise(new Random(plan.Seed * 31 + 7));
            Artillery.Setup(war);
            Supply.Setup(war);
            double until = (plan.Day - 1) * 86400.0 + (plan.Hour - 6.0) * 3600.0;
            while (war.Time < until) war.Tick(60);
            GD.Print($"[window] the war run to day {war.Day} {war.Hour:00.0}h in {(Time.GetTicksMsec() - t0) / 1000.0:0.0}s: "
                     + string.Join(", ", war.Sides.Select(sd => $"{sd.Name} {war.Dead[sd.Index]} dead")) + $"; {war.Fights.Count(f => !f.Over)} fights going on");
        }
        if (plan.Joining && war != null) Join(plan, war);
        var (cx, cz, target) = Place(plan, war, isl);
        var map = new Valley();
        main.AddChild(map);
        map.BuildWindow(isl, cx, cz, plan.Size, setup.Seed);
        Effects.Ground = map;
        main.AddChild(new NavReport { Map = map });
        if (war != null)
        {
            // The armies come in once the navmesh is up (ConquestWindow); the player with them, unless join=0.
            var hud = new Hud { HelpText = Hud.WindowHelp, ShowHelp = plan.Soldier < 0 };
            main.AddChild(hud);
            ConquestWindow.Verbose = plan.Verbose;
            main.AddChild(new ConquestWindow
            {
                War = war, Map = map, PlayerHud = hud, CX = cx, CZ = cz, Plan = plan,
                Cap = plan.Cap, PlayerJoins = plan.Join, Calibrate = plan.Calibrate, Live = plan.Live,
                Assault = plan.Assault, Target = target,
                AttackPlatoons = plan.Attack, DefendPlatoons = plan.Defend, AssaultMinutes = plan.Minutes,
            });
            return;
        }
        var player = new Player();
        main.AddChild(player);
        var spawn = map.Sites.OrderBy(x => x.Center.LengthSquared()).Select(x => x.Center).FirstOrDefault();
        player.GlobalPosition = new Vector3(spawn.X + 6f, map.HeightAt(spawn.X + 6f, spawn.Z) + 0.3f, spawn.Z);
        main.AddChild(new Hud { P = player, HelpText = "A window onto the island: nobody else is here yet.", ShowHelp = false });
    }

    /// <summary>
    /// A replacement's journey: landed at his side's port, he reaches his unit with its nightly resupply (Medical.Rejoin),
    /// so the war runs on until he has, or a day and a half at the most (cut off, he makes his own way up). The window
    /// then opens round his unit, and he's told how he got there.
    /// </summary>
    static void Join(ConquestPlan plan, War war)
    {
        ulong t0 = Time.GetTicksMsec();
        double landed = war.Time;
        string port = war.Sides[plan.Side].Port >= 0 ? war.Isl.Towns[war.Sides[plan.Side].Port].Name : "the coast";
        string when = $"day {war.Day}, {war.Hour:00}:{(int)(war.Hour % 1 * 60):00}";
        while (war.Soldiers[plan.Soldier].State == SoldierState.Joining && war.Time - landed < 36 * 3600) war.Tick(60);
        ref var so = ref war.Soldiers[plan.Soldier];
        var u = war.Units[so.Unit];
        var m = war.Units[u.Mover];
        if (so.State == SoldierState.Joining)
        {
            so.State = SoldierState.Fit;
            so.Since = war.Time;
            war.Recount(m);
        }
        plan.At = (m.X + u.OffX, m.Z + u.OffZ);
        string unit = string.Join(", ", u.Name.Split(", ").Take(3));
        plan.Arrival = $"You are {war.Who(plan.Soldier)}, a replacement. Landed at {port} on {when}; with {unit} since day {war.Day}, {war.Hour:00}:{(int)(war.Hour % 1 * 60):00}.";
        GD.Print($"[window] a replacement: {war.Who(plan.Soldier)} landed at {port} on {when}, joined {u.Name} {(war.Time - landed) / 3600:0.0} h later (the war run in {(Time.GetTicksMsec() - t0) / 1000.0:0.0}s)");
    }

    /// <summary>
    /// Where the window goes: where it's told; for the assault test, the town nearest the island's middle; a town by
    /// name; else where the fighting is (the player's side's, when they've picked one): the biggest fight going on, or
    /// midway between the two nearest enemy companies; with no war, the town of a thousand or more nearest the middle.
    /// </summary>
    static (float X, float Z, Objective? Target) Place(ConquestPlan plan, War? war, Island isl)
    {
        if (plan.Assault)
        {
            var t = war!.Objectives.Where(o => o.Kind == ObjKind.Town).OrderBy(o => o.X * o.X + o.Z * o.Z).First();
            GD.Print($"[window] the assault test on {t.Name}");
            return (t.X, t.Z, t);
        }
        if (plan.At is { } at) return (at.X, at.Z, null);
        if (war != null && plan.Town == "")
        {
            int side = plan.Side;
            var fight = war.Fights.Where(f => !f.Over && (side < 0 || f.In[side])).OrderByDescending(f => f.Movers.Count).FirstOrDefault();
            if (fight != null)
            {
                GD.Print($"[window] at the front, ({fight.X / 1000f:0.0}, {fight.Z / 1000f:0.0}) km, fight {fight.Id} of {fight.Movers.Count} companies");
                return (fight.X, fight.Z, null);
            }
            var front = war.MoverIds.Select(id => war.Units[id]).Where(u => u.People > 0 && Command.Manoeuvre(u)).ToList();
            var pair = front.SelectMany(p => front.Where(q => q.Side != p.Side && (side < 0 || p.Side == side)).Select(q => (p, q, d: (p.X - q.X) * (p.X - q.X) + (p.Z - q.Z) * (p.Z - q.Z))))
                .OrderBy(x => x.d).FirstOrDefault();
            if (pair.p != null)
            {
                GD.Print($"[window] at the front, between the nearest enemies");
                return ((pair.p.X + pair.q.X) / 2f, (pair.p.Z + pair.q.Z) / 2f, null);
            }
        }
        var town = plan.Town != "" ? isl.Towns.FirstOrDefault(t => t.Name.Equals(plan.Town, StringComparison.OrdinalIgnoreCase)) : null;
        town ??= isl.Towns.Where(t => t.Population >= 1000).OrderBy(t => t.X * t.X + t.Z * t.Z).First();
        GD.Print($"[window] at {town.Name} ({town.Kind}, {town.Population} people)");
        return (town.X, town.Z, null);
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
