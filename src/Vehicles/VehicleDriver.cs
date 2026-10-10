using Godot;

namespace Ridgeline;

/// <summary>
/// A bot at the wheel: follows a path on the vehicle navmesh to the vehicle's Goal,
/// slowing for corners and near the end, feeling ahead with rays for things the
/// navmesh doesn't know about (trees, other vehicles, wrecks), and backing off and
/// trying again when it gets stuck. Heavy vehicles just drive through trees.
/// </summary>
public static class VehicleDriver
{
    public static void Tick(Bot b, Vehicle v, float dt)
    {
        double now = Clock.Now;
        var d = v.Drive;
        if (v.Goal is not Vector3 goal || v.Boarding)
        {
            v.Throttle = 0f; v.Steer = 0f; v.Brake = true;
            v.Arrived = false; // no destination: not "arrived" anywhere
            d.Path = Array.Empty<Vector3>();
            return;
        }
        // The gunner has called a halt: a missile is fired and guided from a standstill.
        if (now < v.HaltUntil)
        {
            v.Throttle = 0f; v.Steer = 0f; v.Brake = true;
            d.Note = "halted: the gunner's firing a missile";
            return;
        }
        var pos = v.GlobalPosition;
        var flat = goal - pos;
        flat.Y = 0f;
        float dist = flat.Length();
        d.Note = "";
        if (dist < v.ArriveRadius)
        {
            v.Throttle = 0f; v.Steer = 0f; v.Brake = true;
            v.Arrived = true;
            return;
        }
        v.Arrived = false;
        v.Brake = false;

        // Plan: a fresh path every so often, or when the goal moves.
        if (d.Path.Length == 0 || now > d.RepathAt || d.PathGoal.DistanceTo(goal) > 12f)
        {
            d.PathGoal = goal;
            d.RepathAt = now + 10.0;
            d.Idx = 0;
            if (Valley.VehicleMap.IsValid && NavigationServer3D.MapGetIterationId(Valley.VehicleMap) > 0)
                using (Prof.Time("vpath")) d.Path = NavBaker.Path(Valley.VehicleMap, pos, goal);
            if (d.Path.Length == 0) d.Path = new[] { goal };
        }
        while (d.Idx < d.Path.Length - 1 && (d.Path[d.Idx] - pos with { Y = d.Path[d.Idx].Y }).Length() < 5f) d.Idx++;
        var wp = d.Path[Math.Min(d.Idx, d.Path.Length - 1)];
        var to = (wp - pos) with { Y = 0f };
        var fwd = v.Forward with { Y = 0f };
        fwd = fwd.Normalized();
        float angle = Mathf.RadToDeg(fwd.SignedAngleTo(to.Normalized(), Vector3.Up)); // + = to the left

        // Our own people in the way: stop for them (the enemy's, drive on).
        float reach = v.Def.Hull.Z * 0.5f + 3f + MathF.Abs(v.Speed) * 1.2f;
        float sideR = v.Def.Hull.X * 0.5f + 1.2f;
        foreach (var c in Combatants.All)
        {
            if (c.Team != v.CrewTeam || c.Dead || c.Ride != null) continue;
            var l = v.ToLocal(c.FeetPos);
            float ahead = v.Speed >= 0f ? -l.Z : l.Z;
            if (ahead > 0f && ahead < reach && MathF.Abs(l.X) < sideR && MathF.Abs(l.Y) < 3f)
            {
                v.Throttle = 0f;
                v.Brake = true;
                v.Steer = 0f;
                d.StuckSince = -1;
                d.ProgressAt = now;
                d.Note = $"stopped for {c.Callsign}";
                // Someone who stays put (a mortar man at his tube, a sentry): back off and go round him. (It waited
                // for him to move, and a logistics truck sat behind its own mortar crew for the whole match.)
                if (d.PersonWaitSince < 0) d.PersonWaitSince = now;
                else if (now - d.PersonWaitSince > 6.0)
                {
                    d.PersonWaitSince = -1;
                    BackOff(v, d, now, l.X > 0f ? 0.8f : -0.8f, 2.0);
                }
                return;
            }
        }
        d.PersonWaitSince = -1;

        // Backing out of a jam.
        if (now < d.ReverseUntil)
        {
            v.Throttle = -0.7f;
            v.Steer = d.ReverseSteer;
            d.Note = "reversing";
            return;
        }

        // Backing out: the goal's behind us and not far. Keep the front armour towards whatever we're leaving.
        if (v.PreferReverse && MathF.Abs(angle) > 110f && dist < 70f)
        {
            float back = angle > 0f ? angle - 180f : angle + 180f; // the angle from straight behind
            v.Steer = Mathf.Clamp(-back / 25f, -1f, 1f);
            v.Throttle = -0.85f;
            d.Note = "reversing out";
            return;
        }
        float steer = Mathf.Clamp(angle / 25f, -1f, 1f);
        float throttle = MathF.Abs(angle) < 25f ? 1f : MathF.Abs(angle) < 60f ? 0.55f : v.Def.Tracked ? 0.1f : 0.35f;
        throttle *= Mathf.Clamp(dist / 30f, 0.35f, 1f);

        // Other vehicles: the navmesh doesn't know where they are or where they're going.
        if (Traffic(v, fwd, ref steer, ref throttle, d, now)) return;

        // Feel ahead for what the navmesh doesn't know about.
        var space = v.GetWorld3D().DirectSpaceState;
        var excl = new Godot.Collections.Array<Rid> { v.GetRid() };
        float look = 6f + MathF.Abs(v.Speed) * 1.4f;
        var nose = v.Center + fwd * (v.Def.Hull.Z * 0.5f);
        uint mask = Layers.World | Layers.Vehicles | (v.Def.Heavy ? 0u : Layers.Trees);
        bool Blocked(float deg)
        {
            var dir = fwd.Rotated(Vector3.Up, Mathf.DegToRad(deg));
            return space.IntersectRay(PhysicsRayQueryParameters3D.Create(nose, nose + dir * look, mask, excl)).Count > 0;
        }
        if (Blocked(0f))
        {
            bool left = Blocked(28f), right = Blocked(-28f);
            if (left && right) throttle *= 0.3f;
            else steer = left ? -1f : 1f;
        }

        // Stuck: pushing but not moving, or trying to turn on the spot and not turning (a wall alongside).
        if (throttle > 0.05f && MathF.Abs(v.Speed) < 0.6f && !v.Immobile)
        {
            if (d.StuckSince < 0) { d.StuckSince = now; d.StuckAngle = angle; }
            else if (MathF.Abs(angle - d.StuckAngle) > 12f && throttle <= 0.15f) { d.StuckSince = now; d.StuckAngle = angle; } // it is coming round
            else if (now - d.StuckSince > 2.5)
            {
                d.StuckSince = -1;
                BackOff(v, d, now, -steer, 2.2);
            }
            d.Note = "pushing, not moving";
        }
        else d.StuckSince = -1;
        // Driving, but getting nowhere: shoving at something with the speed flickering over and under the stuck
        // threshold, which resets it every time. Measured on the ground covered instead: under 3 m in 8 s at more
        // than a crawl. (A tank at base pushed at a container like that for the whole match.)
        // (Only counted over unbroken driving: waiting, reversing or stopping starts it again.)
        bool running = now - d.ProgressSeen < 0.5;
        d.ProgressSeen = now;
        if (throttle > 0.3f && !v.Immobile && running)
        {
            if (((pos - d.ProgressPos) with { Y = 0f }).Length() > 3f) { d.ProgressPos = pos; d.ProgressAt = now; }
            else if (now - d.ProgressAt > 8.0)
            {
                d.ProgressPos = pos;
                d.ProgressAt = now;
                d.StuckSince = -1;
                BackOff(v, d, now, -steer, 2.2);
                d.Note = "no headway";
            }
        }
        else { d.ProgressPos = pos; d.ProgressAt = now; }

        v.Throttle = throttle;
        v.Steer = steer;
    }

