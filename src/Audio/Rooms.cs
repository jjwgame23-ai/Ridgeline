using Godot;

namespace Ridgeline;

/// <summary>An opening in a building's shell (a window or a doorway) that sound passes through.</summary>
public sealed class Portal
{
    public Vector3 Pos;   // centre of the opening
    public Vector3 Out;   // outward normal
    public Door? Door;    // null for a window (always open)
    public bool Open => Door == null || Door.IsOpen;
}

/// <summary>
/// An enterable building's interior as one acoustic space: a box in the building's own
/// frame (x0..x1, z0..z1, floor to ceiling), with the openings in its walls.
/// </summary>
public sealed class RoomBox
{
    public Vector3 Origin;
    public Basis Rot, Inv;
    public float X0, X1, Z0, Z1, Height;
    public readonly List<Portal> Portals = new();

    public bool Contains(Vector3 p)
    {
        var l = Inv * (p - Origin);
        return l.X > X0 + 0.05f && l.X < X1 - 0.05f && l.Z > Z0 + 0.05f && l.Z < Z1 - 0.05f && l.Y > -0.6f && l.Y < Height - 0.25f;
    }

    public float Volume => (X1 - X0) * (Z1 - Z0) * MathF.Max(Height, 2.5f);
    public Vector3 Center => Origin + Rot * new Vector3((X0 + X1) / 2f, Height / 2f, (Z0 + Z1) / 2f);
}

/// <summary>
/// Buildings as acoustic spaces. Sound between inside and outside (or between two
/// buildings) goes the way it really does: out through the openings. If the listener is
/// indoors, a shot outside is heard from the window or doorway it comes through, a
/// little quieter and a little later for the longer way round. With every way through
/// shut (doors closed, or no opening facing it) all that's left is what the walls let
/// through: a dull, heavy thump.
/// </summary>
public static class Rooms
{
    const float Cell = 32f;
    static readonly List<RoomBox> _all = new();
    static readonly Dictionary<(int, int), List<RoomBox>> _grid = new();

    public static int Count => _all.Count;

    public static void Clear()
    {
        _all.Clear();
        _grid.Clear();
    }

    public static RoomBox Add(Vector3 origin, Basis rot, float x0, float x1, float z0, float z1, float height)
    {
        var r = new RoomBox { Origin = origin, Rot = rot, Inv = rot.Inverse(), X0 = x0, X1 = x1, Z0 = z0, Z1 = z1, Height = height };
        _all.Add(r);
        // Register in every cell its footprint could touch.
        float rad = MathF.Sqrt(MathF.Max(x0 * x0, x1 * x1) + MathF.Max(z0 * z0, z1 * z1));
        for (int i = (int)MathF.Floor((origin.X - rad) / Cell); i <= (int)MathF.Floor((origin.X + rad) / Cell); i++)
        for (int j = (int)MathF.Floor((origin.Z - rad) / Cell); j <= (int)MathF.Floor((origin.Z + rad) / Cell); j++)
        {
            if (!_grid.TryGetValue((i, j), out var l)) _grid[(i, j)] = l = new List<RoomBox>();
            l.Add(r);
        }
        return r;
    }

    /// <summary>The building interior this point is in, if any.</summary>
    public static RoomBox? At(Vector3 p)
    {
        if (!_grid.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Z / Cell)), out var l)) return null;
        foreach (var r in l) if (r.Contains(p)) return r;
        return null;
    }

    public readonly record struct Route(Vector3 At, float Length, int Portals, bool ThroughClosedDoor);

    /// <summary>
    /// The shortest way sound gets from a source to the listener through openings, when
    /// one or both of them is indoors: via an opening of the source's building, then an
    /// opening of the listener's (a straight line between each, clear of walls and hills).
    /// Returns where the listener hears it come from (the last opening) and how far it
    /// travelled. Closed doors count, but as a heavy loss.
    /// </summary>
    public static Route? Find(PhysicsDirectSpaceState3D space, RoomBox? src, Vector3 s, RoomBox? lis, Vector3 l)
    {
        const int Take = 5;
        var sp = src == null ? new List<Portal> { null! } : src.Portals.OrderBy(p => p.Pos.DistanceTo(s) + p.Pos.DistanceTo(l)).Take(Take).ToList();
        var lp = lis == null ? new List<Portal> { null! } : lis.Portals.OrderBy(p => p.Pos.DistanceTo(l) + p.Pos.DistanceTo(s)).Take(Take).ToList();
        Route? best = null;
        float bestScore = float.MaxValue;
        int rays = 0;
        foreach (var a in sp)
        foreach (var b in lp)
        {
            if (rays > 12) break;
            // Outside points of each opening (half a metre out), and the outdoor leg between them.
            var aOut = a == null ? s : a.Pos + a.Out * 0.5f;
            var bOut = b == null ? l : b.Pos + b.Out * 0.5f;
            float len = (a == null ? 0f : s.DistanceTo(a.Pos)) + aOut.DistanceTo(bOut) + (b == null ? 0f : b.Pos.DistanceTo(l));
            bool closed = (a != null && !a.Open) || (b != null && !b.Open);
            // An open way round, even a longer one, beats a closed door.
            float score = len + (closed ? 60f : 0f);
            if (score >= bestScore) continue;
            rays++;
            if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(aOut, bOut, Layers.World)).Count > 0) continue;
            int n = (a != null ? 1 : 0) + (b != null ? 1 : 0);
            bestScore = score;
            best = new Route(b != null ? b.Pos : a != null ? a.Pos : s, len, n, closed);
        }
        return best;
    }
}
