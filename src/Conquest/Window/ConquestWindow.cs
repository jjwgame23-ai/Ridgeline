using Godot;

namespace Ridgeline;

/// <summary>Somewhere for a squad to be: a patch of ground round a point, the near side toward a threat when there is one.</summary>
public sealed class AreaObjective : IObjective
{
    public Vector3 Center { get; set; }
    public float Radius { get; set; } = 30f;
    /// <summary>Where the enemy is, if known: half the squad's places are on the side facing it.</summary>
    public Vector3? Threat;
    public Valley Map = null!;
    /// <summary>A dug-in squad's places: behind its fighting positions, one each.</summary>
    public readonly List<Vector3> Spots = new();

    public Vector3 PointFor(Bot b, RandomNumberGenerator rng)
    {
        if (Spots.Count > 0 && b.Squad != null)
        {
            // His own place: the one he was put in when he came in (see ConquestWindow.Bring), not one along from it.
            var spot = Spots.OrderBy(s => (s - b.FeetPos with { Y = s.Y }).LengthSquared()).First();
            if ((spot - b.FeetPos with { Y = spot.Y }).Length() > 3f) spot = Spots[Math.Max(0, b.Squad.Members.IndexOf(b)) % Spots.Count];
            if (Threat is { } th && (th - spot) with { Y = 0f } is { } look && look.LengthSquared() > 1f) b.LookOut = look.Normalized();
            return spot;
        }
        float a = rng.Randf() * Mathf.Tau, r = MathF.Sqrt(rng.Randf()) * Radius * 0.8f;
        var off = new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
        if (Threat is { } t && rng.Randf() < 0.5f)
        {
            var dir = (t - Center) with { Y = 0f };
            if (dir.LengthSquared() > 1f && off.Dot(dir) < 0f) off = -off;
        }
        return Map.Ground(Center + off);
    }
}

/// <summary>
/// The armies in a Conquest window, frozen at a moment of the war (slice 2 of the playable window). The war, run
/// headless to that moment, is stopped; every unit near the middle of the window comes in where it is, as squads of
/// bots, with its vehicles and its orders.
/// - Who comes in. The battle maps run at about 100-150 bots. So not everyone in a 4 km window can be embodied: the
///   squads, crews and sections nearest the window's middle are, whole, until the cap is reached (160 by default),
///   shared between the sides as their strengths within a kilometre of the middle are, so the bubble keeps the war's
///   odds. (Nearest first regardless of side, a fight of BRAVO against CHARLIE came in at 131 to 37, and CHARLIE's
///   were all down within a minute.) The rest of the window's units aren't on the ground yet (slice 3 keeps them
///   abstract round the bubble).
/// - Where. A soldier in a fight comes in where the war's fight has him: his squad's place and his own within it. Units
///   not fighting come in at their squad's place in their company.
/// - Dug in. A squad the war has dug in (halted two hours or more, as its fights count trenches) comes in with its
///   fighting positions: a sandbagged position for every two men, 4 m apart across its front, facing the enemy, each
///   man behind his. (Squads dug in for days came in kneeling in the open in front of the enemy's machine guns: in a
///   fight on day 3, CHARLIE lost 83 of 86 in five minutes, 62 of them to BRAVO's 12.7 mm guns at 240 m.)
/// - Who they are. Each war soldier fit to fight (or lightly wounded) becomes a bot with their name and rank, their
///   skill from the war, and a role from their job: squad leaders lead, machine gunners carry the light machine gun,
///   grenadiers the launcher, anti-tank gunners the light anti-tank weapon, marksmen the marksman's rifle, medics their
///   bag; crews and the rest are riflemen.
/// - Vehicles. Each vehicle the war unit still has comes in beside it, crewed from its drivers, gunners and crewmen:
///   IFVs, APCs, tanks, light vehicles, trucks, mortars, air defence, helicopters. Guns and rocket launchers have no
///   embodied model yet and stay out.
/// - Orders. A unit holding ground, or halted, defends where it is (a town district, when it's on one). A unit on the
///   move goes where it was going, clipped to the window. Nothing here is told to the war yet (slice 3).
/// </summary>
public partial class ConquestWindow : Node, IMatch, IMotorHost, ITelemetryMatch
{
    public War War = null!;
    public Valley Map = null!;
    public Hud PlayerHud = null!;
    /// <summary>The window's middle on the island (metres east and south of its centre).</summary>
    public float CX, CZ;
    public int Cap = 160;
    /// <summary>
    /// The embodied assault test (assault=1): AttackPlatoons of ALPHA's rifle platoons attack DefendPlatoons of BRAVO's,
    /// dug in round Target's near edge, with their vehicles, for at most AssaultMinutes; then it reports and quits.
    /// </summary>
    public bool Assault;
    public int AttackPlatoons = 2, DefendPlatoons = 1;
    public Objective? Target;
    public double AssaultMinutes = 45;
    /// <summary>
    /// calib=1: the war runs on beside the fight, abstractly, minute by minute from the same moment, so the same soldiers'
    /// fates can be set side by side: how many of the bubble's soldiers the abstract fight has killed, downed or wounded
    /// by each minute, against the embodied one. (The war's state is otherwise frozen in the window.)
    /// </summary>
    public bool Calibrate;
    public bool PlayerJoins = true;
    public static bool Verbose;

