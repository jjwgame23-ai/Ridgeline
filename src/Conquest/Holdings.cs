namespace Ridgeline;

/// <summary>
/// What the sides start with, and what there is to fight over.
/// - Starting areas. Each side starts round a port, since replacements and supplies come by sea. The three ports are
///   of like size, with about as much round each, as far apart as can be, and none within 20 km of a city: the cities
///   are left to fight over. Each side gets the 6% of the island nearest its port in travel time.
/// - Resource nodes, the sites for an extractor. Fuel (oil and gas) lies under soft sedimentary rock in the lowlands,
///   ore in the hard rock of the mountains. There's one in each starting area and the rest out in the contested
///   ground, at least 7 km apart, each with a track to the nearest road.
/// </summary>
public static class Holdings
{
    const float StartShare = 0.06f;

    public static void Apply(Island isl, Random rng)
    {
        StartAreas(isl);
        Nodes(isl, rng);
    }

    static void StartAreas(Island isl)
    {
        if (isl.Towns.Count < 3) return;
        // The cities are the prizes everyone fights over: no side starts within 20 km of one, or holds its outskirts.
        var cities = isl.Towns.Where(t => t.Kind == TownKind.City || t.Rank == 1).ToList();
        bool Clear(Town t, float m) => cities.All(c => c != t && isl.Dist(t.Cell, c.Cell) >= m);
        var ports = isl.Towns.Where(t => t.Port && Clear(t, 20_000f)).ToList();
        if (ports.Count < 3) ports = isl.Towns.Where(t => t.Port && Clear(t, 12_000f)).ToList();
        if (ports.Count < 3) ports = isl.Towns.Where(t => !cities.Contains(t)).OrderByDescending(t => t.Harbour).Take(6).ToList();
        if (ports.Count < 3) return;
        ports = ports.Take(14).ToList(); // the biggest 14 (the towns are in size order)

        int cnt = isl.Count, land = 0;
        for (int i = 0; i < cnt; i++) if (!isl.Water(i)) land++;
        int take = (int)(land * StartShare);
        var claimable = new bool[cnt];
        Parallel.For(0, cnt, i =>
        {
            if (isl.Water(i)) return;
            foreach (var c in cities) if (isl.Dist(i, c.Cell) < c.RadiusM + 3000f) return;
            claimable[i] = true;
        });
        var reach = ports.Select(p => Reach(isl, p.Cell, 3 * take)).ToArray();
        var people = new Dictionary<int, int>();
        foreach (var t in isl.Towns) if (!cities.Contains(t)) people[t.Cell] = t.Population;

        // Each of three ports takes the first cells it reaches that neither of the others reaches sooner.
        (List<int>[] Cells, long[] People) Areas(int[] idx)
        {
            var cells = new List<int>[3];
            var pop = new long[3];
            for (int s = 0; s < 3; s++)
            {
                var (order, cost) = reach[idx[s]];
                var mine = cells[s] = new List<int>(take);
                foreach (int i in order)
                {
                    if (mine.Count >= take) break;
                    if (!claimable[i]) continue;
                    bool nearest = true;
                    for (int o = 0; o < 3; o++)
                        if (o != s && reach[idx[o]].Cost[i] < cost[i]) nearest = false;
                    if (!nearest) continue;
                    mine.Add(i);
                    if (people.TryGetValue(i, out int p)) pop[s] += p;
                }
            }
            return (cells, pop);
        }

        // The three as far apart as can be, with ports of like size and about as much in each area, so no side
        // starts much richer than another.
        int[] pick = Array.Empty<int>();
        double best = -1;
        for (int a = 0; a < ports.Count; a++)
        for (int b = a + 1; b < ports.Count; b++)
        for (int c = b + 1; c < ports.Count; c++)
        {
            var t = new[] { ports[a], ports[b], ports[c] };
            double apart = Math.Min(isl.Dist(t[0].Cell, t[1].Cell), Math.Min(isl.Dist(t[0].Cell, t[2].Cell), isl.Dist(t[1].Cell, t[2].Cell)));
            double evenPorts = (double)t.Min(x => x.Population) / t.Max(x => x.Population);
            var (cells, pop) = Areas(new[] { a, b, c });
            double evenAreas = (double)pop.Min() / Math.Max(1L, pop.Max());
            double full = (double)cells.Min(l => l.Count) / Math.Max(1, take);
            double score = apart * Math.Sqrt(evenPorts) * evenAreas * evenAreas * full;
            if (score <= best) continue;
            best = score;
            pick = new[] { a, b, c };
        }
        if (pick.Length == 0) return;
        // ALPHA starts furthest north (as on the battle maps), BRAVO the more easterly of the other two, CHARLIE the last.
        int alpha = pick.OrderBy(i => ports[i].Z).First();
        var rest = pick.Where(i => i != alpha).OrderByDescending(i => ports[i].X).ToArray();
        var sides = new[] { alpha, rest[0], rest[1] };
        var (areas, _) = Areas(sides);
        for (int s = 0; s < 3; s++)
        {
            foreach (int i in areas[s]) isl.StartOf[i] = (sbyte)s;
            isl.Starts.Add(new StartArea { Team = s, Port = ports[sides[s]].Id, Cells = areas[s].Count, AreaKm2 = areas[s].Count * isl.CellArea / 1e6f });
        }
    }

