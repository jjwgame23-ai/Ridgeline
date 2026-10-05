namespace Ridgeline;

/// <summary>
/// Summits worth a name on the map: the highest point within 1.2 km, standing 100 m or more over the lowest ground
/// within 3 km, at least 2.5 km from a higher one already taken (up to 80). The highest four are mountains with names.
/// The rest are hills known by their height, as armies name them ("Hill 302").
/// </summary>
public static class Peaks
{
    public static void Find(Island isl, PlaceNames names)
    {
        int n = isl.N, c = n * n;
        float cell = isl.Cell;
        var h = new float[c];
        for (int i = 0; i < c; i++) h[i] = isl.Water(i) ? -1000f : isl.Height[i];
        var top = Grid.Extreme(h, n, Math.Max(1, (int)(1200f / cell)), false);
        var low = Grid.Extreme(h, n, Math.Max(1, (int)(3000f / cell)), true);
        var cand = new List<int>();
        for (int i = 0; i < c; i++)
            if (!isl.Water(i) && h[i] >= top[i] && h[i] - MathF.Max(low[i], 0f) >= 100f) cand.Add(i);
        cand.Sort((a, b) => h[b].CompareTo(h[a]));
        foreach (int i in cand)
        {
            if (isl.Peaks.Count >= 80) break;
            if (isl.Peaks.Any(p => isl.Dist(p.Cell, i) < 2500f)) continue;
            isl.Peaks.Add(new Peak { Cell = i, Height = h[i], Relief = h[i] - MathF.Max(low[i], 0f) });
        }
        for (int k = 0; k < isl.Peaks.Count; k++)
        {
            var p = isl.Peaks[k];
            p.Name = k < 4 ? names.Mountain() : $"Hill {(int)MathF.Round(p.Height)}";
        }
    }
}
