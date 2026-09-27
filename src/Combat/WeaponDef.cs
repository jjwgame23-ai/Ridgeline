using Godot;

namespace Ridgeline;

public sealed class WeaponDef
{
    public string Name = "";
    public Snd Sound;
    public float MuzzleVel, Drag, Rpm;
    public float Damage; // at the muzzle, torso; scales with (v/v0)^1.5 and hit zone
    public bool AutoCapable;
    public bool Explosive;   // 40 mm: the round bursts on impact instead of being a bullet
    public float ArmM;       // an explosive round travels this far before it's armed
    public float Pen = -1f, VehDamage = -1f; // armour penetration / hull damage (-1: small-arms defaults)
    public float Crater = 0.65f;
    /// <summary>
    /// Explosive rounds: how far out a standing man in the open has an even chance of being hit by at
    /// least one fragment (m), and the charge relative to a hand grenade (blast, flash and noise).
    /// </summary>
    public float FragR = 7f, Power = 1f;
    public bool Rocket;      // visible, smoking, with a backblast
    public bool Guided;      // an infrared missile: lock an aircraft first
    public int MagSize, Mags;

    // Recoil: VertKick/HorizKick move your view and you have to pull them back down;
    // RecoverFrac of that returns on its own. Punch is the weapon jumping in your
    // hands and settling again.
    public float VertKickDeg, HorizKickDeg, PunchDeg, RecoverFrac;

    public float AdsFov, AdsTime;
    public bool Scoped;
    public float SpreadAdsDeg, SpreadHipDeg, SwayDeg;
    public float Reload, ReloadEmpty;
    public float ZeroM;
    public Vector3 HipOffset, AdsOffset;
    public Func<(Node3D model, Node3D muzzle)> Build = null!;

    // Drag coefficients are fitted so velocity falls off like real loads:
    // 5.56 ~590 m/s at 500 m, 7.62 match ~560 m/s at 800 m.
    public static readonly WeaponDef Carbine = new()
    {
        Name = "M4 carbine · red dot", Sound = Snd.Rifle556,
        MuzzleVel = 900f, Drag = 0.00085f, Rpm = 800f, Damage = 50f, AutoCapable = true, MagSize = 30, Mags = 7,
        VertKickDeg = 0.55f, HorizKickDeg = 0.22f, PunchDeg = 1.1f, RecoverFrac = 0.55f,
        AdsFov = 58f, AdsTime = 0.24f, SpreadAdsDeg = 0.05f, SpreadHipDeg = 2.6f, SwayDeg = 0.32f,
        Reload = 2.5f, ReloadEmpty = 3.1f, ZeroM = 100f,
        HipOffset = new Vector3(0.15f, -0.14f, -0.06f), AdsOffset = new Vector3(0f, 0f, -0.05f),
        Build = WeaponModels.Carbine,
    };

    /// <summary>M249-style light machine gun: a 100-round belt, heavier and slower to aim, built to keep heads down.</summary>
    public static readonly WeaponDef Lmg = new()
    {
        Name = "M249 LMG · iron sights", Sound = Snd.Rifle556,
        MuzzleVel = 915f, Drag = 0.00085f, Rpm = 780f, Damage = 50f, AutoCapable = true, MagSize = 100, Mags = 6, // 600 rounds: a gunner's load
        VertKickDeg = 0.42f, HorizKickDeg = 0.3f, PunchDeg = 1.3f, RecoverFrac = 0.5f,
        AdsFov = 62f, AdsTime = 0.42f, SpreadAdsDeg = 0.09f, SpreadHipDeg = 3.4f, SwayDeg = 0.42f,
        Reload = 5.8f, ReloadEmpty = 6.6f, ZeroM = 100f,
        HipOffset = new Vector3(0.16f, -0.16f, -0.02f), AdsOffset = new Vector3(0f, 0f, -0.02f),
        Build = WeaponModels.Lmg,
    };

    /// <summary>
    /// M320-style 40 mm launcher: a slow, lobbed high-explosive round. Zeroed at 100 m;
    /// the ladder on the sight gives holds for 50-250 m.
    /// </summary>
    public static readonly WeaponDef Launcher = new()
    {
        Name = "M320 40mm launcher", Sound = Snd.Launcher,
        MuzzleVel = 76f, Drag = 0f, Rpm = 30f, Damage = 0f, AutoCapable = false, MagSize = 1, Mags = 7,
        Explosive = true, ArmM = 14f, FragR = 7f, Power = 0.25f,
        VertKickDeg = 2.4f, HorizKickDeg = 0.5f, PunchDeg = 3.5f, RecoverFrac = 0.7f,
        AdsFov = 64f, AdsTime = 0.3f, SpreadAdsDeg = 0.25f, SpreadHipDeg = 1.5f, SwayDeg = 0.35f,
        Reload = 2.6f, ReloadEmpty = 2.6f, ZeroM = 100f,
        HipOffset = new Vector3(0.14f, -0.15f, -0.05f), AdsOffset = new Vector3(0f, 0f, -0.05f),
        Build = WeaponModels.Launcher,
    };

