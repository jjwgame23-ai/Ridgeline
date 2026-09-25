namespace Ridgeline;

/// <summary>
/// Outdoor sound propagation, applied offline to a "dry" source recording
/// (what you'd hear a metre from the muzzle) to get what it sounds like from
/// further away. Three things change a sound with distance, besides getting quieter:
///
/// 1. Air absorbs high frequencies far more than low ones (ISO 9613-1): at 1 km a
///    rifle has lost ~100 dB at 8 kHz but only ~5 dB at 1 kHz. The sharp crack of a
///    close shot turns into a dull "pop" or "thump".
/// 2. The ground reflection interferes with the direct sound at grazing angles and
///    carves a dip out of the low-mids (a few hundred Hz over grass).
/// 3. The direct sound falls off faster than the reverberant field from hills,
///    trees and buildings. Close up you hear the shot and a short tail; far away you
///    mostly hear the tail — the long rolling echo of distant gunfire.
///
/// Everything is done in the frequency domain: one FFT of the dry source and one of
/// the environment's impulse response, then per distance a multiply and an inverse FFT.
/// </summary>
public static class Acoustics
{
    const float TempC = 15f, Humidity = 60f;

    /// <summary>Atmospheric absorption in dB per metre at frequency f (ISO 9613-1, 1 atm).</summary>
    public static double AbsorptionDbPerM(double f)
    {
        double T = TempC + 273.15, T0 = 293.15, T01 = 273.16;
        double psat = Math.Pow(10, -6.8346 * Math.Pow(T01 / T, 1.261) + 4.6151); // relative to 1 atm
        double h = Humidity * psat;                                              // molar concentration of water vapour, %
        double frO = 24 + 4.04e4 * h * (0.02 + h) / (0.391 + h);
        double frN = Math.Pow(T / T0, -0.5) * (9 + 280 * h * Math.Exp(-4.170 * (Math.Pow(T / T0, -1.0 / 3) - 1)));
        double f2 = f * f;
        return 8.686 * f2 * (1.84e-11 * Math.Sqrt(T / T0)
            + Math.Pow(T / T0, -2.5) * (0.01275 * Math.Exp(-2239.1 / T) / (frO + f2 / frO)
                                        + 0.1068 * Math.Exp(-3352.0 / T) / (frN + f2 / frN)));
    }

    /// <summary>Extra attenuation from the ground reflection: a dip around a few hundred Hz that deepens with distance.</summary>
    static double GroundDip(double f, float d)
    {
        if (d < 12f || f < 20) return 1.0;
        double depthDb = Math.Min(11.0, 7.0 * Math.Log10(d / 12f));
        double centre = 520.0 * Math.Pow(d / 50.0, -0.12); // moves down a little with distance
        double oct = Math.Log2(f / centre);
        return Math.Pow(10, -depthDb * Math.Exp(-oct * oct / (2 * 0.55 * 0.55)) / 20);
    }

    /// <summary>
    /// How strong the reverberant tail is relative to the direct sound. The direct
    /// sound spreads as 1/d, the diffuse field in a valley much more slowly.
    /// </summary>
    static float Wet(float d, float wetAt10) => wetAt10 * MathF.Sqrt(MathF.Max(d, 2f) / 10f);

    /// <summary>
    /// A synthetic outdoor impulse response: a handful of discrete echoes (hillsides,
    /// treelines, walls) and a diffuse tail that loses its highs as it decays.
    /// </summary>
    public static float[] Environment(int length, Random r, float decay, int echoes, float echoSpan)
    {
        const int rate = SoundSynth.Rate;
        var ir = new float[length];
        // Diffuse tail: noise, low-passed more and more over time, exponential decay.
        float y = 0f;
        for (int i = (int)(0.012f * rate); i < length; i++)
        {
            float t = i / (float)rate;
            float env = MathF.Exp(-t / decay) * MathF.Min(1f, t / 0.05f);
            if (env < 1e-4f) break;
            float cutoff = 4000f * MathF.Exp(-t / 0.35f) + 350f;
            float a = 1f - MathF.Exp(-2f * MathF.PI * cutoff / rate);
            y += a * ((float)r.NextDouble() * 2f - 1f - y);
            ir[i] += y * env * 0.05f;
        }
        // Discrete reflections: each one a short smeared blip, darker the later it comes.
        for (int k = 0; k < echoes; k++)
        {
            float t0 = 0.05f + MathF.Pow((float)r.NextDouble(), 1.4f) * echoSpan;
            float amp = (0.25f + 0.5f * (float)r.NextDouble()) * MathF.Exp(-t0 / (decay * 1.6f));
            float cutoff = 2500f / (1f + t0 * 2f);
            float a = 1f - MathF.Exp(-2f * MathF.PI * cutoff / rate);
            int i0 = (int)(t0 * rate), n = (int)(0.02f * rate);
            float z = 0f;
            for (int i = 0; i < n && i0 + i < length; i++)
            {
                float src = i < 3 ? 1f : ((float)r.NextDouble() * 2f - 1f) * 0.25f * MathF.Exp(-i / (0.004f * rate));
                z += a * (src - z);
                ir[i0 + i] += z * amp;
            }
        }
        return ir;
    }

    /// <summary>Spectrum of a signal, zero-padded to n (a power of two).</summary>
    public static (double[] Re, double[] Im) Spectrum(float[] x, int n)
    {
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < Math.Min(n, x.Length); i++) re[i] = x[i];
        Fft(re, im, false);
        return (re, im);
    }

    /// <summary>
    /// The dry source heard from distance d: absorption and ground dip on everything,
    /// plus the environment's reverb scaled up with distance.
    /// </summary>
    public static float[] Propagate((double[] Re, double[] Im) dry, (double[] Re, double[] Im) env, float d, float wetAt10, int outLength)
    {
        int n = dry.Re.Length;
        var re = new double[n];
        var im = new double[n];
        float wet = Wet(d, wetAt10);
        for (int k = 0; k <= n / 2; k++)
        {
            double f = k * (double)SoundSynth.Rate / n;
            double gain = Math.Pow(10, -AbsorptionDbPerM(f) * d / 20) * GroundDip(f, d);
            // Y = X * H * (1 + wet * E)
            double er = 1 + wet * env.Re[k], ei = wet * env.Im[k];
            double xr = dry.Re[k] * gain, xi = dry.Im[k] * gain;
            re[k] = xr * er - xi * ei;
            im[k] = xr * ei + xi * er;
            if (k > 0 && k < n / 2)
            {
                re[n - k] = re[k];
                im[n - k] = -im[k];
            }
        }
        Fft(re, im, true);
        var y = new float[outLength];
        for (int i = 0; i < outLength && i < n; i++) y[i] = (float)re[i];
        return y;
    }

    public static int Pow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>In-place iterative radix-2 FFT. Inverse includes the 1/n scaling.</summary>
    public static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            int half = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < half; k++)
                {
                    int a = i + k, b = a + half;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
        if (!inverse) return;
        for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }
}
