using Godot;

namespace Ridgeline;

/// <summary>
/// The ground of a playable window, read off the island round a point: what the terrain, water, roads, woods and fields
/// are at any spot in it, at the few metres a soldier sees, from the island's 100 m grid.
/// - Heights. The island's heights, interpolated smoothly (Catmull-Rom), with finer relief added: rougher where the
///   ground is steep (outcrops and gullies), gentle where it's flat, none under water.
/// - Rivers. Every stream the island maps (2 km² of catchment or more) runs down its cells as a smoothed line, its
///   bankfull width growing with catchment (Leopold and Maddock 1953, as the island has it) and its depth a twelfth of
///   that (gravel-bed rivers run about 10-15 times as wide as deep). The water surface falls downstream with the
///   island's cells, never rising. The channel is cut into the ground with sloping banks.
/// - Roads. The island's roads, from cell to cell, smoothed into curves: main roads 7 m wide, secondary 5.5 m, tracks
///   3.5 m. The ground is levelled across each road, eased along it, with a shoulder blending back into the slope.
///   Where a road crosses a river 3 m wide or more, the river keeps its channel and the road crosses on a bridge.
/// - Land cover. The island's cover for each cell, with the edges between cells wandered by noise so woods and fields
///   don't come in 100 m squares. Fields are a patchwork of crops a couple of hundred metres across.
/// Positions here are local: metres east and south of the window's middle.
/// </summary>
public sealed class WindowGround
{
    public readonly Island Isl;
    /// <summary>The window's middle on the island (metres east and south of the island's centre), and half its width.</summary>
    public readonly float CX, CZ, Half;
    readonly FastNoiseLite _relief, _fine, _warp, _fields, _meander;

    public sealed class Line
    {
        public Vector2[] P = Array.Empty<Vector2>();
        /// <summary>Width at each point (m), and the height of the water or the road there.</summary>
        public float[] W = Array.Empty<float>(), H = Array.Empty<float>();
        public RoadClass Class;
    }

    public readonly List<Line> Rivers = new(), Roads = new();
    /// <summary>Where roads cross rivers on bridges: the middle, the road's direction, the span, the road's width and height.</summary>
    public readonly List<(Vector2 At, Vector2 Dir, float Span, float Width, float Y)> Bridges = new();

    const float Bucket = 64f;
    readonly Dictionary<long, List<(int Line, int Seg)>> _riverAt = new(), _roadAt = new();

    public WindowGround(Island isl, float cx, float cz, float half)
    {
        Isl = isl;
        CX = cx;
        CZ = cz;
        Half = half;
        int seed = isl.Seed * 7919 + (int)cx * 31 + (int)cz;
        _relief = new FastNoiseLite { Seed = seed, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 1f / 90f, FractalOctaves = 4 };
        _fine = new FastNoiseLite { Seed = seed + 1, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 1f / 14f, FractalOctaves = 2 };
        _warp = new FastNoiseLite { Seed = seed + 2, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 1f / 120f, FractalOctaves = 3 };
        _fields = new FastNoiseLite { Seed = seed + 3, NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular, Frequency = 1f / 170f, CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.CellValue };
        _meander = new FastNoiseLite { Seed = seed + 4, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 1f / 160f, FractalOctaves = 2 };
        TraceRivers();
        TraceRoads();
        FindBridges();
    }

    // ---------------------------------------------------------------- the island's grid

    float At(float[] a, int gx, int gz) => a[Math.Clamp(gz, 0, Isl.N - 1) * Isl.N + Math.Clamp(gx, 0, Isl.N - 1)];

    static float CatmullRom(float a, float b, float c, float d, float t) =>
        b + 0.5f * t * (c - a + t * (2f * a - 5f * b + 4f * c - d + t * (3f * (b - c) + d - a)));

