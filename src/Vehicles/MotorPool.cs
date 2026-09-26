using Godot;

namespace Ridgeline;

/// <summary>
/// Each side's vehicles: which it fields (by team size), where they park at base,
/// who crews them, when a lost one comes back, and the jobs the non-combat ones do:
/// - transports pick up a rifle squad that's far from its objective, drive it to a
///   drop-off short of it, unload, and come back;
/// - the logistics truck carries a FOB's worth of supplies forward for the logistics
///   team to build;
/// - combat vehicles drive to the overwatch position their crew squad was given.
/// Losing a vehicle costs its tickets.
/// </summary>
public sealed partial class MotorPool
{
    public sealed class Slot
    {
        public VKind Kind;
        public int Team;
        public Vehicle? Live;
        public double RespawnAt;
        public Squad? Crew;
        public Vector3 Park;
        public float Yaw;
        // transport job
        public int Job;            // 0 idle, 1 pickup, 2 carry, 3 return
        public Squad? Cargo;
        public double JobSince;
        public Vector3 Drop, LoadedAt;
        public bool HasSupplies = true;
        // combined arms (MotorPoolCombat)
        public int Mech;                  // a carrier with its squad: 0 not carrying, 1 picking up, 2 carrying, 3 dismounted
        public Vector3 DropFor;           // the objective the drop-off was worked out for
        public Vector3 Firing, FiringWatch, FiringAnchor;
        public bool HasFiring;
        public string FiringWhy = "";
        public double FiringAt, LastHitSeen = -99, ScootUntil, RearmAt = -1, LastRequestAt = -99;
        public Vector3 ScootTo;
        public bool Rearming;
        public int ShotsAtFiring;
        public double PickupRetryAt, PickupTimeout;
        public double ShotsCheckedAt;
    }

    readonly TerritoryMode _m;
    public readonly List<Slot> Slots = new();
    double _next;

    public MotorPool(TerritoryMode m) => _m = m;

    /// <summary>What a side of n fields: more, and heavier, as the side grows.</summary>
    /// <summary>
    /// What a side of n fields. Each faction has its own mix: Bravo leans on wheeled
    /// APCs where the others field tracked IFVs, and Charlie's gun is the wheeled MGS
    /// rather than a tank.
    /// </summary>
    public static List<VKind> Fleet(int n, int team)
    {
        var l = new List<VKind>();
        if (n >= 8 && n < 24) l.Add(VKind.LTV);
        if (n >= 8) l.Add(VKind.Transport);
        if (n >= 16) { l.Add(VKind.Logistics); l.Add(team == 1 ? VKind.APC : VKind.IFV); }
        if (n >= 20) l.Add(VKind.Mortar);
        if (n >= 24) { l.Add(VKind.UH); l.Add(team == 2 ? VKind.MGS : VKind.MBT); }
        if (n >= 30) { l.Add(VKind.AH); l.Add(VKind.SPAA); }
        return l;
    }

    /// <summary>How many crew a vehicle takes out of the infantry.</summary>
    public static int Crew(VKind k) => k is VKind.Transport or VKind.UH ? 1 : 2;

    /// <summary>What kind of squad crews it.</summary>
    public static SquadKind CrewKind(VKind k) => k switch
    {
        VKind.Transport => SquadKind.Transport,
        VKind.UH or VKind.AH => SquadKind.Air,
        VKind.Mortar => SquadKind.Mortar,
        VKind.Logistics => SquadKind.Logistics,
        _ => SquadKind.Armor,
    };

    /// <summary>How many soldiers the fleet takes out of the infantry (the logistics truck is driven by the logistics team).</summary>
    public static int CrewCount(int n, int team) => Fleet(n, team).Sum(Crew);

    public void Add(VKind k, int team, Squad? crew, Vector3 park, float yaw)
    {
        var s = new Slot { Kind = k, Team = team, Crew = crew, Park = park, Yaw = yaw };
        Slots.Add(s);
        Spawn(s);
    }

