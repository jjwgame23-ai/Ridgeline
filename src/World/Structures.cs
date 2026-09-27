using Godot;

namespace Ridgeline;

public static class Mats
{
    static StandardMaterial3D M(float r, float g, float b) => new() { AlbedoColor = new Color(r, g, b), Roughness = 1f };
    public static readonly StandardMaterial3D Plaster = M(0.72f, 0.67f, 0.58f), Plaster2 = M(0.62f, 0.6f, 0.55f),
        Concrete = M(0.55f, 0.55f, 0.52f), Wood = M(0.45f, 0.33f, 0.2f), Sand = M(0.52f, 0.47f, 0.34f),
        Stone = M(0.6f, 0.57f, 0.5f), Truck = M(0.3f, 0.35f, 0.27f), Roof = M(0.35f, 0.25f, 0.2f),
        Roof2 = M(0.3f, 0.3f, 0.32f), Metal = M(0.4f, 0.42f, 0.44f), Container = M(0.32f, 0.4f, 0.45f),
        Hesco = M(0.55f, 0.5f, 0.38f),
        // Desert town: sun-bleached mud brick and render.
        Adobe = M(0.76f, 0.66f, 0.5f), Adobe2 = M(0.7f, 0.6f, 0.46f), Adobe3 = M(0.8f, 0.74f, 0.62f),
        // City: rendered and brick apartment blocks, darker trim.
        Render = M(0.7f, 0.68f, 0.64f), Render2 = M(0.62f, 0.58f, 0.5f), Brick = M(0.52f, 0.33f, 0.26f), Panel = M(0.6f, 0.6f, 0.58f),
        Glass = M(0.12f, 0.14f, 0.16f), Asphalt = M(0.22f, 0.22f, 0.23f), Paving = M(0.5f, 0.48f, 0.45f),
        Dirt = M(0.55f, 0.46f, 0.34f), Green = M(0.3f, 0.45f, 0.4f), Canvas = M(0.62f, 0.5f, 0.36f);
}

/// <summary>
/// Collects the boxes a map is built from and merges them: one mesh per material per
/// 64 m chunk, and one static body per chunk holding all its box shapes. A city is tens
/// of thousands of boxes; as separate nodes that would be tens of thousands of draw calls.
/// Floors, stairs and roofs people can stand on go on a second body in the "ground" and
/// "floor" groups, so cover finding treats them as ground.
/// Also keeps a coarse grid of where something solid stands, so trees and rocks aren't
/// planted inside buildings.
/// </summary>
public sealed class Batcher
{
    public static Batcher? Current;
    const float ChunkSize = 64f, Cell = 4f;

    sealed class MeshData { public readonly List<Vector3> V = new(), N = new(); public readonly List<int> I = new(); }
    sealed class Chunk { public StaticBody3D? Solid, Floor; public readonly Dictionary<Material, MeshData> Meshes = new(); }

    readonly Node3D _parent;
    readonly Dictionary<(int, int), Chunk> _chunks = new();
    readonly Dictionary<Vector3I, BoxShape3D> _shapes = new();
    public readonly HashSet<(int, int)> Occupied = new();
    public int Boxes, Shapes;

    public Batcher(Node3D parent) { _parent = parent; }

    public bool IsOccupied(float x, float z) => Occupied.Contains(((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell)));

    public void Add(Transform3D xf, Vector3 size, Material mat, bool solid, bool floor)
    {
        var o = xf.Origin;
        var key = ((int)MathF.Floor(o.X / ChunkSize), (int)MathF.Floor(o.Z / ChunkSize));
        if (!_chunks.TryGetValue(key, out var ch)) _chunks[key] = ch = new Chunk();
        if (!ch.Meshes.TryGetValue(mat, out var md)) ch.Meshes[mat] = md = new MeshData();
        AppendBox(md, xf, size);
        Boxes++;
        if (!solid) return;

        var body = floor ? ch.Floor ??= MakeBody(true) : ch.Solid ??= MakeBody(false);
        var sk = new Vector3I((int)MathF.Round(size.X * 100f), (int)MathF.Round(size.Y * 100f), (int)MathF.Round(size.Z * 100f));
        if (!_shapes.TryGetValue(sk, out var shape)) _shapes[sk] = shape = new BoxShape3D { Size = size };
        var cs = new CollisionShape3D { Shape = shape, Transform = xf };
        Surfaces.Tag(cs, mat); // what a round or a boot finds here: timber, sheet metal, earth, else masonry
        Penetration.Tag(cs, mat, size); // and what a round gets through: an inside wall, a car, a shed...
        body.AddChild(cs);
        Shapes++;

        if (size.Y < 0.6f) return;
        // Footprint, conservatively (the box's bounding square).
        float r = 0.5f * MathF.Sqrt(size.X * size.X + size.Z * size.Z);
        for (float x = o.X - r; x <= o.X + r + Cell; x += Cell)
        for (float z = o.Z - r; z <= o.Z + r + Cell; z += Cell)
            Occupied.Add(((int)MathF.Floor(MathF.Min(x, o.X + r) / Cell), (int)MathF.Floor(MathF.Min(z, o.Z + r) / Cell)));
    }

    StaticBody3D MakeBody(bool floor)
    {
        var b = new StaticBody3D { CollisionLayer = 1 };
        _parent.AddChild(b);
        if (floor) { b.AddToGroup("ground"); b.AddToGroup("floor"); }
        return b;
    }

    static readonly Vector3[] Axes = { Vector3.Right, Vector3.Up, Vector3.Back };

