namespace Ridgeline;

/// <summary>
/// What grows where, and what's farmed and built on.
/// - Natural cover follows Mediterranean ecology. Garrigue grows where it's driest, maquis and Aleppo pine a little
///   wetter, oak wetter still and on north-facing slopes (they keep their moisture), black pine and fir higher up,
///   and open grass above the tree line. Cliffs and steep ground are bare rock, with beaches and marsh on low coasts.
/// - Farmland is as much as the towns' people would work (the climate's hectares per head), taken from the best land
///   nearest them. Fields lie on the flat, olives and vines on terraced slopes.
/// </summary>
public static class LandCover
{
    public static void Slopes(Island isl)
    {
        int n = isl.N;
        float cell = isl.Cell;
        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                float gx = (H(isl, x + 1, y) - H(isl, x - 1, y)) / (2f * cell);
                float gy = (H(isl, x, y + 1) - H(isl, x, y - 1)) / (2f * cell);
                isl.Slope[i] = MathF.Atan(MathF.Sqrt(gx * gx + gy * gy)) * 180f / MathF.PI;
            }
        });
    }

    /// <summary>Height for slopes: the sea counts at its surface, so a beach isn't a cliff above the sea floor.</summary>
    static float H(Island isl, int x, int y)
    {
        int n = isl.N;
        int i = Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1);
        return isl.Sea(i) ? 0f : isl.Height[i];
    }

    public static void Natural(Island isl)
    {
        var clim = isl.Climate;
        int n = isl.N;
        float cell = isl.Cell;
        var patch = new Noise2(isl.Seed + 51);
        var fine = new Noise2(isl.Seed + 52);
        var seaSeed = new bool[isl.Count];
        for (int i = 0; i < seaSeed.Length; i++) seaSeed[i] = isl.Sea(i);
        var dCoast = Grid.Distance(seaSeed, n, cell);
        Parallel.For(0, n, y =>
        {
            for (int x = 0; x < n; x++)
            {
                int i = y * n + x;
                if (isl.Water(i)) continue;
                float s = isl.Slope[i], h = isl.Height[i], rain = isl.Rain[i];
                float p = patch.Fbm(x * cell / 2000f, y * cell / 2000f, 3);
                float f = fine.Fbm(x * cell / 600f, y * cell / 600f, 2);
                // How far the slope faces north (+1) or south (-1): ground rising to the south faces north.
                float gy = H(isl, x, y + 1) - H(isl, x, y - 1), gx = H(isl, x + 1, y) - H(isl, x - 1, y);
                float gl = MathF.Sqrt(gx * gx + gy * gy);
                float north = gl > 1e-3f ? gy / gl : 0f;
                bool coast = dCoast[i] <= cell * 1.5f;
                Cover cv;
                if (coast && h < 8f && s < 5f) cv = Cover.Beach;
                else if (h < 3f && s < 1.2f && dCoast[i] < 1500f && isl.Area[i] > 2e6f) cv = Cover.Marsh;
                else if (s > 38f || (coast && s > 25f)) cv = Cover.Rock;
                else if (h > clim.TreelineM + 150f * p) cv = s > 28f ? Cover.Rock : Cover.Grass;
                else
                {
                    float wet = rain * (1f + 0.35f * north * MathF.Sin(s * MathF.PI / 180f)) + 120f * p;
                    if (h > 1250f + 120f * p) cv = wet > 700f ? Cover.Montane : Cover.Grass;
                    else if (wet < 420f) cv = Cover.Garrigue;
                    else if (wet < 650f) cv = f > 0.25f ? Cover.Pine : Cover.Maquis;
                    else if (wet < 950f) cv = north > 0.2f || h > 700f ? Cover.Oak : f > 0f ? Cover.Pine : Cover.Maquis;
                    else cv = f > -0.2f ? Cover.Oak : Cover.Pine;
                    if (s > 32f) cv = f > 0f ? Cover.Rock : Cover.Garrigue;
                }
                isl.Land[i] = cv;
                isl.Farmable[i] = Potential(isl, i, cv, s, h, rain);
            }
        });
    }

    /// <summary>How good a cell is for farming, 0..1: gentle (terraces up to about 20°), not too high, wet enough, and better on valley floors and soft rock, where soil is deep.</summary>
    static float Potential(Island isl, int i, Cover cv, float s, float h, float rain)
    {
        if (cv is Cover.Beach or Cover.Marsh or Cover.Rock) return 0f;
        float slope = s < 3f ? 1f : s < 8f ? 0.85f : s < 15f ? 0.5f : s < 22f ? 0.2f : 0f;
        float height = h < 400f ? 1f : h < 900f ? 1f - 0.7f * (h - 400f) / 500f : MathF.Max(0f, 0.3f - (h - 900f) / 1700f);
        float water = MathF.Min(1f, rain / 450f);
        float soil = isl.Area[i] > 2e6f || isl.Rock[i] > 1.2f ? 1.15f : 1f;
        return Math.Clamp(slope * height * water * soil, 0f, 1f);
    }

    /// <summary>Built-up ground round each town: a ragged disc of its built radius.</summary>
    public static void Urban(Island isl)
    {
        var shape = new Noise2(isl.Seed + 53);
        int n = isl.N;
        float cell = isl.Cell;
        foreach (var t in isl.Towns)
        {
            int cx = t.Cell % n, cy = t.Cell / n;
            int r = (int)MathF.Ceiling(t.RadiusM * 1.3f / cell) + 1;
            for (int y = Math.Max(0, cy - r); y <= Math.Min(n - 1, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x <= Math.Min(n - 1, cx + r); x++)
            {
                int i = y * n + x;
                if (isl.Water(i)) continue;
                float dx = (x - cx) * cell, dy = (y - cy) * cell;
                float a = MathF.Atan2(dy, dx);
                float rr = t.RadiusM * (0.8f + 0.4f * (0.5f + 0.5f * shape.Fbm(MathF.Cos(a) * 1.3f + t.Id * 7.1f, MathF.Sin(a) * 1.3f, 2)));
                if (MathF.Sqrt(dx * dx + dy * dy) <= MathF.Max(rr, cell * 0.5f)) isl.Land[i] = Cover.Urban;
            }
        }
    }

    /// <summary>
    /// Fields and orchards. Each cell's pull is the people within reach, falling off over 2.5 km (about how far out
    /// fields were worked from a village). The best land with the most pull is farmed until the towns have their
    /// hectares.
    /// </summary>
    public static void Farmland(Island isl)
    {
        int n = isl.N, c = n * n;
        float cell = isl.Cell;
        const float Reach = 2500f;
        int R = (int)(4f * Reach / cell);
        var pull = new float[c];
        double needed = 0;
        foreach (var t in isl.Towns)
        {
            needed += t.Population * isl.Climate.FarmHaPerPerson * 1e4 / isl.CellArea;
            int cx = t.Cell % n, cy = t.Cell / n;
            for (int y = Math.Max(0, cy - R); y <= Math.Min(n - 1, cy + R); y++)
            for (int x = Math.Max(0, cx - R); x <= Math.Min(n - 1, cx + R); x++)
            {
                float dx = (x - cx) * cell, dy = (y - cy) * cell;
                pull[y * n + x] += t.Population * MathF.Exp(-MathF.Sqrt(dx * dx + dy * dy) / Reach);
            }
        }
        var cells = new List<int>();
        var keys = new List<float>();
        for (int i = 0; i < c; i++)
        {
            if (isl.Land[i] is Cover.Sea or Cover.Lake or Cover.Urban or Cover.Beach or Cover.Marsh or Cover.Rock) continue;
            if (isl.Farmable[i] <= 0.05f || pull[i] <= 0f) continue;
            cells.Add(i);
            keys.Add(-isl.Farmable[i] * pull[i]);
        }
        var order = cells.ToArray();
        Array.Sort(keys.ToArray(), order);
        var patchwork = new Noise2(isl.Seed + 54);
        int take = Math.Min(order.Length, (int)needed);
        for (int k = 0; k < take; k++)
        {
            int i = order[k];
            float v = patchwork.Get(i % n * cell / 700f, i / n * cell / 700f);
            isl.Land[i] = isl.Slope[i] < 4f && v < 0.3f ? Cover.Fields : Cover.Orchards;
        }
    }
}