    /// <summary>The island's height at a local point, smoothly interpolated between its cells.</summary>
    public float Base(float x, float z)
    {
        float gx = (x + CX + Isl.Extent / 2f) / Isl.Cell - 0.5f, gz = (z + CZ + Isl.Extent / 2f) / Isl.Cell - 0.5f;
        int ix = (int)MathF.Floor(gx), iz = (int)MathF.Floor(gz);
        float tx = gx - ix, tz = gz - iz;
        Span<float> row = stackalloc float[4];
        for (int k = 0; k < 4; k++)
        {
            int zz = iz - 1 + k;
            row[k] = CatmullRom(At(Isl.Height, ix - 1, zz), At(Isl.Height, ix, zz), At(Isl.Height, ix + 1, zz), At(Isl.Height, ix + 2, zz), tx);
        }
        return CatmullRom(row[0], row[1], row[2], row[3], tz);
    }

    /// <summary>The island cell under a local point, or -1 off the island.</summary>
    public int CellAt(float x, float z) => Isl.CellAtWorld(x + CX, z + CZ);

    /// <summary>A local point's island position (cell centres are at island X(i), Z(i)).</summary>
    Vector2 Local(int i) => new(Isl.X(i) - CX, Isl.Z(i) - CZ);

    // ---------------------------------------------------------------- rivers