    static void AppendBox(MeshData md, Transform3D xf, Vector3 size)
    {
        var h = size / 2f;
        for (int a = 0; a < 3; a++)
        for (int s = -1; s <= 1; s += 2)
        {
            var n = Axes[a] * s;
            var u = Axes[(a + 1) % 3];
            var v = Axes[(a + 2) % 3];
            float hn = h[a], hu = h[(a + 1) % 3], hv = h[(a + 2) % 3];
            var c = n * hn;
            int i0 = md.V.Count;
            md.V.Add(xf * (c - u * hu - v * hv));
            md.V.Add(xf * (c + u * hu - v * hv));
            md.V.Add(xf * (c + u * hu + v * hv));
            md.V.Add(xf * (c - u * hu + v * hv));
            var wn = (xf.Basis * n).Normalized();
            for (int k = 0; k < 4; k++) md.N.Add(wn);
            // Godot's front faces wind clockwise: the triangle's cross product points inward.
            var p0 = md.V[i0]; var p1 = md.V[i0 + 1]; var p2 = md.V[i0 + 2];
            bool flip = (p1 - p0).Cross(p2 - p0).Dot(wn) > 0f;
            if (flip) { md.I.Add(i0); md.I.Add(i0 + 2); md.I.Add(i0 + 1); md.I.Add(i0); md.I.Add(i0 + 3); md.I.Add(i0 + 2); }
            else { md.I.Add(i0); md.I.Add(i0 + 1); md.I.Add(i0 + 2); md.I.Add(i0); md.I.Add(i0 + 2); md.I.Add(i0 + 3); }
        }
    }

    /// <summary>Build the merged meshes (the bodies are already in the scene).</summary>
    public void Flush()
    {
        foreach (var ch in _chunks.Values)
        {
            var mesh = new ArrayMesh();
            foreach (var (mat, md) in ch.Meshes)
            {
                var arr = new Godot.Collections.Array();
                arr.Resize((int)Mesh.ArrayType.Max);
                arr[(int)Mesh.ArrayType.Vertex] = md.V.ToArray();
                arr[(int)Mesh.ArrayType.Normal] = md.N.ToArray();
                arr[(int)Mesh.ArrayType.Index] = md.I.ToArray();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
            }
            _parent.AddChild(new MeshInstance3D { Mesh = mesh });
        }
        _chunks.Clear();
    }
}

/// <summary>A place to fight from inside or on top of a building: a window, or a roof's parapet.</summary>
public readonly record struct Perch(Vector3 Pos, Vector3 Out, int Floor, bool Roof);

/// <summary>
/// Places buildings and props in a local frame (origin, rotation, ground height),
/// so the same village layout works anywhere on the map at any angle. Everything
/// solid is on layer 1, so cover finding, navmesh baking and bullets all see it.
/// </summary>
public sealed class Builder
{
    public const float Storey = 3.2f;

    public readonly Node3D Parent;
    public readonly Vector3 Origin;
    public readonly float YawDeg;
    public readonly List<Vector3> Points = new(); // places worth going to (for bots)
    public readonly List<Perch> Perches = new();  // windows and rooftops to shoot from
    readonly Basis _rot;

    public Builder(Node3D parent, Vector3 origin, float yawDeg)
    {
        Parent = parent;
        Origin = origin;
        YawDeg = yawDeg;
        _rot = Basis.FromEuler(new Vector3(0f, Mathf.DegToRad(yawDeg), 0f));
    }

    public Vector3 W(float x, float z) => Origin + _rot * new Vector3(x, 0f, z);
    public Vector3 L(float x, float y, float z) => Origin + _rot * new Vector3(x, y, z);
    public Vector3 Dir(float x, float z) => _rot * new Vector3(x, 0f, z);

    /// <summary>A child builder at local (x, z), turned by yaw relative to this one.</summary>
    public Builder Sub(float x, float z, float yawDeg, float? y = null)
    {
        var p = W(x, z);
        if (y is float yy) p.Y = yy;
        return new Builder(Parent, p, YawDeg + yawDeg);
    }

    /// <summary>Hand the points and perches of a sub-builder up to this one.</summary>
    public void Absorb(Builder b)
    {
        Points.AddRange(b.Points);
        Perches.AddRange(b.Perches);
    }

    public void Box(Vector3 center, Vector3 size, float worldYawDeg, Material mat, bool floor = false, bool solid = true) =>
        BoxXf(new Transform3D(Basis.FromEuler(new Vector3(0f, Mathf.DegToRad(worldYawDeg), 0f)), center), size, mat, floor, solid);

