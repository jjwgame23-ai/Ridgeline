using Godot;

namespace Ridgeline;

/// <summary>A settlement as something to take and hold.</summary>
public sealed class SiteObjective : IObjective
{
    public Site Site = null!;
    public Valley Map = null!;
    public float R;

    public Vector3 Center => Site.Center;
    public float Radius => R;

    public Vector3 PointFor(Bot b, RandomNumberGenerator rng)
    {
        b.LookOut = null;
        // Under attack: the side it's coming from. Otherwise, outward all round.
        var threat = b.Squad is { Defend: true, Engaged: true } sq0 ? (sq0.ContactAt - Site.Center) with { Y = 0f } : Vector3.Zero;
        var threatDir = threat.LengthSquared() > 25f ? threat.Normalized() : Vector3.Zero;
        // Holding a town district: take a window or a rooftop facing out (toward the attack), if there's one free.
        if (Site.Perches.Count > 0 && b.Squad is { Defend: true } && rng.Randf() < (threatDir != Vector3.Zero ? 0.8f : 0.6f))
            for (int k = 0; k < 10; k++)
            {
                var p = Site.Perches[rng.RandiRange(0, Site.Perches.Count - 1)];
                var outward = (p.Pos - Site.Center) with { Y = 0f };
                if (outward.LengthSquared() > 1f && outward.Normalized().Dot(p.Out) < 0.2f) continue; // faces into the point
                if (threatDir != Vector3.Zero && p.Out.Dot(threatDir) < 0.3f) continue;            // faces away from the attack
                if (!PerchClaims.Free(p.Pos, b)) continue; // someone's there, or on his way
                PerchClaims.Claim(p.Pos, b);
                b.LookOut = p.Out;
                return p.Pos;
            }
        // Mostly the buildings and cover; sometimes anywhere on the point. Defending against an attack: the near side.
        if (Site.Points.Count > 0 && rng.Randf() < 0.7f)
        {
            for (int k = 0; k < (threatDir != Vector3.Zero ? 6 : 1); k++)
            {
                var p = Site.Points[rng.RandiRange(0, Site.Points.Count - 1)];
                if (threatDir == Vector3.Zero || ((p - Site.Center) with { Y = 0f }).Dot(threatDir) > 0f) return p;
            }
            return Site.Points[rng.RandiRange(0, Site.Points.Count - 1)];
        }
        float a = rng.Randf() * Mathf.Tau, r = MathF.Sqrt(rng.Randf()) * R * 0.8f;
        return Map.Ground(Site.Center + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r));
    }
}

/// <summary>
/// Territory: three factions fight for every settlement on the map at once.
///
/// - Each settlement is a capture point. Standing on it with more people than any
///   other side first neutralises the owner's hold, then captures it. More of you
///   (relative to the next side) captures faster.
/// - Each side has tickets: its reserves. Every death costs one, and every 15 s a
///   side loses one more for each point it's behind the side holding the most. At
///   zero a side gets no more reinforcements or vehicles, falls back on its
///   headquarters, and anyone can go for it.
/// - Each side has a headquarters at its base. Once the front reaches it (an enemy
///   holds a point linked to it) the enemy can go for it, and a side whose
///   headquarters is overrun, or that has nobody left, is out. The last side left
///   wins; at the time limit (if there is one), the side holding the most ground.
/// - You respawn at your base or at any point your side owns that isn't under
///   attack and is still connected to the base through your own ground (a point
///   that's been cut off gets no reinforcements). Bots reinforce whichever is
///   closest to their squad's objective, arriving on its far side from the enemy.
/// - Each side has squads of up to six with a leader, and a commander that hands
///   out orders: attack a neutral or enemy point, or defend one of ours that's
///   under threat. The player's squad can be ordered from the map instead.
/// </summary>
public partial class TerritoryMode : Node, IMatch
{
    public const float CaptureExtra = 14f;
    const double TickEvery = 1.0, BleedEvery = 15.0, RespawnDelay = 10.0, CommandEvery = 20.0;
    /// <summary>When the match is decided on ground held, if nobody has lost by then (0: never). The main menu sets it.</summary>
    static double TimeLimit => Settings.MatchMinutes * 60.0;
    const float CapRate = 1f / 40f; // per second, per head of advantage
    /// <summary>A headquarters is worn down at half a point's rate: a one-man edge takes 160 s, four or more 40 s.</summary>
    const float HQCapRate = CapRate * 0.5f;
    const int SquadSize = 6;

    public Valley Map = null!;
    public GameSetup Setup = null!;
    public Hud PlayerHud = null!;

    public List<Bot> Bots { get; } = new();
    public Spectator Spec { get; private set; } = null!;
    public readonly int[] Tickets = new int[3];
    public int StartTickets { get; private set; }
    /// <summary>Test runs (tickets=N): start every side with this many, to get to the end game sooner.</summary>
    public static int TicketsOverride;
    public readonly bool[] Out = new bool[3];
    /// <summary>
    /// Each side's headquarters, at its base: the last thing it has. An enemy can go for it once the front reaches
    /// it (they hold a point linked to it). Worn down to nothing by more attackers than defenders on it, it's
    /// overrun, and that side is out: no more reinforcements, no more vehicles. (There was no way to lose but
    /// tickets, and at 33 a side those outlast the hour: every long match was decided by the clock, with the front
    /// frozen for its last 40 minutes.)
    /// </summary>
    public SiteObjective[] HQ = null!;
    public readonly float[] HQHold = { 1f, 1f, 1f };
    /// <summary>[headquarters, team] alive on it, as of the last tick.</summary>
    public readonly int[,] HQInside = new int[3, 3];
    readonly bool[] _hqWarned = new bool[3];
    /// <summary>Until when a side presses on to the points linked to one it has just taken (see Capture).</summary>
    readonly double[,] _exploit = new double[3, 64];
    public int Winner { get; private set; } = -1;
    public Player? PlayerBody { get; private set; }
    public double PlayerRespawnAt { get; private set; } = -1;

    public SiteObjective[] Points = null!;
    public int[] Owner = null!;       // -1 = neutral
    public float[] Progress = null!;  // owner's hold (0..1), or a neutral point's capture progress for Capper
    public int[] Capper = null!;      // who is capturing a neutral point
    public int[,] Inside = null!;     // [point, team] alive inside, as of the last tick

    /// <summary>
    /// Front line (Hell Let Loose style): the points and the three bases are linked into a
    /// network, and a side can only take a point linked to one it holds (or to its base).
    /// Each side starts holding its own sector (the points clearly nearer its base than
    /// anyone else's) and its infantry deploys forward into it, so the fighting starts in
    /// the middle within minutes and runs along the frontier.
    /// </summary>
    public bool Front;
    /// <summary>Links between nodes: 0..n-1 are the points, n+t is team t's base.</summary>
    public List<int>[] Links = null!;

    /// <summary>Can this side capture point i now: not theirs, and linked to something they hold?</summary>
    public bool CanTake(int team, int i)
    {
        if (Owner[i] == team || Out[team]) return false;
        if (!Front) return true;
        foreach (int j in Links[i])
            if (j == Points.Length + team || (j < Points.Length && Owner[j] == team)) return true;
        return false;
    }

    /// <summary>A side with no reserves left: nobody new comes; it's making its last stand at its headquarters.</summary>
    public bool Spent(int team) => Tickets[team] <= 0;

    /// <summary>Can this side go for team t's headquarters: the front has reached it (they hold a point linked to it), or t has no reserves left.</summary>
    public bool CanTakeHQ(int team, int t)
    {
        if (team == t || Out[team] || Out[t]) return false;
        if (Spent(t)) return true;
        foreach (int j in Links[Points.Length + t])
            if (j < Points.Length && Owner[j] == team) return true;
        return false;
    }

    /// <summary>A target for the commander: points 0..n-1, then the three headquarters.</summary>
    public SiteObjective Obj(int i) => i < Points.Length ? Points[i] : HQ[i - Points.Length];
    /// <summary>Who holds target i (a headquarters is its side's while the side is in the fight).</summary>
    int OwnerOf(int i) => i < Points.Length ? Owner[i] : Out[i - Points.Length] ? -1 : i - Points.Length;

    /// <summary>
    /// The points this side can reinforce: its own, joined to its headquarters by a chain of its own points along
    /// the links. A point the enemy has cut off gets nobody.
    /// </summary>
    public bool[] Supplied(int team)
    {
        int n = Points.Length;
        var ok = new bool[n];
        if (Out[team]) return ok;
        var q = new Queue<int>();
        q.Enqueue(n + team);
        while (q.Count > 0)
            foreach (int j in Links[q.Dequeue()])
                if (j < n && !ok[j] && Owner[j] == team) { ok[j] = true; q.Enqueue(j); }
        return ok;
    }

    /// <summary>Ours and takeable by someone else: where the front runs through our ground.</summary>
    public bool OnFront(int team, int i)
    {
        if (i >= Points.Length || Owner[i] != team) return false;
        for (int t = 0; t < 3; t++) if (t != team && !Out[t] && CanTake(t, i)) return true;
        return false;
    }

    /// <summary>
    /// The network: a Gabriel graph over the points and bases. Two nodes are linked when no
    /// third node sits inside the circle that has them as its diameter, which links each
    /// point to its natural neighbours without long links jumping past other points.
    /// </summary>
    void BuildLinks()
    {
        int n = Points.Length;
        var pos = new Vector2[n + 3];
        for (int i = 0; i < n; i++) pos[i] = new Vector2(Points[i].Center.X, Points[i].Center.Z);
        for (int t = 0; t < 3; t++) pos[n + t] = new Vector2(Map.Bases[t].X, Map.Bases[t].Z);
        Links = Enumerable.Range(0, n + 3).Select(_ => new List<int>()).ToArray();
        for (int a = 0; a < n + 3; a++)
        for (int b = a + 1; b < n + 3; b++)
        {
            if (a >= n && b >= n) continue; // base to base: no
            var mid = (pos[a] + pos[b]) / 2f;
            float r2 = pos[a].DistanceSquaredTo(pos[b]) / 4f;
            bool empty = true;
            for (int c = 0; c < n + 3 && empty; c++)
                if (c != a && c != b && pos[c].DistanceSquaredTo(mid) < r2) empty = false;
            if (!empty) continue;
            Links[a].Add(b);
            Links[b].Add(a);
        }
        // A point linked to two bases would be takeable by both sides from the first minute, whatever happened
        // anywhere else (Novigrad's Signal Hill): it belongs to the nearer one.
        for (int i = 0; i < n; i++)
        {
            var bases = Links[i].Where(j => j >= n).OrderBy(j => pos[i].DistanceSquaredTo(pos[j])).ToList();
            foreach (int j in bases.Skip(1)) { Links[i].Remove(j); Links[j].Remove(i); }
        }
    }
    public readonly List<Squad>[] Squads = { new(), new(), new() };
    public Squad? PlayerSquad { get; private set; }
    /// <summary>Where the player wants to respawn: -1 = the default choice, 0 = base, 1+ = point index + 1.</summary>
    public int PlayerSpawn = -1;

    readonly Dictionary<Personality, (int Team, Squad Squad, Role Role)> _roster = new();
    readonly Dictionary<Squad, (Site Site, Vector3 Post)> _posts = new();
    /// <summary>Enemies our recon teams have reported: (team that knows, where, when). Shown on the map.</summary>
    public readonly List<(int Team, Vector3 Pos, double At)> Intel = new();
    /// <summary>The roles a player can pick (squad leader stays a bot's job for now).</summary>
    public static readonly Role[] PlayerRoles = { Role.Leader, Role.Rifleman, Role.AutoRifleman, Role.Grenadier, Role.Medic, Role.Marksman, Role.Engineer, Role.Ammo, Role.AntiTank, Role.HeavyAT, Role.AntiAir, Role.Crewman, Role.DroneOperator };
    public MotorPool Motor = null!;
    public static TerritoryMode? I { get; private set; }
    static readonly bool NoCombinedArms = OS.GetCmdlineUserArgs().Contains("noca"); // A/B testing
    readonly HashSet<Squad> _bound = new();

    /// <summary>Is the player leading their squad (picked the squad leader role)?</summary>
    public bool PlayerLeads => PlayerSquad != null && PlayerBody is { Alive: true } p && p.Kit == Role.Leader;
    readonly List<(Personality P, double At)> _respawns = new();
    /// <summary>Where each squad last had someone on his feet: a squad wiped out comes back near there.</summary>
    readonly Dictionary<Squad, Vector3> _lastSeenAt = new();
    /// <summary>How long most of a squad waits for the rest of it to get somewhere it can take them on, before it comes up on its own.</summary>
    const double SplitAfter = 90.0;
    /// <summary>A group of replacements that's just come up: the rest of that squad's due in the next few seconds go with it.</summary>
    readonly Dictionary<Squad, ((string Name, Vector3 Pos, int Point) Spawn, double Until, string How)> _joining = new();
    double _nextReinforce;
    /// <summary>Why you aren't back yet while you wait to rejoin your squad ("" when you come back on the timer).</summary>
    public string PlayerWait { get; private set; } = "";
    readonly Queue<Bot> _corpses = new();
    readonly RandomNumberGenerator _rng = new();
    readonly double[] _commandAt = new double[3];
    TerritoryHud _hud = null!;
    double _nextTick, _nextBleed, _nextDump, _playerDiedAt = -1;
    bool _navLogged, _upChecked;
    int _kills;
    int CorpseCap => Math.Max(60, Setup.TeamSize * 3);

