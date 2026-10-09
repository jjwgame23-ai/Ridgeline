namespace Ridgeline;

/// <summary>An army's planned offensive: its axis, the battalions massed for it, and how it went.</summary>
public sealed class Operation
{
    public int Id, Side, Enemy;
    public float X, Z;
    public string Where = "";
    /// <summary>The battalions in it, the places on its axis, and those it took.</summary>
    public readonly List<int> Bns = new(), Axis = new(), Taken = new();
    public double Planned, HHour, Ended = -1, LastTake;
    public bool Attacking, Prepared;
    public int Bounds;
    public float StartStrength, EndStrength, StartOwed, EndOwed;
    public string Outcome = "";
}

/// <summary>
/// Planned offensives: an army masses for an attack on one axis instead of every battalion going for whatever is
/// nearest, and pauses to regroup after it.
/// - The axis. An army on the offensive picks the cluster of enemy places (within 6 km of each other, within 10 km of
///   its own ground) worth most for the least strength its side knows of there.
/// - The force. Enough battalions for three to one against that strength (at least a strong company assumed on each
///   enemy place), 4 to 12: first those not tied down, then
///   garrisons pulled out of quiet stretches of the front against the same enemy, nearest first. Those holding against
///   the other enemy stay (economy of force). Each has to be at 80% strength or more, supplied and rested.
/// - Assembly. They move to an assembly area 6 km short of the axis and attack at dawn (05:30), at least 12 hours after
///   the plan. In the last hour the guns fire a preparation on the enemy units known to be on the axis.
/// - The attack. The battalions go in together, two on each held place. They don't stop to hold what they take: other
///   battalions take newly won ground over, and the attackers go on to the next place, up to three bounds deeper. They
///   attack by night as well as by day, and pause only to take stock after a place falls or for the night's resupply,
///   so an offensive runs until its troops are spent.
/// - The end. The offensive stops when its battalions are worn to half their strength on average (the species will
///   spend itself where human armies stopped at 70%), when its companies are exhausted, when it has taken no new ground
///   for a day, or when it has nothing left to take. The army plans the next for at least a day, and launches it once
///   enough battalions are rested, fed, supplied and strong (at 80% or more) again. (Battalions used to go each for
///   whatever was nearest and then hold it, so within a week most of every army was holding ground and the fronts froze.)
/// </summary>
public static class Offensive
{
    const float Cluster = 6000f, Reach = 10_000f;

    static float Sq(float dx, float dz) => dx * dx + dz * dz;

    /// <summary>Every 10 minutes: plan, prepare, launch and run each army's offensive.</summary>
    public static void Step(War war)
    {
        foreach (var side in war.Sides)
        {
            var op = war.Operations.LastOrDefault(o => o.Side == side.Index && o.Ended < 0);
            if (op == null)
            {
                Plan(war, side);
                continue;
            }
            if (!op.Attacking)
            {
                if (!op.Prepared && war.Time >= op.HHour - 3600)
                {
                    op.Prepared = true;
                    Artillery.Preparation(war, op);
                }
                if (war.Time >= op.HHour) Launch(war, op);
                continue;
            }
            Run(war, op);
        }
    }

