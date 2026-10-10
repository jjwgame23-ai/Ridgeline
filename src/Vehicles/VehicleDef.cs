using Godot;

namespace Ridgeline;

/// <summary>The ground vehicle classes, as in Squad.</summary>
public enum VKind { LTV, Transport, Logistics, APC, IFV, MBT, MGS, UH, AH, SPAA, Mortar }

public enum SeatRole { Driver, Gunner, Passenger }

/// <summary>A vehicle-mounted weapon (or one ammo type of it).</summary>
public sealed class VWeapon
{
    public string Name = "";
    public Snd Sound = Snd.Rifle762;
    public float Speed, Drag, Damage, VehDamage, Pen;   // Pen: mm of rolled-steel-equivalent armour it gets through
    public bool Explosive;
    public float Crater;       // HE: crater radius
    public float FragR;        // HE: how far out a standing man has an even chance of a fragment hit (m)
    public float Power = 1f;   // HE: charge relative to a hand grenade
    public float Rpm;
    public int Mag, Mags;
    public float Reload;
    public float SpreadDeg;
    public float Kick;         // visual recoil of the gun, metres
    public float Flash = 1f;   // muzzle flash scale
    public bool Prox;          // proximity fuse: bursts next to an aircraft it passes
    /// <summary>A guided missile: flies to the vehicle it was launched at (see Ballistics.GuideGround).</summary>
    public bool Guided;
    /// <summary>Guided by its own seeker once launched; otherwise the aircraft has to keep the target in sight until it hits.</summary>
    public bool FireAndForget;
    /// <summary>How far a guided missile reaches (m), and how close it can be used (it has to gather itself and be gathered onto the line of sight).</summary>
    public float Range, MinRange;
    /// <summary>
    /// Flies along the gunner's line of sight to the target (wire-guided SACLOS: TOW, Konkurs), not climbing to come down
    /// on the roof as a helicopter's missile does. Fired from a halt.
    /// </summary>
    public bool LineOfSight;
    /// <summary>Tracer rounds: one in every <c>TracerEvery</c> (0: none; 1: every round), burning out at <c>TraceM</c> in normal flight (see Tracers).</summary>
    public int TracerEvery;
    public float TraceM;
    /// <summary>
    /// An illumination round: a candle of <c>IllumCd</c> candela that burns <c>IllumS</c> seconds under its parachute,
    /// thrown out by a time fuze over the target (see Illumination).
    /// </summary>
    public float IllumCd, IllumS;
    public bool AntiArmor => Pen >= 40f;
}

public sealed class TurretDef
{
    public Vector3 Mount;                  // on the hull, local
    public float YawSpeed = 60f, PitchSpeed = 30f, PitchMin = -8f, PitchMax = 25f;
    public float YawLimit = 180f;          // degrees either side of the mount's forward
    public float MountYaw;                 // which way the mount faces on the hull (door guns face sideways)
    public bool Indirect;                  // a mortar: laid by range, lobs at a high angle
    public bool Fixed;                     // rocket pods: point where the airframe points
    public Vector3 Size = new(1.6f, 0.6f, 2f);
    public float BarrelLen = 3f, BarrelRadius = 0.07f;
    public bool Exposed;                   // a ring or pintle mount: the gunner's head and shoulders show
    /// <summary>Selectable ammunition for the main gun (AP, HE...) — one is loaded at a time.</summary>
    public VWeapon[] Ammo = Array.Empty<VWeapon>();
    /// <summary>A coaxial machine gun, fired separately (RMB... or by bots at infantry).</summary>
    public VWeapon? Coax;
}

public sealed class SeatDef
{
    public SeatRole Role;
    public Vector3 Pos;        // local, where the occupant's eyes are
    public int Turret = -1;
    public bool Exposed;       // can be shot while sitting here
}

