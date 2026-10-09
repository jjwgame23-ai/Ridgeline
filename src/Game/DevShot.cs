using Godot;

namespace Ridgeline;

/// <summary>
/// Development aid, driven by command-line user args (after `--`):
///   mode=range|duel1|duel2|duel5|spec1|spec5   skip the menu
///   verbose                                    log duel rounds and kills
///   shot=out.png [frames=N]                    save a screenshot after N frames and quit
///   [weapon=2] [ads] [grenade] [drill]         set up a pose for the screenshot
///   [view=eyes|free] [debug]                   spectator view / bot debug overlay
/// Add `--headless --fixed-fps 60` for a smoke test with no window.
/// </summary>
public partial class DevShot : Node
{
    string _path = "";
    int _frames = 120, _n;
    bool _ads, _grenade, _drill, _debug, _map, _craters, _gadget;
    string _hurt = "", _board = "";
    int _seat = -1;
    bool _drive, _wreck;
    int _weapon;
    string _view = "";
    string _cam = "", _look = "";
    Camera3D? _shotCam;
    readonly System.Diagnostics.Stopwatch _wall = new();

    static Dictionary<string, string> Args()
    {
        var d = new Dictionary<string, string>();
        foreach (var a in OS.GetCmdlineUserArgs())
        {
            var kv = a.Split('=', 2);
            d[kv[0]] = kv.Length > 1 ? kv[1] : "";
        }
        return d;
    }

