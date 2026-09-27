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
            var look = (Watch - Center) with { Y = 0f };
            b.LookOut = look;
            // One man to a window: the first gets the post, the others a window along from it.
            if (PerchClaims.Free(Center, b)) { PerchClaims.Claim(Center, b); return Center; }
            if (PerchClaims.Near(Center, look, b) is Perch other)
            {
                PerchClaims.Claim(other.Pos, b);
                b.LookOut = other.Out;
                return other.Pos;
            }
            // None free: the ground below, by the building.
            float a0 = rng.Randf() * Mathf.Tau;
            var q = Center + new Vector3(MathF.Cos(a0), 0f, MathF.Sin(a0)) * rng.RandfRange(3f, 8f);
            return q with { Y = g0.HeightAt(q.X, q.Z) };
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
public sealed partial class Squad
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
    /// <summary>A vehicle crew: the rifle squad it's working with (carries, for an IFV/APC; supports, for a tank).</summary>
    public Squad? Supports;
    /// <summary>The (player) leader's orders: everyone put fire on a point; the squad's vehicle fire on a point; the carrier come and get us, or let us out.</summary>
    public Vector3 SuppressAt, VehicleFireAt;
    public double SuppressUntil, VehicleFireUntil;
    public bool WantRide, WantDismount;

    /// <summary>The vehicles working with this squad (its carrier, its tank), with what they're up to.</summary>
    public IEnumerable<Vehicle> Support => All.Where(s => s.Supports == this && s.Vehicle is { Destroyed: false }).Select(s => s.Vehicle!);
    /// <summary>Logistics: where the commander wants a FOB, and when building started.</summary>
    public Vector3? FobSite;
    public double FobBuildStart = -1;

    /// <summary>A recon team saw someone: pass it on (the faction's intel picture, the map).</summary>
    public static event Action<int, ICombatant, Vector3>? Spotted;
    public static void ReportSpotted(int team, ICombatant who, Vector3 at) => Spotted?.Invoke(team, who, at);
    public readonly List<ICombatant> Members = new();

    /// <summary>
    /// The squad is in a fight (someone's been shot at, hurt, or the leader called it):
    /// until then, contacts that aren't in the way are bypassed on the way to the objective;
    /// once engaged, everyone fights back rather than walking on and getting shot.
    /// </summary>
    /// <summary>An assault on an enemy-held point forms up short of it first; this is the objective it's formed up for.</summary>
    public IObjective? StagedFor;
    public double StageSince = -1;
    /// <summary>Is this site held by the enemy (or known to have enemies on it), for this team? Set by the game mode.</summary>
    public static Func<Site, int, bool>? Hostile;

    public double EngagedUntil = -1;
    public Vector3 ContactAt;
    public bool Engaged => Clock.Now < EngagedUntil;
    public void Engage(Vector3 at)
    {
        bool fresh = !Engaged;
        ContactAt = at;
        EngagedUntil = Clock.Now + 20.0;
        if (!fresh) return;
        Engagements++;
        // Holding a point and it's attacked: everyone takes up a position facing the attack now.
        if (Defend)
            foreach (var m in Members)
                if (m is Bot { Alive: true } b && GodotObject.IsInstanceValid(b)) b.Brain.ObjectiveChanged();
    }

    public static int Engagements, Assaults, Hunts;

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
            // A player who picked squad leader leads; then the squad leader himself; failing
            // him, the most experienced man left (a team leader), not whoever happens to be first.
            ICombatant? best = null;
            float bestScore = float.MinValue;
            foreach (var m in Members)
            {
                if (!m.Alive || !GodotObject.IsInstanceValid((GodotObject)m)) continue;
                if (m is Player { Kit: Role.Leader }) return m;
                if (m is not Bot b) continue;
                float score = (b.Role == Role.Leader ? 10f : 0f) + b.P.Skill;
                if (score > bestScore) { bestScore = score; best = m; }
            }
            if (best != _lastLeader)
            {
                if (_lastLeader != null && !_lastLeader.Alive && best is Bot nb) Comms.Say(nb, $"{_lastLeader.Callsign}'s down! I've got the squad!");
                _lastLeader = best;
            }
            return best;
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
        StagedFor = null;
        StageSince = -1;
        AssaultOn = null;
        _bowWaiting = false;
        CancelCrossing();
        if (Phase != AssaultPhase.None) SetPhase(AssaultPhase.None);
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

    public void MarkMoving(ICombatant c, double until)
    {
        _moving[c] = until;
        NoteMoved(c);
    }

    // ---------------------------------------------------------------- formation

    /// <summary>
    /// Where this member should be while the squad travels: a wedge behind the
    /// leader, oriented along the leader's heading (or toward the objective when
    /// the leader is standing still).
    /// </summary>
    /// <summary>
    /// Where the leader has actually walked (newest last, one every 1.5 m). In town and
    /// indoors the squad files along it: every point on it is somewhere you can stand, on the
    /// floor the leader was on, reached the way the leader went.
    /// </summary>
    readonly List<Vector3> _trail = new();

    public void Crumb(Vector3 p)
    {
        if (_trail.Count > 0 && _trail[^1].DistanceTo(p) < 1.5f) return;
        _trail.Add(p);
        if (_trail.Count > 40) _trail.RemoveAt(0);
    }

    /// <summary>A point this far back along the leader's trail (for a tank following its infantry into town).</summary>
    public Vector3? TrailPoint(float dist) => Leader is { } l ? Behind(l.FeetPos, dist) : null;

    /// <summary>The point on the leader's trail this far back from where they are now, if the trail is long enough.</summary>
    Vector3? Behind(Vector3 lead, float dist)
    {
        float acc = 0f;
        var prev = lead;
        for (int i = _trail.Count - 1; i >= 0; i--)
        {
            acc += _trail[i].DistanceTo(prev);
            if (acc >= dist) return _trail[i];
            prev = _trail[i];
        }
        return null;
    }

    public Vector3? SlotFor(ICombatant b)
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
        // March order and the attack's phases first; otherwise the ground decides the shape.
        if ((lead is not Player || PlayerMarch != null) && OrderSlot(b, lead, i, fwd, right) is Vector3 os) return os;
        int row = i / 2 + 1;
        float side = i % 2 == 0 ? -1f : 1f;
        // The shape depends on the ground the leader is on (see Surroundings).
        var env = lead is Bot lb ? lb.Brain.Env : Surroundings.At(null, lead.FeetPos);
        if (lead is not Bot) Crumb(lead.FeetPos);
        // In town and indoors: in file along the leader's own trail, not at a geometric offset
        // that may be inside a wall, under a staircase or on the wrong floor.
        if (env is EnvKind.Urban or EnvKind.Interior && Behind(lead.FeetPos, (i + 1) * 2.6f) is Vector3 onTrail) return onTrail;
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
