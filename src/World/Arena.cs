using Godot;

namespace Ridgeline;

/// <summary>
/// A 200 x 200 m duel map built for testing bots. It's point-symmetric (the
/// east half is the west half rotated 180°), so neither team has the better side.
///
/// - Centre: a low-walled compound with houses, a warehouse, vehicles and crates — CQB.
/// - Flanks: open ground with ruins, rocks and sandbag nests — mid range.
/// - Spawns: behind a low berm at each end (Alpha west, Bravo east).
///
/// Every collider lives under the NavigationRegion3D so the navmesh is baked from it.
/// </summary>
public partial class Arena : Node3D, IGround
{
    public const float Half = 100f;
    static readonly Color Dirt = new(0.42f, 0.40f, 0.30f);

    public NavigationRegion3D Nav = null!;
    public readonly List<Vector3> InterestPoints = new();
    public readonly List<Vector3>[] Spawns = { new(), new() };

    bool _mirror;
    StandardMaterial3D _plaster = null!, _concrete = null!, _wood = null!, _sand = null!, _rock = null!,
                       _stone = null!, _truck = null!, _bark = null!, _leaves = null!, _roof = null!, _metal = null!;

    public float HeightAt(float x, float z) => 0f;
    public Vector3 NormalAt(float x, float z) => Vector3.Up;
    public Color ColorAt(float x, float z) => Dirt;

    public void Build()
    {
        _plaster = Mat(0.72f, 0.67f, 0.58f);
        _concrete = Mat(0.55f, 0.55f, 0.52f);
        _wood = Mat(0.45f, 0.33f, 0.2f);
        _sand = Mat(0.52f, 0.47f, 0.34f);
        _rock = Mat(0.46f, 0.45f, 0.43f);
        _stone = Mat(0.6f, 0.57f, 0.5f);
        _truck = Mat(0.3f, 0.35f, 0.27f);
        _bark = Mat(0.27f, 0.2f, 0.13f);
        _leaves = Mat(0.17f, 0.27f, 0.13f);
        _roof = Mat(0.35f, 0.25f, 0.2f);
        _metal = Mat(0.4f, 0.42f, 0.44f);

        var nm = new NavigationMesh
        {
            AgentRadius = 0.35f, AgentHeight = 1.75f, AgentMaxClimb = 0.25f, AgentMaxSlope = 40f,
            // Agent radius must be a whole number of cells or Recast rounds it up (0.35 -> 0.5 at 0.25 cells).
            CellSize = 0.175f, CellHeight = 0.25f,
            GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
            GeometryCollisionMask = 1,
        };
        Nav = new NavigationRegion3D { NavigationMesh = nm };
        AddChild(Nav);

        BuildGround();
        BuildPerimeter();
        BuildWarehouse();
        for (int side = 0; side < 2; side++)
        {
            _mirror = side == 1;
            BuildHalf();
        }
        _mirror = false;

        NavigationServer3D.MapSetCellSize(GetWorld3D().NavigationMap, 0.175f);
        Nav.BakeNavigationMesh(false);
    }

    static StandardMaterial3D Mat(float r, float g, float b) => new() { AlbedoColor = new Color(r, g, b), Roughness = 1f };

    // ---------- placement helpers (all coordinates are for the west half; the mirror flips them)

    Vector3 M(float x, float z) => _mirror ? new Vector3(-x, 0f, -z) : new Vector3(x, 0f, z);
    float MYaw(float yawDeg) => _mirror ? yawDeg + 180f : yawDeg;

