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
                if (Combatants.All.Any(c => c != b && c.Team == b.Team && c.Alive && c.FeetPos.DistanceTo(p.Pos) < 1.5f)) continue;
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
/// - Each side has tickets. Every death costs one, and every 15 s a side loses one
///   more for each point it's behind the side holding the most. At zero a side
///   can no longer reinforce. The last side with tickets wins.
/// - You respawn at your base or at any point your side owns that isn't under
///   attack. Bots reinforce whichever is closest to their squad's objective.
/// - Each side has squads of up to six with a leader, and a commander that hands
///   out orders: attack a neutral or enemy point, or defend one of ours that's
///   under threat. The player's squad can be ordered from the map instead.
/// </summary>
public partial class TerritoryMode : Node, IMatch
{
    public const float CaptureExtra = 14f;
    const double TickEvery = 1.0, BleedEvery = 15.0, RespawnDelay = 10.0, CommandEvery = 20.0, TimeLimit = 3600.0;
    const float CapRate = 1f / 40f; // per second, per head of advantage
    const int SquadSize = 6;

    public Valley Map = null!;
    public GameSetup Setup = null!;
    public Hud PlayerHud = null!;

    public List<Bot> Bots { get; } = new();
    public Spectator Spec { get; private set; } = null!;
    public readonly int[] Tickets = new int[3];
    public int StartTickets { get; private set; }
    public readonly bool[] Out = new bool[3];
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
        if (Owner[i] == team) return false;
        if (!Front) return true;
        foreach (int j in Links[i])
            if (j == Points.Length + team || (j < Points.Length && Owner[j] == team)) return true;
        return false;
    }

    /// <summary>Ours and takeable by someone else: where the front runs through our ground.</summary>
    public bool OnFront(int team, int i)
    {
        if (Owner[i] != team) return false;
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
    readonly HashSet<Squad> _bound = new();

    /// <summary>Is the player leading their squad (picked the squad leader role)?</summary>
    public bool PlayerLeads => PlayerSquad != null && PlayerBody is { Alive: true } p && p.Kit == Role.Leader;
    readonly List<(Personality P, double At)> _respawns = new();
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
        StartTickets = Setup.TeamSize * 30;
        for (int t = 0; t < 3; t++) Tickets[t] = StartTickets;
        Front = Settings.FrontLine;
        BuildLinks();
        Squad.Hostile = (site, team) =>
        {
            int i = IndexOf(site);
            return i >= 0 && ((Owner[i] >= 0 && Owner[i] != team) || Inside[i, (team + 1) % 3] + Inside[i, (team + 2) % 3] > 0);
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
        }

        var names = Personality.Callsigns.OrderBy(_ => _rng.Randi()).ToList();
        int k = 0;
        BotBrain.ResetStatics();
        Squad.Engagements = Squad.Assaults = Squad.Hunts = 0;
        Squad.ResetAll();
        Squad.Orps = Squad.Deploys = Squad.BuddySwaps = 0;
        Drone.Clear();
        Drone.Log = Log;
        Bot.StuckEvents = 0;
        CrewBrain.MortarRounds = 0;
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

        for (int t = 0; t < 3; t++) Command(t);
        foreach (var (p, (team, _, _)) in _roster) SpawnBot(p, team, atBase: true);
        if (Setup.PlayerJoins) SpawnPlayer(atBase: true);
        else Spec.Activate(Bots.FirstOrDefault());
        for (int t = 0; t < 3; t++) _commandAt[t] = Clock.Now + CommandEvery + t * 5.0;
        _nextTick = Clock.Now + TickEvery;
        _nextBleed = Clock.Now + BleedEvery;
        _hud.Center("Take and hold the settlements", 5f);
        Log($"--- TERRITORY {Setup.TeamSize}x3, {n} points, {StartTickets} tickets, {Squads[0].Count} squads per side ---");
    }

    public override void _ExitTree()
    {
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
        Log($"[{Clock.Now:0}s] VEHICLE LOST {v.Def.Name} (team {v.Team}) by {by?.Callsign ?? "?"}");
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

    /// <summary>Base, plus every point this side owns that has no enemy on or near it.</summary>
    public List<(string Name, Vector3 Pos, int Point)> SpawnOptions(int team)
    {
        var list = new List<(string, Vector3, int)> { ("Base", Map.Bases[team], -1) };
        // FOBs: spawn indices from 100 up.
        for (int k = 0; k < Fob.All.Count; k++)
            if (Fob.All[k].Team == team && !EnemyNear(team, Fob.All[k].GlobalPosition, 40f))
                list.Add((Fob.All[k].Label, Fob.All[k].GlobalPosition, 100 + k));
        for (int i = 0; i < Points.Length; i++)
            if (Owner[i] == team && Progress[i] > 0.5f && !EnemyNear(team, Points[i].Center, Points[i].Radius + 50f))
                list.Add((Points[i].Site.Name, Points[i].Center, i));
        return list;
    }

    bool EnemyNear(int team, Vector3 p, float r)
    {
        foreach (var c in Combatants.All)
            if (c.Alive && c.Team != team && (c.FeetPos - p with { Y = c.FeetPos.Y }).Length() < r) return true;
        return false;
    }

    Vector3 SpawnAt((string Name, Vector3 Pos, int Point) o)
    {
        float a = _rng.Randf() * Mathf.Tau;
        float r = o.Point < 0 ? _rng.RandfRange(3f, 14f) : o.Point >= 100 ? _rng.RandfRange(3f, 10f) : _rng.RandfRange(0f, Points[o.Point].Site.Radius * 0.7f);
        return Map.Ground(o.Pos + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r)) + Vector3.Up * 0.3f;
    }

    /// <summary>The spawn closest to where the squad is headed.</summary>
    (string Name, Vector3 Pos, int Point) BestSpawn(int team, Squad? sq)
    {
        var opts = SpawnOptions(team);
        if (sq?.Objective == null) return opts[0];
        var goal = sq.Objective.Center;
        return opts.OrderBy(o => (o.Pos - goal with { Y = o.Pos.Y }).Length()).First();
    }

    float Face(Vector3 from, Vector3 to)
    {
        var d = to - from;
        return Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z));
    }

    void SpawnBot(Personality p, int team, bool atBase = false)
    {
        var sq = _roster[p].Squad;
        // Vehicle crews come back at base, where the vehicles are.
        // With a front line, the infantry deploys straight to the point nearest its orders
        // (in its own sector) rather than walking kilometres from base; crews start with their vehicles.
        bool crew = sq.Kind is SquadKind.Armor or SquadKind.Transport or SquadKind.Air or SquadKind.Logistics or SquadKind.Mortar;
        var where = crew || (atBase && !Front) ? SpawnOptions(team)[0] : BestSpawn(team, sq);
        var role = _roster[p].Role;
        var b = new Bot { TeamId = team, P = p, Role = role, Def = Roles.Primary(role), Squad = sq };
        GetParent().AddChild(b);
        b.GlobalPosition = SpawnAt(where);
        b.Aim.Yaw = Face(b.GlobalPosition, sq.Objective?.Center ?? Vector3.Zero);
        sq.Join(b);
        Bots.Add(b);
    }

    void SpawnPlayer(bool atBase = false)
    {
        PlayerBody?.QueueFree();
        var opts = SpawnOptions(0);
        var where = atBase && !Front ? opts[0]
            : atBase ? BestSpawn(0, PlayerSquad)
            : PlayerSpawn == 0 ? opts[0]
            : PlayerSpawn > 0 && opts.FirstOrDefault(o => o.Point == PlayerSpawn - 1) is { Name: not null } chosen ? chosen
            : BestSpawn(0, PlayerSquad);
        var p = new Player { TeamId = 0, Kit = Settings.PlayerRole };
        GetParent().AddChild(p);
        p.GlobalPosition = SpawnAt(where);
        p.SetYaw(Face(p.GlobalPosition, PlayerSquad?.Objective?.Center ?? Vector3.Zero));
        PlayerSquad?.Join(p);
        // Leading: your squad forms up on you by default.
        if (PlayerSquad != null) PlayerSquad.FollowPlayer = Settings.PlayerRole == Role.Leader;
        SoundWorld.I.ResetHearing();
        PlayerBody = p;
        PlayerHud.P = p;
        PlayerRespawnAt = -1;
        _playerDiedAt = -1;
        Spec.Deactivate();
    }

    // ================================================================ orders

    /// <summary>
    /// The commander: value every point (take neutral and enemy points, answer
    /// threats to our own), then give each squad the best one near it, spreading
    /// squads over different points rather than piling them onto one.
    /// </summary>
    void Command(int team)
    {
        int n = Points.Length;
        var value = new float[n];
        var known = new HashSet<ICombatant>[n];
        for (int i = 0; i < n; i++) known[i] = new HashSet<ICombatant>();
        foreach (var b in Bots)
        {
            if (!IsInstanceValid(b) || !b.Alive || b.Team != team) continue;
            foreach (var t in b.Senses.Threats)
            {
                if (!t.Who.Alive || Clock.Now - Math.Max(t.LastSeen, t.LastHeard) > 15.0) continue;
                for (int i = 0; i < n; i++)
                    if ((t.LastKnownPos - Points[i].Center with { Y = t.LastKnownPos.Y }).Length() < Points[i].Radius + 50f) known[i].Add(t.Who);
            }
        }
        for (int i = 0; i < n; i++)
        {
            int threat = known[i].Count;
            bool contested = Inside[i, (team + 1) % 3] + Inside[i, (team + 2) % 3] > 0;
            if (Owner[i] == team)
                // Ours: only worth sending people if it's actually being taken or about to be;
                // with a front line, a point on it is worth a garrison even when quiet.
                value[i] = contested || Progress[i] < 0.95f ? 8f + threat * 0.3f : threat > 0 ? 4f : Front && OnFront(team, i) ? 1.5f : -3f;
            else if (!CanTake(team, i)) value[i] = -20f; // behind the enemy's lines: can't be taken yet
            else value[i] = Owner[i] < 0 ? 6.5f : 6f + (Owned(Owner[i]) >= Owned(team) ? 0.8f : 0f); // hit the leader
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

        var assigned = new int[n];
        foreach (var sq in Squads[team])
            if (sq.Kind == SquadKind.Rifle && sq.PlayerOrderUntil > Clock.Now && sq.Site != null) assigned[Array.FindIndex(Points, o => o.Site == sq.Site)]++;

        // Defenders (front line): about one rifle squad in three holds the most threatened points
        // on our side of the front, so ground taken isn't simply walked away from.
        var defenders = new HashSet<Squad>();
        if (Front)
        {
            var rifles = Squads[team].Where(s => s.Kind == SquadKind.Rifle && s.PlayerOrderUntil <= Clock.Now && s.Alive > 0 && s.Transport == null && !s.Busy).ToList();
            // One in three: with only two rifle squads both attack (engineers still dig in on the front).
            int want = rifles.Count / 3;
            var front = Enumerable.Range(0, n).Where(i => OnFront(team, i))
                .OrderByDescending(i => known[i].Count * 3 + (Inside[i, (team + 1) % 3] + Inside[i, (team + 2) % 3] > 0 ? 6 : 0) + (Progress[i] < 0.95f ? 4 : 0)).ToList();
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
            // Consolidating, or in the middle of a deliberate attack: leave them to finish it.
            if (sq.Busy || sq.Phase != AssaultPhase.None) { if (sq.Site != null) assigned[IndexOf(sq.Site)]++; continue; }
            if (sq.Transport != null && sq.Site != null) { assigned[IndexOf(sq.Site)]++; continue; } // riding there: don't change its mind mid-journey
            var origin = sq.Position ?? Map.Bases[team];
            int best = -1;
            float bestScore = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float d = (Points[i].Center - origin with { Y = Points[i].Center.Y }).Length();
                // Sticking with the current order avoids flip-flopping, unless it's a quiet point we already hold.
                bool stick = sq.Site == Points[i].Site && value[i] > 0f;
                float score = value[i] - d / (220f * MathF.Sqrt(Map.SizeScale)) - assigned[i] * 3f + (stick ? 1.5f : 0f);
                if (score <= bestScore) continue;
                bestScore = score;
                best = i;
            }
            if (best < 0) continue;
            assigned[best]++;
            bool changed = sq.Site != Points[best].Site;
            sq.Order(Points[best].Site, Points[best], Owner[best] == team);
            if (!changed) continue;
            if (sq.Leader is Bot lead) Comms.Say(lead, $"{sq.Name}, {sq.OrderText.ToLowerInvariant()}!");
            if (sq == PlayerSquad) _hud.Center($"Squad orders: {sq.OrderText}", 4f);
            Log($"[{Clock.Now:0}s] {sq.Name}: {sq.OrderText}");
        }
        Support(team, value, known);
    }

    int IndexOf(Site? s) => s == null ? -1 : Array.FindIndex(Points, o => o.Site == s);

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
            float Dist(int i) => (Points[i].Center - origin with { Y = Points[i].Center.Y }).Length();
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
                    var p = Points[target];
                    var axis = Vector3.Zero;
                    int front = Enumerable.Range(0, Points.Length).Where(i => Owner[i] != team).OrderBy(i => Points[i].Center.DistanceTo(p.Center)).DefaultIfEmpty(-1).First();
                    if (front >= 0) axis = (Points[front].Center - p.Center) with { Y = 0f };
                    sq.ThreatAxis = axis.LengthSquared() > 1f ? axis.Normalized() : Vector3.Zero;
                    bool changed = sq.Site != p.Site;
                    sq.Order(p.Site, p, Owner[target] == team, Owner[target] == team ? "Fortify" : "Attack");
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
                    var armor = Radio.Latest(team, RadioKind.Armor, 60.0);
                    int cur = IndexOf(sq.Site);
                    int target = attacks.Count == 0 ? Enumerable.Range(0, Points.Length).OrderBy(Dist).First()
                               : attacks.Contains(cur) && backing.GetValueOrDefault(cur) == 0 ? cur
                               : LeastBacked(attacks, Dist);
                    Back(target);
                    var watch = !light && armor != null && armor.Pos.DistanceTo(Points[target].Center) < 500f ? armor.Pos : Points[target].Center;
                    // Shoot and scoot: a new position every couple of minutes, or when hit.
                    bool stale = !_posts.TryGetValue(sq, out var post) || post.Site != Points[target].Site || Clock.Now - sq.OrderSince > 120.0 || Clock.Now - sq.Vehicle.LastHit < 3.0;
                    if (stale)
                    {
                        post = (Points[target].Site, FindPost(team, watch, light ? 90f : 150f, light ? 200f : 330f));
                        _posts[sq] = post;
                        sq.Order(Points[target].Site, new PointObjective { Center = post.Post, Watch = watch }, false, "Support", Points[target].Site.Name);
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
                    if (sq.Site != Points[t2].Site)
                        sq.Order(Points[t2].Site, new PointObjective { Center = FindPost(team, Points[t2].Center, 90f, 170f), Watch = Points[t2].Center }, false, "Cover", Points[t2].Site.Name);
                    break;
                }
                case SquadKind.Air when sq.Vehicle?.Def.Kind == VKind.AH:
                {
                    // Close air support over the main attack, or hunting reported armour.
                    var armor = Radio.Latest(team, RadioKind.Armor, 60.0);
                    int t4 = attacks.Count > 0 ? LeastBacked(attacks, Dist) : Enumerable.Range(0, Points.Length).OrderBy(Dist).First();
                    var watch = armor != null ? armor.Pos : Points[t4].Center;
                    if (sq.Objective is not PointObjective pa || pa.Watch.DistanceTo(watch) > 200f)
                    {
                        sq.Order(Points[t4].Site, new PointObjective { Center = watch, Watch = watch }, false, "Air support", armor != null ? "vs armour" : Points[t4].Site.Name);
                        Log($"[{Clock.Now:0}s] {sq.Name} (gunship): {sq.OrderText}");
                    }
                    break;
                }
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
                    var site = Points[td].Site;
                    if (!_posts.TryGetValue(sq, out var post) || post.Site != site)
                    {
                        post = (site, FindPost(team, Points[td].Center, 350f, 700f));
                        _posts[sq] = post;
                        sq.Order(site, new PointObjective { Center = post.Post, Watch = Points[td].Center }, false, "Drone ops over", site.Name);
                        Log($"[{Clock.Now:0}s] {sq.Name} (drones): {sq.OrderText} from {post.Post.DistanceTo(Points[td].Center):0} m");
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
                        var site = FindFobSite(team, Points[t3].Center);
                        if (Fob.All.All(f => f.Team != team || f.GlobalPosition.DistanceTo(site) > 250f))
                        {
                            sq.FobSite = site;
                            sq.Order(Points[t3].Site, new PointObjective { Center = site, Watch = Points[t3].Center }, false, "Build FOB near", Points[t3].Site.Name);
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
    public int Owned(int team) => Owner.Count(o => o == team);

    public override void _Process(double delta)
    {
        double now = Clock.Now;

        for (int i = _respawns.Count - 1; i >= 0; i--)
        {
            var (p, at) = _respawns[i];
            if (now < at) continue;
            _respawns.RemoveAt(i);
            if (Winner >= 0 || Out[_roster[p].Team]) continue;
            SpawnBot(p, _roster[p].Team);
        }

        if (PlayerBody != null && !PlayerBody.Alive && _playerDiedAt > 0)
        {
            if (!Spec.Active && now - _playerDiedAt > 2.0)
            {
                PlayerHud.P = null;
                Spec.Activate(Bots.FirstOrDefault(b => b.Alive && b.Team == 0) ?? Bots.FirstOrDefault(b => b.Alive));
            }
            if (now >= PlayerRespawnAt && Winner < 0 && !Out[0]) SpawnPlayer();
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
                $"nav iteration {NavigationServer3D.MapGetIterationId(Map.GetWorld3D().NavigationMap)} base->{NavigationServer3D.MapGetClosestPoint(Map.GetWorld3D().NavigationMap, Map.Bases[0]).DistanceTo(Map.Bases[0]):0} m");
            if (!_navLogged && Map.Nav.Finished && NavigationServer3D.MapGetIterationId(Map.GetWorld3D().NavigationMap) > 0)
            {
                _navLogged = true;
                Log($"     navmesh: {Map.Nav.Total} tiles ({Map.Nav.EmptyTiles} empty, {Map.Nav.Polygons} polygons, {Map.Nav.DensePolygons} in town; vehicles {Map.VehicleNav.Polygons}) in {Map.Nav.Seconds:0.0}s wall (map built in {Map.BuildSeconds:0.0}s)");
            }
            else if (_navLogged && !_upChecked)
            {
                _upChecked = true;
                // Can people actually get upstairs? Path from the street to upper-floor windows and rooftops.
                var nm = Map.GetWorld3D().NavigationMap;
                int tried = 0, onMesh = 0, reached = 0;
                var byFloor = new Dictionary<int, (int, int)>();
                foreach (var pc in Map.Perches.Where(p => p.Floor >= 1).OrderBy(_ => _rng.Randi()).Take(40))
                {
                    tried++;
                    var near = NavigationServer3D.MapGetClosestPoint(nm, pc.Pos);
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
            foreach (var sq in Squads.SelectMany(l => l).Where(s => s.Kind == SquadKind.Drone))
                foreach (var m in sq.Members.OfType<Bot>().Where(m => m.Alive && m.Ops != null))
                    Log($"     {sq.Name} {m.Callsign}: {m.Brain.State} ({m.Brain.Note}) stock {m.Ops!.Quads}q/{m.Ops.Bombs}b/{m.Ops.Fpvs}f"
                        + (m.Ops.Quad is { Dead: false } q ? $", quad {q.GlobalPosition.DistanceTo(m.FeetPos):0} m out, {q.Bombs} bombs, batt {q.Battery:P0}, sees {q.Seen.Count(kv => Clock.Now - kv.Value < 3)}" : "")
                        + (m.Ops.Fpv is { Dead: false } f ? $", FPV {f.GlobalPosition.DistanceTo(f.TargetPoint()):0} m to target" : ""));
            Log($"     stuck events {Bot.StuckEvents}, squad engagements {Squad.Engagements}, assaults {Squad.Assaults}, armour hunts {Squad.Hunts}, drones {Drone.Launched} up, {Drone.Spots} spots, {Drone.BombsDropped} bombs, {Drone.FpvStrikes} FPV hits, {Drone.ShotDown} shot down, ORPs {Squad.Orps}, deployed {Squad.Deploys}, buddy swaps {Squad.BuddySwaps}, marching {string.Join(" ", Squads.SelectMany(l => l).Where(s => s.Kind == SquadKind.Rifle && s.Alive > 0).GroupBy(s => s.MarchOrder).Select(g => $"{g.Key}:{g.Count()}"))}, drills {Squad.Drills} (flank {Squad.Flanks}, break {Squad.Breaks}, indirect {Squad.Indirects}, consolidate {Squad.Consolidations}), gunners vs infantry {CrewBrain.InfantryTargets} picks/{CrewBrain.InfantryShots} shots (vs armour {CrewBrain.ArmorShots}), defending {Squads.Sum(l => l.Count(s => s.Kind == SquadKind.Rifle && s.Defend))}, kills so far {_kills} (downs {_downs}, down now {Combatants.All.Count(c => c.Downed)}), bounds {BotBrain.Bounds}, hunts {BotBrain.Hunts}, to-cover {BotBrain.Covers}; " +
                $"medevac'd {Motor.Evacuated}, air assaults {Motor.AirAssaults}, mortar rounds {CrewBrain.MortarRounds}, vehicles {Vehicle.All.Count(v => !v.Destroyed)} live / {Vehicle.All.Count(v => v.Destroyed)} wrecks, FOBs {Fob.All.Count}, rockets {BotBrain.Rockets}, " +
                $"heals {BotBrain.Heals} (revives {BotBrain.Revives}, self-aid {BotBrain.SelfAids}), resupplies {BotBrain.Resupplies}, sandbags {BotBrain.Builds}, 40mm {BotBrain.Launches}, intel {Intel.Count}");
            foreach (var s in Motor.Slots)
                if (s.Live is { Destroyed: false } lv)
                    Log($"     {KothMode.TeamNames[s.Team][..1]} {lv.Def.Name,-18} {lv.Status(),-14} crew {lv.Occupants.Count(o => o != null)}/{lv.Occupants.Length} " +
                        $"speed {MathF.Abs(lv.Speed):0.0} goal {(lv.Goal is Vector3 g ? $"{lv.GlobalPosition.DistanceTo(g):0} m" : "-")} job {s.Job} stuck {lv.Drive.Stucks}{(lv.Drive.Note != "" ? $" ({lv.Drive.Note})" : "")}" +
                        (lv.GunnerSeat >= 0 && lv.Occupants[lv.GunnerSeat] is Bot gb ? $" · {gb.Crew.Note}" : ""));
            foreach (var sqs in Squads)
                Log("     " + string.Join("  ", sqs.Select(s => $"{s.Name}[{s.Kind}]: {s.OrderText} ({s.Alive} alive, {SquadSpread(s):0} m spread)")));
        }
    }

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
        if (n <= 0 || Out[team]) return;
        Tickets[team] = Math.Max(0, Tickets[team] - n);
        if (Tickets[team] > 0) return;
        Out[team] = true;
        _hud.Center($"{KothMode.TeamNames[team]} is out of tickets", 5f);
        Log($"[{Clock.Now:0}s] {KothMode.TeamNames[team]} out of tickets");
    }

    void CheckWin(double now)
    {
        var left = Enumerable.Range(0, 3).Where(t => !Out[t]).ToList();
        int winner = left.Count == 1 ? left[0] : -1;
        if (winner < 0 && now > TimeLimit) winner = Array.IndexOf(Tickets, Tickets.Max());
        if (winner < 0) return;
        Winner = winner;
        _hud.Center($"{KothMode.TeamNames[winner]} WINS\n{Tickets[0]} · {Tickets[1]} · {Tickets[2]} tickets", 30f);
        Log($"[{now:0}s] {KothMode.TeamNames[winner]} wins, tickets {Tickets[0]}-{Tickets[1]}-{Tickets[2]}");
    }

    void Capture(float dt)
    {
        int n = Points.Length;
        Array.Clear(Inside);
        foreach (var c in Combatants.All)
        {
            if (!c.Alive) continue;
            for (int i = 0; i < n; i++)
                if ((c.FeetPos - Points[i].Center with { Y = c.FeetPos.Y }).Length() <= Points[i].Radius) Inside[i, c.Team]++;
        }
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
            if (top < 0 || first == second) continue; // empty or contested: nothing moves
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
                // New ground: the commander reconsiders now rather than in 20 s.
                _commandAt[top] = Math.Min(_commandAt[top], Clock.Now + 2.0);
            }
        }
    }

    void Announce(int lost, int gained, string text)
    {
        Log($"[{Clock.Now:0}s] {text}");
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
        if (victim == PlayerBody) _hud.Center("You're down", 2f);
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
            _playerDiedAt = Clock.Now;
            PlayerRespawnAt = Clock.Now + RespawnDelay;
            _hud.Center($"Killed by {killer} — {zone}, {hit.Distance:0} m", 3f);
        }

        var mate = Bots.Where(m => m.Alive && m.Team == victim.Team && m != victim).OrderBy(m => m.FeetPos.DistanceTo(victim.FeetPos)).FirstOrDefault();
        if (mate != null && mate.FeetPos.DistanceTo(victim.FeetPos) < 80f)
        {
            Comms.Say(mate, $"Man down! {victim.Callsign} is down!");
            if (hit.Shooter is { Alive: true } s) mate.Senses.Alert(s, 0.2f);
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
