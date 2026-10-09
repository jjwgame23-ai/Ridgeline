using Godot;

namespace Ridgeline;

/// <summary>
/// A playable window onto a Conquest island: the battlefield round a point, built from the island's data instead of
/// made up (Valley.Build). The ground, water, roads, woods and fields come from WindowGround. The island's towns stand
/// where they are: those of a thousand people or more as towns of streets and blocks (City, up to 700 m across for
/// now), smaller villages and hamlets as villages, and its farms as farmsteads. Each is a place to fight over (a Site).
/// The window's origin is its middle, so positions stay small: Godot works in single precision, and 50 km from an
/// origin a position comes in 4 mm steps.
/// </summary>
public partial class Valley
{
    /// <summary>The island ground the map was made from, when it's a Conquest window.</summary>
    public WindowGround? Window;
    /// <summary>Every town of streets on the map (a window can hold several); City is the biggest.</summary>
    public readonly List<City> Cities = new();
    int _bridges, _farms;
    /// <summary>Bump when the window's ground changes, so navmesh tiles cached from an older one aren't used.</summary>
    const int WindowVersion = 2;

    /// <summary>Build the window of <paramref name="size"/> metres round (cx, cz) on the island (metres east and south of its centre).</summary>
    public void BuildWindow(Island isl, float cx, float cz, float size, int seed)
    {
        ulong t0 = Time.GetTicksMsec();
        Spec = new MapSpec("window", isl.Name, size, Biome.Temperate, "");
        Current = this;
        Seed = seed;
        Rooms.Clear();
        Door.Clear();
        Builder.Doorways.Clear();
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        float half = size / 2f;
        var g = Window = new WindowGround(isl, cx, cz, half);
        Terrain = new Terrain
        {
            RangeLane = false, Size = size, Biome = Biome.Temperate, SpacingOverride = 5f,
            Source = g.Height, Finish = g.Finish, ColorSource = g.ColorAt, TreesPerHa = g.TreesPerHa, MostPerHa = WindowGround.MostPerHa,
        };

        // The island's towns, biggest first, each where it stands.
        foreach (var t in isl.Towns.OrderByDescending(t => t.Population))
        {
            var at = new Vector2(t.X - cx, t.Z - cz);
            float room = half - 60f - MathF.Max(MathF.Abs(at.X), MathF.Abs(at.Y));
            if (room < 40f) continue;
            if (Cities.Any(c => c.Center.DistanceTo(at) < c.Radius + 80f)) continue;
            float r = MathF.Min(Math.Clamp(t.RadiusM, 150f, 700f), room - 40f);
            if (t.Population >= 1000 && r >= 120f)
            {
                var city = City.Plan(at, r, CityStyle.European, Math.Clamp(t.Population / 1500, 2, 8), rng);
                Cities.Add(city);
                Terrain.Flats.Add((at, r * 1.05f + 10f, 0f));
                foreach (var (name, p) in city.Districts)
                    Sites.Add(new Site { Name = $"{t.Name}: {name}", Kind = "District", Center = new Vector3(p.X, 0f, p.Y), Radius = 42f });
            }
            else Sites.Add(new Site { Name = t.Name, Kind = t.Population >= 150 ? "Village" : "Compound", Center = new Vector3(at.X, 0f, at.Y), Radius = t.Population >= 150 ? 36f : 30f });
        }
        // Farmsteads, clear of the towns and each other.
        foreach (var (fx, fz) in isl.Farms)
        {
            var at = new Vector2(fx - cx, fz - cz);
            if (MathF.Max(MathF.Abs(at.X), MathF.Abs(at.Y)) > half - 120f) continue;
            if (Cities.Any(c => c.Center.DistanceTo(at) < c.Radius + 120f)) continue;
            if (Sites.Any(s => new Vector2(s.Center.X, s.Center.Z).DistanceTo(at) < 150f)) continue;
            if (g.RoadNear(at.X, at.Y, out float rd, out float rw, out _) && rd < rw / 2f + 30f) continue;
            if (g.RiverNear(at.X, at.Y, out float vd, out float vw, out _) && vd < vw / 2f + 40f) continue;
            _farms++;
            Sites.Add(new Site { Name = $"Farm {_farms}", Kind = "Compound", Center = new Vector3(at.X, 0f, at.Y), Radius = 30f });
        }
        foreach (var s in Sites)
            if (s.Kind != "District") Terrain.Flats.Add((new Vector2(s.Center.X, s.Center.Z), s.Radius + 6f, 0f));
        City = Cities.OrderByDescending(c => c.Radius).FirstOrDefault();
        var cities = Cities.ToArray();
        Terrain.Zone = (x, z) =>
        {
            foreach (var c in cities)
            {
                int zc = c.Zone(x, z);
                if (zc != 0) return zc;
            }
            return 0;
        };
        Terrain.Paint = (x, z) =>
        {
            foreach (var c in cities)
                if (c.Paint(x, z) is Color p) return p;
            return g.RoadPaint(x, z);
        };
        AddChild(Terrain);
        Terrain.Generate(seed, plant: false);

        var batch = Batcher.Current = new Batcher(this);
        foreach (var c in Cities) c.Build(this, this, rng);
        foreach (var s in Sites)
        {
            s.Center.Y = Terrain.HeightAt(s.Center.X, s.Center.Z);
            if (s.Kind == "District")
            {
                var city = Cities.First(c => c.Districts.Any(d => s.Name.EndsWith(": " + d.Name) && new Vector2(s.Center.X, s.Center.Z).DistanceTo(d.At) < 1f));
                int d = city.Districts.FindIndex(x => s.Name.EndsWith(": " + x.Name));
                s.Points.AddRange(city.PointsOf(d));
                s.Perches.AddRange(city.PerchesOf(d));
                continue;
            }
            var b = new Builder(this, s.Center, rng.RandfRange(0f, 360f));
            if (s.Kind == "Compound") Settlements.Compound(b, rng);
            else Settlements.Village(b, rng);
            s.Points.AddRange(b.Points);
            s.Perches.AddRange(b.Perches);
            Perches.AddRange(b.Perches);
        }
        foreach (var c in Cities) Perches.AddRange(c.Perches);
        BuildBridges(g);
        batch.Flush();
        Batcher.Current = null;
        var occupied = Occupied = batch.Occupied;
        var cityZone = Terrain.Zone;
        Terrain.Zone = (x, z) =>
        {
            int zc = cityZone(x, z);
            if (zc != 0) return zc;
            return occupied.Contains(((int)MathF.Floor(x / 4f), (int)MathF.Floor(z / 4f))) ? 2 : 0;
        };
        Terrain.Plant(seed);
        BuildWater(g, size);
        BuildSeconds = (Time.GetTicksMsec() - t0) / 1000.0;
        GD.Print($"[window] {isl.Name} at ({cx / 1000f:0.0}, {cz / 1000f:0.0}) km, {size:0} m: {Cities.Count} towns ({Cities.Sum(c => c.Buildings)} buildings), "
                 + $"{Sites.Count(s => s.Kind == "Village")} villages, {_farms} farms, {g.Rivers.Count} streams, {g.Roads.Count} roads, {_bridges} bridges, "
                 + $"{Terrain.TreeCount} trees, {Perches.Count} perches, {Rooms.Count} rooms; built in {BuildSeconds:0.0}s");
        foreach (var br in g.Bridges) GD.Print($"[window] bridge at ({br.At.X:0}, {br.At.Y:0}), {br.Span:0} m span, deck {br.Y:0.0} m");
        foreach (var rv in g.Rivers) GD.Print($"[window] stream of {rv.P.Length} points, {rv.W.Min():0.0}-{rv.W.Max():0.0} m wide, from ({rv.P[0].X:0}, {rv.P[0].Y:0}) at {rv.H[0]:0} m to ({rv.P[^1].X:0}, {rv.P[^1].Y:0}) at {rv.H[^1]:0} m");

        // The navmesh, with water too deep to wade struck out: for people over 1.2 m (chest-deep with kit), for vehicles
        // over 0.9 m (most trucks ford 0.75 m, tanks about 1.2 m unprepared).
        float extent = half - 40f, tile = 252f;
        string key = $"win{WindowVersion}_{isl.Seed}_{(int)cx}_{(int)cz}_{(int)size}";
        Nav = new NavBaker { Root = this, Extent = extent, Tile = tile, Key = key, DenseTile = 63f, Links = Builder.Doorways.ToList() };
        Bar(Nav, g, 1.2f);
        VehicleMap = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(VehicleMap, true);
        NavigationServer3D.MapSetUp(VehicleMap, Vector3.Up);
        VehicleNav = new NavBaker { Root = this, Extent = extent, Tile = tile, Key = $"{key}veh", Map = VehicleMap, Make = NavBaker.VehicleSettings, DenseTile = 126f };
        Bar(VehicleNav, g, 0.9f);
        foreach (var c in Cities)
        {
            var r = new Rect2(c.Center - Vector2.One * c.Radius * 1.12f, Vector2.One * c.Radius * 2.24f);
            Nav.DenseAreas.Add(r);
            VehicleNav.DenseAreas.Add(r);
        }
        AddChild(Nav);
        Nav.Start();
        AddChild(VehicleNav);
        VehicleNav.Start();
    }

