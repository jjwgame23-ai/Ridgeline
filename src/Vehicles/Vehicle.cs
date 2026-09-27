using Godot;

namespace Ridgeline;

/// <summary>
/// A ground vehicle. Kinematic, not a physics toy: it drives at a speed along its
/// heading, follows the ground by sampling it under its four corners (so it pitches
/// and rolls over terrain), and its collision box rides above the ground clearance
/// so it only ever touches walls, trees, rocks and other vehicles. That keeps 30+ of
/// them cheap and stops bots from flipping them.
///
/// Damage works by armour and penetration: each hit is checked against the armour on
/// the face it struck (front, side, rear, top, worse at an angle). Rounds that don't
/// get through do nothing much; ones that do take hull points, can knock out the
/// engine, running gear or turret, can set off the ammunition, and throw spall into
/// the crew compartment — which wounds the people inside through the ordinary body model.
/// </summary>
public partial class Vehicle : CharacterBody3D
{
    public static readonly List<Vehicle> All = new();
    /// <summary>A vehicle was destroyed: (vehicle, who did it).</summary>
    public static event Action<Vehicle, ICombatant?>? Lost;

    public VehicleDef Def = null!;
    public int Team;                     // whose it is (spawned for), even when empty
    public float Hp { get; private set; }
    public bool Destroyed { get; private set; }
    public bool EngineHit, Immobile;
    public bool[] TurretDown = null!;
    public ICombatant?[] Occupants = null!;
    public double LastHit = -99, LastFired = -99;
    /// <summary>Where the last hit came from, and how hard it hit (penetration, mm): 40+ is something that can kill armour.</summary>
    public Vector3 LastHitFrom;
    public float LastHitPen;
    /// <summary>Smoke grenade launchers: salvos left.</summary>
    public int SmokeSalvos;
    /// <summary>Back towards the goal rather than turning round to drive there (out of a firing position, away from a threat).</summary>
    public bool PreferReverse;
    /// <summary>Area fire ordered for the gunner when it has nothing better: a point, until when, and why.</summary>
    /// <summary>The sector it's covering: an idle gunner scans across this, not wherever the hull points.</summary>
    public Vector3? Watch;
    public Vector3? FireAt;
    public double FireAtUntil;
    public string FireAtWhy = "";
    /// <summary>What the crew is doing, in words (for the HUD of the squad it works with).</summary>
    public string Task = "";

    // ---- where a bot driver is taking it (set by the motor pool)
    public Vector3? Goal;
    public bool Boarding;          // parked with doors open: passengers are climbing in
    public bool Arrived;
    public float ArriveRadius = 8f;
    public sealed class DriveState
    {
        public Vector3[] Path = Array.Empty<Vector3>();
        public int Idx, Stucks;
        public Vector3 PathGoal;
        public double RepathAt, StuckSince = -1, ReverseUntil, WaitSince = -1;
        public float ReverseSteer, StuckAngle;
        public string Note = "";
    }
    public readonly DriveState Drive = new();

    // Driver's inputs, -1..1
    public float Throttle, Steer;
    public bool Brake;
    public float Speed { get; private set; }

    public sealed class TurretState
    {
        public TurretDef Def = null!;
        public Node3D YawNode = null!, PitchNode = null!, Barrel = null!, Muzzle = null!;
        public Node3D? CoaxMuzzle;
        public float Yaw, Pitch;             // relative to the hull, degrees
        public Vector3? AimAt;               // world point the gunner wants
        public int AmmoIdx;
        public int[] Loaded = null!, Stock = null!;
        public float Cool, ReloadT;
        public int CoaxLoaded, CoaxStock;
        public float CoaxCool, CoaxReloadT;
        public float Kick;
        public float WantYaw, WantPitch;
        public bool Laid;               // on the ordered bearing and elevation (for a mortar, "on target")
        public VWeapon Weapon => Def.Ammo[AmmoIdx];
        public bool Reloading => ReloadT > 0f;
        public Vector3 Forward => -Barrel.GlobalBasis.Z.Normalized();
    }
    public TurretState[] Turrets = null!;

    VehicleRig _rig = null!;
    CollisionShape3D _shape = null!;
    AudioStreamPlayer3D _engine = null!;
    /// <summary>The engine as heard from inside (only while the player is aboard).</summary>
    AudioStreamPlayer _cabin = null!;
    float _yaw, _wheelSpin, _heightVel;
    readonly RandomNumberGenerator _rng = new();
    readonly Dictionary<ICombatant, double> _ranOver = new();

