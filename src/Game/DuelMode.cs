using Godot;

namespace Ridgeline;

public sealed class GameSetup
{
    public string Map = "range";   // "range", "arena" or "valley"
    public string Mode = "territory"; // on the valley: "territory" or "koth"
    public int TeamSize = 1;
    public bool PlayerJoins = true;
    public int Seed = 20260923;
    /// <summary>Which battlefield (a MapSpec id); empty = the one picked in the menu.</summary>
    public string MapId = "";
}

/// <summary>
/// Team-elimination rounds on the arena. Alpha spawns west, Bravo east. The same
/// named mercs fight every round, so you get to know them. If you're not
/// playing (or you're dead) the camera spectates the bots.
/// </summary>
public partial class DuelMode : Node, IMatch
{
    public static bool Verbose; // log rounds and kills to stdout (headless test runs)
    public static readonly string[] TeamNames = { "ALPHA", "BRAVO" };
    const double RoundLimit = 360;

    public Arena Arena = null!;
    public GameSetup Setup = null!;
    public Hud PlayerHud = null!;

    public readonly int[] Score = new int[2];
    public int Round { get; private set; }
    public double RoundStart { get; private set; }
    public Spectator Spec { get; private set; } = null!;
    public List<Bot> Bots { get; } = new();
    public Player? PlayerBody { get; private set; }

    readonly List<Personality>[] _roster = { new(), new() };
    readonly RandomNumberGenerator _rng = new();
    DuelHud _hud = null!;
    bool _roundOver;
    double _nextRoundAt, _playerDiedAt = -1, _nextDumpAt;

    public override void _Ready()
    {
        _rng.Randomize();
        var names = Personality.Callsigns.OrderBy(_ => _rng.Randi()).ToList();
        int k = 0;
        for (int team = 0; team < 2; team++)
        {
            int n = team == 0 && Setup.PlayerJoins ? Setup.TeamSize - 1 : Setup.TeamSize;
            for (int i = 0; i < n; i++) _roster[team].Add(Personality.Roll(_rng, names[k++]));
        }
        BotBrain.Waypoints = Arena.InterestPoints;
        BotBrain.DefaultObjective = null;

        Spec = new Spectator { Mode = this };
        AddChild(Spec);
        _hud = new DuelHud { Mode = this };
        AddChild(_hud);
        AddChild(new BotDebugDraw { Mode = this });

        Combatants.Killed += OnKilled;
        Comms.Said += OnSaid;
        StartRound();
    }

    public override void _ExitTree()
    {
        Combatants.Killed -= OnKilled;
        Comms.Said -= OnSaid;
    }

    void StartRound()
    {
        Round++;
        foreach (var b in Bots) b.QueueFree();
        Bots.Clear();
        PlayerBody?.QueueFree();
        PlayerBody = null;
        PlayerHud.P = null;
        _playerDiedAt = -1;
        // Clean slate: nothing from the last round (sounds, rounds in the air) may reach the new one.
        SoundWorld.I.History.Clear();
        Ballistics.I.Clear();
        GetTree().CallGroup("grenades", Node.MethodName.QueueFree);

        var world = GetParent<Node3D>();
        for (int team = 0; team < 2; team++)
        {
            var spawns = Arena.Spawns[team].OrderBy(_ => _rng.Randi()).ToList();
            int s = 0;
            float faceYaw = team == 0 ? -90f : 90f; // face the enemy side
            if (team == 0 && Setup.PlayerJoins)
            {
                var p = new Player { TeamId = 0 };
                world.AddChild(p);
                p.GlobalPosition = spawns[s++] + Vector3.Up * 0.2f;
                p.SetYaw(faceYaw);
                PlayerBody = p;
                PlayerHud.P = p;
            }
            foreach (var pers in _roster[team])
            {
                var b = new Bot
                {
                    TeamId = team, P = pers,
                    Def = pers.Marksman ? WeaponDef.Marksman : WeaponDef.Carbine,
                };
                world.AddChild(b);
                b.GlobalPosition = spawns[s++ % spawns.Count] + new Vector3(_rng.RandfRange(-1f, 1f), 0.1f, _rng.RandfRange(-1f, 1f));
                b.Aim.Yaw = faceYaw;
                Bots.Add(b);
            }
        }

        if (PlayerBody != null) Spec.Deactivate();
        else Spec.Activate(Bots.FirstOrDefault(b => b.Team == 0));

        RoundStart = Clock.Now;
        _nextDumpAt = RoundStart + 30.0;
        _roundOver = false;
        _hud.Center($"Round {Round}", 2.5f);
        Log($"--- round {Round} ({Setup.TeamSize}v{Setup.TeamSize}) ---");
    }

