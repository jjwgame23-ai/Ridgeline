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
    float _pitchA, _rollA, _rotorSpin, _spin;
    bool _landed = true, _doomed;
    ICombatant? _doomBy;
    double _flaresUntil = -1;
    public int FlaresLeft;
    public double MissileWarning = -99;

    // ---- bot pilot state
    public HeliMode AirMode;
    public Vector3? AttackPoint;         // what the gunship's runs are aimed at (a vehicle, a cluster)
    public int AttackPhase;
    public Vector3 RunFrom = Vector3.Back;
    public double NextSalvo;
    public double NowT => Clock.Now;
    public int SalvoLeft;
    public Vector3 SalvoAt;
    double _salvoNext;

    public bool Landed => _landed;
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
        bool piloted = Driver != null && !_doomed;
        float power = EngineHit ? 0.62f : 1f;
        if (!piloted)
        {
            // Nobody at the controls: it settles (or, in the air, goes down).
            Collective = _landed ? 0f : MathF.Max(0f, Collective - dt * 0.3f);
            CyclicPitch = CyclicRoll = Pedal = 0f;
        }
        if (_doomed) { Collective = 0.35f; _spin = 150f; }

        float pitchT = _landed ? 0f : CyclicPitch * 32f;
        float rollT = _landed ? 0f : CyclicRoll * 40f;
        _pitchA = Mathf.MoveToward(_pitchA, pitchT, 60f * dt);
        _rollA = Mathf.MoveToward(_rollA, rollT, 80f * dt);

        // Yaw: pedals, plus the turn a bank makes at speed; a shot-out tail rotor spins it.
        float yawRate = _landed ? 0f : Pedal * 65f + (AirSpeed > 12f ? -_rollA * 0.9f : 0f);
        if (Immobile && !_landed) _spin = MathF.Max(_spin, 110f);
        yawRate += _spin;
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
            if (thrust > 10.3f && piloted) { _landed = false; _vel.Y = MathF.Max(_vel.Y, 0.5f); }
            else
            {
                _vel = Vector3.Zero;
                SnapToGround(dt);
                _rotorSpin += dt * (Collective > 0.05f || piloted ? 25f : 4f);
                SpinRotors();
                return;
            }
        }

        GlobalBasis = basis;
        var col = MoveAndCollide(_vel * dt);
        _trail?.MoveTo(Center);
        if (col != null) Touch(col);

        // Height above whatever is below.
        var excl = new Godot.Collections.Array<Rid> { GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(GlobalPosition, GlobalPosition + Vector3.Down * 800f, Layers.World | Layers.Trees, excl));
        Agl = hit.Count > 0 ? GlobalPosition.Y - hit["position"].AsVector3().Y : 800f;
        // Settling gently onto flat ground from a hover: that's a landing.
        if (Agl < Def.GroundClear + 0.35f && _vel.Y < 0.5f && _vel.Y > -3.5f && AirSpeed < 4f && MathF.Abs(_pitchA) < 12f && MathF.Abs(_rollA) < 12f && Collective < 0.55f)
        {
            _landed = true;
            _vel = Vector3.Zero;
            _pitchA = _rollA = 0f;
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

    /// <summary>Contact with the ground or something solid: a landing, a bump, or a crash.</summary>
    void Touch(KinematicCollision3D col)
    {
        var n = col.GetNormal();
        float vs = -_vel.Y, hs = AirSpeed;
        bool soft = n.Y > 0.7f && vs < 4f && hs < 6f && MathF.Abs(_pitchA) < 20f && MathF.Abs(_rollA) < 20f;
        if (_doomed) { _doomed = false; _landed = true; Destroy(_doomBy); return; }
        if (soft)
        {
            _landed = n.Y > 0.7f;
            _vel = Vector3.Zero;
            return;
        }
        // Rotor or airframe into something at speed.
        float impact = _vel.Length();
        if (DuelMode.Verbose) GD.Print($"[{Clock.Now:0}s] CRASH {Def.Name} into {(col.GetCollider() as Node)?.Name} at {impact:0.0} m/s, agl {Agl:0}, normal {n}, pitch {_pitchA:0} roll {_rollA:0}");
        _vel = _vel.Slide(n) * 0.3f;
        // A real crash (not a bump) wrecks it: no helicopter sits on the ground half-broken with its rotors turning.
        Damage(impact > 9f ? 99999f : impact * impact * 2.2f + 40f, null);
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
        _engine.VolumeDb = idle ? (Driver == null ? -34f : -18f) : -2f + load * 4f;
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
        _landed = true;
        _vel = Vector3.Zero;
        _trail?.MoveTo(Center);
        _trail = null; // it burns here now
        Grenade.Detonate(Center, Vector3.Up, _doomBy, 20, 1.2f, 4f, Def.Name + " crash", power: 3f);
        Effects.I.Burn(Center + Vector3.Up * 0.5f, 60f);
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
