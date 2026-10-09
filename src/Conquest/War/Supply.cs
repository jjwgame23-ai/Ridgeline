namespace Ridgeline;

/// <summary>The three cargoes. Ammunition and fuel burn or explode; food doesn't.</summary>
public enum Cargo : byte { Food, Fuel, Ammo }

/// <summary>
/// A supply point and what it holds, in tonnes: the side's depot at its port, a division's support area or a
/// brigade's. A support area stands a few km behind its formation's fighting units and moves with them.
/// </summary>
public sealed class Depot
{
    public int Id, Side;
    /// <summary>The formation it serves (a division or brigade), or -1 for the port.</summary>
    public int Unit = -1;
    /// <summary>The depot it draws from: the division's for a brigade, the port for a division; -1 for the port.</summary>
    public int Parent = -1;
    public float X, Z;
    public string Name = "";
    public readonly float[] Stock = new float[3], Target = new float[3], Coming = new float[3];
}

public enum HaulState : byte { Idle, ToLoad, ToUnload, Back }

/// <summary>A logistics company hauling between depots: the depot it works for, the run it's on and its load.</summary>
public sealed class Haul
{
    public int Unit, Home;
    public int From = -1, To = -1;
    public HaulState State;
    public readonly float[] Load = new float[3];
    /// <summary>What it can carry: tonnes in cargo trucks (food, ammunition) and in tankers (fuel).</summary>
    public float Dry, Wet;
    /// <summary>When it may try again after being turned back or finding no way, and which it was.</summary>
    public double Wait;
    public string Why = "";
}

/// <summary>
/// Supply: food, fuel and ammunition used up, and brought forward from the port by the armies' own trucks.
/// - Using it. Every soldier eats 1.8 kg of rations a day (three ready-to-eat meals). Vehicles burn fuel by the km
///   and, engines running, by the hour in a fight, at their makers' figures. Ammunition goes as it's fired.
/// - Carrying it. A unit starts with three days' food and full tanks. Ships land 4,000 t a day at the side's port while
///   it holds it. A division's support area aims to hold three days of what its units use, a brigade's two (days of
///   supply, as US planning has it).
/// - Bringing it up. The armies' logistics companies drive the depot runs, empty to the depot they draw from and
///   loaded back, by night as well as by day: the army's from the port to the divisions, a division's from the port to
///   its support area, a brigade's from the division's support area to its own. Cargo trucks carry 6 t, tankers 8 t
///   (9,500 L). The convoys are units on the map like any other: they halt when they see the enemy ahead, can be
///   ambushed, and lose what burnt with the trucks.
/// - The last leg. Each night at 22:00 every unit within 30 km of its depot, by a way clear of enemy ground, is topped
///   up: food to three days, fuel to full, ammunition to its basic load (the US calls this nightly run a LOGPAC). A
///   unit its own depot can't reach, or whose depot stands empty, is served by the nearest of its side's depots that
///   can (area support, as US sustainment doctrine has it). The forward support companies' trucks that carry it
///   aren't moved on the map yet.
/// - Falling back. A support area doesn't stand on ground the enemy has taken or is fighting over: it falls back toward
///   the port, stock and all, to its own side's quiet ground. A unit nobody can reach, hungry for half a day, breaks
///   out toward the nearest ground a depot can reach (Command.BreakOut).
/// - Going without. Out of fuel, vehicles don't move. Out of ammunition, soldiers don't fire. Hungry, they shoot worse
///   each day. A battalion low on fuel or ammunition isn't sent on an operation until it has been resupplied, so
///   offensives pause when they outrun their supply.
/// </summary>
public static class Supply
{
    const float RationKg = 1.8f, DieselKgPerL = 0.84f, TruckT = 6f, TankerT = 8f, PortTPerDay = 4000f, LogpacReach = 30_000f;
    const float AtDepot = 1500f;

    /// <summary>
    /// How far behind its fighting units a support area stands: 8 km for a brigade's, 15 for a division's. US doctrine
    /// puts a brigade's 10-20 km behind the front and a division's further back; this island is 80 km across. (At
    /// 2.5 km the brigades' depots were nearly in the front line, and the convoys kept halting for the enemy ahead.)
    /// </summary>
    public const float BrigadeBack = 8000f, DivisionBack = 15000f;