    public void BoxXf(Transform3D xf, Vector3 size, Material mat, bool floor = false, bool solid = true)
    {
        if (Batcher.Current is { } bt) { bt.Add(xf, size, mat, solid, floor); return; }
        if (!solid)
        {
            var mi = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat };
            Parent.AddChild(mi);
            mi.Transform = xf;
            return;
        }
        var body = new StaticBody3D { CollisionLayer = 1 };
        Parent.AddChild(body);
        body.Transform = xf;
        if (floor) { body.AddToGroup("ground"); body.AddToGroup("floor"); }
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = mat });
        var cs = new CollisionShape3D { Shape = new BoxShape3D { Size = size } };
        Surfaces.Tag(cs, mat);
        Penetration.Tag(cs, mat, size);
        body.AddChild(cs);
    }

    /// <summary>A box centred at local (x, y, z), in this builder's orientation.</summary>
    public void LBox(float x, float y, float z, Vector3 size, Material mat, bool floor = false, bool solid = true) =>
        Box(L(x, y, z), size, YawDeg, mat, floor, solid);

    /// <summary>A box resting on the ground at local (x, z). A little is sunk in so slopes leave no gaps.</summary>
    public void Prop(float x, float z, Vector3 size, float yawDeg, Material mat)
    {
        var p = W(x, z);
        Box(p + Vector3.Up * (size.Y / 2f - 0.1f), size + new Vector3(0f, 0.2f, 0f), YawDeg + yawDeg, mat);
    }

    public record struct Opening(float At, float Width, float Bottom, float Top);
    public static Opening Door(float at, float w = 1.4f) => new(at, w, 0f, 2.15f);
    public static Opening Window(float at) => new(at, 1.2f, 1.0f, 1.9f);
    public static Opening TallWindow(float at) => new(at, 1.2f, 1.0f, 2.2f); // a storey of a tall building
    public static Opening Port(float at) => new(at, 0.6f, 1.3f, 1.7f); // firing slit in a compound wall

    /// <summary>A straight wall between local points with holes cut for doors and windows.</summary>
    public void Wall(float ax, float az, float bx, float bz, float h, float thick, Material mat, params Opening[] openings) =>
        WallAt(0f, -0.2f, ax, az, bx, bz, h, thick, mat, openings);

    /// <summary>
    /// A wall starting <paramref name="y0"/> up (a storey), from <paramref name="bottom"/> to h
    /// relative to that. The solid part is cut into as few boxes as possible: the wall is
    /// split into horizontal bands at every opening's sill and head, each band into the runs
    /// between openings, and runs that line up in consecutive bands are merged.
    /// </summary>
    public void WallAt(float y0, float bottom, float ax, float az, float bx, float bz, float h, float thick, Material mat, Opening[] openings)
    {
        var a = W(ax, az);
        var d = W(bx, bz) - a;
        d.Y = 0f;
        float len = d.Length();
        if (len < 0.05f) return;
        var dir = d / len;
        float yaw = Mathf.RadToDeg(MathF.Atan2(-dir.Z, dir.X));

        var ys = new SortedSet<float> { bottom, h };
        foreach (var o in openings)
        {
            ys.Add(Mathf.Clamp(o.Bottom, bottom, h));
            ys.Add(Mathf.Clamp(o.Top, bottom, h));
        }
        var yl = ys.ToList();
        var sorted = openings.OrderBy(o => o.At).ToArray();
        var active = new List<(float S0, float S1, float Y0, float Y1)>();
        void Emit((float S0, float S1, float Y0, float Y1) r)
        {
            if (r.S1 - r.S0 < 0.02f || r.Y1 - r.Y0 < 0.02f) return;
            var mid = a + dir * ((r.S0 + r.S1) / 2f);
            Box(mid + Vector3.Up * (y0 + (r.Y0 + r.Y1) / 2f), new Vector3(r.S1 - r.S0, r.Y1 - r.Y0, thick), yaw, mat);
        }
        for (int k = 0; k + 1 < yl.Count; k++)
        {
            float ya = yl[k], yb = yl[k + 1];
            if (yb - ya < 0.005f) continue;
            var runs = new List<(float, float)>();
            float cursor = 0f;
            foreach (var o in sorted)
            {
                if (o.Bottom > ya + 0.001f || o.Top < yb - 0.001f) continue;
                float s0 = Mathf.Clamp(o.At - o.Width / 2f, 0f, len), s1 = Mathf.Clamp(o.At + o.Width / 2f, 0f, len);
                if (s0 > cursor) runs.Add((cursor, s0));
                cursor = MathF.Max(cursor, s1);
            }
            if (len > cursor) runs.Add((cursor, len));
            var next = new List<(float, float, float, float)>();
            foreach (var (s0, s1) in runs)
            {
                int m = active.FindIndex(r => MathF.Abs(r.S0 - s0) < 0.001f && MathF.Abs(r.S1 - s1) < 0.001f && MathF.Abs(r.Y1 - ya) < 0.001f);
                if (m >= 0) { var r = active[m]; active.RemoveAt(m); next.Add((r.S0, r.S1, r.Y0, yb)); }
                else next.Add((s0, s1, ya, yb));
            }
            foreach (var r in active) Emit(r);
            active = next;
        }
        foreach (var r in active) Emit(r);
    }

    /// <summary>
    /// The openings of an outside wall, for sound: each window and doorway becomes a portal
    /// of the building's acoustic space, and each doorway gets a real door (hinged at one
    /// side, swinging inward), about half of them left open.
    /// </summary>
    void Openings(RoomBox room, Opening[] os, bool alongX, float fixedC, float start, Vector3 n, float y)
    {
        foreach (var o in os)
        {
            float at = start + o.At;
            var local = alongX ? new Vector3(at, y + (o.Bottom + o.Top) / 2f, fixedC) : new Vector3(fixedC, y + (o.Bottom + o.Top) / 2f, at);
            var portal = new Portal { Pos = L(local.X, local.Y, local.Z), Out = Dir(n.X, n.Z) };
            if (o.Bottom < 0.5f)
            {
                // Hinge at one side of the doorway, the leaf along the wall.
                var along = alongX ? new Vector3(1, 0, 0) : new Vector3(0, 0, 1);
                var hingeL = (alongX ? new Vector3(at, y, fixedC) : new Vector3(fixedC, y, at)) - along * (o.Width / 2f - 0.02f);
                var hinge = L(hingeL.X, hingeL.Y, hingeL.Z);
                bool open = Mathf.PosMod(hinge.X * 3.13f + hinge.Z * 1.71f, 1f) < 0.5f;
                portal.Door = Ridgeline.Door.Make(Parent, hinge, Dir(along.X, along.Z), -Dir(n.X, n.Z), o.Width - 0.04f, o.Top - 0.05f, open, Mats.Wood);
            }
            room.Portals.Add(portal);
        }
    }

    /// <summary>
    /// A single-storey building at local (cx, cz), w along local X, d along local Z.
    /// Door and window sides are letters in the building's own frame: F front (-Z), B back, L, R.
    /// </summary>
    public void House(float cx, float cz, float w, float d, string doors, string windows, bool partition = false, Material? wall = null, Material? roof = null)
    {
        const float H = 3f, T = 0.25f;
        wall ??= Mats.Plaster;
        float x0 = cx - w / 2f, x1 = cx + w / 2f, z0 = cz - d / 2f, z1 = cz + d / 2f;

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

        var fo = For('F', w); var bo = For('B', w); var lo = For('L', d - T); var ro = For('R', d - T);
        Wall(x0, z0, x1, z0, H, T, wall, fo);
        Wall(x0, z1, x1, z1, H, T, wall, bo);
        Wall(x0, z0 + T / 2f, x0, z1 - T / 2f, H, T, wall, lo);
        Wall(x1, z0 + T / 2f, x1, z1 - T / 2f, H, T, wall, ro);
        var room = Rooms.Add(Origin, _rot, x0, x1, z0, z1, H);
        Openings(room, fo, true, z0, x0, new Vector3(0, 0, -1), 0f);
        Openings(room, bo, true, z1, x0, new Vector3(0, 0, 1), 0f);
        Openings(room, lo, false, x0, z0 + T / 2f, new Vector3(-1, 0, 0), 0f);
        Openings(room, ro, false, x1, z0 + T / 2f, new Vector3(1, 0, 0), 0f);
        if (partition) Wall(cx, z0 + T / 2f, cx, z1 - T / 2f, H, 0.2f, wall, Door((d - T) / 2f));
        Box(W(cx, cz) + Vector3.Up * (H + 0.1f), new Vector3(w + 0.4f, 0.2f, d + 0.4f), YawDeg, roof ?? Mats.Roof);
        Points.Add(W(cx, cz + (partition ? d * 0.25f : 0f)));
        foreach (var (side, n) in new[] { ('F', new Vector3(0, 0, -1)), ('B', new Vector3(0, 0, 1)), ('L', new Vector3(-1, 0, 0)), ('R', new Vector3(1, 0, 0)) })
            if (windows.Contains(side))
            {
                var at = new Vector3(cx, 0f, cz) + n * ((MathF.Abs(n.X) > 0 ? w : d) / 2f - 0.7f);
                Perches.Add(new Perch(L(at.X, 0f, at.Z), Dir(n.X, n.Z), 0, false));
            }
    }

    /// <summary>
    /// A building of one or more storeys at local (cx, cz): w wide (local X), d deep, front
    /// on -Z facing the street.
    /// - Enterable: real walls with windows every few metres, a door front (and back),
    ///   floors you can stand on, rooms divided by partitions with doorways, and a stair
    ///   (a 30° ramp, 1.4 m wide) up every storey in a shaft along the left wall. The
    ///   ramps stack in the same shaft, each landing at the far end, so you walk back
    ///   along the landing to the next flight. With roof access the last flight comes out
    ///   on a flat roof behind a chest-high parapet.
    /// - Not enterable: a solid shell with dark window panels (the bulk of a city, cheap).
    /// Records room points per floor and perches (every window, the roof edges).
    /// </summary>
    public void Block(float cx, float cz, float w, float d, int floors, Material wall, Material slab, bool enterable, bool roofAccess, RandomNumberGenerator rng,
                      bool backDoor = true, float winEvery = 3.4f, bool parapet = true)
    {
        const float T = 0.25f, S = Storey, SW = 1.8f, SL = 5.5f;
        float x0 = cx - w / 2f, x1 = cx + w / 2f, z0 = cz - d / 2f, z1 = cz + d / 2f;
        floors = Math.Max(1, floors);

        if (!enterable)
        {
            float hh = floors * S + 0.2f;
            LBox(cx, hh / 2f - 0.2f, cz, new Vector3(w, hh + 0.2f, d), wall);
            if (parapet) Parapet(x0, x1, z0, z1, hh, 0.7f, wall, solid: false);
            // Dark window panels on the long faces, standing just proud of the wall.
            int nw = Math.Max(1, (int)((w - 1f) / winEvery)), nd = Math.Max(1, (int)((d - 1f) / winEvery));
            for (int k = 0; k < floors; k++)
            {
                float y = k * S + 1.6f;
                for (int i = 0; i < nw; i++)
                {
                    float x = x0 + (i + 0.5f) * w / nw;
                    if (k == 0 && MathF.Abs(x - cx) < 1f) { LBox(x, 1.07f, z0 - 0.03f, new Vector3(1.3f, 2.15f, 0.08f), Mats.Wood, solid: false); continue; }
                    LBox(x, y, z0 - 0.03f, new Vector3(1.1f, 1.1f, 0.06f), Mats.Glass, solid: false);
                    LBox(x, y, z1 + 0.03f, new Vector3(1.1f, 1.1f, 0.06f), Mats.Glass, solid: false);
                }
                for (int i = 0; i < nd; i++)
                {
                    float z = z0 + (i + 0.5f) * d / nd;
                    LBox(x0 - 0.03f, y, z, new Vector3(0.06f, 1.1f, 1.1f), Mats.Glass, solid: false);
                    LBox(x1 + 0.03f, y, z, new Vector3(0.06f, 1.1f, 1.1f), Mats.Glass, solid: false);
                }
            }
            return;
        }

        bool stairs = (floors > 1 || roofAccess) && d >= 9.4f && w >= 7f;
        if (!stairs) { floors = 1; roofAccess = false; }
        float sx0 = x0 + T / 2f, sx1 = sx0 + SW, zs = z0 + T / 2f + 1.6f, ze = zs + SL;
        float rx0 = stairs ? sx1 + 1.3f : x0 + T / 2f; // the rooms start past the stair and its landing
        float zm = (z0 + z1) / 2f;

        Opening[] Windows(float len, int k, bool door)
        {
            var list = new List<Opening>();
            int n = Math.Max(1, (int)((len - 1f) / winEvery));
            if (door && k == 0) list.Add(Door(len / 2f));
            for (int i = 0; i < n; i++)
            {
                float at = (i + 0.5f) * len / n;
                if (door && k == 0 && MathF.Abs(at - len / 2f) < 1.5f) continue;
                list.Add(TallWindow(at));
            }
            return list.ToArray();
        }

        var room = Rooms.Add(Origin, _rot, x0, x1, z0, z1, floors * S - 0.2f);
        for (int k = 0; k < floors; k++)
        {
            float y = k * S, bot = k == 0 ? -0.2f : 0f;
            var front = Windows(w, k, true);
            var back = Windows(w, k, backDoor);
            var left = Windows(d - T, k, false);
            var right = Windows(d - T, k, false);
            WallAt(y, bot, x0, z0, x1, z0, S, T, wall, front);
            WallAt(y, bot, x0, z1, x1, z1, S, T, wall, back);
            WallAt(y, bot, x0, z0 + T / 2f, x0, z1 - T / 2f, S, T, wall, left);
            WallAt(y, bot, x1, z0 + T / 2f, x1, z1 - T / 2f, S, T, wall, right);
            Openings(room, front, true, z0, x0, new Vector3(0, 0, -1), y);
            Openings(room, back, true, z1, x0, new Vector3(0, 0, 1), y);
            Openings(room, left, false, x0, z0 + T / 2f, new Vector3(-1, 0, 0), y);
            Openings(room, right, false, x1, z0 + T / 2f, new Vector3(1, 0, 0), y);

            // Every window is somewhere to shoot from: stand 0.7 m back from it.
            void Win(Opening[] os, bool alongX, float fixedC, float start, Vector3 n)
            {
                foreach (var o in os)
                {
                    if (o.Bottom < 0.5f) continue;
                    float at = start + o.At;
                    var p = alongX ? new Vector3(at, y, fixedC - n.Z * 0.7f) : new Vector3(fixedC - n.X * 0.7f, y, at);
                    if (!alongX && n.X < 0f && p.Z > zs - 0.8f && p.Z < ze + 0.8f && stairs) continue; // on the stair
                    Perches.Add(new Perch(L(p.X, y + 0.05f, p.Z), Dir(n.X, n.Z), k, false));
                }
            }
            Win(front, true, z0, x0, new Vector3(0, 0, -1));
            Win(back, true, z1, x0, new Vector3(0, 0, 1));
            Win(left, false, x0, z0 + T / 2f, new Vector3(-1, 0, 0));
            Win(right, false, x1, z0 + T / 2f, new Vector3(1, 0, 0));

            // Floor of this storey (the ground floor gets a slab too: dead level, whatever the ground does).
            if (k == 0) LBox(cx, -0.1f, cz, new Vector3(w - T, 0.3f, d - T), slab, floor: true);

            // Rooms: a partition across the middle and, in a wide building, one down it; doorways in both.
            if (x1 - T / 2f - rx0 > 4f)
            {
                WallAt(y, 0f, rx0, zm, x1 - T / 2f, zm, S - 0.2f, 0.15f, wall, new[] { Door((x1 - T / 2f - rx0) * 0.3f, 1.2f) });
                if (w > 15f)
                {
                    float xm = (rx0 + x1) / 2f;
                    WallAt(y, 0f, xm, z0 + T / 2f, xm, zm, S - 0.2f, 0.15f, wall, new[] { Door((zm - z0) / 2f, 1.2f) });
                    WallAt(y, 0f, xm, zm, xm, z1 - T / 2f, S - 0.2f, 0.15f, wall, new[] { Door((z1 - zm) / 2f, 1.2f) });
                }
                float rxc = (rx0 + x1) / 2f;
                Points.Add(L(rxc, y + 0.05f, (z0 + zm) / 2f));
                Points.Add(L(rxc, y + 0.05f, (zm + z1) / 2f));
            }
            else Points.Add(L(cx, y + 0.05f, cz));

            // The slab above: the next floor, or the roof. Open over the stair shaft where a flight comes up through it.
            float top = (k + 1) * S;
            bool last = k == floors - 1;
            bool flight = !last || roofAccess;
            if (last)
            {
                if (flight)
                {
                    LBox((sx1 + x1 + 0.1f) / 2f, top - 0.1f, cz, new Vector3(x1 + 0.1f - sx1, 0.2f, d + 0.2f), slab, floor: true);
                    LBox((x0 - 0.1f + sx1) / 2f, top - 0.1f, (z0 - 0.1f + zs) / 2f, new Vector3(sx1 - x0 + 0.1f, 0.2f, zs - z0 + 0.1f), slab, floor: true);
                    LBox((x0 - 0.1f + sx1) / 2f, top - 0.1f, (ze + z1 + 0.1f) / 2f, new Vector3(sx1 - x0 + 0.1f, 0.2f, z1 + 0.1f - ze), slab, floor: true);
                }
                else LBox(cx, top - 0.1f, cz, new Vector3(w + 0.2f, 0.2f, d + 0.2f), slab, floor: roofAccess);
            }
            else
            {
                float ix0 = x0 + T / 2f, ix1 = x1 - T / 2f, iz0 = z0 + T / 2f, iz1 = z1 - T / 2f;
                LBox((sx1 + ix1) / 2f, top - 0.1f, cz, new Vector3(ix1 - sx1, 0.2f, iz1 - iz0), slab, floor: true);
                LBox((ix0 + sx1) / 2f, top - 0.1f, (iz0 + zs) / 2f, new Vector3(sx1 - ix0, 0.2f, zs - iz0), slab, floor: true);
                LBox((ix0 + sx1) / 2f, top - 0.1f, (ze + iz1) / 2f, new Vector3(sx1 - ix0, 0.2f, iz1 - ze), slab, floor: true);
            }
            if (flight && stairs)
            {
                float th = MathF.Atan2(S, SL), rl = MathF.Sqrt(S * S + SL * SL) + 0.3f;
                var basis = _rot * new Basis(Vector3.Right, -th);
                // Thick, so it rasterises solidly into the navmesh whatever its angle to the grid.
                var c = L((sx0 + sx1) / 2f, y + S / 2f - 0.3f / MathF.Cos(th), (zs + ze) / 2f);
                BoxXf(new Transform3D(basis, c), new Vector3(SW, 0.6f, rl), slab, floor: true);
                // A handrail along the open side: you get on at the foot and off at the head, not over the side.
                var rail = L(sx1 - 0.05f, y + S / 2f + 0.5f / MathF.Cos(th), (zs + ze) / 2f);
                BoxXf(new Transform3D(basis, rail), new Vector3(0.08f, 1.0f, rl - 0.3f), Mats.Metal);
                // And at the floor it arrives at, a rail along the stairwell so nobody steps off into it.
                if (!last) LBox(sx1 + 0.05f, top + 0.5f, (zs + ze) / 2f, new Vector3(0.1f, 1f, SL), Mats.Metal);
            }
        }

        float roofY = floors * S;
        if (roofAccess)
        {
            Parapet(x0, x1, z0, z1, roofY, 1.1f, wall, solid: true);
            // Rails round the open stairhead.
            LBox(sx1 + 0.05f, roofY + 0.5f, (zs + ze) / 2f, new Vector3(0.1f, 1f, SL), Mats.Metal);
            LBox((sx0 + sx1) / 2f, roofY + 0.5f, zs - 0.05f, new Vector3(SW, 1f, 0.1f), Mats.Metal);
            Points.Add(L((rx0 + x1) / 2f, roofY + 0.05f, cz));
            // The parapet is low cover all round.
            Perches.Add(new Perch(L(cx, roofY + 0.05f, z0 + 0.7f), Dir(0, -1), floors, true));
            Perches.Add(new Perch(L(cx, roofY + 0.05f, z1 - 0.7f), Dir(0, 1), floors, true));
            Perches.Add(new Perch(L(x1 - 0.7f, roofY + 0.05f, cz), Dir(1, 0), floors, true));
            Perches.Add(new Perch(L(x0 + 0.7f + SW, roofY + 0.05f, z1 - 1.5f), Dir(-1, 0), floors, true));
        }
        else if (parapet) Parapet(x0, x1, z0, z1, roofY, 0.5f, wall, solid: false);
    }

    void Parapet(float x0, float x1, float z0, float z1, float y, float h, Material mat, bool solid)
    {
        const float t = 0.2f;
        LBox((x0 + x1) / 2f, y + h / 2f, z0 + t / 2f - 0.1f, new Vector3(x1 - x0 + 0.2f, h, t), mat, solid: solid);
        LBox((x0 + x1) / 2f, y + h / 2f, z1 - t / 2f + 0.1f, new Vector3(x1 - x0 + 0.2f, h, t), mat, solid: solid);
        LBox(x0 + t / 2f - 0.1f, y + h / 2f, (z0 + z1) / 2f, new Vector3(t, h, z1 - z0), mat, solid: solid);
        LBox(x1 - t / 2f + 0.1f, y + h / 2f, (z0 + z1) / 2f, new Vector3(t, h, z1 - z0), mat, solid: solid);
    }

    /// <summary>Something that only needs to be seen (a dome, a spire): a mesh with no collision.</summary>
    public void Visual(Mesh mesh, Vector3 localPos, Material mat)
    {
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
        Parent.AddChild(mi);
        mi.Position = L(localPos.X, localPos.Y, localPos.Z);
        mi.RotationDegrees = new Vector3(0f, YawDeg, 0f);
    }
}