    public override void _Ready()
    {
        _rng.Randomize();
        BotBrain.DefaultObjective = null;
        int n = Map.Sites.Count;
        Points = Map.Sites.Select(s => new SiteObjective { Site = s, Map = Map, R = s.Radius + CaptureExtra }).ToArray();
        Owner = Enumerable.Repeat(-1, n).ToArray();
        Progress = new float[n];
        Capper = Enumerable.Repeat(-1, n).ToArray();
        Inside = new int[n, 3];
        StartTickets = TicketsOverride > 0 ? TicketsOverride : Setup.TeamSize * 30;
        for (int t = 0; t < 3; t++) Tickets[t] = StartTickets;
        Front = Settings.FrontLine;
        BuildLinks();
        HQ = Enumerable.Range(0, 3).Select(t => new SiteObjective
        {
            Site = new Site { Name = $"{KothMode.TeamNames[t]} HQ", Kind = "Base", Center = Map.Ground(Map.Bases[t]), Radius = 36f },
            Map = Map, R = 50f,
        }).ToArray();
        Squad.Hostile = (site, team) =>
        {
            int i = IndexOf(site);
            if (i < 0) return false;
            if (i >= n) return OwnerOf(i) != team || HQInside[i - n, (team + 1) % 3] + HQInside[i - n, (team + 2) % 3] > 0;
            return (Owner[i] >= 0 && Owner[i] != team) || Inside[i, (team + 1) % 3] + Inside[i, (team + 2) % 3] > 0;
        };
        if (Front)
        {
            // Each side starts holding its own sector: every point clearly nearer its base than
            // anyone else's. The middle ground starts neutral, so the fight starts there.
            for (int i = 0; i < n; i++)
            {
                var d = Enumerable.Range(0, 3).Select(t => Points[i].Center.DistanceTo(Map.Bases[t])).ToArray();
                int near = Array.IndexOf(d, d.Min());
                float second = Enumerable.Range(0, 3).Where(t => t != near).Min(t => d[t]);
                if (d[near] < second * 0.8f) { Owner[i] = near; Progress[i] = 1f; }
            }
            // And at least its home point: the nearest one linked to its base.
            for (int t = 0; t < 3; t++)
            {
                if (Enumerable.Range(0, n).Any(i => Owner[i] == t)) continue;
                int home = Links[n + t].Where(j => j < n && Owner[j] < 0)
                    .OrderBy(j => Points[j].Center.DistanceTo(Map.Bases[t])).DefaultIfEmpty(-1).First();
                if (home < 0) continue;
                Owner[home] = t;
                Progress[home] = 1f;
            }
            // Everyone starts with as many points as the side with the fewest: the ones nearest its base. (Where the
            // map gave one side 3 and another 6, the short side bled tickets from the first 15 s, before a shot was
            // fired, and lost 183 of them in 13 minutes to the other's none.)
            int fewest = Enumerable.Range(0, 3).Min(t => Owned(t));
            for (int t = 0; t < 3; t++)
                foreach (int i in Enumerable.Range(0, n).Where(i => Owner[i] == t).OrderBy(i => Points[i].Center.DistanceTo(Map.Bases[t])).Skip(Math.Max(1, fewest)))
                {
                    Owner[i] = -1;
                    Progress[i] = 0f;
                }
        }

        var names = Personality.Callsigns.OrderBy(_ => _rng.Randi()).ToList();
        int k = 0;
        BotBrain.ResetStatics();
        Squad.Engagements = Squad.Assaults = Squad.Hunts = 0;
        Squad.ResetAll();
        Squad.Orps = Squad.Deploys = Squad.BuddySwaps = Squad.Crossings = Squad.CrossingsDone = Squad.CrossingsAborted = Squad.SmokeCrossings = 0;
        Drone.Clear();
        SmokeScreen.Clear();
        PerchClaims.Clear();
        MotorPool.ResetCounters();
        CrewBrain.AreaRounds = CrewBrain.HeldForFriendlies = 0;
        Drone.Log = Log;
        Bot.StuckEvents = Bot.StuckNearVehicle = Bot.StuckBoarding = Bot.StuckWaiting = 0;
        CrewBrain.MortarRounds = 0;
        CrewBrain.MortarLoaded = 0;
        Fortifications.Clear();
        Motor = new MotorPool(this);
        Radio.Reset();
        global::Ridgeline.Intel.Reset();
        for (int team = 0; team < 3; team++)
        {
            var fleet = MotorPool.Fleet(Setup.TeamSize, team);
            // Vehicle crews first (they come out of the side's headcount), then the infantry.
            var comp = Roles.Compose(Setup.TeamSize - MotorPool.CrewCount(Setup.TeamSize, team));
            // The logistics truck needs a logistics team to drive it.
            if (fleet.Contains(VKind.Logistics) && comp.All(c => c.Kind != SquadKind.Logistics))
                comp.Add((SquadKind.Logistics, new List<Role> { Role.Ammo, Role.Medic }));
            else if (fleet.Contains(VKind.Logistics))
                comp.First(c => c.Kind == SquadKind.Rifle).Roles.AddRange(new[] { Role.Rifleman, Role.Rifleman }); // headcount already set aside
            foreach (var vk in fleet)
            {
                if (vk == VKind.Logistics) continue;
                comp.Add((MotorPool.CrewKind(vk), Enumerable.Repeat(Role.Crewman, MotorPool.Crew(vk)).ToList()));
            }
            for (int s = 0; s < comp.Count; s++)
            {
                var sq = new Squad { Team = team, Number = s + 1, Kind = comp[s].Kind };
                Squads[team].Add(sq);
                var roles = comp[s].Roles;
                // The player takes a place in the first rifle squad: the leader's, if that's
                // the role they picked, otherwise a rifleman's.
                if (team == 0 && s == 0 && Setup.PlayerJoins && roles.Count > 1)
                {
                    int slot = roles.IndexOf(Settings.PlayerRole == Role.Leader ? Role.Leader : Role.Rifleman);
                    roles.RemoveAt(slot >= 0 ? slot : roles.Count - 1);
                }
                foreach (var role in roles)
                {
                    var p = Personality.Roll(_rng, names[k++ % names.Count]);
                    // Recon teams are picked from the sharper eyes.
                    if (sq.Kind == SquadKind.Recon) { p.SpotRate *= 1.25f; p.Skill = MathF.Max(p.Skill, 0.6f); }
                    _roster[p] = (team, sq, role);
                }
            }
        }
        if (Setup.PlayerJoins) PlayerSquad = Squads[0][0];
        Squad.Spotted += OnSpotted;

        // The motor pool: vehicles parked in a row at each base, facing the middle, each bound to its crew.
        for (int team = 0; team < 3; team++)
        {
            var home = Map.Bases[team];
            var toMid = (-home with { Y = 0f }).Normalized();
            var right = toMid.Cross(Vector3.Up);
            float yaw = MathF.Atan2(-toMid.X, -toMid.Z);
            var fleet = MotorPool.Fleet(Setup.TeamSize, team);
            var crews = Squads[team].Where(s => s.Kind is SquadKind.Armor or SquadKind.Transport or SquadKind.Air or SquadKind.Mortar).ToList();
            var logi = Squads[team].FirstOrDefault(s => s.Kind == SquadKind.Logistics);
            int ci = 0;
            for (int i = 0; i < fleet.Count; i++)
            {
                // Aircraft on their own pad further back; the mortar dug in just forward of base.
                var park = fleet[i] == VKind.Mortar ? home + toMid * 60f + right * 25f
                         : fleet[i] is VKind.UH or VKind.AH ? home - toMid * 25f + right * (fleet[i] == VKind.UH ? -22f : 22f)
                         : home + toMid * 28f + right * ((i - (fleet.Count - 1) / 2f) * 8f);
                var crew = fleet[i] == VKind.Logistics ? logi : crews.FirstOrDefault(c => c.Kind == MotorPool.CrewKind(fleet[i]) && c.Vehicle == null && !_bound.Contains(c));
                if (crew != null) _bound.Add(crew);
                Motor.Add(fleet[i], team, crew, park, yaw);
            }
        }
        Vehicle.Lost += OnVehicleLost;
        Fob.Lost += OnFobLost;

        Spec = new Spectator { Mode = this };
        AddChild(Spec);
        _hud = new TerritoryHud { Mode = this };
        AddChild(_hud);
        AddChild(new BotDebugDraw { Mode = this });

        Combatants.Killed += OnKilled;
        Combatants.Down += OnDowned;
        Comms.Said += OnSaid;
        I = this;

        for (int t = 0; t < 3; t++) Command(t);
        foreach (var (p, (team, _, _)) in _roster) SpawnBot(p, team, atBase: true);
        if (Setup.PlayerJoins) SpawnPlayer(atBase: true);
        else Spec.Activate(Bots.FirstOrDefault());
        for (int t = 0; t < 3; t++) _commandAt[t] = Clock.Now + CommandEvery + t * 5.0;
        _nextTick = Clock.Now + TickEvery;
        _nextBleed = Clock.Now + BleedEvery;
        _hud.Center("Take and hold the settlements", 5f);
        Log($"--- TERRITORY {Setup.TeamSize}x3, {n} points, {StartTickets} tickets, {Squads[0].Count} squads per side ---");
        if (Telemetry.PathFromArgs() is { } tp) Telemetry.Start(tp, this);
    }

    public override void _ExitTree()
    {
        Telemetry.Stop();
        Combatants.Killed -= OnKilled;
        Combatants.Down -= OnDowned;
        Comms.Said -= OnSaid;
        Squad.Spotted -= OnSpotted;
        Vehicle.Lost -= OnVehicleLost;
        Fob.Lost -= OnFobLost;
    }

    void OnVehicleLost(Vehicle v, ICombatant? by)
    {
        Motor.OnLost(v);
        Spend(v.Team, v.Def.Tickets);
        _hud.Event($"{KothMode.TeamNames[v.Team]} lost a {v.Def.Name} ({v.Def.Tickets} tickets){(by != null ? $" to {by.Callsign}" : "")}", v.Team == 0 ? -1 : by?.Team == 0 ? 1 : 0);
        Log($"[{Clock.Now:0}s] VEHICLE LOST {v.Def.Name} (team {v.Team}) by {by?.Callsign ?? "?"}"
            + (by is Bot kb ? $" ({(kb.Ride != null ? kb.Ride.Def.Name : Roles.Name(kb.Role))}, {kb.FeetPos.DistanceTo(v.GlobalPosition):0} m)" : "")
            + (v.Def.Air ? $", {v.Agl:0} m up" : ""));
    }

    void OnFobLost(Fob f, string how)
    {
        _hud.Event($"{KothMode.TeamNames[f.Team]} {f.Label} {how}", f.Team == 0 ? -1 : 0);
        Log($"[{Clock.Now:0}s] FOB {f.Label} (team {f.Team}) {how}");
    }

    int _fobCount;

    public void BuildFob(int team, Vector3 at)
    {
        var f = new Fob { Team = team, Label = $"FOB {(char)('A' + _fobCount++ % 26)}" };
        GetParent().AddChild(f);
        f.GlobalPosition = Map.Ground(at);
        _hud.Event($"{KothMode.TeamNames[team]} built {f.Label}", team == 0 ? 1 : 0);
        Log($"[{Clock.Now:0}s] FOB built by {KothMode.TeamNames[team]} at {at}");
    }

    /// <summary>
    /// A recon team reports a contact: it goes on the map for their side, and any of
    /// their side near it hears about it on the radio.
    /// </summary>
    void OnSpotted(int team, ICombatant who, Vector3 at)
    {
        Intel.Add((team, at, Clock.Now));
        if (Intel.Count > 400) Intel.RemoveRange(0, 100);
        foreach (var b in Bots)
            if (IsInstanceValid(b) && b.Alive && b.Team == team && b.Squad?.Kind != SquadKind.Recon && b.FeetPos.DistanceTo(at) < 250f)
                b.Senses.Share(who, at);
    }

    // ================================================================ spawning

    /// <summary>
    /// Where this side can reinforce: its base, unless the enemy is on it; every FOB with no enemy right on it; and
    /// every point it owns with no enemy on or near it. FOBs and points only while they're supplied: joined to the
    /// headquarters through our own ground (FOBs within 500 m of it or of such a point). Can be empty.
    /// </summary>
    public List<(string Name, Vector3 Pos, int Point)> SpawnOptions(int team)
    {
        var list = new List<(string, Vector3, int)>();
        if (Out[team] || Spent(team)) return list;
        if (HQOpen(team)) list.Add(("Base", Map.Bases[team], -1));
        var supplied = Supplied(team);
        bool Reach(Vector3 p)
        {
            if (p.DistanceTo(Map.Bases[team]) < 500f) return true;
            for (int i = 0; i < Points.Length; i++) if (supplied[i] && Points[i].Center.DistanceTo(p) < 500f) return true;
            return false;
        }
        // FOBs: spawn indices from 100 up.
        for (int k = 0; k < Fob.All.Count; k++)
            if (Fob.All[k].Team == team && !EnemyNear(team, Fob.All[k].GlobalPosition, 40f) && Reach(Fob.All[k].GlobalPosition))
                list.Add((Fob.All[k].Label, Fob.All[k].GlobalPosition, 100 + k));
        for (int i = 0; i < Points.Length; i++)
            if (supplied[i] && Progress[i] > 0.5f && !EnemyNear(team, Points[i].Center, Points[i].Radius + 50f))
                list.Add((Points[i].Site.Name, Points[i].Center, i));
        return list;
    }

    /// <summary>The headquarters is taking reinforcements: we're still in it, and no enemy is on it or close.</summary>
    public bool HQOpen(int team) => !Out[team] && !Spent(team) && !EnemyNear(team, HQ[team].Center, HQ[team].Radius + 40f);

    bool EnemyNear(int team, Vector3 p, float r)
    {
        foreach (var c in Combatants.All)
            if (c.Alive && c.Team != team && (c.FeetPos - p with { Y = c.FeetPos.Y }).Length() < r) return true;
        return false;
    }