    /// <summary>
    /// Fuel by vehicle: tank (L), use per km, use per hour with the engine running. From the makers' figures, averaged
    /// over the three armies: M1A2 1,900 L at about 4 L/km and T-90 1,600 L at 2.5; Bradley 660 L and BMP-2 460;
    /// Stryker 200 L and BTR-82 300; JLTV 90 L; HEMTT 590 L and KamAZ 350; M109 500 L and 2S19 1,000.
    /// </summary>
    public static (float Cap, float PerKm, float Idle) Fuel(VClass v) => v switch
    {
        VClass.Tank => (1600f, 3.5f, 25f),
        VClass.LightTank or VClass.Ifv or VClass.Spaa or VClass.Engineer => (600f, 1.5f, 8f),
        VClass.Apc or VClass.Mortar => (300f, 0.7f, 4f),
        VClass.Ltv => (100f, 0.25f, 2f),
        VClass.Truck or VClass.Tanker => (300f, 0.45f, 3f),
        VClass.Howitzer => (800f, 2f, 10f),
        VClass.Rocket => (400f, 1f, 5f),
        VClass.Ambulance => (150f, 0.3f, 2f),
        _ => (0f, 0f, 0f), // aircraft aren't fuelled from the ground stocks yet
    };

    /// <summary>
    /// Ammunition by weight, packed: rifle and machine-gun rounds 25 g, a 40 mm or hand grenade 0.4 kg, an anti-tank
    /// rocket 7 kg; a tank round 30 kg, an autocannon round 1 kg, a vehicle machine-gun round 30 g; a 155 mm shell with
    /// its charge 45 kg, a rocket 300 kg, a 120 mm mortar bomb 15 kg.
    /// </summary>
    static float RoundKg(VClass v) => v switch
    {
        VClass.Tank => 30f, VClass.LightTank => 20f, VClass.Ifv or VClass.Spaa => 1f, VClass.Howitzer => 45f, VClass.Rocket => 300f, VClass.Mortar => 15f, _ => 0f,
    };

    static float SoldierKg(int ammo, int grenades, int rockets) => ammo * 0.025f + grenades * 0.4f + rockets * 7f;

    /// <summary>Fill the armies' tanks and ration packs, set up the depots, and give the logistics companies their runs.</summary>
    public static void Setup(War war)
    {
        foreach (var u in war.Units)
            if (u.IsMover)
            {
                Measure(war, u);
                u.Fuel = u.FuelCap;
                u.Food = u.People * RationKg * 3f;
            }
        foreach (var side in war.Sides)
        {
            var port = war.Isl.Towns[side.Port];
            var pd = AddDepot(war, side.Index, -1, -1, port.X, port.Z, $"{side.Name} port depot at {port.Name}");
            var army = war.Units[side.Army];
            foreach (var div in war.Below(army).Where(u => u.Echelon == Echelon.Division))
            {
                var (dx, dz) = Behind(war, div, DivisionBack);
                var dd = AddDepot(war, side.Index, div.Id, pd.Id, dx, dz, $"{div.Short} support area");
                foreach (var b in war.Below(div).Where(b => b.Echelon == Echelon.Brigade && Command.Manoeuvre(b)))
                {
                    var (bx, bz) = Behind(war, b, BrigadeBack);
                    AddDepot(war, side.Index, b.Id, dd.Id, bx, bz, $"{b.Short} support area");
                }
            }
            foreach (var b in army.Children.Select(c => war.Units[c]).Where(b => b.Echelon == Echelon.Brigade && Command.Manoeuvre(b)))
            {
                var (bx, bz) = Behind(war, b, BrigadeBack);
                AddDepot(war, side.Index, b.Id, pd.Id, bx, bz, $"{b.Short} support area");
            }
        }
        var byUnit = war.Depots.Where(d => d.Unit >= 0).ToDictionary(d => d.Unit, d => d.Id);
        foreach (var u in war.Units)
        {
            if (!u.IsMover) continue;
            u.Depot = HomeDepot(war, u, byUnit);
            // Logistics companies with trucks haul. Their runs depend on where they belong.
            if (u.Arm != Arm.Logistics) continue;
            var h = new Haul { Unit = u.Id, Home = u.Depot };
            Capacity(war, h);
            if (h.Dry + h.Wet <= 0f) continue;
            u.Hauls = true;
            war.Hauls.Add(h);
        }
        // What each depot aims to hold: days of supply of what all the units it serves use in a day.
        var daily = new Dictionary<int, float[]>();
        foreach (var u in war.Units)
        {
            if (!u.IsMover) continue;
            var use = DailyUse(war, u);
            for (int d = u.Depot; d >= 0; d = war.Depots[d].Parent)
            {
                if (!daily.TryGetValue(d, out var a)) daily[d] = a = new float[3];
                for (int c = 0; c < 3; c++) a[c] += use[c];
            }
        }
        foreach (var d in war.Depots)
        {
            daily.TryGetValue(d.Id, out var a);
            a ??= new float[3];
            float days = d.Unit < 0 ? 10f : war.Units[d.Unit].Echelon == Echelon.Division ? 3f : 2f;
            for (int c = 0; c < 3; c++)
            {
                d.Target[c] = a[c] * days;
                d.Stock[c] = d.Target[c];
            }
        }
        Reach(war);
    }

