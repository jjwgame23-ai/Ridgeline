using Godot;

namespace Ridgeline;

/// <summary>
/// Every crater on the map lives in one shared field, not as separate meshes, so
/// blasts that land on top of each other merge: a second frag in the same hole
/// digs it deeper and wider, and blows the first one's rim away instead of
/// stacking a second rim inside it. A barrage leaves one big, ragged pit.
///
/// The field is a 0.25 m grid in 16 m chunks, holding per point how deep it's been
/// dug, how much spoil has been thrown onto it, and how scorched it is. A chunk's
/// mesh is rebuilt when a blast touches it. On terrain that supports it (Terrain),
/// the ground surface is cut away over the dug area so the bowl has real depth.
/// </summary>
public partial class CraterField : Node3D
{
    const float Chunk = 16f, Cell = 0.25f;
    const int N = (int)(Chunk / Cell) + 1; // points per side
    const float MaxDepth = 1.1f;

    sealed class Patch
    {
        public readonly float[] Dig = new float[N * N], Spoil = new float[N * N], Scorch = new float[N * N];
        public MeshInstance3D? Mesh;
        public readonly List<Node3D> Clods = new();
    }

    readonly Dictionary<(int, int), Patch> _patches = new();
    readonly RandomNumberGenerator _rng = new();
    readonly StandardMaterial3D _mat = new() { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 1f };
    readonly StandardMaterial3D _clodMat = new() { AlbedoColor = new Color(0.28f, 0.22f, 0.16f), Roughness = 1f };
    public IGround Ground = null!;

    static readonly Color Charred = new(0.08f, 0.07f, 0.06f), Dug = new(0.30f, 0.24f, 0.17f), DeepDug = new(0.22f, 0.17f, 0.12f), SpoilCol = new(0.36f, 0.29f, 0.20f);

    Terrain? Holes => Ground as Terrain ?? (Ground as Valley)?.Terrain;

    public override void _Ready() => _rng.Randomize();

    static (int, int) Key(float x, float z) => ((int)MathF.Floor(x / Chunk), (int)MathF.Floor(z / Chunk));

    Patch Get((int, int) k)
    {
        if (!_patches.TryGetValue(k, out var p)) _patches[k] = p = new Patch();
        return p;
    }

    /// <summary>Dug depth at a world point (0 outside any crater), bilinear over the grid.</summary>
    public float DigAt(float x, float z)
    {
        var k = Key(x, z);
        if (!_patches.TryGetValue(k, out var p)) return 0f;
        float fx = (x - k.Item1 * Chunk) / Cell, fz = (z - k.Item2 * Chunk) / Cell;
        int i = Math.Clamp((int)fx, 0, N - 2), j = Math.Clamp((int)fz, 0, N - 2);
        float tx = fx - i, tz = fz - j;
        float a = Mathf.Lerp(p.Dig[j * N + i], p.Dig[j * N + i + 1], tx);
        float b = Mathf.Lerp(p.Dig[(j + 1) * N + i], p.Dig[(j + 1) * N + i + 1], tx);
        return Mathf.Lerp(a, b, tz);
    }

