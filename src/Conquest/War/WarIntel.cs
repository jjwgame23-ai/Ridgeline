namespace Ridgeline;

/// <summary>
/// What each side knows of its enemies: the enemy units it has seen, where, how strong, and when. Commanders plan on
/// this, not on the truth. A sighting more than six hours old is dropped: units move on.
/// </summary>
public sealed class WarIntel
{
    public readonly Dictionary<int, (float X, float Z, int Strength, bool Armour, double At)>[] Known = { new(), new(), new() };

    public void Saw(int side, Unit enemy, double now) => Known[side][enemy.Id] = (enemy.X, enemy.Z, enemy.People, enemy.Armed, now);

    public void Forget(double now)
    {
        foreach (var k in Known)
            foreach (var id in k.Where(e => now - e.Value.At > 6 * 3600).Select(e => e.Key).ToList()) k.Remove(id);
    }

    /// <summary>Enemy soldiers of <paramref name="enemy"/> a side knows of within <paramref name="r"/> of a point.</summary>
    public int Near(War war, int side, int enemy, float x, float z, float r)
    {
        int n = 0;
        foreach (var (id, e) in Known[side])
            if (war.Units[id].Side == enemy && (e.X - x) * (e.X - x) + (e.Z - z) * (e.Z - z) < r * r) n += e.Strength;
        return n;
    }
}
