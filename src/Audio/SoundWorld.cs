using Godot;

namespace Ridgeline;

public enum Snd { Rifle556, Rifle762, Explosion, Crack, Steel, Impact, Footstep, Click, MagOut, MagIn, Bolt, Bounce, Launcher, Build, Bag, Hmg, Autocannon, Cannon, Rocket, ArmorHit, MortarFire, Whistle, Beep, Shell, DoorOpen, DoorClose }

/// <summary>
/// Every sound in the world goes through here. A sound is an event at a point;
/// its wavefront expands at the speed of sound, and it only starts playing when
/// that wavefront reaches the listener. So a steel plate at 600 m rings ~1.7 s
/// after you see it swing, and a sniper's crack arrives before his gunshot.
///
/// Anything anywhere on the map can emit — including fights that are being
/// simulated abstractly far away — and it will be heard with the right delay,
/// distance filtering and loudness. AI hearing will subscribe to Emitted.
/// </summary>
public partial class SoundWorld : Node3D
{
    public static SoundWorld I { get; private set; } = null!;
    public const float SpeedOfSound = 343f;
    const int VoiceCount = 96, TailCount = 40;

    sealed class Def
    {
        public AudioStreamWav[][] Layers = null!;   // [distance layer][variant]
        public float[] Bounds = Array.Empty<float>(); // upper distance of each layer but the last (hard switch)
        public float[] Reps = Array.Empty<float>();   // or: the distance each layer was made for (blended by chance)
        public float[] LayerDb = { 0f };
        public float BaseDb;
        public float Falloff = 20f;                   // dB lost per tenfold distance
        public float Audible = 100f;
        public float PitchVar = 0.03f;
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
        public Vector3 Pos;
        public double T0;
        public float GainDb;
    }

    readonly Dictionary<Snd, Def> _defs = new();
    readonly List<Pending> _pending = new();
    readonly List<Heard> _history = new();
    readonly float[] _voiceDb = new float[VoiceCount];
    readonly RandomNumberGenerator _rng = new();
    AudioStreamPlayer3D[] _voices = null!, _tails = null!;
    readonly float[] _tailDb = new float[TailCount];
    AudioStreamPlayer _ear = null!;
    int _worldBus;

    public Vector3 ListenerPos { get; private set; }
    public int PendingCount => _pending.Count;
    public List<Heard> History => _history;
    public int ActiveVoices { get { int n = 0; foreach (var v in _voices) if (v.Playing) n++; return n; } }

    /// <summary>Fired at emission time (not arrival). For AI hearing later.</summary>
    public event Action<Snd, Vector3, float>? Emitted;

    public override void _EnterTree() => I = this;

    public override void _Ready()
    {
        SetupBuses();
        BuildDefs();

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
    }

    void SetupBuses()
    {
        _worldBus = AudioServer.GetBusIndex("World");
        if (_worldBus >= 0) return;
        AudioServer.AddBus();
        _worldBus = AudioServer.BusCount - 1;
        AudioServer.SetBusName(_worldBus, "World");
        AudioServer.SetBusSend(_worldBus, "Master");
        AudioServer.AddBusEffect(_worldBus, new AudioEffectCompressor { Threshold = -14f, Ratio = 3f, AttackUs = 50f, ReleaseMs = 250f });
        // Blast deafness: the world goes dull (this filter) and quiet (the bus volume), then recovers.
        AudioServer.AddBusEffect(_worldBus, new AudioEffectLowPassFilter { CutoffHz = 20000f, Resonance = 0.5f });

        // The space you're in: its reverberant field. Fed a copy of every sound, wet only; the
        // room size, damping and pre-delay follow where the listener is (see UpdateSpace).
        AudioServer.AddBus();
        int tail = AudioServer.BusCount - 1;
        AudioServer.SetBusName(tail, "Tail");
        AudioServer.SetBusSend(tail, "World");
        AudioServer.AddBusEffect(tail, new AudioEffectReverb { Dry = 0f, Wet = 1f, RoomSize = 0.6f, Damping = 0.8f, Spread = 1f, Hipass = 0.15f, PredelayMsec = 40f, PredelayFeedback = 0.3f });

        // From behind you: the outer ear shades the highs a little. On headphones that's most of
        // what tells front from back.
        AudioServer.AddBus();
        int behind = AudioServer.BusCount - 1;
        AudioServer.SetBusName(behind, "Behind");
        AudioServer.SetBusSend(behind, "World");
        AudioServer.AddBusEffect(behind, new AudioEffectHighShelfFilter { CutoffHz = 3500f, Gain = 0.45f });

        AudioServer.AddBus();
        int ear = AudioServer.BusCount - 1;
        AudioServer.SetBusName(ear, "Ear");
        AudioServer.SetBusSend(ear, "Master");

        AudioServer.AddBusEffect(0, new AudioEffectHardLimiter { CeilingDb = -0.5f });
    }

