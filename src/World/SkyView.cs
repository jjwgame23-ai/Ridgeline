using Godot;

namespace Ridgeline;

/// <summary>
/// The sky and the light you see by, driven every frame from <see cref="Conditions"/>.
/// - Light is split the way it reaches the ground: the sun's (or the moon's) direct beam, which casts shadows,
///   and the diffuse light of the sky. On a clear day the sky gives ~13% of the light with the sun high and all
///   of it once the sun is down; cloud and rain take the beam away altogether (flat light, no shadows); fog lets
///   a little of it through the layer.
/// - How bright the scene looks is set by the eye's adaptation, not the lux: the eye adapts over eight decades
///   of light but not fully, so a moonlit field looks dim and a starlit one very dark (after Ferwerda et al.
///   1996, "A model of visual adaptation for realistic image synthesis"). <see cref="Level"/> is that curve, and
///   every light here is scaled by it, the tonemapper's exposure left alone: so a muzzle flash, a flare or a
///   tracer (their own fixed brightness) stands out of the night as it does to a dark-adapted eye.
/// - The sky's colour follows the sun: blue by day, gold low down, red at sunset with the glow on the sun's
///   side, deep blue through the twilights, then near-black with the horizon a touch lighter (airglow is
///   brightest ~10° up: the van Rhijn effect). Cloud greys it (CIE overcast sky: brightest overhead).
/// - At night the moon (its phase lit from where the sun really is) and the stars, rotating about the pole at
///   the map's latitude; how faint a star you can see goes with how bright the sky is.
/// - Fog density from the visibility (Koschmieder), rain falling round the camera.
/// Each biome's daytime look is the base; the rest is worked out from it.
/// </summary>
public partial class SkyView : Node
{
    public Biome Biome = Biome.Temperate;

    /// <summary>
    /// Display brightness per lux at the eye's present adaptation (open ground at noon is ~1 at ~75 000 lux).
    /// Anything that lights the ground itself (a flare, a fire) can use it to put its light on the same scale:
    /// an OmniLight's energy ~ <see cref="SunEnergyPerLux"/> × lux it gives at a metre.
    /// </summary>
    public static float EyeGain { get; private set; } = 1f / 75000f;
    /// <summary>Godot light energy per lux on the ground, at the present adaptation (the sun's calibration × EyeGain).</summary>
    public static float SunEnergyPerLux { get; private set; } = 1.3f / 75000f;

    /// <summary>
    /// The rain rate (mm/h) that goes with this visibility: extinction in rain σ ≈ 0.25 R^0.63 per km (Atlas 1953),
    /// so 1.5 km is a downpour (~40 mm/h) and 4 km moderate rain (~9 mm/h). 0 when it isn't raining.
    /// </summary>
    public static float RainRate => Conditions.Weather == WeatherKind.Rain
        ? Mathf.Clamp(MathF.Pow(3.912f / (0.25f * Conditions.VisibilityM / 1000f), 1f / 0.63f), 0.5f, 80f) : 0f;

    Godot.Environment _env = null!;
    ShaderMaterial _sky = null!;
    DirectionalLight3D _sun = null!, _moon = null!;
    MeshInstance3D? _rain;
    ShaderMaterial? _rainMat;
    Vector3 _dayTop, _dayHor, _haze, _sunTint;
    float _hazeDensity, _k;
    double _nextSky = double.MinValue;
    float _pushedS = -1f, _pushedUp = 1f;
    // What was last pushed to the sky, for the per-frame ambient and fog.
    Vector3 _zen, _hor, _glow;
    float _s = 1f;

    /// <summary>
    /// Light that isn't in the renderer: what bounces off the ground and everything round (albedo ~0.25), which
    /// with no global illumination has to come in with the ambient.
    /// </summary>
    const float Bounce = 0.25f;
    /// <summary>The depth of a radiation fog (typically 50-200 m): what the beam has to get through.</summary>
    const float FogDepth = 100f;

