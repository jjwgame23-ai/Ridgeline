namespace Ridgeline;

/// <summary>
/// The abstract war on an island: every soldier, unit and vehicle of the three armies, and the time. Nothing here is
/// a Godot node. It runs headless, many times faster than real time, and the part round the player will later be
/// handed to the full simulation.
/// </summary>
public sealed class War
{
    public readonly Island Isl;
    public readonly int Seed;
    public readonly List<Unit> Units = new();
    public Soldier[] Soldiers = new Soldier[1 << 17];
    public int SoldierCount;
    public readonly List<WarVehicle> Vehicles = new();
    public readonly Side[] Sides = new Side[3];
    /// <summary>Seconds since the war began.</summary>
    public double Time;
    /// <summary>Where progress notes go (headless runs).</summary>
    public Action<string>? Note;
    public readonly List<Objective> Objectives = new();
    public MoveGrid Move = null!;
    public Territory Ctl = null!;
    public readonly List<(double T, int Side, string Text, float X, float Z)> Events = new();
    public readonly List<Fight> Fights = new();
    public readonly WarIntel Intel = new();
    public readonly Random Rng;
    /// <summary>Each side's dead and evacuated wounded, all told.</summary>
    public readonly int[] Dead = new int[3], Evacuated = new int[3];
    /// <summary>Each side's soldiers hit, and killed, by cause.</summary>
    public readonly int[,] Hits = new int[3, 10], Kills = new int[3, 10];
    /// <summary>Each side's vehicles destroyed, by cause.</summary>
    public readonly int[,] Wrecked = new int[3, 10];
    /// <summary>A fight to trace (trace=N): its state every 30 s, for reading how fights go.</summary>
    public int Trace = -1;
    public readonly List<string> TraceLines = new();
    /// <summary>Soldiers hit, by the range they were hit from: under 25, 50, 100, 200, 400, 800 m, and further.</summary>
    public readonly int[] HitsAt = new int[7];

    /// <summary>The hour of the day: the war starts at 06:00 on day 1.</summary>
    public double Hour => (6.0 + Time / 3600.0) % 24.0;
    public int Day => 1 + (int)((6.0 + Time / 3600.0) / 24.0);
    /// <summary>
    /// Units march by day, 05:30 to 20:30, and rest at night where they are. (Night marches come with fighting:
    /// doctrine's night rates are about two thirds of the day's.)
    /// </summary>
    public bool MarchingHours => Hour >= 5.5 && Hour < 20.5;

    public War(Island isl, int seed)
    {
        Isl = isl;
        Seed = seed;
        Rng = new Random(seed * 7919 + 13);
        for (int s = 0; s < 3; s++) Sides[s] = new Side { Index = s, Name = KothMode.TeamNames[s] };
    }

    public Unit AddUnit(int side, Echelon e, Arm a, Unit? parent)
    {
        var u = new Unit { Id = Units.Count, Side = side, Echelon = e, Arm = a, Parent = parent?.Id ?? -1 };
        Units.Add(u);
        return u;
    }

    public int AddSoldier(Soldier s)
    {
        if (SoldierCount == Soldiers.Length) Array.Resize(ref Soldiers, Soldiers.Length * 2);
        Soldiers[SoldierCount] = s;
        return SoldierCount++;
    }

    public int AddVehicle(WarVehicle v)
    {
        Vehicles.Add(v);
        return Vehicles.Count - 1;
    }

    /// <summary>Everyone in a unit and all its children.</summary>
    public IEnumerable<int> Everyone(Unit u)
    {
        foreach (int m in u.Members) yield return m;
        foreach (int c in u.Children)
            foreach (int m in Everyone(Units[c])) yield return m;
    }

    public IEnumerable<Unit> Below(Unit u)
    {
        foreach (int c in u.Children)
        {
            yield return Units[c];
            foreach (var d in Below(Units[c])) yield return d;
        }
    }

    public string Who(int soldier) => People.Full(Soldiers[soldier].Side, Seed, soldier, Soldiers[soldier].Rank);

    /// <summary>Make the three armies and put each in its starting area.</summary>
    public void Raise(Random rng)
    {
        for (int s = 0; s < 3; s++)
        {
            Sides[s].Army = Orbat.Build(this, s, rng).Id;
            if (s < Isl.Starts.Count) Sides[s].Port = Isl.Starts[s].Port;
            Note?.Invoke($"war: raised {Sides[s].Name}, {SoldierCount:N0} soldiers so far");
        }
        // A unit with no members of its own (a battalion is its companies) is commanded from its headquarters child.
        foreach (var u in Units)
            if (u.Commander < 0)
                u.Commander = Below(u).Where(c => c.Commander >= 0).Select(c => c.Commander).OrderByDescending(m => Soldiers[m].Rank).FirstOrDefault(-1);
        Note?.Invoke("war: commanders found");
        Deployment.Place(this, rng);
        Prepare();
        Note?.Invoke("war: deployed");
    }

