using Godot;

namespace Ridgeline;

/// <summary>
/// The navmesh knows nothing about vehicles (they move, and it's baked once), so a route runs straight through a
/// parked truck or a burnt-out tank, and whoever follows it walks into the hull, scrapes along it and ends up
/// stuck, sidestepping at random. This bends a route round any vehicle standing in its way (stopped, landed, or
/// a wreck): the shorter way round its footprint, with room for a man, by corners that are ground you can stand
/// on. Moving vehicles aren't obstacles for this: they'll be gone by the time you get there.
/// </summary>
public static class VehicleDetour
{
    /// <summary>A body's radius and a little room either side.</summary>
    const float Room = 0.75f;
    const int MaxBends = 6;

    struct Box
    {
        public Vector2 C, U, F; // centre, right and forward axes (unit)
        public float Hu, Hf;    // half extents along them, with room added
    }

    static readonly List<Box> _boxes = new();
    static readonly SphereShape3D _probe = new() { Radius = 0.28f };
    static readonly PhysicsShapeQueryParameters3D _sweep = new() { Shape = _probe, CollisionMask = Layers.World | Layers.Trees | Layers.Doors };

    /// <summary>
    /// Could a man walk straight from a to b, as far as walls, trees and doors go? (The corners are on standable
    /// ground, but the way to them needn't be: a truck parked by a wall leaves no way round on that side.)
    /// </summary>
    static bool Walkable(World3D world, Vector3 a, Vector3 b)
    {
        _sweep.Transform = new Transform3D(Basis.Identity, a + Vector3.Up * 1.0f);
        _sweep.Motion = b - a;
        var r = world.DirectSpaceState.CastMotion(_sweep);
        return !(r.Length > 0 && r[0] < 0.999f);
    }
    static readonly List<Vector3> _out = new();

    /// <summary>Is this vehicle something to walk round rather than past?</summary>
    static bool Standing(Vehicle v) =>
        GodotObject.IsInstanceValid(v) && (v.Destroyed || (v.Def.Air ? v.Landed : MathF.Abs(v.Speed) < 0.8f));

    static void Gather(Vector3 near, float within)
    {
        _boxes.Clear();
        foreach (var v in Vehicle.All)
        {
            if (!Standing(v)) continue;
            var p = v.GlobalPosition;
            float reach = within + MathF.Max(v.Def.Hull.X, v.Def.Hull.Z);
            if ((p.X - near.X) * (p.X - near.X) + (p.Z - near.Z) * (p.Z - near.Z) > reach * reach) continue;
            var b = v.GlobalBasis;
            var u = new Vector2(b.X.X, b.X.Z);
            var f = new Vector2(b.Z.X, b.Z.Z);
            if (u.LengthSquared() < 1e-4f || f.LengthSquared() < 1e-4f) continue;
            _boxes.Add(new Box
            {
                C = new Vector2(p.X, p.Z), U = u.Normalized(), F = f.Normalized(),
                Hu = v.Def.Hull.X * 0.5f + Room, Hf = v.Def.Hull.Z * 0.5f + Room,
            });
        }
    }

    /// <summary>Into box coordinates.</summary>
    static Vector2 Local(in Box b, Vector2 p)
    {
        var d = p - b.C;
        return new Vector2(d.Dot(b.U), d.Dot(b.F));
    }

    static bool Inside(in Box b, Vector2 p, float shrink = 0f)
    {
        var l = Local(b, p);
        return MathF.Abs(l.X) < b.Hu - shrink && MathF.Abs(l.Y) < b.Hf - shrink;
    }

    /// <summary>Does a→b pass through the box (a little inside its edge: running along the edge is fine)?</summary>
    static bool Crosses(in Box box, Vector2 a, Vector2 b, float shrink = 0.05f)
    {
        var la = Local(box, a);
        var lb = Local(box, b);
        float hx = box.Hu - shrink, hz = box.Hf - shrink;
        // Slabs: the part of the segment within |x| < hx and |z| < hz.
        float t0 = 0f, t1 = 1f;
        var d = lb - la;
        for (int axis = 0; axis < 2; axis++)
        {
            float s = axis == 0 ? la.X : la.Y, v = axis == 0 ? d.X : d.Y, h = axis == 0 ? hx : hz;
            if (MathF.Abs(v) < 1e-6f)
            {
                if (s <= -h || s >= h) return false;
                continue;
            }
            float ta = (-h - s) / v, tb = (h - s) / v;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = MathF.Max(t0, ta);
            t1 = MathF.Min(t1, tb);
            if (t0 >= t1) return false;
        }
        return t1 - t0 > 1e-4f;
    }

    static Vector2 Corner(in Box b, int i)
    {
        float su = i is 0 or 3 ? -1f : 1f, sf = i < 2 ? -1f : 1f;
        return b.C + b.U * (b.Hu * su) + b.F * (b.Hf * sf);
    }