    /// <summary>Road bridges over the rivers: a deck at the road's height, with a parapet each side.</summary>
    void BuildBridges(WindowGround g)
    {
        var deck = new StandardMaterial3D { AlbedoColor = new Color(0.45f, 0.44f, 0.42f), Roughness = 0.95f };
        var b = new Builder(this, Vector3.Zero, 0f);
        foreach (var (at, dir, span, width, y) in g.Bridges)
        {
            float yaw = Mathf.RadToDeg(MathF.Atan2(dir.X, dir.Y));
            var side = new Vector3(dir.Y, 0f, -dir.X);
            var mid = new Vector3(at.X, y - 0.4f, at.Y);
            b.Box(mid, new Vector3(width, 0.8f, span), yaw, deck, floor: true);
            foreach (float s in new[] { -1f, 1f })
                b.Box(mid + side * (width / 2f - 0.15f) * s + Vector3.Up * 0.9f, new Vector3(0.3f, 1f, span), yaw, deck);
            _bridges++;
        }
    }

    /// <summary>The sea at 0 m, the lakes at their levels, and the rivers as ribbons on their surfaces.</summary>
    void BuildWater(WindowGround g, float size)
    {
        var water = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.1f, 0.2f, 0.24f, 0.82f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.08f, Metallic = 0.1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            st.SetNormal(Vector3.Up);
            st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
            st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
        }
        float h = size / 2f;
        bool sea = false, lakes = false;
        var isl = g.Isl;
        for (float x = -h; x < h; x += isl.Cell)
        for (float z = -h; z < h; z += isl.Cell)
        {
            int c = g.CellAt(x + isl.Cell / 2f, z + isl.Cell / 2f);
            if (c >= 0 && isl.Land[c] == Cover.Lake && !float.IsNaN(isl.LakeLevel[c]))
            {
                float y = isl.LakeLevel[c], e = 2f;
                Quad(new(x - e, y, z - e), new(x + isl.Cell + e, y, z - e), new(x + isl.Cell + e, y, z + isl.Cell + e), new(x - e, y, z + isl.Cell + e));
                lakes = true;
            }
            if (c < 0 || isl.Sea(c) || isl.Height[c] < 5f) sea = true;
        }
        if (sea) Quad(new(-h, 0f, -h), new(h, 0f, -h), new(h, 0f, h), new(-h, 0f, h));
        foreach (var r in g.Rivers)
            for (int k = 0; k + 1 < r.P.Length; k++)
            {
                Vector3 Edge(int i, float s)
                {
                    var dir = r.P[Math.Min(i + 1, r.P.Length - 1)] - r.P[Math.Max(i - 1, 0)];
                    var n = new Vector2(-dir.Y, dir.X).Normalized();
                    var p = r.P[i] + n * (r.W[i] / 2f + 0.3f) * s;
                    return new Vector3(p.X, r.H[i], p.Y);
                }
                Quad(Edge(k, -1f), Edge(k, 1f), Edge(k + 1, 1f), Edge(k + 1, -1f));
            }
        if (!sea && !lakes && g.Rivers.Count == 0) return;
        AddChild(new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = water, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }

