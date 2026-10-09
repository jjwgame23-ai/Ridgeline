using static Ridgeline.People;

namespace Ridgeline;

/// <summary>
/// The three armies' orders of battle. Each army has about 50,000, organised like the army whose kit it uses.
/// Each has three divisions and the army troops round them (artillery, engineers, aviation, air defence,
/// sustainment, signals, police). About a third are in the fighting arms (infantry, armour, recon), and half with the
/// artillery, engineers and air defence. The sizes follow the real organisations:
/// - ALPHA, like the US Army. Divisions of an armoured, a Stryker and an infantry brigade combat team. 9-man squads
///   of two fire teams. Combined arms battalions of two tank and two mechanised companies. 4-man tank crews.
/// - BRAVO, like the Russian Ground Forces. Divisions of three motor rifle regiments and a tank regiment, heavier in
///   artillery, plus two separate motor rifle brigades. 8-man squads riding their own BMP or BTR. 3-man tank crews
///   (autoloader) and 10-tank companies.
/// - CHARLIE, like the British Army. Divisions of an armoured infantry and two mechanised brigades. 8-man sections
///   of two fire teams. Battalions with a support company (mortars, anti-tank, recce, machine guns). Armoured regiments
///   of 14-tank squadrons.
/// Armour starts short of its full establishment, at 75%: money buys the rest, and newer tiers.
/// </summary>
public static class Orbat
{
    const Echelon Sq = Echelon.Squad, Pl = Echelon.Platoon, Co = Echelon.Company, Bn = Echelon.Battalion, Bde = Echelon.Brigade, Div = Echelon.Division;

    /// <summary>What a unit is made of: its own members (its headquarters, or the squad itself), vehicles, and children.</summary>
    sealed class Tpl
    {
        public readonly Echelon E;
        public readonly Arm A;
        public readonly string Label;
        public readonly List<(Job Job, byte Rank)> Members = new();
        public readonly List<(VClass V, int N, byte Tier)> Vehicles = new();
        public readonly List<(Tpl T, int N)> Kids = new();

        public Tpl(Echelon e, Arm a, string label)
        {
            E = e;
            A = a;
            Label = label;
        }

        public Tpl M(Job j, byte r, int n = 1)
        {
            for (int k = 0; k < n; k++) Members.Add((j, r));
            return this;
        }

        public Tpl V(VClass v, int n, byte tier = 2)
        {
            Vehicles.Add((v, n, tier));
            return this;
        }

        public Tpl K(Tpl t, int n = 1)
        {
            Kids.Add((t, n));
            return this;
        }
    }

    /// <summary>
    /// A support unit of plain sections (about ten each, under a sergeant), with its people and vehicles shared out
    /// among them.
    /// </summary>
    static Tpl Sections(Echelon e, Arm a, string label, byte boss, (Job Job, int N)[] people, params (VClass V, int N)[] vehicles)
    {
        var t = new Tpl(e, a, label).M(Job.Commander, boss).M(Job.Sergeant, e >= Co ? E8 : E7);
        if (e >= Co) t.M(Job.Signaller, E4).M(Job.Driver, E3);
        int total = people.Sum(p => p.N), sections = Math.Max(1, (int)Math.Round(total / 10.0));
        var list = people.SelectMany(p => Enumerable.Repeat(p.Job, p.N)).ToList();
        var veh = vehicles.SelectMany(v => Enumerable.Repeat(v.V, v.N)).ToList();
        for (int s = 0; s < sections; s++)
        {
            var sec = new Tpl(Sq, a, "Section").M(Job.SquadLeader, E6);
            for (int k = s; k < list.Count; k += sections) sec.M(list[k], k % 5 == 0 ? E5 : k % 5 <= 2 ? E4 : E3);
            for (int k = s; k < veh.Count; k += sections) sec.V(veh[k], 1);
            t.K(sec);
        }
        return t;
    }

    // ------------------------------------------------------------------ ALPHA: organised like the US Army

    static Tpl UsSquad(Arm a) => new Tpl(Sq, a, "Squad").M(Job.SquadLeader, E6).M(Job.TeamLeader, E5, 2).M(Job.MachineGunner, E4, 2)
        .M(Job.Grenadier, E3, 2).M(Job.Rifleman, E2, 2);

    static Tpl UsWeaponsSquad(Arm a) => new Tpl(Sq, a, "Weapons Squad").M(Job.SquadLeader, E6).M(Job.MachineGunner, E4, 2)
        .M(Job.Rifleman, E3, 2).M(Job.AntiTank, E4, 2).M(Job.Rifleman, E3, 2);

    static Tpl UsPlatoon(Arm a)
    {
        var p = new Tpl(Pl, a, "Platoon").M(Job.Commander, O1).M(Job.Sergeant, E7).M(Job.Signaller, E4).M(Job.Scout, E4).M(Job.Medic, E4)
            .K(UsSquad(a), 3);
        if (a == Arm.Mechanised) p.V(VClass.Ifv, 4).M(Job.Crewman, E5, 2).M(Job.Gunner, E4, 4).M(Job.Driver, E3, 4); // Bradleys
        else
        {
            p.K(UsWeaponsSquad(a));
            if (a == Arm.Motorised) p.V(VClass.Apc, 4).M(Job.Crewman, E5, 4).M(Job.Driver, E4, 4); // Strykers
        }
        return p;
    }

    static Tpl UsTankPlatoon() => new Tpl(Pl, Arm.Armour, "Platoon").M(Job.Commander, O1).M(Job.Sergeant, E7).M(Job.Crewman, E6, 2)
        .M(Job.Gunner, E5, 4).M(Job.Crewman, E3, 4).M(Job.Driver, E3, 4).V(VClass.Tank, 4);

    static Tpl UsCompany(Arm a, string label)
    {
        var c = new Tpl(Co, a, label).M(Job.Commander, O3).M(Job.Commander, O2).M(Job.Sergeant, E8).M(Job.Supply, E6)
            .M(Job.Signaller, E4).M(Job.Signaller, E3).M(Job.Medic, E5).M(Job.Medic, E4).M(Job.Driver, E3, 2).V(VClass.Ltv, 2).V(VClass.Truck, 1);
        if (a == Arm.Armour) return c.V(VClass.Tank, 2).M(Job.Gunner, E5, 2).M(Job.Crewman, E3, 2).M(Job.Driver, E3, 2).M(Job.Mechanic, E5, 3)
            .K(UsTankPlatoon(), 3);
        if (a == Arm.Mechanised) c.V(VClass.Ifv, 2).M(Job.Gunner, E4, 2).M(Job.Driver, E3, 2);
        // The company's 60 mm mortars.
        return c.K(UsPlatoon(a), 3).K(new Tpl(Sq, a, "Mortar Section").M(Job.SquadLeader, E6).M(Job.Mortarman, E5, 2).M(Job.Mortarman, E4, 2).M(Job.Mortarman, E3, 2));
    }

