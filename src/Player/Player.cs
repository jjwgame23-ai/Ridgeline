using Godot;

namespace Ridgeline;

public partial class Player : CharacterBody3D, ICombatant
{
    public enum StanceKind { Stand, Crouch, Prone }

    public static Player? I { get; private set; }
    const float BaseFov = 80f, Sens = 0.1f;

    public Node3D Head = null!;
    public Camera3D Cam = null!;
    public WeaponRig Weapon = null!;
    public StanceKind Stance { get; private set; } = StanceKind.Stand;
    public float Stamina = 1f, Suppression, Breath = 1f;
    public bool Sprinting { get; private set; }
    public bool Moving { get; private set; }
    public bool HoldingBreath { get; private set; }
    public float SwayMult { get; private set; } = 1f;
    public Vector3 WeaponBob { get; private set; }

    public bool StanceLocked => _stanceLock > 0f;
    public float Speed => new Vector2(Velocity.X, Velocity.Z).Length();
    public float BreathRate => 1.5f + (1f - Stamina) * 3.2f;
    public float Heading => ((-_yaw) % 360f + 360f) % 360f;
    public float RecoilMult => Stance switch { StanceKind.Crouch => 0.8f, StanceKind.Prone => 0.55f, _ => 1f };

    /// <summary>Set by squad modes before spawning; null elsewhere (range, arena) for the classic two-rifle kit.</summary>
    public Role? Kit;
    public int Frags = 99, Medkits, Sandbags;
    float _gadgetCool, _supplyT;

    // --- ICombatant
    public int TeamId;
    public readonly Body Body = new();
    public float Health => Body.Condition;
    HitInfo? _downHit;
    ICombatant? _lastShooter;
    /// <summary>The last enemy to hit him: a bleed-out is his (not a teammate's stray fragment, or his own grenade, that grazed him since).</summary>
    ICombatant? _lastEnemy;
    float _giveUpT, _selfAidT;
    public float HurtFlash { get; private set; }
    public ICombatant? KilledBy { get; private set; }
    public int Team => TeamId;
    public string Callsign => "You";
    public bool Alive => !Body.Down && !Body.Dead;
    public bool Downed => Body.Down && !Body.Dead;
    public bool Dead => Body.Dead;
    Body ICombatant.Body => Body;
    public Vector3 FeetPos => GlobalPosition;
    public Vector3 EyePos => Cam.GlobalPosition;
    public Vector3 ChestPos => GlobalPosition + Vector3.Up * (_capsule.Height * 0.7f);
    public Vector3 Vel => Velocity;
    public float BodyHeight => _capsule.Height;
    public double LastShotTime { get; private set; } = -99;
    public Rid BodyRid => GetRid();
    float _deathT;

    CollisionShape3D _col = null!;
    CapsuleShape3D _capsule = null!;
    float _yaw, _pitch, _eye = 1.62f, _lean, _bobPhase, _airTime, _stepDist, _stanceLock, _sinceShot = 10f;
    float _shake, _nadeCool, _sinceSprint = 10f, _breathLockT, _landDip, _preMoveVy;
    bool _wasOnFloor = true, _stoodThisFrame;
    Vector2 _recoilDebt, _kickQueue;
    readonly RandomNumberGenerator _rng = new();

    public override void _EnterTree()
    {
        I = this;
        Combatants.Register(this);
    }

    public override void _ExitTree()
    {
        Combatants.Unregister(this);
        if (I == this) I = null;
    }

    /// <summary>Face a compass direction: 0 = north (-Z), -90 = east (+X).</summary>
    public void SetYaw(float deg) => _yaw = deg;
    public void SetPitch(float deg) => _pitch = deg;

