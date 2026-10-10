using Godot;

namespace Ridgeline;

/// <summary>
/// The weapon in your hands. Pivots at the eye under the camera; its own
/// rotation (sway, recoil punch, lag behind mouse movement) means the gun does
/// not always point where the screen centre is — the sight shows where it does.
/// </summary>
public partial class WeaponRig : Node3D
{
    sealed class Slot
    {
        public WeaponDef Def = null!;
        public int Ammo;
        public Magazines Mags = null!;
        public bool Auto;
        public float Zero;
        public Node3D Model = null!, Muzzle = null!;
    }

    const float SwitchTime = 0.6f;

    public Player P = null!;
    /// <summary>What's carried, slot 1 first. Set before the rig enters the tree.</summary>
    public WeaponDef[] Loadout = { WeaponDef.Carbine, WeaponDef.Marksman };
    public Vector2 CameraKick; // degrees (x = pitch up, y = yaw), consumed by the player

    Slot[] _slots = null!;
    int _cur, _pending = -1;
    float _cool, _reloadT, _reloadDur, _switchT, _t, _kickBack, _sprintT, _swayAmp = 1f;
    bool _reloadEmpty, _reloadKeep;
    float _reloadPressT = -9f;
    int _reloadStage;
    Vector2 _punch, _punchVel, _lag, _lagVel, _sway;
    readonly RandomNumberGenerator _rng = new();

    public WeaponDef Def => _slots[_cur].Def;
    public int Ammo => _slots[_cur].Ammo;
    public Magazines Mags => _slots[_cur].Mags;
    public bool Auto => _slots[_cur].Auto;
    public float AimT { get; private set; }
    public bool Reloading => _reloadT > 0f;
    public bool Busy => Reloading || _switchT > 0f || P.StanceLocked;
    public Vector3 AimDir => -GlobalBasis.Z.Normalized();
    public Vector3 MuzzlePos => _slots[_cur].Muzzle.GlobalPosition;

    public override void _Ready()
    {
        _slots = Loadout.Select(Make).ToArray();
        for (int i = 0; i < _slots.Length; i++) _slots[i].Model.Visible = i == _cur;
    }

    Slot Make(WeaponDef d)
    {
        var (model, muzzle) = d.Build();
        AddChild(model);
        model.Position = d.HipOffset;
        return new Slot
        {
            Def = d, Ammo = d.MagSize + d.Chambered, Mags = new Magazines(d.MagSize, d.Mags), Auto = d.AutoCapable,
            Zero = Ballistics.ZeroAngle(d.MuzzleVel, d.Drag, d.ZeroM),
            Model = model, Muzzle = muzzle,
        };
    }

    /// <summary>Someone else's weapon as it is, if it's one carried here: the rounds in it and in his pouches.</summary>
    public void Carry(WeaponDef d, int ammo, Magazines mags)
    {
        foreach (var s in _slots)
        {
            if (s.Def != d) continue;
            s.Ammo = ammo;
            s.Mags.CopyFrom(mags);
        }
    }

