using Godot;

namespace Ridgeline;

/// <summary>
/// A drone operator bot's side of the work, run from a covered spot well back from the fight
/// (the squad's post, from the commander):
/// - The quad pilot keeps a camera over the objective, orbiting at ~90 m. What it sees feeds
///   the whole side (the map, nearby squads, the mortars). Enemies who've gone to ground
///   (not moving, nobody of ours close) get a grenade dropped on them: it comes down to
///   ~55 m, settles over the target, and lets go. When the battery runs low, or it has
///   bombed out, it comes home to swap batteries and re-arm.
/// - The FPV pilot waits for a target worth a drone: armour first (called in on the radio,
///   or seen by our quads), then a group of enemies the side has eyes on. It flies out low,
///   and in the last couple of hundred metres dives into it.
/// Stocks are limited (airframes, grenades, FPVs); a logistics truck or a FOB tops them up.
/// </summary>
public sealed class DroneOps
{
    public const int MaxQuads = 2, MaxBombs = 8, MaxFpvs = 4;
    public int Quads = MaxQuads, Bombs = MaxBombs, Fpvs = MaxFpvs;
    public Drone? Quad, Fpv;
    readonly Bot _b;
    double _rechargeUntil, _nextFpvAt, _orbitT;
    ICombatant? _bombTarget;
    double _bombTargetSince, _nextBombAt;

    public DroneOps(Bot b) { _b = b; }

    public bool Flying => Quad is { Dead: false } || Fpv is { Dead: false };

    /// <summary>How stocked up (0-1), for logistics deciding who needs a run.</summary>
    public float StockLevel => MathF.Min(MathF.Min(Bombs / (float)MaxBombs, Fpvs / (float)MaxFpvs), Quads / (float)MaxQuads);

    public bool Restock()
    {
        if (StockLevel >= 0.999f) return false;
        Quads = MaxQuads; Bombs = MaxBombs; Fpvs = MaxFpvs;
        return true;
    }

    /// <summary>
    /// Which drones this operator flies: the squad's first operator flies the quad, the second
    /// the FPVs (alone, both). Out of his own kind, he flies the other kind with what he has left.
    /// </summary>
    (bool Quad, bool Fpv) Jobs()
    {
        var ops = _b.Squad?.Members.Where(m => m.Alive && m.Role == Role.DroneOperator).ToList();
        if (ops == null || ops.Count < 2) return (true, true);
        return ops.IndexOf(_b) == 0 ? (true, Quads == 0 && Quad == null) : (Fpvs == 0 && Fpv == null, true);
    }

    Drone Launch(DroneKind kind)
    {
        var d = new Drone { Kind = kind, Team = _b.Team, Operator = _b };
        _b.GetParent().AddChild(d);
        d.GlobalPosition = _b.FeetPos + Vector3.Up * 1.2f + _b.Aim.Dir with { Y = 0f } * 0.6f;
        return d;
    }

    bool _launch;

    public void Think(Vector3 area, bool launch = true)
    {
        _launch = launch;
        var (flyQuad, flyFpv) = Jobs();
        if (flyQuad || Quad != null) QuadWork(area);
        if (flyFpv || Fpv != null) FpvWork();
    }

