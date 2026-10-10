using Godot;

namespace Ridgeline;

/// <summary>
/// The player's screen in a Conquest window: the day and hour, who you are and in what unit, your squad (roster, its
/// order and the way to it, the briefing), the radio and the kill feed. M opens the map: the window, or with Tab the
/// whole island and the war's front. Killed, you carry on as a squadmate; with your squad gone, in another squad of
/// your side (ConquestWindow.WhileDead).
/// </summary>
public partial class WindowHud : CanvasLayer
{
    public ConquestWindow W = null!;

    static string[] Hex => TerritoryHud.TeamHex;
    RichTextLabel _top = null!, _who = null!, _feed = null!, _comms = null!, _roster = null!, _brief = null!;
    Label _center = null!, _nav = null!, _spec = null!;
    WindowMapView _map = null!;
    readonly List<(string text, double at)> _kills = new(), _lines = new();
    double _centerUntil;
    readonly string _weather = Conditions.Weather.ToString().ToLowerInvariant();

    public override void _Ready()
    {
        Layer = 5;
        // Below the compass, as the battle maps' score line is.
        _top = Rich(17, 0.2f, 0.8f, 58);
        _who = Rich(14, 0.2f, 0.8f, 84);

        _nav = MakeLabel(15);
        _nav.HorizontalAlignment = HorizontalAlignment.Center;
        _nav.AnchorLeft = 0.2f; _nav.AnchorRight = 0.8f;
        _nav.OffsetTop = 110;

        _brief = Rich(15, 0.2f, 0.8f, 150);

        _center = MakeLabel(28);
        _center.HorizontalAlignment = HorizontalAlignment.Center;
        _center.AnchorLeft = 0.15f; _center.AnchorRight = 0.85f;
        _center.AnchorTop = 0.3f; _center.AnchorBottom = 0.3f;

        _feed = Rich(15, 1f, 1f, 14);
        _feed.OffsetLeft = -460; _feed.OffsetRight = -14;
        _feed.HorizontalAlignment = HorizontalAlignment.Left;

        _roster = Rich(14, 1f, 1f, 250);
        _roster.OffsetLeft = -290; _roster.OffsetRight = -14;
        _roster.HorizontalAlignment = HorizontalAlignment.Left;

        _comms = Rich(15, 0f, 0f, 0);
        _comms.AnchorTop = 1f; _comms.AnchorBottom = 1f;
        _comms.OffsetLeft = 16; _comms.OffsetRight = 700; _comms.OffsetTop = -270;
        _comms.HorizontalAlignment = HorizontalAlignment.Left;

        _spec = MakeLabel(15);
        _spec.AnchorTop = 1f; _spec.AnchorBottom = 1f;
        _spec.OffsetLeft = 16; _spec.OffsetTop = -130;

        _map = new WindowMapView { W = W, Visible = false };
        _map.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_map);
    }

    Label MakeLabel(int size)
    {
        var l = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        l.AddThemeConstantOverride("outline_size", 5);
        AddChild(l);
        return l;
    }

    RichTextLabel Rich(int size, float left, float right, float top)
    {
        var r = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        r.AddThemeFontSizeOverride("normal_font_size", size);
        r.AddThemeConstantOverride("outline_size", 5);
        r.AddThemeColorOverride("font_outline_color", Colors.Black);
        r.AnchorLeft = left; r.AnchorRight = right;
        r.OffsetTop = top;
        r.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(r);
        return r;
    }

    string Tag(ICombatant? c) => c == null ? "?" : $"[color={Hex[c.Team]}]{c.Callsign}[/color]";

    public void Center(string text, float seconds)
    {
        _center.Text = text;
        _centerUntil = Clock.Now + seconds;
    }

    public void AddKill(ICombatant? killer, ICombatant victim, string detail)
    {
        _kills.Add(($"{Tag(killer)}  ▸  {Tag(victim)}  [color=#bbbbbb]({detail})[/color]", Clock.Now));
        if (_kills.Count > 8) _kills.RemoveAt(0);
    }

    public void AddComm(string text, bool squad)
    {
        _lines.Add((squad ? $"[color=#b8ffb0]{text}[/color]" : text, Clock.Now));
        if (_lines.Count > 6) _lines.RemoveAt(0);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("map"))
        {
            _map.Visible = !_map.Visible;
            Input.MouseMode = _map.Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (_map.Visible && k.PhysicalKeycode == Key.Tab)
        {
            _map.Island = !_map.Island;
            GetViewport().SetInputAsHandled();
            return;
        }
        // Dead: 1-9 pick who to carry on as.
        if (W.CarryOnAt > 0 && k.PhysicalKeycode >= Key.Key1 && k.PhysicalKeycode <= Key.Key9)
        {
            W.Pick((int)(k.PhysicalKeycode - Key.Key1));
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>The unit: squad, platoon and company (the war's names run on to the division).</summary>
    string UnitLine(Squad sq) => W.UnitOf.TryGetValue(sq, out var u) ? string.Join(", ", u.Name.Split(", ").Take(3)) : sq.Name;

    public override void _Process(double delta)
    {
        double now = Clock.Now;
        var me = W.PlayerBody is { Alive: true } p ? p : null;
        var sq = W.PlayerSquad;

        // The day, and who's here: the soldiers each side has on their feet in the window.
        var here = Enumerable.Range(0, 3).Select(t => (t, n: W.Bots.Count(b => b.Alive && b.Team == t) + (me != null && me.Team == t ? 1 : 0)))
            .Where(x => x.n > 0).Select(x => $"[color={Hex[x.t]}]{W.War.Sides[x.t].Name}[/color] {x.n}");
        _top.Text = $"Day {W.War.Day} · {Conditions.Clock} · {_weather}     [color=#aaaaaa]in the window:[/color] {string.Join("  ", here)}";
        _who.Text = W.Me != "" && sq != null
            ? $"[color=#cfe8ff]{W.Me}[/color] [color=#9aa]· {Roles.Name(me?.Kit ?? Role.Rifleman)} · {UnitLine(sq)}[/color]"
            : "";

        _roster.Text = SquadPanel.Roster(sq);
        var at = me?.FeetPos ?? W.Watched?.FeetPos;
        _nav.Text = me != null ? SquadPanel.Nav(sq, me, at, false) : "";
        _brief.Text = SquadPanel.Brief(sq, me, false);
        if (me == null) HudOverlay.Marker = null;

        _center.Visible = now < _centerUntil;
        _kills.RemoveAll(k => now - k.at > 10);
        _feed.Text = string.Join("\n", _kills.Select(k => k.text));
        _lines.RemoveAll(l => now - l.at > 8);
        _comms.Text = string.Join("\n", _lines.Select(l => l.text));

        var s = W.Spec;
        _spec.Visible = s.Active || W.CarryOnAt > 0;
        if (!_spec.Visible) return;
        string carry = "";
        if (W.CarryOnAt > 0)
        {
            var c = W.Candidates;
            if (W.CarryOnAs is { } next)
            {
                string pick = string.Join("  ", c.Take(9).Select((b, i) => $"[{i + 1}] {b.Callsign} ({Roles.Short(b.Role)}){(b == next ? "◂" : "")}"));
                carry = $"{(W.Replacement ? $"Your squad is gone. With {next.Squad?.Name}: " : "")}carrying on as {next.Callsign} ({Roles.Name(next.Role)}, {(next.Hp < 99f ? $"{next.Hp:0}% fit" : "unhurt")}) in {Math.Max(0, W.CarryOnAt - now):0}s\n"
                        + (c.Count > 1 ? $"Pick: {pick}\n" : "");
            }
            else carry = "Nobody of yours is left in the window.\n";
        }
        var b = W.Watched;
        string who = b == null ? "nobody" :
            $"{b.Callsign} ({Roles.Name(b.Role)}, {b.Squad?.Name ?? W.War.Sides[b.Team].Name}) · {(b.Alive ? $"{b.Health:0} hp" : "dead")} · {b.Brain.State}{(b.Brain.Target != null ? $" → {b.Brain.Target.Who.Callsign}" : "")} · {b.Brain.Note}";
        _spec.Text = carry + (s.Active ? $"WATCHING [{s.ViewMode}]  {who}\n" + Controls.Fill(W.CarryOnAt > 0 ? "[{spectate_view}] chase/eyes · [{map}] map" : "[{spectate_next}/{spectate_prev}] switch · [{spectate_view}] chase/eyes · [{spectate_free}] free cam · [{map}] map") : "");
    }
}

/// <summary>
/// M: the map. The window: the ground, its towns, your side's squads and where they've been sent, your squad's plan
/// (ORP, support and line of departure), vehicles, the enemy your side has seen in the last minute, and your side's fire
/// missions. Tab: the island, the war's ground as each side holds it, its battalions, and the window on it.
/// </summary>
public partial class WindowMapView : Control
{
    public ConquestWindow W = null!;
    public bool Island;
    /// <summary>How far the window's map is zoomed in on you (the mouse wheel).</summary>
    float _zoom = 2f;
    /// <summary>The map's square, clipped: zoomed in, what's outside it isn't drawn over the rest of the screen.</summary>
    Control _pane = null!;
    static readonly Color[] Team = { new(0.5f, 0.66f, 1f), new(1f, 0.55f, 0.44f), new(1f, 0.83f, 0.35f) };
    float _px;
    Vector2 _o;

    ImageTexture? _ctlTex;
    double _ctlAt = -1;
    readonly float[] _held = new float[3];

    /// <summary>The war's ground as each side holds it: a pixel a square, and each side's share of the land.</summary>
    void HoldingsPicture(War war)
    {
        var ctl = war.Ctl;
        var img = Image.CreateEmpty(ctl.N, ctl.N, false, Image.Format.Rgba8);
        var held = new int[3];
        int land = 0;
        for (int c = 0; c < ctl.N * ctl.N; c++)
        {
            if (!ctl.Land[c]) continue;
            land++;
            int o = ctl.Owner[c];
            if (o < 0) continue;
            held[o]++;
            img.SetPixel(c % ctl.N, c / ctl.N, Team[o] with { A = 0.22f });
        }
        for (int t = 0; t < 3; t++) _held[t] = 100f * held[t] / Math.Max(1, land);
        _ctlTex = ImageTexture.CreateFromImage(img);
        _ctlAt = war.Time;
    }

    static Island? _isl;
    static Task<Image>? _islImage;
    static ImageTexture? _islTex;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Island = OS.GetCmdlineUserArgs().Contains("island"); // a screenshot of it (DevShot)
        TextureFilter = TextureFilterEnum.Nearest; // the territory's squares, sharp
        _pane = new Control { ClipContents = true, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.Linear };
        AddChild(_pane);
        _pane.Draw += () => { if (!Island) DrawWindow(_pane); };
        // The island's picture takes a second or two to draw: started now, off the main thread, for when it's wanted.
        if (_isl != W.War.Isl)
        {
            _isl = W.War.Isl;
            _islTex = null;
            var isl = _isl;
            _islImage = Task.Run(() => IslandRender.Draw(isl).ToImage());
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        var size = GetViewportRect().Size;
        _px = size.Y * 0.86f;
        _o = (size - new Vector2(_px, _px)) / 2f;
        _pane.Position = _o;
        _pane.Size = new Vector2(_px, _px);
        QueueRedraw();
        _pane.QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        if (mb.ButtonIndex == MouseButton.WheelUp) _zoom = MathF.Min(8f, _zoom * 1.25f);
        else if (mb.ButtonIndex == MouseButton.WheelDown) _zoom = MathF.Max(1f, _zoom / 1.25f);
        else return;
        AcceptEvent();
    }

    /// <summary>Spectating with nobody to play: every side shown.</summary>
    bool Omni => !W.PlayerJoins;
    int Side => W.PlayerBody?.Team ?? 0;
    bool Shows(int team) => team == Side || Omni;

    static string VehLetter(VKind k) => k switch
    {
        VKind.LTV => "LTV", VKind.Transport => "TRAN", VKind.Logistics => "LOGI", VKind.APC => "APC",
        VKind.IFV => "IFV", VKind.MBT => "MBT", VKind.MGS => "MGS", VKind.UH => "UH", VKind.AH => "AH", VKind.SPAA => "SPAA", VKind.Mortar => "MORT", _ => "?",
    };

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0f, 0f, 0f, 0.55f));
        if (Island) DrawIsland();
    }

    void DrawWindow(Control pane)
    {
        var map = W.Map;
        float world = map.Size;
        // Zoomed in on you (the wheel): the view is world / zoom across, kept inside the window.
        float view = world / _zoom;
        var you = W.PlayerBody is { Alive: true } pl ? pl.FeetPos : W.Watched?.FeetPos ?? (Omni && W.Spec.Active ? W.Spec.GlobalPosition : Vector3.Zero);
        float lim = (world - view) / 2f;
        var centre = new Vector2(Math.Clamp(you.X, -lim, lim), Math.Clamp(you.Z, -lim, lim));
        Vector2 P(Vector3 w) => new Vector2(((w.X - centre.X) / view + 0.5f) * _px, ((w.Z - centre.Y) / view + 0.5f) * _px);
        float scale = _px / view;
        var font = ThemeDB.FallbackFont;
        double now = Clock.Now;

        var tex = MapImage.For(map);
        float tpx = tex.GetWidth() / world;
        var src = new Rect2((centre.X - view / 2f + world / 2f) * tpx, (centre.Y - view / 2f + world / 2f) * tpx, view * tpx, view * tpx);
        pane.DrawTextureRectRegion(tex, new Rect2(Vector2.Zero, new Vector2(_px, _px)), src, new Color(0.92f, 0.92f, 0.92f, 0.97f));
        // A 500 m grid.
        for (float g = -world / 2f + 500f; g < world / 2f; g += 500f)
        {
            float gx = P(new Vector3(g, 0f, 0f)).X, gy = P(new Vector3(0f, 0f, g)).Y;
            pane.DrawLine(new Vector2(gx, 0), new Vector2(gx, _px), new Color(1, 1, 1, 0.08f));
            pane.DrawLine(new Vector2(0, gy), new Vector2(_px, gy), new Color(1, 1, 1, 0.08f));
        }
        foreach (var site in map.Sites)
        {
            var c = P(site.Center);
            pane.DrawArc(c, site.Radius * scale, 0f, Mathf.Tau, 32, new Color(0.95f, 0.95f, 0.9f, 0.35f), 1.2f);
            pane.DrawString(font, c + new Vector2(6, -4), site.Name, HorizontalAlignment.Left, -1, 12, new Color(0.95f, 0.95f, 0.9f, 0.8f));
        }

        // Squads and where they've been sent.
        foreach (var sq in W.Squads.SelectMany(l => l))
        {
            if (!Shows(sq.Team) || sq.Position is not Vector3 at) continue;
            bool mine = sq == W.PlayerSquad;
            var col = mine ? Colors.White : Team[sq.Team] with { A = 0.55f };
            if (sq.Objective != null && (sq.Objective.Center - at with { Y = sq.Objective.Center.Y }).Length() > 30f)
                pane.DrawDashedLine(P(at), P(sq.Objective.Center), col, mine ? 2f : 1f, 8f);
            if (mine || scale > 0.25f) pane.DrawString(font, P(at) + new Vector2(6, -6), sq.Name, HorizontalAlignment.Left, -1, mine ? 13 : 10, mine ? Colors.White : Team[sq.Team]);
        }
        // Your squad's plan.
        if (W.PlayerSquad is { } ps)
        {
            var hi = new Color(0.55f, 1f, 0.55f);
            void Mark(Vector3 at, string label)
            {
                var q = P(at);
                pane.DrawRect(new Rect2(q - new Vector2(4, 4), new Vector2(8, 8)), hi, false, 1.5f);
                pane.DrawString(font, q + new Vector2(7, -5), label, HorizontalAlignment.Left, -1, 11, hi);
            }
            if (ps.Phase != AssaultPhase.None)
            {
                Mark(ps.OrpAt, "ORP");
                if (ps.Phase != AssaultPhase.Orp || ps.Recon != ReconStep.None) { Mark(ps.SbfAt, $"SBF ({Squad.TeamName(ps.SbfTeam)})"); Mark(ps.LdAt, "LD"); }
            }
            if (ps.CrossingNow)
            {
                pane.DrawLine(P(ps.CrossNear), P(ps.CrossFar), hi, 2.5f);
                Mark(ps.CrossNear, "danger area");
            }
            if (ps.Engaged)
            {
                var q = P(ps.ContactAt);
                pane.DrawLine(q + new Vector2(-6, -6), q + new Vector2(6, 6), new Color(1f, 0.35f, 0.3f), 2f);
                pane.DrawLine(q + new Vector2(-6, 6), q + new Vector2(6, -6), new Color(1f, 0.35f, 0.3f), 2f);
            }
        }
        // Your side's fire missions: where they're to land.
        if (W.Fires != null)
            foreach (var (side, target, at) in W.Fires.Missions)
            {
                if (!Shows(side)) continue;
                var q = P(target);
                var fc = new Color(1f, 0.6f, 0.2f);
                pane.DrawArc(q, MathF.Max(5f, 50f * scale), 0f, Mathf.Tau, 24, fc, 2f);
                pane.DrawString(font, q + new Vector2(8, 14), at > now ? $"fire mission, {at - now:0}s" : "fire mission", HorizontalAlignment.Left, -1, 11, fc);
            }
        // What your side has seen of the enemy in the last minute.
        for (int t = 0; t < 3; t++)
        {
            if (!Shows(t) || Omni) continue;
            foreach (var c in global::Ridgeline.Intel.Recent(t, 60.0))
            {
                float fade = 1f - (float)((now - c.At) / 60.0);
                var q = P(c.Pos);
                var red = new Color(1f, 0.25f, 0.2f, 0.35f + 0.65f * fade);
                pane.DrawLine(q + new Vector2(-4, -4), q + new Vector2(4, 4), red, 2f);
                pane.DrawLine(q + new Vector2(-4, 4), q + new Vector2(4, -4), red, 2f);
            }
        }
        foreach (var v in Vehicle.All)
        {
            if (v.Destroyed || !Shows(v.Team)) continue;
            var q = P(v.GlobalPosition);
            pane.DrawRect(new Rect2(q - new Vector2(5, 4), new Vector2(10, 8)), Team[v.Team]);
            pane.DrawString(font, q + new Vector2(7, 4), VehLetter(v.Def.Kind), HorizontalAlignment.Left, -1, 10, Team[v.Team]);
        }
        foreach (var b in W.Bots)
            if (IsInstanceValid(b) && b.Alive && Shows(b.Team))
            {
                bool mate = b.Squad == W.PlayerSquad;
                pane.DrawCircle(P(b.FeetPos), mate ? 3.5f : 2.2f, mate ? new Color(0.75f, 1f, 0.7f) : Team[b.Team]);
            }
        if (W.CarryOnAs is { } next) pane.DrawArc(P(next.FeetPos), 8f, 0f, Mathf.Tau, 20, Colors.White, 2f);
        if (W.PlayerBody is { Alive: true } p)
        {
            var at = P(p.FeetPos);
            float h = Mathf.DegToRad(p.Heading);
            var fwd = new Vector2(MathF.Sin(h), -MathF.Cos(h));
            var right = new Vector2(-fwd.Y, fwd.X);
            pane.DrawColoredPolygon(new[] { at + fwd * 10f, at - fwd * 6f + right * 6f, at - fwd * 6f - right * 6f }, Colors.White);
        }
        else if (Omni && W.Spec.Active) pane.DrawArc(P(W.Spec.GlobalPosition), 6f, 0f, Mathf.Tau, 16, Colors.White, 2f);
        pane.DrawString(font, new Vector2(10, _px - 12), $"[M] close · [Tab] the island · wheel: zoom ({_zoom:0.#}x) · the window, {world / 1000f:0.#} km across · grid 500 m", HorizontalAlignment.Left, -1, 13, new Color(1, 1, 1, 0.7f));
    }

    void DrawIsland()
    {
        var war = W.War;
        var isl = war.Isl;
        float ext = isl.Extent;
        // Island coordinates (m east and south of its centre) to the screen.
        Vector2 P(float x, float z) => _o + new Vector2((x / ext + 0.5f) * _px, (z / ext + 0.5f) * _px);
        var font = ThemeDB.FallbackFont;

        if (_islTex == null && _islImage is { IsCompleted: true } done) _islTex = ImageTexture.CreateFromImage(done.Result);
        if (_islTex != null) DrawTextureRect(_islTex, new Rect2(_o, new Vector2(_px, _px)), false, new Color(1f, 1f, 1f, 0.95f));
        else DrawString(font, _o + new Vector2(20, 40), "Drawing the island...", HorizontalAlignment.Left, -1, 16, Colors.White);

        // Who holds the ground: each side's squares tinted (a picture a pixel a square, made again when the war moves on),
        // contested ones ringed.
        var ctl = war.Ctl;
        if (_ctlTex == null || _ctlAt != war.Time) HoldingsPicture(war);
        DrawTextureRect(_ctlTex!, new Rect2(P(-ext / 2f, -ext / 2f), new Vector2(ctl.N * Territory.CellM / ext * _px, ctl.N * Territory.CellM / ext * _px)), false);
        float cell = Territory.CellM / ext * _px;
        for (int c = 0; c < ctl.N * ctl.N; c++)
            if (ctl.Land[c] && ctl.Contested[c])
                DrawRect(new Rect2(P((c % ctl.N) * Territory.CellM - ext / 2f, (c / ctl.N) * Territory.CellM - ext / 2f), new Vector2(cell, cell)), new Color(1f, 0.2f, 0.15f, 0.7f), false, 1.2f);
        // Your side's battalions (every side's, watching).
        foreach (var u in war.Units)
        {
            if (u.Echelon != Echelon.Battalion || u.People <= 0 || !Shows(u.Side)) continue;
            var q = P(u.X, u.Z);
            DrawRect(new Rect2(q - new Vector2(3, 3), new Vector2(6, 6)), Team[u.Side]);
        }
        // The window, and you in it.
        float half = W.Map.Size / 2f;
        var a = P(W.CX - half, W.CZ - half);
        var wsz = P(W.CX + half, W.CZ + half) - a;
        DrawRect(new Rect2(a, wsz), Colors.White, false, 2f);
        var me = W.PlayerBody is { Alive: true } p ? p.FeetPos : W.Watched?.FeetPos;
        if (me is Vector3 m) DrawCircle(P(m.X + W.CX, m.Z + W.CZ), 4f, Colors.White);
        for (int t = 0; t < 3; t++)
            DrawString(font, _o + new Vector2(10, 22 + 18 * t), $"{war.Sides[t].Name} holds {_held[t]:0}%", HorizontalAlignment.Left, -1, 14, Team[t]);
        DrawString(font, _o + new Vector2(10, _px - 12), $"[M] close · [Tab] the window · day {war.Day} · squares 1 km, ringed red where contested · white: the window", HorizontalAlignment.Left, -1, 13, new Color(1, 1, 1, 0.75f));
    }
}
