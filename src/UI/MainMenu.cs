using Godot;

namespace Ridgeline;

public partial class MainMenu : Control
{
    static readonly double[] StartHours = { 5.0, 8.0, 12.0, 16.0, 18.5, 22.0, -1.0 };
    static readonly string[] Weathers = { "Clear", "Overcast", "Rain", "Fog", "Random" };
    static readonly float[] TimeScales = { 0f, 1f, 4f, 12f };

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        // Anchors and offsets both: anchors alone leave a control created in code at zero size.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = new ColorRect { Color = new Color(0.07f, 0.08f, 0.08f) };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // Two tabs: the battle maps (everything that was here), and Conquest, whose options are its own.
        var tabs = new TabContainer();
        tabs.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(tabs);
        // Scrolls if the window is too short for everything.
        var scroll = new ScrollContainer { Name = "Battles", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        tabs.AddChild(scroll);
        tabs.AddChild(ConquestPage());
        tabs.CurrentTab = Math.Clamp(Settings.MenuTab, 0, 1);
        if (OS.GetCmdlineUserArgs().Contains("menutab=1")) tabs.CurrentTab = 1; // a screenshot of it (not saved)
        tabs.TabChanged += t => { Settings.MenuTab = (int)t; Settings.Save(); };
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
        // Your squad: one of ALPHA-1's soldiers, or a squad of your own that goes where you go.
        var qrow = new HBoxContainer();
        qrow.AddThemeConstantOverride("separation", 12);
        box.AddChild(qrow);
        qrow.AddChild(new Label { Text = "Your squad:" });
        var squads = new OptionButton
        {
            CustomMinimumSize = new Vector2(220, 0),
            TooltipText = "Territory. Join ALPHA-1 and you're one of its soldiers, and it goes where its leader and the commander send it. "
                        + "A squad of your own is an extra squad you lead: its bots follow you, and the commander gives it no orders. "
                        + "Each other side gets an extra squad of the same size, so it's no edge for you.",
        };
        squads.AddItem("Join ALPHA-1");
        foreach (int sz in Settings.OwnSquadSizes) squads.AddItem(sz == 0 ? "Own squad: just me" : $"Own squad of {sz}");
        squads.Selected = Math.Max(0, Array.IndexOf(Settings.OwnSquadSizes, Settings.OwnSquad) + 1);
        var squadNote = new Label { Modulate = new Color(1, 1, 1, 0.6f) };
        void ShowSquadNote() => squadNote.Text = Settings.OwnSquad >= 1 ? "You lead them, whatever role you picked."
                                               : Settings.OwnSquad == 0 ? "No squad: you go your own way." : "";
        ShowSquadNote();
        squads.ItemSelected += i => { Settings.OwnSquad = i == 0 ? -1 : Settings.OwnSquadSizes[(int)i - 1]; Settings.Save(); ShowSquadNote(); };
        qrow.AddChild(squads);
        qrow.AddChild(squadNote);
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
        var len = new OptionButton { TooltipText = "Territory: a side loses when its headquarters is overrun or its tickets run out. If neither has happened by then, the side holding the most ground wins." };
        foreach (int m in Settings.MatchLengths) len.AddItem(m == 0 ? "No time limit" : $"{m / 60} h limit");
        len.Selected = Math.Max(0, Array.IndexOf(Settings.MatchLengths, Settings.MatchMinutes));
        len.ItemSelected += i => { Settings.MatchMinutes = Settings.MatchLengths[i]; Settings.Save(); };
        mrow.AddChild(len);
        mrow.AddChild(blurb);
        // The time of day, the weather and how fast the clock runs: a three-hour match at 4x goes from noon
        // through dusk into the night.
        var crow = new HBoxContainer();
        crow.AddThemeConstantOverride("separation", 12);
        box.AddChild(crow);
        crow.AddChild(new Label { Text = "Conditions:" });
        OptionButton Cond(string tip, string[] items, int selected, Action<int> set)
        {
            var o = new OptionButton { TooltipText = tip, CustomMinimumSize = new Vector2(150, 0) };
            foreach (var it in items) o.AddItem(it);
            o.Selected = Math.Clamp(selected, 0, items.Length - 1);
            o.ItemSelected += i => { set((int)i); Settings.Save(); };
            crow.AddChild(o);
            return o;
        }
        int hourAt = Array.FindIndex(StartHours, h => Math.Abs(h - Settings.StartHour) < 0.01);
        Cond("When the match starts. Night is dark: a full moon lets you see, a moonless or overcast night hardly at all.",
            new[] { "Dawn 05:00", "Morning 08:00", "Noon 12:00", "Afternoon 16:00", "Dusk 18:30", "Night 22:00", "Random time" },
            hourAt >= 0 ? hourAt : Settings.StartHour < 0 ? StartHours.Length - 1 : 2, i => Settings.StartHour = StartHours[i]);
        int wxAt = Array.FindIndex(Weathers, x => x.Equals(Settings.Weather, StringComparison.OrdinalIgnoreCase));
        Cond("The weather, fixed for the match. Rain and fog cut how far anyone can see; rain covers sound.",
            new[] { "Clear", "Overcast", "Rain", "Fog", "Random weather" }, wxAt >= 0 ? wxAt : Weathers.Length - 1, i => Settings.Weather = Weathers[i]);
        Cond("How fast the time of day runs against the match.",
            new[] { "Clock stopped", "Real time", "4x time", "12x time" },
            Math.Max(0, Array.FindIndex(TimeScales, t => MathF.Abs(t - Settings.TimeScale) < 0.01f)), i => Settings.TimeScale = TimeScales[i]);
        box.AddChild(new Label { Text = "A map's first launch bakes its navigation (up to a minute on the 5 km maps); it's cached after that.", Modulate = new Color(1, 1, 1, 0.4f), AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 0) });
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

