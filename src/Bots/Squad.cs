using Godot;

namespace Ridgeline;

/// <summary>An observation or overwatch position: sit here and watch (and shoot at) somewhere else.</summary>
public sealed class PointObjective : IObjective
{
    public Vector3 Center { get; set; }
    public Vector3 Watch;
    public float Radius => 9f;

    public Vector3 PointFor(Bot b, RandomNumberGenerator rng)
    {
        // A post up in a building (a window, a rooftop): that exact spot, not the ground under it.
        if (Effects.Ground is IGround g0 && Center.Y - g0.HeightAt(Center.X, Center.Z) > 1.5f)
        {
            b.LookOut = (Watch - Center) with { Y = 0f };
            return Center;
        }
        float a = rng.Randf() * Mathf.Tau, r = rng.RandfRange(0f, 6f);
        var p = Center + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
        return Effects.Ground is IGround g ? p with { Y = g.HeightAt(p.X, p.Z) } : p;
    }
}

/// <summary>Stay with another squad (logistics following the assault).</summary>
public sealed class FollowObjective : IObjective
{
    public Squad Target = null!;
    Vector3 _last;

    public Vector3 Center => _last = Target.Position ?? _last;
    public float Radius => 22f;

    public Vector3 PointFor(Bot b, RandomNumberGenerator rng)
    {
        var c = Center;
        float a = rng.Randf() * Mathf.Tau, r = rng.RandfRange(6f, 16f);
        var p = c + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
        return Effects.Ground is IGround g ? p with { Y = g.HeightAt(p.X, p.Z) } : p;
    }
}

/// <summary>
/// A squad: a handful of soldiers with a leader and one order at a time from
/// their faction's commander ("attack Vrbnik", "defend Hilltop"). On the move the
/// members keep a loose wedge behind the leader instead of a conga line; at the
/// objective they spread out over it. In a fight it rations movement, so only a
/// few bound forward at once while the rest keep the enemy's heads down.
/// </summary>
public sealed class Squad
{
    public int Team, Number;
    public SquadKind Kind = SquadKind.Rifle;
    public string Name => $"{KothMode.TeamNames[Team]}-{Number}";
    public string KindName => Roles.Name(Kind);
    /// <summary>Which way the enemy is, from the objective, as the commander sees it (for engineers digging in).</summary>
    public Vector3 ThreatAxis;
    /// <summary>A crew's own vehicle (armour, transport, the logistics truck).</summary>
    public Vehicle? Vehicle;
    /// <summary>A transport sent to carry this squad.</summary>
    public Vehicle? Transport;
    /// <summary>Logistics: where the commander wants a FOB, and when building started.</summary>
    public Vector3? FobSite;
    public double FobBuildStart = -1;

    /// <summary>A recon team saw someone: pass it on (the faction's intel picture, the map).</summary>
    public static event Action<int, ICombatant, Vector3>? Spotted;
    public static void ReportSpotted(int team, ICombatant who, Vector3 at) => Spotted?.Invoke(team, who, at);
    public readonly List<ICombatant> Members = new();

    public IObjective? Objective { get; private set; }
    public Site? Site { get; private set; }
    public bool Defend { get; private set; }
    public double OrderSince { get; private set; }

    /// <summary>The player is leading and wants the squad on them (in formation), rather than off working the objective.</summary>
    public bool FollowPlayer;
    /// <summary>The player picked the objective on the map; the commander leaves this squad alone until then.</summary>
    public double PlayerOrderUntil = -1;

    readonly Dictionary<ICombatant, double> _moving = new();

    string _verb = "", _what = "";
    public string OrderText => Objective == null ? "no orders" : $"{_verb} {_what}";

    public ICombatant? Leader
    {
        get
        {
            // A player who picked squad leader leads; otherwise the first bot does.
            foreach (var m in Members)
                if (m is Player { Alive: true, Kit: Role.Leader } && GodotObject.IsInstanceValid((GodotObject)m)) return m;
            foreach (var m in Members)
                if (m is Bot { Alive: true } && GodotObject.IsInstanceValid((GodotObject)m)) return m;
            return null;
        }
    }

    public int Alive => Members.Count(m => m.Alive && GodotObject.IsInstanceValid((GodotObject)m));

    /// <summary>Roughly where the squad is: the leader, or nothing if they're all dead.</summary>
    public Vector3? Position => Leader?.FeetPos;

    /// <param name="verb">"Attack", "Defend", "Overwatch", "Observe", "Fortify", "Resupply"...</param>
    public void Order(Site? site, IObjective obj, bool defend, string? verb = null, string? what = null)
    {
        bool changed = Site != site || Defend != defend || Objective?.GetType() != obj.GetType() || (verb != null && verb != _verb);
        _verb = verb ?? (defend ? "Defend" : "Attack");
        _what = what ?? site?.Name ?? "";
        Site = site;
        Objective = obj;
        Defend = defend;
        if (!changed) return;
        OrderSince = Clock.Now;
        foreach (var m in Members)
            if (m is Bot { Alive: true } b && GodotObject.IsInstanceValid(b)) b.Brain.ObjectiveChanged();
    }

    public void Join(ICombatant c)
    {
        Members.RemoveAll(m => !GodotObject.IsInstanceValid((GodotObject)m) || !m.Alive);
        if (!Members.Contains(c)) Members.Add(c);
    }

    // ---------------------------------------------------------------- bounding

    public int MovingCount(double now)
    {
        int n = 0;
        foreach (var kv in _moving)
            if (kv.Value > now && kv.Key.Alive) n++;
        return n;
    }

    /// <summary>How many may move at once: a third of the squad, at least one.</summary>
    public bool MayMove(double now) => MovingCount(now) < Math.Max(1, Alive / 3);

    public void MarkMoving(ICombatant c, double until) => _moving[c] = until;

    // ---------------------------------------------------------------- formation

    /// <summary>
    /// Where this member should be while the squad travels: a wedge behind the
    /// leader, oriented along the leader's heading (or toward the objective when
    /// the leader is standing still).
    /// </summary>
    public Vector3? SlotFor(Bot b)
    {
        var lead = Leader;
        if (lead == null || lead == b) return null;
        int i = 0;
        foreach (var m in Members)
        {
            if (m == lead || !m.Alive) continue;
            if (m == b) break;
            i++;
        }
        var fwd = lead.Vel with { Y = 0f };
        if (fwd.LengthSquared() < 0.25f && Objective != null) fwd = (Objective.Center - lead.FeetPos) with { Y = 0f };
        if (fwd.LengthSquared() < 0.01f) fwd = Vector3.Forward;
        fwd = fwd.Normalized();
        var right = fwd.Cross(Vector3.Up);
        int row = i / 2 + 1;
        float side = i % 2 == 0 ? -1f : 1f;
        // The shape depends on the ground the leader is on (see Surroundings).
        var env = lead is Bot lb ? lb.Brain.Env : Surroundings.At(null, lead.FeetPos);
        return env switch
        {
            // A staggered file, close up: down one side of the street, in through a door one after the other.
            EnvKind.Urban or EnvKind.Interior => lead.FeetPos - fwd * ((i + 1) * 2.8f) + right * (side * (env == EnvKind.Interior ? 0.4f : 1.2f)),
            // Wedge, close enough to keep sight of each other between the trunks.
            EnvKind.Forest => lead.FeetPos + right * (side * row * 3.5f) - fwd * (row * 3.5f),
            // Open ground: a wide wedge.
            _ => lead.FeetPos + right * (side * row * 7f) - fwd * (row * 5f),
        };
    }
}