    static Depot AddDepot(War war, int side, int unit, int parent, float x, float z, string name)
    {
        if (unit >= 0) (x, z) = OnRoads(war, side, x, z);
        var d = new Depot { Id = war.Depots.Count, Side = side, Unit = unit, Parent = parent, X = x, Z = z, Name = name };
        war.Depots.Add(d);
        if (unit >= 0) war.Units[unit].Depot = d.Id;
        return d;
    }

    /// <summary>A point some way behind a formation's units, on the way home to its port.</summary>
    static (float X, float Z) Behind(War war, Unit u, float m)
    {
        var (x, z) = Command.Pos(war, u);
        var port = war.Isl.Towns[war.Sides[u.Side].Port];
        float dx = port.X - x, dz = port.Z - z, d = MathF.Sqrt(dx * dx + dz * dz);
        return d < 1f ? (x, z) : (x + dx / d * MathF.Min(m, d), z + dz / d * MathF.Min(m, d));
    }

    /// <summary>The depot that supplies a unit: its brigade's support area, else its division's, else the port's.</summary>
    static int HomeDepot(War war, Unit u, Dictionary<int, int> byUnit)
    {
        for (var a = u; a != null; a = a.Parent >= 0 ? war.Units[a.Parent] : null)
            if (a.Echelon >= Echelon.Brigade && byUnit.TryGetValue(a.Id, out int d)) return d;
        return war.Depots.First(d => d.Side == u.Side && d.Unit < 0).Id;
    }

    /// <summary>A mover's fuel tanks and how fast it burns them, from the vehicles it still has.</summary>
    public static void Measure(War war, Unit m)
    {
        float cap = 0f, km = 0f, idle = 0f;
        foreach (int ci in m.Carries)
            foreach (int vi in war.Units[ci].Vehicles)
            {
                var v = war.Vehicles[vi];
                if (v.Lost) continue;
                var f = Fuel(v.Class);
                cap += f.Cap;
                km += f.PerKm;
                idle += f.Idle;
            }
        m.FuelCap = cap;
        m.PerKm = km;
        m.Idle = idle;
        m.Fuel = MathF.Min(m.Fuel, cap);
    }

    static void Capacity(War war, Haul h)
    {
        float dry = 0f, wet = 0f;
        foreach (int ci in war.Units[h.Unit].Carries)
            foreach (int vi in war.Units[ci].Vehicles)
            {
                var v = war.Vehicles[vi];
                if (v.Lost) continue;
                if (v.Class == VClass.Truck) dry += TruckT;
                else if (v.Class == VClass.Tanker) wet += TankerT;
            }
        // Trucks lost on a run take their share of the load with them.
        if (h.Dry > 0f && dry < h.Dry) Lose(war, h, Cargo.Food, dry / h.Dry, Cargo.Ammo);
        if (h.Wet > 0f && wet < h.Wet) Lose(war, h, Cargo.Fuel, wet / h.Wet, null);
        h.Dry = dry;
        h.Wet = wet;
    }

    static void Lose(War war, Haul h, Cargo a, float keep, Cargo? b)
    {
        int side = war.Units[h.Unit].Side;
        foreach (var c in b is { } bb ? new[] { a, bb } : new[] { a })
        {
            float lost = h.Load[(int)c] * (1f - keep);
            if (lost <= 0f) continue;
            h.Load[(int)c] -= lost;
            war.CargoLost[side, (int)c] += lost;
            if (h.To >= 0) war.Depots[h.To].Coming[(int)c] = MathF.Max(0f, war.Depots[h.To].Coming[(int)c] - lost);
        }
    }

    /// <summary>What a mover uses in a day of ordinary going (tonnes): its rations, a quarter of its tanks, a tenth of its basic load of ammunition (half, for guns and mortars).</summary>
    static float[] DailyUse(War war, Unit m) => new[]
    {
        m.People * RationKg / 1000f,
        m.FuelCap * 0.25f * DieselKgPerL / 1000f,
        BasicLoadKg(war, m) * (m.Guns ? 0.5f : 0.1f) / 1000f,
    };

    static float BasicLoadKg(War war, Unit m)
    {
        float kg = 0f;
        foreach (int ci in m.Carries)
        {
            var c = war.Units[ci];
            foreach (int s in c.Members)
            {
                if (war.Soldiers[s].State is not (SoldierState.Fit or SoldierState.Wounded)) continue;
                var l = Orbat.Load(war.Soldiers[s].Job);
                kg += SoldierKg(l.Ammo, l.Grenades, l.Rockets);
            }
            foreach (int vi in c.Vehicles)
            {
                var v = war.Vehicles[vi];
                if (!v.Lost) kg += Orbat.VehicleLoad(v.Class) * RoundKg(v.Class) + Orbat.MgLoad(v.Class) * 0.03f;
            }
        }
        return kg;
    }