    public List<Bot> Bots { get; } = new();
    public Spectator Spec { get; private set; } = null!;
    public readonly List<Squad>[] Squads = { new(), new(), new() };
    public readonly bool[] Out = { true, true, true }; // what's lost here stays lost: the war decides replacements
    public MotorPool Motor = null!;
    public Player? PlayerBody { get; private set; }
    /// <summary>Each embodied squad's war unit, and each bot's war soldier.</summary>
    public readonly Dictionary<Squad, Unit> UnitOf = new();
    public readonly Dictionary<ICombatant, int> SoldierOf = new();

    Valley IMotorHost.Map => Map;
    List<Squad>[] IMotorHost.Squads => Squads;
    bool[] IMotorHost.Out => Out;
    Valley ITelemetryMatch.Map => Map;
    SiteObjective[] ITelemetryMatch.Points => _points;
    int[] ITelemetryMatch.Owner => _owner;
    List<Squad>[] ITelemetryMatch.Squads => Squads;
    Squad? ITelemetryMatch.PlayerSquad => PlayerBody != null ? Squads.SelectMany(l => l).FirstOrDefault(s => s.Members.Contains(PlayerBody)) : null;
    int[] ITelemetryMatch.Tickets => _tickets;
    bool[] ITelemetryMatch.Out => Out;
    float[] ITelemetryMatch.HQHold => _hold;
    SiteObjective[] _points = Array.Empty<SiteObjective>();
    int[] _owner = Array.Empty<int>();
    readonly int[] _tickets = new int[3];
    readonly float[] _hold = { 1f, 1f, 1f };
    public bool Spent(int team) => true;
    public void BuildFob(int team, Vector3 at) { }

    readonly RandomNumberGenerator _rng = new();
    readonly Dictionary<int, Vector2> _squadAt = new(), _fighterAt = new(), _enemyAt = new();
    readonly HashSet<int> _attacking = new(), _dug = new();
    readonly int[] _numbers = new int[3];
    readonly int[] _killed = new int[3];
    bool _embodied;
    double _nextLog, _startedAt, _warMinute;
    HashSet<int> _bubble = new();
    readonly int[] _down = new int[3], _hurt = new int[3];

    public override void _Ready()
    {
        _rng.Seed = (ulong)(War.Seed * 7919 + (int)War.Time);
        BotBrain.ResetStatics();
        BotBrain.DefaultObjective = null;
        Squad.ResetAll();
        Drone.Clear();
        SmokeScreen.Clear();
        PerchClaims.Clear();
        MotorPool.ResetCounters();
        Fortifications.Clear();
        Radio.Reset();
        global::Ridgeline.Intel.Reset();
        Motor = new MotorPool(this);
        Spec = new Spectator { Mode = this };
        AddChild(Spec);
        // A town district is hostile with an enemy in it.
        Squad.Hostile = (site, team) => Combatants.All.Any(c => c.Alive && c.Team != team && (c.FeetPos - site.Center with { Y = c.FeetPos.Y }).Length() < site.Radius + 30f);
        Combatants.Killed += OnKilled;
    }

    public override void _ExitTree() => Combatants.Killed -= OnKilled;

    public override void _Process(double delta)
    {
        // Everyone comes in once the navmeshes are up: before that there's nowhere to put them or for them to go.
        if (!_embodied)
        {
            if (!Map.Nav.Finished || !Map.VehicleNav.Finished || NavigationServer3D.MapGetIterationId(Map.GetWorld3D().NavigationMap) == 0) return;
            _embodied = true;
            if (Assault) EmbodyAssault();
            else Embody();
            return;
        }
        Motor.Tick();
        Telemetry.Tick(this);
        if (Calibrate) Abstract();
        if (Assault) Judge();
        if (Verbose && Clock.Now >= _nextLog)
        {
            _nextLog = Clock.Now + 30.0;
            Log($"[{Clock.Now:0}s] alive " + string.Join(" / ", Enumerable.Range(0, 3).Select(t => $"{War.Sides[t].Name} {Bots.Count(b => b.Alive && b.Team == t)}"))
                + $"; killed {_killed[0]}/{_killed[1]}/{_killed[2]}; vehicles " + string.Join(" / ", Enumerable.Range(0, 3).Select(t => Motor.Slots.Count(s => s.Team == t && s.Live is { Destroyed: false })))
                + "; states " + string.Join(" ", Bots.Where(b => b.Alive).GroupBy(b => b.Brain.State).Select(g => $"{g.Key}:{g.Count()}")));
        }
    }

    // ---------------------------------------------------------------- who comes in

    Vector2 Local(float x, float z) => new(x - CX, z - CZ);

