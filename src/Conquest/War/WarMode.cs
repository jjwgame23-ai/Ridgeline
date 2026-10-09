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
        if (!a.TryGetValue("mode", out var mode) || mode is not ("war" or "assault")) return false;
        try
        {
            if (mode == "assault") AssaultTest.Run(from, a);
            else Run(from, a);
        }
        catch (Exception e)
        {
            // Left to Godot, an exception would end the war half run and leave Godot idling, which looks like a hang.
            GD.PrintErr($"war: Exception: {e}");
            from.GetTree().Quit(1);
        }
        return true;
    }

    static void Run(Node from, Dictionary<string, string> a)
    {
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
        Artillery.Setup(war);
        Supply.Setup(war);
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
        var clocks = new Dictionary<string, Stopwatch> { ["march"] = new(), ["supply"] = new(), ["contact"] = new(), ["fights"] = new(), ["guns"] = new(), ["command"] = new(), ["ground"] = new() };
        void Timed(string k, Action a)
        {
            war.Doing = k;
            clocks[k].Start();
            a();
            clocks[k].Stop();
        }
        // A watchdog: if the war sits in one place for 15 s of real time, say where, so a hang can be found.
        string seen = "";
        int still = 0;
        using var watch = new System.Threading.Timer(_ =>
        {
            string now = $"{war.Time:0} {war.Doing}";
            still = now == seen ? still + 5 : 0;
            seen = now;
            if (still == 15) Note($"war: stuck at {war.Time:0} s in {war.Doing}");
        }, null, 5000, 5000);
        const double Dt = 60;
        for (int step = 1; step <= days * 86400 / (int)Dt; step++)
        {
            int dayBefore = war.Day;
            war.Tick(Dt, Timed);
            int t = (int)war.Time;
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
        return;
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
            sb.AppendLine($"    Places changing hands: {takes / Math.Max(1.0, war.Time / 86400.0):0} a day; most often "
                          + string.Join(", ", war.Objectives.OrderByDescending(o => o.Flips).Take(4).Select(o => $"{o.Name} {o.Flips}"))
                          + "; by ground alone, the new holder having there " + string.Join(", ", war.FlippedBy.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
            string[] kinds = { "planned offensives", "brigades' own attacks", "meetings on the march", "skirmishes along fronts" };
            var hitIn = new long[4];
            var count = new int[4];
            foreach (var f in fights)
            {
                hitIn[f.Kind] += f.Killed.Sum() + f.Down.Sum() + f.Hurt.Sum();
                count[f.Kind]++;
            }
            long struck = 0;
            var sfor = new long[4];
            for (int i = 0; i < 3; i++)
                for (int p = 0; p < 4; p++)
                {
                    sfor[p] += war.StruckFor[i, p];
                    struck += war.StruckFor[i, p];
                }
            long all = hitIn.Sum() + struck;
            sb.AppendLine("    Where the hits come from: " + string.Join(", ", Enumerable.Range(0, 4).Select(k => $"{kinds[k]} {100.0 * hitIn[k] / Math.Max(1, all):0}% ({count[k]} fights)"))
                          + $"; shellfire outside fights {100.0 * struck / Math.Max(1, all):0}% (on units seen {100.0 * sfor[1] / Math.Max(1, all):0}%, counter-battery {100.0 * sfor[2] / Math.Max(1, all):0}%, preparations {100.0 * sfor[3] / Math.Max(1, all):0}%)");
            static bool Dark(double t) => (6.0 + t / 3600.0) % 24.0 is < 5.5 or >= 20.5;
            var opf = fights.Where(f => f.Kind == 0).ToList();
            sb.AppendLine($"    By night (20:30-05:30): {100.0 * fights.Count(f => Dark(f.Started)) / Math.Max(1, fights.Count):0}% of fights; in the offensives {100.0 * opf.Count(f => Dark(f.Started)) / Math.Max(1, opf.Count):0}% of fights "
                          + $"and {100.0 * opf.Where(f => Dark(f.Started)).Sum(f => f.Killed.Sum() + f.Down.Sum() + f.Hurt.Sum()) / Math.Max(1, opf.Sum(f => f.Killed.Sum() + f.Down.Sum() + f.Hurt.Sum())):0}% of their casualties");
            string Split(int n)
            {
                long tot = Math.Max(1, Enumerable.Range(0, 4).Sum(k => war.OpTime[n, k]));
                string[] what = { "moving", "fighting", "asleep", "awake and halted" };
                return string.Join(", ", Enumerable.Range(0, 4).Select(k => $"{what[k]} {100.0 * war.OpTime[n, k] / tot:0}%"));
            }
            sb.AppendLine($"    An offensive's companies by day: {Split(0)}; by night: {Split(1)}");
            sb.AppendLine("    Offensives:");
            foreach (var op in war.Operations)
                sb.AppendLine($"      {war.Sides[op.Side].Name} against {war.Sides[op.Enemy].Name} near {op.Where}: planned day {1 + (int)((6 + op.Planned / 3600) / 24)}, "
                              + $"{op.Bns.Count} battalions at {op.StartStrength:P0}, in at day {1 + (int)((6 + op.HHour / 3600) / 24)} dawn; "
                              + (op.Ended < 0 ? $"still going, {op.Taken.Count} places taken" : $"{op.Outcome} after {(op.Ended - op.HHour) / 3600:0} h, {op.Taken.Count} places taken, {op.Bounds} bounds deeper, battalions at {op.EndStrength:P0}, owing {op.StartOwed:0} h of sleep at H-hour and {op.EndOwed:0} h at the end"));
            sb.AppendLine("    Artillery:");
            foreach (var sd in war.Sides)
            {
                int i = sd.Index;
                float t = war.ArtyRounds[i, 0] * 15f + war.ArtyRounds[i, 1] * 45f + war.ArtyRounds[i, 2] * (i == 1 ? 280f : 300f);
                int hit = war.Hits[i, (int)Cause.Shell] + war.Hits[i, (int)Cause.Mortar], kia = war.Kills[i, (int)Cause.Shell] + war.Kills[i, (int)Cause.Mortar];
                int allHit = Enumerable.Range(0, 12).Sum(c => war.Hits[i, c]);
                int guns = war.Vehicles.Count(v => v.Side == i && v.Class == VClass.Howitzer);
                double days = Math.Max(1.0 / 24, war.Time / 86400.0);
                sb.AppendLine($"      {sd.Name}: {war.ArtyRounds[i, 1] / Math.Max(1, guns) / days:0} shells a gun a day;");
                sb.AppendLine($"      {sd.Name}: fired {war.ArtyRounds[i, 0]:N0} mortar bombs, {war.ArtyRounds[i, 1]:N0} shells, {war.ArtyRounds[i, 2]:N0} rockets ({t / 1000f:N0} t) in "
                              + $"{war.ArtyMissions[i, 0]} close-support, {war.ArtyMissions[i, 1]} observed and {war.ArtyMissions[i, 2]} counter-battery missions; "
                              + $"lost {war.GunsLost[i]} guns to counter-battery; {hit} of its soldiers hit by shell and mortar fire ({kia} killed; {war.StruckHit[i]} outside fights), {100.0 * hit / Math.Max(1, allHit):0}% of all hit");
            }
            foreach (var sd in war.Sides)
                sb.AppendLine($"      {sd.Name} hit outside fights: riding {war.StruckHow[sd.Index, 0]}, marching {war.StruckHow[sd.Index, 1]}, just halted {war.StruckHow[sd.Index, 2]}, "
                              + $"in shell scrapes {war.StruckHow[sd.Index, 3]}, dug in {war.StruckHow[sd.Index, 4]}; {war.StruckByCb[sd.Index]} by counter-battery fire; by arm: "
                              + string.Join(", ", war.StruckArm[sd.Index].OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key.ToString().ToLowerInvariant()} {kv.Value}")));
            foreach (var (id, n) in war.StruckUnit.OrderByDescending(kv => kv.Value).Take(5))
                sb.AppendLine($"      most shelled outside fights: {war.Units[id].Name} ({war.Sides[war.Units[id].Side].Name}, raised {war.Units[id].Raised}, {war.Units[id].Carries.Count} parts): {n} hit");
            sb.AppendLine("    (real: artillery caused about 60% of casualties in the World Wars, and most in Ukraine)");
            sb.AppendLine("    The wounded and replacements:");
            foreach (var sd in war.Sides)
            {
                int i = sd.Index;
                int recovering = 0, joining = 0;
                for (int s = 0; s < war.SoldierCount; s++)
                {
                    if (war.Soldiers[s].Side != i) continue;
                    if (war.Soldiers[s].State == SoldierState.Recovering) recovering++;
                    else if (war.Soldiers[s].State == SoldierState.Joining) joining++;
                }
                sb.AppendLine($"      {sd.Name}: {war.Evacuated[i]:N0} carried into the medical chain, {war.DiedOfWounds[i]:N0} died of wounds, {war.Invalided[i]:N0} invalided home, "
                              + $"{war.Recovered[i]:N0} back to duty from hospital, {war.Healed[i]:N0} light wounds healed; {war.Replacements[i]:N0} replacements landed; "
                              + $"{war.Joined[i]:N0} joined their units; at the end {recovering:N0} in hospital and {joining:N0} on their way; "
                              + $"tickets {Medical.Tickets - war.Dead[i] + war.Replacements[i]:N0}");
            }
            sb.AppendLine("    Supply, in tonnes of food/fuel/ammunition:");
            var routes = new Dictionary<int, float[]>();
            foreach (var sd in war.Sides)
            {
                int i = sd.Index;
                string T(float[,] a) => $"{a[i, 0]:0}/{a[i, 1]:0}/{a[i, 2]:0}";
                var movers = war.Units.Where(u => u.IsMover && u.Side == i && u.People > 0).ToList();
                int dry = movers.Count(u => u.Mob != Mobility.Foot && u.FuelCap > 0f && u.Fuel <= 0f), hungry = movers.Count(u => u.HungrySince >= 0);
                int low = movers.Count(u => Supply.AmmoShare(war, u) < 0.5f);
                sb.AppendLine($"      {sd.Name}: landed {T(war.Landed)}; hauled to depots {T(war.Hauled)} in {war.Convoys[i]} convoy runs; issued to units {T(war.Issued)}; "
                              + $"lost on the road {T(war.CargoLost)}; {war.CutOff[i]} unit-nights cut off from every depot and {war.TooFar[i]} too far, {war.Area[i]} fed by a depot not their own; {war.BrokeOut[i]} break-outs; at the end {dry} units out of fuel, {hungry} hungry, "
                              + $"{low} below half their ammunition");
                var deps = war.Depots.Where(d => d.Side == i && d.Unit >= 0).ToList();
                string Fill(int c) => string.Join(" ", deps.Select(d => d.Target[c] > 0f ? (int)(100 * d.Stock[c] / d.Target[c]) : 100).OrderBy(x => x).Select(x => x.ToString()));
                sb.AppendLine($"        depots' stocks as % of what they aim for: food {Fill(0)}; fuel {Fill(1)}; ammunition {Fill(2)}");
                foreach (var d in deps.Where(d => d.Target[0] > 0f && d.Stock[0] < 0.05f * d.Target[0]))
                {
                    var hs = war.Hauls.Where(h => h.Home == d.Id || h.To == d.Id).ToList();
                    var par = d.Parent >= 0 ? war.Depots[d.Parent] : null;
                    int dc = war.Ctl.CellOf(d.X, d.Z);
                    sb.AppendLine($"        empty: {d.Name} (its ground {(dc < 0 ? "off the map" : !war.Ctl.Land[dc] ? "sea" : war.Ctl.Owner[dc] == i ? (war.Ctl.Contested[dc] ? "ours, contested" : "ours") : war.Ctl.Owner[dc] < 0 ? "nobody's" : "the enemy's")}), {(par == null ? "" : $"draws on {par.Name} ({(int)(100 * par.Stock[0] / Math.Max(1e-3f, par.Target[0]))}% food, {MathF.Sqrt((d.X - par.X) * (d.X - par.X) + (d.Z - par.Z) * (d.Z - par.Z)) / 1000f:0} km off)")}; "
                                  + $"{hs.Count} hauling for it: " + string.Join(", ", hs.Select(h => $"{war.Units[h.Unit].Short} {h.State} {(war.Time < h.Wait ? $"waiting ({h.Why})" : "")} {(war.Units[h.Unit].Path != null ? "moving" : "still")} {MathF.Sqrt((war.Units[h.Unit].X - d.X) * (war.Units[h.Unit].X - d.X) + (war.Units[h.Unit].Z - d.Z) * (war.Units[h.Unit].Z - d.Z)) / 1000f:0} km")));
                }
                var hung = movers.Where(u => u.HungrySince >= 0).ToList();
                if (hung.Count > 0)
                    sb.AppendLine($"        hungry: {hung.Count(u => u.Hauls)} hauling, {hung.Count(u => u.InFight >= 0)} in a fight, median {hung.Select(u => MathF.Sqrt((u.X - war.Depots[u.Depot].X) * (u.X - war.Depots[u.Depot].X) + (u.Z - war.Depots[u.Depot].Z) * (u.Z - war.Depots[u.Depot].Z)) / 1000f).OrderBy(x => x).ElementAt(hung.Count / 2):0} km from their depot, "
                                  + $"median {hung.Select(u => (war.Time - u.HungrySince) / 3600).OrderBy(x => x).ElementAt(hung.Count / 2):0} h without food, "
                                  + $"{hung.Count(u => !Supply.ClearToDepot(war, u))} cut off ("
                                  + string.Join(", ", hung.Where(u => !Supply.ClearToDepot(war, u)).Select(u => Supply.CutWhy(war, u, routes)).GroupBy(w => w).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))
                                  + "); hungry battalions: " + string.Join(", ", hung.Select(u => Command.Battalion(war, u)).Where(b => b != null).Distinct().Select(b => Command.Why(war, b!)).GroupBy(w => w).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))
                                  + "; by arm: "
                                  + string.Join(", ", hung.GroupBy(u => u.Arm).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key.ToString().ToLowerInvariant()} {g.Count()}")));
            }
            foreach (var sd in war.Sides)
            {
                var bns = war.Below(war.Units[sd.Army]).Where(u => u.Echelon == Echelon.Battalion && Command.Manoeuvre(u)).ToList();
                int holding = bns.Count(u => u.Holds >= 0), going = bns.Count(u => u.Holds < 0 && (u.Next != null || u.Order is { Kind: OrderKind.Occupy, Done: false }));
                int weak = bns.Count(u => u.Holds < 0 && Command.Strength(war, u) < 0.5f), resting = bns.Count(u => u.Holds < 0 && war.Time < u.RestUntil);
                sb.AppendLine($"      {sd.Name} battalions, why idle: " + string.Join(", ", bns.Select(u => Command.Why(war, u)).GroupBy(w => w).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
                var ms = war.MoverIds.Select(id => war.Units[id]).Where(u => u.Side == sd.Index && u.People > 0 && Command.Manoeuvre(u)).ToList();
                sb.AppendLine($"      {sd.Name} fighting companies at the end owe {ms.Select(u => u.SleepDebt).DefaultIfEmpty().Average():0.0} h of sleep on average; {ms.Count(u => u.SleepDebt >= Rest.ReadyDebt)} of {ms.Count} owe {Rest.ReadyDebt:0} h or more");
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
                    int veh = f.V.Where(v => v.Side == s && Combat.Armed(war.Vehicles[v.Vehicle].Class)).Select(v => v.Vehicle).Distinct().Count();
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
