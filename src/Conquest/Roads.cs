namespace Ridgeline;

/// <summary>
/// The road network.
/// - Which places are joined: the relative neighbourhood graph (two places are linked unless a third is nearer to
///   both). Real road networks between towns look much like it. Main roads link the towns, secondary roads the
///   villages, and tracks run out to the hamlets.
/// - Each road is routed by least cost over the ground. Steep grades cost more the further they pass what the road's
///   class is built to (main roads about 5%, tracks 10%), so roads follow the valleys. Marsh and rock cost more,
///   crossing a stream costs more the wider it is (so crossings find narrow places), and an existing road is cheap,
///   so later roads join earlier ones instead of running beside them.
/// - Where a road crosses a stream there's a bridge, or a ford on a small stream and a minor road.
/// </summary>
public static class RoadBuilder
{
    public static void Apply(Island isl, Random rng)
    {
        var net = new RoadNet(isl);
        var towns = isl.Towns;
        var major = towns.Where(t => t.Kind >= TownKind.Town || (t.Port && t.Population >= 1500)).ToList();
        foreach (var (a, b) in Neighbours(isl, major, 40_000f)) net.Connect(a.Cell, b.Cell, RoadClass.Main);
        var villages = towns.Where(t => t.Kind >= TownKind.Village).ToList();
        foreach (var (a, b) in Neighbours(isl, villages, 15_000f)) net.Connect(a.Cell, b.Cell, RoadClass.Secondary);
        // Each hamlet on a track to the nearest place already joined up.
        var joined = new List<Town>(villages);
        foreach (var h in towns.Where(t => t.Kind == TownKind.Hamlet))
        {
            Town? near = null;
            float best = 8000f;
            foreach (var t in joined)
            {
                float d = isl.Dist(h.Cell, t.Cell);
                if (d < best)
                {
                    best = d;
                    near = t;
                }
            }
            if (near != null) net.Connect(h.Cell, near.Cell, RoadClass.Track);
            joined.Add(h);
        }
        Farms(isl, rng);
    }

    /// <summary>Pairs of places with no third nearer to both, up to <paramref name="maxM"/> apart, shortest first.</summary>
    static List<(Town A, Town B)> Neighbours(Island isl, List<Town> pts, float maxM)
    {
        int m = pts.Count;
        var d = new float[m, m];
        for (int i = 0; i < m; i++)
        for (int j = i + 1; j < m; j++)
            d[i, j] = d[j, i] = isl.Dist(pts[i].Cell, pts[j].Cell);
        var edges = new List<(Town, Town, float)>();
        for (int i = 0; i < m; i++)
        for (int j = i + 1; j < m; j++)
        {
            float dij = d[i, j];
            if (dij > maxM) continue;
            bool ok = true;
            for (int k = 0; k < m && ok; k++)
                if (k != i && k != j && MathF.Max(d[i, k], d[j, k]) < dij) ok = false;
            if (ok) edges.Add((pts[i], pts[j], dij));
        }
        return edges.OrderBy(e => e.Item3).Select(e => (e.Item1, e.Item2)).ToList();
    }

    /// <summary>Farmsteads among the fields and orchards: about one per 35 ha near a road, fewer further off.</summary>
    static void Farms(Island isl, Random rng)
    {
        var road = new bool[isl.Count];
        for (int i = 0; i < road.Length; i++) road[i] = isl.RoadAt[i] != RoadClass.None;
        var dRoad = Grid.Distance(road, isl.N, isl.Cell);
        float perCell = isl.CellArea / 1e4f; // hectares
        for (int i = 0; i < isl.Count; i++)
        {
            if (isl.Land[i] is not (Cover.Fields or Cover.Orchards)) continue;
            float perHa = dRoad[i] < 400f ? 1f / 35f : dRoad[i] < 1200f ? 1f / 70f : 1f / 200f;
            if (rng.NextDouble() >= perHa * perCell) continue;
            float jx = (float)(rng.NextDouble() - 0.5) * isl.Cell, jz = (float)(rng.NextDouble() - 0.5) * isl.Cell;
            isl.Farms.Add((isl.X(i) + jx, isl.Z(i) + jz));
        }
    }
}

/// <summary>Builds roads on an island: routes them, marks their cells and records their stream crossings.</summary>
public sealed class RoadNet
{
    readonly Island _isl;
    readonly PathFinder _find;
    readonly Dictionary<int, StreamCrossing> _cross = new();

    public RoadNet(Island isl)
    {
        _isl = isl;
        _find = new PathFinder(isl);
        foreach (var c in isl.Crossings) _cross[c.Cell] = c;
    }

