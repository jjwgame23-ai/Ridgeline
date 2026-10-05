namespace Ridgeline;

/// <summary>
/// Where the towns are, and how big.
/// - Sizes follow the rank-size rule: the r-th largest place has about 1/r^q of the largest's population, with q near
///   1 (Zipf 1949; Gabaix 1999). The total comes from the climate's people per km², down to hamlets of 150. There
///   are no civilians: the people only size what was built.
/// - Towns go where real ones do: flat ground, water near (a river or the coast), farmland within reach, not too
///   high, and sometimes a defensible hilltop. The bigger places favour a natural harbour more. Most Mediterranean
///   island capitals are ports.
/// - Places are kept apart by size, as central place theory has it: villages a few km apart, towns ten or more.
/// </summary>
public static class TownPlanner
{
    public static void Apply(Island isl, Random rng, PlaceNames names)
    {
        int n = isl.N, c = n * n;
        float cell = isl.Cell;
        var seaSeed = new bool[c];
        var riverSeed = new bool[c];
        var ground = new float[c];
        for (int i = 0; i < c; i++)
        {
            seaSeed[i] = isl.Sea(i);
            riverSeed[i] = isl.Area[i] >= 10e6f && !isl.Water(i);
            ground[i] = isl.Water(i) ? 0f : isl.Height[i];
        }
        var dCoast = Grid.Distance(seaSeed, n, cell);
        var dRiver = Grid.Distance(riverSeed, n, cell);
        var slope = Grid.BoxMean(isl.Slope, n, Math.Max(1, (int)(300f / cell)));
        var farm = Grid.BoxMean(isl.Farmable, n, Math.Max(1, (int)(3000f / cell)));
        var hMean = Grid.BoxMean(ground, n, Math.Max(1, (int)(1000f / cell)));
        var harbour = Harbours(isl, dCoast);
        float farmMax = 1e-3f;
        for (int i = 0; i < c; i++) if (!isl.Water(i)) farmMax = MathF.Max(farmMax, farm[i]);

        var mainland = Mainland(isl, 30e6f);
        var jitter = new Noise2(isl.Seed + 61);
        var score = new float[c];
        // Villages, old ones especially, stand back from the low coast. Braudel describes the old Mediterranean pattern:
        // villages up on the hills, out of the malarial coastal marsh and the raiders' reach. The ports and market
        // towns are down on the plains and the shore.
        var village = new float[c];
        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                if (!mainland[i] || isl.Slope[i] > 15f || isl.Height[i] > 1100f) continue;
                float flat = MathF.Pow(MathF.Exp(-slope[i] / 6f), 0.7f);
                float agri = farm[i] / farmMax;
                float water = MathF.Max(MathF.Exp(-dRiver[i] / 1500f), MathF.Exp(-dCoast[i] / 1500f));
                float h = isl.Height[i];
                float elev = h < 600f ? 1f : 1f - 0.8f * (h - 600f) / 500f;
                float prom = h - hMean[i];
                float hill = prom > 40f && prom < 220f && isl.Slope[i] < 9f ? 1f : 0f;
                float j = 0.85f + 0.3f * (0.5f + 0.5f * jitter.Get(x * cell / 3000f, y * cell / 3000f));
                score[i] = flat * (0.25f + 0.75f * agri) * (0.6f + 0.4f * water) * elev * (1f + 0.35f * hill) * j;
                bool lowShore = h < 25f && dCoast[i] < 2500f;
                village[i] = (1f + 0.6f * hill) * (lowShore ? 0.55f : 1f);
            }
        });

        Place(isl, Sizes(isl, rng), score, harbour, village);

        isl.Towns.Sort((a, b) => b.Population.CompareTo(a.Population));
        for (int k = 0; k < isl.Towns.Count; k++)
        {
            var t = isl.Towns[k];
            t.Id = k;
            t.Rank = k + 1;
            int p = t.Population;
            t.Kind = p >= 40000 || (k == 0 && p >= 15000) ? TownKind.City : p >= 4000 ? TownKind.Town : p >= 400 ? TownKind.Village : TownKind.Hamlet;
            t.Harbour = Best(isl, harbour, t.Cell, t.RadiusM + 500f);
            // A port takes ships big enough to land troops and cargo. Fishing villages have harbours, not ports, and
            // a port serves a stretch of coast: none within 10 km of a bigger one.
            bool coastal = dCoast[t.Cell] <= t.RadiusM + 400f;
            t.Port = coastal && ((p >= 3000 && t.Harbour >= 0.35f) || (t.Kind >= TownKind.Town && t.Harbour >= 0.15f))
                     && !isl.Towns.Take(k).Any(o => o.Port && isl.Dist(o.Cell, t.Cell) < 10_000f);
            float prom = isl.Height[t.Cell] - hMean[t.Cell];
            t.Hilltop = prom > 40f && isl.Slope[t.Cell] < 9f;
        }
        // Every side needs a port to start from, so there are at least three after the capital, if anywhere on the
        // coast will do.
        int ports = isl.Towns.Count(t => t.Port && t.Rank > 1);
        foreach (var t in isl.Towns.Where(t => !t.Port && t.Rank > 1 && t.Population >= 1200 && dCoast[t.Cell] <= t.RadiusM + 600f)
                     .OrderByDescending(t => t.Harbour))
        {
            if (ports >= 4) break;
            if (isl.Towns.Any(o => o.Port && isl.Dist(o.Cell, t.Cell) < 10_000f)) continue;
            t.Port = true;
            ports++;
        }
        foreach (var t in isl.Towns) t.Name = names.Town(t);
    }

    /// <summary>
    /// Land on masses of <paramref name="minM2"/> or more (8-connected). Towns aren't put on islets the risen sea has
    /// cut off: the roads can't reach them, and a side starting on one would have nowhere to go.
    /// </summary>
    static bool[] Mainland(Island isl, float minM2)
    {
        int n = isl.N, c = n * n;
        var keep = new bool[c];
        var seen = new bool[c];
        var comp = new List<int>();
        var queue = new Queue<int>();
        int minCells = (int)(minM2 / isl.CellArea);
        for (int i = 0; i < c; i++)
        {
            if (seen[i] || isl.Water(i)) continue;
            comp.Clear();
            seen[i] = true;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int k = queue.Dequeue();
                comp.Add(k);
                int x = k % n, y = k / n;
                for (int m = 0; m < 8; m++)
                {
                    int nx = x + Island.DX[m], ny = y + Island.DY[m];
                    if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                    int j = ny * n + nx;
                    if (seen[j] || isl.Water(j)) continue;
                    seen[j] = true;
                    queue.Enqueue(j);
                }
            }
            if (comp.Count >= minCells) foreach (int k in comp) keep[k] = true;
        }
        return keep;
    }

    /// <summary>The built-up radius (m) of a place of <paramref name="p"/> people: denser the bigger it is.</summary>
    static float Radius(int p)
    {
        float density = Math.Clamp(2000f + 1500f * MathF.Log10(p / 1000f), 1000f, 7000f); // people per km²
        return MathF.Sqrt(p / density / MathF.PI) * 1000f;
    }

    /// <summary>How far apart two places must be (m): set by the smaller one, plus both their built radii.</summary>
    static float Apart(int pa, float ra, int pb, float rb) => 1400f + 50f * MathF.Sqrt(Math.Min(pa, pb)) + ra + rb;

    static List<int> Sizes(Island isl, Random rng)
    {
        int land = 0;
        for (int i = 0; i < isl.Count; i++) if (!isl.Water(i)) land++;
        double km2 = land * isl.CellArea / 1e6;
        // 85% live in the towns and villages; the rest on farms.
        double total = km2 * isl.Climate.PeoplePerKm2 * (0.8 + 0.4 * rng.NextDouble()) * 0.85;
        double q = 0.92 + 0.16 * rng.NextDouble();
        double p1 = total / 6.0;
        for (int it = 0; it < 40; it++)
        {
            int count = Math.Max(1, (int)Math.Floor(Math.Pow(p1 / 150.0, 1.0 / q)));
            double sum = 0;
            for (int r = 1; r <= count; r++) sum += Math.Pow(r, -q);
            p1 = total / sum;
        }
        int cnt = Math.Max(1, (int)Math.Floor(Math.Pow(p1 / 150.0, 1.0 / q)));
        var sizes = new List<int>();
        for (int r = 1; r <= cnt; r++)
        {
            double p = p1 * Math.Pow(r, -q) * (r == 1 ? 1.0 : 0.85 + 0.3 * rng.NextDouble());
            sizes.Add(Math.Max(150, (int)Math.Round(p / 10.0) * 10));
        }
        sizes.Sort((a, b) => b.CompareTo(a));
        return sizes;
    }

    static void Place(Island isl, List<int> sizes, float[] score, float[] harbour, float[] village)
    {
        int n = isl.N, c = n * n;
        float cell = isl.Cell;
        // Candidates best first, with a harbour counting for more the bigger the place.
        int[] Candidates(float portWeight, float[]? extra)
        {
            var cells = new List<int>();
            var keys = new List<float>();
            for (int i = 0; i < c; i++)
            {
                if (score[i] <= 0.02f) continue;
                cells.Add(i);
                keys.Add(-score[i] * (1f + portWeight * harbour[i]) * (extra?[i] ?? 1f));
            }
            var a = cells.ToArray();
            Array.Sort(keys.ToArray(), a);
            return a;
        }
        var lists = new[] { Candidates(0.5f, village), Candidates(1.5f, null), Candidates(3f, null) };
        var start = new int[3];
        var blocked = new bool[c];
        int bs = Math.Max(1, (int)MathF.Ceiling(2000f / cell)), nb = (n + bs - 1) / bs;
        var buckets = new List<Town>?[nb * nb];
        int reach = (int)MathF.Ceiling(18000f / (bs * cell));

        bool FarEnough(int i, int p, float r)
        {
            int bx = i % n / bs, by = i / n / bs;
            for (int y = Math.Max(0, by - reach); y <= Math.Min(nb - 1, by + reach); y++)
            for (int x = Math.Max(0, bx - reach); x <= Math.Min(nb - 1, bx + reach); x++)
            {
                var list = buckets[y * nb + x];
                if (list == null) continue;
                foreach (var t in list)
                    if (isl.Dist(t.Cell, i) < Apart(p, r, t.Population, t.RadiusM)) return false;
            }
            return true;
        }

        float rMin = Radius(150);
        foreach (int p in sizes)
        {
            int li = p >= 20000 ? 2 : p >= 5000 ? 1 : 0;
            var list = lists[li];
            float r = Radius(p);
            int found = -1;
            for (int k = start[li]; k < list.Length; k++)
            {
                int i = list[k];
                if (blocked[i])
                {
                    if (k == start[li]) start[li]++;
                    continue;
                }
                if (!FarEnough(i, p, r)) continue;
                found = i;
                break;
            }
            if (found < 0) continue;
            var town = new Town
            {
                Id = isl.Towns.Count, Cell = found, Population = p, RadiusM = r,
                X = isl.X(found), Z = isl.Z(found), Elevation = isl.Height[found],
            };
            isl.Towns.Add(town);
            int b = found / n / bs * nb + found % n / bs;
            (buckets[b] ??= new List<Town>()).Add(town);
            // Nothing that comes later (all smaller) can stand this close, so take those cells off the lists.
            float block = Apart(150, rMin, p, r);
            int br = (int)(block / cell);
            int fx = found % n, fy = found / n;
            for (int y = Math.Max(0, fy - br); y <= Math.Min(n - 1, fy + br); y++)
            for (int x = Math.Max(0, fx - br); x <= Math.Min(n - 1, fx + br); x++)
            {
                float dx = (x - fx) * cell, dy = (y - fy) * cell;
                if (dx * dx + dy * dy < block * block) blocked[y * n + x] = true;
            }
        }
    }

    /// <summary>
    /// How sheltered the water off each coastal cell is, 0..1, from the share of 24 directions in which land is met
    /// within 8 km. Off a straight coast half the directions meet land at once (the shore itself), which scores 0. A bay
    /// closed all round scores 1.
    /// </summary>
    static float[] Harbours(Island isl, float[] dCoast)
    {
        int n = isl.N, c = n * n;
        float cell = isl.Cell;
        var landSeed = new bool[c];
        for (int i = 0; i < c; i++) landSeed[i] = !isl.Sea(i);
        var dLand = Grid.Distance(landSeed, n, cell);
        var shelter = new float[c];
        int steps = (int)(8000f / cell);
        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                if (!isl.Sea(i) || dLand[i] > cell * 1.5f) continue;
                int hit = 0;
                for (int a = 0; a < 24; a++)
                {
                    float ang = a * MathF.Tau / 24f, dx = MathF.Cos(ang), dy = MathF.Sin(ang);
                    for (int s = 1; s <= steps; s++)
                    {
                        int px = (int)MathF.Round(x + dx * s), py = (int)MathF.Round(y + dy * s);
                        if ((uint)px >= (uint)n || (uint)py >= (uint)n) break;
                        if (!isl.Sea(py * n + px))
                        {
                            hit++;
                            break;
                        }
                    }
                }
                shelter[i] = Math.Clamp((hit / 24f - 0.5f) / 0.4f, 0f, 1f);
            }
        });
        var harbour = new float[c];
        for (int i = 0; i < c; i++)
        {
            if (isl.Water(i) || dCoast[i] > cell * 1.5f) continue;
            int x = i % n, y = i / n;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Island.DX[k], ny = y + Island.DY[k];
                if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                harbour[i] = MathF.Max(harbour[i], shelter[ny * n + nx]);
            }
        }
        return harbour;
    }

    /// <summary>The highest value of <paramref name="a"/> within <paramref name="radiusM"/> of a cell.</summary>
    static float Best(Island isl, float[] a, int cell, float radiusM)
    {
        int n = isl.N, r = (int)(radiusM / isl.Cell), cx = cell % n, cy = cell / n;
        float best = 0f;
        for (int y = Math.Max(0, cy - r); y <= Math.Min(n - 1, cy + r); y++)
        for (int x = Math.Max(0, cx - r); x <= Math.Min(n - 1, cx + r); x++)
            best = MathF.Max(best, a[y * n + x]);
        return best;
    }
}