    /// <summary>
    /// Who moves on their own, and how. Companies (and batteries, troops, squadrons) move, and so do headquarters with
    /// staff of their own. Platoons and squads go with their company. A battalion with no headquarters of its own is
    /// just its parts. A mover rides if its vehicles have seats for three in four of its people, on tracks if any of
    /// them are tracked; otherwise it walks. Armour is on hand at about 75%, and a unit short of seats cross-loads and
    /// leaves the rest with its trains. (At nine in ten, a mechanised company missing one Bradley in four walked.)
    /// </summary>
    void Prepare()
    {
        foreach (var u in Units)
        {
            var parent = u.Parent >= 0 ? Units[u.Parent] : null;
            if (u.Echelon == Echelon.Company || (u.Echelon >= Echelon.Battalion && u.Members.Count > 0)) u.Mover = u.Id;
            else if (u.Echelon >= Echelon.Battalion) u.Mover = -1;
            else if (parent == null || parent.Mover < 0) u.Mover = u.Id;
            else u.Mover = parent.Mover;
        }
        var seats = new int[Units.Count];
        var tracks = new bool[Units.Count];
        foreach (var u in Units)
        {
            if (u.Mover < 0) continue;
            var m = Units[u.Mover];
            if (m != u)
            {
                u.OffX = u.X - m.X;
                u.OffZ = u.Z - m.Z;
            }
            m.People += u.Members.Count;
            m.Carries.Add(u.Id);
            foreach (int vi in u.Vehicles)
            {
                var v = Vehicles[vi];
                if (v.Class is VClass.Helicopter or VClass.Gunship) continue;
                seats[m.Id] += MoveGrid.Seats(v.Class) + Crew(v.Class);
                tracks[m.Id] |= MoveGrid.TrackedClass(v.Class);
                m.Armed |= v.Class is VClass.Ifv or VClass.Tank or VClass.LightTank or VClass.Apc or VClass.Ltv;
            }
        }
        foreach (var u in Units)
            u.Raised = u.People;
        foreach (var u in Units)
            if (u.IsMover) u.Mob = seats[u.Id] >= u.People * 0.75f && seats[u.Id] > 0 ? (tracks[u.Id] ? Mobility.Tracked : Mobility.Wheeled) : Mobility.Foot;
        Ctl = new Territory(Isl);
        Move = new MoveGrid(Isl);
        Command.Objectives(this);
        Ctl.Update(this);
    }

    static int Crew(VClass v) => v switch
    {
        VClass.Tank => 4, VClass.Ifv => 3, VClass.Apc => 2, VClass.Howitzer => 5, VClass.Rocket => 3, VClass.Spaa => 3, VClass.Mortar => 4,
        VClass.Engineer => 2, VClass.LightTank => 4, _ => 1,
    };

    /// <summary>Fit soldiers a mover carries.</summary>
    public int Fit(Unit u) => u.People;

    /// <summary>Count a mover's fit soldiers again (the lightly wounded among them).</summary>
    public void Recount(Unit m)
    {
        int n = 0;
        foreach (int c in m.Carries)
            foreach (int s in Units[c].Members)
                if (Soldiers[s].State is SoldierState.Fit or SoldierState.Wounded) n++;
        m.People = n;
    }

    /// <summary>Set a mover off toward a point: false if there's no way there for it.</summary>
    public bool StartMove(Unit u, float x, float z)
    {
        int to = Move.CellOf(x, z);
        var path = Move.Path(Move.CellOf(u.X, u.Z), to, u.Mob, u.Side);
        if (path == null) return false;
        u.Path = path;
        u.PathAt = 0;
        bool exact = path[^1] == to;
        u.GoX = exact ? x : Move.CX(path[^1]);
        u.GoZ = exact ? z : Move.CZ(path[^1]);
        return true;
    }

