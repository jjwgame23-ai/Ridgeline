using Godot;

namespace Ridgeline;

/// <summary>How a squad moves when it isn't fighting: chosen by the squad leader from the situation.</summary>
public enum March { Travelling, Column, TravellingOverwatch, BoundingOverwatch, Herringbone, AssaultLine, DangerCrossing }

/// <summary>The steps of a deliberate attack on an enemy-held point.</summary>
public enum AssaultPhase { None, Orp, Deploy, Assault }

/// <summary>
/// Movement and the deliberate attack:
/// - March order, picked by the leader: travelling (a wedge in the open, a file along his
///   trail in town and forest) when nothing's expected; travelling overwatch (the second team
///   trailing ~50 m back, ready to react) when a hostile objective is within a kilometre;
///   bounding overwatch (the leader's team advances ~50 m and stops, the other team moves up
///   to it, again) inside 600 m; a herringbone at halts (alternate sides, facing out); an
///   assault line in the assault itself.
/// - The attack: an objective rally point (ORP) ~300 m short, where the squad forms up with
///   all-round security; then the support team goes to a support-by-fire (SBF) position off to
///   one flank with a view of the objective, while the leader takes the assault team to the
///   line of departure (LD); once support is set, it opens fire on the objective and the
///   assault team goes in on line. Nobody chases past the limit of advance (50 m beyond the
///   objective); then the squad consolidates.
/// - Buddy pairs: each team is two pairs; up close, one of a pair moves while the other fires.
/// </summary>
public sealed partial class Squad
{
    public static int Orps, Deploys, BuddySwaps;

    public March MarchOrder { get; private set; }
    /// <summary>A player leading the squad picked this march order (null: the ground decides).</summary>
    public March? PlayerMarch;

    /// <summary>The player leader's pick, applied (bot leaders work it out in UpdateMarch).</summary>
    public void SetPlayerMarch(March? m)
    {
        PlayerMarch = m;
        MarchOrder = m ?? March.Travelling;
    }
    public AssaultPhase Phase { get; private set; }
    public IObjective? AssaultOn;
    public Vector3 OrpAt, LdAt, SbfAt;
    public int SbfTeam;
    double _phaseSince, _leadStillSince = -1;
    bool _bowWaiting;
    double _bowWaitSince;
    double _closeUpSince = -1, _closeUpAgain = -1;
    public static int CloseUps;

    /// <summary>
    /// On the move, strung out behind the leader: anyone in a fight, or half the squad, 60 m and more from him. He
    /// waits for them to close up, rather than walk on and leave them (a squad leader pushing on to the objective
    /// while his men fought 150 m back was much of the time a squad's men spent on their own). Not in an assault
    /// or a crossing, where the teams are apart on purpose; and no more than 40 s at a time, so someone stuck
    /// can't hold the squad forever.
    /// </summary>
    public bool StrungOut(Bot lead)
    {
        double now = Clock.Now;
        if (now < _closeUpAgain || Phase != AssaultPhase.None || Cross != Crossing.None || FollowPlayer || Defend) { _closeUpSince = -1; return false; }
        int n = 0, far = 0;
        bool fighting = false;
        foreach (var m in Members)
        {
            if (m == lead || !m.Alive || m.Ride != null) continue;
            n++;
            if (((m.FeetPos - lead.FeetPos) with { Y = 0f }).LengthSquared() < 60f * 60f) continue;
            far++;
            if (m is Bot b && b.Brain.State is BotState.Engage or BotState.InCover or BotState.Hold or BotState.TakeCover or BotState.Flank) fighting = true;
        }
        if (n == 0 || !(fighting || far * 2 >= n)) { _closeUpSince = -1; return false; }
        if (_closeUpSince < 0)
        {
            _closeUpSince = now;
            CloseUps++;
            Prof.Count(fighting ? "squad:close-up (contact behind)" : "squad:close-up");
            Comms.Say(lead, fighting ? "Hold up, they're in contact back there. On me!" : "Close it up! On me!");
        }
        else if (now - _closeUpSince > 40.0) { _closeUpSince = -1; _closeUpAgain = now + 40.0; return false; }
        return true;
    }

