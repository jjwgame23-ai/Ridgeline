using Godot;

namespace Ridgeline;

/// <summary>A squad-level drill: what the whole squad is doing, on top of each soldier's own reactions.</summary>
public enum Drill { None, Contact, BreakContact, Indirect, Consolidate }

/// <summary>
/// The squad as a unit rather than six men doing their own thing:
/// - Two fire teams (Alpha and Bravo) balanced by role, each with a team leader; the squad
///   leader stands apart. If he falls, the most experienced man left takes the squad.
/// - Team bounding overwatch in a fight: one team moves while the other covers it, and they
///   swap once the movers are set.
/// - Sectors of fire when stopped: each man watches his own slice.
/// - Drills the squad runs together: react to contact (one team fixes, the other flanks),
///   break contact (bound back team by team), react to indirect fire (get off the impact
///   area), and consolidate on a point just taken (360° security, then a report).
/// </summary>
public sealed partial class Squad
{
    public static readonly List<Squad> All = new();
    public static int Drills, Breaks, Indirects, Consolidations, Flanks;

    public static void ResetAll()
    {
        All.Clear();
        Drills = Breaks = Indirects = Consolidations = Flanks = 0;
    }

    public Squad() { All.Add(this); }

    // ---------------------------------------------------------------- fire teams

    readonly Dictionary<ICombatant, int> _team = new();
    double _teamsAt = -1;
    ICombatant? _lastLeader;

    static int RolePriority(Role r) => r switch
    {
        Role.AutoRifleman => 0,
        Role.Grenadier or Role.AntiTank or Role.HeavyAT or Role.Marksman => 1,
        Role.Medic or Role.Engineer or Role.Ammo => 2,
        _ => 3,
    };

    /// <summary>0 = Alpha, 1 = Bravo, -1 = the squad leader (or a squad too small to split).</summary>
    public int TeamOf(ICombatant c)
    {
        if (Clock.Now - _teamsAt > 3.0) SplitTeams();
        return _team.TryGetValue(c, out var t) ? t : -1;
    }

    public static string TeamName(int t) => t == 0 ? "Alpha" : t == 1 ? "Bravo" : "HQ";

    /// <summary>Deal the squad (bar its leader) into two teams, alternating by role so each gets a support weapon and a specialist.</summary>
    void SplitTeams()
    {
        _teamsAt = Clock.Now;
        _team.Clear();
        var lead = Leader;
        var rest = Members.Where(m => m != lead && m.Alive && GodotObject.IsInstanceValid((GodotObject)m))
                          .OrderBy(m => RolePriority(m.Role)).ThenBy(m => m.Callsign).ToList();
        if (rest.Count < 3) return;
        for (int i = 0; i < rest.Count; i++) _team[rest[i]] = i % 2;
        PairUp();
    }

    public ICombatant? TeamLeader(int team) =>
        Members.Where(m => m.Alive && TeamOf(m) == team).OrderByDescending(m => m is Bot b ? b.P.Skill : 1f).FirstOrDefault();

    bool Teams => Alive >= 4 && _team.Count >= 3;

    // ---------------------------------------------------------------- bounding overwatch

    public int BoundingTeam;
    readonly bool[] _teamMoved = new bool[2];
    double _swapAt;

    /// <summary>
    /// May this man bound now? With teams: only the bounding team moves (all of it, together),
    /// while the other covers; once the bounding team has moved and is set again, they swap.
    /// A small squad falls back to "a third at a time".
    /// </summary>
    public bool MayBound(Bot b, double now)
    {
        int t = TeamOf(b);
        if (!Teams || t < 0) return MayMove(now);
        bool moving = Members.Any(m => TeamOf(m) == BoundingTeam && _moving.TryGetValue(m, out var u) && u > now && m.Alive);
        // Swap once the movers are set again, or if they've had their turn and found nowhere to go.
        if (((_teamMoved[BoundingTeam] && !moving) || (!moving && now > _swapAt + 12.0)) && now > _swapAt)
        {
            _teamMoved[BoundingTeam] = false;
            BoundingTeam ^= 1;
            _swapAt = now + 2.0;
            if (Leader is Bot l) Comms.Say(l, $"{TeamName(BoundingTeam)}, move! {TeamName(BoundingTeam ^ 1)}, cover!");
        }
        return t == BoundingTeam;
    }

    /// <summary>The members of the other team: the ones who cover a bound.</summary>
    public IEnumerable<Bot> Overwatch(Bot mover)
    {
        int t = TeamOf(mover);
        foreach (var m in Members)
            if (m is Bot b && b != mover && b.Alive && GodotObject.IsInstanceValid(b) && (t < 0 || TeamOf(b) != t)) yield return b;
    }

    void NoteMoved(ICombatant c)
    {
        int t = TeamOf(c);
        if (t >= 0) _teamMoved[t] = true;
    }

    // ---------------------------------------------------------------- sectors of fire

    /// <summary>
    /// Which way this man watches when the squad is stopped. On a point (holding it, or
    /// consolidating): the squad shares out the whole circle, each his own slice. At a halt on
    /// the move: alternate sides of the direction of travel (a herringbone). Null: no sector
    /// (moving, fighting, or alone).
    /// </summary>
    public Vector3? SectorFor(Bot b, bool inZone)
    {
        var alive = Members.Where(m => m.Alive).ToList();
        int i = alive.IndexOf(b), n = alive.Count;
        if (i < 0 || n < 2) return null;
        if ((Defend || Drill == Drill.Consolidate) && inZone && Objective != null)
        {
            // Spread round the compass; the leader takes the side the last contact came from.
            var baseDir = ContactAt != Vector3.Zero ? (ContactAt - Objective.Center) with { Y = 0f } : Vector3.Forward;
            if (baseDir.LengthSquared() < 1f) baseDir = Vector3.Forward;
            return baseDir.Normalized().Rotated(Vector3.Up, Mathf.Tau * i / n);
        }
        var lead = Leader;
        if (lead == null || Objective == null || lead.Vel.LengthSquared() > 0.5f) return null;
        var fwd = (Objective.Center - lead.FeetPos) with { Y = 0f };
        if (fwd.LengthSquared() < 1f) return null;
        fwd = fwd.Normalized();
        if (b == lead) return fwd;
        float side = i % 2 == 0 ? -1f : 1f;
        return fwd.Rotated(Vector3.Up, side * Mathf.DegToRad(i < 3 ? 60f : 110f));
    }