    /// <summary>
    /// Bend <paramref name="path"/> round standing vehicles, over its stretch from <paramref name="from"/> (where
    /// the walker is) through the waypoints from <paramref name="start"/> on, up to <paramref name="horizon"/>
    /// metres along it. The same array if there was nothing in the way; else a new one, with the bends inserted
    /// where they belong (the walker's next waypoint is still at <paramref name="start"/>).
    /// </summary>
    public static Vector3[] Apply(Vector3[] path, Vector3 from, int start, float horizon, World3D world)
    {
        if (start >= path.Length || Vehicle.All.Count == 0) return path;
        Gather(from, horizon);
        if (_boxes.Count == 0) return path;
        _out.Clear();
        for (int i = 0; i < start; i++) _out.Add(path[i]);
        var prev = from;
        float walked = 0f;
        int bends = 0;
        for (int i = start; i < path.Length; i++)
        {
            var next = path[i];
            if (walked < horizon && bends < MaxBends)
            {
                var a = new Vector2(prev.X, prev.Z);
                var b = new Vector2(next.X, next.Z);
                var from3 = prev;
                for (int k = 0; k < _boxes.Count && bends < MaxBends; k++)
                {
                    var box = _boxes[k];
                    // Ending right against it (a door to get in by): nothing to go round.
                    if (Inside(box, b)) continue;
                    if (Inside(box, a))
                    {
                        // (Only if the way on runs into the hull itself: walking off along it or away is fine.)
                        if (!Crosses(box, a, b, Room)) continue;
                        // Starting within a body's width of it (a mortar's second man, standing by it): step out to
                        // the edge of that first, then round.
                        var l = Local(box, a);
                        bool acrossU = box.Hu - MathF.Abs(l.X) < box.Hf - MathF.Abs(l.Y);
                        var edge = acrossU ? new Vector2((l.X < 0f ? -1f : 1f) * (box.Hu + 0.05f), l.Y) : new Vector2(l.X, (l.Y < 0f ? -1f : 1f) * (box.Hf + 0.05f));
                        var o = box.C + box.U * edge.X + box.F * edge.Y;
                        if (!Standable(new Vector3(o.X, prev.Y, o.Y), world, out var step) || !Walkable(world, from3, step)) continue;
                        _out.Add(step);
                        bends++;
                        Prof.Count("detour:step out");
                        a = new Vector2(step.X, step.Z);
                        from3 = step;
                    }
                    if (!Crosses(box, a, b)) continue;
                    if (Round(box, from3, next, world, out var c1, out var c2, out int n))
                    {
                        _out.Add(c1);
                        if (n > 1) _out.Add(c2);
                        bends++;
                        Prof.Count("detour:bend");
                        from3 = n > 1 ? c2 : c1;
                        a = new Vector2(from3.X, from3.Z);
                    }
                    else Prof.Count("detour:no way round");
                }
            }
            _out.Add(next);
            walked += prev.DistanceTo(next);
            prev = next;
        }
        return bends == 0 ? path : _out.ToArray();
    }

    /// <summary>
    /// The shorter way from a to b round the box: one corner (a and b see each other's side) or two (round an
    /// end). Only corners on ground a man can stand on (not inside a wall, or on a roof); false if neither way works.
    /// </summary>
    static bool Round(in Box box, Vector3 a3, Vector3 b3, World3D world, out Vector3 c1, out Vector3 c2, out int n)
    {
        c1 = c2 = default;
        n = 0;
        var a = new Vector2(a3.X, a3.Z);
        var b = new Vector2(b3.X, b3.Z);
        float y = a3.Y;
        float best = float.MaxValue;
        Span<Vector3> corner = stackalloc Vector3[4];
        Span<bool> ok = stackalloc bool[4];
        for (int i = 0; i < 4; i++)
        {
            var c = Corner(box, i);
            ok[i] = Standable(new Vector3(c.X, y, c.Y), world, out corner[i]);
            // Not into the next vehicle along (two parked side by side).
            for (int k = 0; k < _boxes.Count && ok[i]; k++)
                if (_boxes[k].C != box.C && Inside(_boxes[k], new Vector2(corner[i].X, corner[i].Z))) ok[i] = false;
        }
        for (int i = 0; i < 4; i++)
        {
            if (!ok[i]) continue;
            var ci = new Vector2(corner[i].X, corner[i].Z);
            if (Crosses(box, a, ci)) continue;
            // One corner.
            if (!Crosses(box, ci, b))
            {
                float l = a.DistanceTo(ci) + ci.DistanceTo(b);
                if (l < best && Walkable(world, a3, corner[i]) && Walkable(world, corner[i], b3)) { best = l; c1 = corner[i]; n = 1; }
                continue;
            }
            // Two: on round to the neighbouring corner that sees b.
            for (int side = 0; side < 2; side++)
            {
                int j = side == 0 ? (i + 1) % 4 : (i + 3) % 4;
                if (!ok[j]) continue;
                var cj = new Vector2(corner[j].X, corner[j].Z);
                if (Crosses(box, cj, b)) continue;
                float l = a.DistanceTo(ci) + ci.DistanceTo(cj) + cj.DistanceTo(b);
                if (l < best && Walkable(world, a3, corner[i]) && Walkable(world, corner[i], corner[j]) && Walkable(world, corner[j], b3))
                { best = l; c1 = corner[i]; c2 = corner[j]; n = 2; }
            }
        }
        return n > 0;
    }

    static bool Standable(Vector3 p, World3D world, out Vector3 at)
    {
        at = Valley.Current is { } v && v.Nav.Finished ? v.Nav.ClosestPoint(p) : NavBaker.MapClosest(world.NavigationMap, p);
        return new Vector2(at.X - p.X, at.Z - p.Z).LengthSquared() < 0.5f * 0.5f && MathF.Abs(at.Y - p.Y) < 1.2f;
    }
}