    /// <summary>A formation's support area follows it: placed where its commander wants it (behind the fighting units).</summary>
    public static void Place(War war, Unit u, float x, float z)
    {
        if (u.Depot < 0 || war.Depots[u.Depot].Unit != u.Id) return;
        var d = war.Depots[u.Depot];
        // It moves only for a real shift: support areas aren't packed up for a few hundred metres. (The stock moves
        // with it at once; the trucks that would carry it aren't counted.)
        if ((d.X - x) * (d.X - x) + (d.Z - z) * (d.Z - z) < 3000f * 3000f) return;
        (d.X, d.Z) = OwnGround(war, d.Side, x, z);
    }

    /// <summary>
    /// The nearest spot on the side's own ground toward its port from (x, z), where trucks can reach it. (Support areas
    /// used to be put wherever their brigades' middle was 8 km back from, and one whose brigade had pushed into enemy
    /// country, or had no fighting battalions left to follow, stood on the enemy's ground: none of its 58 units could be
    /// fed, and its convoys found no way to it.)
    /// </summary>
    static (float X, float Z) OwnGround(War war, int side, float x, float z)
    {
        var port = war.Isl.Towns[war.Sides[side].Port];
        float dx = port.X - x, dz = port.Z - z, d = MathF.Sqrt(dx * dx + dz * dz);
        for (float m = 0f; m < d; m += Territory.CellM)
        {
            float px = x + dx / d * m, pz = z + dz / d * m;
            int c = war.Ctl.CellOf(px, pz);
            if (!EnemyGround(war, c, side) && !war.Ctl.Contested[c]) return OnRoads(war, side, px, pz);
        }
        return (port.X, port.Z);
    }

    /// <summary>A depot with less than a twentieth of the food it aims to hold: its convoys aren't getting through.</summary>
    static bool Empty(Depot d) => d.Target[0] > 0f && d.Stock[0] < 0.05f * d.Target[0];

    /// <summary>Ground the enemy holds and the side has nobody on.</summary>
    static bool EnemyGround(War war, int c, int side) => war.Ctl.Owner[c] >= 0 && war.Ctl.Owner[c] != side && !war.Ctl.Present(c, side);

    /// <summary>
    /// A support area stands where trucks can get to it from the port: the nearest such spot to where it's wanted.
    /// (Placed by distance alone, some ended up in country wheels couldn't reach, across a river with no bridge or up
    /// a roadless valley, and stood empty while their convoys found no way.)
    /// </summary>
    static (float X, float Z) OnRoads(War war, int side, float x, float z)
    {
        var port = war.Isl.Towns[war.Sides[side].Port];
        int c = war.Move.Reachable(x, z, war.Move.CellOf(port.X, port.Z));
        return c < 0 ? (x, z) : (war.Move.CX(c), war.Move.CZ(c));
    }

    /// <summary>Food and fuel used over <paramref name="dt"/> seconds, and every 10 minutes the convoys, ships and the night's resupply.</summary>
    public static void Step(War war, double dt)
    {
        foreach (var u in war.Units)
        {
            if (!u.IsMover || u.People <= 0) continue;
            u.Food -= (float)(u.People * RationKg * dt / 86400.0);
            // A convoy carrying rations eats from its load rather than go hungry. (Convoy crews ate only at depots, so
            // one held up on the road went hungry for days beside trucks full of food.)
            if (u.Food <= 0f && u.Hauls && war.Hauls.FirstOrDefault(h => h.Unit == u.Id) is { } hl && hl.Load[(int)Cargo.Food] > 0f)
            {
                float eat = MathF.Min(hl.Load[(int)Cargo.Food] * 1000f, u.People * RationKg * 3f);
                hl.Load[(int)Cargo.Food] -= eat / 1000f;
                if (hl.To >= 0) war.Depots[hl.To].Coming[(int)Cargo.Food] = MathF.Max(0f, war.Depots[hl.To].Coming[(int)Cargo.Food] - eat / 1000f);
                war.Issued[u.Side, 0] += eat / 1000f;
                u.Food += eat;
            }
            if (u.Food <= 0f)
            {
                u.Food = 0f;
                if (u.HungrySince < 0) u.HungrySince = war.Time;
            }
            else u.HungrySince = -1;
            // Engines running in a fight; and every day a couple of hours of running for power, heat and short moves,
            // and generators for headquarters and kitchens (half a litre a soldier). (Burning fuel only on the move and
            // in fights, an army of 11,000 vehicles used 7 L a vehicle a day.)
            float hours = (u.InFight >= 0 ? 1f : 0f) + 2f / 24f;
            u.Fuel = MathF.Max(0f, u.Fuel - (float)((u.Idle * hours + u.People * 0.5f / 24f) * dt / 3600.0));
        }
        int t = (int)war.Time;
        if (t % 600 != 0) return;
        double hour = war.Hour;
        if (Math.Abs(hour - 6.0) < 1e-6) Land(war);
        if (Math.Abs(hour - 22.0) < 1e-6) Logpac(war);
        foreach (var h in war.Hauls) Run(war, h);
    }