    static AudioStreamWav[][] Layered(int layers, int variants, Func<int, int, AudioStreamWav> gen)
    {
        var r = new AudioStreamWav[layers][];
        for (int l = 0; l < layers; l++)
        {
            r[l] = new AudioStreamWav[variants];
            for (int v = 0; v < variants; v++) r[l][v] = gen(1000 + v * 17 + l * 3, l);
        }
        return r;
    }

    /// <summary>The distances each band of a gun or explosion is propagated to.</summary>
    static readonly float[] Bands = { 5f, 35f, 110f, 280f, 600f, 1200f };

    public static string Report = "";

    void BuildDefs()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Loud sounds are made physically: a dry source propagated through air and
        // off terrain to each band's distance (Acoustics). Spreading loss is applied
        // at play time. The far bands carry the growing share of reverb, so the net loss is
        // gentler than the 20 dB per tenfold distance of the direct sound alone: a fight 1-2 km away stays present.
        // The bands hold only what the air and the ground do to the sound over that distance
        // (high frequencies absorbed, the ground-reflection dip): no reverb. Echoes and the
        // reverberant field are added live from where the listener actually is (Play).
        var s556 = SoundSynth.Distanced(s => SoundSynth.GunshotDry(s, 1f), 4, 1000, Bands, 0f, 0.3f, 0, 0.1f, 0.12f, 0.75f);
        var s762 = SoundSynth.Distanced(s => SoundSynth.GunshotDry(s, 1.3f), 4, 1500, Bands, 0f, 0.3f, 0, 0.1f, 0.12f, 0.75f);
        var boom = SoundSynth.Distanced(SoundSynth.ExplosionDry, 3, 2000, Bands, 0f, 0.3f, 0, 0.1f, 0.3f, 0.75f);
        _defs[Snd.Rifle556] = new Def { Layers = s556.Layers, Reps = Bands, LayerDb = s556.LayerDb, BaseDb = 2f, Falloff = 12f, Audible = 5000f, PitchVar = 0.04f };
        _defs[Snd.Rifle762] = new Def { Layers = s762.Layers, Reps = Bands, LayerDb = s762.LayerDb, BaseDb = 3.5f, Falloff = 12f, Audible = 5500f, PitchVar = 0.04f };
        _defs[Snd.Explosion] = new Def { Layers = boom.Layers, Reps = Bands, LayerDb = boom.LayerDb, BaseDb = 8f, Falloff = 11.5f, Audible = 8000f, PitchVar = 0.06f };
        // A shell or mortar bomb: the same physics with four times the charge: a longer, deeper
        // blast wave (its duration grows with the cube root of the charge), heard further.
        var shell = SoundSynth.Distanced(s => SoundSynth.Stretch(SoundSynth.ExplosionDry(s), 1.45f), 3, 2100, Bands, 0f, 0.3f, 0, 0.1f, 0.3f, 0.75f);
        _defs[Snd.Shell] = new Def { Layers = shell.Layers, Reps = Bands, LayerDb = shell.LayerDb, BaseDb = 10f, Falloff = 11.5f, Audible = 9000f, PitchVar = 0.05f };
        var hmg = SoundSynth.Distanced(s => SoundSynth.GunshotDry(s, 1.9f), 3, 2500, Bands, 0f, 0.3f, 0, 0.1f, 0.15f, 0.75f);
        var ac = SoundSynth.Distanced(SoundSynth.AutocannonDry, 3, 3000, Bands, 0f, 0.3f, 0, 0.1f, 0.15f, 0.75f);
        var cannon = SoundSynth.Distanced(SoundSynth.CannonDry, 3, 3500, Bands, 0f, 0.3f, 0, 0.1f, 0.3f, 0.75f);
        _defs[Snd.Hmg] = new Def { Layers = hmg.Layers, Reps = Bands, LayerDb = hmg.LayerDb, BaseDb = 5f, Falloff = 12f, Audible = 6500f, PitchVar = 0.03f };
        _defs[Snd.Autocannon] = new Def { Layers = ac.Layers, Reps = Bands, LayerDb = ac.LayerDb, BaseDb = 7f, Falloff = 12f, Audible = 7000f, PitchVar = 0.03f };
        _defs[Snd.Cannon] = new Def { Layers = cannon.Layers, Reps = Bands, LayerDb = cannon.LayerDb, BaseDb = 11f, Falloff = 12f, Audible = 9000f, PitchVar = 0.04f };
        Report = $"sound synthesis {sw.ElapsedMilliseconds} ms" +
                 $"\n  5.56: {s556.Report}\n  7.62: {s762.Report}\n  frag: {boom.Report}";

