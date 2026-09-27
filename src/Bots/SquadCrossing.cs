using Godot;

namespace Ridgeline;

/// <summary>The steps of crossing a danger area.</summary>
public enum Crossing { None, Halt, First, Second }

/// <summary>
/// Danger areas: the stretch of a route where anyone watching gets a clear shot at you: a
/// street or a road between buildings, a gap in a treeline, a field between two woods. Where
/// the route leaves cover for such a stretch and picks it up again on the far side, the
/// squad doesn't just walk out into it:
/// - Halt short of it, in cover. The leader goes up to the near edge and has a look; the
///   covering team spreads out along the near edge, watching left and right.
/// - The leader's team crosses at a sprint, all at once, and secures the far side (spread
///   out along it, a few metres in).
/// - The covering team crosses under the far side's cover. Then the march goes on.
/// Only near the enemy: a hostile objective within ~1.2 km, or a fight in the last minute
/// and a half. Out in open country with no cover to go from or to, there's no danger
/// "area", it's all open, and the march order (bounding overwatch) deals with it.
/// </summary>
public sealed partial class Squad
{
    public static int Crossings;
    static readonly bool NoCross = OS.GetCmdlineUserArgs().Contains("nocross"); // A/B testing

    public Crossing Cross { get; private set; }
    public Vector3 CrossNear, CrossFar;
    Vector3 _crossAxis;
    string _crossWhat = "";
    double _crossSince, _crossScanAt, _crossCooldown, _edgeAt = -1;

    public Vector3? CrossingGoal => Cross switch
    {
        Crossing.Halt => CrossNear,
        Crossing.First or Crossing.Second => CrossFar,
        _ => null,
    };

    /// <summary>The leader's team (and the leader) crosses first; without teams, alternate men.</summary>
    int CrossGroup(ICombatant m)
    {
        if (m == Leader) return 0;
        int t = TeamOf(m);
        if (Teams && t >= 0) return t;
        int k = 0;
        foreach (var x in Members)
        {
            if (x == m) break;
            if (x.Alive && x != Leader) k++;
        }
        return k % 2 == 0 ? 1 : 0; // the first man after the leader stays back to cover
    }

    public static int CrossingsDone, CrossingsAborted, SmokeCrossings;
    double _smokeUp = -1;
    double _crossStarted;

    void SetCross(Crossing c)
    {
        if (c == Crossing.None && Cross != Crossing.None)
        {
            bool done = Cross == Crossing.Second;
            if (done) CrossingsDone++; else CrossingsAborted++;
            if (DuelMode.Verbose) GD.Print($"[{Clock.Now:0}s] {Name} danger area {(done ? "crossed" : $"abandoned at {Cross}")} after {Clock.Now - _crossStarted:0} s");
        }
        if (c == Crossing.Halt) _crossStarted = Clock.Now;
        _crossSlots.Clear();
        Cross = c;
        _crossSince = Clock.Now;
        _edgeAt = -1;
        if (c == Crossing.None) _crossCooldown = Clock.Now + 25.0;
    }

    public void CancelCrossing()
    {
        if (Cross != Crossing.None) SetCross(Crossing.None);
    }

