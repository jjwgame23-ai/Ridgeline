using Godot;

namespace Ridgeline;

/// <summary>
/// Combat vehicles working with the infantry: combined arms at the level a 33-a-side fight
/// has, one or two fighting vehicles alongside the rifle squads.
/// - An IFV/APC is its rifle squad's ride: it picks them up when they've a long way to go,
///   carries them to a dismount point short of the objective and out of its sight, and they
///   get out on the side away from the enemy. Contact on the way (hit, or enemy close):
///   dismount there and then. Afterwards it supports them with its gun from behind them,
///   never out in front, and comes to pick them up again for the next long move.
/// - A tank supports a rifle squad's attack: from a standoff firing position in open
///   country; from the support-by-fire position alongside the squad's support team during a
///   deliberate attack; and in close terrain (town, forest) it goes in behind its infantry
///   along the squad's own trail, and never alone: with nobody of ours near, it pulls back
///   out. Enemy armour reported close by comes first.
/// - Firing positions are chosen hull-down where the ground allows: the gun sees the
///   target over a crest the hull sits behind. After a few engagements, or two and a half
///   minutes, it moves to another one (shoot and scoot).
/// - Fire for the infantry: when the squad's in contact and calls for it, the gun goes onto
///   where the enemy is; during the assault it fires onto the objective, and lifts off any
///   part of it our own people are within ~35 m of.
/// - Hit by something that can kill it: smoke towards the shooter, and back out of its sight.
/// - Short of main-gun ammunition: back to a FOB, the logistics truck or base to rearm.
/// </summary>
public sealed partial class MotorPool
{
    readonly RandomNumberGenerator _rng = new();

    public static int Mounts, Dismounts, ContactDismounts, FireRequests, SmokePops, Scoots, Rearms, Relocations;

    public static void ResetCounters() => Mounts = Dismounts = ContactDismounts = FireRequests = SmokePops = Scoots = Rearms = Relocations = 0;

    public static bool Carrier(Vehicle v) => v.Def.Kind is VKind.IFV or VKind.APC && v.Def.Passengers >= 4;

    /// <summary>Does this rifle squad have its own carrier (so the trucks leave it alone)?</summary>
    public bool Mechanised(Squad sq) =>
        Slots.Any(s => s.Crew?.Supports == sq && s.Live is { Destroyed: false } lv && Carrier(lv));

    void Combat(Slot s, Vehicle v, double now)
    {
        v.ArriveRadius = 6f;
        v.PreferReverse = now < s.ScootUntil;
        var inf = s.Crew?.Supports;
        if (inf != null && (inf.Alive == 0 || inf.Leader == null)) inf = null;
        if (ReactToHit(s, v, inf, now)) return;
        // The whole crew aboard before setting off: no gunner, no gun.
        if (now - v.LastHit > 5.0 && s.Crew != null && v.GunnerSeat >= 0 && v.Occupants[v.GunnerSeat] == null
            && s.Crew.Members.Any(m => m.Alive && m.Ride == null && m.FeetPos.DistanceTo(v.GlobalPosition) < 300f))
        {
            v.Goal = null;
            v.FireAt = null;
            return;
        }
        if (now < s.ScootUntil) { v.Goal = s.ScootTo; v.ArriveRadius = 4f; return; }
        if (Rearm(s, v, now)) { v.FireAt = null; return; }
        if (Carrier(v) && inf != null && MechWork(s, v, inf, now)) return;
        if (s.Mech is 1 or 2 && inf == null) EndCarry(s, v, null);
        Support(s, v, inf, now);
    }

    // ---------------------------------------------------------------- hit: smoke and back out

