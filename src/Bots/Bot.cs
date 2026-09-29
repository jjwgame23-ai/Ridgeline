using Godot;

namespace Ridgeline;

public enum MoveMode { Walk, Run, Sprint }

/// <summary>How a soldier holds himself: on his feet, on a knee, or flat on the ground.</summary>
public enum Posture { Stand, Crouch, Prone }

/// <summary>
/// A bot soldier's body: movement along navmesh paths, stance, the same weapons
/// and ballistics the player uses, health and death. Perception, aiming and
/// decisions live in BotSenses, BotAim and BotBrain.
/// </summary>
public partial class Bot : CharacterBody3D, ICombatant
{
    public Personality P = null!;
    public WeaponDef Def = WeaponDef.Carbine;
    public int TeamId;
    public readonly Body Body = new();
    /// <summary>0..100 overall condition (from the body model), for HUDs and decisions.</summary>
    public float Health => Body.Condition;
    HitInfo? _downHit;
    ICombatant? _lastShooter;
    /// <summary>The last enemy to hit him: a bleed-out is his (not a teammate's stray fragment, or his own grenade, that grazed him since).</summary>
    ICombatant? _lastEnemy;
    double _moanAt;
    public float Suppression;
    /// <summary>Same wind as the player: about 14 s of flat-out sprint, then you have to walk it off.</summary>
    public float Stamina = 1f;
    bool _winded;
    float _sinceSprint = 10f;
    public int Ammo;
    /// <summary>Spare magazines, each with what's left in it (see Magazines).</summary>
    public Magazines Mags = null!;
    public int ShotsFired, HitsLanded, Kills, ShotsBlocked, ShotsBlockedNear, ShotsWide;
    public Vector3? StrafeDir; // set by the brain to move directly instead of along a path
    public int Grenades = 2;
    /// <summary>Smoke grenades: leaders and riflemen carry one or two, for crossing open ground under fire.</summary>
    public int SmokeGrenades = 1;
    public Role Role = Role.Rifleman;
    public int LauncherRounds, Medkits, Sandbags, Rockets;
    public WeaponDef? RocketDef => Role == Role.AntiTank ? WeaponDef.Lat : Role == Role.HeavyAT ? WeaponDef.Hat : Role == Role.AntiAir ? WeaponDef.Manpad : null;
    public float LeanTarget;   // -1 left .. 1 right, set by the brain when peeking a corner
    /// <summary>Posted at a window or on a roof: which way to watch (set when the post is picked).</summary>
    public Vector3? LookOut;
    public Squad? Squad;

    public BotAim Aim = null!;
    public BotSenses Senses = null!;
    public BotBrain Brain = null!;
    /// <summary>A drone operator's drones and stocks.</summary>
    public DroneOps? Ops;
    public CrewBrain Crew = null!;
    public Vehicle? Ride { get; private set; }
    public int SeatIdx { get; private set; } = -1;

    public Posture Stance { get; private set; } = Posture.Stand;
    public bool Crouched => Stance == Posture.Crouch;
    public bool Prone => Stance == Posture.Prone;
    /// <summary>Getting down onto the ground or up off it: a moment in which he can't shoot and barely moves.</summary>
    public bool ChangingStance => _stanceT > 0f;
    public bool Reloading => _reloadT > 0f;
    public MoveMode Mode { get; private set; } = MoveMode.Run;
    public bool Moving => new Vector2(Velocity.X, Velocity.Z).Length() > 0.5f;
    public bool Arrived => !_hasGoal;
    public Vector3 GoalPos => _goal;
    public Vector3[] PathPoints => _path;
    public int PathIndex => _pathIdx;
    /// <summary>For the verbose log: how the walk is going.</summary>
    public string PathDebug => $"goal {(_hasGoal ? $"{Flat(_goal - GlobalPosition).Length():0} m (asked {Flat(_rawGoal - GlobalPosition).Length():0})" : "-")} path {_pathIdx}/{_path.Length}{(_partial ? " partial" : "")} stuck x{_stuckCount} {Mode} {new Vector2(Velocity.X, Velocity.Z).Length():0.0} m/s at ({GlobalPosition.X:0.0}, {GlobalPosition.Y:0.0}, {GlobalPosition.Z:0.0}), walled {Walled()}/8, mesh {Valley.ClosestOnFoot(GetWorld3D(), GlobalPosition).DistanceTo(GlobalPosition):0.0} m{Probe()}";

    /// <summary>For the log: how far from here a fresh route to the goal gets (a few metres: he's cut off).</summary>
    string Probe()
    {
        if (!_hasGoal) return "";
        var np = NavBaker.Path(GetWorld3D().NavigationMap, GlobalPosition, _rawGoal);
        return np.Length > 0 ? $", a route gets {Flat(np[^1] - GlobalPosition).Length():0} m of {Flat(_rawGoal - GlobalPosition).Length():0}" : ", no route";
    }

    /// <summary>For the log: of eight directions, how many are blocked within 4 m (8: shut in).</summary>
    int Walled()
    {
        var space = GetWorld3D().DirectSpaceState;
        int n = 0;
        for (int k = 0; k < 8; k++)
        {
            float a = k * Mathf.Tau / 8f;
            var from = GlobalPosition + Vector3.Up * 1.0f;
            var q = PhysicsRayQueryParameters3D.Create(from, from + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * 4f, Layers.Solid, SelfOnly);
            if (space.IntersectRay(q).Count > 0) n++;
        }
        return n;
    }
    public float RemainingDistance => _hasGoal ? Flat(_goal - GlobalPosition).Length() : 0f;

    // --- ICombatant
    public int Team => TeamId;
    public string Callsign => P.Name;
    /// <summary>In the fight: not down, not dead.</summary>
    public bool Alive => !Body.Down && !Body.Dead;
    public bool Downed => Body.Down && !Body.Dead;
    public bool Dead => Body.Dead;
    Body ICombatant.Body => Body;
    public Vector3 FeetPos => GlobalPosition;
    /// <summary>Eyes and chest where the body actually is: they follow the figure down and up (see Pose), not the
    /// stance he has decided on. (They used to jump to the new stance's height at once, so a man standing up
    /// from prone saw, and could be hit, over a wall a second before his body rose above it.)</summary>
    public Vector3 EyePos => GlobalPosition + Vector3.Up * (_eyeH - MathF.Abs(_lean) * 0.06f) + Right * (_lean * 0.38f);
    public Vector3 ChestPos => GlobalPosition + Vector3.Up * _chestH + Right * (_lean * 0.2f);
    Vector3 Right => BotAim.DirFrom(Aim.Yaw, 0f).Cross(Vector3.Up);
    public Vector3 Vel => Velocity;
    public float BodyHeight => _capsule.Height;
    public double LastShotTime { get; private set; } = -99;
    public Rid BodyRid => GetRid();
    public Vector3 MuzzlePos => _muzzle.GlobalPosition;

    readonly RandomNumberGenerator _rng = new();
    CapsuleShape3D _capsule = null!;
    CollisionShape3D _col = null!;
    Node3D _visual = null!, _pelvis = null!, _torso = null!, _head = null!, _arms = null!, _hipL = null!, _hipR = null!, _muzzle = null!;
    /// <summary>Which way his hips and legs point (degrees, like Aim.Yaw): toward where he's going, while the
    /// shoulders and rifle stay on the aim, within what a spine can twist.</summary>
    float _legYaw;
    /// <summary>Crouched and on the move: up off the knee into a bent-legged walk (0..1).</summary>
    float _crouchWalkT;
    float _eyeH = 1.62f, _chestH = 1.25f;
    Vector3[] _path = Array.Empty<Vector3>();
    int _pathIdx;
    Vector3 _goal, _rawGoal, _lastProgressPos;
    bool _hasGoal, _reloadEmpty, _partial;
    float _routeCheckT, _doorT;
    float _detourT;
    float _lean, _cool, _reloadT, _reloadDur, _senseT, _thinkT, _stuckT, _repathT, _walkPhase, _crouchT, _proneT, _stanceT, _rangeErr;
    int _reloadStage, _stuckCount;
    double _stepAt, _unstickUntil, _lastRepath = -99;
    Vector3 _unstickDir;

    static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    public override void _EnterTree() => Combatants.Register(this);
    public override void _ExitTree() => Combatants.Unregister(this);

    public override void _Ready()
    {
        _rng.Randomize();
        CollisionLayer = 2;
        CollisionMask = Layers.Solid;
        FloorMaxAngle = Mathf.DegToRad(46f);
        FloorSnapLength = 0.35f;
        _capsule = new CapsuleShape3D { Radius = 0.3f, Height = 1.8f };
        _col = new CollisionShape3D { Shape = _capsule, Position = new Vector3(0, 0.9f, 0) };
        AddChild(_col);

        Ammo = Def.MagSize + (Def.OpenBolt ? 0 : 1);
        Mags = new Magazines(Def.MagSize, Def.Mags);
        Stock();
        Aim = new BotAim(this, _rng.Randf() * 10f);
        Senses = new BotSenses(this);
        Brain = new BotBrain(this);
        if (Role == Role.DroneOperator) Ops = new DroneOps(this);
        Crew = new CrewBrain(this);
        _senseT = _rng.Randf() * 0.1f; // stagger so bots don't all think on the same frame
        _thinkT = _rng.Randf() * 0.2f;
        _rangeErr = _rng.RandfRange(-1f, 1f) * (1f - P.Skill) * 0.15f;
        BuildVisual();
    }

