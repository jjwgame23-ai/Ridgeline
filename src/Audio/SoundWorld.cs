using Godot;

namespace Ridgeline;

public enum Snd
{
    Rifle556, Rifle762, Explosion, Crack, Steel, Impact, Footstep, Click, MagOut, MagIn, Bolt, Bounce, Launcher, Build, Bag, Hmg, Autocannon, Cannon, Rocket, ArmorHit, MortarFire, Whistle, Beep, Shell, DoorOpen, DoorClose,
    CrackHeavy, Ricochet, HitDirt, HitSand, HitStone, HitRock, HitWood, HitMetal, HitFlesh,
}

/// <summary>
/// Every sound in the world goes through here. A sound is an event at a point;
/// its wavefront expands at the speed of sound (for the map's air, carried a little by the
/// wind), and it only starts playing when that wavefront reaches the listener. So a steel
/// plate at 600 m rings ~1.8 s after you see it swing, and a sniper's crack arrives before
/// his gunshot.
///
/// Anything anywhere on the map can emit — including fights that are being
/// simulated abstractly far away — and it will be heard with the right delay,
/// distance filtering and loudness. AI hearing reads History.
/// </summary>
public partial class SoundWorld : Node3D
{
    public static SoundWorld I { get; private set; } = null!;
    /// <summary>For the map's temperature and humidity (Acoustics.SetClimate).</summary>
    public static float SpeedOfSound => Acoustics.SpeedOfSound;
    const int VoiceCount = 96, TailCount = 40;

    sealed class Def
    {
        public AudioStreamWav[][] Layers = null!;   // [distance layer][variant]
        public float[] Reps = Array.Empty<float>();   // the distance each layer was made for (blended by chance)
        public float[] LayerDb = { 0f };
        public float BaseDb;
        public float Falloff = 20f;                   // dB lost per tenfold distance
        public float Audible = 100f;
        public float PitchVar = 0.03f;
        public float Directivity;                     // 1 = a gun's muzzle blast (loudest ahead), 0 = all round
    }

    /// <summary>A sound that happened, kept for a few seconds so AI can hear it when its wavefront reaches them.</summary>
    public struct Heard
    {
        public Snd Kind;
        public Vector3 Pos;
        public double T0;
        public float GainDb;
        public ICombatant? Source;
    }

    struct Pending
    {
        public Snd Kind;
        public Vector3 Pos, Facing;
        public double T0;
        public float GainDb, C;
        public Surface Surface;
        public bool Dry; // no reverb feed and no echoes (grenade fragments' cracks: a dozen at once)
    }

    readonly Dictionary<Snd, Def> _defs = new();
    readonly Def[] _steps = new Def[Enum.GetValues<Surface>().Length];
    readonly List<Pending> _pending = new();
    readonly List<Heard> _history = new();
    readonly float[] _voiceDb = new float[VoiceCount];
    readonly RandomNumberGenerator _rng = new();
    AudioStreamPlayer3D[] _voices = null!, _tails = null!;
    readonly float[] _tailDb = new float[TailCount];
    AudioStreamPlayer _ear = null!;
    int _worldBus;

    public Vector3 ListenerPos { get; private set; }
    /// <summary>Where the player's ears are when that isn't the camera (flying a drone off a screen).</summary>
    public static Node3D? Ears;
    public int PendingCount => _pending.Count;
    public List<Heard> History => _history;
    public int ActiveVoices { get { int n = 0; foreach (var v in _voices) if (v.Playing) n++; return n; } }

    /// <summary>Fired at emission time (not arrival).</summary>
    public event Action<Snd, Vector3, float>? Emitted;

    public override void _EnterTree() => I = this;

    public override void _Ready()
    {
        SetupBuses();
        BuildDefs();
        _rng.Randomize();
        StartWeather();

        _voices = new AudioStreamPlayer3D[VoiceCount];
        for (int i = 0; i < VoiceCount; i++)
        {
            var v = new AudioStreamPlayer3D
            {
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled, // we do distance ourselves
                AttenuationFilterDb = 0f,
                MaxDistance = 0f,
                DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
                Bus = "World",
            };
            AddChild(v);
            _voices[i] = v;
        }
        _tails = new AudioStreamPlayer3D[TailCount];
        for (int i = 0; i < TailCount; i++)
        {
            var v = new AudioStreamPlayer3D
            {
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled, AttenuationFilterDb = 0f, MaxDistance = 0f,
                DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled, Bus = "Tail",
                PanningStrength = 0.35f, // a reverberant field comes from all round, only loosely from the source's side
            };
            AddChild(v);
            _tails[i] = v;
        }
        _ear = new AudioStreamPlayer { Stream = SoundSynth.Tinnitus(), Bus = "Ear" };
        AddChild(_ear);
        // The buses outlive a scene: a player deafened a moment before a restart mustn't start the next one muffled.
        ApplyHearing();
    }

    /// <summary>Make a bus (once per session) after the ones before it, sending to one made earlier.</summary>
    static int Bus(string name, string send, params AudioEffect[] effects)
    {
        int i = AudioServer.GetBusIndex(name);
        if (i >= 0) return i;
        AudioServer.AddBus();
        i = AudioServer.BusCount - 1;
        AudioServer.SetBusName(i, name);
        AudioServer.SetBusSend(i, send);
        foreach (var e in effects) AudioServer.AddBusEffect(i, e);
        return i;
    }

    /// <summary>
    /// The buses. A bus can only send to one made before it (Godot mixes from the last bus to
    /// the first), so they're made in this order. Godot's shelf filters square their gain: a
    /// Gain of A cuts the top end by A² (e.g. 0.63 is -8 dB).
    /// </summary>
    void SetupBuses()
    {
        _worldBus = Bus("World", "Master",
            new AudioEffectCompressor { Threshold = -14f, Ratio = 3f, AttackUs = 50f, ReleaseMs = 250f },
            // Blast deafness: the world goes dull (this filter) and quiet (the bus volume), then recovers.
            new AudioEffectLowPassFilter { CutoffHz = 20000f, Resonance = 0.5f });

        // The space you're in: its reverberant field. Fed a copy of every sound, wet only; the
        // room size, damping and pre-delay follow where the listener is (see UpdateSpace). Godot's
        // reverb has no real pre-delay (its "predelay" only times a feedback echo), so a delay
        // line in front gives the gap before the first reflections.
        Bus("Tail", "World",
            new AudioEffectDelay { Dry = 0f, Tap1Active = true, Tap1DelayMs = 40f, Tap1LevelDb = 0f, Tap1Pan = 0f, Tap2Active = false, FeedbackActive = false },
            new AudioEffectReverb { Dry = 0f, Wet = 1f, RoomSize = 0.6f, Damping = 0.1f, Spread = 1f, Hipass = 0.15f, PredelayMsec = 40f, PredelayFeedback = 0.3f });

        // From behind you: the outer ear shades the highs (-9 dB up top). On headphones that's most of
        // what tells front from back.
        Bus("Behind", "World", new AudioEffectHighShelfFilter { CutoffHz = 3500f, Gain = 0.6f });

        // Through a shut door: doors leak round their edges, so the loss is nearly flat
        // (15-20 dB, applied in Play) with the top end a little duller.
        Bus("Door", "World", new AudioEffectHighShelfFilter { CutoffHz = 1200f, Gain = 0.63f });
        // Through a wall: the loss climbs with frequency (mass law), so the crack is gone and the
        // thump comes through. One biquad (12 dB an octave): with the flat 20 dB of Play, the top
        // end ends up near what real masonry lets through.
        Bus("Wall", "World", new AudioEffectLowPassFilter { CutoffHz = 420f, Resonance = 0.6f, Db = AudioEffectFilter.FilterDB.Filter6Db });
        // Behind a gun's muzzle the blast is quieter and thinner: its lows are thrown forward more
        // than its highs. Right behind a shooter, their body shades the highs too.
        Bus("Thin", "World", new AudioEffectLowShelfFilter { CutoffHz = 500f, Gain = 0.75f });
        Bus("Dull", "World", new AudioEffectHighShelfFilter { CutoffHz = 1800f, Gain = 0.63f });
        // The same, heard from behind you: through the ear's rear shading as well. (Nothing is left
        // above 3.5 kHz through a wall to shade.)
        Bus("ThinBehind", "Behind", new AudioEffectLowShelfFilter { CutoffHz = 500f, Gain = 0.75f });
        Bus("DullBehind", "Behind", new AudioEffectHighShelfFilter { CutoffHz = 1800f, Gain = 0.63f });
        Bus("DoorBehind", "Behind", new AudioEffectHighShelfFilter { CutoffHz = 1200f, Gain = 0.63f });
        // The same losses on the way into the reverb of the room you're in.
        Bus("TailDoor", "Tail", new AudioEffectHighShelfFilter { CutoffHz = 1200f, Gain = 0.63f });
        Bus("TailWall", "Tail", new AudioEffectLowPassFilter { CutoffHz = 420f, Resonance = 0.6f, Db = AudioEffectFilter.FilterDB.Filter6Db });

        // Rain, heard through whatever is between you and it: nothing outdoors, the roof indoors (UpdateRain).
        Bus("Rain", "World", new AudioEffectLowPassFilter { CutoffHz = 20000f, Resonance = 0.5f });

        Bus("Ear", "Master");
        if (AudioServer.GetBusEffectCount(0) == 0) AudioServer.AddBusEffect(0, new AudioEffectHardLimiter { CeilingDb = -0.5f });
    }