    bool ReactToHit(Slot s, Vehicle v, Squad? inf, double now)
    {
        if (v.LastHit <= s.LastHitSeen) return false;
        s.LastHitSeen = v.LastHit;
        if (v.LastHitPen < 40f || v.Immobile || v.Def.Kind == VKind.LTV && v.LastHitPen < 20f) return false;
        var from = v.LastHitFrom;
        var dir = (from - v.Center) with { Y = 0f };
        if (dir.LengthSquared() < 1f) dir = v.Forward;
        dir = dir.Normalized();
        if (v.SmokeSalvos > 0 && from.DistanceTo(v.Center) > 40f)
        {
            v.SmokeSalvos--;
            SmokePops++;
            SmokeScreen.Pop(_m.Map.Ground(v.Center + dir * 20f), 13f, 55f);
            if (v.Driver is Bot) Comms.Say(v.Driver, "We're hit! Smoke, smoke — reverse!");
        }
        // Back out, away from it, the front towards the threat.
        var back = v.GlobalPosition - dir * 35f;
        var nav = NavigationServer3D.MapGetClosestPoint(Valley.VehicleMap, back);
        s.ScootTo = ((nav - back) with { Y = 0f }).Length() < 8f ? nav : back;
        s.ScootUntil = now + 8.0;
        s.HasFiring = false;
        Scoots++;
        if (s.Mech == 2 && inf != null) Dismount(s, v, inf, from, true);
        return true;
    }

    // ---------------------------------------------------------------- rearming

    bool Rearm(Slot s, Vehicle v, double now)
    {
        int have = 0, full = 0;
        foreach (var t in v.Turrets)
        {
            if (t.Def.Indirect || t.Def.Ammo.Length == 0) continue;
            for (int i = 0; i < t.Def.Ammo.Length; i++) { have += t.Stock[i]; full += t.Def.Ammo[i].Mags; }
        }
        if (full == 0) return false;
        if (!s.Rearming && have >= full * 0.2f) return false;
        if (!s.Rearming)
        {
            s.Rearming = true;
            s.RearmAt = -1;
            if (v.Driver is Bot) Comms.Say(v.Driver, "Winchester on the main gun — heading back to rearm.");
        }
        // The nearest place with ammunition: a FOB, the logistics truck, or home.
        var at = s.Park;
        float best = at.DistanceTo(v.GlobalPosition);
        foreach (var f in Fob.All)
            if (f.Team == s.Team && f.GlobalPosition.DistanceTo(v.GlobalPosition) < best) { best = f.GlobalPosition.DistanceTo(v.GlobalPosition); at = f.GlobalPosition; }
        foreach (var o in Vehicle.All)
            if (o != v && !o.Destroyed && o.Def.Kind == VKind.Logistics && o.Team == s.Team && o.GlobalPosition.DistanceTo(v.GlobalPosition) < best)
            { best = o.GlobalPosition.DistanceTo(v.GlobalPosition); at = o.GlobalPosition; }
        v.Goal = at;
        v.ArriveRadius = 14f;
        if (best > 22f || MathF.Abs(v.Speed) > 1f) { s.RearmAt = -1; return true; }
        if (s.RearmAt < 0) s.RearmAt = now + 20.0; // loading rounds in by hand
        if (now < s.RearmAt) return true;
        foreach (var t in v.Turrets)
        {
            for (int i = 0; i < t.Def.Ammo.Length; i++) t.Stock[i] = t.Def.Ammo[i].Mags;
            if (t.Def.Coax != null) t.CoaxStock = t.Def.Coax.Mags;
        }
        v.SmokeSalvos = v.Def.Heavy ? 2 : 0;
        s.Rearming = false;
        s.HasFiring = false;
        Rearms++;
        if (v.Driver is Bot) Comms.Say(v.Driver, "Rearmed. Moving back up.");
        return false;
    }

    // ---------------------------------------------------------------- the carrier and its squad