    public override void _Ready()
    {
        CollisionLayer = 2;
        CollisionMask = Layers.Solid;
        FloorMaxAngle = Mathf.DegToRad(46f);
        FloorSnapLength = 0.35f;

        _capsule = new CapsuleShape3D { Radius = 0.3f, Height = 1.8f };
        _col = new CollisionShape3D { Shape = _capsule, Position = new Vector3(0, 0.9f, 0) };
        AddChild(_col);

        Head = new Node3D { Position = new Vector3(0, _eye, 0) };
        AddChild(Head);
        Cam = new Camera3D { Fov = BaseFov, Near = 0.02f, Far = 5000f };
        Head.AddChild(Cam);
        Cam.MakeCurrent();
        Weapon = new WeaponRig { P = this };
        if (Kit is Role r)
        {
            Weapon.Loadout = Roles.Secondary(r) is WeaponDef sec ? new[] { Roles.Primary(r), sec } : new[] { Roles.Primary(r) };
            Stock(r);
        }
        Cam.AddChild(Weapon);

        Input.MouseMode = Input.MouseModeEnum.Captured;
        Hud.CapturedAtMs = Time.GetTicksMsec();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is not InputEventMouseMotion mm || Input.MouseMode != Input.MouseModeEnum.Captured || !Alive) return;
        // Flying: the mouse is the cyclic (hold Alt to look around instead).
        if (Ride is { Def.Air: true } && SeatIdx == Ride.DriverSeat && !Input.IsActionPressed("free_look"))
        {
            _stick += mm.Relative * 0.0035f;
            _stick = new Vector2(Mathf.Clamp(_stick.X, -1f, 1f), Mathf.Clamp(_stick.Y, -1f, 1f));
            return;
        }
        // Blood loss and a rattled head make you slow to turn.
        float s = Sens * (Cam.Fov / BaseFov) * (1f - Body.Shock * 0.35f - Body.Concussion * 0.15f);
        float dYaw = -mm.Relative.X * s, dPitch = -mm.Relative.Y * s;
        _yaw += dYaw;
        _pitch = Mathf.Clamp(_pitch + dPitch, -88f, 88f);
        // Pulling down against recoil counts toward recovering it, so auto-recovery doesn't overshoot.
        if (dPitch < 0f && _recoilDebt.X > 0f) _recoilDebt.X = Mathf.Max(0f, _recoilDebt.X + dPitch);
        Weapon.AddLookDelta(new Vector2(dPitch, dYaw));
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (!Body.Dead)
            switch (Body.Tick(dt, Clock.Now))
            {
                case HitResult.Downed: GoDown(_downHit ?? BleedHit()); break;
                case HitResult.Dead: Die(_downHit ?? BleedHit()); break;
            }
        if (Ride != null) return; // the vehicle carries you
        if (!Alive)
        {
            Velocity = new Vector3(0f, IsOnFloor() ? 0f : Velocity.Y - 9.81f * dt, 0f);
            MoveAndSlide();
            return;
        }
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured && Piloting == null;
        _stoodThisFrame = false;
        HandleStance(captured);

        var input = captured ? Input.GetVector("move_left", "move_right", "move_forward", "move_back") : Vector2.Zero;
        bool aimHeld = captured && Input.IsActionPressed("aim");
        bool fireHeld = captured && Input.IsActionPressed("fire");
        bool floor = IsOnFloor();
        // IsOnFloor flickers on bumpy ground at speed; treat brief hops as still grounded.
        bool grounded = _airTime < 0.2f;

        Sprinting = captured && Input.IsActionPressed("sprint") && Stance == StanceKind.Stand && input.Y < -0.5f
                    && Stamina > 0.02f && !aimHeld && !fireHeld && grounded && !StanceLocked && Body.CanSprint;

        float speed = Stance switch
        {
            StanceKind.Prone => 0.8f,
            StanceKind.Crouch => 1.9f,
            _ => Sprinting ? 5.8f : 3.4f,
        };
        if (Weapon.AimT > 0.5f) speed *= 0.6f;
        if (StanceLocked) speed *= 0.3f;
        speed *= Body.SpeedMult;
        if (_selfAidT > 0f) speed *= 0.3f;

        var wish = GlobalBasis * new Vector3(input.X, 0f, input.Y);
        wish.Y = 0f;
        if (wish.LengthSquared() > 1f) wish = wish.Normalized();
        var hv = new Vector2(Velocity.X, Velocity.Z);
        var target = new Vector2(wish.X, wish.Z) * speed;
        float accel = floor ? (target.Length() > hv.Length() ? 10f : 14f) : 1.2f;
        hv = hv.MoveToward(target, accel * dt);

        float vy = Velocity.Y;
        if (!floor) vy -= 9.81f * dt;
        if (captured && !_stoodThisFrame && Input.IsActionJustPressed("jump") && floor
            && Stance == StanceKind.Stand && Stamina > 0.15f && !StanceLocked)
        {
            vy = 4.2f;
            Stamina -= 0.1f;
        }
        Velocity = new Vector3(hv.X, vy, hv.Y);
        _preMoveVy = vy;
        MoveAndSlide();

