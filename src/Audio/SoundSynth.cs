using Godot;

namespace Ridgeline;

/// <summary>
/// Builds every sound in the game from noise and sine waves at startup, so the
/// prototype needs no audio files. Loud sounds come in distance layers (near /
/// mid / far): far away, the air has eaten the highs and the terrain has added
/// echoes, which is most of what makes a distant rifle sound distant. Real
/// recordings can replace any of these later without touching SoundWorld.
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

    /// <summary>Adds a sine whose pitch glides from f0 to f1 — the body "thump" of a shot or blast.</summary>
    static void Tone(Buf b, float f0, float f1, float sweepTau, float gain, float tau, float start = 0f, float attack = 0.001f)
    {
        int i0 = (int)(start * Rate);
        double ph = 0;
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

    /// <summary>A darker, delayed copy of everything so far — a reflection off a hillside or treeline.</summary>
    static void Echo(Buf b, float delay, float gain, float lpHz)
    {
        var src = (float[])b.S.Clone();
        float a = LpCoef(lpHz), y = 0f;
        int d = (int)(delay * Rate);
        for (int i = d; i < b.S.Length; i++)
        {
            y += a * (src[i - d] - y);
            b.S[i] += y * gain;
        }
    }

    static void Lowpass(Buf b, float hz)
    {
        float a = LpCoef(hz), y = 0f;
        for (int i = 0; i < b.S.Length; i++) { y += a * (b.S[i] - y); b.S[i] = y; }
    }

    static AudioStreamWav Finish(Buf b) => Finish(b.S);

    static AudioStreamWav Finish(float[] s)
    {
        float peak = 1e-6f;
        foreach (var v in s) peak = MathF.Max(peak, MathF.Abs(v));
        float g = 0.9f / peak;
        int fade = Math.Min(s.Length, Rate / 50);
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
            MixRate = Rate,
            Stereo = false,
            Data = bytes,
        };
    }

    /// <summary>
    /// A Friedlander wave: the pressure pulse of a muzzle blast or explosion. An
    /// instant rise, a positive phase of length T, then a shallower negative phase.
    /// </summary>
    static void Blast(Buf b, float amp, float T, float decay, float start = 0f)
    {
        int i0 = (int)(start * Rate), n = (int)(T * 8f * Rate);
        for (int i = 0; i < n && i0 + i < b.S.Length; i++)
        {
            float t = i / (float)Rate;
            b.S[i0 + i] += amp * (1f - t / T) * MathF.Exp(-decay * t / T);
        }
    }

    /// <summary>
    /// A rifle shot as heard a metre or two from the muzzle, before the air and the
    /// terrain get to it: the blast wave, the roar of the propellant gas leaving the
    /// barrel, a low thump, and the bolt cycling. Acoustics does the distance.
    /// </summary>
    /// <param name="weight">1 = 5.56 carbine; ~1.3 = 7.62 rifle (longer blast, more low end).</param>
    public static float[] GunshotDry(int seed, float weight)
    {
        var b = new Buf(0.45f, seed);
        float k = 0.92f + b.Rnd() * 0.16f;
        Blast(b, 1.0f, 0.00045f * weight * k, 1.3f);
        // The pressure punch: the slower part of the blast wave. Almost all low frequencies,
        // which the air barely absorbs, so it's what still hits at a distance under the crack.
        Blast(b, 0.45f, 0.0022f * weight * k, 1.1f);
        Noise(b, 0.55f, 0.0025f, 14000f);                                     // gas turbulence
        Noise(b, 0.45f, 0.012f * weight, 3000f / weight, attack: 0.0005f);    // the roar: short and bright, it's what carries as the "pop"
        Tone(b, 150f / weight * k, 55f / weight, 0.012f, 0.36f, 0.026f * weight); // body thump: a punch, kept short so far off it doesn't smear into a boom
        // The action: bolt carrier slamming back and forward, only audible up close.
        float cyc = 0.045f + b.Rnd() * 0.01f;
        Noise(b, 0.05f, 0.003f, 6000f, start: cyc);
        Tone(b, 1900f * k, 1700f * k, 0.01f, 0.025f, 0.01f, start: cyc);
        Noise(b, 0.035f, 0.003f, 5000f, start: cyc + 0.03f);
        return b.S;
    }

    /// <summary>30 mm: a heavier, slower-cracking blast with a hard mechanical clank from the feed.</summary>
    public static float[] AutocannonDry(int seed)
    {
        var b = new Buf(0.6f, seed);
        float k = 0.93f + b.Rnd() * 0.14f;
        Blast(b, 1.0f, 0.0012f * k, 1.4f);
        Noise(b, 0.6f, 0.004f, 12000f);
        Noise(b, 0.55f, 0.04f, 1600f, attack: 0.001f);
        Tone(b, 95f * k, 38f, 0.02f, 0.7f, 0.07f);
        Noise(b, 0.08f, 0.004f, 5000f, start: 0.05f);
        Tone(b, 900f, 800f, 0.01f, 0.04f, 0.02f, start: 0.05f);
        return b.S;
    }

    /// <summary>A tank gun: a long, heavy blast wave that you feel more than hear up close.</summary>
    public static float[] CannonDry(int seed)
    {
        var b = new Buf(1.6f, seed);
        Blast(b, 1.0f, 0.0045f * (0.95f + b.Rnd() * 0.1f), 1.5f);
        Noise(b, 0.7f, 0.008f, 12000f);
        Noise(b, 0.8f, 0.09f, 1100f, attack: 0.002f);
        Tone(b, 60f, 24f, 0.06f, 0.9f, 0.3f);
        Noise(b, 0.35f, 0.5f, 300f, start: 0.02f, attack: 0.05f);
        Noise(b, 0.05f, 0.01f, 4000f, start: 0.6f + b.Rnd() * 0.2f); // the breech and the spent case
        Tone(b, 520f, 480f, 0.02f, 0.03f, 0.05f, start: 0.62f);
        return b.S;
    }

    /// <summary>
    /// An engine idling as a seamless loop: firing pulses at a fixed rate (pitch-shifted
    /// live for revs), exhaust rumble, and for tracked vehicles the squeak and clatter of track.
    /// </summary>
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
                // Track links on the sprockets: a light clatter and a high squeal.
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

    /// <summary>A mortar: a deep, hollow cough out of the tube.</summary>
    public static AudioStreamWav MortarFire(int seed)
    {
        var b = new Buf(1.6f, seed);
        Tone(b, 110f, 48f, 0.04f, 1.0f, 0.12f);
        Blast(b, 0.7f, 0.003f, 1.4f);
        Noise(b, 0.6f, 0.05f, 1400f, attack: 0.002f);
        Noise(b, 0.2f, 0.4f, 400f, start: 0.02f, attack: 0.04f);
        Echo(b, 0.4f, 0.3f, 700f);
        Echo(b, 1.0f, 0.15f, 500f);
        return Finish(b);
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

    /// <summary>A seeker's lock tone.</summary>
    public static AudioStreamWav Beep(int seed)
    {
        var b = new Buf(0.12f, seed);
        Tone(b, 1600f, 1600f, 1f, 0.6f, 0.2f, attack: 0.005f);
        return Finish(b);
    }

    /// <summary>A round striking armour and not getting in: a dull, heavy clank, not a target plate's ring.</summary>
    public static AudioStreamWav ArmorHit(int seed)
    {
        var b = new Buf(0.4f, seed);
        float k = 0.9f + b.Rnd() * 0.2f;
        Noise(b, 0.9f, 0.006f, 4000f);
        Tone(b, 240f * k, 180f * k, 0.02f, 0.7f, 0.04f);
        Tone(b, 710f * k, 690f * k, 0.02f, 0.18f, 0.025f);
        Noise(b, 0.2f, 0.05f, 800f, start: 0.004f);
        return Finish(b);
    }

    /// <summary>A hand grenade up close: a long, heavy blast wave, fireball roar, rumble and gravel raining down.</summary>
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

    /// <param name="keep">How much of the absorption's loudness loss to keep (1 = physical).</param>
    public static Banded Distanced(Func<int, float[]> dry, int variants, int seed, float[] distances,
                                   float wetAt10, float decay, int echoes, float echoSpan, float tail, float keep)
    {
        int bands = distances.Length;
        var raw = new float[variants][][];
        System.Threading.Tasks.Parallel.For(0, variants, v =>
        {
            var src = dry(seed + v * 31);
            var r = new Random(seed * 7 + v);
            int tailN = (int)(tail * Rate);
            var ir = Acoustics.Environment(tailN, r, decay, echoes, echoSpan);
            double e = 0;
            foreach (var x in ir) e += x * x;
            float g = (float)(1.0 / Math.Sqrt(Math.Max(e, 1e-12)));
            for (int i = 0; i < ir.Length; i++) ir[i] *= g; // tail energy = direct impulse energy
            int n = Acoustics.Pow2(src.Length + tailN);
            var drySpec = Acoustics.Spectrum(src, n);
            var envSpec = Acoustics.Spectrum(ir, n);
            raw[v] = new float[bands][];
            for (int k = 0; k < bands; k++) raw[v][k] = Acoustics.Propagate(drySpec, envSpec, distances[k], wetAt10, src.Length + tailN);
        });

        var res = new Banded { Layers = new AudioStreamWav[bands][], LayerDb = new float[bands] };
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < bands; k++)
        {
            res.Layers[k] = new AudioStreamWav[variants];
            float db = 0f;
            for (int v = 0; v < variants; v++)
            {
                float l0 = Loudness(raw[v][0]), p0 = Peak(raw[v][0]);
                float l = Loudness(raw[v][k]), p = Peak(raw[v][k]);
                // Played normalised, a buffer's loudness is l/p; we want (l/l0)^keep relative to band 0.
                float target = keep * 20f * MathF.Log10(l / l0);
                float normalised = 20f * MathF.Log10((l / p) / (l0 / p0));
                db += (target - normalised) / variants;
                res.Layers[k][v] = Finish(raw[v][k]);
            }
            res.LayerDb[k] = db;
            sb.Append($"{distances[k]:0}m {db:+0.0;-0.0}dB  ");
        }
        res.Report = sb.ToString();
        return res;
    }

    static float Peak(float[] s)
    {
        float p = 1e-9f;
        foreach (var v in s) p = MathF.Max(p, MathF.Abs(v));
        return p;
    }

    /// <summary>Loudest 50 ms stretch, RMS: a rough stand-in for how loud something sounds.</summary>
    static float Loudness(float[] s)
    {
        int w = Rate / 20;
        double acc = 0, best = 0;
        for (int i = 0; i < s.Length; i++)
        {
            acc += s[i] * s[i];
            if (i >= w) acc -= s[i - w] * s[i - w];
            if (acc > best) best = acc;
        }
        return (float)Math.Sqrt(Math.Max(best, 1e-18) / w);
    }

    /// <summary>The supersonic crack of a bullet passing close by — an N-shaped shock wave, not a gunshot.</summary>
    public static AudioStreamWav Crack(int seed)
    {
        var b = new Buf(0.5f, seed);
        int n = (int)((0.0005f + b.Rnd() * 0.0003f) * Rate);
        for (int i = 0; i < n; i++) b.S[i] += 1f - 2f * i / n;
        Noise(b, 0.5f, 0.002f, 12000f, start: n / (float)Rate);
        Noise(b, 0.12f, 0.05f, 2500f, start: 0.001f);
        Echo(b, 0.1f + b.Rnd() * 0.08f, 0.15f, 2000f);
        return Finish(b);
    }

    public static AudioStreamWav Steel(int seed, int layer)
    {
        var b = new Buf(1.4f, seed);
        float k = 0.9f + b.Rnd() * 0.2f;
        Noise(b, 0.6f, 0.001f, 12000f);
        Tone(b, 820f * k, 820f * k, 1f, 0.9f, 0.5f);
        Tone(b, 2210f * k, 2210f * k, 1f, 0.6f, 0.3f);
        Tone(b, 3900f * k, 3900f * k, 1f, 0.35f, 0.18f);
        Tone(b, 5600f * k, 5600f * k, 1f, 0.2f, 0.1f);
        if (layer > 0) Lowpass(b, 2500f);
        return Finish(b);
    }

    public static AudioStreamWav Impact(int seed)
    {
        var b = new Buf(0.35f, seed);
        Noise(b, 0.8f, 0.012f, 2500f);
        Tone(b, 110f, 60f, 0.01f, 0.6f, 0.03f);
        Noise(b, 0.25f, 0.08f, 900f, start: 0.01f);
        return Finish(b);
    }

    /// <summary>
    /// A boot on grass and grit: a soft, low heel strike (slow attack, no click),
    /// a few tiny grains crunching as the sole rolls, and a bit of gear rustle.
    /// </summary>
    public static AudioStreamWav Footstep(int seed)
    {
        var b = new Buf(0.3f, seed);
        Noise(b, 0.55f, 0.022f, 520f, attack: 0.007f);
        Tone(b, 70f, 55f, 0.02f, 0.18f, 0.025f, attack: 0.006f);
        float roll = 0.02f + b.Rnd() * 0.03f;
        for (int i = 0; i < 14; i++)
            Noise(b, 0.05f + b.Rnd() * 0.05f, 0.0012f, 2500f + b.Rnd() * 2500f, start: roll + b.Rnd() * 0.07f);
        Noise(b, 0.07f, 0.07f, 1600f, start: 0.01f, attack: 0.03f);
        return Finish(b);
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
    public static AudioStreamWav Launcher(int seed)
    {
        var b = new Buf(0.8f, seed);
        Tone(b, 130f, 62f, 0.03f, 1.0f, 0.07f);
        Noise(b, 0.5f, 0.02f, 900f, attack: 0.002f);
        Noise(b, 0.25f, 0.004f, 5000f);
        Tone(b, 420f, 380f, 0.02f, 0.12f, 0.05f, start: 0.004f); // the tube ringing
        Noise(b, 0.06f, 0.2f, 600f, start: 0.03f, attack: 0.03f);
        return Finish(b);
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
    public static AudioStreamWav RocketLaunch(int seed)
    {
        var b = new Buf(2.2f, seed);
        Blast(b, 1.0f, 0.002f, 1.4f);
        Noise(b, 0.8f, 0.05f, 3500f, attack: 0.001f);
        Tone(b, 90f, 40f, 0.03f, 0.6f, 0.12f);
        // The motor: a hissing roar that fades as it goes.
        Noise(b, 0.45f, 0.6f, 2200f, start: 0.03f, attack: 0.02f);
        Noise(b, 0.25f, 0.9f, 700f, start: 0.05f, attack: 0.05f);
        Echo(b, 0.35f, 0.25f, 900f);
        return Finish(b);
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
