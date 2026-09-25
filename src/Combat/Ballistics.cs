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
    public bool Explosive;    // HE: bursts on impact once armed
    public float ArmM;
    public float Pen;         // armour penetration, mm RHA-equivalent
    public float VehDamage;   // hull points taken if it gets through
    public float Crater = 0.65f;
    public int Frags = 45;
    public bool Rocket;
    public MeshInstance3D? Visual;
    public bool Prox;             // bursts when it passes close to an aircraft
    public bool Whistle, Whistled; // a mortar bomb: you hear it coming down
    public AudioStreamPlayer3D? Voice; // its whistle, travelling with it
    public float VoicePitch = 1f, VoiceT;
    public Vehicle? FromVehicle;  // don't proximity-fuse on the aircraft that fired it
    public Vehicle? Homing;       // a guided missile's target
    public bool Decoyed;
    public float MaxLife = 6f;
    public Vector3 DecoyAt;
    public ICombatant? Shooter;
    public string Weapon = "";
    public List<ICombatant>? Passed;
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
    public static readonly Dictionary<string, int> NearBlockTags = new(), ShotTags = new();
    public int LiveCount => _live.Count;

    public override void _EnterTree() => I = this;

    public void Clear() => _live.Clear();

    /// <param name="ignore">A body the ray should pass through (a grenade's own body); otherwise the shooter's.</param>
    public void Fire(Vector3 origin, Vector3 dir, float speed, float drag, ICombatant? shooter, float damage, string weapon,
                     Rid ignore = default, bool silent = false, float intendedDist = 0f, string tag = "", bool explosive = false, float armM = 0f,
                     float pen = -1f, float vehDamage = -1f, float crater = 0.65f, int frags = 45, bool rocket = false,
                     bool prox = false, bool whistle = false, Vehicle? shooterVehicle = null, Vehicle? homing = null)
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
            Frags = frags,
            Rocket = rocket,
            Prox = prox,
            Whistle = whistle,
            FromVehicle = shooterVehicle,
            Homing = homing,
            // Mortar bombs are up for half a minute; missiles and rockets fly a few seconds more than bullets.
            MaxLife = whistle ? 60f : homing != null ? 12f : rocket ? 10f : 6f,
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
        if (ignore.IsValid) p.Exclude.Add(ignore);
        else if (shooter != null) p.Exclude.Add(shooter.BodyRid);
        if (tag != "") ShotTags[tag] = ShotTags.GetValueOrDefault(tag) + 1;
        _live.Add(p);
    }

    public override void _PhysicsProcess(double delta)
    {
        using var _p = Prof.Time("ballistics");
        float dt = (float)delta;
        var space = GetWorld3D().DirectSpaceState;
        var listener = SoundWorld.I.ListenerPos;

        for (int i = _live.Count - 1; i >= 0; i--)
        {
            var p = _live[i];
            if (p.Homing != null) Guide(p, dt);
            float speed = p.Vel.Length();
            var v = p.Vel + (Gravity - p.Vel * (speed * p.Drag)) * dt;
            var next = p.Pos + (p.Vel + v) * (0.5f * dt);

            if (!p.FromListener && !p.Cracked) CheckCrack(p, next, speed, listener);
            if ((p.Prox || p.Homing != null) && ProxBurst(p, next)) { RemoveAt(i); continue; }
            if (p.Whistle && p.Vel.Y < 0f) Whistling(p, listener, dt);
            if (!p.Silent) CheckCombatants(p, next);

            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p.Pos, next, 0xFFFFFFFF, p.Exclude));
            if (hit.Count > 0)
            {
                Impact(p, hit);
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
                if (Engine.GetPhysicsFrames() % 3 == 0) Effects.I.Puff(next - d * 0.4f);
            }
            if (p.Life > p.MaxLife || (speed < 40f && !p.Whistle)) RemoveAt(i);
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
            Grenade.Detonate(at, -seg.Normalized(), p.Shooter, p.Homing != null ? 20 : 6, 0f, p.Homing != null ? 2f : -6f, p.Weapon, v.GetRid());
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

    /// <summary>The sound of the bullet going past the listener's ears.</summary>
    static void CheckCrack(Projectile p, Vector3 next, float speed, Vector3 listener)
    {
        if (speed < SoundWorld.SpeedOfSound * 1.05f) return;
        var seg = next - p.Pos;
        float len2 = seg.LengthSquared();
        if (len2 < 1e-6f) return;
        float t = (listener - p.Pos).Dot(seg) / len2;
        if (t < 0f || t > 1f) return; // closest approach isn't inside this step
        p.Cracked = true;
        var closest = p.Pos + seg * t;
        float d = closest.DistanceTo(listener);
        if (d > (p.Silent ? 4f : 40f)) return;
        SoundWorld.I.Emit(Snd.Crack, closest, p.Silent ? -6f : 0f);
    }

    /// <summary>Suppression: anyone the bullet passes close to knows they're being shot at.</summary>
    static void CheckCombatants(Projectile p, Vector3 next)
    {
        var seg = next - p.Pos;
        float len2 = seg.LengthSquared();
        if (len2 < 1e-6f) return;
        foreach (var c in Combatants.All)
        {
            if (!c.Alive || c == p.Shooter) continue;
            var chest = c.ChestPos;
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

    static void Impact(Projectile p, Godot.Collections.Dictionary hit)
    {
        var pos = hit["position"].AsVector3();
        var normal = hit["normal"].AsVector3();
        var collider = hit["collider"].AsGodotObject();

        if (collider is Vehicle veh)
        {
            veh.TakeProjectile(p, pos, normal);
            if (p.Explosive && pos.DistanceTo(p.Origin) >= p.ArmM)
                Grenade.Detonate(pos + normal * 0.1f, normal, p.Shooter, p.Frags / 3, 0f, p.Frags > 50 ? 3f : -3f, p.Weapon);
            return;
        }
        if (p.Explosive)
        {
            if (pos.DistanceTo(p.Origin) >= p.ArmM)
                Grenade.Detonate(pos + normal * 0.05f, normal, p.Shooter, p.Frags, p.Crater, p.Whistle ? 0f : p.Frags > 50 ? 4f : -3f, p.Weapon, power: p.Whistle ? 4f : 1f);
            else
            {
                // Not armed yet: a dud that thumps into whatever it hit.
                Effects.I.Impact(pos, normal, false);
                SoundWorld.I.Emit(Snd.Impact, pos, 2f);
                if (collider is ICombatant v && v.Alive)
                    v.TakeHit(new HitInfo { Shooter = p.Shooter, Point = pos, Dir = p.Vel.Normalized(), Damage = 60f, Zone = Combatants.ZoneFor(v, pos), Distance = pos.DistanceTo(p.Origin), Weapon = p.Weapon });
            }
            return;
        }

        if (collider is ICombatant victim && victim.Alive)
        {
            float speed = p.Vel.Length();
            var zone = Combatants.ZoneFor(victim, pos);
            float dmg = p.Damage * MathF.Pow(speed / p.MuzzleSpeed, 1.5f) * Combatants.ZoneMultiplier(zone);
            victim.TakeHit(new HitInfo
            {
                Shooter = p.Shooter, Point = pos, Dir = p.Vel.Normalized(), Damage = dmg,
                Zone = zone, Distance = pos.DistanceTo(p.Origin), Weapon = p.Weapon,
            });
            Effects.I.Blood(pos, -p.Vel.Normalized());
            if (!p.Silent) SoundWorld.I.Emit(Snd.Impact, pos, -6f);
        }
        else if (collider is SteelTarget steel) steel.Hit(pos, p.Vel);
        else
        {
            Effects.I.Impact(pos, normal, p.Silent);
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

        if (p.Silent) return;
        foreach (var c in Combatants.All)
        {
            if (!c.Alive || c == p.Shooter || c == collider) continue;
            float d = pos.DistanceTo(c.ChestPos);
            if (d < 4f) c.OnNearMiss(d + 1f, p.Origin);
        }
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