/// <summary>Settlement layouts. Each builds around its builder's origin and records points of interest.</summary>
public static class Settlements
{
    public static readonly string[] Names =
    {
        "Dubrava", "Kamen", "Lipovo", "Staro Selo", "Gorica", "Mlyn", "Brdo", "Polje",
        "Vrbnik", "Hrast", "Zabok", "Orlovac", "Ribnik", "Tisno", "Borje", "Jezero",
        "Zlatar", "Crni Vrh", "Visoko", "Lug",
    };
    public static readonly string[] DesertNames =
    {
        "Qasr Nahr", "Tell Safra", "Bir Sahel", "Umm Qasab", "Mansuriya", "Khirbat Zayd", "Jurf", "Hadithat",
        "Saqlawiya", "Kubaysa", "Rutba Wells", "Nukhayb", "Abu Ghar", "Ruwayshid",
    };

    /// <summary>Houses in a loose ring facing a small square, with yard walls, vehicles and clutter. ~35 m across.</summary>
    public static void Village(Builder b, RandomNumberGenerator rng)
    {
        int n = rng.RandiRange(5, 8);
        float a0 = rng.Randf() * Mathf.Tau;
        for (int i = 0; i < n; i++)
        {
            float ang = a0 + i * Mathf.Tau / n + rng.RandfRange(-0.2f, 0.2f);
            float r = rng.RandfRange(14f, 22f) + n;
            float w = rng.RandfRange(6f, 10f), d = rng.RandfRange(6f, 8f);
            // Local frame of this house: its front faces the square.
            var hb = new Builder(b.Parent, b.W(MathF.Cos(ang) * r, MathF.Sin(ang) * r), b.YawDeg + 90f - Mathf.RadToDeg(ang));
            if (i % 3 == 1)
            {
                // A two-storey house: stairs up, windows over the square.
                float bw = rng.RandfRange(8f, 11f), bd = rng.RandfRange(9.6f, 11f);
                hb.Block(0f, 0f, bw, bd, 2, rng.Randf() < 0.5f ? Mats.Plaster : Mats.Plaster2, Mats.Concrete, true, false, rng, parapet: false);
                hb.Visual(new PrismMesh { Size = new Vector3(bw + 0.6f, 2.2f, bd + 0.6f) }, new Vector3(0f, 2 * Builder.Storey + 1.1f, 0f), rng.Randf() < 0.5f ? Mats.Roof : Mats.Roof2);
            }
            else
            {
                string windows = new[] { "LR", "BL", "BR", "LRB" }[rng.RandiRange(0, 3)];
                hb.House(0f, 0f, w, d, doors: rng.Randf() < 0.5f ? "F" : "FB", windows: windows, partition: w > 8.5f,
                         wall: rng.Randf() < 0.5f ? Mats.Plaster : Mats.Plaster2, roof: rng.Randf() < 0.5f ? Mats.Roof : Mats.Roof2);
                // Yard wall off one side of the house.
                if (rng.Randf() < 0.6f)
                    hb.Wall(w / 2f + 0.5f, -d / 2f, w / 2f + 0.5f, -d / 2f - 7f, 1.3f, 0.4f, Mats.Stone);
            }
            b.Absorb(hb);
        }
        // The square.
        b.Points.Add(b.W(0f, 0f));
        b.Prop(rng.RandfRange(-5f, 5f), rng.RandfRange(-5f, 5f), new Vector3(2.5f, 2.7f, 6.5f), rng.RandfRange(0f, 180f), Mats.Truck);
        b.Prop(rng.RandfRange(-6f, 6f), rng.RandfRange(-6f, 6f), new Vector3(1.9f, 1.5f, 4.4f), rng.RandfRange(0f, 180f), Mats.Truck);
        for (int i = 0; i < 6; i++)
        {
            float s = rng.RandfRange(1f, 1.4f);
            b.Prop(rng.RandfRange(-10f, 10f), rng.RandfRange(-10f, 10f), new Vector3(s, s, s), rng.RandfRange(0f, 90f), Mats.Wood);
        }
        b.Prop(rng.RandfRange(-3f, 3f), 9f, new Vector3(3f, 0.95f, 0.7f), 0f, Mats.Sand);
    }

