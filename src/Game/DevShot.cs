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
            _ => null,
        };
        // level=alhamra etc.: which battlefield.
        if (setup != null && a.TryGetValue("level", out var lv)) setup.MapId = lv;
        if (a.TryGetValue("front", out var fr)) Settings.FrontLine = fr != "0";
        return setup;
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
        };
        parent.AddChild(d);
    }

    public override void _Process(double delta)
    {
        _n++;
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
        if (_n < _frames) return;
        GD.Print(SoundWorld.Report);
        GD.Print($"devshot: {(_frames - 60) / 60.0:0}s of game in {_wall.Elapsed.TotalSeconds:0.0}s wall after startup");
        GD.Print($"devshot: t={Clock.Now:0.0}s combatants={Combatants.All.Count} alive={Combatants.All.Count(c => c.Alive)} " +
                 $"bullets={Ballistics.I?.LiveCount} pendingSounds={SoundWorld.I?.PendingCount}");
        foreach (var kv in Prof.Totals.OrderByDescending(k => k.Value.Ms))
            GD.Print($"prof {kv.Key,-11} {kv.Value.Ms,9:0} ms total  {kv.Value.Calls,8} calls  {kv.Value.Ms / Math.Max(1, kv.Value.Calls),7:0.000} ms/call");
        foreach (var kv in Ballistics.NearBlockTags.OrderByDescending(k => k.Value))
            GD.Print($"nearblock {kv.Key}: {kv.Value} of {Ballistics.ShotTags.GetValueOrDefault(kv.Key)}");
        // Headless runs have nothing rendered; they're used to smoke-test code paths.
        if (DisplayServer.GetName() != "headless") GetViewport().GetTexture().GetImage().SavePng(_path);
        GetTree().Quit();
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
        void Key(Godot.Key k) => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = k, Pressed = true });
        if (_debug) Key(Godot.Key.F6);
        if (_map) Key(Godot.Key.M);
        if (_view == "eyes") Key(Godot.Key.C);
        if (_view == "free") Key(Godot.Key.F);
    }
}
