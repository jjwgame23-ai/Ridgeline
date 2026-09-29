using Godot;

namespace Ridgeline;

/// <summary>What a soldier has for seeing in the dark.</summary>
[Flags]
public enum NightOptic
{
    None = 0,
    /// <summary>Image-intensifier goggles on the helmet (PVS-14/31, 1PN138): about a 40° circle.</summary>
    Goggles = 1,
    /// <summary>An image-intensifier weapon sight (1PN93 on a PKM or an SVD, 1PN51 on an RPG): narrow, only where the weapon points.</summary>
    SightI2 = 2,
    /// <summary>A thermal weapon sight or viewer (clip-on, Javelin CLU, a recon team's hand-held): narrow, blind to the dark.</summary>
    Thermal = 4,
}

/// <summary>How a look at someone went: what it was made with and how well it could do in this light and air.</summary>
public struct Sight
{
    /// <summary>Multiplies the distance at which picking someone out starts to take long (1: broad daylight).</summary>
    public float Reach;
    /// <summary>Multiplies how fast someone is picked out (contrast: 1 by day).</summary>
    public float Rate;
    /// <summary>Contrast left after the air in between (fog, rain, haze).</summary>
    public float Trans;
    public NightOptic By;
    /// <summary>
    /// He's just fired, in the dark: the flash is picked out as if by day (Reach and Rate stay what the man himself
    /// shows, which is what's left to follow once the flash is gone).
    /// </summary>
    public bool Flash;
}

/// <summary>
/// Night vision as the three sides issue it (2020s), and how well a man is picked out by the naked eye, through
/// goggles or through a thermal sight, by the light on him and the air in between.
///
/// Who has what:
/// - ALPHA (US-equipped) and CHARLIE (British-equipped): goggles for every soldier, as their line infantry are issued
///   them (PVS-14/31; the British HMNVS); thermal sights for marksmen (FWS / clip-ons on the SR-25 and L129A1), the
///   recon teams (hand-held thermal viewers), the heavy anti-tank gunners (the Javelin's command launch unit is a
///   thermal sight) and the weapons teams' machine gunners (PAS-13 medium / the GPMG's thermal sight in the sustained-fire
///   role).
/// - BRAVO (Russian-equipped): goggles only for the squad leaders, the recon teams, marksmen and crewmen, which is how
///   1PN138-class goggles were actually spread through Russian units in 2022-24 (plenty in reconnaissance and
///   special units, few in the motor-rifle sections); I2 weapon sights on the weapons teams' PKMs and the SVDs (1PN93)
///   and on the RPGs (1PN51); thermal only in the recon teams.
/// - Vehicle gunners: the sights of the armour (APC, IFV, tank, gun system, gunship, air defence) are thermal on all
///   three sides by now (Sosna-U on a T-72B3, the BTR-82A's and BMP-2M's thermal channel); a Humvee's, a Tigr's, a
///   Land Rover's or a helicopter door gun has only the gunner's goggles.
/// </summary>
public static class NightGear
{
    /// <summary>What a soldier carries, by side, role and squad.</summary>
    public static NightOptic Issue(int team, Role role, SquadKind? squad)
    {
        bool recon = squad == SquadKind.Recon;
        bool weaponsMg = squad == SquadKind.Weapons && role == Role.AutoRifleman;
        if (team == 1)
        {
            var o = NightOptic.None;
            if (role is Role.Leader or Role.Marksman or Role.Crewman || recon) o |= NightOptic.Goggles;
            if (weaponsMg || role is Role.Marksman or Role.HeavyAT) o |= NightOptic.SightI2;
            if (recon) o |= NightOptic.Thermal;
            return o;
        }
        var w = NightOptic.Goggles;
        if (role is Role.Marksman or Role.HeavyAT || recon || weaponsMg) w |= NightOptic.Thermal;
        return w;
    }

    /// <summary>A soldier's own kit, or his seat's sight if he's a vehicle's gunner.</summary>
    public static NightOptic Of(Bot b)
    {
        var own = Issue(b.Team, b.Role, b.Squad?.Kind);
        if (b.Ride is { } v && b.SeatIdx >= 0 && b.SeatIdx < v.Def.Seats.Count && v.Def.Seats[b.SeatIdx].Role == SeatRole.Gunner && ThermalSight(v.Def.Kind))
            own |= NightOptic.Thermal;
        return own;
    }

    public static bool ThermalSight(VKind k) => k is VKind.APC or VKind.IFV or VKind.MBT or VKind.MGS or VKind.AH or VKind.SPAA;

    /// <summary>
    /// Night kit goes on as the light goes: goggles down and thermals on once the sky is below ~10 lux (the sun a
    /// few degrees under the horizon); by day they're off (a goggle tube in daylight shows nothing, and burns).
    /// </summary>
    public static bool NightKitOn => Conditions.Light < 0.72f;

