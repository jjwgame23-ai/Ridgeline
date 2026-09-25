using Godot;

namespace Ridgeline;

public partial class DuelHud : CanvasLayer
{
    public DuelMode Mode = null!;

    Label _score = null!, _center = null!, _spec = null!, _comms = null!;
    ColorRect _dot = null!;
    RichTextLabel _feed = null!;
    readonly List<(string text, double at)> _kills = new(), _lines = new();
    double _centerUntil;

    static readonly string[] TeamHex = { "#7fa8ff", "#ff8a70" };

    public override void _Ready()
    {
        _score = MakeLabel(18);
        _score.HorizontalAlignment = HorizontalAlignment.Center;
        _score.AnchorLeft = 0.3f; _score.AnchorRight = 0.7f;
        _score.OffsetTop = 62;

        _center = MakeLabel(28);
        _center.HorizontalAlignment = HorizontalAlignment.Center;
        _center.AnchorLeft = 0.15f; _center.AnchorRight = 0.85f;
        _center.AnchorTop = 0.3f; _center.AnchorBottom = 0.3f;

        _feed = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _feed.AddThemeFontSizeOverride("normal_font_size", 15);
        _feed.AddThemeConstantOverride("outline_size", 4);
        _feed.AddThemeColorOverride("font_outline_color", Colors.Black);
        _feed.AnchorLeft = 1f; _feed.AnchorRight = 1f;
        _feed.OffsetLeft = -460; _feed.OffsetRight = -14; _feed.OffsetTop = 14;
        AddChild(_feed);

        _comms = MakeLabel(15);
        _comms.AnchorTop = 1f; _comms.AnchorBottom = 1f;
        _comms.OffsetLeft = 16; _comms.OffsetTop = -260;

        // Eyes view: where the bot's gun is pointing (the camera looks straight down it).
        _dot = new ColorRect { Color = new Color(1f, 0.2f, 0.1f, 0.9f), Size = new Vector2(4, 4), MouseFilter = Control.MouseFilterEnum.Ignore };
        _dot.AnchorLeft = _dot.AnchorRight = _dot.AnchorTop = _dot.AnchorBottom = 0.5f;
        _dot.OffsetLeft = -2; _dot.OffsetTop = -2; _dot.OffsetRight = 2; _dot.OffsetBottom = 2;
        AddChild(_dot);

        _spec = MakeLabel(15);
        _spec.AnchorTop = 1f; _spec.AnchorBottom = 1f;
        _spec.OffsetLeft = 16; _spec.OffsetTop = -110;
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

    public void Center(string text, float seconds)
    {
        _center.Text = text;
        _centerUntil = Clock.Now + seconds;
    }

    static string Tag(ICombatant? c) => c == null ? "?" : $"[color={TeamHex[c.Team]}]{c.Callsign}[/color]";

    public void AddKill(ICombatant? killer, ICombatant victim, string detail)
    {
        _kills.Add(($"{Tag(killer)}  ▸  {Tag(victim)}  [color=#bbbbbb]({detail})[/color]", Clock.Now));
        if (_kills.Count > 6) _kills.RemoveAt(0);
    }

    public void AddComm(string text)
    {
        _lines.Add((text, Clock.Now));
        if (_lines.Count > 5) _lines.RemoveAt(0);
    }

    public override void _Process(double delta)
    {
        double now = Clock.Now;
        var m = Mode;
        double t = Math.Max(0, now - m.RoundStart);
        _score.Text = $"{DuelMode.TeamNames[0]} {m.Score[0]}  —  {m.Score[1]} {DuelMode.TeamNames[1]}\n" +
                      $"Round {m.Round} · {(int)t / 60}:{(int)t % 60:00} · alive {m.AliveOn(0)} v {m.AliveOn(1)}";

        _center.Visible = now < _centerUntil;
        _kills.RemoveAll(k => now - k.at > 10);
        _feed.Text = string.Join("\n", _kills.Select(k => k.text));
        _lines.RemoveAll(l => now - l.at > 8);
        _comms.Text = string.Join("\n", _lines.Select(l => l.text));

        var s = m.Spec;
        _spec.Visible = s.Active;
        _dot.Visible = s.Active && s.ViewMode == Spectator.View.Eyes && s.Target is { Alive: true };
        if (!s.Active) return;
        var b = s.Target;
        string who = b == null ? "nobody" :
            $"{b.Callsign} ({DuelMode.TeamNames[b.Team]}) · {(b.Alive ? $"{b.Health:0} hp" : "dead")} · {b.Def.Name} {b.Ammo}/{b.Def.MagSize}\n" +
            $"{b.Brain.State}{(b.Brain.Target != null ? $" → {b.Brain.Target.Who.Callsign}" : "")} · {b.Brain.Note}\n" +
            $"skill {b.P.Skill:0.00} · aggression {b.P.Aggression:0.00} · courage {b.P.Courage:0.00} · reaction {b.P.ReactionS * 1000:0} ms";
        _spec.Text = $"SPECTATING [{s.ViewMode}]  {who}\n[LMB/RMB] switch bot · [C] chase/eyes · [F] free cam · [F6] bot debug";
    }
}