    void Spawn(Slot s)
    {
        var v = new Vehicle { Def = VehicleDef.Get(s.Kind, s.Team), Team = s.Team };
        _m.GetParent().AddChild(v);
        v.GlobalPosition = _m.Map.Ground(s.Park);
        v.Rotation = new Vector3(0f, s.Yaw, 0f);
        s.Live = v;
        s.Job = 0;
        s.HasSupplies = true;
        if (s.Crew != null) s.Crew.Vehicle = v;
    }

    public void OnLost(Vehicle v)
    {
        var s = Slots.Find(x => x.Live == v);
        if (s == null) return;
        s.Live = null;
        s.RespawnAt = Clock.Now + v.Def.Respawn;
        if (s.Crew != null) s.Crew.Vehicle = null;
        if (s.Cargo != null) { s.Cargo.Transport = null; s.Cargo = null; }
    }

    public void Tick()
    {
        double now = Clock.Now;
        if (now < _next) return;
        _next = now + 0.5;
        foreach (var s in Slots)
        {
            if (s.Live == null)
            {
                if (now >= s.RespawnAt && !_m.Out[s.Team]) Spawn(s);
                continue;
            }
            var v = s.Live;
            if (!GodotObject.IsInstanceValid(v) || v.Destroyed) continue;
            if (Abandon(s, v, now)) continue;
            switch (s.Kind)
            {
                case VKind.Transport: Transport(s, v, now); break;
                case VKind.UH: AirLift(s, v, now); break;
                case VKind.AH: Gunship(s, v); break;
                case VKind.Mortar: MortarPit(s, v, now); break;
                case VKind.Logistics: Logistics(s, v, now); break;
                default: Combat(s, v, now); break;
            }
        }
    }


