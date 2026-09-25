using Godot;

namespace Ridgeline;

public partial class MainMenu : Control
{
    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        // Anchors and offsets both: anchors alone leave a control created in code at zero size.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = new Color(0.07f, 0.08f, 0.08f) };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // Scrolls if the window is too short for everything.
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(scroll);
        var center = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        scroll.AddChild(center);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 7);
        center.AddChild(box);

        var title = new Label { Text = "RIDGELINE", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 48);
        box.AddChild(title);
        var sub = new Label { Text = "prototype", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.5f) };
        box.AddChild(sub);
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 16) });

        // Settings: your role in the squad, and volume.
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        box.AddChild(row);
        row.AddChild(new Label { Text = "Your role:" });
        var roles = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
        foreach (var r in TerritoryMode.PlayerRoles) roles.AddItem(Roles.Name(r));
        roles.Selected = Math.Max(0, Array.IndexOf(TerritoryMode.PlayerRoles, Settings.PlayerRole));
        roles.ItemSelected += i => { Settings.PlayerRole = TerritoryMode.PlayerRoles[i]; Settings.Save(); };
        row.AddChild(roles);
        var vol = new Label { Text = $"Volume {Settings.Volume * 100:0}%", CustomMinimumSize = new Vector2(110, 0) };
        row.AddChild(vol);
        var slider = new HSlider { MinValue = 0, MaxValue = 2, Step = 0.05, Value = Settings.Volume, CustomMinimumSize = new Vector2(160, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        slider.ValueChanged += v => { Settings.Volume = (float)v; Settings.Apply(); vol.Text = $"Volume {v * 100:0}%"; };
        slider.DragEnded += _ => Settings.Save();
        row.AddChild(slider);
        // The battlefield for Territory and KOTH.
        var mrow = new HBoxContainer();
        mrow.AddThemeConstantOverride("separation", 12);
        box.AddChild(mrow);
        mrow.AddChild(new Label { Text = "Battlefield:" });
        var maps = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
        foreach (var m in MapSpec.All) maps.AddItem(m.Name);
        var cur = MapSpec.Get(Settings.Map);
        maps.Selected = Array.IndexOf(MapSpec.All, cur);
        var blurb = new Label { Text = cur.Blurb, Modulate = new Color(1, 1, 1, 0.6f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(300, 0) };
        maps.ItemSelected += i => { Settings.Map = MapSpec.All[i].Id; Settings.Save(); blurb.Text = MapSpec.All[i].Blurb; };
        mrow.AddChild(maps);
        var front = new CheckBox { Text = "Front line", ButtonPressed = Settings.FrontLine, TooltipText = "Territory: points are linked, and you can only attack one linked to ground you hold (Hell Let Loose style)." };
        front.Toggled += on => { Settings.FrontLine = on; Settings.Save(); };
        mrow.AddChild(front);
        mrow.AddChild(blurb);
        box.AddChild(new Label { Text = "A map's first launch bakes its navigation (up to a minute on the 5 km maps); it's cached after that.", Modulate = new Color(1, 1, 1, 0.4f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 0) });
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

        Add(box, "TERRITORY — 3 factions × 12", new GameSetup { Map = "valley", TeamSize = 12 });
        Add(box, "TERRITORY — 3 factions × 20", new GameSetup { Map = "valley", TeamSize = 20 });
        Add(box, "TERRITORY — 3 factions × 33  (heavy)", new GameSetup { Map = "valley", TeamSize = 33 });
        Add(box, "King of the hill — 3 factions × 12", new GameSetup { Map = "valley", Mode = "koth", TeamSize = 12 });
        Add(box, "Spectate — Territory × 12", new GameSetup { Map = "valley", TeamSize = 12, PlayerJoins = false });
        Add(box, "Spectate — Territory × 20", new GameSetup { Map = "valley", TeamSize = 20, PlayerJoins = false });
        Add(box, "Spectate — Territory × 33  (heavy)", new GameSetup { Map = "valley", TeamSize = 33, PlayerJoins = false });
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        Add(box, "Firing range", new GameSetup { Map = "range" });
        Add(box, "Duel arena — 1v1  (you vs a bot)", new GameSetup { Map = "arena", TeamSize = 1 });
        Add(box, "Duel arena — 2v2  (you + a bot)", new GameSetup { Map = "arena", TeamSize = 2 });
        Add(box, "Duel arena — 5v5  (you + 4 bots)", new GameSetup { Map = "arena", TeamSize = 5 });
        Add(box, "Battle — 8v8", new GameSetup { Map = "arena", TeamSize = 8 });
        Add(box, "Battle — 12v12", new GameSetup { Map = "arena", TeamSize = 12 });
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        Add(box, "Spectate — 1v1 bots", new GameSetup { Map = "arena", TeamSize = 1, PlayerJoins = false });
        Add(box, "Spectate — 5v5 bots", new GameSetup { Map = "arena", TeamSize = 5, PlayerJoins = false });
        Add(box, "Spectate — 12v12 bots", new GameSetup { Map = "arena", TeamSize = 12, PlayerJoins = false });
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        var quit = new Button { Text = "Quit", CustomMinimumSize = new Vector2(440, 40) };
        quit.Pressed += () => GetTree().Quit();
        box.AddChild(quit);

        var hint = new Label { Text = "F10 returns to this menu from anywhere", HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.45f) };
        box.AddChild(hint);
    }

    void Add(VBoxContainer box, string text, GameSetup setup)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(440, 30) };
        b.AddThemeFontSizeOverride("font_size", 18);
        b.Pressed += () => Main.Launch(this, setup);
        box.AddChild(b);
    }
}
