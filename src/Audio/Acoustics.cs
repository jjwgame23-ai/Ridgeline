using System.Collections.Concurrent;
using System.Numerics;

namespace Ridgeline;

/// <summary>
/// Outdoor sound propagation, applied offline to a "dry" source recording
/// (what you'd hear a metre or two away) to get what it sounds like from
/// further off. Besides getting quieter (applied at play time), two things change a sound with distance:
///
/// 1. Air absorbs high frequencies far more than low ones (ISO 9613-1), and how much
///    depends on the map's temperature and humidity: at 1 km a rifle has lost ~100 dB at
///    8 kHz but only ~5 dB at 1 kHz. The sharp crack of a close shot turns into a dull "pop".
/// 2. The ground. The reflection off it arrives just after the direct sound. Over soft
///    ground at grazing angles it cancels part of the low-mids (the "ground dip", a few
///    hundred Hz over grass) and doubles the lowest frequencies; up close it's a faint comb.
///    Modelled physically: a spherical wave reflected off a porous ground (Miki's impedance
///    model, with the ground wave via the Faddeeva function), whose coherence with the direct
///    sound is lost to turbulence at high frequencies and long range (Clifford &amp; Lataitis).
///
/// The filter for each distance is made minimum phase (causal), as the real one is: a
/// distant shot keeps a clean onset instead of a smeared, pre-ringing one. Everything is
/// done in the frequency domain: one FFT of the dry source, then per distance a multiply
/// by the (cached) path filter and an inverse FFT.
/// </summary>
public static class Acoustics
{
    /// <summary>Air temperature (°C) and relative humidity (%) of the map, which set absorption and the speed of sound.</summary>
    public static float TempC { get; private set; } = 15f;
    public static float Humidity { get; private set; } = 65f;
    /// <summary>Effective flow resistivity of the ground, kPa·s/m² (soft forest floor ~30, grass ~200, packed earth ~2000).</summary>
    public static float GroundSigma { get; private set; } = 200f;
    /// <summary>Mean-square refractive index fluctuation of the air (turbulence): ~1e-6 calm, ~1e-5 a hot, gusty afternoon.</summary>
    public static float Turbulence { get; private set; } = 3e-6f;
    public static float SpeedOfSound { get; private set; } = 340.9f;
    /// <summary>Typical wind near the ground, m/s: how hard it blows on average on this map (SoundWorld's weather wanders round it).</summary>
    public static float WindMean { get; private set; } = 4f;
    public const float ListenerHeight = 1.7f;
    /// <summary>Outer scale of the turbulence near the ground, m (Daigle): the size of the eddies that matter.</summary>
    public const double OuterScale = 1.1;

    /// <summary>Set the air and the ground for a map; call before SoundWorld builds its sounds.</summary>
    public static void SetClimate(Biome biome)
    {
        (TempC, Humidity, GroundSigma, Turbulence, WindMean) = biome switch
        {
            Biome.Desert => (32f, 20f, 700f, 1e-5f, 4.5f),    // hot, dry air over sand: more treble loss, strong convection
            Biome.Forest => (12f, 80f, 40f, 2e-6f, 2.5f),     // damp, still, on a soft litter floor
            Biome.Highlands => (8f, 75f, 250f, 4e-6f, 6f),   // cool, windy moorland
            Biome.Urban => (17f, 55f, 800f, 4e-6f, 3f),       // packed earth, verges and paving
            _ => (15f, 65f, 200f, 3e-6f, 4f),                 // temperate grassland
        };
        // Humid air is a little faster (water vapour is lighter than air); the temperature term dominates.
        SpeedOfSound = (float)(331.3 * Math.Sqrt(1 + TempC / 273.15) * (1 + 0.0016 * VapourPercent()));
        ClearCache();
    }

    /// <summary>Molar concentration of water vapour, % (ISO 9613-1, 1 atm).</summary>
    static double VapourPercent()
    {
        double psat = Math.Pow(10, -6.8346 * Math.Pow(273.16 / (TempC + 273.15), 1.261) + 4.6151); // relative to 1 atm
        return Humidity * psat;
    }

    /// <summary>Atmospheric absorption in dB per metre at frequency f (ISO 9613-1, 1 atm).</summary>
    public static double AbsorptionDbPerM(double f)
    {
        double T = TempC + 273.15, T0 = 293.15;
        double h = VapourPercent();
        double frO = 24 + 4.04e4 * h * (0.02 + h) / (0.391 + h);
        double frN = Math.Pow(T / T0, -0.5) * (9 + 280 * h * Math.Exp(-4.170 * (Math.Pow(T / T0, -1.0 / 3) - 1)));
        double f2 = f * f;
        return 8.686 * f2 * (1.84e-11 * Math.Sqrt(T / T0)
            + Math.Pow(T / T0, -2.5) * (0.01275 * Math.Exp(-2239.1 / T) / (frO + f2 / frO)
                                        + 0.1068 * Math.Exp(-3352.0 / T) / (frN + f2 / frN)));
    }

