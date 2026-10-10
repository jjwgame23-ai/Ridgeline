using Godot;

namespace Ridgeline;

/// <summary>
/// Artillery fired into a Conquest window: the war's guns and rockets, kilometres back and outside the window, firing on
/// what the embodied soldiers call for. The rounds come down as real shells (whistle, burst, fragments) where the war's
/// own rules put them (Artillery):
/// - Close support. Every 10 s, a squad in contact may call fire on the enemy its side knows of near it, if none of its
///   own people are within the gun's safe distance of the target (300 m for guns, 600 m for unguided rockets). The
///   side's nearest battery in range with rounds left in its day's allowance answers. The rounds land 6 minutes after
///   the call for guns (10 for BRAVO, whose fire control is more centralised; 15 for its rockets), as in the war.
/// - A planned fire (a preparation): on a point given, from a set time, without waiting on a call; or on order, when the
///   first squad attacking the point is ready to go in (OnOrder, Until).
/// - Where they land. The observer knows the target to within 10 m plus 3% of his range from it, halved once the fire
///   is adjusted (a planned target is plotted to 10 m); each round then spreads by its weapon's dispersion (0.3% of range
///   for guns, 1.5% for unguided rockets, 5 m for guided ones). The tubes fire in volleys about 8 s apart.
/// - Lift and shift. A mission stops when the side's own people come within the gun's safe distance of its target: a
///   preparation lifts as the assault closes.
/// - The rounds come out of the war: the fire unit's tubes are emptied and its day's allowance spent (Artillery.Take).
/// - The shells. 155/152 mm: an even chance of a fragment hit on a man standing in the open at about 50 m (the war's
///   casualty radius, 25 m, against an 81 mm bomb's 12 m, scaled from the bomb's 25 m here), and about 38 hand grenades'
///   worth of charge (6.6 kg of TNT in a 155 mm M107 against 0.18 kg of explosive in an M67). Rockets about the same.
/// Battalion mortars aren't fired from here: they come into the window as tubes with crews (VKind.Mortar), and lay on
/// whatever their side can see.
/// </summary>
public sealed class WindowFires
{
    readonly War _war;
    readonly ConquestWindow _w;
    readonly RandomNumberGenerator _rng = new();
    double _nextCall;

    sealed class Mission
    {
        public int Side, Unit, Rounds, Fired, Tubes;
        public VClass Kind;
        public Vector3 Target;
        public float Sigma;
        public double At;
        public bool Planned, FromWar;
        public Vector3 From; // the battery, in window coordinates (far outside it): where the shells come from
    }

    readonly List<Mission> _missions = new();
    /// <summary>The missions coming and falling: whose, where, and when the first rounds land (the map).</summary>
    public IEnumerable<(int Side, Vector3 Target, double At)> Missions => _missions.Select(m => (m.Side, m.Target, m.At));
    public readonly int[] Calls = new int[3], Rounds = new int[3], Planned = new int[3], Lifted = new int[3];

    public WindowFires(War war, ConquestWindow w)
    {
        _war = war;
        _w = w;
        _rng.Seed = (ulong)(war.Seed * 6007 + 11);
    }

    Vector3 Local(float x, float z) => new(x - _w.CX, 0f, z - _w.CZ);
    (float X, float Z) Island(Vector3 p) => (p.X + _w.CX, p.Z + _w.CZ);

