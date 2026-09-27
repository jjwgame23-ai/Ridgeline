using Godot;

namespace Ridgeline;

/// <summary>
/// One man to a window. When someone picks a window or a spot on a roof to fight from, he
/// claims it; others looking for one pass it over while he's on his way or there, and
/// look for another.
/// </summary>
public static class PerchClaims
{
    static readonly Dictionary<Vector3, (ICombatant Who, double Until)> _claims = new();

    public static void Clear() => _claims.Clear();

    /// <summary>Is this spot free for me: nobody else has claimed it, and nobody else is standing in it?</summary>
    public static bool Free(Vector3 p, ICombatant me)
    {
        if (_claims.TryGetValue(p, out var c) && c.Who != me && c.Who.Alive && Clock.Now < c.Until && GodotObject.IsInstanceValid((GodotObject)c.Who)) return false;
        foreach (var o in Combatants.All)
            if (o != me && o.Alive && o.FeetPos.DistanceTo(p) < 1.8f) return false;
        return true;
    }

    public static void Claim(Vector3 p, ICombatant me) => _claims[p] = (me, Clock.Now + 120.0);

    /// <summary>
    /// A free window or roof spot for me near this one, facing roughly the same way (for a
    /// squad sent to a post: the first man gets the post, the rest spread along the building).
    /// </summary>
    public static Perch? Near(Vector3 around, Vector3 facing, ICombatant me, float radius = 22f)
    {
        var map = Valley.Current;
        if (map == null) return null;
        Perch? best = null;
        float bestD = float.MaxValue;
        foreach (var pc in map.Perches)
        {
            float d = pc.Pos.DistanceTo(around);
            if (d > radius || d >= bestD) continue;
            if (facing.LengthSquared() > 0.01f && pc.Out.Dot(facing.Normalized()) < 0.2f) continue;
            if (!Free(pc.Pos, me)) continue;
            best = pc;
            bestD = d;
        }
        return best;
    }
}