    void Embody()
    {
        float edge = Map.Half - 150f;
        // Every war unit with soldiers in it (squads, crews, headquarters sections), where it is in the window.
        // The fights going on: where each squad and soldier in them is, who's attacking, and where their enemy is.
        foreach (var f in War.Fights)
        {
            if (f.Over) continue;
            foreach (var s in f.S)
            {
                _squadAt[s.Unit] = Local(s.X, s.Z);
                if (s.Dug) _dug.Add(s.Unit);
            }
            foreach (var fi in f.F)
                if (!fi.Gone) _fighterAt[fi.Soldier] = Local(f.S[fi.Squad].X + fi.OffX, f.S[fi.Squad].Z + fi.OffZ);
            foreach (int mid in f.Movers)
            {
                var enemy = f.S.Where(s => s.Side != War.Units[mid].Side && s.Alive).ToList();
                if (enemy.Count > 0) _enemyAt[mid] = Local(enemy.Average(s => s.X), enemy.Average(s => s.Z));
                if (f.Attacking.Contains(mid)) _attacking.Add(mid);
            }
        }
        var cands = new List<(Unit U, Unit M, Vector2 At, List<int> Fit)>();
        foreach (var u in War.Units)
        {
            if (u.Members.Count == 0 || u.Mover < 0) continue;
            var m = War.Units[u.Mover];
            if (m.People <= 0) continue;
            var at = _squadAt.TryGetValue(u.Id, out var fat) ? fat : Local(m.X + u.OffX, m.Z + u.OffZ);
            if (MathF.Abs(at.X) > edge || MathF.Abs(at.Y) > edge) continue;
            var fit = u.Members.Where(s => War.Soldiers[s].State is SoldierState.Fit or SoldierState.Wounded).ToList();
            if (fit.Count == 0) continue;
            cands.Add((u, m, at, fit));
        }
        // Each side's share of the cap: its share of the soldiers within a kilometre of the middle.
        var near = new int[3];
        foreach (var c in cands)
            if (c.At.LengthSquared() < 1000f * 1000f) near[c.U.Side] += c.Fit.Count;
        int all = Math.Max(1, near.Sum());
        var quota = Enumerable.Range(0, 3).Select(t => (int)MathF.Round(Cap * (float)near[t] / all)).ToArray();
        int n = 0, skipped = 0;
        var took = new int[3];
        var movers = new HashSet<int>();
        foreach (var c in cands.OrderBy(c => c.At.LengthSquared()))
        {
            int t = c.U.Side;
            if (took[t] >= quota[t]) { skipped += c.Fit.Count; continue; }
            took[t] += c.Fit.Count;
            n += c.Fit.Count;
            movers.Add(c.M.Id);
            Bring(c.U, c.M, c.At, c.Fit);
        }
        Log($"--- CONQUEST WINDOW: {War.Isl.Name} day {War.Day} {War.Hour:00.0}h at ({CX / 1000f:0.0}, {CZ / 1000f:0.0}) km ---");
        Log($"{Dug} fighting positions for dug-in squads");
        foreach (var f in War.Fights.Where(f => !f.Over && f.Movers.Any(movers.Contains)))
            Log($"abstract fight {f.Id}: {(War.Time - f.Started) / 60:0} min old, last shot {War.Time - f.LastShot:0} s ago, last hit {War.Time - f.LastHit:0} s ago; "
                + $"{f.Shots.Sum()} shots, {f.Killed.Sum()} killed, {f.Down.Sum()} down, {f.Hurt.Sum()} hurt so far; {f.Attacking.Count} of {f.Movers.Count} companies attacking; "
                + $"{f.F.Count(x => !x.Gone)} fighters in it");
        Log($"embodied {n} soldiers in {Squads.Sum(s => s.Count)} squads from {movers.Count} companies ("
            + string.Join(", ", Enumerable.Range(0, 3).Select(t => $"{War.Sides[t].Name} {Bots.Count(b => b.Team == t)}")) + $"), {Motor.Slots.Count} vehicles; "
            + $"{skipped} more soldiers in the window left abstract, in {cands.Count} units in all");
        foreach (var mid in movers)
        {
            var m = War.Units[mid];
            Log($"  {War.Sides[m.Side].Name} {m.Name}: {m.People} fit, {Doing(m)}, at {Local(m.X, m.Z).Length():0} m from the middle");
        }
        if (PlayerJoins) JoinPlayer();
        else Spec.Activate(Bots.FirstOrDefault());
        _points = Map.Sites.Select(s => new SiteObjective { Site = s, Map = Map, R = s.Radius }).ToArray();
        _owner = Enumerable.Repeat(-1, _points.Length).ToArray();
        if (Telemetry.PathFromArgs() is { } tp) Telemetry.Start(tp, this);
        _bubble = SoldierOf.Values.ToHashSet();
        _startedAt = Clock.Now;
    }

    // ---------------------------------------------------------------- the embodied assault test

