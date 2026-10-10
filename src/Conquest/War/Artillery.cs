namespace Ridgeline;

/// <summary>A fire mission: rounds from one fire unit on their way to a point.</summary>
public sealed class Mission
{
    public int Side, Unit;
    /// <summary>The fight it was called for, or -1 for a strike on a unit outside any fight.</summary>
    public int Fight = -1;
    /// <summary>For a strike, the unit aimed at; for counter-battery, the battery.</summary>
    public int Target = -1;
    public VClass Kind;
    public float X, Z, Sigma, Reach;
    public int Rounds;
    /// <summary>When the rounds land.</summary>
    public double At;
    public bool CounterBattery;
    /// <summary>What it was for: 0 close support, 1 on units seen, 2 counter-battery, 3 an offensive's preparation.</summary>
    public byte Purpose;
}

/// <summary>
/// Artillery: battalions' mortars, brigades' and divisions' guns, and the armies' rockets, firing on what the side can
/// see, and on each other.
/// - Close support. A company in contact calls fire on the strongest enemy squad it can see, outside the distance the
///   weapon needs from its own men (mortars 200 m, guns 300, unguided rockets 600, guided 150). The nearest mortars
///   answer first, then the guns; rockets only for big targets. The rounds land 4 minutes after the call for mortars and
///   6 for guns (10 for BRAVO, whose fire control is more centralised), adjusted onto the target and fired for effect.
/// - Where they land. The observer knows where the target is to within 10 m plus 3% of his range from it, halved once
///   the fire is adjusted; each round then spreads by its weapon's dispersion (0.3% of range for guns, 0.4% for
///   mortars, 1.5% for unguided rockets, 5 m for GMLRS). A round puts a man standing in the open out of action with an
///   even chance at its casualty radius (155/152 mm 25 m, 120 mm 18, 81 mm 12, rockets 30-35), as for tank shells;
///   flat on the ground or dug in, far less. Everyone within four radii is pinned. It falls on whoever is there, ours
///   included.
/// - On what's been seen. Every 10 minutes, idle guns and rockets with half their ammunition left fire on enemy units
///   their side saw in the last 10 minutes, outside any fight, far enough from their own troops, with up to a quarter of
///   their day's allowance.
/// - Counter-battery. A battery that fires is found by the enemy's radars seven times in ten if any of his guns are
///   within 30 km, and his nearest free guns fire back on where it fired from. So guns shoot and move: self-propelled
///   ones go 1.2 km within 2 minutes of finishing, towed ones within 10, and counter-battery fire mostly finds towed
///   batteries still there.
/// - Ammunition. Each mission spends the tubes' rounds (a gun and its ammunition carrier hold 90, a mortar 80, a
///   launcher 12 rockets), brought up by the supply system at 45 kg a shell.
/// </summary>
public static class Artillery
{
    /// <summary>Range (m), casualty radius (m), rounds per tube in a mission, seconds from the call to the rounds landing, dispersion (share of range), guided.</summary>
    internal static (float Range, float Reach, int PerTube, float Delay, float Spread, bool Guided) Gun(int side, VClass v) => v switch
    {
        // A British battalion's mortars are 81 mm; the US and Russian 120 mm (M121, 2S12).
        VClass.Mortar => side == 2 ? (5600f, 12f, 4, 240f, 0.004f, false) : (7200f, 18f, 4, 240f, 0.004f, false),
        // 155/152 mm: M109A7, 2S19, AS90, about 24 km with ordinary rounds. Russian norms fire more rounds a mission.
        VClass.Howitzer => (24_000f, 25f, side == 1 ? 5 : 3, side == 1 ? 600f : 360f, 0.003f, false),
        // GMLRS (US, British), guided, 70 km; BM-27 (Russian), unguided, 35 km.
        VClass.Rocket => side == 1 ? (35_000f, 30f, 4, 900f, 0.015f, false) : (70_000f, 35f, 1, 600f, 0f, true),
        _ => default,
    };

