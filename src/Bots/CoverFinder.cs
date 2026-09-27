using Godot;

namespace Ridgeline;

public struct CoverSpot
{
    public Vector3 Pos;      // where to hide (crouched)
    public Vector3 PeekPos;  // where to stand to shoot: same spot for low cover, a step aside for a corner
    public bool Low;         // shoot over it by standing up
    public bool Side;        // shoot around it: lean (LeanDir) or step out (PeekPos)
    public float LeanDir;    // -1 lean left, 1 lean right, 0 = step out instead
}

/// <summary>
/// Finds cover dynamically: candidate points on the navmesh around the bot are
/// tested with rays from the threat's eye. Nothing is hand-placed, so any wall,
/// crate or truck works — and so will anything added to the map later.
/// </summary>
public static class CoverFinder
{
    const uint World = Layers.Solid; // characters don't count as cover; trees do
    static readonly SphereShape3D Probe = new() { Radius = 0.32f };
    static readonly float[] Radii = { 0f, 1.5f, 3f, 5f, 7.5f, 10f, 13f, 16f };

    /// <summary>Anything at all between a and b: for seeing, and for a clear line to shoot along.</summary>
    static bool Blocked(PhysicsDirectSpaceState3D s, Vector3 a, Vector3 b) =>
        s.IntersectRay(PhysicsRayQueryParameters3D.Create(a, b, World)).Count > 0;

    /// <summary>
    /// Would rounds from a stop before b: cover, not just something to hide behind? An inside wall, a door, a car
    /// or a shed hides a man, but a rifle round goes on through it (see Penetration), so the line is followed on
    /// through those to whatever's behind.
    /// </summary>
    static bool Stops(PhysicsDirectSpaceState3D s, Vector3 a, Vector3 b)
    {
        var from = a;
        var dir = (b - a).Normalized();
        float len = a.DistanceTo(b);
        for (int layer = 0; layer < 3; layer++)
        {
            var hit = s.IntersectRay(PhysicsRayQueryParameters3D.Create(from, b, World));
            if (hit.Count == 0) return false;
            if (!Penetration.RifleGoesThrough(hit, dir, out var exit)) return true;
            if ((exit - a).Dot(dir) >= len) return false;
            from = exit;
        }
        return true; // three layers of it: that'll do
    }

    public static bool Protected(Bot b, Vector3 feet, Vector3 threatEye, bool crouched) =>
        Stops(b.GetWorld3D().DirectSpaceState, threatEye, feet + Vector3.Up * (crouched ? 0.95f : 1.4f));

    /// <summary>Would lying flat here hide us from that eye (a fold in the ground, a kerb, a low wall)?</summary>
    public static bool ProtectedProne(Bot b, Vector3 feet, Vector3 threatEye) =>
        Stops(b.GetWorld3D().DirectSpaceState, threatEye, feet + Vector3.Up * 0.35f);

    static bool Reserved(Bot b, Vector3 p)
    {
        foreach (var c in Combatants.All)
            if (c is Bot mate && mate != b && mate.Alive && mate.Team == b.Team && mate.Brain.Cover is CoverSpot cs && cs.Pos.DistanceTo(p) < 1.5f)
                return true;
        return false;
    }

    /// <summary>
    /// Can a person stand here? Real ground (not a crate top or a roof) within reach of
    /// our height, not too steep, and body-sized free space above it. Physics queries
    /// only — navmesh queries are far too slow to make hundreds of on a big map.
    /// </summary>
    public static bool Standable(PhysicsDirectSpaceState3D s, Vector3 p, float refY, out Vector3 ground)
    {
        ground = p;
        var hit = s.IntersectRay(PhysicsRayQueryParameters3D.Create(p with { Y = refY + 2.5f }, p with { Y = refY - 3f }, World));
        if (hit.Count == 0) return false;
        if (hit["collider"].AsGodotObject() is not Node n || !n.IsInGroup("ground")) return false;
        if (hit["normal"].AsVector3().Y < 0.72f) return false;
        ground = hit["position"].AsVector3();
        var q = new PhysicsShapeQueryParameters3D { Shape = Probe, Transform = new Transform3D(Basis.Identity, ground + Vector3.Up * 0.95f), CollisionMask = World };
        return s.IntersectShape(q, 1).Count == 0;
    }

