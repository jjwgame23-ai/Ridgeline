namespace Ridgeline;

/// <summary>How a unit gets about: on foot, on wheels, or on tracks.</summary>
public enum Mobility : byte { Foot, Wheeled, Tracked }

/// <summary>
/// Movement over the island for the abstract war, on a 400 m grid (four island cells a side).
/// - Each cell has a speed for each kind of mover, from its roads, slope and ground. March rates are doctrine's:
///   - On foot, 4 km/h on roads and 2.4 km/h cross-country, with the hourly halts included (FM 3-21.18). Slower up
///     steep ground and through maquis, rock and marsh.
///   - Wheeled convoys run at 50 km/h on main roads, 35 on secondary roads and 20 on tracks (FM 55-30's road march
///     rates). Off road only over open ground under 20°.
///   - Tracked vehicles do 35 km/h on roads and 20 across open country, less through scrub and woods, and nothing
///     over 30°.
/// - Rivers. A stream too big to wade or ford (20 km² of catchment for wheels, 50 km² for tracks) can only be crossed
///   where a road bridges or fords it. People on foot wade anything but lose half an hour at a big river.
/// - Paths are found by A* on these times.
/// </summary>
public sealed class MoveGrid
{
    public const int Factor = 4;
    public readonly int N;
    public readonly float Cell;
    readonly Island _isl;
    /// <summary>Speed in m/s for each mobility, 0 where it can't go.</summary>
    readonly float[][] _speed = new float[3][];
    /// <summary>Extra seconds to get across the cell (a big river waded on foot).</summary>
    readonly float[] _wade;
    /// <summary>For each side, how much slower a cell counts for routing: enemy ground and contested ground are avoided.</summary>
    readonly float[][] _danger = new float[3][];
    readonly float[] _g;
    readonly int[] _from, _seen, _done;
    int _stamp;
    readonly PriorityQueue<int, float> _open = new();

    public MoveGrid(Island isl)
    {
        _isl = isl;
        N = isl.N / Factor;
        Cell = isl.Cell * Factor;
        int c = N * N;
        for (int m = 0; m < 3; m++) _speed[m] = new float[c];
        _wade = new float[c];
        for (int s = 0; s < 3; s++)
        {
            _danger[s] = new float[c];
            Array.Fill(_danger[s], 1f);
        }
        _g = new float[c];
        _from = new int[c];
        _seen = new int[c];
        _done = new int[c];
        var crossing = new HashSet<int>(isl.Crossings.Select(x => x.Cell));
        Parallel.For(0, N, cy =>
        {
            for (int cx = 0; cx < N; cx++)
            {
                int ci = cy * N + cx, water = 0, land = 0;
                float slope = 0f, river = 0f;
                bool bridged = false;
                RoadClass road = RoadClass.None;
                var cover = new int[Enum.GetValues<Cover>().Length];
                for (int y = cy * Factor; y < (cy + 1) * Factor; y++)
                for (int x = cx * Factor; x < (cx + 1) * Factor; x++)
                {
                    int i = y * isl.N + x;
                    if (isl.Water(i))
                    {
                        water++;
                        continue;
                    }
                    land++;
                    slope += isl.Slope[i];
                    cover[(int)isl.Land[i]]++;
                    if (isl.RoadAt[i] > road) road = isl.RoadAt[i];
                    if (crossing.Contains(i)) bridged = true;
                    else if (isl.Channel(i)) river = MathF.Max(river, isl.Area[i]);
                }
                if (land < water || land == 0) continue; // sea or lake: nothing goes
                slope /= land;
                int top = 0;
                for (int k = 1; k < cover.Length; k++) if (cover[k] > cover[top]) top = k;
                var ground = (Cover)top;
                bool hasRoad = road != RoadClass.None;
                // On foot: Tobler's hiking function for the slope, normalised to flat ground, times the ground's going.
                float tobler = MathF.Exp(-3.5f * MathF.Abs(MathF.Tan(slope * MathF.PI / 180f) + 0.05f)) / MathF.Exp(-3.5f * 0.05f);
                float going = ground switch
                {
                    Cover.Maquis => 0.6f, Cover.Pine or Cover.Oak or Cover.Montane => 0.8f, Cover.Rock => 0.6f, Cover.Marsh => 0.4f,
                    Cover.Garrigue => 0.9f, _ => 1f,
                };
                float foot = hasRoad ? 4f : 2.4f * tobler * going;
                // Wheels: road speed by class; off road only on open ground and gentle slopes.
                bool open = ground is Cover.Fields or Cover.Orchards or Cover.Grass or Cover.Garrigue or Cover.Beach or Cover.Urban;
                float wheeled = road switch { RoadClass.Main => 50f, RoadClass.Secondary => 35f, RoadClass.Track => 20f, _ => 0f };
                if (!hasRoad && open) wheeled = slope < 10f ? 12f : slope < 20f ? 6f : 0f;
                // Tracks: roads, or most open country, slowed by scrub and woods.
                float tracked = road is RoadClass.Main or RoadClass.Secondary ? 35f : road == RoadClass.Track ? 25f : 0f;
                if (!hasRoad && slope < 30f)
                    tracked = (ground switch { Cover.Marsh => 0f, Cover.Rock => 5f, Cover.Maquis or Cover.Pine or Cover.Oak or Cover.Montane => 8f, _ => 20f })
                              * (slope < 15f ? 1f : 0.5f);
                // Rivers with no bridge or ford in the cell.
                if (!bridged)
                {
                    if (river >= 20e6f) wheeled = 0f;
                    if (river >= 50e6f) tracked = 0f;
                    if (river >= 50e6f) _wade[ci] = 1800f;
                }
                _speed[0][ci] = foot / 3.6f;
                _speed[1][ci] = wheeled / 3.6f;
                _speed[2][ci] = tracked / 3.6f;
            }
        });
    }

