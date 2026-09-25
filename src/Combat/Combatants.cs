using Godot;

namespace Ridgeline;

public enum HitZone { Head, Torso, Legs }

/// <summary>Physics layers. Bullets and sight rays hit everything.</summary>
public static class Layers
{
    public const uint World = 1;      // terrain, buildings, props: navmesh is baked from these
    public const uint Characters = 2;
    public const uint Grenades = 4;
    public const uint Vehicles = 8;   // hulls: cover and obstacles, but they move, so not in the navmesh
    public const uint Trees = 16;     // solid, but left out of the navmesh
    public const uint Doors = 32;     // solid when shut; the navmesh treats every doorway as open
    public const uint Solid = World | Trees | Vehicles | Doors;
}

public struct HitInfo
{
    public ICombatant? Shooter;
    public Vector3 Point, Dir;
    public float Damage, Distance;
    public HitZone Zone;
    public string Weapon;
}

/// <summary>Anything that fights, can be seen and can be shot: the player and every bot.</summary>
public interface ICombatant
{
    int Team { get; }
    string Callsign { get; }
    bool Alive { get; }
    Vector3 FeetPos { get; }
    Vector3 EyePos { get; }
    Vector3 ChestPos { get; }
    Vector3 Vel { get; }
    float BodyHeight { get; }
    double LastShotTime { get; }
    Rid BodyRid { get; }
    void TakeHit(HitInfo hit);
    /// <summary>A bullet passed within <paramref name="distance"/> m; <paramref name="from"/> is where it was fired.</summary>
    void OnNearMiss(float distance, Vector3 from);
    /// <param name="power">Charge relative to a hand grenade; overpressure reaches further by its cube root.</param>
    void OnBlast(Vector3 pos, float power = 1f);

    Role Role { get; }
    /// <summary>Down and out of the fight, but not dead yet: a medic can bring them back.</summary>
    bool Downed { get; }
    bool Dead { get; }
    Body Body { get; }

    /// <summary>The vehicle they're in, if any.</summary>
    Vehicle? Ride { get; }
    void Mount(Vehicle v, int seat);
    void Dismount(Vector3 at);
    /// <summary>Called by the vehicle every frame: where the seat is (eye level) and which way it faces.</summary>
    void SyncSeat(Vector3 eye, float yawRad);
    float Hp { get; }
    /// <summary>A medic's work: restore up to <paramref name="amount"/> health.</summary>
    void Heal(float amount);
    /// <summary>Spare ammunition, 0 (none) .. 1 (a full load).</summary>
    float AmmoLevel { get; }
    /// <summary>Top up mags, grenades and kit from an ammo bearer. True if anything was needed.</summary>
    bool Resupply();
}

public static class Combatants
{
    public static readonly List<ICombatant> All = new();
    public static event Action<ICombatant, HitInfo>? Killed;
    public static event Action<ICombatant, HitInfo>? Down;
    public static void ReportDowned(ICombatant victim, HitInfo hit) => Down?.Invoke(victim, hit);

    public static void Register(ICombatant c) { if (!All.Contains(c)) All.Add(c); }
    public static void Unregister(ICombatant c) => All.Remove(c);
    public static void ReportKill(ICombatant victim, HitInfo hit) => Killed?.Invoke(victim, hit);

    public static void Blast(Vector3 pos, float power = 1f)
    {
        foreach (var c in All.ToArray())
            if (c.Alive) c.OnBlast(pos, power);
    }

    public static HitZone ZoneFor(ICombatant c, Vector3 p)
    {
        float y = p.Y - c.FeetPos.Y, h = c.BodyHeight;
        if (h < 0.9f) return HitZone.Torso; // prone: no sensible split yet
        if (y > h - 0.27f) return HitZone.Head;
        if (y < h * 0.48f) return HitZone.Legs;
        return HitZone.Torso;
    }

    public enum Part { Head, UpperChest, Chest, Hip }

    /// <summary>
    /// The points on a body that are looked at and aimed at. Sight checks and aim
    /// use the same points, so "I can see his head" means "I can hit his head".
    /// </summary>
    public static Vector3 PointOn(ICombatant c, Part part)
    {
        float h = c.BodyHeight;
        float y = part switch { Part.Head => h - 0.12f, Part.UpperChest => h * 0.76f, Part.Chest => h * 0.58f, _ => h * 0.4f };
        return c.FeetPos + Vector3.Up * y;
    }

    public static float ZoneMultiplier(HitZone z) => z switch { HitZone.Head => 3f, HitZone.Legs => 0.6f, _ => 1f };
}

public static class Comms
{
    public static event Action<ICombatant, string>? Said;
    public static void Say(ICombatant who, string text) => Said?.Invoke(who, text);

    static readonly string[] Dirs = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

    /// <summary>Compass word from one point to another (north is -Z, east is +X).</summary>
    public static string Bearing(Vector3 from, Vector3 to)
    {
        var d = to - from;
        float h = (Mathf.RadToDeg(MathF.Atan2(d.X, -d.Z)) + 360f) % 360f;
        return Dirs[(int)MathF.Round(h / 45f) % 8];
    }
}