    public override void _Ready()
    {
        bool desert = Biome == Biome.Desert, urban = Biome == Biome.Urban, high = Biome == Biome.Highlands;
        // The daytime look of each biome, as it was: desert haze warm and a little thicker, the city's air greyer,
        // the highlands' clear.
        _dayTop = desert ? new(0.36f, 0.5f, 0.68f) : new(0.32f, 0.45f, 0.62f);
        _dayHor = desert ? new(0.86f, 0.8f, 0.68f) : urban ? new(0.7f, 0.72f, 0.74f) : new(0.72f, 0.76f, 0.8f);
        _haze = desert ? new(0.84f, 0.77f, 0.64f) : urban ? new(0.66f, 0.68f, 0.7f) : new(0.68f, 0.73f, 0.79f);
        _hazeDensity = desert ? 0.00042f : urban ? 0.0004f : high ? 0.00026f : 0.00035f;
        _k = desert ? 1.55f : 1.3f;
        _sunTint = desert ? new(1f, 0.93f, 0.82f) : new(1f, 0.96f, 0.9f);

        _sky = new ShaderMaterial { Shader = new Shader { Code = SkyShader } };
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            // The sky is redrawn into the ambient/reflection map about once a second (it changes slowly), spread
            // over frames; 128 is plenty for light this soft.
            Sky = new Sky { SkyMaterial = _sky, ProcessMode = Sky.ProcessModeEnum.Incremental, RadianceSize = Sky.RadianceSizeEnum.Size128 },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            FogEnabled = true,
            FogAerialPerspective = 0.6f,
            FogSkyAffect = 0.3f,
            SsaoEnabled = true,
            GlowEnabled = true,
        };
        AddChild(new WorldEnvironment { Environment = _env });

