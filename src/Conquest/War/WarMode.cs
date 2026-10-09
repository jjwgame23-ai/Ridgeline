using System.Diagnostics;
using System.Text;
using Godot;

namespace Ridgeline;

/// <summary>
/// `-- mode=war [seed=1] [days=7] [size=1280] [out=worldgen] [trace=fight]`: make the island, raise the three armies on it, and run
/// the war for some days. It writes what was raised and where it started (war-N-report.txt, war-N-start.png) and a
/// replay to watch (war-N.html), then quits. The war advances a minute at a time. Orders are looked at every 10
/// minutes and who holds the ground every 30. The replay gets a frame every hour.
/// </summary>
public static class WarMode
{
    public static bool RunIfRequested(Node from)
    {
        var a = new Dictionary<string, string>();
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            var kv = arg.Split('=', 2);
            a[kv[0]] = kv.Length > 1 ? kv[1] : "";
        }
        if (!a.TryGetValue("mode", out var mode) || mode != "war") return false;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        int seed = a.TryGetValue("seed", out var s) && int.TryParse(s, out var sv) ? sv : 1;
        int n = a.TryGetValue("size", out var z) && int.TryParse(z, out var zv) ? Math.Max(64, zv / 4 * 4) : 1280;
        string dir = a.TryGetValue("out", out var o) && o != "" ? o : "worldgen";
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(ProjectSettings.GlobalizePath("res://"), dir);
        Directory.CreateDirectory(dir);