    StaticBody3D Box(Vector3 center, Vector3 size, float yawDeg, Material mat)
    {
        var body = new StaticBody3D { CollisionLayer = 1 };
        Nav.AddChild(body);
        body.Position = center;
        body.RotationDegrees = new Vector3(0f, yawDeg, 0f);
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat });
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        return body;
    }

    /// <summary>A box sitting on the ground, at mirrored coordinates.</summary>
    void Prop(float x, float z, Vector3 size, float yawDeg, Material mat)
    {
        var p = M(x, z);
        Box(new Vector3(p.X, size.Y / 2f, p.Z), size, MYaw(yawDeg), mat);
    }

    record struct Opening(float At, float Width, float Bottom, float Top);

    static Opening Door(float at, float w = 1.4f) => new(at, w, 0f, 2.15f);
    static Opening Window(float at) => new(at, 1.2f, 1.0f, 1.9f);

    /// <summary>A straight wall from a to b (world XZ, already mirrored) with holes cut for doors and windows.</summary>
    void Wall(Vector3 a, Vector3 b, float h, float thick, Material mat, params Opening[] openings)
    {
        var d = b - a;
        d.Y = 0f;
        float len = d.Length();
        var dir = d / len;
        float yaw = Mathf.RadToDeg(MathF.Atan2(-dir.Z, dir.X));

        void Seg(float s0, float s1, float y0, float y1)
        {
            if (s1 - s0 < 0.02f || y1 - y0 < 0.02f) return;
            var mid = a + dir * ((s0 + s1) / 2f);
            Box(new Vector3(mid.X, (y0 + y1) / 2f, mid.Z), new Vector3(s1 - s0, y1 - y0, thick), yaw, mat);
        }

        float cursor = 0f;
        foreach (var o in openings.OrderBy(o => o.At))
        {
            float s0 = o.At - o.Width / 2f, s1 = o.At + o.Width / 2f;
            Seg(cursor, s0, 0f, h);
            Seg(s0, s1, 0f, o.Bottom);
            Seg(s0, s1, o.Top, h);
            cursor = s1;
        }
        Seg(cursor, len, 0f, h);
    }

    /// <summary>
    /// A single-storey building centred at (cx, cz), w along X and d along Z. Door
    /// and window sides are compass letters (N = -Z); openings are centred on their
    /// wall, so they survive mirroring.
    /// </summary>
    void House(float cx, float cz, float w, float d, string doors, string windows, bool partition = false)
    {
        const float H = 3f, T = 0.25f;
        var c = M(cx, cz);
        float x0 = c.X - w / 2f, x1 = c.X + w / 2f, z0 = c.Z - d / 2f, z1 = c.Z + d / 2f;

        // side is the design (un-mirrored) letter of the wall being built.
        Opening[] For(char side, float len)
        {
            var list = new List<Opening>();
            if (doors.Contains(side)) list.Add(Door(len / 2f));
            if (windows.Contains(side))
            {
                list.Add(Window(len * 0.22f));
                list.Add(Window(len * 0.78f));
            }
            return list.ToArray();
        }

        // Walls are laid out in world space; mirrored, the world-north wall is the design's south wall.
        char N = _mirror ? 'S' : 'N', S = _mirror ? 'N' : 'S', E = _mirror ? 'W' : 'E', W = _mirror ? 'E' : 'W';
        Wall(new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), H, T, _plaster, For(N, w));
        Wall(new Vector3(x0, 0, z1), new Vector3(x1, 0, z1), H, T, _plaster, For(S, w));
        Wall(new Vector3(x0, 0, z0 + T / 2f), new Vector3(x0, 0, z1 - T / 2f), H, T, _plaster, For(W, d - T));
        Wall(new Vector3(x1, 0, z0 + T / 2f), new Vector3(x1, 0, z1 - T / 2f), H, T, _plaster, For(E, d - T));
        if (partition)
            Wall(new Vector3(c.X, 0, z0 + T / 2f), new Vector3(c.X, 0, z1 - T / 2f), H, 0.2f, _plaster, Door((d - T) / 2f));
        Box(new Vector3(c.X, H + 0.1f, c.Z), new Vector3(w + 0.4f, 0.2f, d + 0.4f), 0f, _roof);
        InterestPoints.Add(c);
    }

    void Tree(float x, float z)
    {
        var p = M(x, z);
        Box(new Vector3(p.X, 3.5f, p.Z), new Vector3(0.45f, 7f, 0.45f), 0f, _bark);
        var crown = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 2.4f, Height = 6f, RadialSegments = 7 },
            MaterialOverride = _leaves,
        };
        AddChild(crown); // not under Nav: foliage isn't an obstacle
        crown.Position = new Vector3(p.X, 7.5f, p.Z);
    }

    // ---------- the map

    void BuildGround()
    {
        var noise = new NoiseTexture2D
        {
            Width = 512, Height = 512, Seamless = true,
            Noise = new FastNoiseLite { Frequency = 0.02f, FractalOctaves = 4 },
            ColorRamp = new Gradient { Offsets = new[] { 0f, 1f }, Colors = new[] { new Color(0.34f, 0.33f, 0.24f), new Color(0.5f, 0.47f, 0.35f) } },
        };
        var mat = new StandardMaterial3D { AlbedoTexture = noise, Roughness = 1f, Uv1Scale = new Vector3(24f, 24f, 24f) };
        Box(new Vector3(0f, -0.5f, 0f), new Vector3(2f * Half + 20f, 1f, 2f * Half + 20f), 0f, mat).AddToGroup("ground");
    }

    void BuildPerimeter()
    {
        float e = Half;
        Wall(new Vector3(-e, 0, -e), new Vector3(e, 0, -e), 4f, 1f, _concrete);
        Wall(new Vector3(-e, 0, e), new Vector3(e, 0, e), 4f, 1f, _concrete);
        Wall(new Vector3(-e, 0, -e), new Vector3(-e, 0, e), 4f, 1f, _concrete);
        Wall(new Vector3(e, 0, -e), new Vector3(e, 0, e), 4f, 1f, _concrete);
    }

    void BuildWarehouse()
    {
        const float w = 18f, d = 10f, H = 4.5f, T = 0.3f;
        float x0 = -w / 2f, x1 = w / 2f, z0 = -d / 2f, z1 = d / 2f;
        Wall(new Vector3(x0, 0, z0), new Vector3(x1, 0, z0), H, T, _metal, Window(4f), Door(9f, 1.6f), Window(14f));
        Wall(new Vector3(x0, 0, z1), new Vector3(x1, 0, z1), H, T, _metal, Window(4f), Door(9f, 1.6f), Window(14f));
        Wall(new Vector3(x0, 0, z0 + T / 2f), new Vector3(x0, 0, z1 - T / 2f), H, T, _metal, new Opening((d - T) / 2f, 3.2f, 0f, 3.2f));
        Wall(new Vector3(x1, 0, z0 + T / 2f), new Vector3(x1, 0, z1 - T / 2f), H, T, _metal, new Opening((d - T) / 2f, 3.2f, 0f, 3.2f));
        Box(new Vector3(0f, H + 0.1f, 0f), new Vector3(w + 0.6f, 0.2f, d + 0.6f), 0f, _roof);
        InterestPoints.Add(Vector3.Zero);
    }

    void BuildHalf()
    {
        // --- compound buildings
        House(-20f, -16f, 10f, 8f, doors: "ES", windows: "NW", partition: true);
        House(-25f, 17f, 6f, 6f, doors: "E", windows: "NS");

        // --- warehouse interior cover
        Prop(-4f, 2f, new Vector3(1.4f, 1.4f, 1.4f), 10f, _wood);
        Prop(-5.4f, 2.4f, new Vector3(1.2f, 1.2f, 1.2f), -8f, _wood);
        Prop(-6f, -2.8f, new Vector3(1.2f, 1.2f, 1.2f), 0f, _wood);

        // --- compound wall: low stone, 1.2 m — hides you crouched, lets you shoot standing
        const float cw = 1.2f, ct = 0.45f;
        var a = M(-34f, -30f); var b = M(-34f, 30f);
        Wall(a, b, cw, ct, _stone, new Opening(30f, 5f, 0f, cw), new Opening(50f, 3f, 0f, cw));
        Wall(M(-34f, -30f), M(-4f, -30f), cw, ct, _stone, new Opening(14f, 3f, 0f, cw));
        Wall(M(-34f, 30f), M(-4f, 30f), cw, ct, _stone);

        // --- vehicles and crates in the yard
        Prop(-12f, 7f, new Vector3(2.5f, 2.7f, 6.5f), 0f, _truck);
        Prop(-27f, -3f, new Vector3(1.9f, 1.5f, 4.4f), 20f, _truck);
        Prop(-8f, -22f, new Vector3(1.2f, 1.2f, 1.2f), 15f, _wood);
        Prop(-9.3f, -23.2f, new Vector3(1.2f, 1.2f, 1.2f), -5f, _wood);
        Prop(-30f, -25f, new Vector3(1.4f, 1.4f, 1.4f), 30f, _wood);
        Prop(-29f, 8f, new Vector3(1.2f, 2.4f, 1.2f), 0f, _wood);
        Prop(-15f, 25f, new Vector3(3f, 0.9f, 0.7f), 0f, _sand);
        Prop(-17f, 9f, new Vector3(0.7f, 1.0f, 0.7f), 0f, _metal);
        Prop(-17.9f, 9.6f, new Vector3(0.7f, 1.0f, 0.7f), 0f, _metal);
        InterestPoints.Add(M(-12f, -8f));
        InterestPoints.Add(M(-28f, -24f));
        InterestPoints.Add(M(-10f, 24f));
        InterestPoints.Add(M(0f, -30f));

        // --- ruins on open ground
        Wall(M(-60f, -28f), M(-50f, -28f), 2.4f, 0.35f, _stone, Window(5f));
        Wall(M(-60f, -28f), M(-60f, -18f), 2.4f, 0.35f, _stone, Door(6.5f));
        Wall(M(-60f, -18f), M(-55f, -18f), 1.3f, 0.35f, _stone);
        Wall(M(-64f, 22f), M(-56f, 22f), 2.1f, 0.35f, _stone, Window(4f));
        Wall(M(-56f, 22f), M(-56f, 27f), 1.3f, 0.35f, _stone);
        InterestPoints.Add(M(-56f, -23f));
        InterestPoints.Add(M(-60f, 25f));

        // --- sandbag nests
        Prop(-72f, 2.5f, new Vector3(2.4f, 0.95f, 0.7f), 0f, _sand);
        Prop(-70.5f, 4.8f, new Vector3(2.4f, 0.95f, 0.7f), 60f, _sand);
        Prop(-70.5f, 0.2f, new Vector3(2.4f, 0.95f, 0.7f), -60f, _sand);
        Prop(-48f, 42f, new Vector3(3f, 0.95f, 0.7f), 20f, _sand);
        Prop(-46.5f, 44f, new Vector3(2.2f, 0.95f, 0.7f), 80f, _sand);
        InterestPoints.Add(M(-73f, 2.5f));
        InterestPoints.Add(M(-49f, 44f));

        // --- rocks
        Prop(-45f, -45f, new Vector3(3f, 1.6f, 2.4f), 30f, _rock);
        Prop(-80f, -40f, new Vector3(2.5f, 1.3f, 3f), 70f, _rock);
        Prop(-38f, 48f, new Vector3(2f, 1.1f, 2f), 10f, _rock);
        Prop(-86f, 30f, new Vector3(3f, 1.8f, 2f), -20f, _rock);
        Prop(-42f, 10f, new Vector3(2.2f, 1.2f, 1.6f), 45f, _rock);
        InterestPoints.Add(M(-44f, -43f));
        InterestPoints.Add(M(-42f, 12f));

        // --- trees
        foreach (var (x, z) in new[] { (-44f, -12f), (-50f, 10f), (-66f, -8f), (-70f, -30f), (-40f, 22f), (-92f, -12f), (-88f, 44f), (-60f, 48f), (-75f, -52f), (-52f, -50f) })
            Tree(x, z);

        // --- spawn berm
        Wall(M(-82f, -18f), M(-82f, 18f), 1.0f, 0.8f, _sand, new Opening(12f, 2.5f, 0f, 1f), new Opening(24f, 2.5f, 0f, 1f));

        // Open ground and spawn areas are places to check too, or a camper at spawn is never found.
        foreach (var (x, z) in new[] { (-88f, 0f), (-88f, -24f), (-88f, 24f), (-70f, -60f), (-70f, 62f), (-30f, -70f), (-30f, 72f), (-66f, -20f), (-72f, 30f) })
            InterestPoints.Add(M(x, z));

        var spawns = Spawns[_mirror ? 1 : 0];
        foreach (float z in new[] { -16f, -9f, -3f, 3f, 9f, 16f, -22f, 22f })
            spawns.Add(M(-91f, z));
        foreach (float z in new[] { -12f, -4f, 4f, 12f })
            spawns.Add(M(-95f, z)); // 12 per side for the big fights
    }
}
