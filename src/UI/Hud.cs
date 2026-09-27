using Godot;

namespace Ridgeline;

public partial class Hud : CanvasLayer
{
    public static Hud? I { get; private set; }
    public static ulong CapturedAtMs;

    public Player? P;
    public bool ShowHelp = true;
    public SniperDrill? Drill;
    public DistantWar? War;

    Label _debug = null!, _toast = null!, _help = null!;
    HudOverlay _overlay = null!;
    TextureRect _vignette = null!;
    ColorRect _hurt = null!;
    float _toastT;
    bool _debugOn = true;

    public string HelpText = RangeHelp;

    public const string ArenaHelp =
        "RIDGELINE — duel arena\n\n" +
        "WASD move · Shift sprint · Space jump / stand up\n" +
        "C crouch · Z prone · Q / E lean\n" +
        "LMB fire · RMB aim (hold) · Shift while aiming: hold breath\n" +
        "R reload · V fire mode · T check mag · G frag grenade\n" +
        "1 carbine (red dot) · 2 marksman rifle (4x scope)\n\n" +
        "F1 this help · F3 debug overlay · F6 bot debug\n" +
        "Esc release mouse · F10 main menu · F11 fullscreen\n\n" +
        "Bots notice you gradually — distance, movement, stance and\n" +
        "gunfire all matter. They hear footsteps and shots too.\n" +
        "When you die you spectate your team until the next round.";

    public const string KothHelp =
        "RIDGELINE — king of the hill\n\n" +
        "Three factions fight over one settlement at a time. Every 5 s the\n" +
        "side with the most people alive inside the zone scores a point.\n" +
        "The zone moves every 8 minutes. First to 300 wins.\n\n" +
        "The yellow diamond on the compass points at the zone. M opens the map.\n" +
        "You respawn at your side's rally point 10 s after dying.\n\n" +
        "WASD move · Shift sprint · C crouch · Z prone · Q / E lean\n" +
        "LMB fire · RMB aim · R reload · V fire mode · G frag · 1 / 2 weapons\n" +
        "F1 this help · F3 debug · F6 bot debug · F10 main menu · F11 fullscreen";

    public const string TerritoryHelp =
        "RIDGELINE — territory\n\n" +
        "Three factions fight for every settlement at once. Stand on a point with\n" +
        "more people than anyone else to neutralise it, then capture it.\n" +
        "Every death costs a ticket, and a side holding fewer points than the\n" +
        "leader bleeds tickets. Out of tickets: no more reinforcements.\n\n" +
        "You're in a squad; the compass diamond points at its objective.\n" +
        "M map (pick a spawn there when dead). As squad leader: click a point to\n" +
        "send your squad there, B to call them onto you. Otherwise follow your SL.\n" +
        "Roles: pick yours in the menu, or with 1-8 while waiting to respawn.\n" +
        "H: your role's tool (medic: patch up · engineer: sandbags · drone operator: quad; J / K: FPV / AT FPV).\n" +
        "Green triangles: your squad. The lines under the order tell you what the squad is doing and your part in it;\n" +
        "the green diamond is your spot. Squad leader: N for squad commands.\n\n" +
        "WASD move · Shift sprint · C crouch · Z prone · Q / E lean\n" +
        "LMB fire · RMB aim · R reload · V fire mode · G frag · 1 / 2 weapons\n" +
        "F1 this help · F3 debug · F6 bot debug · F7/F8 volume · F10 main menu · F11 fullscreen";

    const string RangeHelp =
        "RIDGELINE — firing range prototype\n\n" +
        "WASD move · Shift sprint · Space jump / stand up\n" +
        "C crouch · Z prone · Q / E lean\n" +
        "LMB fire · RMB aim (hold) · Shift while aiming: hold breath\n" +
        "R reload · V fire mode · T check mag · G frag grenade\n" +
        "1 carbine (red dot) · 2 marksman rifle (4x scope)\n\n" +
        "F1 this help · F3 debug overlay · F4 sniper drill (range)\n" +
        "F5 distant battle on/off (range) · F6 bot debug (arena)\n" +
        "Esc release mouse · F10 main menu · F11 fullscreen\n\n" +
        "Steel plates from 25 to 800 m. Watch for the swing,\n" +
        "then listen: the ring arrives at the speed of sound.\n" +
        "4x reticle dots are 1 mil apart; the SR-25 is zeroed at 200 m.";

    public override void _EnterTree() => I = this;

    public static void Toast(string text, float seconds = 4f)
    {
        if (I == null) return;
        I._toast.Text = text;
        I._toastT = seconds;
    }