    /// <param name="radius">~0.9 m for a hand grenade, ~1.8 m for a mortar.</param>
    public void Blast(Vector3 pos, float radius)
    {
        const float reach = 2.3f; // spoil lands out to this many radii
        bool deep = Holes != null;
        // Hitting an existing hole: the charge is already below ground level, so it digs deeper and wider.
        float already = DigAt(pos.X, pos.Z);
        float r = radius * (1f + already * 0.35f);
        float depth = MathF.Min(MaxDepth, radius * 0.42f + already * 0.55f);
        float rimH = radius * 0.17f;

        // A ragged outline: a few random harmonics on the radius.
        float p1 = _rng.Randf() * Mathf.Tau, p2 = _rng.Randf() * Mathf.Tau, p3 = _rng.Randf() * Mathf.Tau;
        float a1 = _rng.RandfRange(0.05f, 0.12f), a2 = _rng.RandfRange(0.03f, 0.08f), a3 = _rng.RandfRange(0.02f, 0.05f);
        float Jag(float ang) => 1f + a1 * MathF.Sin(2f * ang + p1) + a2 * MathF.Sin(3f * ang + p2) + a3 * MathF.Sin(5f * ang + p3);

        float span = r * reach * 1.2f;
        var touched = new HashSet<(int, int)>();
        var k0 = Key(pos.X - span, pos.Z - span);
        var k1 = Key(pos.X + span, pos.Z + span);
        for (int kz = k0.Item2; kz <= k1.Item2; kz++)
        for (int kx = k0.Item1; kx <= k1.Item1; kx++)
        {
            var p = Get((kx, kz));
            touched.Add((kx, kz));
            for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                float x = kx * Chunk + i * Cell, z = kz * Chunk + j * Cell;
                float dx = x - pos.X, dz = z - pos.Z;
                float dist = MathF.Sqrt(dx * dx + dz * dz);
                if (dist > span) continue;
                float u = dist / (r * Jag(MathF.Atan2(dz, dx)));
                int idx = j * N + i;
                if (u < 1f)
                {
                    // The bowl: dig down to it, and the blast throws out whatever spoil was lying here.
                    float bowl = depth * (1f - u * u);
                    p.Dig[idx] = MathF.Min(MaxDepth, MathF.Max(p.Dig[idx], bowl) + bowl * 0.15f);
                    p.Spoil[idx] *= u * u;
                }
                else if (u < reach)
                {
                    // The rim and spoil, peaking just outside the lip. Less of it settles inside an older hole.
                    float w = u < 1.15f ? (u - 1f) / 0.15f : MathF.Pow(1f - (u - 1.15f) / (reach - 1.15f), 2f);
                    float settle = 1f - Mathf.Clamp(p.Dig[idx] / (radius * 0.3f), 0f, 0.8f);
                    p.Spoil[idx] = MathF.Min(radius * 0.45f, p.Spoil[idx] + rimH * w * settle);
                }
                float sc = Mathf.Clamp(1.35f - u * 0.75f, 0f, 1f) * (0.75f + 0.25f * _rng.Randf());
                p.Scorch[idx] = MathF.Max(p.Scorch[idx], sc);
            }
            // Loose clods inside the new bowl get thrown clear.
            for (int c = p.Clods.Count - 1; c >= 0; c--)
            {
                var cp = p.Clods[c].GlobalPosition;
                if (new Vector2(cp.X - pos.X, cp.Z - pos.Z).Length() > r * 1.15f) continue;
                p.Clods[c].QueueFree();
                p.Clods.RemoveAt(c);
            }
        }