    /// <summary>
    /// Rounds a tube may fire in a day, the controlled supply rate: for BRAVO's guns about one unit of fire (60 rounds of
    /// 152 mm) by Russian norms, for the others' two thirds of that; 60 bombs a mortar; a launcher's load. A hard fight,
    /// not an all-out one. (Without it guns fired 57 rounds a day each, every day.)
    /// </summary>
    internal static int Allowance(int side, VClass v) => v switch { VClass.Howitzer => side == 1 ? 60 : 40, VClass.Mortar => 60, _ => 12 };

    internal static float MinSafe(VClass v, bool guided) => v switch { VClass.Mortar => 200f, VClass.Howitzer => 300f, _ => guided ? 150f : 600f };

    static Cause CauseOf(VClass v) => v == VClass.Mortar ? Cause.Mortar : Cause.Shell;

    /// <summary>Find the fire units: movers with mortars, guns or rocket launchers.</summary>
    public static void Setup(War war)
    {
        foreach (var u in war.Units)
        {
            if (!u.IsMover) continue;
            var n = new Dictionary<VClass, int>();
            foreach (int ci in u.Carries)
                foreach (int vi in war.Units[ci].Vehicles)
                {
                    var c = war.Vehicles[vi].Class;
                    if (c is VClass.Mortar or VClass.Howitzer or VClass.Rocket) n[c] = n.GetValueOrDefault(c) + 1;
                }
            if (n.Count == 0) continue;
            u.Fires = n.MaxBy(kv => kv.Value).Key;
            u.Guns = true;
            war.FireUnits.Add(u.Id);
        }
    }

    internal static int Tubes(War war, Unit u, out int rounds)
    {
        int tubes = 0;
        rounds = 0;
        foreach (int ci in u.Carries)
            foreach (int vi in war.Units[ci].Vehicles)
            {
                var v = war.Vehicles[vi];
                if (v.Lost || v.Class != u.Fires || v.Ammo <= 0) continue;
                tubes++;
                rounds += v.Ammo;
            }
        return tubes;
    }

    static float Dist(float ax, float az, float bx, float bz) => MathF.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    /// <summary>A company in contact calls for fire on the strongest enemy squad it can see.</summary>
    public static void Call(War war, Fight f, Unit m, List<FightSquad> mine, HashSet<int> seen)
    {
        if (war.Time < m.CalledUntil || !Command.Manoeuvre(m)) return;
        FightSquad? best = null;
        float bestW = 0f, bestOwn = 0f, bestObs = 0f;
        int bigness = 0;
        foreach (int k in seen)
        {
            var t = f.S[k];
            if (!t.Alive) continue;
            int fit = 0;
            foreach (int i in t.Fighters)
                if (war.Soldiers[f.F[i].Soldier].State is SoldierState.Fit or SoldierState.Wounded) fit++;
            bigness += fit;
            float own = float.MaxValue, obs = float.MaxValue;
            foreach (var s in f.S)
            {
                if (s.Side != m.Side || !s.Alive) continue;
                float d = Dist(s.X, s.Z, t.X, t.Z);
                own = MathF.Min(own, d);
                if (s.Mover == m.Id && s.Visible.Contains(k)) obs = MathF.Min(obs, d);
            }
            if (obs == float.MaxValue || own < 150f) continue;
            float w = fit + 4 * t.Vehicles.Count(v => !f.V[v].Dead);
            if (w <= bestW) continue;
            bestW = w;
            best = t;
            bestOwn = own;
            bestObs = obs;
        }
        if (best == null) return;
        var unit = Pick(war, m.Side, best.X, best.Z, bestOwn, bigness >= 30, false);
        if (unit == null) return;
        // The observer adjusts the fire onto the target: what's left of his error is half of it.
        float tle = 0.5f * (10f + 0.03f * bestObs);
        var ms = Fire(war, unit, best.X + tle * Gauss(war.Rng), best.Z + tle * Gauss(war.Rng), tle, f.Id, -1, false);
        if (ms != null)
        {
            m.CalledUntil = ms.At + 120;
            war.ArtyMissions[m.Side, 0]++;
        }
    }