        bool nowFloor = IsOnFloor();
        if (nowFloor && !_wasOnFloor && _preMoveVy < -3.5f)
        {
            SoundWorld.I.Emit(Snd.Footstep, GlobalPosition, 5f, this);
            _landDip = Mathf.Clamp(-_preMoveVy * 0.012f, 0f, 0.12f);
        }
        _wasOnFloor = nowFloor;
        _airTime = nowFloor ? 0f : _airTime + dt;
        Moving = hv.Length() > 0.4f;

        if (_airTime < 0.2f)
        {
            float step = hv.Length() * dt;
            float stride = Stance switch { StanceKind.Prone => 0.55f, StanceKind.Crouch => 0.62f, _ => Sprinting ? 1.05f : 0.78f };
            _stepDist += step;
            // Accumulate phase per step, so a change of stride changes the rhythm, not the position in it.
            _bobPhase = (_bobPhase + step / stride * Mathf.Pi) % (Mathf.Pi * 4f);
            if (_stepDist >= stride)
            {
                _stepDist = 0f;
                float gain = Stance switch { StanceKind.Prone => -12f, StanceKind.Crouch => -7f, _ => Sprinting ? 3f : 0f };
                SoundWorld.I.Emit(Snd.Footstep, GlobalPosition + Vector3.Up * 0.05f, gain, this);
            }
        }