    /// <summary>How well a hungry unit shoots: 15% worse for each day without food, to no worse than 40%.</summary>
    public static float Fed(War war, Unit m) => m.HungrySince < 0 ? 1f : MathF.Max(0.4f, 1f - 0.15f * (float)((war.Time - m.HungrySince) / 86400.0));

    /// <summary>Ready for an operation: fuel for a day's going (a third of its tanks), food, and at least half its basic load of ammunition.</summary>
    public static bool Ready(War war, Unit m)
    {
        if (m.Mob != Mobility.Foot && m.FuelCap > 0f && m.Fuel < m.FuelCap / 3f) return false;
        if (m.Food <= 0f) return false;
        return AmmoShare(war, m) >= 0.5f;
    }

    /// <summary>The share of its soldiers' basic load of ammunition a unit still has, by weight.</summary>
    public static float AmmoShare(War war, Unit m)
    {
        float have = 0f, full = 0f;
        foreach (int ci in m.Carries)
            foreach (int s in war.Units[ci].Members)
            {
                ref var so = ref war.Soldiers[s];
                if (so.State is not (SoldierState.Fit or SoldierState.Wounded)) continue;
                var l = Orbat.Load(so.Job);
                have += SoldierKg(so.Ammo, so.Grenades, so.Rockets);
                full += SoldierKg(l.Ammo, l.Grenades, l.Rockets);
            }
        return full > 0f ? have / full : 1f;
    }

    /// <summary>The ships' landings at 06:00. A port lost to the enemy lands nothing, and its depot is lost with it.</summary>
    static void Land(War war)
    {
        foreach (var side in war.Sides)
        {
            var pd = war.Depots.First(d => d.Side == side.Index && d.Unit < 0);
            var obj = war.Objectives.FirstOrDefault(o => o.Kind == ObjKind.Port && o.Ref == side.Port);
            if (obj != null && obj.Owner >= 0 && obj.Owner != side.Index)
            {
                if (pd.Stock.Sum() > 0f)
                {
                    Array.Clear(pd.Stock);
                    war.Events.Add((war.Time, side.Index, $"{side.Name} loses its port at {obj.Name}, and the depot there", pd.X, pd.Z));
                }
                continue;
            }
            // Ship what the port depot is short of, up to what the port can land. (Shipped in fixed shares, it ran out of
            // shells once the guns were firing, while fuel piled up.)
            var gap = new float[3];
            for (int c = 0; c < 3; c++) gap[c] = MathF.Max(0f, pd.Target[c] - pd.Stock[c]);
            float total = gap.Sum(), scale = total > PortTPerDay ? PortTPerDay / total : 1f;
            for (int c = 0; c < 3; c++)
            {
                pd.Stock[c] += gap[c] * scale;
                war.Landed[side.Index, c] += gap[c] * scale;
            }
        }
    }

    static float Dist(Unit u, Depot d) => MathF.Sqrt((u.X - d.X) * (u.X - d.X) + (u.Z - d.Z) * (u.Z - d.Z));

    /// <summary>One look at a logistics company's run, every 10 minutes.</summary>
    static void Run(War war, Haul h)
    {
        var u = war.Units[h.Unit];
        if (u.People <= 0 || u.InFight >= 0 || war.Time < h.Wait) return;
        Capacity(war, h);
        if (h.Dry + h.Wet <= 0f)
        {
            Drop(war, h);
            return;
        }
        switch (h.State)
        {
            case HaulState.Idle:
            {
                var home = war.Depots[h.Home];
                bool there = Dist(u, home) <= AtDepot;
                if (there) Provision(war, u, home);
                // Where to take a load, and from where: a brigade's or division's company fills its own support area
                // from the one it draws on; the army's take from the port to whichever division is shortest.
                if (home.Unit < 0)
                {
                    var to = war.Depots.Where(d => d.Side == u.Side && d.Parent == home.Id).OrderByDescending(d => Short(d)).FirstOrDefault();
                    if (to == null || Short(to) < 0.2f) return;
                    h.From = home.Id;
                    h.To = to.Id;
                }
                else
                {
                    // Short, it goes for a load from wherever it is: support areas move with their brigades, and a
                    // company that first had to catch up with its own could be days behind. (Runs used to start only
                    // from home, and five depots stood empty while their companies chased them.)
                    if (home.Parent < 0 || Short(home) < 0.2f)
                    {
                        if (!there && u.Path == null) Go(war, h, home);
                        return;
                    }
                    h.From = home.Parent;
                    h.To = home.Id;
                }
                if (home.Unit < 0 && !there)
                {
                    if (u.Path == null) Go(war, h, home);
                    return;
                }
                Plan(war, h);
                if (h.Load.Sum() <= 0f) return;
                h.State = HaulState.ToLoad;
                break;
            }
            case HaulState.ToLoad:
            {
                var from = war.Depots[h.From];
                if (Dist(u, from) > AtDepot)
                {
                    if (u.Path == null) Go(war, h, from);
                    return;
                }
                // Load what's there of what was asked for.
                Provision(war, u, from);
                var to = war.Depots[h.To];
                for (int c = 0; c < 3; c++)
                {
                    float got = MathF.Min(h.Load[c], from.Stock[c]);
                    from.Stock[c] -= got;
                    to.Coming[c] = MathF.Max(0f, to.Coming[c] - (h.Load[c] - got));
                    h.Load[c] = got;
                }
                h.State = HaulState.ToUnload;
                Go(war, h, to);
                break;
            }
            case HaulState.ToUnload:
            {
                var to = war.Depots[h.To];
                if (Dist(u, to) > AtDepot)
                {
                    if (u.Path == null) Go(war, h, to);
                    return;
                }
                for (int c = 0; c < 3; c++)
                {
                    to.Stock[c] += h.Load[c];
                    to.Coming[c] = MathF.Max(0f, to.Coming[c] - h.Load[c]);
                    war.Hauled[u.Side, c] += h.Load[c];
                    h.Load[c] = 0f;
                }
                war.Convoys[u.Side]++;
                h.State = HaulState.Back;
                break;
            }
            case HaulState.Back:
            {
                var home = war.Depots[h.Home];
                if (Dist(u, home) > AtDepot)
                {
                    if (u.Path == null) Go(war, h, home);
                    return;
                }
                h.From = h.To = -1;
                h.State = HaulState.Idle;
                break;
            }
        }
    }

