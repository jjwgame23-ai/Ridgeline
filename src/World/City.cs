using Godot;

namespace Ridgeline;

public enum CityStyle { Desert, European }

/// <summary>
/// A town on a street grid, laid out procedurally.
/// - Streets: a grid with irregular block sizes, wider avenues every few lines, all wide
///   enough for armour. Parked cars and debris along the kerbs give cover in the street.
/// - Blocks: rows of buildings along the street front, back to back, with gaps and
///   alleys between some; a courtyard in the middle of deep blocks.
/// - Buildings: desert style is flat-roofed render and mud brick, 1-3 storeys, nearly
///   all with stairs to the roof (a rooftop war); European style is 3-6 storey
///   apartment blocks. Buildings near the fighting (the districts) can all be entered;
///   further out most are solid shells.
/// - Landmarks: a mosque with a dome and minaret and a covered market (desert), or a
///   church with a tower (European); parks with trees.
/// - Districts: the capture points, spread across the town, each with its buildings'
///   rooms and windows as the places defenders go.
/// Planned before the terrain (so the ground can be flattened and paved, and trees
/// kept to the parks), built after.
/// </summary>
public sealed class City
{
    public Vector2 Center;
    public float Radius;
    public CityStyle Style;
    public float Y;

    public readonly List<Rect2> Blocks = new(), Parks = new();
    public readonly List<(float Pos, float Width)> XStreets = new(), ZStreets = new();
    public readonly List<(string Name, Vector2 At)> Districts = new();
    public readonly List<Perch> Perches = new();
    readonly Dictionary<int, List<Vector3>> _districtPoints = new();
    readonly Dictionary<int, List<Perch>> _districtPerches = new();
    Rect2? _landmark, _market;

    static readonly string[] DesertDistricts = { "Old Souk", "Great Mosque", "Industrial", "Rail Yard", "Hospital", "Government Centre", "Water Tower", "Cemetery", "Bus Station", "Bridge Road" };
    static readonly string[] EuroDistricts = { "Old Town", "Station", "Cathedral", "Tower Blocks", "Factory", "Market Square", "University", "Stadium", "Tram Depot", "Harbour Road" };

