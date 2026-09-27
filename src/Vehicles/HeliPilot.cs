using Godot;

namespace Ridgeline;

public enum HeliMode { Transit, Land, Attack }

/// <summary>
/// A bot flying a helicopter. An autopilot turns "go there at this height and speed"
/// into collective, cyclic and pedals: it works out the acceleration it wants, points
/// the rotor's thrust along it (that gives the attitude and the collective) and yaws
/// toward where it's going. It flies at a set height over the tops of whatever is under
/// and ahead of it (trees and buildings, not just the ground): near the fighting, low,
/// nap of the earth, so the ground hides it.
/// - Transit: fly to the goal and hover there.
/// - Land: approach low, slow, and settle onto the goal (a landing zone).
/// - Attack: pop-up attacks from battle positions 1.2-1.8 km out (see AttackRun).
/// </summary>
public static class HeliPilot
{
    public static void Tick(Bot b, Vehicle v, float dt)
    {
        var pos = v.GlobalPosition;
        var g = Effects.Ground;
        // Tail rotor gone: it's spinning, and it can't fight or carry anyone like this. Whatever the orders, put it
        // down now, here; once down, it stays down.
        if ((v.Immobile ? null : v.Goal) is not Vector3 goal)
        {
            // No orders: sit on the ground (or put it down right here).
            if (v.Landed) { v.Collective = 0f; v.CyclicPitch = v.CyclicRoll = v.Pedal = 0f; return; }
            goal = pos;
            v.AirMode = HeliMode.Land;
        }
        goal = Inside(goal);
        var flat = (goal - pos) with { Y = 0f };
        float dist = flat.Length();
        var dirTo = dist > 0.1f ? flat / dist : -v.GlobalBasis.Z with { Y = 0f };

        if (v.Landed && v.AirMode == HeliMode.Land && dist < 30f) { v.Collective = 0f; v.CyclicPitch = v.CyclicRoll = v.Pedal = 0f; v.Arrived = true; return; }
        if (v.Boarding) { v.Collective = v.Landed ? 0f : 0.4f; v.CyclicPitch = v.CyclicRoll = v.Pedal = 0f; return; }
        v.Arrived = false;

        Vector3 wantH;           // wanted horizontal velocity
        float agl;               // wanted height over the tops of what's under and ahead (trees, buildings, ground)
        Vector3 face;            // where the nose should point
        switch (v.AirMode)
        {
            case HeliMode.Attack:
                (wantH, agl, face) = AttackRun(b, v, goal, pos);
                break;
            case HeliMode.Land:
            {
                // Slow first, then down: come over the LZ at a walking pace. In low (an approach at 70 m
                // is a target for everyone within 2 km), unless it's a long way off still.
                float speed = MathF.Min(v.Def.AirSpeed * 0.8f, dist * 0.14f + 2f);
                wantH = dirTo * speed;
                agl = dist > 3000f ? 45f : Mathf.Clamp((dist - 30f) * 0.3f, 0f, Noe);
                face = dist > 40f ? dirTo : -v.GlobalBasis.Z with { Y = 0f };
                break;
            }
            default:
            {
                float speed = MathF.Min(v.Def.AirSpeed * 0.85f, dist * 0.3f);
                wantH = dirTo * speed;
                // Well back from where it's going, a little height; into it, low.
                agl = dist > 3000f ? 45f : Noe;
                face = dist > 60f ? dirTo : -v.GlobalBasis.Z with { Y = 0f };
                v.Arrived = dist < v.ArriveRadius;
                break;
            }
        }

        // Near the edge of the map (a wall): turn back in, whatever we were doing.
        float edge = Valley.PlayHalf - 200f;
        if (MathF.Abs(pos.X) > edge || MathF.Abs(pos.Z) > edge)
            wantH = (-pos with { Y = 0f }).Normalized() * MathF.Max(wantH.Length(), 25f);

        // Over the tops: the ground, trees and buildings here and far enough along our track to climb over them
        // in time at this speed. (It used to keep 25 m over the highest ground anywhere in the next 450 m, and
        // didn't know about trees or buildings: high over every valley, in sight of everything.)
        float groundHere = g?.HeightAt(pos.X, pos.Z) ?? 0f;
        var track = wantH.LengthSquared() > 1f ? wantH.Normalized() : dirTo;
        bool landing = v.AirMode == HeliMode.Land;
        float top = Tops(v, pos, track, v.AirSpeed, landing ? dist - 15f : float.MaxValue, groundHere);
        bool approach = landing && dist > 45f;
        float wantAlt = landing && !approach ? groundHere + agl : top + MathF.Max(agl, approach ? 6f : 0f);
        float deficit = wantAlt - pos.Y;
        float climb = Mathf.Clamp(deficit * 0.5f, -7f, 13f);
        // Rising ground coming at us faster than we can climb over it: slow down until we're above it.
        if (deficit > 15f) wantH *= Mathf.Clamp(1f - (deficit - 15f) / 50f, 0.25f, 1f);
        // Last few metres of a landing: come down slowly.
        if (v.AirMode == HeliMode.Land && dist < 25f) { wantH *= 0.3f; climb = Mathf.Clamp((groundHere - pos.Y) * 0.4f, -2.2f, 1f); }
        // Tail rotor gone: down briskly (it's spinning, and anyone who shot it is still shooting), easing off only
        // for the last few metres. (At a normal landing's 2 m/s, from 90 m, it was shot down on the way.)
        if (v.Immobile) climb = Mathf.Clamp((groundHere - pos.Y) * 0.4f, -6f, 1f);

        var want = new Vector3(wantH.X, climb, wantH.Z);
        var vel = v.Velocity3;
        var accel = (want - vel) * 0.7f;
        var horiz = new Vector3(accel.X, 0f, accel.Z);
        if (horiz.Length() > 7f) horiz = horiz.Normalized() * 7f;
        accel = new Vector3(horiz.X, Mathf.Clamp(accel.Y, -5f, 6f), horiz.Z);
        // Drag to overcome at the speed we're doing.
        var hv = vel with { Y = 0f };
        var drag = hv * hv.Length() * (9.81f * 0.62f / (v.Def.AirSpeed * v.Def.AirSpeed));
        var thrust = accel + drag + Vector3.Up * 9.81f;
        float power = v.EngineHit ? 0.62f : 1f;
        v.Collective = Mathf.Clamp(thrust.Length() / (v.Def.Lift * power), 0f, 1f);

        // Point the rotor along the thrust: that's the attitude, in the heading frame.
        float yaw = v.GlobalRotation.Y;
        var local = new Basis(Vector3.Up, yaw).Inverse() * thrust.Normalized();
        float pitchDeg = Mathf.RadToDeg(MathF.Atan2(-local.Z, local.Y));
        float rollDeg = Mathf.RadToDeg(MathF.Atan2(local.X, local.Y));
        v.CyclicPitch = Mathf.Clamp(pitchDeg / 32f, -1f, 1f);
        v.CyclicRoll = Mathf.Clamp(rollDeg / 40f, -1f, 1f);

        // Nose where we're going (or at the target on a run).
        if (face.LengthSquared() > 0.01f)
        {
            var fwd = -v.GlobalBasis.Z with { Y = 0f };
            float err = Mathf.RadToDeg(fwd.Normalized().SignedAngleTo(face.Normalized(), Vector3.Up));
            v.Pedal = Mathf.Clamp(err / 25f, -1f, 1f);
        }
    }

