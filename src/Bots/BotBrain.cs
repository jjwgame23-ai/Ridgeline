using Godot;

namespace Ridgeline;

public enum BotState { Advance, Investigate, Engage, TakeCover, InCover, Hold, Flank, Search, Evade, Aid }

/// <summary>
/// Decisions. Think() runs a few times a second and picks what to do; Act()
/// runs every physics tick and does it — moving, aiming, peeking and shooting.
///
/// Advance     no info: move through the map toward the enemy side (sprinting between stops)
/// Investigate heard or glimpsed something: go and look
/// Engage      shooting at a visible enemy from where we stand (strafing and pushing up close)
/// TakeCover   running to a cover spot
/// InCover     hiding, then peeking (standing up, leaning or stepping out) to shoot
/// Hold        lost sight: hold the angle — briefly — and maybe put rounds on their cover
/// Flank       going around, often while a teammate covers
/// Search      pushing their last known position: run up, slow down close
/// Evade       a grenade landed nearby: get away from it
/// Aid         doing the role's job: a medic treating someone, an ammo bearer handing out
///             mags, an engineer filling sandbags
///
/// Standoffs don't last: after a short hold someone commits — a frag, a
/// "cover me" flank with a teammate suppressing, or a straight push.
/// </summary>
public sealed class BotBrain
{
    public static List<Vector3> Waypoints = new();
    /// <summary>Set by objective modes without squads (KOTH): where to go when there's nothing better to do.</summary>
    public static IObjective? DefaultObjective;
    /// <summary>Diagnostics: how often bots bound forward, push/flank, and duck into cover.</summary>
    public static int Bounds, Hunts, Covers, Heals, Resupplies, Builds, Launches, Revives, SelfAids;
    /// <summary>Who's already being seen to by a medic or ammo bearer, so two don't run to the same man.</summary>
    static readonly Dictionary<ICombatant, Bot> _claimed = new();

    public static void ResetStatics()
    {
        _claimed.Clear();
        Bounds = Hunts = Covers = Heals = Resupplies = Builds = Launches = Revives = SelfAids = 0;
    }
    /// <summary>Our squad's current objective, or the mode-wide one.</summary>
    IObjective? Objective => _b.Squad?.Objective ?? DefaultObjective;
    Squad? Sq => _b.Squad;
    Role Role => _b.Role;
    /// <summary>Recon and weapons teams hold a position and fight from it; they don't go chasing.</summary>
    bool Overwatch => Sq?.Kind is SquadKind.Recon or SquadKind.Weapons;
    Vector3? Watch => (Objective as PointObjective)?.Watch;

    readonly Bot _b;
    readonly RandomNumberGenerator _rng = new();

    public BotState State { get; private set; } = BotState.Advance;
    public Threat? Target { get; private set; }
    public CoverSpot? Cover { get; private set; }
    public bool WantsAds { get; private set; }
    public string Note { get; private set; } = "";
    public float AimErrorDeg { get; private set; }
    public bool Reacting => Target is { Visible: true } && !_reacted;
    /// <summary>The ground we're on (open, forest, urban, inside a building): refreshed every few seconds.</summary>
    public EnvKind Env { get; private set; } = EnvKind.Open;
    double _envAt;
    public bool Suppressing => Clock.Now < _suppressUntil;
    public string FireMode { get; private set; } = "";

    double _stateSince, _reactAt, _nextShotAt, _hideUntil, _peekUntil, _holdUntil, _pauseUntil, _pauseStart, _strafeUntil;
    double _lastCallout = -99, _lastSay = -99, _lastContact, _lastHurt = -99, _nextNade = 5, _suppressUntil = -1, _prefireUntil = -1;
    double _evadeUntil, _pushAfter = -1, _nextBoundAt, _followAt;
    /// <summary>Leading, waiting for the ride: since when (and how far off it was then, or when it last came 20 m closer); and walking on until.</summary>
    double _rideWaitSince = -1, _rideGiveUpUntil = -1;
    float _rideWaitGap;
    bool _following;
    double _nextAidCheck, _aidUntil, _nextLauncher = 4, _nextSupply, _lastMedicCall = -99, _nextShovel;
    ICombatant? _patient;
    int _aidKind; // 0 treat, 1 resupply, 2 build
    Vector3 _buildAt, _buildFacing;
    public bool Following => _following;
    /// <summary>For the verbose log: what his Advance movement is doing (see TerritoryMode's report of men standing still).</summary>
    public string MoveDebug => $"wp {(_hasWaypoint ? $"{_waypoint.DistanceTo(_b.FeetPos):0} m" : "-")}{(_pausing ? $" pausing {_pauseUntil - Now:0}s" : "")}{(_following ? " following" : "")}"
        + $" obj {(Objective is SiteObjective so ? so.Site.Name : Objective?.GetType().Name ?? "-")} {FromObjective(_b.FeetPos):0} m{(InZone ? " (in zone)" : "")}";
    /// <summary>Going round with the flanking team in a react-to-contact drill (not a lone man's flank).</summary>
    bool _drillFlank;
    /// <summary>Who the cover we're in was taken from.</summary>
    ICombatant? _coverFrom;
    /// <summary>Pushing on to the objective past a far-off enemy: keep at it a while, rather than flip back and forth.</summary>
    double _pushOnUntil = -1;
    bool _popPeek, _crouchLos = true, _proneLos, _exposedCrouched;
    /// <summary>Flat on the ground for incoming (a mortar bomb whistling down close by) until this time.</summary>
    double _hitTheDirtUntil = -1, _proneBlockedUntil = -1;
    double _popUntil, _popAt, _losCheckAt, _targetSince, _scanNextAt;
    float _scanAngle;
    int _strafeSide;
    bool _nearTube;
    bool _reacted, _wasVisible, _peeking, _autoBurst, _hasWaypoint, _pausing, _preferHead, _holdFire, _checkedSpawn;
    int _burstLeft;
    float _burstPause, _partT;
    Combatants.Part _aimPart = Combatants.Part.Chest;
    Threat? _reactTarget;
    Vector3 _waypoint, _strafe;
    readonly Queue<Vector3> _visited = new();

    public BotBrain(Bot b)
    {
        _b = b;
        _rng.Randomize();
    }

    static double Now => Clock.Now;
    float InState => (float)(Now - _stateSince);
    bool Sweeping => Objective == null && Now - _lastContact > 25.0;
    bool Panicked => _b.Suppression > 0.35f || Now - _lastHurt < 2.0;

    IEnumerable<Bot> Mates()
    {
        foreach (var c in Combatants.All)
            if (c is Bot m && m != _b && m.Alive && m.Team == _b.Team) yield return m;
    }

    void SetState(BotState s, string note)
    {
        Note = note;
        if (s == State) return;
        if (s is not (BotState.InCover or BotState.TakeCover)) Cover = null;
        if (State == BotState.Aid && s != BotState.Aid) EndAid();
        if (s != BotState.Advance) _following = false;
        if (s != BotState.Flank && _drillFlank) Prof.Count($"flank:drill-ended->{s}{(_b.Arrived ? " (there)" : "")}: {note}");
        if (s != BotState.Flank) _drillFlank = false;
        _peeking = false;
        _b.StrafeDir = null;
        _b.LeanTarget = 0f;
        Telemetry.State(_b, State, s, note);
        State = s;
        _stateSince = Now;
    }

    Vector3 ThreatEye(Threat t) => t.Visible ? t.Who.EyePos : t.LastKnownPos + Vector3.Up * 1.6f;
    /// <summary>Where we think the part we're aiming at is (see Threat.Tle): off to one side or the other at range.</summary>
    Vector3 AimPoint(Threat t)
    {
        var p = Combatants.PointOn(t.Who, _aimPart);
        if (t.Tle < 0.005f) return p;
        var across = (p - _b.EyePos).Cross(Vector3.Up);
        if (across.LengthSquared() < 1e-6f) return p;
        return p + (across.Normalized() * t.TleDir.X + Vector3.Up * t.TleDir.Y) * t.Tle;
    }
    static Vector3 SuppressPoint(Threat t) => t.LastKnownPos + Vector3.Up * 1.2f;