        // Graphics: behind a toggle, so it doesn't push the game modes down.
        var gfxToggle = new Button { Text = "Graphics settings ▾", Flat = true, Alignment = HorizontalAlignment.Left };
        box.AddChild(gfxToggle);
        var gfx = new GridContainer { Columns = 4, Visible = false };
        gfx.AddThemeConstantOverride("h_separation", 12);
        gfx.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(gfx);
        gfxToggle.Pressed += () => { gfx.Visible = !gfx.Visible; gfxToggle.Text = gfx.Visible ? "Graphics settings ▴" : "Graphics settings ▾"; };
        OptionButton Pick(string label, string[] items, int selected, Action<int> set)
        {
            gfx.AddChild(new Label { Text = label });
            var o = new OptionButton { CustomMinimumSize = new Vector2(170, 0) };
            foreach (var it in items) o.AddItem(it);
            o.Selected = Math.Clamp(selected, 0, items.Length - 1);
            o.ItemSelected += i => { set((int)i); Settings.ApplyGraphics(); Settings.Save(); };
            gfx.AddChild(o);
            return o;
        }
        Pick("Display", new[] { "Windowed", "Borderless fullscreen", "Exclusive fullscreen" }, (int)Settings.Display, i => Settings.Display = (Settings.DisplayMode)i);
        Pick("V-sync", new[] { "On", "Off" }, Settings.VSync ? 0 : 1, i => Settings.VSync = i == 0);
        Pick("Frame cap", Settings.FpsCaps.Select(f => f == 0 ? "None" : $"{f} fps").ToArray(), Array.IndexOf(Settings.FpsCaps, Settings.MaxFps), i => Settings.MaxFps = Settings.FpsCaps[i]);
        Pick("Render scale", Settings.Scales.Select(f => $"{f * 100:0}%").ToArray(), Array.FindIndex(Settings.Scales, f => MathF.Abs(f - Settings.RenderScale) < 0.01f), i => Settings.RenderScale = Settings.Scales[i]);
        Pick("Anti-aliasing", new[] { "Off", "MSAA 2x", "MSAA 4x" }, Settings.Msaa, i => Settings.Msaa = i);
        Pick("Shadows", new[] { "Off", "Low", "High" }, Settings.Shadows, i => Settings.Shadows = i);
        box.AddChild(new Label { Text = Controls.Fill("{fullscreen} toggles fullscreen anywhere. Shadows apply from the next match."), Modulate = new Color(1, 1, 1, 0.4f) });

        // Controls: every binding, rebindable, behind a toggle like the graphics.
        var ctlToggle = new Button { Text = "Controls ▾", Flat = true, Alignment = HorizontalAlignment.Left };
        box.AddChild(ctlToggle);
        var ctl = new VBoxContainer { Visible = false };
        ctl.AddThemeConstantOverride("separation", 4);
        box.AddChild(ctl);
        ctlToggle.Pressed += () => { ctl.Visible = !ctl.Visible; ctlToggle.Text = ctl.Visible ? "Controls ▴" : "Controls ▾"; };
        BuildControls(ctl);

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