/// <summary>
/// One vehicle type for one faction. All three factions field every class, but each
/// builds its own: different proportions, turret placement, running gear and paint,
/// so you can tell whose it is from its shape, not a name tag.
/// </summary>
public sealed class VehicleDef
{
    public VKind Kind;
    public int Faction;
    public string Name = "";
    public Vector3 Hull;             // width, height, length
    public float GroundClear = 0.45f;
    public float MaxSpeed, Reverse, Accel, TurnRate;
    public bool Tracked;
    public int Axles = 2;
    public float ArmorFront, ArmorSide, ArmorRear, ArmorTop;
    public float Hp;
    public int Tickets;
    public float Respawn;
    public bool Supplies;            // a logistics truck: carries the makings of a FOB
    public bool Heavy => Kind is VKind.APC or VKind.IFV or VKind.MBT or VKind.MGS or VKind.SPAA; // flattens trees
    public bool Air => Kind is VKind.UH or VKind.AH;
    public bool Static => Kind == VKind.Mortar;
    // Flight: lift available (m/s² at full collective), top speed, flares carried, rotor size.
    public float Lift, AirSpeed, RotorRadius;
    public int Flares;
    public readonly List<SeatDef> Seats = new();
    public readonly List<TurretDef> Turrets = new();
    public Color Paint;
    public int Style;                // faction design language, for the model builder

    public int Passengers => Seats.Count(s => s.Role == SeatRole.Passenger);
    public string ClassName => Kind switch
    {
        VKind.LTV => "light vehicle", VKind.Transport => "transport truck", VKind.Logistics => "logistics truck",
        VKind.APC => "APC", VKind.IFV => "IFV", VKind.MBT => "tank", VKind.MGS => "mobile gun",
        VKind.UH => "transport helicopter", VKind.AH => "attack helicopter", VKind.SPAA => "anti-air", VKind.Mortar => "mortar", _ => "vehicle",
    };

    // ---------------------------------------------------------------- weapons

    public static VWeapon Hmg() => new()
    {
        Name = "12.7mm HMG", Sound = Snd.Hmg, Speed = 890f, Drag = 0.0003f, Damage = 95f, VehDamage = 7f, Pen = 25f,
        Rpm = 550f, Mag = 100, Mags = 6, Reload = 7f, SpreadDeg = 0.22f, Kick = 0.03f, Flash = 1.4f,
        // Linked 4 ball : 1 tracer (M33 / M17); the M17's trace reaches about 1 450 m (TM 9-1005-213-10).
        TracerEvery = 5, TraceM = 1450f,
    };

    public static VWeapon Coax() => new()
    {
        Name = "7.62mm coax", Sound = Snd.Rifle762, Speed = 850f, Drag = 0.0005f, Damage = 62f, VehDamage = 1f, Pen = 8f,
        Rpm = 700f, Mag = 250, Mags = 6, Reload = 6f, SpreadDeg = 0.15f, Kick = 0f, Flash = 0.9f,
        // 4 ball : 1 tracer; 7.62 mm tracer (M62, T-46) burns out at about 900 m.
        TracerEvery = 5, TraceM = 900f,
    };

    public static VWeapon[] Autocannon() => new[]
    {
        new VWeapon { Name = "30mm AP", Sound = Snd.Autocannon, Speed = 1100f, Drag = 0.00018f, Damage = 140f, VehDamage = 32f, Pen = 60f,
                      Rpm = 200f, Mag = 70, Mags = 3, Reload = 5f, SpreadDeg = 0.12f, Kick = 0.08f, Flash = 1.8f,
                      // Every cannon round carries its own tracer (APDS-T, AP-T), traced out past the gun's battle range.
                      TracerEvery = 1, TraceM = 2500f },
        new VWeapon { Name = "30mm HE", Sound = Snd.Autocannon, Speed = 1000f, Drag = 0.00022f, Damage = 140f, VehDamage = 10f, Pen = 10f,
                      Explosive = true, Crater = 0.4f, FragR = 3.5f, Power = 0.25f, Rpm = 200f, Mag = 90, Mags = 3, Reload = 5f, SpreadDeg = 0.15f, Kick = 0.08f, Flash = 1.8f,
                      TracerEvery = 1, TraceM = 2000f }, // HEI-T: traced to about 2 km (the 25 mm M792's tracer)
    };