    static Tpl UsHhc(Arm a, string label) => new Tpl(Co, Arm.Headquarters, label)
        .M(Job.Commander, O5).M(Job.Staff, O4, 2).M(Job.Sergeant, E9).M(Job.Staff, O3, 4).M(Job.Staff, O2, 3).M(Job.Staff, E7, 4).M(Job.Staff, E5, 6)
        .M(Job.Signaller, E4, 6).M(Job.Driver, E3, 6).V(VClass.Ltv, 8)
        .K(new Tpl(Pl, Arm.Recon, "Scout Platoon").M(Job.Commander, O1).M(Job.Sergeant, E7).K(new Tpl(Sq, Arm.Recon, "Scout Section").M(Job.SquadLeader, E6)
            .M(Job.Scout, E5).M(Job.Scout, E4, 2).M(Job.Driver, E3, 2).V(VClass.Ltv, 2), 3))
        .K(new Tpl(Pl, Arm.Artillery, "Mortar Platoon").M(Job.Commander, O1).M(Job.Sergeant, E7).K(new Tpl(Sq, Arm.Artillery, "Mortar Section")
            .M(Job.SquadLeader, E6).M(Job.Mortarman, E5).M(Job.Mortarman, E4).M(Job.Driver, E3).V(VClass.Mortar, 1), 4))
        .K(Sections(Pl, Arm.Medical, "Medical Platoon", O2, new[] { (Job.Medic, 16), (Job.Driver, 6) }, (VClass.Ambulance, 6)))
        .K(Sections(Pl, Arm.Logistics, "Support Platoon", O1, new[] { (Job.Driver, 22), (Job.Supply, 6), (Job.Cook, 8), (Job.Mechanic, 14) },
            (VClass.Truck, 12), (VClass.Tanker, 4)));

    static Tpl UsBattalion(Arm a)
    {
        var b = new Tpl(Bn, a, "Battalion").K(UsHhc(a, "Headquarters Company"));
        if (a == Arm.Armour) return b.K(UsCompany(Arm.Armour, "Tank Company"), 2).K(UsCompany(Arm.Mechanised, "Rifle Company"), 2); // combined arms
        return b.K(UsCompany(a, "Rifle Company"), 3);
    }

    static Tpl UsBrigade(Arm a)
    {
        bool light = a == Arm.Infantry;
        var cavVehicle = a == Arm.Armour ? VClass.Ifv : a == Arm.Motorised ? VClass.Apc : VClass.Ltv;
        var b = new Tpl(Bde, a, a == Arm.Armour ? "Armored Brigade Combat Team" : a == Arm.Motorised ? "Stryker Brigade Combat Team" : "Infantry Brigade Combat Team")
            .M(Job.Commander, O6).M(Job.Staff, O5).M(Job.Sergeant, E9).M(Job.Staff, O4, 4).M(Job.Staff, O3, 8).M(Job.Staff, E7, 10).M(Job.Staff, E5, 16)
            .M(Job.Signaller, E4, 30).M(Job.Police, E4, 20).M(Job.Driver, E3, 20).V(VClass.Ltv, 24).V(VClass.Truck, 8);
        b.K(UsBattalion(a), 3);
        // Cavalry squadron: three troops of scouts.
        b.K(new Tpl(Bn, Arm.Recon, "Cavalry Squadron").K(UsHhc(Arm.Recon, "Headquarters Troop"))
            .K(new Tpl(Co, Arm.Recon, "Troop").M(Job.Commander, O3).M(Job.Sergeant, E8).M(Job.Signaller, E4, 2).V(VClass.Ltv, 2)
                .K(new Tpl(Pl, Arm.Recon, "Scout Platoon").M(Job.Commander, O1).M(Job.Sergeant, E7)
                    .K(new Tpl(Sq, Arm.Recon, "Scout Section").M(Job.SquadLeader, E6).M(Job.Scout, E5).M(Job.Scout, E4, 3).M(Job.Gunner, E4).M(Job.Driver, E3, 2)
                        .V(cavVehicle, 2), 3), 3), 3));
        // Field artillery battalion: three batteries of six howitzers (towed in the infantry brigade).
        b.K(new Tpl(Bn, Arm.Artillery, "Field Artillery Battalion").K(Sections(Co, Arm.Artillery, "Headquarters Battery", O5,
                new[] { (Job.Staff, 20), (Job.Signaller, 20), (Job.Gunner, 10), (Job.Mechanic, 12), (Job.Cook, 6), (Job.Driver, 14) }, (VClass.Ltv, 8), (VClass.Truck, 8)))
            .K(new Tpl(Co, Arm.Artillery, "Battery").M(Job.Commander, O3).M(Job.Commander, O2).M(Job.Sergeant, E8).M(Job.Staff, E6, 3).M(Job.Signaller, E4, 3)
                .V(VClass.Ltv, 2).V(VClass.Truck, 4)
                .K(new Tpl(Sq, Arm.Artillery, "Gun Section").M(Job.SquadLeader, E6).M(Job.Gunner, E5).M(Job.Gunner, E4, 2).M(Job.Gunner, E3, 3).M(Job.Driver, E3)
                    .V(VClass.Howitzer, 1, light ? (byte)1 : (byte)2), 6)
                .K(Sections(Pl, Arm.Artillery, "Ammunition Section", O1, new[] { (Job.Driver, 8), (Job.Supply, 4) }, (VClass.Truck, 6))), 3));
        b.K(new Tpl(Bn, Arm.Engineers, "Brigade Engineer Battalion")
            .K(Sections(Co, Arm.Headquarters, "Headquarters Company", O5, new[] { (Job.Staff, 24), (Job.Signaller, 10), (Job.Driver, 12) }, (VClass.Ltv, 8)))
            .K(Sections(Co, Arm.Engineers, "Engineer Company", O3, new[] { (Job.Engineer, 80), (Job.Driver, 14), (Job.Mechanic, 6) }, (VClass.Engineer, 6), (VClass.Truck, 8)), 2)
            .K(Sections(Co, Arm.Signals, "Signal Company", O3, new[] { (Job.Signaller, 60), (Job.Driver, 12) }, (VClass.Truck, 10)))
            .K(Sections(Co, Arm.Headquarters, "Military Intelligence Company", O3, new[] { (Job.Staff, 40), (Job.Scout, 14) }, (VClass.Ltv, 8))));
        b.K(new Tpl(Bn, Arm.Logistics, "Brigade Support Battalion")
            .K(Sections(Co, Arm.Headquarters, "Headquarters Company", O5, new[] { (Job.Staff, 30), (Job.Signaller, 12), (Job.Driver, 10) }, (VClass.Ltv, 6)))
            .K(Sections(Co, Arm.Logistics, "Distribution Company", O3, new[] { (Job.Driver, 90), (Job.Supply, 40) }, (VClass.Truck, 40), (VClass.Tanker, 16)))
            .K(Sections(Co, Arm.Maintenance, "Field Maintenance Company", O3, new[] { (Job.Mechanic, 120), (Job.Driver, 20) }, (VClass.Truck, 14)))
            .K(Sections(Co, Arm.Medical, "Medical Company", O3, new[] { (Job.Medic, 70), (Job.Driver, 14) }, (VClass.Ambulance, 12)))
            .K(Sections(Co, Arm.Logistics, "Forward Support Company", O3, new[] { (Job.Driver, 30), (Job.Mechanic, 24), (Job.Cook, 12), (Job.Supply, 10) },
                (VClass.Truck, 14), (VClass.Tanker, 6)), 5));
        return b;
    }