    /// <summary>The fire unit to answer: free, set up, in range, with rounds, and far enough from our own men; mortars first, then guns, then rockets.</summary>
    static Unit? Pick(War war, int side, float x, float z, float own, bool big, bool noMortars)
    {
        Unit? best = null;
        float bestScore = float.MaxValue;
        foreach (int id in war.FireUnits)
        {
            var u = war.Units[id];
            if (u.Side != side || u.People <= 0 || u.InFight >= 0 || u.Path != null || war.Time < u.FireReady) continue;
            if (noMortars && u.Fires == VClass.Mortar) continue;
            if (u.Fires == VClass.Rocket && !big) continue;
            var g = Gun(side, u.Fires);
            float d = Dist(u.X, u.Z, x, z);
            if (d > g.Range || own < MinSafe(u.Fires, g.Guided)) continue;
            // Ranked first, so its tubes are counted only if it would be chosen.
            float score = (u.Fires == VClass.Mortar ? 0f : u.Fires == VClass.Howitzer ? 1e6f : 2e6f) + d;
            if (score >= bestScore) continue;
            int tubes = Tubes(war, u, out _);
            if (tubes == 0 || u.RoundsToday >= tubes * Allowance(side, u.Fires)) continue;
            bestScore = score;
            best = u;
        }
        return best;
    }

    /// <summary>
    /// Fire a mission: spend the rounds, schedule their landing, and set the guns to move after. The enemy's radars may
    /// pick the battery up and fire back.
    /// </summary>
    static Mission? Fire(War war, Unit u, float x, float z, float tle, int fight, int target, bool cb)
    {
        var g = Gun(u.Side, u.Fires);
        int tubes = Tubes(war, u, out int have);
        if (tubes == 0) return null;
        int rounds = Math.Min(Math.Min(have, g.Guided ? 2 : tubes * g.PerTube), Math.Max(1, tubes * Allowance(u.Side, u.Fires) - u.RoundsToday));
        int left = rounds - Take(war, u, rounds);
        float range = Dist(u.X, u.Z, x, z), spread = g.Guided ? 5f : g.Spread * range;
        var ms = new Mission
        {
            Side = u.Side, Unit = u.Id, Fight = fight, Target = target, Kind = u.Fires, X = x, Z = z, Reach = g.Reach, Rounds = rounds - left,
            Sigma = MathF.Sqrt(tle * tle + spread * spread), At = war.Time + g.Delay, CounterBattery = cb, Purpose = (byte)(fight >= 0 ? 0 : cb ? 2 : 1),
        };
        war.Missions.Add(ms);
        int k = u.Fires == VClass.Mortar ? 0 : u.Fires == VClass.Howitzer ? 1 : 2;
        war.ArtyRounds[u.Side, k] += ms.Rounds;
        // Shoot and move: self-propelled guns are off two minutes after the last round, towed ones ten.
        u.FireReady = ms.At + 120;
        u.DisplaceAt = ms.At + (u.Mob == Mobility.Tracked ? 120 : 600);
        // Counter-battery fire is itself seen by radar, but answering it in the same moment would set off a chain
        // through every battery on the island at once: only fire for others is looked for.
        if (!cb) Located(war, u, ms.At);
        return ms;
    }

    /// <summary>Take up to <paramref name="rounds"/> from a fire unit's tubes in turn, and count them against its day's allowance; how many it had.</summary>
    internal static int Take(War war, Unit u, int rounds)
    {
        var vs = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(war.Vehicles);
        int left = rounds;
        while (left > 0)
        {
            bool any = false;
            foreach (int ci in u.Carries)
                foreach (int vi in war.Units[ci].Vehicles)
                {
                    ref var v = ref vs[vi];
                    if (left == 0 || v.Lost || v.Class != u.Fires || v.Ammo <= 0) continue;
                    v.Ammo--;
                    left--;
                    any = true;
                }
            if (!any) break;
        }
        u.RoundsToday += rounds - left;
        return rounds - left;
    }