    public static GameSetup? SetupFromArgs()
    {
        var a = Args();
        DuelMode.Verbose = a.ContainsKey("verbose");
        if (a.ContainsKey("shot")) { Settings.ForceWindowed = true; Settings.ApplyGraphics(); }
        Settings.Load();
        if (a.TryGetValue("role", out var role) && Enum.TryParse<Role>(role, true, out var r)) Settings.PlayerRole = r;
        if (!a.TryGetValue("mode", out var mode)) return null;
        var setup = mode switch
        {
            "range" => new GameSetup { Map = "range" },
            "duel1" => new GameSetup { Map = "arena", TeamSize = 1 },
            "duel2" => new GameSetup { Map = "arena", TeamSize = 2 },
            "duel5" => new GameSetup { Map = "arena", TeamSize = 5 },
            "spec1" => new GameSetup { Map = "arena", TeamSize = 1, PlayerJoins = false },
            "spec5" => new GameSetup { Map = "arena", TeamSize = 5, PlayerJoins = false },
            "duel8" => new GameSetup { Map = "arena", TeamSize = 8 },
            "spec8" => new GameSetup { Map = "arena", TeamSize = 8, PlayerJoins = false },
            "spec12" => new GameSetup { Map = "arena", TeamSize = 12, PlayerJoins = false },
            "koth8" => new GameSetup { Map = "valley", Mode = "koth", TeamSize = 8 },
            "kspec8" => new GameSetup { Map = "valley", Mode = "koth", TeamSize = 8, PlayerJoins = false },
            "kspec12" => new GameSetup { Map = "valley", Mode = "koth", TeamSize = 12, PlayerJoins = false },
            "vtest" => new GameSetup { Map = "valley", Mode = "vtest", TeamSize = 1 },
            "terr12" => new GameSetup { Map = "valley", TeamSize = 12 },
            "tspec8" => new GameSetup { Map = "valley", TeamSize = 8, PlayerJoins = false },
            "terr20" => new GameSetup { Map = "valley", TeamSize = 20 },
            "tspec12" => new GameSetup { Map = "valley", TeamSize = 12, PlayerJoins = false },
            "tspec20" => new GameSetup { Map = "valley", TeamSize = 20, PlayerJoins = false },
            "tspec33" => new GameSetup { Map = "valley", TeamSize = 33, PlayerJoins = false },
            "window" => new GameSetup { Map = "window" },
            _ => null,
        };
        // level=alhamra etc.: which battlefield.
        if (setup != null && a.TryGetValue("level", out var lv)) setup.MapId = lv;
        if (a.TryGetValue("front", out var fr)) Settings.FrontLine = fr != "0";
        if (a.TryGetValue("minutes", out var mn) && int.TryParse(mn, out var mins)) Settings.MatchMinutes = mins;
        // hour=23.5 (or -1 random), weather=rain|fog|overcast|clear|random, moon=0.5 (full), timescale=4
        if (setup == null) return null; // a mode= the list above doesn't know: the menu
        if (a.TryGetValue("hour", out var hr) && double.TryParse(hr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hour)) setup.Hour = hour;
        if (a.TryGetValue("weather", out var wx)) setup.Weather = wx;
        if (a.TryGetValue("moon", out var mo) && float.TryParse(mo, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var moon)) setup.Moon = moon;
        if (a.TryGetValue("timescale", out var ts) && float.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tsv)) setup.TimeScale = tsv;
        if (a.TryGetValue("tickets", out var tk) && int.TryParse(tk, out var tix)) TerritoryMode.TicketsOverride = tix;
        return setup;
    }

    /// <summary>sounddump=dir: write every synthesised sound to WAV files there, then quit (to listen to or analyse offline).</summary>
    public static void DumpSoundsIfRequested(Node from)
    {
        if (!Args().TryGetValue("sounddump", out var dir) || SoundWorld.I == null) return;
        SoundWorld.I.Dump(dir);
        from.GetTree().Quit();
    }

    public static void AttachIfRequested(Node parent)
    {
        var a = Args();
        if (!a.TryGetValue("shot", out var path)) return;
        // Runs before gameplay nodes so "just pressed" is seen in the same frame.
        var d = new DevShot
        {
            ProcessPriority = -100,
            _path = path,
            _frames = a.TryGetValue("frames", out var f) ? int.Parse(f) : 120,
            _weapon = a.TryGetValue("weapon", out var w) ? int.Parse(w) : 0,
            _ads = a.ContainsKey("ads"),
            _grenade = a.ContainsKey("grenade"),
            _drill = a.ContainsKey("drill"),
            _debug = a.ContainsKey("debug"),
            _map = a.ContainsKey("map"),
            _craters = a.ContainsKey("craters"),
            _gadget = a.ContainsKey("gadget"),
            _hurt = a.TryGetValue("hurt", out var h) ? h : "",
            _board = a.TryGetValue("board", out var bd) ? bd : "",
            _seat = a.TryGetValue("seat", out var st) ? int.Parse(st) : 0,
            _drive = a.ContainsKey("drive"),
            _wreck = a.ContainsKey("wreck"),
            _view = a.TryGetValue("view", out var v) ? v : "",
            _cam = a.TryGetValue("cam", out var cm) ? cm : "",
            _look = a.TryGetValue("look", out var lk) ? lk : "",
            _cmds = a.TryGetValue("squadcmds", out var sc) ? sc.Split(',').Select(int.Parse).ToArray() : Array.Empty<int>(),
        };
        // role=leader etc.: what the player spawns as.
        if (a.TryGetValue("role", out var rl) && Enum.TryParse<Role>(rl, true, out var role)) Settings.PlayerRole = role;
        parent.AddChild(d);
    }

    int[] _cmds = Array.Empty<int>();

    public override void _Process(double delta)
    {
        _n++;
        // squadcmds=0,3,4: the squad leader's commands, one every 5 s from 10 s in (testing).
        if (_cmds.Length > 0 && _n >= 600 && _n % 300 == 0 && (_n - 600) / 300 < _cmds.Length && TerritoryMode.I is { } tm)
        {
            int k = _cmds[(_n - 600) / 300];
            Player.I?.SetPitch(-8f);
            tm.SquadCommand(k);
            GD.Print($"[{Clock.Now:0}s] devshot: squad command {k} ({TerritoryHud.Commands[k]}) -> {tm.PlayerSquad?.OrderText}, march {tm.PlayerSquad?.PlayerMarch}, suppress {(tm.PlayerSquad?.SuppressUntil > Clock.Now)}, vehicle fire {(tm.PlayerSquad?.VehicleFireUntil > Clock.Now)}, want ride {tm.PlayerSquad?.WantRide}");
        }
        // Input is ignored for 150 ms after mouse capture, so wait before pressing anything.
        if (_n == 30 && _weapon == 2) Input.ActionPress("weapon2");
        if (_n == 32) Input.ActionRelease("weapon2");
        if (_n == 90 && _ads) Input.ActionPress("aim");
        if (_n == 30 && _grenade) Player.I?.ThrowGrenade();
        if (_n == 30 && _drill) Hud.I?.Drill?.Toggle();
        if (_hurt != "" && _n == 60 && Player.I is Player pl)
        {
            // Pose a wounded state for a screenshot: blood loss + a knock to the head, or down.
            pl.Body.Blood = _hurt == "down" ? 0.5f : 0.68f;
            pl.Body.Concussion = 0.8f;
            pl.Body.Hit(Region.Leg, 1f, new RandomNumberGenerator());
        }
        if (_board != "" && _n == 50 && Player.I is Player me)
        {
            // Get into the named vehicle (by class), in the given seat.
            var v = Vehicle.All.FirstOrDefault(x => x.Team == me.Team && !x.Crewed && x.Def.Kind.ToString().Equals(_board, StringComparison.OrdinalIgnoreCase));
            if (v != null) v.Enter(me, Math.Min(_seat, v.Def.Seats.Count - 1));
        }
        if (_drive && _n == 70) Input.ActionPress("move_forward");
        if (_wreck && _n == 60)
            foreach (var v in Vehicle.All.ToArray())
                if (v.Team == 1 || v.Def.Kind == VKind.IFV) v.Damage(99999f, null);
        if (_drive && _n == 200) { Input.ActionPress("move_left"); }
        if (_board != "" && _n == 120 && _ads) Input.ActionPress("aim");
        if (_gadget && _n == 40) { Player.I?.SetPitch(-12f); Input.ActionPress("gadget"); }
        if (_gadget && _n == 42) Input.ActionRelease("gadget");
        if (_gadget && _n == 45 && Player.I != null) Player.I.GlobalPosition -= -Player.I.GlobalBasis.Z * 3.5f; // step back to look at it
        if (_craters && _n is 20 or 24 or 28 or 32 or 36 or 40) Blasts();
        if (_n == 5 && (_debug || _map || _view != "")) SendKeys();
        // cam=x,z,height look=x,z,height: a fixed camera for looking at the map itself (heights over the ground).
        if (_cam != "" && _n >= 10 && Effects.Ground is IGround g)
        {
            Vector3 P(string s)
            {
                var f = s.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                return new Vector3(f[0], g.HeightAt(f[0], f[1]) + f[2], f[1]);
            }
            if (_shotCam == null) { _shotCam = new Camera3D { Far = 6000f, Fov = 70f }; GetParent().AddChild(_shotCam); }
            var at = P(_cam);
            _shotCam.GlobalPosition = at;
            _shotCam.LookAt(_look != "" ? P(_look) : at + Vector3.Forward * 10f);
            _shotCam.MakeCurrent();
        }
        if (_n == 60) _wall.Restart();
        if (_n == 900 && Args().ContainsKey("navbench") && Valley.Current is { } vm) NavBench(vm);
        if (_n < _frames) return;
        GD.Print(SoundWorld.Report);
        GD.Print($"devshot: {(_frames - 60) / 60.0:0}s of game in {_wall.Elapsed.TotalSeconds:0.0}s wall after startup");
        GD.Print($"devshot: t={Clock.Now:0.0}s combatants={Combatants.All.Count} alive={Combatants.All.Count(c => c.Alive)} " +
                 $"bullets={Ballistics.I?.LiveCount} pendingSounds={SoundWorld.I?.PendingCount}");
        foreach (var kv in Prof.Totals.OrderByDescending(k => k.Value.Ms))
            GD.Print($"prof {kv.Key,-11} {kv.Value.Ms,9:0} ms total  {kv.Value.Calls,8} calls  {kv.Value.Ms / Math.Max(1, kv.Value.Calls),7:0.000} ms/call");
        foreach (var kv in Prof.Counts.OrderBy(k => k.Key))
            GD.Print($"count {kv.Key,-28} {kv.Value,9}");
        foreach (var kv in Ballistics.NearBlockTags.OrderByDescending(k => k.Value))
            GD.Print($"nearblock {kv.Key}: {kv.Value} of {Ballistics.ShotTags.GetValueOrDefault(kv.Key)}");
        // Headless runs have nothing rendered; they're used to smoke-test code paths.
        if (DisplayServer.GetName() != "headless") GetViewport().GetTexture().GetImage().SavePng(_path);
        GetTree().Quit();
    }

    /// <summary>navbench: what the navmesh queries cost on this map, and do the tile-local ones agree with the map-wide ones?</summary>
    static void NavBench(Valley v)
    {
        var rng = new RandomNumberGenerator { Seed = 7 };
        var sw = new System.Diagnostics.Stopwatch();
        foreach (var (name, nav) in new[] { ("people", v.Nav), ("vehicles", v.VehicleNav) })
        {
            var map = nav.Map;
            double full = 0, local = 0;
            int differ = 0, n = 200;
            for (int i = 0; i < n; i++)
            {
                float x = rng.RandfRange(-v.Half + 80f, v.Half - 80f), z = rng.RandfRange(-v.Half + 80f, v.Half - 80f);
                var p = new Vector3(x, v.HeightAt(x, z) + rng.RandfRange(-1f, 4f), z);
                sw.Restart(); var a = NavigationServer3D.MapGetClosestPoint(map, p); full += sw.Elapsed.TotalMilliseconds;
                sw.Restart(); var b = nav.ClosestPoint(p); local += sw.Elapsed.TotalMilliseconds;
                if (a.DistanceTo(b) > 0.05f && MathF.Abs(a.DistanceTo(p) - b.DistanceTo(p)) > 0.05f) differ++;
            }
            GD.Print($"navbench {name}: closest point map-wide {full / n:0.000} ms, tile-local {local / n:0.000} ms, {differ}/{n} disagree ({nav.Polygons} polygons)");
        }
        // Paths: the whole map, against only the tiles round the start and the end.
        double pf = 0, pr = 0;
        int same = 0, tried = 0;
        for (int i = 0; i < 40; i++)
        {
            float x = rng.RandfRange(-v.Half + 150f, v.Half - 150f), z = rng.RandfRange(-v.Half + 150f, v.Half - 150f);
            var a = v.Ground(new Vector3(x, 0f, z));
            float ang = rng.Randf() * Mathf.Tau, dist = rng.RandfRange(30f, 400f);
            var b = v.Ground(a + new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * dist);
            sw.Restart(); var p1 = NavBaker.Path(v.GetWorld3D().NavigationMap, a, b); pf += sw.Elapsed.TotalMilliseconds;
            var box = new Rect2(new Vector2(MathF.Min(a.X, b.X), MathF.Min(a.Z, b.Z)), new Vector2(MathF.Abs(a.X - b.X), MathF.Abs(a.Z - b.Z))).Grow(120f);
            var q = new NavigationPathQueryParameters3D { Map = v.GetWorld3D().NavigationMap, StartPosition = a, TargetPosition = b, PathSearchMaxPolygons = 16000,
                PathPostprocessing = NavigationPathQueryParameters3D.PathPostProcessing.Corridorfunnel, IncludedRegions = v.Nav.RegionsIn(box) };
            var r = new NavigationPathQueryResult3D();
            sw.Restart(); NavigationServer3D.QueryPath(q, r); pr += sw.Elapsed.TotalMilliseconds;
            var p2 = r.Path;
            tried++;
            float L(Vector3[] pts) { float s = 0f; for (int k = 1; k < pts.Length; k++) s += pts[k].DistanceTo(pts[k - 1]); return s; }
            if (p1.Length > 0 && p2.Length > 0 && p1[^1].DistanceTo(p2[^1]) < 0.5f && MathF.Abs(L(p1) - L(p2)) < 1f) same++;
        }
        GD.Print($"navbench paths: map-wide {pf / tried:0.000} ms, nearby tiles only {pr / tried:0.000} ms, {same}/{tried} identical");
    }

    /// <summary>A cluster of frags on one spot and a lone one beside it: do they merge?</summary>
    void Blasts()
    {
        if (Player.I == null || Effects.Ground == null) return;
        var g = Effects.Ground;
        var fwd = -Player.I.GlobalBasis.Z;
        fwd.Y = 0f;
        fwd = fwd.Normalized();
        var right = fwd.Cross(Vector3.Up);
        int k = (_n - 20) / 4;
        if (k == 0) Player.I.SetPitch(-28f);
        var off = k < 5 ? fwd * 9f + right * (-1.5f + 0.8f * MathF.Sin(k * 2.1f)) + fwd * (0.8f * MathF.Cos(k * 1.7f))
                        : fwd * 8f + right * 4.5f;
        var p = Player.I.GlobalPosition + off;
        p.Y = g.HeightAt(p.X, p.Z);
        Effects.I.Explosion(p, Vector3.Up);
    }

    void SendKeys()
    {
        void Press(string action) => Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
        if (_debug) Press("bot_debug");
        if (_map) Press("map");
        if (_view == "eyes") Press("spectate_view");
        if (_view == "free") Press("spectate_free");
    }
}
