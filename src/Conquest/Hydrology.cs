namespace Ridgeline;

/// <summary>
/// The island's rivers and lakes, on the finished ground:
/// - hollows filled to where they spill (lakes where that stands real water);
/// - each cell's drainage area and mean flow (rain times the climate's runoff, gathered downstream);
/// - flat valley floors along the bigger rivers;
/// - each stream's Strahler order and the longest path above each point (for Horton's and Hack's laws);
/// - the main stems of the biggest rivers, mouth to source.
/// </summary>
public static class Hydrology
{
    /// <summary>Where a stream starts to count: 2 km² of catchment.</summary>
    public const float StreamM2 = 2e6f;

    public static void Apply(Island isl)
    {
        int n = isl.N, c = n * n;
        var h = new double[c];
        var sea = new bool[c];
        for (int i = 0; i < c; i++)
        {
            h[i] = isl.Height[i];
            sea[i] = isl.Sea(i);
        }
        var filled = (double[])h.Clone();
        var d = new Drainage(n, isl.Cell);
        d.Fill(filled, sea, 1e-3);
        Lakes(isl, h, filled);
        d.Route(filled, sea);
        d.Order();
        d.Accumulate();
        for (int i = 0; i < c; i++)
        {
            isl.Area[i] = (float)d.Area[i];
            isl.Receiver[i] = d.Rec[i];
        }
        Floodplains(isl, d);
        // Mean flow: rain (mm a year) times runoff, gathered downstream, in m³/s.
        var w = new double[c];
        double perSecond = isl.Climate.Runoff / 1000.0 / 3.15576e7;
        for (int i = 0; i < c; i++) w[i] = sea[i] ? 0.0 : isl.Rain[i] * perSecond;
        d.Accumulate(w);
        for (int i = 0; i < c; i++) isl.Flow[i] = (float)d.Area[i];
        isl.Stack = d.Stack[..d.Count];
        for (int k = d.Count - 1; k >= 0; k--)
        {
            int i = d.Stack[k], r = d.Rec[i];
            if (r != i) isl.Longest[r] = MathF.Max(isl.Longest[r], isl.Longest[i] + (float)d.Dist[i]);
        }
        Strahler(isl, d);
        MainStems(isl, d);
    }

    /// <summary>
    /// Where filling the hollows stood water a metre deep or more over a dozen cells or more, there's a lake at its
    /// spill level. Shallower hollows are just filled in.
    /// </summary>
    static void Lakes(Island isl, double[] h, double[] filled)
    {
        int n = isl.N, c = n * n;
        var seen = new bool[c];
        var comp = new List<int>();
        var queue = new Queue<int>();
        for (int i = 0; i < c; i++)
        {
            if (seen[i] || isl.Sea(i) || filled[i] - h[i] < 0.05) continue;
            comp.Clear();
            seen[i] = true;
            queue.Enqueue(i);
            double deepest = 0;
            while (queue.Count > 0)
            {
                int k = queue.Dequeue();
                comp.Add(k);
                deepest = Math.Max(deepest, filled[k] - h[k]);
                int x = k % n, y = k / n;
                for (int m = 0; m < 8; m++)
                {
                    int nx = x + Island.DX[m], ny = y + Island.DY[m];
                    if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                    int j = ny * n + nx;
                    if (seen[j] || isl.Sea(j) || filled[j] - h[j] < 0.05) continue;
                    seen[j] = true;
                    queue.Enqueue(j);
                }
            }
            bool lake = comp.Count >= 12 && deepest >= 1.0;
            foreach (int k in comp)
            {
                if (lake && filled[k] - h[k] >= 0.3)
                {
                    isl.Land[k] = Cover.Lake;
                    isl.LakeLevel[k] = (float)filled[k];
                }
                else isl.Height[k] = (float)filled[k];
            }
        }
    }