        var hint = new Label { Text = Controls.Fill("{main_menu} returns to this menu from anywhere"), HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0.45f) };
        box.AddChild(hint);
    }

    // ---------------------------------------------------------------- Conquest

    static readonly double[] ConquestHours = { 6.0, 9.0, 12.0, 15.0, 18.0, 22.0 };
    static readonly int[] ConquestCaps = { 100, 160, 220 };

    /// <summary>
    /// The Conquest tab: you're one soldier in the war on an island, a grunt in one of its squads, wherever the fighting
    /// is. The war is run to the day and hour picked, and you join it there (WindowMode, ConquestPlan).
    /// </summary>
    Control ConquestPage()
    {
        var scroll = new ScrollContainer { Name = "Conquest", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var center = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        scroll.AddChild(center);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 9);
        center.AddChild(box);
        var title = new Label { Text = "CONQUEST", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 44);
        box.AddChild(title);
        Label Note(string text, float alpha) => new()
        {
            Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(540, 0), Modulate = new Color(1, 1, 1, alpha),
        };
        box.AddChild(Note("An island at war: three armies of about 50,000 each, fighting for it. You're one soldier among them, a "
                          + "grunt in one of the war's own squads, with the name and the unit the war gave you. The war goes on round "
                          + "you: your squad does what its company is ordered to, and what happens near you is the war's. Killed, you "
                          + "carry on as one of your squad; with nobody of yours left, you come in as a replacement, by sea.", 0.75f));
        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 8);
        box.AddChild(grid);
        void Row(string label, Control c, string tip)
        {
            grid.AddChild(new Label { Text = label, TooltipText = tip, MouseFilter = MouseFilterEnum.Pass });
            c.TooltipText = tip;
            grid.AddChild(c);
        }
        var seed = new SpinBox { MinValue = 1, MaxValue = 999, Value = Settings.ConquestSeed, CustomMinimumSize = new Vector2(120, 0) };
        seed.ValueChanged += v => { Settings.ConquestSeed = (int)v; Settings.Save(); };
        Row("Island", seed, "Each number is a different island, and a different war on it. The same number is the same island and the same war.");
        var when = new HBoxContainer();
        when.AddThemeConstantOverride("separation", 8);
        var day = new SpinBox { MinValue = 1, MaxValue = 30, Value = Settings.ConquestDay, Prefix = "day", CustomMinimumSize = new Vector2(120, 0) };
        day.ValueChanged += v => { Settings.ConquestDay = (int)v; Settings.Save(); };
        when.AddChild(day);
        var hour = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
        foreach (var h in new[] { "Dawn 06:00", "Morning 09:00", "Noon 12:00", "Afternoon 15:00", "Evening 18:00", "Night 22:00" }) hour.AddItem(h);
        hour.Selected = Math.Max(0, Array.FindIndex(ConquestHours, h => Math.Abs(h - Settings.ConquestHour) < 0.01));
        hour.ItemSelected += i => { Settings.ConquestHour = ConquestHours[i]; Settings.Save(); };
        when.AddChild(hour);
        Row("Join the war on", when, "The war is fought from its first day without you up to then (about 8 seconds a day to run), and you join it where it has got to.");
        var side = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
        foreach (var s in new[] { "Wherever the biggest fight is", "ALPHA (an army like the US's)", "BRAVO (an army like Russia's)", "CHARLIE (an army like Britain's)" }) side.AddItem(s);
        side.Selected = Math.Clamp(Settings.ConquestSide + 1, 0, 3);
        side.ItemSelected += i => { Settings.ConquestSide = (int)i - 1; Settings.Save(); };
        Row("Your side", side, "Picking a side puts you where that side's biggest fight is. Otherwise you're in the biggest fight going, on whichever side has a squad nearest its middle.");
        var cap = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
        foreach (var c in new[] { "100 (lighter)", "160", "220 (heavy)" }) cap.AddItem(c);
        cap.Selected = Math.Max(0, Array.IndexOf(ConquestCaps, Settings.ConquestCap));
        cap.ItemSelected += i => { Settings.ConquestCap = ConquestCaps[i]; Settings.Save(); };
        Row("Soldiers round you", cap, "How many of the war's soldiers are there in person round you (the rest of the war goes on as numbers). More is heavier on the computer.");
        var live = new CheckBox { Text = "The war goes on round you", ButtonPressed = Settings.ConquestLive };
        live.Toggled += on => { Settings.ConquestLive = on; Settings.Save(); };
        Row("", live, "Off: the war stops where it was when you joined, and only the soldiers round you fight on.");

        box.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
        var enlist = new Button { Text = "ENLIST", CustomMinimumSize = new Vector2(540, 46) };
        enlist.AddThemeFontSizeOverride("font_size", 22);
        enlist.Pressed += () => WindowMode.Launch(this, new ConquestPlan
        {
            Seed = Settings.ConquestSeed, Day = Settings.ConquestDay, Hour = Settings.ConquestHour, Side = Settings.ConquestSide,
            Cap = Settings.ConquestCap, Live = Settings.ConquestLive, Join = true,
        });
        box.AddChild(enlist);
        box.AddChild(Note("Making the island takes about 20 seconds, and running its war to the day picked about 8 seconds a day. The first "
                          + "time a stretch of the island is played its ground is baked for walking and driving (a minute or two); after that it loads in seconds.", 0.45f));
        box.AddChild(Note(Controls.Fill("In the war: {map} the map (Tab in it: the whole island; the wheel zooms). Killed: 1-9 pick who you carry on as. "
                                        + "Walk near the edge of the ground round you and it's built again round you. {main_menu} comes back here."), 0.45f));
        return scroll;
    }

    // ---------------------------------------------------------------- controls

    readonly Dictionary<(string Id, int Slot), Button> _keyButtons = new();
    readonly Dictionary<string, Button> _resetButtons = new();
    (string Id, int Slot)? _capture;
    Label _conflicts = null!;

    void BuildControls(VBoxContainer ctl)
    {
        ctl.AddChild(new Label
        {
            Text = "Click a key, then press the new key or mouse button (Esc cancels, Backspace clears it). "
                 + "Two things can share a key only if they're never used in the same place (on foot, in a vehicle, flying a drone, spectating).",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 0), Modulate = new Color(1, 1, 1, 0.55f),
        });
        _conflicts = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(440, 0), Modulate = new Color(1f, 0.55f, 0.45f) };
        ctl.AddChild(_conflicts);
        foreach (var g in Controls.All.GroupBy(a => a.Group))
        {
            var head = new Label { Text = g.Key.ToUpperInvariant(), Modulate = new Color(1, 1, 1, 0.5f) };
            head.AddThemeFontSizeOverride("font_size", 13);
            ctl.AddChild(head);
            var grid = new GridContainer { Columns = 4 };
            grid.AddThemeConstantOverride("h_separation", 8);
            grid.AddThemeConstantOverride("v_separation", 3);
            ctl.AddChild(grid);
            foreach (var a in g)
            {
                grid.AddChild(new Label { Text = a.Label, TooltipText = a.Note, CustomMinimumSize = new Vector2(250, 0), MouseFilter = MouseFilterEnum.Pass });
                for (int slot = 0; slot < 2; slot++)
                {
                    var b = new Button { CustomMinimumSize = new Vector2(92, 0), TooltipText = a.Note };
                    int s = slot;
                    b.Pressed += () => StartCapture(a.Id, s);
                    grid.AddChild(b);
                    _keyButtons[(a.Id, slot)] = b;
                }
                var reset = new Button { Text = "↺", TooltipText = "Back to the default", Flat = true, CustomMinimumSize = new Vector2(28, 0) };
                reset.Pressed += () => { Controls.Reset(a.Id); RefreshControls(); };
                grid.AddChild(reset);
                _resetButtons[a.Id] = reset;
            }
        }
        var all = new Button { Text = "Reset all controls to the defaults", CustomMinimumSize = new Vector2(300, 0) };
        all.Pressed += () => { Controls.ResetAll(); RefreshControls(); };
        ctl.AddChild(all);
        RefreshControls();
    }

    void StartCapture(string id, int slot)
    {
        _capture = (id, slot);
        RefreshControls();
    }

    void RefreshControls()
    {
        var clash = Controls.Conflicts();
        var hot = new HashSet<(string, string)>();
        foreach (var (a, b, binding) in clash) { hot.Add((a.Id, binding)); hot.Add((b.Id, binding)); }
        foreach (var ((id, slot), btn) in _keyButtons)
        {
            var cur = Controls.Current(id);
            string? binding = slot < cur.Length ? cur[slot] : null;
            btn.Text = _capture == (id, slot) ? "press a key…" : binding != null ? Controls.Name(binding) : "—";
            btn.Modulate = binding != null && hot.Contains((id, binding)) ? new Color(1f, 0.45f, 0.4f) : Colors.White;
        }
        foreach (var (id, reset) in _resetButtons) reset.Visible = !Controls.IsDefault(id);
        _conflicts.Text = clash.Count == 0 ? "" : "Same key, same place: " + string.Join("; ", clash.Select(c => $"{Controls.Name(c.Binding)} is both {c.A.Label} and {c.B.Label}"));
        _conflicts.Visible = clash.Count > 0;
    }

    public override void _Input(InputEvent e)
    {
        if (_capture is not (string id, int slot)) return;
        string? binding;
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.PhysicalKeycode == Key.Escape) { _capture = null; RefreshControls(); GetViewport().SetInputAsHandled(); return; }
            binding = k.PhysicalKeycode is Key.Backspace or Key.Delete ? null : Controls.FromEvent(k);
        }
        else if (e is InputEventMouseButton { Pressed: true } mb) binding = Controls.FromEvent(mb);
        else return;
        _capture = null;
        Controls.Set(id, slot, binding);
        RefreshControls();
        GetViewport().SetInputAsHandled();
    }

    void Add(VBoxContainer box, string text, GameSetup setup)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(440, 30) };
        b.AddThemeFontSizeOverride("font_size", 18);
        b.Pressed += () => Main.Launch(this, setup);
        box.AddChild(b);
    }
}