    /// <summary>
    /// Direct plus ground-reflected sound relative to the direct sound alone, as a power ratio,
    /// at frequency f for a source at height hs and a listener at hr, d apart.
    /// </summary>
    public static double GroundPower(double f, double d, double hs, double hr)
    {
        if (f < 1) return 4.0;
        double r1 = Math.Sqrt(d * d + (hs - hr) * (hs - hr));
        double r2 = Math.Sqrt(d * d + (hs + hr) * (hs + hr));
        double k = 2 * Math.PI * f / SpeedOfSound;
        double sinT = (hs + hr) / r2; // sine of the grazing angle
        // Miki's normalised impedance of a porous ground (time convention e^{-iωt}).
        double x = Math.Pow(f / GroundSigma, -0.632);
        var beta = 1.0 / new Complex(1 + 5.50 * x, 8.43 * x);
        var rp = (sinT - beta) / (sinT + beta);               // plane-wave reflection coefficient
        var w = Complex.Sqrt(new Complex(0, 0.5 * k * r2)) * (sinT + beta); // numerical distance
        var bl = 1 + new Complex(0, Math.Sqrt(Math.PI)) * w * Faddeeva(w); // boundary loss factor F(w)
        var q = rp + (1 - rp) * bl;                           // spherical-wave reflection coefficient
        double a = q.Magnitude * r1 / r2;
        double coh = Coherence(k, d, hs, hr);
        return Math.Max(1e-4, 1 + a * a + 2 * a * coh * Math.Cos(k * (r2 - r1) + q.Phase));
    }

    /// <summary>
    /// How coherent the ground reflection stays with the direct sound through turbulent air
    /// (Clifford &amp; Lataitis): the two paths see different eddies, so their phase difference
    /// wanders — a little up close and at low frequencies, completely far off at high ones.
    /// </summary>
    static double Coherence(double k, double d, double hs, double hr)
    {
        const double L0 = OuterScale;
        double h = 2 * hs * hr / (hs + hr); // greatest transverse separation of the two paths
        double rho = h < 1e-3 ? 1.0 : 0.5 * Math.Sqrt(Math.PI) * L0 / h * Erf(h / L0);
        double a = d > k * L0 * L0 ? 0.5 : 1.0;
        double s2 = a * Math.Sqrt(Math.PI) * Turbulence * k * k * d * L0;
        return Math.Exp(-s2 * (1 - rho));
    }

    static double Erf(double x)
    {
        // Abramowitz & Stegun 7.1.26, |error| < 1.5e-7.
        double t = 1 / (1 + 0.3275911 * Math.Abs(x));
        double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return x >= 0 ? y : -y;
    }

    /// <summary>
    /// The Faddeeva function w(z) = exp(-z²) erfc(-iz), by Humlíček's W4 rational
    /// approximations (upper half plane, relative error ~1e-4), reflected for Im z &lt; 0.
    /// </summary>
    public static Complex Faddeeva(Complex z)
    {
        if (z.Imaginary < 0) return 2 * Complex.Exp(-z * z) - Faddeeva(-z);
        double x = z.Real, y = z.Imaginary;
        var t = new Complex(y, -x); // -iz
        double s = Math.Abs(x) + y;
        if (s >= 15)
            return t * 0.5641896 / (0.5 + t * t);
        if (s >= 5.5)
        {
            var u = t * t;
            return t * (1.410474 + u * 0.5641896) / (0.75 + u * (3.0 + u));
        }
        if (y >= 0.195 * Math.Abs(x) - 0.176)
            return (16.4955 + t * (20.20933 + t * (11.96482 + t * (3.778987 + t * 0.5642236))))
                 / (16.4955 + t * (38.82363 + t * (39.27121 + t * (21.69274 + t * (6.699398 + t)))));
        {
            var u = t * t;
            var num = t * (36183.31 - u * (3321.9905 - u * (1540.787 - u * (219.0313 - u * (35.76683 - u * (1.320522 - u * 0.56419))))));
            var den = 32066.6 - u * (24322.84 - u * (9022.228 - u * (2186.181 - u * (364.2191 - u * (61.57037 - u * (1.841439 - u))))));
            return Complex.Exp(u) - num / den;
        }
    }

    /// <summary>Length of a path filter's impulse response: long enough for the ground reflection and the air's ringing.</summary>
    public const int FilterLength = 16384;

    static readonly ConcurrentDictionary<(float D, float Hs), Lazy<double[]>> _irs = new();
    static readonly ConcurrentDictionary<(int N, float D, float Hs), Lazy<(double[] Re, double[] Im)>> _paths = new();

    /// <summary>Drop the cached path filters (large; only needed while sounds are being built).</summary>
    public static void ClearCache()
    {
        _irs.Clear();
        _paths.Clear();
    }

