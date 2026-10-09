namespace Ridgeline;

/// <summary>What a soldier does in their unit.</summary>
public enum Job : byte
{
    Rifleman, TeamLeader, SquadLeader, MachineGunner, Grenadier, AntiTank, Marksman, Medic,
    Crewman, Driver, Gunner, Mortarman, Engineer, Scout, Signaller, Staff, Commander, Sergeant,
    Supply, Mechanic, Cook, Pilot, Police,
}

/// <summary>
/// Who the soldiers are. A soldier's name and nature are worked out from the war's seed and their number whenever
/// they're wanted, so 150,000 identities cost nothing to keep; only what happens to them is stored.
/// Ranks run on one scale for all three armies (1–9 enlisted, E1–E9; 10–19 officers, O1–O10), each army with its
/// own titles for them.
/// </summary>
public static class People
{
    public const byte E1 = 1, E2 = 2, E3 = 3, E4 = 4, E5 = 5, E6 = 6, E7 = 7, E8 = 8, E9 = 9;
    public const byte O1 = 10, O2 = 11, O3 = 12, O4 = 13, O5 = 14, O6 = 15, O7 = 16, O8 = 17, O9 = 18, O10 = 19;

    static readonly string[][] Titles =
    {
        // ALPHA, organised like the US Army.
        new[] { "", "PV1", "PV2", "PFC", "SPC", "SGT", "SSG", "SFC", "1SG", "SGM", "2LT", "1LT", "CPT", "MAJ", "LTC", "COL", "BG", "MG", "LTG", "GEN" },
        // BRAVO, like the Russian Ground Forces (no one-star rank there: a general-major leads a division).
        new[] { "", "Ryadovoy", "Ryadovoy", "Efreitor", "Ml. Serzhant", "Serzhant", "St. Serzhant", "Starshina", "Praporshchik", "St. Praporshchik",
                "Ml. Leytenant", "Leytenant", "Kapitan", "Mayor", "Podpolkovnik", "Polkovnik", "General-Mayor", "General-Mayor", "General-Leytenant", "General-Polkovnik" },
        // CHARLIE, like the British Army.
        new[] { "", "Pte", "Pte", "LCpl", "Cpl", "Sgt", "SSgt", "WO2", "WO1", "WO1", "2Lt", "Lt", "Capt", "Maj", "Lt Col", "Col", "Brig", "Maj Gen", "Lt Gen", "Gen" },
    };

    static readonly string[][] First =
    {
        new[] { "James", "Michael", "Robert", "David", "Daniel", "Joseph", "Matthew", "Anthony", "Joshua", "Andrew", "Kevin", "Brian", "Tyler", "Jacob",
                "Ryan", "Nathan", "Aaron", "Jose", "Luis", "Carlos", "Marcus", "Darnell", "Terrence", "Andre", "Kyle", "Cody", "Dustin", "Travis",
                "Sarah", "Jessica", "Ashley", "Maria", "Amanda", "Nicole", "Megan", "Tanya", "Keisha", "Rachel", "Lauren", "Brittany", "Ethan", "Logan",
                "Austin", "Brandon", "Derek", "Evan", "Garrett", "Hector", "Isaiah", "Jamal" },
        new[] { "Aleksandr", "Sergey", "Dmitriy", "Andrey", "Aleksey", "Maksim", "Ivan", "Mikhail", "Nikolay", "Pavel", "Artyom", "Denis", "Yevgeniy",
                "Vladimir", "Roman", "Igor", "Oleg", "Konstantin", "Anton", "Kirill", "Viktor", "Yuriy", "Ilya", "Vadim", "Ruslan", "Timur", "Bogdan",
                "Stanislav", "Gleb", "Fyodor", "Anna", "Yelena", "Olga", "Natalya", "Tatyana", "Irina", "Svetlana", "Mariya", "Yekaterina", "Darya",
                "Arkadiy", "Boris", "Grigoriy", "Leonid", "Matvey", "Nikita", "Pyotr", "Semyon", "Vasiliy", "Yaroslav" },
        new[] { "Oliver", "Jack", "Harry", "George", "Thomas", "James", "William", "Charlie", "Daniel", "Joshua", "Liam", "Callum", "Connor", "Jamie",
                "Ryan", "Lewis", "Kieran", "Craig", "Gareth", "Rhys", "Owen", "Dylan", "Euan", "Fraser", "Aidan", "Sean", "Declan", "Ross", "Ben",
                "Sam", "Emily", "Sophie", "Chloe", "Hannah", "Lucy", "Amy", "Rebecca", "Laura", "Kirsty", "Ffion", "Archie", "Alfie", "Freddie",
                "Kyle", "Liam", "Nathan", "Reece", "Scott", "Toby", "Will" },
    };

