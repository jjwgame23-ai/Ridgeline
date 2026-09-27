using Godot;

namespace Ridgeline;

public enum Biome { Temperate, Forest, Desert, Urban, Highlands }

/// <summary>
/// A square heightmap map, any size (2-5 km), in one of several biomes:
/// - Temperate: rolling forested hills (the original valley).
/// - Forest: steeper hills under heavy mixed woodland, one big hill.
/// - Desert: sand with low dunes, rocky mesas, an oasis, scrub.
/// - Urban: flat ground for a city, rising to wooded hills at the edges.
/// - Highlands: big hills and ridges, patchy pine forest.
/// Two shapes of terrain: the firing range (a long flat lane), or a battlefield with
/// flat pads (Flats) where settlements and bases sit.
/// </summary>
public partial class Terrain : Node3D, IGround
{
    // ---- set before Generate
    public float Size = 2048f;         // full width, metres
    public Biome Biome = Biome.Temperate;
    public bool RangeLane = true;
    public float HillScale = 1f;
    /// <summary>Flat pads (centre, radius); the pad height is filled in by Generate.</summary>
    public readonly List<(Vector2 C, float R, float H)> Flats = new();
    /// <summary>Big single hills: (centre, radius, height).</summary>
    public readonly List<(Vector2 C, float R, float H)> BigHills = new();
    /// <summary>Flat-topped rocky mesas: (centre, radius, height).</summary>
    public readonly List<(Vector2 C, float R, float H)> Mesas = new();
    /// <summary>An oasis: a shallow basin with a pool, green around it.</summary>
    public (Vector2 C, float R)? Oasis;
    /// <summary>What the map has put at a point, for planting: 0 nothing, 1 a park (trees welcome), 2 built on (streets, buildings: nothing grows).</summary>
    public Func<float, float, int>? Zone;
    /// <summary>A paved area (a town's ground): its colour replaces the grass or sand.</summary>
    public Func<float, float, Color?>? Paint;

    public int Res { get; private set; }
    public float Spacing { get; private set; }
    int Half => (Res - 1) / 2;
    public float Extent { get; private set; }

    public float[] Heights = Array.Empty<float>();
    FastNoiseLite _hills = null!, _ridge = null!, _detail = null!, _forest = null!, _dunes = null!;

    public void Generate(int seed, bool plant = true)
    {
        // Coarser grid on bigger maps: keeps the mesh and heightfield a sensible size.
        Spacing = Size <= 2100f ? 4f : Size <= 3100f ? 5f : 8f;
        Res = (int)MathF.Round(Size / Spacing) / 2 * 2 + 1;
        Extent = Half * Spacing;
        Heights = new float[Res * Res];

        _hills = new FastNoiseLite { Seed = seed, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.0018f, FractalOctaves = 5 };
        _ridge = new FastNoiseLite { Seed = seed + 1, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.0011f, FractalOctaves = 3 };
        _detail = new FastNoiseLite { Seed = seed + 2, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.03f, FractalOctaves = 3 };
        _forest = new FastNoiseLite { Seed = seed + 3, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = Biome == Biome.Forest ? 0.0025f : 0.004f, FractalOctaves = 2 };
        _dunes = new FastNoiseLite { Seed = seed + 4, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.006f, FractalOctaves = 3 };

        for (int k = 0; k < Flats.Count; k++)
        {
            var f = Flats[k];
            Flats[k] = (f.C, f.R, Raw(f.C.X, f.C.Y));
        }
        for (int j = 0; j < Res; j++)
        for (int i = 0; i < Res; i++)
            Heights[j * Res + i] = HeightFn((i - Half) * Spacing, (j - Half) * Spacing);

        BuildMesh();
        BuildCollision();
        Main = this;
        BuildBounds();
        if (plant) Plant(seed);
    }

    /// <summary>Trees, rocks and water: after the map's buildings are up, so nothing grows through a house.</summary>
    public void Plant(int seed)
    {
        ScatterTrees(seed);
        ScatterRocks(seed);
        if (Oasis is { } o) BuildPool(o.C, o.R);
    }

