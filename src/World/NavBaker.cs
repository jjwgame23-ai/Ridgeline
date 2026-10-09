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

    static readonly Godot.Collections.Array<Rid> _everywhere = new();

    /// <summary>
    /// Places that can't be reached (a spot inside a building, a vehicle's goal off the road): the nearest point that
    /// can be, as the whole-map search found it, by map and 2 m cell. Most of the whole-map searches (85% in Novigrad)
    /// were for these, and came back with just what the nearby tiles had.
    /// </summary>
    static readonly Dictionary<(Rid, long), Vector3> _unreachable = new();
    static long Cell(Vector3 p) => ((long)(int)MathF.Floor(p.X / 2f) << 32) ^ (uint)(int)MathF.Floor(p.Z / 2f);
    static float Length(Vector3[] path)
    {
        float l = 0f;
        for (int i = 1; i < path.Length; i++) l += path[i - 1].DistanceTo(path[i]);
        return l;
    }

    /// <summary>
    /// A path, searching further than Godot's default (4096 polygons, after which it gives up
    /// and returns a path to wherever it got): a route across a town's hundreds of thousands of
    /// polygons needs more. Main thread only.
    ///
    /// Asked first of just the tiles round the start and the end (and a good way round them): Godot's
    /// query otherwise goes through every polygon on the map to find where the two ends are, which is
    /// most of its cost (8 ms a path in Novigrad, 22 in Al Hamra, against under 1). If that doesn't get
    /// all the way (the way round lies further out), the whole map is asked.
    /// </summary>
    public static Vector3[] Path(Rid map, Vector3 from, Vector3 to, int maxPolygons = 16000)
    {
        _q.Map = map;
        _q.StartPosition = from;
        _q.TargetPosition = to;
        _q.PathSearchMaxPolygons = maxPolygons;
        // Not synced yet (the first seconds of a match): there's no path to be had, and asking only logs errors.
        if (NavigationServer3D.MapGetIterationId(map) == 0) return Array.Empty<Vector3>();
        // People's routes only. On the vehicles' navmesh, broken up by woods and streets, a route that can't be
        // finished within the nearby tiles mostly can't be finished at all, and searching the tiles first cost more
        // than it saved (Kessel 20 -> 26 ms a route, Novigrad 10 -> 11-13): vehicles ask the whole map, as before.
        var nav = Valley.Current is { } v && map == v.Nav.Map ? v.Nav : null;
        if (nav != null && nav._tiles.Count > 0)
        {
            float margin = MathF.Max(120f, from.DistanceTo(to) * 0.5f);
            var box = new Rect2(new Vector2(MathF.Min(from.X, to.X), MathF.Min(from.Z, to.Z)), new Vector2(MathF.Abs(from.X - to.X), MathF.Abs(from.Z - to.Z))).Grow(margin);
            // Somewhere we already know can't be reached: head straight for the nearest point that can be (a search
            // aimed at a place it can get to stops when it gets there, where one aimed at a place it can't goes
            // through everything within reach to make sure).
            var key = (map, Cell(to));
            bool known = _unreachable.TryGetValue(key, out var nearest);
            if (known) _q.TargetPosition = nearest;
            _q.IncludedRegions = nav.RegionsIn(box);
            // No polygon cap here: the tiles searched are the cap. (With one, Godot 4.7 has a flaw: when the end
            // can't be reached it searches again toward the nearest point it found, without resetting its count,
            // runs out part way, logs "not expect to not find the most reachable polygons" and gives back
            // nothing. Searching a few tiles, a first search could run dry under the cap and the second go over it.)
            _q.PathSearchMaxPolygons = 0;
            Vector3[] path;
            using (Prof.Time("path:tiles")) { NavigationServer3D.QueryPath(_q, _r); path = _r.Path; }
            _q.IncludedRegions = _everywhere;
            _q.PathSearchMaxPolygons = maxPolygons;
            if (path.Length > 0)
            {
                if (!known && path[^1].DistanceTo(nav.ClosestPoint(to)) < 0.5f) return path;
                // The same nearest point the whole map found last time, by a route shorter than any that leaves the
                // tiles could be (out of the box and back in is at least twice the margin): the whole map's answer.
                if (known && path[^1].DistanceTo(nearest) < 0.5f && Length(path) <= 2f * margin) { Prof.Count("path:can't get there (known)"); return path; }
            }
            // The whole map: for somewhere known to be out of reach, the way to its nearest reachable point (the
            // same end its own search found; aimed at a point it can reach, it doesn't have to exhaust everything).
            Vector3[] whole;
            using (Prof.Time("path:whole")) { NavigationServer3D.QueryPath(_q, _r); whole = _r.Path; }
            _q.TargetPosition = to;
            // The whole map can hit the same flaw (a start on an island of 8-16 thousand polygons): keep what the
            // nearby tiles found, rather than nothing.
            if (whole.Length == 0 && path.Length > 0) { Prof.Count("path:whole map failed, kept the nearby one"); return path; }
            if (known) { Prof.Count("path:whole map (known, to its nearest point)"); return whole; }
            bool same = path.Length > 0 && whole.Length > 0 && whole[^1].DistanceTo(path[^1]) < 0.5f;
            if (same) _unreachable[key] = whole[^1];
            Prof.Count(same ? "path:whole map (same end: can't get there)" : "path:whole map (got further)");
            return whole;
        }
        NavigationServer3D.QueryPath(_q, _r);
        return _r.Path;
    }
    public static float Snap(float v) => MathF.Floor(v / Grid) * Grid;

    /// <summary>
    /// MapGetClosestPoint, except before the map's first sync (the first moments of a match), when Godot has
    /// nothing to answer with: it logs an error and says (0, 0, 0), the middle of the map. Then the point itself.
    /// </summary>
    public static Vector3 MapClosest(Rid map, Vector3 p) =>
        NavigationServer3D.MapGetIterationId(map) == 0 ? p : NavigationServer3D.MapGetClosestPoint(map, p);

    public Node3D Root = null!;
    public float Extent = 1000f;   // bake x and z in [-Extent, Extent]
    public float Tile = 200f;
    /// <summary>
    /// A densely built area (a town) baked in smaller tiles: Recast keeps a tile's polygon
    /// mesh under 65 536 vertices and silently drops a tile that goes over, and a big tile
    /// of multi-storey interiors does.
    /// </summary>
    public Rect2? Dense;
    /// <summary>More densely built areas (a Conquest window can hold several towns).</summary>
    public readonly List<Rect2> DenseAreas = new();
    public float DenseTile = 64f;
    /// <summary>
    /// Ground nobody may path on, as outlines with the height up to which it's struck out: water too deep to wade or
    /// ford (a Conquest window's sea, lakes and rivers), leaving a bridge above it.
    /// </summary>
    public readonly List<(Vector3[] Outline, float Below)> Barred = new();
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
        _unreachable.Clear(); // a new map
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
        bool Split(int ix, int iz) => ix >= 0 && iz >= 0 && ix < n && iz < n
            && (Dense is Rect2 d && d.Intersects(new Rect2(-Extent + ix * Tile, -Extent + iz * Tile, Tile, Tile))
                || DenseAreas.Any(r => r.Intersects(new Rect2(-Extent + ix * Tile, -Extent + iz * Tile, Tile, Tile))));
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
            var area = new Rect2(x0, z0, size, size);
            if (Godot.FileAccess.FileExists(path) && ResourceLoader.Load<NavigationMesh>(path) is NavigationMesh cached)
            {
                AddRegion(cached, null, seam, size < Tile, area);
                continue;
            }

            if (source == null)
            {
                source = new NavigationMeshSourceGeometryData3D();
                NavigationServer3D.ParseSourceGeometryData(Make(), source, Root);
                foreach (var (outline, below) in Barred) source.AddProjectedObstruction(outline, -1000f, 1000f + below, true);
            }
            var nm = Make();
            nm.BorderSize = border;
            nm.FilterBakingAabb = new Aabb(new Vector3(x0 - border, -500f, z0 - border), new Vector3(size + 2f * border, 1500f, size + 2f * border));
            NavigationServer3D.BakeFromSourceGeometryDataAsync(nm, source, Callable.From(() => AddRegion(nm, path, seam, size < Tile, area)));
        }
    }

    void AddRegion(NavigationMesh nm, string? savePath, bool seam, bool dense, Rect2 area)
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
            if (polys > 0) _tiles.Add((region.GetRid(), area));
            Done++;
            if (Finished)
            {
                Seconds = (Time.GetTicksMsec() - _startMs) / 1000.0;
                AddLinks();
            }
        }).CallDeferred();
    }

    /// <summary>Short connections to add once the tiles are in (the doorways: see Builder.Doorways), both ways.</summary>
    public List<(Vector3 A, Vector3 B)> Links = new();
    readonly List<Rid> _links = new();

    void AddLinks()
    {
        foreach (var (a, b) in Links)
        {
            var l = NavigationServer3D.LinkCreate();
            NavigationServer3D.LinkSetMap(l, Map);
            NavigationServer3D.LinkSetBidirectional(l, true);
            NavigationServer3D.LinkSetStartPosition(l, a);
            NavigationServer3D.LinkSetEndPosition(l, b);
            _links.Add(l);
        }
    }

    public override void _ExitTree()
    {
        foreach (var l in _links) NavigationServer3D.FreeRid(l);
        _links.Clear();
    }

    /// <summary>Each non-empty tile's region, and the square of ground it was baked for.</summary>
    readonly List<(Rid Region, Rect2 Area)> _tiles = new();

    /// <summary>The regions of the tiles that overlap an area of the ground.</summary>
    public Godot.Collections.Array<Rid> RegionsIn(Rect2 r)
    {
        var a = new Godot.Collections.Array<Rid>();
        foreach (var (rid, area) in _tiles) if (area.Intersects(r)) a.Add(rid);
        return a;
    }

    /// <summary>
    /// The closest point on this navmesh to <paramref name="p"/>: the same answer as MapGetClosestPoint,
    /// found by asking only the tiles near the point. The map-wide query looks at every polygon on the map
    /// (a millisecond or two on the big maps, and vehicles ask it dozens of times to pick a firing position).
    /// Anything outside the tiles searched is further away than the reach, so when something's found
    /// within it, it's the closest there is; otherwise it asks the whole map.
    /// </summary>
    public Vector3 ClosestPoint(Vector3 p)
    {
        const float Reach = 24f;
        if (NavigationServer3D.MapGetIterationId(Map) == 0) return p; // not synced yet: nothing to snap to
        float best = float.MaxValue;
        var at = p;
        foreach (var (rid, area) in _tiles)
        {
            if (p.X < area.Position.X - Reach || p.X > area.End.X + Reach || p.Z < area.Position.Y - Reach || p.Z > area.End.Y + Reach) continue;
            var q = NavigationServer3D.RegionGetClosestPoint(rid, p);
            float d = q.DistanceSquaredTo(p);
            if (d < best) { best = d; at = q; }
        }
        return best <= Reach * Reach ? at : MapClosest(Map, p);
    }
}