    /// <summary>
    /// Where a fire unit's tubes stand: the middle of the parts that have them. A battalion's mortars are a platoon of
    /// its headquarters company, hundreds of metres from the command post. (Counter-battery fire used to be aimed at
    /// the company's middle, the staff's tents, and battalion headquarters lost nine in ten of their people.)
    /// </summary>
    internal static (float X, float Z) TubePos(War war, Unit u)
    {
        float x = 0f, z = 0f;
        int n = 0;
        foreach (int ci in u.Carries)
        {
            var c = war.Units[ci];
            if (!c.Vehicles.Any(vi => !war.Vehicles[vi].Lost && war.Vehicles[vi].Class == u.Fires)) continue;
            x += u.X + (c == u ? 0f : c.OffX);
            z += u.Z + (c == u ? 0f : c.OffZ);
            n++;
        }
        return n == 0 ? (u.X, u.Z) : (x / n, z / n);
    }

    /// <summary>The enemy's counter-battery radars pick up a firing battery, and his guns fire back on where its tubes fired from.</summary>
    static void Located(War war, Unit u, double firedAt)
    {
        var (tx, tz) = TubePos(war, u);
        for (int e = 0; e < 3; e++)
        {
            if (e == u.Side) continue;
            bool covered = war.FireUnits.Any(id => war.Units[id].Side == e && war.Units[id].Fires != VClass.Mortar && war.Units[id].People > 0
                                                    && Dist(war.Units[id].X, war.Units[id].Z, tx, tz) < 30_000f);
            if (!covered || war.Rng.NextDouble() >= 0.7) continue;
            var cb = Pick(war, e, tx, tz, float.MaxValue, true, true);
            if (cb == null) continue;
            // The radar fixes the battery to about 0.3% of its range (taken as the counter-battery guns' distance).
            float fix = 20f + 0.003f * Dist(cb.X, cb.Z, tx, tz);
            var ms = Fire(war, cb, tx + fix * Gauss(war.Rng), tz + fix * Gauss(war.Rng), fix, -1, u.Id, true);
            if (ms == null) continue;
            // The radar sees the battery's rounds when they're fired, as they land; the answer takes its own guns' time
            // from then: about 4 minutes with US and British sensor-to-shooter links, 8 with Russian. (It used to land
            // with the rounds it answered, before any battery had time to move, so every one was caught.)
            ms.At = firedAt + (e == 1 ? 480 : 240);
            war.ArtyMissions[e, 2]++;
        }
    }

    /// <summary>Every minute: rounds landing, guns moving after firing; every 10 minutes, fire on what the side has just seen.</summary>
    public static void Step(War war)
    {
        double now = war.Time;
        if ((int)now % 86400 == 0)
            foreach (int id in war.FireUnits) war.Units[id].RoundsToday = 0;
        for (int i = 0; i < war.Missions.Count; i++)
        {
            var ms = war.Missions[i];
            if (ms.At > now) continue;
            Land(war, ms);
            war.Missions[i] = war.Missions[^1];
            war.Missions.RemoveAt(war.Missions.Count - 1);
            i--;
        }
        foreach (int id in war.FireUnits)
        {
            var u = war.Units[id];
            if (u.DisplaceAt <= 0 || now < u.DisplaceAt) continue;
            u.DisplaceAt = 0;
            if (u.People <= 0 || u.InFight >= 0 || u.Path != null) continue;
            double a = war.Rng.NextDouble() * Math.Tau;
            float x = u.X + 1200f * (float)Math.Cos(a), z = u.Z + 1200f * (float)Math.Sin(a);
            var prev = u.Order;
            u.Order = new Order { Kind = OrderKind.Move, X = x, Z = z, At = now, Tactical = true };
            if (!war.StartMove(u, x, z)) u.Order = prev;
        }
        // A mortar platoon on its own keeps within 1.5 km of its company, set up 700 m behind it: its commander looks
        // where the company has got to every 10 minutes. (Looking every minute, the route-finding for platoons left
        // behind took half the war's running time.)
        foreach (int id in war.FireUnits)
        {
            var u = war.Units[id];
            if ((int)now % 600 != 0 || u.Keeps < 0 || u.People <= 0 || u.InFight >= 0 || u.Path != null || now < u.FireReady || u.DisplaceAt > 0) continue;
            var c = war.Units[u.Keeps];
            if (Dist(u.X, u.Z, c.X, c.Z) < 1500f) continue;
            var port = war.Isl.Towns[war.Sides[u.Side].Port];
            float dx = port.X - c.X, dz = port.Z - c.Z, d = MathF.Max(1f, MathF.Sqrt(dx * dx + dz * dz));
            u.Order = new Order { Kind = OrderKind.Move, X = c.X + dx / d * 700f, Z = c.Z + dz / d * 700f, At = now };
            if (!war.StartMove(u, u.Order.X, u.Order.Z)) u.Order.Done = true;
        }
        // A headquarters or support unit shelled moves 1.5 km back out of it, as headquarters do when they're found.
        foreach (int mid in war.MoverIds)
        {
            var u = war.Units[mid];
            if (u.People <= 0 || now - u.ShelledAt > 60 || u.InFight >= 0 || u.Path != null || u.Hauls || u.Guns || Command.Manoeuvre(u)) continue;
            var port = war.Isl.Towns[war.Sides[u.Side].Port];
            float dx = port.X - u.X, dz = port.Z - u.Z, d = MathF.Max(1f, MathF.Sqrt(dx * dx + dz * dz));
            u.Order = new Order { Kind = OrderKind.Move, X = u.X + dx / d * 1500f, Z = u.Z + dz / d * 1500f, At = now, Tactical = true };
            if (!war.StartMove(u, u.Order.X, u.Order.Z)) u.Order.Done = true;
        }
        if ((int)now % 600 == 0) Observed(war);
    }