    public static float LaneDist(float x, float z)
    {
        float dx = MathF.Max(MathF.Abs(x) - 45f, 0f);
        float dz = MathF.Max(MathF.Max(z - 40f, -1000f - z), 0f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>How far outside anything that must stay clear (the range lane, or a settlement pad).</summary>
    public float ClearDist(float x, float z)
    {
        if (RangeLane) return LaneDist(x, z);
        float best = 9999f;
        foreach (var f in Flats) best = MathF.Min(best, new Vector2(x, z).DistanceTo(f.C) - f.R);
        return best;
    }

    float RawHills(float x, float z)
    {
        float n = _hills.GetNoise2D(x, z) * 0.5f + 0.5f;
        float ridge = 1f - MathF.Abs(_ridge.GetNoise2D(x, z));
        return (MathF.Pow(n, 1.4f) * 150f + ridge * ridge * 40f) * HillScale + _detail.GetNoise2D(x, z) * 0.6f;
    }

    /// <summary>The biome's ground before the pads are flattened into it.</summary>
    float Raw(float x, float z)
    {
        var p = new Vector2(x, z);
        float h;
        switch (Biome)
        {
            case Biome.Desert:
            {
                // Long low dunes on a gently undulating plain.
                float dune = 1f - MathF.Abs(_dunes.GetNoise2D(x * 0.6f, z * 1.4f));
                h = dune * dune * 9f + (_hills.GetNoise2D(x * 0.5f, z * 0.5f) * 0.5f + 0.5f) * 30f + _detail.GetNoise2D(x, z) * 0.4f;
                for (int mi = 0; mi < Mesas.Count; mi++)
                {
                    // A plateau with a ragged, steep edge — except one side, where a long ramp
                    // of scree (about 18°) lets people and vehicles up.
                    var m = Mesas[mi];
                    var off = p - m.C;
                    float rampDir = mi * 2.4f + 0.7f;
                    float along = MathF.Cos(MathF.Atan2(off.Y, off.X) - rampDir);
                    float ramp = Mathf.SmoothStep(0.82f, 0.95f, along);
                    float edge = m.R * (1f + _detail.GetNoise2D(x * 0.2f, z * 0.2f) * 0.25f * (1f - ramp));
                    float width = Mathf.Lerp(30f, m.H * 3.2f, ramp);
                    h += m.H * (1f - Mathf.SmoothStep(edge - 12f, edge - 12f + width, off.Length()));
                }
                break;
            }
            case Biome.Urban:
            {
                // The city sits on nearly flat ground; the land rises into hills toward the edges.
                float edge = MathF.Max(MathF.Abs(x), MathF.Abs(z)) / MathF.Max(Extent, 1f);
                h = RawHills(x, z) * (0.12f + 0.9f * Mathf.SmoothStep(0.55f, 0.95f, edge));
                break;
            }
            default:
                h = RawHills(x, z);
                break;
        }
        foreach (var b in BigHills)
        {
            float d = p.DistanceTo(b.C) / b.R;
            h += b.H * MathF.Exp(-d * d * 2.2f) * (1f + _detail.GetNoise2D(x * 0.1f, z * 0.1f) * 0.08f);
        }
        if (Oasis is { } o)
        {
            float d = p.DistanceTo(o.C);
            h -= 5f * (1f - Mathf.SmoothStep(o.R * 0.4f, o.R, d));
        }
        return h;
    }

    float HeightFn(float x, float z)
    {
        if (!RangeLane)
        {
            float h = Raw(x, z);
            foreach (var f in Flats)
            {
                float d = new Vector2(x, z).DistanceTo(f.C);
                h = Mathf.Lerp(h, f.H, 1f - Mathf.SmoothStep(f.R, f.R + MathF.Min(f.R * 0.8f, 150f) + 15f, d));
            }
            return h;
        }
        float n = _hills.GetNoise2D(x, z) * 0.5f + 0.5f;
        float ridge = 1f - MathF.Abs(_ridge.GetNoise2D(x, z));
        float hills = MathF.Pow(n, 1.4f) * 150f + ridge * ridge * 40f;
        float floor = _detail.GetNoise2D(x, z) * 0.6f + MathF.Max(0f, -z - 200f) * 0.01f; // gentle rise downrange
        float s = Mathf.SmoothStep(15f, 220f, LaneDist(x, z));
        return floor + s * hills;
    }

    float H(int i, int j) => Heights[Math.Clamp(j, 0, Res - 1) * Res + Math.Clamp(i, 0, Res - 1)];

    /// <summary>Height of the rendered surface: the same two triangles per grid square the mesh uses.</summary>
    public float HeightAt(float x, float z)
    {
        float fx = Mathf.Clamp(x / Spacing + Half, 0f, Res - 1.001f);
        float fz = Mathf.Clamp(z / Spacing + Half, 0f, Res - 1.001f);
        int i = (int)fx, j = (int)fz;
        float tx = fx - i, tz = fz - j;
        float a = H(i, j), b = H(i + 1, j), c = H(i, j + 1), d = H(i + 1, j + 1);
        return tx + tz <= 1f
            ? a + (b - a) * tx + (c - a) * tz
            : d + (c - d) * (1f - tx) + (b - d) * (1f - tz);
    }

    /// <summary>
    /// Does the ground itself rise above the straight line from a to b somewhere between them? For sight
    /// lines: a look at the height grid before casting a ray, which would only find the same hill. It says
    /// yes only where the line runs below the lowest corner of a grid cell (however that cell's two
    /// triangles are cut, the ground there is higher still), so it's never wrong about a clear line; where
    /// it can't tell, it says no and the ray decides. <paramref name="at"/> is roughly where it's blocked.
    /// </summary>
    public bool Blocks(Vector3 a, Vector3 b, out Vector3 at)
    {
        at = b;
        var d = b - a;
        float flat = new Vector2(d.X, d.Z).Length();
        if (flat < Spacing * 3f) return false;
        int n = (int)(flat / (Spacing * 0.75f));
        for (int k = 1; k < n; k++)
        {
            var p = a + d * ((float)k / n);
            float fx = Mathf.Clamp(p.X / Spacing + Half, 0f, Res - 1.001f), fz = Mathf.Clamp(p.Z / Spacing + Half, 0f, Res - 1.001f);
            int i = (int)fx, j = (int)fz;
            float low = MathF.Min(MathF.Min(H(i, j), H(i + 1, j)), MathF.Min(H(i, j + 1), H(i + 1, j + 1)));
            if (p.Y < low - 0.05f) { at = p; return true; }
        }
        return false;
    }

    // ---------------------------------------------------------------- holes

    /// <summary>
    /// Where craters have been dug, the terrain surface is cut away (the shader
    /// discards it) so the crater mesh underneath shows its real depth. The mask is a
    /// grid over the whole map holding a smooth "how dug is it" value, so its filtered
    /// 0.5 threshold traces a clean curve rather than square texels.
    /// </summary>
    public const int HoleRes = 4096;
    public float HoleCell => 2f * Extent / HoleRes;
    Image? _holeImg;
    ImageTexture? _holeTex;
    bool _holesDirty;
    double _holesFlushAt;

    public void CutHoles(Func<float, float, float> digAt, Vector2 min, Vector2 max)
    {
        if (_holeImg == null) return;
        float cell = HoleCell;
        int i0 = Math.Max(0, (int)((min.X + Extent) / cell)), i1 = Math.Min(HoleRes - 1, (int)((max.X + Extent) / cell) + 1);
        int j0 = Math.Max(0, (int)((min.Y + Extent) / cell)), j1 = Math.Min(HoleRes - 1, (int)((max.Y + Extent) / cell) + 1);
        for (int j = j0; j <= j1; j++)
        for (int i = i0; i <= i1; i++)
        {
            float x = (i + 0.5f) * cell - Extent, z = (j + 0.5f) * cell - Extent;
            float v = Mathf.Clamp(0.5f + (digAt(x, z) - 0.05f) / 0.12f, 0f, 1f);
            if (v <= _holeImg.GetPixel(i, j).R) continue;
            _holeImg.SetPixel(i, j, new Color(v, 0f, 0f));
            _holesDirty = true;
        }
    }

    public override void _ExitTree()
    {
        if (Main == this) Main = null;
    }

    public override void _Process(double delta)
    {
        // Uploading the mask is a 16 MB copy: batch blasts that land close together in time.
        if (!_holesDirty || Clock.Now < _holesFlushAt) return;
        _holesDirty = false;
        _holesFlushAt = Clock.Now + 0.25;
        _holeTex!.Update(_holeImg);
    }

    const string TerrainShader = @"
shader_type spatial;
uniform sampler2D holes : filter_linear, repeat_disable;
uniform float extent;
varying vec3 wpos;
void vertex() { wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz; }
vec3 lin(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(0.04045, c)); }
void fragment() {
    if (texture(holes, wpos.xz / (2.0 * extent) + 0.5).r > 0.5) discard;
    ALBEDO = lin(COLOR.rgb);
    ROUGHNESS = 0.95;
}";

    public Vector3 NormalAt(float x, float z)
    {
        const float e = 2f;
        return new Vector3(HeightAt(x - e, z) - HeightAt(x + e, z), 2f * e, HeightAt(x, z - e) - HeightAt(x, z + e)).Normalized();
    }

    static readonly Color Grass = new(0.30f, 0.36f, 0.18f), Dry = new(0.46f, 0.43f, 0.28f),
                          Dirt = new(0.40f, 0.33f, 0.24f), Rock = new(0.42f, 0.41f, 0.40f),
                          DeepGreen = new(0.22f, 0.3f, 0.14f), Heath = new(0.4f, 0.4f, 0.26f),
                          Sand = new(0.74f, 0.64f, 0.45f), Sand2 = new(0.66f, 0.55f, 0.38f), Desert = new(0.58f, 0.46f, 0.33f), Palm = new(0.36f, 0.42f, 0.2f);

    Color ColorFn(float x, float z, float h, Vector3 n)
    {
        float noise = Mathf.Clamp(_detail.GetNoise2D(x * 0.3f, z * 0.3f) * 0.8f + 0.4f, 0f, 1f);
        float steep = Mathf.SmoothStep(0.25f, 0.5f, 1f - n.Y);
        if (Paint?.Invoke(x, z) is Color paved) return paved.Lerp(paved.Darkened(0.15f), noise);
        if (Biome == Biome.Desert)
        {
            var c = Sand.Lerp(Sand2, noise);
            if (Oasis is { } o)
                c = c.Lerp(Palm, 1f - Mathf.SmoothStep(o.R * 0.6f, o.R * 1.2f, new Vector2(x, z).DistanceTo(o.C)));
            return c.Lerp(Desert, steep * 0.9f + Mathf.SmoothStep(0.1f, 0.2f, 1f - n.Y) * 0.2f);
        }
        var g = Biome switch { Biome.Forest => DeepGreen.Lerp(Grass, noise * 0.6f), Biome.Highlands => Grass.Lerp(Heath, noise), _ => Grass.Lerp(Dry, noise) };
        if (ClearDist(x, z) < 30f) g = g.Lerp(Dirt, 0.35f);
        return g.Lerp(Rock, steep + Mathf.SmoothStep(120f, 170f, h) * (Biome == Biome.Highlands ? 0.6f : 0.4f));
    }

    /// <summary>Ground colour (sRGB) at a point — lets craters and the like blend into the terrain.</summary>
    public Color ColorAt(float x, float z) => ColorFn(x, z, HeightAt(x, z), NormalAt(x, z));

    void BuildMesh()
    {
        var verts = new Vector3[Res * Res];
        var norms = new Vector3[Res * Res];
        var cols = new Color[Res * Res];

        for (int j = 0; j < Res; j++)
        for (int i = 0; i < Res; i++)
        {
            int idx = j * Res + i;
            float x = (i - Half) * Spacing, z = (j - Half) * Spacing, h = Heights[idx];
            verts[idx] = new Vector3(x, h, z);
            var n = new Vector3(H(i - 1, j) - H(i + 1, j), 2f * Spacing, H(i, j - 1) - H(i, j + 1)).Normalized();
            norms[idx] = n;
            cols[idx] = ColorFn(x, z, h, n);
        }

        var idxs = new int[(Res - 1) * (Res - 1) * 6];
        int k = 0;
        for (int j = 0; j < Res - 1; j++)
        for (int i = 0; i < Res - 1; i++)
        {
            int a = j * Res + i, b = a + 1, c = a + Res, d = c + 1;
            idxs[k++] = a; idxs[k++] = b; idxs[k++] = c;
            idxs[k++] = b; idxs[k++] = d; idxs[k++] = c;
        }

        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = verts;
        arr[(int)Mesh.ArrayType.Normal] = norms;
        arr[(int)Mesh.ArrayType.Color] = cols;
        arr[(int)Mesh.ArrayType.Index] = idxs;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        _holeImg = Image.CreateEmpty(HoleRes, HoleRes, false, Image.Format.R8);
        _holeTex = ImageTexture.CreateFromImage(_holeImg);
        var mat = new ShaderMaterial { Shader = new Shader { Code = TerrainShader } };
        mat.SetShaderParameter("holes", _holeTex);
        mat.SetShaderParameter("extent", Extent);
        AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = mat });
    }

    void BuildCollision()
    {
        var body = new StaticBody3D { CollisionLayer = 1 };
        AddChild(body);
        body.AddToGroup("ground");
        var shape = new HeightMapShape3D { MapWidth = Res, MapDepth = Res, MapData = Heights };
        body.AddChild(new CollisionShape3D { Shape = shape, Scale = new Vector3(Spacing, 1f, Spacing) });
    }

    // ---------------------------------------------------------------- trees

    /// <summary>A kind of tree: how it's built, whether it stops bullets and vehicles, how big.</summary>
    sealed class Species
    {
        public string Name = "";
        public Mesh? Trunk;
        public Mesh Crown = null!;
        public Material TrunkMat = null!, CrownMat = null!;
        public float TrunkY, CrownY;          // mesh centre heights (unscaled)
        public float Radius = 0.2f, Height = 6f; // trunk collision
        public bool Solid = true;
        public float MinS = 0.7f, MaxS = 1.35f;
        public readonly List<Transform3D> Xf = new();
        public MultiMesh? TrunkMM, CrownMM;
    }

    static StandardMaterial3D M(Color c) => new() { AlbedoColor = c, Roughness = 1f };

    static Species Pine() => new()
    {
        Name = "pine", Trunk = new CylinderMesh { TopRadius = 0.1f, BottomRadius = 0.22f, Height = 6f, RadialSegments = 6 }, TrunkY = 3f,
        Crown = new CylinderMesh { TopRadius = 0f, BottomRadius = 2.3f, Height = 8f, RadialSegments = 7 }, CrownY = 7.5f,
        TrunkMat = M(new Color(0.25f, 0.18f, 0.12f)), CrownMat = M(new Color(0.13f, 0.22f, 0.12f)),
    };
    static Species Broadleaf() => new()
    {
        Name = "broadleaf", Trunk = new CylinderMesh { TopRadius = 0.15f, BottomRadius = 0.3f, Height = 5f, RadialSegments = 6 }, TrunkY = 2.5f,
        Crown = new SphereMesh { Radius = 3.2f, Height = 5.2f, RadialSegments = 8, Rings = 4 }, CrownY = 6.3f,
        TrunkMat = M(new Color(0.3f, 0.22f, 0.15f)), CrownMat = M(new Color(0.2f, 0.3f, 0.13f)), Radius = 0.28f, Height = 5f,
    };
    static Species Birch() => new()
    {
        Name = "birch", Trunk = new CylinderMesh { TopRadius = 0.07f, BottomRadius = 0.14f, Height = 8f, RadialSegments = 6 }, TrunkY = 4f,
        Crown = new SphereMesh { Radius = 1.8f, Height = 4.5f, RadialSegments = 7, Rings = 4 }, CrownY = 7.8f,
        TrunkMat = M(new Color(0.8f, 0.78f, 0.72f)), CrownMat = M(new Color(0.34f, 0.42f, 0.18f)), Radius = 0.14f, Height = 8f,
    };
    static Species Bush() => new()
    {
        Name = "bush", Crown = new SphereMesh { Radius = 1.1f, Height = 1.6f, RadialSegments = 7, Rings = 3 }, CrownY = 0.6f,
        CrownMat = M(new Color(0.2f, 0.28f, 0.12f)), Solid = false, MinS = 0.6f, MaxS = 1.5f,
    };
    static Species PalmTree() => new()
    {
        Name = "palm", Trunk = new CylinderMesh { TopRadius = 0.16f, BottomRadius = 0.24f, Height = 9f, RadialSegments = 6 }, TrunkY = 4.5f,
        Crown = new CylinderMesh { TopRadius = 0.2f, BottomRadius = 3.6f, Height = 1.4f, RadialSegments = 9 }, CrownY = 9f,
        TrunkMat = M(new Color(0.45f, 0.36f, 0.24f)), CrownMat = M(new Color(0.28f, 0.38f, 0.14f)), Radius = 0.22f, Height = 9f, MinS = 0.8f, MaxS = 1.2f,
    };
    static Species Scrub() => new()
    {
        Name = "scrub", Crown = new SphereMesh { Radius = 0.7f, Height = 0.8f, RadialSegments = 6, Rings = 3 }, CrownY = 0.3f,
        CrownMat = M(new Color(0.45f, 0.42f, 0.28f)), Solid = false, MinS = 0.5f, MaxS = 1.3f,
    };

    readonly List<Species> _species = new();

    /// <summary>
    /// Plant the map: species and density by biome, in patches (a forest noise field),
    /// off steep slopes, off settlement pads, streets and buildings.
    /// </summary>
    void ScatterTrees(int seed)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)seed };
        float area = Size * Size / (2048f * 2048f);
        var pine = Pine(); var broad = Broadleaf(); var birch = Birch(); var bush = Bush(); var palm = PalmTree(); var scrub = Scrub();
        // (species, how many, forest-noise threshold: lower = more of the map)
        var plan = Biome switch
        {
            Biome.Forest => new[] { (pine, 10000, -0.35f), (broad, 6000, -0.3f), (birch, 2500, -0.25f), (bush, 6000, -0.4f) },
            Biome.Desert => new[] { (scrub, 3500, -1f), (palm, 90, -1f) },
            Biome.Urban => new[] { (broad, 1600, -0.05f), (pine, 1200, 0.05f), (bush, 1500, -0.1f) },
            Biome.Highlands => new[] { (pine, 4200, 0.05f), (birch, 700, 0.1f), (bush, 2000, -0.1f) },
            _ => new[] { (pine, 3200, -0.05f), (broad, 1300, 0f), (bush, 1200, -0.1f) },
        };
        float e = Extent - 20f;
        foreach (var (sp, count, thresh) in plan)
        {
            int want = (int)(count * (RangeLane ? 1f : area));
            for (int tries = 0; tries < want * 14 && sp.Xf.Count < want; tries++)
            {
                float x = rng.RandfRange(-e, e), z = rng.RandfRange(-e, e);
                if (sp == palm && Oasis is { } o)
                {
                    // Palms ring the oasis.
                    float a = rng.Randf() * Mathf.Tau, r = o.R * MathF.Sqrt(rng.RandfRange(0.25f, 1.3f));
                    x = o.C.X + MathF.Cos(a) * r;
                    z = o.C.Y + MathF.Sin(a) * r;
                    if (new Vector2(x, z).DistanceTo(o.C) < o.R * 0.35f) continue; // not in the pool
                }
                else
                {
                    int zone = Zone?.Invoke(x, z) ?? 0;
                    if (zone == 2) continue;
                    if (zone == 1) { if (rng.Randf() > 0.35f) continue; } // parks: scattered trees, whatever the woods are doing
                    else
                    {
                        if (ClearDist(x, z) < (RangeLane ? 70f : 12f)) continue;
                        if (_forest.GetNoise2D(x, z) < thresh) continue;
                    }
                }
                if (Zone?.Invoke(x, z) == 2) continue;
                if (NormalAt(x, z).Y < (sp.Solid ? 0.82f : 0.7f)) continue;
                float s = rng.RandfRange(sp.MinS, sp.MaxS);
                var basis = Basis.FromEuler(new Vector3(0f, rng.RandfRange(0f, Mathf.Tau), 0f)).Scaled(new Vector3(s, s, s));
                sp.Xf.Add(new Transform3D(basis, new Vector3(x, HeightAt(x, z) - 0.2f, z)));
            }
            if (sp.Xf.Count > 0 && !_species.Contains(sp)) _species.Add(sp);
        }

        // Trunks stop bullets; foliage only hides you. They're on their own layer so the navmesh
        // ignores them (thousands of tiny holes) — characters just slide around them.
        // One body per 128 m square: a single body with tens of thousands of shapes costs
        // minutes to build (every added shape rebuilds the body's compound shape).
        var bodies = new Dictionary<(int, int), StaticBody3D>();
        StaticBody3D BodyAt(Vector3 p)
        {
            var k = ((int)MathF.Floor(p.X / 128f), (int)MathF.Floor(p.Z / 128f));
            if (!bodies.TryGetValue(k, out var b)) { b = new StaticBody3D { CollisionLayer = Layers.Trees }; AddChild(b); bodies[k] = b; }
            return b;
        }
        foreach (var sp in _species)
        {
            if (sp.Trunk != null) sp.TrunkMM = MultiMesh(sp.Trunk, sp.TrunkMat, sp.Xf, sp.TrunkY);
            sp.CrownMM = MultiMesh(sp.Crown, sp.CrownMat, sp.Xf, sp.CrownY);
            if (!sp.Solid) continue;
            var cyl = new CylinderShape3D { Radius = sp.Radius, Height = sp.Height };
            for (int i = 0; i < sp.Xf.Count; i++)
            {
                var cs = new CollisionShape3D { Shape = cyl, Position = sp.Xf[i].Origin + Vector3.Up * sp.Height / 2f };
                BodyAt(sp.Xf[i].Origin).AddChild(cs);
                _treeOf[cs] = (sp, i);
                var o = sp.Xf[i].Origin;
                var key = ((int)MathF.Floor(o.X / TreeCell), (int)MathF.Floor(o.Z / TreeCell));
                _treeCells[key] = _treeCells.GetValueOrDefault(key) + 1;
            }
        }
    }

    public int TreeCount => _species.Sum(s => s.Xf.Count);

    /// <summary>Where every tree (and bush) stands, on the ground plane (for maps and plots).</summary>
    public IEnumerable<Vector2> TreePositions() => _species.SelectMany(s => s.Xf).Select(x => new Vector2(x.Origin.X, x.Origin.Z));

    readonly Dictionary<(int, int), int> _treeCells = new();
    const float TreeCell = 20f;

    /// <summary>Trees (trunks, not bushes) standing in the 60 m square round a point.</summary>
    public int TreesNear(float x, float z)
    {
        int cx = (int)MathF.Floor(x / TreeCell), cz = (int)MathF.Floor(z / TreeCell), n = 0;
        for (int i = -1; i <= 1; i++)
        for (int j = -1; j <= 1; j++)
            n += _treeCells.GetValueOrDefault((cx + i, cz + j));
        return n;
    }

    /// <summary>The terrain the trees belong to (the one on the current map).</summary>
    public static Terrain? Main;
    readonly Dictionary<CollisionShape3D, (Species Sp, int I)> _treeOf = new();

    /// <summary>A heavy vehicle has driven into a tree: it goes over, away from the push, and stops being an obstacle.</summary>
    public static void Fell(CollisionShape3D shape, Vector3 push)
    {
        var t = Main;
        if (t == null || !t._treeOf.TryGetValue(shape, out var tr)) return;
        t._treeOf.Remove(shape);
        shape.QueueFree();
        push.Y = 0f;
        if (push.LengthSquared() < 0.01f) push = Vector3.Forward;
        var axis = Vector3.Up.Cross(push.Normalized()).Normalized();
        var x = tr.Sp.Xf[tr.I];
        var b = new Basis(axis, -Mathf.DegToRad(84f)) * x.Basis;
        var fallen = new Transform3D(b, x.Origin + Vector3.Up * 0.3f);
        foreach (var (mm, off) in new[] { (tr.Sp.TrunkMM, tr.Sp.TrunkY), (tr.Sp.CrownMM, tr.Sp.CrownY) })
        {
            if (mm == null) continue;
            var f = fallen;
            f.Origin += f.Basis.Y * off;
            mm.SetInstanceTransform(tr.I, f);
        }
        SoundWorld.I.Emit(Snd.Impact, x.Origin + Vector3.Up, 6f);
    }

    void ScatterRocks(int seed)
    {
        var rng = new RandomNumberGenerator { Seed = (ulong)seed + 7 };
        var xf = new List<Transform3D>();
        var bodies = new Dictionary<(int, int), StaticBody3D>();
        float area = Size * Size / (2048f * 2048f);
        int want = (int)((Biome == Biome.Desert ? 700 : Biome == Biome.Highlands ? 600 : 350) * (RangeLane ? 1f : area));
        float e = Extent - 20f;
        var col = Biome == Biome.Desert ? new Color(0.6f, 0.48f, 0.36f) : new Color(0.45f, 0.44f, 0.42f);
        for (int tries = 0; tries < want * 25 && xf.Count < want; tries++)
        {
            float x = rng.RandfRange(-e, e), z = rng.RandfRange(-e, e);
            if (ClearDist(x, z) < (RangeLane ? 20f : 6f)) continue;
            if (Zone?.Invoke(x, z) is 1 or 2) continue;
            if (NormalAt(x, z).Y > 0.9f && rng.Randf() > 0.15f) continue; // mostly on slopes
            var size = new Vector3(rng.RandfRange(0.8f, 3.5f), rng.RandfRange(0.6f, 2.2f), rng.RandfRange(0.8f, 3.5f));
            var basis = Basis.FromEuler(new Vector3(rng.RandfRange(-0.3f, 0.3f), rng.RandfRange(0f, Mathf.Tau), rng.RandfRange(-0.3f, 0.3f)));
            var pos = new Vector3(x, HeightAt(x, z) + size.Y * 0.25f, z);
            xf.Add(new Transform3D(basis.Scaled(size), pos));
            var k = ((int)MathF.Floor(x / 256f), (int)MathF.Floor(z / 256f));
            if (!bodies.TryGetValue(k, out var body)) { body = new StaticBody3D { CollisionLayer = 1 }; AddChild(body); bodies[k] = body; }
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Transform = new Transform3D(basis, pos) });
        }
        MultiMesh(new BoxMesh(), new StandardMaterial3D { AlbedoColor = col, Roughness = 1f }, xf, 0f);
    }

    /// <summary>The oasis pool: still water at the bottom of the basin.</summary>
    void BuildPool(Vector2 c, float r)
    {
        var water = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = r * 0.45f, BottomRadius = r * 0.45f, Height = 0.1f, RadialSegments = 32 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.18f, 0.32f, 0.36f, 0.85f), Roughness = 0.05f, Metallic = 0.3f, Transparency = BaseMaterial3D.TransparencyEnum.Alpha },
        };
        AddChild(water);
        water.Position = new Vector3(c.X, HeightAt(c.X, c.Y) + 0.9f, c.Y);
    }

    MultiMesh MultiMesh(Mesh mesh, Material mat, List<Transform3D> xf, float yOffset)
    {
        var mm = new MultiMesh { TransformFormat = Godot.MultiMesh.TransformFormatEnum.Transform3D, Mesh = mesh };
        mm.InstanceCount = xf.Count;
        for (int i = 0; i < xf.Count; i++)
        {
            var t = xf[i];
            t.Origin += t.Basis.Y * yOffset;
            mm.SetInstanceTransform(i, t);
        }
        AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = mat });
        return mm;
    }

    void BuildBounds()
    {
        var body = new StaticBody3D { CollisionLayer = 1 };
        AddChild(body);
        float e = Extent - 10f;
        foreach (var (pos, size) in new[]
        {
            (new Vector3(e + 5f, 0f, 0f), new Vector3(10f, 1200f, 2 * Extent)),
            (new Vector3(-e - 5f, 0f, 0f), new Vector3(10f, 1200f, 2 * Extent)),
            (new Vector3(0f, 0f, e + 5f), new Vector3(2 * Extent, 1200f, 10f)),
            (new Vector3(0f, 0f, -e - 5f), new Vector3(2 * Extent, 1200f, 10f)),
        })
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Position = pos });
    }
}
