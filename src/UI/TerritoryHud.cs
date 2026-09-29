using Godot;

namespace Ridgeline;

public partial class TerritoryHud : CanvasLayer
{
    public TerritoryMode Mode = null!;

    public static readonly string[] TeamHex = { "#7fa8ff", "#ff8a70", "#ffd35a" };

    RichTextLabel _score = null!, _points = null!, _feed = null!, _comms = null!, _roster = null!, _brief = null!, _menu = null!;
    /// <summary>The squad leader's command menu is open (the number keys pick a command, not a weapon).</summary>
    public static bool MenuOpen;
    /// <summary>The frame a number key last picked a squad command (so the same press isn't also taken as a seat).</summary>
    public static ulong MenuUsedFrame;

    public static readonly string[] Commands =
    {
        "Move here (where you're aiming)", "Hold here", "On me / work the objective", "Suppress where I'm aiming",
        "Smoke where I'm aiming", "March order: next", "Vehicle: fire where I'm aiming", "Vehicle: pick us up / dismount",
    };
    Label _center = null!, _spec = null!, _nav = null!;
    TerritoryMap _map = null!;
    readonly List<(string text, double at)> _kills = new(), _lines = new();
    double _centerUntil;

    public override void _Ready()
    {
        Layer = 5; // above the general HUD (help panel), so the map covers it
        _score = Rich(18, 0.2f, 0.8f, 58);
        _points = Rich(14, 0.1f, 0.9f, 86);

        _nav = MakeLabel(15);
        _nav.HorizontalAlignment = HorizontalAlignment.Center;
        _nav.AnchorLeft = 0.2f; _nav.AnchorRight = 0.8f;
        _nav.OffsetTop = 112;

        _center = MakeLabel(28);
        _center.HorizontalAlignment = HorizontalAlignment.Center;
        _center.AnchorLeft = 0.15f; _center.AnchorRight = 0.85f;
        _center.AnchorTop = 0.3f; _center.AnchorBottom = 0.3f;

        _feed = Rich(15, 1f, 1f, 14);
        _feed.OffsetLeft = -460; _feed.OffsetRight = -14;
        _feed.HorizontalAlignment = HorizontalAlignment.Left;

        _comms = Rich(15, 0f, 0f, 0);
        _comms.AnchorTop = 1f; _comms.AnchorBottom = 1f;
        _comms.OffsetLeft = 16; _comms.OffsetRight = 700; _comms.OffsetTop = -270;
        _comms.HorizontalAlignment = HorizontalAlignment.Left;

        _spec = MakeLabel(15);
        _spec.AnchorTop = 1f; _spec.AnchorBottom = 1f;
        _spec.OffsetLeft = 16; _spec.OffsetTop = -120;

        _brief = Rich(15, 0.2f, 0.8f, 150);
        _menu = Rich(15, 0f, 0f, 0);
        _menu.AnchorTop = 0.35f; _menu.AnchorBottom = 0.35f;
        _menu.OffsetLeft = 16; _menu.OffsetRight = 460;
        _menu.HorizontalAlignment = HorizontalAlignment.Left;
        _menu.Visible = false;

        _roster = Rich(14, 1f, 1f, 250);
        _roster.OffsetLeft = -290; _roster.OffsetRight = -14;
        _roster.HorizontalAlignment = HorizontalAlignment.Left;

        _map = new TerritoryMap { Mode = this.Mode, Visible = false };
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

    public static string Tag(ICombatant? c) => c == null ? "?" : $"[color={TeamHex[c.Team]}]{c.Callsign}[/color]";
    public static string TeamTag(int t) => $"[color={TeamHex[t]}]{KothMode.TeamNames[t]}[/color]";

    public void Center(string text, float seconds)
    {
        _center.Text = text;
        _centerUntil = Clock.Now + seconds;
    }

    /// <summary>A point changed hands. mood: -1 bad for us, 1 good, 0 someone else's business.</summary>
    public void Event(string text, int mood)
    {
        string col = mood < 0 ? "#ff9a8a" : mood > 0 ? "#a8ffa0" : "#dddddd";
        _kills.Add(($"[color={col}]{text}[/color]", Clock.Now));
        if (_kills.Count > 8) _kills.RemoveAt(0);
        if (mood != 0) Center(text, 3f);
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
            return;
        }
        if (e.IsActionPressed("squad_follow")) { Mode.ToggleFollow(); return; }
        if (e.IsActionPressed("squad_menu") && Mode.PlayerBody is { Alive: true })
        {
            if (!Mode.PlayerLeads) { Center("Only the squad leader gives orders (pick the leader role)", 2f); return; }
            MenuOpen = !MenuOpen;
            return;
        }
        // The number keys in a menu, and Esc, are fixed.
        if (e is not InputEventKey { Pressed: true, Echo: false } k) return;
        if (MenuOpen && k.PhysicalKeycode >= Key.Key1 && k.PhysicalKeycode < Key.Key1 + Commands.Length)
        {
            MenuOpen = false;
            MenuUsedFrame = Engine.GetProcessFrames();
            Mode.SquadCommand((int)(k.PhysicalKeycode - Key.Key1));
            GetViewport().SetInputAsHandled();
        }
        else if (MenuOpen && k.PhysicalKeycode == Key.Escape) MenuOpen = false;
        else if (Mode.PlayerRespawnAt > 0 && RoleKey(k.PhysicalKeycode) is int ri)
        {
            // Dead: pick what to respawn as.
            Settings.PlayerRole = TerritoryMode.PlayerRoles[ri];
            Settings.Save();
            Center($"Respawning as {Roles.Name(Settings.PlayerRole)}", 2f);
        }
    }