        if (Sprinting)
        {
            Stamina = Mathf.Max(0f, Stamina - dt / (Body.Lung ? 7f : 14f));
            _sinceSprint = 0f;
        }
        else
        {
            _sinceSprint += dt;
            if (_sinceSprint > 0.8f) Stamina = Mathf.Min(1f, Stamina + dt * (Moving ? 0.07f : 0.14f));
        }
    }

    void HandleStance(bool captured)
    {
        if (!captured || StanceLocked) return;
        var s = Stance;
        if (Input.IsActionJustPressed("crouch")) s = Stance == StanceKind.Crouch ? StanceKind.Stand : StanceKind.Crouch;
        if (Input.IsActionJustPressed("prone")) s = Stance == StanceKind.Prone ? StanceKind.Crouch : StanceKind.Prone;
        if (Input.IsActionJustPressed("jump") && Stance != StanceKind.Stand) { s = StanceKind.Stand; _stoodThisFrame = true; }
        if (s == Stance) return;

        if (s == StanceKind.Prone || Stance == StanceKind.Prone) _stanceLock = 0.85f;
        Stance = s;
        float h = s switch { StanceKind.Stand => 1.8f, StanceKind.Crouch => 1.2f, _ => 0.62f };
        _capsule.Height = h;
        _col.Position = new Vector3(0, h / 2f, 0);
        SoundWorld.I.Emit(Snd.Footstep, GlobalPosition, -6f, this); // gear rustle
    }

    float _t;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        HurtFlash = Mathf.MoveToward(HurtFlash, 0f, dt * 1.5f);
        // Wearing off wherever you are. (Only on foot, it stayed at full through a ride, a drone flight or lying
        // wounded: the screen stayed dark from a fight long over.)
        Suppression = Mathf.MoveToward(Suppression, 0f, dt * 0.3f);
        _shake = Mathf.MoveToward(_shake, 0f, dt * 2.2f);
        if (Piloting != null) { PilotProcess(dt); if (Piloting != null) return; }
        if (Downed) { DownedView(dt); return; }
        if (Ride != null) { VehicleProcess(dt); return; }
        if (Alive && Input.MouseMode == Input.MouseModeEnum.Captured && Input.IsActionJustPressed("use") && (TryBoard() || Door.Use(this, Cam.GlobalPosition, -Cam.GlobalBasis.Z))) return;
        if (!Alive) { DeathCam(dt); return; }
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        _stanceLock = Mathf.Max(0f, _stanceLock - dt);
        _sinceShot += dt;
        _nadeCool -= dt;

        // --- recoil: kick is spread over a few frames; part of it returns once you stop firing
        var kick = Weapon.CameraKick;
        Weapon.CameraKick = Vector2.Zero;
        _kickQueue += kick;
        _recoilDebt += kick * Weapon.Def.RecoverFrac;
        var apply = _kickQueue * Mathf.Min(1f, dt * 35f);
        _kickQueue -= apply;
        _pitch = Mathf.Clamp(_pitch + apply.X, -88f, 88f);
        _yaw += apply.Y;
        if (_sinceShot > 0.1f && _recoilDebt.LengthSquared() > 1e-6f)
        {
            var r = _recoilDebt * Mathf.Min(1f, dt * 6f);
            _recoilDebt -= r;
            _pitch -= r.X;
            _yaw -= r.Y;
        }

        // --- breath control and sway
        // A punctured lung: you can't hold your breath.
        bool wantHold = captured && Weapon.AimT > 0.7f && Input.IsActionPressed("sprint") && !Moving && !Body.Lung;
        float breathMult = 1f;
        HoldingBreath = false;
        if (_breathLockT > 0f)
        {
            _breathLockT -= dt;
            breathMult = 1.8f; // gasping after holding too long
            Breath = Mathf.Min(1f, Breath + dt / 6f);
        }
        else if (wantHold && Breath > 0f)
        {
            HoldingBreath = true;
            Breath -= dt / 5f;
            breathMult = 0.18f;
            if (Breath <= 0f) _breathLockT = 2.5f;
        }
        else Breath = Mathf.Min(1f, Breath + dt / 6f);

        float stanceSway = Stance switch { StanceKind.Crouch => 0.6f, StanceKind.Prone => 0.3f, _ => 1f };
        SwayMult = stanceSway * (Moving ? 2.6f : 1f) * (1f + (1f - Stamina) * 1.6f) * (1f + Suppression * 2.2f) * breathMult * (1f + Body.AimPenalty);

        // --- head: stance height, lean, bob, shake
        float eyeTarget = Stance switch { StanceKind.Crouch => 1.12f, StanceKind.Prone => 0.38f, _ => 1.62f };
        _eye = Mathf.MoveToward(_eye, eyeTarget, dt * (Stance == StanceKind.Prone || _eye < 0.9f ? 1.6f : 4f));
        float leanIn = 0f;
        if (captured && Stance != StanceKind.Prone && !Sprinting)
        {
            if (Input.IsActionPressed("lean_left")) leanIn -= 1f;
            if (Input.IsActionPressed("lean_right")) leanIn += 1f;
        }
        _lean = Mathf.MoveToward(_lean, leanIn, dt * 5f);
        _landDip = Mathf.MoveToward(_landDip, 0f, dt * 0.5f);

        RotationDegrees = new Vector3(0f, _yaw, 0f);
        Head.Position = new Vector3(_lean * 0.36f, _eye - MathF.Abs(_lean) * 0.07f - _landDip, 0f);
        Head.RotationDegrees = new Vector3(_pitch, 0f, 0f);

        float bobAmp = _airTime < 0.2f ? Mathf.Clamp(Speed / 5.8f, 0f, 1f) * (Sprinting ? 1f : 0.55f) * (1f - Weapon.AimT * 0.85f) : 0f;
        float ph = _bobPhase;
        Cam.Position = new Vector3(MathF.Cos(ph) * 0.035f, -MathF.Abs(MathF.Sin(ph)) * 0.045f, 0f) * bobAmp;
        WeaponBob = new Vector3(MathF.Cos(ph) * 0.012f, MathF.Abs(MathF.Sin(ph)) * 0.012f, 0f) * bobAmp * 1.5f;
        var shake = _shake > 0f ? new Vector2(_rng.RandfRange(-1f, 1f), _rng.RandfRange(-1f, 1f)) * _shake * 1.2f : Vector2.Zero;
        // Concussed: the world swims.
        float swim = Body.Concussion * 2.5f;
        Cam.RotationDegrees = new Vector3(shake.X + MathF.Sin(_t * 0.9f) * swim * 0.4f, shake.Y, -_lean * 11f + MathF.Sin(_t * 0.7f) * swim);

        float a = Weapon.AimT * Weapon.AimT * (3f - 2f * Weapon.AimT);
        float fov = Mathf.Lerp(BaseFov + (Sprinting ? 5f : 0f), Weapon.Def.AdsFov, a);
        Cam.Fov = Mathf.Lerp(Cam.Fov, fov, 1f - MathF.Exp(-dt * 18f));

        // --- actions
        if (captured && Input.IsActionJustPressed("grenade") && _nadeCool <= 0f && !Weapon.Busy && !Sprinting)
        {
            if (Frags > 0) { Frags--; ThrowGrenade(); }
            else Hud.Toast("No frags left — find an ammo bearer", 2f);
        }
        _gadgetCool -= dt;
        if (captured && Input.IsActionJustPressed("gadget") && _gadgetCool <= 0f && !Sprinting) UseGadget();
        DroneTick(dt, captured);
        SelfAidInput(captured, dt);
        if (Kit == Role.Ammo) SupplyAround(dt);
        if (captured && Input.IsActionJustPressed("check_ammo")) Hud.Toast(Weapon.Describe(), 3f);
    }

    // ================================================================ role kit

    Role ICombatant.Role => Kit ?? Role.Rifleman;
    public float Hp => Health;
    public float AmmoLevel => Weapon.AmmoLevel;

    void Stock(Role r)
    {
        Frags = Roles.Frags(r);
        Medkits = r == Role.Medic ? 10 : 0;
        Sandbags = r == Role.Engineer ? 4 : 0;
    }

    /// <summary>A medic (or you, as one) treats you: dressed, fluids, and on your feet if you were down.</summary>
    public void Heal(float amount)
    {
        if (Body.Dead) return;
        bool wasDown = Body.Down;
        Body.Treat();
        if (wasDown && !Body.Down) Revive();
    }

    // ================================================================ wounds

    HitInfo BleedHit() => new() { Shooter = _lastEnemy ?? _lastShooter, Point = ChestPos, Dir = Vector3.Down, Zone = HitZone.Torso, Weapon = "blood loss" };

    void GoDown(HitInfo hit)
    {
        Ride?.Leave(this);
        _downHit = hit;
        _giveUpT = 0f;
        _selfAidT = 0f;
        Weapon.Visible = false;
        Stance = StanceKind.Prone;
        _capsule.Height = 0.62f;
        _col.Position = new Vector3(0, 0.31f, 0);
        Combatants.ReportDowned(this, hit);
    }

    void Revive()
    {
        _downHit = null;
        Weapon.Visible = true;
        Stance = StanceKind.Crouch;
        _capsule.Height = 1.2f;
        _col.Position = new Vector3(0, 0.6f, 0);
        _stanceLock = 1.5f;
        Hud.Toast("Back on your feet — you're still hurt", 3f);
    }

    /// <summary>Down: on the ground looking up, heartbeat in your ears. Bandage, wait, or let go.</summary>
    void DownedView(float dt)
    {
        // Out of the sights: no scope zoom on the sky.
        Cam.Fov = Mathf.Lerp(Cam.Fov, BaseFov, 1f - MathF.Exp(-dt * 6f));
        _eye = Mathf.MoveToward(_eye, 0.3f, dt * 2f);
        Head.Position = new Vector3(0f, _eye, 0f);
        _pitch = Mathf.MoveToward(_pitch, 10f, dt * 20f);
        Head.RotationDegrees = new Vector3(_pitch, 0f, 0f);
        Cam.RotationDegrees = new Vector3(MathF.Sin(_t * 0.5f) * 1.5f, 0f, 35f + MathF.Sin(_t * 0.3f) * 3f);
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        SelfAidInput(captured, dt);
        if (captured && Input.IsActionPressed("jump"))
        {
            _giveUpT += dt;
            if (_giveUpT > 1.5f) { Body.GiveUp(); Die(_downHit ?? BleedHit()); }
        }
        else _giveUpT = 0f;
    }

    /// <summary>X: field dressing and tourniquet on your own bleeds. Takes a few seconds; you can barely move meanwhile.</summary>
    void SelfAidInput(bool captured, float dt)
    {
        if (_selfAidT > 0f)
        {
            _selfAidT -= dt;
            if (_selfAidT <= 0f)
            {
                Body.SelfAid();
                Hud.Toast(Body.Bleeding > 0.0005f ? "Bandaged — but it's still bleeding inside. Find a medic." : "Bleeding stopped", 2.5f);
            }
            return;
        }
        if (!captured || !Input.IsActionJustPressed("selfaid")) return;
        if (!Body.NeedsSelfAid) { Hud.Toast(Body.Wounds.Count == 0 ? "You're not hurt" : "Nothing you can do yourself — find a medic", 1.5f); return; }
        _selfAidT = Body.Bleeding > 0.02f ? 3f : 4.5f;
        _stanceLock = Mathf.Max(_stanceLock, _selfAidT);
        SoundWorld.I.Emit(Snd.Bag, EyePos, 0f, this);
        Hud.Toast("Bandaging...", _selfAidT);
    }

    public bool Resupply()
    {
        if (!Alive) return false;
        bool any = Weapon.Refill();
        if (Kit is Role r)
        {
            int f = Frags, m = Medkits, s = Sandbags;
            Stock(r);
            any |= Frags > f || Medkits > m || Sandbags > s;
        }
        return any;
    }

    /// <summary>H: the role's tool. Medic: patch up whoever is closest and hurt. Engineer: sandbags.</summary>
    void UseGadget()
    {
        _gadgetCool = 0.5f;
        switch (Kit)
        {
            case Role.Medic:
                if (Medkits <= 0) { Hud.Toast("Out of medical supplies", 2f); return; }
                ICombatant? patient = null;
                float best = 2.6f;
                foreach (var c in Combatants.All)
                    if (c != this && !c.Dead && c.Team == Team && (c.Downed || c.Hp < 99f || c.Body.Bleeding > 0f) && c.FeetPos.DistanceTo(FeetPos) < best) { best = c.FeetPos.DistanceTo(FeetPos); patient = c; }
                if (patient == null && Body.Wounds.Count == 0) { Hud.Toast("Nobody wounded within reach", 1.5f); return; }
                patient ??= this;
                bool revive = patient.Downed;
                patient.Heal(50f);
                Medkits--;
                _gadgetCool = revive ? 5f : 2.5f;
                _stanceLock = Mathf.Max(_stanceLock, revive ? 4f : 1.5f);
                SoundWorld.I.Emit(Snd.Bag, EyePos, 0f, this);
                Hud.Toast(patient == this ? $"Patched yourself up ({Medkits} kits left)" : $"Patched up {patient.Callsign} ({Medkits} kits left)", 2f);
                break;
            case Role.Engineer:
                if (Sandbags <= 0) { Hud.Toast("Out of sandbags", 2f); return; }
                var fwd = -Cam.GlobalBasis.Z;
                fwd.Y = 0f;
                fwd = fwd.Normalized();
                var at = GlobalPosition + fwd * 1.3f;
                if (Fortifications.Sandbags(GetParent(), Effects.Ground, at, fwd) == null) return;
                Sandbags--;
                _gadgetCool = 3f;
                _stanceLock = Mathf.Max(_stanceLock, 2.5f);
                SoundWorld.I.Emit(Snd.Build, at, 0f, this);
                Hud.Toast($"Sandbags down ({Sandbags} left)", 2f);
                break;
            case Role.Ammo:
                Hud.Toast("You hand out ammo to anyone close by, automatically", 2f);
                break;
            case Role.DroneOperator:
                FlyQuad();
                break;
            default:
                if (Kit != null) Hud.Toast($"{Roles.Name(Kit.Value)}: no special tool", 1.5f);
                break;
        }
    }

    /// <summary>An ammo bearer tops up anyone who comes close.</summary>
    void SupplyAround(float dt)
    {
        _supplyT -= dt;
        if (_supplyT > 0f) return;
        _supplyT = 3f;
        foreach (var c in Combatants.All)
            if (c != this && c.Alive && c.Team == Team && c.FeetPos.DistanceTo(FeetPos) < 6f && c.AmmoLevel < 0.99f && c.Resupply())
                Hud.Toast($"Resupplied {c.Callsign}", 1.5f);
    }

    public void ThrowGrenade()
    {
        _nadeCool = 1.2f;
        var fwd = -Cam.GlobalBasis.Z.Normalized();
        var g = new Grenade { Thrower = this };
        GetParent().AddChild(g);
        g.GlobalPosition = Cam.GlobalPosition + fwd * 0.4f + Cam.GlobalBasis.X.Normalized() * 0.15f;
        g.LinearVelocity = fwd * 15f + Vector3.Up * 2.5f + Velocity;
        g.AngularVelocity = new Vector3(_rng.RandfRange(-8f, 8f), _rng.RandfRange(-8f, 8f), _rng.RandfRange(-8f, 8f));
        g.AddCollisionExceptionWith(this);
        SoundWorld.I.Emit(Snd.Click, Cam.GlobalPosition); // pin
    }

    public void OnShotFired()
    {
        _sinceShot = 0f;
        LastShotTime = Clock.Now;
    }

    public void OnBlast(Vector3 pos, float power = 1f) => ApplyBlast(pos, power);

    public void TakeHit(HitInfo hit)
    {
        if (Body.Dead) return;
        bool wasDown = Body.Down;
        var region = Body.RegionFor(this, hit.Point, hit.Dir, hit.Zone);
        if (hit.Shooter != null) _lastShooter = hit.Shooter;
        if (hit.Shooter != null && hit.Shooter != this && hit.Shooter.Team != Team) _lastEnemy = hit.Shooter;
        var res = Body.Hit(region, hit.Damage / Combatants.ZoneMultiplier(hit.Zone) / 50f, _rng);
        HurtFlash = Mathf.Min(1f, HurtFlash + 0.35f + hit.Damage / 100f);
        Suppression = Mathf.Min(1f, Suppression + 0.4f);
        _shake = Mathf.Max(_shake, 0.6f);
        _kickQueue += new Vector2(_rng.RandfRange(0.5f, 2f), _rng.RandfRange(-2f, 2f)); // flinch
        if (region == Region.Head && res != HitResult.Dead) SoundWorld.I.Deafen(0.75f);
        if (res == HitResult.Dead) { Die(hit); return; }
        if (res == HitResult.Downed && !wasDown) { GoDown(hit); return; }
        if (!wasDown)
            Hud.Toast(region switch
            {
                Region.Head => "Hit in the head — you're seeing stars",
                Region.Chest => Body.Lung ? "Hit in the chest — it's hard to breathe" : "Hit in the chest",
                Region.Abdomen => "Hit in the gut",
                Region.Arm => "Hit in the arm — hard to hold the rifle steady",
                _ => "Hit in the leg — you can't run",
            }, 2.5f);
    }

    void Die(HitInfo hit)
    {
        Ride?.Leave(this);
        if (!Body.Dead) Body.GiveUp();
        SoundWorld.I.ResetHearing();
        KilledBy = hit.Shooter;
        CollisionLayer = 0;
        Weapon.Visible = false;
        Combatants.ReportKill(this, hit);
    }

    /// <summary>The view drops to the ground and tips over.</summary>
    void DeathCam(float dt)
    {
        _deathT = Mathf.Min(1f, _deathT + dt * 1.6f);
        Cam.Fov = Mathf.Lerp(Cam.Fov, BaseFov, 1f - MathF.Exp(-dt * 6f));
        float e = _deathT * _deathT;
        Head.Position = new Vector3(0f, Mathf.Lerp(_eye, 0.25f, e), 0f);
        Cam.RotationDegrees = new Vector3(Mathf.Lerp(0f, 25f, e), 0f, Mathf.Lerp(0f, 75f, e));
    }

    public void OnNearMiss(float d, Vector3 from)
    {
        Suppression = Mathf.Min(1f, Suppression + Mathf.Clamp(1.2f / (d + 0.8f), 0.06f, 0.45f));
        _shake = Mathf.Max(_shake, 0.25f);
    }

    /// <summary>A blast's overpressure reaches you at the speed of sound, same as its noise.</summary>
    public void ApplyBlast(Vector3 pos, float power = 1f)
    {
        float d = pos.DistanceTo(Cam.GlobalPosition);
        if (d > 60f * MathF.Cbrt(power)) return;
        // A wall or hill between you and it takes most of the overpressure: it hits as if much further away.
        var excl = new Godot.Collections.Array<Rid> { GetRid() };
        bool shielded = GetWorld3D().DirectSpaceState.IntersectRay(
            PhysicsRayQueryParameters3D.Create(pos + Vector3.Up * 0.3f, Cam.GlobalPosition, Layers.World, excl)).Count > 0;
        float eff = (shielded ? d * 2.2f + 2f : d) / MathF.Cbrt(power); // scaled distance: a bigger charge hits like a grenade closer in
        GetTree().CreateTimer(d / SoundWorld.SpeedOfSound).Timeout += () =>
        {
            if (!Alive) return;
            float s = Mathf.Clamp(1f - (eff - 3f) / 40f, 0f, 1f);
            if (eff < 12f) Body.Blast(1f - eff / 12f);
            Suppression = Mathf.Min(1f, Suppression + s);
            _shake = Mathf.Max(_shake, s * 1.2f);
            // Ears: nothing past ~20 m in the open; a frag at a couple of metres is the worst it gets.
            SoundWorld.I.Deafen(Mathf.Clamp(1f - (eff - 2f) / 18f, 0f, 1f));
        };
    }
}
