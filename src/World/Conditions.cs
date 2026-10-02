using Godot;

namespace Ridgeline;

public enum WeatherKind { Clear, Overcast, Rain, Fog }

/// <summary>
/// The time of day and the weather: one model the lighting, the soldiers' eyes and ears, and the sound all read.
/// - The clock starts at the hour picked for the match and runs on at <see cref="TimeScale"/> world seconds per
///   match second, so a long match goes through dusk into night and out the other side.
/// - The sun and the moon are placed by the hour at about 38° N (the Mediterranean) near the equinox-to-summer,
///   so the sun is up from about 06:00 to 18:30. North is -Z, east +X.
/// - <see cref="Lux"/> is the light on open ground: about 50 000 at midday, 400 at sunset, 3 at the end of civil
///   twilight, 0.25 under a full moon, 0.001 by starlight; clouds, rain and fog cut it. <see cref="Light"/> is the
///   same on a 0..1 scale that goes as the eye does (logarithmic).
/// - <see cref="VisibilityM"/> is the meteorological visibility: the range at which a dark object against the sky
///   has faded to 2% contrast (Koschmieder). Fog brings it down to a few hundred metres, rain to a few kilometres.
/// - <see cref="NoiseDb"/> is how much the weather raises the background noise (rain on everything).
/// The weather is fixed for the match.
/// </summary>
public static class Conditions
{
    public const float Latitude = 38f;
    /// <summary>The sun's declination for the match (degrees): late spring to late summer.</summary>
    public static float Declination { get; private set; } = 12f;

    public static double StartHour { get; private set; } = 12.0;
    /// <summary>World seconds per match second: 1 is real time; 4 takes a three-hour match through twelve hours.</summary>
    public static float TimeScale { get; private set; } = 4f;
    public static WeatherKind Weather { get; private set; } = WeatherKind.Clear;
    /// <summary>The moon's age as a fraction of its cycle: 0 new, 0.5 full, 1 new again.</summary>
    public static float MoonPhase { get; private set; } = 0.5f;

    /// <summary>Hour of the day, 0..24.</summary>
    public static double Hour { get; private set; } = 12.0;
    /// <summary>Degrees above the horizon (negative below).</summary>
    public static float SunElevation { get; private set; }
    public static float MoonElevation { get; private set; }
    /// <summary>Unit vectors pointing from the ground up at the sun and the moon.</summary>
    public static Vector3 ToSun { get; private set; } = Vector3.Up;
    public static Vector3 ToMoon { get; private set; } = Vector3.Up;
    /// <summary>How much of the moon's face is lit, 0..1.</summary>
    public static float MoonLit => (1f - MathF.Cos(MoonPhase * Mathf.Tau)) * 0.5f;

    public static float Lux { get; private set; } = 50000f;
    /// <summary>The light as the eye takes it, 0 (overcast moonless night) .. 1 (day): log10 lux from -3.5 to +3.</summary>
    public static float Light { get; private set; } = 1f;
    public static bool Dark => Lux < 1f;
    public static float VisibilityM { get; private set; } = 20000f;
    public static float NoiseDb { get; private set; }

    /// <summary>
    /// Set up for a match. <paramref name="hour"/> below 0: a random hour; <paramref name="weather"/> null: random weather
    /// (clear more often than not).
    /// </summary>
    /// <param name="moonPhase">The moon's age (0 new, 0.5 full); below 0, random.</param>
    public static void Start(double hour, WeatherKind? weather, float timeScale, int seed, float moonPhase = -1f)
    {
        var rng = new System.Random(seed);
        StartHour = hour >= 0 ? hour % 24.0 : rng.NextDouble() * 24.0;
        TimeScale = MathF.Max(0f, timeScale);
        double r = rng.NextDouble();
        Weather = weather ?? (r < 0.5 ? WeatherKind.Clear : r < 0.72 ? WeatherKind.Overcast : r < 0.88 ? WeatherKind.Rain : WeatherKind.Fog);
        MoonPhase = (float)rng.NextDouble();
        if (moonPhase >= 0f) MoonPhase = moonPhase % 1f;
        ClearLights();
        Declination = 5f + (float)rng.NextDouble() * 18f;
        // A fog's thickness varies from one morning to the next; so does rain.
        _fogVis = 180f + (float)rng.NextDouble() * 320f;
        _rainVis = 1500f + (float)rng.NextDouble() * 2500f;
        Tick(0.0);
    }

    static float _fogVis = 300f, _rainVis = 3000f;

    /// <summary>Advance to match time <paramref name="now"/> (seconds since the match began). Called by the Clock every frame.</summary>
    public static void Tick(double now)
    {
        Hour = (StartHour + now * TimeScale / 3600.0) % 24.0;
        if (Hour < 0) Hour += 24.0;
        var sun = Place(Hour);
        ToSun = sun.Dir;
        SunElevation = sun.Elev;
        // The moon trails the sun by its age: new, it's up with the sun; full, it rises as the sun sets.
        var moon = Place(Hour - MoonPhase * 24.0);
        ToMoon = moon.Dir;
        MoonElevation = moon.Elev;

        float sunLux = SunLux(SunElevation);
        float moonLux = MoonElevation > 0f ? 0.32f * MoonLit * MathF.Sin(Mathf.DegToRad(MoonElevation)) : 0f;
        const float stars = 0.0012f;
        // What the cloud lets through: the sun's light is scattered, not stopped; at night the cloud hides the moon
        // and stars all but completely.
        float cloud = Weather switch { WeatherKind.Overcast => 0.3f, WeatherKind.Rain => 0.15f, WeatherKind.Fog => 0.55f, _ => 1f };
        float cloudNight = Weather switch { WeatherKind.Overcast => 0.12f, WeatherKind.Rain => 0.08f, WeatherKind.Fog => 0.4f, _ => 1f };
        Lux = sunLux * cloud + (moonLux + stars) * cloudNight;
        Light = Mathf.Clamp((MathF.Log10(MathF.Max(Lux, 1e-6f)) + 3.5f) / 6.5f, 0f, 1f);
        VisibilityM = Weather switch { WeatherKind.Fog => _fogVis, WeatherKind.Rain => _rainVis, WeatherKind.Overcast => 15000f, _ => 20000f };
        NoiseDb = Weather == WeatherKind.Rain ? 14f : 0f;
    }

