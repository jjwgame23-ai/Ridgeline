using Godot;

namespace Ridgeline;

public partial class KothHud : CanvasLayer
{
    public KothMode Mode = null!;

    static readonly string[] TeamHex = { "#7fa8ff", "#ff8a70", "#ffd35a" };

    RichTextLabel _score = null!, _feed = null!;
    Label _center = null!, _spec = null!, _comms = null!, _nav = null!;
    MapView _map = null!;
    readonly List<(string text, double at)> _kills = new(), _lines = new();
    double _centerUntil;

    public override void _Ready()
    {
        _score = Rich(17);
        _score.AnchorLeft = 0.25f; _score.AnchorRight = 0.75f;
        _score.OffsetTop = 60;
        _score.HorizontalAlignment = HorizontalAlignment.Center;

        _nav = MakeLabel(15);
        _nav.HorizontalAlignment = HorizontalAlignment.Center;
        _nav.AnchorLeft = 0.25f; _nav.AnchorRight = 0.75f;
        _nav.OffsetTop = 112;

        _center = MakeLabel(28);
        _center.HorizontalAlignment = HorizontalAlignment.Center;
        _center.AnchorLeft = 0.15f; _center.AnchorRight = 0.85f;
        _center.AnchorTop = 0.3f; _center.AnchorBottom = 0.3f;

        _feed = Rich(15);
        _feed.AnchorLeft = 1f; _feed.AnchorRight = 1f;
        _feed.OffsetLeft = -460; _feed.OffsetRight = -14; _feed.OffsetTop = 14;

        _comms = MakeLabel(15);
        _comms.AnchorTop = 1f; _comms.AnchorBottom = 1f;
        _comms.OffsetLeft = 16; _comms.OffsetTop = -260;

        _spec = MakeLabel(15);
        _spec.AnchorTop = 1f; _spec.AnchorBottom = 1f;
        _spec.OffsetLeft = 16; _spec.OffsetTop = -110;

        _map = new MapView { Mode = Mode, Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
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

    RichTextLabel Rich(int size)
    {
        var r = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        r.AddThemeFontSizeOverride("normal_font_size", size);
        r.AddThemeConstantOverride("outline_size", 5);
        r.AddThemeColorOverride("font_outline_color", Colors.Black);
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

    public void AddKill(ICombatant? killer, ICombatant victim, string detail)
    {
        _kills.Add(($"{Tag(killer)}  ▸  {Tag(victim)}  [color=#bbbbbb]({detail})[/color]", Clock.Now));
        if (_kills.Count > 7) _kills.RemoveAt(0);
    }

    public void AddComm(string text)
    {
        _lines.Add((text, Clock.Now));
        if (_lines.Count > 5) _lines.RemoveAt(0);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, PhysicalKeycode: Key.M }) _map.Visible = !_map.Visible;
    }

    public override void _Process(double delta)
    {
        double now = Clock.Now;
        var m = Mode;
        _score.Text = $"{TeamTag(0)} {m.Score[0]}   {TeamTag(1)} {m.Score[1]}   {TeamTag(2)} {m.Score[2]}   [color=#bbbbbb]/ {m.TargetScore}[/color]";

        string holder = m.Holder >= 0 ? $"held by {KothMode.TeamNames[m.Holder]}" : m.InZone.Sum() > 0 ? "CONTESTED" : "empty";
        double left = Math.Max(0, m.ZoneMovesAt - now);
        string where = "";
        var me = m.PlayerBody is { Alive: true } p ? p.FeetPos : m.Spec.Target?.FeetPos;
        if (me is Vector3 pos)
        {
            var d = m.Zone.Center - pos;
            d.Y = 0f;
            where = $" · {d.Length():0} m {Comms.Bearing(pos, m.Zone.Center)}";
        }
        _nav.Text = $"ZONE {m.Zone.Name}{where} · {holder} ({m.InZone[0]}/{m.InZone[1]}/{m.InZone[2]}) · moves in {(int)left / 60}:{(int)left % 60:00}";
        HudOverlay.Marker = m.Zone.Center;

        _center.Visible = now < _centerUntil;
        _kills.RemoveAll(k => now - k.at > 10);
        _feed.Text = string.Join("\n", _kills.Select(k => k.text));
        _lines.RemoveAll(l => now - l.at > 8);
        _comms.Text = string.Join("\n", _lines.Select(l => l.text));

        var s = m.Spec;
        _spec.Visible = s.Active;
        if (!s.Active) return;
        string respawn = m.PlayerRespawnAt > 0 ? $"Respawning at the rally point in {Math.Max(0, m.PlayerRespawnAt - now):0}s\n" : "";
        var b = s.Target;
        string who = b == null ? "nobody" :
            $"{b.Callsign} ({KothMode.TeamNames[b.Team]}) · {(b.Alive ? $"{b.Health:0} hp" : "dead")} · {b.Brain.State}{(b.Brain.Target != null ? $" → {b.Brain.Target.Who.Callsign}" : "")} · {b.Brain.Note}";
        _spec.Text = $"{respawn}SPECTATING [{s.ViewMode}]  {who}\n[LMB/RMB] switch · [C] chase/eyes · [F] free cam · [M] map · [F6] bot debug";
    }
}