    /// <summary>
    /// A desert village: flat-roofed mud-brick houses, most with stairs to the roof, each
    /// in its own walled yard, round a dusty square with a well.
    /// </summary>
    public static void DesertVillage(Builder b, RandomNumberGenerator rng)
    {
        int n = rng.RandiRange(5, 8);
        float a0 = rng.Randf() * Mathf.Tau;
        var mats = new[] { Mats.Adobe, Mats.Adobe2, Mats.Adobe3 };
        for (int i = 0; i < n; i++)
        {
            float ang = a0 + i * Mathf.Tau / n + rng.RandfRange(-0.15f, 0.15f);
            float r = rng.RandfRange(17f, 24f) + n;
            var hb = new Builder(b.Parent, b.W(MathF.Cos(ang) * r, MathF.Sin(ang) * r), b.YawDeg + 90f - Mathf.RadToDeg(ang));
            float w = rng.RandfRange(8f, 12f), d = rng.RandfRange(9.6f, 11f);
            hb.Block(0f, 1f, w, d, rng.Randf() < 0.3f ? 2 : 1, mats[rng.RandiRange(0, 2)], Mats.Adobe2, true, rng.Randf() < 0.8f, rng);
            // Yard wall behind the house, with a gap.
            float yw = w + 4f, yd = 7f;
            hb.Wall(-yw / 2f, d / 2f + 1f, -yw / 2f, d / 2f + 1f + yd, 2.2f, 0.35f, Mats.Adobe2);
            hb.Wall(yw / 2f, d / 2f + 1f, yw / 2f, d / 2f + 1f + yd, 2.2f, 0.35f, Mats.Adobe2);
            hb.Wall(-yw / 2f, d / 2f + 1f + yd, yw / 2f, d / 2f + 1f + yd, 2.2f, 0.35f, Mats.Adobe2, Builder.Door(yw * 0.3f, 1.6f));
            b.Absorb(hb);
        }
        b.Points.Add(b.W(0f, 0f));
        b.Prop(0f, 0f, new Vector3(2.2f, 0.9f, 2.2f), 45f, Mats.Stone); // the well
        b.Prop(rng.RandfRange(-7f, 7f), rng.RandfRange(-7f, 7f), new Vector3(1.9f, 1.5f, 4.4f), rng.RandfRange(0f, 180f), Mats.Truck);
        for (int i = 0; i < 5; i++)
        {
            float s = rng.RandfRange(0.9f, 1.3f);
            b.Prop(rng.RandfRange(-10f, 10f), rng.RandfRange(-10f, 10f), new Vector3(s, s * 0.8f, s), rng.RandfRange(0f, 90f), Mats.Wood);
        }
    }

