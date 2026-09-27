using Godot;

namespace Ridgeline;

/// <summary>A place on the battlefield: a village, compound, outpost or town district that a point can sit on.</summary>
public sealed class Site
{
    public string Name = "";
    public string Kind = "";
    public Vector3 Center;
    public float Radius;
    public readonly List<Vector3> Points = new();
    /// <summary>Windows and rooftops in and around it (towns): where defenders fight from.</summary>
    public readonly List<Perch> Perches = new();
    public bool Urban => Kind == "District";
}

/// <summary>One of the battlefields: its size, its biome, and the blurb for the menu.</summary>
public sealed record MapSpec(string Id, string Name, float Size, Biome Biome, string Blurb)
{
    public static readonly MapSpec[] All =
    {
        new("valley", "Valley", 2048f, Biome.Temperate, "2 km. Rolling wooded hills and scattered villages."),
        new("kessel", "Kessel Forest", 3072f, Biome.Forest, "3 km. Dense mixed forest, steep hills, one big summit."),
        new("novigrad", "Novigrad", 3072f, Biome.Urban, "3 km. A city of apartment blocks and parks, wooded hills around it."),
        new("alhamra", "Al Hamra", 5120f, Biome.Desert, "5 km. A walled desert city, outlying compounds, mesas and an oasis."),
        new("highlands", "Highlands", 5120f, Biome.Highlands, "5 km. Big open hills and ridges, pine woods, many hamlets."),
    };

    public static MapSpec Get(string id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];
}

/// <summary>
/// A battlefield, built from its MapSpec: terrain of the biome, settlements (villages,
/// compounds, outposts; a city of districts on the urban and desert maps) through the
/// middle, and three faction bases on a big triangle round the edge (Alpha north,
/// Bravo south-east, Charlie south-west).
/// </summary>
public partial class Valley : Node3D, IGround
{
    public MapSpec Spec = MapSpec.All[0];
    public float Size => Spec.Size;
    /// <summary>Half the map's width: the edge is a wall at about ±Half.</summary>
    public float Half => Spec.Size / 2f;
    /// <summary>How much bigger than the original 2 km valley: distances that were tuned there scale by this.</summary>
    public float SizeScale => Spec.Size / 2048f;
    public float BaseRadius => Spec.Size * 0.4f;
    /// <summary>The map being played (there's only ever one).</summary>
    public static Valley? Current;
    public static float PlayHalf => Current?.Half ?? 1024f;

    public Terrain Terrain = null!;
    public NavBaker Nav = null!, VehicleNav = null!;
    /// <summary>The navigation map vehicles path on (wider clearance than people).</summary>
    public static Rid VehicleMap;
    public readonly List<Site> Sites = new();
    public readonly Vector3[] Bases = new Vector3[3];
    public City? City;
    /// <summary>Every window and rooftop worth fighting from, map-wide (overwatch posts in town).</summary>
    public readonly List<Perch> Perches = new();
    public int Seed;
    public double BuildSeconds;
    /// <summary>4 m cells with something built on them.</summary>
    public HashSet<(int, int)> Occupied = new();

    /// <summary>How many built-on 4 m cells in the 20 m square round a point: a village street scores ~5+.</summary>
    public int BuiltAround(float x, float z, int r = 2)
    {
        int cx = (int)MathF.Floor(x / 4f), cz = (int)MathF.Floor(z / 4f), n = 0;
        for (int i = -r; i <= r; i++)
        for (int j = -r; j <= r; j++)
            if (Occupied.Contains((cx + i, cz + j))) n++;
        return n;
    }

    public float HeightAt(float x, float z) => Terrain.HeightAt(x, z);
    public Vector3 NormalAt(float x, float z) => Terrain.NormalAt(x, z);
    public Color ColorAt(float x, float z) => Terrain.ColorAt(x, z);

    public static readonly Color[] TeamColors = { new(0.22f, 0.32f, 0.58f), new(0.58f, 0.2f, 0.16f), new(0.86f, 0.74f, 0.12f) };