    /// <summary>Light anti-tank: a disposable-style 84 mm rocket. Enough for light armour and the sides of heavier.</summary>
    public static readonly WeaponDef Lat = new()
    {
        Name = "M72-style LAW 66mm", Sound = Snd.Rocket,
        MuzzleVel = 200f, Drag = 0.0002f, Rpm = 20f, Damage = 300f, AutoCapable = false, MagSize = 1, Mags = 1,
        Explosive = true, ArmM = 10f, Pen = 320f, VehDamage = 280f, Crater = 0.5f, FragR = 4f, Power = 1.5f, Rocket = true,
        VertKickDeg = 3f, HorizKickDeg = 0.6f, PunchDeg = 4f, RecoverFrac = 0.8f,
        AdsFov = 55f, AdsTime = 0.45f, SpreadAdsDeg = 0.2f, SpreadHipDeg = 2f, SwayDeg = 0.4f,
        Reload = 4f, ReloadEmpty = 4f, ZeroM = 150f,
        HipOffset = new Vector3(0.12f, -0.1f, 0.05f), AdsOffset = new Vector3(0f, 0f, 0.05f),
        Build = WeaponModels.Tube,
    };

    /// <summary>Heavy anti-tank: a big tandem-warhead launcher that kills tanks from most angles.</summary>
    public static readonly WeaponDef Hat = new()
    {
        Name = "RPG-29-style HAT 105mm", Sound = Snd.Rocket,
        MuzzleVel = 280f, Drag = 0.00012f, Rpm = 12f, Damage = 400f, AutoCapable = false, MagSize = 1, Mags = 3,
        Explosive = true, ArmM = 15f, Pen = 650f, VehDamage = 520f, Crater = 0.7f, FragR = 6f, Power = 3f, Rocket = true,
        VertKickDeg = 3.5f, HorizKickDeg = 0.8f, PunchDeg = 5f, RecoverFrac = 0.8f,
        AdsFov = 40f, AdsTime = 0.6f, SpreadAdsDeg = 0.15f, SpreadHipDeg = 2.5f, SwayDeg = 0.5f,
        Reload = 6f, ReloadEmpty = 6f, ZeroM = 200f,
        HipOffset = new Vector3(0.12f, -0.08f, 0.1f), AdsOffset = new Vector3(0f, 0f, 0.1f),
        Build = WeaponModels.Tube,
    };

    /// <summary>Shoulder-launched infrared anti-aircraft missile: hold it on a helicopter until it locks.</summary>
    public static readonly WeaponDef Manpad = new()
    {
        Name = "Stinger-style MANPADS", Sound = Snd.Rocket,
        MuzzleVel = 550f, Drag = 0f, Rpm = 10f, Damage = 300f, AutoCapable = false, MagSize = 1, Mags = 1,
        Explosive = true, ArmM = 30f, Pen = 30f, VehDamage = 520f, Crater = 0.3f, FragR = 5f, Power = 3f, Rocket = true, Guided = true,
        VertKickDeg = 2f, HorizKickDeg = 0.5f, PunchDeg = 3f, RecoverFrac = 0.8f,
        AdsFov = 45f, AdsTime = 0.6f, SpreadAdsDeg = 0.1f, SpreadHipDeg = 2f, SwayDeg = 0.4f,
        Reload = 7f, ReloadEmpty = 7f, ZeroM = 300f,
        HipOffset = new Vector3(0.12f, -0.08f, 0.1f), AdsOffset = new Vector3(0f, 0f, 0.1f),
        Build = WeaponModels.Tube,
    };

    public static readonly WeaponDef Marksman = new()
    {
        Name = "SR-25 marksman · 4x", Sound = Snd.Rifle762,
        MuzzleVel = 820f, Drag = 0.00048f, Rpm = 320f, Damage = 72f, AutoCapable = false, MagSize = 20, Mags = 5,
        VertKickDeg = 1.5f, HorizKickDeg = 0.4f, PunchDeg = 2.6f, RecoverFrac = 0.6f,
        AdsFov = 23.7f, AdsTime = 0.34f, Scoped = true, SpreadAdsDeg = 0.018f, SpreadHipDeg = 3.2f, SwayDeg = 0.22f,
        Reload = 2.8f, ReloadEmpty = 3.4f, ZeroM = 200f,
        HipOffset = new Vector3(0.15f, -0.15f, -0.04f), AdsOffset = new Vector3(0f, 0f, 0.02f),
        Build = WeaponModels.Marksman,
    };
}