    static Tpl UsArmy()
    {
        var army = new Tpl(Echelon.Army, Arm.Headquarters, "Army").M(Job.Commander, O9).M(Job.Staff, O8).M(Job.Staff, O7, 2).M(Job.Sergeant, E9)
            .M(Job.Staff, O6, 8).M(Job.Staff, O5, 16).M(Job.Staff, O4, 30).M(Job.Staff, O3, 40).M(Job.Staff, E7, 50).M(Job.Staff, E5, 60)
            .M(Job.Signaller, E4, 80).M(Job.Driver, E3, 60).M(Job.Police, E4, 50).V(VClass.Ltv, 40).V(VClass.Truck, 30);
        for (int d = 0; d < 3; d++)
            army.K(new Tpl(Div, Arm.Headquarters, "Division").M(Job.Commander, O8).M(Job.Staff, O7, 2).M(Job.Sergeant, E9).M(Job.Staff, O6, 4)
                .M(Job.Staff, O5, 10).M(Job.Staff, O4, 20).M(Job.Staff, O3, 30).M(Job.Staff, E7, 40).M(Job.Staff, E5, 50).M(Job.Signaller, E4, 120)
                .M(Job.Police, E4, 60).M(Job.Driver, E3, 60).V(VClass.Ltv, 40).V(VClass.Truck, 20)
                .K(UsBrigade(Arm.Armour)).K(UsBrigade(Arm.Motorised)).K(UsBrigade(Arm.Infantry))
                .K(Rockets("Division Artillery", 18)).K(AirDefence("Air Defense Artillery Battalion", 12))
                .K(Sustainment(Bn, "Division Sustainment Battalion", 420, 120, 40)));
        return ArmyTroops(army, "Field Artillery Brigade", "Engineer Brigade", "Combat Aviation Brigade", "Air Defense Artillery Brigade",
            "Sustainment Brigade", "Military Police Battalion", "Signal Battalion");
    }

    // ------------------------------------------------------------------ BRAVO: organised like the Russian Ground Forces

    static Tpl RuSquad(VClass carrier) => new Tpl(Sq, Arm.Mechanised, "Squad").M(Job.SquadLeader, E5).M(Job.Gunner, E3).M(Job.Driver, E3)
        .M(Job.MachineGunner, E2).M(Job.AntiTank, E2).M(Job.Rifleman, E1).M(Job.Grenadier, E2).M(Job.Medic, E2).V(carrier, 1);

    // Three vehicles to a platoon, one per squad, with the platoon commander riding in the first; ten to a company.
    static Tpl RuCompany(VClass carrier) => new Tpl(Co, Arm.Mechanised, "Company").M(Job.Commander, O3).M(Job.Commander, O2).M(Job.Sergeant, E7)
        .M(Job.Medic, E4).M(Job.Signaller, E3).M(Job.Gunner, E3).M(Job.Driver, E3).V(carrier, 1)
        .K(new Tpl(Pl, Arm.Mechanised, "Platoon").M(Job.Commander, O2).M(Job.Sergeant, E6).K(RuSquad(carrier), 3), 3);

    static Tpl RuTankCompany() => new Tpl(Co, Arm.Armour, "Tank Company").M(Job.Commander, O3).M(Job.Gunner, E4).M(Job.Driver, E3).M(Job.Sergeant, E7)
        .M(Job.Mechanic, E5, 2).V(VClass.Tank, 1)
        .K(new Tpl(Pl, Arm.Armour, "Tank Platoon").M(Job.Commander, O2).M(Job.Gunner, E4).M(Job.Driver, E3)
            .K(new Tpl(Sq, Arm.Armour, "Tank Crew").M(Job.Crewman, E5).M(Job.Gunner, E4).M(Job.Driver, E3).V(VClass.Tank, 1), 2).V(VClass.Tank, 1), 3);

    static Tpl RuBattalionHq(Arm a) => new Tpl(Co, Arm.Headquarters, "Battalion Headquarters").M(Job.Commander, O4).M(Job.Staff, O3, 3).M(Job.Staff, O2, 2)
        .M(Job.Sergeant, E8).M(Job.Signaller, E4, 8).M(Job.Driver, E3, 6).V(VClass.Ltv, 4).V(VClass.Truck, 2)
        .K(Sections(Pl, Arm.Logistics, "Support Platoon", O1, new[] { (Job.Driver, 14), (Job.Supply, 6), (Job.Cook, 6), (Job.Mechanic, 8) }, (VClass.Truck, 10), (VClass.Tanker, 3)))
        .K(Sections(Pl, Arm.Medical, "Medical Point", O2, new[] { (Job.Medic, 8), (Job.Driver, 2) }, (VClass.Ambulance, 2)));

    static Tpl RuMotorRifleBattalion(VClass carrier) => new Tpl(Bn, Arm.Mechanised, "Motor Rifle Battalion").K(RuBattalionHq(Arm.Mechanised))
        .K(new Tpl(Co, Arm.Artillery, "Mortar Battery").M(Job.Commander, O3).M(Job.Sergeant, E7).M(Job.Signaller, E3, 2)
            .K(new Tpl(Sq, Arm.Artillery, "Mortar Crew").M(Job.SquadLeader, E5).M(Job.Mortarman, E4).M(Job.Mortarman, E3, 3).M(Job.Driver, E3).V(VClass.Mortar, 1), 6))
        .K(new Tpl(Pl, Arm.Mechanised, "Anti-Tank Platoon").M(Job.Commander, O2).M(Job.Sergeant, E6)
            .K(new Tpl(Sq, Arm.Mechanised, "ATGM Team").M(Job.SquadLeader, E5).M(Job.AntiTank, E4, 2).M(Job.Driver, E3).V(VClass.Ltv, 1), 4))
        .K(new Tpl(Pl, Arm.Mechanised, "Grenade Launcher Platoon").M(Job.Commander, O2).M(Job.Sergeant, E6)
            .K(new Tpl(Sq, Arm.Mechanised, "AGS Team").M(Job.SquadLeader, E5).M(Job.Grenadier, E4, 2).M(Job.Grenadier, E3, 2), 3))
        .K(RuCompany(carrier), 3);