    /// <summary>
    /// While dead: 1-9 pick the first nine roles, and 0 steps through the rest. (There are more roles than number
    /// keys: 10-13 were read as the keycodes after 9, ':' ';' '<' '=', so Heavy AT couldn't be picked at all.)
    /// </summary>
    static int? RoleKey(Key key)
    {
        var roles = TerritoryMode.PlayerRoles;
        if (key >= Key.Key1 && key <= Key.Key9 && key - Key.Key1 < roles.Length) return (int)(key - Key.Key1);
        if (key != Key.Key0 || roles.Length <= 9) return null;
        int cur = Array.IndexOf(roles, Settings.PlayerRole);
        return cur >= 9 && cur + 1 < roles.Length ? cur + 1 : 9;
    }

    static string RoleKeyName(int i) => i < 9 ? $"{i + 1}" : "0";

    string PointsLine()
    {
        var m = Mode;
        var parts = new List<string>();
        for (int i = 0; i < m.Points.Length; i++)
        {
            int o = m.Owner[i];
            string col = o >= 0 ? TeamHex[o] : "#cfcfcf";
            string name = m.Points[i].Site.Name;
            // Being taken: show whose and how far.
            bool contested = Enumerable.Range(0, 3).Count(t => m.Inside[i, t] > 0) > 1;
            string state = o >= 0 && m.Progress[i] < 0.999f ? $" {m.Progress[i] * 100:0}%"
                : o < 0 && m.Capper[i] >= 0 && m.Progress[i] > 0f ? $" [color={TeamHex[m.Capper[i]]}]{m.Progress[i] * 100:0}%[/color]" : "";
            parts.Add($"[color={col}]{(o >= 0 ? "■" : "□")} {name}[/color]{state}{(contested ? " [color=#ff5040]⚔[/color]" : "")}");
        }
        return string.Join("   ", parts);
    }

    /// <summary>Your squad at a glance: who's who, and who's hurt or out of ammo.</summary>
    string RosterText()
    {
        var sq = Mode.PlayerSquad;
        if (sq == null) return "";
        var lines = new List<string> { $"[b]{sq.Name}[/b] · {sq.KindName}" };
        foreach (var c in sq.Members)
        {
            if (!GodotObject.IsInstanceValid((GodotObject)c)) continue;
            string role = Roles.Short(c.Role);
            string name = c is Player ? "You" : c.Callsign;
            if (c.Dead) { lines.Add($"[color=#777777]{role,-4} {name} — KIA[/color]"); continue; }
            if (c.Downed) { lines.Add($"[color=#ff7060]{role,-4} {name} — DOWN, needs a medic[/color]"); continue; }
            int bars = (int)MathF.Ceiling(c.Hp / 20f);
            string col = c.Hp > 66f ? "#9be38f" : c.Hp > 33f ? "#e8d06a" : "#ff7060";
            string ammo = c.AmmoLevel < 0.34f ? " [color=#ffb060]low ammo[/color]" : "";
            string lead = c == sq.Leader ? " ★" : "";
            lines.Add($"{role,-4} {name}{lead}  [color={col}]{new string('█', bars)}{new string('░', 5 - bars)}[/color]{ammo}");
        }
        return string.Join("\n", lines);
    }

    public override void _Process(double delta)
    {
        double now = Clock.Now;
        var m = Mode;
        _roster.Text = RosterText();
        _score.Text = string.Join("     ", Enumerable.Range(0, 3).Select(t =>
            $"{TeamTag(t)} {m.Tickets[t]}{(m.Out[t] ? " [color=#888888](out)[/color]" : m.Spent(t) ? " [color=#ff5040](last stand)[/color]" : "")} [color=#aaaaaa]· {m.Owned(t)} pts[/color]"
            + (!m.Out[t] && m.HQHold[t] < 0.999f ? $" [color=#ff5040]HQ {m.HQHold[t] * 100:0}%[/color]" : "")));
        _points.Text = PointsLine();

        var sq = m.PlayerSquad;
        var me = m.PlayerBody is { Alive: true } p ? p.FeetPos : m.Spec.Target?.FeetPos;
        if (sq?.Objective != null)
        {
            var goal = sq.Objective.Center;
            string where = me is Vector3 pos ? $" · {(goal - pos with { Y = goal.Y }).Length():0} m {Comms.Bearing(pos, goal)}" : "";
            _nav.Text = $"{sq.Name} · {sq.OrderText}{where}{(sq.FollowPlayer && m.PlayerLeads ? " · squad on you" : "")}{(sq.PlayerOrderUntil > now ? " · your order" : "")}";
            HudOverlay.Marker = goal;
            // A ride for the squad: point the player at it until they're aboard.
            var ride = sq.Transport;
            if (ride is { Destroyed: false } && m.PlayerBody is { Alive: true } pb && pb.Ride != ride && me is Vector3 at)
            {
                float d = ride.GlobalPosition.DistanceTo(at);
                _nav.Text += ride.Boarding
                    ? $"\n▶ MOUNT UP: your squad's {ride.Def.Name} is waiting, {d:0} m {Comms.Bearing(at, ride.GlobalPosition)} — [{Controls.Keys("use")}] to get in"
                    : $"\n▶ A {ride.Def.Name} is coming to pick your squad up ({d:0} m {Comms.Bearing(at, ride.GlobalPosition)})";
                HudOverlay.Marker = ride.GlobalPosition;
            }
        }
        else
        {
            _nav.Text = "";
            HudOverlay.Marker = null;
        }

        // The squad briefing: what the squad is doing, and your part in it.
        HudOverlay.Spot = HudOverlay.Sector = null;
        _brief.Text = "";
        if (sq != null && m.PlayerBody is { Alive: true } pp && sq.Members.Contains(pp))
        {
            var br = sq.Brief(pp);
            var lines = new List<string> { $"[color=#cfe8ff]{br.Doing}[/color]" + (br.Team != "" ? $"  [color=#9aa]· {br.Team}[/color]" : "") };
            lines.Add($"[color=#b8ffb0]▶ {br.Task}[/color]");
            foreach (var sv in sq.Support)
                lines.Add($"[color=#d8c890]{sv.Def.Name}: {(sv.Task != "" ? sv.Task : "with you")}{(sv.FireAt != null && Clock.Now < sv.FireAtUntil ? $" — {sv.FireAtWhy}" : "")}[/color]");
            if (m.PlayerLeads) lines.Add($"[color=#888]march: {(sq.PlayerMarch?.ToString() ?? "auto")} · {Controls.Keys("squad_menu")}: squad commands · {Controls.Keys("squad_follow")}: on me · {Controls.Keys("map")}: map[/color]");
            _brief.Text = string.Join("\n", lines);
            HudOverlay.Spot = br.Spot;
            HudOverlay.Sector = br.Sector;
        }
        if (!(m.PlayerBody is { Alive: true } && m.PlayerLeads)) MenuOpen = false;
        _menu.Visible = MenuOpen;
        if (MenuOpen) _menu.Text = "[b]SQUAD COMMANDS[/b]\n" + string.Join("\n", Commands.Select((c, i) => $"{i + 1}  {c}")) + $"\n[color=#888]{Controls.Keys("squad_menu")} or Esc: close[/color]";

        _center.Visible = now < _centerUntil;
        _kills.RemoveAll(k => now - k.at > 10);
        _feed.Text = string.Join("\n", _kills.Select(k => k.text));
        _lines.RemoveAll(l => now - l.at > 8);
        _comms.Text = string.Join("\n", _lines.Select(l => l.text));

        var s = m.Spec;
        _spec.Visible = s.Active;
        if (!s.Active) return;
        string respawn = "";
        if (m.PlayerRespawnAt > 0)
        {
            var opts = m.SpawnOptions(0);
            string chosen = m.PlayerSpawn < 0 ? "with your squad"
                : m.PlayerSpawn == 0 ? "base (vehicles are parked there)"
                : opts.FirstOrDefault(o => o.Point == m.PlayerSpawn - 1).Name ?? "nearest (your pick was lost)";
            string roles = string.Join("  ", TerritoryMode.PlayerRoles.Select((r, i) => r == Settings.PlayerRole ? $"[{RoleKeyName(i)}] {Roles.Short(r)}◂" : $"[{RoleKeyName(i)}] {Roles.Short(r)}"));
            respawn = (m.PlayerWait != "" && m.PlayerSpawn < 0
                          ? $"Waiting to rejoin your squad as {Roles.Name(Settings.PlayerRole)}: {m.PlayerWait}. Map (M): click a spawn to go on your own\n"
                          : $"Respawning in {Math.Max(0, m.PlayerRespawnAt - now):0}s at {chosen} as {Roles.Name(Settings.PlayerRole)} — map (M): click a spawn\n") +
                      $"Role: {roles}\n";
        }
        var b = s.Target;
        string who = b == null ? "nobody" :
            $"{b.Callsign} ({Roles.Name(b.Role)}, {b.Squad?.Name ?? KothMode.TeamNames[b.Team]} {b.Squad?.KindName.ToLowerInvariant()}) · {(b.Alive ? $"{b.Health:0} hp" : "dead")} · {b.Brain.State}{(b.Brain.Target != null ? $" → {b.Brain.Target.Who.Callsign}" : "")} · {b.Brain.Note}";
        _spec.Text = $"{respawn}SPECTATING [{s.ViewMode}]  {who}\n" + Controls.Fill("[{spectate_next}/{spectate_prev}] switch · [{spectate_view}] chase/eyes · [{spectate_free}] free cam · [{map}] map · [{bot_debug}] bot debug");
    }
}

/// <summary>
/// M: the whole battlefield. Points coloured by owner with capture progress, bases,
/// your side's soldiers and where each of your squads has been sent. Click a point
/// to send your squad there (or, while dead, pick where to respawn); B makes your
/// squad follow you.
/// </summary>
public partial class TerritoryMap : Control
{
    public TerritoryMode Mode = null!;
    static readonly Color[] Team = { new(0.5f, 0.66f, 1f), new(1f, 0.55f, 0.44f), new(1f, 0.83f, 0.35f) };
    float World => Mode.Map.Size;
    float _px;
    Vector2 _o;

    public override void _Ready() => MouseFilter = MouseFilterEnum.Stop;

    public override void _Process(double delta)
    {
        if (Visible) QueueRedraw();
    }

    /// <summary>Spectating with no player in the match: every side is shown, not just yours.</summary>
    bool Omni => !Mode.Setup.PlayerJoins;
    /// <summary>Whether to show this side's things (troops, vehicles, orders, intel).</summary>
    bool Shows(int team) => team == 0 || Omni;

    Vector2 P(Vector3 w) => _o + new Vector2((w.X / World + 0.5f) * _px, (w.Z / World + 0.5f) * _px);

    static string VehLetter(VKind k) => k switch
    {
        VKind.LTV => "LTV", VKind.Transport => "TRAN", VKind.Logistics => "LOGI", VKind.APC => "APC",
        VKind.IFV => "IFV", VKind.MBT => "MBT", VKind.MGS => "MGS", VKind.UH => "UH", VKind.AH => "AH", VKind.SPAA => "SPAA", VKind.Mortar => "MORT", _ => "?",
    };

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb) return;
        AcceptEvent();
        var m = Mode;
        // Spectating (nobody to play): a click flies the camera there.
        if (Omni)
        {
            var w = new Vector3((mb.Position.X - _o.X) / _px - 0.5f, 0f, (mb.Position.Y - _o.Y) / _px - 0.5f) * World;
            m.Spec.FlyTo(m.Map.Ground(w));
            Visible = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
            return;
        }
        bool dead = m.PlayerBody is not { Alive: true };
        // Nearest point (or base) to the click.
        int best = -2;
        float bestD = 28f;
        for (int i = 0; i < m.Points.Length; i++)
        {
            float d = P(m.Points[i].Center).DistanceTo(mb.Position);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (dead && P(m.Map.Bases[0]).DistanceTo(mb.Position) < bestD) best = -1;
        // FOBs (spawn indices from 100).
        if (dead)
            foreach (var o in m.SpawnOptions(0))
                if (o.Point >= 100 && P(o.Pos).DistanceTo(mb.Position) < bestD) { bestD = P(o.Pos).DistanceTo(mb.Position); best = o.Point; }
        if (best == -2) return;
        if (dead && best >= 100) { m.PlayerSpawn = best + 1; return; }
        if (dead)
        {
            if (best == -1) m.PlayerSpawn = 0;
            else if (m.SpawnOptions(0).Any(o => o.Point == best)) m.PlayerSpawn = best + 1;
        }
        else if (best >= 0) m.PlayerOrder(best);
    }

    public override void _Draw()
    {
        var m = Mode;
        var size = GetViewportRect().Size;
        _px = size.Y * 0.86f;
        _o = (size - new Vector2(_px, _px)) / 2f;
        float scale = _px / World;
        var font = ThemeDB.FallbackFont;

        DrawTextureRect(MapImage.For(m.Map), new Rect2(_o, new Vector2(_px, _px)), false, new Color(0.92f, 0.92f, 0.92f, 0.97f));
        for (int i = 1; i < 10; i++)
        {
            float f = i / 10f * _px;
            DrawLine(_o + new Vector2(f, 0), _o + new Vector2(f, _px), new Color(1, 1, 1, 0.06f));
            DrawLine(_o + new Vector2(0, f), _o + new Vector2(_px, f), new Color(1, 1, 1, 0.06f));
        }

        bool dead = m.PlayerBody is not { Alive: true };
        var spawns = m.SpawnOptions(0);

        // The front line: the links between points (and bases), coloured where one side holds both ends.
        if (m.Front)
        {
            int n = m.Points.Length;
            Vector3 NodePos(int j) => j < n ? m.Points[j].Center : m.Map.Bases[j - n];
            int NodeOwner(int j) => j < n ? m.Owner[j] : j - n;
            for (int a = 0; a < m.Links.Length; a++)
                foreach (int b in m.Links[a])
                {
                    if (b < a) continue;
                    int oa = NodeOwner(a), ob = NodeOwner(b);
                    var col = oa >= 0 && oa == ob ? Team[oa] with { A = 0.55f } : new Color(1f, 1f, 1f, 0.22f);
                    DrawLine(P(NodePos(a)), P(NodePos(b)), col, oa >= 0 && oa == ob ? 3f : 1.5f);
                }
        }
        for (int i = 0; i < m.Points.Length; i++)
        {
            var pt = m.Points[i];
            var c = P(pt.Center);
            int o = m.Owner[i];
            var col = o >= 0 ? Team[o] : new Color(0.85f, 0.85f, 0.8f);
            DrawCircle(c, pt.Radius * scale, col with { A = 0.18f });
            DrawArc(c, pt.Radius * scale, 0f, Mathf.Tau, 40, col with { A = 0.5f }, 1.5f);
            // Progress ring: the owner's hold, or a neutral point's capture so far.
            int who = o >= 0 ? o : m.Capper[i];
            if (who >= 0 && m.Progress[i] > 0f)
                DrawArc(c, pt.Radius * scale + 4f, -Mathf.Pi / 2f, -Mathf.Pi / 2f + Mathf.Tau * m.Progress[i], 40, Team[who], 3f);
            DrawRect(new Rect2(c - new Vector2(5, 5), new Vector2(10, 10)), col);
            string label = pt.Site.Name + (m.Inside[i, 0] + m.Inside[i, 1] + m.Inside[i, 2] > 0 && Enumerable.Range(0, 3).Count(t => m.Inside[i, t] > 0) > 1 ? "  ⚔" : "");
            // What your side can do here: take it, hold it on the front, or nothing yet (behind their lines).
            if (m.Front && !Omni)
            {
                string tag = m.CanTake(0, i) ? "ATTACK" : m.OnFront(0, i) ? "DEFEND" : o != 0 ? "locked" : "";
                if (tag != "")
                    DrawString(font, c + new Vector2(10, 24), tag, HorizontalAlignment.Left, -1, 11,
                               tag == "ATTACK" ? new Color(1f, 0.85f, 0.4f) : tag == "DEFEND" ? Team[0] : new Color(1, 1, 1, 0.4f));
            }
            DrawString(font, c + new Vector2(10, -8), label, HorizontalAlignment.Left, -1, 14, new Color(0.95f, 0.95f, 0.9f));
            if (dead && spawns.Any(s => s.Point == i))
            {
                bool chosen = m.PlayerSpawn == i + 1;
                DrawString(font, c + new Vector2(10, 10), chosen ? "▶ SPAWN" : "spawn", HorizontalAlignment.Left, -1, 13, chosen ? Colors.White : Team[0]);
            }
        }
        for (int t = 0; t < 3; t++)
        {
            var b = P(m.Map.Bases[t]);
            DrawRect(new Rect2(b - new Vector2(8, 8), new Vector2(16, 16)), m.Out[t] ? Team[t] with { A = 0.35f } : Team[t]);
            DrawString(font, b + new Vector2(12, 5), KothMode.TeamNames[t] + (m.Out[t] ? " HQ (overrun)" : m.HQHold[t] < 0.999f ? $" HQ {m.HQHold[t] * 100:0}%" : " HQ"), HorizontalAlignment.Left, -1, 13, Team[t]);
            // Its hold, when it's being taken.
            if (!m.Out[t] && m.HQHold[t] < 0.999f)
                DrawArc(b, m.HQ[t].Radius * scale + 4f, -Mathf.Pi / 2f, -Mathf.Pi / 2f + Mathf.Tau * m.HQHold[t], 40, Colors.Red, 3f);
        }
        if (dead) DrawString(font, P(m.Map.Bases[0]) + new Vector2(12, 22), m.PlayerSpawn == 0 ? "▶ SPAWN" : "spawn", HorizontalAlignment.Left, -1, 13, m.PlayerSpawn == 0 ? Colors.White : Team[0]);

        // Your side: squads' orders as lines from the leader to the objective, and every soldier.
        foreach (var sq in m.Squads.SelectMany(l => l))
        {
            if (!Shows(sq.Team) || sq.Position is not Vector3 at || sq.Objective == null) continue;
            bool mine = sq == m.PlayerSquad;
            var col = mine ? Colors.White : Team[sq.Team] with { A = 0.6f };
            DrawDashedLine(P(at), P(sq.Objective.Center), col, mine ? 2f : 1.2f, 8f);
            if (sq.Objective is PointObjective po)
            {
                // An overwatch post and what it's watching.
                DrawArc(P(po.Center), 5f, 0f, Mathf.Tau, 12, col, 1.5f);
                DrawLine(P(po.Center), P(po.Watch), col with { A = 0.3f }, 1f);
            }
            string kind = sq.Kind switch { SquadKind.Weapons => " MG", SquadKind.Recon => " recon", SquadKind.Engineer => " eng", SquadKind.Logistics => " log", SquadKind.Drone => " UAV", _ => "" };
            DrawString(font, P(at) + new Vector2(6, -6), sq.Name + kind, HorizontalAlignment.Left, -1, 12, mine ? Colors.White : Team[sq.Team]);
        }
        // Your squad's plan: the ORP, support-by-fire position and line of departure of an attack;
        // a danger-area crossing; where the leader wants suppressing fire.
        if (m.PlayerSquad is { } ps)
        {
            var hi = new Color(0.55f, 1f, 0.55f);
            void Mark(Vector3 at, string label)
            {
                var q = P(at);
                DrawRect(new Rect2(q - new Vector2(4, 4), new Vector2(8, 8)), hi, false, 1.5f);
                DrawString(font, q + new Vector2(7, -5), label, HorizontalAlignment.Left, -1, 11, hi);
            }
            if (ps.Phase != AssaultPhase.None)
            {
                Mark(ps.OrpAt, "ORP");
                if (ps.Phase != AssaultPhase.Orp) { Mark(ps.SbfAt, $"SBF ({Squad.TeamName(ps.SbfTeam)})"); Mark(ps.LdAt, "LD"); }
            }
            if (ps.CrossingNow)
            {
                DrawLine(P(ps.CrossNear), P(ps.CrossFar), hi, 2.5f);
                Mark(ps.CrossNear, "danger area");
            }
            if (ps.SuppressUntil > Clock.Now) Mark(ps.SuppressAt, "suppress");
            if (ps.Current is Drill.Contact or Drill.BreakContact || ps.Engaged)
            {
                var q = P(ps.ContactAt);
                DrawLine(q + new Vector2(-6, -6), q + new Vector2(6, 6), new Color(1f, 0.35f, 0.3f), 2f);
                DrawLine(q + new Vector2(-6, 6), q + new Vector2(6, -6), new Color(1f, 0.35f, 0.3f), 2f);
            }
            foreach (var sv in ps.Support)
            {
                DrawLine(P(sv.GlobalPosition), P(ps.Position ?? sv.GlobalPosition), hi with { A = 0.35f }, 1f);
                if (sv.FireAt is Vector3 fa && Clock.Now < sv.FireAtUntil) DrawLine(P(sv.GlobalPosition), P(fa), new Color(1f, 0.8f, 0.3f, 0.7f), 1.5f);
            }
        }
        // Vehicles: ours as boxes with a class letter; theirs only as reported on the radio.
        foreach (var v in Vehicle.All)
        {
            if (v.Destroyed || !Shows(v.Team)) continue;
            var q = P(v.GlobalPosition);
            DrawRect(new Rect2(q - new Vector2(5, 4), new Vector2(10, 8)), Team[v.Team]);
            DrawString(font, q + new Vector2(7, 4), VehLetter(v.Def.Kind), HorizontalAlignment.Left, -1, 11, Team[v.Team]);
        }
        // Our drones in the air: a small cross, and what the quads' cameras cover.
        foreach (var d in Drone.All)
        {
            if (d.Dead || !Shows(d.Team) || !IsInstanceValid(d)) continue;
            var q = P(d.GlobalPosition);
            var dc = Team[d.Team];
            DrawLine(q + new Vector2(-4, -4), q + new Vector2(4, 4), dc, 1.5f);
            DrawLine(q + new Vector2(-4, 4), q + new Vector2(4, -4), dc, 1.5f);
            if (d.Kind == DroneKind.Quad) DrawArc(q, (P(d.GlobalPosition + new Vector3(130f, 0f, 0f)) - q).Length(), 0f, Mathf.Tau, 32, dc with { A = 0.25f }, 1f);
            else
            {
                DrawString(font, q + new Vector2(6, 4), d.Kind == DroneKind.FpvAt ? "AT FPV" : "FPV", HorizontalAlignment.Left, -1, 10, dc);
                DrawLine(q, P(d.TargetPoint()), new Color(1f, 0.35f, 0.3f, 0.5f), 1f);
            }
        }
        foreach (var r in Radio.Log)
        {
            if (r.Team != 0 || Omni || r.Kind is not (RadioKind.Armor or RadioKind.Air) || Clock.Now - r.At > 60.0 || r.Vehicle is { Destroyed: true }) continue;
            var q = P(r.Pos);
            var red = new Color(1f, 0.3f, 0.25f, 1f - (float)((Clock.Now - r.At) / 60.0) * 0.6f);
            DrawRect(new Rect2(q - new Vector2(6, 5), new Vector2(12, 10)), red, false, 2f);
            DrawString(font, q + new Vector2(8, 4), r.Vehicle != null ? VehLetter(r.Vehicle.Def.Kind) : "?", HorizontalAlignment.Left, -1, 12, red);
        }
        foreach (var f in Fob.All)
        {
            if (!Shows(f.Team)) continue;
            var q = P(f.GlobalPosition);
            DrawColoredPolygon(new[] { q + new Vector2(0, -7), q + new Vector2(7, 5), q + new Vector2(-7, 5) }, Team[0]);
            bool chosen = dead && m.PlayerSpawn >= 101 && m.SpawnOptions(0).Any(o => o.Point == m.PlayerSpawn - 1 && o.Pos == f.GlobalPosition);
            DrawString(font, q + new Vector2(9, 4), f.Label + (dead ? (chosen ? " ▶ SPAWN" : " spawn") : ""), HorizontalAlignment.Left, -1, 12, chosen ? Colors.White : Team[0]);
        }
        // What our recon has seen in the last half minute.
        double now = Clock.Now;
        foreach (var (team, pos, at) in m.Intel)
        {
            if (!Shows(team) || now - at > 30.0) continue;
            float fade = 1f - (float)((now - at) / 30.0);
            var q = P(pos);
            var red = new Color(1f, 0.25f, 0.2f, 0.4f + 0.6f * fade);
            DrawLine(q + new Vector2(-4, -4), q + new Vector2(4, 4), red, 2f);
            DrawLine(q + new Vector2(-4, 4), q + new Vector2(4, -4), red, 2f);
        }
        foreach (var b in m.Bots)
            if (IsInstanceValid(b) && b.Alive && Shows(b.Team))
                DrawCircle(P(b.FeetPos), b.Squad == m.PlayerSquad ? 3.5f : 2.5f, b.Squad == m.PlayerSquad ? new Color(0.75f, 1f, 0.7f) : Team[b.Team]);
        // Spectating: where the camera is.
        if (Omni && m.Spec.Active)
        {
            var sc = P(m.Spec.GlobalPosition);
            DrawArc(sc, 6f, 0f, Mathf.Tau, 16, Colors.White, 2f);
        }
        if (m.PlayerBody is { Alive: true } p)
        {
            var at = P(p.FeetPos);
            float h = Mathf.DegToRad(p.Heading);
            var fwd = new Vector2(MathF.Sin(h), -MathF.Cos(h));
            var right = new Vector2(-fwd.Y, fwd.X);
            DrawColoredPolygon(new[] { at + fwd * 10f, at - fwd * 6f + right * 6f, at - fwd * 6f - right * 6f }, Colors.White);
        }
        string help = Omni ? "[M] close · click anywhere to fly the camera there (free cam: WASD, Space/C, Shift) · every side shown"
            : dead
            ? "[M] close · click your base or an owned point to respawn there"
            : m.PlayerLeads ? "[M] close · click a point to send your squad there · [B] squad on you / work the objective"
            : "[M] close · your squad leader decides where the squad goes";
        DrawString(font, _o + new Vector2(10, _px - 12), help + $" · grid {World / 10f:0} m", HorizontalAlignment.Left, -1, 13, new Color(1, 1, 1, 0.65f));
    }
}
