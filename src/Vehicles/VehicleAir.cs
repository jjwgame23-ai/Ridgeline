using Godot;

namespace Ridgeline;

/// <summary>
/// Rotorcraft flight. Simple, but it flies like a helicopter: collective sets how hard
/// the rotor pulls along its axis, the cyclic tilts that axis (nose down to go forward,
/// bank to turn), pedals yaw. Speed builds from tilt and bleeds off to drag. Attitude
/// is flown by a stability system (the cyclic asks for an attitude rather than a rate),
/// which keeps it controllable with a mouse and lets bots fly with a simple autopilot.
///
/// Damage: an engine hit costs power (you may not be able to hold a hover), a tail-rotor
/// hit (the Immobile flag, for aircraft) sets it spinning. Destroyed in the air, it falls
/// and explodes where it hits. Touch down too hard, too fast or too tilted and it crashes.
/// </summary>
public partial class Vehicle
{
    // ---- pilot's inputs
    public float Collective = 0f;          // 0..1
    public float CyclicPitch, CyclicRoll;  // -1..1: nose down / bank right
    public float Pedal;                    // -1..1: yaw left
    public bool HoverAssist;

    Vector3 _vel;
    float _pitchA, _rollA, _rotorSpin;
    /// <summary>The yaw rate nothing at the controls is asking for (deg/s): what the main rotor's torque turns the fuselage at with the tail rotor gone (see <see cref="SpinUp"/>).</summary>
    float _spin;
    bool _landed = true, _doomed;
    float _wedged; // a falling wreck caught where it can neither rest nor fall on (see FallWreck)
    ICombatant? _doomBy;
    double _flaresUntil = -1;
    public int FlaresLeft;
    public double MissileWarning = -99;
    /// <summary>An air defence radar tracking us (the warning receiver hears it): lately, since when, and whose.</summary>
    public double RadarWarning = -99, RadarSince = -99;
    public Vehicle? RadarFrom;
    /// <summary>Getting out of a radar's sight (see HeliPilot): until when, from where it was, and to where.</summary>
    public double EvadeUntil;
    public Vector3 EvadeFrom, EvadeTo;
    /// <summary>A gunship with nowhere to fire from: where it waits, out of sight of the air defence it knows of.</summary>
    public Vector3 HoldAt;
    /// <summary>Where it last lifted off from (see HeliPilot: straight up first, clear of what's round the pad).</summary>
    public Vector3? LiftedFrom;

    // ---- bot pilot state
    public HeliMode AirMode;
    public Vector3? AttackPoint;         // what the gunship's runs are aimed at (a vehicle, a cluster)
    public int AttackPhase;
    public Vector3 RunFrom = Vector3.Back;
    /// <summary>The vehicle a gunship is going after with its missiles (an air defence gun first, then armour).</summary>
    public Vehicle? MissileFocus;
    /// <summary>A missile in the air that the aircraft is still steering (laser, radio): stay up, eyes on it, until then.</summary>
    public double GuidingUntil, MissileAwayAt = -1;
    // A gunship's pop-up attacks (see HeliPilot.AttackRun): where it's working from, and how high it must come up there to see.
    public Vector3? BattlePos, LastBattlePos;
    public float PopAgl;
    public double PhaseSince, NoPositionSince = -1, SeeCheckAt;
    public bool SeesTarget;
    // See and avoid (see HeliPilot.Avoid): the aircraft we're giving way to, until when, and whether we go over it.
    public Vehicle? AvoidFrom;
    public double AvoidCheckAt, AvoidUntil;
    public bool AvoidClimb;
    // The tops of whatever's under and ahead of it, for flying low (see HeliPilot.Tops).
    public double TopsAt;
    public float TopsCached;
    /// <summary>How steeply it must climb (metres up per metre on) to clear what's ahead at its low height.</summary>
    public float TopsGrade;
    public Vector3 TopsFrom, TopsDir;
    public double NextSalvo;
    public double NowT => Clock.Now;
    public int SalvoLeft;
    public Vector3 SalvoAt;
    double _salvoNext;

    public bool Landed => _landed;
    public bool Doomed => _doomed;
    public Vector3 Velocity3 => Def.Air ? _vel : Forward * Speed;
    public float AirSpeed => new Vector2(_vel.X, _vel.Z).Length();
    public float Agl { get; private set; }
    public bool FlaresActive => Clock.Now < _flaresUntil;

    void FlyReady()
    {
        FlaresLeft = Def.Flares;
        _landed = true;
    }