    /// <summary>
    /// Back off (reversing, steering away) and plan again from wherever we end up. Having to back off again where we
    /// last did, the plan is taking us into the same thing (the navmesh doesn't know about vehicles, or a corner it
    /// cuts): back off further, and drive out to one side, alternately left and right, before heading on. (It only
    /// ever backed off 2 s and replanned, so the same path took it back into the same thing: a tank pinned on the
    /// containers at base, or behind the parked vehicles next to it in the row, pushed at them all match.)
    /// </summary>
    static void BackOff(Vehicle v, Vehicle.DriveState d, double now, float steer, double secs)
    {
        var pos = v.GlobalPosition;
        bool again = now - d.BackedOffTime < 60.0 && ((d.BackedOffAt - pos) with { Y = 0f }).Length() < 15f;
        d.BackOffs = again ? d.BackOffs + 1 : 0;
        d.BackedOffTime = now;
        if (!again) d.BackedOffAt = pos;
        d.Stucks++;
        double back = secs + Math.Min(d.BackOffs, 3) * 1.2;
        d.ReverseUntil = now + back;
        d.ReverseSteer = d.BackOffs % 2 == 1 ? -steer : steer;
        d.RepathAt = now + back + 0.1;
        if (d.BackOffs == 0 || d.Path.Length == 0) return;
        var fwd = (v.Forward with { Y = 0f }).Normalized();
        var right = fwd.Cross(Vector3.Up);
        float side = d.BackOffs % 2 == 1 ? 1f : -1f;
        var p = pos + right * side * (14f + 6f * Math.Min(d.BackOffs, 3)) - fwd * 6f;
        var nav = Valley.ClosestForVehicles(p);
        if (((nav - p) with { Y = 0f }).Length() < 12f) p = nav;
        d.Path = new[] { p, d.PathGoal };
        d.Idx = 0;
        d.RepathAt = now + back + 15.0;
    }

