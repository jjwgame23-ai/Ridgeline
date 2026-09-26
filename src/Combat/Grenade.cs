using Godot;

namespace Ridgeline;

public partial class Grenade : RigidBody3D
{
    public float Fuse = 3.8f;
    public ICombatant? Thrower;
    /// <summary>A smoke grenade: pops a screen instead of going off.</summary>
    public bool Smoke;

    /// <summary>Live grenades, so bots can see one land next to them and get away.</summary>
    public static readonly List<Grenade> Live = new();
    double _lastBounce;
    bool _done;

    public override void _Ready()
    {
        AddToGroup("grenades");
        Live.Add(this);
        Mass = 0.4f;
        ContinuousCd = true;
        ContactMonitor = true;
        MaxContactsReported = 2;
        AngularDamp = 1f;
        CollisionLayer = 4;
        CollisionMask = Layers.Solid;
        PhysicsMaterialOverride = new PhysicsMaterial { Bounce = 0.25f, Friction = 0.9f };
        AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.045f } });
        AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.045f, Height = 0.09f },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Smoke ? new Color(0.5f, 0.5f, 0.48f) : new Color(0.22f, 0.25f, 0.15f), Roughness = 0.7f },
        });
        BodyEntered += _ =>
        {
            double now = Time.GetTicksMsec() / 1000.0;
            float speed = LinearVelocity.Length();
            if (now - _lastBounce < 0.15 || speed < 1.5f) return;
            _lastBounce = now;
            SoundWorld.I.Emit(Snd.Bounce, GlobalPosition, Mathf.Clamp(speed / 5f, 0.3f, 1.5f) * 6f - 6f, Thrower);
        };
    }

    public override void _ExitTree() => Live.Remove(this);

    public override void _PhysicsProcess(double delta)
    {
        Fuse -= (float)delta;
        if (Fuse <= 0f && !_done) Explode();
    }

    void Explode()
    {
        _done = true;
        if (Smoke) { SmokeScreen.Pop(GlobalPosition, 9f, 50f); QueueFree(); return; }
        Detonate(GlobalPosition, Vector3.Up, Thrower, 70, 0.9f, 0f, "frag", GetRid());
        QueueFree();
    }

    /// <summary>
    /// Any high-explosive burst: the bang, the flash and crater, a spray of real
    /// fragments, and the blast wave for suppression.
    /// </summary>
    /// <param name="power">Explosive charge relative to a hand grenade (an 81 mm bomb is about 4).</param>
    public static void Detonate(Vector3 pos, Vector3 normal, ICombatant? by, int frags, float crater, float gainDb, string weapon, Rid ignore = default, float power = 1f)
    {
        SoundWorld.I.Emit(power >= 2f ? Snd.Shell : Snd.Explosion, pos, gainDb, by);
        if (crater > 0f) Effects.I.Explosion(pos, normal, crater, power);
        else Effects.I.Explosion(pos, normal, 0.01f, power);
        Vehicle.BlastAll(pos, frags / 45f);
        Fob.BlastAll(pos, frags / 45f);
        var rng = new RandomNumberGenerator();
        rng.Randomize();
        var up = normal.LengthSquared() > 0.1f ? normal.Normalized() : Vector3.Up;
        for (int i = 0; i < frags; i++)
        {
            var dir = new Vector3(rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f), rng.RandfRange(-1f, 1f)).Normalized();
            if (dir.Dot(up) < -0.25f) dir = (dir - up * (dir.Dot(up) * 1.5f)).Normalized(); // mostly away from what it hit
            Ballistics.I.Fire(pos + up * 0.1f, dir, rng.RandfRange(900f, 1400f), 0.012f, by, 32f, weapon, ignore, silent: true);
        }
        Combatants.Blast(pos, power);
        if (power >= 2f) Squad.IndirectImpact(pos);
    }
}
