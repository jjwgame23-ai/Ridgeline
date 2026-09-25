using Godot;

namespace Ridgeline;

/// <summary>Dust, smoke, flashes and marks. Craters are permanent: the world keeps the scars of fights.</summary>
public partial class Effects : Node3D
{
    public static Effects I { get; private set; } = null!;
    const int MaxMarks = 500;

    readonly Queue<Node3D> _marks = new();
    readonly QuadMesh _unit = new() { Size = Vector2.One };
    QuadMesh _dust = null!, _smoke = null!, _fire = null!, _debris = null!, _flash = null!;
    Gradient _dustRamp = null!, _smokeRamp = null!, _fireRamp = null!, _debrisRamp = null!, _muzzleDustRamp = null!, _bloodRamp = null!;
    Curve _grow = null!;
    StandardMaterial3D _markMat = null!, _scorchMat = null!;

    public override void _EnterTree() => I = this;

    public override void _Ready()
    {
        var soft = SoftCircle();
        _dust = Quad(soft);
        _smoke = Quad(soft);
        _fire = Quad(soft);
        _debris = Quad(null);
        _flash = Quad(soft);

        _dustRamp = Ramp((0f, new Color(0.42f, 0.36f, 0.27f, 0.8f)), (1f, new Color(0.45f, 0.4f, 0.32f, 0f)));
        _muzzleDustRamp = Ramp((0f, new Color(0.55f, 0.5f, 0.42f, 0.5f)), (1f, new Color(0.55f, 0.5f, 0.42f, 0f)));
        _smokeRamp = Ramp((0f, new Color(0.25f, 0.22f, 0.2f, 0.85f)), (0.3f, new Color(0.38f, 0.35f, 0.32f, 0.6f)), (1f, new Color(0.5f, 0.48f, 0.46f, 0f)));
        _fireRamp = Ramp((0f, new Color(1f, 0.85f, 0.5f, 1f)), (0.4f, new Color(1f, 0.45f, 0.1f, 0.8f)), (1f, new Color(0.2f, 0.1f, 0.05f, 0f)));
        _debrisRamp = Ramp((0f, new Color(0.15f, 0.12f, 0.1f, 1f)), (1f, new Color(0.15f, 0.12f, 0.1f, 1f)));
        _bloodRamp = Ramp((0f, new Color(0.45f, 0.03f, 0.03f, 0.9f)), (1f, new Color(0.3f, 0.02f, 0.02f, 0f)));

        _grow = new Curve();
        _grow.AddPoint(new Vector2(0f, 0.35f));
        _grow.AddPoint(new Vector2(1f, 1f));

        _markMat = new StandardMaterial3D
        {
            AlbedoTexture = soft,
            AlbedoColor = new Color(0.07f, 0.06f, 0.05f, 0.9f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _scorchMat = new StandardMaterial3D
        {
            AlbedoTexture = soft,
            AlbedoColor = new Color(0.08f, 0.07f, 0.06f, 0.95f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
    }

    static Texture2D SoftCircle()
    {
        var g = new Gradient();
        g.Offsets = new[] { 0f, 1f };
        g.Colors = new[] { new Color(1, 1, 1, 1), new Color(1, 1, 1, 0) };
        return new GradientTexture2D
        {
            Gradient = g, Width = 64, Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f),
        };
    }

    static QuadMesh Quad(Texture2D? tex) => new()
    {
        Size = Vector2.One,
        Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = tex,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
        },
    };

    static Gradient Ramp(params (float at, Color c)[] stops)
    {
        var g = new Gradient();
        g.Offsets = stops.Select(s => s.at).ToArray();
        g.Colors = stops.Select(s => s.c).ToArray();
        return g;
    }

    CpuParticles3D Burst(Vector3 pos, Vector3 dir, int amount, float life, float vMin, float vMax, float spread,
                         Vector3 gravity, float sMin, float sMax, Gradient ramp, Mesh mesh, float damping = 0f, Curve? scale = null)
    {
        // Local coords, positioned before entering the tree and only started after:
        // a particle node that starts emitting before it's moved spawns its burst at the origin.
        var p = new CpuParticles3D
        {
            Emitting = false, LocalCoords = true, Position = pos,
            Amount = amount, Lifetime = life, OneShot = true, Explosiveness = 1f,
            Direction = dir, Spread = spread,
            InitialVelocityMin = vMin, InitialVelocityMax = vMax,
            Gravity = gravity, DampingMin = damping, DampingMax = damping,
            ScaleAmountMin = sMin, ScaleAmountMax = sMax, ScaleAmountCurve = scale,
            ColorRamp = ramp, Mesh = mesh,
        };
        AddChild(p);
        p.Emitting = true;
        GetTree().CreateTimer(life + 0.5f).Timeout += p.QueueFree;
        return p;
    }

    public void Impact(Vector3 pos, Vector3 normal, bool silent)
    {
        Burst(pos, normal, silent ? 3 : 8, 0.9f, 1.5f, 4.5f, 30f, new Vector3(0, -5f, 0), 0.12f, 0.4f, _dustRamp, _dust, 2f);
        Mark(pos, normal, 0.06f);
        if (!silent) SoundWorld.I.Emit(Snd.Impact, pos);
    }

    public void Blood(Vector3 pos, Vector3 dir)
    {
        Burst(pos, dir, 7, 0.5f, 1f, 3f, 35f, new Vector3(0, -6f, 0), 0.08f, 0.22f, _bloodRamp, _dust, 3f);
    }

    public void Mark(Vector3 pos, Vector3 normal, float size, Node3D? parent = null, bool permanent = false, bool scorch = false)
    {
        var m = new MeshInstance3D
        {
            Mesh = _unit,
            MaterialOverride = scorch ? _scorchMat : _markMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        (parent ?? this).AddChild(m);
        var up = MathF.Abs(normal.Y) > 0.95f ? Vector3.Right : Vector3.Up;
        m.GlobalTransform = new Transform3D(Basis.LookingAt(-normal, up).Scaled(new Vector3(size, size, size)), pos + normal * 0.015f);
        if (permanent) return;
        _marks.Enqueue(m);
        if (_marks.Count > MaxMarks)
        {
            var old = _marks.Dequeue();
            if (IsInstanceValid(old)) old.QueueFree();
        }
    }

    public void MuzzleFlash(Vector3 pos, Vector3 dir, float scale = 1f)
    {
        var light = new OmniLight3D { LightColor = new Color(1f, 0.75f, 0.45f), LightEnergy = 2.5f * scale, OmniRange = 6f * scale };
        AddChild(light);
        light.GlobalPosition = pos;
        GetTree().CreateTimer(0.045f).Timeout += light.QueueFree;

        var flash = new MeshInstance3D { Mesh = _flash, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        flash.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoTexture = ((StandardMaterial3D)_flash.Material).AlbedoTexture,
            AlbedoColor = new Color(1f, 0.8f, 0.45f, 0.9f),
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        AddChild(flash);
        flash.GlobalPosition = pos + dir * 0.05f;
        flash.Scale = Vector3.One * (0.18f + GD.Randf() * 0.1f) * scale;
        if (scale > 2f) Burst(pos, dir, 16, 1.2f, 3f, 10f, 25f, new Vector3(0, 0.3f, 0), 0.8f, 2f, _smokeRamp, _smoke, 3f, _grow);
        GetTree().CreateTimer(0.035f).Timeout += flash.QueueFree;
    }

    /// <summary>Decoy flares: bright, hot, falling away from the aircraft in a spray.</summary>
    public void Flares(Vector3 pos, Vector3 vel)
    {
        _flareRamp ??= Ramp((0f, new Color(1f, 0.95f, 0.8f, 1f)), (0.5f, new Color(1f, 0.6f, 0.2f, 0.9f)), (1f, new Color(0.6f, 0.6f, 0.6f, 0f)));
        var p = Burst(pos, Vector3.Down, 24, 3f, 8f, 18f, 70f, new Vector3(0, -6f, 0), 0.5f, 1.1f, _flareRamp, _fire, 0.5f);
        p.Direction = (Vector3.Down + vel.Normalized() * -0.3f).Normalized();
        var light = new OmniLight3D { LightColor = new Color(1f, 0.8f, 0.5f), LightEnergy = 6f, OmniRange = 40f };
        AddChild(light);
        light.GlobalPosition = pos + Vector3.Down * 3f;
        GetTree().CreateTimer(1.5f).Timeout += light.QueueFree;
    }

    Gradient? _flareRamp;

    /// <summary>One puff of a rocket's smoke trail.</summary>
    public void Puff(Vector3 pos)
    {
        Burst(pos, Vector3.Up, 2, 1.6f, 0.1f, 0.5f, 180f, new Vector3(0, 0.2f, 0), 0.3f, 0.7f, _smokeRamp, _smoke, 1f, _grow);
    }

    /// <summary>A burning wreck: flames, then a long column of black smoke.</summary>
    /// <summary>A fire and its smoke column, which can be moved: a burning aircraft trails them as it falls.</summary>
    public sealed class Burning
    {
        public CpuParticles3D Fire = null!, Smoke = null!;
        /// <summary>Move the source; what's already been given off stays where it was (a trail).</summary>
        public void MoveTo(Vector3 pos)
        {
            if (IsInstanceValid(Fire)) Fire.Position = pos;
            if (IsInstanceValid(Smoke)) Smoke.Position = pos + Vector3.Up * 1.5f;
        }
    }

    public Burning Burn(Vector3 pos, float seconds)
    {
        CpuParticles3D Stream(int amount, float life, float vMin, float vMax, float sMin, float sMax, Gradient ramp, Mesh mesh)
        {
            var p = new CpuParticles3D
            {
                Emitting = false, LocalCoords = false, Position = pos, Amount = amount, Lifetime = life,
                Direction = Vector3.Up, Spread = 12f, InitialVelocityMin = vMin, InitialVelocityMax = vMax,
                Gravity = new Vector3(0.4f, 0.8f, 0f), ScaleAmountMin = sMin, ScaleAmountMax = sMax, ScaleAmountCurve = _grow,
                ColorRamp = ramp, Mesh = mesh, EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 1f,
            };
            AddChild(p);
            p.Emitting = true;
            return p;
        }
        var fire = Stream(40, 1.2f, 1f, 3f, 1f, 2.2f, _fireRamp, _fire);
        // Burning fuel and rubber: thick black smoke that climbs a long way before the wind
        // bends it over, so a wreck marks the battlefield from kilometres off.
        _plumeRamp ??= Ramp((0f, new Color(0.04f, 0.035f, 0.03f, 0.95f)), (0.35f, new Color(0.09f, 0.085f, 0.08f, 0.75f)),
                            (0.75f, new Color(0.18f, 0.18f, 0.18f, 0.35f)), (1f, new Color(0.3f, 0.3f, 0.3f, 0f)));
        if (_plumeGrow == null)
        {
            _plumeGrow = new Curve();
            _plumeGrow.AddPoint(new Vector2(0f, 0.3f));
            _plumeGrow.AddPoint(new Vector2(0.3f, 0.55f));
            _plumeGrow.AddPoint(new Vector2(1f, 1f));
        }
        var smoke = new CpuParticles3D
        {
            Emitting = false, LocalCoords = false, Position = pos + Vector3.Up * 1.5f, Amount = 260, Lifetime = 38f,
            Direction = Vector3.Up, Spread = 9f, InitialVelocityMin = 4f, InitialVelocityMax = 6.5f,
            Gravity = new Vector3(0.45f, 0.12f, 0.15f), DampingMin = 0.06f, DampingMax = 0.12f,
            ScaleAmountMin = 18f, ScaleAmountMax = 34f, ScaleAmountCurve = _plumeGrow,
            ColorRamp = _plumeRamp, Mesh = _smoke, EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 1.2f,
            VisibilityAabb = new Aabb(new Vector3(-150f, -10f, -150f), new Vector3(300f, 320f, 300f)),
        };
        AddChild(smoke);
        smoke.Emitting = true;
        _plumes.Enqueue(smoke);
        // Too many at once is a lot of overdraw: the oldest plume dies down first.
        while (_plumes.Count > 14)
        {
            var old = _plumes.Dequeue();
            if (IsInstanceValid(old)) old.Emitting = false;
        }
        float smokeFor = seconds * 3f; // the fire dies long before the smoke does
        var tree = GetTree();
        tree.CreateTimer(seconds).Timeout += () => { if (IsInstanceValid(fire)) fire.Emitting = false; };
        tree.CreateTimer(smokeFor).Timeout += () => { if (IsInstanceValid(smoke)) smoke.Emitting = false; };
        tree.CreateTimer(smokeFor + 40f).Timeout += () => { if (IsInstanceValid(fire)) fire.QueueFree(); if (IsInstanceValid(smoke)) smoke.QueueFree(); };
        return new Burning { Fire = fire, Smoke = smoke };
    }

    Gradient? _plumeRamp;
    Curve? _plumeGrow;
    readonly Queue<CpuParticles3D> _plumes = new();

    /// <summary>The dust a rifle kicks up off the ground in front of it — how you actually spot a distant shooter.</summary>
    public void MuzzleDust(Vector3 pos, Vector3 dir)
    {
        Burst(pos, dir, 10, 2.2f, 1f, 4f, 50f, new Vector3(0, 0.2f, 0), 0.6f, 1.5f, _muzzleDustRamp, _smoke, 2f, _grow);
    }

    /// <param name="radius">Crater radius in metres: ~0.9 for a hand grenade, ~1.8 for an RPG or mortar.</param>
    /// <param name="power">Explosive charge relative to a hand grenade: the fireball, smoke column, debris and flash scale with its cube root.</param>
    public void Explosion(Vector3 pos, Vector3 normal, float radius = 0.9f, float power = 1f)
    {
        float k = MathF.Cbrt(MathF.Max(power, 0.1f));
        var light = new OmniLight3D { LightColor = new Color(1f, 0.7f, 0.4f), LightEnergy = 14f * k, OmniRange = 35f * k };
        AddChild(light);
        light.GlobalPosition = pos + Vector3.Up * 1.5f * k;
        var tw = light.CreateTween();
        tw.TweenProperty(light, "light_energy", 0f, 0.3f * k);
        tw.TweenCallback(Callable.From(light.QueueFree));

        Burst(pos, Vector3.Up, (int)(18 * k), 0.35f * k, 5f * k, 16f * k, 180f, Vector3.Zero, 0.8f * k, 2.2f * k, _fireRamp, _fire, 20f);
        Burst(pos, Vector3.Up, (int)(26 * k), 6f * k, 2f * k, 8f * k, 70f, new Vector3(0, 0.5f, 0), 2.5f * k, 5f * k, _smokeRamp, _smoke, 2.5f, _grow);
        Burst(pos, Vector3.Up, (int)(30 * k * k), 2f * k, 6f * k, 18f * k, 60f, new Vector3(0, -9.8f, 0), 0.06f, 0.18f * k, _debrisRamp, _debris);
        if (Ground != null) Crater(pos, radius);
        else Mark(pos, normal, radius * 3.5f, permanent: true, scorch: true);
    }

    static IGround? _ground;
    CraterField? _craters;

    /// <summary>The map's ground. Setting it starts a fresh crater field on it.</summary>
    public static IGround? Ground
    {
        get => _ground;
        set { _ground = value; if (I != null) I._craters = null; }
    }

    public void Crater(Vector3 pos, float radius)
    {
        if (_craters == null || !IsInstanceValid(_craters))
        {
            _craters = new CraterField { Ground = Ground! };
            AddChild(_craters);
        }
        _craters.Blast(pos, radius);
    }
}