    readonly List<ICombatant>[] _sides = { new(), new(), new() };
    double _judgeAt, _firstShot = -1;
    Vector3 _townAt;
    float _lineR;
    string _setup = "";

    /// <summary>
    /// The assault test, embodied: the same attack the abstract assault test fights (AssaultTest), at platoon scale so it
    /// fits the bots the battle maps run. BRAVO's platoons dig in round the near (west) edge of the town, squads 50 m
    /// apart across the line, their vehicles 60 m behind. ALPHA's platoons start 800 m out, 250 m apart, and attack:
    /// each squad the stretch of the line opposite it, while its platoon's vehicles support by fire from 450 m.
    /// Three to one is what FM 3-90 plans an attack on a prepared position at.
    /// </summary>
    void EmbodyAssault()
    {
        var obj = Target!;
        var t2 = Local(obj.X, obj.Z);
        _townAt = Map.Ground(new Vector3(t2.X, 0f, t2.Y));
        _lineR = obj.Radius + 40f;
        // Rifle platoons (1st, 2nd, 3rd...), not a battalion's anti-tank or mortar platoon.
        bool Rifles(Unit u) => u.Echelon == Echelon.Platoon && u.Arm is Arm.Mechanised or Arm.Infantry or Arm.Motorised && u.Children.Count > 0
                               && char.IsDigit(u.Name[0]);
        var att = War.Units.Where(u => u.Side == 0 && Rifles(u)).Take(AttackPlatoons).ToList();
        var def = War.Units.Where(u => u.Side == 1 && Rifles(u)).Take(DefendPlatoons).ToList();
        List<int> Fit(Unit u) => u.Members.Where(s => War.Soldiers[s].State is SoldierState.Fit or SoldierState.Wounded).ToList();
        var west = new Vector3(-1f, 0f, 0f);
        var lineMid = _townAt + west * _lineR;
        var attackFrom = lineMid + west * 800f;
        // The defence: every defending squad on the line, 50 m apart, north to south; the platoons' vehicles behind.
        var defSquads = def.SelectMany(p => p.Children.Select(c => War.Units[c]).Where(c => c.Members.Count > 0)).ToList();
        var linePts = new List<Vector3>();
        for (int i = 0; i < defSquads.Count; i++)
        {
            float off = (i - (defSquads.Count - 1) / 2f) * 50f;
            linePts.Add(Map.Ground(lineMid + new Vector3(0f, 0f, off)));
        }
        for (int i = 0; i < defSquads.Count; i++)
        {
            var sq = defSquads[i];
            var at = new Vector2(linePts[i].X, linePts[i].Z);
            var hold = new AreaObjective { Center = linePts[i], Radius = 20f, Threat = attackFrom with { Z = linePts[i].Z }, Map = Map };
            Bring(sq, War.Units[sq.Mover], at, Fit(sq), (hold, null, true, "Defend", obj.Name), digIn: true);
        }
        foreach (var p in def)
        {
            var back = lineMid - west * 60f;
            var at = new Vector2(back.X, back.Z);
            var hold = new AreaObjective { Center = Map.Ground(back), Radius = 25f, Threat = attackFrom, Map = Map };
            Bring(p, War.Units[p.Mover], at, Fit(p), (hold, null, true, "Defend", obj.Name), digIn: false);
        }
        // The defended line as a place to take, so the attacking squads run the deliberate attack on it (ORP, support by
        // fire, assault: SquadMovement), as they would on any enemy-held point. (Sent at a point on the ground, they just
        // walked at it.)
        var line = new Site
        {
            Name = $"the {obj.Name} line", Kind = "Position", Center = lineMid,
            Radius = MathF.Max(40f, (linePts.Count - 1) * 25f + 30f),
        };
        foreach (var sq in Squads[1]) if (sq.Objective is AreaObjective ao) line.Points.AddRange(ao.Spots);
        if (line.Points.Count == 0) line.Points.AddRange(linePts);
        // The attack: each platoon's squads on the start line opposite their stretch of the defence, 60 m apart.
        for (int pi = 0; pi < att.Count; pi++)
        {
            var p = att[pi];
            float plat = (pi - (att.Count - 1) / 2f) * 250f;
            var squads = p.Children.Select(c => War.Units[c]).Where(c => c.Members.Count > 0).ToList();
            for (int si = 0; si < squads.Count; si++)
            {
                var start = Map.Ground(attackFrom + new Vector3(0f, 0f, plat + (si - (squads.Count - 1) / 2f) * 60f));
                var go = new SiteObjective { Site = line, Map = Map, R = line.Radius };
                Bring(squads[si], War.Units[squads[si].Mover], new Vector2(start.X, start.Z), Fit(squads[si]), (go, line, false, "Attack", line.Name), digIn: false);
            }
            var support = Map.Ground(lineMid + west * 450f + new Vector3(0f, 0f, plat));
            var sbf = new AreaObjective { Center = support, Radius = 30f, Threat = lineMid, Map = Map };
            var hq = Map.Ground(attackFrom + new Vector3(0f, 0f, plat) + west * 40f);
            Bring(p, War.Units[p.Mover], new Vector2(hq.X, hq.Z), Fit(p), (new SiteObjective { Site = line, Map = Map, R = line.Radius }, line, false, "Attack", line.Name),
                  digIn: false, crewOrder: (sbf, null, true, "Support", obj.Name), parkAt: new Vector2(hq.X, hq.Z));
        }
        foreach (var b in Bots) _sides[b.Team].Add(b);
        int Vehicles(int t) => Motor.Slots.Count(s => s.Team == t);
        _setup = $"{att.Count} ALPHA platoons ({_sides[0].Count} soldiers, {Vehicles(0)} vehicles) against {def.Count} BRAVO platoon{(def.Count > 1 ? "s" : "")} "
                 + $"dug in ({_sides[1].Count} soldiers, {Vehicles(1)} vehicles, {Dug} fighting positions)";
        Log($"--- EMBODIED ASSAULT on {obj.Name}: {_setup}; start line 800 m out; the fighting positions see {100.0 * FieldSeen / Math.Max(1, FieldProbes):0}% of the ground toward the attack at 100-450 m ---");
        foreach (var p in att.Concat(def)) Log($"  {War.Sides[p.Side].Name} {p.Name}");
        if (PlayerJoins) JoinPlayer();
        else Spec.Activate(Bots.FirstOrDefault());
        _points = Map.Sites.Select(s => new SiteObjective { Site = s, Map = Map, R = s.Radius }).ToArray();
        _owner = Enumerable.Repeat(-1, _points.Length).ToArray();
        if (Telemetry.PathFromArgs() is { } tp) Telemetry.Start(tp, this);
        _startedAt = Clock.Now;
        Combatants.Killed += (_, _) => { if (_firstShot < 0) _firstShot = Clock.Now; };
    }