    /// <summary>The leader's side of it, from UpdateMarch: look ahead for one, and run the steps.</summary>
    bool UpdateCrossing(Bot lead)
    {
        double now = Clock.Now;
        if (Cross != Crossing.None)
        {
            if (Engaged || Phase != AssaultPhase.None || now - _crossSince > 45.0) { SetCross(Crossing.None); return false; }
            switch (Cross)
            {
                case Crossing.Halt:
                {
                    bool atEdge = ((lead.FeetPos - CrossNear) with { Y = 0f }).Length() < 3f;
                    if (!atEdge) break;
                    if (_edgeAt < 0)
                    {
                        _edgeAt = now;
                        // The enemy's close, or has been shooting: screen the crossing with smoke first.
                        _smokeUp = -1;
                        if (now - EngagedUntil < 90.0 || Objective is SiteObjective sso && sso.Center.DistanceTo(CrossNear) < 450f && Hostile?.Invoke(sso.Site, Team) == true)
                        {
                            var mid = (CrossNear + CrossFar) * 0.5f;
                            var threat = now - EngagedUntil < 90.0 ? ContactAt : Objective!.Center;
                            var toward = (threat - mid) with { Y = 0f };
                            var at = mid + (toward.LengthSquared() > 1f ? toward.Normalized() * 8f : Vector3.Zero);
                            if (CrossNear.DistanceTo(CrossFar) > 12f && lead.ThrowGrenadeAt(at, smoke: true))
                            {
                                _smokeUp = now;
                                SmokeCrossings++;
                                Comms.Say(lead, "Popping smoke! Wait for it to build...");
                            }
                        }
                    }
                    int n = 0, up = 0;
                    foreach (var m in Members)
                    {
                        if (!m.Alive || m == lead || CrossGroup(m) != 0) continue;
                        n++;
                        if (m.FeetPos.DistanceTo(CrossNear) < 12f) up++;
                    }
                    // A few seconds' look from the edge, the team closed up behind: go.
                    if (now - _edgeAt > 3.0 && (_smokeUp < 0 || now - _smokeUp > 5.5) && (up >= n * 0.6f || now - _edgeAt > 15.0))
                    {
                        SetCross(Crossing.First);
                        Comms.Say(lead, $"{Squad.TeamName(0)}, across the {_crossWhat} — go, go!");
                    }
                    break;
                }
                case Crossing.First:
                {
                    int n = 0, over = 0;
                    foreach (var m in Members)
                    {
                        if (!m.Alive || CrossGroup(m) != 0) continue;
                        n++;
                        if (m.FeetPos.DistanceTo(CrossFar) < 14f) over++;
                    }
                    if ((over >= n * 0.6f && lead.FeetPos.DistanceTo(CrossFar) < 5f) || now - _crossSince > 25.0)
                    {
                        SetCross(Crossing.Second);
                        Comms.Say(lead, $"Far side's clear. {Squad.TeamName(1)}, move!");
                    }
                    break;
                }
                case Crossing.Second:
                {
                    int n = 0, over = 0;
                    foreach (var m in Members)
                    {
                        if (!m.Alive || CrossGroup(m) != 1) continue;
                        n++;
                        if (m.FeetPos.DistanceTo(CrossFar) < 16f) over++;
                    }
                    if (over >= n * 0.6f || now - _crossSince > 25.0)
                    {
                        SetCross(Crossing.None);
                        Comms.Say(lead, "Everyone's across. Moving on.");
                    }
                    break;
                }
            }
            return Cross != Crossing.None;
        }

        // Looking ahead, every couple of seconds on the move.
        if (NoCross || now < _crossScanAt || now < _crossCooldown || Alive < 2 || Engaged || Phase != AssaultPhase.None || lead.Vel.LengthSquared() < 0.5f) return false;
        // Men on foot: not crews, not anyone riding.
        if (Kind is not (SquadKind.Rifle or SquadKind.Weapons or SquadKind.Recon or SquadKind.Engineer or SquadKind.AntiTank or SquadKind.Drone)) return false;
        if (Members.Any(m => m.Alive && m.Ride != null)) return false;
        _crossScanAt = now + 2.0;
        if (!NearEnemy(lead)) return false;
        if (FindDangerArea(lead) is not var (near, far, what)) return false;
        CrossNear = near;
        CrossFar = far;
        _crossAxis = ((far - near) with { Y = 0f }).Normalized();
        _crossWhat = what;
        Crossings++;
        SetCross(Crossing.Halt);
        Comms.Say(lead, $"Danger area — {what} ahead. Hold up. {Squad.TeamName(1)}, cover left and right.");
        if (DuelMode.Verbose) GD.Print($"[{now:0}s] {Name} danger area: {what}, {near.DistanceTo(far):0} m across, {lead.FeetPos.DistanceTo(near):0} m ahead");
        return true;
    }

    bool NearEnemy(Bot lead)
    {
        if (Clock.Now - EngagedUntil < 90.0) return true;
        if (Objective is SiteObjective so && Hostile?.Invoke(so.Site, Team) == true
            && ((Objective.Center - lead.FeetPos) with { Y = 0f }).Length() < 1200f) return true;
        return false;
    }

    /// <summary>
    /// Along the leader's planned route, the next 150 m: is there a stretch out in the open
    /// (seen from a long way off in several directions) with cover before it and after it?
    /// </summary>
    (Vector3 Near, Vector3 Far, string What)? FindDangerArea(Bot lead)
    {
        var pts = lead.PathPoints;
        int idx = lead.PathIndex;
        if (pts.Length == 0 || idx >= pts.Length) return null;
        var space = lead.GetWorld3D().DirectSpaceState;
        // Sample the route every 5 m.
        var samples = new List<Vector3> { lead.FeetPos };
        var prev = lead.FeetPos;
        float along = 0f, carry = 0f;
        for (int i = idx; i < pts.Length && along < 150f; i++)
        {
            var seg = pts[i] - prev;
            float len = seg.Length();
            float s = 5f - carry;
            while (s <= len && along + s < 150f)
            {
                samples.Add(prev + seg * (s / len));
                s += 5f;
            }
            carry = len - (s - 5f);
            along += len;
            prev = pts[i];
        }
        if (samples.Count < 4) return null;
        // Where we are has to be cover, or it's all open country (bounding overwatch's business).
        if (Exposed(space, samples[0]) || Exposed(space, samples[1])) return null;
        int start = -1;
        for (int i = 2; i < samples.Count && i < 14; i++) // it starts within ~65 m
            if (Exposed(space, samples[i])) { start = i; break; }
        if (start < 0) return null;
        // ...and ends where there are two covered samples in a row.
        int end = -1;
        for (int i = start + 1; i + 1 < samples.Count; i++)
            if (!Exposed(space, samples[i]) && !Exposed(space, samples[i + 1])) { end = i; break; }
        if (end < 0) return null;
        var near = samples[start - 1];
        var far = samples[Math.Min(end + 1, samples.Count - 1)];
        float width = samples[start].DistanceTo(samples[end]);
        if (width < 5f) return null;
        var v = Valley.Current;
        var mid = samples[(start + end) / 2];
        string what = v?.City?.Zone(mid.X, mid.Z) == 2 ? (width < 30f ? "street" : "square")
            : width < 20f ? "gap" : width < 60f ? "clearing" : "open ground";
        return (near, far, what);
    }