    /// <summary>
    /// Travel time from a port to the first <paramref name="limit"/> cells it reaches. Steep ground, marsh and rock are
    /// slow; roads are fast. Returns those cells in the order reached, and every cell's time (infinite where it didn't
    /// get).
    /// </summary>
    static (int[] Order, float[] Cost) Reach(Island isl, int from, int limit)
    {
        int n = isl.N;
        var cost = new float[isl.Count];
        Array.Fill(cost, float.PositiveInfinity);
        var order = new List<int>(limit);
        var pq = new PriorityQueue<int, float>();
        cost[from] = 0f;
        pq.Enqueue(from, 0f);
        while (order.Count < limit && pq.TryDequeue(out int i, out float ci))
        {
            if (ci > cost[i]) continue;
            order.Add(i);
            int x = i % n, y = i / n;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Island.DX[k], ny = y + Island.DY[k];
                if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                int j = ny * n + nx;
                if (isl.Water(j)) continue;
                float d = isl.Cell * Island.DL[k];
                float grade = MathF.Abs(isl.Height[j] - isl.Height[i]) / d;
                float step = d * (1f + grade / 0.15f * (grade / 0.15f)) * (isl.RoadAt[j] != RoadClass.None ? 0.4f : 1f)
                             * (isl.Land[j] is Cover.Marsh or Cover.Rock ? 2f : 1f);
                if (ci + step >= cost[j]) continue;
                cost[j] = ci + step;
                pq.Enqueue(j, cost[j]);
            }
        }
        return (order.ToArray(), cost);
    }

    static void Nodes(Island isl, Random rng)
    {
        int cnt = isl.Count;
        var urban = new bool[cnt];
        for (int i = 0; i < cnt; i++) urban[i] = isl.Land[i] == Cover.Urban;
        var dUrban = Grid.Distance(urban, isl.N, isl.Cell);
        float Fuel(int i) => isl.Height[i] < 350f && isl.Slope[i] < 12f ? Math.Clamp(isl.Rock[i] - 0.7f, 0f, 1.5f) * (1f - isl.Height[i] / 400f) : 0f;
        float Ore(int i) => isl.Slope[i] < 25f ? Math.Clamp((isl.Height[i] - 250f) / 900f, 0f, 1f) * Math.Clamp(1.4f - isl.Rock[i], 0f, 1f) : 0f;
        bool Ok(int i) => !isl.Water(i) && isl.Land[i] != Cover.Urban && dUrban[i] > 1500f && isl.Slope[i] < 25f;
        var placed = isl.Nodes;
        bool Spaced(int i, float min) => placed.All(p => isl.Dist(p.Cell, i) >= min);
        void Add(int i, NodeKind kind, int start) => placed.Add(new ResourceNode
        {
            Id = placed.Count, Cell = i, Kind = kind, Start = start, Richness = 0.6f + 0.8f * (float)rng.NextDouble(),
        });

        foreach (var s in isl.Starts)
        {
            var port = isl.Towns[s.Port];
            int bestCell = -1;
            float bestKey = 0f;
            for (int i = 0; i < cnt; i++)
            {
                if (isl.StartOf[i] != s.Team || !Ok(i)) continue;
                float d = isl.Dist(i, port.Cell);
                if (d < 3000f || d > 15000f) continue;
                float key = (MathF.Max(Fuel(i), Ore(i)) + 0.05f) * (0.7f + 0.6f * (float)rng.NextDouble());
                if (key <= bestKey) continue;
                bestKey = key;
                bestCell = i;
            }
            if (bestCell >= 0) Add(bestCell, Fuel(bestCell) >= Ore(bestCell) ? NodeKind.Fuel : NodeKind.Ore, s.Team);
        }

        // The rest by turns, fuel then ore, each list in a random order weighted by how promising the rock is
        // (Efraimidis and Spirakis's weighted sampling).
        int want = placed.Count + 16 + rng.Next(7);
        int[] Ordered(NodeKind kind)
        {
            var cells = new List<int>();
            var keys = new List<float>();
            for (int i = 0; i < cnt; i++)
            {
                if (!Ok(i) || isl.StartOf[i] >= 0) continue;
                float f = Fuel(i), o = Ore(i), w = kind == NodeKind.Fuel ? f : o;
                if (w <= 0.05f || (kind == NodeKind.Fuel ? o > f : f >= o)) continue;
                cells.Add(i);
                keys.Add(-(float)Math.Pow(rng.NextDouble(), 1.0 / w));
            }
            var a = cells.ToArray();
            Array.Sort(keys.ToArray(), a);
            return a;
        }
        var lists = new[] { Ordered(NodeKind.Fuel), Ordered(NodeKind.Ore) };
        var at = new int[2];
        for (int turn = 0; placed.Count < want && (at[0] < lists[0].Length || at[1] < lists[1].Length); turn ^= 1)
        {
            var list = lists[turn];
            while (at[turn] < list.Length && !Spaced(list[at[turn]], 7000f)) at[turn]++;
            if (at[turn] < list.Length) Add(list[at[turn]++], (NodeKind)turn, -1);
        }

        // Each named for the nearest place that hasn't already lent its name to one of the same kind.
        var net = new RoadNet(isl);
        var names = new HashSet<string>();
        foreach (var node in placed)
        {
            string suffix = node.Kind == NodeKind.Fuel ? " Oilfield" : " Mine";
            node.Name = isl.Towns.OrderBy(t => isl.Dist(t.Cell, node.Cell)).Select(t => t.Name + suffix).First(names.Add);
            int road = NearestRoad(isl, node.Cell, 10_000f);
            if (road >= 0) net.Connect(node.Cell, road, RoadClass.Track);
        }
    }

    /// <summary>The nearest road cell over land (breadth first), within <paramref name="maxM"/>; -1 if none.</summary>
    static int NearestRoad(Island isl, int from, float maxM)
    {
        int n = isl.N;
        var seen = new HashSet<int> { from };
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (isl.RoadAt[i] != RoadClass.None) return i;
            int x = i % n, y = i / n;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Island.DX[k], ny = y + Island.DY[k];
                if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                int j = ny * n + nx;
                if (isl.Water(j) || isl.Dist(j, from) > maxM || !seen.Add(j)) continue;
                queue.Enqueue(j);
            }
        }
        return -1;
    }
}