    /// <summary>Strike water deeper than <paramref name="deep"/> out of a navmesh, in runs of 8 m cells along each row.</summary>
    void Bar(NavBaker nav, WindowGround g, float deep)
    {
        const float Cell = 8f;
        float e = nav.Extent + 8f;
        int n = (int)MathF.Ceiling(2f * e / Cell);
        for (int j = 0; j < n; j++)
        {
            float z = -e + (j + 0.5f) * Cell;
            int start = -1;
            float top = 0f;
            for (int i = 0; i <= n; i++)
            {
                float x = -e + (i + 0.5f) * Cell;
                float w = i < n ? g.WaterAt(x, z) : float.NaN;
                bool wet = !float.IsNaN(w) && w - Terrain.HeightAt(x, z) > deep && !OnBridge(g, x, z);
                if (wet && start < 0) { start = i; top = w; }
                else if (wet) top = MathF.Min(top, w);
                if (wet || start < 0) continue;
                float x0 = -e + start * Cell, x1 = -e + i * Cell, z0 = z - Cell / 2f, z1 = z + Cell / 2f;
                nav.Barred.Add((new[] { new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), new Vector3(x0, 0f, z1) }, top - 0.3f));
                start = -1;
            }
        }
    }

    static bool OnBridge(WindowGround g, float x, float z)
    {
        var p = new Vector2(x, z);
        foreach (var (at, dir, span, width, _) in g.Bridges)
        {
            var d = p - at;
            if (MathF.Abs(d.Dot(dir)) < span / 2f + 4f && MathF.Abs(d.X * dir.Y - d.Y * dir.X) < width / 2f + 4f) return true;
        }
        return false;
    }
}
