using Godot;

namespace Ridgeline;

/// <summary>
/// One climate for a whole island, picked when it's made. Mediterranean first; desert, arctic and others later, each a
/// preset like this one. The latitude comes with it (the battle maps' sun is already placed by latitude).
/// </summary>
public sealed class IslandClimate
{
    public string Id = "", Name = "";
    /// <summary>Degrees north.</summary>
    public float Latitude;
    /// <summary>Annual mean temperature at sea level, °C.</summary>
    public float SeaLevelTempC;
    /// <summary>Annual rain on low ground facing the wet wind, mm.</summary>
    public float RainMm;
    /// <summary>Where the rain-bearing winds come from (degrees from north).</summary>
    public float WetWindFromDeg;
    /// <summary>The share of rain that runs off into the rivers.</summary>
    public float Runoff;
    public float TreelineM;
    /// <summary>The range the highest summit is drawn from.</summary>
    public float PeakMinM, PeakMaxM;
    /// <summary>
    /// How densely an island like this is settled. There are no civilians: it sizes how much was built (towns,
    /// farms, roads), the way a real island of the climate is built up.
    /// </summary>
    public float PeoplePerKm2;
    /// <summary>Farmland per inhabitant, hectares: how much of the land around the towns is fields and orchards.</summary>
    public float FarmHaPerPerson;

    public static readonly IslandClimate Mediterranean = new()
    {
        Id = "mediterranean",
        Name = "Mediterranean",
        // 38° N, like the battle maps' sun. Palermo averages about 18.5 °C and 600 mm a year. Palma, in the lee of
        // Mallorca's mountains, gets about 430 mm, while the Tramuntana facing the north-westerlies gets about 1,400.
        Latitude = 38f,
        SeaLevelTempC = 18f,
        RainMm = 560f,
        WetWindFromDeg = 300f,
        Runoff = 0.3f,
        TreelineM = 1900f,
        // Mallorca tops out at 1,445 m, Cyprus 1,952, Crete 2,456, Corsica 2,706.
        PeakMinM = 1300f,
        PeakMaxM = 2400f,
        // Sicily has about 190 people per km², Sardinia 68, Crete 75: a middling 110.
        PeoplePerKm2 = 110f,
        FarmHaPerPerson = 0.4f,
    };

    public static IslandClimate Get(string id) => id switch
    {
        _ => Mediterranean,
    };

    /// <summary>
    /// Annual mean temperature and rain for every cell.
    /// - Temperature falls 6.5 °C per km of height (the standard atmosphere's lapse rate).
    /// - Rain: moist air comes in on the wet wind and is followed across the island, upwind cells first. Air forced up a
    ///   slope rains more, and higher ground is wetter. What it rains out leaves it drier, so the lee of a range is
    ///   in its rain shadow; over the sea it takes moisture back up. The air rises over the land's large shape, not
    ///   every gully, so the ground is smoothed over a few km first.
    /// </summary>
    public static void Apply(Island isl)
    {
        var c = isl.Climate;
        int n = isl.N, count = n * n;
        float cell = isl.Cell;
        var ground = new float[count];
        for (int i = 0; i < count; i++) ground[i] = isl.Sea(i) ? 0f : MathF.Max(0f, isl.Height[i]);
        int r = Math.Max(1, (int)(1500f / cell));
        var hs = Grid.BoxMean(Grid.BoxMean(ground, n, r), n, r);

        // The way the wet wind blows (to), in cells: x east, y south.
        float to = Mathf.DegToRad(c.WetWindFromDeg + 180f);
        float wx = MathF.Sin(to), wy = -MathF.Cos(to);
        var key = new float[count];
        var order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
            key[i] = (i % n) * wx + (i / n) * wy;
        }
        Array.Sort(key, order);

        var q = new float[count];
        Array.Fill(q, 1f);
        float per100 = cell / 100f;
        float recharge = 1f - MathF.Exp(-cell / 25_000f); // air over the sea is moist again within a few tens of km
        foreach (int i in order)
        {
            float ux = i % n - wx, uy = i / n - wy;
            float qu = Grid.Sample(q, n, ux, uy, 1f), hu = Grid.Sample(hs, n, ux, uy, 0f);
            if (isl.Sea(i))
            {
                q[i] = qu + (1f - qu) * recharge;
                isl.Rain[i] = c.RainMm * qu;
                continue;
            }
            float lift = MathF.Max(0f, hs[i] - hu) / per100; // metres of rise per 100 m travelled
            float p = c.RainMm * qu * (1f + 0.12f * lift) * (1f + 0.35f * hs[i] / 1000f);
            isl.Rain[i] = p;
            // Only rain beyond what flat ground gets dries the air out.
            q[i] = MathF.Max(0.15f, qu * (1f - 0.0035f * per100 * MathF.Max(0f, p / c.RainMm - 1f)));
        }
        var smooth = Grid.BoxMean(isl.Rain, n, Math.Max(1, (int)(500f / cell)));
        for (int i = 0; i < count; i++)
        {
            if (!isl.Sea(i)) isl.Rain[i] = smooth[i];
            isl.Temp[i] = c.SeaLevelTempC - 6.5f * MathF.Max(0f, isl.Height[i]) / 1000f;
        }
    }
}
