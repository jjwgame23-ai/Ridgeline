using Godot;

namespace Ridgeline;

/// <summary>
/// Bakes a navmesh for a big map in square tiles. The scene geometry is parsed
/// once (main thread), then every tile bakes in parallel on worker threads with
/// a border, so neighbouring tiles line up edge to edge and paths cross them
/// seamlessly. Baked tiles are cached under user://navcache keyed by map seed,
/// so only the first launch of a given map pays for it.
/// </summary>
public partial class NavBaker : Node
{
    const string Version = "v13"; // bump when map generation or nav settings change
    /// <summary>
    /// Tile corners and sizes are multiples of this (the least common multiple of the
    /// people's 0.35 m and the vehicles' 0.5 m cells), so every tile's voxel grid is the same
    /// grid and neighbouring tiles' edge vertices coincide exactly: they then join by edge key,
    /// which is fast, instead of by the margin-based edge search, which is very slow on a
    /// map of a few hundred thousand polygons.
    /// </summary>
    public const float Grid = 3.5f;

    static readonly NavigationPathQueryParameters3D _q = new() { PathPostprocessing = NavigationPathQueryParameters3D.PathPostProcessing.Corridorfunnel };
    static readonly NavigationPathQueryResult3D _r = new();

    /// <summary>
    /// A path, searching further than Godot's default (4096 polygons, after which it gives up
    /// and returns a path to wherever it got): a route across a town's hundreds of thousands of
    /// polygons needs more. Main thread only.
    /// </summary>
    public static Vector3[] Path(Rid map, Vector3 from, Vector3 to, int maxPolygons = 16000)
    {
        _q.Map = map;
        _q.StartPosition = from;
        _q.TargetPosition = to;
        _q.PathSearchMaxPolygons = maxPolygons;
        NavigationServer3D.QueryPath(_q, _r);
        return _r.Path;
    }
    public static float Snap(float v) => MathF.Floor(v / Grid) * Grid;

    public Node3D Root = null!;
    public float Extent = 1000f;   // bake x and z in [-Extent, Extent]
    public float Tile = 200f;
    /// <summary>
    /// A densely built area (a town) baked in smaller tiles: Recast keeps a tile's polygon
    /// mesh under 65 536 vertices and silently drops a tile that goes over, and a big tile
    /// of multi-storey interiors does.
    /// </summary>
    public Rect2? Dense;
    public float DenseTile = 64f;
    public string Key = "";
    /// <summary>Which navigation map the regions go into (default: the world's, for people).</summary>
    public Rid Map;
    /// <summary>Settings for this mesh; default is for people on foot.</summary>
    public Func<NavigationMesh> Make = Settings;

    public int Total { get; private set; }
    public int EmptyTiles { get; private set; }
    public long Polygons { get; private set; }
    public long DensePolygons { get; private set; }
    public int Done { get; private set; }
    public bool Finished => Total > 0 && Done >= Total;
    public double Seconds { get; private set; }

    ulong _startMs;

    /// <summary>
    /// For vehicles: 2 m of clearance either side, gentler slopes, taller: routes that a
    /// 3.5 m-wide tank can actually drive, not the alleys a man can squeeze down.
    /// </summary>
    public static NavigationMesh VehicleSettings() => new()
    {
        AgentRadius = 2.0f, AgentHeight = 3f, AgentMaxClimb = 0.6f, AgentMaxSlope = 30f,
        CellSize = 0.5f, CellHeight = 0.25f, EdgeMaxError = 2f, EdgeMaxLength = 0f,
        DetailSampleDistance = 16f, DetailSampleMaxError = 4f,
        RegionMinSize = 16f, RegionMergeSize = 100f,
        GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
        GeometryCollisionMask = Layers.World | Layers.Trees, // vehicles route round the woods
    };

    public static NavigationMesh Settings() => new()
    {
        // Coarse cells keep a 2 km mesh small enough to query quickly; radius = exactly one cell.
        AgentRadius = 0.35f, AgentHeight = 1.8f, AgentMaxClimb = 0.4f, AgentMaxSlope = 42f,
        CellSize = 0.35f, CellHeight = 0.2f, EdgeMaxError = 1.5f,
        EdgeMaxLength = 0f,
        // Godot turns the height-detail triangles into the navmesh's polygons, and by default
        // they follow the ground to within 20 cm: rolling ground becomes a fine lattice of
        // triangles (100k polygons per 2 km, 1M on the 5 km maps), and every path and
        // closest-point query costs in proportion. To within 80 cm is plenty for walking on,
        // and a quarter of the polygons.
        DetailSampleDistance = 16f, DetailSampleMaxError = 4f,
        RegionMinSize = 8f, RegionMergeSize = 60f, // fewer, larger polygons: cheaper pathfinding
        GeometryParsedGeometryType = NavigationMesh.ParsedGeometryType.StaticColliders,
        GeometryCollisionMask = 1,
    };