    /// <summary>Returns true while it's picking the squad up or carrying it (it has the wheel).</summary>
    bool MechWork(Slot s, Vehicle v, Squad inf, double now)
    {
        var lead = inf.Leader!;
        var obj = inf.Objective;
        float toGo = obj == null ? 0f : ((obj.Center - lead.FeetPos) with { Y = 0f }).Length();
        switch (s.Mech)
        {
            case 0:
            case 3:
            {
                // A long move ahead, nothing going on, and we're not far off: come and get them.
                if (inf.WantRide) { inf.WantRide = false; s.PickupRetryAt = 0; if (v.Driver is Bot && lead.Ride == null) goto case 99; }
                bool worth = obj != null && toGo > 450f && !inf.Engaged && inf.Phase == AssaultPhase.None && !inf.Consolidating
                             && inf.Transport == null && lead.Ride == null && lead.FeetPos.DistanceTo(v.GlobalPosition) < MathF.Min(1600f, toGo * 3f);
                // (Worth waiting for: fetching them and driving there beats walking it, at ~10 m/s against ~2.)
                if (!worth || v.Driver is not Bot || now < s.PickupRetryAt) return false;
                goto case 99;
            }
            case 99: // start a pickup
            {
                s.Mech = 1;
                s.PickupTimeout = 40.0 + lead.FeetPos.DistanceTo(v.GlobalPosition) / 5f;
                s.JobSince = now;
                inf.Transport = v;
                Mounts++;
                Comms.Say(v.Driver, $"{inf.Name}, mount up — we'll take you forward!");
                if (DuelMode.Verbose) GD.Print($"[{now:0}s] {v.Def.Name} picking up {inf.Name} ({toGo:0} m to go)");
                return true;
            }
            case 1: // pick up
            {
                if (obj == null || inf.Engaged || toGo < 300f && !inf.Members.Any(m => m is Player)) { EndCarry(s, v, inf); return false; }
                v.Task = $"coming to pick up {inf.Name}";
                if (lead.Ride != v && lead.FeetPos.DistanceTo(v.GlobalPosition) > 25f)
                {
                    var toLead = (lead.FeetPos - v.GlobalPosition) with { Y = 0f };
                    v.Goal = lead.FeetPos - toLead.Normalized() * 12f;
                    v.ArriveRadius = 10f;
                    v.Boarding = false;
                    if (now - s.JobSince > s.PickupTimeout)
                    {
                        // Can't get to them: they'll walk, and we'll try again later.
                        s.PickupRetryAt = now + 180.0;
                        if (DuelMode.Verbose) GD.Print($"[{now:0}s] {v.Def.Name} gave up picking up {inf.Name}, {lead.FeetPos.DistanceTo(v.GlobalPosition):0} m short");
                        EndCarry(s, v, inf);
                        return false;
                    }
                    return true;
                }
                v.Goal = null;
                v.Boarding = true;
                int aboard = inf.Members.Count(m => m.Alive && m.Ride == v);
                int alive = inf.Members.Count(m => m.Alive && GodotObject.IsInstanceValid((GodotObject)m) && (m is Bot || m.FeetPos.DistanceTo(v.GlobalPosition) < 150f));
                if (aboard >= alive || (now - s.JobSince > 45.0 && aboard * 2 >= alive) || now - s.JobSince > 80.0)
                {
                    if (aboard == 0) { EndCarry(s, v, inf); return false; }
                    s.Drop = FindDismount(s.Team, obj.Center, v.GlobalPosition, v.Def.Kind == VKind.IFV ? 180f : 260f, v.Def.Kind == VKind.IFV ? 300f : 380f);
                    s.DropFor = obj.Center;
                    s.Mech = 2;
                    s.JobSince = now;
                    s.LoadedAt = v.GlobalPosition;
                    v.Boarding = false;
                    v.Arrived = false;
                    Comms.Say(v.Driver ?? lead, "All in. Ramp up, moving!");
                    if (DuelMode.Verbose) GD.Print($"[{now:0}s] {v.Def.Name} carrying {inf.Name} ({aboard} aboard), {s.Drop.DistanceTo(v.GlobalPosition):0} m to the dismount point");
                }
                return true;
            }
            case 2: // carrying
            {
                if (obj == null) { Dismount(s, v, inf, null, false); return false; }
                if (obj.Center.DistanceTo(s.DropFor) > 150f)
                {
                    s.Drop = FindDismount(s.Team, obj.Center, v.GlobalPosition, v.Def.Kind == VKind.IFV ? 180f : 260f, v.Def.Kind == VKind.IFV ? 300f : 380f);
                    s.DropFor = obj.Center;
                }
                v.Goal = s.Drop;
                v.ArriveRadius = 8f;
                v.Task = $"carrying {inf.Name}, {((s.Drop - v.GlobalPosition) with { Y = 0f }).Length():0} m to the dismount point";
                if (inf.WantDismount) { inf.WantDismount = false; Dismount(s, v, inf, obj.Center, false); return false; }
                // Contact on the way: the enemy close enough to be a danger to a vehicle full of men.
                Vector3? threat = null;
                if (v.Occupants.FirstOrDefault(o => o is Bot b && b.Ride == v && b.SeatIdx == v.GunnerSeat) is Bot g && g.Crew.Target is { } tgt)
                {
                    var tp = tgt is Vehicle tv ? tv.Center : tgt is ICombatant tc ? tc.FeetPos : (Vector3?)null;
                    if (tp is Vector3 p && (tgt is Vehicle || p.DistanceTo(v.GlobalPosition) < 350f)) threat = p;
                }
                if (threat != null || (now - v.LastHit < 2.0 && now - s.JobSince > 3.0))
                {
                    Dismount(s, v, inf, threat ?? v.LastHitFrom, true);
                    return false;
                }
                bool there = v.Arrived && ((s.Drop - v.GlobalPosition) with { Y = 0f }).Length() < v.ArriveRadius + 5f;
                if (there || now - s.JobSince > 240.0) { Dismount(s, v, inf, obj.Center, false); return false; }
                return true;
            }
        }
        return false;
    }