    bool MuzzleClear(Vector3 pt) =>
        _b.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(_b.MuzzlePos, pt, 1)).Count == 0;

    /// <summary>
    /// Aim at centre mass, or whatever part shows — but only a part the barrel can
    /// actually reach. If the muzzle would put the round into cover, hold fire.
    /// </summary>
    void ChooseAimPart(Threat t)
    {
        var order = new List<Combatants.Part>(3);
        if (_preferHead && t.HeadVisible) order.Add(Combatants.Part.Head);
        if (t.ChestVisible) order.Add(Combatants.Part.Chest);
        if (t.UpperVisible) order.Add(Combatants.Part.UpperChest); // what shows over a low wall
        if (t.HeadVisible && !order.Contains(Combatants.Part.Head)) order.Add(Combatants.Part.Head);
        if (t.HipVisible) order.Add(Combatants.Part.Hip);
        _holdFire = true;
        foreach (var part in order)
        {
            // With a margin below: recoil and tremor would clip a wall top the shot only just clears.
            var pp = Combatants.PointOn(t.Who, part);
            if (!MuzzleClear(pp) || !MuzzleClear(pp - Vector3.Up * 0.15f)) continue;
            _aimPart = part;
            _holdFire = false;
            return;
        }
        if (order.Count > 0) _aimPart = order[0];
    }

    bool ArmourNear(Vector3 p)
    {
        foreach (var v in Vehicle.All)
            if (!v.Destroyed && v.Def.Heavy && v.Crewed && v.CrewTeam == _b.Team && v.GlobalPosition.DistanceTo(p) < 150f) return true;
        return false;
    }

    Threat? PickTarget()
    {
        Threat? best = null;
        float bestScore = float.MinValue;
        foreach (var t in _b.Senses.Threats)
        {
            if (!t.Who.Alive || (!t.Confirmed && t.Awareness < 0.35f)) continue;
            float d = _b.FeetPos.DistanceTo(t.LastKnownPos);
            double age = Now - Math.Max(t.LastSeen, t.LastHeard);
            // The man he's fighting counts as in sight through a moment behind something (a head bob, a wall he
            // ducks behind), and the longer he's been on him the more it takes to switch; turning onto someone else
            // costs, the further round the more. (A target hidden for half a second lost all 100, and the aim
            // swung 60-110 degrees to someone far off and back again, several times a minute.)
            bool current = t == Target;
            bool seen = t.Visible || (current && Now - t.LastSeen < 1.0);
            float score = (seen ? 100f : 0f) - d * 0.3f - (float)age * 2f
                        + (current ? 15f + MathF.Min(20f, (float)(Now - _targetSince) * 4f) : -0.2f * Mathf.RadToDeg(_b.Aim.Dir.AngleTo(t.LastKnownPos - _b.EyePos)));
            // Infantry protect their armour: an enemy with a rocket near one of our vehicles goes first.
            if (t.Who.Role is Role.AntiTank or Role.HeavyAT && ArmourNear(t.LastKnownPos)) score += 40f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = t;
        }
        return best;
    }

    // ================================================================ Think

    public void Think()
    {
        if (Now > _envAt)
        {
            _envAt = Now + _rng.RandfRange(2.5f, 4f);
            Env = Surroundings.At(_b.GetWorld3D().DirectSpaceState, _b.FeetPos);
        }
        var was = Target;
        Target = PickTarget();
        if (Target != was) _targetSince = Now;
        var t = Target;
        bool vis = t is { Visible: true };
        if (t != null) _lastContact = Now;
        FollowThrough(t, vis);
        float dist = t != null ? _b.FeetPos.DistanceTo(t.LastKnownPos) : 999f;
        bool suppressed = _b.Suppression > 0.5f + _b.P.Courage * 0.35f;

        if (CheckGrenades()) return;
        if (CheckIncoming()) return;
        if (State == BotState.Evade)
        {
            SetState(t != null ? BotState.Hold : BotState.Advance, "clear of the frag");
            _holdUntil = Now + 1.0;
        }

        if (!_b.Reloading)
        {
            if (_b.Ammo == 0) { _b.StartReload(); Say("Reloading!"); }
            // Topping up in a lull: the part-used magazine goes in a pouch, unless the rounds are coming in now.
            else if (!vis && _b.Ammo < _b.Def.MagSize * 0.45f && (t == null || Now - t.LastSeen > 1.5))
                _b.StartReload(keep: !(suppressed || Now - _lastShotAt < 3.0 || Now - _lastHurt < 3.0));
        }

        PassiveSupply();
        if (Board(t, vis)) return;
        if (TryAntiArmor()) return;
        if (State == BotState.Aid)
        {
            if (ContinueAid(t, vis)) return;
        }
        else if (TryStartAid(t, vis)) return;
        if (t == null && _b.Health < 50f && Now - _lastMedicCall > 12.0 && Role != Role.Medic)
        {
            _lastMedicCall = Now;
            Say("Medic! I need a medic!");
        }

        if (Sq != null && Sq.Leader == _b) Sq.UpdateMarch(_b, InZone);
        if (DroneWork(t)) return;
        if (TryAntiDrone(t)) return;
        if (RunDrill(t)) return;
        // The squad leader's calls: flank a contact at a sensible range; get out if it's hopeless.
        if (Sq != null && Sq.Leader == _b && Sq.Engaged && Now > _breakCheckAt)
        {
            _breakCheckAt = Now + 3.0;
            int enemies = _b.Senses.Threats.Count(th => th.Who.Alive && (th.Visible || Now - th.LastSeen < 8.0) && th.LastKnownPos.DistanceTo(Sq.ContactAt) < 120f);
            // Outgunned AND actually under pressure (someone hit, or the leader pinned): seeing a lot
            // of enemies from a safe distance is an observation post doing its job, not a reason to run.
            bool pressed = Sq.Members.Any(m => !m.Alive || m.Body.Blood < 0.9f) || _b.Suppression > 0.5f;
            bool closing = Sq.ContactAt.DistanceTo(_b.FeetPos) < 150f;
            bool small = Sq.Kind is SquadKind.Recon or SquadKind.Weapons;
            if (!Sq.Defend && pressed && enemies >= Math.Max(4, Sq.Alive * 2) && (!small || closing)) Sq.BreakContact(Sq.ContactAt);
            else if (t is { Visible: true }) Sq.ReactToContact(t.LastKnownPos);
        }
        if (t == null)
        {
            // Armour close by, out of sight: stay put in cover until it's been quiet a while.
            if (Now < _armorWaryUntil && State is BotState.InCover or BotState.TakeCover) return;
            // Going round with the flanking team: the leader called it, whether or not we can see them yet.
            if (_drillFlank && !_b.Arrived && InState < DrillFlankTime) return;
            if (State != BotState.Advance) SetState(BotState.Advance, "no contacts");
            return;
        }
        // Outnumbered in a fight: call it in so the commander can send help.
        if (vis && Now > _nextContactCall)
        {
            _nextContactCall = Now + 20.0;
            int seen = 0;
            foreach (var th in _b.Senses.Threats) if (th.Visible || Now - th.LastSeen < 5.0) seen++;
            if (seen >= 4) Radio.Report(_b, RadioKind.HeavyContact, _b.FeetPos, null, seen);
        }

        if (vis)
        {
            _pushAfter = -1;
            // Close, or they're shooting at us: the squad is in a fight, whatever the orders.
            if (dist < 60f || (Now - _lastHurt < 3.0) || suppressed) Sq?.Engage(t.LastKnownPos);
            // Further off, the squad leader makes the call: take them on if they're in the way
            // (near our route or our objective), otherwise push on and fight at the objective.
            else if (Sq != null && Sq.Leader == _b && !Sq.Engaged && Objective != null && dist < 300f)
            {
                var toObj = (Objective.Center - _b.FeetPos) with { Y = 0f };
                var toThem = (t.LastKnownPos - _b.FeetPos) with { Y = 0f };
                bool inTheWay = toObj.LengthSquared() > 1f && toThem.Normalized().Dot(toObj.Normalized()) > 0.6f && toThem.Length() < toObj.Length() + 40f;
                bool atObjective = (t.LastKnownPos - Objective.Center with { Y = t.LastKnownPos.Y }).Length() < Objective.Radius + 150f;
                int seen = _b.Senses.Threats.Count(th => th.Visible || Now - th.LastSeen < 5.0);
                if ((inTheWay || atObjective) && seen <= Sq.Alive + 1)
                {
                    Sq.Engage(t.LastKnownPos);
                    Say(seen > 1 ? $"Contact, {seen} of them! Engage!" : "Contact front! Engage!");
                }
            }
            // On the way to the objective, don't get pinned into a long-range duel with someone
            // who isn't even contesting it: push on and fight at the objective (unless the squad's fighting).
            bool pushOn = Objective != null && !InZone && !suppressed && !Relevant(t, dist);
            // Once decided, it holds for a few seconds (unless it's gone wrong): otherwise a man flips between
            // "push on" and "take the fight" every time his suppression or the squad's state ticks over the line.
            if (pushOn || (Now < _pushOnUntil && dist > 60f && !suppressed && Now - _lastHurt > 3.0))
            {
                if (State != BotState.Advance) { SetState(BotState.Advance, "pushing through to the objective"); _hasWaypoint = false; }
                if (pushOn && _pushOnUntil < Now) _pushOnUntil = Now + 8.0;
                return;
            }
            // Just decided he isn't our business, and nothing's changed: carry on (shooting at him if he shows), rather
            // than turn to fight him every time he pops up and ignore him every time he ducks.
            if (State == BotState.Advance && t.Who == _ignored && Now < _ignoreUntil && !Relevant(t, dist)) return;
            if (State is BotState.InCover or BotState.Engage or BotState.Hold && TryLauncher(t)) return;
            switch (State)
            {
                case BotState.TakeCover:
                    break;
                // The flanking team keeps going when it's seen: going to ground halfway round, out in the
                // open, is the worst of both. Only if it's gone wrong (hit, pinned, or on top of them) does it fight here.
                case BotState.Flank when _drillFlank && !_b.Arrived && InState < DrillFlankTime && dist > 30f && !suppressed && Now - _lastHurt > 2.0:
                    break;
                case BotState.InCover:
                    // Cover is judged against whoever it was taken from, and given a few seconds to prove
                    // itself. (Judged against whoever happened to be the target this instant, and every
                    // shift of theirs, men shuffled a metre or two from spot to spot every couple of seconds:
                    // 640 times in a 6-minute match, none of them getting anywhere.)
                    var coverFrom = _coverFrom is { Alive: true } cf && _b.Senses.Find(cf) is { } cth ? cth : t;
                    if (!_peeking && (InState > 3f || Now - _lastHurt < 1.0) && !CoverFinder.Protected(_b, Cover!.Value.Pos, ThreatEye(coverFrom), true))
                    {
                        if (!GoToCover(t, 10f, suppressed)) SetState(BotState.Engage, "cover blown");
                    }
                    else if (!_peeking) TryBound(t);
                    break;
                case BotState.Engage:
                    // Exposed as he is now: lying flat, a fold in the ground or a kerb can be enough.
                    bool exposed = _b.Prone ? !CoverFinder.ProtectedProne(_b, _b.FeetPos, t.Who.EyePos) : !CoverFinder.Protected(_b, _b.FeetPos, t.Who.EyePos, _b.Crouched);
                    if (exposed && (suppressed || _b.Reloading || _b.Health < 40f)) GoToCover(t, suppressed ? 14f : 10f, suppressed);
                    else if (dist > 8f && TryBound(t)) { }
                    else if (exposed && dist > 25f && InState > 2f + _b.P.Aggression * 5f && _rng.Randf() < 0.25f) GoToCover(t, 8f, false);
                    break;
                default:
                    _preferHead = _b.P.Skill > 0.85f && _rng.Randf() < 0.3f;
                    if (dist < 14f && _b.P.Aggression > 0.25f) SetState(BotState.Engage, "close contact");
                    else if (_b.P.Aggression < 0.6f || suppressed)
                    {
                        if (!GoToCover(t, 9f, suppressed)) SetState(BotState.Engage, "no cover near");
                    }
                    else SetState(BotState.Engage, "taking the fight");
                    break;
            }
            return;
        }

        // ---- Known but not visible.
        double since = Now - Math.Max(t.LastSeen, t.LastHeard);
        // Out of sight for a moment (a head bob, a blade of grass across his line): still in the fight, on the spot
        // where the man was. (One tick without a line flipped Engage to Hold: he stood up, stopped moving, and knelt
        // again a fraction of a second later.)
        if (State == BotState.Engage && Now - t.LastSeen < 0.7) return;
        // In cover from armour close by: he stays in it while that lasts, whoever else is about. (With any infantry
        // known of, "objective first" walked him straight back out, and six seconds later back in again.)
        if (Now < _armorWaryUntil && State is BotState.InCover or BotState.TakeCover) return;

        // With an objective, don't get dragged off chasing noises: an unseen enemy that isn't
        // near the objective or right on top of us isn't worth leaving it for. (A drill's flanking
        // team isn't chasing anything: the leader sent it round, and it goes.)
        if (Objective != null && State is not (BotState.TakeCover or BotState.Advance) && !_drillFlank)
        {
            bool relevant = Relevant(t, dist);
            bool stale = Now - t.LastSeen > 12.0 && InState > 8f;
            if (!relevant || stale)
            {
                SetState(BotState.Advance, relevant ? "back to the objective" : "ignoring, objective first");
                if (!relevant) { _ignored = t.Who; _ignoreUntil = Now + 8.0; }
                _hasWaypoint = false;
                return;
            }
        }

        if (_pushAfter > 0 && Now > _pushAfter)
        {
            _pushAfter = -1;
            SetState(BotState.Search, "pushing behind the frag");
            _b.MoveTo(t.LastKnownPos, MoveMode.Run);
            return;
        }
        if (State is BotState.Hold or BotState.InCover or BotState.Search && (TryGrenade(t) || TryLauncher(t))) return;
        // Overwatch doesn't chase: keep watching, keep them pinned, give up only when they're long gone.
        if (Overwatch && State is BotState.InCover or BotState.Hold)
        {
            if (since > 30.0) GiveUp(t);
            else if (State == BotState.Hold && Now > _holdUntil) _holdUntil = Now + _b.P.Patience;
            else MaybeSuppress(t);
            return;
        }

        switch (State)
        {
            case BotState.Engage:
                SetState(BotState.Hold, "lost sight");
                _holdUntil = Now + HuntPatience(dist);
                _popAt = Now + _rng.RandfRange(2f, 4f); // a real pause before the first look over the top
                // Keep them pinned where they ducked.
                if (Now >= _suppressUntil && Now - t.LastSeen < 1.0 && _rng.Randf() < 0.3f + _b.P.Aggression * 0.4f)
                    _suppressUntil = Now + _rng.RandfRange(1.5f, 3f);
                break;
            case BotState.TakeCover:
                break;
            case BotState.InCover:
                if (since > HuntPatience(dist)) Hunt(t);
                else if (!TryBound(t)) MaybeSuppress(t);
                break;
            case BotState.Hold:
                if (Now > _holdUntil)
                {
                    if (since > HuntPatience(dist) * 2f + 8f) GiveUp(t);
                    else Hunt(t);
                }
                else if (!TryBound(t)) MaybeSuppress(t);
                break;
            case BotState.Flank:
                if (_b.Arrived || InState > (_drillFlank ? DrillFlankTime : 20f))
                {
                    if (_drillFlank) Prof.Count(_b.Arrived ? "flank:drill-arrived" : "flank:drill-timed-out");
                    SetState(BotState.Hold, "flank set");
                    _holdUntil = Now + _b.P.Patience * 0.6f;
                }
                break;
            case BotState.Search:
            case BotState.Investigate:
                if (_b.Arrived || InState > 25f)
                {
                    if (since > _b.P.Patience + 8f) GiveUp(t);
                    else
                    {
                        SetState(BotState.Hold, "checking");
                        _holdUntil = Now + _b.P.Patience * 0.5f;
                    }
                }
                // Run up, slow down and get the gun up once close.
                else _b.MoveTo(t.LastKnownPos, dist < 12f ? MoveMode.Walk : MoveMode.Run);
                break;
            case BotState.Advance:
                if (Objective != null && (t.LastKnownPos - Objective.Center with { Y = t.LastKnownPos.Y }).Length() > Objective.Radius + 40f && dist > 35f) break;
                // Further off, keep going with the squad (it's heading there anyway): one man doesn't set off across
                // open ground after someone out of sight 200 m away.
                if (dist > 80f) break;
                if (t.Confirmed) Hunt(t);
                else
                {
                    SetState(BotState.Investigate, "something over there");
                    _b.MoveTo(t.LastKnownPos, MoveMode.Run);
                }
                break;
        }
    }

    void GiveUp(Threat t)
    {
        _b.Senses.Forget(t);
        SetState(BotState.Advance, "lost them");
        _hasWaypoint = false;
    }

    /// <summary>Someone has to peek it: a covered flank, a solo flank, or a straight push.</summary>
    void Hunt(Threat t)
    {
        // Past the limit of advance: hold here, don't run off after them.
        if (Sq?.BeyondLoa(t.LastKnownPos) == true)
        {
            SetState(BotState.Hold, "holding at the limit of advance");
            _holdUntil = Now + 4.0;
            return;
        }
        Hunts++;
        float d = _b.FeetPos.DistanceTo(t.LastKnownPos);
        // Going into a building after someone: a frag through the door first, then in behind it.
        if (d < 30f && Surroundings.Indoors(_b.GetWorld3D().DirectSpaceState, t.LastKnownPos) && TryGrenade(t)) return;
        Bot? cover = null;
        foreach (var m in Mates())
            if (m.Senses.Find(t.Who) is { Confirmed: true } && m.FeetPos.DistanceTo(_b.FeetPos) < 35f && m.Brain.State is not (BotState.Flank or BotState.Evade))
            {
                cover = m;
                break;
            }

        if (cover != null && _rng.Randf() < 0.35f + _b.P.Aggression * 0.5f && CoverFinder.FindFlank(_b, t.LastKnownPos, _rng) is Vector3 fp)
        {
            Say("Moving, cover me!");
            cover.Brain.CoverMe(t.Who);
            SetState(BotState.Flank, "flanking, covered");
            _b.MoveTo(fp, MoveMode.Sprint);
            return;
        }
        if (_rng.Randf() < 0.2f + _b.P.Aggression * 0.35f && CoverFinder.FindFlank(_b, t.LastKnownPos, _rng) is Vector3 p)
        {
            SetState(BotState.Flank, "flanking");
            _b.MoveTo(p, MoveMode.Sprint);
            return;
        }
        SetState(BotState.Search, "pushing");
        _b.MoveTo(t.LastKnownPos, d < 12f ? MoveMode.Walk : MoveMode.Run);
    }

    /// <summary>
    /// How long we watch where they went to ground before going after them ourselves. A few seconds close in (he's
    /// round that corner); at range, much longer: there you keep your head down and keep firing on his position,
    /// and it's the fire team that moves, bounding or going round, not one man running at him across 200 m of
    /// open ground. (Everyone used to set off after a few seconds whatever the range: suppressive fire stopped
    /// almost as soon as the enemy ducked, and men were hit crossing open ground on their own.)
    /// </summary>
    float HuntPatience(float dist) => _b.P.Patience * (1f + Mathf.Clamp((dist - 40f) / 40f, 0f, 5f));

    /// <summary>A teammate is moving: pin the enemy so they can't peek him.</summary>
    bool InZone => Objective != null && (_b.FeetPos - Objective.Center with { Y = _b.FeetPos.Y }).Length() < Objective.Radius;

    /// <summary>The objective moved: drop the current waypoint and head for the new one.</summary>
    public void ObjectiveChanged()
    {
        if (State != BotState.Advance) return;
        _hasWaypoint = false;
        _pausing = false;
    }

    /// <summary>
    /// Fire and manoeuvre: after fighting from a spot for a bit, move up to the next
    /// covered spot toward the enemy, or toward the objective if we're not on it
    /// yet. Only a few of the squad move at once; one of the others covers.
    /// </summary>
    bool TryBound(Threat t)
    {
        if (Now < _nextBoundAt) return false;
        _nextBoundAt = Now + _rng.RandfRange(1.2f, 2.5f);
        if (_b.Suppression > 0.4f + _b.P.Courage * 0.35f || _b.Reloading || _b.Ammo < _b.Def.MagSize * 0.3f || _b.Health < 35f) return false;
        float d = _b.FeetPos.DistanceTo(t.LastKnownPos);
        // Fight from here for a while first; the eager go sooner.
        if (Overwatch) return false;
        float settle = Mathf.Lerp(9f, 2.5f, _b.P.Aggression) * (t.Visible ? 1f : 0.6f) * (Role == Role.AutoRifleman ? 1.8f : 1f);
        // The last 40 m are closed by buddy rushes, pair by pair; further out, team by team.
        if (d < 40f) return InState >= settle * 0.5f && BuddyRush(t, d);
        if (InState < settle) return false;
        if (Sq != null && !Sq.MayBound(_b, Now)) return false;

        Vector3 goal = t.LastKnownPos;
        if (Objective != null)
        {
            if (!InZone) goal = Objective.Center; // take ground toward the objective
            else if (Sq is { Defend: true } && FromObjective(t.LastKnownPos) > Objective.Radius + 25f) return false; // hold it, don't chase
        }
        if (Sq?.BeyondLoa(goal) == true) return false;
        var spot = CoverFinder.FindForward(_b, ThreatEye(t), goal, _rng, Surroundings.BoundStep(Env));
        bool rush = false;
        // In the open with nothing to bound to: a rush, to drop and fire again further on. (Without it an
        // attack across open ground had no way forward at all, and fights sat at 200 m for minutes.)
        if (spot == null && Env is EnvKind.Open or EnvKind.Forest)
        {
            spot = CoverFinder.FindRush(_b, ThreatEye(t), goal, _rng, 12f, 22f);
            rush = spot != null;
        }
        if (spot is not CoverSpot s) return false;

        // The other team keeps their heads down while we go (bounding overwatch).
        int covering = 0;
        foreach (var m in (IEnumerable<Bot>?)Sq?.Overwatch(_b) ?? Mates())
            if (m.FeetPos.DistanceTo(_b.FeetPos) < 45f && m.Brain.State is BotState.InCover or BotState.Engage or BotState.Hold && m.Senses.Find(t.Who) != null)
            {
                m.Brain.CoverMe(t.Who);
                if (++covering >= 3) break;
            }
        Bounds++;
        Sq?.MarkMoving(_b, Now + 5.0);
        Say(rush ? (_rng.Randf() < 0.5f ? "I'm up!" : "Rushing!") : _rng.Randf() < 0.5f ? "Moving up!" : "Moving!");
        SetState(BotState.TakeCover, rush ? RushNote : "bounding forward");
        if (rush) Prof.Count("bound:rush"); else Prof.Count("bound:to cover");
        Cover = s;
        _b.MoveTo(s.Pos, MoveMode.Sprint);
        return true;
    }

    /// <summary>A dash across open ground to drop and fire from (see CoverFinder.FindRush).</summary>
    const string RushNote = "rushing";

    /// <summary>
    /// The last few tens of metres: buddy rushes. One of the pair gets up and dashes a few
    /// metres to the next bit of cover while the other keeps the enemy's head down; then they
    /// swap. Short enough that the enemy can't get a bead on the runner.
    /// </summary>
    bool BuddyRush(Threat t, float d)
    {
        var buddy = Sq?.BuddyOf(_b);
        if (buddy is not Bot bb || buddy.FeetPos.DistanceTo(_b.FeetPos) > 25f || d < 6f) return false;
        if (!Sq!.IsMover(_b, Now, _rng)) return false;
        if (Sq.BeyondLoa(t.LastKnownPos)) return false;
        var spot = CoverFinder.FindForward(_b, ThreatEye(t), t.LastKnownPos, _rng, 9f);
        bool rush = false;
        if (spot == null && Env is EnvKind.Open or EnvKind.Forest)
        {
            spot = CoverFinder.FindRush(_b, ThreatEye(t), t.LastKnownPos, _rng, 6f, 10f);
            rush = spot != null;
        }
        if (spot is not CoverSpot s) return false;
        if (bb.Senses.Find(t.Who) != null) bb.Brain.CoverMe(t.Who);
        Bounds++;
        Say(_rng.Randf() < 0.5f ? "I'm up!" : "Moving!");
        SetState(BotState.TakeCover, rush ? RushNote : "rushing (buddy covering)");
        Cover = s;
        _b.MoveTo(s.Pos, MoveMode.Sprint);
        return true;
    }

    float FromObjective(Vector3 p) => Objective == null ? 0f : (p - Objective.Center with { Y = p.Y }).Length();

    /// <summary>
    /// Is this enemy our business, or do we press on to the objective past him? One test, whether he's in sight or
    /// not. (There were two, and they disagreed: in sight, a man 50 m off, or one while the squad was fighting
    /// somebody else, was taken on; out of sight, he was ignored; so men flipped between the two every time he
    /// showed and hid.) Ours: anyone at or near the objective, anyone close, anyone shooting at us, anyone at all
    /// while the squad's in a fight (we don't walk on and leave it), what an overwatch team is there to watch.
    /// </summary>
    bool Relevant(Threat t, float dist)
    {
        if (Objective == null) return true;
        return FromObjective(t.LastKnownPos) < Objective.Radius + 40f || dist < 80f
               || Now - _lastShotAt < 10.0 || Now - _lastHurt < 10.0
               || Sq is { Engaged: true }
               || (Watch is Vector3 w && (t.LastKnownPos - w with { Y = t.LastKnownPos.Y }).Length() < 110f);
    }

    public void CoverMe(ICombatant enemy)
    {
        var t = _b.Senses.Find(enemy);
        if (t == null || !_b.Alive) return;
        _suppressUntil = Now + _rng.RandfRange(3f, 5f);
        if (State is BotState.Advance or BotState.Search or BotState.Investigate)
        {
            SetState(BotState.Hold, "covering");
            _holdUntil = _suppressUntil + 1.0;
            _b.Stop();
        }
        else if (State == BotState.InCover) _hideUntil = Now; // peek now and start shooting
        Say("Covering!");
    }

    /// <summary>
    /// How long after they were last seen we'll keep putting rounds where they went to ground. A man who ducked
    /// is still there, most likely, for a good while; and fire on his position is what keeps him down while
    /// our people move. (It used to stop five seconds after he dropped out of sight: most of a firefight went
    /// quiet between glimpses.)
    /// </summary>
    double SuppressFor => Role == Role.AutoRifleman ? 25.0 : 15.0;

    /// <summary>The man we were shooting at, while he was in sight.</summary>
    Threat? _sawTarget;
    /// <summary>Someone we've just decided to leave alone (see Think), and until when.</summary>
    ICombatant? _ignored;
    double _ignoreUntil;

    /// <summary>
    /// He's just dropped out of sight while we were firing at him: keep the rounds going onto where he went down
    /// for a few seconds. That's the point of the fire: he can't come back up to shoot, or get up and move, while
    /// it's landing on him. (It used to happen only some of the time, and only for a man out in the open; from
    /// cover the peek ended on its own clock and the fire with it.)
    /// </summary>
    void FollowThrough(Threat? t, bool vis)
    {
        if (vis) { _sawTarget = t; return; }
        if (t == null || t != _sawTarget) { _sawTarget = null; return; }
        _sawTarget = null;
        if (Now - _b.LastShotTime > 2.5 || Now < _suppressUntil || _b.Reloading || _b.Ammo < _b.Def.MagSize * 0.25f) return;
        _suppressUntil = Now + (Role == Role.AutoRifleman ? _rng.RandfRange(3f, 6f) : _rng.RandfRange(2f, 4f));
        if (State == BotState.InCover && _peeking) _peekUntil = Math.Max(_peekUntil, _suppressUntil);
        Prof.Count("supp:follow-through");
    }

    void MaybeSuppress(Threat t)
    {
        // The automatic rifleman's job is exactly this: long, steady fire on where they are.
        bool ar = Role == Role.AutoRifleman;
        double since = Now - t.LastSeen;
        if (since > SuppressFor) { Prof.Count("supp:no (too long since seen)"); return; }
        if (Now < _suppressUntil + (ar ? 0.6 : 1.5)) { Prof.Count("supp:no (in or just after a window)"); return; }
        if (_b.Ammo < _b.Def.MagSize * (ar ? 0.15f : 0.4f)) { Prof.Count("supp:no (magazine low)"); return; }
        bool support = Now < _supportUntil; // the base of fire in a squad drill: that's their whole job
        // Less keen the longer it's been (they may have moved), unless covering someone.
        float fresh = support ? 1f : 1f - 0.6f * (float)(since / SuppressFor);
        if (_rng.Randf() > (0.12f + _b.P.Aggression * 0.2f) * (ar ? 3f : 1f) * (support ? 2.5f : 1f) * fresh) { Prof.Count("supp:no (not this time)"); return; }
        Prof.Count("supp:window opened");
        _suppressUntil = Now + (ar ? _rng.RandfRange(3f, 6f) : _rng.RandfRange(1.5f, 3.5f));
        if (State == BotState.InCover) _hideUntil = Now; // up and firing now (see ActCover), not whenever the next peek comes round
    }

    // ================================================================ role work

    /// <summary>
    /// Grenadier: lob a 40 mm round at people behind cover or too far for a rifle
    /// to do much. Not when they're close and in the open: that's what the carbine is for.
    /// </summary>
    bool TryLauncher(Threat t)
    {
        if (Role != Role.Grenadier || _b.LauncherRounds <= 0 || Now < _nextLauncher) return false;
        _nextLauncher = Now + 1.5;
        double since = Now - Math.Max(t.LastSeen, t.LastHeard);
        if (since > 6.0) return false;
        var flat = t.LastKnownPos - _b.FeetPos;
        flat.Y = 0f;
        float d = flat.Length();
        if (d < 35f || d > 300f) return false;
        if (t.Visible && d < 60f && !CoverFinder.Protected(_b, t.Who.FeetPos, _b.EyePos, true)) return false;
        if (_rng.Randf() > 0.35f + _b.P.Aggression * 0.35f) return false;
        if (OwnNear(t.LastKnownPos, 14f)) return false;
        float skill = _b.P.Skill;
        float rangeErr = _rng.RandfRange(-1f, 1f) * (0.03f + (1f - skill) * 0.08f + (float)since * 0.01f);
        float yawErr = _rng.RandfRange(-1f, 1f) * (0.4f + (1f - skill) * 1.5f);
        _b.Aim.Goal = t.LastKnownPos + Vector3.Up * 1f;
        if (!_b.FireLauncherAt(t.LastKnownPos, rangeErr, yawErr)) return false;
        Launches++;
        _nextLauncher = Now + _rng.RandfRange(5f, 10f);
        Say(_rng.Randf() < 0.5f ? "40 mike out!" : "Launcher, going over!");
        return true;
    }

    // ---- vehicles

    double _boardCheck, _nextContactCall, _boardBestAt;
    float _boardBest;
    int _boardSide;

    /// <summary>
    /// Crews go to their vehicle and get in (driver first, then the gun); a squad whose
    /// transport has come for it climbs aboard; the logistics team rides with its truck
    /// when it's taking supplies forward. Not while there's someone to shoot at close by.
    /// </summary>
    bool Board(Threat? t, bool vis)
    {
        if (_b.Ride != null || Sq == null) return false;
        bool close = vis && t != null && t.LastKnownPos.DistanceTo(_b.FeetPos) < 120f;
        // On the way to it: keep going, between the checks too, unless they're close or we're hit. (Before, the
        // fight logic took over between checks, and a crewman with an enemy in sight 150 m off went "to the
        // vehicle", "take the fight", "to the vehicle", twice a second, and did neither.) A crew's job is its
        // vehicle, not a rifle.
        if (Now < _boardCheck)
            return State == BotState.Advance && Note is "to the transport" or "to the vehicle" or ToTheTube or AssistantGunner && !close && Now - _lastHurt > 2.0;
        if (close) return false;
        Vehicle? v = null;
        SeatRole? seat = null;
        // Not one the motor pool has given up on (it can't move): the crew got out of it for a reason. (Before, a
        // truck with its wheels shot out had its driver bailing out and climbing back in every second, and it was
        // never written off and replaced, since it was never empty.)
        if (Sq.Kind is SquadKind.Armor or SquadKind.Transport or SquadKind.Air or SquadKind.Mortar && Sq.Vehicle is { Destroyed: false, Useless: false } own) { v = own; seat = SeatRole.Driver; }
        else if (Sq.Kind == SquadKind.Logistics && Sq.Vehicle is { Destroyed: false, Useless: false } truck && Sq.TruckRun) { v = truck; seat = SeatRole.Driver; }
        else if (Sq.Transport is { Destroyed: false, Boarding: true } ride) { v = ride; seat = SeatRole.Passenger; }
        if (v == null)
        {
            // The ride left (or was lost) while we were running to it.
            if (Note is "to the transport" or "to the vehicle") { _b.Stop(); _hasWaypoint = false; SetState(BotState.Advance, "ride gone"); }
            return false;
        }
        // No seat for us (a mortar has one, for its gunner): the other man stays by it, not climbing onto it.
        if (v.FreeSeat(seat) < 0)
        {
            // A mortar's second man is its assistant gunner: he kneels beside the tube and hangs the bombs. (He
            // used to stand about near it with nothing to do, "no seat", for the whole match.)
            if (v.Turrets.Length > 0 && v.Turrets[0].Def.Indirect && v.Crewed && v.CrewTeam == _b.Team) return AtTheTube(v);
            // Was on the way to it, and the seat's gone: stop heading for it.
            if (Note is "to the transport" or "to the vehicle") { _b.Stop(); _hasWaypoint = false; SetState(BotState.Advance, "no seat"); }
            return false;
        }
        float d = v.GlobalPosition.DistanceTo(_b.FeetPos);
        // A ride that has come for us, when it's close. A crew's own vehicle however far off it is: it's their job,
        // and nobody else will bring it to them. (Crews more than 400 m from theirs were left to their old orders:
        // a gunship's crew, shot down, walked on to the fight they had been flying over, and the replacement sat
        // on its pad uncrewed for the rest of the match; the logistics team walked to its FOB site on foot and
        // left the truck at base.)
        if (d > 400f && seat != SeatRole.Driver) return false;
        // How far from the hull itself (not its middle, which is inside it).
        var l = v.ToLocal(_b.FeetPos);
        float ex = MathF.Max(0f, MathF.Abs(l.X) - v.Def.Hull.X * 0.5f), ez = MathF.Max(0f, MathF.Abs(l.Z) - v.Def.Hull.Z * 0.5f);
        bool atHull = MathF.Sqrt(ex * ex + ez * ez) < 2.5f && MathF.Abs(l.Y) < 4f;
        if (atHull)
        {
            _boardCheck = Now + 1.0;
            int s = v.FreeSeat(seat);
            // Crews take the driver's seat, then the gun, then anything.
            if (seat == SeatRole.Driver && v.DriverSeat >= 0 && v.Occupants[v.DriverSeat] != null && v.GunnerSeat >= 0 && v.Occupants[v.GunnerSeat] == null) s = v.GunnerSeat;
            if (s >= 0 && v.Enter(_b, s)) { SetState(BotState.Advance, "mounted"); _boardSide = 0; return true; }
            return false;
        }
        bool going = State == BotState.Advance && Note is "to the transport" or "to the vehicle";
        if (!going || d < _boardBest - 3f) { _boardBest = d; _boardBestAt = Now; }
        // Couldn't get round to where we were heading (the walk ended short of the hull, or was given up stuck on
        // something: a wreck, a container, the next vehicle in the row), or no nearer for a while: try the next side.
        // (The walk used to be set off once: a crew that got stuck on the way stood there, still "to the vehicle",
        // for the rest of the match, and their vehicle sat at base uncrewed.)
        bool lost = going && (_b.Arrived || Now - _boardBestAt > 20.0);
        if (lost) { _boardSide++; _boardBest = d; _boardBestAt = Now; }
        // Round to the back (the ramp, the rear doors), onto ground we can stand on, not into the middle of the
        // hull; failing that a side (there are steps and hatches all round), then the front.
        var basis = v.GlobalBasis;
        var door = (_boardSide % 4) switch
        {
            1 => v.GlobalPosition + basis.X * (v.Def.Hull.X * 0.5f + 1.2f),
            2 => v.GlobalPosition - basis.X * (v.Def.Hull.X * 0.5f + 1.2f),
            3 => v.GlobalPosition - basis.Z * (v.Def.Hull.Z * 0.5f + 1.2f),
            _ => v.GlobalPosition + basis.Z * (v.Def.Hull.Z * 0.5f + 1.2f),
        };
        var navDoor = Valley.ClosestOnFoot(_b.GetWorld3D(), door);
        if (((navDoor - door) with { Y = 0f }).Length() < 4f) door = navDoor;
        if (!going || lost || _b.GoalPos.DistanceTo(door) > 2f)
        {
            SetState(BotState.Advance, seat == SeatRole.Passenger ? "to the transport" : "to the vehicle");
            _b.MoveTo(door, MoveMode.Sprint);
            _hasWaypoint = true;
            _waypoint = door;
        }
        _boardCheck = Now + 0.5;
        return true;
    }

    public const string AssistantGunner = "assistant gunner", ToTheTube = "to the tube";

    /// <summary>
    /// The assistant gunner's post: kneeling at the tube's left rear, clear of the way it's laid (he shifts round
    /// with it as it traverses, the way a crew moves the bipod), close enough to hang each bomb in the muzzle.
    /// </summary>
    bool AtTheTube(Vehicle v)
    {
        var tube = v.Turrets[0].YawNode.GlobalBasis;
        var fwd = (-tube.Z) with { Y = 0f };
        fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : v.Forward;
        var left = Vector3.Up.Cross(fwd);
        // Left rear by preference; if the ground there can't be stood on (a pit dug into a steep slope), the other
        // side, behind, or beside it: the first spot a man can get to.
        Vector3? post = null;
        foreach (var off in new[] { -fwd * 0.7f + left * 1.0f, -fwd * 0.7f - left * 1.0f, -fwd * 1.2f, left * 1.2f, -left * 1.2f })
        {
            var want = v.GlobalPosition + off;
            var onFoot = Valley.ClosestOnFoot(_b.GetWorld3D(), want);
            if (((onFoot - want) with { Y = 0f }).Length() < 0.6f) { post = onFoot; break; }
            if (post == null && ((onFoot - v.GlobalPosition) with { Y = 0f }).Length() < 2.5f) post = onFoot;
        }
        var at = post ?? v.GlobalPosition - fwd * 0.7f + left * 1.0f;
        _boardCheck = Now + 0.5;
        float d = ((_b.FeetPos - at) with { Y = 0f }).Length();
        // At his post, or anywhere close beside or behind the tube, clear of the muzzle: he stays there until the
        // tube is swung round onto him, rather than shuffling round after it every time it traverses.
        var fromTube = (_b.FeetPos - v.GlobalPosition) with { Y = 0f };
        // (His post can fall just inside the tube's own footprint, and he kept pushing at it, stuck, beside the tube.)
        // Once there, he stays unless he's well away or the tube swings onto him: the post moves a little each time the
        // tube traverses, and a nudge from the gunner beside him put him past 2.6 m. (He got up, stepped back and knelt
        // again every couple of seconds for as long as the tube was in action.)
        bool byTube = fromTube.Length() < (Note == AssistantGunner ? 3.6f : 2.6f) && fromTube.Dot(fwd) < 0.8f;
        _nearTube = fromTube.Length() < 5f;
        if (d < 0.8f || byTube)
        {
            if (Note != AssistantGunner) { _b.Stop(); _hasWaypoint = false; SetState(BotState.Advance, AssistantGunner); }
            return true;
        }
        if (Note != ToTheTube || _b.GoalPos.DistanceTo(at) > 0.5f)
        {
            SetState(BotState.Advance, ToTheTube);
            _b.MoveTo(at, MoveMode.Walk);
            _hasWaypoint = true;
            _waypoint = at;
        }
        return true;
    }

    // ---- armour

    double _rocketLaidSince = -1, _nextArmorCheck, _armorCoverAt;
    Vehicle? _rocketTarget;
    public static int Rockets;

    /// <summary>
    /// Enemy armour in sight. An AT soldier with a rocket for it stops, shoulders the
    /// launcher, lays it (leading a moving target) and fires. Everyone else gets into
    /// cover from it: rifles don't hurt a tank.
    /// </summary>
    bool TryAntiArmor()
    {
        VehicleThreat? best = null;
        float bestD = float.MaxValue;
        foreach (var ev in _b.Senses.Vehicles)
        {
            if (!ev.Visible || !GodotObject.IsInstanceValid(ev.Who) || ev.Who.Destroyed) continue;
            float d = ev.Who.Center.DistanceTo(_b.EyePos);
            if (d < bestD) { bestD = d; best = ev; }
        }
        if (best == null)
        {
            _rocketTarget = null;
            _rocketLaidSince = -1;
            return RememberedArmor();
        }
        var v = best.Who;
        var rd = _b.RocketDef;
        // Aircraft: only the anti-air missile is any use; everyone else ignores them (or shoots at them with rifles, which is what the rest of the brain does).
        if (v.Def.Air && !v.Landed)
        {
            if (rd != WeaponDef.Manpad || _b.Rockets <= 0 || bestD > 2800f) return false;
            if (_rocketTarget != v) { _rocketTarget = v; _rocketLaidSince = Now; }
            if (State != BotState.Hold) SetState(BotState.Hold, $"AA: tracking a {v.Def.ClassName}");
            _holdUntil = Now + 2.0;
            _b.Stop();
            _b.Aim.Goal = v.Center;
            _b.Aim.Tracking = false;
            // The seeker needs a couple of seconds on the target to lock.
            if (Now - _rocketLaidSince > 2.2 && Mathf.RadToDeg(_b.Aim.Dir.AngleTo(v.Center - _b.EyePos)) < 3f && _b.FireRocket(v.Center, bestD, v))
            {
                Rockets++;
                Say("Missile away!");
                _rocketLaidSince = Now + 3.0;
            }
            return true;
        }
        if (v.Def.Air || rd == WeaponDef.Manpad) rd = null; // a landed helicopter, or an AA gunner facing armour: fall through to cover
        float reach = rd == WeaponDef.Hat ? 380f : 260f;
        if (rd != null && _b.Rockets > 0 && bestD < reach && rd.Pen * 1.1f > v.ArmorToward(_b.EyePos) * 0.8f)
        {
            if (_rocketTarget != v) { _rocketTarget = v; _rocketLaidSince = Now; }
            if (State != BotState.Hold) SetState(BotState.Hold, $"AT: engaging {v.Def.ClassName}");
            _holdUntil = Now + 2.0;
            _b.Stop();
            // Lead: where it'll be when the rocket arrives.
            float tof = bestD / rd.MuzzleVel;
            var aim = best.AimPoint + v.Forward * v.Speed * tof;
            _b.Aim.Goal = aim;
            _b.Aim.Tracking = false;
            float laid = Mathf.Lerp(3.2f, 1.4f, _b.P.Skill);
            if (Now - _rocketLaidSince > laid && Mathf.RadToDeg(_b.Aim.Dir.AngleTo(aim - _b.EyePos)) < 1.2f && !FriendlyInLine(_b.EyePos, aim))
            {
                if (_b.FireRocket(aim, bestD))
                {
                    Rockets++;
                    Say(rd == WeaponDef.Hat ? "Heavy AT, firing!" : "AT rocket out!");
                    _rocketLaidSince = Now + 1.0;
                    if (_b.Rockets <= 0) Say("I'm out of rockets!");
                }
            }
            return true;
        }
        // No way to hurt it: stay out of its sight.
        if (bestD < 250f) _armorWaryUntil = Now + 1.0;
        if (bestD < 250f && Now > _armorCoverAt && State is not (BotState.TakeCover or BotState.InCover or BotState.Aid))
        {
            _armorCoverAt = Now + 6.0;
            var spot = CoverFinder.Find(_b, v.TopPoint, 14f, _rng);
            if (spot is CoverSpot s)
            {
                SetState(BotState.TakeCover, $"armour! {v.Def.ClassName}");
                Cover = s;
                _b.MoveTo(s.Pos, MoveMode.Sprint);
                return true;
            }
        }
        return false;
    }

    double _armorHuntAt, _armorWaryUntil, _lastShotAt = -99, _supportUntil = -1, _breakCheckAt;
    int _drillSeen = -1;
    bool _drillDone;

    /// <summary>
    /// The squad's drill, as it applies to this man. Returns true when it has taken over what
    /// he does this tick.
    /// </summary>
    bool RunDrill(Threat? t)
    {
        if (Sq == null) return false;
        var dr = Sq.Current;
        if (dr == Drill.None) return false;
        if (_drillSeen != Sq.DrillId) { _drillSeen = Sq.DrillId; _drillDone = false; }
        if (State is BotState.Evade or BotState.Aid) return false;
        var ground = Valley.Current;
        switch (dr)
        {
            case Drill.Indirect:
            {
                if (_drillDone) return State == BotState.TakeCover && !_b.Arrived;
                _drillDone = true;
                // Out of the beaten zone, and off to the side of the line the rounds are walking along.
                var away = (_b.FeetPos - Sq.ImpactAt) with { Y = 0f };
                if (away.LengthSquared() < 1f) away = new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f));
                away = away.Normalized();
                var side = away.Cross(Vector3.Up) * (_rng.Randf() < 0.5f ? -1f : 1f);
                var goal = _b.FeetPos + (away * 0.8f + side * 0.6f).Normalized() * _rng.RandfRange(45f, 70f);
                if (ground != null) goal = ground.Ground(goal);
                SetState(BotState.TakeCover, "incoming! off the impact area");
                Cover = null;
                _b.MoveTo(goal, MoveMode.Sprint);
                return true;
            }
            case Drill.BreakContact:
            {
                // The team whose turn it is bounds back to cover; the other keeps the enemy's heads down.
                if (Sq.MayBound(_b, Now) && Now > _nextBoundAt && State is not BotState.TakeCover)
                {
                    _nextBoundAt = Now + 4.0;
                    var away = (_b.FeetPos - Sq.ContactAt) with { Y = 0f };
                    if (away.LengthSquared() < 1f) away = Vector3.Back;
                    var goal = _b.FeetPos + away.Normalized() * 45f;
                    if (CoverFinder.FindForward(_b, Sq.ContactAt + Vector3.Up * 1.6f, goal, _rng, 36f) is CoverSpot s)
                    {
                        SetState(BotState.TakeCover, "breaking contact");
                        Cover = s;
                        _b.MoveTo(s.Pos, MoveMode.Sprint);
                        Sq.MarkMoving(_b, Now + 6.0);
                        foreach (var m in Sq.Overwatch(_b)) if (t != null) m.Brain.CoverMe(t.Who);
                        return true;
                    }
                }
                // Not our turn to go: hold where we are and keep their heads down. (This used to be undone
                // by the rest of Think a moment later: hold, walk off, hold, walk off, every fifth of a second.)
                if (State is BotState.Advance or BotState.Search or BotState.Investigate or BotState.Flank
                    || (State == BotState.Hold && Note == "covering the withdrawal"))
                {
                    if (State != BotState.Hold) SetState(BotState.Hold, "covering the withdrawal");
                    _holdUntil = Now + 3.0;
                    _b.Stop();
                    if (t != null && !t.Visible && Now > _suppressUntil && Now - t.LastSeen < 8.0) _suppressUntil = Now + _rng.RandfRange(1.5f, 3f);
                    return true;
                }
                return false;
            }
            case Drill.Contact:
            {
                if (_drillDone) return false;
                _drillDone = true;
                int team = Sq.TeamOf(_b);
                if (team < 0) return false;
                if (team == Sq.AssaultTeam)
                {
                    // Go wide round the side the leader called, closing to about half the distance: as a team, to the
                    // team's objective, each man to his place in its line.
                    float d = ((Sq.ContactAt - _b.FeetPos) with { Y = 0f }).Length();
                    if (d < 40f) return false;
                    var goal = Sq.FlankSpotFor(_b);
                    if (ground != null) goal = ground.Ground(goal);
                    SetState(BotState.Flank, $"flanking with {Squad.TeamName(team)}");
                    _drillFlank = true;
                    Prof.Count("flank:drill-start");
                    _b.MoveTo(goal, d > 120f ? MoveMode.Sprint : MoveMode.Run);
                    return true;
                }
                // Support: go firm and put rounds on them while the others move.
                _supportUntil = Now + 30.0;
                if (t != null) _suppressUntil = Now + _rng.RandfRange(3f, 6f);
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Fire discipline. Close (150 m), or returning fire: always. Otherwise it depends on the
    /// squad: one that's in the fight engages out to ~400 m (marksmen, recon, weapons teams and
    /// automatic riflemen further); one that isn't doesn't give itself away shooting at a
    /// figure 400 m off; one holding a point waits until the attackers are close (an ambush,
    /// not a long-range plinking match).
    /// </summary>
    bool MayOpenFire(float d)
    {
        if (d < 150f) return true;
        if (Now - _lastHurt < 8.0 || Now - _lastShotAt < 8.0 || _b.Suppression > 0.15f) return true;
        bool longArm = Role == Role.Marksman || Sq?.Kind is SquadKind.Recon or SquadKind.Weapons || (Role == Role.AutoRifleman && d < 450f);
        if (Sq is { Defend: true } && !Sq.Engaged) return d < (longArm ? 350f : 200f);
        if (Sq?.Engaged == true) return d < (longArm ? 700f : 400f);
        return d < (longArm ? 600f : 300f);
    }

    /// <summary>
    /// Armour we can't see right now but saw or heard in the last 20 s, close by: it hasn't
    /// gone anywhere just because a wall is in the way.
    /// - AT soldiers with a rocket that can hurt it go hunting: a flanking spot with a view
    ///   of where it was.
    /// - Everyone else keeps their head down in cover from where it was, rather than walking
    ///   back out into its sights.
    /// </summary>
    bool RememberedArmor()
    {
        VehicleThreat? mem = null;
        float memD = float.MaxValue;
        foreach (var ev in _b.Senses.Vehicles)
        {
            if (!GodotObject.IsInstanceValid(ev.Who) || ev.Who.Destroyed || (ev.Who.Def.Air && !ev.Who.Landed)) continue;
            if (Now - ev.LastKnown > 20.0 || !ev.Who.Def.Heavy && ev.Who.Def.Turrets.Count == 0) continue;
            float d = ev.LastKnownPos.DistanceTo(_b.EyePos);
            if (d < memD) { memD = d; mem = ev; }
        }
        if (mem == null || memD > 350f) return false;
        var v = mem.Who;
        var rd = _b.RocketDef;
        bool canHurt = rd != null && rd != WeaponDef.Manpad && _b.Rockets > 0 && rd.Pen * 1.1f > v.ArmorToward(_b.EyePos) * 0.8f;
        if (canHurt)
        {
            if (Now > _armorHuntAt && State is not (BotState.Flank or BotState.TakeCover or BotState.Aid or BotState.Evade))
            {
                _armorHuntAt = Now + 10.0;
                if (CoverFinder.FindFlank(_b, mem.LastKnownPos, _rng) is Vector3 fp)
                {
                    SetState(BotState.Flank, $"hunting the {v.Def.ClassName}");
                    Squad.Hunts++;
                    _b.MoveTo(fp, MoveMode.Run);
                    Say($"I'll get that {v.Def.ClassName}!");
                    return true;
                }
            }
            return false;
        }
        if (memD > 160f) return false;
        _armorWaryUntil = Now + 1.0;
        if (Now > _armorCoverAt && State is not (BotState.TakeCover or BotState.InCover or BotState.Aid or BotState.Evade))
        {
            _armorCoverAt = Now + 6.0;
            if (CoverFinder.Find(_b, mem.LastKnownPos + Vector3.Up * 2.5f, 14f, _rng) is CoverSpot s)
            {
                SetState(BotState.TakeCover, $"{v.Def.ClassName} nearby");
                Cover = s;
                _b.MoveTo(s.Pos, MoveMode.Sprint);
                return true;
            }
        }
        return false;
    }

    // ================================================================ drones

    /// <summary>A drone operator at his post flies; if the fight comes to him, he fights (the drones hold or come home on their own).</summary>
    bool DroneWork(Threat? t)
    {
        if (_b.Ops == null || Objective == null) return false;
        var area = Watch ?? Objective.Center;
        bool contact = t is { Visible: true } && _b.FeetPos.DistanceTo(t.LastKnownPos) < 70f;
        // Drones in the air keep flying (and come home) whatever he's doing; new ones only go up from the post.
        _b.Ops.Think(area, launch: InZone && !contact);
        if (contact) return false;
        // Short of drones and the logistics truck's close, or a FOB (its cache has them too): go and get more. (Only
        // the truck used to count, within 150 m, and the teams' posts are rarely that close to it: they ran out.)
        if (_b.Ops.StockLevel < 0.5f && !_b.Ops.Flying)
        {
            Vector3? cache = null;
            float cd = float.MaxValue;
            bool truck = false;
            foreach (var v in Vehicle.All)
                if (!v.Destroyed && v.Def.Kind == VKind.Logistics && v.Team == _b.Team && v.Velocity3.Length() < 1f && v.GlobalPosition.DistanceTo(_b.FeetPos) is var dv && dv < 150f && dv < cd)
                { cd = dv; cache = v.GlobalPosition; truck = true; }
            foreach (var f in Fob.All)
                if (f.Team == _b.Team && f.GlobalPosition.DistanceTo(_b.FeetPos) is var df && df < 400f && df < cd)
                { cd = df; cache = f.GlobalPosition; truck = false; }
            if (cache is Vector3 at)
            {
                if (cd < (truck ? 12f : 20f)) { _b.Resupply(); return false; }
                if (State != BotState.Advance || Now > _fetchAt) { _fetchAt = Now + 3.0; SetState(BotState.Advance, truck ? "fetching drones off the truck" : "fetching drones from the FOB"); GoTo(at); }
                return true;
            }
        }
        if (!InZone || !_b.Ops.Flying) return false;
        if (State != BotState.Hold) SetState(BotState.Hold, "flying a drone");
        _holdUntil = Now + 1.0;
        _b.Stop();
        return true;
    }

    double _droneAt, _droneCallAt, _fetchAt, _droneSince, _droneQuitUntil;
    Drone? _droneTarget;

    /// <summary>
    /// Counter-drone. A quad overhead or an FPV coming in is heard before it's seen: the FPV's
    /// scream from ~150 m, the quad's drone from ~120 m (and it has to be in view to shoot).
    /// - An FPV diving at us, close: get out of its path and down.
    /// - Otherwise shoot it (a rifle round brings any of them down; they're hard to hit), or,
    ///   for some, get under a roof or into the trees where the camera can't see.
    /// A man shooting at us close by comes first.
    /// </summary>
    bool TryAntiDrone(Threat? t)
    {
        if (t is { Visible: true } && _b.FeetPos.DistanceTo(t.LastKnownPos) < 60f) return false;
        if (Now < _droneQuitUntil) return false;
        if (Now < _droneAt && _droneTarget is { Dead: false } && GodotObject.IsInstanceValid(_droneTarget)) return EngageDrone(_droneTarget);
        _droneTarget = null;
        var space = _b.GetWorld3D().DirectSpaceState;
        var eye = _b.EyePos;
        Drone? best = null;
        float bestD = float.MaxValue;
        foreach (var d in Drone.All)
        {
            if (d.Dead || d.Team == _b.Team || !GodotObject.IsInstanceValid(d)) continue;
            float dist = d.GlobalPosition.DistanceTo(eye);
            if (dist > (d.IsFpv ? 150f : 140f) || dist >= bestD) continue;
            if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, d.GlobalPosition, Layers.World | Layers.Trees)).Count > 0) continue;
            best = d;
            bestD = dist;
        }
        if (best == null) { _droneSince = 0; return false; }
        // A quad circling at 100 m is a hard shot: after a quarter of a minute of missing, most give up on it for a while.
        if (_droneSince == 0) _droneSince = Now;
        else if (best.Kind == DroneKind.Quad && Now - _droneSince > 15.0)
        {
            _droneSince = 0;
            _droneQuitUntil = Now + 40.0;
            return false;
        }
        _droneTarget = best;
        _droneAt = Now + 3.0;
        if (Now > _droneCallAt) { _droneCallAt = Now + 8.0; Say(best.IsFpv ? "FPV! FPV incoming!" : "Drone overhead!"); }
        // An FPV coming at us: dive out of its line.
        var rel = eye - best.GlobalPosition;
        if (best.IsFpv && bestD < 45f && best.Vel.Dot(rel.Normalized()) > best.Vel.Length() * 0.8f && State != BotState.Evade)
        {
            var side = rel.Cross(Vector3.Up).Normalized() * (_rng.Randf() < 0.5f ? -1f : 1f);
            SetState(BotState.Evade, "FPV! get down");
            _evadeUntil = Now + 1.5;
            _b.MoveTo(_b.FeetPos + side * 7f, MoveMode.Sprint);
            return true;
        }
        // A quad watching us: about a third of us get under cover it can't see through; the rest shoot.
        if (best.Kind == DroneKind.Quad && (_b.GetInstanceId() % 3 == 0) && State is not (BotState.TakeCover or BotState.InCover) && HideFromAbove())
            return true;
        // High up, a quad is a speck: heard, called out, not worth the rounds until it comes down.
        if (best.Kind == DroneKind.Quad && bestD > 100f) { _droneAt = 0; return false; }
        return EngageDrone(best);
    }

    bool EngageDrone(Drone d)
    {
        var eye = _b.EyePos;
        var p = d.GlobalPosition;
        float dist = p.DistanceTo(eye);
        if (_b.Reloading || _b.Ammo <= 0) return true;
        // Lead it by the round's time of flight.
        float tof = dist / MathF.Max(_b.Def.MuzzleVel, 1f);
        var aim = p + d.Vel * tof;
        _b.Aim.Goal = aim;
        _b.Aim.Tracking = true;
        _b.Aim.GoalVel = d.Vel;
        _b.SetCrouch(false);
        if (State is BotState.Advance or BotState.Search) { SetState(BotState.Hold, "shooting at a drone"); _holdUntil = Now + 3.0; _b.Stop(); }
        if (Mathf.RadToDeg(_b.Aim.Dir.AngleTo(aim - eye)) < 1.2f && Now >= _nextShotAt && !FriendlyInLine(eye, aim))
        {
            if (_burstLeft <= 0) StartBurst(dist, false);
            FireMode = "at a drone";
            Shoot(dist);
        }
        return true;
    }

    /// <summary>Somewhere within 15 m with a roof or a canopy overhead: out of the drone's camera.</summary>
    bool HideFromAbove()
    {
        var space = _b.GetWorld3D().DirectSpaceState;
        var origin = _b.FeetPos;
        for (int k = 0; k < 14; k++)
        {
            float a = _rng.Randf() * Mathf.Tau, r = _rng.RandfRange(2f, 15f);
            var p = origin + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * r;
            if (!CoverFinder.Standable(space, p, origin.Y, out var g)) continue;
            if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(g + Vector3.Up * 1.8f, g + Vector3.Up * 30f, Layers.World | Layers.Trees)).Count == 0) continue;
            SetState(BotState.TakeCover, "hiding from the drone");
            Cover = new CoverSpot { Pos = g, PeekPos = g };
            _b.MoveTo(g, MoveMode.Sprint);
            return true;
        }
        return false;
    }

    /// <summary>What an ammo bearer can do something about: rifle ammo, not a drone team's drones.</summary>
    static float Carried(ICombatant c) => c is Bot { Ops: not null } cb ? (cb.Def.Mags == 0 ? 1f : cb.Mags.Rounds / (float)(cb.Def.MagSize * cb.Def.Mags)) : c.AmmoLevel;

    /// <summary>Ammo bearers top up anyone who comes near, without stopping what they're doing.</summary>
    void PassiveSupply()
    {
        if (Role != Role.Ammo || Now < _nextSupply) return;
        _nextSupply = Now + 4.0;
        foreach (var c in Combatants.All)
            if (c != _b && c.Alive && c.Team == _b.Team && c.AmmoLevel < 0.99f && c.FeetPos.DistanceTo(_b.FeetPos) < 6f && c.Resupply())
            {
                Resupplies++;
                if (c is Player) Hud.Toast($"{_b.Callsign} tossed you ammo", 2f);
            }
    }

    /// <summary>
    /// A medic goes to whoever is hurt worst nearby (or treats themself), an ammo bearer to
    /// whoever is running dry, an engineer on a point it holds digs in. Only when not
    /// actually trading shots: in a firefight they're riflemen first.
    /// </summary>
    bool TryStartAid(Threat? t, bool vis)
    {
        if (Now < _nextAidCheck || vis) return false;
        _nextAidCheck = Now + 1.0;
        // In a lull a medic will still crawl over to someone close; a long run waits for the fight to die down.
        bool hot = t != null && Now - t.LastSeen < 2.0 && t.LastKnownPos.DistanceTo(_b.FeetPos) < 60f;
        if (hot && !(_b.Body.Bleeding > 0.02f && _b.Body.NeedsSelfAid)) return false;
        float reach = t != null && Now - t.LastSeen < 8.0 ? 25f : 60f;

        // Anyone: a bleed you can stop yourself, you stop — straight away if it's arterial.
        // Unless it's spurting, he gets to cover first: finishes the dash, gets behind something, then sees to it. (He
        // stopped dead wherever it came to him, mid-sprint across the open, the man who hit him still shooting.)
        bool arterial = _b.Body.Bleeding > 0.02f;
        if (_b.Body.NeedsSelfAid && !arterial && (State is BotState.TakeCover or BotState.Evade && !_b.Arrived
            || Now - _lastHurt < 2.0 && t != null && !CoverFinder.Protected(_b, _b.FeetPos, ThreatEye(t), _b.Crouched))) return false;
        if (_b.Body.NeedsSelfAid)
        {
            _aidKind = 3;
            _aidUntil = 0;
            _patient = _b;
            SetState(BotState.Aid, "bandaging");
            _b.Stop();
            return true;
        }

        if (Role is Role.Medic or Role.Ammo)
        {
            ICombatant? best = null;
            float bestScore = float.MaxValue;
            foreach (var c in Combatants.All)
            {
                if (c.Dead || c.Team != _b.Team || (c.Downed && Role != Role.Medic) || (Role == Role.Ammo && c == _b) || c.Ride != null) continue;
                if (_claimed.TryGetValue(c, out var by) && by != _b && GodotObject.IsInstanceValid(by) && by.Alive && by.Brain.State == BotState.Aid) continue;
                float d = c.FeetPos.DistanceTo(_b.FeetPos);
                bool squad = c is Bot cb && cb.Squad == Sq || c is Player && Sq != null && Sq.Members.Contains(c);
                // A man down in his own squad, and no fight on: the medic goes further for him. (Out to 60 m only, a
                // squad spread over a village left its downed men to bleed out 65-300 m from the medic.)
                if (d > (squad && c.Downed && reach > 25f ? 150f : reach)) continue;
                float need = Role == Role.Medic ? (_b.Medkits <= 0 ? 0f : MedicNeed(c))
                                                : (Carried(c) < 0.4f ? (0.4f - Carried(c)) * 100f : 0f);
                if (need <= 0f) continue;
                float score = d - need * 0.8f - (squad ? 15f : 0f);
                if (score >= bestScore) continue;
                bestScore = score;
                best = c;
            }
            if (best == null) return false;
            _patient = best;
            _claimed[best] = _b;
            _aidKind = Role == Role.Medic ? 0 : 1;
            _aidUntil = 0;
            SetState(BotState.Aid, best == _b ? "treating myself" : Role == Role.Medic ? $"to treat {best.Callsign}" : $"taking ammo to {best.Callsign}");
            if (best != _b) _b.MoveTo(best.FeetPos, MoveMode.Sprint);
            return true;
        }

        // Buddy aid: one of ours down and bleeding, no medic coming (ours is down himself, or dead, or far off), and it's
        // quiet: the nearest of us goes and stops the bleeding. Only a medic gets him up again. (Nobody but the medic
        // ever went to a man who was down: with him hit too, squads walked past their own wounded, "objective first".)
        if (Sq != null && Role is not (Role.Medic or Role.Ammo) && _b.Ride == null)
            foreach (var c in Sq.Members)
            {
                if (c == _b || !c.Downed || c.Dead || c.Ride != null || !c.Body.NeedsSelfAid || c is not GodotObject go || !GodotObject.IsInstanceValid(go)) continue;
                float d = c.FeetPos.DistanceTo(_b.FeetPos);
                if (d > MathF.Min(40f, reach)) continue;
                if (_claimed.TryGetValue(c, out var by) && by != _b && GodotObject.IsInstanceValid(by) && by.Alive && by.Brain.State == BotState.Aid) continue;
                // A medic of ours on his feet and near enough is coming for him.
                if (Sq.Members.Any(m => m is Bot { Role: Role.Medic, Alive: true } mb && mb.Medkits > 0 && mb.FeetPos.DistanceTo(c.FeetPos) < 150f)) continue;
                // The nearest of us goes, not everyone.
                if (Sq.Members.Any(m => m != _b && m != c && m is Bot { Alive: true } ob && ob.Role is not (Role.Medic or Role.Ammo) && ob.Ride == null && ob.FeetPos.DistanceTo(c.FeetPos) < d - 1f)) continue;
                _patient = c;
                _claimed[c] = _b;
                _aidKind = 4;
                _aidUntil = 0;
                SetState(BotState.Aid, $"to help {c.Callsign}");
                _b.MoveTo(c.FeetPos, MoveMode.Sprint);
                return true;
            }

        if (Role == Role.Engineer && _b.Sandbags > 0 && InZone && Objective is SiteObjective && (Sq?.Kind == SquadKind.Engineer || Sq?.Defend == true)
            && FortifySpot(out _buildAt, out _buildFacing))
        {
            _aidKind = 2;
            _aidUntil = 0;
            _patient = null;
            SetState(BotState.Aid, "digging in");
            _b.MoveTo(_buildAt, MoveMode.Run);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Somewhere on the edge of the point facing the enemy, not too close to other
    /// fortifications, with ground to build on.
    /// </summary>
    bool FortifySpot(out Vector3 at, out Vector3 facing)
    {
        at = facing = Vector3.Zero;
        var obj = Objective!;
        // The threat direction: enemies we know of, else the nearest point we don't own (the commander told us via the order).
        Vector3 toward = Vector3.Zero;
        foreach (var th in _b.Senses.Threats)
            if (th.Who.Alive) toward += (th.LastKnownPos - obj.Center).Normalized();
        if (toward.LengthSquared() < 0.01f && Sq != null) toward = Sq.ThreatAxis;
        toward.Y = 0f;
        if (toward.LengthSquared() < 0.01f) toward = new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f));
        toward = toward.Normalized();
        var right = toward.Cross(Vector3.Up);
        var space = _b.GetWorld3D().DirectSpaceState;
        for (int k = 0; k < 10; k++)
        {
            var p = obj.Center + toward * (obj.Radius * _rng.RandfRange(0.45f, 0.8f)) + right * (obj.Radius * _rng.RandfRange(-0.6f, 0.6f));
            if (!CoverFinder.Standable(space, p, obj.Center.Y, out var g)) continue;
            if (Fortifications.Near(g, 7f)) continue;
            // Somewhere with a view out, not facing straight into a house wall.
            if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(g + Vector3.Up * 1.4f, g + Vector3.Up * 1.4f + toward * 12f, Layers.Solid)).Count > 0) continue;
            at = g;
            facing = toward;
            return true;
        }
        return false;
    }

    /// <summary>How badly someone needs a medic: down beats bleeding beats worn.</summary>
    /// <summary>
    /// What a medic can still do for him: get him up, dress open wounds, top up lost blood. Not tissue damage (a
    /// medkit doesn't mend that), nor a dressed wound still bleeding inside (that's surgery). (Both used to count, so a
    /// man a medic had just treated still "needed" him: the medic treated him over and over, a kit every few
    /// seconds, until he had none left for the next man down.)
    /// </summary>
    static float MedicNeed(ICombatant c)
    {
        var body = c.Body;
        if (c.Downed) return 120f + body.Bleeding * 2000f;
        float open = 0f;
        foreach (var w in body.Wounds) if (!w.Treated) open += w.Bleed;
        if (open > 0.001f) return 40f + open * 2000f;
        return body.Blood < 0.7f ? (0.7f - body.Blood) * 200f : 0f;
    }

    bool ContinueAid(Threat? t, bool vis)
    {
        // Bandaging yourself: a few seconds, crouched, whatever's happening (short of being shot at close up).
        if (_aidKind == 3)
        {
            if (_aidUntil == 0) { _aidUntil = Now + (_b.Body.Bleeding > 0.02f ? 3.0 : 4.5); SoundWorld.I.Emit(Snd.Bag, _b.EyePos, 0f, _b); }
            if (vis && t != null && t.LastKnownPos.DistanceTo(_b.FeetPos) < 25f && _b.Body.Bleeding < 0.02f) { SetState(BotState.Engage, "no time to bandage"); return false; }
            if (Now < _aidUntil) return true;
            _b.Body.SelfAid();
            SelfAids++;
            Say(_b.Body.Bleeding > 0.001f ? "Bandaged, but I need a medic." : "Tourniquet's on, I'm good.");
            SetState(t != null ? BotState.Hold : BotState.Advance, "bandaged");
            _holdUntil = Now + 1.0;
            return true;
        }
        // Contact: drop it and fight. (A medic reviving someone is the exception, unless they're right on top of us.)
        bool reviving = _aidKind == 0 && _patient is { Downed: true } && _aidUntil > 0;
        if ((vis && !(reviving && t != null && t.LastKnownPos.DistanceTo(_b.FeetPos) > 30f)) || (t != null && Now - t.LastSeen < 1.0 && !reviving) || InState > 50f)
        { SetState(t != null ? BotState.Hold : BotState.Advance, "interrupted"); _holdUntil = Now + 1.0; return false; }
        if (_aidKind == 2)
        {
            if (_b.FeetPos.DistanceTo(_buildAt) > 1.2f)
            {
                if (_b.Arrived) _b.MoveTo(_buildAt, MoveMode.Run);
                return true;
            }
            _b.Stop();
            if (_aidUntil == 0) { _aidUntil = Now + 7.0; Say("Digging in here!"); }
            if (Now < _aidUntil) return true;
            var parent = _b.GetParent();
            if (Fortifications.Sandbags(parent, Effects.Ground, _buildAt + _buildFacing * 1.1f, _buildFacing) != null)
            {
                _b.Sandbags--;
                Builds++;
            }
            SetState(BotState.Advance, "sandbags down");
            _hasWaypoint = false;
            return true;
        }
        var who = _patient;
        if (who == null || who.Dead || !GodotObject.IsInstanceValid((GodotObject)who)
            || (_aidKind == 0 ? MedicNeed(who) <= 0f : _aidKind == 4 ? !who.Downed || !who.Body.NeedsSelfAid : Carried(who) >= 0.99f || !who.Alive))
        {
            SetState(BotState.Advance, "done");
            _hasWaypoint = false;
            return true;
        }
        float d = who.FeetPos.DistanceTo(_b.FeetPos);
        if (d > 1.8f)
        {
            if (_b.GoalPos.DistanceTo(who.FeetPos) > 2.5f || _b.Arrived) _b.MoveTo(who.FeetPos, d > 12f ? MoveMode.Sprint : MoveMode.Run);
            _aidUntil = 0;
            return true;
        }
        _b.Stop();
        if (_aidUntil == 0)
        {
            _aidUntil = Now + (_aidKind == 0 ? (who.Downed ? 6.0 : 3.0) : _aidKind == 4 ? 5.0 : 1.2);
            if (who is Player) Hud.Toast(_aidKind == 0 ? $"{_b.Callsign} is patching you up" : _aidKind == 4 ? $"{_b.Callsign} is stopping your bleeding" : $"{_b.Callsign} is handing you ammo", 2f);
            Say(_aidKind == 0 ? (who == _b ? "Patching myself up." : who.Downed ? "Stay with me! I've got you!" : "Hold still, I've got you!") : _aidKind == 4 ? "Hang on, I've got the bleeding! Medic!" : "Here, take these mags!");
            SoundWorld.I.Emit(Snd.Bag, _b.EyePos, 0f, _b);
        }
        if (Now < _aidUntil) return true;
        if (_aidKind == 0) { if (who.Downed) Revives++; who.Heal(55f); _b.Medkits--; Heals++; }
        else if (_aidKind == 4) { who.Body.SelfAid(); Prof.Count("aid:buddy aid"); }
        else if (who.Resupply()) Resupplies++;
        _aidUntil = 0;
        _nextAidCheck = Now; // look for the next one straight away
        SetState(BotState.Advance, "done");
        _hasWaypoint = false;
        return true;
    }

    void EndAid()
    {
        if (_patient != null && _claimed.TryGetValue(_patient, out var by) && by == _b) _claimed.Remove(_patient);
        _patient = null;
        _aidUntil = 0;
    }

    /// <summary>Building: the sounds of a shovel while the sandbags fill.</summary>
    void ActAid()
    {
        if (_aidKind == 2 && _aidUntil > 0 && Now < _aidUntil && Now > _nextShovel)
        {
            _nextShovel = Now + _rng.RandfRange(0.7f, 1.1f);
            SoundWorld.I.Emit(Snd.Build, _b.FeetPos + _buildFacing * 0.8f, 0f, _b);
        }
    }

    /// <summary>Enemy tucked behind cover nearby: flush them out.</summary>
    bool TryGrenade(Threat t)
    {
        if (_b.Grenades <= 0 || Now < _nextNade) return false;
        if (Now - Math.Max(t.LastSeen, t.LastHeard) > 8.0) return false;
        var flat = t.LastKnownPos - _b.FeetPos;
        flat.Y = 0f;
        float d = flat.Length();
        // Someone in a room is what grenades are for: throw sooner, from closer (we're round the wall from it).
        var space = _b.GetWorld3D().DirectSpaceState;
        bool room = Env == EnvKind.Interior || Surroundings.Indoors(space, t.LastKnownPos);
        if (d < (room ? 5f : 9f) || d > 34f) return false;
        _nextNade = Now + 2.0;
        if (_rng.Randf() > 0.3f + _b.P.Aggression * 0.35f + (room ? 0.35f : 0f)) return false;
        if (OwnNear(t.LastKnownPos, 10f)) return false; // not on our own guys
        if (!_b.ThrowGrenadeAt(t.LastKnownPos)) return false;
        _nextNade = Now + 12.0;
        Say("Frag out!");
        if (_b.P.Aggression > 0.45f) _pushAfter = Now + 3.6; // follow it in
        return true;
    }

    /// <summary>
    /// A live grenade we know about is close: get something solid between us and it (a wall, a corner, a
    /// car) if there's anything within a few strides, and get down behind it; otherwise run from it.
    /// Out in the open its fragments reach well past where anyone can run in three seconds.
    /// </summary>
    bool CheckGrenades()
    {
        var space = _b.GetWorld3D().DirectSpaceState;
        foreach (var g in Grenade.Live)
        {
            if (!GodotObject.IsInstanceValid(g) || g.Fuse > 3.2f || g.Smoke) continue; // still in the air, or only smoke
            var gp = g.GlobalPosition;
            float d = gp.DistanceTo(_b.FeetPos);
            if (d > 20f) continue;
            bool noticed = d < 4f || space.IntersectRay(PhysicsRayQueryParameters3D.Create(_b.EyePos, gp + Vector3.Up * 0.1f, 1)).Count == 0;
            if (!noticed) continue;
            var away = _b.FeetPos - gp;
            away.Y = 0f;
            if (away.LengthSquared() < 0.01f) away = Vector3.Right;
            if (State != BotState.Evade)
            {
                Say("Grenade!");
                SetState(BotState.Evade, "grenade!");
                // Pick the escape once: behind something, if it's close enough to reach before it goes off.
                if (CoverFinder.Find(_b, gp + Vector3.Up * 0.3f, MathF.Min(7f, g.Fuse * 4f), _rng) is CoverSpot s)
                {
                    Note = "grenade! into cover";
                    _b.MoveTo(s.Pos, MoveMode.Sprint);
                }
                // Far enough off already (his own, thrown from the open, say): flat on the ground where he is.
                else if (d > 10f) { Note = "grenade! down"; _b.Stop(); }
                else _b.MoveTo(_b.FeetPos + away.Normalized() * 11f, MoveMode.Sprint);
            }
            _evadeUntil = Now + g.Fuse + 0.4;
            return true;
        }
        return State == BotState.Evade && Now < _evadeUntil;
    }

    bool GoToCover(Threat t, float radius, bool urgent)
    {
        Covers++;
        _coverFrom = t.Who;
        var spot = CoverFinder.Find(_b, ThreatEye(t), radius, _rng);
        if (spot is not CoverSpot s) { Prof.Count("cover:search-failed"); return false; }
        if (s.Pos.DistanceTo(_b.FeetPos) < 0.7f)
        {
            SetState(BotState.InCover, "in cover");
            Cover = s;
            _hideUntil = Now + _rng.RandfRange(0.15f, 0.5f);
            return true;
        }
        SetState(BotState.TakeCover, urgent ? "breaking contact" : "moving to cover");
        Cover = s;
        _b.MoveTo(s.Pos, urgent || _b.FeetPos.DistanceTo(s.Pos) > 5f ? MoveMode.Sprint : MoveMode.Run);
        return true;
    }

    // ================================================================ Act

    bool _buddyHold;
    Vector3 _areaPoint;
    double _areaPickAt;

    /// <summary>In the support team during an assault, with nobody in sight: fire on the objective itself.</summary>
    bool AreaFire => Sq != null && _b.Ammo > _b.Def.MagSize * 0.3f
                     && (Sq.SuppressUntil > Now && Sq.SuppressAt.DistanceTo(_b.FeetPos) < 400f
                         || Objective != null && Sq.FiringInSupport(_b) && FromObjective(_b.FeetPos) < 260f);

    public void Act(float dt)
    {
        _buddyHold = false;
        var t = Target;
        if (t != null && !t.Who.Alive) { Target = t = null; }
        bool vis = t is { Visible: true };

        // Reaction: seeing someone appear isn't the same as having your crosshair on them.
        if (vis && (!_wasVisible || t != _reactTarget))
        {
            float off = Mathf.RadToDeg(_b.Aim.Dir.AngleTo(t!.Who.ChestPos - _b.EyePos));
            // Already aimed where they were (popping up, re-peeking): almost no delay.
            bool preAimed = off < 10f && t.GapBeforeVisible < 8.0;
            float react = t.GapBeforeVisible < 1.0 ? 0.06f
                : preAimed ? 0.1f
                : _b.P.ReactionS * (1f + _b.Suppression * 0.6f) + (off > 45f ? 0.12f : 0f);
            _reactAt = Now + react;
            _reacted = false;
            _reactTarget = t;
        }
        _wasVisible = vis;
        _partT -= dt;
        if (vis && _partT <= 0f)
        {
            _partT = 0.1f;
            ChooseAimPart(t!);
        }
        if (vis && !_reacted && Now >= _reactAt)
        {
            _reacted = true;
            _b.Aim.Flick(AimPoint(t!), _rng);
        }

        switch (State)
        {
            case BotState.Advance: ActAdvance(); break;
            case BotState.TakeCover when _b.Arrived && Note == RushNote:
                // The end of a rush: down, and back to firing.
                SetState(BotState.Engage, "down, firing");
                _crouchLos = _proneLos = true; // (re-checked in half a second)
                _exposedCrouched = true;
                break;
            case BotState.TakeCover:
                if (_b.Arrived && Cover == null && Note.StartsWith("incoming"))
                {
                    // Off the impact area with nothing picked to get behind: flat where he is for a few seconds. (He
                    // was "in cover" behind nothing, then straight up and walking about in the open while rounds fell.)
                    _hitTheDirtUntil = Now + _rng.RandfRange(4f, 8f);
                    SetState(BotState.Hold, "down, off the impact area");
                    _holdUntil = _hitTheDirtUntil;
                }
                else if (_b.Arrived)
                {
                    SetState(BotState.InCover, "in cover");
                    _hideUntil = Now + _rng.RandfRange(0.15f, 0.6f);
                }
                break;
            case BotState.InCover: ActCover(t, vis); break;
            case BotState.Engage: ActEngage(t, vis); break;
            case BotState.Aid: ActAid(); break;
            case BotState.Hold:
                _b.Stop();
                // Up for a look now and then, when there's someone to look for: a few seconds scanning, then down for
                // a while. Not with nobody about, nor for a man flying a drone or dressing his own wound. (Every 1.5-3 s,
                // whatever: men holding with no enemy for a kilometre, drone operators at their screens, bobbed up and
                // down like targets on a range.)
                if (!vis && t != null && Now > _popAt && !Note.Contains("drone"))
                {
                    _popUntil = Now + _rng.RandfRange(2f, 4f);
                    _popAt = _popUntil + _rng.RandfRange(6f, 12f);
                }
                break;
        }
        if (vis && Now > _losCheckAt)
        {
            _losCheckAt = Now + 0.5;
            var space = _b.GetWorld3D().DirectSpaceState;
            var them = Combatants.PointOn(t!.Who, Combatants.Part.UpperChest);
            _crouchLos = space.IntersectRay(PhysicsRayQueryParameters3D.Create(_b.FeetPos + Vector3.Up * 1.17f, them, 1)).Count == 0;
            var low = _b.FeetPos + Vector3.Up * 0.38f;
            // From the ground he has to see them, and they can't be too far above or below him to aim at lying down.
            float pitch = Mathf.RadToDeg(MathF.Asin(Mathf.Clamp((them - low).Normalized().Y, -1f, 1f)));
            _proneLos = pitch > -18f && pitch < 23f && space.IntersectRay(PhysicsRayQueryParameters3D.Create(low, them, 1)).Count == 0;
            _exposedCrouched = !CoverFinder.Protected(_b, _b.FeetPos, t.Who.EyePos, true);
        }
        // Down on the ground and the ground in front is in the way of the barrel: up onto a knee for a while.
        if (_b.Prone && vis && _holdFire && _reacted) _proneBlockedUntil = Now + 6.0;

        _b.SetStance(WantStance(t, vis));
        WantsAds = State switch
        {
            BotState.Advance or BotState.TakeCover or BotState.Evade or BotState.Flank or BotState.Aid => false,
            BotState.InCover => _peeking,
            _ => vis || _b.Mode == MoveMode.Walk || _b.Arrived,
        };
        UpdateAim(t, vis);
        TryShoot(t, vis);
    }

    void ActAdvance()
    {
        // Heading for a vehicle (ours, or the ride come for us), or shuffling round the tube: that's where he's going, and
        // Board steers him. (The march, the crossing and the wait for the squad below re-ordered or stopped him every
        // frame: a squad leader crawled at 0.4 m/s beside his helicopter, and it left without him.)
        if (Note is "to the transport" or "to the vehicle" or ToTheTube) return;
        // Our ride's on its way: wait for it here (the squad holds round the leader) rather than walk away from it.
        // However far off it is (the pickup was only started because it's worth the wait), but only while it's
        // actually getting closer: a ride that's stuck, or busy elsewhere, gets 25 s to show it's coming, then the
        // squad walks on for a while (the ride keeps heading for the leader, and picks them up on the way). (Squads
        // walked on until the ride was within 400 m, so a carrier sent from the far side of the map chased them at
        // little more than their own walking pace; and a squad whose IFV was stuck or off on overwatch stood in the
        // street for minutes on end.)
        if (Sq != null && Sq.Leader == _b && Sq.Transport is { Destroyed: false, Boarding: false } ride && _b.Ride == null
            && ride.GlobalPosition.DistanceTo(_b.FeetPos) is > 30f and var rideGap && !Sq.Engaged && Now > _rideGiveUpUntil)
        {
            if (_rideWaitSince < 0 || rideGap < _rideWaitGap - 20f) { _rideWaitSince = Now; _rideWaitGap = rideGap; }
            else if (Now - _rideWaitSince > 25.0)
            {
                _rideGiveUpUntil = Now + 45.0;
                _rideWaitSince = -1;
                Prof.Count("squad:ride not coming, walking on");
                Say("Ride's not coming. On me, we walk.");
                Note = "";
                return;
            }
            Sq.RideWaitAt = Now;
            if (State == BotState.Advance && _b.Moving) { _b.Stop(); _hasWaypoint = false; Note = "waiting for the ride"; }
            return;
        }
        _rideWaitSince = -1;
        // Leading the squad over a danger area: up to the near edge, then across at a sprint, then hold on the far side.
        if (Sq != null && Sq.Leader == _b && Sq.CrossingGoal is Vector3 cg)
        {
            if (_b.FeetPos.DistanceTo(cg) > 2.5f)
            {
                if (!_hasWaypoint || _waypoint.DistanceTo(cg) > 1f || _b.Arrived)
                {
                    _waypoint = cg;
                    _hasWaypoint = true;
                    _pausing = false;
                    _following = false;
                    _b.MoveTo(cg, Sq.Cross == Crossing.Halt ? MoveMode.Run : MoveMode.Sprint);
                }
            }
            else { _b.Stop(); _hasWaypoint = false; }
            return;
        }
        // Leading, and the squad's strung out behind: wait for them to close up (see Squad.StrungOut).
        if (Sq != null && Sq.Leader == _b && !InZone && Sq.StrungOut(_b))
        {
            if (_b.Moving) { _b.Stop(); _hasWaypoint = false; }
            Note = "waiting for the squad";
            return;
        }
        if (_following && Now > _followAt)
        {
            // Keep station on the leader: re-aim at the slot once it has drifted.
            _followAt = Now + 1.0;
            if (Sq?.SlotFor(_b) is Vector3 slot && (OnLeader || (!InZone && !LeaderInZone)))
            {
                if (slot.DistanceTo(_waypoint) > 5f) Follow(slot);
            }
            else { _following = false; _hasWaypoint = false; }
        }
        if (!_hasWaypoint)
        {
            PickWaypoint();
            return;
        }
        if (!_b.Arrived) return;
        if (!_pausing)
        {
            _pausing = true;
            _pauseStart = Now;
            _pauseUntil = Now + (Sweeping ? _rng.RandfRange(4f, 6f)
                : _following ? 0.0
                : InZone ? _rng.RandfRange(3f, 9f) // holding the objective
                : _rng.RandfRange(0.3f, 1.4f) * (1.3f - _b.P.Aggression * 0.6f));
        }
        else if (Now > _pauseUntil)
        {
            _pausing = false;
            PickWaypoint();
        }
    }

    /// <summary>
    /// Early on, push toward the enemy side. After a while with no contact, sweep:
    /// prefer places we haven't checked lately, so a 1v1 can't stall forever.
    /// </summary>
    bool LeaderInZone => Sq?.Leader is ICombatant l && Objective != null && FromObjective(l.FeetPos) < Objective.Radius;
    /// <summary>A player squad leader has called the squad onto them.</summary>
    bool OnLeader => Sq is { FollowPlayer: true } && Sq.Leader is Player;

    void Follow(Vector3 slot)
    {
        _following = true;
        _waypoint = slot;
        _hasWaypoint = true;
        _pausing = false;
        float gap = slot.DistanceTo(_b.FeetPos);
        // Falling behind: sprint to catch up. Close: match the leader's pace.
        var mode = gap > 10f || Sq?.CrossingNow == true && gap > 3f ? MoveMode.Sprint : MoveMode.Run;
        // Clear line to the slot: just go. Otherwise trail the leader along their route
        // (it's already been paid for), and only plan our own now and then.
        if (_b.CanWalkStraight(slot)) _b.MoveTo(slot, mode);
        else if (Sq?.Leader is Bot lead && lead.PathPoints.Length > 0 && !lead.Arrived && _b.AdoptPath(lead, mode)) { }
        else _b.MoveToLoose(slot, mode, 4.0);
    }

    void PickWaypoint()
    {
        _following = false;
        if (Objective != null && AssaultPlan()) return;
        if (Sq != null && Sq.Leader == _b && Sq.LeaderShouldWait(_b)) { _b.Stop(); _hasWaypoint = false; return; }
        if (Objective != null)
        {
            // On the way there, keep formation on the squad leader rather than all taking the same path.
            if ((OnLeader || (!InZone && !LeaderInZone)) && Sq?.SlotFor(_b) is Vector3 slot)
            {
                Follow(slot);
                return;
            }
            GoTo(Objective.PointFor(_b, _rng));
            return;
        }
        if (Waypoints.Count == 0) return;
        var me = _b.FeetPos;
        var enemyDir = _b.Team == 0 ? Vector3.Right : Vector3.Left;
        bool sweeping = Sweeping;
        if (sweeping && !_checkedSpawn)
        {
            // Out of ideas: people go and check the other team's spawn.
            _checkedSpawn = true;
            GoTo(new Vector3(_b.Team == 0 ? 86f : -86f, 0f, _rng.RandfRange(-15f, 15f)));
            return;
        }
        Vector3 best = me;
        float bestScore = float.MinValue;
        foreach (var w in Waypoints)
        {
            if (w.DistanceTo(me) < 6f) continue;
            float fromVisited = 999f;
            foreach (var v in _visited) fromVisited = MathF.Min(fromVisited, v.DistanceTo(w));
            float score = sweeping
                ? _rng.Randf() * 20f + MathF.Min(fromVisited, 80f) * 0.4f + (w - me).Dot(enemyDir) * 0.1f - (w - me).Length() * 0.1f
                : _rng.Randf() * 25f + (w - me).Dot(enemyDir) * 0.35f * (0.5f + _b.P.Aggression) - (w - me).Length() * 0.25f;
            if (fromVisited < 8f) score -= 40f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = w;
        }
        _visited.Enqueue(best);
        if (_visited.Count > 6) _visited.Dequeue();
        GoTo(best + new Vector3(_rng.RandfRange(-2f, 2f), 0f, _rng.RandfRange(-2f, 2f)));
    }

    /// <summary>
    /// The squad leader running a deliberate attack on an enemy-held point: halt at the ORP and
    /// form up; send support to its firing position and take the assault team to the line of
    /// departure; when support is set, go. Returns true when it has decided the leader's move.
    /// </summary>
    bool AssaultPlan()
    {
        if (Sq == null || Sq.Leader != _b || Sq.Defend || Objective is not SiteObjective so) return false;
        float d = FromObjective(_b.FeetPos);
        switch (Sq.Phase)
        {
            case AssaultPhase.None:
            {
                bool hostile = Squad.Hostile?.Invoke(so.Site, _b.Team) == true;
                if (!hostile || Sq.RecentlyAssaulted(Objective) || Sq.Alive < 4 || d > 340f || d < 150f) return false;
                Sq.BeginOrp(_b.FeetPos, Objective);
                Say("ORP here. Security out, form up on me.");
                _b.Stop();
                _hasWaypoint = false;
                return true;
            }
            case AssaultPhase.Orp:
            {
                int near = Sq.Members.Count(m => m.Alive && m.FeetPos.DistanceTo(Sq.OrpAt) < 30f);
                // A few seconds at least: security out, the leader looks and gives his orders.
                if ((near >= Sq.Alive * 0.75f && Sq.PhaseAge > 8.0) || Sq.PhaseAge > 30.0)
                {
                    Sq.BeginDeploy(_b.GetWorld3D().DirectSpaceState, Objective, _rng);
                    Say($"{Squad.TeamName(Sq.SbfTeam)}, support by fire from the {Comms.Bearing(so.Site.Center, Sq.SbfAt)}. {Squad.TeamName(Sq.SbfTeam ^ 1)}, on me to the line of departure.");
                    GoTo(Sq.LdAt);
                    return true;
                }
                _b.Stop();
                _hasWaypoint = false;
                return true;
            }
            case AssaultPhase.Deploy:
            {
                bool atLd = _b.FeetPos.DistanceTo(Sq.LdAt) < 10f;
                // Go when support is set, or at the latest after 90 s whatever's holding things up.
                if ((atLd && (Sq.SupportSet || Sq.PhaseAge > 50.0)) || Sq.PhaseAge > 90.0)
                {
                    Sq.BeginAssault();
                    Say($"Support, open fire! {Squad.TeamName(Sq.SbfTeam ^ 1)}, on line — assault, go!");
                    GoTo(Objective.PointFor(_b, _rng));
                    return true;
                }
                if (!atLd) GoTo(Sq.LdAt);
                else { _b.Stop(); _hasWaypoint = false; }
                return true;
            }
            case AssaultPhase.Assault:
                if (InZone || Sq.PhaseAge > 90.0) Sq.EndAssault();
                return false;
        }
        return false;
    }

    void GoTo(Vector3 w)
    {
        _waypoint = w;
        _hasWaypoint = true;
        _pausing = false;
        // Sprint between positions; careful types jog. A squad leader on a long move jogs
        // while the squad is strung out behind, so they can keep up.
        bool far = w.DistanceTo(_b.FeetPos) > 15f;
        bool waitForSquad = Sq != null && Sq.Leader == _b && Sq.Alive > 1 && !InZone && SquadStrungOut();
        // Built-up ground with a fight about: no sprinting down the street; inside, walk with the gun up.
        bool hot = Now - _lastContact < 40.0 || InZone;
        if (hot && Env == EnvKind.Interior) { _b.MoveTo(w, MoveMode.Walk); return; }
        if (hot && Env == EnvKind.Urban) { _b.MoveTo(w, MoveMode.Run); return; }
        _b.MoveTo(w, far && _b.P.Aggression > 0.35f && !waitForSquad ? MoveMode.Sprint : MoveMode.Run);
    }

    bool SquadStrungOut()
    {
        foreach (var m in Sq!.Members)
            if (m != _b && m.Alive && m.FeetPos.DistanceTo(_b.FeetPos) > 30f) return true;
        return false;
    }

    void ActCover(Threat? t, bool vis)
    {
        if (Cover is not CoverSpot c) { SetState(BotState.Advance, "no cover"); return; }
        if (t == null) return;
        if (!_peeking)
        {
            _b.LeanTarget = 0f;
            // Back to the spot if we've been pushed off it (asking every tick while already there costs a sweep test each time).
            if (!_b.Arrived || _b.FeetPos.DistanceTo(c.Pos) > 0.6f) _b.MoveTo(c.Pos, MoveMode.Walk);
            if (Now > _hideUntil && !_b.Reloading && _b.Ammo > 0 && _b.Suppression < 0.75f)
            {
                _peeking = true;
                // Half the time a quick pop — up, a couple of rounds, back down.
                _popPeek = _rng.Randf() < 0.5f;
                _peekUntil = Now + (_popPeek ? _rng.RandfRange(0.5f, 1.0f) : _rng.RandfRange(1.2f, 3.2f) * (0.7f + _b.P.Aggression * 0.6f));
                // Keeping their heads down: up for the whole string of fire.
                if (Now < _suppressUntil) _peekUntil = Math.Max(_peekUntil, _suppressUntil);
                // Pre-fire: spray where they were as we come out, before we can even see them.
                if (!vis && Now - t.LastSeen < 3.0 && _rng.Randf() < 0.35f + _b.P.Aggression * 0.4f)
                    _prefireUntil = Now + 0.6;
            }
        }
        else
        {
            if (c.Side && c.LeanDir == 0f && (!_b.Arrived || _b.FeetPos.DistanceTo(c.PeekPos) > 0.6f)) _b.MoveTo(c.PeekPos, MoveMode.Walk);
            _b.LeanTarget = c.LeanDir;
            bool bail = Now > _peekUntil || _b.Ammo == 0 || _b.Reloading || _b.Suppression > 0.8f + _b.P.Courage * 0.15f;
            if (bail)
            {
                _peeking = false;
                _hideUntil = Now + (_popPeek ? _rng.RandfRange(0.4f, 1.1f) : _rng.RandfRange(0.5f, 1.8f)) * (1.3f - _b.P.Aggression * 0.6f);
            }
        }
    }

    void ActEngage(Threat? t, bool vis)
    {
        if (t == null || !vis)
        {
            _b.Stop();
            return;
        }
        float d = _b.FeetPos.DistanceTo(t.Who.FeetPos);
        if (d > 30f)
        {
            _b.Stop();
            return;
        }
        // Buddy pairs: up close, one of the pair moves while the other fires, and they swap.
        var buddy = Sq?.BuddyOf(_b);
        if (buddy != null && buddy.FeetPos.DistanceTo(_b.FeetPos) < 25f && buddy is Bot bb && bb.Brain.State is BotState.Engage or BotState.InCover or BotState.Hold)
        {
            if (!Sq!.IsMover(_b, Now, _rng))
            {
                _b.StrafeDir = null;
                _b.Stop();
                return; // firing: steady, feet planted
            }
            _buddyHold = true; // moving: the buddy's doing the shooting
        }
        // Up close people don't stand still: short strafes, and the aggressive ones close the distance.
        // One side, kept for a second or three (the other way only when he's stuck or has been at it a while). (A
        // fresh coin toss every 0.3-0.9 s had him reversing before he'd got anywhere: a video-game dodge.)
        if (_strafe != Vector3.Zero && Now > _strafeUntil - 1.0 && _b.Vel.LengthSquared() < 0.1f) { _strafeSide = -_strafeSide; _strafeUntil = Now; }
        if (Now > _strafeUntil)
        {
            _strafeUntil = Now + _rng.RandfRange(1.5f, 3f);
            var to = t.Who.FeetPos - _b.FeetPos;
            to.Y = 0f;
            to = to.Normalized();
            if (_strafeSide == 0 || _rng.Randf() < 0.25f) _strafeSide = _rng.Randf() < 0.5f ? -1 : 1;
            var perp = new Vector3(-to.Z, 0f, to.X) * _strafeSide;
            float push = d > 7f ? _b.P.Aggression - 0.25f : -0.3f;
            _strafe = _rng.Randf() < 0.12f ? Vector3.Zero : (perp + to * push).Normalized();
        }
        _b.StrafeDir = _strafe;
    }

    /// <summary>
    /// On his feet, on a knee or flat on the ground. Out in the open at range with nothing to get behind,
    /// a soldier fights lying down: a fraction of the target, much harder to pick out, and a steadier aim;
    /// but slow to get up and move, and blind over any fold in the ground. So:
    /// - fighting in the open (not in a street or a room) beyond ~40 m with no cover: prone, if he can still
    ///   see them from down there (else a knee); pinned down there with no cover: prone;
    /// - an observation or overwatch post in the open: prone;
    /// - a mortar bomb whistling in close: flat, wherever he is (a grenade with nowhere to hide behind too);
    /// - otherwise as before: a knee behind cover and at range, on his feet to move and up close.
    /// </summary>
    Posture WantStance(Threat? t, bool vis)
    {
        float d = t != null ? _b.FeetPos.DistanceTo(t.LastKnownPos) : 999f;
        bool open = Env is EnvKind.Open or EnvKind.Forest; // a street or a floor: lying down just gets you trodden on and shot from above
        bool mayLie = open && Now > _proneBlockedUntil;
        if (Now < _hitTheDirtUntil && State != BotState.Evade) return Posture.Prone;
        switch (State)
        {
            case BotState.InCover:
                return !_peeking || (!(Cover?.Low ?? false) && !(Cover?.Side ?? false)) ? Posture.Crouch : Posture.Stand;
            case BotState.Engage:
                if (mayLie && d > 40f && _exposedCrouched && (_proneLos || _b.Suppression > 0.7f)) return Posture.Prone;
                return d > 30f && _crouchLos ? Posture.Crouch : Posture.Stand; // don't duck out of your own sight line
            case BotState.Hold:
                if (Cover != null || d <= 18f) return Posture.Stand;
                // Down on the ground he stays down (no bobbing up to look: getting up takes a second each way);
                // pinned in the open, he gets down. Otherwise a knee, up now and then for a look.
                if (mayLie && d > 40f && (_b.Prone || _b.Suppression > 0.6f)) return Posture.Prone;
                return Now < _popUntil ? Posture.Stand : Posture.Crouch;
            case BotState.Aid:
                return _aidUntil > 0 ? Posture.Crouch : Posture.Stand; // kneeling to work
            case BotState.Evade:
                // Down behind it; or, with nothing to get behind, flat, as low as can be, till it goes off.
                return !_b.Arrived ? Posture.Stand : Note.EndsWith("cover") ? Posture.Crouch : Posture.Prone;
            case BotState.Advance when Note == AssistantGunner || Note == ToTheTube && _nearTube:
                return Posture.Crouch; // kneeling at the tube, and shuffling round it on a knee
            case BotState.Advance:
                // At an observation post, stay low: a head on a ridgeline is what gets seen first.
                return Overwatch && InZone && _pausing ? (mayLie ? Posture.Prone : Posture.Crouch) : Posture.Stand;
            default:
                return Posture.Stand;
        }
    }

    /// <summary>
    /// A mortar bomb whistling down close by (it's heard for the last few seconds of its fall): hit the
    /// dirt where you stand; there's no outrunning it. Under a roof, stay put. The squad gets off the
    /// impact area once it's landed (the indirect-fire drill).
    /// </summary>
    bool CheckIncoming()
    {
        if (Now < _hitTheDirtUntil) { _b.Stop(); return true; }
        if (Ballistics.Incoming.Count == 0 || Env == EnvKind.Interior) return false;
        foreach (var (at, when) in Ballistics.Incoming)
        {
            double left = when - Now;
            if (left < -0.2 || left > 3.2 || at.DistanceTo(_b.FeetPos) > 45f) continue;
            _hitTheDirtUntil = when + _rng.RandfRange(0.6f, 1.5f);
            Prof.Count("incoming:hit-the-dirt");
            if (State is not (BotState.Hold or BotState.InCover or BotState.Engage)) SetState(BotState.Hold, "incoming! down!");
            _holdUntil = _hitTheDirtUntil;
            _b.Stop();
            Say(_rng.Randf() < 0.5f ? "Incoming!" : "Get down!");
            return true;
        }
        return false;
    }

    void UpdateAim(Threat? t, bool vis)
    {
        var aim = _b.Aim;
        if (vis && _reacted)
        {
            aim.Goal = AimPoint(t!);
            aim.GoalVel = t!.Who.Vel;
            aim.Tracking = true;
            return;
        }
        aim.Tracking = false;
        if (vis) return; // still reacting: eyes on them, gun not yet
        if (t == null && AreaFire)
        {
            if (Now > _areaPickAt)
            {
                _areaPickAt = Now + _rng.RandfRange(1.5f, 3f);
                // The leader's "suppress there", else the objective in the assault.
                bool ordered = Sq!.SuppressUntil > Now;
                var centre = ordered ? Sq.SuppressAt : Objective!.Center;
                float a = _rng.Randf() * Mathf.Tau, r = MathF.Sqrt(_rng.Randf()) * (ordered ? 7f : Objective!.Radius * 0.6f);
                _areaPoint = centre + new Vector3(MathF.Cos(a) * r, _rng.RandfRange(0.5f, ordered ? 2f : 4f), MathF.Sin(a) * r);
            }
            aim.Goal = _areaPoint;
            return;
        }
        bool movingFar = _b.Mode == MoveMode.Sprint || State == BotState.Evade || (State == BotState.TakeCover && _b.RemainingDistance > 6f);
        if (t != null && State != BotState.Advance && !movingFar)
        {
            aim.Goal = SuppressPoint(t); // pre-aim where they were
            return;
        }
        if (State == BotState.Advance && _pausing)
        {
            // Paused: scan toward the enemy side — or, when hunting for the last of them, all the way round.
            var enemy = _b.Team == 0 ? Vector3.Right : Vector3.Left;
            if (Watch is Vector3 w && InZone)
            {
                // Observation post: glass the objective, sweeping a little either side.
                var wd = w - _b.FeetPos;
                wd.Y = 0f;
                if (wd.LengthSquared() > 1f) enemy = wd.Normalized();
                aim.Goal = _b.EyePos + enemy.Rotated(Vector3.Up, Scan(0.45f)) * 40f + Vector3.Down * 2f;
                return;
            }
            if ((_b.LookOut is not Vector3 lo0 || !InZone) && Sq?.SectorFor(_b, InZone) is Vector3 sec)
            {
                // My sector: a slow sweep either side of it.
                aim.Goal = _b.EyePos + sec.Rotated(Vector3.Up, Scan(0.35f)) * 25f + Vector3.Down * 1f;
                return;
            }
            if (_b.LookOut is Vector3 lo && InZone && lo.LengthSquared() > 0.01f)
            {
                // At a window or on a roof: watch out of it.
                aim.Goal = _b.EyePos + lo.Normalized().Rotated(Vector3.Up, Scan(0.5f)) * 30f + Vector3.Down * 1.5f;
                return;
            }
            if (Objective != null)
            {
                // In the zone, watch outward; on the way there, watch ahead toward it.
                var d = InZone ? _b.FeetPos - Objective.Center : Objective.Center - _b.FeetPos;
                d.Y = 0f;
                if (d.LengthSquared() > 1f) enemy = d.Normalized();
            }
            float a = Sweeping ? (float)(Now - _pauseStart) * 1.2f : Scan(1.1f);
            aim.Goal = _b.EyePos + enemy.Rotated(Vector3.Up, a) * 20f;
            return;
        }
        aim.Goal = _b.LookAheadPoint();
    }

    /// <summary>
    /// Keeping watch, as an angle off the way he's watching (within +/-range): he looks somewhere for a few seconds,
    /// then somewhere else, each man on his own time. (Everyone on a side swept to the same sine wave: thirty men
    /// turning left and right together, in step.)
    /// </summary>
    float Scan(float range)
    {
        if (Now > _scanNextAt)
        {
            _scanNextAt = Now + _rng.RandfRange(1.5f, 4.5f);
            _scanAngle = _rng.RandfRange(-1f, 1f);
        }
        return _scanAngle * range;
    }

    void TryShoot(Threat? t, bool vis)
    {
        if (t == null && AreaFire && !_b.Reloading && State != BotState.Evade)
        {
            // Support by fire: rounds onto the buildings and cover the assault is going into.
            var eye0 = _b.EyePos;
            if (Mathf.RadToDeg(_b.Aim.Dir.AngleTo(_areaPoint - eye0)) < 3f && Now >= _nextShotAt && !FriendlyInLine(eye0, _areaPoint) && _b.ShotReaches(_areaPoint, 10f))
            {
                float d0 = eye0.DistanceTo(_areaPoint);
                if (_burstLeft <= 0) StartBurst(d0, false);
                FireMode = "support by fire";
                Shoot(d0);
            }
            return;
        }
        if (t == null || _b.Reloading || _b.Ammo <= 0 || State == BotState.Evade) { _burstLeft = 0; return; }
        if (State == BotState.InCover && !_peeking) return;
        if (_b.Mode == MoveMode.Sprint && _b.Moving) return;
        var eye = _b.EyePos;

        if (vis)
        {
            if (!_reacted || _holdFire || _buddyHold) { _burstLeft = 0; return; }
            var pt = AimPoint(t);
            float d = eye.DistanceTo(pt);
            if (!MayOpenFire(d)) { _burstLeft = 0; FireMode = "holding fire"; return; }
            float err = Mathf.RadToDeg(_b.Aim.Dir.AngleTo(pt - eye));
            AimErrorDeg = err;
            bool panic = Panicked;
            // A head is a much smaller target than a chest: wait for the aim to be good enough for it.
            float half = _aimPart switch { Combatants.Part.Head => 0.11f, Combatants.Part.UpperChest => 0.2f, Combatants.Part.Hip => 0.22f, _ => 0.26f };
            float tol = Mathf.RadToDeg(MathF.Atan2(half, d)) * Mathf.Lerp(2.6f, 1.3f, _b.P.Skill) * (d < 12f ? 1.6f : 1f) * (panic ? 1.5f : 1f);
            bool keepSpraying = _burstLeft > 0 && _autoBurst && err < tol * (panic ? 4f : 3f);
            if (err > tol && !keepSpraying) return;
            if (Now < _nextShotAt || FriendlyInLine(eye, pt)) return;
            if (_burstLeft <= 0) StartBurst(d, panic);
            FireMode = "aimed";
            Shoot(d);
            return;
        }

        // Not visible: suppressing their cover, or pre-firing the angle as we peek.
        bool suppress = Now < _suppressUntil && Now - t.LastSeen < SuppressFor && _b.Ammo > _b.Def.MagSize * 0.25f;
        bool prefire = Now < _prefireUntil;
        if (!suppress && !prefire) { if (_burstLeft > 0 && !_autoBurst) _burstLeft = 0; return; }
        var sp = SuppressPoint(t);
        if (Mathf.RadToDeg(_b.Aim.Dir.AngleTo(sp - eye)) > 4f) return;
        // Wait until fully leaned out, and check the round's real path clears our own cover. It needn't reach the
        // very spot: fire on a position 200 m off that kicks up the dirt in front of it is doing its job. (Held to
        // within 4 m at any range, much of the fire men meant to put down wasn't: a fold in the ground short of
        // the enemy stopped it.)
        if (!_b.LeanSettled || Now < _nextShotAt || FriendlyInLine(eye, sp)) return;
        if (!_b.ShotReaches(sp, suppress ? MathF.Max(4f, eye.DistanceTo(sp) * 0.05f) : 4f)) return;
        if (_burstLeft <= 0)
        {
            // A machine gun keeps them down with bursts; a rifleman with steady single shots at a position he can't
            // see (automatic from a rifle at that range is noise and an empty magazine), unless it's close.
            // Keeping it up for minutes, not seconds: a machine gun's sustained rate (a burst every few seconds,
            // ~80 rounds a minute) and a rifleman's (a shot every second or two), not their rapid rates.
            bool mg = Role == Role.AutoRifleman;
            _autoBurst = _b.Def.AutoCapable && (mg || prefire || eye.DistanceTo(sp) < 60f);
            if (prefire) { _burstLeft = _autoBurst ? _rng.RandiRange(3, 6) : _rng.RandiRange(1, 2); _burstPause = 0.1f; }
            else if (mg) { _burstLeft = _rng.RandiRange(4, 8); _burstPause = _rng.RandfRange(1.5f, 3.5f); }
            else if (_autoBurst) { _burstLeft = _rng.RandiRange(3, 6); _burstPause = _rng.RandfRange(0.5f, 1.2f); }
            else { _burstLeft = 1; _burstPause = _rng.RandfRange(0.8f, 2.2f); }
        }
        FireMode = prefire ? "prefire" : "suppress";
        Shoot(eye.DistanceTo(sp));
    }

    void Shoot(float d)
    {
        if (!_b.TryFire(d)) return;
        _burstLeft--;
        _nextShotAt = _burstLeft > 0
            ? (_autoBurst ? Now : Now + _rng.RandfRange(0.14f, 0.28f))
            : Now + _burstPause;
    }

    /// <summary>
    /// Fire discipline: long bursts up close, full-auto strings mid-range, aimed
    /// shots far out — and when scared or hit, longer and looser.
    /// </summary>
    void StartBurst(float d, bool panic)
    {
        if (Role == Role.AutoRifleman)
        {
            // A belt-fed gun talks in long bursts at any range it can reach.
            _autoBurst = true;
            _burstLeft = d < 15f ? _rng.RandiRange(8, 15) : d < 250f ? _rng.RandiRange(5, 10) : _rng.RandiRange(3, 6);
            _burstPause = d < 15f ? _rng.RandfRange(0.05f, 0.2f) : _rng.RandfRange(0.25f, 0.55f);
            return;
        }
        if (!_b.Def.AutoCapable)
        {
            _autoBurst = false;
            _burstLeft = d < 40f ? _rng.RandiRange(2, 4) : _rng.RandiRange(1, 2);
            _burstPause = _rng.RandfRange(0.3f, 0.8f) * (panic ? 0.5f : 1f);
            return;
        }
        int extra = panic ? _rng.RandiRange(2, 5) : 0;
        if (d < 15f) { _autoBurst = true; _burstLeft = _rng.RandiRange(5, 12) + extra; _burstPause = _rng.RandfRange(0.05f, 0.2f); }
        else if (d < 35f) { _autoBurst = true; _burstLeft = _rng.RandiRange(3, 7) + extra; _burstPause = _rng.RandfRange(0.12f, 0.3f); }
        else if (d < 70f) { _autoBurst = panic || _rng.Randf() < 0.7f; _burstLeft = _rng.RandiRange(2, 4) + extra; _burstPause = _rng.RandfRange(0.2f, 0.4f); }
        else if (d < 120f) { _autoBurst = panic; _burstLeft = _rng.RandiRange(1, 3) + extra / 2; _burstPause = _rng.RandfRange(0.3f, 0.7f); }
        else { _autoBurst = false; _burstLeft = 1; _burstPause = _rng.RandfRange(0.5f, 1.2f); }
    }

    /// <summary>
    /// Would this shot risk a teammate? Checks a cone around the line of fire, not just
    /// the exact line: spread, recoil and people stepping in mid-burst all matter up close.
    /// </summary>
    bool FriendlyInLine(Vector3 eye, Vector3 pt)
    {
        var seg = pt - eye;
        float len = seg.Length();
        if (len < 0.01f) return false;
        var dir = seg / len;
        float reach = len + 4f;
        foreach (var c in Combatants.All)
        {
            // The wounded on the ground too: a round (or a fragment) finds a man who's down as surely as one who isn't.
            if (c == _b || c.Dead || c.Team != _b.Team) continue;
            var feet = c.FeetPos;
            if ((feet - eye).LengthSquared() > reach * reach) continue; // nowhere near the line
            if (NearLine(eye, dir, len, c.ChestPos) || NearLine(eye, dir, len, c.EyePos) || NearLine(eye, dir, len, feet + Vector3.Up * 0.6f)) return true;
        }
        return false;
    }

    /// <summary>Any of ours (on their feet or down, the player too) this close to where a grenade or a 40 mm round would land?</summary>
    bool OwnNear(Vector3 p, float r)
    {
        foreach (var c in Combatants.All)
            if (c != _b && !c.Dead && c.Team == _b.Team && c.FeetPos.DistanceTo(p) < r) return true;
        return false;
    }

    static bool NearLine(Vector3 eye, Vector3 dir, float len, Vector3 p)
    {
        float along = (p - eye).Dot(dir);
        if (along < -0.3f || along > len + 1.5f) return false;
        return (eye + dir * Mathf.Max(along, 0f)).DistanceTo(p) < 0.85f + along * 0.02f;
    }



    // ================================================================ events

    void Say(string text)
    {
        if (Now - _lastSay < 2.0) return;
        _lastSay = Now;
        Comms.Say(_b, text);
    }

    public void OnSpotted(Threat t)
    {
        if (Now - _lastCallout < 4.0) return;
        _lastCallout = Now;
        float d = _b.FeetPos.DistanceTo(t.Who.FeetPos);
        string where = Comms.Bearing(_b.FeetPos, t.Who.FeetPos);
        Say(d < 15f ? $"Contact! Right on me, {where}!" : $"Contact {where}, {d:0} meters!");
        var who = t.Who;
        var pos = t.LastKnownPos;
        _b.GetTree().CreateTimer(_rng.RandfRange(0.4f, 1.0f)).Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(_b) || !_b.Alive) return; // died before getting the words out
            // Heard by whoever is within shouting distance, and by the rest of the squad on its radio.
            // The rest of the side learns of it the slow way: the map and fire support (Intel), recon reports.
            var me = _b.FeetPos;
            foreach (var m in Mates())
            {
                float apart = m.FeetPos.DistanceTo(me);
                if (apart > ShoutRange && !(m.Squad != null && m.Squad == Sq && apart < SquadRadioRange)) continue;
                // A callout is a bearing and a rough range from the caller: a few metres out per hundred.
                var err = new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f)) * (d * 0.05f);
                m.Senses.Share(who, pos + err);
            }
        };
    }

    /// <summary>How long the flanking team keeps going round before it takes up the fight wherever it is.</summary>
    const float DrillFlankTime = 45f;

    /// <summary>How far a shouted callout carries over a firefight, and a squad's own radio net.</summary>
    const float ShoutRange = 60f, SquadRadioRange = 600f;

    /// <summary>Out of a vehicle (dismounted, or thrown out): back to being infantry.</summary>
    public void OnDismounted()
    {
        SetState(BotState.Advance, "dismounted");
        _hasWaypoint = false;
    }

    /// <summary>Back on our feet after a medic: take stock, get to cover.</summary>
    public void OnRevived()
    {
        SetState(BotState.Advance, "revived");
        _hasWaypoint = false;
        _b.Suppression = 0.6f;
    }

    public void OnHurt(HitInfo hit)
    {
        // Shot by the enemy: we can tell roughly where from, and the squad is in a fight with them.
        // A fragment, a shell or a bomb says where it burst, not where whoever sent it is (a shelled
        // squad gets off the impact area instead); and our own side's fire isn't a new enemy.
        if (hit.Direct && hit.Shooter!.Team != _b.Team)
        {
            Sq?.Engage(hit.Shooter.FeetPos);
            _b.Senses.Alert(hit.Shooter, 0.15f);
        }
        _lastHurt = Now;
        if (_peeking)
        {
            _peeking = false;
            _hideUntil = Now + _rng.RandfRange(0.6f, 1.6f);
        }
        if (_rng.Randf() < 0.5f) Say(_b.Health < 40f ? "I'm hit bad!" : "I'm hit!");
    }

    /// <summary>A round cracked past us. A person can roughly tell where it came from.</summary>
    public void OnShotAt(Vector3 from)
    {
        _lastShotAt = Now;
        Sq?.Engage(from);
        ICombatant? shooter = null;
        float best = 12f;
        foreach (var c in Combatants.All)
        {
            if (!c.Alive || c.Team == _b.Team) continue;
            float d = c.EyePos.DistanceTo(from);
            if (d < best) { best = d; shooter = c; }
        }
        if (shooter != null) _b.Senses.Alert(shooter, 0.1f);
    }
}