    /// <summary>How high over the tops of trees and buildings it flies near the fighting: low enough that the ground hides it.</summary>
    public const float Noe = 11f;

    /// <summary>
    /// The highest top along our track (ground, a building, a wood's canopy), from here out to where we'd need to
    /// start climbing at this speed. A handful of rays straight down, a few times a second.
    /// </summary>
    static float Tops(Vehicle v, Vector3 pos, Vector3 track, float speed, float upTo, float groundHere)
    {
        if (Clock.Now < v.TopsAt && pos.DistanceTo(v.TopsFrom) < 30f && v.TopsDir.Dot(track) > 0.9f) return MathF.Max(v.TopsCached, groundHere);
        var space = v.GetWorld3D().DirectSpaceState;
        float look = Mathf.Clamp(speed * 4f, 40f, 240f); // four seconds' flight
        float top = TopAt(space, pos, groundHere);
        for (int k = 1; k <= 5; k++)
        {
            float ahead = look * k / 5f;
            if (ahead > upTo) break; // landing: only what's between here and the LZ
            top = MathF.Max(top, TopAt(space, pos + track * ahead, null));
        }
        v.TopsAt = Clock.Now + 0.25;
        v.TopsCached = top;
        v.TopsFrom = pos;
        v.TopsDir = track;
        return top;
    }

