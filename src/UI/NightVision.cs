using Godot;

namespace Ridgeline;

/// <summary>
/// The player's night vision goggles, as the picture looks through an image-intensifier tube (a PVS-14 class
/// monocular):
/// - one colour, the green of the P43 phosphor screen, and a little softer than the eye by day (the tube resolves
///   about 64 line pairs per mm);
/// - its gain is set for the whole scene (the automatic brightness control), so a dark field comes up to a usable
///   picture and a bright one (a flare, a fire, a lit street) is turned down, leaving the dark round it black;
/// - bright points bloom into halos;
/// - the fewer photons, the more it scintillates: grainy by starlight, cleaner by moonlight;
/// - it shows a 40° circle; outside it, the other eye's view of the dark.
/// The camera's exposure is raised while they're down (the tube amplifies before anything reaches the eye, so the
/// darks come up with their detail rather than as a stretched 8-bit picture), and the shader does the rest.
/// </summary>
public partial class NightVision : Control
{
    const string ShaderCode = @"
shader_type canvas_item;
uniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;
uniform float on = 0.0;
uniform float t = 0.0;
uniform float starve = 0.0;
uniform float radius = 0.5;
uniform float aspect = 1.7778;
uniform float gain_max = 40.0;

float lum(vec3 c) { return dot(c, vec3(0.2126, 0.7152, 0.0722)); }
float hash(vec2 p) {
    p = mod(p, 1024.0);
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

void fragment() {
    vec2 uv = SCREEN_UV;
    vec3 raw = texture(screen, uv).rgb;
    // Automatic brightness control: the gain comes from the mean of the whole picture (the top of the mip chain).
    float mean = lum(textureLod(screen, vec2(0.5), 10.0).rgb);
    float g = clamp(0.28 / max(mean, 0.0004), 1.0, gain_max);
    float l = lum(textureLod(screen, uv, 0.9).rgb) * g;
    // Halo round whatever is over-bright for the tube.
    float halo = max(lum(textureLod(screen, uv, 4.0).rgb) * g - 0.75, 0.0) * 0.9
               + max(lum(textureLod(screen, uv, 6.0).rgb) * g - 0.9, 0.0) * 0.6;
    float v = l + halo;
    v = v / (1.0 + 0.3 * v); // the phosphor screen saturates
    // Scintillation: shot noise, the worse the fewer photons.
    vec2 px = floor(uv * vec2(aspect, 1.0) * 520.0);
    float n = hash(px + mod(floor(t * 30.0), 97.0) * vec2(17.0, 59.0)) - 0.5;
    v = max(v + n * (0.05 + 0.3 * starve) * (0.35 + sqrt(max(v, 0.0))), 0.0);
    vec3 tube = vec3(0.32, 1.0, 0.45) * v + vec3(0.0, 0.015, 0.005);
    // The tube's circle, dimmer towards its rim; outside it the naked eye, adapted to the dark but dazzled by the tube.
    vec2 p = (uv - 0.5) * vec2(aspect, 1.0);
    float r = length(p) / radius;
    float inside = 1.0 - smoothstep(0.93, 1.0, r);
    tube *= (1.0 - 0.4 * r * r) * inside;
    vec3 other = raw * 0.25 * (1.0 - inside);
    COLOR = vec4(mix(raw, tube + other, on), 1.0);
}";

    /// <summary>The player's own (head) camera: used only when the viewport has no current camera.</summary>
    public Camera3D? Cam;
    /// <summary>
    /// The camera the boost is on: whichever one is being looked through (the head, a vehicle's sight or camera, a
    /// drone's). (It was always the head camera, which isn't the one on screen in a vehicle: the shader then had to
    /// stretch an un-boosted 8-bit night picture by up to 40 times, all banding and noise.)
    /// </summary>
    Camera3D? _boosted;
    /// <summary>Down (true) or up.</summary>
    public bool Down;

    /// <summary>How much the camera's exposure goes up with them down: about three stops, the rest is the shader's gain.</summary>
    const float ExposureBoost = 8f;

    ColorRect _rect = null!;
    ShaderMaterial _mat = null!;
    CameraAttributesPractical? _attrs;
    float _t, _k;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _mat = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        _rect = new ColorRect { Material = _mat, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_rect);
    }

