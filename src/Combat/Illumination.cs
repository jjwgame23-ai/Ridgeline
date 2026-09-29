using Godot;

namespace Ridgeline;

/// <summary>
/// Illumination: a candle hanging under a parachute, thrown out of a mortar round over the target by its time fuze.
/// - It comes down at about 5 m/s under the canopy and drifts with the wind at its height (stronger aloft), so the
///   crew aim the burst upwind of the target and let it drift over.
/// - An 81 mm candle (M853A1) is about 600 000 cd for about a minute: ~7 lux on the ground 300 m below, as light as
///   the end of civil twilight, and still a few times a full moon's half a kilometre off. It's registered with
///   <see cref="Conditions"/>, so everyone's eyes see by it, and drawn as a light and the dazzling point of the candle.
/// </summary>
public partial class Illumination : Node3D
{
    public static Illumination? I { get; private set; }
    /// <summary>Candles lit this match.</summary>
    public static int Lit;

    /// <summary>Descent under the parachute (m/s), and how fast the canopy takes the shell's way off the candle (s).</summary>
    const float Descent = 5f, ChuteTau = 1.5f;

    sealed class Candle
    {
        public Vector3 Pos, Vel;
        public double Lit, Out;
        public float Cd;
        public int LightId;
        public OmniLight3D Light = null!;
        public MeshInstance3D Glow = null!;
        public float Phase;
    }

    readonly List<Candle> _candles = new();
    static readonly Dictionary<Vehicle, float> _fuzes = new();
    QuadMesh _quad = null!;
    ShaderMaterial _glow = null!;

    /// <summary>The time the crew set on the next round's fuze (s after firing).</summary>
    public static void SetFuze(Vehicle tube, float seconds) => _fuzes[tube] = seconds;

    /// <summary>The fuze setting on the round leaving this tube; 0 if none was set (it functions at the top of its flight).</summary>
    public static float TakeFuze(Vehicle? tube) => tube != null && _fuzes.Remove(tube, out float s) ? s : 0f;

    /// <summary>
    /// A point of light that stays a point: a soft disc turned to the camera, never smaller than the glare round a
    /// dazzling source (a few milliradians) however far off, dimmed by the air between (Allard's law).
    /// </summary>
    const string GlowShader = @"
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled, skip_vertex_transform;
uniform float energy = 8.0;
uniform float size = 3.0;
uniform float min_angle = 0.02;
uniform float visibility = 20000.0;
uniform vec3 tint : source_color = vec3(1.0, 0.95, 0.85);
instance uniform float glow = 1.0;
varying vec2 q;
varying float fade;
void vertex() {
    vec3 c = MODEL_MATRIX[3].xyz;
    float d = length(c - INV_VIEW_MATRIX[3].xyz);
    float s = max(size, d * min_angle);
    q = VERTEX.xy * 2.0;
    VERTEX = (VIEW_MATRIX * vec4(c, 1.0)).xyz + vec3(VERTEX.xy * s, 0.0);
    NORMAL = vec3(0.0, 0.0, 1.0);
    fade = exp(-3.912 * d / visibility);
}
void fragment() {
    float r2 = dot(q, q);
    // The candle itself, and the glare round it.
    float a = exp(-r2 * 40.0) + 0.35 * exp(-r2 * 5.0);
    ALBEDO = tint * energy * glow * fade * a;
    ALPHA = clamp(a, 0.0, 1.0);
}
";

    public override void _EnterTree()
    {
        I = this;
        Lit = 0;
        _fuzes.Clear();
    }

    public override void _ExitTree()
    {
        if (I == this) I = null;
    }

    public override void _Ready()
    {
        _quad = new QuadMesh { Size = Vector2.One };
        _glow = new ShaderMaterial { Shader = new Shader { Code = GlowShader } };
    }

