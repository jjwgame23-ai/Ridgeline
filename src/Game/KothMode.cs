using Godot;

namespace Ridgeline;

/// <summary>What a game mode exposes to the spectator camera and bot debug overlay.</summary>
public interface IMatch
{
    List<Bot> Bots { get; }
    Spectator Spec { get; }
}

/// <summary>Where bots with nothing better to do should go.</summary>
public interface IObjective
{
    Vector3 Center { get; }
    float Radius { get; }
    Vector3 PointFor(Bot b, RandomNumberGenerator rng);
}

/// <summary>
/// King of the Hill, three factions. One settlement at a time is the zone;
/// every few seconds the faction with the most people alive inside it scores a
/// point. The zone moves to another settlement on a timer. The dead respawn after
/// a delay at their faction's rally point, which sits a few hundred metres from
/// the zone on their side of the map. First to the target score wins.
/// </summary>
public partial class KothMode : Node, IMatch, IObjective
{
    public static readonly string[] TeamNames = { "ALPHA", "BRAVO", "CHARLIE" };
    public const float ZoneRadius = 70f;
    const double ScoreEvery = 5.0, RespawnDelay = 10.0, ZoneInterval = 480.0;
    const float RallyDistance = 280f;
    const int CorpseCap = 70;

    public Valley Map = null!;
    public GameSetup Setup = null!;
    public Hud PlayerHud = null!;
    public int TargetScore = 300;

    public List<Bot> Bots { get; } = new();
    public Spectator Spec { get; private set; } = null!;
    public readonly int[] Score = new int[3];
    public readonly int[] InZone = new int[3];
    public readonly Vector3[] Rally = new Vector3[3];
    public Site Zone { get; private set; } = null!;
    public int Holder { get; private set; } = -1;
    public double ZoneMovesAt { get; private set; }
    public int Winner { get; private set; } = -1;
    public Player? PlayerBody { get; private set; }
    public double PlayerRespawnAt { get; private set; } = -1;

    public Vector3 Center => Zone.Center;
    public float Radius => ZoneRadius;

    readonly List<Personality>[] _roster = { new(), new(), new() };
    readonly List<(Personality P, int Team, double At)> _respawns = new();
    readonly Queue<Bot> _corpses = new();
    readonly RandomNumberGenerator _rng = new();
    KothHud _hud = null!;
    double _nextTick, _nextDump, _playerDiedAt = -1;
    bool _navLogged, _benched;

    public override void _Ready()
    {
        _rng.Randomize();
        var names = Personality.Callsigns.OrderBy(_ => _rng.Randi()).ToList();
        int k = 0;
        for (int team = 0; team < 3; team++)
        {
            int n = team == 0 && Setup.PlayerJoins ? Setup.TeamSize - 1 : Setup.TeamSize;
            for (int i = 0; i < n; i++) _roster[team].Add(Personality.Roll(_rng, names[k++ % names.Count]));
        }

        Spec = new Spectator { Mode = this };
        AddChild(Spec);
        _hud = new KothHud { Mode = this };
        AddChild(_hud);
        AddChild(new BotDebugDraw { Mode = this });

        Combatants.Killed += OnKilled;
        Comms.Said += OnSaid;
        BotBrain.DefaultObjective = this;

        MoveZone(first: true);
        for (int team = 0; team < 3; team++)
            foreach (var p in _roster[team]) SpawnBot(p, team);
        if (Setup.PlayerJoins) SpawnPlayer();
        else Spec.Activate(Bots.FirstOrDefault());
        _nextTick = Clock.Now + ScoreEvery;
        _hud.Center($"Capture {Zone.Name}", 4f);
        Log($"--- KOTH {Setup.TeamSize}x3, zone {Zone.Name} ({Zone.Kind}) ---");
    }

    public override void _ExitTree()
    {
        Combatants.Killed -= OnKilled;
        Comms.Said -= OnSaid;
        if (BotBrain.DefaultObjective == this) BotBrain.DefaultObjective = null;
    }

    // ================================================================ zone and spawns