    /// <summary>
    /// Every 10 s: is it over? Carried, when no defender is left on his feet on the line and an attacker is on it; held, when
    /// the attackers are down to half (the species fights on to half its men); or out of time. Then the report, and quit.
    /// </summary>
    void Judge()
    {
        if (Clock.Now < _judgeAt) return;
        _judgeAt = Clock.Now + 10.0;
        bool OnLine(ICombatant c) => (c.FeetPos - _townAt with { Y = c.FeetPos.Y }).Length() < _lineR + 60f;
        int Up(int t) => _sides[t].Count(c => c.Alive);
        int attUp = Up(0), defUp = Up(1);
        bool carried = defUp == 0 || !_sides[1].Any(c => c.Alive && OnLine(c)) && _sides[0].Any(c => c.Alive && OnLine(c));
        bool held = attUp * 2 <= _sides[0].Count;
        double minutes = (Clock.Now - _startedAt) / 60.0;
        if (!carried && !held && minutes < AssaultMinutes) return;
        int Killed(int t) => _sides[t].Count(c => c.Dead);
        int Lost(int t) => _sides[t].Count(c => !c.Alive);
        string Pc(int a, int b) => $"{100.0 * a / Math.Max(1, b):0}%";
        GD.Print($"EMBODIED ASSAULT: {_setup}");
        GD.Print($"  {(carried ? "carried" : held ? "held: the attack fought down to half" : "still going")} after {minutes:0} min (first casualty {(_firstShot < 0 ? "none" : $"{(_firstShot - _startedAt) / 60:0} min in")})");
        GD.Print($"  attackers lost {Pc(Lost(0), _sides[0].Count)} ({Pc(Killed(0), _sides[0].Count)} killed); defenders lost {Pc(Lost(1), _sides[1].Count)} ({Pc(Killed(1), _sides[1].Count)} killed); "
                 + $"vehicles lost {Motor.Slots.Count(s => s.Team == 0 && s.Live is not { Destroyed: false })}/{Motor.Slots.Count(s => s.Team == 1 && s.Live is not { Destroyed: false })}");
        GD.Print("  (the abstract assault test, 9 companies on 3 dug in with mortars: attackers lost 25%, defenders 57%, carried 7 times in 12 in a median 2¾ h;");
        GD.Print("   marks: a battalion attack at three to one on a prepared company position cost attackers about 5-15% and defenders more, over hours)");
        GetTree().Quit();
    }

    static string Doing(Unit m) => m.InFight >= 0 ? "fighting" : m.Path != null ? "moving" : "halted";