    public static City Plan(Vector2 center, float radius, CityStyle style, int districts, RandomNumberGenerator rng)
    {
        var c = new City { Center = center, Radius = radius, Style = style };
        bool desert = style == CityStyle.Desert;

        // Street lines across the whole square, then keep the blocks inside the town's circle.
        void Lines(List<(float, float)> list, float c0)
        {
            float p = c0 - radius;
            int n = 0;
            while (p < c0 + radius)
            {
                float width = n % 3 == 0 ? (desert ? 14f : 18f) : (desert ? 9f : 12f);
                list.Add((p, width));
                p += width / 2f + (desert ? rng.RandfRange(34f, 58f) : rng.RandfRange(50f, 76f));
                n++;
            }
            list.Add((p, desert ? 9f : 12f));
        }
        Lines(c.XStreets, center.X);
        Lines(c.ZStreets, center.Y);

        for (int i = 0; i + 1 < c.XStreets.Count; i++)
        for (int j = 0; j + 1 < c.ZStreets.Count; j++)
        {
            float x0 = c.XStreets[i].Pos + c.XStreets[i].Width / 2f, x1 = c.XStreets[i + 1].Pos - c.XStreets[i + 1].Width / 2f;
            float z0 = c.ZStreets[j].Pos + c.ZStreets[j].Width / 2f, z1 = c.ZStreets[j + 1].Pos - c.ZStreets[j + 1].Width / 2f;
            var r = new Rect2(x0, z0, x1 - x0, z1 - z0);
            if (r.GetCenter().DistanceTo(center) > radius * rng.RandfRange(0.82f, 1f)) continue;
            if (Corners(r).Any(p => p.DistanceTo(center) > radius * 1.06f)) continue;
            float park = desert ? 0.04f : 0.13f;
            if (r.GetCenter().DistanceTo(center) > radius * 0.25f && rng.Randf() < park) c.Parks.Add(r);
            else c.Blocks.Add(r);
            c._cells[(i, j)] = r;
        }

        // Landmarks near the centre.
        var central = c.Blocks.Where(b => b.Size.X > 32f && b.Size.Y > 32f).OrderBy(b => b.GetCenter().DistanceTo(center)).ToList();
        if (central.Count > 0) { c._landmark = central[0]; }
        if (desert && central.Count > 3) c._market = central[3];

        // Districts: spread out, the first in the middle.
        var names = (desert ? DesertDistricts : EuroDistricts).OrderBy(_ => rng.Randi()).ToList();
        if (desert && c._landmark != null) names.Remove("Great Mosque");
        if (!desert && c._landmark != null) names.Remove("Cathedral");
        var cands = c.Blocks.Concat(c.Parks).Select(b => b.GetCenter()).OrderBy(_ => rng.Randi()).ToList();
        float spacing = radius * (districts <= 4 ? 0.62f : 0.5f);
        if (c._landmark is Rect2 lm) c.Districts.Add((desert ? "Great Mosque" : "Cathedral", lm.GetCenter()));
        else if (cands.Count > 0) c.Districts.Add((names[0], cands.OrderBy(p => p.DistanceTo(center)).First()));
        for (int tries = 0; tries < 3 && c.Districts.Count < districts; tries++, spacing *= 0.8f)
            foreach (var p in cands)
            {
                if (c.Districts.Count >= districts) break;
                if (p.DistanceTo(center) > radius * 0.78f) continue;
                if (c.Districts.Any(d => d.At.DistanceTo(p) < spacing)) continue;
                c.Districts.Add((names[c.Districts.Count % names.Count], p));
            }
        return c;
    }

    static IEnumerable<Vector2> Corners(Rect2 r) => new[] { r.Position, r.Position + new Vector2(r.Size.X, 0f), r.Position + new Vector2(0f, r.Size.Y), r.End };

    /// <summary>Grid cells (between street lines) that became a block or a park.</summary>
    readonly Dictionary<(int, int), Rect2> _cells = new();