    public static VWeapon[] Cannon(float cal) => new[]
    {
        new VWeapon { Name = $"{cal:0}mm APFSDS", Sound = Snd.Cannon, Speed = cal > 110 ? 1650f : 1500f, Drag = 0.00006f, Damage = 400f,
                      VehDamage = cal > 110 ? 480f : 380f, Pen = cal > 110 ? 560f : 420f, Rpm = cal > 110 ? 7.5f : 9f, Mag = 1,
                      Mags = 22, Reload = 0f, SpreadDeg = 0.03f, Kick = 0.45f, Flash = 4f,
                      // A tank round's tracer is how the commander sees where it went and calls the correction.
                      TracerEvery = 1, TraceM = 3000f },
        new VWeapon { Name = $"{cal:0}mm HE", Sound = Snd.Cannon, Speed = 900f, Drag = 0.00012f, Damage = 400f, VehDamage = 120f, Pen = 60f,
                      Explosive = true, Crater = cal > 110 ? 1.6f : 1.3f, FragR = cal > 110 ? 30f : 25f, Power = cal > 110 ? 12f : 9f, Rpm = cal > 110 ? 7.5f : 9f, Mag = 1,
                      Mags = 16, Reload = 0f, SpreadDeg = 0.05f, Kick = 0.45f, Flash = 4f,
                      TracerEvery = 1, TraceM = 2500f }, // HE-T / HEAT-MP-T
    };

    public static VWeapon DoorGun() => new()
    {
        Name = "7.62mm door gun", Sound = Snd.Rifle762, Speed = 850f, Drag = 0.0005f, Damage = 62f, VehDamage = 2f, Pen = 9f,
        Rpm = 750f, Mag = 200, Mags = 5, Reload = 6f, SpreadDeg = 0.35f, Kick = 0f, Flash = 1f,
        TracerEvery = 5, TraceM = 900f,
    };

    // The chin gun's 30x113 mm HEDP (M789) carries no tracer: the gunner walks his bursts on by where they burst.
    public static VWeapon ChinGun() => new()
    {
        Name = "30mm chain gun", Sound = Snd.Autocannon, Speed = 800f, Drag = 0.00035f, Damage = 140f, VehDamage = 26f, Pen = 50f,
        Explosive = true, Crater = 0.35f, FragR = 3.5f, Power = 0.25f, Rpm = 600f, Mag = 300, Mags = 3, Reload = 8f, SpreadDeg = 0.45f, Kick = 0.05f, Flash = 1.6f,
    };

    /// <summary>
    /// The gunship's anti-tank missile, out to 7 km: it climbs, then comes down on the target's roof, where the armour
    /// is thinnest. The Apache's (AGM-114L) has its own radar seeker: fire it and duck. The Viper's (AGM-114K) rides
    /// the aircraft's laser, which has to stay on the target until it hits.
    /// </summary>
    public static VWeapon Hellfire(bool radar) => new()
    {
        Name = radar ? "Hellfire (radar)" : "Hellfire (laser)", Sound = Snd.Rocket, Speed = 425f, Drag = 0f, Damage = 350f, VehDamage = 900f, Pen = 1000f,
        Explosive = true, Crater = 1f, FragR = 9f, Power = 4f, Rpm = 30f, Mag = 8, Mags = 0, Reload = 0f, SpreadDeg = 0.1f, Kick = 0f, Flash = 2f,
        Guided = true, FireAndForget = radar, Range = 7000f,
    };

    /// <summary>
    /// An IFV's anti-tank missile, wire-guided along the gunner's line of sight: he holds the sight on the target and the
    /// missile flies down it, so it goes wherever he keeps the crosshair until it strikes. Lose sight of the target (the
    /// launcher is hit, the target goes behind something) and it flies on unguided. Fired from a halt. (The IFVs had only
    /// their cannon and coax: they couldn't hurt a tank, fought each other with 30 mm at close range, and whichever came
    /// through turned its cannon on the infantry. In the embodied assault test one BMP killed 31 attackers in three minutes
    /// while six Bradleys failed to kill it.)
    /// - TOW-2, the Bradley's twin launcher: to 3,750 m at about 190 m/s on average (20 s to full range), about 900 mm of
    ///   armour, a 6 kg warhead. Seven carried; the launcher's two and two reloads of two are here (six). Reloading
    ///   the launcher means the crew working through the roof hatch: a minute and a half here (an estimate).
    /// - 9M113M Konkurs, on the BMP-2's turret roof: to 4,000 m at about 210 m/s (19 s), about 750 mm behind reactive
    ///   armour, a 2.7 kg warhead. Four carried, one on the launcher; the gunner reloads it in about a minute.
    /// Neither can be guided inside its first 65-75 m.
    /// </summary>
    public static VWeapon Tow() => new()
    {
        Name = "TOW-2", Sound = Snd.Rocket, Speed = 190f, Drag = 0f, Damage = 300f, VehDamage = 950f, Pen = 900f,
        Explosive = true, Crater = 0.8f, FragR = 6f, Power = 2.5f, Rpm = 3f, Mag = 2, Mags = 2, Reload = 90f, SpreadDeg = 0.1f, Kick = 0f, Flash = 2f,
        Guided = true, FireAndForget = false, LineOfSight = true, Range = 3750f, MinRange = 65f,
    };