    /// <summary>
    /// One war unit's soldiers and vehicles onto the ground, as a squad and its vehicles' crews. Its order, its vehicles'
    /// order and where they park, and whether it's dug in, come from what the war has it doing, unless given (the
    /// assault test sets them).
    /// </summary>
    void Bring(Unit u, Unit m, Vector2 at, List<int> fit, (IObjective Obj, Site? Site, bool Defend, string Verb, string What)? given = null,
               bool? digIn = null, (IObjective Obj, Site? Site, bool Defend, string Verb, string What)? crewOrder = null, Vector2? parkAt = null)
    {
        int side = u.Side;
        var crewJobs = fit.Where(s => War.Soldiers[s].Job is Job.Driver or Job.Gunner or Job.Crewman or Job.Commander).ToList();
        var rest = fit.Except(crewJobs).ToList();
        // Vehicles with an embodied model, each with its crew from the drivers, gunners and crewmen.
        var vehicles = u.Vehicles.Where(v => !War.Vehicles[v].Lost).Select(v => (V: v, K: Kind(War.Vehicles[v].Class))).Where(x => x.K != null).ToList();
        var order = given ?? OrderFor(u, m, at, side);
        var vAt = parkAt ?? at;
        int k = 0;
        foreach (var (v, kind) in vehicles)
        {
            int need = MotorPool.Crew(kind!.Value);
            var crew = crewJobs.Take(need).ToList();
            crewJobs.RemoveRange(0, crew.Count);
            if (crew.Count < need) { crew.AddRange(rest.Take(need - crew.Count)); rest = rest.Skip(need - crew.Count).ToList(); }
            if (crew.Count == 0) continue;
            var sq = NewSquad(side, MotorPool.CrewKind(kind.Value), u);
            Give(sq, crewOrder ?? order);
            float a = k++ * 1.3f;
            var park = Map.Ground(new Vector3(vAt.X + MathF.Cos(a) * 14f, 0f, vAt.Y + MathF.Sin(a) * 14f));
            foreach (int s in crew) Spawn(s, sq, Role.Crewman, park + new Vector3(_rng.RandfRange(-3f, 3f), 0f, _rng.RandfRange(-3f, 3f)));
            Motor.Add(kind.Value, side, sq, park, _rng.RandfRange(0f, Mathf.Tau));
        }
        rest.AddRange(crewJobs); // crewmen left over (more crew than vehicles: the rest of a tank's four) fight on foot
        if (rest.Count == 0) return;
        var squad = NewSquad(side, KindOf(u), u);
        var c = new Vector3(at.X, 0f, at.Y);
        bool dug = digIn ?? (_dug.Contains(u.Id) || m.Path == null && m.InFight < 0 && War.Time - m.HaltedAt >= 2 * 3600);
        var spots = dug && order.Obj is AreaObjective { Threat: { } enemy } area ? DigIn(c, enemy, rest.Count) : null;
        if (spots != null) ((AreaObjective)order.Obj).Spots.AddRange(spots);
        Give(squad, order);
        int i = 0;
        foreach (int s in rest.OrderBy(s => War.Soldiers[s].Job == Job.SquadLeader ? 0 : 1))
        {
            var at3 = spots != null ? spots[i % spots.Count] : _fighterAt.TryGetValue(s, out var fp) ? new Vector3(fp.X, 0f, fp.Y)
                : c + new Vector3(_rng.RandfRange(-6f, 6f), 0f, _rng.RandfRange(-6f, 6f));
            Spawn(s, squad, RoleOf(War.Soldiers[s].Job), at3);
            i++;
        }
    }

    Squad NewSquad(int side, SquadKind kind, Unit u)
    {
        var sq = new Squad { Team = side, Number = ++_numbers[side], Kind = kind };
        Squads[side].Add(sq);
        UnitOf[sq] = u;
        return sq;
    }

    void Spawn(int soldier, Squad sq, Role role, Vector3 near)
    {
        ref var so = ref War.Soldiers[soldier];
        string full = People.Name(so.Side, War.Seed, soldier);
        string call = $"{People.Title(so.Side, so.Rank)} {full[(full.IndexOf(' ') + 1)..]}";
        var p = Personality.Roll(_rng, call, People.Skill(War.Seed, soldier));
        var b = new Bot { TeamId = so.Side, P = p, Role = role, Def = Roles.Primary(role), Squad = sq };
        GetParent().AddChild(b);
        b.GlobalPosition = Map.Ground(near) + Vector3.Up * 0.3f;
        if (sq.Objective is { } o) b.Aim.Yaw = Mathf.RadToDeg(MathF.Atan2(-(o.Center.X - near.X), -(o.Center.Z - near.Z)));
        sq.Join(b);
        Bots.Add(b);
        SoldierOf[b] = soldier;
    }