    void Transport(Slot s, Vehicle v, double now)
    {
        v.ArriveRadius = 10f;
        var sq = s.Cargo;
        switch (s.Job)
        {
            case 0: // idle: find a rifle squad with a long way to go
            {
                v.Boarding = false;
                v.Goal = s.Park;
                Squad? best = null;
                float bestD = 380f;
                foreach (var cand in _m.Squads[s.Team])
                {
                    if (cand.Kind != SquadKind.Rifle || cand.Transport != null || cand.Objective == null || cand.Leader is not ICombatant lead || Mechanised(cand)) continue;
                    float fetch = lead.FeetPos.DistanceTo(v.GlobalPosition);
                    if (fetch > 700f * _m.Map.SizeScale) continue;
                    // Worth it: a long way still to go, and not a long way to fetch them.
                    float d = (cand.Objective.Center - lead.FeetPos with { Y = cand.Objective.Center.Y }).Length();
                    if (d - fetch * 0.5f > bestD) { bestD = d - fetch * 0.5f; best = cand; }
                }
                if (best == null || v.Driver == null) break;
                s.Cargo = best;
                best.Transport = v;
                s.Job = 1;
                s.JobSince = now;
                if (best.Leader is Bot lb) Comms.Say(v.Driver, $"{best.Name}, your ride's here. Mount up!");
                break;
            }
            case 1: // pickup: drive to the squad, then wait while they climb in
            {
                if (sq == null || sq.Objective == null || sq.Alive == 0) { Release(s); break; }
                var lead = sq.Leader;
                if (lead != null && lead.Ride != v && lead.FeetPos.DistanceTo(v.GlobalPosition) > 30f)
                {
                    // Pull up short of them, not on top of them.
                    var toLead = (lead.FeetPos - v.GlobalPosition) with { Y = 0f };
                    v.Goal = lead.FeetPos - toLead.Normalized() * 15f;
                    v.ArriveRadius = 12f;
                    v.Boarding = false;
                    if (now - s.JobSince > 150.0) Release(s); // can't get to them: they'll walk
                    break;
                }
                v.Goal = null;
                v.Boarding = true;
                int aboard = sq.Members.Count(m => m.Alive && m.Ride == v);
                // Bots, plus the player if they're close enough to be waited for.
                int alive = sq.Members.Count(m => m.Alive && GodotObject.IsInstanceValid((GodotObject)m)
                                                  && (m is Bot || m.FeetPos.DistanceTo(v.GlobalPosition) < 150f));
                if (aboard >= alive || (now - s.JobSince > 50.0 && aboard * 2 >= alive) || now - s.JobSince > 90.0)
                {
                    if (aboard == 0) { Release(s); break; }
                    // Drop-off: short of the objective, on the side we're coming from, somewhere it can't see (a soft-skinned truck full of men).
                    var c = sq.Objective.Center;
                    float r0 = MathF.Max(240f, sq.Objective.Radius + 150f);
                    s.Drop = FindDismount(s.Team, c, v.GlobalPosition, r0, r0 + 150f);
                    s.Job = 2;
                    s.JobSince = now;
                    v.Boarding = false;
                    v.Goal = s.Drop;
                    v.Arrived = false; // still set from pulling up to them
                    s.LoadedAt = v.GlobalPosition;
                    if (DuelMode.Verbose) GD.Print($"[{now:0}s] {v.Def.Name} loaded {sq.Name} ({aboard} aboard), {s.Drop.DistanceTo(v.GlobalPosition):0} m to the drop-off");
                    Comms.Say(v.Driver ?? lead!, "Everyone's in. Moving out!");
                }
                break;
            }
            case 2: // carry
            {
                if (sq == null) { Release(s); break; }
                v.Goal = s.Drop;
                // Unload at the drop-off, or early if we're taking fire.
                bool hit = now - v.LastHit < 2.0;
                // Actually there (not just a flag left over from an earlier stop), or taking fire after setting off.
                bool there = v.Arrived && s.Drop is Vector3 dp && (dp - v.GlobalPosition with { Y = dp.Y }).Length() < v.ArriveRadius + 5f;
                bool called = sq.WantDismount;
                sq.WantDismount = false;
                if (there || called || (hit && now - s.JobSince > 3.0) || now - s.JobSince > 240.0)
                {
                    foreach (var o in v.Occupants.ToArray())
                        if (o != null && o != v.Driver && sq.Members.Contains(o))
                        {
                            v.Leave(o, hit ? v.LastHitFrom : null);
                            if (o is Player) Hud.Toast(hit ? "TAKING FIRE — GET OUT!" : "Drop-off — out, and go with your squad", 3f);
                        }
                    if (v.Driver != null) Comms.Say(v.Driver, hit ? "Taking fire! Everybody out!" : "This is your stop. Good luck!");
                    if (DuelMode.Verbose) GD.Print($"[{now:0}s] {v.Def.Name} unloaded {sq.Name} after {v.GlobalPosition.DistanceTo(s.LoadedAt):0} m ({(there ? "at the drop-off" : hit ? "under fire" : "gave up")}), {v.GlobalPosition.DistanceTo(s.Drop):0} m short");
                    Release(s);
                    s.Job = 3;
                }
                break;
            }
            case 3: // back to base for the next run
                v.Goal = s.Park;
                v.ArriveRadius = 25f;
                if (v.Arrived) s.Job = 0;
                break;
        }
    }

    void Release(Slot s)
    {
        if (s.Cargo != null) s.Cargo.Transport = null;
        s.Cargo = null;
        s.Job = 0;
        if (s.Live != null) s.Live.Boarding = false;
    }

    /// <summary>
    /// The logistics truck: loaded, it goes to the FOB site the commander picked for the
    /// logistics team and they build there; empty, it goes home to reload.
    /// </summary>
    void Logistics(Slot s, Vehicle v, double now)
    {
        v.ArriveRadius = 8f;
        var sq = s.Crew;
        if (!s.HasSupplies)
        {
            // Empty: it waits by a FOB or at base for the next supply run to reach it.
            v.Goal = v.Driver != null ? s.Park : null;
            if (s.JobSince <= 0) s.JobSince = now;
            bool home = v.GlobalPosition.DistanceTo(_m.Map.Bases[s.Team]) < 80f || Fob.All.Any(f => f.Team == s.Team && f.GlobalPosition.DistanceTo(v.GlobalPosition) < 60f);
            if ((home && now - s.JobSince > 120.0) || v.Arrived)
            {
                s.HasSupplies = true;
                s.JobSince = 0;
            }
            return;
        }
        var site = sq?.FobSite;
        v.Goal = site ?? s.Park;
        // Can't get there (stuck, blocked): put it down where we are, if that's forward enough.
        if (site != null && v.Driver != null && sq!.FobBuildStart < 0)
        {
            if (s.JobSince <= 0) s.JobSince = now;
            if (now - s.JobSince > 150.0 && v.GlobalPosition.DistanceTo(_m.Map.Bases[s.Team]) > 250f)
            {
                sq.FobSite = site = v.GlobalPosition;
                v.Goal = site;
            }
        }
        else s.JobSince = 0;
        if (site is Vector3 fs && v.Arrived && v.Driver != null)
        {
            // Unload and build: the team gets out; the FOB goes up once they've worked on it.
            if (sq!.FobBuildStart < 0)
            {
                sq.FobBuildStart = now;
                foreach (var o in v.Occupants.ToArray()) if (o is Bot b) v.Leave(b);
                Comms.Say(sq.Leader ?? v.Driver!, "Building a FOB here!");
            }
        }
        if (sq != null && sq.FobBuildStart > 0 && now - sq.FobBuildStart > 25.0 && site is Vector3 at)
        {
            _m.BuildFob(s.Team, at);
            sq.FobSite = null;
            sq.FobBuildStart = -1;
            s.HasSupplies = false;
        }
    }

