using System.Text;

namespace Ridgeline;

/// <summary>
/// The numbers on a generated island, and checks of its shape against laws measured on real landscapes:
/// - Hack's law: a river's length grows as about the 0.6 power of its basin's area (Hack 1957).
/// - Horton's laws: each stream order has 3–5 times as many streams as the next, each 1.5–3.5 times as long (Horton
///   1945; Strahler 1957).
/// - Rank-size: town sizes fall as 1/rank^q, with q of 0.8–1.2 (Zipf 1949; Gabaix 1999).
/// - Coastlines are fractal, with dimension 1.1–1.3 for most real coasts (Mandelbrot 1967).
/// </summary>
public static class IslandReport
{
    public static string Text(Island isl)
    {
        var sb = new StringBuilder();
        var checks = new List<string>();
        void Check(string what, double v, double lo, double hi, string real) =>
            checks.Add($"  {what,-28} {v,6:0.00}   real {lo:0.##}-{hi:0.##} ({real})   {(v >= lo && v <= hi ? "ok" : "OUTSIDE")}");

        int n = isl.N, c = isl.Count;
        float cellKm2 = isl.CellArea / 1e6f;
        int land = 0, coastEdges = 0;
        double sumH = 0, maxH = 0;
        var slopeBins = new int[4];
        for (int i = 0; i < c; i++)
        {
            if (isl.Water(i)) continue;
            land++;
            sumH += isl.Height[i];
            maxH = Math.Max(maxH, isl.Height[i]);
            float s = isl.Slope[i];
            slopeBins[s < 5f ? 0 : s < 15f ? 1 : s < 30f ? 2 : 3]++;
            int x = i % n, y = i / n;
            if (x > 0 && isl.Sea(i - 1)) coastEdges++;
            if (x < n - 1 && isl.Sea(i + 1)) coastEdges++;
            if (y > 0 && isl.Sea(i - n)) coastEdges++;
            if (y < n - 1 && isl.Sea(i + n)) coastEdges++;
        }
        double meanH = sumH / Math.Max(1, land);
        sb.AppendLine($"ISLAND {isl.Name} (seed {isl.Seed}): {isl.Climate.Name}, {isl.Climate.Latitude:0}° N");
        sb.AppendLine($"Map {isl.Extent / 1000f:0} x {isl.Extent / 1000f:0} km, {n} x {n} cells of {isl.Cell:0} m");
        sb.AppendLine($"Land {land * cellKm2:N0} km² ({100.0 * land / c:0}% of the map); highest point {maxH:N0} m; mean height {meanH:0} m");
        // A grid coast is a staircase, about 4/π (1.27) times longer than the smooth line it stands for.
        sb.AppendLine($"Coastline about {coastEdges * isl.Cell / 1000f / 1.27f:N0} km measured at {isl.Cell:0} m; the sea rose {isl.SeaRise:0} m over the carved land");
        sb.AppendLine($"Hypsometric integral {meanH / Math.Max(1.0, maxH):0.00} (mean height over the highest: low means mostly low ground under a few high peaks)");
        sb.AppendLine($"Slopes: under 5° {Pct(slopeBins[0], land)}, 5-15° {Pct(slopeBins[1], land)}, 15-30° {Pct(slopeBins[2], land)}, over 30° {Pct(slopeBins[3], land)}");
        double dim = CoastDimension(isl);
        sb.AppendLine($"Coastline fractal dimension {dim:0.00} (box counting from 200 m to 3.2 km)");
        Check("Coastline dimension", dim, 1.1, 1.3, "Mandelbrot 1967");

        // Climate.
        float rMin = float.MaxValue, rMax = 0f;
        double rSum = 0;
        float tMax = float.MinValue;
        for (int i = 0; i < c; i++)
        {
            if (isl.Water(i)) continue;
            rMin = MathF.Min(rMin, isl.Rain[i]);
            rMax = MathF.Max(rMax, isl.Rain[i]);
            rSum += isl.Rain[i];
        }
        foreach (var p in isl.Peaks.Take(1)) tMax = isl.Temp[p.Cell];
        sb.AppendLine();
        sb.AppendLine($"Rain {rMin:N0}-{rMax:N0} mm a year (mean {rSum / Math.Max(1, land):N0}); {WindwardLee(isl)}");
        sb.AppendLine($"Mean temperature {isl.Climate.SeaLevelTempC:0.0} °C at sea level{(tMax > float.MinValue ? $", {tMax:0.0} °C on the summit" : "")}");

        // Rivers.
        sb.AppendLine();
        // Horton's ratios are measured within drainage basins, so they're taken over the basins of order 4 and up (3
        // on an island without any). The many little coastal basins of one or two streams would swamp them otherwise.
        int maxOrder = 0;
        for (int i = 0; i < c; i++) maxOrder = Math.Max(maxOrder, isl.Order[i]);
        var basin = new byte[c];
        foreach (int i in isl.Stack)
        {
            int r = isl.Receiver[i];
            basin[i] = r == i || isl.Sea(r) ? isl.Order[i] : basin[r];
        }
        int minBasin = Math.Min(4, Math.Max(2, maxOrder));
        var count = new int[maxOrder + 2];
        var length = new double[maxOrder + 2];
        for (int i = 0; i < c; i++)
        {
            int o = isl.Order[i];
            if (o == 0 || basin[i] < minBasin) continue;
            int r = isl.Receiver[i];
            length[o] += isl.Dist(i, r);
            if (r == i || isl.Order[r] > o || isl.Sea(r)) count[o]++;
        }
        sb.Append($"Streams by Strahler order, in basins of order {minBasin}+ (from 2 km² of catchment):");
        for (int o = 1; o <= maxOrder; o++) sb.Append($"  {o}: {count[o]} ({length[o] / Math.Max(1, count[o]) / 1000.0:0.0} km avg)");
        sb.AppendLine();
        int top = maxOrder;
        while (top > 2 && count[top] < 2) top--;
        if (top >= 2 && count[top] > 0)
        {
            double rb = Math.Pow((double)count[1] / count[top], 1.0 / (top - 1));
            double rl = Math.Pow(length[top] / count[top] / (length[1] / Math.Max(1, count[1])), 1.0 / (top - 1));
            sb.AppendLine($"Bifurcation ratio {rb:0.00}, length ratio {rl:0.00} (orders 1-{top})");
            Check("Bifurcation ratio", rb, 3.0, 5.0, "Horton 1945");
            Check("Length ratio", rl, 1.5, 3.5, "Horton 1945");
        }
        var (hack, hackC, hackR2) = HackFit(isl);
        sb.AppendLine($"Hack's law: length = {hackC:0.00} x area^{hack:0.00} (km, km²; R² {hackR2:0.00})");
        Check("Hack exponent", hack, 0.5, 0.65, "Hack 1957: 0.6");
        foreach (var r in isl.Rivers.Take(8))
            sb.AppendLine($"  {r.Name,-14} {r.LengthKm,5:0} km, basin {r.AreaKm2,6:N0} km², mean flow {r.FlowM3s,5:0.0} m³/s at the mouth");
        int lakeCells = 0;
        for (int i = 0; i < c; i++) if (isl.Land[i] == Cover.Lake) lakeCells++;
        sb.AppendLine($"Lakes: {lakeCells * cellKm2:0.0} km²");

        // Land cover.
        sb.AppendLine();
        var cover = new int[Enum.GetValues<Cover>().Length];
        for (int i = 0; i < c; i++) cover[(int)isl.Land[i]]++;
        sb.Append("Land cover:");
        foreach (var cv in Enum.GetValues<Cover>())
            if (cv != Cover.Sea && cover[(int)cv] > 0) sb.Append($" {cv.ToString().ToLowerInvariant()} {Pct(cover[(int)cv], land)},");
        sb.Length--;
        sb.AppendLine();

        // Towns.
        sb.AppendLine();
        var t = isl.Towns;
        sb.AppendLine($"Places: {t.Count} ({t.Count(x => x.Kind == TownKind.City)} cities, {t.Count(x => x.Kind == TownKind.Town)} towns, "
                      + $"{t.Count(x => x.Kind == TownKind.Village)} villages, {t.Count(x => x.Kind == TownKind.Hamlet)} hamlets), "
                      + $"{t.Sum(x => (long)x.Population):N0} people's worth of building; {t.Count(x => x.Port)} ports; {isl.Farms.Count:N0} farmsteads");
        var small = t.Where(x => x.Kind <= TownKind.Village).Select(x => x.Elevation).OrderBy(e => e).ToList();
        if (small.Count > 0)
            sb.AppendLine($"Villages and hamlets: median height {small[small.Count / 2]:0} m, {Pct(small.Count(e => e >= 200f), small.Count)} at 200 m or more, {Pct(t.Count(x => x.Hilltop), t.Count)} of all places on hilltops");
        if (t.Count >= 10)
        {
            var (q, r2) = RankSize(t);
            sb.AppendLine($"Rank-size exponent {q:0.00} (R² {r2:0.00})");
            Check("Rank-size exponent", q, 0.8, 1.2, "Zipf 1949; Gabaix 1999");
        }
        foreach (var x in t.Take(15))
            sb.AppendLine($"  {x.Rank,3}. {x.Name,-20} {x.Population,8:N0}  {x.Kind.ToString().ToLowerInvariant(),-7} {x.Elevation,5:0} m{(x.Port ? "  port" : "")}{(x.Hilltop ? "  hilltop" : "")}");

        // Roads.
        sb.AppendLine();
        var km = new double[4];
        foreach (var road in isl.Roads)
            for (int k = 1; k < road.Cells.Count; k++) km[(int)road.Class] += isl.Dist(road.Cells[k - 1], road.Cells[k]) / 1000.0;
        sb.AppendLine($"Roads (as routed; shared stretches counted once per road): main {km[3]:N0} km, secondary {km[2]:N0} km, tracks {km[1]:N0} km");
        sb.AppendLine($"Stream crossings: {isl.Crossings.Count(x => x.Bridge)} bridges, {isl.Crossings.Count(x => !x.Bridge)} fords; widest bridged {isl.Crossings.Where(x => x.Bridge).Select(x => x.WidthM).DefaultIfEmpty(0f).Max():0} m");

        // Sides and nodes.
        sb.AppendLine();
        foreach (var s in isl.Starts)
        {
            var port = isl.Towns[s.Port];
            int towns = 0;
            long people = 0;
            foreach (var x in t)
                if (isl.StartOf[x.Cell] == s.Team)
                {
                    towns++;
                    people += x.Population;
                }
            int nodes = isl.Nodes.Count(nd => nd.Start == s.Team);
            sb.AppendLine($"{KothMode.TeamNames[s.Team],-8} starts round {port.Name} ({port.Population:N0}): {s.AreaKm2:N0} km², {towns} places ({people:N0} people's worth), {nodes} node{(nodes == 1 ? "" : "s")}");
        }
        sb.AppendLine($"Resource nodes: {isl.Nodes.Count} ({isl.Nodes.Count(x => x.Kind == NodeKind.Fuel)} fuel, {isl.Nodes.Count(x => x.Kind == NodeKind.Ore)} ore)");
        foreach (var nd in isl.Nodes)
            sb.AppendLine($"  {nd.Name,-24} {nd.Kind.ToString().ToLowerInvariant(),-4} richness {nd.Richness:0.00}, {isl.Height[nd.Cell]:0} m{(nd.Start >= 0 ? $", in {KothMode.TeamNames[nd.Start]}'s start" : "")}");
        sb.AppendLine($"Peaks named: {isl.Peaks.Count}; highest: {string.Join(", ", isl.Peaks.Take(4).Select(p => $"{p.Name} {p.Height:0} m"))}");

        sb.AppendLine();
        sb.AppendLine("Checks against real landscapes:");
        foreach (var line in checks) sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("Time: " + string.Join(", ", isl.Timings.Select(x => $"{x.Stage} {x.Ms / 1000.0:0.0} s")));
        return sb.ToString();
    }

    static string Pct(int a, int b) => $"{100.0 * a / Math.Max(1, b):0}%";

    /// <summary>Mean rain on the third of the land nearest the wet wind, and on the third furthest from it.</summary>
    static string WindwardLee(Island isl)
    {
        float to = (isl.Climate.WetWindFromDeg + 180f) * MathF.PI / 180f;
        float wx = MathF.Sin(to), wy = -MathF.Cos(to);
        var pts = new List<(float Along, float Rain)>();
        for (int i = 0; i < isl.Count; i++)
            if (!isl.Water(i)) pts.Add(((i % isl.N) * wx + (i / isl.N) * wy, isl.Rain[i]));
        if (pts.Count < 3) return "";
        pts.Sort((a, b) => a.Along.CompareTo(b.Along));
        int third = pts.Count / 3;
        double windward = pts.Take(third).Average(p => p.Rain), lee = pts.Skip(pts.Count - third).Average(p => p.Rain);
        return $"windward third {windward:N0} mm, lee third {lee:N0} mm";
    }

    /// <summary>Box-counting dimension of the coastline: how the count of boxes it touches grows as they shrink.</summary>
    static double CoastDimension(Island isl)
    {
        int n = isl.N;
        var coast = new bool[isl.Count];
        for (int i = 0; i < isl.Count; i++)
        {
            if (isl.Sea(i)) continue;
            int x = i % n, y = i / n;
            coast[i] = (x > 0 && isl.Sea(i - 1)) || (x < n - 1 && isl.Sea(i + 1)) || (y > 0 && isl.Sea(i - n)) || (y < n - 1 && isl.Sea(i + n));
        }
        var xs = new List<double>();
        var ys = new List<double>();
        for (int b = 2; b <= 32; b *= 2)
        {
            var boxes = new HashSet<int>();
            for (int i = 0; i < isl.Count; i++) if (coast[i]) boxes.Add(i / n / b * 100_000 + i % n / b);
            xs.Add(Math.Log(1.0 / b));
            ys.Add(Math.Log(boxes.Count));
        }
        return Fit(xs, ys).Slope;
    }

    static (double Exponent, double C, double R2) HackFit(Island isl)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        for (int i = 0; i < isl.Count; i++)
        {
            if (isl.Order[i] == 0 || isl.Area[i] < 5e6f || isl.Longest[i] <= 0f) continue;
            xs.Add(Math.Log10(isl.Area[i] / 1e6));
            ys.Add(Math.Log10(isl.Longest[i] / 1000.0));
        }
        if (xs.Count < 10) return (0, 0, 0);
        var f = Fit(xs, ys);
        return (f.Slope, Math.Pow(10, f.Intercept), f.R2);
    }

    static (double Q, double R2) RankSize(List<Town> towns)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        for (int k = 0; k < towns.Count; k++)
        {
            xs.Add(Math.Log10(k + 1));
            ys.Add(Math.Log10(towns[k].Population));
        }
        var f = Fit(xs, ys);
        return (-f.Slope, f.R2);
    }

    static (double Slope, double Intercept, double R2) Fit(List<double> xs, List<double> ys)
    {
        int m = xs.Count;
        double mx = xs.Average(), my = ys.Average(), sxx = 0, sxy = 0, syy = 0;
        for (int k = 0; k < m; k++)
        {
            double dx = xs[k] - mx, dy = ys[k] - my;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }
        double slope = sxy / Math.Max(1e-12, sxx);
        return (slope, my - slope * mx, sxy * sxy / Math.Max(1e-12, sxx * syy));
    }
}
