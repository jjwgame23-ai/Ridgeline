using Godot;

namespace Ridgeline;

public partial class Main : Node3D
{
    static GameSetup? _pending;
    static bool _argsUsed, _menuShot;

    /// <summary>Restart the scene into the given mode, or into the menu when null.</summary>
    public static void Launch(Node from, GameSetup? setup)
    {
        _pending = setup;
        from.GetTree().ReloadCurrentScene();
    }

    public override void _Ready()
    {
        Controls.Register();
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("bindsdoc="))
            {
                // KEYBINDS.md, from the list in Controls (a relative path is from the project folder).
                string p = a["bindsdoc=".Length..];
                Controls.WriteDoc(System.IO.Path.IsPathRooted(p) ? p : ProjectSettings.GlobalizePath("res://" + p));
                GD.Print($"bindsdoc: wrote {p}");
            }
        Settings.Load();
        Combatants.Clear();
        GetTree().Paused = false; // (a scene reloaded from the pause screen)
        AddChild(new Clock());

        var setup = _pending;
        _pending = null;
        if (setup == null && !_argsUsed)
        {
            _argsUsed = true;
            setup = DevShot.SetupFromArgs();
        }
        if (setup == null)
        {
            AddChild(new MainMenu());
            if (!_menuShot) { _menuShot = true; DevShot.AttachIfRequested(this); } // a screenshot of the menu itself
            return;
        }

        AddChild(new PauseOverlay());

        // The hour and the weather, before anything that lights, sees or hears by them.
        double hour = setup.Hour >= -1 ? setup.Hour : Settings.StartHour;
        string wx = setup.Weather != "" ? setup.Weather : Settings.Weather;
        WeatherKind? weather = Enum.TryParse<WeatherKind>(wx, true, out var wk) ? wk : null; // "Random" and anything else: random
        Conditions.Start(hour, weather, setup.TimeScale >= 0 ? setup.TimeScale : Settings.TimeScale, setup.Seed ^ (int)Time.GetTicksMsec(), setup.Moon);

        // Order matters: systems first, since everything after emits sounds and effects. The air
        // and ground of the map go into how every sound is made, so they're set before.
        var biome = setup.Map == "valley" ? MapSpec.Get(string.IsNullOrEmpty(setup.MapId) ? Settings.Map : setup.MapId).Biome : Biome.Temperate;
        Acoustics.SetClimate(biome);
        AddChild(new SoundWorld());
        DevShot.DumpSoundsIfRequested(this);
        AddChild(new Ballistics());
        AddChild(new Effects());
        BuildEnvironment(biome);

        if (setup.Map == "arena") BuildArena(setup);
        else if (setup.Map == "valley") BuildValley(setup);
        else BuildRange();

        DevShot.AttachIfRequested(this);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("main_menu")) Launch(this, null);
        if (e.IsActionPressed("fullscreen")) Settings.ToggleFullscreen();
    }

    void BuildRange()
    {
        var terrain = new Terrain();
        AddChild(terrain);
        terrain.Generate(1337);
        Effects.Ground = terrain;
        AddChild(new FiringRange { Terrain = terrain });

        var player = new Player();
        AddChild(player);
        player.GlobalPosition = new Vector3(0f, terrain.HeightAt(0f, 4f) + 0.3f, 4f);

        var drill = new SniperDrill { Terrain = terrain };
        AddChild(drill);
        var war = new DistantWar { Terrain = terrain };
        AddChild(war);
        AddChild(new Hud { P = player, Drill = drill, War = war });
    }

    void BuildValley(GameSetup setup)
    {
        var valley = new Valley();
        AddChild(valley);
        valley.Build(setup.Seed, MapSpec.Get(string.IsNullOrEmpty(setup.MapId) ? Settings.Map : setup.MapId));
        Effects.Ground = valley;
        var hud = new Hud { HelpText = setup.Mode == "koth" ? Hud.KothHelp : Hud.TerritoryHelp, ShowHelp = setup.PlayerJoins };
        AddChild(hud);
        if (setup.Mode == "vtest") AddChild(new VehicleTest { Map = valley, PlayerHud = hud });
        else if (setup.Mode == "koth") AddChild(new KothMode { Map = valley, Setup = setup, PlayerHud = hud });
        else AddChild(new TerritoryMode { Map = valley, Setup = setup, PlayerHud = hud });
    }

    void BuildArena(GameSetup setup)
    {
        var arena = new Arena();
        AddChild(arena);
        arena.Build();
        Effects.Ground = arena;
        var hud = new Hud { HelpText = Hud.ArenaHelp, ShowHelp = setup.PlayerJoins };
        AddChild(hud);
        AddChild(new DuelMode { Arena = arena, Setup = setup, PlayerHud = hud });
    }

    /// <summary>The sky, the sun and moon, the fog and the rain: SkyView, driven by the time of day and the weather.</summary>
    void BuildEnvironment(Biome biome) => AddChild(new SkyView { Biome = biome });
}
