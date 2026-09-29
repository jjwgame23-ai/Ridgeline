using Godot;

namespace Ridgeline;

/// <summary>
/// A bot's decisions while it's in a vehicle seat.
/// - Gunner: pick a target it can see (enemy vehicles first if the gun can hurt them,
///   otherwise people), lay the gun on it with a human's settling error, pick the right
///   round (AP for armour, HE or the coax for infantry) and fire.
/// - Driver: take the vehicle where its squad has been sent (VehicleDriver).
/// - Passenger: ride along.
/// </summary>
public sealed class CrewBrain
{
    readonly Bot _b;
    readonly RandomNumberGenerator _rng = new();
    double _nextPick, _burstUntil, _nextShot;
    // An air defence gun's radar: the aircraft it's locked on to, since when, and when it last had it in sight.
    Vehicle? _lockOn;
    double _lockSince, _lockSeen;
    /// <summary>How long an air defence gun's radar takes to lock on and its fire control to work out a solution.</summary>
    const double RadarLockTime = 4.0;
    Vector3 _err;
    float _settle;
    public object? Target { get; private set; }
    public string Note { get; private set; } = "";

    public CrewBrain(Bot b)
    {
        _b = b;
        _rng.Randomize();
    }

    public void Tick(float dt)
    {
        var v = _b.Ride;
        if (v == null || v.Destroyed) return;
        var seat = v.Def.Seats[_b.SeatIdx];
        if (seat.Role == SeatRole.Gunner && seat.Turret >= 0)
        {
            if (v.Def.Turrets[seat.Turret].Indirect) Mortar(v, seat.Turret);
            else Gun(v, seat.Turret, dt);
        }
        else if (seat.Role == SeatRole.Driver)
        {
            if (v.Def.Air) HeliPilot.Tick(_b, v, dt);
            else VehicleDriver.Tick(_b, v, dt);
        }
    }

    public float RandSign() => _rng.Randf() < 0.5f ? -1f : 1f;

    double _nextMission, _nextRound;
    public static int MortarRounds;
    int _roundsLeft;
    Vector3 _mission, _lay;
    public static int MortarLoaded;

    /// <summary>
    /// A mortar: find a fresh cluster of enemy sightings in range (not near our own
    /// people), lay on it and put five or six bombs into it with a little spread; then
    /// wait for the next target. With the assistant gunner at the tube hanging the bombs
    /// the gunner only has to check his lay between rounds; alone, he loads each one
    /// himself and re-lays after it, at about half the rate.
    /// </summary>
    void Mortar(Vehicle v, int ti)
    {
        var t = v.Turrets[ti];
        double now = Clock.Now;
        if (_roundsLeft <= 0)
        {
            if (now < _nextMission) { Note = "waiting for a fire mission"; return; }
            _nextMission = now + 4.0;
            var target = Intel.Cluster(_b.Team, v.GlobalPosition, 150f, 3200f);
            if (target is not Vector3 tg) { Note = "no targets"; return; }
            _mission = tg;
            _lay = _mission + Sheaf();
            _roundsLeft = _rng.RandiRange(5, 7);
            Comms.Say(_b, $"Fire mission, {Comms.Bearing(v.GlobalPosition, tg)}, {v.GlobalPosition.DistanceTo(tg):0} meters. Rounds out!");
        }
        if (now < _nextRound) return;
        // Check fire: our own people have moved into the target area since the mission was called (an
        // assault going in, a squad passing through). Every round is cleared before it goes, not just the first.
        if (Combatants.All.Any(f => f.Team == _b.Team && !f.Dead && f.FeetPos.DistanceTo(_mission) < 70f))
        {
            _roundsLeft = 0;
            _nextMission = now + 10.0;
            Note = "check fire: friendlies in the target area";
            Comms.Say(_b, "Check fire, check fire! Friendlies in the target area.");
            return;
        }
        // Each bomb laid a little off the last: the sheaf spreads them over the target. The point is picked once per
        // bomb and the tube laid on it. (It used to be re-picked every tick, so the lay chased a point that jumped
        // about by 20 m, and the bomb went wherever the tube happened to be when it counted as laid.)
        t.AimAt = _lay;
        bool loader = Loader(v) != null;
        if (t.LaidAt == _lay && t.OutOfRange)
        {
            _roundsLeft = 0;
            _nextMission = now + 4.0;
            Note = "target out of range";
            return;
        }
        Note = $"fire mission: {_roundsLeft} to go{(loader ? "" : ", loading himself")}";
        // Laid on this bomb's point, not still on the last one's.
        if (!t.Laid || t.LaidAt != _lay || t.Reloading || t.Cool > 0f) return;
        if (v.Fire(ti, false))
        {
            MortarRounds++;
            if (loader) MortarLoaded++;
            _roundsLeft--;
            _lay = _mission + Sheaf();
            // A crew of two: one lays, the other hangs the bomb, 3.5-5 s a round. One man alone has to take up a
            // bomb, load it and check his sight after every round.
            _nextRound = now + (loader ? _rng.RandfRange(3.5f, 5f) : _rng.RandfRange(7f, 9.5f));
            if (_roundsLeft <= 0) _nextMission = now + _rng.RandfRange(12f, 20f);
        }
    }