    void FlyTick(float dt)
    {
        // Doomed, and it's come to rest (settled rather than struck something): that's the end of it. (Before,
        // one that slid to a stop after hitting the ground sat there intact for the rest of the match, crewed,
        // its guns working, at minus a hundred times its hit points.)
        if (_doomed && _landed) { _doomed = false; Destroy(_doomBy); return; }
        // A pilot who's been hit and is slumped in his seat isn't flying it.
        bool piloted = Driver is { Alive: true } && !_doomed;
        float power = EngineHit ? 0.62f : 1f;
        if (!piloted)
        {
            // Nobody at the controls: it settles (or, in the air, goes down).
            Collective = _landed ? 0f : MathF.Max(0f, Collective - dt * 0.3f);
            CyclicPitch = CyclicRoll = Pedal = 0f;
        }
        if (_doomed) { Collective = 0.35f; _spin = 150f; }
        else SpinUp(dt, power);

        float pitchT = _landed ? 0f : CyclicPitch * 32f;
        float rollT = _landed ? 0f : CyclicRoll * 40f;
        _pitchA = Mathf.MoveToward(_pitchA, pitchT, 60f * dt);
        _rollA = Mathf.MoveToward(_rollA, rollT, 80f * dt);

        // Yaw: pedals (which do nothing with the tail rotor gone: there's nothing for them to pitch), plus the turn a
        // bank makes at speed, plus whatever the rotor's torque is turning it at. (_spin used to be set when the tail
        // rotor was lost, and never taken off again: landed, unpiloted, the airframe spun on the ground for good.)
        float yawRate = _landed ? 0f : (Immobile ? 0f : Pedal * 65f) + (AirSpeed > 12f ? -_rollA * 0.9f : 0f);
        if (!_landed) yawRate += _spin; // (on the ground the skids hold it: see Grounded for what it cost to put it down turning)
        _yaw += Mathf.DegToRad(yawRate) * dt;

        var basis = Basis.FromEuler(new Vector3(Mathf.DegToRad(-_pitchA), _yaw, Mathf.DegToRad(-_rollA)));
        float thrust = Mathf.Clamp(Collective, 0f, 1f) * Def.Lift * power;
        var accel = basis.Y * thrust + Vector3.Down * 9.81f;
        var horiz = new Vector3(_vel.X, 0f, _vel.Z);
        accel -= horiz * horiz.Length() * (9.81f * 0.62f / (Def.AirSpeed * Def.AirSpeed)); // drag: top speed at full forward tilt
        accel -= _vel * 0.05f;
        accel.Y -= _vel.Y * MathF.Abs(_vel.Y) * 0.04f; // climb and sink rates top out around 15 m/s
        _vel += accel * dt;

        if (_landed)
        {
            if (thrust > 10.3f && piloted) { _landed = false; _vel.Y = MathF.Max(_vel.Y, 0.5f); LiftedFrom = GlobalPosition; }
            else
            {
                _vel = Vector3.Zero;
                SnapToGround(dt);
                // On the ground: anyone hit in flight is lifted out now (see CasualtyAboard).
                for (int i = 0; i < Occupants.Length; i++)
                    if (Occupants[i] is { Downed: true } hurt) Leave(hurt);
                _rotorSpin += dt * (Collective > 0.05f || piloted ? 25f : 4f);
                SpinRotors();
                return;
            }
        }

        GlobalBasis = basis;
        var col = MoveAndCollide(_vel * dt);
        _trail?.MoveTo(Center);
        if (col != null) Touch(col);

        // Height above whatever is below. (From a little above the skids: from right at them, touching the ground,
        // the ray could start inside the ground and see nothing: one flying into a hillside read 800 m up.)
        var excl = new Godot.Collections.Array<Rid> { GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(GlobalPosition + Vector3.Up * 1.5f, GlobalPosition + Vector3.Down * 800f, Layers.World | Layers.Trees, excl));
        Agl = hit.Count > 0 ? MathF.Max(0f, GlobalPosition.Y - hit["position"].AsVector3().Y) : 800f;
        // Settling gently onto flat ground from a hover (not pulling up off it): that's a landing. (It asked for under
        // 55% collective: with a damaged engine a hover takes 72%, and a crippled gunship hung a metre over its pad,
        // for good.)
        if (Agl < Def.GroundClear + 0.35f && _vel.Y < 0.5f && _vel.Y > -3.5f && AirSpeed < 4f && MathF.Abs(_pitchA) < 12f && MathF.Abs(_rollA) < 12f && thrust < 9.81f * 1.1f)
        {
            _pitchA = _rollA = 0f;
            Grounded();
        }
        _rotorSpin += dt * 25f;
        SpinRotors();

        // A rocket salvo in progress (a bot pilot's): one every 0.15 s, aimed at the point with a little help.
        if (SalvoLeft > 0 && Clock.Now >= _salvoNext)
        {
            int pods = Def.Turrets.FindIndex(t => t.Fixed);
            if (pods >= 0)
            {
                var from = Turrets[pods].Muzzle.GlobalPosition;
                float d = from.DistanceTo(SalvoAt);
                var dir = (SalvoAt - from).Normalized();
                var right = dir.Cross(Vector3.Up).Normalized();
                dir = dir.Rotated(right, Ballistics.ZeroAngle(600f, 0.0001f, d));
                Fire(pods, false, 0f, dir);
            }
            SalvoLeft--;
            _salvoNext = Clock.Now + 0.15;
        }
    }

