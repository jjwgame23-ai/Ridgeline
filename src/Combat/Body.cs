using Godot;

namespace Ridgeline;

public enum Region { Head, Chest, Abdomen, Arm, Leg }

public sealed class Wound
{
    public Region Region;
    public string Label = "";
    public float Bleed;       // fraction of total blood volume per second
    public bool Internal;     // chest/abdomen: a bandage only slows it; it needs a medic
    public bool Treated;
    public float Rate => Treated ? (Internal ? Bleed * 0.5f : 0f) : Bleed;
}

public enum HitResult { Wounded, Downed, Dead }

/// <summary>
/// A soldier's body, simply modelled: blood volume, bleeding, accumulated trauma,
/// pain, and a few specific injuries that matter in a fight (a punctured lung, a
/// torn artery, the heart, a grazed skull). There's no hit-point bar: people go
/// down from blood loss (shock below ~55 % volume), from a hit that drops them on the
/// spot, or from enough trauma; they die when they bleed out (~35 %), take a
/// catastrophic hit, or are hit again while down.
///
/// Both bots and the player run on this, and the effects are the same for both:
/// a leg wound slows you, an arm wound wrecks your aim and reloads, blood loss makes
/// everything sluggish, a head graze leaves you concussed.
/// </summary>
public sealed class Body
{
    public float Blood = 1f;        // fraction of normal volume
    public float Trauma;            // tissue damage, 0..~200
    public float Pain;              // 0..1
    public float Concussion;        // 0..1, fades
    public float ArmHurt, LegHurt;  // 0..1+ impairment
    public bool Lung;
    public bool Down, Dead;
    public double DownSince = -1;
    public string Cause = "";
    public readonly List<Wound> Wounds = new();

    public const float DownBlood = 0.55f, DeadBlood = 0.35f, DownTrauma = 115f, DeadTrauma = 190f;
    const double MaxDown = 150.0; // lying there untreated this long, stable or not, they don't make it

    public float Bleeding => Wounds.Sum(w => w.Rate);
    /// <summary>0 fine .. 1 deep in shock, from blood loss.</summary>
    public float Shock => Mathf.Clamp((1f - Blood) / (1f - DownBlood), 0f, 1f);
    /// <summary>A single 0..100 "how is he" number for HUDs and bot decisions.</summary>
    public float Condition => Dead ? 0f : 100f * Mathf.Clamp(MathF.Min((Blood - DeadBlood) / (1f - DeadBlood), 1f - Trauma / DeadTrauma), 0f, 1f);
    public bool NeedsSelfAid => !Dead && Wounds.Any(w => !w.Treated && w.Bleed > 0.0015f);
    public bool CanSprint => LegHurt < 0.35f && Blood > 0.72f && !Down;
    public float SpeedMult => Down ? 0f : (1f - MathF.Min(LegHurt, 1f) * 0.5f) * (1f - Shock * 0.3f);
    /// <summary>How much worse aim gets: tremor, sway.</summary>
    public float AimPenalty => Pain * 0.6f + MathF.Min(ArmHurt, 1.2f) * 0.9f + Shock * 1.1f + Concussion * 0.8f;
    public float ReloadMult => 1f + MathF.Min(ArmHurt, 1f) * 0.6f + Shock * 0.3f;

    /// <summary>
    /// Where on the body a round landed: the head, the chest (upper torso), the
    /// abdomen, an arm (well off the body's centre line at torso height) or a leg.
    /// </summary>
    public static Region RegionFor(ICombatant c, Vector3 point, Vector3 dir, HitZone zone)
    {
        if (zone == HitZone.Head) return Region.Head;
        if (zone == HitZone.Legs) return Region.Leg;
        var off = point - c.FeetPos;
        // How far the round's path passes from the body's centre line: through the middle
        // hits the torso, out near the edge of the silhouette is an arm.
        var d2 = new Vector2(dir.X, dir.Z);
        var o2 = new Vector2(off.X, off.Z);
        float miss = d2.LengthSquared() > 1e-4f ? MathF.Abs(d2.Normalized().Cross(o2)) : o2.Length();
        if (miss > 0.19f) return Region.Arm;
        return off.Y / MathF.Max(c.BodyHeight, 0.5f) > 0.6f ? Region.Chest : Region.Abdomen;
    }