        float shadowFar = Settings.Shadows >= 2 ? 250f : 90f;
        // The sky shader places the sun and moon itself, so the lights don't feed it (and moving them doesn't
        // make the sky redraw).
        _sun = new DirectionalLight3D { ShadowEnabled = Settings.Shadows > 0, DirectionalShadowMaxDistance = shadowFar, SkyMode = DirectionalLight3D.SkyModeEnum.LightOnly };
        // Moonlight casts shadows as sharp as the sun's. It only shines when the sun doesn't, so there is never
        // more than one shadowed light.
        _moon = new DirectionalLight3D { ShadowEnabled = Settings.Shadows > 0, DirectionalShadowMaxDistance = shadowFar, SkyMode = DirectionalLight3D.SkyModeEnum.LightOnly, Visible = false };
        AddChild(_sun);
        AddChild(_moon);
        if (Conditions.Weather == WeatherKind.Rain) BuildRain();
        Update();
        GD.Print($"conditions: {Conditions.Clock} {Conditions.Weather}, sun {Conditions.SunElevation:0.0}°, moon {Conditions.MoonLit * 100f:0}% lit at {Conditions.MoonElevation:0.0}°, "
                 + $"{Conditions.Lux:0.####} lux, visibility {Conditions.VisibilityM:0} m{(RainRate > 0f ? $", rain {RainRate:0.0} mm/h" : "")}, x{Conditions.TimeScale:0.#} clock");
    }

    public override void _Process(double delta)
    {
        using var _p = Prof.Time("sky");
        Update();
    }

    // ---------------------------------------------------------------- light

    /// <summary>
    /// How bright open ground looks to an eye adapted to it, 0..1 (1: noon): nearly flat through daylight
    /// (brightness goes as about lux^0.12 there), steeper through twilight, and at the bottom the rods
    /// (scotopic vision) holding on. Full moon ~0.11, starlight ~0.04, an overcast moonless night ~0.02.
    /// Points: log10 lux → log10 brightness, straight lines between.
    /// </summary>
    public static float Level(float lux)
    {
        ReadOnlySpan<float> lx = stackalloc float[] { -5f, -2.92f, -0.6f, 2f, 4.875f };
        ReadOnlySpan<float> lb = stackalloc float[] { -1.921f, -1.398f, -0.959f, -0.347f, 0f };
        float l = MathF.Log10(MathF.Max(lux, 1e-7f));
        if (l <= lx[0]) return MathF.Pow(10f, lb[0]);
        for (int i = 1; i < lx.Length; i++)
            if (l <= lx[i]) return MathF.Pow(10f, Mathf.Lerp(lb[i - 1], lb[i], (l - lx[i - 1]) / (lx[i] - lx[i - 1])));
        return 1f;
    }

    /// <summary>What of a sun or moon's beam gets through the weather to the ground.</summary>
    static float Beam(float sinEl) => Conditions.Weather switch
    {
        WeatherKind.Clear => 1f,
        WeatherKind.Fog => MathF.Exp(-3.912f * FogDepth / (Conditions.VisibilityM * MathF.Max(sinEl, 0.05f))),
        _ => 0f, // a cloud deck: the disc is gone, all the light comes through as diffuse
    };

    /// <summary>
    /// Air mass along a line of sight at this elevation (Kasten &amp; Young 1989): 1 overhead, ~38 at the horizon.
    /// </summary>
    static float AirMass(float elDeg)
    {
        float e = MathF.Max(elDeg, -0.5f);
        return 1f / (MathF.Sin(Mathf.DegToRad(e)) + 0.50572f * MathF.Pow(e + 6.07995f, -1.6364f));
    }

    /// <summary>
    /// The colour of sunlight (or moonlight) after this much air: Rayleigh scattering plus a little haze at red,
    /// green and blue (vertical optical depths ~0.14 / 0.20 / 0.34), so a low sun is gold and a setting one red.
    /// Relative to the sun at 60°, brightest channel 1.
    /// </summary>
    static Vector3 Extinct(float elDeg)
    {
        float m = AirMass(elDeg), m0 = AirMass(60f);
        var c = new Vector3(MathF.Exp(-0.14f * (m - m0)), MathF.Exp(-0.20f * (m - m0)), MathF.Exp(-0.34f * (m - m0)));
        return c / MathF.Max(c.X, MathF.Max(c.Y, c.Z));
    }

    static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    static Color C(Vector3 v) => new(v.X, v.Y, v.Z);

    void Update()
    {
        float lux = MathF.Max(Conditions.Lux, 1e-6f);
        float el = Conditions.SunElevation, mel = Conditions.MoonElevation;
        float sinS = MathF.Sin(Mathf.DegToRad(el)), sinM = MathF.Sin(Mathf.DegToRad(mel));
        var w = Conditions.Weather;

        // The direct beam's share. Clear sky: the diffuse fraction of daylight by the sun's elevation (~0.13 high
        // up, rising to all of it at the horizon; after the IES clear-sky tables).
        float sunDirect = 0f;
        if (el > 0f)
        {
            float diffuse = Mathf.Clamp(0.13f + 0.87f * MathF.Exp(-el / 4.5f), 0f, 1f);
            sunDirect = lux * (1f - diffuse) * Beam(sinS);
        }
        // The moon: 0.32 lux full and overhead (as in Conditions), ~85% of it the direct beam.
        float moonDirect = mel > 0f ? MathF.Min(lux - sunDirect, 0.32f * Conditions.MoonLit * sinM * 0.85f * Beam(sinM)) : 0f;
        float fSun = sunDirect / lux, fMoon = MathF.Max(0f, moonDirect) / lux, fDiff = MathF.Max(0f, 1f - fSun - fMoon);

        float b = Level(lux);
        EyeGain = b / lux;
        SunEnergyPerLux = _k * EyeGain;

        // Energies: the beam's is per unit area square to it (the renderer takes the cosine), the ambient's is
        // what lands on open ground.
        float sunE = _k * b * fSun / MathF.Max(sinS, 0.05f);
        float moonE = _k * b * fMoon / MathF.Max(sinM, 0.05f);
        float ambient = _k * b * (fDiff + Bounce);

        _sun.Visible = sunE > 1e-4f;
        if (_sun.Visible)
        {
            _sun.Basis = Basis.LookingAt(-Conditions.ToSun, MathF.Abs(Conditions.ToSun.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
            _sun.LightEnergy = sunE;
            _sun.LightColor = C(Norm(Extinct(el) * _sunTint));
        }
        _moon.Visible = !_sun.Visible && moonE > 1e-4f;
        if (_moon.Visible)
        {
            _moon.Basis = Basis.LookingAt(-Conditions.ToMoon, MathF.Abs(Conditions.ToMoon.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
            _moon.LightEnergy = moonE;
            // Moonlight is sunlight, a shade redder, but it's seen by the rods, which make it look blue (the
            // Purkinje shift; rendered so after Jensen et al. 2001, "A physically-based night sky model").
            _moon.LightColor = C(Norm(Extinct(mel) * new Vector3(0.62f, 0.74f, 1f)));
        }

        // The sky's brightness beside the ground's: a sky that is the only source (overcast, twilight) is bright
        // against the ground it lights; a clear sky beside the sun is dimmer; a moonless night sky dimmer still.
        float rDiff = Mathf.Lerp(0.55f, 1.4f, Mathf.SmoothStep(-2f, 1f, MathF.Log10(lux)));
        float r = rDiff * fDiff + 0.62f * (fSun + fMoon);
        float s = r * b / 0.72f; // 1: the biome's daytime sky, as it always looked

        double now = Clock.Now;
        if (now >= _nextSky || MathF.Abs(s - _pushedS) > 0.1f * MathF.Max(_pushedS, 0.01f))
        {
            _nextSky = now + 1.0;
            _s = s;
            PushSky(lux, el, mel, sinM, fDiff);
        }

        // The ambient: the sky map's radiance times this energy, so that what reaches open ground is `ambient`.
        _env.AmbientLightEnergy = ambient / MathF.Max(_pushedUp, 1e-5f);

        // Fog: Godot's exponential fog leaves exp(-density·d) of the contrast, so density = 3.912 / visibility
        // (Koschmieder). A clear day keeps the biome's own haze (dust, city air), about 9-15 km. Capped at ~130 m
        // visibility.
        _env.FogDensity = MathF.Min(0.03f, MathF.Max(_hazeDensity, 3.912f / Conditions.VisibilityM));
        var hazeHue = w switch
        {
            WeatherKind.Clear => _haze * (_hor / MathF.Max(_s, 1e-5f)) / _dayHor,
            WeatherKind.Fog => new Vector3(0.72f, 0.73f, 0.74f),
            WeatherKind.Rain => new Vector3(0.46f, 0.48f, 0.5f),
            _ => new Vector3(0.64f, 0.66f, 0.68f),
        };
        _env.FogLightColor = C(hazeHue);
        _env.FogLightEnergy = _s;
        // Fog is its own colour, not the sky's; a cloud deck and rain hide the sky behind a veil.
        _env.FogAerialPerspective = w switch { WeatherKind.Fog => 0.1f, WeatherKind.Rain => 0.35f, _ => 0.6f };
        _env.FogSkyAffect = w switch { WeatherKind.Fog => 1f, WeatherKind.Rain => 0.7f, WeatherKind.Overcast => 0.45f, _ => 0.3f };
        // The glow of the sun through fog: its light scattered forward by the droplets.
        _env.FogSunScatter = w == WeatherKind.Fog && _sun.Visible ? 0.2f : 0f;

        UpdateRain();
    }

    static Vector3 Norm(Vector3 c) => c / MathF.Max(1e-6f, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));

    // ---------------------------------------------------------------- the sky

    static readonly StringName PZenith = "zenith", PHorizon = "horizon", PGroundHor = "ground_hor", PGroundBot = "ground_bot", PGlow = "glow",
        PSunDir = "sun_dir", PSunDisk = "sun_disk", PSunHalo = "sun_halo", PHaloPow = "halo_power", PMoonDir = "moon_dir", PMoonDisk = "moon_disk",
        PStarLim = "star_lim", PStarGain = "star_gain", PStarX = "star_x", PStarY = "star_y", PStarZ = "star_z", PPx = "px";

    // Sky hues by the sun's elevation (at the brightness of the daytime sky; the level is applied on top):
    // golden hour, sunset, civil and nautical twilight, then night.
    static readonly float[] KeyEl = { -15f, -9f, -4f, 0f, 4f, 12f };
    static readonly Vector3[] KeyZen = { default, new(0.09f, 0.13f, 0.32f), new(0.14f, 0.20f, 0.42f), new(0.20f, 0.28f, 0.52f), new(0.26f, 0.38f, 0.60f), default };
    static readonly Vector3[] KeyHor = { default, new(0.24f, 0.26f, 0.38f), new(0.46f, 0.38f, 0.46f), new(0.72f, 0.52f, 0.36f), new(0.80f, 0.72f, 0.60f), default };
    static readonly Vector3[] KeyGlow = { default, new(0.30f, 0.14f, 0.07f), new(0.7f, 0.28f, 0.08f), new(0.75f, 0.32f, 0.08f), new(0.45f, 0.26f, 0.10f), default };

    void PushSky(float lux, float el, float mel, float sinM, float fDiff)
    {
        var w = Conditions.Weather;
        // Night: a moonlit sky is the day's blue, dimmed (the same Rayleigh scattering); a moonless one the grey
        // of airglow and starlight.
        float moonClear = mel > 0f ? 0.32f * Conditions.MoonLit * sinM : 0f;
        float mt = moonClear / (moonClear + 0.004f);
        var nightZen = Lerp(new Vector3(0.16f, 0.18f, 0.26f), new Vector3(0.20f, 0.28f, 0.45f), mt);
        var nightHor = Lerp(new Vector3(0.34f, 0.35f, 0.40f), new Vector3(0.36f, 0.42f, 0.52f), mt);
        Vector3 zen, hor, glow;
        if (el >= KeyEl[^1]) { zen = _dayTop; hor = _dayHor; glow = default; }
        else if (el <= KeyEl[0]) { zen = nightZen; hor = nightHor; glow = default; }
        else
        {
            int i = 1;
            while (el > KeyEl[i]) i++;
            float t = (el - KeyEl[i - 1]) / (KeyEl[i] - KeyEl[i - 1]);
            Vector3 Z(int k) => k == 0 ? nightZen : k == KeyEl.Length - 1 ? _dayTop : KeyZen[k];
            Vector3 H(int k) => k == 0 ? nightHor : k == KeyEl.Length - 1 ? _dayHor : KeyHor[k];
            zen = Lerp(Z(i - 1), Z(i), t);
            hor = Lerp(H(i - 1), H(i), t);
            glow = Lerp(KeyGlow[i - 1], KeyGlow[i], t);
        }
        // Cloud: grey, brightest overhead (the CIE overcast sky is three times brighter at the zenith than at the
        // horizon; haze in front of the horizon evens that out a little). The glow of sunset only just lights the
        // cloud base.
        switch (w)
        {
            case WeatherKind.Overcast: zen = new(0.74f, 0.76f, 0.78f); hor = new(0.56f, 0.58f, 0.6f); glow *= 0.15f; break;
            // Nimbostratus is thick: a darker grey than a thin overcast.
            case WeatherKind.Rain: zen = new(0.4f, 0.42f, 0.45f); hor = new(0.36f, 0.38f, 0.41f); glow *= 0.05f; break;
            case WeatherKind.Fog: zen = hor = new(0.66f, 0.67f, 0.68f); glow *= 0.1f; break;
        }
        float s = _s;
        _zen = zen * s;
        _hor = hor * s;
        _glow = glow * s;
        _pushedS = s;
        // What an upward face sees of the sky (for the ambient's energy).
        _pushedUp = Lum((_zen + _hor) * 0.5f);

        _sky.SetShaderParameter(PZenith, _zen);
        _sky.SetShaderParameter(PHorizon, _hor);
        _sky.SetShaderParameter(PGlow, _glow);
        // Below the horizon (seen past the map's edge, and what lights faces turned down): a shade under the sky's
        // horizon, so it doesn't stand out as a bright band at night.
        _sky.SetShaderParameter(PGroundHor, _hor * 0.8f);
        _sky.SetShaderParameter(PGroundBot, _hor * 0.4f);

        // The sun's disc and the haze round it (the aureole: forward scattering off haze, stronger with more air in
        // the way). Through fog the disc is a pale ring of light; behind cloud it's gone.
        var sunCol = Norm(Extinct(el) * _sunTint);
        float sinS = MathF.Sin(Mathf.DegToRad(el));
        float beam = Beam(sinS);
        _sky.SetShaderParameter(PSunDir, Conditions.ToSun);
        _sky.SetShaderParameter(PSunDisk, el > -1f ? sunCol * 30f * beam : Vector3.Zero);
        float halo = w switch { WeatherKind.Clear => 0.25f * (1f + 2f * (1f - MathF.Max(sinS, 0f))), WeatherKind.Fog => 0.5f, _ => 0f };
        _sky.SetShaderParameter(PSunHalo, el > -3f ? sunCol * halo * s * Mathf.SmoothStep(-3f, 0f, el) : Vector3.Zero);
        _sky.SetShaderParameter(PHaloPow, w == WeatherKind.Fog ? 6f : 40f);

        // The moon's disc: its face ~2500 cd/m² (full), which at night is far past white (it glows); by day it's a
        // pale shape on the blue. Dimmed and reddened low down.
        float moonLumDisplay = MathF.Min(6f, _k * MathF.PI * EyeGain * 2500f);
        var moonCol = Norm(Extinct(mel) * new Vector3(1f, 0.96f, 0.9f)) * MathF.Exp(-0.07f * (AirMass(mel) - 1f));
        _sky.SetShaderParameter(PMoonDir, Conditions.ToMoon);
        _sky.SetShaderParameter(PMoonDisk, mel > -1f ? moonCol * moonLumDisplay * Beam(sinM) : Vector3.Zero);

        // Stars: the faintest you can see goes with the sky's brightness (Crumey 2014, roughly): magnitude 6.5 on
        // a moonless night, ~4.4 under a full moon, only the brightest dozen or so at the end of civil twilight.
        float skyLux = lux * fDiff;
        float lim = w == WeatherKind.Clear ? 6.5f - 1.4f * MathF.Log10(MathF.Max(1f, skyLux / 0.0012f)) : -9f;
        _sky.SetShaderParameter(PStarLim, lim);
        // A star at the limit is only just there against the sky; each magnitude brighter stands out more.
        _sky.SetShaderParameter(PStarGain, 0.004f + Lum(_zen) * 0.15f);
        // They turn about the celestial pole, up at the latitude due north (-Z), once a sidereal day.
        float lat = Mathf.DegToRad(Conditions.Latitude);
        var pole = new Vector3(0f, MathF.Sin(lat), -MathF.Cos(lat));
        var rot = new Basis(pole, Mathf.DegToRad((float)(Conditions.Hour * 15.041)));
        _sky.SetShaderParameter(PStarX, rot.Row0);
        _sky.SetShaderParameter(PStarY, rot.Row1);
        _sky.SetShaderParameter(PStarZ, rot.Row2);
        // A pixel's angle, so a star is a point and the discs' edges are smooth whatever the zoom.
        float px = 0.0014f;
        if (GetViewport().GetCamera3D() is { } cam)
            px = 2f * MathF.Tan(Mathf.DegToRad(cam.Fov) * 0.5f) / MathF.Max(1f, GetViewport().GetVisibleRect().Size.Y);
        _sky.SetShaderParameter(PPx, px);
        _rainMat?.SetShaderParameter(PPx, px);
    }

    static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    const string SkyShader = @"
shader_type sky;

uniform vec3 zenith;
uniform vec3 horizon;
uniform vec3 ground_hor;
uniform vec3 ground_bot;
uniform vec3 glow;
uniform vec3 sun_dir = vec3(0.0, 1.0, 0.0);
uniform vec3 sun_disk;
uniform vec3 sun_halo;
uniform float halo_power = 40.0;
uniform vec3 moon_dir = vec3(0.0, -1.0, 0.0);
uniform vec3 moon_disk;
uniform float star_lim = -9.0;
uniform float star_gain = 0.0;
uniform vec3 star_x = vec3(1.0, 0.0, 0.0);
uniform vec3 star_y = vec3(0.0, 1.0, 0.0);
uniform vec3 star_z = vec3(0.0, 0.0, 1.0);
uniform float px = 0.0014;

// Angular radii of the sun and the moon (rad).
const float SUN_R = 0.00465;
const float MOON_R = 0.00452;

float hash13(vec3 p3) {
    p3 = fract(p3 * 0.1031);
    p3 += dot(p3, p3.zyx + 31.32);
    return fract((p3.x + p3.y) * p3.z);
}

vec3 hash33(vec3 p3) {
    p3 = fract(p3 * vec3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yxz + 33.33);
    return fract((p3.xxy + p3.yxx) * p3.zyx);
}

// Stars: one chance in ~45 of a star in each cell of a grid on the celestial sphere (~6000 over the whole
// sky, as many as the naked eye has to magnitude 6.5), with magnitudes as the real sky has them: about three
// times as many for each magnitude fainter.
vec3 stars(vec3 d, float y) {
    vec3 c = vec3(dot(star_x, d), dot(star_y, d), dot(star_z, d));
    vec3 p = c * 110.0;
    vec3 base = floor(p - 0.5);
    float rad = max(px, 0.0004) * 0.9;
    vec3 acc = vec3(0.0);
    for (int i = 0; i < 8; i++) {
        vec3 cell = base + vec3(float(i & 1), float((i >> 1) & 1), float((i >> 2) & 1));
        float h = hash13(cell);
        if (h > 0.022) continue;
        vec3 j = hash33(cell);
        vec3 sd = normalize(cell + 0.2 + 0.6 * j);
        vec3 e = c * 1.0 - sd;
        float a2 = dot(e, e);
        if (a2 > 9.0 * rad * rad) continue;
        float m = 6.5 + 2.0 * log(max(h / 0.022, 1e-4)) / log(10.0);
        float vis = smoothstep(star_lim + 0.4, star_lim - 0.4, m);
        if (vis <= 0.0) continue;
        float b = star_gain * pow(10.0, 0.25 * (star_lim - m)) * vis;
        // The bright ones show their colour (blue-white to orange); faint ones, seen by the rods, are white.
        vec3 tint = mix(vec3(1.0), mix(vec3(0.72, 0.84, 1.0), vec3(1.0, 0.78, 0.55), j.x), clamp((star_lim - m - 1.5) / 3.0, 0.0, 1.0));
        acc += tint * b * exp(-a2 / (rad * rad));
    }
    // Extinction on the way down through the air: ~0.25 magnitude per air mass.
    float X = 1.0 / max(y, 0.035);
    return acc * pow(10.0, -0.1 * (X - 1.0));
}

// Where on a body's disc this direction falls (unit disc), in the disc's own right/up frame.
vec2 disc_uv(vec3 d, vec3 dir, float r, out vec3 right, out vec3 up) {
    right = normalize(cross(dir, abs(dir.y) > 0.99 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0)));
    up = cross(right, dir);
    vec3 off = d - dir * dot(d, dir);
    return vec2(dot(off, right), dot(off, up)) / r;
}

void sky() {
    vec3 d = EYEDIR;
    float y = d.y;
    vec3 col;
    if (y >= 0.0) {
        float c = 1.0 - acos(clamp(y, 0.0, 1.0)) / (PI * 0.5);
        col = mix(horizon, zenith, clamp(1.0 - pow(1.0 - c, 1.0 / 0.15), 0.0, 1.0));
        // The glow low on the sun's side at sunrise and sunset.
        vec2 dh = normalize(d.xz + vec2(1e-5));
        vec2 sh = normalize(sun_dir.xz + vec2(1e-5));
        float toward = 0.5 + 0.5 * dot(dh, sh);
        col += glow * exp(-y / 0.09) * toward * toward * toward;
    } else {
        float c = acos(clamp(y, -1.0, 0.0)) / (PI * 0.5) - 1.0;
        col = mix(ground_hor, ground_bot, clamp(1.0 - pow(1.0 - c, 1.0 / 0.02), 0.0, 1.0));
    }
    float cs = max(dot(d, sun_dir), 0.0);
    col += sun_halo * (pow(cs, halo_power) + 0.12 * pow(cs, 6.0));

    if (!AT_CUBEMAP_PASS && y > -0.01) {
        vec3 st = vec3(0.0);
        if (star_lim > -1.5 && y > 0.0) st = stars(d, y);
        // The moon, lit on the side the sun is: its phase. The dark part hides the stars behind it.
        if (dot(d, moon_dir) > 0.9998) {
            vec3 right, up;
            vec2 uv = disc_uv(d, moon_dir, MOON_R, right, up);
            float r = length(uv);
            float edge = clamp((1.0 - r) * MOON_R / max(px, 1e-5) + 0.5, 0.0, 1.0);
            if (edge > 0.0) {
                vec3 n = uv.x * right + uv.y * up - moon_dir * sqrt(max(0.0, 1.0 - r * r));
                float lit = smoothstep(-0.02, 0.05, dot(n, sun_dir));
                // The maria: the darker basalt plains.
                float mare = 0.82 + 0.18 * sin(uv.x * 4.1 + 1.3) * sin(uv.y * 3.7 + 0.4);
                col += moon_disk * (lit * mare + 0.004) * edge;
                st *= 1.0 - edge;
            }
        }
        col += st;
        // The sun's disc, darker towards its rim (limb darkening, u ~ 0.6).
        if (dot(d, sun_dir) > 0.9998) {
            vec3 right, up;
            vec2 uv = disc_uv(d, sun_dir, SUN_R, right, up);
            float r = length(uv);
            float edge = clamp((1.0 - r) * SUN_R / max(px, 1e-5) + 0.5, 0.0, 1.0);
            float mu = sqrt(max(0.0, 1.0 - r * r));
            col += sun_disk * (1.0 - 0.6 * (1.0 - mu)) * edge;
        }
    }
    COLOR = col;
}
";

    // ---------------------------------------------------------------- rain

    const int RainDrops = 20000;
    static readonly StringName PWind = "wind", PTint = "tint", PCount = "count";

    /// <summary>
    /// Rain: streaks in a box round whichever camera is looking, each a drop falling at its terminal velocity and
    /// drifting with the wind, drawn over the time the eye integrates (~50 ms), so a streak ~30 cm long. The drops
    /// are fixed in the world and the box wraps round the camera, so nothing runs out or pops in however fast the
    /// camera moves (one mesh moved in its vertex shader: no particle simulation). What's drawn is a thinned
    /// sample of the real count (Marshall-Palmer: hundreds of mm-sized drops per m³); the rest is in the fog.
    /// </summary>
    void BuildRain()
    {
        var rng = new Random(7);
        var v = new Vector3[RainDrops * 4];
        var uv = new Vector2[RainDrops * 4];
        var col = new Color[RainDrops * 4];
        var idx = new int[RainDrops * 6];
        for (int i = 0; i < RainDrops; i++)
        {
            var seed = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());
            // r: the drop's size (so its speed); g: its place in the count, so heavier rain draws more of them.
            var c = new Color((float)rng.NextDouble(), (i + 0.5f) / RainDrops, 0f);
            for (int k = 0; k < 4; k++)
            {
                v[i * 4 + k] = seed;
                col[i * 4 + k] = c;
            }
            uv[i * 4] = new Vector2(-1f, 0f);
            uv[i * 4 + 1] = new Vector2(1f, 0f);
            uv[i * 4 + 2] = new Vector2(1f, 1f);
            uv[i * 4 + 3] = new Vector2(-1f, 1f);
            int o = i * 6, b = i * 4;
            idx[o] = b; idx[o + 1] = b + 1; idx[o + 2] = b + 2;
            idx[o + 3] = b; idx[o + 4] = b + 2; idx[o + 5] = b + 3;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v;
        arrays[(int)Mesh.ArrayType.TexUV] = uv;
        arrays[(int)Mesh.ArrayType.Color] = col;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _rainMat = new ShaderMaterial { Shader = new Shader { Code = RainShader } };
        // More of the drops drawn the harder it rains: the count of mm-sized drops goes as about R^0.66
        // (Marshall-Palmer).
        _rainMat.SetShaderParameter(PCount, Mathf.Clamp(MathF.Pow(RainRate / 40f, 0.66f), 0.15f, 1f));
        _rain = new MeshInstance3D
        {
            Mesh = mesh, MaterialOverride = _rainMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            // The drops are placed in the shader, round the camera wherever it is.
            CustomAabb = new Aabb(new Vector3(-50000f, -5000f, -50000f), new Vector3(100000f, 20000f, 100000f)),
        };
        AddChild(_rain);
    }

    void UpdateRain()
    {
        if (_rain == null || _rainMat == null) return;
        var cam = GetViewport().GetCamera3D();
        // Under a roof it doesn't rain (the box would come through it).
        _rain.Visible = cam != null && Rooms.At(cam.GlobalPosition) == null;
        if (!_rain.Visible) return;
        _rainMat.SetShaderParameter(PWind, SoundWorld.I is { } sw && IsInstanceValid(sw) ? sw.Wind : Vector3.Zero);
        // A drop is a lens: it shows the sky round it, a little darker. At night, only where there's light.
        _rainMat.SetShaderParameter(PTint, (_hor * 0.8f + _zen * 0.2f) * 0.9f);
    }

    const string RainShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled, world_vertex_coords;

uniform vec3 box_size = vec3(30.0, 16.0, 30.0);
uniform vec3 wind;
uniform float exposure = 0.05;
uniform float px = 0.0014;
uniform float count = 1.0;
uniform vec3 tint = vec3(0.6);
uniform float opacity = 0.45;
varying float v_alpha;

void vertex() {
    vec3 seed = VERTEX;
    v_alpha = 0.0;
    if (COLOR.g > count) {
        VERTEX = vec3(0.0);
    } else {
        // Terminal velocity 4-8 m/s: drops of 1-3 mm (Gunn & Kinzer 1949). They drift with the wind.
        float speed = 4.0 + 4.0 * COLOR.r;
        vec3 vel = vec3(wind.x, -speed, wind.z);
        vec3 cam = CAMERA_POSITION_WORLD;
        vec3 origin = cam - box_size * 0.5;
        vec3 p = origin + mod(seed * box_size + vel * TIME - origin, box_size);
        vec3 axis = normalize(vel);
        vec3 to_cam = cam - p;
        float dist = max(length(to_cam), 0.01);
        vec3 side = normalize(cross(axis, to_cam / dist));
        // A 2 mm drop, or at least a pixel wide and correspondingly fainter (the same light spread wider).
        float w = max(0.002, px * dist);
        v_alpha = opacity * (0.002 / w) * smoothstep(0.3, 1.2, dist);
        VERTEX = p + side * UV.x * w + axis * (UV.y - 0.5) * speed * exposure;
    }
}

void fragment() {
    ALBEDO = tint;
    ALPHA = v_alpha * (1.0 - abs(UV.x)) * sin(UV.y * PI);
}
";
}
