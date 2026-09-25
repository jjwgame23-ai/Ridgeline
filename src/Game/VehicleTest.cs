using Godot;

namespace Ridgeline;

/// <summary>
/// Dev testbed on the valley: every vehicle type for your side parked in a row in
/// front of you, and armour duels between bot crews a few hundred metres out, with
/// everything logged. (mode=vtest)
/// </summary>
public partial class VehicleTest : Node, IMatch
{
    public Valley Map = null!;
    public Hud PlayerHud = null!;
    public List<Bot> Bots { get; } = new();
    public Spectator Spec { get; private set; } = null!;
    readonly RandomNumberGenerator _rng = new();
    double _nextLog, _nextAirLog;
    Vehicle? _uh, _ah;
    int _uhLeg;

    public override void _Ready()
    {
        _rng.Seed = 5;
        Spec = new Spectator { Mode = this };
        AddChild(Spec);
        Radio.Reset();
        Vehicle.Lost += OnLost;
        Combatants.Killed += (v, h) => GD.Print($"[{Clock.Now:0}s] {h.Shooter?.Callsign ?? "?"} killed {v.Callsign} with {h.Weapon}");

        // Open ground: the base, facing the middle of the map.
        var home = Map.Bases[0];
        var toMid = (-home with { Y = 0f }).Normalized();
        var right = toMid.Cross(Vector3.Up);
        var p = new Player { TeamId = 0, Kit = Role.Rifleman };
        GetParent().AddChild(p);
        p.GlobalPosition = Map.Ground(home + toMid * 60f) + Vector3.Up * 0.3f;
        p.SetYaw(Mathf.RadToDeg(MathF.Atan2(-toMid.X, -toMid.Z)));
        PlayerHud.P = p;

        int i = 0;
        foreach (VKind k in Enum.GetValues<VKind>())
            Spawn(k, 0, home + toMid * 78f + right * ((i++ - 3) * 7f), toMid);

        // Duels out in the valley: our tank and IFV against theirs, crewed by bots.
        var mid = home + toMid * 380f;
        Spawn(VKind.MBT, 0, mid - right * 40f, toMid, crew: true);
        Spawn(VKind.IFV, 0, mid + right * 40f, toMid, crew: true);
        Spawn(VKind.MBT, 1, mid + toMid * 420f - right * 30f, -toMid, crew: true);
        Spawn(VKind.APC, 1, mid + toMid * 400f + right * 50f, -toMid, crew: true);
        if (OS.GetCmdlineUserArgs().Contains("air"))
        {
            // Flight tests: a transport flies out, lands, and comes back; a gunship attacks the enemy tank.
            _uh = Spawn(VKind.UH, 0, home + toMid * 110f - right * 30f, toMid, crew: true);
            _uh.Goal = Map.Ground(home + toMid * 700f + right * 150f);
            _uh.AirMode = HeliMode.Land;
            _ah = Spawn(VKind.AH, 0, home + toMid * 110f + right * 40f, toMid, crew: true);
            _ah.Goal = mid + toMid * 420f - right * 30f;
            _ah.AirMode = HeliMode.Attack;
            _ah.RunFrom = -toMid;
        }
        GD.Print("--- VEHICLE TEST ---");
    }

    public override void _ExitTree() => Vehicle.Lost -= OnLost;

    Vehicle Spawn(VKind k, int team, Vector3 at, Vector3 facing, bool crew = false)
    {
        var v = new Vehicle { Def = VehicleDef.Get(k, team), Team = team };
        GetParent().AddChild(v);
        v.GlobalPosition = Map.Ground(at);
        v.Rotation = new Vector3(0f, MathF.Atan2(-facing.X, -facing.Z), 0f);
        if (!crew) return v;
        for (int s = 0; s < v.Def.Seats.Count && s < 2; s++)
        {
            var b = new Bot { TeamId = team, P = Personality.Roll(_rng, $"{v.Def.Name.Split(' ')[0]}-{s}"), Role = Role.Rifleman };
            b.P.Skill = 0.8f;
            GetParent().AddChild(b);
            b.GlobalPosition = v.GlobalPosition + Vector3.Up * 3f;
            Bots.Add(b);
            v.Enter(b, s);
        }
        return v;
    }