    /// <summary>A walled compound (2.4 m walls with gates and firing slits) around two buildings and a shed.</summary>
    public static void Compound(Builder b, RandomNumberGenerator rng, bool desert = false)
    {
        const float S = 20f, H = 2.4f, T = 0.45f;
        var wallMat = desert ? Mats.Adobe2 : Mats.Concrete;
        var slits = new[] { Builder.Port(6f), Builder.Port(14f), Builder.Port(26f), Builder.Port(34f) };
        b.Wall(-S, -S, S, -S, H, T, wallMat, slits.Append(new Builder.Opening(20f, 3.5f, 0f, H)).ToArray());
        b.Wall(-S, S, S, S, H, T, wallMat, slits.Append(new Builder.Opening(20f, 3.5f, 0f, H)).ToArray());
        b.Wall(-S, -S, -S, S, H, T, wallMat, slits);
        b.Wall(S, -S, S, S, H, T, wallMat, slits.Append(Builder.Door(20f)).ToArray());
        if (desert)
        {
            b.Block(-9f, -8f, 11f, 10f, 2, Mats.Adobe, Mats.Adobe2, true, true, rng, backDoor: true);
            b.Block(9f, 9f, 9f, 9.6f, 1, Mats.Adobe3, Mats.Adobe2, true, true, rng);
        }
        else
        {
            b.House(-9f, -8f, 10f, 8f, doors: "BR", windows: "FL", partition: true);
            b.House(9f, 9f, 8f, 7f, doors: "F", windows: "LRB");
        }
        // Open-fronted shed.
        b.Wall(4f, -15f, 16f, -15f, 3.2f, 0.25f, Mats.Metal);
        b.Wall(16f, -15f, 16f, -6f, 3.2f, 0.25f, Mats.Metal);
        b.Box(b.W(10f, -10.5f) + Vector3.Up * 3.3f, new Vector3(12.5f, 0.2f, 9.5f), b.YawDeg, Mats.Roof2);
        b.Prop(9f, -11f, new Vector3(2.5f, 2.7f, 6.5f), 90f, Mats.Truck);
        b.Prop(-6f, 6f, new Vector3(1.4f, 1.4f, 1.4f), 20f, Mats.Wood);
        b.Prop(-4.6f, 6.6f, new Vector3(1.2f, 1.2f, 1.2f), -10f, Mats.Wood);
        b.Prop(0f, -2f, new Vector3(3f, 0.95f, 0.7f), 30f, Mats.Sand);
        b.Points.Add(b.W(0f, 0f));
        b.Points.Add(b.W(10f, -10f));
        b.Points.Add(b.W(-15f, 15f));
    }

