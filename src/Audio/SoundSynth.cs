using Godot;

namespace Ridgeline;

/// <summary>
/// Builds every sound in the game from noise, pressure pulses and resonances at startup,
/// so the prototype needs no audio files. Loud sounds are made "dry" (as heard a metre or
/// two away) and propagated offline to a ladder of distances (Acoustics): far away the air
/// has eaten the highs and the ground has hollowed out the low-mids, which is most of what
/// makes a distant rifle sound distant. Real recordings can replace any of these later
/// without touching SoundWorld.
/// </summary>
public static class SoundSynth
{
    public const int Rate = 44100;

    sealed class Buf
    {
        public readonly float[] S;
        public readonly Random R;
        public Buf(float seconds, int seed) { S = new float[(int)(seconds * Rate)]; R = new Random(seed); }
        public float Rnd() => (float)R.NextDouble();
    }

    /// <summary>
    /// A dry source: what it radiates (propagated to every distance), and optionally what's
    /// only felt up close — the body thump of a shot, a near-field effect that fades out past a
    /// few metres instead of carrying like sound (see Distanced).
    /// </summary>
    public readonly record struct Dry(float[] Radiated, float[]? Near = null);

    static float LpCoef(float hz) => 1f - MathF.Exp(-2f * MathF.PI * MathF.Min(hz, Rate * 0.45f) / Rate);

    /// <summary>Adds low-passed white noise with a linear attack and exponential decay.</summary>
    static void Noise(Buf b, float gain, float tau, float lpHz, float start = 0f, float attack = 0.0005f)
    {
        float a = LpCoef(lpHz);
        // A one-pole low-pass scales white noise variance by a/(2-a); undo that so
        // gain means roughly the same loudness whatever the cutoff.
        float norm = MathF.Sqrt((2f - a) / a);
        float y = 0f;
        int i0 = (int)(start * Rate);
        var s = b.S;
        for (int i = i0; i < s.Length; i++)
        {
            float t = (i - i0) / (float)Rate;
            float env = t < attack ? t / attack : MathF.Exp(-(t - attack) / tau);
            if (t > attack && env < 1e-4f) break;
            y += a * (b.Rnd() * 2f - 1f - y);
            s[i] += y * norm * env * gain;
        }
    }

    /// <summary>Adds a sine whose pitch glides from f0 to f1 — the body "thump" of a shot or blast. Phase in cycles.</summary>
    static void Tone(Buf b, float f0, float f1, float sweepTau, float gain, float tau, float start = 0f, float attack = 0.001f, float phase = 0f)
    {
        int i0 = (int)(start * Rate);
        double ph = phase * 2.0 * Math.PI;
        var s = b.S;
        for (int i = i0; i < s.Length; i++)
        {
            float t = (i - i0) / (float)Rate;
            float env = t < attack ? t / attack : MathF.Exp(-(t - attack) / tau);
            if (t > attack && env < 1e-4f) break;
            float f = f1 + (f0 - f1) * MathF.Exp(-t / sweepTau);
            ph += 2.0 * Math.PI * f / Rate;
            s[i] += (float)Math.Sin(ph) * env * gain;
        }
    }

    static AudioStreamWav Finish(Buf b) => Finish(b.S);

    static AudioStreamWav Finish(float[] s, int rate = Rate)
    {
        float peak = 1e-6f;
        foreach (var v in s) peak = MathF.Max(peak, MathF.Abs(v));
        float g = 0.9f / peak;
        int fade = Math.Min(s.Length, rate / 50);
        var bytes = new byte[s.Length * 2];
        for (int i = 0; i < s.Length; i++)
        {
            float v = s[i] * g;
            int left = s.Length - i;
            if (left < fade) v *= left / (float)fade;
            short q = (short)Math.Clamp((int)(v * 32767f), -32768, 32767);
            bytes[2 * i] = (byte)(q & 0xff);
            bytes[2 * i + 1] = (byte)((q >> 8) & 0xff);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = rate,
            Stereo = false,
            Data = bytes,
        };
    }

    /// <summary>
    /// A blast wave: the pressure pulse of a muzzle blast or explosion. An instant rise and a
    /// positive phase of length T falling through zero (Friedlander), then the negative phase —
    /// the air rushing back — shallower and negLen times longer, with the same area as the
    /// positive one. A radiated wave carries no net impulse (what goes out comes back): without
    /// that suck-back a blast has a DC offset and a sub-bass hump no ear or speaker hears, which
    /// also crowds out headroom; with it the energy sits where the "crack" and "thud" really are.
    /// </summary>
    static void Blast(Buf b, float amp, float T, float decay, float start = 0f, float negLen = 3f)
    {
        int i0 = (int)(start * Rate), np = Math.Max(1, (int)MathF.Round(T * Rate));
        double area = 0;
        for (int i = 0; i < np && i0 + i < b.S.Length; i++)
        {
            float u = i / (float)np;
            float v = amp * (1f - u) * MathF.Exp(-decay * u);
            b.S[i0 + i] += v;
            area += v;
        }
        // The negative lobe u(1-u)²: falls quickly, recovers slowly, peak ~0.2 of the positive.
        int nn = Math.Max(3, (int)MathF.Round(T * negLen * Rate));
        double shape = 0;
        for (int i = 0; i < nn; i++) { float u = (i + 0.5f) / nn; shape += u * (1f - u) * (1f - u); }
        float scale = (float)(area / shape);
        for (int i = 0; i < nn && i0 + np + i < b.S.Length; i++)
        {
            float u = (i + 0.5f) / nn;
            b.S[i0 + np + i] -= scale * u * (1f - u) * (1f - u);
        }
    }

    /// <summary>One resonant mode: a decaying sine at a fixed frequency, from a random phase (cycles).</summary>
    static void Mode(Buf b, float f, float gain, float tau, float phase, float start = 0f) =>
        Tone(b, f, f, 1f, gain, tau, start, attack: 0.0002f, phase: phase);

    /// <summary>
    /// The body thump of a shot, felt as much as heard. Not a pure sine: a gliding low partial,
    /// two weaker inharmonic ones (the gas and air round the weapon don't ring at one pitch)
    /// and a puff of low noise under them, each with its own phase and decay.
    /// </summary>
    static void Thump(Buf b, float f0, float f1, float sweepTau, float gain, float tau)
    {
        Tone(b, f0, f1, sweepTau, gain, tau, phase: b.Rnd());
        Tone(b, f0 * 1.58f, f1 * 1.63f, sweepTau, gain * 0.28f, tau * 0.6f, phase: b.Rnd());
        Tone(b, f0 * 2.37f, f1 * 2.21f, sweepTau, gain * 0.12f, tau * 0.45f, phase: b.Rnd());
        Noise(b, gain * 0.3f, tau * 0.8f, f1 * 3f, attack: 0.002f);
    }

