using Godot;

namespace Ridgeline;

public sealed class Projectile
{
    public Vector3 Pos, Vel, Origin;
    public float Drag;        // quadratic drag coefficient k: dv/dt = -k|v|v
    public float Life, Damage, MuzzleSpeed;
    public float IntendedDist;
    public string Tag = "";   // what the shooter was doing (diagnostics)  // how far away the shooter's target was (stats: blocked vs. wide)
    public bool FromListener; // fired from where the listener is: don't crack past our own ears
    public bool Silent;       // fragments: no impact sounds or near-miss spam, or it's a wall of noise
    public bool Cracked;
    public bool HeavyCrack;   // .50 cal and up (HMG, autocannon, tank gun, a supersonic rocket): a heavier crack, heard further
    public bool Explosive;    // HE: bursts on impact once armed
    public float ArmM;
    public float Pen;         // armour penetration, mm RHA-equivalent
    public float VehDamage;   // hull points taken if it gets through
    public float Crater = 0.65f;
    public float FragR = 7f, Power = 1f; // an explosive round's fragment reach and charge (see WeaponDef)
    public uint Mask = 0xFFFFFFFF;       // what it can hit (fragments thrown only for the dust they raise hit only the ground)
    public bool Rocket;
    public MeshInstance3D? Visual;
    public bool Prox;             // bursts when it passes close to an aircraft
    public bool Whistle, Whistled; // a mortar bomb: you hear it coming down
    /// <summary>Let go from a drone: it falls from standing still, so it's no spent round however slow it's going.</summary>
    public bool Dropped;
    public AudioStreamPlayer3D? Voice; // its whistle, travelling with it
    public float VoicePitch = 1f, VoiceT;
    public Vehicle? FromVehicle;  // don't proximity-fuse on the aircraft that fired it
    public Vehicle? Homing;       // a guided missile's target
    /// <summary>An anti-tank missile steered from its aircraft (laser, radio): it needs that aircraft to keep the target in sight.</summary>
    public Vehicle? GuidedBy;
    public double GuideCheckAt;
    public bool Decoyed;
    public float MaxLife = 6f;
    public Vector3 DecoyAt;
    public ICombatant? Shooter;
    public string Weapon = "";
    public List<ICombatant>? Passed;
    /// <summary>Set by Impact when the round went through what it hit (see Penetration): where it came out.</summary>
    public Vector3? ExitAt;
    public readonly Godot.Collections.Array<Rid> Exclude = new();
}

/// <summary>
/// Every bullet is a real projectile with travel time, gravity and drag, swept
/// with a ray each physics tick. Supersonic bullets that pass near the listener
/// emit a crack from the point of closest approach; SoundWorld's propagation
/// then makes it arrive before the (slower, farther) muzzle report. Bullets that
/// pass near any combatant suppress them, and bullets that hit one hurt them.
/// </summary>
public partial class Ballistics : Node3D
{
    public static Ballistics I { get; private set; } = null!;
    public static readonly Vector3 Gravity = new(0f, -9.81f, 0f);

    readonly List<Projectile> _live = new();
    /// <summary>Mortar bombs in the last seconds of their fall: where each will land and when (its whistle is what gives it away).</summary>
    public static readonly List<(Vector3 At, double When)> Incoming = new();
    public static readonly Dictionary<string, int> NearBlockTags = new(), ShotTags = new();
    public int LiveCount => _live.Count;

    public override void _EnterTree()
    {
        I = this;
        Incoming.Clear(); // a new match: the clock starts again
    }

    public void Clear() { _live.Clear(); Incoming.Clear(); }

