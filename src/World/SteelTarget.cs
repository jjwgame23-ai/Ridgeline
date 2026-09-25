using Godot;

namespace Ridgeline;

/// <summary>A steel plate hanging from a crossbar. It swings when hit and rings — at the speed of sound.</summary>
public partial class SteelTarget : StaticBody3D
{
    public static SteelTarget? LastHit { get; private set; }
    public static double LastHitTime { get; private set; }

    public float Distance;
    public event Action<SteelTarget>? WasHit;

    Node3D _pivot = null!;
    float _yaw, _swing, _swingVel;
    int _marks;

    public static SteelTarget Create(Node3D parent, Vector3 hangPoint, float yawDeg, Vector2 size, Color color, float distance)
    {
        var pivot = new Node3D();
        parent.AddChild(pivot);
        pivot.GlobalPosition = hangPoint;
        pivot.RotationDegrees = new Vector3(0f, yawDeg, 0f);

        var t = new SteelTarget { Distance = distance, _pivot = pivot, _yaw = Mathf.DegToRad(yawDeg) };
        pivot.AddChild(t);
        t.Position = new Vector3(0f, -size.Y / 2f - 0.05f, 0f);
        t.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(size.X, size.Y, 0.02f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = color, Roughness = 0.7f },
        });
        t.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size.X, size.Y, 0.03f) } });
        return t;
    }

    public void Hit(Vector3 pos, Vector3 vel)
    {
        SoundWorld.I.Emit(Snd.Steel, pos);
        var face = GlobalBasis.Z.Normalized();
        float along = vel.Dot(face);
        _swingVel += -Mathf.Sign(along) * Mathf.Clamp(vel.Length() / 300f, 0.5f, 4f);
        if (_marks < 40)
        {
            Effects.I.Mark(pos, along > 0f ? -face : face, 0.035f, this, permanent: true);
            _marks++;
        }
        LastHit = this;
        LastHitTime = Time.GetTicksMsec() / 1000.0;
        WasHit?.Invoke(this);
    }

    public override void _Process(double delta)
    {
        if (MathF.Abs(_swing) < 1e-4f && MathF.Abs(_swingVel) < 1e-4f) return;
        float dt = (float)delta;
        _swingVel += (-24f * MathF.Sin(_swing) - 1.0f * _swingVel) * dt;
        _swing += _swingVel * dt;
        _pivot.Rotation = new Vector3(_swing, _yaw, 0f);
    }
}