    /// <summary>
    /// Somewhere to come in round a spawn, not in a wall and not somewhere cut off. A point: just outside it on the
    /// side away from the enemy (toward our base), so men come up to it rather than appearing in the middle of it.
    /// (Respawning anywhere inside the circle, they made whichever point was a squad's nearest spawn an instant
    /// fortress: 13 men sat in one Novigrad district for half an hour, while the points around it were left empty.
    /// And a spot anywhere could be inside a building's wall, or, before the navmesh was up, as for the whole first
    /// wave, an island the navmesh didn't get into: men put there stood there all match, and their squad leader
    /// kept waiting for them.)
    /// </summary>
    Vector3 SpawnAt((string Name, Vector3 Pos, int Point) o, int team)
    {
        var p = o.Pos;
        for (int k = 0; k < 10; k++)
        {
            float a = _rng.Randf() * Mathf.Tau;
            if (o.Point >= 0 && o.Point < 100)
            {
                var pt = Points[o.Point];
                var back = (Map.Bases[team] - pt.Center) with { Y = 0f };
                back = back.LengthSquared() > 1f ? back.Normalized() : Vector3.Forward;
                p = Map.Ground(pt.Center + back.Rotated(Vector3.Up, _rng.RandfRange(-0.7f, 0.7f)) * (pt.Radius + _rng.RandfRange(6f, 22f))) + Vector3.Up * 0.3f;
            }
            else
            {
                float r = o.Point < 0 ? _rng.RandfRange(3f, 14f) : _rng.RandfRange(3f, 10f);
                p = Map.Ground(o.Pos + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r)) + Vector3.Up * 0.3f;
            }
            if (!InSomething(p) && !CutOff(p)) return p;
            Prof.Count(k < 9 ? "spawn:spot rejected" : "spawn:no good spot, took the last");
        }
        return p;
    }

    static readonly CapsuleShape3D _spawnBody = new() { Radius = 0.3f, Height = 1.8f };

    /// <summary>A man standing here would be inside something solid (a wall, a crate, a parked vehicle).</summary>
    bool InSomething(Vector3 feet)
    {
        var q = new PhysicsShapeQueryParameters3D
        {
            Shape = _spawnBody,
            Transform = new Transform3D(Basis.Identity, feet + Vector3.Up * 1.0f),
            CollisionMask = Layers.World | Layers.Vehicles | Layers.Doors,
        };
        return Map.GetWorld3D().DirectSpaceState.IntersectShape(q, 1).Count > 0;
    }

    /// <summary>
    /// On a patch of navmesh with no way off it: a route toward somewhere 60 m away can't get 10 m from here.
    /// Before the navmesh is ready there's no telling; the answer is no.
    /// </summary>
    bool CutOff(Vector3 p)
    {
        var nm = Map.GetWorld3D().NavigationMap;
        if (!Map.Nav.Finished || NavigationServer3D.MapGetIterationId(nm) == 0) return false;
        var from = Map.Nav.ClosestPoint(p);
        float a = _rng.Randf() * Mathf.Tau;
        var to = Map.Ground(from + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * 60f);
        var path = NavBaker.Path(nm, from, to);
        return path.Length > 0 && path[^1].DistanceTo(from) < 10f && to.DistanceTo(from) > 40f;
    }

    /// <summary>The spawn closest to where the squad is headed (null: nowhere to come back yet).</summary>
    (string Name, Vector3 Pos, int Point)? BestSpawn(int team, Squad? sq)
    {
        var opts = SpawnOptions(team);
        if (opts.Count == 0) return null;
        // Replacements join their squad where it is (its leader, on foot); with nobody left, where it was sent.
        // (Coming back nearest the objective, you were often 300-900 m from your squad: ahead of it, or off to
        // one side, with a long walk to find it.)
        // With nobody left on his feet, near where it last was: its men come back near where they fell, not at the
        // objective it was sent to (often a point further off than the FOB by the fight). Only with nothing known of
        // it, where it was sent.
        // (The leader riding counts: where the squad is, not where it got in.)
        var lead = Anchor(sq) ?? sq?.Leader;
        Vector3? near = lead?.FeetPos ?? (sq != null && _lastSeenAt.TryGetValue(sq, out var last) ? last : sq?.Objective?.Center);
        if (near is not Vector3 goal) return opts[0];
        // A FOB counts as 150 m nearer: it's built forward to put men back into the fight close to it.
        return opts.OrderBy(o => (o.Pos - goal with { Y = o.Pos.Y }).Length() - (o.Point >= 100 ? 150f : 0f)).First();
    }

    float Face(Vector3 from, Vector3 to)
    {
        var d = to - from;
        return Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z));
    }

    /// <returns>False if there's nowhere this man can come back yet (the base under attack, no supplied point).</returns>
    bool SpawnBot(Personality p, int team, bool atBase = false)
    {
        var sq = _roster[p].Squad;
        // With a front line, the infantry deploys straight to the point nearest its orders
        // (in its own sector) rather than walking kilometres from base; crews start with their vehicles.
        (string Name, Vector3 Pos, int Point)? where = IsCrew(sq) || (atBase && !Front) ? (HQOpen(team) ? ("Base", Map.Bases[team], -1) : null) : BestSpawn(team, sq);
        if (where is not { } w) return false;
        SpawnBot(p, team, w);
        return true;
    }

    /// <summary>Vehicle crews: they come back at base, where the vehicles are.</summary>
    static bool IsCrew(Squad sq) => sq.Kind is SquadKind.Armor or SquadKind.Transport or SquadKind.Air or SquadKind.Logistics or SquadKind.Mortar;

    void SpawnBot(Personality p, int team, (string Name, Vector3 Pos, int Point) w)
    {
        var sq = _roster[p].Squad;
        var role = _roster[p].Role;
        var b = new Bot { TeamId = team, P = p, Role = role, Def = Roles.Primary(role), Squad = sq };
        GetParent().AddChild(b);
        b.GlobalPosition = SpawnAt(w, team);
        b.Aim.Yaw = Face(b.GlobalPosition, sq.Objective?.Center ?? Vector3.Zero);
        sq.Join(b);
        Bots.Add(b);
    }

    /// <returns>False if there's nowhere to come back yet.</returns>
    bool SpawnPlayer(bool atBase = false)
    {
        PlayerWait = "";
        var opts = SpawnOptions(0);
        if (opts.Count == 0) { PlayerWait = "nowhere to come back: every spawn is cut off or under fire"; return false; }
        var baseOpt = opts.FirstOrDefault(o => o.Point == -1);
        string wait = "";
        (string Name, Vector3 Pos, int Point)? pick = atBase && !Front && baseOpt.Name != null ? baseOpt
            : atBase ? BestSpawn(0, PlayerSquad)
            : PlayerSpawn == 0 && baseOpt.Name != null ? baseOpt
            : PlayerSpawn > 0 && opts.FirstOrDefault(o => o.Point == PlayerSpawn - 1) is { Name: not null } chosen ? chosen
            // Otherwise with your squad, the way its other men come back (a spawn you pick is going on your own).
            : PlayerSquad != null ? JoinAt(PlayerSquad, _playerDiedAt, out wait)
            : BestSpawn(0, PlayerSquad);
        PlayerWait = pick == null ? wait : "";
        if (pick is not { } where) return false;
        PlayerBody?.QueueFree();
        var p = new Player { TeamId = 0, Kit = Settings.PlayerRole };
        GetParent().AddChild(p);
        p.GlobalPosition = SpawnAt(where, 0);
        p.SetYaw(Face(p.GlobalPosition, PlayerSquad?.Objective?.Center ?? Vector3.Zero));
        PlayerSquad?.Join(p);
        // Leading: your squad forms up on you by default.
        if (PlayerSquad != null) PlayerSquad.FollowPlayer = Settings.PlayerRole == Role.Leader;
        SoundWorld.I.ResetHearing();
        PlayerBody = p;
        PlayerHud.P = p;
        PlayerRespawnAt = -1;
        _playerDiedAt = -1;
        // A spawn you picked is for this time only. (It used to stick: every later death put you back there, wherever
        // your squad had got to since.)
        PlayerSpawn = -1;
        Spec.Deactivate();
        return true;
    }

    /// <summary>How long a replacement waits for his squad to get somewhere it can take him on, before he's sent up on his own.</summary>
    const double ReplaceAfter = 180.0;

    /// <summary>
    /// Where the men a squad has lost can come back to it now; null (and why) while they wait. They come up
    /// together, to the squad:
    /// - once it's at a spawn of ours (its leader in or by a point we hold, at a FOB or at base) and out of contact;
    /// - all at once where it was sent, when nobody is left on his feet: the squad comes back as a whole;
    /// - or, when it's got nowhere near one in three minutes, on their own to the spawn nearest it, once it's out of
    ///   contact.
    /// A squad in a fight gets nobody new, so it can be worn down and wiped out. (Every man used to come back on his
    /// own 10 s after he was killed, at the spawn nearest the objective: no squad was ever wiped out, and men came
    /// back 300-900 m from their squads and wandered about looking for them.) Vehicle crews come back at base, where
    /// the vehicles are.
    /// </summary>
    (string Name, Vector3 Pos, int Point)? JoinAt(Squad sq, double diedAt, out string why)
    {
        why = "";
        int team = sq.Team;
        if (IsCrew(sq))
        {
            if (HQOpen(team)) return ("Base", Map.Bases[team], -1);
            why = "the base is under attack";
            return null;
        }
        var opts = SpawnOptions(team);
        if (opts.Count == 0) { why = "nowhere to come back: every spawn is cut off or under fire"; return null; }
        // The rest of a group that's just come up goes with it: the last to die in a wipe, or you a second behind
        // the others. (Coming back, why says how: wiped out, at a spawn, or sent up.)
        if (_joining.TryGetValue(sq, out var j) && Clock.Now < j.Until && opts.Any(o => o.Point == j.Spawn.Point && o.Pos == j.Spawn.Pos))
        {
            why = j.How;
            return j.Spawn;
        }
        (string Name, Vector3 Pos, int Point)? w = null;
        if (Standing(sq) == 0)
        {
            // Wiped out: it comes back as a whole, once the last of them is due, you included. (Whoever was due
            // first used to come back alone; then there was someone standing again, and the rest waited for him.)
            double last = Math.Max(_respawns.Where(r => _roster[r.P].Squad == sq).Select(r => r.At).DefaultIfEmpty(0.0).Max(),
                                   sq == PlayerSquad && PlayerBody is { Dead: true } && _playerDiedAt > 0 ? _playerDiedAt + RespawnDelay : 0.0);
            if (Clock.Now < last) { why = $"{sq.Name} was wiped out: it comes back together in {last - Clock.Now:0}s"; return null; }
            why = "wiped out";
            w = BestSpawn(team, sq);
        }
        else
        {
            var anchor = Anchor(sq);
            if (!sq.Engaged && anchor != null)
                foreach (var o in opts)
                    if (At(o, anchor.FeetPos)) { why = "at a spawn"; w = o; break; }
            double left = ReplaceAfter - (Clock.Now - diedAt);
            if (w == null && !sq.Engaged && left <= 0) { why = "sent up"; w = BestSpawn(team, sq); }
            if (w == null)
            {
                var waitingBots = _respawns.Where(r => _roster[r.P].Squad == sq).ToList();
                double splitIn = waitingBots.Count > 0 ? SplitAfter - (Clock.Now - (waitingBots.Min(r => r.At) - RespawnDelay)) : SplitAfter;
                bool mostDown = sq.Kind == SquadKind.Rifle && sq.DetachedFrom == null && sq.LinkUpWith == null && !Squads[sq.Team].Any(x => x.DetachedFrom == sq)
                                && waitingBots.Count >= 2 && Waiting(sq) >= Standing(sq);
                why = sq.Engaged ? $"{sq.Name} is in contact, and nobody new comes up until it's out of it"
                    : sq.Regrouping ? $"{sq.Name} is falling back to {sq.OrderPlace} to regroup"
                    : $"{sq.Name} takes you on when it reaches a spawn of ours, or you go up on your own in {left:0}s";
                // Most of it down: the rest of you come up as a detachment after a while, fighting or not.
                if (mostDown) why += $" (most of it is down: you come up as a detachment in {Math.Max(0, splitIn):0}s)";
                return null;
            }
        }
        if (w is { } got) _joining[sq] = (got, Clock.Now + 15.0, why);
        return w;
    }

    /// <summary>
    /// Where the squad is, for spawning: its leader, on foot (null while he rides); with no leader, whoever is
    /// still on his feet (you, as a rifleman: only a bot can be the leader then). Not any straggler: one man
    /// wandering off by a spawn brought the whole squad's replacements up there.
    /// </summary>
    static ICombatant? Anchor(Squad? sq) =>
        sq == null ? null : sq.Leader is { } l ? (l.Ride == null ? l : null) : sq.Members.FirstOrDefault(m => Here(m) && m.Ride == null);

    /// <summary>Is this spot at that spawn: in or right by a point, or by a FOB or the base.</summary>
    bool At((string Name, Vector3 Pos, int Point) o, Vector3 p)
    {
        float r = o.Point is >= 0 and < 100 ? Points[o.Point].Radius + 40f : o.Point < 0 ? 80f : 40f;
        return (o.Pos - p with { Y = o.Pos.Y }).Length() < r;
    }

    /// <summary>In the world and on his feet (Alive is false for the downed as well as the dead).</summary>
    static bool Here(ICombatant m) => m is GodotObject g && IsInstanceValid(g) && m.Alive;

    int Standing(Squad sq) => sq.Members.Count(Here);

    /// <summary>Men this squad has lost who are waiting to come back to it (you among them).</summary>
    int Waiting(Squad sq) => _respawns.Count(r => _roster[r.P].Squad == sq) + (sq == PlayerSquad && PlayerBody is { Dead: true } ? 1 : 0);

    /// <summary>
    /// Too few left on their feet to fight as a squad: a third of it or less, with at least two men waiting to come
    /// up. It falls back to take on its replacements.
    /// </summary>
    bool Shattered(Squad sq)
    {
        int standing = Standing(sq), waiting = Waiting(sq);
        int full = standing + waiting + sq.Members.Count(m => m is GodotObject g && IsInstanceValid(g) && m.Downed && !m.Dead);
        // Once it's falling back it keeps going until it has its replacements, or is back over half strength. (A man
        // patched up on the way used to send it straight back to the attack, and his next wound back to the rear.)
        if (sq.Regrouping) return standing >= 1 && waiting >= 1 && standing <= full / 2;
        return standing >= 1 && waiting >= 2 && standing <= Math.Max(2, full / 3);
    }

    /// <summary>Where a shattered squad falls back to: the spawn nearest it, as an objective, and its name for the order.</summary>
    (Site? Site, IObjective Obj, string Name)? RegroupAt(Squad sq)
    {
        // Where it's already falling back to while that's still open: the nearest spawn changes as it goes, and it
        // used to be sent back and forth between two of them.
        var keep = sq.Regrouping ? SpawnOptions(sq.Team).FirstOrDefault(o => (o.Point < 0 ? "base" : o.Name) == sq.OrderPlace) : default;
        (string Name, Vector3 Pos, int Point)? pick = keep.Name != null ? keep : BestSpawn(sq.Team, sq);
        if (pick is not { } w) return null;
        if (w.Point is >= 0 and < 100) return (Points[w.Point].Site, Points[w.Point], w.Name);
        if (w.Point < 0) return (HQ[sq.Team].Site, HQ[sq.Team], "base");
        return (null, new PointObjective { Center = w.Pos, Watch = sq.Objective?.Center ?? w.Pos }, w.Name);
    }

    /// <summary>Once a second: the men waiting to come back who can, squad by squad, all together.</summary>
    void Reinforce(double now)
    {
        _respawns.RemoveAll(r => Winner >= 0 || Out[_roster[r.P].Team] || Spent(_roster[r.P].Team));
        for (int t = 0; t < 3; t++)
            foreach (var s in Squads[t])
                if ((Anchor(s) ?? s.Leader) is { } a) _lastSeenAt[s] = a.FeetPos;
        LinkUps(now);
        foreach (var g in _respawns.Where(r => now >= r.At).GroupBy(r => _roster[r.P].Squad).ToList())
        {
            var sq = g.Key;
            double oldest = g.Min(r => r.At) - RespawnDelay;
            var into = sq;
            if (JoinAt(sq, oldest, out string how) is not { } w)
            {
                // Most of the squad down, and the rest hasn't got anywhere it can take them on in time: they come up
                // anyway, as a detachment of their own under the senior man among them, at the spawn nearest the
                // squad. The smaller of the two goes to join the larger; the larger gets on with the job. They merge
                // again when they meet (LinkUps). (Otherwise they waited for as long as the survivors took to reach a
                // spawn, and a squad down to two men in a long fight kept four men and you out of the match.)
                if (!CanSplit(sq, g.Count(), oldest, now) || BestSpawn(sq.Team, sq) is not { } sw) continue;
                into = Detach(sq);
                w = sw;
                how = "a detachment";
                _joining[into] = (sw, now + 15.0, how);
            }
            int n = 0;
            foreach (var r in g)
            {
                _respawns.Remove(r);
                if (into != sq) _roster[r.P] = (_roster[r.P].Team, into, _roster[r.P].Role);
                SpawnBot(r.P, sq.Team, w);
                n++;
            }
            if (IsCrew(sq)) continue;
            if (into != sq) Split(sq, into, n, now);
            Log($"[{now:0}s] {into.Name}: {n} replacement{(n == 1 ? "" : "s")} up at {w.Name} ({how})");
            Telemetry.Reinforce(into, n, w.Name, how);
            if (into == PlayerSquad && PlayerBody is { Alive: true }) _hud.Event($"{n} replacement{(n == 1 ? "" : "s")} joined {into.Name} at {w.Name}", 1);
            // Back up to strength: new orders.
            if (sq.Regrouping) _commandAt[sq.Team] = Math.Min(_commandAt[sq.Team], now + 2.0);
        }
    }

    /// <summary>A rifle squad with most of its men waiting (two or more), and the oldest of them waiting long enough.</summary>
    bool CanSplit(Squad sq, int due, double oldest, double now) =>
        sq.Kind == SquadKind.Rifle && sq.DetachedFrom == null && sq.LinkUpWith == null && !Squads[sq.Team].Any(d => d.DetachedFrom == sq)
        && due >= 2 && Standing(sq) >= 1 && Waiting(sq) >= Standing(sq) && now - oldest >= SplitAfter;

    Squad Detach(Squad sq)
    {
        var d = new Squad { Team = sq.Team, Number = sq.Number, Kind = sq.Kind, Suffix = "B", DetachedFrom = sq };
        Squads[sq.Team].Add(d);
        return d;
    }

    /// <summary>The detachment is up: who joins whom, and you with it if you're waiting too.</summary>
    void Split(Squad sq, Squad d, int n, double now)
    {
        // Waiting to come back yourself: you come up with the detachment (JoinAt finds its spawn in _joining, which lasts
        // past your own delay).
        if (PlayerSquad == sq && PlayerBody is { Dead: true }) PlayerSquad = d;
        bool detachmentLarger = n > Standing(sq);
        // A squad you lead, or one you've given an order, isn't re-tasked: that's your call, and the detachment comes to you.
        bool yours = sq == PlayerSquad && PlayerLeads || sq.PlayerOrderUntil > now;
        if (detachmentLarger && !yours && sq.Objective != null && sq.Objective is not FollowObjective)
        {
            // It takes over the job; the few left go to it.
            d.Order(sq.Site, sq.Objective, sq.Defend);
            sq.LinkUpWith = d;
            sq.Order(null, new FollowObjective { Target = d }, false, "Link up with", d.Name);
        }
        else
        {
            d.LinkUpWith = sq;
            d.Order(null, new FollowObjective { Target = sq }, false, "Link up with", sq.Name);
        }
        var follower = sq.LinkUpWith != null ? sq : d;
        if (follower.Leader is Bot fl) Comms.Say(fl, $"{follower.Name}, we're linking up with {follower.LinkUpWith!.Name}. Move!");
        if (follower == PlayerSquad || follower.LinkUpWith == PlayerSquad) _hud.Center($"Squad orders: {PlayerSquad!.OrderText}", 4f);
        Log($"[{now:0}s] {sq.Name} split: {d.Name} up with {n}, {sq.Name} has {Standing(sq)} standing; {follower.Name} links up with {follower.LinkUpWith!.Name}");
        _commandAt[sq.Team] = Math.Min(_commandAt[sq.Team], now + 2.0);
    }

    /// <summary>
    /// A squad split in two is one squad again once the two meet (leaders within 40 m), or as soon as either has
    /// nobody left on his feet (what's left of it is the squad).
    /// </summary>
    void LinkUps(double now)
    {
        for (int t = 0; t < 3; t++)
            foreach (var d in Squads[t].Where(s => s.DetachedFrom != null).ToList())
            {
                var p = d.DetachedFrom!;
                var ad = Anchor(d);
                var ap = Anchor(p);
                bool met = ad != null && ap != null && ad.FeetPos.DistanceTo(ap.FeetPos) < 40f;
                // Not while either is in a vehicle: the ride's carrying that squad, and would carry the men off with it (its
                // cargo gone, nobody would be let out at the drop). They merge once they're on foot.
                if (Riding(d) || Riding(p)) continue;
                if (met || Standing(d) == 0 || Standing(p) == 0) Merge(d, p, now, met ? "linked up with" : "merged back into");
            }
    }

    bool Riding(Squad s) => s.Transport != null || s.Members.Any(m => Here(m) && m.Ride != null);

    void Merge(Squad d, Squad p, double now, string how)
    {
        // Whichever of them had the job keeps it; and if you were with the detachment, what you'd told it goes on.
        if (p.LinkUpWith == d && d.Objective != null && d.Objective is not FollowObjective) p.Order(d.Site, d.Objective, d.Defend);
        if (PlayerSquad == d)
        {
            p.FollowPlayer = d.FollowPlayer;
            if (d.PlayerOrderUntil > now && d.Objective != null) { p.Order(d.Site, d.Objective, d.Defend); p.PlayerOrderUntil = d.PlayerOrderUntil; }
            // Still waiting to come up with it: you come up where it did.
            if (PlayerBody is { Dead: true } && _joining.TryGetValue(d, out var jd)) _joining[p] = jd;
        }
        foreach (var m in d.Members.ToList())
        {
            if (m is not GodotObject go || !IsInstanceValid(go)) continue;
            if (m is Bot b) b.Squad = p;
            if (!m.Dead) p.Join(m);
        }
        foreach (var k in _roster.Keys.ToList())
            if (_roster[k].Squad == d) _roster[k] = (_roster[k].Team, p, _roster[k].Role);
        if (PlayerSquad == d) PlayerSquad = p;
        d.Members.Clear();
        p.LinkUpWith = d.LinkUpWith = null;
        Squads[d.Team].Remove(d);
        Squad.All.Remove(d);
        _lastSeenAt.Remove(d);
        _joining.Remove(d);
        _commandAt[d.Team] = Math.Min(_commandAt[d.Team], now + 2.0);
        Log($"[{now:0}s] {d.Name} {how} {p.Name}");
        Telemetry.Note($"{d.Name} {how} {p.Name}");
        if (p == PlayerSquad) _hud.Event($"{d.Name} {how} {p.Name}", 1);
    }

    // ================================================================ orders

    /// <summary>
    /// The commander: value every point (take neutral and enemy points, answer
    /// threats to our own), then give each squad the best one near it, spreading
    /// squads over different points rather than piling them onto one.
    /// </summary>
    void Command(int team)
    {
        if (Out[team]) return;
        int n = Points.Length, N = n + 3;
        var value = new float[N];
        var known = new HashSet<ICombatant>[N];
        for (int i = 0; i < N; i++) known[i] = new HashSet<ICombatant>();
        foreach (var b in Bots)
        {
            if (!IsInstanceValid(b) || !b.Alive || b.Team != team) continue;
            foreach (var t in b.Senses.Threats)
            {
                if (!t.Who.Alive || Clock.Now - Math.Max(t.LastSeen, t.LastHeard) > 15.0) continue;
                for (int i = 0; i < N; i++)
                    if ((t.LastKnownPos - Obj(i).Center with { Y = t.LastKnownPos.Y }).Length() < Obj(i).Radius + 50f) known[i].Add(t.Who);
            }
        }
        double now0 = Clock.Now;
        for (int i = 0; i < n; i++)
        {
            int threat = known[i].Count;
            int inside = Inside[i, (team + 1) % 3] + Inside[i, (team + 2) % 3];
            bool home = Links[i].Contains(n + team);
            if (Owner[i] == team)
                // Ours: worth everyone if it's actually being taken (a real attack on it, or the hold slipping); worth
                // someone if an enemy's on it or about; with a front line, a point on it is worth a garrison even when
                // quiet. The points in front of the headquarters matter most. (A single enemy stepping inside, a passing
                // scout, used to count as an attack and recall every squad from its own attack, every 20 s.)
                value[i] = Progress[i] < 0.8f || inside >= 2 || threat >= 3 ? 8f + threat * 0.3f + (home ? 1.5f : 0f)
                         : inside > 0 || threat > 0 ? 4f
                         : Front && OnFront(team, i) ? 1.5f : -3f;
            else if (!CanTake(team, i)) value[i] = -20f; // behind the enemy's lines: can't be taken yet
            else
                // Take it. Press whoever's weakest rather than whoever's leading, and keep going where we've just
                // broken through. (Everyone used to go for the leader: the two sides behind always pulled it back, so
                // nobody was ever beaten, and a side that was losing was the one left alone.)
                value[i] = (Owner[i] < 0 ? 6.5f : 6f + (Weaker(Owner[i], team) ? 0.8f : 0f))
                         + (i < _exploit.GetLength(1) && _exploit[team, i] > now0 ? 1.2f : 0f);
        }
        // The headquarters: our own is worth everything once the enemy is at it (and nothing otherwise: nobody's sent to
        // sit at base); an enemy's is worth going for once the front reaches it, more when it has little else left.
        for (int t = 0; t < 3; t++)
        {
            int i = n + t;
            if (t == team)
            {
                // (Out of reserves: everyone falls back on it for the last stand.)
                bool threatened = Spent(t) || HQHold[t] < 0.999f || known[i].Count > 0 || Enumerable.Range(0, 3).Any(a => a != t && CanTakeHQ(a, t) && HQInside[t, a] > 0);
                value[i] = threatened ? 14f + known[i].Count * 0.3f : -50f;
            }
            else value[i] = CanTakeHQ(team, t) ? 7.5f + (Owned(t) <= 1 || Spent(t) ? 1.5f : 0f) + (Weaker(t, team) ? 0.8f : 0f) : -50f;
        }

        // Squads calling for help on the radio pull reinforcements to that point.
        foreach (var r in Radio.Log)
        {
            if (r.Team != team || r.Kind != RadioKind.HeavyContact || Clock.Now - r.At > 60.0) continue;
            int near = -1;
            float nd = 300f;
            for (int i = 0; i < n; i++)
            {
                float d = (Points[i].Center - r.Pos with { Y = Points[i].Center.Y }).Length();
                if (d < nd) { nd = d; near = i; }
            }
            if (near >= 0) value[near] += MathF.Min(4f, 1f + r.Count * 0.4f);
        }

        var assigned = new int[N];
        foreach (var sq in Squads[team])
            if (sq.Kind == SquadKind.Rifle && sq.PlayerOrderUntil > Clock.Now && IndexOf(sq.Site) is >= 0 and var pi) assigned[pi]++;

        // Defenders (front line): about one rifle squad in three holds the most threatened points
        // on our side of the front, so ground taken isn't simply walked away from.
        var defenders = new HashSet<Squad>();
        if (Front)
        {
            var rifles = Squads[team].Where(s => s.Kind == SquadKind.Rifle && s.PlayerOrderUntil <= Clock.Now && s.Alive > 0 && s.Transport == null && !s.Busy && !Shattered(s) && s.LinkUpWith == null).ToList();
            // One in three: with only two rifle squads both attack (engineers still dig in on the front).
            int want = rifles.Count / 3;
            // A point that already has a garrison keeps it unless another is clearly worse off (a couple of enemies
            // seen near another one used to march the defenders over, and back again when they'd gone).
            var front = Enumerable.Range(0, n).Where(i => OnFront(team, i))
                .OrderByDescending(i => known[i].Count * 3 + (Inside[i, (team + 1) % 3] + Inside[i, (team + 2) % 3] > 0 ? 6 : 0) + (Progress[i] < 0.95f ? 4 : 0)
                                        + (rifles.Any(s => s.Defend && s.Site == Points[i].Site) ? 5 : 0)).ToList();
            foreach (int i in front.Take(want))
            {
                var pos = Points[i].Center;
                var sq = rifles.Where(s => !defenders.Contains(s))
                    // A squad already defending keeps the job (swapping roles every cycle just marches everyone about).
                    .OrderBy(s => (s.Position ?? Map.Bases[team]).DistanceTo(pos) - (s.Defend ? 1500f : 0f) - (s.Site == Points[i].Site && s.Defend ? 2000f : 0f)).FirstOrDefault();
                if (sq == null) break;
                defenders.Add(sq);
                assigned[i]++;
                bool changed = sq.Site != Points[i].Site || !sq.Defend;
                sq.Order(Points[i].Site, Points[i], true);
                if (!changed) continue;
                if (sq.Leader is Bot dl) Comms.Say(dl, $"{sq.Name}, we're holding {Points[i].Site.Name}. Dig in!");
                if (sq == PlayerSquad) _hud.Center($"Squad orders: {sq.OrderText}", 4f);
                Log($"[{Clock.Now:0}s] {sq.Name}: {sq.OrderText} (defenders)");
            }
        }

        foreach (var sq in Squads[team])
        {
            if (sq.PlayerOrderUntil > Clock.Now || sq.Kind != SquadKind.Rifle || defenders.Contains(sq)) continue;
            // The smaller part of a split squad: it goes to join the rest (see Reinforce).
            if (sq.LinkUpWith is { } rest && Squads[team].Contains(rest))
            {
                sq.Order(null, new FollowObjective { Target = rest }, false, "Link up with", rest.Name);
                continue;
            }
            // Shattered: it breaks off whatever it was doing, falls back to the nearest spawn of ours and takes on its
            // replacements there. (You lead yours: that's your call.)
            if (!(sq == PlayerSquad && PlayerLeads) && Shattered(sq) && RegroupAt(sq) is { } rg)
            {
                bool fresh = !sq.Regrouping;
                sq.Order(rg.Site, rg.Obj, true, "Regroup at", rg.Name);
                if (!fresh) continue;
                if (sq.Leader is Bot rl) Comms.Say(rl, $"{sq.Name}, we can't fight like this. Fall back to {rg.Name}, we regroup there!");
                if (sq == PlayerSquad) _hud.Center($"Squad orders: {sq.OrderText}", 4f);
                Log($"[{Clock.Now:0}s] {sq.Name}: {sq.OrderText} ({Standing(sq)} standing, {Waiting(sq)} waiting)");
                continue;
            }
            // Consolidating, or in the middle of a deliberate attack: leave them to finish it.
            if (sq.Busy || sq.Phase != AssaultPhase.None) { if (IndexOf(sq.Site) is >= 0 and var bi) assigned[bi]++; continue; }
            if (sq.Transport != null && IndexOf(sq.Site) is >= 0 and var ti) { assigned[ti]++; continue; } // riding there: don't change its mind mid-journey
            // (Nobody up: from where it'll come back, near where it last was; see BestSpawn.)
            var origin = sq.Position ?? (_lastSeenAt.TryGetValue(sq, out var lastAt) ? lastAt : Map.Bases[team]);
            int best = -1;
            float bestScore = float.MinValue;
            for (int i = 0; i < N; i++)
            {
                var o = Obj(i);
                float d = (o.Center - origin with { Y = o.Center.Y }).Length();
                // Sticking with the current order avoids flip-flopping, unless it's a quiet point we already hold; a
                // fresh order more so, fading over two minutes. (Squads were re-tasked every minute or two in long
                // matches, often straight back to what they'd left 20-40 s before, because an enemy or two turned up
                // near one of our points and then dropped out of mind: the orders at the top of the screen kept
                // changing and the squad walked back and forth.)
                bool stick = sq.Site == o.Site && value[i] > 0f;
                float commit = stick ? 1.5f + 4f * MathF.Max(0f, 1f - (float)((Clock.Now - sq.OrderSince) / 120.0)) : 0f;
                // Spread over different points; but a defended one, or a headquarters, gets more than one squad: one
                // squad against a held position just trades men with it. (Two rifle squads always went to two
                // different places, and every front was a six-against-six duel nobody could win.)
                float crowd = known[i].Count >= 3 || i >= n ? 1f : 3f;
                float score = value[i] - d / (220f * MathF.Sqrt(Map.SizeScale)) - assigned[i] * crowd + commit;
                if (score <= bestScore) continue;
                bestScore = score;
                best = i;
            }
            if (best < 0) continue;
            assigned[best]++;
            var bo = Obj(best);
            bool changed = sq.Site != bo.Site;
            sq.Order(bo.Site, bo, OwnerOf(best) == team);
            if (!changed) continue;
            if (sq.Leader is Bot lead) Comms.Say(lead, $"{sq.Name}, {sq.OrderText.ToLowerInvariant()}!");
            if (sq == PlayerSquad) _hud.Center($"Squad orders: {sq.OrderText}", 4f);
            Log($"[{Clock.Now:0}s] {sq.Name}: {sq.OrderText}");
        }
        Support(team, value, known);
    }

    /// <summary>A point's index, or n+t for team t's headquarters, or -1.</summary>
    int IndexOf(Site? s)
    {
        if (s == null) return -1;
        int i = Array.FindIndex(Points, o => o.Site == s);
        if (i >= 0) return i;
        for (int t = 0; t < 3; t++) if (HQ[t].Site == s) return Points.Length + t;
        return -1;
    }

    /// <summary>Is side a weaker than side b: less ground, or as much and fewer tickets.</summary>
    bool Weaker(int a, int b) => Owned(a) < Owned(b) || (Owned(a) == Owned(b) && Tickets[a] < Tickets[b]);

    /// <summary>
    /// Orders for everyone who isn't a rifle squad, all of it in service of the rifle
    /// squads' attacks:
    /// - weapons teams set up on high ground 110-230 m from an attacked point and pin it;
    /// - recon teams watch an enemy point from further out and report what they see;
    /// - engineers dig in on the owned point nearest the enemy;
    /// - logistics follow whichever squad is shortest of ammo and bandages.
    /// </summary>
    void Support(int team, float[] value, HashSet<ICombatant>[] known)
    {
        var rifle = Squads[team].Where(s => s.Kind == SquadKind.Rifle).ToList();
        var attacks = rifle.Where(s => s.Site != null && !s.Defend).Select(s => IndexOf(s.Site)).Where(i => i >= 0).Distinct().ToList();
        var watched = new HashSet<int>();
        // How many supporting squads each attack already has: spread them over the attacks, don't pile on one.
        var backing = new Dictionary<int, int>();
        int LeastBacked(IEnumerable<int> pool, Func<int, float> dist) =>
            pool.OrderBy(i => backing.GetValueOrDefault(i) * 400f + dist(i)).First();
        void Back(int i) => backing[i] = backing.GetValueOrDefault(i) + 1;
        foreach (var sq in Squads[team])
        {
            if (sq.Kind == SquadKind.Rifle || sq.PlayerOrderUntil > Clock.Now) continue;
            var origin = sq.Position ?? Map.Bases[team];
            float Dist(int i) => (Obj(i).Center - origin with { Y = Obj(i).Center.Y }).Length();
            switch (sq.Kind)
            {
                case SquadKind.Weapons:
                case SquadKind.Recon:
                {
                    bool recon = sq.Kind == SquadKind.Recon;
                    // Weapons back the nearest attack; recon looks ahead at enemy ground, not where a weapons team already is.
                    var pool = recon
                        ? Enumerable.Range(0, Points.Length).Where(i => Owner[i] != team && !watched.Contains(i)).ToList()
                        : attacks.Where(i => !watched.Contains(i)).ToList();
                    if (pool.Count == 0) pool = Enumerable.Range(0, Points.Length).Where(i => Owner[i] != team).ToList();
                    if (pool.Count == 0) pool = Enumerable.Range(0, Points.Length).Where(i => known[i].Count > 0).ToList();
                    if (pool.Count == 0) continue;
                    int cur = IndexOf(sq.Site);
                    // Once set up, stay on the same target while it's still worth watching: moving a post costs minutes.
                    int target = cur >= 0 && (pool.Contains(cur) || Owner[cur] != team) ? cur
                        : pool.OrderBy(i => Dist(i) - (recon && Owner[i] >= 0 && Owner[i] != team ? 100f : 0f)).First();
                    watched.Add(target);
                    Back(target);
                    var site = Points[target].Site;
                    if (!_posts.TryGetValue(sq, out var post) || post.Site != site)
                    {
                        post = (site, FindPost(team, Points[target].Center, recon ? 170f : 110f, recon ? 320f : 230f));
                        _posts[sq] = post;
                    }
                    bool changed = sq.Site != site;
                    sq.Order(site, new PointObjective { Center = post.Post, Watch = Points[target].Center }, false, recon ? "Observe" : "Overwatch");
                    if (changed) Log($"[{Clock.Now:0}s] {sq.Name} ({sq.KindName}): {sq.OrderText} from {post.Post.DistanceTo(Points[target].Center):0} m");
                    break;
                }
                case SquadKind.Engineer:
                {
                    // The owned point closest to trouble; with nothing owned, go with the main attack.
                    var mine = Enumerable.Range(0, Points.Length).Where(i => Owner[i] == team).ToList();
                    int target;
                    if (mine.Count > 0)
                        target = mine.OrderBy(i => FrontDistance(team, i) / 100f + Dist(i) / 400f - (known[i].Count > 0 ? 2f : 0f) - (i == IndexOf(sq.Site) ? 1f : 0f)).First();
                    else if (attacks.Count > 0) target = attacks.OrderBy(Dist).First();
                    else continue;
                    var p = Obj(target);
                    var axis = Vector3.Zero;
                    int front = Enumerable.Range(0, Points.Length).Where(i => Owner[i] != team).OrderBy(i => Points[i].Center.DistanceTo(p.Center)).DefaultIfEmpty(-1).First();
                    if (front >= 0) axis = (Points[front].Center - p.Center) with { Y = 0f };
                    sq.ThreatAxis = axis.LengthSquared() > 1f ? axis.Normalized() : Vector3.Zero;
                    bool changed = sq.Site != p.Site;
                    sq.Order(p.Site, p, OwnerOf(target) == team, OwnerOf(target) == team ? "Fortify" : "Attack");
                    if (changed) Log($"[{Clock.Now:0}s] {sq.Name} ({sq.KindName}): {sq.OrderText}");
                    break;
                }
                case SquadKind.Armor:
                {
                    // Support the main attack from a firing position; go for enemy armour that's been reported near it.
                    if (sq.Vehicle == null)
                    {
                        if (sq.Objective is not PointObjective { Watch: var w0 } || w0 != Map.Bases[team])
                            sq.Order(null, new PointObjective { Center = Map.Bases[team], Watch = Map.Bases[team] }, false, "Wait for", "a vehicle");
                        break;
                    }
                    bool light = sq.Vehicle.Def.Kind == VKind.LTV;
                    // Who to work with. A carrier (IFV/APC) takes a rifle squad with a long way to go:
                    // it's their ride and then their fire support. The tank and the SPAA go with the
                    // main attack. Stay with them while they last.
                    bool carrier = MotorPool.Carrier(sq.Vehicle);
                    var cur0 = sq.Supports;
                    bool keep = cur0 != null && cur0.Alive >= 2 && cur0.Objective != null && rifle.Contains(cur0);
                    if (NoCombinedArms) { sq.Supports = null; keep = true; }
                    if (!light && !keep)
                    {
                        var taken = Squads[team].Where(o => o != sq && o.Kind == SquadKind.Armor && o.Supports != null && o.Vehicle is { } ov && MotorPool.Carrier(ov) == carrier).Select(o => o.Supports!).ToHashSet();
                        Squad? pick = null;
                        float bestS = float.MinValue;
                        foreach (var r in rifle)
                        {
                            if (r.Alive < 3 || r.Objective == null || taken.Contains(r) || r.Leader is not ICombatant rl) continue;
                            float toGo = ((r.Objective.Center - rl.FeetPos) with { Y = 0f }).Length();
                            float fromUs = rl.FeetPos.DistanceTo(sq.Vehicle.GlobalPosition);
                            float score = carrier ? toGo - fromUs * 0.5f
                                : (r.Defend ? -300f : 0f) + (attacks.Contains(IndexOf(r.Site)) ? 150f : 0f) - fromUs * 0.3f;
                            if (score > bestS) { bestS = score; pick = r; }
                        }
                        if (pick != cur0 && pick != null) Log($"[{Clock.Now:0}s] {sq.Name} ({sq.Vehicle.Def.Name}): {(carrier ? "mechanised with" : "working with")} {pick.Name}");
                        sq.Supports = pick;
                    }
                    if (!light && sq.Supports is { Objective: { } supObj } sup)
                    {
                        if (sq.Site != sup.Site || sq.Objective is not PointObjective spo || spo.Watch.DistanceTo(supObj.Center) > 20f)
                            sq.Order(sup.Site, new PointObjective { Center = supObj.Center, Watch = supObj.Center }, false,
                                     carrier ? "Mechanised with" : sq.Vehicle.Def.Kind == VKind.SPAA ? "Air defence for" : "Supporting", sup.Name);
                        break;
                    }
                    var armor = Radio.Latest(team, RadioKind.Armor, 60.0);
                    int cur = IndexOf(sq.Site);
                    int target = attacks.Count == 0 ? Enumerable.Range(0, Points.Length).OrderBy(Dist).First()
                               : attacks.Contains(cur) && backing.GetValueOrDefault(cur) == 0 ? cur
                               : LeastBacked(attacks, Dist);
                    Back(target);
                    var watch = !light && armor != null && armor.Pos.DistanceTo(Obj(target).Center) < 500f ? armor.Pos : Obj(target).Center;
                    // Shoot and scoot: a new position every couple of minutes, or when hit.
                    bool stale = !_posts.TryGetValue(sq, out var post) || post.Site != Obj(target).Site || Clock.Now - sq.OrderSince > 120.0 || Clock.Now - sq.Vehicle.LastHit < 3.0;
                    if (stale)
                    {
                        // The vehicle picks its own firing position (hull-down, reachable) around what it's watching.
                        post = (Obj(target).Site, watch);
                        _posts[sq] = post;
                        sq.Order(Obj(target).Site, new PointObjective { Center = watch, Watch = watch }, false, light ? "Scout" : "Overwatch", Obj(target).Site.Name);
                        Log($"[{Clock.Now:0}s] {sq.Name} ({sq.Vehicle.Def.Name}): {sq.OrderText}");
                    }
                    break;
                }
                case SquadKind.AntiTank:
                {
                    // Hunt reported armour: an ambush position covering where it was seen.
                    var armor = Radio.Latest(team, RadioKind.Armor, 90.0);
                    if (armor != null)
                    {
                        if (sq.Objective is not PointObjective po || po.Watch.DistanceTo(armor.Pos) > 80f)
                        {
                            var ambush = FindPost(team, armor.Pos, 90f, 200f);
                            sq.Order(null, new PointObjective { Center = ambush, Watch = armor.Pos }, false, "Hunt", armor.Vehicle?.Def.ClassName ?? "armour");
                            Log($"[{Clock.Now:0}s] {sq.Name} (AT): {sq.OrderText} reported by {armor.From}");
                        }
                        break;
                    }
                    if (attacks.Count == 0) break;
                    int t2 = LeastBacked(attacks, Dist);
                    Back(t2);
                    if (sq.Site != Obj(t2).Site)
                        sq.Order(Obj(t2).Site, new PointObjective { Center = FindPost(team, Obj(t2).Center, 90f, 170f), Watch = Obj(t2).Center }, false, "Cover", Obj(t2).Site.Name);
                    break;
                }
                case SquadKind.Air when sq.Vehicle?.Def.Kind == VKind.AH:
                {
                    // Close air support over the main attack, or hunting reported armour.
                    var armor = Radio.Latest(team, RadioKind.Armor, 60.0);
                    int t4 = attacks.Count > 0 ? LeastBacked(attacks, Dist) : Enumerable.Range(0, Points.Length).OrderBy(Dist).First();
                    var watch = armor != null ? armor.Pos : Obj(t4).Center;
                    if (sq.Objective is not PointObjective pa || pa.Watch.DistanceTo(watch) > 200f)
                    {
                        sq.Order(Obj(t4).Site, new PointObjective { Center = watch, Watch = watch }, false, "Air support", armor != null ? "vs armour" : Obj(t4).Site.Name);
                        Log($"[{Clock.Now:0}s] {sq.Name} (gunship): {sq.OrderText}");
                    }
                    break;
                }
                case SquadKind.Air when sq.Vehicle == null:
                    // Shot down (or written off): back to base, where the replacement will be. (Aircrew used to keep
                    // their last orders, and a gunship's crew walked on into the fight they had been flying over.)
                    if (sq.Objective is not PointObjective { Watch: var wa } || wa != Map.Bases[team])
                        sq.Order(null, new PointObjective { Center = Map.Bases[team], Watch = Map.Bases[team] }, false, "Wait for", "an aircraft");
                    break;
                case SquadKind.Air:
                case SquadKind.Mortar:
                    // The transport helicopter's jobs come from the motor pool; the mortar team mans its tube.
                    if (sq.Vehicle != null && (sq.Objective is not PointObjective pm || pm.Center.DistanceTo(sq.Vehicle.GlobalPosition) > 10f))
                        sq.Order(null, new PointObjective { Center = sq.Vehicle.GlobalPosition, Watch = sq.Vehicle.GlobalPosition }, false, sq.Kind == SquadKind.Mortar ? "Man" : "Fly", sq.Vehicle.Def.Name);
                    break;
                case SquadKind.Drone:
                {
                    // Well back and out of sight (350-700 m from the target): the drones do the looking.
                    // Setting up takes time: stay on the current job while it's still one (an attack, or the front).
                    int td = _posts.TryGetValue(sq, out var cur) ? IndexOf(cur.Site) : -1;
                    if (td >= 0 && !(attacks.Contains(td) || OnFront(team, td))) td = -1;
                    if (td < 0)
                        td = attacks.Count > 0 ? LeastBacked(attacks, Dist)
                            : Enumerable.Range(0, Points.Length).Where(i => OnFront(team, i)).DefaultIfEmpty(-1).OrderBy(i => i < 0 ? 0f : Dist(i)).First();
                    if (td < 0) td = Enumerable.Range(0, Points.Length).OrderBy(Dist).First();
                    Back(td);
                    var site = Obj(td).Site;
                    if (!_posts.TryGetValue(sq, out var post) || post.Site != site)
                    {
                        post = (site, FindPost(team, Obj(td).Center, 350f, 700f));
                        _posts[sq] = post;
                        sq.Order(site, new PointObjective { Center = post.Post, Watch = Obj(td).Center }, false, "Drone ops over", site.Name);
                        Log($"[{Clock.Now:0}s] {sq.Name} (drones): {sq.OrderText} from {post.Post.DistanceTo(Obj(td).Center):0} m");
                    }
                    break;
                }
                case SquadKind.Transport:
                    if (sq.Objective == null) sq.Order(null, new PointObjective { Center = Map.Bases[team], Watch = Map.Bases[team] }, false, "Transport", "duty");
                    break;
                case SquadKind.Logistics:
                {
                    // With a truck full of supplies and fewer than two FOBs: put one down behind the main attack.
                    var slot = Motor.Slots.FirstOrDefault(s => s.Team == team && s.Kind == VKind.Logistics);
                    int fobs = Fob.All.Count(f => f.Team == team);
                    if (slot?.Live != null && slot.HasSupplies && fobs < 2 && sq.FobSite == null && attacks.Count > 0)
                    {
                        int t3 = attacks.OrderBy(Dist).First();
                        var site = FindFobSite(team, Obj(t3).Center);
                        if (Fob.All.All(f => f.Team != team || f.GlobalPosition.DistanceTo(site) > 250f))
                        {
                            sq.FobSite = site;
                            sq.Order(Obj(t3).Site, new PointObjective { Center = site, Watch = Obj(t3).Center }, false, "Build FOB near", Obj(t3).Site.Name);
                            Log($"[{Clock.Now:0}s] {sq.Name}: {sq.OrderText}");
                        }
                    }
                    if (sq.FobSite != null) break;
                    // Whoever needs it most: short of ammo, or hurt.
                    Squad? neediest = null;
                    float most = -1f;
                    foreach (var other in Squads[team])
                    {
                        if (other == sq || other.Kind == SquadKind.Logistics || other.Alive == 0) continue;
                        var alive = other.Members.Where(m => m.Alive && IsInstanceValid((GodotObject)m)).ToList();
                        float need = alive.Average(m => (1f - m.AmmoLevel) + (100f - m.Hp) / 100f) + (other.Kind == SquadKind.Rifle ? 0.1f : 0f);
                        if (need <= most) continue;
                        most = need;
                        neediest = other;
                    }
                    if (neediest == null) continue;
                    if (sq.Objective is FollowObjective fo && fo.Target == neediest) continue;
                    sq.Order(neediest.Site, new FollowObjective { Target = neediest }, false, "Resupply", neediest.Name);
                    Log($"[{Clock.Now:0}s] {sq.Name} ({sq.KindName}): {sq.OrderText}");
                    break;
                }
            }
        }
    }

    /// <summary>How far point i is from the nearest point this side doesn't own: small = front line.</summary>
    float FrontDistance(int team, int i)
    {
        float best = 2000f;
        for (int j = 0; j < Points.Length; j++)
            if (Owner[j] != team) best = MathF.Min(best, Points[j].Center.DistanceTo(Points[i].Center));
        return best;
    }

    /// <summary>A FOB site: 170-260 m short of the target on our side, out of its sight if possible.</summary>
    Vector3 FindFobSite(int team, Vector3 target)
    {
        var home = (Map.Bases[team] - target) with { Y = 0f };
        float baseAng = MathF.Atan2(home.Z, home.X);
        var space = Map.GetWorld3D().DirectSpaceState;
        Vector3 best = target + home.Normalized() * 200f;
        float bestScore = float.MinValue;
        for (int k = 0; k < 24; k++)
        {
            float ang = baseAng + _rng.RandfRange(-0.8f, 0.8f), r = _rng.RandfRange(170f, 260f);
            var p = target + new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * r;
            if (MathF.Abs(p.X) > Map.Half - 60f || MathF.Abs(p.Z) > Map.Half - 60f) continue;
            p.Y = Map.HeightAt(p.X, p.Z);
            if (Points.Any(o => (o.Center - p with { Y = o.Center.Y }).Length() < o.Radius + 25f)) continue;
            if (Map.City?.Zone(p.X, p.Z) == 2) continue; // not in the middle of a street
            if (Map.NormalAt(p.X, p.Z).Y < 0.9f) continue; // somewhere flat
            bool seen = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 2f, target + Vector3.Up * 2f, Layers.Solid)).Count == 0;
            float score = (seen ? 0f : 30f) - MathF.Abs(r - 210f) * 0.05f + _rng.RandfRange(0f, 5f);
            if (score <= bestScore) continue;
            bestScore = score;
            best = p;
        }
        return Map.Ground(best);
    }

    /// <summary>
    /// An observation or overwatch post: on our side of the target, between min and
    /// max metres out, as high as possible, and with an actual line of sight onto it.
    /// </summary>
    Vector3 FindPost(int team, Vector3 target, float min, float max)
    {
        var home = (Map.Bases[team] - target) with { Y = 0f };
        float baseAng = MathF.Atan2(home.Z, home.X);
        var space = Map.GetWorld3D().DirectSpaceState;
        Vector3 best = target + home.Normalized() * min;
        float bestScore = float.MinValue;
        for (int k = 0; k < 32; k++)
        {
            float ang = baseAng + _rng.RandfRange(-1.4f, 1.4f), r = _rng.RandfRange(min, max);
            var p = target + new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * r;
            if (MathF.Abs(p.X) > Map.Half - 60f || MathF.Abs(p.Z) > Map.Half - 60f) continue;
            p.Y = Map.HeightAt(p.X, p.Z);
            // Not sitting on another settlement.
            if (Points.Any(o => (o.Center - p with { Y = o.Center.Y }).Length() < o.Radius + 20f)) continue;
            var eye = p + Vector3.Up * 1.2f;
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, target + Vector3.Up * 2f, Layers.Solid));
            bool los = hit.Count == 0 || hit["position"].AsVector3().DistanceTo(target) < 35f;
            float score = (p.Y - target.Y) * 0.5f + (los ? 30f : 0f) - MathF.Abs(r - (min + max) * 0.5f) * 0.03f + _rng.RandfRange(0f, 4f);
            if (score <= bestScore) continue;
            bestScore = score;
            best = p;
        }
        // In town: an upper window or a rooftop that looks onto it beats anything at street level.
        Vector3? perch = null;
        var toHome = home.Normalized();
        int looked = 0;
        foreach (int i in Enumerable.Range(0, Map.Perches.Count).OrderBy(_ => _rng.Randi()))
        {
            if (looked >= 40) break;
            var pc = Map.Perches[i];
            if (pc.Floor == 0) continue;
            var off = (pc.Pos - target) with { Y = 0f };
            float r = off.Length();
            if (r < min || r > max || off.Normalized().Dot(toHome) < -0.2f) continue;
            if (pc.Out.Dot(-off.Normalized()) < 0.5f) continue; // must face the target
            looked++;
            var eye = pc.Pos + Vector3.Up * 1.5f + pc.Out * 0.9f;
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, target + Vector3.Up * 2f, Layers.Solid));
            if (hit.Count > 0 && hit["position"].AsVector3().DistanceTo(target) > 35f) continue;
            float score = (pc.Pos.Y - target.Y) * 0.5f + 30f + (pc.Roof ? 4f : 6f) + _rng.RandfRange(0f, 4f);
            if (score <= bestScore) continue;
            bestScore = score;
            perch = pc.Pos;
        }
        return perch ?? Map.Ground(best);
    }

    /// <summary>The player ordered their squad on the map.</summary>
    public void PlayerOrder(int point)
    {
        if (PlayerSquad == null) return;
        if (!PlayerLeads) { _hud.Center("Only the squad leader gives orders", 2f); return; }
        PlayerSquad.PlayerOrderUntil = Clock.Now + 300.0;
        PlayerSquad.Order(Points[point].Site, Points[point], Owner[point] == 0);
        _hud.Center($"Squad orders: {PlayerSquad.OrderText}" + (Owner[point] != 0 && !CanTake(0, point) ? "\n(behind their lines: it can't be taken until a linked point is ours)" : ""), 3f);
        if (PlayerSquad.Leader is Bot lead) Comms.Say(lead, "Copy that, moving.");
    }

    /// <summary>Where the player is aiming, out to ~900 m (null: at the sky).</summary>
    /// <summary>
    /// The squad goes back to its own leader's judgement when you go down or are killed: your last order doesn't hold it
    /// for the rest of its five minutes. (It did, even through a regroup, with the squad down to two men.)
    /// </summary>
    void ReleasePlayerOrder()
    {
        if (PlayerSquad == null || PlayerSquad.PlayerOrderUntil <= Clock.Now) return;
        PlayerSquad.PlayerOrderUntil = -1;
        _commandAt[0] = Math.Min(_commandAt[0], Clock.Now + 2.0);
    }

    Vector3? PlayerAimPoint()
    {
        if (PlayerBody is not { Alive: true } p) return null;
        // Whatever you're looking through: your eyes, a vehicle's sight or camera, a drone's. (It was always the head
        // camera, which in a vehicle stays pointing wherever you looked as you got in.)
        var cam = p.GetViewport().GetCamera3D() ?? p.Cam;
        var from = cam.GlobalPosition;
        var to = from - cam.GlobalBasis.Z * 900f;
        var q = PhysicsRayQueryParameters3D.Create(from, to, Layers.World | Layers.Trees | Layers.Vehicles);
        if (p.Ride != null) q.Exclude = new Godot.Collections.Array<Rid> { p.Ride.GetRid() };
        var hit = p.GetWorld3D().DirectSpaceState.IntersectRay(q);
        return hit.Count > 0 ? hit["position"].AsVector3() : null;
    }

    /// <summary>The squad leader's command menu (N, then a number).</summary>
    public void SquadCommand(int k)
    {
        var sq = PlayerSquad;
        if (sq == null || !PlayerLeads) return;
        double now = Clock.Now;
        var aim = PlayerAimPoint();
        Bot? Voice() => sq.Members.OfType<Bot>().Where(b => b.Alive).OrderBy(b => b.FeetPos.DistanceTo(PlayerBody!.FeetPos)).FirstOrDefault();
        bool NeedAim() { if (aim != null) return true; _hud.Center("Aim at the ground or a building", 1.5f); return false; }
        switch (k)
        {
            case 0:
            case 1:
            {
                if (!NeedAim()) return;
                var at = Map.Ground(aim!.Value);
                sq.FollowPlayer = false;
                sq.PlayerOrderUntil = now + 300.0;
                sq.Order(null, new PointObjective { Center = at, Watch = at + (at - PlayerBody!.FeetPos) with { Y = 0f } }, k == 1, k == 1 ? "Hold" : "Move to", $"{Comms.Bearing(PlayerBody.FeetPos, at)}, {PlayerBody.FeetPos.DistanceTo(at):0} m");
                foreach (var m in sq.Members) if (m is Bot { Alive: true } b) b.Brain.ObjectiveChanged();
                _hud.Center($"Squad: {sq.OrderText}", 2f);
                if (Voice() is { } v0) Comms.Say(v0, k == 1 ? "Holding there." : "Moving.");
                break;
            }
            case 2: ToggleFollow(); break;
            case 3:
                if (!NeedAim()) return;
                sq.SuppressAt = aim!.Value;
                sq.SuppressUntil = now + 15.0;
                _hud.Center("Squad: suppress!", 1.5f);
                if (Voice() is { } v1) Comms.Say(v1, "Suppressing!");
                break;
            case 4:
            {
                if (!NeedAim()) return;
                var thrower = sq.Members.OfType<Bot>().Where(b => b.Alive && b.Ride == null && b.SmokeGrenades > 0 && b.FeetPos.DistanceTo(aim!.Value) < 40f)
                                .OrderBy(b => b.FeetPos.DistanceTo(aim!.Value)).FirstOrDefault();
                if (thrower != null && thrower.ThrowGrenadeAt(aim!.Value, smoke: true)) { Comms.Say(thrower, "Smoke out!"); _hud.Center("Smoke!", 1.5f); }
                else _hud.Center("Nobody with smoke is within a throw (40 m) of there", 2f);
                break;
            }
            case 5:
            {
                March?[] cycle = { null, March.Column, March.AssaultLine, March.Herringbone };
                int i = Array.IndexOf(cycle, sq.PlayerMarch);
                var next = cycle[(i + 1) % cycle.Length];
                sq.SetPlayerMarch(next);
                _hud.Center($"March order: {next switch { March.Column => "file (behind me, on my track)", March.AssaultLine => "on line (abreast of me)", March.Herringbone => "halt (spread both sides, face out)", _ => "auto (wedge / file by the ground)" }}", 2f);
                break;
            }
            case 6:
                if (!NeedAim()) return;
                if (!sq.Support.Any()) { _hud.Center("No vehicle is working with your squad", 2f); return; }
                sq.VehicleFireAt = aim!.Value;
                sq.VehicleFireUntil = now + 25.0;
                _hud.Center($"Fire mission: {string.Join(", ", sq.Support.Select(v => v.Def.Name))}", 2f);
                Comms.Say(PlayerBody!, $"{sq.Support.First().Def.ClassName}, fire mission, {Comms.Bearing(PlayerBody!.FeetPos, aim.Value)}, {PlayerBody.FeetPos.DistanceTo(aim.Value):0} meters!");
                break;
            case 7:
            {
                var carrier = sq.Support.FirstOrDefault(MotorPool.Carrier) ?? sq.Transport;
                if (carrier == null) { _hud.Center("No carrier or transport for your squad", 2f); return; }
                bool aboard = sq.Members.Any(m => m.Alive && m.Ride == carrier);
                if (aboard) { sq.WantDismount = true; _hud.Center("Dismount!", 1.5f); }
                else { sq.WantRide = true; _hud.Center($"Calling the {carrier.Def.Name} to pick us up", 2f); }
                break;
            }
        }
    }

    public void ToggleFollow()
    {
        if (PlayerSquad == null) return;
        if (!PlayerLeads) { _hud.Center("Only the squad leader gives orders", 2f); return; }
        PlayerSquad.FollowPlayer = !PlayerSquad.FollowPlayer;
        _hud.Center(PlayerSquad.FollowPlayer ? "Squad: on me" : "Squad: go work the objective", 3f);
        foreach (var m in PlayerSquad.Members)
            if (m is Bot { Alive: true } b && IsInstanceValid(b)) b.Brain.ObjectiveChanged();
    }

    // ================================================================ loop

    public int AliveOn(int team) => Combatants.All.Count(c => c.Team == team && c.Alive);
    public int Owned(int team)
    {
        int k = 0;
        foreach (int o in Owner) if (o == team) k++;
        return k;
    }

    public override void _Process(double delta)
    {
        double now = Clock.Now;
        Telemetry.Tick(this);

        if (now >= _nextReinforce)
        {
            _nextReinforce = now + 1.0;
            Reinforce(now);
        }

        if (PlayerBody != null && !PlayerBody.Alive && _playerDiedAt > 0)
        {
            if (!Spec.Active && now - _playerDiedAt > 2.0)
            {
                PlayerHud.P = null;
                // Watching your squad while you wait for it.
                Spec.Activate(Bots.FirstOrDefault(b => b.Alive && b.Squad == PlayerSquad) ?? Bots.FirstOrDefault(b => b.Alive && b.Team == 0) ?? Bots.FirstOrDefault(b => b.Alive));
            }
            if (now >= PlayerRespawnAt && Winner < 0 && !Out[0] && !Spent(0) && !SpawnPlayer()) PlayerRespawnAt = now + (PlayerWait != "" ? 1.0 : 5.0);
        }

        if (Winner >= 0) return;

        if (now >= _nextTick)
        {
            _nextTick = now + TickEvery;
            Capture((float)TickEvery);
        }
        Motor.Tick();
        if (now >= _nextBleed)
        {
            _nextBleed = now + BleedEvery;
            int[] owned = { Owned(0), Owned(1), Owned(2) };
            for (int t = 0; t < 3; t++)
            {
                if (Out[t]) continue;
                int lead = 0;
                for (int o = 0; o < 3; o++) if (o != t && !Out[o]) lead = Math.Max(lead, owned[o]);
                Spend(t, Math.Max(0, lead - owned[t]));
            }
        }
        for (int t = 0; t < 3; t++)
            if (now >= _commandAt[t])
            {
                _commandAt[t] = now + CommandEvery;
                Command(t);
            }
        CheckWin(now);

        if (DuelMode.Verbose && now >= _nextDump)
        {
            _nextDump = now + 60.0;
            Log($"[{now:0}s] tickets {Tickets[0]}/{Tickets[1]}/{Tickets[2]}  points {Owned(0)}/{Owned(1)}/{Owned(2)} " +
                $"(neutral {Owner.Count(o => o < 0)})  alive {AliveOn(0)}/{AliveOn(1)}/{AliveOn(2)}  " +
                $"states {string.Join(" ", Bots.Where(b => b.Alive).GroupBy(b => b.Brain.State).Select(g => $"{g.Key}:{g.Count()}"))}  " +
                $"ground {string.Join(" ", Bots.Where(b => b.Alive && b.Ride == null).GroupBy(b => b.Brain.Env).Select(g => $"{Surroundings.Name(g.Key)}:{g.Count()}"))}  " +
                $"upstairs {Bots.Count(b => b.Alive && b.Ride == null && b.FeetPos.Y - Map.HeightAt(b.FeetPos.X, b.FeetPos.Z) > 2.5f)}  " +
                $"prone {Bots.Count(b => b.Alive && b.Prone)}  dry {Bots.Count(b => b.Alive && b.Ammo == 0 && !b.Mags.Any)}  low {Bots.Count(b => b.Alive && b.Mags.Rounds <= b.Def.MagSize)}  " +
                $"nav iteration {NavigationServer3D.MapGetIterationId(Map.GetWorld3D().NavigationMap)}" +
                (NavigationServer3D.MapGetIterationId(Map.GetWorld3D().NavigationMap) > 0 ? $" base->{Map.Nav.ClosestPoint(Map.Bases[0]).DistanceTo(Map.Bases[0]):0} m" : ""));
            if (!_navLogged && Map.Nav.Finished && NavigationServer3D.MapGetIterationId(Map.GetWorld3D().NavigationMap) > 0)
            {
                _navLogged = true;
                Log($"     navmesh: {Map.Nav.Total} tiles ({Map.Nav.EmptyTiles} empty, {Map.Nav.Polygons} polygons, {Map.Nav.DensePolygons} in town; vehicles {Map.VehicleNav.Polygons}) in {Map.Nav.Seconds:0.0}s wall (map built in {Map.BuildSeconds:0.0}s)");
            }
            else if (_navLogged && !_upChecked)
            {
                _upChecked = true;
                // Can people get off the points? Each site's building points and spots where men come in, checked
                // for being cut off (a route toward somewhere 80 m away that can't get 15 m from there).
                {
                    var nm0 = Map.GetWorld3D().NavigationMap;
                    int pts = 0, isl = 0, sp = 0, spIsl = 0;
                    bool Island(Vector3 at)
                    {
                        var q0 = Map.Nav.ClosestPoint(at);
                        var path0 = NavBaker.Path(nm0, q0, Map.Ground(q0 + new Vector3(80f, 0f, 0f)));
                        return path0.Length == 0 || path0[^1].DistanceTo(q0) < 15f;
                    }
                    foreach (var pt in Points)
                    {
                        foreach (var sp0 in pt.Site.Points)
                        {
                            pts++;
                            if (Island(sp0)) isl++;
                        }
                        for (int k = 0; k < 10; k++)
                        {
                            sp++;
                            if (Island(SpawnAt((pt.Site.Name, pt.Center, Array.IndexOf(Points, pt)), Math.Max(0, Owner[Array.IndexOf(Points, pt)])))) spIsl++;
                        }
                    }
                    Log($"     cut-off check: {isl}/{pts} building points and {spIsl}/{sp} spawn spots cut off ({Builder.Doorways.Count} doorways linked)");
                }
                // Can people actually get upstairs? Path from the street to upper-floor windows and rooftops.
                var nm = Map.GetWorld3D().NavigationMap;
                int tried = 0, onMesh = 0, reached = 0;
                var byFloor = new Dictionary<int, (int, int)>();
                foreach (var pc in Map.Perches.Where(p => p.Floor >= 1).OrderBy(_ => _rng.Randi()).Take(40))
                {
                    tried++;
                    var near = Map.Nav.ClosestPoint(pc.Pos);
                    bool on = near.DistanceTo(pc.Pos) < 0.8f;
                    if (on) onMesh++;
                    var street = Map.Ground(Map.Bases[0]);
                    var q = new NavigationPathQueryParameters3D { Map = nm, StartPosition = street, TargetPosition = pc.Pos, PathSearchMaxPolygons = 200000 };
                    var qr = new NavigationPathQueryResult3D();
                    NavigationServer3D.QueryPath(q, qr);
                    var path = qr.Path;
                    bool ok = path.Length > 0 && path[^1].DistanceTo(pc.Pos) < 1f;
                    if (ok) reached++;
                    var f = byFloor.GetValueOrDefault(pc.Floor);
                    byFloor[pc.Floor] = (f.Item1 + 1, f.Item2 + (ok ? 1 : 0));
                    if (!ok && tried - reached <= 3) Log($"       unreachable perch floor {pc.Floor} roof {pc.Roof} at {pc.Pos}: closest mesh {near} ({near.DistanceTo(pc.Pos):0.0} m), path end {(path.Length > 0 ? path[^1].ToString() : "none")}");
                }
                if (tried > 0) Log($"     upstairs check: {onMesh}/{tried} perches on the navmesh, {reached}/{tried} reachable from the street; by floor " + string.Join(" ", byFloor.OrderBy(k => k.Key).Select(k => $"{k.Key}:{k.Value.Item2}/{k.Value.Item1}")));
            }
            Log("     points: " + string.Join("  ", Points.Select((p, i) => $"{p.Site.Name}={(Owner[i] < 0 ? "-" : KothMode.TeamNames[Owner[i]][..1])}{Progress[i] * 100:0}[{Inside[i, 0]}/{Inside[i, 1]}/{Inside[i, 2]}]"
                + (Front ? "{" + string.Concat(Enumerable.Range(0, 3).Where(t => CanTake(t, i)).Select(t => KothMode.TeamNames[t][..1])) + "}" : ""))));
            foreach (var ms in Motor.Slots)
            {
                if (ms.Live is not { Destroyed: false } mv || ms.Kind is VKind.Transport or VKind.Logistics or VKind.UH or VKind.AH or VKind.Mortar) continue;
                var gun = mv.Occupants.OfType<Bot>().FirstOrDefault(o => o.SeatIdx == mv.GunnerSeat);
                Log($"     {mv.Def.Name} ({KothMode.TeamNames[ms.Team]}): {(ms.Rearming ? "rearming" : ms.FiringWhy)}{(ms.Mech is 1 ? ", picking up" : ms.Mech is 2 ? ", carrying" : "")} with {ms.Crew?.Supports?.Name ?? "-"}, "
                    + $"{(mv.Goal is Vector3 g ? ((g - mv.GlobalPosition) with { Y = 0f }).Length() : 0f):0} m to go, {mv.Hp / mv.Def.Hp:P0}, {mv.Speed * 3.6f:0} km/h thr {mv.Throttle:0.0} path {mv.Drive.Idx}/{mv.Drive.Path.Length}{(mv.Boarding ? " BOARDING" : "")}, gunner: {gun?.Crew.Note ?? "none"}{(mv.Drive.Note != "" ? $", driver: {mv.Drive.Note}" : "")}");
            }
            foreach (var sq in Squads.SelectMany(l => l).Where(s => s.Kind == SquadKind.Drone))
                foreach (var m in sq.Members.OfType<Bot>().Where(m => m.Alive && m.Ops != null))
                    Log($"     {sq.Name} {m.Callsign}: {m.Brain.State} ({m.Brain.Note}) stock {m.Ops!.Quads}q/{m.Ops.Bombs}b/{m.Ops.Fpvs}f"
                        + (m.Ops.Quad is { Dead: false } q ? $", quad {q.GlobalPosition.DistanceTo(m.FeetPos):0} m out, {q.Bombs} bombs, batt {q.Battery:P0}, sees {q.Seen.Count(kv => Clock.Now - kv.Value < 3)}" : "")
                        + (m.Ops.Fpv is { Dead: false } f ? $", FPV {f.GlobalPosition.DistanceTo(f.TargetPoint()):0} m to target" : ""));
            Log($"     stuck events {Bot.StuckEvents} (near a vehicle {Bot.StuckNearVehicle}, boarding {Bot.StuckBoarding}, ride coming {Bot.StuckWaiting}), squad engagements {Squad.Engagements}, assaults {Squad.Assaults}, armour hunts {Squad.Hunts}, mech rides {MotorPool.Mounts} (dismounts {MotorPool.Dismounts}, under fire {MotorPool.ContactDismounts}), fire requests {MotorPool.FireRequests}, area rounds {CrewBrain.AreaRounds}, held for friendlies {CrewBrain.HeldForFriendlies}, smoke {SmokeScreen.Pops} (vehicle {MotorPool.SmokePops}), scoots {MotorPool.Scoots}, relocations {MotorPool.Relocations}, rearms {MotorPool.Rearms}, drones {Drone.Launched} up, {Drone.Spots} spots, {Drone.BombsDropped} bombs, {Drone.FpvStrikes} FPV hits, {Drone.ShotDown} shot down, danger areas {Squad.Crossings} ({Squad.CrossingsDone} crossed, {Squad.CrossingsAborted} abandoned, {Squad.SmokeCrossings} under smoke), ORPs {Squad.Orps}, deployed {Squad.Deploys}, buddy swaps {Squad.BuddySwaps}, marching {string.Join(" ", Squads.SelectMany(l => l).Where(s => s.Kind == SquadKind.Rifle && s.Alive > 0).GroupBy(s => s.MarchOrder).Select(g => $"{g.Key}:{g.Count()}"))}, drills {Squad.Drills} (flank {Squad.Flanks}, break {Squad.Breaks}, indirect {Squad.Indirects}, consolidate {Squad.Consolidations}), gunners vs infantry {CrewBrain.InfantryTargets} picks/{CrewBrain.InfantryShots} shots (vs armour {CrewBrain.ArmorShots}), defending {Squads.Sum(l => l.Count(s => s.Kind == SquadKind.Rifle && s.Defend))}, kills so far {_kills} (downs {_downs}, down now {Combatants.All.Count(c => c.Downed)}), bounds {BotBrain.Bounds}, hunts {BotBrain.Hunts}, to-cover {BotBrain.Covers}; " +
                $"medevac'd {Motor.Evacuated}, air assaults {Motor.AirAssaults}, mortar rounds {CrewBrain.MortarRounds} ({CrewBrain.MortarLoaded} with a loader), vehicles {Vehicle.All.Count(v => !v.Destroyed)} live / {Vehicle.All.Count(v => v.Destroyed)} wrecks, FOBs {Fob.All.Count}, rockets {BotBrain.Rockets}, " +
                $"heals {BotBrain.Heals} (revives {BotBrain.Revives}, self-aid {BotBrain.SelfAids}), resupplies {BotBrain.Resupplies}, sandbags {BotBrain.Builds}, 40mm {BotBrain.Launches}, intel {Intel.Count}");
            foreach (var s in Motor.Slots)
                if (s.Live is { Destroyed: false } lv)
                    Log($"     {KothMode.TeamNames[s.Team][..1]} {lv.Def.Name,-18} {lv.Status(),-14} crew {lv.Occupants.Count(o => o != null)}/{lv.Occupants.Length} " +
                        $"speed {MathF.Abs(lv.Speed):0.0} goal {(lv.Goal is Vector3 g ? $"{lv.GlobalPosition.DistanceTo(g):0} m" : "-")} job {s.Job} stuck {lv.Drive.Stucks}{(lv.Drive.Note != "" ? $" ({lv.Drive.Note})" : "")}" +
                        (lv.GunnerSeat >= 0 && lv.Occupants[lv.GunnerSeat] is Bot gb ? $" · {gb.Crew.Note}" : ""));
            foreach (var sqs in Squads)
                Log("     " + string.Join("  ", sqs.Select(s => $"{s.Name}[{s.Kind}]: {s.OrderText} ({s.Alive} alive, {SquadSpread(s):0} m spread)")));
            // Men in Advance who haven't moved 5 m since an earlier report: meant to be going somewhere, and not.
            foreach (var b in Bots)
            {
                if (!IsInstanceValid(b) || !b.Alive || b.Ride != null) { _stillSince.Remove(b); continue; }
                bool adv = b.Brain.State == BotState.Advance;
                if (!_stillSince.TryGetValue(b, out var was) || !adv || !was.Adv || ((b.FeetPos - was.Pos) with { Y = 0f }).Length() > 5f)
                    _stillSince[b] = (b.FeetPos, adv, Clock.Now);
                else if (Clock.Now - was.Since > 100.0)
                    Log($"     still {Clock.Now - was.Since:0}s: {b.Squad?.Name} {b.Callsign}{(b.Squad?.Leader == b ? " (SL)" : "")} ({b.Brain.Note}) {b.Brain.MoveDebug}; {b.PathDebug}");
            }
        }
    }

    readonly Dictionary<Bot, (Vector3 Pos, bool Adv, double Since)> _stillSince = new();

    static float SquadSpread(Squad s)
    {
        var l = s.Leader;
        if (l == null) return 0f;
        float m = 0f;
        foreach (var c in s.Members) if (c.Alive) m = MathF.Max(m, c.FeetPos.DistanceTo(l.FeetPos));
        return m;
    }

    void Spend(int team, int n)
    {
        if (n <= 0 || Out[team] || Tickets[team] <= 0) return;
        Tickets[team] = Math.Max(0, Tickets[team] - n);
        if (Tickets[team] > 0) return;
        // No reserves left: nobody else comes, and it falls back on its headquarters for a last stand, open to anyone.
        // It's out when that's overrun, or when there's nobody left. (It used to be out on the spot, its men
        // simply stopping where they stood, and no headquarters was ever taken.)
        Announce(team, -1, $"{KothMode.TeamNames[team]} is out of reserves: falling back on its headquarters");
        for (int i = _respawns.Count - 1; i >= 0; i--)
            if (_roster[_respawns[i].P].Team == team) _respawns.RemoveAt(i);
        for (int t = 0; t < 3; t++) _commandAt[t] = Math.Min(_commandAt[t], Clock.Now + 2.0);
    }

    /// <summary>
    /// A side is out: nobody else comes, no more vehicles. Its ground goes back to no one; whoever it has left fights
    /// on where they are.
    /// </summary>
    void Eliminate(int team, string why)
    {
        if (Out[team]) return;
        Out[team] = true;
        for (int i = 0; i < Points.Length; i++)
            if (Owner[i] == team) { Owner[i] = -1; Progress[i] = 0f; Capper[i] = -1; }
        for (int i = _respawns.Count - 1; i >= 0; i--)
            if (_roster[_respawns[i].P].Team == team) _respawns.RemoveAt(i);
        _hud.Center($"{KothMode.TeamNames[team]} {why}", 6f);
        _hud.Event($"{KothMode.TeamNames[team]} {why}", team == 0 ? -1 : 1);
        Log($"[{Clock.Now:0}s] {KothMode.TeamNames[team]} {why}");
        Telemetry.Note($"{KothMode.TeamNames[team]} {why}");
        for (int t = 0; t < 3; t++) _commandAt[t] = Math.Min(_commandAt[t], Clock.Now + 2.0);
    }

    void CheckWin(double now)
    {
        for (int t = 0; t < 3; t++)
            if (!Out[t] && Spent(t) && AliveOn(t) == 0) Eliminate(t, "has nobody left");
        var left = Enumerable.Range(0, 3).Where(t => !Out[t]).ToList();
        int winner = left.Count == 1 ? left[0] : -1;
        // Time's up: whoever holds the most ground, then the most tickets. (It used to be tickets alone, with a tie
        // going to ALPHA; ground held didn't count.)
        if (winner < 0 && TimeLimit > 0 && now > TimeLimit)
            winner = left.OrderByDescending(t => Owned(t)).ThenByDescending(t => Tickets[t]).First();
        if (winner < 0) return;
        Winner = winner;
        _hud.Center($"{KothMode.TeamNames[winner]} WINS\n{Tickets[0]} · {Tickets[1]} · {Tickets[2]} tickets", 30f);
        Log($"[{now:0}s] {KothMode.TeamNames[winner]} wins, tickets {Tickets[0]}-{Tickets[1]}-{Tickets[2]}, points {Owned(0)}/{Owned(1)}/{Owned(2)}");
        Telemetry.Note($"{KothMode.TeamNames[winner]} wins");
    }

    void Capture(float dt)
    {
        int n = Points.Length;
        Array.Clear(Inside);
        Array.Clear(HQInside);
        foreach (var c in Combatants.All)
        {
            // (Flying over a point doesn't take it: a gunship's crew and a helicopter's passengers counted as on it.)
            if (!c.Alive || c.Ride is { Def.Air: true }) continue;
            for (int i = 0; i < n; i++)
                if ((c.FeetPos - Points[i].Center with { Y = c.FeetPos.Y }).Length() <= Points[i].Radius) Inside[i, c.Team]++;
            for (int t = 0; t < 3; t++)
                if ((c.FeetPos - HQ[t].Center with { Y = c.FeetPos.Y }).Length() <= HQ[t].Radius) HQInside[t, c.Team]++;
        }
        CaptureHQ(dt);
        for (int i = 0; i < n; i++)
        {
            int top = -1, first = 0, second = 0;
            for (int t = 0; t < 3; t++)
            {
                // Only the owner and sides that can take it count (the front line).
                int c = Owner[i] == t || CanTake(t, i) ? Inside[i, t] : 0;
                if (c > first) { second = first; first = c; top = t; }
                else if (c > second) second = c;
            }
            // Nobody on it: a side's hold on its own point comes back, slowly, and half-done progress on a neutral one
            // fades. (Both used to stay where they were for good: a point a raid had knocked below half stayed off the
            // spawn list, and kept pulling a squad back to it, until someone went and stood on it.)
            if (top < 0)
            {
                if (Owner[i] >= 0) Progress[i] = MathF.Min(1f, Progress[i] + CapRate * 0.25f * dt);
                else if (Capper[i] >= 0 && (Progress[i] -= CapRate * 0.25f * dt) <= 0f) { Progress[i] = 0f; Capper[i] = -1; }
                continue;
            }
            if (first == second) continue; // contested: nothing moves
            float rate = CapRate * Math.Min(first - second, 4) * dt;
            string name = Points[i].Site.Name;
            if (Owner[i] == top) Progress[i] = MathF.Min(1f, Progress[i] + rate);
            else if (Owner[i] >= 0)
            {
                Progress[i] -= rate;
                if (Progress[i] > 0f) continue;
                Announce(Owner[i], top, $"{KothMode.TeamNames[top]} neutralised {name}");
                Owner[i] = -1;
                Capper[i] = top;
                Progress[i] = 0f;
            }
            else if (Capper[i] != top)
            {
                Progress[i] -= rate;
                if (Progress[i] <= 0f) { Capper[i] = top; Progress[i] = 0f; }
            }
            else
            {
                Progress[i] += rate;
                if (Progress[i] < 1f) continue;
                Progress[i] = 1f;
                Owner[i] = top;
                Capper[i] = -1;
                Announce(-1, top, $"{KothMode.TeamNames[top]} captured {name}");
                // The squads that took it consolidate on it before moving on.
                foreach (var sq in Squads[top])
                    if (sq.Kind == SquadKind.Rifle && sq.Site == Points[i].Site && !sq.Defend && sq.PlayerOrderUntil <= Clock.Now) sq.Consolidate(Points[i].Site, Points[i]);
                // New ground: the commander reconsiders now rather than in 20 s, and presses on to what's next to it
                // for a minute and a half, before the enemy settles.
                _commandAt[top] = Math.Min(_commandAt[top], Clock.Now + 2.0);
                foreach (int j in Links[i])
                    if (j < n && j < _exploit.GetLength(1) && Owner[j] != top) _exploit[top, j] = Clock.Now + 90.0;
            }
        }
    }

    /// <summary>
    /// The headquarters: while an enemy that can go for it (see CanTakeHQ) has more people on it than its own side,
    /// its hold wears down; with none of them on it, it comes back. At nothing, it's overrun and the side is out.
    /// </summary>
    void CaptureHQ(float dt)
    {
        for (int t = 0; t < 3; t++)
        {
            if (Out[t]) continue;
            int def = HQInside[t, t], att = 0, by = -1;
            for (int a = 0; a < 3; a++)
                if (CanTakeHQ(a, t) && HQInside[t, a] > att) { att = HQInside[t, a]; by = a; }
            if (att > def)
            {
                if (!_hqWarned[t])
                {
                    _hqWarned[t] = true;
                    Announce(t, by, $"{KothMode.TeamNames[by]} is attacking {KothMode.TeamNames[t]}'s headquarters");
                }
                HQHold[t] -= HQCapRate * Math.Min(att - def, 4) * dt;
                if (HQHold[t] > 0f) continue;
                HQHold[t] = 0f;
                Announce(t, by, $"{KothMode.TeamNames[by]} overran {KothMode.TeamNames[t]}'s headquarters");
                Eliminate(t, "has lost its headquarters");
            }
            else if (att == 0)
            {
                HQHold[t] = MathF.Min(1f, HQHold[t] + HQCapRate * Math.Max(def, 1) * dt);
                if (HQHold[t] >= 1f) _hqWarned[t] = false;
            }
        }
    }

    void Announce(int lost, int gained, string text)
    {
        Log($"[{Clock.Now:0}s] {text}");
        Telemetry.Note(text);
        _hud.Event(text, lost == 0 ? -1 : gained == 0 ? 1 : 0);
        if (lost >= 0) _commandAt[lost] = Math.Min(_commandAt[lost], Clock.Now + 2.0);
    }

    int _downs;

    void OnDowned(ICombatant victim, HitInfo hit)
    {
        _downs++;
        string zone = hit.Zone.ToString().ToLowerInvariant();
        _hud.AddKill(hit.Shooter, victim, $"downed · {zone}, {hit.Distance:0} m");
        Log($"[{Clock.Now:0}s] {hit.Shooter?.Callsign ?? "?"} downed {victim.Callsign} — {victim.Body.Summary()}");
        if (victim == PlayerBody)
        {
            _hud.Center("You're down", 2f);
            ReleasePlayerOrder();
        }
    }

    void OnKilled(ICombatant victim, HitInfo hit)
    {
        string killer = hit.Shooter?.Callsign ?? "?";
        string zone = hit.Zone.ToString().ToLowerInvariant();
        _hud.AddKill(hit.Shooter, victim, $"{zone}, {hit.Distance:0} m");
        bool tk = hit.Shooter != null && hit.Shooter != victim && hit.Shooter.Team == victim.Team;
        Log($"[{Clock.Now:0}s] {killer} ({hit.Weapon}) killed {victim.Callsign} — {zone}, {hit.Distance:0} m{(tk ? "  TEAMKILL" : "")}");
        Spend(victim.Team, 1);
        _kills++;

        if (victim is Bot b)
        {
            _respawns.Add((b.P, Clock.Now + RespawnDelay));
            _corpses.Enqueue(b);
            while (_corpses.Count > CorpseCap)
            {
                var old = _corpses.Dequeue();
                Bots.Remove(old);
                old.Squad?.Members.Remove(old);
                if (IsInstanceValid(old)) old.QueueFree();
            }
        }
        if (victim == PlayerBody)
        {
            ReleasePlayerOrder();
            _playerDiedAt = Clock.Now;
            PlayerRespawnAt = Clock.Now + RespawnDelay;
            _hud.Center($"Killed by {killer} — {zone}, {hit.Distance:0} m", 3f);
        }

        var mate = Bots.Where(m => m.Alive && m.Team == victim.Team && m != victim).OrderBy(m => m.FeetPos.DistanceTo(victim.FeetPos)).FirstOrDefault();
        if (mate != null && mate.FeetPos.DistanceTo(victim.FeetPos) < 80f)
        {
            Comms.Say(mate, $"Man down! {victim.Callsign} is down!");
            // Shot dead in front of him: he has a rough idea where from (a shell or a fragment gives nothing away).
            if (hit.Direct && hit.Shooter is { Alive: true } s) mate.Senses.Alert(s, 0.2f);
        }
    }

    void OnSaid(ICombatant who, string text)
    {
        int watch = PlayerBody != null && PlayerBody.Alive ? PlayerBody.Team : Spec.Target?.Team ?? 0;
        if (who.Team != watch) return;
        var me = PlayerBody != null && PlayerBody.Alive ? PlayerBody.FeetPos : Spec.Target?.FeetPos ?? who.FeetPos;
        // Your own squad is on your radio wherever they are; others only nearby.
        bool squad = who is Bot { Squad: { } s } && s == PlayerSquad;
        if (squad || who.FeetPos.DistanceTo(me) < 250f) _hud.AddComm($"{who.Callsign}: {text}", squad);
    }

    static void Log(string s)
    {
        if (DuelMode.Verbose) GD.Print(s);
    }
}