        string stem = Path.Combine(dir, $"war-{seed}");
        // Progress goes straight to a file as well: Godot's stdout is only flushed at exit when it's redirected.
        string progress = stem + "-progress.txt";
        File.WriteAllText(progress, "");
        var clock = Stopwatch.StartNew();
        void Note(string msg)
        {
            GD.Print(msg);
            File.AppendAllText(progress, $"{clock.Elapsed.TotalSeconds,7:0.0} s  {msg}{System.Environment.NewLine}");
        }
        var isl = IslandGen.Generate(seed, n, 128_000f, IslandClimate.Mediterranean, Note);
        var sw = Stopwatch.StartNew();
        var war = new War(isl, seed) { Note = Note, Trace = a.TryGetValue("trace", out var tr) && int.TryParse(tr, out var tv) ? tv : -1 };
        war.Raise(new Random(seed * 31 + 7));
        GD.Print($"war: raised {war.SoldierCount:N0} soldiers, {war.Units.Count:N0} units, {war.Vehicles.Count:N0} vehicles in {sw.Elapsed.TotalSeconds:0.0} s");
        DrawStart(war, stem + "-start.png");
        int days = a.TryGetValue("days", out var dy) && int.TryParse(dy, out var dv) ? Math.Max(0, dv) : 7;
        var rec = new WarRecord(war);
        rec.Frame();
        rec.Ground();
        var daily = new List<(int Day, float[] Share, int[] Held, int[] Dead, int[] Evacuated)>();
        var marches = new List<(Mobility Mob, double Km)>();
        var run = Stopwatch.StartNew();
        // Where the time goes, for the daily note.
        var clocks = new Dictionary<string, Stopwatch> { ["march"] = new(), ["contact"] = new(), ["fights"] = new(), ["command"] = new(), ["ground"] = new() };
        void Timed(string k, Action a)
        {
            clocks[k].Start();
            a();
            clocks[k].Stop();
        }
        const double Dt = 60;
        for (int step = 1; step <= days * 86400 / (int)Dt; step++)
        {
            int dayBefore = war.Day;
            Timed("march", () => war.Step(Dt));
            int t = (int)war.Time;
            Timed("contact", () => Combat.Detect(war, t % 300 == 0));
            Timed("fights", () => Combat.Step(war, Dt));
            if (t % 600 == 0) Timed("command", () => Command.Think(war));
            if (t % 1800 == 0)
                Timed("ground", () =>
                {
                    war.UpdateControl();
                    war.Intel.Forget(war.Time);
                });
            if (t % 3600 == 0) rec.Frame();
            if (t % 10800 == 0) rec.Ground();
            if (t % 10800 == 0)
                Note($"war: day {war.Day} {war.Hour:00}:00 ({run.Elapsed.TotalSeconds:0.0} s: " + string.Join(", ", clocks.Select(c => $"{c.Key} {c.Value.Elapsed.TotalSeconds:0}"))
                     + $"; {war.Fights.Count} fights, {war.Fights.Count(f => !f.Over)} going on, {war.Fights.Where(f => !f.Over).Sum(f => f.F.Count)} fighters in them; dead {war.Dead.Sum()})");
            if (war.Day != dayBefore)
            {
                foreach (var u in war.Units)
                {
                    if (!u.IsMover) continue;
                    if (u.MarchedToday > 500) marches.Add((u.Mob, u.MarchedToday / 1000.0));
                    u.MarchedToday = 0;
                }
                var share = war.Sides.Select(sd => war.Ctl.Share(sd.Index)).ToArray();
                var held = war.Sides.Select(sd => war.Objectives.Count(o => o.Owner == sd.Index)).ToArray();
                daily.Add((dayBefore, share, held, (int[])war.Dead.Clone(), (int[])war.Evacuated.Clone()));
                Note($"war: day {dayBefore} done ({run.Elapsed.TotalSeconds:0.0} s: " + string.Join(", ", clocks.Select(c => $"{c.Key} {c.Value.Elapsed.TotalSeconds:0}"))
                     + $"; {war.Fights.Count} fights so far, {war.Fights.Count(f => !f.Over)} going on): "
                     + string.Join(", ", war.Sides.Select(sd => $"{sd.Name} {share[sd.Index]:P0} of the land, {held[sd.Index]} objectives, {war.Dead[sd.Index]} dead")));
            }
        }
        string report = Orbat(war) + Summary(war, daily, marches, run.Elapsed.TotalSeconds);
        if (war.TraceLines.Count > 0) report += $"FIGHT {war.Trace}, every 30 s:\n" + string.Join("\n", war.TraceLines) + "\n";
        File.WriteAllText(stem + "-report.txt", report);
        GD.Print(report);
        IslandRender.Draw(isl).Save(stem + "-map.png");
        rec.Write(stem + "-map.png", stem + ".html", report);
        Note($"war: wrote {stem}-report.txt, -start.png and .html");
        from.GetTree().Quit();
        return true;
    }

    /// <summary>
    /// How the war went: ground and objectives held each day, how far units marched in a day against doctrine's march
    /// rates, and the main places taken.
    /// </summary>
    static string Summary(War war, List<(int Day, float[] Share, int[] Held, int[] Dead, int[] Evacuated)> daily, List<(Mobility Mob, double Km)> marches, double seconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"THE WAR: {war.Day - 1} days, run in {seconds:0} s ({war.Time / Math.Max(1e-3, seconds):0}x real time)");
        sb.AppendLine("  Day   " + string.Join("  ", war.Sides.Select(s => $"{s.Name,-34}")));
        foreach (var (d, share, held, dead, evac) in daily)
            sb.AppendLine($"  {d,3}   " + string.Join("  ", war.Sides.Select(s => $"{share[s.Index],4:P0} land, {held[s.Index],3} obj., {dead[s.Index],5} dead, {evac[s.Index],5} evac.")));
        // Casualties and fights, with checks against real combat.
        var fights = war.Fights;
        if (fights.Count > 0)
        {
            var mins = fights.Select(f => ((f.Over ? f.Ended : war.Time) - f.Started) / 60.0).OrderBy(m => m).ToList();
            sb.AppendLine($"  Fights: {fights.Count}, lasting a median {mins[mins.Count / 2]:0} min (90th percentile {mins[mins.Count * 9 / 10]:0}, longest {mins[^1]:0})");
            foreach (var sd in war.Sides)
            {
                int i = sd.Index;
                long shots = fights.Sum(f => (long)f.Shots[i]);
                int killed = fights.Sum(f => f.Killed[i]), down = fights.Sum(f => f.Down[i]), hurt = fights.Sum(f => f.Hurt[i]), lost = fights.Sum(f => f.Lost[i]);
                long enemyHit = fights.Sum(f => (long)Enumerable.Range(0, 3).Where(o => o != i && f.In.Length > 0).Sum(o => f.Killed[o] + f.Down[o] + f.Hurt[o]));
                int size = war.Everyone(war.Units[sd.Army]).Count();
                double days = Math.Max(1.0 / 24, war.Time / 86400.0);
                sb.AppendLine($"    {sd.Name,-8} killed {killed}, down {down}, lightly wounded {hurt} ({100.0 * (killed + down + hurt) / size / days:0.0}% of the army a day), vehicles lost {lost}; fired {shots:N0} rounds");
                var wrecks = war.Vehicles.Where(v => v.Side == i && v.Lost).GroupBy(v => v.Class).OrderByDescending(g => g.Count());
                if (lost > 0)
                    sb.AppendLine($"             vehicles lost: " + string.Join(", ", wrecks.Select(g => $"{g.Key.ToString().ToLowerInvariant()} {g.Count()}"))
                        + "; to " + string.Join(", ", Enum.GetValues<Cause>().Where(c => war.Wrecked[i, (int)c] > 0).Select(c => $"{c.ToString().ToLowerInvariant()} {war.Wrecked[i, (int)c]}")));
            }
            int takes = war.Events.Count(e => e.Text.Contains(" takes "));
            sb.AppendLine($"    Places changing hands: {takes / Math.Max(1.0, war.Time / 86400.0):0} a day");
            foreach (var sd in war.Sides)
            {
                var bns = war.Below(war.Units[sd.Army]).Where(u => u.Echelon == Echelon.Battalion && Command.Manoeuvre(u)).ToList();
                int holding = bns.Count(u => u.Holds >= 0), going = bns.Count(u => u.Holds < 0 && (u.Next != null || u.Order is { Kind: OrderKind.Occupy, Done: false }));
                int weak = bns.Count(u => u.Holds < 0 && Command.Strength(war, u) < 0.5f), resting = bns.Count(u => u.Holds < 0 && war.Time < u.RestUntil);
                sb.AppendLine($"      {sd.Name} battalions at the end: {bns.Count}: {holding} holding, {going} on an operation, {resting} resting, {weak} below half strength, "
                              + $"{bns.Count - holding - going} otherwise idle; mean strength {bns.Average(u => Command.Strength(war, u)):P0}");
                var objs = war.Objectives;
                sb.AppendLine($"        objectives: {objs.Count(o => o.Owner < 0)} unheld, {objs.Count(o => o.Owner >= 0 && o.Owner != sd.Index)} the enemy's; "
                              + $"{objs.Count(o => o.Unreachable[sd.Index])} marked out of reach, {objs.Count(o => war.Time < o.Retry[sd.Index])} waiting to retry, "
                              + $"{objs.Count(o => o.Claims[sd.Index] > 0)} claimed; tasks: {string.Join(" ", war.Below(war.Units[sd.Army]).Where(u => u.Echelon == Echelon.Division).Select(d => d.Tasks.Count))}; "
                              + $"offensive against {(sd.Offensive >= 0 ? war.Sides[sd.Offensive].Name : "nobody")}");
            }
            sb.AppendLine("    (real, after Dupuy: divisions in battle lost 1-3% a day, whole armies well under 1%, and tanks went at four to");
            sb.AppendLine("    seven times the rate of the soldiers)");
            // Where the casualties come from: the range of the hits, how big the fights were, how often a company fought.
            int[] at = war.HitsAt;
            int allAt = Math.Max(1, at.Sum());
            string[] bands = { "<25 m", "25-50", "50-100", "100-200", "200-400", "400-800", ">800" };
            sb.AppendLine("    Hits by range: " + string.Join(", ", bands.Select((b, k) => $"{b} {100.0 * at[k] / allAt:0}%")));
            var cas = fights.Select(f => f.Killed.Sum() + f.Down.Sum() + f.Hurt.Sum()).ToList();
            int casAll = Math.Max(1, cas.Sum());
            (int Lo, int Hi)[] sizes = { (0, 0), (1, 5), (6, 20), (21, 50), (51, 100), (101, int.MaxValue) };
            sb.AppendLine("    Fights by casualties: " + string.Join(", ", sizes.Select(r =>
            {
                var these = cas.Where(c => c >= r.Lo && c <= r.Hi).ToList();
                string label = r.Hi == int.MaxValue ? $">{r.Lo - 1}" : r.Lo == r.Hi ? $"{r.Lo}" : $"{r.Lo}-{r.Hi}";
                return $"{label}: {these.Count} fights ({100.0 * these.Sum() / casAll:0}% of casualties)";
            })));
            var times = fights.SelectMany(f => f.Movers).GroupBy(id => id).Select(g => g.Count()).OrderBy(n => n).ToList();
            var arms = fights.SelectMany(f => f.Movers).Distinct().GroupBy(id => Command.Manoeuvre(war.Units[id]) ? "combat" : "support")
                .Select(g => $"{g.Count()} {g.Key}");
            sb.AppendLine("    The biggest fights:");
            foreach (var f in fights.OrderByDescending(f => f.Killed.Sum() + f.Down.Sum() + f.Hurt.Sum()).Take(8))
            {
                var sides = Enumerable.Range(0, 3).Where(s => f.Movers.Any(id => war.Units[id].Side == s));
                string who = string.Join("; ", sides.Select(s =>
                {
                    var ms = f.Movers.Where(id => war.Units[id].Side == s).ToList();
                    int came = ms.Sum(id => f.Strength.GetValueOrDefault(id));
                    int veh = f.V.Where(v => v.Side == s).Select(v => v.Vehicle).Distinct().Count();
                    return $"{war.Sides[s].Name} {ms.Count} movers ({ms.Count(id => Command.Manoeuvre(war.Units[id]))} combat), {came} men, {veh} armed vehicles: "
                           + $"{f.Killed[s] + f.Down[s] + f.Hurt[s]} hit, {f.Lost[s]} vehicles lost";
                }));
                string causes = string.Join(", ", Enum.GetValues<Cause>().Where(c => f.HitBy[(int)c] + f.WreckedBy[(int)c] > 0)
                    .Select(c => $"{c.ToString().ToLowerInvariant()} {f.HitBy[(int)c]}/{f.WreckedBy[(int)c]}"));
                double end = f.Over ? f.Ended : war.Time;
                sb.AppendLine($"      #{f.Id} {f.Where} at {(6 + f.Started / 3600) % 24:00.0} h, {(end - f.Started) / 60:0} min, opened at {f.Opened:0} m: {who}");
                sb.AppendLine($"        hits/wrecks by {causes}");
            }
            foreach (var g in fights.SelectMany(f => f.Movers).GroupBy(id => id).OrderByDescending(g => g.Count()).Take(4))
            {
                var u = war.Units[g.Key];
                var fs = fights.Where(f => f.Movers.Contains(g.Key)).ToList();
                sb.AppendLine($"      most fights: {u.Name} ({war.Sides[u.Side].Name}, {u.Arm.ToString().ToLowerInvariant()}, {u.Mob.ToString().ToLowerInvariant()}): {g.Count()} fights, "
                              + $"{fs.Count(f => f.Killed.Sum() + f.Down.Sum() + f.Hurt.Sum() == 0)} with nobody hit, median {fs.Select(f => ((f.Over ? f.Ended : war.Time) - f.Started) / 60).OrderBy(m => m).ElementAt(fs.Count / 2):0} min, "
                              + $"opened median {fs.Select(f => f.Opened).OrderBy(m => m).ElementAt(fs.Count / 2):0} m");
            }
            foreach (var sd in war.Sides)
            {
                var fought = new HashSet<int>();
                foreach (var f in fights)
                    foreach (var fi in f.F)
                        if (fi.Side == sd.Index) fought.Add(fi.Soldier);
                int hit = fought.Count(s => war.Soldiers[s].State is not SoldierState.Fit);
                sb.AppendLine($"      {sd.Name}: {fought.Count} soldiers fought, {100.0 * hit / Math.Max(1, fought.Count):0}% of them hit");
            }
            sb.AppendLine($"    Movers that fought: {times.Count} ({string.Join(", ", arms)}), {times.Sum()} times in all; median {times[times.Count / 2]} fights each, 90th percentile {times[times.Count * 9 / 10]}, most {times[^1]}");
            long allShots = fights.Sum(f => (long)f.Shots.Sum());
            int allCas = fights.Sum(f => f.Killed.Sum() + f.Down.Sum() + f.Hurt.Sum());
            int allKilled = fights.Sum(f => f.Killed.Sum());
            sb.AppendLine($"    Rounds fired per casualty: {(allCas > 0 ? allShots / allCas : 0):N0}. Killed to wounded: 1 to {(allKilled > 0 ? (double)(allCas - allKilled) / allKilled : 0):0.0}.");
            foreach (var sd in war.Sides)
                sb.AppendLine($"    {sd.Name,-8} hit by: " + string.Join(", ", Enum.GetValues<Cause>().Where(c => war.Hits[sd.Index, (int)c] + war.Kills[sd.Index, (int)c] > 0)
                    .Select(c => $"{c.ToString().ToLowerInvariant()} {war.Hits[sd.Index, (int)c]} ({war.Kills[sd.Index, (int)c]} killed)")));
            sb.AppendLine("    (real: WWII armies fired tens of thousands of rounds of all kinds per casualty; killed to wounded ran about 1 to 3 in");
            sb.AppendLine("    WWII and 1 to 5-8 in recent wars with fast evacuation)");
        }
        sb.AppendLine("  Daily marches (units that moved that day):");
        // Doctrine: a normal foot march is 20–32 km a day, a forced march up to 56 (FM 3-21.18); a motorised column
        // makes 150–250 km a day on roads (FM 55-30).
        foreach (var g in marches.GroupBy(m => m.Mob))
        {
            var km = g.Select(m => m.Km).OrderBy(k => k).ToList();
            sb.AppendLine($"    {g.Key.ToString().ToLowerInvariant(),-8} {km.Count,5} unit-days: median {km[km.Count / 2]:0.0} km, 90th percentile {km[km.Count * 9 / 10]:0.0} km, most {km[^1]:0.0} km");
        }
        sb.AppendLine("    (doctrine: on foot 20-32 km a day normally, 56 at most forced; motorised columns 150-250 km on roads)");
        sb.AppendLine($"  Places taken: {war.Events.Count} events; main ones:");
        foreach (var e in war.Events.Where(e => war.Objectives.Any(o => e.Text.EndsWith(o.Name) && o.Kind is ObjKind.Port or ObjKind.Node)).Take(30))
            sb.AppendLine($"    day {1 + (int)((6 + e.T / 3600) / 24)} {(6 + e.T / 3600) % 24:00}:00  {e.Text}");
        sb.AppendLine();
        return sb.ToString();
    }

    static string Orbat(War war)
    {
        var sb = new StringBuilder();
        foreach (var side in war.Sides)
        {
            var army = war.Units[side.Army];
            var people = war.Everyone(army).ToList();
            var units = war.Below(army).Prepend(army).ToList();
            int combat = people.Count(p => war.Units[war.Soldiers[p].Unit].Arm is Arm.Infantry or Arm.Mechanised or Arm.Motorised or Arm.Armour or Arm.Recon);
            sb.AppendLine($"{side.Name}: {army.Name}, commanded by {war.Who(army.Commander)}");
            sb.AppendLine($"  {people.Count:N0} soldiers, {100.0 * combat / people.Count:0}% in combat arms (infantry, armour, recon)");
            sb.AppendLine("  Units: " + string.Join(", ", Enum.GetValues<Echelon>().Reverse().Select(e => $"{units.Count(u => u.Echelon == e)} {e.ToString().ToLowerInvariant()}")));
            var byArm = people.GroupBy(p => war.Units[war.Soldiers[p].Unit].Arm).OrderByDescending(g => g.Count());
            sb.AppendLine("  By arm: " + string.Join(", ", byArm.Select(g => $"{g.Key.ToString().ToLowerInvariant()} {g.Count():N0}")));
            var veh = units.SelectMany(u => u.Vehicles).Select(v => war.Vehicles[v]).GroupBy(v => v.Class).OrderByDescending(g => g.Count());
            sb.AppendLine("  Vehicles: " + string.Join(", ", veh.Select(g => $"{g.Key.ToString().ToLowerInvariant()} {g.Count()}")));
            sb.AppendLine("  Ranks: " + string.Join(", ", people.GroupBy(p => war.Soldiers[p].Rank).OrderBy(g => g.Key)
                .Select(g => $"{People.Title(side.Index, g.Key)} {g.Count():N0}")));
            foreach (var d in army.Children.Select(c => war.Units[c]).Where(c => c.Echelon == Echelon.Division))
            {
                int dn = war.Everyone(d).Count();
                sb.AppendLine($"  {d.Name.Split(',')[0]} ({dn:N0}), {war.Who(d.Commander)}: "
                              + string.Join("; ", d.Children.Select(c => war.Units[c]).Where(c => c.Echelon == Echelon.Brigade)
                                  .Select(b => $"{b.Name.Split(',')[0]} {war.Everyone(b).Count():N0}")));
            }
            // One rifle squad from the first division, everyone in it by name.
            var squad = war.Below(army).FirstOrDefault(u => u.Echelon == Echelon.Squad && u.Arm is Arm.Infantry or Arm.Mechanised or Arm.Motorised && u.Members.Count >= 8);
            if (squad != null)
            {
                sb.AppendLine($"  e.g. {squad.Name}:");
                foreach (int m in squad.Members) sb.AppendLine($"    {war.Who(m)}, {war.Soldiers[m].Job.ToString().ToLowerInvariant()} (skill {People.Skill(war.Seed, m):0.00})");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>The starting positions on the island map: companies as dots, battalions as squares, higher headquarters as rings.</summary>
    static void DrawStart(War war, string path)
    {
        var cv = IslandRender.Draw(war.Isl);
        var dark = new Color(0.08f, 0.08f, 0.08f);
        foreach (var u in war.Units)
        {
            var col = Valley.TeamColors[u.Side];
            var (x, y) = IslandRender.Px(war.Isl, u.X, u.Z);
            switch (u.Echelon)
            {
                case Echelon.Company:
                    cv.Disc(x, y, 2.4f, dark);
                    cv.Disc(x, y, 1.7f, col);
                    break;
                case Echelon.Battalion:
                    cv.Diamond(x, y, 5f, dark);
                    cv.Diamond(x, y, 3.8f, col);
                    break;
                case Echelon.Brigade or Echelon.Division or Echelon.Army:
                    float r = u.Echelon == Echelon.Army ? 10f : u.Echelon == Echelon.Division ? 8f : 6f;
                    cv.Ring(x, y, r, 2.4f, dark);
                    cv.Ring(x, y, r, 1.4f, col);
                    break;
            }
        }
        cv.Save(path);
    }
}