        foreach (var k in touched) Rebuild(k, _patches[k]);
        if (deep)
            Holes!.CutHoles(DigAt, new Vector2(pos.X - r * 1.3f, pos.Z - r * 1.3f), new Vector2(pos.X + r * 1.3f, pos.Z + r * 1.3f));
        ThrowClods(pos, r);
    }

    void ThrowClods(Vector3 pos, float r)
    {
        for (int i = 0; i < 10; i++)
        {
            float ang = _rng.RandfRange(0f, Mathf.Tau), d = r * _rng.RandfRange(1.2f, 2.8f);
            float x = pos.X + MathF.Cos(ang) * d, z = pos.Z + MathF.Sin(ang) * d;
            float sz = r * _rng.RandfRange(0.05f, 0.13f);
            var clod = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(sz, sz * 0.7f, sz) }, MaterialOverride = _clodMat };
            AddChild(clod);
            clod.GlobalPosition = new Vector3(x, SurfaceAt(x, z) + sz * 0.25f, z);
            clod.Rotation = new Vector3(_rng.RandfRange(-0.5f, 0.5f), _rng.RandfRange(0f, Mathf.Tau), _rng.RandfRange(-0.5f, 0.5f));
            Get(Key(x, z)).Clods.Add(clod);
        }
    }

    /// <summary>The visible surface: terrain, minus what's been dug (if the terrain can be cut), plus spoil.</summary>
    public float SurfaceAt(float x, float z)
    {
        var k = Key(x, z);
        float h = Ground.HeightAt(x, z);
        if (!_patches.TryGetValue(k, out var p)) return h;
        int i = Math.Clamp((int)MathF.Round((x - k.Item1 * Chunk) / Cell), 0, N - 1);
        int j = Math.Clamp((int)MathF.Round((z - k.Item2 * Chunk) / Cell), 0, N - 1);
        return h + p.Spoil[j * N + i] - (Holes != null ? p.Dig[j * N + i] : 0f);
    }

    void Rebuild((int, int) k, Patch p)
    {
        bool deep = Holes != null;
        var heights = new float[N * N];
        var delta = new float[N * N];
        var active = new bool[N * N];
        for (int j = 0; j < N; j++)
        for (int i = 0; i < N; i++)
        {
            int idx = j * N + i;
            float x = k.Item1 * Chunk + i * Cell, z = k.Item2 * Chunk + j * Cell;
            delta[idx] = p.Spoil[idx] - (deep ? p.Dig[idx] : 0f);
            heights[idx] = Ground.HeightAt(x, z) + 0.03f + delta[idx];
            active[idx] = p.Dig[idx] > 0.005f || p.Spoil[idx] > 0.003f || p.Scorch[idx] > 0.03f;
        }

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int quads = 0;
        void V(int i, int j)
        {
            int idx = j * N + i;
            float x = k.Item1 * Chunk + i * Cell, z = k.Item2 * Chunk + j * Cell;
            // Terrain normal, bent by the crater's own slopes: an untouched edge matches the ground exactly.
            float dL = delta[j * N + Math.Max(i - 1, 0)], dR = delta[j * N + Math.Min(i + 1, N - 1)];
            float dD = delta[Math.Max(j - 1, 0) * N + i], dU = delta[Math.Min(j + 1, N - 1) * N + i];
            var n = Ground.NormalAt(x, z) + new Vector3(dL - dR, 0f, dD - dU) / (2f * Cell);
            st.SetNormal(n.Normalized());
            st.SetColor(ColorFor(p, idx, x, z));
            st.AddVertex(new Vector3(x, heights[idx], z));
        }
        for (int j = 0; j < N - 1; j++)
        for (int i = 0; i < N - 1; i++)
        {
            int a = j * N + i;
            if (!(active[a] || active[a + 1] || active[a + N] || active[a + N + 1])) continue;
            quads++;
            V(i, j); V(i + 1, j); V(i, j + 1);
            V(i + 1, j); V(i + 1, j + 1); V(i, j + 1);
        }
        if (p.Mesh == null)
        {
            p.Mesh = new MeshInstance3D { MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(p.Mesh);
        }
        p.Mesh.Mesh = quads > 0 ? st.Commit() : null;
    }

    Color ColorFor(Patch p, int idx, float x, float z)
    {
        var c = Ground.ColorAt(x, z);
        float spoil = p.Spoil[idx], dig = p.Dig[idx];
        if (spoil > 0f) c = c.Lerp(SpoilCol, Mathf.Clamp(spoil / 0.05f, 0f, 1f) * 0.85f);
        if (dig > 0f) c = c.Lerp(Dug, Mathf.Clamp(dig / 0.08f, 0f, 1f)).Lerp(DeepDug, Mathf.Clamp((dig - 0.3f) / 0.5f, 0f, 1f));
        // Scorch fades out in a speckle rather than a clean edge.
        float s = p.Scorch[idx];
        float speck = 0.5f + 0.5f * MathF.Sin(x * 12.9898f + z * 78.233f) * MathF.Sin(x * 4.1f - z * 7.3f);
        s *= s < 0.6f ? Mathf.Clamp(speck * 1.6f, 0f, 1f) : 1f;
        return c.Lerp(Charred, s * 0.85f);
    }
}