    /// <summary>
    /// A rifle shot as heard a metre or two from the muzzle, before the air and the
    /// terrain get to it: the blast wave, the roar of the propellant gas leaving the
    /// barrel, and the bolt cycling. Acoustics does the distance. The low body thump is the
    /// near part: felt by the shooter and anyone beside them, gone a few tens of metres off,
    /// where a rifle is a "pop", not a boom.
    /// </summary>
    /// <param name="weight">1 = 5.56 carbine; ~1.3 = 7.62 rifle (longer blast, more low end).</param>
    public static Dry GunshotDry(int seed, float weight)
    {
        var b = new Buf(0.45f, seed);
        var near = new Buf(0.45f, seed + 7);
        float k = 0.92f + b.Rnd() * 0.16f;
        Blast(b, 1.0f, 0.00045f * weight * k, 1.3f);
        // The pressure punch: the slower part of the blast wave, mostly low-mids, which the air
        // barely absorbs, so it's what still hits at a distance under the crack.
        Blast(b, 0.45f, 0.0022f * weight * k, 1.1f);
        Noise(b, 0.55f, 0.0025f, 14000f);                                     // gas turbulence
        Noise(b, 0.45f, 0.012f * weight, 3000f / weight, attack: 0.0005f);    // the roar: short and bright, it's what carries as the "pop"
        Thump(near, 150f / weight * k, 55f / weight, 0.012f, 0.36f, 0.026f * weight); // body thump: a punch, kept short
        // The action: bolt carrier slamming back and forward, only audible up close.
        float cyc = 0.045f + b.Rnd() * 0.01f;
        Noise(b, 0.05f, 0.003f, 6000f, start: cyc);
        Tone(b, 1900f * k, 1700f * k, 0.01f, 0.025f, 0.01f, start: cyc, phase: b.Rnd());
        Noise(b, 0.035f, 0.003f, 5000f, start: cyc + 0.03f);
        return new Dry(b.S, near.S);
    }

    /// <summary>30 mm: a heavier, slower-cracking blast with a hard mechanical clank from the feed.</summary>
    public static Dry AutocannonDry(int seed)
    {
        var b = new Buf(0.6f, seed);
        float k = 0.93f + b.Rnd() * 0.14f;
        Blast(b, 1.0f, 0.0012f * k, 1.4f);
        Noise(b, 0.6f, 0.004f, 12000f);
        Noise(b, 0.55f, 0.04f, 1600f, attack: 0.001f);
        Thump(b, 95f * k, 38f, 0.02f, 0.7f, 0.07f);
        Noise(b, 0.08f, 0.004f, 5000f, start: 0.05f);
        Tone(b, 900f, 800f, 0.01f, 0.04f, 0.02f, start: 0.05f, phase: b.Rnd());
        return new Dry(b.S);
    }

    /// <summary>A tank gun: a long, heavy blast wave that you feel more than hear up close.</summary>
    public static Dry CannonDry(int seed)
    {
        var b = new Buf(1.6f, seed);
        Blast(b, 1.0f, 0.0045f * (0.95f + b.Rnd() * 0.1f), 1.5f, negLen: 3.5f);
        Noise(b, 0.7f, 0.008f, 12000f);
        Noise(b, 0.8f, 0.09f, 1100f, attack: 0.002f);
        Thump(b, 60f, 24f, 0.06f, 0.9f, 0.3f);
        Noise(b, 0.35f, 0.5f, 300f, start: 0.02f, attack: 0.05f);
        Noise(b, 0.05f, 0.01f, 4000f, start: 0.6f + b.Rnd() * 0.2f); // the breech and the spent case
        Tone(b, 520f, 480f, 0.02f, 0.03f, 0.05f, start: 0.62f, phase: b.Rnd());
        return new Dry(b.S);
    }

