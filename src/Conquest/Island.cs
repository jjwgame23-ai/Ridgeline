namespace Ridgeline;

/// <summary>What covers the ground in one cell of the island map.</summary>
public enum Cover : byte { Sea, Lake, Beach, Marsh, Rock, Grass, Garrigue, Maquis, Pine, Oak, Montane, Fields, Orchards, Urban }

public enum TownKind : byte { Hamlet, Village, Town, City }

public sealed class Town
{
    public int Id, Cell, Rank, Population;
    public string Name = "";
    public float X, Z, RadiusM, Elevation, Harbour;
    public TownKind Kind;
    public bool Port, Hilltop;
}

public enum RoadClass : byte { None, Track, Secondary, Main }

public sealed class Road
{
    public RoadClass Class;
    public List<int> Cells = new();
}

/// <summary>Where a road meets a stream: a bridge, or on a small one a ford.</summary>
public sealed class StreamCrossing
{
    public int Cell;
    public bool Bridge;
    public float WidthM;
    public RoadClass Road;
}

public enum NodeKind : byte { Fuel, Ore }

public sealed class ResourceNode
{
    public int Id, Cell;
    public NodeKind Kind;
    public float Richness;
    public string Name = "";
    /// <summary>The side whose starting area it's in, or -1.</summary>
    public int Start = -1;
}

public sealed class StartArea
{
    public int Team;
    /// <summary>The port it's built round (a town id).</summary>
    public int Port;
    public int Cells;
    public float AreaKm2;
}

public sealed class Peak
{
    public int Cell;
    public float Height, Relief;
    public string Name = "";
}

public sealed class River
{
    public string Name = "";
    /// <summary>The main stem: the mouth first, then up the biggest branch to the source.</summary>
    public List<int> Cells = new();
    public float AreaKm2, LengthKm, FlowM3s;
}

/// <summary>
/// A generated island: a square grid of cells (100 m at the default 1280 × 1280 over 128 km), with what the generator
/// worked out for each cell, and the towns, roads and so on placed on it. Row 0 is the north edge; x runs east.
/// </summary>
public sealed class Island
{
    public readonly int Seed, N;
    public readonly float Cell, Extent;
    public readonly IslandClimate Climate;
    public string Name = "";

    public readonly float[] Height;    // m above sea level (the sea floor is negative)
    public readonly float[] Uplift;    // relative rock uplift rate the ground was made with, 0..~1.3
    public readonly float[] Rock;      // erodibility relative to middling rock, 1/3..3
    public readonly float[] Slope;     // degrees
    public readonly float[] Temp;      // annual mean, °C
    public readonly float[] Rain;      // annual, mm
    public readonly float[] Area;      // drainage area upstream of the cell, m²
    public readonly float[] Flow;      // mean annual discharge, m³/s
    public readonly float[] Longest;   // the longest flow path upstream, m
    public readonly float[] LakeLevel; // a lake's surface (m); NaN where there's none
    public readonly float[] Farmable;  // 0..1: how good the land is for farming
    public readonly int[] Receiver;    // the cell water flows on to (itself at the sea)
    public readonly byte[] Order;      // Strahler order of the stream through the cell (0: none)
    public readonly Cover[] Land;
    public readonly RoadClass[] RoadAt;
    public readonly sbyte[] StartOf;   // which side's starting area the cell is in (-1: none)
    /// <summary>Every cell, each one after the cell it drains to.</summary>
    public int[] Stack = Array.Empty<int>();

    public readonly List<Town> Towns = new();
    public readonly List<Road> Roads = new();
    public readonly List<StreamCrossing> Crossings = new();
    public readonly List<(float X, float Z)> Farms = new();
    public readonly List<ResourceNode> Nodes = new();
    public readonly List<StartArea> Starts = new();
    public readonly List<Peak> Peaks = new();
    public readonly List<River> Rivers = new();
    /// <summary>How far the sea rose over the land after it was carved (m).</summary>
    public float SeaRise;
    public readonly List<(string Stage, double Ms)> Timings = new();

    public Island(int seed, int n, float extent, IslandClimate climate)
    {
        Seed = seed;
        N = n;
        Extent = extent;
        Cell = extent / n;
        Climate = climate;
        int c = n * n;
        Height = new float[c]; Uplift = new float[c]; Rock = new float[c]; Slope = new float[c];
        Temp = new float[c]; Rain = new float[c]; Area = new float[c]; Flow = new float[c]; Longest = new float[c];
        LakeLevel = new float[c]; Farmable = new float[c]; Receiver = new int[c]; Order = new byte[c];
        Land = new Cover[c]; RoadAt = new RoadClass[c]; StartOf = new sbyte[c];
        Array.Fill(LakeLevel, float.NaN);
        Array.Fill(StartOf, (sbyte)-1);
    }

    public int Count => N * N;
    public float CellArea => Cell * Cell;
    public bool Sea(int i) => Land[i] == Cover.Sea;
    public bool Water(int i) => Land[i] is Cover.Sea or Cover.Lake;
    /// <summary>A stream with 2 km² of catchment: on the map, and needing a ford or a bridge.</summary>
    public bool Channel(int i) => Area[i] >= 2e6f && !Water(i);
    /// <summary>
    /// A stream's bankfull width (m). Width grows as the square root of discharge (Leopold and Maddock 1953) and
    /// discharge as about the 0.8 power of catchment, so width goes as about catchment^0.4.
    /// </summary>
    public float Width(int i) => 2.5f * MathF.Pow(Area[i] / 1e6f, 0.4f);

