using Godot;

namespace Ridgeline;

/// <summary>
/// The window live (slice 3 of the playable window): the war runs on round it, a minute at a time in step with the
/// window's clock, and the two hand units back and forth.
/// - The window's units are held (War.Held): the war still commands them (orders, routes, supply, sleep) and counts
///   their soldiers, but doesn't march them, start abstract fights with them or resolve fire on them. Their squads in
///   the window get the war's orders as they change (a squad in a fight finishes it first), so a company beaten off is
///   sent again, or pulled back, by its battalion as in the war. Fire the war calls down on them comes in for real
///   (WindowFires.Incoming).
/// - Each minute the window writes back: the dead at once; where each company is (the middle of its squads in the
///   window); vehicles lost; a march that has arrived.
/// - Units come in near the player: war squads within PromoteR (1.2 km) of you come into the window where the war has
///   them, whole, up to the cap, each side's share by its strength there. A company in an abstract fight is handed
///   over where the fight has its soldiers (Combat.Hand), so the fight carries on rather than restarting.
/// - Units go back: squads whose men are all more than DemoteR (1.7 km) from you, or at the window's edge, leave it
///   with their real state (dead, down into the war's medical chain, wounded, fit) and their places.
/// The window itself stays where it was opened (moving it is slice 5), and the bubble round you moves within it.
/// </summary>
public partial class ConquestWindow
{
    /// <summary>live=1 (the default outside the tests): the war runs on and units are handed back and forth.</summary>
    public bool Live;
    const double WarStep = 60.0;
    const float PromoteR = 1200f, DemoteR = 1700f;
    double _warAt = -1;
    /// <summary>Each embodied vehicle's crew, and the war vehicle it is.</summary>
    readonly Dictionary<Squad, int> _vehicleOf = new();
    /// <summary>The orders each held mover's squads were last given (to see when the war changes them).</summary>
    readonly Dictionary<int, string> _orderSeen = new();
    public int Promoted, Demoted, WarTurns, Reordered;

    /// <summary>Where the bubble is centred: you, or whoever the camera is on while you're dead; watching (join=0), the camera.</summary>
    Vector3 BubbleCentre => !PlayerJoins ? (Spec.Active ? Spec.GlobalPosition with { Y = 0f } : Vector3.Zero)
        : PlayerBody is { Alive: true } p ? p.FeetPos : CarryOnAs is { } next && IsInstanceValid(next) ? next.FeetPos : Watched?.FeetPos ?? Vector3.Zero;

    (float X, float Z) ToIsland(Vector3 p) => (p.X + CX, p.Z + CZ);

    static float Flat(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Z * v.Z);

    void LiveTick()
    {
        if (_warAt < 0) { _warAt = Clock.Now + WarStep; return; }
        if (Clock.Now < _warAt) return;
        _warAt += WarStep;
        WarTurns++;
        WriteBack();
        War.Tick(WarStep);
        Reorder();
        Demote();
        Promote();
    }

    /// <summary>A company comes into the window: the war stops marching and fighting it, and the window has it.</summary>
    void Hold(Unit m)
    {
        if (!War.Held.Add(m.Id)) return;
        Combat.Hand(War, m);
        _orderSeen[m.Id] = OrderSig(m);
    }

    static string OrderSig(Unit m) => $"{m.Order?.Kind}:{m.Order?.Target}:{(m.Path != null ? $"{m.GoX:0},{m.GoZ:0}" : "halt")}";

    /// <summary>The squads of each held mover in the window.</summary>
    Dictionary<int, List<Squad>> SquadsByMover()
    {
        var by = new Dictionary<int, List<Squad>>();
        foreach (var (sq, u) in UnitOf)
        {
            if (!by.TryGetValue(u.Mover, out var l)) by[u.Mover] = l = new List<Squad>();
            l.Add(sq);
        }
        return by;
    }

    /// <summary>Where a squad's men on their feet are, on average (null: nobody left up).</summary>
    static Vector3? Middle(IEnumerable<Squad> sqs)
    {
        var sum = Vector3.Zero;
        int n = 0;
        foreach (var sq in sqs)
            foreach (var c in sq.Members)
                if (c.Alive && IsInstanceValid((GodotObject)c)) { sum += c.FeetPos; n++; }
        return n > 0 ? sum / n : null;
    }

