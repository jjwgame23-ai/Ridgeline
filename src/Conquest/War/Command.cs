namespace Ridgeline;

/// <summary>
/// The chain of command. Each level plans at its own pace and hands its orders down, and passing an order on takes
/// time. Doctrine's 1/3–2/3 rule gives a commander a third of the time available and leaves two thirds to those below
/// (ADP 5-0). For moves into empty ground the drills are quick: an hour for a brigade's orders to reach its
/// battalions, half an hour for a battalion's to reach its companies, a quarter of an hour for a company to move off.
/// - The army (every 3 hours) shares out the ground worth taking among its divisions, nearest first: towns, ports,
///   resource nodes, the bridges on the main roads, the big hills. It goes no further than 15 km past its ground, and
///   not within 3 km of an enemy.
/// - Once the open ground is mostly taken (or from day 3), it goes over to the offensive against one enemy. It picks
///   the one whose ground near its own is worth most for the least known strength against its own there. It adds
///   that enemy's objectives within 8 km of its own ground to its divisions' tasks, and holds against the other
///   enemy where it stands.
/// - A division (every hour) gives each brigade as many objectives as it has fighting battalions. Its headquarters
///   and troops follow a few km behind its brigades.
/// - A brigade (every half hour) sends an idle battalion to the nearest objective it has. Its headquarters and its
///   support battalions follow.
/// - A battalion puts its line companies in a ring round the objective and its headquarters a kilometre back. It
///   holds the objective once two thirds of its line companies are on it, and then is free for the next.
/// </summary>
public static class Command
{
    const double ArmyEvery = 3 * 3600, DivisionEvery = 3600, BrigadeEvery = 1800;
    const float Reach = 15_000f, KeepOff = 3000f;

    static double Delay(Echelon e) => e switch
    {
        Echelon.Division => 2 * 3600, Echelon.Brigade => 3600, Echelon.Battalion => 1800, Echelon.Company => 900, _ => 600,
    };

    public static bool Manoeuvre(Unit u) => u.Arm is Arm.Infantry or Arm.Mechanised or Arm.Motorised or Arm.Armour or Arm.Recon;

    /// <summary>The objectives on the island, and who holds them at the start.</summary>
    public static void Objectives(War war)
    {
        var isl = war.Isl;
        void Add(ObjKind k, string name, float x, float z, float value, float radius, int r) =>
            war.Objectives.Add(new Objective { Id = war.Objectives.Count, Kind = k, Name = name, X = x, Z = z, Value = value, Radius = radius, Ref = r });
        foreach (var t in isl.Towns.Where(t => t.Population >= 400 || t.Port))
            Add(t.Port ? ObjKind.Port : ObjKind.Town, t.Name, t.X, t.Z, 1f + MathF.Log10(MathF.Max(1f, t.Population / 400f)) + (t.Port ? 3f : 0f),
                MathF.Max(300f, t.RadiusM), t.Id);
        foreach (var n in isl.Nodes) Add(ObjKind.Node, n.Name, isl.X(n.Cell), isl.Z(n.Cell), 3f, 300f, n.Id);
        foreach (var c in isl.Crossings.Where(c => c.Bridge && c.Road >= RoadClass.Secondary && c.WidthM >= 8f))
        {
            var near = isl.Towns.OrderBy(t => isl.Dist(t.Cell, c.Cell)).First();
            Add(ObjKind.Bridge, $"{near.Name} bridge", isl.X(c.Cell), isl.Z(c.Cell), c.Road == RoadClass.Main ? 1.5f : 1f, 200f, c.Cell);
        }
        foreach (var p in isl.Peaks.Take(30)) Add(ObjKind.Hill, p.Name, isl.X(p.Cell), isl.Z(p.Cell), 0.5f + p.Relief / 500f, 300f, p.Cell);
        foreach (var o in war.Objectives) o.Owner = war.Ctl.Owner[war.Ctl.CellOf(o.X, o.Z)];
    }

