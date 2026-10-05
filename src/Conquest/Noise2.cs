namespace Ridgeline;

/// <summary>
/// 2D gradient noise (Perlin's, with his improved fade curve), seeded and stateless. The island generator samples it
/// from many threads at once, and Godot's FastNoiseLite is an engine object, so this is plain C#: the same seed makes
/// the same island on any machine.
/// </summary>
public readonly struct Noise2
{
    readonly uint _seed;
    static readonly float[] Gx = new float[256], Gy = new float[256];

    static Noise2()
    {
        for (int i = 0; i < 256; i++)
        {
            float a = i * MathF.Tau / 256f;
            Gx[i] = MathF.Cos(a);
            Gy[i] = MathF.Sin(a);
        }
    }

    public Noise2(int seed) => _seed = (uint)seed * 0x9E3779B1u ^ 0x7F4A7C15u;

    static uint Hash(int x, int y, uint s)
    {
        uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ s;
        h ^= h >> 15; h *= 0x2C1B3C6Du;
        h ^= h >> 12; h *= 0x297A2D39u;
        h ^= h >> 15;
        return h;
    }

    float Dot(int ix, int iy, float dx, float dy)
    {
        uint h = Hash(ix, iy, _seed) & 255u;
        return Gx[h] * dx + Gy[h] * dy;
    }

    /// <summary>Noise at (x, y): about -1..1, with features about one unit across.</summary>
    public float Get(float x, float y)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float u = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
        float v = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
        float n00 = Dot(x0, y0, fx, fy), n10 = Dot(x0 + 1, y0, fx - 1f, fy);
        float n01 = Dot(x0, y0 + 1, fx, fy - 1f), n11 = Dot(x0 + 1, y0 + 1, fx - 1f, fy - 1f);
        float a = n00 + u * (n10 - n00), b = n01 + u * (n11 - n01);
        return (a + v * (b - a)) * 1.414f;
    }

    /// <summary>A fractal sum of <paramref name="octaves"/> octaves, each twice the frequency of the last: about -1..1.</summary>
    public float Fbm(float x, float y, int octaves, float gain = 0.5f)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (int o = 0; o < octaves; o++)
        {
            sum += amp * Get(x, y);
            norm += amp;
            amp *= gain;
            // Each octave is shifted so the lattices don't line up into a grid.
            x = x * 2.03f + 17.1f;
            y = y * 2.03f - 9.7f;
        }
        return sum / norm;
    }
}