    /// <summary>
    /// A war fire mission landing on a unit the window has (War.ShellHeld): the war chose the target, fired the rounds
    /// (out of its tubes and allowance) and worked out where they fall; here they come down, starting now. Mortars too:
    /// the war's mortars aren't embodied unless their unit is.
    /// </summary>
    public void Incoming(global::Ridgeline.Mission ms)
    {
        var fu = _war.Units[ms.Unit];
        var (x, z) = Artillery.TubePos(_war, fu);
        int tubes = Math.Max(1, Artillery.Tubes(_war, fu, out _));
        _missions.Add(new Mission
        {
            Side = ms.Side, Unit = ms.Unit, Kind = ms.Kind, Target = Local(ms.X, ms.Z), Rounds = ms.Rounds, Tubes = Math.Min(tubes, ms.Rounds),
            Sigma = ms.Sigma, At = Clock.Now, From = Local(x, z), FromWar = true,
        });
        if (ConquestWindow.Verbose) GD.Print($"[{Clock.Now:0}s] {_war.Sides[ms.Side].Name} {fu.Short} ({ms.Kind}) fires {ms.Rounds} rounds into the window at {ms.X - _w.CX:0},{ms.Z - _w.CZ:0} (the war's mission)");
    }

    /// <summary>A planned fire on <paramref name="target"/> from <paramref name="side"/>'s nearest battery in range, landing at <paramref name="at"/> (Clock time).</summary>
    public bool Plan(int side, Vector3 target, double at)
    {
        // Planned fires are scheduled: a battery fires one after another, so being booked for another doesn't matter.
        var u = Battery(side, target, planned: true);
        if (u == null) return false;
        if (Fire(u, target, 10f, at, true) is not { } m) return false;
        Planned[side]++;
        return true;
    }

    readonly List<(int Side, Vector3 At, int Missions)> _onOrder = new();

    /// <summary>
    /// A preparation planned on a point and fired on order: when the first squad attacking it is back from its leader's
    /// recon and ready to go in (Squad.OwnFiresUntil asks Until). It's a planned target, so it comes in 90 s; its missions
    /// two minutes apart. A preparation is the last thing before the assault moves, so the defenders have no time to
    /// recover from it (FM 3-90).
    /// </summary>
    public void OnOrder(int side, Vector3 target, int missions) => _onOrder.Add((side, target, missions));

    /// <summary>
    /// Squad.OwnFiresUntil: when the side's fire about to fall or falling within its safe distance of a point will be over
    /// (below zero: none). Fires any preparation on order on the point first.
    /// </summary>
    public double Until(int side, Vector3 at)
    {
        double now = Clock.Now;
        for (int i = _onOrder.Count - 1; i >= 0; i--)
        {
            var o = _onOrder[i];
            if (o.Side != side || (o.At - at with { Y = o.At.Y }).Length() > 200f) continue;
            _onOrder.RemoveAt(i);
            int n = 0;
            for (int k = 0; k < o.Missions; k++) if (Plan(side, o.At, now + PlannedResponse + k * 120.0)) n++;
            if (ConquestWindow.Verbose) GD.Print($"[{now:0}s] {_war.Sides[side].Name} preparation on {o.At.X:0},{o.At.Z:0} called: {n} mission{(n == 1 ? "" : "s")}, the first landing in {PlannedResponse / 60:0.0} min");
        }
        double until = -1.0;
        foreach (var m in _missions)
        {
            if (m.Side != side) continue;
            var g = Artillery.Gun(m.Side, m.Kind);
            if ((m.Target - at with { Y = m.Target.Y }).Length() > Artillery.MinSafe(m.Kind, g.Guided)) continue;
            // The last volley, and its flight.
            until = Math.Max(until, m.At + ((m.Rounds - 1) / Math.Max(1, m.Tubes)) * 8.0 + 5.0);
        }
        return until;
    }

    public void Tick()
    {
        double now = Clock.Now;
        if (now >= _nextCall)
        {
            _nextCall = now + 10.0;
            Call();
        }
        foreach (var m in _missions)
            while (m.Fired < m.Rounds && now >= m.At + (m.Fired / Math.Max(1, m.Tubes)) * 8.0 + (m.Fired % Math.Max(1, m.Tubes)) * 0.7)
            {
                // Lift and shift: our own people closer to the target than the gun's safe distance, and the fire stops
                // (a preparation lifts as the assault closes on what it was falling on). The rounds not fired are kept.
                var g = Artillery.Gun(m.Side, m.Kind);
                float safe = Artillery.MinSafe(m.Kind, g.Guided);
                if (Combatants.All.Any(c => c.Team == m.Side && !c.Dead && (c.FeetPos - m.Target with { Y = c.FeetPos.Y }).Length() < safe))
                {
                    Lifted[m.Side] += m.Rounds - m.Fired;
                    m.Rounds = m.Fired;
                    break;
                }
                Shell(m);
            }
        _missions.RemoveAll(m => m.Fired >= m.Rounds);
    }