    /// <summary>
    /// With the tail rotor gone nothing balances the main rotor's torque, and the fuselage turns the other way at a
    /// rate that follows the power being delivered: about 110 deg/s at the power that holds a hover (reports of
    /// tail-rotor losses in a hover give a turn of a few seconds a rev, faster with more power), less as the pilot
    /// reduces power (autorotation is the drill: it takes the torque away), and less again with airspeed as the fin
    /// starts to weathercock it: near enough held straight from about 25 m/s (~50 kt), which is why the drill is a run-on
    /// landing. The turn builds and dies away over a few seconds (the fuselage's inertia), quicker on the ground,
    /// where the skids drag, and with nobody at the controls (rotor idling, engine shut down) there's no torque at all.
    /// </summary>
    void SpinUp(float dt, float power)
    {
        float target = 0f;
        if (Immobile && !_landed)
        {
            float hover = 9.81f / (Def.Lift * power);
            float fin = 1f - 0.9f * Mathf.Clamp((AirSpeed - 8f) / 17f, 0f, 1f);
            target = 110f * Mathf.Clamp(Collective / hover, 0f, 1.3f) * fin;
        }
        _spin = Mathf.MoveToward(_spin, target, dt * (target > _spin ? 60f : _landed ? 240f : 90f));
    }

    /// <summary>
    /// Onto the skids. Turning as it does it, the skids dig in: sideways at the speed the skids are going round (the
    /// yaw rate at their distance from the mast, about a quarter of its length). That's a hard landing that damages it,
    /// and above a few m/s a roll-over, ever more likely the faster (dynamic rollover starts with a skid that catches
    /// while the fuselage is still moving sideways): the rotor hits the ground and it's a wreck. So a helicopter with
    /// its tail rotor gone that's put down at power, in a hover, mostly comes down damaged or crashes; one that's landed
    /// with the power off or a little speed on it walks away.
    /// </summary>
    void Grounded()
    {
        _landed = true;
        _vel = Vector3.Zero;
        float sideways = MathF.Abs(Mathf.DegToRad(_spin)) * Def.Hull.Z * 0.25f;
        _spin = 0f;
        if (Immobile) Collective = 0f; // (the pilot rolls the power off on a landing like that, rather than leave it to bounce off the skids and turn again)
        if (sideways < 1.5f || _doomed) return; // (a doomed one is already coming down as a wreck: see FlyTick)
        bool rolls = _rng.Randf() < Mathf.Clamp((sideways - 3f) / 4f, 0f, 1f);
        if (DuelMode.Verbose) GD.Print($"[{Clock.Now:0}s] HARD LANDING {Def.Name} turning, skids at {sideways:0.0} m/s sideways{(rolls ? ", rolled over" : "")}");
        Prof.Count("heli:landed turning");
        Damage(rolls ? 99999f : sideways * sideways * 5f + 40f, Clock.Now - LastHit < 30.0 ? LastHitBy : null);
    }