    static Tpl RuTankBattalion() => new Tpl(Bn, Arm.Armour, "Tank Battalion").K(RuBattalionHq(Arm.Armour)).K(RuTankCompany(), 3);

    static Tpl RuRegimentTroops(Tpl r)
    {
        r.K(new Tpl(Bn, Arm.Artillery, "Self-Propelled Artillery Battalion").K(Sections(Co, Arm.Artillery, "Headquarters Battery", O4,
                new[] { (Job.Staff, 14), (Job.Signaller, 12), (Job.Driver, 12), (Job.Mechanic, 8) }, (VClass.Ltv, 4), (VClass.Truck, 10)))
            .K(new Tpl(Co, Arm.Artillery, "Battery").M(Job.Commander, O3).M(Job.Sergeant, E7).M(Job.Staff, E5, 2).M(Job.Signaller, E3, 2)
                .K(new Tpl(Sq, Arm.Artillery, "Gun Crew").M(Job.SquadLeader, E5).M(Job.Gunner, E4).M(Job.Gunner, E3, 2).M(Job.Driver, E3).V(VClass.Howitzer, 1), 6), 3));
        r.K(AirDefence("Air Defence Battery", 4, Co));
        r.K(new Tpl(Co, Arm.Recon, "Reconnaissance Company").M(Job.Commander, O3).M(Job.Sergeant, E7)
            .K(new Tpl(Sq, Arm.Recon, "Recon Squad").M(Job.SquadLeader, E5).M(Job.Scout, E4, 2).M(Job.Scout, E3, 3).M(Job.Driver, E3).V(VClass.Ifv, 1, 1), 7));
        r.K(Sections(Co, Arm.Engineers, "Engineer Company", O3, new[] { (Job.Engineer, 60), (Job.Driver, 14) }, (VClass.Engineer, 4), (VClass.Truck, 8)));
        r.K(Sections(Co, Arm.Signals, "Signal Company", O3, new[] { (Job.Signaller, 50), (Job.Driver, 10) }, (VClass.Truck, 8)));
        r.K(Sustainment(Bn, "Material Support Battalion", 160, 60, 30));
        r.K(Sections(Co, Arm.Maintenance, "Repair Company", O3, new[] { (Job.Mechanic, 60), (Job.Driver, 10) }, (VClass.Truck, 10)));
        r.K(Sections(Co, Arm.Medical, "Medical Company", O3, new[] { (Job.Medic, 40), (Job.Driver, 8) }, (VClass.Ambulance, 8)));
        return r;
    }

    static Tpl RuRegimentHq(string label) => new Tpl(Bde, Arm.Mechanised, label).M(Job.Commander, O6).M(Job.Staff, O5, 2).M(Job.Staff, O4, 4)
        .M(Job.Staff, O3, 6).M(Job.Sergeant, E9).M(Job.Staff, E6, 10).M(Job.Signaller, E4, 16).M(Job.Driver, E3, 12).V(VClass.Ltv, 10).V(VClass.Truck, 4);

    static Tpl RuMotorRifleRegiment(VClass carrier) => RuRegimentTroops(RuRegimentHq("Motor Rifle Regiment")
        .K(RuMotorRifleBattalion(carrier), 3).K(RuTankBattalion()));

    static Tpl RuTankRegiment()
    {
        var r = RuRegimentHq("Tank Regiment").K(RuTankBattalion(), 3).K(RuMotorRifleBattalion(VClass.Ifv));
        return RuRegimentTroops(r);
    }

    static Tpl RuArmy()
    {
        var army = new Tpl(Echelon.Army, Arm.Headquarters, "Combined Arms Army").M(Job.Commander, O9).M(Job.Staff, O8).M(Job.Staff, O7, 2).M(Job.Sergeant, E9)
            .M(Job.Staff, O6, 8).M(Job.Staff, O5, 16).M(Job.Staff, O4, 30).M(Job.Staff, O3, 40).M(Job.Staff, E7, 40).M(Job.Staff, E5, 60)
            .M(Job.Signaller, E4, 80).M(Job.Driver, E3, 60).M(Job.Police, E4, 40).V(VClass.Ltv, 40).V(VClass.Truck, 30);
        for (int d = 0; d < 3; d++)
            army.K(new Tpl(Div, Arm.Headquarters, "Motor Rifle Division").M(Job.Commander, O8).M(Job.Staff, O6, 4).M(Job.Staff, O5, 8).M(Job.Staff, O4, 16)
                .M(Job.Staff, O3, 24).M(Job.Sergeant, E9).M(Job.Staff, E6, 40).M(Job.Signaller, E4, 160).M(Job.Police, E4, 40).M(Job.Driver, E3, 60)
                .V(VClass.Ltv, 30).V(VClass.Truck, 30)
                .K(RuMotorRifleRegiment(VClass.Ifv), 2).K(RuMotorRifleRegiment(VClass.Apc)).K(RuTankRegiment())
                .K(new Tpl(Bde, Arm.Artillery, "Artillery Regiment").M(Job.Commander, O6).M(Job.Staff, O4, 4).M(Job.Signaller, E4, 20).M(Job.Driver, E3, 10).V(VClass.Ltv, 8)
                    .K(Guns("Howitzer Battalion", 18, Bn), 2).K(Rockets("Rocket Battalion", 12)))
                .K(AirDefence("Air Defence Regiment", 16, Bde))
                .K(new Tpl(Bn, Arm.Recon, "Reconnaissance Battalion").K(RuBattalionHq(Arm.Recon))
                    .K(new Tpl(Co, Arm.Recon, "Recon Company").M(Job.Commander, O3).M(Job.Sergeant, E7)
                        .K(new Tpl(Sq, Arm.Recon, "Recon Squad").M(Job.SquadLeader, E5).M(Job.Scout, E4, 2).M(Job.Scout, E3, 3).M(Job.Driver, E3).V(VClass.Ifv, 1, 1), 9), 3))
                .K(new Tpl(Bn, Arm.Engineers, "Engineer Battalion").K(Sections(Co, Arm.Engineers, "Engineer Company", O3,
                    new[] { (Job.Engineer, 70), (Job.Driver, 16) }, (VClass.Engineer, 6), (VClass.Truck, 8)), 3))
                .K(Sustainment(Bn, "Material Support Battalion", 400, 120, 40))
                .K(Sections(Bn, Arm.Medical, "Medical Battalion", O4, new[] { (Job.Medic, 150), (Job.Driver, 30) }, (VClass.Ambulance, 24))));
        army.K(RuMotorRifleRegiment(VClass.Apc), 2); // separate motor rifle brigades, organised as reinforced regiments
        return ArmyTroops(army, "Artillery Brigade", "Engineer Regiment", "Army Aviation Brigade", "Air Defence Brigade",
            "Material Support Brigade", "Military Police Battalion", "Signal Brigade");
    }

    // ------------------------------------------------------------------ CHARLIE: organised like the British Army