    public void Build(int seed, MapSpec? spec = null)
    {
        ulong t0 = Time.GetTicksMsec();
        Spec = spec ?? Spec;
        Current = this;
        Seed = seed;
        Rooms.Clear();
        Door.Clear();
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        float k = SizeScale;

        for (int i = 0; i < 3; i++)
        {
            float a = Mathf.DegToRad(-90f + i * 120f);
            Bases[i] = new Vector3(MathF.Cos(a) * BaseRadius, 0f, MathF.Sin(a) * BaseRadius);
        }

        Terrain = new Terrain { RangeLane = false, Size = Size, Biome = Spec.Biome };
        switch (Spec.Biome)
        {
            case Biome.Temperate:
                Terrain.HillScale = 0.55f;
                PlaceSites(rng, new[] { "Compound", "Village", "Village", "Outpost", "Village", "Compound", "Outpost", "Village", "Village" }, 680f * k, 230f, 380f * k);
                break;
            case Biome.Forest:
            {
                Terrain.HillScale = 0.8f;
                // The big hill: off-centre, with an outpost on the summit.
                var hill = RandomSpot(rng, 250f, 600f);
                Terrain.BigHills.Add((hill, 420f, 120f));
                AddSite(rng, "Outpost", new Vector3(hill.X, 0f, hill.Y), "Summit");
                PlaceSites(rng, new[] { "Village", "Compound", "Village", "Outpost", "Village", "Compound", "Village", "Village", "Outpost", "Village" }, 680f * k, 260f, 380f * k);
                break;
            }
            case Biome.Highlands:
            {
                Terrain.HillScale = 1.1f;
                for (int tries = 0; tries < 500 && Terrain.BigHills.Count < 3; tries++)
                {
                    var hill = RandomSpot(rng, 400f, 1700f);
                    if (Terrain.BigHills.Any(b => b.C.DistanceTo(hill) < 900f) || Bases.Any(b => new Vector2(b.X, b.Z).DistanceTo(hill) < 700f)) continue;
                    Terrain.BigHills.Add((hill, rng.RandfRange(450f, 650f), rng.RandfRange(120f, 200f)));
                    AddSite(rng, "Outpost", new Vector3(hill.X, 0f, hill.Y));
                }
                PlaceSites(rng, Enumerable.Range(0, 15).Select(i => i % 4 == 1 ? "Compound" : i % 5 == 3 ? "Outpost" : "Village").ToArray(), 1750f, 330f, 650f);
                break;
            }
            case Biome.Urban:
            {
                // The city fills the middle; the land rises into wooded hills round it, one of them big.
                City = City.Plan(Vector2.Zero, 560f, CityStyle.European, 6, rng);
                Terrain.Flats.Add((Vector2.Zero, 600f, 0f));
                // Between two bases, so it's contested ground and not someone's back yard.
                var hillDir = Mathf.DegToRad(-90f + 60f + 120f * rng.RandiRange(0, 2));
                var hill = new Vector2(MathF.Cos(hillDir), MathF.Sin(hillDir)) * 1000f;
                Terrain.BigHills.Add((hill, 380f, 110f));
                AddCity();
                AddSite(rng, "Outpost", new Vector3(hill.X, 0f, hill.Y), "Signal Hill");
                PlaceSites(rng, new[] { "Village", "Compound", "Village" }, 1250f, 300f, 420f, minR: 800f);
                break;
            }
            case Biome.Desert:
            {
                City = City.Plan(Vector2.Zero, 640f, CityStyle.Desert, 6, rng);
                Terrain.Flats.Add((Vector2.Zero, 690f, 0f));
                AddCity();
                // An oasis with its village between two bases.
                float oa = Mathf.DegToRad(-90f + 60f + 120f * rng.RandiRange(0, 2));
                var oasis = new Vector2(MathF.Cos(oa), MathF.Sin(oa)) * rng.RandfRange(1300f, 1600f);
                Terrain.Oasis = (oasis, 90f);
                AddSite(rng, "DesertVillage", new Vector3(oasis.X + 130f, 0f, oasis.Y), "Ain Dhahab");
                // Mesas out in the sand; outposts on two of them.
                int mesas = 0;
                for (int tries = 0; tries < 400 && mesas < 6; tries++)
                {
                    var m = RandomSpot(rng, 1000f, 2200f);
                    if (Terrain.Mesas.Any(o => o.C.DistanceTo(m) < 600f) || Bases.Any(b => new Vector2(b.X, b.Z).DistanceTo(m) < 500f) || m.DistanceTo(oasis) < 400f) continue;
                    float r = rng.RandfRange(90f, 160f);
                    Terrain.Mesas.Add((m, r, rng.RandfRange(30f, 55f)));
                    if (mesas < 2) AddSite(rng, "Outpost", new Vector3(m.X, 0f, m.Y), mesas == 0 ? "Jebel Ahmar" : "Jebel Aswad");
                    mesas++;
                }
                PlaceSites(rng, new[] { "DesertCompound", "DesertVillage", "DesertCompound", "DesertVillage", "Outpost", "DesertCompound" }, 1900f, 420f, 600f, minR: 900f);
                break;
            }
        }

        foreach (var s in Sites)
            if (s.Kind != "District") Terrain.Flats.Add((new Vector2(s.Center.X, s.Center.Z), s.Radius + 6f, 0f));
        foreach (var b in Bases) Terrain.Flats.Add((new Vector2(b.X, b.Z), 45f, 0f));
        if (City != null)
        {
            Terrain.Zone = City.Zone;
            Terrain.Paint = City.Paint;
        }
        AddChild(Terrain);
        Terrain.Generate(seed, plant: false);

        var batch = Batcher.Current = new Batcher(this);
        City?.Build(this, this, rng);
        for (int i = 0; i < Sites.Count; i++)
        {
            var s = Sites[i];
            s.Center.Y = Terrain.HeightAt(s.Center.X, s.Center.Z);
            if (s.Kind == "District")
            {
                int d = City!.Districts.FindIndex(x => x.Name == s.Name);
                s.Points.AddRange(City.PointsOf(d));
                s.Perches.AddRange(City.PerchesOf(d));
                continue;
            }
            var b = new Builder(this, s.Center, rng.RandfRange(0f, 360f));
            switch (s.Kind)
            {
                case "Compound": Settlements.Compound(b, rng); break;
                case "DesertCompound": Settlements.Compound(b, rng, desert: true); break;
                case "Outpost": Settlements.Outpost(b, rng); break;
                case "DesertVillage": Settlements.DesertVillage(b, rng); break;
                default: Settlements.Village(b, rng); break;
            }
            s.Points.AddRange(b.Points);
            s.Perches.AddRange(b.Perches);
            Perches.AddRange(b.Perches);
        }
        if (City != null) Perches.AddRange(City.Perches);
        for (int i = 0; i < 3; i++)
        {
            Bases[i].Y = Terrain.HeightAt(Bases[i].X, Bases[i].Z);
            // Face the middle of the map.
            float yaw = Mathf.RadToDeg(MathF.Atan2(Bases[i].X, Bases[i].Z));
            Settlements.Base(new Builder(this, Bases[i], yaw), TeamColors[i]);
        }
        batch.Flush();
        Batcher.Current = null;
        var occupied = Occupied = batch.Occupied;
        var cityZone = Terrain.Zone;
        Terrain.Zone = (x, z) =>
        {
            int zc = cityZone?.Invoke(x, z) ?? 0;
            if (zc != 0) return zc;
            return occupied.Contains(((int)MathF.Floor(x / 4f), (int)MathF.Floor(z / 4f))) ? 2 : 0;
        };
        Terrain.Plant(seed);
        BuildSeconds = (Time.GetTicksMsec() - t0) / 1000.0;
        GD.Print($"[map] {Spec.Name} {Size:0} m {Spec.Biome}: {Sites.Count} sites, {batch.Boxes} boxes ({batch.Shapes} solid), {Terrain.TreeCount} trees, {Perches.Count} perches, {Rooms.Count} rooms, {Door.All.Count} doors"
                 + (City != null ? $", city {City.Buildings} buildings ({City.Enterable} enterable), {City.Blocks.Count} blocks" : "") + $", built in {BuildSeconds:0.0}s");

        // The navmesh: tiles over the whole playable square. Bigger maps get bigger tiles.
        // Tile sizes are whole multiples of NavBaker.Grid (see there): 252 = 72 × 3.5, towns in quarters of that.
        float extent = Half - 40f, tile = 252f;
        string key = $"{Spec.Id}{seed}";
        Rect2? dense = City != null ? new Rect2(City.Center - Vector2.One * City.Radius * 1.12f, Vector2.One * City.Radius * 2.24f) : null;
        Nav = new NavBaker { Root = this, Extent = extent, Tile = tile, Key = key, Dense = dense, DenseTile = 63f };
        AddChild(Nav);
        Nav.Start();

        // A second map for vehicles, baked from the same geometry.
        VehicleMap = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(VehicleMap, true);
        NavigationServer3D.MapSetUp(VehicleMap, Vector3.Up);
        VehicleNav = new NavBaker { Root = this, Extent = extent, Tile = tile, Key = $"{key}veh2", Map = VehicleMap, Make = NavBaker.VehicleSettings, Dense = dense, DenseTile = 126f };
        AddChild(VehicleNav);
        VehicleNav.Start();
    }

