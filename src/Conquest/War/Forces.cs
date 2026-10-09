namespace Ridgeline;

public enum Echelon : byte { Squad, Platoon, Company, Battalion, Brigade, Division, Army }

/// <summary>A unit's arm or service: what it's for.</summary>
public enum Arm : byte
{
    Infantry, Mechanised, Motorised, Armour, Recon, Artillery, Rockets, AirDefence, Engineers,
    Logistics, Medical, Maintenance, Signals, Aviation, Police, Headquarters,
}

/// <summary>The kinds of vehicle the armies field, each with a tier (1 light or old, 2 current, 3 the newest).</summary>
public enum VClass : byte
{
    Ltv, Truck, Tanker, Ambulance, Apc, Ifv, Tank, LightTank, Howitzer, Rocket, Mortar, Spaa, Engineer, Helicopter, Gunship,
}

/// <summary>One soldier's state in the abstract war (who they are is in <see cref="People"/>, from their number).</summary>
public struct Soldier
{
    public int Unit;          // the squad, crew or section they belong to
    public byte Side, Rank;
    public Job Job;
    public SoldierState State;
    public float Blood;       // 0..1 of a full blood volume
    public float Experience;  // grows with combat
    public short Ammo;        // rounds for their rifle or machine gun
    public byte Grenades;     // 40 mm for a grenadier, hand grenades for the rest
    public byte Rockets;      // anti-tank rounds
    public double Since;      // when they were hit, for the wounded and the down
    public bool Treated;      // a medic has stopped the bleeding
    public bool Replaced;     // dead or invalided, and a replacement has taken their place
}

/// <summary>
/// Fit; wounded but still going (a light wound); down (out of the fight, dying without help); evacuated (in the
/// medical chain, out of their unit for now); dead; recovering in hospital, due back; invalided home, out of the war;
/// joining their unit (back from hospital, or a replacement), on the way.
/// </summary>
public enum SoldierState : byte { Fit, Wounded, Down, Evacuated, Dead, Recovering, Invalided, Joining }

public struct WarVehicle
{
    public int Unit;
    public VClass Class;
    public byte Side, Tier;
    public bool Lost;
    public short Ammo;        // main gun rounds
    public short Mg;          // machine-gun rounds: a tank's or IFV's coax, an APC's or light vehicle's roof gun
}

/// <summary>
/// A unit at any echelon, from a squad or crew to the army. Its own members are its headquarters (or, for a squad,
/// the squad); everyone else is in its children. A unit that moves on its own is a mover: companies and batteries
/// move, and their platoons and squads go with them at an offset.
/// </summary>
public sealed class Unit
{
    public int Id, Side, Parent = -1;
    public Echelon Echelon;
    public Arm Arm;
    /// <summary>Its full designation ("2nd Platoon, B Company, 1-15 IN") and a short one for maps ("B/1-15").</summary>
    public string Name = "", Short = "";
    public readonly List<int> Children = new();
    public readonly List<int> Members = new();
    public readonly List<int> Vehicles = new();
    /// <summary>The soldier in command (a member of its headquarters), or -1.</summary>
    public int Commander = -1;
    /// <summary>Metres east and south of the island's centre.</summary>
    public float X, Z;
    /// <summary>The unit whose movement carries this one (itself if it moves on its own), and the offset from it.</summary>
    public int Mover = -1;
    public float OffX, OffZ;