    public static void Think(War war)
    {
        double now = war.Time;
        foreach (var side in war.Sides)
        {
            var army = war.Units[side.Army];
            if (now >= army.ThinkAt)
            {
                war.Doing = $"plan army {side.Name}";
                PlanArmy(war, army);
                army.ThinkAt = now + ArmyEvery;
            }
            foreach (var d in Kids(war, army, Echelon.Division))
            {
                if (now < d.ThinkAt) continue;
                d.ThinkAt = now + DivisionEvery;
                war.Doing = $"plan division {d.Short}";
                PlanDivision(war, d);
            }
            foreach (var b in war.Below(army).Where(b => b.Echelon == Echelon.Brigade && Manoeuvre(b)))
            {
                if (now < b.ThinkAt) continue;
                b.ThinkAt = now + BrigadeEvery;
                war.Doing = $"plan brigade {b.Short}";
                PlanBrigade(war, b);
            }
        }
        foreach (var u in war.Units)
            if (u.Next != null && now >= u.Next.At)
            {
                u.Order = u.Next;
                u.Next = null;
                war.Doing = $"execute {u.Short}";
                Execute(war, u);
            }
        foreach (var bn in war.Units)
            if (bn.Echelon == Echelon.Battalion && bn.Order is { Kind: OrderKind.Occupy, Done: false } o)
            {
                war.Doing = $"check {bn.Short}";
                CheckHeld(war, bn, o);
            }
    }

    /// <summary>The army's task forces: its divisions, and the manoeuvre brigades directly under it.</summary>
    static IEnumerable<Unit> Forces(War war, Unit army) =>
        army.Children.Select(c => war.Units[c]).Where(c => c.Echelon == Echelon.Division || (c.Echelon == Echelon.Brigade && Manoeuvre(c)));

    static IEnumerable<Unit> Kids(War war, Unit u, Echelon e) => u.Children.Select(c => war.Units[c]).Where(c => c.Echelon == e);

    static void PlanArmy(War war, Unit army)
    {
        int side = army.Side;
        var forces = Forces(war, army).ToList();
        var reach = Distances(war, side);
        foreach (var f in forces) f.Tasks.Clear();
        var targets = new List<Objective>();
        foreach (var o in war.Objectives)
        {
            if (o.Owner >= 0 || o.Unreachable[side]) continue;
            int c = war.Ctl.CellOf(o.X, o.Z);
            if (reach[c] > Reach || EnemyNear(war, side, o.X, o.Z, KeepOff)) continue;
            targets.Add(o);
        }
        if (targets.Count < forces.Count * 3 || war.Day >= 3)
        {
            int enemy = ChooseEnemy(war, side, reach);
            if (enemy >= 0)
            {
                if (war.Sides[side].Offensive != enemy)
                {
                    war.Sides[side].Offensive = enemy;
                    var (ax, az) = Pos(war, army);
                    war.Events.Add((war.Time, side, $"{war.Sides[side].Name} goes over to the offensive against {war.Sides[enemy].Name}", ax, az));
                }
                foreach (var o in war.Objectives)
                    if (o.Owner == enemy && !o.Unreachable[side] && war.Time >= o.Retry[side] && reach[war.Ctl.CellOf(o.X, o.Z)] <= 8000f) targets.Add(o);
            }
        }
        // Each target, nearest and most valuable first, to the nearest force with battalions free to take it on: two
        // tasks for each free battalion. (Every target used to go to the nearest division, so the one at the front had
        // forty tasks and all its battalions tied down holding ground, while a third of the army sat idle.)
        var room = forces.ToDictionary(f => f.Id, f => 2 * war.Below(f).Count(bn => Free(war, bn)));
        foreach (var o in targets.OrderBy(o => reach[war.Ctl.CellOf(o.X, o.Z)] / o.Value))
        {
            var f = forces.Where(f => room[f.Id] > 0).MinBy(f => Dist(war, f, o.X, o.Z));
            if (f == null) break;
            f.Tasks.Add(o.Id);
            room[f.Id]--;
        }
        var (hx, hz) = Home(war, side);
        foreach (var t in army.Children.Select(c => war.Units[c]).Where(c => !forces.Contains(c)))
            Follow(war, t, hx, hz, 4000f); // the army's own troops stay by the port for now
    }

    /// <summary>
    /// The enemy to attack: the one whose objectives near this side's ground are worth most for the least strength this
    /// side knows of there, against its own strength there. -1 if none is in reach.
    /// </summary>
    static int ChooseEnemy(War war, int side, float[] reach)
    {
        int best = -1;
        float bestScore = 0f;
        for (int e = 0; e < 3; e++)
        {
            if (e == side) continue;
            var near = war.Objectives.Where(o => o.Owner == e && reach[war.Ctl.CellOf(o.X, o.Z)] <= 10_000f).ToList();
            if (near.Count == 0) continue;
            float cx = near.Average(o => o.X), cz = near.Average(o => o.Z), value = near.Sum(o => o.Value);
            int theirs = war.Intel.Near(war, side, e, cx, cz, 15_000f);
            int ours = war.Units.Where(u => u.IsMover && u.Side == side && Manoeuvre(u) && Sq(u.X - cx, u.Z - cz) < 15_000f * 15_000f).Sum(u => u.People);
            float score = value * ours / (theirs + 200f);
            // An army keeps to the offensive it has begun unless the other enemy is now twice as good a target: shifting
            // the main effort means regrouping divisions. (It used to switch every few hours.)
            if (e == war.Sides[side].Offensive) score *= 2f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = e;
        }
        return best;
    }