    /// <summary>
    /// How short a depot is, counting what's on its way: the share missing of whichever cargo it's shortest of.
    /// (Counted by total weight, fuel swamped the rest, and a depot could run out of food without ever looking short.)
    /// </summary>
    static float Short(Depot d)
    {
        float worst = 0f;
        for (int c = 0; c < 3; c++)
            if (d.Target[c] > 0f) worst = MathF.Max(worst, (d.Target[c] - d.Stock[c] - d.Coming[c]) / d.Target[c]);
        return worst;
    }

    /// <summary>
    /// A logistics company at a depot takes its own rations and fuel there. (Left out of the nightly resupply, the
    /// convoy crews used to go hungry.)
    /// </summary>
    static void Provision(War war, Unit u, Depot d)
    {
        Medical.Rejoin(war, u);
        float food = MathF.Min(d.Stock[0] * 1000f, MathF.Max(0f, u.People * RationKg * 3f - u.Food));
        float fuel = MathF.Min(d.Stock[1] * 1000f / DieselKgPerL, MathF.Max(0f, u.FuelCap - u.Fuel));
        u.Food += food;
        u.Fuel += fuel;
        d.Stock[0] -= food / 1000f;
        d.Stock[1] -= fuel * DieselKgPerL / 1000f;
        war.Issued[u.Side, 0] += food / 1000f;
        war.Issued[u.Side, 1] += fuel * DieselKgPerL / 1000f;
    }

    /// <summary>A load to ask for: what the depot is short of, up to what the trucks and tankers take.</summary>
    static void Plan(War war, Haul h)
    {
        var to = war.Depots[h.To];
        float need(Cargo c) => MathF.Max(0f, to.Target[(int)c] - to.Stock[(int)c] - to.Coming[(int)c]);
        float food = need(Cargo.Food), ammo = need(Cargo.Ammo), dry = food + ammo;
        float scale = dry > h.Dry ? h.Dry / dry : 1f;
        h.Load[(int)Cargo.Food] = food * scale;
        h.Load[(int)Cargo.Ammo] = ammo * scale;
        h.Load[(int)Cargo.Fuel] = MathF.Min(need(Cargo.Fuel), h.Wet);
        for (int c = 0; c < 3; c++) to.Coming[c] += h.Load[c];
    }

    /// <summary>A company that has lost all its trucks gives up its run, and what it promised doesn't come.</summary>
    static void Drop(War war, Haul h)
    {
        if (h.To >= 0)
            for (int c = 0; c < 3; c++) war.Depots[h.To].Coming[c] = MathF.Max(0f, war.Depots[h.To].Coming[c] - h.Load[c]);
        Array.Clear(h.Load);
        h.State = HaulState.Idle;
        h.From = h.To = -1;
        h.Wait = double.MaxValue;
        war.Units[h.Unit].Hauls = false;
    }

    /// <summary>Set off for a depot, by night too. With no way there, or just turned back by the enemy, wait an hour and look again.</summary>
    static void Go(War war, Haul h, Depot d)
    {
        var u = war.Units[h.Unit];
        if (war.Time - u.HeldUp < 3600)
        {
            h.Wait = u.HeldUp + 3600;
            h.Why = "held up";
            return;
        }
        u.Order = new Order { Kind = OrderKind.Move, X = d.X, Z = d.Z, At = war.Time, Night = true };
        if (!war.StartMove(u, d.X, d.Z))
        {
            u.Order.Done = true;
            h.Wait = war.Time + 3600;
            h.Why = "no way";
        }
    }

