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
/// - Attack: pop-up attacks from battle positions 1.2-1.8 km out, further with missiles (see AttackRun).
/// Whatever the mode, it keeps clear of other aircraft (see Avoid).
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

        // A radar has us (see Vehicle.RadarLock), whatever we were doing: out of its sight the quickest way, to the
        // nearest spot low over the tops that it can't see, and wait there a little. (Popped up in a battle position,
        // straight down is quicker: that's handled there.) (Flying straight away from it, it still had us in sight
        // when it opened fire.)
        double now = Clock.Now;
        if (v.RadarLocked && !(v.AirMode == HeliMode.Attack && v.AttackPhase == 1) && v.RadarFrom is { } rf && GodotObject.IsInstanceValid(rf))
        {
            if (now >= v.EvadeUntil || v.EvadeFrom.DistanceTo(rf.GlobalPosition) > 50f)
            {
                Prof.Count("heli:evaded a radar");
                v.EvadeTo = EscapePoint(v, pos, rf.GlobalPosition);
                v.BattlePos = null; // and somewhere else to fire from, now we know it's there
            }
            v.EvadeUntil = now + 5.0;
            v.EvadeFrom = rf.GlobalPosition;
        }
        if (now < v.EvadeUntil)
        {
            var to = (v.EvadeTo - pos) with { Y = 0f };
            float dd = to.Length();
            wantH = dd > 3f ? to / dd * MathF.Min(v.Def.AirSpeed * 0.9f, dd * 0.3f + 2f) : Vector3.Zero;
            agl = 5f;
            face = dd > 40f ? to : (v.EvadeFrom - pos) with { Y = 0f };
            v.Task = "evading a radar";
        }

        // Lifting off: straight up first, clear of whatever stands round the pad or the LZ (a flagpole, containers,
        // trees), before going anywhere. (One tipped forward off its pad and flew into the base's flagpole.)
        if (v.LiftedFrom is Vector3 lf && ((pos - lf) with { Y = 0f }).LengthSquared() < 60f * 60f && pos.Y - lf.Y < 15f)
            wantH = wantH.LimitLength(2f + MathF.Max(0f, pos.Y - lf.Y) * 0.4f);

        // Near the edge of the map (a wall): turn back in, whatever we were doing. (Bar a base's pads, nearer it.)
        float edge = Valley.PlayHalf - (NearBase(pos, 300f) ? 100f : 200f);
        if (MathF.Abs(pos.X) > edge || MathF.Abs(pos.Z) > edge)
            wantH = (-pos with { Y = 0f }).Normalized() * MathF.Max(wantH.Length(), 25f);

        // Over the tops: the ground, trees and buildings here and far enough along our track to climb over them
        // in time at this speed. (It used to keep 25 m over the highest ground anywhere in the next 450 m, and
        // didn't know about trees or buildings: high over every valley, in sight of everything.)
        float groundHere = g?.HeightAt(pos.X, pos.Z) ?? 0f;
        var track = wantH.LengthSquared() > 1f ? wantH.Normalized() : dirTo;
        bool landing = v.AirMode == HeliMode.Land;
        float top = Tops(v, pos, track, v.AirSpeed, landing ? dist - 15f : float.MaxValue, groundHere);
        // Ground rising ahead faster than we can climb at this speed (about 9 m/s, held): slow down to what we can.
        // (Flat out up a hillside at 35 m/s, nose down to go faster still, three flew into it in one match.)
        if (v.TopsGrade > 0.05f) wantH = wantH.LimitLength(MathF.Max(9f / v.TopsGrade, 5f));
        bool approach = landing && dist > 45f;
        // (The last 45 m of a landing went by the ground under it alone, and flew one into a roof short of its LZ.)
        float wantAlt = landing && !approach ? MathF.Max(groundHere + agl, top + 2f) : top + MathF.Max(agl, approach ? 6f : 0f);
        Avoid(v, pos, ref wantH, ref wantAlt);
        float deficit = wantAlt - pos.Y;
        float climb = Mathf.Clamp(deficit * 0.5f, -7f, 13f);
        // Rising ground coming at us faster than we can climb over it: slow down until we're above it.
        if (deficit > 15f) wantH *= Mathf.Clamp(1f - (deficit - 15f) / 50f, 0.25f, 1f);
        // Last few metres of a landing: come down slowly.
        if (v.AirMode == HeliMode.Land && dist < 25f) { wantH *= 0.3f; climb = Mathf.Clamp((groundHere - 0.5f - pos.Y) * 0.4f, -2.2f, 1f); }
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
        float top = TopAt(space, pos, groundHere), grade = 0f;
        for (int k = 1; k <= 5; k++)
        {
            float ahead = look * k / 5f;
            if (ahead > upTo) break; // landing: only what's between here and the LZ
            float h = TopAt(space, pos + track * ahead, null);
            top = MathF.Max(top, h);
            // How steeply we'd have to climb to be low over it by the time we're there.
            grade = MathF.Max(grade, (h + Noe - pos.Y) / ahead);
        }
        v.TopsGrade = grade;
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
        // A base's flagpole, 8 m, in the middle of the way in to the pads: too thin for a ray to find, and every pilot
        // knows it's there. (Two clipped it coming home.)
        if (NearBase(p, 6f)) top = MathF.Max(top, gh + 9f);
        return top;
    }

    static bool Clear(PhysicsDirectSpaceState3D space, Vector3 a, Vector3 b) =>
        space.IntersectRay(PhysicsRayQueryParameters3D.Create(a, b, Layers.World)).Count == 0;

    /// <summary>
    /// See and avoid: another aircraft near us and closing, set to pass within two rotor discs in the next ten seconds.
    /// By the rules of the air, the one that has the other on its right gives way (head on, or overtaking, it's us):
    /// it slows and turns right, to pass behind the other, and climbs to pass over it (unless the other is giving way
    /// too and it's the one going over). Whoever's landing, or hovering, is kept clear of; a bot gives way to anyone
    /// it can't expect to (the player, an aircraft nobody's flying). (Two went up side by side from one base at the
    /// start of a match, crossed 12 m up, and came down together.)
    /// </summary>
    static void Avoid(Vehicle v, Vector3 pos, ref Vector3 wantH, ref float wantAlt)
    {
        double now = Clock.Now;
        if (now >= v.AvoidCheckAt)
        {
            v.AvoidCheckAt = now + 0.2;
            foreach (var o in Vehicle.All)
            {
                if (o == v || !o.Def.Air || o.Destroyed || o.Landed || !GodotObject.IsInstanceValid(o)) continue;
                var rp = o.GlobalPosition - pos;
                if (rp.LengthSquared() > 300f * 300f) continue;
                var rv = o.Velocity3 - v.Velocity3;
                float tca = rv.LengthSquared() > 0.01f ? Mathf.Clamp(-rp.Dot(rv) / rv.LengthSquared(), 0f, 10f) : 0f;
                var miss = rp + rv * tca;
                if ((miss with { Y = 0f }).Length() > v.Def.RotorRadius + o.Def.RotorRadius + 12f || MathF.Abs(miss.Y) > 15f) continue;
                if (!GivesWay(v, o)) continue;
                v.AvoidFrom = o;
                v.AvoidUntil = now + 2.0;
                v.AvoidClimb = !GivesWay(o, v) || v.GetInstanceId() < o.GetInstanceId();
                Prof.Count("heli:gave way");
                break;
            }
        }
        if (now >= v.AvoidUntil || v.AvoidFrom is not { } a || !GodotObject.IsInstanceValid(a) || a.Destroyed) return;
        wantH = wantH.Rotated(Vector3.Up, Mathf.DegToRad(-40f)) * 0.5f;
        if (v.AvoidClimb) wantAlt = MathF.Max(wantAlt, a.GlobalPosition.Y + 15f);
    }

    /// <summary>Whether <paramref name="a"/> gives way to <paramref name="b"/> (see Avoid).</summary>
    static bool GivesWay(Vehicle a, Vehicle b)
    {
        if (a.Driver is not Bot) return false;
        if (b.Driver is not Bot) return true;
        bool aLanding = Landing(a), bLanding = Landing(b);
        if (aLanding != bLanding) return bLanding;
        bool aStill = a.AirSpeed < 3f, bStill = b.AirSpeed < 3f;
        if (aStill != bStill) return bStill;
        var track = (aStill ? -a.GlobalBasis.Z : a.Velocity3) with { Y = 0f };
        if (track.LengthSquared() < 1e-4f) return true;
        track = track.Normalized();
        var rp = (b.GlobalPosition - a.GlobalPosition) with { Y = 0f };
        float bearing = Mathf.RadToDeg(MathF.Atan2(rp.Dot(track.Cross(Vector3.Up)), rp.Dot(track)));
        return bearing > -15f && bearing < 110f;
    }

    static bool Landing(Vehicle v) => v.AirMode == HeliMode.Land && v.Goal is Vector3 g && ((g - v.GlobalPosition) with { Y = 0f }).Length() < 250f;

    /// <summary>
    /// A gunship's attack, as they're flown against an enemy with air defence. Low along the ground, masked by it,
    /// to a battle position 1.2-1.8 km out (with a missile, as far out as it reaches and the map allows); up just high
    /// enough to see the target; a rocket salvo (the gunner's cannon joins in by itself), or a missile; and back down
    /// before anyone can get a gun on it. Then along to another position for the next one, never the same twice
    /// running. Shot at, or a radar locked on, while up: straight back down, and on. The positions are chosen out of
    /// sight of any anti-aircraft gun we know of, bar the one a missile's going to.
    /// (It used to make diving runs from 1.4 km at 55 m, in plain view of everything, into the last 450 m: most
    /// gunships were shot down in the first minute, by guns 600-800 m off.)
    /// </summary>
    static (Vector3 wantH, float agl, Vector3 face) AttackRun(Bot b, Vehicle v, Vector3 target, Vector3 pos)
    {
        // Missile work (an air defence gun, armour): from as far out as it reaches and the map allows, one a pop-up.
        var focus = v.MissileFocus is { Destroyed: false } mf && GodotObject.IsInstanceValid(mf) && v.MissilesLeft > 0 ? mf : null;
        var aim = focus?.Center ?? v.AttackPoint ?? target;
        var toT = (aim - pos) with { Y = 0f };
        float d = toT.Length();
        double now = Clock.Now;
        // The gun we've come for has us (its radar's locked on), and we have it, in reach: a snap shot from right
        // here rather than running from it, and it's who shoots first. (Chased off by it again and again on the way
        // to somewhere to shoot from, one never got a missile away.) Not once it's hitting us: then it's too late,
        // and it's down and away.
        if (focus != null && v.RadarFrom == focus && v.RadarLocked && (v.BattlePos == null || v.AttackPhase == 0) && now >= v.GuidingUntil
            && now - v.LastHit >= 2.0 && now - v.MissileWarning >= 3.0
            && v.Center.DistanceTo(focus.Center) < v.MissileRange && Clear(v.GetWorld3D().DirectSpaceState, v.Center + Vector3.Up * 0.5f, focus.TopPoint))
        {
            Prof.Count("missile:snap shot");
            v.BattlePos = pos with { Y = 0f };
            v.PopAgl = Mathf.Clamp(v.Agl, Noe, 48f);
            v.AttackPhase = 1;
            v.PhaseSince = now;
            v.SeesTarget = true;
            v.SeeCheckAt = now + 0.3;
            v.NoPositionSince = -1;
        }
        if (v.BattlePos == null && (v.NoPositionSince < 0 || now - v.NoPositionSince > 4.0))
        {
            v.BattlePos = BattlePosition(v, aim, pos, focus, out float pop);
            v.PopAgl = pop;
            v.AttackPhase = 0;
            v.PhaseSince = now;
            v.NoPositionSince = v.BattlePos == null ? now : -1;
            // Nowhere to fire from that we'd live through: hang back, low, well out, out of sight of the air defence
            // we know of, and look again shortly. (It used to wait at the map's edge, in view of the gun that had
            // just chased it off.)
            if (v.BattlePos == null) v.HoldAt = HiddenNear(v, Inside(aim + ((pos - aim) with { Y = 0f }).Normalized() * 2500f), KnownAa(v, null));
        }
        if (v.BattlePos is not Vector3 bp)
        {
            v.Task = "no firing position: hanging back";
            var toB = (v.HoldAt - pos) with { Y = 0f };
            return (toB.LengthSquared() > 400f ? toB.Normalized() * MathF.Min(v.Def.AirSpeed * 0.6f, toB.Length() * 0.2f) : Vector3.Zero, Noe, toT);
        }
        var toP = (bp - pos) with { Y = 0f };
        float dp = toP.Length();
        // Shot at, a missile coming, or a radar on us: down. Unless it's the gun we've come for that has us, and we
        // have it: then it's who shoots first, and we're ready (see below).
        bool duel = focus != null && v.RadarFrom == focus && v.RadarLocked;
        bool shotAt = now - v.LastHit < 2.0 || now - v.MissileWarning < 3.0 || (v.RadarLocked && !duel);
        switch (v.AttackPhase)
        {
            case 0: // low, to the battle position
            {
                if (dp < 40f) { v.AttackPhase = 1; v.PhaseSince = now; v.SeesTarget = false; v.SeeCheckAt = 0; }
                v.Task = focus != null ? "low, to a missile position" : "low, to a firing position";
                float speed = MathF.Min(v.Def.AirSpeed * 0.75f, dp * 0.25f + 4f);
                return (toP.Normalized() * speed, Noe, dp > 150f ? toP : toT);
            }
            default: // up, fire, down
            {
                var space = v.GetWorld3D().DirectSpaceState;
                if (now > v.SeeCheckAt)
                {
                    v.SeeCheckAt = now + 0.3;
                    v.SeesTarget = Clear(space, v.Center + Vector3.Up * 0.5f, focus?.TopPoint ?? AreaEye(space, aim));
                }
                bool fired;
                if (focus != null)
                {
                    // A missile, once it's in sight and we're steady; a laser or radio one is steered all the way in
                    // from here, so stay up until it hits (unless we're shot at: then it's lost).
                    if (v.SeesTarget && (now - v.PhaseSince > 1.0 || duel) && v.MissileAwayAt < v.PhaseSince && v.LaunchMissile(focus))
                    {
                        v.MissileAwayAt = now;
                        var mw = v.Turrets[v.Def.Turrets.FindIndex(t => t.Fixed)].Def.Ammo[v.MissileIdx];
                        v.GuidingUntil = mw.FireAndForget ? now : now + v.Center.DistanceTo(focus.Center) / mw.Speed + 1.0;
                        if (v.Driver is Bot pb) Comms.Say(pb, $"Missile away — {focus.Def.ClassName}, {v.Center.DistanceTo(focus.Center) / 1000f:0.0} km.");
                    }
                    fired = v.MissileAwayAt > v.PhaseSince && now > v.GuidingUntil + 0.5;
                }
                else
                {
                    if (v.SeesTarget) FireRockets(v, aim, d);
                    fired = v.SalvoLeft == 0 && v.NextSalvo - 6.0 > v.PhaseSince && now - (v.NextSalvo - 6.0) > 1.5;
                }
                v.Task = !v.SeesTarget ? "up, looking for the target" : focus == null ? "up, rockets"
                       : v.MissileAwayAt < v.PhaseSince ? "up, launching" : now < v.GuidingUntil ? "up, guiding the missile" : "up, missile away";
                // Done: shot at, or it's away (and steered in, or had a moment to land), or it's been up too long, or
                // it can't see the target from here after all.
                if (shotAt || fired || now - v.PhaseSince > (focus != null ? 26.0 : 10.0) || (!v.SeesTarget && now - v.PhaseSince > 6.0))
                {
                    v.LastBattlePos = bp;
                    v.BattlePos = null;
                    v.AttackPhase = 0;
                    if (shotAt && v.Driver is Bot pb) Comms.Say(pb, "Taking fire — breaking low!");
                    if (shotAt && now < v.GuidingUntil) Prof.Count("missile:broke off guiding");
                }
                // Hold the spot, nose on the target, up only as far as it takes to see it.
                var hold = toP.LengthSquared() > 4f ? toP * 0.3f : Vector3.Zero;
                return (hold, v.PopAgl, toT);
            }
        }
    }

    /// <summary>
    /// Somewhere to fire from, on our side of the target. For rockets, on an arc 1.2-1.8 km out; for a missile, as far
    /// out as it reaches and the map allows, best about 4 km (beyond where a gun's crew would pick us out) and never
    /// nearer than 800 m. Hidden from the target at our low height if possible (so we come up, fire and go back down
    /// behind the ground), with a view of it no more than 48 m up; out of sight of every enemy anti-aircraft gun we
    /// know about but the one we're after (that one sees us while we see it: that's the duel); not where we fired from
    /// last time. Null if there's nowhere like that. <paramref name="pop"/>: how high over the tops we must come up.
    /// (Missile positions were all 3.2-5.2 km out at first: on a 2 km map every one was pulled in to the edge and
    /// failed, and the gunship hung about there for the rest of the match.)
    /// </summary>
    static Vector3? BattlePosition(Vehicle v, Vector3 aim, Vector3 pos, Vehicle? focus, out float pop)
    {
        pop = Noe;
        bool missiles = focus != null;
        var space = v.GetWorld3D().DirectSpaceState;
        var g = Effects.Ground;
        var away = (pos - aim) with { Y = 0f };
        away = away.LengthSquared() > 1f ? away.Normalized() : v.RunFrom;
        var threats = KnownAa(v, focus);
        // The way there: the gun we're after mustn't see us coming either.
        var watching = focus != null ? KnownAa(v, null) : threats;
        var eye = focus?.TopPoint ?? AreaEye(space, aim);
        float near = missiles ? 800f : 1200f, far = missiles ? MathF.Min(v.MissileRange * 0.85f, 5200f) : 1800f, ideal = missiles ? 4000f : 1500f;
        float arc = missiles ? 100f : 70f;
        Vector3? best = null;
        float bestScore = float.MinValue;
        for (int k = 0; k < 32; k++)
        {
            float ang = Mathf.DegToRad((float)(Random.Shared.NextDouble() * 2.0 - 1.0) * arc);
            float r = near + (float)Random.Shared.NextDouble() * (far - near);
            var c = Inside(aim + away.Rotated(Vector3.Up, ang) * r);
            // How far out it really is, once the edge of the map has had its say.
            float dist = ((c - aim) with { Y = 0f }).Length();
            if (dist < (missiles ? 800f : 700f)) { Prof.Count("bp:x pulled in by the edge"); continue; }
            if (v.LastBattlePos is Vector3 last && ((c - last) with { Y = 0f }).Length() < 300f) { Prof.Count("bp:x where it fired last"); continue; }
            // Not in their back yard: every gun and man they have is between there and us.
            if (NearEnemyBase(c, v.CrewTeam)) { Prof.Count("bp:x by an enemy base"); continue; }
            float gh = g?.HeightAt(c.X, c.Z) ?? 0f;
            float top = TopAt(space, c, gh);
            float need = -1f;
            foreach (float h in new[] { Noe, 22f, 34f, 48f })
                if (Clear(space, new Vector3(c.X, top + h, c.Z), eye)) { need = h; break; }
            if (need < 0f) { Prof.Count("bp:x no view of the target"); continue; }
            var up = new Vector3(c.X, top + need, c.Z);
            bool seenByAa = false;
            // (An air defence radar sees a helicopter in the open out to its guns' reach, 3 km: see BotSenses.)
            foreach (var aa in threats)
                if (aa.DistanceTo(up) < 3200f && Clear(space, aa + Vector3.Up * 3f, up)) { seenByAa = true; break; }
            if (seenByAa) { Prof.Count("bp:x seen by air defence"); continue; }
            Prof.Count("bp:ok");
            float score = (need > Noe ? 3f : 0f) - need * 0.04f - MathF.Abs(dist - ideal) / (missiles ? 1000f : 400f) + (float)Random.Shared.NextDouble();
            if (score <= bestScore) continue;
            // The way there, low: how much of it their air defence would see.
            score -= Exposed(space, pos, c, watching) * 2f;
            if (score <= bestScore) continue;
            bestScore = score;
            best = new Vector3(c.X, top, c.Z);
            pop = need;
        }
        Prof.Count(best == null ? "bp:search failed" : "bp:search found one");
        return best;
    }

    /// <summary>
    /// Keep turn points inside the map (its edge is a wall). A base and its pads may stand nearer the edge than we'd
    /// otherwise go. (On the smaller maps they stood outside it: a gunship sent home to rearm was stopped 140 m short of
    /// its pad, and never rearmed.)
    /// </summary>
    static Vector3 Inside(Vector3 p)
    {
        float e = Valley.PlayHalf - (NearBase(p, 250f) ? 120f : 320f);
        return new(Mathf.Clamp(p.X, -e, e), p.Y, Mathf.Clamp(p.Z, -e, e));
    }

    static bool NearBase(Vector3 p, float within)
    {
        if (Valley.Current is not { } m) return false;
        foreach (var b in m.Bases)
            if (((b - p) with { Y = 0f }).LengthSquared() < within * within) return true;
        return false;
    }

    static bool NearEnemyBase(Vector3 p, int team)
    {
        if (Valley.Current is not { } m) return false;
        float r = Mathf.Clamp(m.Size * 0.17f, 350f, 500f); // (a 500 m ring round each put half the smallest map out of bounds)
        for (int t = 0; t < m.Bases.Length; t++)
            if (t != team && ((m.Bases[t] - p) with { Y = 0f }).LengthSquared() < r * r) return true;
        return false;
    }

    /// <summary>
    /// What must be in sight to put rockets into an area: the top of whatever stands there (the roofs of a village,
    /// a wood's canopy, or the ground), not a point at head height among the houses. (Most places to fire from were
    /// turned down for not seeing into the streets.)
    /// </summary>
    static Vector3 AreaEye(PhysicsDirectSpaceState3D space, Vector3 aim) => new(aim.X, TopAt(space, aim, null) + 1.5f, aim.Z);

    /// <summary>Where the enemy air defence we've heard of is (bar <paramref name="except"/>).</summary>
    static List<Vector3> KnownAa(Vehicle v, Vehicle? except) =>
        Radio.AirDefences(v.CrewTeam).Where(r => r.Vehicle != except)
            .Select(r => r.Vehicle is { } av && GodotObject.IsInstanceValid(av) ? av.Center : r.Pos).ToList();

    /// <summary>Whether any of these air defence guns would see us low over the tops here (within its reach).</summary>
    static bool SeenAt(PhysicsDirectSpaceState3D space, Vector3 at, List<Vector3> threats)
    {
        foreach (var aa in threats)
            if (aa.DistanceTo(at) < 3200f && Clear(space, aa + Vector3.Up * 3f, at)) return true;
        return false;
    }

    /// <summary>How many of four points along the way from here to there, low over the tops, one of these guns would see.</summary>
    static int Exposed(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, List<Vector3> threats)
    {
        if (threats.Count == 0) return 0;
        int n = 0;
        for (int k = 1; k <= 4; k++)
        {
            var p = from.Lerp(to, k / 5f);
            if (SeenAt(space, new Vector3(p.X, TopAt(space, p, null) + Noe, p.Z), threats)) n++;
        }
        return n;
    }

    /// <summary>A spot near <paramref name="p"/>, low over the tops, that none of these guns can see (<paramref name="p"/> itself if nowhere near is).</summary>
    static Vector3 HiddenNear(Vehicle v, Vector3 p, List<Vector3> threats)
    {
        if (threats.Count == 0) return p;
        var space = v.GetWorld3D().DirectSpaceState;
        for (float r = 0f; r <= 450f; r += 150f)
            for (int k = 0; k < (r == 0f ? 1 : 12); k++)
            {
                var c = Inside(p + Vector3.Forward.Rotated(Vector3.Up, k * Mathf.Tau / 12f) * r);
                if (!SeenAt(space, new Vector3(c.X, TopAt(space, c, null) + Noe, c.Z), threats)) return c;
            }
        return p;
    }

    /// <summary>
    /// The quickest way out of a gun's sight: the nearest spot, low over the tops, with the ground or a building
    /// between it and the gun and nothing standing within a rotor's length of it, favouring the way we're going
    /// already (turning round takes longest). Straight away from it if there's nowhere like that near.
    /// </summary>
    static Vector3 EscapePoint(Vehicle v, Vector3 pos, Vector3 threat)
    {
        var space = v.GetWorld3D().DirectSpaceState;
        var vel = v.Velocity3 with { Y = 0f };
        float speed = vel.Length();
        var heading = speed > 3f ? vel / speed : (-v.GlobalBasis.Z with { Y = 0f }).Normalized();
        var eye = threat + Vector3.Up * 3f;
        Vector3? best = null;
        float bestCost = float.MaxValue;
        for (int k = 0; k < 16; k++)
        {
            var dir = heading.Rotated(Vector3.Up, k * Mathf.Tau / 16f);
            float turn = 1f - dir.Dot(heading); // 0 straight on, 2 turning round
            foreach (float r in EscapeReach)
            {
                var c = Inside(pos + dir * r);
                float top = TopAt(space, c, null);
                if (Clear(space, eye, new Vector3(c.X, top + 5f, c.Z)) || Crowded(space, c, top, v.Def.RotorRadius)) continue;
                // Seconds to get there, near enough: the way at speed, and slowing to turn for it.
                float cost = r / MathF.Max(speed, 20f) + turn * speed / 7f;
                if (cost < bestCost) { bestCost = cost; best = c with { Y = 0f }; }
                break; // the nearest hidden spot that way will do
            }
        }
        return best ?? Inside(pos + ((pos - threat) with { Y = 0f }).Normalized() * 400f);
    }

    static readonly float[] EscapeReach = { 90f, 180f, 300f };

    /// <summary>Something standing more than 3 m above <paramref name="top"/> within <paramref name="r"/> of a spot (no room to hover there low).</summary>
    static bool Crowded(PhysicsDirectSpaceState3D space, Vector3 c, float top, float r)
    {
        for (int k = 0; k < 4; k++)
            if (TopAt(space, c + Vector3.Forward.Rotated(Vector3.Up, k * Mathf.Pi / 2f) * r, null) > top + 3f) return true;
        return false;
    }

    /// <summary>A rocket salvo: the pods point along the nose, so the pilot aims the aircraft (a bot gets a little help).</summary>
    static void FireRockets(Vehicle v, Vector3 at, float dist)
    {
        int pods = Array.FindIndex(v.Def.Turrets.ToArray(), t => t.Fixed);
        if (pods < 0 || v.NowT < v.NextSalvo) return;
        var t = v.Turrets[pods];
        t.AmmoIdx = 0; // rockets (the missiles are the other load on the pods)
        var want = (at - t.Muzzle.GlobalPosition).Normalized();
        if (dist < 500f || dist > 2300f) return; // too close to be safe, or too far to hit much
        if (Mathf.RadToDeg(v.Forward.Normalized().AngleTo(want with { Y = v.Forward.Normalized().Y })) > 8f) return; // not lined up yet
        if (t.Loaded[0] <= 0) return;
        v.SalvoLeft = 6;
        v.NextSalvo = v.NowT + 6.0;
        v.SalvoAt = at;
    }
}