    public int CellOf(float x, float z)
    {
        int cx = Math.Clamp((int)((x + _isl.Extent / 2f) / Cell), 0, N - 1), cz = Math.Clamp((int)((z + _isl.Extent / 2f) / Cell), 0, N - 1);
        return cz * N + cx;
    }

    public float CX(int c) => (c % N + 0.5f) * Cell - _isl.Extent / 2f;
    public float CZ(int c) => (c / N + 0.5f) * Cell - _isl.Extent / 2f;

    /// <summary>Speed (m/s) at a cell for a mobility; 0 where it can't go.</summary>
    public float Speed(int c, Mobility m) => _speed[(int)m][c];

    public bool Passable(int c, Mobility m) => _speed[(int)m][c] > 0f;

    /// <summary>The nearest cell a mover of this kind can stand on, within 3 km; -1 if none.</summary>
    public int Nearest(int c, Mobility m)
    {
        if (Passable(c, m)) return c;
        int cx = c % N, cy = c / N, best = -1;
        float bd = float.MaxValue;
        for (int r = 1; r <= 8 && best < 0; r++)
            for (int y = Math.Max(0, cy - r); y <= Math.Min(N - 1, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x <= Math.Min(N - 1, cx + r); x++)
            {
                int i = y * N + x;
                if (!Passable(i, m)) continue;
                float d = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d < bd)
                {
                    bd = d;
                    best = i;
                }
            }
        return best;
    }

    /// <summary>
    /// Routes keep off ground an enemy holds (counted six times as slow), contested ground (four times), and ground
    /// within a kilometre of either (twice). An army doesn't march its columns across the enemy's front.
    /// </summary>
    public void UpdateDanger(Territory ctl)
    {
        for (int s = 0; s < 3; s++)
        {
            var dg = _danger[s];
            for (int c = 0; c < dg.Length; c++)
            {
                int t = ctl.CellOf(CX(c), CZ(c));
                float v = 1f;
                if (ctl.Contested[t]) v = 4f;
                else if (ctl.Owner[t] >= 0 && ctl.Owner[t] != s) v = 6f;
                else
                {
                    int tx = t % ctl.N, ty = t / ctl.N;
                    for (int k = 0; k < 8 && v == 1f; k++)
                    {
                        int nx = tx + Island.DX[k], ny = ty + Island.DY[k];
                        if ((uint)nx >= (uint)ctl.N || (uint)ny >= (uint)ctl.N) continue;
                        int j = ny * ctl.N + nx;
                        if (ctl.Contested[j] || (ctl.Owner[j] >= 0 && ctl.Owner[j] != s)) v = 2f;
                    }
                }
                dg[c] = v;
            }
        }
    }

    /// <summary>The quickest path between two cells for a mobility, as cells (start first), or null.</summary>
    public List<int>? Path(int a, int b, Mobility m, int side = -1)
    {
        a = Nearest(a, m);
        b = Nearest(b, m);
        if (a < 0 || b < 0) return null;
        var sp = _speed[(int)m];
        var danger = side >= 0 ? _danger[side] : null;
        float top = m == Mobility.Foot ? 4f / 3.6f : m == Mobility.Wheeled ? 50f / 3.6f : 35f / 3.6f;
        _stamp++;
        _open.Clear();
        _g[a] = 0f;
        _from[a] = -1;
        _seen[a] = _stamp;
        _open.Enqueue(a, 0f);
        int bx = b % N, by = b / N;
        float H(int i)
        {
            float dx = i % N - bx, dy = i / N - by;
            return MathF.Sqrt(dx * dx + dy * dy) * Cell / top;
        }
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
            int x = i % N, y = i / N;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Island.DX[k], ny = y + Island.DY[k];
                if ((uint)nx >= (uint)N || (uint)ny >= (uint)N) continue;
                int j = ny * N + nx;
                if (_done[j] == _stamp || sp[j] <= 0f) continue;
                // Half the step at each cell's speed, and any wading at the far one.
                float d = Cell * Island.DL[k];
                float ng = _g[i] + (0.5f * d / sp[i] + 0.5f * d / sp[j]) * (danger?[j] ?? 1f) + _wade[j];
                if (_seen[j] == _stamp && ng >= _g[j]) continue;
                _seen[j] = _stamp;
                _g[j] = ng;
                _from[j] = i;
                _open.Enqueue(j, ng + H(j));
            }
        }
        return null;
    }

    public static bool TrackedClass(VClass v) => v is VClass.Ifv or VClass.Tank or VClass.Howitzer or VClass.Rocket or VClass.Spaa or VClass.Engineer;

    /// <summary>Seats for passengers besides the crew (the infantry carried, or troops in the back of a truck).</summary>
    public static int Seats(VClass v) => v switch
    {
        VClass.Ifv => 7, VClass.Apc => 9, VClass.Truck => 16, VClass.Ltv => 3, VClass.Ambulance => 2, VClass.Tanker => 1,
        VClass.Helicopter => 11, _ => 1,
    };
}