    void OnLost(Vehicle v, ICombatant? by) =>
        GD.Print($"[{Clock.Now:0}s] DESTROYED {v.Def.Name} (team {v.Team}) by {by?.Callsign ?? "?"}");

    bool _shotDown;
    Vector3 _killedAt;

    public override void _Process(double delta)
    {
        // "shootdown": kill the gunship outright once it's flying fast, then log where the wreck lands.
        if (OS.GetCmdlineUserArgs().Contains("shootdown") && _ah != null && IsInstanceValid(_ah))
        {
            if (!_shotDown && !_ah.Landed && _ah.AirSpeed > 15f && _ah.Agl > 30f)
            {
                _shotDown = true;
                _killedAt = _ah.GlobalPosition;
                GD.Print($"[{Clock.Now:0.0}s] SHOOTDOWN {_ah.Def.Name} at {_killedAt} speed {_ah.AirSpeed:0} agl {_ah.Agl:0}");
                _ah.Damage(99999f, null);
                _ah.Damage(99999f, null); // a second time: straight past the doomed-autorotation stage to a dead airframe
            }
            else if (_shotDown && _ah.Landed && _killedAt != Vector3.Zero)
            {
                var off = (_ah.GlobalPosition - _killedAt) with { Y = 0f };
                GD.Print($"[{Clock.Now:0.0}s] WRECK DOWN {off.Length():0} m horizontally from the kill, {_killedAt.Y - _ah.GlobalPosition.Y:0} m below");
                _killedAt = Vector3.Zero;
            }
        }
        if (_uh != null && Clock.Now >= _nextAirLog)
        {
            _nextAirLog = Clock.Now + 5.0;
            foreach (var v in new[] { _uh, _ah! })
                GD.Print($"[{Clock.Now:0}s] AIR {v.Def.Name,-16} {v.Status(),-12} pos {v.GlobalPosition.X:0},{v.GlobalPosition.Y:0},{v.GlobalPosition.Z:0} agl {v.Agl:0} spd {v.AirSpeed:0} vs {v.Velocity3.Y:0.0} " +
                         $"coll {v.Collective:0.00} cyc {v.CyclicPitch:0.00}/{v.CyclicRoll:0.00} landed {v.Landed} mode {v.AirMode} phase {v.AttackPhase} goal {(v.Goal is Vector3 g ? v.GlobalPosition.DistanceTo(g) : -1):0} m");
            // The transport: out, land, then back home.
            if (_uhLeg == 0 && _uh.Landed && _uh.Arrived && Clock.Now > 20) { _uhLeg = 1; _uh.Goal = Map.Ground(Map.Bases[0] + (-Map.Bases[0] with { Y = 0f }).Normalized() * 110f); GD.Print("UH landed at LZ; heading home"); }
            if (_uhLeg == 1 && _uh.Landed && _uh.Arrived && Clock.Now > 40) { _uhLeg = 2; GD.Print($"UH home at {Clock.Now:0}s"); }
        }
        if (!DuelMode.Verbose || Clock.Now < _nextLog) return;
        _nextLog = Clock.Now + 10.0;
        foreach (var v in Vehicle.All)
        {
            if (!v.Crewed && !v.Destroyed) continue;
            var g = v.GunnerSeat >= 0 ? v.Occupants[v.GunnerSeat] as Bot : null;
            GD.Print($"[{Clock.Now:0}s] {v.Def.Name,-16} t{v.Team} {v.Status(),-30} {(g != null ? g.Crew.Note : "no gunner: " + string.Join(",", v.Occupants.Select(o => o?.Callsign ?? "-")))}" +
                     $" sees {(g?.Senses.Vehicles.Count(x => x.Visible) ?? 0)} turret {(v.Turrets.Length > 0 ? $"{v.Turrets[0].Yaw:0}/{v.Turrets[0].Pitch:0} err {v.AimError(0):0.0}" : "")}");
        }
    }
}
