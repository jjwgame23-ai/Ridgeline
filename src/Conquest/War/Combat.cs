namespace Ridgeline;

/// <summary>One soldier in a fight: their place in the squad, how exposed and how hard to spot, how suppressed, and when they can next fire.</summary>
public sealed class Fighter
{
    public int Soldier, Squad;
    public byte Side;
    public float OffX, OffZ;
    /// <summary>How much of them a round can find: 1 in the open, about 0.6 behind brush, 0.15 behind a wall or in a trench.</summary>
    public float Cover;
    /// <summary>How easily they're seen: 1 in the open, down to about 0.3 in buildings or woods.</summary>
    public float Hide;
    public float Supp, Ready, Skill;
    /// <summary>Fired in the last few seconds, which gives a soldier away.</summary>
    public float Fired;
    /// <summary>Gone from the fight with their company.</summary>
    public bool Gone;
}

public enum SquadMode : byte { Engage, Bound, Assault, Withdraw }

/// <summary>What wounded or killed a soldier, for the war's report.</summary>
public enum Cause : byte { Rifle, MachineGun, Marksman, Grenade, VehicleGun, Autocannon, TankShell, Rocket, Bled, LeftBehind, Shell, Mortar }

public sealed class FightSquad
{
    public int Index, Unit, Mover, Side;
    public float X, Z, GoalX, GoalZ, Moving;
    public SquadMode Mode;
    /// <summary>Held flat by fire in the middle of a move: nobody gets up until it lifts.</summary>
    public bool Pinned;
    /// <summary>Bounds made pulling back, so the halves take turns.</summary>
    public int Bounds;
    /// <summary>Dug in where it stands: halted two hours or more when the fight found it.</summary>
    public bool Dug;
    /// <summary>Anyone left in it who can fight, or a vehicle still running, as of the last look.</summary>
    public bool Alive;
    /// <summary>The enemy squads this one can see, as of the last look.</summary>
    public readonly List<int> Visible = new();
    public readonly List<int> Fighters = new();
    public readonly List<int> Vehicles = new();
}

public sealed class FightVehicle
{
    public int Vehicle, Squad;
    public byte Side;
    public float Ready;
    public bool Dead, Gone;
}

/// <summary>A fight: everyone in it, soldier by soldier, with what they can see of each other.</summary>
public sealed class Fight
{
    public int Id;
    public float X, Z;
    public string Where = "";
    public double Started, LastShot, LastHit, NextSight, NextThink, Ended = -1;
    /// <summary>When a squad last moved on the enemy, or a company joined.</summary>
    public double Stirred, Traced = double.MinValue;
    public readonly List<Fighter> F = new();
    public readonly List<FightVehicle> V = new();
    public readonly List<FightSquad> S = new();
    public readonly HashSet<int> Movers = new();
    /// <summary>For each mover, what it came in with (fit soldiers), so its losses are known.</summary>
    public readonly Dictionary<int, int> Strength = new();
    public float[,] Sight = new float[0, 0];
    public readonly int[] Shots = new int[3], Killed = new int[3], Down = new int[3], Hurt = new int[3], Lost = new int[3];
    /// <summary>Soldiers hit in it by cause, vehicles destroyed in it by cause, and the range it opened at.</summary>
    public readonly int[] HitBy = new int[12], WreckedBy = new int[12];
    public float Opened;
    public readonly bool[] In = new bool[3];
    /// <summary>Enemy squads within 600 m of each other, or moving: the fight runs every step, not every third.</summary>
    public bool Close = true;
    /// <summary>The companies attacking: their squads give covering fire.</summary>
    public readonly HashSet<int> Attacking = new();
    public bool Over => Ended >= 0;
}

/// <summary>
/// Contact and fighting, soldier by soldier.
/// Contact. Every minute, units on the move look out for enemies within 3 km, and every 5 minutes everyone does. A
/// fight starts only within 1.5 km (2.5 km if either side has vehicles with guns). A unit is seen by:
/// - the line of sight over the ground, with woods and buildings in the way thinning it;
/// - range;
/// - light: by night a third as well, worse for BRAVO, which has fewer night sights;
/// - how much it shows (moving, large, vehicles).
/// Within 400 m a unit is found regardless.
/// Fighting runs in 4-second steps:
/// - Everyone has a place in their squad, and cover from the ground they're on: buildings and rock give hard cover,
///   woods and scrub give soft cover and concealment, and units dug in for 2 hours or more have trenches.
/// - Posture follows suppression: crouched, then flat.
/// - Each soldier picks a target among the enemy squads their squad can see. Fire discipline: they shoot at someone
///   they can make out, and otherwise fire at the place only to cover their own side's attack or to answer fire coming
///   in. Each weapon fires at its own rate.
///   Making someone out gets harder with range and by night, and needs a clear line to them. A hit is likelier the
///   closer they are, the more of the target shows and the steadier the shooter (skill, suppression, the dark).
///   Machine guns fire bursts, mostly to suppress; grenadiers lob 40 mm; anti-tank gunners go for vehicles.
/// - Vehicles look through thermal sights. Tanks and IFVs go for enemy armour first. At troops they fire the machine
///   gun, and keep main-gun and autocannon shells for soldiers behind walls, anti-tank teams and (autocannon) troops
///   out of the machine gun's reach. Shells and grenades land off the aim by the weapon's error.
/// - A hit kills about one time in five. One in three puts the soldier down: out of the fight, and in a third of
///   those cases dying within the hour or so unless a medic or a squadmate stops the bleeding. Otherwise it's a
///   light wound that lets them go on.
/// - Every 30 seconds each company decides, by the numbers it can see. It attacks at 3:1 against a position dug in,
///   2.5:1 against a hasty one, if it was advancing, bounding half its squads at a time and assaulting from 60 m. It pulls back when outnumbered two to
///   one or after losing three in ten, by bounds too, until it's out of sight. Otherwise it holds and fires. Support
///   units pull back at once. A squad pinned flat by fire doesn't move until the fire lifts.
/// - A fight ends when it has been quiet for 10 minutes, or 15 without anyone hit and nobody attacking, or when only
///   one side is left in it. It also ends when nobody has come on for half an hour: the two sides have gone to
///   ground facing each other, and that's a front, not a fight. The holding side's down are evacuated; the others'
///   left behind die. A company held up halts where it is.
/// The numbers are first estimates. Phase 3 fits them to the full battle simulation's telemetry.
/// </summary>
public static class Combat
{
    const float ContactRange = 3000f, Tick = 4f;
    /// <summary>
    /// A squad's ground: soldiers keep about 10 m apart (FM 3-21.8), so nine of them, scattered, take about 60 by 60 m.
    /// (They used to stand in a 40 m square, so a shell found two or three where it should find one.)
    /// </summary>
    const float Spread = 60f;

    /// <summary>
    /// The side of the square a group of <paramref name="n"/> takes at the same spacing: a squad's 60 m, a headquarters
    /// of 300 about 350 m. (Every group used to stand in 60 m whatever its size, so a big headquarters was hundreds of
    /// men in one square, and shellfire found three in four of them in a week.)
    /// </summary>
    public static float Ground(int n) => Spread * MathF.Sqrt(MathF.Max(1f, n / 9f));

