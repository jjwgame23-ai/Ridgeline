using Godot;

namespace Ridgeline;

/// <summary>
/// `-- mode=assault [seed=2] [runs=20] [attack=9] [defend=3] [arty=0]`: a test bench for the war's fights. On island N,
/// BRAVO's first <c>defend</c> rifle companies dig in round the town nearest the island's middle, and ALPHA's first
/// <c>attack</c> rifle companies set off at it from 3 km out; everyone else stands aside. The same attack is fought
/// <c>runs</c> times with fresh dice, and the report gives what it cost each side, how often it carried the town and
/// how long it took, against the historical marks for a battalion attack on a prepared company position.
/// </summary>
public static class AssaultTest
{
    public static void Run(Node from, Dictionary<string, string> a)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        int Arg(string k, int d) => a.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : d;
        int seed = Arg("seed", 2), runs = Arg("runs", 20), attack = Arg("attack", 9), defend = Arg("defend", 3);
        bool arty = Arg("arty", 0) != 0;
        string dir = Path.Combine(ProjectSettings.GlobalizePath("res://"), "worldgen");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"assault-{seed}.txt");
        var lines = new List<string>();
        void Say(string s)
        {
            GD.Print(s);
            lines.Add(s);
            File.WriteAllLines(path, lines);
        }
        var isl = IslandGen.Generate(seed, 1280, 128_000f, IslandClimate.Mediterranean, _ => { });
        var results = new List<(float AttLost, float DefLost, float AttKilled, float DefKilled, bool Carried, double Minutes, long Shots, int Hits, int Close)>();
        string where = "";
        for (int r = 0; r < runs; r++)
        {
            var war = new War(isl, seed * 1000 + r) { Trace = Arg("trace", -1) };
            war.Raise(new Random(seed * 31 + 7));
            Artillery.Setup(war);
            Supply.Setup(war);
            bool Rifles(Unit u) => u.IsMover && u.Echelon == Echelon.Company && u.Arm is Arm.Mechanised or Arm.Infantry or Arm.Motorised;
            var def = war.Units.Where(u => u.Side == 1 && Rifles(u)).Take(defend).ToList();
            var att = war.Units.Where(u => u.Side == 0 && Rifles(u)).Take(attack).ToList();
            var obj = war.Objectives.Where(o => o.Kind == ObjKind.Town).OrderBy(o => o.X * o.X + o.Z * o.Z).First();
            where = obj.Name;
            // Everyone else stands aside: nobody to see or fight.
            var keep = new HashSet<int>(def.Concat(att).Select(u => u.Id));
            // With arty=1, both sides' battalion mortars: those that keep near a headquarters company of a battalion in
            // the fight (US, British), or that are a battery of one (Russian).
            var mortars = new List<(Unit U, int Side)>();
            if (arty)
            {
                var bns = new HashSet<int>(def.Concat(att).Select(u => u.Parent));
                foreach (int id in war.FireUnits)
                {
                    var fu = war.Units[id];
                    if (fu.Fires != VClass.Mortar) continue;
                    int bn = fu.Keeps >= 0 ? war.Units[fu.Keeps].Parent : fu.Parent;
                    if (!bns.Contains(bn)) continue;
                    keep.Add(id);
                    mortars.Add((fu, fu.Side));
                }
            }
            foreach (var u in war.Units)
                if (u.IsMover && !keep.Contains(u.Id)) u.People = 0;
            // The defence: a ring round the town, dug in for days.
            for (int i = 0; i < def.Count; i++)
            {
                float ang = MathF.PI + (i - (def.Count - 1) / 2f) * 0.9f, rr = obj.Radius + 300f;
                Place(def[i], obj.X + MathF.Cos(ang) * rr, obj.Z + MathF.Sin(ang) * rr);
                def[i].HaltedAt = -1e9;
            }
            // The attack: from 3 km west, companies 500 m apart, each for its own point on the ring.
            for (int i = 0; i < att.Count; i++)
            {
                float lat = (i - (att.Count - 1) / 2f) * 500f;
                Place(att[i], obj.X - 3000f, obj.Z + lat);
                float ang = MathF.PI + (i - (att.Count - 1) / 2f) * 0.25f;
                att[i].Order = new Order { Kind = OrderKind.Move, X = obj.X + MathF.Cos(ang) * 200f, Z = obj.Z + MathF.Sin(ang) * 200f };
                war.StartMove(att[i], att[i].Order.X, att[i].Order.Z);
            }
            // The mortars set up 1.5 km behind each side's line.
            for (int i = 0; i < mortars.Count; i++)
            {
                var (mu, side) = mortars[i];
                Place(mu, side == 0 ? obj.X - 4500f : obj.X + 1500f, obj.Z + (i % 4 - 1.5f) * 400f);
                mu.Keeps = -1;
            }
            float attStart = att.Sum(u => u.People), defStart = def.Sum(u => u.People);
            const double Dt = 60;
            for (int step = 1; step <= 8 * 60; step++)
            {
                war.Step(Dt);
                int t = (int)war.Time;
                Combat.Detect(war, t % 300 == 0);
                Combat.Step(war, Dt);
                if (arty) Artillery.Step(war);
                // The attackers press on: a company stopped short of its point on the ring sets off again every half
                // hour, as a battalion renews an attack until the place falls.
                if (t % 1800 == 0)
                    foreach (var u in att)
                        if (u.People > 0 && u.InFight < 0 && u.Path == null && u.Order != null
                            && (u.X - u.Order.X) * (u.X - u.Order.X) + (u.Z - u.Order.Z) * (u.Z - u.Order.Z) > 400f * 400f)
                        {
                            float ox = u.Order.X, oz = u.Order.Z;
                            u.Order = new Order { Kind = OrderKind.Move, X = ox, Z = oz };
                            war.StartMove(u, ox, oz);
                        }
                if (step > 60 && war.Fights.All(f => f.Over) && (def.All(u => u.People * 2 <= u.Raised || u.Order is { Tactical: true }) || att.All(u => u.People * 2 <= u.Raised))) break;
            }
            int Lost(int side) => war.Fights.Sum(f => f.Killed[side] + f.Down[side] + f.Hurt[side]);
            int Killed(int side) => war.Fights.Sum(f => f.Killed[side]);
            // Carried: no defending company still standing on the town with more than half its men, and at least as many
            // attackers there as defenders.
            int Near(Unit u) => u.People > 0 && (u.X - obj.X) * (u.X - obj.X) + (u.Z - obj.Z) * (u.Z - obj.Z) < Sq(obj.Radius + 800f) ? u.People : 0;
            // Only the defending companies that were attacked count: one on the far side of the town, never reached by an
            // attack from one side, doesn't make it held.
            var fought = new HashSet<int>(war.Fights.SelectMany(f => f.Movers));
            bool held = def.Any(u => fought.Contains(u.Id) && Near(u) * 2 > u.Raised);
            bool there = att.Sum(Near) > def.Where(u => fought.Contains(u.Id)).Sum(Near);
            double first = war.Fights.Count > 0 ? war.Fights.Min(f => f.Started) : 0, last = war.Fights.Count > 0 ? war.Fights.Max(f => f.Over ? f.Ended : war.Time) : 0;
            int hits = war.HitsAt.Sum(), close = war.HitsAt[0] + war.HitsAt[1] + war.HitsAt[2];
            results.Add((Lost(0) / MathF.Max(1f, attStart), Lost(1) / MathF.Max(1f, defStart), Killed(0) / MathF.Max(1f, attStart), Killed(1) / MathF.Max(1f, defStart),
                there && !held, (last - first) / 60, war.Fights.Sum(f => (long)f.Shots.Sum()), hits, close));
            foreach (var l in war.TraceLines) Say(l);
            foreach (var f in war.Fights)
                Say($"    fight {f.Id}: {f.Movers.Count} companies ({string.Join(" ", f.Movers.Select(id => war.Units[id].Side == 0 ? "A" : "B"))}), {(f.Over ? f.Ended - f.Started : 0) / 60:0} min, opened {f.Opened:0} m");
            var x = results[^1];
            Say($"run {r + 1,2}: attackers {attStart:0} lost {x.AttLost:P0} ({x.AttKilled:P0} killed), defenders {defStart:0} lost {x.DefLost:P0} ({x.DefKilled:P0} killed); "
                + $"{(x.Carried ? "carried" : "held")}; fought {x.Minutes:0} min in {war.Fights.Count} fights");
        }
        Say("");
        Say($"ASSAULT on {where}: {attack} ALPHA companies against {defend} BRAVO companies dug in{(arty ? ", with mortars" : "")}, {runs} runs");
        Say($"  attackers lost {results.Average(x => x.AttLost):P0} on average ({results.Average(x => x.AttKilled):P0} killed); defenders {results.Average(x => x.DefLost):P0} ({results.Average(x => x.DefKilled):P0} killed)");
        Say($"  carried {results.Count(x => x.Carried)} times in {runs}; fighting lasted a median {results.Select(x => x.Minutes).OrderBy(m => m).ElementAt(runs / 2):0} min");
        long shots = results.Sum(x => x.Shots);
        int allHits = results.Sum(x => x.Hits);
        Say($"  {shots / Math.Max(1, allHits):N0} rounds a hit; {100.0 * results.Sum(x => x.Close) / Math.Max(1, allHits):0}% of hits inside 100 m");
        Say("  (marks: a battalion attack at three to one on a prepared company position cost attackers about 5-15% and defenders");
        Say("  more, took hours, and carried it somewhat more often than not; Dupuy's battalion-level attack rates run 5-15% a day)");
        from.GetTree().Quit();
    }

    static float Sq(float x) => x * x;

    static void Place(Unit u, float x, float z)
    {
        u.X = u.WasX = x;
        u.Z = u.WasZ = z;
        u.Path = null;
        u.Order = null;
        u.Next = null;
    }
}