    Vector2 RandomSpot(RandomNumberGenerator rng, float minR, float maxR)
    {
        float a = rng.Randf() * Mathf.Tau, r = rng.RandfRange(minR, maxR);
        return new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
    }

    readonly HashSet<string> _usedNames = new();

    string NextName(RandomNumberGenerator rng, bool desert)
    {
        var pool = (desert ? Settlements.DesertNames : Settlements.Names).Where(n => !_usedNames.Contains(n)).ToList();
        string name = pool.Count > 0 ? pool[rng.RandiRange(0, pool.Count - 1)] : $"Site {Sites.Count + 1}";
        _usedNames.Add(name);
        return name;
    }

    static float RadiusOf(string kind) => kind switch { "Outpost" => 22f, "Compound" or "DesertCompound" => 30f, "DesertVillage" => 44f, _ => 36f };

    void AddSite(RandomNumberGenerator rng, string kind, Vector3 at, string? name = null)
    {
        name ??= NextName(rng, Spec.Biome == Biome.Desert);
        _usedNames.Add(name);
        Sites.Add(new Site { Name = name, Kind = kind, Center = at, Radius = RadiusOf(kind) });
    }

    /// <summary>The city's districts become capture points.</summary>
    void AddCity()
    {
        foreach (var (name, at) in City!.Districts)
        {
            _usedNames.Add(name);
            Sites.Add(new Site { Name = name, Kind = "District", Center = new Vector3(at.X, 0f, at.Y), Radius = 42f });
        }
    }