    /// <summary>
    /// Targets a side has planned fire on (a defence's approaches), registered beforehand: fire called onto one comes in
    /// 90 s, the guns already having the data. Anywhere else waits on the whole call.
    /// </summary>
    public readonly List<(int Side, Vector3 At)> Registered = new();
    const double PlannedResponse = 90.0;

    /// <summary>
    /// Squad leaders calling for fire on an enemy they can see beyond the guns' safe distance from their own people (an
    /// observer needn't be under fire to call it), or squads in contact on the enemy their side knows of near them.
    /// (Only squads in contact called, and contact came inside the safe distance: the guns never fired.)
    /// </summary>
    void Call()
    {
        for (int side = 0; side < 3; side++)
            foreach (var sq in _w.Squads[side])
            {
                if (sq.Leader is not Bot lead || !lead.Alive) continue;
                Vector3? seen = null;
                foreach (var t in lead.Senses.Threats)
                    if (t.Visible && t.Who.Alive && (t.LastKnownPos - lead.FeetPos).Length() > 350f) { seen = t.LastKnownPos; break; }
                Vector3? found = seen ?? (sq.Engaged ? global::Ridgeline.Intel.Cluster(side, lead.FeetPos, 150f, 3000f) : null);
                if (found is not Vector3 target) continue;
                // One mission at a time on a place.
                if (_missions.Any(m => m.Side == side && (m.Target - target with { Y = m.Target.Y }).Length() < 150f)) continue;
                var u = Battery(side, target);
                if (u == null) continue;
                var g = Artillery.Gun(side, u.Fires);
                float safe = Artillery.MinSafe(u.Fires, g.Guided);
                if (Combatants.All.Any(c => c.Team == side && !c.Dead && (c.FeetPos - target with { Y = c.FeetPos.Y }).Length() < safe)) continue;
                // The observer's error: 10 m plus 3% of his range from it, halved by adjusting the fire.
                float tle = (10f + 0.03f * (lead.FeetPos - target).Length()) / 2f;
                bool registered = Registered.Any(r => r.Side == side && (r.At - target with { Y = r.At.Y }).Length() < 150f);
                double delay = registered ? PlannedResponse : g.Delay;
                if (Fire(u, target, tle, Clock.Now + delay, false) == null) continue;
                Calls[side]++;
                Comms.Say(lead, $"Fire mission, {Comms.Bearing(lead.FeetPos, target)}, {(lead.FeetPos - target).Length():0} meters!");
                if (ConquestWindow.Verbose) GD.Print($"[{Clock.Now:0}s] {_war.Sides[side].Name} {sq.Name} calls fire from {u.Short} ({u.Fires}) on {target.X:0},{target.Z:0}{(registered ? " (a planned target)" : "")}, landing in {delay / 60:0.0} min");
                break; // one call a side each look: the battery has one mission at a time
            }
    }

    /// <summary>The side's nearest fire unit with guns or rockets that reaches the target and has rounds left today, and isn't busy.</summary>
    Unit? Battery(int side, Vector3 target, bool planned = false)
    {
        var (tx, tz) = Island(target);
        Unit? best = null;
        float bestD = float.MaxValue;
        foreach (int id in _war.FireUnits)
        {
            var u = _war.Units[id];
            if (u.Side != side || u.People <= 0 || u.Fires is not (VClass.Howitzer or VClass.Rocket)) continue;
            if (!planned && _missions.Any(m => m.Unit == u.Id)) continue;
            var g = Artillery.Gun(side, u.Fires);
            var (x, z) = Artillery.TubePos(_war, u);
            float d = MathF.Sqrt((x - tx) * (x - tx) + (z - tz) * (z - tz));
            if (d > g.Range) continue;
            int tubes = Artillery.Tubes(_war, u, out _);
            if (tubes == 0 || u.RoundsToday >= tubes * Artillery.Allowance(side, u.Fires)) continue;
            // Guns before rockets, nearest first.
            float score = d + (u.Fires == VClass.Rocket ? 1e6f : 0f);
            if (score >= bestD) continue;
            bestD = score;
            best = u;
        }
        return best;
    }

