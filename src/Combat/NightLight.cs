using Godot;

namespace Ridgeline;

/// <summary>
/// Turning a real light source (candela) into a light the renderer can draw, at night.
/// The eye doesn't see lux, it sees something closer to its logarithm (<see cref="Conditions.Light"/>): a flare that puts
/// 7 lux on the ground where the moon puts 0.25 makes it look like dusk, not thirty times brighter. So what's drawn is
/// the light as the eye takes it: how much brighter the ground looks with the source than without (on the eye's scale,
/// taken back to linear for the renderer, gamma 2.2) at a near and a far distance, and a light whose falloff
/// d^-decay passes through both. Past the far distance it fades out.
/// </summary>
public static class NightLight
{
    /// <summary>The renderer's light energy that stands for full daylight (the sun's, see Main.BuildEnvironment).</summary>
    public const float DayEnergy = 1.3f;

    /// <summary>
    /// How bright ground lit to <paramref name="lux"/> is drawn: the same adaptation curve the sky and the sun and moon are
    /// drawn by (SkyView.Level), so a flare or a fire sits in the scene as the eye would see it. (Each side of the night
    /// work had its own curve: flares came out about 80 times brighter than the starlit ground round them, where the eye
    /// sees them about six times brighter.)
    /// </summary>
    static float Seen(float lux) => SkyView.Level(lux);

    /// <summary>
    /// The energy and falloff exponent for a source of <paramref name="candela"/> between <paramref name="near"/> and
    /// <paramref name="far"/> metres, over the sky's own light <paramref name="ambientLux"/>. Zero energy when it adds
    /// nothing the eye would notice (by day).
    /// </summary>
    public static (float Energy, float Decay) Fit(float candela, float near, float far, float ambientLux)
    {
        float sky = Seen(ambientLux);
        float e1 = Seen(ambientLux + candela / (near * near)) - sky;
        float e2 = Seen(ambientLux + candela / (far * far)) - sky;
        if (e1 < 0.004f) return (0f, 1f);
        e2 = MathF.Max(e2, e1 * 0.01f);
        float decay = Mathf.Clamp(MathF.Log(e1 / e2) / MathF.Log(far / near), 0.2f, 2f);
        return (e1 * MathF.Pow(near, decay) * DayEnergy, decay);
    }

    /// <summary>Set up an omni light for a source (see <see cref="Fit"/>); hidden when it adds nothing.</summary>
    public static void Apply(OmniLight3D l, float candela, float near, float far, float ambientLux, float flicker = 1f)
    {
        var (energy, decay) = Fit(candela, near, far, ambientLux);
        l.Visible = energy > 0f;
        l.LightEnergy = energy * flicker;
        l.OmniAttenuation = decay;
        l.OmniRange = far * 1.6f;
    }

    /// <summary>
    /// The wind at <paramref name="height"/> metres above the ground: the surface wind SoundWorld keeps (gusts and all),
    /// stronger with height by the 1/7 power law from 10 m.
    /// </summary>
    public static Vector3 WindAt(float height)
    {
        var w = SurfaceWind();
        return w * MathF.Pow(MathF.Max(height, 2f) / 10f, 1f / 7f);
    }

    // SoundWorld owns the wind (which way it blows and how hard, gusts included).
    static double _windAt = -1.0;
    static Vector3 _wind;

    static Vector3 SurfaceWind()
    {
        if (_windAt >= 0.0 && Clock.Now >= _windAt && Clock.Now - _windAt < 1.0) return _wind; // (a new match starts the clock again)
        _windAt = Clock.Now;
        _wind = SoundWorld.I?.Wind ?? new Vector3(Acoustics.WindMean, 0f, 0f);
        return _wind;
    }
}
