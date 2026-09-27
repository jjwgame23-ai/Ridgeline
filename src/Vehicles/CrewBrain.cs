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
    Vector3 _mission;

    /// <summary>
    /// A mortar: find a fresh cluster of enemy sightings in range (not near our own
    /// people), lay on it and put five or six bombs into it with a little spread; then
    /// wait for the next target.
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
        // Each bomb a little off: the tube and the ranging aren't perfect.
        var spread = new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f)) * 22f;
        t.AimAt = _mission + spread;
        Note = $"fire mission: {_roundsLeft} to go";
        if (!t.Laid || t.Reloading || t.Cool > 0f) return;
        if (v.Fire(ti, false))
        {
            MortarRounds++;
            _roundsLeft--;
            _nextRound = now + _rng.RandfRange(3.5f, 5f);
            if (_roundsLeft <= 0) _nextMission = now + _rng.RandfRange(12f, 20f);
        }
    }

    public static int InfantryTargets, InfantryShots, ArmorShots, AreaRounds, HeldForFriendlies;
    double _areaPickAt, _ffAt, _calloutAt;
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
        // Aircraft: lead them by where they'll be when the rounds get there.
        if (Target is Vehicle { Def.Air: true } ac)
            p += ac.Velocity3 * (dist / MathF.Max(t.Weapon.Speed, 1f)) + Vector3.Up * (9.81f * MathF.Pow(dist / MathF.Max(t.Weapon.Speed, 1f), 2f) * 0.5f);
        else if (Target is Drone tdr)
            p += tdr.Vel * (dist / MathF.Max(t.Weapon.Speed, 1f)) + Vector3.Up * (9.81f * MathF.Pow(dist / MathF.Max(t.Weapon.Speed, 1f), 2f) * 0.5f);
        // The lay settles over a couple of seconds, faster for better gunners.
        _settle = MathF.Max(0f, _settle - dt * (0.4f + _b.P.Skill * 0.6f));
        var err = _err * (dist * 0.012f * _settle + dist * 0.0015f * (1.2f - _b.P.Skill));
        t.AimAt = p + err;

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
        Note = $"{(armor ? "engaging armour" : "engaging infantry")} with {w.Name} at {dist:0} m";

        float tol = armor ? 0.6f : 1.4f;
        // Only fire at what we can actually see right now.
        if (armor && _b.Senses.Vehicles.Find(x => x.Who == Target) is { Visible: false }) return;
        if (v.AimError(ti) > tol || now < _nextShot) return;
        if (now > _ffAt)
        {
            _ffAt = now + 0.25;
            _ffBlocked = Friendly(v, t.Muzzle.GlobalPosition, p, !useCoax && w.Explosive ? DangerClose(w) : 0f);
            if (_ffBlocked) HeldForFriendlies++;
        }
        if (_ffBlocked) { Note = "holding: friendlies in the way"; return; }
        // Automatic weapons fire in bursts; big guns one round at a time when laid.
        if (w.Mag > 1)
        {
            if (now > _burstUntil + 0.6) _burstUntil = now + _rng.RandfRange(0.4f, 1.2f);
            if (now > _burstUntil) return;
        }
        else _nextShot = now + _rng.RandfRange(0.4f, 1.5f); // a moment to confirm the lay
        if (v.Fire(ti, useCoax, dist)) { if (armor) ArmorShots++; else InfantryShots++; }
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
            _ffBlocked = Friendly(v, t.Muzzle.GlobalPosition, _areaPoint, heIdx >= 0 ? DangerClose(t.Def.Ammo[heIdx]) : 0f);
        }
        t.AimAt = _areaPoint;
        Note = v.FireAtWhy;
        float dist = _areaPoint.DistanceTo(t.Muzzle.GlobalPosition);
        if (dist > Reach(t.Def, false) || _ffBlocked) { Note = v.FireAtWhy + " (holding)"; return; }
        int he = Array.FindIndex(t.Def.Ammo, a => a.Explosive);
        if (he >= 0 && he != t.AmmoIdx) v.SelectAmmo(ti, he);
        bool main = he >= 0 || t.Def.Coax == null;
        var w = main ? t.Weapon : t.Def.Coax!;
        if (v.AimError(ti) > 2.5f || now < _nextShot) return;
        if (w.Mag > 1)
        {
            if (now > _burstUntil + 0.6) _burstUntil = now + _rng.RandfRange(0.4f, 1.0f);
            if (now > _burstUntil) { _nextShot = now + _rng.RandfRange(1.5f, 3.5f); return; }
        }
        else _nextShot = now + _rng.RandfRange(5f, 9f); // a round of HE every few seconds
        if (v.Fire(ti, !main, dist)) AreaRounds++;
    }
}