    // ================================================================ per tick

    float _accum;
    static int _nextId;
    readonly int _id = _nextId++;

    // ---- a per-tick spatial hash of everyone alive, so "who's near me" isn't a scan of every body on the map
    static ulong _gridFrame = ulong.MaxValue;
    static readonly Dictionary<(int, int), List<ICombatant>> _grid = new();
    /// <summary>The cell lists in use this tick, and spare ones: only occupied cells are kept (keeping every
    /// cell anyone had ever walked through, and clearing them all every tick, cost more the longer a match went).</summary>
    static readonly List<List<ICombatant>> _gridUsed = new();
    static readonly Stack<List<ICombatant>> _gridSpare = new();
    const float GridCell = 2f;

    static (int, int) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / GridCell), (int)MathF.Floor(p.Z / GridCell));

    static void BuildGrid()
    {
        ulong frame = Engine.GetPhysicsFrames();
        if (frame == _gridFrame) return;
        _gridFrame = frame;
        foreach (var l in _gridUsed) { l.Clear(); _gridSpare.Push(l); }
        _gridUsed.Clear();
        _grid.Clear();
        foreach (var c in Combatants.All)
        {
            if (!c.Alive) continue;
            var k = CellOf(c.FeetPos);
            if (!_grid.TryGetValue(k, out var l))
            {
                l = _gridSpare.Count > 0 ? _gridSpare.Pop() : new List<ICombatant>();
                _grid[k] = l;
                _gridUsed.Add(l);
            }
            l.Add(c);
        }
    }

    /// <summary>Just us, for queries that shouldn't hit our own body (made once, not per query).</summary>
    Godot.Collections.Array<Rid> SelfOnly => _selfOnly ??= new Godot.Collections.Array<Rid> { GetRid() };
    Godot.Collections.Array<Rid>? _selfOnly;

    public override void _PhysicsProcess(double delta)
    {
        if (Body.Dead) return;
        if (Body.Down) { DownedTick((float)delta); return; }
        float dt = (float)delta;
        // Far from the listener nobody can see the difference: step every other tick with twice the time.
        _accum += dt;
        bool far = GlobalPosition.DistanceSquaredTo(SoundWorld.I.ListenerPos) > 60f * 60f;
        ulong step = !far ? 1UL : GlobalPosition.DistanceSquaredTo(SoundWorld.I.ListenerPos) > 150f * 150f ? 3UL : 2UL;
        if ((Engine.GetPhysicsFrames() + (ulong)_id) % step != 0) return;
        dt = _accum;
        _accum = 0f;
        switch (Body.Tick(dt, Clock.Now))
        {
            case HitResult.Downed: GoDown(_downHit ?? BleedHit()); return;
            case HitResult.Dead: Die(_downHit ?? BleedHit()); return;
        }
        if (Ride != null)
        {
            // Aboard: eyes open, and the seat's job; the vehicle carries the body.
            _senseT -= dt;
            if (_senseT <= 0f) { _senseT += 0.15f; using (Prof.Time("senses")) Senses.Tick(0.15f); }
            using (Prof.Time("crew")) Crew.Tick(dt);
            return;
        }
        Suppression = Mathf.MoveToward(Suppression, 0f, dt * 0.25f);
        _lean = Mathf.MoveToward(_lean, Prone ? 0f : LeanTarget, dt * 4f);
        _stanceT = MathF.Max(0f, _stanceT - dt);
        // Leaning moves the body, so it moves what can be hit too.
        _col.Position = new Vector3(_lean * 0.22f, _capsule.Height / 2f, 0f);
        _cool -= dt;
        UpdateReload(dt);

        _senseT -= dt;
        if (_senseT <= 0f)
        {
            // Eyes open 10 times a second in a fight; 5 when there's been nothing to see for a while.
            float every = Senses.Quiet ? 0.2f : 0.1f;
            _senseT += every;
            using (Prof.Time("senses")) Senses.Tick(every);
        }
        _thinkT -= dt;
        if (_thinkT <= 0f) { _thinkT += 0.2f; using (Prof.Time("think")) Brain.Think(); }
        using (Prof.Time("act")) Brain.Act(dt);
        Aim.Update(dt);
        using (Prof.Time("move")) Move(dt);
        Animate(dt);
    }

    // ================================================================ movement

    public void MoveTo(Vector3 goal, MoveMode mode)
    {
        Mode = mode;
        StrafeDir = null;
        if (_hasGoal && goal.DistanceTo(_rawGoal) < 0.5f && _path.Length > 0) return;
        _rawGoal = goal;
        _goal = goal;
        _hasGoal = true;
        _partial = false; // a new goal: whatever leg the last one was on is over
        // Long paths are expensive on a big map: at most one every 0.6 s. Keep walking
        // the old path meanwhile; Move() repaths when the timer runs out.
        double since = Clock.Now - _lastRepath;
        if (since < 0.6 && !DirectClear(goal))
        {
            // Plan shortly. The old path leads to the old goal: drop it, or reaching its end
            // would read as having arrived at the new one.
            _repathT = (float)(0.6 - since);
            _path = Array.Empty<Vector3>();
            _pathIdx = 0;
            return;
        }
        Repath();
    }

    /// <summary>
    /// Like MoveTo, for a goal that keeps sliding (a formation slot): walk straight at it
    /// when the way is clear, and only ask for a real path every few seconds otherwise.
    /// </summary>
    public void MoveToLoose(Vector3 goal, MoveMode mode, double minRepath)
    {
        if (DirectClear(goal) || Clock.Now - _lastRepath > minRepath || !_hasGoal) { MoveTo(goal, mode); return; }
        Mode = mode;
    }

    public bool CanWalkStraight(Vector3 goal) => DirectClear(goal);

    /// <summary>
    /// Walk the route someone else is already walking (a squad member trailing its
    /// leader round an obstacle): no navmesh query of our own.
    /// </summary>
    public bool AdoptPath(Bot other, MoveMode mode)
    {
        var pts = other._path;
        if (pts.Length == 0 || !other._hasGoal) return false;
        // Join the leader's route at a point we can walk straight to from here; from the other
        // side of a wall, their route is no use to us.
        int from = -1;
        for (int j = Math.Max(0, other._pathIdx - 1); j < Math.Min(pts.Length, other._pathIdx + 3); j++)
            if (MathF.Abs(pts[j].Y - GlobalPosition.Y) < 0.8f && Flat(pts[j] - GlobalPosition).Length() < 15f && BodyClear(GlobalPosition, pts[j])) { from = j; break; }
        if (from < 0) return false;
        Mode = mode;
        StrafeDir = null;
        // Where he's going is his own place in the formation, not the leader's destination: share the leader's
        // route only as far as the point on it nearest that place, then plan the last bit from there. (He used
        // to take the leader's goal along with the route, so he ran on along it for the objective, past the
        // leader, until the next re-aim at his slot turned him round and sent him back.)
        if (Squad?.Leader == other && Squad.SlotFor(this) is Vector3 slot)
        {
            int seg = from;
            var cut = pts[from];
            float best = Flat(cut - slot).LengthSquared();
            for (int j = from; j + 1 < pts.Length; j++)
            {
                var a = pts[j];
                var ab = pts[j + 1] - a;
                float len2 = Flat(ab).LengthSquared();
                float t = len2 > 1e-4f ? Mathf.Clamp(Flat(slot - a).Dot(Flat(ab)) / len2, 0f, 1f) : 0f;
                var p = a + ab * t;
                float d2 = Flat(p - slot).LengthSquared();
                if (d2 < best) { best = d2; cut = p; seg = j; }
            }
            // Nowhere along it gets him any nearer his place than he already is: the leader's route is no use
            // to him (following it would only take him the wrong way and back again every time he re-aims).
            if (MathF.Sqrt(best) > Flat(GlobalPosition - slot).Length() - 1f) return false;
            var leg = new Vector3[seg - from + 2];
            Array.Copy(pts, from, leg, 0, seg - from + 1);
            leg[^1] = cut;
            _path = leg;
            _pathIdx = 0;
            _rawGoal = slot;
            _goal = cut;
            // The slot is off the route: at the end of the shared stretch, plan the rest (see Move).
            _partial = Flat(cut - slot).Length() > 1f;
            _hasGoal = true;
            _repathT = 0f;
            _stuckT = 0f;
            _lastProgressPos = GlobalPosition;
            return true;
        }
        _path = pts[from..];
        _pathIdx = 0;
        _rawGoal = other._rawGoal;
        _goal = other._goal;
        _partial = false;
        _hasGoal = true;
        _repathT = 0f;
        _stuckT = 0f;
        _lastProgressPos = GlobalPosition;
        return true;
    }

    public void Stop()
    {
        _hasGoal = false;
        StrafeDir = null;
    }

    /// <summary>Close and nothing in the way: just walk there, no navmesh query needed.</summary>
    bool DirectClear(Vector3 goal)
    {
        var d = goal - GlobalPosition;
        if (new Vector2(d.X, d.Z).Length() > 15f || MathF.Abs(d.Y) > 0.8f) return false;
        return BodyClear(GlobalPosition, goal);
    }

    static readonly SphereShape3D _probe = new() { Radius = 0.3f };
    static readonly PhysicsShapeQueryParameters3D _probeQ = new() { Shape = _probe, CollisionMask = Layers.Solid };

    /// <summary>
    /// Could a body get from a to b in a straight line? A body-sized sphere swept at knee and
    /// chest height, not a thin ray: a ray can slip past a corner the shoulders would catch
    /// on, or through a window the legs can't.
    /// </summary>
    bool BodyClear(Vector3 a, Vector3 b)
    {
        var space = GetWorld3D().DirectSpaceState;
        // One sweep at hip-to-chest height (0.7-1.3 m) catches corners, window sills and low walls.
        _probeQ.Transform = new Transform3D(Basis.Identity, a + Vector3.Up * 1.0f);
        _probeQ.Motion = b - a;
        _probeQ.Exclude = SelfOnly;
        var r = space.CastMotion(_probeQ);
        return !(r.Length > 0 && r[0] < 0.999f);
    }

    void Repath()
    {
        _lastRepath = Clock.Now;
        if (DirectClear(_rawGoal))
        {
            // Straight there: the whole way, not a leg of it. (Left marked partial, reaching the end of this one-point
            // path planned again, the next frame and every frame after, each time resetting the stuck timer and the
            // repath clock the brain waits on: a man given the first stretch of his leader's route stood where it
            // ended for minutes, "following" a squad 300 m away.)
            _partial = false;
            _path = new[] { _rawGoal };
            _goal = _rawGoal;
            _pathIdx = 0;
            _stuckT = 0f;
            _lastProgressPos = GlobalPosition;
            _repathT = 0f;
            return;
        }
        _partial = false;
        var target = _rawGoal;
        using (Prof.Time("path")) using (Prof.Time("path:" + Brain.State + (Brain.Following ? "/follow" : ""))) _path = NavBaker.Path(GetWorld3D().NavigationMap, GlobalPosition, target);
        // Round any parked vehicle or wreck the navmesh doesn't know is there (see VehicleDetour).
        using (Prof.Time("detour")) _path = VehicleDetour.Apply(_path, GlobalPosition, 0, 40f, GetWorld3D());
        // The path ends at the nearest walkable point, so that's the real goal — a goal just
        // inside a crate would otherwise be "almost reached" forever.
        if (_path.Length > 0) _goal = _partial ? _rawGoal : _path[^1];
        _pathIdx = 0;
        _stuckT = 0f;
        _lastProgressPos = GlobalPosition;
        _repathT = _path.Length == 0 ? 0.5f : 0f; // navmesh may not be synced yet
        if (_path.Length == 0 && Clock.Now > 3.0) Stuck();
    }

    /// <summary>No progress: first sidestep somewhere random, and if that doesn't help, drop the goal so the brain picks another.</summary>
    public static int StuckEvents, StuckNearVehicle, StuckBoarding, StuckWaiting;

    void Stuck()
    {
        StuckEvents++;
        if (Vehicle.All.FirstOrDefault(v => !v.Destroyed && v.GlobalPosition.DistanceTo(FeetPos) < 9f) is { } nv)
        {
            StuckNearVehicle++;
            if (DuelMode.Verbose && StuckNearVehicle % 20 == 1)
                GD.Print($"[{Clock.Now:0}s] stuck by a vehicle: {Callsign} ({Squad?.Name} {Squad?.Kind}, {Brain.State}: {Brain.Note}) by {nv.Def.Name} ({(nv.Team == Team ? "ours" : "theirs")}, {(nv.Crewed ? "crewed" : "empty")}, {nv.Speed:0.0} m/s, {nv.GlobalPosition.DistanceTo(FeetPos):0.0} m), goal {GoalPos.DistanceTo(FeetPos):0} m, path {_pathIdx}/{_path.Length}, stuck x{_stuckCount}");
        }
        if (Brain.Note is "to the transport" or "to the vehicle")
        {
            StuckBoarding++;
            if (DuelMode.Verbose && StuckBoarding % 15 == 1)
            {
                var tv = Squad?.Kind is SquadKind.Rifle ? Squad.Transport : Squad?.Vehicle;
                GD.Print($"[{Clock.Now:0}s] stuck boarding: {Callsign} ({Squad?.Name} {Squad?.Kind}, {Brain.Note}) -> {tv?.Def.Name ?? "?"} {(tv != null ? tv.GlobalPosition.DistanceTo(FeetPos) : 0f):0} m, vehicle speed {tv?.Speed ?? 0f:0.0}, boarding {tv?.Boarding}, goal {GoalPos.DistanceTo(FeetPos):0} m, path {_path.Length}");
            }
        }
        if (Squad?.Transport != null) StuckWaiting++;
        _stuckCount++;
        if (_stuckCount >= 2)
        {
            float a = _rng.Randf() * Mathf.Tau;
            _unstickDir = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
            _unstickUntil = Clock.Now + 0.8;
        }
        if (_stuckCount >= 4)
        {
            _hasGoal = false;
            _stuckCount = 0;
        }
    }

    public Vector3 LookAheadPoint()
    {
        var pos = GlobalPosition;
        Vector3 dir;
        if (_hasGoal && _pathIdx < _path.Length) dir = Flat(_path[_pathIdx] - pos);
        else if (_hasGoal) dir = Flat(_goal - pos);
        else dir = Flat(BotAim.DirFrom(Aim.Yaw, 0f));
        if (dir.LengthSquared() < 0.01f) dir = Flat(BotAim.DirFrom(Aim.Yaw, 0f));
        return EyePos + dir.Normalized() * 10f;
    }

    public void SetBodyVisible(bool v) => _visual.Visible = v;

    public void SetCrouch(bool c) => SetStance(c ? Posture.Crouch : Posture.Stand);

    public void SetStance(Posture s)
    {
        if (s == Stance) return;
        // Down onto the ground and up off it take a moment; dropping to a knee hardly any.
        if (s == Posture.Prone || Stance == Posture.Prone) _stanceT = s == Posture.Prone ? 0.8f : 1.0f;
        if (s == Posture.Prone) Prof.Count($"prone:{Brain.State}");
        Stance = s;
        // The hitbox and eyes follow the body as it goes down or comes up (Pose, from Animate): getting flat takes
        // the time it takes, for being seen and hit as much as for seeing.
    }

    /// <summary>
    /// Collider, eyes and chest from the same eased pose the figure is drawn with: _crouchT lowers the hips,
    /// _crouchWalkT lifts them again into a bent-legged walk, and _proneT tips the whole body over onto the
    /// ground about the feet (so a height is the upright one times the cosine of the tilt, never lower than
    /// lying flat). (The capsule and eyes used to snap to the new stance the moment it was chosen, while the
    /// figure took 0.2-0.8 s to follow.)
    /// </summary>
    void Pose()
    {
        float tilt = MathF.Cos(_proneT * Mathf.Pi * 0.5f), lift = 0.12f * _proneT;
        float Up(float stand, float kneel, float crouchWalk, float flat) =>
            MathF.Max(flat, Mathf.Lerp(stand, Mathf.Lerp(kneel, crouchWalk, _crouchWalkT), _crouchT) * tilt + lift);
        float h = Up(1.8f, 1.25f, 1.4f, 0.62f);
        _eyeH = Up(1.62f, 1.17f, 1.32f, 0.38f);
        _chestH = Up(1.25f, 0.85f, 0.97f, 0.3f);
        if (MathF.Abs(_capsule.Height - h) > 0.005f) _capsule.Height = h;
        _col.Position = new Vector3(_lean * 0.22f, _capsule.Height / 2f, 0f);
    }

    /// <summary>Straight into whatever stance he's in now, with no transition (boarding a vehicle).</summary>
    void SnapPose()
    {
        _crouchT = Crouched ? 1f : 0f;
        _crouchWalkT = 0f;
        _proneT = Prone ? 1f : 0f;
        _legYaw = Aim.Yaw;
        _visual.Rotation = new Vector3(-_proneT * Mathf.Pi * 0.5f, 0f, 0f);
        _visual.Position = new Vector3(0f, 0.12f * _proneT, 0.85f * _proneT);
        Pose();
    }

    /// <summary>Top speeds with the rifle kept pointing well off the way he's going: sideways, and straight back.</summary>
    const float CrabSpeed = 1.8f, BackpedalSpeed = 1.2f;
    /// <summary>Two bodies (0.3 m capsules) shoulder to shoulder; and how close the man in front has to be before he's in the way.</summary>
    const float BodyGap = 0.6f, GiveWayRange = 1.2f;
    /// <summary>How long he's been held up by someone in his way (not by the world): queueing isn't being stuck.</summary>
    float _heldT;

    void Move(float dt)
    {
        var pos = GlobalPosition;
        if (Squad?.Leader == this) Squad.Crumb(pos);
        var wish = Vector3.Zero;
        if (StrafeDir is Vector3 s) wish = s;
        else if (_hasGoal)
        {
            if (_repathT > 0f)
            {
                _repathT -= dt;
                if (_repathT <= 0f) Repath();
            }
            // A waypoint counts as reached only on its own level: on a stair, the next point can be
            // straight overhead.
            while (_pathIdx < _path.Length && Flat(_path[_pathIdx] - pos).Length() < 0.45f && MathF.Abs(_path[_pathIdx].Y - pos.Y) < 1.3f) _pathIdx++;
            if (_pathIdx < _path.Length) wish = Flat(_path[_pathIdx] - pos).Normalized();
            else if (_path.Length > 0)
            {
                if (_partial) Repath(); // end of this leg: plan the next
                else _hasGoal = false;  // walked the whole path
            }
            // A vehicle has stopped across the way since it was planned: round it.
            _detourT -= dt;
            if (_detourT <= 0f && _pathIdx < _path.Length)
            {
                _detourT = 0.5f;
                using (Prof.Time("detour")) _path = VehicleDetour.Apply(_path, pos, _pathIdx, 20f, GetWorld3D());
            }
            // Shoved off the route (by squadmates, a blast, a corner): if the next waypoint is
            // round a corner from here now, plan again rather than grind into the wall.
            _routeCheckT -= dt;
            if (_routeCheckT <= 0f && _pathIdx < _path.Length && Brain.Env is EnvKind.Urban or EnvKind.Interior)
            {
                _routeCheckT = 0.7f;
                var wp = _path[_pathIdx];
                // Only when it's actually held up (walking fine round a bend is no reason to re-plan).
                if (new Vector2(Velocity.X, Velocity.Z).Length() < 1.2f && Flat(wp - pos).Length() < 20f && MathF.Abs(wp.Y - pos.Y) < 0.8f
                    && !BodyClear(pos, wp) && Clock.Now - _lastRepath > 1.5) Repath();
            }

            // Stuck on something: try again from here.
            _stuckT += dt;
            if (_stuckT > 1.2f)
            {
                // Held up behind one of ours (in file, or in a doorway) isn't stuck: he waits his turn, then squeezes
                // past (below). (Counted as stuck, he side-stepped at random or dropped his goal: stuck reports doubled
                // once men stopped walking through each other.) Held up that long, though, something else is wrong.
                if (pos.DistanceTo(_lastProgressPos) < 0.3f && _hasGoal && (_heldT <= 0f || _heldT > 5f)) { Stuck(); if (_hasGoal) Repath(); }
                else _stuckCount = 0;
                _stuckT = 0f;
                _lastProgressPos = pos;
            }
        }
        if (Clock.Now < _unstickUntil) wish = _unstickDir;
        // A shut door in the way: open it (and leave it open, as people do).
        _doorT -= dt;
        if (_doorT <= 0f && wish.LengthSquared() > 0.01f) { _doorT = 0.25f; Door.OpenAhead(this, pos, wish.Normalized()); }

        // Out of breath: jog until you've got some wind back (people don't sprint again the moment they can).
        if (Stamina < 0.05f) _winded = true;
        else if (_winded && Stamina > 0.4f) _winded = false;
        var mode = Mode == MoveMode.Sprint && (!Body.CanSprint || _winded) ? MoveMode.Run : Mode;
        float speed = (mode switch { MoveMode.Sprint => 5.6f, MoveMode.Run => 3.4f, _ => 2.0f }) * Body.SpeedMult;
        if (StrafeDir != null) speed = 2.2f;
        if (Crouched) speed = Mathf.Min(speed, 1.7f);
        if (Prone) speed = Mathf.Min(speed, 0.6f); // crawling
        if (ChangingStance) speed = Mathf.Min(speed, 0.3f);
        if (Brain.WantsAds) speed = Mathf.Min(speed, 2.2f);
        var dirWish = wish.LengthSquared() > 0.01f ? wish.Normalized() : Vector3.Zero;
        if (dirWish != Vector3.Zero && !Prone)
        {
            // Going somewhere other than where he's looking (keeping his rifle on the enemy on the way into cover,
            // say): the hips only turn so far from the shoulders (see Animate), so past about 45 degrees it's a
            // crab-walk, and past 90 a backpedal, and nobody does either at a run. (He used to run sideways at the
            // full 3.4 m/s and backwards at 2.4.)
            float face = dirWish.Dot(BotAim.DirFrom(Aim.Yaw, 0f));
            if (Mode == MoveMode.Sprint && face < 0.5f) speed = MathF.Min(speed, 3.4f * Body.SpeedMult); // can't sprint sideways
            if (face < 0.7f)
            {
                float cap = face >= 0f ? Mathf.Lerp(CrabSpeed, 3.4f, face / 0.7f) : Mathf.Lerp(CrabSpeed, BackpedalSpeed, -face);
                speed = MathF.Min(speed, cap * Body.SpeedMult * (Crouched ? 0.75f : 1f));
            }
        }

        // Don't walk through each other. A soft push keeps people a little apart; closer than shoulder to shoulder
        // he can't move any further into the other man at all; and one walking into the back of another going the
        // same way falls in behind him at his pace, while one who's standing, or coming the other way, gets
        // stepped round. (The push alone let two men sharing a route settle 0.3-0.4 m apart, one body inside the
        // other, for seconds on end.)
        BuildGrid();
        var cell = CellOf(pos);
        Span<Vector3> blocked = stackalloc Vector3[6];
        int nBlocked = 0;
        bool heldUp = false;
        var push = Vector3.Zero;
        for (int gz = -1; gz <= 1; gz++)
        for (int gx = -1; gx <= 1; gx++)
        {
            if (!_grid.TryGetValue((cell.Item1 + gx, cell.Item2 + gz), out var near)) continue;
            foreach (var c in near)
            {
                if (c == this || c is Bot { Ride: not null }) continue;
                // Someone on the floor above or below isn't in the way.
                if (MathF.Abs(c.FeetPos.Y - pos.Y) > 1.6f) continue;
                var away = Flat(pos - c.FeetPos);
                float d = away.Length();
                if (d > GiveWayRange || d < 0.001f) continue;
                var toward = -away / d;
                if (d < 0.8f) push += away / d * (0.8f - d) * 2f;
                if (d < BodyGap && nBlocked < blocked.Length) blocked[nBlocked++] = toward;
                if (d < BodyGap && dirWish != Vector3.Zero && dirWish.Dot(toward) > 0.3f) heldUp = true;
                if (dirWish == Vector3.Zero || dirWish.Dot(toward) < 0.8f) continue;
                // He's right in front, on my line.
                float along = Flat(c.Vel).Dot(dirWish);
                if (along > 0.3f)
                {
                    speed = MathF.Min(speed, MathF.Max(0f, along + (d - 0.9f) * 2f)); // in file: his pace, a pace behind
                    if (speed < 0.3f) heldUp = true;
                }
                else
                {
                    // Round him: to whichever side he isn't already on (the right, if dead ahead).
                    var right = dirWish.Cross(Vector3.Up);
                    push += right * (right.Dot(toward) > 0.05f ? -1f : 1f) * (GiveWayRange - d) * 1.5f;
                }
            }
        }
        wish += push;
        if (wish.LengthSquared() > 1f) wish = wish.Normalized();

        var hv = new Vector2(Velocity.X, Velocity.Z).MoveToward(new Vector2(wish.X, wish.Z) * speed, 10f * dt);
        _heldT = heldUp ? _heldT + dt : 0f;
        // Held up a second and a half by a man who won't move (a doorway, a narrow stair, two men face to face):
        // he squeezes past, shoulder to shoulder, slowly, the way people do. (Blocked outright, men jammed in
        // doorways until the stuck handling tore their routes up.)
        bool squeeze = _heldT > 1.5f;
        if (squeeze) hv = hv.LimitLength(1.2f);
        for (int k = 0; k < nBlocked && !squeeze; k++)
        {
            float into = hv.X * blocked[k].X + hv.Y * blocked[k].Z;
            if (into > 0f) hv -= new Vector2(blocked[k].X, blocked[k].Z) * into;
        }
        bool floor = IsOnFloor();
        // Standing still on firm ground with nothing pushing: there's nothing for the physics to move (someone
        // walking into us shows up in the wish above, and gets the full treatment).
        if (floor && hv.LengthSquared() < 1e-6f && Velocity.LengthSquared() < 1e-6f)
        {
            Velocity = Vector3.Zero;
            UpdateStamina(dt, 0f, mode);
            return;
        }
        float vy = floor ? 0f : Velocity.Y - 9.81f * dt;
        Velocity = new Vector3(hv.X, vy, hv.Y);
        // MoveAndSlide integrates over one physics tick; stretch it when this step covers two.
        float stretch = dt / (float)GetPhysicsProcessDeltaTime();
        if (stretch > 1.01f)
        {
            Velocity *= stretch;
            MoveAndSlide();
            Velocity /= stretch;
        }
        else MoveAndSlide();

        float spd = hv.Length();
        UpdateStamina(dt, spd, mode);
        if (spd > 0.6f && Clock.Now > _stepAt)
        {
            float stride = Mode == MoveMode.Sprint && spd > 4f ? 1.05f : Crouched ? 0.6f : 0.78f;
            _stepAt = Clock.Now + stride / spd;
            float gain = spd > 4f ? 3f : Crouched ? -8f : spd < 2.5f ? -5f : 0f;
            SoundWorld.I.Emit(Snd.Footstep, GlobalPosition + Vector3.Up * 0.05f, gain, this);
        }
    }

    void UpdateStamina(float dt, float spd, MoveMode mode)
    {
        if (mode == MoveMode.Sprint && spd > 4f)
        {
            Stamina = MathF.Max(0f, Stamina - dt / (Body.Lung ? 7f : 14f));
            _sinceSprint = 0f;
        }
        else
        {
            _sinceSprint += dt;
            if (_sinceSprint > 0.8f) Stamina = MathF.Min(1f, Stamina + dt * (spd > 0.4f ? 0.07f : 0.14f));
        }
    }

    // ================================================================ weapon

    public bool TryFire(float targetDist)
    {
        if (Reloading || Ammo <= 0 || _cool > 0f || ChangingStance) return false;
        Ammo--;
        ShotsFired++;
        // The next round is due one cycle after this one was, not after this step: with the step a tick or
        // three long, restarting the cycle each shot would slow the gun down (an M249 far off to ~600 rpm).
        _cool = (_cool > -0.026f ? _cool : 0f) + 60f / Def.Rpm;
        LastShotTime = Clock.Now;

        var eye = EyePos;
        var dir = Aim.Dir;
        float spread = Mathf.DegToRad(Brain.WantsAds ? Def.SpreadAdsDeg : Def.SpreadHipDeg * 0.6f);
        if (spread > 0f)
        {
            var perp = dir.Cross(Vector3.Up).Normalized();
            dir = dir.Rotated(perp.Rotated(dir, _rng.Randf() * Mathf.Tau), MathF.Sqrt(_rng.Randf()) * spread).Normalized();
        }

        var origin = _muzzle.GlobalPosition;
        if (GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, origin, 1, SelfOnly)).Count > 0) origin = eye;
        // Rounds leave the muzzle and cross the line of sight at the target; holdover
        // comes from the bot's own (imperfect) read of the range.
        var bdir = (eye + dir * targetDist - origin).Normalized();
        float est = Mathf.Max(5f, targetDist * (1f + _rangeErr));
        var right = bdir.Cross(Vector3.Up);
        if (right.LengthSquared() > 1e-6f) bdir = bdir.Rotated(right.Normalized(), Ballistics.ZeroAngle(Def.MuzzleVel, Def.Drag, est));

        Ballistics.I.Fire(origin, bdir, Def.MuzzleVel * (1f + _rng.RandfRange(-0.006f, 0.006f)), Def.Drag, this, Def.Damage, Def.Name, intendedDist: targetDist,
            tag: $"{Brain.State}/{Brain.FireMode}{(MathF.Abs(_lean) > 0.1f ? "/lean" : "")}{(Crouched ? "/crouch" : "")}{(origin == eye ? "/eyeorigin" : "")}");
        SoundWorld.I.Emit(Def.Sound, origin, 0f, this, facing: bdir);
        Effects.I.MuzzleFlash(origin, dir);
        Telemetry.Shot(this, origin, eye + dir * targetDist, Def.Name, Brain.FireMode, Brain.Target?.Who);
        Aim.Kick(Def.VertKickDeg * _rng.RandfRange(0.85f, 1.15f), _rng.RandfRange(-1f, 1f) * Def.HorizKickDeg, Crouched ? 0.8f : 1f);
        return true;
    }

    /// <summary>
    /// Lob a frag to land near a point: solve the arc for a few launch angles and
    /// take the first one whose path isn't blocked by a wall or roof on the way.
    /// </summary>
    public bool ThrowGrenadeAt(Vector3 target, bool smoke = false)
    {
        if (smoke ? SmokeGrenades <= 0 : Grenades <= 0) return false;
        var from = EyePos + Vector3.Up * 0.1f;
        var flat = target - from;
        flat.Y = 0f;
        float d = flat.Length();
        if (d < 2f) return false;
        var dir = flat / d;
        float h = target.Y + 0.2f - from.Y;
        var space = GetWorld3D().DirectSpaceState;
        foreach (float deg in new[] { 30f, 45f, 60f })
        {
            float th = Mathf.DegToRad(deg), c = MathF.Cos(th);
            float denom = 2f * c * c * (d * MathF.Tan(th) - h);
            if (denom <= 0.01f) continue;
            float v = MathF.Sqrt(9.81f * d * d / denom);
            if (v > 22f) continue;
            v *= _rng.RandfRange(0.93f, 1.05f); // nobody throws perfectly
            var vel = dir * (v * c) + Vector3.Up * (v * MathF.Sin(th));
            if (!ArcClear(space, from, vel, d)) continue;

            var g = new Grenade { Thrower = this, Fuse = smoke ? 2.0f : 3.4f, Smoke = smoke };
            GetParent().AddChild(g);
            g.GlobalPosition = from + dir * 0.4f;
            g.LinearVelocity = vel;
            g.AddCollisionExceptionWith(this);
            if (smoke) SmokeGrenades--; else Grenades--;
            SoundWorld.I.Emit(Snd.Click, from, 0f, this); // pin
            return true;
        }
        return false;
    }

    static bool ArcClear(PhysicsDirectSpaceState3D space, Vector3 p0, Vector3 vel, float dist)
    {
        float hs = new Vector2(vel.X, vel.Z).Length();
        float T = dist / hs;
        var prev = p0;
        int steps = dist > 60f ? 20 : 12;
        for (int i = 1; i <= steps; i++)
        {
            float t = T * i / steps;
            var p = p0 + vel * t + 0.5f * Ballistics.Gravity * t * t;
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(prev, p, 1));
            if (hit.Count > 0)
            {
                var at = hit["position"].AsVector3() - p0;
                return new Vector2(at.X, at.Z).Length() > dist * 0.75f; // landing short of it is fine
            }
            prev = p;
        }
        return true;
    }

    public bool LeanSettled => MathF.Abs(_lean - LeanTarget) < 0.05f;

    /// <summary>
    /// Trace the exact path the next round would take (same origin and direction as
    /// TryFire, without spread): does it get to within <paramref name="slack"/> m of the point?
    /// </summary>
    public bool ShotReaches(Vector3 pt, float slack)
    {
        var eye = EyePos;
        float d = eye.DistanceTo(pt);
        var space = GetWorld3D().DirectSpaceState;
        var origin = _muzzle.GlobalPosition;
        if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, origin, 1, SelfOnly)).Count > 0) origin = eye;
        var end = eye + Aim.Dir * d;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(origin, end, 1, SelfOnly));
        return hit.Count == 0 || hit["position"].AsVector3().DistanceTo(pt) < slack;
    }

    // ---- role kit

    public Role RoleOf => Role;
    Role ICombatant.Role => Role;
    public float Hp => Health;
    public float AmmoLevel => MathF.Min(Def.Mags == 0 ? 1f : Mags.Rounds / (float)(Def.MagSize * Def.Mags), Ops?.StockLevel ?? 1f);

    void Stock()
    {
        Grenades = Roles.Frags(Role);
        SmokeGrenades = Role == Role.Leader ? 2 : 1;
        LauncherRounds = Role == Role.Grenadier ? 8 : 0;
        Rockets = RocketDef is WeaponDef rd ? rd.Mags + 1 : 0;
        Medkits = Role == Role.Medic ? 10 : 0;
        Sandbags = Role == Role.Engineer ? 3 : 0;
    }

    /// <summary>A medic's treatment: wounds dressed, and if down, back on their feet.</summary>
    public void Heal(float amount)
    {
        if (Body.Dead) return;
        bool wasDown = Body.Down;
        Body.Treat();
        if (wasDown && !Body.Down) StandUp();
    }

    public bool Resupply()
    {
        if (!Alive) return false;
        // Drones, grenades for them and batteries come up on the logistics truck or sit at a FOB, not in an ammo bearer's pack.
        if (Ops != null && (Vehicle.All.Any(v => !v.Destroyed && v.Def.Kind == VKind.Logistics && v.Team == Team && v.GlobalPosition.DistanceTo(FeetPos) < 20f)
                            || Fob.All.Any(f => f.Team == Team && f.GlobalPosition.DistanceTo(FeetPos) < 25f)) && Ops.Restock())
        {
            SoundWorld.I.Emit(Snd.Bag, EyePos, 0f, this);
            Comms.Say(this, "Drones restocked.");
            return true;
        }
        bool need = Mags.Rounds < Def.MagSize * Def.Mags || Grenades < Roles.Frags(Role) || (Role == Role.Grenadier && LauncherRounds < 8)
                    || (Rockets < (RocketDef?.Mags ?? -1) + 1) || (Role == Role.Medic && Medkits < 10) || (Role == Role.Engineer && Sandbags < 3);
        if (!need || !Alive) return false;
        Mags.Refill(Def.Mags);
        Stock();
        if (Ammo == 0 && !Reloading) StartReload();
        SoundWorld.I.Emit(Snd.Bag, EyePos, 0f, this);
        return true;
    }

    /// <summary>
    /// Lob a 40 mm round at a point: solve the launch angle (low arc if it's clear,
    /// else a high one over the obstacle), with the range error of a quick estimate.
    /// </summary>
    public bool FireLauncherAt(Vector3 target, float rangeErr, float yawErrDeg)
    {
        if (LauncherRounds <= 0 || Reloading) return false;
        var from = MuzzlePos;
        var flat = target - from;
        flat.Y = 0f;
        float d = flat.Length() * (1f + rangeErr);
        if (d < 20f) return false;
        var dir = flat.Normalized().Rotated(Vector3.Up, Mathf.DegToRad(yawErrDeg));
        float h = target.Y - from.Y, v = WeaponDef.Launcher.MuzzleVel, g = 9.81f;
        float disc = v * v * v * v - g * (g * d * d + 2f * h * v * v);
        if (disc < 0f) return false; // out of range
        var space = GetWorld3D().DirectSpaceState;
        foreach (float sign in new[] { -1f, 1f })
        {
            float th = MathF.Atan((v * v + sign * MathF.Sqrt(disc)) / (g * d));
            var vel = dir * (v * MathF.Cos(th)) + Vector3.Up * (v * MathF.Sin(th));
            if (!ArcClear(space, from, vel, d)) continue;
            LauncherRounds--;
            Ballistics.I.Fire(from, vel.Normalized(), v, 0f, this, 0f, "40mm", explosive: true, armM: WeaponDef.Launcher.ArmM, fragR: WeaponDef.Launcher.FragR, power: WeaponDef.Launcher.Power);
            SoundWorld.I.Emit(Snd.Launcher, from, 0f, this);
            Effects.I.MuzzleFlash(from, vel.Normalized());
            Telemetry.Shot(this, from, target, "40mm", "launcher", Brain.Target?.Who);
            _cool = 1.5f;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Put a rocket into a vehicle: led for its motion over the flight time, held high for
    /// the drop, fired from the shoulder along our current aim (the brain lays it first).
    /// </summary>
    public bool FireRocket(Vector3 aimPoint, float range, Vehicle? homing = null)
    {
        var rd = RocketDef;
        if (rd == null || Rockets <= 0 || _cool > 0f) return false;
        Rockets--;
        _cool = 3f;
        var from = EyePos + BotAim.DirFrom(Aim.Yaw, 0f).Cross(Vector3.Up) * 0.2f;
        var dir = (aimPoint - from).Normalized();
        var right = dir.Cross(Vector3.Up);
        if (right.LengthSquared() > 1e-6f) dir = dir.Rotated(right.Normalized(), Ballistics.ZeroAngle(rd.MuzzleVel, rd.Drag, range));
        var perp = dir.Cross(Vector3.Up).Normalized();
        float spread = Mathf.DegToRad(rd.SpreadAdsDeg + (1f - P.Skill) * 0.4f);
        dir = dir.Rotated(perp.Rotated(dir, _rng.Randf() * Mathf.Tau), MathF.Sqrt(_rng.Randf()) * spread).Normalized();
        Ballistics.I.Fire(from, dir, rd.MuzzleVel, rd.Drag, this, rd.Damage, rd.Name, explosive: true, armM: rd.ArmM,
            pen: rd.Pen, vehDamage: rd.VehDamage, crater: rd.Crater, fragR: rd.FragR, power: rd.Power, rocket: true, homing: homing, heavyCrack: true);
        SoundWorld.I.Emit(Snd.Rocket, from, 0f, this);
        Effects.I.MuzzleDust(from - dir * 2f, -dir);
        Telemetry.Shot(this, from, aimPoint, rd.Name, "rocket", null);
        return true;
    }

    /// <param name="keep">Put the magazine coming off back in a pouch (slower) rather than drop it (see Magazines).</param>
    public void StartReload(bool keep = true)
    {
        if (Reloading || !Mags.Worth(Ammo, Def)) return;
        _reloadEmpty = Ammo == 0;
        _reloadKeep = keep;
        bool stow = keep && Magazines.InMag(Ammo, Def) > 0;
        _reloadDur = ((_reloadEmpty ? Def.ReloadEmpty : Def.Reload) + (stow ? Magazines.RetainTime : 0f)) * _rng.RandfRange(0.95f, 1.2f);
        _reloadT = _reloadDur;
        _reloadStage = 0;
    }
    bool _reloadKeep;

    void UpdateReload(float dt)
    {
        if (_reloadT <= 0f) return;
        _reloadT -= dt;
        float prog = 1f - _reloadT / _reloadDur;
        var at = EyePos;
        if (_reloadStage == 0 && prog > 0.18f) { SoundWorld.I.Emit(Snd.MagOut, at, 0f, this); _reloadStage = 1; }
        if (_reloadStage == 1 && prog > 0.6f) { SoundWorld.I.Emit(Snd.MagIn, at, 0f, this); _reloadStage = 2; }
        if (_reloadStage == 2 && _reloadEmpty && prog > 0.85f) { SoundWorld.I.Emit(Snd.Bolt, at, 0f, this); _reloadStage = 3; }
        if (_reloadT <= 0f)
        {
            // A round stays chambered through a reload that isn't from empty.
            int got = Mags.Swap(Magazines.InMag(Ammo, Def), _reloadKeep);
            Ammo = _reloadEmpty || Def.Chambered == 0 ? got : got + 1;
        }
    }

    // ================================================================ damage

    public void TakeHit(HitInfo hit)
    {
        if (Body.Dead) return;
        bool wasDown = Body.Down;
        var region = Body.RegionFor(this, hit.Point, hit.Dir, hit.Zone);
        if (hit.Shooter != null) _lastShooter = hit.Shooter;
        if (hit.Shooter != null && hit.Shooter != this && hit.Shooter.Team != Team) _lastEnemy = hit.Shooter;
        var res = Body.Hit(region, hit.Damage / Combatants.ZoneMultiplier(hit.Zone) / 50f, _rng);
        if (hit.Shooter is Bot shooter && !wasDown) shooter.HitsLanded++;
        if (res == HitResult.Dead) { Die(hit); return; }
        if (wasDown) return;
        Suppression = Mathf.Min(1f, Suppression + 0.35f);
        Aim.Flinch(_rng);
        if (res == HitResult.Downed) { GoDown(hit); return; }
        Brain.OnHurt(hit);
    }

    /// <summary>Kill credit for a bleed-out goes to whoever caused it (if we know).</summary>
    HitInfo BleedHit() => new() { Shooter = _lastEnemy ?? _lastShooter, Point = ChestPos, Dir = Vector3.Down, Damage = 0f, Zone = HitZone.Torso, Weapon = "blood loss" };

    /// <summary>Hit badly enough to collapse: on the ground, out of the fight, bleeding.</summary>
    void GoDown(HitInfo hit)
    {
        // Dragged out, or tumbles out; but not from a helicopter in flight (see Vehicle.CasualtyAboard).
        if (Ride is { Def.Air: true, Landed: false } air) air.CasualtyAboard(this);
        else Ride?.Leave(this);
        var vel = Velocity; // which way his weight is going, for the fall
        _downHit = hit;
        Velocity = Vector3.Zero;
        Stop();
        LeanTarget = 0f;
        Collapse(hit, vel, 0.45f, 0.14f, 0.4f, 0.6f);
        SetCrouch(true);
        _proneT = 0f;
        _stanceT = 0f;
        _eyeH = 0.38f;
        _chestH = 0.3f;
        _capsule.Height = 0.7f;
        _col.Position = new Vector3(0f, 0.35f, 0f);
        Combatants.ReportDowned(this, hit);
        Comms.Say(this, _rng.Randf() < 0.5f ? "I'm down! Medic!" : "I'm hit, I'm hit! I can't get up!");
        _moanAt = Clock.Now + _rng.RandfRange(10f, 18f);
    }

    /// <summary>
    /// The body goes down. Lying (or most of the way there) he stays where he lies, face down, and just slumps.
    /// On his feet he falls the way his weight is already going: forward when running, back when backing off,
    /// otherwise away from the hit, and when there's nothing to go by (bleeding out) whichever way he sags.
    /// (A prone man used to be tipped over by a coin flip too: half the time the tween swung him from face down
    /// through upright onto his back, so a man shot lying down stood up before he fell.)
    /// </summary>
    void Collapse(HitInfo hit, Vector3 vel, float tip, float y, float roll, float dur)
    {
        var tw = CreateTween().SetParallel();
        tw.TweenProperty(_pelvis, "rotation", Vector3.Zero, dur); // the hips come round square with the shoulders
        if (_proneT > 0.5f)
        {
            tw.TweenProperty(_visual, "rotation", new Vector3(-Mathf.Pi * 0.5f, 0f, _rng.RandfRange(-0.25f, 0.25f)), dur * 0.6f);
            tw.TweenProperty(_visual, "position", new Vector3(0f, 0.1f, 0.85f), dur * 0.6f);
            return;
        }
        var fwd = BotAim.DirFrom(Aim.Yaw, 0f);
        var right = fwd.Cross(Vector3.Up);
        float moving = Flat(vel).Dot(fwd), shot = Flat(hit.Dir).Dot(fwd);
        // Rotation about X: positive tips him onto his back, negative onto his face. A body part way down to
        // prone carries on the way it was going.
        float dir = _proneT > 0f ? -1f
            : MathF.Abs(moving) > 1.5f ? -MathF.Sign(moving)
            : MathF.Abs(shot) > 0.3f ? MathF.Sign(-shot)
            : _rng.Randf() < 0.5f ? -1f : 1f;
        // Hit from the side, he twists away from it as he goes.
        float side = Flat(hit.Dir).Dot(right);
        float z = Mathf.Clamp(-side * roll + _rng.RandfRange(-roll, roll) * 0.5f, -roll, roll);
        tw.TweenProperty(_visual, "rotation", new Vector3(dir * Mathf.Pi * tip, 0f, z), dur)
          .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tw.TweenProperty(_visual, "position", new Vector3(0f, y, 0f), dur);
    }

    /// <summary>Lying there: bleeding, calling for a medic, maybe patching themselves up.</summary>
    void DownedTick(float dt)
    {
        // Aboard (hit in flight), the seat holds him; otherwise he settles on the ground.
        if (Ride == null)
        {
            Velocity = new Vector3(0f, IsOnFloor() ? 0f : Velocity.Y - 9.81f * dt, 0f);
            MoveAndSlide();
        }
        switch (Body.Tick(dt, Clock.Now))
        {
            case HitResult.Dead: Die(_downHit ?? BleedHit()); return;
        }
        if (Clock.Now > _moanAt)
        {
            _moanAt = Clock.Now + _rng.RandfRange(12f, 20f);
            // Still has hands: slap a dressing on the worst of it.
            if (Body.NeedsSelfAid && _rng.Randf() < 0.6f) Body.SelfAid();
            Comms.Say(this, "Medic!");
            SoundWorld.I.Emit(Snd.Bag, EyePos, -6f, this);
        }
    }

    public void Mount(Vehicle v, int seat)
    {
        Ride = v;
        SeatIdx = seat;
        Stop();
        var s = v.Def.Seats[seat];
        CollisionLayer = s.Exposed ? 2u : 0u;
        SetBodyVisible(s.Exposed);
        Senses.IgnoreBody(v.GetRid(), true);
        SetCrouch(false);
        // Seated (and not animated while aboard): the body is in the seat now, not halfway up off the ground.
        if (!Body.Down) SnapPose();
    }

    /// <summary>Moved to another seat of the same vehicle as a casualty (see Vehicle.TakeControls).</summary>
    public void MovedTo(int seat) => SeatIdx = seat;

    public void Dismount(Vector3 at)
    {
        var v = Ride;
        Ride = null;
        SeatIdx = -1;
        if (v != null) Senses.IgnoreBody(v.GetRid(), false);
        // Onto the ground under the door (a long way down, for a door gunner killed in the air: his body lands
        // there, rather than hanging in the sky where the seat was).
        var q = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 3f, at + Vector3.Down * (v is { Def.Air: true, Landed: false } ? 1500f : 6f), Layers.World);
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
        GlobalPosition = (hit.Count > 0 ? hit["position"].AsVector3() : at) + Vector3.Up * 0.1f;
        CollisionLayer = Body.Dead ? 0u : 2u;
        SetBodyVisible(true);
        Brain.OnDismounted();
    }

    public void SyncSeat(Vector3 eye, float yawRad)
    {
        GlobalPosition = eye - Vector3.Up * 1.62f;
        Aim.Yaw = Mathf.RadToDeg(yawRad);
    }

    void StandUp()
    {
        _downHit = null;
        _capsule.Height = 1.25f;
        _col.Position = new Vector3(0f, 0.625f, 0f);
        var tw = CreateTween().SetParallel();
        tw.TweenProperty(_visual, "rotation", Vector3.Zero, 0.8f);
        tw.TweenProperty(_visual, "position", Vector3.Zero, 0.8f);
        Brain.OnRevived();
        Comms.Say(this, "Thanks, doc. I'm good.");
    }

    public void OnNearMiss(float distance, Vector3 from)
    {
        if (!Alive) return;
        Suppression = Mathf.Min(1f, Suppression + Mathf.Clamp(1.1f / (distance + 0.8f), 0.05f, 0.5f) * (1.3f - P.Courage * 0.6f));
        Brain.OnShotAt(from);
    }

    public void OnBlast(Vector3 pos, float power = 1f)
    {
        if (!Alive) return;
        var chest = ChestPos;
        float d = pos.DistanceTo(chest) / MathF.Cbrt(power); // as far from a grenade as this is from the real thing
        if (d > 30f) return;
        // A wall or a hill between us and it takes most of the overpressure: it hits as if much further off.
        if (GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(pos + Vector3.Up * 0.3f, chest, Layers.World)).Count > 0)
            d = d * 2.2f + 2f;
        if (d > 30f) return;
        Suppression = Mathf.Min(1f, Suppression + (1f - d / 30f) * 1.2f);
        if (d < 12f) Body.Blast(1f - d / 12f);
    }

    void Die(HitInfo hit)
    {
        // Killed at an open hatch or a door gun: he falls out, rather than hanging where the seat was once
        // the vehicle drives off. (One killed inside stays inside, out of sight: the vehicle frees the seat.)
        if (Ride is { } v && SeatIdx >= 0 && v.Def.Seats[SeatIdx].Exposed) v.Leave(this);
        bool wasDown = _downHit != null && !Alive;
        var vel = Velocity;
        CollisionLayer = 0;
        Velocity = Vector3.Zero;
        if (hit.Shooter is Bot killer && killer != this && killer.Team != Team) killer.Kills++;
        Combatants.ReportKill(this, hit);
        if (wasDown) return; // already on the ground

        // Crumple: tip over at the feet, forward or back (or, lying, go still where he lies).
        Collapse(hit, vel, 0.48f, 0.12f, 0.3f, 0.55f);
    }

    // ================================================================ body

    static StandardMaterial3D Mat(Color c) => new() { AlbedoColor = c, Roughness = 0.9f };

    static Node3D Pivot(Node3D parent, Vector3 pos)
    {
        var n = new Node3D();
        parent.AddChild(n);
        n.Position = pos;
        return n;
    }

    static void Part(Node3D parent, Mesh mesh, Vector3 pos, Material m)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = m };
        parent.AddChild(mi);
        mi.Position = pos;
    }

    void BuildVisual()
    {
        var skin = Mat(new Color(0.62f, 0.48f, 0.38f));
        var cloth = Mat(new Color(0.40f, 0.39f, 0.32f));
        var team = Valley.TeamColors[Math.Clamp(TeamId, 0, Valley.TeamColors.Length - 1)];
        var vest = Mat(team);
        var helmet = Mat(team.Darkened(0.4f));
        var boots = Mat(new Color(0.12f, 0.1f, 0.08f));

        _visual = Pivot(this, Vector3.Zero);
        // The legs hang from a pelvis of their own that can turn under the torso (see Animate).
        _pelvis = Pivot(_visual, Vector3.Zero);
        _hipL = Pivot(_pelvis, new Vector3(-0.11f, 0.9f, 0f));
        _hipR = Pivot(_pelvis, new Vector3(0.11f, 0.9f, 0f));
        foreach (var hip in new[] { _hipL, _hipR })
        {
            Part(hip, new BoxMesh { Size = new Vector3(0.15f, 0.86f, 0.17f) }, new Vector3(0f, -0.43f, 0f), cloth);
            Part(hip, new BoxMesh { Size = new Vector3(0.16f, 0.1f, 0.27f) }, new Vector3(0f, -0.85f, -0.04f), boots);
        }
        _torso = Pivot(_visual, new Vector3(0f, 0.9f, 0f));
        Part(_torso, new BoxMesh { Size = new Vector3(0.38f, 0.56f, 0.24f) }, new Vector3(0f, 0.3f, 0f), cloth);
        Part(_torso, new BoxMesh { Size = new Vector3(0.42f, 0.4f, 0.29f) }, new Vector3(0f, 0.3f, 0f), vest);
        _head = Pivot(_torso, new Vector3(0f, 0.62f, 0f));
        Part(_head, new SphereMesh { Radius = 0.11f, Height = 0.22f }, new Vector3(0f, 0.1f, 0f), skin);
        Part(_head, new BoxMesh { Size = new Vector3(0.26f, 0.1f, 0.28f) }, new Vector3(0f, 0.19f, 0f), helmet);
        _arms = Pivot(_torso, new Vector3(0f, 0.56f, 0f));
        Part(_arms, new BoxMesh { Size = new Vector3(0.09f, 0.09f, 0.42f) }, new Vector3(-0.13f, -0.07f, -0.2f), cloth);
        Part(_arms, new BoxMesh { Size = new Vector3(0.09f, 0.09f, 0.3f) }, new Vector3(0.17f, -0.09f, -0.08f), cloth);
        RoleProps(helmet);
        var (gun, muzzle) = Def.Build();
        _arms.AddChild(gun);
        gun.Position = new Vector3(0.07f, 0.0f, -0.1f);
        _muzzle = muzzle;
    }

    /// <summary>What a role carries, visibly: a radio for the leader, a big pack for the ammo bearer, and so on.</summary>
    void RoleProps(StandardMaterial3D helmet)
    {
        var pack = Mat(new Color(0.3f, 0.28f, 0.2f));
        switch (Role)
        {
            case Role.Leader:
                Part(_torso, new BoxMesh { Size = new Vector3(0.24f, 0.26f, 0.12f) }, new Vector3(0f, 0.34f, 0.2f), pack);
                Part(_torso, new CylinderMesh { TopRadius = 0.005f, BottomRadius = 0.008f, Height = 0.7f }, new Vector3(0.08f, 0.78f, 0.24f), Mat(new Color(0.1f, 0.1f, 0.1f)));
                break;
            case Role.Medic:
                Part(_torso, new BoxMesh { Size = new Vector3(0.28f, 0.28f, 0.12f) }, new Vector3(0f, 0.32f, 0.2f), pack);
                Part(_head, new BoxMesh { Size = new Vector3(0.1f, 0.03f, 0.1f) }, new Vector3(0f, 0.25f, 0f), Mat(new Color(0.9f, 0.9f, 0.9f)));
                Part(_head, new BoxMesh { Size = new Vector3(0.03f, 0.031f, 0.14f) }, new Vector3(0f, 0.25f, 0f), Mat(new Color(0.75f, 0.1f, 0.1f)));
                break;
            case Role.Ammo:
                Part(_torso, new BoxMesh { Size = new Vector3(0.36f, 0.46f, 0.2f) }, new Vector3(0f, 0.3f, 0.24f), pack);
                break;
            case Role.Engineer:
                Part(_torso, new BoxMesh { Size = new Vector3(0.3f, 0.34f, 0.16f) }, new Vector3(0f, 0.3f, 0.22f), pack);
                Part(_torso, new BoxMesh { Size = new Vector3(0.05f, 0.5f, 0.05f) }, new Vector3(0.14f, 0.45f, 0.3f), Mat(new Color(0.35f, 0.25f, 0.15f))); // shovel
                break;
            case Role.Marksman:
                Part(_head, new BoxMesh { Size = new Vector3(0.3f, 0.06f, 0.32f) }, new Vector3(0f, 0.22f, 0f), Mat(new Color(0.33f, 0.36f, 0.22f))); // scrim
                break;
        }
    }

    void Animate(float dt)
    {
        var hv = new Vector3(Velocity.X, 0f, Velocity.Z);
        float spd = hv.Length();
        // Getting down to prone, and up from it, goes by way of the knees: he drops to a knee, then goes forward
        // onto the ground, and the legs straighten out behind once he's flat.
        bool kneeling = Crouched || (_proneT > 0.05f && (!Prone || _proneT < 0.95f));
        _crouchT = Mathf.MoveToward(_crouchT, kneeling ? 1f : 0f, dt * 5f);
        _crouchWalkT = Mathf.MoveToward(_crouchWalkT, Crouched && spd > 0.5f && _proneT <= 0f ? 1f : 0f, dt * 4f);
        // Prone: the whole body lies along the ground, head forward, rifle out in front of him. (Left
        // alone when upright, so the tweens of going down and being helped up play out.)
        if (Prone || _proneT > 0f)
        {
            _proneT = Mathf.MoveToward(_proneT, Prone ? 1f : 0f, dt * 1.2f);
            _visual.Rotation = new Vector3(-_proneT * Mathf.Pi * 0.5f, 0f, 0f);
            _visual.Position = new Vector3(0f, 0.12f * _proneT, 0.85f * _proneT);
        }
        Pose();

        // The hips go where he's going, the shoulders and rifle where he's looking, and the spine between them
        // twists only so far. Going backwards he stays square to the front and steps back, rather than turning
        // his hips round. Lying down there's nothing to twist: the body turns as one. (The whole figure, legs
        // and all, used to turn with the aim, so a man running one way while he watched another ran sideways,
        // or backwards, with a forward stride.)
        const float MaxTwist = 80f;
        float lieFlat = 1f - _proneT;
        float legGoal = Aim.Yaw;
        if (spd > 0.4f && lieFlat > 0.5f)
        {
            float rel = BotAim.Wrap(Mathf.RadToDeg(MathF.Atan2(-hv.X, -hv.Z)) - Aim.Yaw);
            if (MathF.Abs(rel) > 110f) rel = BotAim.Wrap(rel + 180f);
            legGoal = Aim.Yaw + Mathf.Clamp(rel, -MaxTwist, MaxTwist);
        }
        // Feet turn at a walking pace's worth of steps, not at the speed a man can swing a rifle.
        float legRate = (Crouched ? 160f : 240f) * dt;
        float off = BotAim.Wrap(legGoal - _legYaw);
        _legYaw = BotAim.Wrap(_legYaw + Mathf.Clamp(off, -legRate, legRate));
        // Swung round past what the spine allows, the shoulders drag the hips with them.
        float twist = Mathf.Clamp(BotAim.Wrap(_legYaw - Aim.Yaw), -MaxTwist * lieFlat, MaxTwist * lieFlat);
        _legYaw = BotAim.Wrap(Aim.Yaw + twist);
        _pelvis.Rotation = new Vector3(0f, Mathf.DegToRad(twist), 0f);

        // The stride goes along the way the legs point; what's left over sideways is a side-step.
        var legFwd = BotAim.DirFrom(_legYaw, 0f);
        float fore = spd > 0.05f ? hv.Dot(legFwd) / spd : 1f, across = spd > 0.05f ? hv.Dot(legFwd.Cross(Vector3.Up)) / spd : 0f;
        _walkPhase += spd * dt * 2.4f;
        float swing = MathF.Sin(_walkPhase) * Mathf.Clamp(spd / 3.4f, 0f, 1f) * 0.55f * fore;
        float spread = MathF.Abs(across) * Mathf.Clamp(spd / 1.8f, 0f, 1f) * 0.22f * (0.5f + 0.5f * MathF.Sin(_walkPhase));
        // Crouched and walking he's up off the knee, bent at the hips, both legs working.
        float hipY = Mathf.Lerp(0.9f, Mathf.Lerp(0.45f, 0.7f, _crouchWalkT), _crouchT);
        float kneel = _crouchT * 1.2f;
        float bendL = Mathf.Lerp(kneel * 0.5f, _crouchT * 0.6f, _crouchWalkT), bendR = Mathf.Lerp(kneel, _crouchT * 0.6f, _crouchWalkT);

        Rotation = new Vector3(0f, Mathf.DegToRad(Aim.Yaw), 0f);
        _torso.Position = new Vector3(_lean * 0.1f, hipY, 0f);
        _torso.Rotation = new Vector3(-_crouchT * (0.2f + 0.25f * _crouchWalkT) + (Reloading ? 0.12f : 0f), 0f, -_lean * 0.35f);
        _hipL.Position = new Vector3(-0.11f, hipY, 0f);
        _hipR.Position = new Vector3(0.11f, hipY, 0f);
        _hipL.Rotation = new Vector3(bendL + swing, 0f, -spread);
        _hipR.Rotation = new Vector3(bendR - swing, 0f, spread);
        _arms.Rotation = new Vector3(Mathf.DegToRad(Aim.Pitch) + (Reloading ? -0.5f : 0f) + _proneT * Mathf.Pi * 0.5f, 0f, 0f);
        // Leaning: the rifle comes out past the corner with the head, not just the eyes.
        _arms.Position = new Vector3(_lean * 0.2f, 0.56f, 0f);
        _arms.RotationDegrees = new Vector3(_arms.RotationDegrees.X, 0f, _lean * 20f);
        _head.Rotation = new Vector3(Mathf.DegToRad(Aim.Pitch) * 0.6f + _proneT * Mathf.Pi * 0.45f, 0f, 0f);
    }
}