    /// <summary>
    /// Valley floors filled with river sediment. How high ground stands above the river it drains to (HAND, "height
    /// above nearest drainage": Rennó et al. 2008) is how floodplains are mapped from terrain. Here, ground a few
    /// metres above a river of 20 km² or more is laid nearly flat, the way floods and a wandering channel leave it.
    /// The band grows with the river: 2 m beside a 20 km² stream, 7 m beside a 2,000 km² river. Heights only come
    /// closer to the river's, so water still runs the same way.
    /// </summary>
    static void Floodplains(Island isl, Drainage d)
    {
        const float RiverM2 = 20e6f;
        int c = isl.Count;
        var baseH = new float[c];
        var baseA = new float[c];
        for (int k = 0; k < d.Count; k++)
        {
            int i = d.Stack[k];
            if (isl.Sea(i)) continue;
            if (isl.Area[i] >= RiverM2)
            {
                baseH[i] = isl.Land[i] == Cover.Lake ? isl.LakeLevel[i] : isl.Height[i];
                baseA[i] = isl.Area[i];
                continue;
            }
            int r = d.Rec[i];
            baseH[i] = baseH[r];
            baseA[i] = baseA[r];
        }
        for (int i = 0; i < c; i++)
        {
            if (isl.Water(i) || baseA[i] < RiverM2 || isl.Area[i] >= RiverM2) continue;
            float hand = isl.Height[i] - baseH[i];
            float band = 2f + 2.5f * MathF.Log10(baseA[i] / RiverM2);
            if (hand > 0f && hand < band) isl.Height[i] = baseH[i] + hand * 0.25f;
        }
    }

    static bool Stream(Island isl, int i) => isl.Area[i] >= StreamM2 && !isl.Sea(i);

    /// <summary>
    /// Strahler order: a stream with no tributaries is order 1, and two of the same order meeting make one of the next
    /// (Strahler 1957). Lakes carry their river's order through.
    /// </summary>
    static void Strahler(Island isl, Drainage d)
    {
        int c = isl.Count;
        var top = new byte[c];
        var many = new byte[c];
        for (int k = d.Count - 1; k >= 0; k--)
        {
            int i = d.Stack[k];
            if (!Stream(isl, i)) continue;
            byte o = top[i] == 0 ? (byte)1 : many[i] >= 2 ? (byte)(top[i] + 1) : top[i];
            isl.Order[i] = o;
            int r = d.Rec[i];
            if (r == i || !Stream(isl, r)) continue;
            if (o > top[r])
            {
                top[r] = o;
                many[r] = 1;
            }
            else if (o == top[r] && many[r] < 255) many[r]++;
        }
    }

    /// <summary>The 14 largest rivers reaching the sea (25 km² and more): mouth first, then up the biggest branch.</summary>
    static void MainStems(Island isl, Drainage d)
    {
        var mouths = new List<int>();
        for (int i = 0; i < isl.Count; i++)
            if (!isl.Sea(i) && isl.Area[i] >= 25e6f && isl.Sea(d.Rec[i])) mouths.Add(i);
        mouths.Sort((a, b) => isl.Area[b].CompareTo(isl.Area[a]));
        foreach (int m in mouths.Take(14))
        {
            var river = new River { AreaKm2 = isl.Area[m] / 1e6f, FlowM3s = isl.Flow[m] };
            int cur = m;
            double len = 0;
            while (true)
            {
                river.Cells.Add(cur);
                int best = -1;
                float most = 0f;
                for (int k = d.DonStart[cur]; k < d.DonStart[cur + 1]; k++)
                {
                    int j = d.Don[k];
                    if (isl.Area[j] > most)
                    {
                        most = isl.Area[j];
                        best = j;
                    }
                }
                if (best < 0 || most < StreamM2) break;
                len += d.Dist[best];
                cur = best;
            }
            river.LengthKm = (float)(len / 1000.0);
            isl.Rivers.Add(river);
        }
    }
}
