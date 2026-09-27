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

    void BuildEnvironment(Biome biome)
    {
        bool desert = biome == Biome.Desert, urban = biome == Biome.Urban, high = biome == Biome.Highlands;
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky
            {
                SkyMaterial = new ProceduralSkyMaterial
                {
                    SkyTopColor = desert ? new Color(0.36f, 0.5f, 0.68f) : new Color(0.32f, 0.45f, 0.62f),
                    SkyHorizonColor = desert ? new Color(0.86f, 0.8f, 0.68f) : urban ? new Color(0.7f, 0.72f, 0.74f) : new Color(0.72f, 0.76f, 0.8f),
                    GroundHorizonColor = new Color(0.6f, 0.6f, 0.6f),
                    GroundBottomColor = new Color(0.3f, 0.3f, 0.3f),
                },
            },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            FogEnabled = true,
            // Desert haze is warm and a little thicker; the city's air greyer; the highlands' clear.
            FogLightColor = desert ? new Color(0.84f, 0.77f, 0.64f) : urban ? new Color(0.66f, 0.68f, 0.7f) : new Color(0.68f, 0.73f, 0.79f),
            FogDensity = desert ? 0.00042f : urban ? 0.0004f : high ? 0.00026f : 0.00035f,
            FogAerialPerspective = 0.6f,
            FogSkyAffect = 0.3f,
            SsaoEnabled = true,
            GlowEnabled = true,
        };
        AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D
        {
            ShadowEnabled = Settings.Shadows > 0,
            LightEnergy = desert ? 1.55f : 1.3f,
            LightColor = desert ? new Color(1f, 0.93f, 0.82f) : new Color(1f, 0.96f, 0.9f),
            DirectionalShadowMaxDistance = Settings.Shadows >= 2 ? 250f : 90f,
        };
        AddChild(sun);
        sun.RotationDegrees = new Vector3(-38f, -35f, 0f);
    }
}