    public override void _ExitTree() => Exposure(1f);

    /// <summary>The tube's field of view: 40° (PVS-14/PVS-31 class).</summary>
    const float TubeFovDeg = 40f;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        // Flipping them down or up takes a moment; the tube comes up to brightness in a fraction of a second.
        _k = Mathf.MoveToward(_k, Down ? 1f : 0f, dt * 3f);
        _rect.Visible = _k > 0.001f;
        var cam = GetViewport()?.GetCamera3D() ?? Cam;
        if (cam != _boosted)
        {
            // Off the camera it was on (if it's still ours there), onto the one now looked through.
            Exposure(1f);
            _boosted = cam;
        }
        float eyeL = cam != null && IsInstanceValid(cam) ? NightGear.LightOf(Conditions.LuxAt(cam.GlobalPosition)) : Conditions.Light;
        // The full boost from moonlight down; none by twilight (the tube's own brightness control is turned right down).
        Exposure(_k > 0.5f ? Mathf.Lerp(1f, ExposureBoost, Mathf.Clamp((0.55f - eyeL) / 0.3f, 0f, 1f)) : 1f);
        if (!_rect.Visible) return;
        var size = GetViewportRect().Size;
        _mat.SetShaderParameter("on", Mathf.SmoothStep(0f, 1f, _k));
        _mat.SetShaderParameter("t", _t);
        // Starved by starlight (light 0.08) or an overcast night, well fed by a moon (0.45).
        _mat.SetShaderParameter("starve", Mathf.Clamp(1f - eyeL / 0.45f, 0f, 1f));
        _mat.SetShaderParameter("aspect", size.X / MathF.Max(1f, size.Y));
        // The tube's 40° circle as it falls in this view (Godot's Fov is vertical, and the shader works in screen
        // heights): about 0.22 of the screen's height in radius at the 80° of the naked eye, filling more of it
        // through a magnified sight. (It filled the whole height, an 80° tube: twice what the bots get.)
        if (cam != null && IsInstanceValid(cam))
            _mat.SetShaderParameter("radius", 0.5f * MathF.Tan(Mathf.DegToRad(TubeFovDeg * 0.5f)) / MathF.Tan(Mathf.DegToRad(cam.Fov * 0.5f)));
    }

    /// <summary>
    /// Raise the camera's exposure by <paramref name="boost"/> over whatever the world's camera attributes say (copied
    /// every frame, so a change there, such as the lighting's own night exposure, carries through).
    /// </summary>
    void Exposure(float boost)
    {
        var cam = _boosted;
        if (cam == null || !IsInstanceValid(cam)) return;
        if (boost <= 1.01f)
        {
            if (_attrs != null && cam.Attributes == _attrs) cam.Attributes = null;
            return;
        }
        var world = cam.GetWorld3D()?.CameraAttributes;
        if (world is CameraAttributesPhysical) return; // physical exposure: the shader's gain does it all
        if (cam.Attributes != null && cam.Attributes != _attrs) return; // someone else's (a sight's): leave it
        _attrs ??= new CameraAttributesPractical();
        if (world is CameraAttributesPractical w)
        {
            _attrs.ExposureSensitivity = w.ExposureSensitivity;
            _attrs.AutoExposureEnabled = w.AutoExposureEnabled;
            _attrs.AutoExposureScale = w.AutoExposureScale;
            _attrs.AutoExposureSpeed = w.AutoExposureSpeed;
            _attrs.AutoExposureMinSensitivity = w.AutoExposureMinSensitivity;
            _attrs.AutoExposureMaxSensitivity = w.AutoExposureMaxSensitivity;
            _attrs.ExposureMultiplier = w.ExposureMultiplier * boost;
        }
        else _attrs.ExposureMultiplier = boost;
        cam.Attributes = _attrs;
    }
}