    static void Plan(War war, Side side)
    {
        int enemy = side.Offensive;
        if (enemy < 0 || war.Time < side.NextOp || war.Day < 2) return;
        var reach = Command.Distances(war, side.Index);
        var cands = war.Objectives.Where(o => o.Owner == enemy && !o.Unreachable[side.Index] && reach[war.Ctl.CellOf(o.X, o.Z)] <= Reach).ToList();
        if (cands.Count == 0) return;
        Objective? seed = null;
        float best = -1f;
        int foe = 0;
        foreach (var o in cands)
        {
            float value = cands.Where(p => Sq(p.X - o.X, p.Z - o.Z) < Cluster * Cluster).Sum(p => p.Value);
            int known = war.Intel.Near(war, side.Index, enemy, o.X, o.Z, Cluster);
            float score = value / (known + 200f);
            if (score <= best) continue;
            best = score;
            seed = o;
            foe = known;
        }
        if (seed == null) return;
        // What's there: what the side has seen, but at least a strong company holding each enemy place on the axis,
        // since a planner assumes held ground is held. (Planned on sightings alone, every offensive went in with the
        // fewest battalions allowed: troops dug in for days are seldom seen.)
        int places = cands.Count(p => Sq(p.X - seed.X, p.Z - seed.Z) < Cluster * Cluster);
        foe = Math.Max(foe, 300 * places);
        int needed = Math.Clamp((int)MathF.Ceiling(3f * foe / 450f), 4, 12);
        // Battalions for it: not tied down first, then garrisons on quiet stretches of this front.
        var army = war.Units[side.Army];
        var pool = war.Below(army)
            .Where(b => b.Echelon == Echelon.Battalion && Command.Manoeuvre(b) && b.Op < 0 && b.Next == null && b.Order is not { Done: false }
                        && Command.Strength(war, b) >= 0.8f && Command.Supplied(war, b))
            .Where(b => b.Holds < 0 || !Enumerable.Range(0, 3).Any(e => e != side.Index && e != enemy && Command.Faces(war, war.Objectives[b.Holds], e)))
            .Where(b => b.Holds < 0 || Sq(war.Objectives[b.Holds].X - seed.X, war.Objectives[b.Holds].Z - seed.Z) > 10_000f * 10_000f)
            .Select(b => (B: b, P: Command.Pos(war, b)))
            .OrderBy(t => t.B.Holds >= 0 ? 1 : 0).ThenBy(t => Sq(t.P.X - seed.X, t.P.Z - seed.Z))
            .Take(needed).Select(t => t.B).ToList();
        if (pool.Count < 4)
        {
            side.NextOp = war.Time + 3 * 3600;
            return;
        }
        var op = new Operation
        {
            Id = war.Operations.Count, Side = side.Index, Enemy = enemy, X = seed.X, Z = seed.Z, Where = seed.Name, Planned = war.Time,
            HHour = NextDawn(war.Time + 12 * 3600),
        };
        op.Axis.AddRange(cands.Where(p => Sq(p.X - seed.X, p.Z - seed.Z) < Cluster * Cluster)
            .OrderBy(p => reach[war.Ctl.CellOf(p.X, p.Z)]).Select(p => p.Id));
        // Assembly 6 km short of the axis, the battalions side by side 1.5 km apart.
        var port = war.Isl.Towns[side.Port];
        float dx = port.X - seed.X, dz = port.Z - seed.Z, d = MathF.Max(1f, MathF.Sqrt(Sq(dx, dz)));
        float ax = seed.X + dx / d * MathF.Min(6000f, d), az = seed.Z + dz / d * MathF.Min(6000f, d);
        for (int i = 0; i < pool.Count; i++)
        {
            var b = pool[i];
            b.Holds = -1;
            b.Op = op.Id;
            op.Bns.Add(b.Id);
            float lat = (i - (pool.Count - 1) / 2f) * 1500f;
            Command.Give(war, b, new Order { Kind = OrderKind.Move, X = ax - dz / d * lat, Z = az + dx / d * lat });
        }
        op.StartStrength = pool.Average(b => Command.Strength(war, b));
        war.Operations.Add(op);
        war.Events.Add((war.Time, side.Index, $"{side.Name} masses {pool.Count} battalions against {war.Sides[enemy].Name} near {op.Where}, to attack at dawn", op.X, op.Z));
    }

    /// <summary>The first 05:30 at or after <paramref name="t"/> (war time starts at 06:00 on day 1).</summary>
    static double NextDawn(double t)
    {
        double hour = (6.0 + t / 3600.0) % 24.0;
        double wait = (5.5 - hour + 24.0) % 24.0;
        return t + wait * 3600.0;
    }

    /// <summary>H-hour: the battalions go in together, two on each held place, one on each empty one.</summary>
    static void Launch(War war, Operation op)
    {
        op.Attacking = true;
        op.StartOwed = Owed(war, op.Bns.Select(id => war.Units[id]));
        op.LastTake = war.Time;
        war.Events.Add((war.Time, op.Side, $"{war.Sides[op.Side].Name}'s offensive near {op.Where} goes in", op.X, op.Z));
        foreach (int id in op.Bns) Next(war, op, war.Units[id]);
    }

    /// <summary>Give a battalion its next place on the axis, nearest first; false if there's none.</summary>
    static bool Next(War war, Operation op, Unit bn)
    {
        var (x, z) = Command.Pos(war, bn);
        int pick = op.Axis.Where(id =>
            {
                var o = war.Objectives[id];
                return o.Owner != op.Side && !o.Unreachable[op.Side] && war.Time >= o.Retry[op.Side] && o.Claims[op.Side] < (o.Owner >= 0 ? 2 : 1);
            })
            .OrderBy(id => Sq(war.Objectives[id].X - x, war.Objectives[id].Z - z)).FirstOrDefault(-1);
        if (pick < 0) return false;
        var obj = war.Objectives[pick];
        obj.Claims[op.Side]++;
        Command.Give(war, bn, new Order { Kind = OrderKind.Occupy, Target = pick, X = obj.X, Z = obj.Z });
        return true;
    }

