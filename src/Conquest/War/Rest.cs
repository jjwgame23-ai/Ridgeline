namespace Ridgeline;

/// <summary>
/// Tiredness and sleep, company by company. The soldiers live to fight and would fight every day; what stops them is the
/// body. Tiredness here is physical only: there's no weight of death on their minds, so no combat stress.
/// - Sleep debt. Awake, a company runs up half an hour of sleep owed for every hour (a normal day's 16 hours awake are
///   paid off by 8 asleep). Marching on foot runs it up at three quarters, riding at a half, fighting at one and a half
///   (hard physical work), and hunger adds a quarter.
/// - Sleeping. A company halted and out of a fight sleeps by night, and by day too once it owes 8 hours. In the rear
///   everyone sleeps and pays off an hour an hour; in the line (enemy ground within 2 km) they sleep in shifts, half at
///   a time, and pay off half.
/// - What it costs. Lost sleep costs judgement and shooting, about 25% per day awake (Belenky et al. 1994): aim falls by
///   a quarter for every 16 hours owed, to no worse than 30%.
/// - Readiness. A battalion owing 8 hours or more isn't sent on an operation, and an offensive whose companies owe 16 on
///   average is called off: exhausted.
/// </summary>
public static class Rest
{
    public const float ReadyDebt = 8f, Exhausted = 16f;

    public static void Step(War war, double dt)
    {
        float hours = (float)(dt / 3600.0);
        bool night = !war.MarchingHours;
        foreach (int id in war.MoverIds)
        {
            var u = war.Units[id];
            if (u.People <= 0) continue;
            // Marching: it moved this minute (War.Step runs first). A column halted for the night or at the end of its
            // day's march, its route not finished, beds down like any other. (Those still had a route and stayed awake
            // all night, so offensives that had marched to their assembly areas went in owing 10 hours and were called
            // off exhausted 8 hours in.)
            bool marching = u.X != u.WasX || u.Z != u.WasZ;
            float rate;
            if (u.InFight >= 0) rate = 1.5f;
            else if (marching) rate = u.Mob == Mobility.Foot ? 0.75f : 0.5f;
            else if (night || u.SleepDebt >= ReadyDebt) rate = InLine(war, u) ? -0.5f : -1f;
            else rate = 0.5f;
            if (u.HungrySince >= 0) rate += 0.25f;
            u.SleepDebt = Math.Clamp(u.SleepDebt + rate * hours, 0f, 48f);
        }
    }

    /// <summary>In the line: ground the enemy holds or is in within 2 km.</summary>
    static bool InLine(War war, Unit u)
    {
        var ctl = war.Ctl;
        int c = ctl.CellOf(u.X, u.Z), cx = c % ctl.N, cz = c / ctl.N;
        for (int y = cz - 2; y <= cz + 2; y++)
        for (int x = cx - 2; x <= cx + 2; x++)
            if ((uint)x < (uint)ctl.N && (uint)y < (uint)ctl.N && ctl.Hostile(y * ctl.N + x, u.Side)) return true;
        return false;
    }

    /// <summary>How well a tired company shoots: a quarter worse for every 16 hours of sleep owed, to no worse than 30%.</summary>
    public static float Fresh(Unit m) => MathF.Max(0.3f, 1f - 0.25f * m.SleepDebt / 16f);

    public static bool Ready(Unit m) => m.SleepDebt < ReadyDebt;
}
