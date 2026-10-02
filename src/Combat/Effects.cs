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

        BuildFlashPools();
        AddChild(new Illumination());

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

    public void Impact(Vector3 pos, Vector3 normal, bool silent, Surface surface = Surface.Earth)
    {
        Burst(pos, normal, silent ? 3 : 8, 0.9f, 1.5f, 4.5f, 30f, new Vector3(0, -5f, 0), 0.12f, 0.4f, _dustRamp, _dust, 2f);
        Mark(pos, normal, 0.06f);
        if (!silent) SoundWorld.I.Emit(SoundWorld.HitFor(surface), pos);
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

    /// <summary>
    /// A shot's flash: the glowing gas at the muzzle, and at night the light it throws on what's round the gun. Both come
    /// from pools, not a new node per round. The light is only worth drawing when it's dark enough to see it (by day the
    /// sun swamps a muzzle flash's light on the ground), and only so many at once: the nearest the camera win.
    /// <paramref name="ownView"/>: the flash of the weapon the camera's on (the player's, in first person). At night
    /// it's cut down to what a flash hider leaves the shooter: a hider (the M16A2's birdcage, the AK's slant-cut brake)
    /// breaks up and cools the gas so little of the flame shows, and keeping the firer's night vision is what it's for.
    /// Others looking at the same shot see the whole of it.
    /// </summary>
    public void MuzzleFlash(Vector3 pos, Vector3 dir, float scale = 1f, bool ownView = false)
    {
        float dark = 1f - Mathf.Clamp(Conditions.Light / 0.8f, 0f, 1f);
        float hidden = ownView ? dark : 0f;
        var sprite = _flashSprites[_nextSprite];
        _flashSpriteUntil[_nextSprite] = _time + 0.035;
        _nextSprite = (_nextSprite + 1) % _flashSprites.Length;
        sprite.Visible = true;
        // (Half a metre from the eye, the full flash covered a fifth of the view at night, and the light it threw
        // lit the weapon and the ground in front of it, dazzling him: he couldn't see what he was shooting at.)
        sprite.Transparency = hidden * 0.67f; // (0.9 opaque down to 0.3 on the darkest night)
        sprite.GlobalPosition = pos + dir * 0.05f;
        sprite.Scale = Vector3.One * (0.18f + GD.Randf() * 0.1f) * scale * Mathf.Lerp(1f, 0.3f, hidden);
        if (scale > 2f) Burst(pos, dir, 16, 1.2f, 3f, 10f, 25f, new Vector3(0, 0.3f, 0), 0.8f, 2f, _smokeRamp, _smoke, 3f, _grow);

        if (Conditions.Light > 0.8f) return;
        float d2 = pos.DistanceSquaredTo(CameraPos);
        int slot = -1;
        float worst = -1f;
        for (int i = 0; i < _flashLights.Length; i++)
        {
            if (_flashLightUntil[i] <= _time) { slot = i; break; }
            if (_flashLightD2[i] > worst) { worst = _flashLightD2[i]; slot = i; }
        }
        if (_flashLightUntil[slot] > _time && worst <= d2) { Prof.Count("flash:lights over budget"); return; }
        var l = _flashLights[slot];
        // A rifle's flash lights a few metres round the firer; a tank gun's the ground tens of metres off. Brighter
        // against a darker night (the eye's scale, see NightLight): a flash is ~a thousandth of a second, but the eye
        // holds it for a few hundredths.
        float own = Mathf.Lerp(1f, 0.15f, hidden);
        l.LightEnergy = 2.5f * scale * dark * own;
        l.OmniRange = 4.5f * MathF.Pow(scale, 1.4f) * Mathf.Lerp(1f, 0.6f, hidden);
        l.GlobalPosition = pos + dir * 0.3f;
        l.Visible = true;
        _flashLightUntil[slot] = _time + 0.045;
        _flashLightD2[slot] = d2;
        Prof.Count("flash:lights");
    }

    /// <summary>At most this many muzzle-flash lights at once.</summary>
    const int FlashLightBudget = 8;
    MeshInstance3D[] _flashSprites = null!;
    readonly double[] _flashSpriteUntil = new double[48];
    int _nextSprite;
    readonly OmniLight3D[] _flashLights = new OmniLight3D[FlashLightBudget];
    readonly double[] _flashLightUntil = new double[FlashLightBudget];
    readonly float[] _flashLightD2 = new float[FlashLightBudget];
    double _time;

    /// <summary>Where the eye is: the camera, or the listener when nothing's rendered.</summary>
    public Vector3 EyePos => CameraPos;
    Vector3 CameraPos => GetViewport().GetCamera3D() is { } cam ? cam.GlobalPosition : SoundWorld.I?.ListenerPos ?? Vector3.Zero;

    void BuildFlashPools()
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoTexture = ((StandardMaterial3D)_flash.Material).AlbedoTexture,
            AlbedoColor = new Color(1f, 0.8f, 0.45f, 0.9f),
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        };
        _flashSprites = new MeshInstance3D[_flashSpriteUntil.Length];
        for (int i = 0; i < _flashSprites.Length; i++)
        {
            _flashSprites[i] = new MeshInstance3D { Mesh = _flash, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = mat, Visible = false };
            AddChild(_flashSprites[i]);
        }
        for (int i = 0; i < _flashLights.Length; i++)
        {
            _flashLights[i] = new OmniLight3D { LightColor = new Color(1f, 0.75f, 0.45f), ShadowEnabled = false, Visible = false, OmniAttenuation = 2f };
            AddChild(_flashLights[i]);
        }
    }

    public override void _Process(double delta)
    {
        _time += delta;
        NightLight.ResolveShadows();
        for (int i = 0; i < _flashSprites.Length; i++)
            if (_flashSprites[i].Visible && _flashSpriteUntil[i] <= _time) _flashSprites[i].Visible = false;
        for (int i = 0; i < _flashLights.Length; i++)
            if (_flashLights[i].Visible && _flashLightUntil[i] <= _time) _flashLights[i].Visible = false;
        UpdateFires();
        DimByNight();
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

    /// <summary>A fire and its smoke column, which can be moved: a burning aircraft trails them as it falls.</summary>
    public sealed class Burning
    {
        public CpuParticles3D Fire = null!, Smoke = null!;
        public Vector3 Pos;
        public double Start, Out;
        public float Candela;
        public int LightId;
        public OmniLight3D Light = null!;
        public float Phase, PuffHz;
        /// <summary>Move the source; what's already been given off stays where it was (a trail).</summary>
        public void MoveTo(Vector3 pos)
        {
            Pos = pos;
            if (IsInstanceValid(Fire)) Fire.Position = pos;
            if (IsInstanceValid(Smoke)) Smoke.Position = pos + Vector3.Up * 1.5f;
            Conditions.MoveLight(LightId, pos + Vector3.Up * 1.5f);
        }
    }

    readonly List<Burning> _fires = new();
    /// <summary>At most this many fires cast light on the scene at once (the nearest the camera); every one still lights the senses (Conditions).</summary>
    const int FireLightBudget = 8;
    double _fireSortAt;

    /// <summary>
    /// A burning wreck: flames, then a long column of black smoke, and at night a light that flickers over the ground
    /// round it. <paramref name="candela"/>: how much light the flames give, as registered for everyone's eyes (a car
    /// about 20 000 cd). Big fires pulse at about 1.5/√D Hz for a fire D metres across (Cetegen &amp; Ahmed 1993): under
    /// once a second for a burning vehicle, with faster turbulent flicker on top.
    /// </summary>
    public Burning Burn(Vector3 pos, float seconds, float candela = 20000f)
    {
        CpuParticles3D Stream(int amount, float life, float vMin, float vMax, float sMin, float sMax, Gradient ramp, Mesh mesh)
        {
            var p = new CpuParticles3D
            {
                Emitting = false, LocalCoords = false, Position = pos, Amount = amount, Lifetime = life,
                Direction = Vector3.Up, Spread = 12f, InitialVelocityMin = vMin, InitialVelocityMax = vMax,
                Gravity = new Vector3(0.4f, 0.8f, 0f), ScaleAmountMin = sMin, ScaleAmountMax = sMax, ScaleAmountCurve = _grow,
                ColorRamp = ramp, Mesh = mesh, EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = 1f,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, // (its own light would shine through it)
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
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
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
        double now = Clock.Now;
        var b = new Burning
        {
            Fire = fire, Smoke = smoke, Pos = pos, Start = now, Out = now + seconds, Candela = candela, Phase = GD.Randf() * Mathf.Tau,
            PuffHz = 1.5f / MathF.Sqrt(3f), // a vehicle fire ~3 m across
            LightId = Conditions.AddLight(pos + Vector3.Up * 1.5f, candela, now + seconds),
            Light = new OmniLight3D { LightColor = new Color(1f, 0.62f, 0.3f), ShadowEnabled = false, Visible = false },
        };
        AddChild(b.Light);
        _fires.Add(b);
        Prof.Count("fire:lights registered");
        return b;
    }

    /// <summary>
    /// The fires' light: dying down over the last third of the burn (the fuel going), pulsing and flickering, and drawn
    /// for the nearest few only. What everyone sees by (Conditions) is kept to the fire's output as it dies down.
    /// </summary>
    void UpdateFires()
    {
        if (_fires.Count == 0) return;
        double now = Clock.Now;
        var cam = CameraPos;
        bool sort = now >= _fireSortAt;
        if (sort)
        {
            _fireSortAt = now + 0.5;
            _fires.Sort((a, b) => a.Pos.DistanceSquaredTo(cam).CompareTo(b.Pos.DistanceSquaredTo(cam)));
        }
        for (int i = _fires.Count - 1; i >= 0; i--)
        {
            var f = _fires[i];
            if (now >= f.Out)
            {
                Conditions.RemoveLight(f.LightId);
                if (IsInstanceValid(f.Light)) f.Light.QueueFree();
                _fires.RemoveAt(i);
                continue;
            }
            float t = (float)(now - f.Start), left = (float)(f.Out - now), span = (float)(f.Out - f.Start);
            float output = Mathf.Clamp(t / 3f, 0f, 1f) * Mathf.Clamp(left / (span * 0.33f), 0f, 1f);
            // Re-registered as it dies down (a light's intensity is fixed when it's added), a step at a time.
            if (sort && output < 0.97f)
            {
                Conditions.RemoveLight(f.LightId);
                f.LightId = Conditions.AddLight(f.Pos + Vector3.Up * 1.5f, f.Candela * output, f.Out);
            }
            if (i >= FireLightBudget) { f.Light.Visible = false; continue; }
            float flicker = 1f + 0.22f * MathF.Sin(t * Mathf.Tau * f.PuffHz + f.Phase)
                               + 0.1f * MathF.Sin(t * 13.7f + f.Phase * 3f) + 0.07f * MathF.Sin(t * 23.1f + f.Phase * 5f);
            // Drawn from the middle of the flames (a vehicle fire's flame is a few metres tall, its light centred about
            // 2.5 m up: Heskestad's flame height), so a shadow doesn't start inside the hull.
            f.Light.GlobalPosition = f.Pos + Vector3.Up * (2.5f + 0.3f * MathF.Sin(t * 5.3f + f.Phase));
            NightLight.Apply(f.Light, f.Candela * output, 3f, 60f, Conditions.Lux, flicker);
            NightLight.Offer(f.Light, f.Candela * output, cam);
        }
    }

    readonly List<StandardMaterial3D> _litByDay = new();
    float _dim = -1f;

    /// <summary>
    /// Dust, smoke and blood are drawn unshaded (lighting every particle would cost too much), so they'd glow grey on
    /// a moonless night. They're darkened to the sky's light as the eye takes it (see NightLight); the flames aren't,
    /// they give their own light.
    /// </summary>
    void DimByNight()
    {
        float dim = MathF.Max(0.02f, MathF.Pow(Conditions.Light, 2.2f));
        if (MathF.Abs(dim - _dim) < 0.005f) return;
        _dim = dim;
        if (_litByDay.Count == 0)
            foreach (var q in new[] { _dust, _smoke, _debris })
                if (q.Material is StandardMaterial3D m) _litByDay.Add(m);
        foreach (var m in _litByDay) m.AlbedoColor = new Color(dim, dim, dim, 1f);
    }

    Gradient? _screenRamp;

    /// <summary>A smoke screen: thick, pale, low and wide, lingering for most of a minute.</summary>
    public void SmokeCloud(Vector3 pos, float radius, float seconds)
    {
        _screenRamp ??= Ramp((0f, new Color(0.78f, 0.78f, 0.76f, 0f)), (0.08f, new Color(0.8f, 0.8f, 0.78f, 0.9f)),
                             (0.7f, new Color(0.74f, 0.74f, 0.72f, 0.8f)), (1f, new Color(0.7f, 0.7f, 0.68f, 0f)));
        var p = new CpuParticles3D
        {
            Emitting = false, LocalCoords = false, Position = pos + Vector3.Up * 1f, Amount = 70, Lifetime = 14f,
            Direction = Vector3.Up, Spread = 80f, InitialVelocityMin = 0.5f, InitialVelocityMax = radius * 0.28f,
            Gravity = new Vector3(0.25f, 0.18f, 0.1f), DampingMin = 0.4f, DampingMax = 0.9f,
            ScaleAmountMin = radius * 0.7f, ScaleAmountMax = radius * 1.2f, ScaleAmountCurve = _grow,
            ColorRamp = _screenRamp, Mesh = _smoke, EmissionShape = CpuParticles3D.EmissionShapeEnum.Sphere, EmissionSphereRadius = radius * 0.4f,
            VisibilityAabb = new Aabb(new Vector3(-60f, -10f, -60f), new Vector3(120f, 60f, 120f)),
        };
        AddChild(p);
        p.Emitting = true;
        var tree = GetTree();
        tree.CreateTimer(MathF.Max(1f, seconds - 12f)).Timeout += () => { if (IsInstanceValid(p)) p.Emitting = false; };
        tree.CreateTimer(seconds + 16f).Timeout += () => { if (IsInstanceValid(p)) p.QueueFree(); };
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