    public override void _Ready()
    {
        _vignette = new TextureRect
        {
            Texture = VignetteTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0),
        };
        _vignette.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_vignette);

        AddChild(new WoundFx()); // first, so its screen effect sits under the HUD text
        _hurt = new ColorRect { Color = new Color(0.6f, 0f, 0f, 0f), MouseFilter = Control.MouseFilterEnum.Ignore };
        _hurt.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_hurt);

        _overlay = new HudOverlay { MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_overlay);

        _debug = MakeLabel(14);
        _debug.Position = new Vector2(12, 60);

        _toast = MakeLabel(18);
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _toast.AnchorLeft = 0.2f; _toast.AnchorRight = 0.8f;
        _toast.AnchorTop = 1f; _toast.AnchorBottom = 1f;
        _toast.OffsetTop = -150; _toast.OffsetBottom = -70;

        _help = MakeLabel(15);
        _help.Text = HelpText;
        _help.Visible = ShowHelp;
        _help.Position = new Vector2(24, 250);
        var panel = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.55f) };
        panel.SetContentMarginAll(14);
        panel.SetCornerRadiusAll(4);
        _help.AddThemeStyleboxOverride("normal", panel);
        GetTree().CreateTimer(25f).Timeout += () => _help.Visible = false;
    }

    Label MakeLabel(int size)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        l.AddThemeConstantOverride("outline_size", 4);
        AddChild(l);
        return l;
    }

    static Texture2D VignetteTexture()
    {
        var g = new Gradient();
        g.Offsets = new[] { 0f, 0.45f, 1f };
        g.Colors = new[] { new Color(0, 0, 0, 0), new Color(0, 0, 0, 0.1f), new Color(0, 0, 0, 0.92f) };
        return new GradientTexture2D
        {
            Gradient = g, Width = 256, Height = 256,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
        };
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo)
        {
            switch (k.PhysicalKeycode)
            {
                case Key.F1: _help.Visible = !_help.Visible; break;
                case Key.F3: _debugOn = !_debugOn; break;
                case Key.F4: Drill?.Toggle(); break;
                case Key.F5:
                    if (War != null)
                    {
                        War.Enabled = !War.Enabled;
                        Toast(War.Enabled ? "Distant battle: ON" : "Distant battle: OFF (sounds already in the air will still arrive)", 3f);
                    }
                    break;
                case Key.Escape: Input.MouseMode = Input.MouseModeEnum.Visible; break;
                case Key.F7: Settings.Nudge(-0.1f); break;
                case Key.F8: Settings.Nudge(0.1f); break;
            }
        }
        else if (e is InputEventMouseButton mb && mb.Pressed && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            CapturedAtMs = Time.GetTicksMsec();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _toastT -= dt;
        _toast.Visible = _toastT > 0f;
        bool alive = P != null && IsInstanceValid(P) && P.Alive;
        _overlay.Visible = alive;
        _vignette.Modulate = new Color(1, 1, 1, alive ? Mathf.Min(1f, P!.Suppression * 1.1f) : 0f);
        float low = alive ? Mathf.Clamp(1f - P!.Health / 60f, 0f, 1f) : 0f;
        float pulse = low * (0.12f + 0.06f * MathF.Sin((float)Clock.Now * 5f));
        _hurt.Color = new Color(0.6f, 0f, 0f, P != null && IsInstanceValid(P) ? Mathf.Clamp(P.HurtFlash * 0.35f + pulse, 0f, 0.6f) : 0f);
        _overlay.QueueRedraw();
        _debug.Visible = _debugOn && alive;
        if (_debug.Visible) _debug.Text = DebugText();
    }

    string DebugText()
    {
        var P = this.P!;
        var w = P.Weapon;
        string range = "—";
        var from = P.Cam.GlobalPosition;
        var q = PhysicsRayQueryParameters3D.Create(from, from + w.AimDir * 3000f, 0xFFFFFFFF, new Godot.Collections.Array<Rid> { P.GetRid() });
        var hit = P.GetWorld3D().DirectSpaceState.IntersectRay(q);
        if (hit.Count > 0) range = $"{hit["position"].AsVector3().DistanceTo(from):0} m";

        string last = "";
        if (SteelTarget.LastHit != null)
        {
            double ago = Time.GetTicksMsec() / 1000.0 - SteelTarget.LastHitTime;
            if (ago < 8) last = $"\nSteel hit at {SteelTarget.LastHit.Distance:0} m ({ago:0.0}s ago)";
        }

        return
            $"FPS {Engine.GetFramesPerSecond():0}\n" +
            $"{w.Def.Name}  {w.Ammo}/{w.Def.MagSize} +{w.Mags} mags{(w.Def.AutoCapable ? (w.Auto ? "  AUTO" : "  SEMI") : "")}\n" +
            $"Health {P.Health:0}   {P.Stance}  {P.Speed:0.0} m/s  stamina {P.Stamina * 100:0}%  breath {P.Breath * 100:0}%{(P.HoldingBreath ? " (holding)" : "")}\n" +
            $"Suppression {P.Suppression * 100:0}%\n" +
            $"Range to aim point: {range}\n" +
            $"Sound: {SoundWorld.I.ActiveVoices} voices playing, {SoundWorld.I.PendingCount} still travelling, space: {SoundWorld.I.SpaceName}\n" +
            $"Bullets in flight: {Ballistics.I.LiveCount}\n" +
            $"F4 sniper drill: {(Drill?.Active == true ? "ON" : "off")}   F5 distant battle: {(War?.Enabled == true ? "ON" : "off")}" +
            last;
    }
}