    /// <summary>
    /// The night's resupply from each depot to the units it serves: within 30 km, by a way round ground the enemy holds
    /// alone. A unit its own depot can't reach, or whose depot is empty, is served by the nearest of its side's depots
    /// that can reach it and has food (area support).
    /// A depot short of something shares out what it has.
    /// </summary>
    static void Logpac(War war)
    {
        var ctl = war.Ctl;
        foreach (var d in war.Depots)
            if (d.Unit >= 0 && (EnemyGround(war, ctl.CellOf(d.X, d.Z), d.Side) || ctl.Contested[ctl.CellOf(d.X, d.Z)])) (d.X, d.Z) = OwnGround(war, d.Side, d.X, d.Z);
        var routes = war.Depots.Select(d => Routes(war, d)).ToArray();
        var by = new List<Unit>?[war.Depots.Count];
        foreach (var u in war.Units)
        {
            // A convoy on the road isn't there to be served; one halted, held up or waiting, is. (Convoys were left out
            // altogether, so one held up away from a depot went hungry until it got through.)
            if (!u.IsMover || u.Depot < 0 || (u.Hauls && u.Path != null) || u.People <= 0 || u.InFight >= 0) continue;
            int cell = ctl.CellOf(u.X, u.Z);
            int from = u.Depot;
            if (routes[from][cell] > LogpacReach || Empty(war.Depots[from]))
            {
                float best = float.MaxValue;
                foreach (var d in war.Depots)
                    if (d.Side == u.Side && d.Id != u.Depot && !Empty(d) && routes[d.Id][cell] <= LogpacReach && routes[d.Id][cell] < best)
                    {
                        best = routes[d.Id][cell];
                        from = d.Id;
                    }
                if (from != u.Depot) war.Area[u.Side]++;
                else if (routes[from][cell] > LogpacReach)
                {
                    if (routes[from][cell] == float.MaxValue) war.CutOff[u.Side]++;
                    else war.TooFar[u.Side]++;
                    continue;
                }
            }
            (by[from] ??= new List<Unit>()).Add(u);
        }
        foreach (var d in war.Depots)
        {
            if (by[d.Id] == null) continue;
            var served = new List<(Unit U, float[] Need)>();
            var total = new float[3];
            foreach (var u in by[d.Id]!)
            {
                // Soldiers back from hospital, and replacements, come up with the resupply.
                Medical.Rejoin(war, u);
                Measure(war, u);
                var need = new[]
                {
                    MathF.Max(0f, u.People * RationKg * 3f - u.Food) / 1000f,
                    MathF.Max(0f, u.FuelCap - u.Fuel) * DieselKgPerL / 1000f,
                    AmmoNeedKg(war, u) / 1000f,
                };
                served.Add((u, need));
                for (int c = 0; c < 3; c++) total[c] += need[c];
            }
            var share = new float[3];
            for (int c = 0; c < 3; c++) share[c] = total[c] > 0f ? MathF.Min(1f, d.Stock[c] / total[c]) : 0f;
            foreach (var (u, need) in served)
            {
                u.Food += need[0] * share[0] * 1000f;
                u.Fuel += need[1] * share[1] * 1000f / DieselKgPerL;
                Rearm(war, u, share[2]);
                for (int c = 0; c < 3; c++)
                {
                    d.Stock[c] -= need[c] * share[c];
                    war.Issued[u.Side, c] += need[c] * share[c];
                }
            }
        }
    }

