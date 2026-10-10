using Godot;

namespace Ridgeline;

/// <summary>
/// What the battle maps' HUD (TerritoryHud) and the Conquest window's (WindowHud) both show of the player's squad: its
/// roster, its order and the way to it, and the briefing (what the squad is doing and your part in it).
/// </summary>
public static class SquadPanel
{
    /// <summary>Your squad at a glance: who's who, and who's hurt or out of ammo.</summary>
    public static string Roster(Squad? sq)
    {
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

    /// <summary>The squad's order and the way to it, with the screen marker on it (or on the squad's ride until you're aboard).</summary>
    public static string Nav(Squad? sq, Player? body, Vector3? me, bool leads)
    {
        double now = Clock.Now;
        string text;
        if (sq?.Objective != null)
        {
            var goal = sq.Objective.Center;
            string where = me is Vector3 pos ? $" · {(goal - pos with { Y = goal.Y }).Length():0} m {Comms.Bearing(pos, goal)}" : "";
            text = $"{sq.Name} · {sq.OrderText}{where}{(sq.FollowPlayer && leads ? " · squad on you" : "")}{(sq.PlayerOrderUntil > now ? " · your order" : "")}";
            HudOverlay.Marker = goal;
            // A ride for the squad: point the player at it until they're aboard.
            var ride = sq.Transport;
            if (ride is { Destroyed: false } && body is { Alive: true } pb && pb.Ride != ride && me is Vector3 at)
            {
                float d = ride.GlobalPosition.DistanceTo(at);
                text += ride.Boarding
                    ? $"\n▶ MOUNT UP: your squad's {ride.Def.Name} is waiting, {d:0} m {Comms.Bearing(at, ride.GlobalPosition)} — [{Controls.Keys("use")}] to get in"
                    : $"\n▶ A {ride.Def.Name} is coming to pick your squad up ({d:0} m {Comms.Bearing(at, ride.GlobalPosition)})";
                HudOverlay.Marker = ride.GlobalPosition;
            }
        }
        else
        {
            text = "";
            HudOverlay.Marker = null;
        }
        return text;
    }

    /// <summary>The squad briefing: what the squad is doing, and your part in it (with its spot and sector on the screen).</summary>
    public static string Brief(Squad? sq, Player? body, bool leads)
    {
        HudOverlay.Spot = HudOverlay.Sector = null;
        string brief = "";
        if (sq != null && body is { Alive: true } pp && sq.Members.Contains(pp))
        {
            var br = sq.Brief(pp);
            var lines = new List<string> { $"[color=#cfe8ff]{br.Doing}[/color]" + (br.Team != "" ? $"  [color=#9aa]· {br.Team}[/color]" : "") };
            lines.Add($"[color=#b8ffb0]▶ {br.Task}[/color]");
            foreach (var sv in sq.Support)
                lines.Add($"[color=#d8c890]{sv.Def.Name}: {(sv.Task != "" ? sv.Task : "with you")}{(sv.FireAt != null && Clock.Now < sv.FireAtUntil ? $" — {sv.FireAtWhy}" : "")}[/color]");
            if (leads) lines.Add($"[color=#888]march: {(sq.PlayerMarch?.ToString() ?? "auto")} · {Controls.Keys("squad_menu")}: squad commands · {Controls.Keys("squad_follow")}: on me · {Controls.Keys("map")}: map[/color]");
            brief = string.Join("\n", lines);
            HudOverlay.Spot = br.Spot;
            HudOverlay.Sector = br.Sector;
        }
        return brief;
    }
}
