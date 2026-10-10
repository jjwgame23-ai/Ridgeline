using Godot;

namespace Ridgeline;

/// <summary>
/// What each side knows about where the enemy is, pooled from everyone's eyes: each
/// sighting is a point with a time. Fire support (mortars, gunships) works from this —
/// a cluster of fresh sightings is a target.
/// </summary>
public static class Intel
{
    public readonly record struct Contact(int Team, ICombatant Who, Vector3 Pos, double At);
    static readonly List<Contact> _contacts = new();
    static readonly Dictionary<(int, ICombatant), double> _last = new();

    public static void Reset() { _contacts.Clear(); _last.Clear(); }

    /// <summary>Where each enemy a side has seen in the last <paramref name="maxAge"/> s was last seen, and when (the map).</summary>
    public static IEnumerable<Contact> Recent(int team, double maxAge)
    {
        double now = Clock.Now;
        var seen = new HashSet<ICombatant>();
        for (int i = _contacts.Count - 1; i >= 0; i--)
        {
            var c = _contacts[i];
            if (now - c.At > maxAge) yield break;
            if (c.Team == team && c.Who.Alive && seen.Add(c.Who)) yield return c;
        }
    }

    public static void Report(int team, ICombatant who, Vector3 pos)
    {
        double now = Clock.Now;
        if (_last.TryGetValue((team, who), out var t) && now - t < 5.0) return;
        _last[(team, who)] = now;
        _contacts.Add(new Contact(team, who, pos, now));
        if (_contacts.Count > 2000) _contacts.RemoveRange(0, 500);
    }

    /// <summary>
    /// The best fire-support target for a side: the 50 m patch with the most enemies
    /// seen in it lately, inside [minRange, maxRange] of <paramref name="origin"/>, and
    /// with no friendlies near it (no danger-close fire).
    /// </summary>
    public static Vector3? Cluster(int team, Vector3 origin, float minRange, float maxRange, float maxAge = 30.0f, int minCount = 2)
    {
        double now = Clock.Now;
        var cells = new Dictionary<(int, int), (int n, Vector3 sum)>();
        var seen = new HashSet<ICombatant>();
        for (int i = _contacts.Count - 1; i >= 0; i--)
        {
            var c = _contacts[i];
            if (now - c.At > maxAge) break;
            if (c.Team != team || !c.Who.Alive || !seen.Add(c.Who)) continue;
            float d = (c.Pos - origin with { Y = c.Pos.Y }).Length();
            if (d < minRange || d > maxRange) continue;
            var k = ((int)MathF.Floor(c.Pos.X / 50f), (int)MathF.Floor(c.Pos.Z / 50f));
            var cur = cells.GetValueOrDefault(k);
            cells[k] = (cur.n + 1, cur.sum + c.Pos);
        }
        Vector3? best = null;
        int bestN = minCount - 1;
        foreach (var (k, (n, sum)) in cells)
        {
            if (n <= bestN) continue;
            var p = sum / n;
            bool danger = false;
            foreach (var f in Combatants.All)
                if (f.Team == team && !f.Dead && f.FeetPos.DistanceTo(p) < 70f) { danger = true; break; }
            if (danger) continue;
            bestN = n;
            best = p;
        }
        return best;
    }
}