    /// <summary>
    /// Advance the war by <paramref name="dt"/> seconds: movers march along their paths, by day. A day's march is at
    /// most 8 hours on foot, the most of a normal march day (FM 3-21.18), or 10 hours at the wheel (FM 55-30's drivers'
    /// limit). After that a unit halts until the next day.
    /// </summary>
    public void Step(double dt)
    {
        int day = Day;
        Time += dt;
        if (Day != day)
            foreach (var u in Units) u.MovedToday = 0;
        foreach (var u in Units)
        {
            u.WasX = u.X;
            u.WasZ = u.Z;
        }
        if (!MarchingHours) return;
        foreach (var u in Units)
        {
            if (u.Path == null || u.InFight >= 0) continue;
            double budget = (u.Mob == Mobility.Foot ? 8 : 10) * 3600.0 - u.MovedToday;
            if (budget <= 0) continue;
            float seconds = (float)Math.Min(dt, budget);
            u.MovedToday += seconds;
            Advance(u, seconds);
        }
    }

    void Advance(Unit u, float seconds)
    {
        while (seconds > 0f && u.Path != null)
        {
            bool last = u.PathAt >= u.Path.Count - 1;
            float tx = last ? u.GoX : Move.CX(u.Path[u.PathAt + 1]), tz = last ? u.GoZ : Move.CZ(u.Path[u.PathAt + 1]);
            float sp = Move.Speed(Move.CellOf(u.X, u.Z), u.Mob);
            if (sp <= 0f) sp = Move.Speed(u.Path[Math.Min(u.PathAt + 1, u.Path.Count - 1)], u.Mob);
            if (sp <= 0f) sp = 0.5f;
            float dx = tx - u.X, dz = tz - u.Z, d = MathF.Sqrt(dx * dx + dz * dz), step = sp * seconds;
            if (step >= d)
            {
                u.X = tx;
                u.Z = tz;
                seconds -= d / sp;
                u.Marched += d;
                u.MarchedToday += d;
                if (!last)
                {
                    u.PathAt++;
                    continue;
                }
                u.Path = null;
                u.HaltedAt = Time;
                if (u.Order != null) u.Order.Done = true;
            }
            else
            {
                u.X += dx / d * step;
                u.Z += dz / d * step;
                u.Marched += step;
                u.MarchedToday += step;
                seconds = 0f;
            }
        }
    }

    /// <summary>Who holds the ground now, and which objectives that changes hands.</summary>
    public void UpdateControl()
    {
        Ctl.Update(this);
        Move.UpdateDanger(Ctl);
        foreach (var o in Objectives)
        {
            int owner = Ctl.Owner[Ctl.CellOf(o.X, o.Z)];
            if (owner == o.Owner) continue;
            o.Owner = owner;
            if (owner >= 0 && (o.Kind != ObjKind.Town || o.Value >= 1.6f))
                Events.Add((Time, owner, $"{Sides[owner].Name} takes {o.Name}", o.X, o.Z));
        }
    }

    /// <summary>A battalion has an objective in hand.</summary>
    public void Taken(Objective o, Unit by)
    {
        o.Owner = by.Side;
        Events.Add((Time, by.Side, $"{Sides[by.Side].Name} takes {o.Name} ({by.Short})", o.X, o.Z));
    }
}