    static void PlanDivision(War war, Unit div)
    {
        int side = div.Side;
        var brigades = Kids(war, div, Echelon.Brigade).Where(Manoeuvre).ToList();
        foreach (var b in brigades)
            b.Tasks.RemoveAll(id => !div.Tasks.Contains(id) && war.Objectives[id].Claims[side] == 0 || war.Objectives[id].Owner == side);
        foreach (var b in brigades.OrderBy(b => b.Tasks.Count))
        {
            int want = Kids(war, b, Echelon.Battalion).Count(bn => Free(war, bn));
            while (b.Tasks.Count < want)
            {
                var (bx, bz) = Pos(war, b);
                var free = div.Tasks.Where(id => brigades.All(o => !o.Tasks.Contains(id)) && war.Objectives[id].Owner != side)
                    .OrderBy(id => Sq(war.Objectives[id].X - bx, war.Objectives[id].Z - bz) / war.Objectives[id].Value).FirstOrDefault(-1);
                if (free < 0) break;
                b.Tasks.Add(free);
            }
        }
        // Headquarters and division troops a few km behind the brigades, on the side toward home.
        if (brigades.Count == 0) return;
        var (cx, cz) = Centre(war, brigades);
        var (hx, hz) = Home(war, side);
        var (fx, fz) = Toward(cx, cz, hx, hz, 4000f);
        var (sx, sz) = Toward(cx, cz, hx, hz, Supply.DivisionBack);
        Supply.Place(war, div, sx, sz);
        Follow(war, div, fx, fz, 5000f);
        foreach (var t in div.Children.Select(c => war.Units[c]).Where(c => !brigades.Contains(c)))
            Follow(war, t, fx, fz, 6000f);
    }