    /// <param name="deg">Camera rotation this event, (pitch, yaw) degrees.</param>
    public void AddLookDelta(Vector2 deg)
    {
        _lag -= deg * 0.12f;
        _lag = new Vector2(Mathf.Clamp(_lag.X, -2.5f, 2.5f), Mathf.Clamp(_lag.Y, -2.5f, 2.5f));
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        var s = _slots[_cur];
        var d = s.Def;
        bool canAct = P.Alive && P.Ride == null && P.Piloting == null && Input.MouseMode == Input.MouseModeEnum.Captured
                      && Time.GetTicksMsec() - Hud.CapturedAtMs > 150
                      && !P.StanceLocked;

        // --- switching
        if (canAct && _switchT <= 0f && !Reloading && !TerritoryHud.MenuOpen)
        {
            int want = Input.IsActionJustPressed("weapon1") ? 0 : Input.IsActionJustPressed("weapon2") ? 1 : -1;
            if (want >= _slots.Length) want = -1;
            if (want >= 0 && want != _cur) { _pending = want; _switchT = SwitchTime; }
        }
        float lower = 0f;
        if (_switchT > 0f)
        {
            _switchT -= dt;
            if (_pending >= 0 && _switchT <= SwitchTime * 0.5f)
            {
                _cur = _pending;
                _pending = -1;
                s = _slots[_cur];
                d = s.Def;
                SoundWorld.I.Emit(Snd.Bolt, P.Cam.GlobalPosition, -8f);
            }
            lower = 1f - Mathf.Abs(Mathf.Max(_switchT, 0f) - SwitchTime * 0.5f) / (SwitchTime * 0.5f);
        }

        // --- aiming
        bool aim = canAct && Input.IsActionPressed("aim") && !P.Sprinting && _switchT <= 0f && !Reloading;
        AimT = Mathf.MoveToward(AimT, aim ? 1f : 0f, dt / d.AdsTime);

        UpdateLock(dt);

        // --- fire mode
        if (canAct && Input.IsActionJustPressed("firemode") && d.AutoCapable && !Busy)
        {
            s.Auto = !s.Auto;
            SoundWorld.I.Emit(Snd.Click, P.Cam.GlobalPosition);
            Hud.Toast(s.Auto ? "Fire mode: AUTO" : "Fire mode: SEMI", 1.5f);
        }

        // --- firing
        _cool -= dt;
        bool trig = canAct && Input.IsActionPressed("fire");
        bool trigDown = canAct && Input.IsActionJustPressed("fire");
        if (!Busy && !P.Sprinting && _sprintT < 0.3f)
        {
            bool want = s.Auto ? trig : trigDown;
            if (want && _cool <= 0f)
            {
                if (s.Ammo > 0) Fire(s);
                else if (trigDown) SoundWorld.I.Emit(Snd.Click, P.Cam.GlobalPosition);
            }
        }

        // --- reload: a tap keeps the magazine coming off (back in a pouch, a little slower); tap again straight
        // away, before it's out, and it's dropped instead, for speed. "Quick reload" does that in one press, if bound.
        bool tap = canAct && Input.IsActionJustPressed("reload"), quick = canAct && Input.IsActionJustPressed("reload_drop");
        if ((tap || quick) && Reloading && _reloadKeep && _reloadStage == 0 && _t - _reloadPressT < 0.45f)
        {
            _reloadKeep = false;
            if (Magazines.InMag(s.Ammo, d) > 0)
            {
                float cut = Magazines.RetainTime * P.Body.ReloadMult;
                _reloadT = MathF.Max(0.05f, _reloadT - cut);
                _reloadDur = MathF.Max(0.1f, _reloadDur - cut);
            }
            Hud.Toast("Dropping the mag", 1f);
        }
        else if ((tap || quick) && !Busy && s.Mags.Worth(s.Ammo, d))
        {
            _reloadEmpty = s.Ammo == 0;
            _reloadKeep = !quick;
            bool stow = _reloadKeep && Magazines.InMag(s.Ammo, d) > 0;
            _reloadDur = ((_reloadEmpty ? d.ReloadEmpty : d.Reload) + (stow ? Magazines.RetainTime : 0f)) * P.Body.ReloadMult;
            _reloadT = _reloadDur;
            _reloadStage = 0;
            _reloadPressT = _t;
        }
        else if (tap && !Busy && s.Mags.Any) Hud.Toast("Nothing fuller to change to", 1.2f);
        float reloadTilt = 0f;
        if (_reloadT > 0f)
        {
            _reloadT -= dt;
            float prog = 1f - _reloadT / _reloadDur;
            var ear = P.Cam.GlobalPosition + AimDir * 0.4f;
            if (_reloadStage == 0 && prog > 0.18f) { SoundWorld.I.Emit(Snd.MagOut, ear, 0f, P); _reloadStage = 1; }
            if (_reloadStage == 1 && prog > 0.6f) { SoundWorld.I.Emit(Snd.MagIn, ear, 0f, P); _reloadStage = 2; }
            if (_reloadStage == 2 && _reloadEmpty && prog > 0.85f) { SoundWorld.I.Emit(Snd.Bolt, ear, 0f, P); _reloadStage = 3; }
            if (_reloadT <= 0f)
            {
                // The fullest magazine goes on; the one that came off is kept or dropped (see Magazines). A round
                // stays chambered through a reload that isn't from empty.
                int got = s.Mags.Swap(Magazines.InMag(s.Ammo, d), _reloadKeep);
                s.Ammo = _reloadEmpty || d.Chambered == 0 ? got : got + 1;
            }
            reloadTilt = Mathf.Sin(Mathf.Clamp(prog, 0f, 1f) * Mathf.Pi);
        }

        // --- springs: punch settles, lag catches up
        // Stiff springs blow up if stepped with a long frame (a lag spike), and the gun
        // would thrash around: step them in small fixed slices instead.
        float left = MathF.Min(dt, 0.25f);
        while (left > 0f)
        {
            float h = MathF.Min(left, 1f / 240f);
            left -= h;
            _punchVel += (-_punch * 260f - _punchVel * 24f) * h;
            _punch += _punchVel * h;
            _lagVel += (-_lag * 140f - _lagVel * 18f) * h;
            _lag += _lagVel * h;
        }
        _kickBack *= MathF.Exp(-dt * 14f);

        // --- sway: slow wander plus breathing, scaled by everything that makes holding steady hard
        float target = P.SwayMult * Mathf.Lerp(2.2f, 1f, AimT) * d.SwayDeg;
        _swayAmp = Mathf.Lerp(_swayAmp, target, 1f - MathF.Exp(-dt * 3f));
        _sway = new Vector2(
            (MathF.Sin(_t * 1.13f) * 0.6f + MathF.Sin(_t * 0.37f + 1.7f) * 0.4f + MathF.Sin(_t * P.BreathRate) * 0.5f) * _swayAmp,
            (MathF.Sin(_t * 0.71f + 0.4f) * 0.7f + MathF.Sin(_t * 0.23f + 2.1f) * 0.3f) * _swayAmp);
        if (P.Suppression > 0.05f)
            _sway += new Vector2(_rng.RandfRange(-1f, 1f), _rng.RandfRange(-1f, 1f)) * P.Suppression * 0.25f;

        Rotation = new Vector3(
            Mathf.DegToRad(_punch.X + _lag.X + _sway.X),
            Mathf.DegToRad(_punch.Y + _lag.Y + _sway.Y),
            0f);

        // --- model pose
        _sprintT = Mathf.MoveToward(_sprintT, P.Sprinting ? 1f : 0f, dt * 5f);
        float a = AimT * AimT * (3f - 2f * AimT);
        var pos = d.HipOffset.Lerp(d.AdsOffset, a) + P.WeaponBob * (1f - AimT * 0.8f);
        pos.Z += _kickBack;
        pos += new Vector3(-0.05f, -0.05f, 0f) * _sprintT;
        pos.Y -= lower * 0.3f + reloadTilt * 0.08f;
        s.Model.Position = pos;
        s.Model.RotationDegrees = new Vector3(-lower * 45f - reloadTilt * 12f - _sprintT * 22f, _sprintT * 38f, reloadTilt * 28f);

        for (int i = 0; i < _slots.Length; i++)
            _slots[i].Model.Visible = i == _cur && !(d.Scoped && AimT > 0.92f);
    }