    static Tpl UkSection(Arm a) => new Tpl(Sq, a, "Section").M(Job.SquadLeader, E4).M(Job.TeamLeader, E3).M(Job.MachineGunner, E2, 2)
        .M(Job.Grenadier, E2, 2).M(Job.Marksman, E2).M(Job.Rifleman, E2);

    static Tpl UkPlatoon(Arm a)
    {
        var p = new Tpl(Pl, a, "Platoon").M(Job.Commander, O1).M(Job.Sergeant, E5).M(Job.Signaller, E3).M(Job.MachineGunner, E3).K(UkSection(a), 3);
        if (a == Arm.Mechanised) p.V(VClass.Ifv, 4).M(Job.Crewman, E4, 4).M(Job.Gunner, E3, 4).M(Job.Driver, E2, 4); // Warriors
        else if (a == Arm.Motorised) p.V(VClass.Apc, 4).M(Job.Crewman, E4, 4).M(Job.Driver, E2, 4); // Boxers
        return p;
    }

    static Tpl UkCompany(Arm a) => new Tpl(Co, a, "Company").M(Job.Commander, O4).M(Job.Commander, O3).M(Job.Sergeant, E7).M(Job.Supply, E6)
        .M(Job.Signaller, E3, 3).M(Job.Medic, E4, 2).M(Job.Driver, E3, 2).M(Job.Staff, E3).V(VClass.Ltv, 2).V(VClass.Truck, 2).K(UkPlatoon(a), 3);

    static Tpl UkBattalion(Arm a) => new Tpl(Bn, a, "Battalion")
        .K(Sections(Co, Arm.Headquarters, "Headquarter Company", O5, new[] { (Job.Staff, 30), (Job.Signaller, 26), (Job.Supply, 30), (Job.Mechanic, 30),
            (Job.Medic, 14), (Job.Cook, 16), (Job.Driver, 14) }, (VClass.Ltv, 10), (VClass.Truck, 14), (VClass.Tanker, 4), (VClass.Ambulance, 4)))
        .K(new Tpl(Co, a, "Support Company").M(Job.Commander, O4).M(Job.Sergeant, E7).M(Job.Signaller, E3, 2).V(VClass.Ltv, 2)
            .K(new Tpl(Pl, Arm.Artillery, "Mortar Platoon").M(Job.Commander, O2).M(Job.Sergeant, E6)
                .K(new Tpl(Sq, Arm.Artillery, "Mortar Section").M(Job.SquadLeader, E4).M(Job.Mortarman, E3, 2).M(Job.Mortarman, E2, 2).V(VClass.Mortar, 1), 8))
            .K(new Tpl(Pl, a, "Anti-Tank Platoon").M(Job.Commander, O2).M(Job.Sergeant, E6)
                .K(new Tpl(Sq, a, "Javelin Detachment").M(Job.SquadLeader, E4).M(Job.AntiTank, E3, 2).M(Job.Driver, E2).V(VClass.Ltv, 1), 6))
            .K(new Tpl(Pl, Arm.Recon, "Recce Platoon").M(Job.Commander, O2).M(Job.Sergeant, E6)
                .K(new Tpl(Sq, Arm.Recon, "Recce Patrol").M(Job.SquadLeader, E4).M(Job.Scout, E3, 2).M(Job.Marksman, E3).V(VClass.Ltv, 1), 6))
            .K(new Tpl(Pl, a, "Machine Gun Platoon").M(Job.Commander, O2).M(Job.Sergeant, E6)
                .K(new Tpl(Sq, a, "GPMG Section").M(Job.SquadLeader, E4).M(Job.MachineGunner, E3, 2).M(Job.Rifleman, E2, 2), 4)))
        .K(UkCompany(a), 3);

    static Tpl UkArmouredRegiment() => new Tpl(Bn, Arm.Armour, "Armoured Regiment")
        .K(Sections(Co, Arm.Headquarters, "Headquarter Squadron", O5, new[] { (Job.Staff, 24), (Job.Signaller, 20), (Job.Mechanic, 40), (Job.Supply, 24),
            (Job.Cook, 10), (Job.Medic, 10), (Job.Driver, 20) }, (VClass.Ltv, 8), (VClass.Truck, 16), (VClass.Tanker, 6), (VClass.Tank, 2)))
        .K(new Tpl(Co, Arm.Armour, "Squadron").M(Job.Commander, O4).M(Job.Commander, O3).M(Job.Sergeant, E7).M(Job.Gunner, E4, 2).M(Job.Crewman, E3, 2)
            .M(Job.Driver, E3, 2).M(Job.Mechanic, E5, 4).V(VClass.Tank, 2).V(VClass.Truck, 2)
            .K(new Tpl(Pl, Arm.Armour, "Troop").M(Job.Commander, O1).M(Job.Sergeant, E5).M(Job.Crewman, E5).M(Job.Gunner, E4, 3).M(Job.Crewman, E3, 3)
                .M(Job.Driver, E2, 3).V(VClass.Tank, 3), 4), 3);

    static Tpl UkBrigade(bool armoured)
    {
        var b = new Tpl(Bde, armoured ? Arm.Armour : Arm.Motorised, armoured ? "Armoured Infantry Brigade" : "Mechanised Brigade")
            .M(Job.Commander, O7).M(Job.Staff, O5).M(Job.Staff, O4, 4).M(Job.Staff, O3, 8).M(Job.Sergeant, E8).M(Job.Staff, E6, 12)
            .M(Job.Signaller, E4, 60).M(Job.Police, E4, 20).M(Job.Driver, E3, 24).V(VClass.Ltv, 24).V(VClass.Truck, 10);
        if (armoured) b.K(UkBattalion(Arm.Mechanised), 2).K(UkArmouredRegiment()).K(UkBattalion(Arm.Motorised));
        else b.K(UkBattalion(Arm.Motorised), 2).K(UkBattalion(Arm.Infantry));
        b.K(Guns("Artillery Regiment", 18, Bn, armoured ? (byte)2 : (byte)1));
        b.K(new Tpl(Bn, Arm.Engineers, "Engineer Regiment").K(Sections(Co, Arm.Engineers, "Field Squadron", O4,
            new[] { (Job.Engineer, 90), (Job.Driver, 20), (Job.Mechanic, 10) }, (VClass.Engineer, 6), (VClass.Truck, 10)), 3));
        b.K(Sustainment(Bn, "Logistic Regiment", 300, 100, 30));
        b.K(Sections(Bn, Arm.Maintenance, "REME Battalion", O5, new[] { (Job.Mechanic, 240), (Job.Driver, 30) }, (VClass.Truck, 24)));
        b.K(Sections(Bn, Arm.Medical, "Medical Regiment", O5, new[] { (Job.Medic, 160), (Job.Driver, 30) }, (VClass.Ambulance, 24)));
        b.K(new Tpl(Co, Arm.Recon, "Formation Reconnaissance Squadron").M(Job.Commander, O4).M(Job.Sergeant, E7).M(Job.Signaller, E3, 3)
            .K(new Tpl(Pl, Arm.Recon, "Troop").M(Job.Commander, O1).M(Job.Sergeant, E5)
                .K(new Tpl(Sq, Arm.Recon, "Patrol").M(Job.SquadLeader, E4).M(Job.Scout, E3, 2).M(Job.Gunner, E3).M(Job.Driver, E2).V(VClass.Ltv, 2), 4), 4));
        return b;
    }

