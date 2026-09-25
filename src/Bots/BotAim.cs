using Godot;

namespace Ridgeline;

/// <summary>
/// How a bot points its gun, modelled on how people aim with a mouse:
///
/// - Swinging onto a new target is a flick that over- or undershoots, then
///   settles (FlickTau). Turn speed is capped, so big swings take time.
/// - A moving target is followed through a smoothed estimate, so poorer
///   trackers lag behind and under-lead.
/// - Hands are never perfectly still (tremor), worse when moving, suppressed or hurt.
/// - Recoil kicks the muzzle; the share the bot fails to pull down lingers as
///   error, so weaker bots' sprays climb.
///
/// Angles are degrees: Pitch up-positive, Yaw like Godot's rotation.y (0 = facing -Z).
/// </summary>
public sealed class BotAim
{
    readonly Bot _b;
    readonly float _seed;
    Vector2 _err;          // (pitch, yaw) offset from the true aim point, decaying
    Vector3 _goalSmooth;
    bool _hasGoal;
    float _t;

    public float Yaw, Pitch;
    public Vector3? Goal;         // where the bot is trying to aim
    public Vector3 GoalVel;       // for leading a moving target
    public bool Tracking;         // goal is an enemy: apply lag, lead and tremor rules

    public BotAim(Bot b, float seed) { _b = b; _seed = seed; }

    public Vector3 Dir => DirFrom(Yaw, Pitch);

    public static Vector3 DirFrom(float yawDeg, float pitchDeg)
    {
        float y = Mathf.DegToRad(yawDeg), p = Mathf.DegToRad(pitchDeg);
        return new Vector3(-MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), -MathF.Cos(y) * MathF.Cos(p));
    }

    public static Vector2 AnglesTo(Vector3 d)
    {
        d = d.Normalized();
        return new Vector2(Mathf.RadToDeg(MathF.Asin(Mathf.Clamp(d.Y, -1f, 1f))), Mathf.RadToDeg(MathF.Atan2(-d.X, -d.Z)));
    }

    public static float Wrap(float deg) => ((deg + 180f) % 360f + 360f) % 360f - 180f;

    /// <summary>Start a flick toward a newly acquired target.</summary>
    public void Flick(Vector3 point, RandomNumberGenerator rng)
    {
        var want = AnglesTo(point - _b.EyePos);
        var delta = new Vector2(want.X - Pitch, Wrap(want.Y - Yaw));
        float mag = delta.Length();
        // Positive factor = overshoot past the target, negative = fall short.
        var err = delta * rng.RandfRange(-0.12f, 0.3f)
                + new Vector2(rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f)) * (mag * 0.08f + (1f - _b.P.Skill) * 1.2f);
        _err = err.LimitLength(7f);
        _goalSmooth = point;
        _hasGoal = true;
    }

    public void Kick(float vert, float horiz, float mult)
    {
        var k = new Vector2(vert, horiz) * mult;
        Pitch += k.X;
        Yaw += k.Y;
        // Only part of what isn't controlled lingers — people pull down through a burst, just not perfectly.
        _err += new Vector2(k.X * 0.45f, k.Y * 0.7f) * (1f - _b.P.RecoilControl);
    }

    public void Flinch(RandomNumberGenerator rng)
    {
        var j = new Vector2(rng.RandfRange(-3f, 3f), rng.RandfRange(-4f, 4f));
        Pitch += j.X;
        Yaw += j.Y;
        _err += j * 0.5f;
    }

    public void Update(float dt)
    {
        _t += dt;
        var P = _b.P;

        if (Goal is Vector3 g)
        {
            if (!_hasGoal || _goalSmooth.DistanceTo(g) > 4f) _goalSmooth = g;
            else _goalSmooth = _goalSmooth.Lerp(g, 1f - MathF.Exp(-(Tracking ? Mathf.Lerp(5f, 16f, P.Tracking) : 10f) * dt));
            _hasGoal = true;
        }
        else _hasGoal = false;

        _err *= MathF.Exp(-dt / P.FlickTau);

        Vector2 desired;
        if (_hasGoal)
        {
            var pt = _goalSmooth;
            if (Tracking)
            {
                float tof = _b.EyePos.DistanceTo(pt) / _b.Def.MuzzleVel;
                pt += GoalVel * tof * P.Tracking;
            }
            desired = AnglesTo(pt - _b.EyePos) + _err;
        }
        else desired = new Vector2(0f, Yaw);

        float amp = P.TremorDeg
                    * (_b.Brain.WantsAds ? 1f : 2.2f)
                    * (1f + _b.Suppression * 2.5f)
                    * (_b.Velocity.Length() > 0.5f ? 1.8f : 1f)
                    * (_b.Crouched ? 0.75f : 1f)
                    * (1f + _b.Body.AimPenalty + (1f - _b.Stamina) * 0.6f); // winded: the sights bob with your breathing
        desired += new Vector2(
            MathF.Sin(_t * 1.7f + _seed) * 0.6f + MathF.Sin(_t * 4.3f + _seed * 2f) * 0.25f,
            MathF.Sin(_t * 1.1f + _seed * 3f) * 0.6f + MathF.Sin(_t * 3.7f + _seed) * 0.25f) * amp;

        // Close most of the gap quickly, but never faster than the bot can physically swing.
        var diff = new Vector2(desired.X - Pitch, Wrap(desired.Y - Yaw));
        var step = diff * Mathf.Min(1f, dt * 14f);
        float cap = P.TurnSpeed * dt;
        if (step.Length() > cap) step = step.Normalized() * cap;
        Pitch = Mathf.Clamp(Pitch + step.X, -80f, 80f);
        Yaw = Wrap(Yaw + step.Y);
    }
}
