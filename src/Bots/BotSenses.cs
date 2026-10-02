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
    /// <summary>At night: is he against the sky from where we are (checked now and then, not every look).</summary>
    public bool Skylined;
    public double SkyCheck = -99;
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
    static double _nextConditionsLog;

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
        if (DuelMode.Verbose && (now >= _nextConditionsLog || now < _nextConditionsLog - 120.0))
        {
            _nextConditionsLog = now + 60.0;
            GD.Print($"[{now:0}s] conditions: {Conditions.Clock} {Conditions.Weather}, {Conditions.Lux:0.####} lux (light {Conditions.Light:0.00}), " +
                     $"sun {Conditions.SunElevation:0}°, moon {Conditions.MoonLit * 100f:0}% lit at {Conditions.MoonElevation:0}°, visibility {Conditions.VisibilityM:0} m, " +
                     $"noise +{Conditions.NoiseDb:0} dB, night kit {(NightGear.NightKitOn ? "on" : "off")}");
        }
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
            bool gunSight = false;
            // Up in an aircraft, with the ground laid out below: out to 1.5 km, and in a gunship's sight (magnified,
            // day and thermal) out to 3 km ahead.
            if (_b.Ride is { Def.Air: true, Landed: false } mine)
            {
                gunSight = mine.Def.Kind == VKind.AH && Mathf.RadToDeg((mine.Forward with { Y = 0f }).AngleTo((c - eye) with { Y = 0f })) < 60f;
                range = gunSight ? 3000f : 1500f;
            }
            bool fired = Clock.Now - v.LastFired < 3.0;
            if (v.Speed * v.Speed > 4f || fired) range *= 1.3f;
            // A helicopter against the sky, and you hear it long before: no need to be looking its way.
            bool flying = v.Def.Air && !v.Landed;
            if (flying) range = MathF.Max(range, 2200f);
            // The dark and the weather: by night a vehicle is a dark mass seen as far as the light (or the goggles, or a
            // thermal sight, to which a running engine glows) allows, or by its gun flashes; against the sky, a helicopter
            // shows further. Through haze, rain or fog, only so far as there's contrast left: about 0.8 of the visibility
            // for a hull (Koschmieder, for an object of half a black body's contrast), three times that in the thermal.
            // (Broad daylight in clear air: as it always was.)
            if (NightGear.NightKitOn || Conditions.VisibilityM < 12000f)
            {
                var gear = gunSight ? _gear | NightOptic.Thermal : _gear;
                float ang = IsGunner || gunSight ? 0f : Mathf.RadToDeg(look.AngleTo(c - eye));
                var s = NightGear.See(gear, ang, d, NightGear.LightOf(Conditions.LuxAt(c)), _eyeL, flying, fired);
                float clear = Conditions.VisibilityM * (s.By == NightOptic.Thermal ? 2.4f : s.Flash ? 1.2f : 0.8f);
                range = MathF.Min(range * (s.Flash ? 1f : s.Reach), clear);
            }
            // An air defence vehicle's search radar: any aircraft in the open within reach of its guns, day or night.
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
                // Rain's noise masks it: every 9 dB more background costs half the distance (spreading plus the ground's
                // and the air's losses over the last few hundred metres come to about that per doubling).
                float hear = (v.Def.Heavy ? 220f : 140f) * MathF.Pow(2f, -Conditions.NoiseDb / 9f);
                if (loud && d < hear && !flying)
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

    /// <summary>This look's night kit (goggles, sights) and the light round our own eyes (which sets a goggle's gain).</summary>
    NightOptic _gear;
    float _eyeL = 1f;

    void Look(float dt, double now)
    {
        var space = _b.GetWorld3D().DirectSpaceState;
        var eye = _b.EyePos;
        var look = _b.Aim.Dir;
        _gear = NightGear.Of(_b);
        _eyeL = NightGear.LightOf(Conditions.LuxAt(eye));

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

            // The light on him and the air between, and what we're looking with (see NightGear).
            var env = EnvOf(c);
            bool fired = now - c.LastShotTime < FlashFor(c);
            float targetL = LightOn(c, env);
            bool sky = targetL < 0.75f && Skylined(t, c, eye, d, now);
            var sight = NightGear.See(_gear, IsGunner ? 0f : ang, d, targetL, _eyeL, sky, fired);
            // Undergrowth and shade in a wood, the dark inside a room seen from out in the light: a man
            // there is much harder to pick out. (A thermal sight sees through most of it.)
            float hidden = Concealment(c, env);
            if (IsGunner || sight.By == NightOptic.Thermal) hidden = MathF.Sqrt(hidden);

            bool tracking = now - t.LastSeen < 1.5; // still tracking someone we just had eyes on
            // In the dark (or thick weather) a man can be picked out by his flash and then lost again the moment he stops
            // firing: past the range at which he could be made out afresh, there's nothing to follow but the spot the
            // flash was (below the "something moved there" level, so it isn't followed as he moves off). By day, once
            // seen, a man in view is kept in view.
            if (tracking && !fired && (sight.Reach < 0.95f || sight.Trans < 0.5f)
                && SpotRate(c, n, ang, d, hidden, sight, false) < HoldRate)
            {
                tracking = false;
                if (t.Visible) Prof.Count("sight:lost in the dark");
                t.Awareness = MathF.Min(t.Awareness, 0.3f);
            }
            if (tracking) t.Awareness = 1f;
            else t.Awareness = Mathf.Min(1f, t.Awareness + SpotRate(c, n, ang, d, hidden, sight, fired) * dt);

            if (t.Awareness >= 1f)
            {
                bool freshContact = !t.Confirmed || now - t.LastSeen > 5.0;
                if (!t.Visible)
                {
                    t.GapBeforeVisible = now - t.LastSeen;
                    t.VisibleSince = now;
                    t.TleRedraw = now; // a new sighting: a new picture of them
                }
                t.Tle = LocationError(d, n, now - t.VisibleSince, hidden, sight);
                if (freshContact) CountContact(d, sight);
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
    float LocationError(float d, int parts, double watched, float hidden, in Sight sight)
    {
        float optic = IsGunner ? 3f : _b.Def.Scoped ? 2.5f : 1f;
        // A dim shape in the dark or through rain, or only a flash: where exactly the man is, is a rougher guess (up to
        // two and a half times as rough as in daylight, for a figure only just made out).
        float dim = 1f + 1.5f * (1f - Mathf.Clamp(sight.Reach * MathF.Sqrt(sight.Trans), 0f, 1f));
        float mrad = (1f + (4 - parts) * 0.5f) * (1f + 2f * MathF.Exp(-(float)watched / 2.5f)) * (1f + _b.Suppression) * dim / (hidden * optic);
        return d * 0.001f * mrad;
    }

    /// <summary>
    /// How fast someone is being picked out (awareness per second): close, central, standing, moving and firing make it
    /// quicker; distance (how far our eyes carry, in this light), the dark and the air between, cover and being shot at
    /// make it slower.
    /// </summary>
    float SpotRate(ICombatant c, int n, float ang, float d, float hidden, in Sight sight, bool fired)
    {
        bool flash = fired && sight.Flash;
        float angle = ang < 20f ? 1f : ang < 50f ? 0.7f : 0.35f;
        // A flash in the dark catches the eye from the side (the rods at the edge of the eye are the ones that see at night).
        if (flash) angle = MathF.Max(angle, 0.7f);
        float scale = EyesReach() * (flash ? 1f : sight.Reach);
        float dist = 1f / (1f + (d / scale) * (d / scale)) * (d < 15f ? 2f : 1f);
        float stance = c.BodyHeight < 1.0f ? 0.35f : c.BodyHeight < 1.5f ? 0.7f : 1f;
        float motion = 1f + c.Vel.Length() / 2.5f;
        float firing = fired ? 4f : 1f;
        float contrast = (flash ? 1f : sight.Rate) * sight.Trans;
        return 2f * _b.P.SpotRate * (n / 4f) * angle * dist * stance * motion * firing * hidden * contrast * (1f - _b.Suppression * 0.5f);
    }

    /// <summary>Below this (about 25 s to pick him out afresh) a man can't be followed by eye once his flash is gone.</summary>
    const float HoldRate = 0.04f;

    /// <summary>
    /// How long after a shot the shooter stays conspicuous: a second of muzzle flash (and the crack and thump); at
    /// night a machine gun's tracers (one round in four or five in a belt) draw a line back to it for a couple of seconds.
    /// </summary>
    static double FlashFor(ICombatant c)
    {
        if (!NightGear.NightKitOn) return 1.0;
        var def = c is Bot b ? b.Def : c is Player p ? p.Weapon?.Def : null;
        return def is { OpenBolt: true } ? 2.5 : 1.0;
    }

    static EnvKind EnvOf(ICombatant c)
    {
        if (c is Bot b) return b.Brain.Env;
        // The player: worked out against the world, as a bot's own Env is, once for everyone looking and twice a second.
        // (Without the world it could never say "indoors", so a player in a room at night was lit as if he stood in the
        // open, and seen as such.)
        if (c is Node3D n && GodotObject.IsInstanceValid(n))
        {
            double now = Clock.Now;
            if (!ReferenceEquals(_envFor, c) || now > _envAt || now < _envAt - 1.0)
            {
                _envFor = c;
                _envAt = now + 0.5;
                _env = Surroundings.At(n.GetWorld3D().DirectSpaceState, c.FeetPos);
            }
            return _env;
        }
        return Surroundings.At(null, c.FeetPos);
    }

    static ICombatant? _envFor;
    static double _envAt;
    static EnvKind _env;

    /// <summary>
    /// The light on someone (0..1 as Conditions.Light): the sky's, flares' and fires'; under a canopy only a sixth or so of
    /// it reaches the ground (forest-floor measurements put it at 5-20%), and inside a building a few percent (a room's
    /// daylight factor).
    /// </summary>
    static float LightOn(ICombatant c, EnvKind env)
    {
        float lux = Conditions.LuxAt(c.ChestPos);
        if (env == EnvKind.Forest) lux *= 0.15f;
        else if (env == EnvKind.Interior) lux *= 0.03f;
        return NightGear.LightOf(lux);
    }

    /// <summary>
    /// Is he against the sky from here: the line from our eye past his head meets no ground for the next 800 m. Checked
    /// once a second per man (a height-grid walk, only at night).
    /// </summary>
    bool Skylined(Threat t, ICombatant c, Vector3 eye, float d, double now)
    {
        if (d < 20f || Terrain.Main is not { } ground) return false;
        if (now - t.SkyCheck < 1.0) return t.Skylined;
        t.SkyCheck = now;
        var head = Combatants.PointOn(c, Combatants.Part.Head);
        var dir = (head - eye) / MathF.Max(0.1f, d);
        t.Skylined = !ground.Blocks(head + dir * 2f, head + dir * 800f, out _);
        return t.Skylined;
    }

    /// <summary>For measuring: at what range contacts are first made, and with what (count sight:*, printed by DevShot).</summary>
    static void CountContact(float d, in Sight s)
    {
        Prof.Count(d < 50f ? "sight:contact 0-50 m" : d < 100f ? "sight:contact 50-100 m" : d < 200f ? "sight:contact 100-200 m" : d < 400f ? "sight:contact 200-400 m" : "sight:contact 400+ m");
        Prof.Count("sight:contact n");
        Prof.Count("sight:contact sum m", (long)d);
        var (by, sum) = s.Flash ? ("sight:contact by flash", "sight:contact by flash sum m") : s.By switch
        {
            NightOptic.Goggles => ("sight:contact by goggles", "sight:contact by goggles sum m"),
            NightOptic.SightI2 => ("sight:contact by I2 sight", "sight:contact by I2 sight sum m"),
            NightOptic.Thermal => ("sight:contact by thermal", "sight:contact by thermal sum m"),
            _ => ("sight:contact by eye", "sight:contact by eye sum m"),
        };
        Prof.Count(by);
        Prof.Count(sum, (long)d);
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
    float Concealment(ICombatant c, EnvKind env)
    {
        if (c.Ride != null) return 1f; // up on a vehicle: in plain view
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
            // Rain on everything raises the noise floor, and a sound has to stand out of it to be heard (masking):
            // footsteps and movement go under long before gunfire does.
            if (level < -44f + Conditions.NoiseDb)
            {
                if (level >= -44f) Prof.Count(e.Kind == Snd.Footstep ? "hear:footstep lost in the rain" : "hear:lost in the rain");
                continue;
            }

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