    /// <param name="energy">Wound energy: 1 is a rifle round at close range, fragments ~0.3-0.7.</param>
    public HitResult Hit(Region r, float energy, RandomNumberGenerator rng)
    {
        if (Dead) return HitResult.Dead;
        float e = energy;
        bool drop = false;
        switch (r)
        {
            case Region.Head:
                if (e > 0.45f && rng.Randf() < 0.88f) return Kill("head");
                // A graze, or a fragment that didn't get through the helmet.
                Add(r, "head graze", 0.004f * e, false);
                Concussion = 1f;
                Trauma += 25f * e;
                Pain += 0.4f;
                drop = rng.Randf() < 0.5f;
                break;
            case Region.Chest:
                Trauma += 45f * e;
                Pain += 0.5f * e;
                if (rng.Randf() < 0.07f * e) { Add(r, "heart / great vessels", 0.07f, true); drop = true; }
                else if (rng.Randf() < 0.35f) { Add(r, "punctured lung", 0.012f * e, true); Lung = true; }
                else Add(r, "chest wound", 0.009f * e, true);
                drop |= rng.Randf() < 0.3f * e;
                break;
            case Region.Abdomen:
                Trauma += 38f * e;
                Pain += 0.65f * e;
                Add(r, "abdominal wound", 0.008f * e, true);
                drop = rng.Randf() < 0.22f * e;
                break;
            case Region.Arm:
                Trauma += 18f * e;
                Pain += 0.3f * e;
                ArmHurt += 0.6f * e;
                if (rng.Randf() < 0.12f) Add(r, "arm, arterial bleed", 0.03f, false);
                else Add(r, "arm wound", 0.004f * e, false);
                break;
            case Region.Leg:
                Trauma += 22f * e;
                Pain += 0.35f * e;
                LegHurt += 0.6f * e;
                if (rng.Randf() < 0.14f) Add(r, "leg, femoral bleed", 0.035f, false);
                else Add(r, "leg wound", 0.005f * e, false);
                drop = LegHurt > 1.3f && rng.Randf() < 0.5f; // both legs gone from under them
                break;
        }
        Pain = MathF.Min(Pain, 1f);
        if (Down)
        {
            // Hit again while down: little chance.
            if (Trauma > DeadTrauma * 0.7f || rng.Randf() < 0.5f * e) return Kill("wounds");
            return HitResult.Downed;
        }
        if (Trauma > DeadTrauma) return Kill("wounds");
        if (drop || Trauma > DownTrauma || Blood < DownBlood) return GoDown();
        return HitResult.Wounded;
    }

    /// <summary>Close to an explosion: rattled brain.</summary>
    public void Blast(float strength)
    {
        if (Dead) return;
        Concussion = MathF.Max(Concussion, strength);
    }

    /// <summary>Bleeding and recovery over time. Returns a change of state, if any.</summary>
    public HitResult? Tick(float dt, double now)
    {
        if (Dead) return null;
        Blood -= Bleeding * dt;
        Concussion = MathF.Max(0f, Concussion - dt / 25f);
        Pain = MathF.Max(0.1f * MathF.Min(Wounds.Count, 3), Pain - dt / 90f);
        if (Blood < DeadBlood) { Kill("blood loss"); return HitResult.Dead; }
        if (Down && now - DownSince > MaxDown) { Kill("wounds"); return HitResult.Dead; }
        if (!Down && Blood < DownBlood) { GoDown(); return HitResult.Downed; }
        return null;
    }

    /// <summary>A soldier's own field dressing and tourniquet: stops limb bleeds, slows the rest.</summary>
    public void SelfAid()
    {
        foreach (var w in Wounds) w.Treated = true;
    }

    /// <summary>
    /// A medic: every wound dressed, fluids in, pain managed. Someone who's down is
    /// brought round and back on their feet — shaky, and still carrying the injuries.
    /// </summary>
    public void Treat()
    {
        if (Dead) return;
        foreach (var w in Wounds)
        {
            w.Treated = true;
            if (w.Internal) w.Bleed *= 0.35f;
        }
        Blood = MathF.Min(1f, MathF.Max(Blood + 0.25f, 0.7f));
        Pain = MathF.Max(0f, Pain - 0.4f);
        if (Down)
        {
            Down = false;
            DownSince = -1;
            Trauma = MathF.Min(Trauma, 70f);
        }
    }

    public string Summary()
    {
        if (Dead) return "dead";
        if (Wounds.Count == 0) return "unhurt";
        var parts = Wounds.GroupBy(w => w.Label).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
        return string.Join(", ", parts);
    }

    HitResult GoDown()
    {
        Down = true;
        DownSince = Clock.Now;
        return HitResult.Downed;
    }

    /// <summary>The player chose to stop waiting for a medic.</summary>
    public void GiveUp() => Kill("gave up");

    HitResult Kill(string cause)
    {
        Dead = true;
        Down = false;
        Cause = cause;
        return HitResult.Dead;
    }

    void Add(Region r, string label, float bleed, bool internalBleed) =>
        Wounds.Add(new Wound { Region = r, Label = label, Bleed = bleed, Internal = internalBleed });
}