    /// <summary>
    /// What the war hears from the window each minute. The dead at once (its counts, its reports). The living stay as they
    /// came in until they leave (Demote), so the war's medical chain doesn't take men the window still has. Where each
    /// company is, and its squads' places in it. Vehicles lost. A march that has arrived.
    /// </summary>
    void WriteBack()
    {
        foreach (var (c, s) in SoldierOf)
        {
            ref var so = ref War.Soldiers[s];
            if (c.Dead && so.State != SoldierState.Dead)
            {
                so.State = SoldierState.Dead;
                so.Since = War.Time;
            }
        }
        foreach (var (sq, v) in _vehicleOf)
            if (Motor.Slots.Find(s => s.Crew == sq) is not { Live: { Destroyed: false } }) LoseVehicle(v);
        foreach (var (mid, sqs) in SquadsByMover())
        {
            var m = War.Units[mid];
            if (Middle(sqs) is Vector3 at)
            {
                (m.X, m.Z) = ToIsland(at);
                foreach (var g in sqs.GroupBy(s => UnitOf[s]))
                    if (Middle(g) is Vector3 ua)
                    {
                        var (ux, uz) = ToIsland(ua);
                        g.Key.OffX = ux - m.X;
                        g.Key.OffZ = uz - m.Z;
                    }
                // Arrived: within 150 m of where the war sent it (its squads were sent to their places round that point).
                if (m.Path != null && MathF.Sqrt((m.X - m.GoX) * (m.X - m.GoX) + (m.Z - m.GoZ) * (m.Z - m.GoZ)) < 150f)
                {
                    m.Path = null;
                    m.HaltedAt = War.Time;
                    if (m.Order != null) m.Order.Done = true;
                }
            }
            War.Recount(m);
        }
    }

    /// <summary>
    /// The war's orders for the window's companies, as they change: each squad gets its part (OrderFor). A squad in a
    /// fight finishes it first: the order waits for the lull.
    /// </summary>
    void Reorder()
    {
        var by = SquadsByMover();
        foreach (int mid in War.Held)
        {
            var m = War.Units[mid];
            string sig = OrderSig(m);
            if (_orderSeen.TryGetValue(mid, out var was) && was == sig) continue;
            if (!by.TryGetValue(mid, out var sqs)) continue;
            bool waiting = false;
            foreach (var sq in sqs)
            {
                if (sq.Members.All(c => !c.Alive)) continue;
                if (sq.Engaged) { waiting = true; continue; }
                var u = UnitOf[sq];
                var at = Middle(new[] { sq }) ?? Vector3.Zero;
                Give(sq, OrderFor(u, m, new Vector2(at.X, at.Z), u.Side));
                Reordered++;
            }
            if (!waiting) _orderSeen[mid] = sig;
            Log($"[{Clock.Now:0}s] the war orders {War.Sides[m.Side].Name} {m.Short}: {Doing(m)}{(waiting ? " (squads in a fight finish it first)" : "")}");
        }
    }

    /// <summary>Units whose men are all far from you (or at the window's edge) go back to the war.</summary>
    void Demote()
    {
        var centre = BubbleCentre;
        float edge = Map.Half - 100f;
        foreach (var g in UnitOf.GroupBy(kv => kv.Value.Id).ToList())
        {
            var sqs = g.Select(kv => kv.Key).ToList();
            var men = sqs.SelectMany(s => s.Members).Where(c => IsInstanceValid((GodotObject)c)).ToList();
            if (men.Any(c => c is Player)) continue;
            var up = men.Where(c => c.Alive).ToList();
            if (up.Count == 0)
            {
                // Nobody left on his feet: the dead stay where they fell; the down go back with the unit.
                if (men.Any(c => !c.Dead)) Release(sqs);
                else foreach (var sq in sqs) Forget(sq);
                continue;
            }
            bool away = up.All(c => Flat(c.FeetPos - centre) > DemoteR || MathF.Abs(c.FeetPos.X) > edge || MathF.Abs(c.FeetPos.Z) > edge);
            if (away) Release(sqs);
        }
        foreach (int mid in War.Held.ToList())
            if (!UnitOf.Values.Any(u => u.Mover == mid))
            {
                War.Held.Remove(mid);
                _orderSeen.Remove(mid);
            }
    }

    void LoseVehicle(int v) => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(War.Vehicles)[v].Lost = true;

    /// <summary>A squad all of whose men are dead: out of the window's books (the bodies stay).</summary>
    void Forget(Squad sq)
    {
        UnitOf.Remove(sq);
        _vehicleOf.Remove(sq);
    }