        _defs[Snd.Crack] = new Def { Layers = Layered(1, 4, (s, _) => SoundSynth.Crack(s)), BaseDb = -3f, Falloff = 18f, Audible = 80f, PitchVar = 0.08f };
        _defs[Snd.Steel] = new Def
        {
            Layers = Layered(2, 3, SoundSynth.Steel),
            Bounds = new[] { 150f }, LayerDb = new[] { 0f, -2f },
            BaseDb = -4f, Falloff = 10f, Audible = 1500f, PitchVar = 0.02f,
        };
        _defs[Snd.Impact] = new Def { Layers = Layered(1, 4, (s, _) => SoundSynth.Impact(s)), BaseDb = -10f, Falloff = 20f, Audible = 120f, PitchVar = 0.1f };
        _defs[Snd.Footstep] = new Def { Layers = Layered(1, 6, (s, _) => SoundSynth.Footstep(s)), BaseDb = -24f, Falloff = 20f, Audible = 50f, PitchVar = 0.1f };
        _defs[Snd.Click] = new Def { Layers = Layered(1, 2, (s, _) => SoundSynth.Click(s)), BaseDb = -14f, Falloff = 20f, Audible = 25f };
        _defs[Snd.MagOut] = new Def { Layers = Layered(1, 2, (s, _) => SoundSynth.MagOut(s)), BaseDb = -12f, Falloff = 20f, Audible = 25f };
        _defs[Snd.MagIn] = new Def { Layers = Layered(1, 2, (s, _) => SoundSynth.MagIn(s)), BaseDb = -12f, Falloff = 20f, Audible = 25f };
        _defs[Snd.Bolt] = new Def { Layers = Layered(1, 2, (s, _) => SoundSynth.Bolt(s)), BaseDb = -10f, Falloff = 20f, Audible = 30f };
        _defs[Snd.Launcher] = new Def { Layers = Layered(1, 3, (s, _) => SoundSynth.Launcher(s)), BaseDb = -4f, Falloff = 16f, Audible = 600f, PitchVar = 0.05f };
        _defs[Snd.Rocket] = new Def { Layers = Layered(1, 3, (s, _) => SoundSynth.RocketLaunch(s)), BaseDb = 4f, Falloff = 14f, Audible = 2500f, PitchVar = 0.05f };
        _defs[Snd.ArmorHit] = new Def { Layers = Layered(1, 4, (s, _) => SoundSynth.ArmorHit(s)), BaseDb = -8f, Falloff = 18f, Audible = 400f, PitchVar = 0.08f };
        _defs[Snd.MortarFire] = new Def { Layers = Layered(1, 3, (s, _) => SoundSynth.MortarFire(s)), BaseDb = 6f, Falloff = 13f, Audible = 3500f, PitchVar = 0.05f };
        _defs[Snd.Whistle] = new Def { Layers = Layered(1, 3, (s, _) => SoundSynth.ShellWhistle(s)), BaseDb = -2f, Falloff = 14f, Audible = 800f, PitchVar = 0.06f };
        _defs[Snd.Beep] = new Def { Layers = Layered(1, 1, (s, _) => SoundSynth.Beep(s)), BaseDb = -12f, Falloff = 20f, Audible = 10f, PitchVar = 0f };
        _defs[Snd.Build] = new Def { Layers = Layered(1, 4, (s, _) => SoundSynth.Shovel(s)), BaseDb = -14f, Falloff = 20f, Audible = 60f, PitchVar = 0.1f };
        _defs[Snd.Bag] = new Def { Layers = Layered(1, 3, (s, _) => SoundSynth.Rustle(s)), BaseDb = -16f, Falloff = 20f, Audible = 30f, PitchVar = 0.1f };
        _defs[Snd.DoorOpen] = new Def { Layers = Layered(1, 3, (s, _) => SoundSynth.DoorSound(s, true)), BaseDb = -12f, Falloff = 20f, Audible = 45f, PitchVar = 0.08f };
        _defs[Snd.DoorClose] = new Def { Layers = Layered(1, 3, (s, _) => SoundSynth.DoorSound(s, false)), BaseDb = -9f, Falloff = 20f, Audible = 60f, PitchVar = 0.06f };
        _defs[Snd.Bounce] = new Def { Layers = Layered(1, 3, (s, _) => SoundSynth.Bounce(s)), BaseDb = -12f, Falloff = 20f, Audible = 60f, PitchVar = 0.1f };
    }

    public void Emit(Snd kind, Vector3 pos, float gainDb = 0f, ICombatant? source = null)
    {
        Emitted?.Invoke(kind, pos, gainDb);
        if (source != null) _history.Add(new Heard { Kind = kind, Pos = pos, T0 = Clock.Now, GainDb = gainDb, Source = source });
        float d = pos.DistanceTo(ListenerPos);
        if (d > _defs[kind].Audible) return;
        // Within a few metres the delay is under a frame; play now rather than a frame late.
        if (d < 4f) { Play(kind, pos, d, gainDb); return; }
        _pending.Add(new Pending { Kind = kind, Pos = pos, T0 = Clock.Now, GainDb = gainDb });
    }

    public override void _Process(double delta)
    {
        UpdateHearing((float)delta);
        double now = Clock.Now;
        int stale = 0;
        while (stale < _history.Count && now - _history[stale].T0 > 5.0) stale++;
        if (stale > 0) _history.RemoveRange(0, stale);
        var cam = GetViewport().GetCamera3D();
        if (cam != null) { ListenerPos = cam.GlobalPosition; _listenerFwd = -cam.GlobalBasis.Z; }
        UpdateSpace((float)delta);
        for (int i = _echoes.Count - 1; i >= 0; i--)
        {
            if (now < _echoes[i].At) continue;
            var e = _echoes[i];
            _echoes.RemoveAt(i);
            Voice(e.Stream, e.Pos, e.Db, e.Pitch);
        }
        _echoBudget = MathF.Min(40f, _echoBudget + (float)delta * 30f);

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            float radius = (float)(now - p.T0) * SpeedOfSound;
            float d = p.Pos.DistanceTo(ListenerPos);
            if (radius < d && now - p.T0 < 30.0) continue;
            if (radius >= d) Play(p.Kind, p.Pos, d, p.GainDb);
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
    ///   the longer way round. With no way through, only what the walls pass: a low thump.
    /// - The reverberant field of the listener's space: a copy to the Tail bus, whose reverb
    ///   follows where the listener is (UpdateSpace).
    /// - Outdoors and close enough, discrete echoes off nearby walls and cliffs, each from its
    ///   own direction, at its own delay (Echoes).
    /// </summary>
    void Play(Snd kind, Vector3 pos, float d, float gainDb)
    {
        var def = _defs[kind];
        bool big = def.Reps.Length > 0 || def.Audible >= 200f;
        var space = GetWorld3D().DirectSpaceState;
        var lb = Rooms.At(ListenerPos);
        var sb = Rooms.At(pos);
        var at = pos;
        float path = d, heardAs = d, extraDb = 0f;
        bool walls = false, viaOpening = false;
        if (lb != sb)
        {
            if (Rooms.Find(space, sb, pos, lb, ListenerPos) is Rooms.Route r)
            {
                at = r.At;
                path = MathF.Max(r.Length, d);
                viaOpening = true;
                // Each opening loses a little (diffraction round its edges); a shut door a lot, and the highs.
                extraDb = -2.5f * r.Portals - (r.ThroughClosedDoor ? 14f : 0f);
                heardAs = r.ThroughClosedDoor ? path * 4f + 80f : path;
            }
            else
            {
                // Through the walls: the mass of the wall passes lows, stops highs.
                walls = true;
                heardAs = d * 4f + 150f;
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

        int layer = LayerFor(def, heardAs, path);
        float db = def.BaseDb + def.LayerDb[layer] - def.Falloff * MathF.Log10(MathF.Max(path, 1f)) + gainDb + extraDb;
        // Wind and turbulence make distant shots swell and fade from one to the next.
        if (d > 150f) db += (_rng.Randf() + _rng.Randf() - 1f) * 3f * MathF.Min(1f, MathF.Log10(d / 150f) * 1.5f);
        if (db < -60f) return;

        var variants = def.Layers[layer];
        var stream = variants[_rng.RandiRange(0, variants.Length - 1)];
        float pitch = 1f + _rng.RandfRange(-def.PitchVar, def.PitchVar);
        // A sound arriving by a longer path than the straight line: late by the difference.
        float late = (path - d) / SpeedOfSound;
        if (late > 0.01f) _echoes.Add(new Echo { At = Clock.Now + late, Stream = stream, Pos = at, Db = db, Pitch = pitch });
        else Voice(stream, at, db, pitch);

        if (!big) return;
        // The reverberant field of the listener's space. Indoors (same room) it's strong and close;
        // heard through an opening or the walls, what reverberates is the room around you;
        // outdoors it grows relative to the direct sound with distance.
        Tail(stream, at, db + TailRel(d, lb, sb, viaOpening, walls), pitch, late);

        if (lb == null && sb == null && def.Reps.Length > 0 && d < 420f) Echoes(def, pos, d, db, pitch, heardAs);
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

    // ---------------------------------------------------------------- continuous sources

    /// <summary>What a continuous source (an engine, a rotor) is doing acoustically; kept per source, eased.</summary>
    public sealed class LoopShape
    {
        public AudioStreamPlayer3D? Tail;
        public float RefDist = 10f, Falloff = 20f;
        internal float Extra, Cut = 20000f, TExtra, TCut = 20000f;
        internal double NextAt;
    }

    static readonly float[] _cutD = new float[64];
    static bool _cutReady;

    /// <summary>
    /// The frequency the air has taken 6 dB off by distance d (ISO 9613-1, the same model the
    /// gunshot bands are made with): about 16 kHz at 50 m, 6 kHz at 400 m, 3 kHz at 1.5 km.
    /// </summary>
    static float AirCutoff(float d)
    {
        if (!_cutReady)
        {
            for (int i = 0; i < _cutD.Length; i++)
            {
                float dist = 10f * MathF.Pow(1.15f, i);
                float lo = 100f, hi = 20000f;
                for (int it = 0; it < 24; it++)
                {
                    float mid = MathF.Sqrt(lo * hi);
                    if (Acoustics.AbsorptionDbPerM(mid) * dist > 6.0) hi = mid; else lo = mid;
                }
                _cutD[i] = lo;
            }
            _cutReady = true;
        }
        float f = MathF.Log(MathF.Max(d, 10f) / 10f) / MathF.Log(1.15f);
        int k = Math.Clamp((int)f, 0, _cutD.Length - 2);
        float u = Mathf.Clamp(f - k, 0f, 1f);
        return _cutD[k] + (_cutD[k + 1] - _cutD[k]) * u;
    }

    /// <summary>
    /// Put a continuous sound (an engine, a rotor) through the same acoustics as everything
    /// else, every frame: <paramref name="p"/>.VolumeDb on the way in is how loud it is at
    /// RefDist; on the way out it's what reaches the listener. Spreading loss past RefDist; the
    /// air's treble loss for the distance; a hill or building in the way (duller and quieter);
    /// a listener indoors hears it through the openings, or through the walls; and a feed to
    /// the reverb of the listener's space.
    /// </summary>
    public void ShapeLoop(AudioStreamPlayer3D p, Vector3 pos, LoopShape s)
    {
        float srcDb = p.VolumeDb;
        float d = MathF.Max(pos.DistanceTo(ListenerPos), 0.5f);
        RoomBox? lb = _room;
        bool via = false, walls = false;
        if (Clock.Now > s.NextAt)
        {
            s.NextAt = Clock.Now + 0.2 + _rng.Randf() * 0.1;
            float cut = AirCutoff(d), extra = 0f;
            var space = GetWorld3D().DirectSpaceState;
            if (lb != null)
            {
                if (Rooms.Find(space, null, pos, lb, ListenerPos) is Rooms.Route r)
                {
                    via = true;
                    extra = r.ThroughClosedDoor ? -14f : -2.5f;
                    if (r.ThroughClosedDoor) cut = MathF.Min(cut, 700f);
                }
                else { walls = true; extra = -18f; cut = MathF.Min(cut, 320f); }
            }
            else if (d > 6f && space.IntersectRay(PhysicsRayQueryParameters3D.Create(ListenerPos, pos + Vector3.Up * 1f, Layers.World)).Count > 0)
            {
                extra = -7f;
                cut = MathF.Min(cut, 900f);
            }
            s.TExtra = extra;
            s.TCut = cut;
        }
        float k = 1f - MathF.Exp(-(float)GetProcessDeltaTime() / 0.25f);
        s.Extra += (s.TExtra - s.Extra) * k;
        s.Cut = MathF.Exp(MathF.Log(s.Cut) + (MathF.Log(s.TCut) - MathF.Log(s.Cut)) * k);
        float db = srcDb - s.Falloff * MathF.Log10(MathF.Max(d, s.RefDist) / s.RefDist) + s.Extra;

        // Godot's inverse-distance model with a 1 m reference only drives its filter (strength
        // follows its attenuation, so full strength past a few metres); its level is undone here.
        p.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance;
        p.UnitSize = 1f;
        p.MaxDistance = 0f;
        p.MaxDb = 6f;
        p.AttenuationFilterDb = -40f;
        p.AttenuationFilterCutoffHz = s.Cut;
        p.VolumeDb = MathF.Min(80f, db + 20f * MathF.Log10(MathF.Max(d, 1f)));

        if (s.Tail is { } t)
        {
            bool on = p.Playing && db > -70f;
            if (on && !t.Playing) t.Play(_rng.Randf() * 2f);
            else if (!on && t.Playing) t.Stop();
            t.PitchScale = p.PitchScale;
            // Engines are steady: a big reverb share of them is a constant wash under everything.
            t.VolumeDb = db + TailRel(d, lb, null, via, walls) - 9f;
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
        else while (layer < def.Bounds.Length && d > def.Bounds[layer]) layer++;
        return layer;
    }

    Vector3 _listenerFwd = Vector3.Forward;

    /// <summary>Play on a free voice (or the quietest one, if this is louder); from behind the listener, via the Behind bus.</summary>
    void Voice(AudioStreamWav stream, Vector3 at, float db, float pitch)
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
        v.Bus = to.LengthSquared() > 1f && to.Normalized().Dot(_listenerFwd with { Y = 0f }) < -0.35f ? "Behind" : "World";
        v.Stream = stream;
        v.GlobalPosition = at;
        v.VolumeDb = db;
        v.PitchScale = pitch;
        v.Play();
        _voiceDb[slot] = db;
    }

    void Tail(AudioStreamWav stream, Vector3 at, float db, float pitch, float late)
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
    }

    readonly List<Echo> _echoes = new();
    float _echoBudget = 40f;

    /// <summary>
    /// First reflections outdoors, found for real: rays from the source feel out the walls and
    /// cliffs round it; each surface that the listener can see gives an echo from that
    /// surface's direction, delayed by the extra path, quieter by the extra spreading and by
    /// what the surface absorbs (a wall reflects most of it, broken ground much less). In a
    /// street that's the slap and flutter off the buildings; in a valley, a cliff answering back.
    /// </summary>
    void Echoes(Def def, Vector3 s, float d, float directDb, float pitch, float heardAs)
    {
        if (_echoBudget < 1f) return;
        _echoBudget -= 1f;
        var space = GetWorld3D().DirectSpaceState;
        var src = s + Vector3.Up * 1.5f;
        var found = new List<(Vector3 At, float Len, float Db)>();
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
            float db = directDb - def.Falloff * MathF.Log10(len / MathF.Max(d, 1f)) - loss - (heardAs > d * 1.5f ? -5f : 0f);
            found.Add((h, len, db));
        }
        foreach (var e in found.OrderByDescending(x => x.Db).Take(3))
        {
            if (e.Db < directDb - 22f || e.Db < -60f) continue;
            int layer = LayerFor(def, e.Len, e.Len);
            var variants = def.Layers[layer];
            _echoes.Add(new Echo
            {
                At = Clock.Now + (e.Len - d) / SpeedOfSound, Stream = variants[_rng.RandiRange(0, variants.Length - 1)],
                Pos = e.At, Db = e.Db, Pitch = pitch,
            });
        }
    }

    // ---------------------------------------------------------------- the listener's space

    EnvKind _env = EnvKind.Open;
    RoomBox? _room;
    double _spaceAt;
    float _rs = 0.6f, _damp = 0.8f, _pre = 40f, _fb = 0.3f, _hp = 0.15f;

    /// <summary>
    /// The reverb of wherever the listener is, eased in over half a second or so:
    /// - a room: small and dense, bright walls, almost no pre-delay; bigger for a bigger building;
    /// - a street: the buildings either side, a quick bright slap and flutter;
    /// - forest: short and soft, the trunks scatter it and the leaves soak up the highs;
    /// - open ground: sparse and distant, the far hills answering after a while.
    /// </summary>
    void UpdateSpace(float dt)
    {
        if (Clock.Now > _spaceAt)
        {
            _spaceAt = Clock.Now + 0.4;
            _room = Rooms.At(ListenerPos);
            _env = _room != null ? EnvKind.Interior : Surroundings.At(null, ListenerPos);
        }
        float rs, damp, pre, fb, hp;
        switch (_env)
        {
            // Outdoors the tail is dark (damping high: the highs die, the lows roll on) and keeps
            // its bass (no high-pass): that's the rumble.
            // Outdoors there are no walls to hold the sound in: a sparse, late, dark return, not a hall.
            case EnvKind.Interior:
                rs = Mathf.Clamp(_room!.Volume / 1800f, 0.15f, 0.45f); damp = 0.4f; pre = 5f; fb = 0.15f; hp = 0.03f; break;
            case EnvKind.Urban: rs = 0.45f; damp = 0.65f; pre = 25f; fb = 0.4f; hp = 0.04f; break;
            case EnvKind.Forest: rs = 0.35f; damp = 0.9f; pre = 12f; fb = 0.15f; hp = 0.02f; break;
            default: rs = 0.6f; damp = 0.95f; pre = 80f; fb = 0.25f; hp = 0f; break;
        }
        float k = 1f - MathF.Exp(-dt / 0.5f);
        _rs += (rs - _rs) * k; _damp += (damp - _damp) * k; _pre += (pre - _pre) * k; _fb += (fb - _fb) * k; _hp += (hp - _hp) * k;
        int bus = AudioServer.GetBusIndex("Tail");
        if (bus >= 0 && AudioServer.GetBusEffect(bus, 0) is AudioEffectReverb rv)
        {
            rv.RoomSize = _rs; rv.Damping = _damp; rv.PredelayMsec = _pre; rv.PredelayFeedback = _fb; rv.Hipass = _hp;
        }
    }

    /// <summary>For the HUD/debug: what space the listener is in.</summary>
    public string SpaceName => _env == EnvKind.Interior ? $"room ({_room?.Volume:0} m³)" : Surroundings.Name(_env);

    bool Occluded(Vector3 pos)
    {
        var space = GetWorld3D().DirectSpaceState;
        var q = PhysicsRayQueryParameters3D.Create(ListenerPos, pos + Vector3.Up * 0.6f, Layers.World);
        return space.IntersectRay(q).Count > 0;
    }

    /// <summary>How loud a sound is at distance d, in the same dB scale playback uses.</summary>
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