    /// <summary>A single-layer sound: its variants, made from seeds 1000, 1017, ...</summary>
    static AudioStreamWav[][] Layered(int variants, Func<int, AudioStreamWav> gen) =>
        new[] { Enumerable.Range(0, variants).Select(v => gen(1000 + v * 17)).ToArray() };

    /// <summary>
    /// The distances each band of a loud sound is propagated to: close enough together (at most
    /// ~2x apart past 5 m) that picking one of two neighbours by chance is a small step in timbre,
    /// not the jump between a near and a far version of the gun.
    /// </summary>
    static readonly float[] Bands = { 1.5f, 5f, 12f, 25f, 50f, 100f, 180f, 320f, 560f, 900f, 1400f, 2400f };

    /// <summary>The bands a sound needs: up to the first at or beyond where it can still be heard.</summary>
    static float[] BandsTo(float audible) => Bands.Where((d, i) => i == 0 || Bands[i - 1] < audible).ToArray();

    /// <summary>
    /// A crack's bands, by the distance from where its shock left the bullet (about the miss
    /// distance: 1.1x at Mach 2.5). Its N-wave lengthens as it spreads (Whitham: T ~ b^1/4),
    /// from a whip-crack at a metre to a snap at a hundred.
    /// </summary>
    static readonly float[] CrackBands = { 1.5f, 4f, 10f, 25f, 60f, 150f, 300f };

    /// <summary>
    /// Whitham's N-wave duration at miss distance b, for a bullet of diameter d and length l
    /// at Mach m: T = 1.82 M b^1/4 d / (c (M²-1)^3/8 l^1/4).
    /// </summary>
    static float NWaveT(float b, float d, float l, float m) =>
        1.82f * m * MathF.Pow(b, 0.25f) * d / (SpeedOfSound * MathF.Pow(m * m - 1f, 0.375f) * MathF.Pow(l, 0.25f));

    public static string Report = "";

    SoundSynth.Banded Banded(Func<int, SoundSynth.Dry> dry, int variants, int seed, float audible, float hs = 1.5f, float keep = 0.75f) =>
        SoundSynth.Distanced(dry, variants, seed, BandsTo(audible), keep, hs);

    Def FromBands(SoundSynth.Banded b, float[] reps, float baseDb, float falloff, float audible, float pitchVar, float directivity = 0f) =>
        new() { Layers = b.Layers, Reps = reps, LayerDb = b.LayerDb, BaseDb = baseDb, Falloff = falloff, Audible = audible, PitchVar = pitchVar, Directivity = directivity };

    void BuildDefs()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Loud sounds are made physically: a dry source propagated through air and over the
        // ground to each band's distance (Acoustics). Spreading loss is applied at play time,
        // gentler than the physical 20 dB per tenfold distance so a fight 1-2 km away stays
        // present. The bands hold only what the air and the ground do to the sound over that
        // distance: echoes and the reverberant field are added live from where the listener
        // actually is (Play).
        // keep: how much of the loudness lost with distance (to the air, the ground, and for a
        // rifle its thump fading) the far bands keep. Physically 1; guns keep well under half, so
        // distant fights stay present in the mix.
        Def Gun(Snd kind, Func<int, SoundSynth.Dry> dry, int variants, int seed, float audible, float hs, float baseDb, float falloff, float pitchVar, float directivity, float keep = 0.75f)
        {
            var b = Banded(dry, variants, seed, audible, hs, keep);
            var def = FromBands(b, BandsTo(audible), baseDb, falloff, audible, pitchVar, directivity);
            _defs[kind] = def;
            Report += $"\n  {kind}: {b.Report}";
            return def;
        }
        Report = "";
        Gun(Snd.Rifle556, s => SoundSynth.GunshotDry(s, 1f), 4, 1000, 5000f, 1.5f, 2f, 12f, 0.04f, 1f, 0.35f);
        Gun(Snd.Rifle762, s => SoundSynth.GunshotDry(s, 1.3f), 4, 1500, 5500f, 1.5f, 3.5f, 12f, 0.04f, 1f, 0.4f);
        Gun(Snd.Hmg, s => SoundSynth.GunshotDry(s, 1.9f), 3, 2500, 6500f, 2.5f, 5f, 12f, 0.03f, 1f, 0.5f);
        Gun(Snd.Autocannon, SoundSynth.AutocannonDry, 3, 3000, 7000f, 2.5f, 5.5f, 12f, 0.03f, 1f);
        Gun(Snd.Cannon, SoundSynth.CannonDry, 3, 3500, 9000f, 2.5f, 9.5f, 12f, 0.04f, 1f);
        // Explosions sit on the ground: a low source, so the ground takes more of the mids far off.
        Gun(Snd.Explosion, s => new SoundSynth.Dry(SoundSynth.ExplosionDry(s)), 3, 2000, 8000f, 0.6f, 6f, 11.5f, 0.06f, 0f);
        // A shell or mortar bomb: the same physics with four times the charge: a longer, deeper
        // blast wave (its duration grows with the cube root of the charge), heard further.
        Gun(Snd.Shell, s => new SoundSynth.Dry(SoundSynth.Stretch(SoundSynth.ExplosionDry(s), 1.45f)), 3, 2100, 9000f, 0.6f, 8.5f, 11.5f, 0.05f, 0f);
        Gun(Snd.MortarFire, SoundSynth.MortarDry, 3, 2200, 3500f, 1.0f, 6f, 13f, 0.05f, 0f);
        Gun(Snd.Rocket, SoundSynth.RocketDry, 3, 2300, 2500f, 1.4f, 4f, 14f, 0.05f, 0f);
        Gun(Snd.Launcher, SoundSynth.LauncherDry, 3, 2400, 600f, 1.4f, -4f, 16f, 0.05f, 0f);
        // A plate at 600 m has lost its top partials to the air: a "tunk", not a "ting".
        Gun(Snd.Steel, SoundSynth.SteelDry, 3, 2600, 1500f, 1.2f, -2f, 10f, 0.02f, 0f, 0.3f);
        Gun(Snd.ArmorHit, SoundSynth.ArmorHitDry, 4, 2700, 400f, 1.5f, -8f, 18f, 0.08f, 0f);

        // Cracks, small arms (5.56 / 7.62, as ~6.7 x 26 mm at Mach 2.5) and heavy (.50 to 30 mm).
        // Whitham's peak falls as b^-3/4: 15 dB per tenfold distance.
        foreach (var (kind, dia, len, baseDb) in new[] { (Snd.Crack, 0.0067f, 0.026f, 5f), (Snd.CrackHeavy, 0.018f, 0.075f, 10f) })
        {
            var b = SoundSynth.Distanced((s, r) =>
                {
                    float miss = r / 1.09f;
                    float close = Mathf.Clamp(1f - MathF.Log10(r / 1.5f) / 1.2f, 0f, 1f);
                    return SoundSynth.CrackDry(s, NWaveT(miss, dia, len, 2.5f), close);
                }, true, 4, kind == Snd.Crack ? 4000 : 4100, CrackBands, 1f);
            _defs[kind] = FromBands(b, CrackBands, baseDb, 15f, kind == Snd.Crack ? 400f : 650f, 0.04f);
            Report += $"\n  {kind}: {b.Report}";
        }
        Acoustics.ClearCache();
        Report = $"sound synthesis {sw.ElapsedMilliseconds} ms" + Report;

