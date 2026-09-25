using Godot;

namespace Ridgeline;

/// <summary>What one bot knows about one enemy.</summary>
public sealed class Threat
{
    public ICombatant Who = null!;
    public Vector3 LastKnownPos;
    public double LastSeen = -999, LastHeard = -999, VisibleSince = -999;
    public double GapBeforeVisible = 999;
    public double Reported = -99;           // recon: when we last passed this one on   // how long it had been out of sight when it reappeared
    public float Awareness;                 // 0..1 build-up before a target is actually noticed
    public bool Visible, Confirmed;
    public bool HeadVisible, UpperVisible, ChestVisible, HipVisible;
}

/// <summary>What one bot knows about one enemy vehicle.</summary>
public sealed class VehicleThreat
{
    public Vehicle Who = null!;
    public Vector3 LastKnownPos;
    public double LastSeen = -999;
    public bool Visible;
    /// <summary>The part of it we can see (hull centre, or just the turret when it's hull-down).</summary>
    public Vector3 AimPoint;
}

/// <summary>
/// Sight and hearing. Nothing is known for free: enemies are noticed over time
/// (faster when close, central, moving, standing or shooting), and sounds are
/// only heard once their wavefront reaches the bot, giving a rough position.
/// </summary>
public sealed class BotSenses
{
    readonly Bot _b;
    readonly RandomNumberGenerator _rng = new();
    readonly Godot.Collections.Array<Rid> _selfOnly = new();
    double _lastTick = -1;
    int _tick;

    public readonly List<Threat> Threats = new();
    public readonly List<VehicleThreat> Vehicles = new();

    /// <summary>See through (or not) a body: our own vehicle's hull while we ride in it.</summary>
    public void IgnoreBody(Rid rid, bool on)
    {
        if (on) { if (!_selfOnly.Contains(rid)) _selfOnly.Add(rid); }
        else _selfOnly.Remove(rid);
    }
    double _lastSighting = -99;

    /// <summary>Nothing seen for a while: the bot can look around less often.</summary>
    public bool Quiet => Clock.Now - _lastSighting > 8.0;

    public BotSenses(Bot b)
    {
        _b = b;
        _rng.Randomize();
    }

    public Threat? Find(ICombatant c)
    {
        foreach (var t in Threats) if (t.Who == c) return t;
        return null;
    }

    public Threat Get(ICombatant c)
    {
        var t = Find(c);
        if (t != null) return t;
        t = new Threat { Who = c, LastKnownPos = c.FeetPos };
        Threats.Add(t);
        return t;
    }

    public void Forget(Threat t) => Threats.Remove(t);

    public void Tick(float dt)
    {
        double now = Clock.Now;
        if (_lastTick < 0) _lastTick = now - dt;
        if (_selfOnly.Count == 0) _selfOnly.Add(_b.GetRid());
        _tick++;
        Look(dt, now);
        if (_tick % 3 == 0) LookForVehicles(now);
        Listen(now);
        Threats.RemoveAll(t => !t.Who.Alive || now - Math.Max(t.LastSeen, t.LastHeard) > 40.0);
        _lastTick = now;
    }