    /// <summary>
    /// What a loop sounds like through a hull: the body and the air in the cabin pass the
    /// low rumble and the firing harmonics, and swallow the hiss. A steep low-pass (four
    /// one-pole stages) over the loop, run round twice so the filter state at the end
    /// matches the start and it still loops without a click. The hull itself rings a
    /// little too: a soft resonance near its panel frequency.
    /// </summary>
    public static AudioStreamWav Muffle(AudioStreamWav src, float cutoffHz, float resonanceHz)
    {
        var data = src.Data;
        int n = data.Length / 2;
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (short)(data[2 * i] | (data[2 * i + 1] << 8)) / 32768f;
        float a = 1f - MathF.Exp(-MathF.Tau * cutoffHz / Rate);
        // Resonator (two-pole band-pass) for the hull's boom.
        float w = MathF.Tau * resonanceHz / Rate, rr = 0.995f, c1 = 2f * rr * MathF.Cos(w), c2 = -rr * rr;
        float s1 = 0f, s2 = 0f, s3 = 0f, s4 = 0f, b1 = 0f, b2 = 0f;
        var y = new float[n];
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                s1 += a * (x[i] - s1); s2 += a * (s1 - s2); s3 += a * (s2 - s3); s4 += a * (s3 - s4);
                float bp = x[i] * (1f - rr) + c1 * b1 + c2 * b2;
                b2 = b1; b1 = bp;
                y[i] = s4 + bp * 0.6f;
            }
        float peak = y.Max(MathF.Abs) + 1e-6f;
        var bytes = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short q = (short)Math.Clamp((int)(y[i] / peak * 0.8f * 32767f), -32768, 32767);
            bytes[2 * i] = (byte)(q & 0xff);
            bytes[2 * i + 1] = (byte)((q >> 8) & 0xff);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = bytes,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = n,
        };
    }

    const int LoopFade = Rate / 5;

    /// <summary>
    /// A seamless loop from a buffer generated LoopFade samples longer than the loop: the
    /// overrun is crossfaded (equal power, for noise) into the start, so the sample after the
    /// last one is exactly what followed it when it was made. No click, no jump at the seam.
    /// </summary>
    static AudioStreamWav LoopWav(float[] s)
    {
        int n = s.Length - LoopFade;
        var o = new float[n];
        Array.Copy(s, o, n);
        for (int i = 0; i < LoopFade; i++)
        {
            float t = (i + 0.5f) / LoopFade;
            o[i] = s[i] * MathF.Sqrt(t) + s[n + i] * MathF.Sqrt(1f - t);
        }
        float peak = o.Max(MathF.Abs) + 1e-6f;
        var bytes = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short q = (short)Math.Clamp((int)(o[i] / peak * 0.8f * 32767f), -32768, 32767);
            bytes[2 * i] = (byte)(q & 0xff);
            bytes[2 * i + 1] = (byte)((q >> 8) & 0xff);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = bytes,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = n,
        };
    }

    /// <summary>
    /// An engine idling as a seamless loop: firing pulses at a fixed rate (pitch-shifted
    /// live for revs), exhaust rumble, and for tracked vehicles the clatter of track.
    /// </summary>
    public static AudioStreamWav EngineLoop(bool tracked, bool heavy)
    {
        // A diesel idling: firing pulses overlap (many cylinders), so it's a buzzy rumble,
        // not separate thumps. The fundamental sits high enough that even pitched down for
        // idle it stays a drone, never a helicopter's chop. Everything repeats a whole
        // number of times in the loop, so it loops without a seam.
        const float seconds = 3f;
        int n = (int)(seconds * Rate) + LoopFade;
        var s = new float[n];
        var r = new Random(heavy ? 31 : 17);
        float f0 = heavy ? 62f : 88f;
        float y = 0f, lo = 0f, clank = 0f;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            float ph = (float)((t * f0) % 1.0);
            // Soft, wide pulses: each cylinder's bang smeared through the exhaust.
            float pulse = 0.55f + 0.45f * MathF.Cos(ph * MathF.Tau);
            float noise = (float)r.NextDouble() * 2f - 1f;
            y += 0.08f * (noise * pulse - y);          // exhaust roar, modulated by the firing rhythm
            lo += 0.01f * (noise - lo);                // deep rumble
            float harm = (float)(Math.Sin(2 * Math.PI * f0 * t) * 0.3 + Math.Sin(2 * Math.PI * f0 * 2 * t) * 0.18
                               + Math.Sin(2 * Math.PI * f0 * 3 * t) * 0.1 + Math.Sin(2 * Math.PI * f0 * 0.5 * t) * 0.22);
            float v = harm * 0.5f + y * 2.0f + lo * 6f;
            if (tracked)
            {
                // Track links on the sprockets: a light clatter.
                float lp = (float)((t * 16.0) % 1.0);
                clank += 0.35f * (noise * MathF.Exp(-lp * 30f) - clank);
                v += clank * 0.8f;
            }
            s[i] = v;
        }
        return LoopWav(s);
    }

    /// <summary>
    /// A helicopter: the blade slap (each blade passing, a few a second, pitch-shifted
    /// live), the rotor's broad whoosh, and the whine of the turbines.
    /// </summary>
    public static AudioStreamWav RotorLoop(bool gunship)
    {
        const float seconds = 4f;
        int n = (int)(seconds * Rate) + LoopFade;
        var s = new float[n];
        var r = new Random(gunship ? 53 : 41);
        float slap = gunship ? 18f : 16f;   // blade passes per second
        float whine = gunship ? 4600f : 4100f;
        float y = 0f, w = 0f, b1 = 0f, b2 = 0f, drift = 0f;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            float ph = (float)((t * slap) % 1.0);
            // Blade slap: each blade's tip vortex hitting the next, a sharp impulsive "wop" rich in
            // low-mids. It's the loudest part and what you hear from kilometres off.
            float thump = MathF.Exp(-ph * 14f) + 0.35f * MathF.Exp(-ph * 4f);
            float noise = (float)r.NextDouble() * 2f - 1f;
            y += 0.25f * (noise - y);
            w += 0.02f * (noise - w);
            float wop = MathF.Sin(MathF.Tau * 85f * (ph / slap)); // the low "wop" of each pass
            // Turbine whine: not a pure tone but a narrow band of noise up at ~4 kHz, wandering a
            // little. The air takes ~30 dB/km off that, so it's there up close and gone far off.
            drift += 0.00002f * ((float)r.NextDouble() * 2f - 1f) - drift * 0.00001f;
            float wf = whine * (1f + drift * 50f);
            float c = 2f * MathF.Sin(MathF.PI * wf / Rate);
            b1 += c * b2; b2 += c * (noise - b1 - 0.08f * b2); // state-variable band-pass, narrow
            s[i] = thump * (y * 1.9f + wop * 0.5f)   // the slap
                 + w * 1.2f                        // rotor wash
                 + b2 * 0.05f;                     // turbines
        }
        return LoopWav(s);
    }

    /// <summary>A mortar: a deep, hollow cough out of the tube. Its echoes come from the terrain round it, live.</summary>
    public static Dry MortarDry(int seed)
    {
        var b = new Buf(1.2f, seed);
        Tone(b, 110f, 48f, 0.04f, 1.0f, 0.12f, phase: b.Rnd());
        Blast(b, 0.7f, 0.003f, 1.4f);
        Noise(b, 0.6f, 0.05f, 1400f, attack: 0.002f);
        Noise(b, 0.2f, 0.4f, 400f, start: 0.02f, attack: 0.04f);
        return new Dry(b.S);
    }

    /// <summary>A bomb coming down: a falling whistle and the rush of air, ending as it arrives.</summary>
    public static AudioStreamWav ShellWhistle(int seed)
    {
        var b = new Buf(2.0f, seed);
        float k = 0.9f + b.Rnd() * 0.2f;
        double ph = 0;
        float y = 0f;
        for (int i = 0; i < b.S.Length; i++)
        {
            float t = i / (float)Rate;
            float f = (1900f - 1100f * t / 2f) * k;
            ph += 2 * Math.PI * f / Rate;
            float env = MathF.Min(1f, t / 0.6f) * (t > 1.85f ? (2f - t) / 0.15f : 1f);
            y += 0.2f * ((float)b.R.NextDouble() * 2f - 1f - y);
            b.S[i] = ((float)Math.Sin(ph) * 0.4f + y * 0.6f) * env * (0.3f + t * 0.5f);
        }
        return Finish(b);
    }

    /// <summary>
    /// The steady voice of a falling bomb, to be pitch-shifted live: a whistle (with a slight
    /// warble from the fins) over a rush of air. One second, every component a whole number
    /// of cycles, and the noise filtered round twice, so it loops seamlessly.
    /// </summary>
    public static AudioStreamWav WhistleLoop()
    {
        int n = Rate;
        var s = new float[n];
        var r = new Random(77);
        var noise = new float[n];
        for (int i = 0; i < n; i++) noise[i] = (float)r.NextDouble() * 2f - 1f;
        float lo = 0f, hi = 0f;
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                // Band-limited rush: a high-passed low-pass, around 1-3 kHz.
                lo += 0.35f * (noise[i] - lo);
                hi += 0.08f * (lo - hi);
                if (pass == 1) s[i] = (lo - hi) * 0.5f;
            }
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            double warble = 0.004 * Math.Sin(2 * Math.PI * 7 * t);
            double ph = 2 * Math.PI * 1300 * t + 1300 * warble / 7.0;
            s[i] += (float)(Math.Sin(ph) * 0.55 + Math.Sin(2 * ph) * 0.12);
        }
        float peak = s.Max(MathF.Abs) + 1e-6f;
        var bytes = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short q = (short)Math.Clamp((int)(s[i] / peak * 0.8f * 32767f), -32768, 32767);
            bytes[2 * i] = (byte)(q & 0xff);
            bytes[2 * i + 1] = (byte)((q >> 8) & 0xff);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = bytes,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = n,
        };
    }

    /// <summary>A door: the latch, a creak of the hinges (opening), or the bang of it shutting.</summary>
    public static AudioStreamWav DoorSound(int seed, bool open)
    {
        var b = new Buf(open ? 0.7f : 0.45f, seed);
        float k = 0.9f + b.Rnd() * 0.2f;
        Noise(b, 0.25f, 0.004f, 5000f);                             // latch
        Tone(b, 1900f * k, 1800f * k, 0.01f, 0.08f, 0.01f);
        if (open)
        {
            // Hinge creak: a scratchy tone sliding up, stick-slip, as the door swings.
            double ph = 0;
            for (int i = (int)(0.06f * Rate); i < b.S.Length; i++)
            {
                float t = i / (float)Rate - 0.06f;
                float env = MathF.Min(1f, t / 0.05f) * MathF.Exp(-t / 0.25f);
                float f = (380f + 260f * t) * k;
                ph += 2 * Math.PI * f / Rate;
                float stick = MathF.Sin(t * 70f) > 0.3f ? 1f : 0.35f;
                b.S[i] += (float)Math.Sin(ph) * env * stick * 0.12f + (b.Rnd() * 2f - 1f) * env * 0.03f;
            }
        }
        else
        {
            Blast(b, 0.6f, 0.004f, 2f, 0.02f);                      // it meets the frame
            Tone(b, 110f * k, 80f, 0.02f, 0.5f, 0.06f, start: 0.02f);
            Noise(b, 0.3f, 0.04f, 900f, start: 0.02f);
        }
        return Finish(b);
    }

    /// <summary>
    /// A small drone's motors and props: four rotors each spinning a little differently, so
    /// their blade-pass tones beat against each other — the quad's angry-hornet drone. The FPV
    /// is the same at twice the pitch and harsher: a scream.
    /// </summary>
    public static AudioStreamWav DroneLoop(bool fpv)
    {
        const float seconds = 3f;
        int n = (int)(seconds * Rate) + LoopFade;
        var s = new float[n];
        var r = new Random(fpv ? 91 : 73);
        float f0 = fpv ? 380f : 190f;
        var fr = new[] { f0 * 0.985f, f0 * 1.0f, f0 * 1.013f, f0 * 1.027f };
        var ph = new double[4];
        float y = 0f;
        for (int i = 0; i < n; i++)
        {
            float v = 0f;
            for (int k = 0; k < 4; k++)
            {
                ph[k] += fr[k] / Rate;
                float p = (float)(ph[k] % 1.0);
                // A blade pass: a sharpish pulse, rich in harmonics (the buzz).
                v += MathF.Exp(-p * (fpv ? 9f : 6f)) - 0.2f;
            }
            float noise = (float)r.NextDouble() * 2f - 1f;
            y += 0.3f * (noise - y);                  // prop wash
            s[i] = v * 0.25f + y * (fpv ? 0.35f : 0.2f);
        }
        return LoopWav(s);
    }

    /// <summary>A seeker's lock tone.</summary>
    public static AudioStreamWav Beep(int seed)
    {
        var b = new Buf(0.12f, seed);
        Tone(b, 1600f, 1600f, 1f, 0.6f, 0.2f, attack: 0.005f);
        return Finish(b);
    }

    /// <summary>
    /// A round striking armour and not getting in: a dull, heavy clank, not a target plate's ring.
    /// Thick, welded plate: a few low modes, heavily damped, each hit exciting them differently.
    /// </summary>
    public static Dry ArmorHitDry(int seed)
    {
        var b = new Buf(0.45f, seed);
        float k = 0.9f + b.Rnd() * 0.2f;
        Noise(b, 0.9f, 0.006f, 4000f);
        Tone(b, 240f * k, 180f * k, 0.02f, 0.7f, 0.04f, phase: b.Rnd());
        foreach (var f in new[] { 470f, 710f, 1130f, 1620f, 2380f })
            Mode(b, f * k * (0.96f + 0.08f * b.Rnd()), (0.06f + 0.14f * b.Rnd()) * MathF.Sqrt(700f / f), 0.02f + 0.03f * b.Rnd(), b.Rnd());
        Noise(b, 0.2f, 0.05f, 800f, start: 0.004f);
        return new Dry(b.S);
    }

    /// <summary>Slow a sound down by a factor (longer and lower), by resampling.</summary>
    public static float[] Stretch(float[] x, float f)
    {
        var y = new float[(int)(x.Length * f)];
        for (int i = 0; i < y.Length; i++)
        {
            float s = i / f;
            int a = (int)s;
            float t = s - a;
            y[i] = a + 1 < x.Length ? x[a] * (1f - t) + x[a + 1] * t : x[^1];
        }
        return y;
    }

    /// <summary>A hand grenade up close: a long, heavy blast wave, fireball roar, rumble and gravel raining down.</summary>
    public static float[] ExplosionDry(int seed)
    {
        var b = new Buf(2.4f, seed);
        Blast(b, 1.0f, 0.0028f * (0.9f + b.Rnd() * 0.2f), 1.6f);
        Noise(b, 0.6f, 0.006f, 14000f);
        Noise(b, 0.7f, 0.12f, 1800f, attack: 0.002f);
        Tone(b, 70f, 26f, 0.08f, 0.7f, 0.35f);
        Noise(b, 0.3f, 0.7f, 350f, start: 0.03f, attack: 0.08f);
        // Debris and gravel coming back down.
        for (int i = 0; i < 70; i++)
        {
            float st = 0.12f + b.Rnd() * b.Rnd() * 2.0f;
            Noise(b, 0.08f * (1f - st / 2.3f), 0.0025f, 4500f + b.Rnd() * 3000f, start: st);
        }
        return b.S;
    }

    /// <summary>
    /// A sound in distance bands: each band is the dry source propagated to that
    /// band's distance (see Acoustics). LayerDb carries the loudness each band should
    /// play at relative to the first, since every buffer is normalised on its own.
    /// </summary>
    public sealed class Banded
    {
        public AudioStreamWav[][] Layers = null!;
        public float[] LayerDb = null!;
        public string Report = "";
    }

    /// <summary>Within this distance the near part of a source (the felt thump) is at full strength; beyond, it fades as 1/d on top of spreading.</summary>
    const float NearRef = 6f;

    /// <param name="keep">
    /// How much of the loudness a band loses against the nearest one (to the air, to the ground, and a
    /// near part fading out) it keeps: 1 is physical; less keeps distant sounds present in the mix.
    /// </param>
    /// <param name="hs">Height of the source above the ground, for the ground reflection.</param>
    public static Banded Distanced(Func<int, Dry> dry, int variants, int seed, float[] distances, float keep, float hs = 1.5f) =>
        Distanced((s, _) => dry(s), false, variants, seed, distances, keep, hs);

    /// <summary>
    /// As above; with <paramref name="perBand"/> the source itself depends on the distance it's
    /// made for (a bullet's crack: its N-wave lengthens as the shock spreads), so it's made per band.
    /// </summary>
    public static Banded Distanced(Func<int, float, Dry> dry, bool perBand, int variants, int seed, float[] distances, float keep, float hs = 1.5f)
    {
        int bands = distances.Length;
        // A source's spectrum, made once per variant (or per variant and band) by whichever job needs it first.
        var sources = new Lazy<(Dry Dry, int Len, (double[] Re, double[] Im) Spec, (double[] Re, double[] Im) Near)>[variants * (perBand ? bands : 1)];
        for (int i = 0; i < sources.Length; i++)
        {
            int v = perBand ? i / bands : i, k = perBand ? i % bands : 0;
            sources[i] = new(() =>
            {
                var src = dry(seed + v * 31, distances[k]);
                int len = Math.Max(src.Radiated.Length, src.Near?.Length ?? 0) + Rate / 25; // room for the path filter's ringing
                int n = Acoustics.Pow2(Math.Max(len, Acoustics.FilterLength));
                return (src, len, Acoustics.Spectrum(src.Radiated, n), src.Near != null ? Acoustics.Spectrum(src.Near, n) : default);
            });
        }
        // Far off, shot-to-shot turbulence and pitch hide repetition: two variants are plenty.
        int VariantsAt(int k) => distances[k] >= 300f ? Math.Min(variants, 2) : variants;
        var raw = new float[variants][][];
        for (int v = 0; v < variants; v++) raw[v] = new float[bands][];
        System.Threading.Tasks.Parallel.For(0, variants * bands, job =>
        {
            int v = job / bands, k = job % bands;
            if (v >= VariantsAt(k)) return;
            var (src, len, spec, near) = sources[perBand ? job : v].Value;
            var y = Acoustics.Propagate(spec, distances[k], hs, len);
            float g = MathF.Min(1f, NearRef / distances[k]);
            if (src.Near != null && g > 0.01f)
            {
                var z = Acoustics.Propagate(near, distances[k], hs, len);
                for (int i = 0; i < len; i++) y[i] += g * z[i];
            }
            raw[v][k] = y;
        });

        var res = new Banded { Layers = new AudioStreamWav[bands][], LayerDb = new float[bands] };
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < bands; k++)
        {
            int vk = VariantsAt(k);
            res.Layers[k] = new AudioStreamWav[vk];
            // Far off the air has left nothing up top: store at half or a quarter of the rate.
            double top = Acoustics.TopFrequency(distances[k]);
            int decim = top < 0.45 * Rate / 4 ? 4 : top < 0.45 * Rate / 2 ? 2 : 1;
            float db = 0f;
            for (int v = 0; v < vk; v++)
            {
                float l0 = Loudness(raw[v][0]), p0 = Peak(raw[v][0]);
                float l = Loudness(raw[v][k]), p = Peak(raw[v][k]);
                // Played normalised, a buffer's loudness is l/p; we want (l/l0)^keep relative to band 0.
                float target = keep * 20f * MathF.Log10(l / l0);
                float normalised = 20f * MathF.Log10((l / p) / (l0 / p0));
                db += (target - normalised) / vk;
                res.Layers[k][v] = Finish(Decimate(Trim(raw[v][k]), decim), Rate / decim);
            }
            res.LayerDb[k] = db;
            sb.Append($"{distances[k]:0.#}m {db:+0.0;-0.0}dB{(decim > 1 ? $"@{Rate / decim / 1000f:0.#}k" : "")}  ");
        }
        res.Report = sb.ToString();
        return res;
    }

    /// <summary>Every f-th sample, after a windowed-sinc low-pass below the new Nyquist.</summary>
    static float[] Decimate(float[] x, int f)
    {
        if (f <= 1) return x;
        const int Half = 16; // taps each side, in output samples
        var y = new float[x.Length / f];
        int taps = Half * f;
        var h = new float[2 * taps + 1];
        double sum = 0;
        for (int m = -taps; m <= taps; m++)
        {
            double t = m / (double)f;
            double sinc = m == 0 ? 1 : Math.Sin(Math.PI * 0.9 * t) / (Math.PI * 0.9 * t);
            double win = 0.42 + 0.5 * Math.Cos(Math.PI * m / taps) + 0.08 * Math.Cos(2 * Math.PI * m / taps);
            h[m + taps] = (float)(sinc * win);
            sum += sinc * win;
        }
        for (int i = 0; i < h.Length; i++) h[i] /= (float)sum;
        for (int j = 0; j < y.Length; j++)
        {
            double acc = 0;
            int c = j * f;
            for (int m = -taps; m <= taps; m++)
            {
                int i = c + m;
                if (i >= 0 && i < x.Length) acc += h[m + taps] * x[i];
            }
            y[j] = (float)acc;
        }
        return y;
    }

    static float Peak(float[] s)
    {
        float p = 1e-9f;
        foreach (var v in s) p = MathF.Max(p, MathF.Abs(v));
        return p;
    }

    /// <summary>
    /// Loudest 50 ms stretch, RMS, after a gentle high-pass (two poles at 80 Hz): a rough
    /// stand-in for how loud something sounds. Sub-bass counts for little to the ear, so it
    /// mustn't dominate the measure.
    /// </summary>
    static float Loudness(float[] s)
    {
        int w = Rate / 20;
        float a = 1f - LpCoef(80f);
        var f = new float[s.Length];
        float x1 = 0f, h1 = 0f, g1 = 0f;
        double acc = 0, best = 0;
        for (int i = 0; i < s.Length; i++)
        {
            // Two one-pole high-passes in series, then a sliding 50 ms window.
            float h = a * (h1 + s[i] - x1);
            float g = a * (g1 + h - h1);
            x1 = s[i]; h1 = h; g1 = g;
            f[i] = g;
            acc += g * g;
            if (i >= w) acc -= f[i - w] * f[i - w];
            if (acc > best) best = acc;
        }
        return (float)Math.Sqrt(Math.Max(best, 1e-18) / w);
    }

    /// <summary>Cut the inaudible end off a buffer (below -66 dB of its peak), keeping a short fade.</summary>
    static float[] Trim(float[] s)
    {
        float floor = Peak(s) * 5e-4f;
        int last = s.Length - 1;
        while (last > 0 && MathF.Abs(s[last]) < floor) last--;
        int n = Math.Min(s.Length, last + Rate / 50);
        return n >= s.Length ? s : s[..n];
    }

    /// <summary>
    /// Adds an N-wave, the shock of a supersonic bullet: a jump up to +amp, a straight fall to
    /// -amp over T, a jump back to zero. Its shock fronts are microseconds thick, far sharper
    /// than a sample, so it's evaluated at 8x the rate through a windowed-sinc low-pass: all
    /// the crack 44.1 kHz can carry, without the aliasing a naive ramp would add.
    /// </summary>
    static void NWave(Buf b, float amp, float T, float start)
    {
        const int Os = 8, Taps = 24;             // oversampling; half-length of the kernel, in output samples
        const double Rise = 3e-6;                // shock thickness, s
        double hiRate = (double)Rate * Os;
        int j0 = Math.Max(0, (int)(start * Rate) - Taps), j1 = Math.Min(b.S.Length, (int)((start + T) * Rate) + Taps + 2);
        static double Step(double t) => 0.5 * (1 + Math.Tanh(t / Rise));
        for (int j = j0; j < j1; j++)
        {
            double acc = 0;
            for (int m = -Taps * Os; m <= Taps * Os; m++)
            {
                double t = (j * (double)Os + m) / hiRate - start;
                double n = Step(t) * (1 - 2 * t / T) * (1 - Step(t - T));
                if (n == 0) continue;
                double x = m / (double)Os;
                double sinc = m == 0 ? 1 : Math.Sin(Math.PI * 0.9 * x) / (Math.PI * 0.9 * x);
                double win = 0.42 + 0.5 * Math.Cos(Math.PI * m / (Taps * Os)) + 0.08 * Math.Cos(2 * Math.PI * m / (Taps * Os));
                acc += 0.9 * sinc * win * n;
            }
            b.S[j] += (float)(amp * acc / Os);
        }
    }

    /// <summary>
    /// The supersonic crack of a passing bullet, as heard from where its shock left the path: an
    /// N-wave T long (Whitham: it lengthens with distance and calibre, which is what turns a
    /// whip-crack close by into a duller snap further off), then, if it passed close, the tear
    /// and hiss of its wake. <paramref name="close"/> 1 for a pass by the ear, 0 far off.
    /// </summary>
    public static Dry CrackDry(int seed, float T, float close)
    {
        var b = new Buf(0.16f, seed);
        float k = 0.92f + b.Rnd() * 0.16f;
        NWave(b, 1f, T * k, 0.001f);
        float end = 0.001f + T * k;
        Noise(b, 0.3f * close, 0.0015f, 12000f, start: end);                                   // the wake tearing past
        Noise(b, 0.03f + 0.09f * close, 0.03f, 3000f, start: end + 0.0005f, attack: 0.002f); // and its hiss
        return new Dry(b.S);
    }

    /// <summary>
    /// A steel plate struck by a bullet. Its bending modes ring out at a free plate's inharmonic
    /// frequency ratios (Leissa: 1, 1.455, 1.802, 2.584 twice, ...; ~500 Hz up for a 25 cm AR500
    /// plate), the degenerate pairs split a little because no plate is perfectly square or even,
    /// so they beat and shimmer. How hard each mode rings depends on where the hit lands (a random
    /// strike point per variant). Hung on chains a plate rings long: ~2 s below its coincidence
    /// frequency (~1.2 kHz for 10 mm), where it also radiates poorly, ~1 s above, where radiation
    /// damps it. So the "ting" sits above a kHz with a quieter low hum under it. Over it all, the
    /// splash of the bullet breaking up on the face, and the chains jingling.
    /// </summary>
    public static Dry SteelDry(int seed)
    {
        var b = new Buf(2.2f, seed);
        float k = 0.85f + b.Rnd() * 0.35f;
        float[] ratios = { 1f, 1.455f, 1.802f, 2.584f, 2.584f, 4.536f, 4.536f, 4.729f, 5.143f, 5.730f, 7.07f, 8.02f, 9.3f };
        float f1 = 500f * k;
        for (int m = 0; m < ratios.Length; m++)
        {
            float f = f1 * ratios[m] * (1f + (b.Rnd() - 0.5f) * 0.03f);
            float strike = 0.1f + 0.9f * b.Rnd();
            float radiate = MathF.Min(1f, MathF.Pow(f / 1200f, 0.9f));
            float t60 = f < 1000f ? 2f : f > 1500f ? 1f : 2f * MathF.Pow(0.5f, MathF.Log(f / 1000f) / MathF.Log(1.5f));
            float amp = strike * radiate / MathF.Sqrt(1f + 0.25f * m);
            Mode(b, f, amp, t60 / 6.9f, b.Rnd());
            if (b.Rnd() < 0.6f) Mode(b, f * (1.003f + b.Rnd() * 0.006f), amp * (0.3f + 0.4f * b.Rnd()), t60 / 6.9f, b.Rnd());
        }
        Noise(b, 0.9f, 0.0008f, 14000f); // the splash
        Noise(b, 0.3f, 0.004f, 6000f);
        for (int i = 0; i < 7; i++)      // the chains
            Noise(b, 0.015f + 0.02f * b.Rnd(), 0.002f, 4000f + 4000f * b.Rnd(), start: 0.02f + b.Rnd() * 0.35f);
        return new Dry(b.S);
    }

    /// <summary>A round landing on a surface: what it hits sets what it sounds like.</summary>
    public static AudioStreamWav Hit(Surface surface, int seed) => Finish(surface switch
    {
        Surface.Sand => HitSand(seed),
        Surface.Rock or Surface.Stone => HitStone(seed, surface == Surface.Rock),
        Surface.Wood => HitWood(seed),
        Surface.Metal => HitMetal(seed),
        Surface.Flesh => HitFlesh(seed),
        _ => HitDirt(seed),
    });

    /// <summary>Into soil or turf: a dull slap and thud, the earth heaving, grit pattering back down.</summary>
    static float[] HitDirt(int seed)
    {
        var b = new Buf(0.4f, seed);
        float k = 0.85f + b.Rnd() * 0.3f;
        Noise(b, 0.8f, 0.012f, 2500f * k);
        Tone(b, 110f * k, 60f, 0.01f, 0.6f, 0.03f, phase: b.Rnd());
        Noise(b, 0.25f, 0.08f, 900f, start: 0.01f);
        for (int i = 0; i < 18; i++)
            Noise(b, 0.03f + 0.05f * b.Rnd(), 0.001f, 3500f + 3000f * b.Rnd(), start: 0.015f + b.Rnd() * b.Rnd() * 0.25f);
        return b.S;
    }

    /// <summary>Into sand: a soft, low puff with no click, and a hiss of spray falling back.</summary>
    static float[] HitSand(int seed)
    {
        var b = new Buf(0.4f, seed);
        float k = 0.85f + b.Rnd() * 0.3f;
        Noise(b, 0.6f, 0.02f, 1400f * k, attack: 0.001f);
        Tone(b, 90f * k, 55f, 0.01f, 0.4f, 0.03f, phase: b.Rnd());
        Noise(b, 0.16f, 0.15f, 7000f, start: 0.01f, attack: 0.01f);
        return b.S;
    }

    /// <summary>
    /// Into masonry or rock: a hard, bright crack, the short knock of a stiff surface, then
    /// chips and grit spraying off and a breath of dust. Rock rings a touch lower and longer.
    /// </summary>
    static float[] HitStone(int seed, bool rock)
    {
        var b = new Buf(0.4f, seed);
        float k = (rock ? 0.8f : 1f) * (0.9f + b.Rnd() * 0.2f);
        Noise(b, 1.0f, 0.0008f, 15000f);
        for (int i = 0; i < 3; i++)
            Mode(b, (1800f + 2400f * b.Rnd()) * k, 0.2f + 0.2f * b.Rnd(), (rock ? 0.012f : 0.007f) + 0.006f * b.Rnd(), b.Rnd());
        Noise(b, 0.35f, 0.006f, 5000f, start: 0.0008f);
        for (int i = 0; i < 25; i++)
            Noise(b, 0.03f + 0.07f * b.Rnd(), 0.0008f, 4000f + 5000f * b.Rnd(), start: 0.004f + b.Rnd() * b.Rnd() * 0.18f);
        Noise(b, 0.08f, 0.12f, 3000f, start: 0.01f, attack: 0.02f);
        return b.S;
    }

    /// <summary>Into timber (a door, a tree, a floor): the strike, a hollow knock, splinters crackling.</summary>
    static float[] HitWood(int seed)
    {
        var b = new Buf(0.3f, seed);
        float k = 0.85f + b.Rnd() * 0.3f;
        Noise(b, 0.6f, 0.0015f, 8000f);
        Mode(b, 380f * k, 0.55f, 0.035f, b.Rnd());
        Mode(b, 610f * k, 0.4f, 0.025f, b.Rnd());
        Mode(b, 1150f * k, 0.25f, 0.015f, b.Rnd());
        Mode(b, 2100f * k, 0.15f, 0.008f, b.Rnd());
        for (int i = 0; i < 8; i++)
            Noise(b, 0.05f + 0.07f * b.Rnd(), 0.0006f, 3000f + 3000f * b.Rnd(), start: 0.002f + b.Rnd() * 0.03f);
        return b.S;
    }

    /// <summary>
    /// Through sheet metal (a container, a truck body): a bright perforation "tink", then the sheet
    /// ringing. Thin metal has so many modes the ring is almost noise, and below its coincidence
    /// frequency (several kHz for a few mm) it radiates poorly: a tinny, high ring and a rattle.
    /// </summary>
    static float[] HitMetal(int seed)
    {
        var b = new Buf(0.6f, seed);
        Noise(b, 0.9f, 0.0006f, 15000f);
        for (int i = 0; i < 14; i++)
        {
            float f = 500f * MathF.Pow(16f, b.Rnd());
            Mode(b, f, (0.1f + 0.2f * b.Rnd()) * MathF.Min(1f, f / 3000f + 0.15f), 0.05f + 0.15f * b.Rnd(), b.Rnd());
        }
        Noise(b, 0.12f, 0.12f, 7000f, start: 0.001f); // the dense ring
        Noise(b, 0.2f, 0.02f, 3000f);
        return b.S;
    }

    /// <summary>Into a body: a dull, wet thud and a slap. Nothing rings.</summary>
    static float[] HitFlesh(int seed)
    {
        var b = new Buf(0.25f, seed);
        float k = 0.85f + b.Rnd() * 0.3f;
        Noise(b, 0.7f, 0.009f, 900f * k, attack: 0.001f);
        Tone(b, 150f * k, 90f, 0.01f, 0.35f, 0.02f, phase: b.Rnd());
        Noise(b, 0.25f, 0.003f, 3500f);
        return b.S;
    }

    /// <summary>
    /// A round glancing off something hard and tumbling away: its two ends beat the air twice a
    /// turn, a harmonic-rich whine (300-1500 Hz for a whole bullet, higher for a small fragment)
    /// that sags in pitch and fades as the spin slows and it leaves, wobbling as it tumbles.
    /// </summary>
    public static AudioStreamWav Ricochet(int seed)
    {
        var b = new Buf(1.2f, seed);
        Noise(b, 0.7f, 0.0008f, 14000f); // the strike
        bool fragment = b.Rnd() < 0.3f;
        float f0 = fragment ? 1000f + 2500f * b.Rnd() : 300f + 1200f * b.Rnd();
        float tauF = 0.5f + b.Rnd(), tauA = (fragment ? 0.08f : 0.2f) + 0.3f * b.Rnd(), wob = 0.05f + 0.1f * b.Rnd();
        double ph = b.Rnd() * Math.Tau;
        float y = 0f, drift = 0f;
        int i0 = (int)(0.002f * Rate);
        for (int i = i0; i < b.S.Length; i++)
        {
            float t = (i - i0) / (float)Rate;
            float env = MathF.Min(1f, t / 0.008f) * MathF.Exp(-t / tauA);
            if (t > 0.02f && env < 1e-3f) break;
            drift += 0.004f * ((b.Rnd() * 2f - 1f) - drift); // slow random wobble of the tumble rate
            float f = f0 * MathF.Exp(-t / tauF) * (1f + wob * drift * 6f);
            ph += Math.Tau * f / Rate;
            y += 0.3f * (b.Rnd() * 2f - 1f - y);
            b.S[i] += ((float)Math.Sin(ph) + 0.5f * (float)Math.Sin(2 * ph + 0.7) + 0.25f * (float)Math.Sin(3 * ph + 1.9) + y * 0.3f) * env * 0.4f;
        }
        return Finish(b);
    }

    /// <summary>A footstep on the given surface.</summary>
    public static AudioStreamWav Step(Surface surface, int seed) => Finish(surface switch
    {
        Surface.Sand => StepSand(seed),
        Surface.Rock => StepGravel(seed),
        Surface.Stone => StepHard(seed),
        Surface.Wood => StepWood(seed),
        Surface.Metal => StepMetal(seed),
        _ => StepGrass(seed),
    });

    /// <summary>
    /// A boot on grass and grit: a soft, low heel strike (slow attack, no click),
    /// a few tiny grains crunching as the sole rolls, and a bit of gear rustle.
    /// </summary>
    static float[] StepGrass(int seed)
    {
        var b = new Buf(0.3f, seed);
        Noise(b, 0.55f, 0.022f, 520f, attack: 0.007f);
        Tone(b, 70f, 55f, 0.02f, 0.18f, 0.025f, attack: 0.006f);
        float roll = 0.02f + b.Rnd() * 0.03f;
        for (int i = 0; i < 14; i++)
            Noise(b, 0.05f + b.Rnd() * 0.05f, 0.0012f, 2500f + b.Rnd() * 2500f, start: roll + b.Rnd() * 0.07f);
        Noise(b, 0.07f, 0.07f, 1600f, start: 0.01f, attack: 0.03f);
        return b.S;
    }

    /// <summary>Sand: the sole sinking in, only a soft low press (it gives), and a dry crunch as it settles.</summary>
    static float[] StepSand(int seed)
    {
        var b = new Buf(0.4f, seed);
        Noise(b, 0.5f, 0.035f, 300f, attack: 0.015f);
        for (int i = 0; i < 45; i++)
            Noise(b, 0.02f + b.Rnd() * 0.03f, 0.0009f, 1500f + b.Rnd() * 4500f, start: 0.01f + b.Rnd() * 0.25f);
        Noise(b, 0.07f, 0.12f, 4500f, start: 0.015f, attack: 0.04f);
        return b.S;
    }

    /// <summary>Gravel and scree: a heel strike, then the stones shifting, a long, dense crunch (stones ring ~6 kHz).</summary>
    static float[] StepGravel(int seed)
    {
        var b = new Buf(0.45f, seed);
        Noise(b, 0.4f, 0.015f, 900f, attack: 0.004f);
        float roll = 0.01f + b.Rnd() * 0.02f;
        for (int i = 0; i < 60; i++)
        {
            float st = roll + b.Rnd() * b.Rnd() * 0.3f;
            Mode(b, 4000f + 5000f * b.Rnd(), 0.04f + b.Rnd() * 0.08f, 0.0015f, b.Rnd(), start: st);
            Noise(b, 0.03f + b.Rnd() * 0.05f, 0.0008f, 8000f, start: st);
        }
        return b.S;
    }

    /// <summary>
    /// Concrete, paving or a stone floor: the heel's short dry click and a little low thump, then
    /// the toe a tenth of a second later, and a gritty scuff.
    /// </summary>
    static float[] StepHard(int seed)
    {
        var b = new Buf(0.35f, seed);
        float k = 0.9f + b.Rnd() * 0.2f;
        Noise(b, 0.6f, 0.004f, 4000f);
        Mode(b, 1800f * k, 0.15f, 0.005f, b.Rnd());
        Tone(b, 70f * k, 55f, 0.01f, 0.35f, 0.025f, phase: b.Rnd());
        float toe = 0.1f + b.Rnd() * 0.03f;
        Noise(b, 0.3f, 0.003f, 4500f, start: toe);
        Tone(b, 90f * k, 70f, 0.01f, 0.15f, 0.02f, start: toe, phase: b.Rnd());
        Noise(b, 0.1f, 0.04f, 6000f, start: 0.01f, attack: 0.012f);
        return b.S;
    }

    /// <summary>A wooden floor: a hollow low thump (the joists and the space under them), the boards knocking, and now and then a creak.</summary>
    static float[] StepWood(int seed)
    {
        var b = new Buf(0.4f, seed);
        float k = 0.9f + b.Rnd() * 0.2f;
        Noise(b, 0.4f, 0.002f, 5000f);
        Tone(b, 90f * k, 70f * k, 0.02f, 0.5f, 0.04f, phase: b.Rnd());
        Mode(b, 190f * k, 0.5f, 0.03f, b.Rnd());
        Mode(b, 470f * k, 0.3f, 0.02f, b.Rnd());
        Mode(b, 1050f * k, 0.12f, 0.012f, b.Rnd());
        if (b.Rnd() < 0.35f)
        {
            // A board creaks: stick-slip, a scratchy tone sliding up.
            double ph = 0;
            float st = 0.05f + b.Rnd() * 0.05f, f0 = 500f + b.Rnd() * 300f;
            for (int i = (int)(st * Rate); i < b.S.Length; i++)
            {
                float t = i / (float)Rate - st;
                if (t > 0.3f) break;
                float env = MathF.Min(1f, t / 0.02f) * MathF.Exp(-t / 0.06f);
                ph += Math.Tau * (f0 + 400f * t) / Rate;
                float stick = MathF.Sin(t * 180f) > 0.2f ? 1f : 0.3f;
                b.S[i] += (float)Math.Sin(ph) * env * stick * 0.08f;
            }
        }
        return b.S;
    }

    /// <summary>A metal deck or grating: a heel click, a hollow boom from the space under it, and plate modes ringing a good while.</summary>
    static float[] StepMetal(int seed)
    {
        var b = new Buf(0.7f, seed);
        float k = 0.9f + b.Rnd() * 0.2f;
        Noise(b, 0.6f, 0.0012f, 9000f);
        foreach (var f in new[] { 310f, 740f, 1230f, 1980f, 3100f })
            Mode(b, f * k * (0.95f + 0.1f * b.Rnd()), 0.12f + 0.15f * b.Rnd(), 0.04f + 0.08f * b.Rnd(), b.Rnd());
        Tone(b, 110f * k, 80f, 0.01f, 0.35f, 0.05f, phase: b.Rnd());
        return b.S;
    }

    public static AudioStreamWav Click(int seed)
    {
        var b = new Buf(0.08f, seed);
        Noise(b, 0.8f, 0.0012f, 14000f);
        Tone(b, 3400f, 3000f, 0.01f, 0.3f, 0.004f);
        Noise(b, 0.4f, 0.001f, 9000f, start: 0.012f);
        return Finish(b);
    }

    public static AudioStreamWav MagOut(int seed)
    {
        var b = new Buf(0.3f, seed);
        Noise(b, 0.8f, 0.0015f, 12000f);
        Noise(b, 0.3f, 0.08f, 3000f, start: 0.02f, attack: 0.02f);
        Tone(b, 600f, 500f, 0.1f, 0.2f, 0.03f, start: 0.1f);
        return Finish(b);
    }

    public static AudioStreamWav MagIn(int seed)
    {
        var b = new Buf(0.25f, seed);
        Tone(b, 220f, 150f, 0.02f, 0.7f, 0.03f);
        Noise(b, 0.9f, 0.004f, 6000f);
        Noise(b, 0.6f, 0.0015f, 12000f, start: 0.05f);
        return Finish(b);
    }

    public static AudioStreamWav Bolt(int seed)
    {
        var b = new Buf(0.3f, seed);
        Noise(b, 1.0f, 0.003f, 9000f);
        Tone(b, 900f, 700f, 0.01f, 0.5f, 0.02f);
        Noise(b, 0.8f, 0.003f, 7000f, start: 0.045f);
        Tone(b, 300f, 200f, 0.02f, 0.6f, 0.04f, start: 0.045f);
        return Finish(b);
    }

    public static AudioStreamWav Bounce(int seed)
    {
        var b = new Buf(0.15f, seed);
        Tone(b, 700f, 500f, 0.01f, 0.5f, 0.02f);
        Noise(b, 0.5f, 0.006f, 4000f);
        return Finish(b);
    }

    /// <summary>The hollow "thoonk" of a 40 mm launcher: low pressure, so a thump rather than a crack.</summary>
    public static Dry LauncherDry(int seed)
    {
        var b = new Buf(0.8f, seed);
        Tone(b, 130f, 62f, 0.03f, 1.0f, 0.07f, phase: b.Rnd());
        Noise(b, 0.5f, 0.02f, 900f, attack: 0.002f);
        Noise(b, 0.25f, 0.004f, 5000f);
        Tone(b, 420f, 380f, 0.02f, 0.12f, 0.05f, start: 0.004f, phase: b.Rnd()); // the tube ringing
        Noise(b, 0.06f, 0.2f, 600f, start: 0.03f, attack: 0.03f);
        return new Dry(b.S);
    }

    /// <summary>Your own heartbeat, heard from the inside: a low lub-dub.</summary>
    public static AudioStreamWav Heartbeat()
    {
        var b = new Buf(0.5f, 7);
        Tone(b, 70f, 45f, 0.03f, 1.0f, 0.05f, attack: 0.008f);
        Noise(b, 0.25f, 0.03f, 120f, attack: 0.01f);
        Tone(b, 62f, 40f, 0.03f, 0.7f, 0.045f, start: 0.16f, attack: 0.008f);
        return Finish(b);
    }

    /// <summary>A rocket leaving the tube: a sharp bang and backblast, then the motor's roar tearing away.</summary>
    public static Dry RocketDry(int seed)
    {
        var b = new Buf(2.2f, seed);
        Blast(b, 1.0f, 0.002f, 1.4f);
        Noise(b, 0.8f, 0.05f, 3500f, attack: 0.001f);
        Tone(b, 90f, 40f, 0.03f, 0.6f, 0.12f, phase: b.Rnd());
        // The motor: a hissing roar that fades as it goes.
        Noise(b, 0.45f, 0.6f, 2200f, start: 0.03f, attack: 0.02f);
        Noise(b, 0.25f, 0.9f, 700f, start: 0.05f, attack: 0.05f);
        return new Dry(b.S);
    }

    /// <summary>A shovel biting into dirt and a sandbag dropping.</summary>
    public static AudioStreamWav Shovel(int seed)
    {
        var b = new Buf(0.5f, seed);
        Noise(b, 0.6f, 0.03f, 1800f, attack: 0.004f);
        for (int i = 0; i < 10; i++) Noise(b, 0.08f, 0.002f, 4000f, start: 0.02f + b.Rnd() * 0.12f);
        Tone(b, 160f, 110f, 0.02f, 0.3f, 0.04f, start: 0.01f);
        Noise(b, 0.3f, 0.05f, 500f, start: 0.22f + b.Rnd() * 0.08f, attack: 0.006f);
        return Finish(b);
    }

    /// <summary>Gear rustle: a pouch opening, magazines changing hands, a bandage being torn.</summary>
    public static AudioStreamWav Rustle(int seed)
    {
        var b = new Buf(0.6f, seed);
        for (int i = 0; i < 6; i++) Noise(b, 0.3f + b.Rnd() * 0.3f, 0.03f, 2500f + b.Rnd() * 3000f, start: b.Rnd() * 0.4f, attack: 0.01f);
        return Finish(b);
    }

    /// <summary>Ear ringing after a close blast.</summary>
    /// <summary>
    /// Ear ringing: a steady, seamlessly looping pair of close tones (they beat slowly
    /// against each other) plus a faint hiss. Its loudness is shaped live by SoundWorld.
    /// </summary>
    public static AudioStreamWav Tinnitus()
    {
        const float seconds = 4f; // whole cycles of every component, so the loop has no seam
        int n = (int)(seconds * Rate);
        var s = new float[n];
        var r = new Random(99);
        float hiss = 0f;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)Rate;
            hiss += 0.08f * ((float)r.NextDouble() * 2f - 1f - hiss);
            s[i] = (float)(0.55 * Math.Sin(2 * Math.PI * 3700 * t) + 0.3 * Math.Sin(2 * Math.PI * 3731.25 * t)) + hiss * 0.15f;
        }
        // Blend the tail of the hiss into the head so it loops cleanly too.
        int x = Rate / 20;
        for (int i = 0; i < x; i++) { float w = i / (float)x; s[n - x + i] = s[n - x + i] * (1f - w) + s[i] * w; }
        var bytes = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            short q = (short)Math.Clamp((int)(s[i] * 0.8f * 32767f), -32768, 32767);
            bytes[2 * i] = (byte)(q & 0xff);
            bytes[2 * i + 1] = (byte)((q >> 8) & 0xff);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = bytes,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = n,
        };
    }
}