    /// <summary>
    /// Out in the open here: no wall or trees close by, and seen from a long way off in at
    /// least half the directions (a street you're crossing, not the pavement along it).
    /// </summary>
    static bool Exposed(PhysicsDirectSpaceState3D space, Vector3 p)
    {
        var v = Valley.Current;
        if (Terrain.Main?.TreesNear(p.X, p.Z) >= 6) return false;
        if (v != null && v.BuiltAround(p.X, p.Z, 1) >= 2) return false; // along a wall
        if (Surroundings.Indoors(space, p)) return false;
        var eye = p + Vector3.Up * 1.3f;
        int open = 0;
        for (int k = 0; k < 6; k++)
        {
            float a = k * Mathf.Tau / 6f;
            var to = eye + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * 60f;
            if (space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, to, Layers.World | Layers.Trees)).Count == 0) open++;
            if (open >= 3) return true;
            if (k - open >= 3) return false;
        }
        return false;
    }

    readonly Dictionary<ICombatant, (Crossing Step, Vector3 At)> _crossSlots = new();

    /// <summary>A man's place during a crossing (null: not crossing), worked out once per step.</summary>
    Vector3? CrossSlot(ICombatant b)
    {
        if (Cross == Crossing.None) return null;
        if (_crossSlots.TryGetValue(b, out var c) && c.Step == Cross) return c.At;
        var raw = RawCrossSlot(b);
        var at = Reachable(b, raw);
        _crossSlots[b] = (Cross, at);
        return at;
    }

    /// <summary>
    /// Pull a slot in off walls: from the edge point the slot hangs off, along the ground, stop a
    /// metre short of anything solid; then onto the navmesh.
    /// </summary>
    Vector3 Reachable(ICombatant b, (Vector3 Base, Vector3 Slot) raw)
    {
        if (b is not Node3D n) return raw.Slot;
        var w = n.GetWorld3D();
        var from = raw.Base + Vector3.Up * 1f;
        var to = raw.Slot with { Y = raw.Base.Y } + Vector3.Up * 1f;
        var hit = w.DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, Layers.World | Layers.Trees | Layers.Vehicles));
        var p = raw.Slot;
        if (hit.Count > 0)
        {
            var h = hit["position"].AsVector3();
            var dir = (to - from).Normalized();
            p = from + dir * MathF.Max(0f, from.DistanceTo(h) - 1.2f) - Vector3.Up * 1f;
        }
        var snapped = Valley.ClosestOnFoot(w, p + Vector3.Up * 0.5f);
        // Snapped somewhere else entirely (a roof, the other side of a wall): stay on the edge point.
        return ((snapped - p) with { Y = 0f }).Length() < 2f && MathF.Abs(snapped.Y - raw.Base.Y) < 2.5f ? snapped : raw.Base;
    }

    (Vector3 Base, Vector3 Slot) RawCrossSlot(ICombatant b)
    {
        int g = CrossGroup(b);
        var perp = _crossAxis.Cross(Vector3.Up);
        int k = 0;
        foreach (var m in Members)
        {
            if (m == b) break;
            if (m.Alive && m != Leader && CrossGroup(m) == g) k++;
        }
        float side = k % 2 == 0 ? -1f : 1f;
        int row = k / 2 + 1;
        bool across = g == 0 ? Cross is Crossing.First or Crossing.Second : Cross == Crossing.Second;
        if (across)
            // The far side: spread along it, a few metres in; the second team a little deeper.
            return (CrossFar, CrossFar + _crossAxis * (g == 0 ? 3f : 7f) + perp * (side * row * 4.5f));
        if (g == 0)
            // Waiting to go: closed up behind the leader at the edge, in cover.
            return (CrossNear, CrossNear - _crossAxis * (2f + k * 2.5f));
        // Near-side security: along the near edge, out to the flanks, watching up and down it.
        return (CrossNear, CrossNear - _crossAxis * 1.5f + perp * (side * (5f + row * 4f)));
    }

    public bool CrossingNow => Cross != Crossing.None;
}