    void Dismount(Slot s, Vehicle v, Squad inf, Vector3? threat, bool contact)
    {
        foreach (var o in v.Occupants.ToArray())
            if (o != null && inf.Members.Contains(o) && o != s.Crew?.Members.FirstOrDefault(c => c == o))
            {
                v.Leave(o, threat);
                if (o is Player) Hud.Toast(contact ? "CONTACT — DISMOUNT! Get out behind the hull!" : "Dismount point — out, and go with your squad", 3f);
            }
        if (v.Driver != null) Comms.Say(v.Driver, contact ? "Contact! Dismount, dismount!" : "Dismount point — out you get. We'll cover you.");
        if (contact) ContactDismounts++; else Dismounts++;
        if (DuelMode.Verbose) GD.Print($"[{Clock.Now:0}s] {v.Def.Name} dismounted {inf.Name} ({(contact ? "contact" : "at the dismount point")}) {v.GlobalPosition.DistanceTo(inf.Objective?.Center ?? v.GlobalPosition):0} m from the objective");
        inf.Transport = null;
        s.Mech = 3;
        s.HasFiring = false;
    }

    void EndCarry(Slot s, Vehicle v, Squad? inf)
    {
        if (inf != null && inf.Transport == v) inf.Transport = null;
        foreach (var o in v.Occupants.ToArray())
            if (o is Bot b && b.Squad != s.Crew && b.Squad == inf) v.Leave(b);
        v.Boarding = false;
        s.Mech = 3;
    }