    /// <summary>Settlements spread through the map, apart from each other, the bases, the city, and hills' crests.</summary>
    void PlaceSites(RandomNumberGenerator rng, string[] kinds, float maxR, float spacing, float baseClear, float minR = 0f)
    {
        int placed = 0;
        for (int tries = 0; tries < 6000 && placed < kinds.Length; tries++)
        {
            float a = rng.Randf() * Mathf.Tau;
            float r = Sites.Count == 0 && minR == 0f ? rng.RandfRange(0f, 120f) : minR + MathF.Sqrt(rng.Randf()) * (maxR - minR);
            var p = new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            if (MathF.Abs(p.X) > Half - 150f || MathF.Abs(p.Z) > Half - 150f) continue;
            if (Sites.Any(s => s.Center.DistanceTo(p) < spacing)) continue;
            if (Bases.Any(b => b.DistanceTo(p) < baseClear)) continue;
            if (City != null && new Vector2(p.X, p.Z).DistanceTo(City.Center) < City.Radius + 250f) continue;
            var p2 = new Vector2(p.X, p.Z);
            if (Terrain.Mesas.Any(m => m.C.DistanceTo(p2) < m.R + 150f)) continue;
            if (Terrain.Oasis is { } o && o.C.DistanceTo(p2) < 350f) continue;
            AddSite(rng, kinds[placed], p);
            placed++;
        }
    }

    public override void _ExitTree()
    {
        if (VehicleMap.IsValid) { NavigationServer3D.FreeRid(VehicleMap); VehicleMap = default; }
        if (Current == this) Current = null;
    }

    /// <summary>The nearest point on the people's navmesh (see NavBaker.ClosestPoint), on any map.</summary>
    public static Vector3 ClosestOnFoot(World3D world, Vector3 p) =>
        Current is { } v && v.Nav.Finished ? v.Nav.ClosestPoint(p) : NavBaker.MapClosest(world.NavigationMap, p);

    /// <summary>The nearest point on the vehicles' navmesh.</summary>
    public static Vector3 ClosestForVehicles(Vector3 p) =>
        Current is { } v && v.VehicleNav.Finished ? v.VehicleNav.ClosestPoint(p) : NavBaker.MapClosest(VehicleMap, p);

    /// <summary>Snap a point to walkable ground if the navmesh is ready, else to the terrain surface.</summary>
    public Vector3 Ground(Vector3 p)
    {
        var map = GetWorld3D().NavigationMap;
        if (Nav.Finished && NavigationServer3D.MapGetIterationId(map) > 0)
        {
            var q = Nav.ClosestPoint(p with { Y = HeightAt(p.X, p.Z) });
            // The navmesh follows the ground only to within a metre: never hand back a point under it.
            if (q.DistanceTo(p with { Y = q.Y }) < 6f) return q with { Y = MathF.Max(q.Y, HeightAt(q.X, q.Z)) };
        }
        return p with { Y = HeightAt(p.X, p.Z) };
    }
}