    /// <summary>Every 10 minutes while it runs: battalions on to their next places, a bound deeper when the axis is taken, and an end when it's spent.</summary>
    static void Run(War war, Operation op)
    {
        var bns = op.Bns.Select(id => war.Units[id]).ToList();
        float strength = bns.Average(b => Command.Strength(war, b));
        // Called off when its battalions are worn to half their strength (they'll put their lives on the line where
        // human armies stopped at 70%), when its companies are exhausted (16 hours of sleep owed on average), or when
        // it has taken nothing for a day. No limit of days: it goes on as long as the troops can. (It used to stop after
        // three days, a human army's rhythm; that, not the troops, ended most offensives.)
        float owed = Owed(war, bns);
        string end = strength < 0.5f ? "worn down" : owed >= Rest.Exhausted ? "exhausted" : war.Time - op.LastTake > 86400 ? "stalled" : "";
        bool waiting = false, any = false;
        if (end == "")
            foreach (var b in bns)
            {
                if (b.Next != null || b.Order is { Done: false })
                    any = true;
                // Resting after taking a place, or waiting for the night's resupply: an offensive pauses for supply.
                // (Battalions out of food or ammunition used to be sent on to the next place regardless.)
                else if (war.Time < b.RestUntil || !Command.Stocked(war, b))
                    waiting = true;
                else if (Next(war, op, b))
                    any = true;
                // The axis is taken or barred: a bound deeper, to the enemy's places beyond it.
                else if (op.Bounds < 3 && Deeper(war, op) && Next(war, op, b))
                    any = true;
            }
        if (end == "" && !any && !waiting) end = op.Taken.Count > 0 ? "took what it could reach" : "stalled";
        if (end == "") return;
        op.Ended = war.Time;
        op.EndStrength = strength;
        op.EndOwed = owed;
        op.Outcome = end;
        foreach (var b in bns)
        {
            b.Op = -1;
            b.RestUntil = Math.Max(b.RestUntil, war.Time + 6 * 3600);
        }
        // A day at least to plan the next; it goes in when enough battalions are rested, fed, supplied and strong again.
        // (Before readiness was checked, a day's pause had the armies attacking without let-up, and ALPHA lost a quarter
        // of its strength killed in a month; three days' pause was a human army's rhythm.)
        war.Sides[op.Side].NextOp = war.Time + 86400;
        war.Events.Add((war.Time, op.Side, $"{war.Sides[op.Side].Name}'s offensive near {op.Where} ends ({end}) after {(war.Time - op.HHour) / 3600:0} h: {op.Taken.Count} places taken", op.X, op.Z));
    }

    /// <summary>The sleep its battalions' companies owe, on average.</summary>
    static float Owed(War war, IEnumerable<Unit> bns)
    {
        var companies = bns.SelectMany(b => Command.TopMovers(war, b)).Where(m => m.People > 0).ToList();
        return companies.Count > 0 ? companies.Average(m => m.SleepDebt) : 0f;
    }

    /// <summary>The next bound: enemy places within 6 km beyond what the offensive has taken.</summary>
    static bool Deeper(War war, Operation op)
    {
        if (op.Taken.Count == 0) return false;
        float cx = (float)op.Taken.Average(id => war.Objectives[id].X), cz = (float)op.Taken.Average(id => war.Objectives[id].Z);
        var more = war.Objectives.Where(o => o.Owner == op.Enemy && !o.Unreachable[op.Side] && !op.Axis.Contains(o.Id) && Sq(o.X - cx, o.Z - cz) < Cluster * Cluster).ToList();
        if (more.Count == 0) return false;
        op.Axis.AddRange(more.Select(o => o.Id));
        op.Bounds++;
        return true;
    }

    /// <summary>A battalion of an offensive took a place: noted, and a short pause before it goes on.</summary>
    public static void Took(War war, Unit bn, Objective obj)
    {
        var op = war.Operations[bn.Op];
        // Only new ground is progress. (A place lost and taken back used to count, so an offensive seesawing over one
        // bridge, taken 127 times, never stalled and ran on for three weeks.)
        if (!op.Taken.Contains(obj.Id))
        {
            op.Taken.Add(obj.Id);
            op.LastTake = war.Time;
        }
        bn.RestUntil = war.Time + 3600;
    }
}