    public void Start()
    {
        _startMs = Time.GetTicksMsec();
        if (!Map.IsValid) Map = Root.GetWorld3D().NavigationMap;
        var proto = Make();
        NavigationServer3D.MapSetCellSize(Map, proto.CellSize);
        NavigationServer3D.MapSetCellHeight(Map, proto.CellHeight);
        // Same-sized tiles on the shared grid join by edge key. Only where big tiles meet a
        // town's small ones do the edges not line up; the regions along that seam alone use
        // the (slow) margin-based edge search.
        NavigationServer3D.MapSetUseEdgeConnections(Map, true);
        float cs = proto.CellSize, border = cs * MathF.Ceiling(2f / cs);
        Extent = Snap(Extent);
        DirAccess.MakeDirRecursiveAbsolute("user://navcache");

        NavigationMeshSourceGeometryData3D? source = null;
        int n = Mathf.CeilToInt(2f * Extent / Tile);
        var tiles = new List<(float X0, float Z0, float Size, string Name, bool Seam)>();
        bool Split(int ix, int iz) => ix >= 0 && iz >= 0 && ix < n && iz < n && Dense is Rect2 d
            && d.Intersects(new Rect2(-Extent + ix * Tile, -Extent + iz * Tile, Tile, Tile));
        for (int ix = 0; ix < n; ix++)
        for (int iz = 0; iz < n; iz++)
        {
            float x0 = -Extent + ix * Tile, z0 = -Extent + iz * Tile;
            if (Split(ix, iz))
            {
                int k = Math.Max(1, Mathf.RoundToInt(Tile / DenseTile));
                float s = Tile / k;
                for (int a = 0; a < k; a++)
                for (int b = 0; b < k; b++)
                {
                    bool seam = (a == 0 && !Split(ix - 1, iz)) || (a == k - 1 && !Split(ix + 1, iz)) || (b == 0 && !Split(ix, iz - 1)) || (b == k - 1 && !Split(ix, iz + 1));
                    tiles.Add((x0 + a * s, z0 + b * s, s, $"{ix}_{iz}_{a}_{b}", seam));
                }
            }
            else tiles.Add((x0, z0, Tile, $"{ix}_{iz}", Split(ix - 1, iz) || Split(ix + 1, iz) || Split(ix, iz - 1) || Split(ix, iz + 1)));
        }
        Total = tiles.Count;
        foreach (var (x0, z0, size, name, seam) in tiles)
        {
            string path = $"user://navcache/{Key}_{Version}_{name}.res";
            if (Godot.FileAccess.FileExists(path) && ResourceLoader.Load<NavigationMesh>(path) is NavigationMesh cached)
            {
                AddRegion(cached, null, seam, size < Tile);
                continue;
            }

            if (source == null)
            {
                source = new NavigationMeshSourceGeometryData3D();
                NavigationServer3D.ParseSourceGeometryData(Make(), source, Root);
            }
            var nm = Make();
            nm.BorderSize = border;
            nm.FilterBakingAabb = new Aabb(new Vector3(x0 - border, -500f, z0 - border), new Vector3(size + 2f * border, 1500f, size + 2f * border));
            NavigationServer3D.BakeFromSourceGeometryDataAsync(nm, source, Callable.From(() => AddRegion(nm, path, seam, size < Tile)));
        }
    }

    void AddRegion(NavigationMesh nm, string? savePath, bool seam, bool dense)
    {
        // Callbacks may arrive from a worker thread; touch the scene tree and disk on the main one.
        Callable.From(() =>
        {
            if (savePath != null) ResourceSaver.Save(nm, savePath);
            int polys = nm.GetPolygonCount();
            Polygons += polys;
            if (dense) DensePolygons += polys;
            if (polys == 0) EmptyTiles++;
            var region = new NavigationRegion3D { NavigationMesh = nm, UseEdgeConnections = seam };
            AddChild(region);
            region.SetNavigationMap(Map);
            Done++;
            if (Finished) Seconds = (Time.GetTicksMsec() - _startMs) / 1000.0;
        }).CallDeferred();
    }
}