    /// <summary>Contact with the ground or something solid: a landing, a bump, or a crash.</summary>
    void Touch(KinematicCollision3D col)
    {
        var n = col.GetNormal();
        var other = col.GetCollider() as Vehicle;
        float vs = -_vel.Y, hs = AirSpeed;
        bool soft = n.Y > 0.7f && vs < 4f && hs < 6f && MathF.Abs(_pitchA) < 20f && MathF.Abs(_rollA) < 20f;
        if (_doomed)
        {
            _doomed = false;
            // Down on the ground (or something that will hold it): it burns there. Into a wall or another aircraft:
            // it breaks up there, and the wreck falls the rest of the way (FallWreck). (Two that collided in the air
            // came to rest on each other, and hung there, 13 m up, for the rest of the match.)
            if (Holds(n, other)) _landed = true;
            else
            {
                if (other != null) Part(other);
                _vel = _vel.Slide(n) * 0.5f;
            }
            Destroy(_doomBy);
            return;
        }
        if (soft)
        {
            Grounded();
            return;
        }
        // Rotor or airframe into something at speed.
        float impact = (other != null ? _vel - other.Velocity3 : _vel).Length();
        if (DuelMode.Verbose) GD.Print($"[{Clock.Now:0}s] CRASH {Def.Name} into {(col.GetCollider() as Node)?.Name} at {impact:0.0} m/s, agl {Agl:0}, normal {n}, pitch {_pitchA:0} roll {_rollA:0}");
        _vel = _vel.Slide(n) * 0.3f;
        // A real crash (not a bump) wrecks it: no helicopter sits on the ground half-broken with its rotors turning.
        // Shot up and brought down that way, it's whoever shot it up that brought it down.
        float dmg = impact > 9f ? 99999f : impact * impact * 2.2f + 40f;
        Damage(dmg, Clock.Now - LastHit < 30.0 ? LastHitBy : null);
        // Another aircraft: it's hit as hard, and the two tangle just the once; then each falls (or flies) on its own.
        if (other is { Def.Air: true, Destroyed: false })
        {
            Part(other);
            other.Damage(dmg, null);
        }
    }

    /// <summary>Whether a wreck coming down on this can rest on it: not a wall, and not something that may move off from under it (a vehicle still going, an aircraft still in the air).</summary>
    static bool Holds(Vector3 normal, Vehicle? other) =>
        normal.Y > 0.6f && (other == null || (other.Destroyed && (!other.Def.Air || other._landed)));

    /// <summary>Two bodies that have met once and mustn't catch on each other again.</summary>
    void Part(Vehicle other)
    {
        AddCollisionExceptionWith(other);
        other.AddCollisionExceptionWith(this);
    }

    void SpinRotors()
    {
        if (_rig.Rotor != null) _rig.Rotor.Rotation = new Vector3(0f, _rotorSpin, 0f);
        if (_rig.TailRotor != null) _rig.TailRotor.Rotation = new Vector3(_rotorSpin * 4f, 0f, 0f);
        float load = Mathf.Clamp(Collective, 0f, 1f);
        // On the ground at low collective it's idling (or shut down): a quiet whine, not a full-power beat
        // heard across the map.
        bool idle = _landed && (Driver == null || Collective < 0.15f);
        _engine.PitchScale = idle ? (Driver == null ? 0.45f : 0.6f) : 0.85f + load * 0.3f;
        _engine.VolumeDb = idle ? (Driver == null ? -36f : -21f) : -5f + load * 4f;
        CabinSound();
    }

    /// <summary>The fire and smoke of a burning aircraft, following it down.</summary>
    Effects.Burning? _trail;

    /// <summary>
    /// A dead airframe keeps the way it was going: it arcs down under gravity, bleeding off
    /// speed to drag, tumbling and nosing over, trailing fire and smoke, until it hits
    /// something and burns where it lands.
    /// </summary>
    void FallWreck(float dt)
    {
        _vel += Vector3.Down * 9.81f * dt;
        var h = new Vector3(_vel.X, 0f, _vel.Z);
        _vel -= h * (h.Length() * 0.004f + 0.08f) * dt;
        _vel.Y -= _vel.Y * MathF.Abs(_vel.Y) * 0.002f * dt;
        _yaw += Mathf.DegToRad(MathF.Max(_spin, 70f)) * dt;
        _pitchA = Mathf.MoveToward(_pitchA, -40f, 25f * dt);
        _rollA = Mathf.MoveToward(_rollA, 25f, 15f * dt);
        GlobalBasis = Basis.FromEuler(new Vector3(Mathf.DegToRad(-_pitchA), _yaw, Mathf.DegToRad(-_rollA)));
        _trail?.MoveTo(Center);
        var col = MoveAndCollide(_vel * dt);
        if (col == null) return;
        // Off a wall, or past another aircraft (or anything that may yet move from under it): on down, the way it
        // was going along the wall. Caught where it can do neither for a second (wedged), it stays there.
        var n = col.GetNormal();
        var what = col.GetCollider() as Vehicle;
        if (!Holds(n, what) && _wedged < 1f)
        {
            if (what != null) Part(what);
            var s = _vel.Slide(n);
            _vel = new Vector3(s.X * 0.5f, s.Y, s.Z * 0.5f);
            _wedged = col.GetTravel().LengthSquared() < 1e-4f ? _wedged + dt : 0f;
            return;
        }
        _landed = true;
        _vel = Vector3.Zero;
        _trail?.MoveTo(Center);
        _trail = null; // it burns here now
        Grenade.Detonate(Center, Vector3.Up, _doomBy, 10f, 1.2f, 4f, Def.Name + " crash", power: 3f);
        Effects.I.Burn(Center + Vector3.Up * 0.5f, 60f);
    }

