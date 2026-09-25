namespace Ridgeline;

/// <summary>What one soldier does in their squad.</summary>
public enum Role { Leader, Rifleman, AutoRifleman, Grenadier, Medic, Marksman, Engineer, Ammo, AntiTank, HeavyAT, Crewman, AntiAir }

/// <summary>What a squad is for.</summary>
public enum SquadKind { Rifle, Weapons, Recon, Engineer, Logistics, AntiTank, Armor, Transport, Air, Mortar }

/// <summary>Loadouts, names and how a faction is split into squads.</summary>
public static class Roles
{
    public static string Name(Role r) => r switch
    {
        Role.Leader => "Squad leader",
        Role.Rifleman => "Rifleman",
        Role.AutoRifleman => "Automatic rifleman",
        Role.Grenadier => "Grenadier",
        Role.Medic => "Medic",
        Role.Marksman => "Marksman",
        Role.Engineer => "Combat engineer",
        Role.Ammo => "Ammo bearer",
        Role.AntiTank => "Light anti-tank",
        Role.HeavyAT => "Heavy anti-tank",
        Role.Crewman => "Vehicle crewman",
        Role.AntiAir => "Anti-air (MANPADS)",
        _ => r.ToString(),
    };

    public static string Short(Role r) => r switch
    {
        Role.Leader => "SL", Role.Rifleman => "RIF", Role.AutoRifleman => "AR", Role.Grenadier => "GL",
        Role.Medic => "MED", Role.Marksman => "MM", Role.Engineer => "ENG", Role.Ammo => "AMMO", Role.AntiTank => "LAT", Role.HeavyAT => "HAT", Role.Crewman => "CRW", Role.AntiAir => "AA", _ => "?",
    };

    public static string Name(SquadKind k) => k switch
    {
        SquadKind.Rifle => "Rifle squad",
        SquadKind.Weapons => "Weapons team",
        SquadKind.Recon => "Recon team",
        SquadKind.Engineer => "Engineer team",
        SquadKind.Logistics => "Logistics team",
        SquadKind.AntiTank => "Anti-tank team",
        SquadKind.Armor => "Armour crew",
        SquadKind.Transport => "Transport crew",
        SquadKind.Air => "Aircrew",
        SquadKind.Mortar => "Mortar team",
        _ => k.ToString(),
    };

    public static WeaponDef Primary(Role r) => r switch
    {
        Role.AutoRifleman => WeaponDef.Lmg,
        Role.Marksman => WeaponDef.Marksman,
        _ => WeaponDef.Carbine,
    };

    /// <summary>The second thing in your hands, if any: the grenadier's launcher.</summary>
    public static WeaponDef? Secondary(Role r) => r switch
    {
        Role.Grenadier => WeaponDef.Launcher,
        Role.AntiTank => WeaponDef.Lat,
        Role.HeavyAT => WeaponDef.Hat,
        Role.AntiAir => WeaponDef.Manpad,
        _ => null,
    };

    public static int Frags(Role r) => r switch
    {
        Role.Rifleman => 3,
        Role.Leader or Role.Grenadier or Role.Engineer => 2,
        _ => 1,
    };

    /// <summary>What the role key (H) does for the player.</summary>
    public static string Gadget(Role r) => r switch
    {
        Role.Medic => "H: patch up the nearest wounded friendly (or yourself)",
        Role.Engineer => "H: build a sandbag wall in front of you",
        Role.Ammo => "you resupply friendlies near you automatically",
        Role.Grenadier => "2: grenade launcher",
        Role.AntiTank or Role.HeavyAT => "2: anti-tank launcher — go for the sides and rear",
        Role.AntiAir => "2: guided anti-air missile — aim at a helicopter until the tone locks, then fire",
        Role.Leader => "M map: click a point to order your squad · B: squad on you / work the objective",
        _ => "",
    };

    static readonly (SquadKind Kind, Role[] Roles, int Min)[] Templates =
    {
        (SquadKind.Rifle, new[] { Role.Leader, Role.Rifleman, Role.AutoRifleman, Role.Grenadier, Role.Medic, Role.AntiTank }, 3),
        (SquadKind.Recon, new[] { Role.Marksman, Role.Marksman }, 2),
        (SquadKind.Rifle, new[] { Role.Leader, Role.Rifleman, Role.AutoRifleman, Role.Grenadier, Role.Medic, Role.AntiTank }, 3),
        (SquadKind.AntiTank, new[] { Role.HeavyAT, Role.HeavyAT, Role.AntiAir }, 2),
        (SquadKind.Weapons, new[] { Role.Leader, Role.AutoRifleman, Role.AutoRifleman, Role.Ammo }, 3),
        (SquadKind.Engineer, new[] { Role.Engineer, Role.Engineer, Role.Engineer }, 2),
        (SquadKind.Rifle, new[] { Role.Leader, Role.Rifleman, Role.AutoRifleman, Role.Grenadier, Role.Medic, Role.AntiTank }, 3),
        (SquadKind.Logistics, new[] { Role.Ammo, Role.Medic }, 2),
        (SquadKind.Weapons, new[] { Role.Leader, Role.AutoRifleman, Role.AutoRifleman, Role.Ammo }, 3),
        (SquadKind.Rifle, new[] { Role.Leader, Role.Rifleman, Role.AutoRifleman, Role.Grenadier, Role.Medic, Role.AntiTank }, 3),
        (SquadKind.AntiTank, new[] { Role.HeavyAT, Role.HeavyAT, Role.Rifleman }, 2),
        (SquadKind.Recon, new[] { Role.Marksman, Role.Marksman }, 2),
    };

    /// <summary>
    /// Split a faction of n into squads: rifle squads first (they take ground),
    /// then recon, weapons, engineers and logistics as numbers allow. Leftovers join
    /// rifle squads as riflemen.
    /// </summary>
    public static List<(SquadKind Kind, List<Role> Roles)> Compose(int n)
    {
        var list = new List<(SquadKind, List<Role>)>();
        int left = n;
        for (int i = 0; left > 0; i++)
        {
            var (kind, roles, min) = Templates[i % Templates.Length];
            if (i >= Templates.Length && kind != SquadKind.Rifle) continue; // past the table: only more rifle squads
            if (left < min) break;
            int take = Math.Min(left, roles.Length);
            list.Add((kind, roles.Take(take).ToList()));
            left -= take;
        }
        for (int k = 0; left > 0; k++, left--)
        {
            var rifle = list.Where(s => s.Item1 == SquadKind.Rifle).ToList();
            if (rifle.Count == 0) { list.Add((SquadKind.Rifle, new List<Role> { Role.Leader })); continue; }
            rifle[k % rifle.Count].Item2.Add(Role.Rifleman);
        }
        return list;
    }
}