    /// <summary>Look for contact. Every minute for units on the move; <paramref name="all"/> for everyone.</summary>
    public static void Detect(War war, bool all)
    {
        const float Bucket = 2000f;
        var grid = new Dictionary<long, List<Unit>>();
        long Key(float x, float z) => ((long)MathF.Floor(x / Bucket) << 32) ^ (uint)(int)MathF.Floor(z / Bucket);
        foreach (var u in war.Units)
            if (u.IsMover && u.People > 0)
            {
                var k = Key(u.X, u.Z);
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<Unit>();
                l.Add(u);
            }
        float minutes = all ? 5f : 1f;
        var rng = war.Rng;
        foreach (var u in war.Units)
        {
            if (!u.IsMover || u.People <= 0 || (!all && u.Path == null)) continue;
            int bx = (int)MathF.Floor(u.X / Bucket), bz = (int)MathF.Floor(u.Z / Bucket);
            for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++)
            {
                if (!grid.TryGetValue(((long)(bx + dx) << 32) ^ (uint)(bz + dz), out var near)) continue;
                foreach (var v in near)
                {
                    if (v.Side == u.Side || (u.InFight >= 0 && u.InFight == v.InFight)) continue;
                    float d = MathF.Sqrt((v.X - u.X) * (v.X - u.X) + (v.Z - u.Z) * (v.Z - u.Z));
                    if (d > ContactRange) continue;
                    // Fresh from pulling out of a fight, a unit isn't drawn straight back in from a distance.
                    if (d > 800f && (war.Time - u.Broke < 1800 || war.Time - v.Broke < 1800)) continue;
                    bool seen = d < 400f, watched = war.Intel.Known[u.Side].TryGetValue(v.Id, out var known);
                    if (!seen && watched && war.Time - known.At < 300) seen = true; // already being watched
                    bool fresh = !watched || war.Time - known.At > 3600;
                    if (!seen)
                    {
                        float vis = Sight(war, u.X, u.Z, v.X, v.Z, 2f, 2f);
                        if (vis <= 0f) continue;
                        float range = 3f * (1f - d / ContactRange) * (1f - d / ContactRange) + 0.2f;
                        float shows = (v.Path != null ? 1f : 0.45f) * MathF.Sqrt(MathF.Min(4f, v.People / 100f + 0.1f)) * (v.Vehicles.Count > 0 ? 1.5f : 1f);
                        float k = vis * range * shows * Light(war, u.Side) * minutes / 5f;
                        seen = rng.NextDouble() < 1.0 - Math.Exp(-k);
                    }
                    if (seen) war.Intel.Saw(u.Side, v, war.Time);
                    // A headquarters or support unit that sees an enemy ahead halts where it is and waits for orders: it
                    // doesn't march on into a fight. (Battalion headquarters used to fight 50 times a day, running into
                    // the enemy and pulling back over and over.)
                    // A supply convoy, knowing where the front runs, halts only for an enemy close ahead (1.5 km).
                    if (seen && u.InFight < 0 && !Command.Manoeuvre(u) && Closing(u, v, d) && (!u.Hauls || d < 1500f)) Halt(war, u);
                    // Seen further off than rifles and guns reach, it's a sighting, not a fight. A fight takes one side
                    // coming on, or a first sighting inside 600 m, or the two within 200 m. Otherwise a unit pulling
                    // back or passing by, or one facing another it already knows of, watches and reports. (Any unit on
                    // the move used to count as coming on, so a company falling back started a new fight with the one
                    // it had just left; and two holding 500 m apart fought all day.)
                    // A unit holding ground engages whatever moves through its field of fire: defending a sector is
                    // stopping what crosses it. (Garrisons used to watch enemy columns drive past into their rear.)
                    bool coming = d < 200f || (fresh && d < 600f) || Closing(u, v, d) || Closing(v, u, d)
                                  || (v.Path != null && Holding(war, u)) || (u.Path != null && Holding(war, v));
                    if (seen && coming && d <= (u.Armed || v.Armed ? 2500f : 1500f)) Contact(war, u, v);
                }
            }
        }
    }

    /// <summary>Holding ground: a battalion garrisoning an objective, or one of its companies.</summary>
    static bool Holding(War war, Unit u) => u.Holds >= 0 || (u.Parent >= 0 && war.Units[u.Parent].Holds >= 0);

    /// <summary>Whether a unit is on the move toward another: where it's going is at least 300 m nearer the other than it is now.</summary>
    static bool Closing(Unit a, Unit b, float d) =>
        a.Path != null && MathF.Sqrt((a.GoX - b.X) * (a.GoX - b.X) + (a.GoZ - b.Z) * (a.GoZ - b.Z)) < d - 300f;

    /// <summary>How well a side sees at this hour: by day fully; by night with its night sights.</summary>
    static float Light(War war, int side)
    {
        double h = war.Hour;
        bool night = h < 5.5 || h >= 20.5;
        if (!night) return 1f;
        return side == 1 ? 0.25f : 0.35f; // BRAVO issues goggles to fewer of its soldiers
    }

    /// <summary>
    /// How far off a side's soldiers pick out a man who keeps still: by day, half as often at 300 m (rifle optics) as
    /// close in; by night, through goggles, at a third of that (BRAVO, which issues fewer, a quarter).
    /// </summary>
    static float Eye(War war, int side) => 300f * Light(war, side);

    /// <summary>Aiming by night, through goggles and with infrared lasers, at someone made out: about 70% as good as by day.</summary>
    static float Aim(War war) => Light(war, 0) < 1f ? 0.7f : 1f;

    /// <summary>
    /// How far off a vehicle's thermal sight picks out a man who keeps still, day or night: half as often at 800 m as
    /// close in. Its narrow field of view has to be pointed the right way.
    /// </summary>
    const float ThermalEye = 800f;

    static void Contact(War war, Unit a, Unit b)
    {
        if (a.InFight >= 0 && b.InFight >= 0) return; // each already fighting someone else
        var f = a.InFight >= 0 ? war.Fights[a.InFight] : b.InFight >= 0 ? war.Fights[b.InFight] : null;
        // A fight is a local action: a dozen companies at most. Along a front, contacts beyond that are fights of their own.
        if (f != null && f.Movers.Count >= 12) return;
        Rewind(a, b);
        float mx = (a.X + b.X) / 2f, mz = (a.Z + b.Z) / 2f;
        if (f == null)
        {
            f = new Fight { Opened = MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z)), Id = war.Fights.Count, X = mx, Z = mz, Started = war.Time, Stirred = war.Time, LastShot = war.Time, LastHit = war.Time, NextSight = war.Time, NextThink = war.Time };
            f.Where = war.Isl.Towns.OrderBy(t => (t.X - mx) * (t.X - mx) + (t.Z - mz) * (t.Z - mz)).Select(t => t.Name).FirstOrDefault("");
            war.Fights.Add(f);
        }
        Join(war, f, a, b.X, b.Z);
        Join(war, f, b, a.X, a.Z);
    }

    /// <summary>
    /// Contact is looked for once a step, and a column on a road covers 800 m in a minute, so two found within 400 m
    /// (where they'd have seen each other whatever) may have come within it partway through the step. Then they're put
    /// back where they were at that moment, so the fight opens at 400 m. (Fights used to open with companies 55 m
    /// apart, driven into each other between two looks.)
    /// </summary>
    static void Rewind(Unit a, Unit b)
    {
        const float R = 400f;
        bool am = a.InFight < 0, bm = b.InFight < 0; // a unit already in a fight hasn't moved
        float ax0 = am ? a.WasX : a.X, az0 = am ? a.WasZ : a.Z, bx0 = bm ? b.WasX : b.X, bz0 = bm ? b.WasZ : b.Z;
        float rx = bx0 - ax0, rz = bz0 - az0;
        float vx = (b.X - bx0) - (a.X - ax0), vz = (b.Z - bz0) - (a.Z - az0);
        float c0 = rx * rx + rz * rz - R * R, c2 = vx * vx + vz * vz, c1 = 2f * (rx * vx + rz * vz);
        float d1 = (rx + vx) * (rx + vx) + (rz + vz) * (rz + vz);
        if (c0 <= 0f || c2 < 1e-6f || d1 > R * R) return; // within 400 m all along, or never this step
        float disc = c1 * c1 - 4f * c2 * c0;
        if (disc < 0f) return;
        float t = Math.Clamp((-c1 - MathF.Sqrt(disc)) / (2f * c2), 0f, 1f);
        if (am)
        {
            a.X = ax0 + (a.X - ax0) * t;
            a.Z = az0 + (a.Z - az0) * t;
        }
        if (bm)
        {
            b.X = bx0 + (b.X - bx0) * t;
            b.Z = bz0 + (b.Z - bz0) * t;
        }
    }

    static bool IsCrew(Job j) => j is Job.Crewman or Job.Gunner or Job.Driver or Job.Pilot;

    public static bool Armed(VClass v) => v is VClass.Ifv or VClass.Tank or VClass.LightTank or VClass.Apc or VClass.Ltv;

    /// <summary>Armour: what anti-armour weapons go for first.</summary>
    static bool Armoured(VClass v) => v is VClass.Tank or VClass.LightTank or VClass.Ifv or VClass.Apc or VClass.Howitzer or VClass.Spaa or VClass.Engineer or VClass.Mortar;

    /// <summary>The vehicle in a squad to fire at: armour first, then the rest. Ambulances, marked, aren't fired on.</summary>
    static int VehicleTarget(War war, Fight f, FightSquad t)
    {
        int soft = -1;
        foreach (int k in t.Vehicles)
        {
            if (f.V[k].Dead) continue;
            var vc = war.Vehicles[f.V[k].Vehicle].Class;
            if (Armoured(vc)) return k;
            if (soft < 0 && vc != VClass.Ambulance) soft = k;
        }
        return soft;
    }

    static int ArmedLeft(War war, Fight f, FightSquad s) => s.Vehicles.Count(v => !f.V[v].Dead && Armed(war.Vehicles[f.V[v].Vehicle].Class));

    /// <summary>
    /// A company comes into a fight with an enemy at (<paramref name="ex"/>, <paramref name="ez"/>). Its squads deploy in
    /// line facing the enemy about 50 m apart, which gives a company the 500-700 m frontage of doctrine, with its
    /// headquarters 150 m behind. (They used to keep the company's layout from the march, squads scattered up to 550 m
    /// round it, so two companies meeting 150 m apart ended up mixed together, squads 4 m from the enemy's.)
    /// </summary>
    static void Join(War war, Fight f, Unit m, float ex, float ez)
    {
        if (m.InFight >= 0) return;
        m.InFight = f.Id;
        f.Movers.Add(m.Id);
        f.Stirred = war.Time;
        f.In[m.Side] = true;
        var rng = war.Rng;
        bool dug = m.Path == null && war.Time - m.HaltedAt > 7200;
        int fit = 0;
        float fx = ex - m.X, fz = ez - m.Z, fl = MathF.Max(1f, MathF.Sqrt(fx * fx + fz * fz));
        fx /= fl;
        fz /= fl;
        var parts = m.Carries.Select(ci => war.Units[ci]).Where(c => c.Members.Count > 0 || c.Vehicles.Count > 0)
            .OrderBy(c => c.Echelon == Echelon.Squad ? 0 : 1).ToList();
        int line = Math.Max(1, parts.Count(c => c.Echelon == Echelon.Squad)), back = parts.Count - line, li = 0, bi = 0;
        foreach (var c in parts)
        {
            bool forward = c.Echelon == Echelon.Squad || line == parts.Count;
            int k = forward ? li++ : bi++, n = forward ? line : back;
            float along = (k - (n - 1) / 2f) * 50f + (float)(rng.NextDouble() - 0.5) * 20f, behind = forward ? 0f : 150f;
            int si = f.S.Count;
            float side = Ground(c.Members.Count);
            var sq = new FightSquad
            {
                Index = si, Unit = c.Id, Mover = m.Id, Side = m.Side, Dug = dug,
                X = m.X - fz * along - fx * behind, Z = m.Z + fx * along - fz * behind,
            };
            bool hasVehicles = c.Vehicles.Any(v => !war.Vehicles[v].Lost && Armed(war.Vehicles[v].Class));
            foreach (int s in c.Members)
            {
                var so = war.Soldiers[s];
                if (so.State is not (SoldierState.Fit or SoldierState.Wounded)) continue;
                if (hasVehicles && IsCrew(so.Job)) continue; // crews fight from their vehicles
                var fi = new Fighter
                {
                    Soldier = s, Squad = si, Side = (byte)m.Side, OffX = (float)(rng.NextDouble() - 0.5) * side, OffZ = (float)(rng.NextDouble() - 0.5) * side,
                    Skill = People.Skill(war.Seed, s), Ready = (float)rng.NextDouble() * Tick * 2f,
                };
                Ground(war, fi, sq.X + fi.OffX, sq.Z + fi.OffZ, dug, rng);
                sq.Fighters.Add(f.F.Count);
                f.F.Add(fi);
                fit++;
            }
            // Vehicles are there to be shot at, and armed ones shoot. A fighting company's trucks stay back with its
            // trains, a km or more behind; a convoy's or a headquarters' are the unit itself. (Trucks used to be left
            // out of every fight, so a supply convoy could be ambushed without losing one; put into all of them, the
            // companies' own trucks died by the hundred 150 m behind the firing line.)
            bool trains = Command.Manoeuvre(m);
            foreach (int vi in c.Vehicles)
                if (!war.Vehicles[vi].Lost && war.Vehicles[vi].Class is not (VClass.Helicopter or VClass.Gunship) && (Armed(war.Vehicles[vi].Class) || !trains))
                {
                    sq.Vehicles.Add(f.V.Count);
                    f.V.Add(new FightVehicle { Vehicle = vi, Squad = si, Side = (byte)m.Side, Ready = (float)rng.NextDouble() * 8f });
                }
            if (sq.Fighters.Count > 0 || sq.Vehicles.Count > 0) f.S.Add(sq);
        }
        f.Strength[m.Id] = fit;
        f.NextSight = war.Time; // new squads: work out what they can see
    }

    /// <summary>
    /// Cover and concealment from the ground a soldier is on. Buildings and rock often give hard cover. Woods and
    /// scrub give soft cover and hide them. Open ground has the odd wall or ditch, and terraced orchards have their
    /// walls. Dug in, most are in a fighting position.
    /// </summary>
    static void Ground(War war, Fighter fi, float x, float z, bool dug, Random rng)
    {
        var isl = war.Isl;
        int i = isl.CellAtWorld(x, z);
        var cv = i >= 0 ? isl.Land[i] : Cover.Grass;
        var (hard, soft, hide) = cv switch
        {
            Cover.Urban => (0.7f, 0.2f, 0.35f),
            Cover.Pine or Cover.Oak or Cover.Montane => (0.25f, 0.65f, 0.4f),
            Cover.Maquis => (0.1f, 0.75f, 0.4f),
            Cover.Rock => (0.5f, 0.2f, 0.6f),
            Cover.Orchards => (0.3f, 0.3f, 0.7f),
            Cover.Garrigue => (0.15f, 0.35f, 0.8f),
            _ => (0.1f, 0.15f, 1f),
        };
        if (dug) hard = MathF.Max(hard, 0.85f);
        double r = rng.NextDouble();
        fi.Cover = r < hard ? 0.15f : r < hard + soft ? 0.6f : 1f;
        fi.Hide = r < hard ? MathF.Min(hide, 0.5f) : r < hard + soft ? hide : MathF.Min(1f, hide + 0.3f);
    }

    /// <summary>
    /// Line of sight over the island between two points (eye heights in m): 0 if the ground is in the way, thinned by
    /// woods and buildings. Beyond that, ground that looks open on a 100 m grid isn't: hedges, walls, folds and sheds
    /// break most long sight lines. Studies of rolling European country for NATO found about even odds of a clear line
    /// at 1 km. So whatever the grid shows, the chance halves with every kilometre.
    /// </summary>
    public static float Sight(War war, float ax, float az, float bx, float bz, float eyeA, float eyeB)
    {
        var isl = war.Isl;
        float dx = bx - ax, dz = bz - az, d = MathF.Sqrt(dx * dx + dz * dz);
        int steps = Math.Max(1, (int)(d / 50f));
        float ha = Height(isl, ax, az) + eyeA, hb = Height(isl, bx, bz) + eyeB, vis = MathF.Exp(-0.693f * d / 1000f);
        for (int k = 1; k < steps; k++)
        {
            float t = (float)k / steps, x = ax + dx * t, z = az + dz * t;
            float h = Height(isl, x, z), line = ha + (hb - ha) * t;
            if (h > line) return 0f;
            int i = isl.CellAtWorld(x, z);
            if (i < 0) continue;
            // 50 m of woods passes about 60% (a third through 100 m), of streets and buildings 30%.
            float above = line - h;
            var cv = isl.Land[i];
            if (cv is Cover.Pine or Cover.Oak or Cover.Montane && above < 14f) vis *= 0.6f;
            else if (cv == Cover.Maquis && above < 3f) vis *= 0.75f;
            else if (cv == Cover.Urban && above < 10f) vis *= 0.3f;
            if (vis < 0.02f) return 0f;
        }
        return vis;
    }

    static float Height(Island isl, float x, float z)
    {
        float cx = (x + isl.Extent / 2f) / isl.Cell - 0.5f, cz = (z + isl.Extent / 2f) / isl.Cell - 0.5f;
        return MathF.Max(0f, Grid.Sample(isl.Height, isl.N, Math.Clamp(cx, 0f, isl.N - 1f), Math.Clamp(cz, 0f, isl.N - 1f), 0f));
    }

    /// <summary>Run every fight on for <paramref name="dt"/> seconds.</summary>
    public static void Step(War war, double dt)
    {
        foreach (var f in war.Fights)
        {
            if (f.Over) continue;
            war.Doing = $"fight {f.Id}";
            for (float t = 0f; t < dt && !f.Over;)
            {
                int k = f.Close ? 1 : 3;
                for (int i = 0; i < k; i++) Run(war, f, i == k - 1, Tick * k);
                t += Tick * k;
            }
        }
    }

    /// <summary>
    /// One step of a fight. In a stand-off only the last of three runs everything, for the whole <paramref name="span"/>;
    /// the others just tick clocks down. (Suppression, bleeding and first aid used to run once a step either way, so
    /// in a stand-off the wounded bled three times slower.)
    /// </summary>
    static void Run(War war, Fight f, bool full, float span)
    {
        double now = war.Time;
        if (!full)
        {
            foreach (var fi in f.F) fi.Ready -= Tick;
            foreach (var fv in f.V) fv.Ready -= Tick;
            return;
        }
        if (now >= f.NextSight)
        {
            See(war, f);
            f.NextSight = now + 20;
        }
        if (now >= f.NextThink)
        {
            Decide(war, f);
            f.NextThink = now + 30;
            if (f.Over) return;
        }
        var rng = war.Rng;
        foreach (var sq in f.S)
        {
            if (sq.Moving <= 0f)
            {
                sq.Pinned = false;
                continue;
            }
            // With most of the squad pressed flat by fire, nobody gets up to move, as in the battle simulation, where a
            // man can't come up or move while the rounds land. The move goes on when the fire lifts.
            float supp = 0f;
            int up = 0;
            foreach (int k in sq.Fighters)
                if (war.Soldiers[f.F[k].Soldier].State is SoldierState.Fit or SoldierState.Wounded)
                {
                    supp += f.F[k].Supp;
                    up++;
                }
            sq.Pinned = up > 0 && supp > 0.5f * up;
            if (sq.Pinned) continue;
            float speed = sq.Mode == SquadMode.Withdraw ? 3f : sq.Mode == SquadMode.Assault ? 2f : 2.5f;
            float dx = sq.GoalX - sq.X, dz = sq.GoalZ - sq.Z, d = MathF.Sqrt(dx * dx + dz * dz), step = MathF.Min(d, speed * Tick);
            if (d > 0.1f)
            {
                sq.X += dx / d * step;
                sq.Z += dz / d * step;
            }
            sq.Moving -= Tick;
            if (sq.Mode != SquadMode.Withdraw) f.Stirred = now;
            if (sq.Moving <= 0f && sq.Mode != SquadMode.Withdraw) sq.Mode = SquadMode.Engage;
            f.Close = true;
        }
        // Suppression wears off over about 15 s once the fire stops, as in the battle simulation, where it lasts while
        // the shooter's last sight of the man is fresh (15 s, 25 for a machine gunner). (It used to wear off over 6 s,
        // so men under fire were mostly up and aiming, and a company could lose everyone in a 12-minute firefight.)
        float decay = MathF.Exp(-span / 15f);
        foreach (var fi in f.F)
        {
            fi.Supp *= decay;
            fi.Fired = MathF.Max(0f, fi.Fired - span);
        }
        for (int k = 0; k < f.F.Count; k++)
        {
            var fi = f.F[k];
            if (fi.Gone) continue;
            var st = war.Soldiers[fi.Soldier].State;
            if (st is not (SoldierState.Fit or SoldierState.Wounded)) continue;
            var sq = f.S[fi.Squad];
            if (sq.Moving > 0f && !sq.Pinned && sq.Mode != SquadMode.Assault) continue; // on the move: no aimed fire
            fi.Ready -= Tick;
            if (fi.Ready > 0f) continue;
            Shoot(war, f, fi, rng);
        }
        foreach (var fv in f.V)
        {
            if (fv.Dead || fv.Gone || !Armed(war.Vehicles[fv.Vehicle].Class)) continue;
            fv.Ready -= Tick;
            if (fv.Ready > 0f) continue;
            Gun(war, f, fv, rng);
        }
        Bleed(war, f, rng, span);
        if (f.Id == war.Trace && now - f.Traced >= 30) TraceFight(war, f);
        if (now - f.Stirred > 1800 || (now - f.LastHit > 900 && f.Attacking.Count == 0) || now - f.LastShot > 600 || f.In.Count(x => x) < 2) End(war, f);
    }

    /// <summary>A line on a traced fight: for each side, who's fit, hit, suppressed, flat, in cover; what it can see and how far.</summary>
    static void TraceFight(War war, Fight f)
    {
        f.Traced = war.Time;
        var sb = new System.Text.StringBuilder($"  {(war.Time - f.Started) / 60,5:0.0} min {(f.Close ? "close" : "far  ")} att {f.Attacking.Count}:");
        for (int side = 0; side < 3; side++)
        {
            if (!f.Movers.Any(id => war.Units[id].Side == side)) continue;
            var me = f.F.Where(x => x.Side == side && !x.Gone).ToList();
            var fit = me.Where(x => war.Soldiers[x.Soldier].State is SoldierState.Fit or SoldierState.Wounded).ToList();
            var sq = f.S.Where(s => s.Side == side && s.Fighters.Count > 0).ToList();
            int pairs = sq.Sum(s => s.Visible.Count);
            var ds = sq.SelectMany(s => s.Visible.Select(k => MathF.Sqrt((f.S[k].X - s.X) * (f.S[k].X - s.X) + (f.S[k].Z - s.Z) * (f.S[k].Z - s.Z)))).ToList();
            sb.Append($" | {war.Sides[side].Name} fit {fit.Count} hit {f.Killed[side] + f.Down[side] + f.Hurt[side]} shots {f.Shots[side]}");
            if (fit.Count > 0)
                sb.Append($" supp {fit.Average(x => x.Supp):0.00} flat {100 * fit.Count(x => x.Supp > 0.3f) / fit.Count}% cover {100 * fit.Count(x => x.Cover < 1f) / fit.Count}%");
            sb.Append($" sees {pairs} (nearest {(ds.Count > 0 ? ds.Min() : 0):0} m, median {(ds.Count > 0 ? ds.OrderBy(x => x).ElementAt(ds.Count / 2) : 0):0})");
            sb.Append($" moving {sq.Count(s => s.Moving > 0f)}/{sq.Count} {string.Join("", sq.Select(s => s.Mode.ToString()[0]))}");
        }
        war.TraceLines.Add(sb.ToString());
    }

    /// <summary>Squad-to-squad sight, for each pair on opposing sides.</summary>
    static void See(War war, Fight f)
    {
        int n = f.S.Count;
        if (f.Sight.GetLength(0) != n) f.Sight = new float[n, n];
        // A squad with nobody left who can fight is nothing to see or shoot at. (Companies used to go on attacking
        // squads of the dead and wounded, counted as no enemy at all, and one fight ran 27 hours.)
        foreach (var sq in f.S)
        {
            sq.Visible.Clear();
            sq.Alive = Fit(war, f, sq) > 0 || sq.Vehicles.Any(v => !f.V[v].Dead);
        }
        f.Close = false;
        for (int a = 0; a < n; a++)
        for (int b = a + 1; b < n; b++)
        {
            var sa = f.S[a];
            var sb = f.S[b];
            float v = 0f;
            if (sa.Side != sb.Side && sa.Alive && sb.Alive)
            {
                float d2 = (sa.X - sb.X) * (sa.X - sb.X) + (sa.Z - sb.Z) * (sa.Z - sb.Z);
                if (d2 < 1500f * 1500f) v = Sight(war, sa.X, sa.Z, sb.X, sb.Z, 1.5f, 1.5f);
                if (d2 < 600f * 600f) f.Close = true;
            }
            f.Sight[a, b] = f.Sight[b, a] = v;
            if (v > 0f)
            {
                sa.Visible.Add(b);
                sb.Visible.Add(a);
            }
        }
        // Remove the dead and the down from the squads, and see who's still in it on each side.
        Array.Clear(f.In);
        foreach (var sq in f.S)
            foreach (int k in sq.Fighters)
                if (war.Soldiers[f.F[k].Soldier].State is SoldierState.Fit or SoldierState.Wounded && sq.Mode != SquadMode.Withdraw) f.In[sq.Side] = true;
        foreach (var fv in f.V)
            if (!fv.Dead && !fv.Gone && Armed(war.Vehicles[fv.Vehicle].Class) && f.S[fv.Squad].Mode != SquadMode.Withdraw) f.In[fv.Side] = true;
    }

    /// <summary>
    /// Weapons by job: (seconds between shots, rounds a time, hit chance per round at 0 m, range scale m, longest range
    /// m, suppression per round). Combat accuracy is far below the range's. Under fire, soldiers shoot a fraction as
    /// well and much less often. Over a war, armies fired thousands of rounds for every casualty, most of it at places
    /// rather than at people they could see.
    /// A round that cracks past a man, aimed at him, hit or not, puts him down for a while: a rifle round 0.3 of the
    /// way to pinned flat, each of a machine gun's six-round burst 0.15. In the battle simulation a burst of suppressive
    /// fire leaves its man at about 0.64. (Rounds used to add 0.05, so men taking casualties every few seconds stayed up
    /// and aiming: in one firefight a side lost 37 in a minute with 15% of its men flat.)
    /// </summary>
    static (float Cycle, int Rounds, float Acc, float Scale, float Max, float Supp) Weapon(Job j) => j switch
    {
        Job.MachineGunner => (6f, 6, 0.04f, 300f, 900f, 0.15f),
        Job.Marksman => (8f, 1, 0.5f, 450f, 900f, 0.3f),
        Job.Rifleman or Job.TeamLeader or Job.SquadLeader or Job.Grenadier or Job.AntiTank or Job.Medic or Job.Scout or Job.Sergeant or Job.Commander or Job.Engineer
            => (5f, 1, 0.25f, 200f, 500f, 0.3f),
        _ => (6f, 1, 0.2f, 150f, 300f, 0.25f),
    };

    static void Shoot(War war, Fight f, Fighter fi, Random rng)
    {
        ref var so = ref war.Soldiers[fi.Soldier];
        var sq = f.S[fi.Squad];
        if (so.Ammo <= 0)
        {
            fi.Ready = 30f;
            return;
        }
        int ti = PickSquad(f, fi.Squad, rng, out float d);
        if (ti < 0)
        {
            fi.Ready = Tick;
            return;
        }
        var t = f.S[ti];
        var w = Weapon(so.Job);
        if (d > w.Max)
        {
            fi.Ready = Tick;
            return;
        }
        float steady = (0.5f + fi.Skill) * (1f - 0.75f * fi.Supp) * (so.State == SoldierState.Wounded ? 0.7f : 1f) * Aim(war) * Supply.Fed(war, war.Units[sq.Mover]);
        // An anti-tank gunner goes for a vehicle in sight.
        int vt = so.Job == Job.AntiTank && so.Rockets > 0 && d < 500f ? VehicleTarget(war, f, t) : -1;
        if (vt >= 0)
        {
            so.Rockets--;
            f.Shots[fi.Side]++;
            fi.Fired = 8f;
            f.LastShot = war.Time;
            var fv = f.V[vt];
            // A rocket hit kills a light vehicle almost always, an APC or IFV more often than not, a tank one time in four.
            var vc = war.Vehicles[fv.Vehicle].Class;
            double kill = vc switch { VClass.Tank => 0.25, VClass.Ifv => 0.6, VClass.Apc => 0.7, _ => 0.9 };
            if (rng.NextDouble() < 0.4 * Math.Exp(-d / 300.0) * steady) Knock(war, f, fv, kill, rng, Cause.Rocket);
            fi.Ready = 30f;
            return;
        }
        int target = PickFighter(war, f, t, rng, d, f.Sight[fi.Squad, ti], Eye(war, fi.Side));
        if (target < 0)
        {
            // Nobody to aim at. Fire at the place only to cover an attack, or to answer fire coming in, and only where
            // the weapon can make it tell (rifles 300 m, machine guns 800 m). Otherwise hold fire and watch.
            bool covering = f.Attacking.Contains(sq.Mover);
            bool answering = fi.Supp > 0.15f;
            if (!(covering || answering) || d > (so.Job == Job.MachineGunner ? 800f : 300f))
            {
                fi.Ready = 8f;
                return;
            }
        }
        // A grenadier lobs a 40 mm every few shots at 50-350 m, at whoever's made out or at the place. It lands off the
        // aim by about 1% of the range each way, more with a shaky hand.
        if (so.Job == Job.Grenadier && so.Grenades > 0 && d is > 50f and < 350f && rng.NextDouble() < 0.35)
        {
            so.Grenades--;
            f.Shots[fi.Side]++;
            fi.Fired = 6f;
            f.LastShot = war.Time;
            Burst(war, f, t, target, (1f + 0.01f * d) / MathF.Max(0.25f, steady), 5f, 0.35f, rng, Cause.Grenade, d);
            fi.Ready = 12f;
            return;
        }
        int rounds = Math.Min(w.Rounds, (int)so.Ammo);
        so.Ammo -= (short)rounds;
        f.Shots[fi.Side] += rounds;
        fi.Fired = 6f;
        f.LastShot = war.Time;
        if (target >= 0)
        {
            var tf = f.F[target];
            float p = w.Acc * MathF.Exp(-d / w.Scale) * Exposure(tf, t) * steady;
            var cause = so.Job switch { Job.MachineGunner => Cause.MachineGun, Job.Marksman => Cause.Marksman, _ => Cause.Rifle };
            for (int r = 0; r < rounds; r++)
                if (rng.NextDouble() < p) Hit(war, f, target, rng, cause, d);
            tf.Supp = MathF.Min(1f, tf.Supp + w.Supp * rounds);
        }
        // The rest of the squad hears aimed rounds go by; fire at the place spreads its near misses over them all. Now and
        // then it finds someone.
        float spill = target < 0 ? 1f / MathF.Max(1, t.Fighters.Count) : 0.03f;
        foreach (int k in t.Fighters)
        {
            var o = f.F[k];
            o.Supp = MathF.Min(1f, o.Supp + w.Supp * rounds * spill);
        }
        if (target < 0 && t.Fighters.Count > 0 && rng.NextDouble() < 0.001 * rounds * Math.Exp(-d / w.Scale))
        {
            int k = t.Fighters[rng.Next(t.Fighters.Count)];
            var o = f.F[k];
            if (war.Soldiers[o.Soldier].State is SoldierState.Fit or SoldierState.Wounded && rng.NextDouble() < Exposure(o, t) * 2f)
                Hit(war, f, k, rng, so.Job == Job.MachineGunner ? Cause.MachineGun : Cause.Rifle, d);
        }
        fi.Ready = w.Cycle * (1f + 2f * fi.Supp) * (0.8f + 0.4f * (float)rng.NextDouble()) * (so.State == SoldierState.Wounded ? 1.4f : 1f);
    }

    /// <summary>
    /// A vehicle's guns at a squad it can see, through thermal sights by day or night. Armour comes first for guns that
    /// can hurt it. At troops, the machine gun, with main-gun and autocannon shells kept for what's worth one, as
    /// gunnery doctrine has it: soldiers behind walls or in buildings, anti-tank teams, and for autocannon troops out
    /// of the machine gun's reach.
    /// </summary>
    static void Gun(War war, Fight f, FightVehicle fv, Random rng)
    {
        ref var v = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(war.Vehicles)[fv.Vehicle];
        int ti = PickSquad(f, fv.Squad, rng, out float d);
        if (ti < 0 || (v.Ammo <= 0 && v.Mg <= 0))
        {
            fv.Ready = Tick;
            return;
        }
        var t = f.S[ti];
        float vis = f.Sight[fv.Squad, ti];
        // On a clear line an enemy vehicle is made out three looks in ten (hull-down, among trees and walls, seldom
        // whole), through thermal sights half as often at 3 km as close in. Light trucks and jeeps only when there's no
        // armour to engage. (Vehicles used to be made out at the same rate at any range, which set tanks duelling
        // across 2.5 km of open country all day.)
        var armour = t.Vehicles.Where(x => !f.V[x].Dead && war.Vehicles[f.V[x].Vehicle].Class != VClass.Ambulance).ToList();
        var heavy = armour.Where(x => Armoured(war.Vehicles[f.V[x].Vehicle].Class)).ToList();
        if (heavy.Count > 0) armour = heavy;
        if (armour.Count > 0 && v.Ammo > 0 && v.Class is VClass.Tank or VClass.Ifv or VClass.LightTank && d < 2500f
            && rng.NextDouble() < 0.3 * vis / (1.0 + d * d / 9e6))
        {
            v.Ammo--;
            f.Shots[fv.Side]++;
            f.LastShot = war.Time;
            var target = f.V[armour[rng.Next(armour.Count)]];
            var tc = war.Vehicles[target.Vehicle].Class;
            // A tank gun against a tank's front about one in two; autocannon against heavy armour hardly ever.
            double kill = v.Class == VClass.Ifv
                ? tc switch { VClass.Tank => 0.02, VClass.Ifv => 0.3, VClass.Apc => 0.6, _ => 0.9 }
                : tc switch { VClass.Tank => 0.5, _ => 0.9 };
            if (rng.NextDouble() < 0.4 * Math.Exp(-d / 1200.0)) Knock(war, f, target, kill, rng, v.Class == VClass.Ifv ? Cause.Autocannon : Cause.TankShell);
            fv.Ready = v.Class == VClass.Ifv ? 8f : 12f;
            return;
        }
        int seen = PickFighter(war, f, t, rng, d, vis, ThermalEye);
        if (seen < 0)
        {
            fv.Ready = 20f; // nothing worth a round in sight: the crew looks again in a while
            return;
        }
        f.LastShot = war.Time;
        var o = f.F[seen];
        // A shell is worth it on someone behind hard cover, or an anti-tank gunner. (Tanks used to fire HE at anyone they
        // saw, and caused more casualties than all the infantry's weapons together.) A tank's fire control puts a shell
        // within about half a mil of the range of the aim; an autocannon's bursts spread about one and a half.
        bool worth = o.Cover < 0.2f || war.Soldiers[o.Soldier] is { Job: Job.AntiTank, Rockets: > 0 };
        if (v.Class is VClass.Tank or VClass.LightTank && worth && v.Ammo > 0 && d < 2000f)
        {
            v.Ammo--;
            f.Shots[fv.Side]++;
            Burst(war, f, t, seen, 1f + 0.0005f * d, 15f, 0.6f, rng, Cause.TankShell, d);
            fv.Ready = 30f;
        }
        else if (v.Class == VClass.Ifv && (worth || d > 800f) && v.Ammo > 0 && d < 1500f)
        {
            int n = Math.Min(3, (int)v.Ammo);
            v.Ammo -= (short)n;
            f.Shots[fv.Side] += n;
            for (int r = 0; r < n; r++) Burst(war, f, t, seen, 1f + 0.0015f * d, 3f, 0.15f, rng, Cause.Autocannon, d);
            fv.Ready = 12f;
        }
        else if (v.Mg > 0 && d < (v.Class == VClass.Ltv ? 800f : 1100f))
        {
            // Machine guns in bursts of eight: a coax, an APC's roof gun, a jeep's (only inside 800 m).
            int n = Math.Min(8, (int)v.Mg);
            v.Mg -= (short)n;
            f.Shots[fv.Side] += n;
            float p = 0.04f * MathF.Exp(-d / 600f) * Exposure(o, t);
            for (int r = 0; r < n; r++)
                if (rng.NextDouble() < p) Hit(war, f, seen, rng, Cause.VehicleGun, d);
            foreach (int k in t.Fighters) f.F[k].Supp = MathF.Min(1f, f.F[k].Supp + (k == seen ? 1f : 0.25f));
            fv.Ready = 8f;
        }
        else fv.Ready = Tick * 2f;
    }

    /// <summary>
    /// A shell or grenade among a squad, aimed at one of them (<paramref name="aimed"/>), or with nobody made out at
    /// somewhere in the squad's ground. It lands off the aim point by the weapon's error, scattered
    /// <paramref name="sigma"/> m each way (one standard deviation). (It used to land exactly on the one aimed at.) Its
    /// fragments put a man standing in the open out of action with an even chance at <paramref name="reach"/> m (40 mm
    /// 5 m, 30 mm 3, 120/125 mm HE 15), falling off as the square of distance. These are casualty radii, shorter than
    /// the battle maps' reach for any fragment hit at all. Lying flat cuts the chance to a third of a crouching man's,
    /// and cover cuts it more. Everyone near is suppressed.
    /// </summary>
    static void Burst(War war, Fight f, FightSquad t, int aimed, float sigma, float reach, float supp, Random rng, Cause cause, float d)
    {
        float ax = aimed >= 0 ? f.F[aimed].OffX : (float)(rng.NextDouble() - 0.5) * Spread;
        float az = aimed >= 0 ? f.F[aimed].OffZ : (float)(rng.NextDouble() - 0.5) * Spread;
        float ix = t.X + ax + sigma * Gauss(rng), iz = t.Z + az + sigma * Gauss(rng);
        foreach (int k in t.Fighters)
        {
            var o = f.F[k];
            if (war.Soldiers[o.Soldier].State is not (SoldierState.Fit or SoldierState.Wounded)) continue;
            float dx = t.X + o.OffX - ix, dz = t.Z + o.OffZ - iz, dist = MathF.Max(1f, MathF.Sqrt(dx * dx + dz * dz));
            float p = MathF.Min(0.95f, 0.5f * (reach / dist) * (reach / dist)) * o.Cover * (o.Supp > 0.3f ? 0.2f : 0.6f);
            if (rng.NextDouble() < p) Hit(war, f, k, rng, cause, d);
            if (dist < reach * 4f) o.Supp = MathF.Min(1f, o.Supp + supp);
        }
    }

    /// <summary>
    /// A shell or mortar bomb landing among a fight's squads, whoever's they are: soldiers by the casualty radius as for
    /// any burst, everyone within four radii pinned, and vehicles near it wrecked (armour only by a near-direct hit).
    /// </summary>
    public static void Shellburst(War war, Fight f, float ix, float iz, float reach, Cause cause, Random rng)
    {
        float far = reach * 4f;
        foreach (var t in f.S)
        {
            float half = Ground(t.Fighters.Count) / 2f;
            if (!t.Alive || MathF.Abs(t.X - ix) > far + half || MathF.Abs(t.Z - iz) > far + half) continue;
            foreach (int k in t.Fighters)
            {
                var o = f.F[k];
                if (o.Gone || war.Soldiers[o.Soldier].State is not (SoldierState.Fit or SoldierState.Wounded)) continue;
                float dx = t.X + o.OffX - ix, dz = t.Z + o.OffZ - iz, dist = MathF.Max(1f, MathF.Sqrt(dx * dx + dz * dz));
                if (dist > far) continue;
                float p = MathF.Min(0.95f, 0.5f * (reach / dist) * (reach / dist)) * o.Cover * (o.Supp > 0.3f ? 0.2f : 0.6f);
                if (rng.NextDouble() < p) Hit(war, f, k, rng, cause, -1f);
                o.Supp = MathF.Min(1f, o.Supp + 0.8f);
            }
            foreach (int v in t.Vehicles)
            {
                var fv = f.V[v];
                if (fv.Dead || fv.Gone) continue;
                float kr = (Armoured(war.Vehicles[fv.Vehicle].Class) ? 0.12f : 0.4f) * reach;
                float dist = MathF.Max(1f, MathF.Sqrt((t.X - ix) * (t.X - ix) + (t.Z - iz) * (t.Z - iz)));
                if (rng.NextDouble() < MathF.Min(0.9f, 0.5f * (kr / dist) * (kr / dist))) Knock(war, f, fv, 1.0, rng, cause);
            }
        }
    }

    /// <summary>A standard normal deviate (Box-Muller).</summary>
    static float Gauss(Random rng) => (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble()));

    /// <summary>A vehicle hit by an anti-armour weapon: destroyed with probability <paramref name="kill"/>, and then its crew are casualties or bail out.</summary>
    internal static void Knock(War war, Fight f, FightVehicle fv, double kill, Random rng, Cause cause)
    {
        if (fv.Dead || rng.NextDouble() >= kill) return;
        fv.Dead = true;
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(war.Vehicles);
        span[fv.Vehicle].Lost = true;
        f.Lost[fv.Side]++;
        war.Wrecked[fv.Side, (int)cause]++;
        f.WreckedBy[(int)cause]++;
        var sq = f.S[fv.Squad];
        var unit = war.Units[sq.Unit];
        int crew = 0;
        foreach (int s in unit.Members)
        {
            ref var so = ref war.Soldiers[s];
            if (!IsCrew(so.Job) || so.State is not (SoldierState.Fit or SoldierState.Wounded) || crew >= War.Crew(war.Vehicles[fv.Vehicle].Class)) continue;
            crew++;
            double r = rng.NextDouble();
            if (r < 0.35) Die(war, f, s, cause);
            else if (r < 0.6) GoDown(war, f, s);
            // The rest bail out and fight on foot.
            else sq.Fighters.Add(AddFighter(war, f, s, fv.Squad, rng));
        }
    }

    static int AddFighter(War war, Fight f, int s, int squad, Random rng)
    {
        // Bailed out by their vehicle: in the open, shaken, somewhere in the squad's ground.
        var fi = new Fighter
        {
            Soldier = s, Squad = squad, Side = war.Soldiers[s].Side, Skill = People.Skill(war.Seed, s), Ready = Tick, Cover = 1f, Hide = 1f, Supp = 0.8f,
            OffX = (float)(rng.NextDouble() - 0.5) * Spread, OffZ = (float)(rng.NextDouble() - 0.5) * Spread,
        };
        f.F.Add(fi);
        return f.F.Count - 1;
    }

    /// <summary>How much of a soldier a round can find: cover, and posture (flat when suppressed, crouched otherwise, upright when moving).</summary>
    static float Exposure(Fighter fi, FightSquad sq)
    {
        if (sq.Moving > 0f && !sq.Pinned) return 0.6f; // upright but hard to follow
        float posture = fi.Supp > 0.3f ? 0.3f : 0.55f;
        return fi.Cover * posture;
    }

    /// <summary>An enemy squad this squad can see, the nearer and the more visible the likelier; -1 if none.</summary>
    static int PickSquad(Fight f, int si, Random rng, out float dist)
    {
        dist = 0f;
        var me = f.S[si];
        int best = -1;
        double bw = 0;
        foreach (int k in me.Visible)
        {
            float v = f.Sight[si, k];
            var o = f.S[k];
            if (o.Fighters.Count == 0 && o.Vehicles.Count == 0) continue;
            float d = MathF.Sqrt((o.X - me.X) * (o.X - me.X) + (o.Z - me.Z) * (o.Z - me.Z)) + 1f;
            double w = v / (d * d) * (o.Fighters.Count + 3 * o.Vehicles.Count) * rng.NextDouble();
            if (w > bw)
            {
                bw = w;
                best = k;
                dist = d;
            }
        }
        return best;
    }

    /// <summary>
    /// A soldier in the target squad who can be made out this time, -1 if none. Seldom, unless they're firing, moving or
    /// close (inside 100 m it rises sharply), and less often the further off: half as often at <paramref name="d50"/> m
    /// as close in. Each look needs a clear line to them as well, which the squads have with chance
    /// <paramref name="vis"/>. Flat on the ground and still, they're half as easy to make out. (The chance used to be the
    /// same at 1500 m as at 150, and with a clear line or not, so gunners found targets across the whole fight.)
    /// </summary>
    static int PickFighter(War war, Fight f, FightSquad t, Random rng, float d, float vis, float d50)
    {
        int best = -1;
        double bw = 0, close = Math.Min(10.0, 1.0 + 10000.0 / Math.Max(100.0, d * d)), far = vis / (1.0 + (d / d50) * (d / d50));
        foreach (int k in t.Fighters)
        {
            var o = f.F[k];
            if (war.Soldiers[o.Soldier].State is not (SoldierState.Fit or SoldierState.Wounded)) continue;
            double shows = o.Hide * (o.Fired > 0f ? 2.5 : 1.0) * (t.Moving > 0f && !t.Pinned ? 2.5 : o.Supp > 0.3f ? 0.5 : 1.0);
            double seen = Math.Min(1.0, shows * 0.04 * close) * far;
            double w = rng.NextDouble() * seen;
            if (w > bw && rng.NextDouble() < seen)
            {
                bw = w;
                best = k;
            }
        }
        return best;
    }

    /// <summary>
    /// A soldier hit. About one in five is killed outright (head, heart, great vessels). One in three is put down: out
    /// of the fight, and dying without help. The rest are lightly wounded and go on. A second wound puts them down.
    /// </summary>
    internal static void Hit(War war, Fight f, int k, Random rng, Cause cause, float d)
    {
        int s = f.F[k].Soldier;
        ref var so = ref war.Soldiers[s];
        war.Hits[so.Side, (int)cause]++;
        f.HitBy[(int)cause]++;
        if (d >= 0f) war.HitsAt[d < 25f ? 0 : d < 50f ? 1 : d < 100f ? 2 : d < 200f ? 3 : d < 400f ? 4 : d < 800f ? 5 : 6]++;
        f.LastHit = war.Time;
        // Fragments wound more often than they kill: a third of the severe outcomes of a bullet.
        bool fragment = cause is Cause.Grenade or Cause.Autocannon or Cause.TankShell;
        double r = rng.NextDouble(), killed = fragment ? 0.1 : 0.2, down = fragment ? 0.4 : 0.55;
        if (r < killed) Die(war, f, s, cause);
        else if (r < down || so.State == SoldierState.Wounded) GoDown(war, f, s);
        else
        {
            so.State = SoldierState.Wounded;
            so.Since = war.Time;
            f.Hurt[so.Side]++;
        }
    }

    static void Die(War war, Fight f, int s, Cause cause)
    {
        ref var so = ref war.Soldiers[s];
        if (so.State == SoldierState.Dead) return;
        so.State = SoldierState.Dead;
        so.Since = war.Time;
        f.Killed[so.Side]++;
        war.Dead[so.Side]++;
        war.Kills[so.Side, (int)cause]++;
    }

    static void GoDown(War war, Fight f, int s)
    {
        ref var so = ref war.Soldiers[s];
        if (so.State is SoldierState.Dead or SoldierState.Down) return;
        so.State = SoldierState.Down;
        so.Since = war.Time;
        so.Treated = false;
        // A third of serious wounds bleed fatally without help (the rest are broken bones, wounds that stop of
        // themselves): their blood is marked low.
        so.Blood = war.Rng.NextDouble() < 0.3 ? 0.4f : 0.7f;
        f.Down[so.Side]++;
    }

    /// <summary>
    /// The down. The fatal bleeders die in half an hour on average without help: haemorrhage is the main killer of
    /// the wounded, mostly in the first hour. A medic of their squad, fit and not pinned down, gets a tourniquet or
    /// dressing on within a few minutes; without one, a squadmate does, a little later. Then they keep.
    /// </summary>
    static void Bleed(War war, Fight f, Random rng, float span)
    {
        double lambda = span / 1800.0, ticks = span / Tick;
        foreach (var sq in f.S)
        {
            bool medic = sq.Fighters.Any(k => war.Soldiers[f.F[k].Soldier] is { Job: Job.Medic, State: SoldierState.Fit } && f.F[k].Supp < 0.5f);
            foreach (int k in sq.Fighters)
            {
                ref var so = ref war.Soldiers[f.F[k].Soldier];
                if (so.State != SoldierState.Down || so.Treated) continue;
                double since = war.Time - so.Since;
                if (since > (medic ? 180 : 300) && rng.NextDouble() < 1.0 - Math.Pow(medic ? 0.95 : 0.98, ticks)) so.Treated = true;
                else if (so.Blood < 0.5f && rng.NextDouble() < lambda) Die(war, f, f.F[k].Soldier, Cause.Bled);
            }
        }
    }

    /// <summary>Each company's call, every 30 seconds, by the numbers it can see.</summary>
    static void Decide(War war, Fight f)
    {
        foreach (int mid in f.Movers)
        {
            var m = war.Units[mid];
            if (m.InFight != f.Id) continue;
            var mine = f.S.Where(s => s.Mover == mid).ToList();
            int own = mine.Sum(s => Fit(war, f, s) + 4 * ArmedLeft(war, f, s));
            if (own == 0)
            {
                Leave(war, f, m);
                continue;
            }
            // The enemy this company can see.
            var seen = new HashSet<int>();
            foreach (var s in mine)
                foreach (int k in s.Visible) seen.Add(k);
            int enemy = seen.Sum(k => Fit(war, f, f.S[k]) + 4 * ArmedLeft(war, f, f.S[k]));
            if (seen.Count == 0)
            {
                // Nobody in sight: a company pulling back has broken contact; one that was going somewhere goes on; one
                // that wasn't, waits.
                if (mine.Any(s => s.Mode == SquadMode.Withdraw) || (m.Path != null && war.Time - f.Started > 300)) Leave(war, f, m);
                continue;
            }
            float ex = 0f, ez = 0f;
            foreach (int k in seen)
            {
                ex += f.S[k].X;
                ez += f.S[k].Z;
            }
            ex /= seen.Count;
            ez /= seen.Count;
            Artillery.Call(war, f, m, mine, seen);
            int came = f.Strength.GetValueOrDefault(mid, own);
            // Pull back when outnumbered two to one, or after losing three in ten.
            bool beaten = own * 2 < enemy || own * 10 < came * 7;
            bool advancing = m.Path != null;
            if (!Command.Manoeuvre(m) || beaten)
            {
                Withdraw(war, f, m, mine, ex, ez);
                continue;
            }
            // The odds to go in at, after US planning ratios (FM 3-90): three to one against a prepared position, two and
            // a half against a hasty one. (It used to be two to one against anything.)
            float odds = seen.Count(k => f.S[k].Dug) * 2 > seen.Count ? 3f : 2.5f;
            if (advancing && own >= odds * enemy)
            {
                Attack(f, mine, ex, ez, war.Rng);
                f.Attacking.Add(mid);
            }
            else
            {
                f.Attacking.Remove(mid);
                foreach (var s in mine) if (s.Mode == SquadMode.Withdraw) s.Mode = SquadMode.Engage;
            }
        }
    }

    static int Fit(War war, Fight f, FightSquad s) =>
        s.Fighters.Count(k => war.Soldiers[f.F[k].Soldier].State is SoldierState.Fit or SoldierState.Wounded);

    /// <summary>Fire and movement: half the squads bound 40 m toward the enemy while the rest fire, and from 60 m they all assault.</summary>
    static void Attack(Fight f, List<FightSquad> mine, float ex, float ez, Random rng)
    {
        int moving = mine.Count(s => s.Moving > 0f);
        foreach (var s in mine)
        {
            if (s.Moving > 0f) continue;
            // The nearest enemy squad to this one.
            float bd = float.MaxValue, tx = ex, tz = ez;
            foreach (var o in f.S)
            {
                if (o.Side == s.Side || !o.Alive) continue;
                float d = (o.X - s.X) * (o.X - s.X) + (o.Z - s.Z) * (o.Z - s.Z);
                if (d < bd)
                {
                    bd = d;
                    tx = o.X;
                    tz = o.Z;
                }
            }
            float dist = MathF.Sqrt(bd);
            if (dist < 60f)
            {
                s.Mode = SquadMode.Assault;
                s.GoalX = tx;
                s.GoalZ = tz;
                s.Moving = 20f;
                continue;
            }
            if (moving * 2 >= mine.Count) continue;
            float step = MathF.Min(40f, dist - 30f) / MathF.Max(1f, dist);
            s.Mode = SquadMode.Bound;
            s.GoalX = s.X + (tx - s.X) * step;
            s.GoalZ = s.Z + (tz - s.Z) * step;
            s.Moving = 16f;
            moving++;
        }
    }

    /// <summary>
    /// Pulling back by bounds: half the squads go 200 m back while the other half stay down and cover them with fire,
    /// then they change over, until the company is out of the enemy's sight or 1.5 km off (FM 3-90's withdrawal under
    /// pressure). (They used to all get up and go at once, upright under the enemy's guns, and companies pulling back
    /// took most of the losses in the big fights.)
    /// </summary>
    static void Withdraw(War war, Fight f, Unit m, List<FightSquad> mine, float ex, float ez)
    {
        float dx = 0f, dz = 0f;
        bool clear = true;
        foreach (var s in mine)
        {
            dx += s.X - ex;
            dz += s.Z - ez;
            float sx = s.X - ex, sz = s.Z - ez;
            if (sx * sx + sz * sz < 1500f * 1500f) clear = false;
            s.Mode = SquadMode.Withdraw;
        }
        if (clear)
        {
            Leave(war, f, m);
            return;
        }
        if (mine.Any(s => s.Moving > 0f && !s.Pinned)) return; // a bound is still on (a pinned squad doesn't hold the rest)
        float d = MathF.Max(1f, MathF.Sqrt(dx * dx + dz * dz));
        // The half that has made fewer bounds goes next, the nearest the enemy first.
        var next = mine.OrderBy(s => s.Bounds).ThenBy(s => (s.X - ex) * (s.X - ex) + (s.Z - ez) * (s.Z - ez)).Take((mine.Count + 1) / 2);
        foreach (var s in next)
        {
            s.GoalX = s.X + dx / d * 200f;
            s.GoalZ = s.Z + dz / d * 200f;
            s.Moving = 200f / 3f;
            s.Bounds++;
        }
    }

    /// <summary>A company leaves the fight: it goes on with its order, or (if it pulled back) heads 2 km further back.</summary>
    static void Leave(War war, Fight f, Unit m)
    {
        var mine = f.S.Where(s => s.Mover == m.Id).ToList();
        bool fell = mine.Any(s => s.Mode == SquadMode.Withdraw);
        Settle(war, f, m, mine, !fell);
        m.InFight = -1;
        m.Broke = war.Time;
        if (fell) FallBack(war, f, m);
        else Resume(war, m, Standing(war, f));
        // Its soldiers and vehicles are out of this fight. (They used to stay on its roll and go on shooting from where
        // they'd been, out of reach of any reply, and twice over if the company came back.)
        foreach (var s in mine)
        {
            foreach (int k in s.Fighters) f.F[k].Gone = true;
            foreach (int k in s.Vehicles) f.V[k].Gone = true;
            s.Fighters.Clear();
            s.Vehicles.Clear();
        }
    }

    /// <summary>
    /// Write a company's fight back into the war: where its squads ended up, and its losses. Its down are evacuated if
    /// it holds the field. If it pulled back, it carries out all but a few of its fatal bleeders not yet treated, and
    /// the ones left die.
    /// </summary>
    static void Settle(War war, Fight f, Unit m, List<FightSquad> mine, bool heldField)
    {
        float x = 0f, z = 0f;
        int n = 0;
        foreach (var s in mine)
        {
            x += s.X;
            z += s.Z;
            n++;
        }
        if (n > 0)
        {
            m.X = x / n;
            m.Z = z / n;
        }
        foreach (var s in mine)
            foreach (int k in s.Fighters)
            {
                ref var so = ref war.Soldiers[f.F[k].Soldier];
                if (so.State != SoldierState.Down) continue;
                if (heldField || so.Treated || so.Blood >= 0.5f || war.Rng.NextDouble() < 0.8) Evacuate(war, f, ref so);
                else Die(war, f, f.F[k].Soldier, Cause.LeftBehind);
            }
        war.Recount(m);
    }

    /// <summary>
    /// A company that was beaten off goes 2 km back, away from the enemy that beat it and toward its own rear (half
    /// one, half the other). That holds up its battalion too. (It used to head straight for its port, and when an enemy
    /// was going the same way it was caught again and again: a headquarters was run down 238 times in a week.)
    /// </summary>
    static void FallBack(War war, Fight f, Unit m)
    {
        var port = war.Isl.Towns[war.Sides[m.Side].Port];
        float px = port.X - m.X, pz = port.Z - m.Z, pl = MathF.Max(1f, MathF.Sqrt(px * px + pz * pz));
        float ex = 0f, ez = 0f;
        int n = 0;
        foreach (var s in f.S)
            if (s.Side != m.Side && s.Alive)
            {
                ex += s.X;
                ez += s.Z;
                n++;
            }
        float dx = px / pl, dz = pz / pl;
        if (n > 0)
        {
            float ax = m.X - ex / n, az = m.Z - ez / n, al = MathF.Max(1f, MathF.Sqrt(ax * ax + az * az));
            dx += ax / al;
            dz += az / al;
        }
        float d = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dz * dz));
        m.HeldUp = war.Time;
        m.Order = new Order { Kind = OrderKind.Move, X = m.X + dx / d * 2000f, Z = m.Z + dz / d * 2000f, At = war.Time, Tactical = true };
        if (!war.StartMove(m, m.Order.X, m.Order.Z)) m.Order.Done = true;
    }

    /// <summary>The companies still standing in a fight, not pulling back: their side, and where their squads are.</summary>
    static List<(int Side, float X, float Z)> Standing(War war, Fight f)
    {
        var standing = new List<(int Side, float X, float Z)>();
        foreach (int id in f.Movers)
        {
            if (war.Units[id].InFight != f.Id) continue;
            var sq = f.S.Where(s => s.Mover == id && s.Mode != SquadMode.Withdraw && Fit(war, f, s) > 0).ToList();
            if (sq.Count > 0) standing.Add((war.Units[id].Side, sq.Average(s => s.X), sq.Average(s => s.Z)));
        }
        return standing;
    }

    /// <summary>
    /// A company out of a fight goes on with its move, unless it's held up: enemies still standing within 1.5 km, and
    /// nearer where it's going than it is. Then it halts where it is and its move is over; its battalion learns of it
    /// and decides what next. (Companies used to march straight on into the same enemy, so the median company fought
    /// seven times a day, and one 73 times.)
    /// </summary>
    static void Resume(War war, Unit m, List<(int Side, float X, float Z)> standing)
    {
        if (m.Path == null) return;
        float gx = m.GoX, gz = m.GoZ, left = (m.X - gx) * (m.X - gx) + (m.Z - gz) * (m.Z - gz);
        bool blocked = standing.Any(e => e.Side != m.Side && (e.X - m.X) * (e.X - m.X) + (e.Z - m.Z) * (e.Z - m.Z) < 1500f * 1500f
                                         && (e.X - gx) * (e.X - gx) + (e.Z - gz) * (e.Z - gz) < left);
        if (!blocked)
        {
            war.StartMove(m, gx, gz);
            return;
        }
        Halt(war, m);
    }

    /// <summary>Held up: a unit halts where it is, its move over, for its commander to decide what next.</summary>
    static void Halt(War war, Unit m)
    {
        m.Path = null;
        m.HaltedAt = m.HeldUp = war.Time;
        if (m.Order != null) m.Order.Done = true;
    }

    static void Evacuate(War war, Fight f, ref Soldier so)
    {
        so.State = SoldierState.Evacuated;
        war.Evacuated[so.Side]++;
    }

    static void End(War war, Fight f)
    {
        f.Ended = war.Time;
        // Who holds the field: the sides still in it.
        var held = new bool[3];
        foreach (var s in f.S)
            if (s.Mode != SquadMode.Withdraw && Fit(war, f, s) > 0) held[s.Side] = true;
        var standing = Standing(war, f);
        foreach (int mid in f.Movers)
        {
            var m = war.Units[mid];
            if (m.InFight != f.Id) continue;
            var mine = f.S.Where(s => s.Mover == mid).ToList();
            Settle(war, f, m, mine, held[m.Side]);
            m.InFight = -1;
            m.Broke = war.Time;
            // A company that was pulling back when the fight ended keeps going back. (It used to march on with its old
            // move, often straight back at the enemy.)
            if (mine.Any(s => s.Mode == SquadMode.Withdraw)) FallBack(war, f, m);
            else Resume(war, m, standing);
        }
        // A brush where nobody was hurt isn't news: it stays out of the events (a third of fights are like that).
        if (f.Killed.Sum() + f.Down.Sum() + f.Hurt.Sum() + f.Lost.Sum() == 0) return;
        int minutes = (int)((war.Time - f.Started) / 60);
        var sides = Enumerable.Range(0, 3).Where(s => f.Movers.Any(id => war.Units[id].Side == s)).ToList();
        string losses = string.Join("; ", sides.Select(s => $"{war.Sides[s].Name} {f.Killed[s]} killed, {f.Down[s] + f.Hurt[s]} wounded{(f.Lost[s] > 0 ? $", {f.Lost[s]} vehicles" : "")}"));
        string holder = sides.Count(s => held[s]) == 1 ? $"; {war.Sides[sides.First(s => held[s])].Name} holds" : "";
        war.Events.Add((war.Time, sides.FirstOrDefault(s => held[s], sides.FirstOrDefault()), $"Fight near {f.Where} over after {minutes} min: {losses}{holder}", f.X, f.Z));
    }
}
