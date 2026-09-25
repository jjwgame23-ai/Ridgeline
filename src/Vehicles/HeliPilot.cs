using Godot;

namespace Ridgeline;

public enum HeliMode { Transit, Land, Attack }

/// <summary>
/// A bot flying a helicopter. An autopilot turns "go there at this height and speed"
/// into collective, cyclic and pedals: it works out the acceleration it wants, points
/// the rotor's thrust along it (that gives the attitude and the collective) and yaws
/// toward where it's going. It follows the terrain at a set height above whatever is
/// under and ahead of it.
/// - Transit: fly to the goal and hover there.
/// - Land: approach, slow, and settle onto the goal (a landing zone).
/// - Attack: gun runs on the goal: out to ~1.4 km, turn in, rockets at ~1 km, break
///   away before overflying the target, come round from a new direction.
/// </summary>
public static class HeliPilot
{
    public static void Tick(Bot b, Vehicle v, float dt)
    {
        var pos = v.GlobalPosition;
        var g = Effects.Ground;
        if (v.Goal is not Vector3 goal)
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
        float agl;               // wanted height over the terrain
        Vector3 face;            // where the nose should point
        switch (v.AirMode)
        {
            case HeliMode.Attack:
                (wantH, agl, face) = AttackRun(b, v, goal, pos);
                break;
            case HeliMode.Land:
            {
                // Slow first, then down: come over the LZ at a walking pace.
                float speed = MathF.Min(v.Def.AirSpeed * 0.8f, dist * 0.14f + 2f);
                wantH = dirTo * speed;
                agl = Mathf.Clamp((dist - 30f) * 0.3f, 0f, 70f);
                face = dist > 40f ? dirTo : -v.GlobalBasis.Z with { Y = 0f };
                break;
            }
            default:
            {
                float speed = MathF.Min(v.Def.AirSpeed * 0.85f, dist * 0.3f);
                wantH = dirTo * speed;
                agl = 70f;
                face = dist > 60f ? dirTo : -v.GlobalBasis.Z with { Y = 0f };
                v.Arrived = dist < v.ArriveRadius;
                break;
            }
        }

        // Near the edge of the map (a wall): turn back in, whatever we were doing.
        float edge = Valley.PlayHalf - 200f;
        if (MathF.Abs(pos.X) > edge || MathF.Abs(pos.Z) > edge)
            wantH = (-pos with { Y = 0f }).Normalized() * MathF.Max(wantH.Length(), 25f);

        // Terrain following: the ground here and a little way along our track, plus room for trees.
        float groundHere = g?.HeightAt(pos.X, pos.Z) ?? 0f;
        float groundAhead = groundHere;
        var track = wantH.LengthSquared() > 1f ? wantH.Normalized() : dirTo;
        if (g != null)
            foreach (float ahead in new[] { 40f, 100f, 180f, 300f, 450f })
            {
                // Landing: only the ground between here and the LZ matters.
                if (v.AirMode == HeliMode.Land && ahead > dist - 15f) break;
                var p = pos + track * ahead;
                groundAhead = MathF.Max(groundAhead, g.HeightAt(p.X, p.Z));
            }
        bool approach = v.AirMode == HeliMode.Land && dist > 45f;
        float floor = MathF.Max(groundHere, groundAhead) + (agl > 5f ? 25f : approach ? 12f : 0f);
        float wantAlt = MathF.Max(groundHere + agl, agl > 5f || approach ? floor : groundHere);
        float deficit = wantAlt - pos.Y;
        float climb = Mathf.Clamp(deficit * 0.5f, -7f, 13f);
        // Rising ground coming at us faster than we can climb over it: slow down until we're above it.
        if (deficit > 15f) wantH *= Mathf.Clamp(1f - (deficit - 15f) / 50f, 0.25f, 1f);
        // Last few metres of a landing: come down slowly.
        if (v.AirMode == HeliMode.Land && dist < 25f) { wantH *= 0.3f; climb = Mathf.Clamp((groundHere - pos.Y) * 0.4f, -2.2f, 1f); }

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

    /// <summary>The gunship's run: approach from ~1.4 km, rockets at ~1 km, break off at ~450 m.</summary>
    static (Vector3 wantH, float agl, Vector3 face) AttackRun(Bot b, Vehicle v, Vector3 target, Vector3 pos)
    {
        var aim = v.AttackPoint ?? target;
        var toT = (aim - pos) with { Y = 0f };
        float d = toT.Length();
        var dirT = d > 0.1f ? toT / d : Vector3.Forward;
        switch (v.AttackPhase)
        {
            case 0: // get out to the start of a run
            {
                var entry = Inside(target + v.RunFrom * 1400f);
                var toE = (entry - pos) with { Y = 0f };
                if (toE.Length() < 150f) v.AttackPhase = 1;
                return (toE.Normalized() * v.Def.AirSpeed * 0.8f, 120f, toE);
            }
            case 1: // run in, nose on the target
            {
                if (d < 1150f && d > 650f) FireRockets(v, aim, d);
                if (d < 450f)
                {
                    v.AttackPhase = 2;
                    // Break off to one side; the next run comes from somewhere else.
                    v.RunFrom = v.RunFrom.Rotated(Vector3.Up, Mathf.DegToRad(b.Crew.RandSign() * 110f));
                }
                return (dirT * v.Def.AirSpeed * 0.55f, 110f, dirT);
            }
            default: // break away
            {
                var exit = Inside(target + v.RunFrom * 1400f);
                var toX = (exit - pos) with { Y = 0f };
                if (toX.Length() < 400f) v.AttackPhase = 0;
                return (toX.Normalized() * v.Def.AirSpeed * 0.9f, 90f, toX);
            }
        }
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
        if (Mathf.RadToDeg(v.Forward.Normalized().AngleTo(want with { Y = v.Forward.Normalized().Y })) > 8f) return; // not lined up yet
        if (t.Loaded[0] <= 0) return;
        v.SalvoLeft = 6;
        v.NextSalvo = v.NowT + 6.0;
        v.SalvoAt = at;
    }
}