    public static VWeapon Konkurs() => new()
    {
        Name = "9M113M Konkurs", Sound = Snd.Rocket, Speed = 210f, Drag = 0f, Damage = 280f, VehDamage = 800f, Pen = 750f,
        Explosive = true, Crater = 0.7f, FragR = 5f, Power = 1.5f, Rpm = 1f, Mag = 1, Mags = 3, Reload = 60f, SpreadDeg = 0.1f, Kick = 0f, Flash = 2f,
        Guided = true, FireAndForget = false, LineOfSight = true, Range = 4000f, MinRange = 75f,
    };

    /// <summary>The Hind's 9M120 Ataka, out to 6 km: steered by radio from the aircraft, whose gunner has to keep the target in sight until it hits.</summary>
    public static VWeapon Ataka() => new()
    {
        Name = "9M120 Ataka", Sound = Snd.Rocket, Speed = 500f, Drag = 0f, Damage = 350f, VehDamage = 850f, Pen = 900f,
        Explosive = true, Crater = 1f, FragR = 8f, Power = 3.5f, Rpm = 30f, Mag = 6, Mags = 0, Reload = 0f, SpreadDeg = 0.1f, Kick = 0f, Flash = 2f,
        Guided = true, FireAndForget = false, Range = 6000f,
    };

    public static VWeapon Hydra() => new()
    {
        Name = "70mm rockets", Sound = Snd.Rocket, Speed = 600f, Drag = 0.0001f, Damage = 300f, VehDamage = 160f, Pen = 180f,
        Explosive = true, Crater = 0.8f, FragR = 18f, Power = 4.5f, Rpm = 400f, Mag = 38, Mags = 0, Reload = 0f, SpreadDeg = 0.9f, Kick = 0f, Flash = 1.5f,
    };

    /// <summary>35 mm anti-aircraft: proximity-fused against aircraft; the AP belt is for ground targets.</summary>
    public static VWeapon[] AaGun() => new[]
    {
        new VWeapon { Name = "35mm HEI prox", Sound = Snd.Autocannon, Speed = 1175f, Drag = 0.00015f, Damage = 140f, VehDamage = 70f, Pen = 20f,
                      Explosive = true, Crater = 0.3f, FragR = 4f, Power = 0.4f, Prox = true, Rpm = 1100f, Mag = 320, Mags = 2, Reload = 10f, SpreadDeg = 0.2f, Kick = 0.05f, Flash = 1.8f,
                      TracerEvery = 1, TraceM = 3500f }, // HEI-T: an anti-aircraft gunner needs his tracer out to the gun's reach
        new VWeapon { Name = "35mm AP", Sound = Snd.Autocannon, Speed = 1175f, Drag = 0.00015f, Damage = 140f, VehDamage = 30f, Pen = 55f,
                      Rpm = 1100f, Mag = 40, Mags = 2, Reload = 6f, SpreadDeg = 0.15f, Kick = 0.05f, Flash = 1.8f,
                      TracerEvery = 1, TraceM = 3000f }, // FAPDS-T
    };

    public static VWeapon MortarHe() => new()
    {
        Name = "81mm HE", Sound = Snd.MortarFire, Speed = 185f, Drag = 0f, Damage = 300f, VehDamage = 60f, Pen = 20f,
        Explosive = true, Crater = 1.6f, FragR = 25f, Power = 4f, Rpm = 18f, Mag = 1, Mags = 40, Reload = 0f, SpreadDeg = 0.5f, Kick = 0.15f, Flash = 1.5f,
    };

    /// <summary>
    /// 81 mm illumination (M853A1): a time fuze throws out a 600 000 cd candle on a parachute about 600 m over the
    /// target, which burns for about a minute as it comes down. It flies to the HE's firing table, so the tube is laid
    /// for it the same way; a section carries a few for a night's work. What goes on to the ground is the empty shell body.
    /// </summary>
    public static VWeapon MortarIllum() => new()
    {
        Name = "81mm illumination", Sound = Snd.MortarFire, Speed = 185f, Drag = 0f, Damage = 300f, VehDamage = 5f, Pen = 5f,
        Rpm = 18f, Mag = 1, Mags = 6, Reload = 0f, SpreadDeg = 0.5f, Kick = 0.15f, Flash = 1.5f, IllumCd = 600000f, IllumS = 60f,
    };