    public int AliveOn(int team) => Combatants.All.Count(c => c.Team == team && c.Alive);

    public override void _Process(double delta)
    {
        double now = Clock.Now;

        if (PlayerBody != null && !PlayerBody.Alive && _playerDiedAt > 0 && now - _playerDiedAt > 2.5 && !Spec.Active)
        {
            PlayerHud.P = null;
            Spec.Activate(Bots.FirstOrDefault(b => b.Alive && b.Team == 0) ?? Bots.FirstOrDefault(b => b.Alive));
        }

        if (!_roundOver)
        {
            int a = AliveOn(0), b = AliveOn(1);
            bool timeUp = now - RoundStart > RoundLimit;
            if (a == 0 || b == 0 || timeUp)
            {
                _roundOver = true;
                _nextRoundAt = now + 6.0;
                string msg;
                if (a > 0 && b == 0) { Score[0]++; msg = $"{TeamNames[0]} wins round {Round}"; }
                else if (b > 0 && a == 0) { Score[1]++; msg = $"{TeamNames[1]} wins round {Round}"; }
                else msg = $"Round {Round}: draw";
                _hud.Center($"{msg}\n{TeamNames[0]} {Score[0]} — {Score[1]} {TeamNames[1]}", 5.5f);
                Log($"{msg} after {now - RoundStart:0.0}s — score {Score[0]}-{Score[1]}");
                foreach (var bot in Bots)
                    Log($"   {bot.Callsign,-8} team {bot.Team} skill {bot.P.Skill:0.00} aggr {bot.P.Aggression:0.00} " +
                        $"{(bot.Alive ? $"alive {bot.Health:0}hp" : "dead")}  shots {bot.ShotsFired} hits {bot.HitsLanded} blocked {bot.ShotsBlocked} near {bot.ShotsBlockedNear} wide {bot.ShotsWide} nades {2 - bot.Grenades} kills {bot.Kills}");
            }
        }
        else if (now >= _nextRoundAt) StartRound();

        if (Verbose && !_roundOver && now >= _nextDumpAt)
        {
            _nextDumpAt = now + 30.0;
            foreach (var bot in Bots.Where(b => b.Alive))
                Log($"   [{now - RoundStart:0}s] {bot.Callsign,-8} t{bot.Team} at ({bot.FeetPos.X:0},{bot.FeetPos.Z:0}) {bot.Brain.State} '{bot.Brain.Note}' " +
                    $"target {bot.Brain.Target?.Who.Callsign ?? "-"} moving {bot.Moving} arrived {bot.Arrived}");
        }
    }

    void OnKilled(ICombatant victim, HitInfo hit)
    {
        string killer = hit.Shooter?.Callsign ?? "?";
        string zone = hit.Zone.ToString().ToLowerInvariant();
        _hud.AddKill(hit.Shooter, victim, $"{zone}, {hit.Distance:0} m");
        bool tk = hit.Shooter != null && hit.Shooter != victim && hit.Shooter.Team == victim.Team;
        Log($"{Clock.Now - RoundStart,6:0.0}s  {killer} ({hit.Weapon}) killed {victim.Callsign} — {zone}, {hit.Distance:0} m{(tk ? "  TEAMKILL" : "")}");

        if (victim == PlayerBody)
        {
            _playerDiedAt = Clock.Now;
            _hud.Center($"Killed by {killer} — {zone}, {hit.Distance:0} m", 3f);
        }

        // Someone on the victim's team calls it and shares where the shot came from.
        var mate = Bots.Where(b => b.Alive && b.Team == victim.Team && b != victim).OrderBy(b => b.FeetPos.DistanceTo(victim.FeetPos)).FirstOrDefault();
        if (mate != null && mate.FeetPos.DistanceTo(victim.FeetPos) < 80f)
        {
            Comms.Say(mate, $"Man down! {victim.Callsign} is down!");
            if (hit.Shooter is { Alive: true } s) mate.Senses.Alert(s, 0.2f);
        }
    }

    void OnSaid(ICombatant who, string text)
    {
        int watchTeam = PlayerBody != null ? PlayerBody.Team : Spec.Target?.Team ?? 0;
        if (who.Team == watchTeam) _hud.AddComm($"{who.Callsign}: {text}");
    }

    static void Log(string s)
    {
        if (Verbose) GD.Print(s);
    }
}
