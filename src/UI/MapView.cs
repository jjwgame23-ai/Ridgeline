using Godot;

namespace Ridgeline;

/// <summary>M: a top-down map of the battlefield — settlements, the zone, bases, rally points and your side.</summary>
public partial class MapView : Control
{
    public KothMode Mode = null!;
    static readonly Color[] Team = { new(0.5f, 0.66f, 1f), new(1f, 0.55f, 0.44f), new(1f, 0.83f, 0.35f) };

    public override void _Process(double delta)
    {
        if (Visible) QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        float px = size.Y * 0.86f;
        var o = (size - new Vector2(px, px)) / 2f;
        float world = Mode.Map.Size;
        Vector2 P(Vector3 w) => o + new Vector2((w.X / world + 0.5f) * px, (w.Z / world + 0.5f) * px);
        var font = ThemeDB.FallbackFont;

        DrawTextureRect(MapImage.For(Mode.Map), new Rect2(o, new Vector2(px, px)), false, new Color(0.92f, 0.92f, 0.92f, 0.97f));
        for (int i = 1; i < 10; i++)
        {
            float f = i / 10f * px;
            DrawLine(o + new Vector2(f, 0), o + new Vector2(f, px), new Color(1, 1, 1, 0.06f));
            DrawLine(o + new Vector2(0, f), o + new Vector2(px, f), new Color(1, 1, 1, 0.06f));
        }

        float scale = px / world;
        DrawCircle(P(Mode.Zone.Center), KothMode.ZoneRadius * scale, new Color(1f, 0.85f, 0.2f, 0.25f));
        DrawArc(P(Mode.Zone.Center), KothMode.ZoneRadius * scale, 0f, Mathf.Tau, 48, new Color(1f, 0.85f, 0.2f), 2f);
        foreach (var s in Mode.Map.Sites)
        {
            var c = P(s.Center);
            DrawRect(new Rect2(c - new Vector2(4, 4), new Vector2(8, 8)), new Color(0.85f, 0.82f, 0.75f));
            DrawString(font, c + new Vector2(8, -6), s.Name, HorizontalAlignment.Left, -1, 13, new Color(0.9f, 0.9f, 0.85f));
        }
        for (int t = 0; t < 3; t++)
        {
            DrawRect(new Rect2(P(Mode.Map.Bases[t]) - new Vector2(7, 7), new Vector2(14, 14)), Team[t]);
            DrawString(font, P(Mode.Map.Bases[t]) + new Vector2(10, 5), KothMode.TeamNames[t], HorizontalAlignment.Left, -1, 13, Team[t]);
            var r = P(Mode.Rally[t]);
            DrawLine(r + new Vector2(-6, -6), r + new Vector2(6, 6), Team[t], 2f);
            DrawLine(r + new Vector2(-6, 6), r + new Vector2(6, -6), Team[t], 2f);
        }

        // Your side only — the map doesn't tell you where the enemy is.
        var me = Mode.PlayerBody is { Alive: true } pl ? (ICombatant)pl : Mode.Spec.Target;
        int side = me?.Team ?? 0;
        foreach (var b in Mode.Bots)
            if (IsInstanceValid(b) && b.Alive && b.Team == side) DrawCircle(P(b.FeetPos), 3f, Team[side]);
        if (Mode.PlayerBody is { Alive: true } p)
        {
            var at = P(p.FeetPos);
            float h = Mathf.DegToRad(p.Heading);
            var fwd = new Vector2(MathF.Sin(h), -MathF.Cos(h));
            var right = new Vector2(-fwd.Y, fwd.X);
            DrawColoredPolygon(new[] { at + fwd * 10f, at - fwd * 6f + right * 6f, at - fwd * 6f - right * 6f }, Colors.White);
        }
        DrawString(font, o + new Vector2(10, px - 12), "[M] close · grid squares are 200 m", HorizontalAlignment.Left, -1, 13, new Color(1, 1, 1, 0.6f));
    }
}