/// <summary>
/// Where each army stands at the start: in its starting area round its port, the way a lodgement is laid out.
/// - Army troops (headquarters, sustainment, the big guns) are near the port, in the third of the area closest to it.
/// - Each division takes a sector of the rest, left, centre and right of the way inland. Its brigades share the
///   sector out, with their fighting battalions in the outer half and their support behind.
/// - A battalion's companies are spread within about 1.5 km of it, platoons within 400 m of their company, and
///   squads within 150 m of their platoon. Nothing stands in the sea or on ground steeper than 15°.
/// </summary>
public static class Deployment
{
    public static void Place(War war, Random rng)
    {
        var isl = war.Isl;
        foreach (var side in war.Sides)
        {
            if (side.Port < 0) continue;
            var port = isl.Towns[side.Port];
            var cells = new List<int>();
            for (int i = 0; i < isl.Count; i++) if (isl.StartOf[i] == side.Index && !isl.Water(i) && isl.Slope[i] < 15f) cells.Add(i);
            if (cells.Count == 0) continue;
            // The way inland: from the port to the middle of the area.
            float mx = (float)cells.Average(i => isl.X(i)), mz = (float)cells.Average(i => isl.Z(i));
            float inland = MathF.Atan2(mz - port.Z, mx - port.X);
            float Dist(int i) => MathF.Sqrt((isl.X(i) - port.X) * (isl.X(i) - port.X) + (isl.Z(i) - port.Z) * (isl.Z(i) - port.Z));
            float Bearing(int i)
            {
                float a = MathF.Atan2(isl.Z(i) - port.Z, isl.X(i) - port.X) - inland;
                while (a > MathF.PI) a -= MathF.Tau;
                while (a < -MathF.PI) a += MathF.Tau;
                return a;
            }
            var byDist = cells.OrderBy(Dist).ToList();
            var rear = byDist.Take(byDist.Count / 3).ToList();
            var forward = byDist.Skip(byDist.Count / 3).OrderBy(Bearing).ToList();

            var army = war.Units[side.Army];
            Put(war, army, port.X, port.Z);
            var divisions = army.Children.Select(c => war.Units[c]).Where(c => c.Echelon == Echelon.Division).ToList();
            foreach (var c in army.Children.Select(c => war.Units[c]).Where(c => c.Echelon != Echelon.Division))
                PlaceUnder(war, c, rear, rng);
            for (int d = 0; d < divisions.Count; d++)
            {
                var sector = Slice(forward, d, divisions.Count).OrderBy(Dist).ToList();
                var div = divisions[d];
                var divRear = sector.Take(sector.Count / 3).ToList();
                var divFront = sector.Skip(sector.Count / 3).OrderBy(Bearing).ToList();
                int hq = divRear.Count > 0 ? divRear[rng.Next(divRear.Count)] : sector[0];
                Put(war, div, isl.X(hq), isl.Z(hq));
                var brigades = div.Children.Select(c => war.Units[c]).Where(c => c.Echelon == Echelon.Brigade && IsManoeuvre(c)).ToList();
                foreach (var c in div.Children.Select(c => war.Units[c]).Where(c => !brigades.Contains(c)))
                    PlaceUnder(war, c, divRear.Count > 0 ? divRear : sector, rng);
                for (int b = 0; b < brigades.Count; b++)
                {
                    var zone = Slice(divFront, b, brigades.Count).OrderBy(Dist).ToList();
                    if (zone.Count == 0) zone = sector;
                    var bde = brigades[b];
                    var inner = zone.Take(zone.Count / 2).ToList();
                    var outer = zone.Skip(zone.Count / 2).ToList();
                    int bhq = inner.Count > 0 ? inner[rng.Next(inner.Count)] : zone[0];
                    Put(war, bde, isl.X(bhq), isl.Z(bhq));
                    foreach (var c in bde.Children.Select(c => war.Units[c]))
                        PlaceUnder(war, c, IsManoeuvre(c) && outer.Count > 0 ? outer : inner.Count > 0 ? inner : zone, rng);
                }
            }
        }
    }

    static bool IsManoeuvre(Unit u) => u.Arm is Arm.Infantry or Arm.Mechanised or Arm.Motorised or Arm.Armour or Arm.Recon;

    static List<int> Slice(List<int> sorted, int k, int n) =>
        sorted.Skip(sorted.Count * k / n).Take(sorted.Count * (k + 1) / n - sorted.Count * k / n).ToList();

    /// <summary>A unit somewhere in a zone, and its children round it.</summary>
    static void PlaceUnder(War war, Unit u, List<int> zone, Random rng)
    {
        if (zone.Count == 0) return;
        int c = zone[rng.Next(zone.Count)];
        Put(war, u, war.Isl.X(c), war.Isl.Z(c));
        Spread(war, u, rng);
    }

    static void Spread(War war, Unit u, Random rng)
    {
        float r = u.Echelon switch { Echelon.Brigade => 3000f, Echelon.Battalion => 1500f, Echelon.Company => 400f, _ => 150f };
        foreach (int ci in u.Children)
        {
            var c = war.Units[ci];
            var (x, z) = Near(war.Isl, u.X, u.Z, r, rng, u.Side);
            Put(war, c, x, z);
            Spread(war, c, rng);
        }
    }

    static void Put(War war, Unit u, float x, float z)
    {
        u.X = x;
        u.Z = z;
    }

    /// <summary>A spot within <paramref name="r"/> of a point, on the side's own ground and not too steep, or the point itself.</summary>
    static (float X, float Z) Near(Island isl, float x, float z, float r, Random rng, int side)
    {
        for (int k = 0; k < 30; k++)
        {
            float a = (float)(rng.NextDouble() * Math.Tau), d = r * MathF.Sqrt((float)rng.NextDouble());
            float px = x + MathF.Cos(a) * d, pz = z + MathF.Sin(a) * d;
            int i = isl.CellAtWorld(px, pz);
            if (i >= 0 && !isl.Water(i) && isl.Slope[i] < 20f && isl.StartOf[i] == side) return (px, pz);
        }
        return (x, z);
    }
}