    /// <summary>What a squad is to do, from what its war unit was doing.</summary>
    (IObjective Obj, Site? Site, bool Defend, string Verb, string What) OrderFor(Unit u, Unit m, Vector2 at, int side)
    {
        float edge = Map.Half - 150f;
        var threat = _enemyAt.TryGetValue(m.Id, out var ea) ? new Vector3(ea.X, 0f, ea.Y) : Threat(m);
        // In a fight: the attackers go for the enemy in it, the rest hold where they are, facing it.
        if (m.InFight >= 0 && _attacking.Contains(m.Id) && threat is { } enemy)
            return (new AreaObjective { Center = Map.Ground(enemy), Radius = 40f, Threat = enemy, Map = Map }, null, false, "Attack", "");
        if (m.Path != null && m.InFight < 0)
        {
            var go = Local(m.GoX + u.OffX, m.GoZ + u.OffZ);
            go = new Vector2(Math.Clamp(go.X, -edge, edge), Math.Clamp(go.Y, -edge, edge));
            bool attack = m.Order is { Kind: OrderKind.Occupy } || War.Units[Math.Max(0, m.Parent)].Order is { Kind: OrderKind.Occupy };
            return (new AreaObjective { Center = Map.Ground(new Vector3(go.X, 0f, go.Y)), Radius = 30f, Threat = threat, Map = Map }, null, false, attack ? "Attack" : "Move", "");
        }
        // Holding: a town district it stands on, or the ground where it is.
        var here = new Vector3(at.X, 0f, at.Y);
        var site = Map.Sites.Where(s => (s.Center - here with { Y = s.Center.Y }).Length() < s.Radius + 60f).OrderBy(s => (s.Center - here with { Y = s.Center.Y }).Length()).FirstOrDefault();
        if (site != null) return (new SiteObjective { Site = site, Map = Map, R = site.Radius + 20f }, site, true, "Defend", site.Name);
        return (new AreaObjective { Center = Map.Ground(here), Radius = 30f, Threat = threat, Map = Map }, null, true, "Defend", "");
    }

    static void Give(Squad sq, (IObjective Obj, Site? Site, bool Defend, string Verb, string What) o) => sq.Order(o.Site, o.Obj, o.Defend, o.Verb, o.What);