    static Tpl UkArmy()
    {
        var army = new Tpl(Echelon.Army, Arm.Headquarters, "Army").M(Job.Commander, O9).M(Job.Staff, O8).M(Job.Staff, O7, 2).M(Job.Sergeant, E9)
            .M(Job.Staff, O6, 8).M(Job.Staff, O5, 16).M(Job.Staff, O4, 30).M(Job.Staff, O3, 40).M(Job.Staff, E7, 50).M(Job.Staff, E5, 60)
            .M(Job.Signaller, E4, 80).M(Job.Driver, E3, 60).M(Job.Police, E4, 50).V(VClass.Ltv, 40).V(VClass.Truck, 30);
        for (int d = 0; d < 3; d++)
            army.K(new Tpl(Div, Arm.Headquarters, "Division").M(Job.Commander, O8).M(Job.Staff, O7).M(Job.Staff, O6, 4).M(Job.Staff, O5, 10)
                .M(Job.Staff, O4, 20).M(Job.Staff, O3, 30).M(Job.Sergeant, E9).M(Job.Staff, E6, 50).M(Job.Signaller, E4, 160).M(Job.Police, E4, 60)
                .M(Job.Driver, E3, 60).V(VClass.Ltv, 40).V(VClass.Truck, 20)
                .K(UkBrigade(true)).K(UkBrigade(false), 2)
                .K(Rockets("Precision Fires Regiment", 12)).K(AirDefence("Air Defence Regiment", 12))
                .K(Sustainment(Bn, "Divisional Logistic Regiment", 380, 110, 40)));
        return ArmyTroops(army, "Artillery Brigade", "Engineer Brigade", "Aviation Brigade", "Air Defence Group",
            "Logistic Brigade", "Military Police Regiment", "Signal Brigade");
    }

    // ------------------------------------------------------------------ shared building blocks

    static Tpl Guns(string label, int guns, Echelon e, byte tier = 2)
    {
        var t = Sections(e, Arm.Artillery, label, O5, new[] { (Job.Staff, 20), (Job.Signaller, 20), (Job.Mechanic, 14), (Job.Cook, 8), (Job.Driver, 20) },
            (VClass.Ltv, 8), (VClass.Truck, 12));
        int batteries = Math.Max(1, guns / 6);
        for (int b = 0; b < batteries; b++)
            t.K(new Tpl(Co, Arm.Artillery, "Battery").M(Job.Commander, O3).M(Job.Sergeant, E8).M(Job.Staff, E6, 2).M(Job.Signaller, E4, 3).V(VClass.Ltv, 2).V(VClass.Truck, 6)
                .M(Job.Driver, E3, 6)
                .K(new Tpl(Sq, Arm.Artillery, "Gun Section").M(Job.SquadLeader, E6).M(Job.Gunner, E5).M(Job.Gunner, E4, 2).M(Job.Gunner, E3, 3).M(Job.Driver, E3)
                    .V(VClass.Howitzer, 1, tier), 6));
        return t;
    }

    static Tpl Rockets(string label, int launchers)
    {
        var t = Sections(Bn, Arm.Rockets, label, O5, new[] { (Job.Staff, 20), (Job.Signaller, 16), (Job.Mechanic, 12), (Job.Driver, 20) }, (VClass.Ltv, 8), (VClass.Truck, 10));
        for (int b = 0; b < Math.Max(1, launchers / 6); b++)
            t.K(new Tpl(Co, Arm.Rockets, "Battery").M(Job.Commander, O3).M(Job.Sergeant, E8).M(Job.Staff, E6, 2).M(Job.Signaller, E4, 2).V(VClass.Ltv, 2)
                .K(new Tpl(Sq, Arm.Rockets, "Launcher Section").M(Job.SquadLeader, E6).M(Job.Gunner, E4).M(Job.Driver, E3).V(VClass.Rocket, 1), 6)
                .K(Sections(Pl, Arm.Rockets, "Ammunition Platoon", O1, new[] { (Job.Driver, 14), (Job.Supply, 6) }, (VClass.Truck, 12))));
        return t;
    }

    static Tpl AirDefence(string label, int launchers, Echelon e = Bn)
    {
        var t = Sections(e, Arm.AirDefence, label, e >= Bde ? O6 : e == Bn ? O5 : O3,
            new[] { (Job.Staff, 10 + launchers), (Job.Signaller, 8 + launchers), (Job.Mechanic, 6 + launchers / 2), (Job.Driver, 6 + launchers) },
            (VClass.Ltv, 2 + launchers / 4), (VClass.Truck, 2 + launchers / 3));
        for (int k = 0; k < launchers; k++)
            t.K(new Tpl(Sq, Arm.AirDefence, "Fire Unit").M(Job.SquadLeader, E6).M(Job.Gunner, E4, 2).M(Job.Driver, E3).V(VClass.Spaa, 1));
        return t;
    }

    static Tpl Sustainment(Echelon e, string label, int drivers, int trucksPerHundred, int tankers) =>
        Sections(e, Arm.Logistics, label, e >= Bde ? O6 : O5, new[] { (Job.Driver, drivers), (Job.Supply, drivers / 3), (Job.Mechanic, drivers / 5),
            (Job.Cook, drivers / 12), (Job.Staff, 20), (Job.Medic, 10) }, (VClass.Truck, drivers * trucksPerHundred / 100), (VClass.Tanker, tankers));