    void Fire(Slot s)
    {
        var d = s.Def;
        s.Ammo--;
        // Due one cycle after the last round, not after this frame (else the rate of fire depends on the frame rate).
        _cool = (_cool > -0.034f ? _cool : 0f) + 60f / d.Rpm;

        float spread = Mathf.Lerp(d.SpreadHipDeg * (P.Moving ? 1.8f : 1f), d.SpreadAdsDeg, AimT);
        var eye = P.Cam.GlobalPosition;
        var aim = RandomCone(AimDir, Mathf.DegToRad(spread));

        // The round leaves the muzzle, below the sight, and crosses the sight line
        // at the zero range — like a real rifle. Close up it hits a little low.
        var origin = MuzzlePos;
        var excl = new Godot.Collections.Array<Rid> { P.GetRid() };
        if (GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, origin, 0xFFFFFFFF, excl)).Count > 0)
            origin = eye; // muzzle is through a wall: don't let the bullet start on the far side
        var dir = (eye + aim * d.ZeroM - origin).Normalized();
        dir = dir.Rotated(GlobalBasis.X.Normalized(), s.Zero);
        Ballistics.I.Fire(origin, dir, d.MuzzleVel * (1f + _rng.RandfRange(-0.006f, 0.006f)), d.Drag, P, d.Damage, d.Name,
            explosive: d.Explosive, armM: d.ArmM, pen: d.Pen, vehDamage: d.VehDamage, crater: d.Crater, fragR: d.FragR, power: d.Power, rocket: d.Rocket,
            homing: d.Guided && LockProgress >= 1f ? LockTarget : null, heavyCrack: d.Rocket);
        LockProgress = 0f;
        if (d.Rocket) Effects.I.MuzzleDust(P.GlobalPosition - AimDir * 2f, -AimDir); // backblast
        SoundWorld.I.Emit(d.Sound, MuzzlePos, 0f, P, facing: dir);
        Effects.I.MuzzleFlash(MuzzlePos, AimDir, ownView: !d.Rocket); // (a rocket has no flash hider: its motor lights the shooter up as it is)
        Telemetry.Shot(P, origin, eye + aim * 300f, d.Name, "player", null);

        float m = P.RecoilMult;
        _punchVel += new Vector2(d.PunchDeg * _rng.RandfRange(0.8f, 1.2f), _rng.RandfRange(-1f, 1f) * d.PunchDeg * 0.5f) * 14f * m;
        _kickBack += d.PunchDeg * 0.012f * m;
        CameraKick += new Vector2(d.VertKickDeg * _rng.RandfRange(0.85f, 1.15f), _rng.RandfRange(-1f, 1f) * d.HorizKickDeg) * m;
        P.OnShotFired();
    }

    // ---- infrared lock (MANPADS): keep an aircraft near the centre of the sight while aiming
    public Vehicle? LockTarget { get; private set; }
    public float LockProgress { get; private set; }
    float _beepT;

    void UpdateLock(float dt)
    {
        if (!Def.Guided || AimT < 0.8f) { LockTarget = null; LockProgress = 0f; return; }
        Vehicle? best = null;
        float bestAng = 4f;
        foreach (var v in Vehicle.All)
        {
            if (!v.Def.Air || v.Destroyed || v.Landed || v.CrewTeam == P.Team) continue;
            float d = v.Center.DistanceTo(P.Cam.GlobalPosition);
            if (d > 3500f) continue;
            float ang = Mathf.RadToDeg(AimDir.AngleTo(v.Center - P.Cam.GlobalPosition));
            if (ang < bestAng) { bestAng = ang; best = v; }
        }
        if (best != LockTarget) LockProgress = 0f;
        LockTarget = best;
        if (best == null) return;
        LockProgress = MathF.Min(1f, LockProgress + dt / 1.8f);
        // The growl while acquiring; a steady tone when locked.
        _beepT -= dt;
        if (_beepT <= 0f)
        {
            _beepT = LockProgress >= 1f ? 0.12f : 0.5f;
            SoundWorld.I.Emit(Snd.Beep, P.Cam.GlobalPosition, LockProgress >= 1f ? 0f : -6f);
        }
    }

    /// <summary>Spare ammo across everything carried, 0..1.</summary>
    public float AmmoLevel => _slots.Average(s => s.Def.Mags == 0 ? 1f : (float)s.Mags.Rounds / (s.Def.MagSize * s.Def.Mags));

    public bool Refill()
    {
        bool any = false;
        foreach (var s in _slots)
            if (s.Mags.Rounds < s.Def.MagSize * s.Def.Mags) { s.Mags.Refill(s.Def.Mags); any = true; }
        return any;
    }

    Vector3 RandomCone(Vector3 dir, float angle)
    {
        if (angle <= 0f) return dir;
        var perp = dir.Cross(MathF.Abs(dir.Y) < 0.99f ? Vector3.Up : Vector3.Right).Normalized();
        float off = MathF.Sqrt(_rng.Randf()) * angle;
        return dir.Rotated(perp.Rotated(dir, _rng.Randf() * Mathf.Tau), off).Normalized();
    }

    public string Describe()
    {
        var s = _slots[_cur];
        float f = s.Ammo / (float)s.Def.MagSize;
        string feel = s.Ammo == 0 ? "empty" : f > 0.9f ? "full" : f > 0.6f ? "mostly full" : f > 0.35f ? "about half" : f > 0.12f ? "getting light" : "nearly empty";
        string mode = s.Def.AutoCapable ? (s.Auto ? " · AUTO" : " · SEMI") : "";
        return $"{s.Def.Name} — mag feels {feel} · {s.Mags.Describe()}{mode}";
    }
}
