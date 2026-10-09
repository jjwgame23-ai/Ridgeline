namespace Ridgeline;

/// <summary>
/// The medical chain and replacements: the wounded going back and coming back, and new soldiers landed by ship.
/// - Light wounds. A soldier lightly wounded fights on, and is fit again a week later.
/// - Serious wounds. A soldier down and carried out goes back through the battalion's aid post to hospital. One in
///   twenty dies of his wounds on the way, as in recent wars with quick evacuation; one in four if his unit is cut off
///   from its depot and the wounded wait. Of the rest, two in five return to duty after one to four weeks; the others
///   are sent home and are lost to the war (in the Second World War about half of those hospitalised came back).
/// - Replacements. Each port a side holds lands 500 soldiers a day, a troopship every two days. They go only to
///   fill vacancies, the units shortest of men first, each in the job of the soldier he replaces, so an army is made up
///   to strength but never grows past it. Each is a new soldier with his own name and skill, and no experience.
/// - Joining. Soldiers back from hospital and replacements reach their unit with its nightly resupply, if it gets
///   through to them.
/// - Tickets. A side's headcount: 50,000 at the start, less the dead, plus the replacements landed.
/// </summary>
public static class Medical
{
    const int PerPortPerDay = 500;
    public const int Tickets = 50_000;

    /// <summary>A soldier down, carried out of the fight or the shelling: where he ends up.</summary>
    public static void Admit(War war, int s, bool cutOff)
    {
        ref var so = ref war.Soldiers[s];
        if (so.State is SoldierState.Dead or SoldierState.Recovering or SoldierState.Invalided) return;
        war.Evacuated[so.Side]++;
        double r = war.Rng.NextDouble(), dow = cutOff ? 0.25 : 0.05;
        if (r < dow)
        {
            so.State = SoldierState.Dead;
            so.Since = war.Time;
            war.Dead[so.Side]++;
            war.DiedOfWounds[so.Side]++;
        }
        else if (r < dow + 0.4 * (1 - dow))
        {
            // Back in one to four weeks: the day he's due is kept in Since.
            so.State = SoldierState.Recovering;
            so.Since = war.Time + (7 + (uint)s * 2654435761u % 22) * 86400.0;
        }
        else
        {
            so.State = SoldierState.Invalided;
            so.Since = war.Time;
            war.Invalided[so.Side]++;
        }
    }

    /// <summary>Each morning at 06:00: light wounds healed, the recovered sent back, replacements landed.</summary>
    public static void Step(War war)
    {
        if (Math.Abs(war.Hour - 6.0) > 1e-6 || (int)war.Time % 600 != 0) return;
        double now = war.Time;
        for (int s = 0; s < war.SoldierCount; s++)
        {
            ref var so = ref war.Soldiers[s];
            if (so.State == SoldierState.Wounded && now - so.Since >= 7 * 86400)
            {
                so.State = SoldierState.Fit;
                war.Healed[so.Side]++;
            }
            else if (so.State == SoldierState.Recovering && now >= so.Since)
            {
                so.State = SoldierState.Joining;
                so.Since = now;
                war.Recovered[so.Side]++;
            }
        }
        foreach (var side in war.Sides) Land(war, side.Index);
    }

    /// <summary>Replacements landed at the side's ports, into the units shortest of men.</summary>
    static void Land(War war, int side)
    {
        int ports = war.Objectives.Count(o => o.Kind == ObjKind.Port && o.Owner == side);
        int room = ports * PerPortPerDay;
        if (room == 0) return;
        // Vacancies: places in a unit no longer filled by a soldier who's with it, on his way back to it or in
        // hospital due back.
        var short_ = new List<(Unit U, int N)>();
        foreach (var u in war.Units)
        {
            if (u.Side != side || u.Establishment == 0) continue;
            int with = 0;
            foreach (int m in u.Members)
                if (war.Soldiers[m].State is not (SoldierState.Dead or SoldierState.Invalided)) with++;
            if (with < u.Establishment) short_.Add((u, u.Establishment - with));
        }
        foreach (var (u, n) in short_.OrderByDescending(v => v.N))
        {
            for (int k = 0; k < n && room > 0; k++)
            {
                // The place of a soldier lost and not yet replaced: his job, at his rank up to a specialist's (NCO
                // places are filled by promotion within the unit, which isn't modelled).
                int gone = -1;
                foreach (int m in u.Members)
                {
                    var so = war.Soldiers[m];
                    if (so.State is SoldierState.Dead or SoldierState.Invalided && !so.Replaced)
                    {
                        gone = m;
                        break;
                    }
                }
                if (gone < 0) break;
                war.Soldiers[gone].Replaced = true;
                var g = war.Soldiers[gone];
                var load = Orbat.Load(g.Job);
                int id = war.AddSoldier(new Soldier
                {
                    Unit = u.Id, Side = (byte)side, Rank = Math.Min(g.Rank, (byte)4), Job = g.Job, State = SoldierState.Joining, Since = war.Time, Blood = 1f,
                    Ammo = load.Ammo, Grenades = load.Grenades, Rockets = load.Rockets,
                });
                u.Members.Add(id);
                war.Replacements[side]++;
                room--;
            }
            if (room == 0) break;
        }
    }

    /// <summary>The soldiers on their way to a mover's units join it: back from hospital, or replacements.</summary>
    public static void Rejoin(War war, Unit m)
    {
        bool any = false;
        foreach (int ci in m.Carries)
            foreach (int s in war.Units[ci].Members)
            {
                ref var so = ref war.Soldiers[s];
                if (so.State != SoldierState.Joining) continue;
                so.State = SoldierState.Fit;
                so.Since = war.Time;
                var load = Orbat.Load(so.Job);
                so.Ammo = load.Ammo;
                so.Grenades = load.Grenades;
                so.Rockets = load.Rockets;
                war.Joined[so.Side]++;
                any = true;
            }
        if (any) war.Recount(m);
    }
}