    public Slot? SlotOf(Vehicle v) => Slots.Find(s => s.Live == v);

    // ================================================================ aircraft

    /// <summary>
    /// A clear, flat landing zone near a point: level ground, and nothing solid (houses,
    /// trees, rocks) within reach of the rotor.
    /// </summary>
    public Vector3 FindLZ(Vector3 near, float within = 80f)
    {
        var space = _m.Map.GetWorld3D().DirectSpaceState;
        var rng = new RandomNumberGenerator();
        rng.Randomize();
        Vector3 best = _m.Map.Ground(near);
        float bestScore = float.MinValue;
        for (int k = 0; k < 24; k++)
        {
            var p = near + new Vector3(rng.RandfRange(-within, within), 0f, rng.RandfRange(-within, within));
            if (MathF.Abs(p.X) > _m.Map.Half - 120f || MathF.Abs(p.Z) > _m.Map.Half - 120f) continue;
            if (_m.Map.NormalAt(p.X, p.Z).Y < 0.94f) continue;
            p.Y = _m.Map.HeightAt(p.X, p.Z);
            bool clear = true;
            foreach (var o in new[] { Vector3.Zero, new Vector3(9f, 0f, 0f), new Vector3(-9f, 0f, 0f), new Vector3(0f, 0f, 9f), new Vector3(0f, 0f, -9f) })
            {
                var top = p + o + Vector3.Up * 40f;
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(top, p + o + Vector3.Down * 2f, Layers.World | Layers.Trees));
                if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node n || !n.IsInGroup("ground") || n.IsInGroup("floor")) { clear = false; break; }
            }
            if (!clear) continue;
            float score = -p.DistanceTo(near) * 0.2f + rng.RandfRange(0f, 3f);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    /// <summary>
    /// Transport helicopter jobs, in order of priority:
    /// - medevac: a cluster of our wounded down somewhere quiet: land by them, load them
    ///   (a crew medic stabilises each one as they're carried aboard) and fly them home;
    /// - air assault: pick up a rifle squad with a long way to go and put it down short
    ///   of its objective;
    /// - otherwise sit at base.
    /// Job: 0 idle, 1 pickup, 2 carry, 3 return, 4 medevac inbound, 5 loading casualties.
    /// </summary>
    void AirLift(Slot s, Vehicle v, double now)
    {
        v.ArriveRadius = 30f;
        switch (s.Job)
        {
            case 0:
            {
                v.AirMode = HeliMode.Land;
                v.Goal = s.Park;
                v.Boarding = false;
                if (v.Driver == null || !v.Landed) break;
                if (Casualties(s.Team) is Vector3 cas)
                {
                    s.Drop = FindLZ(cas, 50f);
                    s.Job = 4;
                    s.JobSince = now;
                    Comms.Say(v.Driver, "Medevac inbound, hold on!");
                    break;
                }
                Squad? best = null;
                float bestD = 420f;
                foreach (var cand in _m.Squads[s.Team])
                {
                    if (cand.Kind != SquadKind.Rifle || cand.Transport != null || cand.Objective == null || cand.Leader is not ICombatant lead) continue;
                    float fetch = lead.FeetPos.DistanceTo(v.GlobalPosition);
                    if (fetch > 900f * _m.Map.SizeScale) continue;
                    float d = (cand.Objective.Center - lead.FeetPos with { Y = cand.Objective.Center.Y }).Length() - fetch * 0.3f;
                    if (d > bestD) { bestD = d; best = cand; }
                }
                if (best == null) break;
                s.Cargo = best;
                best.Transport = v;
                s.Job = 1;
                s.JobSince = now;
                s.Drop = FindLZ(best.Leader!.FeetPos, 60f);
                AirAssaults++;
                Comms.Say(v.Driver, $"{best.Name}, bird's on the way to you. Mark an LZ!");
                break;
            }
            case 1: // fly to the squad, land, wait for them
            {
                var sq = s.Cargo;
                if (sq == null || sq.Objective == null || sq.Alive == 0) { Release(s); s.Job = 3; break; }
                v.AirMode = HeliMode.Land;
                v.Goal = s.Drop;
                if (!v.Landed || v.GlobalPosition.DistanceTo(s.Drop) > 40f) break;
                v.Boarding = true;
                int aboard = sq.Members.Count(m => m.Alive && m.Ride == v);
                int alive = sq.Members.Count(m => m.Alive && GodotObject.IsInstanceValid((GodotObject)m) && (m is Bot || m.FeetPos.DistanceTo(v.GlobalPosition) < 150f));
                if (s.JobSince < now - 200.0 || aboard >= alive || (aboard * 2 >= alive && now - s.JobSince > 120.0))
                {
                    if (aboard == 0) { Release(s); s.Job = 3; break; }
                    var c = sq.Objective.Center;
                    var from = (v.GlobalPosition - c) with { Y = 0f };
                    s.Drop = FindLZ(c + from.Normalized() * MathF.Max(250f, sq.Objective.Radius + 200f), 80f);
                    s.Job = 2;
                    s.JobSince = now;
                    v.Boarding = false;
                    Comms.Say(v.Driver ?? sq.Leader!, "Everyone's in. Lifting off!");
                }
                break;
            }
            case 2: // fly them in, land, unload
            {
                var sq = s.Cargo;
                v.AirMode = HeliMode.Land;
                v.Goal = s.Drop;
                if (sq == null) { s.Job = 3; break; }
                if (v.Landed && v.GlobalPosition.DistanceTo(s.Drop) < 40f)
                {
                    foreach (var o in v.Occupants.ToArray())
                        if (o is Bot b && b.Squad == sq) v.Leave(b);
                    if (v.Driver != null) Comms.Say(v.Driver, "LZ! Everybody out, go go go!");
                    Release(s);
                    s.Job = 3;
                }
                break;
            }
            case 3: // home
                v.AirMode = HeliMode.Land;
                v.Goal = s.Park;
                if (v.Landed && v.GlobalPosition.DistanceTo(s.Park) < 50f)
                {
                    // Anyone we brought back off the field gets out here.
                    foreach (var o in v.Occupants.ToArray())
                        if (o != null && v.Def.Seats[Array.IndexOf(v.Occupants, o)].Role == SeatRole.Passenger) v.Leave(o);
                    s.Job = 0;
                }
                break;
            case 4: // medevac: fly to the casualties
                v.AirMode = HeliMode.Land;
                v.Goal = s.Drop;
                if (v.Landed && v.GlobalPosition.DistanceTo(s.Drop) < 40f) { s.Job = 5; s.JobSince = now; }
                else if (now - s.JobSince > 240.0) s.Job = 3;
                break;
            case 5: // loading: every downed friendly nearby is stabilised and carried aboard
            {
                v.Goal = null;
                foreach (var c in Combatants.All.ToArray())
                {
                    if (!c.Downed || c.Team != s.Team || c.FeetPos.DistanceTo(v.GlobalPosition) > 70f) continue;
                    int seat = v.FreeSeat(SeatRole.Passenger);
                    if (seat < 0) break;
                    c.Heal(40f);
                    if (c.Alive) v.Enter(c, seat);
                    Evacuated++;
                }
                if (now - s.JobSince > 8.0)
                {
                    if (v.Driver != null) Comms.Say(v.Driver, "Wounded aboard. Heading home!");
                    s.Job = 3;
                }
                break;
            }
        }
    }

