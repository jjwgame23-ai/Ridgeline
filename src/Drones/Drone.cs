using Godot;

namespace Ridgeline;

/// <summary>
/// Quad: a camera drone that can drop grenades. Fpv: a racing-quad strike drone with a
/// fragmentation charge, for people. FpvAt: a bigger one carrying an RPG warhead (a
/// shaped charge), for armour: it climbs and dives onto the thin roof.
/// </summary>
public enum DroneKind { Quad, Fpv, FpvAt }

/// <summary>
/// A small drone, the two kinds modern infantry actually use:
/// - Quad (a civilian quadcopter, Mavic-type): slow (~15 m/s), hovers 60-100 m up, a camera
///   looking down. What it sees goes to the whole side's intel picture (the map, nearby
///   squads, the mortars' targeting). It carries a couple of grenades with impact fuses to
///   drop on people who've gone to ground. About 8 minutes of battery.
/// - FPV: a racing drone with a shaped charge and an impact fuse, flown fast (~38 m/s) and
///   low, then dived into a vehicle or a position. One-way.
/// Both are tiny and hard to hit, but a rifle round brings them down; you hear them before
/// you see them (the quad's drone and the FPV's scream).
/// </summary>
public partial class Drone : Node3D
{
    public static readonly List<Drone> All = new();
    public static int Launched, BombsDropped, FpvStrikes, ShotDown, Spots;
    /// <summary>
    /// Grenades in the air from a quad: where each will land and when. A man with the drone in sight overhead may
    /// see the small dark thing let go and fall (see BotBrain.CheckIncoming); nobody hears it.
    /// </summary>
    public static readonly List<(Vector3 At, double When)> Drops = new();
    /// <summary>The mode's event log (verbose runs).</summary>
    public static Action<string>? Log;

    public DroneKind Kind;
    public int Team;
    public ICombatant Operator = null!;
    public Vector3 Vel;
    public bool Dead;
    public float Battery = 1f;
    public int Bombs;
    /// <summary>Autopilot: where to be, and how high above the ground.</summary>
    public Vector3? Goal;
    public float GoalAgl = 90f;
    /// <summary>FPV: what it's going for (a vehicle, a man, or just a point).</summary>
    public Vehicle? TargetV;
    public ICombatant? TargetC;
    public Vector3 AimAt;
    public bool Terminal;
    /// <summary>Flown by the player: stick inputs instead of the autopilot.</summary>
    public bool Manual;
    public Vector3 StickMove;
    public float StickClimb, Yaw, CamPitch, Throttle = 0.7f;
    public double LaunchedAt;
    /// <summary>Who the camera has picked out lately.</summary>
    public readonly Dictionary<ICombatant, double> Seen = new();

    public bool IsFpv => Kind != DroneKind.Quad;
    float MaxSpeed => Kind switch { DroneKind.Fpv => 38f, DroneKind.FpvAt => 30f, _ => 15f };
    float Accel => Kind switch { DroneKind.Fpv => 26f, DroneKind.FpvAt => 17f, _ => 6f };
    /// <summary>Shot or out of control with its charge armed: it goes off where it comes down (mostly: some are duds).</summary>
    bool _fallFuse;
    StaticBody3D _hit = null!;
    AudioStreamPlayer3D _snd = null!;
    SoundWorld.LoopShape _shape = null!;
    Node3D _visual = null!;
    MeshInstance3D[] _props = Array.Empty<MeshInstance3D>();
    double _scanAt, _deadAt;
    static AudioStreamWav? _quadLoop, _fpvLoop;

    public static void Clear()
    {
        foreach (var d in All.ToArray()) if (IsInstanceValid(d)) d.QueueFree();
        All.Clear();
        Drops.Clear();
        Launched = BombsDropped = FpvStrikes = ShotDown = Spots = 0;
    }