    public static CoverSpot? Find(Bot b, Vector3 threatEye, float maxR, RandomNumberGenerator rng)
    {
        using var _ = Prof.Time("cover");
        var space = b.GetWorld3D().DirectSpaceState;
        var origin = b.FeetPos;
        float threatDistNow = origin.DistanceTo(threatEye);
        CoverSpot? best = null;
        float bestScore = float.MinValue;
        float a0 = rng.Randf() * Mathf.Tau;

        foreach (float r in Radii)
        {
            if (r > maxR) break;
            int n = r == 0f ? 1 : 16;
            for (int i = 0; i < n; i++)
            {
                float a = a0 + i * Mathf.Tau / n;
                var p = origin + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * r;
                if (!Standable(space, p, origin.Y, out var snap)) continue;
                if (Reserved(b, snap)) continue;
                if (!Stops(space, threatEye, snap + Vector3.Up * 0.95f)) continue; // not covered even crouched

                var (low, side, lean, peek, dT) = Assess(space, snap, threatEye);

                float score = 10f - r * 0.55f;
                if (low || side) score += 6f;           // somewhere you can fight from, not just hide
                if (lean != 0f) score += 1.5f;          // and leaning exposes the least
                if (dT < 7f) score -= 10f;              // hugging the enemy's side of the wall
                if (dT > threatDistNow + 3f) score -= 2f * b.P.Aggression; // aggressive bots dislike giving ground
                score += rng.RandfRange(0f, 1.5f);
                if (score <= bestScore) continue;
                bestScore = score;
                best = new CoverSpot { Pos = snap, PeekPos = peek, Low = low, Side = side, LeanDir = lean };
            }
        }
        return best;
    }

    /// <summary>How can you fight from this spot: over it (low), around it (side: lean or step out), or not at all.</summary>
    static (bool Low, bool Side, float Lean, Vector3 Peek, float DistToThreat) Assess(PhysicsDirectSpaceState3D space, Vector3 snap, Vector3 threatEye)
    {
        var toThreat = threatEye - snap;
        toThreat.Y = 0f;
        float dT = toThreat.Length();
        toThreat /= MathF.Max(dT, 0.01f);

        bool low = !Blocked(space, threatEye, snap + Vector3.Up * 1.55f);
        bool side = false;
        float lean = 0f;
        var peek = snap;
        if (low) return (true, false, 0f, peek, dT);
        var perp = new Vector3(-toThreat.Z, 0f, toThreat.X); // "right" when facing the threat
        // A corner you can lean around beats one you have to step out from.
        foreach (float s in new[] { 1f, -1f })
        {
            var head = snap + Vector3.Up * 1.52f;
            var leaned = head + perp * (s * 0.4f);
            if (Blocked(space, head, leaned) || Blocked(space, threatEye, leaned)) continue;
            return (false, true, s, peek, dT);
        }
        foreach (float s in new[] { 1f, -1f })
        foreach (float off in new[] { 0.8f, 1.3f })
        {
            var want = snap + perp * (s * off);
            if (!Standable(space, want, snap.Y, out var q)) continue;
            if (Blocked(space, threatEye, q + Vector3.Up * 1.45f)) continue;
            return (false, true, 0f, q, dT);
        }
        return (false, side, lean, peek, dT);
    }

    /// <summary>
    /// The next bound: a covered spot 6-25 m closer to <paramref name="goal"/> (the
    /// enemy, or the objective), hidden from the threat, ideally one you can fight from.
    /// Fire and manoeuvre — this is how a squad takes ground instead of trading shots
    /// from the first wall it found.
    /// </summary>
    public static CoverSpot? FindForward(Bot b, Vector3 threatEye, Vector3 goal, RandomNumberGenerator rng, float maxStep = 26f)
    {
        using var _ = Prof.Time("cover");
        var space = b.GetWorld3D().DirectSpaceState;
        var origin = b.FeetPos;
        var to = goal - origin;
        to.Y = 0f;
        float dist = to.Length();
        if (dist < 10f) return null;
        float baseAng = MathF.Atan2(to.Z, to.X);
        CoverSpot? best = null;
        float bestScore = float.MinValue;
        for (int k = 0; k < 26; k++)
        {
            float ang = baseAng + Mathf.DegToRad(rng.RandfRange(-60f, 60f));
            float r = rng.RandfRange(MathF.Min(6f, maxStep * 0.4f), MathF.Max(MathF.Min(maxStep, dist - 4f), 5f));
            var p = origin + new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * r;
            if (!Standable(space, p, origin.Y, out var snap)) continue;
            float gain = dist - (goal - snap with { Y = goal.Y }).Length();
            if (gain < 5f) continue;
            if (Reserved(b, snap)) continue;
            if (!Stops(space, threatEye, snap + Vector3.Up * 0.95f)) continue; // must cover us crouched
            var (low, side, lean, peek, dT) = Assess(space, snap, threatEye);
            if (dT < 9f) continue;
            float score = gain * 0.35f + (low || side ? 6f : 0f) + (lean != 0f ? 1.5f : 0f) + rng.RandfRange(0f, 2f);
            // A straight run is quicker and doesn't need a path; walls in the way mean a detour.
            if (Blocked(space, origin + Vector3.Up * 0.6f, snap + Vector3.Up * 0.6f)) score -= 3f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = new CoverSpot { Pos = snap, PeekPos = peek, Low = low, Side = side, LeanDir = lean };
        }
        return best;
    }