    // ---------------------------------------------------------------- drills

    public Drill Drill { get; private set; }
    public double DrillUntil;
    /// <summary>Bumped each time a drill starts, so each soldier reacts to it once.</summary>
    public int DrillId;
    public int AssaultTeam = 1;
    public Vector3 ImpactAt;
    double _consolidateUntil;

    public bool Consolidating => Drill == Drill.Consolidate && Clock.Now < DrillUntil;

    void StartDrill(Drill d, double seconds)
    {
        Drill = d;
        DrillUntil = Clock.Now + seconds;
        DrillId++;
        Drills++;
        if (DuelMode.Verbose) GD.Print($"[{Clock.Now:0}s] {Name} drill {d}" + (Leader is { } l ? $" (leader {l.Callsign}, {Alive} alive, contact {l.FeetPos.DistanceTo(d == Drill.Indirect ? ImpactAt : ContactAt):0} m)" : ""));
    }

    /// <summary>Called by the brain: the drill still running, if any.</summary>
    public Drill Current
    {
        get
        {
            if (Drill != Drill.None && Clock.Now > DrillUntil) Drill = Drill.None;
            return Drill;
        }
    }

    /// <summary>
    /// React to contact: the squad has come under fire (or the leader has called it) with the
    /// enemy at 50-300 m. The team nearest the enemy goes firm and suppresses; the other flanks.
    /// </summary>
    public void ReactToContact(Vector3 at)
    {
        if (!Teams || Defend || Current is Drill.BreakContact or Drill.Indirect or Drill.Consolidate) return;
        var lead = Leader;
        if (lead == null) return;
        float d = lead.FeetPos.DistanceTo(at);
        if (d < 50f || d > 300f || Current == Drill.Contact) return;
        // The team closer to the enemy fixes them; the other goes round.
        float Near(int t) => Members.Where(m => m.Alive && TeamOf(m) == t).Select(m => m.FeetPos.DistanceTo(at)).DefaultIfEmpty(9999f).Min();
        AssaultTeam = Near(0) <= Near(1) ? 1 : 0;
        ContactAt = at;
        StartDrill(Drill.Contact, 45.0);
        Flanks++;
        var toThem = (at - lead.FeetPos) with { Y = 0f };
        string side = toThem.Cross(Vector3.Up).Dot(Vector3.Right) > 0f ? "right" : "left";
        if (lead is Bot l) Comms.Say(l, $"Contact {Comms.Bearing(lead.FeetPos, at)}! {TeamName(AssaultTeam ^ 1)}, suppress! {TeamName(AssaultTeam)}, flank {side}!");
    }

    /// <summary>Break contact: outnumbered and outgunned, not holding ground. Bound back, team by team.</summary>
    public void BreakContact(Vector3 from)
    {
        if (Defend || Current is Drill.BreakContact or Drill.Consolidate) return;
        ContactAt = from;
        if (Phase != AssaultPhase.None) EndAssault();
        StartDrill(Drill.BreakContact, 35.0);
        Breaks++;
        if (Leader is Bot l) Comms.Say(l, "Too many of them! Break contact, bound back!");
    }

    /// <summary>
    /// Mortar or artillery landing close: don't just duck, get off the impact area — the next
    /// rounds come to the same place.
    /// </summary>
    public static void IndirectImpact(Vector3 at)
    {
        foreach (var sq in All)
        {
            if (sq.Alive == 0 || sq.Current is Drill.Indirect) continue;
            if (!sq.Members.Any(m => m.Alive && m.Ride == null && m.FeetPos.DistanceTo(at) < 60f)) continue;
            sq.ImpactAt = at;
            sq.StartDrill(Drill.Indirect, 14.0);
            Indirects++;
            if (sq.Leader is Bot l) Comms.Say(l, "Incoming! Move, get off the impact area!");
        }
    }

    /// <summary>
    /// The point this squad was attacking has been taken: consolidate. Hold it (360° security),
    /// check casualties and ammo, report, and ask for resupply. The commander leaves the squad
    /// on it for a minute before sending it on.
    /// </summary>
    public void Consolidate(Site site, IObjective obj)
    {
        Order(site, obj, true, "Consolidate");
        StartDrill(Drill.Consolidate, 40.0);
        _consolidateUntil = Clock.Now + 40.0;
        Consolidations++;
        if (Leader is Bot l)
        {
            int down = Members.Count(m => !m.Alive);
            float ammo = Members.Where(m => m.Alive).Select(m => m.AmmoLevel).DefaultIfEmpty(1f).Average();
            string state = ammo > 0.6f ? "green" : ammo > 0.3f ? "amber" : "red";
            Comms.Say(l, $"{Name}: {site.Name} secure. {(down == 0 ? "No casualties" : $"{down} down")}, ammo {state}. Setting security.");
        }
    }

    /// <summary>The commander won't re-task a squad in the middle of consolidating.</summary>
    public bool Busy => Clock.Now < _consolidateUntil;
}
