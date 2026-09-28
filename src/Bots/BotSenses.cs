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
    /// <summary>
    /// How far off our idea of exactly where they are may be (m, one sigma), while we can see them. Up close,
    /// nothing; at 250 m a man is a small shape among grass and shadow, and where precisely his chest is,
    /// is a guess: a rough one at a first glimpse of a head over a wall, a good one after a few seconds'
    /// watching someone in the open. (Without it, aimed fire at 200-300 m hit one round in six: range
    /// shooting, not a firefight.)
    /// </summary>
    public float Tle;
    /// <summary>The error this moment, in units of <see cref="Tle"/> (across, up). It holds for a second or
    /// three (a burst goes where we think they are) and then our picture of them changes.</summary>
    public Vector2 TleDir;
    public double TleRedraw;
}

/// <summary>What one bot knows about one enemy vehicle.</summary>
public sealed class VehicleThreat
{
    public Vehicle Who = null!;
    public Vector3 LastKnownPos;
    public double LastSeen = -999, LastHeard = -999;
    public bool Visible;
    /// <summary>When we last knew anything about it (saw it, or heard it moving).</summary>
    public double LastKnown => Math.Max(LastSeen, LastHeard);
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
        using (Prof.Time("look")) Look(dt, now);
        if (_tick % 3 == 0) using (Prof.Time("look:vehicles")) LookForVehicles(now);
        using (Prof.Time("listen")) Listen(now);
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
            // Up in an aircraft, with the ground laid out below: out to 1.5 km, and in a gunship's sight (magnified,
            // day and thermal) out to 3 km ahead.
            if (_b.Ride is { Def.Air: true, Landed: false } mine)
                range = mine.Def.Kind == VKind.AH && Mathf.RadToDeg((mine.Forward with { Y = 0f }).AngleTo((c - eye) with { Y = 0f })) < 60f ? 3000f : 1500f;
            if (v.Speed * v.Speed > 4f || Clock.Now - v.LastFired < 3.0) range *= 1.3f;
            // A helicopter against the sky, and you hear it long before: no need to be looking its way.
            bool flying = v.Def.Air && !v.Landed;
            if (flying) range = MathF.Max(range, 2200f);
            // An air defence vehicle's search radar: any aircraft in the open within reach of its guns.
            if (flying && _b.Ride is { Def.Kind: VKind.SPAA }) range = MathF.Max(range, 3000f);
            if (d < range && (_b.Ride != null || flying || Mathf.RadToDeg(look.AngleTo(c - eye)) < 110f))
                // Hull-down, only the turret may show: check it too, and remember which part we saw.
                foreach (var p in new[] { c, v.TopPoint })
                {
                    if (SmokeScreen.Blocks(eye, p)) continue;
                    var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, p, 0xFFFFFFFF, _selfOnly));
                    if (hit.Count == 0 || hit["collider"].AsGodotObject() == v || hit["position"].AsVector3().DistanceTo(p) < 1.2f) { vis = true; c = p; break; }
                }
            if (!vis)
            {
                if (known != null) known.Visible = false;
                // Out of sight isn't out of mind: an armoured vehicle moving close by is heard
                // (engine, tracks), and where it's heard is where it is.
                bool loud = MathF.Abs(v.Speed) > 1f || Clock.Now - v.LastFired < 2.0;
                if (loud && d < (v.Def.Heavy ? 220f : 140f) && !flying)
                {
                    if (known == null) { known = new VehicleThreat { Who = v, LastKnownPos = c }; Vehicles.Add(known); }
                    known.LastHeard = now;
                    known.LastKnownPos = c;
                }
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
        Vehicles.RemoveAll(x => now - x.LastKnown > 60.0 || !GodotObject.IsInstanceValid(x.Who)); // also ones moved (freed and respawned elsewhere)
    }

    bool LineTo(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, ICombatant c) => LineTo(space, from, to, c, out _);

    /// <param name="blockedAt">Where the line was blocked (only meaningful when it returns false).</param>
    bool LineTo(PhysicsDirectSpaceState3D space, Vector3 from, Vector3 to, ICombatant c, out Vector3 blockedAt)
    {
        // Smoke hides the whole man (reported as blocked well short of him).
        if (SmokeScreen.Blocks(from, to)) { blockedAt = from; return false; }
        // A hill in between: the height grid says so without a ray.
        if (Terrain.Main is { } ground && ground.Blocks(from, to, out blockedAt)) return false;
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
            // past 150 m, every sixth past 300 m, every tenth past 600 m.
            bool seen = t?.Visible ?? false;
            int every = d > 600f ? 10 : d > 300f ? 6 : d > 150f ? 3 : 1;
            if (every > 1 && (_tick + c.GetHashCode()) % every != 0 && !seen) continue;

            bool head = false, upper = false, chest = false, hip = false;
            // Mounted, a crew sees all round (vision blocks, the commander's cupola). How far off
            // someone can actually be picked out is the awareness build-up's business below: a man
            // standing still at 600 m takes a long look, one running or firing much less.
            if (d < SightRange && (ang < 100f || _b.Ride != null))
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
                float scale = EyesReach();
                float dist = 1f / (1f + (d / scale) * (d / scale)) * (d < 15f ? 2f : 1f);
                float stance = c.BodyHeight < 1.0f ? 0.35f : c.BodyHeight < 1.5f ? 0.7f : 1f;
                float motion = 1f + c.Vel.Length() / 2.5f;
                float firing = now - c.LastShotTime < 1.0 ? 4f : 1f;
                // Undergrowth and shade in a wood, the dark inside a room seen from out in the light: a man
                // there is much harder to pick out. (A gunner's thermal sight sees through most of it.)
                float hidden = Concealment(c);
                if (IsGunner) hidden = MathF.Sqrt(hidden);
                float rate = 2f * _b.P.SpotRate * (n / 4f) * angle * dist * stance * motion * firing * hidden * (1f - _b.Suppression * 0.5f);
                t.Awareness = Mathf.Min(1f, t.Awareness + rate * dt);
            }

            if (t.Awareness >= 1f)
            {
                bool freshContact = !t.Confirmed || now - t.LastSeen > 5.0;
                if (!t.Visible)
                {
                    t.GapBeforeVisible = now - t.LastSeen;
                    t.VisibleSince = now;
                    t.TleRedraw = now; // a new sighting: a new picture of them
                }
                t.Tle = LocationError(c, d, n, now - t.VisibleSince);
                if (now >= t.TleRedraw)
                {
                    t.TleDir = new Vector2(_rng.Randfn(0f, 1f), _rng.Randfn(0f, 0.7f)); // the ground they're on pins the height down a little
                    t.TleRedraw = now + _rng.RandfRange(1.5f, 3.5f);
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

    /// <summary>
    /// See <see cref="Threat.Tle"/>. About a milliradian (25 cm at 250 m) for someone in full view, watched a
    /// while; more the less of him shows; three times that at first sight, settling over a few seconds of
    /// watching; more among trees or in a dark room; more when we're being shot at; much less through a
    /// magnified sight.
    /// </summary>
    float LocationError(ICombatant c, float d, int parts, double watched)
    {
        float hidden = Concealment(c);
        if (IsGunner) hidden = MathF.Sqrt(hidden);
        float optic = IsGunner ? 3f : _b.Def.Scoped ? 2.5f : 1f;
        float mrad = (1f + (4 - parts) * 0.5f) * (1f + 2f * MathF.Exp(-(float)watched / 2.5f)) * (1f + _b.Suppression) / (hidden * optic);
        return d * 0.001f * mrad;
    }

    /// <summary>Beyond this nobody is picked out at all (and rays aren't cast).</summary>
    const float SightRange = 1000f;

    bool IsGunner => _b.Ride is { } v && _b.SeatIdx >= 0 && v.Def.Seats[_b.SeatIdx].Role == SeatRole.Gunner;

    /// <summary>
    /// How far our eyes carry: the distance at which picking someone out takes about twice as long as
    /// close up. The naked eye; binoculars at a recon post; a marksman's scope; a set-up weapons team;
    /// a vehicle's gun sight (magnified, and thermal), or its vision blocks.
    /// </summary>
    float EyesReach()
    {
        if (_b.Ride != null) return IsGunner ? 300f : 150f;
        if (_b.Moving) return 110f;
        if (_b.Squad?.Kind == SquadKind.Recon) return 260f;
        if (_b.Role == Role.Marksman) return 180f;
        if (_b.Squad?.Kind == SquadKind.Weapons) return 150f;
        return 110f;
    }

    /// <summary>How well someone's surroundings hide them (1 = in plain view): woods, or inside a building seen from outside it.</summary>
    float Concealment(ICombatant c)
    {
        if (c.Ride != null) return 1f; // up on a vehicle: in plain view
        var env = c is Bot b ? b.Brain.Env : Surroundings.At(null, c.FeetPos);
        if (env == EnvKind.Forest) return 0.55f;
        if (env == EnvKind.Interior && _b.Brain.Env != EnvKind.Interior) return 0.5f;
        return 1f;
    }

    void Listen(double now)
    {
        var eye = _b.EyePos;
        float c = SoundWorld.SpeedOfSound;
        foreach (var e in SoundWorld.I.History)
        {
            if (e.Source == null || e.Source.Team == _b.Team) continue;
            // A blast is heard where the round or grenade landed, not where whoever fired or threw it
            // is: it says nothing about where they are (the blast itself is felt through Combatants.Blast).
            if (e.Kind is Snd.Explosion or Snd.Shell) continue;
            // Only what reached our ears since we last listened: most of the history has been heard already, or not yet.
            float d = e.Pos.DistanceTo(eye);
            double arrive = e.T0 + d / c;
            if (arrive <= _lastTick || arrive > now) continue;
            // Only sounds made by combatants still in the world (a previous round's are gone).
            if (!e.Source.Alive || !Combatants.Contains(e.Source)) continue;
            // Footsteps are mixed quiet for the player's ears, but an alert soldier listens for them.
            float level = SoundWorld.I.LevelAt(e.Kind, d, e.GainDb) + (e.Kind == Snd.Footstep ? 8f : 0f);
            if (level < -44f) continue;

            var t = Get(e.Source);
            if (t.Visible) continue;
            bool loud = e.Kind is Snd.Rifle556 or Snd.Rifle762;
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
        if (who.Team == _b.Team || !Combatants.Contains(who) || !who.Alive) return;
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
        if (who.Team == _b.Team || !Combatants.Contains(who) || !who.Alive) return;
        var t = Get(who);
        if (t.Visible) return;
        float d = who.FeetPos.DistanceTo(_b.FeetPos);
        t.LastKnownPos = who.FeetPos + new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f)) * d * errorFrac;
        t.LastHeard = Clock.Now;
        t.Confirmed = true;
        t.Awareness = Mathf.Max(t.Awareness, 0.8f);
    }
}