    /// <summary>
    /// Idle battalions to the brigade's objectives, nearest first: one to an empty objective, two to one the enemy
    /// holds, so they go in with the odds that attacking a held position needs. A brigade of three or more keeps one
    /// back in reserve ("two up, one back"), and a battalion that took ground in the front line stays on it as its
    /// garrison while it's in the front line. (Battalions used to move on from everything they took, leaving it empty
    /// for the enemy to walk back into: each side took 30-100 places a day while what it held hardly changed.)
    /// A battalion just back from an operation
    /// consolidates and reorganises first, and one down to half its strength isn't sent on another (US doctrine reckons
    /// a unit below 50% combat ineffective). (Battalions used to be sent on the next objective the moment they finished
    /// one, so whole armies were on the move into the enemy all day, and the median company fought 11 times in 3 days.)
    /// </summary>
    static void PlanBrigade(War war, Unit bde)
    {
        int side = bde.Side;
        var bns = Kids(war, bde, Echelon.Battalion).Where(Manoeuvre).ToList();
        foreach (var bn in bns)
            if (bn.Holds >= 0 && (war.Objectives[bn.Holds].Owner != side || !FrontLine(war, side, war.Objectives[bn.Holds]))) bn.Holds = -1;
        // A reserve is kept from the battalions free to manoeuvre, when there are two or more of them; those holding
        // ground are in place already. (With every battalion but one holding, the last used to stay back as the reserve,
        // and a third of each army's battalions sat idle while its offensive died.)
        int holding = bns.Count(bn => bn.Holds >= 0), free = bns.Count - holding;
        int committed = bns.Count(bn => bn.Holds < 0 && (bn.Next != null || bn.Order is { Kind: OrderKind.Occupy, Done: false }));
        var held = war.Units.Where(u => u.Side == side && u.Holds >= 0).Select(u => u.Holds).ToHashSet();
        foreach (var bn in bns)
        {
            if (bns.Count >= 3 && free >= 2 && committed >= free - 1) break; // one kept back in reserve
            if (bn.Holds >= 0 || bn.Next != null || bn.Order is { Done: false } || war.Time < bn.RestUntil || Strength(war, bn) < 0.5f) continue;
            if (!Supplied(war, bn)) continue; // waiting for the night's resupply
            var (x, z) = Pos(war, bn);
            int pick = bde.Tasks.Where(id =>
                {
                    var o = war.Objectives[id];
                    return o.Owner != side && !o.Unreachable[side] && war.Time >= o.Retry[side] && o.Claims[side] < (o.Owner >= 0 ? 2 : 1);
                })
                .OrderBy(id => Sq(war.Objectives[id].X - x, war.Objectives[id].Z - z)).FirstOrDefault(-1);
            if (pick < 0)
            {
                // Nothing to take: hold one of the side's own objectives in the front line that nobody holds yet, within
                // 12 km, so the front is a line of strongpoints 4-5 km apart, not the odd garrison where something was
                // taken. Only facing an enemy the army isn't attacking: against that one, its strength goes into the
                // attack (economy of force, FM 3-0). (Ground between garrisons used to lie open, and an offensive walked
                // into the towns behind them: 70-130 places changed hands a day, many without a shot.)
                pick = war.Objectives.Where(o => o.Owner == side && o.Claims[side] == 0 && !held.Contains(o.Id) && Sq(o.X - x, o.Z - z) < 12_000f * 12_000f
                                                 && Enumerable.Range(0, 3).Any(e => e != side && e != war.Sides[side].Offensive && Faces(war, o, e)))
                    .OrderBy(o => Sq(o.X - x, o.Z - z)).Select(o => o.Id).FirstOrDefault(-1);
                if (pick < 0) continue;
                held.Add(pick);
            }
            if (pick < 0) continue;
            var obj = war.Objectives[pick];
            obj.Claims[side]++;
            Give(war, bn, new Order { Kind = OrderKind.Occupy, Target = pick, X = obj.X, Z = obj.Z });
            committed++;
        }
        if (bns.Count == 0) return;
        var (cx, cz) = Centre(war, bns);
        var (hx, hz) = Home(war, side);
        var (fx, fz) = Toward(cx, cz, hx, hz, 2500f);
        var (sx, sz) = Toward(cx, cz, hx, hz, Supply.BrigadeBack);
        Supply.Place(war, bde, sx, sz);
        Follow(war, bde, fx, fz, 4000f);
        foreach (var t in bde.Children.Select(c => war.Units[c]).Where(c => !bns.Contains(c)))
            Follow(war, t, fx, fz, 5000f);
    }

    /// <summary>An order to a unit, taking effect once the unit has planned it.</summary>
    static void Give(War war, Unit u, Order o)
    {
        o.At = war.Time + Delay(u.Echelon);
        u.Next = o;
    }

    /// <summary>Move a unit (or, if it has no headquarters of its own, all of it) to within reach of a point, if it's further off than <paramref name="slack"/>.</summary>
    static void Follow(War war, Unit u, float x, float z, float slack)
    {
        if (u.Hauls || Busy(war, u)) return; // a company on depot runs goes where the runs take it
        var (ux, uz) = Pos(war, u);
        if (Sq(ux - x, uz - z) < slack * slack) return;
        Give(war, u, new Order { Kind = OrderKind.Move, X = x, Z = z });
    }

    /// <summary>Planning or carrying out an order, itself or any of its parts.</summary>
    static bool Busy(War war, Unit u) =>
        u.Next != null || u.Order is { Done: false } || (!u.IsMover && TopMovers(war, u).Any(m => m.Next != null || m.Order is { Done: false }));

    /// <summary>An order coming into effect: a mover sets off; a unit without a headquarters of its own moves its parts.</summary>
    static void Execute(War war, Unit u, bool fromAbove = false)
    {
        var o = u.Order!;
        if (o.Kind == OrderKind.Occupy && u.Echelon == Echelon.Battalion)
        {
            Deploy(war, u, war.Objectives[o.Target]);
            return;
        }
        if (o.Kind != OrderKind.Move) return;
        if (u.IsMover)
        {
            if (!war.StartMove(u, o.X, o.Z)) o.Done = true;
            return;
        }
        // Keep the parts' layout round the new point; the order is then theirs to carry out.
        var (cx, cz) = Pos(war, u);
        foreach (var m in TopMovers(war, u))
        {
            if (m.Hauls) continue;
            m.Next = null;
            m.Order = new Order { Kind = OrderKind.Move, X = o.X + m.X - cx, Z = o.Z + m.Z - cz, At = war.Time };
            if (!war.StartMove(m, m.Order.X, m.Order.Z)) m.Order.Done = true;
        }
        o.Done = true;
    }