    void MoveZone(bool first = false)
    {
        var options = Map.Sites.Where(s => s != Zone).ToList();
        Zone = first
            ? Map.Sites.OrderBy(s => s.Center.Length() + _rng.RandfRange(0f, 250f)).First() // start somewhere central
            : options[_rng.RandiRange(0, options.Count - 1)];
        ZoneMovesAt = Clock.Now + ZoneInterval;
        for (int t = 0; t < 3; t++)
        {
            var away = Map.Bases[t] - Zone.Center;
            away.Y = 0f;
            Rally[t] = Map.Ground(Zone.Center + away.Normalized() * RallyDistance);
        }
        foreach (var b in Bots) if (IsInstanceValid(b) && b.Alive) b.Brain.ObjectiveChanged();
        if (!first)
        {
            _hud.Center($"The zone has moved to {Zone.Name}", 5f);
            Log($"[{Clock.Now:0}s] zone moved to {Zone.Name} ({Zone.Kind})");
        }
    }

    public Vector3 PointFor(Bot b, RandomNumberGenerator rng)
    {
        // Mostly the buildings and cover in the zone; sometimes anywhere inside it.
        if (Zone.Points.Count > 0 && rng.Randf() < 0.7f) return Zone.Points[rng.RandiRange(0, Zone.Points.Count - 1)];
        float a = rng.Randf() * Mathf.Tau, r = MathF.Sqrt(rng.Randf()) * ZoneRadius * 0.8f;
        return Map.Ground(Zone.Center + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r));
    }

    Vector3 SpawnPoint(int team)
    {
        float a = _rng.Randf() * Mathf.Tau, r = _rng.RandfRange(2f, 10f);
        return Map.Ground(Rally[team] + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r)) + Vector3.Up * 0.3f;
    }

    float FaceZone(Vector3 from)
    {
        var d = Zone.Center - from;
        return Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z));
    }

    void SpawnBot(Personality p, int team)
    {
        var b = new Bot { TeamId = team, P = p, Def = p.Marksman ? WeaponDef.Marksman : WeaponDef.Carbine };
        GetParent().AddChild(b);
        b.GlobalPosition = SpawnPoint(team);
        b.Aim.Yaw = FaceZone(b.GlobalPosition);
        Bots.Add(b);
    }

    void SpawnPlayer()
    {
        PlayerBody?.QueueFree();
        var p = new Player { TeamId = 0 };
        GetParent().AddChild(p);
        p.GlobalPosition = SpawnPoint(0);
        p.SetYaw(FaceZone(p.GlobalPosition));
        PlayerBody = p;
        PlayerHud.P = p;
        PlayerRespawnAt = -1;
        _playerDiedAt = -1;
        Spec.Deactivate();
    }

    // ================================================================ loop

    public int AliveOn(int team) => Combatants.All.Count(c => c.Team == team && c.Alive);

    public override void _Process(double delta)
    {
        double now = Clock.Now;

        for (int i = _respawns.Count - 1; i >= 0; i--)
        {
            if (now < _respawns[i].At || Winner >= 0) continue;
            SpawnBot(_respawns[i].P, _respawns[i].Team);
            _respawns.RemoveAt(i);
        }

        if (PlayerBody != null && !PlayerBody.Alive && _playerDiedAt > 0)
        {
            if (!Spec.Active && now - _playerDiedAt > 2.0)
            {
                PlayerHud.P = null;
                Spec.Activate(Bots.FirstOrDefault(b => b.Alive && b.Team == 0) ?? Bots.FirstOrDefault(b => b.Alive));
            }
            if (now >= PlayerRespawnAt && Winner < 0) SpawnPlayer();
        }

        if (!_navLogged && Map.Nav.Finished)
        {
            _navLogged = true;
            Log($"[{now:0}s] navmesh ready: {Map.Nav.Total} tiles in {Map.Nav.Seconds:0.0}s wall");

        }

        if (_navLogged && !_benched && DuelMode.Verbose && NavigationServer3D.MapGetIterationId(Map.GetWorld3D().NavigationMap) > 0 && now > 3.0)
        {
            _benched = true;
            BenchNav();
        }

        if (Winner >= 0) return;

        if (now >= _nextTick)
        {
            _nextTick = now + ScoreEvery;
            Array.Clear(InZone);
            foreach (var c in Combatants.All)
            {
                if (!c.Alive) continue;
                var d = c.FeetPos - Zone.Center;
                d.Y = 0f;
                if (d.Length() <= ZoneRadius) InZone[c.Team]++;
            }
            int best = InZone.Max();
            Holder = best > 0 && InZone.Count(n => n == best) == 1 ? Array.IndexOf(InZone, best) : -1;
            if (Holder >= 0) Score[Holder]++;
            for (int t = 0; t < 3; t++)
            {
                if (Score[t] < TargetScore) continue;
                Winner = t;
                _hud.Center($"{TeamNames[t]} WINS\n{TeamNames[0]} {Score[0]} · {TeamNames[1]} {Score[1]} · {TeamNames[2]} {Score[2]}", 30f);
                Log($"[{now:0}s] {TeamNames[t]} wins {Score[0]}-{Score[1]}-{Score[2]}");
            }
        }

        if (now >= ZoneMovesAt) MoveZone();

        if (DuelMode.Verbose && now >= _nextDump)
        {
            _nextDump = now + 60.0;
            Log($"[{now:0}s] score {Score[0]}-{Score[1]}-{Score[2]}  in zone {InZone[0]}/{InZone[1]}/{InZone[2]}  alive {AliveOn(0)}/{AliveOn(1)}/{AliveOn(2)}  " +
                $"states {string.Join(" ", Bots.Where(b => b.Alive).GroupBy(b => b.Brain.State).Select(g => $"{g.Key}:{g.Count()}"))}");
            for (int t = 0; t < 3; t++)
            {
                var mine = Bots.Where(b => b.Alive && b.Team == t).ToList();
                if (mine.Count == 0) continue;
                float avg = mine.Average(b => (b.FeetPos - Zone.Center with { Y = b.FeetPos.Y }).Length());
                Log($"     {TeamNames[t],-7} avg {avg:0} m from zone, rally {(Rally[t] - Zone.Center with { Y = Rally[t].Y }).Length():0} m; " +
                    string.Join(" ", mine.GroupBy(b => b.Brain.State).Select(g => $"{g.Key}:{g.Count()}")) + "; notes: " +
                    string.Join(", ", mine.Select(b => b.Brain.Note).Distinct().Take(4)));
            }
        }
    }

    void OnKilled(ICombatant victim, HitInfo hit)
    {
        string killer = hit.Shooter?.Callsign ?? "?";
        string zone = hit.Zone.ToString().ToLowerInvariant();
        _hud.AddKill(hit.Shooter, victim, $"{zone}, {hit.Distance:0} m");
        bool tk = hit.Shooter != null && hit.Shooter != victim && hit.Shooter.Team == victim.Team;
        Log($"[{Clock.Now:0}s] {killer} killed {victim.Callsign} — {zone}, {hit.Distance:0} m{(tk ? "  TEAMKILL" : "")}");

        if (victim is Bot b)
        {
            _respawns.Add((b.P, b.Team, Clock.Now + RespawnDelay));
            // The body stays where it fell — up to a point.
            _corpses.Enqueue(b);
            while (_corpses.Count > CorpseCap)
            {
                var old = _corpses.Dequeue();
                Bots.Remove(old);
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
        // Only what you'd actually hear on the radio: your side, within a sensible range.
        var me = PlayerBody != null && PlayerBody.Alive ? PlayerBody.FeetPos : Spec.Target?.FeetPos ?? who.FeetPos;
        if (who.FeetPos.DistanceTo(me) < 300f) _hud.AddComm($"{who.Callsign}: {text}");
    }

    /// <summary>Diagnostics: how expensive are navigation queries on this map?</summary>
    void BenchNav()
    {
        var map = Map.GetWorld3D().NavigationMap;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) NavigationServer3D.MapGetClosestPoint(map, new Vector3(_rng.RandfRange(-600, 600), 50f, _rng.RandfRange(-600, 600)));
        double cp = sw.Elapsed.TotalMilliseconds / 100;
        sw.Restart();
        int n = 0;
        for (int i = 0; i < 10; i++) n += NavigationServer3D.MapGetPath(map, Rally[i % 3], Zone.Center, true).Length;
        double path = sw.Elapsed.TotalMilliseconds / 10;
        Log($"   nav bench: closest point {cp:0.00} ms, path rally->zone {path:0.0} ms ({n / 10} corners)");
        int polys = 0, regions = 0;
        foreach (var r in Map.Nav.GetChildren().OfType<NavigationRegion3D>()) { regions++; polys += r.NavigationMesh.GetPolygonCount(); }
        var probe = NavigationServer3D.MapGetClosestPoint(map, Zone.Center + Vector3.Up * 2f);
        Log($"   nav: {regions} regions, {polys} polygons; zone centre {Zone.Center} snaps to {probe}; rally0 {Rally[0]}");
    }

    static void Log(string s)
    {
        if (DuelMode.Verbose) GD.Print(s);
    }
}