    /// <summary>
    /// Someone aboard has gone down, hit, while it's in the air. Nobody leaves a helicopter in flight: they stay
    /// in their seat until it's on the ground (then they're lifted out). If it was the pilot, the other pilot
    /// takes the controls (a gunship's front seat has a set); with nobody to, it's flying itself into the ground.
    /// (Before, a casualty tumbled out, a hundred metres up, and walked away from the fall.)
    /// </summary>
    public void CasualtyAboard(ICombatant c)
    {
        int seat = Array.IndexOf(Occupants, c);
        if (seat >= 0 && Aircrew(seat)) CrewLost = true;
        if (seat == DriverSeat) TakeControls();
    }

    /// <summary>
    /// One of the aircrew hit: down in their seat, or the pilot bleeding and failing. The mission's over: they go home
    /// (see MotorPool). (A gunship whose front-seater was down pressed on with its attack for half a minute, until
    /// the pilot went down too, and nobody was flying it.)
    /// </summary>
    public bool AircrewHit => CrewLost || (Driver is { } p && p.Body.Bleeding > 0f && p.Body.Condition < 60f);

    /// <summary>One of the aircrew (in the cockpit: not a door gunner or a passenger) was hit in flight, down or dead: cleared once it's home.</summary>
    public bool CrewLost;

    bool Aircrew(int seat) => Def.Seats[seat].Role != SeatRole.Passenger && !Def.Seats[seat].Exposed;

    /// <summary>The pilot's out of it, in the air: whoever else aboard can fly it takes over, swapping seats.</summary>
    void TakeControls()
    {
        int d = DriverSeat;
        if (d < 0 || Occupants[d] is { Alive: true }) return;
        for (int i = 0; i < Occupants.Length; i++)
        {
            // A bot in an enclosed gunner's seat: the co-pilot/gunner. (Not a door gunner or a passenger, and not
            // the player, whose seat isn't moved for them.)
            if (i == d || Occupants[i] is not Bot { Alive: true } b || Def.Seats[i].Role != SeatRole.Gunner || Def.Seats[i].Exposed) continue;
            var casualty = Occupants[d];
            Occupants[d] = b;
            Occupants[i] = casualty;
            b.Mount(this, d);
            if (casualty is Bot cb) cb.MovedTo(i);
            return;
        }
    }

    /// <summary>How far the pods' missiles reach (0: none).</summary>
    public float MissileRange => MissileIdx is int mi and >= 0 ? Def.Turrets[Def.Turrets.FindIndex(t => t.Fixed)].Ammo[mi].Range : 0f;

    /// <summary>The pods' missiles: which ammunition they are, and how many are left.</summary>
    public int MissileIdx => Def.Turrets.FindIndex(t => t.Fixed) is int pods and >= 0 ? Array.FindIndex(Def.Turrets[pods].Ammo, a => a.Guided) : -1;
    public int MissilesLeft
    {
        get
        {
            int pods = Def.Turrets.FindIndex(t => t.Fixed), mi = MissileIdx;
            return pods < 0 || mi < 0 ? 0 : Turrets[pods].Loaded[mi] + Turrets[pods].Stock[mi];
        }
    }