    /// <summary>Where in the target area the next bomb is laid: within ~20 m of the centre of the sightings.</summary>
    Vector3 Sheaf() => new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f)) * 22f;

    /// <summary>The assistant gunner: one of the team at the tube, on his feet, loading (see BotBrain.Board).</summary>
    Bot? Loader(Vehicle v)
    {
        if (_b.Squad == null) return null;
        foreach (var m in _b.Squad.Members)
            if (m is Bot b && b != _b && b.Alive && b.Ride == null && b.Brain.Note == BotBrain.AssistantGunner
                && (b.FeetPos - v.GlobalPosition).Length() < 3f)
                return b;
        return null;
    }

    public static int InfantryTargets, InfantryShots, ArmorShots, AreaRounds, HeldForFriendlies;
    double _areaPickAt, _ffAt, _calloutAt, _maskAt;
    bool _masked;
    Vector3 _areaPoint;
    bool _ffBlocked;

    /// <summary>
    /// How far this mount engages: a tank gun takes on armour out to ~2.5 km, an autocannon
    /// ~1.6 km; against people, HE out to ~1.5 km, a machine gun ~900 m. Beyond that it only
    /// gives the position away.
    /// </summary>
    static float Reach(TurretDef td, bool armor)
    {
        bool gun = td.Ammo.Any(a => a.Mag == 1 && a.AntiArmor);
        bool cannon = td.Ammo.Any(a => a.Mag > 1 && a.AntiArmor);
        bool he = td.Ammo.Any(a => a.Explosive);
        if (armor) return gun ? 2500f : cannon ? 1600f : 800f;
        return (gun || cannon) && he ? 1500f : cannon ? 1400f : 900f;
    }

    /// <summary>
    /// Would a shot from here to there endanger our own: someone close to the line, or within the reach of
    /// its fragments where it lands (<paramref name="blast"/>: 0 for solid shot and bullets).
    /// </summary>
    static bool Friendly(Vehicle v, Vector3 from, Vector3 to, float blast)
    {
        var seg = to - from;
        float len2 = seg.LengthSquared();
        foreach (var c in Combatants.All)
        {
            if (c.Team != v.CrewTeam || c.Dead || c.Ride != null) continue;
            var p = c.ChestPos;
            if (blast > 0f && p.DistanceTo(to) < blast) return true;
            float t = len2 > 1f ? Mathf.Clamp((p - from).Dot(seg) / len2, 0f, 1f) : 0f;
            if (t * MathF.Sqrt(len2) < 6f) continue; // right by the vehicle: under the gun
            if ((from + seg * t).DistanceTo(p) < 2.5f) return true;
        }
        return false;
    }

    /// <summary>
    /// Something solid right in front of the barrel (a wall, a tree, the crest the hull is down behind): the round
    /// would hit it a few metres off, among whoever is round the vehicle. The sight sits higher than the gun and sees
    /// over what the barrel can't. (A Centauro put two 105 mm HE rounds into a wall ten metres in front of it and
    /// killed its own infantry standing beside it.)
    /// </summary>
    static bool Masked(Vehicle v, Vector3 muzzle, Vector3 dir, float dist)
    {
        float reach = MathF.Min(dist - 3f, 60f);
        if (reach < 1f) return false;
        var hit = v.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(muzzle, muzzle + dir * reach, Layers.World | Layers.Trees, new Godot.Collections.Array<Rid> { v.GetRid() }));
        if (hit.Count == 0) return false;
        v.GunMaskedAt = Clock.Now;
        Prof.Count("gun:masked");
        return true;
    }

    float _shortBy;

    /// <summary>How far short of the target the round may come down before the shot isn't worth taking: a burst
    /// in the cover right in front of him still does its work, one on a crest halfway there doesn't.</summary>
    static float ShortLimit(float dist) => 30f + dist * 0.03f;

    /// <summary>
    /// Fly the round the gun is about to fire at <paramref name="to"/> (the superelevation the gun will put on it
    /// included) and find what it first strikes on the way: where, how far along the line to the target, and whether
    /// it's the hull of one of our own vehicles. Nothing before the target: null.
    /// </summary>
    static (Vector3 At, float Along, bool FriendHull)? FirstImpact(Vehicle v, Vector3 muzzle, Vector3 to, VWeapon w, float rangeHint, float dist)
    {
        if (dist < 1f || w.Speed <= 0f) return null;
        var los = (to - muzzle) / dist;
        var dir = los;
        var perp = dir.Cross(Vector3.Up);
        if (rangeHint > 0f && perp.LengthSquared() > 1e-4f) dir = dir.Rotated(perp.Normalized(), Ballistics.ZeroAngle(w.Speed, w.Drag, rangeHint));
        var space = v.GetWorld3D().DirectSpaceState;
        var excl = new Godot.Collections.Array<Rid> { v.GetRid() };
        const float dt = 1f / 60f;
        var g = new Vector3(0f, -9.81f, 0f);
        var pos = muzzle;
        var vel = dir * w.Speed;
        var chordFrom = pos;
        for (int k = 1; k <= 600; k++)
        {
            float sp = vel.Length();
            var nv = vel + (g - vel * (sp * w.Drag)) * dt;
            pos += (vel + nv) * (0.5f * dt);
            vel = nv;
            bool past = (pos - muzzle).Dot(los) > dist + 5f;
            if (k % 4 != 0 && !past) continue;
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(chordFrom, pos, Layers.Solid, excl));
            if (hit.Count > 0)
            {
                var at = hit["position"].AsVector3();
                bool friend = hit["collider"].As<GodotObject>() is Vehicle o && o != v && !o.Destroyed && o.CrewTeam == v.CrewTeam;
                return (at, (at - muzzle).Dot(los), friend);
            }
            // A proximity-fuzed round goes off beside any aircraft in the air it passes (Ballistics.ProxBurst), ours
            // included: one of our helicopters near the line is in the way as surely as a hull on it.
            if (w.Prox)
                foreach (var o in Vehicle.All)
                {
                    if (!o.Def.Air || o.Destroyed || o.Landed || o == v || o.CrewTeam != v.CrewTeam) continue;
                    var seg = pos - chordFrom;
                    float u = Mathf.Clamp((o.Center - chordFrom).Dot(seg) / MathF.Max(seg.LengthSquared(), 1e-6f), 0f, 1f);
                    var near = chordFrom + seg * u;
                    if (near.DistanceTo(o.Center) < 8f + o.Def.Hull.Z * 0.3f) return (near, (near - muzzle).Dot(los), true);
                }
            if (past) return null;
            chordFrom = pos;
        }
        return null;
    }

    /// <summary>How close to our own people HE may land: past most of its fragments' reach (a hand grenade's worth at the least).</summary>
    static float DangerClose(VWeapon w) => MathF.Max(14f, w.FragR * 1.3f);

    void Gun(Vehicle v, int ti, float dt)
    {
        var t = v.Turrets[ti];
        double now = Clock.Now;
        bool canKillArmor = t.Def.Ammo.Any(a => a.AntiArmor);
        if (now >= _nextPick)
        {
            _nextPick = now + 0.4;
            object? best = null;
            float bestScore = float.MinValue;
            // Enemy vehicles we (or our crew) can see.
            foreach (var ev in _b.Senses.Vehicles)
            {
                // Seen in the last few seconds: keep the gun on it, it'll show again.
                if (!GodotObject.IsInstanceValid(ev.Who) || ev.Who.Destroyed || now - ev.LastSeen > 6.0) continue;
                float d = ev.Who.Center.DistanceTo(v.Center);
                if (!(ev.Who.Def.Air && !ev.Who.Landed) && d > Reach(t.Def, true)) continue;
                // Only worth shooting if we can hurt it.
                // Only worth shooting if our best round can get through the side it's showing us.
                float bestPen = t.Def.Ammo.Max(a => a.Pen);
                if (bestPen < ev.Who.ArmorToward(v.Center) * 0.85f) continue;
                bool air = ev.Who.Def.Air && !ev.Who.Landed;
                bool aaGun = t.Def.Ammo.Any(a => a.Prox);
                if (air && d > (aaGun ? 3000f : 1200f)) continue;
                float score = 200f - d * 0.2f + (ev.Who.Def.Heavy ? 40f : 0f) + (air && aaGun ? 300f : 0f);
                // A gun that can kill us, laid on us: that one first.
                if (!air && ev.Who.Turrets.Length > 0 && ev.Who.Turrets[0].Def.Ammo.Length > 0)
                {
                    var et = ev.Who.Turrets[0];
                    if (et.Def.Ammo.Max(a => a.Pen) >= v.ArmorToward(ev.Who.Center) * 0.85f)
                    {
                        score += 40f;
                        if (et.Forward.AngleTo(v.Center - et.Muzzle.GlobalPosition) < 0.1f) score += 60f;
                    }
                }
                if (score > bestScore) { bestScore = score; best = ev.Who; }
            }
            // Drones: the AA gun's proximity rounds make short work of them; an MG will try at short range.
            bool aaGun2 = t.Def.Ammo.Any(a => a.Prox);
            foreach (var dr in Drone.All)
            {
                if (dr.Dead || dr.Team == v.CrewTeam || !GodotObject.IsInstanceValid(dr)) continue;
                float dd = dr.GlobalPosition.DistanceTo(v.Center);
                if (dd > (aaGun2 ? 2000f : 350f)) continue;
                if (v.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(t.Muzzle.GlobalPosition, dr.GlobalPosition, Layers.World | Layers.Trees)).Count > 0) continue;
                float score = (aaGun2 ? 320f : 60f) - dd * 0.1f + (dr.IsFpv && dd < 300f ? 200f : 0f);
                if (score > bestScore) { bestScore = score; best = dr; }
            }
            foreach (var th in _b.Senses.Threats)
            {
                if (!th.Visible || !th.Who.Alive) continue;
                float d = th.Who.FeetPos.DistanceTo(v.Center);
                if (d > Reach(t.Def, false)) continue;
                // Men with rockets are what kills armour; men right on top of us are next.
                float score = 100f - d * 0.2f + (th.Who.Role is Role.AntiTank or Role.HeavyAT && d < 500f ? 70f : 0f) + (d < 80f ? 50f : 0f);
                if (score > bestScore) { bestScore = score; best = th.Who; }
            }
            if (best is ICombatant) InfantryTargets++;
            if (best != Target)
            {
                if (best is Vehicle nv && now > _calloutAt)
                {
                    _calloutAt = now + 6.0;
                    Comms.Say(_b, $"Target, {nv.Def.ClassName.ToUpperInvariant()}, {nv.Center.DistanceTo(v.Center):0} meters — engaging!");
                }
                Target = best;
                _settle = 1f; // a new target: the lay starts rough
                _err = new Vector3(_rng.RandfRange(-1f, 1f), _rng.RandfRange(-0.5f, 1f), _rng.RandfRange(-1f, 1f));
            }
        }

        Vector3? point = Target switch
        {
            Vehicle ev when GodotObject.IsInstanceValid(ev) && !ev.Destroyed => _b.Senses.Vehicles.Find(x => x.Who == ev) is { } known ? (known.Visible ? known.AimPoint : known.LastKnownPos) : ev.Center,
            ICombatant c when c.Alive => c.ChestPos,
            Drone dr when !dr.Dead && GodotObject.IsInstanceValid(dr) => dr.GlobalPosition,
            _ => null,
        };
        if (point == null && v.FireAt is Vector3 fa && now < v.FireAtUntil)
        {
            Target = null;
            AreaFire(v, t, ti, fa, now, dt);
            return;
        }
        if (point is not Vector3 p)
        {
            Target = null;
            // Nothing to shoot: scan the sector we're covering (else ahead of the hull), slowly, side to side.
            var axis = v.Watch is Vector3 wv && ((wv - v.Center) with { Y = 0f }).LengthSquared() > 25f ? ((wv - v.Center) with { Y = 0f }).Normalized() : v.Forward;
            t.AimAt = v.Center + axis.Rotated(Vector3.Up, MathF.Sin((float)now * 0.25f) * (v.Watch != null ? 0.45f : 0.8f)) * 60f + Vector3.Up * 1.5f;
            Note = "scanning";
            return;
        }
        float dist = p.DistanceTo(t.Muzzle.GlobalPosition);
        bool armor = Target is Vehicle;
        // The round for the job.
        int want = -1;
        bool vsAir = Target is Vehicle { Def.Air: true, Landed: false } or Drone;
        for (int i = 0; i < t.Def.Ammo.Length; i++)
            if (vsAir ? t.Def.Ammo[i].Prox : armor ? t.Def.Ammo[i].AntiArmor : t.Def.Ammo[i].Explosive) { want = i; break; }
        if (want < 0 && vsAir) want = 0;
        bool useCoax = !armor && t.Def.Coax != null && (want < 0 || dist < 150f);
        if (!useCoax && want >= 0 && want != t.AmmoIdx) v.SelectAmmo(ti, want);
        var w = useCoax ? t.Def.Coax! : t.Weapon;
        var muzzle = useCoax && t.CoaxMuzzle != null ? t.CoaxMuzzle.GlobalPosition : t.Muzzle.GlobalPosition;
        // Aircraft and drones: lead them by where they'll be when the rounds get there, with the round's real time of
        // flight (drag slows it) and its drop over that flight, both from the fire-control solution for the round
        // being fired. The gun is laid on that solution as it stands, so no superelevation is added on firing (see
        // rangeHint below). (It led by distance over muzzle speed and added the drop, and the gun then added its
        // superelevation for the range on top: at 1.3 km the 35 mm bursts went 6 m high and 7 m short of the aircraft.)
        Vector3? mover = Target switch { Vehicle { Def.Air: true } ac => ac.Velocity3, Drone tdr => tdr.Vel, _ => null };
        if (mover is Vector3 mv)
        {
            var pred = p;
            var dir = (p - muzzle) / MathF.Max(dist, 1e-3f);
            for (int i = 0; i < 3; i++)
            {
                dir = Ballistics.Launch(w.Speed, w.Drag, pred - muzzle, out float tof);
                pred = p + mv * tof;
            }
            dist = pred.DistanceTo(muzzle);
            p = muzzle + dir * dist;
        }
        // The lay settles over a couple of seconds, faster for better gunners. A radar-directed gun locked on is laid
        // by its fire control, not by hand: what's left is the radar's tracking error, a milliradian or two. (It
        // used to be laid by eye like any other gun, with up to 0.6° of slack: 14 m at 1.3 km, beyond the fuze.)
        bool radarLaid = Target is Vehicle { Def.Air: true, Landed: false } rl && rl == _lockOn && !useCoax && w.Prox;
        _settle = MathF.Max(0f, _settle - dt * (0.4f + _b.P.Skill * 0.6f));
        var err = radarLaid ? _err * (dist * 0.0015f) : _err * (dist * 0.012f * _settle + dist * 0.0015f * (1.2f - _b.P.Skill));
        t.AimAt = p + err;
        Note = $"{(armor ? "engaging armour" : "engaging infantry")} with {w.Name} at {dist:0} m";

        // An aircraft: the radar has to lock on to it, and the fire control work out where to put the rounds, before
        // the first burst: a few seconds. The aircraft's warning receiver hears the lock. Out of sight for more than a
        // moment, and it's lost. (The gun used to open up the moment its crew saw one: a gunship that showed itself
        // over a ridge for a second was gone, and nothing it could do would have told it why.)
        if (Target is Vehicle { Def.Air: true, Landed: false } craft && t.Def.Ammo.Any(a => a.Prox))
        {
            if (_b.Senses.Vehicles.Find(x => x.Who == craft) is { Visible: true })
            {
                if (_lockOn != craft || now - _lockSeen > 1.5) { _lockOn = craft; _lockSince = now; }
                _lockSeen = now;
                craft.RadarLock(v);
            }
            if (_lockOn != craft || now - _lockSince < RadarLockTime) { Note = $"locking on to the {craft.Def.ClassName}"; return; }
        }

        // The radar-laid gun holds its fire until the lay is within the fuze's reach of the solution.
        float tol = radarLaid ? 0.25f : armor ? 0.6f : 1.4f;
        // Only fire at what we can actually see right now.
        if (armor && _b.Senses.Vehicles.Find(x => x.Who == Target) is { Visible: false }) return;
        if (v.AimError(ti) > tol || now < _nextShot) return;
        // The range handed to the gun for its superelevation: none when the fire-control solution already has the drop in it.
        float rangeHint = mover != null ? 0f : dist;
        if (now > _ffAt)
        {
            _ffAt = now + 0.25;
            _ffBlocked = Friendly(v, muzzle, p, !useCoax && w.Explosive ? DangerClose(w) : 0f);
            // Where the round will actually land, flown along its trajectory: a friendly hull in the way, or a crest,
            // wall or wood that stops it short and bursts it among our own. (Only the line's first 60 m and the
            // target point were checked: a Gepard shot down its own supply truck 118 m out in the line of fire, and a
            // T-72's HE burst on a rise short of its infantry target, beside its own anti-tank gunner.)
            _shortBy = 0f;
            if (!_ffBlocked && FirstImpact(v, muzzle, p, w, t.Def.Fixed ? 0f : rangeHint, dist) is var (at, along, friendHull))
            {
                if (friendHull) _ffBlocked = true;
                else
                {
                    _shortBy = dist - along;
                    if (!useCoax && w.Explosive && _shortBy > 3f && Friendly(v, muzzle, at, DangerClose(w))) _ffBlocked = true;
                }
            }
            if (_ffBlocked) HeldForFriendlies++;
        }
        if (_ffBlocked) { Note = "holding: friendlies in the way"; return; }
        if (now > _maskAt)
        {
            _maskAt = now + 0.25;
            _masked = Masked(v, muzzle, t.Forward, dist);
        }
        if (_masked) { Note = "holding: gun masked"; return; }
        // The round would come down well short of the target (a crest or buildings between): nothing to be gained
        // by firing, and the position wants changing, as for a masked gun.
        if (_shortBy > ShortLimit(dist))
        {
            v.GunMaskedAt = now;
            Prof.Count("gun:falls short");
            Note = $"holding: no line to the target ({_shortBy:0} m short)";
            return;
        }
        // Automatic weapons fire in bursts; big guns one round at a time when laid.
        if (w.Mag > 1)
        {
            if (now > _burstUntil + 0.6) _burstUntil = now + _rng.RandfRange(0.4f, 1.2f);
            if (now > _burstUntil) return;
        }
        else _nextShot = now + _rng.RandfRange(0.4f, 1.5f); // a moment to confirm the lay
        if (v.Fire(ti, useCoax, rangeHint)) { if (armor) ArmorShots++; else InfantryShots++; }
    }

    /// <summary>
    /// Fire onto a point for the infantry (their contact, the objective in the assault) with
    /// nobody in sight: HE if the gun has it, else short bursts, walked around the area.
    /// </summary>
    void AreaFire(Vehicle v, Vehicle.TurretState t, int ti, Vector3 at, double now, float dt)
    {
        if (now > _areaPickAt)
        {
            _areaPickAt = now + _rng.RandfRange(2f, 4f);
            _areaPoint = at + new Vector3(_rng.RandfRange(-6f, 6f), _rng.RandfRange(-0.5f, 1.5f), _rng.RandfRange(-6f, 6f));
            int heIdx = Array.FindIndex(t.Def.Ammo, a => a.Explosive);
            float blast = heIdx >= 0 ? DangerClose(t.Def.Ammo[heIdx]) : 0f;
            _ffBlocked = Friendly(v, t.Muzzle.GlobalPosition, _areaPoint, blast);
            // Where the round really comes down, as for a laid shot (see Gun): short of the point, among our own, or
            // into one of our hulls.
            _shortBy = 0f;
            bool coax = heIdx < 0 && t.Def.Coax != null;
            var aw = heIdx >= 0 ? t.Def.Ammo[heIdx] : coax ? t.Def.Coax! : t.Weapon;
            var am = coax && t.CoaxMuzzle != null ? t.CoaxMuzzle.GlobalPosition : t.Muzzle.GlobalPosition;
            float ad = _areaPoint.DistanceTo(am);
            if (!_ffBlocked && FirstImpact(v, am, _areaPoint, aw, t.Def.Fixed ? 0f : ad, ad) is var (ip, along, friendHull))
            {
                _shortBy = ad - along;
                _ffBlocked = friendHull || blast > 0f && _shortBy > 3f && Friendly(v, am, ip, blast);
            }
        }
        t.AimAt = _areaPoint;
        Note = v.FireAtWhy;
        float dist = _areaPoint.DistanceTo(t.Muzzle.GlobalPosition);
        if (dist > Reach(t.Def, false) || _ffBlocked) { Note = v.FireAtWhy + " (holding)"; return; }
        if (_shortBy > ShortLimit(dist)) { v.GunMaskedAt = now; Note = v.FireAtWhy + " (no line to it)"; return; }
        int he = Array.FindIndex(t.Def.Ammo, a => a.Explosive);
        if (he >= 0 && he != t.AmmoIdx) v.SelectAmmo(ti, he);
        bool main = he >= 0 || t.Def.Coax == null;
        var w = main ? t.Weapon : t.Def.Coax!;
        if (v.AimError(ti) > 2.5f || now < _nextShot) return;
        if (Masked(v, main ? t.Muzzle.GlobalPosition : t.CoaxMuzzle?.GlobalPosition ?? t.Muzzle.GlobalPosition, t.Forward, dist)) { Note = v.FireAtWhy + " (gun masked)"; _nextShot = now + 1.0; return; }
        if (w.Mag > 1)
        {
            if (now > _burstUntil + 0.6) _burstUntil = now + _rng.RandfRange(0.4f, 1.0f);
            if (now > _burstUntil) { _nextShot = now + _rng.RandfRange(1.5f, 3.5f); return; }
        }
        else _nextShot = now + _rng.RandfRange(5f, 9f); // a round of HE every few seconds
        if (v.Fire(ti, !main, dist)) AreaRounds++;
    }
}
