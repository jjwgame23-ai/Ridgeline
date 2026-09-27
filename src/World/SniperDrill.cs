using Godot;

namespace Ridgeline;

/// <summary>
/// A shooter on a hillside ~450 m out who deliberately misses you: the crack
/// of each round reaches you before the gunshot does, and the gap tells you how
/// far away he is. Spot his dust, then ring the plate beside him to end the drill.
/// </summary>
public partial class SniperDrill : Node3D
{
    const float Speed = 820f, Drag = 0.00048f;

    public Terrain Terrain = null!;
    public bool Active { get; private set; }

    Vector3 _pos;
    SteelTarget _plate = null!;
    float _next, _dist;
    readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        // Highest point on an arc 420-480 m out, off to one side of the range.
        var best = Vector3.Zero;
        float bestH = float.MinValue;
        var rng = new RandomNumberGenerator { Seed = 3 };
        for (int i = 0; i < 400; i++)
        {
            float ang = Mathf.DegToRad(rng.RandfRange(25f, 55f)) * (rng.Randf() < 0.5f ? -1f : 1f);
            float r = rng.RandfRange(420f, 480f);
            float x = MathF.Sin(ang) * r, z = -MathF.Cos(ang) * r;
            float h = Terrain.HeightAt(x, z);
            if (h > bestH && Terrain.NormalAt(x, z).Y > 0.8f) { bestH = h; best = new Vector3(x, h, z); }
        }
        _pos = best + Vector3.Up * 0.6f; // prone-ish
        _dist = new Vector2(_pos.X, _pos.Z).Length();

        float yaw = Mathf.RadToDeg(MathF.Atan2(_pos.X, _pos.Z)); // face the firing line
        _plate = SteelTarget.Create(this, best + new Vector3(1.2f, 1.1f, 0f), yaw, new Vector2(0.45f, 0.6f), new Color(0.35f, 0.38f, 0.25f), _dist);
        _plate.WasHit += _ =>
        {
            if (!Active) return;
            Active = false;
            Hud.Toast("Sniper drill: target neutralised. F4 to run it again.", 5f);
        };
    }

    public void Toggle()
    {
        Active = !Active;
        _next = 1.5f;
        Hud.Toast(Active
            ? $"Sniper drill ON — someone ~{_dist:0} m out is shooting near you. Crack first, then the bang. Find his dust and ring the plate next to him."
            : "Sniper drill OFF", 6f);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Active || Player.I == null) return;
        _next -= (float)delta;
        if (_next > 0f) return;
        _next = _rng.RandfRange(2.5f, 5f);

        var target = Player.I.GlobalPosition + Vector3.Up * 1.3f;
        var line = (target - _pos).Normalized();
        var off = new Vector3(_rng.RandfRange(-1f, 1f), _rng.RandfRange(-0.6f, 1f), _rng.RandfRange(-1f, 1f));
        off -= line * off.Dot(line);
        var aim = target + off.Normalized() * _rng.RandfRange(1.2f, 4.5f);

        var to = aim - _pos;
        var dir = to.Normalized();
        dir = dir.Rotated(dir.Cross(Vector3.Up).Normalized(), Ballistics.ZeroAngle(Speed, Drag, to.Length()));
        var muzzle = _pos + dir * 0.8f;
        // A drill: its rounds are meant to miss, so they carry no damage.
        Ballistics.I.Fire(muzzle, dir, Speed, Drag, null, 0f, "drill", _plate.GetRid());
        SoundWorld.I.Emit(Snd.Rifle762, muzzle, facing: dir);
        Effects.I.MuzzleFlash(muzzle, dir);
        Effects.I.MuzzleDust(muzzle + Vector3.Down * 0.4f, dir);
    }
}