    void QuadWork(Vector3 area)
    {
        double now = Clock.Now;
        if (Quad != null && (Quad.Dead || !GodotObject.IsInstanceValid(Quad)))
        {
            // Lost it (shot down or crashed).
            Quads = Math.Max(0, Quads - 1);
            Quad = null;
            _bombTarget = null;
            Comms.Say(_b, "Lost the drone!");
            _rechargeUntil = now + 15.0;
        }
        if (Quad == null)
        {
            if (Quads <= 0 || now < _rechargeUntil || !_launch) return;
            Quad = Launch(DroneKind.Quad);
            int load = Math.Min(2, Bombs);
            Quad.Bombs = load;
            Bombs -= load;
            Quad.Goal = area;
            Quad.GoalAgl = 105f;
            Comms.Say(_b, "Drone up.");
            return;
        }
        var q = Quad;
        var home = _b.FeetPos;
        // Coming home: low battery, or out of grenades with more at hand.
        bool rtb = q.Battery < 0.15f || (q.Bombs == 0 && Bombs > 0 && q.Battery < 0.8f) || !_launch && q.Battery < 0.5f;
        if (rtb)
        {
            q.Goal = home;
            q.GoalAgl = ((q.GlobalPosition - home) with { Y = 0f }).Length() < 6f ? 1.2f : 40f;
            if (((q.GlobalPosition - home) with { Y = 0f }).Length() < 3f && q.GlobalPosition.Y - home.Y < 3f)
            {
                Bombs += q.Bombs;
                q.QueueFree();
                Quad = null;
                _rechargeUntil = now + 25.0; // swap the battery, hang two more grenades
            }
            return;
        }

        // A bomb run in progress?
        if (_bombTarget != null)
        {
            bool fresh = q.Seen.TryGetValue(_bombTarget, out var seenAt) && now - seenAt < 6.0;
            if (!_bombTarget.Alive || !fresh || now - _bombTargetSince > 60.0 || q.Bombs <= 0) { _bombTarget = null; }
            else
            {
                var tp = _bombTarget.FeetPos;
                q.Goal = tp;
                q.GoalAgl = 55f;
                float off = ((q.GlobalPosition - tp) with { Y = 0f }).Length();
                // Over it, near enough still, and it isn't going anywhere: let go.
                if (off < 1.8f && q.Vel.Length() < 1.5f && _bombTarget.Vel.Length() < 1.5f)
                {
                    q.Drop();
                    Comms.Say(_b, "Drop, drop!");
                    _bombTarget = null;
                    _nextBombAt = now + 10.0; // watch where it went before the next one
                    q.GoalAgl = 105f;
                }
                return;
            }
        }
        // Pick one: someone we can see who's stopped moving, well clear of our own people.
        if (q.Bombs > 0 && now >= _nextBombAt)
        {
            ICombatant? best = null;
            float bestD = float.MaxValue;
            foreach (var (c, at) in q.Seen)
            {
                if (!c.Alive || now - at > 3.0 || c.Vel.Length() > 1.5f || c.Ride != null) continue;
                if (Combatants.All.Any(f => f.Team == _b.Team && f.Alive && f.FeetPos.DistanceTo(c.FeetPos) < 30f)) continue;
                float d = c.FeetPos.DistanceTo(q.GlobalPosition);
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best != null) { _bombTarget = best; _bombTargetSince = now; Comms.Say(_b, "Got one in cover, going for a drop."); return; }
        }
        // Otherwise orbit over the objective, camera down.
        _orbitT += 0.2;
        float a = (float)(_orbitT * 0.08);
        q.Goal = area + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * 80f;
        q.GoalAgl = 105f;
    }

    void FpvWork()
    {
        double now = Clock.Now;
        if (Fpv != null && (Fpv.Dead || !GodotObject.IsInstanceValid(Fpv))) Fpv = null;
        if (Fpv != null)
        {
            // Its target gone before it got there: the nearest other one of theirs near where it's going.
            if (Fpv.TargetV is { Destroyed: true } || Fpv.TargetC is { Alive: false } || Fpv.TargetV == null && Fpv.TargetC == null)
            {
                Fpv.TargetV = null;
                Fpv.TargetC = Combatants.All.Where(c => c.Alive && c.Team != _b.Team && c.FeetPos.DistanceTo(Fpv.AimAt) < 40f)
                    .OrderBy(c => c.FeetPos.DistanceTo(Fpv.AimAt)).FirstOrDefault();
            }
            return;
        }
        if (Fpvs <= 0 || now < _nextFpvAt || !_launch) return;
        _nextFpvAt = now + 4.0;
        var me = _b.FeetPos;
        const float Reach = 2800f;
        Vehicle? tv = null;
        Vector3? point = null;
        ICombatant? tc = null;
        // Armour first: called in on the radio, still about.
        var armor = Radio.Latest(_b.Team, RadioKind.Armor, 60.0);
        if (armor?.Vehicle is { Destroyed: false } av && GodotObject.IsInstanceValid(av) && av.Center.DistanceTo(me) < Reach && !av.Def.Air)
        {
            tv = av;
            point = av.Center;
        }
        else
        {
            // Then a group of them the side has eyes on.
            var cl = Intel.Cluster(_b.Team, me, 150f, Reach, 25f, 2);
            if (cl is Vector3 c)
            {
                point = c;
                tc = Combatants.All.Where(x => x.Alive && x.Team != _b.Team && x.FeetPos.DistanceTo(c) < 40f).OrderBy(x => x.FeetPos.DistanceTo(c)).FirstOrDefault();
            }
        }
        if (point is not Vector3 p) return;
        var d = Launch(DroneKind.Fpv);
        d.TargetV = tv;
        d.TargetC = tc;
        d.AimAt = p;
        d.Goal = p;
        d.GoalAgl = 35f;
        Fpvs--;
        _nextFpvAt = now + 35.0;
        Comms.Say(_b, $"FPV away — {(tv != null ? tv.Def.ClassName : "infantry")}, {Comms.Bearing(me, p)}, {me.DistanceTo(p):0} m.");
    }
}