    public Road? Connect(int a, int b, RoadClass cls)
    {
        var path = _find.Find(a, b, cls);
        if (path == null) return null;
        var isl = _isl;
        for (int s = 0; s < path.Count; s++)
        {
            int c = path[s];
            if (isl.RoadAt[c] < cls) isl.RoadAt[c] = cls;
            if (s == 0) continue;
            int x = _find.CrossingAt(path[s - 1], c);
            if (x < 0) continue;
            if (!_cross.TryGetValue(x, out var cr))
            {
                cr = new StreamCrossing { Cell = x, WidthM = isl.Width(x) };
                _cross[x] = cr;
                isl.Crossings.Add(cr);
            }
            if (cls > cr.Road) cr.Road = cls;
            cr.Bridge = cr.Road >= RoadClass.Secondary ? cr.WidthM >= 3.5f : cr.WidthM >= 6f;
        }
        var road = new Road { Class = cls, Cells = path };
        isl.Roads.Add(road);
        return road;
    }
}

/// <summary>A* over the island's cells, priced for building a road of a given class.</summary>
public sealed class PathFinder
{
    readonly Island _isl;
    readonly float[] _g;
    readonly int[] _from, _seen, _done;
    int _stamp;
    readonly PriorityQueue<int, float> _open = new();

    public PathFinder(Island isl)
    {
        _isl = isl;
        int c = isl.Count;
        _g = new float[c];
        _from = new int[c];
        _seen = new int[c];
        _done = new int[c];
    }

    public List<int>? Find(int a, int b, RoadClass cls, int maxExpand = 3_000_000)
    {
        _stamp++;
        _open.Clear();
        int n = _isl.N;
        _g[a] = 0f;
        _from[a] = -1;
        _seen[a] = _stamp;
        _open.Enqueue(a, H(a, b));
        int expanded = 0;
        while (_open.TryDequeue(out int i, out _))
        {
            if (_done[i] == _stamp) continue;
            _done[i] = _stamp;
            if (i == b)
            {
                var path = new List<int>();
                for (int c = b; c >= 0; c = _from[c]) path.Add(c);
                path.Reverse();
                return path;
            }
            if (++expanded > maxExpand) return null;
            int x = i % n, y = i / n;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Island.DX[k], ny = y + Island.DY[k];
                if ((uint)nx >= (uint)n || (uint)ny >= (uint)n) continue;
                int j = ny * n + nx;
                if (_done[j] == _stamp) continue;
                float cost = StepCost(i, j, k, cls);
                if (float.IsPositiveInfinity(cost)) continue;
                float ng = _g[i] + cost;
                if (_seen[j] == _stamp && ng >= _g[j]) continue;
                _seen[j] = _stamp;
                _g[j] = ng;
                _from[j] = i;
                _open.Enqueue(j, ng + H(j, b));
            }
        }
        return null;
    }

    /// <summary>A little under the cheapest possible cost per metre (riding an existing road through town), so A* stays near optimal.</summary>
    float H(int a, int b) => _isl.Dist(a, b) * 0.45f;

    float StepCost(int i, int j, int k, RoadClass cls)
    {
        var isl = _isl;
        if (isl.Water(j)) return float.PositiveInfinity;
        float d = isl.Cell * Island.DL[k];
        float grade = MathF.Abs(isl.Height[j] - isl.Height[i]) / d;
        float g0 = cls == RoadClass.Main ? 0.05f : cls == RoadClass.Secondary ? 0.07f : 0.1f;
        float over = MathF.Max(0f, grade - g0) / g0;
        float steep = 5f * over * over + (grade > 0.4f ? 60f : 0f);
        float ground = isl.Land[j] switch
        {
            Cover.Marsh => 2.5f,
            Cover.Rock => 1.8f,
            Cover.Pine or Cover.Oak or Cover.Montane => 1.15f,
            Cover.Maquis => 1.1f,
            Cover.Urban => 0.85f,
            _ => 1f,
        };
        float reuse = isl.RoadAt[j] >= RoadClass.Secondary ? 0.4f : isl.RoadAt[j] == RoadClass.Track ? 0.6f : 1f;
        float cost = d * (1f + steep) * ground * reuse;
        int cross = CrossingAt(i, j, k);
        if (cross >= 0) cost += 150f + 40f * isl.Width(cross);
        else if (isl.Channel(i) && isl.Channel(j)) cost += 2f * d; // along a stream bed
        return cost;
    }

    /// <summary>The stream cell a step from <paramref name="i"/> to <paramref name="j"/> crosses, or -1.</summary>
    public int CrossingAt(int i, int j)
    {
        int n = _isl.N;
        int dx = j % n - i % n, dy = j / n - i / n;
        for (int k = 0; k < 8; k++)
            if (Island.DX[k] == dx && Island.DY[k] == dy) return CrossingAt(i, j, k);
        return -1;
    }

    /// <summary>Stepping onto a stream, or cutting diagonally across one between two of its cells.</summary>
    int CrossingAt(int i, int j, int k)
    {
        var isl = _isl;
        if (isl.Channel(j) && !isl.Channel(i)) return j;
        if ((k & 1) == 1)
        {
            int n = isl.N, x = i % n, y = i / n;
            int a = y * n + x + Island.DX[k], b = (y + Island.DY[k]) * n + x;
            if (isl.Channel(a) && isl.Channel(b) && (isl.Receiver[a] == b || isl.Receiver[b] == a))
                return isl.Area[a] > isl.Area[b] ? a : b;
        }
        return -1;
    }
}