    /// <summary>Metres east of the island's centre.</summary>
    public float X(int i) => (i % N + 0.5f) * Cell - Extent / 2f;
    /// <summary>Metres south of the island's centre.</summary>
    public float Z(int i) => (i / N + 0.5f) * Cell - Extent / 2f;
    /// <summary>The cell at a point (metres east and south of the centre), or -1 off the map.</summary>
    public int CellAtWorld(float x, float z)
    {
        int cx = (int)MathF.Floor((x + Extent / 2f) / Cell), cz = (int)MathF.Floor((z + Extent / 2f) / Cell);
        return (uint)cx < (uint)N && (uint)cz < (uint)N ? cz * N + cx : -1;
    }

    public float Dist(int a, int b)
    {
        float dx = (a % N - b % N) * Cell, dz = (a / N - b / N) * Cell;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // The 8 neighbours, east first and round clockwise (y grows southward).
    public static readonly int[] DX = { 1, 1, 0, -1, -1, -1, 0, 1 };
    public static readonly int[] DY = { 0, 1, 1, 1, 0, -1, -1, -1 };
    public static readonly float[] DL = { 1f, 1.41421356f, 1f, 1.41421356f, 1f, 1.41421356f, 1f, 1.41421356f };
}

/// <summary>Whole-grid operations the generator shares: distances, box means, sliding maxima, bilinear samples.</summary>
public static class Grid
{
    /// <summary>Distance (m) from every cell to the nearest seed cell: a two-pass chamfer transform, at most ~8% long.</summary>
    public static float[] Distance(bool[] seed, int n, float cell)
    {
        var d = new float[n * n];
        for (int i = 0; i < d.Length; i++) d[i] = seed[i] ? 0f : 1e9f;
        float a = cell, b = cell * 1.4f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * n + x;
            float v = d[i];
            if (x > 0) v = MathF.Min(v, d[i - 1] + a);
            if (y > 0)
            {
                v = MathF.Min(v, d[i - n] + a);
                if (x > 0) v = MathF.Min(v, d[i - n - 1] + b);
                if (x < n - 1) v = MathF.Min(v, d[i - n + 1] + b);
            }
            d[i] = v;
        }
        for (int y = n - 1; y >= 0; y--)
        for (int x = n - 1; x >= 0; x--)
        {
            int i = y * n + x;
            float v = d[i];
            if (x < n - 1) v = MathF.Min(v, d[i + 1] + a);
            if (y < n - 1)
            {
                v = MathF.Min(v, d[i + n] + a);
                if (x < n - 1) v = MathF.Min(v, d[i + n + 1] + b);
                if (x > 0) v = MathF.Min(v, d[i + n - 1] + b);
            }
            d[i] = v;
        }
        return d;
    }

    /// <summary>The mean over the (2r+1)² square round each cell (clipped at the edges).</summary>
    public static float[] BoxMean(float[] v, int n, int r)
    {
        int w = n + 1;
        var s = new double[w * w];
        for (int y = 0; y < n; y++)
        {
            double row = 0;
            for (int x = 0; x < n; x++)
            {
                row += v[y * n + x];
                s[(y + 1) * w + x + 1] = s[y * w + x + 1] + row;
            }
        }
        var o = new float[n * n];
        Parallel.For(0, n, y =>
        {
            int y0 = Math.Max(0, y - r), y1 = Math.Min(n, y + r + 1);
            for (int x = 0; x < n; x++)
            {
                int x0 = Math.Max(0, x - r), x1 = Math.Min(n, x + r + 1);
                double sum = s[y1 * w + x1] - s[y0 * w + x1] - s[y1 * w + x0] + s[y0 * w + x0];
                o[y * n + x] = (float)(sum / ((y1 - y0) * (x1 - x0)));
            }
        });
        return o;
    }

    /// <summary>The largest (or smallest) value in the (2r+1)² square round each cell: two sliding-window passes.</summary>
    public static float[] Extreme(float[] v, int n, int r, bool min)
    {
        var tmp = new float[v.Length];
        var o = new float[v.Length];
        Parallel.For(0, n, y => Pass(v, tmp, y * n, 1, n, r, min, new int[n]));
        Parallel.For(0, n, x => Pass(tmp, o, x, n, n, r, min, new int[n]));
        return o;
    }

    static void Pass(float[] src, float[] dst, int start, int stride, int len, int r, bool min, int[] dq)
    {
        int head = 0, tail = 0, j = 0;
        for (int i = 0; i < len; i++)
        {
            for (; j < len && j <= i + r; j++)
            {
                float vj = src[start + j * stride];
                while (tail > head && (min ? src[start + dq[tail - 1] * stride] >= vj : src[start + dq[tail - 1] * stride] <= vj)) tail--;
                dq[tail++] = j;
            }
            while (dq[head] < i - r) head++;
            dst[start + i * stride] = src[start + dq[head] * stride];
        }
    }

    /// <summary>Bilinear sample at (x, y) in cell units (cell centres on the integers); <paramref name="outside"/> off the grid.</summary>
    public static float Sample(float[] a, int n, float x, float y, float outside)
    {
        if (x < 0f || y < 0f || x > n - 1 || y > n - 1) return outside;
        int x0 = Math.Min((int)x, n - 2), y0 = Math.Min((int)y, n - 2);
        float fx = x - x0, fy = y - y0;
        int i = y0 * n + x0;
        float top = a[i] + (a[i + 1] - a[i]) * fx, bot = a[i + n] + (a[i + n + 1] - a[i + n]) * fx;
        return top + (bot - top) * fy;
    }
}
