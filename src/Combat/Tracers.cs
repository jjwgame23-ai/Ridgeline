using Godot;

namespace Ridgeline;

/// <summary>
/// Tracer ammunition and how it's drawn.
/// - Which rounds are tracers is the load: a machine-gun belt is linked four ball to one tracer, every cannon round
///   carries its own, rifles are loaded with ball (see the weapon definitions). A tracer's compound burns for a fixed
///   time, so the published trace range (flat fire) is turned into a burn time with the round's own drag, and a round
///   fired steeply up burns as long and goes less far. After it burns out the round flies on unseen.
/// - Colour is the compound: NATO tracers burn red (strontium salts), Russian-pattern ones green (barium salts); BRAVO
///   is Russian-equipped.
/// - What the eye sees of a tracer is a streak, not a dot: the head is brighter than anything round it and the eye
///   holds it for a few hundredths of a second, so it smears along its path by its speed times that. They're drawn as
///   one batch of streaks from the live projectile list, turned to face the camera round their own axis.
/// </summary>
public static class Tracers
{
    public readonly record struct Spec(int Every, float BurnS, float IllumCd, float IllumS);

    static readonly Dictionary<string, Spec> _specs = new();
    static bool _infantry;

    /// <summary>A vehicle weapon's load (called as each vehicle type is defined).</summary>
    public static void Register(VWeapon w) => Add(w.Name, w.TracerEvery, w.TraceM, w.Speed, w.Drag, w.IllumCd, w.IllumS);

    static void Add(string name, int every, float traceM, float speed, float drag, float illumCd = 0f, float illumS = 0f)
    {
        if (every <= 0 && illumCd <= 0f) return;
        _specs[name] = new Spec(every, every > 0 ? BurnTime(traceM, speed, drag) : 0f, illumCd, illumS);
    }

    /// <summary>What's in this weapon's load, by the name it's fired under; false for ball only.</summary>
    public static bool Lookup(string weapon, out Spec spec)
    {
        if (!_infantry)
        {
            _infantry = true;
            foreach (var d in WeaponDef.All) Add(d.Name, d.TracerEvery, d.TraceM, d.MuzzleVel, d.Drag);
        }
        return _specs.TryGetValue(weapon, out spec);
    }

    /// <summary>
    /// How long a round takes to fly <paramref name="range"/> metres in flat fire under quadratic drag (dv/dt = -k v²,
    /// so x(t) = ln(1 + k v0 t) / k): the time its tracer burns.
    /// </summary>
    static float BurnTime(float range, float v0, float k) =>
        k < 1e-7f ? range / MathF.Max(v0, 1f) : (MathF.Exp(k * range) - 1f) / (k * MathF.Max(v0, 1f));

    /// <summary>The colour a side's tracers burn: team 1 (BRAVO) Russian-pattern green, the others NATO red.</summary>
    public static Color ColorFor(int team) => team == 1 ? new Color(0.35f, 1f, 0.3f) : new Color(1f, 0.32f, 0.1f);

    /// <summary>How long the eye holds a bright point moving across it (its integration time, ~40 ms): the streak's length is the speed times this.</summary>
    public const float Persistence = 0.04f;

    // ---------------------------------------------------------------- drawing

    /// <summary>
    /// One quad per streak, laid along the round's path and turned to face the camera round that axis in the vertex
    /// shader. The head is the transform's origin; the custom data is the vector back to the tail and the physical
    /// width of the glow. Never narrower than about a pixel however far off: a tracer is a point source, and it's seen
    /// at any range its light gets through (Allard's law: exp(-σd) with the meteorological visibility's σ = 3.912/V).
    /// Additive, unshaded, and out of the scene's fog, which is applied here as the light's own transmission instead.
    /// </summary>
    public const string StreakShader = @"
shader_type spatial;
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled, skip_vertex_transform;
uniform float energy = 1.0;
uniform float visibility = 20000.0;
uniform float min_angle = 0.0014;
varying float along;
varying float across;
varying float fade;
varying vec4 tint;
void vertex() {
    vec3 head = MODEL_MATRIX[3].xyz;
    vec3 tail = INSTANCE_CUSTOM.xyz;
    vec3 cam = INV_VIEW_MATRIX[3].xyz;
    vec3 view = head - cam;
    float dist = length(view);
    float w = max(INSTANCE_CUSTOM.w, dist * min_angle);
    vec3 side = cross(tail, view);
    float sl = length(side);
    side = sl > 1e-6 ? side / sl : INV_VIEW_MATRIX[0].xyz;
    along = VERTEX.y + 0.5;
    across = VERTEX.x * 2.0;
    // A little past the head as well, so the head is a round glow and not a cut end.
    vec3 axis = length(tail) > 1e-4 ? normalize(tail) : vec3(0.0);
    vec3 world = head + tail * (1.0 - along) - axis * w * 0.5 * along + side * VERTEX.x * w;
    fade = exp(-3.912 * dist / visibility);
    tint = COLOR;
    VERTEX = (VIEW_MATRIX * vec4(world, 1.0)).xyz;
    NORMAL = vec3(0.0, 0.0, 1.0);
}
void fragment() {
    float core = 1.0 - abs(across);
    float a = core * core * along * along * tint.a;
    ALBEDO = mix(tint.rgb, vec3(1.0), core * core * along * 0.3) * energy * fade;
    ALPHA = clamp(a, 0.0, 1.0);
}
";
}
