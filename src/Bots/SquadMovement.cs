using Godot;

namespace Ridgeline;

/// <summary>How a squad moves when it isn't fighting: chosen by the squad leader from the situation.</summary>
public enum March { Travelling, Column, TravellingOverwatch, BoundingOverwatch, Herringbone, AssaultLine, DangerCrossing }

/// <summary>The steps of a deliberate attack on an enemy-held point.</summary>
public enum AssaultPhase { None, Orp, Deploy, Assault }

/// <summary>The leader's reconnaissance from the ORP, at doctrine's pace (Squad.Deliberate).</summary>
public enum ReconStep { None, Out, Watching, Back, Orders }

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
    public static int Orps, Deploys, BuddySwaps, Recons;

    /// <summary>
    /// The deliberate attack at doctrine's pace, as the Conquest window fights it (FM 3-21.8 ch. 7; FM 3-21.71 for the
    /// mechanised platoon). An attack on a prepared position takes its time, and the time goes on things soldiers do:
    /// - bounding overwatch ends each bound with a halt to look and listen before the next (20-40 s);
    /// - at the ORP the leader goes forward with one man to look at the objective from where his support will fire,
    ///   watches it for three minutes (men in fighting positions show themselves for seconds at a time), comes back
    ///   and gives his orders; a planned preparation is fired then, and nobody moves forward under it;
    /// - deployed, the support opens fire once it's set and the assault team is at the line of departure, and the
    ///   assault goes when the support has fire superiority: two minutes of its fire and the squad no longer under
    ///   fire from the objective (at most six minutes).
    /// This species doesn't lie pinned for fear, so none of it is waiting for nerve. The battle maps keep their compressed
    /// attack (a squad takes a point in a few minutes): a 30-60 minute match is a game of many attacks. (In the window
    /// the compressed attack carried a dug-in platoon's line in 7-9 minutes, start line to last man: 8 s at the ORP,
    /// a minute deploying.)
    /// </summary>
    public static bool Deliberate;

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
    double _bowLookUntil = -1;

    /// <summary>The leader's recon (Deliberate): where it's got to, who went, and when the step is over.</summary>
    public ReconStep Recon { get; private set; }
    public ICombatant? ReconBy, ReconMate;
    /// <summary>Where the leader looks at the objective from (ChooseReconPost).</summary>
    public Vector3 ReconAt;
    public double ReconUntil;
    double _reconSince, _supportOpenAt = -1, _quietSince = -1;
    /// <summary>
    /// The side's own artillery about to fall or falling within its safe distance of a point: until when (Clock time), or
    /// below zero for none. A planned preparation on the point not fired yet is called by the asking (the leader back
    /// from his recon is ready for it). Set by the match that has guns off the map (the Conquest window).
    /// </summary>
    public static Func<int, Vector3, double>? OwnFiresUntil;

    /// <summary>The support team is firing on the objective: in the assault, or once it's opened up while deploying.</summary>
    public bool SupportOpen => Phase == AssaultPhase.Assault || Phase == AssaultPhase.Deploy && _supportOpenAt >= 0;

    /// <summary>How far off a human in the squad can be and still be a straggler catching up, not a man who's gone his own way.</summary>
    const float StragglerRange = 150f;

    /// <summary>
    /// On the move, strung out behind the leader: anyone in a fight, or half the squad, 60 m and more from him. He
    /// waits for them to close up, rather than walk on and leave them (a squad leader pushing on to the objective
    /// while his men fought 150 m back was much of the time a squad's men spent on their own). Not in an assault
    /// or a crossing, where the teams are apart on purpose; and no more than 40 s at a time, so someone stuck
    /// can't hold the squad forever.
    /// A human in the squad is no bot to be counted in its strength or waited for: a squad expects a straggler to
    /// catch up, and a man who's gone his own way (beyond earshot of a "close up", ~150 m) is left to it. One who's
    /// only a little behind gets the call and 12 s, then the squad moves on and doesn't wait for him again for a
    /// minute and a half. (The player counted as a bot: in a squad down to two or three, a player who lagged or
    /// wandered off was half of it, and the leader held the squad for 40 s at a time, over and over.)
    /// </summary>
    public bool StrungOut(Bot lead)
    {
        double now = Clock.Now;
        if (now < _closeUpAgain || Phase != AssaultPhase.None || Cross != Crossing.None || FollowPlayer || Defend) { _closeUpSince = -1; return false; }
        int n = 0, far = 0;
        bool fighting = false, playerBehind = false;
        foreach (var m in Members)
        {
            if (m == lead || !m.Alive || m.Ride != null) continue;
            if (m is Player)
            {
                float pd2 = ((m.FeetPos - lead.FeetPos) with { Y = 0f }).LengthSquared();
                // Behind him: further from where the squad is going than the leader is (a man ahead needs no waiting for).
                bool behind = Objective is not { } ob || ((m.FeetPos - ob.Center) with { Y = 0f }).LengthSquared() > ((lead.FeetPos - ob.Center) with { Y = 0f }).LengthSquared();
                if (behind && pd2 >= 60f * 60f && pd2 < StragglerRange * StragglerRange) playerBehind = true;
                continue;
            }
            n++;
            if (((m.FeetPos - lead.FeetPos) with { Y = 0f }).LengthSquared() < 60f * 60f) continue;
            far++;
            if (m is Bot b && b.Brain.State is BotState.Engage or BotState.InCover or BotState.Hold or BotState.TakeCover or BotState.Flank) fighting = true;
        }
        bool botsStrung = n > 0 && (fighting || far * 2 >= n);
        if (!botsStrung && !playerBehind) { _closeUpSince = -1; return false; }
        if (_closeUpSince < 0)
        {
            _closeUpSince = now;
            CloseUps++;
            Prof.Count(fighting ? "squad:close-up (contact behind)" : botsStrung ? "squad:close-up" : "squad:close-up (player behind)");
            Comms.Say(lead, fighting ? "Hold up, they're in contact back there. On me!" : "Close it up! On me!");
        }
        else if (now - _closeUpSince > (botsStrung ? 40.0 : 12.0))
        {
            _closeUpSince = -1;
            _closeUpAgain = now + (botsStrung ? 40.0 : 90.0);
            return false;
        }
        return true;
    }

    public double PhaseAge => Clock.Now - _phaseSince;
    /// <summary>Real seconds in this phase (PhaseAge stands still while the squad is in contact).</summary>
    double PhaseReal => Clock.Now - _phaseReal;
    double _phaseReal;
    static readonly RandomNumberGenerator PlanRng = new();

    int _phaseAlive, _attackAlive;
    double _planTickAt, _frozenFor;

    void SetPhase(AssaultPhase p, string why = "")
    {
        if (DuelMode.Verbose)
        {
            // What the phase that's over came to: how long, who's left, whether it was in a fight, how far the leader is from the objective.
            string was = Phase != AssaultPhase.None ? $" (was {Phase} {PhaseAge:0} s, {_phaseAlive}->{Alive} alive{(Engaged ? ", engaged" : "")}, leader {(Leader != null && Objective != null ? ((Objective.Center - Leader.FeetPos) with { Y = 0f }).Length() : -1):0} m out{(why != "" ? ", " + why : "")})" : "";
            GD.Print($"[{Clock.Now:0}s] {Name} assault phase {p}" + (Objective is SiteObjective so ? $" on {so.Site.Name}" : "") + was);
        }
        Phase = p;
        _phaseSince = _phaseReal = Clock.Now;
        _phaseAlive = Alive;
        _frozenFor = 0.0;
        Recon = ReconStep.None;
        ReconBy = ReconMate = null;
        _supportOpenAt = _quietSince = -1;
    }

    // ---------------------------------------------------------------- the leader's recon (Deliberate)

    /// <summary>
    /// Formed up at the ORP: the plan off the map first (where support goes, the line of departure: PlanAttack), then the
    /// leader goes forward with his buddy to the support position, which was picked for its view of the objective. The
    /// rest hold the ORP. (FM 3-21.8, actions at the ORP: the leader's reconnaissance confirms the plan before the squad
    /// moves on it.)
    /// </summary>
    public void BeginRecon(Bot lead, PhysicsDirectSpaceState3D space, RandomNumberGenerator rng)
    {
        PlanAttack(space, Objective!, rng);
        ReconAt = ChooseReconPost(space, Objective!, rng);
        Recons++;
        ReconBy = lead;
        ReconMate = BuddyOf(lead) is Bot bud && bud.Ride == null ? bud
            : Members.OfType<Bot>().Where(m => m != lead && m.Alive && m.Ride == null).OrderBy(m => m.FeetPos.DistanceSquaredTo(lead.FeetPos)).FirstOrDefault();
        StepRecon(ReconStep.Out, 0.0);
    }

    /// <summary>On to the next step of the recon; <paramref name="lasts"/> s is how long it's to take (0: till it's done).</summary>
    public void StepRecon(ReconStep step, double lasts)
    {
        if (DuelMode.Verbose && step != Recon)
            GD.Print($"[{Clock.Now:0}s] {Name} recon {step}" + (Recon != ReconStep.None ? $" (was {Recon} {Clock.Now - _reconSince:0} s)" : "")
                     + (step == ReconStep.Out ? $": {ReconBy?.Callsign} and {ReconMate?.Callsign ?? "nobody"} to {Flat(ReconAt, Objective?.Center ?? ReconAt):0} m from it" : ""));
        Recon = step;
        _reconSince = Clock.Now;
        ReconUntil = Clock.Now + lasts;
    }

    public double ReconAge => Clock.Now - _reconSince;

    /// <summary>
    /// Where the leader looks from on his recon: somewhere 170-320 m out on the ORP's side that sees some of the
    /// objective from a knee (a sixth or more), with something in front to be behind, the nearest the ORP of the best;
    /// failing one, the support position. A recon is made from concealment, back from the objective, not from where the support will
    /// fire. (He went to the support position itself, picked to shoot from at 100-170 m, and walked up to it upright: in
    /// the first runs a defender saw him and fired, and the squad deployed off the back of it, nearly every time. Looking
    /// for a quarter of the objective in view from 190-300 m, six leaders in eight found nowhere at Froltosa's wooded edge.)
    /// </summary>
    Vector3 ChooseReconPost(PhysicsDirectSpaceState3D space, IObjective obj, RandomNumberGenerator rng)
    {
        var map = Valley.Current;
        var targets = FightingPositions(space, obj, rng, 6, 8);
        var back = (OrpAt - obj.Center) with { Y = 0f };
        if (back.LengthSquared() < 1f) back = Vector3.Back;
        back = back.Normalized();
        var cands = new List<(Vector3 P, float Score)>();
        for (int k = 0; k < 60; k++)
        {
            float ang = Mathf.DegToRad(rng.RandfRange(-70f, 70f));
            var p = obj.Center + back.Rotated(Vector3.Up, ang) * rng.RandfRange(170f, 320f);
            float y = map?.HeightAt(p.X, p.Z) ?? obj.Center.Y;
            if (!CoverFinder.Standable(space, p with { Y = y }, y, out var g)) continue;
            float kneel = ShareSeen(space, g, 1.1f, targets);
            if (kneel < 0.16f) continue;
            cands.Add((g, kneel * 10f + (HasCoverToward(space, g, obj.Center) ? 3f : 0f) - Flat(g, OrpAt) * 0.02f));
        }
        cands.Sort((x, y) => y.Score.CompareTo(x.Score));
        for (int k = 0; k < Math.Min(6, cands.Count); k++)
            if (CanWalk(OrpAt, cands[k].P)) return cands[k].P;
        return SbfAt;
    }

    /// <summary>
    /// Deployed (Deliberate): the support opens fire once it's set and the assault team is at the line of departure, and
    /// the assault goes when the support has fire superiority: it has fired two minutes and nobody in the squad has been
    /// under fire for 20 s (the objective's fire has died down), or it has fired six. Run whatever the leader is busy with:
    /// in contact he's fighting, not walking the plan.
    /// </summary>
    void DeliberateDeploy(Bot lead)
    {
        double now = Clock.Now;
        bool atLd = lead.FeetPos.DistanceTo(LdAt) < 10f;
        if (_supportOpenAt < 0)
        {
            if (!((atLd && SupportSet) || (Engaged && SupportFiring()) || PhaseAge > 360.0)) return;
            _supportOpenAt = now;
            if (DuelMode.Verbose) GD.Print($"[{now:0}s] {Name} support opens fire ({(atLd && SupportSet ? "set, assault team at the LD" : Engaged ? "in contact" : "6 minutes")}, {PhaseAge:0} s deployed)");
            Comms.Say(lead, $"{TeamName(SbfTeam)}, open fire!");
        }
        bool underFire = Members.Any(m => m.Alive && m is Bot { Suppression: > 0.25f });
        if (underFire) _quietSince = -1;
        else if (_quietSince < 0) _quietSince = now;
        double firing = now - _supportOpenAt;
        string why = firing > 120.0 && _quietSince >= 0 && now - _quietSince > 20.0 ? $"fire superiority: {firing:0} s of support fire, quiet {now - _quietSince:0} s"
            : firing > 360.0 ? "6 minutes of support fire" : "";
        if (why == "") return;
        BeginAssault(why);
        Comms.Say(lead, $"{TeamName(SbfTeam ^ 1)}, on line — assault, go!");
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

        AdaptToContact();
        // Time limits that hold even while the leader is busy fighting: an attack that's gone stale
        // is called off (or, once the teams are deployed, launched). At doctrine's pace the ORP has the leader's recon in it
        // (about ten minutes), and deploying has the support's fight for fire superiority.
        if (Phase == AssaultPhase.Orp && PhaseAge > (Deliberate && Recon != ReconStep.None ? 1200.0 : 60.0)) EndAssault("ORP timed out");
        else if (Phase == AssaultPhase.Deploy && PhaseAge > (Deliberate ? 900.0 : 120.0)) BeginAssault("deploy timed out");
        else if (Phase == AssaultPhase.Deploy && Deliberate) DeliberateDeploy(lead);
        // In contact while deploying, the leader is fighting rather than walking the plan (the plan only runs when he's on the
        // move), so the call to go can't wait on him reaching the line of departure. The support's rounds are already on the
        // objective from where they are, and fire is what the assault goes in under. (It used to wait for the clock, or for a
        // lull, and a squad pinned short of the objective sat there while the defenders picked it apart.)
        else if (Phase == AssaultPhase.Deploy && Engaged && PhaseReal > 20.0 && SupportFiring() && !Deliberate) BeginAssault("contact: support already firing on it");
        else if (Phase == AssaultPhase.Assault && PhaseAge > 100.0) EndAssault("assault timed out");
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
    /// Contact during the attack is normal, and the leader decides what it means (FM 3-21.8, react to contact; a
    /// deliberate attack that meets the enemy short of the objective doesn't stop being an attack):
    /// - at the ORP, contact means no forming up: on to the deployment at once (DeployNow). Only the enemy right on the
    ///   ORP (within 60 m), or a man lost, means it's compromised: fight it as a hasty attack (fix and flank, see
    ///   ReactToContact), and once it's quiet the leader may form up again;
    /// - deploying, fire from the objective is what support is for and the assault team is going anyway: once the support
    ///   is firing it goes in under it (UpdateMarch); only fire from somewhere else (a different enemy, off the axis and
    ///   within 80 m), or a third of the squad lost, ends it;
    /// - in the assault, only heavy losses (under half left, or fewer than three) stop it.
    /// While in contact the clocks on forming up and deploying stand still (up to a minute), so a fight doesn't run
    /// them out. (It used to be called off, or launched with support unset, by the clocks alone while everyone was
    /// busy shooting, and the contact drill sent the assault team off flanking in the middle of it.)
    /// </summary>
    void AdaptToContact()
    {
        double now = Clock.Now;
        float dt = (float)Math.Clamp(now - _planTickAt, 0.0, 1.0);
        _planTickAt = now;
        if (Phase == AssaultPhase.None || Objective == null || !Engaged) return;
        var lead = Leader;
        if (lead == null) return;
        switch (Phase)
        {
            case AssaultPhase.Orp:
                // The objective's own fire reaches the ORP: it isn't out of its sight and sound, and nobody forms up in a
                // fight. The squad goes on to the next step now: support to its place, the assault team to the line of
                // departure, and the assault goes in under fire (see UpdateMarch). It stays an attack on the objective
                // rather than a hasty drill at wherever the shots came from.
                if (Flat(ContactAt, Objective.Center) < Objective.Radius + 150f && lead is Bot lb)
                {
                    DeployNow(lb);
                    return;
                }
                if (Alive < _attackAlive) { EndAssault("ORP compromised, a man lost: hasty attack", true); return; }
                // The enemy right on the ORP (inside grenade-throwing and assault distance) compromises it: fight him. Contact
                // further off is the leader's call, and he pushes on: no forming up under it, straight to the deployment, the
                // men in contact fighting it from where they are. (Anything within 120 m ended the plan: with three sides
                // moving about, five ORPs in six were broken up that way within seconds, and no assault was ever deployed.)
                if (Flat(ContactAt, OrpAt) < 60f) { EndAssault("contact on the ORP: hasty attack", true); return; }
                if (lead is Bot lp) { DeployNow(lp); return; }
                break;
            case AssaultPhase.Deploy:
            {
                var toObj = ((Objective.Center - lead.FeetPos) with { Y = 0f });
                var toThem = ((ContactAt - lead.FeetPos) with { Y = 0f });
                // A different enemy, off the axis and close on the flank (80 m: assault distance), stops the deployment for a hasty
                // attack on him; further off, the men facing him fight him from where they are and the attack goes on. (At 150 m
                // the same distant contact that had just moved the squad on from its ORP called the deployment off a few seconds
                // later, two times in five.)
                bool elsewhere = Flat(ContactAt, Objective.Center) > Objective.Radius + 150f && toObj.LengthSquared() > 1f && toThem.LengthSquared() > 1f
                                 && toObj.AngleTo(toThem) > Mathf.DegToRad(70f) && toThem.Length() < 80f;
                if (elsewhere) { EndAssault("contact from another direction: hasty attack", true); return; }
                if (Alive * 3 < _attackAlive * 2) { EndAssault("a third of the squad lost", true); return; }
                break;
            }
            case AssaultPhase.Assault:
                if (Alive < 3 || Alive * 2 < _attackAlive) EndAssault("heavy losses");
                return;
        }
        if (_frozenFor < 60.0) { _phaseSince += dt; _frozenFor += dt; }
    }

    /// <summary>
    /// Bounding overwatch, the leader's side: after ~50 m ahead of the covering team he stops
    /// and calls them up; once they're up (or 30 s), off again.
    /// </summary>
    public bool LeaderShouldWait(Bot lead)
    {
        if (MarchOrder != March.BoundingOverwatch) { _bowWaiting = false; return false; }
        // (Not the player: he's no part of a bound, and wandering off would hold the leader half a minute at a time.)
        var cover = Members.Where(m => m.Alive && m is not Player && TeamOf(m) == 1).Select(m => m.FeetPos).ToList();
        if (cover.Count == 0) { _bowWaiting = false; return false; }
        var c = cover.Aggregate(Vector3.Zero, (a, b) => a + b) / cover.Count;
        float gap = ((c - lead.FeetPos) with { Y = 0f }).Length();
        if (_bowWaiting)
        {
            if (gap < 15f || Clock.Now - _bowWaitSince > 30.0)
            {
                // At doctrine's pace the bound ends in a halt: both teams look and listen before the next (Deliberate).
                if (Deliberate && _bowLookUntil < 0) _bowLookUntil = Clock.Now + PlanRng.RandfRange(20f, 40f);
                if (Deliberate && Clock.Now < _bowLookUntil) return true;
                _bowWaiting = false;
                _bowLookUntil = -1;
                return false;
            }
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
        // The leader's recon: his buddy goes with him, a few metres back and to the side.
        if (Phase == AssaultPhase.Orp && Recon is ReconStep.Out or ReconStep.Watching or ReconStep.Back && b == ReconMate && lead == ReconBy)
            return lead.FeetPos - fwd * 3f + right * 2.5f;
        int t = TeamOf(b);
        int alive = Members.Count(m => m.Alive) - 1;
        float side = i % 2 == 0 ? -1f : 1f;
        if (Phase is AssaultPhase.Deploy or AssaultPhase.Assault && t == SbfTeam) return SbfSlot(b);
        if (Phase == AssaultPhase.Deploy) return LdSlot(b);
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
        _attackAlive = Alive;
    }

    /// <summary>
    /// Where to form up for the attack: out of sight of the objective (FM 3-21.8: an ORP is out of sight and sound of
    /// it), so the enemy doesn't see the squad stop and take a look. If the leader's own spot sees the objective, the
    /// nearest place within 70 m that doesn't and is no closer to it, and a real walk; failing that, where he is.
    /// </summary>
    public Vector3 ChooseOrp(PhysicsDirectSpaceState3D space, Vector3 from, IObjective obj, RandomNumberGenerator rng)
    {
        var targets = FightingPositions(space, obj, rng, 6, 6);
        if (ShareSeen(space, from, 1.4f, targets) <= 0.1f) return from;
        var map = Valley.Current;
        float d0 = Flat(from, obj.Center);
        var cands = new List<Vector3>();
        for (int k = 0; k < 28; k++)
        {
            float a = rng.Randf() * Mathf.Tau, r = rng.RandfRange(8f, 70f);
            var p = from + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            float d = Flat(p, obj.Center);
            if (d < d0 - 5f || d > 420f) continue;
            float y = map?.HeightAt(p.X, p.Z) ?? from.Y;
            if (!CoverFinder.Standable(space, p with { Y = y }, y, out var g)) continue;
            if (ShareSeen(space, g, 1.4f, targets) > 0.1f) continue;
            cands.Add(g);
        }
        foreach (var c in cands.OrderBy(c => Flat(c, from)).Take(2))
            if (CanWalk(from, c)) return c;
        return from;
    }

    // ---- what a position can see: the ground and the buildings decide, not the map

    const uint Sight = Layers.World | Layers.Trees;

    /// <summary>A clear line from an eye to a point (the last 1.5 m may be the window frame or the wall it's in).</summary>
    static bool CanSee(PhysicsDirectSpaceState3D space, Vector3 eye, Vector3 at)
    {
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, at, Sight));
        return hit.Count == 0 || hit["position"].AsVector3().DistanceTo(at) < 1.5f;
    }

    /// <summary>The share of these points a man standing at <paramref name="feet"/> with his eye <paramref name="eyeH"/> up can see.</summary>
    static float ShareSeen(PhysicsDirectSpaceState3D space, Vector3 feet, float eyeH, List<Vector3> at)
    {
        if (at.Count == 0) return 0f;
        var eye = feet + Vector3.Up * eyeH;
        int n = 0;
        foreach (var t in at) if (CanSee(space, eye, t)) n++;
        return n / (float)at.Count;
    }

    /// <summary>
    /// Where the enemy will be on the objective, as far as anything outside can tell: its windows and rooftops, and
    /// open ground you could stand on within it (chest height). A point inside a building isn't one: the walls hide
    /// it from everywhere, and support fire goes at the building.
    /// </summary>
    static List<Vector3> FightingPositions(PhysicsDirectSpaceState3D space, IObjective obj, RandomNumberGenerator rng, int windows, int ground)
    {
        var ts = new List<Vector3>();
        if (obj is SiteObjective so && so.Site.Perches.Count > 0)
            for (int k = 0; k < windows; k++) ts.Add(so.Site.Perches[rng.RandiRange(0, so.Site.Perches.Count - 1)].Pos + Vector3.Up * 1.0f);
        var map = Valley.Current;
        int got = 0;
        for (int k = 0; k < ground * 3 && got < ground; k++)
        {
            float a = rng.Randf() * Mathf.Tau, r = MathF.Sqrt(rng.Randf()) * obj.Radius * 0.8f;
            var q = obj.Center + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            float y = map?.HeightAt(q.X, q.Z) ?? obj.Center.Y;
            if (!CoverFinder.Standable(space, q with { Y = y }, y, out var g)) continue;
            ts.Add(g + Vector3.Up * 1.2f);
            got++;
        }
        if (ts.Count == 0) ts.Add(obj.Center + Vector3.Up * 1.5f);
        return ts;
    }

    /// <summary>Is there something in front of him, on the way to the objective, to get down behind (a wall, a bank, a rock)?</summary>
    static bool HasCoverToward(PhysicsDirectSpaceState3D space, Vector3 feet, Vector3 toward)
    {
        var dir = ((toward - feet) with { Y = 0f }).Normalized();
        var from = feet + Vector3.Up * 0.5f;
        return space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + dir * 8f, Sight)).Count > 0;
    }

    /// <summary>A walk from one point to another that's really there and not a huge detour (the navmesh is what people walk on).</summary>
    static bool CanWalk(Vector3 from, Vector3 to)
    {
        if (Valley.Current is not { } v) return true;
        var path = NavBaker.Path(v.Nav.Map, from, to);
        if (path.Length == 0 || path[^1].DistanceTo(to) > 2.5f) return false;
        float len = 0f;
        for (int k = 1; k < path.Length; k++) len += path[k].DistanceTo(path[k - 1]);
        return len <= ((to - from) with { Y = 0f }).Length() * 1.8f + 40f;
    }

    static float Flat(Vector3 a, Vector3 b) => ((a - b) with { Y = 0f }).Length();

    /// <summary>
    /// The angle between two bearings from the objective, in degrees: how far apart the support and the assault come at it
    /// (doctrine keeps them at least ~30 degrees apart so the support's fire doesn't cross the assault's line).
    /// </summary>
    static float Separation(Vector3 centre, Vector3 a, Vector3 b) =>
        Mathf.RadToDeg(((a - centre) with { Y = 0f }).AngleTo((b - centre) with { Y = 0f }));

    /// <summary>
    /// Out of the ORP: the support team (the one with the automatic rifle) to a support-by-fire position, the assault
    /// team with the leader to the line of departure.
    /// Support by fire has to see the objective and reach it with fire, so its position is chosen on the ground:
    /// spots sampled 90-200 m out round the near side of the objective, and windows and rooftops of buildings in that
    /// ring facing it, each scored by the share of the objective's fighting positions it can really see (raycast from
    /// a standing and a kneeling eye, against the world and the trees), by range (100-170 m: near enough to hurt, far
    /// enough that the defenders' rifles are at their worst), by the angle it makes with the assault (at least ~30
    /// degrees, FM 3-21.8; not so wide the fire crosses the assault), a bank or wall to get down behind, and the
    /// height over the objective; the best few must be a real walk from the ORP. The line of departure is the last
    /// covered ground on the way in: reachable, hidden from the objective if it can be, ~100-140 m out and not on the
    /// support's line of fire. (It used to be a random point 110-180 m out that had a ray to the objective that
    /// ended within 30 m of it, which any wall at the edge of a town satisfied: most support positions saw next
    /// to nothing of the objective, and some were not somewhere a man could walk to.)
    /// </summary>
    public void BeginDeploy(PhysicsDirectSpaceState3D space, IObjective obj, RandomNumberGenerator rng, bool planned = false)
    {
        if (!planned) PlanAttack(space, obj, rng);
        Deploys++;
        SetPhase(AssaultPhase.Deploy);
        if (DuelMode.Verbose) Audit(space, obj);
    }

    /// <summary>Where support goes and the line of departure (see BeginDeploy), without moving off yet.</summary>
    public void PlanAttack(PhysicsDirectSpaceState3D space, IObjective obj, RandomNumberGenerator rng)
    {
        var map = Valley.Current;
        var axis = ((obj.Center - OrpAt) with { Y = 0f });
        if (axis.LengthSquared() < 1f) axis = Vector3.Forward;
        axis = axis.Normalized();
        var back = -axis; // from the objective toward the ORP
        SbfTeam = Members.Any(m => m.Alive && m.Role == Role.AutoRifleman && TeamOf(m) == 1) ? 1 : 0;
        var targets = FightingPositions(space, obj, rng, 8, 8);

        // ---- support by fire
        var cands = new List<(Vector3 P, float Score)>();
        void Consider(Vector3 p, bool window)
        {
            float los = ShareSeen(space, p, 1.4f, targets);
            if (los <= 0.05f) return;
            float kneel = ShareSeen(space, p, 0.9f, targets);
            float d = Flat(p, obj.Center);
            float sep = Separation(obj.Center, p, obj.Center + back);
            float score = (0.6f * los + 0.4f * kneel) * 40f
                - MathF.Max(0f, d - 170f) * 0.08f - MathF.Max(0f, 100f - d) * 0.2f
                - MathF.Max(0f, 30f - sep) * 0.7f - MathF.Max(0f, sep - 95f) * 0.5f
                + Mathf.Clamp((p.Y - obj.Center.Y) * 0.15f, -2f, 3f)
                + (HasCoverToward(space, p, obj.Center) ? 3f : 0f)
                + (window ? 2f : 0f) + rng.RandfRange(0f, 2f);
            cands.Add((p, score));
        }
        for (int k = 0; k < 36; k++)
        {
            float ang = Mathf.DegToRad(rng.RandfRange(25f, 105f)) * (rng.Randf() < 0.5f ? -1f : 1f);
            var p = obj.Center + back.Rotated(Vector3.Up, ang) * rng.RandfRange(90f, 200f);
            float y = map?.HeightAt(p.X, p.Z) ?? obj.Center.Y;
            if (CoverFinder.Standable(space, p with { Y = y }, y, out var g)) Consider(g, false);
        }
        if (map != null)
        {
            // Windows and rooftops facing the objective, in the ring (not in the objective itself).
            var near = new List<Perch>();
            foreach (var pe in map.Perches)
            {
                float d = Flat(pe.Pos, obj.Center);
                if (d < obj.Radius + 10f || d > 200f) continue;
                var toObj = ((obj.Center - pe.Pos) with { Y = 0f }).Normalized();
                if (pe.Out.LengthSquared() > 0.01f && pe.Out.Normalized().Dot(toObj) < 0.3f) continue;
                if (Separation(obj.Center, pe.Pos, obj.Center + back) > 110f) continue;
                near.Add(pe);
            }
            for (int k = 0; k < 10 && near.Count > 0; k++) Consider(near[rng.RandiRange(0, near.Count - 1)].Pos, true);
        }
        cands.Sort((x, y) => y.Score.CompareTo(x.Score));
        Vector3? sbf = null;
        // The best of those a man can walk to (a rooftop or an upper window often has no way up on the navmesh); none of the
        // dozen best is reachable, the old guess below rather than a place nobody can get to.
        for (int k = 0; k < Math.Min(12, cands.Count) && sbf == null; k++)
            if (CanWalk(OrpAt, cands[k].P)) sbf = cands[k].P;
        if (sbf is Vector3 sp) SbfAt = sp;
        else
        {
            // Nowhere with a view of it: the old guess, out to a flank.
            float ang = Mathf.DegToRad(50f) * (rng.Randf() < 0.5f ? -1f : 1f);
            var p = obj.Center + back.Rotated(Vector3.Up, ang) * 140f;
            SbfAt = map?.Ground(p) ?? p;
        }
        SbfSlots = Spread(space, SbfAt, rng, 8, targets, true, obj);

        // ---- line of departure
        float axisLen = Flat(obj.Center, OrpAt);
        float ldMax = MathF.Min(140f, axisLen - 20f), ldMin = MathF.Min(90f, ldMax * 0.75f);
        var lds = new List<(Vector3 P, float Score)>();
        for (int k = 0; k < 24; k++)
        {
            float ang = Mathf.DegToRad(rng.RandfRange(-30f, 30f));
            float r = rng.RandfRange(ldMin, MathF.Max(ldMin, ldMax));
            var p = obj.Center + back.Rotated(Vector3.Up, ang) * r;
            float y = map?.HeightAt(p.X, p.Z) ?? obj.Center.Y;
            if (!CoverFinder.Standable(space, p with { Y = y }, y, out var g)) continue;
            float seenStanding = ShareSeen(space, g, 1.4f, targets), seenCrouched = ShareSeen(space, g, 0.9f, targets);
            float score = -seenStanding * 12f - seenCrouched * 8f
                - MathF.Abs(r - 115f) * 0.08f - MathF.Abs(Mathf.RadToDeg(ang)) * 0.05f
                - MathF.Max(0f, 30f - Separation(obj.Center, g, SbfAt)) * 0.5f
                + (HasCoverToward(space, g, obj.Center) ? 2f : 0f) + rng.RandfRange(0f, 2f);
            lds.Add((g, score));
        }
        lds.Sort((x, y) => y.Score.CompareTo(x.Score));
        Vector3? ld = null;
        for (int k = 0; k < Math.Min(4, lds.Count) && ld == null; k++)
            if (CanWalk(OrpAt, lds[k].P)) ld = lds[k].P;
        var plain = obj.Center - axis * MathF.Min(120f, axisLen - 20f);
        LdAt = ld ?? (lds.Count > 0 ? lds[0].P : map?.Ground(plain) ?? plain);
        LdSlots = Spread(space, LdAt, rng, 8, null, false, obj);
    }

    /// <summary>Where each of a team goes at a point: the point and standable ground round it (3 m or more apart), with a view of the objective for the support.</summary>
    static Vector3[] Spread(PhysicsDirectSpaceState3D space, Vector3 at, RandomNumberGenerator rng, int n, List<Vector3>? mustSee, bool nearWindows, IObjective obj)
    {
        var slots = new List<Vector3> { at };
        var map = Valley.Current;
        var pool = new List<Vector3>();
        if (nearWindows && map != null)
            foreach (var pe in map.Perches)
                if (Flat(pe.Pos, at) < 25f && Flat(pe.Pos, obj.Center) > obj.Radius + 10f) pool.Add(pe.Pos);
        for (int k = 0; k < 24; k++)
        {
            float a = rng.Randf() * Mathf.Tau, r = rng.RandfRange(3f, 14f);
            var p = at + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            float y = map?.HeightAt(p.X, p.Z) ?? at.Y;
            if (CoverFinder.Standable(space, p with { Y = y }, y, out var g)) pool.Add(g);
        }
        foreach (var c in pool)
        {
            if (slots.Count >= n) break;
            if (slots.Any(q => Flat(q, c) < 3f)) continue;
            if (mustSee != null && ShareSeen(space, c, 1.2f, mustSee) <= 0.05f) continue;
            slots.Add(c);
        }
        return slots.ToArray();
    }

    /// <summary>Support's places, one to a man; the assault team's at the line of departure.</summary>
    public Vector3[] SbfSlots = Array.Empty<Vector3>(), LdSlots = Array.Empty<Vector3>();

    /// <summary>
    /// Verbose only: how good the plan's points are on the ground. The share of the objective's outside fighting
    /// positions (windows and rooftops, and open ground round it, chest height) each point can see by a ray against
    /// the world and the trees; the ORP should see none of them; and whether the walk from the ORP to the SBF and
    /// the LD is real.
    /// </summary>
    void Audit(PhysicsDirectSpaceState3D space, IObjective obj)
    {
        var targets = new List<Vector3>();
        if (obj is SiteObjective so)
        {
            var per = so.Site.Perches;
            for (int i = 0; i < per.Count && targets.Count < 8; i += Math.Max(1, per.Count / 8)) targets.Add(per[i].Pos + Vector3.Up * 1.0f);
        }
        int np = targets.Count;
        for (int i = 0; i < 8; i++)
        {
            float a = Mathf.Tau * i / 8f, r = obj.Radius * 0.6f;
            var q = obj.Center + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            targets.Add((Valley.Current?.Ground(q) ?? q) + Vector3.Up * 1.2f);
        }
        targets.Add(obj.Center + Vector3.Up * 1.5f);
        float Sees(Vector3 feet)
        {
            var eye = feet + Vector3.Up * 1.4f;
            int seen = 0;
            foreach (var t in targets)
            {
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, t, Layers.World | Layers.Trees));
                if (hit.Count == 0 || hit["position"].AsVector3().DistanceTo(t) < 1.5f) seen++;
            }
            return seen / (float)targets.Count;
        }
        string Walk(Vector3 from, Vector3 to)
        {
            if (Valley.Current is not { } v) return "?";
            var path = NavBaker.Path(v.Nav.Map, from, to);
            if (path.Length == 0) return "none";
            float len = 0f;
            for (int k = 1; k < path.Length; k++) len += path[k].DistanceTo(path[k - 1]);
            bool ok = path[^1].DistanceTo(to) < 2.5f;
            return $"{(ok ? "ok" : "NO")} {len:0} m";
        }
        float flat(Vector3 a) => ((obj.Center - a) with { Y = 0f }).Length();
        GD.Print($"[{Clock.Now:0}s] {Name} assault audit on {(obj is SiteObjective s2 ? s2.Site.Name : "?")} ({np} windows + {targets.Count - np} ground): ORP sees {Sees(OrpAt):0.00} ({flat(OrpAt):0} m), SBF sees {Sees(SbfAt):0.00} ({flat(SbfAt):0} m, walk {Walk(OrpAt, SbfAt)}), LD sees {Sees(LdAt):0.00} ({flat(LdAt):0} m, walk {Walk(OrpAt, LdAt)})");
    }

    /// <summary>
    /// The support team is set (60% of it at its places). The assault waits for it, so a human in the team is no part
    /// of the count: he goes where he likes, and the squad's bots are not held for him (a team with only him in it
    /// needs nobody).
    /// </summary>
    public bool SupportSet
    {
        get
        {
            var team = Members.Where(m => m.Alive && TeamOf(m) == SbfTeam).ToList();
            var bots = team.Where(m => m is not Player).ToList();
            if (bots.Count == 0 && team.Count > 0) return true;
            return bots.Count(m => m.FeetPos.DistanceTo(SbfSlot(m)) < 10f) >= MathF.Max(1f, bots.Count * 0.6f);
        }
    }

    /// <summary>This man's firing position: his place among the support team's, with the view that place was picked for.</summary>
    public Vector3 SbfSlot(ICombatant m)
    {
        if (SbfSlots.Length == 0) return SbfAt;
        int k = Members.Where(x => x.Alive && TeamOf(x) == SbfTeam).ToList().IndexOf(m);
        return SbfSlots[Math.Max(0, k) % SbfSlots.Length];
    }

    /// <summary>His place at the line of departure (everyone in the squad who isn't in the support team).</summary>
    Vector3 LdSlot(ICombatant m)
    {
        if (LdSlots.Length == 0) return LdAt;
        int k = Members.Where(x => x.Alive && x != Leader && TeamOf(x) != SbfTeam).ToList().IndexOf(m);
        return LdSlots[(Math.Max(0, k) + 1) % LdSlots.Length]; // the leader has the point itself
    }

    /// <summary>The leader's orders for the deployment, given from where he is (the brain's own call is made in a lull, by a leader on the move).</summary>
    void DeployNow(Bot lead)
    {
        BeginDeploy(lead.GetWorld3D().DirectSpaceState, Objective!, PlanRng);
        Comms.Say(lead, $"Contact! {TeamName(SbfTeam)}, support by fire from the {Comms.Bearing(Objective!.Center, SbfAt)}. {TeamName(SbfTeam ^ 1)}, on me to the line of departure.");
    }

    /// <summary>Most of the support team (60%, humans aside) has shot in the last few seconds, within a few hundred metres of the objective.</summary>
    bool SupportFiring()
    {
        if (Objective == null) return false;
        var bots = Members.Where(m => m.Alive && m is not Player && TeamOf(m) == SbfTeam).ToList();
        if (bots.Count == 0) return false;
        int n = bots.Count(m => Clock.Now - m.LastShotTime < 6.0 && Flat(m.FeetPos, Objective.Center) < 300f);
        return n >= MathF.Max(1f, bots.Count * 0.6f);
    }

    public void BeginAssault(string why = "")
    {
        Assaults++;
        SetPhase(AssaultPhase.Assault, why);
    }

    /// <summary>
    /// <paramref name="retry"/>: the plan was overtaken by a fight, not given up on. Once the fight's over (see Engaged) the
    /// leader may form up and go at it again after ~20 s, not wait out the 90 s a finished attack gets.
    /// </summary>
    public void EndAssault(string why = "", bool retry = false)
    {
        AssaultEndedAt = retry ? Clock.Now - 70.0 : Clock.Now;
        SetPhase(AssaultPhase.None, why);
    }

    public double AssaultEndedAt = -999;

    /// <summary>
    /// Already attacking this objective, or did a minute and a half ago: no new ORP. After that, another go at it
    /// gets a proper attack again. (A squad kept the last objective it had attacked for good, so every later attempt
    /// on the same point, by men trickling back one at a time, was a straight walk-in.)
    /// </summary>
    public bool RecentlyAssaulted(IObjective? o) => AssaultOn == o && (Phase != AssaultPhase.None || Clock.Now - AssaultEndedAt < 90.0);

    /// <summary>Support-by-fire, firing on the objective: this man is in the support team during the assault.</summary>
    public bool FiringInSupport(ICombatant c) => SupportOpen && TeamOf(c) == SbfTeam;

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