    /// <summary>Half the field of view of goggles (40°), and of a weapon sight (about 18°, a 2x thermal clip-on).</summary>
    public const float GoggleHalfDeg = 20f, SightHalfDeg = 9f;

    // The naked, dark-adapted eye by the light on the target (the 0..1 Light scale: 0.75 ~ 25 lux, 0.61 end of civil
    // twilight, 0.45 full moon, 0.08 starlight). Reach: how acuity falls as vision goes from cones to rods (about 1'
    // by day, 5-6' by moonlight, 20'+ by starlight). Rate: how the contrast threshold rises (Blackwell's data). With the
    // falloff with distance in BotSenses this puts a man walking in the open at: noticed at 400-600 m by day; about
    // 150-200 m by full moon (the figure field manuals give, FM 3-21.75 ch. 9); tens of metres by starlight.
    static readonly float[] EyeL = { 0f, 0.08f, 0.3f, 0.45f, 0.61f, 0.75f };
    static readonly float[] EyeReach = { 0.12f, 0.18f, 0.28f, 0.4f, 0.62f, 1f };
    static readonly float[] EyeRate = { 0.12f, 0.22f, 0.35f, 0.5f, 0.75f, 1f };

    static float Table(float[] y, float l)
    {
        if (l <= EyeL[0]) return y[0];
        for (int i = 1; i < EyeL.Length; i++)
            if (l <= EyeL[i]) return Mathf.Lerp(y[i - 1], y[i], (l - EyeL[i - 1]) / (EyeL[i] - EyeL[i - 1]));
        return 1f;
    }

    /// <summary>
    /// An image intensifier multiplies the light about 1000-2000 times (3 to 3.3 decades: 0.48 on the Light scale),
    /// but its picture never gets better than what a tube resolves (Gen III: about 20/40 acuity at best, noisy by
    /// starlight), about like the naked eye at the end of civil twilight. Its automatic brightness control turns the gain
    /// down when the scene round the wearer is bright: a flare or a fire near him leaves the dark beyond it black.
    /// </summary>
    const float I2GainL = 0.48f, I2CapL = 0.6f;

    /// <summary>Light on the 0..1 scale for a lux value (as Conditions.Light).</summary>
    public static float LightOf(float lux) => Mathf.Clamp((MathF.Log10(MathF.Max(lux, 1e-6f)) + 3.5f) / 6.5f, 0f, 1f);

    /// <summary>
    /// The best of a man's eyes and instruments for a look at someone <paramref name="dist"/> away, <paramref name="angDeg"/>
    /// off where he's pointing: <paramref name="targetL"/> is the light on the target, <paramref name="eyeL"/> round the
    /// observer. <paramref name="sky"/>: seen against the sky (a silhouette: the night sky is some ten times brighter
    /// than the ground under it). <paramref name="flash"/>: he's just fired (a muzzle flash is a point of light seen for
    /// kilometres at night, and draws the eye even from the side).
    /// </summary>
    public static Sight See(NightOptic gear, float angDeg, float dist, float targetL, float eyeL, bool sky, bool flash)
    {
        float l = MathF.Min(1f, targetL + (sky ? 0.15f : 0f));
        var s = new Sight { Reach = Table(EyeReach, l), Rate = Table(EyeRate, l), Trans = Conditions.Transmission(dist), By = NightOptic.None };
        if (s.Reach >= 1f) return s; // broad daylight: nothing to add
        bool on = NightKitOn;
        if (on && (gear & (NightOptic.Goggles | NightOptic.SightI2)) != 0
            && ((gear & NightOptic.Goggles) != 0 && angDeg < GoggleHalfDeg || (gear & NightOptic.SightI2) != 0 && angDeg < SightHalfDeg))
        {
            float gain = I2GainL * Mathf.Clamp((0.62f - eyeL) / 0.2f, 0f, 1f);
            float gl = MathF.Min(l + gain, MathF.Max(l, I2CapL));
            float r = Table(EyeReach, gl);
            if (r > s.Reach) { s.Reach = r; s.Rate = Table(EyeRate, gl); s.By = (gear & NightOptic.Goggles) != 0 && angDeg < GoggleHalfDeg ? NightOptic.Goggles : NightOptic.SightI2; }
        }
        // A thermal sight sees heat, not light: the dark costs it nothing, and long-wave infrared gets through haze, rain
        // and fog about three times as far as visible light (droplets much smaller than 8-12 µm scatter it far less).
        if (on && (gear & NightOptic.Thermal) != 0 && angDeg < SightHalfDeg)
        {
            s.Reach = 1f;
            s.Rate = 1f;
            s.Trans = Conditions.Transmission(dist / 3f);
            s.By = NightOptic.Thermal;
        }
        s.Flash = flash;
        return s;
    }
}