    /// <summary>
    /// What the aircraft's sight would put a missile on: an enemy vehicle within the missile's reach that it can see
    /// (nothing in between), nearest the line of <paramref name="look"/> and within <paramref name="cone"/> degrees
    /// of it. An air defence gun counts as much nearer the line than it is.
    /// </summary>
    public Vehicle? MissileLock(Vector3 look, float cone)
    {
        int pods = Def.Turrets.FindIndex(t => t.Fixed), mi = MissileIdx;
        if (pods < 0 || mi < 0) return null;
        float range = Def.Turrets[pods].Ammo[mi].Range;
        var space = GetWorld3D().DirectSpaceState;
        var eye = Center;
        Vehicle? best = null;
        float bestAng = cone;
        foreach (var v in All)
        {
            if (v == this || v.Destroyed || v.Def.Air || !v.Crewed || v.CrewTeam == CrewTeam || !GodotObject.IsInstanceValid(v)) continue;
            var to = v.TopPoint - eye;
            float d = to.Length();
            if (d > range || d < 300f) continue;
            float ang = Mathf.RadToDeg(look.AngleTo(to)) * (v.Def.Kind == VKind.SPAA ? 0.6f : 1f);
            if (ang >= bestAng) continue;
            if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, v.TopPoint, Layers.World)).Count > 0) continue;
            bestAng = ang;
            best = v;
        }
        return best;
    }

    /// <summary>Launch a missile from the pods at a vehicle, up at an angle so it climbs before it comes down on it.</summary>
    public bool LaunchMissile(Vehicle target)
    {
        int pods = Def.Turrets.FindIndex(t => t.Fixed), mi = MissileIdx;
        if (pods < 0 || mi < 0) return false;
        var t = Turrets[pods];
        int was = t.AmmoIdx;
        t.AmmoIdx = mi;
        var from = t.Muzzle.GlobalPosition;
        var to = (target.Center - from).Normalized();
        var right = to.Cross(Vector3.Up);
        var dir = right.LengthSquared() > 1e-4f ? to.Rotated(right.Normalized(), Mathf.DegToRad(8f)) : to;
        bool ok = Fire(pods, false, 0f, dir, target);
        t.AmmoIdx = was;
        return ok;
    }

    /// <summary>Hp gone in the air: it doesn't blow up there, it falls.</summary>
    bool AirDoom(ICombatant? by)
    {
        if (!Def.Air || _landed || _doomed) return false;
        _doomed = true;
        _doomBy = by;
        EngineHit = true;
        _trail = Effects.I.Burn(Center, 25f);
        return true;
    }

    /// <summary>A radar has had us long enough for the crew to react: the warning tone, and a moment to take it in.</summary>
    public bool RadarLocked => Clock.Now - RadarWarning < 0.4 && Clock.Now - RadarSince > 0.7;

    /// <summary>
    /// An air defence gun's radar is tracking us (see CrewBrain.Gun). The warning receiver gives its bearing, and near
    /// enough where it is: a bot crew calls it in, and the pilot gets out of its sight (see HeliPilot).
    /// </summary>
    public void RadarLock(Vehicle by)
    {
        double now = Clock.Now;
        bool on = now - RadarWarning < 1.5;
        if (!on) RadarSince = now;
        // Two on us at once: the warning is for the nearer. (Switching back and forth between them, each switch
        // restarted the crew's reaction, and they never reacted at all.)
        if (!on || RadarFrom is not { } cur || !GodotObject.IsInstanceValid(cur) || cur.Destroyed || by.Center.DistanceTo(Center) < cur.Center.DistanceTo(Center))
            RadarFrom = by;
        RadarWarning = now;
        if (Driver is not Bot pilot || (_rwrCalled.TryGetValue(by, out var called) && now - called < 15.0)) return;
        _rwrCalled[by] = now;
        Prof.Count("heli:radar lock");
        float d = by.Center.DistanceTo(Center);
        Radio.Report(pilot, RadioKind.Armor, by.Center + new Vector3((float)GD.Randfn(0.0, d * 0.05), 0f, (float)GD.Randfn(0.0, d * 0.05)), by);
    }

    /// <summary>When each radar that's locked on to us was last called in.</summary>
    readonly Dictionary<Vehicle, double> _rwrCalled = new();

    /// <summary>Decoys: a few seconds during which an incoming missile may go for the flares instead.</summary>
    public void PopFlares()
    {
        if (FlaresLeft <= 0 || FlaresActive) return;
        FlaresLeft--;
        _flaresUntil = Clock.Now + 3.0;
        Effects.I.Flares(Center, _vel);
        SoundWorld.I.Emit(Snd.Click, Center, 6f);
    }

    /// <summary>A missile has been launched at us (the warning receiver hears it).</summary>
    public void MissileLaunched()
    {
        MissileWarning = Clock.Now;
        // A bot pilot (or a sensible player) pops flares.
        if (Driver is Bot && _rng.Randf() < 0.85f) PopFlares();
    }
}
