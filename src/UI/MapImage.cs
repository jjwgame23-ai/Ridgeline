using Godot;

namespace Ridgeline;

/// <summary>
/// A picture of the battlefield for the map screens: the ground's own colours, shaded
/// by the lie of the land (light from the north-west), with built-up ground drawn dark.
/// Made once per map.
/// </summary>
public static class MapImage
{
    static Valley? _for;
    static ImageTexture? _tex;

    public static Texture2D For(Valley v)
    {
        if (_for == v && _tex != null) return _tex;
        const int N = 384;
        var img = Image.CreateEmpty(N, N, false, Image.Format.Rgb8);
        float size = v.Size, cell = size / N;
        var light = new Vector3(-1f, 1.4f, -1f).Normalized();
        for (int j = 0; j < N; j++)
        for (int i = 0; i < N; i++)
        {
            float x = (i + 0.5f) * cell - size / 2f, z = (j + 0.5f) * cell - size / 2f;
            var n = v.NormalAt(x, z);
            // Flat ground at full brightness; slopes facing the light brighter, away darker.
            float shade = Mathf.Clamp(1f + 1.4f * (n.Dot(light) - 0.7f), 0.55f, 1.25f);
            var c = v.ColorAt(x, z) * shade;
            if (v.BuiltAround(x, z) >= 3) c = c.Darkened(0.35f);
            // Contour every 20 m.
            float h = v.HeightAt(x, z);
            if (MathF.Abs(h % 20f) < cell * (1f - n.Y + 0.05f) * 0.6f) c = c.Darkened(0.18f);
            img.SetPixel(i, j, c);
        }
        _tex = ImageTexture.CreateFromImage(img);
        _for = v;
        return _tex;
    }
}
