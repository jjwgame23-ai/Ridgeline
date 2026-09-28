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
        Detonate(GlobalPosition, Vector3.Up, Thrower, 10f, 0.9f, 0f, "frag", GetRid());
        QueueFree();
    }

    static readonly Random Rng = new();

    /// <summary>
    /// Any high-explosive burst: the bang, the flash and crater, the fragments, and the blast wave.
    ///
    /// The fragments are real projectiles, but only the ones that matter are flown. A charge breaks into
    /// thousands, almost all of them into empty air; rather than fly them all, each person near the burst
    /// gets their share of the spray (as many as would come their way for their size and stance and how
    /// far off they are: fragment density falls with the square of the distance), each a real fragment
    /// aimed somewhere across their silhouette. Those fly and strike like any round: a wall, a sandbag or
    /// someone in between stops them, and where they hit is where they hit. A few more fly off every which
    /// way for the dust they kick up.
    /// </summary>
    /// <param name="fragR">How far out a standing man in the open has an even chance of at least one fragment hit (m): ~10 for a hand grenade, ~25 for an 81 mm bomb.</param>
    /// <param name="power">Explosive charge relative to a hand grenade (an 81 mm bomb is about 4): blast, flash and noise.</param>
    /// <param name="indirect">A mortar bomb (or a shell): squads close by run the react-to-indirect-fire drill.</param>
    public static void Detonate(Vector3 pos, Vector3 normal, ICombatant? by, float fragR, float crater, float gainDb, string weapon, Rid ignore = default, float power = 1f, bool indirect = false)
    {
        // Whoever set it off may be long gone (the man who shot down an aircraft that only now hits the ground,
        // dead and cleared away since): then it's nobody's. (Reaching for his body threw, about once every ten minutes
        // in long matches.)
        if (by is GodotObject g && !GodotObject.IsInstanceValid(g)) by = null;
        SoundWorld.I.Emit(power >= 2f ? Snd.Shell : Snd.Explosion, pos, gainDb, by);
        Effects.I.Explosion(pos, normal, crater > 0f ? crater : 0.01f, power);
        Telemetry.Explosion(pos, by, weapon, power, fragR);
        Vehicle.BlastAll(pos, power, by);
        Fob.BlastAll(pos, power);
        var up = normal.LengthSquared() > 0.1f ? normal.Normalized() : Vector3.Up;
        var from = pos + up * 0.1f;
        // A shell's thick casing breaks into heavier fragments than a grenade's.
        Spray(from, by, fragR, power >= 4f ? 42f : 32f, weapon, ignore);
        int dust = (int)Mathf.Clamp(fragR * 0.6f, 3f, 8f);
        for (int i = 0; i < dust; i++)
        {
            var dir = new Vector3(Rng.NextSingle() * 2f - 1f, Rng.NextSingle() * 2f - 1f, Rng.NextSingle() * 2f - 1f).Normalized();
            if (dir.Dot(up) < -0.25f) dir = (dir - up * (dir.Dot(up) * 1.5f)).Normalized(); // mostly away from what it hit
            Ballistics.I.Fire(from, dir, 900f + Rng.NextSingle() * 500f, 0.012f, by, 0f, weapon, ignore, silent: true, mask: Layers.World | Layers.Trees);
        }
        Combatants.Blast(pos, power);
        if (indirect) Squad.IndirectImpact(pos);
    }

    /// <summary>The fragments that come someone's way, flown as real fragments at them (see Detonate).</summary>
    static void Spray(Vector3 from, ICombatant? by, float fragR, float damage, string weapon, Rid ignore)
    {
        const float Ln2 = 0.6931f;
        float reach = fragR * 4f; // past this the odds of a hit are a few percent, and falling
        foreach (var c in Combatants.All)
        {
            // The dead, and people inside a hull (the vehicle takes the blast: BlastAll).
            if (c.Dead || c is CollisionObject3D { CollisionLayer: 0 }) continue;
            var chest = c.ChestPos;
            float r = MathF.Max(from.DistanceTo(chest), 1f);
            if (r > reach) continue;
            float h = c.BodyHeight;
            // What a body shows a burst on the ground: less crouched, and very little lying flat (most of the spray goes up and out, over him).
            float shown = h >= 1.5f ? 1f : h >= 1f ? 0.7f : 0.2f;
            float expected = MathF.Min(Ln2 * (fragR / r) * (fragR / r) * shown, 12f);
            int n = Poisson(expected);
            if (n == 0) continue;
            var across = (chest - from) with { Y = 0f };
            across = across.LengthSquared() > 1e-4f ? across.Normalized().Cross(Vector3.Up) : Vector3.Right;
            for (int k = 0; k < n; k++)
            {
                // Somewhere on him as the burst sees him: head to feet, shoulder to shoulder.
                var at = c.FeetPos + Vector3.Up * (0.08f + Rng.NextSingle() * (h - 0.1f)) + across * (Rng.NextSingle() * 0.5f - 0.25f);
                // A fragment is a small jagged thing: through a door or a car's skin close to the burst, not a wall.
                Ballistics.I.Fire(from, (at - from).Normalized(), 900f + Rng.NextSingle() * 500f, 0.012f, by, damage, weapon, ignore, silent: true, pen: 1.2f);
            }
        }
    }

    /// <summary>A Poisson draw (Knuth's method; fine for the handful expected here).</summary>
    static int Poisson(float mean)
    {
        if (mean <= 0f) return 0;
        double limit = Math.Exp(-mean), p = 1.0;
        int k = 0;
        do { k++; p *= Rng.NextDouble(); } while (p > limit);
        return k - 1;
    }
}