    /// <param name="ignore">A body the ray should pass through (a grenade's own body); otherwise the shooter's.</param>
    public void Fire(Vector3 origin, Vector3 dir, float speed, float drag, ICombatant? shooter, float damage, string weapon,
                     Rid ignore = default, bool silent = false, float intendedDist = 0f, string tag = "", bool explosive = false, float armM = 0f,
                     float pen = -1f, float vehDamage = -1f, float crater = 0.65f, float fragR = 7f, float power = 1f, bool rocket = false,
                     bool prox = false, bool whistle = false, Vehicle? shooterVehicle = null, Vehicle? homing = null, bool heavyCrack = false, uint mask = 0xFFFFFFFF,
                     bool dropped = false, Vehicle? guidedBy = null, bool burst = false)
    {
        var p = new Projectile
        {
            Pos = origin, Origin = origin, Vel = dir.Normalized() * speed, Drag = drag,
            Shooter = shooter, Damage = damage, MuzzleSpeed = speed, Weapon = weapon, Silent = silent,
            FromListener = origin.DistanceTo(SoundWorld.I.ListenerPos) < 2.5f,
            IntendedDist = intendedDist,
            Tag = tag,
            Explosive = explosive,
            ArmM = armM,
            // Small arms: a few mm of steel at most. A 40 mm grenade's shaped charge does a bit better.
            Pen = pen >= 0f ? pen : explosive ? 50f : damage * 0.12f,
            VehDamage = vehDamage >= 0f ? vehDamage : explosive ? 30f : damage * 0.02f,
            Crater = crater,
            FragR = fragR,
            Power = power,
            Mask = mask,
            Rocket = rocket,
            Prox = prox,
            Whistle = whistle,
            Dropped = dropped,
            GuidedBy = guidedBy,
            FromVehicle = shooterVehicle,
            Homing = homing,
            HeavyCrack = heavyCrack,
            // Mortar bombs are up for half a minute; missiles and rockets fly a few seconds more than bullets.
            MaxLife = whistle ? 60f : homing != null ? (homing.Def.Air ? 12f : 22f) : rocket ? 10f : 6f,
        };
        if (shooterVehicle != null) p.Exclude.Add(shooterVehicle.GetRid());
        homing?.MissileLaunched();
        if (rocket)
        {
            p.Visual = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.05f, Height = 0.6f, RadialSegments = 8 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.2f, 0.22f, 0.15f), EmissionEnabled = true, Emission = new Color(1f, 0.6f, 0.2f), EmissionEnergyMultiplier = 0.3f } };
            AddChild(p.Visual);
            p.Visual.GlobalPosition = origin;
        }
        if (dropped)
        {
            // A grenade falling from a drone: small, dark, and you might just see it coming.
            p.Visual = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 0.11f, RadialSegments = 8 },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.2f, 0.14f) } };
            AddChild(p.Visual);
            p.Visual.GlobalPosition = origin;
        }
        if (ignore.IsValid) p.Exclude.Add(ignore);
        else if (!burst && (shooter is not GodotObject gone || GodotObject.IsInstanceValid(gone))) { if (shooter != null) p.Exclude.Add(shooter.BodyRid); }
        if (tag != "") ShotTags[tag] = ShotTags.GetValueOrDefault(tag) + 1;
        Prof.Count(silent ? "proj:fragments" : "proj:rounds");
        _live.Add(p);
    }

    /// <summary>Everyone on their feet and where their chest is, read once per tick (nobody moves while the rounds are being stepped).</summary>
    static readonly List<(ICombatant Who, Vector3 Chest)> _standing = new();

    public override void _PhysicsProcess(double delta)
    {
        using var _p = Prof.Time("ballistics");
        float dt = (float)delta;
        var space = GetWorld3D().DirectSpaceState;
        var listener = SoundWorld.I.ListenerPos;
        _standing.Clear();
        if (_live.Count > 0)
            foreach (var c in Combatants.All)
                if (c.Alive) _standing.Add((c, c.ChestPos));

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var p = _live[i];
            if (p.Homing != null) Guide(p, dt);
            float speed = p.Vel.Length();
            var v = p.Vel + (Gravity - p.Vel * (speed * p.Drag)) * dt;
            var next = p.Pos + (p.Vel + v) * (0.5f * dt);

            if (!p.FromListener && !p.Cracked) CheckCrack(p, next, speed, listener, dt);
            if ((p.Prox || p.Homing != null) && ProxBurst(p, next)) { RemoveAt(i); continue; }
            if (p.Whistle && p.Vel.Y < 0f) Whistling(p, listener, dt);
            if (!p.Silent) CheckCombatants(p, next);

            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p.Pos, next, p.Mask, p.Exclude));
            // A round that only grazed past someone (beside the head) carries on from there, him left out; one that
            // went through a wall, a door or a car carries on from the far side, slower and a little off line.
            bool stopped = false;
            for (int pass = 0; pass < 4 && hit.Count > 0; pass++)
            {
                var at = hit["position"].AsVector3();
                if (Impact(p, hit)) { stopped = true; break; }
                if (p.ExitAt is Vector3 exit)
                {
                    p.ExitAt = null;
                    float rest = MathF.Max(0.05f, next.DistanceTo(at) - exit.DistanceTo(at));
                    at = exit;
                    next = exit + p.Vel.Normalized() * rest;
                    v = p.Vel;
                }
                hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(at, next, p.Mask, p.Exclude));
            }
            if (stopped)
            {
                RemoveAt(i);
                continue;
            }

            p.Pos = next;
            p.Vel = v;
            p.Life += dt;
            if (p.Visual != null)
            {
                var d = v.Normalized();
                // The cylinder's axis is Y: turn it to lie along the flight path.
                var look = Basis.LookingAt(d, MathF.Abs(d.Y) > 0.95f ? Vector3.Right : Vector3.Up);
                p.Visual.GlobalTransform = new Transform3D(look * new Basis(Vector3.Right, -Mathf.Pi / 2f), next);
                if (p.Rocket && Engine.GetPhysicsFrames() % 3 == 0) Effects.I.Puff(next - d * 0.4f);
            }
            // A bullet slowed to a crawl is spent. (That rule also used to delete every grenade a quad let go, on
            // its first step, falling at 2 m/s: not one ever went off.)
            if (p.Life > p.MaxLife || (speed < 40f && !p.Whistle && !p.Dropped)) RemoveAt(i);
        }
    }

    void RemoveAt(int i)
    {
        _live[i].Visual?.QueueFree();
        _live[i].Voice?.QueueFree();
        _live[i] = _live[^1];
        _live.RemoveAt(_live.Count - 1);
    }

    /// <summary>
    /// A guided missile steers toward where its target will be (a lead pursuit), within
    /// its turn rate. If the target has flares out, it may lose it for them.
    /// </summary>
    static void Guide(Projectile p, float dt)
    {
        var tgt = p.Homing!;
        if (!tgt.Def.Air) { GuideGround(p, dt); return; }
        // Flares out: about a 60% chance over their three seconds that the seeker goes for them.
        if (!p.Decoyed && tgt.FlaresActive && GD.Randf() < 0.3f * dt)
        {
            p.Decoyed = true;
            p.DecoyAt = tgt.Center + tgt.Velocity3 * 0.5f + Vector3.Down * 15f;
        }
        if (tgt.Destroyed && !p.Decoyed) { p.Decoyed = true; p.DecoyAt = tgt.Center; }
        float speed = p.Vel.Length();
        var aimAt = p.Decoyed ? p.DecoyAt : tgt.Center + tgt.Velocity3 * (p.Pos.DistanceTo(tgt.Center) / MathF.Max(speed, 1f));
        var want = (aimAt - p.Pos).Normalized();
        var cur = p.Vel / MathF.Max(speed, 1f);
        float maxTurn = Mathf.DegToRad(45f) * dt;
        float ang = cur.AngleTo(want);
        var dir = ang <= maxTurn ? want : cur.Slerp(want, maxTurn / ang);
        // The motor keeps it going: hold speed against drag and gravity.
        p.Vel = dir * MathF.Max(speed, p.MuzzleSpeed * 0.95f) - Gravity * dt;
    }

    /// <summary>
    /// An anti-tank missile on a vehicle: led by where it's going, it climbs and then dives onto the roof. One steered
    /// from its aircraft (a laser, a radio link) needs that aircraft to keep the target in sight all the way: lose it
    /// (the aircraft ducks or is hit, the target goes behind a hill) and the missile flies on unguided.
    /// </summary>
    static void GuideGround(Projectile p, float dt)
    {
        var tgt = p.Homing!;
        if (!GodotObject.IsInstanceValid(tgt) || tgt.Destroyed) { p.Homing = null; return; }
        if (p.GuidedBy != null && Clock.Now >= p.GuideCheckAt)
        {
            p.GuideCheckAt = Clock.Now + 0.2;
            var by = p.GuidedBy;
            bool lit = GodotObject.IsInstanceValid(by) && !by.Destroyed && by.Crewed
                       && by.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(by.Center, tgt.TopPoint, Layers.World)).Count == 0;
            if (!lit) { p.Homing = null; Prof.Count(GodotObject.IsInstanceValid(by) && !by.Destroyed && by.Crewed ? "missile:guidance lost (out of sight)" : "missile:guidance lost (aircraft down)"); return; }
        }
        float speed = p.Vel.Length();
        var goal = tgt.Center + tgt.Velocity3 * (p.Pos.DistanceTo(tgt.Center) / MathF.Max(speed, 1f));
        float d = ((goal - p.Pos) with { Y = 0f }).Length();
        // Aim above it while far off, so it comes down on it from above.
        var aim = goal + Vector3.Up * Mathf.Clamp((d - 250f) * 0.18f, 0f, 160f);
        var want = (aim - p.Pos).Normalized();
        var cur = p.Vel / MathF.Max(speed, 1f);
        float maxTurn = Mathf.DegToRad(30f) * dt;
        float ang = cur.AngleTo(want);
        var dir = ang <= maxTurn ? want : cur.Slerp(want, maxTurn / ang);
        p.Vel = dir * MathF.Max(speed, p.MuzzleSpeed * 0.95f) - Gravity * dt;
    }

    /// <summary>A proximity fuse (or a missile) passing close to an enemy aircraft: burst there.</summary>
    bool ProxBurst(Projectile p, Vector3 next)
    {
        if (p.Life < 0.15f) return false;
        foreach (var v in Vehicle.All)
        {
            if (!v.Def.Air || v.Destroyed || v == p.FromVehicle || v.Landed) continue;
            if (p.Homing != null && v != p.Homing) continue;
            var seg = next - p.Pos;
            float t = Mathf.Clamp((v.Center - p.Pos).Dot(seg) / MathF.Max(seg.LengthSquared(), 1e-6f), 0f, 1f);
            var at = p.Pos + seg * t;
            float d = at.DistanceTo(v.Center);
            float fuse = p.Homing != null ? 7f : 5f;
            if (d > fuse + v.Def.Hull.Z * 0.3f) continue;
            // Blast and fragments into the airframe: worse the closer it went off.
            v.Damage(p.VehDamage * (1f - d / (fuse + v.Def.Hull.Z * 0.3f) * 0.6f), p.Shooter);
            // The crew saw the tracers come up: where the gun is goes out on the radio (other aircraft keep clear of it).
            if (p.FromVehicle is { Destroyed: false } aa && v.Occupants.FirstOrDefault(o => o is { Alive: true }) is { } crew)
                Radio.Report(crew, RadioKind.Armor, aa.Center, aa);
            Grenade.Detonate(at, -seg.Normalized(), p.Shooter, p.FragR, 0f, p.Homing != null ? 2f : -6f, p.Weapon, v.GetRid(), power: p.Power);
            return true;
        }
        return false;
    }

    static AudioStreamWav? _whistleLoop;

    /// <summary>
    /// A bomb coming down whistles (air over its fins), and what you hear depends on where
    /// it's going, the way it does for real:
    /// - the note is Doppler-shifted by how fast it's closing on you. One landing off to the
    ///   side is closing less and less as it drops past, so its note falls, the classic
    ///   falling whistle. One coming straight at you closes at full speed the whole way down:
    ///   the note stays high and steady and just gets louder. If it isn't falling, get down.
    /// - it's louder the closer the bomb is (it's a sound source travelling with the bomb),
    ///   and it's only heard in the last few seconds of the fall, by people it lands within
    ///   ~250 m of: it's a thin sound, lost under everything else further off.
    /// </summary>
    void Whistling(Projectile p, Vector3 listener, float dt)
    {
        if (p.Voice == null)
        {
            if (p.Whistled) return;
            // Start within the last ~4 s of the fall, if it's coming down anywhere near us.
            float drop = -p.Vel.Y;
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(p.Pos, p.Pos + Vector3.Down * 900f, Layers.World));
            if (hit.Count == 0) return;
            var ground = hit["position"].AsVector3();
            float tFall = (p.Pos.Y - ground.Y) / MathF.Max(drop, 1f);
            if (tFall > 4.2f) return;
            p.Whistled = true;
            var land = ground + (p.Vel with { Y = 0f }) * tFall;
            Incoming.RemoveAll(x => x.When < Clock.Now - 2.0);
            Incoming.Add((land, Clock.Now + tFall));
            if (land.DistanceTo(listener) > 250f) return;
            _whistleLoop ??= SoundSynth.WhistleLoop();
            p.Voice = new AudioStreamPlayer3D
            {
                // Inverse-square: it thins out fast with distance, and nothing at all past 450 m.
                Stream = _whistleLoop, Bus = "World", UnitSize = 25f, MaxDistance = 450f, MaxDb = 4f,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseSquareDistance,
                AttenuationFilterCutoffHz = 9000f, AttenuationFilterDb = -8f, DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
            };
            AddChild(p.Voice);
            p.Voice.GlobalPosition = p.Pos;
            p.VoicePitch = 0.9f + (float)Random.Shared.NextDouble() * 0.2f;
            p.Voice.Play((float)Random.Shared.NextDouble());
        }
        p.VoiceT += dt;
        p.Voice.GlobalPosition = p.Pos;
        var to = listener - p.Pos;
        float closing = p.Vel.Dot(to.Normalized());           // m/s toward us
        // Doppler, softened a little so the note stays in a sensible range for a ~200 m/s bomb.
        float doppler = 1f / MathF.Max(0.35f, 1f - 0.6f * closing / SoundWorld.SpeedOfSound);
        p.Voice.PitchScale = Mathf.Clamp(p.VoicePitch * 0.72f * doppler, 0.3f, 3f);
        // Fades in as the fall steepens; the airflow noise is stronger the faster it falls.
        p.Voice.VolumeDb = -14f + Mathf.Clamp(p.VoiceT / 0.8f, 0f, 1f) * 12f + Mathf.Clamp((p.Vel.Length() - 120f) / 80f, 0f, 1f) * 3f;
    }

    /// <summary>
    /// The crack of a supersonic bullet going past the listener. Its shock leaves it all along its
    /// path as a Mach cone, and the part that reaches the listener left from a point upstream of
    /// the closest approach, b/sqrt(M²-1) back toward the shooter (b the miss distance), when
    /// the bullet was there. So it arrives b·sqrt(M²-1)/(M·c) after the bullet passes, from a
    /// direction tilted toward the shooter by the Mach angle (more so as the bullet slows); and a
    /// listener beside or behind the gun, outside the cone, hears no crack at all. Louder and
    /// longer for a bigger bullet, a little louder for a faster one (Whitham). Heard out to
    /// ~150 m for rifle rounds, where it's still a sharp snap over a distant report.
    /// </summary>
    static void CheckCrack(Projectile p, Vector3 next, float speed, Vector3 listener, float dt)
    {
        float c = SoundWorld.SpeedOfSound;
        if (speed < c * 1.05f) return;
        var seg = next - p.Pos;
        float len2 = seg.LengthSquared();
        if (len2 < 1e-6f) return;
        float t = (listener - p.Pos).Dot(seg) / len2;
        if (t < 0f || t > 1f) return; // closest approach isn't inside this step
        p.Cracked = true;
        var dir = seg / MathF.Sqrt(len2);
        var closest = p.Pos + seg * t;
        float b = MathF.Max(closest.DistanceTo(listener), 0.3f);
        bool heavy = p.HeavyCrack;
        if (b > (p.Silent ? 4f : heavy ? 250f : 150f)) return;
        float m = speed / c;
        float back = b / MathF.Sqrt(m * m - 1f);
        // From behind the muzzle there's no cone to hear: the listener is beside or behind the gun.
        if ((closest - p.Origin).Dot(dir) - back < 0.5f) return;
        // When the bullet was there: it reaches the closest point t of this step from now.
        float ago = back / speed - t * dt;
        // Whitham's peak goes as (M²-1)^(1/8) / b^(3/4): re Mach 2.5, and undoing the extra spreading
        // Play applies over the longer path from the emission point (b·M/sqrt(M²-1), 1.09 b at Mach 2.5).
        float gain = 2.5f * MathF.Log10((m * m - 1f) / 5.25f) + 15f * MathF.Log10(m / MathF.Sqrt(m * m - 1f) / 1.091f);
        // Grenade fragments: a dozen pass at once, so keep them low and dry, or they're a wall of noise.
        SoundWorld.I.Emit(heavy ? Snd.CrackHeavy : Snd.Crack, closest - dir * back, p.Silent ? gain - 15f : gain, ago: ago, dry: p.Silent);
    }

    /// <summary>Suppression: anyone the bullet passes close to knows they're being shot at.</summary>
    static void CheckCombatants(Projectile p, Vector3 next)
    {
        var seg = next - p.Pos;
        float len2 = seg.LengthSquared();
        if (len2 < 1e-6f) return;
        foreach (var (c, chest) in _standing)
        {
            if (c == p.Shooter || !c.Alive) continue; // (someone may have gone down to an earlier round this tick)
            // A bomb leaving the tube isn't a near miss for the crew kneeling by it.
            if (p.Whistle && (chest - p.Origin).LengthSquared() < 9f) continue;
            float t = (chest - p.Pos).Dot(seg) / len2;
            if (t < 0f || t > 1f) continue;
            float d = (p.Pos + seg * t).DistanceTo(chest);
            if (d > 6f) continue;
            p.Passed ??= new List<ICombatant>();
            if (p.Passed.Contains(c)) continue;
            p.Passed.Add(c);
            c.OnNearMiss(d, p.Origin);
        }
    }

    /// <summary>What a round does where it strikes. False if it didn't stop there (it went on past).</summary>
    static bool Impact(Projectile p, Godot.Collections.Dictionary hit)
    {
        var pos = hit["position"].AsVector3();
        var normal = hit["normal"].AsVector3();
        var collider = hit["collider"].AsGodotObject();

        if (collider is Node { } dn && dn.GetParent() is Drone drone)
        {
            drone.Hit(p.Shooter);
            return true;
        }
        if (collider is Vehicle veh)
        {
            veh.TakeProjectile(p, pos, normal);
            if (p.Explosive && pos.DistanceTo(p.Origin) >= p.ArmM)
                Grenade.Detonate(pos + normal * 0.1f, normal, p.Shooter, p.FragR * 0.6f, 0f, p.Power >= 6f ? 3f : -3f, p.Weapon, power: p.Power);
            return true;
        }
        if (p.Explosive)
        {
            if (pos.DistanceTo(p.Origin) >= p.ArmM)
                Grenade.Detonate(pos + normal * 0.05f, normal, p.Shooter, p.FragR, p.Crater, p.Whistle ? 0f : p.Power >= 6f ? 4f : -3f, p.Weapon, power: p.Power, indirect: p.Whistle);
            else
            {
                // Not armed yet: a dud that thumps into whatever it hit.
                Effects.I.Impact(pos, normal, false, Surfaces.Of(collider, hit["shape"].AsInt32(), pos, normal));
                SoundWorld.I.Emit(Snd.Impact, pos, 2f);
                if (collider is ICombatant v && !v.Dead && Combatants.ZoneFor(v, pos) is var dz)
                    v.TakeHit(new HitInfo { Shooter = p.Shooter, Point = pos, Dir = p.Vel.Normalized(), Damage = 60f * Combatants.ZoneMultiplier(dz), Zone = dz, Distance = pos.DistanceTo(p.Origin), Weapon = p.Weapon, From = p.Origin });
            }
            return true;
        }

        // Down counts: a stray round or a fragment finds a wounded man on the ground as easily as anyone.
        if (collider is ICombatant victim && !victim.Dead)
        {
            float speed = p.Vel.Length();
            var zone = Combatants.ZoneFor(victim, pos);
            // The body's capsule is shoulder-wide all the way to the crown; the head in it isn't. At head
            // height a round has to pass within a head's width of it: beside it below the chin it's the
            // shoulder, beside it higher up it's a miss (a very near one) and it flies on.
            if (zone == HitZone.Head && !Combatants.OnHead(victim, pos, p.Vel.Normalized()))
            {
                if (pos.Y - victim.FeetPos.Y < victim.BodyHeight - 0.2f) zone = HitZone.Torso;
                else
                {
                    p.Exclude.Add(victim.BodyRid);
                    p.Passed ??= new List<ICombatant>();
                    if (!p.Silent && victim.Alive && !p.Passed.Contains(victim))
                    {
                        p.Passed.Add(victim);
                        victim.OnNearMiss(0.2f, p.Origin);
                    }
                    return false;
                }
            }
            if (!p.Silent) Prof.Count($"hit:{zone}");
            Telemetry.Hit(victim, p.Shooter, pos, zone.ToString(), p.Weapon, pos.DistanceTo(p.Origin), p.Silent);
            float dmg = p.Damage * MathF.Pow(speed / p.MuzzleSpeed, 1.5f) * Combatants.ZoneMultiplier(zone);
            victim.TakeHit(new HitInfo
            {
                Shooter = p.Shooter, Point = pos, Dir = p.Vel.Normalized(), Damage = dmg,
                Zone = zone, Distance = pos.DistanceTo(p.Origin), Weapon = p.Weapon, From = p.Origin,
            });
            Effects.I.Blood(pos, -p.Vel.Normalized());
            if (!p.Silent) SoundWorld.I.Emit(Snd.HitFlesh, pos, -6f);
        }
        else if (collider is SteelTarget steel) steel.Hit(pos, p.Vel);
        else
        {
            var surface = Surfaces.Of(collider, hit["shape"].AsInt32(), pos, normal);
            Effects.I.Impact(pos, normal, p.Silent, surface);
            if (Penetration.Through(p, hit, out var exit, out var through, out var what))
            {
                // On through it: a puff of whatever it is on the far side, and on it goes (the sweep picks it up there).
                Effects.I.Impact(exit, through.Normalized(), true, surface);
                if (!p.Silent)
                {
                    Prof.Count($"pen:{what}");
                    foreach (var (c, chest) in _standing)
                    {
                        if (c == p.Shooter || !c.Alive) continue;
                        float dm = pos.DistanceTo(chest);
                        if (dm < 4f) c.OnNearMiss(dm + 1f, p.Origin);
                    }
                }
                p.ExitAt = exit;
                p.Vel = through;
                return false;
            }
            if (!p.Silent) Ricochet(p, pos, normal, surface is Surface.Rock or Surface.Stone or Surface.Metal);
            if (p.Shooter is Bot bot && p.IntendedDist > 0f)
            {
                float at = pos.DistanceTo(p.Origin);
                if (at < p.IntendedDist * 0.4f)
                {
                    bot.ShotsBlockedNear++; // into our own side's cover: a mistake
                    NearBlockTags[p.Tag] = NearBlockTags.GetValueOrDefault(p.Tag) + 1;
                }
                if (at < p.IntendedDist - 1.5f) bot.ShotsBlocked++;
                else bot.ShotsWide++;
            }
        }

        if (p.Silent) return true;
        foreach (var (c, chest) in _standing)
        {
            if (c == p.Shooter || c == collider || !c.Alive) continue;
            float d = pos.DistanceTo(chest);
            if (d < 4f) c.OnNearMiss(d + 1f, p.Origin);
        }
        return true;
    }

    /// <summary>
    /// A round striking something hard at a shallow angle often glances off and tumbles away,
    /// whirring: heard from a little along the way it went.
    /// </summary>
    public static void Ricochet(Projectile p, Vector3 pos, Vector3 normal, bool hard)
    {
        if (!hard || p.Explosive) return;
        var d = p.Vel.Normalized();
        float graze = MathF.Abs(d.Dot(normal)); // sine of the angle to the surface
        if (graze > 0.34f || Random.Shared.NextDouble() > 0.6 * (1f - graze / 0.34f) + 0.2) return;
        var away = (d - 2f * d.Dot(normal) * normal).Normalized();
        SoundWorld.I.Emit(Snd.Ricochet, pos + away * 1.5f, Mathf.Clamp((p.Vel.Length() - 300f) / 100f, -6f, 2f));
    }

    /// <summary>
    /// A fire-control solution: the direction to launch a round so that it passes through <paramref name="delta"/>
    /// (the point relative to the muzzle), whatever the angle up or down, and how long it takes to get there. The
    /// round is flown the way a live one is (the same drag, gravity and step as the projectile update), so the drag
    /// that slows it and the drop that bends it are both accounted for, once. (An anti-aircraft gun used to lead by
    /// distance over muzzle speed, which a 35 mm round slowed by drag doesn't make, and to add the drop on top of the
    /// gun's own superelevation: its bursts went off metres high and short of the aircraft.)
    /// </summary>
    public static Vector3 Launch(float speed, float drag, Vector3 delta, out float tof)
    {
        float dist = delta.Length();
        tof = 0f;
        if (dist < 1f || speed <= 0f) return dist < 1e-3f ? Vector3.Forward : delta / dist;
        // In the vertical plane through the target: x along the ground towards it, y up.
        var flat = delta with { Y = 0f };
        float fx = flat.Length();
        var h = fx > 1e-3f ? flat / fx : Vector3.Forward;
        var los = new Vector2(fx, delta.Y) / dist;
        float angle = MathF.Atan2(delta.Y, fx);
        const float dt = 1f / 60f;
        var g = new Vector2(0f, -9.81f);
        for (int iter = 0; iter < 3; iter++)
        {
            var pos = Vector2.Zero;
            var vel = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed;
            float t = 0f, along = 0f;
            var crossAt = pos;
            for (int k = 0; k < 900; k++)
            {
                float sp = vel.Length();
                var nv = vel + (g - vel * (sp * drag)) * dt;
                var next = pos + (vel + nv) * (0.5f * dt);
                float nextAlong = next.Dot(los);
                if (nextAlong >= dist)
                {
                    float f = (dist - along) / MathF.Max(nextAlong - along, 1e-4f);
                    crossAt = pos.Lerp(next, f);
                    t += dt * f;
                    break;
                }
                pos = next; vel = nv; along = nextAlong; t += dt;
                crossAt = pos;
            }
            tof = t;
            // How far off the line to the target it went, square to it (+ = above): lay that much lower.
            float miss = los.X * crossAt.Y - los.Y * crossAt.X;
            angle -= MathF.Atan2(miss, dist);
        }
        return (h * MathF.Cos(angle) + Vector3.Up * MathF.Sin(angle)).Normalized();
    }

    /// <summary>Launch angle (radians, above the sight line) that puts the bullet back on the line at <paramref name="range"/>.</summary>
    public static float ZeroAngle(float speed, float drag, float range)
    {
        float angle = 0f;
        const float dt = 1f / 480f;
        for (int iter = 0; iter < 3; iter++)
        {
            var pos = Vector2.Zero;
            var vel = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed;
            for (int k = 0; k < 20000 && pos.X < range; k++)
            {
                float sp = vel.Length();
                vel += (new Vector2(0f, -9.81f) - vel * (sp * drag)) * dt;
                pos += vel * dt;
            }
            angle += MathF.Atan2(-pos.Y, range);
        }
        return angle;
    }
}