    static Tpl ArmyTroops(Tpl army, string artillery, string engineers, string aviation, string airDefence, string sustainment, string police, string signals)
    {
        army.K(new Tpl(Bde, Arm.Artillery, artillery).M(Job.Commander, O6).M(Job.Staff, O5).M(Job.Staff, O4, 4).M(Job.Staff, O3, 8).M(Job.Signaller, E4, 30)
            .M(Job.Driver, E3, 16).V(VClass.Ltv, 12).V(VClass.Truck, 6)
            .K(Rockets("Rocket Battalion", 18), 2).K(Guns("Cannon Battalion", 18, Bn)));
        army.K(new Tpl(Bde, Arm.Engineers, engineers).M(Job.Commander, O6).M(Job.Staff, O4, 4).M(Job.Signaller, E4, 20).M(Job.Driver, E3, 10).V(VClass.Ltv, 8)
            .K(new Tpl(Bn, Arm.Engineers, "Engineer Battalion").K(Sections(Co, Arm.Engineers, "Engineer Company", O3,
                new[] { (Job.Engineer, 90), (Job.Driver, 20), (Job.Mechanic, 10) }, (VClass.Engineer, 8), (VClass.Truck, 10)), 3), 2)
            .K(Sections(Co, Arm.Engineers, "Bridge Company", O3, new[] { (Job.Engineer, 80), (Job.Driver, 40), (Job.Mechanic, 10) }, (VClass.Engineer, 12), (VClass.Truck, 20))));
        // Aviation: an attack battalion (24 gunships, two pilots each) and an assault battalion (30 transports, two
        // pilots and two crew chiefs each), with the ground crews that keep them flying.
        army.K(new Tpl(Bde, Arm.Aviation, aviation).M(Job.Commander, O6).M(Job.Staff, O4, 4).M(Job.Signaller, E4, 20).M(Job.Driver, E3, 10).V(VClass.Ltv, 8)
            .K(new Tpl(Bn, Arm.Aviation, "Attack Helicopter Battalion")
                .K(Sections(Co, Arm.Maintenance, "Aviation Maintenance Company", O3, new[] { (Job.Mechanic, 120), (Job.Supply, 40), (Job.Driver, 20) }, (VClass.Truck, 14), (VClass.Tanker, 6)))
                .K(new Tpl(Co, Arm.Aviation, "Attack Company").M(Job.Commander, O3).M(Job.Sergeant, E7)
                    .K(new Tpl(Sq, Arm.Aviation, "Gunship Crew").M(Job.Pilot, O2).M(Job.Pilot, O1).V(VClass.Gunship, 1), 8), 3))
            .K(new Tpl(Bn, Arm.Aviation, "Assault Helicopter Battalion")
                .K(Sections(Co, Arm.Maintenance, "Aviation Maintenance Company", O3, new[] { (Job.Mechanic, 120), (Job.Supply, 40), (Job.Driver, 20) }, (VClass.Truck, 14), (VClass.Tanker, 6)))
                .K(new Tpl(Co, Arm.Aviation, "Assault Company").M(Job.Commander, O3).M(Job.Sergeant, E7)
                    .K(new Tpl(Sq, Arm.Aviation, "Helicopter Crew").M(Job.Pilot, O2).M(Job.Pilot, O1).M(Job.Crewman, E5).M(Job.Crewman, E4).V(VClass.Helicopter, 1), 10), 3)));
        army.K(new Tpl(Bde, Arm.AirDefence, airDefence).M(Job.Commander, O6).M(Job.Staff, O4, 4).M(Job.Signaller, E4, 16).V(VClass.Ltv, 6)
            .K(AirDefence("Air Defence Battalion", 16), 2));
        army.K(new Tpl(Bde, Arm.Logistics, sustainment).M(Job.Commander, O6).M(Job.Staff, O5, 2).M(Job.Staff, O4, 6).M(Job.Staff, O3, 10).M(Job.Signaller, E4, 30)
            .M(Job.Driver, E3, 20).V(VClass.Ltv, 12)
            .K(Sustainment(Bn, "Transport Battalion", 420, 110, 60), 2)
            .K(Sections(Bn, Arm.Medical, "Medical Battalion", O5, new[] { (Job.Medic, 380), (Job.Driver, 60), (Job.Staff, 20) }, (VClass.Ambulance, 40), (VClass.Truck, 12)))
            .K(Sections(Bn, Arm.Maintenance, "Maintenance Battalion", O5, new[] { (Job.Mechanic, 400), (Job.Driver, 60), (Job.Supply, 40) }, (VClass.Truck, 40)))
            .K(Sustainment(Bn, "Supply Battalion", 220, 60, 30)));
        army.K(Sections(Bn, Arm.Police, police, O5, new[] { (Job.Police, 360), (Job.Driver, 40) }, (VClass.Ltv, 60)));
        army.K(Sections(Bn, Arm.Signals, signals, O5, new[] { (Job.Signaller, 400), (Job.Driver, 40), (Job.Mechanic, 20) }, (VClass.Truck, 40)));
        return army;
    }

    // ------------------------------------------------------------------ building an army

    /// <summary>Make one side's army: its units, soldiers and vehicles, named in its army's way.</summary>
    public static Unit Build(War war, int side, Random rng)
    {
        var tpl = side switch { 0 => UsArmy(), 1 => RuArmy(), _ => UkArmy() };
        var names = new Designations(side, rng);
        return Make(war, side, tpl, null, 0, names, rng);
    }

    static Unit Make(War war, int side, Tpl t, Unit? parent, int ordinal, Designations names, Random rng)
    {
        var u = war.AddUnit(side, t.E, t.A, parent);
        (u.Name, u.Short) = names.Name(t.E, t.A, t.Label, ordinal, parent, war);
        foreach (var (job, rank) in t.Members)
        {
            var (ammo, grenades, rockets) = Load(job);
            u.Members.Add(war.AddSoldier(new Soldier { Unit = u.Id, Side = (byte)side, Rank = rank, Job = job, Blood = 1f, Ammo = ammo, Grenades = grenades, Rockets = rockets }));
        }
        if (u.Members.Count > 0)
            u.Commander = u.Members.OrderByDescending(m => war.Soldiers[m].Rank).First();
        foreach (var (v, n, tier) in t.Vehicles)
        {
            // Armour starts at three-quarters of its establishment; the rest comes with money.
            int have = v is VClass.Tank or VClass.Ifv or VClass.Gunship ? (int)Math.Round(n * 0.75 + rng.NextDouble() * 0.5 - 0.25) : n;
            for (int k = 0; k < have; k++) u.Vehicles.Add(war.AddVehicle(new WarVehicle { Unit = u.Id, Class = v, Side = (byte)side, Tier = tier, Ammo = VehicleLoad(v), Mg = MgLoad(v) }));
        }
        var counts = new Dictionary<string, int>();
        foreach (var (kt, n) in t.Kids)
            for (int k = 0; k < n; k++)
            {
                counts.TryGetValue(kt.Label, out int seen);
                counts[kt.Label] = seen + 1;
                var c = Make(war, side, kt, u, seen, names, rng);
                u.Children.Add(c.Id);
            }
        return u;
    }

    /// <summary>
    /// A soldier's basic load, by job: a rifleman's seven 30-round magazines (210), a machine gunner's 600 linked
    /// rounds, a grenadier's 24 40 mm, an anti-tank gunner's two rockets; crews and support troops a carbine and four
    /// magazines. Most also have two hand grenades.
    /// </summary>
    static (short Ammo, byte Grenades, byte Rockets) Load(Job j) => j switch
    {
        Job.MachineGunner => (600, 2, 0),
        Job.Grenadier => (210, 24, 0),
        Job.AntiTank => (210, 2, 2),
        Job.Marksman => (140, 2, 0),
        Job.Rifleman or Job.TeamLeader or Job.SquadLeader or Job.Medic or Job.Scout or Job.Commander or Job.Sergeant or Job.Engineer => (210, 2, 0),
        _ => (120, 1, 0),
    };