    /// <summary>
    /// Open ground with no cover to bound to: a rush instead. A spot a few seconds' sprint on toward
    /// <paramref name="goal"/> to dash to and drop at, lying down: best a fold in the ground (hidden
    /// lying flat), else somewhere to fire from lying down. The way infantry cross open ground under
    /// fire: up, three to five seconds' run, down, fire, while the others cover.
    /// </summary>
    public static CoverSpot? FindRush(Bot b, Vector3 threatEye, Vector3 goal, RandomNumberGenerator rng, float minR, float maxR)
    {
        using var _ = Prof.Time("cover");
        var space = b.GetWorld3D().DirectSpaceState;
        var origin = b.FeetPos;
        var to = (goal - origin) with { Y = 0f };
        float dist = to.Length();
        if (dist < minR + 8f) return null;
        float baseAng = MathF.Atan2(to.Z, to.X);
        CoverSpot? best = null;
        float bestScore = float.MinValue;
        for (int k = 0; k < 12; k++)
        {
            float ang = baseAng + Mathf.DegToRad(rng.RandfRange(-35f, 35f));
            float r = rng.RandfRange(minR, maxR);
            var p = origin + new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * r;
            if (!Standable(space, p, origin.Y, out var snap)) continue;
            if (Reserved(b, snap)) continue;
            float gain = dist - (goal - snap with { Y = goal.Y }).Length();
            if (gain < minR * 0.6f) continue;
            bool hidden = Stops(space, threatEye, snap + Vector3.Up * 0.35f);        // dead ground, lying down
            bool canFire = !Blocked(space, snap + Vector3.Up * 0.38f, threatEye);   // or a line to fire along from there
            if (!hidden && !canFire) continue;
            float score = (hidden ? 4f : 0f) + (canFire ? 3f : 0f) + gain * 0.1f + rng.RandfRange(0f, 1.5f);
            if (score <= bestScore) continue;
            bestScore = score;
            best = new CoverSpot { Pos = snap, PeekPos = snap };
        }
        return best;
    }

    /// <summary>
    /// A spot off to one side of where the enemy was last seen, with a view of
    /// that spot, reachable by a path of reasonable length.
    /// </summary>
    public static Vector3? FindFlank(Bot b, Vector3 lkp, RandomNumberGenerator rng)
    {
        using var _ = Prof.Time("flank");
        var space = b.GetWorld3D().DirectSpaceState;
        var map = b.GetWorld3D().NavigationMap;
        var bearing = b.FeetPos - lkp;
        float baseAng = MathF.Atan2(bearing.Z, bearing.X);
        Vector3? best = null;
        float bestLen = float.MaxValue;

        // Cheap physics checks first; only the few nearest survivors get a (costly) path query.
        var cands = new List<Vector3>();
        for (int i = 0; i < 20; i++)
        {
            float side = rng.Randf() < 0.5f ? -1f : 1f;
            float ang = baseAng + side * Mathf.DegToRad(rng.RandfRange(50f, 110f));
            var p = lkp + new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * rng.RandfRange(12f, 30f);
            if (!Standable(space, p with { Y = lkp.Y }, lkp.Y + 3f, out var s)) continue;
            if (Blocked(space, s + Vector3.Up * 1.5f, lkp + Vector3.Up * 1.2f)) continue;
            cands.Add(s);
        }
        foreach (var snap in cands.OrderBy(c => c.DistanceTo(b.FeetPos)).Take(3))
        {
            var path = NavigationServer3D.MapGetPath(map, b.FeetPos, snap, true);
            if (path.Length == 0 || path[^1].DistanceTo(snap) > 1f) continue;
            float len = 0f;
            for (int k = 1; k < path.Length; k++) len += path[k].DistanceTo(path[k - 1]);
            if (len > 90f || len >= bestLen) continue;
            bestLen = len;
            best = snap;
        }
        return best;
    }
}