    /// <summary>
    /// The impulse response of the path to distance d from a source at height hs: air absorption
    /// times the ground effect, made minimum phase. Cached; independent of what it's applied to.
    /// </summary>
    static double[] PathIr(float d, float hs) => _irs.GetOrAdd((d, hs), key => new Lazy<double[]>(() =>
    {
        const int n = FilterLength;
        var ground = GroundCurve(key.D, key.Hs);
        var mag = new double[n / 2 + 1];
        for (int k = 0; k <= n / 2; k++)
        {
            double f = k * (double)SoundSynth.Rate / n;
            double air = Math.Pow(10, -AbsorptionDbPerM(f) * key.D / 20);
            // The ground curve is on a log-frequency grid; interpolate its dB.
            double g = Math.Clamp((Math.Log(Math.Max(f, GridLo)) - Math.Log(GridLo)) / GridStep, 0, GridN - 1.001);
            int i = (int)g;
            double db = ground[i] + (ground[i + 1] - ground[i]) * (g - i);
            mag[k] = air * Math.Pow(10, db / 20);
        }
        var (re, im) = MinPhase(mag, n);
        Fft(re, im, true);
        return re;
    })).Value;

    /// <summary>The path filter as a spectrum of n bins (n at least FilterLength). Cached.</summary>
    static (double[] Re, double[] Im) Path(int n, float d, float hs) => _paths.GetOrAdd((n, d, hs), key => new Lazy<(double[] Re, double[] Im)>(() =>
    {
        var ir = PathIr(key.D, key.Hs);
        var re = new double[key.N];
        var im = new double[key.N];
        Array.Copy(ir, re, Math.Min(ir.Length, key.N));
        Fft(re, im, false);
        return (re, im);
    })).Value;

    /// <summary>The frequency at which the air has taken lossDb off by distance d.</summary>
    public static double FrequencyAtLoss(double d, double lossDb)
    {
        double lo = 50, hi = 22050;
        for (int it = 0; it < 30; it++)
        {
            double mid = Math.Sqrt(lo * hi);
            if (AbsorptionDbPerM(mid) * d > lossDb) hi = mid; else lo = mid;
        }
        return lo;
    }

    /// <summary>
    /// The highest frequency the air leaves within 70 dB at distance d: above it a band holds
    /// nothing audible, so it can be stored at a lower sample rate.
    /// </summary>
    public static double TopFrequency(float d) => FrequencyAtLoss(d, 70);

    const int GridN = 600;
    const double GridLo = 10, GridHi = 22050;
    static readonly double GridStep = Math.Log(GridHi / GridLo) / (GridN - 1);

    /// <summary>
    /// The ground effect in dB on a log-frequency grid, averaged over a spread of source and
    /// listener heights: real ground is uneven and nobody stands at exactly 1.7 m, which blurs
    /// the close-up combs to a gentle colouring while the long-range dip stays.
    /// </summary>
    static double[] GroundCurve(float d, float hs)
    {
        var spread = new[] { 0.6, 0.85, 1.15, 1.8 };
        var c = new double[GridN];
        for (int i = 0; i < GridN; i++)
        {
            double f = GridLo * Math.Exp(i * GridStep), p = 0;
            foreach (var a in spread)
                foreach (var b in spread)
                    p += GroundPower(f, d, hs * a, ListenerHeight * b);
            c[i] = 10 * Math.Log10(p / (spread.Length * spread.Length));
        }
        return c;
    }

    /// <summary>
    /// A minimum-phase spectrum (all n bins) with the given magnitude (bins 0..n/2), by the
    /// folded real cepstrum: the causal filter with that response and the sharpest onset.
    /// </summary>
    static (double[] Re, double[] Im) MinPhase(double[] mag, int n)
    {
        var re = new double[n];
        var im = new double[n];
        for (int k = 0; k <= n / 2; k++)
        {
            double l = Math.Log(Math.Max(mag[k], 1e-7)); // floor at -140 dB, where far bands' highs have gone
            re[k] = l;
            if (k > 0 && k < n / 2) re[n - k] = l;
        }
        Fft(re, im, true);
        for (int i = 1; i < n / 2; i++) re[i] *= 2;
        for (int i = n / 2 + 1; i < n; i++) re[i] = 0;
        Array.Clear(im);
        Fft(re, im, false);
        for (int k = 0; k < n; k++)
        {
            double e = Math.Exp(re[k]);
            (re[k], im[k]) = (e * Math.Cos(im[k]), e * Math.Sin(im[k]));
        }
        return (re, im);
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

    /// <summary>The dry source heard from distance d (spreading loss aside): the path filter applied.</summary>
    public static float[] Propagate((double[] Re, double[] Im) dry, float d, float hs, int outLength)
    {
        int n = dry.Re.Length;
        var h = Path(n, d, hs);
        var re = new double[n];
        var im = new double[n];
        for (int k = 0; k < n; k++)
        {
            re[k] = dry.Re[k] * h.Re[k] - dry.Im[k] * h.Im[k];
            im[k] = dry.Re[k] * h.Im[k] + dry.Im[k] * h.Re[k];
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