    /// <summary>A battalion's companies to their places on an objective: the line companies in a ring, the headquarters back.</summary>
    static void Deploy(War war, Unit bn, Objective obj)
    {
        var parts = TopMovers(war, bn).ToList();
        var line = parts.Where(c => c.Echelon == Echelon.Company && Manoeuvre(c)).ToList();
        var rest = parts.Where(c => !line.Contains(c)).ToList();
        var (hx, hz) = Home(war, bn.Side);
        float home = MathF.Atan2(hz - obj.Z, hx - obj.X), r = obj.Radius + 300f;
        for (int k = 0; k < line.Count; k++)
        {
            float a = home + MathF.PI + (k - (line.Count - 1) / 2f) * (MathF.Tau / MathF.Max(3, line.Count));
            Send(war, line[k], obj.X + MathF.Cos(a) * r, obj.Z + MathF.Sin(a) * r);
        }
        foreach (var c in rest) Send(war, c, obj.X + MathF.Cos(home) * (r + 1000f), obj.Z + MathF.Sin(home) * (r + 1000f));
    }

    static void Send(War war, Unit c, float x, float z)
    {
        c.Next = new Order { Kind = OrderKind.Move, X = x, Z = z, At = war.Time + Delay(Echelon.Company) };
    }

    /// <summary>
    /// Whether a battalion holds its objective yet: two thirds of its line companies there. If none of them can get
    /// there, it's marked out of reach for the side.
    /// </summary>
    static void CheckHeld(War war, Unit bn, Order o)
    {
        var obj = war.Objectives[o.Target];
        var line = Kids(war, bn, Echelon.Company).Where(Manoeuvre).ToList();
        if (line.Count == 0) return;
        if (line.Any(c => c.InFight >= 0)) return;
        int there = line.Count(c => c.Path == null && c.Next == null && Sq(c.X - obj.X, c.Z - obj.Z) <= Sq(obj.Radius + 800f, 0f));
        if (there * 3 >= line.Count * 2)
        {
            o.Done = true;
            bn.RestUntil = war.Time + 3 * 3600; // consolidate on it: dig in, resupply, evacuate the wounded
            obj.Claims[bn.Side] = Math.Max(0, obj.Claims[bn.Side] - 1);
            if (obj.Owner != bn.Side) war.Taken(obj, bn);
            if (FrontLine(war, bn.Side, obj)) bn.Holds = obj.Id;
            return;
        }
        // Every company stopped, too few of them there: beaten off or held up by the enemy (try again in six hours), or
        // with no way there, out of reach for the side. (Companies stopped within 2 km of it used to leave the battalion
        // waiting for ever.)
        if (line.All(c => c.Path == null && c.Next == null && c.Order is not { Done: false }))
        {
            bool enemy = obj.Owner >= 0 && obj.Owner != bn.Side || line.Any(c => c.HeldUp >= o.At);
            if (enemy)
            {
                obj.Retry[bn.Side] = war.Time + 6 * 3600;
                bn.RestUntil = war.Time + 6 * 3600; // beaten off or held up: reorganise before trying anything else
            }
            else obj.Unreachable[bn.Side] = true;
            obj.Claims[bn.Side] = Math.Max(0, obj.Claims[bn.Side] - 1);
            o.Done = true;
        }
    }

    /// <summary>A fighting battalion free for an operation, now or once it has rested: not holding ground, and at half strength or more.</summary>
    static bool Free(War war, Unit bn) => bn.Echelon == Echelon.Battalion && Manoeuvre(bn) && bn.Holds < 0 && Strength(war, bn) >= 0.5f;

    /// <summary>Supplied for an operation: every part of it with fuel, food and half its ammunition (see <see cref="Supply.Ready"/>).</summary>
    static bool Supplied(War war, Unit bn) => Movers(war, bn).All(m => Supply.Ready(war, m));

    /// <summary>A unit's movers: itself if it moves on its own, and those its parts make up.</summary>
    static IEnumerable<Unit> Movers(War war, Unit u) => u.IsMover ? TopMovers(war, u).Prepend(u) : TopMovers(war, u);