    // ---- the war's running state
    /// <summary>What it's doing, and an order still being planned (it takes effect at its time).</summary>
    public Order? Order, Next;
    /// <summary>When its commander next looks at things.</summary>
    public double ThinkAt;
    /// <summary>Objectives it has been given (divisions and brigades).</summary>
    public readonly List<int> Tasks = new();
    public Mobility Mob;
    /// <summary>For a mover, how many fit soldiers it carries (its own and its platoons' and squads'), and how many it was raised with.</summary>
    public int People, Raised;
    /// <summary>The places in the unit itself (its members when raised), which replacements fill.</summary>
    public int Establishment;
    /// <summary>Consolidating and reorganising after an operation: no new one before this.</summary>
    public double RestUntil;
    /// <summary>The objective a battalion garrisons, or -1: one it took in the front line, which it stays on.</summary>
    public int Holds = -1;
    /// <summary>The planned offensive a battalion is massed for, or -1.</summary>
    public int Op = -1;
    /// <summary>
    /// Supply, for a mover: fuel in its tanks and their size (L), what it burns per km and per hour running, its food
    /// (kg), and since when it has had none (-1: fed). For a division or brigade, the depot of its support area; for a
    /// mover, the depot that supplies it. Hauls: a logistics company driving depot runs, kept off other moves.
    /// </summary>
    public float Fuel, FuelCap, PerKm, Idle, Food;
    public double HungrySince = -1;
    /// <summary>Hours of sleep the company owes (see <see cref="Rest"/>).</summary>
    public float SleepDebt;
    public int Depot = -1;
    public bool Hauls;
    /// <summary>
    /// Artillery, for a mover with mortars, guns or launchers: which they are, when it can fire again, when it moves after
    /// firing. For any company: when the fire it last called for has landed.
    /// </summary>
    public bool Guns;
    public VClass Fires;
    public double FireReady, DisplaceAt, CalledUntil;
    /// <summary>Rounds fired today, against its daily allowance.</summary>
    public int RoundsToday;
    /// <summary>For a mortar platoon on its own: the company it keeps near.</summary>
    public int Keeps = -1;
    /// <summary>When it was last shelled outside a fight with casualties.</summary>
    public double ShelledAt = double.MinValue;
    /// <summary>The fight it's in, or -1.</summary>
    public int InFight = -1;
    /// <summary>When it last pulled out of a fight: it won't be drawn into another straight away.</summary>
    public double Broke = double.MinValue;
    /// <summary>When it last came to a halt: two hours on, it's dug in.</summary>
    public double HaltedAt;
    /// <summary>When it was last held up: halted by enemies standing in its way, waiting on its battalion.</summary>
    public double HeldUp = double.MinValue;
    /// <summary>For a mover, the units it carries (itself included).</summary>
    public readonly List<int> Carries = new();
    /// <summary>A mover with a vehicle mounting a gun (IFV, tank, APC or armed light vehicle).</summary>
    public bool Armed;
    /// <summary>Where it was at the start of the last step, before it marched on.</summary>
    public float WasX, WasZ;
    /// <summary>The way it's going (move-grid cells), how far along, and where it ends.</summary>
    public List<int>? Path;
    public int PathAt;
    public float GoX, GoZ;
    /// <summary>Metres moved, all told and today, and seconds spent moving today.</summary>
    public double Marched, MarchedToday, MovedToday;
    public bool IsMover => Mover == Id;
}

public enum OrderKind : byte { Hold, Move, Occupy }

/// <summary>An order: what to do, where, and from when (after the time it takes to plan and pass on).</summary>
public sealed class Order
{
    public OrderKind Kind;
    /// <summary>The objective, for Occupy; -1 otherwise.</summary>
    public int Target = -1;
    public float X, Z;
    public double At;
    /// <summary>Done: arrived, or the objective is held.</summary>
    public bool Done;
    /// <summary>A move to get out of contact, not a march: made by night too, and past the day's march limit.</summary>
    public bool Tactical;
    /// <summary>A move made by night as well as by day, within the day's limit (supply convoys).</summary>
    public bool Night;
}

public enum ObjKind : byte { Town, Port, Node, Bridge, Hill }

/// <summary>Something on the island worth holding: a town, a port, a resource node, a bridge or a hill.</summary>
public sealed class Objective
{
    public int Id, Ref;
    public ObjKind Kind;
    public string Name = "";
    public float X, Z, Value, Radius;
    /// <summary>The side holding it (-1: nobody).</summary>
    public int Owner = -1;
    /// <summary>For each side, how many battalions it has sent to take it.</summary>
    public readonly int[] Claims = new int[3];
    /// <summary>For each side, set when it can't be reached, so it isn't tried again and again.</summary>
    public readonly bool[] Unreachable = new bool[3];
    /// <summary>For each side, when it may next try for it after an attack on it failed.</summary>
    public readonly double[] Retry = new double[3];
    /// <summary>When it last changed hands by being taken.</summary>
    public double TakenAt = double.MinValue;
}

/// <summary>One of the three armies at war.</summary>
public sealed class Side
{
    public int Index;
    public string Name = "";
    /// <summary>The enemy its army is attacking (-1: none yet, still taking open ground).</summary>
    public int Offensive = -1;
    public int Army = -1;    // the army's top unit
    public int Port = -1;    // the town it landed at
    /// <summary>When its army may plan its next offensive.</summary>
    public double NextOp;
}
