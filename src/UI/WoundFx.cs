using Godot;

namespace Ridgeline;

/// <summary>
/// What your wounds do to what you see and hear:
/// - blood loss drains the colour out of the world and closes in the edges, and you
///   hear your heart, faster and louder the worse it gets;
/// - pain and shock blur things a little;
/// - a knock to the head (a grazing hit, a close blast) sets off flashes of light and
///   leaves your sight swimming for a while;
/// - down, the world goes grey and dark, and it's just you and your heartbeat.
/// </summary>
public partial class WoundFx : Control
{
    const string ShaderCode = @"
shader_type canvas_item;
uniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;
uniform float gray = 0.0;
uniform float dark = 0.0;
uniform float red = 0.0;
uniform float blur = 0.0;
uniform float wobble = 0.0;
uniform float t = 0.0;
void fragment() {
    vec2 uv = SCREEN_UV;
    uv += vec2(sin(t * 1.3 + uv.y * 6.0), cos(t * 1.1 + uv.x * 5.0)) * 0.004 * wobble;
    vec3 c = textureLod(screen, uv, blur).rgb;
    // Seeing double when concussed: a faint offset copy.
    c = mix(c, textureLod(screen, uv + vec2(0.012, 0.004) * wobble, blur).rgb, 0.35 * wobble);
    float l = dot(c, vec3(0.299, 0.587, 0.114));
    c = mix(c, vec3(l), gray);
    float v = distance(uv, vec2(0.5));
    c *= 1.0 - dark * smoothstep(0.15, 0.75, v);
    c = mix(c, c * vec3(1.0, 0.45, 0.4), red * smoothstep(0.25, 0.8, v));
    COLOR = vec4(c, 1.0);
}";

    ColorRect _rect = null!;
    ShaderMaterial _mat = null!;
    AudioStreamPlayer _heart = null!;
    Label _status = null!;
    readonly List<(Vector2 Pos, float R, float Life, float Age)> _flashes = new();
    readonly RandomNumberGenerator _rng = new();
    double _nextBeat;
    float _t;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _mat = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        _rect = new ColorRect { Material = _mat, MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        _rect.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_rect);
        _heart = new AudioStreamPlayer { Stream = SoundSynth.Heartbeat(), Bus = "Ear" };
        AddChild(_heart);

        _status = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _status.AddThemeFontSizeOverride("font_size", 15);
        _status.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _status.AddThemeConstantOverride("outline_size", 5);
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.AnchorLeft = 0.2f; _status.AnchorRight = 0.8f;
        _status.AnchorTop = 1f; _status.AnchorBottom = 1f;
        _status.OffsetTop = -64;
        AddChild(_status);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _t += dt;
        var p = Hud.I?.P;
        bool live = p != null && IsInstanceValid(p) && !p.Body.Dead;
        if (!live)
        {
            _rect.Visible = false;
            _status.Text = "";
            _flashes.Clear();
            QueueRedraw();
            return;
        }
        var b = p!.Body;
        float shock = b.Shock, down = b.Down ? 1f : 0f;
        float gray = MathF.Max(Mathf.SmoothStep(0.1f, 0.9f, shock) * 0.85f, down * 0.8f);
        float dark = MathF.Max(shock * 0.6f, down * 0.75f);
        float wobble = MathF.Min(1f, b.Concussion * 1.2f);
        float blur = MathF.Max(wobble * 1.2f, shock * 0.55f + down * 1.5f);
        float beatPulse = MathF.Max(0f, 1f - (float)(_nextBeat - Clock.Now) * 3f);
        _rect.Visible = gray > 0.01f || dark > 0.01f || blur > 0.05f;
        _mat.SetShaderParameter("gray", gray);
        _mat.SetShaderParameter("dark", dark);
        _mat.SetShaderParameter("red", MathF.Max(shock - 0.3f, 0f) * (0.4f + 0.6f * beatPulse));
        _mat.SetShaderParameter("blur", blur);
        _mat.SetShaderParameter("wobble", wobble);
        _mat.SetShaderParameter("t", _t);

        // Heartbeat: from about a quarter of your blood gone, or down.
        if ((shock > 0.25f || b.Down) && Clock.Now >= _nextBeat)
        {
            float bpm = 75f + 75f * shock + (b.Down ? 20f : 0f);
            _nextBeat = Clock.Now + 60.0 / bpm;
            _heart.VolumeDb = -30f + 20f * MathF.Max(shock, down * 0.8f);
            _heart.Play();
        }

        // Phosphenes: bright blotches when the head's been rattled.
        if (b.Concussion > 0.05f && _rng.Randf() < b.Concussion * dt * 6f)
        {
            var size = GetViewportRect().Size;
            _flashes.Add((new Vector2(_rng.Randf() * size.X, _rng.Randf() * size.Y), _rng.RandfRange(30f, 140f) * (0.5f + b.Concussion), _rng.RandfRange(0.12f, 0.45f), 0f));
        }
        for (int i = _flashes.Count - 1; i >= 0; i--)
        {
            var f = _flashes[i];
            f.Age += dt;
            if (f.Age > f.Life) _flashes.RemoveAt(i);
            else _flashes[i] = f;
        }
        QueueRedraw();

        _status.Text = b.Down
            ? $"YOU'RE DOWN — {b.Summary()}\nbleeding out: {BleedOut(b)} · [{Controls.Keys("selfaid")}] bandage yourself · wait for a medic · hold [{Controls.Keys("jump")}] to give up"
            : b.Wounds.Count == 0 ? ""
            : $"{b.Summary()}{(b.NeedsSelfAid ? $" — BLEEDING ({BleedOut(b)}) · [{Controls.Keys("selfaid")}] bandage" : b.Bleeding > 0.0005f ? " — still bleeding inside: find a medic" : "")}";
    }

    static string BleedOut(Body b)
    {
        float rate = b.Bleeding;
        if (rate < 1e-4f) return "stable";
        float s = (b.Blood - Body.DeadBlood) / rate;
        return s > 120f ? "slowly" : $"~{s:0}s";
    }

    public override void _Draw()
    {
        foreach (var f in _flashes)
        {
            float k = f.Age / f.Life;
            float a = (k < 0.2f ? k / 0.2f : 1f - (k - 0.2f) / 0.8f) * 0.55f;
            for (int ring = 0; ring < 4; ring++)
                DrawCircle(f.Pos, f.R * (1f - ring * 0.22f), new Color(1f, 0.97f, 0.85f, a * 0.25f));
        }
    }
}