    /// <summary>A unit (its squads in the window) back to the war, with each man's state and the unit's place.</summary>
    void Release(List<Squad> sqs)
    {
        var u = UnitOf[sqs[0]];
        var m = War.Units[u.Mover];
        if (Middle(sqs) is Vector3 at)
        {
            var (ux, uz) = ToIsland(at);
            u.OffX = ux - m.X;
            u.OffZ = uz - m.Z;
        }
        bool? cut = null;
        foreach (var sq in sqs)
        {
            foreach (var c in sq.Members.ToList())
            {
                if (!SoldierOf.TryGetValue(c, out int s)) continue;
                ref var so = ref War.Soldiers[s];
                if (c.Dead) { if (so.State != SoldierState.Dead) { so.State = SoldierState.Dead; so.Since = War.Time; } }
                else if (!c.Alive)
                {
                    // Down: into the war's medical chain, as a company holding the field evacuates its down.
                    so.State = SoldierState.Down;
                    so.Blood = c.Body.Blood;
                    so.Since = War.Time;
                    cut ??= !Supply.ClearToDepot(War, m);
                    Medical.Admit(War, s, cut.Value);
                }
                else
                {
                    so.Blood = c.Body.Blood;
                    if (c.Body.Wounds.Count > 0) so.State = SoldierState.Wounded;
                }
                // What he has left of his kit, as a share of the war's basic load.
                if (c is Bot rb && rb.Def.Mags > 0)
                    so.Ammo = (short)(Orbat.Load(so.Job).Ammo * Math.Clamp(rb.Mags.Rounds / (float)(rb.Def.MagSize * rb.Def.Mags), 0f, 1f));
                SoldierOf.Remove(c);
                if (c is Bot b) { Bots.Remove(b); Retire(b); }
            }
            if (_vehicleOf.TryGetValue(sq, out int v))
            {
                var slot = Motor.Slots.Find(x => x.Crew == sq);
                if (slot?.Live is { Destroyed: false } lv)
                {
                    Motor.Slots.Remove(slot);
                    lv.Visible = false;
                    lv.ProcessMode = ProcessModeEnum.Disabled;
                    lv.CollisionLayer = 0;
                    GetTree().CreateTimer(5.0).Timeout += () => { if (IsInstanceValid(lv)) lv.QueueFree(); };
                }
                else LoseVehicle(v);
            }
            Forget(sq);
            Squads[sq.Team].Remove(sq);
        }
        War.Recount(m);
        Demoted++;
        Log($"[{Clock.Now:0}s] back to the war: {War.Sides[u.Side].Name} {u.Short}");
    }

    /// <summary>
    /// A soldier leaves the world (the man the player becomes, or one going back to the war). Others may know of him (in
    /// their threat lists, their sights), so he's marked gone first: every brain drops a man who isn't alive. Hidden,
    /// still and out of the physics, he's freed a few seconds later. (He was freed at once, and bots that had him among
    /// their threats went on reading a freed object.)
    /// </summary>
    void Retire(Bot b)
    {
        b.Squad?.Members.Remove(b);
        b.Body.Dead = true;
        b.Visible = false;
        b.ProcessMode = ProcessModeEnum.Disabled;
        b.CollisionLayer = b.CollisionMask = 0;
        GetTree().CreateTimer(5.0).Timeout += () => { if (IsInstanceValid(b)) b.QueueFree(); };
    }

    /// <summary>
    /// War units near you come in: squads within PromoteR of the bubble's centre, inside the window, whole, nearest
    /// first, up to the cap, each side's share of it by its soldiers there. Their companies are held from then on.
    /// </summary>
    void Promote()
    {
        var centre = BubbleCentre;
        var c2 = new Vector2(centre.X, centre.Z);
        float edge = Map.Half - 200f;
        ReadFights();
        var inside = UnitOf.Values.Select(u => u.Id).ToHashSet();
        var cands = new List<(Unit U, Unit M, Vector2 At, List<int> Fit, float D)>();
        foreach (var u in War.Units)
        {
            if (u.Members.Count == 0 || u.Mover < 0 || inside.Contains(u.Id)) continue;
            var m = War.Units[u.Mover];
            if (m.People <= 0) continue;
            var at = _squadAt.TryGetValue(u.Id, out var fat) ? fat : Local(m.X + u.OffX, m.Z + u.OffZ);
            if (MathF.Abs(at.X) > edge || MathF.Abs(at.Y) > edge) continue;
            float d = (at - c2).Length();
            if (d > PromoteR) continue;
            var fit = u.Members.Where(s => War.Soldiers[s].State is SoldierState.Fit or SoldierState.Wounded).ToList();
            if (fit.Count == 0) continue;
            cands.Add((u, m, at, fit, d));
        }
        if (cands.Count == 0) return;
        var have = new int[3];
        foreach (var b in Bots) if (b.Alive) have[b.Team]++;
        var near = (int[])have.Clone();
        foreach (var c in cands) near[c.U.Side] += c.Fit.Count;
        int all = Math.Max(1, near.Sum()), total = have.Sum();
        var movers = new HashSet<int>();
        foreach (var c in cands.OrderBy(c => c.D))
        {
            int t = c.U.Side, quota = (int)MathF.Round(Cap * (float)near[t] / all);
            if (have[t] + c.Fit.Count > quota + 4 || total + c.Fit.Count > Cap + 8) continue;
            Bring(c.U, c.M, c.At, c.Fit);
            have[t] += c.Fit.Count;
            total += c.Fit.Count;
            movers.Add(c.M.Id);
            Promoted++;
            Log($"[{Clock.Now:0}s] into the window: {War.Sides[t].Name} {c.U.Short}, {c.Fit.Count} soldiers, {c.D:0} m from you ({Doing(c.M)})");
        }
        foreach (int mid in movers) Hold(War.Units[mid]);
    }
}