    /// <summary>
    /// How far each 1 km square is from a depot by land (m), going round ground the enemy holds alone; float.MaxValue
    /// where it can't be reached. Ground fought over is passable, since the side has troops there. (Resupply used to
    /// go by a straight line, which cut off units behind any bend in the front; and contested ground counted as cut, so
    /// nobody in the front line was resupplied at all.)
    /// </summary>
    static float[] Routes(War war, Depot dp)
    {
        var ctl = war.Ctl;
        int n = ctl.N, side = dp.Side;
        var d = new float[n * n];
        Array.Fill(d, float.MaxValue);
        int start = ctl.CellOf(dp.X, dp.Z);
        bool Blocked(int c) => !ctl.Land[c] || (ctl.Owner[c] >= 0 && ctl.Owner[c] != side && !ctl.Present(c, side));
        if (start < 0 || Blocked(start)) return d;
        var q = new PriorityQueue<int, float>();
        d[start] = 0f;
        q.Enqueue(start, 0f);
        while (q.TryDequeue(out int c, out float dc))
        {
            if (dc > d[c] || dc > LogpacReach) continue;
            int x = c % n, y = c / n;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Island.DX[k], ny = y + Island.DY[k];
                if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                int j = ny * n + nx;
                float nd = dc + Territory.CellM * Island.DL[k];
                if (Blocked(j) || nd >= d[j]) continue;
                d[j] = nd;
                q.Enqueue(j, nd);
            }
        }
        return d;
    }

    /// <summary>Why a unit can't be reached from its depot, for the report.</summary>
    public static string CutWhy(War war, Unit u, Dictionary<int, float[]> cache)
    {
        float[] R(Depot d) => cache.TryGetValue(d.Id, out var r) ? r : cache[d.Id] = Routes(war, d);
        int c = war.Ctl.CellOf(u.X, u.Z);
        var own = war.Depots[u.Depot];
        int dc = war.Ctl.CellOf(own.X, own.Z);
        if (war.Ctl.Owner[dc] >= 0 && war.Ctl.Owner[dc] != u.Side && !war.Ctl.Present(dc, u.Side)) return "its depot on enemy ground";
        if (war.Depots.Any(d => d.Side == u.Side && d.Id != own.Id && R(d)[c] < float.MaxValue)) return "another depot could reach it";
        if (war.Ctl.Owner[c] != u.Side) return "on enemy ground";
        bool enemy = war.Units.Any(e => e.IsMover && e.Side != u.Side && e.People >= 4 && (e.X - u.X) * (e.X - u.X) + (e.Z - u.Z) * (e.Z - u.Z) < 5000f * 5000f);
        return enemy ? "enemy ground all round, the enemy within 5 km" : "enemy ground all round, no enemy within 5 km";
    }

    /// <summary>
    /// The squares some depot of each side reaches within 30 km round the ground the enemy holds, refreshed with the
    /// ground every half hour (War.UpdateControl).
    /// </summary>
    public static void Reach(War war)
    {
        int n = war.Ctl.N;
        for (int s = 0; s < 3; s++) Array.Clear(war.Reached[s] ??= new bool[n * n]);
        foreach (var d in war.Depots)
        {
            var r = Routes(war, d);
            var to = war.Reached[d.Side];
            for (int c = 0; c < r.Length; c++)
                if (r[c] <= LogpacReach) to[c] = true;
        }
    }

    /// <summary>
    /// Whether a unit can be reached from one of its side's depots round the ground the enemy holds, as of the last
    /// ground update. (It used to ask of its own depot alone, worked out afresh each time.)
    /// </summary>
    public static bool ClearToDepot(War war, Unit u)
    {
        if (u.Depot < 0 || war.Reached[u.Side] == null) return true;
        int c = war.Ctl.CellOf(u.X, u.Z);
        return c >= 0 && war.Reached[u.Side][c];
    }

    static float AmmoNeedKg(War war, Unit m)
    {
        float kg = 0f;
        foreach (int ci in m.Carries)
        {
            var c = war.Units[ci];
            foreach (int s in c.Members)
            {
                ref var so = ref war.Soldiers[s];
                if (so.State is not (SoldierState.Fit or SoldierState.Wounded)) continue;
                var l = Orbat.Load(so.Job);
                kg += SoldierKg(Math.Max(0, l.Ammo - so.Ammo), Math.Max(0, l.Grenades - so.Grenades), Math.Max(0, l.Rockets - so.Rockets));
            }
            foreach (int vi in c.Vehicles)
            {
                var v = war.Vehicles[vi];
                if (v.Lost) continue;
                kg += Math.Max(0, Orbat.VehicleLoad(v.Class) - v.Ammo) * RoundKg(v.Class) + Math.Max(0, Orbat.MgLoad(v.Class) - v.Mg) * 0.03f;
            }
        }
        return kg;
    }

    /// <summary>Hand out ammunition: each soldier and vehicle made up by <paramref name="share"/> of what they're short.</summary>
    static void Rearm(War war, Unit m, float share)
    {
        if (share <= 0f) return;
        var vs = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(war.Vehicles);
        foreach (int ci in m.Carries)
        {
            var c = war.Units[ci];
            foreach (int s in c.Members)
            {
                ref var so = ref war.Soldiers[s];
                if (so.State is not (SoldierState.Fit or SoldierState.Wounded)) continue;
                var l = Orbat.Load(so.Job);
                so.Ammo = (short)(so.Ammo + MathF.Round(Math.Max(0, l.Ammo - so.Ammo) * share));
                so.Grenades = (byte)(so.Grenades + MathF.Round(Math.Max(0, l.Grenades - so.Grenades) * share));
                so.Rockets = (byte)(so.Rockets + MathF.Round(Math.Max(0, l.Rockets - so.Rockets) * share));
            }
            foreach (int vi in c.Vehicles)
            {
                ref var v = ref vs[vi];
                if (v.Lost) continue;
                v.Ammo = (short)(v.Ammo + MathF.Round(Math.Max(0, Orbat.VehicleLoad(v.Class) - v.Ammo) * share));
                v.Mg = (short)(v.Mg + MathF.Round(Math.Max(0, Orbat.MgLoad(v.Class) - v.Mg) * share));
            }
        }
    }
}