    public Vector3 Center => GlobalPosition + GlobalBasis.Y * (Def.GroundClear + Def.Hull.Y * 0.5f);
    public Vector3 Forward => -GlobalBasis.Z;
    /// <summary>The highest thing on it that can be seen: the turret roof, or the top of the hull.</summary>
    public Vector3 TopPoint => Turrets.Length > 0 && !Turrets[0].Def.Exposed
        ? Turrets[0].YawNode.GlobalPosition + GlobalBasis.Y * (Turrets[0].Def.Size.Y * 0.8f)
        : Center + GlobalBasis.Y * (Def.Hull.Y * 0.5f + 0.3f);
    public bool Crewed => Occupants.Any(o => o != null);
    public ICombatant? Driver => Occupants[DriverSeat];
    public int DriverSeat => Def.Seats.FindIndex(s => s.Role == SeatRole.Driver);
    public int GunnerSeat => Def.Seats.FindIndex(s => s.Role == SeatRole.Gunner);
    /// <summary>The team it counts as right now: whoever's in it, else whoever it belongs to.</summary>
    public int CrewTeam => Occupants.FirstOrDefault(o => o != null)?.Team ?? Team;

    public override void _EnterTree() => All.Add(this);
    public override void _ExitTree() => All.Remove(this);

    public override void _Ready()
    {
        // Armour carries smoke launchers: two salvos.
        SmokeSalvos = Def.Heavy ? 2 : 0;
        _rng.Randomize();
        Hp = Def.Hp;
        Occupants = new ICombatant?[Def.Seats.Count];
        TurretDown = new bool[Def.Turrets.Count];
        CollisionLayer = Layers.Vehicles;
        CollisionMask = Layers.World | Layers.Trees | Layers.Vehicles;
        MotionMode = MotionModeEnum.Floating;

        _rig = VehicleModels.Build(Def, Team);
        AddChild(_rig.Root);
        var h = Def.Hull;
        // Riding above the ground clearance: it only meets walls, trees, rocks and vehicles.
        float boxH = h.Y + 0.4f;
        _shape = new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(h.X, boxH, h.Z) }, Position = new Vector3(0f, Def.GroundClear + 0.15f + boxH / 2f, 0f) };
        AddChild(_shape);

        Turrets = new TurretState[Def.Turrets.Count];
        for (int i = 0; i < Turrets.Length; i++)
        {
            var td = Def.Turrets[i];
            var r = _rig.Turrets[i];
            Turrets[i] = new TurretState
            {
                Def = td, YawNode = r.Yaw, PitchNode = r.Pitch, Barrel = r.Barrel, Muzzle = r.Muzzle, CoaxMuzzle = r.Coax,
                Loaded = td.Ammo.Select(w => w.Mag).ToArray(), Stock = td.Ammo.Select(w => w.Mags).ToArray(),
                CoaxLoaded = td.Coax?.Mag ?? 0, CoaxStock = td.Coax?.Mags ?? 0,
            };
        }

        _init = false;
        FlyReady();
        var loop = Def.Air ? SoundSynth.RotorLoop(Def.Kind == VKind.AH) : SoundSynth.EngineLoop(Def.Tracked, Def.Heavy);
        _cabin = new AudioStreamPlayer
        {
            Stream = SoundSynth.Muffle(loop, Def.Air ? 520f : Def.Heavy ? 260f : 340f, Def.Heavy ? 55f : Def.Air ? 90f : 75f), Bus = "World",
        };
        AddChild(_cabin);
        _engine = new AudioStreamPlayer3D { Stream = loop, Bus = "World", VolumeDb = -8f, Autoplay = !Def.Static };
        AddChild(_engine);
        // Its share of the reverb of wherever the listener is (SoundWorld.ShapeLoop drives both).
        var tail = new AudioStreamPlayer3D
        {
            Stream = loop, Bus = "Tail", PanningStrength = 0.35f, AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
            MaxDistance = 0f, DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
        };
        AddChild(tail);
        // Full level within RefDist of it (a big thing, not a point), then spreading loss.
        _sound = new SoundWorld.LoopShape { Tail = tail, RefDist = Def.Air ? 40f : Def.Heavy ? 12f : 7f, Falloff = Def.Air ? 18f : 20f };
    }

    bool _init;
    SoundWorld.LoopShape _sound = null!;

    /// <summary>
    /// Aboard, you hear the engine through the hull (low, droning, and quieter than the
    /// hiss-heavy exhaust note outside), not the outside engine from zero metres away.
    /// </summary>
    /// <summary>0 = heard from inside, 1 = from outside; eased, so getting in or out is a crossfade, not a jump.</summary>
    float _outside = 1f;

    void CabinSound()
    {
        CabinMix();
        if (SoundWorld.I != null && !Destroyed) SoundWorld.I.ShapeLoop(_engine, Center, _sound);
    }

    void CabinMix()
    {
        bool inside = !Destroyed && Player.I is { } p && p.Ride == this;
        float dt = (float)GetPhysicsProcessDeltaTime();
        // Climbing out takes a moment and your ears adjust: ease the outside engine up over ~2 s.
        _outside = inside ? MathF.Max(0f, _outside - dt * 3f) : MathF.Min(1f, _outside + dt * 0.6f);
        if (_outside >= 1f)
        {
            if (_cabin.Playing) _cabin.Stop();
            return;
        }
        if (!_cabin.Playing && _engine.Playing) _cabin.Play();
        _cabin.PitchScale = _engine.PitchScale;
        float cabinDb = _engine.VolumeDb - (Def.Air ? 6f : Def.Heavy ? 7f : 9f);
        _cabin.VolumeDb = cabinDb + Mathf.LinearToDb(MathF.Max(1f - _outside, 0.001f));
        _engine.VolumeDb += Mathf.LinearToDb(MathF.Max(_outside * _outside, 0.001f));
    }

    // ================================================================ driving

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (Destroyed) { if (Def.Air && !_landed) FallWreck(dt); return; }
        if (_doomed) { UpdateTurrets(dt); FlyTick(dt); return; }
        if (!_init)
        {
            // Placed after entering the tree: take the heading and settle on the ground now.
            _init = true;
            _yaw = GlobalRotation.Y;
            SnapToGround(1f);
        }
        UpdateTurrets(dt);
        if (Def.Air) { FlyTick(dt); return; }
        if (Def.Static) return;
        bool driven = Driver != null;
        float maxF = Immobile ? 0f : Def.MaxSpeed * (EngineHit ? 0.4f : 1f);
        float target = !driven ? 0f : Throttle > 0f ? Throttle * maxF : Throttle * (Immobile ? 0f : Def.Reverse);
        if (Brake) target = 0f;
        bool slowing = MathF.Abs(target) < MathF.Abs(Speed) || MathF.Sign(target) != MathF.Sign(Speed);
        Speed = Mathf.MoveToward(Speed, target, (Brake ? Def.Accel * 3f : slowing ? Def.Accel * 1.6f : Def.Accel) * dt);

        // Wheels steer only when rolling (and backwards in reverse); tracks can pivot.
        float steerRate = Def.TurnRate * (driven ? Steer : 0f);
        if (!Def.Tracked) steerRate *= Mathf.Clamp(MathF.Abs(Speed) / 4f, 0f, 1f) * MathF.Sign(Speed == 0f ? 1f : Speed) * (1f - Mathf.Clamp((MathF.Abs(Speed) - 12f) / 20f, 0f, 0.5f));
        else if (Immobile) steerRate = 0f;
        _yaw += Mathf.DegToRad(steerRate) * dt;

        var fwd = new Vector3(-MathF.Sin(_yaw), 0f, -MathF.Cos(_yaw));
        if (MathF.Abs(Speed) > 0.01f)
        {
            var motion = fwd * Speed * dt;
            // Too steep ahead: treat it as a wall.
            var ahead = GlobalPosition + fwd * (Def.Hull.Z * 0.5f * MathF.Sign(Speed) + motion.Length() * 4f);
            if (GroundAt(ahead, out var ga) && ga.Y - GlobalPosition.Y > MathF.Max(1.2f, Def.Hull.Z * 0.45f)) Speed *= 0.2f;
            var col = MoveAndCollide(motion);
            if (col != null) OnBump(col, motion);
        }
        SnapToGround(dt);
        RunOver();

        // Wheels turn, engine note follows the revs.
        _wheelSpin += Speed * dt / 0.5f;
        foreach (var w in _rig.Wheels) w.Rotation = new Vector3(-_wheelSpin, 0f, 0f);
        float load = Mathf.Clamp(MathF.Abs(Speed) / MathF.Max(1f, Def.MaxSpeed), 0f, 1f);
        _engine.PitchScale = (driven ? 0.85f : 0.75f) + load * 0.65f + MathF.Abs(Throttle) * 0.1f;
        _engine.VolumeDb = driven ? -11f + load * 5f : -25f;
        CabinSound();
        foreach (var kv in _ranOver.Where(kv => Clock.Now - kv.Value > 2.0).ToList()) _ranOver.Remove(kv.Key);
    }

    bool GroundAt(Vector3 p, out Vector3 hit)
    {
        var excl = new Godot.Collections.Array<Rid> { GetRid() };
        var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 3f, p + Vector3.Down * 6f, Layers.World, excl);
        var r = GetWorld3D().DirectSpaceState.IntersectRay(q);
        hit = r.Count > 0 ? r["position"].AsVector3() : p;
        return r.Count > 0;
    }

    /// <summary>Sit on the ground under the four corners: height, pitch and roll, with a little suspension lag.</summary>
    void SnapToGround(float dt)
    {
        var pos = GlobalPosition;
        var fwd = new Vector3(-MathF.Sin(_yaw), 0f, -MathF.Cos(_yaw));
        var right = new Vector3(MathF.Cos(_yaw), 0f, -MathF.Sin(_yaw));
        float hx = Def.Hull.X * 0.42f, hz = Def.Hull.Z * 0.42f;
        var pts = new[] { pos + fwd * hz - right * hx, pos + fwd * hz + right * hx, pos - fwd * hz - right * hx, pos - fwd * hz + right * hx };
        var g = new Vector3[4];
        for (int i = 0; i < 4; i++) GroundAt(pts[i], out g[i]);
        var up = (g[1] - g[2]).Cross(g[0] - g[3]).Normalized();
        if (up.Y < 0f) up = -up;
        if (up.Y < 0.5f) up = Vector3.Up;
        float targetY = (g[0].Y + g[1].Y + g[2].Y + g[3].Y) / 4f;
        float k = 1f - MathF.Exp(-dt * 10f);
        pos.Y = Mathf.Lerp(pos.Y, targetY, k);
        GlobalPosition = pos;

        var z = -(fwd - up * fwd.Dot(up)).Normalized();
        var x = up.Cross(z).Normalized();
        var target = new Basis(x, up, z).Orthonormalized();
        GlobalBasis = GlobalBasis.Orthonormalized().Slerp(target, MathF.Min(1f, dt * 8f));
    }

    void OnBump(KinematicCollision3D col, Vector3 motion)
    {
        var other = col.GetCollider();
        // A heavy vehicle knocks trees flat; anything else stops for them.
        if (Def.Heavy && other is StaticBody3D sb && (sb.CollisionLayer & Layers.Trees) != 0 && col.GetColliderShape() is CollisionShape3D cs)
        {
            Terrain.Fell(cs, Forward);
            Speed *= 0.85f;
            MoveAndCollide(col.GetRemainder());
            return;
        }
        float impact = MathF.Abs(Speed);
        // Slide along whatever we hit, having lost most of our speed into it.
        var n = col.GetNormal() with { Y = 0f };
        float into = n.LengthSquared() > 0.01f ? -motion.Normalized().Dot(n.Normalized()) : 1f;
        Speed *= 1f - 0.8f * Mathf.Clamp(into, 0f, 1f);
        if (into < 0.9f) MoveAndCollide(col.GetRemainder().Slide(n.Normalized()));
        // Only a real crash hurts (a tree or a wall at speed dents a light vehicle; armour shrugs).
        if (impact > 12f && into > 0.7f) Damage((impact - 12f) * (Def.Heavy ? 3f : 10f), null);
        if (other is Vehicle v && impact > 5f) v.Damage((impact - 5f) * 6f, Driver);
    }

    /// <summary>People in front of (or behind) a moving vehicle get knocked down and run over.</summary>
    void RunOver()
    {
        if (MathF.Abs(Speed) < 2.5f) return;
        float hx = Def.Hull.X * 0.5f + 0.3f, hz = Def.Hull.Z * 0.5f + 0.4f;
        foreach (var c in Combatants.All)
        {
            if (c.Dead || c.Ride != null || _ranOver.ContainsKey(c)) continue;
            if (c.Team == CrewTeam && MathF.Abs(Speed) < 6f) continue; // a nudge from your own side's vehicle, not a death
            var local = ToLocal(c.FeetPos);
            if (MathF.Abs(local.X) > hx || MathF.Abs(local.Z) > hz || local.Y > 2f || local.Y < -1f) continue;
            _ranOver[c] = Clock.Now;
            float dmg = MathF.Abs(Speed) * (Def.Heavy ? 22f : 12f);
            c.TakeHit(new HitInfo { Shooter = Driver, Point = c.ChestPos, Dir = Forward, Damage = dmg, Zone = HitZone.Torso, Distance = 0f, Weapon = Def.Name });
        }
    }

    // ================================================================ turrets and guns

    void UpdateTurrets(float dt)
    {
        for (int i = 0; i < Turrets.Length; i++)
        {
            var t = Turrets[i];
            var td = t.Def;
            t.Cool -= dt;
            t.CoaxCool -= dt;
            if (t.ReloadT > 0f)
            {
                t.ReloadT -= dt;
                if (t.ReloadT <= 0f && t.Stock[t.AmmoIdx] > 0) { t.Stock[t.AmmoIdx]--; t.Loaded[t.AmmoIdx] = t.Weapon.Mag; }
            }
            if (t.CoaxReloadT > 0f)
            {
                t.CoaxReloadT -= dt;
                if (t.CoaxReloadT <= 0f && t.CoaxStock > 0) { t.CoaxStock--; t.CoaxLoaded = td.Coax!.Mag; }
            }
            if (td.Fixed) { t.Yaw = 0f; t.Pitch = td.PitchMin; }
            else if (!TurretDown[i] && t.AimAt is Vector3 aim)
            {
                // The wanted direction in hull space: turrets are stabilised, so this is recomputed every tick.
                var local = GlobalBasis.Inverse() * (aim - t.YawNode.GlobalPosition);
                float wantYaw = Mathf.RadToDeg(MathF.Atan2(-local.X, -local.Z)) - td.MountYaw;
                wantYaw = Mathf.Clamp(Mathf.Wrap(wantYaw, -180f, 180f), -td.YawLimit, td.YawLimit);
                float flat = new Vector2(local.X, local.Z).Length();
                float wantPitch = Mathf.Clamp(Mathf.RadToDeg(MathF.Atan2(local.Y - 0.5f, flat)), td.PitchMin, td.PitchMax);
                if (td.Indirect)
                {
                    // A mortar: the high-angle solution that drops the bomb on the point.
                    float v = td.Ammo[0].Speed, g = 9.81f, x = MathF.Max(flat, 1f), y = local.Y;
                    float disc = v * v * v * v - g * (g * x * x + 2f * y * v * v);
                    wantPitch = disc < 0f ? 45f : Mathf.Clamp(Mathf.RadToDeg(MathF.Atan((v * v + MathF.Sqrt(disc)) / (g * x))), td.PitchMin, td.PitchMax);
                }
                t.WantYaw = wantYaw;
                t.WantPitch = wantPitch;
                float dy = Mathf.Wrap(wantYaw - t.Yaw, -180f, 180f);
                t.Yaw += Mathf.Clamp(dy, -td.YawSpeed * dt, td.YawSpeed * dt);
                t.Pitch = Mathf.MoveToward(t.Pitch, wantPitch, td.PitchSpeed * dt);
            }
            t.YawNode.Rotation = new Vector3(0f, Mathf.DegToRad(t.Yaw + td.MountYaw), 0f);
            t.PitchNode.Rotation = new Vector3(Mathf.DegToRad(t.Pitch), 0f, 0f);
            t.Laid = MathF.Abs(Mathf.Wrap(t.WantYaw - t.Yaw, -180f, 180f)) < 0.6f && MathF.Abs(t.WantPitch - t.Pitch) < 0.6f;
            t.Kick = Mathf.MoveToward(t.Kick, 0f, dt * 1.2f);
            t.Barrel.Position = new Vector3(0f, 0f, t.Kick);
        }
    }

    /// <summary>How far off the gun is from where the gunner wants it, degrees.</summary>
    public float AimError(int turret)
    {
        var t = Turrets[turret];
        if (t.AimAt is not Vector3 a) return 180f;
        return Mathf.RadToDeg(t.Forward.AngleTo(a - t.Muzzle.GlobalPosition));
    }

    public void SelectAmmo(int turret, int idx)
    {
        var t = Turrets[turret];
        if (idx == t.AmmoIdx || idx < 0 || idx >= t.Def.Ammo.Length) return;
        t.AmmoIdx = idx;
        // A cannon has to unload and reload to change round; a dual-feed autocannon just switches belts.
        if (t.Weapon.Mag == 1) { t.ReloadT = 60f / t.Weapon.Rpm; t.Loaded[idx] = 0; }
    }

    public void Reload(int turret)
    {
        var t = Turrets[turret];
        if (t.Reloading || t.Stock[t.AmmoIdx] <= 0 || t.Loaded[t.AmmoIdx] >= t.Weapon.Mag) return;
        t.Loaded[t.AmmoIdx] = 0;
        t.ReloadT = t.Weapon.Mag == 1 ? 60f / t.Weapon.Rpm : t.Weapon.Reload;
    }

    /// <summary>Fire the main gun (or the coax). The round leaves the muzzle down the barrel.</summary>
    public bool Fire(int turret, bool coax, float rangeHint = 0f, Vector3? dirOverride = null)
    {
        if (Destroyed || TurretDown[turret]) return false;
        var t = Turrets[turret];
        var gunner = Occupants[Math.Max(0, Def.Seats.FindIndex(s => s.Turret == turret))];
        VWeapon w;
        Node3D muzzle;
        if (coax)
        {
            if (t.Def.Coax == null || t.CoaxCool > 0f || t.CoaxReloadT > 0f) return false;
            if (t.CoaxLoaded <= 0) { if (t.CoaxStock > 0) t.CoaxReloadT = t.Def.Coax.Reload; return false; }
            w = t.Def.Coax;
            muzzle = t.CoaxMuzzle!;
            t.CoaxLoaded--;
            t.CoaxCool = 60f / w.Rpm;
        }
        else
        {
            if (t.Cool > 0f || t.Reloading) return false;
            if (t.Loaded[t.AmmoIdx] <= 0) { Reload(turret); return false; }
            w = t.Weapon;
            muzzle = t.Muzzle;
            t.Loaded[t.AmmoIdx]--;
            t.Cool = 60f / w.Rpm;
            if (t.Loaded[t.AmmoIdx] <= 0 && t.Stock[t.AmmoIdx] > 0) t.ReloadT = w.Mag == 1 ? 60f / w.Rpm : w.Reload;
            t.Kick = w.Kick;
        }
        var dir = dirOverride ?? -muzzle.GlobalBasis.Z.Normalized();
        float spread = Mathf.DegToRad(w.SpreadDeg) * (1f + MathF.Abs(Speed) / 8f);
        var perp = dir.Cross(Vector3.Up).Normalized();
        dir = dir.Rotated(perp.Rotated(dir, _rng.Randf() * Mathf.Tau), MathF.Sqrt(_rng.Randf()) * spread).Normalized();
        // Superelevation for the range the gunner thinks it is (a mortar's lay already has it).
        if (rangeHint > 0f && w.Speed > 0f && !t.Def.Indirect && !t.Def.Fixed) dir = dir.Rotated(perp, Ballistics.ZeroAngle(w.Speed, w.Drag, rangeHint));
        var from = muzzle.GlobalPosition;
        LastFired = Clock.Now;
        Ballistics.I.Fire(from, dir, w.Speed, w.Drag, gunner, w.Damage, w.Name, GetRid(), explosive: w.Explosive, armM: w.Explosive ? 8f : 0f,
            pen: w.Pen, vehDamage: w.VehDamage, crater: w.Crater, frags: w.Frags, rocket: w.Sound == Snd.Rocket, prox: w.Prox,
            whistle: t.Def.Indirect, shooterVehicle: this, heavyCrack: w.Sound is Snd.Hmg or Snd.Autocannon or Snd.Cannon or Snd.Rocket);
        // Aboard, you're in the gun's near field (and may be hearing through a chase camera): no muzzle directivity.
        SoundWorld.I.Emit(w.Sound, from, 0f, gunner, facing: Player.I is { } pl && pl.Ride == this ? default : dir);
        Effects.I.MuzzleFlash(from, dir, w.Flash);
        if (w.Flash > 2f) Effects.I.MuzzleDust(GlobalPosition + dir * 3f, dir);
        return true;
    }

    // ================================================================ damage

    /// <summary>A projectile struck the hull: does it get through the armour where it hit?</summary>
    public void TakeProjectile(Projectile p, Vector3 pos, Vector3 normal)
    {
        if (Destroyed) return;
        LastHit = Clock.Now;
        LastHitFrom = p.Shooter is { } sh && GodotObject.IsInstanceValid((GodotObject)sh) ? sh.EyePos : pos - p.Vel.Normalized() * 150f;
        LastHitPen = p.Pen;
        float armor = ArmorFacing(-p.Vel.Normalized(), out float cos);
        // Sloped armour: a round striking at an angle has more steel to go through.
        float effective = armor / MathF.Pow(MathF.Max(0.3f, cos), 0.7f);
        float pen = p.Pen * (p.Explosive ? 1f : MathF.Pow(p.Vel.Length() / p.MuzzleSpeed, 1.2f)) * _rng.RandfRange(0.9f, 1.1f);
        if (pen >= effective)
        {
            Penetrate(p.VehDamage, pen / MathF.Max(effective, 1f), p.Shooter, p.Damage);
            if (p.VehDamage >= 20f) Effects.I.Impact(pos, normal, false, Surface.Metal);
        }
        else
        {
            // Bounced or stopped: a spark and a clang; blast still scours the outside a little.
            if (p.Explosive && armor < 30f) Damage(p.VehDamage * 0.15f, p.Shooter);
            // Small arms: a faint tick-clank; a big round bouncing: a heavy one.
            SoundWorld.I.Emit(Snd.ArmorHit, pos, p.Pen > 20f ? 4f : -10f);
            Ballistics.Ricochet(p, pos, normal, true); // a round glancing off sloped armour
            Effects.I.Impact(pos, normal, true);
        }
    }

    void Penetrate(float dmg, float overmatch, ICombatant? by, float bulletDamage)
    {
        // Small arms through a thin skin: the round may find someone inside.
        if (dmg < 5f)
        {
            Damage(dmg, by);
            var inside = Occupants.Where(o => o != null && !o.Dead).ToList();
            if (inside.Count > 0 && _rng.Randf() < 0.35f)
            {
                var o = inside[_rng.RandiRange(0, inside.Count - 1)]!;
                o.TakeHit(new HitInfo { Shooter = by, Point = o.ChestPos, Dir = Vector3.Down, Damage = bulletDamage * 0.7f, Zone = _rng.Randf() < 0.15f ? HitZone.Head : HitZone.Torso, Weapon = "through the door" });
            }
            return;
        }
        Damage(dmg, by);
        if (Destroyed) return;
        // What it hit on the way through.
        if (_rng.Randf() < 0.12f) EngineHit = true;
        if (_rng.Randf() < 0.1f) Immobile = true;
        for (int i = 0; i < TurretDown.Length; i++) if (_rng.Randf() < 0.08f) TurretDown[i] = true;
        if (dmg >= 250f && _rng.Randf() < 0.05f * MathF.Min(overmatch, 3f)) { Damage(Hp + 1f, by); return; } // ammunition
        // Spall into the crew compartment.
        foreach (var o in Occupants)
        {
            if (o == null || o.Dead || _rng.Randf() > MathF.Min(0.8f, dmg / 320f + 0.12f)) continue;
            o.TakeHit(new HitInfo { Shooter = by, Point = o.ChestPos, Dir = Vector3.Down, Damage = 25f + dmg * 0.08f, Zone = _rng.Randf() < 0.1f ? HitZone.Head : _rng.Randf() < 0.3f ? HitZone.Legs : HitZone.Torso, Weapon = "spall" });
        }
    }

    /// <summary>
    /// Which armour faces something coming from direction <paramref name="toSource"/> (unit,
    /// pointing from the vehicle toward the shooter): top if it's steeply from above, else
    /// front / side / rear by bearing. <paramref name="cos"/> is how square-on it strikes that face.
    /// </summary>
    public float ArmorFacing(Vector3 toSource, out float cos)
    {
        var l = GlobalBasis.Inverse() * toSource;
        if (l.Y > 0.55f) { cos = l.Y; return Def.ArmorTop; }
        var flat = new Vector2(l.X, l.Z);
        float len = flat.Length();
        if (len < 1e-3f) { cos = 1f; return Def.ArmorTop; }
        flat /= len;
        // -Z is the front.
        if (-flat.Y > 0.7071f) { cos = -flat.Y; return Def.ArmorFront; }
        if (flat.Y > 0.7071f) { cos = flat.Y; return Def.ArmorRear; }
        cos = MathF.Abs(flat.X);
        return Def.ArmorSide;
    }

    /// <summary>The armour a round from <paramref name="from"/> would meet (for gunners deciding if a shot is worth it).</summary>
    public float ArmorToward(Vector3 from) => ArmorFacing((from - Center).Normalized(), out _);

    /// <summary>Blast outside the hull: light vehicles get torn up, armour shrugs it off.</summary>
    public static void BlastAll(Vector3 pos, float power)
    {
        foreach (var v in All.ToArray())
        {
            if (v.Destroyed) continue;
            float d = v.Center.DistanceTo(pos) - v.Def.Hull.X * 0.5f;
            if (d > 5f) continue;
            float armor = MathF.Min(v.Def.ArmorSide, v.Def.ArmorTop);
            if (armor > 20f) continue;
            v.Damage(power * 60f * (1f - MathF.Max(d, 0f) / 5f) * (armor < 6f ? 1f : 0.4f), null);
        }
    }

    public void Damage(float dmg, ICombatant? by)
    {
        if (Destroyed || dmg <= 0f) return;
        Hp -= dmg;
        LastHit = Clock.Now;
        if (Hp <= 0f) { if (!AirDoom(by)) Destroy(by); }
        else if (Def.Air && dmg > 60f && _rng.Randf() < 0.15f) Immobile = true; // tail rotor
        else if (Hp < Def.Hp * 0.25f && !EngineHit && _rng.Randf() < 0.3f) EngineHit = true;
    }

    void Destroy(ICombatant? by)
    {
        if (Destroyed) return;
        Destroyed = true;
        Speed = 0f;
        bool airborne = Def.Air && !_landed;
        if (!airborne) _vel = Vector3.Zero; // an aircraft keeps its momentum as it falls
        Hp = 0f;
        _engine.Stop();
        _cabin.Stop();
        _sound.Tail?.Stop();
        var c = Center;
        Grenade.Detonate(c, Vector3.Up, by, Def.Heavy ? 40 : 20, 0f, Def.Heavy ? 6f : 2f, Def.Name + " cook-off");
        // In the air the fire goes with the wreck (FallWreck); on the ground it burns here.
        if (airborne) _trail ??= Effects.I.Burn(c, 45f);
        else
        {
            _trail?.MoveTo(c);
            _trail = null;
            Effects.I.Burn(c + Vector3.Up * 0.5f, Def.Heavy ? 90f : 45f);
        }
        // The crew: some make it out.
        for (int i = 0; i < Occupants.Length; i++)
        {
            var o = Occupants[i];
            if (o == null) continue;
            Occupants[i] = null;
            var at = GlobalPosition + GlobalBasis.X * (Def.Hull.X * 0.5f + 1.2f) * (i % 2 == 0 ? -1f : 1f) + Forward * (i * 0.8f - 1.5f);
            o.Dismount(at);
            if (!o.Dead) o.TakeHit(new HitInfo { Shooter = by, Point = o.ChestPos, Dir = Vector3.Up, Damage = _rng.Randf() < 0.55f ? 400f : 60f, Zone = HitZone.Torso, Weapon = Def.Name + " burning" });
        }
        // A burnt-out wreck stays where it died.
        var charred = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.065f, 0.06f), Roughness = 1f };
        foreach (var m in _rig.Meshes) m.MaterialOverride = charred;
        if (Def.Kind == VKind.MBT && _rig.Turrets.Count > 0 && _rng.Randf() < 0.5f)
        {
            var tur = _rig.Turrets[0].Yaw; // the turret toss
            tur.Position += new Vector3(_rng.RandfRange(-1.5f, 1.5f), 0.6f, _rng.RandfRange(-1.5f, 1.5f));
            tur.Rotation = new Vector3(_rng.RandfRange(-0.4f, 0.4f), _rng.RandfRange(0f, 3f), _rng.RandfRange(-0.5f, 0.5f));
        }
        Lost?.Invoke(this, by);
    }

    // ================================================================ seats

    public Vector3 SeatWorld(int seat)
    {
        var s = Def.Seats[seat];
        // A turret seat turns with the turret.
        if (s.Turret >= 0) return Turrets[s.Turret].YawNode.ToGlobal(new Vector3(0f, s.Pos.Y - Def.Turrets[s.Turret].Mount.Y, 0f));
        return ToGlobal(s.Pos);
    }

    public int FreeSeat(SeatRole? prefer = null)
    {
        for (int i = 0; i < Occupants.Length; i++)
            if (Occupants[i] == null && (prefer == null || Def.Seats[i].Role == prefer)) return i;
        return prefer != null ? FreeSeat(null) : -1;
    }

    public bool Enter(ICombatant c, int seat)
    {
        if (Destroyed || seat < 0 || seat >= Occupants.Length || Occupants[seat] != null) return false;
        if (Occupants.Any(o => o != null && o.Team != c.Team)) return false;
        int old = Array.IndexOf(Occupants, c);
        if (old >= 0) Occupants[old] = null;
        Occupants[seat] = c;
        Team = c.Team;
        c.Mount(this, seat);
        return true;
    }

    /// <param name="awayFrom">Get out on the side away from this point (the enemy): the hull between you and them.</param>
    public void Leave(ICombatant c, Vector3? awayFrom = null)
    {
        int i = Array.IndexOf(Occupants, c);
        if (i < 0) return;
        Occupants[i] = null;
        if (Def.Seats[i].Role == SeatRole.Driver) { Throttle = Steer = 0f; Brake = true; }
        // Out the side, away from the hull.
        var side = GlobalBasis.X * (Def.Hull.X * 0.5f + 1.0f) * (Def.Seats[i].Pos.X < 0f ? -1f : 1f);
        var at = GlobalPosition + side + Forward * MathF.Min(Def.Seats[i].Pos.Z, 1f) * -1f;
        if (awayFrom is Vector3 threat)
        {
            // Behind the hull from the threat, spread a little so they don't pile out onto one spot.
            var away = (GlobalPosition - threat) with { Y = 0f };
            if (away.LengthSquared() > 1f)
            {
                away = away.Normalized();
                var across = away.Cross(Vector3.Up);
                at = GlobalPosition + away * (MathF.Max(Def.Hull.X, Def.Hull.Z) * 0.5f + 1.5f) + across * ((i % 3) - 1) * 1.2f;
            }
        }
        c.Dismount(at);
    }

    /// <summary>Everyone aboard follows the vehicle.</summary>
    public override void _Process(double delta)
    {
        for (int i = 0; i < Occupants.Length; i++)
        {
            var o = Occupants[i];
            if (o == null) continue;
            if (o.Dead || !GodotObject.IsInstanceValid((GodotObject)o)) { Occupants[i] = null; continue; }
            o.SyncSeat(SeatWorld(i), Def.Seats[i].Turret >= 0 ? Turrets[Def.Seats[i].Turret].YawNode.GlobalRotation.Y : GlobalRotation.Y);
        }
    }

    /// <summary>
    /// Move this vehicle's own meshes to render layer 2 (or back to 1), so a gunner's sight
    /// camera that leaves that layer out doesn't see the inside of its own turret.
    /// </summary>
    public void OwnView(bool hide)
    {
        foreach (var m in _rig.Meshes) m.Layers = hide ? 2u : 1u;
    }

    public string Status()
    {
        var parts = new List<string> { $"{Hp / Def.Hp * 100f:0}%" };
        if (EngineHit) parts.Add("ENGINE DAMAGED");
        if (Immobile) parts.Add(Def.Air ? "TAIL ROTOR" : Def.Tracked ? "TRACK THROWN" : "WHEELS OUT");
        if (TurretDown.Any(x => x)) parts.Add("TURRET JAMMED");
        return string.Join(" · ", parts);
    }
}