    /// <summary>Fire on enemy units seen in the last 10 minutes, outside fights and clear of our own troops: the biggest first.</summary>
    static void Observed(War war)
    {
        for (int side = 0; side < 3; side++)
        {
            // A target shelled in the last hour waits for fresh word of what's left there. (The same headquarters used
            // to be shelled every 10 minutes for days, and lost nine in ten of its people.)
            var targets = war.Intel.Known[side]
                .Where(kv => war.Time - kv.Value.At <= 600 && war.Units[kv.Key].People > 0 && war.Units[kv.Key].InFight < 0 && war.Time - war.Units[kv.Key].ShelledAt > 3600)
                .OrderByDescending(kv => kv.Value.Strength + (kv.Value.Armour ? 50 : 0)).Take(20).ToList();
            foreach (var (id, e) in targets)
            {
                float own = float.MaxValue;
                foreach (int mid in war.MoverIds)
                {
                    var u = war.Units[mid];
                    if (u.Side == side && u.People > 0) own = MathF.Min(own, Dist(u.X, u.Z, e.X, e.Z));
                }
                var fu = Pick(war, side, e.X, e.Z, own, e.Strength >= 30, true);
                if (fu == null) continue;
                int tubes = Tubes(war, fu, out int have);
                // Fire on what's merely been seen gets a quarter of the day's allowance at most: priority of fires goes
                // to troops in contact and to the main effort. (Using the whole allowance on it, guns shelled whatever
                // was seen along the whole front every day, and a fifth of all casualties came from that.)
                if (tubes == 0 || fu.RoundsToday * 4 >= tubes * Allowance(side, fu.Fires)) continue;
                // Half the ammunition is kept for close support.
                int full = 0;
                foreach (int ci in fu.Carries)
                    foreach (int vi in war.Units[ci].Vehicles)
                        if (!war.Vehicles[vi].Lost && war.Vehicles[vi].Class == fu.Fires) full += Orbat.VehicleLoad(fu.Fires);
                if (have * 2 < full) continue;
                // Seen from afar and not adjusted: 75 m of error on where it is.
                if (Fire(war, fu, e.X + 75f * Gauss(war.Rng), e.Z + 75f * Gauss(war.Rng), 75f, -1, id, false) != null) war.ArtyMissions[side, 1]++;
            }
        }
    }