    /// <summary>
    /// A dismount point: this far short of the objective, on the side we're coming from,
    /// where the objective can't see it (behind a rise, a wood, buildings) and a vehicle can get to.
    /// </summary>
    Vector3 FindDismount(int team, Vector3 obj, Vector3 from, float min, float max)
    {
        var space = _m.Map.GetWorld3D().DirectSpaceState;
        var toUs = (from - obj) with { Y = 0f };
        if (toUs.LengthSquared() < 1f) toUs = (_m.Map.Bases[team] - obj) with { Y = 0f };
        float baseAng = MathF.Atan2(toUs.Z, toUs.X);
        Vector3 best = _m.Map.Ground(obj + toUs.Normalized() * (min + max) * 0.5f);
        float bestScore = float.MinValue;
        for (int k = 0; k < 20; k++)
        {
            float ang = baseAng + _rng.RandfRange(-0.7f, 0.7f), r = _rng.RandfRange(min, max);
            var p = obj + new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * r;
            if (MathF.Abs(p.X) > _m.Map.Half - 40f || MathF.Abs(p.Z) > _m.Map.Half - 40f) continue;
            p.Y = _m.Map.HeightAt(p.X, p.Z);
            var nav = NavigationServer3D.MapGetClosestPoint(Valley.VehicleMap, p);
            if (((nav - p) with { Y = 0f }).Length() > 6f) continue;
            p = nav;
            bool seen = space.IntersectRay(PhysicsRayQueryParameters3D.Create(obj + Vector3.Up * 2.5f, p + Vector3.Up * 2f, Layers.World | Layers.Trees)).Count == 0;
            float score = (seen ? 0f : 40f) - ((p - from) with { Y = 0f }).Length() * 0.02f + _rng.RandfRange(0f, 5f);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    // ---------------------------------------------------------------- supporting fire

    static bool CloseTerrain(Vector3 p)
    {
        var map = Valley.Current;
        if (map == null) return false;
        if (map.City?.Zone(p.X, p.Z) == 2 || map.BuiltAround(p.X, p.Z) >= 5) return true;
        return Terrain.Main?.TreesNear(p.X, p.Z) >= 16;
    }

    static bool FriendliesNear(int team, Vector3 p, float r) =>
        Combatants.All.Any(c => c.Team == team && c.Alive && c.Ride == null && c.FeetPos.DistanceTo(p) < r);

    void Support(Slot s, Vehicle v, Squad? inf, double now)
    {
        var kind = v.Def.Kind;
        bool tank = kind is VKind.MBT or VKind.MGS;
        bool spaa = kind == VKind.SPAA;
        bool light = kind == VKind.LTV;
        Vector3 watch;
        Vector3? anchor = null;
        float min, max, anchorR = 150f;
        bool follow = false, noPast = false;
        string why;
        Vector3? fireAt = null;
        string fireWhy = "";
        var lead = inf?.Leader;
        var armor = Radio.Latest(s.Team, RadioKind.Armor, 45.0);
        var obj = inf?.Objective;
        bool mission = inf != null && lead != null && inf.VehicleFireUntil > now && !spaa;

        // Enemy armour we can fight (a tank takes on anything; an IFV's cannon only lighter armour; an APC keeps out of its way).
        bool canFight = armor?.Vehicle is { Destroyed: false } ev0 && !ev0.Def.Air && (tank || kind == VKind.IFV && ev0.Def.Kind is not (VKind.MBT or VKind.MGS));
        if (mission)
        {
            // A fire mission from the squad leader: get a line on it and fire.
            watch = inf!.VehicleFireAt;
            anchor = lead!.FeetPos;
            anchorR = 200f;
            (min, max) = (40f, 900f);
            why = $"fire mission for {inf.Name}";
            fireAt = inf.VehicleFireAt;
            fireWhy = "fire mission";
        }
        else if (!spaa && !light && canFight && armor!.Vehicle is { } ev && armor.Pos.DistanceTo(v.GlobalPosition) < 1100f)
        {
            // Enemy armour close: that's our fight first. Overwatch where it was seen.
            watch = armor.Pos;
            (min, max) = tank ? (350f, 1000f) : (300f, 700f);
            why = $"overwatching the {ev.Def.ClassName}";
        }
        else if (inf != null && lead != null && obj != null)
        {
            if (spaa)
            {
                // Air defence: behind the squad, out of the direct fire.
                watch = obj.Center;
                anchor = lead.FeetPos;
                (min, max) = (350f, 900f);
                anchorR = 350f;
                noPast = true;
                why = $"air defence over {inf.Name}";
            }
            else if (inf.Engaged && lead.FeetPos.DistanceTo(inf.ContactAt) < 700f)
            {
                // Our infantry are in a fight: get the gun onto it.
                watch = inf.ContactAt;
                anchor = lead.FeetPos;
                (min, max) = (60f, tank ? 700f : 500f);
                noPast = true;
                why = $"supporting {inf.Name} in contact";
                fireAt = inf.ContactAt + Vector3.Up * 1f;
                fireWhy = "suppressing the contact";
                if (now - s.LastRequestAt > 25.0 && lead.Alive)
                {
                    s.LastRequestAt = now;
                    FireRequests++;
                    Comms.Say(lead, $"{v.Def.ClassName}, enemy {Comms.Bearing(lead.FeetPos, inf.ContactAt)}, {lead.FeetPos.DistanceTo(inf.ContactAt):0} meters — put fire on it!");
                }
            }
            else if (inf.Phase is AssaultPhase.Deploy or AssaultPhase.Assault)
            {
                // The deliberate attack: with the support team at its support-by-fire position.
                watch = obj.Center;
                anchor = inf.SbfAt;
                anchorR = tank ? 250f : 150f;
                (min, max) = tank ? (180f, 700f) : (120f, 450f);
                why = $"support by fire for {inf.Name}";
                if (inf.Phase == AssaultPhase.Assault)
                {
                    fireAt = AreaTarget(s.Team, obj);
                    fireWhy = fireAt == null ? "fire lifted: friendlies on the objective" : "fire on the objective";
                }
            }
            else if (!light && CloseTerrain(obj.Center) && ((obj.Center - lead.FeetPos) with { Y = 0f }).Length() < obj.Radius + 250f)
            {
                // Going into town or the woods: behind the infantry, along their own route.
                follow = true;
                var trail = inf.TrailPoint(tank ? 45f : 30f) ?? lead.FeetPos - ((obj.Center - lead.FeetPos) with { Y = 0f }).Normalized() * 40f;
                watch = obj.Center;
                anchor = trail;
                (min, max) = (0f, 99999f);
                why = $"following {inf.Name} in";
            }
            else
            {
                // On the move or holding: overwatch from near the squad, never out in front of it.
                watch = obj.Center;
                anchor = lead.FeetPos;
                anchorR = tank ? 220f : 150f;
                (min, max) = light ? (150f, 400f) : tank ? (200f, 900f) : (150f, 600f);
                noPast = true;
                why = $"overwatching {inf.Name}";
            }
        }
        else
        {
            // Nobody to work with: the commander's overwatch.
            watch = s.Crew?.Objective is PointObjective po ? po.Watch : s.Crew?.Objective?.Center ?? s.Park;
            (min, max) = light ? (150f, 350f) : spaa ? (500f, 1100f) : tank ? (300f, 1000f) : (200f, 700f);
            why = "overwatch";
        }

        // Alone in close terrain is how armour dies: pull out to where it can see them coming.
        bool exposed = !follow && CloseTerrain(v.GlobalPosition) && !FriendliesNear(s.Team, v.GlobalPosition, 60f);

        // Count engagements from here; a tank moves on after a few (they'll have its position by then).
        if (v.LastFired > s.ShotsCheckedAt) { s.ShotsAtFiring++; s.ShotsCheckedAt = v.LastFired; }
        bool moveOn = s.HasFiring && !follow && (now - s.FiringAt > 150.0 || (tank && s.ShotsAtFiring >= 8));
        bool redo = !s.HasFiring || s.FiringWhy != why || s.FiringWatch.DistanceTo(watch) > 60f
                    || (anchor is Vector3 a0 && s.FiringAnchor.DistanceTo(a0) > (follow ? 12f : 60f)) || moveOn || exposed;
        if (redo)
        {
            if (moveOn) Relocations++;
            s.Firing = follow ? SnapVehicle(anchor!.Value) : FiringPosition(v, s.Team, watch, min, max, anchor, anchorR, noPast ? lead : null);
            s.FiringWatch = watch;
            s.FiringAnchor = anchor ?? watch;
            s.FiringWhy = why;
            s.FiringAt = now;
            s.ShotsAtFiring = 0;
            s.HasFiring = true;
            // Leaving a firing position: back out of it rather than turning round in view.
            if (moveOn) { s.ScootTo = s.Firing; s.ScootUntil = now + 6.0; }
        }
        v.Goal = s.Firing;
        v.Watch = watch;
        v.Task = why;
        v.ArriveRadius = follow ? 8f : 5f;
        if (!spaa && fireAt is Vector3 fa)
        {
            v.FireAt = fa;
            v.FireAtUntil = now + 1.5;
            v.FireAtWhy = fireWhy;
        }
        else if (fireWhy != "") v.FireAtWhy = fireWhy;
    }

    /// <summary>A point on the objective to put fire on during the assault, well clear of our own men (null: none, lift fire).</summary>
    Vector3? AreaTarget(int team, IObjective obj)
    {
        for (int k = 0; k < 6; k++)
        {
            float a = _rng.Randf() * Mathf.Tau, r = MathF.Sqrt(_rng.Randf()) * obj.Radius * 0.7f;
            var p = obj.Center + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            p.Y = _m.Map.HeightAt(p.X, p.Z) + _rng.RandfRange(0.5f, 4f);
            if (!FriendliesNear(team, p, 35f)) return p;
        }
        return null;
    }

    Vector3 SnapVehicle(Vector3 p)
    {
        var nav = NavigationServer3D.MapGetClosestPoint(Valley.VehicleMap, p);
        return ((nav - p) with { Y = 0f }).Length() < 15f ? nav : p;
    }

    /// <summary>
    /// A firing position: within [min, max] of what it's watching (and, given an anchor,
    /// within anchorR of that), somewhere a vehicle can get to, with the gun able to see
    /// the target. Best of all hull-down: the turret sees over a crest the hull sits behind.
    /// Given the leader of the infantry it supports, not further forward than him.
    /// </summary>
    Vector3 FiringPosition(Vehicle v, int team, Vector3 watch, float min, float max, Vector3? anchor, float anchorR, ICombatant? notPast)
    {
        var space = v.GetWorld3D().DirectSpaceState;
        var home = (_m.Map.Bases[team] - watch) with { Y = 0f };
        float baseAng = MathF.Atan2(home.Z, home.X);
        float turretH = v.Def.GroundClear + v.Def.Hull.Y + 0.5f, hullH = v.Def.GroundClear + v.Def.Hull.Y * 0.45f;
        float leadD = notPast != null ? ((watch - notPast.FeetPos) with { Y = 0f }).Length() : 0f;
        var target = watch + Vector3.Up * 1.5f;
        Vector3? best = null;
        float bestScore = float.MinValue;
        for (int k = 0; k < 28; k++)
        {
            Vector3 p;
            if (anchor is Vector3 a)
            {
                float ang = _rng.Randf() * Mathf.Tau, r = MathF.Sqrt(_rng.Randf()) * anchorR;
                p = a + new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * r;
            }
            else
            {
                float ang = baseAng + _rng.RandfRange(-1.2f, 1.2f), r = _rng.RandfRange(min, max);
                p = watch + new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang)) * r;
            }
            if (MathF.Abs(p.X) > _m.Map.Half - 40f || MathF.Abs(p.Z) > _m.Map.Half - 40f) continue;
            float d = ((p - watch) with { Y = 0f }).Length();
            if (d < min || d > max) continue;
            if (notPast != null && d < leadD - 10f) continue; // not out in front of our infantry
            p.Y = _m.Map.HeightAt(p.X, p.Z);
            var nav = NavigationServer3D.MapGetClosestPoint(Valley.VehicleMap, p);
            if (((nav - p) with { Y = 0f }).Length() > 5f) continue;
            p = nav;
            var up = Vector3.Up;
            bool Sees(float h)
            {
                var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p + up * h, target, Layers.World | Layers.Trees));
                return hit.Count == 0 || hit["position"].AsVector3().DistanceTo(watch) < 30f;
            }
            bool gun = Sees(turretH);
            bool hullDown = gun && !Sees(hullH);
            bool close = CloseTerrain(p) && !FriendliesNear(team, p, 50f);
            float score = (gun ? 40f : 0f) + (hullDown ? 25f : 0f) - (close ? 35f : 0f)
                          - MathF.Abs(d - (min + max) * 0.5f) * 0.02f + (p.Y - watch.Y) * 0.15f
                          - (anchor is Vector3 a2 ? ((p - a2) with { Y = 0f }).Length() * 0.05f : 0f)
                          + _rng.RandfRange(0f, 4f);
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best ?? SnapVehicle(anchor ?? (watch + home.Normalized() * (min + max) * 0.5f));
    }
}