    /// <summary>What's left of a unit: its movers' fit soldiers against what they were raised with.</summary>
    public static float Strength(War war, Unit u)
    {
        int now = 0, raised = 0;
        foreach (var m in Movers(war, u))
        {
            now += m.People;
            raised += m.Raised;
        }
        return raised > 0 ? (float)now / raised : 1f;
    }

    /// <summary>Where a unit is: a mover's own position, or the middle of its movers.</summary>
    public static (float X, float Z) Pos(War war, Unit u)
    {
        if (u.IsMover) return (u.X, u.Z);
        var ms = TopMovers(war, u).ToList();
        return ms.Count == 0 ? (u.X, u.Z) : Centre(war, ms);
    }

    /// <summary>The movers that make up a unit with no headquarters of its own (its companies, and so on down).</summary>
    public static IEnumerable<Unit> TopMovers(War war, Unit u)
    {
        foreach (int ci in u.Children)
        {
            var c = war.Units[ci];
            if (c.IsMover) yield return c;
            else if (c.Mover < 0)
                foreach (var m in TopMovers(war, c)) yield return m;
        }
    }

    static (float X, float Z) Centre(War war, List<Unit> us)
    {
        float x = 0f, z = 0f;
        foreach (var u in us)
        {
            var (ux, uz) = Pos(war, u);
            x += ux;
            z += uz;
        }
        return (x / us.Count, z / us.Count);
    }

    static (float X, float Z) Home(War war, int side)
    {
        var port = war.Isl.Towns[war.Sides[side].Port];
        return (port.X, port.Z);
    }

    /// <summary>A point <paramref name="m"/> metres from (x, z) toward (tx, tz).</summary>
    static (float X, float Z) Toward(float x, float z, float tx, float tz, float m)
    {
        float dx = tx - x, dz = tz - z, d = MathF.Sqrt(dx * dx + dz * dz);
        return d < 1f ? (x, z) : (x + dx / d * MathF.Min(m, d), z + dz / d * MathF.Min(m, d));
    }

    static float Dist(War war, Unit u, float x, float z)
    {
        var (ux, uz) = Pos(war, u);
        return MathF.Sqrt(Sq(ux - x, uz - z));
    }

    static float Sq(float dx, float dz) => dx * dx + dz * dz;

    /// <summary>Facing one enemy: ground it holds or has troops in within 5 km.</summary>
    static bool Faces(War war, Objective o, int enemy)
    {
        var ctl = war.Ctl;
        int n = (int)MathF.Ceiling(5000f / Territory.CellM), c = ctl.CellOf(o.X, o.Z), cx = c % ctl.N, cz = c / ctl.N;
        for (int y = cz - n; y <= cz + n; y++)
        for (int xx = cx - n; xx <= cx + n; xx++)
            if ((uint)xx < (uint)ctl.N && (uint)y < (uint)ctl.N && ctl.HeldBy(y * ctl.N + xx, enemy)) return true;
        return false;
    }

    /// <summary>In the front line: enemy or contested ground within 5 km of it.</summary>
    static bool FrontLine(War war, int side, Objective o) => EnemyNear(war, side, o.X, o.Z, 5000f);

    static bool EnemyNear(War war, int side, float x, float z, float r)
    {
        var ctl = war.Ctl;
        int n = (int)MathF.Ceiling(r / Territory.CellM), c = ctl.CellOf(x, z), cx = c % ctl.N, cz = c / ctl.N;
        for (int y = cz - n; y <= cz + n; y++)
        for (int xx = cx - n; xx <= cx + n; xx++)
        {
            if ((uint)xx >= (uint)ctl.N || (uint)y >= (uint)ctl.N) continue;
            if (ctl.Hostile(y * ctl.N + xx, side)) return true;
        }
        return false;
    }

    /// <summary>How far each square is from ground the side holds (m), by squares, over land.</summary>
    static float[] Distances(War war, int side)
    {
        var ctl = war.Ctl;
        int n = ctl.N;
        var d = new float[n * n];
        Array.Fill(d, float.MaxValue);
        var q = new Queue<int>();
        for (int c = 0; c < n * n; c++)
            if (ctl.Owner[c] == side)
            {
                d[c] = 0f;
                q.Enqueue(c);
            }
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            int x = c % n, y = c / n;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Island.DX[k], ny = y + Island.DY[k];
                if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                int j = ny * n + nx;
                float nd = d[c] + Territory.CellM * Island.DL[k];
                if (!ctl.Land[j] || nd >= d[j]) continue;
                d[j] = nd;
                q.Enqueue(j);
            }
        }
        return d;
    }
}