    /// <summary>
    /// The preparation before an offensive goes in: in its last hour, up to three batteries each on the enemy units
    /// known to be within 4 km of its axis, the biggest first. Where they were seen may be hours old.
    /// </summary>
    public static void Preparation(War war, Operation op)
    {
        int side = op.Side;
        var axis = op.Axis.Select(id => war.Objectives[id]).ToList();
        var targets = war.Intel.Known[side]
            .Where(kv => war.Units[kv.Key].Side == op.Enemy && war.Units[kv.Key].People > 0 && axis.Any(o => Dist(o.X, o.Z, kv.Value.X, kv.Value.Z) < 4000f))
            .OrderByDescending(kv => kv.Value.Strength).Take(30).ToList();
        foreach (var (id, e) in targets)
        {
            float own = float.MaxValue;
            foreach (int mid in war.MoverIds)
            {
                var u = war.Units[mid];
                if (u.Side == side && u.People > 0) own = MathF.Min(own, Dist(u.X, u.Z, e.X, e.Z));
            }
            for (int k = 0; k < 3; k++)
            {
                var fu = Pick(war, side, e.X, e.Z, own, true, false);
                if (fu == null) break;
                var ms = Fire(war, fu, e.X + 75f * Gauss(war.Rng), e.Z + 75f * Gauss(war.Rng), 75f, -1, id, false);
                if (ms == null) continue;
                ms.Purpose = 3;
                war.ArtyMissions[side, 1]++;
            }
        }
    }

    /// <summary>The rounds land: on a fight's squads, or on a unit outside fights if it's still there.</summary>
    static void Land(War war, Mission ms)
    {
        var rng = war.Rng;
        var cause = CauseOf(ms.Kind);
        if (ms.Fight >= 0)
        {
            var f = war.Fights[ms.Fight];
            if (f.Over) return;
            for (int r = 0; r < ms.Rounds; r++)
                Combat.Shellburst(war, f, ms.X + ms.Sigma * Gauss(rng), ms.Z + ms.Sigma * Gauss(rng), ms.Reach, cause, rng);
            return;
        }
        if (ms.Target < 0) return;
        var t = war.Units[ms.Target];
        if (t.InFight >= 0 || t.People <= 0) return;
        Strike(war, t, ms, cause);
    }