    Mission? Fire(Unit u, Vector3 target, float tle, double at, bool planned)
    {
        var g = Artillery.Gun(u.Side, u.Fires);
        int tubes = Artillery.Tubes(_war, u, out int have);
        if (tubes == 0) return null;
        int want = Math.Min(Math.Min(have, g.Guided ? 2 : tubes * g.PerTube), Math.Max(1, tubes * Artillery.Allowance(u.Side, u.Fires) - u.RoundsToday));
        int rounds = Artillery.Take(_war, u, want);
        if (rounds == 0) return null;
        var (x, z) = Artillery.TubePos(_war, u);
        var from = Local(x, z);
        float range = (from - target with { Y = 0f }).Length();
        float spread = g.Guided ? 5f : g.Spread * range;
        var m = new Mission
        {
            Side = u.Side, Unit = u.Id, Kind = u.Fires, Target = target, Rounds = rounds, Tubes = Math.Min(tubes, rounds),
            Sigma = MathF.Sqrt(tle * tle + spread * spread), At = at, Planned = planned, From = from,
        };
        _missions.Add(m);
        return m;
    }

    /// <summary>One round: where it falls (target, observer's error and dispersion), flown in for its last 3 s from the battery's side.</summary>
    void Shell(Mission m)
    {
        m.Fired++;
        Rounds[m.Side]++;
        var land = m.Target + new Vector3((float)_rng.Randfn(0f, m.Sigma), 0f, (float)_rng.Randfn(0f, m.Sigma));
        if (_w.Map is not { } map) return;
        land.Y = map.HeightAt(land.X, land.Z);
        // Coming down at about 300 m/s, 40 degrees below the horizon, from the battery's side. Flown for its last 3 s, so
        // the whistle is heard and men get down as they do for any round coming in (Ballistics.Incoming).
        var flat = (land - m.From) with { Y = 0f };
        flat = flat.LengthSquared() > 1f ? flat.Normalized() : Vector3.Forward;
        const float T = 3f, Speed = 300f;
        float dive = Mathf.DegToRad(40f);
        var v0 = flat * (Speed * MathF.Cos(dive)) + Vector3.Down * (Speed * MathF.Sin(dive));
        var start = land - v0 * T + Vector3.Up * (0.5f * 9.8f * T * T);
        bool rocket = m.Kind == VClass.Rocket, mortar = m.Kind == VClass.Mortar;
        // A battalion's mortar: 120 mm for ALPHA's and BRAVO's (M120, 2B11), 81 mm for CHARLIE's (L16). The 120 mm bomb
        // carries about three times the 81's charge (2.2 kg of TNT against 0.7-0.9 kg).
        bool big = m.Side != 2;
        string name = rocket ? (m.Side == 1 ? "220 mm rocket" : "GMLRS") : mortar ? (big ? "120 mm HE" : "81 mm HE") : m.Side == 1 ? "152 mm HE" : "155 mm HE";
        float fragR = rocket ? 55f : mortar ? (big ? 35f : 25f) : 50f, power = rocket ? 45f : mortar ? (big ? 12f : 4f) : 38f;
        Ballistics.I.Fire(start, v0, Speed, 0f, null, 600f, name, explosive: true, armM: 0f, pen: 60f, vehDamage: mortar ? 60f : 140f,
            crater: mortar ? 1.8f : 3.2f, fragR: fragR, power: power, whistle: true);
    }
}
