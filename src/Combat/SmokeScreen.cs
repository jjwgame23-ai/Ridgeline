using Godot;

namespace Ridgeline;

/// <summary>
/// Smoke: a vehicle's smoke launchers or a thrown smoke grenade. A cloud blooms over a few
/// seconds, sits there drifting a little, and thins out after ~50 s. Nobody sees through it,
/// bots or crews (the player sees the particles). You can still shoot into it at where
/// someone was.
/// </summary>
public static class SmokeScreen
{
    sealed class Cloud
    {
        public Vector3 Pos;
        public float Radius;
        public double Born, Dies;
    }

    static readonly List<Cloud> _clouds = new();
    public static int Pops;

    public static void Clear() { _clouds.Clear(); Pops = 0; }

    /// <summary>Put a cloud of this radius here, for this long.</summary>
    public static void Pop(Vector3 at, float radius, float seconds)
    {
        double now = Clock.Now;
        _clouds.RemoveAll(c => now > c.Dies);
        _clouds.Add(new Cloud { Pos = at + Vector3.Up * radius * 0.35f, Radius = radius, Born = now, Dies = now + seconds });
        Pops++;
        Effects.I?.SmokeCloud(at, radius, seconds);
        SoundWorld.I?.Emit(Snd.Launcher, at, -10f);
    }

    /// <summary>How big it is now: it takes ~4 s to bloom, and thins over the last ten.</summary>
    static float RadiusNow(Cloud c, double now)
    {
        float grow = Mathf.Clamp((float)(now - c.Born) / 4f, 0.15f, 1f);
        float fade = Mathf.Clamp((float)(c.Dies - now) / 10f, 0f, 1f);
        return c.Radius * grow * (0.4f + 0.6f * fade);
    }

    /// <summary>Does smoke hide one point from the other?</summary>
    public static bool Blocks(Vector3 from, Vector3 to)
    {
        if (_clouds.Count == 0) return false;
        double now = Clock.Now;
        var seg = to - from;
        float len2 = seg.LengthSquared();
        foreach (var c in _clouds)
        {
            if (now > c.Dies) continue;
            float r = RadiusNow(c, now);
            float t = len2 > 1e-4f ? Mathf.Clamp((c.Pos - from).Dot(seg) / len2, 0f, 1f) : 0f;
            // Thick enough to hide behind: the line passes through the middle part of it.
            if ((from + seg * t).DistanceSquaredTo(c.Pos) < r * r * 0.6f) return true;
        }
        return false;
    }

    /// <summary>Is this point inside smoke (so a man there is hidden from everyone)?</summary>
    public static bool Inside(Vector3 p)
    {
        double now = Clock.Now;
        foreach (var c in _clouds)
            if (now <= c.Dies && p.DistanceTo(c.Pos) < RadiusNow(c, now) * 0.8f) return true;
        return false;
    }
}
