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
    bool _following;
    double _nextAidCheck, _aidUntil, _nextLauncher = 4, _nextSupply, _lastMedicCall = -99, _nextShovel;
    ICombatant? _patient;
    int _aidKind; // 0 treat, 1 resupply, 2 build
    Vector3 _buildAt, _buildFacing;
    public bool Following => _following;
    bool _popPeek, _crouchLos = true;
    double _popUntil, _popAt, _losCheckAt;
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
        _peeking = false;
        _b.StrafeDir = null;
        _b.LeanTarget = 0f;
        State = s;
        _stateSince = Now;
    }

    Vector3 ThreatEye(Threat t) => t.Visible ? t.Who.EyePos : t.LastKnownPos + Vector3.Up * 1.6f;
    Vector3 AimPoint(Threat t) => Combatants.PointOn(t.Who, _aimPart);
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

    Threat? PickTarget()
    {
        Threat? best = null;
        float bestScore = float.MinValue;
        foreach (var t in _b.Senses.Threats)
        {
            if (!t.Who.Alive || (!t.Confirmed && t.Awareness < 0.35f)) continue;
            float d = _b.FeetPos.DistanceTo(t.LastKnownPos);
            double age = Now - Math.Max(t.LastSeen, t.LastHeard);
            float score = (t.Visible ? 100f : 0f) - d * 0.3f - (float)age * 2f + (t == Target ? 15f : 0f);
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
        Target = PickTarget();
        var t = Target;
        bool vis = t is { Visible: true };
        if (t != null) _lastContact = Now;
        float dist = t != null ? _b.FeetPos.DistanceTo(t.LastKnownPos) : 999f;
        bool suppressed = _b.Suppression > 0.5f + _b.P.Courage * 0.35f;

        if (CheckGrenades()) return;
        if (State == BotState.Evade)
        {
            SetState(t != null ? BotState.Hold : BotState.Advance, "clear of the frag");
            _holdUntil = Now + 1.0;
        }

        if (!_b.Reloading)
        {
            if (_b.Ammo == 0) { _b.StartReload(); Say("Reloading!"); }
            else if (!vis && _b.Ammo < _b.Def.MagSize * 0.45f && (t == null || Now - t.LastSeen > 1.5)) _b.StartReload();
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
            if (Objective != null && !InZone && dist > 80f && !suppressed && !(Sq?.Engaged ?? false)
                && (t.LastKnownPos - Objective.Center with { Y = t.LastKnownPos.Y }).Length() > Objective.Radius + 40f)
            {
                if (State != BotState.Advance) { SetState(BotState.Advance, "pushing through to the objective"); _hasWaypoint = false; }
                return;
            }
            if (State is BotState.InCover or BotState.Engage or BotState.Hold && TryLauncher(t)) return;
            switch (State)
            {
                case BotState.TakeCover:
                    break;
                case BotState.InCover:
                    if (!_peeking && !CoverFinder.Protected(_b, Cover!.Value.Pos, t.Who.EyePos, true))
                    {
                        if (!GoToCover(t, 10f, suppressed)) SetState(BotState.Engage, "cover blown");
                    }
                    else if (!_peeking) TryBound(t);
                    break;
                case BotState.Engage:
                    bool exposed = !CoverFinder.Protected(_b, _b.FeetPos, t.Who.EyePos, _b.Crouched);
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

        // With an objective, don't get dragged off chasing noises: an unseen enemy that isn't
        // near the objective or right on top of us isn't worth leaving it for.
        if (Objective != null && State is not (BotState.TakeCover or BotState.Advance))
        {
            float fromObj = (t.LastKnownPos - Objective.Center with { Y = t.LastKnownPos.Y }).Length();
            bool relevant = fromObj < Objective.Radius + 40f || dist < 35f
                            || (Sq is { Engaged: true } && Sq.ContactAt.DistanceTo(t.LastKnownPos) < 150f)
                            || (Watch is Vector3 w && (t.LastKnownPos - w with { Y = t.LastKnownPos.Y }).Length() < 110f); // what an overwatch team is there to watch

            bool stale = Now - t.LastSeen > 12.0 && InState > 8f;
            if (!relevant || stale)
            {
                SetState(BotState.Advance, relevant ? "back to the objective" : "ignoring, objective first");
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
                _holdUntil = Now + _b.P.Patience;
                // Keep them pinned where they ducked.
                if (Now - t.LastSeen < 1.0 && _rng.Randf() < 0.3f + _b.P.Aggression * 0.4f)
                    _suppressUntil = Now + _rng.RandfRange(1.5f, 3f);
                break;
            case BotState.TakeCover:
                break;
            case BotState.InCover:
                if (since > _b.P.Patience) Hunt(t);
                else if (!TryBound(t)) MaybeSuppress(t);
                break;
            case BotState.Hold:
                if (Now > _holdUntil)
                {
                    if (since > _b.P.Patience * 2f + 8f) GiveUp(t);
                    else Hunt(t);
                }
                else if (!TryBound(t)) MaybeSuppress(t);
                break;
            case BotState.Flank:
                if (_b.Arrived || InState > 20f)
                {
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
        Say(_rng.Randf() < 0.5f ? "Moving up!" : "Moving!");
        SetState(BotState.TakeCover, "bounding forward");
        Cover = s;
        _b.MoveTo(s.Pos, MoveMode.Sprint);
        return true;
    }

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
        if (spot is not CoverSpot s) return false;
        if (bb.Senses.Find(t.Who) != null) bb.Brain.CoverMe(t.Who);
        Bounds++;
        Say(_rng.Randf() < 0.5f ? "I'm up!" : "Moving!");
        SetState(BotState.TakeCover, "rushing (buddy covering)");
        Cover = s;
        _b.MoveTo(s.Pos, MoveMode.Sprint);
        return true;
    }

    float FromObjective(Vector3 p) => Objective == null ? 0f : (p - Objective.Center with { Y = p.Y }).Length();

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

    void MaybeSuppress(Threat t)
    {
        // The automatic rifleman's job is exactly this: long, steady fire on where they are.
        bool ar = Role == Role.AutoRifleman;
        if (Now - t.LastSeen > (ar ? 10.0 : 5.0) || Now < _suppressUntil + (ar ? 0.6 : 1.5) || _b.Ammo < _b.Def.MagSize * (ar ? 0.15f : 0.4f)) return;
        bool support = Now < _supportUntil; // the base of fire in a squad drill: that's their whole job
        if (_rng.Randf() > (0.12f + _b.P.Aggression * 0.2f) * (ar ? 3f : 1f) * (support ? 2.5f : 1f)) return;
        _suppressUntil = Now + (ar ? _rng.RandfRange(3f, 6f) : _rng.RandfRange(1.5f, 3.5f));
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
        foreach (var m in Mates())
            if (m.FeetPos.DistanceTo(t.LastKnownPos) < 14f) return false;
        foreach (var c in Combatants.All)
            if (c is Player { Alive: true } pl && pl.Team == _b.Team && pl.FeetPos.DistanceTo(t.LastKnownPos) < 14f) return false;
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

    double _boardCheck, _nextContactCall;

    /// <summary>
    /// Crews go to their vehicle and get in (driver first, then the gun); a squad whose
    /// transport has come for it climbs aboard; the logistics team rides with its truck
    /// when it's taking supplies forward. Not while there's someone to shoot at close by.
    /// </summary>
    bool Board(Threat? t, bool vis)
    {
        if (_b.Ride != null || Sq == null || Now < _boardCheck) return false;
        if (vis && t != null && t.LastKnownPos.DistanceTo(_b.FeetPos) < 120f) return false;
        Vehicle? v = null;
        SeatRole? seat = null;
        if (Sq.Kind is SquadKind.Armor or SquadKind.Transport or SquadKind.Air or SquadKind.Mortar && Sq.Vehicle is { Destroyed: false } own) { v = own; seat = SeatRole.Driver; }
        else if (Sq.Kind == SquadKind.Logistics && Sq.Vehicle is { Destroyed: false } truck && Sq.FobSite != null && Sq.FobBuildStart < 0) { v = truck; seat = SeatRole.Driver; }
        else if (Sq.Transport is { Destroyed: false, Boarding: true } ride) { v = ride; seat = SeatRole.Passenger; }
        if (v == null) return false;
        float d = v.GlobalPosition.DistanceTo(_b.FeetPos);
        if (d > 400f) return false;
        if (d < v.Def.Hull.Z * 0.5f + 3f)
        {
            _boardCheck = Now + 1.0;
            int s = v.FreeSeat(seat);
            // Crews take the driver's seat, then the gun, then anything.
            if (seat == SeatRole.Driver && v.DriverSeat >= 0 && v.Occupants[v.DriverSeat] != null && v.GunnerSeat >= 0 && v.Occupants[v.GunnerSeat] == null) s = v.GunnerSeat;
            if (s >= 0 && v.Enter(_b, s)) { SetState(BotState.Advance, "mounted"); return true; }
            return false;
        }
        if (State != BotState.Advance || !_b.GoalPos.IsEqualApprox(v.GlobalPosition))
        {
            SetState(BotState.Advance, seat == SeatRole.Passenger ? "to the transport" : "to the vehicle");
            _b.MoveTo(v.GlobalPosition, MoveMode.Sprint);
            _hasWaypoint = true;
            _waypoint = v.GlobalPosition;
        }
        _boardCheck = Now + 0.5;
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
                if (State is BotState.Advance or BotState.Search or BotState.Investigate or BotState.Flank)
                {
                    SetState(BotState.Hold, "covering the withdrawal");
                    _holdUntil = Now + 3.0;
                    _b.Stop();
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
                    // Go wide round the side the leader called, closing to about half the distance.
                    var to = (Sq.ContactAt - _b.FeetPos) with { Y = 0f };
                    float d = to.Length();
                    if (d < 40f) return false;
                    var dir = to / d;
                    var right = dir.Cross(Vector3.Up) * (Sq.AssaultTeam == 1 ? 1f : -1f);
                    var goal = _b.FeetPos + dir * (d * 0.55f) + right * MathF.Min(90f, d * 0.5f);
                    if (ground != null) goal = ground.Ground(goal);
                    SetState(BotState.Flank, $"flanking with {Squad.TeamName(team)}");
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
                if (c.Dead || c.Team != _b.Team || (c.Downed && Role != Role.Medic)) continue;
                if (_claimed.TryGetValue(c, out var by) && by != _b && GodotObject.IsInstanceValid(by) && by.Alive && by.Brain.State == BotState.Aid) continue;
                float d = c.FeetPos.DistanceTo(_b.FeetPos);
                if (d > reach) continue;
                float need = Role == Role.Medic ? (_b.Medkits <= 0 ? 0f : MedicNeed(c))
                                                : (c.AmmoLevel < 0.4f ? (0.4f - c.AmmoLevel) * 100f : 0f);
                if (need <= 0f) continue;
                bool squad = c is Bot cb && cb.Squad == Sq || c is Player && Sq != null && Sq.Members.Contains(c);
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
    static float MedicNeed(ICombatant c) =>
        c.Downed ? 120f + c.Body.Bleeding * 2000f
        : c.Body.Bleeding > 0.001f ? 40f + c.Body.Bleeding * 2000f
        : c.Hp < 70f ? 70f - c.Hp : 0f;

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
            || (_aidKind == 0 ? MedicNeed(who) <= 0f : who.AmmoLevel >= 0.99f || !who.Alive))
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
            _aidUntil = Now + (_aidKind == 0 ? (who.Downed ? 6.0 : 3.0) : 1.2);
            if (who is Player) Hud.Toast(_aidKind == 0 ? $"{_b.Callsign} is patching you up" : $"{_b.Callsign} is handing you ammo", 2f);
            Say(_aidKind == 0 ? (who == _b ? "Patching myself up." : who.Downed ? "Stay with me! I've got you!" : "Hold still, I've got you!") : "Here, take these mags!");
            SoundWorld.I.Emit(Snd.Bag, _b.EyePos, 0f, _b);
        }
        if (Now < _aidUntil) return true;
        if (_aidKind == 0) { if (who.Downed) Revives++; who.Heal(55f); _b.Medkits--; Heals++; }
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
        foreach (var m in Mates())
            if (m.FeetPos.DistanceTo(t.LastKnownPos) < 10f) return false; // not on our own guys
        if (!_b.ThrowGrenadeAt(t.LastKnownPos)) return false;
        _nextNade = Now + 12.0;
        Say("Frag out!");
        if (_b.P.Aggression > 0.45f) _pushAfter = Now + 3.6; // follow it in
        return true;
    }

    /// <summary>A live grenade we know about is close: get away from it.</summary>
    bool CheckGrenades()
    {
        var space = _b.GetWorld3D().DirectSpaceState;
        foreach (var g in Grenade.Live)
        {
            if (!GodotObject.IsInstanceValid(g) || g.Fuse > 3.2f) continue; // still in the air
            var gp = g.GlobalPosition;
            float d = gp.DistanceTo(_b.FeetPos);
            if (d > 9f) continue;
            bool noticed = d < 4f || space.IntersectRay(PhysicsRayQueryParameters3D.Create(_b.EyePos, gp + Vector3.Up * 0.1f, 1)).Count == 0;
            if (!noticed) continue;
            var away = _b.FeetPos - gp;
            away.Y = 0f;
            if (away.LengthSquared() < 0.01f) away = Vector3.Right;
            if (State != BotState.Evade)
            {
                Say("Grenade!");
                SetState(BotState.Evade, "grenade!");
                _b.MoveTo(_b.FeetPos + away.Normalized() * 11f, MoveMode.Sprint); // pick the escape once
            }
            _evadeUntil = Now + g.Fuse + 0.4;
            return true;
        }
        return State == BotState.Evade && Now < _evadeUntil;
    }

    bool GoToCover(Threat t, float radius, bool urgent)
    {
        Covers++;
        var spot = CoverFinder.Find(_b, ThreatEye(t), radius, _rng);
        if (spot is not CoverSpot s) return false;
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
    bool AreaFire => Sq != null && Objective != null && Sq.FiringInSupport(_b) && FromObjective(_b.FeetPos) < 260f && _b.Ammo > _b.Def.MagSize * 0.3f;

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
            case BotState.TakeCover:
                if (_b.Arrived)
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
                if (!vis && Now > _popAt)
                {
                    _popUntil = Now + _rng.RandfRange(0.6f, 1.2f);
                    _popAt = Now + _rng.RandfRange(1.5f, 3f);
                }
                break;
        }
        if (vis && Now > _losCheckAt)
        {
            _losCheckAt = Now + 0.5;
            var low = _b.FeetPos + Vector3.Up * 1.17f;
            _crouchLos = _b.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(low, Combatants.PointOn(t!.Who, Combatants.Part.UpperChest), 1)).Count == 0;
        }

        _b.SetCrouch(WantCrouch(t, vis));
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
        var mode = gap > 10f ? MoveMode.Sprint : MoveMode.Run;
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
                if (!hostile || Sq.AssaultOn == Objective || Sq.Alive < 4 || d > 340f || d < 150f) return false;
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
            _b.MoveTo(c.Pos, MoveMode.Walk);
            if (Now > _hideUntil && !_b.Reloading && _b.Ammo > 0 && _b.Suppression < 0.75f)
            {
                _peeking = true;
                // Half the time a quick pop — up, a couple of rounds, back down.
                _popPeek = _rng.Randf() < 0.5f;
                _peekUntil = Now + (_popPeek ? _rng.RandfRange(0.5f, 1.0f) : _rng.RandfRange(1.2f, 3.2f) * (0.7f + _b.P.Aggression * 0.6f));
                // Pre-fire: spray where they were as we come out, before we can even see them.
                if (!vis && Now - t.LastSeen < 3.0 && _rng.Randf() < 0.35f + _b.P.Aggression * 0.4f)
                    _prefireUntil = Now + 0.6;
            }
        }
        else
        {
            if (c.Side && c.LeanDir == 0f) _b.MoveTo(c.PeekPos, MoveMode.Walk);
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
        if (Now > _strafeUntil)
        {
            _strafeUntil = Now + _rng.RandfRange(0.3f, 0.9f);
            var to = t.Who.FeetPos - _b.FeetPos;
            to.Y = 0f;
            to = to.Normalized();
            var perp = new Vector3(-to.Z, 0f, to.X) * (_rng.Randf() < 0.5f ? -1f : 1f);
            float push = d > 7f ? _b.P.Aggression - 0.25f : -0.3f;
            _strafe = _rng.Randf() < 0.12f ? Vector3.Zero : (perp + to * push).Normalized();
        }
        _b.StrafeDir = _strafe;
    }

    bool WantCrouch(Threat? t, bool vis)
    {
        float d = t != null ? _b.FeetPos.DistanceTo(t.LastKnownPos) : 999f;
        return State switch
        {
            BotState.InCover => !_peeking || (!(Cover?.Low ?? false) && !(Cover?.Side ?? false)),
            BotState.Engage => d > 30f && _crouchLos,        // don't duck out of your own sight line
            BotState.Hold => Cover == null && d > 18f && Now > _popUntil,
            BotState.Aid => _aidUntil > 0,   // kneeling to work
            // At an observation post, stay low: a head on a ridgeline is what gets seen first.
            BotState.Advance => Overwatch && InZone && _pausing,
            _ => false,
        };
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
                float a = _rng.Randf() * Mathf.Tau, r = MathF.Sqrt(_rng.Randf()) * Objective!.Radius * 0.6f;
                _areaPoint = Objective.Center + new Vector3(MathF.Cos(a) * r, _rng.RandfRange(0.5f, 4f), MathF.Sin(a) * r);
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
                aim.Goal = _b.EyePos + enemy.Rotated(Vector3.Up, MathF.Sin((float)Now * 0.3f + _b.Team) * 0.45f) * 40f + Vector3.Down * 2f;
                return;
            }
            if ((_b.LookOut is not Vector3 lo0 || !InZone) && Sq?.SectorFor(_b, InZone) is Vector3 sec)
            {
                // My sector: a slow sweep either side of it.
                aim.Goal = _b.EyePos + sec.Rotated(Vector3.Up, MathF.Sin((float)Now * 0.4f + _b.GetInstanceId() % 7) * 0.35f) * 25f + Vector3.Down * 1f;
                return;
            }
            if (_b.LookOut is Vector3 lo && InZone && lo.LengthSquared() > 0.01f)
            {
                // At a window or on a roof: watch out of it.
                aim.Goal = _b.EyePos + lo.Normalized().Rotated(Vector3.Up, MathF.Sin((float)Now * 0.4f + _b.Team) * 0.5f) * 30f + Vector3.Down * 1.5f;
                return;
            }
            if (Objective != null)
            {
                // In the zone, watch outward; on the way there, watch ahead toward it.
                var d = InZone ? _b.FeetPos - Objective.Center : Objective.Center - _b.FeetPos;
                d.Y = 0f;
                if (d.LengthSquared() > 1f) enemy = d.Normalized();
            }
            float a = Sweeping ? (float)(Now - _pauseStart) * 1.2f : MathF.Sin((float)Now * 0.5f + _b.Team) * 1.1f;
            aim.Goal = _b.EyePos + enemy.Rotated(Vector3.Up, a) * 20f;
            return;
        }
        aim.Goal = _b.LookAheadPoint();
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
        bool suppress = Now < _suppressUntil && Now - t.LastSeen < 6.0 && _b.Ammo > _b.Def.MagSize * 0.25f;
        bool prefire = Now < _prefireUntil;
        if (!suppress && !prefire) { if (_burstLeft > 0 && !_autoBurst) _burstLeft = 0; return; }
        var sp = SuppressPoint(t);
        if (Mathf.RadToDeg(_b.Aim.Dir.AngleTo(sp - eye)) > 4f) return;
        // Wait until fully leaned out, and check the round's real path clears our own cover.
        if (!_b.LeanSettled || Now < _nextShotAt || FriendlyInLine(eye, sp) || !_b.ShotReaches(sp, 4f)) return;
        if (_burstLeft <= 0)
        {
            _autoBurst = _b.Def.AutoCapable;
            _burstLeft = _autoBurst ? _rng.RandiRange(3, 6) : _rng.RandiRange(1, 2);
            _burstPause = prefire ? 0.1f : _rng.RandfRange(0.5f, 1.2f);
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
        foreach (var c in Combatants.All)
        {
            if (c == _b || !c.Alive || c.Team != _b.Team) continue;
            foreach (var p in new[] { c.ChestPos, c.EyePos, c.FeetPos + Vector3.Up * 0.6f })
            {
                float along = (p - eye).Dot(dir);
                if (along < -0.3f || along > len + 1.5f) continue;
                float off = (eye + dir * Mathf.Max(along, 0f)).DistanceTo(p);
                if (off < 0.85f + along * 0.02f) return true;
            }
        }
        return false;
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
            foreach (var m in Mates()) m.Senses.Share(who, pos);
        };
    }

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
        Sq?.Engage(hit.Shooter?.FeetPos ?? _b.FeetPos);
        _lastHurt = Now;
        if (hit.Shooter != null) _b.Senses.Alert(hit.Shooter, 0.15f);
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
