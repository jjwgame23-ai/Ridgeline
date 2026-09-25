using Godot;

namespace Ridgeline;

public enum Snd { Rifle556, Rifle762, Explosion, Crack, Steel, Impact, Footstep, Click, MagOut, MagIn, Bolt, Bounce, Launcher, Build, Bag, Hmg, Autocannon, Cannon, Rocket, ArmorHit, MortarFire, Whistle, Beep, Shell }

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
    const int VoiceCount = 72;

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
    AudioStreamPlayer3D[] _voices = null!;
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
        var s556 = SoundSynth.Distanced(s => SoundSynth.GunshotDry(s, 1f), 4, 1000, Bands, 0.22f, 0.9f, 9, 2.4f, 2.8f, 0.7f);
        var s762 = SoundSynth.Distanced(s => SoundSynth.GunshotDry(s, 1.3f), 4, 1500, Bands, 0.22f, 0.9f, 9, 2.4f, 2.8f, 0.7f);
        var boom = SoundSynth.Distanced(SoundSynth.ExplosionDry, 3, 2000, Bands, 0.3f, 1.2f, 12, 3.2f, 4.2f, 0.7f);
        _defs[Snd.Rifle556] = new Def { Layers = s556.Layers, Reps = Bands, LayerDb = s556.LayerDb, BaseDb = 0f, Falloff = 12f, Audible = 5000f, PitchVar = 0.04f };
        _defs[Snd.Rifle762] = new Def { Layers = s762.Layers, Reps = Bands, LayerDb = s762.LayerDb, BaseDb = 1.5f, Falloff = 12f, Audible = 5500f, PitchVar = 0.04f };
        _defs[Snd.Explosion] = new Def { Layers = boom.Layers, Reps = Bands, LayerDb = boom.LayerDb, BaseDb = 6f, Falloff = 13f, Audible = 8000f, PitchVar = 0.06f };
        // A shell or mortar bomb: the same physics with four times the charge: a longer, deeper
        // blast wave (its duration grows with the cube root of the charge), heard further.
        var shell = SoundSynth.Distanced(s => SoundSynth.Stretch(SoundSynth.ExplosionDry(s), 1.45f), 3, 2100, Bands, 0.32f, 1.3f, 12, 3.4f, 4.6f, 0.7f);
        _defs[Snd.Shell] = new Def { Layers = shell.Layers, Reps = Bands, LayerDb = shell.LayerDb, BaseDb = 8f, Falloff = 13f, Audible = 9000f, PitchVar = 0.05f };
        var hmg = SoundSynth.Distanced(s => SoundSynth.GunshotDry(s, 1.9f), 3, 2500, Bands, 0.24f, 1.0f, 9, 2.6f, 3.0f, 0.7f);
        var ac = SoundSynth.Distanced(SoundSynth.AutocannonDry, 3, 3000, Bands, 0.26f, 1.1f, 10, 2.8f, 3.4f, 0.7f);
        var cannon = SoundSynth.Distanced(SoundSynth.CannonDry, 3, 3500, Bands, 0.3f, 1.3f, 12, 3.4f, 4.4f, 0.7f);
        _defs[Snd.Hmg] = new Def { Layers = hmg.Layers, Reps = Bands, LayerDb = hmg.LayerDb, BaseDb = 3f, Falloff = 12f, Audible = 6500f, PitchVar = 0.03f };
        _defs[Snd.Autocannon] = new Def { Layers = ac.Layers, Reps = Bands, LayerDb = ac.LayerDb, BaseDb = 5f, Falloff = 12f, Audible = 7000f, PitchVar = 0.03f };
        _defs[Snd.Cannon] = new Def { Layers = cannon.Layers, Reps = Bands, LayerDb = cannon.LayerDb, BaseDb = 9f, Falloff = 13f, Audible = 9000f, PitchVar = 0.04f };
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
        if (cam != null) ListenerPos = cam.GlobalPosition;

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

    void Play(Snd kind, Vector3 pos, float d, float gainDb)
    {
        var def = _defs[kind];
        float heardAs = d, extraDb = 0f;
        // Behind a hill or a building: the direct sound is blocked and what arrives has
        // gone over or around it — darker and quieter, as if from much further away.
        if (d > 8f && def.Reps.Length > 0 && Occluded(pos))
        {
            heardAs = d * 3f + 60f;
            extraDb = -5f;
        }
        else if (d > 8f && def.Audible < 200f && Occluded(pos)) extraDb = -8f;

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

        float db = def.BaseDb + def.LayerDb[layer] - def.Falloff * MathF.Log10(MathF.Max(d, 1f)) + gainDb + extraDb;
        // Wind and turbulence make distant shots swell and fade from one to the next.
        if (d > 150f) db += (_rng.Randf() + _rng.Randf() - 1f) * 3f * MathF.Min(1f, MathF.Log10(d / 150f) * 1.5f);
        if (db < -60f) return;

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

        var variants = def.Layers[layer];
        var v = _voices[slot];
        v.Stop();
        v.Stream = variants[_rng.RandiRange(0, variants.Length - 1)];
        v.GlobalPosition = pos;
        v.VolumeDb = db;
        v.PitchScale = 1f + _rng.RandfRange(-def.PitchVar, def.PitchVar);
        v.Play();
        _voiceDb[slot] = db;
    }

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
