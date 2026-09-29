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
        public double FiringAt, LastHitSeen = -99, ScootUntil, RearmAt = -1, LastRequestAt = -99, CrewWaitSince = -1, CrewWaitFor;
        public Vector3 ScootTo;
        public bool Rearming;
        public int ShotsAtFiring;
        public double PickupRetryAt, PickupTimeout;
        // Boarding: since when it has been stopped with the doors open for them, and how the men still to get in are
        // coming on (the sum of their distances at its lowest, and when that last came down).
        public double BoardSince = -1, ClosingAt;
        public float ClosingBest;
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
        var def = VehicleDef.Get(s.Kind, s.Team);
        s.Park = ClearPark(s.Park, def);
        var v = new Vehicle { Def = def, Team = s.Team };
        _m.GetParent().AddChild(v);
        v.GlobalPosition = _m.Map.Ground(s.Park);
        v.Rotation = new Vector3(0f, s.Yaw, 0f);
        s.Live = v;
        s.Job = 0;
        s.HasSupplies = true;
        if (s.Crew != null) s.Crew.Vehicle = v;
    }

    /// <summary>
    /// Where a replacement parks: its own spot, unless a wreck is lying on it (the burnt-out one it replaces, written
    /// off on its pad), and then the nearest clear ground round it, which is its spot from then on. (Replacements
    /// used to appear inside the wreck, and their crew couldn't get round it to the doors.)
    /// </summary>
    Vector3 ClearPark(Vector3 park, VehicleDef def)
    {
        float room = def.Air ? def.RotorRadius + 7f : MathF.Max(def.Hull.X, def.Hull.Z) * 0.5f + 5f;
        bool Clear(Vector3 p) => !Vehicle.All.Any(o => GodotObject.IsInstanceValid(o) && o.Destroyed
                                                     && ((o.GlobalPosition - p) with { Y = 0f }).Length() < room + MathF.Max(o.Def.Hull.X, o.Def.Hull.Z) * 0.5f);
        if (Clear(park)) return park;
        for (int ring = 1; ring <= 4; ring++)
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4f + ring * 0.4f;
                var p = park + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * room * ring;
                if (MathF.Abs(p.X) > _m.Map.Half - 60f || MathF.Abs(p.Z) > _m.Map.Half - 60f) continue;
                if (_m.Map.NormalAt(p.X, p.Z).Y < (def.Air ? 0.94f : 0.85f)) continue;
                if (!def.Air && ((Valley.ClosestForVehicles(p) - p) with { Y = 0f }).Length() > 4f) continue;
                if (Clear(p)) return p with { Y = _m.Map.HeightAt(p.X, p.Z) };
            }
        return park;
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
                // (None once the side is out of reserves.)
                if (now >= s.RespawnAt && !_m.Out[s.Team] && !_m.Spent(s.Team)) Spawn(s);
                continue;
            }
            var v = s.Live;
            if (!GodotObject.IsInstanceValid(v) || v.Destroyed) continue;
            if (Abandon(s, v, now)) continue;
            // A crippled aircraft (see Vehicle.Useless) flies nothing more: home, low, and down, where it's written
            // off (Abandon). (One at a fifth of its hit points, its engine failing, pressed on with its attacks.) One
            // of its crew hit, likewise: home, where they're lifted out.
            if (v.Def.Air && v.Landed && v.GlobalPosition.DistanceTo(s.Park) < 60f) v.CrewLost = false;
            if (v.Def.Air && !v.Immobile && !v.Landed && (v.Useless || v.AircrewHit))
            {
                if ((v.AirMode != HeliMode.Land || v.Goal != s.Park) && v.Driver is Bot pb)
                    Comms.Say(pb, v.Useless ? "We're hit bad — going home." : "Crew hit — heading home.");
                v.AirMode = HeliMode.Land;
                v.Goal = s.Park;
                v.Task = v.Useless ? "crippled: going home" : "crew hit: going home";
                continue;
            }
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
                    s.BoardSince = -1;
                    if (now - s.JobSince > 150.0) Release(s); // can't get to them: they'll walk
                    break;
                }
                v.Goal = null;
                v.Boarding = true;
                // The clock starts once it has pulled up.
                if (s.BoardSince < 0 && MathF.Abs(v.Speed) > 0.5f) break;
                if (BoardingDone(s, v, sq, now, 20.0, 90.0, out int aboard))
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

    /// <summary>
    /// Stopped for a squad with the doors open: is it time to go? Everyone aboard, yes. Otherwise it waits, timed
    /// from when it stopped for them (not from when it was sent: the drive or the flight out isn't boarding time),
    /// for at least half of them, the squad leader among them, and for as long as those still out there keep coming
    /// on. Taking fire, it goes with whoever's in; and after <paramref name="cap"/> seconds it goes whatever, so a
    /// squad that won't come can't hold it for ever. (It went on a clock started at dispatch, with no look at who
    /// was still out: a Merlin that had been down a few seconds lifted off with three men sprinting at it from 10 m,
    /// and helicopters left squad leaders behind, their squads then walking back to them, away from the objective.)
    /// </summary>
    static bool BoardingDone(Slot s, Vehicle v, Squad sq, double now, double settle, double cap, out int aboard)
    {
        aboard = 0;
        int alive = 0, coming = 0;
        float sum = 0f;
        foreach (var m in sq.Members)
        {
            if (!m.Alive || !GodotObject.IsInstanceValid((GodotObject)m)) continue;
            if (m.Ride == v) { aboard++; alive++; continue; }
            float d = m.FeetPos.DistanceTo(v.GlobalPosition);
            // Bots, plus the player if they're close enough to be waited for.
            if (m is Bot || d < 150f) alive++;
            // Near enough to be making for it (a bot comes for a ride within 400 m; see BotBrain.Board).
            if (m.Ride == null && !m.Downed && d < (m is Bot ? 400f : 150f)) { coming++; sum += d; }
        }
        if (s.BoardSince < 0) { s.BoardSince = now; s.ClosingBest = float.MaxValue; s.ClosingAt = now; }
        if (sum < s.ClosingBest - 2f) { s.ClosingBest = sum; s.ClosingAt = now; }
        double waited = now - s.BoardSince;
        bool go;
        if (aboard >= alive || waited > cap) go = true;
        else if (now - v.LastHit < 3.0 && aboard > 0) go = true; // under fire: go with who's in
        else if (waited < settle || aboard * 2 < alive) go = false;
        else
        {
            var lead = sq.Leader;
            bool leaderOut = lead != null && lead.Ride != v && !lead.Downed && lead.FeetPos.DistanceTo(v.GlobalPosition) < (lead is Bot ? 400f : 150f);
            // Nobody out there has got any nearer for ten seconds: they aren't coming (in a fight, stuck, lost).
            bool stillComing = coming > 0 && now - s.ClosingAt < 10.0;
            go = !leaderOut && !stillComing;
        }
        if (go) s.BoardSince = -1;
        return go;
    }

    void Release(Slot s)
    {
        s.BoardSince = -1;
        if (s.Cargo != null) s.Cargo.Transport = null;
        s.Cargo = null;
        s.Job = 0;
        if (s.Live != null) s.Live.Boarding = false;
    }

    /// <summary>
    /// The logistics truck, driven by the logistics team:
    /// - loaded, to the FOB site the commander picked, where they get out and build;
    /// - empty, home to load up again (it takes a minute), and they get out there;
    /// - loaded with nothing to build, out to the drone team when they're running short of drones (only a
    ///   logistics truck or a FOB carries them), where it's left parked for the operators to restock from.
    /// Job: 0 parked, 1 drone run. (The empty truck used to count as reloaded the moment it stopped anywhere: the
    /// flag it had arrived at the FOB site was still set, so it was full again as the FOB went up, and the second
    /// FOB was ordered seconds after the first. Short of that it reloaded by standing by a FOB for two minutes. And
    /// it never took drones anywhere: the team walked to the drone team, and the drones stayed on the truck at base.)
    /// </summary>
    void Logistics(Slot s, Vehicle v, double now)
    {
        v.ArriveRadius = 8f;
        var sq = s.Crew;
        if (sq == null) { v.Goal = null; return; }
        if (!s.HasSupplies)
        {
            s.Job = 0;
            bool home = ((v.GlobalPosition - s.Park) with { Y = 0f }).Length() < 30f;
            if (!home)
            {
                sq.TruckRun = true;
                v.Goal = v.Driver != null ? s.Park : null;
                v.Task = "empty: home to load up";
                s.JobSince = 0;
                return;
            }
            if (sq.TruckRun) { sq.TruckRun = false; LetOut(v); }
            v.Goal = null;
            v.Task = "loading";
            if (s.JobSince <= 0) s.JobSince = now;
            if (now - s.JobSince > 60.0) { s.HasSupplies = true; s.JobSince = 0; }
            return;
        }
        var site = sq.FobSite;
        if (site == null)
        {
            DroneRun(s, v, sq, now);
            return;
        }
        if (s.Job == 1) { s.Job = 0; s.JobSince = 0; }
        v.Goal = sq.FobBuildStart < 0 ? site : null;
        v.Task = "supplies forward for a FOB";
        sq.TruckRun = sq.FobBuildStart < 0;
        // Can't get there (stuck, blocked): put it down where we are, if that's forward enough.
        if (v.Driver != null && sq.FobBuildStart < 0)
        {
            if (s.JobSince <= 0) s.JobSince = now;
            if (now - s.JobSince > 150.0 && v.GlobalPosition.DistanceTo(_m.Map.Bases[s.Team]) > 250f)
            {
                sq.FobSite = site = v.GlobalPosition;
                v.Goal = site;
            }
        }
        else s.JobSince = 0;
        if (site is Vector3 fs && v.Arrived && ((fs - v.GlobalPosition) with { Y = 0f }).Length() < v.ArriveRadius + 5f && v.Driver != null)
        {
            // Unload and build: the team gets out; the FOB goes up once they've worked on it.
            if (sq!.FobBuildStart < 0)
            {
                sq.FobBuildStart = now;
                sq.TruckRun = false;
                // Beside the truck, on the side towards home, not on top of it. (It went up where the truck stood,
                // and the sandbag ring round it boxed the truck in: it never got out again, and anything driving
                // past got caught on the two of them.)
                var side = (v.GlobalBasis.X with { Y = 0f }).Normalized();
                if (side.Dot(_m.Map.Bases[s.Team] - v.GlobalPosition) < 0f) side = -side;
                var beside = v.GlobalPosition + side * (v.Def.Hull.X * 0.5f + 11f);
                var foot = Valley.ClosestOnFoot(v.GetWorld3D(), beside);
                sq.FobSite = ((foot - beside) with { Y = 0f }).Length() < 4f ? foot : beside;
                Comms.Say(sq.Leader ?? v.Driver!, "Building a FOB here!");
                LetOut(v);
            }
        }
        site = sq.FobSite;
        if (sq.FobBuildStart > 0 && now - sq.FobBuildStart > 25.0 && site is Vector3 at)
        {
            _m.BuildFob(s.Team, at);
            sq.FobSite = null;
            sq.FobBuildStart = -1;
            s.HasSupplies = false;
            s.JobSince = 0;
        }
    }

    static void LetOut(Vehicle v)
    {
        foreach (var o in v.Occupants.ToArray()) if (o is Bot b) v.Leave(b);
    }

    /// <summary>A drone team of ours with an operator running short, and no FOB near them to restock from.</summary>
    Squad? ShortOfDrones(int team)
    {
        foreach (var d in _m.Squads[team])
        {
            if (d.Kind != SquadKind.Drone || d.Leader is not ICombatant lead) continue;
            if (!d.Members.Any(m => m is Bot { Alive: true, Ops: { } ops } && ops.StockLevel < 0.5f)) continue;
            if (Fob.All.Any(f => f.Team == team && f.GlobalPosition.DistanceTo(lead.FeetPos) < 400f)) continue;
            return d;
        }
        return null;
    }

    /// <summary>
    /// Loaded, and no FOB to build: drones out to a drone team that's short of them. The team drives it up to them,
    /// parks it as close as a vehicle gets, and gets out; it stays there for the operators (and anyone short of
    /// ammunition) to restock from until it's wanted for a FOB.
    /// </summary>
    void DroneRun(Slot s, Vehicle v, Squad sq, double now)
    {
        if (s.Job != 1)
        {
            v.Goal = null;
            if (sq.TruckRun) { sq.TruckRun = false; LetOut(v); }
            if (now < s.PickupRetryAt) return; // give the last lot time to walk over to it
            var d = ShortOfDrones(s.Team);
            if (d?.Leader is not ICombatant dl || dl.FeetPos.DistanceTo(v.GlobalPosition) < 120f) return;
            s.Job = 1;
            s.Cargo = d;
            s.JobSince = now;
            s.Drop = Valley.ClosestForVehicles(dl.FeetPos);
            v.Arrived = false;
            if (DuelMode.Verbose) GD.Print($"[{now:0}s] {v.Def.Name} ({KothMode.TeamNames[s.Team]}): drone run to {d.Name}, {s.Drop.DistanceTo(v.GlobalPosition):0} m");
            return;
        }
        var to = s.Cargo;
        sq.TruckRun = true;
        v.Goal = s.Drop;
        v.Task = $"drones up to {to?.Name}";
        // The team walking back to it doesn't count against the run: only the drive.
        if (v.Driver == null) { s.JobSince = now; return; }
        bool there = v.Arrived && ((s.Drop - v.GlobalPosition) with { Y = 0f }).Length() < v.ArriveRadius + 5f;
        if (there || to == null || to.Alive == 0 || now - s.JobSince > 300.0)
        {
            if (DuelMode.Verbose) GD.Print($"[{now:0}s] {v.Def.Name} ({KothMode.TeamNames[s.Team]}): drone run {(there ? "there" : "given up")}, {(to?.Leader is ICombatant l ? l.FeetPos.DistanceTo(v.GlobalPosition) : 0f):0} m from {to?.Name}");
            if (there && v.Driver != null && to?.Leader != null) Comms.Say(v.Driver, $"{to.Name}, drones and batteries on the truck — come and get them.");
            s.Job = 0;
            s.Cargo = null;
            s.PickupRetryAt = now + 180.0;
            sq.TruckRun = false;
            v.Goal = null;
            LetOut(v);
        }
    }

    public Slot? SlotOf(Vehicle v) => Slots.Find(s => s.Live == v);

    // ================================================================ aircraft

    /// <summary>
    /// A clear, flat landing zone near a point: level ground, and nothing solid (houses,
    /// trees, rocks) within reach of the rotor.
    /// </summary>
    /// <summary>
    /// Somewhere near <paramref name="near"/> to put a helicopter down: flat, and open ground all round out past the
    /// rotor's reach (<paramref name="rotor"/>, its radius), trees and walls included, since a blade that touches
    /// anything is the end of the aircraft. Further out if there's nowhere close; failing everywhere, the least
    /// obstructed spot found. (It used to check four points 9 m out, square to the map, and failing those land
    /// on the point asked for, in the middle of a village if that's where it was.)
    /// </summary>
    public Vector3 FindLZ(Vector3 near, float within = 80f, float rotor = 9f)
    {
        var space = _m.Map.GetWorld3D().DirectSpaceState;
        var rng = new RandomNumberGenerator();
        rng.Randomize();
        Vector3 best = _m.Map.Ground(near), fallback = best;
        float bestScore = float.MinValue;
        int fallbackClear = -1;
        float reach = rotor + 4f;
        for (int wide = 1; wide <= 3 && bestScore == float.MinValue; wide++)
        {
            float w = within * wide;
            for (int k = 0; k < 24; k++)
            {
                var p = near + new Vector3(rng.RandfRange(-w, w), 0f, rng.RandfRange(-w, w));
                if (MathF.Abs(p.X) > _m.Map.Half - 120f || MathF.Abs(p.Z) > _m.Map.Half - 120f) continue;
                if (_m.Map.NormalAt(p.X, p.Z).Y < 0.94f) continue;
                p.Y = _m.Map.HeightAt(p.X, p.Z);
                // The middle, then two rings (half the disc and past its edge), eight ways round.
                int clear = 0;
                for (int i = 0; i < 17; i++)
                {
                    var o = i == 0 ? Vector3.Zero : Vector3.Forward.Rotated(Vector3.Up, (i - 1) % 8 * MathF.PI / 4f) * (i <= 8 ? reach * 0.5f : reach);
                    var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p + o + Vector3.Up * 40f, p + o + Vector3.Down * 2f, Layers.World | Layers.Trees));
                    if (hit.Count > 0 && hit["collider"].AsGodotObject() is Node n && n.IsInGroup("ground") && !n.IsInGroup("floor")) clear++;
                }
                if (clear < 17)
                {
                    if (clear > fallbackClear) { fallbackClear = clear; fallback = p; }
                    continue;
                }
                float score = -p.DistanceTo(near) * 0.2f + rng.RandfRange(0f, 3f);
                if (score > bestScore) { bestScore = score; best = p; }
            }
        }
        if (bestScore > float.MinValue) return best;
        Prof.Count("lz:none clear");
        return fallbackClear >= 0 ? fallback : best;
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
                if (Casualties(s.Team) is Vector3 cas && !AirDefenceOnRoute(s.Team, v.GlobalPosition, cas))
                {
                    s.Drop = FindLZ(cas, 50f, v.Def.RotorRadius);
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
                // Not into the enemy's air defence (an LZ within reach of a known anti-aircraft gun): they'll walk.
                if (best == null || AirDefenceOnRoute(s.Team, v.GlobalPosition, best.Objective!.Center)) break;
                s.Cargo = best;
                best.Transport = v;
                s.Job = 1;
                s.JobSince = now;
                s.Drop = FindLZ(best.Leader!.FeetPos, 60f, v.Def.RotorRadius);
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
                if (!v.Landed || v.GlobalPosition.DistanceTo(s.Drop) > 40f)
                {
                    s.BoardSince = -1;
                    // Never got down to them (no LZ it could reach, or held off): they'll walk.
                    if (now - s.JobSince > 200.0) { Release(s); s.Job = 3; }
                    break;
                }
                v.Boarding = true;
                if (BoardingDone(s, v, sq, now, 20.0, 90.0, out int aboard))
                {
                    if (aboard == 0) { Release(s); s.Job = 3; break; }
                    var c = sq.Objective.Center;
                    var from = (v.GlobalPosition - c) with { Y = 0f };
                    s.Drop = FindLZ(c + from.Normalized() * MathF.Max(250f, sq.Objective.Radius + 200f), 80f, v.Def.RotorRadius);
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

    /// <summary>
    /// The gun seat's empty and a crewman is on his feet and on his way to it: wait for him, long enough for him to
    /// walk it from where he was when the wait began. (It used to wait 40 s, and only for one within 400 m: a
    /// replacement whose gunner was walking back from where the last one came down went without him, or, its
    /// pilot away too, never went at all.)
    /// </summary>
    bool CrewComing(Slot s, Vehicle v, double now) => CrewComing(s, v, now, out _);

    /// <param name="who">The nearest of them, the one it's waiting for.</param>
    bool CrewComing(Slot s, Vehicle v, double now, out ICombatant who)
    {
        float d = float.MaxValue;
        who = null!;
        if (v.GunnerSeat >= 0 && v.Occupants[v.GunnerSeat] == null && s.Crew != null)
            foreach (var m in s.Crew.Members)
                if (m.Alive && !m.Downed && m.Ride == null && GodotObject.IsInstanceValid((GodotObject)m))
                {
                    float md = m.FeetPos.DistanceTo(v.GlobalPosition);
                    if (md < d) { d = md; who = m; }
                }
        if (d > 2500f) { s.CrewWaitSince = -1; return false; }
        if (s.CrewWaitSince < 0) { s.CrewWaitSince = now; s.CrewWaitFor = 40.0 + d / 2f; }
        return now - s.CrewWaitSince < s.CrewWaitFor;
    }

    /// <summary>How far round a known anti-aircraft vehicle aircraft keep away: most of its guns' reach.</summary>
    const float AirDefenceReach = 2500f;

    /// <summary>Would a flight from here to there come within reach of the enemy's known air defence?</summary>
    static bool AirDefenceOnRoute(int team, Vector3 from, Vector3 to) =>
        Radio.AirDefenceNear(team, to, AirDefenceReach) != null || Radio.AirDefenceNear(team, (from + to) * 0.5f, AirDefenceReach) != null;

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
        // Rockets gone and the gun low (or everything gone): home to rearm. The ground crew hang new pods
        // and belts once it's down on its pad; before this, a gunship that had shot itself dry sat there for good.
        int pods = Array.FindIndex(v.Def.Turrets.ToArray(), t => t.Fixed);
        bool rocketsGone = pods < 0 || v.Turrets[pods].Loaded.Sum() + v.Turrets[pods].Stock.Sum() == 0;
        static int Rounds(Vehicle.TurretState t) => Enumerable.Range(0, t.Def.Ammo.Length).Sum(i => t.Loaded[i] + t.Stock[i] * t.Def.Ammo[i].Mag);
        bool gunLow = v.Turrets.Where(t => !t.Def.Fixed).All(t => Rounds(t) * 3 < t.Def.Ammo.Sum(a => a.Mag * (a.Mags + 1)));
        if (!s.Rearming && rocketsGone && gunLow) { s.Rearming = true; s.RearmAt = -1; Comms.Say(v.Driver, "Winchester — heading home to rearm."); }
        // The pods jammed (a hit in the works): nothing to attack with but the gun. Home for the armourers to clear them.
        if (!s.Rearming && pods >= 0 && v.TurretDown[pods]) { s.Rearming = true; s.RearmAt = -1; Comms.Say(v.Driver, "Pods are jammed — heading home."); }
        if (s.Rearming)
        {
            v.AirMode = HeliMode.Land;
            v.Goal = s.Park;
            v.Task = "home to rearm";
            if (!v.Landed || v.GlobalPosition.DistanceTo(s.Park) > 50f) return;
            if (s.RearmAt < 0) s.RearmAt = Clock.Now + 45.0;
            if (Clock.Now < s.RearmAt) return;
            v.Restock();
            Array.Fill(v.TurretDown, false);
            s.Rearming = false;
            Rearms++;
            Comms.Say(v.Driver, "Rearmed and refuelled. Back on station.");
        }
        // Enemy air defence known near the target: the gunship works from battle positions out of its sight (see
        // HeliPilot.BattlePosition), and goes after it with its missiles. With the missiles gone and nowhere to
        // work from for half a minute, that airspace is theirs until something kills the gun: hold off. (Gunships
        // used to fly straight in and be shot down within half a minute of every start.)
        if (v.NoPositionSince > 0 && Clock.Now - v.NoPositionSince > 30.0 && v.MissilesLeft == 0 && Radio.AirDefenceNear(s.Team, obj.Watch, AirDefenceReach) is { } aaRep)
        {
            if (v.AirMode == HeliMode.Attack && v.Driver is Bot) Comms.Say(v.Driver, $"Enemy anti-air near the target ({aaRep.Vehicle!.Def.Name}) — nowhere to work from, holding off.");
            v.AirMode = HeliMode.Land;
            v.Goal = s.Park;
            v.Task = "holding off: enemy air defence";
            v.BattlePos = null;
            v.NoPositionSince = -1;
            return;
        }
        // The whole crew aboard before lifting off: without the front-seater there's no gun, and nobody to take
        // the controls if the pilot's hit (one flew off alone, bleeding, passed out at 60 m and came down).
        if (v.Landed && CrewComing(s, v, Clock.Now)) { v.AirMode = HeliMode.Land; v.Goal = v.GlobalPosition; v.Task = "waiting for the gunner"; return; }
        v.AirMode = HeliMode.Attack;
        v.Goal = obj.Watch;
        Vector3? aim = null;
        var armor = Radio.Latest(s.Team, RadioKind.Armor, 45.0);
        // Missiles: first for an air defence gun we know of anywhere near where we're working (it's what kills
        // gunships, and from beyond its guns' 3 km, where the map allows, it can't shoot back), then for armour
        // near the objective. Rockets and the gun for everything else.
        if (v.MissileFocus is { } old && (old.Destroyed || !GodotObject.IsInstanceValid(old))) v.MissileFocus = null;
        if (v.MissilesLeft > 0)
        {
            if (v.MissileFocus is not { Def.Kind: VKind.SPAA })
                foreach (var r in Radio.AirDefences(s.Team))
                    if (r.Vehicle is { Destroyed: false } av && GodotObject.IsInstanceValid(av) && av.Center.DistanceTo(obj.Watch) < 4000f)
                    {
                        if (v.MissileFocus != av && v.Driver is Bot pb) Comms.Say(pb, $"Going after their {av.Def.ClassName} with missiles.");
                        v.MissileFocus = av;
                        v.BattlePos = null;
                        break;
                    }
            // Armour only: the missiles are for what rockets and the gun can't kill. (Half a load went on logistics trucks.)
            if (v.MissileFocus == null && armor?.Vehicle is { Destroyed: false, Def.Heavy: true } mv && GodotObject.IsInstanceValid(mv) && !mv.Def.Air && armor.Pos.DistanceTo(obj.Watch) < 1200f)
                v.MissileFocus = mv;
        }
        else v.MissileFocus = null;
        if (v.MissileFocus is { } focus) aim = focus.Center;
        else if (armor != null && armor.Pos.DistanceTo(obj.Watch) < 450f && armor.Vehicle is { Destroyed: false } ev) aim = ev.Center;
        aim ??= Intel.Cluster(s.Team, obj.Watch, 0f, 450f, 40f, 2);
        v.AttackPoint = aim;
    }

    /// <summary>
    /// The mortar: dug in behind the newest FOB (or at base). When a newer FOB goes up
    /// well forward of it, the team packs it up and it's set up again there a minute later.
    /// </summary>
    void MortarPit(Slot s, Vehicle v, double now)
    {
        // Out of bombs: more are carried up from the FOB's cache or from base (the pit is always by one).
        var tube = v.Turrets[0];
        if (tube.Loaded.Sum() + tube.Stock.Sum() == 0)
        {
            if (s.RearmAt < 0) s.RearmAt = now + 90.0;
            else if (now >= s.RearmAt) { v.Restock(); s.RearmAt = -1; Rearms++; }
        }
        var newest = Fob.All.LastOrDefault(f => f.Team == s.Team);
        if (newest == null || v.GlobalPosition.DistanceTo(newest.GlobalPosition) < 250f) return;
        if (v.Crewed) foreach (var o in v.Occupants.ToArray()) if (o != null) v.Leave(o);
        // Behind the FOB, on ground its crew can walk to. (It was put down 25 m back whatever was there, and the crew
        // walking to it from base could spend minutes stuck on the way.)
        var pit = newest.GlobalPosition + (newest.GlobalPosition - _m.Map.Bases[s.Team]).Normalized() * -25f;
        var onFoot = Valley.ClosestOnFoot(v.GetWorld3D(), pit);
        s.Park = ((onFoot - pit) with { Y = 0f }).Length() < 15f ? onFoot : pit;
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
        if (!v.Useless) { _deadSince.Remove(v); return false; }
        // A crippled aircraft: nobody gets out in the air (the pilot puts it down: see HeliPilot); on the ground,
        // everyone gets out: it can't do its job and it's a big target. (Before, a tail rotor shot out in flight had
        // the crew stepping out at 80 m, and the empty helicopter flew on into a hillside.) A ground vehicle
        // that can still fight is fought from until it's hit again.
        bool bail = v.Def.Air ? v.Landed : v.Turrets.Length == 0 || v.TurretDown.All(x => x) || now - v.LastHit < 1.0;
        if (v.Crewed && bail)
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