    public override void _Ready()
    {
        All.Add(this);
        Launched++;
        LaunchedAt = Clock.Now;
        var dark = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.16f, 0.17f), Roughness = 0.8f };
        var prop = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.3f, 0.3f, 0.35f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        _visual = new Node3D();
        AddChild(_visual);
        float arm = Kind switch { DroneKind.Fpv => 0.14f, DroneKind.FpvAt => 0.2f, _ => 0.18f };
        _visual.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = IsFpv ? new Vector3(0.1f, 0.05f, 0.22f) : new Vector3(0.1f, 0.07f, 0.2f) }, MaterialOverride = dark });
        if (Kind == DroneKind.Fpv) // the charge strapped under it
            _visual.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.04f, Height = 0.3f }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.3f, 0.34f, 0.24f) }, Position = new Vector3(0f, -0.05f, 0.05f), RotationDegrees = new Vector3(90f, 0f, 0f) });
        else if (Kind == DroneKind.FpvAt)
        {
            // An RPG warhead lashed on, pointing forward: the cone out in front, the body under the frame.
            var olive = new StandardMaterial3D { AlbedoColor = new Color(0.28f, 0.32f, 0.2f) };
            _visual.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.045f, BottomRadius = 0.045f, Height = 0.35f }, MaterialOverride = olive, Position = new Vector3(0f, -0.07f, -0.05f), RotationDegrees = new Vector3(90f, 0f, 0f) });
            _visual.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.05f, Height = 0.2f }, MaterialOverride = olive, Position = new Vector3(0f, -0.07f, -0.32f), RotationDegrees = new Vector3(-90f, 0f, 0f) });
        }
        var props = new List<MeshInstance3D>();
        foreach (var (x, z) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
        {
            _visual.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.02f, 0.02f, arm * 1.5f) }, MaterialOverride = dark, Position = new Vector3(x * arm * 0.5f, 0f, z * arm * 0.5f), RotationDegrees = new Vector3(0f, x * z * 45f, 0f) });
            var p = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = arm * 0.55f, BottomRadius = arm * 0.55f, Height = 0.005f, RadialSegments = 12 }, MaterialOverride = prop, Position = new Vector3(x * arm, 0.03f, z * arm) };
            _visual.AddChild(p);
            props.Add(p);
        }
        _props = props.ToArray();

        // Something for bullets to hit (and nothing else: characters and the navmesh ignore it).
        _hit = new StaticBody3D { CollisionLayer = Layers.Drones, CollisionMask = 0 };
        AddChild(_hit);
        _hit.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = IsFpv ? 0.16f : 0.2f } });

        _quadLoop ??= SoundSynth.DroneLoop(false);
        _fpvLoop ??= SoundSynth.DroneLoop(true);
        _snd = new AudioStreamPlayer3D { Stream = IsFpv ? _fpvLoop : _quadLoop, Bus = "World", Autoplay = true };
        AddChild(_snd);
        var tail = new AudioStreamPlayer3D { Stream = _snd.Stream, Bus = "Tail", PanningStrength = 0.35f, AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled, MaxDistance = 0f };
        AddChild(tail);
        // A small thing: loud up close, gone past a few hundred metres (the FPV carries further, it screams).
        _shape = new SoundWorld.LoopShape { Tail = tail, RefDist = IsFpv ? 6f : 3f, Falloff = IsFpv ? 17f : 21f };
    }

    public override void _ExitTree() => All.Remove(this);

    /// <summary>The ground (or roof, or treetop) under a point.</summary>
    float GroundAt(Vector3 p)
    {
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 200f, p + Vector3.Down * 400f, Layers.World | Layers.Trees));
        return hit.Count > 0 ? hit["position"].AsVector3().Y : Effects.Ground?.HeightAt(p.X, p.Z) ?? 0f;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        var pos = GlobalPosition;
        if (Dead)
        {
            // Down: it falls, and lies there a while.
            Vel += Vector3.Down * 9.81f * dt;
            var n = pos + Vel * dt;
            var h = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(pos, n, Layers.World | Layers.Trees | Layers.Vehicles));
            if (h.Count > 0)
            {
                var at = h["position"].AsVector3();
                if (_fallFuse) { _fallFuse = false; Dead = false; Detonate(at, h["normal"].AsVector3(), h["collider"].AsGodotObject()); return; }
                Vel = Vector3.Zero;
                GlobalPosition = at;
            }
            else GlobalPosition = n;
            if (Clock.Now - _deadAt > 30.0) QueueFree();
            return;
        }
        // A target that's gone from the world (a wreck cleared away, a body despawned): aim at where it was.
        if (TargetV != null && !IsInstanceValid(TargetV)) TargetV = null;
        if (TargetC is GodotObject tco && !IsInstanceValid(tco)) TargetC = null;
        if (Kind == DroneKind.Quad) Battery -= dt / 480f;
        if (Battery <= 0f) { Crash(); return; }
        if (Operator is { Dead: true } && !Manual && IsFpv) { Crash(); return; }

        // Where we want to be going.
        Vector3 want;
        if (Manual && Kind == DroneKind.Quad)
            want = StickMove * MaxSpeed + Vector3.Up * StickClimb * 5f;
        else if (Manual && IsFpv)
            want = Basis.FromEuler(new Vector3(Mathf.DegToRad(CamPitch), Mathf.DegToRad(Yaw), 0f)) * Vector3.Forward * (MaxSpeed * Mathf.Clamp(Throttle, 0.25f, 1f));
        else if (IsFpv && (Terminal || TargetPoint().DistanceTo(pos) < 260f))
        {
            // Terminal dive: straight at the target, leading it.
            Terminal = true;
            var tp = TargetPoint();
            float tof = tp.DistanceTo(pos) / MaxSpeed;
            if (TargetV != null) tp += TargetV.Velocity3 * tof;
            else if (TargetC != null) tp += TargetC.Vel * tof;
            // The AT drone goes for the roof: up over the target first, then steeply down onto it.
            float flatD = ((tp - pos) with { Y = 0f }).Length();
            if (Kind == DroneKind.FpvAt && TargetV != null && flatD > 45f && pos.Y - tp.Y < flatD * 0.9f)
                want = ((tp + Vector3.Up * MathF.Min(60f, flatD * 0.9f)) - pos).Normalized() * MaxSpeed;
            else
                want = (tp - pos).Normalized() * MaxSpeed;
        }
        else if (Goal is Vector3 g)
        {
            var flat = (g - pos) with { Y = 0f };
            float dist = flat.Length();
            var h = dist > 0.5f ? flat / dist * MathF.Min(MaxSpeed, dist * 0.7f) : Vector3.Zero;
            // Just launched: straight up clear of whatever's around before going anywhere.
            if (Clock.Now - LaunchedAt < 6.0 && pos.Y - GroundAt(pos) < MathF.Min(15f, GoalAgl * 0.5f)) h = Vector3.Zero;
            // Hold height over whatever's below, and look a little ahead for rising ground.
            float ground = MathF.Max(GroundAt(pos), GroundAt(pos + h * 2f));
            float climb = Mathf.Clamp((ground + GoalAgl - pos.Y) * 0.8f, IsFpv ? -12f : -4f, IsFpv ? 12f : 5f);
            want = h + Vector3.Up * climb;
        }
        else want = Vector3.Zero;

        Vel = Vel.MoveToward(want, Accel * dt);
        var next = pos + Vel * dt;
        // The FPV's fuse arms a couple of seconds out, clear of the man who launched it.
        bool armed = IsFpv && Clock.Now - LaunchedAt > 2.0;
        uint mask = Layers.World | Layers.Trees | Layers.Vehicles | Layers.Doors | (armed ? Layers.Characters : 0u);
        var excl = new Godot.Collections.Array<Rid> { _hit.GetRid() };
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(pos, next, mask, excl));
        if (hit.Count > 0)
        {
            if (IsFpv && !armed) { Crash(); return; }
            if (IsFpv) Detonate(hit["position"].AsVector3(), hit["normal"].AsVector3(), hit["collider"].AsGodotObject());
            else Crash();
            return;
        }
        GlobalPosition = next;
        // Close enough to a man it was going for: the fuse.
        if (armed && TargetC is { Alive: true } tc && tc.ChestPos.DistanceTo(next) < 1.2f) { Detonate(next, Vector3.Up, tc as GodotObject); return; }

        // Attitude: tilt into the direction of travel; props spin.
        var hv = Vel with { Y = 0f };
        if (!Manual && hv.LengthSquared() > 0.5f) Yaw = Mathf.RadToDeg(MathF.Atan2(-hv.X, -hv.Z));
        float tilt = MathF.Min(25f, hv.Length() * (IsFpv ? 1.2f : 1.6f));
        _visual.Rotation = new Vector3(-Mathf.DegToRad(tilt), Mathf.DegToRad(Yaw), 0f);
        foreach (var p in _props) p.RotateY(dt * 90f);

        if (Kind == DroneKind.Quad && Clock.Now > _scanAt) { _scanAt = Clock.Now + 0.4; Scan(); }

        // An FPV screams: you hear it coming a long way off, and hear it change note as it turns towards you.
        _snd.VolumeDb = Kind switch { DroneKind.Fpv => 4f, DroneKind.FpvAt => 5f, _ => -5f };
        _snd.PitchScale = Kind switch
        {
            DroneKind.Fpv => 0.85f + Vel.Length() / MaxSpeed * 0.35f,
            DroneKind.FpvAt => 0.72f + Vel.Length() / MaxSpeed * 0.3f,
            _ => 0.95f + Vel.Length() / MaxSpeed * 0.1f,
        };
        SoundWorld.I?.ShapeLoop(_snd, GlobalPosition, _shape);
    }

    public Vector3 TargetPoint() =>
        TargetV is { Destroyed: false } v && IsInstanceValid(v) ? v.Center + Vector3.Up * 0.8f
        : TargetC is { Alive: true } c ? c.ChestPos
        : AimAt;

    /// <summary>
    /// The quad's camera, looking down and out to ~120 m: everyone it can see (no roof or
    /// canopy in the way) goes on the side's intel picture and to friendlies nearby.
    /// Vehicles get called in on the radio.
    /// </summary>
    void Scan()
    {
        var pos = GlobalPosition;
        var space = GetWorld3D().DirectSpaceState;
        var excl = new Godot.Collections.Array<Rid> { _hit.GetRid() };
        double now = Clock.Now;
        foreach (var c in Combatants.All)
        {
            if (!c.Alive || c.Team == Team) continue;
            var d = c.ChestPos - pos;
            float horiz = new Vector2(d.X, d.Z).Length();
            if (horiz > 130f || d.Y > 0f) continue;
            var h = space.IntersectRay(PhysicsRayQueryParameters3D.Create(pos, c.ChestPos, Layers.World | Layers.Trees | Layers.Vehicles, excl));
            if (h.Count > 0) continue;
            if (GD.Randf() > ThermalTransmission(d.Length())) continue;
            if (!Seen.ContainsKey(c) || now - Seen[c] > 5.0)
            {
                Spots++;
                Intel.Report(Team, c, c.FeetPos);
                Squad.ReportSpotted(Team, c, c.FeetPos);
            }
            Seen[c] = now;
        }
        foreach (var v in Vehicle.All)
        {
            if (v.Destroyed || !v.Crewed || v.CrewTeam == Team) continue;
            var d = v.Center - pos;
            if (new Vector2(d.X, d.Z).Length() > 180f) continue;
            if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(pos, v.Center, Layers.World | Layers.Trees, excl)).Count > 0) continue;
            if (GD.Randf() > ThermalTransmission(d.Length())) continue;
            if (Operator.Alive) Radio.Report(Operator, RadioKind.Armor, v.Center, v);
        }
    }

    /// <summary>
    /// How much of what a quad's camera would show gets through the air: the military quads of 2024 carry a thermal
    /// camera alongside the visible one (Mavic 3T class), so the dark costs it next to nothing, and haze, fog and rain
    /// are what cost. Long-wave infrared goes about 3x as far through haze, 1.5x through fog, no further than light
    /// through rain, relative to clear air (20 km), as BotBrain's night gear works it out (NightGear.AirLoss/ThermalThrough,
    /// not public: the same small formula here). (The scan used to see through any weather as if it were clear.)
    /// Each pass notices a man with this chance, so a sight on the edge of the weather's range is seen less often rather than never.
    /// </summary>
    static float ThermalTransmission(float dist)
    {
        float through = Conditions.Weather switch { WeatherKind.Rain => 1f, WeatherKind.Fog => 1.5f, _ => 3f };
        return MathF.Min(1f, MathF.Exp(-3.912f * (dist / through) * (1f / Conditions.VisibilityM - 1f / 20000f)));
    }

    /// <summary>Let go of a grenade: it falls with the drone's own drift, and goes off on impact.</summary>
    public bool Drop()
    {
        if (Kind != DroneKind.Quad || Bombs <= 0 || Dead) return false;
        Bombs--;
        BombsDropped++;
        Log?.Invoke($"[{Clock.Now:0}s] {Operator?.Callsign}'s quad dropped a grenade from {GlobalPosition.Y - GroundAt(GlobalPosition):0} m");
        var p = GlobalPosition + Vector3.Down * 0.25f;
        // Let go off a picture on a screen, from a drone that's never quite still: it lands a metre or so from where
        // it was meant to, more from higher up.
        float h = MathF.Max(1f, GlobalPosition.Y - GroundAt(GlobalPosition));
        float miss = 0.3f + 0.015f * h, fall = MathF.Sqrt(2f * h / 9.81f);
        var dir = Vel * 0.5f + Vector3.Down * 2f + new Vector3((float)GD.Randfn(0.0, miss), 0f, (float)GD.Randfn(0.0, miss)) / fall;
        Ballistics.I.Fire(p, dir.Normalized(), dir.Length(), 0.01f, Operator, 0f, "drone-dropped grenade",
                          ignore: _hit.GetRid(), silent: true, explosive: true, armM: 0f, pen: 20f, vehDamage: 40f, crater: 0.5f, fragR: 7f, power: 0.6f,
                          dropped: true);
        SoundWorld.I?.Emit(Snd.Click, GlobalPosition, -4f);
        // Where it comes down: on from the drift it's let go with (the metre or so of noise in the aim can't be seen).
        var drift = GlobalPosition + (Vel with { Y = 0f }) * (0.5f * fall);
        Drops.RemoveAll(x => x.When < Clock.Now - 2.0);
        Drops.Add((new Vector3(drift.X, GroundAt(drift), drift.Z), Clock.Now + fall));
        return true;
    }

    /// <summary>
    /// Where this drone will come down if it carries on as it's going, and when. A man watching it dive works it
    /// out the same way: the line it's flying along, out to where that meets the ground, a wall, a tree or a
    /// vehicle. False when it's nearly still, or the line runs on past 250 m.
    /// </summary>
    public bool Predict(out Vector3 at, out float eta)
    {
        at = default;
        eta = 0f;
        float speed = Vel.Length();
        if (Dead || speed < 6f) return false;
        var pos = GlobalPosition;
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(pos, pos + Vel / speed * 250f, Layers.World | Layers.Trees | Layers.Vehicles));
        if (hit.Count == 0) return false;
        at = hit["position"].AsVector3();
        eta = pos.DistanceTo(at) / speed;
        return true;
    }

    void Detonate(Vector3 at, Vector3 normal, GodotObject? what)
    {
        if (Dead) return;
        FpvStrikes++;
        Log?.Invoke($"[{Clock.Now:0}s] {Operator?.Callsign}'s {(Kind == DroneKind.FpvAt ? "AT FPV" : "FPV")} hit {(what is Vehicle hv ? hv.Def.Name : what is ICombatant hc ? hc.Callsign : "the ground")}{(TargetV != null ? $" (going for a {TargetV.Def.Name})" : TargetC != null ? $" (going for {TargetC.Callsign})" : "")}, {(Operator != null ? Operator.FeetPos.DistanceTo(at) : 0f):0} m from its pilot");
        var dir = Vel.LengthSquared() > 1f ? Vel.Normalized() : Vector3.Down;
        if (Kind == DroneKind.FpvAt)
        {
            // An RPG warhead: a shaped charge into whatever it hit, from wherever the pilot brought it in (the roof, the rear).
            if (what is Vehicle)
                Ballistics.I.Fire(at - dir * 0.6f, dir, 300f, 0f, Operator, 150f, "AT FPV drone", ignore: _hit.GetRid(), explosive: true, armM: 0f, pen: 500f, vehDamage: 480f, crater: 0.4f, fragR: 5f, power: 1.8f);
            else
                Grenade.Detonate(at + normal * 0.2f, normal, Operator, 5f, 0.6f, 4f, "AT FPV drone", power: 1.8f);
        }
        else if (what is Vehicle)
            // A frag charge against a hull: shreds optics and anything soft, gets through thin armour.
            Ballistics.I.Fire(at - dir * 0.6f, dir, 300f, 0f, Operator, 150f, "FPV drone", ignore: _hit.GetRid(), explosive: true, armM: 0f, pen: 30f, vehDamage: 90f, crater: 0.4f, fragR: 9f, power: 1.5f);
        else
            Grenade.Detonate(at + normal * 0.2f, normal, Operator, 9f, 0.7f, 3f, "FPV drone", power: 1.5f);
        Dead = true;
        QueueFree();
    }

    /// <summary>The pilot sets it off.</summary>
    public void Blow() => Detonate(GlobalPosition, Vector3.Up, null);

    /// <summary>A camera riding on it shouldn't see its own arms and props.</summary>
    public void OwnCamera(Camera3D cam)
    {
        const uint Own = 1u << 19;
        foreach (var n in _visual.GetChildren())
            if (n is MeshInstance3D m) m.Layers = Own;
        cam.CullMask &= ~Own;
    }

    /// <summary>Out of battery, into a wall, or shot: it falls.</summary>
    public void Crash()
    {
        if (Dead) return;
        // An armed FPV coming down still has its impact fuse: most go off where they land.
        _fallFuse = IsFpv && Clock.Now - LaunchedAt > 2.0 && GD.Randf() < 0.85f;
        Dead = true;
        _deadAt = Clock.Now;
        _snd.Stop();
        _shape.Tail?.Stop();
        Vel = Vel * 0.3f;
        SoundWorld.I?.Emit(Snd.Impact, GlobalPosition, -2f);
    }

    /// <summary>A round hit it. A quad goes down; an FPV's charge may go off where it is.</summary>
    public void Hit(ICombatant? by)
    {
        if (Dead) return;
        ShotDown++;
        Log?.Invoke($"[{Clock.Now:0}s] {Operator?.Callsign}'s {(Kind switch { DroneKind.FpvAt => "AT FPV", DroneKind.Fpv => "FPV", _ => "quad" })} shot down by {by?.Callsign ?? "?"}{(by?.Ride != null ? $" ({by.Ride.Def.Name})" : "")}, {(by != null ? by.EyePos.DistanceTo(GlobalPosition) : 0f):0} m, {GlobalPosition.Y - GroundAt(GlobalPosition):0} m up");
        if (IsFpv && GD.Randf() < 0.4f) { Detonate(GlobalPosition, Vector3.Up, null); return; }
        Crash();
    }
}