    /// <summary>
    /// Rules of the road, so vehicles don't drive into each other and lock up:
    /// - something in our lane ahead: follow it at a distance if it's going our way,
    ///   otherwise steer round it (oncoming traffic: both keep right, so they pass);
    /// - paths about to cross (closest approach under a vehicle's width in the next few
    ///   seconds): give way to traffic from the right, and to whoever gets there first;
    /// - still nose to nose after a while: the one with the lower id backs off and replans.
    /// Returns true when it has taken over the controls (waiting, or backing away).
    /// </summary>
    static bool Traffic(Vehicle v, Vector3 fwd, ref float steer, ref float throttle, Vehicle.DriveState d, double now)
    {
        var pos = v.Center with { Y = 0f };
        var right = fwd.Cross(Vector3.Up);
        var myVel = fwd * v.Speed;
        float myHalfW = v.Def.Hull.X * 0.5f, myHalfL = v.Def.Hull.Z * 0.5f;
        Vehicle? blocker = null;
        bool wait = false;
        foreach (var o in Vehicle.All)
        {
            if (o == v || !GodotObject.IsInstanceValid(o) || (o.Def.Air && !o.Landed)) continue;
            var rel = (o.Center with { Y = 0f }) - pos;
            float dist = rel.Length();
            if (dist > 50f) continue;
            float oR = MathF.Max(o.Def.Hull.X, o.Def.Hull.Z) * 0.5f;
            float clear = myHalfW + oR + 1.2f;
            var ofwd = (o.Forward with { Y = 0f }).Normalized();
            var oVel = o.Destroyed ? Vector3.Zero : ofwd * o.Speed;
            float ahead = rel.Dot(fwd), lateral = rel.Dot(right);
            float gap = ahead - myHalfL - oR;
            float look = 8f + MathF.Abs(v.Speed) * 2.2f;

            // In our lane, ahead.
            if (ahead > 0f && gap < look && MathF.Abs(lateral) < clear)
            {
                float along = oVel.Dot(fwd);
                if (along > 1.5f)
                {
                    // Same way: keep a gap, match its speed.
                    if (gap < 5f) { throttle = 0f; wait = true; blocker = o; d.Note = $"following {o.Def.Name}"; }
                    else if (gap < 14f) throttle = MathF.Min(throttle, Mathf.Clamp(along / MathF.Max(1f, v.Def.MaxSpeed), 0.15f, 1f));
                    continue;
                }
                // Parked, wrecked or coming at us: go round. Oncoming, both go right.
                float side = along < -1.5f ? -1f : lateral >= 0f ? 1f : -1f; // + = steer left
                float urgency = 1f - Mathf.Clamp(gap / look, 0f, 1f);
                steer = Mathf.Clamp(steer + side * (0.6f + urgency), -1f, 1f);
                throttle *= Mathf.Lerp(0.8f, 0.3f, urgency);
                d.Note = $"round {o.Def.Name}";
                if (gap < 2f) { wait = true; blocker = o; }
                continue;
            }

            // Crossing: where will we be closest, and how close?
            var w = oVel - myVel;
            float w2 = w.LengthSquared();
            if (w2 < 0.5f || oVel.LengthSquared() < 1f) continue;
            float tc = -rel.Dot(w) / w2;
            if (tc <= 0f || tc > 5f) continue;
            if ((rel + w * tc).Length() > clear) continue;
            // Give way to the right; head-to-head on the tie, the one further from the crossing waits.
            float myT = MathF.Max(0f, ahead) / MathF.Max(1f, MathF.Abs(v.Speed));
            float theirT = MathF.Max(0f, -rel.Dot(ofwd)) / MathF.Max(1f, MathF.Abs(o.Speed));
            bool yield = lateral > 2f || (MathF.Abs(lateral) <= 2f && myT > theirT);
            if (!yield) continue;
            throttle = MathF.Min(throttle, tc < 2.5f ? 0f : 0.25f);
            if (throttle == 0f) { wait = true; blocker = o; d.Note = $"giving way to {o.Def.Name}"; }
        }

        if (!wait) { d.WaitSince = -1; return false; }
        if (d.WaitSince < 0) d.WaitSince = now;
        // Stuck nose to nose: one of us (always the same one) backs off and finds another way. Against something that
        // isn't going anywhere (parked, empty, a wreck), always us. (It went by id alone, and a tank that had drawn the
        // higher id sat behind a parked truck at base, "going round" it, for the rest of the match.)
        // A crewed vehicle sitting where it was sent (an idle truck on its park spot) isn't going anywhere either; and
        // one that hasn't moved at all while we've waited a good while is as good as parked, whatever it's doing.
        // (An idle transport truck with its driver aboard and its park spot as its goal didn't count as parked, and
        // the logistics truck behind it, holding the higher id, waited on it for five minutes and never went out.)
        bool parked = blocker != null && (blocker.Destroyed || blocker.Driver == null || blocker.Goal == null || blocker.Boarding
                                          || blocker.Arrived && MathF.Abs(blocker.Speed) < 0.5f
                                          || now - d.WaitSince > 15.0 && MathF.Abs(blocker.Speed) < 0.5f);
        if (now - d.WaitSince > 5.0 && blocker != null && (parked || v.GetInstanceId() < blocker.GetInstanceId()))
        {
            d.WaitSince = -1;
            var away = (v.Center - blocker.Center) with { Y = 0f };
            BackOff(v, d, now, away.Dot(right) > 0f ? -0.8f : 0.8f, 2.5);
        }
        v.Throttle = 0f;
        v.Brake = true;
        v.Steer = steer;
        d.StuckSince = -1;
        return true;
    }
}
