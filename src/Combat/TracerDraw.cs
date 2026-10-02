using Godot;

namespace Ridgeline;

/// <summary>
/// Every tracer in flight, drawn as one batch of streaks (see <see cref="Tracers"/>): no node per round. The buffer
/// is rebuilt from the live projectile list each frame and handed to the renderer in one call.
/// </summary>
public partial class TracerDraw : MultiMeshInstance3D
{
    MultiMesh _mm = null!;
    ShaderMaterial _mat = null!;
    float[] _buf = Array.Empty<float>();
    int _cap, _shown;
    const int Stride = 20; // 12 transform, 4 colour, 4 custom

    public override void _Ready()
    {
        TopLevel = true;
        GlobalTransform = Transform3D.Identity;
        CastShadow = ShadowCastingSetting.Off;
        _mat = new ShaderMaterial { Shader = new Shader { Code = Tracers.StreakShader } };
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            UseCustomData = true,
            Mesh = new QuadMesh { Size = Vector2.One, Material = _mat },
        };
        Multimesh = _mm;
        // The streaks are placed in the shader: the instances' own bounds mean nothing, so never cull them.
        CustomAabb = new Aabb(new Vector3(-1e5f, -1e4f, -1e5f), new Vector3(2e5f, 2e4f, 2e5f));
        Grow(256);
    }

    void Grow(int cap)
    {
        _cap = cap;
        _mm.InstanceCount = cap;
        _mm.VisibleInstanceCount = 0;
        _buf = new float[cap * Stride];
    }

    float _energy = -1f, _vis = -1f;

    /// <summary>Lay out a streak for every tracer still burning. <paramref name="ahead"/>: seconds since the last physics step (the rounds are drawn where they are now).</summary>
    public void Update(List<Projectile> live, float ahead)
    {
        // Against a night sky a tracer is by far the brightest thing there; by day it's a faint spark.
        float energy = Mathf.Lerp(4.5f, 1.2f, Conditions.Light);
        if (MathF.Abs(energy - _energy) > 0.02f) { _energy = energy; _mat.SetShaderParameter("energy", energy); }
        if (MathF.Abs(Conditions.VisibilityM - _vis) > 1f) { _vis = Conditions.VisibilityM; _mat.SetShaderParameter("visibility", _vis); }

        int n = 0;
        for (int i = 0; i < live.Count; i++)
        {
            var p = live[i];
            if (p.TraceLeft <= 0f) continue;
            if (n >= _cap)
            {
                var old = _buf;
                Grow(_cap * 2);
                Array.Copy(old, _buf, old.Length);
            }
            var head = p.Pos + p.Vel * ahead;
            float speed = p.Vel.Length();
            // The streak: what the eye holds, but never back past where the round was fired or glanced off.
            float len = MathF.Min(speed * Tracers.Persistence, head.DistanceTo(p.TraceFrom));
            var tail = speed > 1f ? p.Vel * (-len / speed) : Vector3.Zero;
            // The last tenth of a second it dims as the compound runs out.
            float glow = Mathf.Clamp(p.TraceLeft / 0.1f, 0f, 1f);
            int o = n * Stride;
            _buf[o + 0] = 1f; _buf[o + 1] = 0f; _buf[o + 2] = 0f; _buf[o + 3] = head.X;
            _buf[o + 4] = 0f; _buf[o + 5] = 1f; _buf[o + 6] = 0f; _buf[o + 7] = head.Y;
            _buf[o + 8] = 0f; _buf[o + 9] = 0f; _buf[o + 10] = 1f; _buf[o + 11] = head.Z;
            _buf[o + 12] = p.TraceColor.R; _buf[o + 13] = p.TraceColor.G; _buf[o + 14] = p.TraceColor.B; _buf[o + 15] = glow;
            _buf[o + 16] = tail.X; _buf[o + 17] = tail.Y; _buf[o + 18] = tail.Z; _buf[o + 19] = p.TraceWidth;
            n++;
        }
        if (n > 0)
        {
            Prof.Count("tracer:in flight, summed per frame", n);
            if (n > Prof.Counts.GetValueOrDefault("tracer:most in flight at once")) Prof.Counts["tracer:most in flight at once"] = n;
        }
        Prof.Count("tracer:frames drawn");
        if (n == 0 && _shown == 0) return;
        RenderingServer.MultimeshSetBuffer(_mm.GetRid(), _buf);
        _mm.VisibleInstanceCount = n;
        _shown = n;
    }
}