    public double PhaseAge => Clock.Now - _phaseSince;

    void SetPhase(AssaultPhase p)
    {
        Phase = p;
        _phaseSince = Clock.Now;
        if (DuelMode.Verbose) GD.Print($"[{Clock.Now:0}s] {Name} assault phase {p}" + (Objective is SiteObjective so ? $" on {so.Site.Name}" : ""));
    }

    /// <summary>Past the limit of advance: 50 m beyond the objective, during the assault or while consolidating. Don't chase out there.</summary>
    public bool BeyondLoa(Vector3 p) =>
        Objective != null && (Phase == AssaultPhase.Assault || Consolidating)
        && ((p - Objective.Center) with { Y = 0f }).Length() > Objective.Radius + 50f;

    // ---------------------------------------------------------------- march order

    /// <summary>The leader's reading of the situation, a few times a second.</summary>
    public void UpdateMarch(Bot lead, bool inZone)
    {
        if (lead.Vel.LengthSquared() > 0.5f) _leadStillSince = -1;
        else if (_leadStillSince < 0) _leadStillSince = Clock.Now;
        bool still = _leadStillSince > 0 && Clock.Now - _leadStillSince > 2.0;

        // Time limits that hold even while the leader is busy fighting: an attack that's gone stale
        // is called off (or, once the teams are deployed, launched).
        if (Phase == AssaultPhase.Orp && PhaseAge > 60.0) EndAssault();
        else if (Phase == AssaultPhase.Deploy && PhaseAge > 120.0) BeginAssault();
        else if (Phase == AssaultPhase.Assault && PhaseAge > 100.0) EndAssault();
        if (Phase == AssaultPhase.Orp) { MarchOrder = March.Herringbone; return; }
        if (Phase == AssaultPhase.Assault) { MarchOrder = March.AssaultLine; return; }
        if (Phase == AssaultPhase.Deploy || Objective == null || inZone || Engaged) { CancelCrossing(); MarchOrder = March.Travelling; return; }
        if (lead is Bot lb0 && UpdateCrossing(lb0)) { MarchOrder = March.DangerCrossing; return; }
        float d = ((Objective.Center - lead.FeetPos) with { Y = 0f }).Length();
        bool hostile = Objective is SiteObjective so && Hostile?.Invoke(so.Site, Team) == true;
        if (_bowWaiting) { MarchOrder = March.BoundingOverwatch; return; }
        if (still) { MarchOrder = March.Herringbone; return; }
        if (hostile && d < 600f && Teams) MarchOrder = March.BoundingOverwatch;
        else if (hostile && d < 1000f && Teams) MarchOrder = March.TravellingOverwatch;
        else MarchOrder = lead.Brain.Env is EnvKind.Urban or EnvKind.Interior or EnvKind.Forest ? March.Column : March.Travelling;
    }

    /// <summary>
    /// Bounding overwatch, the leader's side: after ~50 m ahead of the covering team he stops
    /// and calls them up; once they're up (or 30 s), off again.
    /// </summary>
    public bool LeaderShouldWait(Bot lead)
    {
        if (MarchOrder != March.BoundingOverwatch) { _bowWaiting = false; return false; }
        var cover = Members.Where(m => m.Alive && TeamOf(m) == 1).Select(m => m.FeetPos).ToList();
        if (cover.Count == 0) { _bowWaiting = false; return false; }
        var c = cover.Aggregate(Vector3.Zero, (a, b) => a + b) / cover.Count;
        float gap = ((c - lead.FeetPos) with { Y = 0f }).Length();
        if (_bowWaiting)
        {
            if (gap < 15f || Clock.Now - _bowWaitSince > 30.0) { _bowWaiting = false; return false; }
            return true;
        }
        if (gap > 55f)
        {
            _bowWaiting = true;
            _bowWaitSince = Clock.Now;
            Comms.Say(lead, "Alpha set. Bravo, bound up!");
            return true;
        }
        return false;
    }