    /// <summary>
    /// Contrast left after <paramref name="dist"/> metres of air in this weather (Koschmieder): 1 close up, 0.02 at the
    /// visibility range. Multiply how easily something is made out by this.
    /// </summary>
    public static float Transmission(float dist) => MathF.Exp(-3.912f * dist / VisibilityM);

    /// <summary>A body's elevation and the direction to it, for a given hour and this match's declination.</summary>
    static (float Elev, Vector3 Dir) Place(double hour)
    {
        float lat = Mathf.DegToRad(Latitude), dec = Mathf.DegToRad(Declination);
        float h = Mathf.DegToRad((float)((hour - 12.0) * 15.0));
        float sinEl = MathF.Sin(lat) * MathF.Sin(dec) + MathF.Cos(lat) * MathF.Cos(dec) * MathF.Cos(h);
        float el = MathF.Asin(Mathf.Clamp(sinEl, -1f, 1f));
        // Azimuth from north, clockwise (east = 90°): morning east, noon south, evening west.
        float cosAz = (MathF.Sin(dec) - MathF.Sin(el) * MathF.Sin(lat)) / MathF.Max(1e-4f, MathF.Cos(el) * MathF.Cos(lat));
        float az = MathF.Acos(Mathf.Clamp(cosAz, -1f, 1f));
        if (MathF.Sin(h) > 0f) az = Mathf.Tau - az;
        float ce = MathF.Cos(el);
        var dir = new Vector3(MathF.Sin(az) * ce, MathF.Sin(el), -MathF.Cos(az) * ce).Normalized();
        return (Mathf.RadToDeg(el), dir);
    }

    /// <summary>
    /// Sunlight plus skylight on open ground by the sun's elevation (lux), through the twilights: about 400 at sunset,
    /// 3 at the end of civil twilight (-6°), 0.008 at the end of nautical (-12°), nothing from the sun past -18°.
    /// </summary>
    static float SunLux(float e)
    {
        // log10(lux) at these elevations, straight lines between.
        ReadOnlySpan<float> el = stackalloc float[] { -18f, -12f, -6f, -3f, 0f, 5f, 10f, 20f, 40f, 90f };
        ReadOnlySpan<float> lg = stackalloc float[] { -4f, -2.1f, 0.5f, 1.6f, 2.6f, 3.5f, 4.0f, 4.4f, 4.75f, 5.0f };
        if (e <= el[0]) return 0f;
        for (int i = 1; i < el.Length; i++)
            if (e <= el[i]) return MathF.Pow(10f, Mathf.Lerp(lg[i - 1], lg[i], (e - el[i - 1]) / (el[i] - el[i - 1])));
        return 100000f;
    }

    public static string Clock => $"{(int)Hour:00}:{(int)((Hour % 1.0) * 60.0):00}";

    // ---------------------------------------------------------------- light that isn't the sky's

    /// <summary>
    /// Light sources on the battlefield: an illumination flare under its parachute, a burning vehicle, a fire. Whatever
    /// makes the light registers it (and moves it, if it moves) and it lights the ground round it as 1/d²:
    /// <paramref name="candela"/> is its luminous intensity (an 81 mm illumination round about 600 000 cd; a burning
    /// car 20 000). Returns a handle for <see cref="MoveLight"/> and <see cref="RemoveLight"/>; it goes out by itself
    /// at <paramref name="until"/> (match time).
    /// </summary>
    public static int AddLight(Vector3 pos, float candela, double until)
    {
        int id = ++_nextLight;
        _lights[id] = (pos, candela, until);
        return id;
    }

    public static void MoveLight(int id, Vector3 pos)
    {
        if (_lights.TryGetValue(id, out var l)) _lights[id] = (pos, l.Candela, l.Until);
    }

    public static void RemoveLight(int id) => _lights.Remove(id);

    /// <summary>The light on the ground at <paramref name="p"/>: the sky's, plus any flare or fire near enough to matter.</summary>
    public static float LuxAt(Vector3 p)
    {
        if (_lights.Count == 0) return Lux;
        float lux = Lux;
        double now = Ridgeline.Clock.Now;
        List<int>? gone = null;
        foreach (var (id, l) in _lights)
        {
            if (now > l.Until) { (gone ??= new()).Add(id); continue; }
            float d2 = MathF.Max(1f, p.DistanceSquaredTo(l.Pos));
            lux += l.Candela / d2;
        }
        if (gone != null) foreach (int id in gone) _lights.Remove(id);
        return lux;
    }

    /// <summary><see cref="LuxAt"/> on the 0..1 scale of <see cref="Light"/>.</summary>
    public static float LightAt(Vector3 p) => Mathf.Clamp((MathF.Log10(MathF.Max(LuxAt(p), 1e-6f)) + 3.5f) / 6.5f, 0f, 1f);

    static readonly Dictionary<int, (Vector3 Pos, float Candela, double Until)> _lights = new();
    static int _nextLight;

    /// <summary>A new match: no lights left over from the last.</summary>
    public static void ClearLights() => _lights.Clear();
}