    /// <summary>How high the top of whatever stands at this spot is: a roof, a mast, a wood's canopy, or the ground.</summary>
    static float TopAt(PhysicsDirectSpaceState3D space, Vector3 p, float? ground)
    {
        float gh = ground ?? Effects.Ground?.HeightAt(p.X, p.Z) ?? 0f;
        float top = gh;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(new Vector3(p.X, gh + 90f, p.Z), new Vector3(p.X, gh - 1f, p.Z), Layers.World));
        if (hit.Count > 0) top = MathF.Max(top, hit["position"].AsVector3().Y);
        // A wood: its crowns (not solid, but no pilot flies through them) stand about 15 m up.
        if (Terrain.Main is { } t && t.TreesNear(p.X, p.Z) >= 6) top = MathF.Max(top, gh + 15f);
        return top;
    }

    static bool Clear(PhysicsDirectSpaceState3D space, Vector3 a, Vector3 b) =>
        space.IntersectRay(PhysicsRayQueryParameters3D.Create(a, b, Layers.World)).Count == 0;

    /// <summary>
    /// A gunship's attack, as they're flown against an enemy with air defence. Low along the ground, masked by it,
    /// to a battle position 1.2-1.8 km out; up just high enough to see the target; a rocket salvo (the gunner's
    /// cannon joins in by itself); and back down before anyone can get a gun on it. Then along to another position
    /// for the next one, never the same twice running. Shot at while up: straight back down, and on. The positions
    /// are chosen out of sight of any anti-aircraft gun we know of.
    /// (It used to make diving runs from 1.4 km at 55 m, in plain view of everything, into the last 450 m: most
    /// gunships were shot down in the first minute, by guns 600-800 m off.)
    /// </summary>
    static (Vector3 wantH, float agl, Vector3 face) AttackRun(Bot b, Vehicle v, Vector3 target, Vector3 pos)
    {
        var aim = v.AttackPoint ?? target;
        var toT = (aim - pos) with { Y = 0f };
        float d = toT.Length();
        double now = Clock.Now;
        if (v.BattlePos == null && (v.NoPositionSince < 0 || now - v.NoPositionSince > 4.0))
        {
            v.BattlePos = BattlePosition(v, aim, pos, out float pop);
            v.PopAgl = pop;
            v.AttackPhase = 0;
            v.PhaseSince = now;
            v.NoPositionSince = v.BattlePos == null ? now : -1;
        }
        if (v.BattlePos is not Vector3 bp)
        {
            // Nowhere to fire from that we'd live through: hang back, low, well out, and look again shortly.
            var back = Inside(aim + ((pos - aim) with { Y = 0f }).Normalized() * 2500f);
            var toB = (back - pos) with { Y = 0f };
            return (toB.LengthSquared() > 400f ? toB.Normalized() * MathF.Min(v.Def.AirSpeed * 0.6f, toB.Length() * 0.2f) : Vector3.Zero, Noe, toT);
        }
        var toP = (bp - pos) with { Y = 0f };
        float dp = toP.Length();
        bool shotAt = now - v.LastHit < 2.0 || now - v.MissileWarning < 3.0;
        switch (v.AttackPhase)
        {
            case 0: // low, to the battle position
            {
                if (dp < 40f) { v.AttackPhase = 1; v.PhaseSince = now; v.SeesTarget = false; v.SeeCheckAt = 0; }
                float speed = MathF.Min(v.Def.AirSpeed * 0.75f, dp * 0.25f + 4f);
                return (toP.Normalized() * speed, Noe, dp > 150f ? toP : toT);
            }
            default: // up, fire, down
            {
                var space = v.GetWorld3D().DirectSpaceState;
                if (now > v.SeeCheckAt)
                {
                    v.SeeCheckAt = now + 0.3;
                    v.SeesTarget = Clear(space, v.Center + Vector3.Up * 0.5f, aim + Vector3.Up * 2f);
                }
                if (v.SeesTarget) FireRockets(v, aim, d);
                // Done: shot at, or the salvo's away and had a moment to land, or it's been up too long, or it
                // can't see the target from here after all.
                bool fired = v.SalvoLeft == 0 && v.NextSalvo - 6.0 > v.PhaseSince && now - (v.NextSalvo - 6.0) > 1.5;
                if (shotAt || fired || now - v.PhaseSince > 10.0 || (!v.SeesTarget && now - v.PhaseSince > 6.0))
                {
                    v.LastBattlePos = bp;
                    v.BattlePos = null;
                    v.AttackPhase = 0;
                    if (shotAt && v.Driver is Bot pb) Comms.Say(pb, "Taking fire — breaking low!");
                }
                // Hold the spot, nose on the target, up only as far as it takes to see it.
                var hold = toP.LengthSquared() > 4f ? toP * 0.3f : Vector3.Zero;
                return (hold, v.PopAgl, toT);
            }
        }
    }

    /// <summary>
    /// Somewhere to fire from: on an arc 1.2-1.8 km from the target, on our side of it; hidden from it at our low
    /// height if possible (so we come up, fire and go back down behind the ground), with a view of it no more than
    /// 48 m up; out of sight of every enemy anti-aircraft gun we know about; not where we fired from last time.
    /// Null if there's nowhere like that. <paramref name="pop"/>: how high over the tops we must come up there.
    /// </summary>
    static Vector3? BattlePosition(Vehicle v, Vector3 aim, Vector3 pos, out float pop)
    {
        pop = Noe;
        var space = v.GetWorld3D().DirectSpaceState;
        var g = Effects.Ground;
        var away = (pos - aim) with { Y = 0f };
        away = away.LengthSquared() > 1f ? away.Normalized() : v.RunFrom;
        var threats = Radio.AirDefences(v.CrewTeam).Select(r => r.Vehicle is { } av && GodotObject.IsInstanceValid(av) ? av.Center : r.Pos).ToList();
        var eye = aim + Vector3.Up * 2f;
        Vector3? best = null;
        float bestScore = float.MinValue;
        for (int k = 0; k < 14; k++)
        {
            float ang = Mathf.DegToRad((float)(Random.Shared.NextDouble() * 140.0 - 70.0));
            float r = 1200f + (float)Random.Shared.NextDouble() * 600f;
            var c = Inside(aim + away.Rotated(Vector3.Up, ang) * r);
            if (v.LastBattlePos is Vector3 last && ((c - last) with { Y = 0f }).Length() < 300f) continue;
            float gh = g?.HeightAt(c.X, c.Z) ?? 0f;
            float top = TopAt(space, c, gh);
            float need = -1f;
            foreach (float h in new[] { Noe, 22f, 34f, 48f })
                if (Clear(space, new Vector3(c.X, top + h, c.Z), eye)) { need = h; break; }
            if (need < 0f) continue;
            var up = new Vector3(c.X, top + need, c.Z);
            bool seenByAa = false;
            foreach (var aa in threats)
                if (aa.DistanceTo(up) < 5000f && Clear(space, aa + Vector3.Up * 3f, up)) { seenByAa = true; break; }
            if (seenByAa) continue;
            float score = (need > Noe ? 3f : 0f) - need * 0.04f - MathF.Abs(r - 1500f) / 400f + (float)Random.Shared.NextDouble();
            if (score <= bestScore) continue;
            bestScore = score;
            best = new Vector3(c.X, top, c.Z);
            pop = need;
        }
        return best;
    }

    /// <summary>Keep turn points inside the map (its edge is a wall).</summary>
    static Vector3 Inside(Vector3 p)
    {
        float e = Valley.PlayHalf - 320f;
        return new(Mathf.Clamp(p.X, -e, e), p.Y, Mathf.Clamp(p.Z, -e, e));
    }

    /// <summary>A rocket salvo: the pods point along the nose, so the pilot aims the aircraft (a bot gets a little help).</summary>
    static void FireRockets(Vehicle v, Vector3 at, float dist)
    {
        int pods = Array.FindIndex(v.Def.Turrets.ToArray(), t => t.Fixed);
        if (pods < 0 || v.NowT < v.NextSalvo) return;
        var t = v.Turrets[pods];
        var want = (at - t.Muzzle.GlobalPosition).Normalized();
        if (dist < 500f || dist > 2300f) return; // too close to be safe, or too far to hit much
        if (Mathf.RadToDeg(v.Forward.Normalized().AngleTo(want with { Y = v.Forward.Normalized().Y })) > 8f) return; // not lined up yet
        if (t.Loaded[0] <= 0) return;
        v.SalvoLeft = 6;
        v.NextSalvo = v.NowT + 6.0;
        v.SalvoAt = at;
    }
}