    static int LineBefore(List<(float Pos, float Width)> lines, float v)
    {
        int lo = 0, hi = lines.Count - 1;
        if (v < lines[0].Pos) return -1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (lines[mid].Pos <= v) lo = mid; else hi = mid - 1; }
        return lo;
    }

    /// <summary>On a block or park of the town, or the street round one (within the margin of it).</summary>
    bool NearTown(float x, float z, float margin, bool parks = true)
    {
        if (new Vector2(x, z).DistanceTo(Center) > Radius * 1.2f) return false;
        int i = LineBefore(XStreets, x), j = LineBefore(ZStreets, z);
        var p = new Vector2(x, z);
        for (int a = i - 1; a <= i; a++)
        for (int b = j - 1; b <= j; b++)
            if (_cells.TryGetValue((a, b), out var r) && r.Grow(margin).HasPoint(p) && (parks || !Parks.Contains(r))) return true;
        return false;
    }

    /// <summary>For the terrain's planting: 1 in a park, 2 on the built-up town (streets and blocks), 0 outside it.</summary>
    public int Zone(float x, float z)
    {
        if (!NearTown(x, z, 11f)) return 0;
        var p = new Vector2(x, z);
        foreach (var r in Parks) if (r.Grow(-3f).HasPoint(p)) return 1;
        return 2;
    }

    public Color? Paint(float x, float z)
    {
        if (!NearTown(x, z, 10f)) return null;
        foreach (var r in Parks) if (r.Grow(-2f).HasPoint(new Vector2(x, z))) return null;
        return Style == CityStyle.Desert ? new Color(0.66f, 0.58f, 0.44f) : new Color(0.46f, 0.45f, 0.42f);
    }

    int DistrictOf(Vector2 p, float within)
    {
        for (int i = 0; i < Districts.Count; i++)
            if (Districts[i].At.DistanceTo(p) < within) return i;
        return -1;
    }

    public IReadOnlyList<Vector3> PointsOf(int district) => _districtPoints.GetValueOrDefault(district) ?? new List<Vector3>();
    public IReadOnlyList<Perch> PerchesOf(int district) => _districtPerches.GetValueOrDefault(district) ?? new List<Perch>();

    public int Buildings, Enterable;

    public void Build(Node3D parent, IGround ground, RandomNumberGenerator rng)
    {
        bool desert = Style == CityStyle.Desert;
        Y = ground.HeightAt(Center.X, Center.Y);
        var root = new Builder(parent, new Vector3(0f, Y, 0f), 0f);

        // Street surfaces: visual only, just above the flattened ground.
        var road = desert ? Mats.Dirt : Mats.Asphalt;
        // Only the stretches of street that run past a block or a park, one piece per stretch between junctions.
        for (int i = 0; i < XStreets.Count; i++)
        for (int j = 0; j + 1 < ZStreets.Count; j++)
        {
            if (!_cells.ContainsKey((i - 1, j)) && !_cells.ContainsKey((i, j))) continue;
            var s = XStreets[i];
            float z0 = ZStreets[j].Pos - ZStreets[j].Width / 2f, z1 = ZStreets[j + 1].Pos + ZStreets[j + 1].Width / 2f;
            root.Box(new Vector3(s.Pos, Y + 0.03f, (z0 + z1) / 2f), new Vector3(s.Width, 0.06f, z1 - z0), 0f, road, solid: false);
        }
        for (int j = 0; j < ZStreets.Count; j++)
        for (int i = 0; i + 1 < XStreets.Count; i++)
        {
            if (!_cells.ContainsKey((i, j - 1)) && !_cells.ContainsKey((i, j))) continue;
            var s = ZStreets[j];
            float x0 = XStreets[i].Pos - XStreets[i].Width / 2f, x1 = XStreets[i + 1].Pos + XStreets[i + 1].Width / 2f;
            root.Box(new Vector3((x0 + x1) / 2f, Y + 0.035f, s.Pos), new Vector3(x1 - x0, 0.06f, s.Width), 0f, road, solid: false);
        }

        foreach (var block in Blocks)
        {
            if (_landmark == block) { Landmark(parent, block, rng); continue; }
            if (_market == block) { Market(parent, block, rng); continue; }
            BuildBlock(parent, block, rng);
        }
        foreach (var park in Parks) Park(parent, park, rng);
        StreetClutter(parent, rng);
    }

    Vector3 At(float x, float z) => new(x, Y, z);

    void Record(Builder b, Vector2 at)
    {
        Perches.AddRange(b.Perches);
        int d = DistrictOf(at, 90f);
        if (d < 0) return;
        if (!_districtPoints.TryGetValue(d, out var pts)) _districtPoints[d] = pts = new List<Vector3>();
        pts.AddRange(b.Points);
        if (!_districtPerches.TryGetValue(d, out var ps)) _districtPerches[d] = ps = new List<Perch>();
        ps.AddRange(b.Perches);
    }

    /// <summary>Rows of buildings along the block's street fronts, facing out.</summary>
    void BuildBlock(Node3D parent, Rect2 block, RandomNumberGenerator rng)
    {
        bool desert = Style == CityStyle.Desert;
        float bd = block.Size.Y;
        bool twoRows = bd >= 26f;
        float rowDepth = twoRows ? MathF.Min(bd / 2f - (desert ? 1f : 3f), desert ? 13f : 16f) : bd - 2f;
        var mats = desert ? new[] { Mats.Adobe, Mats.Adobe2, Mats.Adobe3, Mats.Plaster } : new[] { Mats.Render, Mats.Render2, Mats.Brick, Mats.Panel };
        float centreFrac = block.GetCenter().DistanceTo(Center) / Radius;

        foreach (int row in twoRows ? new[] { 0, 1 } : new[] { 0 })
        {
            float x = block.Position.X + 1f;
            while (x < block.End.X - 6f)
            {
                float lot = desert ? rng.RandfRange(9f, 15f) : rng.RandfRange(15f, 26f);
                if (block.End.X - (x + lot) < 7f) lot = block.End.X - 1f - x;
                float gap = rng.Randf() < (desert ? 0.35f : 0.25f) ? rng.RandfRange(1.8f, 3f) : 0.3f;
                float w = lot - gap;
                float setback = desert ? rng.RandfRange(0f, 1.5f) : rng.RandfRange(0f, 1f);
                float d = MathF.Max(6f, rowDepth - setback - (desert ? rng.RandfRange(0f, 3f) : 0f));
                float cx = x + lot / 2f;
                // Front row faces -Z (the street at the block's top edge); back row faces +Z.
                float cz = row == 0 ? block.Position.Y + setback + d / 2f : block.End.Y - setback - d / 2f;
                var at = new Vector2(cx, cz);
                bool near = DistrictOf(at, 110f) >= 0;
                bool enter = near || rng.Randf() < (desert ? 0.3f : 0.2f);
                int floors = desert
                    ? (rng.Randf() < 0.3f ? 1 : rng.Randf() < 0.7f ? 2 : 3) + (centreFrac < 0.3f && rng.Randf() < 0.3f ? 1 : 0)
                    : 3 + rng.RandiRange(0, 2) + (centreFrac < 0.45f ? rng.RandiRange(0, 2) : 0);
                if (enter && !desert) floors = Math.Min(floors, 5);
                var b = new Builder(parent, At(cx, cz), row == 0 ? 0f : 180f);
                if (d >= 9.4f || !enter)
                    b.Block(0f, 0f, w, d, floors, mats[rng.RandiRange(0, mats.Length - 1)], desert ? Mats.Adobe2 : Mats.Concrete, enter,
                            desert ? rng.Randf() < 0.85f : rng.Randf() < 0.5f, rng, backDoor: !twoRows || rng.Randf() < 0.5f, winEvery: desert ? 3.8f : 3.1f);
                else b.House(0f, 0f, w, d, "F", "FB", partition: w > 9f, wall: mats[rng.RandiRange(0, mats.Length - 1)], roof: Mats.Concrete);
                Buildings++;
                if (enter) Enterable++;
                Record(b, at);
                x += lot;
            }
        }

        // The courtyard between back-to-back rows: walls, junk, a car; somewhere to move through the block.
        if (twoRows)
        {
            var yard = new Builder(parent, At(block.GetCenter().X, block.GetCenter().Y), 0f);
            for (int i = 0; i < 4; i++)
                yard.Prop(rng.RandfRange(-block.Size.X / 2f + 4f, block.Size.X / 2f - 4f), rng.RandfRange(-1.5f, 1.5f),
                          new Vector3(rng.RandfRange(0.8f, 1.6f), rng.RandfRange(0.8f, 1.3f), rng.RandfRange(0.8f, 1.6f)), rng.RandfRange(0f, 90f), Mats.Wood);
            if (desert && rng.Randf() < 0.6f)
                yard.Wall(rng.RandfRange(-4f, 4f), -3f, rng.RandfRange(-4f, 4f) + 8f, -3f, 2.2f, 0.3f, Mats.Adobe2);
            if (rng.Randf() < 0.5f)
                yard.Prop(rng.RandfRange(-block.Size.X / 3f, block.Size.X / 3f), 0f, new Vector3(1.9f, 1.5f, 4.4f), 90f + rng.RandfRange(-15f, 15f), Mats.Truck);
            yard.Points.Add(yard.W(0f, 0f));
            Record(yard, block.GetCenter());
        }
    }

    /// <summary>The big building: a mosque with courtyard, dome and minaret, or a church with a tower.</summary>
    void Landmark(Node3D parent, Rect2 block, RandomNumberGenerator rng)
    {
        var c = block.GetCenter();
        var b = new Builder(parent, At(c.X, c.Y), 0f);
        float hw = block.Size.X / 2f - 1f, hd = block.Size.Y / 2f - 1f;
        if (Style == CityStyle.Desert)
        {
            // Courtyard wall with gates on all sides.
            var gate = new Builder.Opening(0f, 4f, 0f, 2.8f);
            b.Wall(-hw, -hd, hw, -hd, 2.8f, 0.4f, Mats.Adobe3, gate with { At = hw });
            b.Wall(-hw, hd, hw, hd, 2.8f, 0.4f, Mats.Adobe3, gate with { At = hw });
            b.Wall(-hw, -hd, -hw, hd, 2.8f, 0.4f, Mats.Adobe3, gate with { At = hd });
            b.Wall(hw, -hd, hw, hd, 2.8f, 0.4f, Mats.Adobe3, gate with { At = hd });
            float w = MathF.Min(22f, hw * 2f - 10f), d = MathF.Min(18f, hd * 2f - 12f);
            b.Block(0f, hd - d / 2f - 3f, w, d, 1, Mats.Adobe3, Mats.Adobe2, true, true, rng, winEvery: 3f);
            // Dome on a drum (the drum is solid, so the roof isn't a walk through it).
            b.LBox(w / 4f, Builder.Storey + 1.2f, hd - d / 2f - 3f, new Vector3(6f, 2.4f, 6f), Mats.Adobe3);
            b.Visual(new SphereMesh { Radius = 4.4f, Height = 8.8f, RadialSegments = 20, Rings = 10 }, new Vector3(w / 4f, Builder.Storey + 2.4f, hd - d / 2f - 3f), Mats.Green);
            // Minaret.
            var mx = -hw + 4f; var mz = -hd + 4f;
            b.LBox(mx, 13f, mz, new Vector3(2.6f, 26f, 2.6f), Mats.Adobe3);
            b.Visual(new CylinderMesh { TopRadius = 2.2f, BottomRadius = 1.8f, Height = 1.2f, RadialSegments = 12 }, new Vector3(mx, 21f, mz), Mats.Adobe);
            b.Visual(new CylinderMesh { TopRadius = 0f, BottomRadius = 1.4f, Height = 3.5f, RadialSegments = 12 }, new Vector3(mx, 27.7f, mz), Mats.Green);
            b.Points.Add(b.W(0f, -hd + 8f));
        }
        else
        {
            float w = MathF.Min(16f, hw * 2f - 8f), d = MathF.Min(30f, hd * 2f - 8f);
            b.Block(0f, 2f, w, d, 1, Mats.Stone, Mats.Concrete, true, false, rng, winEvery: 4f);
            // A tall nave roof and the tower (solid shells).
            b.Visual(new PrismMesh { Size = new Vector3(w + 1f, 5f, d + 1f) }, new Vector3(0f, Builder.Storey + 2.5f, 2f), Mats.Roof2);
            b.LBox(0f, 13f, 2f - d / 2f - 3f, new Vector3(6f, 26f, 6f), Mats.Stone);
            b.Visual(new CylinderMesh { TopRadius = 0f, BottomRadius = 3.6f, Height = 12f, RadialSegments = 4 }, new Vector3(0f, 32f, 2f - d / 2f - 3f), Mats.Roof2);
            // The square in front.
            for (int i = 0; i < 6; i++) b.Prop(rng.RandfRange(-hw + 3f, hw - 3f), -hd + rng.RandfRange(2f, 8f), new Vector3(2f, 0.5f, 0.5f), rng.RandfRange(0f, 180f), Mats.Stone);
            b.Points.Add(b.W(0f, -hd + 5f));
        }
        Buildings++;
        Enterable++;
        Record(b, c);
    }

    /// <summary>The souk: rows of stalls under canopies, counters to crouch behind, a narrow lane between.</summary>
    void Market(Node3D parent, Rect2 block, RandomNumberGenerator rng)
    {
        var c = block.GetCenter();
        var b = new Builder(parent, At(c.X, c.Y), 0f);
        float hw = block.Size.X / 2f - 2f, hd = block.Size.Y / 2f - 2f;
        for (float z = -hd + 3f; z < hd - 3f; z += 8f)
        for (float x = -hw + 2f; x < hw - 2f; x += rng.RandfRange(4f, 6f))
        {
            b.LBox(x, 2.7f, z, new Vector3(3.6f, 0.08f, 3.4f), rng.Randf() < 0.5f ? Mats.Canvas : Mats.Adobe, solid: false);
            b.Prop(x, z - 0.8f, new Vector3(3f, 1.0f, 0.8f), 0f, Mats.Wood);
            if (rng.Randf() < 0.4f) b.Prop(x + rng.RandfRange(-1f, 1f), z + 0.8f, new Vector3(0.9f, 0.9f, 0.9f), rng.RandfRange(0f, 90f), Mats.Wood);
        }
        b.Points.Add(b.W(0f, 0f));
        b.Points.Add(b.W(hw * 0.5f, 0f));
        b.Points.Add(b.W(-hw * 0.5f, 0f));
        Record(b, c);
    }

    void Park(Node3D parent, Rect2 park, RandomNumberGenerator rng)
    {
        var c = park.GetCenter();
        var b = new Builder(parent, At(c.X, c.Y), 0f);
        int n = rng.RandiRange(3, 7);
        for (int i = 0; i < n; i++)
            b.Prop(rng.RandfRange(-park.Size.X / 2f + 3f, park.Size.X / 2f - 3f), rng.RandfRange(-park.Size.Y / 2f + 3f, park.Size.Y / 2f - 3f),
                   new Vector3(1.8f, 0.5f, 0.5f), rng.RandfRange(0f, 180f), Mats.Wood);
        if (rng.Randf() < 0.5f) b.Prop(0f, 0f, new Vector3(4f, 0.7f, 4f), 0f, Mats.Stone);
        b.Points.Add(b.W(0f, 0f));
        Record(b, c);
    }

    /// <summary>Parked and burnt-out cars along the kerbs, and the odd barricade: cover in the street.</summary>
    void StreetClutter(Node3D parent, RandomNumberGenerator rng)
    {
        var b = new Builder(parent, At(0f, 0f), 0f);
        void Along(float pos, float width, bool alongZ)
        {
            float centre = alongZ ? Center.Y : Center.X;
            for (float t = centre - Radius; t < centre + Radius; t += rng.RandfRange(18f, 45f))
            {
                var p = alongZ ? new Vector2(pos + (rng.Randf() < 0.5f ? -1f : 1f) * (width / 2f - 1.3f), t)
                               : new Vector2(t, pos + (rng.Randf() < 0.5f ? -1f : 1f) * (width / 2f - 1.3f));
                if (!NearTown(p.X, p.Y, 6f, parks: false)) continue;
                if (Blocks.Any(r => r.Grow(1f).HasPoint(p))) continue;
                float yaw = (alongZ ? 0f : 90f) + rng.RandfRange(-8f, 8f);
                if (rng.Randf() < 0.85f)
                    b.Box(At(p.X, p.Y) + Vector3.Up * 0.75f, new Vector3(1.8f, 1.5f, 4.3f), yaw, rng.Randf() < 0.3f ? Mats.Metal : Mats.Truck);
                else
                    b.Box(At(p.X, p.Y) + Vector3.Up * 0.5f, new Vector3(3f, 1f, 0.8f), yaw + 90f, Mats.Sand);
            }
        }
        foreach (var s in XStreets) Along(s.Pos, s.Width, true);
        foreach (var s in ZStreets) Along(s.Pos, s.Width, false);
    }
}