    public int Evacuated, AirAssaults;

    /// <summary>Somewhere with at least two of our wounded down, and no enemy we know of close by.</summary>
    Vector3? Casualties(int team)
    {
        foreach (var c in Combatants.All)
        {
            if (!c.Downed || c.Team != team || Clock.Now - c.Body.DownSince < 15.0) continue;
            int n = Combatants.All.Count(o => o.Downed && o.Team == team && o.FeetPos.DistanceTo(c.FeetPos) < 60f);
            if (n < 2) continue;
            if (Combatants.All.Any(e => e.Alive && e.Team != team && e.FeetPos.DistanceTo(c.FeetPos) < 150f)) continue;
            return c.FeetPos;
        }
        return null;
    }

    /// <summary>
    /// Gunship: attack runs on whatever its crew squad was sent to support: an enemy vehicle
    /// if one's known near there, else the thickest cluster of enemy sightings, else the point.
    /// </summary>
    void Gunship(Slot s, Vehicle v)
    {
        var obj = s.Crew?.Objective as PointObjective;
        if (obj == null || v.Driver == null) { v.AirMode = HeliMode.Land; v.Goal = s.Park; return; }
        v.AirMode = HeliMode.Attack;
        v.Goal = obj.Watch;
        Vector3? aim = null;
        var armor = Radio.Latest(s.Team, RadioKind.Armor, 45.0);
        if (armor != null && armor.Pos.DistanceTo(obj.Watch) < 450f && armor.Vehicle is { Destroyed: false } ev) aim = ev.Center;
        aim ??= Intel.Cluster(s.Team, obj.Watch, 0f, 450f, 40f, 2);
        v.AttackPoint = aim;
        // Out of rockets and bullets: home to rearm.
        if (v.Turrets.All(t => t.Loaded.Sum() + t.Stock.Sum() == 0)) { v.AirMode = HeliMode.Land; v.Goal = s.Park; }
    }