    /// <summary>
    /// A dug-in squad's fighting positions: a two-man position for every two men, 4 m apart across its front, facing the
    /// enemy, with a parapet a metre thick (Fortifications.Parapet); and a place behind each for each of the two. They
    /// stand for the trenches the war has them in.
    /// </summary>
    List<Vector3> DigIn(Vector3 c, Vector3 enemy, int men)
    {
        var facing = (enemy - c) with { Y = 0f };
        if (facing.LengthSquared() < 1f) return new List<Vector3>();
        facing = facing.Normalized();
        var right = facing.Cross(Vector3.Up);
        int walls = (men + 1) / 2;
        // Sited for its field of fire: of the places within 30 m to either side and 20 m back (or 10 forward) of where the
        // squad was, the one whose positions see the most of the ground toward the enemy. (Put down where the squad
        // happened to be, a line at a town's edge saw 110-170 m of the approach, and the attack came up unseen.)
        var space = Map.GetWorld3D().DirectSpaceState;
        int Field(Vector3 centre)
        {
            int seen = 0;
            for (int w = 0; w < walls; w++)
            {
                var at = Map.Ground(centre + right * ((w - (walls - 1) / 2f) * 4f));
                var eye = at + Vector3.Up * 1.4f;
                foreach (float ang in FieldAngles)
                foreach (float r in FieldRanges)
                {
                    var dir = facing.Rotated(Vector3.Up, Mathf.DegToRad(ang));
                    var p = at + dir * r;
                    p.Y = Map.HeightAt(p.X, p.Z) + 0.9f; // a man on a knee out there
                    if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, p, Layers.World | Layers.Trees)).Count == 0) seen++;
                }
            }
            return seen;
        }
        var best = c;
        int bestSeen = Field(c);
        for (float lat = -30f; lat <= 30f; lat += 10f)
        for (float dep = -20f; dep <= 10f; dep += 10f)
        {
            if (lat == 0f && dep == 0f) continue;
            var cand = Map.Ground(c + right * lat + facing * dep);
            int seen = Field(cand);
            if (seen > bestSeen) { bestSeen = seen; best = cand; }
        }
        c = best;
        FieldSeen += bestSeen;
        FieldProbes += walls * FieldAngles.Length * FieldRanges.Length;
        var spots = new List<Vector3>();
        for (int w = 0; w < walls; w++)
        {
            var at = c + right * ((w - (walls - 1) / 2f) * 4f);
            Fortifications.Parapet(GetParent(), Map, at, facing);
            foreach (float s in new[] { -0.6f, 0.6f })
                spots.Add(Map.Ground(at - facing * 1.05f + right * s));
            Dug++;
        }
        return spots;
    }

    /// <summary>Fighting positions built for dug-in squads, and of the ground looked at from them toward the enemy, how much they see.</summary>
    public int Dug, FieldSeen, FieldProbes;
    static readonly float[] FieldAngles = { -30f, -15f, 0f, 15f, 30f }, FieldRanges = { 100f, 200f, 300f, 450f };

    /// <summary>The nearest enemy unit the war's side knows of within 3 km, in window coordinates.</summary>
    Vector3? Threat(Unit m)
    {
        Unit? best = null;
        float bd = 3000f * 3000f;
        foreach (int id in War.MoverIds)
        {
            var e = War.Units[id];
            if (e.Side == m.Side || e.People <= 0) continue;
            float d = (e.X - m.X) * (e.X - m.X) + (e.Z - m.Z) * (e.Z - m.Z);
            if (d >= bd) continue;
            bd = d;
            best = e;
        }
        if (best == null) return null;
        var l = Local(best.X, best.Z);
        return new Vector3(l.X, 0f, l.Y);
    }

    // ---------------------------------------------------------------- the war's terms in the battle's

    static VKind? Kind(VClass c) => c switch
    {
        VClass.Ltv => VKind.LTV,
        VClass.Truck or VClass.Tanker or VClass.Ambulance => VKind.Transport,
        VClass.Apc => VKind.APC,
        VClass.Ifv => VKind.IFV,
        VClass.Tank => VKind.MBT,
        VClass.LightTank => VKind.MGS,
        VClass.Mortar => VKind.Mortar,
        VClass.Spaa => VKind.SPAA,
        VClass.Helicopter => VKind.UH,
        VClass.Gunship => VKind.AH,
        _ => null, // guns, rocket launchers and engineer vehicles have no embodied model yet
    };

    static Role RoleOf(Job j) => j switch
    {
        Job.SquadLeader or Job.Commander => Role.Leader,
        Job.MachineGunner => Role.AutoRifleman,
        Job.Grenadier => Role.Grenadier,
        Job.AntiTank => Role.AntiTank,
        Job.Marksman => Role.Marksman,
        Job.Medic => Role.Medic,
        Job.Engineer => Role.Engineer,
        Job.Supply => Role.Ammo,
        _ => Role.Rifleman,
    };

    static SquadKind KindOf(Unit u) => u.Arm switch
    {
        Arm.Recon => SquadKind.Recon,
        Arm.Engineers => SquadKind.Engineer,
        Arm.Logistics or Arm.Maintenance or Arm.Medical => SquadKind.Logistics,
        _ => SquadKind.Rifle,
    };

    // ---------------------------------------------------------------- the player

    /// <summary>The player takes a rifleman's place in the embodied rifle squad nearest the middle (ALPHA's if it has one there).</summary>
    void JoinPlayer()
    {
        var sq = Squads[0].Concat(Squads[1]).Concat(Squads[2]).Where(s => s.Kind == SquadKind.Rifle && s.Members.Count > 1)
            .OrderBy(s => s.Team == 0 ? 0 : 1).ThenBy(s => s.Position?.LengthSquared() ?? float.MaxValue).FirstOrDefault();
        if (sq == null)
        {
            Spec.Activate(Bots.FirstOrDefault());
            return;
        }
        var stand = sq.Members.OfType<Bot>().LastOrDefault(b => b.Role == Role.Rifleman) ?? sq.Members.OfType<Bot>().Last();
        var p = new Player { TeamId = sq.Team, Kit = Role.Rifleman };
        GetParent().AddChild(p);
        p.GlobalPosition = stand.GlobalPosition;
        sq.Members.Remove(stand);
        Bots.Remove(stand);
        if (SoldierOf.Remove(stand, out int soldier)) SoldierOf[p] = soldier;
        stand.QueueFree();
        sq.Join(p);
        PlayerBody = p;
        PlayerHud.P = p;
        Log($"the player is {War.Who(soldier)}, {sq.Name} ({UnitOf[sq].Name})");
    }

    /// <summary>The war, a minute at a time in step with the fight, and every minute the two counts for the bubble's soldiers.</summary>
    void Abstract()
    {
        double minutes = (Clock.Now - _startedAt) / 60.0;
        if (minutes < _warMinute + 1.0) return;
        _warMinute += 1.0;
        War.Tick(60);
        var dead = new int[3];
        var down = new int[3];
        var hurt = new int[3];
        foreach (int s in _bubble)
        {
            var so = War.Soldiers[s];
            if (so.State == SoldierState.Dead) dead[so.Side]++;
            else if (so.State is SoldierState.Down or SoldierState.Evacuated) down[so.Side]++;
            else if (so.State == SoldierState.Wounded) hurt[so.Side]++;
        }
        var embodiedDown = new int[3];
        foreach (var (c, s) in SoldierOf)
            if (!c.Dead && !c.Alive) embodiedDown[c.Team]++;
        GD.Print($"calib {_warMinute:0} min: abstract killed {string.Join("/", dead)} down {string.Join("/", down)} wounded {string.Join("/", hurt)}; "
                 + $"embodied killed {string.Join("/", _killed)} down {string.Join("/", embodiedDown)}; of {string.Join("/", Enumerable.Range(0, 3).Select(t => _bubble.Count(s => War.Soldiers[s].Side == t)))}");
    }

    void OnKilled(ICombatant victim, HitInfo hit)
    {
        _killed[victim.Team]++;
        Log($"[{Clock.Now:0}s] {hit.Shooter?.Callsign ?? "?"} ({(hit.Shooter != null ? War.Sides[hit.Shooter.Team].Name : "?")}) killed {victim.Callsign} ({War.Sides[victim.Team].Name}), {hit.Distance:0} m");
    }

    static void Log(string s)
    {
        if (Verbose) GD.Print(s);
    }
}