        // What a round hits sets what it sounds like.
        foreach (var (kind, surface, baseDb, audible) in new[]
                 {
                     (Snd.HitDirt, Surface.Earth, -10f, 120f), (Snd.HitSand, Surface.Sand, -12f, 120f),
                     (Snd.HitStone, Surface.Stone, -8f, 150f), (Snd.HitRock, Surface.Rock, -8f, 150f), (Snd.HitWood, Surface.Wood, -10f, 120f),
                     (Snd.HitMetal, Surface.Metal, -12f, 220f), (Snd.HitFlesh, Surface.Flesh, -8f, 120f),
                 })
            _defs[kind] = new Def { Layers = Layered(4, s => SoundSynth.Hit(surface, s + (int)surface * 101)), BaseDb = baseDb, Falloff = 20f, Audible = audible, PitchVar = 0.1f };
        _defs[Snd.Impact] = _defs[Snd.HitDirt]; // a dull thump into whatever it is (a dud, a drone, a tree)
        _defs[Snd.Ricochet] = new Def { Layers = Layered(4, s => SoundSynth.Ricochet(s)), BaseDb = -16f, Falloff = 18f, Audible = 160f, PitchVar = 0.12f };

        // Footsteps by what's underfoot; a boot on gravel or boards carries further than on turf.
        foreach (var s in Enum.GetValues<Surface>())
        {
            if (s == Surface.Flesh) continue; // nobody walks on people
            // Grass and sand give almost no impact; concrete, boards and steel ring out (Ekimov & Sabatier).
            float gain = s switch { Surface.Sand => -2f, Surface.Rock => 4f, Surface.Stone => 5f, Surface.Wood => 5f, Surface.Metal => 7f, _ => 0f };
            _steps[(int)s] = new Def { Layers = Layered(6, seed => SoundSynth.Step(s, seed + (int)s * 53)), BaseDb = -24f + gain, Falloff = 20f, Audible = 50f, PitchVar = 0.1f };
        }
        _steps[(int)Surface.Flesh] = _defs[Snd.Footstep] = _steps[(int)Surface.Earth];

