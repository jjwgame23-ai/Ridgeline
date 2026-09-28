using Godot;

namespace Ridgeline;

public enum RadioKind { Armor, HeavyContact, Air }

public sealed class RadioReport
{
    public int Team;
    public RadioKind Kind;
    public Vector3 Pos;
    public double At;
    public Vehicle? Vehicle;
    public string From = "";
    public int Count;
}

/// <summary>
/// The radio net. Soldiers call in what matters to the whole side — enemy armour
/// ("BMP, north, 400 meters!"), a squad in a heavy fight — and the commander acts on
/// it: sends AT teams after armour, sends reinforcements to a fight going badly.
/// Reports are rate-limited per thing, so one tank isn't reported forty times.
/// </summary>
public static class Radio
{
    public static readonly List<RadioReport> Log = new();
    public static event Action<RadioReport>? Heard;

    public static void Reset() => Log.Clear();

    public static void Report(ICombatant from, RadioKind kind, Vector3 pos, Vehicle? v = null, int count = 0)
    {
        double now = Clock.Now;
        // An aircraft is called in as one, not as armour. (Reported as armour, tanks were sent to overwatch where a
        // helicopter had been, AT teams to ambush it, and gunships to fly air support over it.)
        if (kind == RadioKind.Armor && v is { Def.Air: true }) kind = RadioKind.Air;
        // Someone on our side already called this in recently.
        foreach (var r in Log)
            if (r.Team == from.Team && r.Kind == kind && now - r.At < (kind == RadioKind.HeavyContact ? 30.0 : 15.0)
                && (kind == RadioKind.HeavyContact ? r.Pos.DistanceTo(pos) < 120f : r.Vehicle == v))
                return;
        var rep = new RadioReport { Team = from.Team, Kind = kind, Pos = pos, At = now, Vehicle = v, From = from.Callsign, Count = count };
        Log.Add(rep);
        if (Log.Count > 300) Log.RemoveRange(0, 100);
        float d = from.FeetPos.DistanceTo(pos);
        string where = $"{Comms.Bearing(from.FeetPos, pos)}, {d:0} meters";
        Comms.Say(from, kind switch
        {
            RadioKind.Armor => $"Enemy armor! {v?.Def.ClassName.ToUpperInvariant()}, {where}!",
            RadioKind.Air => $"Enemy aircraft! {v?.Def.ClassName.ToUpperInvariant()}, {where}!",
            _ => $"Heavy contact, {where}! We need support!",
        });
        Heard?.Invoke(rep);
    }

    /// <summary>
    /// Enemy air defence (an anti-aircraft vehicle) this side has seen or been fired on by lately, still in
    /// action and within <paramref name="within"/> of a point: somewhere aircraft shouldn't go.
    /// </summary>
    public static RadioReport? AirDefenceNear(int team, Vector3 p, float within, double maxAge = 180.0)
    {
        double now = Clock.Now;
        for (int i = Log.Count - 1; i >= 0; i--)
        {
            var r = Log[i];
            if (now - r.At > maxAge) break;
            if (r.Team == team && r.Kind == RadioKind.Armor && r.Vehicle is { Destroyed: false, Def.Kind: VKind.SPAA }
                && ((r.Pos - p) with { Y = 0f }).Length() < within) return r;
        }
        return null;
    }

    /// <summary>Every enemy anti-aircraft vehicle a side has heard of lately (the freshest report of each).</summary>
    public static IEnumerable<RadioReport> AirDefences(int team, double maxAge = 180.0)
    {
        double now = Clock.Now;
        var seen = new HashSet<Vehicle>();
        for (int i = Log.Count - 1; i >= 0; i--)
        {
            var r = Log[i];
            if (now - r.At > maxAge) break;
            if (r.Team == team && r.Kind == RadioKind.Armor && r.Vehicle is { Destroyed: false, Def.Kind: VKind.SPAA } av && seen.Add(av)) yield return r;
        }
    }

    /// <summary>The freshest report of a kind for a side, optionally near a point.</summary>
    public static RadioReport? Latest(int team, RadioKind kind, double maxAge)
    {
        double now = Clock.Now;
        for (int i = Log.Count - 1; i >= 0; i--)
        {
            var r = Log[i];
            if (now - r.At > maxAge) break;
            if (r.Team == team && r.Kind == kind && (r.Vehicle == null || !r.Vehicle.Destroyed)) return r;
        }
        return null;
    }
}