    // ---------------------------------------------------------------- the catalogue

    static readonly Color[] Paints = { new(0.36f, 0.4f, 0.42f), new(0.27f, 0.32f, 0.2f), new(0.56f, 0.5f, 0.35f) };

    public static readonly Dictionary<(VKind, int), VehicleDef> All = new();

    public static VehicleDef Get(VKind k, int faction)
    {
        if (!All.TryGetValue((k, faction), out var d)) All[(k, faction)] = d = Make(k, faction);
        return d;
    }

    static readonly string[,] Names =
    {
        { "M1151 HMMWV", "M939 truck", "M939 logistics", "Stryker M1126", "M2A3 Bradley", "M1A2 Abrams", "Stryker M1128", "UH-60 Black Hawk", "AH-64 Apache", "Gepard", "M252 mortar" },
        { "GAZ Tigr", "Ural-4320", "Ural-4320 logistics", "BTR-82A", "BMP-2", "T-72B3", "2S25 Sprut", "Mi-8 Hip", "Mi-24 Hind", "ZSU-23-4 Shilka", "2B14 mortar" },
        { "Land Rover WMIK", "MAN HX60", "MAN HX60 logistics", "Boxer", "Warrior", "Challenger 2", "Centauro", "AW101 Merlin", "AH-1Z Viper", "Stormer AA", "L16 mortar" },
    };

