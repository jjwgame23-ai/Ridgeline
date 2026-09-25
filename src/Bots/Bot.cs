using Godot;

namespace Ridgeline;

public enum MoveMode { Walk, Run, Sprint }

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
    double _moanAt;
    public float Suppression;
    /// <summary>Same wind as the player: about 14 s of flat-out sprint, then you have to walk it off.</summary>
    public float Stamina = 1f;
    bool _winded;
    float _sinceSprint = 10f;
    public int Ammo, Mags;
    public int ShotsFired, HitsLanded, Kills, ShotsBlocked, ShotsBlockedNear, ShotsWide;
    public Vector3? StrafeDir; // set by the brain to move directly instead of along a path
    public int Grenades = 2;
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

    public bool Crouched { get; private set; }
    public bool Reloading => _reloadT > 0f;
    public MoveMode Mode { get; private set; } = MoveMode.Run;
    public bool Moving => new Vector2(Velocity.X, Velocity.Z).Length() > 0.5f;
    public bool Arrived => !_hasGoal;
    public Vector3 GoalPos => _goal;
    public Vector3[] PathPoints => _path;
    public int PathIndex => _pathIdx;
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
    public Vector3 EyePos => GlobalPosition + Vector3.Up * ((Crouched ? 1.17f : 1.62f) - MathF.Abs(_lean) * 0.06f) + Right * (_lean * 0.38f);
    public Vector3 ChestPos => GlobalPosition + Vector3.Up * (Crouched ? 0.85f : 1.25f) + Right * (_lean * 0.2f);
    Vector3 Right => BotAim.DirFrom(Aim.Yaw, 0f).Cross(Vector3.Up);
    public Vector3 Vel => Velocity;
    public float BodyHeight => _capsule.Height;
    public double LastShotTime { get; private set; } = -99;
    public Rid BodyRid => GetRid();
    public Vector3 MuzzlePos => _muzzle.GlobalPosition;

    readonly RandomNumberGenerator _rng = new();
    CapsuleShape3D _capsule = null!;
    CollisionShape3D _col = null!;
    Node3D _visual = null!, _torso = null!, _head = null!, _arms = null!, _hipL = null!, _hipR = null!, _muzzle = null!;
    Vector3[] _path = Array.Empty<Vector3>();
    int _pathIdx;
    Vector3 _goal, _rawGoal, _lastProgressPos;
    bool _hasGoal, _reloadEmpty, _partial;
    float _routeCheckT, _doorT;
    float _lean, _cool, _reloadT, _reloadDur, _senseT, _thinkT, _stuckT, _repathT, _walkPhase, _crouchT, _rangeErr;
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

        Ammo = Def.MagSize + 1;
        Mags = Def.Mags;
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
    const float GridCell = 2f;

    static (int, int) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / GridCell), (int)MathF.Floor(p.Z / GridCell));

    static void BuildGrid()
    {
        ulong frame = Engine.GetPhysicsFrames();
        if (frame == _gridFrame) return;
        _gridFrame = frame;
        foreach (var l in _grid.Values) l.Clear();
        foreach (var c in Combatants.All)
        {
            if (!c.Alive) continue;
            var k = CellOf(c.FeetPos);
            if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new List<ICombatant>();
            l.Add(c);
        }
    }

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
        _lean = Mathf.MoveToward(_lean, LeanTarget, dt * 4f);
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
        foreach (float h in new[] { 1.0f })
        {
            _probeQ.Transform = new Transform3D(Basis.Identity, a + Vector3.Up * h);
            _probeQ.Motion = b - a;
            _probeQ.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var r = space.CastMotion(_probeQ);
            if (r.Length > 0 && r[0] < 0.999f) return false;
        }
        return true;
    }

    void Repath()
    {
        _lastRepath = Clock.Now;
        if (DirectClear(_rawGoal))
        {
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
    public static int StuckEvents;

    void Stuck()
    {
        StuckEvents++;
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

    public void SetCrouch(bool c)
    {
        if (c == Crouched) return;
        Crouched = c;
        float h = c ? 1.25f : 1.8f;
        _capsule.Height = h;
        _col.Position = new Vector3(_lean * 0.22f, h / 2f, 0);
    }

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
                if (pos.DistanceTo(_lastProgressPos) < 0.3f && _hasGoal) { Stuck(); if (_hasGoal) Repath(); }
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
        if (Brain.WantsAds) speed = Mathf.Min(speed, 2.2f);
        if (Mode == MoveMode.Sprint && wish.Dot(BotAim.DirFrom(Aim.Yaw, 0f)) < 0.5f) speed = 3.4f; // can't sprint sideways
        if (wish.Dot(BotAim.DirFrom(Aim.Yaw, 0f)) < -0.3f) speed *= 0.7f;

        // Don't walk through each other.
        BuildGrid();
        var cell = CellOf(pos);
        for (int gz = -1; gz <= 1; gz++)
        for (int gx = -1; gx <= 1; gx++)
        {
            if (!_grid.TryGetValue((cell.Item1 + gx, cell.Item2 + gz), out var near)) continue;
            foreach (var c in near)
            {
                if (c == this) continue;
                var away = Flat(pos - c.FeetPos);
                float d = away.Length();
                if (d < 0.8f && d > 0.001f) wish += away / d * (0.8f - d) * 2f;
            }
        }
        if (wish.LengthSquared() > 1f) wish = wish.Normalized();

        var hv = new Vector2(Velocity.X, Velocity.Z).MoveToward(new Vector2(wish.X, wish.Z) * speed, 10f * dt);
        float vy = IsOnFloor() ? 0f : Velocity.Y - 9.81f * dt;
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
        if (spd > 0.6f && Clock.Now > _stepAt)
        {
            float stride = Mode == MoveMode.Sprint && spd > 4f ? 1.05f : Crouched ? 0.6f : 0.78f;
            _stepAt = Clock.Now + stride / spd;
            float gain = spd > 4f ? 3f : Crouched ? -8f : spd < 2.5f ? -5f : 0f;
            SoundWorld.I.Emit(Snd.Footstep, GlobalPosition + Vector3.Up * 0.05f, gain, this);
        }
    }

    // ================================================================ weapon

    public bool TryFire(float targetDist)
    {
        if (Reloading || Ammo <= 0 || _cool > 0f) return false;
        Ammo--;
        ShotsFired++;
        _cool = 60f / Def.Rpm;
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
        var excl = new Godot.Collections.Array<Rid> { GetRid() };
        if (GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, origin, 1, excl)).Count > 0) origin = eye;
        // Rounds leave the muzzle and cross the line of sight at the target; holdover
        // comes from the bot's own (imperfect) read of the range.
        var bdir = (eye + dir * targetDist - origin).Normalized();
        float est = Mathf.Max(5f, targetDist * (1f + _rangeErr));
        var right = bdir.Cross(Vector3.Up);
        if (right.LengthSquared() > 1e-6f) bdir = bdir.Rotated(right.Normalized(), Ballistics.ZeroAngle(Def.MuzzleVel, Def.Drag, est));

        Ballistics.I.Fire(origin, bdir, Def.MuzzleVel * (1f + _rng.RandfRange(-0.006f, 0.006f)), Def.Drag, this, Def.Damage, Def.Name, intendedDist: targetDist,
            tag: $"{Brain.State}/{Brain.FireMode}{(MathF.Abs(_lean) > 0.1f ? "/lean" : "")}{(Crouched ? "/crouch" : "")}{(origin == eye ? "/eyeorigin" : "")}");
        SoundWorld.I.Emit(Def.Sound, origin, 0f, this);
        Effects.I.MuzzleFlash(origin, dir);
        Aim.Kick(Def.VertKickDeg * _rng.RandfRange(0.85f, 1.15f), _rng.RandfRange(-1f, 1f) * Def.HorizKickDeg, Crouched ? 0.8f : 1f);
        return true;
    }

    /// <summary>
    /// Lob a frag to land near a point: solve the arc for a few launch angles and
    /// take the first one whose path isn't blocked by a wall or roof on the way.
    /// </summary>
    public bool ThrowGrenadeAt(Vector3 target)
    {
        if (Grenades <= 0) return false;
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

            var g = new Grenade { Thrower = this, Fuse = 3.4f };
            GetParent().AddChild(g);
            g.GlobalPosition = from + dir * 0.4f;
            g.LinearVelocity = vel;
            g.AddCollisionExceptionWith(this);
            Grenades--;
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
        var excl = new Godot.Collections.Array<Rid> { GetRid() };
        if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, origin, 1, excl)).Count > 0) origin = eye;
        var end = eye + Aim.Dir * d;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(origin, end, 1, excl));
        return hit.Count == 0 || hit["position"].AsVector3().DistanceTo(pt) < slack;
    }

    // ---- role kit

    public Role RoleOf => Role;
    Role ICombatant.Role => Role;
    public float Hp => Health;
    public float AmmoLevel => MathF.Min(Def.Mags == 0 ? 1f : Mags / (float)Def.Mags, Ops?.StockLevel ?? 1f);

    void Stock()
    {
        Grenades = Roles.Frags(Role);
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
        bool need = Mags < Def.Mags || Grenades < Roles.Frags(Role) || (Role == Role.Grenadier && LauncherRounds < 8)
                    || (Rockets < (RocketDef?.Mags ?? -1) + 1) || (Role == Role.Medic && Medkits < 10) || (Role == Role.Engineer && Sandbags < 3);
        if (!need || !Alive) return false;
        Mags = Def.Mags;
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
            Ballistics.I.Fire(from, vel.Normalized(), v, 0f, this, 0f, "40mm", explosive: true, armM: WeaponDef.Launcher.ArmM);
            SoundWorld.I.Emit(Snd.Launcher, from, 0f, this);
            Effects.I.MuzzleFlash(from, vel.Normalized());
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
            pen: rd.Pen, vehDamage: rd.VehDamage, crater: rd.Crater, frags: rd.Frags, rocket: true, homing: homing);
        SoundWorld.I.Emit(Snd.Rocket, from, 0f, this);
        Effects.I.MuzzleDust(from - dir * 2f, -dir);
        return true;
    }

    public void StartReload()
    {
        if (Reloading || Mags <= 0 || Ammo > Def.MagSize) return;
        _reloadEmpty = Ammo == 0;
        _reloadDur = (_reloadEmpty ? Def.ReloadEmpty : Def.Reload) * _rng.RandfRange(0.95f, 1.2f);
        _reloadT = _reloadDur;
        _reloadStage = 0;
    }

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
            Ammo = _reloadEmpty ? Def.MagSize : Def.MagSize + 1;
            Mags--;
        }
    }

    // ================================================================ damage

    public void TakeHit(HitInfo hit)
    {
        if (Body.Dead) return;
        bool wasDown = Body.Down;
        var region = Body.RegionFor(this, hit.Point, hit.Dir, hit.Zone);
        if (hit.Shooter != null) _lastShooter = hit.Shooter;
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
    HitInfo BleedHit() => new() { Shooter = _lastShooter, Point = ChestPos, Dir = Vector3.Down, Damage = 0f, Zone = HitZone.Torso, Weapon = "blood loss" };

    /// <summary>Hit badly enough to collapse: on the ground, out of the fight, bleeding.</summary>
    void GoDown(HitInfo hit)
    {
        Ride?.Leave(this); // dragged out, or tumbles out
        _downHit = hit;
        Velocity = Vector3.Zero;
        Stop();
        LeanTarget = 0f;
        SetCrouch(true);
        _capsule.Height = 0.7f;
        _col.Position = new Vector3(0f, 0.35f, 0f);
        Combatants.ReportDowned(this, hit);
        Comms.Say(this, _rng.Randf() < 0.5f ? "I'm down! Medic!" : "I'm hit, I'm hit! I can't get up!");
        _moanAt = Clock.Now + _rng.RandfRange(10f, 18f);
        float dir = _rng.Randf() < 0.5f ? -1f : 1f;
        var tw = CreateTween().SetParallel();
        tw.TweenProperty(_visual, "rotation", new Vector3(dir * Mathf.Pi * 0.45f, 0f, _rng.RandfRange(-0.4f, 0.4f)), 0.6f)
          .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tw.TweenProperty(_visual, "position", new Vector3(0f, 0.14f, 0f), 0.6f);
    }

    /// <summary>Lying there: bleeding, calling for a medic, maybe patching themselves up.</summary>
    void DownedTick(float dt)
    {
        Velocity = new Vector3(0f, IsOnFloor() ? 0f : Velocity.Y - 9.81f * dt, 0f);
        MoveAndSlide();
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
    }

    public void Dismount(Vector3 at)
    {
        var v = Ride;
        Ride = null;
        SeatIdx = -1;
        if (v != null) Senses.IgnoreBody(v.GetRid(), false);
        var q = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 3f, at + Vector3.Down * 6f, Layers.World);
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
        float d = pos.DistanceTo(ChestPos) / MathF.Cbrt(power); // as far from a grenade as this is from the real thing
        if (d > 30f) return;
        Suppression = Mathf.Min(1f, Suppression + (1f - d / 30f) * 1.2f);
        if (d < 12f) Body.Blast(1f - d / 12f);
    }

    void Die(HitInfo hit)
    {
        bool wasDown = _downHit != null && !Alive;
        CollisionLayer = 0;
        Velocity = Vector3.Zero;
        if (hit.Shooter is Bot killer) killer.Kills++;
        Combatants.ReportKill(this, hit);
        if (wasDown) return; // already on the ground

        // Crumple: tip over at the feet, forward or back.
        float dir = _rng.Randf() < 0.5f ? -1f : 1f;
        var tw = CreateTween().SetParallel();
        tw.TweenProperty(_visual, "rotation", new Vector3(dir * Mathf.Pi * 0.48f, 0f, _rng.RandfRange(-0.3f, 0.3f)), 0.55f)
          .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        tw.TweenProperty(_visual, "position", new Vector3(0f, 0.12f, 0f), 0.55f);
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
        _hipL = Pivot(_visual, new Vector3(-0.11f, 0.9f, 0f));
        _hipR = Pivot(_visual, new Vector3(0.11f, 0.9f, 0f));
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
        float spd = new Vector2(Velocity.X, Velocity.Z).Length();
        _crouchT = Mathf.MoveToward(_crouchT, Crouched ? 1f : 0f, dt * 5f);
        _walkPhase += spd * dt * 2.4f;
        float swing = MathF.Sin(_walkPhase) * Mathf.Clamp(spd / 3.4f, 0f, 1f) * 0.55f;
        float hipY = Mathf.Lerp(0.9f, 0.45f, _crouchT);
        float kneel = _crouchT * 1.2f;

        Rotation = new Vector3(0f, Mathf.DegToRad(Aim.Yaw), 0f);
        _torso.Position = new Vector3(0f, hipY, 0f);
        _torso.Position = new Vector3(_lean * 0.1f, hipY, 0f);
        _torso.Rotation = new Vector3(-_crouchT * 0.2f + (Reloading ? 0.12f : 0f), 0f, -_lean * 0.35f);
        _hipL.Position = new Vector3(-0.11f, hipY, 0f);
        _hipR.Position = new Vector3(0.11f, hipY, 0f);
        _hipL.Rotation = new Vector3(kneel * 0.5f + swing, 0f, 0f);
        _hipR.Rotation = new Vector3(kneel - swing, 0f, 0f);
        _arms.Rotation = new Vector3(Mathf.DegToRad(Aim.Pitch) + (Reloading ? -0.5f : 0f), 0f, 0f);
        // Leaning: the rifle comes out past the corner with the head, not just the eyes.
        _arms.Position = new Vector3(_lean * 0.2f, 0.56f, 0f);
        _arms.RotationDegrees = new Vector3(_arms.RotationDegrees.X, 0f, _lean * 20f);
        _head.Rotation = new Vector3(Mathf.DegToRad(Aim.Pitch) * 0.6f, 0f, 0f);
    }
}