    /// <summary>The streams in and near the window as smoothed lines, head to mouth.</summary>
    void TraceRivers()
    {
        float reach = Half + 600f;
        bool Near(int i) => MathF.Abs(Isl.X(i) - CX) < reach && MathF.Abs(Isl.Z(i) - CZ) < reach;
        int n = Isl.Count;
        var donors = new int[n];
        for (int i = 0; i < n; i++)
            if (Isl.Channel(i) && Isl.Receiver[i] != i && Near(i)) donors[Isl.Receiver[i]]++;
        var done = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (!Isl.Channel(i) || !Near(i) || donors[i] > 0) continue;
            // A head: no channel flows in from within reach. Follow it down until the water, the edge, or a stream
            // already traced (joining it there).
            var cells = new List<int>();
            int c = i;
            while (true)
            {
                cells.Add(c);
                if (done[c] || Isl.Water(c) || !Near(c)) break;
                done[c] = true;
                int r = Isl.Receiver[c];
                if (r == c) break;
                c = r;
            }
            if (cells.Count < 2) continue;
            var pts = cells.Select(Local).ToList();
            var w = cells.Select(k => Isl.Water(k) ? Isl.Width(cells[^2]) : Isl.Width(k)).ToList();
            // The water surface: the island's height at each cell, a little below the banks, never rising downstream.
            var h = new List<float>();
            float run = float.MaxValue;
            foreach (int k in cells)
            {
                float s = Isl.Water(k) ? (Isl.Sea(k) ? 0f : Isl.LakeLevel[k]) : Isl.Height[k] - 0.4f;
                if (float.IsNaN(s)) s = Isl.Height[k];
                run = MathF.Min(run, s);
                h.Add(run);
            }
            var line = Smooth(pts, w, h, 4);
            line.Class = RoadClass.None;
            // Meanders. A stream wanders from cell to cell rather than running straight: every river point is moved by
            // one smooth field, up to about 30 m, bending a line into loops a few hundred metres long (meanders run
            // about 10-14 channel widths, Leopold and Wolman 1960). Moving every river by the same field keeps a
            // tributary's mouth on the stream it joins.
            for (int k = 0; k < line.P.Length; k++)
            {
                var q = line.P[k];
                line.P[k] = q + new Vector2(_meander.GetNoise2D(q.X, q.Y), _meander.GetNoise2D(q.X + 5000f, q.Y - 3000f)) * 30f;
            }
            Rivers.Add(line);
        }
        Index(Rivers, _riverAt, l => l.W.Max() / 2f + Bank(l.W.Max()));
    }

    /// <summary>A river's banks: as wide as half the channel, at least 3 m.</summary>
    static float Bank(float w) => MathF.Max(3f, w * 0.5f);

    /// <summary>Bankfull depth: a twelfth of the width, at least 0.4 m.</summary>
    static float Depth(float w) => MathF.Max(0.4f, w / 12f);

    // ---------------------------------------------------------------- roads

    void TraceRoads()
    {
        float reach = Half + 400f;
        foreach (var road in Isl.Roads)
        {
            // The stretches of the road near the window, each as its own line.
            var run = new List<int>();
            void Flush()
            {
                if (run.Count >= 2)
                {
                    float w = road.Class switch { RoadClass.Main => 7f, RoadClass.Secondary => 5.5f, _ => 3.5f };
                    var pts = run.Select(Local).ToList();
                    var line = Smooth(pts, pts.Select(_ => w).ToList(), pts.Select(_ => 0f).ToList(), 3);
                    line.Class = road.Class;
                    // The road's height: the ground along it, eased over about 60 m either way.
                    var raw = line.P.Select(p => Base(p.X, p.Y)).ToArray();
                    for (int k = 0; k < line.P.Length; k++)
                    {
                        float sum = 0f, wt = 0f;
                        for (int j = Math.Max(0, k - 3); j <= Math.Min(line.P.Length - 1, k + 3); j++)
                        {
                            float f = 4f - Math.Abs(j - k);
                            sum += raw[j] * f;
                            wt += f;
                        }
                        line.H[k] = MathF.Max(sum / wt, 0.3f);
                    }
                    Roads.Add(line);
                }
                run.Clear();
            }
            foreach (int c in road.Cells)
            {
                if (MathF.Abs(Isl.X(c) - CX) < reach && MathF.Abs(Isl.Z(c) - CZ) < reach) run.Add(c);
                else
                {
                    if (run.Count > 0) run.Add(c); // out to the next cell, so the road runs off the edge
                    Flush();
                }
            }
            Flush();
        }
        Index(Roads, _roadAt, l => l.W[0] / 2f + Shoulder);
    }

    const float Shoulder = 6f;

    /// <summary>Bridges where roads meet rivers 3 m wide or more.</summary>
    void FindBridges()
    {
        foreach (var road in Roads)
            for (int s = 0; s + 1 < road.P.Length; s++)
            {
                var a = road.P[s];
                var b = road.P[s + 1];
                foreach (var (li, si) in Candidates(_riverAt, (a + b) / 2f))
                {
                    var river = Rivers[li];
                    var c = river.P[si];
                    var d = river.P[si + 1];
                    if (!Cross(a, b, c, d, out float t, out float u)) continue;
                    float w = Mathf.Lerp(river.W[si], river.W[si + 1], u);
                    if (w < 3f) continue;
                    var at = a.Lerp(b, t);
                    var dir = (b - a).Normalized();
                    // Across the channel and its banks, square to the river or not.
                    var rd = (d - c).Normalized();
                    float sin = MathF.Max(0.35f, MathF.Abs(dir.X * rd.Y - dir.Y * rd.X));
                    float span = (w + 2f * Bank(w)) / sin + 4f;
                    float y = Mathf.Lerp(road.H[s], road.H[s + 1], t);
                    float water = Mathf.Lerp(river.H[si], river.H[si + 1], u);
                    // The deck clears the water by a metre and a half; the road rises to it over 40 m either side.
                    float deck = MathF.Max(y, water + 1.5f);
                    for (int k = 0; k < road.P.Length; k++)
                    {
                        float dd = road.P[k].DistanceTo(at);
                        road.H[k] = MathF.Max(road.H[k], Mathf.Lerp(deck, road.H[k], Mathf.SmoothStep(span / 2f, span / 2f + 40f, dd)));
                    }
                    Bridges.Add((at, dir, span, road.W[0] + 1f, deck));
                }
            }
    }

    static bool Cross(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out float t, out float u)
    {
        var r = b - a;
        var s = d - c;
        float den = r.X * s.Y - r.Y * s.X;
        t = u = 0f;
        if (MathF.Abs(den) < 1e-6f) return false;
        var q = c - a;
        t = (q.X * s.Y - q.Y * s.X) / den;
        u = (q.X * r.Y - q.Y * r.X) / den;
        return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
    }

    // ---------------------------------------------------------------- lines

    /// <summary>Chaikin's corner cutting, <paramref name="rounds"/> times: a line through 100 m cells turned into a curve. The ends stay put.</summary>
    static Line Smooth(List<Vector2> p, List<float> w, List<float> h, int rounds)
    {
        for (int r = 0; r < rounds; r++)
        {
            var np = new List<Vector2> { p[0] };
            var nw = new List<float> { w[0] };
            var nh = new List<float> { h[0] };
            for (int k = 0; k + 1 < p.Count; k++)
            {
                np.Add(p[k].Lerp(p[k + 1], 0.25f));
                np.Add(p[k].Lerp(p[k + 1], 0.75f));
                nw.Add(Mathf.Lerp(w[k], w[k + 1], 0.25f));
                nw.Add(Mathf.Lerp(w[k], w[k + 1], 0.75f));
                nh.Add(Mathf.Lerp(h[k], h[k + 1], 0.25f));
                nh.Add(Mathf.Lerp(h[k], h[k + 1], 0.75f));
            }
            np.Add(p[^1]);
            nw.Add(w[^1]);
            nh.Add(h[^1]);
            p = np;
            w = nw;
            h = nh;
        }
        return new Line { P = p.ToArray(), W = w.ToArray(), H = h.ToArray() };
    }

    static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

    static void Index(List<Line> lines, Dictionary<long, List<(int, int)>> at, Func<Line, float> reach)
    {
        for (int li = 0; li < lines.Count; li++)
        {
            var l = lines[li];
            float r = reach(l);
            for (int s = 0; s + 1 < l.P.Length; s++)
            {
                var a = l.P[s];
                var b = l.P[s + 1];
                int x0 = (int)MathF.Floor((MathF.Min(a.X, b.X) - r) / Bucket), x1 = (int)MathF.Floor((MathF.Max(a.X, b.X) + r) / Bucket);
                int z0 = (int)MathF.Floor((MathF.Min(a.Y, b.Y) - r) / Bucket), z1 = (int)MathF.Floor((MathF.Max(a.Y, b.Y) + r) / Bucket);
                for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long k = Key(x, z);
                    if (!at.TryGetValue(k, out var list)) at[k] = list = new List<(int, int)>();
                    list.Add((li, s));
                }
            }
        }
    }

    static IEnumerable<(int Line, int Seg)> Candidates(Dictionary<long, List<(int, int)>> at, Vector2 p) =>
        at.TryGetValue(Key((int)MathF.Floor(p.X / Bucket), (int)MathF.Floor(p.Y / Bucket)), out var list) ? list : Enumerable.Empty<(int, int)>();

    /// <summary>The nearest line to a point among those indexed: its distance, and its width and height there.</summary>
    static bool Nearest(List<Line> lines, Dictionary<long, List<(int, int)>> at, Vector2 p, out float dist, out float w, out float h)
    {
        dist = float.MaxValue;
        w = h = 0f;
        foreach (var (li, s) in Candidates(at, p))
        {
            var l = lines[li];
            var a = l.P[s];
            var ab = l.P[s + 1] - a;
            float len2 = ab.LengthSquared();
            float t = len2 > 0f ? Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f) : 0f;
            float d = p.DistanceTo(a + ab * t);
            if (d >= dist) continue;
            dist = d;
            w = Mathf.Lerp(l.W[s], l.W[s + 1], t);
            h = Mathf.Lerp(l.H[s], l.H[s + 1], t);
        }
        return dist < float.MaxValue;
    }

    /// <summary>The road nearest a point, if it's within its width and shoulder: distance, width, road height, class.</summary>
    public bool RoadNear(float x, float z, out float dist, out float w, out float h) => Nearest(Roads, _roadAt, new Vector2(x, z), out dist, out w, out h);

    /// <summary>The river nearest a point: distance from its middle, width, and water surface height.</summary>
    public bool RiverNear(float x, float z, out float dist, out float w, out float h) => Nearest(Rivers, _riverAt, new Vector2(x, z), out dist, out w, out h);

    // ---------------------------------------------------------------- heights

    /// <summary>The ground before settlements are levelled into it: the island's relief and the roads across it.</summary>
    public float Height(float x, float z)
    {
        float h = Base(x, z);
        // Finer relief on land: a few metres of outcrop and gully where it's steep, half a metre where it's flat.
        int c = CellAt(x, z);
        if (c >= 0 && h > 0.5f)
        {
            float slope = Isl.Slope[c];
            float amp = 0.5f + 5f * Mathf.SmoothStep(4f, 30f, slope) * Isl.Rock[c] / (Isl.Rock[c] + 1f);
            float shore = Mathf.SmoothStep(0.5f, 6f, h);
            h += (_relief.GetNoise2D(x, z) * amp + _fine.GetNoise2D(x, z) * 0.25f) * shore;
        }
        if (RoadNear(x, z, out float d, out float w, out float rh))
        {
            float flat = w / 2f + 1f;
            if (d < flat + Shoulder)
                h = Mathf.Lerp(rh, h, Mathf.SmoothStep(flat, flat + Shoulder, d));
        }
        return h;
    }

    /// <summary>After settlements are levelled: the river channels cut in (they run through towns too), except under bridges.</summary>
    public float Finish(float x, float z, float h)
    {
        if (!RiverNear(x, z, out float d, out float w, out float s)) return h;
        float half = w / 2f, bank = Bank(w);
        if (d >= half + bank) return h;
        // A brook under a road goes through a culvert: the road isn't cut.
        if (w < 3f && RoadNear(x, z, out float rd, out float rw, out _) && rd < rw / 2f + 2f) return h;
        float bed = s - Depth(w) * (1f - (d / half) * (d / half));
        float cut = d < half ? bed : Mathf.Lerp(s, h, (d - half) / bank);
        return MathF.Min(h, cut);
    }

    /// <summary>The water's surface at a point: the sea, a lake or a river; NaN on dry ground.</summary>
    public float WaterAt(float x, float z)
    {
        if (RiverNear(x, z, out float d, out float w, out float s) && d < w / 2f) return s;
        int c = CellAt(x, z);
        if (c < 0 || Isl.Sea(c)) return 0f;
        if (Isl.Land[c] == Cover.Lake && !float.IsNaN(Isl.LakeLevel[c])) return Isl.LakeLevel[c];
        return 0f; // the sea, wherever the ground dips below it
    }

    // ---------------------------------------------------------------- cover

    /// <summary>The land cover at a point, its cell's edges wandered by up to about 60 m.</summary>
    public Cover CoverAt(float x, float z)
    {
        float wx = _warp.GetNoise2D(x, z) * 60f, wz = _warp.GetNoise2D(x + 917f, z - 413f) * 60f;
        int c = CellAt(x + wx, z + wz);
        return c < 0 ? Cover.Sea : Isl.Land[c];
    }

    static readonly Color Grass = new(0.33f, 0.38f, 0.2f), Garrigue = new(0.47f, 0.44f, 0.31f), Maquis = new(0.27f, 0.31f, 0.18f),
                          PineFloor = new(0.27f, 0.28f, 0.17f), OakFloor = new(0.24f, 0.29f, 0.15f), Montane = new(0.37f, 0.39f, 0.27f),
                          Orchard = new(0.36f, 0.38f, 0.22f), RockC = new(0.5f, 0.48f, 0.45f), Beach = new(0.76f, 0.68f, 0.5f),
                          Marsh = new(0.28f, 0.33f, 0.21f), Bed = new(0.42f, 0.4f, 0.33f),
                          Asphalt = new(0.21f, 0.21f, 0.21f), Gravel = new(0.33f, 0.31f, 0.28f), Dirt = new(0.45f, 0.38f, 0.28f);
    static readonly Color[] Crops = { new(0.62f, 0.55f, 0.32f), new(0.38f, 0.44f, 0.21f), new(0.43f, 0.34f, 0.25f), new(0.55f, 0.5f, 0.35f), new(0.47f, 0.48f, 0.26f) };

    /// <summary>The ground's colour (sRGB) by its cover, rock on steep ground, and the bed of rivers.</summary>
    public Color ColorAt(float x, float z, float h, Vector3 n, float noise)
    {
        var cover = CoverAt(x, z);
        Color g = cover switch
        {
            Cover.Grass => Grass,
            Cover.Garrigue => Garrigue,
            Cover.Maquis => Maquis,
            Cover.Pine => PineFloor,
            Cover.Oak => OakFloor,
            Cover.Montane => Montane,
            Cover.Fields => Crops[(int)((_fields.GetNoise2D(x, z) * 0.5f + 0.5f) * Crops.Length * 0.999f)],
            Cover.Orchards => Orchard,
            Cover.Rock => RockC,
            Cover.Beach => Beach,
            Cover.Marsh => Marsh,
            // The island's town cover reaches further than the streets built so far (a town's radius is capped for
            // now): outside them it's the outskirts, gardens and rough grass, not bare paving.
            Cover.Urban => Grass.Lerp(Garrigue, 0.4f),
            _ => Bed,
        };
        g = g.Lerp(g.Darkened(0.12f), noise);
        if (h < 1.2f && cover is not (Cover.Urban or Cover.Fields)) g = g.Lerp(Beach, Mathf.SmoothStep(1.2f, 0.2f, h)); // the shore
        float steep = Mathf.SmoothStep(0.25f, 0.5f, 1f - n.Y);
        g = g.Lerp(RockC, steep);
        if (!float.IsNaN(WaterAt(x, z)) && h < WaterAt(x, z) - 0.1f) g = g.Lerp(Bed, 0.7f);
        return g;
    }

    /// <summary>A road's surface colour at a point, or null off the road.</summary>
    public Color? RoadPaint(float x, float z)
    {
        var p = new Vector2(x, z);
        foreach (var (li, s) in Candidates(_roadAt, p))
        {
            var l = Roads[li];
            var a = l.P[s];
            var ab = l.P[s + 1] - a;
            float len2 = ab.LengthSquared();
            float t = len2 > 0f ? Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f) : 0f;
            if (p.DistanceTo(a + ab * t) < l.W[s] / 2f)
                return l.Class switch { RoadClass.Main => Asphalt, RoadClass.Secondary => Gravel, _ => Dirt };
        }
        return null;
    }

    /// <summary>
    /// Trees per hectare by species (pine, broadleaf, birch, bush, palm, scrub) at a point, by its cover. Fewer than in
    /// real woods (pinewoods stand 300-1,000 stems a hectare), as on the battle maps, to keep the count playable; the
    /// proportions between covers hold. None on roads, in water, or on bare rock.
    /// </summary>
    public float TreesPerHa(float x, float z, int species)
    {
        if (RoadNear(x, z, out float d, out float w, out _) && d < w / 2f + 3f) return 0f;
        if (RiverNear(x, z, out float rd, out float rw, out _) && rd < rw / 2f + 2f) return 0f;
        var cover = CoverAt(x, z);
        return (cover, species) switch
        {
            (Cover.Pine, 0) => 45f,
            (Cover.Pine, 3) => 8f,
            (Cover.Oak, 1) => 38f,
            (Cover.Oak, 3) => 14f,
            (Cover.Montane, 0) => 30f,
            (Cover.Montane, 2) => 10f,
            (Cover.Maquis, 3) => 55f,
            (Cover.Maquis, 1) => 4f,
            (Cover.Garrigue, 5) => 25f,
            (Cover.Garrigue, 3) => 4f,
            (Cover.Orchards, 1) => 20f,
            (Cover.Grass, 1) => 1.5f,
            (Cover.Grass, 3) => 3f,
            (Cover.Fields, 1) => 0.6f,
            (Cover.Marsh, 3) => 6f,
            _ => 0f,
        };
    }

    /// <summary>The most trees per hectare of a species anywhere (for sampling).</summary>
    public static readonly float[] MostPerHa = { 45f, 38f, 10f, 55f, 0f, 25f };
}