    /// <summary>The fuze has functioned at <paramref name="pos"/>: out comes the candle, moving with the shell.</summary>
    public void Deploy(Vector3 pos, Vector3 vel, float candela, float burnS)
    {
        Lit++;
        Prof.Count("illum:candles lit");
        double now = Clock.Now;
        var c = new Candle { Pos = pos, Vel = vel, Cd = candela, Lit = now, Out = now + burnS, Phase = GD.Randf() * Mathf.Tau };
        c.LightId = Conditions.AddLight(pos, candela, c.Out);
        // Magnesium and sodium nitrate: a hard, slightly yellow white.
        c.Light = new OmniLight3D { LightColor = new Color(1f, 0.94f, 0.82f), ShadowEnabled = false, Visible = false };
        AddChild(c.Light);
        c.Glow = new MeshInstance3D { Mesh = _quad, MaterialOverride = _glow, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        c.Glow.CustomAabb = new Aabb(new Vector3(-50f, -50f, -50f), new Vector3(100f, 100f, 100f));
        AddChild(c.Glow);
        _candles.Add(c);
        Place(c, 0f);
    }

    float _energy = -1f, _vis = -1f;

    public override void _Process(double delta)
    {
        if (_candles.Count == 0) return;
        float dt = (float)delta;
        float energy = Mathf.Lerp(14f, 2f, Conditions.Light);
        if (MathF.Abs(energy - _energy) > 0.05f) { _energy = energy; _glow.SetShaderParameter("energy", energy); }
        if (MathF.Abs(Conditions.VisibilityM - _vis) > 1f) { _vis = Conditions.VisibilityM; _glow.SetShaderParameter("visibility", _vis); }
        double now = Clock.Now;
        for (int i = _candles.Count - 1; i >= 0; i--)
        {
            var c = _candles[i];
            if (now >= c.Out)
            {
                Conditions.RemoveLight(c.LightId);
                c.Light.QueueFree();
                c.Glow.QueueFree();
                _candles[i] = _candles[^1];
                _candles.RemoveAt(_candles.Count - 1);
                continue;
            }
            float ground = Effects.Ground?.HeightAt(c.Pos.X, c.Pos.Z) ?? 0f;
            float agl = c.Pos.Y - ground;
            if (agl > 0.3f)
            {
                // The canopy takes the shell's way off it, and it hangs in the wind, coming down steadily.
                var want = NightLight.WindAt(agl) + Vector3.Down * Descent;
                c.Vel += (want - c.Vel) * MathF.Min(1f, dt / ChuteTau);
                c.Pos += c.Vel * dt;
            }
            else
            {
                // Fuzed too low: it burns out on the ground where it came down.
                c.Vel = Vector3.Zero;
                c.Pos = c.Pos with { Y = ground + 0.3f };
            }
            Place(c, (float)(now - c.Lit));
        }
    }

    void Place(Candle c, float age)
    {
        float left = (float)(c.Out - Clock.Now);
        // It takes a second to catch; the last few seconds it gutters out. In between a magnesium candle burns steadily,
        // with a little flicker, and swings under its canopy.
        float burn = Mathf.Clamp(age / 1f, 0f, 1f) * Mathf.Clamp(left / 3f, 0f, 1f);
        float flicker = 1f + 0.04f * MathF.Sin(age * 23f + c.Phase) + 0.03f * MathF.Sin(age * 37f + c.Phase * 2f);
        var swing = new Vector3(MathF.Sin(age * 1.8f + c.Phase), 0f, MathF.Cos(age * 1.5f + c.Phase)) * 1.2f;
        var at = c.Pos + swing;
        Conditions.MoveLight(c.LightId, at);
        float agl = at.Y - (Effects.Ground?.HeightAt(at.X, at.Z) ?? 0f);
        c.Light.GlobalPosition = at;
        // Fitted over the ground it's lighting: from straight below out to a few times its height.
        NightLight.Apply(c.Light, c.Cd * burn, MathF.Max(40f, agl), MathF.Max(400f, agl * 3f), Conditions.Lux, flicker);
        c.Glow.GlobalPosition = at;
        c.Glow.SetInstanceShaderParameter("glow", burn * flicker);
    }
}