        _defs[Snd.Click] = new Def { Layers = Layered(2, s => SoundSynth.Click(s)), BaseDb = -14f, Falloff = 20f, Audible = 25f };
        _defs[Snd.MagOut] = new Def { Layers = Layered(2, s => SoundSynth.MagOut(s)), BaseDb = -12f, Falloff = 20f, Audible = 25f };
        _defs[Snd.MagIn] = new Def { Layers = Layered(2, s => SoundSynth.MagIn(s)), BaseDb = -12f, Falloff = 20f, Audible = 25f };
        _defs[Snd.Bolt] = new Def { Layers = Layered(2, s => SoundSynth.Bolt(s)), BaseDb = -10f, Falloff = 20f, Audible = 30f };
        _defs[Snd.Whistle] = new Def { Layers = Layered(3, s => SoundSynth.ShellWhistle(s)), BaseDb = -2f, Falloff = 14f, Audible = 800f, PitchVar = 0.06f };
        _defs[Snd.Beep] = new Def { Layers = Layered(1, s => SoundSynth.Beep(s)), BaseDb = -12f, Falloff = 20f, Audible = 10f, PitchVar = 0f };
        _defs[Snd.Build] = new Def { Layers = Layered(4, s => SoundSynth.Shovel(s)), BaseDb = -14f, Falloff = 20f, Audible = 60f, PitchVar = 0.1f };
        _defs[Snd.Bag] = new Def { Layers = Layered(3, s => SoundSynth.Rustle(s)), BaseDb = -16f, Falloff = 20f, Audible = 30f, PitchVar = 0.1f };
        _defs[Snd.DoorOpen] = new Def { Layers = Layered(3, s => SoundSynth.DoorSound(s, true)), BaseDb = -12f, Falloff = 20f, Audible = 45f, PitchVar = 0.08f };
        _defs[Snd.DoorClose] = new Def { Layers = Layered(3, s => SoundSynth.DoorSound(s, false)), BaseDb = -9f, Falloff = 20f, Audible = 60f, PitchVar = 0.06f };
        _defs[Snd.Bounce] = new Def { Layers = Layered(3, s => SoundSynth.Bounce(s)), BaseDb = -12f, Falloff = 20f, Audible = 60f, PitchVar = 0.1f };
    }

    /// <summary>The sound of a round landing on a surface.</summary>
    public static Snd HitFor(Surface s) => s switch
    {
        Surface.Sand => Snd.HitSand, Surface.Rock => Snd.HitRock, Surface.Stone => Snd.HitStone, Surface.Wood => Snd.HitWood,
        Surface.Metal => Snd.HitMetal, Surface.Flesh => Snd.HitFlesh, _ => Snd.HitDirt,
    };

    /// <summary>
    /// Something made a sound. <paramref name="facing"/> is which way a gun's muzzle pointed (its
    /// blast is loudest ahead); <paramref name="ago"/> is how long before now it left its source
    /// (a crack's shock left the bullet upstream of where it passed you; negative: shortly).
    /// </summary>
    public void Emit(Snd kind, Vector3 pos, float gainDb = 0f, ICombatant? source = null, Vector3 facing = default, float ago = 0f, bool dry = false)
    {
        Emitted?.Invoke(kind, pos, gainDb);
        double t0 = Clock.Now - ago;
        if (source != null) _history.Add(new Heard { Kind = kind, Pos = pos, T0 = t0, GainDb = gainDb, Source = source });
        float d = pos.DistanceTo(ListenerPos);
        if (d > _defs[kind].Audible) return;
        var p = new Pending
        {
            Kind = kind, Pos = pos, T0 = t0, GainDb = gainDb, Facing = facing, Dry = dry,
            C = SpeedOfSound + WindAlong(pos, ListenerPos, d),
            Surface = kind == Snd.Footstep ? Surfaces.Under(GetWorld3D().DirectSpaceState, pos) : Surface.Earth,
        };
        // Within a few metres the delay is under a frame; play now rather than a frame late.
        if (d < 4f && ago == 0f) { Play(p, d); return; }
        _pending.Add(p);
    }

    public override void _Process(double delta)
    {
        using var _p = Prof.Time("sound");
        float dt = (float)delta;
        UpdateHearing(dt);
        UpdateWeather(dt);
        double now = Clock.Now;
        int stale = 0;
        while (stale < _history.Count && now - _history[stale].T0 > 5.0) stale++;
        if (stale > 0) _history.RemoveRange(0, stale);
        Node3D? cam = Ears is { } ears && IsInstanceValid(ears) && ears.IsInsideTree() ? ears : GetViewport().GetCamera3D();
        if (cam != null)
        {
            var at = cam.GlobalPosition;
            // How fast the listener is moving, for Doppler. Cameras move on the 120 Hz physics ticks,
            // so one frame's step is 0, 1 or 2 ticks' worth: measure over 50 ms or more. A jump
            // (respawn, a new seat, a camera cut) isn't motion: nothing here does 100 m/s, so restart there.
            if (_velSpan < 0f || at.DistanceTo(ListenerPos) > MathF.Max(3f, 100f * dt)) { _velFrom = at; _velSpan = 0f; }
            else if ((_velSpan += dt) >= 0.05f)
            {
                _listenerVel += ((at - _velFrom) / _velSpan - _listenerVel) * (1f - MathF.Exp(-_velSpan / 0.1f));
                _velFrom = at;
                _velSpan = 0f;
            }
            ListenerPos = at;
            _listenerFwd = -cam.GlobalBasis.Z;
        }
        UpdateSpace(dt);
        UpdateRain(dt);
        for (int i = _echoes.Count - 1; i >= 0; i--)
        {
            if (now < _echoes[i].At) continue;
            var e = _echoes[i];
            _echoes.RemoveAt(i);
            Voice(e.Stream, e.Pos, e.Db, e.Pitch, e.Bus);
        }
        _echoBudget = MathF.Min(40f, _echoBudget + dt * 30f);

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            float radius = (float)(now - p.T0) * p.C;
            float d = p.Pos.DistanceTo(ListenerPos);
            if (radius < d && now - p.T0 < 30.0) continue;
            if (radius >= d) Play(p, d);
            _pending[i] = _pending[^1];
            _pending.RemoveAt(_pending.Count - 1);
        }
    }

    /// <summary>
    /// A sound's wavefront has reached the listener: work out how it gets here and play it.
    /// - Same space (both outside, or both in the same building): the direct sound, from where
    ///   it happened. Outdoors, behind a hill or a wall: darker and quieter (it went over or
    ///   round). Indoors, round a partition: a little dulled.
    /// - Different spaces (one indoors, or two different buildings): through the openings, via
    ///   Rooms.Find. The listener hears it from the window or doorway it comes in by, after
    ///   the longer way round; through a shut door, 16 dB down and a little dull. With no way
    ///   through, only what the walls pass: the low thump (the Wall bus).
    /// - Outdoors, far off: the wind (downwind carries, upwind there's a shadow) and any forest
    ///   in the way (it scatters the highs).
    /// - A gun is loudest in front of its muzzle, and duller from behind it.
    /// - The reverberant field of the listener's space: a copy to the Tail bus, whose reverb
    ///   follows where the listener is (UpdateSpace). It's fed the gun's all-round output, so a
    ///   shot fired away from you is more reverb and less shot.
    /// - Outdoors and close enough, discrete echoes off nearby walls and cliffs, each from its
    ///   own direction, at its own delay (Echoes).
    /// </summary>
    void Play(in Pending p, float d)
    {
        using var _p = Prof.Time("soundplay");
        var def = p.Kind == Snd.Footstep ? _steps[(int)p.Surface] : _defs[p.Kind];
        var pos = p.Pos;
        bool big = def.Reps.Length > 0 || def.Audible >= 200f;
        var space = GetWorld3D().DirectSpaceState;
        var lb = Rooms.At(ListenerPos);
        var sb = Rooms.At(pos);
        var at = pos;
        float path = d, heardAs = d, extraDb = 0f;
        bool walls = false, viaOpening = false, door = false;
        if (lb != sb)
        {
            if (Rooms.Find(space, sb, pos, lb, ListenerPos) is Rooms.Route r)
            {
                at = r.At;
                path = MathF.Max(r.Length, d);
                viaOpening = true;
                door = r.ThroughClosedDoor;
                // Each opening loses a little (diffraction round its edges). A shut door leaks round
                // its edges: the loss is 15-20 dB and nearly flat, a little duller (the Door bus).
                extraDb = -2.5f * r.Portals - (door ? 16f : 0f);
            }
            else
            {
                // Through the walls: masonry passes the lows and stops the highs (the Wall bus).
                walls = true;
                extraDb = -20f;
            }
        }
        else if (lb != null)
        {
            if (d > 3f && Occluded(pos)) { heardAs = d * 1.6f + 10f; extraDb = -6f; }
        }
        else if (d > 8f && Occluded(pos))
        {
            // Behind a hill or a building: over or round it, darker and quieter, as if from much further away.
            heardAs = big ? d * 3f + 60f : d;
            extraDb = big ? -5f : -8f;
        }
        if (lb == null && sb == null && d > 40f)
        {
            float forest = ForestAlong(pos, ListenerPos);
            // Trunks and leaves scatter the highs: ~2 dB per 100 m at 1 kHz, and at 4-8 kHz about
            // what the air takes over half again the distance.
            extraDb -= 0.018f * forest;
            heardAs += 1.5f * forest;
            var (wDb, wDull) = Refraction(pos, ListenerPos, d);
            extraDb += wDb;
            heardAs *= wDull;
        }

        int layer = LayerFor(def, heardAs, path);
        float db = def.BaseDb + def.LayerDb[layer] - def.Falloff * MathF.Log10(MathF.Max(path, 1f)) + p.GainDb + extraDb;
        // Turbulence makes distant shots swell and fade, from one burst to the next.
        if (d > 40f) db += Scintillation(pos, d);

        // The muzzle's directivity, toward where the sound leaves for the listener. Right by the
        // gun (the shooter's own ears, a squadmate alongside) it's the near field: left alone.
        var toward = (at - pos).LengthSquared() > 0.01f ? at - pos : ListenerPos - pos;
        float cos = MuzzleCos(def, p.Facing, toward);
        float near = Mathf.SmoothStep(2f, 8f, d);
        float direct = db + MuzzleDb(def, p.Facing, cos) * near;
        if (direct < -60f && (!big || db < -50f)) return;

        var variants = def.Layers[layer];
        var stream = variants[_rng.RandiRange(0, variants.Length - 1)];
        float pitch = 1f + _rng.RandfRange(-def.PitchVar, def.PitchVar);
        string bus = walls ? "Wall" : door ? "Door" : near > 0.5f && cos < -0.94f && d < 12f ? "Dull" : near > 0.5f && cos < -0.3f ? "Thin" : "World";
        // A sound arriving by a longer path than the straight line: late by the difference.
        float late = (path - d) / SpeedOfSound;
        if (direct >= -60f)
        {
            if (late > 0.01f) _echoes.Add(new Echo { At = Clock.Now + late, Stream = stream, Pos = at, Db = direct, Pitch = pitch, Bus = bus });
            else Voice(stream, at, direct, pitch, bus);
        }

        if (!big || p.Dry) return;
        // The reverberant field of the listener's space. Indoors (same room) it's strong and close;
        // heard through an opening or the walls, what reverberates is the room around you;
        // outdoors it grows relative to the direct sound with distance.
        // A gun's all-round output is 3 dB under the level MuzzleDb is set against.
        float allRound = p.Facing.LengthSquared() < 0.01f ? 0f : -3f * def.Directivity;
        Tail(stream, at, db + TailRel(d, lb, sb, viaOpening, walls) + allRound, pitch, walls ? "TailWall" : door ? "TailDoor" : "Tail");

        if (lb == null && sb == null && def.Reps.Length > 0 && d < 420f) Echoes(def, pos, d, db, pitch, heardAs, p.Facing);
    }

    /// <summary>Cosine of the angle between a gun's muzzle and a direction; 1 (dead ahead) for a sound with no facing.</summary>
    static float MuzzleCos(Def def, Vector3 facing, Vector3 toward)
    {
        if (def.Directivity <= 0f || facing.LengthSquared() < 0.01f || toward.LengthSquared() < 0.01f) return 1f;
        return facing.Normalized().Dot(toward.Normalized());
    }

    /// <summary>
    /// A muzzle blast's level by angle from the bore. The propellant gas and the blast are thrown
    /// forward: measured on rifles (Maher &amp; Shaw 2010, Routh &amp; Maher 2016) and fitted from
    /// rifles up to a 105 mm tank gun by Fansler's momentum index (mu = 0.78):
    /// 20 log10((mu cos a + sqrt(1 - mu² sin² a)) / (1 + mu)): 0 dB ahead, -9 at the side, -18
    /// behind. Relative to the all-round average that's +6 / -3 / -12; offset to +3 / -6 / -15,
    /// so incoming fire is a little louder than a gun used to be and a squadmate firing away
    /// from you a lot quieter. (The reverb is fed the all-round output, 3 dB down.)
    /// </summary>
    static float MuzzleDb(Def def, Vector3 facing, float cos)
    {
        if (def.Directivity <= 0f || facing.LengthSquared() < 0.01f) return 0f; // no facing given: as it always was
        const float Mu = 0.78f;
        float sin2 = MathF.Max(0f, 1f - cos * cos);
        float beta = (Mu * cos + MathF.Sqrt(1f - Mu * Mu * sin2)) / (1f + Mu);
        return def.Directivity * (20f * MathF.Log10(MathF.Max(beta, 1e-3f)) + 3f);
    }

    FastNoiseLite? _scint;

    /// <summary>
    /// Turbulence scintillation: eddies make a distant sound's level wander (Gaussian in dB),
    /// with a standard deviation from the Rytov log-amplitude variance, saturating (Wenzel) at
    /// ~5.6 dB: about 2 dB at 100 m and 4 dB at 1 km around 700 Hz, more in a stronger wind.
    /// It changes over about half a second and differs by direction, so a burst swells or fades
    /// together while the next one, or another fight, doesn't.
    /// </summary>
    float Scintillation(Vector3 pos, float d)
    {
        _scint ??= new FastNoiseLite { Seed = 17, NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex, Frequency = 1f };
        double k = 2 * Math.PI * 700 / SpeedOfSound, L0 = Acoustics.OuterScale;
        double mu2 = Acoustics.Turbulence * (0.5 + 0.5 * _wind.Length() / MathF.Max(1f, Acoustics.WindMean));
        double chi2 = Math.Sqrt(Math.PI) / 2 * mu2 * k * k * d * L0;
        double sat = 1 / (Math.Sqrt(Math.PI) * mu2 * k * k * L0);
        float sigma = (float)Math.Min(5.6, 8.686 * Math.Sqrt(chi2 / (1 + d / sat)));
        var to = pos - ListenerPos;
        float bearing = MathF.Atan2(to.X, to.Z) * 3f; // ~20 degrees per unit
        float slow = Mathf.Clamp(_scint.GetNoise2D(bearing, (float)Clock.Now / 0.5f) * 2.5f, -2.5f, 2.5f);
        float fast = (_rng.Randf() + _rng.Randf() + _rng.Randf() - 1.5f) * 2f; // ~unit variance
        return sigma * (0.8f * slow + 0.6f * fast);
    }

    /// <summary>
    /// How loud the reverberant field is next to the direct sound. Indoors, in the same room,
    /// it's strong and close. Outdoors it falls off far more slowly than the direct sound
    /// (hills, trees and buildings scatter it back): a hint close up, a few dB under the shot
    /// a kilometre off, which is the rumble of a distant fight.
    /// </summary>
    float TailRel(float d, RoomBox? lb, RoomBox? sb, bool viaOpening, bool walls)
    {
        float lg = MathF.Log10(MathF.Max(d, 10f) / 10f);
        float rel = lb != null
            ? (lb == sb ? -10f : viaOpening ? -13f : -9f)
            : _env switch
            {
                EnvKind.Urban => -16f + MathF.Min(9f, 4.5f * lg),
                EnvKind.Forest => -18f + MathF.Min(8f, 4f * lg),
                _ => -19f + MathF.Min(11f, 5.5f * lg),
            };
        if (walls) rel += 4f; // through the walls, the room's boom is most of what you get
        return rel;
    }

    // ---------------------------------------------------------------- weather

    Vector3 _wind;              // m/s, horizontal: which way it blows and how hard, gusts included
    float _windDir, _windMean, _windTarget, _windSpeed, _gust, _gustT;
    double _windNext;

    void StartWeather()
    {
        _windMean = Acoustics.WindMean;
        _windDir = _rng.Randf() * Mathf.Tau;
        _windSpeed = _windTarget = _windMean * _rng.RandfRange(0.6f, 1.4f);
        _windNext = Clock.Now + _rng.RandfRange(60f, 180f);
        UpdateWeather(0f);
        if (SkyView.RainRate > 0f)
        {
            _rain = new AudioStreamPlayer { Stream = RainLoop(_rng.RandiRange(1, 1 << 20)), Bus = "Rain", VolumeDb = RainDb };
            AddChild(_rain);
            _rain.Play(_rng.Randf() * 5f);
        }
    }

    AudioStreamPlayer? _rain;
    float _rainIn; // 0 outdoors .. 1 under a roof, eased

    /// <summary>
    /// How loud the rain is: the sound comes from the drops' impacts, so its power goes with the kinetic energy
    /// brought down, about in proportion to the rain rate (+10 dB for ten times the rain). Moderate rain
    /// (10 mm/h, ~50-55 dBA in the open) sits some 8 dB under a footstep beside you; a downpour 6 dB over that.
    /// </summary>
    static float RainDb => -26f + 10f * MathF.Log10(MathF.Max(SkyView.RainRate, 0.5f) / 10f);

    /// <summary>
    /// Indoors the rain is on the roof and the walls: masonry passes its low end and stops the patter's highs
    /// (mass law), some 10 dB down overall.
    /// </summary>
    void UpdateRain(float dt)
    {
        if (_rain == null) return;
        float target = Rooms.At(ListenerPos) != null ? 1f : 0f;
        _rainIn += (target - _rainIn) * (1f - MathF.Exp(-dt / 0.3f));
        _rain.VolumeDb = RainDb - 10f * _rainIn;
        if (AudioServer.GetBusEffect(AudioServer.GetBusIndex("Rain"), 0) is AudioEffectLowPassFilter lp)
            lp.CutoffHz = 20000f * MathF.Pow(800f / 20000f, _rainIn);
    }

    /// <summary>
    /// Rain as a seamless stereo loop, different in each ear so it's all round you: the near drops as separate
    /// ticks, thousands a second, most of them faint (drop sizes fall off exponentially and a drop's impact
    /// energy goes as about D^4, so 30 dB between the least and the most), over the hiss of the countless
    /// far ones. Band-limited to where rain on earth and leaves has its energy, ~0.5-8 kHz.
    /// </summary>
    static AudioStreamWav RainLoop(int seed)
    {
        const int rate = SoundSynth.Rate;
        int n = rate * 6, fade = rate / 5, len = n + fade;
        var rnd = new Random(seed);
        var ch = new float[2][];
        float peak = 1e-6f;
        float ah = MathF.Exp(-2f * MathF.PI * 500f / rate), al = 1f - MathF.Exp(-2f * MathF.PI * 8000f / rate);
        for (int c = 0; c < 2; c++)
        {
            var s = new float[len];
            int ticks = (int)(len / (float)rate * 2500f);
            for (int k = 0; k < ticks; k++)
            {
                int at = rnd.Next(len);
                float a = MathF.Pow(10f, -1.5f * (float)rnd.NextDouble());
                float tau = rate * (0.0003f + 0.0012f * (float)rnd.NextDouble());
                float decay = MathF.Exp(-1f / tau), env = a;
                int m = Math.Min(len - at, (int)(tau * 5f));
                for (int i = 0; i < m; i++, env *= decay) s[at + i] += env * ((float)rnd.NextDouble() * 2f - 1f);
            }
            float hiss = 0f, x0 = 0f, hp = 0f, lo = 0f;
            for (int i = 0; i < len; i++)
            {
                hiss += 0.35f * ((float)rnd.NextDouble() * 2f - 1f - hiss);
                float x = s[i] + hiss * 0.25f;
                hp = ah * (hp + x - x0);
                x0 = x;
                lo += al * (hp - lo);
                s[i] = lo;
            }
            // The overrun crossfaded into the start (equal power, for noise): the loop has no seam.
            for (int i = 0; i < fade; i++)
            {
                float t = (i + 0.5f) / fade;
                s[i] = s[i] * MathF.Sqrt(t) + s[n + i] * MathF.Sqrt(1f - t);
            }
            for (int i = 0; i < n; i++) peak = MathF.Max(peak, MathF.Abs(s[i]));
            ch[c] = s;
        }
        var bytes = new byte[n * 4];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 2; c++)
            {
                short q = (short)Math.Clamp((int)(ch[c][i] / peak * 0.8f * 32767f), -32768, 32767);
                bytes[i * 4 + c * 2] = (byte)(q & 0xff);
                bytes[i * 4 + c * 2 + 1] = (byte)((q >> 8) & 0xff);
            }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = true, Data = bytes,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = n,
        };
    }

    /// <summary>
    /// The wind wanders: its direction drifts a few degrees a minute, its strength eases to a new
    /// value every minute or three, and gusts come and go every few seconds. It's what makes a
    /// distant fight swell and fade over a match (see Refraction).
    /// </summary>
    void UpdateWeather(float dt)
    {
        if (Clock.Now > _windNext)
        {
            _windNext = Clock.Now + _rng.RandfRange(60f, 180f);
            _windTarget = _windMean * MathF.Max(0.1f, _rng.RandfRange(0.3f, 1.7f));
        }
        _windSpeed += (_windTarget - _windSpeed) * (1f - MathF.Exp(-dt / 20f));
        _windDir += (_rng.Randf() * 2f - 1f) * dt * 0.02f;
        _gustT -= dt;
        if (_gustT <= 0f) { _gustT = _rng.RandfRange(3f, 9f); _gust = _rng.RandfRange(-0.3f, 0.45f); }
        float u = _windSpeed * (1f + _gust * MathF.Min(1f, _gustT / 2f));
        _wind = new Vector3(MathF.Cos(_windDir), 0f, MathF.Sin(_windDir)) * MathF.Max(0f, u);
    }

    /// <summary>The wind's component along the way from a source to the listener (positive: blowing the sound along).</summary>
    float WindAlong(Vector3 src, Vector3 lis, float d) => d < 1f ? 0f : _wind.Dot((lis - src) with { Y = 0f }) / d;

    /// <summary>
    /// Refraction by the wind. Wind is slower near the ground, so sound going downwind bends
    /// down toward the ground and carries (a few dB up, and the ground dip partly filled);
    /// going upwind it bends up and away, and beyond a shadow boundary (~100 m in a 5 m/s wind,
    /// nearer the stronger it blows) what's left is scattered in by turbulence: many dB down
    /// and duller. Returns the dB change and a factor on how far away it sounds.
    /// </summary>
    (float Db, float Dull) Refraction(Vector3 src, Vector3 lis, float d)
    {
        float u = _wind.Length();
        if (u < 0.3f) return (0f, 1f);
        float along = WindAlong(src, lis, d) / u; // cos of the angle between the wind and the sound's way
        if (along >= 0f)
        {
            float gain = MathF.Min(4f, 0.6f * u) * along * Mathf.SmoothStep(60f, 300f, d);
            return (gain, 1f / (1f + 0.04f * gain));
        }
        float up = -along;
        // The shadow begins tens of metres off (the wind's log profile is steep near the ground)
        // but deepens gradually, diffraction and turbulence filling it: measured shadows run 10-30 dB.
        float shadow = 120f / MathF.Sqrt(u * up);
        float depth = MathF.Min(16f, 2f + 1.5f * u) * up;
        float a = depth * Mathf.SmoothStep(shadow, shadow * 4f, d);
        return (-a, 1f + a / 6f);
    }

    /// <summary>Metres of woodland along the straight way between two points (sampled every ~20 m).</summary>
    static float ForestAlong(Vector3 a, Vector3 b)
    {
        var t = Terrain.Main;
        if (t == null) return 0f;
        float d = a.DistanceTo(b);
        int n = Math.Clamp((int)(d / 20f), 1, 30);
        float step = d / n, total = 0f;
        for (int i = 0; i < n; i++)
        {
            var p = a.Lerp(b, (i + 0.5f) / n);
            total += Mathf.Clamp((t.TreesNear(p.X, p.Z) - 8f) / 24f, 0f, 1f) * step;
        }
        return MathF.Min(total, 400f);
    }

    // ---------------------------------------------------------------- continuous sources

    /// <summary>What a continuous source (an engine, a rotor) is doing acoustically; kept per source, eased.</summary>
    public sealed class LoopShape
    {
        public AudioStreamPlayer3D? Tail;
        public float RefDist = 10f, Falloff = 20f;
        internal float Extra, Cut = 20000f, TExtra, TCut = 20000f;
        internal double NextAt, LastAt = -1;
        internal bool Via, Walls; // how the listener indoors hears it: through an opening, or the walls

        // Where it's been, so it's heard from where it was when the sound now arriving left it:
        // 15 s of it, enough for a helicopter 5 km off.
        const int Trail = 450;
        const double Every = 1.0 / 30;
        readonly Vector3[] _p = new Vector3[Trail];
        readonly double[] _t = new double[Trail];
        int _head = -1, _count;
        internal float Doppler = 1f, BasePitch = 1f, SetPitch = -1f;

        internal void Record(double now, Vector3 pos)
        {
            if (_count > 0 && _p[_head].DistanceTo(pos) > 60f) _count = 0; // a jump (respawn): no motion
            if (_count > 0 && now - _t[_head] < Every) return;
            _head = (_head + 1) % Trail;
            _p[_head] = pos;
            _t[_head] = now;
            _count = Math.Min(_count + 1, Trail);
        }

        /// <summary>
        /// The retarded position: where it was when the sound reaching the listener now left it
        /// (|p(t) - listener| = c (now - t)), and how fast it was moving then.
        /// </summary>
        internal (Vector3 Pos, Vector3 Vel) Retarded(double now, Vector3 pos, Vector3 lis, float c)
        {
            double tPrev = now;
            var pPrev = pos;
            double gPrev = -pos.DistanceTo(lis);
            for (int k = 0; k < _count; k++)
            {
                int i = (_head - k + Trail) % Trail;
                double g = c * (now - _t[i]) - _p[i].DistanceTo(lis);
                if (g >= 0)
                {
                    double f = gPrev / (gPrev - g);
                    var at = pPrev.Lerp(_p[i], (float)f);
                    double span = tPrev - _t[i];
                    var vel = span > 1e-4 ? (pPrev - _p[i]) / (float)span : Vector3.Zero;
                    return (at, vel);
                }
                tPrev = _t[i]; pPrev = _p[i]; gPrev = g;
            }
            // Further off than the trail reaches: the oldest point, moving as it was then.
            if (_count >= 2)
            {
                int o = (_head - _count + 1 + Trail) % Trail, n = (o + 1) % Trail;
                double span = _t[n] - _t[o];
                return (pPrev, span > 1e-4 ? (_p[n] - _p[o]) / (float)span : Vector3.Zero);
            }
            return (pPrev, Vector3.Zero);
        }
    }

    // Per SoundWorld (per map), since it depends on the map's air.
    readonly float[] _cutD = new float[64];
    bool _cutReady;

    /// <summary>
    /// The frequency the air has taken 12 dB off by distance d (ISO 9613-1, the same model the
    /// gunshot bands are made with). Godot's per-voice filter is a high shelf; set to 12 dB down
    /// at this frequency it tracks the air's loss below it and a little above.
    /// </summary>
    float AirCutoff(float d)
    {
        if (!_cutReady)
        {
            for (int i = 0; i < _cutD.Length; i++) _cutD[i] = (float)Acoustics.FrequencyAtLoss(10f * MathF.Pow(1.15f, i), 12);
            _cutReady = true;
        }
        float f = MathF.Log(MathF.Max(d, 10f) / 10f) / MathF.Log(1.15f);
        int k = Math.Clamp((int)f, 0, _cutD.Length - 2);
        float u = Mathf.Clamp(f - k, 0f, 1f);
        return _cutD[k] + (_cutD[k + 1] - _cutD[k]) * u;
    }

    Vector3 _listenerVel, _velFrom;
    float _velSpan = -1f;

    /// <summary>
    /// Put a continuous sound (an engine, a rotor) through the same acoustics as everything
    /// else, every frame: <paramref name="p"/>.VolumeDb on the way in is how loud it is at
    /// RefDist; on the way out it's what reaches the listener. Heard from where it was when the
    /// sound left it (a helicopter a kilometre off is heard ~3 s behind where you see it) and
    /// Doppler-shifted by how fast it was closing; spreading loss past RefDist; the air's treble
    /// loss for the distance; a hill or building in the way (duller and quieter); a listener
    /// indoors hears it through the openings, or through the walls; and a feed to the reverb of
    /// the listener's space.
    /// </summary>
    public void ShapeLoop(AudioStreamPlayer3D p, Vector3 pos, LoopShape s)
    {
        using var _p = Prof.Time("soundloop");
        float srcDb = p.VolumeDb;
        double now = Clock.PhysicsNow; // called per physics tick: stamp the trail with the tick's time
        s.Record(now, pos);
        var (src, vel) = s.Retarded(now, pos, ListenerPos, SpeedOfSound);
        float dist = src.DistanceTo(ListenerPos), d = MathF.Max(dist, 0.5f);
        // Called every physics tick, several per frame: ease by tick time, not by the frame.
        float dt = s.LastAt < 0 ? 0f : (float)(now - s.LastAt);
        s.LastAt = now;

        // Doppler: (c - listener's speed toward it) / (c - its speed toward the listener).
        var u = (ListenerPos - src) / d;
        float c = SpeedOfSound;
        float target = Mathf.Clamp((c - _listenerVel.Dot(u)) / MathF.Max(c * 0.3f, c - vel.Dot(u)), 0.6f, 1.6f);
        s.Doppler += (target - s.Doppler) * (1f - MathF.Exp(-dt / 0.08f));
        // The caller sets the pitch for the revs each frame; don't compound our own shift on it if it didn't.
        if (p.PitchScale != s.SetPitch) s.BasePitch = p.PitchScale;
        p.PitchScale = s.SetPitch = s.BasePitch * s.Doppler;
        p.GlobalPosition = src;
        if (s.Tail is { } tl) tl.GlobalPosition = src;

        RoomBox? lb = _room;
        if (now > s.NextAt)
        {
            s.NextAt = now + 0.2 + _rng.Randf() * 0.1;
            float cut = AirCutoff(d), extra = 0f;
            s.Via = s.Walls = false;
            var space = GetWorld3D().DirectSpaceState;
            if (lb != null)
            {
                if (Rooms.Find(space, null, src, lb, ListenerPos) is Rooms.Route r)
                {
                    s.Via = true;
                    extra = r.ThroughClosedDoor ? -16f : -2.5f;
                    if (r.ThroughClosedDoor) cut = MathF.Min(cut, 1500f);
                }
                else { s.Walls = true; extra = -18f; cut = MathF.Min(cut, 420f); }
            }
            else if (d > 6f && space.IntersectRay(PhysicsRayQueryParameters3D.Create(ListenerPos, src + Vector3.Up * 1f, Layers.World)).Count > 0)
            {
                extra = -7f;
                cut = MathF.Min(cut, 900f);
            }
            else if (d > 40f)
            {
                float forest = ForestAlong(src, ListenerPos);
                var (wDb, wDull) = Refraction(src, ListenerPos, d);
                extra = wDb - 0.018f * forest;
                cut = MathF.Min(cut, AirCutoff(d * wDull + 1.5f * forest));
            }
            s.TExtra = extra;
            s.TCut = cut;
        }
        float k = 1f - MathF.Exp(-dt / 0.25f);
        s.Extra += (s.TExtra - s.Extra) * k;
        s.Cut = MathF.Exp(MathF.Log(s.Cut) + (MathF.Log(s.TCut) - MathF.Log(s.Cut)) * k);
        float db = srcDb - s.Falloff * MathF.Log10(MathF.Max(d, s.RefDist) / s.RefDist) + s.Extra;

        // Godot's inverse-distance model with a 1 m reference only drives its filter; its level is
        // undone here. The filter is a high shelf whose depth at the cutoff is
        // (1 - min(1, final gain)) x AttenuationFilterDb; aim for 12 dB down at the cutoff.
        const float MaxDb = 6f;
        p.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance;
        p.UnitSize = 1f;
        p.MaxDistance = 0f;
        p.MaxDb = MaxDb;
        float room = 1f - MathF.Min(1f, Mathf.DbToLinear(MathF.Min(db, MaxDb)));
        p.AttenuationFilterDb = s.Cut > 18000f ? 0f : Mathf.Clamp(-12f / MathF.Max(room, 0.15f), -80f, 0f);
        p.AttenuationFilterCutoffHz = s.Cut;
        p.VolumeDb = MathF.Min(80f, db + 20f * MathF.Log10(MathF.Max(dist, 0.01f)));

        if (s.Tail is { } t)
        {
            bool on = p.Playing && db > -70f;
            if (on && !t.Playing) t.Play(_rng.Randf() * 2f);
            else if (!on && t.Playing) t.Stop();
            t.PitchScale = p.PitchScale;
            t.AttenuationFilterDb = 0f; // no treble shelf on the reverb feed (Godot's default applies even with no distance model)
            // Engines are steady: a big reverb share of them is a constant wash under everything.
            t.VolumeDb = db + TailRel(d, lb, null, s.Via, s.Walls) - 9f;
        }
    }

    int LayerFor(Def def, float heardAs, float d)
    {
        int layer = 0;
        if (def.Reps.Length > 0)
        {
            // Between two bands, pick one by chance weighted by (log) distance, so
            // walking away from a fight shifts its sound gradually rather than at a line.
            var r = def.Reps;
            while (layer < r.Length - 1 && heardAs > r[layer + 1]) layer++;
            if (layer < r.Length - 1 && heardAs > r[layer])
            {
                float u = MathF.Log(heardAs / r[layer]) / MathF.Log(r[layer + 1] / r[layer]);
                if (_rng.Randf() < u) layer++;
            }
        }
        return layer;
    }

    Vector3 _listenerFwd = Vector3.Forward;

    /// <summary>Play on a free voice (or the quietest one, if this is louder); from behind the listener, through the ear's rear shading too.</summary>
    void Voice(AudioStreamWav stream, Vector3 at, float db, float pitch, string bus = "World")
    {
        int slot = -1, quietestIdx = -1;
        float quietest = float.MaxValue;
        for (int i = 0; i < VoiceCount; i++)
        {
            if (!_voices[i].Playing) { slot = i; break; }
            if (_voiceDb[i] < quietest) { quietest = _voiceDb[i]; quietestIdx = i; }
        }
        if (slot < 0)
        {
            if (db <= quietest) return;
            slot = quietestIdx;
        }
        var v = _voices[slot];
        v.Stop();
        var to = (at - ListenerPos) with { Y = 0f };
        bool behind = to.LengthSquared() > 1f && to.Normalized().Dot(_listenerFwd with { Y = 0f }) < -0.35f;
        v.Bus = !behind ? bus : bus switch { "World" => "Behind", "Thin" => "ThinBehind", "Dull" => "DullBehind", "Door" => "DoorBehind", _ => bus };
        v.Stream = stream;
        v.GlobalPosition = at;
        v.VolumeDb = db;
        v.PitchScale = pitch;
        v.Play();
        _voiceDb[slot] = db;
    }

    void Tail(AudioStreamWav stream, Vector3 at, float db, float pitch, string bus)
    {
        if (db < -60f) return;
        int slot = -1, quietestIdx = -1;
        float quietest = float.MaxValue;
        for (int i = 0; i < TailCount; i++)
        {
            if (!_tails[i].Playing) { slot = i; break; }
            if (_tailDb[i] < quietest) { quietest = _tailDb[i]; quietestIdx = i; }
        }
        if (slot < 0)
        {
            if (db <= quietest) return;
            slot = quietestIdx;
        }
        var v = _tails[slot];
        v.Stop();
        v.Bus = bus;
        v.Stream = stream;
        v.GlobalPosition = at;
        v.VolumeDb = db;
        v.PitchScale = pitch;
        v.Play(0f);
        _tailDb[slot] = db;
    }

    // ---------------------------------------------------------------- echoes

    struct Echo
    {
        public double At;
        public AudioStreamWav Stream;
        public Vector3 Pos;
        public float Db, Pitch;
        public string Bus;
    }

    readonly List<Echo> _echoes = new();
    float _echoBudget = 40f;

    /// <summary>
    /// First reflections outdoors, found for real: rays from the source feel out the walls and
    /// cliffs round it; each surface that the listener can see gives an echo from that
    /// surface's direction, delayed by the extra path, quieter by the extra spreading and by
    /// what the surface absorbs (a wall reflects most of it, broken ground much less). A gun
    /// throws its blast forward, so a cliff in front of the muzzle answers loudest. In a street
    /// that's the slap and flutter off the buildings; in a valley, a cliff answering back.
    /// </summary>
    void Echoes(Def def, Vector3 s, float d, float directDb, float pitch, float heardAs, Vector3 facing)
    {
        if (_echoBudget < 1f) return;
        _echoBudget -= 1f;
        var space = GetWorld3D().DirectSpaceState;
        var src = s + Vector3.Up * 1.5f;
        var found = new List<(Vector3 At, float Len, float Db, bool Thin)>();
        const int Dirs = 12;
        float a0 = _rng.Randf() * Mathf.Tau;
        for (int k = 0; k < Dirs; k++)
        {
            float a = a0 + k * Mathf.Tau / Dirs;
            var dir = new Vector3(MathF.Cos(a), 0.04f, MathF.Sin(a));
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(src, src + dir * 260f, Layers.World));
            if (hit.Count == 0) continue;
            var h = hit["position"].AsVector3();
            var n = hit["normal"].AsVector3();
            if (MathF.Abs(n.Y) > 0.75f) continue; // the ground under us, not a wall or slope facing us
            float len = src.DistanceTo(h) + h.DistanceTo(ListenerPos);
            if (len - d < 9f) continue; // arrives with the direct sound: part of it, not an echo
            // The reflector must be in view from where we are.
            var back = space.IntersectRay(PhysicsRayQueryParameters3D.Create(h + n * 0.6f, ListenerPos, Layers.World));
            if (back.Count > 0) continue;
            bool building = hit["collider"].AsGodotObject() is Node c && !c.IsInGroup("ground");
            float loss = building ? 2.5f : 8f;
            float cos = MuzzleCos(def, facing, dir);
            float db = directDb + MuzzleDb(def, facing, cos) - def.Falloff * MathF.Log10(len / MathF.Max(d, 1f)) - loss - (heardAs > d * 1.5f ? -5f : 0f);
            found.Add((h, len, db, cos < -0.3f));
        }
        foreach (var e in found.OrderByDescending(x => x.Db).Take(3))
        {
            if (e.Db < directDb - 22f || e.Db < -60f) continue;
            int layer = LayerFor(def, e.Len, e.Len);
            var variants = def.Layers[layer];
            _echoes.Add(new Echo
            {
                At = Clock.Now + (e.Len - d) / SpeedOfSound, Stream = variants[_rng.RandiRange(0, variants.Length - 1)],
                Pos = e.At, Db = e.Db, Pitch = pitch, Bus = e.Thin ? "Thin" : "World",
            });
        }
    }

    // ---------------------------------------------------------------- the listener's space

    EnvKind _env = EnvKind.Open;
    RoomBox? _room;
    double _spaceAt;
    float _rs = 0.6f, _damp = 0.1f, _pre = 40f, _echo = 40f, _fb = 0.3f, _hp = 0.15f, _preSet = -1f, _echoSet = -1f;
    float _roomRs = 0.3f, _roomPre = 7f;

    /// <summary>
    /// The reverb of wherever the listener is, eased in over half a second or so:
    /// - a room: its decay from its volume and surfaces (Sabine), bright, with its first
    ///   reflection after a mean free path;
    /// - a street: the buildings either side, a quick slap and a flutter between the facades;
    /// - forest: short and soft, the trunks scatter it and the leaves soak up the highs;
    /// - open ground: sparse and distant, dark, the far hills answering after a while.
    /// pre is the gap before the reverb starts (the delay line); echo and fb time and feed the
    /// reverb's own recirculating echo (the flutter).
    /// Godot's reverb damping runs backwards: 0 is the darkest tail (its combs roll off from
    /// 2.5 kHz), 1 the brightest (10 kHz).
    /// </summary>
    void UpdateSpace(float dt)
    {
        if (Clock.Now > _spaceAt)
        {
            _spaceAt = Clock.Now + 0.4;
            var room = Rooms.At(ListenerPos);
            if (room != _room && room != null) (_roomRs, _roomPre) = RoomReverb(room);
            _room = room;
            _env = _room != null ? EnvKind.Interior : Surroundings.At(null, ListenerPos);
        }
        float rs, damp, pre, echo, fb, hp;
        switch (_env)
        {
            case EnvKind.Interior: rs = _roomRs; damp = 0.6f; pre = _roomPre; echo = _roomPre; fb = 0.15f; hp = 0.03f; break;
            case EnvKind.Urban: rs = 0.45f; damp = 0.4f; pre = 15f; echo = 25f; fb = 0.4f; hp = 0.04f; break;
            case EnvKind.Forest: rs = 0.35f; damp = 0.15f; pre = 8f; echo = 12f; fb = 0.15f; hp = 0.02f; break;
            // Outdoors there are no walls to hold the sound in: a sparse, late, dark return, not a hall.
            default: rs = 0.6f; damp = 0.05f; pre = 60f; echo = 80f; fb = 0.25f; hp = 0f; break;
        }
        float k = 1f - MathF.Exp(-dt / 0.5f);
        _rs += (rs - _rs) * k; _damp += (damp - _damp) * k; _pre += (pre - _pre) * k; _echo += (echo - _echo) * k; _fb += (fb - _fb) * k; _hp += (hp - _hp) * k;
        int bus = AudioServer.GetBusIndex("Tail");
        if (bus < 0) return;
        // Moving a delay jumps its read point: only in steps worth hearing.
        if (AudioServer.GetBusEffect(bus, 0) is AudioEffectDelay dl && MathF.Abs(_pre - _preSet) > 3f) dl.Tap1DelayMs = _preSet = _pre;
        if (AudioServer.GetBusEffect(bus, 1) is AudioEffectReverb rv)
        {
            rv.RoomSize = _rs; rv.Damping = _damp; rv.PredelayFeedback = _fb; rv.Hipass = _hp;
            if (MathF.Abs(_echo - _echoSet) > 3f) rv.PredelayMsec = _echoSet = _echo;
        }
    }

    /// <summary>
    /// A room's reverb: its decay time by Sabine (0.161 V / A), with bare masonry absorbing a few
    /// per cent, the rubble and odd furniture of an empty building a little more, and every
    /// window and doorway absorbing everything that reaches it (it leaves). Real bare rooms ring
    /// a bit shorter than Sabine says. Then the reverb's room size for that decay (its combs
    /// average 31 ms: RT60 = 3 x 0.0312 / -log10(0.7 + 0.28 room size)), and the pre-delay of
    /// a mean free path, 4V/S.
    /// </summary>
    static (float RoomSize, float PreMs) RoomReverb(RoomBox r)
    {
        float w = r.X1 - r.X0, l = r.Z1 - r.Z0, h = MathF.Max(r.Height, 2.5f);
        float v = w * l * h, surf = 2f * (w * l + w * h + l * h);
        float openings = 0f;
        foreach (var p in r.Portals) openings += p.Door == null ? 1.3f : p.Door.IsOpen ? 2f : 0.2f;
        float a = surf * 0.05f + openings;
        float rt = Mathf.Clamp(0.161f * v / a * 0.75f, 0.6f, 6f);
        float g = MathF.Pow(10f, -3f * 0.031247f / rt);
        return (Mathf.Clamp((g - 0.7f) / 0.28f, 0f, 1f), Mathf.Clamp(4f * v / surf / SpeedOfSound * 1000f, 2f, 60f));
    }

    /// <summary>For the HUD/debug: what space the listener is in.</summary>
    public string SpaceName => _env == EnvKind.Interior ? $"room ({_room?.Volume:0} m³)" : Surroundings.Name(_env);

    /// <summary>The wind (m/s, horizontal, gusts included): what the rain drifts with.</summary>
    public Vector3 Wind => _wind;

    /// <summary>For the HUD/debug: the wind, as speed and the compass bearing it blows from.</summary>
    public string WindName => $"{_wind.Length():0.0} m/s from {Mathf.PosMod(Mathf.RadToDeg(MathF.Atan2(-_wind.X, _wind.Z)), 360f):000}"; // compass: north is -Z

    bool Occluded(Vector3 pos)
    {
        var space = GetWorld3D().DirectSpaceState;
        var q = PhysicsRayQueryParameters3D.Create(ListenerPos, pos + Vector3.Up * 0.6f, Layers.World);
        return space.IntersectRay(q).Count > 0;
    }

    /// <summary>
    /// Development aid: every sound as WAV files in a folder (name_layer_variant.wav), plus the
    /// continuous loops, to listen to or analyse outside the game.
    /// </summary>
    public void Dump(string dir)
    {
        DirAccess.MakeDirRecursiveAbsolute(dir);
        var levels = new System.Text.StringBuilder();
        void One(string name, Def def)
        {
            for (int l = 0; l < def.Layers.Length; l++)
                for (int v = 0; v < def.Layers[l].Length; v++)
                    def.Layers[l][v].SaveToWav($"{dir}/{name}_{l}_{v}.wav");
            // name base falloff | band distances | band dB
            levels.AppendLine($"{name} {def.BaseDb} {def.Falloff} | {string.Join(",", def.Reps)} | {string.Join(",", def.LayerDb)}");
        }
        foreach (var (kind, def) in _defs) One(kind.ToString(), def);
        foreach (var s in Enum.GetValues<Surface>()) if (s != Surface.Flesh) One($"Step{s}", _steps[(int)s]);
        System.IO.File.WriteAllText(ProjectSettings.GlobalizePath($"{dir}/levels.txt"), levels.ToString());
        SoundSynth.EngineLoop(false, false).SaveToWav($"{dir}/loop_engine_light.wav");
        SoundSynth.EngineLoop(true, true).SaveToWav($"{dir}/loop_engine_tracked.wav");
        SoundSynth.RotorLoop(false).SaveToWav($"{dir}/loop_rotor.wav");
        SoundSynth.DroneLoop(false).SaveToWav($"{dir}/loop_drone.wav");
        SoundSynth.DroneLoop(true).SaveToWav($"{dir}/loop_fpv.wav");
        RainLoop(1).SaveToWav($"{dir}/loop_rain.wav");
        GD.Print($"sounddump: wrote {dir}\n{Report}");
    }

    /// <summary>
    /// How loud a sound is at distance d, in the same dB scale playback uses: its base level and
    /// spreading only (not the band, the muzzle's direction or what's underfoot). For AI hearing.
    /// </summary>
    public float LevelAt(Snd kind, float d, float gainDb)
    {
        var def = _defs[kind];
        return def.BaseDb - def.Falloff * MathF.Log10(MathF.Max(d, 1f)) + gainDb;
    }

    // ---------------------------------------------------------------- hearing damage

    float _ring, _deaf, _ringTau = 3f, _deafTau = 10f;

    /// <summary>
    /// A blast close enough to hurt your ears. Strength 0..1 (from distance and
    /// whether a wall was in the way). First a ringing that drowns out the world,
    /// which fades into a long stretch where everything sounds muffled and far away.
    /// The closer the blast, the louder, and the longer both last.
    /// </summary>
    public void Deafen(float strength)
    {
        strength = Mathf.Clamp(strength, 0f, 1f);
        if (strength <= 0.02f) return;
        _ring = MathF.Max(_ring, strength);
        _deaf = MathF.Max(_deaf, strength);
        _ringTau = MathF.Max(_ringTau, 1.5f + 5f * strength);
        _deafTau = MathF.Max(_deafTau, 4f + 16f * strength);
        if (!_ear.Playing) _ear.Play();
    }

    /// <summary>Death, respawn, a new round: hearing back to normal.</summary>
    public void ResetHearing()
    {
        _listenerVel = Vector3.Zero;
        _ring = _deaf = 0f;
        _ear.Stop();
        ApplyHearing();
    }

    void UpdateHearing(float dt)
    {
        if (_ring <= 0f && _deaf <= 0f) return;
        _ring *= MathF.Exp(-dt / _ringTau);
        _deaf *= MathF.Exp(-dt / _deafTau);
        if (_ring < 0.01f) { _ring = 0f; _ringTau = 3f; }
        if (_deaf < 0.01f) { _deaf = 0f; _deafTau = 10f; }
        ApplyHearing();
    }

    void ApplyHearing()
    {
        // The ring is mixed well below a gunshot: it masks, it isn't meant to hurt yours.
        if (_ring > 0f) _ear.VolumeDb = -34f + 16f * _ring;
        else if (_ear.Playing) _ear.Stop();
        // Everything else: dull (low-pass sweeping down to ~300 Hz) and quiet, most right after.
        float d = _deaf;
        AudioServer.SetBusVolumeDb(_worldBus, -22f * d * d - 6f * d);
        if (AudioServer.GetBusEffect(_worldBus, 1) is AudioEffectLowPassFilter lp)
            lp.CutoffHz = 20000f * MathF.Pow(300f / 20000f, MathF.Pow(d, 0.6f));
    }
}