    /// <summary>
    /// Vehicles are big, loud and easy to see: anything crewed by the enemy with a
    /// clear line to its hull (or its turret top) inside ~1.5 km and our field of view.
    /// </summary>
    void LookForVehicles(double now)
    {
        var space = _b.GetWorld3D().DirectSpaceState;
        var eye = _b.EyePos;
        var look = _b.Aim.Dir;
        foreach (var v in Vehicle.All)
        {
            var known = Vehicles.Find(x => x.Who == v);
            if (v.Destroyed || !v.Crewed || v.CrewTeam == _b.Team)
            {
                if (known != null) Vehicles.Remove(known);
                continue;
            }
            var c = v.Center;
            float d = eye.DistanceTo(c);
            bool vis = false;
            // Mounted crews have periscopes and a commander looking all round.
            // By eye, a vehicle is spotted out to ~600 m (further if it's moving or firing);
            // a crew's optics ~900 m; recon with binoculars ~1500 m.
            float range = _b.Squad?.Kind == SquadKind.Recon ? 1500f : _b.Ride != null ? 900f : 600f;
            if (v.Speed * v.Speed > 4f || Clock.Now - v.LastFired < 3.0) range *= 1.3f;
            // A helicopter against the sky, and you hear it long before: no need to be looking its way.
            bool flying = v.Def.Air && !v.Landed;
            if (flying) range = MathF.Max(range, 2200f);
            if (d < range && (_b.Ride != null || flying || Mathf.RadToDeg(look.AngleTo(c - eye)) < 110f))
                // Hull-down, only the turret may show: check it too, and remember which part we saw.
                foreach (var p in new[] { c, v.TopPoint })
                {
                    var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, p, 0xFFFFFFFF, _selfOnly));
                    if (hit.Count == 0 || hit["collider"].AsGodotObject() == v || hit["position"].AsVector3().DistanceTo(p) < 1.2f) { vis = true; c = p; break; }
                }
            if (!vis)
            {
                if (known != null) known.Visible = false;
                continue;
            }
            if (known == null)
            {
                known = new VehicleThreat { Who = v };
                Vehicles.Add(known);
                Radio.Report(_b, RadioKind.Armor, c, v);
            }
            else if (now - known.LastSeen > 20.0) Radio.Report(_b, RadioKind.Armor, c, v);
            known.Visible = true;
            known.LastSeen = now;
            known.LastKnownPos = c;
            known.AimPoint = c;
        }
        Vehicles.RemoveAll(x => now - x.LastSeen > 60.0 || !GodotObject.IsInstanceValid(x.Who)); // also ones moved (freed and respawned elsewhere)
    }

    bool LineTo(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, ICombatant c) => LineTo(space, from, to, c, out _);

    /// <param name="blockedAt">Where the line was blocked (only meaningful when it returns false).</param>
    bool LineTo(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, ICombatant c, out Vector3 blockedAt)
    {
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to, 0xFFFFFFFF, _selfOnly));
        blockedAt = hit.Count > 0 ? hit["position"].AsVector3() : to;
        return hit.Count == 0 || hit["collider"].AsGodotObject() == c;
    }

    void Look(float dt, double now)
    {
        var space = _b.GetWorld3D().DirectSpaceState;
        var eye = _b.EyePos;
        var look = _b.Aim.Dir;

        foreach (var c in Combatants.All)
        {
            if (!c.Alive || c.Team == _b.Team) continue;
            var t = Find(c);
            var to = c.ChestPos - eye;
            float d = to.Length();
            float ang = Mathf.RadToDeg(look.AngleTo(to));
            // Far enemies are looked for less often (eyes on the near fight): every third tick
            // past 150 m, every sixth past 300 m.
            bool seen = t?.Visible ?? false;
            int every = d > 300f ? 6 : d > 150f ? 3 : 1;
            if (every > 1 && (_tick + c.GetHashCode()) % every != 0 && !seen) continue;

            bool head = false, upper = false, chest = false, hip = false;
            if (d < 400f && ang < 100f)
            {
                var hp = Combatants.PointOn(c, Combatants.Part.Head);
                head = LineTo(space, eye, hp, c, out var at);
                // A hill or building well short of them hides the whole body: skip the other rays.
                // (Low walls right in front of them are close to the target, so those still get the full check.)
                bool farBlock = !head && !seen && at.DistanceTo(hp) > 6f;
                if (!farBlock)
                {
                    upper = LineTo(space, eye, Combatants.PointOn(c, Combatants.Part.UpperChest), c);
                    chest = LineTo(space, eye, Combatants.PointOn(c, Combatants.Part.Chest), c);
                    hip = LineTo(space, eye, Combatants.PointOn(c, Combatants.Part.Hip), c);
                }
            }
            int n = (head ? 1 : 0) + (upper ? 1 : 0) + (chest ? 1 : 0) + (hip ? 1 : 0);

            if (n == 0)
            {
                if (t != null)
                {
                    t.Visible = false;
                    t.Awareness = Mathf.Max(0f, t.Awareness - dt * 0.15f);
                }
                continue;
            }

            t ??= Get(c);
            t.HeadVisible = head;
            t.UpperVisible = upper;
            t.ChestVisible = chest;
            t.HipVisible = hip;

            if (now - t.LastSeen < 1.5) t.Awareness = 1f; // still tracking someone we just had eyes on
            else
            {
                float angle = ang < 20f ? 1f : ang < 50f ? 0.7f : 0.35f;
                // Recon glassing from a stationary post sees much further (binoculars); so does a set-up weapons team.
                float scale = _b.Squad?.Kind == SquadKind.Recon && !_b.Moving ? 260f : _b.Squad?.Kind == SquadKind.Weapons && !_b.Moving ? 150f : 110f;
                float dist = 1f / (1f + (d / scale) * (d / scale)) * (d < 15f ? 2f : 1f);
                float stance = c.BodyHeight < 1.0f ? 0.35f : c.BodyHeight < 1.5f ? 0.7f : 1f;
                float motion = 1f + c.Vel.Length() / 2.5f;
                float firing = now - c.LastShotTime < 1.0 ? 4f : 1f;
                float rate = 2f * _b.P.SpotRate * (n / 4f) * angle * dist * stance * motion * firing * (1f - _b.Suppression * 0.5f);
                t.Awareness = Mathf.Min(1f, t.Awareness + rate * dt);
            }

            if (t.Awareness >= 1f)
            {
                bool freshContact = !t.Confirmed || now - t.LastSeen > 5.0;
                if (!t.Visible)
                {
                    t.GapBeforeVisible = now - t.LastSeen;
                    t.VisibleSince = now;
                }
                t.Visible = true;
                t.Confirmed = true;
                t.LastSeen = now;
                _lastSighting = now;
                t.LastKnownPos = c.FeetPos;
                if (freshContact) _b.Brain.OnSpotted(t);
                Intel.Report(_b.Team, c, c.FeetPos);
                if (_b.Squad?.Kind == SquadKind.Recon && now - t.Reported > 3.0)
                {
                    t.Reported = now;
                    Squad.ReportSpotted(_b.Team, c, c.FeetPos);
                }
            }
            else
            {
                t.Visible = false;
                // A glimpse: "something moved over there" — worth a look, not a shot.
                if (t.Awareness > 0.35f)
                {
                    t.LastHeard = now;
                    t.LastKnownPos = c.FeetPos + new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f)) * d * 0.05f;
                }
            }
        }
    }

    void Listen(double now)
    {
        var eye = _b.EyePos;
        foreach (var e in SoundWorld.I.History)
        {
            // Only sounds made by combatants still in the world (a previous round's are gone).
            if (e.Source == null || !Combatants.All.Contains(e.Source) || e.Source.Team == _b.Team || !e.Source.Alive) continue;
            float d = e.Pos.DistanceTo(eye);
            double arrive = e.T0 + d / SoundWorld.SpeedOfSound;
            if (arrive <= _lastTick || arrive > now) continue;
            // Footsteps are mixed quiet for the player's ears, but an alert soldier listens for them.
            float level = SoundWorld.I.LevelAt(e.Kind, d, e.GainDb) + (e.Kind == Snd.Footstep ? 8f : 0f);
            if (level < -44f) continue;

            var t = Get(e.Source);
            if (t.Visible) continue;
            bool loud = e.Kind is Snd.Rifle556 or Snd.Rifle762 or Snd.Explosion or Snd.Shell;
            float spread = d * (loud ? 0.05f : 0.12f) * (1.6f - _b.P.Skill);
            var est = e.Pos + new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f)) * spread;
            est.Y = _b.FeetPos.Y;
            t.LastHeard = now;
            t.LastKnownPos = est;
            t.Awareness = Mathf.Max(t.Awareness, loud ? 0.6f : 0.3f);
            if (loud) t.Confirmed = true; // gunfire means somebody is definitely there
        }
    }

    /// <summary>A teammate called this contact out.</summary>
    public void Share(ICombatant who, Vector3 pos)
    {
        if (!Combatants.All.Contains(who) || !who.Alive) return;
        var t = Get(who);
        if (t.Visible) return;
        t.LastKnownPos = pos;
        t.LastHeard = Clock.Now;
        t.Confirmed = true;
        t.Awareness = Mathf.Max(t.Awareness, 0.5f);
    }

    /// <summary>Shot at or hit: we know roughly where it came from.</summary>
    public void Alert(ICombatant who, float errorFrac)
    {
        if (!Combatants.All.Contains(who) || !who.Alive) return;
        var t = Get(who);
        if (t.Visible) return;
        float d = who.FeetPos.DistanceTo(_b.FeetPos);
        t.LastKnownPos = who.FeetPos + new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f)) * d * errorFrac;
        t.LastHeard = Clock.Now;
        t.Confirmed = true;
        t.Awareness = Mathf.Max(t.Awareness, 0.8f);
    }
}