    /// <summary>
    /// A member's place by march order and assault phase (null: fall back to the ground-shaped
    /// travelling formation). <paramref name="i"/> is his index among the living, bar the leader.
    /// </summary>
    Vector3? OrderSlot(ICombatant b, ICombatant lead, int i, Vector3 fwd, Vector3 right)
    {
        if (CrossSlot(b) is Vector3 cs) return cs;
        int t = TeamOf(b);
        int alive = Members.Count(m => m.Alive) - 1;
        float side = i % 2 == 0 ? -1f : 1f;
        if (Phase is AssaultPhase.Deploy or AssaultPhase.Assault && t == SbfTeam)
        {
            // Support by fire: spread along a line facing the objective.
            var toObj = Objective != null ? ((Objective.Center - SbfAt) with { Y = 0f }).Normalized() : fwd;
            return SbfAt + toObj.Cross(Vector3.Up) * (side * ((i / 2) + 1) * 4f);
        }
        if (Phase == AssaultPhase.Deploy)
        {
            var toObj = Objective != null ? ((Objective.Center - LdAt) with { Y = 0f }).Normalized() : fwd;
            return LdAt + toObj.Cross(Vector3.Up) * (side * ((i / 2) + 1) * 5f);
        }
        switch (MarchOrder)
        {
            case March.Herringbone:
            {
                // At the ORP, a ring round it; at a halt, alternate sides of the route, a few metres off it.
                if (Phase == AssaultPhase.Orp)
                {
                    float a = Mathf.Tau * i / MathF.Max(1, alive);
                    return OrpAt + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * 7f;
                }
                int row = i / 2 + 1;
                return lead.FeetPos - fwd * (row * 4f) + right * (side * 4.5f);
            }
            case March.Column when PlayerMarch == March.Column:
                // In file behind the leader, along his own track.
                return Behind(lead.FeetPos, (i + 1) * 3.5f) ?? lead.FeetPos - fwd * ((i + 1) * 3.5f);
            case March.AssaultLine:
            {
                // Abreast of the leader, 5 m apart, a step behind so he leads.
                int k = i / 2 + 1;
                return lead.FeetPos + right * (side * k * 5f) - fwd * 1.5f;
            }
            case March.TravellingOverwatch when t == 1:
            {
                // The trail team: ~50 m back along the leader's own route.
                int k = Members.Where(m => m.Alive && TeamOf(m) == 1).ToList().IndexOf(b);
                return Behind(lead.FeetPos, 50f + k * 4f) ?? lead.FeetPos - fwd * (50f + k * 4f);
            }
            case March.BoundingOverwatch when t == 1:
            {
                // Covering: stay set while the leader's team moves; when called, come up behind him.
                if (!_bowWaiting) return b.FeetPos;
                int k = Members.Where(m => m.Alive && TeamOf(m) == 1).ToList().IndexOf(b);
                return Behind(lead.FeetPos, 8f + k * 4f) ?? lead.FeetPos - fwd * (8f + k * 4f);
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- the deliberate attack

    public void BeginOrp(Vector3 at, IObjective obj)
    {
        AssaultOn = obj;
        OrpAt = at;
        Orps++;
        SetPhase(AssaultPhase.Orp);
    }

    /// <summary>
    /// Out of the ORP: the support team (the one with the automatic rifle) to a flank with a view
    /// of the objective, 110-180 m out; the assault team with the leader to the line of departure.
    /// </summary>
    public void BeginDeploy(PhysicsDirectSpaceState3D space, IObjective obj, RandomNumberGenerator rng)
    {
        var map = Valley.Current;
        var axis = ((obj.Center - OrpAt) with { Y = 0f });
        if (axis.LengthSquared() < 1f) axis = Vector3.Forward;
        axis = axis.Normalized();
        SbfTeam = Members.Any(m => m.Alive && m.Role == Role.AutoRifleman && TeamOf(m) == 1) ? 1 : 0;
        Vector3 best = obj.Center - axis * 140f;
        float bestScore = float.MinValue;
        float flank = rng.Randf() < 0.5f ? -1f : 1f;
        for (int k = 0; k < 24; k++)
        {
            float ang = flank * Mathf.DegToRad(rng.RandfRange(30f, 70f)) * (k % 4 == 3 ? -1f : 1f);
            float r = rng.RandfRange(110f, 180f);
            var p = obj.Center - axis.Rotated(Vector3.Up, ang) * r;
            if (map != null) p.Y = map.HeightAt(p.X, p.Z);
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 1.4f, obj.Center + Vector3.Up * 2f, Layers.World));
            bool los = hit.Count == 0 || hit["position"].AsVector3().DistanceTo(obj.Center) < 30f;
            float score = (los ? 30f : 0f) + (p.Y - obj.Center.Y) * 0.4f + rng.RandfRange(0f, 4f);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        SbfAt = map?.Ground(best) ?? best;
        float ld = MathF.Min(120f, ((obj.Center - OrpAt) with { Y = 0f }).Length() - 20f);
        LdAt = obj.Center - axis * ld;
        if (map != null) LdAt = map.Ground(LdAt);
        Deploys++;
        SetPhase(AssaultPhase.Deploy);
    }

    public bool SupportSet =>
        Members.Where(m => m.Alive && TeamOf(m) == SbfTeam).Count(m => m.FeetPos.DistanceTo(SbfAt) < 18f)
        >= MathF.Max(1f, Members.Count(m => m.Alive && TeamOf(m) == SbfTeam) * 0.6f);

    public void BeginAssault()
    {
        Assaults++;
        SetPhase(AssaultPhase.Assault);
    }

    public void EndAssault() => SetPhase(AssaultPhase.None);

    /// <summary>Support-by-fire, firing on the objective: this man is in the support team during the assault.</summary>
    public bool FiringInSupport(ICombatant c) => Phase == AssaultPhase.Assault && TeamOf(c) == SbfTeam;

    // ---------------------------------------------------------------- buddy pairs

    readonly Dictionary<ICombatant, ICombatant> _buddy = new();
    readonly Dictionary<ICombatant, (ICombatant Mover, double SwapAt)> _pair = new();

    /// <summary>Pairs stay pairs while both are in the same team; whoever's left without one is paired with the next.</summary>
    void PairUp()
    {
        foreach (var (a, b) in _buddy.ToList())
            if (!_team.TryGetValue(a, out var ta) || !_team.TryGetValue(b, out var tb) || ta != tb) { _buddy.Remove(a); _buddy.Remove(b); }
        for (int t = 0; t < 2; t++)
        {
            var loose = Members.Where(m => _team.TryGetValue(m, out var mt) && mt == t && !_buddy.ContainsKey(m)).ToList();
            for (int k = 0; k + 1 < loose.Count; k += 2)
            {
                _buddy[loose[k]] = loose[k + 1];
                _buddy[loose[k + 1]] = loose[k];
            }
        }
    }

    public ICombatant? BuddyOf(ICombatant c) => _buddy.TryGetValue(c, out var o) && o.Alive && GodotObject.IsInstanceValid((GodotObject)o) ? o : null;

    /// <summary>Up close, is it this man's turn to move (while his buddy fires)? They swap every couple of seconds.</summary>
    public bool IsMover(ICombatant c, double now, RandomNumberGenerator rng)
    {
        var o = BuddyOf(c);
        if (o == null) return true;
        var key = c.GetHashCode() < o.GetHashCode() ? c : o;
        if (!_pair.TryGetValue(key, out var p) || now > p.SwapAt || !p.Mover.Alive)
        {
            var mover = _pair.TryGetValue(key, out var old) && old.Mover == c ? o : c;
            p = (mover, now + rng.RandfRange(1.8f, 3.2f));
            _pair[key] = p;
            BuddySwaps++;
        }
        return p.Mover == c;
    }
}