    /// <summary>Main-gun rounds carried: an IFV's autocannon (Bradley 900, BMP-2 500, Warrior 230), a tank's main gun, the guns of air defence and artillery.</summary>
    static short VehicleLoad(VClass v) => v switch
    {
        VClass.Ifv => 400, VClass.Tank => 40, VClass.LightTank => 30, VClass.Spaa => 600,
        VClass.Howitzer => 40, VClass.Rocket => 12, VClass.Mortar => 60, _ => 0,
    };

    /// <summary>
    /// Machine-gun rounds carried: a tank's coax (M1A2 10,800, T-90 2,000, Challenger 2 4,000), an IFV's (Bradley
    /// 2,200, BMP-2 2,000), an APC's roof gun (Stryker 2,000), a light vehicle's.
    /// </summary>
    static short MgLoad(VClass v) => v switch
    {
        VClass.Tank => 4000, VClass.LightTank => 2000, VClass.Ifv => 2000, VClass.Apc => 2000, VClass.Ltv => 600, _ => 0,
    };

    /// <summary>
    /// Unit designations in each army's style: US lettered companies and regimental battalions ("B/1-15"), Russian
    /// numbered companies and regiments ("3 MRC/245 MRR"), British lettered companies and numbered platoons
    /// ("B Coy, 1/24 FOOT").
    /// </summary>
    sealed class Designations
    {
        readonly int _side;
        readonly Random _rng;
        readonly HashSet<int> _used = new();
        int _regimentPlatoon;

        public Designations(int side, Random rng)
        {
            _side = side;
            _rng = rng;
        }

        int Number(int lo, int hi)
        {
            for (int k = 0; k < 200; k++)
            {
                int v = _rng.Next(lo, hi);
                if (_used.Add(v)) return v;
            }
            return _rng.Next(lo, hi);
        }

        readonly Dictionary<int, int> _next = new();

        /// <summary>1, 2, 3... for each numbered child of a parent, in the order they're made.</summary>
        int Next(Unit? parent)
        {
            int key = parent?.Id ?? -1;
            _next.TryGetValue(key, out int v);
            _next[key] = ++v;
            return v;
        }

        static string Ord(int n) => n % 100 is 11 or 12 or 13 ? $"{n}th" : (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" };

        public (string Name, string Short) Name(Echelon e, Arm a, string label, int ordinal, Unit? parent, War war)
        {
            string p = parent?.Name ?? "", ps = parent?.Short ?? "";
            int n = ordinal + 1;
            bool line = label is "Company" or "Rifle Company" or "Tank Company" or "Troop" or "Battery" or "Squadron";
            switch (e)
            {
                case Echelon.Army:
                    int an = _side switch { 0 => Number(1, 9), 1 => Number(2, 59), _ => Number(1, 9) };
                    return _side switch
                    {
                        0 => ($"{Ord(an)} Army", $"{an} ARMY"),
                        1 => ($"{Ord(an)} Guards Combined Arms Army", $"{an} GCAA"),
                        _ => ($"{Ord(an)} Army", $"{an} ARMY"),
                    };
                case Echelon.Division:
                    int dn = Number(1, 60);
                    string dl = _side == 0 ? (ordinal == 0 ? "Armored" : "Infantry") : _side == 1 ? "Guards Motor Rifle" : "";
                    string dname = _side == 2 ? $"{Ord(dn)} Division" : $"{Ord(dn)} {dl} Division";
                    return (dname, $"{dn} DIV");
                case Echelon.Brigade:
                    if (_side == 1 && label.EndsWith("Regiment"))
                    {
                        int rn = Number(60, 999);
                        _regimentPlatoon = 0;
                        return ($"{Ord(rn)} {label}, {p}", $"{rn} {Initials(label)}");
                    }
                    // US brigades are numbered within their division; British ones across the army.
                    int brn = _side == 2 ? Number(1, 60) : Next(parent);
                    return ($"{Ord(brn)} {label}, {p}", $"{brn}/{ps}");
                case Echelon.Battalion:
                    if (_side == 0 && (a is Arm.Infantry or Arm.Mechanised or Arm.Motorised or Arm.Armour) && label == "Battalion")
                    {
                        int reg = Number(5, 90);
                        string branch = a == Arm.Armour ? "Armor" : "Infantry";
                        return ($"{Ord(n)} Battalion, {Ord(reg)} {branch}, {p}", $"{n}-{reg} {(a == Arm.Armour ? "AR" : "IN")}");
                    }
                    if (_side == 2 && label == "Battalion")
                    {
                        int foot = Number(1, 110);
                        _regimentPlatoon = 0;
                        return ($"{Ord(n)} Battalion, {Ord(foot)} Foot, {p}", $"{n}/{foot} FOOT");
                    }
                    if (_side == 1 && label.Contains("Battalion") && (a is Arm.Mechanised or Arm.Armour))
                        return ($"{Ord(n)} {label}, {p}", $"{n} {Initials(label)}/{ps}");
                    int bn = Number(100, 999);
                    return ($"{Ord(bn)} {label}, {p}", $"{bn} {Initials(label)}");
                case Echelon.Company:
                    if (line)
                    {
                        // Lettered through the battalion, tank and rifle companies alike (A and B tank, C and D rifle).
                        string letter = ((char)('A' + Next(parent) - 1)).ToString();
                        int cn = letter[0] - 'A' + 1;
                        if (_side == 1) return ($"{Ord(cn)} {label}, {p}", $"{cn}/{ps}");
                        string word = _side == 2 && label == "Company" ? "Company" : label.Replace("Rifle ", "").Replace("Tank ", "");
                        return ($"{letter} {word}, {p}", $"{letter}/{ps}");
                    }
                    return ($"{label}, {p}", $"{Initials(label)}/{ps}");
                case Echelon.Platoon:
                    if (label is "Platoon" or "Troop" or "Tank Platoon")
                    {
                        // British platoons are numbered through the battalion (1–3 in A Company, 4–6 in B...).
                        int pn = _side == 2 ? ++_regimentPlatoon : n;
                        return _side == 2 ? ($"{pn} {label}, {p}", $"{pn}/{ps}") : ($"{Ord(pn)} {label}, {p}", $"{pn}/{ps}");
                    }
                    return ($"{label}, {p}", $"{Initials(label)}/{ps}");
                default:
                    if (label is "Squad" or "Section" or "Tank Crew" or "Gun Section" or "Gun Crew" or "Scout Section" or "Recon Squad" or "Patrol")
                        return _side == 2 && label == "Section" ? ($"{n} {label}, {p}", $"{n}/{ps}") : ($"{Ord(n)} {label}, {p}", $"{n}/{ps}");
                    return ($"{label} {n}, {p}", $"{Initials(label)}{n}/{ps}");
            }
        }

        static string Initials(string label) => string.Concat(label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsUpper(w[0])).Select(w => w[0]));
    }
}