    /// <summary>
    /// Rounds on a unit outside any fight: its soldiers spread over its ground, dug in (after two hours halted), in
    /// their vehicles on the move, or in the open. The first round finds them up; the rest, flat or in their holes.
    /// The down are evacuated (one in twenty dies of wounds on the way); vehicles near a burst are wrecked.
    /// </summary>
    static void Strike(War war, Unit t, Mission ms, Cause cause)
    {
        var rng = war.Rng;
        // Halted, troops scrape shell scrapes within half an hour and dig trenches within two (US hasty and deliberate
        // fighting positions). On the move, they're inside their carriers: armour stops nearly all fragments, a truck's
        // canvas few. (Everyone outside a fight used to have middling cover until two hours had passed, and riders the
        // same whatever they rode in.)
        double halted = t.Path == null ? war.Time - t.HaltedAt : 0;
        bool dug = halted > 7200, scraped = halted > 1800;
        bool mounted = t.Path != null && t.Mob != Mobility.Foot;
        int armouredSeats = 0, softSeats = 0;
        foreach (int ci in t.Carries)
            foreach (int vi in war.Units[ci].Vehicles)
            {
                var v = war.Vehicles[vi];
                if (v.Lost) continue;
                if (v.Class is VClass.Ifv or VClass.Apc or VClass.Tank or VClass.LightTank or VClass.Howitzer or VClass.Mortar or VClass.Spaa or VClass.Engineer) armouredSeats++;
                else if (v.Class is VClass.Truck or VClass.Ltv or VClass.Tanker or VClass.Ambulance or VClass.Rocket) softSeats++;
            }
        float riding = armouredSeats >= softSeats ? 0.1f : 0.5f;
        var bursts = new (float X, float Z)[ms.Rounds];
        for (int r = 0; r < ms.Rounds; r++) bursts[r] = (ms.X + ms.Sigma * Gauss(rng), ms.Z + ms.Sigma * Gauss(rng));
        float R = ms.Reach, reach4 = R * 4f;
        bool? cut = null;
        var vs = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(war.Vehicles);
        foreach (int ci in t.Carries)
        {
            var c = war.Units[ci];
            float ux = t.X + (c == t ? 0f : c.OffX), uz = t.Z + (c == t ? 0f : c.OffZ), side = Combat.Ground(c.Members.Count);
            if (Dist(ux, uz, ms.X, ms.Z) > ms.Sigma * 3f + reach4 + side) continue;
            foreach (int s in c.Members)
            {
                ref var so = ref war.Soldiers[s];
                if (so.State is not (SoldierState.Fit or SoldierState.Wounded)) continue;
                // Where they are and what's round them, fixed by who they are so a soldier doesn't jump between rounds.
                uint h = (uint)s * 2654435761u;
                float sx = ux + ((h & 0xFF) / 255f - 0.5f) * side, sz = uz + (((h >> 8) & 0xFF) / 255f - 0.5f) * side;
                uint roll = (h >> 16) % 100;
                float cover = dug ? (roll < 85 ? 0.15f : 0.6f) : scraped ? (roll < 50 ? 0.35f : 0.6f) : mounted ? riding : 0.6f;
                for (int r = 0; r < bursts.Length; r++)
                {
                    float d = MathF.Max(1f, Dist(sx, sz, bursts[r].X, bursts[r].Z));
                    if (d > reach4) continue;
                    float p = MathF.Min(0.95f, 0.5f * (R / d) * (R / d)) * cover * (r == 0 ? 0.6f : 0.2f);
                    if (rng.NextDouble() < p)
                    {
                        t.ShelledAt = war.Time;
                        war.StruckHow[t.Side, dug ? 4 : scraped ? 3 : t.Path == null ? 2 : mounted ? 0 : 1]++;
                        war.StruckArm[t.Side][t.Arm] = war.StruckArm[t.Side].GetValueOrDefault(t.Arm) + 1;
                        war.StruckUnit[t.Id] = war.StruckUnit.GetValueOrDefault(t.Id) + 1;
                        if (ms.CounterBattery) war.StruckByCb[t.Side]++;
                        war.StruckFor[t.Side, ms.Purpose]++;
                        Wound(war, s, cause, rng, ref cut, t);
                        if (so.State is not (SoldierState.Fit or SoldierState.Wounded)) break;
                    }
                }
            }
            foreach (int vi in c.Vehicles)
            {
                ref var v = ref vs[vi];
                if (v.Lost) continue;
                uint h = (uint)vi * 2246822519u;
                float vx = ux + ((h & 0xFF) / 255f - 0.5f) * 80f, vz = uz + (((h >> 8) & 0xFF) / 255f - 0.5f) * 80f;
                bool armoured = v.Class is VClass.Tank or VClass.LightTank or VClass.Ifv or VClass.Apc or VClass.Howitzer or VClass.Spaa or VClass.Engineer or VClass.Mortar;
                float k = armoured ? 0.12f : 0.4f;
                foreach (var b in bursts)
                {
                    float d = MathF.Max(1f, Dist(vx, vz, b.X, b.Z));
                    if (rng.NextDouble() < MathF.Min(0.9f, 0.5f * (k * R / d) * (k * R / d)))
                    {
                        v.Lost = true;
                        war.Wrecked[t.Side, (int)cause]++;
                        if (ms.CounterBattery && v.Class is VClass.Howitzer or VClass.Rocket or VClass.Mortar) war.GunsLost[t.Side]++;
                        break;
                    }
                }
            }
        }
        war.Recount(t);
        Supply.Measure(war, t);
    }

    /// <summary>A soldier hit by a shell outside a fight: one in ten killed, three in ten carried into the medical chain (as is anyone hit a second time), the rest lightly wounded.</summary>
    static void Wound(War war, int s, Cause cause, Random rng, ref bool? cut, Unit t)
    {
        ref var so = ref war.Soldiers[s];
        war.Hits[so.Side, (int)cause]++;
        war.StruckHit[so.Side]++;
        double r = rng.NextDouble();
        if (r < 0.1)
        {
            so.State = SoldierState.Dead;
            so.Since = war.Time;
            war.Dead[so.Side]++;
            war.Kills[so.Side, (int)cause]++;
        }
        else if (r < 0.4 || so.State == SoldierState.Wounded)
        {
            cut ??= !Supply.ClearToDepot(war, t);
            Medical.Admit(war, s, cut.Value);
        }
        else
        {
            so.State = SoldierState.Wounded;
            so.Since = war.Time;
        }
    }

    static float Gauss(Random rng) => (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble()));
}