    static readonly string[][] Last =
    {
        new[] { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez", "Hernandez", "Lopez", "Wilson",
                "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark",
                "Ramirez", "Lewis", "Robinson", "Walker", "Young", "Allen", "King", "Wright", "Scott", "Torres", "Nguyen", "Hill", "Flores", "Green",
                "Adams", "Nelson", "Baker", "Hall", "Rivera", "Campbell", "Mitchell", "Carter", "Roberts", "Gomez", "Phillips", "Evans", "Turner",
                "Diaz", "Parker", "Cruz", "Edwards", "Collins", "Reyes", "Stewart", "Morris", "Morales", "Murphy", "Cook", "Rogers", "Gutierrez",
                "Ortiz", "Morgan", "Cooper", "Peterson", "Bailey", "Reed", "Kelly", "Howard", "Ramos", "Kim", "Cox", "Ward", "Richardson", "Watson",
                "Brooks", "Chavez", "Wood", "James", "Bennett", "Gray", "Mendoza", "Ruiz", "Hughes", "Price", "Alvarez", "Castillo", "Sanders",
                "Patel", "Myers", "Long", "Ross", "Foster", "Jimenez", "Powell" },
        new[] { "Ivanov", "Smirnov", "Kuznetsov", "Popov", "Vasilyev", "Petrov", "Sokolov", "Mikhaylov", "Novikov", "Fyodorov", "Morozov", "Volkov",
                "Alekseyev", "Lebedev", "Semyonov", "Yegorov", "Pavlov", "Kozlov", "Stepanov", "Nikolayev", "Orlov", "Andreyev", "Makarov", "Nikitin",
                "Zakharov", "Zaytsev", "Solovyov", "Borisov", "Yakovlev", "Grigoryev", "Romanov", "Vorobyov", "Sergeyev", "Kuzmin", "Frolov",
                "Aleksandrov", "Dmitriyev", "Korolyov", "Gusev", "Kiselyov", "Ilyin", "Maksimov", "Polyakov", "Sorokin", "Vinogradov", "Kovalyov",
                "Belov", "Medvedev", "Antonov", "Tarasov", "Zhukov", "Baranov", "Filippov", "Komarov", "Davydov", "Belyayev", "Gerasimov", "Bogdanov",
                "Osipov", "Sidorov", "Matveyev", "Titov", "Markov", "Mironov", "Krylov", "Kulikov", "Karpov", "Vlasov", "Melnikov", "Denisov",
                "Gavrilov", "Tikhonov", "Kazakov", "Afanasyev", "Danilov", "Savelyev", "Timofeyev", "Fomin", "Chernov", "Abramov", "Martynov",
                "Yefimov", "Fedotov", "Shcherbakov", "Nazarov", "Kalinin", "Isayev", "Chernyshov", "Bykov", "Maslov", "Rodionov", "Konovalov",
                "Lazarev", "Voronin", "Klimov", "Filatov", "Ponomaryov", "Golubev", "Kudryavtsev", "Prokhorov" },
        new[] { "Smith", "Jones", "Williams", "Taylor", "Brown", "Davies", "Evans", "Wilson", "Thomas", "Johnson", "Roberts", "Robinson", "Thompson",
                "Wright", "Walker", "White", "Edwards", "Hughes", "Green", "Hall", "Lewis", "Harris", "Clarke", "Patel", "Jackson", "Wood", "Turner",
                "Martin", "Cooper", "Hill", "Ward", "Morris", "Moore", "Clark", "Lee", "King", "Baker", "Harrison", "Morgan", "Allen", "James",
                "Scott", "Phillips", "Watson", "Davis", "Parker", "Price", "Bennett", "Young", "Griffiths", "Mitchell", "Kelly", "Cook", "Carter",
                "Richardson", "Bailey", "Collins", "Bell", "Shaw", "Murphy", "Miller", "Cox", "Richards", "Khan", "Marshall", "Anderson", "Simpson",
                "Ellis", "Adams", "Singh", "Begum", "Wilkinson", "Foster", "Chapman", "Powell", "Webb", "Rogers", "Gray", "Mason", "Ali", "Hunt",
                "Hussain", "Campbell", "Matthews", "Owen", "Palmer", "Holmes", "Mills", "Barnes", "Knight", "Lloyd", "Butler", "Russell", "Barker",
                "Fisher", "Stevens", "Jenkins", "Murray", "Dixon", "Harvey", "MacLeod" },
    };

    static uint Hash(int seed, int id)
    {
        uint h = (uint)seed * 0x9E3779B1u ^ (uint)id * 0x85EBCA77u;
        h ^= h >> 15; h *= 0x2C1B3C6Du;
        h ^= h >> 12; h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }

    public static string Name(int side, int seed, int id)
    {
        uint h = Hash(seed, id);
        var f = First[side];
        var l = Last[side];
        return $"{f[h % (uint)f.Length]} {l[(h >> 11) % (uint)l.Length]}";
    }

    public static string Title(int side, byte rank) => Titles[side][Math.Clamp(rank, (byte)0, (byte)19)];

    public static string Full(int side, int seed, int id, byte rank) => $"{Title(side, rank)} {Name(side, seed, id)}";

    /// <summary>
    /// How good a soldier is, 0.15..0.97, around 0.58: the same curve the battle maps' bots are rolled on (Personality).
    /// Drawn from the seed and number, so it never needs storing.
    /// </summary>
    public static float Skill(int seed, int id)
    {
        uint a = Hash(seed ^ 0x51CA, id), b = Hash(seed ^ 0x2B17, id);
        double u1 = (a + 1.0) / 4294967297.0, u2 = (b + 1.0) / 4294967297.0;
        double normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return (float)Math.Clamp(0.58 + 0.17 * normal, 0.15, 0.97);
    }
}