    /// <summary>A hilltop position: sandbag ring, a bunker with slits, hesco barriers.</summary>
    public static void Outpost(Builder b, RandomNumberGenerator rng)
    {
        for (int i = 0; i < 10; i++)
        {
            if (i == 2 || i == 7) continue; // two ways in
            float a = i * Mathf.Tau / 10f;
            b.Prop(MathF.Cos(a) * 9f, MathF.Sin(a) * 9f, new Vector3(5.5f, 1.0f, 0.8f), -Mathf.RadToDeg(a) + 90f, Mats.Sand);
        }
        // Bunker: thick walls with firing slits, a roof, one door.
        b.Wall(-2.5f, -2f, 2.5f, -2f, 2.2f, 0.5f, Mats.Concrete, Builder.Port(1.2f), Builder.Port(3.8f));
        b.Wall(-2.5f, 2f, 2.5f, 2f, 2.2f, 0.5f, Mats.Concrete, Builder.Door(2.5f, 1.2f));
        b.Wall(-2.5f, -1.75f, -2.5f, 1.75f, 2.2f, 0.5f, Mats.Concrete, Builder.Port(1.75f));
        b.Wall(2.5f, -1.75f, 2.5f, 1.75f, 2.2f, 0.5f, Mats.Concrete, Builder.Port(1.75f));
        b.Box(b.W(0f, 0f) + Vector3.Up * 2.35f, new Vector3(5.8f, 0.3f, 4.8f), b.YawDeg, Mats.Concrete);
        for (int i = 0; i < 4; i++)
        {
            float a = rng.Randf() * Mathf.Tau;
            float r = rng.RandfRange(14f, 18f);
            b.Prop(MathF.Cos(a) * r, MathF.Sin(a) * r, new Vector3(3f, 1.8f, 1.1f), rng.RandfRange(0f, 180f), Mats.Hesco);
        }
        // A watchtower: a platform on legs with a ladder-ramp, sandbagged.
        if (rng.Randf() < 0.6f)
        {
            var t = b.Sub(-6f, 5f, rng.RandfRange(0f, 360f));
            t.Block(0f, 0f, 7f, 9.6f, 1, Mats.Concrete, Mats.Concrete, true, true, rng, backDoor: false, winEvery: 2.6f);
            b.Absorb(t);
        }
        b.Points.Add(b.W(0f, 0f));
        b.Points.Add(b.W(6f, 0f));
        b.Points.Add(b.W(-6f, 3f));
    }

    /// <summary>A faction's rear base: containers, barriers, a flag.</summary>
    public static void Base(Builder b, Color flag)
    {
        for (int i = 0; i < 4; i++)
        {
            b.Prop(-12f + i * 8f, -10f, new Vector3(2.5f, 2.6f, 6f), 0f, Mats.Container);
            b.Prop(-12f + i * 8f, 12f, new Vector3(2.5f, 2.6f, 6f), 0f, Mats.Container);
        }
        for (int i = 0; i < 6; i++)
        {
            float a = i * Mathf.Tau / 6f + 0.3f;
            b.Prop(MathF.Cos(a) * 22f, MathF.Sin(a) * 22f, new Vector3(4f, 1.8f, 1.1f), -Mathf.RadToDeg(a) + 90f, Mats.Hesco);
        }
        var p = b.W(0f, 0f);
        b.Box(p + Vector3.Up * 4f, new Vector3(0.12f, 8f, 0.12f), 0f, Mats.Metal);
        var cloth = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.8f, 1.1f, 0.03f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = flag, Roughness = 1f },
        };
        b.Parent.AddChild(cloth);
        cloth.Position = p + new Vector3(0.95f, 7.3f, 0f);
    }
}