    /// <summary>
    /// The mortar: dug in behind the newest FOB (or at base). When a newer FOB goes up
    /// well forward of it, the team packs it up and it's set up again there a minute later.
    /// </summary>
    void MortarPit(Slot s, Vehicle v, double now)
    {
        var newest = Fob.All.LastOrDefault(f => f.Team == s.Team);
        if (newest == null || v.GlobalPosition.DistanceTo(newest.GlobalPosition) < 250f) return;
        if (v.Crewed) foreach (var o in v.Occupants.ToArray()) if (o != null) v.Leave(o);
        s.Park = newest.GlobalPosition + (newest.GlobalPosition - _m.Map.Bases[s.Team]).Normalized() * -25f;
        s.Live = null;
        s.RespawnAt = now + 60.0;
        if (s.Crew != null) s.Crew.Vehicle = null;
        v.QueueFree();
    }

    readonly Dictionary<Vehicle, double> _deadSince = new();

    /// <summary>
    /// A vehicle that can't move (or can neither move far nor fight) is a coffin: the crew
    /// bail out, and once it's sat empty a while it's written off (scuttled) so a
    /// replacement can come. That still costs the tickets.
    /// </summary>
    bool Abandon(Slot s, Vehicle v, double now)
    {
        bool useless = v.Immobile || (v.EngineHit && v.Hp < v.Def.Hp * 0.3f);
        if (!useless) { _deadSince.Remove(v); return false; }
        if (v.Crewed && (v.Turrets.Length == 0 || v.TurretDown.All(x => x) || now - v.LastHit < 1.0))
        {
            foreach (var o in v.Occupants.ToArray())
                if (o is Bot b) v.Leave(b);
            if (s.Cargo != null) { s.Cargo.Transport = null; s.Cargo = null; }
        }
        if (!_deadSince.TryGetValue(v, out var since)) _deadSince[v] = since = now;
        if (!v.Crewed && now - since > 60.0)
        {
            _deadSince.Remove(v);
            v.Damage(v.Hp + 1f, null);
            return true;
        }
        return false;
    }
}