    static VehicleDef Make(VKind k, int f)
    {
        var d = new VehicleDef { Kind = k, Faction = f, Name = Names[f, (int)k], Paint = Paints[f], Style = f };
        // Faction flavour: Alpha long and low, Bravo short, tall and fast-turning, Charlie in between.
        float len = f == 1 ? 0.92f : f == 2 ? 1.03f : 1f;
        float hgt = f == 1 ? 1.08f : f == 2 ? 1.02f : 0.95f;
        switch (k)
        {
            case VKind.LTV:
                d.Hull = new Vector3(2.2f, 1.2f, 4.8f * len); d.MaxSpeed = 24f; d.Reverse = 7f; d.Accel = 6f; d.TurnRate = 55f;
                d.ArmorFront = d.ArmorSide = d.ArmorRear = 5f; d.ArmorTop = 3f; d.Hp = 260f; d.Tickets = 3; d.Respawn = 120f;
                d.Turrets.Add(new TurretDef { Mount = new Vector3(0f, 1.65f, 0.5f), YawSpeed = 120f, PitchSpeed = 80f, PitchMin = -12f, PitchMax = 35f,
                    Size = new Vector3(0.7f, 0.25f, 0.7f), BarrelLen = 1.5f, BarrelRadius = 0.035f, Exposed = true, Ammo = new[] { Hmg() } });
                d.Seats.Add(new SeatDef { Role = SeatRole.Driver, Pos = new Vector3(-0.45f, 1.55f, -0.5f) });
                d.Seats.Add(new SeatDef { Role = SeatRole.Gunner, Pos = new Vector3(0f, 2.25f, 0.5f), Turret = 0, Exposed = true });
                d.Seats.Add(new SeatDef { Role = SeatRole.Passenger, Pos = new Vector3(0.45f, 1.55f, -0.5f) });
                d.Seats.Add(new SeatDef { Role = SeatRole.Passenger, Pos = new Vector3(0.5f, 1.55f, 1.3f), Exposed = true });
                break;
            case VKind.Transport:
            case VKind.Logistics:
                d.Hull = new Vector3(2.5f, 1.6f, 7.4f * len); d.GroundClear = 0.8f; d.MaxSpeed = 20f; d.Reverse = 6f; d.Accel = 4f; d.TurnRate = 38f; d.Axles = 3;
                d.ArmorFront = d.ArmorSide = d.ArmorRear = 3f; d.ArmorTop = 2f; d.Hp = 320f; d.Tickets = k == VKind.Logistics ? 3 : 2; d.Respawn = 90f;
                d.Supplies = k == VKind.Logistics;
                d.Seats.Add(new SeatDef { Role = SeatRole.Driver, Pos = new Vector3(-0.5f, 2.3f, -2.6f * len) });
                d.Seats.Add(new SeatDef { Role = SeatRole.Passenger, Pos = new Vector3(0.5f, 2.3f, -2.6f * len) });
                if (k == VKind.Transport)
                    for (int i = 0; i < 10; i++)
                        d.Seats.Add(new SeatDef { Role = SeatRole.Passenger, Pos = new Vector3(i % 2 == 0 ? -0.8f : 0.8f, 2.4f, (-0.6f + (i / 2) * 0.7f) * len), Exposed = true });
                break;
            case VKind.APC:
                d.Hull = new Vector3(2.9f, 1.8f * hgt, 7.6f * len); d.GroundClear = 0.5f; d.MaxSpeed = 19f; d.Reverse = 6f; d.Accel = 4.5f; d.TurnRate = 38f; d.Axles = 4;
                d.ArmorFront = 25f; d.ArmorSide = 15f; d.ArmorRear = 12f; d.ArmorTop = 10f; d.Hp = 800f; d.Tickets = 5; d.Respawn = 180f;
                d.Turrets.Add(new TurretDef { Mount = new Vector3(0f, 2.3f * hgt, -0.8f), YawSpeed = 70f, PitchSpeed = 40f, PitchMin = -10f, PitchMax = 50f,
                    Size = new Vector3(0.9f, 0.45f, 1.1f), BarrelLen = 1.6f, BarrelRadius = 0.04f, Ammo = new[] { Hmg() } });
                d.Seats.Add(new SeatDef { Role = SeatRole.Driver, Pos = new Vector3(-0.7f, 2.0f * hgt, -2.8f * len) });
                d.Seats.Add(new SeatDef { Role = SeatRole.Gunner, Pos = new Vector3(0f, 2.75f * hgt, -0.8f), Turret = 0 });
                for (int i = 0; i < 8; i++) d.Seats.Add(new SeatDef { Role = SeatRole.Passenger, Pos = new Vector3(i % 2 == 0 ? -0.6f : 0.6f, 1.9f, (0.2f + (i / 2) * 0.7f) * len) });
                break;
            case VKind.IFV:
                d.Hull = new Vector3(3.2f, 1.9f * hgt, 6.6f * len); d.GroundClear = 0.45f; d.Tracked = true; d.MaxSpeed = 15f; d.Reverse = 5f; d.Accel = 4f; d.TurnRate = 42f;
                d.ArmorFront = 70f; d.ArmorSide = 35f; d.ArmorRear = 22f; d.ArmorTop = 15f; d.Hp = 1000f; d.Tickets = 7; d.Respawn = 240f;
                d.Turrets.Add(new TurretDef { Mount = new Vector3(f == 1 ? 0f : 0.2f, 2.35f * hgt, f == 1 ? -0.9f : -0.4f), YawSpeed = 45f, PitchSpeed = 30f, PitchMin = -8f, PitchMax = 50f,
                    Size = new Vector3(1.9f, 0.7f, 2.0f), BarrelLen = 2.6f, BarrelRadius = 0.05f, Ammo = Autocannon().Append(f == 1 ? Konkurs() : Tow()).ToArray(), Coax = Coax() });
                d.Seats.Add(new SeatDef { Role = SeatRole.Driver, Pos = new Vector3(-0.8f, 2.0f * hgt, -2.4f * len) });
                d.Seats.Add(new SeatDef { Role = SeatRole.Gunner, Pos = new Vector3(0.2f, 3.0f * hgt, -0.4f), Turret = 0 });
                for (int i = 0; i < 6; i++) d.Seats.Add(new SeatDef { Role = SeatRole.Passenger, Pos = new Vector3(i % 2 == 0 ? -0.6f : 0.6f, 1.8f, (0.9f + (i / 2) * 0.6f) * len) });
                break;
            case VKind.MBT:
                d.Hull = new Vector3(3.6f, 1.4f * hgt, 7.6f * len); d.GroundClear = 0.45f; d.Tracked = true; d.MaxSpeed = 13f; d.Reverse = 4f; d.Accel = 3.2f; d.TurnRate = 34f;
                d.ArmorFront = f == 1 ? 480f : 520f; d.ArmorSide = 110f; d.ArmorRear = 45f; d.ArmorTop = 30f; d.Hp = 1600f; d.Tickets = 10; d.Respawn = 360f;
                d.Turrets.Add(new TurretDef { Mount = new Vector3(0f, 1.9f * hgt, f == 2 ? 0.1f : -0.2f), YawSpeed = 32f, PitchSpeed = 12f, PitchMin = -7f, PitchMax = 20f,
                    Size = new Vector3(2.6f, 0.9f, f == 1 ? 2.6f : 3.4f), BarrelLen = 5.2f, BarrelRadius = 0.09f, Ammo = Cannon(f == 1 ? 125f : 120f), Coax = Coax() });
                d.Seats.Add(new SeatDef { Role = SeatRole.Driver, Pos = new Vector3(0f, 1.85f * hgt, -3.0f * len) });
                d.Seats.Add(new SeatDef { Role = SeatRole.Gunner, Pos = new Vector3(0.5f, 2.9f * hgt, -0.2f), Turret = 0 });
                break;
            case VKind.MGS:
                d.Hull = new Vector3(3.0f, 1.6f * hgt, 7.2f * len); d.GroundClear = 0.5f; d.MaxSpeed = 20f; d.Reverse = 6f; d.Accel = 4.5f; d.TurnRate = 36f; d.Axles = 4;
                d.ArmorFront = 45f; d.ArmorSide = 25f; d.ArmorRear = 15f; d.ArmorTop = 15f; d.Hp = 900f; d.Tickets = 6; d.Respawn = 240f;
                d.Turrets.Add(new TurretDef { Mount = new Vector3(0f, 2.1f * hgt, 0f), YawSpeed = 38f, PitchSpeed = 15f, PitchMin = -8f, PitchMax = 18f,
                    Size = new Vector3(2.2f, 0.75f, 2.6f), BarrelLen = 4.6f, BarrelRadius = 0.075f, Ammo = Cannon(105f), Coax = Coax() });
                d.Seats.Add(new SeatDef { Role = SeatRole.Driver, Pos = new Vector3(-0.7f, 2.0f * hgt, -2.9f * len) });
                d.Seats.Add(new SeatDef { Role = SeatRole.Gunner, Pos = new Vector3(0.4f, 3.0f * hgt, 0f), Turret = 0 });
                break;
            case VKind.UH:
                // A utility helicopter: pilot, two door gunners, eight in the back.
                d.Hull = new Vector3(2.4f, 2.0f, 11.5f * len); d.GroundClear = 0.35f; d.Lift = 20f; d.AirSpeed = 65f; d.RotorRadius = 8f; d.Flares = 12;
                d.ArmorFront = d.ArmorSide = d.ArmorRear = 8f; d.ArmorTop = 6f; d.Hp = 700f; d.Tickets = 8; d.Respawn = 300f;
                d.Seats.Add(new SeatDef { Role = SeatRole.Driver, Pos = new Vector3(-0.5f, 1.9f, -3.6f * len) });
                foreach (float side in new[] { -1f, 1f })
                {
                    d.Turrets.Add(new TurretDef { Mount = new Vector3(side * 1.25f, 1.6f, -1.6f * len), MountYaw = side * -90f, YawLimit = 75f,
                        YawSpeed = 160f, PitchSpeed = 120f, PitchMin = -60f, PitchMax = 20f, Size = new Vector3(0.3f, 0.2f, 0.3f),
                        BarrelLen = 1.1f, BarrelRadius = 0.03f, Exposed = true, Ammo = new[] { DoorGun() } });
                    d.Seats.Add(new SeatDef { Role = SeatRole.Gunner, Pos = new Vector3(side * 0.95f, 2.1f, -1.6f * len), Turret = d.Turrets.Count - 1, Exposed = true });
                }
                for (int i = 0; i < 8; i++) d.Seats.Add(new SeatDef { Role = SeatRole.Passenger, Pos = new Vector3(i % 2 == 0 ? -0.55f : 0.55f, 1.8f, (-0.9f + (i / 2) * 0.55f) * len) });
                break;
            case VKind.AH:
                // A gunship: a gunner on the chin cannon in front, the pilot behind with the rocket pods.
                d.Hull = new Vector3(1.6f, 2.0f, 13f * len); d.GroundClear = 0.4f; d.Lift = 22f; d.AirSpeed = 75f; d.RotorRadius = 7.3f; d.Flares = 16;
                d.ArmorFront = 20f; d.ArmorSide = 14f; d.ArmorRear = 10f; d.ArmorTop = 8f; d.Hp = 900f; d.Tickets = 10; d.Respawn = 360f;
                d.Turrets.Add(new TurretDef { Mount = new Vector3(0f, 0.6f, -4.6f * len), YawLimit = 110f, YawSpeed = 90f, PitchSpeed = 70f, PitchMin = -60f, PitchMax = 12f,
                    Size = new Vector3(0.5f, 0.35f, 0.5f), BarrelLen = 1.6f, BarrelRadius = 0.05f, Ammo = new[] { ChinGun() } });
                d.Turrets.Add(new TurretDef { Mount = new Vector3(0f, 1.25f, -1.4f * len), Fixed = true, YawLimit = 0f, PitchMin = -4f, PitchMax = -4f,
                    Size = new Vector3(3.8f, 0.3f, 0.9f), BarrelLen = 0.9f, BarrelRadius = 0.18f, Ammo = new[] { Hydra(), f == 1 ? Ataka() : Hellfire(radar: f == 0) } });
                d.Seats.Add(new SeatDef { Role = SeatRole.Driver, Pos = new Vector3(0f, 2.4f, -2.8f * len), Turret = 1 });
                d.Seats.Add(new SeatDef { Role = SeatRole.Gunner, Pos = new Vector3(0f, 2.1f, -4.1f * len), Turret = 0 });
                if (f == 1) for (int i = 0; i < 4; i++) d.Seats.Add(new SeatDef { Role = SeatRole.Passenger, Pos = new Vector3(i % 2 == 0 ? -0.4f : 0.4f, 1.8f, -0.8f + (i / 2) * 0.6f) }); // the Hind carries troops
                break;
            case VKind.SPAA:
                d.Hull = new Vector3(3.2f, 1.7f * hgt, 6.9f * len); d.GroundClear = 0.45f; d.Tracked = true; d.MaxSpeed = 15f; d.Reverse = 5f; d.Accel = 4f; d.TurnRate = 42f;
                d.ArmorFront = 40f; d.ArmorSide = 25f; d.ArmorRear = 15f; d.ArmorTop = 12f; d.Hp = 850f; d.Tickets = 6; d.Respawn = 240f;
                d.Turrets.Add(new TurretDef { Mount = new Vector3(0f, 2.2f * hgt, 0f), YawSpeed = 90f, PitchSpeed = 60f, PitchMin = -8f, PitchMax = 85f,
                    Size = new Vector3(2.4f, 1.0f, 2.2f), BarrelLen = 3.2f, BarrelRadius = 0.045f, Ammo = AaGun() });
                d.Seats.Add(new SeatDef { Role = SeatRole.Driver, Pos = new Vector3(-0.8f, 2.0f * hgt, -2.4f * len) });
                d.Seats.Add(new SeatDef { Role = SeatRole.Gunner, Pos = new Vector3(0.3f, 3.1f * hgt, 0f), Turret = 0 });
                break;
            case VKind.Mortar:
                d.Hull = new Vector3(1.2f, 0.5f, 1.2f); d.GroundClear = 0f; d.MaxSpeed = 0f; d.Reverse = 0f; d.Accel = 1f; d.TurnRate = 0f;
                d.ArmorFront = d.ArmorSide = d.ArmorRear = d.ArmorTop = 2f; d.Hp = 120f; d.Tickets = 1; d.Respawn = 90f;
                d.Turrets.Add(new TurretDef { Mount = new Vector3(0f, 0.2f, 0f), Indirect = true, YawSpeed = 40f, PitchSpeed = 25f, PitchMin = 45f, PitchMax = 85f,
                    Size = new Vector3(0.3f, 0.1f, 0.3f), BarrelLen = 1.2f, BarrelRadius = 0.05f, Exposed = true, Ammo = new[] { MortarHe(), MortarIllum() } });
                d.Seats.Add(new SeatDef { Role = SeatRole.Gunner, Pos = new Vector3(0.6f, 1.2f, 0.6f), Turret = 0, Exposed = true });
                break;
        }
        if (f == 1) d.TurnRate *= 1.1f;
        foreach (var t in d.Turrets)
        {
            foreach (var w in t.Ammo) Tracers.Register(w);
            if (t.Coax != null) Tracers.Register(t.Coax);
        }
        return d;
    }
}
