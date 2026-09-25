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

    public static int InfantryTargets, InfantryShots, ArmorShots;

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
                // Only worth shooting if we can hurt it.
                // Only worth shooting if our best round can get through the side it's showing us.
                float bestPen = t.Def.Ammo.Max(a => a.Pen);
                if (bestPen < ev.Who.ArmorToward(v.Center) * 0.85f) continue;
                bool air = ev.Who.Def.Air && !ev.Who.Landed;
                bool aaGun = t.Def.Ammo.Any(a => a.Prox);
                if (air && d > (aaGun ? 3000f : 1200f)) continue;
                float score = 200f - d * 0.2f + (ev.Who.Def.Heavy ? 40f : 0f) + (air && aaGun ? 300f : 0f);
                if (score > bestScore) { bestScore = score; best = ev.Who; }
            }
            foreach (var th in _b.Senses.Threats)
            {
                if (!th.Visible || !th.Who.Alive) continue;
                float d = th.Who.FeetPos.DistanceTo(v.Center);
                float score = 100f - d * 0.2f;
                if (score > bestScore) { bestScore = score; best = th.Who; }
            }
            if (best is ICombatant) InfantryTargets++;
            if (best != Target)
            {
                Target = best;
                _settle = 1f; // a new target: the lay starts rough
                _err = new Vector3(_rng.RandfRange(-1f, 1f), _rng.RandfRange(-0.5f, 1f), _rng.RandfRange(-1f, 1f));
            }
        }

        Vector3? point = Target switch
        {
            Vehicle ev when GodotObject.IsInstanceValid(ev) && !ev.Destroyed => _b.Senses.Vehicles.Find(x => x.Who == ev) is { } known ? (known.Visible ? known.AimPoint : known.LastKnownPos) : ev.Center,
            ICombatant c when c.Alive => c.ChestPos,
            _ => null,
        };
        if (point is not Vector3 p)
        {
            Target = null;
            // Nothing to shoot: scan ahead of the hull.
            t.AimAt = v.Center + v.Forward.Rotated(Vector3.Up, MathF.Sin((float)now * 0.3f) * 0.8f) * 60f + Vector3.Up * 1.5f;
            Note = "scanning";
            return;
        }
        float dist = p.DistanceTo(t.Muzzle.GlobalPosition);
        // Aircraft: lead them by where they'll be when the rounds get there.
        if (Target is Vehicle { Def.Air: true } ac)
            p += ac.Velocity3 * (dist / MathF.Max(t.Weapon.Speed, 1f)) + Vector3.Up * (9.81f * MathF.Pow(dist / MathF.Max(t.Weapon.Speed, 1f), 2f) * 0.5f);
        // The lay settles over a couple of seconds, faster for better gunners.
        _settle = MathF.Max(0f, _settle - dt * (0.4f + _b.P.Skill * 0.6f));
        var err = _err * (dist * 0.012f * _settle + dist * 0.0015f * (1.2f - _b.P.Skill));
        t.AimAt = p + err;

        bool armor = Target is Vehicle;
        // The round for the job.
        int want = -1;
        bool vsAir = Target is Vehicle { Def.Air: true, Landed: false };
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
        // Automatic weapons fire in bursts; big guns one round at a time when laid.
        if (w.Mag > 1)
        {
            if (now > _burstUntil + 0.6) _burstUntil = now + _rng.RandfRange(0.4f, 1.2f);
            if (now > _burstUntil) return;
        }
        else _nextShot = now + _rng.RandfRange(0.4f, 1.5f); // a moment to confirm the lay
        if (v.Fire(ti, useCoax, dist)) { if (armor) ArmorShots++; else InfantryShots++; }
    }
}
